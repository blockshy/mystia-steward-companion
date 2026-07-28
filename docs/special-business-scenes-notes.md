# 特殊经营场景规则与实现边界

更新日期：2026-07-12

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
| `Story_BloodPondHell` | 血池地狱挑战 |
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
- `IncomeControllerYuuma.SetTargetTag`：血池地狱挑战的双料理 Tag。
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
- `NormalOrder` 开锅确认后锁存完整 execution target。评价前要求实际 `Sellable.Modifier` 加料 ID 与请求 extras 精确全等，并用 `Sellable.Tags.Except(RawTags)` 重建原生 `addedTags`，与锁存的 `expectedFoodModifierTags` 精确全等；库存变化不能替换已开锅目标，读取失败或不一致时暂停评价。`SpecialOrder` 不使用该 modifier-only 契约：门禁精确校验已锁定执行目标和实际加料，并确认两个请求 Tag 存在于实送料理/酒水的完整 Tag 数组；额外喜恶与最终档位由游戏原生评价计算。
- 原生评价前先用已匹配的 Completion 对象确认 `IsFullfilled`；未送齐只等待下一轮。评价定位优先重新验证 capture 的强身份、controller 所有权、已送达目标和回调，失败后才扫描 `GuestsManager` 当前集合并执行同一验证器。剧情版必须从最终选中的同一原生 order/controller 捕获记录复用 `SetManualControllerOrderInternal` 的 `onEvaluate`，通过该 controller 调用 `EvaulateManualOrder`，不能按相同请求身份借用另一对象的回调。重修版必须确认 `_50` / `_70` 原生进度回调后调用游戏 `EvaluateOrder()`，不复用剧情版手动评价路径。
- controller、对应版本的评价回调、已送达执行目标或订单形态对应的评价条件任一不可确认时，Mod 暂停送达或评价，不消耗订单来伪造完成。

## 其他挑战

- `Story_Basic` / `Story_Advanced`：展示目标营业额和符卡计数，不改变推荐排序。
- `Story_BloodPondHell`：展示血池地狱挑战的双料理 Tag；由于 BOSS 怒气、伤害和评价副作用未完整验证，其特殊订单不交给标准自动化。
- 青娥、布都、屠自古料理挑战：展示目标营业额，不改变推荐排序。
- 云居一轮、村纱水蜜、寅丸星音游挑战、芙兰朵露的笼女游戏和 `Rogue Like`：不接入料理/酒水推荐。
- 瑞灵相关挑战：当前只提示挑战存在；抓捕、错误料理/酒水 Tag 和辣椒等副作用尚未有完整实机证据，不推断规则，不改变自动化。

## 实现约束

- C# 挑战上下文、订单分类、运行时匹配和场景策略放在 `mods/bepinex/src/Save/SpecialBusiness/`。前端规则、普客执行目标和失败组合处理放在 `apps/companion/src/companion/domain/special-business/`，由 registry 按 `challengeType` 分发。
- 挑战/BOSS 订单可能以 `OrderBase/Normal` 或 `SpecialOrder` 形态出现。“原订单匹配目标”用于找到真实运行时订单，“实际执行目标”用于开锅和送达，两者不得混用；`AllOrders` / `AllOrdersData` / `PeekOrders()` 返回的 `OrderBase` 必须通过共享 IL2CPP cast 入口规范为 `SpecialOrder` 后再读取特殊订单身份。古明地恋本体继续使用 manager 可发现的 live-controller 专用定位，不复用幽幽子三阶段的 capture 优先路径。
- 特殊经营规则完成计划排序后，`executionPlans[0]` 是该稀客订单唯一主执行计划。页面料理/酒水首项、自动化初始锁、游戏界面置顶、列表高亮和厨具高亮必须消费同一计划，不得按场景再实现第二套目标选择。订单层可以跳过没有主计划的订单，但选中订单后不能扫描其后续计划。
- 任何跨帧待办和 `AutomationCookingJob` 都必须保留挑战类型、阶段、订单角色、原订单目标、执行目标、Tag 签名和当前夜间经营 generation，防止旧订单或旧目标串到新订单。进入 Closing 时清理特殊经营上下文和未完成 job，Destroyed 后不得再访问已失效的 Unity wrapper。
- 总日志必须记录原始/有效挑战类型、阶段、订单角色、match/execution 目标、评价回调证据、结构化 outcome 和阻断原因。用户帮助页只说明可观察行为，不暴露内部 hook 名称或伪代码地址。

## 重新验证清单

游戏更新、DLC 差异或 interop 重新生成后，恢复或扩展特殊经营前必须：

1. 核对 `Assembly-CSharp` 类型和方法签名。
2. 用 IDA 索引、调用交叉引用和伪代码重新确认 Native 副作用。
3. 用实机总日志确认 HUD 目标、订单归属、送达、评价和挑战进度。
4. 同步检查前端推荐首项、自动化初始锁、游戏界面置顶、高亮与诊断文案是否使用同一主执行计划和同一夜间经营 generation。
