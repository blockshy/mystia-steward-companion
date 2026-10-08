# P0 退出回调诊断页

这是定位退出通知缺失的独立诊断，不属于 [MSC1](PROTOCOL.md)，不额外增加 Hook、前台动作、重试、关闭等待或退出成功条件。正式实现的原生退出边界和有界等待见下文及 MSC1；诊断只记录阶段。原生命周期测试仍要求真实 Exit/ExitAck；未收到时仍 FAIL。诊断版改变代码与初始化调度，即使收到 5/6，也不能替代普通产品构建的实机证据。

当前页版本为 3：保留版本 1 的 Quit/Destroy/Dispose 槽，将版本 2 的 Application 事件槽替换为原生注册、注册失败、最终入口、ACK、未确认、异常和原函数调用七项，并记录实际原生入口线程。71b6607 lifecycle03 的版本 1、cda4339 lifecycle05 的版本 2 原始失败证据保留；后者已记录正式订阅，但退出事件入口仍为零。历史解码必须显式选择版本，不自动升级。

同版本 metadata、#783 interop 和 IDA 已确认：`GamePlatformManagerImpl.OnApplicationQuit`（Omt.GamePlatformAPI，token `060000C2`、RVA `2318910`）先调用平台 Dispose，再直接调用 `ApplicationExitHelper.dll!ApplicationExit`。`SaveManagement.ExitGameEX`（RVA `593400`）经 helper wrapper（RVA `2317380`）到同一导出；前一调用已内联 wrapper，所以只 Harmony wrapper 会漏掉窗口退出。相同 SHA 的 UnityPlayer 原生退出顺序为 wants-to-quit、MonoBehaviour `OnApplicationQuit` 消息、最后 `Application.quitting`；游戏自己的退出回调因此可在正式事件之前终止进程。

精确 helper 为游戏主程序同目录 `<exe名>_Data/Plugins/x86_64/ApplicationExitHelper.dll`，49,237 字节、SHA256 `6c424e3ed02d2767494e37352ffa3c32eb7999b00d5c4771c43073d675e724ae`。导出 `ApplicationExit` 的 RVA `15A0`、Windows x64 `void(void)`，真实指令只调用 `ucrtbase!exit(0)`。DllMain/DllEntryPoint 返回 TRUE，加载时只有标准 CRT/异常框架初始化，不调用退出导出。原生证据能确定控制流；此前实机零标记不能单独证明哪一条 native 调用已发生。

正式 `NativeExitRegistration` 只在显式 IdentityPipeV1 的真实 Update 注册。完整路径、各级无 reparse、只读禁写/删除文件句柄、大小/哈希、PE x64、唯一已加载模块路径、导出 RVA 和未修改入口机器码联合验证。未加载时用绝对路径及仅 System32 依赖搜索的 LoadLibraryEx；不改全局 DLL 搜索路径，不调用退出导出做试验。#783 `INativeDetour.Create`、生成 trampoline、发布静态根后才 Apply；文件、模块引用、detour 和委托保持进程寿命。未知 DLL 或已有不同 patch 停止身份控制，不覆盖。

唯一最终导出处记录实际线程后，只投递一次托管工作并在 monotonic 13 秒预算内等待。lifecycle/session 锁与同步日志只在被投递的工作线程执行；会话仍复用同一个退出完成任务，以原会话截止时间和本次入口预算的较早者为限，迟到 ACK 不冒充期限内完成。线程不等于 Update TID 时如实记录，不访问 Unity 对象。确认、失败、取消、无会话或超时都继续精确原 trampoline，不取消游戏退出、不发送输入、不重发退出。

普通包初始化记录 `native_exit_registered`；终止日志 `native_exit_worker_entered/completion` 仅由被投递的工作线程写入，携带捕获的原生入口线程标识，完成结果明确区分确认、失败和超时。它们证明入口投递和工作线程阶段，不证明原生等待者实际观察到结果；原生结果只能由诊断页的 Interlocked 槽证明。日志缺失不追认成功，普通包仍必须用新 run 的入口工作日志、Mod 已消费真实 5/6 和保留进程 exit0 验收。原生回调的正常、catch 和 finally 路径均不写同步日志、不进入 lifecycle/session 锁。

只有 `CompanionControlExitDiagnostic=true` 才定义 `COMPANION_CONTROL_EXIT_DIAGNOSTIC`、允许诊断指针代码并加入同名 AssemblyMetadata。普通构建的 `Conditional` 调用连同参数求值均被移除；编译 SHA 本身不是诊断许可。

专用构建入口在原命令末尾显式加 `--exit-diagnostic`，输出 manifest 增加 `exitDiagnostic:true`。准备器必须同时使用 `-Lifecycle -ExitDiagnostic`，且匹配该 manifest；诊断包不能用于普通或旧 Mod 场景。原 `control-lifecycle.json` 保持七字段不变，另以七字段 `control-exit-diagnostic.json` 绑定 schema/kind/run/SHA、`diagnosticOnly:true`、Mod manifest 与 prepared evidence 哈希。所有结果创建新文件，不覆盖已有证据。

控制器创建游戏后、恢复其唯一主线程之前建立 4096 字节 pagefile-backed 映射：`Local\mystia-steward-companion.exitdiag.v3.<gamePid>.<gameCreationHex>`。ACL 仅当前用户、不可继承，已存在同名对象一律拒绝。控制器保留 HANDLE/view，游戏只打开已有对象。诊断初始化仅在现有真实 Update 的 Prepare 入口执行，须验证固定 fresh run 的程序集、游戏及配置宿主路径、无 reparse point、编译标记、显式授权、构建哈希，以及当前游戏与控制器的实际 PID/creation/path/user/session。主线程 OS TID 必须等于真实采样 TID；诊断不保留或跨线程访问 Unity wrapper。

页整数均为 little-endian UInt64，原子槽解释为非负 Int64，且按八字节对齐：

| 字节偏移 | 内容 |
| --- | --- |
| 0–127 | 16 words：magic `MCEXD003`、version 3、size 4096、header size 512、game PID/creation、controller PID/creation、Windows session、两个正 Int64 随机实例 nonce、session capacity 2、四个保留零 |
| 128–167 | 编译 SHA 的 40 字节 ASCII |
| 168–247 | runId，80 字节，尾部零填充 |
| 248 / 312 / 376 / 440 | 各 64 字节 ASCII SHA256：宿主 EXE、Mod DLL、Mod manifest、游戏 EXE |
| 504–511 | 保留零 |
| 512 / 520 / 528 / 536 | 全局序号计数、创建会话数、真实主线程 OS TID、首次原生退出 OS TID |
| 544–695 | 19 个全局阶段槽，见 `ExitGlobalStage` 顺序 |
| 768 / 1280 起 | 两个 512 字节会话块；首四 words 为会话编号、实际客户端 PID/creation、输入 OS TID，随后 16 阶段槽，见 `ExitSessionStage` 顺序 |

诊断在游戏退出路径只对预打开指针执行 `Interlocked`，不申请文件、不查进程/窗口、不分配日志内容、不调用 Unity API、不等待 ACK。每个槽记录该阶段第一次出现的全局序号；零表示尚未记录，同阶段重复不覆盖第一次记录。并发竞争可留下序号空隙，不用计数差推断丢事件。两代会话分别记录 Prepared/ClientBound、Notify 的 stopping/faulted 返回、Stop、入队、worker dequeue、写入开始/完成、ACK 校验、失败、取消和日志异常。第一代退休 Cancel 不能用于判断第二代 Notify。

既有 OnApplicationQuit/OnDestroy 仅加入口/正常返回标记；Dispose 另记幂等返回，launcher 另记已停止或会话不存在。原业务异常、日志异常、队列和 13 秒取消语义保持原样；诊断不吞掉原本会传播的业务异常。诊断初始化失败会留下缺少 Attached 的页，外部必须按诊断不完整处理，不能据此声称回调未进入。

控制器在原收尾完成后写 schemaVersion 3 的 `control-exit-diagnostic-result.json`，包含实际保留游戏退出状态、映射名、不可变头哈希、整页 hex、`nativeExitThreadId` 和按代次解析的阶段。授权 sidecar 仍为原七字段 schemaVersion 1，通过编译 SHA 和 manifest 哈希绑定具体版本。`captureComplete` 要求游戏已退出、Attached、两代绑定与控制器观察 PID/creation 对齐；`productExitVerified` 始终 false。仍存活时的页只是瞬时诊断，不用于完整时序结论。主报告也记录 `exitDiagnosticOnly`，原失败断言保持不变。

本地验证覆盖锁 .NET6 的头身份/边界与真实 Interlocked 内存竞争、两代隔离，以及准备器显式配对负例。它们不证明 Windows 命名映射 ACL、Unity 回调分派或退出通知已实机通过；这些需要下一次独立诊断运行。
