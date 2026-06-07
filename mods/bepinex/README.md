# BepInEx Mod 使用说明

本文档仅说明 Mod 安装、使用、配置和故障排查。开发环境、构建流程、运行时反射和本地 API 说明见 [README.dev.md](README.dev.md)。

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
mystia-steward-companion-bepinex.zip
```

将压缩包中的 `mystia-steward-companion/` 整个目录放入游戏目录：

```text
游戏根目录/
  BepInEx/
    plugins/
      mystia-steward-companion/
        MystiaStewardCompanion.BepInEx.dll
        Data/
        companion/
          mystia-steward-companion.exe
```

安装后启动游戏。控制台或 `BepInEx/LogOutput.log` 中应出现 `mystia-steward-companion loaded` 和本地 API 启动日志。

## 使用方式

- `F8`：在游戏与独立伴随窗口之间切换焦点；窗口隐藏或关闭到托盘时会重新显示。
- `RS Click`：手柄默认在游戏与独立伴随窗口之间切换焦点。
- `F9`：手动刷新当前运行时数据检测。
- `普客`：按当前地区显示料理和酒水推荐。
- `稀客`：按候选稀客和点单词条显示满足点单的推荐。
- `经营中`：按订单出现顺序查看当前稀客、桌位、点单词条、满足点单的推荐料理、推荐加料和推荐酒水。
- `稀客订单专注模式`：只显示当前点单推荐；可切换精简模式以减少 Tag 和行距，没有点单时显示等待提示。
- `修改`：修改当前运行时材料和酒水库存数量；修改后需要在游戏内保存才会持久化。
- `日志`：读取 `BepInEx/LogOutput.log` 尾部内容、按需开启经营诊断，并可打开日志文件夹；日志读取有行数和字节上限，不会在窗口内无限累积。

如果游戏还停留在标题、菜单或加载页面，伴随窗口会提示运行时数据不可用。进入游戏并加载进度后，Mod 会自动读取当前游戏状态，不需要选择存档文件。

`修改` 页会直接写入当前游戏运行时库存。建议在非夜间经营流程中使用；若在经营中修改，结果可能与实时消耗同时发生。

## 独立伴随窗口

伴随窗口会在 Mod 加载后自动启动。它是一个独立桌面窗口，可以移动、缩放和置顶，不受游戏窗口边界限制。

窗口关闭按钮默认隐藏到系统托盘，而不是直接退出。可以通过以下方式重新打开：

- 在游戏内按 `F8` 或 `RS Click`。
- 使用系统托盘菜单 `显示 mystia-steward-companion`。
- 再次双击 `mystia-steward-companion.exe`。

如果独立窗口当前获得焦点，再按 `F8` 或 `RS Click` 会隐藏伴随窗口并尝试切回游戏窗口。游戏关闭或 Mod 卸载时，插件会立即通知伴随窗口退出；本地 API 断开检测仍作为兜底，避免游戏结束后残留后台窗口。

## 常用配置

配置文件位于：

```text
BepInEx/config/com.tyukki.mystia-steward-companion.cfg
```

如果从旧版本升级，Mod 会在新配置不存在时自动复制旧配置到上述新文件名。

常用项：

- `Language`：显示语言，支持 `zh-CN` 和 `en`。
- `ToggleKey`：游戏与独立窗口焦点切换热键，默认 `F8`。
- `ControllerToggleKey`：手柄焦点切换热键，默认 `JoystickButton9`，常见映射为 `RS Click`。
- `ReloadKey`：实时数据刷新热键，默认 `F9`。
- `Companion.AutoLaunch`：是否自动启动独立伴随窗口，默认开启。
- `Companion.ExecutablePath`：伴随窗口可执行文件路径；留空时自动从 Mod 目录和 `companion/` 子目录查找。
- `LocalApi.Port`：本地 API 端口，默认 `32145`。
- `LocalApi.ExposeLogs`：是否允许伴随窗口读取 `BepInEx/LogOutput.log`，默认开启，可在 `日志` 页切换。
- `LocalApi.MaxLogLines`：日志页每次最多返回的日志行数，默认 `300`。
- `LocalApi.MaxLogBytes`：日志页每次最多扫描的日志尾部字节数，默认 `262144`。
- `BepInEx.DisableConsoleLogWindow`：启动后写入 `BepInEx/config/BepInEx.cfg`，将 BepInEx 控制台日志窗口设为下次启动关闭，默认开启。
- `BepInEx.HideConsoleWindow`：Mod 加载后尝试隐藏当前 Windows 控制台窗口，默认开启。
- `Diagnostics.EnableNightBusinessDiagnostics`：是否写入夜间经营诊断日志，默认关闭，可在 `日志` 页切换。
- `SetConsoleUtf8`：加载 Mod 后尝试将 Windows 控制台切换到 UTF-8，默认开启。
- `EnableInGameOverlay`：是否启用旧游戏内 IMGUI 面板，默认关闭。

开启经营诊断后，除 `night-business-diagnostics.log` 外，还会写入运行时固定数据快照：

- `runtime-static-data.log`：稀客事件变体映射表和运行时同名别名归一化来源。
- `runtime-tags.log`：料理/酒水标签文本和 TagRule。
- `runtime-database-diff.log`：食材、酒水、料理、菜品运行时数据与本地数据对照，并记录运行时表读取方式。
- `runtime-guests.log`：普客、稀客、映射稀客和特殊料理规则。
- `runtime-izakayas.log`：经营场景、场景标签和客人池。

这些文件不是游戏启动瞬间立即生成，而是在诊断开启且运行时经营数据扫描触发时读取；若游戏数据库尚未初始化，Mod 会每 5 秒重试。日志顶部 `Complete: True` 且 `Status` 中各项计数大于 0，表示本次固定数据读取成功。

## 故障排查

- `F8` 无法打开独立窗口：确认 `mystia-steward-companion.exe` 位于 `BepInEx/plugins/mystia-steward-companion/companion/`，或在 `Companion.ExecutablePath` 中填写绝对路径。若窗口已打开，`F8` 会在游戏和独立窗口之间切换。
- 一直显示运行时数据不可用：先确认已经进入游戏并加载进度；再到 `日志` 页临时开启日志读取。
- `经营中` 没有稀客或稀客点单：进入 `日志` 页开启经营诊断并查看扫描状态，确认游戏内确实处于夜间经营流程。
- 启动时仍短暂出现控制台：Mod 加载时控制台已经由 BepInEx 创建，本次启动只能尝试隐藏；`BepInEx.DisableConsoleLogWindow=true` 写入的配置会在下一次启动生效。
- 控制台早期中文乱码：Mod 只能在自身加载后切换 UTF-8，不能修复 BepInEx preloader 已经输出的日志。日常建议关闭控制台并在伴随窗口 `日志` 页查看 `LogOutput.log`。
- 需要旧游戏内面板：设置 `Ui.EnableInGameOverlay=true` 后重启游戏。

排查运行时识别问题时，请在 `日志` 页开启日志读取和经营诊断，然后提供 `BepInEx/LogOutput.log`、诊断日志和伴随窗口 `日志` 页内容。
