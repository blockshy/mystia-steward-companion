# 订单领域完整 JSON 差分

`differential.mjs` 通过隔离的 `tests/reference` 基线运行原 TypeScript 规则，与纯 C# 结果逐项比较完整 JSON，并由测试宿主验证输入没有被修改。生产应用不会导入参考规则。

从仓库根目录运行：

```powershell
dotnet restore tests/csharp-business-orders/CSharpBusinessOrders.csproj --ignore-failed-sources -p:NuGetAudit=false
dotnet build tests/csharp-business-orders/CSharpBusinessOrders.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
node tests/csharp-business-orders/differential.mjs
```

夹具覆盖普通/稀客推荐、预算策略、收藏与自定义置顶、任务料理、特殊经营全部角色与阶段、普通精确目标、库存/厨具缺失、详细阻断原因、页面行截取、游戏 UI 身份及过期目标，以及固定随机种子的规则组合。若发现差异，会生成 `last-failure.json` 供本地排查；该文件不应提交。

页面投影缓存另以实际 C# 未缓存计算为参照：30 组输入变化、60 次完整 JSON 对比，覆盖查询、目录、运行时库存/厨具、偏好、收藏和自定义料理。连续重复输入必须命中最终页面投影，所有缓存共享 32 项/32 MiB 上限；无关的订单观察时间、送达状态和宿主许可不进入页面值，也不能让旧执行许可随缓存复用。

`benchmark.mjs` 使用完全人工生成的 163 个配方、61 种材料和 62 种酒水验证规模下的计算耗时，不访问游戏或存档。`--ts` 测冻结基线，默认测单个订单；`--full` 增加四订单场景；`--page` 分别报告页面冷计算与最终投影命中耗时；`--write-fixture` 只把完整负载输入写入忽略的 `temp/csharp-business-load-fixture.json`。应串行运行性能测试，避免并行构建/负载影响结果。
