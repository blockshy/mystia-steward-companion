# 本地 API

更新日期：2026-10-23

本文档只定义游戏进程内本地 HTTP API 的监听、鉴权、设备权威、方法矩阵、请求生命周期和传输边界。运行时数据含义见 [运行时 Provider](runtime-provider.md)，订单身份见 [运行时订单生命周期](runtime-order-lifecycle.md)，自动化状态机见 [自动化运行时](automation-runtime.md)。

## 监听模型

`LocalApiServer` 使用 `TcpListener` 实现轻量 HTTP 服务，不引入额外 Web 框架。

- `LocalApi.Enabled=true` 时，服务先绑定 `127.0.0.1` 作为本机入口；总开关关闭时不启动任何 listener。
- LAN listener 是附加通道，不能取代回环 listener。它既可由插件启动时读取的 `AllowLanConnections` 配置启用，也可由回环客户端更新本机连接配置后启用。
- `LanHost=auto` 只选择合格的私网 IPv4；服务拒绝公网来源。
- LAN 配置变化时串行停止旧 worker，再启动新地址；配置与地址集合未变化时不重启。
- 每个 listener 拥有独立停止状态和阻塞 accept 线程。主动停止导致的 accept 异常直接结束；意外异常最多报告一次并终止该 worker，不做无限重试。
- 客户端 handler 上限为 16。停止时先拒绝新连接并关闭在途 socket，再有界等待 handler 退出。

不要把 API 端口映射到公网。正式 Flutter 客户端通过 Dart IO 直连并禁用代理，不依赖 WebView 或浏览器 CORS。真实 Mod 的浏览器 CORS allowlist 不包含 authority revision header，浏览器直连不能承担设备权威写请求；协议验证使用真实隔离 HTTP 宿主。

## HTTP 与请求上限

服务只接受 `GET`、`POST` 与预检 `OPTIONS`：

- `GET` 只读状态。
- `POST` 承担文件、配置、运行时、网络更新和进程相关操作。
- `OPTIONS` 返回 204。
- 其他方法返回 405；未知路径或错误方法组合返回 404。
- 根路径 `/` 不映射健康检查，`/api/*` 不作为规范路径别名。

请求头必须在 32 KiB 内完整出现 `CRLFCRLF`，请求体上限为 64 KiB。EOF 截断返回 400，头超限返回 431，body 超限返回 413。`Transfer-Encoding`、重复或非法 `Content-Length` 均被拒绝；同次读取已经进入缓冲区、但超出声明长度的字节也会被拒绝。每个连接只处理一个请求并在响应后关闭，不支持流水线。设备协议 body 必须是严格 UTF-8 JSON，最大解析深度 16，不接受注释、尾随逗号、缺项或额外字段。

单个 socket 的收发超时为 2.5 秒。需要访问 Unity/IL2CPP 的命令会进入有界主线程队列：未开始命令超时后取消，主线程恢复时不得迟到执行；已经开始的命令等待确定结果，避免客户端因传输超时重放已发生的副作用。

## 鉴权与身份头

只有 `GET /health` 无需 Token。其他所有端点要求：

```text
X-Mystia-Steward-Companion-Token: <token>
```

设备与自动化协议另外使用：

| Header | 语义 |
| --- | --- |
| `X-Mystia-Steward-Companion-Client-Id` | 16–64 位 ASCII 字母、数字或 `-` 组成的稳定设备 ID |
| `X-Mystia-Steward-Companion-Client-Label` | 用户可读设备名，服务端限制为 48 字符 |
| `X-Mystia-Steward-Companion-Authority-Revision` | 当前主设备的正数配置权威 revision；运行时 writer 必须精确匹配 |

Token 只证明能访问 Mod；它不代表设备是主设备，也不代表持有自动化控制权。`/local-api/config`（含自定义Token）和 BepInEx 控制台显隐只允许游戏电脑的回环客户端调用。

手工写入收藏、自定义配方、库存、稀客邀请、订单缓存清除、日志配置/导出/打开目录、诊断包删除、诊断写入、本机连接配置（含自定义 Token）、游戏资源重读，以及更新下载/安排安装，均由 Mod 校验已注册 Client-Id、当前主设备和精确 Authority-Revision。主设备还必须已保存并确认共享配置，不能带有待确认的 `pendingSyncId`。查看设备、缺失修订、旧修订、未注册设备或配置待确认时在 HTTP 入口返回 403；这些手工操作不要求 automation lease。`/updates/status` 和 `/updates/check` 继续允许持 Token 的查看客户端使用。设备管理仍执行各自 CAS 规则，不能把这张手工写入权限表套用到 register/sync/primary。

库存与邀请请求进入主线程队列时携带不可变的写入许可，等待期间不持有权威锁；实际副作用在同一权威锁下重新验证并执行。因此等待期间发生切主或配置修订变化，旧请求会通过该操作的 `ok: false` / `error` 返回拒绝且不产生游戏写入；客户端不能把 HTTP 200 当作操作成功。已经开始的操作等待确定结果，不回滚或自动重放。下载与安排安装在入口接受一次授权意图，长网络等待不占用权威锁；其错误和最终安装状态由更新服务报告。

## 规范路由

下表是唯一方法矩阵。不要增加旧路径别名或用 GET 承担写操作。

### GET

| 路由 | 职责 |
| --- | --- |
| `/health` | 进程与 listener 存活状态；不证明 Token 或运行时可用 |
| `/local-api/config` | 本机 endpoint、LAN状态、实际可选网卡地址与Token；仅回环 |
| `/devices` | 当前客户端可见的设备权威状态 |
| `/snapshot` | 当前运行态主快照及 C# 订单显示排序；支持 `knownSignature` 与 `knownOrderPresentationSignature` |
| `/runtime-data` | 完整运行时目录 |
| `/game-media/status`、`/game-media/manifest`、`/game-media/image` | 图像资源初始化状态、清单与按精确标识读取的图片 |
| `/missions/tracked` | active-only 已追踪任务快照 |
| `/missions/available` | Unity 主线程 fresh read 的可接取任务快照 |
| `/automation/lease` | 当前客户端的 automation lease 状态 |
| `/business/status?protocolVersion=1` | C# 业务结果、自动化状态与当前性；只读后台缓存 |
| `/logs/settings` | 总日志和控制台状态 |
| `/logs/packages` | 本项目已导出诊断包列表及精确删除标识，不返回任意目录文件 |
| `/favorites` | 料理与酒水收藏 |
| `/custom-recipes` | 自定义推荐料理配置 |
| `/rare-guests/invitations` | 稀客邀请只读候选 |

邀请候选的 `locationStatus` 为 `current-map`、`not-in-current-map` 或 `unavailable`。位置只通过已验证的 `trackedNPCs[currentMap.Label][runtimeStringId]` 精确键读取；确切命中才填充 `sceneLabels/sceneNames`，未登记不表示在其它地图。`scope=all` 的位置读取允许明确降级，不继承当前地图的可见性或剩余行动过滤，也不改变按canonical客人ID执行的邀请资格与写入。`scope=current`仍要求位置读取成功；原有日间就绪和执行前场景校验保持。

### POST

| 分组 | 路由 |
| --- | --- |
| 设备权威 | `/devices/register`、`/devices/profile`、`/devices/primary`、`/devices/sync`、`/devices/sync-ack`、`/devices/rename`、`/devices/forget` |
| 自动化控制 | `/automation/lease/acquire`、`/automation/lease/release`、`/automation/barriers/ack` |
| 业务意图 | `/business/page-session/open`、`/business/page-session/update`、`/business/automation/retry` |
| 本机连接 | `/local-api/config` |
| 游戏资源 | `/game-media/reload` |
| 更新 | `/updates/status`、`/updates/check`、`/updates/download`、`/updates/install-on-exit` |
| 日志与诊断 | `/diagnostics/automation-decision`、`/logs/export-diagnostics`、`/logs/packages/delete`、`/logs/config`、`/logs/console`、`/logs/open-folder` |
| 运行时库存 | `/inventory/set`、`/inventory/bulk-set` |
| 订单 | `/orders/rare/dismiss` |
| 稀客邀请 | `/rare-guests/invite`、`/rare-guests/invite-all` |
| 收藏 | `/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` |
| 自定义料理 | `/custom-recipes/upsert`、`/custom-recipes/remove`、`/custom-recipes/settings`、`/custom-recipes/update-flags`、`/custom-recipes/move` |

设备权威、业务意图、人工确认 `/automation/barriers/ack`、本机连接配置与诊断包删除 POST 使用有界 JSON body 和 exact property set。租约获取/释放只读取身份及权威请求头，游戏资源重读不携带业务参数；其余端点有参数时按下文定义使用 URL query。新增协议不能同时保留 query、JSON 或别名多套写法。

### 本机局域网与连接凭据

`GET /local-api/config` 的 `availableLanEndpoints` 列出当前可选的实际网卡，`lanEndpoints` 描述当前监听地址；关闭LAN也可读取候选。`POST /local-api/config` 使用JSON `{lanEnabled?, lanHost?, token?}`，显式host必须仍在实际可选列表内，`auto`监听当前符合条件的全部私网网卡。自定义Token为8–128位非空白可见ASCII字符，省略表示保留，空值拒绝。所有字段先验证再应用，返回当前完整配置；旧随机重置路由移除。

本机管理仍要求回环、已注册主设备及精确Authority-Revision。当前客户端只在明确成功回执后用新凭据接续原设备身份，并保存至本机安全存储；迟到的旧连接请求或已替换的启动凭据不能恢复旧Token。失败或结果未知不自动重放修改请求。

### 诊断包管理

`GET /logs/packages` 返回`{ok, items, error}`，每项提供`id/fileName/byteLength/createdAtUtc`。`id`绑定文件名称、大小与修改时间，客户端不提交文件路径。`POST /logs/packages/delete`接受JSON `{id}`，重新核对当前列表身份，只删除本项目导出目录内符合格式的诊断ZIP；拒绝目录、重解析点、路径穿越、替代文件和失效标识。删除仍受主设备及精确权威修订校验，不清理原始日志。

日志保留文件数统一为1–30。客户端最近操作记录是本次运行内的有界交互历史，不替代服务端日志；不记录后台轮询、凭据或原始请求正文。

### 库存快捷调整

单条“−10/+10”使用 `/inventory/set` 的 `type`、`id`、`qty`，并携带非负整数 `expectedQuantity`。宿主在 Unity 主线程读取实际数量后比较预期值，一致才调用已有增减写入并核对结果；不一致时返回业务失败，库存保持原状，客户端提示刷新且不自动重试。参数缺失仍表示现有绝对数量设置，批量端点契约不变；未知、无限或不可修改库存继续拒绝，设备权限及场景检查不因快捷按钮放宽。

### 自定义料理双模式

`GET /custom-recipes` 只返回 `version: 2`，包含 `enabled`、`activeMode: customer|food`、非负整数 `modeRevision` 和 `recipes`。每条配方新增 `mode`；其余精确条目 ID、`customerId`、`foodId`、`recipeId`、点单 `foodTag`、加料、启用/置顶、顺序与时间字段保留。`foodId` 是料理目录 ID，`recipeId` 是独立游戏配方编号，客户端不可互换。

两模式独立保存，只使用当前模式的启用条目。客人模式按 `customerId` 管理，料理模式按 `foodId` 管理；料理模式中的每条记录仍须绑定一个明确 `customerId`，同料理可以保存多个客人的独立绑定。`foodTag: null` 只表示该绑定客人的全部点单标签，不表示全部客人。

| 写入端点 | 必填模式边界与作用域 |
| --- | --- |
| `/custom-recipes/settings` | `expectedModeRevision`；`enabled`、`activeMode` 至少一项。不需要 `mode` |
| `/custom-recipes/upsert` | `mode`、`expectedModeRevision` 和绑定字段；提供 `id` 时必须精确命中本模式已有记录 |
| `/custom-recipes/remove` | `mode`、`expectedModeRevision`、精确 `id` |
| `/custom-recipes/update-flags` | `mode`、`expectedModeRevision`、`scope`，并提供 `enabled` 或 `pinToTop`；`all` 只作用本模式，`customer` 仅用于客人模式，`recipe` 按 `foodId`，`entry` 按精确 `id` |
| `/custom-recipes/move` | `mode`、`expectedModeRevision`、`id`、`direction: up|down`；只移动本模式当前客人组或料理组 |

每次写入在同一存储锁内复核当前模式及其修订。模式实际切换才递增 `modeRevision`；开关或条目修改不递增。即使模式切换后又切回，旧弹窗也因旧修订被拒绝。失败返回 `ok: false` 与当前 `customRecipes`；客户端将业务失败作为错误处理，并重新读取后由用户决定重试，不自动重放旧写入。设备主权限与权威修订仍须满足上文约束，模式修订不能代替它们。

历史磁盘 version 1 在首次成功读取时迁入客人模式，初始化 `modeRevision: 0`；已保存绑定、顺序和开关不重算。迁移前将源文件原字节保存到同目录 `custom-recipes.json.v1.backup`，不覆盖已有备份；相同备份允许中断恢复，不同备份或备份失败会拒绝迁移并保留源文件。严格校验通过后才原子替换为 version 2，后续普通读取不写盘。未知版本、重复/缺失字段、错误 JSON 类型或负 ID 均保留原文件并明确报错，不修补成空配置。HTTP 不接受 version 1 写入契约。

`/orders/prepare-next`、`/orders/complete-first`、`/orders/normal/complete-first` 和 `/ui-pinning/targets` 已移除，返回 HTTP 404，不再接受客户端提交的执行方案。桌面客户端、Android 客户端和 Mod 必须成套升级。

## 纯 C# 业务协议

`modules/companion-business` 负责候选搜索、订单方案、特殊经营规则、UI 目标投影与自动化编排。`LocalApiServer.Business.cs` 是游戏进程内宿主：独立后台任务读取冻结快照和权威配置，HTTP 线程只排队意图和返回缓存，Unity 主线程只捕获事实与执行经复核的动作。

- `GET /business/status?protocolVersion=1` 返回 `isCurrent`、`pending`、`isCalculating`、`error`、`inputVersion`、`sourceSnapshotSignature`、推荐、运行时集合、游戏 UI 目标和自动化诊断。客户端不能把旧结果或错误状态解释为可执行结果。`pending` 表示尚无完整当前版本；`isCalculating` 仅表示后台此刻正在求解订单，不能作为方案新鲜度或权限依据。页头计算动画使用后者，已完成但非当前的方案保留静态参考提示，避免持续观察变化造成永久转圈。
- `POST /business/page-session/open` 只接受 `{protocolVersion: 1}`，为当前设备创建随机 `sessionId` 的空槽位，不登记查询。`POST /business/page-session/update` 接受 `{protocolVersion: 1, sessionId, sequence, intent}`；`sequence` 必须严格递增，`intent: null` 撤销查询，其余意图包含 `protocolVersion: 1` 和 `kind: normal|rare`，可附 `selectedPlace`、`customerId`、`foodTag`、`beverageTag`。文本最长 100 字符，稀客 ID 必须为非负整数。只传筛选意图；目录、库存、偏好和收藏均由宿主读取，客户端不能上传业务快照。
- 页面查询最多 16 个独立设备会话槽位，每周期按序处理一个。可见页轮询续期意图，意图 15 秒未续期清除、会话 60 秒未使用清除；全部使用单调时钟。离开页面、切条件、暂停、断线或资源门禁撤销时客户端发送空意图。撤销保留有界序号记录，迟到的旧请求不能恢复查询；会话过期或设备不匹配返回 409，更新不能隐式创建会话。旧 `/business/query` 已删除，客户端与 Mod 必须成套更新。
- 页面纯计算开始后不强制中断算法；计算结束会重新检查输入版本、会话和查询引用，撤销或过期后丢弃结果，后续轮次不再计算隐藏页。状态轮询不会登记查询，也不会撤销其它设备的可见页。
- 页面计算先于可能等待 Unity 主线程并触发新快照的自动化动作，避免每轮动作都使页面原帧失效而永久没有结果。
- `sourceContext` 标识推荐展示所属的游戏实例、场景代次、生命周期、目录、特殊经营目标及设备权威配置。同一上下文内可返回最后成功的展示值，并保持其原始 `sourceSnapshotSignature`；它仍可能是 `isCurrent: false`，不能据此执行动作或发布游戏辅助。上下文变化立即清除旧展示。
- 客户端的 `/snapshot`、业务状态和页面查询是独立轮询，不能以全量快照签名相等作为展示条件。客户端只核对来源上下文和查询意图；切换连接或场景后立即清空，稀客订单还须逐笔匹配精确身份。
- `POST /business/automation/retry` 只接受 `{protocolVersion, automationSessionId, businessGeneration, kind, key}`，其中人工交互协议 `protocolVersion` 为 2，`kind` 为 `rare|normal`，`key` 为精确订单标识。服务端同时核对快照会话、经营代次和真实 Active 生命周期；重试还需要当前主设备、精确权威 revision 和有效租约。实际动作由编排器根据最新事实重新决定。
- 后台读取不会更新设备在线时间，也不会自动取得或续期租约；现有客户端心跳继续承担在线许可。
- 同一经营代次的持续输入故障只撤销一次业务结果和 UI 目标；恢复发布后或经营代次变化后，新故障仍会重新撤销。错误原因变化仍记录诊断，不能用日志限流替代状态处理。
- 游戏 UI 投影和共享偏好保留 `#RRGGBB` 颜色；宿主在适配边界转换为既有游戏目标协议的 `RRGGBB`，仍使用严格的大写十六进制解析器。
- 候选缓存仅存完整值参数对应的纯计算结果；订单身份、代次、执行许可和副作用结果不缓存。输入变化会撤销旧帧，动作排队和主线程执行前再次核对版本。

协议版本不匹配时明确提示成套升级；不存在客户端算法回退或两个自动化 writer。

## 设备配置权威

`CompanionDeviceAuthorityStore` 是共享功能配置的唯一权威；窗口主题、字体、连接地址等本地 UI 偏好不进入该 profile。

协议版本有不同职责，不能用一个版本号替代所有校验：

| 边界 | 当前版本 | 实现来源 |
| --- | --- | --- |
| 设备注册、配置与 CAS 协议 | `protocolVersion: 1` | `CompanionDeviceAuthorityStore.ProtocolVersion` |
| 共享 profile 与设备注册表磁盘格式 | `profileSchemaVersion: 4` / `version: 4` | `CompanionDeviceAuthorityStore`、Dart `CompanionProfile` |
| 推荐查询与业务状态 | `protocolVersion: 1` | `modules/companion-contracts/BusinessProtocol.cs` |
| 暂停订单重试与人工确认 | `protocolVersion: 2` | `modules/companion-contracts/ClientProtocol.cs` |

设备注册表保存于 `BepInEx/config/MystiaStewardCompanion/companion-devices.json`。已知磁盘 v1/v2/v3 先严格验证原字段、修订和 profile hash，再一次性原子迁入 v4：v1 从历史游戏辅助分项推导总开关，v1/v2 删除已取消的 `serviceOrderSortMode`；三种旧版评分参数与其预设不一致时仅将模式改为 `custom`，保留全部参数。迁移保留设备身份与主设备选择，为各设备更新 profile revision/hash 和待同步标识；有设备时同时推进 authority/state revision，客户端须保存并确认新配置。损坏或未来版本保留原文件并拒绝使用，HTTP 不接受旧 schema。

Dart 的 `CompanionProfile.fromCheckpoint` 只负责本机历史 checkpoint 迁移，作为注册初值使用；只有这个明确的历史入口将缺失版本解释为 v1。服务端磁盘文件与 HTTP 均要求显式版本，不能套用此规则补全损坏数据。

- 第一个成功注册的设备成为初始主设备，不因离线自动转移。
- 只有当前主设备能通过 `expectedAuthorityRevision + expectedProfileRevision` 更新生效 profile。
- 设置主设备、同步、忘记设备等操作使用 `expectedAuthorityRevision` 做 CAS；冲突必须刷新后重试，不做 last-write-wins 合并。
- “同步配置”是主设备整份 profile 覆盖目标非主设备，不合并字段；目标通过 sync ID、profile revision 和 hash 确认应用结果。
- 损坏、未知未来 schema 或不完整 JSON fail-closed；持久化使用原子写入。收藏和自定义配方分别位于同一配置目录的 `favorites.json`、`custom-recipes.json`，不合并进设备 profile；自定义配方迁移边界见上文。

主设备或生效 profile 变化在同一 authority transition 锁内提交，并原子执行：

1. 推进 authority revision。
2. 撤销旧 automation lease。
3. 推进 automation command epoch，取消尚未开始的旧命令。
4. 发布新的自动化 profile。
5. 清空旧游戏 UI operational targets，但保留需要由 Unity 主线程安全处理的页面登记。

自动化暂停与恢复语义见 [自动化运行时](automation-runtime.md)。

## Automation lease

只有当前主设备、精确 authority revision 可以取得或续约 automation lease。lease 的 TTL 为 15 秒：

- 同一设备和 revision 的 acquire 用于续约。
- 另一设备或不同 revision 不能接管尚未失效的 lease。
- 从无 lease 状态第一次取得控制、显式释放、主设备切换或 profile revision 变化会推进 command epoch。lease 过期会立即撤销控制许可；下一次从空状态取得控制时再推进 epoch。
- release 只撤销未来副作用权限并推进 epoch，不删除活动 cooking job。
- C# 宿主派发订单动作时要求有效 lease，并把验证后的 epoch 和输入版本检查器写入主线程命令。
- `POST /automation/barriers/ack` 必须由当前 lease owner 提交完整 JSON `{protocolVersion: 2, automationSessionId, businessGeneration, sequence}`，`sequence` 为正整数。服务端核对快照会话、经营代次与真实 Active 生命周期；不接受旧 query sequence 格式。客户端还会保留打开确认界面时的会话和经营代次，确认期间边界变化则在发送前拒绝，不能把旧确认重新绑定到后来重用的事件序号。

运行时阶段 permit 与 job 行为由 [自动化运行时](automation-runtime.md) 定义。

## 快照与缓存协议

`recommendationState.inventoryReadReady` 参与正式快照内容签名，其变化不能被 `unchanged` 缓存掩盖。缺失或 false 不得归一化为已读取；真实数量、稀疏零值和经营兜底的含义由[运行时 Provider](runtime-provider.md)定义，不由客户端或 HTTP 缓存猜测。

`/snapshot` 和任务端点的 `knownSignature` 只用于压缩响应，不能跳过业务要求的 fresh read。规范内容签名固定为 64 字符小写 SHA-256，不把随订单增长的原文放进 query。

`/snapshot` 另有 `orderPresentationSignature` 显示令牌；客户端同时回传 `knownOrderPresentationSignature`，两项均相同才返回 `unchanged`。当前令牌为 `order-v2:time:<snapshotSignature>`，仅用于显示缓存，不能代替 `snapshotSignature` 参与业务 current 或权限判断。HTTP 读取端固定按点单时间重排当前订单，同刻按精确身份稳定排序；有效订单获得跨普客/稀客通道从 0 开始的整数 `serviceOrderPosition`，专注页只据此合并，不在 Dart 重算业务排序。错误或不完整的订单读取保持原形。投影按原正文与签名缓存单份结果，不改原始业务输入，不等待方案求解，也不在 Unity 主线程额外解析 JSON。该顺位是纯显示字段，不参与需求联结、动作身份或执行许可；自动化和游戏辅助仍独立遵守特殊经营执行优先级。

完整 `RuntimeDataCatalog` 不嵌入主快照，而由 `/runtime-data` 单独返回。主快照只携带完整性、来源、状态和签名；伴随窗口在本地无缓存或签名变化时获取目录。主快照的签名排除捕获时间和性能数字，但包含会改变 UI 与动作判断的经营 generation、订单、自动化 job/event、门禁和目录身份。

`/missions/available` 每次 GET 都进入 Unity 主线程 fresh read；`knownSignature` 只允许返回 unchanged 结果，不能复用旧资格判断。任务业务规则由对应任务专题和测试维护，本页只定义传输边界。

## 生命周期与错误

shutdown 先取消业务循环并撤销旧输入，再停止接收新客户端、通知更新服务取消、关闭在途 socket、等待 handler、最后释放更新服务。Unity 主线程不等待可能正在排队等待它的业务任务；版本栅栏阻止取消后的任务提交动作。资源释放或诊断失败不能泄漏 handler 槽位，也不能让 worker 无限重启。

传输层使用明确 HTTP 状态处理协议错误；业务层可能以 200 返回结构化 `ok=false`、`error`、outcome 或 unavailable 状态。客户端必须读取结构化响应，不能仅凭 HTTP 200 推断副作用成功。

已删除并禁止恢复的路径包括：

- 根路径健康检查别名
- `/api/*` 别名
- `/automation/cancel`
- `/automation/jobs/cancel`
- `/ui-pinning/target` 单目标旧路由

## 修改与验证

修改 listener、请求解析、路由或设备权威时至少运行：

```bash
dotnet run --project tests/local-api-listener-lifecycle/LocalApiListenerLifecycleSmoke.csproj -c Release
dotnet run --project tests/local-api-client-handlers/LocalApiClientHandlersSmoke.csproj -c Release
dotnet run --project tests/local-api-method-matrix/LocalApiMethodMatrixSmoke.csproj -c Release
dotnet run --project tests/local-api-storage/LocalApiStorageSmoke.csproj -c Release
dotnet run --project tests/main-thread-command/MainThreadCommandSmoke.csproj -c Release
dotnet run --project tests/snapshot-signature/SnapshotSignatureSmoke.csproj -c Release
```

更新端点还需运行：

```bash
dotnet run --project tests/update-protocol/UpdateProtocolSmoke.csproj -c Release
```

本机连接凭据交接、诊断包文件边界及客户端续接由 `tests/flutter-contracts/` 的隔离 HTTP 宿主与 Dart 契约覆盖；具体构建和运行顺序见[验证指南](validation-guide.md)。它们不等同于实际局域网网卡切换、设备断连或真实游戏验证。
