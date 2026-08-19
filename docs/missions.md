# 任务系统

更新日期：2026-08-19

本文说明任务的只读运行时投影、伴随窗口状态和任务料理优先信号。运行时反射和数据完整性的一般原则见
[运行时数据提供器](runtime-provider.md)，诊断文件边界见 [可观测性](observability.md)。

## 职责边界

当前任务系统提供两份相互独立的业务投影：

- **活动任务**：只包含游戏当前 active 的任务，并投影为 `unverified`、`tracking` 或 `fulfilled`。
- **可接取任务**：被动观察当前日/永久 scheduler 来源，投影尚未 active 且满足已验证条件的任务。

Mod 不接受任务、不生成任务、不触发 scheduler node，也不调用任务推进 API。“可接取”只表示已观察到游戏
原生调度条件满足或正在进入原生启动过程，不表示 NPC 当前可见、可交互，也不保证玩家能在任意场景手动接取。

ServeInWork 任务料理信号是第三个内部只读投影，只用于推荐优先级；它不替代活动任务或可接取任务列表。

## 活动任务投影

活动任务的核心实现位于：

- `mods/bepinex/src/Save/RuntimeMissionLoadSeedParser.cs`
- `mods/bepinex/src/Save/RuntimeMissionDefinitionDiagnosticReader.cs`
- `mods/bepinex/src/Save/RuntimeMissionDiagnosticCapture.cs`
- `mods/bepinex/src/Save/RuntimeMissionDiagnosticState.cs`
- `mods/bepinex/src/Save/RuntimeTrackedMissionSnapshot.cs`

读档时先从有界保存数据建立任务 seed，再在精确初始化边界绑定当前运行时 identity。新任务只在游戏原生
`StartMission` 已完成列表插入后安排受控刷新。后续完成状态来自游戏自然更新的 tracking 数据；Mod 不主动
调用有副作用的生成或推进函数。

活动任务只在任务 definition、条件数组、当前任务 identity 和状态形态全部一致时发布：

- `unverified`：已确认 active，但当前条件状态尚无法安全验证。
- `tracking`：条件数量与定义一致，且尚未全部完成。
- `fulfilled`：条件数量与定义一致，且全部完成。

一次刷新中任一关键形态错误会使整轮结果 fail closed；不能混入上次的部分任务或把缺失条件当作未完成。
非 active 的保存历史不会进入业务投影。

## 可接取任务投影

来源捕获与分类位于：

- `mods/bepinex/src/Save/RuntimeAvailableMissionSourceCapture.cs`
- `mods/bepinex/src/Save/RuntimeScheduledMissionSourceReader.cs`
- `mods/bepinex/src/Save/RuntimeAvailableMissionCapture.cs`
- `mods/bepinex/src/Save/RuntimeAvailableMissionTriggerClassifier.cs`
- `mods/bepinex/src/Save/RuntimeAvailableMissionSnapshot.cs`

系统只读取当前日和永久调度桶，以及 `postMissions`、`postMissionsAfterPerformance`。当前正式投影支持
trigger type `0`、`1` 和 `5`：前两类是进入日间场景/地图的自动触发，type `5` 是满足精确羁绊等级条件的
候选。其他类型不会通过通用猜测发布。

四个 scheduler 边界 `ScheduleEvent`、`DismissEvent`、`FinishSchedulerNode` 和
`FinishSchedulerNodePost` 只更新被动来源及过渡状态。`GET /missions/available` 的每次读取都安排 Unity
主线程 fresh read，并用 `missionGeneration` 和单调 `sourceRevision` 隔离旧场景和迟到结果。

事件 label、任务 label、pre-node 和 finished history 均按精确 Ordinal identity 处理；不 `Trim`、不建立
别名，也不把重复历史折叠成新的业务事实。任务必须同时通过定义、前置节点、active、finished、looped 和
receiver/presentation 等门禁才能发布。

## API 与前端

本地 API 发布两个独立端点：

- `GET /missions/tracked`
- `GET /missions/available`

两者都使用规范内容签名和 `knownSignature` 的 unchanged 响应。端点协议和认证见
[本地 API](local-api.md)。

前端读取入口为：

- `apps/companion/src/companion/hooks/useTrackedMissions.ts`
- `apps/companion/src/companion/hooks/useAvailableMissions.ts`
- `apps/companion/src/companion/pages/ModMissionListPanel.tsx`
- `apps/companion/src/companion/tracked-missions.ts`
- `apps/companion/src/companion/available-missions.ts`
- `apps/companion/src/companion/mission-presentation.ts`

任务列表模块默认关闭，并把启用状态保存在当前客户端的 localStorage。只有模块开启且“扩展功能 -> 任务列表”
可见时才读取和轮询两个端点；关闭或离开页面时取消请求。这个开关只控制列表流量，不关闭后端共享的被动任务
捕获，也不关闭任务料理优先信号。

页面按 label 合并两份投影；同 label 已成为活动任务时，活动任务覆盖可接取行，完成原生
`triggering -> tracked` 交接。筛选页签固定为“全部 / 可接取 / 可完成 / 进行中 / 待确认”，并使用稳定排序。
任务标题、角色名、相关场景和 receiver 等展示信息来自同一严格 presentation reader，不能由 label 文本猜测。

## ServeInWork 任务料理

`RuntimeServeInWorkMissionDiagnosticCapture` 被动观察活动 ServeInWork 条件，
`RuntimeMissionRecipePriorityProjection` 只在以下条件同时成立时输出一个优先信号：

- 当前是普通经营且 generation 匹配。
- 活动任务、canonical 稀客身份和静态 definition 一致。
- 当前恰有一笔未送餐的匹配订单。
- 料理的 `foodId + recipeId` 唯一且可验证。

任务完成、移除、经营 Closing、身份歧义或信号失效时立即清除。推荐引擎仍需重新通过库存、预算、厨具等硬
约束；完整优先级见 [推荐引擎](recommendation-engine.md)。

## 维护规则

- 新任务来源必须有精确游戏类型、字段、Hook 和副作用审计，不接受通用反射扫描。
- 诊断 JSON 只用于定位，不能反向成为业务快照的数据源。
- 可接取与活动投影保持独立；不要用其中一份填补另一份缺失状态。
- 任何不确定状态应显式保留为 unavailable/unverified，不建立兼容性别名或文本回退。
- 变更游戏运行时读取前按 [IL2CPP 分析流程](il2cpp-analysis-workflow.md) 交叉验证反编译声明与实际 wrapper。

## 验证

后端按改动范围运行任务专项 smoke：

```bash
dotnet run --project tests/runtime-mission-load-seed/RuntimeMissionLoadSeedSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-definition/RuntimeMissionDefinitionSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-diagnostic/RuntimeMissionDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-scheduled-event-diagnostic/RuntimeScheduledEventDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-available-missions/RuntimeAvailableMissionsSmoke.csproj -c Release
dotnet run --project tests/runtime-serve-in-work-diagnostic/RuntimeServeInWorkMissionDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-tracked-missions/RuntimeTrackedMissionsSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-recipe-priority/RuntimeMissionRecipePrioritySmoke.csproj -c Release
```

协议、前端生命周期和 UI：

```bash
corepack pnpm audit:runtime-missions
corepack pnpm audit:runtime-missions:ui
```

完整验证分层见 [验证指南](validation-guide.md)。
