# 伴随窗口界面

更新日期：2026-08-19

本文说明伴随窗口的页面职责、交互边界与维护入口。整体进程、数据流和平台关系见
[架构说明](architecture.md)；本地 API 的认证、设备配置权威与接口约束见
[本地 API](local-api.md)。

## 职责边界

伴随窗口负责把 Mod 发布的只读运行时状态、推荐结果和受控操作组织成可操作界面。它不直接读取
Unity 对象，也不在浏览器线程中执行游戏动作。所有游戏运行时写操作都必须经过本地 API 和 Mod 的
Unity 主线程队列。

以下专题由独立文档维护，本文不重复其业务规则：

- 推荐候选、主执行方案和收藏/自定义料理：[推荐引擎](recommendation-engine.md)
- 游戏内列表置顶、加料行和高亮：[游戏界面辅助](game-ui-integration.md)
- 任务列表的数据来源和状态：[任务系统](missions.md)
- 版本检测、下载和独立更新程序：[更新系统](update-system.md)
- 日志、控制台和诊断包：[可观测性](observability.md)
- 自动化命令和暂停/恢复语义：[自动化运行时](automation-runtime.md)

## 页面结构

当前一级页签由 `apps/companion/src/companion/ModWorkbench.tsx` 统一编排：

| 一级页签 | 内容边界 |
| --- | --- |
| 概览 | 连接、存档、时间、经营状态和基础数据可用性摘要 |
| 推荐料理 | 普客、稀客、自定义推荐料理、收藏管理四个二级页签 |
| 经营中 | 当前订单、推荐与自动化运行状态 |
| 扩展功能 | 任务列表、稀客邀请、修改三个二级页签 |
| 设置 | 窗口、连接、推荐、实验性功能、更新、帮助六个二级页签 |
| 日志 | 仅在“显示调试详情”开启时出现 |

较重的页面只在对应页签真正激活时挂载。新增页面时应继续遵守这一点，避免隐藏页面保留轮询、
Worker、计时器或全局输入作用域。

## 组合与状态所有权

`ModWorkbench.tsx` 是页面组合根，不应继续吸收可独立测试的业务算法。常见职责按以下位置划分：

- `apps/companion/src/companion/pages/`：页面和展示组件。
- `apps/companion/src/companion/hooks/`：带生命周期的读取、发布和轮询。
- `apps/companion/src/companion/domain/`：纯业务组合与协议映射。
- `apps/companion/src/companion/features/`：更新等边界清晰的功能模块。
- `apps/companion/src/companion/workers/`：高成本计算的 Worker 协议与调度。
- `apps/companion/src/components/ui/`：项目统一的基础控件封装。
- `apps/companion/src/companion/preferences.ts`：当前设备的界面和功能偏好归一化。
- `apps/companion/src/companion/storage.ts`：不属于共享 profile 的本地页面状态。

跨设备生效的推荐、自动化和游戏界面辅助配置由“主设备”权威模型管理；纯显示偏好仍属于当前窗口。
页面不得把浏览器内的临时状态当成游戏运行时已经接受的状态。远端结果需要结合连接修订、请求代际或
内容签名拒绝迟到响应。

## 视觉与响应式约束

界面采用紧凑、扁平、矩形化的项目组件风格。页面优先复用现有 `ListPanel`、设置行、状态条、
徽标、对话框和 Tabs 封装，不在业务页面中建立第二套视觉系统。

稳定的响应式规则如下：

- 桌面窗口以 640 px 为最窄受支持宽度；在该宽度及以上，一级和嵌套页签应平铺占满整行。
- 小于 640 px 的移动端视口允许页签横向滚动，但不能压缩为不可辨认的窄按钮。
- “经营中”的六项经营摘要和全局三项摘要在 640 px 仍保持三列。
- 列表行允许内容换行或内部滚动，不应通过隐藏关键状态来换取固定高度。
- 背景透明度、内容透明度和字体缩放是互相独立的显示设置；字体缩放通过根级 CSS 变量传播。
- 确认对话框必须使用项目提供的实色 surface，不能依赖页面背景提供可读性。

解释设置含义时使用统一的帮助字段和 portal tooltip；当前值、错误和阻塞原因仍需直接可见，不能只放在
tooltip 中。仅供诊断的内部 ID、原始状态和耗时受“显示调试详情”控制。

## 键盘、鼠标与手柄

手柄输入由两层组成：

- `apps/companion/src/companion/gamepad/gamepad-input-engine.ts` 负责标准手柄状态、重复节奏和语义动作。
- `apps/companion/src/companion/gamepad/gamepad-focus-manager.ts` 与
  `use-gamepad-navigation.ts` 负责焦点、页签、列表滚动和 modal scope。

可交互元素应使用既有的 `data-gamepad-*` 契约，并提供稳定、唯一的 focus key。对话框打开后必须限制在
modal scope；关闭后恢复合理焦点。浏览器原生点击、键盘操作与手柄动作应共享同一业务 handler，不能维护
两套结果不同的路径。

F8 和 RS Click 的窗口聚焦切换属于 Tauri 桌面能力，不受“手柄导航”开关影响。Android 端只提供页面和
连接能力，不提供置顶、鼠标穿透、托盘或桌面窗口聚焦。

## 维护入口

- 页面组合：`apps/companion/src/companion/ModWorkbench.tsx`
- 设置页：`apps/companion/src/companion/pages/ModSettingsPanel.tsx`
- 共享控件：`apps/companion/src/components/ui/`
- 主题与布局：`apps/companion/src/index.css`
- 帮助内容：`apps/companion/src/data/help-content.json`
- 桌面窗口：`apps/companion/src-tauri/src/app.rs`
- 本地开发与 mock： [本地开发](local-development.md)
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
