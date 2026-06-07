# mystia-steward-companion BepInEx Mod 开发说明

本文档面向开发者，记录本 Mod 的本地开发、构建、运行时读取和调试方式。用户安装和使用说明见 [README.md](README.md)。

## 项目结构

- `src/Core/`：推荐算法、数据模型和排序规则。
- `src/Save/`：运行时反射读取、兼容探测和推荐状态构造。
- `src/Ui/`：旧游戏内 IMGUI 回退面板、伴随窗口控制器和快照缓存。
- `src/Plugin/`：BepInEx 入口、配置和伴随窗口启动逻辑。
- `src/LocalApi/`：本地回环 API，供 Tauri 伴随窗口读取实时状态。
- `Data/`：打包进 Mod 的料理、酒水、食材、普客、稀客和 tag 数据。
- `References/`：本机编译引用 DLL，不提交到仓库。
- `tools/`：前置检查、数据同步、构建和打包脚本。

运行时读取说明见 [docs/RUNTIME_PROVIDER_NOTES.md](docs/RUNTIME_PROVIDER_NOTES.md)。

## 开发环境

Windows 上通常需要：

- .NET 6 SDK 或更新版本。
- Node.js 20+，并通过 Corepack 使用仓库固定的 `pnpm@10.10.0`。
- PowerShell 7。
- Rust stable、Microsoft C++ Build Tools 2022 或 Visual Studio “使用 C++ 的桌面开发”组件。
- Microsoft Edge WebView2 Runtime。
- 已安装并启动过一次 BepInEx Unity IL2CPP 的游戏目录。

推荐初始化命令：

```powershell
corepack enable
corepack prepare pnpm@10.10.0 --activate
winget install Rustlang.Rustup
```

Linux 验证 Tauri 构建时还需要：

```bash
sudo apt-get install -y pkg-config libwebkit2gtk-4.1-dev libayatana-appindicator3-dev librsvg2-dev libssl-dev libxdo-dev
```

## 构建引用

本项目不提交 BepInEx 和 Unity DLL。构建前只需要把 BepInEx、Il2CppInterop 和 Unity 引用复制到 `References/`，不需要也不应该复制 `Assembly-CSharp.dll`：

```text
mods/bepinex/
  References/
    BepInEx.Core.dll
    BepInEx.Unity.IL2CPP.dll
    0Harmony.dll
    Il2CppInterop.Runtime.dll
    Il2Cppmscorlib.dll
    UnityEngine.CoreModule.dll
    UnityEngine.IMGUIModule.dll
    UnityEngine.InputLegacyModule.dll
```

常见来源：

- `游戏根目录/BepInEx/core/`
- `游戏根目录/BepInEx/interop/`

复制完成后运行前置检查：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\preflight.ps1
```

Git Bash 可运行：

```bash
bash mods/bepinex/tools/preflight.sh
```

## 一键构建

PowerShell 7 从仓库根目录执行：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1
```

该脚本会依次执行 `pnpm install --frozen-lockfile`、`preflight.ps1`、数据同步、伴随窗口前端构建、Tauri 伴随窗口构建、Mod DLL 构建和安装包生成。
脚本开始时会先检查 `mods\bepinex\References` 中的 BepInEx/Unity 引用 DLL。若引用 DLL 放在其他目录，可显式传入：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

常用增量构建：

```powershell
# 跳过依赖安装
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -SkipInstall

# 只改 C# Mod，不重建伴随窗口前端和 Tauri 程序
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1 -SkipInstall -SkipFrontendBuild -SkipTauriBuild
```

如果修改了 `apps/companion/src/` 或 Tauri 窗口相关代码，不要使用 `-SkipTauriBuild`，否则安装包中的伴随窗口仍会使用旧产物。

## 拆分构建

需要拆分排查时，可从仓库根目录手动运行：

```bash
pnpm install
pnpm build
pnpm tauri:build
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

仅重新生成安装包：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\package-release.ps1
```

Linux 或 Git Bash：

```bash
bash mods/bepinex/tools/package-release.sh
```

常见产物：

```text
apps/companion/dist/
apps/companion/src-tauri/target/release/mystia-steward-companion(.exe)
apps/companion/src-tauri/target/release/bundle/nsis/*.exe
mods/bepinex/bin/Release/MystiaStewardCompanion.BepInEx.dll
mods/bepinex/dist/mystia-steward-companion-bepinex.zip
```

PowerShell 7 脚本固定生成 `.zip`；bash 脚本在系统没有 `zip` 时会改为生成 `.tar.gz`。打包脚本会在检测到 `apps/companion/src-tauri/target/release/mystia-steward-companion(.exe)` 时自动复制到安装包的 `companion/` 子目录。

## 本地发布

本地发布方案见仓库根目录的 `docs/local-release.md`。仓库不使用 GitHub Actions 自动构建 Release；版本发布需要在 Windows 本机构建完整产物后通过 GitHub CLI 上传。

GitHub Release 只上传以下资产：

- `mystia-steward-companion-bepinex.zip`
- `checksums.txt`

不上传 Tauri setup 安装器，避免用户误以为只安装桌面程序即可使用 Mod。

发布前检查：

- `gh auth status` 能正常显示已登录账号。
- `mods\bepinex\References` 中 8 个编译引用 DLL 齐全。
- 已运行 `mods\bepinex\tools\set-version.ps1` 并提交版本号变更。
- 若发布新版本，先提交版本号变更并创建或移动对应 tag，例如 `v1.0.1`。

### 同步版本号

以 `1.0.1` 为例：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\set-version.ps1 -Version 1.0.1

git add package.json apps\companion\src-tauri\Cargo.toml apps\companion\src-tauri\Cargo.lock apps\companion\src-tauri\tauri.conf.json mods\bepinex\src\Plugin\MystiaStewardCompanionPlugin.cs
git commit -m "chore(release): bump version to 1.0.1"
git push origin main
```

发布脚本会根据 `-Tag` 校验 `package.json`、`tauri.conf.json`、`Cargo.toml`、`Cargo.lock` 和 `PluginVersion`。如果版本不一致，脚本会失败并提示先同步版本。

### 发布新版本

以 `v1.0.1` 为例：

```powershell
git pull --ff-only origin main
git fetch --tags --force origin

pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.1 `
  -Title "v1.0.1" `
  -Notes "版本更新说明"
```

脚本会先执行完整构建，再用 `gh release create` 创建 Release 并上传 zip 与 checksums。

如果引用 DLL 不在 `mods\bepinex\References`，传入 `-ReferenceDir`：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.1 `
  -Title "v1.0.1" `
  -Notes "版本更新说明" `
  -ReferenceDir "D:\path\to\mystia-steward-companion-references"
```

### 更新已有版本资产

如果只需要修改已有 Release 的标题或发布说明，不需要重新构建：

```powershell
gh release edit v1.0.0 `
  --repo blockshy/mystia-steward-companion `
  --title "v1.0.0" `
  --notes "修正后的发布说明"
```

如果 Release 已存在，只想替换同名 zip 和 checksums，使用 `-Clobber`：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.0 `
  -Title "v1.0.0" `
  -Notes "首个正式版本" `
  -Clobber
```

如果已经运行过 `build-release.ps1`，只重新上传已有产物：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\publish-release.ps1 `
  -Tag v1.0.0 `
  -SkipBuild `
  -Clobber
```

### 清理旧安装器资产

如果历史 Release 里已经上传过 setup 安装器，需要手动删除一次：

```powershell
gh release delete-asset v1.0.0 mystia-steward-companion_1.0.0_x64-setup.exe `
  --repo blockshy/mystia-steward-companion `
  --yes

gh release delete-asset v1.0.0 Mystia.Steward.Companion_0.1.0_x64-setup.exe `
  --repo blockshy/mystia-steward-companion `
  --yes
```

之后重新运行发布脚本不会再上传 setup。

### 版本 tag

发布脚本不会自动创建或移动 Git tag。新版本发布前应显式处理 tag：

```powershell
git tag -a v1.0.1 -m "v1.0.1"
git push origin v1.0.1
```

如果需要修正尚未正式发布的 tag 指向：

```powershell
git tag -f -a v1.0.1 -m "v1.0.1"
git push --force origin v1.0.1
```

## 数据同步

结构化数据位于 `apps/companion/src/data/`。修改 JSON 数据后需要同步到 Mod：

```bash
bash mods/bepinex/tools/sync-data.sh
```

`build-release.ps1` 会默认执行同等同步逻辑。

## 运行时刷新行为

Mod 会定期检查当前页面和游戏运行时状态。进入游戏并加载进度后，推荐状态来自当前内存中的运行时对象，不读取 `.memory` 存档文件。

夜间经营中，`经营中 / Service` 页会读取 `GuestsManager`、稀客队列、`OrderController`、HUD、服务面板和桌位控制器中的稀客与订单。稀客进场、排队或入座后会先显示当前稀客；稀客点单后，工作台会按桌号列出稀客、料理词条和酒水词条，并复用稀客推荐算法计算候选料理、加料和酒水。

若 IL2CPP getter 无法读取订单列表，Mod 会继续尝试 `AllOrdersData` 和 `PeekOrders()`；若 tag ID 读取失败，会从稀客控制器的订单文本方法读取中文词条。

稀客推荐结果会按角色、点单词条、库存状态和加料上限缓存。自动刷新没有检测到库存变化时，不会在每个刷新周期重复枚举加料组合。

如果没有检测到运行时数据，普客和稀客推荐页只显示运行时数据不可用，不会回退到“全内容可用”状态，避免误以为库存和解锁内容已经同步。

开启经营诊断后，`night-business-diagnostics.log` 会额外输出 `Candidates` 和 `RecentRuntimeParseFailures`。前者记录被扫描到的 controller/order 候选、接纳状态和过滤原因；后者记录运行时订单捕获器最近未能解析为稀客订单的样本。排查映射稀客或特殊事件稀客时，优先查看这两段。

同一目录还会输出运行时固定数据快照，默认目录为 `BepInEx/config/MystiaStewardCompanion/`：

- `runtime-static-data.log`：`DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 运行时同名别名，日志中的 `aliasSource` 会标明归一化来源。
- `runtime-tags.log`：`DataBaseLanguage` 的料理/酒水标签文本、DLC 标签映射，以及 `DataBaseCore.TagRules`。
- `runtime-database-diff.log`：`DataBaseCore` 食材、酒水、菜品、料理运行时表，并附本地 JSON 名称、标签、价格对照；每个表会记录 `GetAllX` 方法读取结果，以及静态字典 fallback 的读取结果。
- `runtime-guests.log`：`DataBaseCharacter` 普客、稀客、映射稀客、原始稀客映射和 `GuestFoodEasterEggData` 类型/简单字段。
- `runtime-izakayas.log`：`DataBaseCore.GetAllIzakayas()` 或静态 `Izakayas` 字典读取到的经营场景标签、等级、普通/稀客池和刷新参数。

固定数据快照只在 `Diagnostics.EnableNightBusinessDiagnostics=true` 且 `NightBusinessReflectionProvider.LoadContext()` 被调用时写入；也就是说，通常需要进入游戏并让伴随窗口/Mod 触发一次经营数据刷新。若游戏的 `DataBaseCore`、`DataBaseLanguage` 或 `DataBaseCharacter` 尚未初始化，快照会记录缺失状态并按 5 秒间隔重试。判断读取成功时优先看日志头部 `Complete: True` 和 `Status` 中各类计数是否大于 0。

## 本地 API 与伴随窗口

Mod 默认监听：

```text
http://127.0.0.1:32145
```

端点：

- `GET /health`：检查本地 API 是否启动，不需要 token。
- `GET /snapshot`：读取最新运行态快照。快照由 Unity 主线程按自动刷新节奏生成，网络线程只返回缓存 JSON。
- `GET /logs/settings`：读取日志读取和经营诊断开关状态。
- `GET /logs/config?logAccess=true|false&diagnostics=true|false`：由伴随窗口回写日志和诊断开关。
- `GET /logs/open-folder?target=log|diagnostics`：打开对应日志目录。
- `GET /logs`：在 `LocalApi.ExposeLogs=true` 时读取 `BepInEx/LogOutput.log` 尾部日志，按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪。
- `GET /inventory/set?type=ingredient|beverage&id=ID&qty=数量`：在 Unity 主线程修改当前运行时材料或酒水库存。

除 `/health` 外，端点都需要 `X-Mystia-Steward-Companion-Token`。Token 由插件生成并保存在 BepInEx 配置中，启动伴随窗口时通过 `--token=` 参数传入 Tauri 后端。Tauri 伴随窗口会显示实时 Mod 工作台，包含 `概览`、`普客`、`稀客`、`经营中`、`修改`、`日志` 六个页签。它通过原生后端读取本地 API，不依赖浏览器或前端开发服务器。

代理工具注意事项：

- 默认使用 `127.0.0.1`，不要改成 `localhost`。
- 若代理扩展或系统代理拦截本地请求，将 `127.0.0.1`、`localhost` 和回环地址加入直连/绕过列表。
- 若伴随窗口无法连接，先确认日志中出现 `Local API listening at http://127.0.0.1:32145`，再检查端口占用。
- 由于接口使用 token 且不再开放通配 CORS，不建议直接用浏览器访问受保护端点；调试伴随窗口时使用 Tauri 运行环境。

## 输入处理

旧游戏内 IMGUI 面板默认关闭。启用后，面板脚本会释放锁定光标、消费 IMGUI 鼠标/键盘事件，并调用 `Input.ResetInputAxes()`，减少点击同时传递给游戏的情况。

如果游戏逻辑在更早阶段直接读取鼠标输入，后续需要通过 Harmony Hook 游戏输入逻辑才能完全拦截。

## 调试建议

- `preflight.ps1` 报 DLL 缺失：先启动一次已安装 BepInEx 的游戏，再从 `BepInEx/core` 和 `BepInEx/interop` 复制所需引用。
- 构建报 `Il2Cppmscorlib` 缺失：从 `游戏根目录/BepInEx/interop/Il2Cppmscorlib.dll` 复制到 `References/`。
- PowerShell 执行 `bash ...` 报 WSL `/bin/bash` 不存在：在 Windows 下改用对应 `.ps1` 脚本。
- 运行时数据不可用：查看设置页场景名、扫描状态和 `BepInEx/LogOutput.log`。
- `经营中` 没有稀客或点单：查看 `经营扫描 / Scan status`；如果 `manager=missing`，需要核对夜间经营管理器字段；如果 `guests>0` 但 `orders=0`，提供 `Generated Special Guest Order` 日志和扫描状态。

## 已知限制

- 构建依赖本机 `References/` 中的 BepInEx、Il2CppInterop 和 Unity DLL；这些 DLL 不提交到仓库。
- 运行时反射依赖游戏版本中的类型和字段名；如果游戏更新导致字段变化，需要根据导出的 `Assembly-CSharp` 项目调整 provider。
- 旧游戏内 UI 使用 Unity IMGUI，仅保留回退用途；主要交互应放在 Tauri 独立伴随窗口中。
