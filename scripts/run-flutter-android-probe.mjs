#!/usr/bin/env node
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, readdirSync, writeFileSync, copyFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertLockedNode, checkedPath, checkInstalledFlutter, resolveFlutterCommand } from './flutter-toolchain.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const project = path.join(root, 'tests/flutter-android-probe');
const args = process.argv.slice(2);
if (args.length !== 2 && args.length !== 4 || args[0] !== '--sdk-root' || args.length === 4 && args[2] !== '--build-output') {
  throw new Error('Usage: node scripts/run-flutter-android-probe.mjs --sdk-root <locked-sdk> [--build-output <new-dir>]');
}
assertLockedNode();
const sdk = checkedPath(args[1]);
const versions = checkInstalledFlutter(sdk);
const lock = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json')));
if (!lock.flutterAndroid || Object.keys(lock.flutterAndroid).sort().join(',') !== 'androidGradlePlugin,kotlin' ||
    Object.values(lock.flutterAndroid).some((value) => typeof value !== 'string' || !/^\d+\.\d+\.\d+$/u.test(value))) throw new Error('Exact Flutter Android plugin versions are required in the root lock');
const env = { ...process.env, CI: 'true', FLUTTER_SUPPRESS_ANALYTICS: 'true', PUB_CACHE: path.join(root, 'temp/flutter-pub-cache') };
if (env.JAVA_HOME) env.PATH = `${path.join(env.JAVA_HOME, 'bin')}${path.delimiter}${env.PATH}`;
for (const name of ['FLUTTER_TOOL_ARGS', 'FLUTTER_ENGINE', 'FLUTTER_ENGINE_SRC_PATH', 'FLUTTER_PREBUILT_ENGINE_VERSION', 'FLUTTER_ROOT']) delete env[name];
function run(command, argv, capture = false, extra = {}) {
  const result = spawnSync(command, argv, { cwd: project, env, shell: false, windowsHide: true,
    encoding: 'utf8', stdio: capture ? ['ignore', 'pipe', 'pipe'] : 'inherit', maxBuffer: 16 * 1024 * 1024, ...extra });
  if (result.error || result.status !== 0) throw new Error(`${command}: ${result.error?.message ?? result.stderr ?? result.status}`);
  return result.stdout;
}
function flutter(argv) {
  const command = resolveFlutterCommand(sdk, argv);
  return run(command.command, command.args, false, { windowsVerbatimArguments: command.windowsVerbatimArguments });
}
const dart = path.join(sdk, 'bin/cache/dart-sdk/bin', process.platform === 'win32' ? 'dart.exe' : 'dart');
flutter(['pub', 'get', '--enforce-lockfile']);
const tempRoot = path.join(root, 'temp/flutter-android-pigeon');
mkdirSync(tempRoot, { recursive: true });
const generated = mkdtempSync(path.join(tempRoot, 'run-'));
run(dart, ['run', 'pigeon', '--input', 'pigeons/android_probe_api.dart', '--base_path', generated]);
const outputs = ['lib/generated/android_probe_api.g.dart', 'android/app/src/main/kotlin/com/tyukki/mystia/steward/companion/p0probe/AndroidProbeApi.g.kt'];
run(dart, ['format', path.join(generated, outputs[0])]);
for (const file of outputs) if (!readFileSync(path.join(project, file)).equals(readFileSync(path.join(generated, file)))) throw new Error(`Stale Pigeon output: ${file}`);
run(dart, ['format', '--output=none', '--set-exit-if-changed', 'lib', 'test', 'pigeons']);
flutter(['analyze', '--fatal-infos', '--no-pub']);
flutter(['test', '--no-pub']);
if (process.platform === 'linux') run('python3', ['-B', 'test_apk_audit.py']);
if (args.length === 4) {
  if (process.platform !== 'linux') throw new Error('This APK build/audit entry is explicitly Linux-only. Runtime uses the separate PowerShell driver.');
  const android = checkedPath(env.ANDROID_HOME ?? '');
  if (!env.ANDROID_HOME || env.ANDROID_SDK_ROOT !== android || !env.JAVA_HOME) throw new Error('Explicit matching Android SDK and locked JAVA_HOME are required');
  const java = spawnSync(path.join(env.JAVA_HOME, 'bin/java'), ['-XshowSettings:properties', '-version'], { encoding: 'utf8' });
  if (java.status !== 0 || !java.stderr.includes(`java.version = ${lock.android.jdkVersion}`) || !java.stderr.includes('java.vendor = Eclipse Adoptium')) throw new Error('JDK differs from lock');
  const ndk = readFileSync(path.join(android, 'ndk', lock.android.ndkPackage, 'source.properties'), 'utf8');
  if (!ndk.includes(`Pkg.Revision = ${lock.android.ndkRevision}`)) throw new Error('NDK differs from lock');
  const wrapper = readFileSync(path.join(project, 'android/gradle/wrapper/gradle-wrapper.properties'), 'utf8');
  if (!wrapper.includes(`gradle-${lock.android.gradle}-bin.zip`) || !wrapper.includes(`distributionSha256Sum=${lock.android.gradleDistributionSha256}`)) throw new Error('Gradle differs from lock');
  const output = checkedPath(args[3]); mkdirSync(output);
  const hash = (file) => createHash('sha256').update(readFileSync(file)).digest('hex');
  const sources = [];
  function scan(directory) {
    for (const item of readdirSync(directory, { withFileTypes: true })) {
      if (['build', '.dart_tool', '.gradle', '.idea'].includes(item.name)) continue;
      const file = path.join(directory, item.name);
      if (item.isDirectory()) scan(file);
      else if (item.isFile() && !item.name.endsWith('.iml') && !['local.properties', 'GeneratedPluginRegistrant.java', 'gradlew', 'gradlew.bat', 'gradle-wrapper.jar'].includes(item.name)) sources.push({ path: path.relative(root, file).replaceAll(path.sep, '/'), sha256: hash(file) });
    }
  }
  scan(project); scan(path.join(root, 'tests/flutter-network-probe/lib'));
  sources.push({ path: 'toolchain.lock.json', sha256: hash(path.join(root, 'toolchain.lock.json')) });
  sources.sort((a, b) => a.path.localeCompare(b.path, 'en'));
  const sourceDigest = createHash('sha256').update(JSON.stringify(sources)).digest('hex');
  flutter(['build', 'apk', '--release', '--split-per-abi', '--target-platform', 'android-arm,android-arm64,android-x64', '--no-pub', `--dart-define=MYSTIA_ANDROID_PROBE_SOURCE_DIGEST=${sourceDigest}`]);
  for (const source of sources) if (hash(path.join(root, source.path)) !== source.sha256) throw new Error(`Source changed during build: ${source.path}`);
  const buildTools = path.join(android, 'build-tools', lock.android.buildTools);
  const apks = [];
  for (const abi of ['armeabi-v7a', 'arm64-v8a', 'x86_64']) {
    const apk = path.join(project, `build/app/outputs/flutter-apk/app-${abi}-release.apk`);
    const metadata = run(path.join(buildTools, 'aapt2'), ['dump', 'badging', apk], true);
    if (!metadata.includes("package: name='com.tyukki.mystia.steward.companion.p0probe'") || !metadata.includes("minSdkVersion:'24'") || !metadata.includes("targetSdkVersion:'36'") || !metadata.includes(`native-code: '${abi}'`) || metadata.includes('application-debuggable')) throw new Error('Unexpected APK metadata');
    const manifestPermissions = run(path.join(buildTools, 'aapt2'), ['dump', 'permissions', apk], true);
    if (!manifestPermissions.includes("uses-permission: name='android.permission.INTERNET'") || manifestPermissions.includes('android.permission.ACCESS_LOCAL_NETWORK')) throw new Error('Target36 APK must declare INTERNET and must not declare ACCESS_LOCAL_NETWORK');
    const signature = run(path.join(env.JAVA_HOME, 'bin/java'), ['-jar', path.join(buildTools, 'lib/apksigner.jar'), 'verify', '--verbose', '--print-certs', apk], true);
    if (signature.toLowerCase().includes(lock.android.signingCertificateSha256)) throw new Error('Probe must never use the product signing key');
    run(path.join(buildTools, 'zipalign'), ['-c', '-P', '16', '4', apk]);
    const audit = JSON.parse(run('python3', [path.join(root, 'scripts/audit-flutter-android-apk.py'), apk, abi], true));
    copyFileSync(apk, path.join(output, path.basename(apk)));
    writeFileSync(path.join(output, `${abi}.audit.json`), `${JSON.stringify({ ...audit, metadata, manifestPermissions, signature }, null, 2)}\n`, { flag: 'wx' });
    apks.push(audit);
  }
  writeFileSync(path.join(output, 'build-evidence.json'), `${JSON.stringify({ schemaVersion: 1, createdAt: new Date().toISOString(), versions, android: lock.android, flutterAndroid: lock.flutterAndroid, sourceDigest, sources, apks, releaseSignedWithIsolatedTestKey: true, runtimeVerified: false }, null, 2)}\n`, { flag: 'wx' });
  console.log(`Android Release APKs and static audits: ${output}`);
}
