# 游戏界面辅助

更新日期：2026-08-19

本文说明 Mod 如何把伴随窗口选出的普客与稀客目标投影到游戏原生 UI。目标如何选出见
[推荐引擎](recommendation-engine.md)，订单身份和生命周期见
[订单运行时生命周期](runtime-order-lifecycle.md)。

## 职责边界

游戏界面辅助只增强已有的料理、酒水、厨具、座位和订单界面，不创建独立游戏菜单，也不替代游戏的原生
选择、提交或评价逻辑。当前功能包括：

- 把目标食材、料理和酒水排到原生列表前部并着色。
- 为目标料理提供基于推荐加料的额外料理行。
- 高亮对应厨具和座位。
- 高亮经营 HUD 与投掷送餐面板中的对应订单。

所有功能默认受实验性功能设置控制。任一运行时身份、页面绑定或生命周期无法精确证明时，都必须保持原生
界面不变并 fail closed。

## 目标发布模型

前端通过 `apps/companion/src/companion/domain/game-ui-targets.ts` 构造目标，
`apps/companion/src/companion/hooks/useGameUiTargetPublisher.ts` 以一个原子 target set 发布到
`POST /ui-pinning/targets`。

一个 target set 最多包含一个稀客目标和一个普客目标，并按稀客、普客的稳定顺序发布。每个目标必须携带：

- `kind`、目标色和内容修订。
- 订单 trace、lifecycle、桌位，以及普客的原生 order key。
- 料理、基础食材、有序加料、酒水和厨具类型。
- 五个目标级功能位：列表置顶、加料料理、厨具高亮、座位高亮、订单高亮。

功能位属于具体目标，不能恢复集合级总开关。加料料理依赖同一目标的列表置顶；全部功能均关闭的 target 无效。
后端 `RuntimeUiTargetSet` 只保存不可变托管标量，可由 API 线程更新；任何 Unity wrapper、指针或场景对象只允许
在 Unity 主线程解析和使用。

设备主权威或生效 profile 改变时，后端先推进一个空目标的 authority fence，再由下一次主线程 Tick 接受新
目标。这个边界不会销毁仍打开的页面登记，也不会猜测中止已经发生的加料事务。只有经营进入 Closing、
Destroyed，或控制器 shutdown，才进行终态退休。

## 页面登记与窄刷新

料理页只 Hook `WorkSceneCookingSelectionPannel.OnPanelOpen` 和 `OnPanelClose`。已登记页面在每次主线程
Tick 先验证 wrapper 指针，再按同一 publication lease 和 target scope 执行：

```text
UpdateIngField
-> UpdateRecipeField
-> m_StaticIngredientsGroup.UpdateElements()
-> m_StaticRecipeGroup.UpdateElements()
```

酒水页对应执行：

```text
UpdateBevField
-> m_BevsGroup.UpdateElements()
```

这是“目标变化时刷新已打开列表”的唯一窄刷新路径。它重建列表数据和列表行，但不重建已选材料区或 output
surface。任一步失败都不能提交 applied，也不能在同一代际盲目重放。

`WorkSceneCookingSelectionPannel.OnPanelDestroyed` 与另一个面板共享空 IL2CPP 原生别名，禁止安装该
Hook。料理页生命周期由 open/close 登记和每帧指针验证处理。酒水仓库页的 Destroy 方法有独立非空原生实现，
保留其精确 Hook。

## 加料料理事务

加料行只在目标级 `RecipeVariantEnabled` 开启且推荐包含加料时产生：

- 相同基础料理和相同有序加料合并 claims；不同加料组合保持独立行。
- 原始基础料理行始终保留。
- 当前厨具内每个方案的权威 `Recipe` 必须唯一；无匹配表示该厨具不适用，多重匹配则整项 fail closed。
- 所需的精确 Hook 必须全部安装成功后才允许向原生列表 Insert。
- synthetic recipe、权威 recipe pointer、来源 identity、页面 epoch、target generation 和 publication lease
  共同组成事务身份。

选择加料行后，额外扣料、原生 List 写入、output callback 和换菜过程采用显式状态机与 receipt。原生调用已
发生但结果无法证明时，事务进入 `Uncertain`，本场不重放，也不猜测退款。切换基础料理、普通行或另一加料行
只能由真实 submit 建立 switch attempt；只有原生链路正常完成且 exact receipt 一致，才能结束旧事务。

加料事务的细粒度状态、闭包所有权和嵌套 `UpdateAllVisual` 约束由
`tests/runtime-target-recipe-variant/` 锁定。维护实现时应以专项测试为完整契约，本文不复制逐条断言。

## 高亮资源所有权

各高亮服务只修改或销毁自身创建、且能再次精确证明所有权的资源：

- 列表行：保留游戏原生 callback 和 `interactable`，只管理 Mod 自己的颜色租约。
- 厨具：精确绑定 controller、renderer 和原始颜色。
- 座位：从精确 tile/sprite 几何创建独占的 fill、texture、sprite 和 material 资源。
- HUD 订单：只在精确订单 identity 与 pool membership 匹配时挂接自有 Image。
- 投掷送餐：只在 exact button、listener、背景层和 selection 结构全部验证后插入自有 fill。

禁止用对象名称、显示文本、层级路径、数组下标、近似几何、场景扫描或替代视觉兜底。禁止修改原生背景颜色、
焦点、listener 或 callback 来“兼容”未知结构。

默认目标色为稀客 `#FFDB2E`、普客 `#5FACD3`。同一物理对象被两类目标共同 claim 时，两种颜色往返显示，
不存在隐藏的优先级；claim 移除后必须准确恢复仍有效的单方颜色或原始状态。

## 维护入口

- 目标模型：`mods/bepinex/src/Save/RuntimeUiTargetSet.cs`
- 总协调器：`mods/bepinex/src/Save/RuntimeUiPinningService.cs`
- 列表刷新：`mods/bepinex/src/Save/RuntimeUiListSurfaceRefresh.cs`
- 列表高亮：`mods/bepinex/src/Save/RuntimePinnedListHighlightService.cs`
- 加料事务：`mods/bepinex/src/Save/RuntimeTargetRecipeVariantRuntime.cs`
- 厨具高亮：`mods/bepinex/src/Save/RuntimeCookerHighlightService.cs`
- 座位高亮：`mods/bepinex/src/Save/RuntimeSeatHighlightService.cs`
- HUD 订单高亮：`mods/bepinex/src/Save/RuntimeOrderHighlightService.cs`
- 投掷送餐高亮：`mods/bepinex/src/Save/RuntimeThrowDeliverOrderHighlightService.cs`
- 目标解析：`mods/bepinex/src/Save/RuntimeUiTargetOrderResolver.cs`
- Harmony/IL2CPP 约束： [IL2CPP 分析流程](il2cpp-analysis-workflow.md)

## 验证

前端目标发布：

```bash
corepack pnpm audit:ui-pinning
```

后端各表面：

```bash
dotnet run --project tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj -c Release
dotnet run --project tests/runtime-seat-highlight/RuntimeSeatHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-order-highlight/RuntimeOrderHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-throw-delivery-order-highlight/RuntimeThrowDeliverOrderHighlightSmoke.csproj -c Release
```

加料事务修改后必须强制重建再运行，避免增量时间戳造成假绿：

```bash
dotnet build tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release -t:Rebuild
dotnet run --project tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release --no-build
```

需要锁定 .NET 6 + Harmony 的组合验证时运行 `corepack pnpm test:dotnet6-harmony`。完整验证分层见
[验证指南](validation-guide.md)。
