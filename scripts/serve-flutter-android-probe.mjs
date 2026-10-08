#!/usr/bin/env node
import { createServer } from 'node:http';
import { appendFileSync, mkdirSync, writeFileSync } from 'node:fs';
import { randomBytes } from 'node:crypto';
import path from 'node:path';

const [directory, host, portText] = process.argv.slice(2);
if (!directory || !/^192\.168\.\d{1,3}\.\d{1,3}$/u.test(host ?? '') || !/^\d{4,5}$/u.test(portText ?? '')) {
  throw new Error('Usage: node scripts/serve-flutter-android-probe.mjs <new-evidence-dir> <LAN-IP> <port>');
}
const port = Number(portText);
if (port > 65535) throw new Error('Invalid port');
mkdirSync(directory);
const nonce = randomBytes(16).toString('hex');
const info = { schemaVersion: 1, endpoint: `http://${host}:${port}/probe`, nonce, startedAt: new Date().toISOString() };
const server = createServer((request, response) => {
  const uri = new URL(request.url, `http://${host}:${port}`);
  const authorized = request.headers['x-mystia-steward-companion-token'] === nonce;
  const event = { at: new Date().toISOString(), remoteAddress: request.socket.remoteAddress,
    method: request.method, path: uri.pathname, transport: uri.searchParams.get('transport'), authorized };
  appendFileSync(path.join(directory, 'requests.jsonl'), `${JSON.stringify(event)}\n`, { mode: 0o600 });
  response.setHeader('Cache-Control', 'no-store');
  if (!authorized || request.method !== 'GET') { response.writeHead(403).end(); return; }
  if (uri.pathname === '/redirect') {
    response.writeHead(302, { Location: `http://${host}:${port}/must-not-follow` }).end(); return;
  }
  if (uri.pathname !== '/probe' || !['dart', 'native'].includes(event.transport)) {
    response.writeHead(404).end(); return;
  }
  const body = JSON.stringify({ success: true, nonce, transport: event.transport });
  response.writeHead(200, { 'Content-Type': 'application/json', 'Content-Length': Buffer.byteLength(body) }).end(body);
});
server.listen(port, host, () => {
  writeFileSync(path.join(directory, 'fixture.json'), `${JSON.stringify(info, null, 2)}\n`, { flag: 'wx', mode: 0o600 });
  console.log(JSON.stringify(info));
});
for (const signal of ['SIGINT', 'SIGTERM']) process.on(signal, () => server.close());
