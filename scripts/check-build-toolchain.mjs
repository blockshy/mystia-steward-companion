import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync, realpathSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const supportedProfiles = new Set([
  'frontend',
  'mod',
  'tauri',
  'android',
  'full',
  'release',
  'release-tools',
]);
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
const androidGradleManifest = await readText('apps/companion/src-tauri/gen/android/app/build.gradle.kts');
const androidGradleWrapper = await readText(
  'apps/companion/src-tauri/gen/android/gradle/wrapper/gradle-wrapper.properties',
);

validateLockedVersion('Node.js', toolchain.node);
validateLockedVersion('Corepack', toolchain.corepack);
validateLockedVersion('pnpm', toolchain.pnpm);
validateLockedVersion('PowerShell', toolchain.powershell);
validateLockedVersion('GitHub CLI', toolchain.githubCli);
validateLockedVersion('.NET SDK', toolchain.dotnetSdk);
validateLockedVersion('Rust', toolchain.rust);
validateLockedVersion('Android Temurin JDK', toolchain.android?.jdkVersion);
validateLockedVersion('Android Gradle', toolchain.android?.gradle);
validateLockedVersion('Android Build Tools', toolchain.android?.buildTools);
validateLockedVersion('Android NDK package', toolchain.android?.ndkPackage);
validateLockedRevision('Android NDK revision', toolchain.android?.ndkRevision);
validateLockedVersion('.NET 6 Harmony SDK', toolchain.dotnet6HarmonySdk);

if (!/^sha512-[A-Za-z0-9+/]+={0,2}$/u.test(toolchain.corepackIntegrity ?? '')) {
  failures.push('Corepack package integrity must be a canonical sha512 Subresource Integrity value.');
}

expectEqual('toolchain schema version', toolchain.schemaVersion, 3);
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
expectEqual('Android JDK distribution', toolchain.android?.jdkDistribution, 'temurin');
expectEqual('Android JDK vendor', toolchain.android?.jdkVendor, 'Eclipse Adoptium');
validatePositiveInteger('Android compile SDK', toolchain.android?.compileSdk);
validatePositiveInteger('Android target SDK', toolchain.android?.targetSdk);
expectEqual(
  'Android NDK package/revision base',
  toolchain.android?.ndkRevision?.split('-')[0],
  toolchain.android?.ndkPackage,
);
expectEqual(
  'Android Rust targets',
  JSON.stringify(toolchain.android?.rustTargets),
  JSON.stringify(['aarch64-linux-android', 'armv7-linux-androideabi']),
);
expectGradleInteger(
  'Android Gradle compileSdk',
  androidGradleManifest,
  'compileSdk',
  toolchain.android?.compileSdk,
);
expectGradleInteger(
  'Android Gradle targetSdk',
  androidGradleManifest,
  'targetSdk',
  toolchain.android?.targetSdk,
);
expectGradleString(
  'Android Gradle Build Tools',
  androidGradleManifest,
  'buildToolsVersion',
  toolchain.android?.buildTools,
);
expectGradleWrapperProperty(
  'Android Gradle distribution URL',
  androidGradleWrapper,
  'distributionUrl',
  `https://services.gradle.org/distributions/gradle-${toolchain.android?.gradle}-bin.zip`,
);
expectGradleWrapperProperty(
  'Android Gradle distribution SHA-256',
  androidGradleWrapper,
  'distributionSha256Sum',
  toolchain.android?.gradleDistributionSha256,
);

if (!/^[a-f0-9]{64}$/u.test(toolchain.android?.signingCertificateSha256 ?? '')) {
  failures.push('Android signing certificate SHA-256 must be exactly 64 lowercase hexadecimal characters.');
}
if (!/^[a-f0-9]{64}$/u.test(toolchain.android?.gradleDistributionSha256 ?? '')) {
  failures.push('Android Gradle distribution SHA-256 must be exactly 64 lowercase hexadecimal characters.');
}

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
  if (requiredTools.has('powershell')) {
    expectCommandVersion(
      'installed PowerShell version',
      'pwsh',
      ['--version'],
      powerShellOutput,
      toolchain.powershell,
    );
  }
  if (requiredTools.has('githubCli')) {
    expectCommandVersion(
      'installed GitHub CLI version',
      'gh',
      ['--version'],
      githubCliOutput,
      toolchain.githubCli,
    );
  }
  if (requiredTools.has('java')) {
    expectTemurinJdk(toolchain.android);
  }
  if (requiredTools.has('androidSdk')) {
    expectAndroidSdk(toolchain.android);
  }
  if (requiredTools.has('androidNdk')) {
    expectAndroidNdk(toolchain.android);
  }
  if (requiredTools.has('androidRustTargets')) {
    expectAndroidRustTargets(toolchain.android);
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
    'Usage: node scripts/check-build-toolchain.mjs [frontend|mod|tauri|android|full|release|release-tools] [--policy-only] [--require-corepack-invocation]',
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
    case 'android':
      return new Set([
        'node',
        'corepack',
        'pnpm',
        'cargo',
        'rustc',
        'java',
        'androidSdk',
        'androidNdk',
        'androidRustTargets',
      ]);
    case 'full':
      return new Set(['node', 'corepack', 'pnpm', 'dotnet', 'cargo', 'rustc']);
    case 'release':
      return new Set([
        'node',
        'corepack',
        'pnpm',
        'dotnet',
        'cargo',
        'rustc',
        'powershell',
        'githubCli',
      ]);
    case 'release-tools':
      return new Set(['node', 'powershell', 'githubCli']);
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

function validateLockedRevision(label, value) {
  if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$/u.test(value ?? '')) {
    failures.push(`${label} must be an exact canonical revision; received ${describe(value)}.`);
  }
}

function validatePositiveInteger(label, value) {
  if (!Number.isInteger(value) || value <= 0) {
    failures.push(`${label} must be a positive integer; received ${describe(value)}.`);
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

function expectGradleInteger(label, source, key, expected) {
  const escapedKey = key.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const match = new RegExp(`^\\s*${escapedKey}\\s*=\\s*(\\d+)\\s*$`, 'mu').exec(source);
  const actual = match ? Number(match[1]) : undefined;
  expectEqual(label, actual, expected);
}

function expectGradleString(label, source, key, expected) {
  const escapedKey = key.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const match = new RegExp(`^\\s*${escapedKey}\\s*=\\s*"([^"]+)"\\s*$`, 'mu').exec(source);
  expectEqual(label, match?.[1], expected);
}

function expectGradleWrapperProperty(label, source, key, expected) {
  const escapedKey = key.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const raw = new RegExp(`^${escapedKey}=(.+?)\\s*$`, 'mu').exec(source)?.[1];
  expectEqual(label, raw?.replaceAll('\\:', ':'), expected);
}

function expectTemurinJdk(androidToolchain) {
  const result = spawnSync('java', ['-XshowSettings:properties', '-version'], {
    cwd: repoRoot,
    encoding: 'utf8',
    windowsHide: true,
  });
  if (result.error) {
    failures.push(`installed Android JDK could not run java: ${result.error.message}`);
    return;
  }
  if (result.status !== 0) {
    const diagnostic = [result.stderr, result.stdout].map((value) => value?.trim()).filter(Boolean).join(' | ');
    failures.push(`installed Android JDK command exited with ${result.status}${diagnostic ? `: ${diagnostic}` : ''}`);
    return;
  }

  const output = [result.stdout, result.stderr].filter(Boolean).join('\n');
  const runtimeVersion = /^\s*java\.runtime\.version\s*=\s*(\S+)\s*$/mu.exec(output)?.[1];
  const vendor = /^\s*java\.vendor\s*=\s*(.+?)\s*$/mu.exec(output)?.[1];
  const javaHome = /^\s*java\.home\s*=\s*(.+?)\s*$/mu.exec(output)?.[1];
  if (!runtimeVersion) {
    failures.push('installed Android JDK did not report java.runtime.version.');
  } else {
    const escapedVersion = androidToolchain.jdkVersion.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
    if (!new RegExp(`^${escapedVersion}(?:$|[+_-])`, 'u').test(runtimeVersion)) {
      failures.push(`installed Android JDK version must be ${describe(androidToolchain.jdkVersion)}; received ${describe(runtimeVersion)}.`);
    }
  }
  expectEqual('installed Android JDK vendor', vendor, androidToolchain.jdkVendor);

  const configuredJavaHome = process.env.JAVA_HOME?.trim();
  if (!configuredJavaHome) {
    failures.push('JAVA_HOME must point to the locked Android Temurin JDK.');
  } else if (javaHome) {
    expectSameExistingPath('JAVA_HOME', configuredJavaHome, javaHome);
  }
}

function expectAndroidSdk(androidToolchain) {
  const androidHome = process.env.ANDROID_HOME?.trim();
  if (!androidHome) {
    failures.push('ANDROID_HOME must point to the locked Android SDK.');
    return;
  }

  const sdkRootAlias = process.env.ANDROID_SDK_ROOT?.trim();
  if (sdkRootAlias) {
    expectSameExistingPath('ANDROID_SDK_ROOT', sdkRootAlias, androidHome);
  }

  expectRequiredFile(
    'Android platform android.jar',
    path.join(androidHome, 'platforms', `android-${androidToolchain.compileSdk}`, 'android.jar'),
  );
  const buildToolsRoot = path.join(androidHome, 'build-tools', androidToolchain.buildTools);
  expectRequiredFile('Android Build Tools source.properties', path.join(buildToolsRoot, 'source.properties'));
  expectRequiredFile('Android Build Tools apksigner.jar', path.join(buildToolsRoot, 'lib', 'apksigner.jar'));
  expectRequiredFile(
    'Android Build Tools aapt2',
    path.join(buildToolsRoot, process.platform === 'win32' ? 'aapt2.exe' : 'aapt2'),
  );
  expectPropertiesValue(
    'Android Build Tools package revision',
    path.join(buildToolsRoot, 'source.properties'),
    'Pkg.Revision',
    androidToolchain.buildTools,
  );
}

function expectAndroidNdk(androidToolchain) {
  const androidHome = process.env.ANDROID_HOME?.trim();
  if (!androidHome) return;

  const expectedNdkHome = path.join(androidHome, 'ndk', androidToolchain.ndkPackage);
  const configuredNdkHome = process.env.NDK_HOME?.trim();
  if (!configuredNdkHome) {
    failures.push('NDK_HOME must point to the locked Android NDK.');
  } else {
    expectSameExistingPath('NDK_HOME', configuredNdkHome, expectedNdkHome);
  }

  for (const alias of ['ANDROID_NDK', 'ANDROID_NDK_HOME', 'ANDROID_NDK_ROOT']) {
    const configuredAlias = process.env[alias]?.trim();
    if (configuredAlias) {
      expectSameExistingPath(alias, configuredAlias, expectedNdkHome);
    }
  }

  const sourceProperties = path.join(expectedNdkHome, 'source.properties');
  expectRequiredFile('Android NDK source.properties', sourceProperties);
  expectPropertiesValue(
    'Android NDK package revision',
    sourceProperties,
    'Pkg.Revision',
    androidToolchain.ndkRevision,
  );
}

function expectAndroidRustTargets(androidToolchain) {
  const result = spawnSync(
    'rustup',
    ['target', 'list', '--installed', '--toolchain', toolchain.rust],
    {
      cwd: repoRoot,
      encoding: 'utf8',
      windowsHide: true,
    },
  );
  if (result.error) {
    failures.push(`installed Android Rust targets could not be queried: ${result.error.message}`);
    return;
  }
  if (result.status !== 0) {
    const diagnostic = [result.stderr, result.stdout]
      .map((value) => value?.trim())
      .filter(Boolean)
      .join(' | ');
    failures.push(`rustup target list exited with ${result.status}${diagnostic ? `: ${diagnostic}` : ''}`);
    return;
  }

  const installed = new Set(result.stdout.split(/\r?\n/u).filter(Boolean));
  for (const target of androidToolchain.rustTargets) {
    if (!installed.has(target)) failures.push(`Android Rust target is not installed: ${target}`);
  }
}

function expectSameExistingPath(label, actualPath, expectedPath) {
  if (!existsSync(actualPath)) {
    failures.push(`${label} path does not exist: ${actualPath}`);
    return;
  }
  if (!existsSync(expectedPath)) {
    failures.push(`${label} expected path does not exist: ${expectedPath}`);
    return;
  }

  const actual = normalizePath(realpathSync.native(actualPath));
  const expected = normalizePath(realpathSync.native(expectedPath));
  if (actual !== expected) {
    failures.push(`${label} must resolve to ${describe(expectedPath)}; received ${describe(actualPath)}.`);
  }
}

function normalizePath(value) {
  return process.platform === 'win32' ? value.toLowerCase() : value;
}

function expectRequiredFile(label, filePath) {
  if (!existsSync(filePath)) {
    failures.push(`${label} was not found: ${filePath}`);
  }
}

function expectPropertiesValue(label, filePath, key, expected) {
  if (!existsSync(filePath)) return;
  const source = readFileSync(filePath, 'utf8');
  const escapedKey = key.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
  const actual = new RegExp(`^\\s*${escapedKey}\\s*=\\s*(.+?)\\s*$`, 'mu').exec(source)?.[1];
  expectEqual(label, actual, expected);
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

function powerShellOutput(output) {
  return /^PowerShell (\d+\.\d+\.\d+)$/u.exec(output)?.[1] ?? null;
}

function githubCliOutput(output) {
  return /^gh version (\d+\.\d+\.\d+)(?:\s|$)/u.exec(output)?.[1] ?? null;
}

function describe(value) {
  return value === undefined ? '<missing>' : JSON.stringify(value);
}
