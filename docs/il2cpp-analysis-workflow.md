# IL2CPP 源码与 IDA 分析工作流

更新日期：2026-08-19

本文只负责分析资料生成、证据层级、锁定分析工具和已确认的失效路径；产品构建与测试命令见
[本地开发与构建](local-development.md)和[验证指南](validation-guide.md)。

## 目标与边界

当前 Steam 安装的《东方夜雀食堂》虽然运行在 Linux 主机上，主程序仍是 Windows x64 PE：
`Touhou Mystia Izakaya.exe`、`GameAssembly.dll` 和 `UnityPlayer.dll`。因此 Linux 版 BepInEx
不能加载该游戏；运行 Mod 时应使用 Windows x64 IL2CPP 包并通过 Proton 启动。

源码分析不需要启动游戏。仓库工具直接只读当前主游戏的 `GameAssembly.dll`、
`global-metadata.dat`、`globalgamemanagers` 和 `ScriptingAssemblies.json`，在外部分析目录生成：

1. Il2CppDumper metadata、DummyDll、`dump.cs`、IDA 映射和类型头文件；
2. 全部非空 metadata 程序集的 ILSpy C# 项目；
3. 按 BepInEx #783 精确流程离线生成的 Il2CppInterop 1.5.3 wrapper 及其 C#/CIL；
4. IDA 9 + Hex-Rays 的函数索引、方法映射、调用图、伪代码和失败函数反汇编；
5. 输入哈希、工具链版本、完整性计数和新旧对比报告。

生成过程不写 Steam 游戏目录，不启动 Unity，不读取或修改存档，也不依赖游戏目录中安装的 BepInEx。

## 当前目录布局

```text
/huyu/data/disk/mystia-steward-companion/
  backup/
    legacy-analysis-20260608/       # 原 Windows 分析，原样保留
    rejected-generated/             # 可恢复的无效/中止生成产物
  new/
    analysis-manifest.json
    comparison-report.md
    inputs/
      ScriptingAssemblies.json
      appmanifest_1584090.acf
    raw-metadata/
      DummyDll/
      dump.cs
      il2cpp.h
      script.json
    cpp2il/bepinex-783-dummy/
    interop-783/
      assemblies/
      unity-libs/
    managed-source/
      metadata/
      interop-783/
      interop-783-il/
    ida/
      database/GameAssembly.i64
      method_map.csv
      import_stats.json
      export/
    logs/
    reports/
      metadata-decompilation.json
      interop-decompilation.json
    toolchain/
```

`backup/` 仅用于历史核对，不向 `new/` 建立符号链接、复制别名或读取 fallback。游戏更新时在空的
候选目录重新生成，验证后将旧 `new/` 移入 `backup/`，再把候选目录原子改名为 `new/`。

## 锁定工具链

精确版本和下载 SHA-256 在
`mods/bepinex/tools/il2cpp-analysis/toolchain.lock.json`：

- Il2CppDumper 6.7.46；
- Cpp2IL `2022.1.0-pre-release.21+58fc404a...`；
- ILSpyCmd 10.1.1.8388；
- BepInEx Unity IL2CPP Windows x64 #783（`6.0.0-be.783+c58c42d`）；
- 与 Unity 2021.3.28 匹配的 BepInEx Unity 基础库；
- IDA / Hex-Rays 9.0.0.240925。

脚本会在解压或运行前验证下载哈希。游戏 Unity 版本与锁定基础库不一致时直接失败，必须审查并更新
工具链锁，不能静默选择其他版本。

## 完整生成

完整分析使用根目录 `toolchain.lock.json` / `global.json` 精确锁定的 .NET SDK `10.0.110` 和 Python
3.10+；Mod 发布 DLL 仍目标
`net6.0`，两者不得混为同一个工程。先确认 IDA 已由当前用户完成首次许可确认，且 `idapyswitch`
已配置可用 Python 3。目标目录必须为空：

```bash
mods/bepinex/tools/il2cpp-analysis/generate-analysis.sh \
  '/home/blockshy/.local/share/Steam/steamapps/common/Touhou Mystia Izakaya' \
  '/huyu/data/disk/mystia-steward-companion/new-next'
```

脚本不会自动删除或覆盖既有分析。生成完成后先检查：

```bash
python3 -m json.tool /huyu/data/disk/mystia-steward-companion/new-next/analysis-manifest.json >/dev/null
sed -n '1,240p' /huyu/data/disk/mystia-steward-companion/new-next/comparison-report.md
```

只有输入哈希、关键 RVA、interop 形态、函数体覆盖率和日志都通过后，才把当前 `new/` 移入一个明确的
`backup/` 版本目录，并将 `new-next/` 改名为 `new/`。不要叠加两代输出，也不要在脚本中添加旧目录回退。

## 正确使用三层资料

metadata C# 用来确认 IL2CPP 声明：类型、命名空间、字段、属性、方法签名、RVA 和 token。它的方法体是
DummyDll 占位，不代表游戏实现。

BepInEx #783 interop C# 用来确认运行时反射实际看到的 wrapper。例如当前生成结果确认：

- `DEYU.Singletons.Singleton<T>.Instance` 是静态属性；
- `DataBaseDay.allNPCs` 和 `RunTimeAlbum.RecordedSpecialNPCs` 是具体泛型字典的静态属性；
- `NPC.possibleDestinations` 是 `Il2CppReferenceArray<Destination>`；
- `SchedulerNode.Character.characterIdentity` 是公开字段。

IDA 用来确认 Native 控制流、调用关系、副作用和异常顺序。`functions.csv` 的 `body_path` 是唯一入口：
Hex-Rays 成功时指向分片 `pseudocode/`，失败时指向 `disassembly/`。不能把伪代码局部变量名反推为
interop 成员形态。函数索引的 `size` 是全部 IDA chunks 的总大小，`chunk_count` 明确记录非连续 tail
chunks；反汇编 fallback 也逐 chunk 输出边界，不能只导出主区间。如果 IDA 的某条指令文本含无效
Unicode，该行保留地址和原始机器码并明确标注，降级行数写入 `export_stats.json`；不得因文本解码失败
丢弃整个函数体。

涉及游戏运行时行为的实现结论必须按 metadata → #783 interop → IDA → 实机日志的顺序闭环。

## 已确认的无效路径

Cpp2IL 当前锁定版本虽然暴露 `dll_il_recovery` 名称，但该输出器源码实际只给托管方法写入
`ldnull; throw`。它不会恢复游戏方法体，生成速度快也不是恢复成功。流程只使用 BepInEx #783 本身使用的
`AttributeInjectorProcessingLayer + AsmResolverDllOutputFormatDefault` 来产生 interop 输入，不发布
`dll_il_recovery` 产物。

ILSpy 10.1.1 对少数 Il2CppInterop 复杂泛型程序集无法生成项目 C#，会抛出泛型参数索引异常。流程明确
记录失败程序集，并为这些程序集导出精确 CIL；局部项目输出放入 `_partial/`，不得当作完整 C# 使用。

旧 `ida_ai.zip` 包含 153,634 个伪代码文件，旧展开目录只有 143,151 个，缺口集中在地址尾段，属于旧
解压未完成。旧 ZIP 的地址+哈希命名没有发生覆盖。新流程按 `(RVA >> 4) & 0xff` 使用 256 个均匀分片，
并用统计文件数和失败函数反汇编避免再次出现“统计成功但展开目录不完整”。

## 风险与验证

- 旧资料没有保存输入二进制哈希。关键 RVA 一致只能说明新旧版本高度相符，不能证明二进制完全相同。
- IDA 自动分析、65 MiB 类型头导入和全量 Hex-Rays 导出耗时较长；进程异常退出时保留日志和候选数据库，
  重新生成必须使用新的空目录。导出器每 1,000 个函数清理 Hex-Rays/Python 缓存，避免全量导出因缓存
  线性增长而耗尽内存；不得删除这一批次边界。导入完成后保存前和导出枚举前都必须等待 IDA
  auto-analysis 收敛，否则函数集合会受数据库打开次数影响。导出完成后再次等待并核对完整函数地址集合；
  即使总数相同，只要有地址增删也按整轮失败处理，不发布已落后于数据库的函数索引。
- `ScriptingAssemblies.json` 中的程序集数会大于 DummyDll 数。只有 metadata 中含实际类型的程序集才生成
  DummyDll，不能用空项目补齐数量。
- Il2CppInterop DLL 的文件哈希包含生成期身份，不应单凭 DLL SHA-256 判断 wrapper 语义变化；应比较明确
  类型/成员形态并重新构建 Mod 与专项 smoke。
- 分析结论进入代码后，按[验证指南](validation-guide.md)运行受影响的构建、smoke/audit 和游戏实测；
  分析生成成功本身不证明产品行为正确。

## Steam 游戏目录约束

分析脚本只读本页列出的四个游戏输入，不安装、修复或清理 Steam 目录中的 BepInEx。Linux 主机通过
Proton 实际运行 Mod 时使用锁定的 Windows x64 #783 包，并按
`WINEDLLOVERRIDES="winhttp=n,b" %command%` 加载；加载器维护与离线分析保持为两个独立流程。
