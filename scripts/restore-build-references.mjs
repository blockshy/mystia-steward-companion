import { createHash } from 'node:crypto';
import {
  existsSync,
  lstatSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateRawSync } from 'node:zlib';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const defaultLockPath = path.join(repoRoot, 'mods', 'bepinex', 'References', 'references.lock.json');
const defaultOutputPath = path.join(repoRoot, 'mods', 'bepinex', 'References');
const requiredReferenceNames = Object.freeze([
  'BepInEx.Core.dll',
  'BepInEx.Unity.IL2CPP.dll',
  '0Harmony.dll',
  'Il2CppInterop.Runtime.dll',
  'Il2Cppmscorlib.dll',
  'UnityEngine.CoreModule.dll',
  'UnityEngine.InputLegacyModule.dll',
]);
const restoreStagePrefix = '.reference-restore-stage-';
const maxLockBytes = 64 * 1024;
const maxArchiveBytes = 64 * 1024 * 1024;
const zipLocalHeaderSignature = 0x04034b50;
const zipCentralHeaderSignature = 0x02014b50;
const zipEndSignature = 0x06054b50;
const zipEndLength = 22;
const allowedZipFlags = 0x0800;

function main() {
  const options = parseArguments(process.argv.slice(2));
  const lock = loadReferenceLock(options.lockPath);

  if (options.verifyOnly) {
    validateReferenceDirectory(options.outputPath, lock);
    console.log(`Build references match references.lock.json: ${path.resolve(options.outputPath)}`);
    return;
  }

  restoreReferenceBundle(options.archivePath, options.outputPath, lock);
  console.log(`Build references restored and verified: ${path.resolve(options.outputPath)}`);
}

function parseArguments(args) {
  let archivePath = '';
  let outputPath = defaultOutputPath;
  let lockPath = defaultLockPath;
  let verifyOnly = false;

  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    switch (argument) {
      case '--archive':
        archivePath = readArgumentValue(args, ++index, argument);
        break;
      case '--output':
        outputPath = readArgumentValue(args, ++index, argument);
        break;
      case '--lock':
        lockPath = readArgumentValue(args, ++index, argument);
        break;
      case '--verify':
        verifyOnly = true;
        break;
      default:
        throw new Error(
          `Unknown argument: ${argument}\n${usage()}`,
        );
    }
  }

  if (verifyOnly === Boolean(archivePath)) {
    throw new Error(`Choose exactly one operation: --verify or --archive <zip>.\n${usage()}`);
  }

  return { archivePath, outputPath, lockPath, verifyOnly };
}

function readArgumentValue(args, index, option) {
  const value = args[index];
  if (value === undefined || value.length === 0 || value.startsWith('--')) {
    throw new Error(`Missing value for ${option}.\n${usage()}`);
  }
  return value;
}

function usage() {
  return [
    'Usage:',
    '  node scripts/restore-build-references.mjs --verify [--output <directory>]',
    '  node scripts/restore-build-references.mjs --archive <downloaded-zip> --output <directory>',
  ].join('\n');
}

function loadReferenceLock(lockPath = defaultLockPath) {
  const resolvedLockPath = path.resolve(lockPath);
  const lockStats = assertRegularFile(resolvedLockPath, 'Reference lock');
  if (lockStats.size <= 0 || lockStats.size > maxLockBytes) {
    throw new Error(`Reference lock size is outside 1..${maxLockBytes} bytes: ${resolvedLockPath}`);
  }

  let lock;
  try {
    lock = JSON.parse(readFileSync(resolvedLockPath, 'utf8'));
  } catch (error) {
    throw new Error(`Reference lock is not valid UTF-8 JSON: ${resolvedLockPath}`, { cause: error });
  }

  validateReferenceLock(lock);
  return lock;
}

function validateReferenceLock(lock) {
  assertPlainObject(lock, 'Reference lock');
  assertExactKeys(lock, ['schemaVersion', 'source', 'bundle', 'files'], 'Reference lock');
  if (lock.schemaVersion !== 1) {
    throw new Error(`Unsupported reference lock schemaVersion: ${describe(lock.schemaVersion)}`);
  }

  assertPlainObject(lock.source, 'Reference lock source');
  assertExactKeys(lock.source, ['bepInEx', 'game'], 'Reference lock source');
  assertPlainObject(lock.source.bepInEx, 'Reference lock BepInEx source');
  assertExactKeys(
    lock.source.bepInEx,
    ['build', 'version', 'asset', 'sha256'],
    'Reference lock BepInEx source',
  );
  assertPositiveInteger(lock.source.bepInEx.build, 'BepInEx build');
  assertNonEmptyString(lock.source.bepInEx.version, 'BepInEx version');
  assertFlatFileName(lock.source.bepInEx.asset, 'BepInEx asset');
  assertSha256(lock.source.bepInEx.sha256, 'BepInEx asset SHA-256');

  assertPlainObject(lock.source.game, 'Reference lock game source');
  assertExactKeys(
    lock.source.game,
    ['steamAppId', 'steamBuildId', 'gameAssemblySha256', 'globalMetadataSha256'],
    'Reference lock game source',
  );
  assertDecimalString(lock.source.game.steamAppId, 'Steam app ID');
  assertDecimalString(lock.source.game.steamBuildId, 'Steam build ID');
  assertSha256(lock.source.game.gameAssemblySha256, 'GameAssembly SHA-256');
  assertSha256(lock.source.game.globalMetadataSha256, 'global-metadata SHA-256');

  assertPlainObject(lock.bundle, 'Reference bundle');
  assertExactKeys(
    lock.bundle,
    ['repository', 'tag', 'asset', 'size', 'sha256'],
    'Reference bundle',
  );
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u.test(lock.bundle.repository ?? '')) {
    throw new Error(`Reference bundle repository is invalid: ${describe(lock.bundle.repository)}`);
  }
  if (!/^[A-Za-z0-9_.-]+$/u.test(lock.bundle.tag ?? '')) {
    throw new Error(`Reference bundle tag is invalid: ${describe(lock.bundle.tag)}`);
  }
  assertFlatFileName(lock.bundle.asset, 'Reference bundle asset');
  if (!lock.bundle.asset.endsWith('.zip')) {
    throw new Error(`Reference bundle asset must be a .zip file: ${lock.bundle.asset}`);
  }
  assertPositiveInteger(lock.bundle.size, 'Reference bundle size');
  if (lock.bundle.size > maxArchiveBytes) {
    throw new Error(`Reference bundle size exceeds ${maxArchiveBytes} bytes: ${lock.bundle.size}`);
  }
  assertSha256(lock.bundle.sha256, 'Reference bundle SHA-256');

  if (!Array.isArray(lock.files) || lock.files.length !== requiredReferenceNames.length) {
    throw new Error(
      `Reference lock must contain exactly ${requiredReferenceNames.length} files in canonical order.`,
    );
  }

  const seenNames = new Set();
  for (let index = 0; index < lock.files.length; index += 1) {
    const file = lock.files[index];
    const label = `Reference lock file ${index}`;
    assertPlainObject(file, label);
    assertExactKeys(file, ['name', 'size', 'sha256'], label);
    assertFlatFileName(file.name, `${label} name`);
    if (file.name !== requiredReferenceNames[index]) {
      throw new Error(
        `${label} must be ${requiredReferenceNames[index]}; received ${describe(file.name)}.`,
      );
    }
    if (seenNames.has(file.name)) {
      throw new Error(`Reference lock contains duplicate file: ${file.name}`);
    }
    seenNames.add(file.name);
    assertPositiveInteger(file.size, `${file.name} size`);
    assertSha256(file.sha256, `${file.name} SHA-256`);
  }
}

function validateReferenceDirectory(referenceDirectory, lock) {
  const resolvedDirectory = assertRealDirectory(referenceDirectory, 'Reference directory');
  for (const expected of lock.files) {
    const referencePath = path.join(resolvedDirectory, expected.name);
    const stats = assertRegularFile(referencePath, `Build reference ${expected.name}`);
    if (stats.size !== expected.size) {
      throw new Error(
        `Build reference size mismatch for ${expected.name}: expected=${expected.size} actual=${stats.size}`,
      );
    }
    const actualSha256 = sha256(readFileSync(referencePath));
    if (actualSha256 !== expected.sha256) {
      throw new Error(
        `Build reference SHA-256 mismatch for ${expected.name}: expected=${expected.sha256} actual=${actualSha256}`,
      );
    }
  }
  return resolvedDirectory;
}

function readAndValidateReferenceBundle(archivePath, lock) {
  const resolvedArchivePath = path.resolve(archivePath);
  const archiveStats = assertRegularFile(resolvedArchivePath, 'Reference bundle archive');
  if (archiveStats.size !== lock.bundle.size) {
    throw new Error(
      `Reference bundle archive size mismatch: expected=${lock.bundle.size} actual=${archiveStats.size}`,
    );
  }
  if (archiveStats.size > maxArchiveBytes) {
    throw new Error(`Reference bundle archive exceeds ${maxArchiveBytes} bytes.`);
  }

  const archive = readFileSync(resolvedArchivePath);
  const actualArchiveSha256 = sha256(archive);
  if (actualArchiveSha256 !== lock.bundle.sha256) {
    throw new Error(
      `Reference bundle archive SHA-256 mismatch: expected=${lock.bundle.sha256} actual=${actualArchiveSha256}`,
    );
  }

  return parseStrictZip(archive, lock.files);
}

function parseStrictZip(archive, expectedFiles) {
  if (archive.length < zipEndLength) {
    throw new Error('Reference bundle ZIP is truncated before the end record.');
  }

  const endOffset = archive.length - zipEndLength;
  if (archive.readUInt32LE(endOffset) !== zipEndSignature) {
    throw new Error('Reference bundle ZIP must have one comment-free end record at EOF.');
  }

  const diskNumber = archive.readUInt16LE(endOffset + 4);
  const centralDisk = archive.readUInt16LE(endOffset + 6);
  const diskEntryCount = archive.readUInt16LE(endOffset + 8);
  const entryCount = archive.readUInt16LE(endOffset + 10);
  const centralSize = archive.readUInt32LE(endOffset + 12);
  const centralOffset = archive.readUInt32LE(endOffset + 16);
  const commentLength = archive.readUInt16LE(endOffset + 20);
  if (diskNumber !== 0 || centralDisk !== 0 || diskEntryCount !== entryCount) {
    throw new Error('Reference bundle ZIP must be a single-disk archive.');
  }
  if (entryCount === 0xffff || centralSize === 0xffffffff || centralOffset === 0xffffffff) {
    throw new Error('Reference bundle ZIP64 archives are not supported.');
  }
  if (commentLength !== 0 || centralOffset + centralSize !== endOffset) {
    throw new Error('Reference bundle ZIP has a comment, trailing data, or inconsistent central directory.');
  }
  if (entryCount !== expectedFiles.length) {
    throw new Error(
      `Reference bundle ZIP must contain exactly ${expectedFiles.length} entries; received ${entryCount}.`,
    );
  }

  const entries = [];
  const names = new Set();
  let centralCursor = centralOffset;
  for (let index = 0; index < entryCount; index += 1) {
    assertBufferRange(archive, centralCursor, 46, 'ZIP central header');
    if (archive.readUInt32LE(centralCursor) !== zipCentralHeaderSignature) {
      throw new Error(`Reference bundle ZIP central header ${index} is invalid.`);
    }

    const versionMadeBy = archive.readUInt16LE(centralCursor + 4);
    const flags = archive.readUInt16LE(centralCursor + 8);
    const method = archive.readUInt16LE(centralCursor + 10);
    const crc = archive.readUInt32LE(centralCursor + 16);
    const compressedSize = archive.readUInt32LE(centralCursor + 20);
    const uncompressedSize = archive.readUInt32LE(centralCursor + 24);
    const nameLength = archive.readUInt16LE(centralCursor + 28);
    const extraLength = archive.readUInt16LE(centralCursor + 30);
    const entryCommentLength = archive.readUInt16LE(centralCursor + 32);
    const entryDisk = archive.readUInt16LE(centralCursor + 34);
    const externalAttributes = archive.readUInt32LE(centralCursor + 38);
    const localOffset = archive.readUInt32LE(centralCursor + 42);
    const variableLength = nameLength + extraLength + entryCommentLength;
    assertBufferRange(archive, centralCursor + 46, variableLength, 'ZIP central variable data');

    const nameBytes = archive.subarray(centralCursor + 46, centralCursor + 46 + nameLength);
    const name = decodeFlatZipName(nameBytes, index);
    if (names.has(name)) {
      throw new Error(`Reference bundle ZIP contains duplicate entry: ${name}`);
    }
    names.add(name);
    if (name !== expectedFiles[index].name) {
      throw new Error(
        `Reference bundle ZIP entry ${index} must be ${expectedFiles[index].name}; received ${name}.`,
      );
    }
    if (flags & ~allowedZipFlags) {
      throw new Error(`Reference bundle ZIP entry ${name} uses unsupported or unsafe flags: 0x${flags.toString(16)}`);
    }
    if (method !== 0 && method !== 8) {
      throw new Error(`Reference bundle ZIP entry ${name} uses unsupported compression method ${method}.`);
    }
    if (compressedSize === 0xffffffff || uncompressedSize === 0xffffffff || localOffset === 0xffffffff) {
      throw new Error(`Reference bundle ZIP entry ${name} requires ZIP64.`);
    }
    if (uncompressedSize !== expectedFiles[index].size) {
      throw new Error(
        `Reference bundle ZIP entry size mismatch for ${name}: expected=${expectedFiles[index].size} actual=${uncompressedSize}`,
      );
    }
    if (extraLength !== 0 || entryCommentLength !== 0 || entryDisk !== 0) {
      throw new Error(`Reference bundle ZIP entry ${name} must not contain extra data, comments, or split-disk state.`);
    }
    assertNotZipSymlink(versionMadeBy, externalAttributes, name);

    entries.push({
      name,
      flags,
      method,
      crc,
      compressedSize,
      uncompressedSize,
      localOffset,
      expected: expectedFiles[index],
      nameBytes,
    });
    centralCursor += 46 + variableLength;
  }
  if (centralCursor !== centralOffset + centralSize) {
    throw new Error('Reference bundle ZIP central directory size is inconsistent.');
  }

  let localCursor = 0;
  const extracted = new Map();
  for (const entry of entries) {
    if (entry.localOffset !== localCursor) {
      throw new Error(`Reference bundle ZIP has hidden, overlapping, or out-of-order local data before ${entry.name}.`);
    }
    assertBufferRange(archive, localCursor, 30, `ZIP local header ${entry.name}`);
    if (archive.readUInt32LE(localCursor) !== zipLocalHeaderSignature) {
      throw new Error(`Reference bundle ZIP local header is invalid for ${entry.name}.`);
    }

    const localFlags = archive.readUInt16LE(localCursor + 6);
    const localMethod = archive.readUInt16LE(localCursor + 8);
    const localCrc = archive.readUInt32LE(localCursor + 14);
    const localCompressedSize = archive.readUInt32LE(localCursor + 18);
    const localUncompressedSize = archive.readUInt32LE(localCursor + 22);
    const localNameLength = archive.readUInt16LE(localCursor + 26);
    const localExtraLength = archive.readUInt16LE(localCursor + 28);
    assertBufferRange(
      archive,
      localCursor + 30,
      localNameLength + localExtraLength,
      `ZIP local variable data ${entry.name}`,
    );
    const localNameBytes = archive.subarray(localCursor + 30, localCursor + 30 + localNameLength);
    if (
      localFlags !== entry.flags
      || localMethod !== entry.method
      || localCrc !== entry.crc
      || localCompressedSize !== entry.compressedSize
      || localUncompressedSize !== entry.uncompressedSize
      || localExtraLength !== 0
      || !localNameBytes.equals(entry.nameBytes)
    ) {
      throw new Error(`Reference bundle ZIP local and central metadata disagree for ${entry.name}.`);
    }

    const dataStart = localCursor + 30 + localNameLength;
    const dataEnd = dataStart + entry.compressedSize;
    if (dataEnd > centralOffset) {
      throw new Error(`Reference bundle ZIP compressed data overlaps the central directory for ${entry.name}.`);
    }
    const compressed = archive.subarray(dataStart, dataEnd);
    let content;
    try {
      content = entry.method === 0
        ? Buffer.from(compressed)
        : inflateRawSync(compressed, { maxOutputLength: entry.expected.size + 1 });
    } catch (error) {
      throw new Error(`Reference bundle ZIP decompression failed for ${entry.name}.`, { cause: error });
    }
    if (content.length !== entry.expected.size) {
      throw new Error(
        `Reference bundle content size mismatch for ${entry.name}: expected=${entry.expected.size} actual=${content.length}`,
      );
    }
    if (crc32(content) !== entry.crc) {
      throw new Error(`Reference bundle CRC-32 mismatch for ${entry.name}.`);
    }
    const actualSha256 = sha256(content);
    if (actualSha256 !== entry.expected.sha256) {
      throw new Error(
        `Reference bundle SHA-256 mismatch for ${entry.name}: expected=${entry.expected.sha256} actual=${actualSha256}`,
      );
    }
    extracted.set(entry.name, content);
    localCursor = dataEnd;
  }
  if (localCursor !== centralOffset || extracted.size !== expectedFiles.length) {
    throw new Error('Reference bundle ZIP contains unaccounted local data or missing entries.');
  }

  return extracted;
}

function restoreReferenceBundle(archivePath, outputDirectory, lock) {
  const extracted = readAndValidateReferenceBundle(archivePath, lock);
  const resolvedOutput = assertRealDirectory(outputDirectory, 'Reference output directory');
  assertNoPendingRestoreStages(resolvedOutput);

  let alreadyValid = true;
  try {
    validateReferenceDirectory(resolvedOutput, lock);
  } catch {
    alreadyValid = false;
  }
  if (alreadyValid) {
    return;
  }

  for (const expected of lock.files) {
    const targetPath = path.join(resolvedOutput, expected.name);
    if (!existsSync(targetPath)) continue;
    assertRegularFile(targetPath, `Existing build reference ${expected.name}`);
  }

  const stageDirectory = mkdtempSync(path.join(resolvedOutput, restoreStagePrefix));
  const installedPaths = [];
  const backups = [];
  let preserveStage = false;
  try {
    for (const expected of lock.files) {
      const stagedPath = path.join(stageDirectory, expected.name);
      writeFileSync(stagedPath, extracted.get(expected.name), { flag: 'wx', mode: 0o644 });
    }
    validateReferenceDirectory(stageDirectory, lock);

    for (let index = 0; index < lock.files.length; index += 1) {
      const expected = lock.files[index];
      const targetPath = path.join(resolvedOutput, expected.name);
      if (existsSync(targetPath)) {
        const backupPath = path.join(stageDirectory, `.backup-${index}`);
        renameSync(targetPath, backupPath);
        backups.push({ targetPath, backupPath });
      }
      renameSync(path.join(stageDirectory, expected.name), targetPath);
      installedPaths.push(targetPath);
    }

    validateReferenceDirectory(resolvedOutput, lock);
  } catch (error) {
    const rollbackErrors = [];
    for (const installedPath of installedPaths.reverse()) {
      try {
        rmSync(installedPath, { force: true });
      } catch (rollbackError) {
        rollbackErrors.push(rollbackError);
      }
    }
    for (const backup of backups.reverse()) {
      try {
        if (existsSync(backup.backupPath)) {
          renameSync(backup.backupPath, backup.targetPath);
        }
      } catch (rollbackError) {
        rollbackErrors.push(rollbackError);
      }
    }
    if (rollbackErrors.length > 0) {
      preserveStage = true;
      throw new AggregateError(
        [error, ...rollbackErrors],
        `Reference restore failed and rollback is incomplete. Inspect: ${stageDirectory}`,
      );
    }
    throw error;
  } finally {
    if (!preserveStage) {
      rmSync(stageDirectory, { recursive: true, force: true });
    }
  }
}

function assertNoPendingRestoreStages(outputDirectory) {
  const pending = readdirSync(outputDirectory, { withFileTypes: true })
    .filter((entry) => entry.name.startsWith(restoreStagePrefix))
    .map((entry) => path.join(outputDirectory, entry.name));
  if (pending.length > 0) {
    throw new Error(
      `A previous reference restore transaction is incomplete. Inspect before retrying:\n${pending.map((item) => `  - ${item}`).join('\n')}`,
    );
  }
}

function assertRealDirectory(directoryPath, label) {
  const resolvedPath = path.resolve(directoryPath);
  assertNoSymlinkPathComponents(resolvedPath, label);
  const stats = lstatSync(resolvedPath);
  if (!stats.isDirectory() || stats.isSymbolicLink()) {
    throw new Error(`${label} must be a real directory: ${resolvedPath}`);
  }
  return resolvedPath;
}

function assertNoSymlinkPathComponents(resolvedPath, label) {
  const parsed = path.parse(resolvedPath);
  const relativeParts = resolvedPath.slice(parsed.root.length).split(path.sep).filter(Boolean);
  let current = parsed.root;
  for (const part of relativeParts) {
    current = path.join(current, part);
    if (!existsSync(current)) {
      throw new Error(`${label} path does not exist: ${current}`);
    }
    if (lstatSync(current).isSymbolicLink()) {
      throw new Error(`${label} path must not contain a symlink or junction: ${current}`);
    }
  }
}

function assertRegularFile(filePath, label) {
  let stats;
  try {
    stats = lstatSync(filePath);
  } catch (error) {
    throw new Error(`${label} is missing: ${filePath}`, { cause: error });
  }
  if (!stats.isFile() || stats.isSymbolicLink()) {
    throw new Error(`${label} must be a regular non-symlink file: ${filePath}`);
  }
  return stats;
}

function decodeFlatZipName(nameBytes, index) {
  if (nameBytes.length === 0 || nameBytes.includes(0)) {
    throw new Error(`Reference bundle ZIP entry ${index} has an empty or NUL-containing name.`);
  }
  const name = nameBytes.toString('utf8');
  if (!Buffer.from(name, 'utf8').equals(nameBytes)) {
    throw new Error(`Reference bundle ZIP entry ${index} name is not valid UTF-8.`);
  }
  assertFlatFileName(name, `Reference bundle ZIP entry ${index}`);
  return name;
}

function assertFlatFileName(name, label) {
  if (
    typeof name !== 'string'
    || name.length === 0
    || name === '.'
    || name === '..'
    || name.includes('/')
    || name.includes('\\')
    || path.isAbsolute(name)
    || /^[A-Za-z]:/u.test(name)
    || !/^[A-Za-z0-9_.+-]+$/u.test(name)
  ) {
    throw new Error(`${label} must be one flat, traversal-free file name: ${describe(name)}`);
  }
}

function assertNotZipSymlink(versionMadeBy, externalAttributes, name) {
  const hostSystem = versionMadeBy >>> 8;
  if (hostSystem !== 3) return;
  const unixMode = externalAttributes >>> 16;
  const fileType = unixMode & 0xf000;
  if (fileType !== 0 && fileType !== 0x8000) {
    throw new Error(`Reference bundle ZIP entry ${name} is not a regular file (Unix mode 0${unixMode.toString(8)}).`);
  }
}

function assertBufferRange(buffer, offset, length, label) {
  if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(length) || offset < 0 || length < 0) {
    throw new Error(`${label} has an invalid byte range.`);
  }
  if (offset + length > buffer.length) {
    throw new Error(`${label} is truncated.`);
  }
}

function assertPlainObject(value, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label} must be an object.`);
  }
}

function assertExactKeys(value, expectedKeys, label) {
  const actualKeys = Object.keys(value).sort();
  const canonicalKeys = [...expectedKeys].sort();
  if (actualKeys.length !== canonicalKeys.length || actualKeys.some((key, index) => key !== canonicalKeys[index])) {
    throw new Error(
      `${label} keys must be exactly ${canonicalKeys.join(', ')}; received ${actualKeys.join(', ')}.`,
    );
  }
}

function assertPositiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0) {
    throw new Error(`${label} must be a positive safe integer; received ${describe(value)}.`);
  }
}

function assertNonEmptyString(value, label) {
  if (typeof value !== 'string' || value.trim().length === 0 || value !== value.trim()) {
    throw new Error(`${label} must be a non-empty trimmed string; received ${describe(value)}.`);
  }
}

function assertDecimalString(value, label) {
  if (typeof value !== 'string' || !/^[1-9]\d*$/u.test(value)) {
    throw new Error(`${label} must be a positive decimal string; received ${describe(value)}.`);
  }
}

function assertSha256(value, label) {
  if (typeof value !== 'string' || !/^[a-f0-9]{64}$/u.test(value)) {
    throw new Error(`${label} must be a lowercase SHA-256 digest; received ${describe(value)}.`);
  }
}

function sha256(buffer) {
  return createHash('sha256').update(buffer).digest('hex');
}

let crcTable;
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
  for (const byte of buffer) {
    value = crcTable[(value ^ byte) & 0xff] ^ (value >>> 8);
  }
  return (value ^ 0xffffffff) >>> 0;
}

function isMainModule() {
  return Boolean(process.argv[1])
    && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url));
}

function describe(value) {
  return value === undefined ? '<missing>' : JSON.stringify(value);
}

function formatError(error) {
  if (error instanceof AggregateError) {
    return `${error.message}\n${error.errors.map((item) => `  - ${formatError(item)}`).join('\n')}`;
  }
  return error instanceof Error ? error.message : String(error);
}

if (isMainModule()) {
  try {
    main();
  } catch (error) {
    console.error(`Build reference operation failed: ${formatError(error)}`);
    process.exitCode = 1;
  }
}

export {
  loadReferenceLock,
  parseStrictZip,
  readAndValidateReferenceBundle,
  requiredReferenceNames,
  restoreReferenceBundle,
  validateReferenceDirectory,
  validateReferenceLock,
};
