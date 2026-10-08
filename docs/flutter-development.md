# Flutter 开发与探针

当前 Flutter 代码处于迁移探针范围。正式主客户端和更新链路的入口仍以[架构](architecture.md)、[更新系统](update-system.md)为准。开发方案保存在本地 `dev/deploy/`，阶段进度与实机证据缺口保存在 `dev/SESSION_HANDOVER.md`。

## 工具链

`toolchain.lock.json` 的 `flutter` 字段固定 Flutter、Dart、framework/engine revision，以及 Linux/Windows x64 官方归档的大小和 SHA-256。工具必须使用显式 SDK 目录；不得通过 PATH 搜索另一个 Flutter 或执行 `flutter upgrade`。已有 React/Tauri 入口检查新增锁字段的有效性，不要求安装 Flutter。

从仓库根目录使用锁定 Node 执行：

```bash
node scripts/install-locked-flutter.mjs --install-root temp/toolchains/flutter-3.47.6
node scripts/flutter-toolchain.mjs --sdk-root temp/toolchains/flutter-3.47.6
node --test tests/flutter-toolchain/flutter-toolchain.test.mjs
node scripts/run-flutter-network-probe.mjs --sdk-root temp/toolchains/flutter-3.47.6
node scripts/run-flutter-platform-probe.mjs --sdk-root temp/toolchains/flutter-3.47.6
node scripts/run-flutter-window-probe.mjs --sdk-root temp/toolchains/flutter-3.47.6
```

安装目标必须是尚不存在的目录，父目录须存在。`--archive <file>` 可使用已下载的官方归档，仍执行大小、哈希、归档路径和安装后版本校验。安装失败只清理本次创建的目录，不覆盖已有 SDK。版本以锁文件为准，上例路径名称仅方便区分本地安装。

## 目录和依赖

| 路径 | 职责 |
| --- | --- |
| `tests/flutter-network-probe/` | 纯 Dart IO 传输与原始 HTTP 行为，分别执行 JIT/AOT |
| `tests/flutter-platform-probe/` | Flutter Windows 探针 UI、Pigeon 进程内接口和 C++ 管道适配 |
| `tests/flutter-updater-probe/` | 无界面 Rust bootstrap、旧 CLI/状态契约和嵌入 bundle 校验 |
| `tests/flutter-toolchain/` | SDK 锁、安装失败边界与工具命令验证 |
| `tests/flutter-old-mod-probe/` | 原始 Mod 1.3.1 的副本准备、实际启动采集和物理桌面自动取消 |
| `tests/flutter-window-probe/` | 受控窗口、托盘与单实例，以及独立 XInput 和游戏副本焦点套件 |
| `tests/flutter-focus-cooperator/` | 仅装入新游戏副本的 BepInEx 前台授权插件、固定 IPC 契约和 .NET 6 smoke |
| `mods/bepinex/src/Plugin/CompanionControl/` | Mod 内显式启用的客户端身份协议；默认仍使用现有 Tauri 控制方式 |
| `tests/companion-control/` | 新控制协议、身份发现、输入释放门槛和取消的纯托管 .NET 6 smoke |

探针不能作为生产安装器分发或写入真实游戏目录。探针不存在安装操作，取消与失败必须写出明确结果，不能用“安装成功”表示连接成功。

Dart 包提交 `pubspec.lock`，直接依赖使用精确版本。标准验证入口使用锁定 SDK 和 `--enforce-lockfile`。生成代码与其定义同时提交；Pigeon 两端必须用同一锁定版本生成，禁止手改生成输出。在平台探针目录用固定 Dart 执行 `dart run pigeon --input pigeons/probe_api.dart`，随后执行 `dart format lib/generated/probe_api.g.dart`。标准检查将重新生成到临时目录，格式化后逐字节比较三份输出。共享 SDK 版本只写入根锁文件，子包 SDK 区间用于声明语言兼容范围，实际运行仍核对精确版本。

## 编码与进程边界

- Dart 开启 strict casts/inference/raw types；UI 负责显示和交互，传输、协议校验和状态转换分别置于独立模块。
- `dart:io` 与原生平台服务通过窄接口接入。Widget 不拼接协议 JSON，不持有安装事务，不直接读写游戏目录。
- 网络端点、认证及未知写入结果遵守[本地 API](local-api.md)；Flutter 探针不得引入另一套生产契约。
- Pigeon 只负责同一 Flutter 进程中的 Dart/C++ 通信。C++/Rust 跨进程使用独立版本的 Named Pipe 协议，并核对 nonce、进程身份、有界帧和请求/状态序号。
- 管道等待不阻塞平台线程；窗口销毁先停止后台 I/O 并回收适配器，再销毁 Flutter engine。异步结果只能在有效生命周期内交付。
- Windows 消息循环因应用退出而结束时，也必须显式完成窗口销毁，不能仅依赖 `WM_DESTROY`。释放资源前清空消息处理器可访问的成员，防止同步窗口回调重入正在销毁的 engine；COM 的寿命覆盖窗口与 engine。
- 不把未知数据自动修正成已知成功状态；通讯断开或校验失败后禁用进一步操作，不自动重复写请求。

## Windows 构建与实测

Windows 开发机需 Visual Studio C++ 桌面组件。PowerShell 7 中执行：

```powershell
./scripts/build-flutter-updater-probe.ps1 -SdkRoot C:/toolchains/flutter-3.47.6 -OutputDirectory C:/probe-output
```

输出目录必须不存在。构建依次验证 SDK、Dart、Flutter UI、Rust core 和嵌入 bundle；VC runtime 随 UI bundle 携带。工具版本、源码提交和产物哈希写入 `build-evidence.json`。

[Flutter migration probes](../.github/workflows/flutter-probe.yml) 是迁移分支的普通 Windows 构建工作流，输出 Actions artifact，不创建标签或 Release，也不需要发布密钥。用户操作和证据收集见[Windows 探针说明](../tests/flutter-platform-probe/README.md)。CI 构建通过只证明产物可构建；旧 Mod 启动、干净机 Defender/SmartScreen、真实安装/回滚及 Android 能力必须另有实测证据。

该工作流还在 Windows runner 启动交付的 bootstrap/Flutter Release 二进制。仅此探针支持 `MYSTIA_UPDATER_PROBE_AUTOMATION=cancel-after-ready`：首个 ready 帧后执行与按钮相同的取消、回包校验和 Flutter 退出流程。`Start-Probe.ps1` 区分自动与手动证据，要求退出码为 0、状态严格为 `cancelled`/进度 0，且 fixture 未修改、backup 不存在；非零退出或错误状态会使 CI 失败。独立 runtime evidence artifact 保留报告，不能用此自动运行替代用户视觉、防护或旧 Mod 实测。

报告判定的失败边界由 `tests/flutter-platform-probe/Test-Start-Probe.ps1` 验证，CI 分别用锁定 PowerShell 7 与 Windows 内置 PowerShell 5.1 执行；后者仅验证交付脚本的兼容性，不替代构建工具链。

旧 Mod 实际启动使用独立的[实机探针](../tests/flutter-old-mod-probe/README.md)：完整游戏副本保留旧 DLL，注入离线测试缓存，严格核对游戏/runner PID、路径和哈希后只发一次安装等待请求，最终验证取消退出与插件不变。SSH 接入时通过已配置的交互式桌面任务执行，自动操作与人工观察分别记录。[旧 Mod 探针工作流](../.github/workflows/flutter-old-mod-probe.yml) 只构建该测试驱动和运行独立测试；实际游戏结果另行保存。测试工具不会改变旧产品的更新发现、下载和安装契约。

[窗口探针](../tests/flutter-window-probe/README.md) 独立验证主客户端需要的 Windows 能力。构建入口为 `scripts/build-flutter-window-probe.ps1`；通过交互式节点运行完整 bundle，用受控窗口的实际屏幕采样、标记输入和前台身份验证行为。生命周期阶段另启独立 Flutter 主实例，验证隔离端口的真实第二进程转交、Shell 托盘点击与菜单，以及退出后同端口重建。`all` 的 37 项必测检查全部通过才报告本套 PASS。

同一工程另有 `xinput` 和 `focus`，分别读取用户实际手柄操作、验证本次固定游戏副本与 Flutter 的焦点交接，结果不并入原 37 项。准备脚本冻结原游戏身份、复制并比较源快照；游戏副本不隔离存档或 Steam Cloud，因此探针只检查操作系统窗口，不注入游戏业务输入。新 Pigeon 接口与原窗口接口分别生成并逐字比较。六项手柄检查、八项游戏焦点检查及全部 P0/DPI 矩阵仍各自按实机证据验收，不据此宣称产品控制器领域逻辑或旧 Mod F8/RS 链路已迁移。

游戏→后台 Flutter 的自动恢复另受 Windows 前台权限约束。`GetFocus` 和 Dart 焦点只说明相应线程/控件状态，即使两者已恢复，系统前台仍可能属于游戏；`SetForegroundWindow` 返回失败时也不能用 `GetLastError=0` 推断成功。独立 `focus` 中 Flutter 启动游戏的拓扑不代表旧 Mod 启动 Flutter，受控窗口的协作授权也不证明真实游戏会授权。[Windows 前台规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)、[线程焦点规则](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getfocus)。

旧 Mod 1.3.1 的已有客户端路径优先直连 TCP，写入成功便返回，不能假定每次 F8/RS 都创建可转授权限的第二进程。首次启动、已有客户端自动恢复、用户点击激活须分别验收。独立[合作插件](../tests/flutter-focus-cooperator/PROTOCOL.md)与新的 [Mod 身份控制协议](../mods/bepinex/src/Plugin/CompanionControl/PROTOCOL.md)分别验收，不据其结果声称旧 Mod 已运行实例兼容。不通过反复抢前台、模拟 Alt 或放宽判定绕过限制。[AllowSetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow) 授权也可能失效，接收端始终核对实际前台。

该测试插件通过同步注册的独立 `MonoBehaviour.Update` 记录主循环推进；后台只读取不可变托管心跳，不持有 Unity 对象。首次连接就绪、各轮授权及管道 EOF 收尾均等待真实新心跳，序号、单调时钟和操作系统线程身份纳入严格证据校验。心跳仅证明测试组件收到帧回调，不能替代菜单、业务状态或正常退出判定；固定延迟不作为就绪依据。

`hotkey` 套件用于新 Mod 的真实 F8/RS 路径：Flutter 探针先独占监听实际控制端口 32146，再启动只替换了 Mod DLL 的游戏副本。首次真实 Mod Update 注册身份；自动 F8 经 Win32 输入进入 Unity，客户端 F8 经 Dart KeyDown 与原生消息双重确认；真实 RS 由用户按提示操作。每次游戏→客户端激活都使用本次游戏授权及实际前台回执，长按跨焦点必须先释放才能再次触发。默认 `LegacyTcp` 与旧 Mod 不变；显式 `IdentityPipeV1` 不静默降级。此套件只验收新 Mod 与已有 P0 宿主，不代表完整 Flutter 主客户端、旧 Mod 兼容或首次启动已完成。构建、隔离准备及十项判定见[窗口探针](../tests/flutter-window-probe/README.md#真实-mod-控制套件)。
