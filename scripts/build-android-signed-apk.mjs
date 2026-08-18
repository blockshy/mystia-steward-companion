import {
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  renameSync,
  rmSync,
} from 'node:fs';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptDir, '..');
const companionDir = path.join(repoRoot, 'apps', 'companion');
const androidDir = path.join(companionDir, 'src-tauri', 'gen', 'android');
const androidJavaSourcesDir = path.join(androidDir, 'app', 'src', 'main', 'java');
const keystorePropertiesPath = path.join(androidDir, 'keystore.properties');
const apkOutputDir = path.join(androidDir, 'app', 'build', 'outputs', 'apk');
const distDir = path.join(repoRoot, 'mods', 'bepinex', 'dist');
const buildArtifactManager = path.join(repoRoot, 'scripts', 'manage-build-artifacts.mjs');
const buildToolchainCheck = path.join(repoRoot, 'scripts', 'check-build-toolchain.mjs');
const toolchain = JSON.parse(readFileSync(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
const androidToolchain = toolchain.android;
const projectPackage = JSON.parse(readFileSync(path.join(repoRoot, 'package.json'), 'utf8'));
const androidApplicationId = 'com.tyukki.mystia.steward.companion';
const maximumAndroidVersionCode = 2_100_000_000;
const releaseApkTargets = [
  {
    target: 'aarch64',
    flavor: 'arm64',
    abi: 'arm64-v8a',
    assetName: 'mystia-steward-companion-android-arm64-v8a.apk',
  },
  {
    target: 'armv7',
    flavor: 'arm',
    abi: 'armeabi-v7a',
    assetName: 'mystia-steward-companion-android-armeabi-v7a.apk',
  },
];
const androidReleaseProfileEnv = {
  CARGO_PROFILE_RELEASE_STRIP: 'symbols',
  CARGO_PROFILE_RELEASE_LTO: 'thin',
  CARGO_PROFILE_RELEASE_CODEGEN_UNITS: '1',
};

if (isMainModule()) {
  main();
}

function main() {
  verifyBuildToolchain();
  assertSigningConfig();
  mkdirSync(distDir, { recursive: true });
  assertRealDirectory(distDir, 'Android release dist');
  assertNoPendingAndroidStages();
  pruneBuildArtifacts();
  cleanGeneratedAndroidSources();
  rmSync(apkOutputDir, { recursive: true, force: true });
  runTauriAndroidApkBuild();

  const aapt2 = findAapt2Command();
  const signedApks = findSignedApks(aapt2);
  const apkSigner = findApkSignerCommand();

  const stagedApks = stageAndVerifyAndroidApks(signedApks, apkSigner, aapt2);
  pruneThenCommitAndroidApks(stagedApks);

  console.log('');
  console.log(`Built ${signedApks.length} signed Android APKs.`);
}

function verifyBuildToolchain() {
  run(process.execPath, [buildToolchainCheck, 'android'], { cwd: repoRoot });
}

function isMainModule() {
  return Boolean(process.argv[1])
    && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url));
}

function assertRealDirectory(directoryPath, description) {
  const stats = lstatSync(directoryPath);
  if (!stats.isDirectory() || stats.isSymbolicLink()) {
    throw new Error(`${description} must be a real directory, not a file, symlink, or junction: ${directoryPath}`);
  }
}

function assertNoPendingAndroidStages() {
  const pendingStages = readdirSync(distDir, { withFileTypes: true })
    .filter((entry) => entry.name.startsWith('.android-apk-stage-'))
    .map((entry) => path.join(distDir, entry.name));
  if (pendingStages.length === 0) return;

  throw new Error(
    [
      'A previous Android APK transaction was not completed.',
      'Inspect and remove or restore these paths before building again:',
      ...pendingStages.map((stagePath) => `  - ${stagePath}`),
    ].join('\n'),
  );
}

function stageAndVerifyAndroidApks(signedApksToStage, signer, aapt2Command) {
  const stagingDir = mkdtempSync(path.join(distDir, '.android-apk-stage-'));

  try {
    const items = signedApksToStage.map((item) => {
      const stagedPath = path.join(stagingDir, item.target.assetName);
      copyFileSync(item.apkPath, stagedPath);
      verifyAndroidApkSignature(stagedPath, signer, androidToolchain.signingCertificateSha256);
      assertAndroidApkIdentity(dumpAndroidApkBadging(stagedPath, aapt2Command), {
        applicationId: androidApplicationId,
        versionName: projectPackage.version,
        versionCode: computeTauriAndroidVersionCode(projectPackage.version),
        abi: item.target.abi,
      });
      console.log(`Signed Android APK certificate and package identity verified: ${item.apkPath}`);
      return {
        ...item,
        stagedPath,
        releaseAssetPath: path.join(distDir, item.target.assetName),
      };
    });

    return { stagingDir, items };
  } catch (error) {
    rmSync(stagingDir, { recursive: true, force: true });
    throw error;
  }
}

function commitStagedAndroidApks(stagedApksToCommit) {
  const installedPaths = [];
  const backups = [];
  let removeStagingDirectory = true;

  try {
    for (const item of stagedApksToCommit.items) {
      if (!existsSync(item.releaseAssetPath)) continue;

      const backupPath = path.join(stagedApksToCommit.stagingDir, `.previous-${item.target.assetName}`);
      renameSync(item.releaseAssetPath, backupPath);
      backups.push({ releaseAssetPath: item.releaseAssetPath, backupPath });
    }

    for (const item of stagedApksToCommit.items) {
      renameSync(item.stagedPath, item.releaseAssetPath);
      installedPaths.push(item.releaseAssetPath);
    }

    for (const item of stagedApksToCommit.items) {
      console.log(`Release asset installed: ${item.releaseAssetPath}`);
    }
  } catch (error) {
    const rollbackErrors = [];

    for (const installedPath of installedPaths.reverse()) {
      try {
        rmSync(installedPath, { force: true });
      } catch (rollbackError) {
        rollbackErrors.push(rollbackError);
      }
    }

    for (const backup of backups.reverse()) {
      try {
        if (existsSync(backup.backupPath)) {
          renameSync(backup.backupPath, backup.releaseAssetPath);
        }
      } catch (rollbackError) {
        rollbackErrors.push(rollbackError);
      }
    }

    if (rollbackErrors.length > 0) {
      removeStagingDirectory = false;
      throw new AggregateError([error, ...rollbackErrors], 'Failed to install Android APK assets and restore the previous assets.');
    }

    throw error;
  } finally {
    if (removeStagingDirectory) {
      rmSync(stagedApksToCommit.stagingDir, { recursive: true, force: true });
    } else {
      console.error(`Android APK rollback files were preserved at: ${stagedApksToCommit.stagingDir}`);
    }
  }
}

function pruneThenCommitAndroidApks(stagedApksToCommit, prune = pruneBuildArtifacts) {
  try {
    prune();
  } catch (error) {
    rmSync(stagedApksToCommit.stagingDir, { recursive: true, force: true });
    throw error;
  }

  commitStagedAndroidApks(stagedApksToCommit);
}

function pruneBuildArtifacts() {
  if (readBooleanEnvironmentVariable('MYSTIA_SKIP_BUILD_CACHE_CLEANUP', false)) {
    console.log('Build cache cleanup skipped by MYSTIA_SKIP_BUILD_CACHE_CLEANUP.');
    return;
  }

  const limitGiB = readPositiveIntegerEnvironmentVariable('MYSTIA_BUILD_CACHE_LIMIT_GIB', 12);
  const targetGiB = readPositiveIntegerEnvironmentVariable('MYSTIA_BUILD_CACHE_TARGET_GIB', 8);
  if (targetGiB >= limitGiB) {
    throw new Error(
      `MYSTIA_BUILD_CACHE_TARGET_GIB must be less than MYSTIA_BUILD_CACHE_LIMIT_GIB. Actual: target=${targetGiB}, limit=${limitGiB}`,
    );
  }

  run(
    process.execPath,
    [buildArtifactManager, 'prune', '--limit-gib', String(limitGiB), '--target-gib', String(targetGiB)],
    { cwd: repoRoot },
  );
}

function readPositiveIntegerEnvironmentVariable(name, defaultValue) {
  const rawValue = process.env[name];
  if (rawValue === undefined || rawValue.trim() === '') return defaultValue;

  const value = Number(rawValue);
  if (!Number.isInteger(value) || value <= 0) {
    throw new Error(`${name} must be a positive integer. Actual: ${rawValue}`);
  }

  return value;
}

function readBooleanEnvironmentVariable(name, defaultValue) {
  const rawValue = process.env[name];
  if (rawValue === undefined || rawValue.trim() === '') return defaultValue;
  if (/^(1|true)$/iu.test(rawValue)) return true;
  if (/^(0|false)$/iu.test(rawValue)) return false;
  throw new Error(`${name} must be 1, 0, true, or false. Actual: ${rawValue}`);
}

function assertSigningConfig() {
  if (!existsSync(keystorePropertiesPath)) {
    throw new Error(
      [
        `Missing Android signing config: ${keystorePropertiesPath}`,
        '',
        'Create the file with these keys before running this command:',
        '  keyAlias=mystia-steward-companion',
        '  storePassword=<keystore password>',
        '  keyPassword=<key password>',
        '  storeFile=<absolute path to your release keystore>',
      ].join('\n'),
    );
  }

  const properties = parseProperties(readFileSync(keystorePropertiesPath, 'utf8'));
  assertSigningProperties(properties, keystorePropertiesPath);

  const storeFile = resolveStoreFile(properties.storeFile);
  if (!existsSync(storeFile)) {
    throw new Error(`Android signing keystore does not exist: ${storeFile}`);
  }
}

function assertSigningProperties(properties, sourceLabel = 'Android signing configuration') {
  const requiredKeys = ['keyAlias', 'storePassword', 'keyPassword', 'storeFile'];
  const unexpectedKeys = Object.keys(properties).filter((key) => !requiredKeys.includes(key));
  if (unexpectedKeys.length > 0) {
    throw new Error(
      `Unsupported Android signing properties in ${sourceLabel}: ${unexpectedKeys.join(', ')}`,
    );
  }

  const missingKeys = requiredKeys.filter((key) => !properties[key]);
  if (missingKeys.length > 0) {
    throw new Error(`Missing Android signing properties in ${sourceLabel}: ${missingKeys.join(', ')}`);
  }
}

function parseProperties(content) {
  const properties = {};
  for (const rawLine of content.split(/\r?\n/)) {
    const lineWithoutLeadingWhitespace = rawLine.replace(/^[ \t\f]+/u, '');
    if (!lineWithoutLeadingWhitespace
      || lineWithoutLeadingWhitespace.startsWith('#')
      || lineWithoutLeadingWhitespace.startsWith('!')) continue;

    const separatorIndex = rawLine.search(/[:=]/);
    if (separatorIndex < 0) continue;

    const key = rawLine.slice(0, separatorIndex).trim();
    const encodedValue = rawLine
      .slice(separatorIndex + 1)
      .replace(/^[ \t\f]+/u, '');
    const value = decodeJavaPropertyValue(encodedValue);
    if (key) {
      if (Object.hasOwn(properties, key)) {
        throw new Error(`Android signing property is duplicated: ${key}`);
      }
      Object.defineProperty(properties, key, {
        configurable: false,
        enumerable: true,
        value,
        writable: false,
      });
    }
  }

  return properties;
}

function decodeJavaPropertyValue(value) {
  let decoded = '';
  for (let index = 0; index < value.length; index += 1) {
    const character = value[index];
    if (character !== '\\') {
      decoded += character;
      continue;
    }

    index += 1;
    if (index >= value.length) {
      throw new Error('Android signing property ends with an incomplete escape sequence.');
    }
    const escaped = value[index];
    switch (escaped) {
      case 't': decoded += '\t'; break;
      case 'n': decoded += '\n'; break;
      case 'r': decoded += '\r'; break;
      case 'f': decoded += '\f'; break;
      case 'u': {
        const hexadecimal = value.slice(index + 1, index + 5);
        if (!/^[0-9a-fA-F]{4}$/u.test(hexadecimal)) {
          throw new Error('Android signing property contains an invalid Unicode escape sequence.');
        }
        decoded += String.fromCharCode(Number.parseInt(hexadecimal, 16));
        index += 4;
        break;
      }
      default: decoded += escaped; break;
    }
  }
  return decoded;
}

function resolveStoreFile(storeFile) {
  if (path.isAbsolute(storeFile)) return storeFile;
  if (/^[A-Za-z]:[\\/]/.test(storeFile)) return storeFile;
  return path.resolve(androidDir, storeFile);
}

function runTauriAndroidApkBuild() {
  const tauriCliScript = findTauriCliScript();
  const buildOptions = withAndroidReleaseProfileEnv({ cwd: companionDir });
  const buildArgs = [
    'android',
    'build',
    '--apk',
    '--split-per-abi',
    '--target',
    ...releaseApkTargets.map((target) => target.target),
  ];
  if (tauriCliScript) {
    run(process.execPath, [tauriCliScript, ...buildArgs], buildOptions);
    return;
  }

  run('tauri', buildArgs, buildOptions);
}

function withAndroidReleaseProfileEnv(options) {
  return {
    ...options,
    env: {
      ...process.env,
      ...androidReleaseProfileEnv,
    },
  };
}

function cleanGeneratedAndroidSources() {
  if (!existsSync(androidJavaSourcesDir)) return;

  for (const generatedDir of findGeneratedDirectories(androidJavaSourcesDir)) {
    rmSync(generatedDir, { recursive: true, force: true });
  }
}

function findGeneratedDirectories(rootDir) {
  const matches = [];
  const stack = [rootDir];

  while (stack.length > 0) {
    const currentDir = stack.pop();
    for (const entry of readdirSync(currentDir, { withFileTypes: true })) {
      if (!entry.isDirectory()) continue;

      const entryPath = path.join(currentDir, entry.name);
      if (entry.name === 'generated') {
        matches.push(entryPath);
        continue;
      }

      stack.push(entryPath);
    }
  }

  return matches;
}

function findTauriCliScript() {
  try {
    const packageJsonPath = require.resolve('@tauri-apps/cli/package.json', { paths: [repoRoot] });
    const packageJson = JSON.parse(readFileSync(packageJsonPath, 'utf8'));
    const binPath = typeof packageJson.bin === 'string' ? packageJson.bin : packageJson.bin?.tauri;
    if (!binPath) return '';

    const cliScript = path.resolve(path.dirname(packageJsonPath), binPath);
    return existsSync(cliScript) ? cliScript : '';
  } catch {
    return '';
  }
}

function findSignedApks(aapt2Command) {
  if (!existsSync(apkOutputDir)) {
    throw new Error(`Android APK output directory was not generated: ${apkOutputDir}`);
  }

  const candidates = listFilesRecursive(apkOutputDir)
    .filter((filePath) => isSignedReleaseApk(filePath))
    .sort((left, right) => left.localeCompare(right));

  if (candidates.length === 0) {
    throw new Error(`No signed release APK found in ${apkOutputDir}`);
  }

  return resolveReleaseAndroidApkCandidates(
    candidates,
    (candidate) => dumpAndroidApkBadging(candidate, aapt2Command),
    projectPackage.version,
  );
}

function listFilesRecursive(rootDir) {
  const files = [];
  const stack = [rootDir];

  while (stack.length > 0) {
    const currentDir = stack.pop();
    for (const entry of readdirSync(currentDir, { withFileTypes: true })) {
      const entryPath = path.join(currentDir, entry.name);
      if (entry.isDirectory()) {
        stack.push(entryPath);
      } else if (entry.isFile()) {
        files.push(entryPath);
      }
    }
  }

  return files;
}

function isTargetReleaseApk(candidate, target) {
  const normalized = candidate.replace(/\\/g, '/').toLowerCase();
  const flavor = target.flavor.toLowerCase();
  return normalized.includes(`/apk/${flavor}/release/`);
}

function isSignedReleaseApk(candidate) {
  const normalized = candidate.replace(/\\/g, '/').toLowerCase();
  return normalized.endsWith('.apk')
    && !normalized.endsWith('-unsigned.apk')
    && (normalized.includes('/release/') || normalized.endsWith('-release.apk'));
}

function resolveReleaseAndroidApkCandidates(candidates, inspectBadging, expectedVersionName) {
  if (!Array.isArray(candidates) || candidates.length === 0) {
    throw new Error('Expected signed Android release APK candidates.');
  }
  if (typeof inspectBadging !== 'function') {
    throw new Error('Android APK badging inspector must be a function.');
  }
  if (typeof expectedVersionName !== 'string' || expectedVersionName.trim() !== expectedVersionName
      || expectedVersionName.length === 0) {
    throw new Error('Expected Android versionName must be a non-empty project version.');
  }
  const expectedVersionCode = computeTauriAndroidVersionCode(expectedVersionName);

  const badgingByCandidate = candidates.map((candidate) => ({
    candidate,
    badging: inspectBadging(candidate),
  }));
  const inspected = badgingByCandidate.map(({ candidate, badging }) => {
    const matchingTargets = releaseApkTargets.filter((target) => isTargetReleaseApk(candidate, target));
    if (matchingTargets.length !== 1) {
      throw new Error(`Unconsumed signed Android release APK: ${candidate}`);
    }

    const target = matchingTargets[0];
    assertAndroidApkIdentity(badging, {
      applicationId: androidApplicationId,
      versionName: expectedVersionName,
      versionCode: expectedVersionCode,
      abi: target.abi,
    });
    return { target, apkPath: candidate };
  });

  return releaseApkTargets.map((target) => {
    const matches = inspected.filter((candidate) => candidate.target === target);
    if (matches.length !== 1) {
      throw new Error(
        `Expected exactly one signed ${target.abi} release APK, received ${matches.length}.`,
      );
    }
    return matches[0];
  });
}

function findAapt2Command() {
  const executableName = process.platform === 'win32' ? 'aapt2.exe' : 'aapt2';
  const executablePath = findAndroidBuildToolFile(executableName);
  if (!executablePath) {
    throw new Error(`Locked Android Build Tools ${androidToolchain.buildTools} does not contain ${executableName}.`);
  }
  return executablePath;
}

function dumpAndroidApkBadging(apkPath, aapt2Command) {
  const result = runCaptured(aapt2Command, ['dump', 'badging', apkPath], { cwd: repoRoot });
  return result.stdout;
}

function assertAndroidApkIdentity(output, expected) {
  if (!expected || typeof expected.applicationId !== 'string' || typeof expected.versionName !== 'string'
      || !Number.isInteger(expected.versionCode) || expected.versionCode <= 0
      || expected.versionCode > maximumAndroidVersionCode || typeof expected.abi !== 'string') {
    throw new Error('Expected Android APK identity is incomplete.');
  }

  const actual = parseAapt2Badging(output);
  if (actual.applicationId !== expected.applicationId) {
    throw new Error(
      `Android APK package mismatch: expected ${expected.applicationId}, received ${actual.applicationId}.`,
    );
  }
  if (actual.versionName !== expected.versionName) {
    throw new Error(
      `Android APK versionName mismatch: expected ${expected.versionName}, received ${actual.versionName}.`,
    );
  }
  if (actual.versionCode !== expected.versionCode) {
    throw new Error(
      `Android APK versionCode mismatch: expected ${expected.versionCode}, received ${actual.versionCode}.`,
    );
  }
  if (actual.nativeCodes.length !== 1 || actual.nativeCodes[0] !== expected.abi) {
    throw new Error(
      `Android APK native-code mismatch: expected exactly ${expected.abi}, received ${actual.nativeCodes.join(', ') || '<none>'}.`,
    );
  }
  return actual;
}

function parseAapt2Badging(output) {
  if (typeof output !== 'string') {
    throw new Error('Android APK badging output must be text.');
  }
  const lines = output.split(/\r?\n/u);
  const packageLines = lines.filter((line) => line.startsWith('package:'));
  if (packageLines.length !== 1) {
    throw new Error(`Expected exactly one Android package badging line, received ${packageLines.length}.`);
  }
  const nativeCodeLines = lines.filter((line) => line.startsWith('native-code:'));
  if (nativeCodeLines.length !== 1) {
    throw new Error(`Expected exactly one Android native-code badging line, received ${nativeCodeLines.length}.`);
  }

  const applicationId = extractSingleBadgingAttribute(packageLines[0], 'name');
  const rawVersionCode = extractSingleBadgingAttribute(packageLines[0], 'versionCode');
  const versionName = extractSingleBadgingAttribute(packageLines[0], 'versionName');
  if (!/^[1-9][0-9]*$/u.test(rawVersionCode)) {
    throw new Error('Android package versionCode must be a canonical positive decimal integer.');
  }
  const versionCodeValue = BigInt(rawVersionCode);
  if (versionCodeValue > BigInt(maximumAndroidVersionCode)) {
    throw new Error(`Android package versionCode exceeds ${maximumAndroidVersionCode}.`);
  }
  const versionCode = Number(versionCodeValue);
  const nativeCodePayload = nativeCodeLines[0].slice('native-code:'.length).trim();
  const nativeCodes = [...nativeCodePayload.matchAll(/'([^']*)'/gu)].map((match) => match[1]);
  const unparsedNativeCode = nativeCodePayload.replace(/'[^']*'/gu, '').trim();
  if (unparsedNativeCode || nativeCodes.some((abi) => abi.length === 0)) {
    throw new Error('Android native-code badging line is malformed.');
  }

  return { applicationId, versionName, versionCode, nativeCodes };
}

function computeTauriAndroidVersionCode(versionName) {
  if (typeof versionName !== 'string') {
    throw new Error('Android release version must be canonical X.Y.Z or X.Y.Z-preview.N.');
  }
  const match = /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-preview\.([1-9][0-9]*))?$/u.exec(versionName);
  if (!match) {
    throw new Error(`Android release version must be canonical X.Y.Z or X.Y.Z-preview.N: ${versionName}`);
  }

  const major = BigInt(match[1]);
  const minor = BigInt(match[2]);
  const patch = BigInt(match[3]);
  if (minor > 999n || patch > 999n) {
    throw new Error('Android release minor and patch versions must each be between 0 and 999.');
  }

  const versionCode = (major * 1_000_000n) + (minor * 1_000n) + patch;
  if (versionCode < 1n || versionCode > BigInt(maximumAndroidVersionCode)) {
    throw new Error(
      `Android versionCode must be between 1 and ${maximumAndroidVersionCode}; received ${versionCode}.`,
    );
  }
  return Number(versionCode);
}

function extractSingleBadgingAttribute(line, attributeName) {
  const expression = new RegExp(`(?:^|\\s)${attributeName}='([^']*)'(?=\\s|$)`, 'gu');
  const matches = [...line.matchAll(expression)].map((match) => match[1]);
  if (matches.length !== 1 || matches[0].length === 0) {
    throw new Error(`Expected exactly one non-empty Android package ${attributeName} attribute.`);
  }
  return matches[0];
}

function findApkSignerCommand() {
  const apksignerJar = findAndroidBuildToolFile(path.join('lib', 'apksigner.jar'));
  if (!apksignerJar) {
    throw new Error(`Locked Android Build Tools ${androidToolchain.buildTools} does not contain lib/apksigner.jar.`);
  }

  return {
    command: findJavaCommand(),
    args: ['-jar', apksignerJar],
  };
}

function findJavaCommand() {
  const javaHome = process.env.JAVA_HOME?.trim();
  const executableName = process.platform === 'win32' ? 'java.exe' : 'java';
  if (!javaHome) {
    throw new Error('JAVA_HOME must point to the locked Android Temurin JDK.');
  }

  const candidate = path.join(javaHome, 'bin', executableName);
  if (!existsSync(candidate)) {
    throw new Error(`Locked Android JDK executable does not exist: ${candidate}`);
  }
  return candidate;
}

function findAndroidBuildToolFile(relativePath) {
  const sdkRoot = process.env.ANDROID_HOME?.trim();
  if (!sdkRoot) return '';
  const candidate = path.join(sdkRoot, 'build-tools', androidToolchain.buildTools, relativePath);
  return existsSync(candidate) ? candidate : '';
}

function verifyAndroidApkSignature(apkPath, signer, expectedCertificateSha256) {
  const result = runCaptured(
    signer.command,
    [...signer.args, 'verify', '--verbose', '--print-certs', apkPath],
    { cwd: repoRoot },
  );
  assertApkSignerCertificateSha256(
    [result.stdout, result.stderr].filter(Boolean).join('\n'),
    expectedCertificateSha256,
  );
}

function assertApkSignerCertificateSha256(output, expectedCertificateSha256) {
  if (!/^[a-f0-9]{64}$/u.test(expectedCertificateSha256 ?? '')) {
    throw new Error('Locked Android signing certificate SHA-256 is invalid.');
  }
  const actualCertificateSha256 = extractApkSignerCertificateSha256(output);
  if (actualCertificateSha256 !== expectedCertificateSha256) {
    throw new Error(
      `Android APK signing certificate mismatch: expected ${expectedCertificateSha256}, received ${actualCertificateSha256}.`,
    );
  }
  return actualCertificateSha256;
}

function extractApkSignerCertificateSha256(output) {
  const matches = [...output.matchAll(
    /Signer\s+#\d+\s+certificate\s+SHA-256\s+digest:\s*([A-Fa-f0-9:]+)/gu,
  )].map((match) => match[1].replaceAll(':', '').toLowerCase());
  if (matches.length !== 1 || !/^[a-f0-9]{64}$/u.test(matches[0] ?? '')) {
    throw new Error(`Expected exactly one valid Android APK signer certificate, received ${matches.length}.`);
  }
  return matches[0];
}

function run(command, args, options) {
  console.log(`> ${command} ${args.join(' ')}`);
  const spawnOptions = {
    ...options,
    stdio: 'inherit',
    shell: false,
  };

  const result = shouldRunThroughWindowsCommandShell(command)
    ? spawnSync(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', `"${[command, ...args].map(quoteWindowsCommandArg).join(' ')}"`], spawnOptions)
    : spawnSync(command, args, spawnOptions);

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    throw new Error(`Command failed with exit code ${result.status}: ${command} ${args.join(' ')}`);
  }
}

function runCaptured(command, args, options) {
  const spawnOptions = {
    ...options,
    encoding: 'utf8',
    shell: false,
    windowsHide: true,
  };
  const result = shouldRunThroughWindowsCommandShell(command)
    ? spawnSync(process.env.ComSpec || 'cmd.exe', ['/d', '/s', '/c', `"${[command, ...args].map(quoteWindowsCommandArg).join(' ')}"`], spawnOptions)
    : spawnSync(command, args, spawnOptions);
  if (result.error) throw result.error;
  if (result.status !== 0) {
    const diagnostic = [result.stderr, result.stdout]
      .map((value) => value?.trim())
      .filter(Boolean)
      .join(' | ')
      .slice(0, 4096);
    throw new Error(
      `Command failed with exit code ${result.status}: ${command} ${args.join(' ')}`
        + (diagnostic ? `\n${diagnostic}` : ''),
    );
  }
  return result;
}

function shouldRunThroughWindowsCommandShell(command) {
  return process.platform === 'win32' && /\.(bat|cmd)$/iu.test(command);
}

function quoteWindowsCommandArg(value) {
  const text = String(value);
  if (text.length === 0) return '""';
  if (!/[\s"&()<>^|]/u.test(text)) return text;
  return `"${text.replace(/"/g, '\\"')}"`;
}

export {
  assertAndroidApkIdentity,
  assertApkSignerCertificateSha256,
  assertRealDirectory,
  assertSigningProperties,
  commitStagedAndroidApks,
  computeTauriAndroidVersionCode,
  decodeJavaPropertyValue,
  extractApkSignerCertificateSha256,
  parseAapt2Badging,
  parseProperties,
  pruneThenCommitAndroidApks,
  resolveReleaseAndroidApkCandidates,
  verifyAndroidApkSignature,
};
