# 本地开发与构建

唯一工具版本基线为根 `toolchain.lock.json`。客户端为 `apps/companion-flutter`，纯Dart包为 `packages/companion_protocol` 和 `packages/companion_client`，原生适配为 `packages/companion_platform`。业务在两个C# modules中，独立Windows更新程序位于 `native/updater`。

## 准备

按工具锁准备Node/Corepack/pnpm、.NET、Rust、Flutter/Dart与对应平台工具。Flutter路径必须显式传入，不通过PATH选择未知SDK。Windows使用原生VS/MSVC/CMake，Android使用锁定JDK/SDK/NDK/Gradle；不能用跨平台产物代替Windows验证。

```powershell
corepack pnpm install --frozen-lockfile
node scripts/flutter-workspace.mjs --sdk-root <Flutter目录> --task get
corepack pnpm references:verify
```

首次新增依赖时在对应应用或包目录运行锁定 Flutter 的 `pub get`，审查并提交该目录的 `pubspec.lock`；日常 get 强制 lockfile。Pigeon 生成源与三端结果一起提交。签名私钥只通过仓库外的签名配置或 CI 环境提供，不写进偏好、日志或源码。客户端连接凭据使用平台安全存储；Mod 的 API Token 则保存在主 CFG 中，迁移备份也保留其原字节，不能将客户端加密规则理解为全部配置均已加密。

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

公开版 v1.3.1 首次升级的默认流程是手动删除旧插件目录并解压完整新包、保留配置根，随后由新 Mod 自动迁移主 CFG 并在验证后清除旧原件。发布清单协议与普通 updater 均阻止跨代自动安装；开发安装仍使用前述 PowerShell 入口。边界及配置接管顺序见[手动首次升级](update-system.md#v131-手动首次升级)。

`build-windows` 按 [Flutter 官方 ZIP 分发要求](https://docs.flutter.dev/platform-integration/windows/building)，从当前根锁匹配的 VS `VC/Redist/MSVC/<锁定版本>/x64/Microsoft.VC14x.CRT` 复制未修改的 `msvcp140.dll`、`vcruntime140.dll`、`vcruntime140_1.dll` 到 EXE 同级，并带上根 NOTICE 的 `THIRD-PARTY-NOTICES.txt` 副本。构建验证 x64 PE、有效微软签名、同系列且不早于基线的统一文件版本，以及源/目标 SHA-256；Redist 目录版本不等于 DLL 的服务补丁版本，实际身份记录在 `temp/flutter-build-evidence/windows-runtime.json`。不从 System32 或 PATH 搜索替代 DLL，不分发系统 UCRT 或 debug runtime。

独立 updater 由根 `.cargo/config.toml` 强制 x64 MSVC 静态 CRT；它被单独复制到临时 runner 时无需旁置上述 DLL。打包入口再次只读验证完整运行树和真实 PE 导入，缺库、字节冲突、未知 VC 依赖，或环境 `RUSTFLAGS` 导致 updater 重新动态链接均拒绝。更换锁定工具链、SDK 或 NOTICE 后若已有 Release 同名文件冲突，先停止使用该目录的客户端/测试进程，在应用目录使用锁定 Flutter 执行 `flutter clean`，再完整重建；不要手工从 System32 换 DLL 或混用不同 Redist。跨平台边界测试为 `corepack pnpm audit:windows-runtime`；真实 Windows 校验为 `node scripts/windows-runtime.mjs verify <完整运行树> <updater.exe>`。

## 独立 Windows 客户端开发

普通 Flutter `build-windows` 输出通用 EXE，仍使用每用户 AppData。便携分发由 `build-release.ps1` 最后的规范打包步骤生成：`mods/bepinex/dist/mystia-steward-companion-windows-x64/` 及对应独立 ZIP。该目录使用 `mystia-steward-companion-portable.exe` 和 `portable-client-v1.manifest`，主程序字节与 Mod 包内客户端相同；完整格式与跨包校验见[发布流程](local-release.md#独立-windows-分发契约)。不要在普通构建树中手工改名或补标记来代替打包。

需要只复查打包时，先准备当前源码对应的 Mod DLL、完整 Flutter Release 树及 updater，再运行 `pwsh -File mods/bepinex/tools/package-release.ps1`；可用其 `-CompanionDirectory`、`-UpdaterPath` 显式选择输入。此入口不重新编译，也不会清理污染输入；运行过的便携目录含 `client-data`，不得用作构建输入。客户端启动、迁移或窗口测试使用完整包的独占测试副本，不能直接在待分发目录生成真实偏好或凭据。

便携目录、原生存储及首次初始化 UI 是不同验证层次，命令与隔离来源规则集中在[验证指南](validation-guide.md#独立-windows-客户端验证)。`build-release.ps1` 的 Flutter/HTTP 和整包检查不隐含纯 Win32 存储专项、实际首次启动或跨电脑验收。

## 数据迁移测试

`tests/mod-data-layout` 直接链接生产迁移与配置绑定代码，使用真实 BepInEx `ConfigFile` 和 TOML 转换器；游戏枚举及无关 DTO 使用隔离替身。除已核验的正式 `BepInEx.Core.dll` 编译引用外，还需与 HTTP 宿主相同的 `SemanticVersioning.dll`。先运行下方恢复入口；它按锁定原始分发包和共用 `BepInExRuntime.props` 校验成员路径、大小与 SHA-256，测试项目编译前再次核对摘要。默认运行库目录为 `temp/flutter-mod-host-runtime`；需要离线恢复或使用独立目录时，沿用上文 HTTP 宿主的恢复参数及 `BepInExRuntimeDirectory` 约束，不能换用任意同名 DLL。

```powershell
pwsh -File scripts/restore-bepinex-test-runtime.ps1
dotnet run --project tests/mod-data-layout/ModDataLayoutSmoke.csproj -c Release
```

手动升级测试入口接收精确匹配公开 v1.3.1 的旧 ZIP 与待验证的实际完整新包；它只在本仓库新建的人工游戏目录删除旧插件，再通过真实配置绑定宿主验证首次迁移、旧 CFG 自动清理及重复启动：

```powershell
pwsh -File tests/csharp-business-package/manual-v131-upgrade.ps1 -LegacyReleaseZip '<公开 v1.3.1 ZIP 的绝对路径>' -NewPackageDirectory '<本次构建完整新版目录的绝对路径>'
```

脚本不运行 Unity 或旧客户端，保护区均为合成数据；结果不能替代真实游戏加载和双机局域网验收。测试输出保存在该次 `temp/manual-v131-<标识>/`，不自动清理其他测试目录。输入路径、源码提交和实际结果应记录在当次验证报告，文档中的命令不表示已经执行。

保留的旧维护事务使用以下 Windows 隔离脚本；该测试不代表普通用户仍需执行离线安装命令：

```powershell
pwsh -File tests/csharp-business-package/offline-v131-migration.ps1 -LegacyReleaseZip '<公开 v1.3.1 ZIP 的绝对路径>'
```

脚本验证公开 ZIP 的固定摘要与三个载荷条目，在仓库 `temp` 下建立独立人工游戏目录，运行真实旧包加合成新包的故障矩阵。可追加 `-NewPackageDirectory '<已构建完整新版目录的绝对路径>'`，额外调用该完整包中的正式 updater CLI，验证只读预览、实际安装、载荷一致性、保护区与无参数运行边界。两种方式均不执行旧游戏或客户端，不读取真实配置或存档；实际完整包的隔离安装通过也不代表真实游戏加载或客户端交互验收。

旧版残留清理使用 updater 的 `--cleanup-v1-3-1`，参数及永久删除边界见[更新系统](update-system.md#v131-残留清理)。验证应使用合成游戏树、迁移记录、旧公开载荷和独立临时目录；不得对真实用户的 WebView 目录执行清理。Known Folders 接口不提供环境变量或 CLI 根替换，路径映射单测只使用固定合成路径，不调用系统目录解析、不枚举真实资料：

```powershell
cargo test --locked --manifest-path native/updater/Cargo.toml legacy_cleanup_client::tests
```

纯路径映射测试只证明旧标识、Roaming 单文件范围与当前 Flutter 路径分离，不证明删除事务、系统进程识别或真实用户资料迁移完成。清理引擎另需验证计划摘要失配、备份/原件冲突、链接、占用和部分删除结果；不能以旧版离线安装测试代替清理验证。

完整 CLI 的 Windows 隔离入口同时接收公开旧 ZIP 和本次完整新包：

```powershell
pwsh -File tests/csharp-business-package/legacy-cleanup.ps1 -LegacyReleaseZip '<公开 v1.3.1 ZIP 的绝对路径>' -NewPackageDirectory '<已构建完整新版目录的绝对路径>'
```

脚本在仓库 `temp` 下构造独立游戏、配置、存档和升级备份，只清理其中的测试副本，不启用真实用户客户端数据选项；输入 ZIP 和完整新包仍只读保留。此入口不证明真实 WebView 删除或用户历史偏好已完成迁移。

## 本机缓存

构建缓存放入被忽略的temp或Flutter build目录；不提交.dart_tool、Gradle/NuGet缓存或SDK。JDK在Windows长临时目录下出现Unix-domain socket/loopback错误时，可为当前构建进程选择短临时路径，同时设置TEMP/TMP及 `jdk.net.unixdomain.tmpdir` / `java.io.tmpdir`。不得修改系统全局设置掩盖工具锁不一致。

游戏运行时的 Mod 数据与开发缓存分开：前者统一位于游戏的 `BepInEx/config/MystiaStewardCompanion/`，其中 `cache/game-media/` 是可重建图片缓存，`updates/` 属于整包更新事务，均不得作为源码或发布载荷收集。Mod 内置 Windows 客户端使用每用户 AppData，专用独立 Windows 包使用 EXE 旁 `client-data/`，Android 使用应用私有目录。便携模式由专用 EXE 与完整清单确定，不能用工作目录、局域网连接或启动参数推断；普通 Flutter 构建输出不是便携发布包。迁移验证不得读取真实 Token 或复用正式设备身份。布局迁移、旧版离线整包升级与客户端偏好导入各有独立边界，验证应使用合成配置、Token、缓存与存档夹具；规则见[更新系统](update-system.md#数据与程序目录)、[独立客户端](standalone-client.md)和[Mod 数据位置](../mods/bepinex/README.md#配置与数据位置)。

验证选择见[验证指南](validation-guide.md)，原生协议见[伴随UI](companion-ui.md)，正式发布事务见[发布流程](local-release.md)。
