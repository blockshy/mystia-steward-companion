# 运行时 Provider 说明

当前 Mod 默认使用 `RuntimeReflectionRecommendationStateProvider`。它不在构建期引用额外的游戏业务 DLL，而是在游戏运行时通过反射查找 BepInEx 已加载的 IL2CPP interop 类型，并直接读取当前内存中的运行时数据。

`References/` 只放构建所需的 BepInEx、Il2CppInterop 和 Unity 基础引用，不放额外的游戏业务 DLL。

## 读取流程

1. 从 `DataBaseCore` 的 `IngredientsMapping`、`BeveragesMapping`、`FoodsMapping`、`RecipesMapping` 和 `IzakayasMapping` 五张精确映射表读取静态 ID，再逐 ID 调用对应 `Ref*` 与语言 getter，构造完整 `RuntimeDataCatalog`。
2. 基础普客与稀客只读取 `GetAllNormalGuests()` / `GetAllSpecialGuests()` 精确引用数组中的声明字段；料理、酒水和食材标签读取原始数组，不读取会展开或计算状态的属性。
3. 静态目录完成后，以目录 ID 闭包逐项调用 `RunTimeStorage.HaveRecipe()`、`GetIngredientCountById()` 和 `GetBeverageCountById()`，分别读取解锁料理和当前库存；不生成或枚举完整存档容器。
4. 材料和酒水数量都只有原生返回的 `-1` 表示无限；低于 `-1` 的数量、目录外 ID、重复 ID 或集合形状不符都会让本轮读取失败并等待重试。
5. 通过 `GameData.RunTime.Common.RunTimePlayerData.GetLevel()` 和 `GetPopFoodTags(...)` 读取玩家等级与流行喜好/厌恶标签。
6. 通过 `GameData.RunTime.DaySceneUtility.RunTimeDayScene.GetTrackedSwitch("Aya_FamousIzakaya", false)` 判断明星店状态。
7. 将读取结果转换为 `ParsedSaveData`，再生成推荐算法使用的 `RecommendationState`。只保留推荐状态实际消费的字段，不读取或持久化无消费者的运行时状态。

## 夜间经营订单

自动化在经营生命周期之外还有独立的教学门禁。`RuntimeNightBusinessAutomationGate` 只在 Unity 主线程从精确 `MonoSingleton<NightSceneDirector>.Instance` 读取 `IsInTutorial`；当前 Active generation 一旦读到 true，到本场结束都不再开放自动化。单例、属性或经营代际无法精确确认时返回结构化阻断，不用新档、第一天、场景名、教程对话或订单内容回退。`/snapshot` 发布 `nightBusinessAutomationAllowed`、`nightBusinessAutomationBlockReason` 和 `runtimeNightBusinessAutomationStatus`，仅供伴随窗口停止调度与显示原因；所有自动化写入边界仍必须由 Mod 再次校验门禁。

`NightBusinessReflectionProvider` 用于 `经营中 / Service` 页。它同样只读当前运行时对象，不读存档文件：

1. 从 `Night.UI.HUD.Ordering.OrderController.GetShowInUIOrders()` 和 `OrderController.m_Orders[].ActiveOrder` 读取普客补充可见行；这些来源不提供自动化 controller 所有权，也不得过滤已就绪捕获。
2. 从 `NightScene.UI.GuestManagementUtility.OrderingElement.ActiveOrder`、`WorkSceneServePannel`、`GuestsManager` 各控制器集合和 `GuestGroupController.QueuedGuestControllers` 采集稀客/订单诊断，并读取活动稀客与预算展示。诊断订单不得合入稀客业务集合。
3. 对稀客控制器优先读取 `SpecialGuest`。只有当 `OrderingGuest` 本身是 `SpecialGuest`，或带有明确的稀客 `StringId` / `SourceGuestID` 时，才把它作为稀客候选；普通 `GuestBase` 的数字 ID 不参与稀客识别，避免普通客 ID 与稀客 ID 重叠导致幽灵稀客。候选只能命中已验证的完整身份快照，不能按显示名称或硬编码别名猜测。
4. 对 `SpecialOrder` 读取 `RequestFoodTag`、`RequestBeverageTag`、`DeskCode` 和 `SpecialGuests`。其中两个 `Request*Tag` 原始数值属于订单身份；合法负数必须保留，读取失败必须显式标记为缺失，不能用负数哨兵折叠两种状态。
5. `GuestGroupController.AllOrders` 与 `AllOrdersData` 指向只累积历史订单的同一 `Stack<OrderBase>`，不是活动订单集合，Provider、普客快照、诊断业务判断和自动化均不得枚举它们。`PeekOrders()` 只用于动作前复核已经精确捕获的订单，或古明地恋/幽幽子显式专用 live-controller 路径；不得用它在空捕获时创建一般业务订单。
6. 默认不依赖 BepInEx/Unity 日志识别点单。成功返回的 `GuestGroupController.PushToOrder` 或 `SetManualControllerOrderInternal` 是建立 order/controller/native-key 绑定的唯一入口；native key 只接受非零 IL2CPP 原生对象指针，读取不到时拒绝捕获，不得回退到 managed hash 或其他派生值。生成、HUD、伙伴与状态回调只能按同一 native key 更新或移除既有绑定。运行时捕获、Provider 和自动化都会将 IL2CPP 暴露的当前 `OrderBase` 通过共享的具体类型解析入口重新包装为唯一订单类型，再读取身份：`RequestFoodTag` / `RequestBeverageTag` 原始数值与桌位、运行时原始稀客 ID 组成强身份；归一化 `guestId` 只供推荐与特殊经营规则使用。controller 的订单文本 getter 只提供最终展示文本；没有 controller 时不调用订单文本 getter 或 `SpecialOrder.ToString()` 补齐。所有文本都绝不用于订单匹配。
7. 捕获业务就绪要求 `PushToOrder`、`SetManualControllerOrderInternal`、`RemoveFromOrder`、`EvaluateOrder`、`EvaulateManualOrder`、`CleanOrderInfo`、`RepellInternal` 七个精确 Hook 全部安装，并要求当前 Active generation 从开始时已被覆盖。Hook 在经营中途才补齐时，本场保持 fail-closed，下一场才可消费捕获。标准/手动评价只在调用前订单已 fulfilled 且原调用成功时退休；`RemoveFromOrder` / `CleanOrderInfo` 成功后退休；成功返回的 `RepellInternal` 无论 `out haveSeated` 为何值都退休调用前捕获的订单。`EndDlc4SpecialManualOrder` 只移除 arrival event，不是订单退出边界。
8. 运行时稀客身份只组合 `GetAllSpecialGuests()` 的基础 `id` / `stringId` 与 `GetAllMappedGuests()` 的 `ID` / `StrID` / `SourceGuestID`。每个映射项必须沿无环、无缺口的 source chain 收敛到基础稀客；重复 ID/StringId、缺来源或循环会让整轮身份读取 fail-closed。身份快照保留所有基础与映射原生身份，即使某项不属于当前可推荐目录，也不通过名称、语言 getter、喜好计算属性、dummy 或手工别名补齐。
9. 一般订单动作只接受已就绪精确捕获，并按桌位、`runtimeGuestId`、料理原始 Tag ID、酒水原始 Tag ID 强匹配；必要身份缺失或冲突时 fail-closed。自动送达前确认捕获对象仍是其 controller 的 `PeekOrders()` 当前栈顶且未满足；完成定位执行同一当前栈顶与强身份复核，并允许 `IsFullfilled=true` 进入评价。一般路径不得扫描 `GuestsManager` 回退；只有幽幽子三阶段和古明地恋 BOSS 两个显式命名路径在各自额外原生门禁下读取 manager 当前 controller。
10. 从 `GameData.RunTime.NightSceneUtility.IzakayaConfigure.IzakayaData` 尝试识别当前经营场景。
11. 游戏内部 `DeskCode` 从 0 开始；数据层保留原值用于去重，UI 显示时统一加 1。

`NightBusinessReflectionProvider` 会优先读取 auto-property backing field。IL2CPP 数组和列表按精确 Length/Count 与 indexer 读取；确需全量遍历的已知静态字典才校验同泛参 Enumerator 与 KeyValuePair 后枚举；运行时 tracking/scheduled 字典只对已验证 key 调用 `TryGetValue`，并校验前后 Count 与缓冲区状态。任何形状、元素或数量不一致都会丢弃整组结果，不使用会静默跳项的通用 object 枚举器。`NightBusinessContext.Source` 会记录扫描摘要，例如 `OrderController=1; ServePanel=1; manager=ok; Presented=1; Desk=0; Queue=1; guests=1; orders=1`，用于判断是管理器未找到、集合为空，还是只缺少订单数据。如果当前游戏版本字段名变化，优先核对以上路径；无法映射稀客 ID 时，检查 `aggregate-mod.log` 中的 `runtime-static-data` 和 `runtime-guests` section。

运行时捕获订单维护 `SpecialOrderRuntimeCapture.ChangeVersion`。当订单新增、合并或移除时，UI 控制器会在 Unity 主线程等待 0.2 秒防抖后强制刷新经营数据并发布本地 API 快照；捕获版本、场景和诊断状态都未变化时复用已有经营上下文，并按较慢节奏重新校验，避免 750ms 快照轮询带动完整经营扫描。基础运行时库存仍按 `AutoRefreshSeconds` 慢刷新，避免恢复高频反射扫描导致掉帧。伴随窗口在 `经营中` 和稀客专注模式下以 750ms 轮询缓存快照，其他页面保持 2 秒。

幽幽子剧情版三阶段手动评价回调必须绑定最终选中的同一原生 order/controller 对象，不能只按相同桌位、稀客和 Tag 身份从另一条捕获记录借用。重修版不能把 `_50` / `_70` 合并成挑战级统一入口：精确 `ManualOrderSet` 回调绑定与瞬时 `OrderBase.ManualOrder` 分开保存，同一活动订单的后续状态更新不得清除绑定；绑定只随订单移除、过期或经营代际清理退休，空回调、不同回调或来源冲突均停止。同订单稳定绑定的 `DisplayClass16_10 + b__77/b__78` 与主幽幽子 `_50` 同时成立时只调用 `EvaulateManualOrder`；当前 capture 明确无手动绑定、具体订单已唯一解析为 `NormalOrder` 或 `SpecialOrder`，且仅有组订单 `_70`、没有 `_50` 时只调用 `EvaluateOrder(controller, false, null)`。两种入口都要求 fresh `PeekOrders()` 所有权、当前经营 generation、fulfilled 和已送达目标；证据缺失或冲突时停止，不跨入口重试。幽幽子三阶段与古明地恋 BOSS 的 live-controller 规则保持独立；精确捕获不可用或不满足专用门禁时，只有这两个命名路径的 `SpecialOrder` 可扫描 manager 当前 controller，并继续执行同一严格身份验证。

七 Hook 与当前经营 generation 就绪后，普客订单快照标记 `normalOrderMode=authoritativeCapture`：`NormalOrderRuntimeCapture` 的精确捕获是业务和执行权威，live `OrderController` / HUD 只追加没有捕获绑定的不可执行可见行。同一非零 native `orderKey` 的记录必须去重为捕获订单；不得按桌位、料理或酒水槽位重绑。单轮 HUD 空窗或读取错误不得过滤、隐藏或退休捕获。捕获未通过门禁时标记 `normalOrderMode=visibleFailClosed`，仅显示 HUD 行并禁止自动化，不扫描 `GuestsManager` 或队列建立启动绑定。

启用总日志后，稀客别名和运行时固定数据会写入 `aggregate-mod.log` 的 `runtime-static-data`、`runtime-tags`、`runtime-database`、`runtime-guests` 和 `runtime-izakayas` section。诊断只复用已缓存的运行时目录快照并标记 `aliasSource`，内容只在快照变化时追加；关闭、重开或更换总日志后会重置诊断签名，使新文件能得到完整首份快照。

`/snapshot` 只发布运行时目录状态和签名，不再发布 `runtimeRareCustomers` 或合成稀客模型。伴随窗口中可用于推荐的稀客目录唯一来自按签名缓存的 `/runtime-data.rareCustomers`；完整身份快照只留在 Mod 内部用于运行时订单归一化。

诊断开关不改变正式订单来源。稀客推荐和自动化只使用当前 generation 已就绪且完整绑定的捕获数据；捕获已就绪但为空时就是权威空集合，缺少绑定的反射/HUD/controller 样本仅进入诊断快照。普客捕获就绪后同样是业务和执行权威；HUD 样本只追加不可执行可见行，同 native key 与捕获去重，任何单轮 HUD 缺口或错误都不得裁剪捕获。

## 血池地狱受控推进

- 血池地狱仅在挑战类型、具体订单形态、订单顾客与 controller 顾客都精确确认为 BOSS `1003` 时接管。`SpecialOrder` 保持原点单和动态双 Tag 全命中的严格规则；只有显示在普客区域的 BOSS `NormalOrder` 可以在严格方案为空时考虑受控推进。
- 受控推进仍必须保持游戏原始料理与原始酒水，并通过料理/酒水解锁、库存、材料、厨具、排除项、订单绑定和经营 generation 等全部候选硬门禁。动态目标策略仍是 `owner=yuuma + match=all + 2 tags`；受控推进不是 `Any`，也不能在目标缺失或不可读时生成执行计划。
- 前端通过 `/orders/normal/complete-first` 的 `allowYuumaControlledProgression` 显式传递该许可。Mod 只接受精确 BOSS `NormalOrder`，并要求 `foodId == matchFoodId`、`beverageId == matchBeverageId`；请求还必须显式携带完整预测 Tag，且该预测确实未满足当前双 Tag，预测已全命中却标记受控时拒绝。许可同时进入后端 target、cooking job 和快照 identity，严格 job 与受控 job 不得复用。该布尔值不参与特殊目标 policy/signature/revision。
- 出锅后仍要重新读取当前策略、订单/controller、原订单项目、实际料理 ID、厨具锅次和成品 Tag。受控许可只允许在 Tag 可读但未全命中时继续专用结算；Tag 不可读、策略轮换、成品 ID 不符或其他门禁失败仍 fail-closed。受控方案交由游戏原生评价，可能造成较低伤害并增加狂暴，状态和诊断必须明确标记，不能表述为完整双 Tag 或最高收益方案。

## 回退行为

- 如果当前场景被 `NonGameplaySceneKeywords` 命中，伴随窗口提示运行时数据不可用。
- 如果运行时类型或实时数据方法不可读，伴随窗口显示失败原因。
- 如果夜间基础库存或完整运行时目录暂不可用，`经营中 / Service` 页仍可继续展示已捕获的稀客和订单，但推荐保持不可用状态并等待下一轮读取，不假设“全内容可用”。
- 如果夜间订单读取不到 `GuestsManager`、稀客队列、`OrderController`、HUD 或桌位对象，`经营中 / Service` 页会显示扫描摘要辅助排查。
- 稀客自动化请求或运行时订单缺少桌位、`runtimeGuestId`、料理原始 Tag ID、酒水原始 Tag ID 中任一必要身份时，订单匹配保持不可执行并输出缺失字段诊断；不得退回归一化 `guestId`、展示文本、名称包含或旧字段猜测。
- 当前 BepInEx Mod 不读取 `.memory` 存档文件，也不会扫描或解析固定存档路径。

## 开发约束

- Provider 不应写入或修改游戏存档。
- 推荐算法保持游戏无关，运行时反射代码只放在 `Save/` 或其他 Mod 专属层。
- 字段名和类型名集中维护在 provider 内，避免散落到 UI 或推荐服务。
- 稀客推荐组合搜索必须走伴随窗口缓存，不能在高频快照轮询中直接反复调用完整 `RankRecipes(...)`。
- 游戏更新后如果字段变化，优先核对 provider 中的运行时类型名、字段名和方法名。
