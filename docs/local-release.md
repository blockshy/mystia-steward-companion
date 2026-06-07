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

`mods/bepinex/References/` 需要包含：

```text
BepInEx.Core.dll
BepInEx.Unity.IL2CPP.dll
0Harmony.dll
Il2CppInterop.Runtime.dll
Il2Cppmscorlib.dll
UnityEngine.CoreModule.dll
UnityEngine.IMGUIModule.dll
UnityEngine.InputLegacyModule.dll
```

## 同步版本号

发布前先同步项目内版本号。脚本会同时修改 `package.json`、`tauri.conf.json`、`Cargo.toml`、`Cargo.lock` 和 Mod 的 `PluginVersion`：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\set-version.ps1 -Version 1.0.1
```

版本号同步后需要提交并推送，再执行发布：

```powershell
git add package.json apps\companion\src-tauri\Cargo.toml apps\companion\src-tauri\Cargo.lock apps\companion\src-tauri\tauri.conf.json mods\bepinex\src\Plugin\MystiaStewardCompanionPlugin.cs
git commit -m "chore(release): bump version to 1.0.1"
git push origin main
```

`publish-release.ps1` 会根据 `-Tag` 校验代码版本。如果代码仍是旧版本，脚本会失败并提示先运行 `set-version.ps1`。

## 一键构建并发布

从仓库根目录执行：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.1 `
  -Title "v1.0.1" `
  -Notes "版本更新说明"
```

如果引用 DLL 不在 `mods\bepinex\References`，传入同一个目录：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.1 `
  -Title "v1.0.1" `
  -Notes "版本更新说明" `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

脚本会先运行 `build-release.ps1`，然后只上传 Mod 压缩包：

- `mods/bepinex/dist/mystia-steward-companion-bepinex.zip`
- `mods/bepinex/dist/checksums.txt`

`checksums.txt` 只包含 zip 的 SHA256。Tauri setup 安装器不会上传到 Release，避免和 Mod 分发包混淆。

## 只上传已有产物

如果已经构建过，只重新上传：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.0 `
  -SkipBuild `
  -Clobber
```

`-Clobber` 会覆盖同名 Release 资产。

## 注意事项

- 不要直接推送 tag 期待 GitHub 自动构建；仓库没有 Release 构建 workflow。
- 构建引用 DLL 只留在本机 `References/`，不要提交。
- 发布前运行 `set-version.ps1` 并提交版本号变更；发布脚本会自动校验版本一致性。
