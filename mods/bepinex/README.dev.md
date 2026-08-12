# mystia-steward-companion BepInEx Mod 开发说明

本文档面向开发者，记录本 Mod 的本地开发、构建、运行时读取和调试方式。用户安装和使用说明见 [README.md](README.md)。

## 项目结构

- `src/Core/`：推荐算法、数据模型和排序规则。
- `src/Save/`：运行时反射读取、兼容探测和推荐状态构造。
- `src/Ui/`：伴随窗口控制器、运行时循环和快照缓存。
- `src/Plugin/`：BepInEx 入口、配置和伴随窗口启动逻辑。
- `src/LocalApi/`：Token 保护的本地 API，始终保留回环 listener，也可显式开启附加 LAN listener。
- `References/`：本机编译引用 DLL，不提交到仓库。
- `tools/`：前置检查、构建、打包和锁定的 IL2CPP/IDA 分析脚本。

`tools/il2cpp-analysis/InteropGenerator` 是独立的 .NET 工具工程；Mod csproj 明确排除 `tools/**/*.cs`，
不能把生成器依赖编入发布 DLL。

运行时读取说明见 [docs/RUNTIME_PROVIDER_NOTES.md](docs/RUNTIME_PROVIDER_NOTES.md)。
游戏源码结构、BepInEx #783 interop 与 IDA 资料的完整重建方式见
[IL2CPP 源码与 IDA 分析工作流](../../docs/il2cpp-analysis-workflow.md)。

## 开发环境

Windows 上通常需要：

- 仓库通过 `toolchain.lock.json` 和 `global.json` 精确锁定 .NET SDK `10.0.110`。它同时构建仍以
  `net6.0` 为目标的 Mod 和以 `net10.0` 为目标的独立 `InteropGenerator`；不要为了 Mod 目标框架安装
  已停止支持的 .NET 6 SDK。若 Windows 仅安装
  .NET 10 runtime，运行仓库内一般 net6 smoke 前为该终端设置 `$env:DOTNET_ROLL_FORWARD = "Major"`。
  三项真实 Harmony/MonoMod 动态补丁 smoke 不能在 Linux .NET 10 CoreCLR 上运行；使用
  `corepack pnpm test:dotnet6-harmony` 在锁定的 .NET 6 容器中执行，不要删除探针或把测试改成源码断言。
- Node.js 精确锁定为 `24.19.0`、Corepack 为 `0.35.0`，并通过 Corepack 使用仓库固定的
  `pnpm@10.10.0`；构建不回退到全局 pnpm。
- PowerShell 7。
- Rust/Cargo `1.97.1`、Microsoft C++ Build Tools 2022 或 Visual Studio
  “使用 C++ 的桌面开发”组件。
- Microsoft Edge WebView2 Runtime。
- Windows 实机运行验证需要已安装并启动过一次 BepInEx Unity IL2CPP 的游戏目录；优先使用 #783 构建
  `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.783+c58c42d.zip`，不要直接追最新 Bleeding Edge。Linux 只做
  源码分析和构建时可使用下文的离线 interop，不需要先启动游戏。

推荐初始化命令：

```powershell
npm install --global corepack@0.35.0
corepack enable
corepack install
winget install Rustlang.Rustup
rustup toolchain install 1.97.1 --profile minimal
corepack pnpm toolchain:check
```

Linux 验证 Tauri 构建时还需要：

```bash
sudo apt-get install -y pkg-config libwebkit2gtk-4.1-dev libayatana-appindicator3-dev librsvg2-dev libssl-dev libxdo-dev
```

Linux 主机上的 Steam 版本仍是 Windows x64 PE 游戏。实际运行 Mod 必须使用 Windows x64 IL2CPP #783
包并由 Proton 加载，不能安装 Linux BepInEx；只生成开发分析资料时使用离线分析工作流，不需要启动游戏。

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
- Linux 离线分析环境的 `/huyu/data/disk/mystia-steward-companion/new/interop-783/assemblies/`

`tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj` 会使用真实 Harmony wrapper 验证 scoped prefix 返回传播、料理/材料/酒水列表 Hook、Food/Beverage 隔离、稀客/普客双槽原子发布、每槽五个功能位、颜色与共享资源 claims、经营 generation、revision/trace/lifecycle、普客 raw order key、Closing/下一场隔离、池化重绑、已打开列表单次主线程刷新，以及空目标恢复全部 Mod-owned 状态。运行该 smoke 时还要从 `BepInEx/core` 复制 `MonoMod.RuntimeDetour.dll` 和 `MonoMod.Utils.dll`；它们只供测试，不加入 Mod 编译和发布 preflight。外部引用目录通过 `-p:ReferenceDir="..."` 传给 `dotnet run`。

`tests/runtime-order-highlight/RuntimeOrderHighlightSmoke.csproj` 只验证游戏左下 HUD 订单卡片高亮：精确 `OrderingElement.Initialize/5` prefix 清除池化旧状态，通过唯一 `OrderController.CreateOrderingElement(OrderBase) -> OrderingElement` postfix 登记已完成绑定的卡片，并以 `OrderingElement.Out/0`、`DestroySelf/0` 处理正常退出；创建 postfix 必须严格复核 `__result.ActiveOrder` 与 `__0` 的非零原生指针相同，并读取 exact `OrderBase.DeskCode`。测试还覆盖 opaque trace 到活动 capture 的唯一解析、原生订单指针与 `ActiveOrder` 的最终复核、同桌池化重绑、仅有精确三组件且无子节点的原生边框模板、IL2CPP 组件枚举只暴露基类 wrapper 时的 typed native component 查询与指针集合闭合、无 `LayoutGroup` 的目标 parent、Mod 私有 overlay、原生焦点状态不变、主线程门禁、异常 fail-closed、Closing 销毁和 Destroyed 遗弃。该测试必须锁定不 detour 大量组件共用的 `OnDestroy/0` 空桩或 `ActiveOrder` setter，不存在桌号、名称、Tag、推荐内容、列表索引或首项兜底，不添加 `LayoutElement` 或替代视觉兼容路径，也不得调用 `ChangeBorderStyle`、`TryFocusToOrder` 或 `SetHighlight`。

`tests/runtime-seat-highlight/RuntimeSeatHighlightSmoke.csproj` 覆盖桌位定位、模板角色识别、owned texture/Sprite/material、几何映射、脉冲、健康复核、部分失败清理和经营生命周期；同时锁定 Unity 2021.3 的小纹理 mesh 限制及所有禁止的旧视觉路径。精确契约见下文“游戏内目标高亮精确契约”。

`tests/runtime-throw-delivery-order-highlight/RuntimeThrowDeliverOrderHighlightSmoke.csproj` 覆盖面板 Hook、订单到按钮的 fresh identity 链、serialized selection listener、双背景 XOR、相对几何、owned fill 层级、池化重绑、背景切换、安全退休和生命周期；同时模拟 BepInEx 783 的基类 wrapper 边界并锁定无界面副作用、场景扫描和备用视觉。精确契约见下文“游戏内目标高亮精确契约”。

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

该脚本会先严格验证 `toolchain.lock.json` 中的 Node/Corepack/pnpm/.NET/Rust 版本，再依次执行
`pnpm install --frozen-lockfile`、`preflight.ps1`、运行时数据模式提示、伴随窗口前端构建、Tauri 伴随窗口
构建、Mod DLL 构建和安装包生成。任一版本不一致都会在编译前停止。
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

Mod 包中的 `mystia-steward-companion-updater.exe` 最低支持 Windows 10 1703。它在创建窗口前强制确认
Per-Monitor DPI Aware V2，使用当前 DPI 的系统 message font、逻辑坐标和原生 Progress Bar，并在
`WM_DPICHANGED` 后按新显示器缩放重建字体与布局。修改该程序时不得恢复 `DEFAULT_GUI_FONT`、固定物理
像素或 GDI 手绘进度条；除常规 Cargo 检查外，还要在 Windows 的 100%/125%/150%/200% 缩放与跨显示器
移动场景中实测。

如发布机已配置 Android SDK/NDK、JDK、Rust Android targets 和 APK 签名配置，可在同一次发布构建中生成 Android APK：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -BuildAndroidApk
```

该参数会在 Windows 伴随窗口、Mod 包和 Windows 独立伴随窗口 EXE 生成后，额外执行 `pnpm tauri:android:apk:signed`，并把签名 APK 放到 `mods\bepinex\dist\mystia-steward-companion-android-arm64-v8a.apk` 和 `mods\bepinex\dist\mystia-steward-companion-android-armeabi-v7a.apk`。默认不启用该参数，避免普通 Windows-only 构建被 Android 工具链、keystore 或 Android 专用 Rust LTO 体积优化绑定。

## 拆分构建

需要拆分排查时，可从仓库根目录手动运行：

```bash
corepack pnpm toolchain:check
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

报告和截图默认写到 `/tmp/mystia-companion-ui-audit`。通用 UI 巡检覆盖 1280x900、900x760 和 640x760 三组视口；640px 用于验证 Tauri 桌面最小宽度下核心内容保持双列、全局状态与经营中六项摘要保持三列、一级导航按实际可见的五或六列单行完整显示且没有空轨道、二级及更深层页签等分铺满单行，并检查连接工具栏四项保持单行、专注工具栏右对齐和自定义配方入口与料理标题同行。如果使用 `pnpm preview`，把 `MYSTIA_APP_URL` 改成 Vite preview 输出的地址，通常是 `http://127.0.0.1:4173`。

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
dotnet run --project tests/runtime-static-data-catalog/RuntimeStaticDataCatalogSmoke.csproj -c Release
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

读档种子 smoke 验证有界 JSON 结构、实际 DLC 选择、日期偏移、bucket 合并、tracking 标签唯一性、finished 标签重复频次和畸形输入 fail-closed；定义审计锁定 `TargetNodeExists -> RefMission`、精确条件数组和语言字典；状态 smoke 验证存档 bool 只作诊断证据，读档初始化按已验证 bucket 和顺序绑定精确原任务 identity 后各执行一次 `UpdateFinishStates`，新任务在 `GenerateTrackingData` 捕获对象并等待 `StartMission` 完成列表插入，只在同一 `Ready + runtimeAvailable` generation、所有者线程和定义预读门禁内按需补做一次刷新，并要求条件数量与静态定义一致后才投影为 `Tracking/Fulfilled`，后续游戏自然刷新继续更新同一 identity；scheduled-event smoke 验证 frozen 诊断、当天与永久 bucket 精确查键、BepInEx 783 `Il2CppStringArray`、两类 post mission 来源、finished 完整序列、type 0/1/3/5 eligibility 和生成/触发入口禁令；available missions smoke 验证每请求 fresh read、type 0/1/5、`postMissions` / `postMissionsAfterPerformance`、四个 exact scheduler Hook、source lifecycle revision、`preNodes`、active、looped/finished、no-receiver、任务/mapped 身份、稳定签名和 unchanged 响应，且不调用原生推进。ServeInWork smoke 验证被动查询结果受任务/经营 generation、canonical 稀客身份和静态定义约束，并锁定成功任务生命周期按 active canonical/food pair 精确复核、无关刷新保留、完成/移除删除、失败全清和有界诊断。tracked missions smoke 另验证 active-only 业务投影、受控初始刷新、零条件任务、`Unverified/Tracking/Fulfilled` 三态条件、后续自然刷新形态 fail-closed/恢复、不可用状态和稳定内容签名；recipe priority smoke 验证普通经营、唯一未送餐订单、精确 `foodId + recipeId`、任务/经营 generation 与特殊经营门禁，以及复核保留和失效后清理。源码审计继续锁定 TryUpgrade 后单次 `GenerateSaveString(None)`、Initialize 实际 DLC 字典的 `Count + ContainsKey` 与已知 bucket 精确查键、具体 List 索引、两个且仅两个主动刷新调用边界、新任务三重代际门禁、刷新前后容器与 identity 不变量、`FinishNodeExtern` append-only 快照、原生异常透传，以及任务完成、奖励、移除和宽泛枚举禁止清单；前端审计验证任务列表模块严格默认关闭、关闭时零请求、显式开启与跨刷新持久化、关闭停止轮询、重新开启后不携带旧签名 fresh read，以及 `全部 / 可接取 / 可完成 / 进行中 / 待确认` 互斥页签、`自动触发 / 可接取 / 接取中` 展示、pending -> triggering -> tracked 交接、稳定排序、零计数空态、1280/640/390px 布局和手柄焦点。

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

该审计验证完整候选按硬过滤、任务料理置顶、自定义置顶、收藏料理/酒水置顶和普通权重生成唯一 `executionPlans[0]` 主计划；exact active ServeInWork 任务料理只跳过普通 food Tag 和料理厌恶判断，酒水与全部硬门禁继续生效。收藏限定在执行计划截断前归一化，页面料理/酒水首项投影该计划，自动化与游戏界面辅助不再扫描后续计划；还验证血池地狱只在精确 BOSS 身份下启用规则，`SpecialOrder` 保持原点单与双 Tag 严格全命中，`NormalOrder` 先选严格方案、严格无解时才从保持原料理/酒水且通过全部硬门禁的候选中选受控推进，非 BOSS 普通订单结果不变。订单展示审计另锁定语义签名不含观测时间/来源/显示标签、同硬上下文的逐单稳定展示、新订单局部 pending、送达/需求变化立即失效，以及展示投影不进入动作路径。只有零计划结果才生成 `blockedDiagnostic`，缺厨具、缺基础材料、酒水 Tag、预算和幽幽子二阶段 `ExGood` 不足能定位到各自首个清零阶段，诊断不会改变候选、排序或自动化目标。

Yuuma 双 Tag 审计另使用超过公共 beam 宽度的压制冲突样本，确认专用 `matched/reachable/blocked` 分桶不会把仍可达的严格组合误判成受控推进；怪诞料理仍使用原 preference-first 公共排序。

修改游戏界面置顶、料理/材料/酒水列表项、HUD 订单卡片、投掷送达面板高亮契约、连接重发或推荐 Worker 生命周期后，还要运行定向巡检：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:ui-pinning
```

该巡检会验证 `POST /ui-pinning/targets` 原子发布零至两个目标，双目标稳定按 rare、normal 排列，并携带当前 `businessGeneration`、五个功能开关及每槽正式 revision、颜色、exact trace、正 lifecycle、0-based 桌位和内容字段；普客还必须携带 raw pointer order key。它同时锁定 `Recipe.Id` 与 food ID 分离、两类主计划独立选择和同时保留、稀客槽内任务方案优先、送达/Worker pending/error 只影响所属槽、连接与经营代际清空、业务失败退避、迟到响应隔离、默认与自定义颜色发布、实验性功能页唯一风险提示，以及十个游戏界面辅助开关使用不带重复“（实验性）”后缀的规范名称，并确保不恢复 singular 路由。

修改自定义推荐料理总开关、分组、批量状态或排序契约后，运行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:custom-recipes
```

该巡检会验证总开关持久化、草稿跨页签保留、稀客/基础料理分组记忆、页面级/分组级/单条状态更新、同稀客排序、单写者和最小窗口横向溢出。

修改自动化阶段机、料理 job、配置/控制权切换或断线接管协议后，运行：

```bash
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
dotnet run --project tests/runtime-automation-control/RuntimeAutomationControlSmoke.csproj -c Release
dotnet run --project tests/automation-cooking-job/AutomationCookingJobSmoke.csproj -c Release
dotnet run --project tests/runtime-order-terminal-receipt/RuntimeOrderTerminalReceiptSmoke.csproj -c Release
dotnet run --project tests/runtime-cooker-snapshot/RuntimeCookerSnapshotSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-special-business/RuntimeYuumaSpecialBusinessSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-finalization/RuntimeYuumaFinalizationSmoke.csproj -c Release
dotnet run --project tests/yuuma-cooker-topology/YuumaCookerTopologySmoke.csproj -c Release
pnpm audit:automation
pnpm audit:connection-recovery
```

经营生命周期 smoke 直接编译生产 Hook，并验证五个精确边界、倒计时结束后在座服务仍保持 Active、重复 Closing 幂等、Closing 后禁止重新激活和下一场 generation 递增。自动化控制 smoke 验证当前主设备 profile、精确 authority revision lease、逐阶段开关、租约过期/撤销、古明地恋阶段特例和已放行原子副作用与权威切换串行。料理 job smoke 验证 `SetCook` generation、`Extract` / `Store` 内容代际、同代际原生结果替换、控制暂停期间所有权丢失后的手动交接、严格空闲与已完成 `Extract` 残留的厨具选择、厨具暂忙等待、有效停滞时钟和 `StoreFood` 提交后有界复位；job 只保存稳定预约和 pointer，轮询、复位及出锅均从当前物理目录重新绑定，不保留跨帧 `CookController` wrapper。该 smoke 还验证 cooker controller lease 在人工交接、严格 cleanup 完成或 cleanup 明确终止后单调释放、释放后 evaluation receipt 不占锅，以及 12 次/5–20 秒有效评价收口、同步 exact receipt 和同 lifecycle ACK。独立 terminal-receipt smoke 只直接编译 `RuntimeOrderTerminalReceiptStore.cs`，验证 store 的单调 lifecycle、精确 identity、原生指针 ABA 隔离、`Evaluated` 优先级、旧 postfix 隔离、有界容量、Clear 不回退 watermark 和 wrapper-free token/receipt；它不承担生产 Hook 接线验证。成功创建门禁和五个生产终态 Hook 由 special-order-runtime-capture smoke 直接编译生产捕获代码验证。厨具快照 smoke 验证 complete/unavailable 两态、精确空厨具位不计容量且不压缩后续 index、任一条目失败整轮不可用、坐标/原生身份/挑战锁定交叉校验、后端统一自动化可用性分类、有界诊断和内容签名；拓扑 smoke 另验证血池地狱三个公开锁锅/可用性方法的短屏障、完整 Hook、经营代际、revision、规范 SHA-256 租约、永久锁锅后开放厨具继续工作及旧租约失效。自动化审计验证按 index + native identity + grid position 的实体槽位、锁定位置先于 controller 状态读取拒绝、开锅前重验、零合成容量、多类型控制器单次预约、优先保留多类型控制器，以及厨具不可用的队头订单不阻塞其他类型。Yuuma 特殊经营与最终事务 smoke 锁定精确订单形态与强身份、原始料理/酒水、目标 policy/revision、精确锅次、严格/受控许可作为不同 job identity、受控模式仅允许可读 Tag 未全命中而不跳过其他门禁，以及逐次读取料理送达/订单完成开关、精确控制 permit、玩家投掷能力不阻断、双 in-air 等待和 `ManualOrder` 精确评价路由。最终事务仍覆盖不可逆 claim 前 fresh 厨具复核、最终 setter、两次完整订单复核、fresh 同锅复位、availability 前后及 `AfterPlayerExtract` 前重取、fresh fulfilled、评价、消费与 Partner 通知；酒水另覆盖专用 fresh lookup、包括 `-1` 的完整原生库存序列、有限库存显示、精确 Tags/FullName/closed generic 参数和普客 Yuuma 终态前置返回。它还禁止恢复旧大范围 coordinator、送餐面板/UI 模拟、生成协程、托盘和 `MoveNext`。其余审计覆盖通用原生 `ServFood` 回执、读取失败有界栅栏、结构化 outcome、阶段计时、command epoch、运行时事件时序、mock 暂停/接管、快照恢复、持续退避、连接身份幂等和 automation control lease 会话绑定协议。

修改稀客订单捕获、料理/酒水点单原始 Tag ID、运行时 Tag 映射或稀客自动化匹配后，运行：

```bash
dotnet run --project tests/special-order-runtime-capture/SpecialOrderRuntimeCaptureSmoke.csproj -c Release
dotnet run --project tests/rare-order-identity-matching/RareOrderIdentityMatchingSmoke.csproj -c Release
```

两个 smoke 直接编译生产捕获/匹配代码，验证 `RequestFoodTag` / `RequestBeverageTag` 原始有符号数值身份完整保存、`无酒精(-1)` 等合法负数 ID 不会被当作缺失，并锁定创建入口、只接受非零 IL2CPP 原生对象指针且不回退 managed hash 的严格 native key、七个必需生命周期 Hook、经营 generation 覆盖门禁、fulfilled 评价清理，以及 `CleanOrderInfo` 与成功 `RepellInternal` 的直接退出边界；`RepellInternal` 的两个 `haveSeated` 分支都必须退休订单。同一 native slot + lifecycle 中任一 food/beverage raw ID 漂移时，必须删除 capture、失效该 lifecycle，且不得合并新值或发布替代 identity/trace/order；只有后续成功原生创建绑定分配新 lifecycle 才能恢复。捕获与 Provider 不得调用 `GetOrderFoodText`、`GetOrderBevText`、override 委托链、`SpecialGuest.Get*TagText` 或 `ToString()`；展示只允许按 raw ID 查规范运行时 signed Tag map，映射缺失时有界记录并 fail-closed，不生成文本或 `#id` 回退。普通 `OrderBase` 回调只计入 `notApplicable`，不得污染特殊订单 `parseFailures`。匹配覆盖 nullable 原始身份、映射稀客的独立 `runtimeGuestId`、Delivery/Completion/NativeEvaluation 的 fulfilled 差异、捕获就绪后 HUD 空窗或读取错误不裁剪捕获、同 native key 的 HUD 行与捕获去重、历史 `AllOrders` 不参与业务，以及幽幽子重修三阶段并发订单隔离；`pnpm audit:automation` 另检查一般动作拒绝 manager 回退、仅古明地恋 BOSS/幽幽子三阶段保留显式 live-controller 门禁、`OrderBase` 规范转换、未送齐等待顺序和剧情版回调对象绑定。

修改特殊经营挑战名称来源、目标捕获状态、上下文规则注册表、运行时稀客目录或页面名称 fallback 后，运行：

```bash
pnpm audit:special-business
```

该审计会验证挑战名称使用游戏原生 IL2CPP `InspectorName` 固定中文元数据、永久失败缓存诊断且不重试、瞬时失败按固定间隔持续重试、规则注册表不再保存中文名称映射、名称不可用时页面只显示一次有效 challenge type；同时验证 HUD 目标按 raw challenge owner、target kind、夜间经营 generation 和 inactive 会话边界隔离。血池地狱另锁定六个精确、被动、no-throw 的 `IncomeControllerYuuma` HUD 入口、挑战类型不可读时清空旧目标并 fail-closed、声明为 `OrderBase` 的回调对象只能通过共享 IL2CPP 入口唯一转换成 `NormalOrder` 或 `SpecialOrder`、具体订单成员与 `GuestBase.Id=1003` 强身份、非 BOSS 订单隔离，以及统一特殊目标 `All` 契约。受控推进许可独立于目标 policy/signature/revision，仅允许精确 BOSS `NormalOrder` 和原料理/原酒水，不得用于 `SpecialOrder` 或跳过硬门禁；出锅复核只对已许可受控 job 放宽可读 Tag 的全命中要求。专用结算源码审计锁定双开关、玩家投掷能力不得作为门禁、两类原生 in-air 无副作用等待、`OrderBase.ManualOrder` 精确 bool、同订单手动回调、标准/手动评价分流、最终 setter 与同锅清理、fresh fulfilled、评价后消费/Partner 状态/桌位通知、评价前缓存上下文、评价后不复读 wrapper 和不可逆阶段不重放。此前缺少完整顺序和订单路由复核的通用直评会破坏 P3；只有专用结算可调用对应评价入口，旧大范围结算、送餐面板/UI 模拟与生成协程路径都不得恢复。运行时目录审计继续禁止重新调用未消费且会产生 Warning 的特殊请求语言 getter。

该审计不能替代实机确认。血池地狱实机测试应开启总日志，完整覆盖三个阶段、第二阶段混合订单、第三阶段 `NormalOrder`、严格方案与受控推进方案、多笔并发、`ManualOrder=true/false` 两条评价路由、目标轮换、黑暗料理、真实料理/酒水 in-air、事件锁锅/毁锅、关闭阶段开关、切换主设备、lease 过期和手动接管。确认严格方案仍命中双 Tag；受控推进只在严格无解时出现，明确显示命中数量，并由游戏正常结算。需同时观察受控方案的伤害和狂暴变化，不将其视为最高收益承诺。控制在最终结算前变化时原锅成品应稳定暂停，恢复正确配置与 lease 后完成料理提交、评价和原生状态通知；玩家取走或替换成品才进入手动交接。锁定厨具应退出推荐、预约和高亮，开放厨具继续工作。测试结束后提供完整 `aggregate-mod.log` 和诊断包；只有发生报错、卡死或闪退时再补充 `BepInEx/LogOutput.log` 与 Unity `Player.log`。

修改稀客邀请候选读取、GET/POST 方法边界、日间刷新代际或邀请页面请求生命周期后，运行：

```bash
dotnet run --project tests/rare-guest-invitation-readonly/RareGuestInvitationReadOnlySmoke.csproj -c Release
dotnet run --project tests/local-api-method-matrix/LocalApiMethodMatrixSmoke.csproj -c Release
pnpm audit:rare-guest-invitations
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:rare-guest-invitations:ui
```

只读 smoke 按 BepInEx 783 实际元数据验证同名静态属性、closed generic 具体字典的 `ContainsKey`/indexer 精确查键、错误 key/容器形态拒绝、non-blittable struct boxing、`possibleDestinations` 精确引用数组非空门禁，以及基于现有 `NPC` / `TrackedNPC` / 玩家状态字段的可见性判定。blittable `SchedulerNode.Character` 被装箱后，`characterIdentity` 必须按 exact public declared field 读取并只接受 `Special=0` / `Normal=1`；不得改用属性或 field/property fallback。non-blittable `NPC.Destination` 仍按 Il2CppInterop wrapper 的精确属性读取 `spawnMarker`。`StatusTracker` 和 `DayScene.SceneManager` 只能分别从各自直接泛型基类的精确静态 `Instance` 属性取得，并且 readiness 未通过前不得读取。生产源码扫描同时禁止 NPC 刷新、羁绊生成、全量字典枚举、`GetMapLabelFromSpawnMarker`、`RefNPC`、`TrackedNPC.ShouldShown`、`NPC.Destination.None`、`RuntimeReflectionUtility.GetSingletonInstance`、本地泛型单例扫描和 `FindUnityObject`。同一 C# 审计锁定 `expectedDaySceneGeneration` / `expectedMapLabel` 必填、主线程入口复核以及每次 `RecordInvitedGuest` 前复核；方法矩阵锁定列表 GET-only、单独/批量邀请 POST-only。前端静态审计验证模块总控严格默认关闭且按客户端持久化，写入上下文和列表读取身份拆分，并共同包含连接代际、endpoint、范围、日间 generation 和地图；非法日间上下文不会读取或发送写入，API 使用 GET，`runtimeAvailable=false` 或传输失败只按 500/1000/2000/4000ms 有界重试，旧列表请求由 AbortController/请求 generation 隔离。Playwright 巡检验证默认关闭时零请求、显式开启与跨刷新持久化、范围/页签身份变化、瞬时不可用恢复、确定性失败显示、手动强制刷新，以及 POST 已开始后跨分栏仍保持 busy 和确定返回、写入期间总控不可关闭；它不替代后端写入栅栏 smoke。

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

运行时固定数据读取成功后，C# 会把 `DataBaseCore`、`DataBaseCharacter` 和 `DataBaseLanguage` 中的料理、食材、酒水、普客、稀客和 tag 映射构造成 `RuntimeDataCatalog`，发布到独立的 `/runtime-data` 缓存与端点，并切换 C# 推荐仓库到运行时仓库。核心目录只以 `DataBaseCore.IngredientsMapping`、`BeveragesMapping`、`FoodsMapping`、`RecipesMapping` 和 `IzakayasMapping` 五张精确 `Dictionary<int,string>` 为枚举根。映射配方先冻结 `foodID`、`ingredients` 与厨具；直接引用但未进入 `FoodsMapping` / `IngredientsMapping` 的非负 ID 会加入有界依赖闭包，并通过精确 `RefFood` / `RefIngredient` 和对应语言入口读取。这一闭包不会扫描全量数据库、写入共享 Mapping、读取第三方 Mod 专用注册表或跳过无效配方。所有 mapping 条目先严格验证 CLR `Int32` 键、非空 CLR `String` 值、原始 ID 唯一性和容量；材料、酒水、料理和配方显式使用非负内容 ID 域，负数内部键在核心业务投影边界排除且不会调用对应 `Ref*`，排除后没有非负 ID 时整轮读取失败。依赖项额外验证 ID、运行时对象 identity、语言数据、引用总量和闭包容量；失败日志会给出依赖类型、ID 与来源配方。`IzakayasMapping` 显式保留完整 signed ID，再逐项调用 `RefIzakaya`；允许 signed ID 的料理/酒水 Tag 字典也保持独立规则。Izakaya 条目先精确读取 `DaySceneMapLabel`，只有空标签占位允许跳过；非空标签必须严格读取原生 `DaySceneMapName`，读取失败令整轮失败，合法但不属于支持日间经营地点的条目才记录 skipped。确认支持地点后再严格读取普通/稀客池，不读取特殊经营和占位条目中与推荐无关的合法空池。基础稀客从 `GetAllSpecialGuests()` 的精确引用数组读取，喜好、厌恶和酒水 Tag 只取声明的原始字段，不调用生成或计算型入口。核心目录与基础/映射稀客 identity 快照独立记录完成状态；二者均完整前不构造推荐状态 provider。普通地图或就绪变化复用已完成的静态身份，只让未完成读取立即重试。进入主菜单等非游戏场景清空存档运行态后，identity 会独立重建，不能被仍完整的核心目录短路。伴随窗口概览页的“推荐数据”显示“游戏运行时”时，表示前端推荐算法已经获得完整运行时数据。

前端从同一份完整稀客原始目录构造两个互不混用的投影：`rareCustomers` 只包含有合法日间地点、可在普通页面选择的稀客；`rareCustomerProfiles` 只携带特殊经营评价所需的 canonical ID、名称与喜好/厌恶 Tag，即使原生 `places=[]` 也保留。幽幽子重修等规则严格按已验证的基础 character ID 读取评价档案，不按映射身份、中文名或内置 Tag 回退。两份投影都属于推荐数据签名，worker 和 NormalOrder 特殊目标缓存不能跨签名复用结果。

运行态读取不依赖固定秒数等待。`DaySceneSustainedPannel.OnPannelPostOpen` 只表示日间面板已出现，是独立最终门闩而不是 ready 信号；manager/Action 链可在面板前后捕获。普通读档必须从 `DayScene.SceneManager.OnFirstEnterDaySceneFinish` 捕获同一 manager 的 `RunTimeScheduler.OnEnterDayScene` 外层 Action，等该 Action 进入匹配的 `DefaultOnFinish` 后再捕获 `OnEnterDaySceneMap` 最终 Action，并只在最终 Action 返回且面板已打开后解锁。手动经营返回只接受入口前明确读取到的 `NightSceneDirector.IsManualWorkSceneSession` 分支。每次读取仍要求同一原生 manager、`IsMapSwapping=false`、`m_HasTriggerOnEnterDaySceneEvent=false`、`RunTimeScheduler.isExecuting=false`、`SceneDirector.IsInEvent=false`、manager 的 `isExecutingScheduledActions=false`、`UniversalGameManager.IsSwitchScene=false` 且当前地图 label 有效；任一 Hook 或字段不可验证时保持 fail-closed。夜间经营准备读取要求 `PrepNightScene.UI.IzakayaConfigPannel.OnPanelOpen` / `GoToSpecific` 已触发，且 `WorkPrepScenePannelRoot` 下的 `IzakayaConfigPannelNew` 仍激活。准备阶段只读取库存、已解锁、流行 Tag 等基础玩家运行态，因此 `修改`、`普客` 和 `稀客` 页面可以提前使用；当前日间地图和稀客邀请仍只在完成上述解锁的日间场景读取。

推荐状态以完整运行时静态目录的 ID 闭包为边界：料理逐 ID 调用 `RunTimeStorage.HaveRecipe(int)`，材料逐 ID 调用 `GetIngredientCountById(int)`，酒水逐 ID 调用 `GetBeverageCountById(int)`，不生成存储快照或解析其 IL2CPP 容器。材料和酒水数量都保留精确 `-1` 作为无限，`0` 不发布，低于 `-1` 失败。玩家等级、流行喜好/厌恶 Tag 和明星店开关继续使用轻量 getter 读取；没有任何已解锁料理时等待下一轮，不发布空的可用料理集合。

为降低经营中掉帧风险，本地 API 快照发布会做轻量节流：Unity 主线程最多约每 0.35 秒刷新一次缓存 JSON；若快照内容签名未变化，会复用上一份缓存 JSON，不为了 `CapturedAtUtc` 或性能数字重复序列化。完整 `RuntimeDataCatalog` 不再放进 `/snapshot`；快照只发布目录是否完整、来源、状态和签名，伴随窗口仅在本地缓存为空或签名变化时通过 `/runtime-data` 读取完整目录。`runtimeDataComplete/runtimeDataStatus` 使用 core+identity 组合状态；core JSON 可以提前序列化，但 identity 未完成时前端不会消费。运行时固定数据已经完整读取后，会缓存稀客映射和静态目录快照，经营 provider 与经营诊断只消费缓存，不再从经营快照热路径反复触发静态数据扫描；读取未完整时只由控制器按约 5 秒间隔重试，目录对象不叠加第二套时钟。目录或 identity 未完整时直接发布带阶段的精确状态，不再继续调用 provider 产生泛化的“目录不完整”异常。伴随窗口按签名缓存最近一次完整运行时数据，不能把 `/runtime-data` 的临时读取失败当作主快照丢失；未完整占位则必须随新快照更新，不能锁存首次等待文本。总日志的 `[snapshot]` 段包含 `runtimeSceneReadiness`、`runtimeDataComplete`、`runtimeDataSource` 和 `runtimeDataStatus`，可直接定位日间目录失败阶段。概览页和经营中页会显示 `performanceMs` 中最近约 12 秒内耗时最高的快照环节，排查卡顿时优先记录 `refresh.business`、`refresh.runtime`、`snapshot.serialize`、`runtimeData.serialize`、`automation.collect` 和 `snapshot.publish`。经营扫描还会细分 `business.rare.*`、`business.normal.*` 和 `runtime.cookerSnapshot` 等子项；普客订单快照会在短时间内复用，避免同一轮 `/snapshot` 发布重复枚举 `OrderController`、HUD 和 `GuestsManager`。

夜间经营运行时由 `RuntimeNightBusinessLifecycle` 的精确 generation 管理。只有 `WorkSceneSustainedPannel.OnPannelPostOpen`、`GuestsManager.CloseIzakayaDelayed`、`CloseIzakayaAndLeaveChallengeMode`、`NightScene.SceneManager.ToResult` 和 `OnInstanceDestroyed` 五个 Hook 全部成功后才允许进入 Active；任一成员缺失时保持 fail-closed。`TryCloseIzakaya` 只在倒计时结束后停止接客、遣散排队顾客并等待在座顾客完成服务，不得进入 Closing；清桌期间保持同一 Active generation，继续处理订单、自动化和界面目标。最后一桌离席后创建 `CloseIzakayaDelayed`，或特殊经营退出/结果转换时，才进入 Closing，同步停止运行时访问、失效界面目标并清理稀客/普客捕获、特殊经营上下文、料理 generation 和自动料理 job；`OnInstanceDestroyed` 进入 Destroyed。Closing 后不能重新 Active，只有 Destroyed 后下一次工作面板打开才能递增 generation。

普客订单被动快照缓存约 1 秒，且只在当前为 Work 场景并处于 Active generation 时读取。`NormalOrderRuntimeCapture` 通过成功 `GuestGroupController.PushToOrder` 和 `GuestsManager.SetManualControllerOrderInternal` 捕获订单与可执行控制器绑定，并要求七个生命周期 Hook 与当前经营 generation 均已完整覆盖。捕获就绪后进入 `normalOrderMode=authoritativeCapture`：精确捕获是业务和执行权威，live `OrderController` / HUD 只追加没有捕获绑定的不可执行可见行；同一非零 native `orderKey` 必须去重为捕获订单。不得按桌位/料理/酒水重绑，也不枚举 `NightSceneDirector.controlledGuest`、`GuestsManager` 或队列做启动扫描。单轮 HUD 空窗或读取错误不得过滤、隐藏或删除捕获；捕获未就绪时进入 `normalOrderMode=visibleFailClosed`，仅显示不可执行 HUD 行。捕获版本变化只刷新普客订单快照。没有活动 `AutomationCookingJob` 时不在 `Update()` 热路径轮询料理 job。

游戏界面置顶不反射改写 UI 列表；`UpdateAllVisual`、`UpdateRecipeField` 与 `UpdateBevField` 建立可嵌套的 ThreadStatic 数据刷新作用域，最外层 prefix 固定同一份 immutable 双槽快照。`RunTimePlayerData.CheckPinned` 的 bool prefix 只为任一槽精确声明的目标设置 `__result=true` 并跳过原方法，非目标或作用域外完整执行游戏逻辑。目标集合内容代际变化且页面已打开时，只能按代际去重，在 Unity 主线程与同一 exact target publication lease 内执行唯一有序窄刷新：制作页固定执行 `UpdateIngField -> UpdateRecipeField -> m_StaticIngredientsGroup.UpdateElements() -> m_StaticRecipeGroup.UpdateElements()`，酒水架仅在 `openType == Beverage` 时执行 `UpdateBevField -> m_BevsGroup.UpdateElements()`。programmatic 全链路必须处于同一 panel-specific ThreadStatic target scope，使 backing data 排序和 visible callback 的原生 `CheckPinned` 消费同一 target。全部阶段成功后才能提交 applied generation；任一步失败都以 `ingredient-backing-data`、`recipe-backing-data`、`ingredient-visible-elements`、`recipe-visible-elements`、`beverage-backing-data` 或 `beverage-visible-elements` 精确诊断，同一 generation 不重放。制作页不得改用完整 `UpdateAllVisual`，不得调用 `UpdateSelectedField` 或 `UpdateOutputField`；两类页面都不得主动调用或重调 `OnPanelOpen`、扫描或轮询，也不得把 food 模式仓库页登记为酒水表面。不得压制玩家收藏，cooker 类型 `3` 只由独立高亮服务处理，也不得恢复旧 recipe-only 置顶刷新或列表改写路径。制作页加料选项对瞬时 Recipe 列表的受控插入和提交是独立边界，不能扩展为置顶实现。

`RuntimePinnedListHighlightService` 只 Hook `WorkSceneCookingSelectionPannel.OnRecipeElementEnabled/3`、`OnIngElementEnabled/3` 和 `WorkSceneStoragePannel.OnElementEnabled/3`，酒水还必须确认 `Sellable.Type=Beverage`。`RuntimeUiPinningService` 维护排序与全部视觉共用的唯一 immutable target set：集合至多各含一个 rare 和 normal 目标，固定 rare 后 normal；每个目标携带自己的 `ListPinningEnabled / RecipeVariantEnabled / CookerHighlightEnabled / SeatHighlightEnabled / OrderHighlightEnabled`，集合级不再保留公共功能位。各服务只消费本目标已开启的功能，不得在视觉服务中保存第二份业务目标。Local API 工作线程只能原子发布 immutable desired state，不得访问 Unity 对象。列表 Image、厨具 renderer 和其他 owned 视觉的着色、恢复及协调只能在元素回调、`Tick` 或 `LateUpdate` 主线程执行。制作页只安装 `OnPanelOpen` 与 `OnPanelClose` 生命周期 Hook，并在每次主线程 Tick 先精确检查已登记 wrapper pointer；`WorkSceneCookingSelectionPannel.OnPanelDestroyed` 与 `WorkSceneServePannel.OnPanelDestroyed` 在当前游戏中别名到同一空原生 RVA `0x54D310`，UI pinning、列表高亮和加料料理服务均禁止 detour 该入口。酒水仓库页拥有独立非空实现，其 Destroy Hook 保持不变。进入 Closing 时恢复仍可安全访问的对象并挂起服务；Destroyed 后只丢弃 wrapper。只有新的 Active generation 能恢复服务，网络重发不能复用上一场目标。

制作页加料选项服务是唯一变体入口；旧的基础料理隐式加料服务和 `recipeId -> extras` 冲突解析不得保留。服务只为 `RecipeVariantEnabled=true` 且 extras 非空的目标生成选项：同 recipe ID、同 ordered extras 合并 claims，不同 extras 按 rare、normal 稳定分行。每个加料选项使用独立 native Recipe identity，并保存到权威基础 Recipe 和精确目标方案的映射；基础行始终代表原配方，不得因同 ID 的加料目标被隐式改写。

选择加料选项时，只能把该行映射回权威基础 Recipe，并为紧随其后的原生导入刷新登记一次精确事务。事务同时保存不可变的 origin identity、权威 Recipe 和被点击 synthetic Recipe 的 native pointer；后续 recipe surface 绑定可以换到新的 target snapshot，但不得改写 origin identity。扣料前 fresh 证明当前列表中这两行各自唯一，recipe ID、完整 `base + ordered extras` 和 CookCount 全部一致，并复核 panel、button、经营 generation、target revision、当前基础材料、五格上限、自由烹饪、倍率和库存；扣减库存、写入选中材料与最终目标复核必须处于同一 target publication lock。列表注入时，某方案对应 `0` 个权威 Recipe 表示当前厨具不适用，跳过该方案；精确 `1` 个才插入，超过 `1` 个 fail-closed。其他适用方案仍可正常生成。列表读取或 surface 插入失败只退休本轮 recipe surface，不得直接锁存 mutation uncertainty；只有 Insert 已调用但结果无法确认时，才按 business + target generation 记入 insertion ledger 并禁止重放。列表注入、CookCount、行重建、迟到视觉诊断及 submit/output preflight 只读取 selection-only state，不得依赖已被刷新消费的 imported Recipe。完整 imported Recipe 只在 `UpdateAllVisual` prefix 内用于初次原生基础配方导入、追加 extras 后的同次复核和换菜 staged receipt。Recipe 行跨 epoch 复用物理按钮时只原子替换 Mod 的 exact lease，不清游戏 submit callback，也不写 `interactable`。物理按钮只有在 fresh 证明仍属于旧 Mod output exact closure 时才清理旧 output ownership；否则只退休 managed owner。

已发生加料副作用后，移动到另一料理只登记 selection intent，不退料也不结束事务。实际提交基础料理、普通料理或另一加料项时，`CallSubmitAction` 的 exact 同步作用域登记 switch attempt，并允许游戏原生 callback 依次退回旧 selected ingredients、扣除新基础材料、写入新 selected ingredients 和 imported Recipe。`UpdateAllVisual` prefix 只提交 staged receipt：fresh 证明新 imported/base/selected、倍率、重复材料、有限或 `-1` 库存和自由烹饪结果；另一加料项此时建立新 sequence 并只追加一次自己的 ordered extras。`UpdateAllVisual` finalizer 正常返回后状态为 `VisualCompleted`，只有最外层 `CallSubmitAction` finalizer 也正常返回才把旧事务以 switched 原因提交为 `Cancelled`。成功日志依次为 `Target recipe variant switch-armed -> Target recipe variant switch-receipt -> Target recipe variant switch-cancelled`；callback 未消费为 `Target recipe variant switch-armed -> Target recipe variant switch-unconsumed`；失败为 `Target recipe variant switch-armed -> Target recipe variant switch-uncertain`，已观察 staged receipt 时则在两者之间包含 `Target recipe variant switch-receipt`。`switch-receipt` 只表示 staged receipt，不代表整次换菜成功。任一原生异常、部分副作用或收据漂移都进入 `Uncertain`，禁止手工退款、补偿、重放或旧指针别名。

`OnOutputSelected` prefix 不得清理当前 callback：原生无效分支会自行清空，有效分支会直接覆盖为本次 output closure，只有正常返回后 Mod 才取得该槽的本次所有权。目标候选必须 fresh 证明 raw MethodInfo pointer 精确对应 `WorkSceneCookingSelectionPannel.__c__DisplayClass79_0.Method_Internal_Void_PDM_0`、Target 为 exact display class，且 closure 的 panel/output/solved 与当前事务一致，才登记输出绑定；非目标或不合格候选只在原生正常返回后 post-clean。post-clean 失败时 finalizer 注入异常，阻止最外层 `CallSubmitAction` fresh 读取并执行错误 closure；原生方法自身抛异常时保留原异常，不清理所有权未知的旧 callback。`CallSubmitAction` 使用线程内严格栈记录 exact active button；普通嵌套不受影响，只有与已登记事务冲突的嵌套被阻断。不能用 `OnOutputSelected` 或 `CallSubmitAction` 正常返回推断料理已开始。按钮 behaviour 非 `2` 时首次点击可先选中再在同一次调用中提交，behaviour 为 `2` 时首次只选中；两种情况都只认 exact final closure 实际进入。

final closure prefix 持有同一 business generation publication lease 并 fresh preflight，不再要求 origin target snapshot 仍是当前引用。游戏闭包会同步调用 `ClosePanel -> OnPanelClose`，因此只有闭包无异常、同一 closure/panel/output/solved/transaction sequence 的 close receipt 已登记，且保留事务仍为 `OutputSubmitting` 时才记为 `Completed`；缺少任一证据都记为 `Uncertain`。非 final 手动关闭在 `Applied/OutputPending/OutputReady` 后正常返回时，以原生 `TryReturnSelectedIngredients` 为退料收据记 `Cancelled`，重开后只能建立新 sequence；`Applying` 中关闭或关闭异常记 `Uncertain`。`OutputSubmitting` 只认 final close receipt。库存扣除或选中材料写入的结果一旦不确定，本场经营锁存所有加料选项；当前页面停止继续选菜，关闭页面并核对库存与五格材料后，重新打开时基础料理仍走游戏原生路径，加料选项到下一经营 generation 才恢复。页面关闭、经营边界或目标刷新都不能清除当前 generation 的锁存，也不得重放。Mod 不直接调用或重放 `SetCook`、`StartCookCountDown` 或关闭面板。

Hook 必须覆盖列表构造、元素绑定、选择、原生导入刷新、全局 `CallSubmitAction` active-button 关联、exact output final closure 和面板 close，共 8 个精确入口；明确不得 Hook 共享空原生别名 `WorkSceneCookingSelectionPannel.OnPanelDestroyed`。选择与提交门禁必须先于插行启用；任一必要 Hook 不完整时禁止生成加料选项。目标变化时，`UpdateIngField` 先更换食材分类，`UpdateRecipeField` 再更换 recipe data/surface epoch，随后 UI pinning 依次重建当前食材行和料理行，但不重建已选材料区或 output surface。主设备或生效 profile 变化先发布 `authority-fence-preserved` 空 operational target，立即阻断旧 writer 与旧目标的新料理选择；已打开页面仍持有最后确认的 presentation target，主线程 Tick 跳过该 fence generation，自然刷新也只能消费所持 presentation target。新权威首次发布完整目标（即使确认空集合）时强制生成新 generation，再执行一次窄刷新；不得调用经营边界的 `Abandon` 或事务退休，也不得用 presentation target 获取 exact target publication lease。稳定 output transaction 的最终游戏内闭包仍按原有 business-generation lease 收口。稳定 `Applied` 事务在 fresh 权威 Recipe 与 selected-ingredient receipt 一致时保留同 sequence；稳定 `OutputReady` 还须 fresh 证明 output button、closure 与 output receipt 精确一致，随后可跨 target snapshot（包括空集合）保留。完整 `UpdateAllVisual` 会重建 output surface，因此 outer prefix 必须先 tombstone 稳定 `OutputReady` 的旧 closure/final callback、清空输出标量并退回 `Applied`，再允许原生 `UpdateRecipeField` 运行；只有 exact armed switch 可把这次撤销交给自己的 switch receipt。`Applying`、`OutputPending`、`OutputSubmitting`、`Switching`，target snapshot 变化发生在活动 switch attempt 内，或任何 authoritative/selected/output receipt drift 时，都转为 `Uncertain` 并锁存本场；同一 target snapshot 内的活动 switch attempt 继续按 exact identity 随正常 `UpdateAllVisual` 转移。已退休页面若仍保留非终态事务，后续刷新必须在 synthetic Recipe 分配和原生 `List.Insert` 前以 `pre-insert-retired-rejected` 停止，不能把 managed identity 冲突误记成插入结果未知。不得用 surface 刷新失败代替这些精确门禁。`Uncertain` 只保留锁存且不可执行。只有物理输出按钮使用 `AwaitingRebind`、单调 cleanup lease 和 fresh binding sequence；Recipe 行不得复用该清理协议。不匹配输出候选只在原生正常返回后 post-clean；清理失败、owner 漂移或不稳定事务 fail-closed。经营 Closing/Destroyed 由唯一生命周期服务调用 managed-only 边界：只退休 exact business generation 的 panel，只删除 `panelEpoch <= 当前退休实例 epoch` 的旧 button/closure owner，并把已 mutation 的非终态事务及活动 switch attempt 标记为 `Uncertain`；迟到旧 generation 不得清除更晚 ABA 实例。不得使用名称、文本、路径、列表索引或 `recipeId` 兜底识别，也不得恢复旧的全局基础料理自动加料路径。

outer prefix 的 output reset receipt 只允许通过 ThreadStatic 嵌套 scope 被最近的 exact inner recipe postfix 消费一次。消费须复核同一 PanelState/epoch/transaction、`Applied`、全零 output 标量、旧 `AwaitingRebind` button 和 `Tombstone` closure；若期间出现 fresh output rebind 或同指针 ABA，旧 receipt 单调退休，caller 必须重新 reset 当前 `OutputReady`。嵌套 FullVisual 需沿 parent scope 查找最近的 exact receipt，不得按 panel pointer 和 business generation 宽匹配。

专项验证命令：

```bash
dotnet build tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release -t:Rebuild
dotnet run --project tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release --no-build
```

### 游戏内目标高亮精确契约

#### 共同目标、颜色与所有权

- `RuntimeUiTargetSetSnapshot` 是唯一业务目标根。每次发布原子替换整个集合，最多各含一笔 rare 和 normal 目标，并固定按 rare、normal 排列；两类目标可以同时存在、独立送达和独立失效。每个目标都携带五个目标级功能位、kind、颜色、revision、exact trace、正 lifecycle、0-based desk 和方案内容；normal 还必须携带与原生订单指针一致的 raw `ptr:<lowercase hex>` key。`targetCount=0` 是唯一清空表达；旧集合级开关和未知 query 字段必须拒绝，不能忽略或迁移。
- 颜色只接受六位大写 RGB wire 值。稀客默认 `FFDB2E`，普客默认 `5FACD3`；前端设置使用带 `#` 的形式。自定义颜色统一进入料理/材料/酒水列表、加料料理选项、厨具、桌位、HUD 卡片和投掷送达面板，不在各视觉服务内保存固定颜色。
- 同一个列表项、厨具或桌位被两类目标共同声明时，只建立一份 Mod ownership 和一份原始基线，claims 为 `Rare | Normal`，颜色在两端持续往返且不设置类型优先级。HUD 卡片和投掷送达按钮按精确订单绑定，每个 owned visual 只使用所属目标颜色。关闭、目标变化或生命周期结束必须恢复/销毁同一份 owned 状态，不得叠加第二套视觉。

#### 目标桌位

- `RuntimeSeatHighlightService` 只从根级 `TileManager.onSelection` 取模板。桌位由 exact `GetCustomerDesk`、`Tile.sprite`、`GetCellCenterWorld` 和 `Tilemap.GetTransformMatrix` 定位，clone 保持 scene root，并定位到 cell center 加 tilemap translation。`UIElementCluster.GetObjects<SpriteRenderer>()` 必须恰好返回两个不同且存活的 renderer；角色只按 exact shader 名 `THIZKY/Effects/OutlineBlinkOnly` 与 `THIZKY/Effects/RegionalHSVFillter` 唯一识别，数组顺序不承载语义。两张模板 renderer 均禁用。
- 可见层是单独新建的唯一 `SpriteRenderer`。它与 Regional 同 parent，并复制 local position/rotation/scale、layer、sorting、Simple draw mode 和 flip。BepInEx 783 的 typed query 可返回 managed `Component` wrapper；只允许一次 exact cast，并要求 query/cast pointer 相同、native class 为 `UnityEngine.SpriteRenderer`、owner 是新 fill object。禁止再次 AddComponent、换查询方式或建立备用视觉。
- 每次创建唯一 owned `64 x 64` `Texture2D`：`RGBA32`、sRGB、无 mipmap、保持 readable；用恰好 4096 个不透明白色 `Color32` 调用一次 `SetPixels32`，再调用一次 `Apply(false, false)`。owned Sprite 只从该纹理和 source `rect/pivot/pixelsPerUnit/vertices/triangles` 创建，mesh 必须为 `Tight`。source 顶点按同一比例 letterbox 到完整 `64 x 64` pixel rect，pivot 与 PPU 同步换算，再以原 triangle indices 调用 `OverrideGeometry`；顶点和索引上限分别为 4096 与 12288，输出几何及 bounds 必须与 source 一致。Unity 2021.3 会把任一边小于 32 的 Tight Sprite 退化为不可覆盖的 FullRect，因此禁止 `2 x 2`、`Texture2D.whiteTexture`、`FullRect`、重试、双 Sprite 和任何 fallback。
- 新 renderer 的默认 `Sprites/Default` shared material 只读。唯一材质实例化入口是 `renderer.material`；owned material 必须与默认材质 pointer 不同并使用同一 shader。默认材质只在创建事务中验证，不跨帧保存。禁止 `Shader.Find`、`UI/Default`、`new Material`、Regional rebind、共享材质写入、source `Tile.sprite` 可见填充，以及读取或清空 `MaterialPropertyBlock`。
- 每个物理桌位只发布一个完整 `ActiveSeatVisual` owned 资源组；创建失败和健康失败成组清理 selection clone、fill object、owned texture、Sprite 与 material。目标切换、禁用、Closing 和 Dispose 在主线程销毁仍存活资源；off-main 或 Destroyed 只 abandon wrapper。source Sprite/texture 和 Unity 默认材质永不销毁。逐帧按该桌位 claims 和当前 palette 写入 `0.45..0.70` alpha 脉冲；两类目标共桌时在两色间往返。低频健康检查复核整组 identity、几何、层级和渲染基线。
- 成功与失败日志有界。成功日志只使用验证过程中已取得的 generation、desk、texture/mesh 和 geometry 等托管标量；不得为了诊断再读取 Unity getter，也不得让日志决定创建或健康结果。专项测试必须覆盖小纹理退化、`64 x 64` Tight 成功、非方形 rect、非中心 pivot、非 1 PPU、负顶点、像素/Apply/OverrideGeometry 异常、exact cast、identity/geometry 漂移、partial cleanup、两种 renderer 顺序和全部生命周期。

`RuntimeOrderHighlightService` 在 BepInEx 783 上只用精确 `OrderingElement.Initialize/5` prefix 清除池化卡片的旧登记和 overlay；该方法返回时游戏尚未写入 `ActiveOrder`，禁止用 postfix 登记。唯一登记边界是 `OrderController.CreateOrderingElement(OrderBase) -> OrderingElement` postfix：原方法成功返回后，严格读取 `__result.ActiveOrder`，要求它与参数 `__0` 都有非零且相同的原生指针，再读取 exact `OrderBase.DeskCode` 并登记该卡片；`OrderingElement.Out/0` 和 `DestroySelf/0` 仅处理正常退出。`OnDestroy/0` 的 RVA 是大量无关 `MonoBehaviour` 共用的空桩，`ActiveOrder` setter 也不是允许的观察边界，两者均禁止 detour；意外销毁由低频健康检查识别。稀客目标只接受不经 Trim 的 exact `R-` 加 1 至 16 位 ASCII 数字，并在当前 generation 的活动 `SpecialOrderRuntimeCapture` 中唯一解析；普客只接受同形态 `N-` trace，同时精确匹配 raw order key、正 lifecycle、desk 和活动 `NormalOrderRuntimeCapture`。两类都必须把解析出的非零原生订单指针与唯一已登记卡片的 `ActiveOrder` 复核。卡片高亮只能复制组件恰好为 `RectTransform + CanvasRenderer + Image`、`childCount=0` 的 `borderStyleImageForCurrent` 根对象；typed query、native pointer 集合、transform/Image owner 和无 `LayoutGroup` parent 必须闭合。合法 clone 禁用 raycast，并按所属目标颜色脉冲。不得添加 `LayoutElement`、建立替代视觉、调用 `ChangeBorderStyle` / `TryFocusToOrder` / `SetHighlight`，或按桌号、名称、Tag、内容、列表位置兜底。任一 Hook、capture、trace、key、lifecycle、指针、DeskCode 或视觉形态不精确时只让该订单视觉 unavailable；Closing 销毁仍有效私有对象，Destroyed 只遗弃 wrapper。

#### 投掷送达面板目标订单

- `RuntimeThrowDeliverOrderHighlightService` 只观察 exact `NightScene.UI.HUDUtility.WorkSceneThrowDeliverPanel.OnPanelOpen/0` First prefix + Last postfix 和 `OnPanelClose/0` First prefix。open prefix 在原生重建前清理旧 binding；postfix 登记重建完成的 live panel，与当前目标是否启用无关。close 只有在回调 `__instance` 的 nonzero native pointer 与当前已登记 panel 完全相同时才清理；迟到的旧 panel close 不得关闭新面板。off-main close、off-main dispose 和 Destroyed 都只 abandon wrapper，不调用 Unity API。
- 视觉绑定每次从当前正式目标重新开始。稀客使用 exact `R-*` + 活动 rare capture；普客使用 exact `N-*` + raw order key + 活动 normal capture。二者都要求正 lifecycle、0-based DeskCode、唯一 order/controller pointer，并依次复核 `m_Data[deskCode]` 的 exact `(Vector3, OrderBase, GuestGroupController)` tuple、closed `m_Group.m_Children.TryGetValue(deskCode, out UILogicalUnit)`、unit `RectTransform` 和 `m_BtnInstances` 的唯一 live pool membership。panel 登记与 scalar binding 分离；跨帧不保留 order/controller wrapper。BepInEx 783 的 typed unit query 可返回同 pointer、同 owner 的 generic `Component` proxy，实际 native class 仍可为 `UIButtonSimple` 派生类；exact cast 后只要求 query/cast/既有 unit pointer 和 button owner 一致。
- selection 只由 serialized listener 证明。declared `m_OnSelectionUpdateCallback` 必须是 exact `AdpUISystemUtils+UnityEvent_Bool`，direct base 是 closed `UnityEvent<bool>`，再上层是 exact `UnityEventBase`。只沿 `m_PersistentCalls -> m_Calls -> concrete List<PersistentCall>` 读取，数量必须恰好为 1。唯一 listener 必须 live、method 为 `set_enabled`，target pointer 唯一对应 button 下 exact leaf `Image`。服务不调用、添加或删除 listener。
- selection 必须是 button direct child；anchors、pivot、anchoredPosition、sizeDelta、offsetMin/Max、localScale 和完整 Rect 均有限，scale 三轴非零，Rect 宽高为正。排除 selection 和既有 owned fill 后，button direct children 中必须恰好有两张同 parent、leaf-only、组件精确为 `RectTransform + CanvasRenderer + Image` 的背景。两张背景与 selection 的上述几何字段逐项 `==`，不使用容差；二者均 active、Sprite identity 不同、共享同一 Material 与 exact Image type，且 enabled 严格 XOR。名称、文本、Sprite 名、颜色、path 和固定 index 不参与选择。
- 当前 enabled 背景是唯一模板。clone 必须保留 exact Sprite、Material、Image type 和完整 RectTransform 几何，设为 enabled、active、non-raycast；两个原生背景的相对顺序不变，owned fill 位于其后并严格满足 `fillSibling + 1 == selectionSibling`。颜色只写 owned Image，并使用该 exact 订单所属目标的 palette 颜色；原生背景、selection、CanvasGroup、logical state 和事件均只读。模板与 clone 都要闭合验证 exact 三组件、native class、owner、资源、几何和 sibling identity。
- 每 0.25 秒 fresh 重取 unit、listener、背景和 owned fill。背景 XOR 切换时，只有旧 fill 可安全退休后才能从新 enabled 背景重建。每帧 SetColor、SetActive 或 Destroy 前都 fresh 复核 owned GameObject/Image pointer 及 Image owner；identity 漂移、对象已 dead 或无法证明 ownership 时只 abandon，不能触碰可能已池化重绑的对象。target 关闭、匹配的 panel close/rebuild、Closing、Suspend 和主线程 Dispose 才销毁仍存活且 identity 精确的 clone。
- `fill bound`、失败和正常退休日志分别有界；成功日志只包含验证阶段已经取得的托管 scalar/fingerprint，不为诊断额外读取 Unity getter。专项测试必须覆盖 Normal/Special、同桌新 trace、面板内目标变化、generic wrapper、pool rebind、非 canonical 但逐项相同的合法几何、每个字段漂移、非有限值、零 scale、非正 Rect、背景 XOR 切换、stale close、partial failure、安全退休和全部生命周期。
- 禁止 sibling-0 underlay、last-sibling 蒙版、四边框、原生改色、HUD 模板回退、旧 ServePanel、场景扫描、名称/文本/path/index 兜底、共享 selection Hook、`AddChild`、`SpawnPooled`、`OnPanelDestroyed`、双视觉、兼容 fallback，以及任何打开、聚焦、提交或评价副作用。

夜间 live 厨具控制器只从 `CookSystemManager.get_AllCookers` 返回的精确 `Dictionary<Vector3Int,CookController>` 读取，具体字典通过 `RuntimeConcreteCollectionReader.TryReadDictionary` 枚举；开锅选择、已摆放厨具快照和厨具高亮共用该读取结果。每个控制器的完整能力读取 `Cooker.AllAvailableCookerType`：按 BepInEx 783 的精确 `IEnumerable<CookerType>` 形态使用泛型 `Current`、非泛型 `MoveNext` 和 `IDisposable.Dispose`，不能在读到基础 `Cooker.Type` 后提前返回。`AllCookers` 的固定空位只有在 `CookController.IsEmptyDesk=true`、类型序列为 Empty-only、phase 空闲且无结果/配方时才成立；非空控制器必须至少有一个 1-5 能力。不得恢复 `AllCookerControllers` 的通用对象枚举、字典内部 slots 读取或 Unity 场景扫描。

物理厨具快照只允许 complete 和 unavailable。每轮先读精确 `EventManager.get_LockedCookers()` 坐标，再将该集合传入 `get_AllCookers` 读取。锁定 key 只校验字典坐标、native pointer 和唯一性后计入 `placedCookerLockedControllerCount`，不调用可能失效的 controller `get_GridPosition()`、`CouldOpen`、内容、能力或渲染器 getter，也不发布为 `placedCookers`、类型或容量。非锁定条目仍必须精确校验字典坐标与 controller 坐标一致、native pointer/坐标唯一且 `CouldOpen=true`。精确空位计入 `placedCookerEmptyControllerCount`，不发布为容量。complete 响应要求 `placedCookers + emptyControllers + lockedControllers == controllerCount` 且失败为零；unavailable 响应要求 placed/type/empty 为空且 `lockedControllers + readFailures == controllerCount`，锁定计数只作诊断。unavailable 不参与展示推荐的厨具过滤或类型推断，但自动化容量、写操作与高亮整轮 fail-closed。complete 且 locked 计数大于零时，只有存活非锁定条目证明的类型保持可用，其他类型作为事件期间独立硬门禁。Local API 和前端必须严格校验两类计数闭合，只对已发布的非锁定条目校验三重身份、`challengeLocked=false`、`couldOpen=true`、类型投影和自动化分类。

快照与实际开锅选择必须共用 `RuntimeCookerStartAvailabilityService` 的完整分类，不能把 `CouldOpen` 单独解释为自动化可用。只有严格空闲，或原生 `Extract` 已完成且所有权证据完整的配方残留控制器，才能发布 `automationAvailable=true`；忙碌、锁定、未完成 mutation 或残留所有权不可读均为 unavailable。前端按 `controllerIndex + controllerIdentity + gridPosition` 构建实体槽位池并与普客、稀客共用；多类型控制器同一轮只能预约一次，优先选择支持类型更少的控制器以保留多类型槽位。需要开锅的写请求必须同时传递本轮预约的 `cookerControllerIndex`、规范原生 identity 和三维 grid；后端 fresh-read `AllCookers` 与 `LockedCookers` 后复核三者、类型、Mod 预约、共享可用性分类和 `CouldOpen`/lock 一致性，并在紧邻 `SetCook` 前再次执行相同复核。任何漂移都进入局部等待，不扫描或改选其他控制器。不开锅请求不携带预约且必须明确关闭开锅阶段。需要开锅的候选必须在厨具准入后才消耗并发，队头订单缺少可用类型时继续扫描后续候选，不能阻塞其他可执行类型；不得再按类型计数制造最小容量或把未读控制器推测为槽位。厨具已存在但被 Mod 预约、游戏占用或特殊经营暂时禁用时，开锅入口返回可重试等待并分别记录数量，不把它误报为“没有读取到任何厨具”。

夜间经营中，`经营中 / Service` 页的稀客正式订单集合只使用 `SpecialOrderRuntimeCapture`。捕获只能由成功返回的 `GuestGroupController.PushToOrder` 或 `SetManualControllerOrderInternal` 建立精确 order/controller/native-key 绑定；七个必需生命周期 Hook 必须全部安装，且当前经营 generation 从开始时已被完整覆盖。若 Hook 在经营中途才补齐，本场保持 fail-closed，下一场才开放；捕获已就绪但为空时，空集合就是权威业务结果。`GuestsManager`、稀客队列、`OrderController`、HUD、服务面板和桌位控制器的订单样本只用于诊断，不能补稀客业务订单。页面仍会读取桌位控制器中的活动稀客，用于显示当前稀客和资金。普客在捕获就绪后以 `NormalOrderRuntimeCapture` 的精确捕获作为业务和执行权威；`OrderController` / HUD 只追加没有捕获绑定的不可执行可见行，同一非零 native `orderKey` 与捕获去重，单轮 HUD 空窗或错误不得过滤捕获。稀客和普客动作前都用 `PeekOrders()` 复核同一 controller 当前栈顶及原生指针身份；完成定位允许 `IsFullfilled=true` 进入评价。

`GuestGroupController.AllOrders` 与 `AllOrdersData` 指向同一只累积历史订单的 `Stack<OrderBase>`；它们不代表活动所有权，Provider、普客快照、诊断业务判断和自动化不得枚举该历史栈。捕获就绪所需的七个精确 Hook 是 `PushToOrder`、`SetManualControllerOrderInternal`、`RemoveFromOrder`、`EvaluateOrder`、`EvaulateManualOrder`、`CleanOrderInfo` 和 `RepellInternal`。订单创建只在前两个 Hook 成功返回后提交绑定；key 只接受非零 IL2CPP 原生对象指针，不得回退到 managed hash，状态与 UI 回调只能按同一 key 更新既有绑定。每次成功创建都会为 generation + concrete kind + order/controller pointer 分配独立、进程单调的 lifecycle sequence；业务快照、运行时事件与两类订单动作请求必须携带该正数序列，任何副作用前要求请求、fresh capture 和 active store 三者完全一致。标准/手动评价只在调用前订单已 fulfilled 且原方法成功后发布精确终态；`RemoveFromOrder` / `CleanOrderInfo` 在成功返回后发布移除终态；成功返回的 `RepellInternal` 无论 `out haveSeated` 为何值都发布调用前生命周期的移除终态。`EndDlc4SpecialManualOrder` 只移除 arrival event，不是订单退出边界。动作前复核只读取 `PeekOrders()` 当前栈顶；返回的 `OrderBase` 必须通过共享解析器得到唯一具体包装。一般自动化定位不再扫描 manager 回退，只有古明地恋 BOSS 与幽幽子三阶段两个显式专用路径可在各自额外门禁下读取当前 manager controller。稀客订单身份必须读取 `SpecialOrder.RequestFoodTag` / `RequestBeverageTag` 的原始数值，并与桌位、运行时原始稀客 ID 组成强身份。`-1` 等合法负数必须原样保留，展示只由规范运行时 signed Tag map 生成，不调用订单文本 getter，也不参与匹配。同一 native slot + lifecycle 内任一 raw ID 与捕获值冲突时立即移除 capture、失效 active lifecycle 且不发布终态 receipt；本次读数不得成为替代 identity，直到成功的新原生创建绑定产生新 lifecycle。

幽幽子第三阶段的 NativeEvaluation 必须 fresh read 精确捕获的 order/controller，并在调用原生评价前重新校验强身份、`PeekOrders()` 当前所有权、经营 generation、fulfilled、已送达目标与同订单回调绑定；只有 `SpecialOrder` 的专用复核失败时，才扫描 `GuestsManager` 当前集合并执行同一验证器，`NormalOrder` 不使用 manager 回退。剧情版的 `onEvaluate` 必须来自最终选中的同一原生 order/controller 捕获记录，不能只按相同请求身份借用其他记录的回调。重修版逐订单选择入口：精确 setter 建立的 `ManualOrderSet` 绑定在活动订单内保持到移除、过期或经营代际清理，不被后续瞬时 `ManualOrder=false` 覆盖；空回调、不同回调或来源冲突均不可执行。稳定绑定的 `DisplayClass16_10 + b__77/b__78` 与主幽幽子 `_50` 同时成立时调用 `EvaulateManualOrder`；明确无手动绑定、具体订单已唯一解析为 `NormalOrder` 或 `SpecialOrder`，且仅有组订单 `_70`、没有 `_50` 时调用 `EvaluateOrder(controller, false, null)`。未知或冲突组合 fail-closed，不能换入口重试。重修门禁还必须按实际订单形态分流：稀客 `SpecialOrder` 校验请求料理/酒水 Tag 存在于实送完整 `Tags`，按标准点单喜恶评价，不读取 `expectedFoodModifierTags`；精确料理/酒水 `NormalOrder` 才校验实际加料和 `Tags.Except(RawTags)` modifier Tag，其中只排除 `SparrowSeries` 原生 signed 厨具来源标记 `-30`，不得按本地化名称或“负数 Tag”泛化过滤。不得用字段为空、预测 Tag 或等级合计规则在两种形态之间兜底。古明地恋本体继续要求 manager 可发现的 live controller，不使用本段 capture 优先策略。

稀客推荐结果会按角色、点单词条、库存状态、厨具快照、排序配置、置顶开关、同基础料理展示数量、自动化收藏限定和加料上限缓存。经营中先对完整候选应用硬过滤，再依次应用任务料理置顶、自定义置顶、收藏料理/酒水置顶和普通权重，构造执行计划；`executionPlans[0]` 是订单唯一主执行计划，再把该计划的料理和酒水投影为页面首项。exact active ServeInWork 任务料理可以跳过普通 food Tag 与料理厌恶判断，但配对酒水和全部硬门禁仍须通过。同基础料理展示数量只裁剪其余行。自动化和每个游戏界面目标只消费所属订单的主计划，不得扫描后续计划；目标集合分别选择至多一笔稀客和一笔普客，稀客槽在普通经营中优先尚未送达的任务计划，普客槽不因此被替换。收藏限定只在自动化总开关、对应料理/酒水阶段和当前订单自动化权限都开启时参与主计划归一化，并且必须在执行计划数量截断前处理；找不到满足限定的方案时保留推荐展示，但对应自动化动作不执行。自动化锁定后，即使开锅或送酒引起库存重算并改变页面主计划，也继续处理原锁定目标。自动刷新没有检测到算法相关变化时，不会在每个刷新周期重复枚举加料组合；`lastSeenAtUtc`、诊断来源和显示标签不得进入推荐语义签名。页面另用连接代际、自动化会话、经营 generation/lifecycle、特殊经营语义和数据签名建立硬上下文，同上下文内按订单强身份保留上一轮展示，新订单局部显示 pending，送达/需求/上下文变化立即失效；该投影只供渲染，动作路径仍只使用 Worker current 结果。所有影响主计划的设置都必须进入缓存签名。只有最终没有执行计划时才生成 `blockedDiagnostic`，并复用正式候选管线记录料理、酒水、预算和特殊评价各阶段的首个清零位置及资源证据；该诊断不参与业务缓存身份、候选选择或排序。幽幽子二阶段没有可预测 `ExGood` 的完整组合时保持无计划，不得降级执行。

收藏数据由 Mod 本地 API 持久化到 `BepInEx/config/MystiaStewardCompanion/favorites.json`。前端只通过 `/favorites`、`/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` 读写，不使用 localStorage 存储收藏，避免版本更新或 WebView 数据迁移时丢失。

如果没有检测到运行时数据，普客和稀客推荐页只显示运行时数据不可用，不会回退到“全内容可用”状态，避免误以为库存和解锁内容已经同步。

开启总日志后，经营扫描会额外输出 `night-business` section，其中包含 `Candidates` 和 `RecentRuntimeParseFailures`。前者记录被扫描到的 controller/order 候选、接纳状态和过滤原因；后者记录运行时订单捕获器最近未能解析为稀客订单的样本。排查映射稀客或特殊事件稀客时，优先查看这两段。

诊断采样不得改变正式业务输入。稀客推荐和自动化只使用当前经营 generation 已就绪的精确捕获；即使捕获为空，也不得用反射/HUD/controller 订单补充。普客捕获就绪后同样以精确捕获为业务和执行权威；HUD/controller 只追加不可执行可见行，同 native `orderKey` 去重，单轮缺口或读取错误不得裁剪捕获。额外样本只写入 `night-business` section。

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
- `GET /missions/available?knownSignature=...`：每个请求排队到 Unity 主线程执行 fresh read，发布已由新版反编译资料与现有实机日志交叉确认的 `OnEnterDaySceneMap=0`、`OnEnterDayScene=1` 与 `KizunaCheckPoint=5`，来源可为 `postMissions` 或 `postMissionsAfterPerformance`。四个 exact scheduler Hook 与既有 Initialize/StartMission 捕获链只保存托管来源身份，覆盖事件被移出调度桶到任务 Started/Retired 之间的短暂窗口。读取严格复核 `preNodes`、active labels、`loopedMission`、finished history、任务 generation、单调 `sourceRevision` 和 mapped identity snapshot；不再以 day-scene readiness/day generation 阻断业务读取。完整响应使用 `missionGeneration + sourceRevision`，每项返回有限的 activation mode/status、trigger kind、source timing 与 hint；无 receiver 合法。任务展示元数据仍使用同一 receiver 和静态相关场景协议，相关场景不表示角色实时位置；展示运行态未就绪只返回 pending 展示状态，不改变业务资格。`knownSignature` 只压缩响应，不能省略 fresh read。端点不复用 frozen scheduled 诊断，不调用事件触发、任务接取或其他推进方法。
- `GET /automation/lease`：读取当前 automation control lease 状态。
- `GET /logs/settings`：读取总日志开关、总日志路径、单文件分片大小、文件上限、总容量上限，以及 BepInEx 控制台的平台支持、启动设置、活动和可见状态。
- `POST /logs/config?aggregateLog=true|false&aggregateLogMaxFiles=30`：由伴随窗口回写总日志开关和文件上限；`aggregateLog` 会即时注册或移除 BepInEx 全局日志监听器。
- `POST /logs/console?visible=true|false`：仅允许游戏电脑回环客户端调用。显示时使用 BepInEx #783 `ConsoleManager.CreateConsole()`，以调用前后 `GetConsoleWindow()` 是否从空变为非空判定 Mod 本次新建窗口，并仅对该窗口删除 `SC_CLOSE`、补充唯一 `ConsoleLogListener`；driver active、窗口存在和真实可见性分别读取，隐藏及失败回滚只按真实窗口可见性调用 Win32 `ShowWindow(SW_HIDE)`。显隐、`Diagnostics.ShowBepInExConsoleOnStartup` 和响应快照在同一服务锁内提交，不得 `DetachConsole()`、改写全局 `BepInEx.cfg` 或影响总日志开关。
- `POST /logs/open-folder?target=aggregate`：打开总日志目录。
- `POST /inventory/set?type=ingredient|beverage&id=ID&qty=数量`：在 Unity 主线程通过 `RunTimeStorage` 原生 Range API 修改当前运行时材料或酒水库存，并回读校验最终数量；原生调用失败时不会绕过 callback 直写私有字典。
- `POST /inventory/bulk-set?type=ingredient|beverage&ids=ID1,ID2&qty=数量`：批量修改当前运行时材料或酒水库存；用于修改页的材料/酒水批量设为 `99`，只在批量结束后刷新一次运行时快照。
- `POST /orders/prepare-next?...`：按伴随窗口传入的稀客订单执行准备步骤，可组合送达酒水、开始料理、出锅后直送和收藏限定；调用方必须持有 automation control lease。请求必须携带快照公开的正数 `orderLifecycleSequence`，并在任何游戏副作用前与 fresh capture 和活动生命周期精确一致；同时必须携带 nullable `runtimeGuestId`、`foodTagId`、`beverageTagId` 原始数值身份，归一化 `guestId` 和 `foodTag` / `beverageTag` 只用于推荐、展示和诊断，不能作为对象匹配兜底。
- `POST /logs/export-diagnostics?open=true`：生成诊断 zip，包含 manifest、当前 snapshot、运行时目录、只含托管状态的 `snapshot/runtime-mission-diagnostic.json`、首次稳定日间采集的 `snapshot/runtime-scheduled-event-diagnostic.json`、`snapshot/runtime-mission-serve-in-work-diagnostic.json`、最近一次 `GET /missions/available` 形成的 `snapshot/runtime-available-missions.json`、当前被动来源生命周期 `snapshot/runtime-available-mission-sources.json`，以及总日志分片尾部；`open=true` 会打开诊断包目录。前三个任务文件只用于诊断，available 与 source 文件分别是最近一次业务快照和当前来源状态的托管副本，五者都不能替代实时 API 读取。scheduled/post/active node label 与 nullable Trigger ID 是游戏的不透明精确身份，导出和读取均不得 `Trim()`、大小写归一化或建立修剪别名；node label 必须是非 null 且 `Length > 0`，Trigger ID 要区分 null、空字符串、纯空白、边缘空白和其他原始字符串。finished history 是非 null 的有界原始字符串，采集期间保留空字符串、纯空白、边缘空白、重复项和完整顺序，只用 Ordinal membership 判断，不将历史列表投影为业务数据。scheduled 报告另外包含事件级 `eligibility`、eligible/ineligible/not-applicable/excluded 四类计数、单独的 eligibility failure 计数，以及每个任务引用对应的 source-event eligibility；这些字段与 structural candidate 分开，资格读取失败也不会伪装成定义失败或抹掉已读结构证据，整份报告仍会 fail-closed。它不能直接作为业务可接取列表。scheduled 诊断失败不影响 tracked 任务、任务料理主计划、推荐、自动化、置顶或高亮。
- `POST /orders/complete-first?...`：按伴随窗口传入的稀客订单确认送达状态；一般订单可补送酒水并在满足后触发评价。血池地狱 BOSS 的最终料理只由绑定精确锅次的 cooking job 处理，并在当前料理送达/完成配置与精确 authority lease 取得 `YuumaSettlement` permit 后进入专用结算；控制暂不可用时保留原锅暂停，恢复后继续，玩家改变厨具内容时才手动交接。该独立完成入口不得绕过 job 补做最终 setter 或评价。调用方必须持有 automation control lease，并携带快照公开的正数 `orderLifecycleSequence`；后端在任何副作用前要求请求序列、fresh capture 序列与活动生命周期完全一致。请求仍沿用 nullable `runtimeGuestId`、`foodTagId`、`beverageTagId` 原始数值身份定位同一订单，不使用归一化 `guestId` 或文本兜底。
- `POST /orders/rare/dismiss?...`：按桌号及已知的 `runtimeGuestId` / 原始 Tag ID 全维度匹配并删除运行时稀客订单捕获缓存；缺少桌位或全部原始身份时拒绝删除，避免跨订单误清理。
- `POST /orders/normal/complete-first?...`：按请求中的订单 key、桌位、原订单目标和实际执行目标处理一笔普客订单；调用方必须持有 automation control lease，并携带快照公开的正数 `orderLifecycleSequence`。后端在任何游戏副作用前要求请求序列、fresh capture 序列与活动生命周期完全一致。一般普客可按 `autoNormal*` 阶段配置送达酒水、开始料理、出锅后直接送达料理，并在订单 `get_IsFullfilled()` 为真后调用 `EvaluateOrder()` 完成评价；显示在普客区域的血池地狱 BOSS 订单由绑定精确锅次的 cooking job 和当前 `YuumaSettlement` permit 独占处理，控制暂不可用时保留成品暂停，该普通完成入口不得重复提交最终料理或评价。`IsFullfilled` 只表示订单已满足并可评价；前端可以把后续 fresh snapshot 中的 `HasEvaluated`、精确终态回执或订单消失作为外部收敛信号，但 Mod 自己发起一次原生评价后，只接受该调用期间同步发布的同 lifecycle `Evaluated` receipt 证明本次提交成功。若订单只存在于 HUD / `OrderController`，但没有可执行 `GuestGroupController`，后端必须拒绝自动送达并返回不可执行诊断。
- `GET /rare-guests/invitations?scope=current|all`：排队到 Unity 主线程执行严格纯读查询，返回候选、当前已邀请列表和禁用原因，不接受 POST。BepInEx 783 将目标原生静态成员暴露为同名托管静态属性，因此候选只从已完成的 base+mapped identity 快照 `Entries` 出发，严格读取并校验 `DataBaseDay.allNPCs` 与 `RunTimeAlbum.RecordedSpecialNPCs` 的 closed generic 字典形态。`RuntimeId` / `RuntimeStringId` 只用于精确定位 base 或 mapped 日间 NPC；取得 NPC 后必须严格验证 `NPC.identity.characterId ==` 已解析到最终基础稀客的 `SourceGuestId`。该 canonical character ID 是候选 API、羁绊字典、`HasNPCInvited` 和 `RecordInvitedGuest` 的唯一身份，同一 canonical 稀客的 base/mapped 形态必须先合并为一个候选；不得用 RuntimeId 查写羁绊/邀请，也不得做 runtime/source 双查或双写。`all` 要求每个 `RuntimeStringId` 在 `allNPCs` 中精确存在，并要求该 NPC 的 `possibleDestinations` 精确引用数组非空，再按 canonical character ID 精确查羁绊。目的地数组只作为“存在日间落点”的结构门禁，不读取 marker、不反查或展示地点名称。`current` 额外严格读取 `RunTimeDayScene.trackedNPCs`，只接受 `trackedNPCs[currentMap][RuntimeStringId]` 精确存在的当前地图候选，并基于已取得对象纯读 `overridePosition`、角色身份、`RunTimePlayerData.ShouldShowSpecialGuestsInDay`、`TrackedNPC.currentDestination.spawnMarker`、`NPC.defaultDestination.spawnMarker`、`openStatus`、`restDays`、`NPC.showTime` 和 `RunTimeDayScene.RemainActions` 重建 IDA 字段级可见性判断。`NPC.identity` 是外层 wrapper 属性，其值为 boxed blittable `SchedulerNode.Character`；内部 `characterIdentity` 只能读 exact public declared field，并严格解释 `Special=0` / `Normal=1`。两个 destination 是 non-blittable wrapper，`spawnMarker` 保持 exact property；不得把这些不同元数据形态合并成 property/field 兼容读取。单个候选的可见性字段读取失败时只禁用该候选并写入诊断，全部当前候选均失败时整轮标记为运行时不可用并进入有限重试。不得调用会转入 `DataBaseDay.RefNPC` 的 `TrackedNPC.ShouldShown`，也不得用 `NPC.Destination.None` 或硬编码隐藏标记替代 `NPC.defaultDestination.spawnMarker`。`DataBaseDay.GetMapLabelFromSpawnMarker` 会对未知 marker 执行抛异常的 `First` 查找，该方法和地点 label 匹配均不得进入候选链，地点展示也不得决定资格。合法缺少羁绊项返回 `kizuna-uninitialized`，不生成记录。列表禁止全量字典枚举、NPC 刷新、tracked NPC 创建、羁绊生成、`RefNPC()`、场景对象扫描和 dummy/组合稀客来源。readiness 未通过时不得解析单例；通过后 `StatusTracker` 只从直接基类 `DEYU.Singletons.Singleton<StatusTracker>.Instance` 读取，`DayScene.SceneManager` 只从直接基类 `DEYU.Singletons.MonoSingleton<SceneManager>.Instance` 读取，类型或属性不精确即 fail-closed，不使用通用单例解析或单对象场景扫描。
- `POST /rare-guests/invite-all?scope=current|all&levels=2,3&expectedDaySceneGeneration=GEN&expectedMapLabel=LABEL`：两项场景身份参数必填；缺少、generation 非正数、主线程开始执行时 generation/地图不匹配，或批处理中任一 `RecordInvitedGuest()` 前 readiness、generation、地图发生变化，都会拒绝继续写入并提示刷新。通过场景栅栏后重新构造最新纯读上下文，按范围、可见性、羁绊、当前等级成功邀请对话和已邀请状态复核，再逐项调用 `StatusTracker.RecordInvitedGuest()`；`levels` 可选，只邀请指定羁绊等级的可邀请项。不得信任前端旧列表，不调用 `DaySceneChatSelectionPannel.InviteSpecGuest()`，不使用 `HasTemptInvited()` 跳过候选，不直接刷客、推进时间或写 `Story.SpecialGuestControlled`。
- `POST /rare-guests/invite?guestId=ID&scope=current|all&expectedDaySceneGeneration=GEN&expectedMapLabel=LABEL`：同样要求发起时的正数日间 generation 和非空地图 label，在 Unity 主线程入口及 `RecordInvitedGuest()` 前复核，再按最新上下文重新校验指定稀客后写入今晚邀请名单；任何缺参或上下文变化都拒绝写入，不保留无场景身份的旧调用方式。
- `POST /automation/lease/acquire`：获取或续约当前客户端的 automation control lease；新所有者取得控制权时会进入新的 command epoch。
- `POST /automation/lease/release`：只允许当前主设备以精确当前 authority revision 释放 automation control lease；服务端在同一权威转换锁内撤销 lease 并推进内部 command epoch，使排队与迟到命令失效，但保留活动 cooking job。响应复用规范 lease DTO，并以 `ok=true`、`owned=false` 表示释放完成。旧 `/automation/cancel`、`target` query、取消 ACK 和 `automationCancellationAppliedEpoch` 已删除，不保留兼容路由或迁移。
- `POST /automation/barriers/ack?sequence=SEQ`：由当前 automation control lease 所有者确认已人工检查对应安全事件；后端按精确 sequence 解除同一订单截至该事件的未确认栅栏。找不到事件或不持有 automation control lease 时不得清除前端人工状态。
- `POST /diagnostics/automation-decision?...`：把伴随窗口的自动化候选决策写入总日志。
- `POST /ui-pinning/targets?businessGeneration=GEN&...`：原子替换游戏内目标集合，是唯一写入路由。顶层只接受正数 `businessGeneration` 和精确 `targetCount=0|1|2`。每个索引 `target{i}` 必须完整提供 `Kind`、`ListPinningEnabled`、`RecipeVariantEnabled`、`CookerHighlightEnabled`、`SeatHighlightEnabled`、`OrderHighlightEnabled`、`Revision`、`Color`、`TraceId`、`OrderKey`、`OrderLifecycleSequence`、`DeskCode`、`RecipeId`、`IngredientIds`、`ExtraIngredientIds`、`BeverageId`、`CookerTypeId`；五个功能位必须至少开启一个，且 `RecipeVariantEnabled=true` 要求同一目标的 `ListPinningEnabled=true`。双目标必须按 rare、normal 排列，单目标可为任一类型。颜色是六位大写 RGB，不带 `#`；rare trace 为 exact `R-` 加 1 至 16 位 ASCII 数字且 `OrderKey` 为空，normal trace 同形态使用 `N-` 并要求 exact nonzero `ptr:<lowercase hex>` raw key。lifecycle 必须为正数，桌位为 0-based 非负数，内容 ID 只以 `-1` 表示缺失，`RecipeId` 与 food ID 分开。端点只接受当前 Active 的正数经营 generation；Closing、Destroyed、上一场请求、旧集合级字段、未知字段、缺字段、多余索引或非法顺序都失败。`targetCount=0` 会在 Unity 主线程恢复或销毁仍安全归属 Mod 的列表、厨具、桌位、HUD 与投掷送达视觉。
- `GET /favorites`：读取收藏料理和收藏酒水。
- `POST /favorites/add-recipe?...`、`POST /favorites/remove-recipe?id=...`、`POST /favorites/add-beverage?...`、`POST /favorites/remove-beverage?id=...`：增删收藏数据。
- `GET /custom-recipes`：读取自定义推荐料理。
- `POST /custom-recipes/settings?enabled=true|false`：切换整个自定义推荐料理功能，不改写单条状态。
- `POST /custom-recipes/update-flags?...`：按 `entry`、`customer`、`recipe` 或 `all` 作用域原子更新单条、分组或全部配方的启用/置顶状态。
- `POST /custom-recipes/upsert?...`、`POST /custom-recipes/remove?id=...`、`POST /custom-recipes/move?id=...&direction=up|down`：新增/编辑、删除和调整同一稀客内的推荐优先级。
- `POST /updates/status`：归并并返回当前更新检查、下载、暂存和安装程序状态，以及 `lastAttemptAtUtc`、`lastSuccessAtUtc`、`nextCheckAtUtc`、`consecutiveFailures`；归并 updater 结果时可能写入或删除状态文件，因此不是只读查询。
- `POST /updates/check`、`POST /updates/download`、`POST /updates/install-on-exit`：手动检查、下载或启动退出安装流程；更新服务按单操作串行。后台调度成功后按配置间隔续检，失败按 15m/30m/1h/2h/4h/6h 退避。Local API 关闭时先阻止新操作，再通过统一生命周期令牌取消自动/手动检查和下载，等待 handler、活动操作及调度器退出；取消检查恢复稳定状态并立即到期。下次启动会恢复强制退出留下的瞬时状态，并只清理下载一级目录内严格符合本服务语义版本/GUID 格式的临时目录。

`扩展功能 -> 稀客邀请` 使用按当前客户端持久化且严格默认关闭的模块总控。模块开启、连接成功、`runtimeLoaded=true`、`runtimeDaySceneReady=true`、generation 为正数且地图有效时，连接代际、规范 endpoint、`current|all` 范围、`runtimeDaySceneGeneration` 和当前地图 label 组成稳定写入上下文；列表读取还要求该二级页签可见。首次开启或进入、范围变化、换图、同地图新 generation、重连和邀请完成后各读取一次，无关快照变化不会重复读取。同一列表身份收到结构化 `ok=false/runtimeAvailable=false` 结果或传输失败时只按 500/1000/2000/4000ms 重试四次；成功或确定性业务结果立即停止，耗尽后保留后端 `error/status` 并等待手动刷新或身份变化，失败响应不得渲染成普通空列表，也不能以未记录尝试形成热循环。关闭模块、离开页签或上下文失效时立即取消列表请求、重试计时器并清空旧结果；Hook 同时使用 `AbortController`、请求 generation 和操作 ID，确保旧列表响应或旧 `finally` 不能覆盖新场景结果。手动刷新只强制重读当前有效列表身份。单独/批量邀请从同一有效上下文固定 `expectedDaySceneGeneration` 和 `expectedMapLabel`，没有有效写入上下文时前端不发 POST；后端场景栅栏负责阻止已经排队但延迟执行的旧写入。已发出的 POST 使用独立于页签可见性的操作身份，切换页签不得清除 busy 或丢弃确定返回，写入结束前模块开关锁定。页面只渲染当前协议中的完整 `candidates`；今晚已邀请名单固定在筛选区之前且不受搜索/羁绊筛选影响，候选按可邀请/暂不可邀请分区。搜索仅影响展示，羁绊筛选同时决定展示与批量 POST 参数。后端整批读取异常记录 scope、读取阶段和完整异常；上下文拒绝或成功结果记录可用的状态与候选诊断，便于从总日志定位实机 IL2CPP 差异。

`任务列表` 与 `稀客邀请` 是 `扩展功能` 下相邻的独立二级页签；两个模块总控彼此独立、严格默认关闭并按当前客户端持久化，不读取旧路径或旧键。任务列表只有在总控开启且自身二级页签可见时才并行读取 tracked 与 available，以连接代际、endpoint、日间代际和任务代际组成各自请求身份；关闭、切换页面、断开连接或身份变化会中止旧请求、停止轮询、清空页面结果并拒绝迟到响应，重新开启执行不带旧签名的 fresh read。任务列表开关不卸载共享被动任务捕获，也不影响任务料理置顶等独立能力。后端 `runtimeAvailable=false` 必须显示对应的明确用户状态，完整技术原因保留在日志、API 和诊断包中，不能渲染为普通空列表。列表使用 `全部 / 可接取 / 可完成 / 进行中 / 待确认` 互斥页签，计数始终包含零值；同 label 重叠时 active tracked 项优先，`全部` 按 `available -> fulfilled -> tracking -> unverified`、中文标题和 label 稳定排序，单状态页签只渲染对应列表。窄屏页签只在自身横向滚动，不允许页面溢出。available 只用于列表展示，不得进入任务料理置顶、推荐、自动化、置顶或高亮。

血池地狱的订单准备/完成请求必须额外携带当前 `specialTargetRevision`。它独立于规范 target signature，只接受运行时同锁发布的正 revision；怪诞料理和空策略使用 `0`。后端不会因 `A -> B -> A` 的 signature 再次相同而接受第一轮 A 的迟到请求。BOSS `NormalOrder` 的受控推进另以 `allowYuumaControlledProgression` 显式传递；请求必须提供完整预测 Tag，且预测确实未满足当前双 Tag，预测已全命中却标记受控时拒绝。该许可不修改 policy/signature/revision，但必须进入 execution target、后端 job 和快照 identity，严格与受控动作不得复用。

### 教学经营自动化门禁

教学经营使用独立的自动化门禁。`RuntimeNightBusinessAutomationGate` 在 Unity 主线程只读取 `MonoSingleton<NightSceneDirector>.Instance.IsInTutorial`；本 generation 一旦确认教学即单调锁存到经营结束，不用第一天、新存档、对话、场景名、订单内容或 `NotChallenge` 猜测。状态不可读时 fail-closed；前端快照门禁仅停止调度并作废迟到响应，后端在命令入口和不可逆边界重新校验。暂停不改写用户总开关、订单状态或推荐/置顶展示。

### v1.3.0 数据边界

`v1.3.0` 已删除 `v1.2.x` 的一次性手动自定义料理迁移、跨存储依赖和旧字段类型。`favorites.json` 只保存正常料理/酒水收藏，`custom-recipes.json` 只保存自定义推荐料理；Local API 启动、只读与 CRUD 均不执行跨文件迁移。需要保留旧手动自定义料理的 `v1.1.x` 或更早用户必须先启动一次 `v1.2.0` 完成转换，再升级到 `v1.3.0`；不为直接跨版本升级恢复旧路径。自 `v1.2.0` 起也不再读取旧 GUID 配置 `com.tyukki.mystia-steward.cfg`。

除 `/health` 外，端点都需要 `X-Mystia-Steward-Companion-Token`。Token 由插件生成并保存在 BepInEx 配置中，同机启动伴随窗口时通过 `--token=` 参数传入 Tauri 后端；A 设备本机设置页可以复制或重置 Token。远程局域网连接时，用户需要在 B 设备伴随窗口顶部连接区手动输入 A 设备的 endpoint 和 token，点击 `连接` 后才开始轮询。Tauri 伴随窗口的顶级导航固定为 `概览 / 推荐料理 / 经营中 / 扩展功能 / 设置`，调试信息开启后额外显示 `日志`；`概览` 内部按 `状态 / 库存 / 操作` 分栏，`推荐料理` 按 `普客 / 稀客 / 自定义推荐料理 / 收藏管理` 分栏，`扩展功能` 按 `任务列表 / 稀客邀请 / 修改` 分栏，`设置` 按 `窗口 / 连接 / 推荐 / 实验性功能 / 更新 / 帮助` 分栏。收藏管理从当前运行时目录解析收藏名称并允许逐项取消，目录缺失项仍保留 ID 和可用存储名称。窗口设置包含透明度、90% 至 130% 字体大小、焦点切换、始终置顶、鼠标穿透锁定、手柄导航和显示调试信息；连接设置包含本地 API/LAN 连接配置并逐项展示可复制的 endpoint；推荐设置包含订单排序、推荐权重、预算策略、缺失厨具过滤、任务料理置顶、收藏料理/收藏酒水置顶、带库存显示和名称/库存排序的排除材料/酒水及同基础料理展示数量；实验性功能设置集中管理自动化总控、稀客/普客自动化、并发/重试参数，以及两类目标各自的游戏界面置顶、加料料理选项、厨具/桌位/订单高亮和目标颜色。经营中推荐视图的稀客与普客页签共用 `当前订单方案` 展示框架：稀客显示主方案和候选，普客显示游戏原订单与当前执行方案。工作台级更新控制器只读取 Mod 更新状态，活动状态 2 秒、稳定状态 60 秒轮询；发现新版时显示非模态提示，并按 endpoint + tag 保存 24 小时延后状态。Tauri opener 只允许打开本项目 Release URL。Android 伴随窗口只作为 B 设备 LAN 客户端，不提供桌面托盘、置顶、鼠标穿透、焦点切换、单实例控制和游戏关闭自动退出；独立 Windows 伴随窗口和 Android APK 不参与 Mod 主包自动更新。桌面鼠标穿透必须通过 Tauri 原生窗口忽略鼠标事件实现，不能只用 CSS `pointer-events` 模拟。帮助内容来自 `apps/companion/src/data/help-content.json`，由 `设置 -> 帮助` 渲染为目录树和详情面板，修改文案时优先改 JSON。`日志` 页签、扫描状态、运行时来源、性能耗时、订单来源和内部 key 这类诊断信息只在 `设置 -> 窗口 -> 显示调试信息` 开启后显示。正式 Tauri 客户端通过原生后端读取本地 API。

伴随窗口的自动化能力只在设置页总开关开启、对应的 `autoRareOrderEnabled` / `autoNormalOrderEnabled` 订单组开启、持有 automation control lease 且当前经营 generation 为 Active 时运行。所有持久开关集中在 `设置 -> 实验性功能`；经营中自动化视图只保留状态、步骤、重试、重置和人工确认。稀客并发、普客并发、最大重试和最大回退由 `CompanionPreferences` 控制；订单排序、推荐过滤、收藏限定和厨具预约仍复用经营中推荐的同一输入。稀客 `autoPrep*` 与普客 `autoNormal*` 的送酒、开始料理、送达料理、完成订单和出错暂停完全独立保存、独立传参、独立推进，但任一直接送达开关为 true 时，同组完成开关必须为 true：偏好归一化、UI 原子更新、前端请求和 C# 副作用入口共同 enforce 该不变量，无效请求返回 `automation-config-invalid`。锁存完成意图的送酒或送料理如果成为最后一项，必须在同一 API 调用或 cooking job 事务内 fresh reacquire 订单并走精确评价入口；评价完成后外层立即返回，不得再访问调用前缓存的 IL2CPP 订单 wrapper。古明地恋 full-feed 也必须携带完成意图并继续使用其精确评价入口。所有自动开锅都登记 `AutomationCookingJob` 作为服务端精确锅次回执，防止 HTTP 响应丢失后再次扣料；job 不保存开锅时的料理送达或订单完成开关，而是在每个未提交的后续副作用边界读取当前主设备 profile 并取得精确 authority revision permit。总控、订单组或阶段关闭，以及 lease 缺失/过期/变化时保留 job 和原锅并暂停有效超时；恢复配置与正确 lease 后继续，玩家改变厨具内容时才按所有权丢失手动交接。已经取得 permit 的原子边界完整结束后才允许权威转换。Closing/Destroyed 后所有后续检查停止访问该场已释放或正在释放的游戏对象。

`AutomationCookingJob` 是料理跨帧状态的唯一来源。`RuntimeCookingGenerationTracker` 对 `CookController.SetCook(Sellable, Recipe, bool)`、`Extract(Action<Sellable>)` 和厨具内容换入 `Store(Sellable)` 建立精确被动观察：`SetCook` 分配新的 generation，三类事件共同推进可核对的厨具 content revision。每个精确 Harmony prefix 先登记 `MutationCompleted=false`，只有同一 revision 的 postfix 在 `__runOriginal=true` 且原方法正常返回时才改为 `true`；期间发生嵌套或后续 mutation 时，旧 postfix 不得覆盖新状态。所有回调由 no-throw 外壳隔离，追踪失败只能保留 default token 或未完成 mutation，不得影响游戏原调用。job 必须在自身 `SetCook` 返回后立即捕获 `LastMutation=SetCook && MutationCompleted=true` 的 snapshot，并在游戏回调后与登记时复核 snapshot 完全一致，再绑定 controller 指针、generation、content revision 和原生配方身份。除现有自动开锅路径调用一次 `SetCook` 外，所有权丢失恢复不得主动调用或重放 `Extract`、`Store`、`FinishCooking`，也不得读取、跟踪或操作 `IzakayaTray`。既有成功送达后的 commit-once 厨具复位与出锅回调不属于所有权丢失恢复，本功能不改变该清理语义。generation 与 content revision 均未变化时，游戏原生 `FinishCooking` 替换同锅 `Result` 继续沿用既有完成阶段和精确成品身份逻辑，原生黑暗料理 `Food/-1` 继续走既有边界；新 `SetCook` generation 发布 `interrupted/cooking-controller-reused`，`Extract` / `Store` content revision 或稳定严格空闲态发布 `interrupted/cooking-ownership-lost`。两者都只释放旧 job，不得送达、入箱或 reset 当前内容。后续选锅只接受两种状态：`Phase=Idle + Result=null + ChosenRecipe=null + CouldOpen=true` 的严格空闲，或同样 `Idle/Result=null/CouldOpen=true` 且最近 mutation 为正常完成 `Extract` 的旧 `ChosenRecipe` 残留；选择时和扣材料前必须分别重新读取并应用同一分类器。对于非空 `ChosenRecipe` 的残留例外，最近 mutation 为 `Store` / 新 `SetCook`、mutation 未完成或所有权不可读时一律不可用；标准严格空闲锅不要求已有所有权快照。控制暂停期间仍按同一目标保留 job 和 controller reservation；若玩家通过 `Extract` / `Store` 或其他内容变更造成精确所有权丢失，则释放预约并按正常手动交接收口。快照 `automationCookingJobs` 除既有 job、目标、厨具、generation/content revision、phase/progress、终态与事务字段外，还暴露 `controlState`、`controlReasonCode`、`controlMessage`、`controlAuthorityRevision`、`controlStage` 和 `controlSuspendedAtUtc`；`automationEvents` 用递增 sequence 发布终态，`automationSessionId` 标识当前 Mod 进程，断线后的同会话 automation control lease 所有者据此接管。

自动化响应使用 `waiting/progressed/completed/interrupted/retryable-failure/blocked/fatal/cancelled` outcome，并携带 `stage/reasonCode/jobId/retryAfterMs`。C# 返回的 `beverage/cooking-start/cooking-delivery/order` 真实阶段必须优先于前端请求前推测，避免同一请求先送酒、后开锅失败时把料理副作用归到酒水阶段。前端用 `retryStage` 绑定失败计数，切换或关闭普通失败阶段时清除旧阶段退避。只有真实送酒、开锅、料理进度前进、送达提交或评价触发能报告 progressed；`cooking-cooker-waiting` 必须是携带正数退避的局部 waiting，不增加阶段重试次数，`cooking-ownership-lost` 与 `cooking-controller-reused` 必须是 interrupted 并只消耗既有玩家干预回退预算。waiting 和 interrupted 都不清零既有阶段失败次数，retryable-failure 才有界累加。副作用不确定的 blocked/fatal 必须设置人工确认栅栏并保留 `prepared`，不能被普通重试、阶段开关、总开关或无关订单事实清除。automation control suspension 是可恢复 waiting，不建立人工 ACK；前端以 request epoch 和事件 sequence 丢弃配置/权威变化前或终态事件前发出的迟到响应，不再解析中文文本或使用前端经过时长猜测恢复。烹饪与送达超时只累计游戏可推进的有效区间，控制暂停、断线、场景不可读和运行时不可达不消耗预算；进度停滞会保留旧锅，因此必须是 blocked，而不是自动重新开锅的 interrupted。

一般料理直接送达订单；job 仍拥有厨具成品且最终副作用尚未开始时，非目标成品、已确认的特殊经营目标签名变化、Tag 不可读、严格方案 Tag 不符或目标连续不可达才使用保温箱恢复。血池地狱受控推进只放宽已经成功读取但未全命中的 Tag，不改变其他恢复条件。CookController Result 使用独立的 signed 成品身份读取，必须以读取成功标志区分无效成员和原生黑暗料理 `Sellable(Type=Food, Id=-1)`；只接受非负料理 ID 或精确 `-1`，且 `-1` 不得进入静态目录、推荐或 `RefFood`。Phase 2 读到黑暗料理时保持 job 所有权并等待游戏原生完成，Phase 3 再按非目标成品进入既有入箱/回退链。`IzakayaConfigure.StoreFood()` 是非幂等 commit-once 操作，IDA 显示它先 `StoredFoods.Add`，再调用 UI/伙伴回调；正常返回和异常后在 `StoredFoods` 中确认到同一 Sellable 对象都代表已提交。只要原生调用已经开始，异常后对象不存在或状态不可读都不能证明没有发生前置副作用，必须 blocked，且不得再次入箱或清厨具。`OrderBase.set_ServFood/set_ServBeverage` 同样先写最终字段再调用视觉回调；订单送达要以最终字段中的同一 Sellable 对象确认 commit，料理只做有界同 generation cleanup，酒水只在确认 commit 后扣一次库存。厨具 cleanup 必须同时确认 `Phase == Idle`、`Result == null`、`ChosenRecipe == null`，读取失败不得当作成功。cleanup 完成、人工交接或 cleanup 明确终止后，cooker controller lease 单调释放；后续评价回执可继续存在，但不得参与厨具预约或同 controller job 替换。普客与稀客 target 都保存请求中的正数 order lifecycle sequence，绑定时要求请求、fresh capture 与 active store 精确一致；普客另严格匹配 `OrderKey`，稀客另保存 trace、桌位、`runtimeGuestId` 与料理/酒水原始 Tag ID。一般路径只使用精确捕获，古明地恋 BOSS/幽幽子三阶段的显式 live-controller 例外也必须取得同一活动 lifecycle；不得按桌号、名称、文本或复用后的相同指针回退。挑战订单在任何可能发生开锅副作用前锁存执行目标，后续料理送达和评价继续透传同一 extras/modifier/受控许可契约，直到订单完成、退休或经营 generation 变化。锁存完成意图的常规料理 job 在厨具清理后继续消费精确 terminal receipt 或 fresh order 完成评价；暂时不可读只在有效运行时间内有界等待，确定性不一致或耗尽时登记同生命周期 ACK 栅栏并退休，不重放送达或评价。

血池地狱是受控例外。BOSS `NormalBusinessOrder.RuntimeGuestId` 必须从已确认的订单/控制器原始身份发布并由前端原样透传，缺失时不以 `GuestId`、名称或 role 回退。`SpecialOrder` 只使用双 Tag 严格方案；BOSS `NormalOrder` 严格方案优先，严格无解且原料理、原酒水和全部硬门禁仍成立时才使用明确标记的受控推进。Mod 可以自动推荐、送达酒水和开锅；成品完成后必须复核经营 generation、job、order/order-controller/cooker-controller 身份、原始 nullable Tag 或 `NormalOrder` 精确 ID、桌位、canonical BOSS `1003`、目标签名和 revision、精确锅次、实际成品 ID 与可读 Tag。严格方案要求当前双 Tag 全命中；受控许可只放宽已读 Tag 未全命中，Tag 不可读、目标轮换、成品 ID 或原订单项目不符仍 fail-closed。匹配成品必须取得当前 `YuumaSettlement` permit 才进入专用无界面结算；开关、订单组、总控或 lease 暂不可用时保留原锅暂停并在恢复后继续，玩家改变厨具内容时才手动交接。permit 覆盖完整不可拆分结算，后到的权威变化等待事务结束。`ShouldPlayerThrowDeliver` 只表示玩家投掷送餐能力，不是单次事务状态，专用结算不得读取或等待它；当前订单的 `ServedFoodInAir` / `ServedBeverageInAir` 才是原生送餐并发门禁，非空时无副作用等待。预检锁定 `OrderBase.ManualOrder`、同一原生 order/controller、最终 setter、fulfilled getter、对应评价与全部记账入口；手动控制订单只调用带同订单捕获回调的 `EvaulateManualOrder`，标准订单只调用 `EvaluateOrder(controller,false,null)`，禁止跨路由兜底。执行顺序固定为料理 setter、同锅清理、fresh fulfilled、评价、`AddBussinessFoodConsumes`、`OnOrderBaseStatusUpdate(FoodDelivered)` 和 `TryAddPlayerOccupiedDeskCode`；酒水同样补齐对应 consume/status/desk 通知。记账上下文必须在评价前缓存，评价返回后不复读 wrapper。每个 job 使用小型单调 tracker，不可逆阶段不确定即进入终止 ACK 栅栏且不重放；不得恢复旧大范围 finalization gate/coordinator、consumed/ACK 特例、送餐面板/UI 模拟、生成协程、托盘或 `MoveNext`；专用入口之外不允许通用直评。受控推进交由游戏原生评价，可能造成较低伤害并增加狂暴，必须在状态与诊断中显式区分。通用自动化安全栅栏和 `/automation/barriers/ack` 保持原有语义。

专用查询必须在每次 captured/live match 时重新读取当前 `OrderBase.ManualOrder`；只有当前为手动控制订单时，才按同一 order/controller 原生身份取回 `ManualOrderSet` 回调。酒水送达在任何扣库前及每个不可逆回调后的 fresh order 上精确门禁 `ServedBeverageInAir`，最终料理还同时门禁 `ServedFoodInAir`；初始在途状态无副作用等待，不可逆步骤后的冲突进入不重放栅栏。标准订单只有酒水先送达时，才在 final setter 后、range 库存调整前恢复一次部分送达耐心，手动控制订单保持原生 no-op；耐心与 range 回调后均再次复核 revision、同一订单和精确酒水。前端在稀客或普客的自动送达料理、自动完成订单开关变化或主设备切换时推进 request epoch 并释放旧 lease；后端在下一未提交边界读取当前 profile 与精确 authority revision permit，暂停或恢复现有 job，不使用开锅时的旧意图。

挑战订单后续送达、评价和厨具预约只允许复用同时匹配经营 generation、规范特殊目标签名与严格/受控许可的锁存目标。同场目标轮换后不得预约旧目标厨具；特殊模块认领的订单存在任一自动化副作用意图但缺少当前锁存目标时整单暂停，不得仅放行普通酒水或评价。

厨具出锅结果只能读取 `CookController.Result` 或其精确 backing field，并确认对象是料理 `Sellable` 后才能送达或进入保温箱恢复。精确 `Type=Food, Id=-1` 是 IDA 验证的黑暗料理，不属于不可读；除此之外的负 ID、错误类型或成员读取失败仍必须拒绝。`CookController.result` / `resultVisual` 是视觉 `SpriteRenderer`，不能作为成品对象；连续读到非 `Sellable`、generation/content revision 所有权无法确认或内容处于无事件支持的矛盾状态时，必须形成有界 waiting/blocked/interrupted 结果，不能无限等待或触碰其他锅次。

自动化在订单部分送达后恢复顾客耐心时，必须先读取同一 `GuestGroupController` 的 `CurrentPatient` 和 `MaxPatient`，再把 `AddPatient` 入参限制到剩余耐心空间内；若检测到当前耐心已经高于上限，只允许调用 `SetPatient(MaxPatient)` 做一次状态校正。游戏原生 `AddPatient` 不裁剪上限，而 `GuestTableDisplayer.UpdatePatient` 会先用 progress 索引贴图数组，因此不得恢复到 `MaxPatient` 以上。

稀客自动化诊断由前端状态机维护，每个当前候选订单都要暴露当前步骤、下次动作、已开锅、已送酒、重试/回退次数、最近原因、暂停状态和人工确认栅栏。普客自动化也要按订单 key 展示下次动作、送酒、开锅、送料理、完成订单和订单已有料理/酒水状态，避免只靠长文本判断卡住位置。订单级执行步骤必须保存在对应订单状态中，并在经营中页订单条目下方的 `自动化详情` 折叠区展示；默认全部折叠。普通 `重试` 只解除该订单暂停并保留已完成阶段，普通 `重置` 让该订单重新判断；人工确认栅栏禁用重试，按钮改为 `确认已处理`。订单已从快照消失时仍必须通过独立待确认列表暴露事件。确认动作调用 `/automation/barriers/ack`，只有后端成功解除 sequence 后才清理本地状态；任何操作都不得影响其他订单。无执行计划时优先显示 `blockedDiagnostic.message`，总日志额外记录 code、首个阶段、分阶段计数、资源证据和稳定状态签名；稀客与普客分别使用当前自动化会话内最多 64 条的有界签名集合去重，切换会话清空，避免两类订单互相覆盖签名或同一状态重复刷日志。

伴随窗口直接双击启动时通常没有本地 API Token。前端必须停留在未授权状态，不得高频请求 `/snapshot` 或日志端点；用户修改端点或 token 输入框时也不得立即重连，只有点击 `连接` 或从游戏启动参数收到新的连接身份后才恢复轮询。相同 endpoint/token 的重复单实例通知必须幂等，不得清空快照或推进连接代际。自动探测和失败重试必须使用较短本地 API 超时且不触发全局刷新 loading；手动刷新可使用稍长超时。连接失败后只按递增退避重试 `/snapshot`，并且只有快照成功才能清除错误和恢复写操作；`/health` 成功不能建立已连接状态。允许用户点击 `停止` 暂停自动重连。

普客订单自动化仍是实验性功能。设置页自动化总控和 `普客自动化设置` 中的 `启用普客处理` 必须同时开启，并至少开启一个独立阶段；订单按首次出现顺序处理，不保留手动处理按钮。一般订单的酒水和料理统一提交到顾客桌面，只有订单已满足才评价；后续 fresh snapshot 的 `HasEvaluated` 或订单消失只作为外部状态收敛，本 Mod 发起评价的提交证明仅接受同一次调用内发布的 exact lifecycle `Evaluated` receipt。特殊经营规则按模块接入：`AutomationCookingJob` 同时保存原订单 match 目标、实际执行目标、规范特殊料理目标策略和受控推进许可，出锅时不能用执行料理反查原订单。怪诞料理目标使用 `Any`，血池地狱目标策略保持 `All`；开锅和出锅复核阶段的 challenge、owner、经营 generation、Tag、match mode 或签名任一确认漂移都会使原执行目标失效。幽幽子三阶段评价必须取得当前仍由 controller 持有的精确订单与所需回调，capture 严格复核和显式命名的幽幽子 live-controller 例外都使用同一验证器；怪诞料理大赛中的古明地恋本体在护盾期走通用评价，破防后交给 Boss 原生回调。血池地狱第二阶段显示在普客区域的 BOSS `NormalOrder` 和第三阶段 `NormalOrder` 仍走挑战模块，只有共享 IL2CPP 转换唯一成功且 BOSS 双身份成立时才开放；Mod 先选择并复核双 Tag 严格方案，严格无解时仅对保持原料理/酒水且通过全部硬门禁的精确 BOSS `NormalOrder` 使用受控推进，最终结算受当前 profile 与精确 authority lease permit 控制，可暂停恢复而不锁存开锅时开关。具体规则见 `docs/special-business-scenes-notes.md`。

总日志文件 `BepInEx/config/MystiaStewardCompanion/aggregate-mod.log` 默认关闭，由 `Diagnostics.EnableAggregateModLog` 或日志页“总日志”开关启用。启用后注册 BepInEx 全局 `ILogListener`，捕获所有日志源并按时间、级别、来源和线程标注；自动化日志记录 jobId、trace、controller/result、generation/content revision、phase/progress、结构化 outcome/reason、厨具暂忙证据、StoreFood commit、reset 尝试、事务和 controller lease 状态、订单原生身份与 lifecycle、评价状态，以及 `controlState/controlReasonCode/controlAuthorityRevision/controlStage/controlSuspendedAtUtc`。控制状态日志只在状态转换时写入，连续相同 automation action、目标和消息合并为 `repeat` 摘要。单个文件达到 10 MB 后拆分为递增编号分片；默认保留 30 个文件，约 300 MB。监听器不得回写自身状态，写入、分片和裁剪失败也不得影响游戏流程。

上述分片只保护 `aggregate-mod.log`，不保护 BepInEx/Unity 共享的 `LogOutput.log`、`output_log.txt` 或 `Player.log`。后台 worker 不得用无限异常重试向插件日志源刷写；本插件也不得接管、截断或删除共享日志。

每个总日志集中写入入口都在原有服务锁内确认活动路径仍存在；外部删除或移走当前文件后，下一条日志写入会释放旧句柄、重建原路径、写入恢复边界并重置当前文件的去重诊断签名。它不使用 `FileSystemWatcher` 或后台磁盘轮询，没有后续日志事件时不主动生成空文件。

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
- 结构化任务能力发布 active-only tracked 与被动来源 available 两条独立业务链。`RuntimeMissionDiagnosticCapture` 的有界读档种子、Initialize 具体 DLC 校验、受控初始刷新和自然状态观察服务 active tracked，同时为 available 来源提供唯一 generation/owner 与 StartMission 确定结果。available 正式覆盖 type 0/1/5 与两类 post source；type 3、当日计划、限时任务和其他未闭环来源仍不在业务支持范围。
- `RuntimeScheduledMissionSourceReader` 由 frozen scheduled 诊断和 fresh available capture 共用，只读取 `scheduledEvents[CorrectedDay]` / `[-1]`、具体 List、精确 `Il2CppStringArray`、定义、active labels 和 finished 历史。frozen 诊断每个稳定 day generation 只采集一次；available 每次 GET 都在 Unity 主线程重新读取，不绑定 day-scene readiness/day generation，而在开始和提交时复核同一任务 generation/change version、source revision/owner、mapped identity snapshot、当前日及容器序列。四个 exact scheduler Hook 只记录托管来源 transition；业务投影接受 type 0/1/5 + `postMissions` / `postMissionsAfterPerformance`，按类型重建进入场景或 canonical 羁绊门禁，并严格检查任务 `preNodes`、active、`loopedMission` 和 finished 状态。所有 label 保持原始 Ordinal identity，不枚举 scheduled 字典，不复用冻结报告。
- available 读取严格纯读。严禁调用 `CanContinue`、`StartMission`、`RefNPC`、`CheckCharacterInteractEvent`、`HasSpecialNPCKizunaExpFull`、`RefOrGenerateSpecialRunTimeData`、`GetOrGenerateSpecialNPCKizunaLevel` 和任何触发、生成或推进任务的入口。available 结果只进入任务列表，不进入任务料理置顶、推荐、自动化、置顶或高亮。
- 存档中的任务 bool 只作诊断证据，`conditionFinishStates` 合法为空且不要求与静态定义条件数一致，不得直接发布为当前进度。每次加载最多同步读取 512 条静态定义，超限即令本代诊断 fail-closed；反射元数据可以缓存，但不得跨 generation 缓存 native 任务或语言对象。静态定义仅在 Unity 主线程按 `DataBaseScheduler.TargetNodeExists(label) -> RefMission(label)` 读取精确 `finishCondition` 引用数组、原始 `reciever`、独立 `hasReciever` 诊断值、`conditionType` 和 `amount`；标题只从语言 `Missions` 具体字典精确查键后读取 `LanguageBase.Name`，标题不可用时保留明确状态而不回退 `GetMissionLanguage`。`RunTimeScheduler.Initialize` 原方法成功返回后，Mod 只按已验证的 merged bucket 和任务顺序，对 `trackingMissions` 使用已知 int key 精确查值并按具体 List Count/indexer 绑定全部原 `TrackedMissionData`；label、数量或 identity 不唯一即整代 fail-closed。首次主动调用前必须确认 bucket 数、空 buffer、finished 多重集，并为全部唯一 label 完成上述核心定义预读；任一失败时不得调用任何任务对象。每个通过预读的读档任务对象只主动执行一次 `UpdateFinishStates`，随后要求 tracking bucket 数、buffer Count、finished 标签完整序列以及逐项任务 label/identity 与刷新前一致，并要求刷新结果数量与预读定义条件数一致，才原子提交当前状态。`GenerateTrackingData` Postfix 只捕获该次返回的精确新任务对象；`StartMission` 原方法完成、对象已经插入全局任务列表后，先预读该任务的精确定义，并在预读前、主动刷新后和提交前分别确认状态仍属于同一所有者线程上的 `Ready + runtimeAvailable` generation；若期间没有捕获该对象的自然刷新，才执行一次同样的 `UpdateFinishStates`，并立即复核 label、identity 和条件数量。旧 frame 一旦失去代际所有权，只允许清理，不得调用或提交。除这两个边界外，Mod 不主动刷新或轮询任务；后续变化只观察游戏自身的自然 `UpdateFinishStates` 及稳定生命周期 Hook。禁止调用 `HasFulfilled`、`ParseActiveMissionData`、任务完成/奖励/移除写入或宽泛枚举；`FinishNodeExtern` 只接受 finished 列表不变或保持原前缀的尾部追加，循环任务重复完成按频次保留。所有 Harmony 入口必须以 no-throw 外壳隔离诊断异常，finalizer 无条件返回原生异常；inactive 指针转移时清除旧身份，active 冲突和迟到旧指针回调 fail-closed。
- `RuntimeServeInWorkMissionDiagnosticCapture` 只被动观察游戏原本调用的 `ContainsSpecialNPCServeInWorkMission` 返回值和 `out foodId`，并按已加载 canonical 稀客身份、静态 ServeInWork 定义、任务/夜间经营 generation 交叉校验；Mod 不得主动调用该方法。任务/经营 generation 变化、经营 Closing/Destroyed、Hook/任务读取失败和原生异常全量清除信号；成功任务生命周期从当前完整定义构造 active、非 `Fulfilled` 的 `(canonicalGuestId, foodId)` 集合并精确复核现有信号，无关任务刷新不得清空其他有效信号，定义、目录、receiver 或 food ID 不完整时整轮 fail-closed。只有普通经营、同一 canonical/runtime 稀客恰好匹配一笔未送餐活动订单，且 food ID 在完整目录中唯一解析出非负 recipe ID 时，才向该订单投影 `MissionRecipePriority`；前端只有在默认开启的 `missionRecipePriorityEnabled` 为 true 时，才从正式候选中匹配同一 `foodId + recipeId`。exact active ServeInWork 任务料理可以跳过普通 food Tag 和料理厌恶判断；配对酒水仍须满足当前点单，料理解锁与库存、材料、厨具、预算、排除项、自动化收藏限定和其他硬门禁均继续决定能否成为主计划。合法任务计划排在自定义置顶、收藏料理/酒水和普通权重之前；页面、自动化和稀客游戏界面目标统一消费 `executionPlans[0]`，普通经营的稀客槽优先尚未送达的任务计划，普客槽独立保留。关闭设置只从 sort context 移除任务目标，不关闭后端被动捕获；特殊经营、歧义、字段缺失或代际不一致一律不投影。任务主方案的料理行显示 `任务目标`，游戏料理、材料和酒水列表发布仍由 `gameUiPinningEnabled` 独立控制。已打开列表收到新目标后，只能按目标内容代际在 Unity 主线程执行一次安全刷新；制作页只允许 Harmony 只读观察精确 `OnPanelOpen` / `OnPanelClose` 并在 Tick 检查 dead pointer，禁止 Hook 共享空原生别名 `WorkSceneCookingSelectionPannel.OnPanelDestroyed`；酒水仓库页可继续使用自身独立的 close / destroyed 生命周期。禁止主动调用或重调 `OnPanelOpen` 触发刷新，也禁止场景/面板扫描或轮询。tracked、ServeInWork 与 scheduled-event 三份任务 JSON 仍只用于诊断，available JSON 保存最近一次业务读取结果，available-source JSON 保存当前被动来源状态。不得恢复 `AllNodesMapping`、`GetAllNodes()`、`GetAllMissionData()`、`GetTrackedMissionData()`、`ParseActiveMissionData()`、主动 `HasFulfilled`、在上述两个精确边界之外主动 `UpdateFinishStates`、编译器生成 Hook、managed/field 兼容读取、复杂 tracking Enumerator 或 scheduled 字典全量枚举。
- 伴随窗口是唯一用户界面；游戏内不再提供备用 IMGUI 面板。
