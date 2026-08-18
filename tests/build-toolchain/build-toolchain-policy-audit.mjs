import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const toolchain = JSON.parse(await readFile(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
const packageJson = JSON.parse(await readFile(path.join(repoRoot, 'package.json'), 'utf8'));
const [
  gitAttributes,
  androidGradle,
  androidGradleWrapper,
  buildToolchainChecker,
  lockedCorepackInstaller,
  lockedReleaseToolsInstaller,
  buildRelease,
  ciWorkflow,
  dotnet6Runner,
  analysisGenerator,
  androidApkBuilder,
  preflightPowerShell,
  preflightBash,
] = await Promise.all([
  readFile(path.join(repoRoot, '.gitattributes'), 'utf8'),
  readFile(path.join(repoRoot, 'apps/companion/src-tauri/gen/android/app/build.gradle.kts'), 'utf8'),
  readFile(
    path.join(repoRoot, 'apps/companion/src-tauri/gen/android/gradle/wrapper/gradle-wrapper.properties'),
    'utf8',
  ),
  readFile(path.join(repoRoot, 'scripts/check-build-toolchain.mjs'), 'utf8'),
  readFile(path.join(repoRoot, 'scripts/install-locked-corepack.mjs'), 'utf8'),
  readFile(path.join(repoRoot, 'scripts/install-locked-release-tools.mjs'), 'utf8'),
  readFile(path.join(repoRoot, 'mods/bepinex/tools/build-release.ps1'), 'utf8'),
  readFile(path.join(repoRoot, '.github/workflows/ci.yml'), 'utf8'),
  readFile(path.join(repoRoot, 'scripts/run-dotnet6-harmony-smoke.mjs'), 'utf8'),
  readFile(path.join(repoRoot, 'mods/bepinex/tools/il2cpp-analysis/generate-analysis.sh'), 'utf8'),
  readFile(path.join(repoRoot, 'scripts/build-android-signed-apk.mjs'), 'utf8'),
  readFile(path.join(repoRoot, 'mods/bepinex/tools/preflight.ps1'), 'utf8'),
  readFile(path.join(repoRoot, 'mods/bepinex/tools/preflight.sh'), 'utf8'),
]);

assert.match(
  gitAttributes,
  /^apps\/companion\/src-tauri\/Cargo\.toml text eol=lf$/mu,
  'Tauri Cargo.toml must remain LF on Windows because the Tauri CLI rewrites the manifest.',
);

const policyCheck = spawnSync(
  process.execPath,
  [path.join(repoRoot, 'scripts/check-build-toolchain.mjs'), '--policy-only'],
  { cwd: repoRoot, encoding: 'utf8' },
);
assert.equal(
  policyCheck.status,
  0,
  `Toolchain policy projections drifted:\n${policyCheck.stderr || policyCheck.stdout}`,
);

const globalPackageManagerCheck = spawnSync(
  process.execPath,
  [
    path.join(repoRoot, 'scripts/check-build-toolchain.mjs'),
    'frontend',
    '--require-corepack-invocation',
  ],
  {
    cwd: repoRoot,
    encoding: 'utf8',
    env: { ...process.env, COREPACK_ROOT: '' },
  },
);
assert.notEqual(globalPackageManagerCheck.status, 0, 'A global package-manager invocation was accepted.');
assert.match(globalPackageManagerCheck.stderr, /must be invoked through Corepack/u);

assert.equal(packageJson.scripts['toolchain:check'], 'node scripts/check-build-toolchain.mjs full');
assert.equal(packageJson.scripts['test:dotnet6-harmony'], 'node scripts/run-dotnet6-harmony-smoke.mjs all');
assert.equal(toolchain.schemaVersion, 3);
assert.equal(
  toolchain.corepackIntegrity,
  'sha512-9BuIGHDFE7Zieor1CeRsvt7X7AJFEuJ6OnbSbsVprq83ChDFoBh1wP98NeUS9FT3ZwlzFllPElXcz/OiDf0YGw==',
);
assert.equal(toolchain.powershell, '7.6.4');
assert.equal(toolchain.githubCli, '2.97.0');
assert.deepEqual(toolchain.releaseToolArchives, {
  'linux-x64': {
    powershell: {
      url: 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.4/powershell-7.6.4-linux-x64.tar.gz',
      size: 77628778,
      sha256: '4471b5a36bfe86ec7af8525d36bb1cacba0128e7aac22d05cc064bc00e604721',
    },
    githubCli: {
      url: 'https://github.com/cli/cli/releases/download/v2.97.0/gh_2.97.0_linux_amd64.tar.gz',
      size: 14770812,
      sha256: 'a2c9b8497e1f85b1ad0dfcb78b5a622e098801b8e461e459e88e1ee12f018112',
    },
  },
  'win32-x64': {
    powershell: {
      url: 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.4/PowerShell-7.6.4-win-x64.zip',
      size: 116979293,
      sha256: '80832551c52809301e6071c8bac977beb5a2f1ec953eb4db9f94deb953333793',
    },
    githubCli: {
      url: 'https://github.com/cli/cli/releases/download/v2.97.0/gh_2.97.0_windows_amd64.zip',
      size: 14938517,
      sha256: '35d7fe05c4dd1411ffda1e73dfc7c6f44b75c936ca51fa6595c657fdc0350cec',
    },
  },
});
assert.deepEqual(toolchain.android, {
  jdkDistribution: 'temurin',
  jdkVendor: 'Eclipse Adoptium',
  jdkVersion: '21.0.4',
  compileSdk: 36,
  targetSdk: 36,
  gradle: '8.14.3',
  gradleDistributionSha256: 'bd71102213493060956ec229d946beee57158dbd89d0e62b91bca0fa2c5f3531',
  buildTools: '35.0.0',
  ndkPackage: '30.0.14904198',
  ndkRevision: '30.0.14904198-beta1',
  rustTargets: ['aarch64-linux-android', 'armv7-linux-androideabi'],
  signingCertificateSha256: '1540b609d5cd54e06a8429bb0aaa2cc4b511e055565fdac93acf206c1791d1fb',
});
assert.match(
  buildToolchainChecker,
  /path\.join\(androidHome, 'ndk', androidToolchain\.ndkPackage\)/u,
  'The Android NDK install path must use the SDK package coordinate.',
);
assert.match(
  buildToolchainChecker,
  /'Pkg\.Revision',[\s\S]*androidToolchain\.ndkRevision/u,
  'The Android NDK source.properties identity must use the exact package revision.',
);
assert.doesNotMatch(
  buildToolchainChecker,
  /androidToolchain\.ndk(?!Package|Revision)/u,
  'The ambiguous legacy Android NDK lock field must not be restored.',
);
assert.match(androidGradle, /^\s*buildToolsVersion = "35\.0\.0"\s*$/mu);
assert.match(
  androidGradleWrapper,
  /^distributionUrl=https\\:\/\/services\.gradle\.org\/distributions\/gradle-8\.14\.3-bin\.zip$/mu,
);
assert.match(
  androidGradleWrapper,
  /^distributionSha256Sum=bd71102213493060956ec229d946beee57158dbd89d0e62b91bca0fa2c5f3531$/mu,
);
assert.match(androidApkBuilder, /process\.platform === 'win32' \? 'aapt2\.exe' : 'aapt2'/u);
assert.match(androidApkBuilder, /findAndroidBuildToolFile\(executableName\)/u);
assert.match(androidApkBuilder, /\['dump', 'badging', apkPath\]/u);
assert.match(lockedCorepackInstaller, /createHash\('sha512'\)/u);
assert.match(lockedCorepackInstaller, /actualIntegrity !== expectedIntegrity/u);
assert.match(lockedCorepackInstaller, /'--ignore-scripts'/u);
assert.match(lockedCorepackInstaller, /process\.platform === 'win32'/u);
assert.match(lockedCorepackInstaller, /'node_modules', 'npm', 'bin', 'npm-cli\.js'/u);
assert.match(lockedCorepackInstaller, /run\(process\.execPath, \[npmCliPath/u);
assert.doesNotMatch(lockedCorepackInstaller, /run\('npm'/u);
assert.match(lockedCorepackInstaller, /--install-root <new-directory>/u);
assert.match(lockedCorepackInstaller, /'--prefix',\s*transactionRoot/u);
assert.match(lockedCorepackInstaller, /\.stage-\$\{randomUUID\(\)\}/u);
assert.match(lockedCorepackInstaller, /renameSync\(transactionRoot, installRoot\)/u);
assert.match(lockedCorepackInstaller, /process\.env\.GITHUB_PATH/u);
assert.match(lockedCorepackInstaller, /'corepack\.cmd'/u);
assert.match(lockedCorepackInstaller, /'pnpm\.cmd'/u);
assert.doesNotMatch(lockedCorepackInstaller, /['"]--force['"]|corepack enable/u);
assert.match(lockedReleaseToolsInstaller, /--install-root <new-directory>/u);
assert.match(lockedReleaseToolsInstaller, /process\.arch !== 'x64'/u);
assert.match(lockedReleaseToolsInstaller, /!\['linux', 'win32'\]\.includes\(process\.platform\)/u);
assert.match(lockedReleaseToolsInstaller, /assertExactKeys\(lock\.releaseToolArchives, \['linux-x64', 'win32-x64'\]/u);
assert.match(lockedReleaseToolsInstaller, /parsedUrl\.protocol !== 'https:'/u);
assert.match(lockedReleaseToolsInstaller, /parsedUrl\.hostname !== 'github\.com'/u);
assert.match(lockedReleaseToolsInstaller, /createWriteStream\(destination, \{ flags: 'wx', mode: 0o600 \}\)/u);
assert.match(lockedReleaseToolsInstaller, /await pipeline\(response, verifier, output\)/u);
assert.match(lockedReleaseToolsInstaller, /const hash = createHash\('sha256'\)/u);
assert.match(lockedReleaseToolsInstaller, /received > record\.size/u);
assert.match(lockedReleaseToolsInstaller, /stats\.size !== record\.size/u);
assert.match(lockedReleaseToolsInstaller, /digest !== record\.sha256/u);
assert.match(lockedReleaseToolsInstaller, /tar: '\/usr\/bin\/tar'/u);
assert.match(lockedReleaseToolsInstaller, /path\.join\(systemRoot, 'System32', 'tar\.exe'\)/u);
assert.match(
  lockedReleaseToolsInstaller,
  /'linux-x64':[\s\S]*?githubCli: \{[\s\S]*?payloadRoot: `gh_\$\{lock\.githubCli\}_linux_amd64`[\s\S]*?executablePath: 'bin\/gh'/u,
  'Linux GitHub CLI tarball must retain its versioned top-level payload directory.',
);
assert.match(
  lockedReleaseToolsInstaller,
  /'win32-x64':[\s\S]*?githubCli: \{[\s\S]*?payloadRoot: '\.'[\s\S]*?executablePath: 'bin\/gh\.exe'/u,
  'Windows GitHub CLI ZIP must use its root-level bin/gh.exe payload layout.',
);
assert.match(lockedReleaseToolsInstaller, /spawnSync\(executable, args,[\s\S]*shell: false/u);
assert.doesNotMatch(lockedReleaseToolsInstaller, /execSync|execFileSync|shell: true|Expand-Archive/u);
assert.match(lockedReleaseToolsInstaller, /assertExecutable\(extractedExecutable/u);
assert.match(lockedReleaseToolsInstaller, /stats\.isFile\(\)[\s\S]*stats\.isSymbolicLink\(\)[\s\S]*stats\.size <= 0/u);
assert.match(lockedReleaseToolsInstaller, /mkdirSync\(installRoot, \{ mode: 0o700 \}\)/u);
assert.match(lockedReleaseToolsInstaller, /assertRealDirectory\(installRoot, 'release-tools install root'\)/u);
assert.match(lockedReleaseToolsInstaller, /const destination = path\.join\(installRoot, toolPolicy\.installDirectory\)/u);
assert.match(lockedReleaseToolsInstaller, /installCreated && \(!installReady \|\| cleanupErrors\.length > 0\)/u);
assert.match(lockedReleaseToolsInstaller, /removeOwnedDirectory\(temporaryRoot,[\s\S]*removeOwnedDirectory\(installRoot/u);
assert.match(lockedReleaseToolsInstaller, /maxRetries: 5,[\s\S]*retryDelay: 100/u);
assert.match(lockedReleaseToolsInstaller, /throw new AggregateError\(errors, message\)/u);
assert.doesNotMatch(lockedReleaseToolsInstaller, /randomUUID|renameSync|transactionRoot/u);
assert.match(lockedReleaseToolsInstaller, /install root already exists/u);
assert.match(lockedReleaseToolsInstaller, /process\.env\.GITHUB_PATH/u);
assert.match(lockedReleaseToolsInstaller, /appendFileSync\(githubPath, `\$\{directories\.join\('\\n'\)\}\\n`/u);
const releaseToolsPowerShellCheckIndex = lockedReleaseToolsInstaller.indexOf('installedPaths.powershell,');
const releaseToolsGitHubCliCheckIndex = lockedReleaseToolsInstaller.indexOf('installedPaths.githubCli,');
const releaseToolsPathPublishIndex = lockedReleaseToolsInstaller.indexOf('writeGitHubPath(pathDirectories);');
const releaseToolsReadyIndex = lockedReleaseToolsInstaller.indexOf('installReady = true;');
const releaseToolsCleanupIndex = lockedReleaseToolsInstaller.indexOf(
  "removeOwnedDirectory(temporaryRoot, 'temporary release-tools workspace', cleanupErrors);",
);
assert.ok(releaseToolsPowerShellCheckIndex >= 0);
assert.ok(releaseToolsGitHubCliCheckIndex > releaseToolsPowerShellCheckIndex);
assert.ok(releaseToolsPathPublishIndex > releaseToolsGitHubCliCheckIndex);
assert.ok(releaseToolsReadyIndex > releaseToolsPathPublishIndex);
assert.ok(releaseToolsCleanupIndex > releaseToolsReadyIndex);

const releaseToolsUsageCheck = spawnSync(
  process.execPath,
  [path.join(repoRoot, 'scripts/install-locked-release-tools.mjs')],
  { cwd: repoRoot, encoding: 'utf8' },
);
assert.notEqual(releaseToolsUsageCheck.status, 0, 'Release-tool installer accepted a missing install root.');
assert.match(releaseToolsUsageCheck.stderr, /--install-root <new-directory>/u);

const releaseToolsExistingRootCheck = spawnSync(
  process.execPath,
  [
    path.join(repoRoot, 'scripts/install-locked-release-tools.mjs'),
    '--install-root',
    repoRoot,
  ],
  { cwd: repoRoot, encoding: 'utf8' },
);
assert.notEqual(releaseToolsExistingRootCheck.status, 0, 'Release-tool installer accepted an existing root.');
assert.match(releaseToolsExistingRootCheck.stderr, /install root already exists/u);

const corepackUsageCheck = spawnSync(
  process.execPath,
  [path.join(repoRoot, 'scripts/install-locked-corepack.mjs')],
  { cwd: repoRoot, encoding: 'utf8' },
);
assert.notEqual(corepackUsageCheck.status, 0, 'Corepack installer accepted a missing install root.');
assert.match(corepackUsageCheck.stderr, /--install-root <new-directory>/u);

const corepackExistingRootCheck = spawnSync(
  process.execPath,
  [
    path.join(repoRoot, 'scripts/install-locked-corepack.mjs'),
    '--install-root',
    repoRoot,
  ],
  { cwd: repoRoot, encoding: 'utf8' },
);
assert.notEqual(corepackExistingRootCheck.status, 0, 'Corepack installer accepted an existing root.');
assert.match(corepackExistingRootCheck.stderr, /install root already exists/u);
assert.match(
  packageJson.scripts.build,
  /^node scripts\/check-build-toolchain\.mjs frontend --require-corepack-invocation && /u,
);
assert.match(
  packageJson.scripts.lint,
  /^node scripts\/check-build-toolchain\.mjs frontend --require-corepack-invocation && /u,
);

for (const scriptName of [
  'tauri:dev',
  'tauri:build',
]) {
  assert.match(
    packageJson.scripts[scriptName],
    /^node scripts\/check-build-toolchain\.mjs tauri --require-corepack-invocation && /u,
  );
}

for (const scriptName of ['tauri:android:dev', 'tauri:android:build', 'tauri:android:apk']) {
  assert.match(
    packageJson.scripts[scriptName],
    /^node scripts\/check-build-toolchain\.mjs android --require-corepack-invocation && /u,
  );
}

assert.match(buildRelease, /Join-Path \$RepoRoot "scripts\/check-build-toolchain\.mjs"/u);
assert.match(buildRelease, /-Title "Validate locked build toolchain"/u);
assert.match(buildRelease, /-Arguments @\(\$ToolchainCheckScript, "full"\)/u);
assert.doesNotMatch(buildRelease, /Get-Command "pnpm"/u);
assert.match(buildRelease, /Get-Command "corepack"/u);

assert.match(ciWorkflow, /node-version-file: \.nvmrc/u);
assert.match(
  ciWorkflow,
  /node scripts\/install-locked-corepack\.mjs --install-root "\$RUNNER_TEMP\/mystia-corepack"/u,
);
assert.doesNotMatch(ciWorkflow, /corepack enable/u);
assert.match(ciWorkflow, /node scripts\/check-build-toolchain\.mjs frontend/u);

for (const smokeName of [
  'automation-cooking-job',
  'ui-pinning-runtime',
  'runtime-target-recipe-variant',
]) {
  assert.match(dotnet6Runner, new RegExp(`'${escapeRegex(smokeName)}'`, 'u'));
}
assert.match(dotnet6Runner, /target=\/workspace\/global\.json,readonly/u);
assert.doesNotMatch(dotnet6Runner, /mcr\.microsoft\.com\/dotnet\/sdk@sha256:/u);

assert.match(analysisGenerator, /build_toolchain_lock="\$repo_root\/toolchain\.lock\.json"/u);
assert.match(analysisGenerator, /dotnet_version=\$\(cd "\$repo_root" && dotnet --version\)/u);
assert.match(analysisGenerator, /"\$dotnet_version" != "\$expected_dotnet_version"/u);

assert.match(preflightPowerShell, /toolchain\.lock\.json/u);
assert.match(preflightPowerShell, /\$ActualDotnetSdk -cne \$ExpectedDotnetSdk/u);
assert.match(preflightBash, /toolchain\.lock\.json/u);
assert.match(preflightBash, /"\$ACTUAL_DOTNET_SDK" == "\$EXPECTED_DOTNET_SDK"/u);

console.log('Build toolchain policy audit passed.');

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}
