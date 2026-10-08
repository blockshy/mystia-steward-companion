# 本地构建引用

真实 DLL 不提交到源码仓库。所有正式构建必须使用
[`references.lock.json`](./references.lock.json) 锁定的同一组引用，不能从当前游戏目录、另一版
BepInEx 或旧 interop 目录中临时拼接。锁文件同时记录了以下标识：

- BepInEx #783（`6.0.0-be.783+c58c42d`）及上游压缩包 SHA-256；
- Steam App `1584090`、Build `23158340`；
- 对应 `GameAssembly.dll` 与 `global-metadata.dat` 的 SHA-256；
- 私有 Release 引用包的仓库、版本标签、产物名、字节数及 SHA-256；
- 下列 8 个 DLL 各自的精确字节数及 SHA-256；schema 2 将原私有包的 7 个文件与官方包中的原生挂钩引用分开绑定。

正式引用只有：

- `BepInEx.Core.dll`
- `BepInEx.Unity.IL2CPP.dll`
- `0Harmony.dll`
- `Il2CppInterop.Runtime.dll`
- `Il2Cppmscorlib.dll`
- `UnityEngine.CoreModule.dll`
- `UnityEngine.InputLegacyModule.dll`
- `MonoMod.RuntimeDetour.dll`

不需要游戏业务 DLL。Mod 对游戏运行时状态的读取使用反射，编译只依赖上方列出的
BepInEx、Il2CppInterop 和 Unity 基础引用。
原生退出挂钩通过 BepInEx #783 的 `INativeDetour` 使用其 MonoMod 接口；该 DLL 不随 Mod 重复发布，由锁定 BepInEx 提供。

## 恢复与校验

锁定引用包位于私有仓库 `blockshy/mystia-steward-build-assets`：

- 版本标签：`bepinex-783-tmi-91ce5ae3-995d1a08-v2`
- 产物：`mystia-steward-build-references.zip`

先使用有权访问该私有仓库的 GitHub 凭据下载产物。下载是独立步骤；恢复脚本不会联网，
也不读取 Base64 或其他密钥：

```powershell
New-Item -ItemType Directory -Force temp\build-references | Out-Null

gh release download bepinex-783-tmi-91ce5ae3-995d1a08-v2 `
  --repo blockshy/mystia-steward-build-assets `
  --pattern mystia-steward-build-references.zip `
  --dir temp\build-references

node scripts/download-bepinex-reference.mjs `
  --output temp\build-references\bepinex-783.zip

node scripts/restore-build-references.mjs `
  --archive temp\build-references\mystia-steward-build-references.zip `
  --bepinex-archive temp\build-references\bepinex-783.zip `
  --output mods/bepinex/References
```

以上 Node 命令使用锁定工具链。已有完整 #783 官方 ZIP 时可跳过官方下载，并将 `--bepinex-archive` 指向该文件。下载入口只接受锁文件的官方 URL、大小及 SHA-256；已有文件必须匹配，不能覆盖。官方来源见 [BepInEx 构建归档](https://builds.bepinex.dev/projects/bepinex_be)。

恢复前会核对两个压缩包的精确大小和 SHA-256：私有 ZIP 仍严格要求原 7 个扁平 DLL；官方 ZIP 只读取 `BepInEx/core/MonoMod.RuntimeDetour.dll`，核对目录/本地头、CRC、大小及 DLL 哈希，不展开其他文件。两包全部校验后才在同一恢复事务内替换 8 个引用。缺项、多项、符号链接、大小或哈希不一致直接失败，不尝试其他来源或版本。目标目录可保留分析或测试文件，但 8 个正式引用必须全部符合锁。

只校验现有引用时运行：

```bash
corepack pnpm references:verify
```

PowerShell 和 Bash 预检都会执行同一项标识校验，不再只检查文件名是否存在。

## 测试专用引用

运行 `tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj` 的实际 Harmony wrapper 测试时，除已锁定的 RuntimeDetour，还需要从同一 BepInEx `core` 目录复制下列 HarmonyX 运行依赖；它只用于该 smoke，不属于 Mod 编译或发布预检的额外依赖：

- `MonoMod.Utils.dll`

## 恢复后的验证

恢复引用后，在仓库根目录运行：

```bash
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\preflight.ps1
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

如需使用外部目录，先将锁定引用包恢复到该目录，再在构建或发布时传入；外部目录也执行
完全相同的标识校验：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

锁定 .NET 6 smoke 只挂载当前仓库；运行前将正式引用和上文测试专用依赖准备到
`mods/bepinex/References/`，然后使用 `corepack pnpm test:dotnet6 ui-pinning-runtime`。

工具链安装、完整构建和缓存治理见[本地开发与构建](../../../docs/local-development.md)；测试专用依赖与
Harmony/MonoMod 容器入口见[验证指南](../../../docs/validation-guide.md)。本文件不重复维护通用工具版本或
发布流程。
