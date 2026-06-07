# 开发约定与流程

更新日期：2026-06-07

## 代码边界

- 仓库只维护 BepInEx Mod 与 Tauri 伴随窗口，不再维护独立网站和存档导入页面。
- 伴随窗口入口为 `apps/companion/src/companion/ModWorkbench.tsx`，顶层挂载在 `apps/companion/src/App.tsx`。
- 推荐算法集中在 `apps/companion/src/lib/normal-recommend.ts`、`apps/companion/src/lib/rare-recommend.ts` 和 `apps/companion/src/lib/tags.ts`。
- 结构化数据以 `apps/companion/src/data/*.json` 为源头，构建时同步到 `mods/mystia-steward-bepinex/Data/`。
- C# Mod 不引用 TypeScript 模块；共享数据只通过 JSON 同步。

## 编码规范

- TypeScript 使用 strict 写法，避免 `any`。
- `src` 内导入统一使用 `@/` 别名。
- React 代码使用函数组件和 hooks。
- 面向用户的文案默认使用中文；Mod UI 需要同时保留中文和英文入口。
- 不在组件中硬编码平衡值，优先更新结构化数据和类型化逻辑。

## 构建验证

常规检查：

```bash
pnpm lint
pnpm build
```

伴随窗口：

```bash
pnpm tauri:build
```

BepInEx 插件：

```bash
dotnet build mods/mystia-steward-bepinex/MystiaSteward.BepInEx.csproj -c Release
```

一键发布包：

```powershell
powershell -ExecutionPolicy Bypass -File mods\mystia-steward-bepinex\tools\build-release.ps1
```

## 运行时约束

- Mod 只读取当前游戏运行时数据，不读取 `.memory` 存档文件。
- 夜间经营订单优先使用运行时对象；日志捕获仅作为兼容和排障手段。
- 夜间经营订单必须按首次出现时间稳定显示；不得因桌号排序或推荐完整度排序让新订单插到旧订单前面。
- 稀客/经营中主推荐必须先满足点单料理 Tag 和酒水 Tag；不满足点单的 fallback 只能作为调试信息或明确标注的备选，不得进入正式推荐列表。
- 已捕获且仍能匹配当前稀客的订单不得使用短时间缓存过期清理；只应在明确移除、确认上菜完成、稀客离场或长时间硬上限后消失。
- 本地 API 监听 `127.0.0.1`，避免代理工具干扰 `localhost`；除 `/health` 外，接口必须通过伴随窗口传入的 token 访问。
- 伴随窗口单实例控制监听 `127.0.0.1:32146`；热键逻辑必须先发送 `show`/`toggle`/`exit` 控制消息，控制端口不可达时才启动伴随进程，避免手柄快捷键重复创建窗口。
- `F8` 和 `RS Click` 默认用于在游戏和伴随窗口之间切换焦点；伴随窗口聚焦时由 Tauri 前端处理热键并调用后端切回游戏窗口。手柄切换必须做释放锁存和后端防抖，避免一次长按在两侧窗口间反复触发。
- 运行时库存修改必须排队到 Unity 主线程执行，避免本地 API 网络线程直接写游戏对象。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，必须保留后端读取上限和前端显示上限，避免无限累积日志。
- BepInEx 控制台窗口由 Mod 写入 `BepInEx.cfg` 在下次启动关闭；当前启动只能在 Windows 上隐藏已创建的控制台窗口。
- 旧游戏内 IMGUI 面板仅保留回退用途；主要交互应放在独立伴随窗口。

## 文档维护

- 用户安装和使用写入 `mods/mystia-steward-bepinex/README.md`。
- 开发和构建写入 `mods/mystia-steward-bepinex/README.dev.md`。
- 机制或运行时读取路径变化时，同步更新 `docs/` 和 `mods/mystia-steward-bepinex/docs/`。
