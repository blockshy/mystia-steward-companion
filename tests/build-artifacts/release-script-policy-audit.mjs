import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const sources = Object.fromEntries(await Promise.all([
  ['build', 'mods/bepinex/tools/build-release.ps1'],
  ['packagePowerShell', 'mods/bepinex/tools/package-release.ps1'],
  ['packageBash', 'mods/bepinex/tools/package-release.sh'],
  ['publish', 'mods/bepinex/tools/publish-release.ps1'],
  ['android', 'scripts/build-android-signed-apk.mjs'],
  ['tauriApp', 'apps/companion/src-tauri/src/app.rs'],
  ['packageJson', 'package.json'],
].map(async ([name, relativePath]) => [name, await readFile(path.join(repoRoot, relativePath), 'utf8')])));

const packageScripts = JSON.parse(sources.packageJson).scripts;
for (const scriptName of [
  'tauri:dev',
  'tauri:build',
  'tauri:android:dev',
  'tauri:android:build',
  'tauri:android:apk',
]) {
  assert.match(
    packageScripts[scriptName],
    /^node scripts\/check-build-toolchain\.mjs tauri --require-corepack-invocation && node scripts\/manage-build-artifacts\.mjs prune && /u,
    `${scriptName} does not enforce the toolchain and cache policies before building.`,
  );
}

assert.match(sources.publish, /if \(\$SkipBuild -and \$BuildAndroidApk\)/u);
assert.match(sources.publish, /if \(\$BuildAndroidApk -and -not \[string\]::IsNullOrWhiteSpace\(\$AndroidApkPath\)\)/u);
assert.match(sources.publish, /Get-StaleCanonicalAndroidAssets/u);
assert.match(sources.publish, /release",\s*"delete-asset"/u);
assert.match(sources.publish, /StaleCanonicalAndroidAssets\.Count -gt 0 -and -not \$Clobber/u);
assert.match(sources.publish, /update-catalog\.json/u);
assert.match(sources.publish, /generate-update-catalog\.mjs/u);
assert.match(sources.publish, /catalogSha256/u);
assert.match(sources.publish, /catalogSize/u);
assert.match(sources.publish, /Release notes are required/u);
assert.doesNotMatch(sources.publish, /Built locally and uploaded with GitHub CLI/u);

const prebuildPruneCondition = sources.build.match(
  /if \(-not \$SkipBuildCacheCleanup[^\n]+\) \{\s*Invoke-BuildCachePrune -Title "Prune stale build artifacts before compilation"/u,
)?.[0] ?? '';
assert.ok(prebuildPruneCondition, 'Missing prebuild Tauri cache prune condition.');
assert.match(prebuildPruneCondition, /-not \$SkipTauriBuild/u);
assert.doesNotMatch(prebuildPruneCondition, /SkipFrontendBuild/u);

assert.match(sources.packagePowerShell, /function Assert-NoPendingReleaseTransactions/u);
assert.match(sources.packagePowerShell, /\^dist\\\.\(staging\|backup\)-/u);
assert.match(sources.packageBash, /assert_no_pending_release_transactions/u);
assert.match(sources.packageBash, /cleanup_transaction 130/u);
assert.match(sources.packageBash, /cleanup_transaction 143/u);

assert.match(sources.android, /assertRealDirectory\(distDir, 'Android release dist'\)/u);
assert.match(sources.android, /const stagingDir = mkdtempSync\(path\.join\(distDir, '\.android-apk-stage-'\)\)/u);
const androidMainBody = sources.android.match(/function main\(\) \{[\s\S]+?\n\}/u)?.[0] ?? '';
assert.ok(androidMainBody, 'Missing signed Android APK main function.');
assert.ok(
  androidMainBody.indexOf('verifyBuildToolchain();') < androidMainBody.indexOf('mkdirSync(distDir'),
  'Signed Android APK builds do not verify the locked toolchain before writing build artifacts.',
);
assert.ok(
  androidMainBody.indexOf('pruneBuildArtifacts();') < androidMainBody.indexOf('runTauriAndroidApkBuild();'),
  'Signed Android APK builds do not prune stale caches before compiling.',
);
assert.match(sources.android, /check-build-toolchain\.mjs/u);
assert.match(sources.android, /\[buildToolchainCheck, 'tauri'\]/u);
for (const helperName of ['is_main_window_focused', 'hide_main_window']) {
  const definitions = [...sources.tauriApp.matchAll(new RegExp(
    `(?<attributes>(?:#\\[cfg\\([^\\n]+\\)\\]\\s*)*)fn ${helperName}\\(`,
    'gu',
  ))];
  assert.equal(definitions.length, 1, `${helperName} must have one canonical implementation.`);
  assert.match(
    definitions[0].groups?.attributes ?? '',
    /#\[cfg\(desktop\)\]/u,
    `${helperName} must remain excluded from Android and other mobile targets.`,
  );
}
const pruneThenCommitBody = sources.android.match(
  /function pruneThenCommitAndroidApks[\s\S]+?\n\}/u,
)?.[0] ?? '';
assert.ok(pruneThenCommitBody, 'Missing Android prune-then-commit transaction function.');
assert.ok(
  pruneThenCommitBody.indexOf('prune();') < pruneThenCommitBody.indexOf('commitStagedAndroidApks'),
  'Android APK assets are committed before build artifact pruning.',
);

console.log('Release script policy audit passed.');
