# 原生列表按点单顺序置顶专项

运行方式（先按分支说明加载锁定开发环境）：

```powershell
dotnet run --project tests/runtime-ui-list-order/RuntimeUiListOrderSmoke.csproj -c Release -p:NuGetAudit=false
```

本项目直接编译生产 `RuntimeUiListOrderMutation`、`RuntimeUiListOrderService`、目标集合与生命周期优先级查询。只把原生集合、Harmony注册、日志和外围作用域替换成可观测边界，不复制排序实现。

主要覆盖：

- 八笔混合普客/稀客订单按生命周期排列，早单结束、空槽复用、共享物品使用最早剩余声明。
- 料理、食材、酒水的当前目标置顶，非目标保留游戏相对次序，同单多种食材也保持原次序。
- 排列只置换原对象，不增删库存条目，不改数量；相同料理ID的不同加料行按精确指针声明分开排序。
- 重复刷新不写已排好的列表；空目标、关闭列表辅助与空列表不写入。
- 非法类型、身份、数量、缺失成员、重复指针与非法优先级在写入前拒绝。
- 列表/目标读取途中变化、写入前抛错、写入后抛错、静默漏写和读回不符；恢复原序时不覆盖外部新增或修改的成员。
- 精确原生形态的三个postfix直接调用；料理排序明确安排在加料服务之后。
- 首次完整刷新直接进入食材分类，不通过 `UpdateIngField`；四个列表必须逐一属于当前面板，非库存来源、非酒水模式、其他面板和非主线程不写入。
- 键值对使用 `class : Il2CppSystem.ValueType` 表达真实装箱形态；食材与酒水仅允许走 `System_Collections_IList_set_Item(Int32, Il2CppSystem.Object)`，引用型料理仍走泛型索引器。危险的泛型键值对setter会在夹具中模拟写后身份损坏，使旧实现明确失败；正常排列与异常恢复均须保持对象和库存数量。

`NativeBridgeStubs.cs` 的类型名、包装继承关系、属性和方法参数对应已确认的interop结构，但它仍是托管夹具。该项目**不安装Harmony，不运行Unity或真实游戏**，通过不代表IL2CPP注入、实际控件刷新及游戏视觉通过。

对已安装和构建参考Interop程序集，另运行以下只读合同检查（显式提供当前环境实际路径）：

```powershell
./tests/runtime-ui-list-order/verify-interop-contract.ps1 `
  -InteropAssemblies $GameInteropAssembly,$ReferenceInteropAssembly `
  -CecilAssembly $CecilAssembly
```

该探针使用Cecil读取PE元数据及CIL，不执行游戏程序集。它核对真实键值对为CLR引用包装、泛型setter缺少原生拆箱、非泛型setter参数为Object，以及路径与SHA256，防止仅凭托管夹具推断原生封送安全。生成器升级使这些合同变化时应重新审查；检查通过不等于运行时游戏验收通过。

作用域、权威代次与发布租约由 `ui-pinning-runtime` 的生产服务专项验证；精确变体映射由 `runtime-target-recipe-variant` 验证。本项目仅消费这两个外围边界的明确契约，以单独验证原生列表反射桥与事务排序。
