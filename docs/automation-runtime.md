# 自动化运行时

更新日期：2026-10-23

本文档只定义订单自动化从命令准入、开锅、跨帧跟踪到送达与评价的运行时安全边界。订单身份由 [运行时订单生命周期](runtime-order-lifecycle.md) 定义，运行时数据和厨具快照来源见 [运行时 Provider](runtime-provider.md)，HTTP 路由与设备协议见 [本地 API](local-api.md)，特殊场景策略见[特殊经营实现](special-business-implementation.md)。

## 安全模型

自动化不会把一次业务计算解释为整笔订单的永久授权。每个尚未提交的副作用都必须重新通过当前运行时状态、订单身份、主设备配置和自动化控制权门禁。不能确认结果时停止并保留现场，不猜测成功、不补偿、不重放非幂等操作。

推荐、动作阶段推进、重试和预约由 `modules/companion-business/Application/Automation` 的唯一 C# 编排器管理。它不引用 Unity/BepInEx，不持有游戏对象。`LocalApiServer.Business.cs` 在后台生成命令，通过现有主线程队列交给游戏适配层；Flutter 仅展示状态、维持主设备心跳及发送配置、重试和确认意图。

业务帧包含输入版本、快照签名与权威 revision。计算后、派发前及游戏队列开始执行前均检查当前性。排队命令同时绑定本次准入租约的截止时间：自然到期即拒绝，后续续约不能追认旧命令；该主线程检查不取得 authority/lease 锁。已生成但确定未提交的命令通过 `Abandon` 释放 pending 和预约，不记为副作用失败；已开始的命令仍等待明确结果，禁止超时重放。关闭宿主只取消并撤销版本，不让 Unity 主线程等待可能依赖它的后台任务。

自动化仅在以下条件同时成立时开始新动作：

- 夜间经营五个生命周期 Hook 全部就绪，且当前为同一 `Active` generation。
- 教学经营门禁允许执行。
- 宿主观察到当前主设备在线，并携带精确 authority revision。
- 当前主设备持有该 revision 的有效 automation control lease；后台不能为它续约。
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
  `nightBusinessAutomationBlockReason` 和 `runtimeNightBusinessAutomationStatus` 发布给业务核心用于停止调度，并由前端显示原因，
  但所有后端命令入口和跨帧检查点仍须独立复核。

## 主设备配置与控制权

“扩展 → 自动化”的总开关和分项属于共享 profile，重启后沿用已保存配置；不存在“允许本次客户端驱动自动化”的临时开关。客户端只在握手成功、资源就绪、主设备配置已保存并确认、连接有效且未暂停时，依据总开关申请或续期租约。白天可持有租约等待经营，持有租约本身不代表已经满足夜间执行条件。

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

两项恢复上限在普客与稀客表单中共享同一配置值，但按每笔订单分别计数：

- “同一步骤失败上限（共享）”对应 `autoMaxStepRetries`，范围 1–10、默认 3。包含第一次失败；仅在对应订单组启用“出错暂停”时因失败次数达到上限而暂停，不把等待厨具记作失败。
- “重新安排次数上限（共享）”对应 `autoMaxRollbacks`，范围 0–5、默认 2。控制已确认可恢复中断后的重新规划次数，达到上限即暂停，0 表示不自动重新安排；不是存档回滚、材料退款或撤销已提交动作。安全入箱的计数与恢复见下文。

人工安全屏障不受这两项普通恢复设置放宽，也不会因为调高上限或重新开启总控而自动解除。

## 厨具预约与开锅

普客开锅前按目录中的精确 `ingredientIds` 加上实际目标的 `extraIngredientIds` 核对当前库存，重复材料按实际份数累计，`-1` 表示无限库存。未知或缺失材料信息不能视为足够；特殊经营核对实际执行配方，不用原点料理替代。缺料订单显示具体材料原因，不占厨具、开锅并发及为普客保留的跨周期槽位；其他可行普客/稀客仍继续。库存补足后自然恢复准入，已有成品和可独立送达的酒水不因开锅缺料而被撤销。主线程真正扣料前仍复核库存和身份。

开启聚合日志后，每五秒记录一次 `automation / Scheduling state`：当前授权/推荐状态、有界订单等待/暂停/任务事实、厨具可用性和本周期预约。最多 16 个订单、32 个厨具，超额订单明确计数；不保存目录、候选、凭据或原生指针，也不修改调度状态。日志用于区分材料不足、任务暂停与真正的厨具不可用，不能据此恢复执行许可。

物理厨具目录只接受当前完整 `AllCookers + LockedCookers` 快照。每个实体槽位由 controller index、非零原生 identity 和三维 grid position 共同标识。C# 业务预约与游戏适配层开锅都使用同一身份：

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

## 安全入箱后的有限回退

成品不符（包括已确认的黑暗料理 `foodId=-1`）只有在入箱已确认提交、厨具已严格复位并释放该精确锅次后，才产生可恢复终态。编排器按订单 lifecycle、job 与事件 sequence 对账；同一事件重复出现在快照中不会重复计数。一次安全回退消耗一次现有 `autoMaxRollbacks` 预算，默认上限为 2；还有预算时等待下一轮重新核对订单、库存、厨具和执行许可，达到上限则暂停。厨具变空闲或等待更长时间都不会自动重开预算。

暂停详情显示最新终态原因及回退上限，取代旧的“制作中”说明。在“经营 → 自动化状态”中检查原因后，当前有执行权限的主设备可点击“重试暂停订单”，重新开放该精确订单的普通回退预算；操作本身不立即开锅。入箱或复位不确定的人工安全屏障不能通过此按钮解除，仍须按下节规则核对现场并精确确认。安全入箱不说明黑暗料理的生成原因，也不保证下一锅一定成功。

## 人工确认栅栏

不确定副作用、无法完成的清理、进度回退和其他明确安全错误按订单 lifecycle 登记有界、单调 sequence 的 safety barrier：

- barrier 使对应订单保持 `blocked`，不被总控、阶段开关、普通重试或无关快照变化清除。
- 前端必须展示独立待确认项，即使订单已从当前快照消失。
- 只有当前主设备、匹配 authority revision 且持有 automation lease 的客户端，才能按 exact sequence 确认；新版请求体同时绑定 `protocolVersion=2`、`automationSessionId` 和 `businessGeneration`，不能用旧会话中相同序号确认新事件。健康检查中的对应能力字段为 `clientProtocolVersion=2`。
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
corepack pnpm audit:business
node scripts/flutter-workspace.mjs --sdk-root <锁定SDK绝对路径> --task contracts-test
```

完整 Dart 契约测试须先按[验证指南](validation-guide.md)构建真实 HTTP 隔离宿主；断线、身份、租约与旧会话确认边界由该入口验证。

`automation-cooking-job` 使用真实 Harmony/MonoMod 探针，应通过锁定的 .NET 6 容器入口运行：

```bash
corepack pnpm test:dotnet6-harmony
```

若修改特殊经营结算，再执行对应专项 smoke，并按 [IL2CPP / IDA 分析流程](il2cpp-analysis-workflow.md) 复核原生边界。
