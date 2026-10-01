import assert from "node:assert/strict";
import { execFileSync, spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, mkdir, readFile, writeFile } from "node:fs/promises";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID, generateKeyPairSync, createHash, sign } from "node:crypto";
import { createRequire } from "node:module";
import { setTimeout as delay } from "node:timers/promises";
import { startFixtureProxy } from './fixture-proxy.mjs';
import { connectFixtureClient } from './fixture-client.mjs';

// The assertions concern real host behavior, not mock hook dispatch. The local
// HTTP server supplies deterministic provider tokens without billing or secrets.
const entry = process.env.OPENCLAW_PROBE_ENTRY;
const controlOnly = process.env.OPENCLAW_PROBE_CONTROL === "1";
const canonicalControl = controlOnly && process.env.OPENCLAW_PROBE_CANONICAL === '1';
const production = process.env.OPENCLAW_PROBE_PRODUCTION === '1';
const rejectedHostProfile = production && process.env.OPENCLAW_PROBE_REJECT_HOST_PROFILE === '1';
const withTool = production && process.env.OPENCLAW_PROBE_TOOL === '1';
const longSpeech = process.env.OPENCLAW_PROBE_LONG_SPEECH === '1';
const ttsMode = process.env.OPENCLAW_PROBE_TTS_MODE ?? 'always';
assert.ok(['always', 'tagged', 'inbound', 'off'].includes(ttsMode));
const fixtureSpeech = longSpeech ? '[curious] ' + 'A complete spoken explanation. '.repeat(55) : '[curious] Spoken only.';
const wav = Buffer.alloc(364);
wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16);
wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(16000, 24); wav.writeUInt32LE(32000, 28);
wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(320, 40);
const inboundAttachments = ttsMode === 'inbound' ? [{ type: 'audio', mimeType: 'audio/wav', fileName: 'synthetic-silence.wav', content: wav.toString('base64') }] : undefined;
const chatgptTransport = canonicalControl || production || process.env.OPENCLAW_PROBE_CHATGPT === "1";
assert.ok(entry, "Set OPENCLAW_PROBE_ENTRY to an installed openclaw.mjs entrypoint.");
const hostProfile = process.env.OPENCLAW_PROBE_HOST_PROFILE;
if (hostProfile) {
  const commit = 'a181c3f1d9086f52d5c198b69553f689715f6109';
  assert.equal(hostProfile, `upstream-${commit}`);
  const root = dirname(resolve(entry));
  const build = JSON.parse(await readFile(join(root, 'dist', 'build-info.json'), 'utf8'));
  assert.equal(build.version, '2026.9.6');
  assert.equal(build.commit, commit);
  assert.equal(execFileSync('git', ['-C', root, 'rev-parse', 'HEAD'], { encoding: 'utf8', windowsHide: true }).trim(), commit);
  assert.equal(execFileSync('git', ['-C', root, 'status', '--porcelain', '--untracked-files=no'], { encoding: 'utf8', windowsHide: true }).trim(), '', 'Development compatibility requires an unmodified official checkout.');
}
const WebSocketClient = createRequire(resolve(entry))("ws");
const directory = await mkdtemp(join(tmpdir(), "expressive-speech-host-"));
const workspace = join(directory, "workspace");
await mkdir(workspace);
await writeFile(join(workspace, 'speech-probe.txt'), 'STOCK_TOOL_READ_PROOF');
const modelRequests = [];
const rejectedProviderPaths = [];
const rejectedProviderPayloads = [];
let modelCompleted = false;
const model = createServer(async (req, res) => {
  if (req.method !== 'POST') { res.writeHead(404, { 'content-type': 'application/json' }); res.end('{"error":"Synthetic fixture exposes model POST requests only."}'); return; }
  // The native ChatGPT transport requires /codex/responses. Accepting the old
  // /backend-api/responses alias hid a real decorated-provider routing defect.
  if (req.url?.startsWith('/backend-api') && req.url !== '/backend-api/codex/responses') {
    rejectedProviderPaths.push(req.url);
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end('{"error":"Unsupported ChatGPT Responses endpoint"}');
    return;
  }
  let body = "";
  for await (const chunk of req) body += chunk;
  if (!body) { res.writeHead(400); res.end(); return; }
  if (req.url === '/backend-api/codex/responses') {
    const request = JSON.parse(body);
    const forbidden = ['max_output_tokens', 'metadata', 'prompt_cache_retention', 'prompt_cache_options', 'service_tier', 'temperature', 'top_p'].filter((key) => Object.hasOwn(request, key));
    if (request.text && Object.hasOwn(request.text, 'format')) forbidden.push('text.format');
    if (request.store !== false) forbidden.push('store must be false');
    if (request.input?.some((item) => item.type === 'item_reference')) forbidden.push('item_reference');
    if (forbidden.length) {
      rejectedProviderPayloads.push(forbidden);
      res.writeHead(400, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: `Unsupported native ChatGPT payload: ${forbidden.join(', ')}` } }));
      return;
    }
  }
  modelRequests.push({ path: req.url, body: JSON.parse(body) });
  res.writeHead(200, { "content-type": "text/event-stream" });
  const chunks = body.includes('UNTAGGED_CONTROL') ? ['An ordinary answer without speech directives.'] : production ? ['[[tts:', 'text]]', fixtureSpeech, '[[/tts:text]]', 'Visible. ', 'End.'] : ["Visible. ", "[[tts:", "text]]", fixtureSpeech, "[[/tts:text]]", " End."];
  if (chatgptTransport) {
    const send = (event) => res.write(`data: ${JSON.stringify(event)}\n\n`);
    if (withTool && modelRequests.length === 1) {
      const item = { id: 'fc_probe', type: 'function_call', call_id: 'call_probe', name: 'read', arguments: '', status: 'in_progress' };
      send({ type: 'response.created', response: { id: 'resp_tool', status: 'in_progress', model: 'synthetic' } });
      send({ type: 'response.output_item.added', output_index: 0, item });
      const args = JSON.stringify({ path: join(workspace, 'speech-probe.txt') });
      send({ type: 'response.function_call_arguments.delta', output_index: 0, item_id: item.id, delta: args });
      item.arguments = args; item.status = 'completed';
      send({ type: 'response.output_item.done', output_index: 0, item });
      send({ type: 'response.completed', response: { id: 'resp_tool', status: 'completed', model: 'synthetic', output: [item], usage: { input_tokens: 10, output_tokens: 12, input_tokens_details: { cached_tokens: 0 } } } });
      res.end(); return;
    }
    const item = { id: "msg_probe", type: "message", role: "assistant", phase: "final_answer", status: "in_progress", content: [] };
    send({ type: "response.created", response: { id: "resp_probe", status: "in_progress", model: "synthetic" } });
    send({ type: "response.output_item.added", output_index: 0, item });
    send({ type: "response.content_part.added", output_index: 0, content_index: 0, item_id: item.id, part: { type: "output_text", text: "", annotations: [] } });
    for (const delta of chunks) { send({ type: "response.output_text.delta", output_index: 0, content_index: 0, item_id: item.id, delta }); await delay(production ? 200 : 30); }
    item.status = "completed";
    item.content = [{ type: "output_text", text: chunks.join(""), annotations: [] }];
    send({ type: "response.output_item.done", output_index: 0, item });
    send({ type: "response.completed", response: { id: "resp_probe", status: "completed", model: "synthetic", output: [item], usage: { input_tokens: 10, output_tokens: 12, input_tokens_details: { cached_tokens: 0 } } } });
    res.end();
    modelCompleted = true;
    return;
  }
  for (const text of chunks) {
    res.write(`data: ${JSON.stringify({ id: "probe", object: "chat.completion.chunk", created: 1, model: "synthetic", choices: [{ index: 0, delta: { content: text }, finish_reason: null }] })}\n\n`);
    await delay(30);
  }
  res.end(`data: ${JSON.stringify({ id: "probe", object: "chat.completion.chunk", created: 1, model: "synthetic", choices: [{ index: 0, delta: {}, finish_reason: "stop" }], usage: { prompt_tokens: 10, completion_tokens: 12, total_tokens: 22 } })}\n\ndata: [DONE]\n\n`);
});
model.listen(0, "127.0.0.1");
await once(model, "listening");
const reservation = createServer();
reservation.listen(0, "127.0.0.1");
await once(reservation, "listening");
const port = reservation.address().port;
await new Promise((resolveClose) => reservation.close(resolveClose));
const token = randomUUID();
const fixture = resolve(dirname(fileURLToPath(import.meta.url)), "fixture-plugin");
const config = {
  gateway: { mode: "local", port, bind: "loopback", auth: { mode: "token", token } },
  tts: { auto: ttsMode, provider: "probe-tts", summaryModel: 'probe/synthetic' },
  agents: { defaults: { workspace, model: { primary: "probe/synthetic" }, skipBootstrap: true } },
  models: { providers: { probe: { api: "openai-completions", baseUrl: `http://127.0.0.1:${model.address().port}/v1`, apiKey: "synthetic-not-a-secret", models: [{ id: "synthetic", name: "Synthetic fixture", reasoning: false, input: ["text"], contextWindow: 32768, maxTokens: 4096, cost: { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 } }] } } },
  plugins: { allow: ["expressive-speech-probe"], load: { paths: [fixture] }, entries: { "expressive-speech-probe": { enabled: true, hooks: { allowConversationAccess: true } } } },
};
if (controlOnly) { config.plugins = { enabled: false }; delete config.tts; }
else {
  config.models.providers["probe-openai"] = { baseUrl: "https://chatgpt.com/backend-api/codex", api: "openai-chatgpt-responses", auth: "oauth", models: [{ id: "gpt-5.6-sol", name: "Synthetic alias only" }] };
  config.auth = { order: { openai: ["openai:synthetic-probe"] } };
}
if (chatgptTransport) {
  config.models.providers.probe.api = "openai-chatgpt-responses";
  config.models.providers.probe.apiKey = `e30.${Buffer.from(JSON.stringify({ "https://api.openai.com/auth": { chatgpt_account_id: "synthetic-account" } })).toString("base64url")}.synthetic`;
}
let proxy;
if (canonicalControl) {
  config.models.providers.openai = { ...config.models.providers.probe, baseUrl: 'https://chatgpt.com/backend-api/codex', models: [{ ...config.models.providers.probe.models[0], id: 'gpt-5.6-sol' }] };
  config.agents.defaults.model.primary = 'openai/gpt-5.6-sol';
  proxy = await startFixtureProxy(directory, model.address().port);
}
if (production) {
  config.models.providers['expressive-speech-openai'] = { ...config.models.providers.probe, baseUrl: 'https://chatgpt.com/backend-api/codex', auth: 'oauth', models: [{ ...config.models.providers.probe.models[0], id: 'gpt-5.6-sol' }] };
  delete config.models.providers['expressive-speech-openai'].apiKey;
  config.agents.defaults.model.primary = 'expressive-speech-openai/gpt-5.6-sol';
  config.plugins.allow.push('expressive-speech');
  config.plugins.load.paths.push(process.env.OPENCLAW_PROBE_PLUGIN ?? resolve(dirname(fileURLToPath(import.meta.url)), '..'));
  config.plugins.entries['expressive-speech'] = { enabled: true, hooks: { allowConversationAccess: true, allowPromptInjection: true }, config: { enabled: true, ...(hostProfile && !rejectedHostProfile ? { hostCompatibilityProfile: hostProfile } : {}) } };
  proxy = await startFixtureProxy(directory, model.address().port);
}
await writeFile(join(directory, "openclaw.json"), JSON.stringify(config));
const childEnv = { ...process.env, OPENCLAW_STATE_DIR: directory, OPENCLAW_CONFIG_PATH: join(directory, "openclaw.json"), EXPRESSIVE_SPEECH_PROBE_WORKSPACE: workspace, OPENCLAW_SKIP_CHANNELS: "1" };
if (proxy) { Object.assign(childEnv, proxy.env); childEnv.EXPRESSIVE_SPEECH_PROBE_DUMMY_JWT = config.models.providers.probe.apiKey; }
// Prevent ambient provider credentials/config from entering this synthetic host.
for (const key of Object.keys(childEnv)) if (/^(OPENAI|ANTHROPIC|ELEVENLABS|MINIMAX|TELEGRAM|DISCORD|SLACK)_/.test(key)) delete childEnv[key];
const child = spawn(process.execPath, [resolve(entry), "gateway", "run", "--port", String(port), "--bind", "loopback"], { env: childEnv, windowsHide: true, stdio: ["ignore", "pipe", "pipe"] });
let logs = "";
child.stdout.on("data", (data) => { logs += data; });
child.stderr.on("data", (data) => { logs += data; });
let socket;
const frames = [];
try {
  const deadline = Date.now() + 45000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`Host exited ${child.exitCode}`);
    try { if ((await fetch(`http://127.0.0.1:${port}/readyz`)).ok) break; } catch {}
    await delay(250);
  }
  socket = new WebSocketClient(`ws://127.0.0.1:${port}`);
  const pending = new Map();
  socket.addEventListener("message", ({ data }) => {
    const frame = JSON.parse(data);
    frames.push(frame);
    if (frame.type === "res") pending.get(frame.id)?.(frame);
  });
  await new Promise((resolveOpen, reject) => { socket.addEventListener("open", resolveOpen, { once: true }); socket.addEventListener("error", reject, { once: true }); });
  const rpc = (method, params) => new Promise((resolveReply, reject) => {
    const id = randomUUID();
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timed out`)); }, 35000);
    pending.set(id, (frame) => { clearTimeout(timeout); pending.delete(id); if (frame.ok) resolveReply(frame.payload); else reject(new Error(JSON.stringify(frame.error))); });
    socket.send(JSON.stringify({ type: "req", id, method, params }));
  });
  const challengeDeadline = Date.now() + 10000;
  while (!frames.some((frame) => frame.event === "connect.challenge")) {
    if (Date.now() > challengeDeadline) throw new Error('Gateway challenge timed out');
    await delay(10);
  }
  const nonce = frames.find((frame) => frame.event === "connect.challenge").payload.nonce;
  const keys = generateKeyPairSync("ed25519");
  const publicKey = keys.publicKey.export({ type: "spki", format: "der" }).subarray(-32);
  const deviceId = createHash("sha256").update(publicKey).digest("hex");
  const signedAt = Date.now();
  const scopes = ["operator.admin", "operator.read", "operator.write"];
  const signature = sign(null, Buffer.from(["v3", deviceId, "cli", "cli", "operator", scopes.join(","), String(signedAt), token, nonce, "win32", ""].join("|")), keys.privateKey).toString("base64url");
  await rpc("connect", { minProtocol: 3, maxProtocol: 4, client: { id: "cli", version: "probe", platform: "win32", mode: "cli" }, role: "operator", scopes, auth: { token }, caps: [], device: { id: deviceId, publicKey: publicKey.toString("base64url"), signature, signedAt, nonce } });
  if (controlOnly) {
    const sessionKey = `agent:main:control-${randomUUID()}`;
    await rpc("sessions.patch", { key: sessionKey, model: config.agents.defaults.model.primary });
    const sent = await rpc("chat.send", { sessionKey, message: "Normal synthetic control without plugins", idempotencyKey: randomUUID() });
    // A freshly built official host can spend tens of seconds preparing a cold
    // runtime. Wait for a terminal message within a bounded two-minute window.
    let history;
    for (let i = 0; i < 240; i++) {
      history = await rpc("chat.history", { sessionKey, limit: 20 });
      if (history.messages.some((message) => message.role === 'assistant')) break;
      await delay(500);
    }
    await writeFile(join(directory, "evidence.json"), JSON.stringify({ controlOnly, sent, history, frames, modelRequests }, null, 2));
    console.log(JSON.stringify({ controlOnly, modelRequests: modelRequests.length, messages: history.messages.length }));
    assert.ok(modelRequests.length > 0 && history.messages.some((message) => message.role === 'assistant' && message.stopReason !== 'error'), 'Plugins-disabled ordinary chat must actually reach the provider and complete an assistant reply.');
  } else if (rejectedHostProfile) {
    assert.ok(hostProfile, 'Negative profile proof requires the exact verified development checkout.');
    const capabilities = await rpc('expressive-speech.capabilities', {});
    assert.equal(capabilities.available, false);
    assert.equal(capabilities.reason, 'unsupported-host', 'An arbitrary .6 host must not become available without the named profile.');
    await assert.rejects(rpc('expressive-speech.intent', { protocol: { major: 1, minor: 0 }, sessionKey: 'agent:main:unselected', idempotencyKey: randomUUID(), delivery: 'live' }), /plugin-unavailable/);
    assert.equal(modelRequests.length, 0);
    await writeFile(join(directory, 'host-profile-evidence.json'), JSON.stringify({ hostProfile, selectedProfile: null, capabilities, modelRequests: modelRequests.length }, null, 2));
  } else if (production) {
    const disableSessionTts = async (sessionKey) => {
      await rpc('chat.send', { sessionKey, message: '/tts chat off', idempotencyKey: randomUUID() });
      for (let i = 0; i < 240; i++) {
        const readiness = await rpc('expressive-speech.capabilities', { sessionKey });
        if (readiness.available || readiness.reason === 'unsupported-model') return;
        assert.equal(readiness.reason, 'session-core-tts-must-be-off');
        await delay(250);
      }
      throw new Error('The host did not persist the chat-scoped core TTS opt-out.');
    };
    await rpc('expressive-speech-probe.auth', {});
    await rpc('sessions.subscribe', {});
    const capabilities = await rpc('expressive-speech.capabilities', {});
    assert.equal(capabilities.available, true);
    const sessionKey = `agent:main:production-${randomUUID()}`;
    const missingSession = await rpc('expressive-speech.capabilities', { sessionKey });
    assert.equal(missingSession.available, false);
    assert.equal(missingSession.reason, 'invalid-session', 'Missing sessions must not suggest a core TTS command.');
    await rpc('sessions.patch', { key: sessionKey, model: 'expressive-speech-openai/gpt-5.6-sol' });
    const readiness = await rpc('expressive-speech.capabilities', { sessionKey });
    assert.equal(readiness.reason, 'session-core-tts-must-be-off');
    await assert.rejects(rpc('expressive-speech.intent', { protocol: { major: 1, minor: 0 }, sessionKey, idempotencyKey: randomUUID(), delivery: 'live' }), /session-core-tts-must-be-off/);
    await disableSessionTts(sessionKey);
    const idempotencyKey = randomUUID();
    const intent = await rpc('expressive-speech.intent', { protocol: { major: 1, minor: 0 }, sessionKey, idempotencyKey, delivery: 'live', maxSpeechCharacters: 2000 });
    const sent = await rpc('chat.send', { sessionKey, message: 'Production entrypoint fixture check', idempotencyKey, ...(inboundAttachments ? { attachments: inboundAttachments } : {}) });
    const events = [];
    let earlySpeech = false;
    for (let i = 0; i < 150; i++) {
      const read = await rpc('expressive-speech.read', { ticket: intent.ticket, afterSequence: events.at(-1)?.sequence ?? -1, maxEvents: 64 });
      events.push(...read.events);
      if (!modelCompleted && read.events.some((event) => event.kind === 'segment')) earlySpeech = true;
      if (read.state === 'terminal') break;
      await delay(100);
    }
    await delay(300);
    const history = await rpc('chat.history', { sessionKey, limit: 20 });
    const status = await rpc('expressive-speech-probe.status', {});
    await writeFile(join(directory, 'evidence.json'), JSON.stringify({ production, capabilities, sent, events, earlySpeech, history, status, frames, modelRequests, rejectedProviderPaths, rejectedProviderPayloads }, null, 2));
    assert.equal(events.at(-1)?.outcome, 'complete', JSON.stringify(events));
    assert.equal(earlySpeech, true, 'Production speech must arrive before provider completion.');
    assert.equal(history.messages.filter((message) => message.role === 'user' && JSON.stringify(message.content).includes('Production entrypoint fixture check')).length, 1);
    assert.equal(history.messages.filter((message) => message.role === 'assistant' && message.openclawExpressiveSpeech).length, 1);
    const assistant = history.messages.find((message) => message.role === 'assistant' && message.openclawExpressiveSpeech);
    assert.ok(JSON.stringify(modelRequests[0].body).includes('Keep the entire speech block under 1800 characters'), 'The real prompt must include the budgeted composition guide.');
    if (withTool) { assert.equal(modelRequests.length, 2); assert.ok(JSON.stringify(modelRequests[1].body.input).includes('STOCK_TOOL_READ_PROOF'), 'The unmodified stock tool must return its real result to the same agent.'); }
    assert.equal(assistant.openclawExpressiveSpeech.text, fixtureSpeech);
    assert.equal(events.at(-1).messageId, assistant.__openclaw.id);
    assert.equal(rejectedProviderPaths.length, 0, 'OAuth delegation must preserve the canonical ChatGPT Responses endpoint.');
    assert.equal(rejectedProviderPayloads.length, 0, 'OAuth delegation must preserve the native stateless request contract.');
    assert.ok(modelRequests.some((request) => request.path === '/backend-api/codex/responses'), 'The decorated model must reach the actual native Responses path.');
    assert.equal(status.syntheses, 0);
    const textFrames = frames.filter((frame) => frame.type === 'event' && ['chat', 'agent'].includes(frame.event));
    assert.equal(JSON.stringify(textFrames).includes(fixtureSpeech), false, 'Speech must not enter ordinary chat broadcasts.');
    assert.equal(assistant.content.filter((part) => part.type === 'text').map((part) => part.text).join(''), 'Visible. End.');
    const racedSession = `agent:main:model-race-${randomUUID()}`;
    await rpc('sessions.patch', { key: racedSession, model: 'probe/synthetic' });
    await disableSessionTts(racedSession);
    await assert.rejects(rpc('expressive-speech.intent', { protocol: { major: 1, minor: 0 }, sessionKey: racedSession, idempotencyKey: randomUUID(), delivery: 'live' }), /unsupported-model/);
    await rpc('sessions.patch', { key: racedSession, model: 'expressive-speech-openai/gpt-5.6-sol' });
    const racedRun = randomUUID();
    const racedIntent = await rpc('expressive-speech.intent', { protocol: { major: 1, minor: 0 }, sessionKey: racedSession, idempotencyKey: racedRun, delivery: 'live' });
    await rpc('sessions.patch', { key: racedSession, model: 'probe/synthetic' });
    const beforeRaceCalls = modelRequests.length;
    await rpc('chat.send', { sessionKey: racedSession, message: 'This admission changed model before dispatch.', idempotencyKey: racedRun });
    await delay(500);
    const racedRead = await rpc('expressive-speech.read', { ticket: racedIntent.ticket, afterSequence: -1, maxEvents: 64 });
    const racedHistory = await rpc('chat.history', { sessionKey: racedSession, limit: 20 });
    const racedStatus = await rpc('expressive-speech-probe.status', {});
    await writeFile(join(directory, 'admission-evidence.json'), JSON.stringify({ readiness, racedRead, racedHistory, racedStatus, beforeRaceCalls, afterRaceCalls: modelRequests.length }, null, 2));
    assert.equal(racedRead.events.at(-1)?.outcome, 'cancelled');
    assert.equal(racedRead.events.at(-1)?.reason, 'unsupported-model');
    assert.equal(modelRequests.length, beforeRaceCalls, 'Unsupported admitted race must not start a model or summarizer.');
    assert.equal(racedStatus.syntheses, 0, 'Unsupported admitted race must not fall through to core TTS.');
    assert.equal(racedHistory.messages.filter((message) => message.role === 'user' && JSON.stringify(message.content).includes('This admission changed model')).length, 1, 'Cancelled compatibility race must retain its admitted input exactly once.');
    const second = await connectFixtureClient(WebSocketClient, port, token, scopes, keys);
    try { await assert.rejects(second.rpc('expressive-speech.read', { ticket: intent.ticket, afterSequence: -1, maxEvents: 64 }), /intent-unavailable/); }
    finally { second.socket.close(); }
    const reader = await connectFixtureClient(WebSocketClient, port, token, ['operator.read'], keys);
    try {
      const readerCaps = await reader.rpc('expressive-speech.capabilities', {});
      assert.equal(readerCaps.available, false);
      const readerHistory = await reader.rpc('chat.history', { sessionKey, limit: 20 });
      assert.equal(readerHistory.messages.find((message) => message.openclawExpressiveSpeech).openclawExpressiveSpeech.text, fixtureSpeech);
    } finally { reader.socket.close(); }
    const reset = await rpc('sessions.reset', { key: sessionKey });
    const afterResetHistory = await rpc('chat.history', { sessionKey, limit: 20 });
    const sessionList = await rpc('sessions.list', {});
    await writeFile(join(directory, 'reset-evidence.json'), JSON.stringify({ reset, afterResetHistory, sessionList, frames }, null, 2));
    assert.ok(frames.some((frame) => frame.event === 'sessions.changed' && frame.payload.sessionKey === sessionKey && frame.payload.reason === 'reset'), 'Remote reset must have an authoritative subscription event even though physical sessionId is unchanged.');
    await assert.rejects(rpc('expressive-speech.read', { ticket: intent.ticket, afterSequence: -1, maxEvents: 64 }), /session-invalidated/);
    const unrelatedKey = `agent:main:unrelated-tts-${randomUUID()}`;
    await rpc('sessions.patch', { key: unrelatedKey, model: 'probe/synthetic' });
    const beforeUnrelatedCalls = modelRequests.length;
    modelCompleted = false;
    await rpc('chat.send', { sessionKey: unrelatedKey, message: 'Unrelated ordinary core speech control.', idempotencyKey: randomUUID(), ...(inboundAttachments ? { attachments: inboundAttachments } : {}) });
    let unrelatedStatus;
    for (let i = 0; i < 120; i++) {
      unrelatedStatus = await rpc('expressive-speech-probe.status', {});
      if (unrelatedStatus.syntheses > 0 || ttsMode === 'off' && modelCompleted) break;
      await delay(100);
    }
    await writeFile(join(directory, 'core-tts-control-evidence.json'), JSON.stringify({ ttsMode, longSpeech, speechLength: fixtureSpeech.length, beforeUnrelatedCalls, afterUnrelatedCalls: modelRequests.length, unrelatedStatus, modelRequests: modelRequests.slice(beforeUnrelatedCalls) }, null, 2));
    assert.equal(unrelatedStatus.syntheses, ttsMode === 'off' ? 0 : 1, 'Per-session speech opt-out must preserve unrelated ordinary core synthesis policy.');
    if (longSpeech) assert.equal(modelRequests.length - beforeUnrelatedCalls, ttsMode === 'off' ? 1 : 2, 'Long applicable ordinary speech must invoke a real summarization model request while opted speech did not.');
    if (ttsMode === 'inbound') assert.ok(unrelatedStatus.admissionShapes.some((shape) => shape.sessionKey === unrelatedKey && shape.inboundAudio), 'The real host must classify the control as inbound audio.');
    if (ttsMode === 'inbound' || ttsMode === 'tagged') {
      const negativeKey = `agent:main:nonapplicable-tts-${randomUUID()}`;
      await rpc('sessions.patch', { key: negativeKey, model: 'probe/synthetic' });
      const beforeNegativeCalls = modelRequests.length;
      modelCompleted = false;
      await rpc('chat.send', { sessionKey: negativeKey, message: 'UNTAGGED_CONTROL without audio.', idempotencyKey: randomUUID() });
      for (let i = 0; i < 100 && !modelCompleted; i++) await delay(100);
      await delay(300);
      const negativeStatus = await rpc('expressive-speech-probe.status', {});
      assert.equal(negativeStatus.syntheses, 1, 'Non-applicable ordinary turn must not synthesize.');
      assert.equal(modelRequests.length - beforeNegativeCalls, 1, 'Non-applicable ordinary turn must not summarize.');
      await writeFile(join(directory, 'core-tts-negative-evidence.json'), JSON.stringify({ ttsMode, beforeNegativeCalls, afterNegativeCalls: modelRequests.length, negativeStatus }, null, 2));
    }
    console.log(JSON.stringify({ production, earlySpeech, events: events.length, ordinaryHistoryMessages: history.messages.length, synthesisCalls: status.syntheses, modelRequests: modelRequests.length }));
  } else {
  const authDelegation = await rpc("expressive-speech-probe.auth", {});
  const sessionKey = `agent:main:owned-${randomUUID()}`;
  await rpc("sessions.patch", { key: sessionKey, model: "probe/synthetic" });
  const idempotencyKey = randomUUID();
  await rpc("expressive-speech-probe.intent", { sessionKey, idempotencyKey });
  const sent = await rpc("chat.send", { sessionKey, message: "OWNED_SYNTHETIC contract check", idempotencyKey });
  let dispatchStatus;
  for (let i = 0; i < 60; i++) {
    dispatchStatus = await rpc("expressive-speech-probe.status", {});
    if (dispatchStatus.dispatches.at(-1)?.result || dispatchStatus.dispatches.at(-1)?.error) break;
    await delay(250);
  }
  await delay(500);
  const history = await rpc("chat.history", { sessionKey, limit: 20 });
  const controlKey = `agent:main:tts-control-${randomUUID()}`;
  await rpc("sessions.patch", { key: controlKey, model: "probe/synthetic" });
  await rpc("chat.send", { sessionKey: controlKey, message: "Normal synthetic TTS control", idempotencyKey: randomUUID() });
  let controlHistory;
  for (let i = 0; i < 60; i++) {
    controlHistory = await rpc("chat.history", { sessionKey: controlKey, limit: 20 });
    if (controlHistory.messages.some((message) => message.role === "assistant")) break;
    await delay(250);
  }
  let ttsControlStatus;
  for (let i = 0; i < 120; i++) {
    ttsControlStatus = await rpc("expressive-speech-probe.status", {});
    if (ttsControlStatus.syntheses > 0 || ttsControlStatus.lastTtsAttempt) break;
    await delay(250);
  }
  const baseline = await rpc("expressive-speech-probe.run", { suppress: false });
  const suppressed = await rpc("expressive-speech-probe.run", { suppress: true });
  const decorated = await rpc("expressive-speech-probe.run", { decorate: true });
  const baselineRaw = JSON.stringify(baseline.observations.global).includes("Spoken only.");
  const suppressedPartials = suppressed.observations.partials.length;
  const summary = { host: entry, directory, modelRequests: modelRequests.length, baselineRawGlobalEvent: baselineRaw, baselinePartials: baseline.observations.partials.length, suppressedPartials, suppressedRunCompleted: suppressed.result.meta?.executionTrace?.attempts?.[0]?.result === "success", decoratedChunks: decorated.observations.rawChunks.length, decoratedRawGlobalEvent: JSON.stringify(decorated.observations.global).includes("Spoken only."), ordinaryDispatchInvoked: dispatchStatus.dispatches.length > 0, ordinaryHistoryMessages: history.messages.length, synthesisCalls: dispatchStatus.syntheses, featureReady: false };
  await writeFile(join(directory, "evidence.json"), JSON.stringify({ summary, authDelegation, baseline, suppressed, decorated, sent, dispatchStatus, ttsControlStatus, controlHistory, history, frames, modelRequests }, null, 2));
  assert.equal(baselineRaw, true, "The unsuppressed candidate should expose synthetic speech in the real global event bus.");
  assert.ok(baseline.observations.partials.length > 0, "The unsuppressed run should supply incremental callbacks.");
  assert.equal(suppressedPartials, 0, "The suppressed candidate loses the required incremental callbacks on this host.");
  assert.equal(summary.suppressedRunCompleted, true, "Zero partials must not be a failed/empty model run.");
  assert.ok(decorated.observations.rawChunks.join("").includes("Spoken only."), "The public provider wrapper must receive hidden text before the core.");
  assert.equal(summary.decoratedRawGlobalEvent, false, "The decorated transport must not release synthetic speech into the core global bus.");
  console.log(JSON.stringify(summary, null, 2));
  assert.equal(summary.ordinaryDispatchInvoked, true, "W1 is incomplete: ordinary chat did not reach the registered reply dispatcher. Inspect host.log.");
  assert.equal(history.messages.filter((message) => message.role === 'user').length, 1, 'The admitted user turn must be recorded exactly once.');
  assert.equal(history.messages.filter((message) => message.role === 'assistant').length, 1, 'The written assistant reply must be recorded exactly once.');
  assert.equal(dispatchStatus.dispatches[0].intentBound, true, 'Signed device identity must survive admitted dispatch.');
  assert.equal(authDelegation.mode, 'oauth');
  assert.equal(authDelegation.syntheticProfileSelected && authDelegation.dummyCredentialSelected, true);
  assert.equal(dispatchStatus.syntheses, 0, 'Opted-in dispatch must bypass core synthesis.');
  assert.equal(ttsControlStatus.syntheses, 1, 'Unrelated normal chat must still synthesize with the same configuration.');
  const assistant = history.messages.find((message) => message.role === 'assistant');
  const receipt = ttsControlStatus.transcriptUpdates.find((update) => update.runId === sent.runId && update.message?.role === 'assistant');
  assert.equal(receipt?.messageId, assistant?.__openclaw?.id, 'Final identity must come from the actual committed transcript.');
  assert.equal(assistant?.expressiveSpeechProbe?.text, '[curious] Spoken only.', 'Separate rendition metadata must survive core-authorized history.');
  }
} finally {
  socket?.close();
  child.kill();
  await writeFile(join(directory, "host.log"), logs);
  model.closeAllConnections();
  await proxy?.close();
  await new Promise((resolveClose) => model.close(resolveClose));
  console.log(`Synthetic host evidence: ${directory}`);
}

