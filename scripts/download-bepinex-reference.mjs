import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadReferenceLock } from './restore-build-references.mjs';

// Acquisition is separate from the offline, transactional reference restorer.
const root = fileURLToPath(new URL('..', import.meta.url));
const tools = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json'), 'utf8'));
if (process.versions.node !== tools.node) throw new Error('Use the locked Node version.');
if (process.argv.length !== 4 || process.argv[2] !== '--output') {
  throw new Error('Usage: node scripts/download-bepinex-reference.mjs --output <official-zip>');
}
const destination = path.resolve(process.argv[3]);
if (realpathSync(path.dirname(destination)) !== path.dirname(destination)) throw new Error('Download parent must not be a symlink.');
const record = loadReferenceLock().source.bepInEx;
const verify = (bytes) => {
  if (bytes.length !== record.size || createHash('sha256').update(bytes).digest('hex') !== record.sha256) {
    throw new Error('Official BepInEx archive size/SHA-256 mismatch.');
  }
};
if (existsSync(destination)) {
  const stat = lstatSync(destination);
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size !== record.size) throw new Error('Existing archive differs.');
  verify(readFileSync(destination));
} else {
  const response = await fetch(record.url, { redirect: 'error', signal: AbortSignal.timeout(120_000) });
  if (!response.ok || !response.body) throw new Error(`Official archive HTTP ${response.status}.`);
  const chunks = []; let size = 0;
  for await (const chunk of response.body) {
    size += chunk.length;
    if (size > record.size) throw new Error('Official archive exceeds its locked size.');
    chunks.push(chunk);
  }
  const bytes = Buffer.concat(chunks);
  verify(bytes);
  writeFileSync(destination, bytes, { flag: 'wx', mode: 0o644 });
}
console.log(`Official BepInEx archive verified: ${record.sha256}`);
