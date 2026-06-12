# mystia-steward-companion BepInEx Mod 架构边界

## 目标

将 `mystia-steward-companion` 推荐逻辑接入《东方夜雀食堂》运行时，提供可打包发布的 BepInEx IL2CPP Mod 和独立桌面伴随窗口。

## 项目边界

- Mod 根目录：`mods/bepinex/`。
- 伴随窗口前端：`apps/companion/src/companion/`。
- 静态数据源：`apps/companion/src/data/`，发布前同步到 Mod `Data/`。
- C# 插件不引用 TypeScript 模块，也不编译引用 `Assembly-CSharp.dll`。

## 技术路线

1. Mod 从 `BepInEx/plugins/mystia-steward-companion/Data` 加载 JSON 静态数据。
2. 通过运行时反射读取游戏当前内存状态。
3. 将运行时料理、酒水、食材、流行标签、经营场景和订单转换为推荐状态。
4. 本地 API 返回快照与日志。
5. Tauri 伴随窗口读取本地 API 并渲染实时工作台。

## 发布验证

- `pnpm lint`
- `pnpm build`
- `pnpm tauri:build`
- `dotnet build mods/bepinex/MystiaStewardCompanion.BepInEx.csproj -c Release`
- `mods/bepinex/tools/build-release.ps1`

## 风险

- 游戏版本更新可能改变 IL2CPP 类型、字段或方法。
- Hook 和运行时反射必须集中在 provider 层，避免污染推荐算法。
- 本地 API 必须保持回环地址访问，减少代理和网络环境影响。
