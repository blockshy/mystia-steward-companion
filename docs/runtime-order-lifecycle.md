# 运行时订单生命周期

更新日期：2026-08-19

本文档只定义普客与稀客订单从原生创建绑定到终态退休的权威身份、Hook 和业务投影。运行时目录与场景读取见 [运行时 Provider](runtime-provider.md)，订单后续副作用见 [自动化运行时](automation-runtime.md)，特殊经营的命名例外见[特殊经营实现](special-business-implementation.md)。

## 权威来源

游戏没有可作为业务权威的“当前全部订单”集合。正式订单所有权只能由以下两个原生入口成功返回后建立：

- `GuestGroupController.PushToOrder(OrderBase)`
- `GuestsManager.SetManualControllerOrderInternal(...)`

两条路径都必须取得同一 `OrderBase` 与 `GuestGroupController` 的非零 IL2CPP 原生指针，并通过共享 `RuntimeOrderTypeResolver` 唯一解析为 `NormalOrder` 或 `SpecialOrder`。解析为零种或同时解析为两种具体类型都拒绝捕获。

创建提交前还必须精确确认：

- 当前夜间经营处于 `Active`，且 generation 为正。
- 原方法正常返回。
- `HasEvaluated == false`。
- `PeekOrders()` 的当前栈顶就是本次 order。
- order 与 controller 的原生身份均唯一可读。

不得使用 managed hash、桌位、料理、酒水、显示名称或列表位置代替原生身份。

## 完整 Hook 集

普客与稀客捕获各自必须完整安装同一组七个生命周期 Hook：

1. `GuestGroupController.PushToOrder`
2. `GuestsManager.SetManualControllerOrderInternal`
3. `GuestsManager.RemoveFromOrder`
4. `GuestsManager.EvaluateOrder`
5. `GuestsManager.EvaulateManualOrder`
6. `GuestsManager.CleanOrderInfo`
7. `GuestsManager.RepellInternal`

只有七个 Hook 全部就绪，并且当前经营 generation 从开始时已被完整覆盖，`IsBusinessReady` 才为真。若 Hook 在经营中途补齐，本场继续 fail-closed，下一场才允许业务消费。

状态观察 Hook 可以更新已存在的绑定，但不能创建订单所有权。`SpecialOrderRuntimeCapture` 的 Partner observer 同样只能按既有 native key 更新。

## 订单身份

每次成功创建绑定都会调用 `RuntimeOrderTerminalReceiptStore.BeginLifecycle`，产生进程内单调递增的 `OrderLifecycleSequence`。权威公共身份由以下字段共同组成：

- 夜间经营 `businessGeneration`
- 具体订单类型 `Normal` 或 `Special`
- `orderPointer`
- `controllerPointer`
- 正数 `orderLifecycleSequence`

同一组原生指针被游戏复用时必须分配新 lifecycle；仅比较指针无法阻止 ABA。清理经营状态只清空活动身份和回执，不回退 lifecycle 或 receipt 的进程水位。

普客业务另外发布规范 raw `orderKey`、0-based `DeskCode`、原订单料理与酒水身份。稀客强身份另外包含：

- 0-based `DeskCode`
- 原始 `runtimeGuestId`
- nullable 原始料理 Tag ID
- nullable 原始酒水 Tag ID

Tag ID 是 signed 原生身份，`0`、`-1` 等合法值必须原样保留；读取失败使用缺失状态，不能与任意数值折叠。canonical 稀客 ID 只用于推荐和特殊规则，不替代订单匹配身份。

展示文本只能由完整运行时 signed Tag map 查询。映射缺失时有界记录并拒绝业务投影；禁止调用 `GetOrderFoodText`、`GetOrderBevText`、override 委托、`SpecialGuest.Get*TagText`、`ToString()`，也不生成 `#id` 文本。

## 身份漂移隔离

任一已绑定 observer 在判断 context、fulfilled 或送达状态之前，都必须比较当前 order 的两项原始 Tag ID。若同一 native slot 与 lifecycle 中任一 ID 和捕获值冲突：

1. 删除对应捕获。
2. 使该 exact lifecycle 失效。
3. 不发布 evaluated 或 removed 回执。
4. 不把新读数合并或发布为替代订单。
5. 只有后续成功原生创建绑定分配新 lifecycle 后才恢复。

该规则防止原生对象池复用、迟到 observer 或第三方 Mod 改写对象后把两个逻辑订单拼接为一个。

## 业务投影与可见样本

稀客捕获就绪后，`SpecialOrderRuntimeCapture` 是稀客订单唯一业务集合；就绪的空集合就是权威空集合。`GuestsManager`、队列、`OrderController`、HUD、服务面板和桌位对象只提供活动客人或诊断样本，不能补造稀客订单。

普客捕获就绪后进入 `normalOrderMode=authoritativeCapture`：

- `NormalOrderRuntimeCapture` 的正 lifecycle 捕获是业务与执行权威。
- `OrderController` 和 HUD 可以追加没有捕获绑定的不可执行可见行。
- 与捕获拥有相同 raw order key 的可见行必须排除，不能覆盖捕获状态。
- HUD 空窗、读取失败或某行消失不能过滤、隐藏或退休捕获。
- 同桌的新订单、相同料理或相同酒水都不能作为重绑依据。

捕获未就绪时进入 `normalOrderMode=visibleFailClosed`，只显示可见行并禁止自动化。不得扫描 `GuestsManager`、队列或 `NightSceneDirector.controlledGuest` 建立启动绑定。

## 动作前复核

每个订单写请求必须携带快照公开的正 `orderLifecycleSequence`。任何副作用前必须同时满足：

- 请求序列与 fresh capture 序列相同。
- `RuntimeOrderTerminalReceiptStore` 中 exact tuple 的活动序列仍相同。
- 当前经营 generation 和具体订单类型相同。
- order/controller 指针和该类订单的强身份相同。
- `PeekOrders()` 当前栈顶仍是该 order。

一般订单动作不得在 manager 中搜索替代 controller。古明地恋 BOSS 与幽幽子三阶段只有在各自已验证的特殊规则和额外门禁成立时，才允许使用明确命名的 live-controller 路径；不能把该例外扩展为通用回退。

## 终态回执

终态 Hook 的 prefix 只捕获标量 token，不跨调用保存 IL2CPP wrapper。原方法正常返回后，postfix 才可为调用前锁存 lifecycle 发布回执：

| 原生边界 | 前置条件 | 回执 |
| --- | --- | --- |
| `EvaluateOrder` | 调用前 `IsFullfilled == true` | `Evaluated` |
| `EvaulateManualOrder` | 调用前 `IsFullfilled == true` | `Evaluated` |
| `RemoveFromOrder` | order 必须唯一命中活动 lifecycle | `Removed` |
| `CleanOrderInfo` | exact order/controller/lifecycle | `Removed` |
| `RepellInternal` | exact order/controller/lifecycle | `Removed`，不区分 `haveSeated` |

原生异常不发布成功回执。`EndDlc4SpecialManualOrder` 只移除 arrival event，不是订单终态。

回执存储有界为 128 项，只保存 generation、kind、两项原生指针、lifecycle、单调 receipt sequence、disposition 和 source。对同一 lifecycle，`Evaluated` 强于嵌套回调产生的 `Removed`；同 disposition 选择更新的 receipt。旧 postfix 不得退休同 tuple 上更晚的新 lifecycle。

## 生命周期边界

进入夜间经营 `Closing` 或 `Destroyed` 时，按当前 generation 清理普客/稀客捕获、活动 lifecycle 和回执。过期捕获只能从内存缓存移除，不能据此推断游戏已经评价或移除订单。

手动“忽略稀客订单”只按桌位及已有的 runtime guest/原始 Tag 身份精确删除插件捕获，不产生游戏终态、不操作游戏订单，也不能影响其他 lifecycle。

## 禁止路径

- `GuestGroupController.AllOrders` 与 `AllOrdersData` 是累积历史栈，不得作为活动订单、启动扫描或业务投影。
- `PeekOrders()` 只用于已捕获订单的动作前复核，不能在空捕获时创建一般订单。
- 不恢复 managed hash、文本、名称包含、桌位/内容合并、短时间宽限或 manager 全量扫描。
- 不以 HUD 可见性作为捕获存在性的必要条件。
- 不把诊断样本、Partner observer 或终态回执解释为 controller lease。

## 修改与验证

修改捕获、强身份或终态处理时至少运行：

```bash
dotnet run --project tests/special-order-runtime-capture/SpecialOrderRuntimeCaptureSmoke.csproj -c Release
dotnet run --project tests/runtime-order-terminal-receipt/RuntimeOrderTerminalReceiptSmoke.csproj -c Release
dotnet run --project tests/rare-order-identity-matching/RareOrderIdentityMatchingSmoke.csproj -c Release
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
```

涉及具体游戏成员或 Hook 形态时，按 [IL2CPP / IDA 分析流程](il2cpp-analysis-workflow.md) 重新验证，不增加旧路径或宽泛反射兼容层。
