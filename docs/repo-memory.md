# 项目事实与决策索引

更新日期：2026-08-19

本文保存需要跨会话快速确认、但不属于具体操作步骤的稳定项目决策。它不是功能流水账，也不复制专题契约；细节以链接文档和测试源码为准。

## 项目定位

- `mystia-steward-companion` 由 BepInEx IL2CPP Mod、React 伴随窗口和 Tauri 桌面/Android 壳组成。
- Mod 在游戏电脑上读取运行时状态并提供本地 API；伴随窗口可以同机运行，也可以作为局域网内的另一台 Windows/Android 设备连接。
- 伴随窗口是唯一用户界面。游戏内不提供备用 IMGUI 或嵌入式菜单页面。
- 当前产品版本、支持平台和用户操作以两份用户 README 与内置帮助为准；本文不保存发布版本快照。

组件关系见[项目架构](architecture.md)。

## 关键目录

| 路径 | 作用 |
| --- | --- |
| `apps/companion/src/` | React 页面、领域逻辑、推荐引擎、Worker 和结构化数据 |
| `apps/companion/src-tauri/` | Tauri 桌面/移动入口、窗口控制、本地代理和更新程序 |
| `mods/bepinex/src/` | 运行时 Provider、业务服务、本地 API、自动化和游戏 UI 集成 |
| `mods/bepinex/References/` | 由 lock 精确校验、但不提交真实 DLL 的构建引用目录 |
| `tests/` | C# smoke、Node audit、mock 与 Playwright 契约 |
| `docs/` | 按职责拆分的开发、运行时、机制与发布文档 |

## 当前稳定决策

| 领域 | 决策摘要 | 权威文档 |
| --- | --- | --- |
| 工具链 | `toolchain.lock.json` 是本地与 CI 的唯一版本基线；不接受全局工具或 latest 回退 | [本地开发与构建](local-development.md) |
| 构建引用 | 正式 Mod 只使用 `references.lock.json` 指向的 7 个精确 DLL；不从游戏目录临时拼接 | [本地构建引用](../mods/bepinex/References/README.md) |
| 游戏证据 | metadata、interop、IDA 与实机日志交叉验证；未知状态 fail-closed | [IL2CPP / IDA 分析工作流](il2cpp-analysis-workflow.md) |
| 运行时数据 | 静态目录、玩家状态和业务快照分层；完整目录按内容签名独立获取 | [运行时数据 Provider](runtime-provider.md) |
| 订单 | 普客与稀客都以成功原生创建边界形成的精确捕获为权威；HUD 只补展示，不证明所有权 | [订单捕获与生命周期](runtime-order-lifecycle.md) |
| 自动化 | 每次副作用受当前主设备 profile、authority revision、automation lease、经营 generation 和订单 lifecycle 共同约束 | [自动化运行时](automation-runtime.md) |
| 推荐 | 候选管线先执行硬过滤，再组合和排序；只有完整计划为空才生成阻断诊断 | [推荐引擎](recommendation-engine.md) |
| 游戏 UI | 后台只发布 immutable target；Unity 主线程按精确 ownership 应用 Mod-owned 视觉和加料事务 | [游戏 UI 集成](game-ui-integration.md) |
| 设备配置 | 设备注册表中只有一个主设备；主设备 profile 是生效配置，非主设备可显式同步 | [本地 API](local-api.md) |
| 任务 | tracked 与 available 是两条只读业务链，不用存档 bool 或副作用查询补全状态 | [任务系统](missions.md) |
| 特殊经营 | 游戏原生规则与 Mod 执行策略分开记录，未验证事实不能进入自动化 | [游戏规则](special-business-scenes-notes.md) / [Mod 实现](special-business-implementation.md) |
| 收藏与自定义料理 | `favorites.json` 只保存料理/酒水收藏；`custom-recipes.json` 只保存自定义推荐料理 | [推荐引擎](recommendation-engine.md) |
| 更新 | catalog 提供累计说明，manifest 是安装权威；说明失败不能阻断有效包的检查和安装 | [更新系统](update-system.md) |
| 发布 | 稳定版只从 `main` 手动 Actions 发布，经过两道审批和 create-only immutable 事务 | [发布流程](local-release.md) |

## 设计底线

- 不用 UI 文本、名称、列表位置、托管 hash、固定延迟或全场景扫描猜测运行时 identity。
- 不把“未知”“读取失败”“部分数据”解释成空集合或成功。
- 不保留已经被当前 schema、路由、Hook 或存储模型替代的兼容路径。
- 不跨帧持有活的 IL2CPP wrapper；跨线程只传不可变托管数据和精确身份标量。
- 不让前端配置、自动化 lease、订单 lifecycle、UI target revision 或特殊经营 revision 相互冒充。
- 不把测试的逐条断言复制到本文；变更范围与测试入口见[验证指南](validation-guide.md)。

## 文档路由

完整职责表见[开发文档索引](README.md)。新增长期结论时先写入对应专题；只有当它确实影响多个模块且需要跨会话快速发现时，才在本页增加一行索引。
