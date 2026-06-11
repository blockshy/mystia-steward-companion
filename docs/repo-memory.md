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
- `tools/sync-data.sh` 和 `build-release.ps1` 会把 `apps/companion/src/data` 同步到 Mod `Data/`。
- 独立伴随窗口通过 `127.0.0.1:32145` 读取运行态；除 `/health` 外，本地 API 使用 `X-Mystia-Steward-Companion-Token` 授权。
- 伴随窗口控制端口固定为 `127.0.0.1:32146`，支持 `show`、`toggle`、`exit` 消息；Mod 热键应先通知已有窗口，控制端口不可达时才启动新进程。
- 伴随窗口会在 Tauri app data 目录保存 `window-state.txt`，记录外框位置和内框尺寸；启动时恢复大小和仍在显示器范围内的位置，防止换显示器后窗口离屏。
- 伴随窗口 `设置` 页负责窗口透明度、焦点切换行为、切换冷却时间、置顶、主题、手柄导航、缺失厨具过滤、经营中订单排序、料理/酒水推荐排序、实验性游戏界面置顶、目标厨具高亮和实验性自动化总开关。透明度通过 Tauri transparent window + CSS 背景 alpha 实现，文字不随背景变淡；稀客专注模式的料理/酒水显示数量在专注模式浮层内调整。
- 伴随窗口根滚动区域固定预留纵向滚动条槽位，避免页面高度变化时滚动条挤占宽度造成内容横向跳动；窗口、下拉和日志滚动条使用主题色并跟随透明度。
- 焦点切换支持两种模式：隐藏伴随窗口再聚焦游戏，或保持伴随窗口悬浮并只聚焦游戏。保持悬浮依赖窗口置顶，独占全屏游戏可能覆盖置顶窗口，推荐窗口化或无边框窗口化。
- 伴随窗口退出跟随不只依赖本地 API `/health` 失联，还会监控启动参数中的 `--game-pid`。游戏窗口 X、游戏内退出按钮、或 Unity 退出阶段未及时发送 `exit` 控制消息时，都应由 PID 监控兜底关闭伴随窗口。
- `修改` 页通过 `/inventory/set` 在 Unity 主线程写入当前运行时材料和酒水库存；页面只保留 `-10`、`+10` 和 `99` 快捷按钮，用户仍需在游戏内保存才能持久化。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，接口按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪尾部内容，前端也只保留有限行数显示。
- Mod 默认写入 `BepInEx/config/BepInEx.cfg` 将 `[Logging.Console] Enabled=false`，并在 Windows 当前会话尝试隐藏控制台窗口；配置对下一次启动完全生效。
- 旧游戏内 IMGUI 面板默认关闭，仅作为回退方案。
- 仓库不使用 GitHub Actions 自动构建 Release；`.github/workflows/ci.yml` 只保留手动前端检查。版本发布采用 Windows 本机构建后由 GitHub CLI 上传。
- 默认热键 `F8` 和 `RS Click` 的主语义是游戏与伴随窗口焦点切换；伴随窗口聚焦时由 Tauri 前端处理热键并按设置切回游戏。手柄切换需要释放锁存和可配置后端防抖，防止同一次长按连续 toggle；默认冷却时间为 800ms。
- 伴随窗口内手柄导航由 `apps/companion/src/companion/use-gamepad-navigation.ts` 管理：左摇杆/十字键移动焦点，`A` 确认，`B` 返回或退出专注模式，`LB/RB` 切页，`LT/RT` 滚动，`Y` 进入专注模式或切换精简模式，`X` 收藏当前推荐行。导航采用 `data-gamepad-scope` 分区，顶部页签栏左右键只在页签之间移动，向下进入当前页面内容；行内控件左右移动优先在当前 `data-gamepad-row` 内完成，range 滑杆左右键直接调值，推荐行和收藏按钮需要稳定 `data-gamepad-focus-key` 以便状态变化后回焦。
- 经营中稀客订单默认按首次捕获时间稳定排序；也可在设置页切换为稀客分组。稀客分组模式下，同一稀客订单放在一起，稀客组之间按该稀客最早订单出现时间排序，组内仍按点单先后排序。运行时捕获订单保留到明确移除、稀客离场或 6 小时硬上限，避免长时间未上菜时从伴随窗口消失。
- 运行时推荐状态会尝试读取当前夜间经营场景已摆放的全部厨具，优先读取 `CookSystemManager.Instance.AllCookers` 中的控制器和 `Cooker.AllAvailableCookerType`，再兜底读取 `IzakayaConfigure.CookerConfigure` 与 `RunTimeStorage.GetAllCookers()`；读不到快照时不要过滤料理。目标厨具高亮会复用当前推荐目标，并扫描 `AllCookers`、`AllCookerControllers` 和场景中的 `CookController` 兜底寻找可高亮对象。
- 经营中概览会显示厨具快照读取状态和 `RuntimeUiPinningService.Status`。排查“缺失厨具过滤/游戏界面置顶/目标厨具高亮”时，优先让用户提供这两行状态和 `BepInEx/LogOutput.log`。
- 运行时捕获订单维护 `ChangeVersion`；UI 控制器在版本变化后延迟 0.2 秒强制刷新经营数据并发布本地 API 快照。伴随窗口在 `经营中` 和稀客专注模式下以 750ms 轮询快照，其他页面保持 2 秒。
- 场景切换后 Mod 不再做固定秒数等待；运行时和经营快照会立即尝试刷新。任务快照会跳过切场景后的前几个 Unity 帧，避免与 DayScene UI `Awake/Initialize` 同帧竞争；之后读取代码仍必须避开 IL2CPP `IEnumerator.Current` 这类加载阶段不稳定路径，优先用 Count/indexer、字段或静态快照读取，失败时返回状态提示并等待下一轮刷新，不得阻塞伴随窗口或影响游戏场景初始化。
- `任务` 页通过 `RunTimeScheduler.GetAvailableInteractMissionForCharacter()` 读取 NPC 交谈任务，并通过 `RunTimeDayScene.trackedInteradctables`、`MissionInteractConditionComponent` 与 `RunTimeScheduler.trackingMissions` 中的 `InspectInteractable` 条件读取场景调查任务；候选来源会写入分来源诊断。`HaveMissionStarted()` 不能用于过滤任务页条目，因为它等价于检查任务是否在 `trackingMissions` 中。场景来源全空时可用 `trackingMissions` 未完成任务 fallback，并根据第一个未完成条件判断未接取/已开始；NPC 所在场景优先从 `RunTimeDayScene.trackedNPCs` 的 mapLabel 反查，本地化失败时显示原始 label；读取失败不回退静态全任务。
- 经营中页顶部只放经营场景、扫描状态、推荐数据、厨具与置顶状态等通用信息，下面用 `稀客` / `普客` 二级页签承载各自列表、推荐和自动化配置。普客订单诊断来源包括 `OrderController.GetShowInUIOrders()`、HUD `OrderingElement.ActiveOrder` 和经营管理器控制器订单；读取文本时必须过滤 `GameData.CoreLanguage.LanguageBase` 等运行时类型名，普客订单 key 优先使用运行时订单对象指针 `orderKey`，不要只靠桌号/料理/酒水粗匹配。普客自动化入口需要实验性自动化总开关和普客子开关同时开启；开启后不再保留手动处理按钮，伴随窗口会按首次出现时间稳定排序并发轮询最多 3 笔仍需启动料理的未满足普客订单，已开始制作或等待收取的订单不得继续占用调度名额。每笔普客订单独立记录料理、保温箱收取和暂停状态；非临时错误只暂停对应订单，不影响稀客自动化或其他普客订单。普客自动化只制作料理并在完成后调用 `IzakayaConfigure.StoreFood()` 写入游戏料理暂存容器，不处理酒水、不写 `ServFood/ServBeverage/ServedFoodInAir`、不调用 `EvaluateOrder`、`CookController.Store()` 或 `CookController.AfterPlayerExtract`，最终送达和进餐状态交给玩家走游戏原生流程。同一订单已有 pending 料理时必须等待，不能重复占用同类厨具制作。
- 运行时稀客 ID 会先归一化为本地 `customer_rare.json` 身份；优先读取游戏 `DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 完整运行时稀客表，运行时表按游戏语言名称匹配本地唯一同名稀客，手工事件变体只作为兜底。本地缺失但运行时具备有效喜好 Tag 的稀客会合成为临时 `RuntimeRareCustomer`，供经营中订单推荐和伴随窗口稀客页使用；剧情 Intro/Parallel/Current、问号占位、隐藏图鉴、NeverCome、无喜好数据的角色不合成。带具体桌号的捕获订单只允许匹配同一桌活跃稀客，未入座 `desk=-1` 稀客不能保活旧订单。
- 诊断开启且经营数据扫描触发时，运行时固定数据会按主题写到诊断目录：`runtime-static-data.log` 映射稀客与 `aliasSource`、`runtime-tags.log` 标签和 TagRule、`runtime-database-diff.log` 核心食材/酒水/料理表对照与读取方式、`runtime-guests.log` 普客/稀客/事件变体、`runtime-izakayas.log` 场景和客人池。游戏数据库未初始化时每 5 秒重试，日志头部 `Complete: True` 表示读取成功。
- 稀客订单专注模式支持精简模式和料理/酒水显示数量配置；精简模式隐藏推荐料理 Tag 并压缩推荐面板间距，显示数量包含收藏置顶项。
- 实验性自动化由设置页总开关启用，经营中页按稀客订单和普客订单分组配置。稀客使用 `autoPrep*` 阶段配置，普客使用 `autoNormal*` 阶段配置，取酒、开始料理、收取、QTE 和出错暂停互不复用。开启自动开始料理后，可选择跳过原生 QTE 或自动完成原生 QTE；自动完成不会打开游戏音游面板，只尝试调用 QTE 成功奖励入口，失败时回退为跳过 QTE 继续料理。稀客自动化只处理当前排序第一笔稀客订单；普客自动化需要开启“启用普客处理”且至少开启一个实际阶段；临时失败应继续等待并重试，非临时失败才按对应订单类型配置暂停。稀客与普客暂停状态不能共用，普客内部也要按订单 key 隔离暂停。

## 推荐排序口径

- 经营中/稀客推荐排序可由伴随窗口 `设置` 页自定义启用、方向和优先级。料理默认：满足点单 Tag -> 分数降序 -> 加料种类数升序 -> 资源压力升序 -> 料理售价降序 -> 加料成本升序 -> 料理 ID 升序。酒水默认：满足点单 Tag -> 分数降序 -> 酒水售价降序 -> 酒水 ID 升序。
- 料理额外可选排序项包含推荐评级、基础成本、总成本、预计利润、当前厨具可制作；酒水额外可选当前库存数量。资源压力优先惩罚低库存材料，并对额外加料加权；不要再使用“总成本越高越靠前”作为收益判断。
- 稀客和经营中主推荐列表只展示满足当前点单料理 Tag / 酒水 Tag 的结果；未满足点单但命中稀客喜好的结果只能显示在“喜好备选（不满足点单）”区域，不得混入正式推荐、收藏置顶或自动化。料理推荐优先 3 分以上候选，但低于 3 分且满足点单的料理仍要作为兜底显示。
- `排除缺失厨具` 开启且已读取厨具快照时，正式推荐和喜好备选都会隐藏当前场景未摆放对应厨具的料理；厨具类型 1-5 映射为煮锅、烧烤架、油锅、蒸锅、料理台。
- 稀客收藏保存在 `BepInEx/config/MystiaStewardCompanion/favorites.json`，按 `customerId + foodTag` 收藏料理方案（含加料 ID），按 `customerId + beverageTag` 收藏酒水。收藏只置顶当前仍在推荐候选中的结果，不绕过解锁、库存和点单 Tag 校验。
- 经营中订单显示顺序默认是首次出现时间升序；切换为稀客分组时，同组内仍按首次出现时间升序。新订单不应在点单顺序模式下插到已有订单前面；自动化和置顶目标必须使用页面同一排序结果。
- 推荐行需要显示库存数量；料理行需要显示厨具、基础配方和加料，并对这些定位信息做高亮。
