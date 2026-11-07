# 更新系统

更新日期：2026-11-07

本文说明版本检测、累计版本说明、包下载和独立更新程序的完整链路。正式发布如何生成对应资产见
[发布流程](local-release.md)；接口认证与方法矩阵见 [本地 API](local-api.md)。

用户操作步骤集中在 [Mod 安装与更新](../mods/bepinex/README.md#更新-mod)和[独立 Windows 客户端](standalone-client.md)。本文保留实现契约、失败恢复与验证入口；自动迁移旧配置、删除旧残留和更新程序文件分别遵守各自条件。

## 职责边界

更新系统由四个边界清晰的部分组成：

- Mod 内的 `UpdateService` 是唯一外部网络访问者和更新状态权威。
- GitHub Release 上的 manifest、catalog 与 ZIP 是只读发布资产。
- 伴随窗口只通过本地 API 展示状态和发起用户动作。
- 独立 updater 在游戏退出边界之外替换 Mod 目录，并回写安装结果。

前端不直接查询 GitHub，不自行比较版本，也不直接解压覆盖文件。Mod 不在游戏进程仍运行时替换自己的程序集。
更新检测与下载不会关闭游戏。打开独立 updater 后，仍须用户明确开始安装；Windows 的开始动作只观察游戏退出；用户自行保存并正常退出游戏，更新器不发送关闭消息或终止进程。

## 数据与程序目录

| 范围 | 存储位置 | 更新时的处理 |
| --- | --- | --- |
| Mod 程序与内置客户端 | `BepInEx/plugins/mystia-steward-companion/` | 成套替换，不能混用 DLL、客户端或 updater。 |
| Mod 自有数据 | `BepInEx/config/MystiaStewardCompanion/` | 保留配置、共享业务数据、缓存、诊断与更新事务资料。 |
| Mod 内置 Windows 客户端数据 | `%LOCALAPPDATA%/mystia-steward-companion/flutter-v1/` | 不随插件替换迁移或清空。 |
| 独立 Windows 客户端数据 | 实际 EXE 旁的 `client-data/` | 退出后备份，携带整个数据目录更换完整独立包。 |
| Android 客户端数据 | 应用私有目录与 Keystore | 同包名、原证书签名且更高版本号覆盖安装，不能以卸载或清空数据代替升级。 |

Mod 自有数据根为 `BepInEx/config/MystiaStewardCompanion/`，更新服务继续使用其中的 `updates/`，不因布局调整另建下载状态。`update-state.json` 保存检查与下载状态，`install-status.json` 接收安装结果；下载包和解压目录位于 `downloads/`，独立运行副本位于 `runner/`，插件原件备份位于 `backups/`。这些文件与插件程序目录分离，整包替换不清理配置根。

Mod DLL、Windows `companion/` 完整运行树与 updater 是插件目录中的受管理载荷。图片缓存写入数据根的 `cache/game-media/`，布局迁移本身不复制或删除旧插件 `user-data/game-media`；新根需要时重建缓存。各客户端分别保存偏好、设备身份、连接凭据与本机共享配置确认记录，不随 Mod 数据迁移复制或重置。

### 独立客户端目录准入

独立 Windows ZIP 包含唯一产品外层文件夹、专用 `mystia-steward-companion-portable.exe` 与 `portable-client-v1.manifest`。启动以实际 EXE 路径核对完整文件集合、大小及 SHA-256；缺失、混装、符号链接或公共目录根部均拒绝初始化。`client-data/` 不属于发布载荷，打包入口拒绝夹带它。独立客户端更新使用完整新目录并保留原 `client-data/`，不经过 Mod updater；首次导入旧 Flutter 配置需用户选择，旧文件保留且不复制设备身份，详见[独立客户端](standalone-client.md)。

### Mod 配置迁移与自动清理

布局迁移使用独立 `layout-state.json`，当前 `layoutVersion` 为 1；它不替代设备 profile、配方等文件各自的 schema 迁移。修改前先为明确白名单中的配置保存原字节及摘要，备份目录为 `migration-backups/layout-v1-<标识>/`，以 `prepared`、`complete` 区分准备和发布完成状态。中断恢复必须符合已记录的源摘要和预期目标，不能重新选取另一份旧文件或覆盖冲突目标。完成后只使用新主配置，不再双写。`complete` 不代表真实配置绑定、业务 JSON 或客户端初始化已经完成，不能单凭此标记删除旧 CFG。迁移不递归复制日志和更新暂存；旧自定义聚合日志路径归一为统一根默认位置，外部历史日志不删除。

`complete` 后继续校验当前主配置的语法、已知类型及凭据约束，但不以首次迁移摘要禁止合法配置修改。新主配置缺失或损坏时停止插件初始化，不从旧源回填；布局记录损坏或版本未知也不能当作首次安装。配置绑定仍会将再次手填的聚合日志路径归一为空默认值，保证新写入留在统一根。

新配置完成真实 BepInEx 绑定、必要 Token 保存及回读验证后，才进入旧 CFG 自动清理。清理再次核对完整迁移备份、原件内容和文件身份，只删除旧根的单个 CFG；统一根内独立的 `legacy-cfg-cleanup.json` 记录用于恢复和防止重复删除。仅清理失败时保留旧源并提示，不阻止已经可用的新版初始化；后续启动只重试同一原件。完成后旧路径重新出现或原件变化时保留并提示。该步骤不等待 Flutter 或强制提前升级业务 JSON，也不声称全部历史数据已经迁移；迁移备份始终保留。

上述过程只处理 Mod 配置；客户端身份、WebView 资料及主设备配置同步有独立边界。公开 v1.3.1 的程序替换方式见下文[手动首次升级](#v131-手动首次升级)，配置接管见 [Mod 使用说明](../mods/bepinex/README.md#历史数据迁移)。

## 发布资产

完整 Windows 树在 `companion/` 内必带 EXE、Flutter engine/data/插件及应用本地 `msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`，三种语言的清单验证器逐项拒绝遗漏或大小写不规范。运行库从锁定 VS 正式 Redist 复制并带许可声明。独立 updater 使用根 Cargo 配置静态链接 CRT，复制到 `updates/runner` 时仍是独立 EXE；打包验证真实导入表，不能依赖开发机全局 VC 安装掩盖动态链接。

启动和许可关键文件也必须明确列入清单且非空：`companion/data/app.so`、`companion/data/icudtl.dat`、`companion/data/flutter_assets/` 下的 `AssetManifest.bin`、`FontManifest.json`、`NOTICES.Z`，以及 `companion/THIRD-PARTY-NOTICES.txt`。不能用任意 `data/` 文件替代这些关键项；普通非关键资源允许零长度。打包在创建 staging 前检查 Windows 关键文件，再由三语言整包验证器核对完整集合、大小和摘要。

更新协议直接读取以下资产；完整正式 Release 的七项资产及 MIME 约束见[发布流程](local-release.md#正式资产)：

- `update-manifest.json`：该版本的通道、包名、大小、SHA-256、Release URL 和可选 catalog 元数据。
- 版本固定的 Mod ZIP 包。
- `update-catalog.json`：以该 Release 为 owner 的累计版本说明目录。

manifest 是更新可用性和包完整性的权威。catalog 只负责展示从当前版本到最新版本之间的公开版本和说明；
catalog 获取或校验失败时，更新仍可检测、下载和安装，只把“版本说明”标记为不可用。

首个 Flutter Release 及后续正式版、预发布版的 `update-manifest.json` 均使用 `schemaVersion: 2`。公开 v1.3.1 的读取器只接受 1，因此会在生成新版候选前报告协议不支持，不会继续读取 catalog 展示迁移说明。首次手动升级提示必须写入 Release 正文；仅修改 catalog 或添加旧版不识别的字段不能阻断升级。catalog、整包 `package-manifest.json` 与发布工件 `release-metadata.json` 的 schema 各自独立，不随此处升为 2。

清单隔离不能约束旧缓存或用户自行运行的旧 updater。新版普通 updater 因此在等待游戏和请求客户端退出前校验来源安装树，对没有有效完整 Flutter 清单的安装给出手动升级提示；替换前仍会再次验证，避免等待期间树发生变化。

正式版发现入口允许使用下文的 `latest/download/update-manifest.json`；候选确认后的 ZIP、catalog 和预发布候选 manifest 均使用 tag 固定的 Release 下载地址。manifest 与 catalog 都执行严格 schema、owner、通道、
SemVer、数量、大小、URL 和 SHA-256 门禁。catalog 的版本必须严格升序，包含唯一 owner，且不能包含晚于
owner 的版本。ZIP 下载同时校验声明长度与 SHA-256，并在 staging 中拒绝路径穿越和不符合包结构的内容。

### Flutter 完整运行树与业务组件成套校验

每份插件包必须同时包含主 Mod DLL、`MystiaStewardCompanion.Contracts.dll`、`MystiaStewardCompanion.Business.dll`、Windows 客户端和 updater，并附唯一 `package-manifest.json`。清单列出三个 C# DLL、独立 updater 及 `companion/` 下全部 EXE、DLL、插件和 data 资源的相对路径、字节数与 SHA-256，保留源码与工具链身份。不接受缺项、重复字段/路径、大小写冲突、额外文件、链接或混装；程序文件不得为空，合法空资源可存在。

PowerShell/Bash 打包入口先验证全部输入，再事务替换 dist；C# 下载服务与 Rust updater 在 staging/安装后分别核对清单。清单用于发现缺包或组件混装，外部可信来源仍由 Release manifest 的整包 SHA-256 与受限下载地址保证。业务协议版本为 1，客户端协议版本为 2；新客户端与宿主成套交付。Node、C#、Rust 验证器使用相同人工载荷矩阵验证判断一致。ZIP 在解压前检查条目上限、规范路径、链接属性和展开总量，解压期间再次限制实际写入长度。

本地开发安装可使用 `mods/bepinex/tools/install-local-package.ps1`：显式提供游戏、包、存档和工作区记录目录，默认只读校验，`-Execute` 才成套替换。它要求相关进程停止、验证原件备份，并对配置、存档及核心文件做前后摘要检查。安装后校验失败恢复完整原插件；工作区备份保留用于人工回退。附加文件仅保留显式白名单，默认 logs、diagnostics、exports、user-data；发现未知旧文件立即停止，人工审查后才通过 PreserveRelativePaths 指定保留。事务目录位于游戏同级，完整备份位于仓库 temp/flutter-install-<GUID>，避免 BepInEx 扫描到重复 DLL。离线事务验证入口为 `pwsh -NoProfile -File tests/csharp-business-package/install-transaction.ps1`。

旧 updater 无参数运行可能在插件根目录留下内容为 `missing --game-pid` 的失败状态。已审查该残留时，显式传入 `-ArchiveLegacyUpdateStatus`，本地 PowerShell 安装器会先保留完整原件，再把原字节归档到 `user-data/legacy-update-status.json` 并记录前后摘要。未知内容、归档目标冲突或摘要变化均拒绝；不开启开关时，旧根文件仍按未知附加文件处理。此一次性归档不会扩大 Rust updater 的四个永久保留目录，也不是下面 v1.3.1 离线入口的附加文件放行规则。新版普通 updater 只接受完整安装参数与显式绝对 `--status-file`，无参数运行不再向任意工作目录写 `update-status.json`。

## 首次跨代升级与残留清理

### v1.3.1 手动首次升级

公开升级流程为退出进程、移出自行保存在插件内的资料、删除精确的 `BepInEx/plugins/mystia-steward-companion`、解压完整新包并启动游戏。配置根及存档保持原位；主 CFG 迁移与自动清理由新 Mod 启动完成。完整用户步骤见[更新 Mod](../mods/bepinex/README.md#更新-mod)。手动删除没有旧程序自动回滚，不承诺与在线安装事务相同的恢复能力。

程序安装不代替随后 Mod 启动时的布局与文件 schema 迁移，也不导入 WebView 身份或 Token 数据库。独立电脑的 Windows 客户端应取得同版本完整 Windows ZIP，更新操作不涉及该电脑的 BepInEx 或游戏存档，详见[独立客户端](standalone-client.md)。

首次旧 CFG 迁移按文件内容精确识别公开 v1.3.1 的外置 `Companion.ExecutablePath`，匹配时恢复包内默认客户端，不删除外置程序。未知或不可读取的路径不猜测重写，提示人工核对；不能把文件存在视为已验证 Flutter 兼容性，也不新增旧客户端在线兼容分支。

此前维护脚本的显式 `--migrate-v1-3-1` 事务入口暂留作兼容，不是公开默认升级路径，也不是普通在线更新失败后的回退。它仍要求精确旧载荷、独立备份根和显式执行参数；现存成功事务中的 `original-copy/`、`plugin-backup/` 仍可由可选清理工具按原证明核验。普通用户无需执行该命令或提供存档路径。

### v1.3.1 残留清理

`--cleanup-v1-3-1 --game-dir <绝对游戏根>` 是升级后可选的独立离线入口，不是升级必经步骤，默认只读预览。执行必须同时提供 `--execute`、`--plan-sha256 <本次预览摘要>` 和 `--confirm-migrated`；实际候选和内容需仍与计划相符。该模式不放宽包清单，也不替代首次升级、启动时的旧 CFG 自动清理或主设备配置同步，使用顺序见[用户清理步骤](../mods/bepinex/README.md#清理-v131-残留)。

默认只接受布局迁移完整完成且原件备份可验证的旧 cfg，以及身份严格匹配的旧 updater 副本。`--migration-backup-dir` 限定明确成功的离线升级事务，只处理 `original-copy`、`plugin-backup` 中公开旧版的三个程序，保留附加用户资料与小型事务记录；`--legacy-package-zip` 仅接受精确官方 SHA-256 的旧 ZIP，必须为绝对文件路径。ZIP 若位于游戏目录内，只允许直接位于 `BepInEx/plugins/` 下；也可显式指定游戏目录外的绝对文件路径。工具不从未知文件名推断归属，不清理任意 `update-status.json`、整个 `updates` 树、布局迁移原件、当前 Flutter 数据或外部历史日志。

`--include-legacy-client-data` 才允许删除当前用户旧 WebView 资料。Windows 位置由 Known Folders 解析，不接受命令行或测试环境替代根：LocalAppData 的固定旧标识目录可整树删除，RoamingAppData 只处理同标识下的 `.window-state.json`；未知兄弟文件保留，父目录只有确认为空才可移除。此选项包括旧 localStorage 的凭据、身份和未导出偏好，不能用白名单偏好导入记录证明所有资料已迁移。当前 Flutter 根与旧 Tauri 根分离；Android 同包名沙箱不属于此清理功能。

清理不自动创建完整归档，永久多文件删除不是原子事务。执行记录逐项区分结果；失败时保留已经完成的事实并报告 `partial`，不声称回滚恢复。后续重试重新生成计划，以剩余候选为准。备份中旧程序被清理后，原离线升级备份不再具备完整程序回退能力；小型报告不能作为被删内容的恢复副本。

实际清理记录固定放在 Mod 数据根的 `legacy-cleanup/<时间戳-进程号>/`，文件为 `plan.json`、`events.jsonl` 和 `result.json`，分别保存计划、逐项事件及结果。预览不落盘；执行计划中既无文件也无目录候选时，返回 `mode: cleaned`、零删除量及 `journalDirectory: null`，不创建空记录目录。已删旧 ZIP 再次预览视为没有该候选，不搜索替代文件。

## 版本发现策略

为了避免大量中国大陆用户共享 GitHub 出口节点时频繁命中 REST API 未认证速率限制，正式版默认读取：

```text
https://github.com/blockshy/mystia-steward-companion/releases/latest/download/update-manifest.json
```

这个路径只发现最新正式版，不调用 GitHub REST API。启用“包含预发布版本”时，服务读取公开
`releases.atom`，按 SemVer 选择候选 tag，再读取该 tag 固定的 manifest。Atom 只用于候选发现，最终版本和
包仍由 manifest 门禁确认。

发现比当前版本新的 manifest 后，服务读取其 tag 固定的 catalog，并筛选 `当前版本 < release <= 最新版本`
的条目。前端按从旧到新的顺序逐一展示，所以跨多个版本升级时不会只看到最新一版说明。

## Mod 内状态机

核心实现位于 `mods/bepinex/src/Updates/UpdateService.cs`。检测、下载和安排安装共享单一操作门禁，不能并发
写更新状态或 staging 目录。

自动检测成功后按用户设置的 1–168 小时间隔安排下一次检查；连续失败采用 15 分钟、30 分钟、1 小时、
2 小时、4 小时、6 小时封顶的退避。调度线程关停时停止接受新操作，取消并等待当前操作；取消会恢复到可解释
的稳定状态，并允许下一次启动立即继续检测。

启动恢复只处理本程序拥有的临时目录和状态。上次进程在 `checking` 或 `downloading` 等瞬态中断时，服务回到
由已验证 manifest/staging 推导出的稳定状态，不把半成品当作可安装包。可下载候选与已暂存包分别保存实际验证的清单协议版本，缺失、旧版或未知来源均撤销对应状态引用并重新检查；不通过重新填入当前常量把旧缓存解释成新版。撤销状态不删除历史 ZIP、备份或不明目录。未来 schema、损坏缓存和旧 manifest 均 fail closed。

本地 API 使用以下操作端点：

- `POST /updates/status`
- `POST /updates/check`
- `POST /updates/download`
- `POST /updates/install-on-exit`

`status` 也可能合并 updater 回写状态并清理由本程序拥有的已消费文件，因此不是纯 GET。接口 DTO 和请求边界
由 [本地 API](local-api.md) 维护。

## Flutter 伴随窗口

当前更新页位于 `apps/companion-flutter/lib/features/update_content.dart`，网络组合根为 `apps/companion-flutter/lib/app/live_workbench_model.dart`。页面分别说明游戏电脑上的 Mod 更新与本机客户端更新，服务返回的当前/最新版本、安装状态及版本列表均属于所连接的 Mod，不代表另一台电脑的客户端已更新。本机客户端的手动更新说明和固定发布页入口在未连接或协议不兼容时仍可使用。

“检查更新”由已连接设备发起；“下载整包”要求可编辑的主设备且存在更新，包下载到游戏电脑；“退出游戏后安装”还要求 Windows 同机连接、已下载暂存包和明确确认。服务端根据 TCP 对端再次验证回环来源，LAN 请求即使来自主设备也返回 403，不接受 Header 自报本机来源。Android 不运行桌面更新器。版本说明按受限结构展示标题、段落及列表，不执行 HTML；外部发布页仍通过固定项目 URL 白名单校验。

资源读取采用单飞、连接代次和页面订阅代次检查。切换连接或离开页面后，迟到响应不得覆盖新状态。写操作失败不会自动重发，安装权限由服务端再次验证。

## 独立 updater

updater 位于 `native/updater/src/main.rs`。整套产品的目标平台为 Windows 10/11 x64；目标平台声明不等于每个 Windows 10 小版本都已经实测。图形界面在创建任何
HWND 前启用 Per-Monitor DPI Awareness V2，使用系统 message font、逻辑坐标换算、原生进度条，并处理
`WM_DPICHANGED`。这些约束用于避免非 100% 缩放下整窗文字模糊；不能退回由系统对非 DPI-aware 窗口做位图
拉伸的实现。

安装流程为：

1. 展示目标版本并等待用户开始。
2. 验证来源安装与新包；不兼容旧安装在等待和客户端控制前拒绝。随后只读观察窗口创建时打开的游戏进程，等待用户自行保存并退出；准备与等待期间支持取消。
3. 通过新版控制协议请求 Flutter 客户端退出，核对 ACK 中的进程及创建时间，等待真实退出并确认程序组件无占用。
4. 验证旧包及新 staging，复制四个白名单目录内的附加文件，再将当前插件目录移动到备份目录。
5. 把已验证 staging 内容移动到正式目录并复核完整清单与摘要。
6. 持续写入状态、消息和进度，供下一次 Mod 启动读取。
7. 失败时尽力恢复备份，并写入可展示的失败状态。

updater 不从网络下载文件。它接收 `UpdateService` 已校验的 staging 路径，并在替换前后再次检查目标目录名、
全部受管理文件的完整性及 `package-manifest.json`；整包长度、SHA-256 和 ZIP 安全性仍由启动它的 `UpdateService` 负责。

游戏退出观察在更新器窗口创建时通过 `OpenProcess(PROCESS_SYNCHRONIZE)` 打开一次 HANDLE，之后只等待该对象；不再启动 `tasklist`，不使用 `taskkill`，也不发送 `WM_CLOSE` 给游戏。每 250ms 检查退出与取消，每约两秒更新状态。观察失败或超时停止安装，不能视为退出成功。当前游戏启动协议只提供 PID，若原游戏在打开 HANDLE 前已退出且 PID 被重用，可能保守延长等待，但不会操作同号新进程。Flutter 客户端仍另外通过 v2 ACK 的 PID 和创建时间验证身份。

客户端退出控制使用 localhost 32146 的 `mystia-companion-v2:exit\n` 请求。客户端返回带 PID 和 Windows 创建时间的 ACK 后触发退出；更新器据此核对进程身份并有界等待实际退出，随后独占打开安装树内的 EXE/DLL 确认没有冲突句柄，不以固定延迟判断退出成功。连接被拒绝时也必须完成载荷占用检查。此端口是本机客户端控制通道，不是独立客户端的远程更新服务。

“取消安装”、Escape 和窗口关闭操作在校验准备、等待游戏退出、等待客户端退出期间均可取消后续安装。取消与文件替换准入使用同一把锁：取消成功后不进入目录事务；开始替换后必须完成或回滚，不能中断。窗口等待工作线程确认退出后才允许关闭。取消不修改已安装插件或下载暂存包；如果客户端已接受退出请求，更新器不会自动重新启动它。

原生按钮支持 Tab / Shift+Tab、Enter 与 Escape，保持系统字号及 DPI 适配；开始等待后焦点转到取消按钮，完成后转到关闭按钮。纯状态注入测试覆盖准备、等待、最后退出观察和客户端确认的取消边界，真实受控子进程测试覆盖只读 HANDLE、客户端创建时间不匹配和退出超时；这些验证不启动真实游戏。

## 维护规则

- 变更 manifest/catalog 字段时，同步生成器、发布脚本、DTO、缓存归一化和协议 smoke；不要叠加旧字段别名。
- 核心更新权威与版本说明可用性必须继续解耦。
- 不引入 GitHub REST API 作为默认或静默回退。
- 下载 URL 必须绑定已验证 tag 和预期资产名，不能接受 manifest 提供的任意主机。
- updater UI 变更必须在 Win32 DPI-aware 分支验证，浏览器截图不能替代 Windows 多缩放实测。

## 验证

更新协议与前端：

```bash
dotnet run --project tests/update-protocol/UpdateProtocolSmoke.csproj -c Release
# Flutter 简写需要显式的 MYSTIA_FLUTTER_SDK_ROOT；完整契约前先准备并构建 HTTP 宿主。
corepack pnpm flutter:contracts-test
corepack pnpm flutter:test
corepack pnpm audit:package-manifest
```

updater 的跨平台逻辑与 Windows UI 类型检查：

```bash
cargo test --locked --manifest-path native/updater/Cargo.toml
cargo check --locked --manifest-path native/updater/Cargo.toml --features updater-windows-ui-check
```

Linux 的 feature check 不替代 Windows 10/11 x64 MSVC 构建以及 96/120/144/192 DPI 实测。发布资产生成与
发布门禁见[发布流程](local-release.md)，完整验证分层见 [验证指南](validation-guide.md)。

旧 CFG 布局迁移、自动清理、公开旧包手动替换及可选残留清理使用不同的隔离测试入口，依赖与命令见[本地开发](local-development.md)。便携目录、首次初始化、导入及双 ZIP 投影的测试见[验证指南](validation-guide.md)和[平台包说明](../packages/companion_platform/README.md#隔离验证)。这些测试只使用合成资料或明确提供的公开旧包，不能把已有报告、故障注入或本机回环 HTTP 当作双机 LAN、跨 Windows 用户 DPAPI 或真实游戏验收。
