import { createHash, randomUUID } from 'node:crypto';
import { constants as fsConstants } from 'node:fs';
import {
  lstat,
  open,
  readFile,
  readdir,
  realpath,
  rm,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const GIB = 1024 ** 3;
const DAY_MS = 24 * 60 * 60 * 1000;
const FIXTURE_SENTINEL = '.mystia-build-artifacts-fixture';
const FIXTURE_SENTINEL_CONTENT = 'mystia-build-artifacts-fixture-v1';
const LOCK_STALE_AFTER_MS = DAY_MS;
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repositoryRoot = path.resolve(scriptDir, '..');

const androidCachePaths = [
  'apps/companion/src-tauri/gen/android/.gradle',
  'apps/companion/src-tauri/gen/android/build',
  'apps/companion/src-tauri/gen/android/app/build',
  'apps/companion/src-tauri/gen/android/app/.cxx',
  'apps/companion/src-tauri/gen/android/app/.kotlin',
  'apps/companion/src-tauri/gen/android/buildSrc/.gradle',
  'apps/companion/src-tauri/gen/android/buildSrc/.kotlin',
  'apps/companion/src-tauri/gen/android/buildSrc/build',
];

const frontendArtifactPaths = [
  'apps/companion/dist',
  'apps/companion/.vite',
];

const protectedPathPrefixes = [
  'mods/bepinex/dist',
  'mods/bepinex/References',
  'temp',
  'node_modules',
  '.playwright-cli',
  'apps/companion/src-tauri/gen/android/keystore.properties',
];

const categoryLabels = {
  android: 'Android cache',
  cargo: 'Cargo target',
  dotnet: '.NET output',
  frontend: 'Frontend output',
};

async function main(argv) {
  const options = parseArguments(argv);
  const root = await resolveManagedRoot(options.root);
  let releaseLock = null;

  try {
    if (options.command === 'prune' && readBooleanEnvironment('MYSTIA_SKIP_BUILD_CACHE_CLEANUP', false)) {
      console.log('Build artifact prune skipped by MYSTIA_SKIP_BUILD_CACHE_CLEANUP.');
      return;
    }

    if (options.command !== 'report') {
      releaseLock = await acquireExclusiveLock(root);
    }

    const candidates = await collectCandidates(root);
    printReport(candidates, options);

    if (options.command === 'report') return;

    const selections = options.command === 'clean'
      ? candidates.map((candidate) => ({ candidate, reason: 'clean' }))
      : selectPruneCandidates(candidates, options);

    await removeCandidates(root, selections, options.dryRun);
    printResult(candidates, selections, options);
  } finally {
    if (releaseLock) await releaseLock();
  }
}

function readBooleanEnvironment(name, defaultValue) {
  const rawValue = process.env[name];
  if (rawValue === undefined || rawValue.trim() === '') return defaultValue;
  if (/^(1|true)$/iu.test(rawValue)) return true;
  if (/^(0|false)$/iu.test(rawValue)) return false;
  throw new Error(`${name} must be 1, 0, true, or false. Actual: ${rawValue}`);
}

function parseArguments(argv) {
  const [command, ...rawOptions] = argv;
  if (!['report', 'prune', 'clean'].includes(command)) {
    throw new Error(usage(`Unknown or missing command: ${command ?? '(none)'}`));
  }

  const options = {
    command,
    dryRun: false,
    limitGiB: 12,
    targetGiB: 8,
    androidLimitGiB: 1.5,
    dotnetLimitGiB: 0.5,
    maxAgeDays: 14,
    root: '',
    rootProvided: false,
  };

  for (let index = 0; index < rawOptions.length; index += 1) {
    const argument = rawOptions[index];
    if (argument === '--') {
      continue;
    }
    if (argument === '--dry-run') {
      options.dryRun = true;
      continue;
    }

    const [name, inlineValue] = splitOption(argument);
    if (![
      '--limit-gib',
      '--target-gib',
      '--android-limit-gib',
      '--dotnet-limit-gib',
      '--max-age-days',
      '--root',
    ].includes(name)) {
      throw new Error(usage(`Unknown option: ${argument}`));
    }

    const value = inlineValue ?? rawOptions[++index];
    if (value === undefined || value.startsWith('--')) {
      throw new Error(usage(`Missing value for ${name}`));
    }

    if (name === '--root') {
      if (options.rootProvided) throw new Error('--root may only be provided once.');
      if (!value.trim()) throw new Error('--root must not be empty.');
      options.root = value;
      options.rootProvided = true;
      continue;
    }

    const numericValue = Number(value);
    const allowsZero = name === '--max-age-days';
    if (!Number.isFinite(numericValue) || numericValue < 0 || (!allowsZero && numericValue === 0)) {
      throw new Error(`${name} must be a ${allowsZero ? 'non-negative' : 'positive'} finite number.`);
    }

    if (name === '--limit-gib') options.limitGiB = numericValue;
    if (name === '--target-gib') options.targetGiB = numericValue;
    if (name === '--android-limit-gib') options.androidLimitGiB = numericValue;
    if (name === '--dotnet-limit-gib') options.dotnetLimitGiB = numericValue;
    if (name === '--max-age-days') options.maxAgeDays = numericValue;
  }

  if (options.targetGiB >= options.limitGiB) {
    throw new Error('--target-gib must be lower than --limit-gib.');
  }

  return options;
}

function splitOption(argument) {
  const separator = argument.indexOf('=');
  if (separator < 0) return [argument, undefined];
  return [argument.slice(0, separator), argument.slice(separator + 1)];
}

function usage(message) {
  return [
    message,
    '',
    'Usage:',
    '  node scripts/manage-build-artifacts.mjs report|prune|clean [options]',
    '',
    'Options:',
    '  --dry-run             Show removals without changing files',
    '  --limit-gib <number>   Prune high-water mark (default: 12)',
    '  --target-gib <number>  Prune low-water target (default: 8)',
    '  --android-limit-gib <number>  Android cache limit (default: 1.5)',
    '  --dotnet-limit-gib <number>   .NET output limit (default: 0.5)',
    '  --max-age-days <days>  Remove inactive buckets older than this (default: 14)',
  ].join('\n');
}

async function resolveManagedRoot(rootOption) {
  const requestedRoot = path.resolve(rootOption || repositoryRoot);
  const rootStats = await lstat(requestedRoot).catch((error) => {
    if (error.code === 'ENOENT') throw new Error(`Managed root does not exist: ${requestedRoot}`);
    throw error;
  });
  if (!rootStats.isDirectory() || rootStats.isSymbolicLink()) {
    throw new Error(`Managed root must be a real directory, not a symlink: ${requestedRoot}`);
  }

  const resolvedRoot = await realpath(requestedRoot);
  if (!samePath(requestedRoot, resolvedRoot)) {
    throw new Error(`Managed root must not be reached through a symlink: ${requestedRoot}`);
  }
  if (samePath(resolvedRoot, path.parse(resolvedRoot).root)) {
    throw new Error('Refusing to manage a filesystem root.');
  }

  if (rootOption) {
    const sentinelPath = path.join(resolvedRoot, FIXTURE_SENTINEL);
    const sentinelStats = await lstat(sentinelPath).catch((error) => {
      if (error.code === 'ENOENT') {
        throw new Error(`Fixture root is missing safety sentinel: ${sentinelPath}`);
      }
      throw error;
    });
    if (!sentinelStats.isFile() || sentinelStats.isSymbolicLink()) {
      throw new Error(`Fixture safety sentinel must be a regular file: ${sentinelPath}`);
    }
    const sentinelContent = (await readFile(sentinelPath, 'utf8')).trim();
    if (sentinelContent !== FIXTURE_SENTINEL_CONTENT) {
      throw new Error(`Fixture root has an invalid safety sentinel: ${sentinelPath}`);
    }
  }

  return resolvedRoot;
}

async function collectCandidates(root) {
  const candidateDefinitions = [
    ...await discoverCargoBuckets(root),
    ...androidCachePaths.map((relativePath) => ({ category: 'android', relativePath })),
    ...frontendArtifactPaths.map((relativePath) => ({ category: 'frontend', relativePath })),
    ...await discoverDotnetOutputs(root),
  ];
  const uniqueDefinitions = new Map();
  for (const definition of candidateDefinitions) {
    const normalizedRelativePath = normalizeRelativePath(definition.relativePath);
    assertNotProtected(normalizedRelativePath);
    uniqueDefinitions.set(normalizedRelativePath, {
      ...definition,
      relativePath: normalizedRelativePath,
    });
  }

  const candidates = [];
  for (const definition of uniqueDefinitions.values()) {
    const absolutePath = path.join(root, definition.relativePath);
    const stats = await lstat(absolutePath).catch((error) => {
      if (error.code === 'ENOENT') return null;
      throw error;
    });
    if (!stats) continue;
    if (!stats.isDirectory() || stats.isSymbolicLink()) {
      throw new Error(`Managed bucket must be a real directory: ${absolutePath}`);
    }

    await assertSafePath(root, absolutePath);
    const measurement = await measureDirectory(absolutePath);
    candidates.push({
      ...definition,
      absolutePath,
      bytes: measurement.bytes,
      modifiedAtMs: measurement.modifiedAtMs,
    });
  }

  return candidates.sort((left, right) => left.relativePath.localeCompare(right.relativePath));
}

async function discoverCargoBuckets(root) {
  const relativeTargetRoot = 'apps/companion/src-tauri/target';
  const targetRoot = path.join(root, relativeTargetRoot);
  const targetStats = await lstat(targetRoot).catch((error) => {
    if (error.code === 'ENOENT') return null;
    throw error;
  });
  if (!targetStats) return [];
  if (!targetStats.isDirectory() || targetStats.isSymbolicLink()) {
    throw new Error(`Cargo target root must be a real directory: ${targetRoot}`);
  }

  await assertSafePath(root, targetRoot);
  const definitions = [];
  for (const entry of await readdir(targetRoot, { withFileTypes: true })) {
    const entryPath = path.join(targetRoot, entry.name);
    if (entry.isSymbolicLink()) {
      throw new Error(`Refusing symbolic link in Cargo target root: ${entryPath}`);
    }
    if (entry.isDirectory()) {
      definitions.push({
        category: 'cargo',
        relativePath: path.join(relativeTargetRoot, entry.name),
      });
    }
  }
  return definitions;
}

async function discoverDotnetOutputs(root) {
  const projectDirectories = new Set();
  for (const relativeSearchRoot of ['mods', 'tests']) {
    const searchRoot = path.join(root, relativeSearchRoot);
    const stats = await lstat(searchRoot).catch((error) => {
      if (error.code === 'ENOENT') return null;
      throw error;
    });
    if (!stats) continue;
    if (!stats.isDirectory() || stats.isSymbolicLink()) {
      throw new Error(`.NET search root must be a real directory: ${searchRoot}`);
    }

    const stack = [searchRoot];
    while (stack.length > 0) {
      const current = stack.pop();
      for (const entry of await readdir(current, { withFileTypes: true })) {
        if (entry.isSymbolicLink()) continue;
        const entryPath = path.join(current, entry.name);
        if (entry.isFile() && entry.name.endsWith('.csproj')) {
          projectDirectories.add(current);
          continue;
        }
        if (!entry.isDirectory() || shouldSkipDotnetDiscoveryDirectory(entry.name)) continue;
        stack.push(entryPath);
      }
    }
  }

  return [...projectDirectories].flatMap((projectDirectory) => ['bin', 'obj'].map((name) => ({
    category: 'dotnet',
    relativePath: path.relative(root, path.join(projectDirectory, name)),
  })));
}

function shouldSkipDotnetDiscoveryDirectory(name) {
  return ['.git', '.playwright-cli', 'References', 'bin', 'dist', 'node_modules', 'obj', 'target', 'temp'].includes(name);
}

async function assertSafePath(root, candidatePath) {
  const relativePath = path.relative(root, candidatePath);
  if (!relativePath || relativePath.startsWith(`..${path.sep}`) || path.isAbsolute(relativePath)) {
    throw new Error(`Managed path is outside the repository root: ${candidatePath}`);
  }

  let current = root;
  for (const segment of relativePath.split(path.sep)) {
    current = path.join(current, segment);
    const stats = await lstat(current);
    if (stats.isSymbolicLink()) {
      throw new Error(`Refusing symbolic link in managed path: ${current}`);
    }
  }

  const resolvedCandidate = await realpath(candidatePath);
  if (!isPathInside(root, resolvedCandidate)) {
    throw new Error(`Managed path resolves outside the repository root: ${candidatePath}`);
  }
}

async function measureDirectory(rootDirectory) {
  let bytes = 0;
  let modifiedAtMs = (await lstat(rootDirectory)).mtimeMs;
  const stack = [rootDirectory];

  while (stack.length > 0) {
    const current = stack.pop();
    for (const entry of await readdir(current, { withFileTypes: true })) {
      const entryPath = path.join(current, entry.name);
      const stats = await lstat(entryPath);
      if (stats.isSymbolicLink()) {
        throw new Error(`Refusing symbolic link inside managed bucket: ${entryPath}`);
      }

      modifiedAtMs = Math.max(modifiedAtMs, stats.mtimeMs);
      if (stats.isDirectory()) {
        stack.push(entryPath);
      } else if (stats.isFile()) {
        bytes += stats.size;
      } else {
        throw new Error(`Refusing unsupported filesystem entry inside managed bucket: ${entryPath}`);
      }
    }
  }

  return { bytes, modifiedAtMs };
}

function selectPruneCandidates(candidates, options) {
  const now = Date.now();
  const limitBytes = options.limitGiB * GIB;
  const targetBytes = options.targetGiB * GIB;
  const oldestFirst = [...candidates].sort((left, right) => (
    left.modifiedAtMs - right.modifiedAtMs || left.relativePath.localeCompare(right.relativePath)
  ));
  const selectedPaths = new Set();
  const selections = [];
  let remainingBytes = sumBytes(candidates);

  for (const candidate of oldestFirst) {
    const ageDays = Math.max(0, now - candidate.modifiedAtMs) / DAY_MS;
    if (ageDays < options.maxAgeDays) continue;
    selectedPaths.add(candidate.relativePath);
    selections.push({ candidate, reason: 'expired' });
    remainingBytes -= candidate.bytes;
  }

  const categoryLimits = [
    { category: 'android', bytes: options.androidLimitGiB * GIB },
    { category: 'dotnet', bytes: options.dotnetLimitGiB * GIB },
  ];
  for (const categoryLimit of categoryLimits) {
    let categoryBytes = sumBytes(candidates.filter((candidate) => (
      candidate.category === categoryLimit.category && !selectedPaths.has(candidate.relativePath)
    )));
    if (categoryBytes <= categoryLimit.bytes) continue;

    for (const candidate of oldestFirst) {
      if (candidate.category !== categoryLimit.category || selectedPaths.has(candidate.relativePath)) continue;
      selectedPaths.add(candidate.relativePath);
      selections.push({ candidate, reason: `${categoryLimit.category}-quota` });
      remainingBytes -= candidate.bytes;
      categoryBytes -= candidate.bytes;
      if (categoryBytes <= categoryLimit.bytes) break;
    }
  }

  if (remainingBytes > limitBytes) {
    for (const candidate of oldestFirst) {
      if (selectedPaths.has(candidate.relativePath)) continue;
      selectedPaths.add(candidate.relativePath);
      selections.push({ candidate, reason: 'quota' });
      remainingBytes -= candidate.bytes;
      if (remainingBytes <= targetBytes) break;
    }
  }

  return selections;
}

async function removeCandidates(root, selections, dryRun) {
  for (const { candidate, reason } of selections) {
    const action = dryRun ? 'Would remove' : 'Removing';
    console.log(`${action} [${reason}] ${candidate.relativePath} (${formatBytes(candidate.bytes)})`);
    if (dryRun) continue;

    const stats = await lstat(candidate.absolutePath).catch((error) => {
      if (error.code === 'ENOENT') return null;
      throw error;
    });
    if (!stats) throw new Error(`Managed bucket disappeared before removal: ${candidate.absolutePath}`);
    if (!stats.isDirectory() || stats.isSymbolicLink()) {
      throw new Error(`Managed bucket changed type before removal: ${candidate.absolutePath}`);
    }
    await assertSafePath(root, candidate.absolutePath);
    await rm(candidate.absolutePath, { recursive: true, force: false, maxRetries: 3, retryDelay: 100 });
  }
}

function printReport(candidates, options) {
  console.log(`Managed build artifacts: ${candidates.length} bucket(s), ${formatBytes(sumBytes(candidates))}`);
  for (const candidate of candidates) {
    const ageDays = Math.max(0, Date.now() - candidate.modifiedAtMs) / DAY_MS;
    console.log(
      `  ${categoryLabels[candidate.category].padEnd(16)} ${formatBytes(candidate.bytes).padStart(10)}  ${formatAge(ageDays).padStart(9)}  ${candidate.relativePath}`,
    );
  }
  console.log(
    `Policy: limit=${formatBytes(options.limitGiB * GIB)}, target=${formatBytes(options.targetGiB * GIB)}, Android=${formatBytes(options.androidLimitGiB * GIB)}, .NET=${formatBytes(options.dotnetLimitGiB * GIB)}, max-age=${options.maxAgeDays} day(s)`,
  );
  if (options.command !== 'report') {
    console.log('Concurrency: prune/clean must not run while Cargo, Gradle, Vite, or dotnet builds are active.');
  }
}

function printResult(candidates, selections, options) {
  const removedBytes = sumBytes(selections.map(({ candidate }) => candidate));
  const remainingBytes = Math.max(0, sumBytes(candidates) - removedBytes);
  const verb = options.dryRun ? 'would remove' : 'removed';
  console.log(`Artifact ${options.command} ${verb} ${selections.length} bucket(s), ${formatBytes(removedBytes)}; remaining ${formatBytes(remainingBytes)}.`);
}

async function acquireExclusiveLock(root) {
  const lockPath = lockPathForRoot(root);
  const token = randomUUID();
  let handle;

  for (let attempt = 0; attempt < 2; attempt += 1) {
    try {
      handle = await open(lockPath, fsConstants.O_CREAT | fsConstants.O_EXCL | fsConstants.O_WRONLY, 0o600);
      break;
    } catch (error) {
      if (error.code !== 'EEXIST') throw error;
      const lockOwner = await readLockOwner(lockPath);
      if (lockOwner && isProcessAlive(lockOwner.pid)) {
        throw new Error(`Another artifact prune/clean is running (pid ${lockOwner.pid}): ${lockPath}`);
      }

      const lockStats = await lstat(lockPath).catch(() => null);
      const malformedLockIsFresh = !lockOwner && lockStats && Date.now() - lockStats.mtimeMs < LOCK_STALE_AFTER_MS;
      if (malformedLockIsFresh) {
        throw new Error(`Artifact cleanup lock exists and cannot be verified: ${lockPath}`);
      }
      await rm(lockPath, { force: true });
    }
  }

  if (!handle) throw new Error(`Unable to acquire artifact cleanup lock: ${lockPath}`);
  try {
    await handle.writeFile(JSON.stringify({ pid: process.pid, token, createdAt: new Date().toISOString() }));
  } catch (error) {
    await handle.close().catch(() => {});
    await rm(lockPath, { force: true }).catch(() => {});
    throw error;
  }

  return async () => {
    await handle.close();
    const currentOwner = await readLockOwner(lockPath);
    if (currentOwner?.token === token) await rm(lockPath, { force: true });
  };
}

async function readLockOwner(lockPath) {
  try {
    const parsed = JSON.parse(await readFile(lockPath, 'utf8'));
    if (!Number.isInteger(parsed.pid) || parsed.pid <= 0 || typeof parsed.token !== 'string') return null;
    return parsed;
  } catch {
    return null;
  }
}

function isProcessAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code !== 'ESRCH';
  }
}

function lockPathForRoot(root) {
  const digest = createHash('sha256').update(normalizeForComparison(root)).digest('hex').slice(0, 24);
  return path.join(os.tmpdir(), `mystia-build-artifacts-${digest}.lock`);
}

function assertNotProtected(relativePath) {
  const normalized = normalizeRelativePath(relativePath);
  for (const protectedPrefix of protectedPathPrefixes) {
    const normalizedPrefix = normalizeRelativePath(protectedPrefix);
    if (normalized === normalizedPrefix || normalized.startsWith(`${normalizedPrefix}/`)) {
      throw new Error(`Managed bucket overlaps protected path: ${relativePath}`);
    }
  }
}

function normalizeRelativePath(value) {
  const normalized = value.split(path.sep).join('/').replace(/^\.\//, '').replace(/\/$/, '');
  if (!normalized || normalized === '..' || normalized.startsWith('../') || path.posix.isAbsolute(normalized)) {
    throw new Error(`Invalid managed relative path: ${value}`);
  }
  return normalized;
}

function isPathInside(root, candidate) {
  const relativePath = path.relative(root, candidate);
  return Boolean(relativePath) && !relativePath.startsWith(`..${path.sep}`) && !path.isAbsolute(relativePath);
}

function samePath(left, right) {
  return normalizeForComparison(left) === normalizeForComparison(right);
}

function normalizeForComparison(value) {
  const normalized = path.resolve(value);
  return process.platform === 'win32' ? normalized.toLowerCase() : normalized;
}

function sumBytes(candidates) {
  return candidates.reduce((total, candidate) => total + candidate.bytes, 0);
}

function formatBytes(bytes) {
  if (bytes === 0) return '0 B';
  const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB'];
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / (1024 ** exponent);
  const digits = value >= 100 || exponent === 0 ? 0 : value >= 10 ? 1 : 2;
  return `${value.toFixed(digits)} ${units[exponent]}`;
}

function formatAge(days) {
  if (days < 1 / 24) return '<1 hour';
  if (days < 1) return `${Math.floor(days * 24)} hour(s)`;
  return `${Math.floor(days)} day(s)`;
}

const invokedAsScript = process.argv[1]
  && samePath(fileURLToPath(import.meta.url), process.argv[1]);
if (invokedAsScript) {
  main(process.argv.slice(2)).catch((error) => {
    console.error(`Artifact management failed: ${error.message}`);
    process.exitCode = 1;
  });
}

export {
  FIXTURE_SENTINEL,
  FIXTURE_SENTINEL_CONTENT,
  lockPathForRoot,
  main,
};
