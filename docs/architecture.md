# 项目架构

更新日期：2026-08-19

本文只描述长期稳定的组件边界和数据流。构建命令见[本地开发与构建](local-development.md)，具体运行时约束见对应专题文档。

## 组件

| 组件 | 目录 | 职责 |
| --- | --- | --- |
| BepInEx IL2CPP Mod | `mods/bepinex/` | 在 Unity 主线程边界读取游戏状态、维护运行时缓存、执行受控游戏动作并提供本地 API |
| 伴随窗口前端 | `apps/companion/src/` | 展示运行态、运行推荐 Worker、管理设置与设备连接，不直接访问游戏对象 |
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
Loopback/LAN local API ── Tauri TCP proxy ── React companion
        ▲                                         │
        └──── authenticated commands / leases ────┘
```

- Mod 是游戏运行时事实和游戏副作用的唯一所有者。前端不得通过静态表、UI 文本或计时器猜测游戏状态。
- 网络线程只读取托管快照或把命令排入 Unity 主线程；不得直接持有或修改 IL2CPP wrapper。
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
- React 页面只渲染领域层和 Worker 的结构化结果；热路径状态先进入专用 store/ref，再按语义签名发布，避免把每次轮询都提升到顶层组件。

## 权威来源

| 问题 | 权威来源 |
| --- | --- |
| 游戏类型、字段与方法语义 | 锁定的 metadata C#、BepInEx #783 interop、IDA/Hex-Rays 与实机日志交叉验证 |
| 当前料理、库存、角色、场景和 Tag | 游戏运行时目录与状态 Provider |
| 推荐候选与排序 | 前端推荐引擎及 Worker |
| 游戏动作是否允许 | Mod 内当前 generation、精确 identity、配置权威和 automation lease |
| 发布版本与资产 | `main` 上的版本字段、正式 workflow、manifest/catalog 和 immutable GitHub Release |
| 逐条回归断言 | `tests/` 与 `scripts/` 中的 smoke/audit |

## 不属于架构文档的内容

本文不保存具体 Hook 清单、API 路由表、测试命令矩阵、Release 操作步骤、版本迁移记录或临时排障结论。它们分别由专题文档、[验证指南](validation-guide.md)、[发布流程](local-release.md)和会话临时文档负责。
