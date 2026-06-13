# Repo Memory

## 当前项目定位

仓库项目名统一为 `mystia-steward-companion`，已经收敛为《东方夜雀食堂》BepInEx IL2CPP Mod 与 Tauri 桌面伴随窗口。旧的浏览器工具、导入页面、路由和独立验收流程不再维护。

## 关键目录

- `mods/bepinex/`：插件源码、本地 API、运行时读取、构建脚本和 Mod 文档。
- `apps/companion/src/`：伴随窗口 React 工作台、推荐算法、tag 规则、类型和结构化数据。
- `apps/companion/src-tauri/`：桌面伴随窗口壳。

## 开发事实

- Mod 不编译引用 `Assembly-CSharp.dll`，运行时通过反射读取游戏已加载的 IL2CPP interop 类型。
- 用户可见项目名、安装目录和发布产物使用 `mystia-steward-companion`；旧名称只保留在兼容迁移和上游来源说明中。
- `References/` 只放本机编译 DLL，不提交仓库。
- 推荐数据来自游戏运行时 `RuntimeDataCatalog`；仓库不再保留旧版推荐 JSON，`build-release.ps1` 和发布包不再同步或包含 Mod `Data/`。
- 独立伴随窗口通过 `127.0.0.1:32145` 读取运行态；除 `/health` 外，本地 API 使用 `X-Mystia-Steward-Companion-Token` 授权。
- 伴随窗口控制端口固定为 `127.0.0.1:32146`，支持 `show`、`toggle`、`exit` 消息；Mod 热键应先通知已有窗口，控制端口不可达时才启动新进程。
- 伴随窗口会在 Tauri app data 目录保存 `window-state.txt`，记录外框位置和内框尺寸；启动时恢复大小和仍在显示器范围内的位置，防止换显示器后窗口离屏。
- 伴随窗口 `设置` 页负责窗口透明度、焦点切换行为、切换冷却时间、置顶、主题、手柄导航、BepInEx 原生日志窗口、缺失厨具过滤、任务料理优先、经营中订单排序、料理/酒水推荐排序、实验性游戏界面置顶、目标厨具高亮和实验性自动化总开关。透明度通过 Tauri transparent window + CSS 背景 alpha 实现，文字不随背景变淡；稀客专注模式的料理/酒水显示数量在专注模式浮层内调整。
- 伴随窗口根滚动区域固定预留纵向滚动条槽位，避免页面高度变化时滚动条挤占宽度造成内容横向跳动；窗口、下拉和日志滚动条使用主题色并跟随透明度。
- 焦点切换支持两种模式：隐藏伴随窗口再聚焦游戏，或保持伴随窗口悬浮并只聚焦游戏。保持悬浮依赖窗口置顶，独占全屏游戏可能覆盖置顶窗口，推荐窗口化或无边框窗口化。
- 伴随窗口退出跟随不只依赖本地 API `/health` 失联，还会监控启动参数中的 `--game-pid`。游戏窗口 X、游戏内退出按钮、或 Unity 退出阶段未及时发送 `exit` 控制消息时，都应由 PID 监控兜底关闭伴随窗口。
- `修改` 页通过 `/inventory/set` 和 `/inventory/bulk-set` 在 Unity 主线程写入当前运行时材料和酒水库存；页面只保留单项 `-10`、`+10`、`99` 和当前存档可编辑材料/酒水批量设为 `99` 快捷按钮，用户仍需在游戏内保存才能持久化。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，接口按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪尾部内容，前端也只保留有限行数显示。
- Mod 默认写入 `BepInEx/config/BepInEx.cfg` 将 `[Logging.Console] Enabled=false`，并在 Windows 当前会话尝试隐藏控制台窗口；设置页可临时开启/关闭原生日志窗口，接口会同时修改当前窗口可见性和下一次启动配置。
- 游戏内 IMGUI 面板已移除；Mod 在游戏侧只保留后台控制器、本地 API、运行时读取、自动化和伴随窗口唤起。
- 仓库不使用 GitHub Actions 自动构建 Release；`.github/workflows/ci.yml` 只保留手动前端检查。版本发布采用 Windows 本机构建后由 GitHub CLI 上传。
- 默认热键 `F8` 和 `RS Click` 的主语义是游戏与伴随窗口焦点切换；伴随窗口聚焦时由 Tauri 前端处理热键并按设置切回游戏。手柄切换需要释放锁存和可配置后端防抖，防止同一次长按连续 toggle；默认冷却时间为 800ms。
- 伴随窗口内手柄导航由 `apps/companion/src/companion/use-gamepad-navigation.ts` 管理：左摇杆/十字键移动焦点，`A` 确认，`B` 返回或退出专注模式，`LB/RB` 切页，`LT/RT` 滚动，`Y` 进入专注模式或切换精简模式，`X` 收藏当前推荐行。导航采用 `data-gamepad-scope` 分区，顶部页签栏左右键只在页签之间移动，向下进入当前页面内容；行内控件左右移动优先在当前 `data-gamepad-row` 内完成，range 滑杆左右键直接调值，推荐行和收藏按钮需要稳定 `data-gamepad-focus-key` 以便状态变化后回焦。
- 经营中稀客订单默认按首次捕获时间稳定排序；也可在设置页切换为稀客分组。稀客分组模式下，同一稀客订单放在一起，稀客组之间按该稀客最早订单出现时间排序，组内仍按点单先后排序。运行时捕获订单保留到明确移除、稀客离场或 6 小时硬上限，避免长时间未上菜时从伴随窗口消失。
- 运行时推荐状态会尝试读取当前夜间经营场景已摆放的全部厨具，优先读取 `CookSystemManager.Instance.AllCookers` 中的控制器和 `Cooker.AllAvailableCookerType`，再兜底读取 `IzakayaConfigure.CookerConfigure` 与 `RunTimeStorage.GetAllCookers()`；读不到快照时不要过滤料理。目标厨具高亮会复用当前推荐目标，并扫描 `AllCookers`、`AllCookerControllers` 和场景中的 `CookController` 兜底寻找可高亮对象。
- 经营中概览会显示厨具快照读取状态和 `RuntimeUiPinningService.Status`。排查“缺失厨具过滤/游戏界面置顶/目标厨具高亮”时，优先让用户提供这两行状态和 `BepInEx/LogOutput.log`。
- 运行时捕获订单维护 `ChangeVersion`；UI 控制器在版本变化后延迟 0.2 秒强制刷新经营数据并发布本地 API 快照。伴随窗口在 `经营中` 和稀客专注模式下以 750ms 轮询快照，其他页面保持 2 秒。
- 本地 API 快照发布在 Unity 主线程节流到约 0.5 秒一次；完整 `RuntimeDataCatalog` 只在首次、变化、强制刷新或约每 10 秒补发，其余快照可省略 `runtimeData`。前端必须缓存最近一次完整运行时数据。`performanceMs` 会记录 `refresh.business`、`refresh.runtime`、`snapshot.serialize`、`automation.collect`、`snapshot.publish` 等耗时，概览页和经营中页展示最高几项用于排查掉帧。经营扫描还会记录 `business.rare.*`、`business.normal.*`、`runtime.cookerSnapshot`、`mission.serveTargets` 等子项；普客订单快照有短 TTL 缓存，避免一次快照发布链路内重复枚举 `OrderController`、HUD 和 `GuestsManager`。
- 场景切换后 Mod 不再做固定秒数等待；运行时和经营快照会立即尝试刷新。任务快照会跳过切场景后的前几个 Unity 帧，避免与 DayScene UI `Awake/Initialize` 同帧竞争；之后读取代码仍必须避开 IL2CPP `IEnumerator.Current` 这类加载阶段不稳定路径，优先用 Count/indexer、字段或静态快照读取，失败时返回状态提示并等待下一轮刷新，不得阻塞伴随窗口或影响游戏场景初始化。
- `任务` 页任务状态优先对 `RunTimeScheduler.trackingMissions` 中每条任务调用只读 `RunTimeScheduler.ParseActiveMissionData()`，映射为 `available`、`tracking`、`fulfilled`；读取时还要刷新 `TrackedMissionData.UpdateFinishStates()`，并用 `HasFulfilled/get_HasFulfilled` 与 `conditionFinishStates` 全 true 兜底确认 `fulfilled`，避免可完成任务因 tuple/枚举读取失败继续显示为进行中。已完成任务不再作为筛选分类展示，默认筛选显示可接取和可完成。NPC 交谈任务通过 `RunTimeScheduler.GetAvailableInteractMissionForCharacter()` 读取，但该接口返回“可交互推进”的任务，已存在于 `trackingMissions` 的 label 应归为可完成而不是可接取。真正未接取任务还需要只读扫描 `RunTimeScheduler.scheduledEvents` 当前修正日和 `-1` 桶，使用 `DataBaseScheduler.RefEvent()` 解析 `EventNode.postMissions` / `postMissionsAfterPerformance`；只接受 `OnTalkWithCharacter` 或通过 `CheckCharacterInteractEvent()` 门控的 `KizunaCheckPoint` 事件，且过滤已开始/已完成 label。候选 NPC 来源还需要从 `DataBaseDay` 的 NPC 映射表补齐。场景调查任务通过 `RunTimeDayScene.trackedInteradctables`、`MissionInteractConditionComponent` 与 `trackingMissions` 中的 `InspectInteractable` 条件读取；候选来源会写入分来源诊断。`HaveMissionStarted()` 不能用于过滤已追踪任务页条目，因为它等价于检查任务是否在 `trackingMissions` 中。经营投喂任务读取 `ServeInWork` 条件、mission `reciever` 和 `RunTimeScheduler.ContainsSpecialNPCServeInWorkMission()`，只用于任务页展示和经营中任务料理推荐置顶，不得调用 `TryTriggerServeMission()`。NPC 所在场景优先从 `RunTimeDayScene.trackedNPCs` 的 mapLabel 反查，并通过 `DaySceneLanguage.GetMapLanguageData()` 本地化；tracked 位置缺失时用 `DataBaseDay.RefNPC()` 的 `possibleDestinations` 解析可能场景兜底。NPC 名称优先用 `DaySceneLanguage.RefDaySceneName()`，再回退到 `SchedulerNode.Character.GetLanguageData()`；读取失败不回退静态全任务。
- 经营中页顶部只放经营场景、扫描状态、推荐数据、厨具与置顶状态等通用信息，下面用 `稀客` / `普客` 二级页签承载各自列表、推荐和自动化配置。普客订单诊断来源包括 `OrderController.GetShowInUIOrders()`、HUD `OrderingElement.ActiveOrder` 和经营管理器控制器订单；普客订单读取不能要求 `Place` / `PlaceLabel` 必须存在，游戏在 `Work` 场景且 `GuestsManager` 已初始化时可能已经有订单但经营场景标签仍为空。普客 `NormalOrder` 应优先读取 `foodRequest` / `beverageRequest`，并兜底调用 `get_RequestFood()` / `get_RequestBeverage()` 和 `get_id`；普客 `GuestBase` ID 要按普客表解析，不要在诊断里强行显示稀客映射。读取文本时必须过滤 `GameData.CoreLanguage.LanguageBase` 等运行时类型名，普客订单 key 优先使用运行时订单对象指针 `orderKey`，不要只靠桌号/料理/酒水粗匹配。普客自动化入口需要实验性自动化总开关和普客子开关同时开启；开启后不再保留手动处理按钮，伴随窗口会按首次出现时间稳定排序并发轮询仍需启动料理的未满足普客订单，并发上限来自设置页参数。普客轮询使用独立快节拍，订单 key、料理、酒水或送达状态变化时立即触发一次处理。已开始制作或等待收取的订单不得继续占用调度名额。稀客和普客开锅请求会共用伴随窗口本轮厨具预约表，预约容量按当前已摆放厨具快照计算；同类厨具容量不足时优先保留普客待处理订单，稀客料理开锅等待不计作失败。每笔普客订单独立记录料理、保温箱收取和暂停状态；C# pending 和短期已收取回执优先绑定 `orderKey`，避免同桌同料理或桌位复用时串单；非临时错误只暂停对应订单，不影响稀客自动化或其他普客订单。普客自动化只制作料理并在完成后调用 `IzakayaConfigure.StoreFood()` 写入游戏料理暂存容器，不处理酒水、不写 `ServFood/ServBeverage/ServedFoodInAir`、不调用 `EvaluateOrder`、`CookController.Store()` 或 `CookController.AfterPlayerExtract`，最终送达和进餐状态交给玩家走游戏原生流程。同一订单已有 pending 料理时必须等待，不能重复占用同类厨具制作。
- 经营中页的动态区域应保持固定入口：自动化资源、稀客/普客自动化状态和当前点单推荐在暂无数据时也显示紧凑空状态，避免稀客入场、点单或自动化诊断刷新时整块内容突然插入导致页面跳动。
- 经营中页的“当前点单推荐”标题右侧承载稀客订单专注模式入口，以及料理/酒水推荐显示数量设置；这两个数量设置与稀客专注模式内部共用同一组 localStorage 配置，不要拆成两套。
- `稀客` 页下拉选项不再按当前存档已解锁稀客过滤；只按经营场景、可读中文名称和可用点单 Tag 过滤。`availableRareCustomerIds` 曾经误删可测试稀客，除非找到游戏内明确稳定的已解锁字段，否则不要恢复该过滤。
- 普客 pending 料理完成后，只要成品料理 ID 匹配目标，就必须写入 `IzakayaConfigure.StoreFood()`；不要因为目标订单对象的 `ServFood` 或 `ServedFoodInAir` 非空而清理厨具并丢弃成品。订单对象在大量普客订单和桌位回收时可能短暂复用或状态滞后，丢弃成品会造成“显示已制作但保温箱没有”的堆积问题。
- 普客订单显示“已开始制作”后如果长时间没有进入暂存容器，伴随窗口必须允许该订单重新确认后端 pending 待收取任务；后端仍报告已在制作或等待收取时，前端刷新等待时间并清理旧回退计数，不得直接暂停。C# pending 收取也不能因为一次 `Phase=0` 或 IL2CPP 短暂读取异常就立即移除任务。成功写入保温箱后，C# 要保留短期完成回执，让下一次 API 确认能把前端状态标记为已收取。短暂失败应继续重试，超过超时再输出诊断并释放 pending，避免普客/稀客同时自动化抢占厨具后出现永久堆积。
- 自动化状态机只把实际取酒、开锅、收取、单项送达、写入订单和触发评价视为真实进展；“选择订单”“匹配订单”不能刷新进展时间。稀客料理和酒水可以分别送达，只要送餐盘中出现目标项就写入订单并释放该格子，订单满足后才触发评价；前端用 `hasServedFood/hasServedBeverage` 校准状态，避免重复取酒或重复开锅。稀客目标料理/酒水长时间未进入送餐盘且未送达时会回退到重新开锅或重新取酒；普客已开锅但长时间未收至暂存容器时先重查 pending，pending 仍存在则继续等待，pending 消失才重新进入开锅调度。达到回退上限后才按对应订单类型暂停。C# 侧开锅、pending 收取和 pending 移除会写入 `BepInEx/config/MystiaStewardCompanion/automation-jobs.log`，该日志约 1 MB 轮换，连续相同日志会合并为 `repeat` 摘要，并通过伴随窗口日志页的自动化作业日志区按上限读取展示。日志页也提供一键诊断包导出，zip 包含当前 snapshot 和日志尾部，便于后续排查。
- 稀客自动化会在订单状态中锁定料理、加料、酒水、厨具和可接受成品料理 ID 列表；库存变化或推荐排序变化不会让同一订单中途切换目标。正式推荐料理为空时，自动化可以锁定“喜好备选”料理，状态里要显示“喜好备选”。完成订单时 C# 会先精确匹配锁定目标料理，若不存在，再允许复用送餐盘中已观察超过 30 秒且属于该订单可接受成品列表的堆积料理，并在步骤中输出“复用堆积料理”诊断。
- 稀客自动化诊断由前端按订单 key 展示当前候选订单状态，包括步骤、料理/酒水阶段、重试、回退和最近原因。单笔 `重试` 只解除暂停并保留已完成阶段，单笔 `重置` 删除该订单本地状态并让下一轮重新判断；不得清空其他稀客、普客或全局自动化状态。
- 稀客自动化完成订单时，C# 必须同时返回缺料理和缺酒水诊断；前端回退料理时要同步失效酒水缓存，下一轮重新校验取酒。稀客料理自动收取 pending 需要按目标料理 ID 去重，厨具冻结或 Debuff 期间只等待已有 pending，不得重复消耗材料开同一道目标料理。
- 经营中页的自动化资源视图只做可视化：厨具预约来自已摆放厨具容量、普客待开锅订单和稀客候选订单；送餐盘压力来自稀客诊断中已处理但尚未送达的料理/酒水。不要让该视图替代真实预约表或后端送达状态。
- 运行时稀客 ID 归一化优先依赖游戏 `DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 完整运行时稀客表；运行时表按游戏语言名称和同族 `StringId` 建立自动别名，例如 `Yuyuko_Free -> Yuyuko`、`DLC4_Remilia -> Remilia`。同一轮游戏内曾成功解析的变体别名会暂存，避免后续短暂读到 `localResolved=0` 时丢失中文名和本地 ID。手工事件变体只作为兜底。本地缺失但运行时具备有效喜好 Tag 的稀客会合成为临时 `RuntimeRareCustomer`，供经营中订单推荐和伴随窗口稀客页使用；剧情 Intro/Parallel/Current、问号占位、隐藏图鉴、NeverCome、无喜好数据的角色不合成。带具体桌号的捕获订单只允许匹配同一桌活跃稀客；手动/事件型 Special 订单允许在 `ManualDesk` 或订单本身已确认 Special 的上下文里用 `GuestBase.Id` 解析稀客身份，并在运行时订单对象仍可引用且包含点单 Tag 时保留捕获订单，避免活跃稀客快照短暂解析失败导致订单一闪而过。
- 任务页提供稀客邀请列表，可在当前场景和全部日间场景之间切换，并支持单独邀请或邀请全部。实现走游戏日间羁绊邀请链路：`current` 范围优先通过 `DayScene.SceneManager.CurrentActiveMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`DaySceneMap.allCharacters` 和场景 `CharacterConditionComponent` 获取当前场景候选，并用 `DataBaseDay` 目的地兜底；`all` 范围从 `DataBaseDay.GetAllNPCKeys()`、`AllMappedNPCsMapping`、`AllNPCsMapping` 或 `allNPCs` 读取全部日间 NPC 并解析所在地图。候选经 `DataBaseCharacter.RefSGuest()` 映射到 `SpecialGuest`；后端检查 `StatusTracker.HasNPCInvited()`、`RunTimeAlbum.GetOrGenerateSpecialNPCKizunaLevel()`、当前等级成功邀请对话包和 `RunTimeDayScene.RefTrackedNPCAvailability()`。不满足条件的候选也要返回给前端显示原因，只禁用按钮；符合条件后调用 `StatusTracker.RecordInvitedGuest()` 写入今晚邀请名单。列表、单独邀请和全部邀请必须共用同一套扫描和判定逻辑。该功能不调用会随机失败并消耗今日尝试次数的 `DaySceneChatSelectionPannel.InviteSpecGuest()`，也不使用 `HasTemptInvited()` 跳过候选，不直接刷客、不推进时间，也不再写 `Story.SpecialGuestControlled`。
- 稀客订单捕获缓存只应长期保留仍匹配当前活跃稀客的订单；不匹配活跃稀客的捕获项只给短暂宽限。离开夜间经营场景或清除运行时状态时要清空捕获缓存，避免跨天旧订单复活。若过时订单仍残留，经营中稀客订单行右侧删除按钮会调用 `/orders/rare/dismiss` 清理插件端缓存。
- 稀客自动化匹配运行时捕获订单时要兼容事件变体名称和不完整 Tag。强买强卖等变体可能显示 `Tewi_HardSell`，捕获到的 `foodTag` 可能为空，`beverageTag` 可能是“请给我甘的饮料”这类完整句子；同桌且对象仍有效的捕获订单应优先保留，Tag 匹配允许包含关系，不要只做完全相等。
- 手动事件稀客订单不一定走普通 `PostGenerateOrder` 路径。运行时捕获需要覆盖 `GuestsManager.SetManualControllerOrderInternal`，并在 `EvaulateManualOrder` / `EndDlc4SpecialManualOrder` 清理缓存，否则订单会在列表中一闪而过，无法进入自动化流程。项目不再解析 Unity/BepInEx 控制台订单日志，稀客点单必须来自运行时订单对象、控制器、HUD/面板或运行时缓存。
- 诊断开启且经营数据扫描触发时，运行时固定数据会按主题写到诊断目录：`runtime-static-data.log` 映射稀客与 `aliasSource`、`runtime-tags.log` 标签和 TagRule、`runtime-database-diff.log` 核心食材/酒水/料理表对照与读取方式、`runtime-guests.log` 普客/稀客/事件变体、`runtime-izakayas.log` 场景和客人池。游戏数据库未初始化时每 5 秒重试，日志头部 `Complete: True` 表示读取成功。
- 运行时固定数据不只写诊断日志，也会构造成 `RuntimeDataCatalog` 并发布到 `/snapshot.runtimeData`。伴随窗口只使用运行时料理、食材、酒水、普客和稀客数据；`runtimeData.isComplete=false` 时显示等待运行时数据，不使用内置 JSON 兜底。排查数据依赖时，先看概览页“推荐数据”是否显示“游戏运行时”，再检查 `runtime-static-data.log`、`runtime-database-diff.log`、`runtime-guests.log` 的 `Complete: True`。
- 稀客订单专注模式支持精简模式和料理/酒水显示数量配置；精简模式隐藏推荐料理 Tag 并压缩推荐面板间距，显示数量包含收藏置顶项。
- 实验性自动化由设置页总开关启用，经营中页按稀客订单和普客订单分组配置。稀客使用 `autoPrep*` 阶段配置，普客使用 `autoNormal*` 阶段配置，取酒、开始料理、收取和出错暂停互不复用。设置页参数控制稀客/普客并发、稀客送餐盘等待、普客保温箱复查、最大重试和最大回退，默认值为 `2`、`3`、`30s`、`45s`、`3`、`2`；完成订单写入每轮最多执行 1 笔。开启自动开始料理后固定尝试完成原生 QTE 奖励结算，不再提供跳过或完成 QTE 的配置开关；该流程不会打开游戏音游面板，失败时只显示诊断并继续料理流程。普客自动化需要开启“启用普客处理”且至少开启一个实际阶段；临时失败应继续等待并重试，非临时失败才按对应订单类型配置暂停。稀客与普客暂停状态不能共用，普客内部也要按订单 key 隔离暂停。

## 推荐排序口径

- 经营中/稀客推荐排序可由伴随窗口 `设置` 页自定义启用、方向和优先级。料理默认：满足点单 Tag -> 分数降序 -> 加料种类数升序 -> 资源压力升序 -> 料理售价降序 -> 加料成本升序 -> 料理 ID 升序。酒水默认：满足点单 Tag -> 分数降序 -> 酒水售价降序 -> 酒水 ID 升序。
- 料理额外可选排序项包含推荐评级、基础成本、总成本、预计利润、当前厨具可制作；酒水额外可选当前库存数量。资源压力优先惩罚低库存材料，并对额外加料加权；不要再使用“总成本越高越靠前”作为收益判断。
- 稀客和经营中主推荐列表只展示满足当前点单料理 Tag / 酒水 Tag 的结果；未满足点单但命中稀客喜好的结果只能显示在“喜好备选（不满足点单）”区域，不得混入正式推荐、收藏置顶或自动化。料理推荐优先 3 分以上候选，但低于 3 分且满足点单的料理仍要作为兜底显示。
- `排除缺失厨具` 开启且已读取厨具快照时，正式推荐和喜好备选都会隐藏当前场景未摆放对应厨具的料理；厨具类型 1-5 映射为煮锅、烧烤架、油锅、蒸锅、料理台。
- 稀客收藏保存在 `BepInEx/config/MystiaStewardCompanion/favorites.json`，按 `customerId + foodTag` 收藏料理方案（含加料 ID），按 `customerId + beverageTag` 收藏酒水。收藏只置顶当前仍在推荐候选中的结果，不绕过解锁、库存和点单 Tag 校验。
- `优先任务料理` 默认关闭。开启后，当前稀客若存在已接取的 `ServeInWork` 投喂任务，任务指定料理可在经营中正式推荐中排到第一位，并显示 `任务` 标识；它仍需要通过解锁、库存和缺失厨具过滤。该置顶只影响推荐顺序和自动化选择，不自动推进任务状态。
- 经营中订单显示顺序默认是首次出现时间升序；切换为稀客分组时，同组内仍按首次出现时间升序。新订单不应在点单顺序模式下插到已有订单前面；自动化和置顶目标必须使用页面同一排序结果。
- 推荐行需要显示库存数量；料理行需要显示厨具、基础配方和加料，并对这些定位信息做高亮。
