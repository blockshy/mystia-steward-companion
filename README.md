# 夜雀掌柜

**夜雀掌柜 Mystia Steward** 现在是面向《东方夜雀食堂》的 BepInEx IL2CPP Mod。它读取游戏当前运行时数据，并通过独立桌面伴随窗口提供普客、稀客和夜间经营推荐。

本仓库不再维护独立网站、存档导入页面或浏览器版推荐工具。所有推荐入口都围绕游戏内 Mod 和本地回环 API 工作。

## 功能

- 实时读取已解锁料理、酒水、食材、库存、流行标签、明星店状态和当前经营场景。
- 普客页按当前地区推荐料理与酒水。
- 稀客页按候选稀客和点单 tag 推荐料理、加料与酒水。
- 经营中页自动检测当前稀客点单，显示桌号、料理 tag、酒水 tag 和推荐结果。
- 支持稀客订单专注模式，只显示当前点单推荐。
- 日志页显示 Mod 实时日志，便于排查运行时读取问题。
- 桌面伴随窗口可移动、缩放、置顶，并会在游戏关闭后自动退出。

## 目录结构

```text
mods/mystia-steward-bepinex/   BepInEx 插件、运行时读取、本地 API、打包脚本
src/companion/                 Tauri 伴随窗口的 React 工作台
src/components/                伴随窗口复用 UI 组件
src/lib/                       推荐算法、tag 规则和类型定义
src/data/                      结构化游戏数据，作为前端与 Mod 数据源
src-tauri/                     桌面伴随窗口壳
docs/                          Mod 开发约定、机制知识库和运行时说明
```

## 用户安装

用户安装、BepInEx 配置、快捷键和故障排查见 [mods/mystia-steward-bepinex/README.md](mods/mystia-steward-bepinex/README.md)。

安装包结构应类似：

```text
游戏根目录/
  BepInEx/
    plugins/
      MystiaSteward/
        MystiaSteward.BepInEx.dll
        Data/
        companion/
          mystia-steward-companion.exe
```

默认快捷键：

- `F8`：打开或唤起独立伴随窗口。
- `RS Click`：手柄打开或唤起独立伴随窗口。
- `F9`：手动刷新运行时数据。

## 开发环境

Windows 开发通常需要：

- Node.js 20+，通过 Corepack 使用仓库固定的 `pnpm@10.10.0`。
- .NET 6 SDK 或更新版本。
- Rust stable。
- Microsoft C++ Build Tools 2022 或 Visual Studio “使用 C++ 的桌面开发”组件。
- Microsoft Edge WebView2 Runtime。
- 已安装并启动过一次 BepInEx Unity IL2CPP 的游戏目录。

初始化示例：

```powershell
corepack enable
corepack prepare pnpm@10.10.0 --activate
```

Linux 仅用于构建验证时，还需要 WebKitGTK 依赖：

```bash
sudo apt-get install -y pkg-config libwebkit2gtk-4.1-dev libayatana-appindicator3-dev librsvg2-dev libssl-dev libxdo-dev
```

## 构建

推荐使用一键构建脚本：

```powershell
powershell -ExecutionPolicy Bypass -File mods\mystia-steward-bepinex\tools\build-release.ps1
```

脚本会执行依赖安装、Mod 前置检查、数据同步、伴随窗口前端构建、Tauri 桌面窗口构建、BepInEx DLL 构建和安装包生成。

常用增量构建：

```powershell
powershell -ExecutionPolicy Bypass -File mods\mystia-steward-bepinex\tools\build-release.ps1 -SkipInstall
powershell -ExecutionPolicy Bypass -File mods\mystia-steward-bepinex\tools\build-release.ps1 -SkipInstall -SkipTauriBuild
```

拆分构建命令：

```bash
pnpm install
pnpm build
pnpm tauri:build
dotnet build mods/mystia-steward-bepinex/MystiaSteward.BepInEx.csproj -c Release
```

常见产物：

```text
dist/                                                   # 伴随窗口前端产物
src-tauri/target/release/mystia-steward-companion.exe   # Windows 伴随窗口
src-tauri/target/release/bundle/nsis/*.exe              # Windows 安装包
mods/mystia-steward-bepinex/bin/Release/MystiaSteward.BepInEx.dll
mods/mystia-steward-bepinex/dist/MystiaSteward-BepInEx.zip
```

开发细节见 [mods/mystia-steward-bepinex/README.dev.md](mods/mystia-steward-bepinex/README.dev.md)。

## 本地 API

Mod 默认监听：

```text
http://127.0.0.1:32145
```

端点：

- `GET /health`：检查本地 API 是否启动。
- `GET /snapshot`：读取最新运行态快照。
- `GET /logs`：读取 `BepInEx/LogOutput.log` 尾部日志。

默认使用 `127.0.0.1`，用于减少代理工具、IPv6 和 `localhost` 解析带来的连接问题。若代理软件影响连接，请将 `127.0.0.1` 和 `localhost` 加入直连规则。

## 开发文档

- 开发约定：[docs/development-conventions.md](docs/development-conventions.md)
- 仓库状态：[docs/repo-memory.md](docs/repo-memory.md)
- 料理机制：[docs/tmi-cooking-mechanics-knowledge-base.md](docs/tmi-cooking-mechanics-knowledge-base.md)
- Addressables 映射：[docs/addressables-tag-mapping-playbook.md](docs/addressables-tag-mapping-playbook.md)
- 运行时读取：[mods/mystia-steward-bepinex/docs/RUNTIME_PROVIDER_NOTES.md](mods/mystia-steward-bepinex/docs/RUNTIME_PROVIDER_NOTES.md)

## 许可证与来源

本项目以 `AGPL-3.0-only` 发布。仓库源自 `Well2333/mystia-steward`，且原项目使用或派生自 `AnYiEE/touhou-mystia-izakaya-assistant`；后者标注为 `AGPL-3.0-only`。

详细说明见 [NOTICE](NOTICE) 和 [LICENSE](LICENSE)。

## 免责声明

本项目为非官方开源工具，仅用于学习与辅助决策，不隶属于游戏官方。游戏版本更新可能导致运行时读取路径、数据或规则变化。
