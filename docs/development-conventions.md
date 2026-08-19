# 开发约定

更新日期：2026-08-19

本文只记录跨模块开发规则。工具安装和构建命令见[本地开发与构建](local-development.md)，逐模块验证见[验证指南](validation-guide.md)，运行时细节从[文档索引](README.md)进入相应专题。

## 项目与命名

- 仓库、产品、包 slug、Tauri 产品名、安装目录、发布产物和用户可见项目名统一使用 `mystia-steward-companion`。
- C# 命名空间和标识符使用 `MystiaStewardCompanion`。
- 旧名称只允许出现在上游来源声明或仍处于有效期的一次性迁移中；迁移到期后直接删除旧类型、旧路径和旧逻辑。
- 游戏角色、场景、挑战和规则名称优先采用游戏运行时数据或程序集元数据中的正式名称。社区昵称不能成为业务 identity。
- 修改产品名、目录或产物名时，同步检查 README、帮助页、脚本、workflow、文档索引和发布清单。

## 模块边界

- 前端入口位于 `apps/companion/src/companion/ModWorkbench.tsx`，页面按 `pages/`、`domain/`、`hooks/`、`features/` 和 `workers/` 分层。
- 推荐核心位于 `apps/companion/src/recommendation-engine/`。页面不复制候选生成、硬过滤或排序算法；密集计算通过 Worker 执行。
- C# Mod 不引用 TypeScript。两端通过版本化、本地认证的结构化 API 交换快照和命令。
- 游戏运行时事实由 Mod 发布；前端不使用静态表、DOM 文本、HUD 可见性或超时来补全未知游戏状态。
- 游戏运行时目录由 Mod 从当前游戏对象构建；前端静态资料不能替代或反向生成该目录。

稳定组件和数据流见[项目架构](architecture.md)。

## TypeScript 与 React

- 使用 TypeScript strict 写法，避免 `any`；`src` 内导入统一使用 `@/` 别名。
- React 使用函数组件和 hooks。服务端状态、轮询生命周期和 UI 局部状态分离，不把高频运行态直接写入顶层组件。
- 页面组件负责组合和渲染，业务决策放入领域函数、store 或 Worker；测试应能脱离完整页面验证核心逻辑。
- 用户文案默认中文。内部英文标识可用于类型、字段、日志 key 和原始运行时名称，但不得直接泄漏为用户可见文本。
- 平衡值、Tag 规则和功能说明写入结构化数据或领域模块，不散落在 JSX。

伴随窗口的组件、布局、手柄和焦点约束见[伴随窗口 UI](companion-ui.md)。

## C#、Rust 与运行时代码

- 游戏对象只在 Unity 主线程读取或修改。网络线程和后台线程只处理不可变托管数据、签名和命令排队。
- 跨帧状态保存精确 identity、generation、revision 和必要标量，不缓存可失效的 IL2CPP wrapper。
- 运行时集合使用已验证的具体类型和读取器；集合形态、字段、值域、容量或 identity 不符合预期时整轮拒绝，不跳过坏项拼出部分真相。
- Harmony Hook 必须锁定具体声明类型、方法名、参数和返回类型。多个托管方法别名到同一空原生地址时不得分别安装 detour。
- Rust/Tauri 平台代码用 `cfg` 保持桌面与移动能力分离。Android 不继承桌面托盘、聚焦、鼠标穿透或单实例逻辑。
- 原生异常和不确定 mutation 原样进入失败状态；不得用日志成功、方法进入或预期结果推断副作用已提交。

具体订单、自动化和 UI ownership 分别见[订单捕获与生命周期](runtime-order-lifecycle.md)、[自动化运行时](automation-runtime.md)和[游戏 UI 集成](game-ui-integration.md)。

## Fail-closed 原则

- 未知不是 false，部分成功不是完整成功，UI 可见不是运行时所有权。
- 只有完整、唯一、同 generation 且可复核的证据才能开放业务或副作用；证据缺失时保持等待、暂停或不可用。
- 不通过名称、文本、路径、列表位置、托管 hash、浮点容差、固定延迟或扫描整个场景猜测 identity。
- 不用兼容性堆叠掩盖信息不足。无法确认的新来源应先记录诊断、补充反编译或实机证据，再替换旧路径。
- 不可逆操作前后都按该操作所属专题的精确 identity 重新检查；不确定结果必须锁存并阻止自动重放。

## 游戏分析证据

- 运行时行为依次用 metadata C#、BepInEx #783 interop、IDA/Hex-Rays 和实机日志交叉验证。
- 分析固定使用 `mods/bepinex/tools/il2cpp-analysis/` 的锁定工具链，输出放在仓库外；旧分析只做历史对比，不参与业务 fallback。
- Cpp2IL `dll_il_recovery` 中仅含 `ldnull; throw` 的方法不是可用源码，不得作为实现依据。
- 涉及游戏原生行为的改动必须在代码或专题文档中留下证据入口，并由对应 runtime smoke/audit 固定。

完整流程见[IL2CPP / IDA 分析工作流](il2cpp-analysis-workflow.md)。

## 配置、存储与协议

- 配置、JSON 文件和 API 只保留当前规范 schema、键名和路由。删除旧路径时同步删除旧类型、解析和测试。
- 本地文件写入采用有界输入、临时文件、原子替换和明确的未来 schema 拒绝；损坏文件不得静默覆盖。
- API 的只读与写入语义由 HTTP method 区分；任何写文件、改运行时、改控制权或生成诊断包的操作都不能伪装成 GET。
- 协议身份使用大小写敏感的 canonical 值；序列化、签名和内容 hash 必须稳定。
- 设备配置权威、订单 lifecycle、自动化 lease 和 UI target revision 是不同概念，不能共用一个 revision 或隐式转换。

详见[本地 API](local-api.md)和相应存储 smoke。

## 变更与验证

1. 先确认分支、工作区和远端基线，保留用户已有改动。
2. 找到该领域的唯一权威文档、实现入口和测试入口。
3. 删除被替代实现，再加入新实现；不要保留旧路径作为兜底。
4. 先运行最窄的专项验证，再运行与变更风险相称的 lint、build、Cargo、dotnet 或 Playwright。
5. 提交前检查用户 README、帮助页、文档索引、`SESSION_HANDOVER.md` 和本文件是否仍准确。

命令选择见[验证指南](validation-guide.md)。正式 Release 的分支、审批和远端事务只由[发布流程](local-release.md)负责。

## 文档维护

- `README.md` 与 `mods/bepinex/README.md` 面向用户，只说明功能、安装、操作和排障。
- `mods/bepinex/README.dev.md` 是 Mod 开发入口，只保留最短上手和专题导航。
- 本文只保存跨模块规范，不再收录工具版本、API 清单、Hook 清单、测试逐条断言或业务功能快照。
- 专题文档只记录本领域当前有效的事实。历史提交、已解决故障和阶段进度不进入长期文档。
- 新增或移动文档时同步[文档索引](README.md)和所有相对链接；不为旧路径保留跳转副本。
