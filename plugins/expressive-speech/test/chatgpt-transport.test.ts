import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createAssistantMessageEventStream } from '@openclaw/ai/event-stream';
import type { Model } from '@openclaw/ai';
import { wrapChatGptTransport } from '../src/chatgpt-transport.ts';

const model: Model<'openai-chatgpt-responses'> = { id: 'gpt-5.6-sol', provider: 'expressive-speech-openai', api: 'openai-chatgpt-responses', baseUrl: 'https://chatgpt.com/backend-api/codex', name: 'GPT-5.6 Sol', reasoning: true, input: ['text', 'image'], contextWindow: 372000, maxTokens: 128000, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } };

// Real HTTP proof owns the current rejected request; this boundary additionally
// protects caller-hook ordering, less-common request fields, and opaque ownership.
test('native ChatGPT adaptation runs after caller payload hooks while preserving runtime identity and full-context replay', async () => {
  const owner = Symbol('opaque-model-owner');
  const ownedModel = Object.defineProperty({ ...model }, owner, { value: {}, enumerable: false });
  const input = { messages: [] };
  const abort = new AbortController();
  let captured: unknown;
  let callerRan = false;
  const raw = { model: model.id, input: [{ type: 'message', role: 'user', content: 'hello' }], instructions: 'Be helpful.', stream: true };
  const wrapper = wrapChatGptTransport({ provider: model.provider, modelId: model.id, streamFn: async (actualModel, actualInput, options) => {
    assert.equal(actualModel, ownedModel);
    assert.equal(actualInput, input);
    assert.equal(options?.signal, abort.signal);
    assert.equal(options?.apiKey, 'dummy-preserved');
    assert.equal(options?.sessionId, 'same-session');
    assert.equal(options?.headers?.['x-caller-test'], 'preserved');
    assert.equal(options && 'replayResponsesItemIds' in options && options.replayResponsesItemIds, false);
    captured = await options?.onPayload?.(raw, actualModel);
    assert.equal(callerRan, true);
    const stream = createAssistantMessageEventStream(); stream.end(); return stream;
  } });
  await wrapper(ownedModel, input, { signal: abort.signal, apiKey: 'dummy-preserved', sessionId: 'same-session', headers: { 'x-caller-test': 'preserved' }, onPayload: async (payload, actualModel) => {
    assert.equal(payload, raw); assert.equal(actualModel, ownedModel); callerRan = true;
    return { ...raw, store: true, max_output_tokens: 128000, metadata: { harmless: 'value' }, prompt_cache_retention: '24h', prompt_cache_options: {}, service_tier: 'priority', temperature: 0.5, top_p: 0.5, text: { format: { type: 'json_object' }, verbosity: 'low' }, tools: [{ type: 'function', name: 'read', parameters: {} }], reasoning: { effort: 'high', summary: 'auto' } };
  } });
  assert.deepEqual(captured, { ...raw, store: false, text: { verbosity: 'low' }, tools: [{ type: 'function', name: 'read', parameters: {} }], reasoning: { effort: 'high', summary: 'auto' } });
  assert.equal(Object.hasOwn(raw, 'store'), false);
});

test('other providers and endpoints retain their original stream options', async () => {
  for (const actual of [{ ...model, provider: 'other' }, { ...model, baseUrl: 'https://example.invalid/v1' }]) {
    const options = { maxTokens: 128, temperature: 0.5 };
    const wrapper = wrapChatGptTransport({ provider: model.provider, modelId: model.id, streamFn: (receivedModel, _input, receivedOptions) => {
      assert.equal(receivedModel, actual); assert.equal(receivedOptions, options);
      const stream = createAssistantMessageEventStream(); stream.end(); return stream;
    } });
    await wrapper(actual, { messages: [] }, options);
  }
});
