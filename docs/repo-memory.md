# Repo Memory

## 当前项目定位

仓库已经收敛为《东方夜雀食堂》BepInEx IL2CPP Mod 与 Tauri 桌面伴随窗口。旧的浏览器工具、导入页面、路由和独立验收流程不再维护。

## 关键目录

- `mods/mystia-steward-bepinex/`：插件源码、本地 API、运行时读取、构建脚本和 Mod 文档。
- `apps/companion/src/`：伴随窗口 React 工作台、推荐算法、tag 规则、类型和结构化数据。
- `apps/companion/src-tauri/`：桌面伴随窗口壳。

## 开发事实

- Mod 不编译引用 `Assembly-CSharp.dll`，运行时通过反射读取游戏已加载的 IL2CPP interop 类型。
- `References/` 只放本机编译 DLL，不提交仓库。
- `tools/sync-data.sh` 和 `build-release.ps1` 会把 `apps/companion/src/data` 同步到 Mod `Data/`。
- 独立伴随窗口通过 `127.0.0.1:32145` 读取运行态；除 `/health` 外，本地 API 使用 `X-Mystia-Steward-Token` 授权。
- `修改` 页通过 `/inventory/set` 在 Unity 主线程写入当前运行时材料和酒水库存；用户仍需在游戏内保存才能持久化。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，接口按 `LocalApi.MaxLogLines` 和 `LocalApi.MaxLogBytes` 裁剪尾部内容，前端也只保留有限行数显示。
- Mod 默认写入 `BepInEx/config/BepInEx.cfg` 将 `[Logging.Console] Enabled=false`，并在 Windows 当前会话尝试隐藏控制台窗口；配置对下一次启动完全生效。
- 旧游戏内 IMGUI 面板默认关闭，仅作为回退方案。
- 经营中稀客订单按首次捕获时间稳定排序；运行时捕获订单保留到明确移除、稀客离场或 6 小时硬上限，避免长时间未上菜时从伴随窗口消失。
- 稀客订单专注模式支持精简模式，精简模式隐藏推荐料理 Tag 并压缩推荐面板间距。

## 推荐排序口径

- 经营中料理推荐：分数降序 -> 总成本降序 -> 料理 ID 升序。
- 经营中酒水推荐：分数降序 -> 总成本降序 -> 酒水 ID 升序。
- 经营中订单显示顺序：首次出现时间升序；新订单不应插到已有订单前面。
- 推荐行需要显示库存数量；料理行需要显示厨具和基础配方。
