# 特殊经营验证

更新日期：2026-08-19

本文只记录特殊经营的自动化验证入口、当前实机闭环状态和复测清单。规则事实见[特殊经营游戏规则](special-business-scenes-notes.md)，实现策略见[特殊经营实现](special-business-implementation.md)。

## 自动化验证

前端规则、上下文、运行时分类、血池结算和厨具拓扑的聚合入口：

```bash
corepack pnpm audit:special-business
corepack pnpm audit:mizuchi
corepack pnpm audit:automation
```

按修改范围可单独运行：

```bash
dotnet run --project tests/runtime-mizuchi-special-business/RuntimeMizuchiSpecialBusinessSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-special-business/RuntimeYuumaSpecialBusinessSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-finalization/RuntimeYuumaFinalizationSmoke.csproj -c Release
dotnet run --project tests/yuuma-cooker-topology/YuumaCookerTopologySmoke.csproj -c Release
dotnet run --project tests/special-order-runtime-capture/SpecialOrderRuntimeCaptureSmoke.csproj -c Release
dotnet run --project tests/rare-order-identity-matching/RareOrderIdentityMatchingSmoke.csproj -c Release
```

逐条断言由测试源码负责；完整分层和平台要求见[验证指南](validation-guide.md)。游戏成员或闭包变化时还必须按 [IL2CPP / IDA 分析工作流](il2cpp-analysis-workflow.md)重新取得证据。

## 当前实机状态

- 月都试炼 1/2/3 已逐场确认推荐与自动化正常；试炼 1 还闭环了合法 guest ID `0`、possessed/ordinary、辣椒水 modifier、捕获推进和 `(-1,None)` 保护期。
- 寻找瑞灵踪迹已确认 challenge、剧情入口和订单链。当前实现已接入材料 `5002` 与控制身份，但仍需在最新构建完成下面列出的完整基础场景复测。
- 其他场景只把具有完整日志、诊断和用户确认的路径标记为闭环；测试通过不能代替真实游戏评价和阶段推进。

该状态用于规划复测，不是兼容性开关。未闭环路径继续按当前严格门禁 fail-closed，不增加宽松反射或文本回退。

## 通用实机记录

每次复测记录：

- 游戏 build、BepInEx、Mod 版本和同时启用的其他 Mod；
- challenge、阶段、经营 generation、订单 trace/lifecycle 和 concrete kind；
- 原订单目标、执行目标、primary plan、加料和酒水；
- 预期/实际评价、挑战进度与厨具状态；
- 总日志和诊断包的时间范围；
- 是否发生断线、切主设备、阶段开关变化、玩家手动接管或场景退出。

## 怪诞料理大赛

至少覆盖：

- 第一阶段 0/1/2/3 个喜好命中；
- 第二阶段 `ExGood` 但目标 Tag 命中/未命中；
- 第三阶段分身与本体，护盾期与破防期；
- 目标倒计时不足、开锅后目标刷新、成品 Tag 不符和回收；
- 分身/本体 identity 歧义时保持暂停。

## 幽幽子

至少覆盖：

- 第二阶段按当前稀客评价达到/未达到 `ExGood`；
- 剧情版第三阶段等级合计 `<2`、`2..4`、`5..7`、`>=8`；
- 重修版 `SpecialOrder` 与精确 `NormalOrder` 两种形态；
- modifier 与成品 Tag、`-30` 厨具 Tag、加料不一致和订单未 fulfilled；
- 手动/标准评价路由、回调 identity、评价回执和厨具 lease 提前释放；
- closeout 超时只产生人工栅栏，不重复送达或评价。

## Mizuchi

基础场景最新构建仍需至少闭环：

- 一笔 possessed 和一笔 ordinary 订单；
- 一次持续的 `(-1,None)` 保护期；
- 目标材料 `5002` 作为真实 modifier；
- 捕获 HUD 推进与最终完成；
- 错误 guest、错误 control、错误材料和 identity 漂移时无副作用。

三场试炼回归至少各覆盖固定 control、材料 `5005`、原料理/酒水 Tag 和保护期。

## 血池地狱

至少覆盖：

- 第一、第二、第三阶段，以及第二阶段 `NormalOrder` / `SpecialOrder` 混合；
- 严格双 Tag 有解、严格无解的 `NormalOrder` 受控推进、`SpecialOrder` 严格无解阻断；
- 多笔 BOSS 订单、相同料理但不同原始 Tag、目标 `A -> B -> A` revision 隔离；
- 预测/实际 Tag 不符、黑暗料理、错误成品和手动接管；
- 事件锁锅、毁锅、坐标/identity/锅次漂移和下一锅在 `AfterPlayerExtract` 内合法开始；
- 料理/酒水 in-air 等待，玩家投掷模式不被误判为 in-air；
- 分别关闭料理送达、订单完成、订单组和总控，以及切换主设备或 lease 过期；
- permit 前暂停可恢复，permit 后原子结算不被中途撕裂；
- 任一不可逆阶段不确定后只产生人工确认栅栏且不重放。

## 完成标准

只有自动化测试、运行时 identity、原生评价/进度和实机日志相互一致，才把路径标记为闭环。若证据不足，明确记录缺口并保持当前门禁；不能因为“看起来成功”降低验证条件。
