# 特殊经营场景规则与实现边界

更新日期：2026-07-30

本文档记录特殊经营的游戏原生规则、Mod 执行策略和已验证边界。三者必须分开表述，不得把 Mod 的保守约束写成游戏原生门槛。

## 证据与术语

结论按以下顺序交叉验证：

1. `Assembly-CSharp/` 中的类型、字段、属性和方法签名。
2. `01_functions_index.csv`、`06_call_xrefs.csv` 和 `pseudocode/` 中的 IL2CPP Native 执行路径。
3. 当前游戏版本的 interop DLL、Mod 反射结果和实机总日志。

文档中的评价名称使用游戏枚举语义：`ExGood`、`Good`、`Normal`、`Bad`、`Exbad`。角色使用游戏中文名称“古明地恋”；`Koishi` 只作为内部类型、字段或角色标识的一部分。不在文档、诊断或用户界面中使用社区昵称。

## 挑战名称

`RuntimeSpecialBusinessContextService` 读取 `NightSceneDirector.ChallengeMode`，再从 `NightSceneDirector.ChallengeType` 枚举成员的 `UnityEngine.InspectorNameAttribute` 取固定中文标签。这些标签在 `Assembly-CSharp/NightScene/NightSceneDirector.cs` 中可直接核对：

| `ChallengeType` | 游戏元数据标签 |
| --- | --- |
| `NotChallenge` | 不是挑战 |
| `Story_Basic` | 妖梦科目一 |
| `Story_Advanced` | 妖梦科目二 |
| `Story_Yuyuko` | 幽幽子挑战 |
| `Challenge_Yuyuko` | 幽幽子重修 |
| `AnyChallenge` | 任何挑战 |
| `Story_BloodPondHell` | 血池地狱 |
| `Story_WackyCookingCompetition` | 怪诞料理大赛挑战 |
| `Story_Seiga_TempleCuisineCompetition` | 青娥 料理挑战 |
| `Story_Futo_TempleCuisineCompetition` | 布都 料理挑战 |
| `Story_Tochiko_TempleCuisineCompetition` | 屠自古 料理挑战 |
| `Story_Ichirin_MusicCompetition` | 云居一轮 音游挑战 |
| `Story_Minamitu_MusicCompetition` | 村纱水蜜 音游挑战 |
| `Story_Toramaru_MusicCompetition` | 寅丸星 音游挑战 |
| `Story_Flandre` | 芙兰朵露的笼女游戏 |
| `RogueLike` | Rogue Like |
| `Story_Mizuchi` | 寻找瑞灵踪迹 |
| `Story_Mizuchi_1` / `_2` / `_3` | 月都试炼1 / 2 / 3 |

该标签不是当前语言下的多语言文本，不会随游戏语言切换。元数据读取失败时，Mod 只保留有效 `challengeType` 和原始 `ChallengeMode` 诊断，不回退到自建名称表。`DataBaseLanguage.GetMissionLanguage(challengeType)` 会为未知 key 返回合成对象，不能用来猜测挑战标题。

## 上下文与 HUD 目标

后端只发布已确认的目标来源：

- `IncomeControllerChallenge.SetTargetFund` / `UpdateSpellCount`：妖梦科目一、科目二的目标营业额与符卡计数。
- `IncomeControllerYuuma.SetTargetTag` / `SetContext` / `SetTargetProgress` / `SetAngerProgress` / `SetTargetTime` / `SetTargetProgressImmediate`：血池地狱的双料理 Tag、进度、怒气和时间状态。
- `IncomeControllerKoishi.SetContext` / `SetTargetProgress` / `SetTargetTag` / `SetTargetTagTime*`：怪诞料理大赛的阶段、进度、单料理 Tag 和刷新倒计时比例。
- `IncomeControllerYuyuko.SetContext` / `SetTargetProgress` / `SetTargetTime`：幽幽子挑战的阶段、进度和目标时间。
- `IncomeControllerMausoleumCuisineCompetition.SetTargetFund`：青娥、布都、屠自古料理挑战的目标营业额。

捕获值只在当前 `ChallengeMode` 与来源匹配时展示，防止上一个场景的目标残留。特殊目标不写入 `RecommendationState`；目标营业额、符卡计数和未确认结算规则只做展示。

## 怪诞料理大赛挑战

### 游戏原生规则

相关数据类型为 `GameData.Profile.DLC2_KoishiBossData`。当前反编译与 Native 伪代码可确认：

- 第一阶段评价回调按普客料理和酒水的喜好 Tag 总命中数改判：`0 -> Exbad`、`1 -> Bad`、`2 -> Normal`、`>= 3 -> Good`；达到 `Good` 时增加挑战分。对应 Native 路径为 `GroupOverrideEvaluationCallback` 伪代码 `18075C480` 附近。
- 第二阶段回调只在原始评价为 `ExGood` 时继续，再检查已送达料理是否含当前轮换目标 Tag，并按运行时数据中的 `satisfy1TagScore` / `notSatisfyTagScore` 计分。对应伪代码为 `18075CA10`。
- 第三阶段分身回调接受传入的 `Normal`、`Good` 或 `ExGood`。命中当前目标 Tag 时改判为 `ExGood` 并恢复对应桌位/订单状态；未命中时改判为 `Normal` 并调整耐心或订单状态。对应伪代码为 `18075CBD0`。因此，“第三阶段分身原生要求 ExGood”是错误结论。
- 古明地恋本体的数据包含护盾、揭示的正面料理 Tag、厌恶料理 Tag、酒水 Tag、投食目标分和等级到效果的运行时数组。具体数值由当前游戏数据资产决定，不在 Mod 或文档中写死。

### Mod 执行策略

- 第一阶段选择能稳定命中至少三个喜好 Tag 的料理/酒水组合。
- 第二阶段和第三阶段分身均保留原订单料理/酒水，只在原料理上选择安全加料，并同时要求预估 `ExGood` 和当前目标 Tag。对第三阶段分身的 `ExGood` 要求是 Mod 的保守安全策略，不是游戏原生最低门槛。
- 目标 Tag 剩余时间不足时暂缓开锅。`AutomationCookingJob` 保存开锅时的挑战、阶段、目标 Tag 签名和实际执行目标；出锅后签名变化或成品 Tag 不匹配时，只将本 generation 成品提交到 `IzakayaConfigure.StoreFood()`，并屏蔽已确认失败的配方/加料组合。
- 古明地恋本体护盾期使用场上揭示的正面料理、厌恶料理和酒水 Tag；破防后不再绑定轮换目标 Tag，但必须保留原订单料理/酒水，并按剩余目标分、预算和剩余提交次数共用同一投食规划入口。
- `guestId=2006` 不能单独证明是古明地恋本体。分身优先通过 `GuestControllerSpawnType=GhostInChallenge` 识别；本体还需要第三阶段、订单类型、手动控制器状态或真实 controller 绑定等证据。
- 本体送齐后必须进入游戏 `EvaluateOrder()` 和 Boss `OverrideEvaluationCallback` 结算链路。订单生成回调只作为诊断证据，不作为送达或评价的唯一条件。

## 幽幽子挑战与重修

### 游戏原生规则

相关数据类型为 `GameData.Profile.YuyukoBossData`。第二阶段的目标数来自 `phase2TargetPositiveSpells`。`SpecialGuestsController.PostEvaluation`（`180514F20`）只在稀客标准评价为 `ExGood` 时调用 `TriggerPositiveBuff`；挑战将 `GuestsManager.OnPositiveSpellTriggered` 接入 `AddPositiveSpellCount`（`18078A2D0`），由后者增加计数并更新 HUD 进度。因此，第二阶段取决于每位稀客自身的标准评价，不使用幽幽子第三阶段的固定厌恶 Tag 或料理与酒水等级合计规则。

剧情版 `Story_Yuyuko` 的三阶段主幽幽子使用 `YuyukoOverrideEvaluationCallback_33`。伪代码 `18078A590` 直接读取已送达料理与酒水的等级之和并分档：

| 等级合计 | 评价 | 回调系数 |
| --- | --- | --- |
| `>= 8` | `ExGood` | `2.0` |
| `>= 5` | `Good` | `1.5` |
| `>= 2` | `Normal` | `1.0` |
| `< 2` | `Null` | 不进入上述评价档 |

重修版 `Challenge_Yuyuko` 的三阶段分身不使用该等级合计表，并且运行时存在两种订单形态：

- 稀客 `GuestsManager.SpecialOrder` 使用游戏标准点单评价。满足 `RequestFoodTag` 与 `RequestBeverageTag` 后形成基础 `Normal=2`，再按最终料理和酒水完整 `Tags` 中点单外的当前稀客喜好/厌恶调整评价；这类订单不是精确料理/酒水订单，也不是 modifier-only。
- 精确料理/酒水的 `OrderBase/NormalOrder` 先由原料理与原酒水匹配建立 `Normal=2`，后续才只统计实际生效的厨具/价格/大份等动态料理 Tag，以及由游戏列入 modifier、且相对原配方新增的额外材料 Tag。原料理自身的基础 Tag 不属于 modifier，不能再计一次喜好或厌恶。

`18078AFF0` 和 `18078B760` 附近的结果处理路径保留传入的原生评价：`Good` / `ExGood` 会按数据资产配置推进三阶段血量，`Normal` 会正常清理订单但不推进，`Bad` / `Exbad` 可能触发厨具锁定等处罚。具体扣血数值来自运行时字段，不写死在 Mod 或用户文档中。

### Mod 执行策略

- 第二阶段必须满足当前稀客的料理/酒水点单、避开该稀客自身的厌恶 Tag，并在排除点单 Tag 后命中至少 `2` 个额外喜好 Tag，以预估标准评价达到 `ExGood`。该阶段不得使用幽幽子的固定厌恶 Tag 或等级合计门槛筛选候选；没有满足条件的完整料理/酒水组合时保持无执行计划并输出候选阶段诊断，不得降级为 `Good`、`Normal` 或其他较低评价方案。
- 剧情版三阶段使用 `story-level-sum` 模式；`progress` 保留原订单料理/酒水，并要求等级合计至少为 `5`，等级合计至少为 `8` 的 `ExGood` 组合优先。只能达到 `Normal` 时按精确原订单清理，不承诺推进。
- 重修版稀客 `SpecialOrder` 使用 `retake-tag-order`。推荐必须满足料理/酒水点单，避开当前稀客厌恶，并至少额外命中一个当前稀客喜好，使保守评价从基础 `2` 达到 `Good>=3`；额外命中两个喜好的 `ExGood>=4` 方案优先。评价依据是最终料理与酒水的完整 Tag，不得把候选收窄到某个精确原料理/酒水，也不得要求 `expectedFoodModifierTags`。
- 重修版精确 `NormalOrder` 使用 `retake-food-modifiers`。候选在搜索前只保留订单指定的原料理和原酒水，并将单独生成的无加料原菜合并回候选集；只从实际生效的动态料理 Tag 与相对原配方新增的额外材料 Tag 判断能否把 `Normal=2` 提升到 `Good` / `ExGood`。没有可推进 modifier、但无加料方案仍保持 `Normal=2` 时，使用该精确订单清理桌位；任何会让结果低于 `Normal=2` 的 modifier 组合都必须拒绝。
- `NormalOrder` 在调用可能开锅的本地 API 前按经营 generation 锁存完整 execution target。评价前要求实际 `Sellable.Modifier` 加料 ID 与请求 extras 精确全等，并用 `Sellable.Tags.Except(RawTags)` 重建原生 `addedTags`，与锁存的 `expectedFoodModifierTags` 精确全等；响应时序和库存变化都不能替换已锁存目标，读取失败或不一致时暂停评价。`SpecialOrder` 不使用该 modifier-only 契约：门禁精确校验已锁定执行目标和实际加料，并确认两个请求 Tag 存在于实送料理/酒水的完整 Tag 数组；额外喜恶与最终档位由游戏原生评价计算。
- 原生评价前先用已匹配的 Completion 对象确认 `IsFullfilled`；未送齐只等待下一轮。评价定位必须 fresh read 已通过七个生命周期 Hook 与当前经营 generation 门禁的精确 capture，重新验证强身份、`PeekOrders()` 当前栈顶、已送达目标和同订单回调绑定，并要求 HTTP request、fresh capture 与 active store 的正 `OrderLifecycleSequence` 完全一致。只有幽幽子三阶段 `SpecialOrder` 的显式命名路径在 capture 不可用或专用复核失败时，才扫描 `GuestsManager` 当前 controller 并执行同一严格验证器；该路径仍必须取得同一 active lifecycle，`NormalOrder` 不使用 manager 回退。剧情版必须从最终选中的同一原生 order/controller 捕获记录复用 `SetManualControllerOrderInternal` 的 `onEvaluate`，通过该 controller 调用 `EvaulateManualOrder`，不能按相同请求身份借用另一对象的回调。重修版在同一阶段存在两条原生入口：捕获层把瞬时 `ManualOrder` 与精确 setter 绑定分开，setter 回调在同一活动订单状态更新中保持到原生移除或经营清理，空回调、不同回调和来源冲突都不可执行。主幽幽子订单只有在该稳定绑定精确匹配 `DisplayClass16_10 + b__77/b__78`，且 controller 为 `_50 YuyukoOverrideEvaluationCallback` 时才调用 `EvaulateManualOrder`；组订单只有在 capture 明确无手动绑定、具体类型已唯一解析为 `NormalOrder` 或 `SpecialOrder`，且 controller 仅为 `_70 GroupOverrideEvaluationCallback`、不含 `_50` 时才调用 `EvaluateOrder(controller, false, null)`。其他组合全部暂停，禁止从失败入口降级到另一入口。评价调用返回后还必须消费同 lifecycle 的 `Evaluated` receipt；外部评价、移除、清理或驱逐也只通过五个终态 Hook 的 exact receipt 收口。
- controller、对应版本的评价回调、已送达执行目标或订单形态对应的评价条件任一不可确认时，Mod 暂停送达或评价，不消耗订单来伪造完成。
- 幽幽子料理送达后的厨具严格 cleanup 或人工交接一旦结束，就单调释放该 job 的 cooker controller lease；后续 wrapper-free evaluation receipt 可以继续有界等待，但不得再占用厨具或阻断第三阶段其他订单。closeout 最多 12 次、至少 5 秒且最多 20 秒有效运行时间，耗尽只登记同生命周期人工栅栏，不重送、不猜测成功，也不恢复或扩大通用 manager fallback、文本 getter 或 pointer-only 兼容路径；上一条定义的幽幽子 `SpecialOrder` 显式命名 live-controller 例外保持唯一边界。
- 幽幽子 `SpecialOrder` 的强身份必须同时读到 `RequestFoodTag` 与 `RequestBeverageTag` 两项 raw signed ID；`0`、`-1` 等合法值原样保留，展示只查完整运行时 signed Tag map，map miss 有界记录并 fail-closed。禁止调用 `GetOrderFoodText/GetOrderBevText`、override 委托链、`SpecialGuest.Get*TagText`、`ToString()`、`#id` 或其他文本回退。任一已绑定 observer（包括未送齐或非送达 context）一旦在同一 native slot + lifecycle 看到 food/beverage raw ID 与捕获值冲突，就必须移除 capture、失效该 lifecycle，并停止发布 identity、trace 和订单；不得合并漂移值，也不得把 Provider 主动 fresh 扫描作为恢复方式，只有后续成功原生创建绑定产生新 lifecycle 才能恢复。

## 血池地狱

### 游戏原生规则

相关数据类型为 `GameData.Profile.DLC1_YuumaBossData`，挑战类型为 `Story_BloodPondHell`，挑战 BOSS 的 canonical 角色 ID 为 `1003`。当前反编译与 Native 伪代码可确认：

- 第二阶段混合生成 `NormalOrder` 与 `SpecialOrder`，第三阶段生成 `NormalOrder`。订单生成和集合委托的声明返回类型为 `OrderBase`，因此 BepInEx 回调看到基础包装类型不代表订单实际形态未知，也不能据此把它当作普通顾客订单。
- 原订单条件始终是第一层门禁。`SpecialOrder` 需要实送料理和酒水分别满足原始 `RequestFoodTag` / `RequestBeverageTag`；`NormalOrder` 需要先通过游戏对原料理和原酒水的原生匹配与评价。动态目标 Tag 不能替代原订单。
- HUD 每轮发布两个料理目标 Tag；两个 Tag 使用同时满足语义，任意一个缺失都不属于完整命中。
- 原订单不满足时走最低评价并触发订单不满足相关怒气与原生挑战副作用。原订单满足但双目标 Tag 未全部命中时保留普通评价并使用较低伤害，同时可能触发目标 Tag 不满足怒气；原订单和两个目标 Tag 都满足时进入最高评价并使用较高伤害。
- 伤害、怒气、阶段阈值和剩余时间由当前游戏运行时数据资产决定。Mod 不写死这些数值，也不直接调用伤害、怒气、目标刷新或阶段推进入口。

### Mod 执行策略

- 只在挑战类型精确等于 `Story_BloodPondHell` 时，通过共享 IL2CPP cast 入口分别尝试把声明为 `OrderBase` 的对象转换成 `NormalOrder` 和 `SpecialOrder`；只有恰好一种转换成功，并且具体订单角色与 controller 的 `OrderingGuest` 都能通过 `GuestBase.Id` 精确确认 canonical ID `1003` 时，才分类为血池地狱 BOSS 订单。双重成功、均失败、成员不可读或身份冲突全部 fail-closed；名称、显示文本、模糊类型判断和所有 `OrderBase` 放行不参与匹配。具体订单类型只决定进入普客或稀客列表，`yuuma-boss-order` 等角色只决定血池地狱推荐与自动化策略，不能把 `NormalOrder` 重新投影为稀客订单。
- 保留原订单料理和酒水约束，并始终先在可用料理及安全加料组合中搜索能同时提供两个动态目标 Tag 的严格方案。`SpecialOrder` 缺少严格方案时继续保持无计划。BOSS `NormalOrder` 仅在严格方案为空、同一原料理和原酒水仍有通过全部硬门禁的候选时，才选择受控推进；候选仍按已命中目标 Tag 数、正面/负面 Tag、加料数和资源压力排序。缺少厨具、基础材料、料理或酒水解锁、库存、排除条件、完整目标或强订单身份时不生成受控方案。
- 严格方案搜索使用只对 Yuuma 双 Tag 开启的可达性分桶：同一加料深度分别保留两个目标的已命中、仍可达和已被压制状态，确认不存在严格解后才允许受控推进。通用 beam 排序保持不变，避免该规则改变其他特殊经营或普通推荐。
- `executionPlans[0]` 是页面首项、自动化初始锁、游戏界面置顶、料理/材料/酒水列表项高亮、目标厨具高亮、目标桌位高亮和目标订单高亮的唯一目标。严格方案与受控推进方案都通过该入口投影；只有精确确认不属于 BOSS 的普通顾客订单保持普通规则。受控方案必须在页面原因、请求诊断和最终结算事件中明确标记，不能显示为完整双 Tag 方案。
- 前端与 Mod 使用同一规范特殊料理目标策略：`challenge=Story_BloodPondHell`、`owner=yuuma`、当前正数经营 generation、Ordinal 排序后的两个 Tag、`match=all` 及由这些字段生成的签名。缺少任一字段、目标数量不是两个、请求与实时策略不一致或策略绑定到非 BOSS 订单时 fail-closed；不保留旧 Wacky 专用签名或请求回退。`allowYuumaControlledProgression` 是执行目标和 cooking job 的独立许可，不属于该策略的 identity，也不得把 `All` 改成 `Any`。它只允许精确 Yuuma BOSS `NormalOrder`，请求必须保持 `foodId == matchFoodId`、`beverageId == matchBeverageId`；稀客、其他场景或非原订单项目携带该许可时直接拒绝。
- 受控请求必须显式携带完整预测 Tag 列表，且预测结果确实未满足当前双 Tag；预测已全命中却携带受控许可时拒绝。开锅前严格方案要求预测 Tag 全命中，受控方案只在上述显式许可成立时允许预测 Tag 未全命中。
- 自动化仅接管精确 `yuuma-boss-order`。开锅前验证主计划预测 Tag，出锅后读取实际 `Sellable` Tag；严格方案要求预测和实际成品同时满足双 Tag。受控推进只在策略 identity 与 revision 仍精确有效、实际 Tag 可读但未全命中时跳过这一项，不跳过原订单项目、实际料理 ID、订单/controller、厨具锅次、经营代际、送达状态或结算入口复核。策略变化、Tag 不可读、实际料理 ID 不符或非受控方案 Tag 不符仍进入既有 commit-once `StoreFood` 和同 generation 厨具复位链，无法确认提交或复位时保留人工确认栅栏。匹配成品在料理送达与订单完成双开关同时开启时进入专用精确无界面结算，任一开关关闭时进入手动交接；手动交接后不再送达、入箱或复位该锅，专用结算的最终 setter 开始后也不得回到恢复链。跨帧状态保存订单强身份、角色、特殊目标签名、受控推进许可和厨具的稳定预约/pointer/锅次标量，不保存 `CookController` wrapper；严格与受控目标不得复用同一个 job。每次读取、送达、复位和出锅回调前都从当前物理目录重新取得同一厨具。稀客和普客各自保存最后一个非空回退预算所有者，目标短暂清空时保留，下一不同非空签名到达后退休旧预算；黑暗料理或同一签名下的非目标成品仍正常消耗预算。挑战事件通过公开 `LockCookers` / `LockCookers_Forever` 与 `OnCookerAvailabilityUpdate` 更新物理拓扑时，只在这些方法同步执行期间阻止副作用；方法返回后必须先确认新的完整 `AllCookers + LockedCookers` 拓扑 revision。被锁、移除、替换、坐标漂移或锅次变化的旧 job 按一次 `cooking-ownership-lost` 释放并重新规划，未受影响的开放厨具不因永久锁锅长期停用。物理快照先读取 `LockedCookers`；锁定字典条目只确认 key 与 native identity 并计数，禁止读取 controller 坐标、状态、类型或 renderer。未锁条目仍精确交叉确认字典坐标、controller `GridPosition` 和 `CouldOpen`；被锁实体退出推荐、自动化预约和高亮。料理请求携带 index + native identity + grid，并在紧邻 `SetCook` 前复核，漂移时等待新快照而不改选。不得主动解锁、恢复、重建或扫描替代 controller。
- `AllCookers` 中原生确认的未摆放空位与事件锁定条目是两种独立状态。空位只闭合 controller 总数并保留原始 index，不进入已摆类型、推荐、自动化预约或高亮；锁定条目只保留字典位置、pointer identity 和数量，不读取或缓存其旧物理类型。存在锁定条目时，只有仍开放控制器证明的类型保留为可用，其余类型按零开放容量退出当前方案。两者都不能作为其他控制器的兼容替代来源。
- 酒水可由自动化或玩家送达；Mod 自动开锅后按经营 generation、订单形态、原始 nullable Tag 或 `NormalOrder` 精确 ID、桌位、canonical BOSS `1003`、目标签名与 revision、厨具锅次和实际成品严格复核。匹配成品只有在料理送达与订单完成双开关开启时才自动结算，否则保留在原锅并交给玩家。`EventManager.ShouldPlayerThrowDeliver` 只反映 `registeredTimedBuff` 中的玩家投掷送餐能力，不代表订单已有送餐动作在飞行中，因此不作为专用无界面结算门禁；真实并发只按当前订单的 `ServedFoodInAir` / `ServedBeverageInAir` 精确字段无副作用等待，且不生成、驱动或模拟投掷送餐界面与协程。
- 专用结算预检必须一次性确认同一经营 generation、订单与控制器原生指针、`OrderBase.ManualOrder` 精确 bool、最终料理 setter、fulfilled getter、订单对应评价入口，以及消费统计、Partner 状态和桌位通知入口。手动控制订单只允许使用同一次 `SetManualControllerOrderInternal` 为同一订单捕获的回调调用 `EvaulateManualOrder(controller, callback)`；标准订单只允许调用 `EvaluateOrder(controller, false, null)`，缺少回调、形态不匹配或身份漂移时 fail-closed，禁止把手动订单降级为标准评价。
- 在锁定不可逆料理提交前，必须 fresh 取得当前同一厨具并确认预约、pointer、未锁定、phase 3、实际 Result、ChosenRecipe 与 SetCook 所有权；预约位置已锁定时必须在读取 controller 状态前终止。随后执行顺序固定为最终料理 setter、第一次重新取得同一原生订单并完整确认其精确成品/路由/双身份/经营代际、fresh 同锅次复位、再次 fresh 后调用 `OnCookerAvailabilityUpdate(-1)`、回调返回后再次 fresh 并调用该控制器的 `AfterPlayerExtract()`、第二次 `FindYuumaRuntimeOrder` 与同等完整复核、fresh fulfilled、按订单形态评价、`StatusTracker.AddBussinessFoodConsumes`、`PartnerManager.OnOrderBaseStatusUpdate(FoodDelivered)` 和 `TryAddPlayerOccupiedDeskCode`。`AfterPlayerExtract()` 可合法在内部为 PureHellFryer 开下一锅，因此正常返回后不校验旧 Extract receipt；回调异常仍属于不可逆不确定。酒水专用入口从稳定 target 内部 fresh lookup，不接受外层通用 order wrapper；其库存序列对所有非零值（包括无限哨兵 `-1`）都调用缓存的精确 `BeverageOut(int,bool)` 和 `BeverageInRange/BeverageOutRange(IEnumerable<int>,bool)`，由游戏 API 保持无限哨兵，有限库存显示按免费净零或额外消耗全量计算。实际项目 `get_Tags()` 只接受 `Il2CppStructArray<int>`。酒水送达同样补齐 `AddBussinessBeverageConsumes`、`BeverageDelivered` 和桌位通知。所有记账对象、精确方法、枚举上下文和桌位必须在评价前缓存；评价返回后禁止重新读取可能已经释放的 IL2CPP wrapper，缓存 order 仅作为 Partner 状态通知的不透明参数。
- 每次 captured/live 专用查询都从当前订单精确读取 `ManualOrder` 并重新绑定同一 order/controller 的评价回调，不能沿用捕获时的旧路由。酒水事务在扣库前和每次 fresh reacquire 都读取 `ServedBeverageInAir`；初始非空只等待，不执行副作用，任何不可逆步骤后出现则进入不重放栅栏。最终料理同时拒绝 `ServedFoodInAir` 与 `ServedBeverageInAir`。酒水是唯一已送达项且订单尚未 fulfilled 时，标准订单在 final setter 后、range 库存调整前恢复一次原生耐心，手动控制订单不恢复；耐心回调和 range 回调后都重新复核目标 revision、订单和精确酒水。设置中任一料理送达或订单完成阶段开关从开启变为关闭时，使用对应 `rare` / `normal` scope 推进命令代际并撤销该组未进入不可逆步骤的 job，不能让开锅时锁存的旧开关继续结算，也不能清理另一订单组的已登记 job。
- 每个 cooking job 只使用小型单调 `YuumaSettlementTransactionTracker` 记录不可重放阶段；最终 setter、评价或记账任一阶段进入后结果不确定即转为终止人工确认栅栏，不得自动重试或跨路由补做。此前导致第三阶段无订单的是缺少完整原生顺序与订单路由复核的通用直评；评价只允许存在于上述专用入口。不得恢复旧的大范围 Yuuma finalization gate/coordinator、consumed/ACK 特例、HUD 进度 identity、送餐面板/UI 模拟、手动回调兼容分支、托盘、生成闭包、协程或 `MoveNext` 路径。
- HUD、挑战类型、订单分类和料理 job 诊断均按当前夜间经营 generation 隔离并有界去重；挑战类型无法读取时立即清除旧目标，使推荐、置顶、高亮和自动化 fail-closed，不把该场景当作普通经营继续处理。
- 规范 signature 不包含运行时 revision；revision 必须作为独立正数从同锁快照原样透传到前端 wire policy、本地 API、后端 target 和 cooking job，并在每个副作用边界与当前值精确比较。怪诞料理和空策略使用 `0`。因此 `A -> B -> A` 后第一轮 A 的迟到动作不能在第二轮 A 生效，缺失 revision 不按签名兼容放行。
- 酒水基础扣库返回后，必须先复核 generation/policy/revision 并 fresh reacquire 同一未提交订单，再从 fresh order 解析最终 setter；setter 和 range 调整返回后也分别重新复核。只有最后一次 fresh order 可以建立并执行经营记账上下文，任何一步不确定都不得重放扣库或 setter。

## 其他挑战

- `Story_Basic` / `Story_Advanced`：展示目标营业额和符卡计数，不改变推荐排序。
- 青娥、布都、屠自古料理挑战：展示目标营业额，不改变推荐排序。
- 云居一轮、村纱水蜜、寅丸星音游挑战、芙兰朵露的笼女游戏和 `Rogue Like`：不接入料理/酒水推荐。
- 瑞灵相关挑战：当前只提示挑战存在；抓捕、错误料理/酒水 Tag 和辣椒等副作用尚未有完整实机证据，不推断规则，不改变自动化。

## 实现约束

- C# 挑战上下文、订单分类、运行时匹配和场景策略放在 `mods/bepinex/src/Save/SpecialBusiness/`。前端规则、普客执行目标和失败组合处理放在 `apps/companion/src/companion/domain/special-business/`，由 registry 按 `challengeType` 分发。
- 挑战/BOSS 订单可能以 `OrderBase/Normal` 或 `SpecialOrder` 形态出现。“原订单匹配目标”用于找到真实运行时订单，“实际执行目标”用于开锅和送达，两者不得混用；`AllOrders` / `AllOrdersData` 是历史栈，禁止用于活动定位、业务 bootstrap 或捕获退休。一般订单只能使用由成功创建 Hook 锁存、七个生命周期 Hook 完整且覆盖当前经营 generation 的精确对象；捕获 key 只接受非零 IL2CPP 原生对象指针，不得回退到 managed hash。`PeekOrders()` 只复核其当前栈顶，不创建订单。古明地恋 BOSS 与幽幽子三阶段各自保留显式命名的 live-controller 专用定位，并继续执行各自额外原生门禁；不得把这两个例外抽象成通用 manager fallback。同一 native slot + lifecycle 的 raw Tag 冲突属于身份损坏，不是订单更新：任一已绑定 observer 发现后必须移除 capture、失效 lifecycle 且不发布替代身份，直到成功的新创建绑定分配新 lifecycle。
- 特殊经营规则完成计划排序后，`executionPlans[0]` 是该稀客订单唯一主执行计划。页面料理/酒水首项、自动化初始锁、游戏界面置顶、料理/材料/酒水列表项高亮、目标厨具高亮、目标桌位高亮和目标订单高亮必须消费同一计划，不得按场景再实现第二套目标选择。订单层可以跳过没有主计划的订单，但选中订单后不能扫描其后续计划。
- 任何跨帧待办、`AutomationCookingJob`、runtime event 和订单动作请求都必须保留挑战类型、owner、订单角色、原订单目标、执行目标、匹配模式、规范 Tag 签名、当前夜间经营 generation，以及 concrete order kind、order/controller native pointer 和进程单调 lifecycle sequence，防止旧订单、旧请求或复用 native tuple 串到新订单。阶段文本不单独进入 wire 身份；阶段导致目标 Tag 或匹配规则变化时，规范签名自然变化。进入 Closing 时清理特殊经营上下文、当前 generation 的安全栅栏和未完成 job，Destroyed 后不得再访问已失效的 Unity wrapper；调用栈内晚到的旧结果只形成有界 lifecycle-ended 诊断。
- 总日志必须记录原始/有效挑战类型、阶段、订单角色、match/execution 目标、评价回调证据、结构化 outcome 和阻断原因。用户帮助页只说明可观察行为，不暴露内部 hook 名称或伪代码地址。

## 重新验证清单

游戏更新、DLC 差异或 interop 重新生成后，恢复或扩展特殊经营前必须：

1. 核对 `Assembly-CSharp` 类型和方法签名。
2. 用 IDA 索引、调用交叉引用和伪代码重新确认 Native 副作用。
3. 用实机总日志确认 HUD 目标、订单归属、送达、评价和挑战进度。
4. 同步检查前端推荐首项、自动化初始锁、游戏界面置顶、高亮与诊断文案是否使用同一主执行计划和同一夜间经营 generation。
5. 血池地狱至少覆盖第一、第二、第三阶段，多笔 BOSS 订单并发、`ManualOrder=true/false` 两条评价路由、同名料理但不同原始 Tag、相同/不同 Tag 目标刷新、成品 Tag 不匹配、黑暗料理、玩家投掷送餐模式持续生效、真实料理/酒水 in-air、事件锁锅/毁锅、分别关闭自动送达料理与自动完成、取消、手动接管和场景退出；确认双开关开启时 Mod 完成送酒、开锅、料理提交、评价和原生状态通知，任一开关关闭时稳定进入人工交接，玩家投掷模式不阻断无界面结算而真实 in-air 期间无副作用等待，第三阶段持续生成并处理订单。锁定厨具应退出推荐、预约和高亮；任一不可逆阶段不确定后通用栅栏仍按现有 ACK 流程验证且不重放。
