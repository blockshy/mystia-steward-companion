# Flutter 安全存储与目录 P0

本独立探针仅验证 Windows/Android 候选插件的实际可用性，不提供生产迁移向导。只生成随机合成凭据，固定独立 key 前缀、Android 包/Keystore namespace、Windows ProductName 支持目录；不读取用户 Token，不枚举旧凭据，不调用 `deleteAll`，不自动修复、迁移或覆盖未知存储。

## 精确候选与隔离

Flutter、Dart、AGP、Kotlin、JDK、NDK、Gradle 读取根 `toolchain.lock.json`。Android 候选固定 `flutter_secure_storage 10.3.4`，双平台目录固定 `path_provider 2.1.6`；全部传递版本/下载 SHA 在 `pubspec.lock`，构建使用 `--enforce-lockfile`。Android 为 RSA OAEP/AES-GCM 与 Android Keystore，明确禁用 `resetOnError`、算法迁移和 Auto Backup。[安全存储 10.3.4](https://pub.dev/packages/flutter_secure_storage/versions/10.3.4)、[path_provider 2.1.6](https://pub.dev/packages/path_provider/versions/2.1.6)。

**拒绝 Windows 插件后端**：传递包 `flutter_secure_storage_windows 4.2.2` 在解密/JSON 损坏时删除该 APP 加密存储文件后抛异常，WindowsOptions 没有关闭选项，Android 的 `resetOnError=false` 不适用。其传递注册代码可以存在于 bundle，但探针 Windows 路径不调用它。不能登记该候选选型通过，也不写捕获删除后恢复的兼容层。

Windows 改用 [windows_dpapi_store.dart](lib/windows_dpapi_store.dart) 的窄实现：`win32 6.4.0` / `ffi 2.2.0` 绑定当前用户 DPAPI，禁止交互且不设置 LOCAL_MACHINE；独立目录、单 key 文件、有界 envelope 校验、flush 后同卷 MoveFileEx 发布且不允许覆盖现有文件。读取/删除遇到解密或格式失败直接停止，保留文件。原生分配通过 Arena / LocalFree 释放；这不承诺 Dart 堆内明文可彻底擦除。[DPAPI 契约](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata)、[MoveFileEx](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-movefileexw)。

Android 项目保持 min24、compile/target36，Release 仅 ARM64（双 ABI 可行性由独立 Android P0 验证）。包名为 `com.tyukki.mystia.steward.companion.storagep0`，测试证书不许等于产品证书。`path_provider_android 2.3.1` 使用 `jni_flutter 1.0.4+1`，该传递插件使用 Android Platform35 r2 与 CMake 3.22.1 编译，实际版本写入 APK 审计；主应用并未改 target。显式 split-per-abi 后审计所有插件 `.so` 的 ABI、16KB ZIP/ELF 布局，不能由仅 engine 合规外推插件。Windows ProductName 为 `mystia_storage_probe`，与正式客户端分离。

## 四个独立进程

1. `write`：拒绝已有 key/目录，生成随机 32 字节合成值，安全存储写入并回读；私有 support 目录仅写合成值的摘要和 PID，以临时文件 flush/rename 并回读验证。
2. `read`：要求新 PID，从安全存储恢复并比较摘要；不从普通文件恢复明文。
3. `delete`：要求已有独立 read 进程证据，只删除本 run 精确 key，立即验证不可读。
4. `confirm`：再次独立进程验证删除持久化，只删除三个精确自建文件和空目录。

每阶段还验证 path_provider 临时目录的专属文件写入/读取/删除。报告只含来源摘要、运行 ID、PID、路径和断言，不含凭据值或任意插件异常文本。Android Pigeon 将有界报告写到独立 APP 的外部 files 路径，便于未 root 真机的 ADB 收集；被测凭据与普通状态仍在 APP 私有存储。

Android 插件写入使用异步 `SharedPreferences.apply()`；Dart Future 返回不是持久化 ACK。探针让 Activity 正常 finish，经实际 onStop/onDestroy 后发布报告，再由驱动结束进程、启动下一阶段。不能用 `dart:io exit()` 代替 Android 正常生命周期，也不以固定等待时间猜测落盘。首次强制退出后的 read 失败报告保留，正常生命周期实测通过也不证明突然杀进程或断电耐久性。生产凭据保存成功提示和协议 ACK 必须区分内存写成功、生命周期落盘与可确认的持久化保证。[Android apply 契约](https://developer.android.com/reference/android/content/SharedPreferences.Editor#apply())。

Windows 另有独立 `corrupt` 进程：生成自有密文控制样本和篡改副本，要求 DPAPI 读取和删除均拒绝，两个文件逐字节摘要不变且控制样本仍可读。证据保留两个合成密文文件及 SHA；之后由测试控制器精确清理自建数据，不能把这一清理当存储适配器的错误恢复行为。

## 构建与运行

使用锁定 Node，先运行 `node scripts/run-flutter-storage-probe.mjs --sdk-root <精确SDK>`，完成生成逐字校验、format/analyze 与四项逻辑测试。逻辑测试使用内存存储，不能替代平台验证。

Linux 设定锁定 `JAVA_HOME` 和相等的 `ANDROID_HOME`/`ANDROID_SDK_ROOT` 后，加 `--build android --output <新绝对目录>`。输出 APK、签名/包名/Release 审计、源码 digest 和完整文件哈希。开发期允许明确记录 dirty checkout 的 Android 源码 digest；不能将它说成干净提交的 CI 包。

Windows 使用 `--build windows --output <新绝对目录>`；必须干净完整 Git 提交，前后核验不变，包包含完整 Flutter bundle 和 app-local VC CRT。独立 workflow 只构建，不替代物理桌面证据。上传完整输出到 `D:/dev/mystia-node/runs/<runId>/payload/`，按既有节点请求运行 `mystia-steward-companion-storage-probe.exe`、`suite: all`。GUI 不经 SSH 直接启动。父进程依次创建五个实际子进程，保留进程对象/退出码并核对独立 PID、精确报告身份，最后写 `probe-result.json`。

Android 使用 [Invoke-Android-Storage-Probe.ps1](Invoke-Android-Storage-Probe.ps1)，参数为 `Adb`、明确 `Serial`、`Bundle`、全新 `RunId` 和 `OutputDirectory`。只允许新安装独立包，安装前后核验 APK 哈希，不修改正式 APP。四阶段各由独立 Activity/进程运行，收集后外部核验报告/sourceDigest/唯一 PID。完成后再次核对安装 APK 哈希再仅卸载独立包；禁止清空正式 APP 或安全存储。

该结果不证明硬件安全级别、备份恢复、跨用户/跨机迁移、普通用户权限矩阵、生产升级或断电原子性。Android API24 和原生 ARM64 16KB 仍有独立硬件覆盖缺口。安装版导出/导入与来源证明由后续产品阶段实现。

## 已取得的实测证据

2026-10-08，两个独立来源的 Release 包均完成真实进程重启后的写入、恢复、精确删除和删除持久化验证，每个平台四阶段各 34 项断言。它们的源码摘要不同，不能视为同一个构建或整项迁移 P0 全部通过。

| 平台与来源 | 实际结果 |
| --- | --- |
| Windows x64，提交 `033247de2224cc51cf362c60191084062b180a42`；[CI 37714133139](https://github.com/blockshy/mystia-steward-companion/actions/runs/37714133139)，artifact `11522752597` | 固定物理桌面节点、内置管理员运行 `storage-windows-20261008-01`。父进程及五个不同 PID 的子进程全部退出 0；DPAPI 损坏副本的读取和删除均拒绝，原密文和损坏密文摘要不变，控制样本仍可读。回收后独立核对两个保留的合成密文仅差一字节，bundle 文件未变，无本次进程和自建存储残留。此结论只适用于自有 DPAPI 实现，不改变 Windows 插件后端的拒绝决定。 |
| Android 16 / API36、ARM64、4KB 物理手机；本地 `storage-android-build-20261008-07`，明确为 dirty checkout | 独立包 `storage-android-physical-20261008-03` 的四个真实 PID 完成全部断言，均取得正常 Activity stop/destroy 回执；Release/独立测试签名及所有原生库的 16KB ELF/ZIP 静态检查通过。收集报告并核验安装 APK 摘要后仅卸载独立包。未把 4KB 真机运行当成原生 ARM64 16KB 运行证据。 |

Windows 构建源码摘要为 `2a6fac19b2bafb3a3895f029977074dde19b265a90b7de229946d57d532d1c27`；Android 构建源码摘要为 `61795074ae364fb9c32810da35aeca92443b763b4856e848b027577234a28ad2`，APK SHA-256 为 `4cff9d81d9b1839881c71829ecfa402d8513ea409ec3a2df98ac668bc0f752e7`。本地运行目录 `temp/flutter-p0/storage-windows-20261008-01/` 和 `temp/flutter-p0/storage-android-physical-20261008-03/` 保留原始阶段报告、独立验证和清理证据；临时证据不提交 Git。

初始失败同样保留：Android `storage-android-physical-20261008-02` 在直接 `exit()` 后，新进程未读到安全值；对保留合成状态的受控诊断明确返回 `secure-value-present-after-process-restart` 失败，而目录、原状态和独立 PID 检查已通过。改为正常生命周期后再重新安装运行，才获得上表结果。首个 Windows CI 在构建前因 `Path`/`PATH` 大小写处理丢失搜索路径而失败；`033247d` 修正该问题，并禁止 Windows 构建发现无用的可选 JNI/JVM 依赖，随后干净 CI 和真机均通过。
