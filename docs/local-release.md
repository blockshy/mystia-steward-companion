# 本地构建与发布方案

## 发布方式

本项目不再使用 GitHub Actions 自动构建 Release。发布采用：

```text
本机 Windows 构建完整产物 -> GitHub CLI 上传 Release
```

原因是 Mod 编译依赖 BepInEx、Il2CppInterop 和 Unity interop DLL。这些 DLL 不提交到仓库，也不上传到 GitHub runner。

## 本机要求

发布机器需要是 Windows，并预装：

- Node.js 22，启用 Corepack。
- .NET 6 SDK 或更新版本。
- Rust stable。
- Microsoft C++ Build Tools 2022 或 Visual Studio “使用 C++ 的桌面开发”组件。
- Microsoft Edge WebView2 Runtime。
- PowerShell 7。
- GitHub CLI，并完成 `gh auth login`。

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

如果引用 DLL 不在 `mods\bepinex\References`，传入同一个目录：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.1.0 `
  -Title "v1.1.0" `
  -Notes "版本更新说明" `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

脚本会先运行 `build-release.ps1`，然后上传 Mod 压缩包、自动更新清单和供其他设备直接使用的独立伴随窗口 EXE：

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

没有 `-BuildAndroidApk` 时，Windows 发布流程继续只构建 Mod 主包、更新清单和 Windows 独立伴随窗口 EXE，不强制依赖 Android SDK/NDK/JDK 或 keystore。

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

构建、验签并复制发布资产：

```powershell
pnpm tauri:android:apk:signed
```

成功后会生成：

```text
mods\bepinex\dist\mystia-steward-companion-android-arm64-v8a.apk
mods\bepinex\dist\mystia-steward-companion-android-armeabi-v7a.apk
```

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

## 注意事项

- 不要直接推送 tag 期待 GitHub 自动构建；仓库没有 Release 构建 workflow。
- 构建引用 DLL 只留在本机 `References/`，不要提交。
- 发布前运行 `set-version.ps1` 并提交版本号变更；发布脚本会自动校验版本一致性。
