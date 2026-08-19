# 本地构建引用

真实 DLL 不提交到源码仓库。所有正式构建必须使用
[`references.lock.json`](./references.lock.json) 锁定的同一组引用，不能从当前游戏目录、另一版
BepInEx 或旧 interop 目录中临时拼接。锁文件同时记录了以下身份：

- BepInEx #783（`6.0.0-be.783+c58c42d`）及上游压缩包 SHA-256；
- Steam App `1584090`、Build `23158340`；
- 对应 `GameAssembly.dll` 与 `global-metadata.dat` 的 SHA-256；
- 私有 Release bundle 的仓库、tag、asset、字节数及 SHA-256；
- 下列 7 个 DLL 各自的精确字节数及 SHA-256。

正式引用只有：

- `BepInEx.Core.dll`
- `BepInEx.Unity.IL2CPP.dll`
- `0Harmony.dll`
- `Il2CppInterop.Runtime.dll`
- `Il2Cppmscorlib.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.InputLegacyModule.dll`

不需要游戏业务 DLL。Mod 对游戏运行时状态的读取使用反射，编译只依赖上方列出的
BepInEx、Il2CppInterop 和 Unity 基础引用。

## 恢复与校验

锁定 bundle 位于私有仓库 `blockshy/mystia-steward-build-assets`：

- tag：`bepinex-783-tmi-91ce5ae3-995d1a08-v2`
- asset：`mystia-steward-build-references.zip`

先使用有权访问该私有仓库的 GitHub 凭据下载 asset。下载是独立步骤；恢复脚本不会联网，
也不读取 base64 或其他 secret：

```powershell
New-Item -ItemType Directory -Force temp\build-references | Out-Null

gh release download bepinex-783-tmi-91ce5ae3-995d1a08-v2 `
  --repo blockshy/mystia-steward-build-assets `
  --pattern mystia-steward-build-references.zip `
  --dir temp\build-references

node scripts/restore-build-references.mjs `
  --archive temp\build-references\mystia-steward-build-references.zip `
  --output mods/bepinex/References
```

恢复前会先核对压缩包的精确大小和 SHA-256，再严格检查 ZIP 只有扁平路径的 7 个锁定
DLL。缺项、多项、路径穿越、symlink、大小或哈希漂移都会直接失败，不会尝试其他来源或版本。
目标目录可以保留分析或测试文件，但 7 个正式引用必须全部存在、不是 symlink，并与锁完全一致。

只校验现有引用时运行：

```bash
corepack pnpm references:verify
```

PowerShell 和 Bash preflight 都会执行同一项身份校验，不再只检查文件名是否存在。

## 测试专用引用

运行 `tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj` 的实际 Harmony wrapper 测试时，还需要从同一 BepInEx `core` 目录复制以下两个 HarmonyX 运行依赖；它们只用于该 smoke，不属于 Mod 编译或发布 preflight 的额外依赖：

- `MonoMod.RuntimeDetour.dll`
- `MonoMod.Utils.dll`

## 恢复后的验证

恢复引用后，在仓库根目录运行：

```bash
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\preflight.ps1
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

如需使用外部目录，先将锁定 bundle 恢复到该目录，再在构建或发布时传入；外部目录也执行
完全相同的身份校验：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"

dotnet run --project tests\ui-pinning-runtime\UiPinningRuntimeSmoke.csproj `
  -c Release -p:ReferenceDir="D:\path\to\mystia-steward-companion-references"
```

工具链安装、完整构建和缓存治理见[本地开发与构建](../../../docs/local-development.md)；测试专用依赖与
Harmony/MonoMod 容器入口见[验证指南](../../../docs/validation-guide.md)。本文件不重复维护通用工具版本或
发布流程。
