# Expressive speech Gateway plugin

Experimental implementation for OpenClaw **2026.9.4**, disabled by default. It composes a separate spoken rendition in the same model generation while keeping ordinary written chat and history. Windows Companion owns ElevenLabs credentials, synthesis and playback. The Gateway plugin never contacts ElevenLabs.

The initial supported route is ordinary Companion `chat.send` on an existing, unrestricted core OpenClaw session, using a signed `operator.admin` device and explicitly selected `expressive-speech-openai/gpt-5.6-sol` over `openai-chatgpt-responses`. The provider uses the endpoint-scoped public auth alias to reuse the existing `openai` OAuth realm at `https://chatgpt.com/backend-api/codex`. It does not copy credentials or implement another login flow. The `/codex` base is required: the decorated provider does not inherit the built-in OpenAI provider's legacy URL normalization. The resulting request path is `/backend-api/codex/responses`; the old `/backend-api/responses` path is rejected by the host fixture.

The auth alias also does not inherit native request shaping gated on provider identity. A narrow `chatgpt-transport.ts` adapter uses the public stream wrapper and `onPayload` hook to apply the pinned hosts' native ChatGPT contract: full-context replay, `store: false`, removal of unsupported token/sampling/cache/metadata/service-tier fields and `text.format`. Caller payload hooks run first; tools, reasoning, existing headers, abort signal, credentials and the original model/runtime owner remain intact. No private sanitizer import, model identity rewrite, or separate transport is used. The public stream-family helper does not supply this identity-gated sanitizer. This explicit compatibility adapter must be reviewed alongside upstream request/replay changes when adding supported host versions; it does not claim inheritance of every OpenAI provider hook or optional transport optimization. Strict HTTP fixtures enforce both the endpoint and stateless payload contract.

## Integration

Load this directory through the host's supported plugin installer or `plugins.load.paths`, allow `expressive-speech`, and enable its entry with `config.enabled: true` only after compatibility validation. Prompt composition requires the host's conversation and prompt-injection hook permissions. Add an explicit model provider named `expressive-speech-openai`, with `api: "openai-chatgpt-responses"`, `auth: "oauth"`, and `baseUrl: "https://chatgpt.com/backend-api/codex"`. Copy the existing selected model's published catalog metadata; do not invent context limits, costs or reasoning capabilities. Opt the intended agent/session into the decorated provider/model through normal configuration or model selection.

In each selected chat, run **`/tts chat off`** before enabling Dialog. This persists the core TTS override for that session only. The plugin refuses an intent unless the current session explicitly has core TTS off, even if the global default happens to be off. This keeps duplicate core synthesis disabled if a later policy change prevents the takeover hook from running. Other chats keep their own TTS settings. `/tts off` is a broader preference command and is not this prerequisite.

No production configuration is changed by this repository. Keep the existing provider/model selection available for rollback. Disabling the plugin and restoring that selection returns the standard path; existing written history remains ordinary core history. Stored rendition replay uses normal authorized `chat.history`, independently of whether the plugin is enabled or the reader has admin scope.

## Public interfaces and ownership

| Interface | Responsibility |
| --- | --- |
| `expressive-speech.capabilities` | Versioned availability and the 2,000 UTF-16 character speech limit. Optional `{sessionKey}` checks selected model and core-TTS readiness; no key means host-only capabilities. Availability does not grant permission to read history. |
| `expressive-speech.intent` | Register one prepared/live intent for a session and client idempotency key before normal `chat.send`. Returns an opaque 15-minute ticket. |
| `expressive-speech.read` | Bounded ordered delivery events, at most 64 per response. Original authenticated device and connection only; current authority and session revision are checked on every call. |
| `expressive-speech.detach` | Release this connection's delivery ticket. Does not abort the agent or delete its later persisted rendition. |
| `reply_dispatch` | Claim eligible opted turns; invoke the public stock embedded runner with host recorder, transcript preparer, abort, tools, sender and approval identity. Forward written results through the host dispatcher, bypassing core automatic TTS. |
| `before_prompt_build` | Add a speech-first composition guide with a target below the configured character ceiling. Prompt text does not implement the split or authorize delivery. |
| `registerProvider.wrapStreamFn` | Split the raw provider stream into clean written output and hidden speech before the stock runner can broadcast or persist it. |
| `before_message_write` | Attach completed `openclawExpressiveSpeech` metadata separately from the written content. |
| `runtime.events.onSessionTranscriptUpdate` | Complete delivery only after the real committed message ID is known. |

The provider wrapper uses plugin-module `AsyncLocalStorage`, not a register-call-local instance: the host can register a module again for provider activation. It creates a new event stream and does not mutate provider-owned event snapshots.

`expiresAt` is the whole ticket lifetime, including admission, active polling and detach. After 15 minutes the server rejects the ticket; the agent may still finish and persist ordinary written output and rendition metadata. It is not an admission-only timeout. The server's time is authoritative.

Live speech requires authoritative provider `final_answer` phase present before text deltas. Missing early phase fails closed; prompt delimiters alone cannot make commentary or a tool turn eligible. Prepared speech waits for a successful non-tool final. Malformed, repeated, oversized or incomplete speech never becomes a successful rendition. Written Markdown literals remain written.

Malformed structure, invalid Unicode or parser overflow preserve the already safe written prefix and fail the assistant completion rather than reporting a successful shortened answer. A complete answer without a speech block remains ordinary written success with speech unavailable. A structurally valid speech block exceeding the independent synthesis character budget also preserves the complete written answer.

The protocol's `sessionIncarnation` currently carries the core physical `sessionId` for history binding. **On this host, reset preserves that ID and changes `lifecycleRevision`.** Tickets separately bind the actual lifecycle revision. The Companion must also cancel current playback on authoritative `sessions.changed` reset/delete/archive events and connection loss; physical ID equality alone is not reset detection.

## Initial compatibility limits

- Runtime 2026.9.4 is admitted by default. The exact official development commit `a181c3f1d9086f52d5c198b69553f689715f6109` (package version 2026.9.6) is also supported with explicit plugin config `hostCompatibilityProfile: "upstream-a181c3f1d9086f52d5c198b69553f689715f6109"`. Its external deployment must verify the pinned artifact hash and full build-info commit before launch. The SDK exposes only package version; this profile is a compatibility opt-in, not cryptographic attestation or permission to use arbitrary .6 builds. Native 2026.9.7 remains excluded. Only the explicit model/API/endpoint above is admitted; other selections continue ordinary written chat with speech unavailable.
- Existing session required; the first turn creating a new session can remain written-only.
- Automatic/live RPCs require a currently authorized signed admin device. Manual replay remains core-history-authorized, including read-only users who can read the message.
- Session exec/elevated overrides, restricted tool/permission sessions, custom harnesses, routed/group/internal turns and slash commands are not eligible. Intent rejects observable incompatibility before send. If compatibility changes after admission and the hook runs, the plugin claims an explicit cancelled turn/retry error without core-TTS fallthrough; it retains the admitted input through the host recorder when the transcript remains current. This cancellation is an exception to normal written-answer continuation.
- A concurrent explicit re-enable of core TTS is outside the supported admission policy. It is rejected when observed. Host policy can suppress the hook altogether, so the plugin cannot promise atomic interception across simultaneous administrative changes. The persistent chat-scoped off prerequisite avoids that dependence during ordinary supported operation.
- The public hook exposes signed device and admitted idempotency identity, but not the original submitting connection ID. Delivery tickets are connection-owned; admission is signed-device-owned. A second connection with the same device identity and same idempotency key can name that admitted turn. This is not proof of exact originating-connection admission.
- Session and configured thinking/fast/verbosity/reasoning preferences are forwarded. The hook does not expose arbitrary per-turn `GetReplyOptions` overrides or the outer retry/queue orchestration. Companion's supported path persists its session options before sending. Broader client/retry parity is not claimed.
- Public plugin APIs are experimental. Pinning and runtime compatibility tests are still maintenance requirements even without a Gateway fork.

## Validation

```powershell
npm ci
npm run check
npm test
npm run build
$env:OPENCLAW_PROBE_ENTRY = Join-Path $PWD 'node_modules\openclaw\openclaw.mjs'
$env:OPENCLAW_PROBE_PRODUCTION = '1'
node test/host-probe.mjs
# Optional stock read-tool attempt followed by final answer:
$env:OPENCLAW_PROBE_TOOL = '1'
node test/host-probe.mjs
# Preferred distribution acceptance: build, pack, unpack and test compiled code.
npm run probe:packed
# Required compatibility matrix, including applicable/non-applicable controls
# and a long rendition that triggers actual core summarization when unclaimed:
npm run probe:matrix
# Official development checkout proof (must be clean and built at this exact commit):
$env:OPENCLAW_PROBE_ENTRY = 'V:\worktrees\openclaw-upstream-a181c3f1\openclaw.mjs'
$env:OPENCLAW_PROBE_HOST_PROFILE = 'upstream-a181c3f1d9086f52d5c198b69553f689715f6109'
npm run probe:packed
```

The production probe loads the actual plugin entry point into an isolated real host. A loopback CONNECT proxy and process-scoped ephemeral test certificate supply synthetic ChatGPT Responses SSE at the exact configured endpoint. Only an isolated dummy OAuth profile is created; no production credential, OS trust store or global proxy setting is used. OpenSSL is required for this fixture, with `OPENCLAW_PROBE_OPENSSL` available to select its executable. Temporary evidence includes ordinary chat/history, real message receipts, authorization negatives, reset notifications and request counts.

Unset `OPENCLAW_PROBE_PRODUCTION` for the independent seam/negative-control probe. `OPENCLAW_PROBE_CONTROL=1` tests ordinary chat with plugins disabled; `OPENCLAW_PROBE_ORCHESTRATOR=1` reproduces the unsupported nested `getReplyFromConfig` candidate. These controls are research fixtures, not alternative production routes.

Passing synthetic host checks do not establish native packaged deployment, real OAuth refresh, or end-to-end Companion operation. See the dated compatibility record in `V:\docs\ai\speech\2026-10-01-expressive-speech-host-compatibility.md` for evidence and outstanding gates.
