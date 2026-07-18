import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import {
  lstat,
  mkdir,
  mkdtemp,
  readFile,
  realpath,
  readdir,
  rm,
  symlink,
  truncate,
  utimes,
  writeFile,
} from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  FIXTURE_SENTINEL,
  FIXTURE_SENTINEL_CONTENT,
  lockPathForRoot,
} from '../../scripts/manage-build-artifacts.mjs';

const root = fileURLToPath(new URL('../..', import.meta.url));
const cliPath = path.join(root, 'scripts', 'manage-build-artifacts.mjs');
const MIB = 1024 ** 2;
const GIB = 1024 ** 3;

await auditReportDryRunCleanAndProtection();
await auditQuotaPruneUsesWholeOldestBuckets();
await auditCategoryQuotasApplyBelowGlobalLimit();
await auditExpiredBucketRemovalBelowQuota();
await auditPruneEnvironmentGate();
await auditFixtureRootSafety();
await auditPolicyValidation();
await auditSymlinkRefusal();
await auditExclusiveLock();

console.log('Build artifact storage audit passed.');

async function auditReportDryRunCleanAndProtection() {
  await withFixture(async (fixtureRoot) => {
    const managedPaths = [
      'apps/companion/src-tauri/target/debug/cache.bin',
      'apps/companion/src-tauri/gen/android/.gradle/cache.bin',
      'apps/companion/src-tauri/gen/android/app/build/output.apk',
      'apps/companion/src-tauri/gen/android/buildSrc/build/plugin.jar',
      'apps/companion/dist/index.html',
      'apps/companion/.vite/cache.bin',
      'mods/bepinex/bin/Release/plugin.dll',
      'mods/bepinex/obj/project.assets.json',
    ];
    await writeFixtureFile(fixtureRoot, 'mods/bepinex/MystiaStewardCompanion.BepInEx.csproj', 128);
    for (const relativePath of managedPaths) {
      await writeFixtureFile(fixtureRoot, relativePath, 1024);
    }

    const protectedFiles = new Map([
      ['mods/bepinex/dist/release.zip', 'release'],
      ['mods/bepinex/References/GameAssembly.dll', 'reference'],
      ['temp/user-log.txt', 'user data'],
      ['node_modules/example/index.js', 'dependency'],
      ['.playwright-cli/chromium/browser', 'browser'],
      ['apps/companion/src-tauri/gen/android/keystore.properties', 'secret'],
      ['apps/companion/src-tauri/gen/android/release.jks', 'signing'],
    ]);
    for (const [relativePath, content] of protectedFiles) {
      await writeTextFile(fixtureRoot, relativePath, content);
    }

    const report = runCli(fixtureRoot, 'report');
    assertSuccess(report, 'report');
    assert.match(report.stdout, /Managed build artifacts: 8 bucket\(s\)/);
    assert.match(report.stdout, /apps\/companion\/src-tauri\/target\/debug/);
    assert.doesNotMatch(report.stdout, /mods\/bepinex\/dist/);
    for (const relativePath of managedPaths) {
      assert.equal(await exists(path.join(fixtureRoot, relativePath)), true, `Report changed ${relativePath}.`);
    }

    const dryRun = runCli(fixtureRoot, 'clean', ['--dry-run']);
    assertSuccess(dryRun, 'clean --dry-run');
    assert.match(dryRun.stdout, /Would remove \[clean\]/);
    for (const relativePath of managedPaths) {
      assert.equal(await exists(path.join(fixtureRoot, relativePath)), true, `Dry-run changed ${relativePath}.`);
    }

    const packageManagerDryRun = runCli(fixtureRoot, 'clean', ['--', '--dry-run']);
    assertSuccess(packageManagerDryRun, 'clean -- --dry-run');
    assert.match(packageManagerDryRun.stdout, /Would remove \[clean\]/);
    for (const relativePath of managedPaths) {
      assert.equal(await exists(path.join(fixtureRoot, relativePath)), true, `Package-manager dry-run changed ${relativePath}.`);
    }

    const clean = runCli(fixtureRoot, 'clean');
    assertSuccess(clean, 'clean');
    assert.match(clean.stdout, /Artifact clean removed 8 bucket\(s\)/);
    for (const relativePath of managedPaths) {
      assert.equal(await exists(path.join(fixtureRoot, relativePath)), false, `Clean retained ${relativePath}.`);
    }
    for (const [relativePath, content] of protectedFiles) {
      assert.equal(await readFile(path.join(fixtureRoot, relativePath), 'utf8'), content);
    }
  });
}

async function auditQuotaPruneUsesWholeOldestBuckets() {
  await withFixture(async (fixtureRoot) => {
    const oldestBucket = 'apps/companion/src-tauri/target/debug';
    const middleBucket = 'apps/companion/src-tauri/gen/android/app/build';
    const newestBucket = 'apps/companion/src-tauri/target/release';
    await writeFixtureFile(fixtureRoot, `${oldestBucket}/old.bin`, 2 * MIB);
    await writeFixtureFile(fixtureRoot, `${middleBucket}/middle.bin`, 2 * MIB);
    await writeFixtureFile(fixtureRoot, `${newestBucket}/new.bin`, MIB);
    await setTreeAge(path.join(fixtureRoot, oldestBucket), 10);
    await setTreeAge(path.join(fixtureRoot, middleBucket), 5);
    await setTreeAge(path.join(fixtureRoot, newestBucket), 1);

    const quotaOptions = [
      '--limit-gib', String((4.5 * MIB) / GIB),
      '--target-gib', String((1.5 * MIB) / GIB),
      '--max-age-days', '999',
    ];
    const dryRun = runCli(fixtureRoot, 'prune', [...quotaOptions, '--dry-run']);
    assertSuccess(dryRun, 'quota prune --dry-run');
    assert.match(dryRun.stdout, /Would remove \[quota\].*target\/debug/);
    assert.match(dryRun.stdout, /Would remove \[quota\].*android\/app\/build/);
    assert.doesNotMatch(dryRun.stdout, /Would remove \[quota\].*target\/release/);
    assert.equal(await exists(path.join(fixtureRoot, oldestBucket)), true);
    assert.equal(await exists(path.join(fixtureRoot, middleBucket)), true);

    const prune = runCli(fixtureRoot, 'prune', quotaOptions);
    assertSuccess(prune, 'quota prune');
    assert.equal(await exists(path.join(fixtureRoot, oldestBucket)), false);
    assert.equal(await exists(path.join(fixtureRoot, middleBucket)), false);
    assert.equal(await exists(path.join(fixtureRoot, newestBucket)), true);
    assert.equal(await exists(path.join(fixtureRoot, `${newestBucket}/new.bin`)), true);
  });
}

async function auditExpiredBucketRemovalBelowQuota() {
  await withFixture(async (fixtureRoot) => {
    await writeTextFile(fixtureRoot, 'tests/example/Example.csproj', '<Project />');
    const expiredOutput = 'tests/example/bin/Debug/example.dll';
    await writeFixtureFile(fixtureRoot, expiredOutput, 1024);
    await setTreeAge(path.join(fixtureRoot, 'tests/example/bin'), 20);

    const prune = runCli(fixtureRoot, 'prune', [
      '--limit-gib', '10',
      '--target-gib', '8',
      '--max-age-days', '14',
    ]);
    assertSuccess(prune, 'expired prune');
    assert.match(prune.stdout, /Removing \[expired\].*tests\/example\/bin/);
    assert.equal(await exists(path.join(fixtureRoot, expiredOutput)), false);
    assert.equal(await exists(path.join(fixtureRoot, 'tests/example/Example.csproj')), true);
  });
}

async function auditCategoryQuotasApplyBelowGlobalLimit() {
  await withFixture(async (fixtureRoot) => {
    await writeTextFile(fixtureRoot, 'tests/example/Example.csproj', '<Project />');
    const androidBucket = 'apps/companion/src-tauri/gen/android/app/build';
    const dotnetBucket = 'tests/example/bin';
    const cargoBucket = 'apps/companion/src-tauri/target/debug';
    await writeFixtureFile(fixtureRoot, `${androidBucket}/output.apk`, 2 * MIB);
    await writeFixtureFile(fixtureRoot, `${dotnetBucket}/example.dll`, MIB);
    await writeFixtureFile(fixtureRoot, `${cargoBucket}/cache.bin`, MIB);

    const prune = runCli(fixtureRoot, 'prune', [
      '--limit-gib', '10',
      '--target-gib', '8',
      '--android-limit-gib', String((1.5 * MIB) / GIB),
      '--dotnet-limit-gib', String((0.5 * MIB) / GIB),
      '--max-age-days', '999',
    ]);
    assertSuccess(prune, 'category quota prune');
    assert.match(prune.stdout, /Removing \[android-quota\].*android\/app\/build/);
    assert.match(prune.stdout, /Removing \[dotnet-quota\].*tests\/example\/bin/);
    assert.equal(await exists(path.join(fixtureRoot, androidBucket)), false);
    assert.equal(await exists(path.join(fixtureRoot, dotnetBucket)), false);
    assert.equal(await exists(path.join(fixtureRoot, `${cargoBucket}/cache.bin`)), true);
  });
}

async function auditFixtureRootSafety() {
  const unsafeRoot = await mkdtemp(path.join(os.tmpdir(), 'mystia-build-artifacts-unsafe-'));
  try {
    await writeFixtureFile(unsafeRoot, 'apps/companion/src-tauri/target/debug/cache.bin', 1024);
    const result = runCli(unsafeRoot, 'clean');
    assert.notEqual(result.status, 0, 'A custom root without the fixture sentinel must be rejected.');
    assert.match(result.stderr, /missing safety sentinel/);
    assert.equal(await exists(path.join(unsafeRoot, 'apps/companion/src-tauri/target/debug/cache.bin')), true);
  } finally {
    await rm(unsafeRoot, { recursive: true, force: true });
  }
}

async function auditPruneEnvironmentGate() {
  await withFixture(async (fixtureRoot) => {
    const managedFile = 'apps/companion/src-tauri/target/debug/cache.bin';
    await writeFixtureFile(fixtureRoot, managedFile, MIB);

    const skipped = runCli(
      fixtureRoot,
      'prune',
      ['--limit-gib', String((0.5 * MIB) / GIB), '--target-gib', String((0.25 * MIB) / GIB)],
      { MYSTIA_SKIP_BUILD_CACHE_CLEANUP: '1' },
    );
    assertSuccess(skipped, 'prune environment gate');
    assert.match(skipped.stdout, /prune skipped by MYSTIA_SKIP_BUILD_CACHE_CLEANUP/);
    assert.equal(await exists(path.join(fixtureRoot, managedFile)), true);

    const explicitClean = runCli(
      fixtureRoot,
      'clean',
      [],
      { MYSTIA_SKIP_BUILD_CACHE_CLEANUP: '1' },
    );
    assertSuccess(explicitClean, 'clean ignores prune environment gate');
    assert.equal(await exists(path.join(fixtureRoot, managedFile)), false);
  });
}

async function auditPolicyValidation() {
  await withFixture(async (fixtureRoot) => {
    const equalWatermarks = runCli(fixtureRoot, 'prune', ['--limit-gib', '8', '--target-gib', '8']);
    assert.notEqual(equalWatermarks.status, 0);
    assert.match(equalWatermarks.stderr, /target-gib must be lower than --limit-gib/);

    const zeroLimit = runCli(fixtureRoot, 'prune', ['--limit-gib', '0', '--target-gib', '0']);
    assert.notEqual(zeroLimit.status, 0);
    assert.match(zeroLimit.stderr, /limit-gib must be a positive finite number/);
  });
}

async function auditSymlinkRefusal() {
  await withFixture(async (fixtureRoot) => {
    const outsideRoot = await mkdtemp(path.join(os.tmpdir(), 'mystia-build-artifacts-outside-'));
    try {
      await writeTextFile(outsideRoot, 'keep.txt', 'outside');
      const targetRoot = path.join(fixtureRoot, 'apps/companion/src-tauri/target');
      await mkdir(targetRoot, { recursive: true });
      await symlink(outsideRoot, path.join(targetRoot, 'debug'), process.platform === 'win32' ? 'junction' : 'dir');

      const result = runCli(fixtureRoot, 'clean');
      assert.notEqual(result.status, 0, 'A symlink bucket must be rejected.');
      assert.match(result.stderr, /Refusing symbolic link/);
      assert.equal(await readFile(path.join(outsideRoot, 'keep.txt'), 'utf8'), 'outside');
    } finally {
      await rm(outsideRoot, { recursive: true, force: true });
    }
  });
}

async function auditExclusiveLock() {
  await withFixture(async (fixtureRoot) => {
    await writeFixtureFile(fixtureRoot, 'apps/companion/src-tauri/target/debug/cache.bin', 1024);
    const resolvedRoot = await realpath(fixtureRoot);
    const lockPath = lockPathForRoot(resolvedRoot);
    await writeFile(lockPath, JSON.stringify({
      pid: process.pid,
      token: 'audit-owner',
      createdAt: new Date().toISOString(),
    }));
    try {
      const result = runCli(fixtureRoot, 'prune');
      assert.notEqual(result.status, 0, 'A concurrent prune/clean lock must be rejected.');
      assert.match(result.stderr, /Another artifact prune\/clean is running/);
      assert.equal(await exists(path.join(fixtureRoot, 'apps/companion/src-tauri/target/debug/cache.bin')), true);
    } finally {
      await rm(lockPath, { force: true });
    }
  });
}

async function withFixture(run) {
  const fixtureRoot = await mkdtemp(path.join(os.tmpdir(), 'mystia-build-artifacts-fixture-'));
  try {
    await writeFile(path.join(fixtureRoot, FIXTURE_SENTINEL), `${FIXTURE_SENTINEL_CONTENT}\n`);
    await run(fixtureRoot);
  } finally {
    await rm(fixtureRoot, { recursive: true, force: true });
  }
}

function runCli(fixtureRoot, command, options = [], environment = {}) {
  return spawnSync(process.execPath, [cliPath, command, '--root', fixtureRoot, ...options], {
    cwd: root,
    encoding: 'utf8',
    env: { ...process.env, ...environment },
  });
}

function assertSuccess(result, label) {
  assert.equal(
    result.status,
    0,
    `${label} failed.\nstdout:\n${result.stdout}\nstderr:\n${result.stderr}`,
  );
}

async function writeFixtureFile(fixtureRoot, relativePath, size) {
  const filePath = path.join(fixtureRoot, relativePath);
  await mkdir(path.dirname(filePath), { recursive: true });
  await writeFile(filePath, 'x');
  await truncate(filePath, size);
}

async function writeTextFile(fixtureRoot, relativePath, content) {
  const filePath = path.join(fixtureRoot, relativePath);
  await mkdir(path.dirname(filePath), { recursive: true });
  await writeFile(filePath, content);
}

async function setTreeAge(directory, ageDays) {
  const date = new Date(Date.now() - ageDays * 24 * 60 * 60 * 1000);
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    const entryPath = path.join(directory, entry.name);
    if (entry.isDirectory()) await setTreeAge(entryPath, ageDays);
    await utimes(entryPath, date, date);
  }
  await utimes(directory, date, date);
}

async function exists(filePath) {
  return lstat(filePath).then(() => true, () => false);
}
