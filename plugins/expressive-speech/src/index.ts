import { AsyncLocalStorage } from 'node:async_hooks';
import type { OpenClawPluginApi } from 'openclaw/plugin-sdk/core';
import { resolveAgentConfig } from 'openclaw/plugin-sdk/agent-scope-runtime';
import { MAX_SPEECH_CHARACTERS, protocol, SpeechState, type Caller, type Intent } from './speech-state.ts';
import { wrapSpeechStream, type TurnScope } from './provider-stream.ts';
import { wrapChatGptTransport } from './chatgpt-transport.ts';

const PROVIDER = 'expressive-speech-openai';
const MODEL = 'gpt-5.6-sol';
const BASE_URL = 'https://chatgpt.com/backend-api/codex';
const DEVELOPMENT_PROFILE = 'upstream-a181c3f1d9086f52d5c198b69553f689715f6109';
// The host can register the same module again for provider-only activation.
// Correlation belongs to the module, not one registry's register() call.
const turns = new AsyncLocalStorage<TurnScope>();
const GUIDANCE = `For the final answer only, first emit exactly one [[tts:text]]...[[/tts:text]] block containing a self-contained spoken rendition. Then produce the ordinary written answer with its formatting, commands and links unchanged in purpose. The spoken rendition must convey the same factual meaning and qualifications as the written answer. Explain the meaning of code and commands naturally instead of reading syntax. Use optional short ElevenLabs expressive cues such as [curious] or [warmly] sparingly. This is a spoken presentation, not private reasoning. Never put a speech block in commentary or a tool call. Never nest or repeat the delimiters.`;
type RpcOptions = Parameters<Parameters<OpenClawPluginApi['registerGatewayMethod']>[1]>[0];
const validId = (value: unknown): value is string => typeof value === 'string' && value.length > 0 && value.length <= 512 && value.trim().length > 0;
const compatible = (value: unknown): boolean => !!value && typeof value === 'object' && 'major' in value && value.major === 1;

export default {
  id: 'expressive-speech',
  register(api: OpenClawPluginApi) {
    const state = new SpeechState();
    // Development package versions are not unique build identities. The named
    // profile is an explicit deployment opt-in; the external launcher must pin
    // and verify that official artifact. The public SDK exposes only version.
    const hostSupported = () => api.runtime.version === '2026.9.4'
      ? api.pluginConfig?.hostCompatibilityProfile === undefined || api.pluginConfig.hostCompatibilityProfile === 'release-2026.9.4'
      : api.runtime.version === '2026.9.6' && api.pluginConfig?.hostCompatibilityProfile === DEVELOPMENT_PROFILE;
    const enabled = () => api.pluginConfig?.enabled === true && hostSupported();
    // The public model resolver still types its read-only input as mutable config.
    // Never mutate this host-owned snapshot.
    const currentConfig = () => api.runtime.config.current() as OpenClawPluginApi['config'];
    const selectedModelSupported = (sessionKey: string, entry: NonNullable<ReturnType<typeof api.runtime.agent.session.getSessionEntry>>, cfg: OpenClawPluginApi['config']) => {
      const agentId = sessionKey.startsWith('agent:') ? sessionKey.split(':')[1] : undefined;
      if (!agentId) return false;
      const defaults = api.runtime.modelConfig.resolveDefaultModelForAgent({ cfg, agentId });
      const config = cfg.models?.providers?.[PROVIDER];
      return (entry.providerOverride ?? defaults.provider) === PROVIDER && (entry.modelOverride ?? defaults.model) === MODEL
        && config?.api === 'openai-chatgpt-responses' && config.baseUrl.replace(/\/+$/, '') === BASE_URL;
    };
    const caller = (opts: RpcOptions): Caller => {
      const deviceId = opts.client?.connect.device?.id;
      const connectionId = opts.client?.connId;
      if (!deviceId || !connectionId || opts.client?.connect.role !== 'operator' || !opts.client.connect.scopes?.includes('operator.admin') || !opts.hasCurrentClientAuthority?.()) throw new Error('signed-admin-device-required');
      return { deviceId, connectionId };
    };
    const current = (intent: Intent) => {
      const incarnation = intent.identity?.sessionIncarnation ?? intent.expectedSessionId;
      const entry = api.runtime.agent.session.getSessionEntry({ sessionKey: intent.sessionKey, readConsistency: 'latest' });
      return incarnation ? entry?.sessionId === incarnation && entry.lifecycleRevision === intent.expectedLifecycleRevision && !entry.archivedAt : !entry?.archivedAt;
    };
    const rpc = (name: string, handler: (opts: RpcOptions, owner: Caller) => unknown) => api.registerGatewayMethod(`expressive-speech.${name}`, (opts) => {
      try { opts.respond(true, handler(opts, caller(opts))); }
      catch (error) { opts.respond(false, undefined, { code: 'EXPRESSIVE_SPEECH_UNAVAILABLE', message: error instanceof Error ? error.message : 'speech-unavailable' }); }
    }, { scope: 'operator.admin', profileAccess: 'required' });

    api.registerGatewayMethod('expressive-speech.capabilities', (opts) => {
      let reason: string | undefined;
      try { caller(opts); } catch { reason = 'signed-admin-device-required'; }
      if (!enabled()) reason = hostSupported() ? 'plugin-disabled' : 'unsupported-host';
      // Without a session key these are only host capabilities. Session readiness
      // is checked again by intent, including current per-session core TTS policy.
      if (!reason && opts.params.sessionKey !== undefined) {
        if (!validId(opts.params.sessionKey)) reason = 'invalid-session';
        else {
          const entry = api.runtime.agent.session.getSessionEntry({ sessionKey: opts.params.sessionKey, readConsistency: 'latest' });
          if (!entry?.sessionId || entry.archivedAt) reason = 'invalid-session';
          else if (entry.ttsAuto !== 'off') reason = 'session-core-tts-must-be-off';
          else if (!selectedModelSupported(opts.params.sessionKey, entry, currentConfig())) reason = 'unsupported-model';
        }
      }
      const available = !reason;
      opts.respond(true, { protocol, available, prepared: available, live: available, limits: { maxSpeechCharacters: MAX_SPEECH_CHARACTERS }, ...(reason ? { reason } : {}) });
    }, { scope: 'operator.read', profileAccess: 'required' });
    rpc('intent', (opts, owner) => {
      if (!enabled()) throw new Error('plugin-unavailable');
      const { sessionKey, idempotencyKey, delivery } = opts.params;
      if (!compatible(opts.params.protocol) || !validId(sessionKey) || !validId(idempotencyKey) || (delivery !== 'prepared' && delivery !== 'live')) throw new Error('invalid-intent');
      // Admin scope has the core's global read authority. Reject sessions whose
      // policy prevents reply_dispatch, so an intent cannot wait indefinitely.
      const entry = api.runtime.agent.session.getSessionEntry({ sessionKey, readConsistency: 'latest' });
      if (!entry?.sessionId || entry.archivedAt) throw new Error('existing-session-required');
      if (entry.ttsAuto !== 'off') throw new Error('session-core-tts-must-be-off');
      if (!selectedModelSupported(sessionKey, entry, currentConfig())) throw new Error('unsupported-model');
      if (entry?.permissionMode && entry.permissionMode !== 'full' || entry?.toolOverrides) throw new Error('unsupported-session-policy');
      if (entry?.acp || entry?.agentHarnessId && entry.agentHarnessId !== 'openclaw') throw new Error('unsupported-runner');
      if (entry?.elevatedLevel || entry?.execHost || entry?.execNode || entry?.execCwd || entry?.agentRuntimeOverride && entry.agentRuntimeOverride !== 'openclaw') throw new Error('unsupported-session-overrides');
      const maxSpeechCharacters = opts.params.maxSpeechCharacters ?? MAX_SPEECH_CHARACTERS;
      if (typeof maxSpeechCharacters !== 'number') throw new Error('invalid-speech-limit');
      const intent = state.create(owner, sessionKey, idempotencyKey, delivery, maxSpeechCharacters, entry.sessionId, entry.lifecycleRevision);
      return { ticket: intent.ticket, expiresAt: intent.expiresAt };
    });
    rpc('read', (opts, owner) => {
      if (!validId(opts.params.ticket)) throw new Error('intent-unavailable');
      const intent = state.owned(opts.params.ticket, owner);
      if (!current(intent)) { state.fail(intent, 'session-invalidated', 'cancelled'); state.detach(intent); throw new Error('session-invalidated'); }
      const after = opts.params.afterSequence;
      const count = opts.params.maxEvents;
      if (!Number.isSafeInteger(after) || Number(after) < -1 || Number(after) >= intent.events.length && Number(after) !== -1 || !Number.isSafeInteger(count) || Number(count) < 1 || Number(count) > 64) throw new Error('invalid-cursor');
      const events = [];
      let bytes = 0;
      for (const event of intent.events.slice(Number(after) + 1, Number(after) + 1 + Number(count))) {
        const size = Buffer.byteLength(JSON.stringify(event));
        if (bytes + size > 360 * 1024) break;
        events.push(event); bytes += size;
      }
      return { events, state: intent.state, retryAfterMs: 100 };
    });
    rpc('detach', (opts, owner) => {
      if (!validId(opts.params.ticket)) throw new Error('intent-unavailable');
      state.detach(state.owned(opts.params.ticket, owner));
      return { detached: true };
    });

    api.registerProvider({ id: PROVIDER, label: 'OpenAI expressive speech', auth: [], wrapStreamFn: (context) => wrapSpeechStream({ ...context, streamFn: wrapChatGptTransport(context) }, turns) });
    api.on('before_prompt_build', (_event, context) => {
      const turn = turns.getStore();
      if (turn && context.modelProviderId === PROVIDER && context.modelId === MODEL) return { appendSystemContext: `${GUIDANCE} Keep the entire speech block under ${Math.floor(turn.intent.maxSpeechCharacters * 0.9)} characters, including expressive cues. Keep the written answer as detailed as needed.` };
    });
    api.on('before_message_write', (event) => {
      const turn = turns.getStore();
      if (!turn?.intent.metadata || event.message.role !== 'assistant') return;
      if (!turn.current()) { state.fail(turn.intent, 'session-invalidated', 'cancelled'); return; }
      return { message: { ...event.message, openclawExpressiveSpeech: turn.intent.metadata } };
    });
    const unsubscribe = api.runtime.events.onSessionTranscriptUpdate((update) => {
      if (!update.runId || !update.messageId) return;
      if (!update.target.sessionKey) return;
      const intent = state.findByRun(update.runId, update.target.sessionKey);
      if (!intent?.metadata || !current(intent) || update.target.sessionId !== intent.identity?.sessionIncarnation) return;
      const message = update.message;
      if (message && typeof message === 'object' && 'role' in message && message.role === 'assistant' && 'openclawExpressiveSpeech' in message) state.persisted(intent, update.messageId);
    });
    api.registerService({ id: 'expressive-speech', start: () => {}, stop: () => { unsubscribe(); state.clear(); } });

    api.on('reply_dispatch', async (event, ctx) => {
      if (!enabled() || !event.sessionKey || !event.runId || !event.ctx.ApprovalReviewerDeviceId) return;
      const entry = api.runtime.agent.session.getSessionEntry({ sessionKey: event.sessionKey, readConsistency: 'latest' });
      const intent = state.admit(event.ctx.ApprovalReviewerDeviceId, event.sessionKey, event.ctx.MessageSid ?? event.runId, entry?.sessionId ?? '');
      if (!intent) return;
      const rejectAdmitted = async (reason: string) => {
        state.fail(intent, reason, 'cancelled');
        try {
          await ctx.onReplyStart?.();
          ctx.onAgentRunStart?.(event.runId!);
          // The host recorder owns exact-once input persistence. Never recreate
          // an invalidated/reset transcript merely to record this cancellation.
          if (entry?.sessionId && current(intent)) await ctx.userTurnTranscriptRecorder?.persistApproved({ expectedSessionId: entry.sessionId });
        } catch {
          // Returning unhandled after a recorder failure could re-enter core TTS.
          api.logger.warn('Expressive speech cancellation could not confirm input persistence.');
        }
        const queuedFinal = ctx.dispatcher.sendFinalReply({ text: 'The session changed before expressive speech could start. Please check the session settings and retry.', isError: true });
        await ctx.dispatcher.waitForIdle();
        ctx.recordProcessed('error', { reason }); ctx.markIdle('expressive speech admission cancelled');
        return { handled: true, queuedFinal, counts: ctx.dispatcher.getQueuedCounts() };
      };
      if (!entry?.sessionId || !current(intent)) return rejectAdmitted('session-invalidated');
      if (entry.ttsAuto !== 'off') return rejectAdmitted('session-core-tts-must-be-off');
      if (entry.permissionMode && entry.permissionMode !== 'full' || entry.toolOverrides) return rejectAdmitted('unsupported-session-policy');
      const agentId = event.sessionKey.startsWith('agent:') ? event.sessionKey.split(':')[1] : undefined;
      if (!agentId || ctx.dispatchKind !== 'agent' || event.shouldRouteToOriginating || event.ctx.CommandTurn?.kind !== 'normal' || entry.acp || (entry.agentHarnessId && entry.agentHarnessId !== 'openclaw') || event.ctx.Provider !== 'webchat' || event.ctx.InternalTurnSource || event.ctx.ChatType === 'group' || event.suppressUserDelivery || event.isTailDispatch || event.sendPolicy !== 'allow' || event.sourceReplyDeliveryMode && event.sourceReplyDeliveryMode !== 'automatic') {
        return rejectAdmitted('unsupported-runner');
      }
      if (entry.elevatedLevel || entry.execHost || entry.execNode || entry.execCwd || entry.agentRuntimeOverride && entry.agentRuntimeOverride !== 'openclaw') return rejectAdmitted('unsupported-session-overrides');
      const defaults = api.runtime.modelConfig.resolveDefaultModelForAgent({ cfg: ctx.cfg, agentId });
      const provider = entry.providerOverride ?? defaults.provider;
      const model = entry.modelOverride ?? defaults.model;
      const config = ctx.cfg.models?.providers?.[PROVIDER];
      if (provider !== PROVIDER || model !== MODEL || config?.api !== 'openai-chatgpt-responses' || config.baseUrl.replace(/\/+$/, '') !== BASE_URL) {
        return rejectAdmitted('unsupported-model');
      }
      const turn: TurnScope = { intent, state, current: () => !ctx.abortSignal?.aborted && current(intent), modelCalls: 0 };
      const runId = event.runId;
      const preferences = resolveAgentConfig(ctx.cfg, agentId);
      const verbose = entry.verboseLevel ?? preferences?.verboseDefault;
      const reasoning = entry.reasoningLevel ?? preferences?.reasoningDefault;
      try {
        await ctx.onReplyStart?.();
        ctx.onAgentRunStart?.(event.runId);
        const result = await turns.run(turn, () => api.runtime.agent.runEmbeddedAgent({
          config: ctx.cfg, agentId, sessionId: entry.sessionId, sessionKey: event.sessionKey,
          workspaceDir: api.runtime.agent.resolveAgentWorkspaceDir(ctx.cfg, agentId),
          agentDir: api.runtime.agent.resolveAgentDir(ctx.cfg, agentId),
          provider, model, modelSelectionLocked: entry.modelSelectionLocked,
          authProfileId: entry.authProfileOverride, authProfileIdSource: entry.authProfileOverrideSource === 'auto' ? 'auto' : entry.authProfileOverrideSource ? 'user' : undefined,
          thinkLevel: api.runtime.agent.normalizeThinkingLevel(entry.thinkingLevel ?? preferences?.thinkingDefault) ?? api.runtime.agent.resolveThinkingDefault({ cfg: ctx.cfg, provider, model }),
          fastMode: entry.fastMode ?? preferences?.fastModeDefault,
          verboseLevel: verbose === 'on' || verbose === 'full' ? verbose : 'off',
          reasoningLevel: reasoning === 'on' || reasoning === 'stream' ? reasoning : 'off',
          contextWindow: entry.contextWindow, permissionMode: entry.permissionMode, toolOverrides: entry.toolOverrides,
          prompt: event.ctx.BodyForAgent ?? event.ctx.Body ?? '', images: event.images?.map((image) => ({ type: 'image' as const, ...image })), toolsAllow: event.toolsAllow,
          messageProvider: event.ctx.Provider, messageChannel: event.ctx.Surface,
          senderId: event.ctx.SenderId, senderIsOwner: event.ctx.GatewayClientScopes?.includes('operator.admin'),
          approvalReviewerDeviceId: event.ctx.ApprovalReviewerDeviceId, clientCaps: event.ctx.GatewayClientCaps,
          senderName: event.ctx.SenderName, senderUsername: event.ctx.SenderUsername,
          conversationToolPolicy: event.ctx.ConversationToolPolicy,
          currentMessageId: event.ctx.MessageSid, trigger: 'user',
          runId, timeoutMs: api.runtime.agent.resolveAgentTimeoutMs({ cfg: ctx.cfg }),
          abortSignal: ctx.abortSignal, userTurnTranscriptRecorder: ctx.userTurnTranscriptRecorder,
          prepareAssistantTranscriptMessage: ctx.prepareAssistantTranscriptMessage,
          onToolResult: (payload) => { if (event.shouldSendToolSummaries || event.shouldSendFullToolDetails) ctx.dispatcher.sendToolResult(payload); },
          onBlockReply: (payload) => { ctx.dispatcher.sendBlockReply(payload); },
        }));
        let queuedFinal = false;
        for (const payload of result.payloads ?? []) queuedFinal = ctx.dispatcher.sendFinalReply(payload) || queuedFinal;
        await ctx.dispatcher.waitForIdle();
        if (intent.state === 'active') state.fail(intent, 'rendition-not-persisted', 'unavailable');
        ctx.recordProcessed('completed'); ctx.markIdle('expressive speech completed');
        return { handled: true, queuedFinal, counts: ctx.dispatcher.getQueuedCounts() };
      } catch {
        state.fail(intent, ctx.abortSignal?.aborted ? 'run-cancelled' : 'run-failed', ctx.abortSignal?.aborted ? 'cancelled' : 'failed');
        ctx.recordProcessed('error', { reason: 'expressive-speech-run-failed' }); ctx.markIdle('expressive speech failed');
        const queuedFinal = ctx.dispatcher.sendFinalReply({ text: 'The assistant run could not finish. Please retry.', isError: true });
        return { handled: true, queuedFinal, counts: ctx.dispatcher.getQueuedCounts() };
      }
    }, { eligibleDispatchKinds: ['agent'] });
  },
};
