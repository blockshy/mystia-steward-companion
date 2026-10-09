# TypeScript 行为基线

`manifest.json` 记录改造前 main 的精确提交与冻结文件。这里的旧计算实现只供离线测试作为 oracle；React/Tauri、Mod 和 Flutter 不得导入，也不得将其作为业务服务异常时的自动回退。

`vite.config.ts` 将 `@/` 隔离到本目录的旧源码。`tests/csharp-recommendations/differential.mjs` 使用同一组 JSON 输入比对实际 C# 业务程序集，逐字段比较输出并检查输入未被修改。现有推荐审计保留对旧行为的断言，不代表新 C# 或游戏实机验收。

`freeze-reference.mjs` 仅在需要重建这份固定基线时使用，始终读取标明的 Git 提交，不读取当前工作树。`ModWorkbench.tsx` 和旧发布 Hook 仅供历史源码约束审计，不作为可运行客户端。
