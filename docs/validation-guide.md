# 验证指南

更新日期：2026-08-19

本文档负责回答“改动后应运行哪些验证”。它只记录测试入口、选择规则和平台边界；每项测试的完整断言、
fixtures 和禁止路径以 `tests/` 下的源码为权威，业务契约不在这里重复维护。

环境安装与日常启动见[本地开发与构建](local-development.md)，Android 专项见
[Android 开发](android-development.md)，发布前门禁见[发布流程](local-release.md)。

## 基本原则

1. 先运行 `corepack pnpm toolchain:check`，确认测试使用锁定工具链；
2. 先做受影响模块的编译和静态检查，再运行该模块的 smoke/audit；
3. 修改跨边界协议时同时验证生产者和消费者，例如 C# API、Tauri proxy 与 React UI；
4. UI 自动化通过不替代至少一次人工视觉巡检，运行时 smoke 通过也不替代游戏实测；
5. Windows、Android 和真实游戏行为只能在对应平台确认，Linux 结果不得扩大解释；
6. 测试失败时修复实现或更新经过确认的规范断言，不添加兼容分支来绕过失败。

除明确标注外，命令均从仓库根目录运行。pnpm 命令统一经 Corepack 调用。

## 基线验证

按变更触达的每一行叠加执行，不是只选一行：

| 变更范围 | 最低验证 |
| --- | --- |
| 仅 Markdown 文档 | `git diff --check`，检查相对链接和命令仍存在 |
| React、TypeScript、CSS、前端数据 | `corepack pnpm lint`、`corepack pnpm build` |
| Tauri Rust | `cargo check --manifest-path apps/companion/src-tauri/Cargo.toml` |
| Tauri 窗口切换或聚焦 | 上项加 `cargo test --manifest-path apps/companion/src-tauri/Cargo.toml --lib` |
| 独立 updater | updater 单测、Linux Windows-UI 类型检查及 Windows 实机 |
| C# Mod 或本地 API | `dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release`，再选专项 smoke |
| Android Rust/Gradle/签名 | Android 工具链检查、对应 APK 构建及 Android 专项 audit |
| 构建、打包或引用恢复脚本 | 对应 build-artifacts/build-references audit |
| workflow、版本或发布脚本 | release policy、GitHub Actions、toolchain audit，并按发布流程做全量验证 |

文档或注释改动通常无需重编译产品，但修改文档中的命令、文件路径或版本时，应实际核对对应脚本和锁文件，
不能只检查 Markdown 语法。

## 前端审计选择

package scripts 是聚合入口；其当前子测试列表以 [`package.json`](../package.json) 为准。

| 功能范围 | 命令 |
| --- | --- |
| 通用响应式布局 | `corepack pnpm audit:ui` |
| 推荐主计划、特殊经营候选与阻塞诊断 | `corepack pnpm audit:recommendations` |
| 自定义推荐料理 | `corepack pnpm audit:custom-recipes` |
| 料理与酒水收藏 | `corepack pnpm audit:favorites` |
| 自动化前端恢复与运行时动作审计 | `corepack pnpm audit:automation` |
| 连接恢复 | `corepack pnpm audit:connection-recovery` |
| 主设备与配置权威 | `corepack pnpm audit:device-authority`、`corepack pnpm audit:device-authority:ui` |
| 字号与缩放 | `corepack pnpm audit:font-scale` |
| 设置与帮助 | `corepack pnpm audit:settings-help` |
| 订单状态与展示 | `corepack pnpm audit:service-orders` |
| 更新协议的前端投影 | `corepack pnpm audit:updates`、`corepack pnpm audit:updates:ui` |
| 游戏界面目标发布 | `corepack pnpm audit:ui-pinning` |
| 特殊经营前端与运行时集合 | `corepack pnpm audit:special-business` |
| 稀客邀请 | `corepack pnpm audit:rare-guest-invitations`、`corepack pnpm audit:rare-guest-invitations:ui` |
| 手柄输入与焦点 | `corepack pnpm audit:gamepad` |
| BepInEx 控制台 UI | `corepack pnpm audit:logs:console` |
| 任务列表与运行时投影 | `corepack pnpm audit:runtime-missions`、`corepack pnpm audit:runtime-missions:ui` |

含 Playwright 的聚合命令需要下文的 mock/preview 环境。纯源码 audit 通常会自行启动所需 fixture；失败输出与
对应测试文件说明优先于本表的简述。

## Playwright 巡检

首次运行安装锁文件中的 Playwright Chromium：

```bash
corepack pnpm exec playwright install chromium
```

先按[本地开发与构建](local-development.md#mock-api-与浏览器预览)启动 mock API 和 Vite preview，
再在另一个终端设置统一地址：

```bash
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
corepack pnpm audit:ui
```

其他 Playwright 聚合命令使用相同环境变量。Ubuntu 环境已有兼容系统浏览器时可显式指定：

```bash
PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH=/usr/bin/google-chrome \
MYSTIA_APP_URL=http://127.0.0.1:4173 \
MYSTIA_API_URL=http://127.0.0.1:32145 \
corepack pnpm audit:runtime-missions:ui
```

专项脚本负责自己的视口、字号、键盘/手柄模拟、截图和报告目录。人工巡检至少复核测试覆盖的最窄桌面宽度、
手机宽度、焦点顺序、横向溢出、弹窗遮罩、加载/空/错误态；不能只查看默认桌面截图。

## Tauri Rust 与独立 updater

普通 Tauri 变更：

```bash
cargo check --manifest-path apps/companion/src-tauri/Cargo.toml
cargo test --manifest-path apps/companion/src-tauri/Cargo.toml --lib
```

独立 updater 逻辑和 DPI 换算：

```bash
cargo test \
  --manifest-path apps/companion/src-tauri/Cargo.toml \
  --bin mystia-steward-companion-updater

cargo check \
  --manifest-path apps/companion/src-tauri/Cargo.toml \
  --bin mystia-steward-companion-updater \
  --features updater-windows-ui-check
```

第二项只让 Linux 类型检查完整 Win32 UI 分支，不替代 Windows 10 1703+ 的 MSVC 构建。修改 updater
窗口、字体或进度展示后，还要在 Windows 100%/125%/150%/200% 缩放和跨显示器移动中实测。

## C# smoke 选择

先确保锁定 References 已恢复，再运行 Mod 构建。下列按生产模块分组，选择所有受影响组中的项目。
`AutomationCookingJobSmoke`、`UiPinningRuntimeSmoke` 和 `RuntimeTargetRecipeVariantSmoke` 会安装真实动态补丁；
Windows 可在支持的运行时直接执行，Linux 必须改用后文的锁定容器入口。

### 本地 API、存储、日志与更新

```bash
dotnet run --project tests/local-api-listener-lifecycle/LocalApiListenerLifecycleSmoke.csproj -c Release
dotnet run --project tests/local-api-client-handlers/LocalApiClientHandlersSmoke.csproj -c Release
dotnet run --project tests/local-api-method-matrix/LocalApiMethodMatrixSmoke.csproj -c Release
dotnet run --project tests/local-api-storage/LocalApiStorageSmoke.csproj -c Release
dotnet run --project tests/main-thread-command/MainThreadCommandSmoke.csproj -c Release
dotnet run --project tests/snapshot-signature/SnapshotSignatureSmoke.csproj -c Release
dotnet run --project tests/update-protocol/UpdateProtocolSmoke.csproj -c Release
dotnet run --project tests/aggregate-mod-log-lifecycle/AggregateModLogLifecycleSmoke.csproj -c Release
dotnet run --project tests/bepinex-console-window/BepInExConsoleWindowSmoke.csproj -c Release
```

连接设备权威或 profile 写入同时运行前端 `audit:device-authority` 与 UI 巡检；更新协议同时运行
`audit:updates` 和 `audit:updates:ui`。

### 运行时反射、静态数据与经营生命周期

```bash
dotnet run --project tests/runtime-reflection/RuntimeReflectionSmoke.csproj -c Release
dotnet run --project tests/runtime-static-data-catalog/RuntimeStaticDataCatalogSmoke.csproj -c Release
dotnet run --project tests/runtime-cooker-snapshot/RuntimeCookerSnapshotSmoke.csproj -c Release
dotnet run --project tests/night-business-lifecycle/NightBusinessLifecycleSmoke.csproj -c Release
dotnet run --project tests/night-business-automation-gate/NightBusinessAutomationGateSmoke.csproj -c Release
```

修改反射 helper、BepInEx 版本锁定、厨具枚举或经营 generation 时，应先运行本组，再运行依赖这些基础的
订单、任务或自动化组。

### 任务系统

```bash
dotnet run --project tests/runtime-mission-load-seed/RuntimeMissionLoadSeedSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-definition/RuntimeMissionDefinitionSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-diagnostic/RuntimeMissionDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-scheduled-event-diagnostic/RuntimeScheduledEventDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-available-missions/RuntimeAvailableMissionsSmoke.csproj -c Release
dotnet run --project tests/runtime-serve-in-work-diagnostic/RuntimeServeInWorkMissionDiagnosticSmoke.csproj -c Release
dotnet run --project tests/runtime-tracked-missions/RuntimeTrackedMissionsSmoke.csproj -c Release
dotnet run --project tests/runtime-mission-recipe-priority/RuntimeMissionRecipePrioritySmoke.csproj -c Release
corepack pnpm audit:runtime-missions
corepack pnpm audit:runtime-missions:ui
```

### 订单、自动化与稀客

```bash
dotnet run --project tests/special-order-runtime-capture/SpecialOrderRuntimeCaptureSmoke.csproj -c Release
dotnet run --project tests/runtime-order-terminal-receipt/RuntimeOrderTerminalReceiptSmoke.csproj -c Release
dotnet run --project tests/rare-order-identity-matching/RareOrderIdentityMatchingSmoke.csproj -c Release
dotnet run --project tests/runtime-automation-control/RuntimeAutomationControlSmoke.csproj -c Release
dotnet run --project tests/automation-cooking-job/AutomationCookingJobSmoke.csproj -c Release
dotnet run --project tests/rare-guest-invitation-readonly/RareGuestInvitationReadOnlySmoke.csproj -c Release
corepack pnpm audit:automation
```

配置权威、总控或阶段开关影响 automation lease 时，再叠加本地 API 存储组和
`audit:device-authority` / `audit:connection-recovery`。

### 游戏 UI 集成

```bash
dotnet run --project tests/ui-pinning-runtime/UiPinningRuntimeSmoke.csproj -c Release
dotnet run --project tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release
dotnet run --project tests/runtime-seat-highlight/RuntimeSeatHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-order-highlight/RuntimeOrderHighlightSmoke.csproj -c Release
dotnet run --project tests/runtime-throw-delivery-order-highlight/RuntimeThrowDeliverOrderHighlightSmoke.csproj -c Release
corepack pnpm audit:ui-pinning
```

这些 smoke 只验证 Mod-owned 状态、身份和生命周期契约，仍需在锁定游戏/BepInEx 环境实测制作页、酒水页、
HUD 订单、桌位和投掷送达面板。

修改 target recipe variant 服务或专项测试后，避免同秒增量时间戳造成假绿，强制执行：

```bash
dotnet build tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release -t:Rebuild
dotnet run --project tests/runtime-target-recipe-variant/RuntimeTargetRecipeVariantSmoke.csproj -c Release --no-build
```

### 特殊经营与输入

```bash
dotnet run --project tests/runtime-mizuchi-special-business/RuntimeMizuchiSpecialBusinessSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-special-business/RuntimeYuumaSpecialBusinessSmoke.csproj -c Release
dotnet run --project tests/runtime-yuuma-finalization/RuntimeYuumaFinalizationSmoke.csproj -c Release
dotnet run --project tests/yuuma-cooker-topology/YuumaCookerTopologySmoke.csproj -c Release
dotnet run --project tests/controller-toggle-state/ControllerToggleStateSmoke.csproj -c Release
corepack pnpm audit:special-business
corepack pnpm audit:gamepad
```

特殊经营的游戏机制来源和实测记录见
[特殊经营验证](special-business-validation.md)。

## 真实 Harmony/MonoMod 测试

以下三个测试会安装真实 Harmony/MonoMod 动态补丁：

- `tests/automation-cooking-job/`；
- `tests/ui-pinning-runtime/`；
- `tests/runtime-target-recipe-variant/`。

Linux .NET 10 CoreCLR 上的真实探针可能原生崩溃。统一使用仓库锁定的 .NET 6 SDK 容器入口：

```bash
corepack pnpm test:dotnet6-harmony
```

该入口使用 `toolchain.lock.json` 中固定 digest 的 .NET SDK `6.0.428` 镜像。不要删除探针、改成源码字符串
断言或用本机较新 SDK 的表面通过代替它。容器不可用时明确记录未执行原因，在支持环境补跑。

## 构建、引用与发布脚本

| 变更范围 | 命令 |
| --- | --- |
| 产物配额、prune/clean | `corepack pnpm audit:build-artifacts` |
| References lock/恢复器 | `corepack pnpm audit:build-references` |
| Mod ZIP 事务打包 | `corepack pnpm audit:release-package` |
| Android APK 原子发布、签名材料 | `corepack pnpm audit:android-apk-transaction` |
| 版本目录、发布 PowerShell 与 workflow policy | `corepack pnpm audit:release-policy` |
| GitHub Actions workflow | `corepack pnpm audit:github-actions` |
| 工具链锁、安装器或版本检查 | `corepack pnpm audit:toolchain` |

修改公共构建入口时通常需要叠加相邻项。例如 Android 签名脚本同时影响工具链、APK 事务和正式 workflow，
至少运行 `audit:toolchain`、`audit:android-apk-transaction`、`audit:release-policy` 和
`audit:github-actions`。

### Android 构建与签名

Android 代码或工程配置：

```bash
node scripts/check-build-toolchain.mjs android
corepack pnpm tauri:android:apk
```

签名脚本、Gradle signing config 或发布资产命名：

```bash
corepack pnpm audit:android-apk-transaction
corepack pnpm tauri:android:apk:signed
```

签名命令需要本机私有材料，只能在已配置环境运行。双 ABI、证书和实机检查见
[Android 开发](android-development.md#设备验证)。

## 游戏运行时实测

涉及反射、Harmony Hook、订单、任务、自动化或游戏 UI 时，在 smoke 后使用锁定的游戏 build、BepInEx #783
和正式 References 交叉验证。反编译资料的取得、可引用证据和禁止猜测路径见
[IL2CPP 源码与 IDA 分析工作流](il2cpp-analysis-workflow.md)。

实测记录至少包含：

- 游戏、BepInEx、Mod 版本和同时启用的其他 Mod；
- 新存档/旧存档、场景、经营类型和复现步骤；
- 预期与实际行为、订单或 trace identity；
- `ModLog` 日志和诊断包的时间范围；
- 是否发生切换主设备、断线、开关变更、面板保持打开或跨经营；
- 验证通过的正常路径，以及至少一个关闭/失败/重连路径。

不能取得关键运行时资料时保持 fail-closed，并把缺少的证据记入开发记录；不要以名称匹配、场景扫描、
宽泛反射或旧路径兼容代替验证。

## 提交前检查

1. `git diff --check` 无空白错误；
2. `git status --short` 只含本次范围内文件；
3. 基线构建与所有受影响专项测试通过；
4. 新增或重命名文件的相对链接有效；
5. 用户行为、安装、配置或发布流程变化已同步相应 README、帮助页或文档；
6. 无法在当前平台执行的项目被明确列出，并安排对应平台验证；
7. 测试输出、截图、临时诊断和私密材料未加入提交。
