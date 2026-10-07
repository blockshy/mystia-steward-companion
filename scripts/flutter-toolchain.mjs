#!/usr/bin/env node

import { spawnSync } from 'node:child_process';
import { accessSync, constants, lstatSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const platformKeys = ['linux-x64', 'win32-x64'];
const exactVersion = /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/u;

export function assertLockedNode() {
  const expected = JSON.parse(readFileSync(path.join(repoRoot, 'toolchain.lock.json'), 'utf8')).node;
  if (process.versions.node !== expected) throw new Error(`Node.js ${expected} is required.`);
}

export function readFlutterLock(lockPath = path.join(repoRoot, 'toolchain.lock.json')) {
  return validateFlutterLock(JSON.parse(readFileSync(lockPath, 'utf8')).flutter);
}

export function validateFlutterLock(value) {
  assertKeys(value, [
    'version', 'channel', 'frameworkRevision', 'engineRevision', 'dartVersion', 'archives',
  ], 'flutter');
  for (const key of ['version', 'dartVersion']) {
    if (typeof value[key] !== 'string' || !exactVersion.test(value[key])) {
      throw new Error(`flutter.${key} must be an exact stable x.y.z version.`);
    }
  }
  if (value.channel !== 'stable') throw new Error('flutter.channel must be stable.');
  for (const key of ['frameworkRevision', 'engineRevision']) {
    if (typeof value[key] !== 'string' || !/^[a-f0-9]{40}$/u.test(value[key])) {
      throw new Error(`flutter.${key} must be a complete lowercase Git revision.`);
    }
  }
  assertKeys(value.archives, platformKeys, 'flutter.archives');
  for (const key of platformKeys) {
    const archive = value.archives[key];
    assertKeys(archive, ['url', 'size', 'sha256'], `flutter.archives.${key}`);
    const platform = key === 'linux-x64' ? 'linux' : 'windows';
    const extension = key === 'linux-x64' ? 'tar.xz' : 'zip';
    const expected = `https://storage.googleapis.com/flutter_infra_release/releases/stable/${platform}/flutter_${platform}_${value.version}-stable.${extension}`;
    if (archive.url !== expected) {
      throw new Error(`flutter.archives.${key}.url must identify the exact official release archive.`);
    }
    if (!Number.isSafeInteger(archive.size) || archive.size <= 0) {
      throw new Error(`flutter.archives.${key}.size must be a positive safe integer.`);
    }
    if (typeof archive.sha256 !== 'string' || !/^[a-f0-9]{64}$/u.test(archive.sha256)) {
      throw new Error(`flutter.archives.${key}.sha256 must be 64 lowercase hexadecimal characters.`);
    }
  }
  return value;
}

export function flutterPlatformKey(platform = process.platform, architecture = process.arch) {
  const key = `${platform}-${architecture}`;
  if (!platformKeys.includes(key)) throw new Error(`Locked Flutter does not support ${key}.`);
  return key;
}

export function assertFlutterVersion(report, lock) {
  validateFlutterLock(lock);
  if (!report || typeof report !== 'object' || Array.isArray(report)) {
    throw new Error('Flutter --version --machine did not return a JSON object.');
  }
  const fields = {
    frameworkVersion: lock.version,
    channel: lock.channel,
    frameworkRevision: lock.frameworkRevision,
    engineRevision: lock.engineRevision,
    dartSdkVersion: lock.dartVersion,
  };
  for (const [key, expected] of Object.entries(fields)) {
    if (report[key] !== expected) {
      throw new Error(`Installed Flutter ${key} is ${JSON.stringify(report[key])}; expected ${expected}.`);
    }
  }
  if (report.flutterVersion !== undefined && report.flutterVersion !== lock.version) {
    throw new Error(`Installed Flutter flutterVersion does not match ${lock.version}.`);
  }
  return report;
}

export function checkedPath(input, platform = process.platform) {
  if (typeof input !== 'string' || !input.trim() || /[\x00-\x1f\x7f]/u.test(input)) {
    throw new Error('Flutter path must be explicit and contain no control characters.');
  }
  // flutter.bat passes its own path through cmd.exe. Reject expansion/metacharacters,
  // even inside quotes, rather than changing the path before executing it.
  if (platform === 'win32' && /["%!^&|<>]/u.test(input)) {
    throw new Error('Flutter Windows paths must not contain cmd.exe metacharacters.');
  }
  return (platform === 'win32' ? path.win32 : path.posix).resolve(input);
}

export function tryLstat(target) {
  try {
    return lstatSync(target);
  } catch (error) {
    if (error.code === 'ENOENT') return null;
    throw error;
  }
}

export function assertRealDirectory(directory) {
  // Checking every ancestor also rejects a real child reached through a link/junction.
  let current = directory;
  while (true) {
    const stats = tryLstat(current);
    if (!stats?.isDirectory() || stats.isSymbolicLink()) {
      throw new Error(`Flutter directory must exist without symbolic links: ${current}`);
    }
    const parent = path.dirname(current);
    if (parent === current) return;
    current = parent;
  }
}

export function assertRegularFile(file, { executable = false } = {}) {
  assertRealDirectory(path.dirname(file));
  const stats = tryLstat(file);
  if (!stats?.isFile() || stats.isSymbolicLink() || stats.size <= 0) {
    throw new Error(`Flutter toolchain requires a non-empty regular file: ${file}`);
  }
  if (executable && process.platform !== 'win32') accessSync(file, constants.X_OK);
}

export function windowsSystemCommand(name, systemRoot = process.env.SystemRoot) {
  if (!systemRoot || !path.win32.isAbsolute(systemRoot)) {
    throw new Error('SystemRoot must identify the Windows system directory.');
  }
  return path.win32.join(checkedPath(systemRoot, 'win32'), 'System32', name);
}

export function resolveFlutterCommand(sdkRoot, args, {
  platform = process.platform,
  systemRoot = process.env.SystemRoot,
} = {}) {
  const root = checkedPath(sdkRoot, platform);
  if (!Array.isArray(args) || args.some((arg) => typeof arg !== 'string' || /[\x00-\x1f\x7f]/u.test(arg))) {
    throw new Error('Flutter arguments must be strings without control characters.');
  }
  if (platform === 'linux') {
    return { command: path.posix.join(root, 'bin', 'flutter'), args, windowsVerbatimArguments: false };
  }
  if (platform !== 'win32') throw new Error(`Unsupported Flutter platform: ${platform}`);
  if (args.some((arg) => /["%!^&|<>]/u.test(arg))) {
    throw new Error('Flutter Windows arguments must not contain cmd.exe metacharacters.');
  }
  const executable = path.win32.join(root, 'bin', 'flutter.bat');
  return {
    command: windowsSystemCommand('cmd.exe', systemRoot),
    args: ['/d', '/s', '/c', `""${executable}" ${args.map((arg) => `"${arg}"`).join(' ')}"`],
    windowsVerbatimArguments: true,
  };
}

export function runFlutterTool(command, args, options = {}) {
  const result = spawnSync(command, args, {
    encoding: 'utf8',
    windowsHide: true,
    shell: false,
    stdio: ['ignore', 'pipe', 'pipe'],
    timeout: 120_000,
    maxBuffer: 16 * 1024 * 1024,
    ...options,
  });
  if (result.error) throw new Error(`Flutter toolchain command failed: ${result.error.message}`);
  if (result.status !== 0) {
    throw new Error(`Flutter toolchain command exited with ${result.status}: ${(result.stderr || result.stdout || '').trim()}`);
  }
  return result.stdout;
}

export function checkInstalledFlutter(sdkRoot, lock = readFlutterLock()) {
  validateFlutterLock(lock);
  const key = flutterPlatformKey();
  const root = checkedPath(sdkRoot);
  assertRealDirectory(root);
  const executable = path.join(root, 'bin', key === 'win32-x64' ? 'flutter.bat' : 'flutter');
  assertRegularFile(executable, { executable: true });
  // A damaged archive must fail before Flutter's bootstrap can download another SDK.
  const dartVersionFile = path.join(root, 'bin', 'cache', 'dart-sdk', 'version');
  assertRegularFile(dartVersionFile);
  if (readFileSync(dartVersionFile, 'utf8').trim() !== lock.dartVersion) {
    throw new Error('Bundled Dart SDK version does not match the Flutter lock.');
  }
  assertRegularFile(path.join(root, 'bin', 'cache', 'flutter_tools.snapshot'));

  const env = { ...process.env, CI: 'true', FLUTTER_SUPPRESS_ANALYTICS: 'true' };
  for (const name of [
    'FLUTTER_TOOL_ARGS', 'FLUTTER_ENGINE', 'FLUTTER_ENGINE_SRC_PATH',
    'FLUTTER_PREBUILT_ENGINE_VERSION', 'FLUTTER_ROOT',
  ]) delete env[name];
  if (key === 'win32-x64') {
    // shared.bat may rebuild flutter_tools and print pub output on first use.
    // Finish that bootstrap separately; the next invocation must be pure JSON.
    const initialize = resolveFlutterCommand(root, ['--version']);
    assertRegularFile(initialize.command, { executable: true });
    runFlutterTool(initialize.command, initialize.args, {
      cwd: root, env, windowsVerbatimArguments: initialize.windowsVerbatimArguments,
    });
  }
  const invocation = resolveFlutterCommand(root, ['--version', '--machine']);
  assertRegularFile(invocation.command, { executable: true });
  const output = runFlutterTool(invocation.command, invocation.args, {
    cwd: root, env, windowsVerbatimArguments: invocation.windowsVerbatimArguments,
  });
  let report;
  try {
    report = JSON.parse(output);
  } catch {
    throw new Error(`Flutter --version --machine returned invalid JSON; stdout prefix: ${JSON.stringify(output.slice(0, 800))}`);
  }
  return assertFlutterVersion(report, lock);
}

function assertKeys(value, expected, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)
    || JSON.stringify(Object.keys(value).sort()) !== JSON.stringify([...expected].sort())) {
    throw new Error(`${label} has an incomplete or unexpected schema.`);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    assertLockedNode();
    const args = process.argv.slice(2);
    if (args.length === 1 && args[0] === '--policy-only') {
      const lock = readFlutterLock();
      console.log(`Flutter lock policy passed: ${lock.version}, Dart ${lock.dartVersion}.`);
    } else if (args.length === 2 && args[0] === '--sdk-root') {
      const report = checkInstalledFlutter(args[1]);
      console.log(`Locked Flutter ${report.frameworkVersion} / Dart ${report.dartSdkVersion} verified at ${checkedPath(args[1])}.`);
    } else {
      throw new Error('Usage: node scripts/flutter-toolchain.mjs --policy-only | --sdk-root <installed-sdk-directory>');
    }
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
