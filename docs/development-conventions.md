# 开发约定与流程

更新日期：2026-06-09

## 代码边界

- 仓库只维护 BepInEx Mod 与 Tauri 伴随窗口，不再维护独立网站和存档导入页面。
- 伴随窗口入口为 `apps/companion/src/companion/ModWorkbench.tsx`，顶层挂载在 `apps/companion/src/App.tsx`。
- 推荐算法集中在 `apps/companion/src/lib/normal-recommend.ts`、`apps/companion/src/lib/rare-recommend.ts` 和 `apps/companion/src/lib/tags.ts`。
- 结构化数据以 `apps/companion/src/data/*.json` 为源头，构建时同步到 `mods/bepinex/Data/`。
- C# Mod 不引用 TypeScript 模块；共享数据只通过 JSON 同步。

## 命名约束

- 项目、产品名、安装目录、发布产物和用户可见项目引用统一使用 `mystia-steward-companion`。
- C# 命名空间和类型可使用 `MystiaStewardCompanion`。
- 旧名称只允许出现在明确的兼容迁移代码或上游来源说明中，例如旧 BepInEx 配置和旧 localStorage key 迁移。
- 修改路径或项目名时，必须同步更新 README、AGENTS、构建脚本、GitHub Actions 和相关 docs。

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
dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release
```

一键发布包：

```powershell
pwsh -ExecutionPolicy Bypass -File mods\bepinex\tools\build-release.ps1
```

该命令会生成发布包；除非用户明确要求，不要运行。

## GitHub Actions 与发布

- `.github/workflows/ci.yml` 仅支持手动触发，用于前端 lint 和 build 检查。
- 仓库不使用 GitHub Actions 自动构建 Release；不要新增 tag 自动构建 workflow。
- 版本发布采用本机 Windows 构建后通过 `gh` 上传，详细说明见 `docs/local-release.md`。
- 不要主动创建 tag 或发布 Release；版本构建必须等待用户明确指令。

## 运行时约束

- Mod 只读取当前游戏运行时数据，不读取 `.memory` 存档文件。
- 夜间经营订单优先使用运行时对象；日志捕获仅作为兼容和排障手段。
- 夜间经营订单必须按首次出现时间稳定显示；不得因桌号排序或推荐完整度排序让新订单插到旧订单前面。
- 稀客/经营中主推荐必须先满足点单料理 Tag 和酒水 Tag；不满足点单的 fallback 只能作为调试信息或明确标注的备选，不得进入正式推荐列表。料理推荐优先 `foodScore >= 3`，但必须保留“满足点单且低于 3 分”的候选作为正式兜底。
- 稀客料理展示排序以评价和经营体验优先：`foodScore` 后先比较加料种类数与库存资源压力，再比较料理售价、加料成本和 ID；不要用总成本降序代表“收益更高”。
- 已捕获且仍能匹配当前稀客的订单不得使用短时间缓存过期清理；只应在明确移除、确认上菜完成、稀客离场或长时间硬上限后消失。
- 本地 API 监听 `127.0.0.1`，避免代理工具干扰 `localhost`；除 `/health` 外，接口必须通过伴随窗口传入的 token 访问。
- 伴随窗口单实例控制监听 `127.0.0.1:32146`；热键逻辑必须先发送 `show`/`toggle`/`exit` 控制消息，控制端口不可达时才启动伴随进程，避免手柄快捷键重复创建窗口。
- `F8` 和 `RS Click` 默认用于在游戏和伴随窗口之间切换焦点；伴随窗口聚焦时由 Tauri 前端处理热键并调用后端按设置切回游戏窗口。焦点行为不能写死为隐藏窗口，必须支持保持伴随窗口悬浮只切焦点。手柄切换必须做释放锁存和可配置后端防抖，避免一次长按在两侧窗口间反复触发。伴随窗口内手柄焦点必须优先遵守 `data-gamepad-scope` 区域，不要让顶部页签横向移动跳入页面内容。
- 伴随窗口透明度通过 Tauri transparent window 和前端 CSS 背景透明度实现；文本、按钮和标签不要整体设置 opacity，以免影响可读性。
- 伴随窗口根滚动区域必须预留稳定纵向滚动条槽位，避免页面内容因滚动条出现或消失产生横向跳动。
- 伴随窗口滚动条样式必须跟随主题和窗口透明度；不要使用全局 `*::-webkit-scrollbar` 覆盖会刻意隐藏滚动条的导航栏。
- 伴随窗口内手柄导航应优先处理同一行内的左右移动；range 滑杆获得焦点时，左右方向应调节数值而不是跳到其他按钮。收藏按钮等点击后会改变 UI 状态的控件，应使用稳定 `data-gamepad-focus-key` 恢复焦点。
- 运行时库存修改必须排队到 Unity 主线程执行，避免本地 API 网络线程直接写游戏对象。
- 运行时库存修改页只保留快捷操作，当前为 `+10` 和 `99`；不要恢复自定义数量输入、`+1` 或单独“应用”按钮，除非用户明确要求。
- 自动化能力是实验性功能，必须由设置页总开关控制；总开关关闭时经营中页不显示自动化配置，也不执行任何自动化动作。自动化只处理当前排序第一笔稀客订单，子选项默认关闭但记忆用户上次配置。
- 自动化遇到厨具占用、目标成品未进入送餐盘、运行时订单或厨具对象短暂不可读等临时状态时，应保持可重试；不得因为一次失败永久停止。`出错时暂停` 只用于非临时错误。
- `BepInEx/LogOutput.log` 通过伴随窗口 `日志` 页读取，必须保留后端读取上限和前端显示上限，避免无限累积日志。
- BepInEx 控制台窗口由 Mod 写入 `BepInEx.cfg` 在下次启动关闭；当前启动只能在 Windows 上隐藏已创建的控制台窗口。
- 旧游戏内 IMGUI 面板仅保留回退用途；主要交互应放在独立伴随窗口。

## 文档维护

- 用户安装和使用写入 `mods/bepinex/README.md`。
- 开发和构建写入 `mods/bepinex/README.dev.md`。
- 机制或运行时读取路径变化时，同步更新 `docs/` 和 `mods/bepinex/docs/`。
- 用户可见功能、快捷键、设置项、自动化行为、页面布局或本地 API 变化后，必须在提交前同步文档。
- 版本发布前如果文档落后，先补文档再提交版本号并合并 `main`。
