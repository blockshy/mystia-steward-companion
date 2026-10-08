# Flutter Windows 窗口探针

此工程验证迁移中的 Windows 窗口与输入能力。`all` 套件先启动 Flutter 窗口和另一个进程中的受控 Win32 底窗，测量真实桌面像素、鼠标与键盘输入；随后验证单实例控制、Shell 托盘和退出，不启动游戏。独立的 `xinput` 套件读取手柄，`focus` 套件启动原 Mod 与合作插件副本，`hotkey` 套件验证新 Mod 的 F8/RS 控制链。各套件分别运行、分别判定，均不执行更新安装。

工具版本来自根 [toolchain.lock.json](../../toolchain.lock.json)。与更新器的 [Flutter 平台探针](../flutter-platform-probe/README.md) 分开构建，避免窗口实验影响已经验收的启动与取消链路。

## 构建

在仓库根目录，使用锁定 Node 和显式 Flutter SDK：

```bash
node scripts/run-flutter-window-probe.mjs --sdk-root temp/toolchains/flutter-3.47.6
```

入口执行依赖锁校验、Pigeon 重新生成比对、格式、静态分析与 Dart 测试。Linux 测试只验证模型和 Widget，不证明 Windows 合成或输入行为。

在具有 Visual Studio C++ 桌面组件的 Windows 环境中，使用锁定 PowerShell：

```powershell
./scripts/build-flutter-window-probe.ps1 -SdkRoot C:/toolchains/flutter-3.47.6 -OutputDirectory C:/window-probe-output
```

输出目录必须不存在。发布目录随附 Flutter 运行资源、VC runtime 和 `build-evidence.json`；源码提交同时编入 Dart 与原生程序。构建后以固定 `--loader-check <提交SHA>` 无界面入口检查完整包能否通过 Windows DLL 加载；manifest 显式声明托盘图标所需 Common Controls v6。该检查不证明 GUI 行为。普通 [Actions 工作流](../../.github/workflows/flutter-window-probe.yml) 生成测试 artifact，不创建标签或 Release。

## 物理桌面执行

GUI 通过已配置 Windows 节点的交互式任务运行。SSH Session 0 仅用于上传和查询；先用节点的 `session-check` 确认已登录、解锁的本地桌面。每次创建新的运行 ID，在节点 `runs/<runId>/payload/` 放置完整发布目录。

节点请求示例：

```json
{
  "schemaVersion": 1,
  "runId": "flutter-window-20261007-01",
  "action": "run-payload",
  "payloadExe": "mystia-steward-companion-window-probe.exe",
  "suite": "all",
  "timeoutSeconds": 600
}
```

节点入口为 `D:/dev/mystia-node/runner/Invoke-MystiaNode.ps1`，以 `-Action run-payload -RunId <runId>` 调度。探针只接受以下固定参数与结果位置：

```text
--probe --run-id <runId> --suite all --result-file D:/dev/mystia-node/runs/<runId>/probe-result.json
```

探针自行创建并绑定受控进程，不能传入任意游戏 PID、HWND 或输入坐标。输入只落在本次受控窗口、按 GUID 和唯一可访问名称确认的 Shell 图标及其实际菜单；身份、遮挡或按键状态不确定时终止相关操作。结果采用首次创建写入，已有结果不能覆写重用。节点最终关闭本次进程树；保留运行目录中的请求、会话、节点结果、业务报告和日志。

## 证据范围

| 能力 | 实际测量 |
| --- | --- |
| 背景、内容透明度独立 | 背景与内容 alpha 各取 0、128、255，在黑白两种底色上采样屏幕像素，与预期合成颜色比较 |
| 点击穿透与恢复 | 同一物理点发送带本次标记的真实 `SendInput`，同时检查 Flutter 与另进程底窗收到的 down/up，覆盖交互→穿透→交互 |
| 隐藏与关闭恢复 | 原生隐藏、`WM_CLOSE` 隐藏，再显示时解除穿透并恢复可操作状态 |
| 精确焦点 | 记录实际前台 HWND/PID、键盘焦点与双方收到的真实 F24 输入，同时核对 Flutter 事件 |
| 置顶 | 样式回读与两个受控窗口实际叠放/像素证据共同判定 |
| F10 恢复 | 注册本次热键后发送真实 F10；被其他程序占用时保留失败信息，不覆盖注册 |
| 环境 | 记录实际窗口 DPI、awareness 与监视器；节点会话报告确认物理桌面和权限 |
| 单实例 | 第二进程实际独占绑定失败，核对 loopback 监听者 PID 后发送旧格式 show/toggle/exit；主进程实际应用计数、窗口焦点、原生与 Dart 输入共同证明转交 |
| 托盘 | 实际注册 version 4 图标，经 Shell GUID 矩形、唯一 tooltip、UIA 点命中确认后真实点击；右键显示/穿透/退出使用实际弹出菜单的 HWND、HMENU 与项目矩形 |
| 退出与重建 | 分别由托盘和第二进程请求退出；保留句柄观察 exit 0，确认图标删除及监听端口释放，再以新 PID 在同一隔离端口重建 |

状态修改、Dart 一帧完成和 `DwmFlush` 都不能独自证明最终屏幕呈现。UIA Invoke 或向窗口直接发送鼠标消息不能替代穿透测试。像素仅采自本次受控区域，不保存全桌面截图。

系统输入使用独立随机的非零 31 位标记，接收端比较完整值；控制通信另用 63 位 nonce。该划分避免已测 Windows 输入链截去高 32 位造成误判，同时避开 Flutter 的触摸/笔消息签名。发送数量、原始消息与完整 `extraInfo` 都保留在诊断中，不能仅依据计数超时推断输入没有到达。

键盘注入期间另保留有界的自有窗口消息诊断，分别在 `TranslateMessage` 前和窗口过程记录键码、扫描码、完整标记与只读输入法状态。`VK_PROCESSKEY` 的原始键码须在消息转换前读取，不能把输入法处理过的键直接等同于 Flutter 普通键事件。诊断不采集字符、候选词或组合文本，也不切换系统输入法。

焦点场景固定使用非文本键 F24（Win32 `VK_F24=135` / Flutter `LogicalKeyboardKey.f24`），要求发送、完整标记、窗口身份及双端计数同时符合预期。实测中文输入法会将 A 转为 `VK_PROCESSKEY`，Flutter 按其输入法路径处理；本探针不把普通 A 事件作为所有输入法环境的共同前提。中文组合输入、文本框与输入法候选交互仍需独立验收，不能由 F24 通过替代。

受控进程通过 `AllowSetForegroundWindow` 协作授权焦点切换，报告记录调用结果，再以实际焦点和输入验收。跨输入队列的前台切换只发起一次，由定时观察实际 HWND/PID 后发布交接确认；调用刚返回不能视为已完成，等待期间不继续业务输入。这不证明真实游戏在所有前台限制下都能被聚焦。收尾仅在鼠标仍位于最后注入点时恢复原位置，不抢回其他应用的前台。

单实例探针使用系统分配的独立 loopback 端口，排除产品的 32145/32146；第二代沿用同一测试端口。旧协议是原始 TCP 文本：`mystia-steward-companion:show\n`、`toggle\n`、`exit\n`（三者均带完整产品前缀），附加 `--api=`、`--token=`、`--game-pid=`，没有应用层 ACK。探针只使用合成连接身份，第二进程退出 0 不代表主进程已执行；另读取受控主实例的实际应用结果。分段 show 必须等 EOF 才应用，无效消息不得部分修改身份。真实游戏的 toggle 防抖和前台切换策略仍单独验收。

Shell 图标未展开时，仅对已识别且唯一的隐藏图标按钮核验 Shell 进程、UIA 身份、可见性和实际点命中，再真实点击一次。后续只读轮询等待本次 GUID/tooltip/点命中一致后，完成原先请求的图标点击；等待期间不接受其他业务命令，超时或取消均不重放点击。图标仍不可见、overflow 控件身份未知或 Explorer 更换时记录 `BLOCKED`；不猜坐标，不改固定图标设置，不重启 Explorer。`TaskbarCreated` 恢复逻辑存在不等于已验证真实 Explorer 重启。UIA 只读身份检查不替代实际 Shell 点击。

`probe-result.json` 的 `PASS` 要求 26 项核心窗口检查及 11 项生命周期检查完整、唯一且全部通过，只代表报告列出的场景。测试中的 core-only 模式明确标注为模型测试，生产入口不能退回只跑核心检查。`FAIL` 表示取得了不符合预期的行为，`BLOCKED` 表示缺少条件；`p0Verified` 始终为 `false`。节点的 `READY`、`COMPLETED` 和构建成功各有独立含义，不能代替业务判定。

普通用户权限、干净机、全部 DPI 倍率/跨屏和 Android 尚需独立验收。各套件只有取得对应本次实机报告后才登记通过。此工程不会注册全局 F8：现有 F8 来源包含 Unity 输入和客户端键盘事件，不能误当作既有全局 Win32 热键。Defender 专项按用户决定暂缓。

## 独立手柄套件

新运行请求将 `suite` 改为 `xinput`；参数和结果位置仍由节点固定。保持探针窗口前台，按窗口提示松开全部控件、按下右摇杆键（RS）至少一秒、松开，再按下和松开一次。总等待上限 240 秒；没有手柄、多个连接槽位或未完成操作不能报告通过。

原生层仅加载系统 `XInput1_4.dll`，每次返回四个槽位的实际 `XInputGetState` 返回码及原始状态；不安装虚拟设备、不发送震动。Dart 检查中立门槛、单次长按只产生一个边沿以及释放后重新触发，失焦、断连或采样间隙超过 250 ms 时重新建立证明。报告必须包含完整六项检查。逻辑槽位不能证明设备的物理来源，真实操作由用户配合；不据此声称已测拔插恢复。

探针使用左右摇杆 7849/8689、扳机 30 的中立阈值；该阈值仅服务本次输入观测，不代表旧产品领域算法已迁移。焦点外没有语义动作派发。本套件不调用现有 Mod 的 RS/F8 切换链路。

## 独立游戏焦点套件

本套件通过独立的 [BepInEx 合作插件](../flutter-focus-cooperator/PROTOCOL.md)验证游戏进程显式授权。原 Mod 1.3.1 DLL 保持原样，不接入产品 F8/RS。无合作授权的旧探针曾观察到后台激活被拒绝，原 BLOCKED 证据保留；当前入口要求合作插件，不提供隐式降级。

Flutter Windows bundle 与合作 DLL 必须从同一个干净提交构建。合作 DLL 使用锁定 .NET SDK 10 编译 net6.0，依赖本地已校验的私有 References；引用不进入交付包。仓库根目录执行：

```bash
node scripts/build-flutter-focus-cooperator.mjs --dotnet "$PWD/temp/toolchains/dotnet-sdk-10.0.110/dotnet" --output "$PWD/temp/focus-cooperator-bundle"
corepack pnpm test:dotnet6 flutter-focus-cooperator
```

输出必须不存在。两文件包包含 DLL 与独立 `build-evidence.json`，记录工具锁、References 锁、提交、源码字节和 DLL 哈希。将完整包放入新运行的 `cooperator/`，先从可信构建结果核对 manifest SHA-256，再以锁定 PowerShell 执行准备脚本，最后发布 `suite: "focus"` 的节点请求：

```powershell
& D:/dev/mystia-node/tools/powershell-7.6.4/pwsh.exe -NoProfile -File D:/dev/mystia-node/runs/<runId>/payload/support/tests/flutter-window-probe/Prepare-InputFocus-Probe.ps1 -RunDirectory D:/dev/mystia-node/runs/<runId> -RunId <runId> -GitSha <完整提交SHA> -SourceGameDirectory '<原游戏目录>' -SteamAppManifestPath '<appmanifest_1584090.acf>' -Port 32755 -CooperatorBundleDirectory D:/dev/mystia-node/runs/<runId>/cooperator -CooperatorEvidenceSha256 <合作包manifest的SHA256>
```

准备脚本核对固定游戏、原始 Mod 1.3.1、BepInEx 二进制及 Steam app 1584090/build 23158340，完整复制到本次 `workspace/game`，前后比较源文件快照。副本关闭客户端自动启动及更新检查，使用独立 API 端口和随机 token；脚本和探针均不发送 Mod API 请求。不得采集或上传含 token 的配置。已有 workspace、结果或目录重叠时拒绝重用；失败保留现场。游戏副本不隔离 Steam Cloud 或用户存档，因此不执行读档、菜单导航或游戏业务输入。

原生层核对固定 sidecar、准备证据和副本哈希，挂起创建游戏，绑定进程句柄、PID、创建时间、完整路径、会话与节点 Job 后才恢复运行。只接受该 PID 唯一可见的 `UnityWndClass` 窗口；身份不明、Steam 重定向至别的进程或窗口更换时停止，不追认其他进程。窗口出现仅证明启动窗口存在，不代表游戏业务场景已就绪。

sidecar schema 2 同时绑定合作 DLL 与构建证据；插件只复制到新游戏副本，并在运行时核对程序集编入的 SHA。原生与游戏插件通过本机、当前用户、单连接 Named Pipe 交叉核验 OS 对端 PID 及保留身份。三次从游戏切回 Flutter 均使用新的序号，由游戏侧先确认自身是精确前台，再调用一次 `AllowSetForegroundWindow`；客户端只接受本次 ACK，重新检查桌面和前台后才请求一次切换。授权返回 true 仍不能替代真实前台检查，失效、拒绝或超时不重放。

插件在同步 `Load` 中注册独立心跳组件，`Update` 只发布不可变托管快照。ready 等待首次真实帧回调，每轮请求和 EOF 各等待接收之后的新回调，再执行原有身份与前台核验；有界等待超时即失败。游戏证据 schema 2 记录序号、单调时钟和操作系统线程，原生核对其连续推进及与绑定游戏窗口线程一致。它证明测试组件获得帧回调，不证明游戏业务就绪，也不以固定等待时长替代该证据。

八项检查覆盖游戏与 Flutter 实际前台交接、保留可见、隐藏后恢复、穿透后恢复，以及保留进程句柄确认游戏正常退出 0。每次切换只请求一次，再观察精确 HWND/PID/线程焦点；API 返回值不能独自证明成功。标记鼠标和 F24 输入仅发往本次 Flutter 窗口，并核对原生与 Dart 计数，游戏不接收注入按键或点击。结束时只向绑定的游戏窗口发送一次 `WM_CLOSE`；节点 Job 清理不能代替正常退出证据。

`xinput`/`focus` 使用独立 Pigeon 定义与结果契约，分别要求六项/八项完整唯一检查。`PASS` 退出 0、`FAIL` 退出 1、`BLOCKED` 退出 2；报告首次创建、上限 1 MiB，`p0Verified` 仍为 `false`。失败后的游戏清理另存 `native-cleanup.json`，保留是否实际退出的信息。以上检查不替代旧 Mod F8/RS 的真实业务切换、产品防抖策略或完整 P0 验收。

正常收尾先单次归还游戏前台，观察精确 HWND/PID 和线程焦点，再断开合作管道、保存游戏侧授权证据，最后通过 `SendMessageCallbackW` 向绑定游戏发送唯一一次 `WM_CLOSE`；实际发送前再次核验前台条件。诊断记录发送时的真实前台和焦点、发送结果及窗口过程完成回调，正常退出仍以保留句柄观察到 exit 0 为准。回调只证明窗口过程返回，不代表同意退出，也不作为游戏生命周期就绪信号；窗口过程内直接退出时可能没有回调。先前 `PostMessageW` 的后台及前台关闭均出现超时，原失败记录保留；消息入队成功不能证明游戏已经处理，恢复前台也未单独解决问题。验收联合检查 Flutter 报告、游戏侧三次授权记录、节点和清理结果；任何单份 PASS 都不能替代其余证据。具体帧、身份和完成回执约定见合作协议。

唯一关闭请求成功后，探针进入保留句柄的退出观察阶段，并拒绝新的焦点或输入动作。此时仍核对 PID/创建时间及进程等待状态，只有句柄已触发且退出码为 0 才通过；不再要求正在销毁的映像路径、窗口或 GUI 线程可查询。游戏仍活时 `gameWindowCountKnown=false` 表示该阶段没有枚举窗口，不能将占位计数 0 解释为窗口消失。关闭发送前的完整存活身份校验保持不变。依据为 Windows 的[进程终止与保留句柄规则](https://learn.microsoft.com/en-us/windows/win32/procthread/terminating-a-process)。

准备脚本测试使用临时合成文件和测试范围内的固定身份替换，验证路径、复制、哈希、端口、合作包篡改与失败边界；它不启动游戏，不计作实机证据。Windows 构建会执行该测试，并将准备脚本、公共模块和锁文件纳入包哈希清单。

## 真实 Mod 控制套件

`hotkey` 使用 [MSC1 身份控制协议](../../mods/bepinex/src/Plugin/CompanionControl/PROTOCOL.md)，游戏内入口是产品 `StewardOverlayController.Update`。此副本不装入独立 focus 合作插件。新 Mod 默认 `LegacyTcp`，仅准备脚本在本次副本显式设置 `IdentityPipeV1`、绝对客户端路径、`AutoLaunch=false`。原游戏的 Mod 1.3.1 及配置保持原样。

新 Mod 与 Windows bundle 必须来自同一个干净提交。锁定 SDK 10 编译 net6.0，并在程序集 metadata 中绑定完整 SHA：

```bash
node scripts/build-flutter-control-mod.mjs --dotnet "$PWD/temp/toolchains/dotnet-sdk-10.0.110/dotnet" --output "$PWD/temp/control-mod-bundle"
corepack pnpm test:dotnet6 companion-control
```

新目录只包含 `MystiaStewardCompanion.BepInEx.dll` 和 `build-evidence.json`。核验 manifest、源码字节、锁及 DLL metadata 后上传至新运行的 `control-mod/`，Windows bundle 上传至 `payload/`。使用锁定 PowerShell 准备副本：

```powershell
& D:/dev/mystia-node/tools/powershell-7.6.4/pwsh.exe -NoProfile -File D:/dev/mystia-node/runs/<runId>/payload/support/tests/flutter-window-probe/Prepare-Control-Probe.ps1 -RunDirectory D:/dev/mystia-node/runs/<runId> -RunId <runId> -GitSha <完整提交SHA> -SourceGameDirectory '<原游戏目录>' -SteamAppManifestPath '<appmanifest_1584090.acf>' -Port 32755 -ModBundleDirectory D:/dev/mystia-node/runs/<runId>/control-mod -ModEvidenceSha256 <Mod包manifest的SHA256>
```

准备脚本核验七个固定文件、Steam 版本和完整源目录前后快照，唯一替换路径为副本中既有的 Mod DLL。控制端口 32146 任意 IPv4/IPv6 地址存在监听者时停止，不关闭其他客户端。API 端口固定 32755，随机 token 配置不纳入采集。`control-probe.json` 绑定原生代码核对的精确 14 项身份字段、准备证据和新 Mod 构建证据；节点请求使用 `suite: "hotkey"`。准备不启动游戏，实际 GUI 仍只经既有交互任务。

自动阶段验证真实 Mod F8 唤起、Flutter 原生与 Dart 输入、F8 返回游戏保持可见、隐藏恢复和穿透恢复。F8 down/up 分开：只在保留身份对应的前台发送一次 down，等实际目标切换后才发送一次 up；未知结果不重放。客户端 F8 必须同时对应新的 Dart KeyDown 和原生队列中的按下边沿，重复事件不派发。

手动阶段，游戏获得焦点后按住 RS，Flutter 出现后继续保持，看到“松开”提示再释放；恢复中立后再次按住 RS，游戏获得焦点后继续保持至少两秒再释放。两个方向均采集至少一秒无反弹证据，并要求采样间隔不超过 250 ms；多个/无手柄、失焦、断开或不完整操作不算通过。逻辑槽位不证明设备物理来源，不宣称已验证拔插。结束时唯一发送 `WM_CLOSE`，实际游戏 exit 0、平衡自有注入按键、节点退出和源文件不变分别验证。

单次焦点请求已经提交后，[GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow) 可在窗口失活过程中暂时返回 NULL。此时只继续既有期限内的只读观察，并记录空前台次数与时刻；不发送新输入、不重发请求，也不将空值当作成功。非空且不属于绑定双方的窗口仍立即终止交接；期限内未取得精确目标前台与线程焦点仍失败。提交请求前的完整身份与前台核验保持不变。

报告固定十项检查，要求完整、唯一且全部 PASS；`p0Verified=false`。`native-cleanup.json` 保存原生独立门槛和收尾证据，不能以报告字符串代替实际原生计数/句柄观察。失败报告和现场不覆盖。首发进程拓扑、未修改的旧 Mod、用户点击激活、普通用户权限、完整客户端页面与整体 P0 仍各自验收。

准备脚本的合成 fixture 与 .NET 6 纯托管测试只能证明相应失败边界；真实 Unity 输入、OS 前台权限及释放门槛必须由同提交实机记录确认。

## 平台依据

### 首次启动与客户端换代

同一准备命令增加 `-Lifecycle` 时，创建新的 `control-lifecycle.json` 授权并仅在本次副本启用 `AutoLaunch=true`。节点仍使用 `hotkey`；原生控制进程先校验全部游戏/Mod 文件、准备证据、配置与 Flutter 程序，再启动游戏。游戏真实 Mod 启动 Flutter，原生适配核验父进程、PID/创建时间、实际参数及 token 的哈希；Dart 只收到脱敏的运行编号和代次。

第一代完成注册、AutoLaunch 激活、原生/Dart 输入及 F8 归还后正常退出，保留游戏。控制进程观察保留客户端句柄 exit0、Mod 已建立新会话且无监听者后，只发送一次 F8 down/up；第二代必须由真实 Mod 启动并完成来源为 F8 的注册/激活，随后验证退出通知与游戏正常退出。没有用固定等待假定 Unity 就绪，注入返回成功也不代表游戏消费成功。

每代各有六项独立结果和原生清理文件；控制进程复核结果、保留进程身份和文件哈希，写出 `kind=flutter-control-lifecycle` 的总报告。该报告与原十项 F8/RS 套件分别验收，`p0Verified=false`。失败证据保留；首次启动、新客户端换代、退出帧须实际通过后才能登记完成。

- [DWM 扩展客户区](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmextendframeintoclientarea)和 [DWM alpha 合成](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmenableblurbehindwindow)：API 成功之后仍以屏幕像素为准。
- [Layered window 输入规则](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features)与 [WM_NCHITTEST](https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest)：同线程命中返回不能证明跨进程穿透。
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)与 [GetGUIThreadInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguithreadinfo)：核对发送数量、接收标记与实际焦点。
- [ImmGetVirtualKey](https://learn.microsoft.com/en-us/windows/win32/api/imm/nf-imm-immgetvirtualkey)：在 `TranslateMessage` 前取得输入法接管的原始虚拟键码。
- [Shell 图标矩形](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicongetrect)和 [通知图标](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyiconw)：注册/删除返回值之外，另核验图标与实际输入。
- [独占端口绑定](https://learn.microsoft.com/en-us/windows/win32/winsock/using-so-reuseaddr-and-so-exclusiveaddruse)和 [TCP 监听者身份](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)：绑定错误码可能为 10013 或 10048，错误码本身不能证明端口属于目标实例。
- [跨输入队列的前台切换](https://devblogs.microsoft.com/oldnewthing/20161118-00/?p=94745)：窗口线程需处理异步激活通知，调用返回时窗口未必已经成为前台。
- [异步消息完成回调](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagecallbackw)：仅补充窗口过程返回证据，不能代替进程退出检查。
