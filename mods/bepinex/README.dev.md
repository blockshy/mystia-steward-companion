# mystia-steward-companion BepInEx Mod 开发入口

本文面向维护者，只提供 Mod 子项目地图、最短上手步骤和专题文档入口。用户安装与操作说明见 [Mod 使用说明](README.md)，全部开发文档职责见[开发文档索引](../../docs/README.md)。

## 目录

- `src/Core/`：领域模型、运行时目录仓库、稀客标识和事务文件基础设施。
- `src/LocalApi/`：HTTP 监听器、请求解析、DTO、主设备配置和本地 JSON 存储。
- `src/Plugin/`：BepInEx 插件入口、配置、控制台和伴随进程生命周期。
- `src/Save/`：游戏运行时读取、订单捕获、自动化、任务和特殊经营服务。
- `src/Ui/`：Unity 主线程命令、自动化控制器和游戏内 UI 接入。
- `src/Updates/`：版本检查与更新状态服务。
- `References/`：锁定的本地编译引用；真实 DLL 不提交。
- `tools/`：preflight、构建、打包、发布和 IL2CPP 分析工具。
- `bin/`、`obj/`、`dist/`：可再生构建产物。

`tools/il2cpp-analysis/InteropGenerator` 是独立工具工程；Mod csproj 会排除 `tools/**/*.cs`，不要把分析工具源码加入插件程序集。

## 最短上手

先按[本地开发与构建](../../docs/local-development.md)安装锁定工具链并执行冻结依赖安装。Mod 构建前还要按 [References 说明](References/README.md)恢复并校验 7 个正式引用。

在仓库根目录运行：

```bash
corepack pnpm toolchain:check
corepack pnpm references:verify
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

Windows 可先运行 Mod preflight：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\preflight.ps1
```

构建完整 Windows 本地包：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1
```

这些命令只构建本地产物，不创建 Git 标签或 GitHub Release。Android 开发环境和签名见 [Android 开发](../../docs/android-development.md)，正式/预览发布只按[发布流程](../../docs/local-release.md)执行。

## 专题入口

| 主题 | 文档 |
| --- | --- |
| 组件和数据流 | [项目架构](../../docs/architecture.md) |
| 命名、编码和 fail-closed 规则 | [开发约定](../../docs/development-conventions.md) |
| 工具链、依赖、构建和缓存 | [本地开发与构建](../../docs/local-development.md) |
| 按改动选择测试 | [验证指南](../../docs/validation-guide.md) |
| 静态目录、玩家状态与快照 | [游戏数据提供器](../../docs/runtime-provider.md) |
| 普客/稀客捕获与最终状态记录 | [订单捕获与生命周期](../../docs/runtime-order-lifecycle.md) |
| 稀客调度名单、订单队列与执行条件 | [稀客调度与订单队列](../../docs/rare-order-participation.md) |
| 厨具占用、`CookingJob` 与暂停恢复 | [自动化运行时](../../docs/automation-runtime.md) |
| HTTP 监听器、鉴权、路由与主设备配置 | [本地 API](../../docs/local-api.md) |
| 置顶、变体与游戏内高亮 | [游戏 UI 集成](../../docs/game-ui-integration.md) |
| 任务运行时 | [任务系统](../../docs/missions.md) |
| 特殊挑战 | [特殊经营实现](../../docs/special-business-implementation.md)与[验证](../../docs/special-business-validation.md) |
| 更新与 updater | [更新系统](../../docs/update-system.md) |
| 总日志、控制台和诊断包 | [日志与诊断](../../docs/observability.md) |

## 游戏运行时分析

涉及游戏类型、字段、集合、Hook 或游戏写操作边界时，不凭名称或旧日志猜测。按 [IL2CPP / IDA 分析工作流](../../docs/il2cpp-analysis-workflow.md)依次核对：

1. 当前游戏 metadata C#；
2. 锁定 BepInEx #783 interop；
3. IDA/Hex-Rays 原生执行路径；
4. 实机日志和专项 smoke。

分析输出位于仓库外，不提交反编译产物。旧分析只用于历史比较，不建立兼容备用路径。

## 运行时开发规则

- Mod 不读取 `.memory` 存档作为业务来源；游戏状态来自当前内存对象，存档只用于明确的只读诊断。
- Unity 对象只在主线程访问；API worker 只读不可变缓存或排队命令。
- 读取不完整、对象标识不唯一、Hook 未完整就绪或原生写操作结果不确定时，必须停止相关功能。
- 普客/稀客业务只使用精确捕获的当前订单。HUD 空窗或读取失败不能过滤捕获，也不能通过启动扫描补建执行所有权。
- 不恢复旧 Hook、旧路由、旧存储 schema、备用视觉或按名称/路径/位置猜测的逻辑。

具体约束只在上表对应专题维护，不再复制到本入口。

## 调试顺序

1. 构建失败先运行 `corepack pnpm toolchain:check` 和 `corepack pnpm references:verify`。
2. 游戏状态不可用时先看伴随窗口状态与诊断包，再根据领域查看总日志中的对应部分。
3. 本地 API 问题先验证 `http://127.0.0.1:32145/health`；LAN 问题再检查私网地址、防火墙、AP 隔离和 Token。
4. 运行时字段或 Hook 发生变化时先重新生成分析资料，再修改 Provider；不要增加第二套反射来源。
5. 修复后先跑最窄专项 smoke/audit，再按[验证指南](../../docs/validation-guide.md)补充仓库级检查。

## 已知边界

- 正式编译依赖私有锁定 References；公开仓库本身不能独立完成 Mod 构建。
- 游戏更新可能改变 IL2CPP 类型、字段和原生地址，需要按分析工作流重新确认。
- Android 伴随窗口只作为 LAN 客户端；托盘、聚焦、鼠标穿透和单实例属于桌面平台。
- 伴随窗口是唯一用户界面，不提供游戏内备用面板。
