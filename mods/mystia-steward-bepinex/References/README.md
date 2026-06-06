# 本地构建引用

该目录在源码中保持为空，不提交真实 DLL。构建前只需要从已安装 BepInEx Unity IL2CPP 的《东方夜雀食堂》游戏目录复制以下基础引用：

- `BepInEx.Core.dll`
- `BepInEx.Unity.IL2CPP.dll`
- `0Harmony.dll`
- `Il2CppInterop.Runtime.dll`
- `Il2Cppmscorlib.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.IMGUIModule.dll`
- `UnityEngine.InputLegacyModule.dll`

不需要复制 `Assembly-CSharp.dll`。Mod 对游戏运行时状态的读取使用反射，类型和字段名来自导出的 `/tmp/Assembly-CSharp` 项目作为开发参考，不作为编译引用。

常见来源：

- `游戏根目录/BepInEx/core/`
- `游戏根目录/BepInEx/interop/`

如果 `BepInEx/interop/` 不存在，先启动游戏一次。BepInEx 首次启动 IL2CPP 游戏时会生成 interop 程序集。

复制引用后，在仓库根目录运行：

```bash
powershell -ExecutionPolicy Bypass -File mods\mystia-steward-bepinex\tools\preflight.ps1
dotnet build mods/mystia-steward-bepinex/MystiaSteward.BepInEx.csproj -c Release
```

构建环境建议使用 .NET 6 SDK 或更新版本，项目目标框架为 `net6.0`。
