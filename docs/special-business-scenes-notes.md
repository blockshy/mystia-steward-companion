# 特殊经营场景分析记录

当前版本不在运行时代码中适配特殊经营场景。推荐和自动化只走标准料理、酒水链路：稀客按点单 Tag、喜好、库存、厨具和排序规则推荐，普客按订单料理和阶段开关处理。遇到怪诞料理大赛、饕餮尤魔挑战等特殊规则时，以游戏内提示和原生结算为准。

## 已分析场景

### Story_WackyCookingCompetition

- 相关类型集中在 `GameData.Profile.DLC2_KoishiBossData`。
- 反编译线索包括 `Phase3GuestSpawnLoop`、`Phase3OrderLoop`、`KoishiSpecialOrder`、`GroupOverrideEvaluationCallback`、`KoishiOverrideEvaluationCallback` 和 `MainChallengeLoop` 内部计时逻辑。
- HUD 目标 Tag 线索来自 `NightScene.UI.HUDUtility.IncomeControllerKoishi.SetTargetTag(tag1String, useEffect)`。
- 游戏日志中可见 `The Best Tag:The Final Tags ...`，表示原生挑战会刷新本轮目标 Tag。
- 曾观察到第三阶段直接自动送达和评价可能触发游戏侧空引用，栈上涉及 `DLC2_KoisBossData`、`GuestGroupController.MoveToSpawn`、`AStarInputGeneratorComponent.SetPath` 和 `StartCoroutine`。

### Story_BloodPondHell / 饕餮尤魔挑战

- HUD 目标 Tag 线索来自 `IncomeControllerYuuma.SetTargetTag(tag1String, tag2String, useEffect)`。
- 早期设计曾尝试把目标 Tag 纳入料理排序，但这会把挑战规则、推荐排序和自动化状态耦合在一起。

## 后续恢复适配前需要确认

- 目标 Tag 来源必须稳定，可从 HUD、运行时对象或 Harmony 捕获中交叉验证。
- 适配不能绕过游戏原生送达、移动、评价和挑战结算回调。
- 前端推荐、C# 推荐镜像、本地 API 快照和自动化选菜必须保持同一套规则。
- 必须提供可关闭的用户设置，并在帮助页说明风险。
