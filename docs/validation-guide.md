# 验证指南

更新：2026-11-06。只把实际执行结果记为通过。Flutter 组件、真实 HTTP 隔离宿主、纯业务差分和原生受控测试各自证明不同边界；不互相替代。本文维护当前入口与证据要求，历史断言数量和某次本机结果留在对应验证记录中。

## 按改动选择

| 范围 | 检查 |
| --- | --- |
| Flutter UI/状态 | apps/companion-flutter 中 flutter analyze、flutter test；1280/640/390、大字体、深色、中文IME、焦点、空/错状态 |
| Dart协议与客户端 | companion_protocol / companion_client 分别 analyze；共用回归位于 tests/flutter-contracts，运行 dart analyze、dart run bin/verify.dart；完整模式先构建真实 .NET 6 host |
| Windows平台 | packages/companion_platform 测试 + Windows Release构建 + 隔离控制端口/存储的原生smoke |
| Android平台 | 平台Dart测试 + 双ABI Release编译；正式升级还需正确签名与设备测试 |
| C#业务 | corepack pnpm audit:business；冻结oracle差分、模拟订单/租约/场景边界 |
| 完整包/更新 | Node/C#/Rust共同manifest负例、updater测试、隔离安装/故障回退、路径/链接拒绝 |
| Mod | Release构建，真实.NET6宿主；按变化补现有runtime smoke |
| 文档 | 路径/链接/命令审查、git diff --check |
| 仅源码注释 | 对照实现和调用方复核语义，检查源码词法单元或语法结构未变；执行相关语言分析/编译及现有回归，生成注释另做生成一致性检查 |

## 非游戏验证入口

游戏内专注显示另运行 `dotnet run --project tests/runtime-game-focus-overlay/RuntimeGameFocusOverlaySmoke.csproj -c Release -p:NuGetAudit=false`，以及构建正式契约宿主后运行 `dotnet tests/flutter-contracts/host/bin/Release/net6.0/FlutterContractsHost.dll --game-focus-overlay`。前者为托管UGUI模拟，验证图标缓存/分帧解码、紧凑卡片布局、永久输入穿透、外部位置尺寸和稳定帧开销；后者链接真实存储与订单投影，验证v2迁移、精确图片/颜色/内容过滤与身份隔离。两者均不连接游戏，不能代替真实游戏内绘制效果验收。

新增功能按以下入口复核，完整命令与逐条断言以各测试工程为准：

| 功能 | 隔离验证入口 |
| --- | --- |
| 黑暗料理任务与供给 | `tests/dark-cuisine-task-priority/`、`tests/runtime-dark-cuisine/`及自动化工程中的特殊料理用例，纳入`audit:business` |
| 稀客队列及等待条规则 | `tests/runtime-rare-guest-participation/`、`tests/runtime-automation-control/`；契约宿主`--participation-admission`、`--order-patience`、`--rare-patience-auto-resume` |
| 未开锅方案更换与缺料等待 | `tests/csharp-automation/RareIngredientFallbackTests.cs`；契约宿主`--cooking-ingredient-wait` |
| 暂停订单完整显示 | 契约宿主`--rare-order-display`及Flutter的`paused_service_orders_test.dart` |
| 多目标背景与排序 | `tests/runtime-cooker-color-blocks/`、`tests/runtime-list-color-blocks/`、`tests/runtime-ui-list-order/`及`ui-pinning-runtime`，纳入`audit:business` |
| 调度设置与专注布局 | Flutter的`rare_guest_scheduling_test.dart`、`game_focus_overlay_controls_test.dart`、`game_focus_mirror_layout_test.dart`，覆盖窄屏、字号和状态变动 |

契约宿主各模式需要先编译当前`FlutterContractsHost.csproj`；上述模式已由完整`flutter:contracts-test`串行调用，不能使用旧宿主DLL代替当前源码。明确缺料的等待测试不模拟实际扣料，未知提交的安全屏障仍须独立验证。

```powershell
# 先设置 MYSTIA_FLUTTER_SDK_ROOT；运行库恢复只写隔离测试目录，不连接游戏。
pwsh -File scripts/restore-bepinex-test-runtime.ps1
dotnet build tests/flutter-contracts/host/FlutterContractsHost.csproj -c Release -p:NuGetAudit=false
corepack pnpm flutter:format-check
corepack pnpm flutter:generated-check
corepack pnpm flutter:verify
corepack pnpm flutter:contracts-test
cargo test --locked --manifest-path native/updater/Cargo.toml
corepack pnpm audit:business
```

以上在仓库根目录执行，Flutter/Dart 入口核对根工具锁。`flutter:verify` 只覆盖应用 analyze/test，`flutter:contracts-test` 覆盖独立 Dart runner；二者不隐含格式、生成一致性或平台包测试。

修改 `packages/companion_platform` 时，在包目录用同一 SDK 的 Flutter 执行 `pub get --enforce-lockfile`、`analyze --fatal-infos` 和 `test`。协议包与客户端包当前均没有独立 `test` 目录；客户端包有受管 `pubspec.lock`，可用同一 SDK 的 Dart 执行 `pub get --enforce-lockfile`，同时解析其协议包路径依赖。协议包自身没有受管锁文件，不应单独套用 `--enforce-lockfile`。两包分别执行 `dart analyze --fatal-infos`，完整回归通过 `tests/flutter-contracts` 的锁定依赖及统一 runner 执行。纯 Dart 契约工程本身的 analyze 也需另行运行，不能将不存在的测试入口记为通过。

公开仓库或没有私有引用的环境使用 `flutter:verify-unit`（排除 `production-http` 测试）与 `flutter:contracts-unit`。这是明确缩小的公共子集，不代表真实生产 HTTP、Mod 构建或原生平台已经验证。普通 CI 仅由 `.github/workflows/ci.yml` 的 `workflow_dispatch` 手动触发，推送或创建 PR 不会自动运行。

真实HTTP宿主直接编译当前Mod reader/router/store，运行于.NET6，使用随机回环端口、独占临时目录与公开合成Token。游戏写入委托明确拒绝，不启动游戏。详情见[协议测试说明](../tests/flutter-contracts/README.md)。

连接与诊断管理专项使用真实 BepInEx 配置保存。上述恢复脚本从锁定 #783 原始分发包验证整包及成员摘要，独立准备 `SemanticVersioning.dll`；正确缓存复用，错误缓存保留并报错。离线使用 `-ArchivePath <已下载原始ZIP>`，自定义目录使用 `-OutputDirectory` 并在宿主构建时传相同的 `-p:BepInExRuntimeDirectory`；完整发布构建入口自动调用恢复。具体约束见[协议测试说明](../tests/flutter-contracts/README.md#执行)。新包列举/删除、凭据更新及配置保存的正反向边界由完整 runner 一并执行。

恢复脚本自身的隔离事务检查为 `pwsh -File tests/build-artifacts/bepinex-test-runtime-audit.ps1 -ArchivePath <锁定原始ZIP>`，覆盖空目录恢复、正确缓存复用、错误包拒绝、错误缓存保留和临时文件清理。该检查不运行发布 workflow、不清理用户工作区缓存；发布工作流与脚本策略另由 `node tests/github-actions/release-workflow-audit.mjs` 和 `node tests/flutter-release/release-policy-audit.mjs` 静态核对。

Widget截图可设置 `MYSTIA_UI_FONT` 为本机中文字体文件、`MYSTIA_UI_EVIDENCE` 为临时输出目录；字体仅用于本机审阅，不打包。截图之外仍需断言命令次数、权限、焦点、滚动与状态边界。

`audit:business` 还包含真实 DTO 的同次采集复用测试及聚合日志写入/轮换测试。人工大目录可用 `node tests/csharp-business-orders/benchmark.mjs --write-fixture` 准备，再运行 `node tests/csharp-business-orders/arrival-cache.mjs` 与 `dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --load-stream --client-poll`。前者区分相同需求复用、无关客人变化和库存重算，后者在连续250/500ms输入中以750ms读取实际宿主；记录采样到的当前版本，不能据此声称测得Unity帧率。

现有三项真实Harmony/MonoMod探针的锁定入口为 `corepack pnpm test:dotnet6-harmony`，需要锁定Docker环境。环境不可用时明确记录未执行，不能用普通net6单元测试冒充这三项结果。

`audit:business`中的`ui-pinning-runtime`默认模式也会初始化真实Harmony，需要匹配的MonoMod运行依赖；仅有正式Mod编译引用不保证该测试运行库齐全。缺少依赖时完整门禁会失败，不能把此前已通过的项目记为整套通过。可另运行该工程已有的`--color-blocks-only`、`--beverage-targets`、`--list-order-context`等隔离模式及其余独立项目，逐项记录实际结果；这些补测不补足Harmony注入证据，也不应修改默认门禁以静默跳过。

`audit:business` 同时运行窗口计时/帧间隔、Steam 子进程环境、订单高亮身份失效与座位投影的纯托管回归。八卡稳定场景比较真实生产维护代码的属性读取次数，座位测试比较纯托管投影分配；这些结果不换算为 Unity 帧率。实际用户复现时的五秒性能窗口及日志字段见[日志与诊断](observability.md)。

冻结输入专项分别验证自动化与游戏辅助的新组件入口和原 JSON 入口完整等价、输出所有权及当前性门禁。`--warm-pipeline` 使用人工八种点单，覆盖捕获、绑定、编排、目标、发布、HTTP 整链的新旧分配对照和并发读写；必须先生成上述大目录夹具。方案物化专项使用固定 TypeScript 参考比较完整结果，保证任务/收藏提升发生在80/32项截断之前。这些专项已纳入串行业务门禁，累计分配与墙钟时间均不代表 Unity FPS。

## 真实游戏边界

列表按订单排序的离线回归使用 `dotnet run --project tests/runtime-ui-list-order/RuntimeUiListOrderSmoke.csproj -c Release`，直接链接正式稳定置换及原生反射桥；`ui-pinning-runtime -- --list-order-context`验证实际刷新作用域、主线程和发布租约。加料声明的绑定前身份映射由 `runtime-target-recipe-variant` 验证；本机缺少Harmony运行依赖时可显式使用 `--without-harmony` 执行其余状态机组，输出必须注明跳过六组FullVisual补丁及HarmonyContractSmoke，不能将其记为完整注入通过。三类列表桥的类型及调用顺序按当前metadata、interop、Native交叉核对；测试中的原生列表、Harmony与变体边界仍为可观测替身。

食材/酒水列表的回归夹具必须以继承 `Il2CppSystem.ValueType` 的托管类模拟键值对，并区分泛型引用参数和非泛型装箱参数的写入，避免CLR结构体掩盖原生封送缺陷。另以 `tests/runtime-ui-list-order/verify-interop-contract.ps1 -InteropAssemblies <实际安装及离线生成的Il2Cppmscorlib.dll路径数组> -CecilAssembly <Mono.Cecil.dll路径>` 只读核对真实DLL的类型、精确setter签名与CIL调用；该工具不执行游戏程序集。原生IList的类型检查、拆箱与赋值行为仍须和当前Native资料交叉核对，不能由托管替身单独证明。

涉及 Unity 对象、Hook 或原生游戏副作用的最终行为需要对应游戏环境证明。离线构建、模拟经营、组件截图和用户提供的经营测试反馈应分别标注来源与范围，不据此推导未执行的平台或场景已经通过。只有文档整理或历史压缩时检查链接、命令、文件树与差异即可，不触发游戏测试、安装或发布。

安装授权与测试授权分别确认；成套安装核对文件清单、哈希、备份及配置/存档保护，不自动启动游戏。真实 Harmony/MonoMod 的锁定容器、Windows 多 DPI、实体手柄、Android 系统版本及真实 LAN 切换等未执行项，要在当次报告明确列出。
