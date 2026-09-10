import { spawnSync } from 'node:child_process';
import { readFileSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const toolchain = JSON.parse(readFileSync(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
const dotnet6GlobalJsonPath = path.join(repoRoot, 'tests/dotnet6/global.json');
const dotnet6GlobalJson = JSON.parse(readFileSync(dotnet6GlobalJsonPath, 'utf8'));

if (dotnet6GlobalJson.sdk?.version !== toolchain.dotnet6Sdk
  || dotnet6GlobalJson.sdk?.rollForward !== 'disable'
  || dotnet6GlobalJson.sdk?.allowPrerelease !== false) {
  throw new Error('The .NET 6 SDK selector does not match toolchain.lock.json.');
}
if (!/^mcr\.microsoft\.com\/dotnet\/sdk@sha256:[a-f0-9]{64}$/u.test(
  toolchain.dotnet6Image ?? '',
)) {
  throw new Error('The .NET 6 SDK image must be locked by a canonical SHA-256 digest.');
}

const smokeTests = new Map([
  ['local-api-storage', [
    'dotnet run --project tests/local-api-storage/LocalApiStorageSmoke.csproj -c Release --no-build',
  ]],
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
  ['runtime-rare-guest-participation', [
    'dotnet run --project tests/runtime-rare-guest-participation/RuntimeRareGuestParticipationSmoke.csproj -c Release',
  ]],
  ['night-business-lifecycle', [
    'dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release',
  ]],
]);

const requested = process.argv.slice(2);
const selected = requested.length === 0 || (requested.length === 1 && requested[0] === 'all')
  ? [...smokeTests.keys()]
  : requested;

if (selected.length === 0
  || new Set(selected).size !== selected.length
  || selected.some((name) => !smokeTests.has(name))) {
  console.error(`Usage: node scripts/run-dotnet6-smoke.mjs [all|${[...smokeTests.keys()].join('|')}]`);
  process.exit(2);
}

for (const name of selected) {
  console.log(`Running locked .NET 6 smoke: ${name}`);
  runDocker(smokeTests.get(name), name);
}

console.log(`Locked .NET 6 smoke passed: ${selected.join(', ')}.`);

function runDocker(commands, name) {
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
    'NUGET_PACKAGES=/tmp/nuget-packages',
    '--env',
    'XDG_DATA_HOME=/tmp/dotnet-data',
    '--env',
    'XDG_CACHE_HOME=/tmp/dotnet-cache',
    '--env',
    'DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1',
    '--env',
    'DOTNET_CLI_TELEMETRY_OPTOUT=1',
  ];

  if (process.platform !== 'win32') {
    const repositoryOwner = statSync(repoRoot);
    dockerArgs.push('--user', `${repositoryOwner.uid}:${repositoryOwner.gid}`);
  }

  dockerArgs.push(
    toolchain.dotnet6Image,
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
    console.error(`Unable to start .NET 6 container smoke ${name}: ${result.error.message}`);
    process.exit(1);
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}
