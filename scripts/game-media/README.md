# 离线游戏图片分析工具

本目录保留开发阶段的只读分析与精确映射核验工具。**产品不再打包本工具生成的 PNG 或 Dart 映射**；图片由 Mod 首次加载提取并缓存，当前产品实现见[运行时图像资源](../../docs/game-media.md)。以下导出结果只留在指定临时工作目录用于人工诊断。

本工具只读取合法安装的《东方夜雀食堂》资源副本，不启动游戏、不连接游戏 API、
不访问 Unity 主线程，不调用随机外观选择。生成的是展示图片与序列化配方引用，
不包含推荐算法。未知版本、缺少精确身份或引用冲突均应停止或保留通用图标。

当前验证来源为 Steam build `23158340`、Unity `2021.3.28f1`、Addressables `1.18.24`。
游戏图片版权归游戏及相应作者；本项目代码许可证不授予这些图片的再分发许可。
本地提取与验证不等于已获公开发布图片的授权。

## 依赖与隔离

- PowerShell 7、Python 3.10 及以上；缩图步骤需要已有 Pillow。
- 已有 AssetStudio `0.16.47`。两个核心 DLL 必须与脚本内固定 SHA256 相同。
- 游戏 Addressables（AA）目录由调用方明确传入；检查器拒绝工作目录位于 AA
  子树内。所有后续输入/输出受同一个显式工作目录约束并拒绝重解析祖先；调用方
  应将工作目录设为本项目 `temp` 下的独立目录，不使用游戏安装目录。
- 不自动下载工具，不把工具 DLL、原始 bundle、游戏字体纳入仓库或客户端。
- `AssetStudio.LoadFiles` 会预处理 split 文件，因此检查器先复制指定文件到新目录，
  校验摘要，再仅对副本调用。禁止直接将游戏目录传给 `LoadFolder/LoadFiles`。
- 每次工作输出使用新目录，失败证据不覆盖；不要删除旧输出后伪装成一次成功执行。

`Export-GameMedia.ps1` 的宿主为 PowerShell，AssetStudio 原生解码器默认却从宿主
EXE 的 `x64` 子目录加载。导出器仅在新临时目录中生成 `AssetStudio.PInvoke.dll`
副本，把唯一 `GetDirectedDllDirectory` 方法改为工具本身的 `x64` 目录；不修改任何
解码算法或软件安装目录。原/派生摘要、方法前后 IL 和失败异常链均保留在证据中。

## 执行顺序

在 PowerShell 中明确赋值 `$gameAa`、`$assetStudio`、`$python`、`$pillowPython`、
`$work` 为本机路径。不要把个人机器绝对路径写入纳管脚本。

```powershell
./scripts/game-media/Inspect-GameMedia.ps1 `
  -GameAddressablesDirectory $gameAa -AssetStudioDirectory $assetStudio `
  -PythonExecutable $python -WorkDirectory $work -NormalizeGameStream
```

首次只读 `catalog.bundle`。在生成报告的 `objects` 中找到 `TextAsset` 的
`extractedJsonPath`，将其作为 `$catalogJson`，然后执行：

```powershell
& $python -B ./scripts/game-media/decode_addressables_catalog.py $catalogJson `
  --work-directory $work --output "$work/catalog-decoded.json"
```

`locations` 提供类型、主键、资源路径、精确依赖 location 索引。下一轮检查只传
这些引用所列的 `StandaloneWindows64/<hash>.bundle` 相对路径，示例：

```powershell
./scripts/game-media/Inspect-GameMedia.ps1 `
  -GameAddressablesDirectory $gameAa -AssetStudioDirectory $assetStudio `
  -PythonExecutable $python -WorkDirectory $work -NormalizeGameStream `
  -InputRelativeFiles $exactBundleRelativePaths
```

当前提取根为 `Core_ItemSprites`、`DLC1_ItemSprites` 至 `DLC5_ItemSprites`、
`DLCMusic_ItemSprites` 的 SpriteAtlas 显式闭包，以及 `RecipeProfile` / DLC1–5
RecipeProfile 的直接 bundle。不能用显示名称关联实体；后者用于读取基础食材与
厨具引用，无需加载它们无关的图像、音乐或场景依赖。

把各次结构检查的报告路径放入 `$reports`，其中所有 normalized 副本必须仍在
同一工作目录中且摘要不变：

```powershell
./scripts/game-media/Export-GameMedia.ps1 -ReportPaths $reports `
  -AssetStudioDirectory $assetStudio -WorkDirectory $work

& $pillowPython -B ./scripts/game-media/prepare_flutter_media.py `
  --export $spriteExportDirectory --profile-report $profileReport `
  --catalog-report $catalogReport `
  --work-directory $work --output "$work/flutter-media-ready"
```

`--export` 和 `--profile-report` 可重复传入。最后一步生成透明 PNG（最长边128，
小图不放大）、manifest、独立来源证据与 `game_media_catalog.g.dart`。这些文件只用于
检查数量、缺图及来源/输出 SHA256，保留在临时目录，不复制到客户端资产或源码目录。

客人立绘分析按 Profile 的精确 PPtr 补齐 `CharacterProtrayalSet` 与关联图集依赖；
此路径得到的是立绘及其表情，**不是当前产品使用的图鉴像素头像**。分析工具
使用检查器报告中的 `serializedFiles` 外部表解释 fileID。目录解析器同时保留 GUID
别名，避免把主键显示名称当作身份。结构报告与包含 GUID 别名的目录准备好后：

```powershell
& $python -B ./scripts/game-media/resolve_guest_bindings.py `
  --report $profileReport --report $guestReferenceReport --catalog $decodedCatalog `
  --work-directory $work --output "$work/guest-bindings.json"
```

导出器增加 `-GuestBindingsPath "$work/guest-bindings.json"`，缩图器增加
`--guest-bindings "$work/guest-bindings.json"`，即可把精确身份链和缺图原因写入
最终来源证据。需要读取多个报告时重复 `--report`。早期临时报告未包含外部表，
`--legacy-external-table` 仅用于附带同源独立检查表；新导出应直接使用完整报告。

最小拒绝分支回归只使用合成 JSON：`python -B scripts/game-media/test_pipeline.py`，
覆盖精确 GUID、同 ID 冲突硬失败、未知引用退回缺图及工作目录越界拒绝。

## 身份规则

- 物品严格采用游戏 ItemSprites 图集中 `Foods_<id>`、`Beverages_<id>`、
  `Ingredients_<id>`（含对应 DLC 前缀）的当前原生解析规则；负数占位图不导出。
- 标准厨具只取 `DefaultCookers.png` 容器中精确 enum 名的五个 Sprite，
  不把升级厨具的资源 ID 当成类型。
- 配方同时保留 `foodID` 和 `id`（recipeId），键为 `foodId:recipeId`。
  食材沿序列化顺序与重复用量保存；多义映射拒绝，不按名称或单 foodId 猜测。
- 客人图片必须继续沿 Profile → PPtr 的 fileID/PathID →实际图像引用绑定。
  不可用全局 PathID、中文名称或目录位置猜测。普客代表外观不等于当场随机皮肤。
- `manifest.version=1`；允许命名空间 `food/beverage/ingredient/cooker/rare-guest/normal-group`。
  缺失项由客户端退回既有图标，不能将游戏 fallback 图片冒充已确认的客人头像。

更新游戏版本后应重新核验 metadata、原生加载路径和 Addressables 格式，再重新
生成；旧图片目录不能作为新版本已经匹配的证据。
