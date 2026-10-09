using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.LocalApi
{
    /// <summary>
    /// 仅替换非业务的边界：文件、权威、HTTP正文解析和整个Business partial直接链接生产源码；
    /// 不启动TCP监听，不加载Unity，不读取任何游戏文件。适配委托允许测试精确阻塞和观察副作用。
    /// </summary>
    internal sealed partial class LocalApiServer
    {
        private readonly object _authorityTransitionLock = new();
        private readonly object _automationLeaseLock = new();
        private readonly object _snapshotLock = new();
        private readonly CompanionDeviceAuthorityStore _deviceAuthorityStore;
        private readonly FavoriteStore _favoriteStore;
        private readonly CustomRecipeStore _customRecipeStore;
        private readonly ManualLogSource _log;
        private readonly string _testRoot;
        private readonly Func<OrderPreparationRequest, OrderPreparationResult> _prepareOrder;
        private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeOrder;
        private readonly Func<OrderPreparationRequest, OrderPreparationResult> _completeNormalOrder;
        private string _snapshotJson = "{}", _runtimeDataJson = "{}", _snapshotSignature = "initial";
        private long _automationCommandEpoch = 1;
        private TestLease? _automationLease;
        private sealed class TestLease
        {
            public string ClientId { get; init; } = "";
            public long AuthorityRevision { get; init; }
            public DateTime ExpiresAtUtc { get; init; }
        }
        internal LocalApiServer(string root, ManualLogSource log, Func<OrderPreparationRequest, OrderPreparationResult> adapter)
        {
            _log = log; _testRoot = root;
            _deviceAuthorityStore = new(Path.Combine(root, "devices.json"), log);
            _favoriteStore = new(Path.Combine(root, "favorites.json"), log);
            _customRecipeStore = new(Path.Combine(root, "custom.json"), log);
            _prepareOrder = _completeOrder = _completeNormalOrder = adapter;
        }
        internal CompanionDeviceAuthorityStore Authority => _deviceAuthorityStore;
        internal void Start() => StartBusinessHost();
        internal void Cancel() => StopBusinessHost();
        internal async Task Stop()
        {
            StopBusinessHost();
            if (_businessTask != null) await _businessTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        internal void Publish(JsonObject snapshot, JsonObject catalog)
        {
            lock (_snapshotLock)
            {
                _snapshotJson = snapshot.ToJsonString(); _runtimeDataJson = catalog.ToJsonString();
                _snapshotSignature = J.Str(snapshot["snapshotSignature"], Guid.NewGuid().ToString("N")); InvalidateBusinessInput();
            }
        }
        /// <summary>测试桥接在同一权威锁内注入外部mock已确认的值，保留生产存储的真实校验。</summary>
        internal void Import(JsonObject input, string client)
        {
            lock (_authorityTransitionLock)
            {
                InvalidateBusinessInput();
                if (input["profile"] is JsonObject profile)
                {
                    var state = _deviceAuthorityStore.ReadBusinessState(DateTime.UtcNow);
                    _deviceAuthorityStore.UpdatePrimaryProfile(client, new CompanionDeviceProfileUpdateRequest
                    {
                        ProtocolVersion = 1, ProfileSchemaVersion = 1,
                        ExpectedAuthorityRevision = state.AuthorityRevision, ExpectedProfileRevision = state.ActiveProfileRevision,
                        Profile = JsonSerializer.SerializeToElement(profile),
                    }, DateTime.UtcNow);
                }
                if (input["favorites"] is JsonObject favorites) File.WriteAllText(Path.Combine(_testRoot, "favorites.json"), favorites.ToJsonString());
                if (input["customRecipes"] is JsonObject custom) File.WriteAllText(Path.Combine(_testRoot, "custom.json"), custom.ToJsonString());
                Publish(J.Obj(input["snapshot"]), J.Obj(input["catalog"]));
            }
        }
        internal void ExpireLease() { lock (_automationLeaseLock) _automationLease = null; }
        internal void GrantLease(string id, long revision, TimeSpan duration)
        {
            lock (_automationLeaseLock) _automationLease = new() { ClientId = id, AuthorityRevision = revision, ExpiresAtUtc = DateTime.UtcNow + duration };
        }
        internal JsonObject Status(int version = 1) => J.Obj(JsonNode.Parse(GetBusinessStatusJson($"protocolVersion={version}")));
        internal JsonObject Query(string client, JsonObject intent) => J.Obj(JsonNode.Parse(QueueBusinessPageQuery(client,
            new HttpRequestData("", Encoding.UTF8.GetBytes(intent.ToJsonString())))));
        internal JsonObject RawQuery(string client, string json) => J.Obj(JsonNode.Parse(QueueBusinessPageQuery(client,
            new HttpRequestData("", Encoding.UTF8.GetBytes(json)))));
        internal int QueryCount { get { lock (_businessLock) return _businessPageQueries.Count; } }
        internal string[] QueryKeys { get { lock (_businessLock) return _businessPageQueries.Keys.ToArray(); } }
        internal void Invalidate() => InvalidateBusinessInput();
        internal void AddFavorite() => _favoriteStore.AddRecipe(3, "测试稀客", "鲜", 101, Array.Empty<int>());
        internal void CheckFrameAuthorityDoesNotRenew()
        {
            var frame = CaptureBusinessFrame();
            _ = BuildBusinessAutomationAuthority(frame);
        }
        internal bool HasLease { get { lock (_automationLeaseLock) return _automationLease != null; } }
        internal long InputVersion => Interlocked.Read(ref _businessInputVersion);
        private void PruneExpiredAutomationLease(DateTime now)
        {
            if (_automationLease != null && (_automationLease.ExpiresAtUtc <= now
                || _automationLease.AuthorityRevision != _deviceAuthorityStore.ReadAuthorityRevision())) _automationLease = null;
        }
        private static (string, string) ReadRequiredClientIdentity(string request) => (request, "test");
        private static int ReadIntQuery(string query, string name, int fallback) => int.TryParse(ReadQuery(query, name), out var value) ? value : fallback;
        private static string ReadQuery(string query, string name) => query.Split('&').Select(part => part.Split('=', 2))
            .Where(parts => Uri.UnescapeDataString(parts[0]) == name).Select(parts => parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "").FirstOrDefault() ?? "";
        private static string ToJson(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        private static T ReadJsonRequest<T>(HttpRequestData data, params string[] fields) => JsonSerializer.Deserialize<T>(HttpRequestReader.ReadRequiredJsonBody(data), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        private bool TryRequireAutomationLease(string request, out object error, out long epoch)
        {
            lock (_automationLeaseLock) { PruneExpiredAutomationLease(DateTime.UtcNow); epoch = _automationCommandEpoch; error = new { ok = false }; return _automationLease?.ClientId == request; }
        }
        private static string BuildOrderActionJson(string query, Func<OrderPreparationRequest, OrderPreparationResult> action, long epoch, Func<bool>? current)
        {
            var request = new OrderPreparationRequest { AutomationEpoch = epoch, IsBusinessInputCurrent = current,
                TraceId = ReadQuery(query, "traceId"), OrderKey = ReadQuery(query, "orderKey"),
                RecipeId = ReadIntQuery(query, "recipeId", -1), BeverageId = ReadIntQuery(query, "beverageId", -1) };
            return ToJson(action(request));
        }
        private static RuntimeUiTargetSnapshot ReadUiPinningTarget(string query, int index) => new(ReadQuery(query, "target0Kind"), ReadQuery(query, "target0Revision"));
    }
    internal sealed record RuntimeUiTargetSnapshot(string Kind, string Revision);
}

namespace MystiaStewardCompanion.Save
{
    /// <summary>只记录业务宿主提交的UI值，不创建游戏对象。</summary>
    internal static class RuntimeUiPinningService
    {
        internal static IReadOnlyList<MystiaStewardCompanion.LocalApi.RuntimeUiTargetSnapshot> Targets = Array.Empty<MystiaStewardCompanion.LocalApi.RuntimeUiTargetSnapshot>();
        internal static void UpdateTargets(long generation, IReadOnlyList<MystiaStewardCompanion.LocalApi.RuntimeUiTargetSnapshot> targets) => Targets = targets.ToArray();
        internal static void ClearTargetsForAuthorityTransition(long generation, string reason) => Targets = Array.Empty<MystiaStewardCompanion.LocalApi.RuntimeUiTargetSnapshot>();
    }
    internal static class RuntimeNightBusinessLifecycle { internal static long Generation => 1; }
}
