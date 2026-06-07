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
- `BepInEx/LogOutput.log` 读取和夜间经营诊断默认关闭，由伴随窗口 `日志` 页按需开启。
- 旧游戏内 IMGUI 面板默认关闭，仅作为回退方案。

## 推荐排序口径

- 经营中料理推荐：分数降序 -> 总成本降序 -> 料理 ID 升序。
- 经营中酒水推荐：分数降序 -> 总成本降序 -> 酒水 ID 升序。
- 推荐行需要显示库存数量；料理行需要显示厨具和基础配方。
