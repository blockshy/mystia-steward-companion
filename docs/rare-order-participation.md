# 稀客订单参与队列

更新日期：2026-09-01

本文定义“受控稀客名单”和经营中参与队列的唯一运行时契约。订单强身份与终态见
[订单捕获与生命周期](runtime-order-lifecycle.md)，副作用事务见[自动化运行时](automation-runtime.md)，
高亮资源所有权见[游戏 UI 集成](game-ui-integration.md)，传输与主设备权威见[本地 API](local-api.md)。

## 模块与名单语义

共享 profile schema v3 使用严格布尔值 `rareGuestParticipationModuleEnabled` 控制独立模块，并用 `managedRareGuestIds` 保存“需要玩家在经营中手动调度的稀客”。模块默认关闭；配置名单不是允许出现或允许推荐的白名单：

- 模块关闭时保留配置名单，但 Mod 暴露给参与状态的有效名单为空；所有稀客完全保持原有推荐、自动化和游戏 UI 目标选择行为。
- 模块开启且配置名单为空时，同样不增加参与门禁。
- 未入名单的稀客订单继续自动参与。
- 名单内每个新出现的 exact order lifecycle 默认暂停；过去启用过同一稀客，不会授权其后续新 lifecycle。
- 暂停订单仍保留在订单捕获、Worker 推荐事实、稀客队列管理页和诊断集合中；它只从 operational 消费者、
  “经营中 -> 推荐 -> 稀客”和稀客订单专注模式排除。
- 将稀客移出名单时，其当前订单恢复自动参与；名单发生变化会推进自动化 command epoch。

从关闭切换到开启时，当前配置名单成为有效名单，名单内已有订单立即按默认暂停纳入调度。从开启切换到关闭时，有效名单变为空，当前受控订单恢复自动参与并排到队尾；配置名单本身不被清空。持久化 schema 与迁移规则只在[本地 API](local-api.md#设备配置权威)维护。

名单只保存 canonical non-negative `guestId`，严格升序、去重且最多 512 项。名称、桌位、运行时 guest ID、Tag、显示文本和托管 hash 都不能替代 canonical ID。

## 权威状态与身份

`RuntimeRareGuestParticipationState` 是当前经营代际的唯一参与状态。公开订单身份由以下四个标量组成：

```text
business generation
+ R-* trace
+ order lifecycle sequence
+ canonical guestId
```

公开身份用于 snapshot、mutation、前端队列以及尚未进入原生事务的准入。料理或送达事务解析到原生订单后，再为同一公开身份单向补强 `RuntimeOrderBindingToken`；已经绑定的 identity 或 token 不能替换、转移或在 ABA 后复用。跨帧状态只保留这些托管标量，不保存活的 IL2CPP wrapper。

已绑定料理 job 的参与身份和名单对齐必须从 exact `OrderBinding.BusinessGeneration` 取得经营代次。cooker ownership generation 只标识当前厨具内容的所有权锅次，不属于订单公开身份，也不能用于参与状态或经营代次比较。

一次完整订单读取最多投影 512 个当前 lifecycle，并在一个经营代际内保留最多 4096 个已见 identity tombstone。完整、无错误的 `NightBusinessContext` 才能退休缺失订单；读取错误或部分集合保留上一份权威状态，不把空集合猜成经营现场已清空。

## 队列规则

参与状态保存为一份按 exact identity 排列的显式队列；快照为每笔参与订单派生连续正整数
`queuePosition`，暂停的受控订单使用 `null`：

- 未受控的新订单在首次完整观测时自动追加到队尾。
- `enable-tail` 只把目标中尚未参与的订单按首次观测顺序连续追加到队尾。
- `pause` 从队列移除目标中正在参与的订单，剩余订单保持原相对次序并重新派生连续位置。
- 对已经参与的订单再次执行任一启用动作都是 no-op；不能借此改变它与其他已参与订单的相对次序。
- 稀客级 mutation 原子处理该稀客完整当前 exact lifecycle 集合，但启用动作只插入其中暂停的订单；
  单订单 mutation 只处理一个 exact lifecycle。

`enable-front` 不是抢占当前队首，而是“排到当前工作之后”：

1. 服务端先固定请求所指向的完整 participation snapshot 与 revision，再在同一权威转换临界区取得当前
   高亮所用的 rare UI target，并在 automation cooking-job 锁内取得所有缓存为 `ControlState == active` 的稀客料理
   job 候选；客户端不提交或推测这些身份。
2. 当前 rare UI target 只有在同一快照中仍是 current、exact 且 `Participating` 时才成为保护项；没有
   rare target 是合法状态，但 target 已暂停、过期或身份不完整时整次 mutation 冲突。
3. 缓存 active 的料理 job 还不是保护项。服务端释放 cooking-job 锁后，按同一 participation snapshot
   分类：current 且 `Participating` 的 exact lifecycle 才受保护；current 但已暂停的候选表示 job 缓存尚未
   观察到暂停，必须排除并写入有界诊断；候选缺失、身份/绑定未知、重复、代际不匹配或 revision 漂移均
   fail closed，不把不确定项静默忽略。
4. 有效 UI target 与 job 身份按 exact identity 合并；同一订单同时出现时只保留一个保护项。成功日志以
   有界列表记录缓存 active、因暂停排除的 job、最终保护项及插入位置；拒绝日志有界记录失败原因。
5. 新启用订单插到现有队列中最靠后的保护项之后；没有任何保护项时才插入位置 1。
6. 现有队列的完整相对次序始终保留。多个新启用订单按首次观测顺序形成连续区段，不会打断当前
   UI 目标或任何已经开始的稀客料理任务。

特殊经营中已经验证的硬安全 lane 先于 `queuePosition`；其余跨订单 operational 顺序以
`queuePosition` 为准。同一队首暂时缺材料或厨具时，不阻塞后续可执行订单。订单捕获和 Worker 推荐事实
与参与队列分离，不能把队列位置写回 `firstSeenAtUtc`、改变候选或重新定义 primary plan；经营中参与展示
则只投影具有有效 `queuePosition` 的订单，并按该位置排列。

该队列只属于 Companion/Mod。实现不修改游戏的 `AllOrdersData`、Stack、HUD 列表或其他原生订单集合，也不按游戏 UI 位置模拟重排。

## Mutation 与主设备权威

唯一写入口是 `POST /orders/rare/participation`，且只有模块开启时接受 mutation。调用方必须是当前主设备，并同时在 authority header 和严格 JSON body 中携带同一正数 authority revision。请求还必须包含：

- expected business generation；
- expected participation revision；
- `action`：`pause`、`enable-tail` 或 `enable-front`；
- `target.type == guest` 时的 canonical `guestId` 和该 guest 完整当前 `expectedCurrentOrders` identity 集合；或者
- `target.type == order` 时的一笔完整 exact order identity。

guest target 在 participation monitor 内执行全量集合 CAS；新增、终止或身份变化都会使整次写入以冲突失败，
不允许部分授权。order target 必须精确命中一笔当前、受控 lifecycle，不通过 guest 名称、桌位或位置推断。
通过主设备检查后，后端先推进 automation command epoch，再为 `enable-front` 采集服务端权威保护集合并提交
参与状态。即使后续 CAS 或保护项校验冲突，旧排队命令也已经被安全作废。客户端不提交保护集合，也不能用
旧 UI 状态自行计算插入位置。

主设备切换会撤销有效名单内所有当前人工授权并恢复默认暂停。普通 profile 更新按新旧有效名单差异处理：模块开启或新加入名单时，当前 lifecycle 暂停；模块关闭或移出名单时，当前 lifecycle 自动排尾。模块关闭期间只修改配置名单不会改变有效名单。经营结束由 lifecycle owner 按 exact generation 在状态锁内原子退休，不使用先读 revision 再关闭的 TOCTOU 路径。

规范契约不包含 `/orders/rare/dismiss`、前端删除按钮或捕获层弱匹配；暂停与启用不能通过删除捕获记录实现。

## 自动化与游戏 UI 门禁

模块关闭或有效名单为空时不增加 participation 门禁。有效名单非空时，所有稀客 operational 路径都必须取得与当前 profile 对齐的许可：

- 排队的准备/完成命令在进入运行时服务前取得 public-identity admission permit。
- 订单解析成功后，把 exact native binding 补强到同一 lifecycle。
- 已开锅 job 在送达、评价和特殊经营结算等后续不可逆边界取得 bound side-effect permit，并在许可持有期间复核 active terminal-receipt lifecycle。
- Mod 尚未提交送达时若 exact native binding 已收到 Evaluated/Removed terminal receipt，必须先于 control/participation gate 和任何 wrapper 读取退休 job、释放逻辑厨具预约并保留现场；陈旧 lifecycle receipt 不得命中，也不得送达、评价、入箱或复位。
- 游戏 UI target 明确携带 canonical `guestId`；服务端在发布前按 generation、trace、lifecycle、guestId 复核 admission，不按桌位或名称反查。
- 普通暂停成功后精确移除对应 rare target，同时保留 normal target；operational target 与各高亮服务立即同步，已打开的料理/酒水列表由下一次 Unity 主线程 Tick 清除旧置顶、高亮和加料行。

暂停先推进 command epoch。尚未开始的命令取消；已经进入 permit 的同步原生动作完整结束后，暂停 mutation 才能提交。已经开锅的 job 不退款、不清锅、不重复开锅，暂停期间停止自动收取、送达与评价，恢复后从下一安全步骤继续且不消耗暂停时长的有效超时预算。

后端门禁是最终权威。前端即使因旧快照短暂尝试发布 target 或发送动作，服务端仍必须拒绝 paused、stale generation、identity 缺失、profile/state 不一致或 inactive lifecycle。

profile 或主设备 authority 切换还有独立的空 operational fence。后端应用新有效名单后，在 participation permit 与 target publication 临界区内过滤原 presentation target：只保留仍被许可且 exact identity 未变化的 rare target，normal target 始终独立保留；被暂停的 rare claims 使用唯一中间 generation，空 fence 使用下一 generation。Unity 主线程只刷新一次过滤投影，不清页面登记，也不退休、退款或重放未决 recipe transaction。

## 前端投影

前端领域层明确区分以下集合：

- capture / Worker recommendation facts：保留全部当前稀客订单及其已计算推荐；暂停不删除订单事实、
  Worker 输入、结果或 primary plan。
- management：保留受控稀客的全部当前 exact lifecycle，在“稀客队列”中显示已启用、已暂停或状态不可用，
  供玩家恢复暂停订单。
- participating presentation：只包含 Mod 权威快照中正在参与且具有有效正 `queuePosition` 的订单；
  “经营中 -> 推荐 -> 稀客”和稀客订单专注模式使用这一集合并严格按 `queuePosition` 显示。
- operational：消费与 participating presentation 相同的权威参与身份和位置，再执行各自的安全门禁。

顶层“扩展功能 -> 稀客调度”负责模块开关和配置名单；“经营中 -> 推荐 -> 稀客队列”只管理当前订单。
经营中队列按 canonical guestId 分组，每组提供全部优先启用、全部队尾启用和暂停全部；每笔订单也提供
优先启用、队尾启用或暂停。稀客级按钮必须回传该组完整当前 exact lifecycle 集合，单笔按钮只回传一笔
exact identity。

模块开启且有效名单非空时，前端只接受与当前完整、无错误订单集合全局精确对齐的一份 participation
snapshot；任一 malformed、duplicate、missing/extra identity、非法 revision 或非连续/重复队列位置都会使
整个 participation projection 不可用，参与展示和所有 operational 消费停止，而不是只禁用单组。模块关闭或
有效名单为空时，不建立 participation 展示或执行门禁：“经营中 -> 推荐 -> 稀客”、专注模式和 operational
消费者全部沿用原有集合与排序。主设备可以修改模块、名单和当前参与状态，非主设备只读；409 冲突必须刷新后
由用户重试，不做乐观合并或 last-write-wins。

## 验证

最窄自动验证入口：

```bash
corepack pnpm audit:rare-order-participation
corepack pnpm audit:rare-order-participation:ui
corepack pnpm test:dotnet6 runtime-rare-guest-participation
corepack pnpm test:dotnet6 night-business-lifecycle
corepack pnpm audit:ui-pinning
```

涉及 C# 门禁还需锁定 SDK 的 Mod Release 构建；涉及前端还需 lint + build。参与状态、订单、自动化或游戏
UI ownership 的自动测试不能替代锁定游戏/BepInEx 实机 smoke。实机至少覆盖模块默认关闭、关闭时保留非空
配置名单且全部稀客保持原有展示和正常自动送达、开启后两名受控稀客默认暂停、暂停订单从稀客推荐与专注模式
隐藏但仍保留捕获/Worker 推荐事实和队列管理入口、同 guest 多订单的单笔/整组操作、同 guest 新 lifecycle、
队尾启用、启用后按 `queuePosition` 恢复展示、无保护项优先启用、仅 UI target、单个/多个活动料理任务保护、
已启用订单再次点击不重排、暂停旧高亮、开锅前后暂停、恢复不重复执行、主设备切换和有效空名单回归，并保存
完整日志与订单 trace。
