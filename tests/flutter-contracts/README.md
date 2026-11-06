# Flutter / C# Local API 契约验证

此测试覆盖当前 profile schema 10 与 C# Business 协议。宿主直接编译当前生产 `LocalApiServer`、HTTP reader/router、设备权威、收藏与自定义菜谱 store；运行于真实 .NET 6。它不是 Node HTTP 模拟服务。所有数据在本次独占临时目录，Token 是公开合成值。原生游戏与更新委托默认拒绝；库存/邀请的指定合成参数仅进入 stdin 控制队列并更新内存计数，以验证生产写入许可的正反向路径，不操作任何游戏对象。

## 执行

在仓库根目录构建（SDK 由 `global.json` 锁定，宿主启动时强制 runtime major=6）：

管理专项调用真实 BepInEx `ConfigFile`，还需其锁定运行库 `SemanticVersioning.dll`。执行下方恢复脚本后再构建宿主；完整 `build-release.ps1` 已自动包含这一步。脚本只下载 `references.lock.json` 指定的 BepInEx #783 原始 ZIP，验证整包 SHA-256，再按共用 `host/BepInExRuntime.props` 核对唯一成员路径、长度和摘要，提取到 `temp/flutter-mod-host-runtime`。正确缓存可复用；错误缓存保留并报错，缺失或损坏不能悄悄使用其他版本。宿主编译前再次校验摘要，依赖不进入 Mod 成套包。

```powershell
pwsh -File scripts/restore-bepinex-test-runtime.ps1
dotnet build tests/flutter-contracts/host/FlutterContractsHost.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --ignore-failed-sources
```

离线恢复传 `-ArchivePath <已下载的原始ZIP>`，仍执行整包与成员验证。可选 `-OutputDirectory <独立目录>` 需与宿主构建的 `-p:BepInExRuntimeDirectory=<同一目录>` 配对；目录链含重解析点时拒绝写入。恢复脚本只清理本次创建的下载/提取临时文件，不删除传入 ZIP 或已有缓存。发布 workflow 另在成功及失败路径清理其 checkout 内的隔离运行库目录。原有私有编译引用 bundle 不含该依赖，不能用是否存在旧临时目录替代校验。

在 `tests/flutter-contracts` 使用根工具锁指定 SDK 内的 Dart（不能从 PATH 选择另一套版本）：

```powershell
dart pub get --enforce-lockfile
dart analyze
dart run bin/verify.dart
```

依赖已完整缓存且需要断网执行时，可在 `pub get` 中额外加 `--offline`，仍保留 `--enforce-lockfile`。也可从仓库根运行 `node scripts/flutter-workspace.mjs --sdk-root <锁定SDK> --task contracts-test`；该入口校验 SDK 并准备冻结依赖，但不自动构建上述 C# 宿主，也不替代本目录的 analyze。

只运行纯值、真实 Dart HttpServer 传输和可控时序测试时使用 `dart run bin/verify.dart --unit-only`。完整测试还会启动前述已编译宿主，使用真实 TCP 请求；不会构建或启动游戏。

完整runner先执行 `--snapshot-signature` 与 `--participation-admission`：实际生产内容签名必须响应稀客/普客读取完整位的变化，主线程准入对不完整/错误/集合冲突拒绝并正确释放许可锁；不操作游戏。

完整runner同时执行 `--management <合成profile.json>` 专项：实际网卡校验、Token修改与旧凭据拒绝、BepInEx配置单次保存/失败内存恢复、排队激活的凭据切换、日志数量1–30、诊断包范围/重解析点/陈旧ID/精确删除及主设备权限。文件操作只作用于专项创建的临时目录，不读取或删除用户诊断包。

Flutter 组合测试位于 `apps/companion-flutter/test/live_http_contract_test.dart`。在 Flutter 应用目录执行 `flutter test test/live_http_contract_test.dart`。可通过 `FLUTTER_CONTRACT_HOST` 覆盖 DLL 路径，通过 `DOTNET_EXE` 覆盖 dotnet 可执行文件。该测试以原生 HostApi 替身接入真实 `CompanionPlatform`、`LiveWorkbenchModel` 和 HTTP，会持久化合成偏好并回读；不把原生安全存储替身当作 DPAPI/Keystore 证据。

## 手工接入隔离宿主

先用 `CompanionProfile.defaults().json` 写出当前 schema 10 的完整 profile JSON（包括 `gameUiEnabled`），再启动：

```powershell
dotnet tests/flutter-contracts/host/bin/Release/net6.0/FlutterContractsHost.dll recovery <profile.json>
```

首行 JSON 的 `port` 给出临时回环端口，`kind` 必须为 `flutter-contracts-real-local-api`。合成 Token 为 `flutter-contracts-synthetic-token`，已有离线主设备 ID 为 `p0-old-device-00000001`。标准输入换行关闭并清理宿主自己的目录。另支持 `full`（32 个设备）、`corrupt`、`future` 三种存储场景；不得把实际游戏配置作为 profile 输入。

## 覆盖范围与证据边界

游戏内专注显示的独立入口为 `dotnet host/bin/Release/net6.0/FlutterContractsHost.dll --game-focus-overlay`。它直接验证正式设置存储、v1原字节备份与v2迁移、严格磁盘格式、CAS、原子写入失败，以及正式订单投影的需求指纹、场景/目录/生命周期、调度暂停恢复、内容开关、精确图片键、订单底色和黑暗料理来源；Flutter真实HTTP测试补充主设备权限、内容/主题同步、迟到GET、409补读与未知回执不重发。旧编辑状态字段已移除，游戏内面板始终穿透，位置尺寸由外部客户端设置。

`--rare-order-display`以去敏后的四笔同客人订单验证显示与执行分离：名单中新单默认暂停时显示键仍完整，但自动化命令、原生高亮目标及参与许可均为空；逐单启用、再暂停和恢复只改变相应精确身份。缺项、重复、旧身份和不完整采集仍拒绝执行。完整验证入口包含该专项，不能只凭客户端卡片可见就认定动作资格有效。

`--order-patience`验证原生字段读取边界、生命周期与ABA隔离、未知null、复制链、耐心变化刷新快照且不使推荐payload失效。`--rare-patience-auto-resume`调用正式宿主权威入口，覆盖严格低于阈值、主设备确认/在线、场景/资源、非阻塞锁、只触发一次以及没有自动化租约时的独立调度。自动化仍经过自己的执行许可；这两项都不操作真实游戏。

`--cooking-ingredient-wait`调用原生生产结果工厂、结果分类与托管自动化状态机，验证开锅前缺料为普通等待、不累积失败预算、保留已经送达的酒水、料理任务与安全屏障优先。另检查缺料分支位于扣料及 `SetCook` 之前；此源码检查不等同于真实 Unity 执行验证。该入口不创建游戏对象、不扣库存、不启动游戏。

本文件描述当前测试实现，不累积历次断言数量或个人工作区日志路径。每次报告应记录源码 SHA、锁定环境、实际命令与 runner 输出；管理、快照投影等子进程会单独报告断言，不能把不同时间或互相重叠的验证数量拼成一次完整运行结果。

- DTO 严格类型、schema 10 默认安全配置/依赖约束/范围、不可变对象、精确来源上下文与当前签名。`gameUiEnabled` 是必需共享布尔字段，新配置默认关闭；配置关闭时不删除十个普客/稀客分项及颜色。`normalGameUiEnabled`、`rareGameUiEnabled` 为必需布尔分类总开关，默认开启，受全局开关约束；关闭分类保留分项与配色。两类调色板默认各2色，范围各1–3色，校验非法长度、同类重复色和颜色格式，跨类同色合法。
- 本机历史 checkpoint 使用独立迁移入口：缺少版本的记录按 schema 1 处理，要求对应旧版本字段全集和类型严格有效。v1/v2 删除旧订单排序，v1 按旧辅助分项推导总开关，v2 以后保留显式开关。v1–v3 的历史可编辑评分预设按实际参数识别为固定预设或自定义，v4 以后的固定预设严格验证。v1–v4 删除无效厨具权重、保留其余八项；各自保留原单色并补为两色，新增调度默认关闭。v5 公共八色分别取前三色迁入两类；v6 严格验证历史颜色后保留前三色；v7 原默认整套或缩短前缀迁为同数量的冷暖预设，其余自定义保留前三色。v8 配色保持原样。v1–v6 先严格验证历史字段，再移除独立辅助数量字段，实际颜色数成为唯一辅助上限；保留其他偏好，不修改输入对象。缺字段、未知字段、错误类型及未来版本拒绝。v1–v8严格验证后新增两类总开关为true，不改变全局辅助状态和子项偏好。v1–v9新增等待条自动启用策略默认false/30/tail，与名单同一修订保存。迁移后的 checkpoint 带 `profileSchemaVersion=10`，仅作为设备注册初值，revision/hash/同步 ACK 仍来自服务端返回并耐久保存的权威结果；当前 HTTP 只接受 schema 10。
- 四组排序预设逐组与生产 C# 导出的完整目标配置对比；默认采用 balanced。四个固定预设只接受其规范八项，只有显式 `custom` 允许调整启停、权重和方向；自定义即使与某预设同值也不反向改名。生产模型模式切换与叶字段修改串行合并，旧滑块不能覆盖刚切换的固定预设，不把列表位置解释为业务优先序。
- 端点仅回环/私有 IPv4、禁止 DNS/重定向；UTF-8 Content-Length 与无 chunked；中文 Header 编解码；写请求损坏响应标记结果未知。
- acquire/release 串行与迟到响应、取消尚未开始的 enable、游戏重启但 authorityRevision 相同的租约隔离。
- 自动化意图仅来自已保存的 `activeProfile.automationEnabled`：全新客户端会话恢复开启/关闭状态，保存开启后自动获取租约，关闭或暂停释放；连接恢复与前台恢复不要求第二个临时开关。查看设备、未完成配置落盘/ACK、旧主设备仍不能租约。开启配置不绕过实际场景和业务来源检查。
- health 协议不匹配不可绕过握手注册/轮询；pause/resume 等待旧刷新退出后重新获取当前业务状态。
- `/devices/register`、`/devices`、`/devices/profile`、`/devices/sync`、`/devices/sync-ack`、`/devices/primary`：辅设备权限、CAS 冲突、真实落盘回读后 ACK、显式切主。
- `/automation/lease/acquire`、`/automation/lease/release`、`/automation/barriers/ack`：主设备租约、旧修订拒绝、非 Active 场景 ACK 拒绝、旧 query ACK 格式拒绝；C# 值契约覆盖旧版本/旧进程/旧经营代次拒绝。
- 人工确认绑定打开对话框时的会话与经营代次：真实 HTTP 读取两次合成边界并重用 sequence=1，验证旧确认在发包前被拒绝；纯会话测试另验证新会话已取得有效执行许可时仍拒绝旧确认。宿主仅通过测试 stdin 推进合成快照，不触发游戏生命周期，也不新增生产控制端点。
- `/snapshot` 完整/unchanged、`/business/status`、`/business/page-session/open` 与 `/business/page-session/update` normal/rare 的当前信封与只读行为；显式撤销后的旧序号与其它设备会话被拒绝，旧 `/business/query` 和 `/orders/prepare-next` 返回 404。
- 页面离开、Windows 隐藏、Android 暂停与关闭主动撤销；普通业务轮询不登记推荐查询。受控 Future 覆盖迟到打开、逆序更新、取消失败及恢复可见，隐藏推荐不能撤销经营租约；服务端 TTL 另由 C# 宿主验证。
- `/favorites` 的料理/饮料增删及规范化；`/custom-recipes` 的新增、开关、标记、排序、删除及非法参数拒绝；全部走真实生产文件 store。
- 23 个手工写端点逐项拒绝查看设备、缺失修订和旧修订；另验证未注册设备拒绝。四个库存/邀请队列逐项覆盖准入后切主，确认副作用计数保持 0；当前主设备无需 automation lease，每条队列正向请求各执行恰好一次合成副作用。真实文件 store 的手工增删同样在释放 lease 后执行。
- `/missions/tracked`、`/missions/available` 不可用状态和签名命中；邀请读取/写入、库存单项/批量写入的运行时拒绝传播。
- `/updates/status` 保留真实状态 DTO：历史操作错误即使伴随 `ok=true` 也完整返回；更新命令拒绝仍作为操作失败传播，避免把历史错误误当作读取失败而丢失版本与 staged 状态。

这些测试不提供真实游戏、Unity/IL2CPP 写入、Android 实机、平台安全存储或正式更新发布证据。运行时委托拒绝与合成队列计数只证明协议/权限及返回处理，不证明可在实际场景完成库存编辑或邀请。
