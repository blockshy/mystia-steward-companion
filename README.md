# mystia-steward-companion

`mystia-steward-companion` 是《东方夜雀食堂》的非官方 BepInEx IL2CPP Mod。它读取游戏当前状态，并通过独立的 Windows 或 Android 伴随窗口提供料理推荐、经营辅助和可选自动化。

本页只介绍主要功能和使用入口。完整安装、页面操作与故障排查请阅读 [Mod 使用说明](mods/bepinex/README.md)，具体功能也可以在伴随窗口的 `设置 -> 帮助` 中查看。

## 主要功能

- **料理推荐**：按地区、普客、稀客和点单 Tag 推荐料理、加料与酒水。
- **收藏与自定义方案**：集中管理料理和酒水收藏，也可以为指定稀客配置固定推荐料理。
- **经营中助手**：实时显示普客与稀客订单、推荐方案、执行状态和必要诊断。
- **可选自动化**：可分别控制送酒、开锅、送料理和完成订单；功能默认由用户手动配置。
- **游戏界面辅助**：可选料理、食材、酒水、厨具、桌位和订单高亮，以及目标加料料理选项。
- **任务与邀请**：查看可接取及进行中的任务，并管理当天的稀客邀请名单；两个模块默认关闭。
- **特殊经营支持**：支持怪诞料理大赛、幽幽子挑战与重修、寻找瑞灵踪迹、月都试炼 1/2/3、血池地狱等场景。
- **多设备连接**：游戏电脑负责运行 Mod，Windows 或 Android 设备可在可信局域网内连接；多设备时由用户指定主设备配置。
- **日志与更新**：提供运行日志、诊断包和正式版本更新检查，便于排查问题和升级。

自动化、运行时库存修改和部分游戏界面辅助会改变游戏运行状态。首次使用前建议备份存档，并先熟悉对应设置和帮助说明。

## Mod窗口页面一览

| 页面 | 用途 |
| --- | --- |
| `概览` | 查看连接、运行时数据和库存摘要。 |
| `推荐料理` | 查看普客、稀客推荐，维护自定义方案和收藏。 |
| `经营中` | 查看实时订单、推荐方案和自动化状态。 |
| `扩展功能` | 使用任务列表、稀客邀请和运行时库存修改。 |
| `设置` | 管理窗口、连接、推荐、实验性功能、更新和帮助。 |
| `日志` | 开启调试信息后显示，用于导出日志和诊断包。 |

## 安装与快速开始

1. 从 [GitHub Releases](https://github.com/blockshy/mystia-steward-companion/releases) 下载发布包。
2. 按 [安装 BepInEx](mods/bepinex/README.md#安装-bepinex) 和 [安装 Mod](mods/bepinex/README.md#安装-mod) 完成部署。
3. 启动游戏并读取存档，等待伴随窗口自动连接。
4. 在 `概览` 页确认运行时数据已经就绪，再进入推荐或经营页面。
5. 自动化、游戏界面辅助、任务列表和稀客邀请按需手动开启。

游戏电脑必须安装 Mod。另一台 Windows 电脑或 Android 设备只需安装对应伴随客户端，再按 [其他设备连接说明](mods/bepinex/README.md#另一台设备连接windows--android) 连接游戏电脑。

## 常用操作

| 操作 | 作用 |
| --- | --- |
| `F8` / `RS Click` | 在游戏和伴随窗口之间切换。 |
| `F10` | 开启或关闭伴随窗口鼠标穿透。 |
| `A` / `B` | 手柄确认 / 返回。 |
| `LB` / `RB` | 切换页面或页签。 |
| `LT` / `RT` | 滚动当前区域。 |
| `X` | 在推荐项上收藏或取消收藏。 |
| `Y` | 进入稀客专注模式或切换精简显示。 |

完整键盘、手柄和窗口行为见 [快捷键与手柄](mods/bepinex/README.md#快捷键与手柄)。

## 遇到问题时

1. 在 `设置 -> 窗口` 开启 `显示调试信息`。
2. 进入 `日志` 页开启总日志，并复现问题。
3. 导出诊断包，必要时记录相关订单的 `R-xxxx` 或 `N-xxxx` 日志标识。
4. 提供诊断包以及对应的 `aggregate-mod.log` 日志文件；分享前请检查日志中的本机路径和游戏状态信息。

详细步骤见 [日志与诊断](mods/bepinex/README.md#日志与诊断) 和伴随窗口内置帮助。

## 文档入口

面向用户：

- [Mod 安装与使用说明](mods/bepinex/README.md)
- 伴随窗口内的 `设置 -> 帮助`

面向开发者：

- [开发环境、构建与打包](mods/bepinex/README.dev.md)
- [开发约定](docs/development-conventions.md)
- [本地构建与发布](docs/local-release.md)
- [IL2CPP / IDA 分析流程](docs/il2cpp-analysis-workflow.md)
- [项目机制与当前约束](docs/repo-memory.md)

## 主要开源组件

| 组件 | 主要用途 | 开源协议 | 项目链接 |
| --- | --- | --- | --- |
| BepInEx | Unity IL2CPP Mod 加载与插件框架 | LGPL-2.1-only | [BepInEx/BepInEx](https://github.com/BepInEx/BepInEx) |
| HarmonyX | 运行时方法补丁 | MIT | [BepInEx/HarmonyX](https://github.com/BepInEx/HarmonyX) |
| Il2CppInterop | CoreCLR 与 Unity IL2CPP 运行时互操作 | LGPL-3.0-only | [BepInEx/Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) |
| Tauri 及官方插件 | Windows / Android 伴随窗口和系统能力 | MIT OR Apache-2.0 | [tauri-apps/tauri](https://github.com/tauri-apps/tauri)、[plugins-workspace](https://github.com/tauri-apps/plugins-workspace) |
| React / React DOM | 伴随窗口用户界面 | MIT | [facebook/react](https://github.com/facebook/react) |
| Mantine | 界面组件与 React Hooks | MIT | [mantinedev/mantine](https://github.com/mantinedev/mantine) |
| Tailwind CSS | 界面样式与构建插件 | MIT | [tailwindlabs/tailwindcss](https://github.com/tailwindlabs/tailwindcss) |
| Tabler Icons | 界面图标 | MIT | [tabler/tabler-icons](https://github.com/tabler/tabler-icons) |
| Geist | 伴随窗口字体 | OFL-1.1 | [Fontsource Geist](https://fontsource.org/fonts/geist) |
| clsx | CSS 类名组合 | MIT | [lukeed/clsx](https://github.com/lukeed/clsx) |
| Serde / serde_json | Tauri 端数据序列化 | MIT OR Apache-2.0 | [serde-rs/serde](https://github.com/serde-rs/serde)、[serde-rs/json](https://github.com/serde-rs/json) |
| Vite | 前端开发与生产构建 | MIT | [vitejs/vite](https://github.com/vitejs/vite) |
| TypeScript | 前端类型检查与编译 | Apache-2.0 | [microsoft/TypeScript](https://github.com/microsoft/TypeScript) |
| ESLint | 代码静态检查 | MIT | [eslint/eslint](https://github.com/eslint/eslint) |
| Playwright | 界面自动化巡检 | Apache-2.0 | [microsoft/playwright](https://github.com/microsoft/playwright) |

上表列出项目直接使用的主要运行时组件和开发工具。完整依赖及锁定版本以 [package.json](package.json)、[pnpm-lock.yaml](pnpm-lock.yaml)、[Cargo.toml](apps/companion/src-tauri/Cargo.toml) 和 [Cargo.lock](apps/companion/src-tauri/Cargo.lock) 为准；各组件的使用与再分发分别遵循其自身许可证。

## 许可证与来源

本项目以 `AGPL-3.0-only` 发布，完整许可证文本见 [LICENSE](LICENSE)。复制、分发、修改或公开提供本项目及其衍生版本时，请遵守 AGPL-3.0-only 的条款，包括在适用场景下提供对应源代码和保留许可证声明。

本项目保留对 `AnYiEE/touhou-mystia-izakaya-assistant` 的来源与授权声明；该项目标注为 `AGPL-3.0-only`。相关来源、授权和版权声明见 [NOTICE](NOTICE)。

本项目不主张拥有《东方夜雀食堂》或相关作品中的名称、图像、图标、文本、角色、商标及其他游戏素材的权利。这些内容的权利归其各自权利人所有。

本项目不是官方工具，也不代表游戏开发者、发行商或任何相关权利方。项目名称中的游戏相关表述仅用于说明适配对象。

## 免责声明

本项目为非官方开源工具，仅用于学习、研究和游戏过程中的辅助决策。使用本项目前，建议先备份存档，并自行确认所在地、平台规则和社区规则是否允许使用此类 Mod。

Mod 会读取游戏运行时状态；部分功能会修改运行时库存、调整窗口行为或执行实验性自动化操作。这些功能可能影响原始游戏体验，也可能在游戏更新、DLC 差异、特殊经营场景、其他 Mod 或系统环境变化时失效。

日志、总日志分片和诊断包可能包含游戏运行状态、订单、订单日志标识、库存、错误信息或本机路径。对外分享日志前，请自行检查并删除不希望公开的内容。

本项目按“现状”提供，不承诺无错误、无兼容问题、无数据损失、持续维护或适合特定用途。因安装、使用、修改、分发本项目造成的游戏异常、存档问题、平台风险或其他损失，由使用者自行承担。
