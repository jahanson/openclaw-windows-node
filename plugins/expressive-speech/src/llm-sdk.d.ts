// OpenClaw 2026.9.4 exports this runtime facade without a .d.ts. Its stream
// protocol is the public @openclaw/ai contract; no private runtime is imported.
declare module 'openclaw/plugin-sdk/llm' {
  export { createAssistantMessageEventStream } from '@openclaw/ai/event-stream';
  export const streamSimple: import('@openclaw/ai').StreamFunction;
}
