import { spawnSync, execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// 本入口只编译纯托管测试宿主、运行固定参考差分和模拟边界；不会启动游戏、连接游戏 API 或部署文件。
// 串行执行避免多个 MSBuild 同时覆盖共享业务程序集，也避免并行搜索使性能结果失真。
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
function run(command, args) {
  const result = spawnSync(command, args, { cwd: root, stdio: 'inherit', windowsHide: true });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`离线验证失败：${command} ${args.join(' ')}，exit=${result.status}`);
}

// 固定参考文件必须等于声明的 main 提交；测试不重生成 oracle，防止误把当前实现当作预期答案。
const manifest = JSON.parse(readFileSync(path.join(root, 'tests/reference/manifest.json'), 'utf8'));
for (const file of manifest.files) {
  const baseline = execFileSync('git', ['-c', `safe.directory=${root.replaceAll('\\', '/')}`, 'show', `${manifest.revision}:${file}`],
    { cwd: root, maxBuffer: 16 * 1024 * 1024, windowsHide: true }).toString('utf8').replaceAll('\r\n', '\n');
  const frozen = readFileSync(path.join(root, 'tests/reference', file), 'utf8').replaceAll('\r\n', '\n');
  if (baseline !== frozen) throw new Error(`参考文件偏离冻结提交：${file}`);
}
console.log(`PASS: ${manifest.files.length} 个参考文件与 main ${manifest.revision} 一致。`);

const projects = [
  ['csharp-recommendations', 'CSharpRecommendations', false],
  ['csharp-business-orders', 'CSharpBusinessOrders', false],
  ['csharp-automation', 'CSharpAutomationSmoke', true],
  ['csharp-business-host', 'CSharpBusinessHost', true],
  ['csharp-business-package', 'CSharpBusinessPackageSmoke', true],
  ['local-api-storage', 'LocalApiStorageSmoke', true],
  ['runtime-automation-control', 'RuntimeAutomationControlSmoke', true],
];
for (const [directory, name, smoke] of projects) {
  run('dotnet', ['build', `tests/${directory}/${name}.csproj`, '-c', 'Release', '-m:1', '-p:UseAppHost=false', '-p:NuGetAudit=false']);
  if (smoke) run('dotnet', [`tests/${directory}/bin/Release/net6.0/${name}.dll`]);
}
// 排队时租约自然到期不能靠“没有新快照”获得延迟执行权；这项模拟不触发真实副作用。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--lease-expiry']);
// 持续故障只撤销一次真实UI边界；恢复后再次故障仍须撤销，不能靠隐藏日志通过。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--ui-failure-transition']);
for (const directory of ['csharp-recommendations', 'csharp-business-orders', 'csharp-automation']) {
  run(process.execPath, [`tests/${directory}/differential.mjs`]);
}
run(process.execPath, ['tests/csharp-recommendations/client-boundary-audit.mjs']);
console.log('PASS: C# 业务离线门禁完成；这不代表 Unity/IL2CPP 或真实游戏测试通过。');
