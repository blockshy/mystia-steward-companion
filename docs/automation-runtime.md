# 自动化运行时

更新日期：2026-08-19

本文档只定义订单自动化从命令准入、开锅、跨帧跟踪到送达与评价的运行时安全边界。订单身份由 [运行时订单生命周期](runtime-order-lifecycle.md) 定义，运行时数据和厨具快照来源见 [运行时 Provider](runtime-provider.md)，HTTP 路由与设备协议见 [本地 API](local-api.md)，特殊场景策略见[特殊经营实现](special-business-implementation.md)。

## 安全模型

自动化不会把一次前端决定解释为整笔订单的永久授权。每个尚未提交的副作用都必须重新通过当前运行时状态、订单身份、主设备配置和自动化控制权门禁。不能确认结果时停止并保留现场，不猜测成功、不补偿、不重放非幂等操作。

自动化仅在以下条件同时成立时开始新动作：

- 夜间经营五个生命周期 Hook 全部就绪，且当前为同一 `Active` generation。
- 教学经营门禁允许执行。
- 请求来自当前主设备，并携带精确 authority revision。
- 请求方持有该 revision 的有效 automation control lease。
- 请求携带当前 automation command epoch。
- 普客或稀客订单捕获完整就绪，且请求 lifecycle 与 fresh 活动订单一致。
- 总控、对应订单组和所请求阶段的配置满足不变量。

直接送达料理或酒水的配置必须同时允许完成订单；无效组合在前端归一化、请求解析和 C# 副作用入口三处拒绝，不保留旧配置语义。

## 教学经营门禁

`RuntimeNightBusinessAutomationGate` 只在 Unity 主线程读取精确 `MonoSingleton<NightSceneDirector>.Instance.IsInTutorial`：

- 不使用第一天、新存档、场景名、对话、订单内容或挑战类型猜测教学状态。
- 单例、属性、线程或经营 generation 不可确认时 fail-closed，并暂停有效超时计时。
- 同一 generation 首次确认 `true` 后锁存到本场结束。
- 已确认进入教学经营时释放现有自动料理 job 的 Mod 所有权，保留游戏中的厨具和成品原状；它们不在离开教学后自动重接。
- 门禁状态通过 `/snapshot` 的 `nightBusinessAutomationAllowed`、
  `nightBusinessAutomationBlockReason` 和 `runtimeNightBusinessAutomationStatus` 发布给前端用于停止调度和显示原因，
  但所有后端命令入口和跨帧检查点仍须独立复核。

## 主设备配置与控制权

`RuntimeAutomationControlState` 只接受当前主设备的生效 profile。主设备、profile 或 authority revision 变化时：

1. 旧 automation lease 失效。
2. automation command epoch 单调推进，尚未开始的排队命令取消。
3. 已开始的 cooking job 保留，不删除、不退款、不清锅。
4. job 在下一未提交边界进入 `suspended-authority` 或 `suspended-configuration`。
5. 新主设备应用配置并取得匹配 revision 的 lease 后，从下一安全步骤继续。

料理送达、订单评价和血池地狱完整结算分别取得 `FoodDelivery`、`OrderEvaluation`、`YuumaSettlement` permit。permit 在一个原子原生副作用边界内持有控制锁；已经取得的边界完整结束后，后到的权威变化才能提交。尚未取得 permit 的边界必须观察新配置并暂停。

关闭总控、订单组或当前阶段与切换主设备使用同一种可恢复暂停语义。暂停期间不消耗 cooking stall、送达或评价 closeout 的有效超时预算。玩家在暂停期间取走或替换 job 所属成品，才转为手动交接。

古明地恋 full-feed 可以绕过其专用阶段配置，但不能绕过自动化总控、对应订单组、authority 或 lease。

## 命令与结构化结果

订单动作使用结构化 outcome：

- `progressed`：本次发生了可证明的送酒、开锅或其他阶段推进。
- `waiting`：现场暂不可执行但没有失败，例如厨具暂忙或等待原生完成。
- `completed`：料理交接或订单评价已由精确证据完成。
- `interrupted`：所有权丢失、控制器复用或已按规则转移现场，当前 job 不再继续原路径。
- `retryable-failure`：尚未跨越不确定提交点，可以有界重试。
- `blocked`：结果不确定或需要人工确认，禁止普通重试。
- `fatal`：请求本身或必要契约无效。
- `cancelled`：经营生命周期、command epoch 或明确终止边界取消了动作。

`stage`、`reasonCode`、`jobId` 与 `retryAfterMs` 是状态机输入；前端不得解析中文消息推断行为。`waiting` 和 `interrupted` 不清零已有阶段失败次数，只有真实进展才重置相应停滞状态。

## 厨具预约与开锅

物理厨具目录只接受当前完整 `AllCookers + LockedCookers` 快照。每个实体槽位由 controller index、非零原生 identity 和三维 grid position 共同标识。前端预约与后端开锅都使用同一身份：

- 锁定、关闭、忙碌、内容 mutation 未完成或状态不可读的控制器不可预约。
- 可用状态只有严格空闲，或已正常完成 `Extract`、结果为空但残留旧 `ChosenRecipe` 的已验证例外。
- 多类型控制器一轮只能预约一次，并优先保留能力更广的槽位。
- 请求在动作前和紧邻 `SetCook` 前两次 fresh 复核 index、identity、grid、能力、锁定状态、可用性和预约所有权。
- 任一漂移只返回局部等待，不扫描或改选另一控制器。

`SetCook` 是一次非幂等提交。成功返回后必须立即取得由 `RuntimeCookingGenerationTracker` 发布的同 controller `SetCook` generation、content revision 与原生配方身份，才能登记 `AutomationCookingJob`。HTTP 响应丢失不能导致第二次扣料或第二次开锅。

## 跨帧料理 Job

`AutomationCookingJob` 是 Mod 对一锅自动料理的唯一跨帧状态。它保存稳定标量身份和预约，不跨帧保存 `CookController` wrapper：

- job ID、经营 generation、订单 binding token 与执行目标。
- controller index、native identity、grid 和 cooking generation/content revision。
- 配方、料理、加料、锅次和特殊目标签名等不可变执行标量。
- 当前 phase/progress、结构化 outcome、控制状态、有效超时和有界清理 tracker。

每次轮询、送达、复位、availability 检查和 `AfterPlayerExtract` 前都从当前物理目录重新绑定同一控制器。`SetCook`、`Extract` 和 `Store` 的 prefix 先发布未完成 mutation，只有同一 revision 的成功 postfix 才标记完成；嵌套或迟到 postfix 不得覆盖更新状态。

同 generation 下由游戏原生完成料理并替换 `Result` 是正常推进。出现新的 `SetCook` generation 时以 `cooking-controller-reused` 中断；出现已确认的 `Extract`、`Store` 或稳定空闲且内容所有权丢失时以 `cooking-ownership-lost` 中断。两种情况都只释放 Mod 所有权，不操作当前内容。

进度停滞只累计前后观测都可推进的有效时间。控制暂停、断线、场景不可读和 controller 暂不可达不计时。phase 或 progress 真正前进才重置停滞钟；达到有界阈值后保留旧锅并进入人工确认，不自动重开。

## 送达、评价与 commit-once

每个不可逆调用都遵循同一规则：调用前 fresh 复核；调用后只接受精确回读或同步终态回执；异常且无法证明未提交时进入不确定栅栏，绝不重放。

- 酒水扣库与送达按当前订单、原生库存和最终字段逐步确认。
- 料理送达只接受 final setter 后同一 `Sellable` 对象出现在订单最终字段。
- `StoreFood` 一旦开始调用，即使抛异常也可能已执行前置写入；只有明确未提交才可重试。
- 厨具 cleanup 只在同 generation 下确认 `Phase == Idle`、`Result == null`、`ChosenRecipe == null` 后完成。
- controller lease 在人工交接、delivery cleanup 成功或 cleanup 明确终止后单调释放；评价回执可晚于 lease，但不得继续占锅。
- Mod 发起评价只接受同一次调用内发布、精确命中订单 lifecycle 的 `Evaluated` terminal receipt。订单消失或快照 `HasEvaluated` 只表示外部收敛，不能证明本次提交成功。

订单强身份与 terminal receipt 规则见 [运行时订单生命周期](runtime-order-lifecycle.md)。血池地狱、幽幽子和古明地恋的额外动作顺序只在[特殊经营实现](special-business-implementation.md)维护，本页不重复。

## 人工确认栅栏

不确定副作用、无法完成的清理、进度回退和其他明确安全错误按订单 lifecycle 登记有界、单调 sequence 的 safety barrier：

- barrier 使对应订单保持 `blocked`，不被总控、阶段开关、普通重试或无关快照变化清除。
- 前端必须展示独立待确认项，即使订单已从当前快照消失。
- 只有当前主设备、匹配 authority revision 且持有 automation lease 的客户端，才能按 exact sequence 确认。
- 确认只解除该订单截至指定事件的栅栏，不修改游戏对象，也不影响其他订单。
- 经营生命周期结束会退休该 generation 的 barrier。

## 明确禁止的旧路径

- 不提供 `/automation/cancel`、target cancellation 或 job cancellation 兼容路由；控制变化通过 authority、lease、epoch 和阶段 permit 处理。
- 不在一般订单路径扫描 manager 寻找替代 controller。
- 不通过送餐面板、按钮、托盘、协程或 `MoveNext` 模拟游戏 UI。
- 不自动退款、补扣、重放 `SetCook`、`StoreFood`、最终 setter 或评价。
- 不保存跨帧 IL2CPP 厨具或订单 wrapper。
- 不把玩家投掷能力、HUD 可见性或中文日志当作事务完成证据。

## 修改与验证

修改自动化控制、料理 job 或不可逆事务时至少运行：

```bash
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
dotnet run --project tests/night-business-automation-gate/NightBusinessAutomationGateSmoke.csproj -c Release
dotnet run --project tests/runtime-automation-control/RuntimeAutomationControlSmoke.csproj -c Release
dotnet run --project tests/runtime-order-terminal-receipt/RuntimeOrderTerminalReceiptSmoke.csproj -c Release
dotnet run --project tests/runtime-cooker-snapshot/RuntimeCookerSnapshotSmoke.csproj -c Release
corepack pnpm audit:automation
corepack pnpm audit:connection-recovery
```

`automation-cooking-job` 使用真实 Harmony/MonoMod 探针，应通过锁定的 .NET 6 容器入口运行：

```bash
corepack pnpm test:dotnet6-harmony
```

若修改特殊经营结算，再执行对应专项 smoke，并按 [IL2CPP / IDA 分析流程](il2cpp-analysis-workflow.md) 复核原生边界。
