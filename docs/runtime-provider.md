# 游戏数据提供器

更新日期：2026-09-02

本文档只说明 Mod 如何从当前游戏对象读取并发布推荐所需数据，包括读取来源、场景就绪、缓存、失败语义和诊断边界。订单标识与生命周期见[运行时订单生命周期](runtime-order-lifecycle.md)，自动化游戏写操作见[自动化运行时](automation-runtime.md)，HTTP 传输协议见[本地 API](local-api.md)。

## 总体原则

- 游戏当前内存是唯一业务来源；Provider 不读取 `.memory` 存档文件，也不解析固定存档路径。
- 静态目录、玩家动态状态和夜间经营状态分别读取、分别判定完整性；一类数据成功不能代替另一类数据。
- 反射入口必须精确匹配已验证的类型、成员、返回值和具体集合形态。成员缺失、类型错误、容量异常、对象标识有歧义或读取异常均按本轮不可用处理。
- 数据提供器只读游戏状态，不生成游戏记录、不刷新 NPC、不推进任务，也不写入游戏存档。
- 诊断来源不能进入业务数据；打开总日志只增加观测，不改变推荐或自动化输入。
- 只有需要完整枚举的已验证静态字典才使用匹配泛参的具体 `Enumerator` / `KeyValuePair`，并复核数量与缓冲区；
  运行时 tracking、scheduled 等字典只按已验证 key 精确查找。任何形态或元素错误都拒绝整组结果，不使用会
  静默跳项的通用对象枚举器。

## 静态游戏目录

`RuntimeStaticDataCatalog` 从以下五张 `DataBaseCore` 映射读取目录根：

- `IngredientsMapping`
- `BeveragesMapping`
- `FoodsMapping`
- `RecipesMapping`
- `IzakayasMapping`

映射只提供有界枚举根。每个 ID 仍须通过对应的精确 `RefIngredient`、`RefBeverage`、`RefFood`、`RefRecipe` 或 `RefIzakaya` 读取对象，并通过 `DataBaseLanguage` 的精确入口读取语言数据。

配方会先固定料理、材料和厨具引用。被映射配方直接引用、但没有进入 `FoodsMapping` 或 `IngredientsMapping` 的非负 ID，会进入有数量上限且记录来源的完整依赖集合；不得扫描全量数据库、写回共享映射或读取第三方 Mod 注册表补齐。任一依赖缺失、重复、对象标识不一致或超过容量会使整轮目录读取失败。

各 ID 必须保持游戏中的原始取值含义：

- 料理、食材、酒水和配方使用非负内容 ID；负数映射键在对应业务边界排除，且不得调用其 `Ref*`。
- 料理与酒水标签保留完整的有符号 ID。
- `IzakayasMapping` 保留有符号 ID；只有合法空标签占位可以跳过，非空条目的语言或场景数据读取失败会使整轮失败。

基础普客和稀客只从 `GetAllNormalGuests()`、`GetAllSpecialGuests()` 的精确引用数组读取声明字段。喜好、厌恶和酒水标签使用原始数组，不调用生成型或计算型属性。目录必须同时包含材料、酒水、料理、配方、普客、稀客和两类标签，才可标记为完整。

## 稀客标识目录

基础稀客与映射稀客的标识由 `RuntimeMappedGuestCatalog` 单独构建，不能因静态目录已完整就跳过：

- 基础标识来自 `GetAllSpecialGuests()` 的 `id` 与 `stringId`。
- 映射标识来自 `GetAllMappedGuests()` 的 `ID`、`StrID` 与 `SourceGuestID`。
- 每条映射都必须沿无环、无缺口的来源链最终解析到一个基础稀客。
- ID 或 StringId 重复、来源缺失、循环和歧义都会使标识数据不可用。
- 不按显示名称、语言文本、占位对象或手工别名补齐标识。

核心静态目录与标识目录都完整后，数据提供器才允许建立推荐状态。普通地图变化可复用完整静态目录；进入主菜单等非游戏场景清空存档运行状态后，标识目录必须独立重建。

## 玩家动态状态

`RuntimeReflectionRecommendationStateProvider` 只按完整静态目录中的 ID 调用游戏 getter：

- `RunTimeStorage.HaveRecipe(int)`：已解锁配方。
- `RunTimeStorage.GetIngredientCountById(int)`：食材数量。
- `RunTimeStorage.GetBeverageCountById(int)`：酒水数量。
- `RunTimePlayerData.GetLevel()`：玩家等级。
- `RunTimePlayerData.GetPopFoodTags(...)`：流行喜好与厌恶标签。
- `RunTimeDayScene.GetTrackedSwitch("Aya_FamousIzakaya", false)`：明星店状态，仅在日间状态允许读取时使用。

食材和酒水数量只有精确 `-1` 表示无限，`0` 不发布，低于 `-1` 视为读取失败。数据提供器不生成存储快照，也不枚举玩家存档内部容器。没有任何已解锁配方时不发布空推荐状态，而是等待后续刷新。

夜间经营才读取物理厨具快照。厨具快照只有 `complete` 与 `unavailable` 两种业务状态；任一条目形态、对象标识、坐标、锁定状态或能力读取失败都会使整轮不可用于自动化，不按已读子集推测容量。厨具的执行所有权不属于数据提供器，详见[自动化运行时](automation-runtime.md)。

## 场景就绪

日间运行状态不以固定等待秒数判断。`RuntimeSceneReadinessCapture` 使用经过验证的 Hook 和同一游戏管理器/动作标识建立就绪轮次：

1. `DaySceneSustainedPannel.OnPannelPostOpen` 只提供最终面板就绪信号。
2. 普通读档从 `DayScene.SceneManager.OnFirstEnterDaySceneFinish` 捕获同一管理器。
3. 外层 `RunTimeScheduler.OnEnterDayScene` 动作必须进入匹配的 `DefaultOnFinish`。
4. 随后的 `OnEnterDaySceneMap` 最终动作必须正常返回。
5. 手动经营返回只接受入口前已精确读取的 `NightSceneDirector.IsManualWorkSceneSession` 分支。

每次实际读取还必须确认同一管理器存活、地图标签有效，并且 `IsMapSwapping`、待处理日间事件、调度器执行、场景事件、已安排动作和全局切场状态均允许读取。任一成员不可确认时保持不可用。

夜间准备阶段以 `IzakayaConfigPannel.OnPanelOpen` 或 `GoToSpecific` 记录准备就绪，并要求 `WorkPrepScenePannelRoot` 下的目标面板仍激活。准备阶段可读取库存、解锁和流行标签，但当前日间地图与稀客邀请仍要求完整日间就绪。

夜间经营生命周期由五个精确 Hook 管理：

- `WorkSceneSustainedPannel.OnPannelPostOpen`
- `GuestsManager.CloseIzakayaDelayed`
- `GuestsManager.CloseIzakayaAndLeaveChallengeMode`
- `NightScene.SceneManager.ToResult`
- `NightScene.SceneManager.OnInstanceDestroyed`

五个 Hook 全部安装后才允许进入 `Active`。`TryCloseIzakaya` 只停止接客并等待清桌，不是经营关闭边界；在座顾客仍可服务。进入 `Closing` 后停止新的游戏对象访问并清理本场所有权，`Destroyed` 后才允许下一场生成新的单调经营轮次。

## 夜间经营读取边界

`NightBusinessReflectionProvider` 读取活动稀客、预算、经营地点和诊断样本。正式订单业务不由反射扫描创建，而只消费已就绪的运行时捕获：

- 稀客正式订单只来自 `SpecialOrderRuntimeCapture`。
- 普客捕获就绪后以 `NormalOrderRuntimeCapture` 作为业务和执行的唯一依据；`OrderController` 与 HUD 只能追加没有捕获绑定的不可执行可见行。
- 捕获未就绪时，普客可以显示可见行，但必须标记 `visibleFailClosed` 并禁止自动化；稀客业务保持不可用。
- HUD、服务面板、管理器集合和队列可用于活动客人与诊断，但不得建立订单控制器所有权，也不得过滤已经确认的捕获。
- 诊断中的稀客候选只接受实际 `SpecialGuest`，或带明确稀客 `StringId` / `SourceGuestID` 的对象；普通
  `GuestBase` 的数字 ID 不能单独证明它是对应稀客，避免与稀客 ID 重叠时生成错误候选。

具体创建、订单标识、退出与 ABA 隔离统一由[运行时订单生命周期](runtime-order-lifecycle.md)定义。

## 缓存与发布

静态目录成功后按对象标识缓存；未完成读取按约五秒间隔重试。玩家动态状态按配置的自动刷新节奏读取。普客订单有独立的短缓存，订单捕获 `ChangeVersion` 变化只刷新对应经营上下文，不触发完整静态目录重扫。

本地快照在 Unity 主线程生成，网络线程只返回缓存 JSON。快照发布有轻量节流；规范内容没有变化时复用已有 JSON，不因捕获时间或性能数字重新序列化。完整 `RuntimeDataCatalog` 单独发布，主快照只携带其完整性、来源、状态和固定 64 字符小写 SHA-256 签名。传输细节见 [本地 API](local-api.md)。

前端可以按签名缓存最近一次完整目录。临时读取失败不能伪装成新的完整目录，也不能永久保留未完整的占位状态。没有完整游戏目录时，推荐保持不可用，不能改用“全部料理、材料和酒水均可用”的假设。

## 失败与诊断

每轮失败应保留能定位阶段的状态，不用宽泛异常掩盖具体来源。主要诊断包括：

- `runtimeSceneReadiness`：场景就绪状态、经营轮次和当前阻断原因。
- `runtimeDataComplete`、`runtimeDataSource`、`runtimeDataStatus`：核心目录与标识目录组合状态。
- `runtime-static-data`、`runtime-tags`、`runtime-database`、`runtime-guests`、`runtime-izakayas`：总日志中的缓存目录证据。
- `night-business`：订单捕获状态、活动客人、可见样本和限制数量的解析失败记录。
- `performanceMs`：`refresh.runtime`、`runtimeData.serialize`、`runtime.cookerSnapshot` 等已发布耗时。

诊断写入失败不得影响正式读取；经营热路径只能复用已缓存的静态诊断，不得为写日志重新扫描数据库。

## 修改与验证

修改 Provider、集合读取或场景就绪时至少运行：

```bash
dotnet run --project tests/runtime-reflection/RuntimeReflectionSmoke.csproj -c Release
dotnet run --project tests/runtime-static-data-catalog/RuntimeStaticDataCatalogSmoke.csproj -c Release
dotnet run --project tests/runtime-cooker-snapshot/RuntimeCookerSnapshotSmoke.csproj -c Release
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
dotnet run --project tests/snapshot-signature/SnapshotSignatureSmoke.csproj -c Release
```

涉及游戏成员、Hook 或集合形态的变更还必须按 [IL2CPP / IDA 分析流程](il2cpp-analysis-workflow.md) 重新取得证据，不能用兼容读取代替验证。
