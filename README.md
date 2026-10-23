# mystia-steward-companion

`mystia-steward-companion` 是《东方夜雀食堂》的非官方 BepInEx IL2CPP Mod。游戏电脑上的 C# 模块负责读取游戏、计算推荐和执行可选自动化；Flutter 伴随客户端在 Windows 或 Android 上展示结果、管理配置并提供人工操作。

本页介绍产品与入口。安装、配置和故障排查见 [Mod 使用说明](mods/bepinex/README.md)，客户端内也可离线阅读 `设置 → 帮助`。当前源码使用 Flutter、Forui 和官方 animations；实际已发布版本以 [GitHub Releases](https://github.com/blockshy/mystia-steward-companion/releases) 为准，分支开发完成不代表已经发布。

## 页面与主要功能

| 页面 | 功能 |
| --- | --- |
| 总览 | 经营状态、今日供给、食材与酒水合并低库存表。 |
| 经营 | 稀客与普客的实时订单方案、自动化状态、精简制作信息的专注页，以及订单数量、当前厨具、暂停原因与待确认事项的统计信息。 |
| 推荐 | 普客地区推荐、稀客需求推荐、收藏、自定义配方、推荐偏好。有效条件确定后自动查询，可搜索结果。 |
| 扩展 | 任务列表、稀客邀请、库存修改、自动化、游戏界面辅助。各模块页签常驻，在页内启停。 |
| 数据 | 料理与酒水图鉴，按已解锁、未解锁或全部筛选并搜索；游戏图像资源管理。 |
| 设置 | 连接与设备、外观、成套更新、诊断与帮助。 |

- **推荐与配方**：普客推荐默认跟随经营地点，可手动固定或恢复跟随，并查看地区完整客群偏好。稀客推荐与经营的具体方案可收藏或取消，收藏支持名称、标签、食材及编号搜索。自定义配方分为独立的“料理模式”和“稀客模式”，支持当前模式全部或本组批量启停、置顶；评分提供四个固定预设及可调权重的“自定义”。
- **订单与专注**：订单按点单时间显示。专注页默认合并两类订单的时间顺序，也可在本机启用“稀客优先”，选择制作字段及收藏按钮的显示、隐藏窗口 UI；不改变自动化调度规则。
- **自动化与辅助**：分别控制普客/稀客的送酒、开锅、料理交付和完成订单；游戏界面辅助提供目标列表置顶、加料选项及厨具、桌位、订单高亮。支持已实现规则的特殊经营场景，详见[特殊经营说明](docs/special-business-scenes-notes.md)。
- **多设备**：游戏电脑运行完整 Mod，其它 Windows 或 Android 客户端通过可信局域网连接。主设备管理共享配置并维持自动化，查看设备不会自动接管。
- **诊断与更新**：记录实际操作及结果，支持聚合日志、诊断包导出和删除管理；页头更新提醒可直达更新页或延后 24 小时提醒，版本说明保留文档层次并提供发布页入口。

任务列表、稀客邀请和库存修改的开关按设备保存，默认关闭；自动化与游戏界面辅助使用持久化共享总开关，重启或重连后沿用。自动化、库存修改和加料辅助会改变游戏状态，首次使用前请备份存档并阅读对应帮助。

任务按“全部、可完成、进行中、可接取、待确认”一层状态筛选，逐条显示条件要求和已确认进度。统计中的当前厨具使用紧凑行，显示实际名称、游戏效果说明及制作中、待取餐等状态；未知资料不会据类型猜测。

## 安装与开始使用

1. 在游戏电脑按[安装 BepInEx](mods/bepinex/README.md#安装-bepinex)准备已验证的 IL2CPP #783 环境。
2. 按[安装 Mod](mods/bepinex/README.md#安装-mod)部署完整目录。Mod、Windows/Android 客户端应成套更新，不能混用旧客户端或只复制单个 EXE/DLL。
3. 启动游戏，等待图像初始化或缓存校验完成；进入存档后查看总览及推荐。Windows 窗口隐藏时可按 F8。
4. 按需启用扩展功能。自动化暂停时到 `经营 → 统计信息` 查看原因与待确认事项；详细任务过程在对应客人类型的“自动化状态”中查看。
5. 另一台设备按[局域网连接步骤](mods/bepinex/README.md#另一台设备连接windows--android)填写游戏电脑地址和 Token；切换主设备前先同步共享配置。

首次运行从游戏本机读取图像，不随安装包分发游戏图片。缓存校验、等待目录、提取和失败统一在独立初始化界面显示。旧缓存缺少启动凭据时需进入存档确认一次；随后游戏版本、DLC、资源身份及缓存完整性检查通过，即可在主菜单复用。数据中的“资源管理”支持分类、搜索和主设备重新读取。

Windows 使用窄侧栏；Android 使用底部六项导航。字号、主题和显示偏好按设备保存；主题支持跟随系统、浅色和深色，Windows 的背景与内容不透明度分别控制填充与前景。料理/酒水列表展示紧凑标签摘要，余项以“+N”表示，详情保留完整内容；刷新时已有有效信息保持显示，新方案未返回的字段使用占位。

## 常用操作

| 操作 | 作用 |
| --- | --- |
| F8 / RS Click | Windows 游戏与伴随窗口之间切换。 |
| F10 | Windows 鼠标穿透开关及恢复。 |
| 左摇杆 / 十字键 | 移动焦点。 |
| A / B | 手柄确认 / 返回。 |
| LB / RB | 切换主页面。 |
| LT / RT | 切换当前横向页签。 |
| 可收藏行上的 X | 收藏或取消当前具体推荐项。 |

Windows 与 Android 使用同一套手柄导航；提示仅在手柄操作时显示，重连或恢复焦点后先松开按键和摇杆回中。关闭 Windows 窗口会隐藏到托盘；完整退出使用托盘或外观页入口。作为主设备的 Android 进入后台会暂停自动化，回到前台并恢复连接后才按保存配置继续。

## 遇到问题时

在游戏电脑的 `设置 → 诊断` 开启聚合 Mod 日志，记录复现时间、操作和订单 `R-xxxx` / `N-xxxx` 标识，再导出诊断包。分享前检查其中的本机路径与游戏信息。操作结果未知时先核对现场，不通过反复提交猜测是否成功；详见[日志与诊断](mods/bepinex/README.md#日志与诊断)。

## 文档入口

| 读者 | 入口 |
| --- | --- |
| 用户 | [Mod 安装与使用说明](mods/bepinex/README.md)、客户端 `设置 → 帮助` |
| 开发者 | [开发环境、构建与打包](mods/bepinex/README.dev.md)、[专题索引](docs/README.md)、[开发约定](docs/development-conventions.md) |
| UI 与平台开发 | [客户端 UI 规范](docs/companion-ui.md)、[Android 开发](docs/android-development.md) |
| 运行时分析 | [IL2CPP / IDA 流程](docs/il2cpp-analysis-workflow.md)、[项目机制与约束](docs/repo-memory.md) |

## 主要开源组件

| 组件 | 主要用途 | 开源协议 | 项目链接 |
| --- | --- | --- | --- |
| BepInEx | Unity IL2CPP Mod 加载与插件框架 | LGPL-2.1-only | [BepInEx/BepInEx](https://github.com/BepInEx/BepInEx) |
| HarmonyX | 运行时方法补丁 | MIT | [BepInEx/HarmonyX](https://github.com/BepInEx/HarmonyX) |
| Il2CppInterop | CoreCLR 与 Unity IL2CPP 运行时互操作 | LGPL-3.0-only | [BepInEx/Il2CppInterop](https://github.com/BepInEx/Il2CppInterop) |
| Flutter / Dart | Windows / Android 界面与客户端 | BSD-3-Clause | [flutter/flutter](https://github.com/flutter/flutter)、[dart-lang/sdk](https://github.com/dart-lang/sdk) |
| Forui | 统一组件、主题与图标 | MIT；字体等资产见各包声明 | [forus-labs/forui](https://github.com/forus-labs/forui) |
| animations | 官方过渡动画 | BSD-3-Clause | [flutter/packages](https://github.com/flutter/packages/tree/main/packages/animations) |
| Riverpod | 页面依赖注入 | MIT | [rrousselGit/riverpod](https://github.com/rrousselGit/riverpod) |
| windows-sys | 独立 Windows 更新程序的 DPI 与原生控件接入 | MIT OR Apache-2.0 | [microsoft/windows-rs](https://github.com/microsoft/windows-rs) |
| Serde / serde_json | 独立更新程序序列化 | MIT OR Apache-2.0 | [serde-rs/serde](https://github.com/serde-rs/serde)、[serde-rs/json](https://github.com/serde-rs/json) |
| Vite / TypeScript | 冻结的离线业务差分参照 | MIT / Apache-2.0 | [vitejs/vite](https://github.com/vitejs/vite)、[microsoft/TypeScript](https://github.com/microsoft/TypeScript) |

上表列出主要组件。完整依赖及锁定版本以 [Flutter 依赖](apps/companion-flutter/pubspec.lock)、[Node 依赖](pnpm-lock.yaml) 和 [更新程序依赖](native/updater/Cargo.lock) 为准；各组件的使用与再分发分别遵循自身许可证。Windows 使用系统中文字体，不随包复制系统字体文件。

## 许可证与来源

本项目以 `AGPL-3.0-only` 发布，完整许可证文本见 [LICENSE](LICENSE)。复制、分发、修改或公开提供本项目及其衍生版本时，请遵守 AGPL-3.0-only 的条款，包括在适用场景下提供对应源代码和保留许可证声明。

本项目保留对 `AnYiEE/touhou-mystia-izakaya-assistant` 的来源与授权声明；该项目标注为 `AGPL-3.0-only`。相关来源、授权和版权声明见 [NOTICE](NOTICE)。

本项目不主张拥有《东方夜雀食堂》或相关作品中的名称、图像、图标、文本、角色、商标及其他游戏素材的权利。这些内容的权利归其各自权利人所有。

本项目不是官方工具，也不代表游戏开发者、发行商或任何相关权利方。项目名称中的游戏相关表述仅用于说明适配对象。

## 免责声明

本项目为非官方开源工具，仅用于学习、研究和游戏过程中的辅助决策。使用本项目前，建议先备份存档，并自行确认所在地、平台规则和社区规则是否允许使用此类 Mod。

Mod 会读取游戏当前状态；部分功能会修改当前库存、调整窗口行为或执行实验性自动化操作。这些功能可能影响原始游戏体验，也可能在游戏更新、DLC 差异、特殊经营场景、其他 Mod 或系统环境变化时失效。

日志、总日志分片和诊断包可能包含游戏运行状态、订单、订单日志标识、库存、错误信息或本机路径。对外分享日志前，请自行检查并删除不希望公开的内容。

本项目按“现状”提供，不承诺无错误、无兼容问题、无数据损失、持续维护或适合特定用途。因安装、使用、修改、分发本项目造成的游戏异常、存档问题、平台风险或其他损失，由使用者自行承担。
