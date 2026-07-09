# 特殊经营场景分析记录

当前版本在本地 API 中发布 `specialBusiness` 上下文，用于识别当前 `NightSceneDirector.ChallengeMode`，并在经营中页提示特殊经营场景。已能从 HUD 或运行时回调稳定确认且会影响评价的目标会进入经营中推荐：怪诞料理大赛会按阶段选择能触发高评价或命中当前目标 Tag 的执行料理，第二阶段和三阶段分身订单必须同时满足原订单料理/酒水、最高评价和当前目标 Tag；三阶段小石本体订单按场上揭示的正面料理 Tag、厌恶料理 Tag、酒水 Tag 选择执行料理/酒水，破防后必须先满足原订单料理/酒水，再结合剩余目标分和当前预算选择执行方案，避免高分高价组合提前耗尽预算；幽幽子挑战第二阶段会避开实际厌恶 Tag 并优先保证安全评价，第三阶段会避开实际厌恶 Tag，剧情版和重修版 P3 boss 普客都保持原订单料理/酒水匹配，优先选择按等级合计预测橙评/粉评的可推进目标；若原订单只能预测绿评，会以清理模式调用原生评价清理当前普客订单但不承诺推进进度，低于绿评、含厌恶 Tag、目标不一致或回调链不完整时会停止自动送达和评价；剧情版通过手动 `onEvaluate` 链路评价，重修版确认原生进度回调后走游戏 `EvaluateOrder()`；目标营业额、试炼目标金额和符卡计数只展示，不改变推荐排序。特殊经营目标不会写回 `RecommendationState`，常规稀客和普客页面仍按标准数据工作。

特殊经营中存在“运行时对象是 `OrderBase/Normal`，但实际归属于挑战/BOSS”的订单。该类订单会在经营中页显示特殊经营标识和日志 ID，并按场景决定是否允许标准普客/稀客自动化接管：幽幽子和怪诞料理大赛走原生订单送达与评价路径；饕餮尤魔仍阻止自动化，避免绕过原生怒气和伤害评价流程。

实现上按模块拆分：C# 侧 `mods/bepinex/src/Save/SpecialBusiness/` 负责 challenge 上下文规则、订单分类模块、运行时对象识别、场景专用自动化策略和诊断 helper，其中怪诞料理大赛的运行时策略集中在 `WackyCookingCompetitionRuntimePolicy`，通用自动化链路只委托该策略做场景判定；前端 `apps/companion/src/companion/domain/special-business/` 通过 registry 暴露经营推荐、普客执行目标、自动化延迟和失败组合屏蔽入口，规则构造放在 `rules/` 子目录，普客执行目标按场景放在 `normal-targets/wacky.ts`、`normal-targets/yuyuko.ts` 等文件。新增特殊经营场景时，先在对应目录新增场景模块，再把模块注册到 registry，不要在页面、推荐调用方或自动化流程里直接追加分散的 challenge type 分支。

## 当前支持边界

- 后端读取 `NightScene.NightSceneDirector.ChallengeMode`，发布挑战类型、中文说明、推荐策略提示、自动化策略提示、HUD 目标和运行时进度。
- 后端只捕获已通过反编译资料确认的 HUD 目标：
  - `IncomeControllerChallenge.SetTargetFund(targetAmount)` / `UpdateSpellCount(current, total)`：妖梦试炼目标营业额与符卡计数。
  - `IncomeControllerYuuma.SetTargetTag(tag1String, tag2String, useEffect)`：饕餮尤魔双料理 Tag。
  - `IncomeControllerKoishi.SetContext(context, currentValue, maxValue, phase)` / `SetTargetProgress(targetValue)` / `SetTargetTag(tag1String, useEffect)` / `SetTargetTagTime(progress)` / `SetTargetTagTimeImmediately(progress)`：怪诞料理大赛阶段进度、单料理 Tag 和 Tag 刷新倒计时比例。
  - `IncomeControllerYuyuko.SetContext(context, currentValue, maxValue, phase)` / `SetTargetProgress(targetValue)` / `SetTargetTime(progress)`：幽幽子挑战阶段进度与目标时间。
  - `IncomeControllerMausoleumCuisineCompetition.SetTargetFund(targetAmount)`：DLC3 料理竞赛目标营业额。
- 捕获目标只在当前 `ChallengeMode` 与目标来源匹配时展示，避免跨场景残留。
- 不在 `RecommendationState` 或库存修改中写入特殊场景规则；经营中推荐只对已确认会影响评价的挑战目标加规则。怪诞料理目标 Tag 是分身/二阶段订单的开锅前硬过滤，且第二阶段和三阶段分身需要同时硬性满足原订单和最高评价；三阶段小石本体不使用当前怪诞 Tag，而使用场上揭示的本体正面/厌恶/酒水 Tag，破防后仍先满足原订单料理/酒水，再按剩余目标分和当前预算规划投食总分；幽幽子第二阶段需要避开实际厌恶 Tag 并选择安全评价组合，第三阶段避开实际厌恶 Tag，剧情版和重修版 P3 boss 普客都保持原订单料理/酒水匹配，优先选择按等级合计可触发橙评/粉评的 `progress` 目标；若原订单组合只能达到绿评，则可生成 `refresh` 清理目标，用于调用原生评价清理订单但不承诺推进进度；低于绿评、含厌恶 Tag、目标不一致或回调链不完整时会停止自动送达和评价，喜好命中只用于同评价内排序；纯营业额和符卡目标只展示。
- 普客自动化支持“原订单匹配目标”和“实际执行目标”分离：后端仍用原订单的料理/酒水 ID 匹配运行时订单。只有确认游戏允许替代执行目标的场景才可以改用另一套料理/酒水；怪诞料理第二阶段和三阶段分身必须保持原订单料理和酒水，只允许在原料理上选择安全加料；三阶段小石本体破防前可使用揭示 Tag 计算出的替代料理/酒水，料理和酒水送齐后仍调用通用 `EvaluateOrder()` 触发线索阶段评价；破防后必须满足原订单料理/酒水，且不会绑定当前怪诞目标 Tag，评价交给 Boss 原生回调。跨帧 pending 出锅直送必须保存目标信息，出锅后继续按原订单目标定位订单，再按执行目标校验成品和送达。自动化步骤和总日志会记录原订单、执行目标与规则原因。

## 场景清单

- `Story_Basic` / `Story_Advanced`：妖梦科目一、科目二，多轮目标金额和符卡条件。
- `Story_Yuyuko` / `Challenge_Yuyuko`：幽幽子挑战/重修，阶段分数、血量、符卡和厨具锁定。
- `Story_BloodPondHell`：血池地狱/饕餮尤魔，随机两个料理 Tag 影响伤害和怒气。
- `Story_WackyCookingCompetition`：怪诞料理大赛，随机一个料理 Tag 影响挑战分数。
- `Story_Seiga_TempleCuisineCompetition` / `Story_Futo_TempleCuisineCompetition` / `Story_Tochiko_TempleCuisineCompetition`：DLC3 料理竞赛，目标营业额与 Boss/伙伴流程。
- `Story_Ichirin_MusicCompetition` / `Story_Minamitu_MusicCompetition` / `Story_Toramaru_MusicCompetition`：DLC3 音游挑战，不接入料理推荐。
- `Story_Flandre`：DLC4 笼女游戏，战斗、卡牌和血量流程。
- `RogueLike`：DLC5 肉鸽流程，非标准经营推荐。
- `Story_Mizuchi` / `Story_Mizuchi_1` / `Story_Mizuchi_2` / `Story_Mizuchi_3`：瑞灵相关挑战，抓捕、错误料理/酒水 Tag、辣椒等规则仍需实机日志确认。

## 已分析细节

### Story_WackyCookingCompetition

- 相关类型集中在 `GameData.Profile.DLC2_KoishiBossData`。
- 反编译线索包括 `Phase3GuestSpawnLoop`、`Phase3OrderLoop`、`KoishiSpecialOrder`、`GroupOverrideEvaluationCallback`、`KoishiOverrideEvaluationCallback` 和 `MainChallengeLoop` 内部计时逻辑。
- HUD 目标 Tag 线索来自 `NightScene.UI.HUDUtility.IncomeControllerKoishi.SetTargetTag(tag1String, useEffect)`。
- 游戏日志中可见 `The Best Tag:The Final Tags ...`，表示原生挑战会刷新本轮目标 Tag。
- 推荐系统会按阶段选择执行目标：第一阶段要求料理/酒水组合稳定命中普通客人喜好 Tag；第二阶段和三阶段分身要求先满足原订单料理/酒水，再保证最高评价估算和当前怪诞目标 Tag。当前目标 Tag 剩余比例过低时暂停分身/二阶段订单开锅，等待下一轮目标刷新。小石本体破防后不再使用当前怪诞目标 Tag，但必须先满足原订单料理/酒水，再按剩余目标分、当前预算和剩余提交次数规划投食总分；投食分优先按运行时料理等级 + 酒水等级估算，价格只作为等级缺失时的兜底，同时会避免在当前订单不能补满目标时把预算压到无法续单。自动化执行目标和经营中可视推荐首项必须共享同一套投食评分，确保关闭自动化后手动按推荐第一项制作也与自动化策略一致。日志会记录料理等级、酒水等级、投食分估算、喜好命中、厌恶命中、评价参考、剩余提交次数和预算诊断。
- 第一阶段常见情况是游戏原订单请求一个普通料理，但 Mod 为高评价改做另一道料理并加料；这类订单送达时不能用执行料理反查订单，只能用原订单的请求料理/酒水与订单 key 定位运行时订单。
- 第三阶段会出现 `GuestBase id=2006` 与 `OrderBase/Normal` 形态的 BOSS/分身订单；`guestId=2006` 不能单独作为小石本体依据。分身优先通过 `SpecialGuestsController.GuestControllerSpawnType == GhostInChallenge` 识别；最终 BOSS 也可能先以无 guest/controller 绑定的 `OrderControllerElement` / HUD 可见订单出现，随后由 `GuestsManager.SetManualControllerOrderInternal` 挂到 `NightSceneDirector.controlledGuest` 的手动控制器上，或以古明地恋 `SpecialOrder` 进入全力投食阶段。C# 侧需要通过统一特殊经营订单分类器结合 `Story_WackyCookingCompetition + Phase3`、订单来源、手动控制器状态、`SpecialOrder` 类型和真实 controller 绑定识别为怪诞小石订单。未破防的揭示线索阶段允许使用仍能通过原订单料理/酒水、桌号和控制器校验的 `RuntimeCapture:ManualOrderSet`，送齐后仍调用通用 `EvaluateOrder()`；破防后的全力投食阶段只能使用带 `OverrideEvaluationCallback` 的 live controller，不使用陈旧捕获对象，料理和酒水送齐后也必须通过该 live controller 调用游戏原生 `EvaluateOrder()` 进入 Boss 原生回调评分链路；`OverrideOrderGenerationCallback` 是订单生成证据，可能在订单生成后被清理，只能作为诊断信息，不能作为送达阶段硬条件。三阶段分身订单保持原订单料理/酒水并满足当前怪诞 Tag；最终 BOSS/全力投食订单不使用当前怪诞 Tag，但破防后仍必须满足原订单料理/酒水，前端到本地 API 的自动化请求必须携带 `specialBusinessRole=wacky-koishi-boss`，跨帧 pending 也必须保留该角色，避免空目标 Tag 回退成当前轮换 Tag。小石本体规则通过 `IncomeControllerKoishi.IntoShieldMode` 和 `DLC2_KoishiBossData.__c__DisplayClass13_0.MainChallengeLoop_g__SetKoishiLoveFoodAndSpawnStand_40` 生成点读取 `likeFoodTagNum`、`hateFoodTagNum`、`likeBevTagNum`、`koishiTag` 的完整揭示线索：护盾期按正面料理 Tag + 酒水 Tag、避开厌恶 Tag，破防期先满足原订单，再按剩余目标分、当前预算和预计投食分选择执行方案。该 local function 在当前运行时 interop 中暴露为 `Method_Internal_Void_2()`，不是反编译语义名；相邻的 `Method_Internal_Void_1()` 对应三阶段随机 Tag 刷新，`_MainChallengeLoop_b__77` 是生成函数内部排除厌恶 Tag 的谓词。`DisplayClass13_12.b__104` 只负责把线索文字写到 UI，不作为业务采集来源。
- 由于怪诞目标 Tag 会随时间刷新，C# 出锅直送前还会读取成品 `Sellable.Tags`，用 `DataBaseLanguage.GetFoodTag(int)` 转为文本后与当前目标 Tag 再校验一次。不满足当前目标时，成品通过 `IzakayaConfigure.StoreFood()` 放入保温箱，自动化事件会带上目标 Tag、实际成品 Tag、料理和加料组合；前端据此立即抑制同一失败组合，Mod 侧也会记录最近失败组合并阻止同一目标 Tag 下重复开锅，下一轮按当前目标重试其他可执行方案。

### Story_BloodPondHell / 饕餮尤魔挑战

- HUD 目标 Tag 线索来自 `IncomeControllerYuuma.SetTargetTag(tag1String, tag2String, useEffect)`。
- 当前只把目标 Tag 作为经营中推荐排序上下文，不修改标准订单需求，也不让目标 Tag 覆盖订单本身的可完成性。
- 尤魔挑战订单会阻止标准自动化接管，避免绕过原生怒气和伤害评价流程。

### Story_Yuyuko / Challenge_Yuyuko

- 相关类型集中在 `GameData.Profile.YuyukoBossData` 与 `NightScene.UI.HUDUtility.IncomeControllerYuyuko`。
- `YuyukoBossData` 中可确认阶段分数、符卡、三阶段血量、分身扣血和料理等级到血量变化的规则入口；具体扣血数值来自运行时数据资产，当前只记录上下文诊断，不在 UI 中展示固定伤害数字。
- 反编译资料显示剧情版 P3 存在按已送达料理等级 + 酒水等级分档的评分回调：等级合计 `>= 8` 可获得更稳定的高评价，`>= 5` 只达到较低推进档；进度推进绑定在 `SetManualControllerOrderInternal` 传入的剧情 `onEvaluate` 回调上。自动化必须复用捕获到的 `onEvaluate`，通过当前 live controller 调用游戏 `EvaulateManualOrder(controller, onEvaluate)`，让评分与剧情进度回调按原生手动订单顺序执行。
- 重修版 P3 的血量/进度推进绑定在原生 `EvaluateOrder()` 的 `_50` / `_70` 评价回调上，不应复用剧情版手动 `onEvaluate` 路径。Mod 会同时记录原始 `ChallengeMode`、有效挑战类型、`DifficultyMode` 和回调证据；当运行时仍上报 `Story_Yuyuko` 但已捕获重修难度或重修评价回调时，按重修版规则发布上下文和执行自动化。
- 推荐系统在第二阶段捕获到阶段信息时会排除 `素`、`小巧`、`清淡` 等幽幽子实际厌恶料理 Tag，并优先选择能稳定达到安全评价的组合，降低负面符卡触发差评的概率。第三阶段用料理等级 + 酒水等级预测评价：`progress` 模式要求橙评/粉评并作为可推进目标；`refresh` 模式只允许精确原订单且预计至少绿评、低于橙评的组合，用于清理卡住的普客订单但不承诺推进。剧情版和重修版 P3 `yuyuko-boss-order` 普客形态订单都以原订单料理 `foodId` 和酒水 `beverageId` 匹配运行时订单，不再把普客 `foodId` 回退解释为 `recipeId`；若执行目标与原订单不一致，Mod 会在送达或评价前阻断并记录 `originalFoodMatched` 与 `originalBeverageMatched` 诊断；稀客订单的推荐首项、执行方案和自动化目标共享同一套幽幽子评分模型。喜好命中继续参与排序，但不能让低于绿评或含实际厌恶 Tag 的组合通过。其他阶段只展示进度，不改变排序。
- 日志中确认幽幽子三阶段存在 `SpecialGuest id=23/40` 的 `SpecialOrder` 与 `OrderBase/Normal` 混合订单。该类订单保留剧情版/重修版特殊经营标识，自动化可以通过当前有效的 captured 原生订单对象送达料理和酒水；但评价阶段必须重新匹配当前 live controller。剧情版必须使用 captured 订单中来自 `SetManualControllerOrderInternal` 的 `onEvaluate` 调用 `EvaulateManualOrder`；重修版必须确认 `_50` / `_70` 原生进度回调后调用游戏 `EvaluateOrder()`。`progress` 模式会继续校验已送达料理和酒水等级合计达到推进阈值；`refresh` 模式会改为校验已送达成品精确等于请求目标与对应版本回调链，不再因等级低于 Good 阻断，但日志会明确记录 `executionMode=refresh` 和“不承诺推进”。live `OrderBase` 读取不到客人字段时，不再仅因 `guestId` 不可读而拒绝；若 controller、对应版本的评价 callback、送达目标、普客原订单不变量或清理/推进阈值不满足，Mod 会暂停自动送达或自动评价，不调用错误评价链路消耗订单。
- 幽幽子三阶段出锅直送只允许读取 `CookController.Result` / `<Result>k__BackingField` 的料理 `Sellable`。`CookController.result`、`resultVisual` 是视觉 `SpriteRenderer`，不得作为成品或 `StoreFood(Sellable, Int32)` 参数。稀客 pending 目标需要携带订单 trace 与料理/酒水 Tag，防止上一单未完成时拦截下一单。
- 总日志开启时，幽幽子 HUD 上下文和自动化链路会写入 `special-business.yuyuko` section，记录阶段、当前/最大值、目标进度、目标时间、原始/有效挑战类型、难度、请求目标、运行时订单匹配、送达料理/酒水等级、剧情手动 `onEvaluate` callback、重修原生进度 callback、原生 `EvaulateManualOrder` / `EvaluateOrder` / `PostEvaluation` 参数和阻断原因，用于对照推荐与自动化行为。
- `IncomeControllerYuyuko.SetContext` 切换阶段时需要清空上一阶段目标进度，避免 UI 在下一次 `SetTargetProgress` 到来前显示旧目标。

### Story_Basic / Story_Advanced

- 相关类型集中在 `GameData.Profile.GeneralTrialChallengeBossData`、`BasicChallengeTrialOneData` 和 `AdvancedChallengeTrialTwoData`。
- `IncomeControllerChallenge.SetTargetFund` 与 `UpdateSpellCount` 可捕获目标营业额和符卡计数。该类目标只展示，不改变推荐排序，也不推断每轮隐藏条件。

### DLC3 料理竞赛

- HUD 目标金额线索来自 `IncomeControllerMausoleumCuisineCompetition.SetTargetFund(targetAmount)`。
- 目标营业额会展示到经营中页，但不改变推荐排序。

## 后续恢复适配前需要确认

- 目标 Tag 来源必须稳定，可从 HUD、运行时对象或 Harmony 捕获中交叉验证。
- 适配不能绕过游戏原生送达、移动、评价和挑战结算回调。
- 前端推荐、本地 API 快照、运行时数据仓库和自动化选菜必须保持同一套规则。
- 若要进一步自动化其他 BOSS 挑战订单，必须提供对应阶段的总日志，确认原生移动、送达、评价和挑战结算回调，不得复用会绕过原生挑战回调的完成方式。
