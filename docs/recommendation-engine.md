# 推荐引擎

更新日期：2026-10-10

本文说明料理与酒水推荐的输入、候选管线和唯一主执行方案契约。运行时快照如何产生见
[运行时数据提供器](runtime-provider.md)；自动化如何消费推荐见
[自动化运行时](automation-runtime.md)；游戏内 UI 如何消费同一目标见
[游戏界面辅助](game-ui-integration.md)。

## 职责边界

推荐引擎是纯数据决策层，负责：

- 将完整游戏运行时料理、食材、酒水和客人目录与当前状态组合为推荐输入。
- 对普客与稀客订单执行相同的硬约束检查，再生成可排序的料理、酒水和组合方案。
- 为每笔订单发布有序的 `executionPlans`，并定义唯一主执行方案。
- 把收藏、自定义推荐料理、已验证任务料理和用户排序偏好作为明确的优先级信号。
- 在没有可执行方案时给出与生产管线一致的阻塞诊断。

它不负责读取 Unity 对象、推进订单、调用自动化命令或修改游戏 UI。特殊经营的目标身份和阶段规则由运行时
提供器与自动化层确认，推荐引擎只消费已经验证的上下文。

## 数据入口

核心实现位于 `modules/companion-business/Domain/Recommendation/`：

- `RecommendationEngine.Tags.cs`：运行时与动态 Tag、压制和已验证任务料理信号。
- `RecommendationEngine.Candidates.cs`、`RecommendationEngine.SearchState.cs`：稀客候选与有限宽度加料搜索。
- `RecommendationEngine.Normal.cs`：普客覆盖推荐。
- `RecommendationEngine.Plans.cs`、`RecommendationEngine.Sort.cs`：组合、预算和排序。
- `RecommendationEngine.Koishi.cs`：恋的评分与预算计划纯函数。
- `RecommendationJson.cs`：只读输入、独立输出值树及跨语言数值语义。

订单和特殊经营消费侧位于：

- `modules/companion-business/Domain/Orders/`：稀客订单、阻塞诊断、展示行和普客详情。
- `modules/companion-business/Domain/Support/`：目录入口、收藏、自定义配方、厨具与主方案策略。
- `modules/companion-business/Domain/SpecialBusiness/`：特殊经营规则、评分与普客执行目标。
- `modules/companion-business/Domain/GameUi/`：消费同一主方案生成游戏 UI 目标。
- `modules/companion-business/Application/BusinessQueries.cs`：手动页面查询与订单输入装配。
- `mods/bepinex/src/LocalApi/LocalApiServer.Business.cs`：后台宿主、结果版本和查询缓存。

客户端的 `recommendation-engine/` 仅保留 API 类型、排序设置编辑和目录显示过滤，不包含候选算法。手动推荐、收藏预览、经营订单和自动化都使用上述 C# 实现。

解锁、库存、厨具、地点、订单 Tag、预算和当前经营上下文以运行时快照为准。快照或
必要目录不完整时必须停止对应决策，不能用部分数据猜测可执行性。

## 候选管线

推荐按固定顺序处理：

1. 解析订单身份、所需料理/酒水 Tag 和当前上下文。
2. 对料理与酒水执行解锁、库存、排除项、厨具、配方禁忌、预算等硬约束。
3. 为料理选择合法加料；重复基础材料仍各占槽位并计入成本。候选资格沿用运行时可用 ID、禁用与排除集合，库存数量参与资源压力，不在迁移中增加另一套数量硬过滤；库存恰为 `-1` 才按无限量处理。
4. 组合能完整覆盖当前订单的料理与酒水，形成 `executionPlans`。
5. 在硬约束之后应用任务、收藏、自定义料理和用户排序权重。
6. 将结果稳定排序，并把第一项确认为唯一主执行方案。

任何“优先”都不能绕过硬约束。诊断模式复用同一候选管线，只在最终方案为空时解释最先阻断的原因；它不应
另外维护一套宽松算法，否则界面解释会与实际行为分叉。

## 主执行方案契约

`executionPlans[0]` 是一笔订单唯一的 primary plan。以下消费者必须读取同一项：

- “经营中”页面展示的首选组合。
- 自动化首次锁定的料理、加料和酒水。
- 游戏界面辅助发布的食材、料理、酒水和厨具目标。

不得让各消费者自行从候选中再次挑选，也不得从展示行反推执行目标。若 primary plan 缺失、身份不完整或与
订单当前 lifecycle 不一致，消费者应 fail closed。

当前优先边界为：

1. 先满足全部硬约束。
2. 已验证且与当前订单唯一匹配的 ServeInWork 任务料理可置顶。
3. 启用且匹配的自定义推荐料理参与候选优先级。
4. 料理与酒水收藏可分别置顶。
5. 其余候选按用户的推荐排序 profile 加权排序。

“自动化仅使用收藏”会在既有合法方案中收窄自动化可消费的 primary plan，不会让收藏跳过库存、预算、厨具或
订单条件。任务信号的验证与生命周期见 [任务系统](missions.md)。

## 收藏与自定义料理

两类数据有明确且独立的存储职责：

- `favorites.json` 只保存料理和酒水收藏。
- `custom-recipes.json` 只保存自定义推荐料理、启用状态和条目顺序。

自定义料理必须进入标准候选管线；不能作为另一路径直接注入自动化或游戏内列表。页面操作通过本地 API 的
规范读写接口完成，原子存储、schema 和设备权威约束见 [本地 API](local-api.md)。

相关前端入口：

- `apps/companion/src/companion/domain/custom-recipes.ts`
- `apps/companion/src/companion/domain/favorites.ts`
- `apps/companion/src/companion/domain/favorite-management.ts`
- `apps/companion/src/companion/pages/ModCustomRecipesPanel.tsx`
- `apps/companion/src/companion/pages/ModFavoritesPanel.tsx`

## 后台计算与缓存

高成本推荐在 Mod 后台业务宿主中运行，HTTP 线程登记有界查询意图并读取缓存。目录、库存、排除项、偏好、动态标签和特殊目标的任何语义变化都必须使对应候选缓存失效；不能仅按对象引用、数组数量或快照时间戳复用结果。纯候选复用不省略当前订单代次、预算、状态、权限及执行目标绑定。

手动页面还缓存最终配对和排序投影，键包含完整查询、推荐运行时、目录、偏好、收藏和自定义料理。页面、订单及候选共用 32 项/32 MiB 的有界缓存；页面值只含料理/酒水展示行，不含快照版本、订单或执行许可。连续订单观察更新不会重复阻塞手动页面，库存等语义变化仍必须重算。

搜索内部使用不可变强类型中间状态，保留旧算法的 Beam 宽度 64、每配方最终 16 个候选、稳定顺序和两标签可达性代表。只为最终候选构造 JSON，不把每个扩展状态序列化再解析。重复排列去重必须保留原 Map 的首插位置与最后排列值，避免改变标签展示次序。

客户端串行轮询带版本的查询结果；旧响应仅可标记为过期展示，服务异常不得触发本地计算。离开页面时停止对应页面查询；经营计算与自动化是否允许由宿主当前控制权和游戏状态决定。

## 维护规则

- 新硬约束应进入共享管线，并同时覆盖结果与阻塞诊断。
- 新优先信号必须说明它位于硬约束之后的具体顺序。
- 新消费者只能消费 primary plan，不能建立局部选优规则。
- 新运行时字段必须先在协议和数据集入口归一化，不能散落在页面组件内解析。
- 变更特殊经营规则前同时检查 [运行时数据提供器](runtime-provider.md) 与
  [自动化运行时](automation-runtime.md) 的身份和阶段约束。

## 验证

实际 C# 纯函数与完整订单对照：

```powershell
dotnet build tests/csharp-recommendations/CSharpRecommendations.csproj -c Release
node tests/csharp-recommendations/differential.mjs
dotnet build tests/csharp-business-orders/CSharpBusinessOrders.csproj -c Release
node tests/csharp-business-orders/differential.mjs
node tests/csharp-recommendations/client-boundary-audit.mjs
```

`tests/reference` 固定改造前 main 的旧 TS 计算闭包。`audit:recommendations` 验证该历史基线；生产等价性必须由实际项目引用的 C# 差分证明。规模性能应另运行 `tests/csharp-business-orders/benchmark.mjs`，区分冷计算、缓存命中和库存/偏好改变后的重算，不能只报告热缓存速度。

收藏和自定义料理：

```bash
corepack pnpm audit:custom-recipes
dotnet run --project tests/local-api-storage/LocalApiStorageSmoke.csproj -c Release
```

涉及前端展示时追加 `corepack pnpm audit:ui`；涉及自动化或游戏内 UI 时运行对应专题测试。完整验证分层见
[验证指南](validation-guide.md)。
