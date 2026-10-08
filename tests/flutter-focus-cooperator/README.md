# Flutter 焦点合作测试插件

该插件仅用于 P0 新游戏副本，保留原 Mod 1.3.1。它在同步 BepInEx `Load()` 中添加独立 `Update` 心跳组件，后台 worker 仅读取不可变托管快照，并通过命名管道对精确 Flutter PID 执行每轮一次的前台授权。它不接入产品 F8/RS，不操作游戏业务对象，也不把主循环心跳解释为菜单或存档就绪。

[PROTOCOL.md](PROTOCOL.md) 维护固定身份、176 字节帧、主线程进展与 schema 2 证据契约。锁定 BepInEx #783、Il2CppInterop、Unity wrapper 的来源和 SHA 由 [references.lock.json](../../mods/bepinex/References/references.lock.json) 统一维护；新增引用仍来自这份既有基线，不使用 NuGet 或系统安装的游戏程序集。

构建须使用锁定 SDK 10、明确 References 目录和完整 `ProbeGitSha`；用于实机的干净源码双文件包由 [build-flutter-focus-cooperator.mjs](../../scripts/build-flutter-focus-cooperator.mjs) 生成。包必须与原生探针来自同一提交，准备与实机步骤见[窗口探针说明](../flutter-window-probe/README.md)。

纯托管契约 smoke 从仓库根运行 `corepack pnpm test:dotnet6 flutter-focus-cooperator`。它验证字节帧、身份拒绝、授权次数、心跳推进/停止/线程/取消及 EOF；这些是模型证据。实际组件是否收到 Update、游戏是否完成正常退出，必须由锁定 Windows 游戏副本实测确认。
