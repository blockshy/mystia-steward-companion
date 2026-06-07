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
- `修改` 页通过 `/inventory/set` 在 Unity 主线程写入当前运行时材料和酒水库存；用户仍需在游戏内保存才能持久化。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，接口按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪尾部内容，前端也只保留有限行数显示。
- Mod 默认写入 `BepInEx/config/BepInEx.cfg` 将 `[Logging.Console] Enabled=false`，并在 Windows 当前会话尝试隐藏控制台窗口；配置对下一次启动完全生效。
- 旧游戏内 IMGUI 面板默认关闭，仅作为回退方案。
- GitHub Actions 的发布工作流只在 `v*` tag 或手动 dispatch 时运行；版本构建等待用户明确指令。
- 默认热键 `F8` 和 `RS Click` 的主语义是游戏与伴随窗口焦点切换；伴随窗口聚焦时由 Tauri 前端处理热键并切回游戏。手柄切换需要释放锁存和 1.2 秒后端防抖，防止同一次长按连续 toggle。
- 经营中稀客订单按首次捕获时间稳定排序；运行时捕获订单保留到明确移除、稀客离场或 6 小时硬上限，避免长时间未上菜时从伴随窗口消失。
- 运行时捕获订单维护 `ChangeVersion`；UI 控制器在版本变化后延迟 0.2 秒强制刷新经营数据并发布本地 API 快照。伴随窗口在 `经营中` 和稀客专注模式下以 750ms 轮询快照，其他页面保持 2 秒。
- 运行时稀客 ID 会先归一化为本地 `customer_rare.json` 身份；优先读取游戏 `DataBaseCharacter.GetAllMappedGuests()` 固定映射和 `GetSpecialGuestsAndMappedGuests()` 完整运行时稀客表，运行时表按游戏语言名称匹配本地唯一同名稀客，手工事件变体只作为兜底。带具体桌号的捕获订单只允许匹配同一桌活跃稀客，未入座 `desk=-1` 稀客不能保活旧订单。
- 诊断开启且经营数据扫描触发时，运行时固定数据会按主题写到诊断目录：`runtime-static-data.log` 映射稀客与 `aliasSource`、`runtime-tags.log` 标签和 TagRule、`runtime-database-diff.log` 核心食材/酒水/料理表对照与读取方式、`runtime-guests.log` 普客/稀客/事件变体、`runtime-izakayas.log` 场景和客人池。游戏数据库未初始化时每 5 秒重试，日志头部 `Complete: True` 表示读取成功。
- 稀客订单专注模式支持精简模式，精简模式隐藏推荐料理 Tag 并压缩推荐面板间距。

## 推荐排序口径

- 经营中料理推荐：分数降序 -> 总成本降序 -> 料理 ID 升序。
- 经营中酒水推荐：分数降序 -> 总成本降序 -> 酒水 ID 升序。
- 稀客和经营中主推荐列表只展示满足当前点单料理 Tag / 酒水 Tag 的结果；未满足点单的 fallback 不得混入正式推荐。料理推荐优先 3 分以上候选，但低于 3 分且满足点单的料理仍要作为兜底显示。
- 经营中订单显示顺序：首次出现时间升序；新订单不应插到已有订单前面。
- 推荐行需要显示库存数量；料理行需要显示厨具和基础配方。
