# Companion IdentityPipeV1（MSC1）

本文件是产品 Mod 与客户端 MSC1 控制协议的唯一契约。实现为同目录 C#；当前客户端对端为 [P0 Windows 宿主](../../../../../tests/flutter-window-probe/windows/runner/control_probe_bridge.cpp)。它验证新的 Mod 输入入口，不宣称旧 Mod 1.3.1 已有实例兼容，也不代表完整 Flutter 产品已经实现。

## 配置与入口

`[Companion] ControlProtocol` 为枚举 `LegacyTcp`（默认）或 `IdentityPipeV1`，启动会话时固定，修改后重启游戏。LegacyTcp 保持原有 TCP 文本路径。IdentityPipeV1 仅 Windows x64，必须显式配置现存、本地、绝对 `ExecutablePath`；路径及其祖先不允许 reparse point。该模式不搜索相邻 EXE、不向旧 TCP 服务写命令、不因失败降级。

现有 `StewardOverlayController.ProcessToggleInput` 在真实 Unity Update 中第一次执行时，把 OS TID 交给单次 Prepare。Prepare 可注册已存在客户端，不能启动进程；无监听就结束此次准备。它不依赖固定延迟、窗口出现或业务菜单推断 Update 已运行。`AutoLaunch=false` 仍生效；只有实际 F8/RS 或既有自动启动入口提交的请求可启动进程。自动请求也必须等此首次 Update 后处理。

键盘和手柄继续读取现有配置键，默认 F8 和 JoystickButton9（RS）。`source=F8/RS` 表示输入通道；自定义按键不会改变协议编号。保留现有 Unity Legacy 输入和 InputSystem 反射读取，不引入业务 Hook。每个接受的物理输入在主线程冻结单调序号、OS TID、采样时前台 HWND/PID、本地 handoff epoch 及输入来源布尔值；跨线程只传这些不可变托管值，不能传 Unity/IL2CPP wrapper。

## 实例身份

1. 用 `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_LISTENER)` 只读查找端口 32146 的唯一 `127.0.0.1` IPv4 监听 PID。该端口上的 wildcard、IPv6、重复或未知监听是拒绝条件，不等于“没有客户端”。本协议不通过 TCP 连接/写入探测。
2. 保留该 PID 的进程 HANDLE，核对 creation FILETIME、完整映像路径、当前游戏进程的 Windows session 和 TokenUser SID。映像路径须精确等于配置的完整路径（Windows 大小写不敏感），且客户端不能是游戏进程。
3. 确实没有监听时，调用现有 `Process.Start` 方式一次，继续传 `--api`、`--game-pid` 及必要的 `--token`；保留新进程及其身份。最多 8 秒只读等待监听出现，每 40 ms 观察一次；监听者必须就是保留的新进程。它不是重试启动或重发控制命令的计时器。
4. 管道名固定为 `mystia-steward-companion.control.v1.<十进制 PID>.<小写十六进制 creation FILETIME>`，无前导补零。客户端须在当前用户 ACL、拒绝远程连接的本地管道上提供单一持久连接。C# 用 `GetNamedPipeServerProcessId` 与实际监听 PID 交叉核对；服务端须用 `GetNamedPipeClientProcessId` 核对保留的游戏 PID。
5. 双方独立核验保留 HANDLE 与 creation/path/session/user；注册绑定唯一游戏 `UnityWndClass` 可见、无 owner、非空客户区窗口，以及唯一 Flutter `FLUTTER_RUNNER_WIN32_WINDOW` 无 owner 顶层窗口。游戏窗口 TID 必须等于首次真实 Update 的 OS TID，激活输入 TID 仍须一致。

路径与 PID/creation 绑定不是 EXE 内容签名。P0 准备器另行校验同提交 DLL/宿主的 manifest 与哈希；不能把此测试包约束当成产品协议中的签名验证。

## 固定帧

每帧恰好 208 字节，所有整数 little-endian，无 JSON、字符串或变长字段。头部四个 UInt32 分别是 magic `0x3143534d`（字节 `4d 53 43 31`）、version `1`、kind、size `208`。kind 为 `1 Register`、`2 Registered`、`3 Activate`、`4 ActivationAck`、`5 Exit`、`6 ExitAck`、`7 ActivationObserved`。不接受其他版本、长度或 kind。

从 offset 16 开始为 24 个 UInt64，字段偏移为 `16 + index * 8`：

| index | 字段 | 含义 |
| --- | --- | --- |
| 0–1 | nonceLow / nonceHigh | 服务端产生的 128 位随机实例 nonce，两半均非零；注册请求为零 |
| 2–3 | gamePid / gameCreation | 保留的游戏进程身份 |
| 4–5 | clientPid / clientCreation | 实际监听者的保留进程身份 |
| 6 | requestId | 注册为 0，之后从 1 严格连续递增 |
| 7 | source | 0 AutoLaunch、1 F8、2 RS、3 Exit |
| 8–9 | gameHwnd / clientHwnd | 已绑定的顶层窗口 |
| 10–11 | gameThread / clientThread | 窗口的 Windows OS TID |
| 12–13 | foregroundBeforeHwnd / Pid | 单次 ASFW 前的真实游戏前台 |
| 14–15 | foregroundAfterHwnd / Pid | 激活 ACK 时的实际前台 |
| 16–18 | allowAttempted / allowSucceeded / allowError | ASFW 调用与 BOOL 结果（0/1）及实际 DWORD last-error |
| 19 | inputSequence | 主线程物理输入单调序号；Auto/Exit 为 0 |
| 20 | inputThread | 真实输入/首次 Update 的 OS TID；Exit 复用注册值 |
| 21 | clientFocusHwnd | ACK 时客户端 GUI 线程的实际 focus HWND |
| 22 | clientFlags | visible=1、interactive=2，仅允许这些位 |
| 23 | status | 请求为 0，ACK 为 1 已应用或 2 明确拒绝；注册/退出成功为 1 |

PID/TID 非零且不超过 UInt32.MaxValue；creation 为真实正 FILETIME；非零 HWND 不超过 Int64.MaxValue。物理 inputSequence 正且不超过 Int64.MaxValue；它只要求严格增长，丢弃的本地输入可以造成间隔。requestId 则不能跳号。P0 宿主额外将单次测试会话 requestId 限为 1000，超过即拒绝；这不是自动重连/换 nonce 的许可。

### 注册

Register 只填 2–5、8、10、20，其他全部为零。20 等于 10。服务端在 OS 身份核验后，Registered 原样回显请求，填入 0/1 随机 nonce、9/11 客户端窗口/TID、23=1；其他原零字段继续为零。注册失败关闭连接，不发送猜测身份的成功回复。

注册 ACK 证明该新 Mod 的真实 Update 已进入，且双方已绑定 OS 身份。它不证明游戏菜单、存档、业务对象或后续输入可用。

### 激活

Activate 继承注册回复的 nonce、双方进程及窗口身份；23 恢复为 0。6 为下一个 requestId；7 只能为 0/1/2；12/13 为已绑定的真实游戏前台；16=17=1，18 原样保留 ASFW last-error（不假定 BOOL 成功时 last-error 一定为零）。19 是该输入序号，20 为游戏窗口 TID。14/15/21/22/23 全部为零。

AutoLaunch 仅可作为 requestId=1，inputSequence=0；物理 F8/RS 的序号须大于先前物理序号。每次发送前重新核验保留身份、唯一窗口、精确前台及输入 OS TID；只有游戏实际前台时才向精确 clientPid 调用一次 `AllowSetForegroundWindow`，成功后提交一次 Activate。此方向的 toggle 语义是显示并激活客户端；由客户端的真实 F8/RS 把前台归还游戏，不用再次向 Mod 合成输入。

ActivationAck 原样回显请求，只有 14/15/21/22/23 可变化。status=1 必须同时满足真实前台为注册的 clientHwnd/PID、可见且可交互 flags=3、focus HWND 属于客户端线程及其顶层窗口。C# 在收到帧后独立读取 `GetGUIThreadInfo`、前台、可见/启用和透明/NOACTIVATE 样式再核验，不能把 ASFW、SetForegroundWindow 返回或帧声明当作实际成功。status=2 是拒绝；未知/无 ACK 不能重发或视为成功。

成功 ACK 后，Mod 完成上述实际核验并递增本地 handoff epoch，再发送一次 ActivationObserved。其 24 个 UInt64 与收到的 ActivationAck **逐字段相同**，仅头部 kind 从 4 改为 7。它仍是同一个 requestId，不调用 ASFW、不代表新激活动作、不再等待另一个 ACK。明确拒绝、错误 echo、未提交请求或已确认请求均不能产生此帧。

服务端写出成功 ACK 后保持 activationPending，不公开本次完成计数，也不允许客户端 F8/RS 提前归还前台。它只读等待 ActivationObserved，最多 3 秒；收到后逐项核验原 ACK echo，并再次核验客户端实际前台/focus，才计数成功并结束 pending。这样客户端后续操作不会抢在 Mod 消费 ACK 及实际核验之前；不能用固定 sleep 替代此同步。

### 退出

Exit 继承注册身份和窗口值，6 为下一个 requestId、7=3、20 为已绑定 gameThread；所有动作字段 12–19、21–23 为零。ExitAck 仅修改 kind=6、23=1，其余逐项回显。退出不重新枚举可能已销毁的游戏窗口；仍核对保留的进程与管道 OS 对端身份。该 ACK 只确认收到退出通知，真实 game exit0 必须由客户端另行观察保留 HANDLE。

Identity 模式在首次真实 Update 的 Prepare 中、worker 注册/激活入队前，注册当前游戏已核实的最终原生退出导出 `ApplicationExitHelper.dll!ApplicationExit`。`NativeExitRegistration` 固定实际游戏目录、helper 完整 SHA/长度、x64 PE、导出 RVA 与原始入口字节，通过锁定 #783 的 `INativeDetour` 创建并强引用原 trampoline 后才 Apply；拒绝已有修改、歧义模块或不同路径。没有已加载模块时，只加载已验证的绝对路径，依赖搜索限 System32；保留文件/模块/委托/detour 至进程结束，不重试失败注册。LegacyTcp 模式不安装此入口。

该游戏的 `GamePlatformManagerImpl.OnApplicationQuit` 原生实现会直接调用此 helper，helper 最终调用 CRT `exit(0)`；UnityPlayer 对 MonoBehaviour 的退出分派又先于 `Application.quitting`。因此只订阅后者不能保证在该游戏中获得退出通知，已移除该订阅实现。此次入口限定已确定结束进程的实际 export，不在可取消的 WM_CLOSE 或 wantsToQuit 阶段提前停止控制。回调不访问 Unity 对象，不 Dispose Controller，不修改退出码；任何结果最终都调用原 trampoline，不能以通信失败取消或替换原游戏退出。

最终 export 首次进入即创建单调的 13 秒预算，再调用当前 launcher 的 `NotifyNativeExitAndWait`。它只派发一次托管任务；全部普通日志、launcher/session 锁获取和通知都在该任务中执行，原生线程只有限等待该任务，不同步调用日志或获取产品锁。任务在日志之后、每次获得产品锁之后重新检查预算，过期则不再提交通知。它复用当前会话单次完成任务，将入口预算与会话已有截止时间取最早值，在释放两把锁之后等待严格 ExitAck；后台 worker 全程只使用托管值、管道和 Win32 身份查询，不要求主线程继续执行 Unity Update。ACK 经 `ValidateAcknowledgement` 全字段校验且未超时才完成为 `Acknowledged`。失败、取消、队列拒绝、未注册、worker 结束均完成明确结果，不留下永不完成的任务；无会话立即返回，绝不为了退出启动客户端。既有 OnApplicationQuit/OnDestroy 的 best-effort 通知若先发生，最终入口仍取同一任务/截止时间，不再提交一帧。

## 串行、停止和输入边界

单 worker、容量 8 的有界队列、一个持久管道连接；任意时刻仅一项请求等待响应。注册/退出 exchange 最多 3 秒，激活 exchange 最多 6 秒（P0 服务端实际焦点观察界为 5 秒），ActivationObserved 写入最多 3 秒。EOF、取消、错误版本、身份漂移、非法/额外帧、未知 ACK、满队列或其他未确定结果关闭会话；不自动重连、重放或回退。只读监听等待不提交控制动作。

每次提交前、注册/激活 ACK 验证时，用 PeekNamedPipe 拒绝尚未请求的积压数据。写入使用固定完整帧，读取处理分片并在 EOF 时拒绝不完整帧。

NotifyExit 与唯一 Process.Start、注册首 write、ASFW/激活首 write 使用同一停止门禁。停止后丢弃尚未提交的 Prepare/Activate；已经提交的激活仍可读取 ACK、核验真实状态并发送唯一 ActivationObserved，完成后才允许 Exit，不借此提交新的前台动作。会话首次停止最多 13 秒，覆盖在途激活 6 秒、确认 3 秒、退出 3 秒及调度余量；最终原生入口的预算还会约束此前的任务排队、日志和锁竞争，重复调用不能延长任一截止时间。空闲已注册会话的 Exit exchange 仍最多 3 秒。最终原生线程等待任务完成或入口剩余预算，不固定 sleep；即使工作线程卡在日志或产品锁，也会超时继续原函数，不重发。超过截止时间的 ACK 不能把超时结果改为成功。新会话取消旧 worker，不销毁客户端进程。此有界等待可能延长游戏最终退出，须用普通构建实测 kind5/6、Mod ACK 消费日志和保留游戏 HANDLE exit0，不能把入队、进入 hook 或诊断包通过算作普通产品通过。

普通日志 `native_exit_registered` 记录初始化；`native_exit_worker_entered/completion` 记录原生入口派发任务的执行阶段及捕获的原生线程 ID。后两项不证明原生线程实际等到了该结果：日志本身可能晚于其预算。实际原生等待结果只在显式 v3 诊断页以 Interlocked 记录，诊断不能替代普通构建。普通实测须分别核验工作阶段、Mod 严格 ACK 消费、原始 5/6 和进程 exit0。

采样时非游戏前台的 F8/RS 不入队；即便如此仍观察 held 状态并让键盘和 RS 分别等待回焦后的真实 released/neutral 采样。键盘在新模式额外调用既有 `Input.GetKey(config.ToggleKey)` 读取 held；不会改变 Legacy 路径。成功的客户端激活 ACK 还发布纯托管 handoff epoch，保证游戏后台没有 Update 时，下一次游戏帧也必须先观察对应按键释放；客户端归还游戏时仍按住的 F8/RS 不能变成新游戏 edge。每个输入冻结采样 epoch；worker 在实际提交时再次核对 epoch 与 HWND/PID，旧输入不能在同一游戏窗口重新获前台后被重放。该本地 epoch 不占用协议字段。前台或 epoch 已改变的、尚未提交的样本只丢弃，不消耗 requestId。客户端注入 key-up 成功不等于 Unity 已消费 released 帧；实机自动步骤若缺少该证据，应记录缺口，不用固定延迟假定就绪。

诊断仅输出结构化事件、来源、PID、输入序号/TID、可用的输入布尔值、Win32 error 和可选编译 SHA；不输出 nonce、API token、命令行或任意异常消息。`CompanionControlBuildGitSha` 非空时写入同名 AssemblyMetadata，P0 构建必须另行校验其值与完整源码提交一致。

退出通知缺失的 [P0 共享页诊断](EXIT-DIAGNOSTIC.md) 由独立编译开关和 fresh fixture 授权启用，不改变 MSC1；普通构建不含共享页访问。诊断页中的随机实例标识与 MSC1 nonce 无关；诊断运行不能替代普通产品退出证据。

## 依据与验证范围

客户端进程结束后，下一次真实 Update 仅在保留的进程 HANDLE 已触发、PID 与创建时间仍匹配时取消旧会话并创建新会话。新会话推进本地 handoff epoch，注册 nonce 和请求序号重新建立；旧队列与旧授权不转移，不自动再次启动客户端。新物理按键仍须先观察释放，才可启动新客户端。管道 EOF 或相同 PID 本身不构成换代依据，活着但协议失败的客户端不自动重连。

现有游戏入口遵循 [IL2CPP 分析工作流](../../../../../docs/il2cpp-analysis-workflow.md)。本次复核的当前 metadata / #783 wrapper / IDA 入口包括：

- `UnityEngine.Input.GetKeyDown(KeyCode)`：token `0x06000020`，RVA `0x2C89260`，对应原生 `GetKeyDownInt` icall；`GetKey` token `0x0600001F`，RVA `0x2C892A0`，对应 `GetKeyInt` icall。
- 默认 KeyCode：F8=289、JoystickButton9=339。InputSystem `Gamepad.current` token `0x060005E6` / RVA `0x2A17F80`；`rightStickButton` token `0x060005C9` / RVA `0x483190`，metadata backing offset `0x180`。该 RVA 与其他 getter 共享，IDA 自动命名不是 Gamepad 字段语义证据。
- `ButtonControl.isPressed` token `0x06001284` / RVA `0x2ACDD70`；`wasPressedThisFrame` token `0x06001285` / RVA `0x2ACDE20`，原生需本帧设备更新且当前按下、前帧未按下。沿用当前代码中每帧局部 wrapper 的反射读取，不新增对象缓存。
- `GamePlatform.MonoScripts.GamePlatformManagerImpl.OnApplicationQuit`：metadata token `0x060000C2` / RVA `0x2318910`；实际原生体先执行 Platform.Dispose，再直接解析/调用 helper export。它没有经过 metadata 中同 RVA `0x2317380` 的 `Exit/NativeApplicationExit` wrapper；只 Hook wrapper 会漏掉该退出路径。`SaveManagement.ExitGameEX` RVA `0x593400` 也汇合到相同 helper。
- helper `ApplicationExit` 导出 RVA `0x15a0` 的原生体调用 `exit`，其 thunk RVA `0x2bf0` 经 IAT 指向 ucrtbase。完整 SHA、大小与入口字节只在 [NativeExitContract.cs](NativeExitContract.cs) 定义。#783 `INativeDetour` 及 `IDetour` 分别来自已锁 BepInEx.Unity.IL2CPP / MonoMod.RuntimeDetour，不能用系统或额外未核验的 DLL 代替。
- 同版本 UnityPlayer 的退出控制流先运行 wantsToQuit，再分派 MonoBehaviour.OnApplicationQuit，之后才调用 `Internal_ApplicationQuit`（GameAssembly RVA `0x2BEEC60`）。这解释了后者在 helper 提前结束进程时不保证到达；静态链本身不替代本次 export 实际命中及双方 ACK 的实机证据。

分析输出按工作流保留在仓库外 `new/managed-source/{metadata,interop-783}` 和 `new/ida/export/pseudocode`，不是提交的游戏源码；Cpp2IL `ldnull; throw` 桩不能当实现。这里的 OS TID 绑定来自真实 Update 与窗口核验，不把托管 ManagedThreadId 当 OS TID。

[纯托管 smoke](../../../../../tests/companion-control/Program.cs) 覆盖 wire/身份规则、OS 表字节解析、序列、输入 neutral/epoch、分片/EOF/取消，以及一次注册、失败不重试、helper 身份/入口拒绝、同任务同截止时间和终止结果。可注入的短预算负例让日志或产品锁实际阻塞，验证调用线程有限返回、worker 不内联、过期后不继续操作；不每例等待真实 13 秒。它还直接编译生产 session，使用明确拒绝的注册桩验证未 Prepare 即退出、退休后重复通知、注册/日志失败不会悬挂任务；该桩不执行 Unity、Hook 或任何 Windows 进程/管道操作。这些测试不证明原生 detour、Windows API 或实物手柄通过。各首次启动、换代、旧 Mod、完整客户端、其他 DPI/普通用户、拔插场景须各自取得实机证据；不得从此契约或独立合作插件结果推断已经完成。
