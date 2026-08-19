# 开发文档索引

更新日期：2026-08-19

本目录面向维护者，记录项目架构、开发流程和已经由代码、测试或游戏分析资料确认的实现边界。用户安装与操作说明仍以根目录 `README.md`、`mods/bepinex/README.md` 和伴随窗口内置帮助为准。

## 维护原则

- 一个事实只由一份文档负责；其他文档只给出摘要和链接。
- 入口文档负责导航，不复制专题契约、完整命令矩阵或版本历史。
- 操作手册记录“怎样做”；架构和契约文档记录“边界是什么”；游戏机制文档记录“证据说明了什么”。
- 测试源码是逐条断言的权威来源，[验证指南](validation-guide.md) 只说明改动范围与验证入口。
- 临时排障过程、发布演练和阶段进度放在被 Git 忽略的 `temp/` 或 `SESSION_HANDOVER.md`，问题解决后不并入长期文档。
- 不为删除的旧路径、旧类型或旧行为保留兼容说明。仍有价值的结论应改写为当前唯一规则。

## 入口与架构

| 文档 | 唯一职责 |
| --- | --- |
| [项目架构](architecture.md) | 组件边界、平台边界和主要数据流 |
| [项目事实与决策索引](repo-memory.md) | 当前稳定决策的短索引，以及各决策的权威文档入口 |
| [开发约定](development-conventions.md) | 跨模块命名、编码、失败策略、变更与文档维护规则 |
| [Mod 开发入口](../mods/bepinex/README.dev.md) | BepInEx 子项目地图、最短上手步骤和专题导航 |

## 构建、验证与发布

| 文档 | 唯一职责 |
| --- | --- |
| [本地开发与构建](local-development.md) | 锁定工具链、依赖安装、桌面/Mod 构建、缓存和本地预览 |
| [Android 开发](android-development.md) | Android 工具链、签名、双 ABI APK 构建和本地排障 |
| [验证指南](validation-guide.md) | 按变更范围选择 lint、build、Cargo、dotnet、audit、smoke 与 Playwright |
| [发布流程](local-release.md) | 正式 Actions 发布、预览版发布、审批、资产事务和失败处理 |
| [本地构建引用](../mods/bepinex/References/README.md) | 私有引用 bundle 的身份、恢复和校验 |

## 运行时与应用契约

| 文档 | 唯一职责 |
| --- | --- |
| [运行时数据 Provider](runtime-provider.md) | 游戏静态目录、玩家状态、readiness、快照和失败状态 |
| [订单捕获与生命周期](runtime-order-lifecycle.md) | 夜间经营、普客/稀客捕获、强身份和终态回执 |
| [自动化运行时](automation-runtime.md) | 控制租约、阶段门禁、CookingJob、暂停恢复和安全栅栏 |
| [本地 API](local-api.md) | listener、鉴权、请求限制、规范路由和设备配置权威 |
| [游戏 UI 集成](game-ui-integration.md) | 置顶、加料变体、厨具/桌位/订单高亮及 Unity ownership |
| [伴随窗口 UI](companion-ui.md) | 导航、响应式布局、设计系统、手柄焦点和前端状态边界 |
| [推荐引擎](recommendation-engine.md) | 候选生成、硬过滤、排序、执行计划和阻断诊断 |
| [任务系统](missions.md) | tracked、available、调度来源与 ServeInWork 的只读边界 |
| [更新系统](update-system.md) | 检查调度、catalog/manifest、更新页面与 Windows updater |
| [日志与诊断](observability.md) | 总日志、控制台、诊断包和日志生命周期 |

## 游戏机制与分析资料

| 文档 | 唯一职责 |
| --- | --- |
| [IL2CPP / IDA 分析工作流](il2cpp-analysis-workflow.md) | 分析资料生成、证据层级、锁定工具和失效路径 |
| [Addressables 标签提取](addressables-tag-mapping-playbook.md) | 从游戏资源恢复 Tag ID 映射的操作步骤 |
| [料理机制知识库](tmi-cooking-mechanics-knowledge-base.md) | 游戏原生评价、Tag 生成与压制机制 |
| [特殊经营游戏规则](special-business-scenes-notes.md) | 特殊场景的游戏原生规则与证据 |
| [特殊经营实现](special-business-implementation.md) | Mod 对特殊场景的推荐、身份和自动化策略 |
| [特殊经营验证](special-business-validation.md) | 自动化入口、实机闭环状态和复测清单 |

## 修改时更新哪一份

- 改组件关系、进程边界或数据流：更新 `architecture.md`。
- 改构建命令、锁定工具或本地准备：更新 `local-development.md`；Android 专属内容只改 `android-development.md`。
- 改正式 Release 事务、GitHub 配置或资产集合：更新 `local-release.md`。
- 改运行时读取、订单、自动化、本地 API 或 UI ownership：只更新对应专题文档。
- 改用户可见功能：另行检查两份用户 README 和 `apps/companion/src/data/help-content.json`，但不要把开发实现复制到用户文档。
- 新增专题前先确认现有文档没有承担该职责；确需新增时同步本索引和相关入口链接。
