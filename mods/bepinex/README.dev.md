# mystia-steward-companion BepInEx Mod 开发说明

本文档面向开发者，记录本 Mod 的本地开发、构建、运行时读取和调试方式。用户安装和使用说明见 [README.md](README.md)。

## 项目结构

- `src/Core/`：推荐算法、数据模型和排序规则。
- `src/Save/`：运行时反射读取、兼容探测和推荐状态构造。
- `src/Ui/`：伴随窗口控制器、运行时循环和快照缓存。
- `src/Plugin/`：BepInEx 入口、配置和伴随窗口启动逻辑。
- `src/LocalApi/`：本地回环 API，供 Tauri 伴随窗口读取实时状态。
- `References/`：本机编译引用 DLL，不提交到仓库。
- `tools/`：前置检查、构建和打包脚本。

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

该脚本会依次执行 `pnpm install --frozen-lockfile`、`preflight.ps1`、运行时数据模式提示、伴随窗口前端构建、Tauri 伴随窗口构建、Mod DLL 构建和安装包生成。
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
- 用户可见功能和开发约束已同步到 README 或 `docs/`。
- 若发布新版本，先提交版本号变更并创建或移动对应 tag，例如 `v1.0.1`。

Release Note 只写从上一个版本到当前版本新增的用户可见功能、优化和 BUG 修复。内部重构、文档、构建脚本、版本号变更不写入 Note；如果某个优化或修复只是本版本新增功能的二次调整，不单独列出，只在新增功能描述中体现最终能力。

### 同步版本号

以 `1.0.1` 为例：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\set-version.ps1 -Version 1.0.1

git add package.json apps\companion\src-tauri\Cargo.toml apps\companion\src-tauri\Cargo.lock apps\companion\src-tauri\tauri.conf.json mods\bepinex\src\Plugin\MystiaStewardCompanionPlugin.cs
git commit -m "chore(release): bump version to 1.0.1"
git push origin dev
```

版本号变更先进入 `dev`；确认版本可发布后，再合并到 `main`，并在 `main` 上执行发布脚本。

Linux 开发环境可使用：

```bash
bash mods/bepinex/tools/set-version.sh 1.0.1
```

发布脚本会根据 `-Tag` 校验 `package.json`、`tauri.conf.json`、`Cargo.toml`、`Cargo.lock` 和 `PluginVersion`。如果版本不一致，脚本会失败并提示先同步版本。

### 发布新版本

以 `v1.0.1` 为例：

```powershell
git checkout main
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

## 运行时数据源

推荐、库存名称、任务目标和自动化目标解析只使用游戏运行时读取到的 `RuntimeDataCatalog`。伴随窗口未连接游戏、游戏数据库未初始化或 `/snapshot.runtimeData.isComplete=false` 时，页面会显示等待运行时数据，不再回退到内置 JSON。

`apps/companion/src/data/` 中的历史 JSON 暂时保留为后续清理参考，不参与 Mod 启动、前端推荐或发布包。`mods/bepinex/tools/sync-data.sh` 已废弃为 no-op，`build-release.ps1` 和发布包不再同步或包含 `Data/*.json`。

## 运行时刷新行为

Mod 会定期检查当前页面和游戏运行时状态。进入游戏并加载进度后，推荐状态来自当前内存中的运行时对象，不读取 `.memory` 存档文件。

运行时固定数据读取成功后，C# 会把 `DataBaseCore`、`DataBaseCharacter` 和 `DataBaseLanguage` 中的料理、食材、酒水、普客、稀客和 tag 映射构造成 `RuntimeDataCatalog`，写入本地 API 快照并切换 C# 推荐仓库到运行时仓库。伴随窗口概览页的“推荐数据”显示“游戏运行时”时，表示前端推荐算法已经获得完整运行时数据。

夜间经营中，`经营中 / Service` 页会读取 `GuestsManager`、稀客队列、`OrderController`、HUD、服务面板和桌位控制器中的稀客与订单。页面顶部只展示经营场景、扫描状态、推荐数据、厨具与置顶状态等通用信息，随后用 `稀客` / `普客` 页签分区展示各自功能。稀客进场、排队或入座后会先显示当前稀客，并尽量读取 `GuestGroupController.GetFund`、`BaseFundCarry`、`MaxFundCarry` 等当前携带金钱信息；稀客点单后，工作台会按桌号列出稀客、料理词条和酒水词条，并复用稀客推荐算法计算候选料理、加料和酒水。普客订单读取到 `GameData.CoreLanguage.LanguageBase` 这类 IL2CPP 本地化对象时，必须过滤为无文本，不得把运行时类型名当作客人、料理或酒水名称展示。

若 IL2CPP getter 无法读取订单列表，Mod 会继续尝试 `AllOrdersData` 和 `PeekOrders()`；若 tag ID 读取失败，会从稀客控制器的订单文本方法读取中文词条。

稀客推荐结果会按角色、点单词条、库存状态、厨具快照、排序配置和加料上限缓存。自动刷新没有检测到相关变化时，不会在每个刷新周期重复枚举加料组合；排序配置变化必须进入缓存签名，否则用户调整排序后会继续看到旧顺序。

收藏数据由 Mod 本地 API 持久化到 `BepInEx/config/MystiaStewardCompanion/favorites.json`。前端只通过 `/favorites`、`/favorites/add-recipe`、`/favorites/remove-recipe`、`/favorites/add-beverage`、`/favorites/remove-beverage` 读写，不使用 localStorage 存储收藏，避免版本更新或 WebView 数据迁移时丢失。

如果没有检测到运行时数据，普客和稀客推荐页只显示运行时数据不可用，不会回退到“全内容可用”状态，避免误以为库存和解锁内容已经同步。

开启经营诊断后，`night-business-diagnostics.log` 会额外输出 `Candidates` 和 `RecentRuntimeParseFailures`。前者记录被扫描到的 controller/order 候选、接纳状态和过滤原因；后者记录运行时订单捕获器最近未能解析为稀客订单的样本。排查映射稀客或特殊事件稀客时，优先查看这两段。

同一目录还会输出运行时固定数据快照，默认目录为 `BepInEx/config/MystiaStewardCompanion/`：

- `runtime-static-data.log`：`DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 运行时同名别名，日志中的 `aliasSource` 会标明归一化来源。
- `runtime-tags.log`：`DataBaseLanguage` 的料理/酒水标签文本、DLC 标签映射，以及 `DataBaseCore.TagRules`。
- `runtime-database-diff.log`：`DataBaseCore` 食材、酒水、菜品、料理运行时表；每个表会记录 `GetAllX` 方法读取结果，以及游戏静态字典 fallback 的读取结果。
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
- `GET /snapshot`：读取最新运行态快照。快照由 Unity 主线程按自动刷新节奏生成，网络线程只返回缓存 JSON。快照包含推荐状态、夜间稀客订单、任务状态、经营投喂任务目标和普客订单诊断；任务状态优先遍历 `RunTimeScheduler.trackingMissions` 并调用只读 `RunTimeScheduler.ParseActiveMissionData()`，再结合全局 NPC、当前场景 NPC、`DaySceneMap`、当天/常驻 `RunTimeScheduler.scheduledEvents` 后置任务、跟踪交互物件、场景任务交互组件和未完成 `trackingMissions` fallback 补充来源，并优先从 `RunTimeDayScene.trackedNPCs` 反查 NPC 所在场景，缺失时用 `DataBaseDay.RefNPC().possibleDestinations` 解析可能场景。夜间经营时会通过 `ContainsSpecialNPCServeInWorkMission()` 读取当前稀客是否有已接取的投喂任务指定料理；普客诊断会扫描 HUD 订单和经营管理器桌位订单。
- `GET /logs/settings`：读取日志读取、经营诊断和 BepInEx 原生日志窗口开关状态。
- `GET /logs/config?logAccess=true|false&diagnostics=true|false&nativeConsole=true|false`：由伴随窗口回写日志、诊断和 BepInEx 原生日志窗口开关；`nativeConsole` 会同时尝试显示/隐藏当前 Windows 控制台，并写入下一次启动的 `BepInEx.cfg`。
- `GET /logs/open-folder?target=log|diagnostics`：打开对应日志目录。
- `GET /logs`：在 `LocalApi.ExposeLogs=true` 时读取 `BepInEx/LogOutput.log` 尾部日志，按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪。
- `GET /inventory/set?type=ingredient|beverage&id=ID&qty=数量`：在 Unity 主线程修改当前运行时材料或酒水库存。
- `GET /inventory/bulk-set?type=ingredient|beverage&ids=ID1,ID2&qty=数量`：批量修改当前运行时材料或酒水库存；用于修改页的材料/酒水批量设为 `99`，只在批量结束后刷新一次运行时快照。
- `GET /orders/prepare-next?...`：按伴随窗口传入的稀客订单执行准备步骤，可组合取酒、开始料理、收取料理和收藏限定。
- `GET /logs/automation`：读取 `BepInEx/config/MystiaStewardCompanion/automation-jobs.log` 尾部内容，返回结构与 `/logs` 一致，受日志读取开关和读取上限控制。
- `GET /logs/export-diagnostics?open=true`：生成诊断 zip，包含 manifest、当前 snapshot、`LogOutput.log` 尾部、自动化作业日志尾部和诊断目录中的 `.log` 尾部；`open=true` 会打开诊断包目录。
- `GET /orders/complete-first?...`：按伴随窗口传入的稀客订单匹配送餐盘内容并尝试完成订单。
- `GET /orders/normal/complete-first?...`：按请求中的订单 key、桌位和料理处理一笔普客订单。普客自动化只负责按订单料理开始制作，并在完成后通过 `IzakayaConfigure.StoreFood()` 把料理写入游戏料理暂存容器；不处理酒水，不写入 `ServFood/ServBeverage/ServedFoodInAir`，也不触发订单评价。
- `GET /rare-guests/invite-all`：排队到 Unity 主线程，读取游戏原生 `SpecialGuestControlled` 可邀请候选，向受控稀客列表追加新的 `PreControl` 条目并持久化到 `RunTimePlayerData`。该端点不直接刷出稀客，后续仍由游戏经营准备流程处理。

除 `/health` 外，端点都需要 `X-Mystia-Steward-Companion-Token`。Token 由插件生成并保存在 BepInEx 配置中，启动伴随窗口时通过 `--token=` 参数传入 Tauri 后端。Tauri 伴随窗口会显示实时 Mod 工作台，包含 `概览`、`普客`、`稀客`、`经营中`、`任务`、`修改`、`日志`、`设置` 八个页签。它通过原生后端读取本地 API，不依赖浏览器或前端开发服务器。

伴随窗口的自动化能力只在前端 `设置` 页总开关开启后运行。稀客并发、普客并发、稀客送餐盘等待、普客保温箱复查、最大重试和最大回退都由 `CompanionPreferences` 配置控制，默认值分别为 `2`、`3`、`30s`、`45s`、`3`、`2`；完成订单评价每轮仍最多执行 1 笔。经营中订单排序支持点单顺序和稀客分组，必须同时影响经营中列表、专注模式、游戏界面置顶和自动化选单；料理/酒水排序配置会影响稀客页、经营中页、专注模式和自动化选单，新增排序项时需要同时覆盖这些入口。`优先任务料理` 开启时，当前稀客已接取投喂任务的指定料理可提升到正式推荐第一位，并会影响自动化选单，但仍必须通过解锁、库存和缺失厨具过滤。稀客正式推荐料理为空时，自动化允许使用“喜好备选”料理并在诊断中标识；收藏限定开启时，备选料理也必须命中收藏。稀客与普客自动化的阶段配置必须独立保存和独立传参：稀客使用 `autoPrep*` 配置，普客使用 `autoNormal*` 配置；普客只保留开始料理、收至保温箱和出错暂停开关，不得复用稀客取酒或完成订单开关。自动开始料理固定尝试完成原生 QTE 奖励结算，不提供跳过开关。普客自动化需要按订单 key 维护独立状态，非临时错误只暂停对应普客订单，不得暂停稀客自动化或其他普客订单；已进入制作中的普客料理必须绑定目标订单/桌位，后续轮询检测到 pending 后只能等待，不得在同类多个厨具上重复开始同一订单料理。普客订单变化需要立即触发一次处理，常规重复轮询仍需节流。C# pending target 和短期已收取回执都要优先保存并匹配 `OrderKey`，避免桌位复用或同料理多单时串单。稀客与普客的开锅请求必须经过前端同一轮厨具预约表，预约容量来自当前已摆放厨具快照；同类厨具容量不足时，普客待处理订单优先保留容量，稀客订单进入等待态并继续处理不占厨具的取酒/完成步骤。稀客完成订单流程必须支持料理和酒水单项先送达：只要送餐盘中存在目标项，就可写入 `ServFood` 或 `ServBeverage` 并调用 `IzakayaTray.Deliver()` 释放送餐盘格子；只有 `get_IsFullfilled()` 为真时才能调用 `EvaluateOrder()`。子选项默认关闭并记忆用户上次配置。临时失败例如厨具占用、运行时对象暂不可读，应保持可重试，不应永久停止自动任务；非临时错误在对应订单类型的 `出错时暂停` 开启时才暂停当前订单。前端状态机只将取酒、开锅、收取、单项送达、写入订单和触发评价视为真实进展；稀客目标料理/酒水超过等待阈值仍未进入送餐盘且未送达时，需要回退到上一实际步骤重新执行，并在达到回退上限后按设置暂停。普客已开锅但暂未进入暂存容器时，应先重新确认后端 pending 待收取任务；后端仍在制作时刷新等待时间，不增加回退或暂停，后端已完成收取时用短期回执标记已收至保温箱，只有 pending 消失且需要重新开锅时才重新发起制作。稀客页下拉选项不再按存档进度集合过滤，只按经营场景、可读名称和可用 Tag 过滤。

稀客自动化诊断由前端状态机维护，每个当前候选订单都要暴露当前步骤、下次动作、已开锅、已取酒、重试/回退次数、最近原因和暂停状态。普客自动化也要按订单 key 展示下次复查、开锅、保温箱收取和订单已有料理/酒水状态，避免只靠长文本判断卡住位置。`重试` 只解除该订单暂停并保留已完成阶段，`重置` 删除该订单本地状态并在下一轮重新判断；两者都不得影响其他稀客订单或普客订单状态。

伴随窗口直接双击启动时通常没有本地 API Token。前端必须停留在未授权状态，不得高频请求 `/snapshot` 或 `/logs`；用户修改端点输入框时也不得立即重连，只有点击 `连接` 或从游戏启动参数收到新 token 后才恢复轮询。连接失败后使用递增退避，允许用户点击 `停止` 暂停自动重连。

普客订单自动化仍是实验性功能。伴随窗口会显示当前 UI 订单里识别到的普客桌位、料理、酒水和完成状态；设置页开启自动化总开关后，还需要在经营中自动化面板开启“启用普客处理”，并至少开启自动开始料理或自动收取料理中的一个阶段，之后会自动处理按首次出现时间排序的未满足普客订单，不再需要点击手动处理按钮。普客流程只制作料理并收至游戏料理暂存容器，最终送达和酒水处理保留给玩家走游戏原生操作，避免绕过游戏进餐动画、Buff 和订单状态机。`ServedFoodInAir` 属于订单待送达状态，`CookController` 也不是独立保温箱，普客自动化不得写入这些字段。

自动化诊断文件 `BepInEx/config/MystiaStewardCompanion/automation-jobs.log` 由 C# 侧写入，记录开锅成功/失败、pending 收取、pending 移除和目标订单信息，约 1 MB 自动轮换为 `.1`。伴随窗口 `日志` 页通过 `/logs/automation` 读取并解析该文件，显示 action、target、桌号、订单 key、料理和最近消息；`导出诊断包` 通过 `/logs/export-diagnostics` 生成 zip，所有日志内容都只取尾部上限。该日志只用于排查，不得让写入、读取或打包失败影响自动化或游戏运行。

代理工具注意事项：

- 默认使用 `127.0.0.1`，不要改成 `localhost`。
- 若代理扩展或系统代理拦截本地请求，将 `127.0.0.1`、`localhost` 和回环地址加入直连/绕过列表。
- 若伴随窗口无法连接，先确认日志中出现 `Local API listening at http://127.0.0.1:32145`，再检查端口占用。
- 由于接口使用 token 且不再开放通配 CORS，不建议直接用浏览器访问受保护端点；调试伴随窗口时使用 Tauri 运行环境。

## 输入处理

游戏内不再保留 IMGUI 面板。Mod 在游戏侧只处理 F8/RS Click 热键、后台读取、本地 API 和自动化执行；用户交互全部放在 Tauri 独立伴随窗口中。伴随窗口获得焦点时由独立进程消费鼠标、键盘和手柄输入，不需要拦截游戏内 IMGUI 事件。

## 调试建议

- `preflight.ps1` 报 DLL 缺失：先启动一次已安装 BepInEx 的游戏，再从 `BepInEx/core` 和 `BepInEx/interop` 复制所需引用。
- 构建报 `Il2Cppmscorlib` 缺失：从 `游戏根目录/BepInEx/interop/Il2Cppmscorlib.dll` 复制到 `References/`。
- PowerShell 执行 `bash ...` 报 WSL `/bin/bash` 不存在：在 Windows 下改用对应 `.ps1` 脚本。
- 运行时数据不可用：查看设置页场景名、扫描状态和 `BepInEx/LogOutput.log`。
- `经营中` 没有稀客或点单：查看 `经营扫描 / Scan status`；如果 `manager=missing`，需要核对夜间经营管理器字段；如果 `guests>0` 但 `orders=0`，提供经营诊断中的 `Sources`、`Candidates` 和 `RecentRuntimeParseFailures`。

## 已知限制

- 构建依赖本机 `References/` 中的 BepInEx、Il2CppInterop 和 Unity DLL；这些 DLL 不提交到仓库。
- 运行时反射依赖游戏版本中的类型和字段名；如果游戏更新导致字段变化，需要根据导出的 `Assembly-CSharp` 项目调整 provider。
- 伴随窗口是唯一用户界面；游戏内不再提供备用 IMGUI 面板。
