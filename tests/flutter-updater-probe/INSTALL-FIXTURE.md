# P0 完整 bundle 隔离安装

这是独立测试目标，不是可发布的安装器。默认 `mystia-steward-companion-updater` 仍为 v1 只读 hello/cancel；新增 `mystia-steward-companion-updater-install-probe` 必须显式启用 `install-fixture`，分发时重命名为旧 Mod 使用的同名 updater。它仅接受本次新建的 `mystia-steward-companion-install-p0-<32 位随机十六进制>` 根及固定 runner/staging/backups/state 布局，不连接真实 Mod 控制端口、不读取或关闭真实游戏。

当前范围是一次真实 Windows 文件安装闭环。完整 P6 的重启恢复、磁盘耗尽、持锁/杀进程矩阵、句柄固定路径与目录替换防竞态、真实发布来源信任不由这个 fixture 宣称完成。旧 Mod 1.3.1 启动/取消的既有实机证据仍属于默认 v1，不冒称旧 Mod 已执行本次安装事务。

## 组成与约束

- `transaction.rs` 负责完整旧树/暂存树清单验证、等待进程正常退出、旧树移动至独占 backup、staging 移入安装目录和所有 DLL/assets 复核。验证失败保留失败新树并恢复已复核旧树；无法确认恢复时明确失败，不覆盖已有 backup。
- 取消与替换准入直接复用产品 Rust updater 的 `InstallControl`。二者竞争同一把锁；准入前取消不改安装树，准入后 UI 只能观察。默认 v1 `wire.rs` 未增加 start。
- `windows_install_fixture.rs` 保留独立自有 waiter 的 HANDLE/PID/creation，并在存活时绑定精确 runner 映像；等待只观察该句柄正常退出。退出观察不再依赖正在消失的路径。进程不确定或非零退出均不安装。
- Flutter 只发送固定 v2 `hello/start/status/cancel/finish`；C++ 平台桥固定命令集合、顺序与长度，Rust 再校验会话/序号/状态。UI 显示就绪后须明确 Start；测试自动化由显式环境值 `install-fixture-after-ready` 启用。UI 丢失会请求取消；已准入事务由 Rust worker 继续完成或回滚，并等待它发布终态。
- 主客户端输入是同 SHA 的完整 P0 Windows Flutter bundle，Mod 是同 SHA 的真实 Release DLL。主客户端可执行文件映射到未来产品的 `companion/mystia-steward-companion.exe`，但其内容仍是 P0 窗口/输入探针，不能当作业务功能已经迁移。updater 自带第二份完整 Flutter bundle，因此测量包含双 engine/资源实际成本。

## 构建与运行

在锁定 Windows 工具链、干净提交工作区构建；不要用原只读 updater 替代此二进制。

独立 workflow `flutter-updater-install-probe.yml` 同 SHA 构建主客户端和 updater。若该 workflow 可读取既有受限 build-assets App 凭据，还会构建真实 Mod 并实际运行安装；正式 release environment 的凭据不自动向迁移 workflow 开放。缺少时只构建可构建组件，上传 `updater-install-runtime-gate.json` 明确标记 `BLOCKED`，随后用授权本地环境构建的同 SHA Mod 在 Windows 实测，不把 CI 绿色构建称作安装通过，也不扩大 secret 作用域。

```powershell
./scripts/build-flutter-updater-probe.ps1 -SdkRoot D:/toolchains/flutter-3.47.6 `
  -OutputDirectory D:/artifacts/updater-install -InstallFixture
```

把同一提交的 `flutter-window-probe-bundle` 与 `flutter-control-mod-bundle` artifact 准备到独立目录后，在真实桌面或有交互 session 的 Windows runner 运行：

```powershell
./Start-Install-Fixture.ps1 -CompanionBundleDirectory D:/artifacts/window `
  -ModBundleDirectory D:/artifacts/control-mod -FixtureParent D:/dev/mystia-node/fixtures -Automate
```

移除 `-Automate` 可手工点击开始/完成。可选 `-OldPluginDirectory <真实旧插件目录>` 只读取并复制旧树；结束时按完整清单复核来源不变。未提供时使用明确标注的 synthetic 旧树，其体积不代表真实 1.3.1。所有来源中的链接/重解析点、歧义文件名、清单不符或异提交均在安装前拒绝。源文件不在 fixture 中移动或写入。

物理机 SSH 调度使用包内固定 `mystia-updater-install-node-driver.exe`，经既有 `MystiaFlutterProbe` 交互任务运行，suite 为 `all`。将 updater 包放到新 `runs/<runId>/payload`，同 SHA 的两个组件分别放到 `payload/components/window` 与 `payload/components/control-mod`。driver 只接受节点既定参数、固定 run root/result 与锁定 PS 路径，等待 `Node-Install-Fixture.ps1` 完成；该脚本校验自身/driver/安装脚本的构建 hash，独占新 `run/workspace`，并核对实际完整业务报告后才写节点 PASS。若用户指定的 `E:/SteamLibrary/steamapps/common/Touhou Mystia Izakaya/BepInEx/plugins/mystia-steward-companion` 存在，只读复制该旧树测量备份体积。节点退出码零不单独代表安装通过。

脚本实际创建完整 ZIP，再真实解包到 staging。它先启动自有 waiter，验证 ready 中的 PID/creation 与保留的 `Process` 对象吻合；只有 updater 发布 `waiting-game` 后才写入该 fixture 的 release capability，让 waiter 自行 exit0。发布状态是观察条件，25 ms 轮询仅为节拍。脚本不按固定延迟推定游戏或 UI 已就绪。

## 证据与通过门槛

根中保留 `fixture.json`、`package.zip`、新旧 manifest、`install-evidence.json`、`state/install-status.json`、`runtime-modules.json`、`disk-observation.json` 与 `probe-report.json`。只有 bootstrap/UI/waiter 正常退出、完整 installed 与 backup 清单都吻合、staging 已移动、状态为 `succeeded/100`、来源未变时脚本才报告 PASS。`p0Verified` 始终 false；这是一项 P0 子证据。

体积证据包含实际 ZIP 字节、新旧展开字节、内嵌 updater ZIP、updater 展开和外部 bootstrap。逻辑共存峰值按 `ZIP + 旧树/backup + staging/installed + runner bootstrap + 展开 updater UI` 求和；同卷 rename 不同时保留两份旧树或两份新树。`waiting-game` 阶段用 `GetFileInformationByHandleEx` 的 [FileStandardInfo.AllocationSize](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info) 记录文件分配字节，涵盖此时完整 payload 与小型元数据；目录元数据、外部输入 artifact、后续新证据另计，不把总卷空闲变化冒充本进程的精确占用。

五分钟表以实测 ZIP 字节计算最小有效吞吐与 1/5/10/20 Mbps 场景。旧五分钟门槛约束下载，不含随后安装；脚本另记本地解包/安装耗时。计算不证明公网带宽、GitHub Release 可达性或旧 Mod 五分钟下载已经通过。后者必须用明确候选下载来源单独实测；本任务不创建正式 Release。

`runtime-modules.json` 来自真实运行的独占 updater UI 子进程，验证 Flutter 与 VC runtime 从该展开 bundle 加载。装有 VS 的物理机和 hosted CI 即使实际运行成功，也不能称作干净 OS；`cleanOsVerified`/`cleanMachine` 固定 false。真正干净机验证需要独立未装 Flutter/VS/VC 开发组件的 Windows OS，并实际运行所交付包、保留 OS 与模块加载证据。Defender/SmartScreen 按用户要求暂缓。

## 本地验证

```bash
cargo test --locked --offline --manifest-path tests/flutter-updater-probe/Cargo.toml --features install-fixture
cargo check --locked --offline --manifest-path tests/flutter-updater-probe/Cargo.toml --features install-fixture --target x86_64-pc-windows-msvc
node scripts/run-flutter-platform-probe.mjs --sdk-root <锁定 Flutter SDK>
pwsh -NoProfile -File tests/flutter-updater-probe/Test-Install-Fixture.ps1
```

Rust 的 portable process 是明确 mock，只证明事务/取消/回滚与协议断言；Windows target check 只证明类型检查。CI 构建与真正运行分开报告，不用 Linux 测试、构建成功或 loader-only 结果替代 Windows GUI 与安装证据。

## 2026-10-08 Windows 实测

`c80d35cf313933a4f0986bb0b5e7af8ab4f2ea42` 的 `flutter-updater-install-20261008-01` 在 Windows 11 物理机通过。Flutter updater 真实驱动完整安装，bootstrap、UI 和自有 waiter 正常退出；独立复核 23 个新文件、4 个备份文件及原 Mod 1.3.1 来源哈希一致，没有自有进程残留。原来源只读复制，未安装到游戏目录。这台主机已有开发工具，因此本项不是干净 OS 证据。

| 测量 | 字节或耗时 |
| --- | --- |
| 完整更新 ZIP | 25,817,835 B |
| 新安装树（主 Flutter、updater、真实 Mod） | 44,603,355 B |
| 原 1.3.1 插件树 | 13,246,016 B |
| 外部 updater bootstrap | 13,127,168 B |
| updater UI 展开 | 28,839,547 B |
| 逻辑共存峰值 | 125,633,921 B |
| 等待点实际文件分配 | 125,768,808 B |
| ZIP 解包 / updater 展开 | 78 / 358 ms |
| 本地 fixture 总耗时 | 37,324 ms |

该包在 300 秒内完成纯 payload 传输需要约 0.688 Mbps 有效吞吐；1 Mbps 时计算值约 207 秒。此值不包含网络连接、重试、协议开销或最终业务界面增长，不能用来声称旧 Mod 的公网下载已通过。CI `37709442936` 成功构建同提交组件；真实 CI 安装因缺少受限 Mod 构建凭据明确标记 BLOCKED，物理机以上述独立运行补足安装子证据。

原始报告及独立核验位于本地 `temp/flutter-p0/windows-updater-install-20261008-01/`，Windows 同名 run 目录保留完整 fixture。独立核验同时比较再次读取的 installed、backup、原来源清单及实际加载模块，不仅依赖节点退出码。

同提交另在全新 Windows 10 Enterprise LTSC Evaluation 19044 VM 实际验证：官方 ISO 的 SHA256 为 `e4ab2e3535be5748252a8d5d57539a6e59be8d6726345ee10e7afd2cb89fefb5`，系统未安装 VS、Windows SDK、Flutter SDK、全局 VC 运行库或 VMware Tools；执行工具只来自只读 DVD 的锁定 portable PowerShell。updater 完整安装及正常退出通过，实际 engine/VC 模块从自身展开目录加载。该 VM 采用 synthetic 旧树，真实 1.3.1 备份尺寸仍以物理机测量为准。

首次系统登录附加运行主客户端探针时，首个透明像素样本被未绑定窗口遮挡，原始 FAIL 保留。常规重启后在该 VM 完成系统网络发现提示（选择 No），以全新 run 执行原二进制，完整窗口 suite 的 **37 项全部通过**；实际模块哈希与 CI artifact 一致，main exit0，VM 正常关机、无自有进程残留。没有修改探针前台或遮挡守卫，也不追认首次遮挡窗口的具体身份。两轮证据分别位于本地 `temp/flutter-p0/windows-updater-clean-20261008-01/` 和 `temp/flutter-p0/windows-updater-clean-main-20261008-02/`；首次整体验证仍为 FAIL，updater/main 的独立子证据分别记录通过。

这是 Win10 Evaluation 的干净运行依赖证据，不能泛化为干净 Win11、所有 GPU 或完成业务迁移。该官方旧 Evaluation 镜像出现许可过期水印，原样记录，未调整激活/许可状态；Defender 专项仍未验收。准备阶段的调度路径比较失败、WinPE 未识别 LSI Parallel 空盘失败也保留；改用原生 AHCI/SATA 后才完成系统安装，未安装额外开发组件。
