# mystia-steward-companion

**mystia-steward-companion** 是面向《东方夜雀食堂》的 BepInEx IL2CPP Mod。它读取游戏当前运行时数据，并通过独立桌面伴随窗口提供普客、稀客和夜间经营推荐。

本仓库不再维护独立网站、存档导入页面或浏览器版推荐工具。所有推荐入口都围绕游戏内 Mod 和本地回环 API 工作。

## 功能概览

- 实时读取已解锁料理、酒水、食材、库存、流行标签、明星店状态和当前经营场景。
- 普客页按当前地区推荐料理与酒水。
- 稀客页按候选稀客和点单 tag 推荐料理、加料与酒水。
- 经营中页自动检测当前稀客点单，按订单出现顺序显示桌号、料理 tag、酒水 tag 和推荐结果。
- 支持稀客订单专注模式和精简模式，只显示当前点单推荐并减少滚动切换。
- 修改页可调整当前运行时材料和酒水库存数量。
- 日志页读取 `BepInEx/LogOutput.log` 尾部内容并限制缓存上限，可按需开启经营诊断。
- Mod 默认关闭下一次启动的 BepInEx 控制台日志窗口，并在当前 Windows 会话尝试隐藏控制台。
- 桌面伴随窗口可移动、缩放、置顶；`F8`/`RS Click` 可在游戏和伴随窗口之间切换，游戏关闭后窗口会自动退出。

## 目录结构

```text
apps/companion/                Tauri 伴随窗口应用
apps/companion/src/            React 工作台、推荐算法、UI 组件和结构化数据
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
