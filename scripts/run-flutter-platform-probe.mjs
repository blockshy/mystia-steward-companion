#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { checkedPath, checkInstalledFlutter, resolveFlutterCommand } from './flutter-toolchain.mjs';
import { mergeWindowsEnvironment, prepareWindowsToolchain, verifyProjectToolchain } from './flutter-windows-toolchain.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const project = path.join(root, 'tests/flutter-platform-probe');
let windowsTools;

function run(command, args, extra = {}) {
  const env = { ...process.env, CI: 'true', FLUTTER_SUPPRESS_ANALYTICS: 'true', PUB_CACHE: path.join(root, 'temp/flutter-pub-cache') };
  for (const name of ['FLUTTER_TOOL_ARGS', 'FLUTTER_ENGINE', 'FLUTTER_ENGINE_SRC_PATH', 'FLUTTER_PREBUILT_ENGINE_VERSION', 'FLUTTER_ROOT']) {
    delete env[name];
  }
  const result = spawnSync(command, args, {
    cwd: project, stdio: 'inherit', shell: false, windowsHide: true,
    env: windowsTools ? mergeWindowsEnvironment(env, windowsTools.environment) : env,
    ...extra,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${path.basename(command)} exited with ${result.status}.`);
}

try {
  const args = process.argv.slice(2);
  if (![2, 3].includes(args.length) || args[0] !== '--sdk-root' ||
      (args.length === 3 && args[2] !== '--build-windows')) {
    throw new Error('Usage: node scripts/run-flutter-platform-probe.mjs --sdk-root <sdk> [--build-windows]');
  }
  const policy = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json'), 'utf8'));
  if (process.versions.node !== policy.node) throw new Error(`Node ${policy.node} is required.`);
  if (args[2] === '--build-windows') windowsTools = prepareWindowsToolchain();
  const sdk = checkedPath(args[1]);
  checkInstalledFlutter(sdk);
  const flutter = (flutterArgs) => {
    const call = resolveFlutterCommand(sdk, flutterArgs);
    run(call.command, call.args, { windowsVerbatimArguments: call.windowsVerbatimArguments });
  };
  const dart = path.join(sdk, 'bin/cache/dart-sdk/bin', process.platform === 'win32' ? 'dart.exe' : 'dart');
  flutter(['pub', 'get', '--enforce-lockfile']);
  const generatedRoot = path.join(root, 'temp/flutter-generated-checks');
  mkdirSync(generatedRoot, { recursive: true });
  const generated = mkdtempSync(path.join(generatedRoot, 'run-'));
  const outputs = [
    ['dart_out', 'lib/generated/probe_api.g.dart'],
    ['cpp_header_out', 'windows/runner/probe_api.g.h'],
    ['cpp_source_out', 'windows/runner/probe_api.g.cpp'],
  ];
  run(dart, ['run', 'pigeon', '--input', 'pigeons/probe_api.dart', '--base_path', generated]);
  run(dart, ['format', path.join(generated, 'lib/generated/probe_api.g.dart')]);
  for (const [, file] of outputs) {
    if (!readFileSync(path.join(project, file)).equals(readFileSync(path.join(generated, file)))) {
      throw new Error(`Generated Pigeon output is stale: ${file}. Run the pinned generator and review both sides.`);
    }
  }
  run(dart, ['format', '--output=none', '--set-exit-if-changed', 'lib', 'pigeons', 'test']);
  flutter(['analyze', '--fatal-infos', '--no-pub']);
  flutter(['test', '--no-pub']);
  if (args[2] === '--build-windows') {
    if (process.platform !== 'win32') throw new Error('Windows probe builds require a Windows host.');
    flutter(['build', 'windows', '--release', '--no-pub']);
    verifyProjectToolchain(project, windowsTools);
  }
  console.log('Flutter platform probe checks passed. Platform runtime evidence is collected separately.');
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
