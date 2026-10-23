# Flutter / C# Local API 契约验证

此测试覆盖当前 profile schema 4 与 C# Business 协议。宿主直接编译当前生产 `LocalApiServer`、HTTP reader/router、设备权威、收藏与自定义菜谱 store；运行于真实 .NET 6。它不是 Node HTTP 模拟服务。所有数据在本次独占临时目录，Token 是公开合成值。原生游戏与更新委托默认拒绝；库存/邀请的指定合成参数仅进入 stdin 控制队列并更新内存计数，以验证生产写入许可的正反向路径，不操作任何游戏对象。

## 执行

在仓库根目录构建（SDK 由 `global.json` 锁定，宿主启动时强制 runtime major=6）：

管理专项调用真实BepInEx `ConfigFile`，还需其锁定运行库 `SemanticVersioning.dll`。从 `references.lock.json` 指定且整包SHA256通过校验的BepInEx #783原始ZIP中，恢复成员 `BepInEx/core/SemanticVersioning.dll` 到 `temp/flutter-mod-host-runtime/SemanticVersioning.dll`；也可在下方构建命令增加 `-p:BepInExRuntimeDirectory=<包含该DLL的目录>`。该文件34816字节，SHA256为 `a5b57607225a0ebde049e93469f49e5b2d07a7d602a03864ce3a72ccf57103a2`，项目在编译前强制核验。原私有编译引用包不含它；不得用其他版本代替或把临时目录存在当作已完成校验。此依赖只供隔离宿主测试，Mod成套包不额外携带。

```powershell
dotnet build tests/flutter-contracts/host/FlutterContractsHost.csproj -c Release -m:1 -nr:false -p:UseSharedCompilation=false -p:NuGetAudit=false --ignore-failed-sources
```

在 `tests/flutter-contracts` 使用根工具锁指定 SDK 内的 Dart（不能从 PATH 选择另一套版本）：

```powershell
dart pub get --enforce-lockfile
dart analyze
dart run bin/verify.dart
```

依赖已完整缓存且需要断网执行时，可在 `pub get` 中额外加 `--offline`，仍保留 `--enforce-lockfile`。也可从仓库根运行 `node scripts/flutter-workspace.mjs --sdk-root <锁定SDK> --task contracts-test`；该入口校验 SDK 并准备冻结依赖，但不自动构建上述 C# 宿主，也不替代本目录的 analyze。

只运行纯值、真实 Dart HttpServer 传输和可控时序测试时使用 `dart run bin/verify.dart --unit-only`。完整测试还会启动前述已编译宿主，使用真实 TCP 请求；不会构建或启动游戏。

完整runner同时执行 `--management <合成profile.json>` 专项：实际网卡校验、Token修改与旧凭据拒绝、BepInEx配置单次保存/失败内存恢复、排队激活的凭据切换、日志数量1–30、诊断包范围/重解析点/陈旧ID/精确删除及主设备权限。文件操作只作用于专项创建的临时目录，不读取或删除用户诊断包。

Flutter 组合测试位于 `apps/companion-flutter/test/live_http_contract_test.dart`。在 Flutter 应用目录执行 `flutter test test/live_http_contract_test.dart`。可通过 `FLUTTER_CONTRACT_HOST` 覆盖 DLL 路径，通过 `DOTNET_EXE` 覆盖 dotnet 可执行文件。该测试以原生 HostApi 替身接入真实 `CompanionPlatform`、`LiveWorkbenchModel` 和 HTTP，会持久化合成偏好并回读；不把原生安全存储替身当作 DPAPI/Keystore 证据。

## 手工接入隔离宿主

先用 `CompanionProfile.defaults().json` 写出当前 schema 4 的完整 profile JSON（包括 `gameUiEnabled`），再启动：

```powershell
dotnet tests/flutter-contracts/host/bin/Release/net6.0/FlutterContractsHost.dll recovery <profile.json>
```

首行 JSON 的 `port` 给出临时回环端口，`kind` 必须为 `flutter-contracts-real-local-api`。合成 Token 为 `flutter-contracts-synthetic-token`，已有离线主设备 ID 为 `p0-old-device-00000001`。标准输入换行关闭并清理宿主自己的目录。另支持 `full`（32 个设备）、`corrupt`、`future` 三种存储场景；不得把实际游戏配置作为 profile 输入。

## 覆盖范围与证据边界

本文件描述当前测试实现，不累积历次断言数量或个人工作区日志路径。每次报告应记录源码 SHA、锁定环境、实际命令与 runner 输出；管理、快照投影等子进程会单独报告断言，不能把不同时间或互相重叠的验证数量拼成一次完整运行结果。

- DTO 严格类型、schema 4 默认安全配置/依赖约束/范围、不可变对象、精确来源上下文与当前签名。`gameUiEnabled` 是必需共享布尔字段，新配置默认关闭；配置关闭时不删除十个普客/稀客分项及颜色。
- 本机历史 checkpoint 使用独立迁移入口：缺少版本的历史记录按 schema 1 处理，要求对应旧版本字段全集和类型严格有效；schema 1/2 均移除已取消的 `serviceOrderSortMode`。schema 1 按十个旧辅助分项是否任一开启推导 `gameUiEnabled`，schema 2/3 保留明确的总开关值。schema 1/2/3 的旧评分配置若九项目标精确匹配其命名预设，则保留预设；否则仅将模式设为 `custom`，完整保留原启用、权重、方向与数组顺序。自动化及其余偏好不被重置。缺字段、未知字段、错误类型及未来版本拒绝。迁移后的 checkpoint 带 `profileSchemaVersion=4`，仅作为设备注册初值，revision/hash/同步 ACK 仍来自服务端返回并耐久保存的权威结果；当前 HTTP 只接受 schema 4，不接受旧排序字段。
- 四组排序预设逐组与生产 C# 导出的完整目标配置对比；默认采用 balanced。四个固定预设只接受其规范九项，只有显式 `custom` 允许调整启停、权重和方向；自定义即使与某预设同值也不反向改名。生产模型模式切换与叶字段修改串行合并，旧滑块不能覆盖刚切换的固定预设，不把列表位置解释为业务优先序。
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
