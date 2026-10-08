#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertLockedNode, checkInstalledFlutter } from './flutter-toolchain.mjs';

assertLockedNode();
if (process.platform !== 'linux' || process.arch !== 'x64') throw new Error('This isolated .NET 6 fixture builder currently targets Linux x64.');
const root = fileURLToPath(new URL('..', import.meta.url));
const lock = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json'), 'utf8'));
const flutter = path.join(root, 'temp', 'toolchains', `flutter-${lock.flutter.version}`);
const dotnetRoot = path.join(root, 'temp', 'toolchains', `dotnet-sdk-${lock.dotnetSdk}`);
const dotnet = path.join(dotnetRoot, 'dotnet');
const dart = path.join(flutter, 'bin', 'cache', 'dart-sdk', 'bin', 'dart');
const env = { ...process.env, DOTNET_ROOT: dotnetRoot, PUB_CACHE: path.join(root, 'temp', 'flutter-identity-pub-cache') };
function run(executable, args, cwd = root, capture = false) {
  const result = spawnSync(executable, args, { cwd, env, shell: false, encoding: 'utf8', stdio: capture ? 'pipe' : 'inherit', timeout: 120_000 });
  if (result.error || result.status !== 0) throw new Error(`Identity fixture build step failed: ${path.basename(executable)} (${result.status ?? result.error?.code}).`);
  return result.stdout?.trim();
}
checkInstalledFlutter(flutter);
if (run(dotnet, ['--version'], root, true) !== lock.dotnetSdk) throw new Error('Identity fixture requires the locked .NET SDK.');
run(process.execPath, ['scripts/restore-build-references.mjs', '--verify', '--output', 'mods/bepinex/References']);
run(dotnet, ['build', 'tests/identity-migration/IdentityMigrationHost.csproj', '-c', 'Release', '--nologo']);
const project = path.join(root, 'tests', 'flutter-identity-probe');
run(dart, ['pub', 'get', '--enforce-lockfile'], project);
run(dart, ['analyze', '--fatal-infos'], project);
run(dart, ['compile', 'kernel', 'bin/probe.dart', '-o', '.dart_tool/identity-probe.dill'], project);
console.log('Identity fixture host and Dart kernel built with the locked toolchains. Use the sole pnpm test:dotnet6 identity-migration entry for execution.');
