/**
 * 用锁定 MSVC 编译纯 Win32 生产存储边界，仅在仓库 temp 的新建合成树运行。
 * 不启动 Flutter/游戏、不读取真实 AppData；真实 DPAPI 调用只处理本进程生成的测试文本。
 */
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { prepareWindowsToolchain, mergeWindowsEnvironment } from '../../scripts/flutter-windows-toolchain.mjs';
const root = fileURLToPath(new URL('../..', import.meta.url));
const prepared = prepareWindowsToolchain();
const environment = mergeWindowsEnvironment(process.env, prepared.environment);
const directory = mkdtempSync(path.join(root, 'temp/windows-portable-storage-'));
const bundle = path.join(directory, 'base');
mkdirSync(bundle, { recursive: true });
writeFileSync(path.join(directory, '.synthetic-native-storage-test'), 'synthetic-only');
const files = [
  'mystia-steward-companion-portable.exe', 'flutter_windows.dll', 'msvcp140.dll',
  'vcruntime140.dll', 'vcruntime140_1.dll', 'data/app.so', 'data/icudtl.dat',
  'data/flutter_assets/AssetManifest.bin', 'data/flutter_assets/FontManifest.json',
  'data/flutter_assets/NOTICES.Z', 'THIRD-PARTY-NOTICES.txt', 'native_assets.json',
].sort();
const content = 'synthetic payload\n';
const digest = createHash('sha256').update(content).digest('hex');
let manifest = 'mystia-portable-client-v1\n';
for (const name of files) {
  const destination = path.join(bundle, name);
  mkdirSync(path.dirname(destination), { recursive: true });
  writeFileSync(destination, content);
  manifest += digest + '\t' + Buffer.byteLength(content) + '\t' + name + '\n';
}
writeFileSync(path.join(bundle, 'portable-client-v1.manifest'), manifest);
function run(file, args) {
  const result = spawnSync(file, args, { cwd: root, env: environment, encoding: 'utf8', windowsHide: true, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error('Native portable-storage validation failed: ' + result.status);
}
const build = path.join(directory, 'build');
run(prepared.identity.cmakePath, ['-S', path.join(root, 'tests/windows-portable-storage'), '-B', build,
  '-G', prepared.identity.generator, '-A', 'x64']);
run(prepared.identity.cmakePath, ['--build', build, '--config', 'Release']);
run(path.join(build, 'Release/portable_storage_test.exe'), [directory]);
console.log('Native portable-storage evidence: ' + directory);
