import { spawnSync } from 'node:child_process';
import { readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const toolchain = JSON.parse(readFileSync(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
const dotnet6GlobalJsonPath = path.join(repoRoot, 'tests/dotnet6-harmony/global.json');
const dotnet6GlobalJson = JSON.parse(readFileSync(dotnet6GlobalJsonPath, 'utf8'));

if (dotnet6GlobalJson.sdk?.version !== toolchain.dotnet6HarmonySdk
  || dotnet6GlobalJson.sdk?.rollForward !== 'disable') {
  throw new Error('The .NET 6 Harmony SDK selector does not match toolchain.lock.json.');
}

const smokeTests = new Map([
  ['automation-cooking-job', [
    'dotnet run --project tests/automation-cooking-job/AutomationCookingJobSmoke.csproj -c Release',
  ]],
  ['ui-pinning-runtime', [
    'dotnet run --project tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj -c Release',
  ]],
  ['runtime-target-recipe-variant', [
    'dotnet build tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release -t:Rebuild',
    'dotnet run --project tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release --no-build',
  ]],
]);

const requested = process.argv.slice(2);
const selected = requested.length === 0 || (requested.length === 1 && requested[0] === 'all')
  ? [...smokeTests.keys()]
  : requested;

if (selected.length === 0 || selected.some((name) => !smokeTests.has(name))) {
  console.error(`Usage: node scripts/run-dotnet6-harmony-smoke.mjs [all|${[...smokeTests.keys()].join('|')}]`);
  process.exit(2);
}

for (const name of selected) {
  const commands = smokeTests.get(name);
  console.log(`Running locked .NET 6 Harmony smoke: ${name}`);
  const dockerArgs = [
    'run',
    '--rm',
    '--mount',
    `type=bind,source=${repoRoot},target=/workspace`,
    '--mount',
    `type=bind,source=${dotnet6GlobalJsonPath},target=/workspace/global.json,readonly`,
    '--workdir',
    '/workspace',
    '--env',
    'DOTNET_CLI_HOME=/tmp/dotnet-home',
    '--env',
    'HOME=/tmp',
  ];

  if (process.platform !== 'win32') {
    const repositoryOwner = statSync(repoRoot);
    dockerArgs.push('--user', `${repositoryOwner.uid}:${repositoryOwner.gid}`);
  }

  dockerArgs.push(
    toolchain.dotnet6HarmonyImage,
    '/bin/sh',
    '-eu',
    '-c',
    commands.join(' && '),
  );

  const result = spawnSync('docker', dockerArgs, {
    cwd: repoRoot,
    stdio: 'inherit',
    windowsHide: true,
  });
  if (result.error) {
    console.error(`Unable to start Docker for ${name}: ${result.error.message}`);
    process.exit(1);
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

console.log(`Locked .NET 6 Harmony smoke passed: ${selected.join(', ')}.`);
