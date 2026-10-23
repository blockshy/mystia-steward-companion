# 运行时图片隔离验证

`RuntimeGameMediaSmoke` 直接链接生产纯托管服务/存储。所有图片、身份与目录均为合成，路径只使用独占临时根；不引用 Unity、不连接游戏、不读真实配置。

使用锁定 .NET 6 运行时：

```powershell
dotnet build tests/runtime-game-media/RuntimeGameMediaSmoke.csproj -c Release -m:1 -p:UseAppHost=false
dotnet tests/runtime-game-media/bin/Release/net6.0/RuntimeGameMediaSmoke.dll
```

其中 `StartupCacheTests` 使用真实临时文件验证启动来源指纹与附属凭据：建立 4,097 个一字节合成 `.bundle` 文件，覆盖旧 4,096 上限之外的完整读取、旧图片代补写凭据和下次启动复用；分别改变尾端文件的名称、长度和最后写入时间，确认旧凭据失效。未知 DLC、空资源目录、零长度包和不可用目录必须给出明确原因；即使缓存完整，DLC 证据未知仍不能提前就绪。缺凭据、身份变化和 PNG 损坏也分别核对缓存失败原因。测试不创建 65,537 个文件，不读取真实游戏包，也不把这些合成文件当作 Unity 解包验证。

另一个验证入口位于实际 Mod HTTP 宿主，需要精确引用包、锁定测试运行库及当前 schema 4 合成 profile 文件，准备方法见 [HTTP 契约测试](../flutter-contracts/README.md#执行)：

```powershell
dotnet build tests/flutter-contracts/host/FlutterContractsHost.csproj -c Release -m:1 -p:UseAppHost=false
dotnet tests/flutter-contracts/host/bin/Release/net6.0/FlutterContractsHost.dll --game-media <synthetic-profile.json>
```

该入口验证认证、状态/清单/二进制长度、主设备与修订权限、等待期租约拒绝和注册/释放可用、重复重读合并，以及已排队手工写跨资源代次失效。实际 Unity Sprite 提取、首屏等待时长和 GPU 性能不属于本隔离测试的证明范围。
