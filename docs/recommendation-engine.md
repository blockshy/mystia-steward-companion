# 推荐引擎

更新日期：2026-08-19

本文说明料理与酒水推荐的输入、候选管线和唯一主执行方案契约。运行时快照如何产生见
[运行时数据提供器](runtime-provider.md)；自动化如何消费推荐见
[自动化运行时](automation-runtime.md)；游戏内 UI 如何消费同一目标见
[游戏界面辅助](game-ui-integration.md)。

## 职责边界

推荐引擎是纯数据决策层，负责：

- 将静态料理、食材、酒水和稀客数据与当前运行时状态组合为 `RecommendationDataSet`。
- 对普客与稀客订单执行相同的硬约束检查，再生成可排序的料理、酒水和组合方案。
- 为每笔订单发布有序的 `executionPlans`，并定义唯一主执行方案。
- 把收藏、自定义推荐料理、已验证任务料理和用户排序偏好作为明确的优先级信号。
- 在没有可执行方案时给出与生产管线一致的阻塞诊断。

它不负责读取 Unity 对象、推进订单、调用自动化命令或修改游戏 UI。特殊经营的目标身份和阶段规则由运行时
提供器与自动化层确认，推荐引擎只消费已经验证的上下文。

## 数据入口

核心实现位于 `apps/companion/src/recommendation-engine/`：

- `types.ts`：推荐输入和中间类型。
- `tag-resolution.ts`、`dynamic-food-tags.ts`：运行时 Tag 解析。
- `rare-orders.ts`、`normal-coverage.ts`：订单候选与覆盖计算。
- `mission-recipe-priority.ts`：已验证任务料理优先信号。
- `sort-profile.ts`：排序目标和预设。
- `index.ts`：公共计算入口。

页面和运行时消费侧位于：

- `apps/companion/src/companion/domain/service-recommendations.ts`
- `apps/companion/src/companion/domain/primary-execution-plan.ts`
- `apps/companion/src/companion/domain/normal-order-details.ts`
- `apps/companion/src/companion/workers/order-recommendations.worker.ts`
- `apps/companion/src/companion/workers/page-recommendations.worker.ts`

静态目录只是候选基础；解锁、库存、厨具、地点、订单 Tag、预算和当前经营上下文以运行时快照为准。快照或
必要目录不完整时必须停止对应决策，不能用部分数据猜测可执行性。

## 候选管线

推荐按固定顺序处理：

1. 解析订单身份、所需料理/酒水 Tag 和当前上下文。
2. 对料理与酒水执行解锁、库存、排除项、厨具、配方禁忌、预算等硬约束。
3. 为料理选择合法加料，并按数量核算重复材料；库存 `-1` 仅表示无限库存，其他负数无效。
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

## Worker 与并发

高成本推荐在 Worker 中运行。数据集按语义签名缓存，而不是按对象引用或计数缓存；同计数但内容变化必须使
签名变化。每个 Worker 只保留正在计算的请求和最新待处理请求，迟到结果必须按请求身份丢弃。离开相应页面或
停用对应功能时，应停止轮询或终止不再需要的 Worker。

## 维护规则

- 新硬约束应进入共享管线，并同时覆盖结果与阻塞诊断。
- 新优先信号必须说明它位于硬约束之后的具体顺序。
- 新消费者只能消费 primary plan，不能建立局部选优规则。
- 新运行时字段必须先在协议和数据集入口归一化，不能散落在页面组件内解析。
- 变更特殊经营规则前同时检查 [运行时数据提供器](runtime-provider.md) 与
  [自动化运行时](automation-runtime.md) 的身份和阶段约束。

## 验证

推荐算法与主执行方案：

```bash
corepack pnpm audit:recommendations
```

收藏和自定义料理：

```bash
corepack pnpm audit:custom-recipes
dotnet run --project tests/local-api-storage/LocalApiStorageSmoke.csproj -c Release
```

涉及前端展示时追加 `corepack pnpm audit:ui`；涉及自动化或游戏内 UI 时运行对应专题测试。完整验证分层见
[验证指南](validation-guide.md)。
