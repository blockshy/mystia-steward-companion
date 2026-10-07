# Dart 原生 HTTP P0 探针

独立纯 Dart 包，仅使用 SDK。它使用自己启动的回环 HTTP 服务、受控记录代理和虚构 Token，不连接真实游戏、修改 Mod 或读取用户凭据。当前产品不引用此代码。

从仓库根目录运行标准入口（先精确核验 Node/Flutter/Dart，随后验证依赖锁、格式、静态分析、JIT 和真实 AOT）：

```text
corepack pnpm flutter:network-probe --sdk-root <locked-flutter-sdk>
```

报告与 AOT 程序保存到 `temp/flutter-network-probe/run-*/`；入口不安装 SDK、不修改正式客户端。单独排查时可从本目录使用同一锁定 Dart SDK：

```text
<locked-dart> analyze
<locked-dart> tool/run_probe.dart
```

探针使用显式检查与非零退出码，不依赖 `assert`，因此 AOT/关闭断言也执行所有检查。可用相同锁定 SDK 的 `dart compile exe tool/run_probe.dart -o <output>` 生成当前主机平台的独立程序；Windows EXE 必须在 Windows 构建。JSON 报告含实际平台与 Dart 版本，不记录 Token。

验证真实 HTTP 接收、UTF-8 JSON 字节长度、无 chunked、重定向拒绝、当前 IPv4/localhost 地址范围、代理环境隔离、HTTP/业务/超时错误区分以及 POST 结果未知时不重放。代理测试另起同一探针的子进程注入代理变量，同时确认来源服务收到请求且代理没有连接；不修改系统代理。

关闭回环端口的连接可能立即被系统拒绝，也可能超过连接预算后超时；Windows CI 已观察到后一种情况。该检查只接受 `connectionRefused` 或 `connectTimeout`，且必须为 `notSent`；`unknown` 仍会失败。探针不通过固定等待、增加预算或重试来假定系统返回时机。

`lib/local_api_probe.dart` 只提供一次请求的探针适配。`maxResponseBytes` 是本地测试预算，不是新生产协议上限；无会话管理、租约、通用重试、配置迁移或客户端页面。`ok=false` 会区分为业务失败，但不擅自解释为“没有产生游戏副作用”；具体动作仍需未来业务状态机判断。

当前 C# `HttpRequestReader` 按 ASCII 解码请求头，现行客户端设备头标签固定为英文。这里拒绝非 ASCII 请求头；中文用户设备名称在 JSON 请求体中完整往返，不声称已扩展原有头部协议。

探针结果只证明当前主机的受控回环行为。真实 Mod 解析器、局域网与系统防火墙、Android Release 明文策略及 LAN 权限、Windows 实机均需对应平台证据。客户端关闭/超时只能结束本地等待，不能证明服务端撤销已接收的 POST。
