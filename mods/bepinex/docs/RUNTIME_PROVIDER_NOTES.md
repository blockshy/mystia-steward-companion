# 运行时 Provider 说明

当前 Mod 默认使用 `RuntimeReflectionRecommendationStateProvider`。它不在构建期引用 `Assembly-CSharp.dll`，而是在游戏运行时通过反射查找 BepInEx 已加载的 IL2CPP interop 类型，并直接读取当前内存中的运行时数据。

`/tmp/Assembly-CSharp` 导出的项目只作为开发参考，用来确认稳定的类型、字段和方法名。不要把导出的 `Assembly-CSharp.dll` 或游戏生成的 `Assembly-CSharp.dll` 复制到 `References/`。

## 读取流程

1. 通过 `GameData.RunTime.Common.RunTimeStorage.GetAllRecipeIndex()` 读取当前已解锁料理。
2. 通过 `GameData.RunTime.Common.RunTimeStorage.GetAllBeveragesId()` 读取当前酒水数量。
3. 通过 `GameData.RunTime.Common.RunTimeStorage.GetAllIngredients()` 读取当前食材数量。
4. 通过 `GameData.RunTime.Common.RunTimePlayerData.GetLevel()` 和 `GetPopFoodTags(...)` 读取玩家等级与流行喜好/厌恶标签。
5. 通过 `GameData.RunTime.DaySceneUtility.RunTimeDayScene.GetTrackedSwitch("Aya_FamousIzakaya", false)` 判断明星店状态。
6. 对没有直接 getter 的字段，例如 `collabStatus`，调用同一批运行时对象的 `GenerateSaveData()` 生成当前内存快照作为补充；这不是读取 `.memory` 文件。
7. 将读取结果转换为 `ParsedSaveData`，再生成推荐算法使用的 `RecommendationState`。

## 夜间经营订单

`NightBusinessReflectionProvider` 用于 `经营中 / Service` 页。它同样只读当前运行时对象，不读存档文件：

1. 从 `Night.UI.HUD.Ordering.OrderController.GetShowInUIOrders()` 读取当前 HUD 订单。
2. 从 `OrderController.m_Orders` 中的 `OrderingElement.ActiveOrder` 补充读取 UI 订单元素。
3. 从 `NightScene.UI.GuestManagementUtility.OrderingElement.ActiveOrder` 读取 HUD 上可见的稀客点单。
4. 从 `NightScene.UI.GuestManagementUtility.WorkSceneServePannel` 的 `OpenContext`、`operatingOrder` 和 `currentGuestController` 读取当前上菜服务面板。
5. 从 `NightScene.GuestManagementUtility.GuestsManager` 读取稀客控制器集合，包括 `AllPresentedGuestGroupController`、`AllGuestInDeskController`、`AllGuestsControllersInDesk`、`CanPlayerRepellGuest` 和 `ManualDesksDic`。
6. 从 `NightScene.GuestManagementUtility.GuestGroupController.QueuedGuestControllers` 补充读取排队中的稀客。
7. 对稀客控制器优先读取 `SpecialGuest`。只有当 `OrderingGuest` 本身是 `SpecialGuest`，或带有明确的稀客 `StringId` / `SourceGuestID` 时，才把它作为稀客兜底；普通 `GuestBase` 的数字 ID 不参与稀客识别，避免普通客 ID 与稀客 ID 重叠导致幽灵稀客。若本地数据缺失但运行时稀客表提供有效名称与喜好 Tag，则合成为临时运行时稀客继续参与推荐。
8. 对 `SpecialOrder` 读取 `RequestFoodTag`、`RequestBeverageTag`、`DeskCode` 和 `SpecialGuests`。
9. 如果 `GuestGroupController.AllOrders` 读不到订单，则继续读取 `AllOrdersData`，并用 `PeekOrders()` 读取栈顶订单兜底。
10. 默认不依赖 BepInEx/Unity 日志识别点单。运行时捕获会将 IL2CPP 暴露的 `OrderBase` 通过 `TryCast<SpecialOrder>()` 重新包装为真实特殊订单，再读取 `SpecialOrder.ToString()`、`RequestFoodTag` / `RequestBeverageTag`、必要的 `SpecialGuestsController.GetOrderBevText(...)` 和当前桌位稀客补齐信息。`0` 是有效料理 tag（`肉`），但只有确认属性读取成功时才能按 0 映射；酒水 tag 不要复用料理 tag 映射，负数酒水 tag id 视为未识别而不是显示为 `#-1`。特殊订单文本可能返回稀客台词而不是标准标签，因此 provider 会优先用料理 tag id 映射，并从本地料理/酒水候选标签中抽取标准词条。同一订单被多个 hook 捕获时会合并保留更完整的料理/酒水 tag，避免 `OrderAdd` 用缺失字段覆盖 `PostGenerateOrder` 的有效文本。不要用基类 `foodRequest` / `beverageRequest` 作为特殊订单兜底，这两个字段在 `SpecialOrder` 上可能对应普通食物或酒水请求，容易把 `肉/高酒精` 读成 `素` 等错误词条。
11. 订单删除不再根据 `OrderController.GetShowInUIOrders()` 的空列表全量清空；HUD 订单列表会在点单、服务或刷新期间短暂为空。运行时捕获只在 `RemoveFromOrder`、`PartnerManager` 的 `OrderRemove`，或 `FoodDelivered` / `BeverageDelivered` 后订单已 `IsFullfilled` 时删除对应订单。
12. 运行时稀客身份优先依赖 `GameData.Core.Collections.CharacterUtility.DataBaseCharacter.GetAllMappedGuests()` 和 `GetSpecialGuestsAndMappedGuests()`：固定映射优先使用 `MappedSpecialGuest.ID` / `StrID` 落到 `SourceGuestID`，完整运行时稀客表会按游戏语言名称和同族 `StringId` 建立自动别名，例如 `Yuyuko_Free -> Yuyuko`、`DLC4_Remilia -> Remilia`、`Tewi_HardSell -> Tewi`。读取不到时再回退到 `StringId` / 名称别名和少量手工兜底。若仍无法归一化，但运行时对象有可用的 `LikeFoodTag`、`HateFoodTag` 或 `LikeBevTag`，会生成临时运行时稀客并随本地 API 快照发布给伴随窗口；剧情 Intro/Parallel/Current、问号占位、隐藏图鉴、NeverCome、无喜好数据的角色不合成。
13. 带具体桌号的运行时捕获订单只能匹配同一桌活跃稀客；未入座或排队状态的 `desk=-1` 稀客不能保活旧订单，避免同一稀客再次出现时复活上一次经营的历史点单。
14. 从 `GameData.RunTime.NightSceneUtility.IzakayaConfigure.IzakayaData` 尝试识别当前经营场景。
15. 游戏内部 `DeskCode` 从 0 开始；数据层保留原值用于去重，UI 显示时统一加 1。

`NightBusinessReflectionProvider` 会优先读取 auto-property backing field，并在普通 `IEnumerable` 不可用时通过反射调用 `GetEnumerator()`、`MoveNext()` 和 `Current` 枚举 IL2CPP 集合。`NightBusinessContext.Source` 会记录扫描摘要，例如 `OrderController=1; ServePanel=1; manager=ok; Presented=1; Desk=0; Queue=1; guests=1; orders=1`，用于判断是管理器未找到、集合为空，还是只缺少订单数据。如果当前游戏版本字段名变化，优先核对以上路径；无法映射稀客 ID 时，检查运行时固定数据日志中的 `runtime-static-data.log` 和 `runtime-guests.log`。

运行时捕获订单维护 `SpecialOrderRuntimeCapture.ChangeVersion`。当订单新增、合并或移除时，UI 控制器会在 Unity 主线程等待 0.2 秒防抖后强制刷新经营数据并发布本地 API 快照；基础运行时库存仍按 `AutoRefreshSeconds` 慢刷新，避免恢复高频反射扫描导致掉帧。伴随窗口在 `经营中` 和稀客专注模式下以 750ms 轮询缓存快照，其他页面保持 2 秒。

启用经营诊断后，稀客别名快照会写入独立日志 `runtime-static-data.log`，默认路径为 `BepInEx/config/MystiaStewardCompanion/runtime-static-data.log`。如果用户配置了自定义经营诊断日志路径，则该静态日志写入同一目录。日志会标记 `aliasSource`，用于区分固定映射、运行时同名归一化、直接本地 ID 和手工兜底；内容只在快照变化时追加，便于排查事件稀客别名而不污染逐帧经营诊断。

## 回退行为

- 如果当前场景被 `NonGameplaySceneKeywords` 命中，伴随窗口提示运行时数据不可用。
- 如果运行时类型或实时数据方法不可读，伴随窗口显示失败原因。
- 如果夜间基础库存运行时对象为空，`经营中 / Service` 页仍继续读取稀客和订单，并临时按“全内容可用”计算推荐。
- 如果夜间订单读取不到 `GuestsManager`、稀客队列、`OrderController`、HUD 或桌位对象，`经营中 / Service` 页会显示扫描摘要辅助排查。
- 当前 BepInEx Mod 不读取 `.memory` 存档文件，也不会扫描或解析固定存档路径。

## 开发约束

- Provider 不应写入或修改游戏存档。
- 推荐算法保持游戏无关，运行时反射代码只放在 `Save/` 或其他 Mod 专属层。
- 字段名和类型名集中维护在 provider 内，避免散落到 UI 或推荐服务。
- 稀客推荐组合搜索必须走伴随窗口缓存，不能在高频快照轮询中直接反复调用完整 `RankRecipes(...)`。
- 游戏更新后如果字段变化，优先重新导出 `Assembly-CSharp` 项目并核对 provider 中的字符串路径。
