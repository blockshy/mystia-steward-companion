import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  loadReferenceLock,
  parseStrictZip,
  readAndValidateReferenceBundle,
  requiredReferenceNames,
  restoreReferenceBundle,
  validateReferenceDirectory,
  validateReferenceLock,
} from '../../scripts/restore-build-references.mjs';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const lockPath = path.join(repoRoot, 'mods', 'bepinex', 'References', 'references.lock.json');
const sourceScript = readFileSync(
  path.join(repoRoot, 'scripts', 'restore-build-references.mjs'),
  'utf8',
);
const preflightPowerShell = readFileSync(
  path.join(repoRoot, 'mods', 'bepinex', 'tools', 'preflight.ps1'),
  'utf8',
);
const preflightBash = readFileSync(
  path.join(repoRoot, 'mods', 'bepinex', 'tools', 'preflight.sh'),
  'utf8',
);
const productionLock = loadReferenceLock(lockPath);
let crcTable;

assert.deepEqual(
  productionLock.bundle,
  {
    repository: 'blockshy/mystia-steward-build-assets',
    tag: 'bepinex-783-tmi-91ce5ae3-995d1a08-v2',
    asset: 'mystia-steward-build-references.zip',
    size: 2231579,
    sha256: '05bc343aeecc866cbb3b5d2f862bf2f31d815d9218fffcacfa059a2ef9775e28',
  },
);
assert.equal(
  productionLock.source.game.gameAssemblySha256,
  '91ce5ae3dad5da07dfed63bab4c9e454f67b6e50f9a6e8ec498ef9b0b806a789',
);
assert.equal(
  productionLock.source.game.globalMetadataSha256,
  '995d1a08cac7a784d397927cf73ae71a8ce47cc8637cc4dd7ea534a3368b31e7',
);
assert.deepEqual(productionLock.files.map((item) => item.name), requiredReferenceNames);
assert.equal(productionLock.schemaVersion, 2);
assert.equal(productionLock.files.length, 8);
assert.equal(requiredReferenceNames.at(-1), 'MonoMod.RuntimeDetour.dll');
assert.equal(productionLock.source.bepInEx.size, 34335702);
assert.equal(productionLock.source.bepInEx.referencePath, 'BepInEx/core/MonoMod.RuntimeDetour.dll');
assert.match(productionLock.source.bepInEx.url, /^https:\/\/builds\.bepinex\.dev\//u);

assert.match(preflightPowerShell, /restore-build-references\.mjs/u);
assert.match(preflightPowerShell, /--verify/u);
assert.match(preflightPowerShell, /--output/u);
assert.match(preflightBash, /restore-build-references\.mjs/u);
assert.match(preflightBash, /--verify/u);
assert.match(preflightBash, /--output/u);
assert.doesNotMatch(sourceScript, /\bfetch\s*\(/u);
assert.doesNotMatch(sourceScript, /\bgh\b.*release/u);
assert.doesNotMatch(sourceScript, /base64/iu);

const fixtureRoot = mkdtempSync(path.join(os.tmpdir(), 'mystia-build-references-audit-'));
try {
  const contents = new Map(requiredReferenceNames.map((name, index) => [
    name,
    Buffer.from(`locked-reference-${index}-${name}\n`, 'utf8'),
  ]));
  const privateNames = requiredReferenceNames.slice(0, -1);
  const canonicalEntries = privateNames.map((name) => ({
    name,
    content: contents.get(name),
    unixMode: 0o100644,
  }));
  const validArchive = createStoredZip(canonicalEntries);
  const validArchivePath = path.join(fixtureRoot, 'valid.zip');
  writeFileSync(validArchivePath, validArchive);
  const officialEntries = [
    { name: productionLock.source.bepInEx.referencePath, content: contents.get(requiredReferenceNames.at(-1)), unixMode: 0o100644 },
    { name: 'doorstop_config.ini', content: Buffer.from('not-a-reference'), unixMode: 0o100644 },
  ];
  const officialArchive = createStoredZip(officialEntries);
  const officialArchivePath = path.join(fixtureRoot, 'bepinex.zip');
  writeFileSync(officialArchivePath, officialArchive);
  const fixtureLock = createFixtureLock(productionLock, contents, validArchive, officialArchive);
  const privateFiles = fixtureLock.files.slice(0, -1);
  validateReferenceLock(fixtureLock);

  const extracted = readAndValidateReferenceBundle(validArchivePath, fixtureLock, officialArchivePath);
  assert.deepEqual([...extracted.keys()], requiredReferenceNames);
  for (const name of requiredReferenceNames) {
    assert.deepEqual(extracted.get(name), contents.get(name));
  }

  const outputPath = path.join(fixtureRoot, 'output');
  mkdirSync(outputPath);
  writeFileSync(path.join(outputPath, 'analysis-only.dll'), 'preserve-me');
  for (const name of requiredReferenceNames) {
    writeFileSync(path.join(outputPath, name), 'stale');
  }
  restoreReferenceBundle(validArchivePath, outputPath, fixtureLock, officialArchivePath);
  validateReferenceDirectory(outputPath, fixtureLock);
  assert.equal(readFileSync(path.join(outputPath, 'analysis-only.dll'), 'utf8'), 'preserve-me');

  restoreReferenceBundle(validArchivePath, outputPath, fixtureLock, officialArchivePath);
  validateReferenceDirectory(outputPath, fixtureLock);

  const missingPath = path.join(fixtureRoot, 'missing');
  mkdirSync(missingPath);
  assert.throws(
    () => validateReferenceDirectory(missingPath, fixtureLock),
    /is missing/u,
  );

  const hashDriftPath = path.join(fixtureRoot, 'hash-drift');
  mkdirSync(hashDriftPath);
  for (const [name, content] of contents) writeFileSync(path.join(hashDriftPath, name), content);
  const firstName = requiredReferenceNames[0];
  const sameSizeDrift = Buffer.from(contents.get(firstName));
  sameSizeDrift[0] ^= 0xff;
  writeFileSync(path.join(hashDriftPath, firstName), sameSizeDrift);
  assert.throws(
    () => validateReferenceDirectory(hashDriftPath, fixtureLock),
    /SHA-256 mismatch/u,
  );

  const sizeDriftPath = path.join(fixtureRoot, 'size-drift');
  mkdirSync(sizeDriftPath);
  for (const [name, content] of contents) writeFileSync(path.join(sizeDriftPath, name), content);
  writeFileSync(path.join(sizeDriftPath, firstName), Buffer.concat([contents.get(firstName), Buffer.of(0)]));
  assert.throws(
    () => validateReferenceDirectory(sizeDriftPath, fixtureLock),
    /size mismatch/u,
  );

  const symlinkReferencePath = path.join(fixtureRoot, 'symlink-reference');
  mkdirSync(symlinkReferencePath);
  for (const [name, content] of contents) writeFileSync(path.join(symlinkReferencePath, name), content);
  rmSync(path.join(symlinkReferencePath, firstName));
  symlinkSync(path.join(outputPath, firstName), path.join(symlinkReferencePath, firstName));
  assert.throws(
    () => validateReferenceDirectory(symlinkReferencePath, fixtureLock),
    /regular non-symlink file/u,
  );
  assert.throws(
    () => restoreReferenceBundle(validArchivePath, symlinkReferencePath, fixtureLock, officialArchivePath),
    /regular non-symlink file/u,
  );

  const archiveSizeDriftLock = structuredClone(fixtureLock);
  archiveSizeDriftLock.bundle.size += 1;
  assert.throws(
    () => readAndValidateReferenceBundle(validArchivePath, archiveSizeDriftLock, officialArchivePath),
    /archive size mismatch/u,
  );
  const archiveHashDriftLock = structuredClone(fixtureLock);
  archiveHashDriftLock.bundle.sha256 = '0'.repeat(64);
  assert.throws(
    () => readAndValidateReferenceBundle(validArchivePath, archiveHashDriftLock, officialArchivePath),
    /archive SHA-256 mismatch/u,
  );

  const archiveSymlinkPath = path.join(fixtureRoot, 'archive-symlink.zip');
  symlinkSync(validArchivePath, archiveSymlinkPath);
  assert.throws(
    () => readAndValidateReferenceBundle(archiveSymlinkPath, fixtureLock, officialArchivePath),
    /regular non-symlink file/u,
  );

  const missingEntryArchive = createStoredZip(canonicalEntries.slice(0, -1));
  assert.throws(
    () => parseStrictZip(missingEntryArchive, privateFiles),
    /exactly 7 entries/u,
  );
  const extraEntryArchive = createStoredZip([
    ...canonicalEntries,
    { name: 'unexpected.dll', content: Buffer.from('unexpected'), unixMode: 0o100644 },
  ]);
  assert.throws(
    () => parseStrictZip(extraEntryArchive, privateFiles),
    /exactly 7 entries/u,
  );
  const traversalEntries = canonicalEntries.map((entry, index) => (
    index === 0 ? { ...entry, name: '../BepInEx.Core.dll' } : entry
  ));
  assert.throws(
    () => parseStrictZip(createStoredZip(traversalEntries), privateFiles),
    /traversal-free/u,
  );
  const symlinkEntries = canonicalEntries.map((entry, index) => (
    index === 0 ? { ...entry, unixMode: 0o120777 } : entry
  ));
  assert.throws(
    () => parseStrictZip(createStoredZip(symlinkEntries), privateFiles),
    /not a regular file/u,
  );

  const wrongContentEntries = canonicalEntries.map((entry, index) => (
    index === 0 ? { ...entry, content: Buffer.alloc(entry.content.length, 0x5a) } : entry
  ));
  assert.throws(
    () => parseStrictZip(createStoredZip(wrongContentEntries), privateFiles),
    /SHA-256 mismatch/u,
  );

  const pendingOutputPath = path.join(fixtureRoot, 'pending-output');
  mkdirSync(pendingOutputPath);
  mkdirSync(path.join(pendingOutputPath, '.reference-restore-stage-orphan'));
  assert.throws(
    () => restoreReferenceBundle(validArchivePath, pendingOutputPath, fixtureLock, officialArchivePath),
    /previous reference restore transaction is incomplete/u,
  );

  const outputSymlinkPath = path.join(fixtureRoot, 'output-symlink');
  symlinkSync(outputPath, outputSymlinkPath, 'dir');
  assert.throws(
    () => restoreReferenceBundle(validArchivePath, outputSymlinkPath, fixtureLock, officialArchivePath),
    /symlink or junction/u,
  );

  const unknownLockField = structuredClone(fixtureLock);
  unknownLockField.bundle.fallbackAsset = 'legacy.zip';
  assert.throws(
    () => validateReferenceLock(unknownLockField),
    /keys must be exactly/u,
  );
  const duplicateLockFile = structuredClone(fixtureLock);
  duplicateLockFile.files[1].name = duplicateLockFile.files[0].name;
  assert.throws(
    () => validateReferenceLock(duplicateLockFile),
    /must be BepInEx\.Unity\.IL2CPP\.dll/u,
  );

  // All eight references are validated, even though the private archive stays
  // at seven members and the additional DLL has an independent official source.
  const missingDetourPath = path.join(fixtureRoot, 'missing-detour');
  mkdirSync(missingDetourPath);
  for (const name of privateNames) writeFileSync(path.join(missingDetourPath, name), contents.get(name));
  assert.throws(() => validateReferenceDirectory(missingDetourPath, fixtureLock), /MonoMod\.RuntimeDetour\.dll.*is missing/u);
  assert.throws(() => readAndValidateReferenceBundle(validArchivePath, fixtureLock), /archive|path|argument/iu);
  assert.throws(
    () => readAndValidateReferenceBundle(validArchivePath, fixtureLock, path.join(fixtureRoot, 'missing-official.zip')),
    /is missing/u,
  );
  const officialSizeDriftLock = structuredClone(fixtureLock);
  officialSizeDriftLock.source.bepInEx.size += 1;
  assert.throws(
    () => readAndValidateReferenceBundle(validArchivePath, officialSizeDriftLock, officialArchivePath),
    /archive size mismatch/u,
  );
  const officialHashDriftLock = structuredClone(fixtureLock);
  officialHashDriftLock.source.bepInEx.sha256 = '0'.repeat(64);
  assert.throws(
    () => readAndValidateReferenceBundle(validArchivePath, officialHashDriftLock, officialArchivePath),
    /archive SHA-256 mismatch/u,
  );
  const officialSymlinkPath = path.join(fixtureRoot, 'official-symlink.zip');
  symlinkSync(officialArchivePath, officialSymlinkPath);
  assert.throws(
    () => readAndValidateReferenceBundle(validArchivePath, fixtureLock, officialSymlinkPath),
    /regular non-symlink file/u,
  );
  // Failure to validate the second archive must not replace any existing DLL.
  const untouchedOutput = path.join(fixtureRoot, 'untrusted-official-output');
  mkdirSync(untouchedOutput);
  for (const name of requiredReferenceNames) writeFileSync(path.join(untouchedOutput, name), `old-${name}`);
  assert.throws(
    () => restoreReferenceBundle(validArchivePath, untouchedOutput, officialHashDriftLock, officialArchivePath),
    /archive SHA-256 mismatch/u,
  );
  for (const name of requiredReferenceNames) assert.equal(readFileSync(path.join(untouchedOutput, name), 'utf8'), `old-${name}`);

  let invalidOfficialIndex = 0;
  function rejectsOfficial(entries, expectedError) {
    const archive = createStoredZip(entries);
    const candidate = path.join(fixtureRoot, `official-invalid-${invalidOfficialIndex++}.zip`);
    writeFileSync(candidate, archive);
    const candidateLock = createFixtureLock(productionLock, contents, validArchive, archive);
    // Deliberately bind the altered archive identity, so these checks exercise
    // member metadata/content validation after the whole-archive hash succeeds.
    assert.throws(() => readAndValidateReferenceBundle(validArchivePath, candidateLock, candidate), expectedError);
  }
  rejectsOfficial([...officialEntries, officialEntries[0]], /duplicate|exactly one/iu);
  rejectsOfficial(officialEntries.map((entry, index) => index === 0
    ? { ...entry, name: 'Elsewhere/MonoMod.RuntimeDetour.dll' } : entry), /missing|not found|exactly one/iu);
  rejectsOfficial(officialEntries.map((entry, index) => index === 0
    ? { ...entry, unixMode: 0o120777 } : entry), /regular|symlink/iu);
  rejectsOfficial(officialEntries.map((entry, index) => index === 0
    ? { ...entry, content: Buffer.alloc(entry.content.length, 0x5a) } : entry), /SHA-256 mismatch/u);
  rejectsOfficial(officialEntries.map((entry, index) => index === 0
    ? { ...entry, content: Buffer.concat([entry.content, Buffer.of(0)]) } : entry), /reference metadata/u);

  const mismatchArchive = createStoredZip(officialEntries);
  // Change only the local filename, preserving the central name and payload.
  mismatchArchive[30] ^= 1;
  const mismatchPath = path.join(fixtureRoot, 'official-local-mismatch.zip');
  writeFileSync(mismatchPath, mismatchArchive);
  const mismatchLock = createFixtureLock(productionLock, contents, validArchive, mismatchArchive);
  assert.throws(
    () => readAndValidateReferenceBundle(validArchivePath, mismatchLock, mismatchPath),
    /local\/central metadata mismatch/u,
  );

  for (const changedSource of [
    { url: fixtureLock.source.bepInEx.url.replace('builds.bepinex.dev', 'example.com') },
    { url: `${fixtureLock.source.bepInEx.url}?mirror=1` },
    { referencePath: '../MonoMod.RuntimeDetour.dll' },
    { size: 64 * 1024 * 1024 + 1 },
  ]) {
    const invalidCoordinates = structuredClone(fixtureLock);
    Object.assign(invalidCoordinates.source.bepInEx, changedSource);
    assert.throws(() => validateReferenceLock(invalidCoordinates), /coordinates|size limit/u);
  }
} finally {
  rmSync(fixtureRoot, { recursive: true, force: true });
}

console.log('Build reference bundle audit passed.');

function createFixtureLock(baseLock, contents, archive, officialArchive) {
  const lock = structuredClone(baseLock);
  lock.bundle.size = archive.length;
  lock.bundle.sha256 = sha256(archive);
  lock.source.bepInEx.size = officialArchive.length;
  lock.source.bepInEx.sha256 = sha256(officialArchive);
  lock.files = requiredReferenceNames.map((name) => ({
    name,
    size: contents.get(name).length,
    sha256: sha256(contents.get(name)),
  }));
  return lock;
}

function createStoredZip(entries) {
  const localParts = [];
  const centralParts = [];
  let localOffset = 0;

  for (const entry of entries) {
    const name = Buffer.from(entry.name, 'utf8');
    const content = Buffer.from(entry.content);
    const crc = crc32(content);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(0, 6);
    local.writeUInt16LE(0, 8);
    local.writeUInt16LE(0, 10);
    local.writeUInt16LE(0x21, 12);
    local.writeUInt32LE(crc, 14);
    local.writeUInt32LE(content.length, 18);
    local.writeUInt32LE(content.length, 22);
    local.writeUInt16LE(name.length, 26);
    local.writeUInt16LE(0, 28);
    localParts.push(local, name, content);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE((3 << 8) | 30, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(0, 8);
    central.writeUInt16LE(0, 10);
    central.writeUInt16LE(0, 12);
    central.writeUInt16LE(0x21, 14);
    central.writeUInt32LE(crc, 16);
    central.writeUInt32LE(content.length, 20);
    central.writeUInt32LE(content.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt16LE(0, 30);
    central.writeUInt16LE(0, 32);
    central.writeUInt16LE(0, 34);
    central.writeUInt16LE(0, 36);
    central.writeUInt32LE((entry.unixMode ?? 0o100644) * 0x10000, 38);
    central.writeUInt32LE(localOffset, 42);
    centralParts.push(central, name);
    localOffset += local.length + name.length + content.length;
  }

  const centralDirectory = Buffer.concat(centralParts);
  const end = Buffer.alloc(22);
  end.writeUInt32LE(0x06054b50, 0);
  end.writeUInt16LE(0, 4);
  end.writeUInt16LE(0, 6);
  end.writeUInt16LE(entries.length, 8);
  end.writeUInt16LE(entries.length, 10);
  end.writeUInt32LE(centralDirectory.length, 12);
  end.writeUInt32LE(localOffset, 16);
  end.writeUInt16LE(0, 20);
  return Buffer.concat([...localParts, centralDirectory, end]);
}

function sha256(value) {
  return createHash('sha256').update(value).digest('hex');
}

function crc32(buffer) {
  if (!crcTable) {
    crcTable = new Uint32Array(256);
    for (let index = 0; index < 256; index += 1) {
      let value = index;
      for (let bit = 0; bit < 8; bit += 1) {
        value = (value & 1) ? (0xedb88320 ^ (value >>> 1)) : (value >>> 1);
      }
      crcTable[index] = value >>> 0;
    }
  }
  let value = 0xffffffff;
  for (const byte of buffer) value = crcTable[(value ^ byte) & 0xff] ^ (value >>> 8);
  return (value ^ 0xffffffff) >>> 0;
}
