# 旧 Mod → Flutter 更新器实机探针

本工具验证已安装的 **Mod 1.3.1 原始 DLL** 在独立游戏副本中，复制并启动已验收的 Flutter 更新器探针，随后取消并正常退出。它不执行安装，不替换源游戏中的任何文件。

## 输入和边界

- 使用完整游戏目录及原有 BepInEx。脚本通过 PE metadata 读取准确类型的 `PluginVersion` 常量，不加载目标 DLL；PE 的 FileVersion 不代表 Mod 版本。
- 固定使用提交 `283bd56cd10564d64169a8ea521f9fdffe0019b4` 的五文件探针包；bootstrap SHA-256 为 `18c3a381e6f363150209605f65da553680e1e13b250a50eb90c6298d7438357b`。这是已经单独验收的只读包，后续构建出的同名 EXE 不能替代它。
- 源游戏、探针包和新工作目录必须互不包含；拒绝符号链接、junction、越界启动配置、已存在的工作目录及不足的磁盘空间。准备失败保留现场，不能继续运行。
- 源游戏应处于关闭状态。准备前后比较源文件快照；游戏副本排除本插件配置和整个 `BepInEx/config/MystiaStewardCompanion/` 状态目录，生成新的 loopback 端口、随机令牌，并关闭伴随客户端自动启动与更新自动检查。
- 测试缓存中的 `1.3.2-preview.1` **仅用于通过旧版本的更新候选判断，不是正式版本或发布声明**。staging 保留原插件文件，仅将 staging 中的 updater 换为固定探针。此项不证明 GitHub 更新发现、下载或真实安装/回滚。
- 游戏副本不隔离 Steam、存档或 Steam Cloud。只启动到可用 API，不执行读档、保存或营业操作；游戏启动本身仍可能触发云同步。若 Steam 将启动转交给另一个进程，脚本按原始 PID 和绝对路径拒绝继续，不能通过进程名猜测后继。
- Steam 安装使用 `-SteamAppManifestPath` 指定真实安装清单。Prepare 核对 App ID `1584090`、安装目录及 build ID，只在新副本创建开发用 `steam_appid.txt`，记录清单和文件哈希。已有的正确 App ID 文件逐字保留；错误文件或缺少清单的已有文件直接拒绝。消费者在启动前再次核对开发文件。
- 该开发文件采用 [Steam 官方测试方式](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_RestartAppIfNecessary)，避免副本被重启到 Steam 库中的原安装。客户端、相同用户/权限上下文及账号许可仍由 [Steam 初始化](https://partner.steamgames.com/doc/api/steam_api#SteamAPI_Init)验证；此文件不进入产品发布包。

## 准备和采集

使用根 `toolchain.lock.json` 固定的 PowerShell 7.6.4。以下路径仅为例子，工作目录的父目录必须已存在：

```powershell
./Prepare-OldMod-Probe.ps1 `
  -SourceGameDirectory 'E:/SteamLibrary/steamapps/common/Touhou Mystia Izakaya' `
  -ProbeDirectory 'D:/probe/accepted-283bd56' `
  -WorkspaceDirectory 'D:/probe/old-mod-01' -Port 32155 `
  -SteamAppManifestPath 'E:/SteamLibrary/steamapps/appmanifest_1584090.acf'

./Invoke-OldMod-Probe.ps1 -Workspace 'D:/probe/old-mod-01'
```

准备结束且 `probe-workspace.json` 已发布后才可采集。采集器启动副本并核对监听端口归属、游戏路径、健康信息和 Mod 版本；仅调用一次 `/updates/install-on-exit`。未知安装请求结果不会重试。随后核对旧 Mod 创建的 runner 路径、文件哈希、父进程和完整 CLI 参数，确认 `waiting` 状态，再等待“结束探针”完成。

成功必须同时具备：bootstrap 退出码 0、状态 `cancelled`/进度 0、旧 Mod 清空安装 PID、插件完整快照不变、backup 目录不存在，以及结果采集时原游戏进程仍存活。`waiting-observed.json` 只是中间证据。失败后不要重用工作目录；保留证据并使用新目录重试。

采集器只读取健康和更新状态，不自动重试安装或结束游戏。报告不包含令牌，不应上传配置文件。人工运行后可自行关闭本次游戏副本。

## SSH 物理桌面执行

`node-driver.rs` 和 `Node-OldMod-Probe.ps1` 适配已配置的 Windows 节点协议。它们固定使用 `D:/dev/mystia-node` 与锁定 PowerShell，不注册或修改计划任务，也不在 SSH 的 Session 0 中直接启动 GUI。

[普通构建工作流](../../.github/workflows/flutter-old-mod-probe.yml) 从明确提交构建 driver；完整 Git SHA 编译进 EXE，并写入包内 `build-evidence.json`。这是测试 artifact，不是产品 Release。将完整 payload 放到新建的 `runs/<runId>/payload/`；准备脚本必须在同一 run 下创建 `workspace/`，固定的历史探针包放在独立的兄弟目录。

节点 `request.json` 使用既有协议：

```json
{"schemaVersion":1,"runId":"old-mod-01","action":"run-payload","payloadExe":"old-mod-node-driver.exe","suite":"all","timeoutSeconds":600}
```

同一 run 下另写 `old-mod-probe-input.json`，其中 `gitSha` 必须与 driver 编译身份一致：

```json
{"schemaVersion":1,"runId":"old-mod-01","gitSha":"<完整40位提交SHA>","workspace":"D:/dev/mystia-node/runs/old-mod-01/workspace"}
```

依照节点接入说明，先执行 `session-check`，再通过已有 `Invoke-MystiaNode.ps1 -Action run-payload -RunId <runId>` 提交。不能把任务层的 `READY` 或 `COMPLETED` 当作业务通过。

adapter 等待采集器确认旧 Mod 已启动 bootstrap，然后按父子 PID、路径、固定 UI 文件哈希找到 Flutter 窗口，仅对唯一启用的“结束探针”按钮调用一次 UI Automation `InvokePattern`。不使用坐标、标题猜测或自动游戏操作。该结果标记为物理桌面自动测试，不冒充人工视觉检查。

保留 `result.json`、`session.json`（若节点输出）、`probe-result.json`、`node-uia-*.json`、`workspace/probe-workspace.json`、`workspace/evidence/` 和副本中的 `install-status.json`。业务报告哈希和 run ID 必须一致。源快照只作审计，无需上传游戏文件或配置令牌。

既有节点 worker 使用 Job Object，在 payload 返回时清理本次进程树，包括本次游戏副本。因此报告中的 `gameStillRunning` 是清理前的业务断言，不表示任务结束后游戏仍在运行。探针不按进程名结束其他游戏。

## 验证范围

```powershell
./Test-Prepare-OldMod-Probe.ps1 -ProbeDirectory '<已验收的五文件探针包>'
./Test-Invoke-OldMod-Probe.ps1
```

Prepare 测试使用固定包、真实临时目录和合成 metadata DLL，不启动游戏。Invoke 测试使用 mock 进程/网络与真实 fixture 文件。driver 测试覆盖固定参数、路径和真实子进程退出传播。普通 Windows CI 验证可独立运行的测试并构建 payload；固定历史包的准备验证与实际游戏链路另行执行，不能由 mock 或 CI 构建结果替代。

Defender/SmartScreen、无开发工具干净机、窗口穿透、安装事务及 Android 能力均不是本项通过的含义。
