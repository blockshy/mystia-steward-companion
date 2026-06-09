# Repo Memory

## 当前项目定位

仓库项目名统一为 `mystia-steward-companion`，已经收敛为《东方夜雀食堂》BepInEx IL2CPP Mod 与 Tauri 桌面伴随窗口。旧的浏览器工具、导入页面、路由和独立验收流程不再维护。

## 关键目录

- `mods/bepinex/`：插件源码、本地 API、运行时读取、构建脚本和 Mod 文档。
- `apps/companion/src/`：伴随窗口 React 工作台、推荐算法、tag 规则、类型和结构化数据。
- `apps/companion/src-tauri/`：桌面伴随窗口壳。

## 开发事实

- Mod 不编译引用 `Assembly-CSharp.dll`，运行时通过反射读取游戏已加载的 IL2CPP interop 类型。
- 用户可见项目名、安装目录和发布产物使用 `mystia-steward-companion`；旧名称只保留在兼容迁移和上游来源说明中。
- `References/` 只放本机编译 DLL，不提交仓库。
- `tools/sync-data.sh` 和 `build-release.ps1` 会把 `apps/companion/src/data` 同步到 Mod `Data/`。
- 独立伴随窗口通过 `127.0.0.1:32145` 读取运行态；除 `/health` 外，本地 API 使用 `X-Mystia-Steward-Companion-Token` 授权。
- 伴随窗口控制端口固定为 `127.0.0.1:32146`，支持 `show`、`toggle`、`exit` 消息；Mod 热键应先通知已有窗口，控制端口不可达时才启动新进程。
- 伴随窗口会在 Tauri app data 目录保存 `window-state.txt`，记录外框位置和内框尺寸；启动时恢复大小和仍在显示器范围内的位置，防止换显示器后窗口离屏。
- 伴随窗口 `设置` 页负责窗口透明度、焦点切换行为、切换冷却时间、置顶、主题、手柄导航、稀客专注模式显示数量和实验性自动化总开关。透明度通过 Tauri transparent window + CSS 背景 alpha 实现，文字不随背景变淡。
- 伴随窗口根滚动区域固定预留纵向滚动条槽位，避免页面高度变化时滚动条挤占宽度造成内容横向跳动；窗口、下拉和日志滚动条使用主题色并跟随透明度。
- 焦点切换支持两种模式：隐藏伴随窗口再聚焦游戏，或保持伴随窗口悬浮并只聚焦游戏。保持悬浮依赖窗口置顶，独占全屏游戏可能覆盖置顶窗口，推荐窗口化或无边框窗口化。
- 伴随窗口退出跟随不只依赖本地 API `/health` 失联，还会监控启动参数中的 `--game-pid`。游戏窗口 X、游戏内退出按钮、或 Unity 退出阶段未及时发送 `exit` 控制消息时，都应由 PID 监控兜底关闭伴随窗口。
- `修改` 页通过 `/inventory/set` 在 Unity 主线程写入当前运行时材料和酒水库存；页面只保留 `+10` 和 `99` 快捷按钮，用户仍需在游戏内保存才能持久化。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，接口按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪尾部内容，前端也只保留有限行数显示。
- Mod 默认写入 `BepInEx/config/BepInEx.cfg` 将 `[Logging.Console] Enabled=false`，并在 Windows 当前会话尝试隐藏控制台窗口；配置对下一次启动完全生效。
- 旧游戏内 IMGUI 面板默认关闭，仅作为回退方案。
- 仓库不使用 GitHub Actions 自动构建 Release；`.github/workflows/ci.yml` 只保留手动前端检查。版本发布采用 Windows 本机构建后由 GitHub CLI 上传。
- 默认热键 `F8` 和 `RS Click` 的主语义是游戏与伴随窗口焦点切换；伴随窗口聚焦时由 Tauri 前端处理热键并按设置切回游戏。手柄切换需要释放锁存和可配置后端防抖，防止同一次长按连续 toggle；默认冷却时间为 800ms。
- 伴随窗口内手柄导航由 `apps/companion/src/companion/use-gamepad-navigation.ts` 管理：左摇杆/十字键移动焦点，`A` 确认，`B` 返回或退出专注模式，`LB/RB` 切页，`LT/RT` 滚动，`Y` 进入专注模式或切换精简模式，`X` 收藏当前推荐行。导航采用 `data-gamepad-scope` 分区，顶部页签栏左右键只在页签之间移动，向下进入当前页面内容；行内控件左右移动优先在当前 `data-gamepad-row` 内完成，range 滑杆左右键直接调值，推荐行和收藏按钮需要稳定 `data-gamepad-focus-key` 以便状态变化后回焦。
- 经营中稀客订单按首次捕获时间稳定排序；运行时捕获订单保留到明确移除、稀客离场或 6 小时硬上限，避免长时间未上菜时从伴随窗口消失。
- 运行时捕获订单维护 `ChangeVersion`；UI 控制器在版本变化后延迟 0.2 秒强制刷新经营数据并发布本地 API 快照。伴随窗口在 `经营中` 和稀客专注模式下以 750ms 轮询快照，其他页面保持 2 秒。
- 运行时稀客 ID 会先归一化为本地 `customer_rare.json` 身份；优先读取游戏 `DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 完整运行时稀客表，运行时表按游戏语言名称匹配本地唯一同名稀客，手工事件变体只作为兜底。本地缺失但运行时具备有效喜好 Tag 的稀客会合成为临时 `RuntimeRareCustomer`，供经营中订单推荐和伴随窗口稀客页使用；剧情 Intro/Parallel/Current、问号占位、隐藏图鉴、NeverCome、无喜好数据的角色不合成。带具体桌号的捕获订单只允许匹配同一桌活跃稀客，未入座 `desk=-1` 稀客不能保活旧订单。
- 诊断开启且经营数据扫描触发时，运行时固定数据会按主题写到诊断目录：`runtime-static-data.log` 映射稀客与 `aliasSource`、`runtime-tags.log` 标签和 TagRule、`runtime-database-diff.log` 核心食材/酒水/料理表对照与读取方式、`runtime-guests.log` 普客/稀客/事件变体、`runtime-izakayas.log` 场景和客人池。游戏数据库未初始化时每 5 秒重试，日志头部 `Complete: True` 表示读取成功。
- 稀客订单专注模式支持精简模式和料理/酒水显示数量配置；精简模式隐藏推荐料理 Tag 并压缩推荐面板间距，显示数量包含收藏置顶项。
- 实验性自动化由设置页总开关启用，经营中页提供子选项：自动完成订单、自动取酒、自动开始料理、自动收取料理、只处理收藏配方、出错暂停。自动化只处理当前排序第一笔稀客订单；临时失败应继续等待并重试，非临时失败才按配置暂停。

## 推荐排序口径

- 经营中/稀客料理推荐：满足点单 Tag -> 分数降序 -> 加料种类数升序 -> 资源压力升序 -> 料理售价降序 -> 加料成本升序 -> 料理 ID 升序。资源压力优先惩罚低库存材料，并对额外加料加权；不要再使用“总成本越高越靠前”作为收益判断。
- 经营中酒水推荐：分数降序 -> 酒水售价降序 -> 酒水 ID 升序。
- 稀客和经营中主推荐列表只展示满足当前点单料理 Tag / 酒水 Tag 的结果；未满足点单的 fallback 不得混入正式推荐。料理推荐优先 3 分以上候选，但低于 3 分且满足点单的料理仍要作为兜底显示。
- 稀客收藏保存在 `BepInEx/config/MystiaStewardCompanion/favorites.json`，按 `customerId + foodTag` 收藏料理方案（含加料 ID），按 `customerId + beverageTag` 收藏酒水。收藏只置顶当前仍在推荐候选中的结果，不绕过解锁、库存和点单 Tag 校验。
- 经营中订单显示顺序：首次出现时间升序；新订单不应插到已有订单前面。
- 推荐行需要显示库存数量；料理行需要显示厨具、基础配方和加料，并对这些定位信息做高亮。
