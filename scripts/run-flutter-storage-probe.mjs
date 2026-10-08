#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertLockedNode, checkedPath, checkInstalledFlutter, resolveFlutterCommand } from './flutter-toolchain.mjs';
import { mergeWindowsEnvironment, prepareWindowsToolchain, verifyProjectToolchain } from './flutter-windows-toolchain.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const project = path.join(root, 'tests/flutter-storage-probe');
const args = process.argv.slice(2);
if (![2, 6].includes(args.length) || args[0] !== '--sdk-root' ||
    args.length === 6 && (args[2] !== '--build' || !['android', 'windows'].includes(args[3]) || args[4] !== '--output')) {
  throw new Error('Usage: --sdk-root <locked-sdk> [--build android|windows --output <new-absolute-dir>]');
}
assertLockedNode();
const sdk = checkedPath(args[1]);
const versions = checkInstalledFlutter(sdk);
const lock = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json')));
let env = { ...process.env, CI: 'true', FLUTTER_SUPPRESS_ANALYTICS: 'true', PUB_CACHE: path.join(root, 'temp/flutter-pub-cache') };
const windowsTools = args[3] === 'windows' ? prepareWindowsToolchain() : null;
if (windowsTools) env = mergeWindowsEnvironment(env, windowsTools.environment);
let windowsBuildTools;
if (process.platform !== 'win32' && env.JAVA_HOME) env.PATH = `${path.join(env.JAVA_HOME, 'bin')}${path.delimiter}${env.PATH}`;
for (const name of ['FLUTTER_TOOL_ARGS', 'FLUTTER_ENGINE', 'FLUTTER_ENGINE_SRC_PATH', 'FLUTTER_PREBUILT_ENGINE_VERSION', 'FLUTTER_ROOT']) delete env[name];
function run(command, argv, capture = false, extra = {}) {
  const result = spawnSync(command, argv, { cwd: project, env, shell: false, windowsHide: true, encoding: 'utf8',
    stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit', maxBuffer: 16 * 1024 * 1024, ...extra });
  if (result.error || result.status !== 0) throw new Error(`${command}: ${result.error?.message ?? result.stderr ?? result.status}`);
  return result.stdout;
}
function flutter(argv) {
  const command = resolveFlutterCommand(sdk, argv);
  return run(command.command, command.args, false, { windowsVerbatimArguments: command.windowsVerbatimArguments });
}
const dart = path.join(sdk, 'bin/cache/dart-sdk/bin', process.platform === 'win32' ? 'dart.exe' : 'dart');
flutter(['pub', 'get', '--enforce-lockfile']);
const generatedRoot = path.join(root, 'temp/flutter-storage-generated'); mkdirSync(generatedRoot, { recursive: true });
const generated = mkdtempSync(path.join(generatedRoot, 'run-'));
run(dart, ['run', 'pigeon', '--input', 'pigeons/storage_probe_api.dart', '--base_path', generated]);
const outputs = ['lib/generated/storage_probe_api.g.dart', 'android/app/src/main/kotlin/com/tyukki/mystia/steward/companion/storagep0/StorageProbeApi.g.kt'];
run(dart, ['format', path.join(generated, outputs[0])]);
for (const file of outputs) if (!readFileSync(path.join(generated, file)).equals(readFileSync(path.join(project, file)))) throw new Error(`Stale Pigeon: ${file}`);
run(dart, ['format', '--output=none', '--set-exit-if-changed', 'lib', 'test', 'pigeons']);
flutter(['analyze', '--fatal-infos', '--no-pub']);
flutter(['test', '--no-pub']);
if (args.length === 2) process.exit(0);

const platform = args[3];
if (platform === 'windows' && process.platform !== 'win32' || platform === 'android' && process.platform !== 'linux') throw new Error('Unsupported build host');
const output = checkedPath(args[5]);
if (existsSync(output)) throw new Error('Output must be new');
const gitSha = run('git', ['rev-parse', 'HEAD'], true).trim();
const clean = () => run('git', ['status', '--porcelain=v1', '--untracked-files=all'], true).trim() === '';
const cleanCheckout = clean();
if (!/^[a-f0-9]{40}$/u.test(gitSha) || platform === 'windows' && !cleanCheckout) throw new Error('Windows build requires clean committed checkout');
const hash = file => createHash('sha256').update(readFileSync(file)).digest('hex');
const sources = [];
function scan(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    if (['build', '.dart_tool', '.gradle', '.idea', 'ephemeral'].includes(entry.name)) continue;
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) scan(file);
    else if (entry.isFile() && !entry.name.endsWith('.iml') && !['local.properties', '.flutter-plugins-dependencies', 'GeneratedPluginRegistrant.java', 'gradlew', 'gradlew.bat', 'gradle-wrapper.jar'].includes(entry.name)) {
      sources.push({ path: path.relative(root, file).replaceAll(path.sep, '/'), sha256: hash(file) });
    }
  }
}
scan(project);
for (const file of ['toolchain.lock.json', '.gitattributes', 'scripts/run-flutter-storage-probe.mjs', 'scripts/flutter-windows-toolchain.mjs', 'scripts/audit-flutter-android-apk.py']) sources.push({ path: file, sha256: hash(path.join(root, file)) });
sources.sort((a, b) => a.path.localeCompare(b.path, 'en'));
const sourceDigest = createHash('sha256').update(JSON.stringify(sources)).digest('hex');
const defines = [`--dart-define=MYSTIA_STORAGE_SOURCE_DIGEST=${sourceDigest}`, `--dart-define=MYSTIA_STORAGE_GIT_SHA=${gitSha}`];
mkdirSync(output);
if (platform === 'android') {
  const android = checkedPath(env.ANDROID_HOME ?? '');
  if (!env.JAVA_HOME || !env.ANDROID_HOME || env.ANDROID_SDK_ROOT !== android) throw new Error('Explicit locked JDK/Android SDK required');
  const java = spawnSync(path.join(env.JAVA_HOME, 'bin/java'), ['-XshowSettings:properties', '-version'], { encoding: 'utf8' });
  if (java.status !== 0 || !java.stderr.includes(`java.version = ${lock.android.jdkVersion}`)) throw new Error('JDK differs from root lock');
  if (!readFileSync(path.join(android, 'ndk', lock.android.ndkPackage, 'source.properties'), 'utf8').includes(`Pkg.Revision = ${lock.android.ndkRevision}`)) throw new Error('NDK differs from root lock');
  const wrapper = readFileSync(path.join(project, 'android/gradle/wrapper/gradle-wrapper.properties'), 'utf8');
  if (!wrapper.includes(`gradle-${lock.android.gradle}-bin.zip`) || !wrapper.includes(`distributionSha256Sum=${lock.android.gradleDistributionSha256}`)) throw new Error('Gradle differs from root lock');
  flutter(['build', 'apk', '--release', '--split-per-abi', '--target-platform', 'android-arm64', '--no-pub', ...defines]);
  const apk = path.join(project, 'build/app/outputs/flutter-apk/app-arm64-v8a-release.apk');
  const buildTools = path.join(android, 'build-tools', lock.android.buildTools);
  const metadata = run(path.join(buildTools, 'aapt2'), ['dump', 'badging', apk], true);
  if (!metadata.includes("package: name='com.tyukki.mystia.steward.companion.storagep0'") || !metadata.includes("minSdkVersion:'24'") || !metadata.includes("targetSdkVersion:'36'") || !metadata.includes("native-code: 'arm64-v8a'") || metadata.includes('application-debuggable')) throw new Error('Wrong probe APK metadata');
  const signature = run(path.join(env.JAVA_HOME, 'bin/java'), ['-jar', path.join(buildTools, 'lib/apksigner.jar'), 'verify', '--verbose', '--print-certs', apk], true);
  if (signature.toLowerCase().includes(lock.android.signingCertificateSha256)) throw new Error('Production signing certificate forbidden');
  run(path.join(buildTools, 'zipalign'), ['-c', '-P', '16', '4', apk]);
  const alignment = JSON.parse(run('python3', [path.join(root, 'scripts/audit-flutter-android-apk.py'), apk, 'arm64-v8a'], true));
  copyFileSync(apk, path.join(output, 'mystia-storage-p0-arm64.apk'));
  const nativeBuildDependencies = { platform35: readFileSync(path.join(android, 'platforms/android-35/source.properties'), 'utf8'),
    cmake: readFileSync(path.join(android, 'cmake/3.22.1/source.properties'), 'utf8') };
  writeFileSync(path.join(output, 'apk-audit.json'), JSON.stringify({ metadata, signature, alignment, nativeBuildDependencies }, null, 2), { flag: 'wx' });
} else {
  flutter(['build', 'windows', '--release', '--no-pub', ...defines]);
  windowsBuildTools = verifyProjectToolchain(project, windowsTools);
  const bundle = path.join(project, 'build/windows/x64/runner/Release');
  function copyTree(from, to) {
    mkdirSync(to, { recursive: true });
    for (const entry of readdirSync(from, { withFileTypes: true })) {
      if (entry.isDirectory()) copyTree(path.join(from, entry.name), path.join(to, entry.name));
      else if (entry.isFile()) copyFileSync(path.join(from, entry.name), path.join(to, entry.name));
      else throw new Error('Unexpected bundle link');
    }
  }
  copyTree(bundle, output);
  const installation = windowsTools.identity.visualStudioPath;
  const crtVersion = windowsTools.identity.vcRuntimeVersion;
  const crtBase = path.join(installation, 'VC/Redist/MSVC', crtVersion, 'x64');
  const crt = readdirSync(crtBase).filter(name => /^Microsoft\.VC.*\.CRT$/u.test(name));
  if (crt.length !== 1) throw new Error('Expected one app-local CRT');
  for (const name of readdirSync(path.join(crtBase, crt[0])).filter(name => name.endsWith('.dll'))) {
    const source = path.join(crtBase, crt[0], name), destination = path.join(output, name);
    if (existsSync(destination) && hash(source) !== hash(destination)) throw new Error('Conflicting bundled runtime');
    copyFileSync(source, destination);
  }
  for (const name of ['mystia-steward-companion-storage-probe.exe', 'flutter_windows.dll', 'data/app.so', 'data/icudtl.dat']) if (!existsSync(path.join(output, name))) throw new Error(`Missing bundle ${name}`);
}
for (const source of sources) if (hash(path.join(root, source.path)) !== source.sha256) throw new Error(`Source changed during build: ${source.path}`);
if (platform === 'windows' && !clean()) throw new Error('Checkout changed during build');
const files = [];
function record(directory) {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) record(file);
    else files.push({ path: path.relative(output, file).replaceAll(path.sep, '/'), bytes: readFileSync(file).length, sha256: hash(file) });
  }
}
record(output);
writeFileSync(path.join(output, 'build-evidence.json'), `${JSON.stringify({ schemaVersion: 1, kind: 'flutter-storage-p0-bundle', gitSha, cleanCheckout, versions, platform, windowsBuildTools, sourceDigest, sources, files, runtimeVerified: false }, null, 2)}\n`, { flag: 'wx' });
console.log(`Storage probe bundle: ${output}`);
