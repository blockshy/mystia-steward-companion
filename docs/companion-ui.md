# 伴随窗口界面

更新日期：2026-09-21

本文说明伴随窗口的页面职责、交互边界与维护入口。整体进程、数据流和平台关系见
[架构说明](architecture.md)；本地 API 的认证、主设备与共享配置约束见
[本地 API](local-api.md)。

## 职责边界

伴随窗口负责把 Mod 发布的只读游戏状态、推荐结果和可用操作组织成界面。它不直接读取
Unity 对象，也不在浏览器线程中执行游戏动作。所有游戏写操作都必须经过本地 API 和 Mod 的
Unity 主线程队列。

以下专题由独立文档维护，本文不重复其业务规则：

- 推荐候选、主执行方案和收藏/自定义料理：[推荐引擎](recommendation-engine.md)
- 游戏内列表置顶、加料行和高亮：[游戏界面辅助](game-ui-integration.md)
- 任务列表的数据来源和状态：[任务系统](missions.md)
- 版本检测、下载和独立更新程序：[更新系统](update-system.md)
- 日志、控制台和诊断包：[可观测性](observability.md)
- 自动化命令和暂停/恢复语义：[自动化运行时](automation-runtime.md)
- 稀客调度名单、订单队列和写入前状态检查（CAS）：[稀客调度与订单队列](rare-order-participation.md)

## 页面结构

当前一级页签由 `apps/companion/src/companion/ModWorkbench.tsx` 统一编排：

| 一级页签 | 内容边界 |
| --- | --- |
| 概览 | 客户端、主机网络、设备共享、运行状态；客户端地址和 Token、主机监听配置、设备配置各自集中管理 |
| 推荐 | 普客、稀客、自定义、收藏、推荐规则 |
| 经营 | 稀客、普客、稀客调度、游戏辅助，以及开启调试信息后的诊断；顶部保留默认收起的经营概况和只读自动化摘要 |
| 自动化 | 运行状态、执行配置；运行页负责稀客与普客诊断、人工确认和重试，配置页负责总控、阶段、并发和重试规则 |
| 工具 | 任务列表、稀客邀请、库存；低库存提醒与库存修改同页 |
| 设置 | 外观窗口、输入、更新、帮助，以及开启调试信息后的日志；输入页统一手柄导航和快捷键说明 |

较重的页面只在对应页签真正激活时挂载。新增页面时应继续遵守这一点，避免隐藏页面保留轮询、
Worker、计时器或全局输入作用域。已发送的设置、库存和自定义料理写请求由工作台常驻控制器管理，不随页面卸载丢失操作占用。自动化的状态、控制权、运行事件和双调度控制器同样常驻，页面只消费状态并提供操作入口，职责见[自动化运行时](automation-runtime.md#客户端控制器与推荐边界)。

经营页顶部“经营概况”是页面内临时展开状态：首次进入时默认收起，展开后显示经营场景、推荐数据、自动化、
特殊经营、已摆放厨具和目标厨具六项；离开经营页导致页面卸载，再次进入时恢复默认收起，不写入设备偏好或
共享配置。

`经营 -> 稀客调度` 统一模块开关、名单与订单队列。页签和开关入口常驻；模块关闭时不挂载名单与队列内容，开启后同页显示。
入口可见不等于当前设备可写；当前设备不是主设备、连接或共享配置尚未就绪、订单集合不完整，或队列状态与订单不一致时仍保持只读。

工作台不保留全局连接表头。API 地址、Token、连接启停、刷新和连接/运行态/经营摘要集中在
`概览 -> 客户端`；其他一级页面只在连接未确认时提供状态与返回客户端页入口。草稿应用与连接启停独立，恢复只使用已应用身份。
桌面穿透提示由壳返回的实际状态生成；只有热键注册可用时才提示 F10，失败时展示其他恢复入口。

## 组合与状态所有权

`ModWorkbench.tsx` 是页面组合根，不应继续吸收可独立测试的业务算法。常见职责按以下位置划分：

- `apps/companion/src/companion/pages/`：页面和展示组件。
- `apps/companion/src/companion/hooks/`：带生命周期的读取、发布和轮询。
- `apps/companion/src/companion/domain/`：纯业务组合与协议映射。
- `apps/companion/src/companion/features/`：更新等边界清晰的功能模块。
- `apps/companion/src/companion/workers/`：高成本计算的 Worker 协议与调度。
- `apps/companion/src/components/ui/`：项目统一的基础控件封装。
- `apps/companion/src/companion/preferences.ts`：当前设备的界面和功能偏好归一化。
- `apps/companion/src/companion/storage.ts`：不属于共享配置的本地页面状态。

跨设备生效的推荐、稀客调度模块开关与调度名单、自动化和游戏界面辅助配置统一由“主设备”管理；纯显示偏好仍属于当前窗口。
扩展模块统一使用 `domain/extension-module-control.ts` 汇总配置作用域和控制状态，并由
`ModuleControlPanel.tsx` 展示“当前设备”或“主设备共享”。任务列表、稀客邀请只控制当前窗口的读取或动作入口，
断线时可以预设；稀客调度会改变共享运行配置，只有连接并确认当前设备为主设备后才可写。共享偏好写命令必须在
共享配置 Hook 的唯一 `stagePrimaryProfile` 边界提交。该边界固定设备注册表、当前设备、主设备、`authorityRevision` 与
配置的修订号和哈希基准；防抖等待和已发送 POST 期间的连续输入只更新完整草稿，前一笔确认后才按新的确认版本串行提交下一笔。
轮询、刷新和 POST 响应使用同一事务状态合并规则：旧版本读数不得覆盖草稿，只有下一修订号精确匹配且完整配置相符才确认；
连接轮次或已确认配置版本发生变化时明确撤销草稿，不做隐式变基。未确认草稿只用于设置页展示，不进入游戏配置或本地生效缓存；
已经发出的设备配置写请求由 Hook 记录为正在处理，切换连接轮次后必须等待旧请求响应或客户端超时再重新注册；
服务端写入前状态检查继续拒绝基于同一旧版本的竞争写入，后续读取负责把超时后的显示状态同步到当前生效配置。
设备变更请求通过同步取得的操作令牌串行执行；待确认同步按连接轮次、同步 ID、配置修订号和哈希限制为至多一个正在处理的请求，在应用与确认
期间不开放游戏写操作。以上边界不建立离线待同步或最后写入覆盖路径。
页面不得把浏览器内的临时状态当成游戏运行时已经接受的状态。远端结果需要结合连接修订号、请求轮次或
内容签名拒绝迟到响应。

页面推荐以连接修订、目录签名、选择和请求上下文判定结果归属；切换选择立即撤下旧行。同一选择的旧结果可保留阅读，计算未完成或失败时不能发起收藏写入。
收藏控制器统一发布读取/写入能力与原因；写入响应失败后撤销集合确认，只通过 GET 核验真实收藏，确认前禁写，不自动重发修改。库存控制器用一个同步操作令牌覆盖写入及刷新确认；不明结果必须显式重读后才能再写，不自动重发，确认读取不会撤销用户暂停连接的选择。
连接设置控制器绑定连接身份与修订，旧请求不能把当前窗口切回旧地址或 Token。当前连接的 Token 重置成功响应会保存新凭据，同时保留用户此时的暂停状态。日志数字输入先保留草稿，明确保存后提交。

模块开启后显示的经营页稀客队列必须同时提供稀客级和单订单级操作：前者回传该稀客当前的完整订单实例集合并做全量状态检查，
后者只提交一笔精确订单标识。两种范围都使用 `pause`、`enable-tail`、`enable-front` 三种明确操作；已参与订单不能通过启用按钮重排。队列展示只接受 Mod 发布的连续
`queuePosition`，前端不自行维护序号或推测优先插入位置。

模块开启时，稀客队列保留暂停订单，以便执行启用动作；模块关闭时名单和队列内容不挂载。订单捕获和 Worker
推荐结果不因暂停删除。模块开启且生效名单非空时，“经营 -> 稀客”和稀客订单专注模式只展示
已启用订单，并按 Mod 返回的 `queuePosition` 排列；
暂停订单从这两个展示入口隐藏，启用后按新位置恢复。模块关闭或生效名单为空时，两个入口跳过稀客调度筛选，
沿用原有集合与排序。

## 视觉与响应式约束

界面采用紧凑、扁平、矩形化的项目组件风格。页面优先复用现有 `ListPanel`、设置行、状态条、
徽标、对话框和 Tabs 封装，不在业务页面中建立第二套视觉系统。

浅色使用纸色表面和深棕文字；深色保留近黑底、深棕表面、暖米文字与橙金强调色，对比度调整优先局部增强
辅助文字、状态色和控件轮廓，保持原有背景层次与色调。正文、次要文字、成功、警告、危险、
必要控件边界与焦点色由 `index.css` 的语义变量统一提供，并同步 Mantine 主题桥接。
装饰分隔线与必要输入轮廓使用不同变量；占位文字、只读原因和人工确认提示不能依靠整体透明度弱化。
颜色调整通过 `audit:theme-contrast` 检查实际合成背景，包括默认背景透明度下的黑白底；
用户自行降低透明度叠加任意游戏画面时，仍需现场确认可读性。

历史一级日志导航值只做一次明确迁移至 `设置 -> 日志`；关闭调试信息后回到外观窗口。
一级入口的渲染顺序与手柄切换顺序一致，不能用条件日志改变主要导航顺序。

稳定的响应式规则如下：

- 桌面窗口以 640 px 为最窄受支持宽度；在该宽度及以上，一级和嵌套页签应平铺占满整行。
- 小于 640 px 的移动端视口允许页签横向滚动，但不能压缩为不可辨认的窄按钮。
- 普通 Card 统一负责内边距，CardContent 不重复留白；ListPanel 的外层无内边距，由标题、工具栏和正文各自负责间距。
- 移动端普通推荐采用页面自然滚动，桌面双列与专注模式按各自可用高度滚动。料理、基础材料、加料和错误正文允许换行。
- 经营页的经营概况默认收起；展开后的六项摘要在 640 px 仍保持三列。
- 列表行允许内容换行或内部滚动，不应通过隐藏关键状态来换取固定高度。
- 背景透明度、内容透明度和字体缩放是互相独立的显示设置；字体缩放通过根级 CSS 变量传播。
- 确认对话框必须使用项目提供的实色表面，不能依赖页面背景提供可读性。

解释设置含义时使用统一的帮助字段和浮层提示；当前值、错误和阻塞原因仍需直接可见，不能只放在
提示浮层中。仅供诊断的内部 ID、原始状态和耗时受“显示调试详情”控制。

## 键盘、鼠标与手柄

手柄输入由两层组成：

- `apps/companion/src/companion/gamepad/gamepad-input-engine.ts` 负责标准手柄状态、重复节奏和语义动作。
- `apps/companion/src/companion/gamepad/gamepad-focus-manager.ts` 与
  `use-gamepad-navigation.ts` 负责焦点、页签、列表滚动和对话框焦点范围。

可交互元素应使用既有的 `data-gamepad-*` 契约，并提供稳定、唯一的焦点键。对话框打开后必须限制在
对话框焦点范围；关闭后恢复合理焦点。浏览器点击、键盘操作与手柄动作应共享同一业务处理函数，不能维护
两套结果不同的路径。

帮助目录的鼠标、Enter、Space 和手柄共用选择逻辑；窄屏选中后定位正文并提供返回目录，返回恢复原项焦点。

F8 和 RS Click 的窗口聚焦切换属于 Tauri 桌面能力，不受“手柄导航”开关影响。Android 端只提供页面和
连接能力，不提供置顶、鼠标穿透、托盘或桌面窗口聚焦。

## 维护入口

- 页面组合：`apps/companion/src/companion/ModWorkbench.tsx`
- 概览连接页：`apps/companion/src/companion/pages/overview/OverviewConnectionPanel.tsx`
- 扩展模块控制状态：`apps/companion/src/companion/domain/extension-module-control.ts`
- 主设备配置草稿事务：`apps/companion/src/companion/domain/primary-profile-transaction.ts`
- 扩展模块统一外壳：`apps/companion/src/companion/pages/ModuleControlPanel.tsx`
- 稀客调度模块和名单：`apps/companion/src/companion/pages/ModRareGuestParticipationPanel.tsx`
- 稀客订单队列：`apps/companion/src/companion/pages/service/RareOrderParticipationPanel.tsx`
- 自动化运行页：`apps/companion/src/companion/pages/automation/AutomationRuntimePanel.tsx`
- 分类设置面板：`apps/companion/src/companion/pages/settings/`
- 设置页：`apps/companion/src/companion/pages/ModSettingsPanel.tsx`
- 共享控件：`apps/companion/src/components/ui/`
- 主题与布局：`apps/companion/src/index.css`
- 帮助内容：`apps/companion/src/data/help-content.json`
- 桌面窗口：`apps/companion/src-tauri/src/app.rs`
- 本地开发与模拟服务： [本地开发](local-development.md)
- Android 调试： [Android 开发](android-development.md)

## 验证

基础改动至少运行：

```bash
corepack pnpm lint
corepack pnpm build
corepack pnpm audit:ui
```

按改动范围追加：

```bash
corepack pnpm audit:font-scale
corepack pnpm audit:gamepad
```

涉及 Tauri 窗口行为时运行对应 Cargo 检查与测试；涉及专题页面时，再运行该专题文档列出的专项审计。
完整验证分层见 [验证指南](validation-guide.md)。
