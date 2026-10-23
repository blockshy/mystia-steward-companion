# C# 业务宿主离线验证

本项目直接链接生产 `LocalApiServer.Business.cs`、`LocalApiServer.PageQueries.cs`、设备权威存储、收藏/自定义配方存储、HTTP 正文解析、夜间生命周期状态机、UI 发布会话门禁和业务类库。游戏适配委托与最终 UI 提交替换为受控纯托管边界，不启动游戏或 TCP 服务，也不读取真实配置。它验证的是业务宿主线程与值协议；实际 Unity 反射、游戏行为和网络监听仍需各自验证。

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
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --input-wake
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --input-reuse
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --page-subscriptions
dotnet tests/csharp-business-host/bin/Release/net6.0/CSharpBusinessHost.dll --game-media-gate
```

测试覆盖：无主设备/无租约、协议拒绝、后台读取不延长在线状态或租约、页面队列容量及公平轮转、收藏变化、排队后输入失效、目录不可用、特殊目标当前代次绑定、重复身份拒绝、候选缓存隔离及容量、停止时不等待主线程委托。

`--page-subscriptions` 覆盖新的 `/business/page-session/open` 与 `/business/page-session/update`：打开只创建空槽，更新绑定设备、随机会话与递增序号，`intent:null` 显式撤销。用真实宿主验证旧序号不能复活、设备不能相互撤销、意图到期后的新快照不再计算、会话到期不能由更新重建；只推进测试槽位的截止时间，不等待真实十五秒。默认领域测试的 `Query` 便捷方法也通过新协议，生产旧 `/business/query` 不再提供。

`--input-reuse` 验证完全相同原始输入的解析树与订单结果复用，同时保留实时权威和租约推进；分配对照只描述隔离托管路径，不换算为真实游戏 FPS。

`--input-wake` 在真实后台循环的受控采集边界制造突发输入，验证合并通知只发布最终快照、每次新输入都撤销旧结果、库存变化重新计算，以及停止和在途通知不能恢复执行资格；持续吞吐另由 `--load-stream` 测量。

`--game-media-gate` 在已排队的生产命令许可闭包前暂停托管动作，验证图片资源重读立即使旧命令失效；资源恢复不能追认旧版本命令，不依赖客户端先释放租约。它不提取真实游戏图片，也不执行 Unity 写入。

`--warm-pipeline` 先用 `node tests/csharp-business-orders/benchmark.mjs --write-fixture` 生成大目录，再对八种点单的完整结果运行旧复制边界与生产冻结组件路径。验证完整响应、公开读取隔离、四个 HTTP 读者与唯一发布者的并发一致性、新输入立即失效，并测量12轮（含状态正文）的线程累计分配。没有启动游戏或授予执行租约。

`--ui-failure-transition` 使用真实后台循环和 UI 边界计数，验证缺少主设备、持续损坏输入及错误原因变化不会重复撤销目标或自行推进业务版本；经营代次变化仅重新撤销一次，输入恢复后会重新发布目标，再次发生故障仍正确撤销。它检查实际状态迁移，不以日志被隐藏作为通过条件。

`--ui-colors` 链接真实游戏目标解析器、颜色类型和不可变目标快照，覆盖稀客/普客同时或分别开启、全部关闭、默认/自定义颜色、精确订单身份、五类功能标志、材料及缺失 ID。业务结果保留 `#RRGGBB`，游戏目标使用 `RRGGBB`；两个协议槽位均须拒绝带前缀、小写、错误长度、非十六进制或空白颜色。仅最终 Unity 发布使用记录替身，不加载游戏。

`--display-continuity` 在动作替身阻塞时每250毫秒发布观察更新，验证页面先于动作发布、经营稀客与页面保留上次成功结果、原来源签名不变且仍为非当前。会话、场景、生命周期、目录、配置、特殊经营语义或查询意图变化必须立即清空旧展示；保留展示不能恢复旧命令执行许可。该模式验证只读展示连续性，不代表旧推荐仍满足最新库存或订单状态。

`--daytime-ui-lifecycle` 使用生产 `NightBusinessLifecycleTracker` 与 `RuntimeUiTargetSessionGuard`，覆盖从未营业的白天、Closing、Destroyed、下一代营业、缺失/未知阶段、快照落后及发布中切场。非营业期稀客/普客查询必须完成，不能提交空夜间目标或反复创建撤销屏障；真实 Active 发布失败仍报错并禁止动作，排队动作在没有新快照时也须因关闭营业而失去许可。附加 `--expect-before-fix` 仅用于旧实现的失败复现，正常验证不使用该参数。

`--serve` 提供历史离线 harness 可调用的 JSONL 桥接，不是 Flutter 的生产通信方式。每行请求带 `id` 和 `operation`；响应为 `{id,ok,result}` 或 `{id,ok:false,error}`。操作包括：

- `initialize`：`clientId`、完整共享 `profile`，建立测试主设备并启动后台业务循环。
- `publish`：`snapshot`、原始运行时 `catalog`，可附 `profile`、`favorites`、`customRecipes`；快照签名采用 `snapshot.snapshotSignature`。可信 mock 必须明确提供 `nightBusinessGeneration` 与精确 `nightBusinessLifecyclePhase`，桥接据此驱动真实托管生命周期；实际目标入口仍执行与生产相同的严格门禁。
- `status`：`protocolVersion`，获取真实后台状态。
- `query`：`clientId`、`intent`，提交页面查询并读取结果。
- `heartbeat`：只刷新测试主设备在线时间，不延长自动化租约。
- `lease`：`owned`，仅用于门禁测试；即使主动授予，游戏替身也始终返回取消，不能操作真实游戏。

测试配置只写入各专项创建的独占临时目录，使用仓库忽略的 `temp` 或系统临时根，正常结束时清理自己的目录。不得将真实用户配置传入测试。

`--lease-expiry` 在托管替身中扣住排队命令，等待短租约自然到期，验证没有新快照、没有主动 prune 时也不允许执行，且续约不能追认旧命令。它不等待或调用真实游戏主线程。

大目录持续流验证先运行 `node tests/csharp-business-orders/benchmark.mjs --write-fixture`，再运行本宿主 `--load-stream` 和 `--load-stream --client-poll`。前者每10毫秒读取状态和页面，后者使用当前前端的750毫秒读取周期；两者都持续每250/500毫秒更新观察时间、送达状态与展示进度，并要求经营和页面均发布多个当前版本。库存改变必须立即撤销旧结果，重算后使用新签名。输出含缓存容量/命中计数，便于区分冷计算、淘汰和读竞争；该验证依赖当前机器速度，应串行运行。

追加 `--volatile-runtime` 使用八种不同需求的幽幽子第三阶段，同时改变厨具忙闲及特殊经营观察值。每个输入频率保留40秒观察窗口，覆盖较长冷计算之后的持续当前发布；保留同样的业务/页面多个版本与真实库存失效断言。冷计算、缓存命中和真实游戏帧率是不同指标，不能互相替代。

`--completed-display` 确定性地在每次捕获后、完成前推进快照，验证持续过期仍推进只读结果，并检查原来源签名、跨作用域拒绝、版本不倒退和零动作。与流式计时测试分开，避免依赖机器恰好处于安静时段。
