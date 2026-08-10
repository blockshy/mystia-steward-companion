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

  const signedApks = findSignedApks();
  const apkSigner = findApkSignerCommand();

  const stagedApks = stageAndVerifyAndroidApks(signedApks, apkSigner);
  pruneThenCommitAndroidApks(stagedApks);

  console.log('');
  console.log(`Built ${signedApks.length} signed Android APKs.`);
}

function verifyBuildToolchain() {
  run(process.execPath, [buildToolchainCheck, 'tauri'], { cwd: repoRoot });
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

function stageAndVerifyAndroidApks(signedApksToStage, signer) {
  const stagingDir = mkdtempSync(path.join(distDir, '.android-apk-stage-'));

  try {
    const items = signedApksToStage.map((item) => {
      const stagedPath = path.join(stagingDir, item.target.assetName);
      copyFileSync(item.apkPath, stagedPath);
      run(signer.command, [...signer.args, 'verify', '--verbose', '--print-certs', stagedPath], { cwd: repoRoot });
      console.log(`Signed Android APK verified: ${item.apkPath}`);
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
        '  password=<keystore and key password>',
        '  storeFile=<absolute path to your release keystore>',
      ].join('\n'),
    );
  }

  const properties = parseProperties(readFileSync(keystorePropertiesPath, 'utf8'));
  const requiredKeys = ['keyAlias', 'storeFile'];
  if (!properties.password) {
    requiredKeys.push('storePassword', 'keyPassword');
  }

  const missingKeys = requiredKeys.filter((key) => !properties[key]);
  if (missingKeys.length > 0) {
    throw new Error(`Missing Android signing properties in ${keystorePropertiesPath}: ${missingKeys.join(', ')}`);
  }

  const storeFile = resolveStoreFile(properties.storeFile);
  if (!existsSync(storeFile)) {
    throw new Error(`Android signing keystore does not exist: ${storeFile}`);
  }
}

function parseProperties(content) {
  const properties = {};
  for (const rawLine of content.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith('#')) continue;

    const separatorIndex = line.search(/[:=]/);
    if (separatorIndex < 0) continue;

    const key = line.slice(0, separatorIndex).trim();
    const value = line.slice(separatorIndex + 1).trim();
    if (key) properties[key] = value;
  }

  return properties;
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

function findSignedApks() {
  if (!existsSync(apkOutputDir)) {
    throw new Error(`Android APK output directory was not generated: ${apkOutputDir}`);
  }

  const candidates = listFilesRecursive(apkOutputDir)
    .filter((filePath) => filePath.endsWith('.apk') && !filePath.endsWith('-unsigned.apk'));

  if (candidates.length === 0) {
    throw new Error(`No signed release APK found in ${apkOutputDir}`);
  }

  return releaseApkTargets.map((target) => {
    const matches = candidates
      .filter((candidate) => isTargetReleaseApk(candidate, target))
      .sort((left, right) => left.localeCompare(right));

    if (matches.length === 0) {
      const relativeCandidates = candidates.map((candidate) => path.relative(apkOutputDir, candidate));
      throw new Error(
        [
          `No signed ${target.abi} release APK found in ${apkOutputDir}`,
          'Generated APKs:',
          ...relativeCandidates.map((candidate) => `  - ${candidate}`),
        ].join('\n'),
      );
    }

    return {
      target,
      apkPath: matches[0],
    };
  });
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
  return normalized.includes(`/apk/${flavor}/release/`)
    || normalized.endsWith(`/app-${flavor}-release.apk`);
}

function findApkSignerCommand() {
  const apksignerJar = findAndroidBuildToolFile(path.join('lib', 'apksigner.jar'));
  if (apksignerJar) {
    return {
      command: findJavaCommand(),
      args: ['-jar', apksignerJar],
    };
  }

  return {
    command: findAndroidBuildToolExecutable('apksigner'),
    args: [],
  };
}

function findJavaCommand() {
  const javaHome = process.env.JAVA_HOME;
  const executableName = process.platform === 'win32' ? 'java.exe' : 'java';
  if (javaHome) {
    const candidate = path.join(javaHome, 'bin', executableName);
    if (existsSync(candidate)) return candidate;
  }

  return executableName;
}

function findAndroidBuildToolExecutable(toolName) {
  const sdkRoot = process.env.ANDROID_HOME || process.env.ANDROID_SDK_ROOT;
  const executableName = process.platform === 'win32' ? `${toolName}.bat` : toolName;

  return findAndroidBuildToolFile(executableName) || executableName;
}

function findAndroidBuildToolFile(relativePath) {
  const sdkRoot = process.env.ANDROID_HOME || process.env.ANDROID_SDK_ROOT;

  if (sdkRoot) {
    const buildToolsDir = path.join(sdkRoot, 'build-tools');
    if (existsSync(buildToolsDir)) {
      const versions = readdirSync(buildToolsDir, { withFileTypes: true })
        .filter((entry) => entry.isDirectory())
        .map((entry) => entry.name)
        .sort((left, right) => right.localeCompare(left, undefined, { numeric: true }));

      for (const version of versions) {
        const candidate = path.join(buildToolsDir, version, relativePath);
        if (existsSync(candidate)) return candidate;
      }
    }
  }

  return '';
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
  assertRealDirectory,
  commitStagedAndroidApks,
  pruneThenCommitAndroidApks,
};
