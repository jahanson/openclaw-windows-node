import type { AsyncLocalStorage } from 'node:async_hooks';
import type { AssistantMessage, AssistantMessageEvent } from '@openclaw/ai';
import type { ProviderWrapStreamFnContext } from 'openclaw/plugin-sdk/core';
import { createAssistantMessageEventStream, streamSimple } from 'openclaw/plugin-sdk/llm';
import { ContentSeparator, type ContentSeparatorResult } from './content-separator.ts';
import { type Intent, SpeechState } from './speech-state.ts';

export type TurnScope = { intent: Intent; current: () => boolean; state: SpeechState; modelCalls: number };
type TextSlot = { parser: ContentSeparator; raw: string; earlyFinal: boolean; phase?: string; ended: boolean; speech: string[] };
const phaseOf = (signature: unknown): string | undefined => {
  if (typeof signature !== 'string' || signature.length > 4096) return;
  try { const parsed: unknown = JSON.parse(signature); if (parsed && typeof parsed === 'object' && 'v' in parsed && parsed.v === 1 && 'phase' in parsed && (parsed.phase === 'final_answer' || parsed.phase === 'commentary')) return parsed.phase; } catch { /* An opaque signature is not eligibility. */ }
};

/** A new plugin-owned stream, before the stock agent's broadcast/persistence. */
export function wrapSpeechStream(context: ProviderWrapStreamFnContext, scope: AsyncLocalStorage<TurnScope>): NonNullable<ProviderWrapStreamFnContext['streamFn']> {
  const underlying = context.streamFn ?? streamSimple;
  return (model, input, options) => {
    const turn = scope.getStore();
    if (!turn) return underlying(model, input, options);
    const output = createAssistantMessageEventStream();
    const slots = new Map<number, TextSlot>();
    let latest: AssistantMessage | undefined;
    let tools = false;
    let finalSlots = 0;
    let unsafeWritten = false;
    turn.modelCalls++;
    const current = () => turn.current() && turn.intent.state === 'active';
    const slotFor = (index: number, message?: AssistantMessage): TextSlot => {
      let slot = slots.get(index);
      if (!slot) {
        const part = message?.content[index];
        const phase = part?.type === 'text' ? phaseOf(part.textSignature) : undefined;
        slot = { parser: new ContentSeparator(), raw: '', earlyFinal: phase === 'final_answer', phase, ended: false, speech: [] };
        slots.set(index, slot);
        if (slot.earlyFinal && ++finalSlots > 1) turn.state.fail(turn.intent, 'multiple-final-blocks');
      }
      return slot;
    };
    const sanitize = (message: AssistantMessage): AssistantMessage => ({
      ...message,
      content: message.content.map((part, index) => {
        if (part.type !== 'text') return { ...part };
        const slot = slots.get(index);
        if (slot) return { ...part, text: slot.parser.writtenText };
        const parser = new ContentSeparator(); parser.push(part.text); parser.end();
        if (parser.failed !== undefined && parser.failed !== 'missing-speech') unsafeWritten = true;
        return { ...part, text: parser.writtenText };
      }),
    });
    const deliver = (slot: TextSlot, index: number, result: ContentSeparatorResult) => {
      if (result.failure && result.failure !== 'missing-speech') turn.state.fail(turn.intent, result.failure);
      if (result.failure !== undefined && result.failure !== 'missing-speech') unsafeWritten = true;
      if (!current()) { if (turn.intent.state === 'active') turn.state.fail(turn.intent, 'session-invalidated', 'cancelled'); }
      for (const text of result.speech) {
        slot.speech.push(text);
        if (current() && turn.intent.delivery === 'live' && slot.earlyFinal && !tools) turn.state.segment(turn.intent, text);
      }
      for (const delta of result.written) output.push({ type: 'text_delta', contentIndex: index, delta, ...(latest ? { partial: sanitize(latest) } : {}) } as AssistantMessageEvent);
    };
    const timer = setInterval(() => { for (const [index, slot] of slots) if (!slot.ended) deliver(slot, index, slot.parser.flush()); }, 100);
    timer.unref();
    void (async () => {
      try {
        for await (const event of await underlying(model, input, options)) {
          if ('partial' in event) latest = event.partial;
          if (event.type === 'text_start') {
            slotFor(event.contentIndex, event.partial);
            output.push({ ...event, partial: sanitize(event.partial) });
          } else if (event.type === 'text_delta') {
            const slot = slotFor(event.contentIndex, latest);
            slot.raw += event.delta;
            deliver(slot, event.contentIndex, slot.parser.push(event.delta));
          } else if (event.type === 'text_end') {
            const slot = slotFor(event.contentIndex, event.partial);
            const part = event.partial.content[event.contentIndex];
            slot.phase = phaseOf(part?.type === 'text' ? part.textSignature : undefined) ?? slot.phase;
            if (slot.raw !== event.content) {
              turn.state.fail(turn.intent, 'provider-revised-text');
              const replacement = new ContentSeparator(); replacement.push(event.content); replacement.end(); slot.parser = replacement;
              if (replacement.failed !== undefined && replacement.failed !== 'missing-speech') unsafeWritten = true;
            } else deliver(slot, event.contentIndex, slot.parser.end());
            slot.ended = true;
            if (unsafeWritten) throw new Error('unsafe-written-output');
            output.push({ ...event, content: slot.parser.writtenText, partial: sanitize(event.partial) });
          } else if (event.type === 'done') {
            latest = event.message;
            for (const [index, slot] of slots) if (!slot.ended) { deliver(slot, index, slot.parser.end()); slot.ended = true; }
            if (unsafeWritten) throw new Error('unsafe-written-output');
            const candidates = [...slots.values()].filter((slot) => slot.parser.hasSpeech && !slot.parser.failed && slot.phase !== 'commentary');
            if (!tools && event.reason === 'stop' && current()) {
              const slot = candidates.length === 1 ? candidates[0] : undefined;
              if (!slot) turn.state.fail(turn.intent, candidates.length ? 'multiple-final-blocks' : 'missing-speech', 'unavailable');
              else if (turn.intent.delivery === 'live' && !slot.earlyFinal) turn.state.fail(turn.intent, 'early-final-phase-unavailable', 'unavailable');
              else {
                if (turn.intent.delivery === 'prepared') for (const text of slot.speech) turn.state.segment(turn.intent, text);
                turn.state.finish(turn.intent, slot.parser.speechText);
              }
            }
            const cleanMessage = sanitize(event.message);
            if (unsafeWritten) throw new Error('unsafe-written-output');
            output.push({ ...event, message: cleanMessage });
          } else {
            if (event.type.startsWith('toolcall_')) {
              tools = true;
              if (turn.intent.spoken) turn.state.fail(turn.intent, 'tool-after-final-speech');
            }
            if (event.type === 'error') { turn.state.fail(turn.intent, 'model-error'); output.push({ ...event, error: sanitize(event.error) }); }
            else output.push('partial' in event ? { ...event, partial: sanitize(event.partial) } : event);
          }
          if (unsafeWritten) throw new Error('unsafe-written-output');
        }
      } catch {
        turn.state.fail(turn.intent, 'provider-stream-failed');
        output.push({ type: 'error', reason: 'error', error: { role: 'assistant', content: latest ? sanitize(latest).content : [], api: model.api, provider: model.provider, model: model.id, usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, totalTokens: 0, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } }, stopReason: 'error', errorMessage: unsafeWritten ? 'The response could not be safely separated into written and spoken content. Please retry.' : 'Expressive speech could not process the provider stream.', timestamp: Date.now() } });
      } finally { clearInterval(timer); output.end(); }
    })();
    return output;
  };
}
