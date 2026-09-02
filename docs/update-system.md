# 更新系统

更新日期：2026-09-02

本文说明版本检测、累计版本说明、包下载和独立更新程序的完整流程。正式发布如何生成对应资产见
[发布流程](local-release.md)；接口认证与方法矩阵见 [本地 API](local-api.md)。

## 职责边界

更新系统由四个边界清晰的部分组成：

- Mod 内只有 `UpdateService` 可以访问外部网络并维护生效的更新状态。
- GitHub Release 上的更新清单、版本目录与 ZIP 是只读发布资产。
- 伴随窗口只通过本地 API 展示状态和发起用户动作。
- 独立更新程序在游戏退出后替换 Mod 目录，并回写安装结果。

前端不直接查询 GitHub，不自行比较版本，也不直接解压覆盖文件。Mod 不在游戏进程仍运行时替换自己的程序集。
更新检测、下载和安装都不会自动关闭游戏；安装必须由用户明确发起。

## 发布资产

每个可安装 Release 至少包含：

- `update-manifest.json`：该版本的通道、包名、大小、SHA-256、Release URL 和可选版本目录元数据。
- 版本固定的 Mod ZIP 包。
- `update-catalog.json`：以该 Release 为所属版本的累计版本说明目录。

更新清单决定更新是否可用以及安装包是否完整。版本目录只负责展示从当前版本到最新版本之间的公开版本和说明；
版本目录获取或校验失败时，更新仍可检测、下载和安装，只把“版本说明”标记为不可用。

所有资产必须通过 Git 标签固定的 Release 下载地址读取。更新清单与版本目录都执行严格的结构、所属版本、通道、
SemVer、数量、大小、URL 和 SHA-256 检查。版本目录的版本必须严格升序，包含唯一所属版本，且不能包含晚于
所属版本的条目。ZIP 下载同时校验声明长度与 SHA-256，并在暂存目录中拒绝路径穿越和不符合包结构的内容。

## 版本发现策略

为了避免大量中国大陆用户共享 GitHub 出口节点时频繁命中 REST API 未认证速率限制，正式版默认读取：

```text
https://github.com/blockshy/mystia-steward-companion/releases/latest/download/update-manifest.json
```

这个路径只发现最新正式版，不调用 GitHub REST API。启用“包含预发布版本”时，服务读取公开
`releases.atom`，按 SemVer 选择候选版本标签，再读取该版本标签固定的更新清单。Atom 只用于候选发现，最终版本和
安装包仍由更新清单校验确认。

发现比当前版本新的更新清单后，服务读取其版本标签固定的版本目录，并筛选 `当前版本 < release <= 最新版本`
的条目。前端按从旧到新的顺序逐一展示，所以跨多个版本升级时不会只看到最新一版说明。

## Mod 内状态机

核心实现位于 `mods/bepinex/src/Updates/UpdateService.cs`。检测、下载和安排安装共享同一个操作锁，不能并发
写更新状态或暂存目录。

自动检测成功后按用户设置的 1–168 小时间隔安排下一次检查；连续失败采用 15 分钟、30 分钟、1 小时、
2 小时、4 小时、6 小时封顶的退避。调度线程关停时停止接受新操作，取消并等待当前操作；取消会恢复到可解释
的稳定状态，并允许下一次启动立即继续检测。

启动恢复只处理本程序拥有的临时目录和状态。上次进程在 `checking` 或 `downloading` 等瞬态中断时，服务回到
由已验证更新清单和暂存目录推导出的稳定状态，不把半成品当作可安装包。未来结构、损坏缓存和旧更新清单
都会使更新保持不可用。

本地 API 使用以下操作端点：

- `POST /updates/status`
- `POST /updates/check`
- `POST /updates/download`
- `POST /updates/install-on-exit`

`status` 也可能合并 updater 回写状态并清理由本程序拥有的已消费文件，因此不是纯 GET。接口 DTO 和请求边界
由 [本地 API](local-api.md) 维护。

## 伴随窗口

`apps/companion/src/companion/features/updates/useUpdateManager.ts` 是前端唯一更新控制器。它分别维护状态读取
和用户动作的请求轮次；API 地址、Token 或连接修订号改变时取消旧请求并拒绝迟到结果。主动变化
阶段短轮询，稳定阶段低频轮询，连续读取失败使用有上限的退避等待。

设置页展示：

- 当前版本、最新版本、通道、检测和下载/安装状态。
- 从当前版本到最新版本的完整 Release 列表和每版 Markdown 说明。
- 版本目录有独立的失败提示，不覆盖核心更新状态。
- 手动检测、下载、退出后安装和打开项目 Release 页动作。

顶部更新提示条是非模态提醒；“24 小时后提醒”按 API 地址与目标版本标签保存，不能隐藏后续新版本。Release
说明使用受限 Markdown，不渲染原始 HTML。说明内链接不直接变成任意外链；只有经过项目 Release URL 白名单
校验的按钮才能交给 Tauri 打开。

前端入口包括：

- `apps/companion/src/companion/features/updates/UpdateSettingsPanel.tsx`
- `apps/companion/src/companion/features/updates/UpdateNoticeBar.tsx`
- `apps/companion/src/companion/features/updates/useUpdateManager.ts`
- `apps/companion/src/companion/features/updates/update-request-coordinator.ts`
- `apps/companion/src/companion/features/updates/update-polling.ts`

## 独立更新程序

更新程序位于 `apps/companion/src-tauri/src/bin/updater.rs`，最低支持 Windows 10 1703。图形界面在创建任何
HWND 前启用 Per-Monitor DPI Awareness V2，使用系统 message font、逻辑坐标换算、原生进度条，并处理
`WM_DPICHANGED`。这些约束用于避免非 100% 缩放下整窗文字模糊；不能退回由系统对非 DPI-aware 窗口做位图
拉伸的实现。

安装流程为：

1. 展示目标版本并等待用户开始。
2. 请求游戏正常关闭并等待目标进程退出；强制结束是显式的独立用户动作。
3. 将当前插件目录移动到备份目录。
4. 把已验证暂存内容移动到正式目录并复核结构。
5. 以原子方式持续写入状态、消息和进度，供下一次 Mod 启动读取。
6. 失败时尽力恢复备份，并写入可展示的失败状态。

更新程序不从网络下载文件。它接收 `UpdateService` 已校验的暂存路径，并在替换前后再次检查目标目录名和
最小文件集合；包的长度、SHA-256 和 ZIP 安全性仍由启动它的 `UpdateService` 负责。

## 维护规则

- 变更 manifest/catalog 字段时，同步生成器、发布脚本、DTO、缓存归一化和协议 smoke；不要叠加旧字段别名。
- 安装包判定与版本说明可用性必须继续解耦。
- 不引入 GitHub REST API 作为默认或静默备用路径。
- 下载 URL 必须绑定已验证的版本标签和预期产物名，不能接受更新清单提供的任意主机。
- updater UI 变更必须在 Win32 DPI-aware 分支验证，浏览器截图不能替代 Windows 多缩放实测。

## 验证

更新协议与前端：

```bash
dotnet run --project tests/update-protocol/UpdateProtocolSmoke.csproj -c Release
corepack pnpm audit:updates
corepack pnpm audit:updates:ui
```

updater 的跨平台逻辑与 Windows UI 类型检查：

```bash
cargo test --manifest-path apps/companion/src-tauri/Cargo.toml --bin mystia-steward-companion-updater
cargo check --manifest-path apps/companion/src-tauri/Cargo.toml --bin mystia-steward-companion-updater --features updater-windows-ui-check
```

Linux 的 feature check 不替代 Windows 10 1703+ MSVC 构建以及 96/120/144/192 DPI 实测。发布资产生成与
发布前检查见[发布流程](local-release.md)，完整验证分层见 [验证指南](validation-guide.md)。
