using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.Save;
using J = MystiaStewardCompanion.Business.Domain.Recommendation.RecommendationJson;

namespace MystiaStewardCompanion.LocalApi
{
    /// <summary>
    /// 仅替换非业务的边界：文件、权威、HTTP正文、UI目标解析和值类型及整个Business partial直接链接生产源码；
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
        private static string ToJson(object value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        private static T ReadJsonRequest<T>(HttpRequestData data, params string[] fields) => JsonSerializer.Deserialize<T>(HttpRequestReader.ReadRequiredJsonBody(data), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        private bool TryRequireAutomationLease(string request, out object error, out long epoch)
        {
            lock (_automationLeaseLock) { PruneExpiredAutomationLease(DateTime.UtcNow); epoch = _automationCommandEpoch; error = new { ok = false }; return _automationLease?.ClientId == request; }
        }
        private static string BuildOrderActionJson(string query, Func<OrderPreparationRequest, OrderPreparationResult> action, long epoch, Func<bool>? current)
        {
            var request = new OrderPreparationRequest { AutomationEpoch = epoch, IsBusinessInputCurrent = current,
                TraceId = ReadStringQuery(query, "traceId"), OrderKey = ReadStringQuery(query, "orderKey"),
                RecipeId = ReadIntQuery(query, "recipeId", -1), BeverageId = ReadIntQuery(query, "beverageId", -1) };
            return ToJson(action(request));
        }
    }
}

namespace MystiaStewardCompanion.Save
{
    /// <summary>只记录业务宿主提交的UI值，不创建游戏对象。</summary>
    internal static class RuntimeUiPinningService
    {
        internal static IReadOnlyList<RuntimeUiTargetSnapshot> Targets = Array.Empty<RuntimeUiTargetSnapshot>();
        private static int _publicationCount;
        private static int _withdrawalCount;
        private static int _rejectionCount;
        internal static int PublicationCount => Volatile.Read(ref _publicationCount);
        internal static int WithdrawalCount => Volatile.Read(ref _withdrawalCount);
        internal static int RejectionCount => Volatile.Read(ref _rejectionCount);
        internal static Action? BeforePublicationForTest;
        internal static Exception? PublicationFailureForTest;

        /// <summary>统计真实宿主调用边界的次数，避免把仅压制日志误判为幂等撤销。</summary>
        internal static void UpdateTargets(long generation, IReadOnlyList<RuntimeUiTargetSnapshot> targets)
        {
            // 复用生产发布锁内外的真实会话校验；测试委托只在两次校验之间制造确定的切场竞争。
            try
            {
                RuntimeUiTargetSessionGuard.Validate(generation, RuntimeNightBusinessLifecycle.Snapshot);
                BeforePublicationForTest?.Invoke();
                RuntimeUiTargetSessionGuard.Validate(generation, RuntimeNightBusinessLifecycle.Snapshot);
            }
            catch (InvalidOperationException) { Interlocked.Increment(ref _rejectionCount); throw; }
            if (PublicationFailureForTest is { } failure) throw failure;
            // 目标集合本身也走生产不可变值类型，覆盖数量、种类唯一性与调色板组装校验。
            Targets = new RuntimeUiTargetSetSnapshot(generation, generation, targets).Targets;
            Interlocked.Increment(ref _publicationCount);
        }
        internal static void ClearTargetsForAuthorityTransition(long generation, string reason)
        {
            Targets = Array.Empty<RuntimeUiTargetSnapshot>();
            Interlocked.Increment(ref _withdrawalCount);
        }
    }
    internal static class RuntimeNightBusinessLifecycle
    {
        private static NightBusinessLifecycleTracker _tracker = CreateActiveTracker();
        internal static NightBusinessLifecycleSnapshot Snapshot => Volatile.Read(ref _tracker).Snapshot;
        internal static long Generation => Snapshot.Generation;
        private static NightBusinessLifecycleTracker CreateActiveTracker()
        {
            var tracker = new NightBusinessLifecycleTracker();
            tracker.TryActivate("offline initial active", DateTime.UtcNow, Environment.CurrentManagedThreadId, out _);
            return tracker;
        }

        /// <summary>离线边界只驱动生产状态机；清理记录目标模拟真实生命周期已经撤销的目标，绝不创建权威屏障。</summary>
        internal static void ResetForTest() { Volatile.Write(ref _tracker, new NightBusinessLifecycleTracker()); RuntimeUiPinningService.Targets = Array.Empty<RuntimeUiTargetSnapshot>(); }
        internal static void ActivateForTest() => _tracker.TryActivate("offline active", DateTime.UtcNow, Environment.CurrentManagedThreadId, out _);
        internal static void CloseForTest()
        {
            _tracker.TryBeginClosing("offline closing", DateTime.UtcNow, Environment.CurrentManagedThreadId, out _);
            RuntimeUiPinningService.Targets = Array.Empty<RuntimeUiTargetSnapshot>();
        }
        internal static void DestroyForTest()
        {
            _tracker.TryMarkDestroyed("offline destroyed", DateTime.UtcNow, Environment.CurrentManagedThreadId, out _);
            RuntimeUiPinningService.Targets = Array.Empty<RuntimeUiTargetSnapshot>();
        }
        internal static void SetGenerationForTest(long generation) => SynchronizeForBridge(generation, "Active");

        /// <summary>仅 JSONL mock 的可信 publish 可同步生命周期；普通 Publish 不同步，以便覆盖快照落后于真实切场的竞争。</summary>
        internal static void SynchronizeForBridge(long generation, string phase)
        {
            if (generation < 0 || generation > 1000 || !Enum.TryParse<NightBusinessLifecyclePhase>(phase, out var parsed)
                || !Enum.IsDefined(typeof(NightBusinessLifecyclePhase), parsed) || parsed.ToString() != phase
                || (parsed == NightBusinessLifecyclePhase.Inactive && generation != 0))
                throw new ArgumentException("离线生命周期输入无效。");
            if (Snapshot.Generation > generation || (parsed == NightBusinessLifecyclePhase.Inactive && Snapshot.Phase != parsed)) ResetForTest();
            while (Snapshot.Generation < generation)
            {
                if (Snapshot.Phase is NightBusinessLifecyclePhase.Active or NightBusinessLifecyclePhase.Closing) DestroyForTest();
                ActivateForTest();
            }
            if (parsed == NightBusinessLifecyclePhase.Closing) CloseForTest();
            else if (parsed == NightBusinessLifecyclePhase.Destroyed) DestroyForTest();
            else if (parsed == NightBusinessLifecyclePhase.Active && Snapshot.Phase != parsed)
                throw new ArgumentException("离线重开营业必须推进代次。");
        }
    }
}
