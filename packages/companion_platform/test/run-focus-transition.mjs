import { spawnSync } from 'node:child_process';
import { mkdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { prepareWindowsToolchain, mergeWindowsEnvironment } from '../../../scripts/flutter-windows-toolchain.mjs';

// 原生纯决策回归也遵守根锁定的 MSVC/Windows SDK，不从 PATH 猜测编译器。
// 测试程序没有窗口、网络或真实游戏依赖，Release 下断言仍始终执行。
const root = fileURLToPath(new URL('../../..', import.meta.url));
const output = path.join(root, 'temp/focus-transition-test');
mkdirSync(output, { recursive: true });
const prepared = prepareWindowsToolchain();
const env = mergeWindowsEnvironment(process.env, prepared.environment);
const executable = path.join(output, 'focus-transition-test.exe');
for (const [command, args] of [
  [prepared.identity.compilerPath, ['/nologo', '/std:c++17', '/EHsc', '/W4', '/WX', '/utf-8', '/O2',
    path.join(root, 'packages/companion_platform/test/focus_transition_test.cpp'),
    `/Fe:${executable}`, `/Fo:${path.join(output, 'focus-transition-test.obj')}`]],
  [executable, []],
]) {
  const result = spawnSync(command, args, { cwd: output, env, shell: false, windowsHide: true, stdio: 'inherit' });
  if (result.error || result.status !== 0) throw new Error(`焦点回归失败：${result.error?.message ?? result.status}`);
}
