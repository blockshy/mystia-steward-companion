import { spawnSync } from 'node:child_process';
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
run(process.execPath, ['tests/reference-client/freeze-reference.mjs']);

const projects = [
  ['csharp-recommendations', 'CSharpRecommendations', false],
  ['csharp-business-orders', 'CSharpBusinessOrders', false],
  ['csharp-automation', 'CSharpAutomationSmoke', true],
  ['csharp-business-host', 'CSharpBusinessHost', true],
  ['csharp-game-ui-targets', 'CSharpGameUiTargetsSmoke', true],
  ['csharp-business-package', 'CSharpBusinessPackageSmoke', true],
  ['local-api-storage', 'LocalApiStorageSmoke', true],
  ['runtime-automation-control', 'RuntimeAutomationControlSmoke', true],
  // 主线程采集复用与整段日志写入使用独立托管夹具，串行纳入同一离线门禁。
  ['runtime-snapshot-read-cache', 'RuntimeSnapshotReadCacheSmoke', true],
  ['runtime-game-media', 'RuntimeGameMediaSmoke', true],
  ['aggregate-mod-log-lifecycle', 'AggregateModLogLifecycleSmoke', true],
  // 隔离父/子测试进程验证环境继承，不启动游戏、管家或 Steam，不改变机器级配置。
  ['companion-process-environment', 'CompanionProcessEnvironmentSmoke', true],
  ['runtime-performance-diagnostics', 'RuntimePerformanceDiagnosticsSmoke', true],
  // 高频 UI 维护的身份失效、重绑和投影分配回归同样纳入门禁，不能只测计时器本身。
  ['runtime-order-highlight', 'RuntimeOrderHighlightSmoke', true],
  ['runtime-seat-highlight', 'RuntimeSeatHighlightSmoke', true],
  // 来源订单失效必须同时撤回各高亮表面；同帧关闭/重开不得枚举到待销毁的私有克隆。
  ['ui-pinning-runtime', 'UiPinningRuntimeSmoke', true],
  ['runtime-throw-delivery-order-highlight', 'RuntimeThrowDeliverOrderHighlightSmoke', true],
];
for (const [directory, name, smoke] of projects) {
  run('dotnet', ['build', `tests/${directory}/${name}.csproj`, '-c', 'Release', '-m:1', '-p:UseAppHost=false', '-p:NuGetAudit=false']);
  if (smoke) run('dotnet', [`tests/${directory}/bin/Release/net6.0/${name}.dll`]);
}
// 双模式新增行为不能由冻结的旧 TypeScript oracle 表达，使用生产领域程序集验证。
run('dotnet', ['tests/csharp-recommendations/bin/Release/net6.0/CSharpRecommendations.dll', '--custom-modes']);
// 排队时租约自然到期不能靠“没有新快照”获得延迟执行权；这项模拟不触发真实副作用。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--lease-expiry']);
// 持续故障只撤销一次真实UI边界；恢复后再次故障仍须撤销，不能靠隐藏日志通过。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--ui-failure-transition']);
// 真实游戏目标解析器覆盖 CSS #RRGGBB 到协议 RRGGBB 的转换，禁止用宽松解析替身遗漏边界。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--ui-colors']);
// 持续观察更新可以保留同作用域的只读结果，但不得把旧结果标为当前或恢复旧动作许可。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--display-continuity']);
// 完成时总有更新快照也必须推进只读展示，不能因此给旧结果执行权。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--completed-display']);
// 白天只读推荐不依赖夜间目标发布；真实生命周期/发布门禁仍拒绝非Active副作用及排队后切场。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--daytime-ui-lifecycle']);
// 合并突发输入只恢复最终快照；库存立即失效，通知后停止及在途通知均不得恢复执行资格。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--input-wake']);
// 相同原文复用不能冻结租约、自动化时间或返回可被后续绑定修改的共享结果。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--input-reuse']);
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--page-subscriptions']);
// 已进入主线程队列的自动化命令不得跨资源重读恢复；真实生产许可闭包接受相同门禁验证。
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--game-media-gate']);
for (const directory of ['csharp-recommendations', 'csharp-business-orders', 'csharp-automation']) {
  run(process.execPath, [`tests/${directory}/differential.mjs`]);
}
// 两侧独立候选只能忽略另一侧需求；冷算/命中值树均不得与输入或下一次结果共享可变节点。
run(process.execPath, ['tests/csharp-business-orders/candidate-cache.mjs']);
// 完整候选重排之后再截取；覆盖后位任务/收藏提升与公开可变 JSON 的独立所有权。
run(process.execPath, ['tests/csharp-business-orders/plan-materialization.mjs']);
// 人工大目录的八种需求覆盖整条命中消费链，防止只测求解缓存却遗漏编排/发布的大复制。
run(process.execPath, ['tests/csharp-business-orders/benchmark.mjs', '--write-fixture']);
run('dotnet', ['tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll', '--warm-pipeline']);
run(process.execPath, ['tests/csharp-recommendations/client-boundary-audit.mjs']);
// Flutter 的来源边界和连续展示由 tests/flutter-contracts 验证，不再运行旧 TS 页面 helper。
console.log('PASS: C# 业务离线门禁完成；这不代表 Unity/IL2CPP 或真实游戏测试通过。');
