# GitHub Actions 构建发布方案

## 工作流

- `.github/workflows/ci.yml`：手动触发的前端检查，只运行 `pnpm lint` 和 `pnpm build`。
- `.github/workflows/release.yml`：版本发布工作流，仅在推送 `v*` tag 或手动 `workflow_dispatch` 时运行。

普通 `main` 分支提交不会触发发布构建。需要发布时，再按版本号创建 tag 或手动运行 Release workflow。

## Runner 要求

发布构建使用 self-hosted Windows runner：

```text
self-hosted, Windows, mystia-steward-companion
```

原因是 Mod 编译需要 BepInEx、Il2CppInterop 和 Unity interop DLL；这些 DLL 不提交到仓库，也不应上传到公开 GitHub runner。

Runner 需要预装：

- Node.js 22，启用 Corepack。
- .NET 6 SDK。
- Rust stable。
- Microsoft C++ Build Tools 2022 或 Visual Studio C++ 桌面开发组件。
- Microsoft Edge WebView2 Runtime。

## Repository Variables

在 GitHub 仓库 `Settings -> Secrets and variables -> Actions -> Variables` 中配置：

```text
MYSTIA_REFERENCE_DIR=C:\path\to\mystia-steward-companion-references
```

该目录必须包含：

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

## 发布产物

Release workflow 会生成并上传：

- `mods/bepinex/dist/mystia-steward-companion-bepinex.zip`
- Tauri NSIS 安装器：`apps/companion/src-tauri/target/release/bundle/nsis/*.exe`
- `checksums.txt`

## 触发发布

推荐使用 tag：

```bash
git tag v1.0.0
git push origin v1.0.0
```

也可以在 GitHub Actions 页面手动运行 Release workflow，并填写 `tag` 输入。除非明确准备发布，不要触发 Release workflow。
