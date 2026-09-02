# 游戏界面辅助

更新日期：2026-09-02

本文说明 Mod 如何把伴随窗口选出的普客与稀客目标显示到游戏原生 UI。目标如何选出见
[推荐引擎](recommendation-engine.md)，订单标识和生命周期见
[订单捕获与生命周期](runtime-order-lifecycle.md)，调度名单内稀客的执行许可见
[稀客调度与订单队列](rare-order-participation.md)。

## 职责边界

游戏界面辅助只增强已有的料理、酒水、厨具、座位和订单界面，不创建独立游戏菜单，也不替代游戏的原生
选择、提交或评价逻辑。当前功能包括：

- 把目标食材、料理和酒水排到原生列表前部并着色。
- 为目标料理提供基于推荐加料的额外料理行。
- 高亮对应厨具和座位。
- 高亮经营 HUD 与投掷送餐面板中的对应订单。

所有功能默认受实验性功能设置控制。任一订单标识、页面绑定或订单实例无法精确确认时，都必须保持游戏原有
界面不变并拒绝应用辅助效果。

## 目标发布模型

前端通过 `apps/companion/src/companion/domain/game-ui-targets.ts` 构造目标，
`apps/companion/src/companion/hooks/useGameUiTargetPublisher.ts` 把完整目标集合一次发布到
`POST /ui-pinning/targets`。

一个目标集合最多包含一个稀客目标和一个普客目标，并按稀客、普客的稳定顺序发布。每个目标必须携带：

- `kind`、目标色和内容修订。
- 订单跟踪标识、实例序号、桌位、规范 `guestId`（稀客为非负 ID，普客固定为 `-1`），以及普客的原生订单键。
- 料理、基础食材、有序加料、酒水和厨具类型。
- 五个目标级功能位：列表置顶、加料料理、厨具高亮、座位高亮、订单高亮。

功能位属于具体目标，不能恢复集合级总开关。加料料理依赖同一目标的列表置顶；全部功能均关闭的目标无效。
后端 `RuntimeUiTargetSet` 只保存不可变托管值，可由 API 线程更新；任何 Unity wrapper、指针或场景对象只允许
在 Unity 主线程解析和使用。

稀客调度模块关闭或生效名单为空时，稀客目标保持原有选择逻辑。生效名单非空时，前端只从 Mod 当前稀客队列中选择，
后端还会在发布临界区持有精确的准入许可；订单标识缺失、状态未同步、已经暂停或过期的稀客目标
全部拒绝，普客目标不受其影响。

`enable-front` 需要越过当前稀客 UI 目标时，服务端只读取 `RuntimeUiPinningService` 保存的不可变目标标识，
并复核经营轮次、R 类追踪编号、订单实例序号和规范 `guestId`。有效目标作为队列插入位置的一个基准；
没有稀客目标是合法状态，存在但已过期、暂停或标识不完整则整次变更冲突。客户端不会根据当前页面
显示位置猜测插入点，也不会通过优先启用替换或清空正在显示的目标。活动料理任务提供的其他插入位置基准见
[自动化运行时](automation-runtime.md)，最终插入规则见[稀客调度与订单队列](rare-order-participation.md)。

主设备或生效配置改变时，后端先应用新名单，再发布一次内容为空的执行目标，以隔离旧配置留下的目标。
仍打开页面使用的展示目标会按新的参与状态过滤：移除已暂停稀客的料理、材料和酒水目标，保留独立的普客目标。
过滤结果与空目标请求使用不同的单调轮次；下一次 Unity 主线程 Tick 只应用一次过滤结果，清除已打开页面中的旧稀客置顶和高亮。
这个边界不销毁页面登记，也不结束、退款或重复执行已经发生的加料事务。只有经营进入 `Closing`、`Destroyed`，
或控制器关闭时，才结束并清理相关状态。

## 页面登记与窄刷新

料理页只 Hook `WorkSceneCookingSelectionPannel.OnPanelOpen` 和 `OnPanelClose`。已登记页面在每次主线程
每次更新先验证 wrapper 指针，再按同一发布许可和目标范围执行：

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

这是“目标变化时刷新已打开列表”的唯一局部刷新路径。它重建列表数据和列表行，但不重建已选材料区或输出
区域。任一步失败都不能标记为已应用，也不能在同一目标轮次盲目重复执行。

`WorkSceneCookingSelectionPannel.OnPanelDestroyed` 与另一个面板共享空 IL2CPP 原生别名，禁止安装该
Hook。料理页生命周期由 open/close 登记和每帧指针验证处理。酒水仓库页的 Destroy 方法有独立非空原生实现，
保留其精确 Hook。

## 加料料理事务

加料行只在目标级 `RecipeVariantEnabled` 开启且推荐包含加料时产生：

- 相同基础料理和相同有序加料合并显示请求；不同加料组合保持独立行。
- 原始基础料理行始终保留。
- 当前厨具内每个方案必须唯一匹配游戏中的 `Recipe`；无匹配表示该厨具不适用，多重匹配则整项拒绝处理。
- 所需的精确 Hook 必须全部安装成功后才允许向游戏列表插入条目。
- 合成配方、游戏 `Recipe` 指针、来源标识、页面轮次、目标轮次和发布许可共同组成事务标识。

选择加料行后，额外扣料、游戏列表写入、输出回调和换菜过程采用显式状态机与确认记录。游戏调用已
发生但结果无法确认时，事务进入 `Uncertain`，本场不重复执行，也不猜测退款。切换基础料理、普通行或另一加料行
只能由真实提交建立切换尝试；只有游戏原有流程正常完成且确认记录精确一致，才能结束旧事务。

加料事务的细粒度状态、回调捕获对象管理和嵌套 `UpdateAllVisual` 约束由
`tests/runtime-target-recipe-variant/` 锁定。维护实现时应以专项测试为完整契约，本文不复制逐条断言。

## 高亮资源所有权

各高亮服务只修改或销毁自身创建、且能再次精确证明所有权的资源：

- 列表行：保留游戏原生回调和 `interactable`，只管理 Mod 自己施加的颜色状态。
- 厨具：精确绑定控制器、渲染器和原始颜色。
- 座位：从精确图块/精灵几何创建独占的填充、纹理、精灵和材质资源。
- HUD 订单：只在精确订单标识与对象池成员关系匹配时挂接自有图像。
- 投掷送餐：只在精确按钮、监听器、背景层和选择结构全部验证后插入自有填充。

禁止用对象名称、显示文本、层级路径、数组下标、近似几何、场景扫描或备用视觉方案。禁止修改游戏原有背景颜色、
焦点、监听器或回调来“兼容”未知结构。

默认目标色为稀客 `#FFDB2E`、普客 `#5FACD3`。同一物理对象同时被两类目标标记时，两种颜色往返显示，
不存在隐藏的优先级；一方标记移除后必须准确恢复仍有效的另一方颜色或原始状态。

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
corepack pnpm audit:rare-order-participation
```

后端各表面：

```bash
dotnet run --project tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj -c Release
dotnet run --project tests/runtime-seat-highlight/RuntimeSeatHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-order-highlight/RuntimeOrderHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-throw-delivery-order-highlight/RuntimeThrowDeliverOrderHighlightSmoke.csproj -c Release
```

加料事务修改后必须强制重建再运行，避免增量时间戳导致测试误报通过：

```bash
dotnet build tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release -t:Rebuild
dotnet run --project tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release --no-build
```

需要锁定 .NET 6 + Harmony 的组合验证时运行 `corepack pnpm test:dotnet6`。完整验证分层见
[验证指南](validation-guide.md)。
