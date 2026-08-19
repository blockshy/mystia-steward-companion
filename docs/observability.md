# 日志与诊断

更新日期：2026-08-19

本文说明聚合日志、运行时诊断、BepInEx 控制台和诊断包的职责。运行时数据本身如何采集见
[运行时数据提供器](runtime-provider.md)，本地 API 的认证和暴露范围见 [本地 API](local-api.md)。

## 总原则

可观测性只能观察和解释业务行为，不能成为业务行为的输入。日志、诊断采样、导出失败或控制台窗口异常都不
得改变推荐、自动化、任务、游戏 UI 或本地 API 的正常响应。

诊断输出应满足：

- 有界：限制缓存项、文本长度、文件大小、文件数量和导出 tail。
- 可关联：保留时间、通道、来源、线程、经营 generation、订单 trace/lifecycle 等现有身份。
- 可去重：对高频重复状态使用有界签名或周期摘要，不无限刷屏。
- fail-safe：写日志失败被隔离，不能把异常抛回游戏主循环。
- 不猜测：缺失运行时字段记录明确失败原因，不用显示文本或对象名拼出业务身份。

## 聚合 Mod 日志

`mods/bepinex/src/Save/AggregateModLogService.cs` 提供可选的聚合日志。它注册唯一 `ILogListener`，收集
BepInEx 日志源中的 Mod、自动化和运行时消息，并写为带时间、channel、source、level 和线程信息的结构化
文本。

聚合日志默认关闭。单段固定上限为 10 MiB，默认保留 30 段，约 300 MiB；保留数量可在设置页调整并经过
上限归一化。滚动和清理只管理聚合日志自己的命名空间，不截断、不移动、不删除 BepInEx `LogOutput.log`、
Unity Player 日志或其他 Mod 的文件。

每次写入都在服务锁内重新确认当前路径健康状态。若用户或外部程序移动/删除活动文件，下一条日志会恢复到
新的有效段，并重置与旧文件相关的去重签名。实现不依赖文件 watcher 或高频轮询，也不为“等待日志”预建空
文件。

## 运行时诊断

运行时捕获服务发布两类信息：

- 面向业务的规范快照，只包含经过完整门禁的数据。
- 面向定位的有界诊断，记录 Hook 就绪、形态失败、generation、identity、状态机与拒绝原因。

第二类不得被页面或自动化反向用作兜底数据源。诊断开关只改变采样和日志详细度，不得开启另一套更宽松的
运行时读取。任务专题的业务/诊断边界见 [任务系统](missions.md)，自动化状态机见
[自动化运行时](automation-runtime.md)。

订单使用进程内单调 trace（稀客 `R-*`、普客 `N-*`）以及精确 lifecycle 与原生 key 关联跨组件记录。新增
日志时优先携带这些既有身份，不要引入仅凭客人名、桌号或显示文本关联订单的旁路。

前端可通过受限诊断端点追加“推荐为何无法执行”等决策摘要。服务对 client、签名、行数和文本长度做门禁；
前端诊断异常仍不得影响原 API 响应。

## BepInEx 控制台

控制台窗口控制由以下实现负责：

- `mods/bepinex/src/Plugin/BepInExConsoleRuntime.cs`
- `mods/bepinex/src/Plugin/BepInExConsoleWindowService.cs`

该能力仅支持游戏 PC 上的 Windows 回环连接，不允许 LAN 客户端控制。它不修改 `BepInEx.cfg`。默认自动显示
关闭时，不会隐藏用户或其他组件已经打开的控制台。

显示控制台时，如 BepInEx #783 尚未创建 console，服务调用其正式 `ConsoleManager.CreateConsole` 入口，并
只为本次由 Mod 新建的窗口禁用系统菜单关闭命令，避免关闭窗口导致日志后端状态损坏。服务保持 driver、Win32
窗口存在性和可见性三种状态分离，并确保唯一 `ConsoleLogListener`。

隐藏操作只调用 `ShowWindow(SW_HIDE)`，不 detach console、不销毁 driver。显隐、启动偏好和状态读取在同一锁
下提交，失败时尽量恢复调用前状态。UTF-8 设置只作用于当前已确认的 console。

## 诊断包

“导出诊断包”由 `LocalApiServer` 在
`BepInEx/config/MystiaStewardCompanion/diagnostic-packages/` 创建带时间戳的 ZIP。当前包按类别包含：

- manifest 和当前规范快照。
- 运行时静态目录快照。
- 任务、scheduler、可接取任务来源和 ServeInWork 等诊断 JSON。
- 聚合日志各段的有界 tail（存在时）。

导出只复制有界内容，不暂停业务服务，也不修改源日志。单个源缺失时应在其边界内处理，不能因此生成一个
看似完整却混用旧数据的包。诊断包可能包含存档进度、订单和本机路径等运行信息，提交 issue 前应由用户确认
分享范围。

## 前端与 API

日志页位于 `apps/companion/src/companion/pages/ModLogsPanel.tsx`，只有开启“显示调试详情”后才显示。页面负责：

- 查看并修改聚合日志启用状态与保留数量。
- 打开日志或诊断包目录。
- 导出诊断包。
- 在受支持的本机 Windows 连接上显示或隐藏 BepInEx 控制台。

规范端点为：

- `GET /logs/settings`
- `POST /logs/config`
- `POST /logs/console`
- `POST /logs/open-folder`
- `POST /logs/export-diagnostics`

`/logs/console` 已限制为回环客户端。当前 `/logs/open-folder` 与
`/logs/export-diagnostics?open=true` 只校验 Token，尚未追加回环门禁；这是现有安全限制，不能把 LAN 暴露给
不可信网络，也不能在文档中把它们描述成已经隔离。后续强化时应直接为这两个本机打开动作增加回环检查并补充
方法矩阵 smoke，不保留远程旧路径。详细方法矩阵见 [本地 API](local-api.md)。

## 维护规则

- 新诊断项先定义容量、去重、清理和生命周期，再接入日志或 ZIP。
- 不在持有游戏视觉状态锁或 Unity 容器遍历期间做文件 I/O。
- 不记录连接 Token、完整授权头或其他秘密。
- 新导出条目使用稳定类别路径，并在协议测试中验证边界。
- 不恢复“读取共享日志再解析业务状态”的路径。

## 验证

聚合日志和 API 方法边界：

```bash
dotnet run --project tests/aggregate-mod-log-lifecycle/AggregateModLogLifecycleSmoke.csproj -c Release
dotnet run --project tests/local-api-method-matrix/LocalApiMethodMatrixSmoke.csproj -c Release
```

控制台与前端：

```bash
dotnet run --project tests/bepinex-console-window/BepInExConsoleWindowSmoke.csproj -c Release
corepack pnpm audit:logs:console
```

运行时专题诊断由各专题 smoke 负责，完整验证分层见 [验证指南](validation-guide.md)。
