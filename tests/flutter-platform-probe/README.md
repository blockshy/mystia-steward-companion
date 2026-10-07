# Windows Flutter updater 只读探针

本包用于验证单 EXE 展开 Flutter UI、进程通信和取消退出。它会创建独立临时目录，**不会安装更新、连接 Mod、关闭游戏或操作实际插件目录**。不要把它放进真实游戏目录，也不要替换正式 updater。

## 运行

1. 从本次迁移分支的 `Flutter migration probes` Actions 页面下载 `mystia-steward-companion-windows-updater-probe` artifact，解压到普通用户可写目录。
2. 保持 Defender、SmartScreen 和现有系统策略开启。在该目录打开普通权限 PowerShell，执行 `./Start-Probe.ps1`。如果系统策略阻止脚本或程序运行，记录提示并停止，不降低执行策略、不添加防护排除、不移除来源标记。
3. 正常情况下出现标题为 `mystia-steward-companion · 更新探针` 的 Flutter 窗口。窗口应自动完成连接并显示“启动链已连通”。点击“结束探针”，窗口应自动关闭并记录取消结果。
4. 等待脚本显示 `PASS (manual)` 或 `FAIL (manual)`。窗口正常出现、关闭并不代表通过；脚本还会核对退出码、最终状态和 fixture。失败时终端会列出原因并报错。回传证据目录中的 `probe-report.json`、`state/install-status.json`（如存在），说明窗口是否出现、是否能正常取消，以及防护提示。也请告知 Windows 版本与是否安装过 Visual Studio/VC 运行库；这些背景影响干净机结论。

默认证据目录为 `%TEMP%/mystia-steward-companion-p0-<随机值>/`。请保留现场，确认结果后可手动删除该次专用目录。报告含临时路径与系统防护版本，回传前可检查内容。

## 结果边界

- 只有 bootstrap 退出码为 `0`、状态文件严格符合 `state/message/progress` 三字段结构且 `state="cancelled"`、数字整数 `progress=0`、`pluginUnchanged=true`、`backupAbsent=true` 同时满足时，报告才记录 `result="passed"`。`succeeded`、重复字段、字符串形式的进度及缺失/无效状态均不能通过。
- `result="failed"` 的 `errors` 列出启动、等待、退出码、状态或 fixture 检查失败的原因；报告保留原始状态文本。失败在报告写入后抛出错误，不以窗口外观正常作为成功证据。状态读取失败时仍保留现场文件。防护版本或来源标记查询不可用会单独记录，不等同于进程运行失败。
- 在本地脚本中模拟旧 CLI，只证明 fixture 启动链；真实旧 Mod 的复制/启动/PID 检查仍须后续在受控游戏环境验证。
- Actions artifact 下载、共享目录复制、本地构建和带来源标记的文件是不同来源，请准确描述。不能以本次结果替代正式候选的下载信誉验证。
- 本批不覆盖实际安装/回滚、窗口穿透、Android、身份迁移及旧 Mod 五分钟下载预算。后续探针会分批提供。

## 开发入口

源码与构建规则见[Flutter 开发与探针](../../docs/flutter-development.md)。分发包中的同名文档只提供操作说明；开发者从仓库阅读链接。`build-evidence.json` 记录构建 SHA、固定 SDK、bootstrap 哈希、资源包大小和 VC runtime。

CI 可在当前进程设置 `MYSTIA_UPDATER_PROBE_AUTOMATION=cancel-after-ready`，让 UI 连接就绪后自动走同一取消路径；脚本标记 `executionMode="automatic"`。未设置或空值为 `manual`，其他非空值会失败。自动运行不证明人工点击、窗口观感或下载信誉。

开发/CI 可传 `-EvidenceParent <已有目录>`，将唯一 fixture 子目录放到指定证据目录下；脚本不会创建或覆盖指定的父目录，默认仍使用 `%TEMP%`。

`Test-Start-Probe.ps1` 使用临时文件和模拟 bootstrap 进程检查交付脚本的通过/失败判定与报告保留，不启动真实 UI。用仓库锁定 PowerShell 执行；Windows CI 还应使用系统 Windows PowerShell 5.1 验证兼容性。测试结果不能替代 Windows UI 实机证据。
