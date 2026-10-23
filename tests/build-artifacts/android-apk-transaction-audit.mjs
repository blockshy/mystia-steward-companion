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
  readFlutterAndroidVersionCode,
  extractApkSignerCertificateSha256,
  parseAapt2Badging,
  pruneThenCommitAndroidApks,
  resolveReleaseAndroidApkCandidates,
  verifyAndroidApkAlignment,
  verifyNoBundledGameMediaInApk,
} from '../../scripts/build-android-signed-apk.mjs';

const canonicalAssetNames = [
  'mystia-steward-companion-android-arm64-v8a.apk',
  'mystia-steward-companion-android-armeabi-v7a.apk',
];
const fixtureRoot = mkdtempSync(path.join(os.tmpdir(), 'mystia-android-apk-transaction-audit-'));

try {
  auditRealDirectoryGuard();
  auditSigningCertificateParsing();
  auditApkAlignmentGate();
  auditRetiredGameMediaGate();
  auditAndroidApkIdentity();
  auditFlutterAndroidVersionCode();
  auditAndroidApkCandidateResolution();
  auditPruneFailureDoesNotChangeReleaseAssets();
  auditSecondApkFailureRollsBackBothAssets();
  auditSuccessfulTwoApkCommit();
  console.log('Android APK transaction audit passed.');
} finally {
  rmSync(fixtureRoot, { recursive: true, force: true });
}

function auditApkAlignmentGate() {
  // 固定使用只读 -c 和 16 KiB 页检查；验证工具失败必须向上传播，不能继续交付。
  let called = false;
  verifyAndroidApkAlignment('fixture.apk', 'locked-zipalign', (command, args) => {
    assert.equal(command, 'locked-zipalign');
    assert.deepEqual(args, ['-c', '-P', '16', '-v', '4', 'fixture.apk']);
    called = true;
    return { status: 0 };
  });
  assert.equal(called, true);
  assert.throws(() => verifyAndroidApkAlignment('fixture.apk', 'locked-zipalign', () => {
    throw new Error('alignment verification failed');
  }), /alignment verification failed/u);
}

function auditRetiredGameMediaGate() {
  // 仅替换外部 jar 调用：生产的条目解析/拒绝逻辑照常运行，测试不需要 Android SDK、
  // 签名材料或真实 APK。只读命令及工具失败传播也必须明确验证，避免失败后继续发布。
  const candidate = path.join(fixtureRoot, 'candidate.apk');
  const previous = path.join(fixtureRoot, 'previous.apk');
  writeFileSync(candidate, 'candidate-apk-fixture');
  writeFileSync(previous, 'previous-complete-apk');
  const cleanEntries = ['AndroidManifest.xml', 'classes.dex', 'assets/flutter_assets/assets/help/zh-CN.json'];
  const inspect = (entries) => verifyNoBundledGameMediaInApk(candidate, 'locked-jar', (command, args, options) => {
    assert.equal(command, 'locked-jar');
    assert.deepEqual(args, ['--list', '--file', candidate]);
    assert.equal(options.maxBuffer, 8 * 1024 * 1024);
    return { stdout: `${entries.join('\n')}\n` };
  });
  inspect(cleanEntries);
  // 名称相似的合法目录不得误杀；只拒绝整个退役命名空间，而非所有 PNG 或 game-media 字样。
  inspect([...cleanEntries, 'assets/flutter_assets/assets/game-media-preview/example.png']);
  for (const retired of [
    'assets/flutter_assets/assets/game-media/images/food-1.png',
    'assets/flutter_assets/assets/game-media/manifest.json',
    'assets/flutter_assets/assets/game-media/',
    'ASSETS/FLUTTER_ASSETS/ASSETS/GAME-MEDIA/old.PNG',
    'assets\\flutter_assets\\assets\\game-media\\old.png',
  ]) {
    assert.throws(() => inspect([...cleanEntries, retired]), /retired bundled assets\/game-media/u);
    assert.equal(readFileSync(candidate, 'utf8'), 'candidate-apk-fixture');
    assert.equal(readFileSync(previous, 'utf8'), 'previous-complete-apk');
  }
  assert.throws(() => inspect([]), /no AndroidManifest.xml/u);
  assert.throws(() => verifyNoBundledGameMediaInApk(candidate, 'locked-jar', () => {
    throw new Error('ZIP listing failed');
  }), /ZIP listing failed/u);
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
        '1003002',
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

function auditFlutterAndroidVersionCode() {
  const version=JSON.parse(readFileSync(new URL('../../package.json',import.meta.url),'utf8')).version;
  assert.ok(readFlutterAndroidVersionCode(version)>1003001);
  assert.throws(() => readFlutterAndroidVersionCode('0.0.0'), /版本不一致/u);
}

// 人工夹具固定 build number，无需依赖仓库实际版本；版本身份与ABI校验仍走生产函数。
function computeAndroidVersionCode() { return 1003000; }

function auditAndroidApkCandidateResolution() {
  const arm64Path = path.join(
    fixtureRoot,
    'identity',
    'apk',
    'arm64',
    'release',
    'app-arm64-v8a-release.apk',
  );
  const armv7Path = path.join(
    fixtureRoot,
    'identity',
    'apk',
    'arm',
    'release',
    'app-armeabi-v7a-release.apk',
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
      1003000,
  );
  assert.deepEqual(resolved.map((item) => item.apkPath), [arm64Path, armv7Path]);
  assert.deepEqual(inspectedPaths, [armv7Path, arm64Path]);

  assert.throws(
    () => resolveReleaseAndroidApkCandidates(
      [arm64Path],
      (candidate) => badgingByPath.get(candidate),
      '1.3.0',
      1003000,
    ),
    /exactly one signed armeabi-v7a release APK, received 0/u,
  );

  const duplicateArm64Path = path.join(
    fixtureRoot,
    'identity',
    'apk',
    'arm64',
    'release',
    'duplicate', 'app-arm64-v8a-release.apk',
  );
  assert.throws(
    () => resolveReleaseAndroidApkCandidates(
      [arm64Path, duplicateArm64Path, armv7Path],
      (candidate) => candidate === armv7Path
        ? badgingByPath.get(armv7Path)
        : badgingByPath.get(arm64Path),
      '1.3.0',
      1003000,
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
      1003000,
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
      1003000,
    ),
    /Android APK native-code mismatch/u,
  );
}

function createBadging(
  applicationId,
  versionName,
  nativeCodes,
  versionCode = String(computeAndroidVersionCode(versionName)),
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
