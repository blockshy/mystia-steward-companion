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

`tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj` 会使用真实 Harmony wrapper 验证 scoped prefix 返回传播，以及料理/材料/酒水列表元素 Hook、Food/Beverage 类型隔离、池化重绑恢复、后台目标发布、面板和场景生命周期，因此运行该 smoke 时还要从 `BepInEx/core` 复制 `MonoMod.RuntimeDetour.dll` 和 `MonoMod.Utils.dll`。这两个 DLL 是测试运行依赖，不加入 Mod 编译和发布 preflight；引用放在外部目录时，通过 `-p:ReferenceDir="..."` 传给 `dotnet run`。

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

该命令会构建 release APK、调用 `apksigner verify --verbose --print-certs` 验签，并复制发布资产到：

```text
mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk
mods/bepinex/dist/mystia-steward-companion-android-armeabi-v7a.apk
```

签名 APK 脚本会在 Android 构建进程内注入 `CARGO_PROFILE_RELEASE_STRIP=symbols`、`CARGO_PROFILE_RELEASE_LTO=thin` 和 `CARGO_PROFILE_RELEASE_CODEGEN_UNITS=1`，用于降低 APK 体积。该优化不会写入全局 Cargo release profile，避免普通 Windows 发布构建在 Rust 链接优化阶段耗时过长。

也可以通过 `build-release.ps1 -BuildAndroidApk` 或 `publish-release.ps1 -BuildAndroidApk` 在本地构建/发布流程中自动生成这些文件。如果 APK 放在其他位置，发布时通过 `publish-release.ps1 -AndroidApkPath "D:\path\android-apks"` 指定 APK 文件或所在目录。APK 只作为 GitHub Release 的独立下载资产，不写入 `update-manifest.json`，也不参与 Mod 自动更新。

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

报告和截图默认写到 `/tmp/mystia-companion-ui-audit`。通用 UI 巡检覆盖 1280x900、900x760 和 640x760 三组视口；640px 用于验证 Tauri 桌面最小宽度下核心内容保持双列、顶部状态保持三列、一级导航以五列两行完整显示，并检查连接和页面工具条的紧凑布局。如果使用 `pnpm preview`，把 `MYSTIA_APP_URL` 改成 Vite preview 输出的地址，通常是 `http://127.0.0.1:4173`。

修改手柄输入状态机、复合控件焦点语义、动态回焦、局部滚动或游戏/伴随窗口焦点切换后，运行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:gamepad
cargo test --manifest-path apps/companion/src-tauri/Cargo.toml
dotnet run --project tests/controller-toggle-state/ControllerToggleStateSmoke.csproj -c Release
```

`audit:gamepad` 先验证纯输入状态机的 standard mapping、活动设备所有权、中立门控、按键模拟量、摇杆滞回、方向仲裁、重复节奏和 RS 隔离，再用 Playwright 验证 A/B/X/Y、LB/RB、LT/RT、Select/MultiSelect、Tabs、SegmentedControl、NumberInput、Slider、Dialog、动态回焦、局部滚动，以及 1280x900/100%、640x520/130%、390x844/90% 三组窗口和字号组合。Rust 单测验证所有切换来源共用的 applied-only cooldown gate；C# smoke 验证 RS 持续按住、迟到边沿和物理释放后的重新武装。

修改字体 token、字号偏好、更新状态协议或全局更新提示后，在同一 mock API 与 preview 环境中运行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 MYSTIA_API_URL=http://127.0.0.1:32145 pnpm audit:font-scale
pnpm audit:updates
MYSTIA_APP_URL=http://127.0.0.1:4173 MYSTIA_API_URL=http://127.0.0.1:32145 pnpm audit:updates:ui
```

字号巡检覆盖 90%/100%/130%、非法值归一化、鼠标/键盘操作、刷新持久化、恢复默认、640x520、390x844、全部页签、设置五个分栏、稀客订单专注模式和 Select Portal；截图写入 `/tmp/mystia-companion-font-scale-audit`。更新协议审计覆盖启动 `idle -> checking -> available` 收敛、状态读取失败退避、请求代际、endpoint/tag 延后键、安装提示和 Release URL 限制；提示巡检覆盖动作中断连、连接身份切换、迟到响应隔离、首帧延后状态和安装失败，截图写入 `/tmp/mystia-companion-update-ui-audit`。

修改游戏界面置顶/列表高亮目标契约、连接重发或推荐 Worker 生命周期后，还要运行定向巡检：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:ui-pinning
```

该巡检会验证 `POST /ui-pinning/target`、`Recipe.Id` 与 food ID 分离、业务失败退避重试、短暂断线不重复发布、服务端会话或显式连接身份变化后重发、内部签名变化不污染 wire 去重、过期 Worker 结果不会下发，以及 Worker error 后空目标锁存只在新的 current 成功 revision 到达后解除。

修改自定义推荐料理总开关、分组、批量状态或排序契约后，运行：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
pnpm audit:custom-recipes
```

该巡检会验证总开关持久化、草稿跨页签保留、稀客/基础料理分组记忆、页面级/分组级/单条状态更新、同稀客排序、单写者和最小窗口横向溢出。

修改自动化阶段机、料理 job、控制权取消或断线接管协议后，运行：

```bash
dotnet run --project tests/automation-cooking-job/AutomationCookingJobSmoke.csproj -c Release
pnpm audit:automation
pnpm audit:connection-recovery
```

料理 job smoke 验证 `SetCook` generation 所有权、手动取走/同厨具复用、有效停滞时钟和 `StoreFood` 提交后有界复位；两个前端 audit 分别验证结构化 outcome、阶段计时、command epoch、运行时事件时序、mock 取消/接管，以及快照恢复、持续退避、连接身份幂等和 lease 会话绑定协议。

修改特殊经营挑战名称来源、目标捕获状态、上下文规则注册表、运行时稀客目录或页面名称 fallback 后，运行：

```bash
pnpm audit:special-business
```

该审计会验证挑战名称使用游戏原生 IL2CPP `InspectorName` 固定中文元数据、永久失败缓存诊断且不重试、瞬时失败按固定间隔持续重试、规则注册表不再保存中文名称映射、名称不可用时页面只显示一次有效 challenge type；同时验证 HUD 目标按 raw challenge owner、target kind 和 inactive 会话边界隔离，并禁止运行时稀客目录重新调用未消费且会产生 Warning 的特殊请求语言 getter。它不能替代实机确认原生元数据可读、跨挑战目标不会残留，以及首次目录加载不再产生对应数字 ID Warning。

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

PowerShell 7 脚本固定生成 Mod 主包 `.zip`；bash 脚本在系统没有 `zip` 时会改为生成 `.tar.gz`。打包脚本会在检测到 `apps/companion/src-tauri/target/release/mystia-steward-companion(.exe)` 时自动复制到安装包的 `companion/` 子目录，并把 `mystia-steward-companion-updater(.exe)` 放在插件目录根部。检测到 Windows `.exe` 时，还会在 `dist` 根目录复制一份 `mystia-steward-companion-companion-windows-x64.exe`，供其他设备只下载伴随窗口并通过 LAN 连接。Android APK 由 Tauri mobile/Android 工具链单独构建和签名，打包脚本不会从 Windows EXE 派生 APK。Windows 下该 updater 会显示独立更新窗口，负责提示关闭游戏、展示阶段进度并在游戏退出后替换插件目录。

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

推荐、库存名称、任务目标和自动化目标解析使用游戏运行时读取到的 `RuntimeDataCatalog`。伴随窗口未连接游戏、游戏数据库未初始化或 `/snapshot` 返回的 `runtimeDataComplete=false` 时，页面会显示等待运行时数据。

发布包包含 Mod DLL 和伴随窗口程序，推荐、库存、任务和自动化目标都来自游戏当前运行时。

## 运行时刷新行为

Mod 会定期检查当前页面和游戏运行时状态。进入游戏并加载进度后，推荐状态来自当前内存中的运行时对象，不读取 `.memory` 存档文件。

运行时固定数据读取成功后，C# 会把 `DataBaseCore`、`DataBaseCharacter` 和 `DataBaseLanguage` 中的料理、食材、酒水、普客、稀客和 tag 映射构造成 `RuntimeDataCatalog`，写入本地 API 快照并切换 C# 推荐仓库到运行时仓库。伴随窗口概览页的“推荐数据”显示“游戏运行时”时，表示前端推荐算法已经获得完整运行时数据。

运行态读取不再依赖固定秒数等待。日间任务列表、当前日间地图和稀客邀请通过 `DayScene.SceneManager.CurrentActiveMapLabel` / `TargetMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`RunTimeDayScene.RefTrackedNPCAvailability()`、`DaySceneMap.allCharacters` 和 `RunTimeScheduler` 等运行态入口读取，不再把 `DaySceneSustainedPannel` 面板激活状态作为总门禁；夜间经营准备读取要求 `PrepNightScene.UI.IzakayaConfigPannel.OnPanelOpen` / `GoToSpecific` 已触发，且 `WorkPrepScenePannelRoot` 下的 `IzakayaConfigPannelNew` 仍激活。准备阶段只读取库存、已解锁、流行 Tag 等基础玩家运行态，因此 `修改`、`普客` 和 `稀客` 页面可以提前使用；任务列表、当前日间地图和稀客邀请仍只在日间场景读取。

推荐状态中的库存、酒水和已解锁料理使用 `RunTimeStorage.GenerateSaveData()` 生成的一份当前运行时存储快照作为权威来源；玩家等级、流行喜好/厌恶 Tag 和明星店开关继续使用轻量 getter 读取。若存储快照中的 `recipes` 为空，Mod 会等待下一轮运行时读取，不会向伴随窗口发布空的可用料理集合。

为降低经营中掉帧风险，本地 API 快照发布会做轻量节流：Unity 主线程最多约每 0.35 秒刷新一次缓存 JSON；若快照内容签名未变化，会复用上一份缓存 JSON，不为了 `CapturedAtUtc` 或性能数字重复序列化。完整 `RuntimeDataCatalog` 不再放进 `/snapshot`；快照只发布目录是否完整、来源、状态和签名，伴随窗口仅在本地缓存为空或签名变化时通过 `/runtime-data` 读取完整目录。运行时固定数据已经完整读取后，会缓存稀客映射和静态目录快照，经营 provider 与经营诊断只消费缓存，不再从经营快照热路径反复触发静态数据扫描；读取未完整时也按约 5 秒间隔重试，避免 `runtimeData.staticData` 在每轮经营刷新里反复消耗主线程。伴随窗口需要按签名缓存最近一次完整运行时数据，不能把 `/runtime-data` 的临时读取失败当作主快照丢失。概览页和经营中页会显示 `performanceMs` 中最近约 12 秒内耗时最高的快照环节，排查卡顿时优先记录 `refresh.business`、`refresh.runtime`、`snapshot.serialize`、`runtimeData.serialize`、`automation.collect` 和 `snapshot.publish`。经营扫描还会细分 `business.rare.*`、`business.normal.*`、`runtime.cookerSnapshot`、`mission.serveTargets` 等子项；普客订单快照会在短时间内复用，避免同一轮 `/snapshot` 发布重复枚举 `OrderController`、HUD 和 `GuestsManager`。

普客订单被动快照缓存约 1 秒。`NormalOrderRuntimeCapture` 通过 `GuestGroupController.PushToOrder` 和 `GuestsManager.SetManualControllerOrderInternal` 捕获订单与可执行控制器绑定；常规快照先读取 live `OrderController` / HUD，再合并仍匹配订单 key 或桌位/料理/酒水槽位的捕获缓存。手动控制订单还需枚举 `NightSceneDirector.controlledGuest`。已不可见缓存必须剔除，捕获不可用时才做 `GuestsManager` 启动扫描；捕获版本变化只刷新普客订单快照。没有活动 `AutomationCookingJob` 时不在 `Update()` 热路径轮询料理 job。

游戏界面置顶不反射读写 UI 列表；`UpdateAllVisual` 与 `UpdateBevField` 只建立 ThreadStatic 刷新作用域，最外层 prefix 固定目标快照。`RunTimePlayerData.CheckPinned` 的 bool prefix 只为精确目标设置 `__result=true` 并跳过原方法，非目标或作用域外完整执行游戏逻辑。不得压制玩家收藏，cooker 类型 `3` 只由独立高亮服务处理，也不得恢复列表改写路径。

`RuntimePinnedListHighlightService` 只 Hook `WorkSceneCookingSelectionPannel.OnRecipeElementEnabled/3`、`OnIngElementEnabled/3` 和 `WorkSceneStoragePannel.OnElementEnabled/3`，酒水还必须确认 `Sellable.Type=Beverage`。`RuntimeUiPinningService` 维护排序与列表高亮共用的唯一 immutable target/generation，并串行化完整目标发布；不得在视觉服务中再保存第二份目标。Local API 工作线程不得读取或写入 Unity 对象；列表 Image 着色只在元素回调和 `LateUpdate` 主线程执行，保留原始 alpha，并在池化重绑、panel close/destroy、场景 suspend 和插件 Dispose 时恢复。场景 suspend 只能由两个面板的 `OnPanelOpen/0` prefix 恢复，不得由网络目标重发解除。

夜间经营中，`经营中 / Service` 页优先使用 `SpecialOrderRuntimeCapture` 捕获到的稀客订单缓存；捕获缓存为空、需要初始化校验或本轮可接受订单少于缓存数量时，再扫描 `GuestsManager`、稀客队列、`OrderController`、HUD、服务面板和桌位控制器补充业务缺失项。诊断开启时允许额外采样这些来源，但样本只写日志，不合入正式订单集合。捕获版本、场景和诊断状态都未变化时会复用已有经营上下文，并按较慢节奏重新校验。页面仍会读取桌位控制器中的活动稀客，用于显示当前稀客和 `GuestGroupController.GetFund`、`BaseFundCarry`、`MaxFundCarry` 等当前携带金钱信息。普客订单使用 `OrderController` / HUD 判断订单可见性，使用 `NormalOrderRuntimeCapture` 记录 `GuestGroupController.PushToOrder` 或 `SetManualControllerOrderInternal` 建立的订单归属；HUD / `OrderController` 单独存在时只能显示推荐和不可执行诊断，不能单独用于自动送达或评价。页面顶部只展示经营场景、扫描状态、推荐数据、厨具与置顶状态等通用信息，随后用 `稀客` / `普客` 页签分区展示各自功能。稀客点单后，工作台会按桌号列出稀客、料理词条和酒水词条，并复用稀客推荐算法计算候选料理、加料和酒水。普客订单读取到 `GameData.CoreLanguage.LanguageBase` 这类 IL2CPP 本地化对象时，必须过滤为无文本，不得把运行时类型名当作客人、料理或酒水名称展示。

若 IL2CPP getter 无法读取订单列表，Mod 会继续尝试 `AllOrdersData` 和 `PeekOrders()`；若 tag ID 读取失败，会从稀客控制器的订单文本方法读取中文词条。

稀客推荐结果会按角色、点单词条、库存状态、厨具快照、排序配置、置顶开关、同基础料理展示数量和加料上限缓存。经营中展示行应从完整排序候选池派生统一料理/酒水列表；不满足点单但命中稀客喜好的候选直接进入同一列表并标注为偏好备选，不能被自动化执行候选上限提前裁掉。自动化目标只对少量独立执行候选做组合选择，不能依赖 UI 裁剪后的展示行。自动刷新没有检测到相关变化时，不会在每个刷新周期重复枚举加料组合；排序配置、置顶开关或同基础料理展示数量变化必须进入缓存签名，否则用户调整设置后会继续看到旧顺序或旧展示数量。

收藏数据由 Mod 本地 API 持久化到 `BepInEx/config/MystiaStewardCompanion/favorites.json`。前端只通过 `/favorites`、`/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` 读写，不使用 localStorage 存储收藏，避免版本更新或 WebView 数据迁移时丢失。

如果没有检测到运行时数据，普客和稀客推荐页只显示运行时数据不可用，不会回退到“全内容可用”状态，避免误以为库存和解锁内容已经同步。

开启总日志后，经营扫描会额外输出 `night-business` section，其中包含 `Candidates` 和 `RecentRuntimeParseFailures`。前者记录被扫描到的 controller/order 候选、接纳状态和过滤原因；后者记录运行时订单捕获器最近未能解析为稀客订单的样本。排查映射稀客或特殊事件稀客时，优先查看这两段。

诊断采样不得改变正式业务输入。有 runtime capture 时，推荐和自动化始终使用捕获订单及既有缺失项反射补充；为完整诊断额外枚举到的 HUD/controller 订单只写入 `night-business` section，不合入最终订单集合。

总日志还会输出运行时固定数据快照：

- `runtime-static-data`：`DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 运行时同名别名，日志中的 `aliasSource` 会标明归一化来源。
- `runtime-tags`：`DataBaseLanguage` 的料理/酒水标签文本、DLC 标签映射，以及 `DataBaseCore.TagRules`。
- `runtime-database`：`DataBaseCore` 食材、酒水、菜品、料理运行时表；每个表会记录 `GetAllX` 方法读取结果，以及游戏静态字典 fallback 的读取结果。
- `runtime-guests`：`DataBaseCharacter` 普客、稀客、映射稀客、原始稀客映射和 `GuestFoodEasterEggData` 类型/简单字段。
- `runtime-izakayas`：`DataBaseCore.GetAllIzakayas()` 或静态 `Izakayas` 字典读取到的经营场景标签、等级、普通/稀客池和刷新参数。

固定数据快照由基础运行时目录刷新路径读取并缓存；总日志开启且 `NightBusinessReflectionProvider.LoadContext()` 被调用时，经营诊断只把已缓存的目录快照写入总日志，不会在经营热路径重新扫描 `DataBaseCore`、`DataBaseLanguage` 或 `DataBaseCharacter`。若游戏数据库尚未初始化，目录刷新会按 5 秒间隔重试。判断读取成功时优先看 section 里的 `Complete: True` 和 `Status` 中各类计数是否大于 0。

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
- `GET /snapshot?knownSignature=...`：读取最新运行态快照。快照由 Unity 主线程按自动刷新节奏生成，网络线程只返回缓存 JSON；内容签名固定为规范内容的 64 字符小写 SHA-256，不能把随订单增长的规范原文放进查询串。签名未变化时返回轻量 unchanged 响应。快照包含推荐状态、稀客/普客订单、任务、活动 `automationCookingJobs`、递增序号 `automationEvents`、运行时目录元信息和 `performanceMs`，不包含完整 `RuntimeDataCatalog`。
- `GET /runtime-data`：读取当前完整 `RuntimeDataCatalog`。伴随窗口只在本地没有目录缓存或 `/snapshot` 中的 `runtimeDataSignature` 变化时调用。
- `GET /automation/lease`：读取当前自动化控制权状态。
- `GET /logs/settings`：读取总日志开关、总日志路径、单文件分片大小、文件上限和总容量上限。
- `POST /logs/config?aggregateLog=true|false&aggregateLogMaxFiles=30`：由伴随窗口回写总日志开关和文件上限；`aggregateLog` 会即时注册或移除 BepInEx 全局日志监听器。
- `POST /logs/open-folder?target=aggregate`：打开总日志目录。
- `POST /inventory/set?type=ingredient|beverage&id=ID&qty=数量`：在 Unity 主线程通过 `RunTimeStorage` 原生 Range API 修改当前运行时材料或酒水库存，并回读校验最终数量；原生调用失败时不会绕过 callback 直写私有字典。
- `POST /inventory/bulk-set?type=ingredient|beverage&ids=ID1,ID2&qty=数量`：批量修改当前运行时材料或酒水库存；用于修改页的材料/酒水批量设为 `99`，只在批量结束后刷新一次运行时快照。
- `POST /orders/prepare-next?...`：按伴随窗口传入的稀客订单执行准备步骤，可组合送达酒水、开始料理、出锅后直送和收藏限定；调用方必须持有自动化 lease。
- `POST /logs/export-diagnostics?open=true`：生成诊断 zip，包含 manifest、当前 snapshot、运行时目录和总日志分片尾部；`open=true` 会打开诊断包目录。
- `POST /orders/complete-first?...`：按伴随窗口传入的稀客订单确认直接送达状态，必要时补送酒水，并在订单满足后触发评价；调用方必须持有自动化 lease。
- `POST /orders/rare/dismiss?...`：按桌号和点单 Tag 删除一笔运行时稀客订单捕获缓存，用于清理偶发未被游戏移除事件命中的过时订单。
- `POST /orders/normal/complete-first?...`：按请求中的订单 key、桌位、原订单目标和实际执行目标处理一笔普客订单；调用方必须持有自动化 lease。普客自动化可按 `autoNormal*` 阶段配置送达酒水、开始料理、出锅后直接送达料理，并在订单 `get_IsFullfilled()` 为真后调用 `EvaluateOrder()` 完成评价；该字段只表示订单已满足并可评价，前端仍需以 `HasEvaluated` 或订单消失判断真正完成。若订单只存在于 HUD / `OrderController`，但没有可执行 `GuestGroupController`，后端必须拒绝自动送达并返回不可执行诊断。
- `POST /rare-guests/invitations?scope=current|all`：排队到 Unity 主线程，返回指定范围内的稀客邀请候选、当前已邀请列表和禁用原因。候选扫描会通过 `GetOrGenerateSpecialNPCKizunaLevel()` 补齐运行时羁绊状态，因此不是纯读请求；结果应默认返回全量候选，前端再按羁绊等级筛选显示，避免切换筛选时丢失其他等级选项。
- `POST /rare-guests/invite-all?scope=current|all&levels=2,3`：按同一套候选扫描和判定逻辑批量邀请可邀请稀客；`levels` 可选，只邀请指定羁绊等级的可邀请项。`current` 候选优先使用 `DayScene.SceneManager.CurrentActiveMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`DaySceneMap.allCharacters` 和场景中的 `CharacterConditionComponent`，若这些实时对象还未填充，则按当前地图反查 `DataBaseDay.GetAllNPCKeys()`、`AllMappedNPCsMapping`、`AllNPCsMapping` 或 `allNPCs` 中的 NPC key，再通过 `RefNPC().possibleDestinations` 判断所在地图，并用 `RunTimeDayScene.RefTrackedNPCAvailability()` 判断当前范围内的运行时可见性。`all` 候选会合并当前场景候选和全部日间静态 NPC 候选；全部静态候选不使用当前时间可见性作为硬过滤，避免 `TrackedNPC.ShouldShown(RemainActions)` 误删跨场景候选。当前场景候选为空时直接失败，不回退到 `DataBaseCharacter.GetSpecialGuestsAndMappedGuests()` 执行全量邀请。每个候选会读取 `RunTimeAlbum.GetOrGenerateSpecialNPCKizunaLevel()`、检查 `StatusTracker.HasNPCInvited()` 和当前等级成功邀请对话包；符合条件后直接调用 `StatusTracker.RecordInvitedGuest()` 写入今晚邀请名单。该端点不调用 `DaySceneChatSelectionPannel.InviteSpecGuest()`，避免触发随机失败和消耗今日尝试次数；也不以 `HasTemptInvited()` 作为跳过条件，避免旧版本或手动失败尝试把可写入邀请卡住。该端点不直接刷出稀客，不推进时间，不写 `Story.SpecialGuestControlled`。
- `POST /rare-guests/invite?guestId=ID&scope=current|all`：邀请单个当前可邀请稀客。
- `POST /automation/lease/acquire`：获取或续约当前客户端的自动化控制权；新所有者取得 lease 时会进入新的 command epoch。
- `POST /automation/jobs/cancel`：由当前 lease 所有者原子取消全部自动料理 job 和旧 epoch 排队命令，确认取消屏障后释放控制权；响应返回 `commandEpoch`、`cancelledJobs`、`cancelledCommands` 和 `leaseReleased`。
- `POST /automation/barriers/ack?sequence=SEQ`：由当前 lease 所有者确认已人工检查对应安全事件；后端按精确 sequence 解除同一订单截至该事件的未确认栅栏。找不到事件或不持有 lease 时不得清除前端人工状态。
- `POST /diagnostics/automation-decision?...`：把伴随窗口的自动化候选决策写入总日志。
- `POST /ui-pinning/target?...`：更新游戏内料理、食材、酒水置顶和厨具高亮目标；`recipeId` 是游戏 `Recipe.Id`，与成品 food ID 分开。
- `GET /favorites`：读取收藏料理和收藏酒水。
- `POST /favorites/add-recipe?...`、`POST /favorites/remove-recipe?id=...`、`POST /favorites/add-beverage?...`、`POST /favorites/remove-beverage?id=...`：增删收藏数据。
- `GET /custom-recipes`：读取自定义推荐料理。
- `POST /custom-recipes/settings?enabled=true|false`：切换整个自定义推荐料理功能，不改写单条状态。
- `POST /custom-recipes/update-flags?...`：按 `entry`、`customer`、`recipe` 或 `all` 作用域原子更新单条、分组或全部配方的启用/置顶状态。
- `POST /custom-recipes/upsert?...`、`POST /custom-recipes/remove?id=...`、`POST /custom-recipes/move?id=...&direction=up|down`：新增/编辑、删除和调整同一稀客内的推荐优先级。
- `POST /updates/status`：归并并返回当前更新检查、下载、暂存和安装程序状态，以及 `lastAttemptAtUtc`、`lastSuccessAtUtc`、`nextCheckAtUtc`、`consecutiveFailures`；归并 updater 结果时可能写入或删除状态文件，因此不是只读查询。
- `POST /updates/check`、`POST /updates/download`、`POST /updates/install-on-exit`：手动检查、下载或启动退出安装流程；更新服务按单操作串行。后台调度成功后按配置间隔续检，失败按 15m/30m/1h/2h/4h/6h 退避。Local API 关闭时先阻止新操作，再通过统一生命周期令牌取消自动/手动检查和下载，等待 handler、活动操作及调度器退出；取消检查恢复稳定状态并立即到期。下次启动会恢复强制退出留下的瞬时状态，并只清理下载一级目录内严格符合本服务语义版本/GUID 格式的临时目录。

### v1.2.x 一次性迁移边界

`v1.2.x` 只保留一项明确的一次性数据迁移：Local API 启动阶段先把 `favorites.json` 中 `source=manual` 的料理原子写入 `custom-recipes.json`，写入成功后再从收藏文件删除旧条目。目标料理按 `customerId + foodTag + foodId + extraIngredientIds` 去重，中断后重试不会重复生成；目标文件无法读取或写入时不删除来源条目。后续 `GET /custom-recipes` 和 CRUD 端点保持无隐式迁移副作用。该迁移计划在 `v1.3.0` 删除，不得扩大为旧 API、旧配置、旧类型或旧业务路径兼容层。自 `v1.2.0` 起不再读取旧 GUID 配置 `com.tyukki.mystia-steward.cfg`。

除 `/health` 外，端点都需要 `X-Mystia-Steward-Companion-Token`。Token 由插件生成并保存在 BepInEx 配置中，同机启动伴随窗口时通过 `--token=` 参数传入 Tauri 后端；A 设备本机设置页可以复制或重置 Token。远程局域网连接时，用户需要在 B 设备伴随窗口顶部连接区手动输入 A 设备的 endpoint 和 token，点击 `连接` 后才开始轮询。Tauri 伴随窗口会显示实时 Mod 工作台，默认包含 `概览`、`普客`、`稀客`、`自定义推荐料理`、`经营中`、`任务`、`修改`、`帮助`、`设置` 九个页签；`概览` 内部按 `状态`、`库存`、`操作` 分栏，`设置` 内部按 `窗口`、`连接`、`推荐`、`自动化`、`更新` 分栏。窗口设置包含透明度、90% 至 130% 字体大小、焦点切换、始终置顶、鼠标穿透锁定、手柄导航和显示调试信息；连接设置包含本地 API/LAN 连接配置并逐项展示可复制的 endpoint；推荐设置包含订单排序、推荐权重、预算策略、缺失厨具过滤、任务料理/收藏料理/收藏酒水置顶、带库存显示和名称/库存排序的排除材料/酒水、同基础料理展示数量、游戏界面置顶和厨具高亮。工作台级更新控制器只读取 Mod 更新状态，活动状态 2 秒、稳定状态 60 秒轮询；发现新版时显示非模态提示，并按 endpoint + tag 保存 24 小时延后状态。Tauri opener 只允许打开本项目 Release URL。Android 伴随窗口只作为 B 设备 LAN 客户端，不提供桌面托盘、置顶、鼠标穿透、焦点切换、单实例控制和游戏关闭自动退出；独立 Windows 伴随窗口和 Android APK 不参与 Mod 主包自动更新。桌面鼠标穿透必须通过 Tauri 原生窗口 `set_ignore_cursor_events` 控制，不能只用 CSS `pointer-events` 模拟。帮助页内容来自 `apps/companion/src/data/help-content.json`，由前端渲染为目录树和详情面板，修改文案时优先改 JSON。`日志` 页签、扫描状态、运行时来源、性能耗时、订单来源和内部 key 这类诊断信息只在 `设置 -> 显示调试信息` 开启后显示。正式 Tauri 客户端通过原生后端读取本地 API。

伴随窗口的自动化能力只在设置页总开关开启并持有 lease 时运行。稀客并发、普客并发、最大重试和最大回退由 `CompanionPreferences` 控制；订单排序、推荐过滤、收藏限定和厨具预约仍复用经营中推荐的同一输入。稀客 `autoPrep*` 与普客 `autoNormal*` 的送酒、开始料理、送达料理、完成订单和出错暂停完全独立保存、独立传参、独立推进。所有自动开锅都登记 `AutomationCookingJob` 作为服务端精确锅次回执，防止 HTTP 响应丢失后再次扣料；只开启“开始料理”时 job 进入手动交接模式，不送达、不入箱、不复位，直到订单送达、订单稳定消失、场景结束或显式取消。

`AutomationCookingJob` 是料理跨帧状态的唯一来源。`RuntimeCookingGenerationTracker` 精确 Hook `CookController.SetCook(Sellable, Recipe, bool)`，每次 SetCook 分配 generation；job 绑定 controller 指针和该 generation。同 generation 内游戏原生完成流程替换 `Result` 可安全接续；Mod 不主动调用或重试非幂等的 `FinishCooking`，只等待原生 `Phase == Finished`，有效运行时间内长期不前进则进入人工确认。玩家手动取走成品会得到 `interrupted/cooking-result-removed`，立即复用同一厨具会因 generation 改变得到 `interrupted/cooking-controller-reused`，旧 job 不得送达、存储或 reset 新锅。快照 `automationCookingJobs` 暴露 jobId、目标、controller/result、generation、phase/progress、outcome/reason 和清理计数；`automationEvents` 用递增 sequence 发布终态，`automationSessionId` 标识当前 Mod 进程，断线后的同会话 lease 所有者据此接管。

自动化响应使用 `waiting/progressed/completed/interrupted/retryable-failure/blocked/fatal/cancelled` outcome，并携带 `stage/reasonCode/jobId/retryAfterMs`。C# 返回的 `beverage/cooking-start/cooking-delivery/order` 真实阶段必须优先于前端请求前推测，避免同一请求先送酒、后开锅失败时把料理副作用归到酒水阶段。前端用 `retryStage` 绑定失败计数，切换或关闭普通失败阶段时清除旧阶段退避。只有真实送酒、开锅、料理进度前进、送达提交或评价触发能报告 progressed；waiting 和 interrupted 不清零当前阶段失败次数，retryable-failure 有界累加。副作用不确定的 blocked/fatal 必须设置人工确认栅栏并保留 `prepared`，不能被普通重试、阶段开关、总开关或无关订单事实清除。前端以 request epoch 和事件 sequence 丢弃取消前或终态事件前发出的迟到响应，不再解析中文文本或使用前端经过时长猜测恢复。烹饪与送达超时只累计游戏可推进的有效区间，暂停、断线、场景不可读和运行时不可达不消耗预算；进度停滞会保留旧锅，因此必须是 blocked，而不是自动重新开锅的 interrupted。

正常料理直接送达订单；非目标成品、特殊经营目标签名变化、Tag 不符或目标连续不可达时才使用保温箱恢复。`IzakayaConfigure.StoreFood()` 是非幂等 commit-once 操作，IDA 显示它先 `StoredFoods.Add`，再调用 UI/伙伴回调；正常返回和异常后在 `StoredFoods` 中确认到同一 Sellable 对象都代表已提交。只要原生调用已经开始，异常后对象不存在或状态不可读都不能证明没有发生前置副作用，必须 blocked，且不得再次入箱或清厨具。`OrderBase.set_ServFood/set_ServBeverage` 同样先写最终字段再调用视觉回调；订单送达要以最终字段中的同一 Sellable 对象确认 commit，料理只做有界同 generation cleanup，酒水只在确认 commit 后扣一次库存。厨具 cleanup 必须同时确认 `Phase == Idle`、`Result == null`、`ChosenRecipe == null`，读取失败不得当作成功。普客 target 保存并严格匹配 `OrderKey`；key 缺失时只接受同一原生订单对象，不按桌号或料理回退。稀客 target 保存 trace 与料理/酒水 Tag。料理 job 不负责评价；后续订单阶段只在 `get_IsFullfilled()` 为真且严格读到 `HasEvaluated=false` 时调用一次评价。评价调用异常后只有严格回读为 true 才确认提交，否则登记未确认栅栏并禁止自动重试。

厨具出锅结果只能读取 `CookController.Result` 或其精确 backing field，并确认对象是料理 `Sellable` 后才能送达或进入保温箱恢复。`CookController.result` / `resultVisual` 是视觉 `SpriteRenderer`，不能作为成品对象；连续读到非 `Sellable` 或无法确认 generation 所有权时必须形成有界 blocked/interrupted 终态，不能无限等待或触碰其他锅次。

自动化在订单部分送达后恢复顾客耐心时，必须先读取同一 `GuestGroupController` 的 `CurrentPatient` 和 `MaxPatient`，再把 `AddPatient` 入参限制到剩余耐心空间内；若检测到当前耐心已经高于上限，只允许调用 `SetPatient(MaxPatient)` 做一次状态校正。游戏原生 `AddPatient` 不裁剪上限，而 `GuestTableDisplayer.UpdatePatient` 会先用 progress 索引贴图数组，因此不得恢复到 `MaxPatient` 以上。

稀客自动化诊断由前端状态机维护，每个当前候选订单都要暴露当前步骤、下次动作、已开锅、已送酒、重试/回退次数、最近原因、暂停状态和人工确认栅栏。普客自动化也要按订单 key 展示下次动作、送酒、开锅、送料理、完成订单和订单已有料理/酒水状态，避免只靠长文本判断卡住位置。订单级执行步骤必须保存在对应订单状态中，并在经营中页订单条目下方的 `自动化详情` 折叠区展示；默认全部折叠。普通 `重试` 只解除该订单暂停并保留已完成阶段，普通 `重置` 让该订单重新判断；人工确认栅栏禁用重试，按钮改为 `确认已处理`。订单已从快照消失时仍必须通过独立待确认列表暴露事件。确认动作调用 `/automation/barriers/ack`，只有后端成功解除 sequence 后才清理本地状态；任何操作都不得影响其他订单。

伴随窗口直接双击启动时通常没有本地 API Token。前端必须停留在未授权状态，不得高频请求 `/snapshot` 或日志端点；用户修改端点或 token 输入框时也不得立即重连，只有点击 `连接` 或从游戏启动参数收到新的连接身份后才恢复轮询。相同 endpoint/token 的重复单实例通知必须幂等，不得清空快照或推进连接代际。自动探测和失败重试必须使用较短本地 API 超时且不触发全局刷新 loading；手动刷新可使用稍长超时。连接失败后只按递增退避重试 `/snapshot`，并且只有快照成功才能清除错误和恢复写操作；`/health` 成功不能建立已连接状态。允许用户点击 `停止` 暂停自动重连。

普客订单自动化仍是实验性功能。设置页总开关和经营中“启用普客处理”必须同时开启，并至少开启一个独立阶段；订单按首次出现顺序处理，不保留手动处理按钮。酒水和料理统一提交到顾客桌面，只有订单已满足才评价，最终完成以 `HasEvaluated` 或订单移除为准。特殊经营规则按模块接入：`AutomationCookingJob` 同时保存原订单 match 目标、实际执行目标和场景签名，出锅时不能用执行料理反查原订单。幽幽子三阶段评价必须重新取得 live controller 和所需回调；怪诞料理大赛中的古明地恋本体在护盾期走通用评价，破防后交给 Boss 原生回调。具体规则见 `docs/special-business-scenes-notes.md`。

总日志文件 `BepInEx/config/MystiaStewardCompanion/aggregate-mod.log` 默认关闭，由 `Diagnostics.EnableAggregateModLog` 或日志页“总日志”开关启用。启用后注册 BepInEx 全局 `ILogListener`，捕获所有日志源并按时间、级别、来源和线程标注；自动化日志记录 jobId、trace、controller/result、generation、phase/progress、结构化 outcome/reason、StoreFood commit 和 reset 尝试。连续相同 automation action、目标和消息合并为 `repeat` 摘要。单个文件达到 10 MB 后拆分为递增编号分片；默认保留 30 个文件，约 300 MB。监听器不得回写自身状态，写入、分片和裁剪失败也不得影响游戏流程。

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
- 伴随窗口是唯一用户界面；游戏内不再提供备用 IMGUI 面板。
