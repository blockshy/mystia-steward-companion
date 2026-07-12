# 开发约定与流程

更新日期：2026-07-10

## 代码边界

- 仓库维护 BepInEx Mod 与 Tauri 伴随窗口。
- 伴随窗口入口为 `apps/companion/src/companion/ModWorkbench.tsx`，顶层挂载在 `apps/companion/src/App.tsx`。
- 推荐算法核心集中在 `apps/companion/src/recommendation-engine/`；经营中订单推荐由 `apps/companion/src/companion/domain/service-recommendations.ts` 组装，并通过 `apps/companion/src/companion/workers/order-recommendations.worker.ts` 放到 worker 中计算。经营中普客订单的执行方案详情由 `apps/companion/src/companion/domain/normal-order-details.ts` 派生，也必须通过独立的经营中订单推荐 worker 请求按需计算，不得放回 React render 链路，也不得影响稀客订单推荐和自动化使用的 `pending/isCurrent` 门禁。完整经营中订单推荐 worker 不得因为自动化总开关或普客自动化单独开启就在所有页面后台运行；只有经营中页面、专注模式、游戏界面置顶、厨具高亮或稀客自动化实际需要推荐候选时才启用。主推荐请求和普客详情请求都必须用订单、库存、厨具、特殊经营上下文和相关推荐偏好的语义签名稳定输入；不要让 `/snapshot` 轮询产生的新对象引用反复触发 worker 投递、结构化克隆或整列表重绘。推荐 worker hook 必须使用 active + latest queued 请求模型，快照连续刷新时只投递最新排队请求，避免重计算响应被 750ms 轮询持续冲掉。订单推荐 worker 和页面推荐 worker 都要按 `RecommendationDataSet` 语义签名缓存完整运行时目录，只有签名变化或 worker 重新初始化时才随请求发送完整数据集，后续请求只传业务 payload 和数据签名，避免自动化轮询导致大对象反复结构化克隆。普客页和稀客页的页面推荐通过 `apps/companion/src/companion/workers/page-recommendations.worker.ts` 计算，页面组件只负责选择状态和结果渲染，不得把候选搜索、排序或自定义料理合并重新放回 React render 链路。顶层工作台的重型页面内容只允许当前 active tab 挂载，inactive 页面不得常驻完整运行时目录派生列表或页面 worker；页面推荐 hook 在 payload 为空时必须 terminate worker 并清空 worker 数据签名。
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
- `apps/companion/src-tauri/Cargo.toml` 的 mobile lib target 使用内部名 `mystia_steward_companion_mobile`，不要改回 `mystia_steward_companion`。Windows MSVC 会把桌面 bin target `mystia-steward-companion` 的 PDB 名规格化为 `mystia_steward_companion.pdb`；同名 lib target 会触发 Cargo `output filename collision` 警告。

## 运行时约束

- Mod 只读取当前游戏运行时数据，不读取 `.memory` 存档文件。
- 运行时固定数据读取成功后，C# 侧会把 `DataBaseCore` / `DataBaseCharacter` / `DataBaseLanguage` 结构化为 `RuntimeDataCatalog`，并切换 `DataRepository` 到运行时仓库；伴随窗口收到 `/snapshot` 中的 `runtimeDataComplete=true` 和新的 `runtimeDataSignature` 后，只在本地缓存为空或签名变化时读取 `/runtime-data`，普客/稀客推荐、经营中推荐、任务目标、库存修改页和自动化目标解析都必须使用这份缓存后的运行时数据集。
- 本地 API 快照需要避免在 Unity 主线程和 WebView IPC 热路径高频序列化大对象。完整 `RuntimeDataCatalog` 不得再随 `/snapshot` 周期性发布；`/snapshot` 只携带运行时目录完成状态、来源、状态文本和签名，完整目录由 `/runtime-data` 独立端点按签名懒加载。`/snapshot` 必须携带 `snapshotSignature`，该值必须是规范快照内容的固定长度加密摘要，不能直接使用会随订单和事件增长的规范原文；前端轮询时带上 `knownSignature`。内容未变化时后端返回 `{ unchanged: true, snapshotSignature }`，前端只更新连接存活状态，不得重新 `setSnapshot`、重新派生推荐或触发 worker。伴随窗口收到完整目录后应保存到独立缓存，不得放进主 `snapshot` state，避免大对象随轮询快照、Tauri IPC 字符串、props 链路和 React DevTools/闭包长期留存。快照内容签名未变化时后端应复用上一份缓存 JSON，不要为了 `CapturedAtUtc` 或性能数字重复序列化。前端仍必须按成功返回的快照更新连接状态，避免跳过运行时目录恢复。运行时固定数据读取完成后不得在经营快照热路径重复刷新，未完成时也要做重试间隔保护；经营诊断只能复用已缓存的运行时目录快照。新增重扫描或自动化轮询时要记录到 `performanceMs` 或复用现有耗时指标，便于概览页排查掉帧；性能快照只保留近期样本，避免旧耗时长期误导判断。经营扫描指标应尽量按来源拆分，例如 `business.rare.*`、`business.normal.*`、`runtime.cookerSnapshot` 和 `mission.serveTargets`；普客订单快照应优先复用短 TTL 缓存，不要在一次快照发布链路中重复枚举同一批运行时对象。
- 夜间经营订单优先使用 `SpecialOrderRuntimeCapture` 运行时捕获缓存；捕获缓存为空、需要初始化/回退校验，或捕获缓存有订单但本轮可接受订单少于缓存数量时，才把 OrderController、HUD、服务面板和桌位控制器反射结果用于业务缺失项补充。诊断开启时可以额外采样这些来源，但诊断样本只能进入日志快照，不能改变正式订单集合。控制器扫描仍要读取活动稀客和预算资金信息。
- 夜间经营稀客订单必须保留订单级 `IsFreeOrder`。免费订单不应用 `GuestGroupController.WillPayMoney=false` 的付款预算阻止；非免费订单继续按当前预算策略和剩余资金判断。
- 夜间经营订单必须按首次出现时间稳定显示；不得因桌号排序或推荐完整度排序让新订单插到旧订单前面。
- 经营中订单排序支持 `点单顺序` 和 `稀客分组`。默认必须保持点单顺序；稀客分组模式下，同一稀客订单放在一起，稀客组之间按该稀客最早订单出现时间排序，组内仍按点单先后排序。经营中列表、当前点单推荐、专注模式、游戏界面置顶目标和自动化第一单选择必须复用同一排序函数。普客特殊经营自动化目标选择是候选搜索级工作，必须通过经营中订单推荐 Worker 预计算并按订单 key 复用，自动化 tick 只能消费结果，不能在 UI 主线程逐单同步调用完整目标搜索。
- 稀客/经营中推荐使用统一料理/酒水列表：满足点单 Tag 的候选优先，不满足点单但命中稀客偏好的候选直接进入同一列表并标注 `偏好备选`。不得再维护满足点单与喜好备选两套结果数组。自定义推荐料理必须从 `custom-recipes.json` 转换为普通料理候选参与同一套硬过滤、预算、排序、自动化和 UI 展示，不得再通过 `favorites.source=manual` 或其他兼容路径混入收藏体系。`custom-recipes.enabled` 是唯一功能总门禁，关闭时不得改写单条 enabled、pinToTop 或 sortOrder；管理页的稀客/基础料理分组只能从同一列表派生，flags 批量更新必须由 Mod 在一个锁和一次保存内原子完成，排序只允许在同一稀客内移动。当前稀客已接取的经营投喂任务指定料理可通过 `任务料理置顶` 开关在硬过滤后置顶；自定义推荐料理的单条置顶、收藏料理和收藏酒水分别按独立规则置顶。置顶不得绕过解锁、库存、预算阻止、排除项和缺失厨具过滤。料理推荐优先 `foodScore >= 3`，但必须保留“满足点单且低于 3 分”的候选作为兜底。
- `v1.1.x` 的兼容支持只包含两项一次性迁移：旧 GUID 配置 `com.tyukki.mystia-steward.cfg` 在当前配置不存在时复制到新 GUID，以及 Local API 启动阶段将 `favorites.source=manual` 在自定义料理写入成功后迁出收藏文件。迁移不得延迟到 `GET /custom-recipes`，只读端点不能隐含文件写入。两项代码计划在 `v1.2.0` 删除；不得扩大为长期兼容层，也不得借迁移名义继续保留旧 API 路径、旧类型或旧业务逻辑。
- 稀客页场景候选必须优先使用运行时数据集；只按经营场景、可用中文名称和可用点单 Tag 过滤，不再按当前存档记录/解锁进度过滤。当前进度来源不稳定，容易误删可测试稀客；后续若要恢复，必须先基于游戏运行时明确的已解锁字段重新实现。
- 经营中读取到已摆放厨具快照时，`排除缺失厨具` 可过滤当前场景没有对应厨具的料理；读不到快照或无法映射厨具名时不得误删推荐。料理厨具类型以游戏 `CookSystemManager` 的 `AllAvailableCookerType` 为准，前端只消费本地 API 给出的中文厨具名。设置页的 `同基础料理显示` 控制同一基础料理在稀客页和经营中推荐中最多展示多少个加料变体；该限制只裁剪 UI 展示行。经营中展示行应从完整料理/酒水候选直接派生，自动化目标应从独立执行候选构造，不能只依赖裁剪后的 UI 行，也不能让执行候选上限提前裁掉用于补位的偏好候选。非可见经营中页的后台自动化推荐只能返回自动化需要的紧凑结果，并限制 worker 候选缓存和回包重复更新，避免自动化开启后在概览、设置等页面继续累积完整推荐对象。
- `游戏界面置顶推荐` 和 `目标厨具高亮` 是两个独立开关。料理、材料和酒水列表项的黄色脉冲属于 `enabled` 对应的置顶功能，不是第三个开关，也不得受 `highlightEnabled` 厨具开关控制。两者可以共享当前第一笔稀客订单的推荐目标，但本地 API 必须分别传递 `enabled` 与 `highlightEnabled`；`recipeId` 必须是游戏 `Recipe.Id`，不能用成品 `foodID`。伴随窗口只允许把与当前推荐输入签名一致的 Worker 结果更新为新目标：pending 期间保留上一有效目标，error 时发布空目标并锁存失败 revision，只有后续新的 current 成功 revision 才能解除，任一开关关闭都要立即下发而不能被 freshness 门禁阻止。`ok:false` 或网络失败不能记为成功，必须有上限退避重试；Tauri 原生请求不可取消，发布器必须保持单写者并合并为最新 desired 状态。发布去重边界必须包含 endpoint、token 和连接代际，同地址 Mod 重启或重新应用连接后也要重发。
- 游戏界面置顶必须复用游戏原生 pinned 排序，不得直接读取或改写 `m_RecipeInstances`、`m_Beverages` 或 `ClassifyIngredientByType` 的 IL2CPP 泛型列表。`WorkSceneCookingSelectionPannel.UpdateAllVisual` 与 `WorkSceneStoragePannel.UpdateBevField` 只用 prefix/finalizer 建立 ThreadStatic 同步作用域，并在最外层 prefix 固定一份目标快照；`RunTimePlayerData.CheckPinned` 的 bool prefix 只能在对应作用域内为精确目标设置 `__result=true` 并跳过原方法：料理使用 `1 + Recipe.Id`，材料使用 `0/4/5/6 + Ingredient.Id`，酒水使用 `2 + Sellable.Id`。非目标、作用域外和 cooker 类型 `3` 必须执行游戏原逻辑，不得压制玩家真实收藏；无实际 caller 的 `UpdateIngField` 不得恢复。
- 列表高亮只能使用三个精确元素绑定 Hook：`WorkSceneCookingSelectionPannel.OnRecipeElementEnabled/3`、`OnIngElementEnabled/3` 和 `WorkSceneStoragePannel.OnElementEnabled/3`；storage 行必须用 `Sellable.Type=Beverage` 排除同 ID 料理。后台目标更新只发布 immutable target/generation，Unity Image 读写必须留在元素回调、`LateUpdate` 和面板/场景清理主线程，并保留原 alpha；虚拟列表池化重绑、panel close/destroy、场景 suspend 和插件 Dispose 都必须恢复原色。不得通过全局场景扫描、猜测子节点名、修改 CanvasGroup alpha 或重新引入 UI 列表改写实现。
- 稀客料理/酒水展示排序由伴随窗口设置页的置顶开关、自定义推荐料理置顶和推荐权重共同控制。硬过滤后先应用任务料理、自定义推荐料理、收藏料理、收藏酒水置顶，再由 `RecommendationSortProfile` 的启用项、权重、方向和预设共同决定顺序；默认均衡预设综合稀客偏好、厌恶风险、加料数量、资源压力、成本、利润、酒水库存和当前厨具可做。新增排序或置顶规则时必须同时接入稀客页、经营中页、专注模式、缓存签名和自动化当前第一单选择，不要让不同入口出现不同排序。
- 推荐 tag 解析统一维护在 `apps/companion/src/recommendation-engine/tag-resolution.ts`，动态料理 tag 维护在 `dynamic-food-tags.ts`。运行时导出的 `tagPriorityRules` 优先级最高；运行时缺失时只允许使用 `PROJECT_VERIFIED_TAG_PRIORITY_RULES` 这组项目验证规则，不得在其他模块重新硬编码互斥/压制关系。新增或调整 tag 规则必须来自游戏运行时行为、反编译资料或可复现实测，并同步更新该集中模块和相关文档。
- 运行时料理配方的基础食材必须作为数量敏感序列处理，重复项表示同一材料需要多份；`RuntimeDataCatalog.Recipes[].Ingredients` 和前端 `RecipeCatalogItem.ingredients` 不得去重。只有 tag、场景、ID 列表等集合语义字段可以在解析和归一化时去重。推荐加料槽位、大份动态 tag、基础成本和自动化下单保护都必须基于保留重复后的真实基础食材数量。
- 已捕获且仍能匹配当前稀客，或仍能从原运行时订单对象和控制器确认未完成的订单，不得使用短时间缓存过期清理；只应在明确移除、确认上菜完成、稀客离场或长时间硬上限后消失。
- 本地 API 必须始终保留 `127.0.0.1` 回环监听，避免 LAN 配置错误导致用户无法从 A 设备本机恢复；`LocalApi.AllowLanConnections=true` 只会额外启用 LAN listener。LAN listener 必须限制私网来源，连接配置和 Token 重置端点只能由回环客户端调用；每个 listener 必须拥有独立停止状态和线程所有权，动态重配要串行、幂等并等待旧 worker 有界退出，accept 终止错误不得无延迟重试或逐条刷日志。Tauri 代理只接受 loopback/private/link-local IPv4 endpoint，除 `/health` 外所有接口都必须通过伴随窗口传入的 token 访问。
- 本地 API 路由只接受规范路径，不提供根路径 `/` 到快照或健康检查的映射，也不接受 `/api/*` 前缀别名。只读查询使用 `GET`；文件写入、运行时修改、配置变更、控制权变更、诊断导出/打开目录和更新操作统一使用 `POST`，即使参数当前仍放在 query 中也不得退回副作用 `GET`。`/updates/status` 会归并 updater 结果并可能写入或删除状态文件，必须使用 `POST`。新增客户端、mock 和文档必须同步方法与精确路径；不存在的路径/方法组合返回 404，GET、POST、OPTIONS 以外的方法返回 405。在正式定义结构化 body schema 前不得自行编造 JSON request body 契约。
- 正式 Tauri runtime 的 Rust TCP 代理必须把连接超时和响应读取超时分开处理：连接超时最长 5 秒，读取超时可按前端命令要求放宽到最多 60 秒。长耗时请求不能被连接上限提前截断；浏览器开发模式的 fetch 超时仍由前端调用方控制。
- 每个伴随窗口必须生成稳定客户端 ID，并通过本地 API header 传给 Mod。订单自动化端点必须由 Mod 本地 API 的自动化 lease 仲裁，同一时间只允许一个客户端执行自动化；其他客户端可以读取快照和配置普通偏好，但不得绕过 lease 直接调用订单自动化动作。连接断开或快照错误时，前端可以保留最近一次只读展示，但不得继续用旧快照驱动自动化、游戏界面置顶目标或其他写入游戏运行时的后台动作。断线重连只允许一条退避调度路径重试带 Token 的 `/snapshot`；`/health` 只能用于人工可达性诊断，不能清除快照错误或恢复已连接状态。相同 endpoint/token 的 Tauri 启动连接通知必须幂等；自动化 lease 的连接身份由 endpoint、token 和 Mod `automationSessionId` 确定，短暂网络错误不能伪造新服务端会话。桌面和 Android 等所有正式 Tauri runtime 必须统一通过在线程池执行阻塞 TCP 请求的 Rust command 访问本地 API，只有浏览器开发模式可以直接 `fetch` mock API，不得保留移动端 WebView 网络 fallback。
- 伴随窗口单实例控制监听 `127.0.0.1:32146`；热键逻辑必须先发送 `show`/`toggle`/`exit` 控制消息，控制端口不可达时才启动伴随进程，避免手柄快捷键重复创建窗口。
- `F8` 和 `RS Click` 默认用于在游戏和伴随窗口之间切换焦点；伴随窗口聚焦时由 Tauri 前端处理热键并调用后端按设置切回游戏窗口。焦点行为不能写死为隐藏窗口，必须支持保持伴随窗口悬浮只切焦点。手柄切换必须做释放锁存和可配置后端防抖，避免一次长按在两侧窗口间反复触发。伴随窗口内手柄焦点必须优先遵守 `data-gamepad-scope` 区域，不要让顶部页签横向移动跳入页面内容。Tabs、SegmentedControl、横向按钮组和 slider 这类复合控件必须使用组件级语义处理方向键；通用空间导航必须按交叉轴对齐优先选择候选，不允许依赖只看中心点距离的全局几何搜索碰运气。
- 伴随窗口透明度通过 Tauri transparent window 和前端 CSS 变量实现；背景透明度只影响窗口背景、面板、弹层和滚动条轨道，文字透明度只影响普通文字、图标和辅助徽章内容，主操作按钮必须保持可读。不要用 Windows `SetLayeredWindowAttributes(..., LWA_ALPHA)` 或其他整窗 alpha 实现背景透明度，因为它会让文字和图标一起变淡。桌面主窗口在 Tauri 配置中保持初始隐藏，Rust setup 先恢复窗口大小和位置，再 `show()` 并聚焦，避免启动时先出现在默认位置再跳到保存位置。全平台图标的唯一母版是 `apps/companion/src-tauri/icon-source.svg`；母版必须使用 LF、正方形 viewBox、自包含纯矢量，并在根元素分别声明通用 `data-icon-background="#RRGGBB"` 和 Android adaptive `data-android-background="#RRGGBB"`，同时保留唯一的 `icon-background` 和 `icon-foreground` 分层。修改后运行 `pnpm icons:generate`，提交桌面 PNG、Windows tile、ICO、tray、ICNS、iOS、`icons/android`、APK 实际使用的 `gen/android/app/src/main/res` 图标和 favicon，再用 `pnpm icons:check` 做全量幂等、尺寸、alpha、安全区和结构校验。Windows ICO 必须保留 16/20/24/32/40/48/64/128/256 九层且将 256px 置于第一层；ICNS chunk 固定排序；iOS 由同背景色的不透明全幅派生图生成；Android legacy 使用规范 padding 和 48/72/96/144/192 尺寸，adaptive foreground 只包含缩入官方安全区并保留额外桌面视觉留白的 `icon-foreground`，不得退回整张预圆角图或保留旧移动端资源。
- 鼠标穿透锁定必须通过 Tauri 原生窗口 `set_ignore_cursor_events` 实现，不能只使用 CSS `pointer-events`。`F10` 负责切换鼠标穿透；`F8`、`RS Click`、托盘显示/重连和单实例 `show` 控制消息必须自动关闭穿透，避免窗口被唤回后仍无法点击。
- 伴随窗口根滚动区域必须预留稳定纵向滚动条槽位，避免页面内容因滚动条出现或消失产生横向跳动。
- 伴随窗口滚动条样式必须跟随主题和背景透明度；不要使用全局 `*::-webkit-scrollbar` 覆盖会刻意隐藏滚动条的导航栏。
- 伴随窗口内手柄导航应优先处理同一行内的左右移动；`TabsList` 左右键只在当前页签组内移动，下键进入当前 active panel，panel 顶部上键回到控制该 panel 的 trigger；`SegmentedControl` 使用可见 label 作为手柄焦点目标，左右键只在当前选项组内切换；横向按钮组和工具条统一使用 `data-gamepad-axis="x"` 约束左右移动。通用 content 空间导航左右移动必须优先垂直对齐候选，上下移动必须优先水平对齐候选，避免焦点从上方表单跳到下方列表按钮。range 和 Mantine `[role="slider"]` 获得焦点时，左右方向应调节数值而不是跳到其他按钮。Select/MultiSelect 下拉框获得焦点时不得因方向键自动展开，必须由确认键展开，展开后方向键才用于移动选项。收藏按钮等点击后会改变 UI 状态的控件，应使用稳定 `data-gamepad-focus-key` 恢复焦点。Gamepad API 轮询必须使用集中输入状态机，按键读取同时检查 `pressed` 和模拟量 `value`，并在窗口 blur、页面隐藏、手柄断开、无手柄或禁用导航时清理 latch；确认/收藏动作在 active element 丢失时应优先使用上一手柄高亮元素继续执行，不把本次按键只消耗在重新找焦点上。
- 经营中、专注模式和日志等实时页面的动态内容区应保留稳定容器和紧凑空状态；不要因为暂无订单、暂无预约或暂无日志就直接卸载整块区域，避免数据刷新时页面大幅跳动。
- 帮助页内容必须保存在 `apps/companion/src/data/help-content.json`，前端只负责搜索、目录树和详情渲染。新增用户可见功能或排查流程时，同步更新帮助 JSON，避免只改 README。
- Unity 场景切换后不要再用固定秒数等待来规避加载问题。日间任务列表、日间地图和稀客邀请必须通过运行态数据入口判断可读性：排除主菜单、夜间经营和经营准备后，优先读取 `DayScene.SceneManager.CurrentActiveMapLabel` / `TargetMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`RunTimeDayScene.RefTrackedNPCAvailability()` 和 `RunTimeScheduler` 数据；不能把 `DaySceneSustainedPannel` 是否激活作为日间数据总门禁，否则常规日间场景会被误判为 UI 初始化中。夜间经营准备读取仍以 `PrepNightScene.UI.IzakayaConfigPannel.OnPanelOpen` / `GoToSpecific` 为 ready 信号，并用 `Cleanup_Generated` / `GotoWork` 清理；进入夜间经营准备时，只能用 `WorkPrepScenePannelRoot` 下活跃的 `IzakayaConfigPannelNew` 和 ready 信号阻断日间读取，不能用泛化的同名面板或残留对象判断。读取代码必须避开不稳定的 IL2CPP 托管枚举路径，尤其不要直接依赖 `IEnumerator.Current`；优先使用 Count/indexer、字段、静态快照或可空单例。读取失败应降级为状态提示并等待下一轮刷新。
- 运行时静态目录（料理、材料、酒水、普客、稀客、场景）和玩家存档状态（库存、已解锁、流行 Tag、已摆放厨具）必须分层读取。静态目录可用后应立即发布给伴随窗口，让任务、邀请、普客和稀客基础选项可用；玩家存档状态读取失败时只影响库存和推荐可用性，不应阻塞任务和邀请。基础推荐状态的库存、酒水和已解锁料理必须以 `RunTimeStorage.GenerateSaveData()` 生成的单份运行时存储快照为权威来源，`recipes` 为空时等待下一轮读取而不是发布空可用料理集合；玩家等级、流行喜好/厌恶 Tag 和明星店开关使用 `RunTimePlayerData` / `RunTimeDayScene` 轻量 getter 读取。夜间经营准备阶段在准备面板 ready 后允许读取这些基础玩家运行态，用于修改页、普客页和稀客页；但仍不得读取 DayScene 快照、任务列表或稀客邀请。
- 主菜单 `Main Scene` / `MainMenuPannel` 必须在代码中显式视为非游戏场景，不要只依赖 `NonGameplaySceneKeywords` 默认配置，因为用户已有配置文件不会自动补充新关键词。非游戏场景下不得读取运行时静态目录或玩家存档状态，避免 DataBase/Language 初始化不完整时触发 Unity 空引用。
- 日间任务、邀请和当前地图读取必须依赖 `DayScene.SceneManager`、`RunTimeDayScene`、`DataBaseDay` 和 `RunTimeScheduler` 的运行态对象。若这些入口缺失或当前地图 label 为空，读取服务应返回分来源诊断；不要在外层统一返回“日间 UI 初始化中”。
- 运行时库存修改必须排队到 Unity 主线程执行，避免本地 API 网络线程直接写游戏对象。
- 运行时库存修改页的材料和酒水列表支持按名称或库存排序；操作只保留单项 `-10`、`+10`、`99`，以及当前存档可编辑材料/酒水批量设为 `99`；不要恢复自定义数量输入、`+1` 或单独“应用”按钮，除非用户明确要求。
- `任务` 页读取当前进度可接取、进行中或可完成的交互任务。任务状态优先对 `RunTimeScheduler.trackingMissions` 中每条任务调用只读 `RunTimeScheduler.ParseActiveMissionData()`，映射为 `available`、`tracking`、`fulfilled`；同时必须主动刷新 `TrackedMissionData.UpdateFinishStates()`，并用 `HasFulfilled/get_HasFulfilled` 与 `conditionFinishStates` 全部完成作为 `fulfilled` 强兜底，避免 IL2CPP tuple 或枚举读取失败时把可完成任务显示为进行中；已完成任务不在筛选分类中展示。NPC 交谈任务优先使用 `RunTimeScheduler.GetAvailableInteractMissionForCharacter()`，但该接口表示“当前可交互推进”，不等同于未接取；若返回的 label 已在 `trackingMissions` 中，应按 `fulfilled` 展示。真正未接取、由 NPC 对话事件触发的任务，需要只读扫描 `RunTimeScheduler.scheduledEvents` 的当前修正日和 `-1` 桶，通过 `DataBaseScheduler.RefEvent()` 读取 `EventNode.postMissions` / `postMissionsAfterPerformance`，并只接受 `OnTalkWithCharacter` 或通过 `CheckCharacterInteractEvent()` 门控的 `KizunaCheckPoint` 触发事件；这些任务状态为 `available`。候选角色来源包括 `DataBaseDay.GetAllNPCKeys()`、`DataBaseDay.AllMappedNPCsMapping` / `AllNPCsMapping` / `allNPCs`、`RunTimeDayScene.trackedNPCs`、`DaySceneMap.allCharacters` 和当前场景 `CharacterConditionComponent`。场景调查任务读取 `RunTimeDayScene.trackedInteradctables` 与 `MissionInteractConditionComponent`，再只读匹配 `RunTimeScheduler.trackingMissions` 中 `MissionNode.FinishCondition.ConditionType.InspectInteractable` 的任务。经营投喂任务只允许读取 `MissionNode.FinishCondition.ConditionType.ServeInWork`、mission `reciever` 和 `RunTimeScheduler.ContainsSpecialNPCServeInWorkMission()`；不得调用 `TryTriggerServeMission()` 或其他会改变任务状态的方法。NPC 所在场景优先从 `RunTimeDayScene.trackedNPCs` 的 mapLabel 反查，并用 `DaySceneLanguage.GetMapLanguageData()` 显示地图中文名称；tracked 数据为空时，用 `DataBaseDay.RefNPC()` 的 `possibleDestinations[].spawnMarker` 经 `GetMapLabelFromSpawnMarker()` 解析为可能场景。NPC 显示名必须优先用 `DaySceneLanguage.RefDaySceneName()`，再回退到 `SchedulerNode.Character.GetLanguageData()`，不要直接显示英文 label 或 `NPC.ToString()`。不要再用 `HaveMissionStarted()` 过滤已追踪任务，因为它本身就是检查任务是否在 `trackingMissions` 中；但调度事件后置任务必须过滤已开始和已完成的 label，避免重复显示。读取失败必须显示分来源诊断信息，不得回退到静态全任务列表误导用户。
- 普客订单自动化必须建立在只读诊断可识别订单且能绑定可执行控制器的前提下。常规快照先用 `OrderController.GetShowInUIOrders()` 和 HUD `OrderingElement.ActiveOrder` 判断 live 可见订单，再合并仍匹配 live 订单 key 或在 key 缺失时匹配桌位/料理/酒水槽位的 `NormalOrderRuntimeCapture` 来补充 `GuestGroupController`；请求已经携带 `orderKey` 时不得删除 key 后回退同桌新订单。捕获为空或不可用时才扫描 `GuestsManager` 与 `NightSceneDirector.controlledGuest`。普客按首次出现顺序和 `autoNormalConcurrency` 并发调度，订单变化用短防抖触发复查。稀客 `autoPrep*` 与普客 `autoNormal*` 的送酒、开锅、送料理、评价和出错暂停必须独立保存、独立传参、独立推进。所有自动开锅都必须登记 generation job 作为防重复回执；只开启“开始料理”时 job 进入手动交接模式，绝不送达、入箱或复位厨具。稀客与普客开锅仍共用同一轮厨具预约表，容量不足只进入结构化等待，不计作失败。
- 跨帧料理所有权统一由 `AutomationCookingJob` 表达，不再使用前端布尔或创建时间猜测。`RuntimeCookingGenerationTracker` 必须精确 Hook `CookController.SetCook(Sellable, Recipe, bool)`，每次调用产生新的 generation；job 只拥有登记时的 `CookController + SetCook generation`，同 generation 内游戏原生完成流程替换 `Result` 仍属于本锅，generation 变化则表示玩家或其他逻辑已立即复用同一厨具，旧 job 不得读取、送达、存储或复位新锅。Mod 不得主动调用或重试非幂等 `FinishCooking`，只能观察原生 `Phase == Finished`。原 Result 消失并经过短观测宽限时按自动送达模式进入可恢复中断，手动交接模式则保留防重复开锅回执。
- job 的停滞和送达超时必须使用有效运行时间时钟，只累计游戏处于可推进夜间经营且前后观测都 eligible 的区间；暂停、场景不可读、控制器暂不可达和断线期间不消耗预算。烹饪阶段只在 phase 或 progress 真正前进时重置停滞钟，普通 waiting、等待原生完成和 API 轮询不能伪装成进展；停滞会保留旧锅，必须 blocked 并要求人工确认，不能自动重开。料理完成后默认直接送达目标订单；非目标成品、目标签名变化或目标连续不可达时才允许进入 `StoreFood` 恢复事务。订单最终字段 setter 和 `StoreFood` 都是先写数据、后执行回调的非幂等提交点；正常返回或异常后读到同一 Sellable 对象都代表 committed。`StoreFood` 一旦实际进入方法，异常后对象不存在也不能证明无前置副作用，必须 uncertain 且绝不能再次调用。commit 后只允许对同 generation 厨具有界 cleanup，并且必须精确确认 `Phase == Idle`、`Result == null`、`ChosenRecipe == null`；任一字段不可读都不是成功。
- 自动化部分送达后恢复顾客耐心时，调用 `GuestGroupController.AddPatient` 前必须读取同一控制器的 `CurrentPatient` 和 `MaxPatient`，并把恢复值限制到剩余耐心空间内；若已经超过上限，只能用 `SetPatient(MaxPatient)` 校正一次。不得直接传入固定恢复值，因为游戏原生 `AddPatient` 不裁剪上限，`GuestTableDisplayer.UpdatePatient` 会在裁剪显示比例前使用 progress 索引贴图数组。
- 普客自动化的可执行控制器绑定必须保留 `NormalOrderRuntimeCapture` 的 `GuestGroupController.PushToOrder` 和 `GuestsManager.SetManualControllerOrderInternal` 捕获来源；HUD / `OrderController` 是订单是否可见的事实来源，但只有合并到捕获控制器或启动扫描控制器后才能执行送达、耐心恢复或评价。优化掉帧时应限制刷新范围和轮询频率，不得删除该绑定链路。普客捕获版本变化后只强制刷新普客订单快照，不应重跑完整稀客经营扫描；捕获快照已有可解析订单时，也必须用 live 可见订单做存在性校验。普客特殊经营执行目标选择必须走 registry 统一入口并使用有界语义缓存；前端自动化热路径还必须通过 Worker 预计算该结果，厨具预约、资源视图和实际自动化执行只复用同一轮结果，不得各自重复跑一遍完整目标搜索。
- 本地 API 快照生产必须走统一 dirty-domain 发布器。场景、运行时、稀客经营、普客订单、特殊经营、自动化和任务变化只标记脏域，由控制器统一节流、刷新、签名去重和发布；不要在新增路径中直接散落调用快照发布。普客快照的读取门禁是夜间经营场景可读，不是稀客经营上下文已稳定；`OrderController.GetShowInUIOrders()` / HUD `OrderingElement.ActiveOrder` 已能提供 live 普客订单时，应允许 `NormalBusinessContext` 进入快照，避免必须通过切换页签或手动刷新才能驱动经营中页面和自动化。
- 特殊经营场景通过 `RuntimeSpecialBusinessContextService` 发布 `specialBusiness` 上下文，用于展示 `ChallengeMode`、已确认 HUD 目标、阶段进度和符卡计数。C# 场景规则、订单分类和上下文说明集中放在 `mods/bepinex/src/Save/SpecialBusiness/`，前端经营推荐/自动化规则集中放在 `apps/companion/src/companion/domain/special-business/` 并通过 registry 引入；新增场景必须新增或扩展对应模块，不要在页面、推荐调用方或自动化调用方直接硬编码 challenge type。不要把特殊目标 Tag 写回 `RecommendationState`；前端只能在经营中订单推荐中应用已确认会影响评价的特殊经营规则：怪诞料理大赛第一阶段按高评价/目标 Tag 选择执行料理，第二阶段和三阶段分身必须把“满足原订单料理/酒水、最高评价、当前目标 Tag”作为硬条件，三阶段小石本体必须使用场上揭示的正面料理 Tag、厌恶料理 Tag、酒水 Tag；破防期仍要先满足原订单料理/酒水，再按剩余目标分和当前预算规划投食总分，避免高分高价组合提前耗尽预算；幽幽子第二阶段必须避开实际厌恶 Tag 并优先选择安全评价组合，三阶段必须避开实际厌恶 Tag，剧情版和重修版 P3 boss 普客都保持原订单料理/酒水匹配，优先选择按等级合计可触发橙评/粉评的 `progress` 目标；若原订单组合只能达到绿评，则可生成 `refresh` 清理目标，用于调用原生评价清理订单但不承诺推进进度；低于绿评、含厌恶 Tag、目标不一致或回调链不完整时会停止自动送达和评价，喜好命中只作为同评价内排序参考；纯目标营业额/试炼目标金额只展示，不改变推荐排序。挑战/BOSS 订单可能以 `OrderBase/Normal` 或 `SpecialOrder` 形态出现，必须通过统一特殊经营订单分类器标记归属，并按场景明确是否允许自动化：幽幽子订单需要区分剧情版/重修版，若游戏运行时把重修挑战仍上报为 `Story_Yuyuko`，必须结合只读 `DifficultyMode` 和 `_50` / `_70` 评价回调证据发布有效挑战类型；送达阶段可使用当前有效 captured 原生订单对象，三阶段评价前必须重新取得当前 live controller。剧情版 `progress` 必须复用 `SetManualControllerOrderInternal` 捕获的 `onEvaluate` 调用 `EvaulateManualOrder`，并确认已送达目标、等级合计和剧情三阶段评分回调；重修版 `progress` 必须确认已送达目标、等级合计和 `_50` / `_70` 原生进度回调后调用游戏 `EvaluateOrder()`，不得走剧情手动评价路径；`refresh` 必须确认已送达成品精确等于请求目标与对应版本评价回调链，但不再因等级合计低于 Good 阻断；live `OrderBase` 读取不到客人字段时不得仅因 `guestId` 不可读拒绝，但 controller、当前版本所需评价 callback、送达目标、普客原订单不变量或清理/推进阈值不满足时必须暂停自动送达或自动评价，并记录请求、运行时匹配、执行模式、送达料理/酒水等级、原始/有效挑战类型、难度、callback 详情和阻断原因；怪诞料理大赛三阶段不能只凭 `guestId=2006` 判定小石本体，分身优先读取 `GuestControllerSpawnType=GhostInChallenge`，同一订单多来源合并时特殊经营角色应优先取可执行 controller 来源；怪诞料理大赛分身允许自动化但必须保持原订单料理/酒水，只在原料理上选择安全加料，并执行开锅前目标 Tag 校验和出锅后成品 Tag 校验，失败组合需要被前端即时屏蔽并由 Mod 侧最近失败缓存阻止重复开锅；小石本体破防前允许按揭示规则选择执行目标，并可使用仍通过原订单、桌号和控制器校验的 `RuntimeCapture:ManualOrderSet`，料理和酒水送齐后仍调用通用 `EvaluateOrder()` 触发线索阶段评价；破防后必须满足原订单料理/酒水并按剩余目标分和当前预算选择执行方案，且自动化匹配只接受带 Boss 原生评价回调的 live controller，订单生成回调只作为诊断信息，不绑定当前怪诞目标 Tag，料理和酒水送齐后仍必须通过该 live controller 调用游戏原生 `EvaluateOrder()` 进入 Boss `OverrideEvaluationCallback` 评分链路；自动化请求和自动料理 job 出锅目标必须保留 `specialBusinessRole=wacky-koishi-boss`，不得在空目标 Tag 时回退当前怪诞轮换 Tag；饕餮尤魔订单仍阻止标准普客/稀客自动化接管。任何跨帧待办、自动料理 job或运行时事件都必须持久保存目标信息，稀客自动料理 job 目标应保留订单 trace 与料理/酒水 Tag，避免旧订单待办串到下一单。自动化步骤和总日志需要记录规则原因以及 match/execution 对照。已分析内容记录在 `docs/special-business-scenes-notes.md`。
- 特殊经营模块应拥有自己的规则构造、普客执行目标选择、订单角色识别和失败组合 key 生成；`special-business/registry.ts` 只负责按 `challengeType` 分发，不应重新承载场景分支。前端规则构造放在 `special-business/rules/`，普客执行目标放在 `special-business/normal-targets/<scene>.ts`，`rules.ts` 和 `normal-targets.ts` 只作为稳定导出入口。C# 侧订单分类、运行时匹配策略、Boss 评价入口、`AutomationCookingJob` 出锅校验和诊断 helper 应放在 `mods/bepinex/src/Save/SpecialBusiness/` 下的场景策略文件中；`RuntimeOrderPreparationService*.cs` 只保留通用自动化编排和跨场景共享动作，怪诞料理大赛运行时判定集中在 `WackyCookingCompetitionRuntimePolicy`。
- 怪诞料理大赛小石本体破防期的投食方案必须共享同一套评分入口；自动化执行目标、稀客/经营推荐 execution plan、推荐料理和推荐酒水首项都要从同一投食分、预算、剩余目标分和剩余提交次数规划结果派生。不得在自动化和可视推荐中各写一套排序公式，否则关闭自动化后手动照推荐制作会偏离实机策略。
- 普客订单中的客人、料理和酒水名称只能使用本地数据仓库名称或明确可读文本。`GameData.CoreLanguage.LanguageBase`、`Il2Cpp*`、`GameData.*` 这类运行时类型名必须过滤掉；普客订单去重也不得依赖不稳定的客人文本。
- 自动化能力是实验性功能，必须由设置页总开关控制；总开关关闭时经营中页不显示自动化配置，也不执行任何自动化动作。稀客并发、普客并发、最大重试和最大回退都必须走 `CompanionPreferences`，默认分别为 `2`、`3`、`3`、`2`；稀客和普客都按订单独立判断是否可完成，已满足料理和酒水的订单应优先触发评价，但本地 API 调用仍必须串行 await，避免多订单同时修改 Unity 运行时对象。普客订单处理必须额外由经营中自动化面板的“启用普客处理”子开关启用，开启后不保留手动处理按钮，由伴随窗口轮询自动执行。稀客阶段配置使用 `autoPrep*`，普客阶段配置使用 `autoNormal*`；普客送酒、开锅、送料理、完成订单和出错暂停都要独立保存、独立传参。子选项默认关闭但记忆用户上次配置。
- 自动化总开关开启后，前端必须先获取 lease 并持续续约；未取得 lease、连接失败或快照错误时不得发出新动作。lease acquire 必须单飞，显式取消要先等待在途 acquire，避免取消完成后旧续约重新占有 lease。明确关闭自动化必须调用 `POST /automation/jobs/cancel`：Local API 在 lease 锁内递增 command epoch，命令 epoch fence 等待当前已开始命令完成，再作废其余旧命令并原子清理全部 `AutomationCookingJob`，确认取消屏障后才释放 lease。前端必须持久记住待确认的 endpoint 并重试，不能只清空本地状态。临时断线或客户端退出不主动清 job，lease TTL 到期后新控制者取得更高 epoch，并从快照中的活动 job 与递增事件序列接管；旧 epoch 请求和迟到响应不得改变新状态。
- 自动化端点和运行时事件统一返回 `waiting`、`progressed`、`completed`、`interrupted`、`retryable-failure`、`blocked`、`fatal`、`cancelled` outcome，并携带 `stage`、`reasonCode`、`jobId`、`retryAfterMs`。C# 真实 `beverage/cooking-start/cooking-delivery/order` 阶段优先于前端请求前推测。前端失败状态必须保存 `retryStage`；阶段改变或对应阶段开关关闭时只能清除普通失败的计数/退避，不能让送酒失败污染开锅，也不能清除副作用不确定栅栏。只有 `progressed/completed` 能清零当前阶段重试并刷新真实进展时间；`waiting/interrupted` 保留重试计数，`retryable-failure` 有界累加。blocked/fatal 中凡涉及开锅、送达提交、入箱、成品读取或厨具 cleanup 不确定的状态，都必须设置 `manualResolutionRequired`、保留 `prepared` 并禁用普通重试；只有玩家处理现场后点击 `确认已处理` 才能解除。前端用 request epoch 和事件 sequence 拒绝取消前或终态事件前发出的迟到响应，禁止再解析中文消息或使用前端经过时长猜测恢复。
- 厨具出锅结果必须读取 `CookController.Result` 或其精确 backing field，并确认对象是料理 `Sellable` 后才能送达或进入 `StoreFood` 恢复事务。不得通过大小写退化读取 `CookController.result` / `resultVisual`，这些字段是视觉 `SpriteRenderer`。非 `Sellable`、连续不可读、进度回退和所有权不可确认都必须形成有界的结构化 blocked/interrupted 终态；不得无限重试，也不得碰触不属于本 generation 的结果。
- 自动化状态机只把实际送酒、开锅、单项送达提交、料理进度前进或触发评价视为进展；“选择订单”“匹配订单”、普通轮询和等待游戏原生完成都不是进展。稀客和普客必须通过 `hasServedFood/hasServedBeverage` 与订单事实校准阶段；只有 `get_IsFullfilled()` 为真、严格读到 `HasEvaluated=false` 时才能调用一次评价，调用异常后必须严格回读，无法确认即进入人工栅栏，不能盲重试。job 快照和运行时事件必须携带 `jobId`、trace/order key、controller/result 指针、generation、phase/progress、outcome/reason、失败与清理计数，并写入 `aggregate-mod.log`；连续相同日志仍合并为 `repeat`。经营中订单行、快照和总日志必须显示同一 `trace=R-*` / `trace=N-*`，便于逐单关联。
- 稀客订单进入自动化后必须锁定本订单的料理、加料和酒水；后续轮询即使库存或排序导致推荐列表变化，也不能改用新的第一推荐，除非用户重置该订单或订单自然结束。自动化锁定排序后的统一推荐候选；若锁定料理标注为 `偏好备选`，状态中也必须保留该标识。`只处理收藏料理` 只限制料理动作，`只处理收藏酒水` 只限制酒水动作；执行候选截断必须保留当前订单可用收藏项，避免收藏项不在首屏推荐时被误判为不可用。
- 稀客页下拉选项不再依赖本地 API 推荐状态里的稀客进度字段；后端也不再发布这类字段，避免热路径读取 `RunTimeAlbum` 和映射表。
- `任务` 页的稀客邀请必须走日间羁绊邀请链路，不再写 `Story.SpecialGuestControlled`。候选扫描支持 `current` 和 `all` 两种范围：`current` 优先通过 `DayScene.SceneManager.CurrentActiveMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`DaySceneMap.allCharacters` 和场景 `CharacterConditionComponent` 读取当前日间场景 NPC，并用 `DataBaseDay` 的 NPC 目的地补足，再用 `RunTimeDayScene.RefTrackedNPCAvailability()` 判断当前范围内的运行时可见性；`all` 从 `DataBaseDay.GetAllNPCKeys()`、`AllMappedNPCsMapping`、`AllNPCsMapping` 或 `allNPCs` 读取全部日间 NPC 并解析所在地图，同时合并当前场景已读取到的候选。`all` 的全部静态候选不得用当前时间可见性作为硬过滤，因为 `RefTrackedNPCAvailability()` 依赖 `TrackedNPC.ShouldShown(RemainActions)`，只代表当前时间/剩余行动下是否显示。候选经 `DataBaseCharacter.RefSGuest()` 映射到 `SpecialGuest`，再统一检查 `StatusTracker.HasNPCInvited()`、`RunTimeAlbum.GetOrGenerateSpecialNPCKizunaLevel()` 和当前等级成功邀请对话包；当前范围内确认不可见的候选也必须保留在列表中并返回原因，只禁用邀请按钮；已被 `StatusTracker.HasNPCInvited()` 标记的候选必须进入 `ExistingInvited`，供任务页 `当前已邀请` 区块展示。列表、单独邀请和全部邀请必须复用同一套候选扫描与条件判定；羁绊筛选和名称搜索只限制前端展示或批量邀请的前端等级参数，不得改变后端候选扫描。符合条件后调用 `StatusTracker.RecordInvitedGuest()` 写入今晚邀请名单。不要调用 `DaySceneChatSelectionPannel.InviteSpecGuest()`，该方法会触发随机成功率并记录今日已尝试，会把可邀请但随机失败的稀客错误跳过。不要把 `StatusTracker.HasTemptInvited()` 作为跳过条件，避免旧版本或手动失败尝试阻止邀请。该功能不得直接刷客、不得推进日间时间、不得修改受控稀客队列。
- 运行时稀客订单捕获只能长期显示仍匹配当前活跃稀客，或仍能从原运行时订单对象和控制器确认未完成的订单；无法确认仍在场的捕获项只允许短暂宽限，避免跨伴随窗口重启或跨天残留。离开夜间经营场景或清除运行时状态时必须清空捕获缓存。经营中稀客订单行的删除按钮调用 `/orders/rare/dismiss` 清理插件端缓存，不应只做前端隐藏。
- 稀客和普客自动化诊断都必须按订单 key 展示，不只依赖长文本日志。每笔订单需要显示步骤、已处理阶段、重试/回退次数、最近原因和人工确认状态，并提供单笔重试和重置。普通重试只解除该订单暂停并保留已完成阶段；普通重置只重建该订单本地判断。副作用不确定时禁用重试并把重置入口改为 `确认已处理`；即使订单已从快照消失，也必须在独立待确认列表保留 ACK 入口。确认必须由当前 session lease 所有者提交准确 barrier sequence，并按后端返回的 acknowledged sequences 清理本地 latch；失败时保持阻断。三种操作都不得影响其他订单或自动化总开关。
- 经营中页需要显示自动化资源状态：厨具预约按当前已摆放厨具容量展示普客和稀客的本轮真实预计占用。特殊经营普客必须复用 Worker 预计算的执行目标结果；预计差评、目标不一致或回调链不完整等 blocked 订单只显示为未占用厨具原因，不得计入预约容量。该视图只用于诊断和用户判断，不应反向驱动自动化状态机。
- 稀客订单完成前必须同时校验料理和酒水送达状态，不要只返回或处理第一个缺失项。如果快照已有绑定该订单和目标料理的活动 `AutomationCookingJob`，前后端都应消费其结构化状态，不得因厨具冻结或 Debuff 重复开锅；job 以 interrupted/completed/cancelled 终止后再按订单事实决定重新开锅或推进下一阶段。
- 自动开始料理固定尝试完成原生 QTE 奖励结算，不再提供跳过或完成 QTE 的配置开关。该功能不打开游戏音游面板，只尝试调用游戏 QTE 成功奖励入口；运行时失败时返回诊断信息，不应中断已开始的料理。
- 游戏内料理/材料/酒水列表置顶及目标列表项高亮是实验性功能，只允许在对应面板刷新作用域内影响游戏原生 `CheckPinned` 判定，并在精确元素绑定回调中增加可见提示；不得直接改写 IL2CPP 列表、自动点击或绕过游戏自身筛选。本地 API 更新置顶目标失败时必须静默降级。
- 普客订单被动快照应保持约 1 秒级缓存；普客自动化动作后只强制刷新普客订单快照。没有活动 `AutomationCookingJob` 时不要在 `Update()` 热路径轮询料理 job。
- 伴随窗口 `日志` 页只控制总日志 `aggregate-mod.log` 和诊断包导出，不再读取 `BepInEx/LogOutput.log`、自动化独立日志或经营诊断独立日志。总日志分片上限不保护 BepInEx/Unity 共享 output log；任何后台 worker 都不得用无限异常重试向共享日志持续写入，Mod 也不得接管、截断或删除共享日志。
- Mod 不得改写全局 `BepInEx.cfg` 或主动隐藏宿主控制台，避免影响同进程中的其他插件；控制台是否启用由 BepInEx/用户配置决定。`SetConsoleUtf8` 只负责用户显式启用时的当前控制台编码。
- 本地 API 提交到 Unity 主线程的库存、订单和稀客邀请命令必须有排队上限和明确状态。尚未开始的命令超时后必须原子取消，主线程不得晚到执行；已经开始的命令必须返回确定结果，避免客户端在副作用已发生时重试。控制器销毁时先取消并唤醒排队命令，再停止并等待有界的 HTTP handler；每类队列每帧最多执行一个有效命令。
- 运行时库存修改只允许调用当前游戏的 `RunTimeStorage.IngredientInRange/IngredientOutRange/BeverageInRange/BeverageOutRange` 原生入口，并在调用后读取最终数量做精确校验。不得在原生调用异常时直接写 `Ingredients`/`Beverages` 私有字典绕过 callback。
- 诊断开关只能增加采样和日志，不得改变推荐、订单或自动化的权威业务输入。夜间经营在有 runtime capture 时始终以 capture 为正式来源；反射探针结果只进入诊断快照。
- 自动更新同一时刻只允许一个检查、下载或安装操作。`update-manifest.json` 必须严格校验当前 schema、版本/tag/channel、资产名、SHA256 和大小；更新包流式写盘后同时校验长度和 hash。安装只能使用已校验 staged 包中的 updater，且 staged 版本必须高于当前版本，禁止旧暂存包降级安装。
- 面向普通用户的伴随窗口默认隐藏调试信息。新增扫描状态、运行时来源、性能耗时、内部订单来源、订单 key、总日志、任务 label/source 等偏排查内容时，必须受 `CompanionPreferences.showDebugDetails` 总开关控制；该开关默认关闭，并只在 `设置 -> 窗口 -> 显示调试信息` 中开启。
- 伴随窗口信息密度优先通过内部页签控制，不要把所有区块直接堆到同一页面。`概览` 固定使用 `状态 / 库存 / 操作` 分栏；`设置` 固定使用 `窗口 / 连接 / 推荐 / 自动化 / 更新` 分栏。连接配置必须集中在 `连接` 分栏，稀客专注模式默认精简放在 `推荐` 分栏。普客、稀客、经营中和稀客订单专注模式的推荐/订单列表应保留稳定内容区域和内部滚动，避免数据从空变有时造成大幅布局跳动。
- 游戏内不再保留 IMGUI 面板；游戏侧只负责后台读取、自动化执行、本地 API 和伴随窗口唤起，所有用户交互放在独立伴随窗口。

## 文档维护

- 用户安装和使用写入 `mods/bepinex/README.md`。
- 开发和构建写入 `mods/bepinex/README.dev.md`。
- 机制或运行时读取路径变化时，同步更新 `docs/` 和 `mods/bepinex/docs/`。
- 用户可见功能、快捷键、设置项、自动化行为、页面布局或本地 API 变化后，必须在提交前同步文档。
- 版本发布前如果文档落后，先补文档再提交版本号并合并 `main`。
