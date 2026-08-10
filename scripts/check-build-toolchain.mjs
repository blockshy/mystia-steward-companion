import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const supportedProfiles = new Set(['frontend', 'mod', 'tauri', 'full']);
const argumentsSet = new Set(process.argv.slice(2));
const policyOnly = argumentsSet.delete('--policy-only');
const requireCorepackInvocation = argumentsSet.delete('--require-corepack-invocation');

if (argumentsSet.size > 1) {
  failUsage();
}

const profile = argumentsSet.size === 1 ? [...argumentsSet][0] : 'full';
if (!supportedProfiles.has(profile)) {
  failUsage();
}

const failures = [];
const toolchain = await readJson('toolchain.lock.json');
const packageJson = await readJson('package.json');
const globalJson = await readJson('global.json');
const dotnet6GlobalJson = await readJson('tests/dotnet6-harmony/global.json');
const nvmVersion = (await readText('.nvmrc')).trim();
const rustToolchain = await readText('rust-toolchain.toml');
const cargoManifest = await readText('apps/companion/src-tauri/Cargo.toml');

validateLockedVersion('Node.js', toolchain.node);
validateLockedVersion('Corepack', toolchain.corepack);
validateLockedVersion('pnpm', toolchain.pnpm);
validateLockedVersion('.NET SDK', toolchain.dotnetSdk);
validateLockedVersion('Rust', toolchain.rust);
validateLockedVersion('.NET 6 Harmony SDK', toolchain.dotnet6HarmonySdk);

expectEqual('toolchain schema version', toolchain.schemaVersion, 1);
expectEqual('.nvmrc Node.js version', nvmVersion, toolchain.node);
expectEqual('package.json engines.node', packageJson.engines?.node, toolchain.node);
expectEqual('package.json engines.pnpm', packageJson.engines?.pnpm, toolchain.pnpm);

const packageManagerMatch = /^pnpm@([^+]+)\+sha512\.[A-Za-z0-9]+$/u.exec(
  packageJson.packageManager ?? '',
);
if (!packageManagerMatch) {
  failures.push('package.json packageManager must pin pnpm with an exact sha512 integrity hash.');
} else {
  expectEqual('package.json packageManager pnpm version', packageManagerMatch[1], toolchain.pnpm);
}

expectEqual('global.json SDK version', globalJson.sdk?.version, toolchain.dotnetSdk);
expectEqual('global.json SDK rollForward', globalJson.sdk?.rollForward, 'disable');
expectEqual('global.json SDK allowPrerelease', globalJson.sdk?.allowPrerelease, false);
expectEqual(
  '.NET 6 Harmony global.json SDK version',
  dotnet6GlobalJson.sdk?.version,
  toolchain.dotnet6HarmonySdk,
);
expectEqual(
  '.NET 6 Harmony global.json SDK rollForward',
  dotnet6GlobalJson.sdk?.rollForward,
  'disable',
);
expectEqual(
  '.NET 6 Harmony global.json SDK allowPrerelease',
  dotnet6GlobalJson.sdk?.allowPrerelease,
  false,
);
expectTomlValue('rust-toolchain.toml channel', rustToolchain, 'channel', toolchain.rust);
expectTomlValue('rust-toolchain.toml profile', rustToolchain, 'profile', 'minimal');
expectTomlValue('Cargo.toml rust-version', cargoManifest, 'rust-version', toolchain.rust);

if (!/^mcr\.microsoft\.com\/dotnet\/sdk@sha256:[a-f0-9]{64}$/u.test(
  toolchain.dotnet6HarmonyImage ?? '',
)) {
  failures.push('dotnet6HarmonyImage must use the locked Microsoft SDK image digest.');
}

if (!policyOnly) {
  const requiredTools = profileTools(profile);
  if (requireCorepackInvocation) {
    if (!requiredTools.has('pnpm')) {
      failures.push('Corepack invocation can only be required for a profile that includes pnpm.');
    } else if (!process.env.COREPACK_ROOT?.trim()) {
      failures.push('This package script must be invoked through Corepack; a global pnpm/npm invocation is not allowed.');
    }
  }
  if (requiredTools.has('node')) {
    expectEqual('installed Node.js version', process.version.replace(/^v/u, ''), toolchain.node);
  }
  if (requiredTools.has('corepack')) {
    expectCommandVersion('installed Corepack version', 'corepack', ['--version'], exactOutput, toolchain.corepack);
  }
  if (requiredTools.has('pnpm')) {
    expectCommandVersion(
      'Corepack-selected pnpm version',
      'corepack',
      ['pnpm', '--version'],
      exactOutput,
      toolchain.pnpm,
    );
  }
  if (requiredTools.has('dotnet')) {
    expectCommandVersion('installed .NET SDK version', 'dotnet', ['--version'], exactOutput, toolchain.dotnetSdk);
  }
  if (requiredTools.has('cargo')) {
    expectCommandVersion('installed Cargo version', 'cargo', ['--version'], cargoOutput, toolchain.rust);
  }
  if (requiredTools.has('rustc')) {
    expectCommandVersion('installed rustc version', 'rustc', ['--version'], rustcOutput, toolchain.rust);
  }
}

if (failures.length > 0) {
  console.error('Build toolchain check failed:');
  for (const failure of failures) {
    console.error(`  - ${failure}`);
  }
  process.exitCode = 1;
} else if (policyOnly) {
  console.log('Build toolchain policy is internally consistent.');
} else {
  console.log(`Build toolchain '${profile}' profile matches toolchain.lock.json.`);
}

function failUsage() {
  console.error(
    'Usage: node scripts/check-build-toolchain.mjs [frontend|mod|tauri|full] [--policy-only] [--require-corepack-invocation]',
  );
  process.exit(2);
}

function profileTools(selectedProfile) {
  switch (selectedProfile) {
    case 'frontend':
      return new Set(['node', 'corepack', 'pnpm']);
    case 'mod':
      return new Set(['dotnet']);
    case 'tauri':
      return new Set(['node', 'corepack', 'pnpm', 'cargo', 'rustc']);
    case 'full':
      return new Set(['node', 'corepack', 'pnpm', 'dotnet', 'cargo', 'rustc']);
    default:
      throw new Error(`Unsupported profile: ${selectedProfile}`);
  }
}

async function readJson(relativePath) {
  return JSON.parse(await readText(relativePath));
}

async function readText(relativePath) {
  return readFile(path.join(repoRoot, relativePath), 'utf8');
}

function validateLockedVersion(label, value) {
  if (!/^\d+\.\d+\.\d+$/u.test(value ?? '')) {
    failures.push(`${label} must be an exact three-part version; received ${describe(value)}.`);
  }
}

function expectEqual(label, actual, expected) {
  if (actual !== expected) {
    failures.push(`${label} must be ${describe(expected)}; received ${describe(actual)}.`);
  }
}

function expectTomlValue(label, source, key, expected) {
  const escapedKey = key.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const match = new RegExp(`^${escapedKey}\\s*=\\s*"([^"]+)"\\s*$`, 'mu').exec(source);
  expectEqual(label, match?.[1], expected);
}

function expectCommandVersion(label, command, args, parse, expected) {
  const useWindowsCorepackShim = process.platform === 'win32' && command === 'corepack';
  const executable = useWindowsCorepackShim ? (process.env.ComSpec ?? 'cmd.exe') : command;
  const executableArgs = useWindowsCorepackShim
    ? ['/d', '/s', '/c', ['corepack.cmd', ...args].join(' ')]
    : args;
  const result = spawnSync(executable, executableArgs, {
    cwd: repoRoot,
    encoding: 'utf8',
    env: {
      ...process.env,
      COREPACK_DEFAULT_TO_LATEST: '0',
      COREPACK_ENABLE_PROJECT_SPEC: '1',
      COREPACK_ENABLE_STRICT: '1',
    },
    windowsHide: true,
  });

  if (result.error) {
    failures.push(`${label} could not run ${command}: ${result.error.message}`);
    return;
  }
  if (result.status !== 0) {
    const diagnostic = [result.stderr, result.stdout].map((value) => value?.trim()).filter(Boolean).join(' | ');
    failures.push(`${label} command exited with ${result.status}${diagnostic ? `: ${diagnostic}` : ''}`);
    return;
  }

  const output = result.stdout.trim();
  const actual = parse(output);
  if (actual == null) {
    failures.push(`${label} returned an unrecognized value: ${describe(output)}.`);
    return;
  }
  expectEqual(label, actual, expected);
}

function exactOutput(output) {
  return /^\d+\.\d+\.\d+$/u.test(output) ? output : null;
}

function cargoOutput(output) {
  return /^cargo (\d+\.\d+\.\d+)(?:\s|$)/u.exec(output)?.[1] ?? null;
}

function rustcOutput(output) {
  return /^rustc (\d+\.\d+\.\d+)(?:\s|$)/u.exec(output)?.[1] ?? null;
}

function describe(value) {
  return value === undefined ? '<missing>' : JSON.stringify(value);
}
