import type { ProviderWrapStreamFnContext } from 'openclaw/plugin-sdk/core';
import { streamSimple } from 'openclaw/plugin-sdk/llm';

// Pinned host contract: native ChatGPT sanitization is gated on provider=openai,
// so an auth alias does not inherit it. Preserve the decorated model object and
// its opaque runtime owner; adapt only public request/replay options instead.
// Upstream a181c3f1 packages/ai/src/transports/openai-responses-params-internal.ts.
const unsupported = ['max_output_tokens', 'metadata', 'prompt_cache_retention', 'prompt_cache_options', 'service_tier', 'temperature', 'top_p'] as const;
const isRecord = (value: unknown): value is Record<string, unknown> => value !== null && typeof value === 'object' && !Array.isArray(value);

export function wrapChatGptTransport(context: ProviderWrapStreamFnContext): NonNullable<ProviderWrapStreamFnContext['streamFn']> {
  const underlying = context.streamFn ?? streamSimple;
  return (model, input, options) => {
    if (model.provider !== 'expressive-speech-openai' || model.id !== 'gpt-5.6-sol'
      || model.api !== 'openai-chatgpt-responses' || model.baseUrl.replace(/\/+$/, '') !== 'https://chatgpt.com/backend-api/codex') return underlying(model, input, options);
    const nativeOptions: NonNullable<typeof options> & { replayResponsesItemIds: false } = {
      ...options,
      // ChatGPT store=false requires full context, including tool results. This
      // must happen before input conversion, not by deleting replay IDs later.
      replayResponsesItemIds: false,
      onPayload: async (payload, payloadModel) => {
        const transformed = await options?.onPayload?.(payload, payloadModel);
        const original = transformed ?? payload;
        if (!isRecord(original)) throw new Error('ChatGPT requires a Responses request object.');
        const request: Record<string, unknown> = { ...original, store: false };
        for (const field of unsupported) delete request[field];
        if (isRecord(request.text)) {
          const text = { ...request.text };
          delete text.format;
          if (Object.keys(text).length) request.text = text;
          else delete request.text;
        }
        return request;
      },
    };
    return underlying(model, input, nativeOptions);
  };
}
