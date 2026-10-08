#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  assertLockedNode, checkedPath, checkInstalledFlutter, resolveFlutterCommand,
} from './flutter-toolchain.mjs';
import { mergeWindowsEnvironment, prepareWindowsToolchain, verifyProjectToolchain } from './flutter-windows-toolchain.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const project = path.join(root, 'tests/flutter-window-probe');
let windowsTools;

function toolEnvironment() {
  const env = {
    ...process.env, CI: 'true', FLUTTER_SUPPRESS_ANALYTICS: 'true',
    PUB_CACHE: path.join(root, 'temp/flutter-pub-cache'),
  };
  for (const name of [
    'FLUTTER_TOOL_ARGS', 'FLUTTER_ENGINE', 'FLUTTER_ENGINE_SRC_PATH',
    'FLUTTER_PREBUILT_ENGINE_VERSION', 'FLUTTER_ROOT',
  ]) delete env[name];
  return windowsTools ? mergeWindowsEnvironment(env, windowsTools.environment) : env;
}

function run(command, args, extra = {}) {
  const result = spawnSync(command, args, {
    cwd: project, stdio: 'inherit', shell: false, windowsHide: true,
    env: toolEnvironment(), ...extra,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${path.basename(command)} exited with ${result.status}.`);
}

function git(args) {
  const result = spawnSync('git', args, {
    cwd: root, encoding: 'utf8', shell: false, windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`Git source verification failed: ${result.stderr.trim()}`);
  return result.stdout.trim();
}

function committedBuildIdentity() {
  const sha = git(['rev-parse', 'HEAD']);
  if (!/^[a-f0-9]{40}$/u.test(sha)) throw new Error('Windows builds require a complete Git commit SHA.');
  if (git(['status', '--porcelain=v1', '--untracked-files=all'])) {
    throw new Error('Windows builds require a clean committed checkout, including untracked files.');
  }
  for (const name of ['GITHUB_SHA', 'MYSTIA_WINDOW_PROBE_GIT_SHA']) {
    if (process.env[name] && process.env[name] !== sha) throw new Error(`${name} differs from checkout HEAD.`);
  }
  return sha;
}

try {
  const args = process.argv.slice(2);
  if (![2, 3].includes(args.length) || args[0] !== '--sdk-root' ||
      (args.length === 3 && args[2] !== '--build-windows')) {
    throw new Error('Usage: node scripts/run-flutter-window-probe.mjs --sdk-root <sdk> [--build-windows]');
  }
  assertLockedNode();
  const buildWindows = args[2] === '--build-windows';
  if (buildWindows && process.platform !== 'win32') throw new Error('Windows builds require a Windows host.');
  const commit = buildWindows ? committedBuildIdentity() : null;
  if (buildWindows) windowsTools = prepareWindowsToolchain();
  const sdk = checkedPath(args[1]);
  checkInstalledFlutter(sdk);
  const dart = path.join(sdk, 'bin/cache/dart-sdk/bin', process.platform === 'win32' ? 'dart.exe' : 'dart');
  const flutter = (flutterArgs, extra = {}) => {
    const invocation = resolveFlutterCommand(sdk, flutterArgs);
    run(invocation.command, invocation.args, {
      windowsVerbatimArguments: invocation.windowsVerbatimArguments, ...extra,
    });
  };
  flutter(['pub', 'get', '--enforce-lockfile']);
  const generatedRoot = path.join(root, 'temp/flutter-window-generated-checks');
  mkdirSync(generatedRoot, { recursive: true });
  const generated = mkdtempSync(path.join(generatedRoot, 'run-'));
  for (const schema of ['window_api', 'input_probe_api', 'control_probe_api']) {
    const outputs = [
      `lib/generated/${schema}.g.dart`,
      `windows/runner/${schema}.g.h`,
      `windows/runner/${schema}.g.cpp`,
    ];
    run(dart, ['run', 'pigeon', '--input', `pigeons/${schema}.dart`, '--base_path', generated]);
    run(dart, ['format', path.join(generated, outputs[0])]);
    for (const file of outputs) {
      if (!readFileSync(path.join(project, file)).equals(readFileSync(path.join(generated, file)))) {
        throw new Error(`Generated Pigeon output is stale: ${file}. Regenerate with the locked dependency and review both sides.`);
      }
    }
  }
  run(dart, ['format', '--output=none', '--set-exit-if-changed', 'lib', 'pigeons', 'test']);
  flutter(['analyze', '--fatal-infos', '--no-pub']);
  flutter(['test', '--no-pub']);
  if (buildWindows) {
    flutter(['build', 'windows', '--release', '--no-pub', `--dart-define=MYSTIA_WINDOW_PROBE_GIT_SHA=${commit}`], {
      env: { ...toolEnvironment(), MYSTIA_WINDOW_PROBE_GIT_SHA: commit },
    });
    verifyProjectToolchain(project, windowsTools);
    if (committedBuildIdentity() !== commit) throw new Error('Checkout changed during the Windows build.');
  }
  console.log('Flutter window probe checks passed. Windows compilation does not establish desktop runtime results.');
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
