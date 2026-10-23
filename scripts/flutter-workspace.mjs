#!/usr/bin/env node
/**
 * Flutter 产品的唯一命令入口。SDK 必须由 --sdk-root 或 MYSTIA_FLUTTER_SDK_ROOT 显式选择；
 * 工具先核对 framework、engine 和 Dart 的精确身份，再运行请求的单一任务。
 * 本入口不安装 SDK、不启动游戏、不发布产物；失败保留完整退出码供 CI 判定。
 */
import { spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { assertLockedNode, checkInstalledFlutter, resolveFlutterCommand } from './flutter-toolchain.mjs';
import { mergeWindowsEnvironment, prepareWindowsToolchain, verifyProjectToolchain } from './flutter-windows-toolchain.mjs';
import { verifyWindowsRuntime } from './windows-runtime.mjs';

const root = fileURLToPath(new URL('..', import.meta.url));
const project = path.join(root, 'apps/companion-flutter');
const lock = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json'), 'utf8'));
const options = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  const key = process.argv[index];
  const value = process.argv[index + 1];
  if (!['--task', '--sdk-root'].includes(key) || !value || options.has(key)) throw new Error('用法：--sdk-root <SDK> --task get|analyze|test|format-check|generated-check|build-windows|build-android|verify');
  options.set(key, value);
}
const task = options.get('--task');
const sdk = options.get('--sdk-root') ?? process.env.MYSTIA_FLUTTER_SDK_ROOT;
if (!sdk || !task) throw new Error('必须明确提供 Flutter SDK 路径和任务。');
assertLockedNode();
checkInstalledFlutter(sdk);
let environment = { ...process.env, FLUTTER_SUPPRESS_ANALYTICS: 'true', CI: 'true' };

/** 以参数数组启动已校验工具；Windows Flutter 的批处理转义只由受测 helper 处理。 */
function flutter(args, workingDirectory = project) {
  const invocation = resolveFlutterCommand(sdk, args);
  const result = spawnSync(invocation.command, invocation.args, {
    cwd: workingDirectory, env: environment, stdio: 'inherit', shell: false,
    windowsHide: true, windowsVerbatimArguments: invocation.windowsVerbatimArguments,
  });
  if (result.error || result.status !== 0) throw new Error(`Flutter ${args.join(' ')} 失败：${result.error?.message ?? result.status}`);
}

/** 直接运行同一已校验 SDK 的 Dart，不通过 PATH 选择另一套工具。 */
function dart(args, cwd = root) {
  const executable = path.join(sdk, 'bin/cache/dart-sdk/bin', process.platform === 'win32' ? 'dart.exe' : 'dart');
  const result = spawnSync(executable, args, { cwd, env: environment, encoding: 'utf8', shell: false, windowsHide: true });
  if (result.stdout) process.stdout.write(result.stdout);
  if (result.stderr) process.stderr.write(result.stderr);
  if (result.error || result.status !== 0) throw new Error(`Dart ${args.join(' ')} 失败：${result.error?.message ?? result.status}`);
}

/** 格式门禁只检查当前产品与当前契约测试，不改写源码，也不扫描历史探针或构建缓存。 */
function checkFormat() {
  const directories = [
    'apps/companion-flutter/lib', 'apps/companion-flutter/test', 'apps/companion-flutter/integration_test',
    'packages/companion_protocol/lib', 'packages/companion_protocol/test',
    'packages/companion_client/lib', 'packages/companion_client/test',
    'packages/companion_platform/lib', 'packages/companion_platform/test', 'packages/companion_platform/pigeons',
    'tests/flutter-contracts/bin',
  ].filter((directory) => existsSync(path.join(root, directory)));
  dart(['format', '--output=none', '--set-exit-if-changed', ...directories]);
}

/**
 * Pigeon 一致性检查完全在新建 temp 目录执行。只复制输入、pubspec 与依赖锁，不复制任何输出，
 * 从零生成 Dart/C++/Kotlin 后对 Dart 做标准 format，再与生产文件逐字节比较。
 * 成败均保留隔离目录和 JSON 证据，绝不覆写生产生成文件或悄悄接受新的依赖解析。
 */
function checkGenerated() {
  const source = path.join(root, 'packages/companion_platform');
  mkdirSync(path.join(root, 'temp'), { recursive: true });
  const temporary = mkdtempSync(path.join(root, 'temp/pigeon-check-'));
  for (const directory of ['pigeons', 'lib/src', 'windows', 'android/src']) {
    mkdirSync(path.join(temporary, directory), { recursive: true });
  }
  for (const file of ['pubspec.yaml', 'pubspec.lock', 'pigeons/platform.dart']) {
    copyFileSync(path.join(source, file), path.join(temporary, file));
  }
  const before = readFileSync(path.join(temporary, 'pubspec.lock'));
  flutter(['pub', 'get', '--enforce-lockfile'], temporary);
  if (!readFileSync(path.join(temporary, 'pubspec.lock')).equals(before)) throw new Error('隔离生成修改了依赖锁。');
  dart(['run', 'pigeon', '--input', 'pigeons/platform.dart'], temporary);
  dart(['format', 'lib/src/platform.g.dart'], temporary);
  const outputs = ['lib/src/platform.g.dart', 'windows/platform.g.h', 'windows/platform.g.cpp', 'android/src/PlatformApi.g.kt'];
  const evidence = outputs.map((file) => ({ file, matches: readFileSync(path.join(source, file)).equals(readFileSync(path.join(temporary, file))) }));
  writeFileSync(path.join(temporary, 'comparison.json'), `${JSON.stringify({ result: evidence.every((item) => item.matches) ? 'passed' : 'failed', files: evidence }, null, 2)}\n`);
  const differences = evidence.filter((item) => !item.matches).map((item) => item.file);
  if (differences.length) throw new Error(`Pigeon 生成文件不一致：${differences.join(', ')}。隔离证据：${temporary}`);
  console.log(`Pigeon Dart/C++/Kotlin 生成一致性通过：${temporary}`);
}

/** Release 构建必须让 Flutter 刷新 mode 对应的原生插件注册表；--no-pub 会跳过此步骤。
 * 先强制锁文件解析，再验证构建后的锁原字节未变化，兼顾可复现依赖和正确的原生插件集合。
 */
function releaseBuild(args) {
  const lockPath = path.join(project, 'pubspec.lock'); const before = readFileSync(lockPath);
  flutter(['pub', 'get', '--enforce-lockfile']); flutter(['build', ...args]);
  if (!readFileSync(lockPath).equals(before)) throw new Error('Release 构建修改了 pubspec.lock，拒绝把未审查依赖打包。');
}

/** 纯 Dart 契约验收使用同一 SDK；完整模式连接隔离 C# 宿主，公共模式只运行传输与状态单元测试。 */
function contracts(unitOnly) {
  const dart = path.join(sdk, 'bin/cache/dart-sdk/bin', process.platform === 'win32' ? 'dart.exe' : 'dart');
  const cwd = path.join(root, 'tests/flutter-contracts');
  for (const args of [['pub', 'get', '--enforce-lockfile'], ['run', 'bin/verify.dart', ...(unitOnly ? ['--unit-only'] : [])]]) {
    const result = spawnSync(dart, args, { cwd, env: environment, stdio: 'inherit', windowsHide: true });
    if (result.error || result.status !== 0) throw new Error(`Dart 契约测试失败：${result.error?.message ?? result.status}`);
  }
}

switch (task) {
  case 'run': flutter(['run', '-d', 'windows']); break;
  case 'get': flutter(['pub', 'get', '--enforce-lockfile']); break;
  case 'analyze': flutter(['analyze', '--fatal-infos']); break;
  case 'test': flutter(['test']); break;
  case 'format-check': checkFormat(); break;
  case 'generated-check': checkGenerated(); break;
  case 'contracts-test': contracts(false); break;
  case 'contracts-unit': contracts(true); break;
  case 'verify': flutter(['analyze', '--fatal-infos']); flutter(['test']); break;
  // 公共 CI 无私有游戏参考 DLL，明确只跑单元测试；正式构建在受保护环境恢复参考包后跑 verify 全套。
  case 'verify-unit': flutter(['analyze', '--fatal-infos']); flutter(['test', '--exclude-tags', 'production-http']); break;
  case 'build-windows': {
    if (process.platform !== 'win32') throw new Error('Windows Release 必须在原生 Windows 工具链上构建。');
    const prepared = prepareWindowsToolchain(environment);
    environment = mergeWindowsEnvironment(environment, prepared.environment);
    releaseBuild(['windows', '--release']);
    verifyProjectToolchain(project, prepared);
    // 官方 ZIP 分发要求应用本地 VC 运行库；只从锁定 VS 的正式 Redist 读取并校验签名与摘要。
    const runtime = verifyWindowsRuntime(path.join(project, 'build/windows/x64/runner/Release'), prepared, { complete: true });
    const record = path.join(root, 'temp/flutter-build-evidence');
    mkdirSync(record, { recursive: true });
    writeFileSync(path.join(record, 'windows-toolchain.json'), `${JSON.stringify(prepared.identity, null, 2)}\n`);
    writeFileSync(path.join(record, 'windows-runtime.json'), `${JSON.stringify(runtime, null, 2)}\n`);
    break;
  }
  case 'build-android': {
    // Android 的 unsigned APK 虽然可以编译成功，却不能安装；因此在依赖解析/Gradle 前拒绝缺签名。
    // 动态加载只影响 Android 任务，不让 Android 配置缺口阻止 Windows 或纯 Dart 检查。
    const { assertSigningConfig, verifyReleaseBuildOutput } = await import('./build-android-signed-apk.mjs');
    assertSigningConfig();
    if (process.platform === 'win32') {
      // Windows 的长沙箱临时目录会超出 JDK Unix-domain socket 限制；只隔离本次进程环境。
      // 目录位于受管理工作区，不修改用户或系统 TEMP/JAVA 配置，也不复用其它项目的 daemon。
      const temporary = path.join(root, 'temp/javatmp'); mkdirSync(temporary, { recursive: true });
      environment.TEMP = temporary; environment.TMP = temporary;
      environment.JAVA_TOOL_OPTIONS = `${environment.JAVA_TOOL_OPTIONS ?? ''} -Djdk.net.unixdomain.tmpdir="${temporary}" -Djava.io.tmpdir="${temporary}"`.trim();
    }
    // Java 由调用环境显式传入，禁止用 PATH 的另一套 JDK 悄悄继续构建。
    if (!environment.JAVA_HOME) throw new Error(`请显式设置 JAVA_HOME 为锁定 Temurin ${lock.android.jdkVersion}。`);
    const java = path.join(environment.JAVA_HOME, 'bin', process.platform === 'win32' ? 'java.exe' : 'java');
    if (!existsSync(java)) throw new Error('JAVA_HOME 中没有 java 可执行文件。');
    const version = spawnSync(java, ['-version'], { encoding: 'utf8', windowsHide: true });
    if (version.status !== 0 || !`${version.stdout}${version.stderr}`.includes(lock.android.jdkVersion)) throw new Error('Android JDK 与根工具锁不一致。');
    releaseBuild(['apk', '--release', '--split-per-abi', '--target-platform', 'android-arm,android-arm64']);
    // 核对原 applicationId 与根锁证书，避免签错证书的包被误认为可覆盖已有正式安装。
    verifyReleaseBuildOutput();
    break;
  }
  default: throw new Error(`未知 Flutter 任务：${task}`);
}
