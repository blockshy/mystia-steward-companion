# C# 推荐领域差分验证

本目录只运行离线纯计算。原 TypeScript 推荐引擎作为测试 oracle，生产路径不得调用它或在 C# 失败时回退到它。

在仓库根目录按 [本地开发](../../docs/local-development.md)准备当前分支锁定的 Node、依赖、.NET SDK 与 .NET 6 运行时后执行：

```powershell
dotnet build tests/csharp-recommendations/CSharpRecommendations.csproj -c Release -p:NuGetAudit=false
node tests/csharp-recommendations/differential.mjs
dotnet tests/csharp-recommendations/bin/Release/net6.0/CSharpRecommendations.dll --custom-modes
```

`differential.mjs` 同时执行 `tests/reference-client` 中固定迁移前提交的 TypeScript 基线和实际 ProjectReference 引用的 C# 业务程序集，逐用例比较完整 JSON 输出，包括候选顺序、主方案、预算、条件及中文解释；测试宿主另外验证 C# 未修改输入。核心搜索使用固定候选限额，随机组合使用固定种子，失败可以稳定复现。

覆盖标签压制与动态标签、地区普客覆盖、稀客加料搜索、必选/禁选材料、双标签可达性、目录与库存资格、预算、不付款、全部九项权重与四个预设、收藏/自定义/任务优先信号、特殊目标排序及古明地恋投食评分；另覆盖厨具协议、主方案策略和原始目录清洗。此处只验证纯函数等价性，不等同于 HTTP 契约、自动化事务或真实游戏验证。

集合字段在 fixture 中使用数组；oracle 恢复为 TypeScript `Set`，C# 使用相同数组语义。C# 数字采用双精度、显式 JS 舍入规则，禁止将生产错误伪装为成功的空推荐。

`node tests/csharp-recommendations/client-boundary-audit.mjs` 检查当前客户端没有旧算法、执行候选上传接口或 oracle 导入，并要求推荐使用可撤销页面订阅。完整串行入口为 `corepack pnpm audit:business`；旧 `audit:recommendations` 已不再是当前 package.json 的命令。订单与特殊经营 C# 对照见 [订单差分](../csharp-business-orders/README.md)，不能将冻结参考实现自身可运行替代当前程序集通过。

冻结参考只具有旧的稀客自定义配方模式，测试适配仅补齐当前 v2 的模式元数据；当前新增的料理/稀客互斥模式由 `--custom-modes` 直接验证真实 C# 领域程序集。该专项与客户端边界审计已纳入 `audit:business`。

性能拆分先运行 `node tests/csharp-business-orders/benchmark.mjs --write-fixture` 生成完全人工目录，再运行 `node tests/csharp-recommendations/benchmark.mjs`。后者分别报告食物搜索、酒水搜索与组合排序的冷计算耗时，不使用整轮命中缓存作为首轮性能指标。
