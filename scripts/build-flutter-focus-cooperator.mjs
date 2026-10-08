#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, constants, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertLockedNode } from './flutter-toolchain.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const lock = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json'), 'utf8'));
const scopes = ['tests/flutter-focus-cooperator', 'scripts/build-flutter-focus-cooperator.mjs',
  'scripts/flutter-toolchain.mjs', 'scripts/run-dotnet6-smoke.mjs', 'scripts/restore-build-references.mjs',
  'mods/bepinex/References/references.lock.json', 'global.json', 'toolchain.lock.json', '.gitattributes'];
function run(command, args, capture = false) {
  const result = spawnSync(command, args, { cwd: root, shell: false, windowsHide: true,
    encoding: 'utf8', stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${path.basename(command)} failed (${result.status}): ${capture ? result.stderr : ''}`);
  return capture ? result.stdout.trim() : '';
}
function cleanCommit() {
  const sha = run('git', ['rev-parse', 'HEAD'], true);
  if (!/^[a-f0-9]{40}$/u.test(sha) || run('git', ['status', '--porcelain=v1', '--untracked-files=all'], true)) {
    throw new Error('A clean, committed checkout is required.');
  }
  return sha;
}
function plain(file, directory = false) {
  const item = lstatSync(file);
  if (item.isSymbolicLink() || (directory ? !item.isDirectory() : !item.isFile())) throw new Error('Only real files/directories are allowed.');
}
function record(base, relative) {
  const file = path.join(base, relative);
  plain(file);
  const bytes = readFileSync(file);
  return { path: relative.split(path.sep).join('/'), size: bytes.length,
    sha256: createHash('sha256').update(bytes).digest('hex') };
}
try {
  assertLockedNode();
  const args = process.argv.slice(2);
  if (args.length !== 4 || args[0] !== '--dotnet' || args[2] !== '--output') throw new Error('Usage: --dotnet <absolute locked dotnet> --output <new absolute directory>');
  const [dotnet, output] = [args[1], args[3]];
  if (![dotnet, output].every(value => path.isAbsolute(value) && !/[\x00-\x1f]/u.test(value)) || existsSync(output)) throw new Error('Use absolute paths and a new output directory.');
  plain(dotnet);
  for (let parent = path.dirname(output);; parent = path.dirname(parent)) {
    plain(parent, true);
    if (parent === path.dirname(parent)) break;
  }
  if (run(dotnet, ['--version'], true) !== lock.dotnetSdk) throw new Error('The .NET SDK differs from toolchain.lock.json.');
  const commit = cleanCommit();
  run(process.execPath, ['scripts/restore-build-references.mjs', '--verify', '--output', 'mods/bepinex/References']);
  const sources = run('git', ['ls-files', '--', ...scopes], true).split('\n').sort();
  if (!sources.length || sources.some(value => !value)) throw new Error('Build sources are missing.');
  const sourceFiles = sources.map(value => record(root, value));
  const scratchRoot = path.join(root, 'temp/flutter-focus-cooperator-builds');
  mkdirSync(scratchRoot, { recursive: true });
  const scratch = mkdtempSync(path.join(scratchRoot, 'run-'));
  const compiled = path.join(scratch, 'compiled');
  run(dotnet, ['build', 'tests/flutter-focus-cooperator/MystiaStewardCompanion.FocusProbe.csproj', '-c', 'Release',
    '--no-incremental', '-p:ContinuousIntegrationBuild=true', `-p:ProbeGitSha=${commit}`,
    `-p:ReferenceDir=${path.join(root, 'mods/bepinex/References')}`, '-o', compiled]);
  if (cleanCommit() !== commit || JSON.stringify(sources.map(value => record(root, value))) !== JSON.stringify(sourceFiles)) throw new Error('Build identity/source bytes changed.');
  run(process.execPath, ['scripts/restore-build-references.mjs', '--verify', '--output', 'mods/bepinex/References']);
  const entrypoint = 'MystiaStewardCompanion.FocusProbe.dll';
  const dll = record(compiled, entrypoint);
  mkdirSync(output);
  copyFileSync(path.join(compiled, entrypoint), path.join(output, entrypoint), constants.COPYFILE_EXCL);
  if (JSON.stringify(record(output, entrypoint)) !== JSON.stringify(dll)) throw new Error('Copied plugin changed.');
  const evidence = { schemaVersion: 1, kind: 'flutter-focus-cooperator-bundle', commit, compiledGitSha: commit,
    cleanCheckout: true, target: 'net6.0-windows-in-game', entrypoint, builtUtc: new Date().toISOString(),
    tools: { node: process.versions.node, dotnetSdk: lock.dotnetSdk },
    toolchainLockSha256: record(root, 'toolchain.lock.json').sha256,
    referencesLockSha256: record(root, 'mods/bepinex/References/references.lock.json').sha256,
    sourceFiles, files: [dll], checks: { lockedReferences: 'passed', releaseBuild: 'passed', gameRuntime: 'not-run' } };
  writeFileSync(path.join(output, 'build-evidence.json'), `${JSON.stringify(evidence, null, 2)}\n`, { flag: 'wx' });
  console.log(JSON.stringify({ output, commit, dll, evidence: record(output, 'build-evidence.json') }));
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
