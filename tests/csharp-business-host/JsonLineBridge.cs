using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

/// <summary>
/// 给离线浏览器mock提供真实C#业务宿主的JSONL桥接。stdin串行请求只注入mock值；
/// 所有游戏动作返回取消，不启动游戏或端口，进程退出后删除其独占测试配置目录。
/// </summary>
internal static class JsonLineBridge
{
    internal static async Task Run()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        var root = Path.GetFullPath(Path.Combine("temp", "mystia-business-bridge-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var log = Logger.CreateLogSource("business-host-bridge");
        var host = new LocalApiServer(root, log, _ =>
        {
            var result = new OrderPreparationResult { Ok = false, Prepared = false, Error = "离线桥接禁止游戏副作用。" };
            result.Automation.Outcome = "cancelled"; result.Automation.Stage = "lease"; return result;
        });
        var client = ""; var started = false;
        try
        {
            string? line;
            while ((line = await Console.In.ReadLineAsync()) != null)
            {
                JsonObject input = new();
                try
                {
                    input = J.Obj(JsonNode.Parse(line));
                    JsonNode? result;
                    switch (J.Str(input["operation"]))
                    {
                        case "initialize":
                            if (started) throw new InvalidOperationException("桥接只能初始化一次。");
                            client = J.Str(input["clientId"]);
                            if (client.Length == 0) throw new ArgumentException("缺少clientId。");
                            var authority = host.Authority.Register(client, "Offline mock", new CompanionDeviceRegisterRequest
                            { ProtocolVersion = 1, ProfileSchemaVersion = 1, Platform = "windows", AppVersion = "offline", Profile = JsonSerializer.SerializeToElement(J.Obj(input["profile"])) }, DateTime.UtcNow);
                            host.Start(); started = true;
                            result = J.Object(("authorityRevision", authority.AuthorityRevision)); break;
                        case "publish":
                            if (!started) throw new InvalidOperationException("桥接尚未初始化。");
                            // 此输入由本地mock生成，不接受浏览器业务查询覆盖；驱动真实托管生命周期以验证严格发布门禁。
                            var trustedSnapshot = J.Obj(input["snapshot"]);
                            MystiaStewardCompanion.Save.RuntimeNightBusinessLifecycle.SynchronizeForBridge(
                                (long)J.Num(trustedSnapshot["nightBusinessGeneration"]), J.Str(trustedSnapshot["nightBusinessLifecyclePhase"]));
                            host.Import(input, client); result = J.Object(("inputVersion", host.InputVersion.ToString())); break;
                        case "status": result = host.Status((int)J.Num(input["protocolVersion"], 1)); break;
                        case "query": result = host.Query(J.Str(input["clientId"], client), J.Obj(input["intent"])); break;
                        case "heartbeat":
                            // 只接受mock已观测到的设备请求；它刷新设备在线时间，完全不续自动化租约。
                            var presence = host.Authority.Read(client, DateTime.UtcNow);
                            result = J.Object(("authorityRevision", presence.AuthorityRevision)); break;
                        case "lease":
                            // 默认无租约；显式测试租约也只到达上方取消适配器，不产生真实游戏副作用。
                            if (J.Bool(input["owned"])) host.GrantLease(client, host.Authority.ReadAuthorityRevision(), TimeSpan.FromSeconds(15)); else host.ExpireLease();
                            result = J.Object(("owned", host.HasLease)); break;
                        default: throw new ArgumentException("未知桥接操作。");
                    }
                    Console.WriteLine(J.Object(("id", input["id"]), ("ok", true), ("result", result)).ToJsonString());
                }
                catch (Exception error) { Console.WriteLine(J.Object(("id", input["id"]), ("ok", false), ("error", error.GetBaseException().Message)).ToJsonString()); }
            }
        }
        finally
        {
            await host.Stop(); Logger.Sources.Remove(log);
            var parent = Path.GetFullPath("temp") + Path.DirectorySeparatorChar;
            if (root.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, recursive: true);
        }
    }
}
