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

`NightBusinessReflectionProvider` 用于 `经营中 / Service` 页。它同样只读当前运行时对象，不读存档文件：

1. 从 `Night.UI.HUD.Ordering.OrderController.GetShowInUIOrders()` 读取当前 HUD 订单。
2. 从 `OrderController.m_Orders` 中的 `OrderingElement.ActiveOrder` 补充读取 UI 订单元素。
3. 从 `NightScene.UI.GuestManagementUtility.OrderingElement.ActiveOrder` 读取 HUD 上可见的稀客点单。
4. 从 `NightScene.UI.GuestManagementUtility.WorkSceneServePannel` 的 `OpenContext`、`operatingOrder` 和 `currentGuestController` 读取当前上菜服务面板。
5. 从 `NightScene.GuestManagementUtility.GuestsManager` 读取稀客控制器集合，包括 `AllPresentedGuestGroupController`、`AllGuestInDeskController`、`AllGuestsControllersInDesk`、`CanPlayerRepellGuest` 和 `ManualDesksDic`。
6. 从 `NightScene.GuestManagementUtility.GuestGroupController.QueuedGuestControllers` 补充读取排队中的稀客。
7. 对稀客控制器优先读取 `SpecialGuest`。只有当 `OrderingGuest` 本身是 `SpecialGuest`，或带有明确的稀客 `StringId` / `SourceGuestID` 时，才把它作为稀客候选；普通 `GuestBase` 的数字 ID 不参与稀客识别，避免普通客 ID 与稀客 ID 重叠导致幽灵稀客。候选只能命中已验证的完整身份快照，不能按显示名称或硬编码别名猜测。
8. 对 `SpecialOrder` 读取 `RequestFoodTag`、`RequestBeverageTag`、`DeskCode` 和 `SpecialGuests`。其中两个 `Request*Tag` 原始数值属于订单身份；合法负数必须保留，读取失败必须显式标记为缺失，不能用负数哨兵折叠两种状态。
9. 如果 `GuestGroupController.AllOrders` 读不到订单，则继续读取 `AllOrdersData`，并用 `PeekOrders()` 读取栈顶订单兜底。
10. 默认不依赖 BepInEx/Unity 日志识别点单。运行时捕获、Provider 和自动化实时扫描都会将 IL2CPP 暴露的 `OrderBase` 通过共享的 `TryCast<SpecialOrder>()` 入口重新包装为真实特殊订单，再读取身份和展示数据：`RequestFoodTag` / `RequestBeverageTag` 原始数值与桌位、运行时原始稀客 ID 组成强身份；归一化 `guestId` 只供推荐与特殊经营规则使用，独立 nullable `runtimeGuestId` 才参与对象定位。`SpecialGuestsController.GetOrderFoodText(...)` / `GetOrderBevText(...)` 是游戏服务面板和投掷送达面板使用的最终展示文本，会应用订单文本 override。override 句子可能完全不包含标准 Tag 名，因此其原文保留在捕获诊断；页面优先按原始 ID 显示规范 Tag，ID 没有目录映射时才把捕获文本规范化后用于显示。所有文本都绝不用于相等、包含或宽松订单匹配。没有 controller 上下文时，`SpecialOrder.ToString()` 只能补充展示文本。`0` 和 `-1` 等值只要属性读取成功都必须作为有效身份保存；读取失败用 nullable / `Has*TagId=false` 表达。同一订单被多个 hook 捕获时，原始 ID 和展示文本按各自完整性独立合并，缺失字段不能覆盖有效值。不要用基类 `foodRequest` / `beverageRequest` 作为特殊订单身份兜底，这两个字段在错误订单视角下可能对应普通食物或酒水请求。
11. 订单删除不再根据 `OrderController.GetShowInUIOrders()` 的空列表全量清空；HUD 订单列表会在点单、服务或刷新期间短暂为空。`FoodDelivered` / `BeverageDelivered` 后的 `IsFullfilled=true` 只表示料理与酒水均已送达，捕获记录必须保留给完成/评价阶段；只有 `RemoveFromOrder`、`PartnerManager` 的 `OrderRemove`、明确评价或手动订单结束等生命周期回调才能删除对应订单。
12. 运行时稀客身份只组合 `GetAllSpecialGuests()` 的基础 `id` / `stringId` 与 `GetAllMappedGuests()` 的 `ID` / `StrID` / `SourceGuestID`。每个映射项必须沿无环、无缺口的 source chain 收敛到基础稀客；重复 ID/StringId、缺来源或循环会让整轮身份读取 fail-closed。身份快照保留所有基础与映射原生身份，即使某项不属于当前可推荐目录，也不通过名称、语言 getter、喜好计算属性、dummy 或手工别名补齐。
13. 捕获路径与 `GuestsManager` 实时扫描路径统一按桌位、`runtimeGuestId`、料理原始 Tag ID、酒水原始 Tag ID 强匹配；必要身份缺失或冲突时 fail-closed。带具体桌号的运行时捕获订单只能匹配同一桌活跃稀客；未入座或排队状态的 `desk=-1` 稀客不能保活旧订单，避免同一稀客再次出现时复活上一次经营的历史点单。自动送达前还要确认捕获订单对象仍由同一 controller 持有且未满足；满足这些校验后，即使经营末尾 manager 集合暂时枚举不到该 controller，也可以继续使用捕获对象完成最后一单。完成定位仍检查 controller 所有权和强身份并允许 `IsFullfilled=true`；原生评价查找必须先用该精确对象确认 fulfilled，未送齐返回正常等待。幽幽子三阶段评价优先严格复核同一捕获对象，失败后才把 manager 扫描作为同一验证器的第二发现来源，不把 manager 集合可枚举性当作 capture 存活的必要条件。
14. 从 `GameData.RunTime.NightSceneUtility.IzakayaConfigure.IzakayaData` 尝试识别当前经营场景。
15. 游戏内部 `DeskCode` 从 0 开始；数据层保留原值用于去重，UI 显示时统一加 1。

`NightBusinessReflectionProvider` 会优先读取 auto-property backing field。IL2CPP 数组和列表按精确 Length/Count 与 indexer 读取；确需全量遍历的已知静态字典才校验同泛参 Enumerator 与 KeyValuePair 后枚举；运行时 tracking/scheduled 字典只对已验证 key 调用 `TryGetValue`，并校验前后 Count 与缓冲区状态。任何形状、元素或数量不一致都会丢弃整组结果，不使用会静默跳项的通用 object 枚举器。`NightBusinessContext.Source` 会记录扫描摘要，例如 `OrderController=1; ServePanel=1; manager=ok; Presented=1; Desk=0; Queue=1; guests=1; orders=1`，用于判断是管理器未找到、集合为空，还是只缺少订单数据。如果当前游戏版本字段名变化，优先核对以上路径；无法映射稀客 ID 时，检查 `aggregate-mod.log` 中的 `runtime-static-data` 和 `runtime-guests` section。

运行时捕获订单维护 `SpecialOrderRuntimeCapture.ChangeVersion`。当订单新增、合并或移除时，UI 控制器会在 Unity 主线程等待 0.2 秒防抖后强制刷新经营数据并发布本地 API 快照；捕获版本、场景和诊断状态都未变化时复用已有经营上下文，并按较慢节奏重新校验，避免 750ms 快照轮询带动完整经营扫描。基础运行时库存仍按 `AutoRefreshSeconds` 慢刷新，避免恢复高频反射扫描导致掉帧。伴随窗口在 `经营中` 和稀客专注模式下以 750ms 轮询缓存快照，其他页面保持 2 秒。

幽幽子剧情版三阶段手动评价回调必须绑定最终选中的同一原生 order/controller 对象，不能只按相同桌位、稀客和 Tag 身份从另一条捕获记录借用；重修版仍要求当前 controller 的 `_50` / `_70` 原生进度回调、已送达目标和等级门槛全部通过后调用 `EvaluateOrder()`。古明地恋本体的 live-controller 规则保持独立，仍跳过 capture 并从 manager 当前集合定位。

普客订单快照每轮先读取 live `OrderController` / HUD 可见订单确认订单仍存在，再把仍能按 `orderKey` 或桌位/料理/酒水槽位对上的 `NormalOrderRuntimeCapture` 绑定合并回来；正常来源摘要标记 `normalOrderMode=liveCaptureReconciled`。捕获缓存不单独证明订单仍存在。只有 capture 不可用时才扫描 `GuestsManager` 控制器集合和队列建立启动绑定，并标记 `normalOrderMode=reflectionBootstrap`。

启用总日志后，稀客别名和运行时固定数据会写入 `aggregate-mod.log` 的 `runtime-static-data`、`runtime-tags`、`runtime-database`、`runtime-guests` 和 `runtime-izakayas` section。诊断只复用已缓存的运行时目录快照并标记 `aliasSource`，内容只在快照变化时追加；关闭、重开或更换总日志后会重置诊断签名，使新文件能得到完整首份快照。

`/snapshot` 只发布运行时目录状态和签名，不再发布 `runtimeRareCustomers` 或合成稀客模型。伴随窗口中可用于推荐的稀客目录唯一来自按签名缓存的 `/runtime-data.rareCustomers`；完整身份快照只留在 Mod 内部用于运行时订单归一化。

诊断开关不改变正式订单来源。有运行时捕获订单时，推荐和自动化始终使用捕获数据及既有缺失项反射补充；为诊断额外枚举到的 HUD/controller 订单只进入诊断快照，不合入业务订单集合。

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
