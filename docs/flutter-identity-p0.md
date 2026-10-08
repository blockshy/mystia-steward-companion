# Flutter 配置与设备身份迁移 P0

本探针验证两条可行路线：受限 `Storage.getItem` 导出 → Dart 白名单导入；旧端不在线时，新设备通过**现有真实 Mod HTTP API**完成注册、读取 authority、同步旧主设备配置、本地落盘回读、ACK 和显式确认主设备。全部身份、Token 和服务端文件均为新建合成数据，不操作用户真实主设备。

这项 P0 已证明迁移契约和恢复协议可以复用。尚未交付安装版导出入口、生产客户端的完整迁移向导及 Windows/Android 凭据安全存储适配器，也没有读取安装版的 localStorage。[独立安全存储 P0](../tests/flutter-storage-probe/README.md) 已取得双平台合成凭据实测证据，不能当作生产适配器已经交付。浏览器 Storage API、Linux 上真实 HTTP 服务和物理设备目录元数据是三种独立证据，不能互相替代。

## 当前旧数据位置与读取边界

位置依据包括锁定 Tauri 2.11.2 的 `manager/webview.rs`（Windows 默认 `LocalData/identifier`）与 `manager/mod.rs`（默认 `http://tauri.localhost`）、[Tauri 配置](../apps/companion/src-tauri/tauri.conf.json)、[Android 配置](../apps/companion/src-tauri/tauri.android.conf.json)。当前工程未覆盖 `dataDirectory` 或启用 HTTPS scheme。

| 平台 | 已确认位置/事实 | 本次实际读取 |
| --- | --- | --- |
| Windows 物理机 | `%LOCALAPPDATA%/com.tyukki.mystia-steward-companion/EBWebView/Default/Local Storage/leveldb` 的固定目录均存在且非 reparse；根目录的直接 `Default` 不存在 | [固定路径脚本](../scripts/read-flutter-identity-storage.ps1) 只取目录存在性、类型、reparse 元数据，无枚举、文件内容或 Token |
| Android 物理机 | 正式包 `com.tyukki.mystia.steward.companion` 为 1.3.1 / versionCode 1003001；User 0 dataDir 为 `/data/user/0/com.tyukki.mystia.steward.companion`，无 DEBUGGABLE | 对固定 `app_webview` 路径的 `ls -ld` 返回 Permission denied；不能据此认定该子目录存在、确认其内部布局或读取 localStorage |

合法的旧数据导出入口应在旧客户端所属 WebView 的原 origin 内，调用它自己的 Storage API，再显式交给新端。Android 要求现有应用身份下的代码执行；保留包名与签名只能保留应用身份，不能让 Flutter 自动获得 WebView 的 Storage API。当前安装版没有显式配置导出功能，因此正式迁移需增加该入口或走新设备恢复路线。本 P0 的导出函数可用于后续适配，不能当作已可直接执行的用户迁移工具。

禁止扫描用户目录、尝试解析未知 LevelDB、把浏览器 Cookie/缓存当配置、复制整个 WebView 目录给 Flutter，或绕过 Android 私有目录权限。旧目录是否存在不证明文件可安全解析。Windows/Android 实际迁移器还需平台存储适配与安装来源证明。

## 白名单文件契约

[导出函数](../tests/identity-migration/legacy-export.mjs) 只读当前 `mystia-steward-companion-` 前缀下的明确键，不枚举 Storage，不调用现有会顺便迁移/删除旧键的读取器。字段依据为 [storage.ts](../apps/companion/src/companion/storage.ts)、[preferences.ts](../apps/companion/src/companion/preferences.ts)、[theme.ts](../apps/companion/src/lib/theme.ts) 和 [client-identity.ts](../apps/companion/src/companion/client-identity.ts)。早于当前规范的旧前缀、旧透明度键与未知键不自动猜测或转换。

JSON 最大 16 KiB，根字段为 `schemaVersion: 1`、`kind: mystia-settings-export`、`source`、`settings`；显式身份迁移时才有 `deviceIdentity`。`source` 包含 platform、精确 origin 和 installationBinding。缺失设置保留新端默认值，未知字段、未来版本、错误类型/枚举/范围均拒绝，源文件不变。

| 设置组 | 白名单与校验 |
| --- | --- |
| 连接与外观 | endpoint 仅 HTTP(S) authority，可选末尾 `/`，不含凭据、query、fragment；theme、navigation 为当前明确枚举 |
| 窗口与手柄 | fontScalePercent 90–130、步长 5；backgroundOpacity 0.2–1；contentOpacity 0.35–1；focusSwitchBehavior 枚举；focusSwitchCooldownMs 250–2000；alwaysOnTop、gamepadNavigation 为布尔值 |
| 本地界面偏好 | showDebugDetails、missionListModuleEnabled、rareGuestInvitationModuleEnabled、focusCompact 为布尔值；focusRecipeLimit/focusBeverageLimit 整数 1–20；customRecipeGroupMode 为 recipe/customer |

默认导出**不读 Client ID**；显式身份导出仅保留合法的 `clientId`。导入还要求显式确认、同平台及相同安装绑定。探针的安装绑定是合成 fixture 约束，JSON 中自报的 binding 不是身份凭据：生产端必须从可信的平台适配器验证来源，不能仅比较文件里的字符串就认定归属。在该证明完成前，正式迁移应生成新 Client ID 并走恢复路线。

Token 不进入导出文件；Token 的重新配对/输入与平台安全存储是独立流程。共享配置、收藏和自定义配方由 Mod authority 保留，不能把旧 localStorage 当权威副本覆盖服务端。draft、lease、generation、未完成请求、正在运行的自动化状态一律不迁移。

[Dart 导入器](../tests/flutter-identity-probe/lib/migration.dart) 在新建测试目录内先写临时文件、flush、回读后 rename。已有目标文件（包括损坏或未来版本）保留并拒绝覆盖；测试还验证独立凭据文件不变。该验证是 Linux 本地文件行为，不能替代 Windows/Android 存储适配器的原子性和崩溃恢复测试。

## 旧端不可启动的恢复顺序

[Dart 恢复状态机](../tests/flutter-identity-probe/lib/recovery.dart) 使用真实 [LocalApiServer](../mods/bepinex/src/LocalApi/LocalApiServer.cs) 与 [CompanionDeviceAuthorityStore](../mods/bepinex/src/LocalApi/CompanionDeviceAuthorityStore.cs)，不复制路由、注册逻辑或 HTTP mock。协议仍为 device protocol 1、共享 profile schema 4。

1. 生成新 Client ID，以 automationEnabled=false 的安全默认 profile 注册。现存旧主设备仍是 authority，离线不影响读取其持久化 profile。
2. `GET /devices` 固定 registryId、旧 primaryDeviceId、authorityRevision，校验当前/生效 profile 的规范化 SHA-256。
3. 以当前 authorityRevision 执行 `POST /devices/sync`。新设备保持 secondary，获得待应用 profile 和 pendingSyncId；不先设主设备。
4. 将 profile、registry/client identity、revision/hash 和 `runtimeExecutionEnabled:false` 写入新的本地文件并回读核验。落盘失败时不发送 ACK，也不提升主设备。
5. `POST /devices/sync-ack` 精确提交 syncId/revision/hash，验证 pending 已清除及 appliedProfileRevision 一致。未知或失败 ACK 不自动重发，不允许后续提升。
6. 等待显式用户确认。确认后重新读 authority；registry/旧主设备/profile hash/revision 任一变化则停止。以最新 authorityRevision 执行 `POST /devices/primary`，核验生效 profile 未变。探针中的确认是明确的测试输入，未替用户确认真实设备。

全过程不请求 lease，不调用游戏写入/执行入口。恢复的 profile 可以包含旧用户保存的 automationEnabled=true 偏好，但它只是权威配置数据，**不恢复执行资格或正在运行的任务**。

服务端的已存在 registry 假设必须明确：空 registry 的第一个注册设备会由现有协议自动成为主设备，这属于首次配置，不是旧主设备恢复。本探针不把空库注册伪装成成功迁移。32 台满额返回 409，损坏/未来 registry 返回 503，均停止且保留原文件。满额时需已有合法设备身份与用户明确选择才能按现有设备管理流程处理离线记录；不可清空注册表、随机删除或冒用旧 Client ID。

真实 HTTP 联调确认，现有 Mod TCP HTTP reader 不接受客户端默认 chunked JSON。Dart 必须先生成 UTF-8 bytes、设置精确 Content-Length，再发送；错误 Token 为 401、未注册设备读取为 409、错误请求字段为 400。不会自动更换 endpoint、重注册、重试未知写入或把失败解释为成功。

## 验证入口与证据

所有命令使用仓库锁定 Node/Corepack、Flutter/Dart、.NET SDK；先按本地开发文档设置 PATH。fixture 必须是仓库 `temp/` 下新的单层目录，不覆盖旧证据。

```bash
node scripts/build-flutter-identity-probe.mjs
node scripts/test-flutter-identity-export.mjs "$PWD/temp/flutter-identity-fixtures-<unique>"
MYSTIA_IDENTITY_FIXTURES=temp/flutter-identity-fixtures-<unique> corepack pnpm test:dotnet6 identity-migration
```

构建脚本检查锁定 Flutter/.NET SDK 与 references.lock，SDK 10 Release 编译实际 Mod 源码链接的隔离 host，Dart analyze 后预编译 kernel。唯一 `.NET 6` 入口运行锁镜像中的真实服务；直接执行预编译 Dart kernel 避免容器内 Dart CLI analytics 写入未知用户目录。没有替换 Unity wrapper、没有启动游戏、没有用全局较新运行时替代 .NET 6。

[浏览器脚本](../scripts/test-flutter-identity-export.mjs) 使用独立 Chromium context，拦截全部请求到合成页面，在精确 origin 内真实执行 localStorage/Storage API。Windows/Android 两份 fixture 仅代表配置中的平台分支，不代表相应物理 WebView。当前本机使用锁定 Playwright 依赖对应 Chromium Headless Shell 148.0.7778.96 / build 1223；Ubuntu 26 下载采用显式 `PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu24.04-x64`，已实际运行成功，未将其冒充 WebView2。

当前 14 组断言见 [probe.dart](../tests/flutter-identity-probe/bin/probe.dart)：白名单、身份显式归属、错误版本/字段、保留已有文件、新身份、真实鉴权/字段约束、离线主设备、sync CAS、Android platform 下失败 apply 不 ACK、应用/ACK、用户确认以及满额/损坏/未来 registry。每个 host 使用独立新目录，结束后验证服务正常退出并清理自己创建的服务端目录；报告只含动作/HTTP 状态，不记录 Token。

2026-10-08 的本地证据：

- `temp/flutter-identity-fixtures-20261008-02/export-evidence.json`：实际浏览器 Storage API 白名单通过。
- `temp/flutter-identity-fixtures-20261008-02/identity-result.json`：首次 14 组真实 HTTP 全通过。
- `temp/flutter-identity-fixtures-20261008-03/export-evidence.json` 与 `identity-result.json`：最终版再次 14 组通过，报告分别标明 feasibilityVerified=true、installedDataMigrationVerified=false、gameRuntimeVerified=false；原证据未覆盖。
- `temp/flutter-identity-fixtures-20261008-02/windows-storage-location.json`：Windows 固定路径元数据，无文件内容读取。
- `temp/flutter-p0/android-device-storage-20261008/readonly-metadata.txt` 与 `private-directory-boundary-raw.txt`：Android 安装元数据与实际 Permission denied；不采用仅包含 exit 的旁路文件作为拒绝原文证据。

后续产品阶段复用这些契约，但必须另行实现并验证：安装版显式导出入口、来源证明与凭据安全存储、真实安装数据迁移及崩溃恢复、用户可理解的逐步确认界面。P0 的合成确认和 Linux 文件回读不能视为这些产品能力已交付。
