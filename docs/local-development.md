# 本地开发与构建

更新日期：2026-08-19

本文档只说明日常本地开发环境、构建入口和开发服务。测试选择见
[验证指南](validation-guide.md)，Android 专用环境见
[Android 开发](android-development.md)，版本与发布操作见
[发布流程](local-release.md)。

## 工具链基线

根目录 [`toolchain.lock.json`](../toolchain.lock.json) 是本地与 CI 共用的唯一版本来源。文档中的版本仅帮助安装；
升级时先修改锁文件及其校验逻辑，不使用系统中的 `stable`、`latest` 或较新预装版本作为回退。

| 工具 | 锁定版本 | 用途 |
| --- | --- | --- |
| Node.js | `24.19.0` | 前端、构建与审计脚本 |
| Corepack | `0.35.0` | 提供锁定的包管理器入口 |
| pnpm | `10.10.0` | 依赖、前端、Tauri 与项目脚本 |
| .NET SDK | `10.0.110` | 构建目标为 `net6.0` 的 Mod 及测试工程 |
| Rust / Cargo | `1.97.1` | Tauri 桌面与移动端壳 |
| PowerShell | `7.6.4` | Windows 完整构建与发布编排脚本 |
| GitHub CLI | `2.97.0` | 恢复私有引用和准备发布元数据 |

Windows 桌面构建还需要 Microsoft C++ Build Tools 2022（或 Visual Studio 的“使用 C++ 的桌面开发”组件）
和 Microsoft Edge WebView2 Runtime。Linux 上检查 Tauri 时需要 WebKitGTK、AppIndicator、SVG、OpenSSL
和 `xdotool` 的开发包；不同发行版使用对应包管理器安装。

先在仓库根目录检查环境：

```bash
corepack pnpm toolchain:check
```

该命令要求 Node、Corepack、pnpm、.NET 与 Rust 和锁文件完全一致。只改前端时，`pnpm` 脚本仍会先验证
前端所需的锁定工具；不要绕过脚本直接调用 Vite 或 Tauri CLI。

### 首次初始化

若系统尚无锁定的 Corepack，可将其安装到新的隔离目录。Windows PowerShell 示例：

```powershell
$toolchainParent = Join-Path $env:LOCALAPPDATA "mystia-steward-companion\toolchain"
New-Item -ItemType Directory -Force $toolchainParent | Out-Null
$corepackRoot = Join-Path $toolchainParent "corepack-0.35.0"
node scripts/install-locked-corepack.mjs --install-root $corepackRoot
$env:PATH = "$corepackRoot;$env:PATH"
corepack install
rustup toolchain install 1.97.1 --profile minimal
corepack pnpm install --frozen-lockfile
corepack pnpm toolchain:check
```

安装器只接受尚不存在的目录，并校验锁定 tarball 的完整性。升级 Corepack 时创建新的版本目录，不覆盖旧目录，
也不执行全局 `corepack enable`。

Mod 的目标框架是 `net6.0`，产品构建仍使用锁定的 .NET SDK `10.0.110`。只有三项真实 Harmony/MonoMod
动态补丁测试使用锁定的 .NET 6 容器；入口和限制见[验证指南](validation-guide.md#真实-harmonymonomod-测试)。

## BepInEx 构建引用

`mods/bepinex/References/` 中的真实 DLL 不提交到公开仓库。引用的来源、文件集合、大小和 SHA-256 由
[`references.lock.json`](../mods/bepinex/References/references.lock.json) 唯一锁定。

首次恢复、外部引用目录和测试专用 HarmonyX 依赖的操作见
[`mods/bepinex/References/README.md`](../mods/bepinex/References/README.md)。已有引用可直接验证：

```bash
corepack pnpm references:verify
```

不要从当前游戏目录、另一版 BepInEx 或旧 interop 临时拼接 DLL。恢复器会拒绝 bundle 内缺项、多项、
子目录和符号链接；恢复器与 preflight 都会拒绝正式文件的大小或哈希漂移，不会联网寻找替代版本。

## 常规开发入口

以下命令均从仓库根目录运行。

### 前端

```bash
corepack pnpm dev
corepack pnpm lint
corepack pnpm build
corepack pnpm preview
```

- `dev` 启动 Vite 开发服务；
- `build` 先执行 TypeScript project build，再生成 `apps/companion/dist/`；
- `preview` 只预览当前已有的生产构建，因此先运行 `build`；
- 功能完成前按变更范围选择专项 audit，不能用生产构建成功代替行为验证。

### Tauri 桌面壳

```bash
corepack pnpm tauri:dev
corepack pnpm tauri:build
cargo check --manifest-path apps/companion/src-tauri/Cargo.toml
```

`tauri:build` 使用 `--no-bundle`，适合日常验证 Rust 壳和嵌入式前端；完整安装包由后文的构建脚本生成。
修改窗口聚焦、单实例或 updater 时，还应运行对应 Rust 单测和 Windows 实机检查，详见
[验证指南](validation-guide.md#tauri-rust-与独立-updater)。

### BepInEx Mod

```bash
pwsh -ExecutionPolicy Bypass -File mods/bepinex/tools/preflight.ps1
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

Git Bash 可使用：

```bash
bash mods/bepinex/tools/preflight.sh
```

Mod 开发目录与运行时入口见
[`mods/bepinex/README.dev.md`](../mods/bepinex/README.dev.md)，运行时数据读取边界见
[运行时 Provider](runtime-provider.md)。

## Windows 完整本地构建

PowerShell 7 从仓库根目录运行：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1
```

脚本依次验证锁定工具链与 References、安装冻结依赖、构建前端和 Tauri、构建 Mod，再事务式生成本地安装资产。
该操作不会创建 Git tag 或 GitHub Release。

常用的定向参数：

```powershell
# 依赖已经按 lockfile 安装
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -SkipInstall

# 只改 C# Mod
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 `
  -SkipInstall -SkipFrontendBuild -SkipTauriBuild

# 使用已按 references.lock.json 恢复的外部目录
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

只要修改了 `apps/companion/src/` 或 Tauri 代码，就不能跳过对应构建，否则最终包可能继续携带旧产物。
`-SkipPackage` 只用于保留裸构建输出以便诊断；Android 扩展参数及签名要求见
[Android 本地开发](android-development.md)。

## 构建产物管理

Cargo 多目标缓存、Android Gradle 输出和 .NET `bin/obj` 会持续增长。使用统一脚本治理，不手工删除 Cargo
`deps` 中的单个哈希目录：

```bash
# 仅报告
corepack pnpm artifacts:report

# 超过默认 12 GiB 时清理到 8 GiB
corepack pnpm artifacts:prune

# 预览及执行完整清理
corepack pnpm artifacts:clean -- --dry-run
corepack pnpm artifacts:clean
```

清理以完整 profile、target triple、Gradle build 目录或 .NET 项目为单位，并拒绝白名单外路径和符号链接。
`mods/bepinex/dist`、`References`、`temp`、`node_modules`、Playwright 数据与签名材料不在自动清理范围。

不要在 Cargo、Gradle、Vite 或 dotnet 仍运行时执行 prune/clean。首次清理后需要完整重编译属于预期。
构建脚本可用 `-BuildCacheLimitGiB`、`-BuildCacheTargetGiB` 调整高低水位；
`-SkipBuildCacheCleanup` 仅用于定位问题。

## Mock API 与浏览器预览

不启动游戏时，可用稳定 mock 数据开发伴随窗口。

终端一：

```bash
corepack pnpm mock:api
```

默认 API 地址为 `http://127.0.0.1:32145`，token 为 `mock-token`。

终端二：

```bash
corepack pnpm dev -- --host 127.0.0.1 --port 5173
```

浏览器打开 `http://127.0.0.1:5173`，在开发者工具 Console 写入开发连接并刷新：

```js
localStorage.setItem('mystia-steward-companion-mod-api-endpoint', 'http://127.0.0.1:32145');
localStorage.setItem('mystia-steward-companion-mod-api-token', 'mock-token');
localStorage.setItem('mystia-steward-companion-show-debug-details', '1');
location.reload();
```

生产构建预览使用另一端口，避免与 Vite dev 混淆：

```bash
corepack pnpm build
corepack pnpm preview -- --host 127.0.0.1 --port 4173
```

浏览器/Vite 开发模式会直接访问 mock API；打包后的桌面和 Android 应用通过 Tauri
`request_local_api` 原生命令访问 Mod。不要通过放宽 CSP 或加入 WebView direct-fetch 回退来掩盖原生代理错误。

Playwright 环境变量、浏览器安装和按功能选择的巡检命令见
[验证指南](validation-guide.md#playwright-巡检)。

## 本地运行边界

- Windows 游戏实测使用 Windows x64 IL2CPP BepInEx #783；不要把 Linux BepInEx 安装到通过 Proton 运行的
  Windows PE 游戏。
- Linux 可完成源码分析、前端、Rust、普通 .NET 构建和大部分 smoke；Win32 窗口、WebView2、APK 签名及
  真实游戏行为仍需对应平台验证。
- 游戏运行时结构的判断以锁定反编译资料为依据，流程见
  [IL2CPP 源码与 IDA 分析工作流](il2cpp-analysis-workflow.md)。
- 日常构建不改变版本、不创建 tag，也不发布资产；预览版和正式版操作统一见
[发布流程](local-release.md)。
