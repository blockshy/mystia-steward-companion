# 本地 API

更新日期：2026-09-02

本文档只定义游戏进程内本地 HTTP API 的监听、鉴权、主设备与共享配置、方法矩阵、请求生命周期和传输边界。游戏数据含义见[游戏数据提供器](runtime-provider.md)，订单标识见[运行时订单生命周期](runtime-order-lifecycle.md)，自动化状态机见[自动化运行时](automation-runtime.md)。

## 监听模型

`LocalApiServer` 使用 `TcpListener` 实现轻量 HTTP 服务，不引入额外 Web 框架。

- `LocalApi.Enabled=true` 时，服务先绑定 `127.0.0.1` 作为本机入口；总开关关闭时不启动任何监听器。
- LAN 监听器是附加通道，不能取代回环监听器。它既可由插件启动时读取的 `AllowLanConnections` 配置启用，也可由回环客户端更新本机连接配置后启用。
- `LanHost=auto` 只选择合格的私网 IPv4；服务拒绝公网来源。
- LAN 配置变化时串行停止旧工作线程，再启动新地址；配置与地址集合未变化时不重启。
- 每个监听器拥有独立停止状态和阻塞接收线程。主动停止导致的接收异常直接结束；意外异常最多报告一次并终止该工作线程，不做无限重试。
- 客户端处理器上限为 16。停止时先拒绝新连接并关闭正在处理的套接字，再按时间上限等待处理器退出。

不要把 API 端口映射到公网。正式 Tauri 客户端通过 Rust 原生 TCP 代理访问，不依赖 WebView 或系统 HTTP 代理。浏览器开发模式只应直连模拟 API；真实 Mod 的 CORS 允许列表包含规范客户端、Token 与配置修订号请求头，但这不改变回环/局域网来源、Token、主设备和修订号校验。

## HTTP 与请求上限

服务只接受 `GET`、`POST` 与预检 `OPTIONS`：

- `GET` 只读状态。
- `POST` 承担文件、配置、运行时、网络更新和进程相关操作。
- `OPTIONS` 返回 204。
- 其他方法返回 405；未知路径或错误方法组合返回 404。
- 根路径 `/` 不映射健康检查，`/api/*` 不作为规范路径别名。

请求头必须在 32 KiB 内完整出现 `CRLFCRLF`，请求体上限为 64 KiB。EOF 截断返回 400，请求头超限返回 431，请求体超限返回 413。`Transfer-Encoding`、重复或非法 `Content-Length` 均被拒绝；同次读取已经进入缓冲区、但超出声明长度的字节也会被拒绝。每个连接只处理一个请求并在响应后关闭，不支持流水线。设备协议请求体必须是严格 UTF-8 JSON，最大解析深度 16，不接受注释、尾随逗号、缺项或额外字段。

单个 socket 的收发超时为 2.5 秒。需要访问 Unity/IL2CPP 的命令会进入有界主线程队列：未开始命令超时后取消，主线程恢复时不得迟到执行；已经开始的命令等待确定结果，避免客户端因传输超时重复执行已经发生的游戏操作。

## 鉴权与设备标识头

只有 `GET /health` 无需 Token。其他所有端点要求：

```text
X-Mystia-Steward-Companion-Token: <token>
```

设备与自动化协议另外使用：

| Header | 语义 |
| --- | --- |
| `X-Mystia-Steward-Companion-Client-Id` | 16–64 位 ASCII 字母、数字或 `-` 组成的稳定设备 ID |
| `X-Mystia-Steward-Companion-Client-Label` | 用户可读设备名，服务端限制为 48 字符 |
| `X-Mystia-Steward-Companion-Authority-Revision` | 当前主设备的正数配置版本；运行时写入请求必须精确匹配 |

Token 只证明能访问 Mod；它不代表设备是主设备，也不代表持有自动化控制权。`/local-api/config`、Token 重置和 BepInEx 控制台显隐只允许游戏电脑的回环客户端调用。

## 规范路由

下表是唯一方法矩阵。不要增加旧路径别名或用 GET 承担写操作。

### GET

| 路由 | 职责 |
| --- | --- |
| `/health` | 进程与监听器存活状态；不证明 Token 有效或游戏数据可用 |
| `/local-api/config` | 本机端点、LAN 状态与 Token；仅回环 |
| `/devices` | 当前客户端可见的主设备和共享配置状态 |
| `/snapshot` | 缓存的当前状态主快照；支持 `knownSignature` |
| `/runtime-data` | 完整游戏数据目录 |
| `/missions/tracked` | 仅包含活动项的已追踪任务快照 |
| `/missions/available` | Unity 主线程重新读取的可接取任务快照 |
| `/automation/lease` | 当前客户端的自动化控制权状态 |
| `/logs/settings` | 总日志和控制台状态 |
| `/favorites` | 料理与酒水收藏 |
| `/custom-recipes` | 自定义推荐料理配置 |
| `/rare-guests/invitations` | 稀客邀请只读候选 |

### POST

| 分组 | 路由 |
| --- | --- |
| 设备与共享配置 | `/devices/register`、`/devices/profile`、`/devices/primary`、`/devices/sync`、`/devices/sync-ack`、`/devices/rename`、`/devices/forget` |
| 自动化控制 | `/automation/lease/acquire`、`/automation/lease/release`、`/automation/barriers/ack` |
| 本机连接 | `/local-api/config`、`/local-api/token/regenerate` |
| 更新 | `/updates/status`、`/updates/check`、`/updates/download`、`/updates/install-on-exit` |
| 日志与诊断 | `/diagnostics/automation-decision`、`/logs/export-diagnostics`、`/logs/config`、`/logs/console`、`/logs/open-folder` |
| 运行时库存 | `/inventory/set`、`/inventory/bulk-set` |
| 订单 | `/orders/prepare-next`、`/orders/complete-first`、`/orders/normal/complete-first`、`/orders/rare/participation` |
| 稀客邀请 | `/rare-guests/invite`、`/rare-guests/invite-all` |
| 游戏 UI 目标 | `/ui-pinning/targets` |
| 收藏 | `/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` |
| 自定义料理 | `/custom-recipes/upsert`、`/custom-recipes/remove`、`/custom-recipes/settings`、`/custom-recipes/update-flags`、`/custom-recipes/move` |

设备配置 POST 与 `/orders/rare/participation` 使用有界 JSON 请求体，并要求属性集合完全匹配。其余当前端点使用 URL
查询参数；新增协议不能同时保留查询参数、JSON 或别名多套写法。队列变更的规范请求体为：

```text
expectedAuthorityRevision
+ expectedBusinessGeneration
+ expectedParticipationRevision
+ action: pause | enable-tail | enable-front
+ target:
    { type: guest, guestId, expectedCurrentOrders: 精确订单标识[] }
  | { type: order, order: 精确订单标识 }
```

稀客目标必须与该稀客当前完整订单实例集合执行写入前状态检查，订单目标必须精确命中一个当前订单实例；
配置修订号请求头和请求体、经营轮次或队列修订号任一不匹配均返回 409。`enable-front` 使用的
当前稀客 UI 目标与缓存为活动状态的稀客料理任务候选只由服务端读取，不进入客户端请求体；服务端再按同一
队列快照分类，只有仍参与的当前候选需要保持在新订单之前，已暂停候选排除。请求缺项、额外字段、非法
操作/目标组合或不完整订单标识返回 400。

## 主设备与共享配置

`CompanionDeviceAuthorityStore` 是共享功能配置的唯一生效来源；窗口主题、字体、连接地址等本地 UI 偏好不进入共享配置。

- 当前线上配置结构与 `companion-devices.json` 存储结构均为 v3。配置必须包含严格布尔值 `rareGuestParticipationModuleEnabled` 和规范 `managedRareGuestIds`。
- 存储格式 v1/v2 只在加载时执行一次不可分割的迁移并立即写回 v3：v1 增加空名单，v2 保留已有名单，两者都把模块设为关闭。迁移前必须由固定的版本描述完整校验存储外层对象、设备记录和配置；未知、缺失、`null` 或额外字段均拒绝加载，损坏或未知版本不写回。
- 第一个成功注册的设备成为初始主设备，不因离线自动转移。
- 只有当前主设备能通过 `expectedAuthorityRevision + expectedProfileRevision` 更新生效配置。
- 设置主设备、同步、忘记设备等操作使用 `expectedAuthorityRevision` 做状态比较后写入（CAS）；冲突必须刷新后重试，不做最后写入覆盖。
- “同步配置”是主设备整份配置覆盖目标非主设备，不合并字段；目标通过同步 ID、配置修订号和哈希确认应用结果。
- 损坏、未知未来结构或不完整 JSON 一律拒绝加载；持久化使用不可分割的写入过程。

主设备或生效配置的变化在同一个状态转换临界区内提交，并作为一个整体执行：

1. 推进配置修订号。
2. 撤销旧自动化控制权。
3. 推进自动化命令轮次，取消尚未开始的旧命令。
4. 发布新的自动化配置。
5. 根据模块开关应用生效名单：模块关闭时生效名单为空但配置名单保留；切换主设备时撤销所有当前人工启用状态，普通共享配置更新只处理生效名单差异。
6. 清空旧游戏 UI 执行目标，并按新的参与状态过滤仍打开页面的稀客展示目标；页面登记保留。

自动化暂停与恢复语义见 [自动化运行时](automation-runtime.md)。

## 自动化控制权

只有当前主设备且配置修订号精确匹配时，才可以取得或续约自动化控制权。控制权的 TTL 为 15 秒：

- 同一设备和修订号再次取得控制权用于续期。
- 另一设备或不同修订号不能接管尚未失效的控制权。
- 从无控制权状态第一次取得控制、显式释放、主设备切换或配置修订号变化时推进命令轮次。控制权过期会立即撤销执行许可；下一次从无控制权状态取得控制时再次推进命令轮次。
- 释放控制权只撤销未来游戏写操作的权限并推进命令轮次，不删除活动料理任务。
- 三个订单动作端点要求有效控制权，并把验证后的命令轮次写入主线程命令。
- 待人工确认事件还必须由当前控制设备按大于零的事件序号确认。

运行时阶段执行许可与料理任务行为由 [自动化运行时](automation-runtime.md) 定义。

## 快照与缓存协议

`/snapshot` 和任务端点的 `knownSignature` 只用于压缩响应，不能跳过业务要求的重新读取。规范内容签名固定为 64 字符小写 SHA-256，不把随订单增长的原文放进查询参数。

完整 `RuntimeDataCatalog` 不嵌入主快照，而由 `/runtime-data` 单独返回。主快照只携带完整性、来源、状态和签名；伴随窗口在本地无缓存或签名变化时获取目录。主快照的签名排除捕获时间和性能数字，但包含会改变 UI 与动作判断的经营轮次、订单、稀客队列修订号和连续 `queuePosition` 队列、自动化任务/事件、执行条件和目录标识。

`/missions/available` 每次 GET 都进入 Unity 主线程重新读取；`knownSignature` 只允许返回未变化结果，不能复用旧资格判断。任务业务规则由对应任务专题和测试维护，本页只定义传输边界。

## 生命周期与错误

监听器关闭顺序固定为：停止接收新客户端、通知更新服务取消、关闭在途 socket、等待处理器，最后释放更新服务。资源释放或诊断失败不能泄漏处理器槽位，也不能让工作线程无限重启。

传输层使用明确 HTTP 状态处理协议错误；业务层可能以 200 返回结构化 `ok=false`、`error`、结果或不可用状态。客户端必须读取结构化响应，不能仅凭 HTTP 200 推断游戏操作成功。

已删除并禁止恢复的路径包括：

- 根路径健康检查别名
- `/api/*` 别名
- `/automation/cancel`
- `/automation/jobs/cancel`
- `/orders/rare/dismiss`
- `/ui-pinning/target` 单目标旧路由

## 修改与验证

修改监听器、请求解析、路由、主设备或共享配置时至少运行：

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
