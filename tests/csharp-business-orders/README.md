# 订单领域完整 JSON 差分

`differential.mjs` 通过隔离的 `tests/reference-client` 基线运行原 TypeScript 规则，与纯 C# 结果逐项比较完整 JSON，并由测试宿主验证输入没有被修改。生产应用不会导入参考规则。来源是固定迁移前提交，不读取当前 `main` 分支的最新内容。

已明确变更的协议/展示语义只在测试适配边界处理：移除旧订单排序偏好、按固定点单时间整理顶层订单，以及血池地狱指定说明字段的中文文案。候选、评分、标签、库存条件及其余结果仍完整比较；不能任意改写 oracle 来匹配被测输出。

从仓库根目录运行：

```powershell
dotnet restore tests/csharp-business-orders/CSharpBusinessOrders.csproj --ignore-failed-sources -p:NuGetAudit=false
dotnet build tests/csharp-business-orders/CSharpBusinessOrders.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
node tests/csharp-business-orders/differential.mjs
```

夹具覆盖普通/稀客推荐、预算策略、收藏与自定义置顶、任务料理、特殊经营全部角色与阶段、普通精确目标、库存/厨具缺失、详细阻断原因、页面行截取、游戏 UI 身份及过期目标，以及固定随机种子的规则组合。若发现差异，会生成 `last-failure.json` 供本地排查；该文件不应提交。

页面投影缓存另以实际 C# 未缓存计算为参照：30 组输入变化、60 次完整 JSON 对比，覆盖查询、目录、运行时库存/厨具、偏好、收藏和自定义料理。连续重复输入必须命中最终页面投影，所有缓存共享 32 项/32 MiB 上限；无关的订单观察时间、送达状态和宿主许可不进入页面值，也不能让旧执行许可随缓存复用。

`benchmark.mjs` 使用完全人工生成的 163 个配方、61 种材料和 62 种酒水验证规模下的计算耗时，不访问游戏或存档。`--ts` 测冻结基线，默认测单个订单；`--full` 增加四订单场景；`--page` 分别报告页面冷计算与最终投影命中耗时；`--write-fixture` 只把完整负载输入写入忽略的 `temp/csharp-business-load-fixture.json`。应串行运行性能测试，避免并行构建/负载影响结果。

`--pressure` 连续运行 8 个不同语义需求，验证中间候选不能挤掉最终投影。重复输入及食材压力等价变化必须命中全部 8 桌、符合共同容量上限，分配低于冷轮的十分之一；库存变更结果与未缓存的同输入完整计算比较。该模式单独执行，不把耗时数字设为机器无关门槛。常规差分还覆盖食材压力阈值、无限/缺失/负数、可用集合和酒水数量变更，以及启用自定义配方的相同边界。

`node tests/csharp-business-orders/candidate-cache.mjs` 直接比较生产候选缓存和完整无缓存结果：仅另一侧顶层点单标签允许复用，本侧需求、顾客、特殊目标、选项、目录、排除和数量变化均验证失效。另在同一真实缓存内修改冷算及命中返回树，验证目录和加料节点与原输入、缓存及后续结果相互独立；此专项纳入标准业务门禁。

`node tests/csharp-business-orders/plan-materialization.mjs` 验证完整候选先重排后截断，任务/收藏的后位候选仍能进入最终方案，并检查公开 JSON 的独立所有权。上述差分、候选缓存和方案物化都纳入 `corepack pnpm audit:business`；大目录压力与计时扩展按需串行运行，具体入口以同目录脚本参数为准。
