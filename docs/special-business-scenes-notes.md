# 特殊经营游戏规则

更新日期：2026-08-19

本文只记录已由游戏元数据、BepInEx #783 interop、IDA/Hex-Rays 和实机日志确认的特殊经营原生规则。Mod 如何选择和执行方案见[特殊经营实现](special-business-implementation.md)，验证状态与复测清单见[特殊经营验证](special-business-validation.md)。不得把 Mod 的保守策略写成游戏原生门槛。

## 证据与术语

证据使用顺序见 [IL2CPP / IDA 分析工作流](il2cpp-analysis-workflow.md)。评价名称使用游戏枚举 `ExGood`、`Good`、`Normal`、`Bad`、`Exbad`，角色使用游戏正式中文名称；内部类型名不能替代用户可见名称。

`RuntimeSpecialBusinessContextService` 读取 `NightSceneDirector.ChallengeMode`，挑战显示名来自 `NightSceneDirector.ChallengeType` 枚举成员的 `InspectorNameAttribute`。它是固定中文元数据，不随游戏语言切换。元数据不可读时只发布原始 challenge 诊断，不使用自建名称表或语言 API 合成标题。

当前与料理业务有关的 challenge：

| `ChallengeType` | 游戏标签 |
| --- | --- |
| `Story_Basic` / `Story_Advanced` | 妖梦科目一 / 科目二 |
| `Story_Yuyuko` | 幽幽子挑战 |
| `Challenge_Yuyuko` | 幽幽子重修 |
| `Story_BloodPondHell` | 血池地狱 |
| `Story_WackyCookingCompetition` | 怪诞料理大赛挑战 |
| `Story_Seiga_TempleCuisineCompetition` | 青娥 料理挑战 |
| `Story_Futo_TempleCuisineCompetition` | 布都 料理挑战 |
| `Story_Tochiko_TempleCuisineCompetition` | 屠自古 料理挑战 |
| `Story_Mizuchi` | 寻找瑞灵踪迹 |
| `Story_Mizuchi_1` / `_2` / `_3` | 月都试炼1 / 月都试炼2 / 月都试炼3 |

音游挑战、芙兰朵露的笼女游戏和 `RogueLike` 当前没有已确认的料理/酒水推荐规则。

## HUD 上下文

HUD Hook 只被动记录挑战已显示的目标：营业额、符卡数、阶段、料理 Tag、进度、怒气和剩余时间。捕获值只在当前 challenge 与来源匹配时有效；切换场景必须清除旧值。

HUD 目标不是订单 identity，也不直接写入普通 `RecommendationState`。营业额、符卡数和未确认结算条件只展示；只有已确认影响评价或挑战进度的料理条件才能进入特殊经营策略。

## 怪诞料理大赛

相关数据类型为 `GameData.Profile.DLC2_KoishiBossData`。

- 第一阶段按普客料理和酒水的喜好 Tag 总命中数改判：`0 -> Exbad`、`1 -> Bad`、`2 -> Normal`、`>= 3 -> Good`；达到 `Good` 增加挑战分。
- 第二阶段只有原始评价为 `ExGood` 时继续检查当前轮换料理 Tag，再按运行时数据计分。
- 第三阶段分身接受传入的 `Normal`、`Good` 或 `ExGood`。命中当前目标 Tag 时改为 `ExGood`，未命中时改为 `Normal`；“原生必须 ExGood”是不正确的结论。
- 古明地恋本体的数据包含护盾、揭示的正面/厌恶料理 Tag、酒水 Tag、投食目标分和等级效果数组。数值来自当前运行时资产，不在代码或文档中写死。
- `guestId=2006` 不能单独区分本体与分身；分身具有 `GhostInChallenge` spawn type，本体还需要阶段、订单形态和 controller 证据。

## 幽幽子挑战与重修

相关数据类型为 `GameData.Profile.YuyukoBossData`。

### 第二阶段

`SpecialGuestsController.PostEvaluation` 只在当前稀客标准评价为 `ExGood` 时触发正面符卡；挑战通过 `OnPositiveSpellTriggered` 增加计数。因此该阶段按每位当前稀客自己的点单、喜好和厌恶评价，不使用主幽幽子的等级合计规则。

### 剧情版第三阶段

`Story_Yuyuko` 主幽幽子按已送达料理与酒水等级之和评价：

| 等级合计 | 评价 |
| --- | --- |
| `>= 8` | `ExGood` |
| `>= 5` | `Good` |
| `>= 2` | `Normal` |
| `< 2` | 不进入上述评价档 |

### 重修版第三阶段

`Challenge_Yuyuko` 存在两种不同订单语义：

- `SpecialOrder` 使用标准 Tag 点单评价：料理/酒水点单建立基础 `Normal=2`，再按最终完整 Tag 中额外喜好/厌恶调整。
- 精确料理/酒水 `NormalOrder` 先由原料理和原酒水匹配建立 `Normal=2`，后续只统计实际生效的厨具、价格、大份以及相对原配方新增的 modifier Tag；原料理基础 Tag 不能重复计分。

结果路径保留传入评价：`Good` / `ExGood` 按运行时配置推进，`Normal` 清理订单但不推进，负面评价可能触发厨具锁定等处罚。具体数值不写死。

## 寻找瑞灵踪迹与月都试炼

四场共用 `GameData.Profile.DLC5_MizuchiChallengeBossData` 的订单和评价闭包。

- `Story_Mizuchi` 使用目标材料噗噗呦果 `5002`，异常控制类型从酒水、料理、对话三类中选择。
- 月都试炼 1/2/3 使用目标材料辣椒水 `5005`，控制类型分别固定为错误料理、错误酒水和错误对话。
- 被控制稀客的原始料理/酒水 Tag 不会被异常表现替换，仍是订单成立的第一层条件。
- 捕获只检查最终 `Food.Modifier` 是否包含目标材料；基础配方自带同材料不算，必须作为额外材料加入。
- 原生控制身份以 `currentGuestWhoIsControlledByMizuchi` 为准。合法 guest ID 包含 `0`；只有 `-1` 表示无当前目标。
- `controlled=-1 && control=None(3)` 是开场及每次捕获后的正式无目标保护期。活动期要求 controlled 非负，且 control 与当前 challenge 契约一致。
- 唯一评价委托及其闭包必须把 selected guest、group controller、订单 guest 与 controller guest 绑定到同一原生身份。
- Mod 不调用评价委托，不修改捕获数、Moon Eye、QTE 或阶段推进。

## 血池地狱

相关数据类型为 `GameData.Profile.DLC1_YuumaBossData`，challenge 为 `Story_BloodPondHell`，BOSS canonical ID 为 `1003`。

- 第二阶段同时可能生成 `NormalOrder` 和 `SpecialOrder`，第三阶段生成 `NormalOrder`。回调声明为 `OrderBase` 不代表具体形态未知。
- 原订单始终是第一层门禁：`SpecialOrder` 要求实送料理/酒水满足两项原始 Tag；`NormalOrder` 要求原料理和原酒水匹配。
- HUD 每轮发布两个料理目标 Tag，语义是同时满足；只命中一个不算完整命中。
- 原订单不满足时进入最低评价及对应怒气/副作用；原订单满足而双 Tag 未全中时保留普通评价和较低伤害；全部满足时进入最高评价和较高伤害。
- 伤害、怒气、阶段阈值、时间和目标刷新来自运行时资产。Mod 不主动调用这些推进或伤害入口。
- 事件可锁定或移除厨具；锁定实体不是可用厨具，也不能读取旧 controller 能力推断容量。

## 仅展示的挑战

- 妖梦科目一/二：展示目标营业额和符卡计数，不改变推荐。
- 青娥、布都、屠自古料理挑战：展示目标营业额，不改变推荐。
- 其他未列出挑战保持普通展示或不可用，不通过名称相似性套用规则。

## 修改规则

- 新结论必须标明来自游戏原生规则、Mod 策略还是尚未确认；三者不能混写。
- 游戏更新或 DLC 差异出现时，先重新取得 metadata、interop、IDA 和实机证据，再修改实现。
- 本文不保存 Hook 实现、自动化状态机、测试命令或某次实机进度；分别由实现与验证文档承担。
