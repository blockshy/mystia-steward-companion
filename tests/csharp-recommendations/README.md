# C# 推荐领域差分验证

本目录只运行离线纯计算。原 TypeScript 推荐引擎作为测试 oracle，生产路径不得调用它或在 C# 失败时回退到它。

在仓库根目录加载当前 `main` 锁定环境后执行：

```powershell
. ./local-docs/windows-handoff/Enter-CSharpRefactor.ps1
dotnet build tests/csharp-recommendations/CSharpRecommendations.csproj -c Release -p:NuGetAudit=false
node tests/csharp-recommendations/differential.mjs
```

`differential.mjs` 同时执行 `tests/reference` 中冻结的 main TypeScript 基线和实际 ProjectReference 引用的 C# 业务程序集，逐用例比较完整 JSON 输出，包括候选顺序、主方案、预算、条件及中文解释；测试宿主另外验证 C# 未修改输入。核心搜索使用固定候选限额，随机组合使用固定种子，失败可以稳定复现。

覆盖标签压制与动态标签、地区普客覆盖、稀客加料搜索、必选/禁选材料、双标签可达性、目录与库存资格、预算、不付款、全部九项权重与四个预设、收藏/自定义/任务优先信号、特殊目标排序及古明地恋投食评分；另覆盖厨具协议、主方案策略和原始目录清洗。此处只验证纯函数等价性，不等同于 HTTP 契约、自动化事务或真实游戏验证。

集合字段在 fixture 中使用数组；oracle 恢复为 TypeScript `Set`，C# 使用相同数组语义。C# 数字采用双精度、显式 JS 舍入规则，禁止将生产错误伪装为成功的空推荐。

`node tests/csharp-recommendations/client-boundary-audit.mjs` 检查当前客户端没有旧算法、Worker、执行候选上传接口或 oracle 导入。旧 `audit:recommendations` 是固定 TS 行为基线；订单与特殊经营 C# 对照见 `tests/csharp-business-orders`，不能将旧基线通过替代当前程序集通过。

性能拆分先运行 `node tests/csharp-business-orders/benchmark.mjs --write-fixture` 生成完全人工目录，再运行 `node tests/csharp-recommendations/benchmark.mjs`。后者分别报告食物搜索、酒水搜索与组合排序的冷计算耗时，不使用整轮命中缓存作为首轮性能指标。
