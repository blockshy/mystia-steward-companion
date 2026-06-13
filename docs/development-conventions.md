# 开发约定与流程

更新日期：2026-06-09

## 代码边界

- 仓库只维护 BepInEx Mod 与 Tauri 伴随窗口，不再维护独立网站和存档导入页面。
- 伴随窗口入口为 `apps/companion/src/companion/ModWorkbench.tsx`，顶层挂载在 `apps/companion/src/App.tsx`。
- 推荐算法集中在 `apps/companion/src/lib/normal-recommend.ts`、`apps/companion/src/lib/rare-recommend.ts` 和 `apps/companion/src/lib/tags.ts`。
- 推荐、库存名称、任务目标和自动化目标只使用 Mod 从游戏运行时读取并通过本地 API 发布的结构化数据；运行时数据未就绪时，伴随窗口显示等待状态，不再回退到外部 JSON。
- C# Mod 不引用 TypeScript 模块；前端和 Mod 的共享数据通过本地 API 的运行时快照传递，仓库和发布包都不保留旧版推荐 JSON，不要为新增稀客事件变体优先补静态表。

## 命名约束

- 项目、产品名、安装目录、发布产物和用户可见项目引用统一使用 `mystia-steward-companion`。
- C# 命名空间和类型可使用 `MystiaStewardCompanion`。
- 旧名称只允许出现在明确的兼容迁移代码或上游来源说明中，例如旧 BepInEx 配置和旧 localStorage key 迁移。
- 修改路径或项目名时，必须同步更新 README、AGENTS、构建脚本、GitHub Actions 和相关 docs。

## 编码规范

- TypeScript 使用 strict 写法，避免 `any`。
- `src` 内导入统一使用 `@/` 别名。
- React 代码使用函数组件和 hooks。
- 面向用户的文案默认使用中文；Mod UI 需要同时保留中文和英文入口。
- 不在组件中硬编码平衡值，优先更新结构化数据和类型化逻辑。

## 构建验证

常规检查：

```bash
pnpm lint
pnpm build
```

伴随窗口：

```bash
pnpm tauri:build
```

BepInEx 插件：

```bash
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

一键发布包：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1
```

该命令会生成发布包；除非用户明确要求，不要运行。

## GitHub Actions 与发布

- `.github/workflows/ci.yml` 仅支持手动触发，用于前端 lint 和 build 检查。
- 仓库不使用 GitHub Actions 自动构建 Release；不要新增 tag 自动构建 workflow。
- 版本发布采用本机 Windows 构建后通过 `gh` 上传，详细说明见 `docs/local-release.md`。
- 不要主动创建 tag 或发布 Release；版本构建必须等待用户明确指令。

## 运行时约束

- Mod 只读取当前游戏运行时数据，不读取 `.memory` 存档文件。
- 运行时固定数据读取成功后，C# 侧会把 `DataBaseCore` / `DataBaseCharacter` / `DataBaseLanguage` 结构化为 `RuntimeDataCatalog`，并切换 `DataRepository` 到运行时仓库；伴随窗口收到 `snapshot.runtimeData.isComplete=true` 后，普客/稀客推荐、经营中推荐、任务目标、库存修改页和自动化目标解析都必须使用这份运行时数据集。
- 本地 API 快照需要避免在 Unity 主线程高频序列化大对象。完整 `RuntimeDataCatalog` 可以被节流省略，前端必须缓存最近一次完整数据并继续使用；不要把缺失的 `snapshot.runtimeData` 当作数据不可用。新增重扫描或自动化轮询时要记录到 `performanceMs` 或复用现有耗时指标，便于概览页排查掉帧。经营扫描指标应尽量按来源拆分，例如 `business.rare.*`、`business.normal.*`、`runtime.cookerSnapshot` 和 `mission.serveTargets`；普客订单快照应优先复用短 TTL 缓存，不要在一次快照发布链路中重复枚举同一批运行时对象。
- 夜间经营订单优先使用运行时对象；日志捕获仅作为兼容和排障手段。
- 夜间经营订单必须按首次出现时间稳定显示；不得因桌号排序或推荐完整度排序让新订单插到旧订单前面。
- 经营中订单排序支持 `点单顺序` 和 `稀客分组`。默认必须保持点单顺序；稀客分组模式下，同一稀客订单放在一起，稀客组之间按该稀客最早订单出现时间排序，组内仍按点单先后排序。经营中列表、当前点单推荐、专注模式、游戏界面置顶目标和自动化第一单选择必须复用同一排序函数。
- 稀客/经营中主推荐必须先满足点单料理 Tag 和酒水 Tag；不满足点单的 fallback 只能作为明确标注的“喜好备选”，不得进入收藏置顶、自动化或一键订单使用的正式推荐数组。唯一例外是用户开启 `优先任务料理` 时，当前稀客已接取的经营投喂任务指定料理可被提升到正式推荐第一位，但仍必须通过解锁、库存和缺失厨具过滤。料理推荐优先 `foodScore >= 3`，但必须保留“满足点单且低于 3 分”的候选作为正式兜底。
- 稀客页场景候选必须优先使用运行时数据集；当 `/snapshot.recommendationState.availableRareCustomerIds` 非空时，候选还要按当前存档已记录/解锁稀客过滤。该集合为空表示暂未读取到进度，前端不得因此把候选列表清空。
- 经营中读取到已摆放厨具快照时，`排除缺失厨具` 可过滤当前场景没有对应厨具的料理；读不到快照或无法映射厨具名时不得误删推荐。料理厨具类型以游戏 `CookSystemManager` 的 `AllAvailableCookerType` 为准，前端只消费本地 API 给出的中文厨具名。
- `游戏界面置顶推荐` 和 `目标厨具高亮` 是两个独立开关。两者可以共享当前第一笔稀客订单的推荐目标，但本地 API 必须分别传递 `enabled` 与 `highlightEnabled`，C# 侧也要分别控制列表置顶补丁和厨具高亮服务。
- 稀客料理/酒水展示排序由伴随窗口设置页的排序规则控制，默认保持：料理 `满足点单 Tag -> foodScore -> 加料种类数 -> 资源压力 -> 料理售价 -> 加料成本 -> 料理 ID`，酒水 `满足点单 Tag -> bevScore -> 酒水售价 -> 酒水 ID`。新增排序项时必须同时接入稀客页、经营中页、专注模式、缓存签名和自动化当前第一单选择，不要让不同入口出现不同排序。
- 已捕获且仍能匹配当前稀客的订单不得使用短时间缓存过期清理；只应在明确移除、确认上菜完成、稀客离场或长时间硬上限后消失。
- 本地 API 监听 `127.0.0.1`，避免代理工具干扰 `localhost`；除 `/health` 外，接口必须通过伴随窗口传入的 token 访问。
- 伴随窗口单实例控制监听 `127.0.0.1:32146`；热键逻辑必须先发送 `show`/`toggle`/`exit` 控制消息，控制端口不可达时才启动伴随进程，避免手柄快捷键重复创建窗口。
- `F8` 和 `RS Click` 默认用于在游戏和伴随窗口之间切换焦点；伴随窗口聚焦时由 Tauri 前端处理热键并调用后端按设置切回游戏窗口。焦点行为不能写死为隐藏窗口，必须支持保持伴随窗口悬浮只切焦点。手柄切换必须做释放锁存和可配置后端防抖，避免一次长按在两侧窗口间反复触发。伴随窗口内手柄焦点必须优先遵守 `data-gamepad-scope` 区域，不要让顶部页签横向移动跳入页面内容。
- 伴随窗口透明度通过 Tauri transparent window 和前端 CSS 背景透明度实现；文本、按钮和标签不要整体设置 opacity，以免影响可读性。
- 伴随窗口根滚动区域必须预留稳定纵向滚动条槽位，避免页面内容因滚动条出现或消失产生横向跳动。
- 伴随窗口滚动条样式必须跟随主题和窗口透明度；不要使用全局 `*::-webkit-scrollbar` 覆盖会刻意隐藏滚动条的导航栏。
- 伴随窗口内手柄导航应优先处理同一行内的左右移动；range 滑杆获得焦点时，左右方向应调节数值而不是跳到其他按钮。收藏按钮等点击后会改变 UI 状态的控件，应使用稳定 `data-gamepad-focus-key` 恢复焦点。
- 经营中、专注模式和日志等实时页面的动态内容区应保留稳定容器和紧凑空状态；不要因为暂无订单、暂无预约或暂无日志就直接卸载整块区域，避免数据刷新时页面大幅跳动。
- Unity 场景切换后不要再用固定秒数等待来规避加载问题；运行时、经营和普客扫描应立即尝试读取。任务扫描允许跳过切场景后的前几个 Unity 帧，避免与 DayScene UI `Awake/Initialize` 同帧竞争。读取代码必须避开不稳定的 IL2CPP 托管枚举路径，尤其不要直接依赖 `IEnumerator.Current`；优先使用 Count/indexer、字段、静态快照或可空单例。读取失败应降级为状态提示并等待下一轮刷新。
- 运行时库存修改必须排队到 Unity 主线程执行，避免本地 API 网络线程直接写游戏对象。
- 运行时库存修改页只保留快捷操作，当前为单项 `-10`、`+10`、`99`，以及当前存档可编辑材料/酒水批量设为 `99`；不要恢复自定义数量输入、`+1` 或单独“应用”按钮，除非用户明确要求。
- `任务` 页读取当前进度可接取、进行中或可完成的交互任务。任务状态优先对 `RunTimeScheduler.trackingMissions` 中每条任务调用只读 `RunTimeScheduler.ParseActiveMissionData()`，映射为 `available`、`tracking`、`fulfilled`；同时必须主动刷新 `TrackedMissionData.UpdateFinishStates()`，并用 `HasFulfilled/get_HasFulfilled` 与 `conditionFinishStates` 全部完成作为 `fulfilled` 强兜底，避免 IL2CPP tuple 或枚举读取失败时把可完成任务显示为进行中；已完成任务不在筛选分类中展示。NPC 交谈任务优先使用 `RunTimeScheduler.GetAvailableInteractMissionForCharacter()`，但该接口表示“当前可交互推进”，不等同于未接取；若返回的 label 已在 `trackingMissions` 中，应按 `fulfilled` 展示。真正未接取、由 NPC 对话事件触发的任务，需要只读扫描 `RunTimeScheduler.scheduledEvents` 的当前修正日和 `-1` 桶，通过 `DataBaseScheduler.RefEvent()` 读取 `EventNode.postMissions` / `postMissionsAfterPerformance`，并只接受 `OnTalkWithCharacter` 或通过 `CheckCharacterInteractEvent()` 门控的 `KizunaCheckPoint` 触发事件；这些任务状态为 `available`。候选角色来源包括 `DataBaseDay.GetAllNPCKeys()`、`DataBaseDay.AllMappedNPCsMapping` / `AllNPCsMapping` / `allNPCs`、`RunTimeDayScene.trackedNPCs`、`DaySceneMap.allCharacters` 和当前场景 `CharacterConditionComponent`。场景调查任务读取 `RunTimeDayScene.trackedInteradctables` 与 `MissionInteractConditionComponent`，再只读匹配 `RunTimeScheduler.trackingMissions` 中 `MissionNode.FinishCondition.ConditionType.InspectInteractable` 的任务。经营投喂任务只允许读取 `MissionNode.FinishCondition.ConditionType.ServeInWork`、mission `reciever` 和 `RunTimeScheduler.ContainsSpecialNPCServeInWorkMission()`；不得调用 `TryTriggerServeMission()` 或其他会改变任务状态的方法。NPC 所在场景优先从 `RunTimeDayScene.trackedNPCs` 的 mapLabel 反查，并用 `DaySceneLanguage.GetMapLanguageData()` 显示地图中文名称；tracked 数据为空时，用 `DataBaseDay.RefNPC()` 的 `possibleDestinations[].spawnMarker` 经 `GetMapLabelFromSpawnMarker()` 解析为可能场景。NPC 显示名必须优先用 `DaySceneLanguage.RefDaySceneName()`，再回退到 `SchedulerNode.Character.GetLanguageData()`，不要直接显示英文 label 或 `NPC.ToString()`。不要再用 `HaveMissionStarted()` 过滤已追踪任务，因为它本身就是检查任务是否在 `trackingMissions` 中；但调度事件后置任务必须过滤已开始和已完成的 label，避免重复显示。读取失败必须显示分来源诊断信息，不得回退到静态全任务列表误导用户。
- 普客订单自动化必须建立在只读诊断可识别订单的前提下。经营中页诊断和执行路径都需要复用健壮的 IL2CPP 枚举与单例查找逻辑，至少读取 `OrderController.GetShowInUIOrders()`、HUD `OrderingElement.ActiveOrder`、`GuestsManager` 的 Presented/Desk/DeskMap/Repellable/ManualDesk 控制器以及 Queue 控制器中的 `AllOrders`/`AllOrdersData`/`PeekOrders()`。当前普客执行入口按首次出现顺序并发处理仍需启动料理的未满足订单，并发上限来自 `CompanionPreferences.autoNormalConcurrency`；已经开始制作、等待 pending 自动收取或已收至保温箱的订单不得继续占用新的调度名额，但“已开始制作”不能作为永久终态，长时间未收进暂存容器时必须按 `autoNormalStorageWaitSeconds` 重新询问后端并恢复调度。普客自动化需要独立于稀客节拍快速轮询，并在普客订单 key、料理、酒水或送达状态变化时绕过节流立即处理一轮。普客自动化只保留自动开始料理和料理完成后写入游戏料理暂存容器 `IzakayaConfigure.StoreFood()` 两步；保温箱不是 `CookController` 空位，不要再扫描厨具控制器或调用 `CookController.Store()`。不得直送酒水，不得写入 `ServFood/ServBeverage/ServedFoodInAir`，不得调用 `EvaluateOrder`、`CookController.AfterPlayerExtract` 或补写送达副作用。每笔普客订单需要独立记录已开始料理、已收至保温箱和暂停状态；C# pending 和短期已收取回执必须优先绑定运行时 `orderKey`，只有缺少 key 时才退回桌号/料理兜底。同一订单已有 pending 料理时只能等待，不能因场景里有多个同类厨具而重复制作。短期“已收至保温箱”回执不能作为永久事实，下一轮必须尝试读取 `IzakayaConfigure.StoredFoods` / `get_StoredFoods()` 复查；若容器可读且目标料理不存在，应撤销回执并允许重新开锅，若容器暂不可读则保留回执并继续周期性复查。稀客和普客开锅前必须共用前端同一轮厨具预约表，按已摆放厨具快照计算同类厨具容量；容量不足时优先保留普客待处理订单，稀客只能等待料理开锅但仍可继续处理取酒或完成订单。
- 普客订单中的客人、料理和酒水名称只能使用本地数据仓库名称或明确可读文本。`GameData.CoreLanguage.LanguageBase`、`Il2Cpp*`、`GameData.*` 这类运行时类型名必须过滤掉；普客订单去重也不得依赖不稳定的客人文本。
- 自动化能力是实验性功能，必须由设置页总开关控制；总开关关闭时经营中页不显示自动化配置，也不执行任何自动化动作。稀客并发、普客并发、稀客送餐盘等待、普客保温箱复查、最大重试和最大回退都必须走 `CompanionPreferences`，默认分别为 `2`、`3`、`30s`、`45s`、`3`、`2`；完成订单写入每轮最多执行 1 笔，避免多订单同时修改运行时对象。普客订单处理必须额外由经营中自动化面板的“启用普客处理”子开关启用，开启后不保留手动处理按钮，由伴随窗口轮询自动执行。稀客阶段配置使用 `autoPrep*`，普客阶段配置使用 `autoNormal*`。普客只保留开始料理、收至保温箱和出错暂停，不要恢复取酒、直送订单、完成订单或 QTE 配置开关。子选项默认关闭但记忆用户上次配置。
- 自动化遇到厨具占用、目标成品未进入送餐盘/保温箱、运行时订单或厨具对象短暂不可读等临时状态时，应保持可重试；不得因为一次失败永久停止。`出错时暂停` 只用于非临时错误。稀客暂停状态和普客暂停状态必须隔离；普客的非临时错误只暂停对应订单 key，不得影响稀客自动化或其他普客订单。
- 自动化状态机只把实际动作视为进展，例如取酒、开锅、收取、单项送达、写入订单或触发评价；不要把“选择订单”“匹配订单”这类前置成功当作进展，否则会掩盖真实卡住。稀客完成订单必须允许料理和酒水分别送达，只要目标项已进入订单就应通过 `hasServedFood/hasServedBeverage` 校准前端状态，释放送餐盘压力；只有订单 `get_IsFullfilled()` 为真时才能触发 `EvaluateOrder()`。稀客目标料理/酒水长时间未进入送餐盘且未送达时允许按订单状态回退到上一实际步骤重新执行，超过回退上限再暂停。普客目标料理长时间未进入暂存容器时，应先重新询问后端 pending 待收取任务；后端仍报告已在制作或等待收取时，前端刷新等待时间并清理旧回退计数，不得直接暂停。只有 pending 消失且需要重新开锅时，才重新发起普客制作。普客已收至保温箱后也要按保温箱复查间隔重新调用后端确认：后端若能验证目标料理仍在暂存容器中则继续等待玩家送达，若能验证目标料理缺失则前端必须清除 `collected` 状态并回到可调度流程。C# pending 收取、开锅成功/失败和 pending 移除需要写入 `BepInEx/config/MystiaStewardCompanion/automation-jobs.log`，连续相同日志必须合并为 `repeat` 摘要，伴随窗口日志页需要能通过 `/logs/automation` 有上限读取并展示结构化作业记录，也需要能通过 `/logs/export-diagnostics` 导出包含 snapshot 和日志尾部的诊断 zip；诊断日志和诊断包都必须有大小上限并且不能影响游戏流程。
- 稀客订单进入自动化后必须锁定本订单的料理、加料、酒水和可接受成品料理 ID 列表；后续轮询即使库存或排序导致推荐列表变化，也不能改用新的第一推荐，除非用户重置该订单或订单自然结束。当正式推荐料理为空时，自动化可以选择“喜好备选”料理，并必须在状态中标识；只处理收藏配方开启时，喜好备选也必须命中收藏。完成订单时若锁定目标料理不在送餐盘中，可以复用已在送餐盘中堆积超过阈值且属于该订单可接受成品列表的料理；不要复用未锁定的“喜好备选”或未知来源料理。
- 稀客页下拉选项只按经营场景、可用中文名称和可用点单 Tag 过滤，不再按 `availableRareCustomerIds` 或其他存档进度集合过滤。当前进度过滤来源不稳定，容易误删可测试稀客；后续若要恢复，必须基于游戏运行时明确的已解锁字段重新实现。
- `任务` 页的稀客邀请必须走日间羁绊邀请链路，不再写 `Story.SpecialGuestControlled`。候选扫描支持 `current` 和 `all` 两种范围：`current` 优先通过 `DayScene.SceneManager.CurrentActiveMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`DaySceneMap.allCharacters` 和场景 `CharacterConditionComponent` 读取当前日间场景 NPC，并用 `DataBaseDay` 的 NPC 目的地补足；`all` 可从 `DataBaseDay.GetAllNPCKeys()`、`AllMappedNPCsMapping`、`AllNPCsMapping` 或 `allNPCs` 读取全部日间 NPC 并解析所在地图。候选经 `DataBaseCharacter.RefSGuest()` 映射到 `SpecialGuest`，再统一检查 `StatusTracker.HasNPCInvited()`、`RunTimeAlbum.GetOrGenerateSpecialNPCKizunaLevel()`、当前等级成功邀请对话包和 `RunTimeDayScene.RefTrackedNPCAvailability()`。不满足条件的候选必须保留在列表中并返回原因，只禁用邀请按钮；列表、单独邀请和全部邀请必须复用同一套候选扫描与条件判定。符合条件后调用 `StatusTracker.RecordInvitedGuest()` 写入今晚邀请名单。不要调用 `DaySceneChatSelectionPannel.InviteSpecGuest()`，该方法会触发随机成功率并记录今日已尝试，会把可邀请但随机失败的稀客错误跳过。不要把 `StatusTracker.HasTemptInvited()` 作为跳过条件，避免旧版本或手动失败尝试阻止邀请。该功能不得直接刷客、不得推进日间时间、不得修改受控稀客队列。
- 运行时稀客订单捕获只能长期显示仍匹配当前活跃稀客的订单；未匹配活跃稀客的捕获订单只允许短暂宽限，避免跨伴随窗口重启或跨天残留。离开夜间经营场景或清除运行时状态时必须清空捕获缓存。经营中稀客订单行的删除按钮调用 `/orders/rare/dismiss` 清理插件端缓存，不应只做前端隐藏。
- 稀客自动化诊断必须按订单 key 展示，不只依赖长文本日志。每笔当前候选订单需要显示步骤、已处理阶段、重试/回退次数和最近原因，并提供单笔重试和重置。重试只解除该订单暂停并保留已完成阶段；重置只清除该订单本地状态。两者都不得影响其他稀客订单、普客订单或自动化总开关。
- 经营中页需要显示自动化资源状态：厨具预约按当前已摆放厨具容量展示普客和稀客的本轮预计占用，送餐盘压力按稀客已取/已收但尚未送达的料理和酒水展示。该视图只用于诊断和用户判断，不应反向驱动自动化状态机。
- 稀客订单完成前必须同时校验送餐盘料理和酒水，不要只返回或处理第一个缺失项。料理长时间未进入送餐盘触发回退时，需要重新校验酒水状态；如果 C# 侧已有同一目标料理的 pending 自动收取任务，前端/后端都应等待该任务完成，不得因厨具冻结或 Debuff 重复开同一道料理。
- 自动开始料理固定尝试完成原生 QTE 奖励结算，不再提供跳过或完成 QTE 的配置开关。该功能不打开游戏音游面板，只尝试调用游戏 QTE 成功奖励入口；运行时失败时返回诊断信息，不应中断已开始的料理。
- 游戏内料理/酒水列表置顶是实验性功能，只允许重排已生成的 UI 列表，不得自动点击或绕过游戏自身筛选；本地 API 更新置顶目标失败时必须静默降级。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，必须保留后端读取上限和前端显示上限，避免无限累积日志。
- BepInEx 控制台窗口默认由 Mod 写入 `BepInEx.cfg` 在下次启动关闭，并在 Windows 上隐藏已创建的控制台窗口；伴随窗口 `设置` 页可通过本地 API 临时开启/关闭原生日志窗口，修改时必须同时更新当前窗口可见性和下次启动配置。
- 游戏内不再保留 IMGUI 面板；游戏侧只负责后台读取、自动化执行、本地 API 和伴随窗口唤起，所有用户交互放在独立伴随窗口。

## 文档维护

- 用户安装和使用写入 `mods/bepinex/README.md`。
- 开发和构建写入 `mods/bepinex/README.dev.md`。
- 机制或运行时读取路径变化时，同步更新 `docs/` 和 `mods/bepinex/docs/`。
- 用户可见功能、快捷键、设置项、自动化行为、页面布局或本地 API 变化后，必须在提交前同步文档。
- 版本发布前如果文档落后，先补文档再提交版本号并合并 `main`。
