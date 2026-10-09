# 项目架构

更新日期：2026-10-10

本文只描述长期稳定的组件边界和数据流。构建命令见[本地开发与构建](local-development.md)，具体运行时约束见对应专题文档。

## 组件

| 组件 | 目录 | 职责 |
| --- | --- | --- |
| BepInEx IL2CPP Mod | `mods/bepinex/` | 在 Unity 主线程边界读取游戏状态、维护运行时缓存、执行受控游戏动作并提供本地 API |
| 业务协议 | `modules/companion-contracts/` | 版本化的纯值查询意图与输入边界，不依赖 Unity 或客户端框架 |
| C# 业务类库 | `modules/companion-business/` | 纯数据推荐、特殊经营、订单与主方案、游戏 UI 目标投影和自动化编排；目标 .NET 6 / C# 10 |
| 伴随窗口前端 | `apps/companion/src/` | 展示服务端结果、管理表单、设置、焦点和连接；提交小型查询意图，不自行计算推荐或生成执行目标 |
| Tauri 桌面/移动壳 | `apps/companion/src-tauri/` | 窗口与单实例控制、本地 TCP 代理、独立更新程序，以及 Windows/Android 平台接入 |
| 测试与审计 | `tests/`、`scripts/` | 固定协议、运行时 identity、发布策略和 UI 行为，阻止文档与实现边界漂移 |

## 主要数据流

```text
Unity / IL2CPP runtime
        │  Unity 主线程读取与受控命令
        ▼
BepInEx providers and services
        │  immutable managed snapshots
        ▼
C# business host ── pure business library
        │                    │
        │ cached results     └─ exact commands ── Unity main-thread adapter
        ▼
Loopback/LAN local API ── Tauri TCP proxy ── React companion
        ▲                                         │
        └── authenticated query intent / leases ───┘
```

- Mod 是游戏运行时事实和游戏副作用的唯一所有者。前端不得通过静态表、UI 文本或计时器猜测游戏状态。
- 网络线程只读取托管快照或把命令排入 Unity 主线程；不得直接持有或修改 IL2CPP wrapper。
- 推荐和编排由 Mod 进程内的单一后台业务宿主执行；不另起外部业务服务器，也不在 Unity 主线程运行候选搜索。纯类库不引用 BepInEx、Unity、HTTP、React 或 Tauri。
- 客户端只能提交地区、客人、料理/酒水标签等查询意图；目录、库存、订单、收藏、自定义配方及生效配置由宿主装配。客户端不能上传自己计算的候选、执行步骤或 UI 目标。
- 原客户端订单执行与 UI 目标发布路由显式拒绝。服务不可用、协议不匹配或结果过期时显示等待/错误；生产不回退到旧 TypeScript 算法。
- `/snapshot` 发布频繁变化的轻量状态；完整运行时目录由 `/runtime-data` 按内容签名缓存。详见[运行时数据 Provider](runtime-provider.md)。
- 订单捕获、自动化控制和游戏 UI 目标各有独立 generation/revision/lease，不能用一个布尔状态代替。详见[订单捕获与生命周期](runtime-order-lifecycle.md)、[自动化运行时](automation-runtime.md)和[游戏 UI 集成](game-ui-integration.md)。

## 平台边界

- 游戏电脑必须运行 BepInEx Mod；伴随窗口可在同一台 Windows 电脑或局域网内另一台 Windows/Android 设备运行。
- 回环 listener 始终存在，LAN listener 是额外能力。远端设备不能调用只允许游戏电脑执行的本机管理操作。
- 桌面专属能力包括托盘、窗口聚焦、鼠标穿透、单实例和游戏退出联动；Android 只承担 LAN 伴随客户端，不实现这些桌面行为。
- 独立更新程序只面向 Windows 10 1703 及以上；Android APK 和独立 Windows EXE 不参与 Mod ZIP 自动安装。

## 线程与状态所有权

- Unity 对象只能在已确认的主线程边界读取、修改、恢复或销毁。
- 跨线程只传递不可变的托管 DTO、签名、generation、revision、原生 identity 标量或命令结果；不跨帧缓存活的 IL2CPP wrapper。
- 读不到状态、identity 不唯一、集合形态漂移或原生调用结果不确定时一律 fail-closed。恢复必须来自下一次可信读取，不增加旧来源或猜测路径。
- React 页面只渲染业务 API 结果；客户端仍负责 pending/错误/旧结果展示、列表排序、焦点和表单格式整理。旧结果可供展示，但不可据此触发游戏动作。
- 宿主计算后、发布前和动作提交前分别检查输入版本、快照与控制权；旧计算不发布为当前结果。缓存只复用完整语义输入一致的纯候选，每轮仍重新绑定订单身份、代际、执行状态与设备权威。
- 自动化迁移不改变断线语义：客户端 lease、设备配置权威与游戏经营许可仍是执行条件。不能因为协调器在 Mod 进程内，就把失去控制权后的行为改为无人值守继续执行。

## 权威来源

| 问题 | 权威来源 |
| --- | --- |
| 游戏类型、字段与方法语义 | 锁定的 metadata C#、BepInEx #783 interop、IDA/Hex-Rays 与实机日志交叉验证 |
| 当前料理、库存、角色、场景和 Tag | 游戏运行时目录与状态 Provider |
| 推荐候选、主执行方案、特殊经营与自动化调度 | 纯 C# 业务类库，由 Mod 内单一宿主装配当前输入 |
| 页面、焦点、显示数量和本地表单状态 | 客户端 UI；必要业务查询通过版本化意图表达 |
| 游戏动作是否允许 | Mod 内当前 generation、精确 identity、配置权威和 automation lease |
| 发布版本与资产 | `main` 上的版本字段、正式 workflow、manifest/catalog 和 immutable GitHub Release |
| 逐条回归断言 | `tests/` 与 `scripts/` 中的 smoke/audit |

旧 TypeScript 计算实现固定在 `tests/reference/`，只作为离线差分 oracle。它不被生产构建导入，也不作为服务失败时的兼容层。推荐纯函数、订单组合、自动化与实际宿主分别测试；mock 和离线结果不等同于游戏实机证据。

## 不属于架构文档的内容

本文不保存具体 Hook 清单、API 路由表、测试命令矩阵、Release 操作步骤、版本迁移记录或临时排障结论。它们分别由专题文档、[验证指南](validation-guide.md)、[发布流程](local-release.md)和会话临时文档负责。
