# Android P0 Release 探针

这是隔离的 Flutter Android 可行性工程，不替换正式 Android APP。测试 ID 固定为
`com.tyukki.mystia.steward.companion.p0probe`，Release 使用测试证书，禁止使用产品签名。
正式应用 ID、签名、数据与目标 SDK 不受此工程影响。

## 构建和验证

从仓库根目录使用根锁中的 Node、Flutter、JDK、SDK36、Build Tools35 和 NDK30。
根锁 `flutterAndroid` 固定 AGP8.11.1 / Kotlin2.2.20，新工程直接读取；这是锁定 Flutter3.47.6 接受的最低版本；
根 Gradle8.14.3 保持不变。Flutter 的未来弃用警告保留，不绕过兼容校验。

```bash
node scripts/run-flutter-android-probe.mjs --sdk-root temp/toolchains/flutter-3.47.6
node scripts/run-flutter-android-probe.mjs --sdk-root temp/toolchains/flutter-3.47.6 --build-output temp/android-probe-output
```

构建入口要求显式 `JAVA_HOME`、一致的 `ANDROID_HOME`/`ANDROID_SDK_ROOT`。输出目录必须不存在。
检查 Pigeon 双端逐字生成、Dart format/analyze/test；随后构建两个 ARM Release APK 与
单独 x64 模拟器 APK，逐个审计包身份、min24/target36、非 debuggable、签名、独立 ABI、
ZIP 16KB 对齐、每个 ELF 的 LOAD/RELRO。x64 只是测试包，不成为正式资产。
构建时另用锁定 aapt2 直接读取 APK 权限，要求 INTERNET 且不声明 ACCESS_LOCAL_NETWORK；
这与设备 PackageManager 返回的 OS 有效权限分别取证。
ARM32 的 ELF 要求4KB；ARM64/x64 要求16KB。静态对齐不代表16KB运行通过。
RELRO 同时记录末端整除结果和真实页保护边界：按
[AOSP bionic](https://android.googlesource.com/platform/bionic/+/refs/heads/main/linker/linker_phdr.cpp)
向页边界取整后不得覆盖任何 RELRO 外的可写 LOAD 字节。只保护段间空隙不判成运行失败；
这项静态判断不能代替对应设备运行。

## 运行边界

`scripts/serve-flutter-android-probe.mjs <新证据目录> <192.168.x.x> <端口>` 在指定 LAN
地址创建一次性只读 fixture。它仅提供 `/probe` 与 `/redirect`，记录实际来源、独立传输
路径及 nonce 匹配，不连接游戏、读取真实 Token 或处理业务写入。

`Invoke-Android-Probe.ps1` 用明确 adb、serial、APK SHA256、runId、endpoint、nonce 和新证据目录
安装/运行探针。`-Install` 仅允许设备上不存在此测试包时安装；后续运行不覆盖 APK。
Windows 通过锁定 PowerShell7调用。手机需接入与 fixture 可达的局域网，adb 仅用来安装与采证，
**不使用 adb reverse 代替 LAN 证据**。

应用在第一帧实际栅格化后分别调用现有 Dart P0 HttpClient 传输与窄 Kotlin HttpURLConnection
服务。两者禁用代理及重定向，发包前拒绝域名/公网地址，验证 nonce 绑定的响应；
动态 LAN IP 通过 Release Network Security Configuration 显式允许 HTTP。
报告包含运行 ABI、系统/target、实际 page size、权限状态和每项结果；外部驱动核对报告身份与哈希。
启动命令不无限等待 Android 窗口绘制；驱动在90秒内等应用实际首帧后的报告，超时保留失败证据。
HTTP fixture 是受控模拟服务，不能表述为真实 Mod 集成已通过。

## 权限与模拟器

默认 `Baseline` 不修改 compat flag。API36 的 `RESTRICT_LOCAL_NETWORK` 实验只允许
新建隔离模拟器：`Restricted` 启用 flag、撤销 NEARBY_WIFI_DEVICES 后重启模拟器，
`Granted` 授予权限并验证恢复，`Restored` 重置 flag 并撤销探针权限。
失败后仍须恢复 flag 或停止本次隔离 AVD，保留原始失败证据。
测试权限只在此探针 manifest 中声明；正式 target36 不增加 ACCESS_LOCAL_NETWORK。
当前 target36 在 Android17 上的运行证据与未来 target37 的 SDK/授权矩阵分别记录，
不根据 Android16 或当前 target36 的结果推断后者。
Android17 的 split permission 机制会向 target36 + INTERNET 隐式授予
ACCESS_LOCAL_NETWORK；`requestedPermissions` 包含 OS 补入项，不能称为 APK 的显式声明。
运行报告分别记录 `accessLocalNetworkEffective` / `accessLocalNetworkGranted`，不调用权限请求。
API36 的受限/授予模式只验证该版本的实验路径，不用它模拟未来 target37 授权流程。

`Start-Isolated-Emulator.ps1` 创建全新私有 AVD，请求1536MB/2核，无窗口且不触碰已有 AVD；
部分系统镜像会自动提高到自身最低内存要求，应以 emulator 日志和实际内存为准。
SSH 会话保持存活以保留其子进程；在该 run 目录创建 `stop-requested` 后使用精确 serial 正常关闭。
进程非零退出会使启动脚本失败；启动成功仍须独立确认 `sys.boot_completed` 和探针运行报告。
GPU 默认 `swiftshader_indirect`，可显式选 `host`；必须记录实际 backend，不能把软件GPU失败或
ARM NativeBridge翻译执行表述为真机结论。
API30 x86 r16 的 SwiftShader ES2 实测因 `GL_MAX_FRAGMENT_UNIFORM_VECTORS (261)` 限制导致
Impeller shader 链接失败；同一 ARM32 APK 在 host GPU 下通过。必须保留失败记录，并在正式
承诺旧设备覆盖前补真实 GPU 与必要渲染回退验证，不能只凭 host GPU 的结果推定全部兼容。
API30 镜像若拒绝 shell 读取测试包的外部报告目录，只可在本次私有 userdebug 模拟器上临时
提升 adbd 采证并随后还原；不将此操作用于手机或正式 APP 数据。
`Install-Probe-Image.ps1` 只下载内置官方精确 revision/大小/SHA1 的测试镜像，另记录 SHA256；
不升级 SDK、不修改已有镜像。镜像放容量足够的 Windows 测试目录。

Android17 使用根 `toolchain.lock.json` 的 `flutterAndroidProbe.api37Windows` 独立测试配置：
Emulator37.2.12 与 API37.0 Google APIs x64 r6，4GiB/2核。其版本不改变生产 Android target36，
也不覆盖已有 Emulator35.4.9。官方镜像声明最低 Emulator36.5.11，旧工具不满足该依赖；
该声明不是一次实际启动失败的证据。
`Install-Api37-Environment.ps1 -ToolchainLock <根锁> -Directory <全新绝对目录>`
核对官方大小/SHA1 后解包并记录 SHA256，支持 `-ArchiveDirectory` 复用已经下载的
`emulator.zip` 与 `image.zip`（仍执行完整校验）。
`Start-Api37-Emulator.ps1 -ToolchainLock <根锁> -SdkRoot <已有SDK>`
`-EnvironmentDirectory <前述环境> -RunDirectory <全新运行目录>`
再次核验根锁配置与归档，使用私有 AVD、host GPU 和明确 emulator 路径；
按前述 `stop-requested` 正常关闭。下载与启动是独立步骤；没有运行报告即不算兼容通过。
版本与上游校验值来自 [SDK 仓库清单](https://dl.google.com/android/repository/repository2-3.xml)
和 [Google APIs 镜像清单](https://dl.google.com/android/repository/sys-img/google_apis/sys-img2-3.xml)，
[Emulator 发布说明](https://developer.android.com/studio/releases/emulator) 明确 API37 的 4GiB 要求。

## Android17 实测记录（2026-10-08）

`android-api37-target36-baseline-02` 在上述官方 x64 镜像、host GPU / RTX4080、
实际 Android17 / SDK37 / 4096页上取得14项 PASS：target36 Release首帧、Dart与Kotlin
真实LAN、域名/公网及redirect拒绝。APK SHA256 为
`61003ac8f89cb14410c6893c52d101e8425049d5b2036288849a84e2a7c60ba6`，
本地 build06 sourceDigest 为 `60812502abaa3abf090f396dd13ae10416a44fd5967bcff45c111d69c9248321`；
报告 SHA256 为 `648101789da4d00e17421aabc8694c331fdc1c1079f91026e0d8c7d4c43f8635`。
独立 aapt2 未声明新权限，OS隐式授权为true、NEARBY_WIFI_DEVICES仍未授予；
驱动未执行权限授予或adb reverse，抓取时探针窗口可见且无权限窗口。
这是窗口状态快照和源码/命令证据，不是全程人工录像。

同AVD的 baseline01 使用旧 build05，原“未声明”断言误读OS有效权限而13/14失败；原FAIL保留。
修正后的 build06 三ABI静态审计通过，本轮仅x64在Android17实际运行；此前7轮97项属于
build05 sourceDigest `cc5f49232c5f3d144fdd4b6f44979a14bd5b97a5d33a55fd217d320de586e436`。
不能把不同构建拼接成同一包全矩阵通过，也不覆盖ARM64物理16KB或ARM32 min24旧GPU。

环境收尾另有异常：`adb emu kill` 返回OK后，保留的官方 emulator 进程退出码为1，
启动脚本据此正确失败，未放宽退出0守卫。日志中的ColorBuffer/texture错误不足以证明唯一原因。
独立核验确认无该AVD进程、监听端口或adb设备，原全局Emulator35.4.9未变、两个LAN fixture已停。
运行兼容PASS与模拟器正常退出0未满足分别记录；未据此升级正式target或修改系统设置。
本地原始报告、40文件构建源码快照与独立15项核验位于
`temp/flutter-p0/android-api37-target36-baseline-02/`、`android-release-20261008-06/`、
`android17-target36-independent-verification-20261008.json`；环境日志在
`temp/flutter-p0/android-api37-avd-20261008-01/`。后续installer的归档复用/脚本hash记录小改
没有冒称为首次实际安装所执行版本，首次脚本另存为 `Install-Api37-Environment.invoked.ps1`。

依据：[Android 16KB](https://developer.android.com/guide/practices/page-sizes)、
[LAN 权限](https://developer.android.com/privacy-and-security/local-network-permission)、
[Release 网络安全配置](https://developer.android.com/privacy-and-security/security-config)。
运行证据必须明确区分真机、x64模拟器和静态检查，不把任意一个当成全部 P0 完成。
