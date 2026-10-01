import { randomUUID } from "node:crypto";
import { join } from "node:path";
import { AsyncLocalStorage } from "node:async_hooks";
import { createAssistantMessageEventStream, streamSimple } from "openclaw/plugin-sdk/llm";
import { upsertAuthProfile } from "openclaw/plugin-sdk/provider-auth";
import { getReplyFromConfig } from "openclaw/plugin-sdk/reply-runtime";
import { getLastTtsAttempt } from "openclaw/plugin-sdk/tts-runtime";

const scope = new AsyncLocalStorage();

function cleanFixtureMessage(message) {
  if (!message?.content) return message;
  return { ...message, content: message.content.map((part) => part.type === "text" ? { ...part, text: "Visible. End." } : part) };
}

// Test-only native plugin. It intentionally exercises an unsafe candidate with
// synthetic content. This is not the production expressive-speech plugin.
export default {
  id: "expressive-speech-probe",
  register(api) {
    api.registerProvider({ id: "probe-openai", label: "Synthetic OAuth alias", auth: [] });
    api.registerGatewayMethod("expressive-speech-probe.auth", async ({ respond }) => {
      const dummyAccess = process.env.EXPRESSIVE_SPEECH_PROBE_DUMMY_JWT ?? "synthetic-access-not-a-secret";
      upsertAuthProfile({ agentDir: join(process.env.OPENCLAW_STATE_DIR, "agents", "main", "agent"), profileId: "openai:synthetic-probe", credential: { type: "oauth", provider: "openai", access: dummyAccess, refresh: "synthetic-refresh-not-a-secret", expires: Date.now() + 86400000 } });
      const model = { id: "gpt-5.6-sol", name: "Synthetic alias only", provider: "probe-openai", api: "openai-chatgpt-responses", baseUrl: "https://chatgpt.com/backend-api/codex", reasoning: false, input: ["text"], contextWindow: 32768, maxTokens: 4096, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } };
      try {
        const auth = await api.runtime.modelAuth.getApiKeyForModel({ model, cfg: api.config, workspaceDir: process.env.EXPRESSIVE_SPEECH_PROBE_WORKSPACE });
        respond(true, { mode: auth.mode, syntheticProfileSelected: auth.profileId === "openai:synthetic-probe", dummyCredentialSelected: auth.apiKey === dummyAccess });
      } catch (error) { respond(false, undefined, { code: "AUTH_PROBE_FAILED", message: String(error) }); }
    });
    const dispatches = [];
    const admissionShapes = [];
    const intents = new Map();
    api.registerGatewayMethod("expressive-speech-probe.intent", ({ params, client, respond }) => {
      const deviceId = client?.connect?.device?.id;
      if (!deviceId || !client?.connId) return respond(false, undefined, { code: "SIGNED_DEVICE_REQUIRED", message: "Signed device required" });
      const ticket = randomUUID();
      intents.set(params.idempotencyKey, { ticket, deviceId, connId: client.connId, sessionKey: params.sessionKey });
      respond(true, { ticket });
    });
    const transcriptUpdates = [];
    let syntheses = 0;
    api.runtime.events.onSessionTranscriptUpdate((update) => transcriptUpdates.push(update));
    api.on("before_message_write", (event) => {
      if (scope.getStore()?.decorate && event.message.role === "assistant") return { message: { ...event.message, expressiveSpeechProbe: { text: "[curious] Spoken only.", version: 1 } } };
    });
    api.registerSpeechProvider({ id: "probe-tts", label: "Synthetic speech counter", isConfigured: () => true,
      async synthesize() { syntheses++; return { audioBuffer: Buffer.from("synthetic"), outputFormat: "mp3", fileExtension: ".mp3", voiceCompatible: false }; } });
    api.registerGatewayMethod("expressive-speech-probe.status", ({ respond }) => respond(true, { syntheses, lastTtsAttempt: getLastTtsAttempt(), dispatches, transcriptUpdates, admissionShapes }));
    api.on("reply_dispatch", async (event, ctx) => {
      api.logger.info(`Synthetic reply_dispatch entered: ${event.sessionKey}`);
      admissionShapes.push({ sessionKey: event.sessionKey, inboundAudio: event.inboundAudio, provider: event.ctx.Provider, surface: event.ctx.Surface, kind: ctx.dispatchKind, command: event.ctx.CommandTurn, sendPolicy: event.sendPolicy, suppressUserDelivery: event.suppressUserDelivery, suppressReplyLifecycle: event.suppressReplyLifecycle, isTailDispatch: event.isTailDispatch, sourceReplyDeliveryMode: event.sourceReplyDeliveryMode });
      if (!event.ctx.Body?.includes("OWNED_SYNTHETIC")) return;
      const entry = api.runtime.agent.session.getSessionEntry({ sessionKey: event.sessionKey, readConsistency: "latest" });
      const observation = { sessionKey: event.sessionKey, entry, runId: event.runId, messageSid: event.ctx.MessageSid, rawChunks: [], decorate: true, finished: false };
      const intent = intents.get(event.ctx.MessageSid);
      observation.intentBound = !!intent && intent.deviceId === event.ctx.ApprovalReviewerDeviceId && intent.sessionKey === event.sessionKey;
        dispatches.push(observation);
        try {
          if (process.env.OPENCLAW_PROBE_ORCHESTRATOR !== '1') ctx.onAgentRunStart?.(event.runId);
        const replyResult = process.env.OPENCLAW_PROBE_ORCHESTRATOR === '1' ? await scope.run(observation, () => getReplyFromConfig(event.ctx, {
          runId: event.runId, abortSignal: ctx.abortSignal,
          onAgentRunStart: ctx.onAgentRunStart,
          userTurnTranscriptRecorder: ctx.userTurnTranscriptRecorder,
          prepareAssistantTranscriptMessage: ctx.prepareAssistantTranscriptMessage,
          onToolResult: (payload) => { ctx.dispatcher.sendToolResult(payload); },
          onBlockReply: (payload) => { ctx.dispatcher.sendBlockReply(payload); },
        }, ctx.cfg)) : await scope.run(observation, () => api.runtime.agent.runEmbeddedAgent({
          config: ctx.cfg, sessionId: entry.sessionId, sessionKey: event.sessionKey,
          workspaceDir: process.env.EXPRESSIVE_SPEECH_PROBE_WORKSPACE,
          agentDir: join(process.env.OPENCLAW_STATE_DIR, 'agents', 'main', 'agent'),
          provider: 'probe', model: 'synthetic', prompt: event.ctx.Body,
          runId: event.runId, timeoutMs: 20000, disableTools: true,
          abortSignal: ctx.abortSignal, userTurnTranscriptRecorder: ctx.userTurnTranscriptRecorder,
          prepareAssistantTranscriptMessage: ctx.prepareAssistantTranscriptMessage,
        }));
        const result = process.env.OPENCLAW_PROBE_ORCHESTRATOR === '1' ? replyResult : replyResult.payloads;
        observation.result = result ?? [];
        let queuedFinal = false;
        for (const payload of Array.isArray(result) ? result : result ? [result] : []) queuedFinal = ctx.dispatcher.sendFinalReply(payload) || queuedFinal;
        await ctx.dispatcher.waitForIdle();
        ctx.recordProcessed("completed");
        ctx.markIdle("synthetic probe complete");
        return { handled: true, queuedFinal, counts: ctx.dispatcher.getQueuedCounts() };
      } catch (error) {
        observation.error = String(error);
        throw error;
      }
    }, { eligibleDispatchKinds: ["agent"] });
    api.registerProvider({
      id: "probe", label: "Synthetic isolated provider", auth: [],
      wrapStreamFn({ streamFn }) {
        const underlying = streamFn ?? streamSimple;
        return (model, context, options) => {
          const owned = scope.getStore();
          const source = underlying(model, context, process.env.OPENCLAW_PROBE_CHATGPT === "1" ? { ...options, transport: "sse" } : options);
          if (!owned?.decorate) return source;
          const output = createAssistantMessageEventStream();
          void (async () => {
            try {
              for await (const event of await source) {
                if (event.type === "text_delta") {
                  owned.rawChunks.push(event.delta);
                  continue;
                }
                if (event.type === "text_start") owned.startSignatures = [...(owned.startSignatures ?? []), event.partial?.content?.[event.contentIndex]?.textSignature ?? null];
                if (event.type === "text_start" || event.type === "text_end") continue;
                if (event.type === "done") owned.finished = true;
                output.push({ ...event, ...(event.partial ? { partial: cleanFixtureMessage(event.partial) } : {}), ...(event.message ? { message: cleanFixtureMessage(event.message) } : {}) });
              }
            } catch (error) {
              owned.wrapperError = String(error);
              output.push({ type: "error", reason: "error", error: { role: "assistant", content: [], api: model.api, provider: model.provider, model: model.id, usage: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0, total: 0, cost: { total: 0 } }, stopReason: "error", errorMessage: "Synthetic wrapper failed", timestamp: Date.now() } });
            } finally { output.end(); }
          })();
          return output;
        };
      },
    });
    api.registerGatewayMethod("expressive-speech-probe.run", async ({ params, respond }) => {
      const observations = { partials: [], events: [], global: [], rawChunks: [], decorate: params.decorate === true, finished: false };
      const runId = randomUUID();
      const stop = api.runtime.events.onAgentEvent((event) => {
        if (event.runId === runId) observations.global.push(event);
      });
      try {
        const workspaceDir = process.env.EXPRESSIVE_SPEECH_PROBE_WORKSPACE;
        const sessionId = randomUUID();
        const result = await scope.run(observations, () => api.runtime.agent.runEmbeddedAgent({
          config: api.config,
          sessionId,
          sessionKey: `agent:main:probe-${sessionId}`,
          workspaceDir,
          agentDir: join(process.env.OPENCLAW_STATE_DIR, "agents", "main", "agent"),
          provider: "probe",
          model: "synthetic",
          prompt: "Synthetic isolated contract check. Return the fixture response.",
          runId,
          timeoutMs: 20000,
          disableTools: true,
          suppressLiveStreamOutput: params.suppress === true,
          onPartialReply: (payload) => { observations.partials.push(payload); },
          onAgentEvent: (event) => { observations.events.push(event); },
        }));
        respond(true, { observations, result });
      } catch (error) {
        respond(false, undefined, { code: "PROBE_FAILED", message: String(error) });
      } finally {
        stop();
      }
    });
  },
};
