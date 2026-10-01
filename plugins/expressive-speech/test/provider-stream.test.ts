import assert from 'node:assert/strict';
import { test } from 'node:test';
import { AsyncLocalStorage } from 'node:async_hooks';
import { setTimeout as delay } from 'node:timers/promises';
import { createAssistantMessageEventStream } from '@openclaw/ai/event-stream';
import type { AssistantMessage, AssistantMessageEvent, Model } from '@openclaw/ai';
import { wrapSpeechStream, type TurnScope } from '../src/provider-stream.ts';
import { SpeechState } from '../src/speech-state.ts';

const model: Model<'openai-chatgpt-responses'> = { id: 'synthetic', provider: 'probe', api: 'openai-chatgpt-responses', baseUrl: 'https://example.invalid', name: 'Synthetic', reasoning: false, input: ['text'], contextWindow: 4096, maxTokens: 512, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } };
const speech = '[warmly] This is the spoken explanation, clear and complete.';
const raw = `Written answer. [[tts:text]]${speech}[[/tts:text]] More written detail.`;
function setup() {
  const state = new SpeechState();
  state.create({ deviceId: 'device', connectionId: 'connection' }, 'agent:main:test', 'run', 'live');
  const intent = state.admit('device', 'agent:main:test', 'run', 'session')!;
  const storage = new AsyncLocalStorage<TurnScope>();
  const turn: TurnScope = { state, intent, current: () => true, modelCalls: 0 };
  return { state, intent, storage, turn };
}
function message(phase?: string): AssistantMessage {
  return { role: 'assistant', content: [{ type: 'text', text: '', ...(phase ? { textSignature: JSON.stringify({ v: 1, id: 'message', phase }) } : {}) }], api: model.api, provider: model.provider, model: model.id, stopReason: 'stop', timestamp: Date.now(), usage: { input: 1, output: 1, cacheRead: 0, cacheWrite: 0, totalTokens: 2, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0 } } };
}
async function start(harness: ReturnType<typeof setup>, phase?: string, spoken = speech, chunks = ['Written answer. [[tt', 's:text]]', spoken, '[[/tts:text]] More written detail.']) {
  const input = createAssistantMessageEventStream();
  const wrapper = wrapSpeechStream({ provider: model.provider, modelId: model.id, model, streamFn: () => input }, harness.storage);
  const stream = await harness.storage.run(harness.turn, () => wrapper(model, { messages: [] }, {}));
  const events: AssistantMessageEvent[] = [];
  const drain = (async () => { for await (const event of stream) events.push(structuredClone(event)); })();
  const original = message(phase);
  input.push({ type: 'text_start', contentIndex: 0, partial: original });
  await delay(0);
  for (const chunk of chunks) {
    const part = original.content[0]; assert.equal(part.type, 'text'); if (part.type !== 'text') throw new Error('fixture');
    part.text += chunk;
    input.push({ type: 'text_delta', contentIndex: 0, delta: chunk });
    await delay(0);
  }
  return { input, original, events, drain };
}

test('provider speech exceeding the synthesis character ceiling cannot be truncated into a successful rendition', async () => {
  const harness = setup();
  const oversized = 'a'.repeat(2001);
  const run = await start(harness, 'final_answer', oversized);
  const full = `Written answer. [[tts:text]]${oversized}[[/tts:text]] More written detail.`;
  run.input.push({ type: 'text_end', contentIndex: 0, content: full, partial: run.original });
  run.input.push({ type: 'done', reason: 'stop', message: run.original }); run.input.end();
  await run.drain;
  assert.equal(harness.intent.events.at(-1)?.reason, 'content-limit');
  assert.equal(harness.intent.events.some((event) => event.outcome === 'complete'), false);
  assert.equal(harness.intent.metadata, undefined);
  assert.ok(harness.intent.events.filter((event) => event.kind === 'segment').map((event) => event.text).join('').length <= 2000);
  const final = run.events.find((event) => event.type === 'done');
  assert.equal(final?.message.content[0].type === 'text' ? final.message.content[0].text : '', 'Written answer.  More written detail.');
});

test('unsafe or oversized written output never becomes a successful truncated assistant completion', async () => {
  for (const [name, suffix] of [['written-limit', 'x'.repeat(1024 * 1024 + 1)], ['invalid-unicode', '\ud800unsafe remainder']]) {
    const harness = setup();
    const run = await start(harness, 'final_answer');
    const part = run.original.content[0];
    assert.equal(part.type, 'text');
    if (part.type !== 'text') throw new Error('fixture');
    part.text += suffix;
    run.input.push({ type: 'text_delta', contentIndex: 0, delta: suffix });
    run.input.push({ type: 'text_end', contentIndex: 0, content: part.text, partial: run.original });
    run.input.push({ type: 'done', reason: 'stop', message: run.original }); run.input.end();
    await run.drain;
    assert.equal(run.events.some((event) => event.type === 'done'), false, name);
    assert.equal(run.events.at(-1)?.type, 'error', name);
    assert.equal(harness.intent.metadata, undefined, name);
    assert.equal(harness.intent.events.at(-1)?.outcome, 'failed', name);
    assert.equal(JSON.stringify(run.events).includes(speech), false, name);
  }
});

test('malformed separation cannot report a successful written response after discarding its suffix', async () => {
  const cases = [
    ['nested-speech', 'Written prefix. [[tts:text]]Hidden [[tts:text]]nested[[/tts:text]] Written suffix.'],
    ['repeated-speech', 'Written prefix. [[tts:text]]Hidden[[/tts:text]][[tts:text]]again[[/tts:text]] Written suffix.'],
    ['unexpected-close', 'Written prefix. [[/tts:text]] Written suffix.'],
    ['empty-speech', 'Written prefix. [[tts:text]][[/tts:text]] Written suffix.'],
    ['unfinished-cue', 'Written prefix. [[tts:text]][warm[[/tts:text]] Written suffix.'],
    ['speech-limit', `Written prefix. [[tts:text]]${'x'.repeat(65537)}[[/tts:text]] Written suffix.`],
    ['unclosed-speech', 'Written prefix. [[tts:text]]Hidden'],
    ['incomplete-marker', 'Written prefix. [[tts:'],
  ];
  for (const [name, text] of cases) {
    const harness = setup();
    const run = await start(harness, 'final_answer', '', [text]);
    run.input.push({ type: 'text_end', contentIndex: 0, content: text, partial: run.original });
    run.input.push({ type: 'done', reason: 'stop', message: run.original }); run.input.end();
    await run.drain;
    assert.equal(run.events.some((event) => event.type === 'done'), false, name);
    const final = run.events.at(-1);
    assert.equal(final?.type, 'error', name);
    assert.equal(final?.type === 'error' && final.error.content[0]?.type === 'text' ? final.error.content[0].text : '', 'Written prefix. ', name);
    assert.equal(harness.intent.metadata, undefined, name);
    assert.equal(harness.intent.events.at(-1)?.reason, name);
  }

  const harness = setup();
  const text = 'A complete ordinary written answer without a speech block.';
  const run = await start(harness, 'final_answer', '', [text]);
  run.input.push({ type: 'text_end', contentIndex: 0, content: text, partial: run.original });
  run.input.push({ type: 'done', reason: 'stop', message: run.original }); run.input.end();
  await run.drain;
  const final = run.events.find((event) => event.type === 'done');
  assert.equal(final?.message.content[0].type === 'text' ? final.message.content[0].text : '', text);
  assert.equal(harness.intent.events.at(-1)?.reason, 'missing-speech');
});

test('eligible provider stream releases hidden speech before done while every core event stays written-only', async () => {
  const harness = setup();
  const run = await start(harness, 'final_answer');
  await delay(120);
  assert.equal(harness.intent.events.filter((event) => event.kind === 'segment').map((event) => event.text).join(''), speech);
  assert.equal(harness.intent.events.some((event) => event.kind === 'terminal'), false);
  run.input.push({ type: 'text_end', contentIndex: 0, content: raw, partial: run.original });
  run.input.push({ type: 'done', reason: 'stop', message: run.original }); run.input.end();
  await run.drain;
  assert.equal(harness.intent.metadata?.text, speech);
  assert.equal(JSON.stringify(run.events).includes(speech), false);
  assert.equal(JSON.stringify(run.events).includes('[[tts:text]]'), false);
  const final = run.events.find((event) => event.type === 'done');
  assert.equal(final?.message.content[0].type === 'text' ? final.message.content[0].text : '', 'Written answer.  More written detail.');
  assert.equal(run.original.content[0].type === 'text' ? run.original.content[0].text : '', raw, 'Wrapper must not mutate provider objects.');
  harness.state.persisted(harness.intent, 'actual-receipt-id');
  assert.equal(harness.intent.events.at(-1)?.messageId, 'actual-receipt-id');
});

test('live speech fails closed without early provider final phase even when prompt delimiters are valid', async () => {
  const harness = setup();
  const run = await start(harness);
  await delay(120);
  assert.equal(harness.intent.events.some((event) => event.kind === 'segment'), false);
  run.input.push({ type: 'text_end', contentIndex: 0, content: raw, partial: run.original });
  run.input.push({ type: 'done', reason: 'stop', message: run.original }); run.input.end();
  await run.drain;
  assert.equal(harness.intent.events.at(-1)?.reason, 'early-final-phase-unavailable');
  assert.equal(JSON.stringify(run.events).includes(speech), false);
});

test('commentary tool attempt never publishes speech and a later final attempt owns the rendition', async () => {
  const harness = setup();
  const intermediate = await start(harness, 'commentary');
  intermediate.input.push({ type: 'text_end', contentIndex: 0, content: raw, partial: intermediate.original });
  intermediate.input.push({ type: 'done', reason: 'toolUse', message: { ...intermediate.original, stopReason: 'toolUse' } }); intermediate.input.end();
  await intermediate.drain;
  assert.equal(harness.intent.events.length, 1);
  assert.equal(Boolean(harness.intent.metadata), false);
  const final = await start(harness, 'final_answer');
  final.input.push({ type: 'text_end', contentIndex: 0, content: raw, partial: final.original });
  final.input.push({ type: 'done', reason: 'stop', message: final.original }); final.input.end();
  await final.drain;
  assert.equal(harness.intent.metadata?.text, speech);
  assert.equal(harness.intent.spoken, speech);
});
