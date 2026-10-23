# companion_platform

Flutter 客户端的平台边界，不包含业务 HTTP 或自动化许可判断。Pigeon 源为 `pigeons/platform.dart`，通过锁定 Dart 执行 `dart run pigeon --input pigeons/platform.dart` 生成 Dart/C++/Kotlin；生成文件与源一起纳管。

仓库根 `scripts/flutter-workspace.mjs --sdk-root <锁定SDK> --task format-check` 只读检查格式；`--task generated-check` 在全新的 `temp/pigeon-check-*` 目录复制输入/依赖锁，从零生成三种语言并标准格式化生成的 Dart，然后逐字节比较生产文件。失败时保留差异证据，不自动覆盖纳管输出。更新 Pigeon 后需对生成 Dart 执行同 SDK 的 `dart format`。

- Windows runner 显式编译 `windows/platform_bridge.cpp` 与 Pigeon C++；提供窗口/托盘、F8/RS 焦点切换、F10 穿透恢复、保留句柄的游戏进程监视、单实例与 v2 控制通道。原生窗口状态存入 `%LOCALAPPDATA%/mystia-steward-companion/flutter-v1`。
- Windows 身份密钥使用当前用户 DPAPI，Android 使用 AndroidKeyStore AES-GCM 与应用 no-backup 目录。密文损坏时读、覆盖和删除均报错并保留原文件；普通偏好采用原子写入、持久化回读。
- `CompanionPlatform.writePreferences` 串行合并顶层字段，只有原生和 Dart 两次回读一致才完成 Future。设备同步层必须等待此 Future，再发送精确 ACK。
- `HistoryMigration` 仅接收用户明确提供的 schemaVersion 1 JSON 偏好文件；严格白名单及数值/枚举校验，旧 1/0 布尔值规范化。不读取浏览器数据库，不导入 Token、clientId 或任何自动化运行开关。
- `GamepadInputController` 只解释纯输入快照；失焦、断开和首次连接要求回中，边缘动作不连发，方向键使用初始延时与重复间隔。LB/RB 切换主页面，LT/RT 切换当前页面横向 Tab；扳机不模拟滚动，同时按下两侧扳机不导航，释放并重新按下才产生新动作。Windows 原生负责 F8/RS，Flutter 不重复处理这两个切换入口。

## 控制协议

生产回环端口为 32146。请求必须使用 LF 行尾，总长不超过 4096 字节；动作是 `mystia-companion-v2:identity`、`show`、`toggle`、`exit`。identity 不带参数且不改变 UI。show/toggle 可追加 `--api=`、`--token=`、`--game-pid=` 行，写完后关闭发送半边。

首次启动与单实例激活使用同一连接参数规则：更新连接时，`--api=` 与 `--token=` 必须各出现一次并成对解析，地址不能为空；单边参数、重复参数或参数内换行均在生效前拒绝，不能组成“新地址 + 旧 Token”。无凭据的 show/toggle 保留原有连接。显式 `--token=` 空值表示本次没有凭据，原生快照会与地址一起原子更新，不从旧快照补值；冷启动不会恢复安全存储 Token，已有 Dart 会话继续忽略空凭据激活，不把它解释为断开或重连指令。

ACK 为 `mystia-companion-v2:ok:<pid>:<creationTimeFileTimeU64>\n`，仅表示该进程接管请求；退出完成必须等待已验证进程句柄。Mod 和二次启动先通过 identity 核对创建时间与完整 EXE 路径，再授权前台并发送凭据/动作。错误帧响应 `mystia-companion-v2:rejected\n`。

窗口按钮、F8 和 RS 使用同一焦点切换流程。跨进程激活由系统异步完成，平台 bool 表示请求已接受；冷却期重复输入直接吸收。管家通过已有 UI 定时器观察已绑定游戏进程成为前台后才隐藏，不阻塞游戏消息循环。切到其他窗口、进程退出或 1.5 秒仍未完成时取消隐藏；不会通过附加输入队列或模拟按键抢占焦点。

## 隔离验证

`--control-port=` 与 `--isolation-id=` 必须成对使用，非生产端口对应独立测试数据子目录，不能传入真实游戏 PID 或凭据。

- `flutter test`：持久化同步边界、历史导入校验与手柄状态机。
- `node packages/companion_platform/test/run-focus-transition.mjs`：锁定 Windows 工具链的纯原生时序回归，覆盖接受后延迟激活、拒绝、取消、目标退出与超时，不启动窗口或游戏。
- `test/native_smoke_main.dart` 和 `test/Invoke-NativeSmoke.ps1`：单独编译测试入口，验证真实 DPAPI、损坏保留、窗口、单例、身份握手和实际退出；增加首次启动不完整参数拒绝、控制帧拒绝不改变旧值、完整凭据对原子替换与显式空Token。所有凭据为合成值，不创建业务HTTP连接；仅允许仓库 temp 内的测试 EXE。
- `test/Measure-OfflineClient.ps1`：最终 Release 的隔离离线窗口采样，默认 60 分钟。通过同 PID 的精确 Flutter 主窗类与 v2 show 测量受控显示时间，不把输入法辅助窗或 `Process.MainWindowHandle` 当作主窗；该时间不是自然启动、首帧或业务连接就绪。CPU/内存数据不等于游戏负载性能，也不提供旧客户端对照。
- `test/Test-GameLifetime.ps1`：最终 Release 绑定本次自行创建的睡眠测试进程，检查该进程结束后客户端正常退出；不启动、不查找、不终止真实游戏。
- `test/Test-EmbeddedIcons.ps1`：仅把 EXE 映射为数据/图像资源，逐帧比对源 ICO；Flutter 传 `-GroupIconId 101`，Rust updater 传 `32512`，不执行被检查程序。
- `test/transparency_visual_main.dart`：隔离原生逐像素透明度验收入口；在黑/白/绿色背景板上实际观察alpha=0/64/128/255红色色块及不透明文字。`DwmExtendFrameIntoClientArea`或全窗alpha=255返回成功都不能代替这个检查。Windows使用DWM逐像素合成，Flutter负责背景/内容分层alpha；不把该能力描述成Acrylic模糊。
- `tests/flutter-platform-control`：C# 回环 fake peer 与自建退出子进程，核对 LF、身份/路径、授权顺序、凭据隔离及 ACK 后实际进程退出。

Android Keystore 真机故障恢复、硬件手柄、多显示器/DPI 和真实游戏焦点切换仍须在对应设备环境验证，不能由上述测试推导通过。
