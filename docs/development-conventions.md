# 开发约定与流程

更新日期：2026-07-02

## 代码边界

- 仓库维护 BepInEx Mod 与 Tauri 伴随窗口。
- 伴随窗口入口为 `apps/companion/src/companion/ModWorkbench.tsx`，顶层挂载在 `apps/companion/src/App.tsx`。
- 推荐算法核心集中在 `apps/companion/src/recommendation-engine/`；经营中订单推荐由 `apps/companion/src/companion/domain/service-recommendations.ts` 组装，并通过 `apps/companion/src/companion/workers/order-recommendations.worker.ts` 放到 worker 中计算。普客页和稀客页的页面推荐通过 `apps/companion/src/companion/workers/page-recommendations.worker.ts` 计算，页面组件只负责选择状态和结果渲染，不得把候选搜索、排序或自定义料理合并重新放回 React render 链路。
- 推荐、库存名称、任务目标和自动化目标使用 Mod 从游戏运行时读取并通过本地 API 发布的结构化数据；运行时数据未就绪时，伴随窗口显示等待状态。
- C# Mod 不引用 TypeScript 模块；前端和 Mod 的共享数据通过本地 API 的运行时快照传递。新增稀客事件变体时，优先确认游戏运行时映射和别名归一化逻辑。

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
- 伴随窗口 UI 基础组件统一放在 `apps/companion/src/components/ui/`。按钮、输入框、选择框、页签、卡片、徽标、开关、滑杆、选项组、折叠面板、状态卡片、空状态和信息字段都优先使用该目录组件，不要在业务页面复制外部模板组件或手写第二套样式。
- UI 原语以 Mantine 组件为交互基础，通过 `apps/companion/src/components/ui/` 和 `components/ui-kit` 做项目级适配；样式由 Mantine theme、项目 CSS token 和少量尺寸归一 wrapper 控制。新增组件要保持工具型窗口的紧凑布局、企业控制台式扁平分组、直角窄边框、弱动画和可读高对比；除开关、滚动条滑块等需要圆形几何的控件外，不再使用圆角。列表和推荐优先使用表格化行项目，不引入通用后台模板、玻璃拟态、过度圆角、卡片堆叠或独立视觉体系。
- 伴随窗口布局必须覆盖 Tauri 主窗口最小宽度 720px。常规双列密集面板应在 720px 及以上保持双列，仅低于最小窗口限制时退回单列；顶部工具条、状态摘要、滑杆和表单控件不得通过提高窗口最小宽度、隐藏关键信息或横向溢出来规避问题；UI 巡检需包含 720px 最小宽度截图和关键布局断言。
- 页面级代码应继续按页面和业务域拆分到 `apps/companion/src/companion/pages/`、`domain/`、`hooks/`、`features/` 或 `workers/`。新增页面时先复用 `ListPanel`、`InfoLine`、`EmptyState`、`SwitchField`、`SliderField`、`SegmentedControl`、`Tree` 和 `Accordion`，避免页面层样式混乱。

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
- 自动更新发布只支持稳定版 `X.Y.Z` 和预览版 `X.Y.Z-preview.N`。预览版必须发布为 GitHub Prerelease，用于 `dev` 上验证自动更新链路；稳定版确认后再合并 `main` 并发布普通 Release。
- GitHub Release 需要上传 Mod 主包、`update-manifest.json` 和独立 Windows x64 伴随窗口 EXE：`mystia-steward-companion-companion-windows-x64.exe`；如发布机已配置 Android 工具链和签名配置，可通过 `build-release.ps1 -BuildAndroidApk` 或 `publish-release.ps1 -BuildAndroidApk` 生成并上传按 ABI 拆分的 Android APK，默认资产为 `mystia-steward-companion-android-arm64-v8a.apk` 和 `mystia-steward-companion-android-armeabi-v7a.apk`。`update-manifest.json` 只服务 Mod 自动更新，必须继续指向 `mystia-steward-companion-bepinex.zip`，不要把独立伴随窗口 EXE 或 Android APK 纳入自动更新清单。
- 不要主动创建 tag 或发布 Release；版本构建必须等待用户明确指令。
- 用户和测试文档中的 BepInEx 安装版本优先固定到已验证的 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.783+c58c42d.zip`。不要笼统推荐最新 Bleeding Edge；#784 及之后构建若要恢复支持，需要先通过实测和运行时日志确认。
- Android APK 不是 Windows 伴随窗口 EXE 的转换产物。Android 版按 Tauri mobile 目标维护，只作为 B 设备 LAN 伴随窗口；桌面托盘、置顶、鼠标穿透、焦点切换、单实例控制和游戏关闭自动退出必须继续隔离在桌面平台代码中。Android applicationId 固定为 `com.tyukki.mystia.steward.companion`；不要使用带连字符的产品名作为 Android 包名。桌面 Tauri identifier 继续使用既有值，Android 通过 `apps/companion/src-tauri/tauri.android.conf.json` 单独覆盖 identifier，避免影响桌面端本地数据目录。仓库保留 `apps/companion/src-tauri/gen/android/` 工程，Gradle Rust 插件必须通过 Corepack 调用 pnpm。Android 发布 APK 默认通过 `--split-per-abi --target aarch64 armv7` 构建，避免 universal fat APK；`pnpm tauri:android:apk:signed` 读取被 Git 忽略的 `apps/companion/src-tauri/gen/android/keystore.properties`，构建后用 `apksigner verify` 验签并复制 `mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk` 和 `mods/bepinex/dist/mystia-steward-companion-android-armeabi-v7a.apk`；`build-release.ps1 -BuildAndroidApk` 和 `publish-release.ps1 -BuildAndroidApk` 只是复用该签名构建流程，不允许把 keystore、密码和签名配置提交。Android Gradle 已关闭 Kotlin incremental compilation，避免 Windows 上 Cargo registry 与项目分属不同盘符时出现 Kotlin daemon 相对路径报错。APK 需要 Android 工具链构建、签名和真机验证，作为独立 Release 资产上传，不参与 Mod 自动更新。
- Android APK 体积优化只允许在 Android 构建脚本中通过 `CARGO_PROFILE_RELEASE_*` 环境变量启用；不要把 `lto` 或 `codegen-units` 写入全局 Cargo release profile，避免普通 Windows `build-release.ps1` 构建被 Android 优化拖慢。

## 运行时约束

- Mod 只读取当前游戏运行时数据，不读取 `.memory` 存档文件。
- 运行时固定数据读取成功后，C# 侧会把 `DataBaseCore` / `DataBaseCharacter` / `DataBaseLanguage` 结构化为 `RuntimeDataCatalog`，并切换 `DataRepository` 到运行时仓库；伴随窗口收到 `snapshot.runtimeData.isComplete=true` 后，普客/稀客推荐、经营中推荐、任务目标、库存修改页和自动化目标解析都必须使用这份运行时数据集。
- 本地 API 快照需要避免在 Unity 主线程高频序列化大对象。完整 `RuntimeDataCatalog` 可以被节流省略，前端必须缓存最近一次完整数据并继续使用；不要把缺失的 `snapshot.runtimeData` 当作数据不可用。快照内容签名未变化时后端应复用上一份缓存 JSON，不要为了 `CapturedAtUtc` 或性能数字重复序列化。前端仍必须按成功返回的快照更新连接状态，避免跳过运行时目录恢复。运行时固定数据读取完成后不得在经营快照热路径重复刷新，未完成时也要做重试间隔保护；经营诊断只能复用已缓存的运行时目录快照。新增重扫描或自动化轮询时要记录到 `performanceMs` 或复用现有耗时指标，便于概览页排查掉帧；性能快照只保留近期样本，避免旧耗时长期误导判断。经营扫描指标应尽量按来源拆分，例如 `business.rare.*`、`business.normal.*`、`runtime.cookerSnapshot` 和 `mission.serveTargets`；普客订单快照应优先复用短 TTL 缓存，不要在一次快照发布链路中重复枚举同一批运行时对象。
- 夜间经营订单优先使用 `SpecialOrderRuntimeCapture` 运行时捕获缓存；捕获缓存为空、诊断开启、需要初始化/回退校验，或捕获缓存有订单但本轮可接受订单少于缓存数量时，再扫描 OrderController、HUD、服务面板和桌位控制器补位。控制器扫描仍要读取活动稀客和预算资金信息，但不应在已有完整捕获订单时重复做完整订单反射扫描。
- 夜间经营订单必须按首次出现时间稳定显示；不得因桌号排序或推荐完整度排序让新订单插到旧订单前面。
- 经营中订单排序支持 `点单顺序` 和 `稀客分组`。默认必须保持点单顺序；稀客分组模式下，同一稀客订单放在一起，稀客组之间按该稀客最早订单出现时间排序，组内仍按点单先后排序。经营中列表、当前点单推荐、专注模式、游戏界面置顶目标和自动化第一单选择必须复用同一排序函数。
- 稀客/经营中推荐使用统一料理/酒水列表：满足点单 Tag 的候选优先，不满足点单但命中稀客偏好的候选直接进入同一列表并标注 `偏好备选`。不得再维护满足点单与喜好备选两套结果数组。自定义推荐料理必须从 `custom-recipes.json` 转换为普通料理候选参与同一套硬过滤、预算、排序、自动化和 UI 展示，不得再通过 `favorites.source=manual` 或其他兼容路径混入收藏体系。当前稀客已接取的经营投喂任务指定料理可通过 `任务料理置顶` 开关在硬过滤后置顶；自定义推荐料理的单条置顶、收藏料理和收藏酒水分别按独立规则置顶。置顶不得绕过解锁、库存、预算阻止、排除项和缺失厨具过滤。料理推荐优先 `foodScore >= 3`，但必须保留“满足点单且低于 3 分”的候选作为兜底。
- 稀客页场景候选必须优先使用运行时数据集；只按经营场景、可用中文名称和可用点单 Tag 过滤，不再按当前存档记录/解锁进度过滤。当前进度来源不稳定，容易误删可测试稀客；后续若要恢复，必须先基于游戏运行时明确的已解锁字段重新实现。
- 经营中读取到已摆放厨具快照时，`排除缺失厨具` 可过滤当前场景没有对应厨具的料理；读不到快照或无法映射厨具名时不得误删推荐。料理厨具类型以游戏 `CookSystemManager` 的 `AllAvailableCookerType` 为准，前端只消费本地 API 给出的中文厨具名。设置页的 `同基础料理显示` 控制同一基础料理在稀客页和经营中推荐中最多展示多少个加料变体；该限制只裁剪 UI 展示行。经营中展示行应从完整料理/酒水候选直接派生，自动化目标应从独立执行候选构造，不能只依赖裁剪后的 UI 行，也不能让执行候选上限提前裁掉用于补位的偏好候选。
- `游戏界面置顶推荐` 和 `目标厨具高亮` 是两个独立开关。两者可以共享当前第一笔稀客订单的推荐目标，但本地 API 必须分别传递 `enabled` 与 `highlightEnabled`，C# 侧也要分别控制列表置顶补丁和厨具高亮服务。
- 游戏界面置顶只能在料理、材料和酒水面板字段刷新后重排游戏已经生成的列表；不得 Hook `RunTimePlayerData.CheckPinned` 这类全局高频查询，也不得通过伪装原生收藏状态实现推荐置顶。
- 稀客料理/酒水展示排序由伴随窗口设置页的置顶开关、自定义推荐料理置顶和推荐权重共同控制。硬过滤后先应用任务料理、自定义推荐料理、收藏料理、收藏酒水置顶，再由 `RecommendationSortProfile` 的启用项、权重、方向和预设共同决定顺序；默认均衡预设综合稀客偏好、厌恶风险、加料数量、资源压力、成本、利润、酒水库存和当前厨具可做。新增排序或置顶规则时必须同时接入稀客页、经营中页、专注模式、缓存签名和自动化当前第一单选择，不要让不同入口出现不同排序。
- 推荐 tag 解析统一维护在 `apps/companion/src/recommendation-engine/tag-resolution.ts`，动态料理 tag 维护在 `dynamic-food-tags.ts`。运行时导出的 `tagPriorityRules` 优先级最高；运行时缺失时只允许使用 `PROJECT_VERIFIED_TAG_PRIORITY_RULES` 这组项目验证规则，不得在其他模块重新硬编码互斥/压制关系。新增或调整 tag 规则必须来自游戏运行时行为、反编译资料或可复现实测，并同步更新该集中模块和相关文档。
- 运行时料理配方的基础食材必须作为数量敏感序列处理，重复项表示同一材料需要多份；`RuntimeDataCatalog.Recipes[].Ingredients` 和前端 `RecipeCatalogItem.ingredients` 不得去重。只有 tag、场景、ID 列表等集合语义字段可以在解析和归一化时去重。推荐加料槽位、大份动态 tag、基础成本和自动化下单保护都必须基于保留重复后的真实基础食材数量。
- 已捕获且仍能匹配当前稀客，或仍能从原运行时订单对象和控制器确认未完成的订单，不得使用短时间缓存过期清理；只应在明确移除、确认上菜完成、稀客离场或长时间硬上限后消失。
- 本地 API 必须始终保留 `127.0.0.1` 回环监听，避免 LAN 配置错误导致用户无法从 A 设备本机恢复；`LocalApi.AllowLanConnections=true` 只会额外启用 LAN listener。LAN listener 必须限制私网来源，连接配置和 Token 重置端点只能由回环客户端调用；Tauri 代理只接受 loopback/private/link-local IPv4 endpoint，除 `/health` 外所有接口都必须通过伴随窗口传入的 token 访问。
- 每个伴随窗口必须生成稳定客户端 ID，并通过本地 API header 传给 Mod。订单自动化端点必须由 Mod 本地 API 的自动化 lease 仲裁，同一时间只允许一个客户端执行自动化；其他客户端可以读取快照和配置普通偏好，但不得绕过 lease 直接调用订单自动化动作。连接断开或快照错误时，前端可以保留最近一次只读展示，但不得继续用旧快照驱动自动化、游戏界面置顶目标或其他写入游戏运行时的后台动作。断线重连期间应优先轮询轻量 `/health`，确认 API 恢复后再读取 `/snapshot`；Android 端应直接使用 WebView `fetch` 访问 LAN API，桌面端 Tauri 代理必须在线程池执行阻塞 TCP 请求，避免同步 command 卡住 WebView。
- 伴随窗口单实例控制监听 `127.0.0.1:32146`；热键逻辑必须先发送 `show`/`toggle`/`exit` 控制消息，控制端口不可达时才启动伴随进程，避免手柄快捷键重复创建窗口。
- `F8` 和 `RS Click` 默认用于在游戏和伴随窗口之间切换焦点；伴随窗口聚焦时由 Tauri 前端处理热键并调用后端按设置切回游戏窗口。焦点行为不能写死为隐藏窗口，必须支持保持伴随窗口悬浮只切焦点。手柄切换必须做释放锁存和可配置后端防抖，避免一次长按在两侧窗口间反复触发。伴随窗口内手柄焦点必须优先遵守 `data-gamepad-scope` 区域，不要让顶部页签横向移动跳入页面内容。Tabs、SegmentedControl、横向按钮组和 slider 这类复合控件必须使用组件级语义处理方向键；通用空间导航必须按交叉轴对齐优先选择候选，不允许依赖只看中心点距离的全局几何搜索碰运气。
- 伴随窗口透明度通过 Tauri transparent window 和前端 CSS 变量实现；背景透明度只影响窗口背景、面板、弹层和滚动条轨道，文字透明度只影响普通文字、图标和辅助徽章内容，主操作按钮必须保持可读。不要用 Windows `SetLayeredWindowAttributes(..., LWA_ALPHA)` 或其他整窗 alpha 实现背景透明度，因为它会让文字和图标一起变淡。
- 鼠标穿透锁定必须通过 Tauri 原生窗口 `set_ignore_cursor_events` 实现，不能只使用 CSS `pointer-events`。`F10` 负责切换鼠标穿透；`F8`、`RS Click`、托盘显示/重连和单实例 `show` 控制消息必须自动关闭穿透，避免窗口被唤回后仍无法点击。
- 伴随窗口根滚动区域必须预留稳定纵向滚动条槽位，避免页面内容因滚动条出现或消失产生横向跳动。
- 伴随窗口滚动条样式必须跟随主题和背景透明度；不要使用全局 `*::-webkit-scrollbar` 覆盖会刻意隐藏滚动条的导航栏。
- 伴随窗口内手柄导航应优先处理同一行内的左右移动；`TabsList` 左右键只在当前页签组内移动，下键进入当前 active panel，panel 顶部上键回到控制该 panel 的 trigger；`SegmentedControl` 使用可见 label 作为手柄焦点目标，左右键只在当前选项组内切换；横向按钮组和工具条统一使用 `data-gamepad-axis="x"` 约束左右移动。通用 content 空间导航左右移动必须优先垂直对齐候选，上下移动必须优先水平对齐候选，避免焦点从上方表单跳到下方列表按钮。range 和 Mantine `[role="slider"]` 获得焦点时，左右方向应调节数值而不是跳到其他按钮。Select/MultiSelect 下拉框获得焦点时不得因方向键自动展开，必须由确认键展开，展开后方向键才用于移动选项。收藏按钮等点击后会改变 UI 状态的控件，应使用稳定 `data-gamepad-focus-key` 恢复焦点。Gamepad API 轮询必须使用集中输入状态机，按键读取同时检查 `pressed` 和模拟量 `value`，并在窗口 blur、页面隐藏、手柄断开、无手柄或禁用导航时清理 latch；确认/收藏动作在 active element 丢失时应优先使用上一手柄高亮元素继续执行，不把本次按键只消耗在重新找焦点上。
- 经营中、专注模式和日志等实时页面的动态内容区应保留稳定容器和紧凑空状态；不要因为暂无订单、暂无预约或暂无日志就直接卸载整块区域，避免数据刷新时页面大幅跳动。
- 帮助页内容必须保存在 `apps/companion/src/data/help-content.json`，前端只负责搜索、目录树和详情渲染。新增用户可见功能或排查流程时，同步更新帮助 JSON，避免只改 README。
- Unity 场景切换后不要再用固定秒数等待来规避加载问题。日间任务列表、日间地图和稀客邀请必须通过运行态数据入口判断可读性：排除主菜单、夜间经营和经营准备后，优先读取 `DayScene.SceneManager.CurrentActiveMapLabel` / `TargetMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`RunTimeDayScene.RefTrackedNPCAvailability()` 和 `RunTimeScheduler` 数据；不能把 `DaySceneSustainedPannel` 是否激活作为日间数据总门禁，否则常规日间场景会被误判为 UI 初始化中。夜间经营准备读取仍以 `PrepNightScene.UI.IzakayaConfigPannel.OnPanelOpen` / `GoToSpecific` 为 ready 信号，并用 `Cleanup_Generated` / `GotoWork` 清理；进入夜间经营准备时，只能用 `WorkPrepScenePannelRoot` 下活跃的 `IzakayaConfigPannelNew` 和 ready 信号阻断日间读取，不能用泛化的同名面板或残留对象判断。读取代码必须避开不稳定的 IL2CPP 托管枚举路径，尤其不要直接依赖 `IEnumerator.Current`；优先使用 Count/indexer、字段、静态快照或可空单例。读取失败应降级为状态提示并等待下一轮刷新。
- 运行时静态目录（料理、材料、酒水、普客、稀客、场景）和玩家存档状态（库存、已解锁、流行 Tag、已摆放厨具）必须分层读取。静态目录可用后应立即发布给伴随窗口，让任务、邀请、普客和稀客基础选项可用；玩家存档状态读取失败时只影响库存和推荐可用性，不应阻塞任务和邀请。基础推荐状态的库存、酒水和已解锁料理必须以 `RunTimeStorage.GenerateSaveData()` 生成的单份运行时存储快照为权威来源，`recipes` 为空时等待下一轮读取而不是发布空可用料理集合；玩家等级、流行 Tag、明星店开关和联动状态使用 `RunTimePlayerData` / `RunTimeDayScene` 轻量 getter 或静态字段读取。夜间经营准备阶段在准备面板 ready 后允许读取这些基础玩家运行态，用于修改页、普客页和稀客页；但仍不得读取 DayScene 快照、任务列表或稀客邀请。
- 主菜单 `Main Scene` / `MainMenuPannel` 必须在代码中显式视为非游戏场景，不要只依赖 `NonGameplaySceneKeywords` 默认配置，因为用户已有配置文件不会自动补充新关键词。非游戏场景下不得读取运行时静态目录或玩家存档状态，避免 DataBase/Language 初始化不完整时触发 Unity 空引用。
- 日间任务、邀请和当前地图读取必须依赖 `DayScene.SceneManager`、`RunTimeDayScene`、`DataBaseDay` 和 `RunTimeScheduler` 的运行态对象。若这些入口缺失或当前地图 label 为空，读取服务应返回分来源诊断；不要在外层统一返回“日间 UI 初始化中”。
- 运行时库存修改必须排队到 Unity 主线程执行，避免本地 API 网络线程直接写游戏对象。
- 运行时库存修改页的材料和酒水列表支持按名称或库存排序；操作只保留单项 `-10`、`+10`、`99`，以及当前存档可编辑材料/酒水批量设为 `99`；不要恢复自定义数量输入、`+1` 或单独“应用”按钮，除非用户明确要求。
- `任务` 页读取当前进度可接取、进行中或可完成的交互任务。任务状态优先对 `RunTimeScheduler.trackingMissions` 中每条任务调用只读 `RunTimeScheduler.ParseActiveMissionData()`，映射为 `available`、`tracking`、`fulfilled`；同时必须主动刷新 `TrackedMissionData.UpdateFinishStates()`，并用 `HasFulfilled/get_HasFulfilled` 与 `conditionFinishStates` 全部完成作为 `fulfilled` 强兜底，避免 IL2CPP tuple 或枚举读取失败时把可完成任务显示为进行中；已完成任务不在筛选分类中展示。NPC 交谈任务优先使用 `RunTimeScheduler.GetAvailableInteractMissionForCharacter()`，但该接口表示“当前可交互推进”，不等同于未接取；若返回的 label 已在 `trackingMissions` 中，应按 `fulfilled` 展示。真正未接取、由 NPC 对话事件触发的任务，需要只读扫描 `RunTimeScheduler.scheduledEvents` 的当前修正日和 `-1` 桶，通过 `DataBaseScheduler.RefEvent()` 读取 `EventNode.postMissions` / `postMissionsAfterPerformance`，并只接受 `OnTalkWithCharacter` 或通过 `CheckCharacterInteractEvent()` 门控的 `KizunaCheckPoint` 触发事件；这些任务状态为 `available`。候选角色来源包括 `DataBaseDay.GetAllNPCKeys()`、`DataBaseDay.AllMappedNPCsMapping` / `AllNPCsMapping` / `allNPCs`、`RunTimeDayScene.trackedNPCs`、`DaySceneMap.allCharacters` 和当前场景 `CharacterConditionComponent`。场景调查任务读取 `RunTimeDayScene.trackedInteradctables` 与 `MissionInteractConditionComponent`，再只读匹配 `RunTimeScheduler.trackingMissions` 中 `MissionNode.FinishCondition.ConditionType.InspectInteractable` 的任务。经营投喂任务只允许读取 `MissionNode.FinishCondition.ConditionType.ServeInWork`、mission `reciever` 和 `RunTimeScheduler.ContainsSpecialNPCServeInWorkMission()`；不得调用 `TryTriggerServeMission()` 或其他会改变任务状态的方法。NPC 所在场景优先从 `RunTimeDayScene.trackedNPCs` 的 mapLabel 反查，并用 `DaySceneLanguage.GetMapLanguageData()` 显示地图中文名称；tracked 数据为空时，用 `DataBaseDay.RefNPC()` 的 `possibleDestinations[].spawnMarker` 经 `GetMapLabelFromSpawnMarker()` 解析为可能场景。NPC 显示名必须优先用 `DaySceneLanguage.RefDaySceneName()`，再回退到 `SchedulerNode.Character.GetLanguageData()`，不要直接显示英文 label 或 `NPC.ToString()`。不要再用 `HaveMissionStarted()` 过滤已追踪任务，因为它本身就是检查任务是否在 `trackingMissions` 中；但调度事件后置任务必须过滤已开始和已完成的 label，避免重复显示。读取失败必须显示分来源诊断信息，不得回退到静态全任务列表误导用户。
- 普客订单自动化必须建立在只读诊断可识别订单的前提下。常规普客快照优先使用 `NormalOrderRuntimeCapture`；只有捕获为空、捕获 Hook 尚未可用或需要诊断/启动校验时，才复用健壮的 IL2CPP 枚举与单例查找逻辑读取 `OrderController.GetShowInUIOrders()`、HUD `OrderingElement.ActiveOrder`、`GuestsManager` 的 Presented/Desk/DeskMap/Repellable/ManualDesk 控制器以及 Queue 控制器中的 `AllOrders`/`AllOrdersData`/`PeekOrders()`。当前普客执行入口按首次出现顺序并发处理未满足订单，并发上限来自 `CompanionPreferences.autoNormalConcurrency`；已经开始制作、等待 pending 直送或正在送达/评价的订单不得继续占用新的开锅调度名额，但“已开始制作”不能作为永久终态，长时间未直接送达时必须允许恢复调度。普客自动化需要独立于稀客节拍快速轮询，并在普客订单 key、料理、酒水或送达状态变化时绕过节流立即处理一轮。普客阶段包括送达酒水、自动开始料理、自动送达料理和自动完成订单；可见阶段必须由 `autoNormal*` 子开关独立控制，默认关闭。料理完成后必须直接送达目标订单，不再写入 `IzakayaConfigure.StoreFood()` 或扫描 `StoredFoods` 作为自动化中转。送达提交必须通过统一运行时送达 helper 完成 `Served*InAir`、顾客桌面显示和最终 `Serv*` 同步；`get_IsFullfilled()` 只表示料理和酒水都已送达、订单可以评价，不代表顾客流程已结束，实际完成状态必须以 `HasEvaluated` 或订单从运行时移除为准。只有 `get_IsFullfilled()` 为真时才能调用 `EvaluateOrder()`。同一订单已有 pending 料理时只能等待，不能因场景里有多个同类厨具而重复制作；pending 直送应优先用 `orderKey` 匹配，但普客运行时对象可能被游戏重建，严格 key 失效时必须要求桌号、料理和酒水仍一致后再重匹配当前订单。稀客和普客开锅前必须共用前端同一轮厨具预约表，按已摆放厨具快照计算同类厨具容量；容量不足时优先保留普客待处理订单，稀客只能等待料理开锅但仍可继续处理送酒或完成订单。
- 自动化部分送达后恢复顾客耐心时，调用 `GuestGroupController.AddPatient` 前必须读取同一控制器的 `CurrentPatient` 和 `MaxPatient`，并把恢复值限制到剩余耐心空间内；若已经超过上限，只能用 `SetPatient(MaxPatient)` 校正一次。不得直接传入固定恢复值，因为游戏原生 `AddPatient` 不裁剪上限，`GuestTableDisplayer.UpdatePatient` 会在裁剪显示比例前使用 progress 索引贴图数组。
- 普客自动化的可执行控制器绑定必须保留 `NormalOrderRuntimeCapture` 的 `GuestGroupController.PushToOrder` 捕获来源；HUD / `OrderController` 只作为启动校验、空捕获补位和诊断来源。优化掉帧时应限制刷新范围和轮询频率，不得删除该绑定链路。普客捕获版本变化后只强制刷新普客订单快照，不应重跑完整稀客经营扫描；捕获快照已有可解析订单时，不应继续做完整普客订单反射扫描。
- 特殊经营场景暂不接入标准推荐和自动化链路。不要在 `RecommendationState`、本地 API、前端排序或自动化阶段中重新加入特殊目标 Tag 分支，除非先基于运行时日志和反编译资料重新验证完整原生副作用。已分析内容记录在 `docs/special-business-scenes-notes.md`，只作为后续设计参考。
- 普客订单中的客人、料理和酒水名称只能使用本地数据仓库名称或明确可读文本。`GameData.CoreLanguage.LanguageBase`、`Il2Cpp*`、`GameData.*` 这类运行时类型名必须过滤掉；普客订单去重也不得依赖不稳定的客人文本。
- 自动化能力是实验性功能，必须由设置页总开关控制；总开关关闭时经营中页不显示自动化配置，也不执行任何自动化动作。稀客并发、普客并发、最大重试和最大回退都必须走 `CompanionPreferences`，默认分别为 `2`、`3`、`3`、`2`；稀客完成订单评价每轮最多执行 1 笔，避免多稀客订单同时修改运行时对象，普客按普客并发数处理。普客订单处理必须额外由经营中自动化面板的“启用普客处理”子开关启用，开启后不保留手动处理按钮，由伴随窗口轮询自动执行。稀客阶段配置使用 `autoPrep*`，普客阶段配置使用 `autoNormal*`；普客送酒、开锅、送料理、完成订单和出错暂停都要独立保存、独立传参。子选项默认关闭但记忆用户上次配置。
- 自动化总开关开启后，前端必须先获取自动化 lease 并持续续约；未取得 lease、连接失败或快照处于错误状态时，不得启动稀客或普客自动化轮询。关闭自动化时应释放 lease，异常退出或 Android 后台暂停时依赖后端 TTL 自动释放。
- 自动化遇到厨具占用、出锅料理暂未直接送达、运行时订单或厨具对象短暂不可读等临时状态时，应保持可重试；不得因为一次失败永久停止。`出错时暂停` 只用于非临时错误。稀客暂停状态和普客暂停状态必须隔离；普客的非临时错误只暂停对应订单 key，不得影响稀客自动化或其他普客订单。
- 自动化状态机只把实际动作视为进展，例如送酒、开锅、单项送达提交或触发评价；不要把“选择订单”“匹配订单”这类前置成功当作进展，否则会掩盖真实卡住。稀客和普客完成订单都必须允许料理和酒水按阶段分别送达，只要目标项已进入订单就应通过 `hasServedFood/hasServedBeverage` 校准前端状态；只有订单 `get_IsFullfilled()` 为真时才能触发 `EvaluateOrder()`，但本地状态不能把该字段当成已完成，应继续等待 `HasEvaluated` 或订单消失。C# pending 直送、开锅成功/失败和 pending 移除需要写入 `BepInEx/config/MystiaStewardCompanion/automation-jobs.log`，连续相同日志必须合并为 `repeat` 摘要，伴随窗口日志页需要能通过 `/logs/automation` 有上限读取并展示结构化作业记录，也需要能通过 `/logs/export-diagnostics` 导出包含 snapshot 和日志尾部的诊断 zip；诊断日志和诊断包都必须有大小上限并且不能影响游戏流程。
- 稀客订单进入自动化后必须锁定本订单的料理、加料和酒水；后续轮询即使库存或排序导致推荐列表变化，也不能改用新的第一推荐，除非用户重置该订单或订单自然结束。自动化锁定排序后的统一推荐候选；若锁定料理标注为 `偏好备选`，状态中也必须保留该标识。`只处理收藏料理` 只限制料理动作，`只处理收藏酒水` 只限制酒水动作；执行候选截断必须保留当前订单可用收藏项，避免收藏项不在首屏推荐时被误判为不可用。
- 稀客页下拉选项不再依赖本地 API 推荐状态里的稀客进度字段；后端也不再发布这类字段，避免热路径读取 `RunTimeAlbum` 和映射表。
- `任务` 页的稀客邀请必须走日间羁绊邀请链路，不再写 `Story.SpecialGuestControlled`。候选扫描支持 `current` 和 `all` 两种范围：`current` 优先通过 `DayScene.SceneManager.CurrentActiveMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`DaySceneMap.allCharacters` 和场景 `CharacterConditionComponent` 读取当前日间场景 NPC，并用 `DataBaseDay` 的 NPC 目的地补足，再用 `RunTimeDayScene.RefTrackedNPCAvailability()` 判断当前范围内的运行时可见性；`all` 从 `DataBaseDay.GetAllNPCKeys()`、`AllMappedNPCsMapping`、`AllNPCsMapping` 或 `allNPCs` 读取全部日间 NPC 并解析所在地图，同时合并当前场景已读取到的候选。`all` 的全部静态候选不得用当前时间可见性作为硬过滤，因为 `RefTrackedNPCAvailability()` 依赖 `TrackedNPC.ShouldShown(RemainActions)`，只代表当前时间/剩余行动下是否显示。候选经 `DataBaseCharacter.RefSGuest()` 映射到 `SpecialGuest`，再统一检查 `StatusTracker.HasNPCInvited()`、`RunTimeAlbum.GetOrGenerateSpecialNPCKizunaLevel()` 和当前等级成功邀请对话包；当前范围内确认不可见的候选也必须保留在列表中并返回原因，只禁用邀请按钮；已被 `StatusTracker.HasNPCInvited()` 标记的候选必须进入 `ExistingInvited`，供任务页 `当前已邀请` 区块展示。列表、单独邀请和全部邀请必须复用同一套候选扫描与条件判定；羁绊筛选和名称搜索只限制前端展示或批量邀请的前端等级参数，不得改变后端候选扫描。符合条件后调用 `StatusTracker.RecordInvitedGuest()` 写入今晚邀请名单。不要调用 `DaySceneChatSelectionPannel.InviteSpecGuest()`，该方法会触发随机成功率并记录今日已尝试，会把可邀请但随机失败的稀客错误跳过。不要把 `StatusTracker.HasTemptInvited()` 作为跳过条件，避免旧版本或手动失败尝试阻止邀请。该功能不得直接刷客、不得推进日间时间、不得修改受控稀客队列。
- 运行时稀客订单捕获只能长期显示仍匹配当前活跃稀客，或仍能从原运行时订单对象和控制器确认未完成的订单；无法确认仍在场的捕获项只允许短暂宽限，避免跨伴随窗口重启或跨天残留。离开夜间经营场景或清除运行时状态时必须清空捕获缓存。经营中稀客订单行的删除按钮调用 `/orders/rare/dismiss` 清理插件端缓存，不应只做前端隐藏。
- 稀客自动化诊断必须按订单 key 展示，不只依赖长文本日志。每笔当前候选订单需要显示步骤、已处理阶段、重试/回退次数和最近原因，并提供单笔重试和重置。重试只解除该订单暂停并保留已完成阶段；重置只清除该订单本地状态。两者都不得影响其他稀客订单、普客订单或自动化总开关。
- 经营中页需要显示自动化资源状态：厨具预约按当前已摆放厨具容量展示普客和稀客的本轮预计占用。该视图只用于诊断和用户判断，不应反向驱动自动化状态机。
- 稀客订单完成前必须同时校验料理和酒水送达状态，不要只返回或处理第一个缺失项。如果 C# 侧已有同一目标料理的 pending 直送任务，前端/后端都应等待该任务完成，不得因厨具冻结或 Debuff 重复开同一道料理。
- 自动开始料理固定尝试完成原生 QTE 奖励结算，不再提供跳过或完成 QTE 的配置开关。该功能不打开游戏音游面板，只尝试调用游戏 QTE 成功奖励入口；运行时失败时返回诊断信息，不应中断已开始的料理。
- 游戏内料理/酒水列表置顶是实验性功能，只允许重排已生成的 UI 列表，不得自动点击或绕过游戏自身筛选；本地 API 更新置顶目标失败时必须静默降级。
- 普客订单被动快照应保持约 1 秒级缓存；普客自动化动作后只强制刷新普客订单快照。pending 出锅直送无待办时不要在 `Update()` 热路径轮询。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，必须保留后端读取上限和前端显示上限，避免无限累积日志。
- BepInEx 控制台窗口默认由 Mod 写入 `BepInEx.cfg` 在下次启动关闭，并在 Windows 上隐藏已创建的控制台窗口；伴随窗口 `设置` 页可通过本地 API 临时开启/关闭原生日志窗口，修改时必须同时更新当前窗口可见性和下次启动配置。
- 面向普通用户的伴随窗口默认隐藏调试信息。新增扫描状态、运行时来源、性能耗时、内部订单来源、订单 key、诊断日志、BepInEx 控制台控制、任务 label/source 等偏排查内容时，必须受 `CompanionPreferences.showDebugDetails` 总开关控制；该开关默认关闭，并只在 `设置 -> 窗口 -> 显示调试信息` 中开启。
- 伴随窗口信息密度优先通过内部页签控制，不要把所有区块直接堆到同一页面。`概览` 固定使用 `状态 / 库存 / 操作` 分栏；`设置` 固定使用 `窗口 / 连接 / 推荐 / 自动化 / 更新` 分栏，调试开关开启后才显示 `调试` 分栏。连接配置必须集中在 `连接` 分栏，稀客专注模式默认精简放在 `推荐` 分栏。普客、稀客、经营中和稀客订单专注模式的推荐/订单列表应保留稳定内容区域和内部滚动，避免数据从空变有时造成大幅布局跳动。
- 游戏内不再保留 IMGUI 面板；游戏侧只负责后台读取、自动化执行、本地 API 和伴随窗口唤起，所有用户交互放在独立伴随窗口。

## 文档维护

- 用户安装和使用写入 `mods/bepinex/README.md`。
- 开发和构建写入 `mods/bepinex/README.dev.md`。
- 机制或运行时读取路径变化时，同步更新 `docs/` 和 `mods/bepinex/docs/`。
- 用户可见功能、快捷键、设置项、自动化行为、页面布局或本地 API 变化后，必须在提交前同步文档。
- 版本发布前如果文档落后，先补文档再提交版本号并合并 `main`。
