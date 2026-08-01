# mystia-steward-companion BepInEx Mod 开发说明

本文档面向开发者，记录本 Mod 的本地开发、构建、运行时读取和调试方式。用户安装和使用说明见 [README.md](README.md)。

## 项目结构

- `src/Core/`：推荐算法、数据模型和排序规则。
- `src/Save/`：运行时反射读取、兼容探测和推荐状态构造。
- `src/Ui/`：伴随窗口控制器、运行时循环和快照缓存。
- `src/Plugin/`：BepInEx 入口、配置和伴随窗口启动逻辑。
- `src/LocalApi/`：Token 保护的本地 API，始终保留回环 listener，也可显式开启附加 LAN listener。
- `References/`：本机编译引用 DLL，不提交到仓库。
- `tools/`：前置检查、构建和打包脚本。

运行时读取说明见 [docs/RUNTIME_PROVIDER_NOTES.md](docs/RUNTIME_PROVIDER_NOTES.md)。

## 开发环境

Windows 上通常需要：

- .NET 6 SDK 或更新版本。
- Node.js 20+，并通过 Corepack 使用仓库固定的 `pnpm@10.10.0`。
- PowerShell 7。
- Rust stable、Microsoft C++ Build Tools 2022 或 Visual Studio “使用 C++ 的桌面开发”组件。
- Microsoft Edge WebView2 Runtime。
- 已安装并启动过一次 BepInEx Unity IL2CPP 的游戏目录；普通开发和验证优先使用 #783 构建 `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.783+c58c42d.zip`，不要直接追最新 Bleeding Edge。

推荐初始化命令：

```powershell
corepack enable
corepack prepare pnpm@10.10.0 --activate
winget install Rustlang.Rustup
```

Linux 验证 Tauri 构建时还需要：

```bash
sudo apt-get install -y pkg-config libwebkit2gtk-4.1-dev libayatana-appindicator3-dev librsvg2-dev libssl-dev libxdo-dev
```

## 构建引用

本项目不提交 BepInEx 和 Unity DLL。构建前只需要把 BepInEx、Il2CppInterop 和 Unity 基础引用复制到 `References/`，不需要也不应该复制额外的游戏业务 DLL：

```text
mods/bepinex/
  References/
    BepInEx.Core.dll
    BepInEx.Unity.IL2CPP.dll
    0Harmony.dll
    Il2CppInterop.Runtime.dll
    Il2Cppmscorlib.dll
    UnityEngine.CoreModule.dll
    UnityEngine.InputLegacyModule.dll
```

常见来源：

- `游戏根目录/BepInEx/core/`
- `游戏根目录/BepInEx/interop/`

`tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj` 会使用真实 Harmony wrapper 验证 scoped prefix 返回传播，以及料理/材料/酒水列表元素 Hook、Food/Beverage 类型隔离、经营 generation 校验、Closing 旧目标失效、下一场隔离、池化重绑恢复、后台目标发布、已打开列表按目标代际单次主线程刷新，以及开关仍启用但所有组件 ID 为空时恢复原生置顶语义、列表原色和厨具禁用状态，因此运行该 smoke 时还要从 `BepInEx/core` 复制 `MonoMod.RuntimeDetour.dll` 和 `MonoMod.Utils.dll`。这两个 DLL 是测试运行依赖，不加入 Mod 编译和发布 preflight；引用放在外部目录时，通过 `-p:ReferenceDir="..."` 传给 `dotnet run`。

复制完成后运行前置检查：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\preflight.ps1
```

Git Bash 可运行：

```bash
bash mods/bepinex/tools/preflight.sh
```

## 一键构建

PowerShell 7 从仓库根目录执行：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1
```

该脚本会依次执行 `pnpm install --frozen-lockfile`、`preflight.ps1`、运行时数据模式提示、伴随窗口前端构建、Tauri 伴随窗口构建、Mod DLL 构建和安装包生成。
脚本开始时会先检查 `mods\bepinex\References` 中的 BepInEx/Unity 引用 DLL。若引用 DLL 放在其他目录，可显式传入：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

常用增量构建：

```powershell
# 跳过依赖安装
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -SkipInstall

# 只改 C# Mod，不重建伴随窗口前端和 Tauri 程序
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -SkipInstall -SkipFrontendBuild -SkipTauriBuild
```

如果修改了 `apps/companion/src/` 或 Tauri 窗口相关代码，不要使用 `-SkipTauriBuild`，否则安装包中的伴随窗口仍会使用旧产物。

如发布机已配置 Android SDK/NDK、JDK、Rust Android targets 和 APK 签名配置，可在同一次发布构建中生成 Android APK：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -BuildAndroidApk
```

该参数会在 Windows 伴随窗口、Mod 包和 Windows 独立伴随窗口 EXE 生成后，额外执行 `pnpm tauri:android:apk:signed`，并把签名 APK 放到 `mods\bepinex\dist\mystia-steward-companion-android-arm64-v8a.apk` 和 `mods\bepinex\dist\mystia-steward-companion-android-armeabi-v7a.apk`。默认不启用该参数，避免普通 Windows-only 构建被 Android 工具链、keystore 或 Android 专用 Rust LTO 体积优化绑定。

## 拆分构建

需要拆分排查时，可从仓库根目录手动运行：

```bash
pnpm install
pnpm build
pnpm tauri:build
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

## 构建产物空间管理

仓库内的 Tauri/Cargo 多目标缓存和 Android Gradle 中间目录可能在多次桌面、Android ABI 与检查构建后持续增长。使用统一命令查看和治理，不要手工删除 Cargo `deps` 或单个 hash 文件：

```bash
# 只报告各类产物大小
pnpm artifacts:report

# 默认超过 12 GiB 时清理到 8 GiB；Android/.NET 分类上限为 1.5/0.5 GiB
pnpm artifacts:prune

# 预览完整清理清单；确认后去掉 --dry-run 执行
pnpm artifacts:clean -- --dry-run
pnpm artifacts:clean
```

清理器以完整 Cargo profile/target triple、Gradle build 目录或单个 .NET 项目的 `bin/obj` 为单位，并拒绝白名单外路径和符号链接。`mods/bepinex/dist`、`References`、`temp`、`node_modules`、`.playwright-cli`、keystore 和签名配置永远不在自动清理范围。首次清理后 Rust/Gradle 会完整重建，耗时增加属于预期。

统计和配额使用文件逻辑大小，保证 Windows/Linux 口径一致并保守预留磁盘空间。`pnpm tauri:dev`、`tauri:build` 和普通 Android 构建会在启动前清理旧缓存；直接运行 Cargo/dotnet 后使用 `pnpm artifacts:report` 检查。`prune`/`clean` 之间有独占锁，但无法接管直接启动的 Cargo、Gradle、Vite 或 dotnet 进程；不要在任何构建仍运行时手动执行清理。

`build-release.ps1` 默认在安全边界执行配额检查，可通过 `-BuildCacheLimitGiB` 和 `-BuildCacheTargetGiB` 调整高低水位；仅在诊断时使用 `-SkipBuildCacheCleanup`。完整发布完成后才会清理已经复制到 `dist` 的中间产物；使用 `-SkipPackage` 生成裸构建输出时不会执行事后清理。

## Android 伴随窗口

Android 版是给 B 设备使用的移动端伴随窗口，只通过可信局域网连接 A 设备上的游戏和 Mod。它不是 Windows EXE 的转换产物，也不包含托盘、置顶、鼠标穿透、焦点切换、单实例控制和游戏关闭自动退出等桌面能力。

Android 与桌面正式构建统一通过 Tauri Rust `request_local_api` command 使用原生 TCP 访问 Mod；只有浏览器/Vite 开发模式直接 `fetch` mock API。不得为 Android 保留 WebView direct-fetch fallback，也不得通过放宽 CSP 绕过原生代理错误。

Android 开发或发布机器还需要：

- Android Studio、Android SDK、platform-tools、build-tools 和 NDK。
- JDK 17，并确保 `JAVA_HOME`、`ANDROID_HOME` 或 `ANDROID_SDK_ROOT` 指向正确位置。
- Rust Android targets，例如 `aarch64-linux-android`、`armv7-linux-androideabi`、`i686-linux-android` 和 `x86_64-linux-android`。
- 真机或模拟器；LAN 连接测试建议至少覆盖手机竖屏、手机横屏和平板/大屏横屏。

常用命令：

```bash
pnpm tauri:android:dev
pnpm tauri:android:build
pnpm tauri:android:apk
pnpm tauri:android:apk:signed
```

仓库已包含 Tauri mobile 生成的 Android 工程，路径为 `apps/companion/src-tauri/gen/android/`。不要删除或绕过该工程；需要重新生成时才运行 `tauri android init`。签名文件、keystore、Gradle 缓存、JNI `.so` 和 Android build 输出不能提交。

`pnpm tauri:android:apk` 可用于本地构建验证，默认输出按 ABI 拆分的未签名 release APK：

```text
apps/companion/src-tauri/gen/android/app/build/outputs/apk/arm64/release/app-arm64-release-unsigned.apk
apps/companion/src-tauri/gen/android/app/build/outputs/apk/arm/release/app-arm-release-unsigned.apk
```

发布用 APK 必须完成签名。先生成或准备自己的发布 keystore，例如 Windows PowerShell：

```powershell
keytool -genkeypair -v `
  -keystore "$env:USERPROFILE\.android\mystia-steward-companion-release.jks" `
  -storetype PKCS12 `
  -keyalg RSA `
  -keysize 2048 `
  -validity 10000 `
  -alias mystia-steward-companion
```

然后在 `apps/companion/src-tauri/gen/android/keystore.properties` 写入本机私有签名配置。该文件已被 Git 忽略，不能提交：

```properties
keyAlias=mystia-steward-companion
password=<keystore 和 key 共用密码>
storeFile=C:\\Users\\Administrator\\.android\\mystia-steward-companion-release.jks
```

如果 keystore 密码和 key 密码不同，改用 `storePassword` 和 `keyPassword`：

```properties
keyAlias=mystia-steward-companion
storePassword=<keystore 密码>
keyPassword=<key 密码>
storeFile=C:\\Users\\Administrator\\.android\\mystia-steward-companion-release.jks
```

签名发布包使用：

```bash
pnpm tauri:android:apk:signed
```

该命令会构建 release APK、调用 `apksigner verify --verbose --print-certs` 验签，并在全部目标验证通过后原子复制发布资产到：

```text
mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk
mods/bepinex/dist/mystia-steward-companion-android-armeabi-v7a.apk
```

签名 APK 脚本会在 Android 构建进程内注入 `CARGO_PROFILE_RELEASE_STRIP=symbols`、`CARGO_PROFILE_RELEASE_LTO=thin` 和 `CARGO_PROFILE_RELEASE_CODEGEN_UNITS=1`，用于降低 APK 体积。该优化不会写入全局 Cargo release profile，避免普通 Windows 发布构建在 Rust 链接优化阶段耗时过长。

全部 APK 已落入 `dist` 后，脚本才允许按统一空间配额清理 Android Gradle/Cargo 中间产物。也可以通过 `build-release.ps1 -BuildAndroidApk` 或 `publish-release.ps1 -BuildAndroidApk` 在本地构建/发布流程中自动生成这些文件。如果 APK 放在其他位置，发布时通过 `publish-release.ps1 -AndroidApkPath "D:\path\android-apks"` 指定 APK 文件或所在目录。APK 只作为 GitHub Release 的独立下载资产，不写入 `update-manifest.json`，也不参与 Mod 自动更新。

Windows 下如果 `pnpm tauri:android:apk` 出现 `this and base files have different roots: C:\... and D:\...`，这是 Kotlin 增量编译缓存跨盘符相对路径问题。仓库已在 Android Gradle 配置中关闭 Kotlin incremental compilation；若本机仍复用旧 daemon 或旧缓存，先执行：

```powershell
cd apps\companion\src-tauri\gen\android
.\gradlew --stop
Remove-Item -Recurse -Force .gradle, build, app\build, buildSrc\build -ErrorAction SilentlyContinue
```

## 模拟本地 API 与 UI 审查

不启动游戏时，可以用仓库内 mock 服务给伴随窗口提供一组稳定的运行时数据。先安装依赖：

```bash
pnpm install
```

启动 mock API：

```bash
pnpm mock:api
```

默认地址和 token：

```text
http://127.0.0.1:32145
mock-token
```

另开一个终端启动伴随窗口前端：

```bash
pnpm dev -- --host 127.0.0.1 --port 5173
```

浏览器打开 `http://127.0.0.1:5173` 后，在开发者工具 Console 写入本地连接信息并刷新页面：

```js
localStorage.setItem('mystia-steward-companion-mod-api-endpoint', 'http://127.0.0.1:32145');
localStorage.setItem('mystia-steward-companion-mod-api-token', 'mock-token');
localStorage.setItem('mystia-steward-companion-show-debug-details', '1');
location.reload();
```

需要跑自动化样式审查时，先安装 Playwright 浏览器：

```bash
pnpm exec playwright install chromium
```

保持 mock API 和前端服务运行，然后执行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:5173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:ui
```

报告和截图默认写到 `/tmp/mystia-companion-ui-audit`。通用 UI 巡检覆盖 1280x900、900x760 和 640x760 三组视口；640px 用于验证 Tauri 桌面最小宽度下核心内容保持双列、顶部状态保持三列、一级导航以五列两行完整显示，并检查连接工具栏四项保持单行、设置五组分段标签不变形、专注工具栏右对齐和自定义配方入口与料理标题同行。如果使用 `pnpm preview`，把 `MYSTIA_APP_URL` 改成 Vite preview 输出的地址，通常是 `http://127.0.0.1:4173`。

修改 BepInEx 控制台显隐、日志设置协议或日志页按钮后，在同一 mock API 与 preview 环境运行：

```bash
dotnet run --project tests/bepinex-console-window/BepInExConsoleWindowSmoke.csproj -c Release
dotnet run --project tests/local-api-method-matrix/LocalApiMethodMatrixSmoke.csproj -c Release
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:logs:console
```

控制台 smoke 锁定默认关闭、Windows-only、BepInEx driver/Win32 窗口/真实可见性三态分离、仅 Mod 本次新建窗口禁用关闭命令、唯一 `ConsoleLogListener`、无焦点显示、hide-only，以及显隐/启动偏好的同锁提交；方法矩阵锁定 `/logs/console` 只接受 POST。前端审计验证 IPv4 回环端点限制、远程禁用、迟到读取隔离、动作错误保持、显示/隐藏与刷新持久化、键盘和模拟标准手柄 `A` 激活，以及 640px 布局。Linux 测试不能替代 Windows 实机的 Win32 窗口、重复切换和持续日志验证。

修改任务生命周期诊断、BepInEx 783 任务容器形态或诊断包任务状态后，运行：

```bash
dotnet run --project tests/runtime-reflection/RuntimeReflectionSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-load-seed/RuntimeMissionLoadSeedSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-definition/RuntimeMissionDefinitionSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-diagnostic/RuntimeMissionDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-scheduled-event-diagnostic/RuntimeScheduledEventDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-available-missions/RuntimeAvailableMissionsSmoke.csproj -c Release
dotnet run --project tests/runtime-serve-in-work-diagnostic/RuntimeServeInWorkMissionDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-tracked-missions/RuntimeTrackedMissionsSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-recipe-priority/RuntimeMissionRecipePrioritySmoke.csproj -c Release
pnpm audit:runtime-missions
pnpm audit:runtime-missions:ui
pnpm audit:recommendations
pnpm audit:special-business
```

读档种子 smoke 验证有界 JSON 结构、实际 DLC 选择、日期偏移、bucket 合并、tracking 标签唯一性、finished 标签重复频次和畸形输入 fail-closed；定义审计锁定 `TargetNodeExists -> RefMission`、精确条件数组和语言字典；状态 smoke 验证存档 bool 只作诊断证据，读档初始化按已验证 bucket 和顺序绑定精确原任务 identity 后各执行一次 `UpdateFinishStates`，新任务在 `GenerateTrackingData` 捕获对象并等待 `StartMission` 完成列表插入，只在同一 `Ready + runtimeAvailable` generation、所有者线程和定义预读门禁内按需补做一次刷新，并要求条件数量与静态定义一致后才投影为 `Tracking/Fulfilled`，后续游戏自然刷新继续更新同一 identity；scheduled-event smoke 验证 frozen 诊断、当天与永久 bucket 精确查键、BepInEx 783 `Il2CppStringArray`、两类 post mission 来源、finished 完整序列、type 3/5 eligibility 和生成/触发入口禁令；available missions smoke 验证每请求 fresh read、type 5 + `postMissionsAfterPerformance` 唯一业务边界、`preNodes`、active、looped/finished、任务/日间/mapped 代际、稳定签名和 unchanged 响应，且不调用原生推进。ServeInWork smoke 验证被动查询结果受任务/经营 generation、canonical 稀客身份和静态定义约束，并锁定成功任务生命周期按 active canonical/food pair 精确复核、无关刷新保留、完成/移除删除、失败全清和有界诊断。tracked missions smoke 另验证 active-only 业务投影、受控初始刷新、零条件任务、`Unverified/Tracking/Fulfilled` 三态条件、后续自然刷新形态 fail-closed/恢复、不可用状态和稳定内容签名；recipe priority smoke 验证普通经营、唯一未送餐订单、精确 `foodId + recipeId`、任务/经营 generation 与特殊经营门禁，以及复核保留和失效后清理。源码审计继续锁定 TryUpgrade 后单次 `GenerateSaveString(None)`、Initialize 实际 DLC 字典的 `Count + ContainsKey` 与已知 bucket 精确查键、具体 List 索引、两个且仅两个主动刷新调用边界、新任务三重代际门禁、刷新前后容器与 identity 不变量、`FinishNodeExtern` append-only 快照、原生异常透传，以及任务完成、奖励、移除和宽泛枚举禁止清单；前端审计验证 `全部 / 可接取 / 可完成 / 进行中 / 待确认` 互斥页签、稳定排序、零计数空态、窄屏横向滚动和手柄焦点。

修改手柄输入状态机、复合控件焦点语义、动态回焦、局部滚动或游戏/伴随窗口焦点切换后，运行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:gamepad
cargo test --manifest-path apps/companion/src-tauri/Cargo.toml
dotnet run --project tests/controller-toggle-state/ControllerToggleStateSmoke.csproj -c Release
```

`audit:gamepad` 先验证纯输入状态机的 standard mapping、活动设备所有权、中立门控、按键模拟量、摇杆滞回、方向仲裁、重复节奏和 RS 隔离，再用 Playwright 验证 A/B/X/Y、LB/RB、LT/RT、Select/MultiSelect、Tabs、SegmentedControl、NumberInput、Slider、Dialog、动态回焦、局部滚动，以及生效自定义配方入口展开后进入详情滚动区的声明式确认焦点。响应式巡检覆盖 1280x900/100%、640x520/130%、390x844/90% 三组窗口和字号组合。Rust 单测验证所有切换来源共用的 applied-only cooldown gate；C# smoke 验证 RS 持续按住、迟到边沿和物理释放后的重新武装。

修改字体 token、字号偏好、更新状态协议或全局更新提示后，在同一 mock API 与 preview 环境中运行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 MYSTIA_API_URL=http://127.0.0.1:32145 pnpm audit:font-scale
pnpm audit:updates
MYSTIA_APP_URL=http://127.0.0.1:4173 MYSTIA_API_URL=http://127.0.0.1:32145 pnpm audit:updates:ui
```

字号巡检覆盖 90%/100%/130%、非法值归一化、鼠标/键盘操作、刷新持久化、恢复默认、640x520、390x844、全部页签、设置五个分栏、设置分段控件单行几何、连接工具栏单行、稀客订单专注工具栏右对齐、生效配方入口与料理标题同行和 Select Portal；截图写入 `/tmp/mystia-companion-font-scale-audit`。更新协议审计覆盖启动 `idle -> checking -> available` 收敛、状态读取失败退避、请求代际、endpoint/tag 延后键、安装提示和 Release URL 限制；提示巡检覆盖动作中断连、连接身份切换、迟到响应隔离、首帧延后状态和安装失败，截图写入 `/tmp/mystia-companion-update-ui-audit`。

修改经营中推荐主计划、页面首项投影、无计划阻塞诊断、自动化初始目标或收藏限定后，先运行：

```bash
pnpm audit:recommendations
```

该审计验证完整候选按硬过滤、任务料理置顶、自定义置顶、收藏料理/酒水置顶和普通权重生成唯一 `executionPlans[0]` 主计划；exact active ServeInWork 任务料理只跳过普通 food Tag 和料理厌恶判断，酒水与全部硬门禁继续生效。收藏限定在执行计划截断前归一化，页面料理/酒水首项投影该计划，自动化与游戏界面辅助不再扫描后续计划；还验证血池地狱第二阶段混合的 `NormalOrder` / `SpecialOrder` 与第三阶段 `NormalOrder` 只在精确 BOSS 身份下保留原订单并同时满足两个动态料理 Tag、缺少任一目标时保持无计划、真正的非 BOSS 普通订单结果不变，以及多订单全局界面目标优先任务计划。订单展示审计另锁定语义签名不含观测时间/来源/显示标签、同硬上下文的逐单稳定展示、新订单局部 pending、送达/需求变化立即失效，以及展示投影不进入动作路径。只有零计划结果才生成 `blockedDiagnostic`，缺厨具、缺基础材料、酒水 Tag、预算和幽幽子二阶段 `ExGood` 不足能定位到各自首个清零阶段，诊断不会改变候选、排序或自动化目标。

修改游戏界面置顶/列表高亮目标契约、连接重发或推荐 Worker 生命周期后，还要运行定向巡检：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:ui-pinning
```

该巡检会验证 `POST /ui-pinning/target` 携带当前 `businessGeneration`、`Recipe.Id` 与 food ID 分离、主计划目标与页面首项一致、多订单全局目标优先任务计划、非 Active 经营不发布、业务失败退避重试、短暂断线不重复发布、服务端会话或显式连接身份变化后重发、内部签名变化不污染 wire 去重、过期 Worker 结果不会下发，以及源订单组件送达后只保留未送达组件、全部送达或源订单失效后先发布空目标再切换下一订单、Worker error 后空目标锁存只在新的 current 成功 revision 到达后解除。

修改自定义推荐料理总开关、分组、批量状态或排序契约后，运行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:custom-recipes
```

该巡检会验证总开关持久化、草稿跨页签保留、稀客/基础料理分组记忆、页面级/分组级/单条状态更新、同稀客排序、单写者和最小窗口横向溢出。

修改自动化阶段机、料理 job、控制权取消或断线接管协议后，运行：

```bash
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
dotnet run --project tests/automation-cooking-job/AutomationCookingJobSmoke.csproj -c Release
dotnet run --project tests/runtime-cooker-snapshot/RuntimeCookerSnapshotSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-special-business/RuntimeYuumaSpecialBusinessSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-finalization/RuntimeYuumaFinalizationSmoke.csproj -c Release
dotnet run --project tests/yuuma-cooker-topology/YuumaCookerTopologySmoke.csproj -c Release
pnpm audit:automation
pnpm audit:connection-recovery
```

经营生命周期 smoke 直接编译生产 Hook，并验证五个精确边界、倒计时结束后在座服务仍保持 Active、重复 Closing 幂等、Closing 后禁止重新激活和下一场 generation 递增。料理 job smoke 验证 `SetCook` generation、`Extract` / `Store` 内容代际、同代际原生结果替换、所有权丢失后的旧 job 隔离、严格空闲与已完成 `Extract` 残留的厨具选择、厨具暂忙等待、有效停滞时钟和 `StoreFood` 提交后有界复位；job 只保存稳定预约和 pointer，轮询、复位及出锅均从当前物理目录重新绑定，不保留跨帧 `CookController` wrapper。厨具快照 smoke 验证 complete/unavailable 两态、精确空厨具位不计容量且不压缩后续 index、任一条目失败整轮不可用、坐标/原生身份/挑战锁定交叉校验、后端统一自动化可用性分类、有界诊断和内容签名；拓扑 smoke 另验证血池地狱三个公开锁锅/可用性方法的短屏障、完整 Hook、经营代际、revision、规范 SHA-256 租约、永久锁锅后开放厨具继续工作及旧租约失效。自动化审计验证按 index + native identity + grid position 的实体槽位、锁定位置先于 controller 状态读取拒绝、开锅前重验、零合成容量、多类型控制器单次预约、优先保留多类型控制器，以及厨具不可用的队头订单不阻塞其他类型。Yuuma 特殊经营与最终事务 smoke 锁定精确订单形态与强身份、实际成品和双 Tag、原始料理/酒水、目标 policy/revision、精确锅次、料理送达/订单完成双开关门禁、玩家投掷能力不阻断与双 in-air 等待、按 `ManualOrder` 选择精确评价路由，以及不可逆 claim 前 fresh 厨具复核、最终 setter、首次完整订单复核、fresh 同锅复位、availability 前后及 `AfterPlayerExtract` 前重取、允许回调内合法开启下一锅、二次完整订单复核、fresh fulfilled、评价、消费与 Partner 通知的固定顺序；酒水另覆盖专用 fresh lookup、包括 `-1` 的完整原生库存序列、有限库存显示、精确 Tags/FullName/closed generic 参数和普客 Yuuma 终态前置返回。它还禁止恢复旧大范围 coordinator、送餐面板/UI 模拟、生成协程、托盘和 `MoveNext`。其余审计覆盖通用原生 `ServFood` 回执、读取失败有界栅栏、结构化 outcome、阶段计时、command epoch、运行时事件时序、mock 取消/接管、快照恢复、持续退避、连接身份幂等和 lease 会话绑定协议。

修改稀客订单捕获、料理/酒水点单原始 Tag ID、展示文本或稀客自动化匹配后，运行：

```bash
dotnet run --project tests/special-order-runtime-capture/SpecialOrderRuntimeCaptureSmoke.csproj -c Release
dotnet run --project tests/rare-order-identity-matching/RareOrderIdentityMatchingSmoke.csproj -c Release
```

两个 smoke 直接编译生产捕获/匹配代码，验证 `RequestFoodTag` / `RequestBeverageTag` 原始数值身份与 controller 最终展示文本独立保存、`无酒精(-1)` 等合法负数 ID 不会被当作缺失，以及没有稀客 controller 的回调仍能使用订单自身展示文本。普通 `OrderBase` 回调只计入 `notApplicable`，不得污染真正的特殊订单 `parseFailures`。匹配覆盖 nullable 原始身份、映射稀客的独立 `runtimeGuestId`、manager 空扫描下的 controller 所有权、Delivery/Completion/NativeEvaluation 的 fulfilled 差异、幽幽子重修三阶段并发订单隔离及精确缓存清理；`pnpm audit:automation` 另检查 NativeEvaluation 的 capture 优先严格复核、manager 扫描回退、`OrderBase -> SpecialOrder` 规范转换、未送齐等待顺序和剧情版回调对象绑定。订单文本 override 只能改变展示和诊断，不能改变捕获、对象定位或料理 job 去重身份。

修改特殊经营挑战名称来源、目标捕获状态、上下文规则注册表、运行时稀客目录或页面名称 fallback 后，运行：

```bash
pnpm audit:special-business
```

该审计会验证挑战名称使用游戏原生 IL2CPP `InspectorName` 固定中文元数据、永久失败缓存诊断且不重试、瞬时失败按固定间隔持续重试、规则注册表不再保存中文名称映射、名称不可用时页面只显示一次有效 challenge type；同时验证 HUD 目标按 raw challenge owner、target kind、夜间经营 generation 和 inactive 会话边界隔离。血池地狱另锁定六个精确、被动、no-throw 的 `IncomeControllerYuuma` HUD 入口、挑战类型不可读时清空旧目标并 fail-closed、声明为 `OrderBase` 的回调对象只能通过共享 IL2CPP 入口唯一转换成 `NormalOrder` 或 `SpecialOrder`、具体订单成员与 `GuestBase.Id=1003` 强身份、非 BOSS 订单隔离、统一特殊目标 `All` 契约和实际成品 ID/当前策略/Tag 复核。专用结算源码审计锁定双开关、玩家投掷能力不得作为门禁、两类原生 in-air 无副作用等待、`OrderBase.ManualOrder` 精确 bool、同订单手动回调、标准/手动评价分流、最终 setter 与同锅清理、fresh fulfilled、评价后消费/Partner 状态/桌位通知、评价前缓存上下文、评价后不复读 wrapper 和不可逆阶段不重放。此前缺少完整顺序和订单路由复核的通用直评会破坏 P3；只有专用结算可调用对应评价入口，旧大范围结算、送餐面板/UI 模拟与生成协程路径都不得恢复。运行时目录审计继续禁止重新调用未消费且会产生 Warning 的特殊请求语言 getter。

该审计不能替代实机确认。血池地狱实机测试应开启总日志，完整覆盖三个阶段、第二阶段混合订单、第三阶段 `NormalOrder`、多笔并发、`ManualOrder=true/false` 两条评价路由、目标轮换、黑暗料理、真实料理/酒水 in-air、事件锁锅/毁锅、关闭阶段开关、取消和手动接管。确认双开关开启时能够完成送酒、开锅、料理提交、评价和原生状态通知，任一开关关闭时稳定进入人工交接；锁定厨具应退出推荐、预约和高亮，开放厨具继续工作。测试结束后提供完整 `aggregate-mod.log` 和诊断包；只有发生报错、卡死或闪退时再补充 `BepInEx/LogOutput.log` 与 Unity `Player.log`。

修改稀客邀请候选读取、GET/POST 方法边界、日间刷新代际或邀请页面请求生命周期后，运行：

```bash
dotnet run --project tests/rare-guest-invitation-readonly/RareGuestInvitationReadOnlySmoke.csproj -c Release
dotnet run --project tests/local-api-method-matrix/LocalApiMethodMatrixSmoke.csproj -c Release
pnpm audit:rare-guest-invitations
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:rare-guest-invitations:ui
```

只读 smoke 按 BepInEx 783 实际元数据验证同名静态属性、closed generic 具体字典的 `ContainsKey`/indexer 精确查键、错误 key/容器形态拒绝、non-blittable struct boxing、`possibleDestinations` 精确引用数组非空门禁，以及基于现有 `NPC` / `TrackedNPC` / 玩家状态字段的可见性判定。blittable `SchedulerNode.Character` 被装箱后，`characterIdentity` 必须按 exact public declared field 读取并只接受 `Special=0` / `Normal=1`；不得改用属性或 field/property fallback。non-blittable `NPC.Destination` 仍按 Il2CppInterop wrapper 的精确属性读取 `spawnMarker`。`StatusTracker` 和 `DayScene.SceneManager` 只能分别从各自直接泛型基类的精确静态 `Instance` 属性取得，并且 readiness 未通过前不得读取。生产源码扫描同时禁止 NPC 刷新、羁绊生成、全量字典枚举、`GetMapLabelFromSpawnMarker`、`RefNPC`、`TrackedNPC.ShouldShown`、`NPC.Destination.None`、`RuntimeReflectionUtility.GetSingletonInstance`、本地泛型单例扫描和 `FindUnityObject`。同一 C# 审计锁定 `expectedDaySceneGeneration` / `expectedMapLabel` 必填、主线程入口复核以及每次 `RecordInvitedGuest` 前复核；方法矩阵锁定列表 GET-only、单独/批量邀请 POST-only。前端静态审计验证刷新身份和写入上下文包含连接代际、endpoint、范围、日间 generation 和地图，非法日间上下文不会读取或发送写入，API 使用 GET，`runtimeAvailable=false` 或传输失败只按 500/1000/2000/4000ms 有界重试，旧请求由 AbortController/请求 generation 隔离；Playwright 巡检验证默认 `current` 首次自动加载、范围/页签身份变化、瞬时不可用恢复、确定性失败显示、失败写入提示持久化和手动强制刷新，不替代后端写入栅栏 smoke。

修改快照内容签名或 `knownSignature` 协议后，运行：

```bash
dotnet run --project tests/snapshot-signature/SnapshotSignatureSmoke.csproj -c Release
```

该 smoke 使用超过 40 KB 的规范内容验证签名仍固定为 64 字符小写 SHA-256，避免规范原文进入查询串后触发 HTTP 431。

仅重新生成安装包：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\package-release.ps1
```

Linux 或 Git Bash：

```bash
bash mods/bepinex/tools/package-release.sh
```

常见产物：

```text
apps/companion/dist/
apps/companion/src-tauri/target/release/mystia-steward-companion(.exe)
apps/companion/src-tauri/target/release/mystia-steward-companion-updater(.exe)
mods/bepinex/bin/Release/MystiaStewardCompanion.BepInEx.dll
mods/bepinex/dist/mystia-steward-companion-bepinex.zip
mods/bepinex/dist/mystia-steward-companion-companion-windows-x64.exe
mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk
mods/bepinex/dist/mystia-steward-companion-android-armeabi-v7a.apk
```

PowerShell 7 和 bash 脚本都固定生成 canonical Mod 主包 `.zip`；bash 环境缺少 `zip` 时会在触碰现有 `dist` 前失败，不再生成不同格式的 tar 回退。打包脚本会先校验输入，再在 staging 中生成本次完整资产并替换 `dist`，因此正常打包会移除上次残留的 APK、manifest、tar、zip 和旧目录；失败时保留上一套有效 `dist`。若异常终止留下 `dist.staging-*` 或 `dist.backup-*`，下一次打包会停止并列出路径，必须先确认当前 `dist` 后人工恢复或删除，不能继续堆叠事务备份。脚本在检测到 `apps/companion/src-tauri/target/release/mystia-steward-companion(.exe)` 时自动复制到安装包的 `companion/` 子目录，并把 `mystia-steward-companion-updater(.exe)` 放在插件目录根部。检测到 Windows `.exe` 时，还会在 `dist` 根目录复制一份 `mystia-steward-companion-companion-windows-x64.exe`，供其他设备只下载伴随窗口并通过 LAN 连接。Android APK 由 Tauri mobile/Android 工具链单独构建和签名，打包脚本不会从 Windows EXE 派生 APK。Windows 下该 updater 会显示独立更新窗口，负责提示关闭游戏、展示阶段进度并在游戏退出后替换插件目录。

## 本地发布

本地发布方案见仓库根目录的 `docs/local-release.md`。仓库不使用 GitHub Actions 自动构建 Release；版本发布需要在 Windows 本机构建完整产物后通过 GitHub CLI 上传。

GitHub Release 上传以下资产：

- `mystia-steward-companion-bepinex.zip`
- `update-manifest.json`
- `mystia-steward-companion-companion-windows-x64.exe`
- 可选：`mystia-steward-companion-android-arm64-v8a.apk`
- 可选：`mystia-steward-companion-android-armeabi-v7a.apk`

`update-manifest.json` 给 Mod 内置自动更新使用，只包含版本、资产文件名、zip 大小和 SHA256，不记录本机打包路径，并且只指向 `mystia-steward-companion-bepinex.zip`。Mod 严格接受 schema 1，并校验 version/tag/channel、资产名、包长度和 SHA256；缓存候选也必须重新满足同一组约束。下载正文使用五分钟取消令牌并按声明长度边读边限制，先写同目录临时目录，完整校验后再替换正式 staged 目录。检查、下载和安装同一时刻只运行一项；updater 存活时拒绝覆盖 staged 目录或重复启动。安装只能使用已校验 staged 包内的 updater，且 staged 版本必须高于当前插件版本，禁止旧暂存包降级。独立 Windows 伴随窗口 EXE 和 Android APK 只给 B 设备跨局域网连接使用，不参与 Mod 自动更新。不上传 Tauri setup 安装器，避免用户误以为只安装桌面程序即可使用 Mod。

发布前检查：

- `gh auth status` 能正常显示已登录账号。
- `mods\bepinex\References` 中 8 个编译引用 DLL 齐全。
- 已运行 `mods\bepinex\tools\set-version.ps1` 并提交版本号变更。
- 用户可见功能和开发约束已同步到 README 或 `docs/`。
- 若发布新版本，先提交版本号变更并创建或移动对应 tag，例如 `v1.1.0` 或 `v1.1.0-preview.1`。

Release Note 只写从上一个版本到当前版本新增的用户可见功能、优化和 BUG 修复。内部重构、文档、构建脚本、版本号变更不写入 Note；如果某个优化或修复只是本版本新增功能的二次调整，不单独列出，只在新增功能描述中体现最终能力。

### 同步版本号

以稳定版 `1.1.0` 为例：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\set-version.ps1 -Version 1.1.0

git add package.json apps\companion\src-tauri\Cargo.toml apps\companion\src-tauri\Cargo.lock apps\companion\src-tauri\tauri.conf.json mods\bepinex\src\Plugin\MystiaStewardCompanionPlugin.cs
git commit -m "chore(release): bump version to 1.1.0"
git push origin dev
```

版本号变更先进入 `dev`；确认稳定版可发布后，再合并到 `main`，并在 `main` 上执行发布脚本。预览版使用 `X.Y.Z-preview.N`，只用于自动更新测试，通常不合并 `main`。

Linux 开发环境可使用：

```bash
bash mods/bepinex/tools/set-version.sh 1.1.0
```

发布脚本会根据 `-Tag` 校验 `package.json`、`tauri.conf.json`、`Cargo.toml`、`Cargo.lock` 和 `PluginVersion`。如果版本不一致，脚本会失败并提示先同步版本。自动更新发布只支持稳定版 `X.Y.Z` 和预览版 `X.Y.Z-preview.N`；预览版必须加 `-Prerelease` 或 `-Preview`，稳定版不能加。

### 发布预览版

预览版用于验证 Mod 内置自动更新链路。示例流程：

```text
v1.1.0-preview.1 -> v1.1.0-preview.2 -> v1.1.0
```

在 `dev` 上同步预览版本、提交、推送并创建 tag：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\set-version.ps1 -Version 1.1.0-preview.1
git add package.json apps\companion\src-tauri\Cargo.toml apps\companion\src-tauri\Cargo.lock apps\companion\src-tauri\tauri.conf.json mods\bepinex\src\Plugin\MystiaStewardCompanionPlugin.cs
git commit -m "chore(release): bump version to 1.1.0-preview.1"
git push origin dev

git tag -a v1.1.0-preview.1 -m "v1.1.0-preview.1"
git push origin v1.1.0-preview.1
```

发布 GitHub Prerelease：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0-preview.1 `
  -Title "v1.1.0-preview.1" `
  -Notes "预览版更新测试说明" `
  -Prerelease
```

测试者需要在 `BepInEx/config/com.tyukki.mystia-steward-companion.cfg` 中设置：

```ini
[Updates]
IncludePrerelease = true
```

默认配置不会检查预览版。测试通过后，同步 `1.1.0` 并按稳定版流程合并 `main`、发布普通 Release。

### 发布新版本

以稳定版 `v1.1.0` 为例：

```powershell
git checkout main
git pull --ff-only origin main
git fetch --tags --force origin

pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -Title "v1.1.0" `
  -Notes "版本更新说明"
```

脚本会先执行完整构建，再用 `gh release create` 创建 Release 并上传 Mod zip、独立伴随窗口 EXE 与 update-manifest。

如果引用 DLL 不在 `mods\bepinex\References`，传入 `-ReferenceDir`：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -Title "v1.1.0" `
  -Notes "版本更新说明" `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

### 更新已有版本资产

如果只需要修改已有 Release 的标题或发布说明，不需要重新构建：

```powershell
gh release edit v1.0.0 `
  --repo blockshy/mystia-steward-companion `
  --title "v1.0.0" `
  --notes "修正后的发布说明"
```

如果 Release 已存在，只想替换同名 zip 和 update-manifest，使用 `-Clobber`：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.0 `
  -Title "v1.0.0" `
  -Notes "首个正式版本" `
  -Clobber
```

如果已经运行过 `build-release.ps1`，只重新上传已有产物：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.0 `
  -SkipBuild `
  -Clobber
```

### 版本 tag

发布脚本不会自动创建或移动 Git tag。新版本发布前应显式处理 tag：

```powershell
git tag -a v1.1.0 -m "v1.1.0"
git push origin v1.1.0
```

如果需要修正尚未正式发布的 tag 指向：

```powershell
git tag -f -a v1.1.0 -m "v1.1.0"
git push --force origin v1.1.0
```

## 运行时数据源

推荐、库存名称和自动化目标解析使用游戏运行时读取到的 `RuntimeDataCatalog`。伴随窗口未连接游戏、游戏数据库未初始化或 `/snapshot` 返回的 `runtimeDataComplete=false` 时，页面会显示等待运行时数据。

发布包包含 Mod DLL 和伴随窗口程序，推荐、库存和自动化目标都来自游戏当前运行时。

## 运行时刷新行为

Mod 会定期检查当前页面和游戏运行时状态。进入游戏并加载进度后，推荐状态来自当前内存中的运行时对象，不读取 `.memory` 存档文件。

运行时固定数据读取成功后，C# 会把 `DataBaseCore`、`DataBaseCharacter` 和 `DataBaseLanguage` 中的料理、食材、酒水、普客、稀客和 tag 映射构造成 `RuntimeDataCatalog`，发布到独立的 `/runtime-data` 缓存与端点，并切换 C# 推荐仓库到运行时仓库。核心目录 ID 只枚举 `DataBaseCore.IngredientsMapping`、`BeveragesMapping`、`FoodsMapping`、`RecipesMapping` 和 `IzakayasMapping` 五张精确 `Dictionary<int,string>`。所有 mapping 条目先严格验证 CLR `Int32` 键、非空 CLR `String` 值、原始 ID 唯一性和容量；材料、酒水、料理和配方显式使用非负内容 ID 域，负数内部键在核心业务投影边界排除且不会调用对应 `Ref*`，排除后没有非负 ID 时整轮读取失败。`IzakayasMapping` 显式保留完整 signed ID，再逐项调用 `RefIzakaya`；允许 signed ID 的料理/酒水 Tag 字典也保持独立规则。Izakaya 条目先精确读取 `DaySceneMapLabel`，只有空标签占位允许跳过；非空标签必须严格读取原生 `DaySceneMapName`，读取失败令整轮失败，合法但不属于支持日间经营地点的条目才记录 skipped。确认支持地点后再严格读取普通/稀客池，不读取特殊经营和占位条目中与推荐无关的合法空池。基础稀客从 `GetAllSpecialGuests()` 的精确引用数组读取，喜好、厌恶和酒水 Tag 只取声明的原始字段，不调用生成或计算型入口。核心目录与基础/映射稀客 identity 快照独立记录完成状态；二者均完整前不构造推荐状态 provider。普通地图或就绪变化复用已完成的静态身份，只让未完成读取立即重试。进入主菜单等非游戏场景清空存档运行态后，identity 会独立重建，不能被仍完整的核心目录短路。伴随窗口概览页的“推荐数据”显示“游戏运行时”时，表示前端推荐算法已经获得完整运行时数据。

前端从同一份完整稀客原始目录构造两个互不混用的投影：`rareCustomers` 只包含有合法日间地点、可在普通页面选择的稀客；`rareCustomerProfiles` 只携带特殊经营评价所需的 canonical ID、名称与喜好/厌恶 Tag，即使原生 `places=[]` 也保留。幽幽子重修等规则严格按已验证的基础 character ID 读取评价档案，不按映射身份、中文名或内置 Tag 回退。两份投影都属于推荐数据签名，worker 和 NormalOrder 特殊目标缓存不能跨签名复用结果。

运行态读取不依赖固定秒数等待。`DaySceneSustainedPannel.OnPannelPostOpen` 只表示日间面板已出现，是独立最终门闩而不是 ready 信号；manager/Action 链可在面板前后捕获。普通读档必须从 `DayScene.SceneManager.OnFirstEnterDaySceneFinish` 捕获同一 manager 的 `RunTimeScheduler.OnEnterDayScene` 外层 Action，等该 Action 进入匹配的 `DefaultOnFinish` 后再捕获 `OnEnterDaySceneMap` 最终 Action，并只在最终 Action 返回且面板已打开后解锁。手动经营返回只接受入口前明确读取到的 `NightSceneDirector.IsManualWorkSceneSession` 分支。每次读取仍要求同一原生 manager、`IsMapSwapping=false`、`m_HasTriggerOnEnterDaySceneEvent=false`、`RunTimeScheduler.isExecuting=false`、`SceneDirector.IsInEvent=false`、manager 的 `isExecutingScheduledActions=false`、`UniversalGameManager.IsSwitchScene=false` 且当前地图 label 有效；任一 Hook 或字段不可验证时保持 fail-closed。夜间经营准备读取要求 `PrepNightScene.UI.IzakayaConfigPannel.OnPanelOpen` / `GoToSpecific` 已触发，且 `WorkPrepScenePannelRoot` 下的 `IzakayaConfigPannelNew` 仍激活。准备阶段只读取库存、已解锁、流行 Tag 等基础玩家运行态，因此 `修改`、`普客` 和 `稀客` 页面可以提前使用；当前日间地图和稀客邀请仍只在完成上述解锁的日间场景读取。

推荐状态以完整运行时静态目录的 ID 闭包为边界：料理逐 ID 调用 `RunTimeStorage.HaveRecipe(int)`，材料逐 ID 调用 `GetIngredientCountById(int)`，酒水逐 ID 调用 `GetBeverageCountById(int)`，不生成存储快照或解析其 IL2CPP 容器。材料和酒水数量都保留精确 `-1` 作为无限，`0` 不发布，低于 `-1` 失败。玩家等级、流行喜好/厌恶 Tag 和明星店开关继续使用轻量 getter 读取；没有任何已解锁料理时等待下一轮，不发布空的可用料理集合。

为降低经营中掉帧风险，本地 API 快照发布会做轻量节流：Unity 主线程最多约每 0.35 秒刷新一次缓存 JSON；若快照内容签名未变化，会复用上一份缓存 JSON，不为了 `CapturedAtUtc` 或性能数字重复序列化。完整 `RuntimeDataCatalog` 不再放进 `/snapshot`；快照只发布目录是否完整、来源、状态和签名，伴随窗口仅在本地缓存为空或签名变化时通过 `/runtime-data` 读取完整目录。`runtimeDataComplete/runtimeDataStatus` 使用 core+identity 组合状态；core JSON 可以提前序列化，但 identity 未完成时前端不会消费。运行时固定数据已经完整读取后，会缓存稀客映射和静态目录快照，经营 provider 与经营诊断只消费缓存，不再从经营快照热路径反复触发静态数据扫描；读取未完整时只由控制器按约 5 秒间隔重试，目录对象不叠加第二套时钟。目录或 identity 未完整时直接发布带阶段的精确状态，不再继续调用 provider 产生泛化的“目录不完整”异常。伴随窗口按签名缓存最近一次完整运行时数据，不能把 `/runtime-data` 的临时读取失败当作主快照丢失；未完整占位则必须随新快照更新，不能锁存首次等待文本。总日志的 `[snapshot]` 段包含 `runtimeSceneReadiness`、`runtimeDataComplete`、`runtimeDataSource` 和 `runtimeDataStatus`，可直接定位日间目录失败阶段。概览页和经营中页会显示 `performanceMs` 中最近约 12 秒内耗时最高的快照环节，排查卡顿时优先记录 `refresh.business`、`refresh.runtime`、`snapshot.serialize`、`runtimeData.serialize`、`automation.collect` 和 `snapshot.publish`。经营扫描还会细分 `business.rare.*`、`business.normal.*` 和 `runtime.cookerSnapshot` 等子项；普客订单快照会在短时间内复用，避免同一轮 `/snapshot` 发布重复枚举 `OrderController`、HUD 和 `GuestsManager`。

夜间经营运行时由 `RuntimeNightBusinessLifecycle` 的精确 generation 管理。只有 `WorkSceneSustainedPannel.OnPannelPostOpen`、`GuestsManager.CloseIzakayaDelayed`、`CloseIzakayaAndLeaveChallengeMode`、`NightScene.SceneManager.ToResult` 和 `OnInstanceDestroyed` 五个 Hook 全部成功后才允许进入 Active；任一成员缺失时保持 fail-closed。`TryCloseIzakaya` 只在倒计时结束后停止接客、遣散排队顾客并等待在座顾客完成服务，不得进入 Closing；清桌期间保持同一 Active generation，继续处理订单、自动化和界面目标。最后一桌离席后创建 `CloseIzakayaDelayed`，或特殊经营退出/结果转换时，才进入 Closing，同步停止运行时访问、失效界面目标并清理稀客/普客捕获、特殊经营上下文、料理 generation 和自动料理 job；`OnInstanceDestroyed` 进入 Destroyed。Closing 后不能重新 Active，只有 Destroyed 后下一次工作面板打开才能递增 generation。

普客订单被动快照缓存约 1 秒，且只在当前为 Work 场景并处于 Active generation 时读取。`NormalOrderRuntimeCapture` 通过 `GuestGroupController.PushToOrder` 和 `GuestsManager.SetManualControllerOrderInternal` 捕获订单与可执行控制器绑定；常规快照先读取 live `OrderController` / HUD，再合并仍匹配订单 key 或桌位/料理/酒水槽位的捕获缓存。手动控制订单还需枚举 `NightSceneDirector.controlledGuest`。已不可见缓存必须剔除，捕获不可用时才做 `GuestsManager` 启动扫描；捕获版本变化只刷新普客订单快照。没有活动 `AutomationCookingJob` 时不在 `Update()` 热路径轮询料理 job。

游戏界面置顶不反射读写 UI 列表；`UpdateAllVisual` 与 `UpdateBevField` 只建立 ThreadStatic 刷新作用域，最外层 prefix 固定目标快照。`RunTimePlayerData.CheckPinned` 的 bool prefix 只为精确目标设置 `__result=true` 并跳过原方法，非目标或作用域外完整执行游戏逻辑。目标内容代际变化且对应列表已经打开时，只能按代际去重后在 Unity 主线程执行一次安全刷新。Harmony 可以只读观察精确 `OnPanelOpen` / close / destroyed 生命周期来登记或清理面板实例；不得主动调用或重调 `OnPanelOpen` 触发刷新，也不得扫描场景/面板或轮询。不得压制玩家收藏，cooker 类型 `3` 只由独立高亮服务处理，也不得恢复列表改写路径。

`RuntimePinnedListHighlightService` 只 Hook `WorkSceneCookingSelectionPannel.OnRecipeElementEnabled/3`、`OnIngElementEnabled/3` 和 `WorkSceneStoragePannel.OnElementEnabled/3`，酒水还必须确认 `Sellable.Type=Beverage`。`RuntimeUiPinningService` 维护排序与列表高亮共用的唯一 immutable target，并要求发布携带当前 Active 的经营 generation；不得在视觉服务中再保存第二份业务目标。Local API 工作线程只能发布 immutable desired state，不得读取或写入 Unity 对象；列表 Image 和厨具 renderer 的着色、恢复及目标协调只能在元素回调、`Tick` 或 `LateUpdate` 主线程执行。进入 Closing 时，在对象仍有效的主线程边界恢复原色并挂起视觉服务；进入 Destroyed 后只丢弃 wrapper 引用，不再调用 Unity setter。新的 Active generation 才能恢复服务，网络重发不能解除挂起或复用上一场目标。

夜间 live 厨具控制器只从 `CookSystemManager.get_AllCookers` 返回的精确 `Dictionary<Vector3Int,CookController>` 读取，具体字典通过 `RuntimeConcreteCollectionReader.TryReadDictionary` 枚举；开锅选择、已摆放厨具快照和厨具高亮共用该读取结果。每个控制器的完整能力读取 `Cooker.AllAvailableCookerType`：按 BepInEx 783 的精确 `IEnumerable<CookerType>` 形态使用泛型 `Current`、非泛型 `MoveNext` 和 `IDisposable.Dispose`，不能在读到基础 `Cooker.Type` 后提前返回。`AllCookers` 的固定空位只有在 `CookController.IsEmptyDesk=true`、类型序列为 Empty-only、phase 空闲且无结果/配方时才成立；非空控制器必须至少有一个 1-5 能力。不得恢复 `AllCookerControllers` 的通用对象枚举、字典内部 slots 读取或 Unity 场景扫描。

物理厨具快照只允许 complete 和 unavailable。每轮先读精确 `EventManager.get_LockedCookers()` 坐标，再将该集合传入 `get_AllCookers` 读取。锁定 key 只校验字典坐标、native pointer 和唯一性后计入 `placedCookerLockedControllerCount`，不调用可能失效的 controller `get_GridPosition()`、`CouldOpen`、内容、能力或渲染器 getter，也不发布为 `placedCookers`、类型或容量。非锁定条目仍必须精确校验字典坐标与 controller 坐标一致、native pointer/坐标唯一且 `CouldOpen=true`。精确空位计入 `placedCookerEmptyControllerCount`，不发布为容量。complete 响应要求 `placedCookers + emptyControllers + lockedControllers == controllerCount` 且失败为零；unavailable 响应要求 placed/type/empty 为空且 `lockedControllers + readFailures == controllerCount`，锁定计数只作诊断。unavailable 不参与展示推荐的厨具过滤或类型推断，但自动化容量、写操作与高亮整轮 fail-closed。complete 且 locked 计数大于零时，只有存活非锁定条目证明的类型保持可用，其他类型作为事件期间独立硬门禁。Local API 和前端必须严格校验两类计数闭合，只对已发布的非锁定条目校验三重身份、`challengeLocked=false`、`couldOpen=true`、类型投影和自动化分类。

快照与实际开锅选择必须共用 `RuntimeCookerStartAvailabilityService` 的完整分类，不能把 `CouldOpen` 单独解释为自动化可用。只有严格空闲，或原生 `Extract` 已完成且所有权证据完整的配方残留控制器，才能发布 `automationAvailable=true`；忙碌、锁定、未完成 mutation 或残留所有权不可读均为 unavailable。前端按 `controllerIndex + controllerIdentity + gridPosition` 构建实体槽位池并与普客、稀客共用；多类型控制器同一轮只能预约一次，优先选择支持类型更少的控制器以保留多类型槽位。需要开锅的写请求必须同时传递本轮预约的 `cookerControllerIndex`、规范原生 identity 和三维 grid；后端 fresh-read `AllCookers` 与 `LockedCookers` 后复核三者、类型、Mod 预约、共享可用性分类和 `CouldOpen`/lock 一致性，并在紧邻 `SetCook` 前再次执行相同复核。任何漂移都进入局部等待，不扫描或改选其他控制器。不开锅请求不携带预约且必须明确关闭开锅阶段。需要开锅的候选必须在厨具准入后才消耗并发，队头订单缺少可用类型时继续扫描后续候选，不能阻塞其他可执行类型；不得再按类型计数制造最小容量或把未读控制器推测为槽位。厨具已存在但被 Mod 预约、游戏占用或特殊经营暂时禁用时，开锅入口返回可重试等待并分别记录数量，不把它误报为“没有读取到任何厨具”。

夜间经营中，`经营中 / Service` 页优先使用 `SpecialOrderRuntimeCapture` 捕获到的稀客订单缓存；捕获缓存为空、需要初始化校验或本轮可接受订单少于缓存数量时，再扫描 `GuestsManager`、稀客队列、`OrderController`、HUD、服务面板和桌位控制器补充业务缺失项。诊断开启时允许额外采样这些来源，但样本只写日志，不合入正式订单集合。捕获版本、场景和诊断状态都未变化时会复用已有经营上下文，并按较慢节奏重新校验。页面仍会读取桌位控制器中的活动稀客，用于显示当前稀客和 `GuestGroupController.GetFund`、`BaseFundCarry`、`MaxFundCarry` 等当前携带金钱信息。普客订单使用 `OrderController` / HUD 判断订单可见性，使用 `NormalOrderRuntimeCapture` 记录 `GuestGroupController.PushToOrder` 或 `SetManualControllerOrderInternal` 建立的订单归属；HUD / `OrderController` 单独存在时只能显示推荐和不可执行诊断，不能单独用于自动送达或评价。页面顶部只展示经营场景、扫描状态、推荐数据、厨具与置顶状态等通用信息，随后用 `稀客` / `普客` 页签分区展示各自功能。稀客点单后，工作台会按桌号列出稀客、料理词条和酒水词条，并复用稀客推荐算法计算候选料理、加料和酒水。普客订单读取到 `GameData.CoreLanguage.LanguageBase` 这类 IL2CPP 本地化对象时，必须过滤为无文本，不得把运行时类型名当作客人、料理或酒水名称展示。稀客自动送达会再次验证捕获订单对象仍由同一 controller 持有、尚未满足且强身份完全一致；因此经营末尾 `GuestsManager` 集合暂时无法枚举时，仍可使用已经通过这些校验的捕获对象，不把 manager 空集合误判为订单消失。完成定位允许 `IsFullfilled=true`，因为该字段只表示料理和酒水均已送达，不表示订单已经评价或移除；进入原生评价查找前必须先用该精确完成对象检查 fulfilled，未送齐只返回等待，不得把 manager 扫描失败当作订单错误。

若 IL2CPP getter 无法读取订单列表，Mod 会通过 IL2CPP enumerator 读取 `AllOrders` / `AllOrdersData`，并以 `PeekOrders()` 补充栈顶。这些入口声明为 `OrderBase`，读取特殊订单身份前必须统一通过 IL2CPP `TryCast<SpecialOrder>()` 得到具体包装；捕获、Provider 和自动化实时扫描不得各自维护不同的转换实现。稀客订单身份必须读取 `SpecialOrder.RequestFoodTag` / `RequestBeverageTag` 的原始数值，并与桌位、运行时原始稀客 ID 一起用于捕获路径和实时扫描路径的同一套强匹配；快照 `guestId` 保留归一化目录 ID 给推荐与特殊经营规则，独立 nullable `runtimeGuestId` 才参与对象定位。`-1` 等合法负数必须原样保留，读取失败用 nullable / `Has*TagId=false` 显式表达，不能用负数哨兵冒充缺失。`SpecialGuestsController.GetOrderFoodText(...)` / `GetOrderBevText(...)` 会应用订单文本 override，其原文保留在捕获诊断；页面优先使用原始 ID 对应的规范 Tag 名，ID 没有目录映射时才把捕获文本规范化后用于显示。所有文本都绝不参与订单匹配。不得用基类 `foodRequest` / `beverageRequest` 猜测身份，任一必要身份缺失时自动化必须 fail-closed。

幽幽子第三阶段的 NativeEvaluation 先复用精确捕获的 order/controller，并在调用原生评价前重新校验强身份、controller 当前所有权、fulfilled、已送达目标与对应评价回调；只有 capture 失效或校验不通过时，才扫描 `GuestsManager` 的当前集合并执行同一验证器。剧情版的 `onEvaluate` 必须来自最终选中的同一原生 order/controller 捕获记录，不能只按相同请求身份借用其他记录的回调；重修版仍要求 `_50` / `_70` 回调。重修门禁还必须按实际订单形态分流：稀客 `SpecialOrder` 校验请求料理/酒水 Tag 存在于实送完整 `Tags`，按标准点单喜恶评价，不读取 `expectedFoodModifierTags`；精确料理/酒水 `NormalOrder` 才校验实际加料和 `Tags.Except(RawTags)` modifier Tag。不得用字段为空、预测 Tag 或等级合计规则在两种形态之间兜底。古明地恋本体继续要求 manager 可发现的 live controller，不使用本段 capture 优先策略。

稀客推荐结果会按角色、点单词条、库存状态、厨具快照、排序配置、置顶开关、同基础料理展示数量、自动化收藏限定和加料上限缓存。经营中先对完整候选应用硬过滤，再依次应用任务料理置顶、自定义置顶、收藏料理/酒水置顶和普通权重，构造执行计划；`executionPlans[0]` 是订单唯一主执行计划，再把该计划的料理和酒水投影为页面首项。exact active ServeInWork 任务料理可以跳过普通 food Tag 与料理厌恶判断，但配对酒水和全部硬门禁仍须通过。同基础料理展示数量只裁剪其余行。自动化开始时、游戏界面置顶、列表高亮和厨具高亮只消费同一主计划，不得扫描后续计划；普通经营多订单全局界面目标优先选择主计划为任务计划的订单。收藏限定只在自动化总开关、对应料理/酒水阶段和当前订单自动化权限都开启时参与主计划归一化，并且必须在执行计划数量截断前处理；找不到满足限定的方案时保留推荐展示，但对应自动化动作不执行。自动化锁定后，即使开锅或送酒引起库存重算并改变页面主计划，也继续处理原锁定目标。自动刷新没有检测到算法相关变化时，不会在每个刷新周期重复枚举加料组合；`lastSeenAtUtc`、诊断来源和显示标签不得进入推荐语义签名。页面另用连接代际、自动化会话、经营 generation/lifecycle、特殊经营语义和数据签名建立硬上下文，同上下文内按订单强身份保留上一轮展示，新订单局部显示 pending，送达/需求/上下文变化立即失效；该投影只供渲染，动作路径仍只使用 Worker current 结果。所有影响主计划的设置都必须进入缓存签名。只有最终没有执行计划时才生成 `blockedDiagnostic`，并复用正式候选管线记录料理、酒水、预算和特殊评价各阶段的首个清零位置及资源证据；该诊断不参与业务缓存身份、候选选择或排序。幽幽子二阶段没有可预测 `ExGood` 的完整组合时保持无计划，不得降级执行。

收藏数据由 Mod 本地 API 持久化到 `BepInEx/config/MystiaStewardCompanion/favorites.json`。前端只通过 `/favorites`、`/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` 读写，不使用 localStorage 存储收藏，避免版本更新或 WebView 数据迁移时丢失。

如果没有检测到运行时数据，普客和稀客推荐页只显示运行时数据不可用，不会回退到“全内容可用”状态，避免误以为库存和解锁内容已经同步。

开启总日志后，经营扫描会额外输出 `night-business` section，其中包含 `Candidates` 和 `RecentRuntimeParseFailures`。前者记录被扫描到的 controller/order 候选、接纳状态和过滤原因；后者记录运行时订单捕获器最近未能解析为稀客订单的样本。排查映射稀客或特殊事件稀客时，优先查看这两段。

诊断采样不得改变正式业务输入。有 runtime capture 时，推荐和自动化始终使用捕获订单及既有缺失项反射补充；为完整诊断额外枚举到的 HUD/controller 订单只写入 `night-business` section，不合入最终订单集合。

总日志还会输出运行时固定数据快照：

- `runtime-static-data`：`DataBaseCharacter.GetAllMappedGuests()` 的 `ID` / `StrID` / `SourceGuestID` 原始映射，以及 `GetAllSpecialGuests()` 的基础 `id` / `stringId`。两者都按 Assembly-CSharp 声明的精确引用数组通过 Length/indexer 读取；映射项只沿已验证的 source ID 链归一到基础稀客，不生成临时稀客、不按名称猜测，也不调用喜好/厌恶计算入口。身份快照保留全部基础与映射原生身份，即使某项不属于当前可推荐目录也不会从身份域删除；日志中的 `aliasSource` 会区分直接来源与 source chain。
- `runtime-tags`：`DataBaseLanguage` 的料理/酒水标签文本与 DLC 标签映射；运行时目录不投影 Tag 压制规则，伴随窗口使用项目已验证的固定规则。
- `runtime-database`：`DataBaseCore` 五张精确 int/string Mapping 提供目录 ID，再逐 ID 调用对应 `Ref*` 得到的食材、酒水、菜品和料理运行时表。
- `runtime-guests`：`DataBaseCharacter` 普客与基础稀客的名称、地点和原始喜好/厌恶 Tag；映射稀客身份单独记录在 `runtime-static-data`。
- `runtime-izakayas`：`DataBaseCore.IzakayasMapping` 提供场景 ID，再逐 ID 调用 `RefIzakaya` 读取的经营场景标签与普通/稀客池。

固定数据快照由基础运行时目录刷新路径读取并缓存；核心目录与基础/映射稀客 identity 使用独立完成状态和重试路径。普通地图或就绪变化复用完整静态身份，未完成读取可立即重试；进入非游戏场景清空存档运行态后会使 identity 失效并独立重建，不能因核心目录已完整而跳过。总日志开启且 `NightBusinessReflectionProvider.LoadContext()` 被调用时，经营诊断只把已缓存的目录快照写入总日志，不会在经营热路径重新扫描 `DataBaseCore`、`DataBaseLanguage` 或 `DataBaseCharacter`。若游戏数据库尚未初始化，目录刷新会按 5 秒间隔重试。判断读取成功时优先看 section 里的 `Complete: True` 和 `Status` 中各类计数是否大于 0。

## 本地 API 与伴随窗口

Mod 默认监听：

```text
http://127.0.0.1:32145
```

本机回环 listener 始终启用。需要让 B 设备连接 A 设备上的游戏时，优先在 A 设备本机伴随窗口 `设置 -> 连接` 开启 `允许局域网设备连接`；该开关只会额外启用 LAN listener，不会关闭 `127.0.0.1` 恢复入口。对应配置为：

```ini
[LocalApi]
AllowLanConnections = true
LanHost = auto
Port = 32145
```

`LanHost=auto` 会按默认网关、接口类型和 link-local 状态排列检测到的私网 IPv4，同时监听全部合格候选；也可以写 A 设备的具体局域网 IPv4。结构化 endpoint 明细只通过回环限定的连接配置端点返回，`/health` 不暴露本机网卡名称。LAN 通道仍要求除 `/health` 外的所有端点携带 `X-Mystia-Steward-Companion-Token`，服务端会拒绝非私网来源；连接配置和 Token 重置端点只允许 A 设备本机回环客户端调用。用户仍可能需要在 Windows 防火墙中允许游戏 EXE 的该端口入站。不要把该端口通过公网端口映射暴露出去。

每个 loopback/LAN listener 都必须拥有独立停止状态和 worker 线程。动态应用 LAN 配置时先比较规范化配置和目标地址集合；无变化直接返回，有变化时串行停止并有界等待旧 worker，再启动新地址。主动停止引发的 accept 异常直接结束 worker；未预期 accept 异常最多记录一次并终止对应 listener，不得无延迟无限重试。

客户端 handler 使用有界调度器限制并发；服务停止时先拒绝新连接、关闭在途 socket，再有界等待 handler 退出，资源释放异常也必须归还 handler 槽位。HTTP 请求头必须在 32 KiB 内完整出现 `CRLFCRLF`，EOF 截断返回 400，超限返回 431；业务异常返回结构化 500，不能只断开连接。需要访问 IL2CPP 的库存、订单和稀客邀请命令继续回到 Unity 主线程，但每类队列有容量上限且每帧最多执行一个有效命令：尚未开始的命令超时后会被取消，主线程恢复后不得晚到执行；已经开始的命令等待确定结果，避免客户端在副作用已发生时重试。控制器销毁时先取消并唤醒排队命令，再等待 API handler。

正式 Tauri 客户端的 Rust TCP 代理必须分别限制连接和响应读取：连接超时最长 5 秒，读取超时按前端命令要求最多允许 60 秒。不能用连接超时上限截断更新下载等长耗时请求，否则客户端会先报失败而 Mod 侧仍可能继续处理。

端点：

路由只接受下列规范路径。根路径 `/` 不代表健康检查或快照，`/api/*` 也不是这些路由的别名；不存在的路径/方法组合返回 404，GET、POST、OPTIONS 之外的方法返回 405。只读查询使用 `GET`；任何会写文件、修改运行时状态、更新服务配置、获取或释放控制权、创建诊断包、打开本机目录、访问网络更新状态或启动进程的操作都使用 `POST`。当前写操作仍使用 URL query 传递参数，尚未定义 JSON request body 契约。

- `GET /health`：检查本地 API 是否启动，不需要 token。
- `GET /local-api/config`：读取本机 endpoint、LAN listener 状态、结构化 LAN endpoint 候选和当前 Token；每个候选包含地址、接口、默认网关、link-local 和推荐状态，只允许回环客户端调用。
- `POST /local-api/config?lanEnabled=true|false&lanHost=auto|IPv4`：由 A 设备本机伴随窗口保存 LAN 开关和监听地址，并动态启停 LAN listener；本机回环 listener 不重启也不关闭。
- `POST /local-api/token/regenerate`：重置本地 API Token，返回新 Token 并立即更新当前 API 鉴权；只允许回环客户端调用。
- `GET /snapshot?knownSignature=...`：读取最新运行态快照。快照由 Unity 主线程按自动刷新节奏生成，网络线程只返回缓存 JSON；内容签名固定为规范内容的 64 字符小写 SHA-256，不能把随订单增长的规范原文放进查询串。签名未变化时返回轻量 unchanged 响应。快照包含推荐状态、稀客/普客订单、活动 `automationCookingJobs`、递增序号 `automationEvents`、运行时目录元信息、日间 `runtimeDaySceneReady` / `runtimeDaySceneGeneration` 和 `performanceMs`，不包含完整 `RuntimeDataCatalog`，也不包含旧的 `runtimeRareCustomers` 合成目录；可推荐稀客只来自 `/runtime-data.rareCustomers`。
- `GET /runtime-data`：读取当前完整 `RuntimeDataCatalog`。`runtimeDataSignature` 是完整响应 JSON 的固定 SHA-256，而不是集合计数；伴随窗口只在本地没有目录缓存或该签名变化时调用。
- `GET /missions/tracked?knownSignature=...`：读取当前 generation 中 active-only 的已追踪任务。响应投影稳定的任务 label、游戏当前语言标题、`unverified|tracking|fulfilled` 三态、条件总数和 nullable 条件状态，并为每项携带原始 receiver、游戏语言角色名、静态相关场景和展示读取状态；状态来自读档初始化或新任务创建边界的一次受控原生刷新，以及之后游戏自身的自然刷新。任一 active 任务定义、标题或条件形态不完整时整轮 `runtimeAvailable=false`，但角色或场景展示失败只清空未确认字段，不得使任务业务状态不可用。完整响应使用规范内容 SHA-256；签名未变化时只返回 `unchanged + contentSignature`。该端点自身不进入 Unity 主线程、不发布未接取任务，也不调用任务状态或写入方法。
- `GET /missions/available?knownSignature=...`：每个请求排队到 Unity 主线程执行 fresh read，只发布实机闭环验证的 type 5 `KizunaCheckPoint` 且来源为 `postMissionsAfterPerformance` 的可接取任务。读取严格复核 `preNodes`、active labels、`loopedMission`、finished history、任务 generation、日间 generation 和 mapped identity snapshot；任务展示元数据使用同一 receiver 和静态相关场景协议，相关场景不表示角色实时位置。`knownSignature` 只压缩响应，不能省略 fresh read。端点不复用 frozen scheduled 诊断，不调用事件触发、任务接取或其他推进方法。
- `GET /automation/lease`：读取当前自动化控制权状态。
- `GET /logs/settings`：读取总日志开关、总日志路径、单文件分片大小、文件上限、总容量上限，以及 BepInEx 控制台的平台支持、启动设置、活动和可见状态。
- `POST /logs/config?aggregateLog=true|false&aggregateLogMaxFiles=30`：由伴随窗口回写总日志开关和文件上限；`aggregateLog` 会即时注册或移除 BepInEx 全局日志监听器。
- `POST /logs/console?visible=true|false`：仅允许游戏电脑回环客户端调用。显示时使用 BepInEx #783 `ConsoleManager.CreateConsole()`，以调用前后 `GetConsoleWindow()` 是否从空变为非空判定 Mod 本次新建窗口，并仅对该窗口删除 `SC_CLOSE`、补充唯一 `ConsoleLogListener`；driver active、窗口存在和真实可见性分别读取，隐藏及失败回滚只按真实窗口可见性调用 Win32 `ShowWindow(SW_HIDE)`。显隐、`Diagnostics.ShowBepInExConsoleOnStartup` 和响应快照在同一服务锁内提交，不得 `DetachConsole()`、改写全局 `BepInEx.cfg` 或影响总日志开关。
- `POST /logs/open-folder?target=aggregate`：打开总日志目录。
- `POST /inventory/set?type=ingredient|beverage&id=ID&qty=数量`：在 Unity 主线程通过 `RunTimeStorage` 原生 Range API 修改当前运行时材料或酒水库存，并回读校验最终数量；原生调用失败时不会绕过 callback 直写私有字典。
- `POST /inventory/bulk-set?type=ingredient|beverage&ids=ID1,ID2&qty=数量`：批量修改当前运行时材料或酒水库存；用于修改页的材料/酒水批量设为 `99`，只在批量结束后刷新一次运行时快照。
- `POST /orders/prepare-next?...`：按伴随窗口传入的稀客订单执行准备步骤，可组合送达酒水、开始料理、出锅后直送和收藏限定；调用方必须持有自动化 lease。请求必须携带 nullable `runtimeGuestId`、`foodTagId`、`beverageTagId` 原始数值身份，归一化 `guestId` 和 `foodTag` / `beverageTag` 只用于推荐、展示和诊断，不能作为对象匹配兜底。
- `POST /logs/export-diagnostics?open=true`：生成诊断 zip，包含 manifest、当前 snapshot、运行时目录、只含托管状态的 `snapshot/runtime-mission-diagnostic.json`、首次稳定日间采集的 `snapshot/runtime-scheduled-event-diagnostic.json`、`snapshot/runtime-mission-serve-in-work-diagnostic.json`、最近一次 `GET /missions/available` 形成的 `snapshot/runtime-available-missions.json`，以及总日志分片尾部；`open=true` 会打开诊断包目录。前三个任务文件只用于诊断，available 文件是最近一次独立业务读取的托管快照副本，四者都不能替代实时 API 读取。scheduled/post/active node label 与 nullable Trigger ID 是游戏的不透明精确身份，导出和读取均不得 `Trim()`、大小写归一化或建立修剪别名；node label 必须是非 null 且 `Length > 0`，Trigger ID 要区分 null、空字符串、纯空白、边缘空白和其他原始字符串。finished history 是非 null 的有界原始字符串，采集期间保留空字符串、纯空白、边缘空白、重复项和完整顺序，只用 Ordinal membership 判断，不将历史列表投影为业务数据。scheduled 报告另外包含事件级 `eligibility`、eligible/ineligible/not-applicable/excluded 四类计数、单独的 eligibility failure 计数，以及每个任务引用对应的 source-event eligibility；这些字段与 structural candidate 分开，资格读取失败也不会伪装成定义失败或抹掉已读结构证据，整份报告仍会 fail-closed。它不能直接作为业务可接取列表。scheduled 诊断失败不影响 tracked 任务、任务料理主计划、推荐、自动化、置顶或高亮。
- `POST /orders/complete-first?...`：按伴随窗口传入的稀客订单确认送达状态；一般订单可补送酒水并在满足后触发评价。血池地狱 BOSS 的最终料理只由绑定精确锅次的 cooking job 处理：料理送达与订单完成双开关同时开启时走专用结算，任一开关关闭时人工交接；该独立完成入口不得绕过 job 补做最终 setter 或评价。调用方必须持有自动化 lease，且沿用 nullable `runtimeGuestId`、`foodTagId`、`beverageTagId` 原始数值身份定位同一订单，不使用归一化 `guestId` 或文本兜底。
- `POST /orders/rare/dismiss?...`：按桌号及已知的 `runtimeGuestId` / 原始 Tag ID 全维度匹配并删除运行时稀客订单捕获缓存；缺少桌位或全部原始身份时拒绝删除，避免跨订单误清理。
- `POST /orders/normal/complete-first?...`：按请求中的订单 key、桌位、原订单目标和实际执行目标处理一笔普客订单；调用方必须持有自动化 lease。一般普客可按 `autoNormal*` 阶段配置送达酒水、开始料理、出锅后直接送达料理，并在订单 `get_IsFullfilled()` 为真后调用 `EvaluateOrder()` 完成评价；显示在普客区域的血池地狱 BOSS 订单由绑定精确锅次的 cooking job 按料理送达/订单完成双开关选择专用结算或人工交接，该普通完成入口不得重复提交最终料理或评价。`IsFullfilled` 只表示订单已满足并可评价，前端仍需以 `HasEvaluated` 或订单消失判断真正完成。若订单只存在于 HUD / `OrderController`，但没有可执行 `GuestGroupController`，后端必须拒绝自动送达并返回不可执行诊断。
- `GET /rare-guests/invitations?scope=current|all`：排队到 Unity 主线程执行严格纯读查询，返回候选、当前已邀请列表和禁用原因，不接受 POST。BepInEx 783 将目标原生静态成员暴露为同名托管静态属性，因此候选只从已完成的 base+mapped identity 快照 `Entries` 出发，严格读取并校验 `DataBaseDay.allNPCs` 与 `RunTimeAlbum.RecordedSpecialNPCs` 的 closed generic 字典形态。`RuntimeId` / `RuntimeStringId` 只用于精确定位 base 或 mapped 日间 NPC；取得 NPC 后必须严格验证 `NPC.identity.characterId ==` 已解析到最终基础稀客的 `SourceGuestId`。该 canonical character ID 是候选 API、羁绊字典、`HasNPCInvited` 和 `RecordInvitedGuest` 的唯一身份，同一 canonical 稀客的 base/mapped 形态必须先合并为一个候选；不得用 RuntimeId 查写羁绊/邀请，也不得做 runtime/source 双查或双写。`all` 要求每个 `RuntimeStringId` 在 `allNPCs` 中精确存在，并要求该 NPC 的 `possibleDestinations` 精确引用数组非空，再按 canonical character ID 精确查羁绊。目的地数组只作为“存在日间落点”的结构门禁，不读取 marker、不反查或展示地点名称。`current` 额外严格读取 `RunTimeDayScene.trackedNPCs`，只接受 `trackedNPCs[currentMap][RuntimeStringId]` 精确存在的当前地图候选，并基于已取得对象纯读 `overridePosition`、角色身份、`RunTimePlayerData.ShouldShowSpecialGuestsInDay`、`TrackedNPC.currentDestination.spawnMarker`、`NPC.defaultDestination.spawnMarker`、`openStatus`、`restDays`、`NPC.showTime` 和 `RunTimeDayScene.RemainActions` 重建 IDA 字段级可见性判断。`NPC.identity` 是外层 wrapper 属性，其值为 boxed blittable `SchedulerNode.Character`；内部 `characterIdentity` 只能读 exact public declared field，并严格解释 `Special=0` / `Normal=1`。两个 destination 是 non-blittable wrapper，`spawnMarker` 保持 exact property；不得把这些不同元数据形态合并成 property/field 兼容读取。单个候选的可见性字段读取失败时只禁用该候选并写入诊断，全部当前候选均失败时整轮标记为运行时不可用并进入有限重试。不得调用会转入 `DataBaseDay.RefNPC` 的 `TrackedNPC.ShouldShown`，也不得用 `NPC.Destination.None` 或硬编码隐藏标记替代 `NPC.defaultDestination.spawnMarker`。`DataBaseDay.GetMapLabelFromSpawnMarker` 会对未知 marker 执行抛异常的 `First` 查找，该方法和地点 label 匹配均不得进入候选链，地点展示也不得决定资格。合法缺少羁绊项返回 `kizuna-uninitialized`，不生成记录。列表禁止全量字典枚举、NPC 刷新、tracked NPC 创建、羁绊生成、`RefNPC()`、场景对象扫描和 dummy/组合稀客来源。readiness 未通过时不得解析单例；通过后 `StatusTracker` 只从直接基类 `DEYU.Singletons.Singleton<StatusTracker>.Instance` 读取，`DayScene.SceneManager` 只从直接基类 `DEYU.Singletons.MonoSingleton<SceneManager>.Instance` 读取，类型或属性不精确即 fail-closed，不使用通用单例解析或单对象场景扫描。
- `POST /rare-guests/invite-all?scope=current|all&levels=2,3&expectedDaySceneGeneration=GEN&expectedMapLabel=LABEL`：两项场景身份参数必填；缺少、generation 非正数、主线程开始执行时 generation/地图不匹配，或批处理中任一 `RecordInvitedGuest()` 前 readiness、generation、地图发生变化，都会拒绝继续写入并提示刷新。通过场景栅栏后重新构造最新纯读上下文，按范围、可见性、羁绊、当前等级成功邀请对话和已邀请状态复核，再逐项调用 `StatusTracker.RecordInvitedGuest()`；`levels` 可选，只邀请指定羁绊等级的可邀请项。不得信任前端旧列表，不调用 `DaySceneChatSelectionPannel.InviteSpecGuest()`，不使用 `HasTemptInvited()` 跳过候选，不直接刷客、推进时间或写 `Story.SpecialGuestControlled`。
- `POST /rare-guests/invite?guestId=ID&scope=current|all&expectedDaySceneGeneration=GEN&expectedMapLabel=LABEL`：同样要求发起时的正数日间 generation 和非空地图 label，在 Unity 主线程入口及 `RecordInvitedGuest()` 前复核，再按最新上下文重新校验指定稀客后写入今晚邀请名单；任何缺参或上下文变化都拒绝写入，不保留无场景身份的旧调用方式。
- `POST /automation/lease/acquire`：获取或续约当前客户端的自动化控制权；新所有者取得 lease 时会进入新的 command epoch。
- `POST /automation/jobs/cancel`：由当前 lease 所有者原子取消全部自动料理 job 和旧 epoch 排队命令，确认取消屏障后释放控制权；响应返回 `commandEpoch`、`cancelledJobs`、`cancelledCommands` 和 `leaseReleased`。
- `POST /automation/barriers/ack?sequence=SEQ`：由当前 lease 所有者确认已人工检查对应安全事件；后端按精确 sequence 解除同一订单截至该事件的未确认栅栏。找不到事件或不持有 lease 时不得清除前端人工状态。
- `POST /diagnostics/automation-decision?...`：把伴随窗口的自动化候选决策写入总日志。
- `POST /ui-pinning/target?businessGeneration=GEN&...`：更新游戏内料理、食材、酒水置顶和厨具高亮目标；只接受与当前 Active 经营完全一致的正数 generation，Closing、Destroyed 或上一场请求均失败；`recipeId` 是游戏 `Recipe.Id`，与成品 food ID 分开。
- `GET /favorites`：读取收藏料理和收藏酒水。
- `POST /favorites/add-recipe?...`、`POST /favorites/remove-recipe?id=...`、`POST /favorites/add-beverage?...`、`POST /favorites/remove-beverage?id=...`：增删收藏数据。
- `GET /custom-recipes`：读取自定义推荐料理。
- `POST /custom-recipes/settings?enabled=true|false`：切换整个自定义推荐料理功能，不改写单条状态。
- `POST /custom-recipes/update-flags?...`：按 `entry`、`customer`、`recipe` 或 `all` 作用域原子更新单条、分组或全部配方的启用/置顶状态。
- `POST /custom-recipes/upsert?...`、`POST /custom-recipes/remove?id=...`、`POST /custom-recipes/move?id=...&direction=up|down`：新增/编辑、删除和调整同一稀客内的推荐优先级。
- `POST /updates/status`：归并并返回当前更新检查、下载、暂存和安装程序状态，以及 `lastAttemptAtUtc`、`lastSuccessAtUtc`、`nextCheckAtUtc`、`consecutiveFailures`；归并 updater 结果时可能写入或删除状态文件，因此不是只读查询。
- `POST /updates/check`、`POST /updates/download`、`POST /updates/install-on-exit`：手动检查、下载或启动退出安装流程；更新服务按单操作串行。后台调度成功后按配置间隔续检，失败按 15m/30m/1h/2h/4h/6h 退避。Local API 关闭时先阻止新操作，再通过统一生命周期令牌取消自动/手动检查和下载，等待 handler、活动操作及调度器退出；取消检查恢复稳定状态并立即到期。下次启动会恢复强制退出留下的瞬时状态，并只清理下载一级目录内严格符合本服务语义版本/GUID 格式的临时目录。

`任务 -> 稀客邀请` 的自动列表读取以连接代际、规范 endpoint、`current|all` 范围、`runtimeDaySceneGeneration` 和当前地图 label 组成稳定身份，只在对应分栏可见、连接成功、`runtimeLoaded=true`、`runtimeDaySceneReady=true`、generation 为正数且地图有效时发起 GET。首次进入、范围变化、换图、同地图新 generation、重连和邀请完成后各读取一次，无关快照变化不会重复读取。同一身份收到结构化 `ok=false/runtimeAvailable=false` 结果或传输失败时只按 500/1000/2000/4000ms 重试四次；成功或确定性业务结果立即停止，耗尽后保留后端 `error/status` 并等待手动刷新或身份变化，失败响应不得渲染成普通空列表，也不能以未记录尝试形成热循环。单独/批量邀请从同一有效快照固定 `expectedDaySceneGeneration` 和 `expectedMapLabel`，没有有效写入上下文时前端不发 POST；后端场景栅栏负责阻止已经排队但延迟执行的旧写入。身份失效时立即取消请求、重试计时器并清空旧结果；Hook 同时使用 `AbortController`、请求 generation 和操作 ID，确保旧响应或旧 `finally` 不能覆盖新场景的结果与 busy 状态。手动刷新保留，但只强制重读当前有效身份。后端整批读取异常记录 scope、读取阶段和完整异常；上下文拒绝或成功结果记录可用的状态与候选诊断，便于从总日志定位实机 IL2CPP 差异。

`任务` 一级页默认打开 `任务列表`，另含 `稀客邀请` 分栏。任务列表并行读取 tracked 与 available，以连接代际、endpoint、日间代际和任务代际组成各自请求身份；切换页面、断开连接或身份变化会中止旧请求并拒绝迟到结果。后端 `runtimeAvailable=false` 必须显示对应的明确用户状态，完整技术原因保留在日志、API 和诊断包中，不能渲染为普通空列表。列表使用 `全部 / 可接取 / 可完成 / 进行中 / 待确认` 互斥页签，计数始终包含零值；同 label 重叠时 active tracked 项优先，`全部` 按 `available -> fulfilled -> tracking -> unverified`、中文标题和 label 稳定排序，单状态页签只渲染对应列表。窄屏页签只在自身横向滚动，不允许页面溢出。available 只用于列表展示，不得进入任务料理置顶、推荐、自动化、置顶或高亮。

血池地狱的订单准备/完成请求必须额外携带当前 `specialTargetRevision`。它独立于规范 target signature，只接受运行时同锁发布的正 revision；怪诞料理和空策略使用 `0`。后端不会因 `A -> B -> A` 的 signature 再次相同而接受第一轮 A 的迟到请求。

### v1.2.x 一次性迁移边界

`v1.2.x` 只保留一项明确的一次性数据迁移：Local API 启动阶段先把 `favorites.json` 中 `source=manual` 的料理原子写入 `custom-recipes.json`，写入成功后再从收藏文件删除旧条目。目标料理按 `customerId + foodTag + foodId + extraIngredientIds` 去重，中断后重试不会重复生成；目标文件无法读取或写入时不删除来源条目。后续 `GET /custom-recipes` 和 CRUD 端点保持无隐式迁移副作用。该迁移计划在 `v1.3.0` 删除，不得扩大为旧 API、旧配置、旧类型或旧业务路径兼容层。自 `v1.2.0` 起不再读取旧 GUID 配置 `com.tyukki.mystia-steward.cfg`。

除 `/health` 外，端点都需要 `X-Mystia-Steward-Companion-Token`。Token 由插件生成并保存在 BepInEx 配置中，同机启动伴随窗口时通过 `--token=` 参数传入 Tauri 后端；A 设备本机设置页可以复制或重置 Token。远程局域网连接时，用户需要在 B 设备伴随窗口顶部连接区手动输入 A 设备的 endpoint 和 token，点击 `连接` 后才开始轮询。Tauri 伴随窗口会显示实时 Mod 工作台，默认包含 `概览`、`普客`、`稀客`、`自定义推荐料理`、`经营中`、`任务`、`修改`、`帮助`、`设置` 九个页签；`任务` 内部按 `任务列表`、`稀客邀请` 分栏，`概览` 内部按 `状态`、`库存`、`操作` 分栏，`设置` 内部按 `窗口`、`连接`、`推荐`、`自动化`、`更新` 分栏。窗口设置包含透明度、90% 至 130% 字体大小、焦点切换、始终置顶、鼠标穿透锁定、手柄导航和显示调试信息；连接设置包含本地 API/LAN 连接配置并逐项展示可复制的 endpoint；推荐设置包含订单排序、推荐权重、预算策略、缺失厨具过滤、任务料理置顶、收藏料理/收藏酒水置顶、带库存显示和名称/库存排序的排除材料/酒水、同基础料理展示数量、游戏界面置顶和厨具高亮。工作台级更新控制器只读取 Mod 更新状态，活动状态 2 秒、稳定状态 60 秒轮询；发现新版时显示非模态提示，并按 endpoint + tag 保存 24 小时延后状态。Tauri opener 只允许打开本项目 Release URL。Android 伴随窗口只作为 B 设备 LAN 客户端，不提供桌面托盘、置顶、鼠标穿透、焦点切换、单实例控制和游戏关闭自动退出；独立 Windows 伴随窗口和 Android APK 不参与 Mod 主包自动更新。桌面鼠标穿透必须通过 Tauri 原生窗口 `set_ignore_cursor_events` 控制，不能只用 CSS `pointer-events` 模拟。帮助页内容来自 `apps/companion/src/data/help-content.json`，由前端渲染为目录树和详情面板，修改文案时优先改 JSON。`日志` 页签、扫描状态、运行时来源、性能耗时、订单来源和内部 key 这类诊断信息只在 `设置 -> 显示调试信息` 开启后显示。正式 Tauri 客户端通过原生后端读取本地 API。

伴随窗口的自动化能力只在设置页总开关开启、持有 lease 且当前经营 generation 为 Active 时运行。稀客并发、普客并发、最大重试和最大回退由 `CompanionPreferences` 控制；订单排序、推荐过滤、收藏限定和厨具预约仍复用经营中推荐的同一输入。稀客 `autoPrep*` 与普客 `autoNormal*` 的送酒、开始料理、送达料理、完成订单和出错暂停完全独立保存、独立传参、独立推进。所有自动开锅都登记 `AutomationCookingJob` 作为服务端精确锅次回执，防止 HTTP 响应丢失后再次扣料；只开启“开始料理”时 job 进入手动交接模式，不送达、不入箱、不复位，直到订单送达、订单稳定消失、显式取消或 Closing 边界同步取消。手动交接 job 仍按同一目标抑制重复开锅，但在成品离开厨具后不得继续参与 controller 预约，也不得因其他订单复用该 controller 而删除目标回执。Closing/Destroyed 后所有后续检查停止访问该场已释放或正在释放的游戏对象。

`AutomationCookingJob` 是料理跨帧状态的唯一来源。`RuntimeCookingGenerationTracker` 对 `CookController.SetCook(Sellable, Recipe, bool)`、`Extract(Action<Sellable>)` 和厨具内容换入 `Store(Sellable)` 建立精确被动观察：`SetCook` 分配新的 generation，三类事件共同推进可核对的厨具 content revision。每个精确 Harmony prefix 先登记 `MutationCompleted=false`，只有同一 revision 的 postfix 在 `__runOriginal=true` 且原方法正常返回时才改为 `true`；期间发生嵌套或后续 mutation 时，旧 postfix 不得覆盖新状态。所有回调由 no-throw 外壳隔离，追踪失败只能保留 default token 或未完成 mutation，不得影响游戏原调用。job 必须在自身 `SetCook` 返回后立即捕获 `LastMutation=SetCook && MutationCompleted=true` 的 snapshot，并在游戏回调后与登记时复核 snapshot 完全一致，再绑定 controller 指针、generation、content revision 和原生配方身份。除现有自动开锅路径调用一次 `SetCook` 外，所有权丢失恢复不得主动调用或重放 `Extract`、`Store`、`FinishCooking`，也不得读取、跟踪或操作 `IzakayaTray`。既有成功送达后的 commit-once 厨具复位与出锅回调不属于所有权丢失恢复，本功能不改变该清理语义。generation 与 content revision 均未变化时，游戏原生 `FinishCooking` 替换同锅 `Result` 继续沿用既有完成阶段和精确成品身份逻辑，原生黑暗料理 `Food/-1` 继续走既有边界；新 `SetCook` generation 发布 `interrupted/cooking-controller-reused`，`Extract` / `Store` content revision 或稳定严格空闲态发布 `interrupted/cooking-ownership-lost`。两者都只释放旧 job，不得送达、入箱或 reset 当前内容。后续选锅只接受两种状态：`Phase=Idle + Result=null + ChosenRecipe=null + CouldOpen=true` 的严格空闲，或同样 `Idle/Result=null/CouldOpen=true` 且最近 mutation 为正常完成 `Extract` 的旧 `ChosenRecipe` 残留；选择时和扣材料前必须分别重新读取并应用同一分类器。对于非空 `ChosenRecipe` 的残留例外，最近 mutation 为 `Store` / 新 `SetCook`、mutation 未完成或所有权不可读时一律不可用；标准严格空闲锅不要求已有所有权快照。自动送达开启时由前端使用既有有界玩家干预回退重新准备；自动送达关闭时保留目标绑定的手动交接，不预约已空闲 controller，也不因其他订单复用该 controller 而删除。快照 `automationCookingJobs` 暴露 jobId、目标、controller/result、generation/content revision、phase/progress、outcome/reason 和清理计数；`automationEvents` 用递增 sequence 发布终态，`automationSessionId` 标识当前 Mod 进程，断线后的同会话 lease 所有者据此接管。

自动化响应使用 `waiting/progressed/completed/interrupted/retryable-failure/blocked/fatal/cancelled` outcome，并携带 `stage/reasonCode/jobId/retryAfterMs`。C# 返回的 `beverage/cooking-start/cooking-delivery/order` 真实阶段必须优先于前端请求前推测，避免同一请求先送酒、后开锅失败时把料理副作用归到酒水阶段。前端用 `retryStage` 绑定失败计数，切换或关闭普通失败阶段时清除旧阶段退避。只有真实送酒、开锅、料理进度前进、送达提交或评价触发能报告 progressed；`cooking-cooker-waiting` 必须是携带正数退避的局部 waiting，不增加阶段重试次数，`cooking-ownership-lost` 与 `cooking-controller-reused` 必须是 interrupted 并只消耗既有玩家干预回退预算。waiting 和 interrupted 都不清零既有阶段失败次数，retryable-failure 才有界累加。副作用不确定的 blocked/fatal 必须设置人工确认栅栏并保留 `prepared`，不能被普通重试、阶段开关、总开关或无关订单事实清除。前端以 request epoch 和事件 sequence 丢弃取消前或终态事件前发出的迟到响应，不再解析中文文本或使用前端经过时长猜测恢复。烹饪与送达超时只累计游戏可推进的有效区间，暂停、断线、场景不可读和运行时不可达不消耗预算；进度停滞会保留旧锅，因此必须是 blocked，而不是自动重新开锅的 interrupted。

一般料理直接送达订单；job 仍拥有厨具成品且最终副作用尚未开始时，非目标成品、已确认的特殊经营目标签名变化、Tag 不符或目标连续不可达才使用保温箱恢复。CookController Result 使用独立的 signed 成品身份读取，必须以读取成功标志区分无效成员和原生黑暗料理 `Sellable(Type=Food, Id=-1)`；只接受非负料理 ID 或精确 `-1`，且 `-1` 不得进入静态目录、推荐或 `RefFood`。Phase 2 读到黑暗料理时保持 job 所有权并等待游戏原生完成，Phase 3 再按非目标成品进入既有入箱/回退链。`IzakayaConfigure.StoreFood()` 是非幂等 commit-once 操作，IDA 显示它先 `StoredFoods.Add`，再调用 UI/伙伴回调；正常返回和异常后在 `StoredFoods` 中确认到同一 Sellable 对象都代表已提交。只要原生调用已经开始，异常后对象不存在或状态不可读都不能证明没有发生前置副作用，必须 blocked，且不得再次入箱或清厨具。`OrderBase.set_ServFood/set_ServBeverage` 同样先写最终字段再调用视觉回调；订单送达要以最终字段中的同一 Sellable 对象确认 commit，料理只做有界同 generation cleanup，酒水只在确认 commit 后扣一次库存。厨具 cleanup 必须同时确认 `Phase == Idle`、`Result == null`、`ChosenRecipe == null`，读取失败不得当作成功。普客 target 保存并严格匹配 `OrderKey`；key 缺失时只接受同一原生订单对象，不按桌号或料理回退。普通经营普客不进入特殊经营目标 Worker；特殊模块确认的挑战订单才要求特殊执行目标。挑战订单在任何可能发生开锅副作用前锁存执行目标，后续料理送达和评价继续透传同一 extras/modifier 契约，直到订单完成、退休或经营 generation 变化。稀客 target 保存 trace、桌位、`runtimeGuestId` 与料理/酒水原始 Tag ID，出锅后重建请求时必须完整保留；捕获路径和实时扫描路径都使用这套强身份，展示文本不得参与相等、包含或宽松匹配。常规料理 job 不负责评价，后续订单阶段只在 `get_IsFullfilled()` 为真且严格读到 `HasEvaluated=false` 时调用一次。

血池地狱是受控例外。BOSS `NormalBusinessOrder.RuntimeGuestId` 必须从已确认的订单/控制器原始身份发布并由前端原样透传，缺失时不以 `GuestId`、名称或 role 回退。Mod 可以自动推荐、送达酒水和开锅；成品完成后必须复核经营 generation、job、order/order-controller/cooker-controller 身份、原始 nullable Tag 或 `NormalOrder` 精确 ID、桌位、canonical BOSS `1003`、目标签名和 revision、精确锅次与实际成品，并要求成品同时满足原订单和当前双 Tag。匹配成品只有在自动送达料理与自动完成订单同时开启时进入专用无界面结算，任一开关关闭时进入人工交接。`ShouldPlayerThrowDeliver` 只表示玩家投掷送餐能力，不是单次事务状态，专用结算不得读取或等待它；当前订单的 `ServedFoodInAir` / `ServedBeverageInAir` 才是原生送餐并发门禁，非空时无副作用等待。预检锁定 `OrderBase.ManualOrder`、同一原生 order/controller、最终 setter、fulfilled getter、对应评价与全部记账入口；手动控制订单只调用带同订单捕获回调的 `EvaulateManualOrder`，标准订单只调用 `EvaluateOrder(controller,false,null)`，禁止跨路由兜底。执行顺序固定为料理 setter、同锅清理、fresh fulfilled、评价、`AddBussinessFoodConsumes`、`OnOrderBaseStatusUpdate(FoodDelivered)` 和 `TryAddPlayerOccupiedDeskCode`；酒水同样补齐对应 consume/status/desk 通知。记账上下文必须在评价前缓存，评价返回后不复读 wrapper。每个 job 使用小型单调 tracker，不可逆阶段不确定即进入终止 ACK 栅栏且不重放；不得恢复旧大范围 finalization gate/coordinator、consumed/ACK 特例、送餐面板/UI 模拟、生成协程、托盘或 `MoveNext`；专用入口之外不允许通用直评。通用自动化安全栅栏和 `/automation/barriers/ack` 保持原有语义。

专用查询必须在每次 captured/live match 时重新读取当前 `OrderBase.ManualOrder`；只有当前为手动控制订单时，才按同一 order/controller 原生身份取回 `ManualOrderSet` 回调。酒水送达在任何扣库前及每个不可逆回调后的 fresh order 上精确门禁 `ServedBeverageInAir`，最终料理还同时门禁 `ServedFoodInAir`；初始在途状态无副作用等待，不可逆步骤后的冲突进入不重放栅栏。标准订单只有酒水先送达时，才在 final setter 后、range 库存调整前恢复一次部分送达耐心，手动控制订单保持原生 no-op；耐心与 range 回调后均再次复核 revision、同一订单和精确酒水。前端在稀客或普客的自动送达料理、自动完成订单开关从 true 变为 false 时复用现有取消端点和 command epoch，撤销未进入不可逆阶段的 cooking job；不得让旧锁存意图继续最终结算。

挑战订单后续送达、评价和厨具预约只允许复用同时匹配经营 generation 与规范特殊目标签名的锁存目标。同场目标轮换后不得预约旧目标厨具；特殊模块认领的订单存在任一自动化副作用意图但缺少当前锁存目标时整单暂停，不得仅放行普通酒水或评价。

厨具出锅结果只能读取 `CookController.Result` 或其精确 backing field，并确认对象是料理 `Sellable` 后才能送达或进入保温箱恢复。精确 `Type=Food, Id=-1` 是 IDA 验证的黑暗料理，不属于不可读；除此之外的负 ID、错误类型或成员读取失败仍必须拒绝。`CookController.result` / `resultVisual` 是视觉 `SpriteRenderer`，不能作为成品对象；连续读到非 `Sellable`、generation/content revision 所有权无法确认或内容处于无事件支持的矛盾状态时，必须形成有界 waiting/blocked/interrupted 结果，不能无限等待或触碰其他锅次。

自动化在订单部分送达后恢复顾客耐心时，必须先读取同一 `GuestGroupController` 的 `CurrentPatient` 和 `MaxPatient`，再把 `AddPatient` 入参限制到剩余耐心空间内；若检测到当前耐心已经高于上限，只允许调用 `SetPatient(MaxPatient)` 做一次状态校正。游戏原生 `AddPatient` 不裁剪上限，而 `GuestTableDisplayer.UpdatePatient` 会先用 progress 索引贴图数组，因此不得恢复到 `MaxPatient` 以上。

稀客自动化诊断由前端状态机维护，每个当前候选订单都要暴露当前步骤、下次动作、已开锅、已送酒、重试/回退次数、最近原因、暂停状态和人工确认栅栏。普客自动化也要按订单 key 展示下次动作、送酒、开锅、送料理、完成订单和订单已有料理/酒水状态，避免只靠长文本判断卡住位置。订单级执行步骤必须保存在对应订单状态中，并在经营中页订单条目下方的 `自动化详情` 折叠区展示；默认全部折叠。普通 `重试` 只解除该订单暂停并保留已完成阶段，普通 `重置` 让该订单重新判断；人工确认栅栏禁用重试，按钮改为 `确认已处理`。订单已从快照消失时仍必须通过独立待确认列表暴露事件。确认动作调用 `/automation/barriers/ack`，只有后端成功解除 sequence 后才清理本地状态；任何操作都不得影响其他订单。无执行计划时优先显示 `blockedDiagnostic.message`，总日志额外记录 code、首个阶段、分阶段计数、资源证据和稳定状态签名；稀客与普客分别使用当前自动化会话内最多 64 条的有界签名集合去重，切换会话清空，避免两类订单互相覆盖签名或同一状态重复刷日志。

伴随窗口直接双击启动时通常没有本地 API Token。前端必须停留在未授权状态，不得高频请求 `/snapshot` 或日志端点；用户修改端点或 token 输入框时也不得立即重连，只有点击 `连接` 或从游戏启动参数收到新的连接身份后才恢复轮询。相同 endpoint/token 的重复单实例通知必须幂等，不得清空快照或推进连接代际。自动探测和失败重试必须使用较短本地 API 超时且不触发全局刷新 loading；手动刷新可使用稍长超时。连接失败后只按递增退避重试 `/snapshot`，并且只有快照成功才能清除错误和恢复写操作；`/health` 成功不能建立已连接状态。允许用户点击 `停止` 暂停自动重连。

普客订单自动化仍是实验性功能。设置页总开关和经营中“启用普客处理”必须同时开启，并至少开启一个独立阶段；订单按首次出现顺序处理，不保留手动处理按钮。一般订单的酒水和料理统一提交到顾客桌面，只有订单已满足才评价，最终完成以 `HasEvaluated` 或订单移除为准。特殊经营规则按模块接入：`AutomationCookingJob` 同时保存原订单 match 目标、实际执行目标和规范特殊料理目标策略，出锅时不能用执行料理反查原订单。怪诞料理目标使用 `Any`，血池地狱目标使用 `All`；开锅和出锅复核阶段的 challenge、owner、经营 generation、Tag、match mode 或签名任一确认漂移都会使原执行目标失效。幽幽子三阶段评价必须取得当前仍由 controller 持有的精确订单与所需回调，capture 严格复核和 manager fallback 都使用同一验证器；怪诞料理大赛中的古明地恋本体在护盾期走通用评价，破防后交给 Boss 原生回调。血池地狱第二阶段显示在普客区域的 BOSS `NormalOrder` 和第三阶段 `NormalOrder` 仍走挑战模块，只有共享 IL2CPP 转换唯一成功且 BOSS 双身份成立时才开放；Mod 复核实际成品、原订单、当前策略、精确锅次和双 Tag，再按料理送达与订单完成双开关选择专用结算或人工交接。具体规则见 `docs/special-business-scenes-notes.md`。

总日志文件 `BepInEx/config/MystiaStewardCompanion/aggregate-mod.log` 默认关闭，由 `Diagnostics.EnableAggregateModLog` 或日志页“总日志”开关启用。启用后注册 BepInEx 全局 `ILogListener`，捕获所有日志源并按时间、级别、来源和线程标注；自动化日志记录 jobId、trace、controller/result、generation/content revision、phase/progress、结构化 outcome/reason、厨具暂忙证据、StoreFood commit 和 reset 尝试。夜间经营中若游戏把精确 `-1` 传入 `RunTimeStorage.ObjectOut`，只读诊断会记录对应的六类单项/批量出口、callback 抑制状态和经营 generation；每个入口和每局都有上限，不枚举批量参数、不读取库存，也不改变原生调用或异常。连续相同 automation action、目标和消息合并为 `repeat` 摘要。单个文件达到 10 MB 后拆分为递增编号分片；默认保留 30 个文件，约 300 MB。监听器不得回写自身状态，写入、分片和裁剪失败也不得影响游戏流程。

上述分片只保护 `aggregate-mod.log`，不保护 BepInEx/Unity 共享的 `LogOutput.log`、`output_log.txt` 或 `Player.log`。后台 worker 不得用无限异常重试向插件日志源刷写；本插件也不得接管、截断或删除共享日志。

代理工具注意事项：

- 默认同机使用 `127.0.0.1`，不要改成 `localhost`。
- LAN 连接只支持明确的私网 IPv4 endpoint，例如 `http://192.168.1.20:32145`；Tauri 代理会拒绝公网地址、`0.0.0.0` 和 HTTPS。
- 正式 Tauri runtime 使用原生 TCP，不经过 WebView 或系统 HTTP 代理；只有浏览器开发模式需要考虑浏览器代理和 CORS。
- 若同机伴随窗口无法连接，先确认日志中出现 `Local API loopback listener is available at http://127.0.0.1:32145`，再检查端口占用。
- 若 B 设备无法连接，先在 A 设备设置页确认 LAN 状态并选择与 B 设备同网段的 endpoint；再用 B 设备浏览器访问 `/health`，据此区分地址/防火墙/AP 隔离与 Token 问题。
- 受保护端点需要 token；调试伴随窗口时使用 Tauri 运行环境或显式携带 token 的本地客户端。

## 输入处理

游戏内不再保留 IMGUI 面板。默认 `RS Click` 在游戏侧同时读取 Unity legacy `JoystickButton9` 和 Unity Input System `Gamepad.current.rightStickButton`，并由 `ControllerToggleState` 锁存到物理释放；首次观察到 held 即锁存，迟到 edge 不得在释放前触发，也不得再叠加独立定时防抖。控制端口不可达时，`CompanionProcessLauncher` 只允许一个启动流程在途，并保持到新进程控制端口可连接或明确超时。

Tauri 进程必须在初始化窗口前原子绑定控制端口，再把预绑定 listener 移交控制线程；绑定失败的并发实例只通知端口所有者并退出。`F8`、游戏侧 `RS Click`、伴随窗口侧 `RS Click` 和 TCP `toggle` 共用 Tauri `WindowSwitchGate`；同一时刻只允许一个切换，冷却从 `applied` 结果开始，失败不提交冷却。切回游戏时必须先确认 `SetForegroundWindow` 成功，再按设置隐藏伴随窗口；Win32 非零返回值本身就是成功证据，返回零时只允许用当前前台窗口属于目标进程确认幂等成功，不能要求激活转换期的 `GetForegroundWindow` 与枚举 HWND 立即精确相等。失败返回明确 outcome，不能静默当成成功。Tauri 侧 `F10` 全局热键用于切换鼠标穿透锁定；`F8`、`RS Click`、单实例 `show` 控制消息和托盘显示/重连菜单必须自动关闭穿透。

前端只接受 Gamepad API `standard` 映射。`GamepadInputEngine` 负责设备所有权、neutral rearm、按键边沿/重复、摇杆滞回与单方向仲裁；`GamepadFocusManager` 负责可见焦点、控件语义、弹窗边界、局部滚动和 DOM 变化后的回焦；`useGamepadNavigation` 只编排 React 生命周期与业务动作。

## 调试建议

- `preflight.ps1` 报 DLL 缺失：先启动一次已安装 BepInEx 的游戏，再从 `BepInEx/core` 和 `BepInEx/interop` 复制所需引用。
- 构建报 `Il2Cppmscorlib` 缺失：从 `游戏根目录/BepInEx/interop/Il2Cppmscorlib.dll` 复制到 `References/`。
- PowerShell 执行 `bash ...` 报 WSL `/bin/bash` 不存在：在 Windows 下改用对应 `.ps1` 脚本。
- 运行时数据不可用：查看设置页场景名、扫描状态；需要日志时开启总日志并导出诊断包。
- `经营中` 没有稀客或点单：查看 `经营扫描 / Scan status`；如果 `manager=missing`，需要核对夜间经营管理器字段；如果 `guests>0` 但 `orders=0`，提供总日志中 `night-business` section 的 `Sources`、`Candidates` 和 `RecentRuntimeParseFailures`。

## 已知限制

- 构建依赖本机 `References/` 中的 BepInEx、Il2CppInterop 和 Unity DLL；这些 DLL 不提交到仓库。
- 运行时反射依赖游戏版本中的类型和字段名；如果游戏更新导致字段变化，需要核对并调整 provider 中的运行时类型名、字段名和方法名。
- 结构化任务能力发布 active-only tracked 与首版 type 5 available 两条独立业务链。`RuntimeMissionDiagnosticCapture` 的有界读档种子、Initialize 具体 DLC 校验、受控初始刷新和自然状态观察仍只服务 active tracked；type 3 角色互动、当日计划和限时任务仍不在 available 支持范围。
- `RuntimeScheduledMissionSourceReader` 由 frozen scheduled 诊断和 fresh available capture 共用，只读取 `scheduledEvents[CorrectedDay]` / `[-1]`、具体 List、精确 `Il2CppStringArray`、定义、active labels 和 finished 历史。frozen 诊断每个稳定 day generation 只采集一次；available 每次 GET 都在 Unity 主线程重新读取，开始和提交时复核同一任务 generation、day generation、mapped identity snapshot、当前日及容器序列。业务投影只接受 type 5 `KizunaCheckPoint` + `postMissionsAfterPerformance`，按 exact NPC/canonical 羁绊状态重建经验门禁，并严格检查任务 `preNodes`、active、`loopedMission` 和 finished 状态。所有 label 保持原始 Ordinal identity，不枚举 scheduled 字典，不复用冻结报告。
- available 读取严格纯读。严禁调用 `CanContinue`、`StartMission`、`RefNPC`、`CheckCharacterInteractEvent`、`HasSpecialNPCKizunaExpFull`、`RefOrGenerateSpecialRunTimeData`、`GetOrGenerateSpecialNPCKizunaLevel` 和任何触发、生成或推进任务的入口。available 结果只进入任务列表，不进入任务料理置顶、推荐、自动化、置顶或高亮。
- 存档中的任务 bool 只作诊断证据，`conditionFinishStates` 合法为空且不要求与静态定义条件数一致，不得直接发布为当前进度。每次加载最多同步读取 512 条静态定义，超限即令本代诊断 fail-closed；反射元数据可以缓存，但不得跨 generation 缓存 native 任务或语言对象。静态定义仅在 Unity 主线程按 `DataBaseScheduler.TargetNodeExists(label) -> RefMission(label)` 读取精确 `finishCondition` 引用数组、原始 `reciever`、独立 `hasReciever` 诊断值、`conditionType` 和 `amount`；标题只从语言 `Missions` 具体字典精确查键后读取 `LanguageBase.Name`，标题不可用时保留明确状态而不回退 `GetMissionLanguage`。`RunTimeScheduler.Initialize` 原方法成功返回后，Mod 只按已验证的 merged bucket 和任务顺序，对 `trackingMissions` 使用已知 int key 精确查值并按具体 List Count/indexer 绑定全部原 `TrackedMissionData`；label、数量或 identity 不唯一即整代 fail-closed。首次主动调用前必须确认 bucket 数、空 buffer、finished 多重集，并为全部唯一 label 完成上述核心定义预读；任一失败时不得调用任何任务对象。每个通过预读的读档任务对象只主动执行一次 `UpdateFinishStates`，随后要求 tracking bucket 数、buffer Count、finished 标签完整序列以及逐项任务 label/identity 与刷新前一致，并要求刷新结果数量与预读定义条件数一致，才原子提交当前状态。`GenerateTrackingData` Postfix 只捕获该次返回的精确新任务对象；`StartMission` 原方法完成、对象已经插入全局任务列表后，先预读该任务的精确定义，并在预读前、主动刷新后和提交前分别确认状态仍属于同一所有者线程上的 `Ready + runtimeAvailable` generation；若期间没有捕获该对象的自然刷新，才执行一次同样的 `UpdateFinishStates`，并立即复核 label、identity 和条件数量。旧 frame 一旦失去代际所有权，只允许清理，不得调用或提交。除这两个边界外，Mod 不主动刷新或轮询任务；后续变化只观察游戏自身的自然 `UpdateFinishStates` 及稳定生命周期 Hook。禁止调用 `HasFulfilled`、`ParseActiveMissionData`、任务完成/奖励/移除写入或宽泛枚举；`FinishNodeExtern` 只接受 finished 列表不变或保持原前缀的尾部追加，循环任务重复完成按频次保留。所有 Harmony 入口必须以 no-throw 外壳隔离诊断异常，finalizer 无条件返回原生异常；inactive 指针转移时清除旧身份，active 冲突和迟到旧指针回调 fail-closed。
- `RuntimeServeInWorkMissionDiagnosticCapture` 只被动观察游戏原本调用的 `ContainsSpecialNPCServeInWorkMission` 返回值和 `out foodId`，并按已加载 canonical 稀客身份、静态 ServeInWork 定义、任务/夜间经营 generation 交叉校验；Mod 不得主动调用该方法。任务/经营 generation 变化、经营 Closing/Destroyed、Hook/任务读取失败和原生异常全量清除信号；成功任务生命周期从当前完整定义构造 active、非 `Fulfilled` 的 `(canonicalGuestId, foodId)` 集合并精确复核现有信号，无关任务刷新不得清空其他有效信号，定义、目录、receiver 或 food ID 不完整时整轮 fail-closed。只有普通经营、同一 canonical/runtime 稀客恰好匹配一笔未送餐活动订单，且 food ID 在完整目录中唯一解析出非负 recipe ID 时，才向该订单投影 `MissionRecipePriority`；前端只有在默认开启的 `missionRecipePriorityEnabled` 为 true 时，才从正式候选中匹配同一 `foodId + recipeId`。exact active ServeInWork 任务料理可以跳过普通 food Tag 和料理厌恶判断；配对酒水仍须满足当前点单，料理解锁与库存、材料、厨具、预算、排除项、自动化收藏限定和其他硬门禁均继续决定能否成为主计划。合法任务计划排在自定义置顶、收藏料理/酒水和普通权重之前；页面、自动化、游戏界面置顶、列表高亮和厨具高亮统一消费 `executionPlans[0]`，普通经营多订单全局目标优先选择任务计划。关闭设置只从 sort context 移除任务目标，不关闭后端被动捕获；特殊经营、歧义、字段缺失或代际不一致一律不投影。任务主方案的料理行显示 `任务目标`，游戏列表发布仍由 `gameUiPinningEnabled` 独立控制。已打开列表收到新目标后，只能按目标内容代际在 Unity 主线程执行一次安全刷新；允许 Harmony 只读观察精确 `OnPanelOpen` / close / destroyed 生命周期来登记或清理面板实例，禁止主动调用或重调 `OnPanelOpen` 触发刷新，也禁止场景/面板扫描或轮询。tracked、ServeInWork 与 scheduled-event 三份任务 JSON 仍只用于诊断，available JSON 仅保存最近一次业务读取结果。不得恢复 `AllNodesMapping`、`GetAllNodes()`、`GetAllMissionData()`、`GetTrackedMissionData()`、`ParseActiveMissionData()`、主动 `HasFulfilled`、在上述两个精确边界之外主动 `UpdateFinishStates`、编译器生成 Hook、managed/field 兼容读取、复杂 tracking Enumerator 或 scheduled 字典全量枚举。
- 伴随窗口是唯一用户界面；游戏内不再提供备用 IMGUI 面板。
