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

该参数会在 Windows 伴随窗口、Mod 包和 Windows 独立伴随窗口包生成后，额外执行 `pnpm tauri:android:apk:signed`，并把签名 APK 放到 `mods\bepinex\dist\mystia-steward-companion-android-universal.apk`。默认不启用该参数，避免普通 Windows-only 构建被 Android 工具链或 keystore 绑定。

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

`pnpm tauri:android:apk` 可用于本地构建验证，默认输出未签名 universal release APK：

```text
apps/companion/src-tauri/gen/android/app/build/outputs/apk/universal/release/app-universal-release-unsigned.apk
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
mods/bepinex/dist/mystia-steward-companion-android-universal.apk
```

也可以通过 `build-release.ps1 -BuildAndroidApk` 或 `publish-release.ps1 -BuildAndroidApk` 在本地构建/发布流程中自动生成该文件。如果 APK 放在其他位置，发布时通过 `publish-release.ps1 -AndroidApkPath "D:\path\mystia-steward-companion-android-universal.apk"` 指定。APK 只作为 GitHub Release 的独立下载资产，不写入 `update-manifest.json`，也不参与 Mod 自动更新。

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

报告和截图默认写到 `/tmp/mystia-companion-ui-audit`。如果使用 `pnpm preview`，把 `MYSTIA_APP_URL` 改成 Vite preview 输出的地址，通常是 `http://127.0.0.1:4173`。

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
mods/bepinex/dist/mystia-steward-companion-companion-windows-x64.zip
mods/bepinex/dist/mystia-steward-companion-android-universal.apk
```

PowerShell 7 脚本固定生成 `.zip`；bash 脚本在系统没有 `zip` 时会改为生成 `.tar.gz`。打包脚本会在检测到 `apps/companion/src-tauri/target/release/mystia-steward-companion(.exe)` 时自动复制到安装包的 `companion/` 子目录，并把 `mystia-steward-companion-updater(.exe)` 放在插件目录根部。检测到 Windows `.exe` 时，还会生成 `mystia-steward-companion-companion-windows-x64.zip`，供其他设备只下载伴随窗口并通过 LAN 连接。Android APK 由 Tauri mobile/Android 工具链单独构建和签名，打包脚本不会从 Windows EXE 派生 APK。Windows 下该 updater 会显示独立更新窗口，负责提示关闭游戏、展示阶段进度并在游戏退出后替换插件目录。

## 本地发布

本地发布方案见仓库根目录的 `docs/local-release.md`。仓库不使用 GitHub Actions 自动构建 Release；版本发布需要在 Windows 本机构建完整产物后通过 GitHub CLI 上传。

GitHub Release 上传以下资产：

- `mystia-steward-companion-bepinex.zip`
- `update-manifest.json`
- `mystia-steward-companion-companion-windows-x64.zip`
- 可选：`mystia-steward-companion-android-universal.apk`

`update-manifest.json` 给 Mod 内置自动更新使用，只包含版本、资产文件名、zip 大小和 SHA256，不记录本机打包路径，并且只指向 `mystia-steward-companion-bepinex.zip`。独立 Windows 伴随窗口包和 Android APK 只给 B 设备跨局域网连接使用，不参与 Mod 自动更新。不上传 Tauri setup 安装器，避免用户误以为只安装桌面程序即可使用 Mod。

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

脚本会先执行完整构建，再用 `gh release create` 创建 Release 并上传 Mod zip、独立伴随窗口 zip 与 update-manifest。

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

推荐、库存名称、任务目标和自动化目标解析使用游戏运行时读取到的 `RuntimeDataCatalog`。伴随窗口未连接游戏、游戏数据库未初始化或 `/snapshot.runtimeData.isComplete=false` 时，页面会显示等待运行时数据。

发布包包含 Mod DLL 和伴随窗口程序，推荐、库存、任务和自动化目标都来自游戏当前运行时。

## 运行时刷新行为

Mod 会定期检查当前页面和游戏运行时状态。进入游戏并加载进度后，推荐状态来自当前内存中的运行时对象，不读取 `.memory` 存档文件。

运行时固定数据读取成功后，C# 会把 `DataBaseCore`、`DataBaseCharacter` 和 `DataBaseLanguage` 中的料理、食材、酒水、普客、稀客和 tag 映射构造成 `RuntimeDataCatalog`，写入本地 API 快照并切换 C# 推荐仓库到运行时仓库。伴随窗口概览页的“推荐数据”显示“游戏运行时”时，表示前端推荐算法已经获得完整运行时数据。

运行态读取不再依赖固定秒数等待。日间任务列表、当前日间地图和稀客邀请通过 `DayScene.SceneManager.CurrentActiveMapLabel` / `TargetMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`RunTimeDayScene.RefTrackedNPCAvailability()`、`DaySceneMap.allCharacters` 和 `RunTimeScheduler` 等运行态入口读取，不再把 `DaySceneSustainedPannel` 面板激活状态作为总门禁；夜间经营准备读取要求 `PrepNightScene.UI.IzakayaConfigPannel.OnPanelOpen` / `GoToSpecific` 已触发，且 `WorkPrepScenePannelRoot` 下的 `IzakayaConfigPannelNew` 仍激活。准备阶段只读取库存、已解锁、流行 Tag 等基础玩家运行态，因此 `修改`、`普客` 和 `稀客` 页面可以提前使用；任务列表、当前日间地图和稀客邀请仍只在日间场景读取。

推荐状态中的库存、酒水和已解锁料理使用 `RunTimeStorage.GenerateSaveData()` 生成的一份当前运行时存储快照作为权威来源；玩家等级、流行 Tag、明星店开关和联动状态继续使用轻量 getter 或静态字段读取。若存储快照中的 `recipes` 为空，Mod 会等待下一轮运行时读取，不会向伴随窗口发布空的可用料理集合。

为降低经营中掉帧风险，本地 API 快照发布会做轻量节流：Unity 主线程最多约每 0.35 秒刷新一次缓存 JSON；若快照内容签名未变化，会复用上一份缓存 JSON，不为了 `CapturedAtUtc` 或性能数字重复序列化；完整 `RuntimeDataCatalog` 只在首次、内容变化、强制刷新或约每 10 秒补发一次，其余快照可能省略 `runtimeData`。运行时固定数据已经完整读取后，不再从经营快照热路径反复触发静态数据扫描；读取未完整时也按约 5 秒间隔重试，避免 `runtimeData.staticData` 在每轮经营刷新里反复消耗主线程。伴随窗口需要缓存最近一次完整运行时数据，不能把缺失的 `runtimeData` 当作数据丢失。概览页和经营中页会显示 `performanceMs` 中最近约 12 秒内耗时最高的快照环节，排查卡顿时优先记录 `refresh.business`、`refresh.runtime`、`snapshot.serialize`、`automation.collect` 和 `snapshot.publish`。经营扫描还会细分 `business.rare.*`、`business.normal.*`、`runtime.cookerSnapshot`、`mission.serveTargets` 等子项；普客订单快照会在短时间内复用，避免同一轮 `/snapshot` 发布重复枚举 `OrderController`、HUD 和 `GuestsManager`。

普客订单被动快照缓存约 1 秒。`NormalOrderRuntimeCapture` 通过 `GuestGroupController.PushToOrder` 捕获订单与可执行客人控制器绑定，不能为了性能删除；常规快照先读捕获缓存，捕获已有可解析订单时直接返回，捕获为空或不可用时才做 `OrderController`、HUD 和 `GuestsManager` 启动扫描。捕获版本变化后只强制刷新普客订单快照，不重新扫描稀客经营上下文。pending 出锅直送没有待办项时不在 `Update()` 热路径轮询。游戏界面置顶只在料理、材料和酒水面板字段刷新后重排列表，不再 Hook `RunTimePlayerData.CheckPinned` 这类全局高频查询。

夜间经营中，`经营中 / Service` 页优先使用 `SpecialOrderRuntimeCapture` 捕获到的稀客订单缓存；捕获缓存为空、诊断开启或需要初始化/回退校验时，再扫描 `GuestsManager`、稀客队列、`OrderController`、HUD、服务面板和桌位控制器中的订单。页面仍会读取桌位控制器中的活动稀客，用于显示当前稀客和 `GuestGroupController.GetFund`、`BaseFundCarry`、`MaxFundCarry` 等当前携带金钱信息，但捕获缓存已有订单时不应重复做完整订单反射扫描。普客订单使用 `NormalOrderRuntimeCapture` 记录 `GuestGroupController.PushToOrder` 建立的订单归属；HUD / `OrderController` 只作为捕获为空的启动扫描和诊断来源，不能单独作为自动送达或评价依据。页面顶部只展示经营场景、扫描状态、推荐数据、厨具与置顶状态等通用信息，随后用 `稀客` / `普客` 页签分区展示各自功能。稀客点单后，工作台会按桌号列出稀客、料理词条和酒水词条，并复用稀客推荐算法计算候选料理、加料和酒水。普客订单读取到 `GameData.CoreLanguage.LanguageBase` 这类 IL2CPP 本地化对象时，必须过滤为无文本，不得把运行时类型名当作客人、料理或酒水名称展示。

若 IL2CPP getter 无法读取订单列表，Mod 会继续尝试 `AllOrdersData` 和 `PeekOrders()`；若 tag ID 读取失败，会从稀客控制器的订单文本方法读取中文词条。

稀客推荐结果会按角色、点单词条、库存状态、厨具快照、排序配置、置顶开关、同基础料理展示数量和加料上限缓存。经营中展示行应从完整排序候选池派生统一料理/酒水列表；不满足点单但命中稀客喜好的候选直接进入同一列表并标注为偏好备选，不能被自动化执行候选上限提前裁掉。自动化目标只对少量独立执行候选做组合选择，不能依赖 UI 裁剪后的展示行。自动刷新没有检测到相关变化时，不会在每个刷新周期重复枚举加料组合；排序配置、置顶开关或同基础料理展示数量变化必须进入缓存签名，否则用户调整设置后会继续看到旧顺序或旧展示数量。

收藏数据由 Mod 本地 API 持久化到 `BepInEx/config/MystiaStewardCompanion/favorites.json`。前端只通过 `/favorites`、`/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` 读写，不使用 localStorage 存储收藏，避免版本更新或 WebView 数据迁移时丢失。

如果没有检测到运行时数据，普客和稀客推荐页只显示运行时数据不可用，不会回退到“全内容可用”状态，避免误以为库存和解锁内容已经同步。

开启经营诊断后，`night-business-diagnostics.log` 会额外输出 `Candidates` 和 `RecentRuntimeParseFailures`。前者记录被扫描到的 controller/order 候选、接纳状态和过滤原因；后者记录运行时订单捕获器最近未能解析为稀客订单的样本。排查映射稀客或特殊事件稀客时，优先查看这两段。

同一目录还会输出运行时固定数据快照，默认目录为 `BepInEx/config/MystiaStewardCompanion/`：

- `runtime-static-data.log`：`DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 运行时同名别名，日志中的 `aliasSource` 会标明归一化来源。
- `runtime-tags.log`：`DataBaseLanguage` 的料理/酒水标签文本、DLC 标签映射，以及 `DataBaseCore.TagRules`。
- `runtime-database-diff.log`：`DataBaseCore` 食材、酒水、菜品、料理运行时表；每个表会记录 `GetAllX` 方法读取结果，以及游戏静态字典 fallback 的读取结果。
- `runtime-guests.log`：`DataBaseCharacter` 普客、稀客、映射稀客、原始稀客映射和 `GuestFoodEasterEggData` 类型/简单字段。
- `runtime-izakayas.log`：`DataBaseCore.GetAllIzakayas()` 或静态 `Izakayas` 字典读取到的经营场景标签、等级、普通/稀客池和刷新参数。

固定数据快照只在 `Diagnostics.EnableNightBusinessDiagnostics=true` 且 `NightBusinessReflectionProvider.LoadContext()` 被调用时写入；也就是说，通常需要进入游戏并让伴随窗口/Mod 触发一次经营数据刷新。若游戏的 `DataBaseCore`、`DataBaseLanguage` 或 `DataBaseCharacter` 尚未初始化，快照会记录缺失状态并按 5 秒间隔重试。判断读取成功时优先看日志头部 `Complete: True` 和 `Status` 中各类计数是否大于 0。

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

`LanHost=auto` 会监听检测到的私网 IPv4，也可以写 A 设备的具体局域网 IPv4。LAN 通道仍要求除 `/health` 外的所有端点携带 `X-Mystia-Steward-Companion-Token`，服务端会拒绝非私网来源；连接配置和 Token 重置端点只允许 A 设备本机回环客户端调用。用户仍可能需要在 Windows 防火墙中允许该端口入站。不要把该端口通过公网端口映射暴露出去。

端点：

- `GET /health`：检查本地 API 是否启动，不需要 token。
- `GET /local-api/config`：读取本机 endpoint、LAN listener 状态、LAN endpoint 和当前 Token；只允许回环客户端调用。
- `POST /local-api/config?lanEnabled=true|false&lanHost=auto|IPv4`：由 A 设备本机伴随窗口保存 LAN 开关和监听地址，并动态启停 LAN listener；本机回环 listener 不重启也不关闭。
- `POST /local-api/token/regenerate`：重置本地 API Token，返回新 Token 并立即更新当前 API 鉴权；只允许回环客户端调用。
- `GET /snapshot`：读取最新运行态快照。快照由 Unity 主线程按自动刷新节奏生成，网络线程只返回缓存 JSON。快照包含推荐状态、夜间稀客订单、任务状态、经营投喂任务目标、普客订单诊断和 `performanceMs` 快照耗时；任务状态优先遍历 `RunTimeScheduler.trackingMissions` 并调用只读 `RunTimeScheduler.ParseActiveMissionData()`，再结合全局 NPC、当前场景 NPC、`DaySceneMap`、当天/常驻 `RunTimeScheduler.scheduledEvents` 后置任务、跟踪交互物件、场景任务交互组件和未完成 `trackingMissions` fallback 补充来源，并优先从 `RunTimeDayScene.trackedNPCs` 反查 NPC 所在场景，缺失时用 `DataBaseDay.RefNPC().possibleDestinations` 解析可能场景。夜间经营时会通过 `ContainsSpecialNPCServeInWorkMission()` 读取当前稀客是否有已接取的投喂任务指定料理；普客诊断会扫描 HUD 订单和经营管理器桌位订单。完整 `runtimeData` 可能被节流省略，前端必须复用最近一次完整数据。
- `GET /logs/settings`：读取日志读取、经营诊断和 BepInEx 原生日志窗口开关状态。
- `GET /logs/config?logAccess=true|false&diagnostics=true|false&nativeConsole=true|false`：由伴随窗口回写日志、诊断和 BepInEx 原生日志窗口开关；`nativeConsole` 会同时尝试显示/隐藏当前 Windows 控制台，并写入下一次启动的 `BepInEx.cfg`。
- `GET /logs/open-folder?target=log|diagnostics`：打开对应日志目录。
- `GET /logs`：在 `LocalApi.ExposeLogs=true` 时读取 `BepInEx/LogOutput.log` 尾部日志，按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪。
- `GET /inventory/set?type=ingredient|beverage&id=ID&qty=数量`：在 Unity 主线程修改当前运行时材料或酒水库存。
- `GET /inventory/bulk-set?type=ingredient|beverage&ids=ID1,ID2&qty=数量`：批量修改当前运行时材料或酒水库存；用于修改页的材料/酒水批量设为 `99`，只在批量结束后刷新一次运行时快照。
- `GET /orders/prepare-next?...`：按伴随窗口传入的稀客订单执行准备步骤，可组合送达酒水、开始料理、出锅后直送和收藏限定。
- `GET /logs/automation`：读取 `BepInEx/config/MystiaStewardCompanion/automation-jobs.log` 尾部内容，返回结构与 `/logs` 一致，受日志读取开关和读取上限控制。
- `GET /logs/export-diagnostics?open=true`：生成诊断 zip，包含 manifest、当前 snapshot、`LogOutput.log` 尾部、自动化作业日志尾部和诊断目录中的 `.log` 尾部；`open=true` 会打开诊断包目录。
- `GET /orders/complete-first?...`：按伴随窗口传入的稀客订单确认直接送达状态，必要时补送酒水，并在订单满足后触发评价。
- `GET /orders/rare/dismiss?...`：按桌号和点单 Tag 删除一笔运行时稀客订单捕获缓存，用于清理偶发未被游戏移除事件命中的过时订单。
- `GET /orders/normal/complete-first?...`：按请求中的订单 key、桌位、料理和酒水处理一笔普客订单。普客自动化可按 `autoNormal*` 阶段配置送达酒水、开始料理、出锅后直接送达料理，并在订单 `get_IsFullfilled()` 为真后调用 `EvaluateOrder()` 完成评价；该字段只表示订单已满足并可评价，前端仍需以 `HasEvaluated` 或订单消失判断真正完成。若订单只存在于 HUD / `OrderController`，但没有可执行 `GuestGroupController`，后端必须拒绝自动送达并返回不可执行诊断。
- `GET /rare-guests/invitations?scope=current|all`：排队到 Unity 主线程，返回指定范围内的稀客邀请候选、当前已邀请列表和禁用原因。列表查询应默认返回全量候选，前端再按羁绊等级筛选显示，避免切换筛选时丢失其他等级选项。
- `GET /rare-guests/invite-all?scope=current|all&levels=2,3`：按同一套候选扫描和判定逻辑批量邀请可邀请稀客；`levels` 可选，只邀请指定羁绊等级的可邀请项。`current` 候选优先使用 `DayScene.SceneManager.CurrentActiveMapLabel`、`RunTimeDayScene.GetMapNPCs()`、`DaySceneMap.allCharacters` 和场景中的 `CharacterConditionComponent`，若这些实时对象还未填充，则按当前地图反查 `DataBaseDay.GetAllNPCKeys()`、`AllMappedNPCsMapping`、`AllNPCsMapping` 或 `allNPCs` 中的 NPC key，再通过 `RefNPC().possibleDestinations` 判断所在地图，并用 `RunTimeDayScene.RefTrackedNPCAvailability()` 判断当前范围内的运行时可见性。`all` 候选会合并当前场景候选和全部日间静态 NPC 候选；全部静态候选不使用当前时间可见性作为硬过滤，避免 `TrackedNPC.ShouldShown(RemainActions)` 误删跨场景候选。当前场景候选为空时直接失败，不回退到 `DataBaseCharacter.GetSpecialGuestsAndMappedGuests()` 执行全量邀请。每个候选会读取 `RunTimeAlbum.GetOrGenerateSpecialNPCKizunaLevel()`、检查 `StatusTracker.HasNPCInvited()` 和当前等级成功邀请对话包；符合条件后直接调用 `StatusTracker.RecordInvitedGuest()` 写入今晚邀请名单。该端点不调用 `DaySceneChatSelectionPannel.InviteSpecGuest()`，避免触发随机失败和消耗今日尝试次数；也不以 `HasTemptInvited()` 作为跳过条件，避免旧版本或手动失败尝试把可写入邀请卡住。该端点不直接刷出稀客，不推进时间，不写 `Story.SpecialGuestControlled`。

除 `/health` 外，端点都需要 `X-Mystia-Steward-Companion-Token`。Token 由插件生成并保存在 BepInEx 配置中，同机启动伴随窗口时通过 `--token=` 参数传入 Tauri 后端；A 设备本机设置页可以复制或重置 Token。远程局域网连接时，用户需要在 B 设备伴随窗口顶部连接区手动输入 A 设备的 endpoint 和 token，点击 `连接` 后才开始轮询。Tauri 伴随窗口会显示实时 Mod 工作台，默认包含 `概览`、`普客`、`稀客`、`经营中`、`任务`、`修改`、`帮助`、`设置` 八个页签；`概览` 内部按 `状态`、`库存`、`操作` 分栏，`设置` 内部按 `窗口`、`连接`、`推荐`、`自动化`、`更新` 分栏，调试开关开启后才显示 `调试` 分栏。窗口设置包含透明度、焦点切换、始终置顶、鼠标穿透锁定、手柄导航和显示调试信息；连接设置包含本地 API/LAN 连接配置；推荐设置包含订单排序、推荐权重、预算策略、缺失厨具过滤、任务料理/收藏料理/收藏酒水置顶、带库存显示和名称/库存排序的排除材料/酒水、同基础料理展示数量、游戏界面置顶和厨具高亮。Android 伴随窗口只作为 B 设备 LAN 客户端，不提供桌面托盘、置顶、鼠标穿透、焦点切换、单实例控制和游戏关闭自动退出；桌面鼠标穿透必须通过 Tauri 原生窗口 `set_ignore_cursor_events` 控制，不能只用 CSS `pointer-events` 模拟。帮助页内容来自 `apps/companion/src/data/help-content.json`，由前端渲染为目录树和详情面板，修改文案时优先改 JSON。`日志` 页签、设置中的 `调试` 分栏、BepInEx 原生日志窗口控制、扫描状态、运行时来源、性能耗时、订单来源和内部 key 这类诊断信息只在 `设置 -> 显示调试信息` 开启后显示。它通过 Tauri 原生后端读取本地 API。

伴随窗口的自动化能力只在前端 `设置` 页总开关开启后运行。稀客并发、普客并发、最大重试和最大回退都由 `CompanionPreferences` 配置控制，默认值分别为 `2`、`3`、`3`、`2`；稀客完成订单评价每轮仍最多执行 1 笔，普客按普客并发数处理。经营中订单排序支持点单顺序和稀客分组，必须同时影响经营中列表、专注模式、游戏界面置顶和自动化选单；料理/酒水排序配置会影响稀客页、经营中页、专注模式和自动化选单，新增排序或置顶规则时需要同时覆盖这些入口。同基础料理展示数量只裁剪页面推荐行，自动化必须从独立执行候选构造目标，不能因为页面隐藏了加料变体而跳过可执行方案。预算策略、任务料理/收藏料理/收藏酒水置顶、排除材料和排除酒水都必须进入推荐链路和缓存签名；预算可阻止、提示或忽略超预算方案，排除材料需要同时过滤基础配方和加料。任务料理置顶只在当前稀客有已接取投喂任务且目标料理通过解锁、库存、预算、排除项和缺失厨具过滤后生效；收藏料理置顶和收藏酒水置顶分别只影响对应列表，且不绕过硬过滤。偏好命中但不满足点单的料理/酒水直接进入统一推荐列表并标识为 `偏好备选`，自动化根据排序后的执行候选锁定目标；收藏限定开启时，锁定目标仍必须命中收藏。稀客与普客自动化的阶段配置必须独立保存和独立传参：稀客使用 `autoPrep*` 配置，普客使用 `autoNormal*` 配置；普客阶段包括送达酒水、开始料理、送达料理、完成订单和出错暂停，不能复用稀客送酒或完成订单开关。自动开始料理固定尝试完成原生 QTE 奖励结算，不提供跳过开关。普客自动化需要按订单 key 维护独立状态，非临时错误只暂停对应普客订单，不得暂停稀客自动化或其他普客订单；已进入制作中的普客料理必须绑定目标订单/桌位，后续轮询检测到 pending 后只能等待，不得在同类多个厨具上重复开始同一订单料理。普客订单变化需要立即触发一次处理，常规重复轮询仍需节流。C# pending target 必须优先保存并匹配 `OrderKey`，避免桌位复用或同料理多单时串单；如果游戏重建普客订单对象导致旧 key 失效，只能在桌号、料理和酒水仍一致时重匹配当前订单继续直送。稀客与普客的开锅请求必须经过前端同一轮厨具预约表，预约容量来自当前已摆放厨具快照；同类厨具容量不足时，普客待处理订单优先保留容量，稀客订单进入等待态并继续处理不占厨具的送酒/完成步骤。稀客和普客都必须支持料理和酒水单项先送达：送达提交必须同步顾客桌面显示和订单状态，只有 `get_IsFullfilled()` 为真时才能调用 `EvaluateOrder()`；`get_IsFullfilled()` 不是终态字段，普客快照还需要区分 `ReadyToEvaluate` 和 `HasEvaluated`。酒水创建对象后必须在送达提交成功后才扣库存；料理出锅后直接送达目标订单，送达失败时保留成品和 pending 以便下一轮重试。子选项默认关闭并记忆用户上次配置。临时失败例如厨具占用、运行时对象暂不可读、桌面显示暂不可写，应保持可重试，不应永久停止自动任务；非临时错误在对应订单类型的 `出错时暂停` 开启时才暂停当前订单。前端状态机只将送酒、开锅、单项送达提交和触发评价视为真实进展；稀客页下拉选项不再按存档进度集合过滤，只按经营场景、可读名称和可用 Tag 过滤。

自动化在订单部分送达后恢复顾客耐心时，必须先读取同一 `GuestGroupController` 的 `CurrentPatient` 和 `MaxPatient`，再把 `AddPatient` 入参限制到剩余耐心空间内；若检测到当前耐心已经高于上限，只允许调用 `SetPatient(MaxPatient)` 做一次状态校正。游戏原生 `AddPatient` 不裁剪上限，而 `GuestTableDisplayer.UpdatePatient` 会先用 progress 索引贴图数组，因此不得恢复到 `MaxPatient` 以上。

稀客自动化诊断由前端状态机维护，每个当前候选订单都要暴露当前步骤、下次动作、已开锅、已送酒、重试/回退次数、最近原因和暂停状态。普客自动化也要按订单 key 展示下次动作、送酒、开锅、送料理、完成订单和订单已有料理/酒水状态，避免只靠长文本判断卡住位置。`重试` 只解除该订单暂停并保留已完成阶段，`重置` 删除该订单本地状态并在下一轮重新判断；两者都不得影响其他稀客订单或普客订单状态。

伴随窗口直接双击启动时通常没有本地 API Token。前端必须停留在未授权状态，不得高频请求 `/snapshot` 或 `/logs`；用户修改端点或 token 输入框时也不得立即重连，只有点击 `连接` 或从游戏启动参数收到新 token 后才恢复轮询。自动探测和失败重试必须使用较短本地 API 超时且不触发全局刷新 loading；手动刷新可使用稍长超时。连接失败后使用递增退避，允许用户点击 `停止` 暂停自动重连。

普客订单自动化仍是实验性功能。伴随窗口会显示当前 UI 订单里识别到的普客桌位、料理、酒水、待评价和已评价状态；设置页开启自动化总开关后，还需要在经营中自动化面板开启“启用普客处理”，并至少开启送达酒水、自动开始料理、自动送达料理或自动完成订单中的一个阶段，之后会自动处理按首次出现时间排序的未评价普客订单，不再需要点击手动处理按钮。普客酒水和料理送达都走统一送达提交，在顾客桌面显示和订单状态同步后才调用 `EvaluateOrder()`。特殊经营场景不再接入运行时推荐和自动化分支；已分析过的怪诞料理大赛、饕餮尤魔挑战链路记录在 `docs/special-business-scenes-notes.md`，后续若恢复适配必须重新验证原生评价副作用。`ServedFoodInAir` / `ServedBeverageInAir` 只作为统一送达提交中的过渡状态，顾客桌面显示必须通过 `GuestTableDisplayer` 更新，是否可评价以 `ServFood` / `ServBeverage` 和 `get_IsFullfilled()` 为准，是否真正完成以 `HasEvaluated` 或订单移除为准。

自动化诊断文件 `BepInEx/config/MystiaStewardCompanion/automation-jobs.log` 由 C# 侧写入，记录开锅成功/失败、pending 直送、pending 移除和目标订单信息，约 1 MB 自动轮换为 `.1`。连续相同 action、目标和消息会合并为 `repeat` 摘要，避免厨具冻结、订单对象短暂不可读等临时状态刷爆日志。伴随窗口 `日志` 页通过 `/logs/automation` 读取并解析该文件，显示 action、target、桌号、订单 key、料理和最近消息；`导出诊断包` 通过 `/logs/export-diagnostics` 生成 zip，所有日志内容都只取尾部上限。该日志只用于排查，不得让写入、读取或打包失败影响自动化或游戏运行。

代理工具注意事项：

- 默认同机使用 `127.0.0.1`，不要改成 `localhost`。
- LAN 连接只支持明确的私网 IPv4 endpoint，例如 `http://192.168.1.20:32145`；Tauri 代理会拒绝公网地址、`0.0.0.0` 和 HTTPS。
- 若代理扩展或系统代理拦截本地请求，将 `127.0.0.1`、`localhost`、A 设备局域网 IP 和回环地址加入直连/绕过列表。
- 若同机伴随窗口无法连接，先确认日志中出现 `Local API loopback listener is available at http://127.0.0.1:32145`，再检查端口占用。
- 若 B 设备无法连接，先在 A 设备设置页连接面板确认 LAN 状态和 LAN 地址，再检查 Windows 防火墙入站规则。
- 受保护端点需要 token；调试伴随窗口时使用 Tauri 运行环境或显式携带 token 的本地客户端。

## 输入处理

游戏内不再保留 IMGUI 面板。Mod 在游戏侧只处理 F8/RS Click 热键、后台读取、本地 API 和自动化执行；用户交互全部放在 Tauri 独立伴随窗口中。伴随窗口获得焦点时由独立进程消费鼠标、键盘和手柄输入，不需要拦截游戏内 IMGUI 事件。Tauri 侧 `F10` 全局热键用于切换鼠标穿透锁定；`F8`、`RS Click`、单实例 `show` 控制消息和托盘显示/重连菜单必须自动关闭穿透，防止用户找回窗口后仍无法点击。

## 调试建议

- `preflight.ps1` 报 DLL 缺失：先启动一次已安装 BepInEx 的游戏，再从 `BepInEx/core` 和 `BepInEx/interop` 复制所需引用。
- 构建报 `Il2Cppmscorlib` 缺失：从 `游戏根目录/BepInEx/interop/Il2Cppmscorlib.dll` 复制到 `References/`。
- PowerShell 执行 `bash ...` 报 WSL `/bin/bash` 不存在：在 Windows 下改用对应 `.ps1` 脚本。
- 运行时数据不可用：查看设置页场景名、扫描状态和 `BepInEx/LogOutput.log`。
- `经营中` 没有稀客或点单：查看 `经营扫描 / Scan status`；如果 `manager=missing`，需要核对夜间经营管理器字段；如果 `guests>0` 但 `orders=0`，提供经营诊断中的 `Sources`、`Candidates` 和 `RecentRuntimeParseFailures`。

## 已知限制

- 构建依赖本机 `References/` 中的 BepInEx、Il2CppInterop 和 Unity DLL；这些 DLL 不提交到仓库。
- 运行时反射依赖游戏版本中的类型和字段名；如果游戏更新导致字段变化，需要核对并调整 provider 中的运行时类型名、字段名和方法名。
- 伴随窗口是唯一用户界面；游戏内不再提供备用 IMGUI 面板。
