# 日志与诊断

更新日期：2026-10-23

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

聚合日志默认关闭。单段固定上限为 10 MiB，默认保留 30 段，约 300 MiB；保留数量可在诊断页选择1–30，界面与服务端范围一致。
滚动和清理只管理聚合日志自己的命名空间，不截断、不移动、不删除 BepInEx `LogOutput.log`、
Unity Player 日志或其他 Mod 的文件。

每次写入都在服务锁内重新确认当前路径健康状态。若用户或外部程序移动/删除活动文件，下一条日志会恢复到
新的有效段，并重置与旧文件相关的去重签名。实现不依赖文件 watcher 或高频轮询，也不为“等待日志”预建空
文件。

### 主线程性能窗口

开启现有聚合日志后，`StewardOverlayController` 将阶段耗时汇总到同一目录的
`BepInEx/config/MystiaStewardCompanion/aggregate-mod.log` 及其轮转段。查找 channel `performance`
或稳定标记 `MSC-PERF-WINDOW v1`。窗口每 **5 秒**输出一次，不逐帧写日志；输出时间沿用日志头，
`windowSeconds` 是实际窗口长度。每个 `stage` 包含 `count`、`meanMs`、`maxMs`、`slow`：

- 普通阶段的慢调用阈值为 **大于等于 8ms**；这是定位阈值，不是帧预算或执行限制。
- `frame.updateGap` 是相邻 Unity `Update` 入口的 Stopwatch 墙钟间隔，慢间隔阈值单独设为
  **大于等于 50ms**。正常 60Hz 约 16.7ms 不会被全部标为慢间隔。
- `count` 统计确实调用该阶段的次数，部分每帧入口可能没有待办而立即返回；结合最大值和慢调用数判断，
  不仅看均值。关闭日志、运行时未就绪、游戏时间暂停、场景或经营代次变化会清除相邻间隔的前序时戳，
  恢复首帧不计，避免把加载/暂停时长当成连续经营卡顿。

覆盖阶段包括 Update/LateUpdate、经营/场景门禁、运行期图片、任务读取、库存和订单写入队列、邀请、
自动化 cooking job，以及置顶、厨具、座位、订单、投掷送达订单和列表高亮；快照、普客/稀客采集与运行时
读取保留已有细分计时。每个窗口同时带场景、普客/稀客订单数、运行时/自动化支持状态及已有 UI 目标摘要。
上下文只取现成托管状态，不为日志额外遍历游戏对象。

`main.orderCaptureRefresh` 进一步分为 `capture.versionRefresh`（每帧 Hook 版本检查及发生变化时的刷新）
与 `snapshot.activePollDue`（既有 750ms 活跃经营快照失效门禁）。周期入口先判断截止时间，再执行可能
查询主菜单对象的可读性检查；这不改变原刷新频率，也不缓存可读性或游戏对象，事件刷新仍独立按原条件处理。

订单写入入口的 `action.gate` 记录控制器原场景/会话检查；`action.rare.prepare`、
`action.rare.complete`、`action.normal.complete` 分别记录整个运行时动作服务调用，**包含服务内部执行
门禁、反射与原生调用，不能解释为纯原生函数耗时**。`action.refresh.rare` / `action.refresh.normal`
单独记录动作后原有强制经营刷新；失败或等待结果仍按原条件刷新。队列代次与权限检查未因此省略。
阶段键最多 96 个，以容纳全扩展窗口和新增子阶段，同时保留固定容量上限。

普客动作进一步记录 `action.normal.lookup`（查单）、`beverage`（送酒）、`delivery`（送料理）、
`collect`（取出已完成料理）、`cook`（开锅）和 `settle`（评价及原有收尾）；
`action.normal.requestLog`、`action.log` 分离现有同步日志调用。座位高亮首次创建记录
`ui.seat.whiteSprite` 总段及 `textureCreate`、`pixelFill`、`textureUpload`、`spriteGeometry` 子段。
这些固定键只包装既有调用，不预热原生动作、不新增像素写入或日志，不改变对象销毁及每帧身份守卫。
作用域按主线程和 Controller 所有权绑定；日志关闭、窗口销毁或替换后旧作用域不再回写。

`MSC-FRAME-STALL v1` 在相同日志中补充逐帧关联，每五秒至多保留八个不小于 **33.333ms**
的最慢相邻 Update 间隔，并统计不小于 16.667/33.333/50ms 的次数及未保留样本数。
每条样本把该间隔与**前一帧**完整 Mod Update、LateUpdate、最慢业务阶段、这两段 Mod 范围的
主线程 CLR 分配量以及两次入口间 CLR GC 代次计数差关联。未调用 LateUpdate 时明确记录调用数为零；
暂停、换场、经营代次变化和日志重启均丢弃旧关联。样本通过 `ageMs` 与日志头时刻对应，不把五秒窗口内
多个互不相干的阶段最大值拼成同一帧。

历史 `main.update` 保留核心更新范围以便前后比较；新样本的 `updateMs` **包含诊断汇总与同步写日志**，
`diagnostics.flush` 单独记录该成本。分配计数不是整帧或 Unity 原生堆分配；GC 差值属于整个 CLR 进程，
只提供同一间隔的相关事实，不证明 GC 造成停顿。采样不触发 GC，不改变游戏帧率设置或自动化调度。

UI 目标发布的原子锁内执行目标与权限状态转换，并保存本次诊断文本；日志输出和完整跨高亮状态读取移到锁外，
避免后台日志写盘阻塞主线程取得发布锁。同一发布事务内部日志顺序保持；并发事务的锁外输出可能交错，
应使用日志已有的 publication/目标代次关联，不用日志物理顺序作为业务顺序。

### 后台业务分配窗口

`MSC-BUSINESS-WINDOW v1` 随同一个聚合日志开关启用，由唯一后台业务循环每五秒至多输出一次。
记录输入捕获、payload、订单推荐、当前目标绑定、自动化、UI 目标、发布、页面查询、命令与整轮的调用数、
平均/最大耗时、累计/单次最大 CLR 分配字节；同时记录输入复用、过期丢弃、失败和候选缓存命中/未命中次数。
固定十个阶段，不含订单全文或身份，不逐请求写盘；格式化和输出在业务锁、权威锁和发布锁之外进行。

每个作用域只覆盖同一线程的同步工作，等待下一轮之前结束。整轮与子阶段存在包含关系，不能把字节或耗时相加；
命令阶段可能等待 Unity 主线程，不能把等待时间当成后台 CPU 时间。该分配量用于与相邻 `MSC-FRAME-STALL`
的 GC 计数、时间和主线程阶段共同排查，仍不能单独证明 GC 暂停或换算 FPS。

相同业务输入会复用只读结果；观察时间等非推荐语义变化仍通过完整 payload 核验，食材库存大于等于五和无限库存
仅在纯方案缓存键内视为相同资源压力。权威、租约、最新目标绑定与自动化时间每轮重新检查，缓存命中不延长执行许可。
页面查询按可见会话登记/撤销，已隐藏页不持续求解，协议边界见[本地 API](local-api.md)。

### 其它诊断与复现

图片缓存另使用 channel `game-media`、标记 `MSC-GAME-MEDIA v1`，只在 `cache-check`、`cache-hit`、
`cache-miss`、`forced-extract`、`complete`、`failed` 转换点记录状态、进度和已验证代次，不逐帧写入。
缓存命中应有 `cache-hit`；用户主动重读走 `forced-extract`，不能把两者混作自动重新提取。

阶段可能嵌套，例如 `snapshot.publish` 包含 `snapshot.serialize`；**不要把所有阶段相加**。
`frame.updateGap` 反映调用间隔，能帮助识别“Mod 内部阶段正常，但下次调用已迟到”的情况，不能据此把
停顿归因于 GPU、游戏原逻辑或其它 Mod，更不能将该日志称为完整游戏 FPS 测量。

完整 `snapshot` 诊断首条以及场景、可读性、错误或恢复变化立即记录；其它内容变化和 `skip-unchanged`
合并为五秒一次的最新完整快照，`windowOutcomes` 保留 publish/forced/unchanged 数量。原错误日志通道
不受此合并限制。性能采样不改变快照发布时间、自动化门禁或业务执行。

复现时，在“设置 → 诊断”开启聚合日志，按平常方式经营，记录明显卡顿的本机时刻、订单数量、启用的扩展，
以及是否刚进入场景或暂停。尽量保留卡顿前后各一分钟日志；需要对比时，在相近订单/场景条件下分别记录
开关某项扩展的片段，不在同一短窗口连续切换很多开关。之后提供相关日志段或诊断包，由阶段统计定位下一步。
未经真实游戏测量的离线调用次数/托管分配优化，只能作为减少重复工作的证据，不能换算成 FPS 收益。

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

导出、列举和删除统一使用 `DiagnosticPackageStore` 的同一固定目录。导出先写唯一临时文件，完整关闭 ZIP 后才发布；列表只接受本项目诊断包命名、有效 manifest 及两份必要快照，显示文件名、大小和创建时间，不管理原始日志或任意改名 ZIP。

删除只能提交本次服务实例已列举的精确 ID；ID 绑定文件名、长度和修改时间，删除前再次检查文件状态与路径祖先。文件替换、失效 ID、路径链接、目录或未知文件均拒绝，不做递归删除。客户端确认必须绑定原连接、权限与包 ID；列表读取失败显示失败并撤下旧删除入口，不能显示为“暂无诊断包”。完整传输字段以[本地 API](local-api.md)为准。

## 前端与 API

诊断页由 `apps/companion-flutter/lib/features/settings.dart` 组合，诊断包和操作记录组件位于 `apps/companion-flutter/lib/features/settings_management.dart`。页面负责：

- 查看并修改聚合日志启用状态与保留数量。
- 打开日志或诊断包目录。
- 导出诊断包。
- 查看已导出诊断包，并经确认删除指定包。
- 在受支持的本机 Windows 连接上显示或隐藏 BepInEx 控制台。
- 查看本次客户端运行的最近操作：具体中文动作、时间与成功/失败/结果待确认状态。历史最多 20 条，不收录后台轮询、凭据或请求正文；它不替代持久服务日志。写入已经确认成功、后续刷新失败时仍保留写入成功事实，不诱导重放。

规范端点为：

- `GET /logs/settings`
- `GET /logs/packages`
- `POST /logs/config`
- `POST /logs/console`
- `POST /logs/open-folder`
- `POST /logs/export-diagnostics`
- `POST /logs/packages/delete`

五个写端点均要求有效 Token、已注册 clientId、当前主设备和精确 authorityRevision，且主设备的共享配置必须已保存并确认；不需要自动化 lease。
`/logs/console` 另限制为回环客户端。当前 `/logs/open-folder` 与 `/logs/export-diagnostics?open=true` 在服务端没有
额外回环门禁，通过主设备鉴权的 LAN 请求仍可调用；Flutter 页面仅为游戏电脑连接提供本机打开操作，不能将该 UI
限制描述为服务器已经拒绝 LAN。详细方法矩阵与真实 HTTP 负例见 [本地 API](local-api.md)。

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
dotnet run --project tests/runtime-performance-diagnostics/RuntimePerformanceDiagnosticsSmoke.csproj -c Release
```

控制台与前端：

```bash
dotnet run --project tests/bepinex-console-window/BepInExConsoleWindowSmoke.csproj -c Release
node scripts/flutter-workspace.mjs --sdk-root <锁定SDK绝对路径> --task test
```

运行时专题诊断由各专题 smoke 负责，完整验证分层见 [验证指南](validation-guide.md)。
性能窗口、活动订单逐帧身份检查及托管投影分配的专项命令和证据边界见
[性能诊断离线验证](../tests/runtime-performance-diagnostics/README.md)。
