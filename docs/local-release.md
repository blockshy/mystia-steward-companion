# 本地构建与发布方案

## 发布方式

本项目不再使用 GitHub Actions 自动构建 Release。发布采用：

```text
本机 Windows 构建完整产物 -> GitHub CLI 上传 Release
```

原因是 Mod 编译依赖 BepInEx、Il2CppInterop 和 Unity interop DLL。这些 DLL 不提交到仓库，也不上传到 GitHub runner。

## 本机要求

发布机器需要是 Windows，并预装：

- Node.js `24.19.0`、Corepack `0.35.0`，并通过 Corepack 使用仓库固定的 `pnpm@10.10.0`。
- .NET SDK `10.0.110`。Mod 发布目标仍是 `net6.0`，不要为此安装已停止支持的 .NET 6 SDK；
  运行一般 net6 smoke 时可在当前 PowerShell 设置 `$env:DOTNET_ROLL_FORWARD = "Major"`。
- Rust/Cargo `1.97.1`，通过 rustup 安装；不要用会随时间变化的 `stable` 作为发布工具链。
- Microsoft C++ Build Tools 2022 或 Visual Studio “使用 C++ 的桌面开发”组件。
- Microsoft Edge WebView2 Runtime。
- PowerShell 7。
- GitHub CLI，并完成 `gh auth login`。
- 需要完整复现三项真实 Harmony/MonoMod 动态补丁 smoke 时安装 Docker Desktop，并运行
  `corepack pnpm test:dotnet6-harmony` 使用锁定的 .NET 6 SDK 容器；不要删除这些运行时探针。

根目录 `toolchain.lock.json` 是上述版本的唯一基线，`.nvmrc`、`global.json` 和
`rust-toolchain.toml` 会分别选择 Node、.NET SDK 与 Rust；`package.json` 继续以版本加完整性哈希锁定
pnpm。正式构建不接受较新补丁、全局 pnpm 或缺失 Corepack 的回退。Windows x64 初始化建议使用同一套
Node 安装方式，不要同时叠加官方 MSI 和 nvm-windows；安装完成后执行：

```powershell
npm install --global corepack@0.35.0
corepack enable
corepack install
rustup toolchain install 1.97.1 --profile minimal
corepack pnpm toolchain:check
```

如果 Node 官方 MSI 自带的 Corepack 阻止全局升级，先在 Node 安装程序的“修改”界面移除 Corepack
Manager 组件，再安装上方精确版本。`.NET SDK 10.0.110` 建议使用独立 x64 SDK 安装包，避免 Visual
Studio 更新替换它。检查结果必须依次为 Node `v24.19.0`、Corepack `0.35.0`、pnpm `10.10.0`、
.NET SDK `10.0.110`、rustc/cargo `1.97.1`；发布脚本会再次执行同一硬门禁。

如需同时发布 Android APK，还需要 Android Studio/SDK/NDK、JDK 17、Android Rust targets，并完成 APK 签名配置。Android APK 是 Tauri mobile 的单独构建产物，不从 Windows EXE 转换。

`mods/bepinex/References/` 需要包含：

```text
BepInEx.Core.dll
BepInEx.Unity.IL2CPP.dll
0Harmony.dll
Il2CppInterop.Runtime.dll
Il2Cppmscorlib.dll
UnityEngine.CoreModule.dll
UnityEngine.InputLegacyModule.dll
```

## 版本号与发布通道

发布前先同步项目内版本号。脚本会同时修改 `package.json`、`tauri.conf.json`、`Cargo.toml`、`Cargo.lock` 和 Mod 的 `PluginVersion`：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\set-version.ps1 -Version 1.1.0
```

Linux 开发环境可使用等价脚本：

```bash
bash mods/bepinex/tools/set-version.sh 1.1.0
```

自动更新发布只支持两种公开通道：

- 稳定版：`X.Y.Z`，例如 `1.1.0`，发布为普通 GitHub Release。
- 预览版：`X.Y.Z-preview.N`，例如 `1.1.0-preview.1`，发布为 GitHub Prerelease。

`publish-release.ps1` 会强制校验通道和 tag：

- `v1.1.0-preview.1` 必须带 `-Prerelease` 或 `-Preview`。
- `v1.1.0` 不能带 `-Prerelease`。
- 其他后缀，例如 `alpha`、`beta`、`rc`，不进入自动更新发布流程。

版本号同步后先提交到 `dev`：

```powershell
git add package.json apps\companion\src-tauri\Cargo.toml apps\companion\src-tauri\Cargo.lock apps\companion\src-tauri\tauri.conf.json mods\bepinex\src\Plugin\MystiaStewardCompanionPlugin.cs
git commit -m "chore(release): bump version to 1.0.1"
git push origin dev
```

稳定版确认可发布后，再合并到 `main`，并在 `main` 上执行发布脚本。预览版只用于更新链路测试，通常保留在 `dev` 上打 tag 并发布 GitHub Prerelease，不合并 `main`。

`publish-release.ps1` 会根据 `-Tag` 校验代码版本。如果代码仍是旧版本，脚本会失败并提示先运行 `set-version.ps1`。

## 预览版更新测试流程

预览版用于验证自动更新链路，典型流程如下：

```text
v1.1.0-preview.1
↓ 测试检查、下载、打开安装程序并完成安装
v1.1.0-preview.2
↓ 修复问题后再次测试
v1.1.0
↓ 正式发布
```

发布预览版时，在 `dev` 上同步预览版本号、提交并推送，然后创建并推送 tag：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\set-version.ps1 -Version 1.1.0-preview.1

git add package.json apps\companion\src-tauri\Cargo.toml apps\companion\src-tauri\Cargo.lock apps\companion\src-tauri\tauri.conf.json mods\bepinex\src\Plugin\MystiaStewardCompanionPlugin.cs
git commit -m "chore(release): bump version to 1.1.0-preview.1"
git push origin dev

git tag -a v1.1.0-preview.1 -m "v1.1.0-preview.1"
git push origin v1.1.0-preview.1
```

然后在 Windows 发布机上发布 GitHub Prerelease：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0-preview.1 `
  -Title "v1.1.0-preview.1" `
  -Notes "预览版更新测试说明" `
  -Prerelease
```

测试者需要在 BepInEx 配置中开启预发布检查：

```ini
[Updates]
IncludePrerelease = true
```

开启后，预览版会参与自动更新检查；默认配置下普通用户只检查稳定版。测试 `preview.1 -> preview.2` 时，重复以上步骤发布 `v1.1.0-preview.2`。测试通过后，再同步 `1.1.0`，按稳定版流程合并 `main` 并正式发布。

## Release Note 规则

发布说明只描述从上一个版本到当前版本的用户可见变化：

- 新增功能。
- 体验或性能优化。
- BUG 修复。

不要写内部重构、文档、构建脚本、版本号变更或 Git 流程调整。如果某个优化或 BUG 修复只是本版本新增功能带来的二次调整，不单独列入 Note，只在新增功能描述中体现最终交付能力。

整理 Note 前先查看上一版本 tag 到当前分支的提交记录，例如：

```powershell
git log --oneline v1.0.2..HEAD
```

## 一键构建并发布

从仓库根目录执行：

```powershell
git checkout main
git pull --ff-only origin main

pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -Title "v1.1.0" `
  -Notes "版本更新说明"
```

发布构建默认管理仓库内可再生缓存：超过 12 GiB 时以完整 Cargo profile/target triple、Gradle build 或 .NET `bin/obj` 为单位清理到 8 GiB，Android 和 .NET 分类上限分别为 1.5 GiB 和 0.5 GiB。发布资产 `mods/bepinex/dist`、本机引用和签名材料不计入配额。可在构建前查看实际占用：

```powershell
pnpm artifacts:report
pnpm artifacts:prune -- --dry-run
```

需要调整发布机预算时传入 `-BuildCacheLimitGiB` 和 `-BuildCacheTargetGiB`；`-SkipBuildCacheCleanup` 只用于保留中间目录排查构建问题，不应作为常规发布参数。

配额按文件逻辑大小统计，因此可能高于磁盘工具显示的实际分配块大小。手动运行 `prune`/`clean` 前必须退出 Cargo、Gradle、Vite 和 dotnet 构建进程；清理器只互斥其他清理进程，不会终止或接管正在运行的构建。

如果引用 DLL 不在 `mods\bepinex\References`，传入同一个目录：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -Title "v1.1.0" `
  -Notes "版本更新说明" `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

脚本会先运行 `build-release.ps1`。正常构建通过 staging 完整重建 `mods/bepinex/dist`，旧 APK、manifest、tar、zip 和旧目录不会进入本次资产；随后脚本生成本次 update manifest，并上传 Mod 压缩包、自动更新清单和供其他设备直接使用的独立伴随窗口 EXE：

- `mods/bepinex/dist/mystia-steward-companion-bepinex.zip`
- `mods/bepinex/dist/update-manifest.json`
- `mods/bepinex/dist/mystia-steward-companion-companion-windows-x64.exe`
- 可选：`mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk`
- 可选：`mods/bepinex/dist/mystia-steward-companion-android-armeabi-v7a.apk`

`update-manifest.json` 包含版本号、资产文件名、zip 大小和 SHA256，不包含本机打包路径，并且只指向 `mystia-steward-companion-bepinex.zip`。独立 Windows 伴随窗口 EXE 和 Android APK 只给 B 设备跨局域网连接使用，不参与 Mod 自动更新。Tauri setup 安装器不会上传到 Release，避免和 Mod 分发包混淆。

如发布机已配置 Android 工具链和签名配置，可在发布构建时直接生成 Android APK：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -BuildAndroidApk
```

正式发布命令也可以透传该参数：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -Title "v1.1.0" `
  -Notes "版本更新说明" `
  -BuildAndroidApk
```

没有 `-BuildAndroidApk` 时，Windows 发布流程继续只构建 Mod 主包、更新清单和 Windows 独立伴随窗口 EXE，不强制依赖 Android SDK/NDK/JDK 或 keystore，也不会启用 Android APK 专用的 Rust LTO 体积优化。

`-BuildAndroidApk` 不能与 `-SkipBuild` 或 `-AndroidApkPath` 同时使用：前者要求本次实际构建，后者表示使用调用者提供的外部 APK，资产来源必须唯一。覆盖已有 Release 时，脚本会对账两个 canonical Android APK；本次不再发布的旧 APK 只有在显式使用 `-Clobber` 时才会于基础资产上传成功后删除，否则发布会停止并列出差异。

Android APK 也可以在具备 Android 工具链的机器上单独构建。仓库已包含 `apps/companion/src-tauri/gen/android/` 工程；签名配置、keystore、Gradle 缓存和 build 输出不能提交：

```powershell
pnpm tauri:android:apk
```

该命令默认会生成按 ABI 拆分的未签名验证包，例如：

```text
apps\companion\src-tauri\gen\android\app\build\outputs\apk\arm64\release\app-arm64-release-unsigned.apk
apps\companion\src-tauri\gen\android\app\build\outputs\apk\arm\release\app-arm-release-unsigned.apk
```

正式发布必须使用已签名 APK。先准备本机私有 keystore：

```powershell
keytool -genkeypair -v `
  -keystore "$env:USERPROFILE\.android\mystia-steward-companion-release.jks" `
  -storetype PKCS12 `
  -keyalg RSA `
  -keysize 2048 `
  -validity 10000 `
  -alias mystia-steward-companion
```

然后创建 `apps\companion\src-tauri\gen\android\keystore.properties`。该文件已被 Git 忽略，不能提交：

```properties
keyAlias=mystia-steward-companion
password=<keystore 和 key 共用密码>
storeFile=C:\\Users\\Administrator\\.android\\mystia-steward-companion-release.jks
```

如果 keystore 密码和 key 密码不同，使用：

```properties
keyAlias=mystia-steward-companion
storePassword=<keystore 密码>
keyPassword=<key 密码>
storeFile=C:\\Users\\Administrator\\.android\\mystia-steward-companion-release.jks
```

构建、验签并原子复制发布资产：

```powershell
pnpm tauri:android:apk:signed
```

签名 APK 脚本会在 Android 构建进程内注入 `CARGO_PROFILE_RELEASE_STRIP=symbols`、`CARGO_PROFILE_RELEASE_LTO=thin` 和 `CARGO_PROFILE_RELEASE_CODEGEN_UNITS=1`，用于降低 APK 体积。该优化不会写入全局 Cargo release profile，避免普通 Windows 发布构建在 Rust 链接优化阶段耗时过长。

只有全部目标 APK 均构建并验签成功后才会替换 `dist` 中的 Android 资产；随后可按空间配额清理 Android Gradle/Cargo 中间产物。成功后会生成：

```text
mods\bepinex\dist\mystia-steward-companion-android-arm64-v8a.apk
mods\bepinex\dist\mystia-steward-companion-android-armeabi-v7a.apk
```

构建和验签只能证明 APK 产物有效，不能证明 LAN 功能可用。正式上传 Android 资产前必须使用真机完成以下检查：

1. A 设备启动游戏和 Mod，开启 LAN listener，确认设置页列出的推荐地址与 B 设备处于同一局域网网段。
2. Android 浏览器通过 `GET http://A设备局域网地址:32145/health` 看到 JSON，先排除错误地址、Windows 防火墙和 AP/client isolation。
3. 安装本次签名 APK，使用同一地址和正确 Token 执行 `GET /snapshot`、`GET /runtime-data`，再用错误 Token 确认显示未授权。不得用 `/` 或 `/api/*` 代替规范路径。
4. 在 A 设备连续关闭、开启和重新应用 LAN 配置，确认回环连接不受影响，worker 线程不累积，`LogOutput.log` 不出现重复 `Local API LAN accept failed`。
5. Android 断开并恢复 Wi-Fi、切到后台再返回，确认重连和自动化 lease 状态正确。

没有真机时只能记录“构建、签名和包元数据通过，LAN 未实机验证”，不能把 APK 标记为已完成 LAN 发布验证。

`publish-release.ps1` 会自动把 `mods\bepinex\dist` 下的 Android APK 作为额外 Release 资产上传。若 APK 位于其他路径，发布时可显式传入单个 APK 或包含 APK 的目录：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -Title "v1.1.0" `
  -Notes "版本更新说明" `
  -AndroidApkPath "D:\path\android-apks"
```

没有 APK 时，Windows 发布流程继续只上传 Mod 主包、更新清单和 Windows 独立伴随窗口 EXE。

Windows 下如果 Android 构建出现 `this and base files have different roots: C:\... and D:\...`，这是 Kotlin 增量编译缓存跨盘符相对路径问题。仓库已在 Android Gradle 配置中关闭 Kotlin incremental compilation；如果本机仍使用旧 daemon 或旧缓存，先清理：

```powershell
cd apps\companion\src-tauri\gen\android
.\gradlew --stop
Remove-Item -Recurse -Force .gradle, build, app\build, buildSrc\build -ErrorAction SilentlyContinue
```

## 只上传已有产物

如果已经构建过，只重新上传：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -SkipBuild `
  -Clobber
```

`-Clobber` 会覆盖同名 Release 资产。

`-SkipBuild` 会显式复用当前 `dist`，不会重建目录或自动移除已有 APK。执行前必须用脚本输出的资产清单确认这些文件确实属于目标 tag；常规发布不要依赖上一次构建残留。

正常打包若检测到 `mods\bepinex\dist.staging-*` 或 `dist.backup-*`，会停止而不会生成新的事务目录。先检查 canonical `dist` 是否完整；确认后再恢复唯一有效备份或删除已无用的 staging/backup，避免失败事务继续累积。

## 注意事项

- 不要直接推送 tag 期待 GitHub 自动构建；仓库没有 Release 构建 workflow。
- 构建引用 DLL 只留在本机 `References/`，不要提交。
- 发布前运行 `set-version.ps1` 并提交版本号变更；发布脚本会自动校验版本一致性。
