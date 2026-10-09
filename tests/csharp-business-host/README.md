# C# 业务宿主离线验证

本项目直接链接生产 `LocalApiServer.Business.cs`、设备权威存储、收藏/自定义配方存储、HTTP 正文解析、夜间生命周期状态机、UI 发布会话门禁和业务类库。游戏适配委托与最终 UI 提交替换为受控纯托管边界，不启动游戏或 TCP 服务，也不读取真实配置。它验证的是业务宿主线程与值协议；实际 Unity 反射、游戏行为和网络监听仍需各自验证。

从仓库根目录运行：

```powershell
dotnet restore tests/csharp-business-host/CSharpBusinessHost.csproj --ignore-failed-sources -p:NuGetAudit=false
dotnet build tests/csharp-business-host/CSharpBusinessHost.csproj -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --ui-failure-transition
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --lease-expiry
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --ui-colors
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --display-continuity
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --daytime-ui-lifecycle
```

测试覆盖：无主设备/无租约、协议拒绝、后台读取不延长在线状态或租约、页面队列容量及公平轮转、收藏变化、排队后输入失效、目录不可用、特殊目标当前代次绑定、重复身份拒绝、候选缓存隔离及容量、停止时不等待主线程委托。

`--ui-failure-transition` 使用真实后台循环和 UI 边界计数，验证缺少主设备、持续损坏输入及错误原因变化不会重复撤销目标或自行推进业务版本；经营代次变化仅重新撤销一次，输入恢复后会重新发布目标，再次发生故障仍正确撤销。它检查实际状态迁移，不以日志被隐藏作为通过条件。

`--ui-colors` 链接真实游戏目标解析器、颜色类型和不可变目标快照，覆盖稀客/普客同时或分别开启、全部关闭、默认/自定义颜色、精确订单身份、五类功能标志、材料及缺失 ID。业务结果保留 `#RRGGBB`，游戏目标使用 `RRGGBB`；两个协议槽位均须拒绝带前缀、小写、错误长度、非十六进制或空白颜色。仅最终 Unity 发布使用记录替身，不加载游戏。

`--display-continuity` 在动作替身阻塞时每250毫秒发布观察更新，验证页面先于动作发布、经营稀客与页面保留上次成功结果、原来源签名不变且仍为非当前。会话、场景、生命周期、目录、配置、特殊经营语义或查询意图变化必须立即清空旧展示；保留展示不能恢复旧命令执行许可。该模式验证只读展示连续性，不代表旧推荐仍满足最新库存或订单状态。

`--daytime-ui-lifecycle` 使用生产 `NightBusinessLifecycleTracker` 与 `RuntimeUiTargetSessionGuard`，覆盖从未营业的白天、Closing、Destroyed、下一代营业、缺失/未知阶段、快照落后及发布中切场。非营业期稀客/普客查询必须完成，不能提交空夜间目标或反复创建撤销屏障；真实 Active 发布失败仍报错并禁止动作，排队动作在没有新快照时也须因关闭营业而失去许可。附加 `--expect-before-fix` 仅用于旧实现的失败复现，正常验证不使用该参数。

`--serve` 提供离线浏览器 mock 使用的 JSONL 桥接。每行请求带 `id` 和 `operation`；响应为 `{id,ok,result}` 或 `{id,ok:false,error}`。操作包括：

- `initialize`：`clientId`、完整共享 `profile`，建立测试主设备并启动后台业务循环。
- `publish`：`snapshot`、原始运行时 `catalog`，可附 `profile`、`favorites`、`customRecipes`；快照签名采用 `snapshot.snapshotSignature`。可信 mock 必须明确提供 `nightBusinessGeneration` 与精确 `nightBusinessLifecyclePhase`，桥接据此驱动真实托管生命周期；实际目标入口仍执行与生产相同的严格门禁。
- `status`：`protocolVersion`，获取真实后台状态。
- `query`：`clientId`、`intent`，提交页面查询并读取结果。
- `heartbeat`：只刷新测试主设备在线时间，不延长自动化租约。
- `lease`：`owned`，仅用于门禁测试；即使主动授予，游戏替身也始终返回取消，不能操作真实游戏。

测试配置只写入仓库忽略的 `temp/mystia-business-*` 独占目录，正常结束时清理。

`--lease-expiry` 在托管替身中扣住排队命令，等待短租约自然到期，验证没有新快照、没有主动 prune 时也不允许执行，且续约不能追认旧命令。它不等待或调用真实游戏主线程。

大目录持续流验证先运行 `node tests/csharp-business-orders/benchmark.mjs --write-fixture`，再运行本宿主 `--load-stream` 和 `--load-stream --client-poll`。前者每10毫秒读取状态和页面，后者使用当前前端的750毫秒读取周期；两者都持续每250/500毫秒更新观察时间、送达状态与展示进度，并要求经营和页面均发布多个当前版本。库存改变必须立即撤销旧结果，重算后使用新签名。输出含缓存容量/命中计数，便于区分冷计算、淘汰和读竞争；该验证依赖当前机器速度，应串行运行。
