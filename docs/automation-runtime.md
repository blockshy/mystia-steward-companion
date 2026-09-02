# 自动化运行时

更新日期：2026-09-02

本文档只定义订单自动化从命令检查、开锅、跨帧跟踪到送达与评价的运行时安全边界。订单标识由[运行时订单生命周期](runtime-order-lifecycle.md)定义，调度名单内稀客的执行许可见[稀客调度与订单队列](rare-order-participation.md)，游戏数据和厨具快照来源见[游戏数据提供器](runtime-provider.md)，HTTP 路由与设备协议见[本地 API](local-api.md)，特殊场景策略见[特殊经营实现](special-business-implementation.md)。

## 安全模型

自动化不会把一次前端决定解释为整笔订单的永久许可。每个尚未提交且会改变游戏状态的操作，都必须重新检查当前游戏状态、订单标识、主设备配置和自动化控制权。不能确认结果时停止并保留现场，不猜测成功、不补偿，也不重复执行非幂等操作。

自动化仅在以下条件同时成立时开始新动作：

- 夜间经营五个生命周期 Hook 全部就绪，且当前仍是同一轮 `Active` 经营。
- 教学经营检查允许执行。
- 请求来自当前主设备，并携带精确匹配的配置修订号。
- 请求方持有该配置修订号对应的有效自动化控制权。
- 请求携带当前自动化命令轮次。
- 普客或稀客订单捕获完整就绪，且请求中的订单实例与最新读取的活动订单一致。
- 稀客调度模块开启且生效名单非空时，这笔稀客订单已进入当前队列；公开订单标识与共享配置或队列状态任一不一致即拒绝。
- 总控、对应订单组和所请求阶段的配置满足不变量。

直接送达料理或酒水的配置必须同时允许完成订单；无效组合在前端规范化、请求解析和 C# 游戏写入入口三处拒绝，不保留旧配置行为。

## 教学经营检查

`RuntimeNightBusinessAutomationGate` 只在 Unity 主线程读取精确 `MonoSingleton<NightSceneDirector>.Instance.IsInTutorial`：

- 不使用第一天、新存档、场景名、对话、订单内容或挑战类型猜测教学状态。
- 单例、属性、线程或经营轮次不可确认时拒绝执行，并暂停有效超时计时。
- 同一经营轮次首次确认 `true` 后固定保持到本场结束。
- 已确认进入教学经营时停止控制现有自动料理任务，保留游戏中的厨具和成品原状；它们不在离开教学后自动重新接管。
- 检查状态通过 `/snapshot` 的 `nightBusinessAutomationAllowed`、
  `nightBusinessAutomationBlockReason` 和 `runtimeNightBusinessAutomationStatus` 发布给前端用于停止调度和显示原因，
  但所有后端命令入口和跨帧检查点仍须独立复核。

## 主设备配置与控制权

`RuntimeAutomationControlState` 只接受当前主设备的生效配置。主设备、生效配置或配置修订号变化时：

1. 旧自动化控制权失效。
2. 自动化命令轮次单调推进，尚未开始的排队命令取消。
3. 已开始的料理任务保留，不删除、不退款、不清锅。
4. 任务在下一未提交步骤进入 `suspended-authority` 或 `suspended-configuration`。
5. 新主设备应用配置并取得匹配修订号的自动化控制权后，从下一项尚未提交的步骤继续。

料理送达、订单评价和血池地狱完整结算分别取得 `FoodDelivery`、`OrderEvaluation`、`YuumaSettlement` 执行许可。许可在一次不可分割的游戏写操作中持有控制锁；已经取得许可的操作完整结束后，后到的配置变化才能提交。尚未取得许可的操作必须采用新配置并暂停。

关闭总控、订单组或当前阶段与切换主设备使用同一种可恢复暂停规则。暂停期间不消耗料理停滞、送达或评价收尾的有效超时预算。玩家在暂停期间取走或替换任务所属成品，才转为手动交接。

古明地恋的完整投食流程可以绕过其专用阶段配置，但不能绕过自动化总控、对应订单组、主设备配置或自动化控制权检查。

## 稀客队列执行许可

稀客调度模块关闭或生效名单为空时不增加队列检查，原有候选顺序和动作保持不变。生效名单非空时，稀客动作还有两层许可：

- 排队准备和完成命令使用“经营轮次 + R 类追踪编号 + 订单实例序号 + 规范 `guestId`”取得准入许可。
- 游戏订单绑定成功后，料理送达、评价和特殊经营结算使用同一公开订单标识及精确的 `RuntimeOrderBindingToken` 取得游戏写入许可。

许可在同步游戏操作结束前持有；暂停变更必须等待已经取得许可的操作完成，然后推进命令轮次。未开始任务直接失效，已开锅任务保留原锅并进入 `suspended-participation`，不消耗暂停期间的有效超时，重新启用后从下一项尚未提交的步骤继续。

优先启用变更会在自动料理任务锁内筛出所有缓存为 `ControlState == active` 的稀客任务。
筛选只接受当前经营轮次、特殊订单类型、非零 `OrderBinding`，以及有效的 R 类追踪编号、订单实例序号和 `guestId`，并且只
传递不可变托管标识，不暴露或跨线程保存仍与游戏对象关联的 IL2CPP wrapper。释放任务锁后，调用方再用
请求对应的同一队列快照分类候选：只有当前且 `Participating` 的任务需要保持在新订单之前；当前但已经暂停的缓存任务
从该集合排除，并在成功日志中限制数量的 `suspendedActiveJobs` 列表里记录，等待后续轮询把任务转入 `suspended-participation`。
候选缺失、绑定或订单标识未知、重复、经营轮次不匹配，以及分类前后队列修订号发生变化时，都拒绝整次变更；
拒绝原因和候选任务 ID 只按数量上限记录。最终需要保持在前的订单、插入规则和写入前状态检查只在[稀客调度与订单队列](rare-order-participation.md)维护。

## 命令与结构化结果

订单动作使用结构化结果：

- `progressed`：本次发生了可证明的送酒、开锅或其他阶段推进。
- `waiting`：现场暂不可执行但没有失败，例如厨具暂忙或等待游戏完成当前动作。
- `completed`：料理交接或订单评价已经得到明确确认。
- `interrupted`：厨具控制归属变化、控制器复用或已按规则转移现场，当前任务不再继续原路径。
- `retryable-failure`：尚未跨越不确定提交点，可以有界重试。
- `blocked`：结果不确定或需要人工确认，禁止普通重试。
- `fatal`：请求本身或必要协议无效。
- `cancelled`：经营生命周期、命令轮次或明确的结束条件取消了动作。

`stage`、`reasonCode`、`jobId` 与 `retryAfterMs` 是状态机输入；前端不得解析中文消息推断行为。`waiting` 和 `interrupted` 不清零已有阶段失败次数，只有真实进展才重置相应停滞状态。

## 厨具预约与开锅

物理厨具目录只接受当前完整的 `AllCookers + LockedCookers` 快照。每个实体槽位由控制器索引、非零游戏对象标识和三维网格位置共同标识。前端预约与后端开锅都使用同一组标识：

- 锁定、关闭、忙碌、内容变更未完成或状态不可读的控制器不可预约。
- 可用状态只有严格空闲，或已正常完成 `Extract`、结果为空但残留旧 `ChosenRecipe` 的已验证例外。
- 多类型控制器一轮只能预约一次，并优先保留能力更广的槽位。
- 请求在动作前和紧邻 `SetCook` 前两次重新读取并核验索引、对象标识、网格位置、能力、锁定状态、可用性和预约归属。
- 任一信息发生变化时只返回局部等待，不扫描或改选另一控制器。

`SetCook` 是一次非幂等提交。成功返回后必须立即取得由 `RuntimeCookingGenerationTracker` 发布的同一控制器 `SetCook` 轮次、内容修订号与游戏配方标识，才能登记 `AutomationCookingJob`。HTTP 响应丢失不能导致第二次扣料或第二次开锅。

## 跨帧料理任务

`AutomationCookingJob` 是 Mod 对一锅自动料理的唯一跨帧状态。它保存稳定标识和预约信息，不跨帧保存 `CookController` wrapper：

- 任务 ID、包含精确 `BusinessGeneration` 的订单绑定令牌与执行目标。
- 控制器索引、游戏对象标识、网格位置、厨具内容轮次和内容修订号。
- 配方、料理、加料、锅次和特殊目标签名等不可变执行数据。
- 当前阶段、进度、结构化结果、控制状态、有效超时和有界清理记录器。

订单经营轮次只取自精确匹配的 `OrderBinding.BusinessGeneration`。厨具内容轮次只证明任务对当前厨具内容的控制归属，不能用于订单队列标识、名单匹配或经营生命周期判断。

每次轮询、送达、复位、可用性检查和 `AfterPlayerExtract` 前都从当前物理目录重新绑定同一控制器。`SetCook`、`Extract` 和 `Store` 的 prefix 先发布未完成变更，只有同一修订号的成功 postfix 才标记完成；嵌套或迟到的 postfix 不得覆盖更新状态。

同一厨具内容轮次中，游戏完成料理并替换 `Result` 是正常推进。出现新的 `SetCook` 轮次时以 `cooking-controller-reused` 中断；出现已确认的 `Extract`、`Store`，或厨具稳定空闲且任务不再控制当前内容时，以 `cooking-ownership-lost` 中断。两种情况都只让 Mod 停止控制，不操作当前内容。

进度停滞只累计前后两次读取都可推进的有效时间。控制暂停、断线、场景不可读和控制器暂不可访问时不计时。阶段或进度真正前进才重置停滞计时；达到有界阈值后保留旧锅并进入人工确认，不自动重开。

## 送达、评价与单次提交

每个不可逆调用都遵循同一规则：调用前重新读取并核验；调用后只接受精确回读或同步的最终状态确认；发生异常且无法证明尚未提交时进入待人工确认状态，绝不重复执行。

- 酒水扣库与送达按当前订单、原生库存和最终字段逐步确认。
- 料理送达只接受最终 setter 执行后同一 `Sellable` 对象出现在订单最终字段。
- `StoreFood` 一旦开始调用，即使抛异常也可能已执行前置写入；只有明确未提交才可重试。
- 厨具清理只在同一厨具内容轮次下确认 `Phase == Idle`、`Result == null`、`ChosenRecipe == null` 后完成。
- 控制器占用在人工交接、送达清理成功或清理明确终止后单调释放；评价确认可以晚于占用释放，但不得继续占锅。
- Mod 发起评价时，只接受同一次调用内发布且精确命中订单实例的 `Evaluated` 最终状态记录。订单消失或快照中的 `HasEvaluated` 只表示外部状态最终一致，不能证明本次提交成功。

订单强标识与最终状态记录规则见 [运行时订单生命周期](runtime-order-lifecycle.md)。血池地狱、幽幽子和古明地恋的额外动作顺序只在[特殊经营实现](special-business-implementation.md)维护，本页不重复。

## 待人工确认状态

结果不确定的游戏操作、无法完成的清理、进度倒退和其他明确安全错误，按订单实例登记有界、单调递增的待确认事件序号：

- 待确认事件使对应订单保持 `blocked`，不被总控、阶段开关、普通重试或无关快照变化清除。
- 前端必须展示独立待确认项，即使订单已从当前快照消失。
- 只有当前主设备、配置修订号匹配且持有自动化控制权的客户端，才能按精确事件序号确认。
- 确认只解除该订单截至指定事件的阻断状态，不修改游戏对象，也不影响其他订单。
- 经营生命周期结束会清理该经营轮次的待确认事件。

## 明确禁止的旧路径

- 不提供 `/automation/cancel`、目标取消或任务取消兼容路由；控制变化通过主设备配置、自动化控制权、命令轮次和阶段执行许可处理。
- 不在一般订单路径扫描 manager 寻找替代控制器。
- 不通过送餐面板、按钮、托盘、协程或 `MoveNext` 模拟游戏 UI。
- 不自动退款、补扣或重复执行 `SetCook`、`StoreFood`、最终 setter 或评价。
- 不保存跨帧 IL2CPP 厨具或订单 wrapper。
- 不把玩家投掷能力、HUD 可见性或中文日志当作事务完成证据。

## 修改与验证

修改自动化控制、料理任务或不可逆事务时至少运行：

```bash
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
dotnet run --project tests/night-business-automation-gate/NightBusinessAutomationGateSmoke.csproj -c Release
dotnet run --project tests/runtime-automation-control/RuntimeAutomationControlSmoke.csproj -c Release
dotnet run --project tests/runtime-rare-guest-participation/RuntimeRareGuestParticipationSmoke.csproj -c Release
dotnet run --project tests/runtime-order-terminal-receipt/RuntimeOrderTerminalReceiptSmoke.csproj -c Release
dotnet run --project tests/runtime-cooker-snapshot/RuntimeCookerSnapshotSmoke.csproj -c Release
corepack pnpm audit:automation
corepack pnpm audit:rare-order-participation
corepack pnpm audit:connection-recovery
```

`automation-cooking-job` 使用真实 Harmony/MonoMod 探针，应通过锁定的 .NET 6 容器入口运行：

```bash
corepack pnpm test:dotnet6
```

若修改特殊经营结算，再执行对应专项 smoke，并按 [IL2CPP / IDA 分析流程](il2cpp-analysis-workflow.md) 复核原生边界。
