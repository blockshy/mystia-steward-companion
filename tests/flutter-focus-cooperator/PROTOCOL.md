# P0 游戏前台合作协议

本插件只随 `focus` 探针装入本次新游戏副本，不替换原 Mod，不接入产品 F8/RS、游戏业务对象、业务 Hook 或 Mod API。它通过独立测试组件记录 Unity `Update` 进展，并验证游戏进程显式授权精确 Flutter 进程后，操作系统焦点是否实际转移。主循环进展不等于菜单、存档或业务就绪。

## 固定身份

原生探针先创建本机、当前用户、单连接的 overlapped named pipe，再挂起创建本次游戏。绑定 HANDLE/PID/创建时间/完整路径/会话后，以 CREATE_NEW 写 `run/foreground-session.json`，随后恢复游戏。

描述符只允许以下字段，除 `schemaVersion: 1` 外均为 ASCII 字符串：`runId`、`gitSha`、`pipeName`、`nonceHex`、`gamePid`、`gameCreationHex`、`probePid`、`probeCreationHex`、`probeHwnd`、`probeThreadId`、`probeExeSha256`。nonce 为 32 位小写十六进制，前 16 位表示 nonceLo 的数值，后 16 位表示 nonceHi 的数值；不是主机字节序的 hex dump。创建时间为 Win32 FILETIME 无符号整数的小写十六进制；PID/HWND/TID 为十进制。

固定 run 根为 `D:/dev/mystia-node/runs/<runId>`；游戏为 `workspace/game/Touhou Mystia Izakaya.exe`，插件为 `workspace/game/BepInEx/plugins/mystia-steward-companion-focus-probe/MystiaStewardCompanion.FocusProbe.dll`，Flutter 为 `payload/mystia-steward-companion-window-probe.exe`。插件从自己的程序集路径推导根路径，核对实际游戏映像及描述符。插件编译的 AssemblyMetadata `ProbeGitSha` 必须是完整 40 位小写 SHA，构建时必填。

此路径判断适用于锁定 BepInEx #783：其 [BaseChainloader](https://github.com/BepInEx/BepInEx/blob/c58c42d/BepInEx.Core/Bootstrap/BaseChainloader.cs#L391-L395) 使用 `Assembly.LoadFrom(plugin.Location)`；已用锁定 `BepInEx.Core.dll` 反编译交叉核对。[IL2CPPChainloader](https://github.com/BepInEx/BepInEx/blob/c58c42d/Runtimes/Unity/BepInEx.Unity.IL2CPP/IL2CPPChainloader.cs#L123-L132) 随后实例化插件并调用 `Load`。这支持使用程序集 `Location` 的源码判断，最终加载是否成功仍由本次游戏实机报告确认。

插件为 named-pipe client；双方分别用 GetNamedPipeServerProcessId/GetNamedPipeClientProcessId 获取操作系统 peer PID，并持有核验后的进程 HANDLE。对端映像路径、创建时间、会话、存活状态和精确窗口必须符合描述符/保留身份。路径中的 reparse point、额外/重复 JSON 字段及身份变化均拒绝。pipeName 采用不含 `\\.\pipe\` 前缀的本机简单名称，字符限 ASCII 字母/数字/`-`/`_`，必须含本次 runId 和 nonceHex。

## 主线程心跳

`Load()` 先同步核验 Windows x64、程序集固定 run 路径、非 reparse 路径及本进程完整游戏映像，再调用锁定 #783 的 `BasePlugin.AddComponent<FocusHeartbeatBehaviour>()`，丢弃返回 wrapper。组件使用公开 `IntPtr` 构造函数和私有无参 `Update()`；`Update()` 只读取 Win32 OS TID、`Stopwatch.GetTimestamp()`，并以 `Interlocked` 发布不可变托管快照。后台 worker 只读取快照，不持有、访问或销毁 Unity 对象。组件异常或 `OnDestroy` 只将托管状态永久标记为停止，不调用游戏退出、销毁或业务 API。

Load 的 OS TID 固定为 `heartbeatThreadId`，每次 Update 必须相同；ready、每轮授权和 EOF 收尾还须与精确唯一游戏窗口的线程一致。ready 最多等待 100 秒，必须看到真实首次 Update；每轮完整请求到达后及管道 EOF 后分别保留当时快照为 baseline，最多等待 3 秒，只有后续 Update 的序号和单调时钟值均严格增加才继续。ASFW 前重核心跳存活、线程与保留进展，再执行原有身份/前台检查。10 毫秒采样间隔只用于观察条件，等待时长本身不构成通过依据。超时、停止和取消均失败，不重试授权。

注册依据已交叉核对锁定二进制：#783 `BasePlugin.AddComponent<T>` → `IL2CPPChainloader.AddUnityComponent` → `Il2CppUtils.AddComponent` 会先调用 `ClassInjector.RegisterTypeInIl2Cpp`，再将组件加入 BepInEx Manager；`Il2CppInterop.Runtime.dll` 注入器支持 `IntPtr` 构造函数及私有无参 void 方法。现有 Mod 在同步 `Load()` 注册 `StewardOverlayBehaviour`，采用相同的 Unity 回调模式。当前 metadata 的 `GameObject.AddComponent(Type)` token 为 `0x6000AF5`、RVA 为 `0x2C2FF70`，#783 wrapper 通过同一 token 调用；IDA Native 控制流确认为转发 `Internal_AddComponentWithType(System.Type)`。metadata C# 空桩不作为实现依据。该核对只支持主线程添加测试组件，不证明游戏业务状态，也不替代本轮实机 Update 与退出证据。

## 176 字节帧

每帧严格 176 字节，小端序，无文本 terminator。头部依次为四个 uint32：magic `0x4d534647`、version `1`、kind（1 ready / 2 request / 3 reply）、length `176`。其后 20 个 uint64 按顺序为：

| 索引 | 字段 |
| --- | --- |
| 0–5 | nonceLo, nonceHi, gamePid, gameCreation, probePid, probeCreation |
| 6–11 | grantSequence, requestId, gameHwnd, probeHwnd, gameThreadId, probeThreadId |
| 12–15 | foregroundBeforeHwnd, foregroundBeforePid, foregroundAfterHwnd, foregroundAfterPid |
| 16–19 | grantAttempted, grantSucceeded, lastError, reserved0 |

ready 的 grantSequence、requestId 及索引 8–19 全为 0；帧本身不携带窗口字段，但本版发送 ready 前要求首次真实 Update、唯一游戏窗口及线程一致。request 的 grantSequence 从 1 严格连续增长，最多 3 次；requestId 必须严格增加，允许与其他焦点操作共用编号而有间隔。request 的窗口/线程必须精确匹配；索引 12–19 全为 0。reply 回显索引 0–11，填入真实 Win32 前台快照及一次 ASFW 的调用/返回，reserved0 为 0，布尔值只允许 0/1。

请求解析、身份或前台失败时不调用 ASFW、不重发，保留失败证据并断开。调用 ASFW 前最后一次读取的实际前台必须等于请求 gameHwnd 且 PID 为绑定游戏；ASFW 对精确 probePid 仅调用一次。无论返回成功或失败，记录返回与前后台快照；失败回复后断开。前台在授权过程中变化也记录失败，接收者不得继续切换。

原生侧仅在本次完整成功 reply 后调用一次 SetForegroundWindow，随后有界观察真正前台 HWND/PID 及线程焦点；ASFW 返回成功不代表切换成功。授权可能因用户输入或其他授权撤销，失败不得自动重试或以线程 GetFocus/Dart focused 代替前台判定。

## 收尾与证据

插件只有一个后台 Task、一次连接，不重连。管道正常 EOF 结束会话；不足一帧的 EOF、超时及多于 3 个请求均失败。正常收尾先通过单次 `focusGame` 观察精确游戏前台及线程焦点，再进入原生 `closeGame`；该阶段关闭管道，观察完整证据，重新核验游戏仍在前台后才通过 `SendMessageCallbackW` 发送唯一一次 `WM_CLOSE`。回调仅补充窗口过程返回证据，不能代替保留进程句柄观察正常退出。游戏侧以 CREATE_NEW 写 `run/game-foreground-evidence.pending.json`，Flush(true) 并关闭后以不覆盖的 File.Move 原子发布 `run/game-foreground-evidence.json`。证据不含 token。只有三次成功授权且帧边界 EOF 才记合作会话 PASS；这仍不代表 Flutter 实际切换成功或游戏已正常退出，最终由独立 focus 报告裁定。

Win32 断管由 .NET 6 的 [PipeStream 异步读取](https://github.com/dotnet/runtime/blob/v6.0.36/src/libraries/System.IO.Pipes/src/System/IO/Pipes/PipeStream.Windows.cs#L299-L345)及其[完成回调](https://github.com/dotnet/runtime/blob/v6.0.36/src/libraries/System.IO.Pipes/src/System/IO/Pipes/PipeStream.ValueTaskSource.cs#L149-L184)映射为读取结束；本协议只在下一帧尚未收到任何字节时接受它，取消和半帧不会被当作正常完成。

游戏证据 schema 为 2；176 字节管道帧仍为 version 1。顶层固定为以下 27 字段：原有 schemaVersion（2）、kind（`mystia-game-foreground-evidence`）、runId/gitSha/outcome（`PASS` 或 `FAIL`）、gamePid/probePid/pipeServerPid（数字）、gameCreationHex/probeCreationHex/probeExecutableSha256/descriptorSha256（字符串）、readySent/pipeEof（布尔）、successfulGrants（数字）、startedAtUtc/finishedAtUtc（UTC ISO 字符串）、requests（数组）、error（字符串或 null），以及 8 个数值字段 `heartbeatThreadId`、`heartbeatFrequency`、`readyHeartbeatSequence`、`readyHeartbeatTicks`、`eofBaselineSequence`、`eofBaselineTicks`、`eofHeartbeatSequence`、`eofHeartbeatTicks`。

requests 每项固定 7 字段：requestFrameHex（352 位小写 hex）、replyFrameHex（352 位小写 hex 或 null）、error（字符串或 null），以及数值 `baselineSequence`、`baselineTicks`、`heartbeatSequence`、`heartbeatTicks`。序号、ticks、frequency 最大均为 `Int64.MaxValue`；ticks 来自游戏进程的 `Stopwatch`，frequency 为 `Stopwatch.Frequency`。线程 ID 范围为 `1..UInt32.MaxValue`；成功记录的序号、ticks、frequency 均大于零。baseline 的 `0/0` 只用于首次 Update 之前或失败记录，不能通过成功验证。

成功要求 readySent/pipeEof 为 true、successfulGrants 与 requests.length 均为 3、error 均为 null；从 ready 到每轮 baseline/observed、再到 EOF baseline/observed，序号与 ticks 分别非递减，每个 observed 必须严格大于自己的 baseline。`heartbeatThreadId` 必须等于三轮帧中的 gameThreadId。消费者核对这些约束及每一帧与原生侧请求/回复，不能只按文件存在或子串认定 PASS。失败时未取得的数值保持 0，错误保留在顶层或对应请求；不会将未观察到的心跳填成成功值。

依据：[AllowSetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-allowsetforegroundwindow)、[SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)、[GetNamedPipeServerProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid)、[GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)。
