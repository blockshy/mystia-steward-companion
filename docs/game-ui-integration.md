# 游戏界面辅助

更新日期：2026-11-06

本文说明 Mod 如何把 C# 业务层选出的普客与稀客目标投影到游戏原生 UI。目标如何选出见
[推荐引擎](recommendation-engine.md)，订单身份和生命周期见
[订单运行时生命周期](runtime-order-lifecycle.md)。

## 职责边界

独立的[游戏内专注显示](game-focus-overlay.md)由经营专注页配置，属于只读订单显示层，不复用本章的目标高亮开关、自动化许可或原生背景染色流程。

游戏界面辅助只增强已有的料理、酒水、厨具、座位和订单界面，不创建独立游戏菜单，也不替代游戏的原生
选择、提交或评价逻辑。当前功能包括：

- 把目标食材、料理和酒水按点单先后排到原生列表前部并着色；食材保留原分类列，在各类内部排序。
- 为目标料理提供基于推荐加料的额外料理行。
- 高亮对应厨具和座位。
- 高亮经营 HUD 与投掷送餐面板中的对应订单。

功能由“扩展 → 游戏界面辅助”的共享总开关 `gameUiEnabled`、分类总开关 `normalGameUiEnabled` / `rareGameUiEnabled` 与各类分项共同控制，独立于自动化模块总开关。关闭一类会撤销其全部游戏目标，包括列表置顶、配方变体和各处高亮，不改变另一类；关闭整个辅助模块则撤销两类。分项与颜色配置均保留，关闭期间仍可编辑，重启沿用。后台仍要求当前主设备在线、共享配置已确认和经营场景允许辅助。任一运行时身份、页面绑定或生命周期无法精确证明时，都必须保持原生界面不变并 fail closed。

## 目标发布模型

`modules/companion-business/Domain/GameUi/GameUiTargetService.cs` 使用权威快照和同一订单主方案构造目标，
`mods/bepinex/src/LocalApi/LocalApiServer.Business.cs` 在后台业务结果仍为当前版本时原子发布。
客户端只编辑共享功能开关、颜色等设置，并通过 `/business/status` 的 `gameUiTargets` 查看投影；
旧的 `POST /ui-pinning/targets` 客户端入口拒绝写入。业务线程只处理托管值，Unity 绑定仍由主线程处理。

目标发布只允许快照声明 `Active`，且实时托管生命周期仍为同一经营代次的 `Active`。白天、`Inactive`、
`Closing`、`Destroyed` 或代次不匹配时，宿主返回空 UI 槽位并继续只读推荐，不调用夜间目标发布接口。
空目标也是一次发布，不能用 `UpdateTargets(empty)` 绕过生命周期要求；旧目标由既有经营边界负责清理，
后台旧帧不得另行撤销下一场经营的目标。自动化准入及排队后许可同样复核实时经营状态。

`RuntimeUiTargetSessionGuard` 保存原有的严格发布校验，生产服务与离线宿主共用该判断。计算后发生切场竞争时，
发布仍会被拒绝并取消本轮未提交动作；下一轮按非经营状态继续只读推荐。仍处于同代 `Active` 的真实异常必须报告，
不能通过吞掉错误或放宽目标校验恢复页面。

当前业务最多辅助六笔订单，两类各以配色数量为辅助上限（默认2、最多3），一色一单，按稳定颜色槽位发布。底层目标容器仍保留八个声明位，避免数量收缩时重新编号已有订单。颜色由唯一后台分配器按精确身份保持，未知读取与重复身份不释放已知槽位；完整候选集合中的未入选订单释放名额，已送齐及暂停订单不占色。每个目标必须携带：

- `kind`、`colorSlot`（0–7）、目标色和内容修订。
- 订单 trace、lifecycle、桌位，以及普客的原生 order key。
- 料理、基础食材、有序加料、酒水和厨具类型。
- 五个目标级功能位：列表置顶、加料料理、厨具高亮、座位高亮、订单高亮。

功能位属于具体目标，不能恢复集合级总开关。加料料理依赖同一目标的列表置顶；全部功能均关闭的 target 无效。
后端 `RuntimeUiTargetSet` 只保存不可变托管标量，可由 API 线程更新；任何 Unity wrapper、指针或场景对象只允许
在 Unity 主线程解析和使用。

设备主权威或生效 profile 改变时，后端先推进一个空目标的 authority fence，再由下一次主线程 Tick 接受新
目标。这个边界不会销毁仍打开的页面登记，也不会猜测中止已经发生的加料事务。只有经营进入 Closing、
Destroyed，或控制器 shutdown，才进行终态退休。

主线程公共消费入口在目标代次或订单 capture 版本变化时立即验证当前来源，未变化时每250毫秒兜底。
验证沿用 HUD 的精确 trace/key/lifecycle/desk 与订单/控制器原生指针，不按客人名称或桌号猜测。
来源结束、歧义或指针变化时仅撤回该笔精确目标，通过同一 target set 发布同步更新厨具、座位、HUD与抛送，
保留其它仍有效的目标；撤销不等待后台方案完成。同桌新单只能由新的当前业务发布获得高亮。

抛送面板的私有填充退休时，先复核 owner/image/rect 三个指针，再立即移出原按钮层级并调用 Destroy。
Unity 帧尾销毁前的同帧重开因此不会把私有克隆误算成第三个原生背景。两原生背景的严格绑定条件不变，
不能通过忽略任意 inactive Image 或按名字过滤来掩盖层级异常。

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

## 列表置顶的订单顺序

原生 `CheckPinned` 只返回布尔值，不能表达多笔订单之间的先后。现有目标置顶仍限定于刷新作用域；`RuntimeUiListOrderService` 在原生数据生成后、控件绑定前，对显示列表做稳定置换：当前目标按其最早有效 `OrderLifecycleSequence` 排列，非目标和同优先级条目保留原生相对次序。只调整已有条目，不添加库存记录，不改变数量、菜单过滤、配方内容或选择逻辑。

- 酒水：`UpdateBevField`（RVA `0x5901D0`）结束后重排精确 `m_Beverages`。
- 食材：`ClassifyIngredientByType`（RVA `0x620720`）结束后，要求来源为 `InStorageOnly`，且四个参数列表分别等于当前料理面板的四个分类字段。`UpdateAllVisual`（`0x589FB0`）内联调用该分类函数，不能只监听 `UpdateIngField`。`OnPanelInitialize`（`0x5876E0`）以五列定义海鲜0、肉类1、蔬菜2、其他3/4，因此排序限定于各分类，不把食材移到错误分类。
- 料理：`UpdateRecipeField`（`0x58A570`）的排序postfix显式在加料服务之后执行。加料行先按原有插入顺序完整验收，再以已确认的原生配方指针读取对应声明，进行跨基础行与加料行排序；不能仅用相同料理ID推断加料方案身份。完整性、目标、页面代次或列表指针不匹配时拒绝排序。

所有读写都在Unity主线程、同一经营代次的发布租约和精确面板作用域中执行；先有界读取并校验完整列表，再计算稳定排序，写前后核对原生身份、数量及对象。只通过索引替换完成置换；异常时仅在当前列表仍属于该快照时恢复原序，避免覆盖外部变化。空目标及关闭列表辅助不执行额外列表读取；不变顺序不写入。诊断通过 `listOrder` 输出并限制警告次数。

**原生值类型写入边界：** 食材及酒水列表项是 `KeyValuePair<实体,int>`，在 #783 interop 中却表示为继承 `Il2CppSystem.ValueType` 的托管类，不能用 CLR 的 `IsValueType` 判断原生参数形态。该版本 `List<T>.Item` 泛型 setter 会把盒对象地址当作结构体内容传给原生方法，损坏实体指针及数量；写后校验无法撤销已经发生的原生内存损坏。两类列表必须在任何写入前解析并验证精确的非泛型 `IList.Item(int, Il2CppSystem.Object)` setter，由原生实现执行类型检查和拆箱后再赋值。缺少该入口即停止排序，禁止回退到泛型 setter。料理列表保存引用类型 `Recipe`，继续使用经过签名校验的引用型 setter。回归夹具必须保留“托管类包装原生值类型”的差异，不能再用 CLR `record struct` 代替此边界。

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

各高亮服务只作用于已确认身份的显示表面；游戏共享资产不改写，自建资源只由创建者释放：

- 列表行：在已登记背景的 `Image.OnPopulateMesh(VertexHelper)` 回调内从左到右分区染色原网格；不创建覆盖矩形，不修改 Image 颜色属性、Sprite、材质或输入行为，物品图标不参与分色。
- 厨具：从精确 CookDesk 地块读取原背景精灵，复用原生选区变换生成自有分色网格，并验证背景与锅体之间的绘制顺序；不替换交互 Tile，不改写原厨具、蒸汽及成品精灵。
- 座位：从精确 tile/sprite 几何创建独占的 fill、texture、sprite 和 material 资源。
- HUD 订单：只在精确订单 identity 与 pool membership 匹配时挂接自有 Image。私有裁剪容器上沿以精确 `OrderingElement.text` 桌号矩形的下沿为界，仅显示正文；原背景、桌号及交互区域不修改。背景是九宫格 Image，必须保持克隆图片原尺寸和位置，由 RectMask2D 裁掉顶部，不能缩小图片把桌号造型重新压入正文。背景与桌号必须属于已确认的同父级轴对齐模板，250ms健康检查同步布局；游戏以零缩放隐藏当前边框时仍读取其逻辑矩形，不能以退化世界矩阵计算。无法确认边界时撤回覆盖层。
- 投掷送餐：只在 exact button、listener、背景层和 selection 结构全部验证后插入自有 fill。

禁止用对象名称、显示文本、层级路径、数组下标、近似几何、场景扫描或替代视觉兜底。禁止修改原生背景颜色、
焦点、listener 或 callback 来“兼容”未知结构。

普客、稀客分别使用独立调色板，各1–3种颜色、默认2种，一种颜色只辅助该类一笔订单；默认普客冷色、稀客暖色。每笔订单独占全局八位声明集合中的一个槽位，跨类别允许同色；全局槽位用于稳定身份，每类可配置上限仍为三。厨具摆放区域与料理、食材、酒水列表均沿原背景形状同时显示分区颜色。座位与HUD订单正文采用固定透明度，取消明暗呼吸；同一表面有多笔声明时仍保留颜色轮换，HUD桌号保留游戏原样。单笔声明移除后保留其它声明，同色也不互相覆盖，最后一个声明结束才撤回高亮。迁移与分配规则见[多订单高亮](multi-order-highlights.md)。

空间分色的显示次序独立于声明槽：普客和稀客的生命周期均由 `RuntimeOrderTerminalReceiptStore.BeginLifecycle` 的同一递增计数器生成，发布内按 `OrderLifecycleSequence` 排序，并在调色板中缓存槽位访问顺序。厨具与列表共用同一取色方法，按各资源的声明掩码筛选后合并同色。新订单复用旧槽时排到较早订单之后；目标集合仍按槽位规范化，颜色分配、执行优先级和原生身份不变。排序只在不可变发布构造时执行，稳定帧不排序或分配集合。

### 厨具摆放区域背景

厨具只从 `CookSystemManager` 的精确目录解析。先读锁定坐标，再读目录；锁定项在任何实例状态和视觉属性访问之前跳过。每个位置还须由 `TileManager.GetInteractableType` 确认为 CookDesk，并有唯一的原背景精灵和原生选区模板。目录不完整、不可用状态未被锁表解释或身份歧义时，停止对应显示，不猜测替代厨具。

当前 metadata、#783 interop 与 Native `TileManager.UpdateCurser`（RVA `0x52E1D0`）共同确认位置、地块精灵和 `onSelection` 的关系。背景使用本格 Tile.sprite 的顶点、三角形与 UV，位置及模板变换沿原生选区计算；不读取模板随玩家选择而临时替换的厨具图标。实际轮廓决定分区边界，不再按格距或截图比例推算小矩形。

厨具颜色沿背景宽度从左到右均分，在原三角形内裁剪，边界顶点分离以避免渐变。每色从顶部贯穿到底部，使锅体挡住上方后仍能从外露桌沿辨认；料理、食材和酒水列表采用相同的左右排列。厨具背景左右外缘各让出4%宽度染为暖暗色，形成相邻桌格的细分隔线；原桌面约50像素宽、格距48像素，两侧暗边在接缝处重合为约2像素。分隔只占用原轮廓内部，不添加独立线条对象，保留原 alpha、UV 及颜色槽位的内部边界；列表不加此桌格分隔线。自有 MeshRenderer 使用标准精灵材质，覆盖不透明度为220/255，不继承选区闪烁特效，也不增加碰撞或输入组件。这是贴合原摆放选区的视觉层，不是修改原 Tilemap：原生随后会用 `tile.sprite` 查询高亮映射，替换 Tile.sprite 会破坏该索引，故交互资源保持原样。

原桌面为棕色压缩贴图，直接乘色会压暗蓝色等选色。首次绑定从实际 UV 包围盒生成白色 RGB、原 alpha 的自有小纹理，并重映射原 UV；透明孔与轮廓保持。采用与游戏图片提取相同的显式 Sprites/Default 材质、白色调制、GL 顶点和浮点离屏目标，直接在不超过256×256的小区域绘制实际裁剪 UV，再读回 alpha；不再依赖整图隐式 Blit 后的像素矩形读取。同源同裁剪区域由多台厨具共享租约，颜色和数量变化不再次读回；上限16份，最后一个使用者释放。所有路径恢复 GL 矩阵、RenderTexture.active 和 GL.sRGBWrite，原图集不改写或销毁。

增强遮罩失败时，背景继续使用原纹理与原 UV，仍保留左右分区和220/255不透明度；不会因一次全透明读回撤销全部厨具。失败来源按当前经营会话、图集身份与裁剪区域记忆，当前会话内不重复读回；失败记录达到上限后不再尝试新来源，下一会话可重新尝试。非法身份或几何仍只拒绝该锅，不猜测替代资源。诊断区分 `white-alpha` 与 `source-fallback` 并记录失败阶段、裁剪范围；单锅资源或网格错误只释放该项，其余已确认的厨具继续显示。

`Work.unity` 的确切模板层级为 CookAnimator 根 → 含 SortingGroup 的布局节点 → `m_CookerSpriteRenderer`。`LayerSortingController.GroupSortingMethod`（RVA `0x3E8250`）按世界 Y 每帧更新整组排序；锅体 renderer 的 order 是组内值，不能拿来与组外 Tilemap 比较。背景作为精确锅体 renderer 的子节点，使用同层、组内 order 减一，并保留单位世界变换与桌格位置，因此继承原排序组且位于锅体后方。网格锚点验证仍使用 CookAnimator 根，避免图标自带的 `(-0.5,-0.5)` 偏移影响桌格定位；源 renderer 更换时撤销旧绑定。低频状态记录源、父级、排序组、网格范围和纹理尺寸，不能仅凭“active”认定实际可见。

目标变化更新分区网格，普通帧仅做存活检查；目录按既有低频间隔复核。相同来源和颜色不重复构造网格。拆卸、尤魔锁定、关闭辅助、切场均撤回自有显示；只销毁自建根、网格与材质并归还遮罩租约，不销毁借用的原精灵和纹理。后台线程只发布托管快照，Unity 创建和释放仍由主线程执行。

隔离测试可以证明上述几何属性、对象复用、资源释放和声明合并行为；无法证明真实游戏的 GPU 合成、相机缩放或其他图层的最终遮挡效果。正式引用编译也不代替真实游戏视觉验收。

### 制作与酒水列表背景色带

三个`On*ElementEnabled`回调使用当前料理或`data.Key`的`Text.Visual`设置物品图标。高亮只在该图片原生指针能唯一匹配cluster已声明成员、且图标为按钮背景的独立后代时绑定；不按对象名、子节点索引或全场景搜索猜图标。图标、数量和文字的层级不变，分色只作用于已登记的背景 Image。

`Image.OnPopulateMesh(VertexHelper)`（唯一 RVA `0x2CF3110`、虚表槽44）完成原生 Simple/Sliced/Tiled/Filled 网格后，沿背景宽度从左到右裁剪等宽分区并调制顶点颜色，每色贯穿原背景高度。位置、四组 UV、法线、切线及 alpha 均保留或按边界插值；不修改 Sprite、材质或 Image.color。`Graphic.DoMeshGeneration`（`0x2CEB0B0`）随后仍执行原生 IMeshModifier、遮罩和 CanvasRenderer 提交，因此九宫格、透明轮廓、滚动与原生层级继续生效。

共享 VertexHelper 只在当前同步回调使用，不能跨帧保存；只保存有界的纯值顶点用于失败恢复。颜色变化才标记原背景网格重建，普通 Tick 不执行裁剪。同色只合并显示，声明逐笔校验；写入期间目标改变则在提交前恢复本次原网格。关闭、池重绑或身份失效后撤销登记并请求原生重建，不回写旧 Image 属性。未知结构记录限频诊断；隔离测试不替代实际 GPU 与游戏模板验证。

酒水置顶查询完整目标集合。Native `UpdateBevField`（RVA `0x5901D0`）按`CheckPinned(2,id)`区分目标和非目标，`_UpdateBevField_b__49_2`（`0x5F2610`）逐项传入真实酒水ID；生产前缀匹配全部`GetBeverageClaims`，不只取第一单。原生数据生成后再由上述列表排序服务按订单先后重排目标。相同酒水只有一个库存条目并合并颜色，已送达酒水以`-1`撤回；只有一种待取酒水时只置顶一种属于预期。底层容器回归覆盖八个声明槽、同种合并、逐笔撤回与作用域结束后恢复原生收藏判断；此测试容量不改变产品每类三单、合计六单的上限。

## 维护入口

- 目标模型：`mods/bepinex/src/Save/RuntimeUiTargetSet.cs`
- 发布生命周期校验：`mods/bepinex/src/Save/RuntimeUiTargetSessionGuard.cs`
- 总协调器：`mods/bepinex/src/Save/RuntimeUiPinningService.cs`
- 列表刷新：`mods/bepinex/src/Save/RuntimeUiListSurfaceRefresh.cs`
- 列表高亮：`mods/bepinex/src/Save/RuntimePinnedListHighlightService.cs`
- 列表原背景分色租约：`mods/bepinex/src/Save/RuntimePinnedListColorBlocks.cs`
- 原生网格边界与分带算法：`mods/bepinex/src/Save/RuntimePinnedListMeshAccess.cs`、`RuntimeBackgroundBandMesh.cs`
- 列表运行时UI访问：`mods/bepinex/src/Save/RuntimePinnedListUiAccess.cs`（绑定期解析已证实UI类型并缓存委托，不增加锁定静态引用）
- 加料事务：`mods/bepinex/src/Save/RuntimeTargetRecipeVariantRuntime.cs`
- 厨具高亮：`mods/bepinex/src/Save/RuntimeCookerHighlightService.cs`
- 厨具原生摆放区绑定、分色网格与透明遮罩：`mods/bepinex/src/Save/RuntimeCookerBackgroundSurface.cs`、`RuntimeCookerColorBlocks.cs`、`RuntimeCookerAlphaMask.cs`
- 座位高亮：`mods/bepinex/src/Save/RuntimeSeatHighlightService.cs`
- HUD 订单高亮：`mods/bepinex/src/Save/RuntimeOrderHighlightService.cs`
- 投掷送餐高亮：`mods/bepinex/src/Save/RuntimeThrowDeliverOrderHighlightService.cs`
- 目标解析：`mods/bepinex/src/Save/RuntimeUiTargetOrderResolver.cs`
- Harmony/IL2CPP 约束： [IL2CPP 分析流程](il2cpp-analysis-workflow.md)

## 验证

客户端边界审计和真实 C# 离线目标投影：

```bash
corepack pnpm audit:business
```

此项验证不启动真实游戏，也不需要浏览器或客户端预览。它覆盖前端不再发布目标、稀客/普客开关独立生效、颜色变化不更换业务身份；
游戏原生 UI、IL2CPP wrapper 与实际渲染行为仍需独立实机证据。

白天推荐与日夜切换另由 `tests/csharp-business-host` 的 `--daytime-ui-lifecycle` 模式验证，已纳入
`corepack pnpm audit:business`。客户端界面与实际 HTTP 组合分别由 `apps/companion-flutter/test/workbench_test.dart`
及 `apps/companion-flutter/test/live_http_contract_test.dart` 验证；后者须先构建真实 .NET 6 隔离宿主。
这些组件、模拟生命周期和 HTTP 证据均不等同于游戏原生 UI 实机验收。

后端各表面：

```bash
node scripts/run-dotnet6-harmony-smoke.mjs ui-pinning-runtime
dotnet run --project tests/runtime-list-color-blocks/RuntimeListColorBlocksSmoke.csproj -c Release
dotnet run --project tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj -c Release -- --color-blocks-only
dotnet run --project tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj -c Release -- --beverage-targets
dotnet run --project tests/runtime-cooker-color-blocks/RuntimeCookerColorBlocksSmoke.csproj -c Release
dotnet run --project tests/runtime-cooker-snapshot/RuntimeCookerSnapshotSmoke.csproj -c Release
dotnet run --project tests/runtime-seat-highlight/RuntimeSeatHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-order-highlight/RuntimeOrderHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-throw-delivery-order-highlight/RuntimeThrowDeliverOrderHighlightSmoke.csproj -c Release
```

加料事务修改后通过锁定容器入口强制重建再运行，避免增量时间戳造成假绿：

```bash
node scripts/run-dotnet6-harmony-smoke.mjs runtime-target-recipe-variant
```

需要锁定 .NET 6 + Harmony 的组合验证时运行 `corepack pnpm test:dotnet6-harmony`。完整验证分层见
[验证指南](validation-guide.md)。
