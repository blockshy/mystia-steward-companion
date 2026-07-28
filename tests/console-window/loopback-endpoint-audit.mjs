import assert from 'node:assert/strict';
import { isLoopbackLocalApiEndpoint } from '../../apps/companion/src/companion/local-api-endpoint.ts';

for (const endpoint of [
  'http://127.0.0.1:32145',
  'http://localhost:32145',
  'http://LOCALHOST:32145',
]) {
  assert.equal(isLoopbackLocalApiEndpoint(endpoint), true, `${endpoint} 应允许控制本机控制台。`);
}

for (const endpoint of [
  'http://127.0.0.2:32145',
  'http://192.168.1.20:32145',
  'http://localhost.example.com:32145',
  'https://127.0.0.1:32145',
  'http://[::1]:32145',
  'not-an-endpoint',
  '',
]) {
  assert.equal(isLoopbackLocalApiEndpoint(endpoint), false, `${endpoint} 不应允许控制本机控制台。`);
}

console.log('PASS: BepInEx console writes are enabled only for canonical loopback HTTP endpoints.');
