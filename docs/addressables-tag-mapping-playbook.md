# Addressables 标签映射提取手册

更新日期：2026-04-30
适用范围：当存档中的料理或酒水 tag 使用数字 id，且仓库内映射需要重新核实时。

## 1. 目标

本手册用于从游戏本体资源中恢复权威的 tag id -> 名称映射，避免依赖上游仓库、手工猜测或标签显示顺序。

优先目标：

- 料理标签：`FoodTagProfile.asset` + `FoodTagsLang.txt`
- 酒水标签：`BeverageTagProfile.asset` + 对应语言文本
- 标签规则：`FoodTagRuleProfile.asset`

## 2. 权威数据源

优先级从高到低：

1. 游戏本体 Addressables 资源
2. 游戏本体 IL2CPP 元数据与字符串
3. 上游仓库与社区资料（仅作语义交叉验证）

当前仓库已确认：

- `catalog.bundle` 可直接读取，其中包含 Addressables catalog 的 JSON TextAsset。
- 实际内容 bundle 经过固定 `0x53` 异或处理，解码后可还原为标准 UnityFS 包。

## 3. 操作流程

### 3.1 定位 catalog

- 打开 `Touhou Mystia Izakaya_Data/StreamingAssets/aa/catalog.bundle`
- 读取其中的 TextAsset JSON

重点字段：

- `m_KeyDataString`
- `m_BucketDataString`
- `m_EntryDataString`
- `m_ExtraDataString`
- `m_InternalIds`
- `m_ProviderIds`

### 3.2 解析 key / bucket / entry

不要用“可打印字符串在字节流中的位置”去推测 key index，必须按 Unity Addressables 的真实格式解析。

已验证要点：

- `m_BucketDataString`：`bucketCount` + 每个 bucket 的 `keyDataOffset`、entry 数量、entry 索引数组。
- `m_KeyDataString`：`ObjectType + payload` 的对象序列，需按 `SerializationUtilities.ReadObjectFromByteArray` 读取。
- `m_EntryDataString`：`count + count * 7 * int32`。

每条 entry 的 7 个字段语义已核实为：

1. `internalId`
2. `providerIndex`
3. `dependencyKeyIndex`
4. `depHash`
5. `dataIndex`
6. `primaryKey`
7. `resourceType`

### 3.3 先找 key，再找 bundle

先按真实 key 精确定位：

- `FoodTagProfile`
- `BeverageTagProfile`
- `FoodTagRuleProfile`
- `FoodTagsLang`

再使用该 entry 的 `dependencyKeyIndex` 反查依赖 bundle 集合，不要直接在所有 bundle 中全文搜索。

### 3.4 解码内容 bundle

内容 bundle 当前使用固定 `0x53` 异或。

处理方式：

- 原始 bytes 逐字节执行 `byte ^ 0x53`
- 解码后的结果再交给 UnityPy 或其他 Unity 资源工具读取

若解码后的文件头为 `UnityFS`，说明处理正确。

### 3.5 提取标签顺序与显示名

对于料理标签：

- `FoodTagProfile.asset`
  - 读取 `indexes` 数组，得到真实 tag id 顺序
- 中文 `FoodTagsLang.txt`
  - 读取 `id\ttag` 表，得到中文显示名

将两者结合后，即可恢复权威 id -> tag 映射。

## 4. 已核实结论（料理标签）

当前仓库已验证以下关键事实：

- `FoodTagProfile.asset` 位于核心 bundle `core_07e01badce0c3466a71d003dd46efa15.bundle`
- 中文 `FoodTagsLang.txt` 位于核心 bundle `core_d588e1cad1b8b9b47f46af2be495e6c3.bundle`
- 上述两个 bundle 都需要先做 `0x53` 异或再读取

已确认示例：

- `7 = 清淡`
- `19 = 招牌`
- `-3 = 昂贵`
- `-2 = 实惠`
- `-1 = 大份`
- `-21 = 流行·厌恶`
- `-20 = 流行·喜爱`

## 5. 落地要求

当映射被重新核实后，必须同步更新：

- `apps/companion/src/data/food-tag-id-map.json` 或对应结构化数据
- `mods/bepinex/src/Save/` 中的运行时解析逻辑
- `docs/tmi-cooking-mechanics-knowledge-base.md`
- 相关 README 或运行时说明文档

## 6. 常见误区

- 不要把 key 字节流里的字符串出现顺序当成真正的 key index。
- 不要把 `m_EntryDataString` 的第 5 个字段误认为 `internalId` 索引。
- 不要在未核实映射前重新启用数字 tag 自动识别。
- 不要直接对全部 bundle 做盲目全文搜索，优先使用 catalog 反查依赖。

## 7. 建议输出格式

当需要把提取结果回填到仓库时，优先输出为结构化 JSON：

- key：数字 id（字符串形式）
- value：当前仓库使用的标准中文 tag 名称

若游戏文本与仓库内部命名不一致，应在解析层做别名归一化，不要在上游数据层硬改原始映射。
