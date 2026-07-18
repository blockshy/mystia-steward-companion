import assert from 'node:assert/strict';
import {
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  assertRealDirectory,
  commitStagedAndroidApks,
  pruneThenCommitAndroidApks,
} from '../../scripts/build-android-signed-apk.mjs';

const canonicalAssetNames = [
  'mystia-steward-companion-android-arm64-v8a.apk',
  'mystia-steward-companion-android-armeabi-v7a.apk',
];
const fixtureRoot = mkdtempSync(path.join(os.tmpdir(), 'mystia-android-apk-transaction-audit-'));

try {
  auditRealDirectoryGuard();
  auditPruneFailureDoesNotChangeReleaseAssets();
  auditSecondApkFailureRollsBackBothAssets();
  auditSuccessfulTwoApkCommit();
  console.log('Android APK transaction audit passed.');
} finally {
  rmSync(fixtureRoot, { recursive: true, force: true });
}

function auditRealDirectoryGuard() {
  const realDirectory = path.join(fixtureRoot, 'real-dist');
  const linkedDirectory = path.join(fixtureRoot, 'linked-dist');
  mkdirSync(realDirectory);
  symlinkSync(realDirectory, linkedDirectory, process.platform === 'win32' ? 'junction' : 'dir');

  assert.doesNotThrow(() => assertRealDirectory(realDirectory, 'fixture dist'));
  assert.throws(
    () => assertRealDirectory(linkedDirectory, 'fixture dist'),
    /real directory, not a file, symlink, or junction/,
  );
}

function auditPruneFailureDoesNotChangeReleaseAssets() {
  const transaction = createTransactionFixture('prune-failure', true);

  assert.throws(
    () => pruneThenCommitAndroidApks(transaction, () => {
      throw new Error('fixture prune failure');
    }),
    /fixture prune failure/,
  );
  assertCanonicalContents(transaction.distDir, ['old-arm64', 'old-armv7']);
  assert.equal(lstatExists(transaction.stagingDir), false, 'Failed prune left a protected staging directory behind.');
}

function auditSecondApkFailureRollsBackBothAssets() {
  const transaction = createTransactionFixture('second-apk-failure', false);

  assert.throws(
    () => commitStagedAndroidApks(transaction),
    /ENOENT|no such file/i,
  );
  assertCanonicalContents(transaction.distDir, ['old-arm64', 'old-armv7']);
  assert.equal(lstatExists(transaction.stagingDir), false, 'Successful rollback left its staging directory behind.');
}

function auditSuccessfulTwoApkCommit() {
  const transaction = createTransactionFixture('success', true);

  pruneThenCommitAndroidApks(transaction, () => {});
  assertCanonicalContents(transaction.distDir, ['new-arm64', 'new-armv7']);
  assert.equal(lstatExists(transaction.stagingDir), false, 'Successful commit left its staging directory behind.');
}

function createTransactionFixture(name, includeSecondStagedApk) {
  const distDir = path.join(fixtureRoot, name, 'dist');
  const stagingDir = path.join(distDir, '.android-apk-stage-fixture');
  mkdirSync(stagingDir, { recursive: true });

  const oldContents = ['old-arm64', 'old-armv7'];
  const newContents = ['new-arm64', 'new-armv7'];
  const items = canonicalAssetNames.map((assetName, index) => {
    const releaseAssetPath = path.join(distDir, assetName);
    const stagedPath = path.join(stagingDir, assetName);
    writeFileSync(releaseAssetPath, oldContents[index]);
    if (index === 0 || includeSecondStagedApk) {
      writeFileSync(stagedPath, newContents[index]);
    }

    return {
      target: { assetName },
      releaseAssetPath,
      stagedPath,
    };
  });

  return { distDir, stagingDir, items };
}

function assertCanonicalContents(distDir, expectedContents) {
  canonicalAssetNames.forEach((assetName, index) => {
    assert.equal(readFileSync(path.join(distDir, assetName), 'utf8'), expectedContents[index]);
  });
}

function lstatExists(candidatePath) {
  try {
    lstatSync(candidatePath);
    return true;
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw error;
  }
}
