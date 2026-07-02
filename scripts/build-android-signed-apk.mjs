import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync } from 'node:fs';
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

assertSigningConfig();
cleanGeneratedAndroidSources();
rmSync(apkOutputDir, { recursive: true, force: true });
runTauriAndroidApkBuild();

const signedApks = findSignedApks();
const apkSigner = findApkSignerCommand();

mkdirSync(distDir, { recursive: true });
for (const item of signedApks) {
  run(apkSigner.command, [...apkSigner.args, 'verify', '--verbose', '--print-certs', item.apkPath], { cwd: repoRoot });
  const releaseAssetPath = path.join(distDir, item.target.assetName);
  copyFileSync(item.apkPath, releaseAssetPath);
  console.log(`Signed Android APK verified: ${item.apkPath}`);
  console.log(`Release asset copied to: ${releaseAssetPath}`);
}

console.log('');
console.log(`Built ${signedApks.length} signed Android APKs.`);

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
  const buildArgs = [
    'android',
    'build',
    '--apk',
    '--split-per-abi',
    '--target',
    ...releaseApkTargets.map((target) => target.target),
  ];
  if (tauriCliScript) {
    run(process.execPath, [tauriCliScript, ...buildArgs], { cwd: companionDir });
    return;
  }

  run('tauri', buildArgs, { cwd: companionDir });
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
