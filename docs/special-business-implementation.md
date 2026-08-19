# 特殊经营实现

更新日期：2026-08-19

本文只记录 Mod 对已确认特殊经营规则的推荐和自动化策略。游戏原生规则见[特殊经营游戏规则](special-business-scenes-notes.md)，通用订单与自动化安全边界见[订单捕获与生命周期](runtime-order-lifecycle.md)和[自动化运行时](automation-runtime.md)。

## 共同架构

- C# challenge 上下文、订单角色和运行时匹配位于 `mods/bepinex/src/Save/SpecialBusiness/`。
- 前端规则位于 `apps/companion/src/companion/domain/special-business/`，通过 registry 按 exact `challengeType` 接入。
- “原订单目标”用于定位和验证真实订单，“实际执行目标”用于推荐、开锅和送达；两者不能互换。
- `executionPlans[0]` 是页面首项、自动化初始锁和游戏 UI 目标的唯一主执行方案。场景模块不能再选第二次。
- 所有跨帧状态保留 challenge、owner、订单角色、match/execution 目标、Tag 策略签名、经营 generation、具体订单类型、order/controller 指针和 lifecycle sequence。
- challenge、角色、闭包、阶段、Tag、revision 或 identity 不完整时 fail-closed，不把特殊订单转回普通规则继续执行。

## 怪诞料理大赛策略

- 第一阶段选择能稳定命中至少三个喜好 Tag 的料理/酒水组合。
- 第二阶段和第三阶段分身保留原订单，只在原料理上选择安全加料，并保守要求预估 `ExGood` 和当前目标 Tag。第三阶段的 `ExGood` 是安全策略，不是原生最低门槛。
- 目标剩余时间不足时不开始新锅。CookingJob 锁定开锅时 challenge、阶段和 Tag 签名；出锅时签名变化或成品 Tag 不符则进入受控回收，不送到旧目标。
- 古明地恋本体护盾期按揭示的正面/厌恶/酒水 Tag 规划；破防后按剩余目标分、预算和提交次数共用同一投食评分入口。
- 本体与分身必须通过 spawn type、阶段、订单形态和 controller 身份区分，不能只看 guest ID。

## 幽幽子策略

- 第二阶段按当前稀客标准点单评价寻找 `ExGood`：满足料理/酒水点单、避开该稀客厌恶，并在点单外至少命中两个喜好。无完整方案时保持阻断，不降级。
- `Story_Yuyuko` 第三阶段使用 `story-level-sum`：保留原料理/酒水，等级合计至少 `5` 才作为推进方案，`>= 8` 优先。
- `Challenge_Yuyuko` 的 `SpecialOrder` 使用 `retake-tag-order`：满足两项点单并至少增加一个额外喜好，两个额外喜好的方案优先。
- `Challenge_Yuyuko` 的精确 `NormalOrder` 使用 `retake-food-modifiers`：只保留原料理/酒水，仅以真实生效的动态或新增 modifier Tag 判断能否从 `Normal` 提升。
- 可能开锅前锁存完整 execution target。评价前 fresh 验证加料、成品、订单 lifecycle、`PeekOrders()` 和回调绑定。
- 只有显式命名且通过完整闭包/身份门禁的幽幽子 `SpecialOrder` 可以使用专用 live-controller 路径；该例外不能抽象成一般 manager fallback。
- 料理已送达后的厨具 lease 可先释放，评价回执继续有界等待；超出 closeout 预算只产生人工栅栏，不重送或猜测成功。

## Mizuchi 策略

- 只匹配 `Story_Mizuchi` 与 `Story_Mizuchi_1/_2/_3`，基础场景和三场试炼使用不同 wire role，不互认。
- 共同 identity reader 精确复核评价委托、两层闭包、selected/group/order/controller guest、控制类型和目标材料。
- `(-1,None)` 是 ordinary 保护期；合法活动目标按 controlled guest 把订单分为 possessed/ordinary；其他组合为 unverified。
- possessed 订单除原料理/酒水 Tag 外，基础场景必须额外加入 `5002`，试炼必须加入 `5005`；ordinary 订单禁止该场目标材料。
- 自定义料理只有明确包含目标额外材料时才能进入 possessed 候选。目录、库存、禁忌或五格上限不允许时保持无方案。
- 回调、身份、材料或 lifecycle 漂移时停止后续副作用；不通过旧 role、名称、文本或宽松反射恢复。

## 血池地狱策略

- 只有 challenge、具体订单类型和 order/controller guest 都精确确认 BOSS `1003` 时，才标记 `yuuma-boss-order`。声明为 `OrderBase` 时必须通过共享 cast 恰好解析为一种具体订单。
- 保留原订单条件，并先搜索同时满足两项动态料理 Tag 的严格方案。`SpecialOrder` 严格无解时保持阻断；只有 BOSS `NormalOrder` 可在严格无解且原料理/酒水仍合法时进入显式受控推进。
- 双 Tag 使用 `require + all`。Yuuma 严格搜索按 `matched/reachable/blocked` 保留可达分支，不能被通用 beam 提前裁掉。
- 受控推进由 `/orders/normal/complete-first` 的 `allowYuumaControlledProgression` 显式授权，并携带完整预测
  Tag；预测必须确实未全中。该许可须贯穿后端 target、CookingJob 和快照 identity，严格 job 与受控 job
  不能复用；它不放宽原订单、实际料理、订单 identity、厨具锅次或结算路由。
- 规范策略签名与运行时 target revision 分离。revision 必须作为独立正数贯穿快照、Worker、请求、后端 target 和 CookingJob，防止 `A -> B -> A` 接受第一轮迟到动作。
- 已匹配成品在当前主设备 profile、authority revision 和 `YuumaSettlement` permit 暂不可用时保留在原锅并可恢复；玩家取走或替换成品才转为手动交接。
- 最终结算以一次 permit 串行执行料理/酒水提交、fresh reacquire、厨具复位、`AfterPlayerExtract`、fulfilled、评价和经营记账。任何不可逆阶段结果不确定都进入不重放的人工栅栏。
- 每次厨具操作都从当前物理目录按 index、native identity、grid 和锅次重新取得 controller。事件锁定、移除、替换或坐标漂移会释放旧 job；不主动解锁、恢复或扫描替代厨具。

## 禁止路径

- 不枚举 `AllOrders` / `AllOrdersData` 建立活动订单，不用 HUD 或 manager 扫描补建一般捕获。
- 不按名称、文本、模糊类型、托管 hash、桌位或料理相同合并订单。
- 不把特殊经营条件写入普通推荐状态后让普通规则猜测。
- 不保留旧 challenge role、旧签名、旧请求参数、通用直评、手动回调兼容或 UI 模拟路径。
- 不在文档或代码写死来自数据资产的伤害、阈值、倒计时或投食次数。

## 维护与验证

修改场景模块时同时检查游戏规则证据、推荐 primary plan、自动化、游戏 UI 目标和日志 identity。自动化测试与实机覆盖见[特殊经营验证](special-business-validation.md)。
