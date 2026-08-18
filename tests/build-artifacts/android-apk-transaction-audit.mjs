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
  assertAndroidApkIdentity,
  assertApkSignerCertificateSha256,
  assertRealDirectory,
  commitStagedAndroidApks,
  computeTauriAndroidVersionCode,
  extractApkSignerCertificateSha256,
  parseAapt2Badging,
  pruneThenCommitAndroidApks,
  resolveReleaseAndroidApkCandidates,
} from '../../scripts/build-android-signed-apk.mjs';

const canonicalAssetNames = [
  'mystia-steward-companion-android-arm64-v8a.apk',
  'mystia-steward-companion-android-armeabi-v7a.apk',
];
const fixtureRoot = mkdtempSync(path.join(os.tmpdir(), 'mystia-android-apk-transaction-audit-'));

try {
  auditRealDirectoryGuard();
  auditSigningCertificateParsing();
  auditAndroidApkIdentity();
  auditTauriAndroidVersionCode();
  auditAndroidApkCandidateResolution();
  auditPruneFailureDoesNotChangeReleaseAssets();
  auditSecondApkFailureRollsBackBothAssets();
  auditSuccessfulTwoApkCommit();
  console.log('Android APK transaction audit passed.');
} finally {
  rmSync(fixtureRoot, { recursive: true, force: true });
}

function auditSigningCertificateParsing() {
  const expected = '1540b609d5cd54e06a8429bb0aaa2cc4b511e055565fdac93acf206c1791d1fb';
  assert.equal(
    extractApkSignerCertificateSha256(
      `Signer #1 certificate SHA-256 digest: ${expected.toUpperCase()}`,
    ),
    expected,
  );
  assert.equal(
    assertApkSignerCertificateSha256(
      `Signer #1 certificate SHA-256 digest: ${expected}`,
      expected,
    ),
    expected,
  );
  assert.throws(
    () => assertApkSignerCertificateSha256(
      `Signer #1 certificate SHA-256 digest: ${'0'.repeat(64)}`,
      expected,
    ),
    /Android APK signing certificate mismatch/u,
  );
  assert.throws(
    () => assertApkSignerCertificateSha256(
      `Signer #1 certificate SHA-256 digest: ${expected}`,
      expected.toUpperCase(),
    ),
    /Locked Android signing certificate SHA-256 is invalid/u,
  );
  assert.equal(
    extractApkSignerCertificateSha256(
      `Signer #1 certificate SHA-256 digest: ${expected.match(/../gu).join(':')}`,
    ),
    expected,
  );
  assert.throws(
    () => extractApkSignerCertificateSha256('Verified using v2 scheme (APK Signature Scheme v2): true'),
    /exactly one valid Android APK signer certificate/u,
  );
  assert.throws(
    () => extractApkSignerCertificateSha256(
      `Signer #1 certificate SHA-256 digest: ${expected}\nSigner #2 certificate SHA-256 digest: ${expected}`,
    ),
    /received 2/u,
  );
}

function auditAndroidApkIdentity() {
  const arm64Badging = createBadging('com.tyukki.mystia.steward.companion', '1.3.0', ['arm64-v8a']);
  assert.deepEqual(
    parseAapt2Badging(arm64Badging),
    {
      applicationId: 'com.tyukki.mystia.steward.companion',
      versionName: '1.3.0',
      versionCode: 1_003_000,
      nativeCodes: ['arm64-v8a'],
    },
  );
  assert.deepEqual(
    assertAndroidApkIdentity(arm64Badging, {
      applicationId: 'com.tyukki.mystia.steward.companion',
      versionName: '1.3.0',
      versionCode: 1_003_000,
      abi: 'arm64-v8a',
    }),
    {
      applicationId: 'com.tyukki.mystia.steward.companion',
      versionName: '1.3.0',
      versionCode: 1_003_000,
      nativeCodes: ['arm64-v8a'],
    },
  );
  assert.equal(
    assertAndroidApkIdentity(
      createBadging(
        'com.tyukki.mystia.steward.companion',
        '1.3.0-preview.2',
        ['arm64-v8a'],
      ),
      {
        applicationId: 'com.tyukki.mystia.steward.companion',
        versionName: '1.3.0-preview.2',
        versionCode: 1_003_000,
        abi: 'arm64-v8a',
      },
    ).versionCode,
    1_003_000,
  );

  assert.throws(
    () => assertAndroidApkIdentity(
      createBadging('com.example.wrong', '1.3.0', ['arm64-v8a']),
      expectedIdentity('arm64-v8a'),
    ),
    /Android APK package mismatch/u,
  );
  assert.throws(
    () => assertAndroidApkIdentity(
      createBadging('com.tyukki.mystia.steward.companion', '1.2.0', ['arm64-v8a']),
      expectedIdentity('arm64-v8a'),
    ),
    /Android APK versionName mismatch/u,
  );
  assert.throws(
    () => assertAndroidApkIdentity(
      createBadging(
        'com.tyukki.mystia.steward.companion',
        '1.3.0',
        ['arm64-v8a'],
        '1003001',
      ),
      expectedIdentity('arm64-v8a'),
    ),
    /Android APK versionCode mismatch/u,
  );
  assert.throws(
    () => assertAndroidApkIdentity(
      createBadging(
        'com.tyukki.mystia.steward.companion',
        '1.3.0',
        ['arm64-v8a', 'armeabi-v7a'],
      ),
      expectedIdentity('arm64-v8a'),
    ),
    /Android APK native-code mismatch/u,
  );
  assert.throws(
    () => parseAapt2Badging(
      "package: name='com.tyukki.mystia.steward.companion' versionCode='1003000' versionName='1.3.0'",
    ),
    /exactly one Android native-code badging line/u,
  );
  assert.throws(
    () => parseAapt2Badging(
      `${createBadging('com.tyukki.mystia.steward.companion', '1.3.0', ['arm64-v8a'])}`
      + "package: name='duplicate' versionName='1.3.0'\n",
    ),
    /exactly one Android package badging line/u,
  );
  assert.throws(
    () => parseAapt2Badging(
      "package: name='com.tyukki.mystia.steward.companion' versionCode='1003000' versionName='1.3.0'\n"
      + "native-code: arm64-v8a\n",
    ),
    /native-code badging line is malformed/u,
  );
  assert.throws(
    () => parseAapt2Badging(
      "package: name='com.tyukki.mystia.steward.companion' versionName='1.3.0'\n"
      + "native-code: 'arm64-v8a'\n",
    ),
    /exactly one non-empty Android package versionCode attribute/u,
  );
  assert.throws(
    () => parseAapt2Badging(
      "package: name='com.tyukki.mystia.steward.companion' versionCode='1003000' versionCode='1003000' versionName='1.3.0'\n"
      + "native-code: 'arm64-v8a'\n",
    ),
    /exactly one non-empty Android package versionCode attribute/u,
  );
  for (const invalidVersionCode of ['0', '01003000', '+1003000', '1.003e6', '2100000001']) {
    assert.throws(
      () => parseAapt2Badging(
        createBadging(
          'com.tyukki.mystia.steward.companion',
          '1.3.0',
          ['arm64-v8a'],
          invalidVersionCode,
        ),
      ),
      /versionCode must be a canonical positive decimal integer|versionCode exceeds/u,
    );
  }
}

function auditTauriAndroidVersionCode() {
  assert.equal(computeTauriAndroidVersionCode('1.3.0'), 1_003_000);
  assert.equal(computeTauriAndroidVersionCode('1.3.0-preview.1'), 1_003_000);
  assert.equal(computeTauriAndroidVersionCode('1.3.0-preview.999'), 1_003_000);
  assert.equal(computeTauriAndroidVersionCode('0.0.1'), 1);
  assert.equal(computeTauriAndroidVersionCode('1.999.999'), 1_999_999);
  assert.equal(computeTauriAndroidVersionCode('2099.999.999'), 2_099_999_999);
  assert.equal(computeTauriAndroidVersionCode('2100.0.0'), 2_100_000_000);

  for (const invalidVersion of [
    '1.3.0-preview.0',
    '1.3.0-preview.01',
    '1.3.0-preview',
    '1.3.0-rc.1',
    '01.3.0',
    '1.03.0',
    '1.3.00',
    '1.3',
    '1.3.0.0',
    ' 1.3.0',
    '1.3.0 ',
  ]) {
    assert.throws(
      () => computeTauriAndroidVersionCode(invalidVersion),
      /canonical X\.Y\.Z or X\.Y\.Z-preview\.N/u,
    );
  }
  for (const invalidVersion of ['1.1000.0', '1.0.1000']) {
    assert.throws(
      () => computeTauriAndroidVersionCode(invalidVersion),
      /minor and patch versions must each be between 0 and 999/u,
    );
  }
  for (const invalidVersion of ['0.0.0', '2100.0.1', '2101.0.0']) {
    assert.throws(
      () => computeTauriAndroidVersionCode(invalidVersion),
      /versionCode must be between 1 and 2100000000/u,
    );
  }
}

function auditAndroidApkCandidateResolution() {
  const arm64Path = path.join(
    fixtureRoot,
    'identity',
    'apk',
    'arm64',
    'release',
    'app-arm64-release.apk',
  );
  const armv7Path = path.join(
    fixtureRoot,
    'identity',
    'apk',
    'arm',
    'release',
    'app-arm-release.apk',
  );
  const badgingByPath = new Map([
    [arm64Path, createBadging('com.tyukki.mystia.steward.companion', '1.3.0', ['arm64-v8a'])],
    [armv7Path, createBadging('com.tyukki.mystia.steward.companion', '1.3.0', ['armeabi-v7a'])],
  ]);
  const inspectedPaths = [];
  const resolved = resolveReleaseAndroidApkCandidates(
    [armv7Path, arm64Path],
    (candidate) => {
      inspectedPaths.push(candidate);
      return badgingByPath.get(candidate);
    },
    '1.3.0',
  );
  assert.deepEqual(resolved.map((item) => item.apkPath), [arm64Path, armv7Path]);
  assert.deepEqual(inspectedPaths, [armv7Path, arm64Path]);

  assert.throws(
    () => resolveReleaseAndroidApkCandidates(
      [arm64Path],
      (candidate) => badgingByPath.get(candidate),
      '1.3.0',
    ),
    /exactly one signed armeabi-v7a release APK, received 0/u,
  );

  const duplicateArm64Path = path.join(
    fixtureRoot,
    'identity',
    'apk',
    'arm64',
    'release',
    'app-arm64-copy-release.apk',
  );
  assert.throws(
    () => resolveReleaseAndroidApkCandidates(
      [arm64Path, duplicateArm64Path, armv7Path],
      (candidate) => candidate === armv7Path
        ? badgingByPath.get(armv7Path)
        : badgingByPath.get(arm64Path),
      '1.3.0',
    ),
    /exactly one signed arm64-v8a release APK, received 2/u,
  );

  const unconsumedPath = path.join(
    fixtureRoot,
    'identity',
    'apk',
    'universal',
    'release',
    'app-universal-release.apk',
  );
  const unconsumedInspectionOrder = [];
  assert.throws(
    () => resolveReleaseAndroidApkCandidates(
      [unconsumedPath, arm64Path, armv7Path],
      (candidate) => {
        unconsumedInspectionOrder.push(candidate);
        return candidate === armv7Path
          ? badgingByPath.get(armv7Path)
          : badgingByPath.get(arm64Path);
      },
      '1.3.0',
    ),
    /Unconsumed signed Android release APK/u,
  );
  assert.deepEqual(unconsumedInspectionOrder, [unconsumedPath, arm64Path, armv7Path]);

  assert.throws(
    () => resolveReleaseAndroidApkCandidates(
      [arm64Path, armv7Path],
      (candidate) => candidate === arm64Path
        ? badgingByPath.get(armv7Path)
        : badgingByPath.get(arm64Path),
      '1.3.0',
    ),
    /Android APK native-code mismatch/u,
  );
}

function createBadging(
  applicationId,
  versionName,
  nativeCodes,
  versionCode = String(computeTauriAndroidVersionCode(versionName)),
) {
  return [
    `package: name='${applicationId}' versionCode='${versionCode}' versionName='${versionName}' compileSdkVersion='36'`,
    "sdkVersion:'24'",
    `native-code: ${nativeCodes.map((abi) => `'${abi}'`).join(' ')}`,
    '',
  ].join('\n');
}

function expectedIdentity(abi) {
  return {
    applicationId: 'com.tyukki.mystia.steward.companion',
    versionName: '1.3.0',
    versionCode: 1_003_000,
    abi,
  };
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
