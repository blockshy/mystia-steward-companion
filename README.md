# mystia-steward-companion

**mystia-steward-companion** 是面向《东方夜雀食堂》的 BepInEx IL2CPP Mod。它读取游戏当前运行时数据，并通过独立桌面伴随窗口提供普客、稀客和夜间经营推荐。

本仓库不再维护独立网站、存档导入页面或浏览器版推荐工具。所有推荐入口都围绕游戏内 Mod 和本地回环 API 工作。

## 功能概览

- 实时读取已解锁料理、酒水、食材、库存、流行标签、明星店状态和当前经营场景。
- 普客页按当前地区推荐料理与酒水。
- 稀客页按候选稀客和点单 tag 推荐料理、加料与酒水，可在设置页调整推荐排序规则。
- 任务页会列出日间场景中的稀客邀请候选，可在当前场景和全部场景之间切换；可邀请项支持单独邀请或一次邀请全部，不满足羁绊或已邀请的项会显示原因并禁用按钮。
- 经营中页自动检测当前稀客点单，显示桌号、料理 tag、酒水 tag、当前稀客携带金钱和推荐结果；订单可按点单顺序或稀客分组排列，严格推荐不足时会额外显示明确标注的喜好备选。
- 概览和经营中页会显示关键快照耗时，用于排查运行时读取或自动化导致的卡顿。
- 支持收藏稀客的常用料理方案和酒水；同一稀客再次出现相同点单 tag 时会优先显示可用收藏。
- 支持稀客订单专注模式，可调节料理/酒水显示数量并切换精简模式，减少滚动切换。
- 任务页可读取当前进度中可接取、进行中或可完成的交互任务，默认显示可接取和可完成任务，支持多选状态筛选，并尽量显示未接取 NPC 所在场景；任务 ID 和读取来源默认隐藏，可用 `显示额外信息` 辅助排查。
- 独立伴随窗口会记住上次的位置和大小，下次启动时自动恢复。
- 独立伴随窗口可在 `设置` 页调整背景透明度、置顶状态、主题、焦点切换行为、BepInEx 原生日志窗口、缺失厨具过滤、任务料理优先、推荐排序、实验性游戏界面置顶和目标厨具高亮。
- 独立伴随窗口支持手柄导航，可用摇杆/十字键移动、确认、返回、切页、滚动和收藏推荐项。
- 设置页提供实验性自动化总开关和自动化参数；开启后可在经营中页分别配置稀客订单和普客订单的处理阶段，并查看厨具预约和送餐盘压力。稀客自动化会锁定本次选择的料理/酒水并可复用已堆积的可用料理，自动开始料理时会固定尝试完成原生 QTE 奖励结算。
- 修改页可用快捷按钮调整当前运行时材料和酒水库存数量，也可把当前存档可编辑材料或酒水批量设为 99。
- 日志页读取 `BepInEx/LogOutput.log` 和自动化作业日志尾部内容并限制缓存上限，可按需开启经营诊断，也可以一键导出诊断包。
- Mod 默认关闭下一次启动的 BepInEx 控制台日志窗口，并在当前 Windows 会话尝试隐藏控制台；需要临时查看时可在 `设置` 页重新开启。
- 桌面伴随窗口可移动、缩放、置顶；`F8`/`RS Click` 可在游戏和伴随窗口之间切换，游戏关闭后窗口会自动退出。

## 目录结构

```text
apps/companion/                Tauri 伴随窗口应用
apps/companion/src/            React 工作台、推荐算法、UI 组件和运行时数据适配
apps/companion/src-tauri/      桌面伴随窗口壳
mods/bepinex/                  BepInEx 插件、运行时读取、本地 API、打包脚本
docs/                          Mod 开发约定、机制知识库和运行时说明
```

## 使用与开发入口

- 用户安装、快捷键和故障排查：[mods/bepinex/README.md](mods/bepinex/README.md)
- 开发环境、构建和打包：[mods/bepinex/README.dev.md](mods/bepinex/README.dev.md)
- 开发约定：[docs/development-conventions.md](docs/development-conventions.md)
- 本地构建与发布方案：[docs/local-release.md](docs/local-release.md)
- 仓库状态：[docs/repo-memory.md](docs/repo-memory.md)
- 料理机制：[docs/tmi-cooking-mechanics-knowledge-base.md](docs/tmi-cooking-mechanics-knowledge-base.md)
- Addressables 映射：[docs/addressables-tag-mapping-playbook.md](docs/addressables-tag-mapping-playbook.md)
- 运行时读取：[mods/bepinex/docs/RUNTIME_PROVIDER_NOTES.md](mods/bepinex/docs/RUNTIME_PROVIDER_NOTES.md)

## 许可证与来源

本项目以 `AGPL-3.0-only` 发布。仓库源自 `Well2333/mystia-steward`，且原项目使用或派生自 `AnYiEE/touhou-mystia-izakaya-assistant`；后者标注为 `AGPL-3.0-only`。

详细说明见 [NOTICE](NOTICE) 和 [LICENSE](LICENSE)。

## 免责声明

本项目为非官方开源工具，仅用于学习与辅助决策，不隶属于游戏官方。游戏版本更新可能导致运行时读取路径、数据或规则变化。
