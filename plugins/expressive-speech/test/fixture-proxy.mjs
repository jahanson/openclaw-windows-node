import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import { createServer as createHttpServer } from 'node:http';
import { createServer as createHttpsServer } from 'node:https';
import { connect } from 'node:net';
import { join } from 'node:path';
import { once } from 'node:events';

/** Isolated test-process transport. No OS trust installation or real credentials. */
export async function startFixtureProxy(directory, modelPort) {
  const key = join(directory, 'fixture-key.pem');
  const certificate = join(directory, 'fixture-cert.pem');
  execFileSync(process.env.OPENCLAW_PROBE_OPENSSL ?? (process.platform === 'win32' ? 'C:\\Program Files\\Git\\usr\\bin\\openssl.exe' : 'openssl'), ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', certificate, '-days', '1', '-subj', '/CN=chatgpt.com', '-addext', 'subjectAltName=DNS:chatgpt.com'], { windowsHide: true, stdio: 'ignore' });
  const sockets = new Set();
  const tls = createHttpsServer({ key: await readFile(key), cert: await readFile(certificate) }, async (req, res) => {
    let body = '';
    for await (const part of req) body += part;
    // Only synthetic fixture traffic is forwarded. Never forward auth headers.
    const response = await fetch(`http://127.0.0.1:${modelPort}${req.url}`, { method: req.method, body, headers: { 'content-type': 'application/json' } });
    res.writeHead(response.status, { 'content-type': 'text/event-stream' });
    for await (const chunk of response.body) res.write(chunk);
    res.end();
  });
  tls.listen(0, '127.0.0.1'); await once(tls, 'listening');
  const proxy = createHttpServer((_req, res) => { res.writeHead(403); res.end(); });
  proxy.on('connect', (req, socket, head) => {
    if (req.url !== 'chatgpt.com:443') { socket.end('HTTP/1.1 403 Forbidden\r\n\r\n'); return; }
    sockets.add(socket); socket.on('close', () => sockets.delete(socket));
    const tunnel = connect(tls.address().port, '127.0.0.1', () => {
      socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
      if (head.length) tunnel.write(head);
      socket.pipe(tunnel).pipe(socket);
    });
    tunnel.on('error', () => socket.destroy()); socket.on('error', () => tunnel.destroy());
    socket.on('close', () => tunnel.destroy());
  });
  proxy.listen(0, '127.0.0.1'); await once(proxy, 'listening');
  return {
    env: { HTTPS_PROXY: `http://127.0.0.1:${proxy.address().port}`, HTTP_PROXY: `http://127.0.0.1:${proxy.address().port}`, NO_PROXY: 'localhost,127.0.0.1', NODE_EXTRA_CA_CERTS: certificate },
    async close() { for (const socket of sockets) socket.destroy(); tls.closeAllConnections(); await Promise.all([new Promise((resolve) => proxy.close(resolve)), new Promise((resolve) => tls.close(resolve))]); },
  };
}
