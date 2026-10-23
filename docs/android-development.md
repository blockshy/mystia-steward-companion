# Android 客户端开发

唯一Android产品工程是 `apps/companion-flutter/android`，页面与Windows共用Flutter/Forui。Dart IO直接连接游戏电脑的局域网HTTP API；不经过WebView、Tauri代理或浏览器CORS。

## 工具与构建

从仓库根目录运行脚本。版本由 `toolchain.lock.json` 控制：Flutter/Dart、JDK、AGP、Kotlin、Gradle、SDK、Build Tools和NDK均须精确匹配。默认端点必须填写带端口的私有IPv4 HTTP地址；禁止DNS、公网端点、重定向和系统代理。

```powershell
$env:MYSTIA_FLUTTER_SDK_ROOT = '<锁定的 Flutter 目录>'
node scripts/flutter-workspace.mjs --sdk-root $env:MYSTIA_FLUTTER_SDK_ROOT --task get
$env:MYSTIA_ANDROID_KEYSTORE_PROPERTIES = '<既有正式签名配置绝对路径>'
node scripts/build-android-signed-apk.mjs
```

校验通过的可交付产物位于 `mods/bepinex/dist/mystia-steward-companion-android-arm64-v8a.apk` 和同目录的 `mystia-steward-companion-android-armeabi-v7a.apk`。ARM64 设备选择前者。`build/app/outputs/flutter-apk/` 是构建中间输出目录，不应把历史残留文件当成本次交付结果。

普通 `build-android` 入口和 Gradle Release 任务缺少签名配置均明确失败；不会再静默生成未签名 `release.apk`。构建后实际校验原 applicationId、根锁签名证书、版本/ABI、`apksigner verify` 和 16 KiB `zipalign`。新正式版本必须使用同 applicationId、同证书并递增 versionCode；构建脚本还要求显式 build number 高于历史正式基线 1003001。相同版本的本地重新构建不构成新的正式发布；不能用 debug 或新证书替代原证书。

## 身份、生命周期与数据

Android 页面采用底部六项导航（总览、经营、推荐、扩展、数据、设置），二级页签单行横向滚动；不复用Windows窄侧栏。页面保留触屏控件尺寸、系统安全区和软键盘避让，状态摘要可点击查看完整版本/连接/错误信息。设备检查使用独立的 `tool/preview/android_main.dart` 合成模型入口，必须在派生副本中使用独立 `uipreview` 包名并移除网络权限；正式发布入口始终为 `lib/main.dart`，不提供产品内模拟连接模式。

applicationId固定为 `com.tyukki.mystia.steward.companion`。Flutter版本名和显式build number来自pubspec；build number必须高于历史正式基线并符合发布版本约束，不能仅套用旧版本名计算规则。当前工程 minSdk=24、targetSdk=36；这只是构建目标，不等于已完成所有 Android 版本的实机验证。

Pigeon将凭据交给Android Keystore保护，普通配置通过原子发布与回读保存。关闭系统备份，不把旧设备身份或运行许可带入新客户端。损坏的凭据报错并保留，不以清空后重建伪装恢复成功。

Android进入hidden/paused/detached停止租约续约并尽力释放；inactive可由短暂系统面板产生，不直接当成永久后台。回到resumed重新读取设备权威及当前业务状态。手机不提供Windows托盘、穿透、全局焦点和游戏进程联动。

连接页默认展示当前连接；点击“修改连接”时回填安全存储中的 Token，密码框可主动显隐。切换地址会撤回尚未编辑的旧 Token，草稿、选区与输入法组合状态由页面保留。Token 不进入最近操作或原始请求日志；允许显隐不代表取消认证、配置同步或主设备权限。局域网监听和 Token 修改仍由游戏电脑本机管理，Android 只编辑自己的连接凭据。

游戏图像经已认证连接按需读取游戏电脑的已验证缓存，不内置于 APK。数据页提供料理、酒水和资源管理；资源重新读取由主设备发起，不在手机提取 Unity 纹理。详见[客户端 UI 规范](companion-ui.md)与[运行时图像资源](game-media.md)。

## 正式签名

`scripts/build-android-signed-apk.mjs` 是正式签名包装入口：由调用环境提供既有 keystore 与密码，校验全部交付条件后才事务写入本地 dist，不发布 GitHub Release。`MYSTIA_ANDROID_KEYSTORE_PROPERTIES` 可指向已有受保护配置，构建只读使用，不复制或删除原件；未设置时读取 `apps/companion-flutter/android/keystore.properties`。配置必须恰有 `keyAlias`、`storePassword`、`keyPassword`、`storeFile` 四项，不能记录到日志或提交 Git。CI 临时物化材料由其原有 `always()` 清理步骤处理。

没有可用的正式密钥时必须报告缺口，不能新建证书声称延续原安装。发布步骤见[发布流程](local-release.md)。构建成功不表示已完成Android实机、安全存储硬件行为或覆盖升级测试。

## 安装失败诊断

Android 要求 APK 在安装前完成数字签名，见 [Android 官方签名说明](https://developer.android.com/studio/publish/app-signing)。`aapt2 dump badging` 能读出包信息、ZIP 对齐通过，都不能代替签名验证。若 `apksigner verify --verbose --print-certs` 返回 `DOES NOT VERIFY` / `Missing META-INF/MANIFEST.MF`，该 APK 不可安装，应重新用既有证书构建。手机显示的“33 / packageInfo is null”可能是厂商安装器包装信息，不能单凭它推定通用 Android 错误码，也不应通过卸载旧版绕过证书问题。

## 验证

- Dart平台边界测试覆盖持久化回读、并发合并、敏感字段拒绝和迁移白名单。
- Flutter组件测试覆盖触屏宽度、字号、输入、搜索、Token回填/显隐及权限；帮助使用正式随包 JSON。
- 真实HTTP隔离宿主验证客户端协议与设备同步。
- 双ABI Release编译验证Android/Kotlin/Flutter资源与工具链。
- 离线预览仅验证指定设备上的布局和合成交互，不能证明正式 API、自动化或真实游戏图像运行正常。
- 正式包覆盖升级应保留应用数据，并回读安装包核对签名与哈希；不能通过卸载旧版或清空身份来替代升级验证。
- 实机连接、后台/前台、旋转/键盘、Keystore硬件/损坏行为及各目标 Android 版本需要各自的设备证据。记录机型、系统、APK 来源与验证范围，不能将 Android 12 预览结果当作 Android 16 通过。
