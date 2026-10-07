#!/usr/bin/env node

import { createHash } from 'node:crypto';
import { createReadStream, createWriteStream, mkdirSync, mkdtempSync, rmSync } from 'node:fs';
import https from 'node:https';
import path from 'node:path';
import { Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';
import {
  assertRealDirectory,
  assertLockedNode,
  assertRegularFile,
  checkedPath,
  checkInstalledFlutter,
  flutterPlatformKey,
  readFlutterLock,
  runFlutterTool,
  tryLstat,
  validateFlutterLock,
  windowsSystemCommand,
} from './flutter-toolchain.mjs';

export function assertInstallDestination(input) {
  const destination = checkedPath(input);
  if (destination === path.dirname(destination)) {
    throw new Error('Flutter install root must not be a filesystem root.');
  }
  assertRealDirectory(path.dirname(destination));
  if (tryLstat(destination)) throw new Error(`Flutter install root already exists: ${destination}`);
  return destination;
}

export async function verifyFlutterArchive(archivePath, record) {
  assertRegularFile(archivePath);
  if (tryLstat(archivePath).size !== record.size) {
    throw new Error('Flutter archive size does not match the lock.');
  }
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(archivePath)) hash.update(chunk);
  if (hash.digest('hex') !== record.sha256) {
    throw new Error('Flutter archive SHA-256 does not match the lock.');
  }
}

async function downloadFlutterArchive(record, destination) {
  const response = await new Promise((resolve, reject) => {
    const request = https.get(record.url, {
      headers: { 'User-Agent': 'mystia-steward-companion-locked-flutter' },
    }, (result) => {
      if (result.statusCode !== 200) {
        result.resume();
        reject(new Error(`Flutter archive returned HTTP ${result.statusCode}; redirects are not accepted.`));
        return;
      }
      const length = result.headers['content-length'];
      if (length !== undefined && (!/^\d+$/u.test(length) || Number(length) !== record.size)) {
        result.destroy();
        reject(new Error('Flutter archive Content-Length does not match the lock.'));
        return;
      }
      resolve(result);
    });
    request.setTimeout(120_000, () => request.destroy(new Error('Flutter archive download timed out.')));
    request.on('error', reject);
  });
  let received = 0;
  const sizeLimit = new Transform({
    transform(chunk, _encoding, callback) {
      received += chunk.length;
      callback(received > record.size ? new Error('Flutter archive exceeded its locked size.') : null, chunk);
    },
  });
  await pipeline(response, sizeLimit, createWriteStream(destination, { flags: 'wx', mode: 0o600 }));
}

export function validateFlutterArchiveListing(names, details) {
  const splitLines = (listing) => {
    // tar.exe writes CRLF. Remove only line separators, never path whitespace
    // or a bare CR, so embedded/trailing control characters remain rejectable.
    const lines = listing.split(/\r?\n/u);
    if (lines.at(-1) === '') lines.pop();
    return lines;
  };
  const entries = splitLines(names);
  const metadata = splitLines(details);
  if (entries.length !== metadata.length || !entries.length) {
    throw new Error('Flutter archive listing is incomplete.');
  }
  const members = new Map();
  for (const [index, entry] of entries.entries()) {
    const type = metadata[index][0];
    if (!entry.startsWith('flutter/') || /[\x00-\x1f\x7f\\:]/u.test(entry)
      || entry.split('/').some((part) => part === '..' || part === '.')
      || !['-', 'd', 'l'].includes(type)) {
      throw new Error(`Flutter archive contains an unsafe path, link or special file: ${JSON.stringify(entry)} (type ${JSON.stringify(type)}, metadata ${JSON.stringify(metadata[index].slice(0, 240))})`);
    }
    const name = entry.replace(/\/$/u, '');
    if (members.has(name) || name.includes('//')) {
      throw new Error(`Flutter archive contains a duplicate or ambiguous path: ${JSON.stringify(entry)}`);
    }
    let target;
    if (type === 'l') {
      const marker = `${entry} -> `;
      const markerIndex = metadata[index].indexOf(marker);
      if (markerIndex < 0 || markerIndex !== metadata[index].lastIndexOf(marker)) {
        throw new Error(`Flutter archive contains an unreadable symbolic link: ${JSON.stringify(entry)}`);
      }
      const relative = metadata[index].slice(markerIndex + marker.length);
      if (!relative || relative.startsWith('/') || /[\x00-\x1f\x7f\\:]/u.test(relative)
        || relative.split('/').some((part) => part === '' || part === '.')) {
        throw new Error(`Flutter archive contains an unsafe symbolic link target: ${JSON.stringify(entry)}`);
      }
      target = path.posix.normalize(path.posix.join(path.posix.dirname(name), relative));
      if (!target.startsWith('flutter/')) {
        throw new Error(`Flutter archive symbolic link escapes its SDK root: ${JSON.stringify(entry)}`);
      }
    }
    members.set(name, { type, target, children: [] });
  }
  // No member may be extracted through a link or file ancestor. Validate after
  // collecting all entries so archive ordering cannot bypass this check.
  const requireDirectoryAncestors = (name) => {
    for (let parent = path.posix.dirname(name); parent !== '.'; parent = path.posix.dirname(parent)) {
      if (members.get(parent)?.type !== 'd') {
        throw new Error(`Flutter archive path traverses a missing, linked or non-directory ancestor: ${JSON.stringify(name)}`);
      }
    }
  };
  for (const [name, member] of members) {
    requireDirectoryAncestors(name);
    if (name !== 'flutter') members.get(path.posix.dirname(name)).children.push(name);
    if (member.target) {
      if (!members.has(member.target)) {
        throw new Error(`Flutter archive symbolic link target is missing: ${JSON.stringify(name)}`);
      }
      requireDirectoryAncestors(member.target);
    }
  }
  // Include directory children in the graph: links between two sibling
  // directories can form a cycle even when neither link points to another link.
  const visiting = new Set();
  const complete = new Set();
  const visit = (name) => {
    if (complete.has(name)) return;
    if (visiting.has(name)) throw new Error(`Flutter archive contains a symbolic link cycle: ${JSON.stringify(name)}`);
    visiting.add(name);
    const member = members.get(name);
    for (const child of member.children) visit(child);
    if (member.target) visit(member.target);
    visiting.delete(name);
    complete.add(name);
  };
  for (const name of members.keys()) visit(name);
}

function extractFlutterArchive(archive, destination) {
  const tar = process.platform === 'win32' ? windowsSystemCommand('tar.exe') : '/usr/bin/tar';
  assertRegularFile(tar, { executable: true });
  const options = { timeout: 600_000 };
  const names = runFlutterTool(tar, ['-tf', archive], options);
  const details = runFlutterTool(tar, ['-tvf', archive], options);
  validateFlutterArchiveListing(names, details);
  runFlutterTool(tar, ['-xf', archive, '-C', destination, '--strip-components=1'], options);
}

export async function installLockedFlutter(input, {
  lock = readFlutterLock(),
  archive,
  download = downloadFlutterArchive,
} = {}) {
  validateFlutterLock(lock);
  const record = lock.archives[flutterPlatformKey()];
  const destination = assertInstallDestination(input);
  const temporaryRoot = mkdtempSync(path.join(path.dirname(destination), '.flutter-download-'));
  const archivePath = archive === undefined
    ? path.join(temporaryRoot, process.platform === 'win32' ? 'flutter.zip' : 'flutter.tar.xz')
    : checkedPath(archive);
  let created = false;
  let installed = false;
  try {
    // mkdir is exclusive: even a competing empty directory must never be overwritten.
    mkdirSync(destination, { mode: 0o700 });
    created = true;
    if (archive === undefined) await download(record, archivePath);
    await verifyFlutterArchive(archivePath, record);
    extractFlutterArchive(archivePath, destination);
    checkInstalledFlutter(destination, lock);
    installed = true;
    return destination;
  } finally {
    try {
      rmSync(temporaryRoot, { recursive: true, force: true });
    } finally {
      if (created && !installed) rmSync(destination, { recursive: true, force: true });
    }
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    assertLockedNode();
    const args = process.argv.slice(2);
    if ((args.length !== 2 && args.length !== 4) || args[0] !== '--install-root'
      || (args.length === 4 && args[2] !== '--archive')) {
      throw new Error('Usage: node scripts/install-locked-flutter.mjs --install-root <new-directory> [--archive <verified-release-archive>]');
    }
    const lock = readFlutterLock();
    const destination = await installLockedFlutter(args[1], { lock, archive: args[3] });
    console.log(`Installed verified Flutter ${lock.version} / Dart ${lock.dartVersion} at ${destination}.`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
