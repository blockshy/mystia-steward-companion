# 本地 API

更新日期：2026-10-10

本文档只定义游戏进程内本地 HTTP API 的监听、鉴权、设备权威、方法矩阵、请求生命周期和传输边界。运行时数据含义见 [运行时 Provider](runtime-provider.md)，订单身份见 [运行时订单生命周期](runtime-order-lifecycle.md)，自动化状态机见 [自动化运行时](automation-runtime.md)。

## 监听模型

`LocalApiServer` 使用 `TcpListener` 实现轻量 HTTP 服务，不引入额外 Web 框架。

- `LocalApi.Enabled=true` 时，服务先绑定 `127.0.0.1` 作为本机入口；总开关关闭时不启动任何 listener。
- LAN listener 是附加通道，不能取代回环 listener。它既可由插件启动时读取的 `AllowLanConnections` 配置启用，也可由回环客户端更新本机连接配置后启用。
- `LanHost=auto` 只选择合格的私网 IPv4；服务拒绝公网来源。
- LAN 配置变化时串行停止旧 worker，再启动新地址；配置与地址集合未变化时不重启。
- 每个 listener 拥有独立停止状态和阻塞 accept 线程。主动停止导致的 accept 异常直接结束；意外异常最多报告一次并终止该 worker，不做无限重试。
- 客户端 handler 上限为 16。停止时先拒绝新连接并关闭在途 socket，再有界等待 handler 退出。

不要把 API 端口映射到公网。正式 Tauri 客户端通过 Rust 原生 TCP 代理访问，不依赖 WebView 或系统 HTTP 代理。浏览器开发模式只应直连 mock API；当前真实 Mod 的 CORS allowlist 不包含 authority revision header，因此浏览器直连不能承担设备权威写请求。

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

Token 只证明能访问 Mod；它不代表设备是主设备，也不代表持有自动化控制权。`/local-api/config`、Token 重置和 BepInEx 控制台显隐只允许游戏电脑的回环客户端调用。

## 规范路由

下表是唯一方法矩阵。不要增加旧路径别名或用 GET 承担写操作。

### GET

| 路由 | 职责 |
| --- | --- |
| `/health` | 进程与 listener 存活状态；不证明 Token 或运行时可用 |
| `/local-api/config` | 本机 endpoint、LAN 状态与 Token；仅回环 |
| `/devices` | 当前客户端可见的设备权威状态 |
| `/snapshot` | 缓存的运行态主快照；支持 `knownSignature` |
| `/runtime-data` | 完整运行时目录 |
| `/missions/tracked` | active-only 已追踪任务快照 |
| `/missions/available` | Unity 主线程 fresh read 的可接取任务快照 |
| `/automation/lease` | 当前客户端的 automation lease 状态 |
| `/business/status?protocolVersion=1` | C# 业务结果、自动化状态与当前性；只读后台缓存 |
| `/logs/settings` | 总日志和控制台状态 |
| `/favorites` | 料理与酒水收藏 |
| `/custom-recipes` | 自定义推荐料理配置 |
| `/rare-guests/invitations` | 稀客邀请只读候选 |

### POST

| 分组 | 路由 |
| --- | --- |
| 设备权威 | `/devices/register`、`/devices/profile`、`/devices/primary`、`/devices/sync`、`/devices/sync-ack`、`/devices/rename`、`/devices/forget` |
| 自动化控制 | `/automation/lease/acquire`、`/automation/lease/release`、`/automation/barriers/ack` |
| 业务意图 | `/business/query`、`/business/automation/retry` |
| 本机连接 | `/local-api/config`、`/local-api/token/regenerate` |
| 更新 | `/updates/status`、`/updates/check`、`/updates/download`、`/updates/install-on-exit` |
| 日志与诊断 | `/diagnostics/automation-decision`、`/logs/export-diagnostics`、`/logs/config`、`/logs/console`、`/logs/open-folder` |
| 运行时库存 | `/inventory/set`、`/inventory/bulk-set` |
| 订单 | `/orders/rare/dismiss` |
| 稀客邀请 | `/rare-guests/invite`、`/rare-guests/invite-all` |
| 收藏 | `/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` |
| 自定义料理 | `/custom-recipes/upsert`、`/custom-recipes/remove`、`/custom-recipes/settings`、`/custom-recipes/update-flags`、`/custom-recipes/move` |

设备权威与业务意图 POST 使用有界 JSON body 和 exact property set。其余当前端点使用 URL query；新增协议不能同时保留 query、JSON 或别名多套写法。

`/orders/prepare-next`、`/orders/complete-first`、`/orders/normal/complete-first` 和 `/ui-pinning/targets` 只返回 HTTP 409 升级拒绝响应，不再执行客户端提交的方案。桌面客户端、Android 客户端和 Mod 必须成套升级。

## 纯 C# 业务协议

`modules/companion-business` 负责候选搜索、订单方案、特殊经营规则、UI 目标投影与自动化编排。`LocalApiServer.Business.cs` 是游戏进程内宿主：独立后台任务读取冻结快照和权威配置，HTTP 线程只排队意图和返回缓存，Unity 主线程只捕获事实与执行经复核的动作。

- `GET /business/status?protocolVersion=1` 返回 `isCurrent`、`pending`、`error`、`inputVersion`、`sourceSnapshotSignature`、推荐、运行时集合、游戏 UI 目标和自动化诊断。客户端不能把旧结果或错误状态解释为可执行结果。
- `POST /business/query` body 必须含 `protocolVersion: 1` 和 `kind: normal|rare`，可附 `selectedPlace`、`customerId`、`foodTag`、`beverageTag`。文本最长 100 字符，稀客 ID 必须为非负整数。只传筛选意图；目录、库存、偏好和收藏均由宿主读取，客户端不能上传业务快照。
- 页面查询按设备与种类合并，最多 16 个槽位，每周期按序处理一个；结果必须重新通过输入版本检查，过期结果不会作为当前结果返回。
- `POST /business/automation/retry` 只接受 `{protocolVersion, kind, key}`；重试还需要当前主设备、精确权威 revision 和有效租约。实际动作由编排器根据最新事实重新决定。
- 后台读取不会更新设备在线时间，也不会自动取得或续期租约；现有客户端心跳继续承担在线许可。
- 同一经营代次的持续输入故障只撤销一次业务结果和 UI 目标；恢复发布后或经营代次变化后，新故障仍会重新撤销。错误原因变化仍记录诊断，不能用日志限流替代状态处理。
- 游戏 UI 投影和共享偏好保留 `#RRGGBB` 颜色；宿主在适配边界转换为既有游戏目标协议的 `RRGGBB`，仍使用严格的大写十六进制解析器。
- 候选缓存仅存完整值参数对应的纯计算结果；订单身份、代次、执行许可和副作用结果不缓存。输入变化会撤销旧帧，动作排队和主线程执行前再次核对版本。

协议版本不匹配时明确提示成套升级；不存在客户端算法回退或两个自动化 writer。

## 设备配置权威

`CompanionDeviceAuthorityStore` 是共享功能配置的唯一权威；窗口主题、字体、连接地址等本地 UI 偏好不进入该 profile。

- 第一个成功注册的设备成为初始主设备，不因离线自动转移。
- 只有当前主设备能通过 `expectedAuthorityRevision + expectedProfileRevision` 更新生效 profile。
- 设置主设备、同步、忘记设备等操作使用 `expectedAuthorityRevision` 做 CAS；冲突必须刷新后重试，不做 last-write-wins 合并。
- “同步配置”是主设备整份 profile 覆盖目标非主设备，不合并字段；目标通过 sync ID、profile revision 和 hash 确认应用结果。
- 损坏、未知未来 schema 或不完整 JSON fail-closed；持久化使用原子写入。

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
- barrier ack 还必须由当前 lease owner 按正 sequence 发起。

运行时阶段 permit 与 job 行为由 [自动化运行时](automation-runtime.md) 定义。

## 快照与缓存协议

`/snapshot` 和任务端点的 `knownSignature` 只用于压缩响应，不能跳过业务要求的 fresh read。规范内容签名固定为 64 字符小写 SHA-256，不把随订单增长的原文放进 query。

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
