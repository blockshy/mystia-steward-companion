# 夜雀掌柜 BepInEx Mod

这是“夜雀掌柜”的《东方夜雀食堂》BepInEx IL2CPP Mod。Mod 会读取游戏当前运行时数据，并通过独立伴随窗口显示普客、稀客和夜间经营推荐。

开发环境、构建流程、运行时反射和本地 API 说明见 [README.dev.md](README.dev.md)。

## 功能说明

- 实时读取游戏内料理、酒水、食材、流行标签、明星店状态和夜间经营数据。
- 按地区显示普客料理与酒水推荐。
- 按稀客和点单词条显示料理、加料与酒水推荐。
- 夜间经营中自动检测当前稀客、桌位和稀客点单，并给出当前点单推荐。
- 独立伴随窗口支持 `设置`、`普客`、`稀客`、`经营中`、`日志` 页面。
- `经营中` 页支持稀客订单专注模式，只显示当前点单推荐。
- 推荐结果会显示食材和酒水剩余数量。
- 支持中文和英文显示。
- 不修改游戏存档。

## 安装 BepInEx

1. 找到游戏根目录。Steam 可通过“管理 -> 浏览本地文件”打开，目录中应能看到游戏 `.exe` 和 `GameAssembly.dll`。
2. 打开 BepInEx Bleeding Edge 下载页：<https://builds.bepinex.dev/projects/bepinex_be>。
3. 下载与系统和游戏架构匹配的 Unity IL2CPP 包。Windows 64 位通常选择：

```text
BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.*.zip
```

4. 将压缩包内容解压到游戏根目录。解压后应出现：

```text
BepInEx/
doorstop_config.ini
winhttp.dll
```

5. 启动游戏一次，等待 BepInEx 生成目录和 IL2CPP interop 程序集。首次启动可能较慢。
6. 关闭游戏，确认以下目录存在：

```text
BepInEx/config/
BepInEx/core/
BepInEx/interop/
BepInEx/plugins/
```

如果没有生成这些目录，通常是下载了非 IL2CPP 包、解压位置错误，或游戏没有成功启动过一次。

## 安装 Mod

获取安装包：

```text
MystiaSteward-BepInEx.zip
```

将压缩包中的 `MystiaSteward/` 整个目录放入游戏目录：

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

安装后启动游戏。控制台或 `BepInEx/LogOutput.log` 中应出现 `Mystia Steward loaded` 和本地 API 启动日志。

## 使用方式

- `F8`：打开或唤起独立伴随窗口。
- `RS Click`：手柄默认打开或唤起独立伴随窗口。
- `F9`：手动刷新当前运行时数据检测。
- `经营中`：查看当前稀客、稀客点单、推荐料理、推荐加料和推荐酒水。
- `稀客订单专注模式`：只显示当前点单推荐；没有点单时会显示等待提示。
- `日志`：读取 `BepInEx/LogOutput.log` 尾部内容，便于排查运行时读取和点单识别问题。

如果游戏还停留在标题、菜单或加载页面，伴随窗口会提示运行时数据不可用。进入游戏并加载进度后，Mod 会自动读取当前游戏状态，不需要选择存档文件。

## 独立伴随窗口

伴随窗口会在 Mod 加载后自动启动。它是一个独立桌面窗口，可以移动、缩放和置顶，不受游戏窗口边界限制。

窗口关闭按钮默认隐藏到系统托盘，而不是直接退出。可以通过以下方式重新打开：

- 在游戏内按 `F8` 或 `RS Click`。
- 使用系统托盘菜单 `显示夜雀掌柜`。
- 再次双击 `mystia-steward-companion.exe`。

如果游戏关闭、Mod 卸载或本地 API 连续断开，伴随窗口会自动退出，避免游戏结束后残留后台窗口。

## 常用配置

配置文件位于：

```text
BepInEx/config/com.tyukki.mystia-steward.cfg
```

常用项：

- `Language`：显示语言，支持 `zh-CN` 和 `en`。
- `ToggleKey`：独立窗口唤起热键，默认 `F8`。
- `ControllerToggleKey`：手柄唤起热键，默认 `JoystickButton9`，常见映射为 `RS Click`。
- `ReloadKey`：实时数据刷新热键，默认 `F9`。
- `Companion.AutoLaunch`：是否自动启动独立伴随窗口，默认开启。
- `Companion.ExecutablePath`：伴随窗口可执行文件路径；留空时自动从 Mod 目录和 `companion/` 子目录查找。
- `LocalApi.Port`：本地 API 端口，默认 `32145`。
- `SetConsoleUtf8`：加载 Mod 后尝试将 Windows 控制台切换到 UTF-8，默认开启。
- `EnableInGameOverlay`：是否启用旧游戏内 IMGUI 面板，默认关闭。

## 故障排查

- `F8` 无法打开独立窗口：确认 `mystia-steward-companion.exe` 位于 `BepInEx/plugins/MystiaSteward/companion/`，或在 `Companion.ExecutablePath` 中填写绝对路径。
- 一直显示运行时数据不可用：先确认已经进入游戏并加载进度；再打开 `日志` 页或查看 `BepInEx/LogOutput.log`。
- `经营中` 没有稀客或稀客点单：进入 `日志` 页查看扫描状态，并确认游戏内确实处于夜间经营流程。
- 控制台早期中文乱码：Mod 只能在自身加载后切换 UTF-8，不能修复 BepInEx preloader 已经输出的日志。需要启动阶段也正常显示时，可先在控制台执行 `chcp 65001` 再启动游戏。
- 需要旧游戏内面板：设置 `Ui.EnableInGameOverlay=true` 后重启游戏。

排查运行时识别问题时，请提供 `BepInEx/LogOutput.log` 和伴随窗口 `日志` 页内容。
