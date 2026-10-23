# 更新系统

更新日期：2026-10-23

本文说明版本检测、累计版本说明、包下载和独立更新程序的完整链路。正式发布如何生成对应资产见
[发布流程](local-release.md)；接口认证与方法矩阵见 [本地 API](local-api.md)。

## 职责边界

更新系统由四个边界清晰的部分组成：

- Mod 内的 `UpdateService` 是唯一外部网络访问者和更新状态权威。
- GitHub Release 上的 manifest、catalog 与 ZIP 是只读发布资产。
- 伴随窗口只通过本地 API 展示状态和发起用户动作。
- 独立 updater 在游戏退出边界之外替换 Mod 目录，并回写安装结果。

前端不直接查询 GitHub，不自行比较版本，也不直接解压覆盖文件。Mod 不在游戏进程仍运行时替换自己的程序集。
更新检测与下载不会关闭游戏。打开独立 updater 后，仍须用户明确开始安装；Windows 的开始动作会请求游戏正常关闭，强制结束另有独立用户动作。

## 发布资产

完整 Windows 树在 `companion/` 内必带 EXE、Flutter engine/data/插件及应用本地 `msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll`，三种语言的清单验证器逐项拒绝遗漏或大小写不规范。运行库从锁定 VS 正式 Redist 复制并带许可声明。独立 updater 使用根 Cargo 配置静态链接 CRT，复制到 `updates/runner` 时仍是独立 EXE；打包验证真实导入表，不能依赖开发机全局 VC 安装掩盖动态链接。

启动和许可关键文件也必须明确列入清单且非空：`companion/data/app.so`、`companion/data/icudtl.dat`、`companion/data/flutter_assets/` 下的 `AssetManifest.bin`、`FontManifest.json`、`NOTICES.Z`，以及 `companion/THIRD-PARTY-NOTICES.txt`。不能用任意 `data/` 文件替代这些关键项；普通非关键资源允许零长度。打包在创建 staging 前检查 Windows 关键文件，再由三语言整包验证器核对完整集合、大小和摘要。

每个可安装 Release 至少包含：

- `update-manifest.json`：该版本的通道、包名、大小、SHA-256、Release URL 和可选 catalog 元数据。
- 版本固定的 Mod ZIP 包。
- `update-catalog.json`：以该 Release 为 owner 的累计版本说明目录。

manifest 是更新可用性和包完整性的权威。catalog 只负责展示从当前版本到最新版本之间的公开版本和说明；
catalog 获取或校验失败时，更新仍可检测、下载和安装，只把“版本说明”标记为不可用。

正式版发现入口允许使用下文的 `latest/download/update-manifest.json`；候选确认后的 ZIP、catalog 和预发布候选 manifest 均使用 tag 固定的 Release 下载地址。manifest 与 catalog 都执行严格 schema、owner、通道、
SemVer、数量、大小、URL 和 SHA-256 门禁。catalog 的版本必须严格升序，包含唯一 owner，且不能包含晚于
owner 的版本。ZIP 下载同时校验声明长度与 SHA-256，并在 staging 中拒绝路径穿越和不符合包结构的内容。

### Flutter 完整运行树与业务组件成套校验

每份插件包必须同时包含主 Mod DLL、`MystiaStewardCompanion.Contracts.dll`、`MystiaStewardCompanion.Business.dll`、Windows 客户端和 updater，并附唯一 `package-manifest.json`。清单列出三个 C# DLL、独立 updater 及 `companion/` 下全部 EXE、DLL、插件和 data 资源的相对路径、字节数与 SHA-256，保留源码与工具链身份。不接受缺项、重复字段/路径、大小写冲突、额外文件、链接或混装；程序文件不得为空，合法空资源可存在。

PowerShell/Bash 打包入口先验证全部输入，再事务替换 dist；C# 下载服务与 Rust updater 在 staging/安装后分别核对清单。清单用于发现缺包或组件混装，外部可信来源仍由 Release manifest 的整包 SHA-256 与受限下载地址保证。业务协议版本为 1，客户端协议版本为 2；新客户端与宿主成套交付。Node、C#、Rust 验证器使用相同人工载荷矩阵验证判断一致。ZIP 在解压前检查条目上限、规范路径、链接属性和展开总量，解压期间再次限制实际写入长度。

本地开发安装可使用 `mods/bepinex/tools/install-local-package.ps1`：显式提供游戏、包、存档和工作区记录目录，默认只读校验，`-Execute` 才成套替换。它要求相关进程停止、验证原件备份，并对配置、存档及核心文件做前后摘要检查。安装后校验失败恢复完整原插件；工作区备份保留用于人工回退。附加文件仅保留显式白名单，默认 logs、diagnostics、exports、user-data；发现未知旧文件立即停止，人工审查后才通过 PreserveRelativePaths 指定保留。事务目录位于游戏同级，完整备份位于仓库 temp/flutter-install-<GUID>，避免 BepInEx 扫描到重复 DLL。离线事务验证入口为 `pwsh -NoProfile -File tests/csharp-business-package/install-transaction.ps1`。

旧 updater 无参数运行可能在插件根目录留下内容为 `missing --game-pid` 的失败状态。已审查该残留时，显式传入 `-ArchiveLegacyUpdateStatus`，安装器会先保留完整原件，再把原字节归档到 `user-data/legacy-update-status.json` 并记录前后摘要。未知内容、归档目标冲突或摘要变化均拒绝；不开启开关时，旧根文件仍按未知附加文件处理。此一次性归档不会扩大 Rust updater 的四个永久保留目录。

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
由已验证 manifest/staging 推导出的稳定状态，不把半成品当作可安装包。未来 schema、损坏缓存和旧 manifest
均 fail closed。

本地 API 使用以下操作端点：

- `POST /updates/status`
- `POST /updates/check`
- `POST /updates/download`
- `POST /updates/install-on-exit`

`status` 也可能合并 updater 回写状态并清理由本程序拥有的已消费文件，因此不是纯 GET。接口 DTO 和请求边界
由 [本地 API](local-api.md) 维护。

## Flutter 伴随窗口

当前实现位于 `apps/companion-flutter/lib/features/settings.dart` 的“成套更新”设置页，网络组合根为 `apps/companion-flutter/lib/app/live_workbench_model.dart`。页面展示当前/最新版本、更新与安装状态、核心错误、版本说明错误和服务器返回的版本列表。

“检查更新”由已连接设备发起；“下载整包”要求可编辑的主设备且存在更新；“退出游戏后安装”还要求 Windows 同机连接、已下载暂存包和明确确认。Android 不运行桌面更新器。版本说明目前作为普通文本显示，不执行 HTML，也不把说明中的任意地址交给外部打开程序。

资源读取采用单飞、连接代次和页面订阅代次检查。切换连接或离开页面后，迟到响应不得覆盖新状态。写操作失败不会自动重发，安装权限由服务端再次验证。

## 独立 updater

updater 位于 `native/updater/src/main.rs`。整套产品的目标平台为 Windows 10/11 x64；目标平台声明不等于每个 Windows 10 小版本都已经实测。图形界面在创建任何
HWND 前启用 Per-Monitor DPI Awareness V2，使用系统 message font、逻辑坐标换算、原生进度条，并处理
`WM_DPICHANGED`。这些约束用于避免非 100% 缩放下整窗文字模糊；不能退回由系统对非 DPI-aware 窗口做位图
拉伸的实现。

安装流程为：

1. 展示目标版本并等待用户开始。
2. 请求游戏正常关闭并等待目标进程退出；强制结束是显式的独立用户动作。
3. 通过新版控制协议请求 Flutter 客户端退出，核对 ACK 中的进程及创建时间，等待真实退出并确认程序组件无占用。
4. 验证旧包及新 staging，复制四个白名单目录内的附加文件，再将当前插件目录移动到备份目录。
5. 把已验证 staging 内容移动到正式目录并复核完整清单与摘要。
6. 以原子方式持续写入状态、消息和进度，供下一次 Mod 启动读取。
7. 失败时尽力恢复备份，并写入可展示的失败状态。

updater 不从网络下载文件。它接收 `UpdateService` 已校验的 staging 路径，并在替换前后再次检查目标目录名、
全部受管理文件的完整性及 `package-manifest.json`；整包长度、SHA-256 和 ZIP 安全性仍由启动它的 `UpdateService` 负责。

当前实现的游戏退出等待每约 700ms 通过 `tasklist /FI "PID eq ..." /NH` 查询目标 PID，每约两秒更新进度，直到退出或超时；它没有为游戏持有创建时间绑定的等待句柄。客户端退出则由 v2 协议与已核验的进程创建时间单独确认，不能把这两种等待机制混为一谈。Windows 强制结束只使用指定 PID 的 `taskkill /F`，不带 `/T`，以免将游戏启动的 updater 子进程一起关闭。

“取消”只在尚未开始安装时有效。工作线程启动后，关闭按钮被禁用，窗口关闭消息也不会取消等待或文件替换；失败或完成后才可关闭。当前分支不支持等待期间取消，不引用其他分支的取消行为或测试作为本实现的证明。

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

客户端退出控制使用 localhost 32146 的 `mystia-companion-v2:exit\n` 请求；客户端先返回 PID 和 Windows 创建时间，再触发退出。更新器核对进程身份并有界等待真实客户端进程结束，随后独占打开 EXE/DLL 确认没有冲突句柄；不以固定延迟判断客户端退出成功。目录替换后再次校验整包，失败恢复完整旧树并保留回退证据。
