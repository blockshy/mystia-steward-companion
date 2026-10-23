# 主线程性能诊断离线验证

在仓库根目录、锁定 .NET 环境执行：

```powershell
dotnet run --project tests/runtime-performance-diagnostics/RuntimePerformanceDiagnosticsSmoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project tests/runtime-order-highlight/RuntimeOrderHighlightSmoke.csproj -c Release -p:NuGetAudit=false
dotnet run --project tests/runtime-seat-highlight/RuntimeSeatHighlightSmoke.csproj -c Release -p:NuGetAudit=false
```

第一项验证生产窗口的 mean/max/count/slow、五秒汇总、8ms 慢调用阈值、错误即时输出、时钟回退和有界键数量；相邻 Update 间隔使用独立 50ms 阈值，验证暂停/恢复、场景/经营代次改变、日志重启首帧排除。后两项链接生产高亮服务：八订单注册表验证活动身份逐帧检查、非活动卡读取次数减少，以及新座位投影的代次隔离、同桌双声明和托管分配对比。

第一项还链接活跃经营的生产周期门禁：600 帧中保持原 750ms 刷新帧完全一致，将稳定场景的可读性探针从 600 次降到 14 次；覆盖未到期不查询、未就绪不推进截止时间、恢复即时探测、场景复位及当前可读性重新读取。`PERIODIC-READINESS-COUNTS` 只表示该探针次数，不代表其它实时身份检查被降低频率，也不代表实测帧率提升。

第一项同时验证 `MSC-FRAME-STALL v1` 的前帧关联：间隔与完整 Update（包含诊断 flush）、LateUpdate、最慢阶段、Mod 范围分配及全进程 CLR GC 次数差一一对应。使用确定性计数检查八个固定样本槽、阈值分布、未保留计数、跨输出窗口的当前帧保留、暂停/代次重置和常态零分配。相关样本不证明 GC 因果。

`SNAPSHOT-LOG-COUNTS`、`MAINTENANCE-COUNTS`、`SEAT-PROJECTION-ALLOC` 是离线操作数量/分配证据，不是游戏 FPS 或真实 IL2CPP 耗时。游戏内开启现有聚合日志后，`MSC-PERF-WINDOW v1` 每五秒输出一次实际主线程阶段统计及场景、订单数、支持状态，`MSC-FRAME-STALL v1` 补充最多八个慢间隔的前帧关联。嵌套阶段不能相加；日志未覆盖 Unity 本体渲染与第三方 Mod。
