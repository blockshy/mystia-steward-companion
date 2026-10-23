# 本地开发与构建

唯一工具版本基线为根 `toolchain.lock.json`。客户端为 `apps/companion-flutter`，纯Dart包为 `packages/companion_protocol` 和 `packages/companion_client`，原生适配为 `packages/companion_platform`。业务在两个C# modules中，独立Windows更新程序位于 `native/updater`。

## 准备

按工具锁准备Node/Corepack/pnpm、.NET、Rust、Flutter/Dart与对应平台工具。Flutter路径必须显式传入，不通过PATH选择未知SDK。Windows使用原生VS/MSVC/CMake，Android使用锁定JDK/SDK/NDK/Gradle；不能用跨平台产物代替Windows验证。

```powershell
corepack pnpm install --frozen-lockfile
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task get
corepack pnpm references:verify
```

首次新增依赖时在对应应用或包目录运行锁定 Flutter 的 `pub get`，审查并提交该目录的 `pubspec.lock`；日常 get 强制 lockfile。Pigeon 生成源与三端结果一起提交。密钥只通过本机安全存储或 CI 签名环境提供，不写进普通偏好、日志或源码。

`.NET SDK` 与测试运行时是两个前置条件：`global.json` 选择锁定 SDK，`net6.0` 隔离宿主还要求已安装 .NET 6 运行时。完整 Flutter 测试和整包构建会启动真实 HTTP 宿主；除 7 个正式编译引用外，还需测试专用 `SemanticVersioning.dll`。完整构建入口会自动从锁定 BepInEx #783 原始分发包恢复，单独构建宿主先按[协议测试说明](../tests/flutter-contracts/README.md#执行)执行恢复脚本。只执行 `references:verify` 不能证明这个额外运行库已经就绪。

## 检查与构建

```powershell
dotnet build tests/flutter-contracts/host/FlutterContractsHost.csproj -c Release -p:NuGetAudit=false
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task format-check
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task generated-check
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task verify
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task contracts-test
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task build-windows
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task build-android
cargo test --locked --manifest-path native/updater/Cargo.toml
cargo build --locked --manifest-path native/updater/Cargo.toml --release
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

上述命令在仓库根目录运行。`verify` 仅执行 Flutter 应用的 analyze/test，不包含独立 Dart 契约、格式或 Pigeon 生成一致性；因此这些任务分别列出。协议、客户端和平台包的独立测试入口，以及无私有引用时的公共验证子集，见[验证指南](validation-guide.md)。若使用 `corepack pnpm flutter:*` 简写，先显式设置 `MYSTIA_FLUTTER_SDK_ROOT` 为同一个已锁定 SDK 目录。

Android 交付入口是 `scripts/build-android-signed-apk.mjs`；普通 Android Release 构建同样要求既有正式签名材料，并校验原证书、包身份和对齐，缺失时明确失败。APK 安装需要签名，不能把旧的未签名编译产物交给用户；构建和签名校验仍不表示已验证 Android 真机或覆盖升级。配置与交付路径见 [Android 开发](android-development.md)。

完整Windows包入口：

```powershell
pwsh -File mods/bepinex/tools/build-release.ps1 -FlutterSdkRoot <Flutter目录>
```

该入口自动核验或恢复测试运行库，再构建 HTTP 宿主并运行完整应用/契约测试；`-SkipFlutterBuild` 只跳过 Windows 客户端编译，不跳过这些前置验证。默认宿主运行库目录为 `temp/flutter-mod-host-runtime`，离线可向恢复脚本传入锁定原始 ZIP；单独构建宿主也可用 `-p:BepInExRuntimeDirectory=<目录>` 指定已核验的其他目录。发布 workflow 的隔离目录清理及来源约束见[发布流程](local-release.md#隔离宿主测试依赖)。

构建只生成本地产物，不部署游戏、不创建标签、不发布 GitHub Release。完整 Windows 运行树 ZIP、Mod ZIP 与可选双 ABI APK 输出到 `mods/bepinex/dist`。安装入口为 `mods/bepinex/tools/install-local-package.ps1`，接收包含完整运行树与 `package-manifest.json` 的解包目录并核对全部载荷，默认只读校验，显式 `-Execute` 才执行安装；不能直接把 ZIP 路径传作包目录，也不能单独复制主 EXE 或 DLL。

`build-windows` 按 [Flutter 官方 ZIP 分发要求](https://docs.flutter.dev/platform-integration/windows/building)，从当前根锁匹配的 VS `VC/Redist/MSVC/<锁定版本>/x64/Microsoft.VC14x.CRT` 复制未修改的 `msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll` 到 EXE 同级，并带上根 NOTICE 的 `THIRD-PARTY-NOTICES.txt` 副本。构建验证 x64 PE、有效微软签名、同系列且不早于基线的统一文件版本，以及源/目标 SHA-256；Redist 目录版本不等于 DLL 的服务补丁版本，实际身份记录在 `temp/flutter-build-evidence/windows-runtime.json`。不从 System32 或 PATH 搜索替代 DLL，不分发系统 UCRT 或 debug runtime。

独立 updater 由根 `.cargo/config.toml` 强制 x64 MSVC 静态 CRT；它被单独复制到临时 runner 时无需旁置上述 DLL。打包入口再次只读验证完整运行树和真实 PE 导入，缺库、字节冲突、未知 VC 依赖，或环境 `RUSTFLAGS` 导致 updater 重新动态链接均拒绝。更换锁定工具链、SDK 或 NOTICE 后若已有 Release 同名文件冲突，先停止使用该目录的客户端/测试进程，在应用目录使用锁定 Flutter 执行 `flutter clean`，再完整重建；不要手工从 System32 换 DLL 或混用不同 Redist。跨平台边界测试为 `corepack pnpm audit:windows-runtime`；真实 Windows 校验为 `node scripts/windows-runtime.mjs verify <完整运行树> <updater.exe>`。

## 本机缓存

构建缓存放入被忽略的temp或Flutter build目录；不提交.dart_tool、Gradle/NuGet缓存或SDK。JDK在Windows长临时目录下出现Unix-domain socket/loopback错误时，可为当前构建进程选择短临时路径，同时设置TEMP/TMP及 `jdk.net.unixdomain.tmpdir` / `java.io.tmpdir`。不得修改系统全局设置掩盖工具锁不一致。

验证选择见[验证指南](validation-guide.md)，原生协议见[伴随UI](companion-ui.md)，正式发布事务见[发布流程](local-release.md)。
