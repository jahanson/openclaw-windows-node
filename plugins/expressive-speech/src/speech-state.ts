import { randomBytes, randomUUID } from 'node:crypto';

export const protocol = Object.freeze({ major: 1, minor: 0 });
export const MAX_SPEECH_CHARACTERS = 2000;
export type Caller = { deviceId: string; connectionId: string };
export type Identity = { sessionKey: string; sessionIncarnation: string; responseId: string; renditionId: string };
export type DeliveryEvent = { protocol: typeof protocol; identity: Identity; sequence: number; kind: 'begin' | 'segment' | 'terminal'; origin?: 'composed'; cueFormat?: 'elevenlabs-audio-tags'; text?: string; outcome?: 'complete' | 'unavailable' | 'failed' | 'cancelled'; reason?: string; messageId?: string };
export type SpeechMetadata = { protocol: typeof protocol; identity: Identity; origin: 'composed'; cueFormat: 'elevenlabs-audio-tags'; outcome: 'complete'; text: string };
export type Intent = {
  ticket: string; caller: Caller; sessionKey: string; idempotencyKey: string;
  delivery: 'prepared' | 'live'; expiresAt: number; identity?: Identity;
  events: DeliveryEvent[]; state: 'pending' | 'active' | 'terminal';
  spoken: string; metadata?: SpeechMetadata; persistedMessageId?: string;
  maxSpeechCharacters: number;
  expectedSessionId?: string;
  expectedLifecycleRevision?: string;
};
const MAX_INTENTS = 128;
const TTL_MS = 15 * 60 * 1000;
const sameCaller = (a: Caller, b: Caller) => a.deviceId === b.deviceId && a.connectionId === b.connectionId;

/** Connection-owned admission tickets; durable replay belongs to core history. */
export class SpeechState {
  private readonly intents = new Map<string, Intent>();

  create(caller: Caller, sessionKey: string, idempotencyKey: string, delivery: Intent['delivery'], maxSpeechCharacters = MAX_SPEECH_CHARACTERS, expectedSessionId?: string, expectedLifecycleRevision?: string): Intent {
    if (!Number.isSafeInteger(maxSpeechCharacters) || maxSpeechCharacters < 1 || maxSpeechCharacters > MAX_SPEECH_CHARACTERS) throw new Error('invalid-speech-limit');
    for (const [key, value] of this.intents) if (value.expiresAt < Date.now()) this.intents.delete(key);
    const existing = [...this.intents.values()].find((value) => value.sessionKey === sessionKey && value.idempotencyKey === idempotencyKey);
    if (existing) {
      if (!sameCaller(existing.caller, caller) || existing.delivery !== delivery || existing.maxSpeechCharacters !== maxSpeechCharacters || existing.expectedSessionId !== expectedSessionId || existing.expectedLifecycleRevision !== expectedLifecycleRevision) throw new Error('intent-conflict');
      return existing;
    }
    if (this.intents.size >= MAX_INTENTS) throw new Error('intent-capacity');
    const intent: Intent = { ticket: randomBytes(32).toString('base64url'), caller, sessionKey, idempotencyKey, delivery, maxSpeechCharacters, expectedSessionId, expectedLifecycleRevision, expiresAt: Date.now() + TTL_MS, events: [], state: 'pending', spoken: '' };
    this.intents.set(intent.ticket, intent);
    return intent;
  }

  owned(ticket: string, caller: Caller): Intent {
    const intent = this.intents.get(ticket);
    if (!intent || !sameCaller(intent.caller, caller) || intent.expiresAt < Date.now()) throw new Error('intent-unavailable');
    return intent;
  }

  admit(deviceId: string, sessionKey: string, runId: string, sessionId: string): Intent | undefined {
    const intent = [...this.intents.values()].find((value) => value.caller.deviceId === deviceId && value.sessionKey === sessionKey && value.idempotencyKey === runId && value.expiresAt > Date.now() && value.state === 'pending');
    if (!intent) return;
    // Keep admission bound to its original incarnation so the dispatcher can
    // cancel a reset race explicitly instead of falling through to core TTS.
    intent.identity = { sessionKey, sessionIncarnation: intent.expectedSessionId ?? sessionId, responseId: runId, renditionId: randomUUID() };
    intent.state = 'active';
    this.emit(intent, { kind: 'begin', origin: 'composed', cueFormat: 'elevenlabs-audio-tags' });
    return intent;
  }

  emit(intent: Intent, event: Omit<DeliveryEvent, 'protocol' | 'identity' | 'sequence'>): void {
    if (!intent.identity || intent.state === 'terminal') return;
    if (intent.events.length >= 4095 && event.kind !== 'terminal') { this.fail(intent, 'event-limit'); return; }
    const frame: DeliveryEvent = { protocol, identity: intent.identity, sequence: intent.events.length, ...event };
    if (Buffer.byteLength(JSON.stringify(frame)) > 96 * 1024) { this.fail(intent, 'event-limit'); return; }
    intent.events.push(frame);
    if (event.kind === 'terminal') intent.state = 'terminal';
  }

  segment(intent: Intent, text: string): void {
    if (intent.state !== 'active' || !text) return;
    if (intent.spoken.length + text.length > intent.maxSpeechCharacters || Buffer.byteLength(text) > 4096 || Buffer.byteLength(intent.spoken + text) > 65536 || text.includes('\r')) { this.fail(intent, 'content-limit'); return; }
    intent.spoken += text;
    this.emit(intent, { kind: 'segment', text });
  }

  fail(intent: Intent, reason: string, outcome: 'unavailable' | 'failed' | 'cancelled' = 'failed'): void {
    intent.metadata = undefined;
    this.emit(intent, { kind: 'terminal', outcome, reason });
  }

  finish(intent: Intent, text: string): SpeechMetadata | undefined {
    if (intent.state !== 'active' || !intent.identity || !text.trim() || text !== intent.spoken) { this.fail(intent, 'content-incomplete'); return; }
    const metadata: SpeechMetadata = { protocol, identity: intent.identity, origin: 'composed', cueFormat: 'elevenlabs-audio-tags', outcome: 'complete', text };
    intent.metadata = metadata;
    return metadata;
  }

  persisted(intent: Intent, messageId: string): void {
    if (!intent.metadata || intent.state !== 'active') return;
    intent.persistedMessageId = messageId;
    this.emit(intent, { kind: 'terminal', outcome: 'complete', text: intent.metadata.text, messageId });
  }

  findByRun(runId: string, sessionKey: string): Intent | undefined { return [...this.intents.values()].find((value) => value.identity?.responseId === runId && value.sessionKey === sessionKey); }
  // Detaching playback must not cancel composition or erase later history metadata.
  detach(intent: Intent): void { this.intents.delete(intent.ticket); }
  clear(): void { this.intents.clear(); }
}
