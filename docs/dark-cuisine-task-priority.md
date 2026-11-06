# 黑暗料理任务与供给

更新日期：2026-11-06

本专题负责黑暗料理任务从读取、推荐到自动化供给的契约。普通任务来源见[任务系统](missions.md)，标准候选排序见[推荐引擎](recommendation-engine.md)，订单事务见[自动化运行时](automation-runtime.md)。

## 用户行为

启用“任务料理优先”后，当前稀客的有效任务要求黑暗料理时，任务列表仍能正常读取。经营页面会为该任务显示黑暗料理方案，不会把其他普通料理作为任务的替代品。

- 保温箱有可确认且未被其他烹饪事务占用的黑暗料理时，优先使用现成成品；显示供给来源，不虚构配方或厨具。
- 没有成品时，选择当前可制作的真实基底料理，以及能触发其禁忌的一项加料。仍使用正常扣料、开锅、出锅和送达流程。
- 没有合法材料或厨具、存在料理保护或成品替换效果、任务身份尚未确认时，只等待该任务订单；其他订单继续正常计算和调度。
- 保温箱成品被玩家或其他订单先取走后，原库存方案等待下一份推荐；有合法生产方式时可转为新制作。

这不是给黑暗料理新增一份游戏配方，也不改变游戏任务的接取、推进或完成规则。自动化仍需启用对应步骤并取得当前设备控制许可；仅使用收藏、预算及订单其他限制继续有效。与原客户端一致，任务优先不会绕过“仅用收藏料理”；黑暗料理没有可收藏配方，启用该限制时会明确提示关闭此限制或手动完成该任务订单。

## 身份与目录

`ServeInWork` 条件的 `amount` 是料理 ID，合法 `Food -1` 表示黑暗料理。读取必须先确认精确条件枚举、字段存在且为 `Int32`，再允许这个特殊值；小于 `-1` 仍拒绝。其他地方缺省或缺失的 `-1` 不能因此变成合法黑暗料理。

普通料理仍使用唯一的 `foodId + recipeId`。特殊任务额外要求 `productKind: dark-cuisine`、`foodId: -1` 和 `recipeId: null`，且任务、经营代际、canonical/raw 稀客、订单 trace 和生命周期精确一致。任务不再有效时清除信号。

静态目录以 `specialProducts` 单独发布从 `RefFood(-1)` 读取的名字、标签、等级和价格，不插入普通 `recipes`，也不制造 `RecipeId=-1`。特殊项读取失败不使普通目录失效。宿主目录标准化显式保留这份独立定义，候选缓存键包含特殊目录和保温箱数量，能力或供给变化不会复用旧方案。

候选的 `food.recipe` 是统一显示组件使用的成品投影；`food.productionRecipe` 才是生产时使用的真实基底。两者身份不能互换：

| 供给方式 | 成品 | 生产配方 | 材料与厨具 |
| --- | --- | --- | --- |
| `supplySource: stored` | 黑暗料理 | `null` | 不新增占用，取餐时确认成品实例 |
| `supplySource: cook` | 黑暗料理 | 真实普通配方 | 原配方材料及一项禁忌加料、匹配厨具 |

存量只在存在黑暗料理任务时于原有主线程快照中有界读取；快照仅传递数量，不跨线程保存 IL2CPP wrapper。未知数量与零库存不同。每次取餐仍必须重新确认真实实例，数量快照不构成写入许可。

## 原生制作依据

当前 metadata、BepInEx interop 和 Native 控制流共同确认：

- `CookController.FinishCooking`（RVA `0x525340`）在 QTE 失败，或不存在 `Medicine_Positive = 110` 且加料触发禁忌时，调用原生黑暗料理转换。实现选择真实配方加禁忌加料，不主动制造 QTE 失败。
- `CheckFailCook` 谓词（`0x53B0F0`、`0x53B1D0`）读取加料 `RefIngredient(id).Tags`，判断是否命中初始料理 `banTags`。
- 转换后的 `AsNewFood(-1)` 不保留原配方与加料标签，因此推荐只用特殊成品标签评价最终料理，实际材料仍完整计入库存和成本。
- `EventManager.GetOverrideFood`（`0x4F0370`）可能使用随机数，不能调用它进行只读预测。自动开锅要求 `registeredOverrideFoodBuff` 可完整读取且为空。

执行前重复确认基底、初始成品、加料数组、禁忌交集及保护状态。保护状态不可读或替换集合不明确时不扣料。完成后以真实 Sellable 类型和 ID 检验结果：目标明确为黑暗料理时，`Food -1` 走正常送达；普通目标误制黑暗料理仍走既有入箱与重试流程。

## 保温箱事务

`IzakayaConfigure.RemoveStoredFood`（RVA `0x605120`）先移除 Sellable，再执行伙伴和其他回调，因此“抛出异常”不代表“没有移除”。

取餐只在同一 Unity 主线程调用中执行：完整读取容器并挑选唯一原生实例，排除仍被活跃烹饪事务持有的成品；确认订单未送餐、未在送达中，检查任务和精确订单绑定，然后调用原生移除一次。回调返回后先检查生命周期，再重新取得同一订单，最后进入既有送达事务。

明确未送达且原会话仍有效时，允许把同一成品回存一次并验证。移除结果、送达结果或回存结果不确定时，登记该订单的安全屏障，停止重复取餐、扣料和送达。不能把“列表中已经没有该对象”当成回调完整成功的证明。库存方案不占用虚构厨具；如果已有烹饪事务，则继续原事务，不另外取一份成品。

## 验证入口与范围

```powershell
# 从当前分支根运行；沿用项目锁定工具入口。
dotnet run --project tests/runtime-serve-in-work-diagnostic/RuntimeServeInWorkMissionDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-recipe-priority/RuntimeMissionRecipePrioritySmoke.csproj -c Release
dotnet run --project tests/dark-cuisine-task-priority/DarkCuisineTaskPriority.csproj -c Release
dotnet run --project tests/runtime-dark-cuisine/RuntimeDarkCuisineSmoke.csproj -c Release
dotnet run --project tests/csharp-automation/CSharpAutomationSmoke.csproj -c Release
```

任务信号/投影测试覆盖明确 `-1`、非法负数、缺省值、任务代际和唯一订单；推荐测试覆盖实际目录标准化、特殊目录与库存的缓存失效、真实基底、重复材料成本与数量、禁忌加料、存量优先、不可执行目标隔离。自动化测试覆盖库存供给不占厨具、库存失效后的重新规划和不确定提交禁止重复操作。

`runtime-dark-cuisine` 直接编译生产 partial，使用明确标注的托管原生边界替身，验证保护、替换效果、任务变化、移除回调异常、送达不确定、回存失败、生命周期结束及订单漂移。它不验证实际 Unity 反射形态、原生图像显示、游戏任务闭环或真实扣料；这些仍需独立真实游戏验证。各次验证记录应明确离线、构建与实机范围，不能将上述隔离测试记作实机通过。
