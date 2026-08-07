# 本地构建引用

该目录在源码中保持为空，不提交真实 DLL。构建前只需要从已验证的 BepInEx #783 core，以及实机或离线
生成的同版本 interop 中复制以下基础引用：

- `BepInEx.Core.dll`
- `BepInEx.Unity.IL2CPP.dll`
- `0Harmony.dll`
- `Il2CppInterop.Runtime.dll`
- `Il2Cppmscorlib.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.InputLegacyModule.dll`

不需要复制额外的游戏业务 DLL。Mod 对游戏运行时状态的读取使用反射，编译只依赖上方列出的 BepInEx、Il2CppInterop 和 Unity 基础引用。

运行 `tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj` 的实际 Harmony wrapper 测试时，还需要从同一 BepInEx `core` 目录复制以下两个 HarmonyX 运行依赖；它们只用于该 smoke，不属于 Mod 编译或发布 preflight 的额外依赖：

- `MonoMod.RuntimeDetour.dll`
- `MonoMod.Utils.dll`

常见来源：

- `游戏根目录/BepInEx/core/`
- `游戏根目录/BepInEx/interop/`
- Linux 离线分析环境的 `/huyu/data/disk/mystia-steward-companion/new/interop-783/assemblies/`

Windows 环境中如果 `BepInEx/interop/` 不存在，可先启动游戏一次。Linux 主机上的 Steam 游戏仍是
Windows x64 PE，不能用 Linux BepInEx 生成 interop；开发分析使用
[`generate-analysis.sh`](../tools/il2cpp-analysis/generate-analysis.sh) 离线复现 BepInEx #783 的生成路径，
无需启动游戏。完整说明见 [`docs/il2cpp-analysis-workflow.md`](../../../docs/il2cpp-analysis-workflow.md)。

复制引用后，在仓库根目录运行：

```bash
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\preflight.ps1
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

如果不想复制到 `mods\bepinex\References`，也可以把上述 DLL 放在同一个外部目录，并在构建或发布时传入：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"

dotnet run --project tests\ui-pinning-runtime\UiPinningRuntimeSmoke.csproj `
  -c Release -p:ReferenceDir="D:\path\to\mystia-steward-companion-references"
```

构建环境建议使用 .NET 6 SDK 或更新版本，项目目标框架为 `net6.0`。
