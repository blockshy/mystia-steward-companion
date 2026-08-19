# Android 本地开发

更新日期：2026-08-19

本文档只说明 Android 伴随窗口的本地环境、构建、签名、产物和故障排查。通用 Node/Rust 环境与
Mock API 见[本地开发与构建](local-development.md)，测试选择见[验证指南](validation-guide.md)，
GitHub Actions 正式发布与 Secrets 配置见[发布流程](local-release.md)。

## 产品边界

Android 版用于可信局域网内的 B 设备，通过 A 设备上运行的游戏和 Mod 提供的 API 工作。它不是 Windows
可执行文件的转换产物，也不包含托盘、置顶、鼠标穿透、桌面单实例、游戏窗口聚焦和游戏关闭时自动退出等能力。

桌面与 Android 正式构建均通过 Tauri Rust `request_local_api` 命令使用原生 TCP 访问 Mod。只有浏览器/Vite
开发模式直接访问 mock API；不得为 Android 增加 WebView direct-fetch 回退或放宽 CSP 来绕过代理错误。

## 锁定工具链

根目录 [`toolchain.lock.json`](../toolchain.lock.json) 是唯一版本来源：

| 组件 | 锁定值 |
| --- | --- |
| Eclipse Temurin JDK | `21.0.4` |
| Android compile SDK | `36` |
| Android target SDK | `36` |
| Android Build Tools | `35.0.0` |
| Gradle wrapper | `8.14.3` |
| Android NDK SDK 包坐标 | `30.0.14904198` |
| NDK 包内 `Pkg.Revision` | `30.0.14904198-beta1` |
| Rust / Cargo | `1.97.1` |
| Rust targets | `aarch64-linux-android`、`armv7-linux-androideabi` |

NDK 的 SDK 目录名与包内 revision 是两个需要同时满足的值。不要把 beta1 的 package revision 改写为目录名，
也不要用其他 r30 包作为回退。Gradle distribution 的 SHA-256 同样由锁文件固定。

## 环境准备

通过 Android Studio SDK Manager 或 `sdkmanager` 精确安装 platform `36`、Build Tools `35.0.0` 和 NDK
package `30.0.14904198`。安装 Eclipse Temurin JDK `21.0.4`，再配置：

- `JAVA_HOME`：JDK `21.0.4` 根目录；
- `ANDROID_HOME`：包含 `platforms/`、`build-tools/` 和 `ndk/` 的 SDK 根目录；
- `NDK_HOME`：`$ANDROID_HOME/ndk/30.0.14904198`。

若系统还设置 `ANDROID_SDK_ROOT`，它必须与 `ANDROID_HOME` 解析到同一目录；若设置
`ANDROID_NDK`、`ANDROID_NDK_HOME` 或 `ANDROID_NDK_ROOT`，它们必须与 `NDK_HOME` 解析到同一目录。
不要保留彼此冲突的旧别名。

Windows PowerShell 示例：

```powershell
$env:JAVA_HOME = "D:\environment\jdk-21.0.4"
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:NDK_HOME = Join-Path $env:ANDROID_HOME "ndk\30.0.14904198"

rustup target add aarch64-linux-android armv7-linux-androideabi --toolchain 1.97.1
node scripts/check-build-toolchain.mjs android
```

标准项目命令还会要求由 Corepack 调用锁定 pnpm：

```bash
corepack pnpm toolchain:check
corepack pnpm install --frozen-lockfile
```

仓库已经提交 Tauri mobile 生成工程 `apps/companion/src-tauri/gen/android/`。日常构建不得删除、绕过或重复
生成该目录；只有明确升级 Tauri mobile 工程结构时才运行 `tauri android init`，并审查完整生成差异。

## 开发与构建入口

所有命令从仓库根目录运行：

```bash
# 连接真机或模拟器进行开发
corepack pnpm tauri:android:dev

# 普通 Android 构建
corepack pnpm tauri:android:build

# release、按 ABI 拆分，未签名
corepack pnpm tauri:android:apk

# release、按 ABI 拆分、签名、验签并复制 canonical 资产
corepack pnpm tauri:android:apk:signed
```

前三项入口先运行 Android profile 的工具链检查并治理过期构建缓存。`apk` 明确传入
`--split-per-abi --target aarch64 armv7`，不得用单一 universal APK 代替正式双 ABI 产物。

Windows、Mod 与 Android 一次构建可使用：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -BuildAndroidApk
```

该命令仅构建本地资产，不创建 tag 或 GitHub Release。正式稳定版工作流见
[发布流程](local-release.md)。

## 未签名产物

`corepack pnpm tauri:android:apk` 的预期输出为：

```text
apps/companion/src-tauri/gen/android/app/build/outputs/apk/arm64/release/app-arm64-release-unsigned.apk
apps/companion/src-tauri/gen/android/app/build/outputs/apk/arm/release/app-arm-release-unsigned.apk
```

未签名 APK 只用于定位编译和打包问题，不能作为发布资产。构建成功也不代表 LAN、前后台恢复或 Android
系统行为已经验证。

## 本地签名

### 创建或准备 keystore

以下命令仅作新建私有发布 keystore 的示例；已有正式 key 时继续使用同一 key，不要重新生成：

```powershell
keytool -genkeypair -v `
  -keystore "$env:USERPROFILE\.android\mystia-steward-companion-release.jks" `
  -storetype PKCS12 `
  -keyalg RSA `
  -keysize 2048 `
  -validity 10000 `
  -alias mystia-steward-companion
```

在被 Git 忽略的
`apps/companion/src-tauri/gen/android/keystore.properties` 中写入四个规范字段：

```properties
keyAlias=mystia-steward-companion
storePassword=<keystore 密码>
keyPassword=<key 密码>
storeFile=C:\Users\Administrator\.android\mystia-steward-companion-release.jks
```

即使 store 与 key 使用同一密码，也要分别填写 `storePassword` 和 `keyPassword`。旧 `password` 字段不是
有效路径。`storeFile` 可使用绝对路径，或以 Android 工程根目录为基准的相对路径。

keystore、密码、`keystore.properties`、Gradle 缓存、JNI `.so` 与构建输出均不得提交、写入诊断包或日志。

### 签名身份与元数据

签名脚本要求最终 APK 的证书 SHA-256 等于：

```text
15:40:B6:09:D5:CD:54:E0:6A:84:29:BB:0A:AA:2C:C4:B5:11:E0:55:56:5F:DA:C9:3A:CF:20:6C:17:91:D1:FB
```

这是 APK 内签名证书的指纹，不是 keystore 文件字节的 SHA-256。脚本使用锁定 Build Tools `35.0.0` 的
`apksigner` 验签，并通过 `aapt2 dump badging` 逐项验证：

- application ID 与项目配置一致；
- `versionName` 与当前项目版本完整一致；
- `versionCode` 是唯一合法十进制值；
- 每个 APK 只包含其目标 ABI；
- 输出目录不存在额外的 release signed APK。

Android `versionCode` 使用 Tauri v2 规则 `major * 1_000_000 + minor * 1_000 + patch`。
`X.Y.Z-preview.N` 与同一核心稳定版共享 code；minor 或 patch 大于 `999`、code 为 `0` 或超过
`2100000000` 时构建停止。

### 签名产物

只有两个 ABI 全部构建、签名、验签和元数据检查通过后，脚本才原子复制：

```text
mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk
mods/bepinex/dist/mystia-steward-companion-android-armeabi-v7a.apk
```

签名构建会在该 Android 进程内设置 `CARGO_PROFILE_RELEASE_STRIP=symbols`、
`CARGO_PROFILE_RELEASE_LTO=thin` 和 `CARGO_PROFILE_RELEASE_CODEGEN_UNITS=1`。这些优化不会写入桌面 Cargo
release profile。APK 是独立下载资产，不写入 Mod 的 `update-manifest.json`，也不参与 Mod 自动更新。

## 设备验证

本地构建完成后，至少在目标 Android 版本和一台真实设备上检查：

1. A 设备启动游戏和 Mod，LAN listener 使用预期端口与 token；
2. B 设备从连接页成功建立连接，错误 token 和不可达地址能明确失败；
3. 手机竖屏、手机横屏及可用的大屏/平板布局无裁切；
4. 锁屏、切后台、恢复前台和 Wi-Fi 短暂断开后能重新连接；
5. 主设备切换、配置同步和 automation lease 不会让非主设备取得写入权；
6. 安装升级后数据与配置仍保留，包版本和 ABI 与预期一致。

没有真机时，在验证记录中明确写“仅构建、签名和包元数据通过”，不能用模拟器或桌面浏览器替代真机结论。

按脚本和 UI 变更选择的自动化命令见[验证指南](validation-guide.md#android-构建与签名)。

## 故障排查

### 工具链检查报告版本或路径冲突

- 先读取 `toolchain.lock.json`，不要根据 Android Studio 的推荐版本升级；
- 确认命令实际使用的 `java`、`rustc`、`cargo`、SDK 和 NDK 与环境变量一致；
- 检查 `NDK_HOME/source.properties` 的 `Pkg.Revision` 是 `30.0.14904198-beta1`；
- 删除或修正指向其他目录的 Android SDK/NDK 别名，而不是增加检测回退。

### 缺少 Rust Android target

```bash
rustup target add aarch64-linux-android armv7-linux-androideabi --toolchain 1.97.1
```

添加后重新运行 Android profile 检查。不要让默认 Rust toolchain 的 target 掩盖锁定 toolchain 缺项。

### Windows 报告不同盘符

若 Kotlin 增量缓存报告 `this and base files have different roots: C:\... and D:\...`，仓库配置已经关闭
Kotlin incremental compilation；先停止旧 daemon 并清除旧 Gradle 中间目录：

```powershell
Set-Location apps\companion\src-tauri\gen\android
.\gradlew --stop
Remove-Item -Recurse -Force .gradle, build, app\build, buildSrc\build -ErrorAction SilentlyContinue
Set-Location ..\..\..\..\..
```

随后从仓库根目录重建。不要删除整个生成工程。

### 找不到 keystore、`apksigner` 或 `aapt2`

- keystore 错误：确认 `keystore.properties` 恰有四个规范字段，`storeFile` 指向实际文件；
- `apksigner` / `aapt2` 错误：确认 Build Tools `35.0.0` 完整安装，不借用其他版本；
- 密码错误：直接修正本机私有文件，不把值打印到终端历史或构建日志。

### 证书或 APK 身份不匹配

证书指纹不匹配表示用了错误的 key；application ID、版本或 ABI 不匹配表示配置或构建输出发生漂移。
这些错误必须修正来源后重新构建，不能跳过验签、改名复制 APK 或降低检查强度。

### 只有桌面检查通过，Android 编译失败

`cargo check` 的桌面目标不能证明 Android target 可编译。检查是否把桌面专用 Tauri 类型、Win32 API 或 feature
无条件带入移动端；在明确的平台模块边界修正 `cfg` 和依赖，不保留运行时探测或空实现兼容层。

### 磁盘空间持续增长

返回仓库根目录使用：

```bash
corepack pnpm artifacts:report
corepack pnpm artifacts:prune
```

不要在 Gradle 或 Cargo 正在运行时清理。产物治理的完整边界见
[本地开发与构建](local-development.md#构建产物管理)。
