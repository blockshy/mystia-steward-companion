# 项目架构

更新日期：2026-10-23

本文只描述长期稳定的组件边界和数据流。构建命令见[本地开发与构建](local-development.md)，具体运行时约束见对应专题文档。

## 组件

| 组件 | 目录 | 职责 |
| --- | --- | --- |
| BepInEx IL2CPP Mod | `mods/bepinex/` | 在 Unity 主线程边界读取游戏状态、维护运行时缓存、执行受控游戏动作并提供本地 API |
| 业务协议 | `modules/companion-contracts/` | 版本化的纯值查询意图与输入边界，不依赖 Unity 或客户端框架 |
| C# 业务类库 | `modules/companion-business/` | 纯数据推荐、特殊经营、订单与主方案、游戏 UI 目标投影和自动化编排；目标 .NET 6 / C# 10 |
| Flutter 客户端 | `apps/companion-flutter/` | Forui页面、表单、设置与焦点；提交小型查询意图，不自行计算推荐或生成执行目标 |
| Dart 协议/会话 | `packages/companion_protocol/`、`packages/companion_client/` | 不可变协议、Dart IO HTTP、设备CAS、串行租约、来源与执行资格 |
| 原生平台 | `packages/companion_platform/` | Pigeon 连接 Windows 窗口/托盘/焦点/DPAPI 与 Android Keystore/耐久存储 |
| 游戏图片服务 | `mods/bepinex/src/GameMedia/`、`apps/companion-flutter/lib/app/game_media_controller.dart` | 游戏主线程按精确目录提取图像、后台校验与原子缓存、客户端按已确认代次读取 |
| 独立更新程序 | `native/updater/` | Win32界面、精确进程退出确认、完整清单与整目录事务回退 |
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
Loopback/LAN local API ── Dart IO HTTP ── Flutter companion
        ▲                                         │
        └── authenticated query intent / leases ───┘
```

- Mod 是游戏运行时事实和游戏副作用的唯一所有者。前端不得通过静态表、UI 文本或计时器猜测游戏状态。
- 网络线程只读取托管快照或把命令排入 Unity 主线程；不得直接持有或修改 IL2CPP wrapper。
- 推荐和编排由 Mod 进程内的单一后台业务宿主执行；不另起外部业务服务器，也不在 Unity 主线程运行候选搜索。纯类库不引用 BepInEx、Unity、HTTP、React 或 Tauri。
- 客户端只能提交地区、客人、料理/酒水标签等查询意图；目录、库存、订单、收藏、自定义配方及生效配置由宿主装配。客户端不能上传自己计算的候选、执行步骤或 UI 目标。
- 已移除原客户端订单执行与 UI 目标发布路由。服务不可用、协议不匹配或结果过期时显示等待/错误；生产不回退到旧客户端算法。
- `/snapshot` 发布频繁变化的轻量状态；完整运行时目录由 `/runtime-data` 按内容签名缓存。详见[运行时数据 Provider](runtime-provider.md)。
- 游戏图像按独立代次初始化和复用，不随生产包分发。图像服务提前在主菜单就绪不能代替业务目录或场景就绪；校验期间客户端使用独立初始化界面，运行时副作用仍受服务端资源状态约束。详见[游戏图像资源](game-media.md)。
- 订单捕获、自动化控制和游戏 UI 目标各有独立 generation/revision/lease，不能用一个布尔状态代替。详见[订单捕获与生命周期](runtime-order-lifecycle.md)、[自动化运行时](automation-runtime.md)和[游戏 UI 集成](game-ui-integration.md)。

## 平台边界

- 游戏电脑必须运行 BepInEx Mod；伴随窗口可在同一台 Windows 电脑或局域网内另一台 Windows/Android 设备运行。
- Mod 启动 Windows 管家与独立更新器时，仅在子进程环境中移除 Steam 游戏关联 ID，并设置 `SteamNoOverlayUIDrawing=1`，减少 Steam 叠加界面误绘入工具窗口的机会；父游戏环境及 Steam 配置不变。该变量不是 Steamworks 承诺稳定的公开接口，不能据此保证所有 Steam 版本阻止绘制或 DLL 注入；需在用户开启性能监视器的实际启动链路中确认。
- 回环 listener 始终存在，LAN listener 是额外能力。远端设备不能调用只允许游戏电脑执行的本机管理操作。
- 桌面专属能力包括托盘、窗口聚焦、鼠标穿透、单实例和游戏退出联动；Android 只承担 LAN 伴随客户端，不实现这些桌面行为。
- 整套桌面产品面向 Windows 10/11 x64，独立更新程序只在 Windows 执行；Android APK 和独立 Windows 客户端 ZIP 不参与 Mod ZIP 自动安装。具体平台证据范围见[发布流程](local-release.md)。

## 线程与状态所有权

- Unity 对象只能在已确认的主线程边界读取、修改、恢复或销毁。
- 跨线程只传递不可变的托管 DTO、签名、generation、revision、原生 identity 标量或命令结果；不跨帧缓存活的 IL2CPP wrapper。
- 读不到状态、identity 不唯一、集合形态漂移或原生调用结果不确定时一律 fail-closed。恢复必须来自下一次可信读取，不增加旧来源或猜测路径。
- Flutter 页面只渲染业务 API 结果；客户端负责 pending/错误/旧结果展示、显示筛选、焦点和表单格式整理。旧结果可供展示，但不可据此触发游戏动作。
- 宿主计算后、发布前和动作提交前分别检查输入版本、快照与控制权；旧计算不发布为当前结果。缓存只复用完整语义输入一致的纯候选，每轮仍重新绑定订单身份、代际、执行状态与设备权威。
- 已完成但过期的计算可在相同来源作用域内推进只读展示，保留原版本和签名，不更新执行帧或游戏辅助目标。经营列表以最新快照中的订单为全集，按唯一 trace/生命周期（普客另需 orderKey）及完整需求核验后附加方案；订单消失立即移除，新单先显示现场事实，不能等待慢方案后才更新列表。
- 宿主私有 JSON 在唯一业务线程完成容器懒展开后永久只读；自动化与游戏辅助通过组件视图读取，避免把完整目录和方案反复装入 JSON 参数袋。公开可变 JSON 入口仍取得独立副本。自动化视图会保留到后续完成/状态读取，不能只保证调用期间不修改；新权限、时间和订单绑定仍每轮更新。
- HTTP 发布只保存不可变 JSON 片段：完整推荐按结果引用编码一次，普客当前目标独立编码；读取时拼接并附加最新编排状态，不复制整棵结果树，也不缓存旧执行资格。线程共享前的递归物化是 .NET 6 JSON 实现的并发边界，序列化本身不能替代它。
- 自动化迁移不改变断线语义：客户端 lease、设备配置权威与游戏经营许可仍是执行条件。不能因为协调器在 Mod 进程内，就把失去控制权后的行为改为无人值守继续执行。

## 配置与持久化所有权

- 共享推荐、自动化与游戏界面辅助配置由 Mod 设备权威存储持有；客户端先持久化服务端返回的 profile，再确认同步。设备协议、磁盘 schema 及一次性历史迁移由[本地 API](local-api.md)定义，迁移不构成旧客户端兼容层。
- 窗口、字体、导航、专注显示、连接与本机扩展偏好由客户端平台存储持有。任务、邀请和库存页面的本机模块开关仅控制该设备的附加读取与人工操作入口，不改写其他设备的配置，也不关闭后台共享的任务捕获。
- 收藏、自定义配方、诊断包与游戏图片缓存各有独立存储，不混入设备 profile。发布载荷与运行产生的配置、存档、`user-data` 分离，完整包安装不能把这些数据当作可清理的构建文件。

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

旧 TypeScript 纯计算闭包固定在 `tests/reference-client/`，只作为离线差分 oracle。其来源提交、文件摘要和偏好声明的 AST 投影可复核，不包含旧产品入口。它不被生产构建导入，也不作为服务失败时的兼容层。推荐纯函数、订单组合、自动化与实际宿主分别测试；离线结果不等同于游戏实机证据。

## 不属于架构文档的内容

本文不保存具体 Hook 清单、API 路由表、测试命令矩阵、Release 操作步骤、版本迁移记录或临时排障结论。它们分别由专题文档、[验证指南](validation-guide.md)、[发布流程](local-release.md)和会话临时文档负责。
