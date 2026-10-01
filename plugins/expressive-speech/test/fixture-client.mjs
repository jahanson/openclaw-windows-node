import { randomUUID, generateKeyPairSync, createHash, sign } from 'node:crypto';
import { setTimeout as delay } from 'node:timers/promises';

export async function connectFixtureClient(WebSocketClient, port, token, scopes = ['operator.admin', 'operator.read', 'operator.write'], keys = generateKeyPairSync('ed25519')) {
  const frames = [];
  const pending = new Map();
  const socket = new WebSocketClient(`ws://127.0.0.1:${port}`);
  socket.addEventListener('message', ({ data }) => { const frame = JSON.parse(data); frames.push(frame); if (frame.type === 'res') pending.get(frame.id)?.(frame); });
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', reject, { once: true }); });
  const rpc = (method, params) => new Promise((resolve, reject) => {
    const id = randomUUID();
    const timeout = setTimeout(() => { pending.delete(id); reject(new Error(`${method} timed out`)); }, 35000);
    pending.set(id, (frame) => { clearTimeout(timeout); pending.delete(id); frame.ok ? resolve(frame.payload) : reject(new Error(JSON.stringify(frame.error))); });
    socket.send(JSON.stringify({ type: 'req', id, method, params }));
  });
  const deadline = Date.now() + 10000;
  while (!frames.some((frame) => frame.event === 'connect.challenge')) { if (Date.now() > deadline) throw new Error('Challenge timed out'); await delay(10); }
  const nonce = frames.find((frame) => frame.event === 'connect.challenge').payload.nonce;
  const publicKey = keys.publicKey.export({ type: 'spki', format: 'der' }).subarray(-32);
  const deviceId = createHash('sha256').update(publicKey).digest('hex');
  const signedAt = Date.now();
  const signature = sign(null, Buffer.from(['v3', deviceId, 'cli', 'cli', 'operator', scopes.join(','), String(signedAt), token, nonce, 'win32', ''].join('|')), keys.privateKey).toString('base64url');
  await rpc('connect', { minProtocol: 3, maxProtocol: 4, client: { id: 'cli', version: 'probe', platform: 'win32', mode: 'cli' }, role: 'operator', scopes, auth: { token }, caps: [], device: { id: deviceId, publicKey: publicKey.toString('base64url'), signature, signedAt, nonce } });
  return { rpc, socket, frames, keys };
}
