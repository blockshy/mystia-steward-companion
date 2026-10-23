# 冻结的纯业务参考实现

这里只有 C# 离线差分测试实际使用的 TypeScript 入口及其传递依赖，不属于客户端，不参与 Flutter 或 Mod 发布。入口由 `freeze-reference.mjs` 显式列出；不保留旧 React 页面、Hooks、HTTP API、实际 Worker 或 Tauri 运行入口。`companion/workers/order-recommendations.types.ts` 仅是领域结果类型声明。

来源固定为迁移前 `098f414a4ddacc2471e3ca04119f13e2f0733ba8`。除 `preferences.ts` 外逐字冻结；该混合文件只保留纯值声明，删除 localStorage、DOM 和 Tauri 调用。生成器使用 TypeScript AST 选择原始声明文本，不重写算法；清单记录原文件与投影双哈希。

校验需要本地 Git 对象库包含该精确提交；浅克隆若缺少对象，先补齐对应历史再运行，不把当前 `main` 或另一个近似版本当作替代。整理迁移分支提交时保留这一共同基点，不能因为压缩提交而重新冻结参考实现。

`node tests/reference-client/freeze-reference.mjs` 只验证固定提交、依赖闭包、投影内容和清单。`--write` 用于明确重建相同固定来源，不作为测试自动步骤。`node scripts/test-csharp-business.mjs` 使用它验证基线，再执行实际 C# 程序集与完整 JSON 差分。不要把当前实现复制回来作为预期结果，也不要从生产代码导入此目录。
