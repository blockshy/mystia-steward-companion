# 运行时订单生命周期

更新日期：2026-09-02

本文档只定义普客与稀客订单从游戏创建绑定到最终结束时使用的订单标识、Hook 和业务数据。游戏目录与场景读取见[游戏数据提供器](runtime-provider.md)，后续会改变游戏状态的订单操作见[自动化运行时](automation-runtime.md)，特殊经营的命名例外见[特殊经营实现](special-business-implementation.md)。

## 订单建立依据

游戏没有可直接作为业务依据的“当前全部订单”集合。正式订单记录只能在以下两个游戏入口成功返回后建立：

- `GuestGroupController.PushToOrder(OrderBase)`
- `GuestsManager.SetManualControllerOrderInternal(...)`

两条路径都必须取得同一 `OrderBase` 与 `GuestGroupController` 的非零 IL2CPP 原生指针，并通过共享 `RuntimeOrderTypeResolver` 唯一解析为 `NormalOrder` 或 `SpecialOrder`。解析为零种或同时解析为两种具体类型都拒绝捕获。

创建提交前还必须精确确认：

- 当前夜间经营处于 `Active`，且经营轮次 `generation` 为正。
- 原方法正常返回。
- `HasEvaluated == false`。
- `PeekOrders()` 的当前栈顶就是本次 order。
- 订单与控制器的原生对象标识均唯一可读。

不得使用托管哈希、桌位、料理、酒水、显示名称或列表位置代替原生对象标识。

## 完整 Hook 集

普客与稀客捕获各自必须完整安装同一组七个生命周期 Hook：

1. `GuestGroupController.PushToOrder`
2. `GuestsManager.SetManualControllerOrderInternal`
3. `GuestsManager.RemoveFromOrder`
4. `GuestsManager.EvaluateOrder`
5. `GuestsManager.EvaulateManualOrder`
6. `GuestsManager.CleanOrderInfo`
7. `GuestsManager.RepellInternal`

只有七个 Hook 全部就绪，并且当前经营轮次 `generation` 从开始时已被完整覆盖，`IsBusinessReady` 才为真。若 Hook 在经营中途补齐，本场继续停止相关功能，下一场才允许使用捕获结果。

状态观察 Hook 可以更新已存在的绑定，但不能创建订单所有权。`SpecialOrderRuntimeCapture` 的伙伴状态观察器同样只能按既有原生键更新。

## 订单标识

每次成功创建绑定都会调用 `RuntimeOrderTerminalReceiptStore.BeginLifecycle`，产生进程内单调递增的 `OrderLifecycleSequence`。公开订单标识由以下字段共同组成：

- 夜间经营 `businessGeneration`
- 具体订单类型 `Normal` 或 `Special`
- `orderPointer`
- `controllerPointer`
- 正数 `orderLifecycleSequence`

同一组游戏对象指针被复用时必须分配新的订单实例序号；仅比较指针无法阻止 ABA。清理经营状态只清空活动订单标识和最终状态记录，不降低订单实例序号或状态记录序号的进程水位。

普客业务另外发布规范的原始 `orderKey`、从零开始的 `DeskCode`、原订单料理与酒水标识。稀客的完整订单标识另外包含：

- 0-based `DeskCode`
- 原始 `runtimeGuestId`
- 可空的原始料理标签 ID
- 可空的原始酒水标签 ID

标签 ID 是有符号的原生标识，`0`、`-1` 等合法值必须原样保留；读取失败使用缺失状态，不能与任意数值合并。规范稀客 ID 只用于推荐和特殊规则，不替代订单匹配标识。

展示文本只能由完整的有符号标签映射查询。映射缺失时按数量上限记录并拒绝生成业务数据；禁止调用 `GetOrderFoodText`、`GetOrderBevText`、重写委托、`SpecialGuest.Get*TagText`、`ToString()`，也不生成 `#id` 文本。

## 订单标识变化隔离

任一已绑定的状态观察器在判断上下文、完成或送达状态之前，都必须比较当前订单的两项原始标签 ID。若同一原生槽位与订单实例中任一 ID 和捕获值冲突：

1. 删除对应捕获。
2. 使该精确订单实例失效。
3. 不发布 `evaluated` 或 `removed` 最终状态记录。
4. 不把新读数合并或发布为替代订单。
5. 只有后续原生创建绑定成功并分配新订单实例后才恢复。

该规则防止原生对象池复用、迟到的状态观察回调或第三方 Mod 改写对象后把两个逻辑订单拼接为一个。

## 业务数据与可见样本

稀客捕获就绪后，`SpecialOrderRuntimeCapture` 是稀客订单唯一生效的业务集合；就绪的空集合明确表示当前没有稀客订单。`GuestsManager`、队列、`OrderController`、HUD、服务面板和桌位对象只提供活动客人或诊断样本，不能补造稀客订单。

普客捕获就绪后进入 `normalOrderMode=authoritativeCapture`：

- `NormalOrderRuntimeCapture` 中具有正订单实例序号的捕获是业务与执行的唯一依据。
- `OrderController` 和 HUD 可以追加没有捕获绑定的不可执行可见行。
- 与捕获拥有相同原始订单键的可见行必须排除，不能覆盖捕获状态。
- HUD 空窗、读取失败或某行消失不能过滤、隐藏或结束捕获。
- 同桌的新订单、相同料理或相同酒水都不能作为重绑依据。

捕获未就绪时进入 `normalOrderMode=visibleFailClosed`，只显示可见行并禁止自动化。不得扫描 `GuestsManager`、队列或 `NightSceneDirector.controlledGuest` 建立启动绑定。

## 动作前复核

每个订单写请求必须携带快照公开的正数 `orderLifecycleSequence`。执行任何游戏写操作前必须同时满足：

- 请求序列与最新捕获的序列相同。
- `RuntimeOrderTerminalReceiptStore` 中精确标识组合的活动序列仍相同。
- 当前经营轮次 `generation` 和具体订单类型相同。
- 订单/控制器指针和该类订单的完整标识相同。
- `PeekOrders()` 当前栈顶仍是该订单。

一般订单动作不得在管理器中搜索替代控制器。古明地恋 BOSS 与幽幽子三阶段只有在各自已验证的特殊规则和额外条件全部成立时，才允许使用明确命名的实时控制器路径；不能把该例外扩展为通用降级方案。

## 最终状态记录

最终状态 Hook 的 prefix 只捕获托管令牌值，不跨调用保存 IL2CPP wrapper。原方法正常返回后，postfix 才可为调用前固定的订单实例发布状态记录：

| 游戏调用边界 | 前置条件 | 状态记录 |
| --- | --- | --- |
| `EvaluateOrder` | 调用前 `IsFullfilled == true` | `Evaluated` |
| `EvaulateManualOrder` | 调用前 `IsFullfilled == true` | `Evaluated` |
| `RemoveFromOrder` | 订单必须唯一命中活动实例 | `Removed` |
| `CleanOrderInfo` | 精确匹配订单、控制器和实例 | `Removed` |
| `RepellInternal` | 精确匹配订单、控制器和实例 | `Removed`，不区分 `haveSeated` |

游戏调用发生异常时不发布成功记录。`EndDlc4SpecialManualOrder` 只移除 arrival event，不表示订单已经结束。

状态记录存储上限为 128 项，只保存经营轮次、类型、两项游戏对象指针、订单实例序号、单调递增的记录序号、结果类型和来源。对同一订单实例，`Evaluated` 强于嵌套回调产生的 `Removed`；结果类型相同时选择更新的记录。旧 postfix 不得结束同一标识组合上更晚的新订单实例。

## 生命周期边界

进入夜间经营 `Closing` 或 `Destroyed` 时，按当前经营轮次清理普客/稀客捕获、活动订单实例和最终状态记录。过期捕获只能从内存缓存移除，不能据此推断游戏已经评价或移除订单。

手动“忽略稀客订单”只按桌位、已捕获的 `runtimeGuestId`、原始料理标签 ID 和原始酒水标签 ID 精确删除插件捕获，不产生游戏最终状态、不操作游戏订单，也不能影响其他订单实例。

## 禁止路径

- `GuestGroupController.AllOrders` 与 `AllOrdersData` 是累积历史栈，不得作为活动订单、启动扫描或业务数据来源。
- `PeekOrders()` 只用于已捕获订单的动作前复核，不能在空捕获时创建一般订单。
- 不恢复托管哈希、文本、名称包含、桌位/内容合并、短时间宽限或管理器全量扫描。
- 不以 HUD 可见性作为捕获存在性的必要条件。
- 不把诊断样本、Partner 状态观察器或最终状态记录解释为控制器占用。

## 修改与验证

修改捕获、强订单标识或最终状态处理时至少运行：

```bash
dotnet run --project tests/special-order-runtime-capture/SpecialOrderRuntimeCaptureSmoke.csproj -c Release
dotnet run --project tests/runtime-order-terminal-receipt/RuntimeOrderTerminalReceiptSmoke.csproj -c Release
dotnet run --project tests/rare-order-identity-matching/RareOrderIdentityMatchingSmoke.csproj -c Release
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
```

涉及具体游戏成员或 Hook 形态时，按 [IL2CPP / IDA 分析流程](il2cpp-analysis-workflow.md) 重新验证，不增加旧路径或宽泛反射兼容层。
