using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;
using MystiaStewardCompanion.Business.Domain.Recommendation;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 只读展示使用的最小配置身份。没有设备列表、配置正文或租约，也不授予任何写入许可。
/// 存储事务提交后整体发布，Unity稳定帧可直接比较引用，避免读取完整权威DTO及取得存储锁。
/// </summary>
internal sealed record CompanionAuthorityDisplayScope(string RegistryId, long AuthorityRevision,
    long ActiveProfileRevision, string ActiveProfileHash)
{
    internal static CompanionAuthorityDisplayScope Empty { get; } = new("", 0, 0, "");
}

/// <summary>
/// 持久保存伴随设备及唯一生效的功能配置权威。
/// </summary>
/// <remarks>
/// 设备本地的窗口、连接和页面偏好不进入此存储。所有 mutation 都先构建新状态并原子落盘，
/// 文件损坏或未来 schema 会使配置写入与运行时 writer fail-closed，且不会覆盖原文件。
/// </remarks>
internal sealed class CompanionDeviceAuthorityStore
{
    private CompanionAuthorityDisplayScope _displayScope = CompanionAuthorityDisplayScope.Empty;
    internal CompanionAuthorityDisplayScope DisplayScope => Volatile.Read(ref _displayScope);
    public const int ProtocolVersion = 1;
    public const int ProfileSchemaVersion = 10;
    private const int StoreSchemaVersion = 10;
    private const int MaxDevices = 32;
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(20);
    private static readonly Regex ColorPattern = new("^#[0-9A-F]{6}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ProfileBooleanFields = new(StringComparer.Ordinal)
    {
        "automationEnabled",
        "gameUiEnabled",
        "rareGameUiEnabled",
        "normalGameUiEnabled",
        "rareGuestParticipationModuleEnabled",
        "rareGuestParticipationAutoResumeEnabled",
        "autoRareOrderEnabled",
        "autoNormalOrderEnabled",
        "autoNormalTakeBeverage",
        "autoNormalStartCooking",
        "autoNormalDeliverFood",
        "autoNormalCompleteOrder",
        "autoNormalStopOnError",
        "autoPrepCompleteOrder",
        "autoPrepTakeBeverage",
        "autoPrepStartCooking",
        "autoPrepCollectCooking",
        "autoPrepRecipeFavoritesOnly",
        "autoPrepBeverageFavoritesOnly",
        "autoPrepStopOnError",
        "filterMissingCookers",
        "missionRecipePriorityEnabled",
        "pinFavoriteRecipeEnabled",
        "pinFavoriteBeverageEnabled",
        "rareGameUiPinningEnabled",
        "normalGameUiPinningEnabled",
        "rareRecipeVariantEnabled",
        "normalRecipeVariantEnabled",
        "rareCookerHighlightEnabled",
        "normalCookerHighlightEnabled",
        "rareSeatHighlightEnabled",
        "normalSeatHighlightEnabled",
        "rareOrderHighlightEnabled",
        "normalOrderHighlightEnabled",
    };
    private static readonly HashSet<string> ProfileFields = new(
        ProfileBooleanFields.Concat(new[]
        {
            "autoRareConcurrency",
            "autoNormalConcurrency",
            "autoMaxStepRetries",
            "autoMaxRollbacks",
            "rareOrderHighlightPalette",
            "normalOrderHighlightPalette",
            "managedRareGuestIds",
            "rareGuestParticipationAutoResumeThresholdPercent",
            "rareGuestParticipationAutoResumePosition",
            "recommendationSortProfile",
            "recommendationBudgetPolicy",
            "recipeVariantLimitPerBase",
            "recommendationExclusions",
        }),
        StringComparer.Ordinal);
    private static readonly HashSet<string> ObjectiveKeys = new(StringComparer.Ordinal)
    {
        "foodPreference",
        "beveragePreference",
        "negativeRisk",
        "extraCount",
        "resourcePressure",
        "totalCost",
        "profit",
        "beverageStock",
    };
    /// <summary>新配置默认每类两组；普客冷色、稀客暖色，一种颜色对应一笔辅助订单。</summary>
    internal static readonly string[] DefaultRareHighlightPalette = { "#F2CA5C", "#EF856D" };
    internal static readonly string[] DefaultNormalHighlightPalette = { "#5AA9E6", "#57C7B6" };
    private static readonly string[] RareMigrationPalette = { "#F2CA5C", "#EF856D", "#E785B4" };
    private static readonly string[] NormalMigrationPalette = { "#5AA9E6", "#57C7B6", "#9A8FE8" };
    private static readonly string[] FormerDefaultRarePalette = { "#BA9CE8", "#FFFFFF", "#000000", "#F08C78" };
    private static readonly string[] FormerDefaultNormalPalette = { "#FFDB2E", "#5FACD3", "#F97373", "#7CCB8A" };
    // 历史 v1–v4 单色补两色规则独立冻结，产品默认调整不能改写历史迁移结果。
    private static readonly string[] LegacyRareMigrationPalette = { "#FFDB2E", "#F08C78" };
    private static readonly string[] LegacyNormalMigrationPalette = { "#5FACD3", "#97C878" };
    private static readonly string[] PaletteFields = { "rareOrderHighlightPalette", "normalOrderHighlightPalette" };
    private static readonly string[] NewProfileFields =
    { "rareGuestParticipationModuleEnabled", "managedRareGuestIds", "rareOrderHighlightPalette", "normalOrderHighlightPalette" };
    // 仅 v5/v6 历史磁盘记录使用，必须先验证再删除；当前在线配置不能继续保留独立数量。
    private static readonly string[] LegacyHighlightCountFields = { "rareHighlightOrderCount", "normalHighlightOrderCount" };
    /// <summary>等待条自动启用属于共享调度策略；历史配置显式迁移为关闭，不能悄然恢复暂停订单。</summary>
    private static readonly string[] AutoResumeFields = { "rareGuestParticipationAutoResumeEnabled",
        "rareGuestParticipationAutoResumeThresholdPercent", "rareGuestParticipationAutoResumePosition" };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ManualLogSource? _log;
    private readonly Dictionary<string, DateTime> _lastSeenUtc = new(StringComparer.Ordinal);
    private DeviceAuthorityData? _data;
    private string _loadError = "";

    public CompanionDeviceAuthorityStore(string path, ManualLogSource? log = null)
    {
        _path = path;
        _log = log;
        Load();
    }

    public static string ResolvePath()
    {
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "companion-devices.json");
    }

    public CompanionDeviceAuthorityStateDto Register(
        string clientId,
        string clientLabel,
        CompanionDeviceRegisterRequest request,
        DateTime nowUtc)
    {
        ValidateProtocol(request.ProtocolVersion, request.ProfileSchemaVersion);
        var profile = ValidateAndCloneProfile(request.Profile);
        var platform = NormalizePlatform(request.Platform);
        var appVersion = NormalizeMetadata(request.AppVersion, 32, "appVersion");

        lock (_lock)
        {
            var data = RequireData();
            var next = CloneData(data);
            var device = next.Devices.FirstOrDefault(item =>
                string.Equals(item.DeviceId, clientId, StringComparison.Ordinal));
            if (device == null)
            {
                if (next.Devices.Count >= MaxDevices)
                {
                    throw new CompanionDeviceAuthorityException(409, "设备记录已达到 32 台上限，请先移除离线设备。");
                }

                var now = nowUtc.ToUniversalTime();
                device = new CompanionDeviceRecord
                {
                    DeviceId = clientId,
                    Label = NormalizeLabel(clientLabel),
                    Platform = platform,
                    AppVersion = appVersion,
                    ProfileRevision = 1,
                    AppliedProfileRevision = 1,
                    ProfileHash = ComputeProfileHash(profile),
                    Profile = profile,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                next.Devices.Add(device);
                if (string.IsNullOrWhiteSpace(next.PrimaryDeviceId))
                {
                    next.PrimaryDeviceId = clientId;
                    next.AuthorityRevision = 1;
                }
                next.StateRevision++;
                SaveAndCommit(next);
            }
            else
            {
                var metadataChanged = !string.Equals(device.Platform, platform, StringComparison.Ordinal)
                    || !string.Equals(device.AppVersion, appVersion, StringComparison.Ordinal);
                if (metadataChanged)
                {
                    device.Platform = platform;
                    device.AppVersion = appVersion;
                    device.UpdatedAtUtc = nowUtc.ToUniversalTime();
                    next.StateRevision++;
                    SaveAndCommit(next);
                }
            }

            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return BuildState(_data!, clientId, nowUtc);
        }
    }

    public CompanionDeviceAuthorityStateDto Read(string clientId, DateTime nowUtc)
    {
        lock (_lock)
        {
            var data = RequireData();
            RequireRegisteredDevice(data, clientId);
            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return BuildState(data, clientId, nowUtc);
        }
    }

    public CompanionDeviceAuthorityStateDto UpdatePrimaryProfile(
        string clientId,
        CompanionDeviceProfileUpdateRequest request,
        DateTime nowUtc)
    {
        ValidateProtocol(request.ProtocolVersion, request.ProfileSchemaVersion);
        var profile = ValidateAndCloneProfile(request.Profile);
        lock (_lock)
        {
            var data = RequireData();
            RequireExpectedAuthority(data, request.ExpectedAuthorityRevision);
            RequirePrimary(data, clientId);
            var current = RequireRegisteredDevice(data, clientId);
            // 迁移后的主设备也可能处于待确认状态；禁止用一次普通配置修改
            // 越过新 profile 的持久化确认，必须先按新 hash/revision 完成 ACK。
            if (!string.IsNullOrWhiteSpace(current.PendingSyncId))
                throw Conflict("请先保存并确认当前同步配置，再修改共享设置。");
            if (request.ExpectedProfileRevision != current.ProfileRevision)
            {
                throw Conflict("配置版本已经变化，请刷新设备状态后重试。");
            }

            var nextHash = ComputeProfileHash(profile);
            if (!string.Equals(nextHash, current.ProfileHash, StringComparison.Ordinal))
            {
                var next = CloneData(data);
                var device = RequireRegisteredDevice(next, clientId);
                device.Profile = profile;
                device.ProfileHash = nextHash;
                device.ProfileRevision++;
                device.AppliedProfileRevision = device.ProfileRevision;
                device.PendingSyncId = "";
                device.UpdatedAtUtc = nowUtc.ToUniversalTime();
                next.AuthorityRevision++;
                next.StateRevision++;
                SaveAndCommit(next);
            }

            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return BuildState(_data!, clientId, nowUtc);
        }
    }

    public CompanionDeviceAuthorityMutationResult SetPrimary(
        string clientId,
        CompanionDeviceSetPrimaryRequest request,
        DateTime nowUtc)
    {
        ValidateProtocol(request.ProtocolVersion, ProfileSchemaVersion);
        lock (_lock)
        {
            var data = RequireData();
            RequireRegisteredDevice(data, clientId);
            RequireExpectedAuthority(data, request.ExpectedAuthorityRevision);
            var target = RequireRegisteredDevice(data, request.DeviceId);
            if (!IsOnline(target.DeviceId, nowUtc))
            {
                throw Conflict("只能把当前在线的设备设为主设备。");
            }
            if (!string.IsNullOrWhiteSpace(target.PendingSyncId)
                || target.AppliedProfileRevision != target.ProfileRevision)
            {
                throw Conflict("目标设备仍有尚未确认的配置同步，完成应用后才能设为主设备。");
            }

            var changed = !string.Equals(data.PrimaryDeviceId, target.DeviceId, StringComparison.Ordinal);
            if (changed)
            {
                var next = CloneData(data);
                next.PrimaryDeviceId = target.DeviceId;
                next.AuthorityRevision++;
                next.StateRevision++;
                SaveAndCommit(next);
            }

            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return new CompanionDeviceAuthorityMutationResult
            {
                Changed = changed,
                State = BuildState(_data!, clientId, nowUtc),
            };
        }
    }

    public CompanionDeviceAuthorityStateDto SyncFromPrimary(
        string clientId,
        CompanionDeviceSyncRequest request,
        DateTime nowUtc)
    {
        ValidateProtocol(request.ProtocolVersion, ProfileSchemaVersion);
        lock (_lock)
        {
            var data = RequireData();
            RequireExpectedAuthority(data, request.ExpectedAuthorityRevision);
            RequireRegisteredDevice(data, clientId);
            if (string.Equals(request.DeviceId, data.PrimaryDeviceId, StringComparison.Ordinal))
            {
                throw new CompanionDeviceAuthorityException(400, "主设备不需要同步自己的配置。");
            }

            var primary = RequireRegisteredDevice(data, data.PrimaryDeviceId);
            RequireRegisteredDevice(data, request.DeviceId);
            var next = CloneData(data);
            var target = RequireRegisteredDevice(next, request.DeviceId);
            target.Profile = primary.Profile.Clone();
            target.ProfileHash = primary.ProfileHash;
            target.ProfileRevision++;
            target.PendingSyncId = Guid.NewGuid().ToString("N");
            target.UpdatedAtUtc = nowUtc.ToUniversalTime();
            next.StateRevision++;
            SaveAndCommit(next);
            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return BuildState(_data!, clientId, nowUtc);
        }
    }

    public CompanionDeviceAuthorityStateDto AcknowledgeSync(
        string clientId,
        CompanionDeviceSyncAckRequest request,
        DateTime nowUtc)
    {
        ValidateProtocol(request.ProtocolVersion, ProfileSchemaVersion);
        lock (_lock)
        {
            var data = RequireData();
            var current = RequireRegisteredDevice(data, clientId);
            if (string.IsNullOrWhiteSpace(current.PendingSyncId)
                || !string.Equals(current.PendingSyncId, request.SyncId, StringComparison.Ordinal)
                || current.ProfileRevision != request.ProfileRevision
                || !string.Equals(current.ProfileHash, request.ProfileHash, StringComparison.Ordinal))
            {
                throw Conflict("待确认的配置同步已经变化，请重新读取设备状态。");
            }

            var next = CloneData(data);
            var target = RequireRegisteredDevice(next, clientId);
            target.AppliedProfileRevision = target.ProfileRevision;
            target.PendingSyncId = "";
            target.UpdatedAtUtc = nowUtc.ToUniversalTime();
            next.StateRevision++;
            SaveAndCommit(next);
            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return BuildState(_data!, clientId, nowUtc);
        }
    }

    public CompanionDeviceAuthorityStateDto Rename(
        string clientId,
        CompanionDeviceRenameRequest request,
        DateTime nowUtc)
    {
        ValidateProtocol(request.ProtocolVersion, ProfileSchemaVersion);
        var label = NormalizeLabel(request.Label);
        lock (_lock)
        {
            var data = RequireData();
            var current = RequireRegisteredDevice(data, clientId);
            if (!string.Equals(current.Label, label, StringComparison.Ordinal))
            {
                var next = CloneData(data);
                var target = RequireRegisteredDevice(next, clientId);
                target.Label = label;
                target.UpdatedAtUtc = nowUtc.ToUniversalTime();
                next.StateRevision++;
                SaveAndCommit(next);
            }
            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return BuildState(_data!, clientId, nowUtc);
        }
    }

    public CompanionDeviceAuthorityStateDto Forget(
        string clientId,
        CompanionDeviceForgetRequest request,
        DateTime nowUtc)
    {
        ValidateProtocol(request.ProtocolVersion, ProfileSchemaVersion);
        lock (_lock)
        {
            var data = RequireData();
            RequireRegisteredDevice(data, clientId);
            RequireExpectedAuthority(data, request.ExpectedAuthorityRevision);
            if (string.Equals(request.DeviceId, clientId, StringComparison.Ordinal))
            {
                throw new CompanionDeviceAuthorityException(400, "不能移除当前设备。");
            }
            if (string.Equals(request.DeviceId, data.PrimaryDeviceId, StringComparison.Ordinal))
            {
                throw new CompanionDeviceAuthorityException(400, "不能移除主设备，请先切换主设备。");
            }
            RequireRegisteredDevice(data, request.DeviceId);
            if (IsOnline(request.DeviceId, nowUtc))
            {
                throw Conflict("不能移除当前在线的设备。");
            }

            var next = CloneData(data);
            next.Devices.RemoveAll(device => string.Equals(device.DeviceId, request.DeviceId, StringComparison.Ordinal));
            next.StateRevision++;
            SaveAndCommit(next);
            _lastSeenUtc.Remove(request.DeviceId);
            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            return BuildState(_data!, clientId, nowUtc);
        }
    }

    public bool TryAuthorizePrimary(
        string clientId,
        long expectedAuthorityRevision,
        DateTime nowUtc,
        out string error,
        bool recordActivity = true)
    {
        lock (_lock)
        {
            if (_data == null)
            {
                error = string.IsNullOrWhiteSpace(_loadError)
                    ? "设备连接信息尚未准备好，请先完成设备注册。"
                    : _loadError;
                return false;
            }
            if (!_data.Devices.Any(device => string.Equals(device.DeviceId, clientId, StringComparison.Ordinal)))
            {
                error = "当前伴随设备尚未注册，不能修改游戏运行时状态。";
                return false;
            }
            if (!string.Equals(_data.PrimaryDeviceId, clientId, StringComparison.Ordinal))
            {
                error = "当前设备不是主设备，只能查看主设备的生效配置。";
                return false;
            }
            if (expectedAuthorityRevision <= 0 || expectedAuthorityRevision != _data.AuthorityRevision)
            {
                error = "主设备或共享配置已变化，请刷新设备状态后重试。";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(RequireRegisteredDevice(_data, clientId).PendingSyncId))
            {
                error = "当前主设备尚未保存并确认同步配置，不能修改游戏运行时状态。";
                return false;
            }

            // 后台业务许可检查不能伪造客户端心跳；仅外部真实请求记录在线活动。
            if (recordActivity) _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            error = "";
            return true;
        }
    }

    public long ReadAuthorityRevision()
    {
        lock (_lock) return _data?.AuthorityRevision ?? 0;
    }

    /// <summary>
    /// 主线程调度采样使用的轻量权威检查：不克隆配置、不伪造设备心跳，也不等待后台
    /// 配置落盘持有的锁。忙碌、主设备离线、修订不符或尚未确认同步时跳过本次采样，
    /// 下一份新鲜现场状态再评估；调用方还须持有authority transition的非阻塞许可。
    /// </summary>
    internal bool IsCurrentPrimaryOnline(long expectedAuthorityRevision, DateTime nowUtc)
    {
        if (!Monitor.TryEnter(_lock)) return false;
        try
        {
            if (_data == null || expectedAuthorityRevision <= 0 || expectedAuthorityRevision != _data.AuthorityRevision)
                return false;
            var primary = _data.Devices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, _data.PrimaryDeviceId, StringComparison.Ordinal));
            return primary != null && string.IsNullOrEmpty(primary.PendingSyncId)
                && primary.AppliedProfileRevision == primary.ProfileRevision && IsOnline(primary.DeviceId, nowUtc);
        }
        finally { Monitor.Exit(_lock); }
    }

    /// <summary>读取后台计算使用的权威配置副本，不刷新任何设备的在线时间。</summary>
    public CompanionDeviceAuthorityStateDto ReadBusinessState(DateTime nowUtc)
    {
        lock (_lock)
        {
            var data = RequireData();
            return BuildState(data, data.PrimaryDeviceId, nowUtc);
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _data = CreateEmptyData();
                PublishDisplayScope(_data);
                return;
            }

            try
            {
                // 原文只读取一次，再基于同一份 JSON 做结构校验和反序列化。
                // 不能先交给 POCO：缺少 devices、修订或 pendingSyncId 时，属性默认值
                // 会将损坏文件伪装成合法空注册表或已确认设备，进而被迁移覆盖。
                var raw = File.ReadAllText(_path, Encoding.UTF8);
                using var document = JsonDocument.Parse(raw);
                ValidateStoredDocument(document.RootElement);
                var data = document.RootElement.Deserialize<DeviceAuthorityData>(JsonOptions)
                    ?? throw new InvalidDataException("Device authority document is null.");
                // 只迁移已知 v1–v9 存储，先验证原配置哈希与字段，再原子落盘新格式。
                // 文件异常或未来版本不会进入迁移，也不会被空默认值覆盖。
                if (data.Version is 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9) data = MigrateLegacyVersion(data);
                ValidateStoredData(data);
                _data = data;
                PublishDisplayScope(data);
                _loadError = "";
            }
            catch (Exception ex)
            {
                _data = null;
                Volatile.Write(ref _displayScope, CompanionAuthorityDisplayScope.Empty);
                _loadError = "设备配置文件无法读取，原文件已保留；请检查 companion-devices.json。";
                _log?.LogError($"Failed to load companion device authority from '{_path}': {ex}");
            }
        }
    }

    /// <summary>
    /// 在默认值或大小写宽松的反序列化规则介入前，验证磁盘协议的完整外壳。
    /// v1–v10 的外壳相同，差异仅在 profile；profile 的字段、类型、依赖和原始
    /// 哈希继续由 ValidateStoredData 按明确版本验证。此方法只读，不修补缺省数据。
    /// </summary>
    private static void ValidateStoredDocument(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Device authority document must be an object.");
        RequireExactProperties(document, new HashSet<string>(StringComparer.Ordinal)
        {
            "version", "registryId", "authorityRevision", "stateRevision", "primaryDeviceId", "devices",
        }, "设备配置文件");
        // 版本必须在原始 JSON 中明确为受支持整数；不推断缺省版本，也不接受字符串版本。
        var version = document.GetProperty("version");
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)
            || number is not (1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or StoreSchemaVersion))
            throw new InvalidDataException("Unsupported device authority document version.");
        _ = RequireString(document, "registryId");
        _ = RequireString(document, "primaryDeviceId");
        var devices = document.GetProperty("devices");
        if (devices.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Stored devices must be an array.");

        var recordFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "deviceId", "label", "platform", "appVersion", "profileRevision", "appliedProfileRevision",
            "profileHash", "profile", "pendingSyncId", "createdAtUtc", "updatedAtUtc",
        };
        foreach (var device in devices.EnumerateArray())
        {
            if (device.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Stored device must be an object.");
            // 精确字段检查同时拒绝重复和未知字段，避免 Deserialize 的后值覆盖前值
            // 或忽略未知属性造成身份、同步状态及原始记录信息悄然丢失。
            RequireExactProperties(device, recordFields, "设备记录");
            foreach (var field in new[]
            {
                "deviceId", "label", "platform", "appVersion", "profileHash", "pendingSyncId",
                "createdAtUtc", "updatedAtUtc",
            })
                _ = RequireString(device, field);
            // 修订数值和时间仍交给类型反序列化以及后续范围校验，避免维护第二套语义规则。
        }
    }

    /// <summary>
    /// 将已验证的 v1–v9 设备配置一次性升级为 v10，增加默认关闭的低耐心自动启用策略。
    /// v1 仍按历史辅助分项推导新增总开关；v2 保留用户显式选择，不重新推导。
    /// 除已移除字段外，保留身份、主设备、自动化及全部有效偏好。
    /// 每个设备获得新 revision/hash 与待确认标识，旧 ACK 和旧写入凭据不能
    /// 确认新配置，客户端必须先持久化新 profile 后重新 ACK 才恢复写入。
    /// </summary>
    private DeviceAuthorityData MigrateLegacyVersion(DeviceAuthorityData legacy)
    {
        ValidateStoredData(legacy, legacy.Version);
        var next = CloneData(legacy);
        next.Version = StoreSchemaVersion;
        foreach (var device in next.Devices)
        {
            var fields = device.Profile.EnumerateObject().ToDictionary(
                property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            if (legacy.Version == 1)
            {
                var enabled = fields.Any(field => IsGameUiFeature(field.Key) && field.Value.GetBoolean());
                fields.Add("gameUiEnabled", JsonSerializer.SerializeToElement(enabled));
            }
            // 旧版本没有类别总开关，既有行为完全由全局开关与五个分项决定。
            // 新开关迁移为 true，不能根据分项反推而意外关闭以后重新开启的辅助。
            if (legacy.Version < 9)
            {
                fields.Add("rareGameUiEnabled", JsonSerializer.SerializeToElement(true));
                fields.Add("normalGameUiEnabled", JsonSerializer.SerializeToElement(true));
            }
            // 与名单一起保存并同步；旧版本没有用户授权，迁移绝不能开启自动启用。
            fields.Add("rareGuestParticipationAutoResumeEnabled", JsonSerializer.SerializeToElement(false));
            fields.Add("rareGuestParticipationAutoResumeThresholdPercent", JsonSerializer.SerializeToElement(30));
            fields.Add("rareGuestParticipationAutoResumePosition", JsonSerializer.SerializeToElement("tail"));
            fields.Remove("serviceOrderSortMode");
            if (legacy.Version < 5)
            {
                var sort = fields["recommendationSortProfile"];
                var retained = sort.GetProperty("objectives").EnumerateArray()
                    .Where(item => item.GetProperty("key").GetString() != "cookerAvailable")
                    .Select(item => item.Clone()).ToArray();
                var migratedSort = JsonSerializer.SerializeToElement(new
                {
                    preset = sort.GetProperty("preset").GetString(), objectives = retained,
                });
                // 删除无效评分项后保留其余八项的全部数值；历史可编辑预设仍按实际值标识为自定义。
                fields["recommendationSortProfile"] = SortMatchesPreset(migratedSort)
                    ? migratedSort : JsonSerializer.SerializeToElement(new { preset = "custom", objectives = retained });
            }
            // v1–v4 保留各自单色并补足两色；v5 公共八色分别复制前三色，保留自定义顺序。
            if (legacy.Version < 6)
            {
                foreach (var kind in new[] { "rare", "normal" })
                {
                    var palette = legacy.Version == 5
                        ? fields["orderHighlightPalette"].EnumerateArray().Select(color => color.GetString()!).Take(3).ToArray()
                        : new[] { fields[kind + "TargetHighlightColor"].GetString()! }
                            .Concat(kind == "rare" ? LegacyRareMigrationPalette : LegacyNormalMigrationPalette)
                            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
                    fields[kind + "OrderHighlightPalette"] = JsonSerializer.SerializeToElement(palette);
                }
            }
            else
            {
                // v6/v7在完整验证旧1–4色后缩减到前三色，不能以截断掩盖损坏的第四个值。
                // 只有v7精确命中旧默认及其前缀才换同数量的新冷暖色；自定义颜色不被重置。
                foreach (var kind in new[] { "rare", "normal" })
                {
                    var field = kind + "OrderHighlightPalette";
                    var colors = fields[field].EnumerateArray().Select(color => color.GetString()!).ToArray();
                    var formerDefault = kind == "rare" ? FormerDefaultRarePalette : FormerDefaultNormalPalette;
                    var isFormerDefault = legacy.Version == 7 && colors.SequenceEqual(formerDefault.Take(colors.Length));
                    var source = isFormerDefault ? (kind == "rare" ? RareMigrationPalette : NormalMigrationPalette) : colors;
                    fields[field] = JsonSerializer.SerializeToElement(source.Take(Math.Min(colors.Length, 3)).ToArray());
                }
            }
            fields.Remove("rareTargetHighlightColor");
            fields.Remove("normalTargetHighlightColor");
            fields.Remove("orderHighlightPalette");
            foreach (var field in LegacyHighlightCountFields) fields.Remove(field);
            if (legacy.Version < 5)
            {
                fields["rareGuestParticipationModuleEnabled"] = JsonSerializer.SerializeToElement(false);
                fields["managedRareGuestIds"] = JsonSerializer.SerializeToElement(Array.Empty<int>());
            }
            device.Profile = JsonSerializer.SerializeToElement(fields);
            device.ProfileRevision = checked(device.ProfileRevision + 1);
            device.ProfileHash = ComputeProfileHash(device.Profile);
            device.PendingSyncId = Guid.NewGuid().ToString("N");
        }
        if (next.Devices.Count > 0)
        {
            next.AuthorityRevision = checked(next.AuthorityRevision + 1);
            next.StateRevision = checked(next.StateRevision + 1);
        }
        ValidateStoredData(next);
        // 与常规 mutation 共用已验证的原子文件替换，不在迁移失败时提交内存态。
        JsonFileStore.Save(_path, next, JsonOptions);
        return next;
    }

    /// <summary>精确匹配十个历史辅助字段，避免将自动化或收藏偏好算入总开关。</summary>
    private static bool IsGameUiFeature(string field) => field is
        "rareGameUiPinningEnabled" or "normalGameUiPinningEnabled"
        or "rareRecipeVariantEnabled" or "normalRecipeVariantEnabled"
        or "rareCookerHighlightEnabled" or "normalCookerHighlightEnabled"
        or "rareSeatHighlightEnabled" or "normalSeatHighlightEnabled"
        or "rareOrderHighlightEnabled" or "normalOrderHighlightEnabled";

    private void SaveAndCommit(DeviceAuthorityData next)
    {
        ValidateStoredData(next);
        JsonFileStore.Save(_path, next, JsonOptions);
        _data = next;
        PublishDisplayScope(next);
    }

    /// <summary>
    /// 调用者已经持有存储锁，且文件已提交或首次读取已验证。仅身份变化时替换引用；设备心跳、
    /// 非主设备资料等无关变动不会造成专注层重绘。落盘失败不可能发布尚未生效的配置身份。
    /// </summary>
    private void PublishDisplayScope(DeviceAuthorityData data)
    {
        var primary = data.Devices.FirstOrDefault(device => device.DeviceId == data.PrimaryDeviceId);
        var next = primary == null ? CompanionAuthorityDisplayScope.Empty
            : new CompanionAuthorityDisplayScope(data.RegistryId, data.AuthorityRevision, primary.ProfileRevision, primary.ProfileHash);
        if (next != _displayScope) Volatile.Write(ref _displayScope, next);
    }

    private CompanionDeviceAuthorityStateDto BuildState(
        DeviceAuthorityData data,
        string currentDeviceId,
        DateTime nowUtc)
    {
        var current = RequireRegisteredDevice(data, currentDeviceId);
        var primary = RequireRegisteredDevice(data, data.PrimaryDeviceId);
        return new CompanionDeviceAuthorityStateDto
        {
            Ok = true,
            ProtocolVersion = ProtocolVersion,
            ProfileSchemaVersion = ProfileSchemaVersion,
            RegistryId = data.RegistryId,
            AuthorityRevision = data.AuthorityRevision,
            StateRevision = data.StateRevision,
            PrimaryDeviceId = data.PrimaryDeviceId,
            CurrentDeviceId = currentDeviceId,
            CurrentDeviceIsPrimary = string.Equals(currentDeviceId, data.PrimaryDeviceId, StringComparison.Ordinal),
            ActiveProfileRevision = primary.ProfileRevision,
            ActiveProfileHash = primary.ProfileHash,
            ActiveProfile = primary.Profile.Clone(),
            CurrentDeviceProfileRevision = current.ProfileRevision,
            CurrentDeviceProfileHash = current.ProfileHash,
            CurrentDeviceProfile = current.Profile.Clone(),
            PendingSyncId = string.IsNullOrWhiteSpace(current.PendingSyncId) ? null : current.PendingSyncId,
            Devices = data.Devices
                .OrderByDescending(device => string.Equals(device.DeviceId, data.PrimaryDeviceId, StringComparison.Ordinal))
                .ThenBy(device => device.CreatedAtUtc)
                .Select(device => new CompanionDeviceDto
                {
                    DeviceId = device.DeviceId,
                    Label = device.Label,
                    Platform = device.Platform,
                    AppVersion = device.AppVersion,
                    IsCurrent = string.Equals(device.DeviceId, currentDeviceId, StringComparison.Ordinal),
                    IsPrimary = string.Equals(device.DeviceId, data.PrimaryDeviceId, StringComparison.Ordinal),
                    Online = IsOnline(device.DeviceId, nowUtc),
                    LastSeenAtUtc = _lastSeenUtc.TryGetValue(device.DeviceId, out var lastSeen)
                        ? lastSeen.ToUniversalTime().ToString("O")
                        : "",
                    ProfileRevision = device.ProfileRevision,
                    AppliedProfileRevision = device.AppliedProfileRevision,
                    ProfileHash = device.ProfileHash,
                    SyncPending = !string.IsNullOrWhiteSpace(device.PendingSyncId),
                    CreatedAtUtc = device.CreatedAtUtc.ToUniversalTime().ToString("O"),
                    UpdatedAtUtc = device.UpdatedAtUtc.ToUniversalTime().ToString("O"),
                })
                .ToArray(),
        };
    }

    private bool IsOnline(string deviceId, DateTime nowUtc)
    {
        return _lastSeenUtc.TryGetValue(deviceId, out var lastSeen)
            && nowUtc.ToUniversalTime() - lastSeen <= OnlineTtl;
    }

    private DeviceAuthorityData RequireData()
    {
        return _data ?? throw new CompanionDeviceAuthorityException(
            503,
            string.IsNullOrWhiteSpace(_loadError) ? "设备连接与共享配置信息暂不可用。" : _loadError);
    }

    private static CompanionDeviceRecord RequireRegisteredDevice(DeviceAuthorityData data, string deviceId)
    {
        return data.Devices.FirstOrDefault(device =>
                string.Equals(device.DeviceId, deviceId, StringComparison.Ordinal))
            ?? throw new CompanionDeviceAuthorityException(409, "当前设备未注册或设备记录已经变化，请重新连接。");
    }

    private static void RequirePrimary(DeviceAuthorityData data, string clientId)
    {
        if (!string.Equals(data.PrimaryDeviceId, clientId, StringComparison.Ordinal))
        {
            throw new CompanionDeviceAuthorityException(403, "只有当前主设备可以修改生效配置。");
        }
    }

    private static void RequireExpectedAuthority(DeviceAuthorityData data, long expected)
    {
        if (expected <= 0 || expected != data.AuthorityRevision)
        {
            throw Conflict("主设备或共享配置已变化，请刷新设备状态后重试。");
        }
    }

    private static CompanionDeviceAuthorityException Conflict(string message)
    {
        return new CompanionDeviceAuthorityException(409, message);
    }

    private static void ValidateProtocol(int protocolVersion, int profileSchemaVersion)
    {
        if (protocolVersion != ProtocolVersion)
        {
            throw new CompanionDeviceAuthorityException(409, $"不支持的设备协议版本：{protocolVersion}。");
        }
        if (profileSchemaVersion != ProfileSchemaVersion)
        {
            throw new CompanionDeviceAuthorityException(409, $"不支持的共享配置版本：{profileSchemaVersion}。");
        }
    }

    private static JsonElement ValidateAndCloneProfile(JsonElement profile, int schemaVersion = ProfileSchemaVersion)
    {
        if (profile.ValueKind != JsonValueKind.Object)
        {
            throw new CompanionDeviceAuthorityException(400, "共享配置必须是 JSON 对象。");
        }
        // 旧字段集合只允许磁盘迁移使用。所有 HTTP 请求严格接受 schema 10，
        // 不能把旧排序字段偷偷忽略，令调用方误认为已保存了不存在的设置。
        var fields = new HashSet<string>(ProfileFields, StringComparer.Ordinal);
        if (schemaVersion is 1 or 2) fields.Add("serviceOrderSortMode");
        if (schemaVersion == 1) fields.Remove("gameUiEnabled");
        if (schemaVersion < 9) fields.ExceptWith(new[] { "rareGameUiEnabled", "normalGameUiEnabled" });
        if (schemaVersion < 10) fields.ExceptWith(AutoResumeFields);
        if (schemaVersion is 5 or 6) fields.UnionWith(LegacyHighlightCountFields);
        if (schemaVersion < 5)
        {
            fields.ExceptWith(NewProfileFields);
            fields.Add("rareTargetHighlightColor");
            fields.Add("normalTargetHighlightColor");
        }
        else if (schemaVersion == 5)
        {
            fields.ExceptWith(PaletteFields);
            fields.Add("orderHighlightPalette");
        }
        RequireExactProperties(profile, fields, "共享配置");
        foreach (var field in ProfileBooleanFields)
        {
            if (schemaVersion == 1 && field == "gameUiEnabled" || schemaVersion < 5 && field == "rareGuestParticipationModuleEnabled") continue;
            if (schemaVersion < 9 && field is ("rareGameUiEnabled" or "normalGameUiEnabled")) continue;
            if (schemaVersion < 10 && field == "rareGuestParticipationAutoResumeEnabled") continue;
            RequireBoolean(profile, field);
        }

        RequireInteger(profile, "autoRareConcurrency", 1, 4);
        RequireInteger(profile, "autoNormalConcurrency", 1, 6);
        RequireInteger(profile, "autoMaxStepRetries", 1, 10);
        RequireInteger(profile, "autoMaxRollbacks", 0, 5);
        RequireInteger(profile, "recipeVariantLimitPerBase", 1, 8);
        if (schemaVersion >= 10)
        {
            RequireInteger(profile, "rareGuestParticipationAutoResumeThresholdPercent", 1, 100);
            RequireStringChoice(profile, "rareGuestParticipationAutoResumePosition", "front", "tail");
        }
        if (schemaVersion is 1 or 2) RequireStringChoice(profile, "serviceOrderSortMode", "ordered", "guest");
        RequireStringChoice(profile, "recommendationBudgetPolicy", "block", "warn", "ignore");
        if (schemaVersion < 5)
        {
            RequireColor(profile, "rareTargetHighlightColor");
            RequireColor(profile, "normalTargetHighlightColor");
        }
        else
        {
            if (schemaVersion is 5 or 6)
                foreach (var field in LegacyHighlightCountFields) RequireInteger(profile, field, 1, 8);
            ValidateIdArray(profile.GetProperty("managedRareGuestIds"), "手动调度名单", 512);
            foreach (var field in schemaVersion == 5 ? new[] { "orderHighlightPalette" } : PaletteFields)
            {
                var palette = profile.GetProperty(field);
                var minimum = schemaVersion == 5 ? 8 : 1;
                var maximum = schemaVersion == 5 ? 8 : schemaVersion < 8 ? 4 : 3;
                if (palette.ValueKind != JsonValueKind.Array || palette.GetArrayLength() < minimum || palette.GetArrayLength() > maximum)
                    throw new CompanionDeviceAuthorityException(400, $"{field} 必须包含 {minimum}–{maximum} 种颜色。");
                // 独立通道内部不得重复；跨通道同色合法，不以另一个通道的配置限制编辑。
                var colors = new HashSet<string>(StringComparer.Ordinal);
                foreach (var color in palette.EnumerateArray())
                    if (color.ValueKind != JsonValueKind.String || !ColorPattern.IsMatch(color.GetString()!) || !colors.Add(color.GetString()!))
                        throw new CompanionDeviceAuthorityException(400, "调色板颜色必须为不同的大写 #RRGGBB。");
            }
        }
        ValidateSortProfile(profile.GetProperty("recommendationSortProfile"), schemaVersion);
        ValidateExclusions(profile.GetProperty("recommendationExclusions"));
        ValidatePreferenceDependencies(profile);
        return profile.Clone();
    }

    private static void ValidateSortProfile(JsonElement value, int schemaVersion)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new CompanionDeviceAuthorityException(400, "推荐排序配置必须是对象。");
        }
        RequireExactProperties(value, new HashSet<string>(new[] { "preset", "objectives" }, StringComparer.Ordinal), "推荐排序配置");
        if (schemaVersion < 4) RequireStringChoice(value, "preset", "balanced", "resources", "profit", "simple");
        else RequireStringChoice(value, "preset", "balanced", "resources", "profit", "simple", "custom");
        var keys = new HashSet<string>(ObjectiveKeys, StringComparer.Ordinal);
        if (schemaVersion < 5) keys.Add("cookerAvailable");
        var objectives = value.GetProperty("objectives");
        if (objectives.ValueKind != JsonValueKind.Array || objectives.GetArrayLength() != keys.Count)
        {
            throw new CompanionDeviceAuthorityException(400, "推荐排序目标必须包含对应版本定义的全部项目。");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var objective in objectives.EnumerateArray())
        {
            if (objective.ValueKind != JsonValueKind.Object)
            {
                throw new CompanionDeviceAuthorityException(400, "推荐排序目标格式无效。");
            }
            RequireExactProperties(
                objective,
                new HashSet<string>(new[] { "key", "enabled", "weight", "direction" }, StringComparer.Ordinal),
                "推荐排序目标");
            var key = RequireString(objective, "key");
            if (!keys.Contains(key) || !seen.Add(key))
            {
                throw new CompanionDeviceAuthorityException(400, "推荐排序目标包含未知项或重复项。");
            }
            RequireBoolean(objective, "enabled");
            RequireInteger(objective, "weight", 0, 100);
            RequireStringChoice(objective, "direction", "asc", "desc");
        }
        if (schemaVersion >= 4 && value.GetProperty("preset").GetString() != "custom" && !SortMatchesPreset(value, schemaVersion))
            throw new CompanionDeviceAuthorityException(400, "固定评分预设不能修改参数，请使用自定义模式。");
    }

    /// <summary>
    /// 对照推荐引擎唯一的四组预设定义验证完整目标值，不复制权重表，也不在 HTTP
    /// 入口执行推荐计算。调用前已验证目标全集、唯一 key 和类型；目标数组排列不影响判定。
    /// 此比较仅用于固定预设校验和旧记录迁移，不把显式 custom 自动改回任何预设。
    /// </summary>
    private static bool SortMatchesPreset(JsonElement value, int schemaVersion = ProfileSchemaVersion)
    {
        var preset = value.GetProperty("preset").GetString()!;
        if (preset == "custom") return false;
        var expectedProfile = RecommendationEngine.BuildDefaultRecommendationSortProfile(preset);
        // v4 已禁止修改固定预设；先按原九项定义验证再迁移，避免把损坏记录当成有效自定义。
        if (schemaVersion < 5)
        {
            var weight = preset switch { "balanced" or "resources" => 60, "profit" => 50, "simple" => 70, _ => 60 };
            expectedProfile["objectives"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject
            { ["key"] = "cookerAvailable", ["enabled"] = true, ["weight"] = weight, ["direction"] = "desc" });
        }
        using var document = JsonDocument.Parse(expectedProfile.ToJsonString());
        var expected = document.RootElement.GetProperty("objectives").EnumerateArray()
            .ToDictionary(item => item.GetProperty("key").GetString()!, StringComparer.Ordinal);
        return value.GetProperty("objectives").EnumerateArray().All(item =>
        {
            var rule = expected[item.GetProperty("key").GetString()!];
            return item.GetProperty("enabled").GetBoolean() == rule.GetProperty("enabled").GetBoolean()
                && item.GetProperty("weight").GetInt32() == rule.GetProperty("weight").GetInt32()
                && item.GetProperty("direction").GetString() == rule.GetProperty("direction").GetString();
        });
    }

    private static void ValidateExclusions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new CompanionDeviceAuthorityException(400, "推荐排除项必须是对象。");
        }
        RequireExactProperties(
            value,
            new HashSet<string>(new[] { "excludedIngredientIds", "excludedBeverageIds" }, StringComparer.Ordinal),
            "推荐排除项");
        ValidateIdArray(value.GetProperty("excludedIngredientIds"), "排除食材");
        ValidateIdArray(value.GetProperty("excludedBeverageIds"), "排除酒水");
    }

    private static void ValidateIdArray(JsonElement value, string label, int maximum = 4096)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum)
        {
            throw new CompanionDeviceAuthorityException(400, $"{label}列表格式或数量无效。");
        }
        var previous = -1;
        foreach (var element in value.EnumerateArray())
        {
            if (!element.TryGetInt32(out var id) || id < 0 || id <= previous)
            {
                throw new CompanionDeviceAuthorityException(400, $"{label} ID 必须为严格递增的非负整数。");
            }
            previous = id;
        }
    }

    private static void ValidatePreferenceDependencies(JsonElement profile)
    {
        if (!profile.GetProperty("autoNormalCompleteOrder").GetBoolean()
            && (profile.GetProperty("autoNormalTakeBeverage").GetBoolean()
                || profile.GetProperty("autoNormalDeliverFood").GetBoolean()))
        {
            throw new CompanionDeviceAuthorityException(400, "普客送餐子步骤要求启用完成订单。");
        }
        if (!profile.GetProperty("autoPrepCompleteOrder").GetBoolean()
            && (profile.GetProperty("autoPrepTakeBeverage").GetBoolean()
                || profile.GetProperty("autoPrepCollectCooking").GetBoolean()))
        {
            throw new CompanionDeviceAuthorityException(400, "稀客送餐子步骤要求启用完成订单。");
        }
    }

    private static void RequireExactProperties(JsonElement value, HashSet<string> expected, string label)
    {
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Count
            || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length
            || actual.Any(name => !expected.Contains(name)))
        {
            throw new CompanionDeviceAuthorityException(400, $"{label}字段与当前 schema 不一致。");
        }
    }

    private static void RequireBoolean(JsonElement value, string name)
    {
        var kind = value.GetProperty(name).ValueKind;
        if (kind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new CompanionDeviceAuthorityException(400, $"共享配置字段 {name} 必须是布尔值。");
        }
    }

    private static void RequireInteger(JsonElement value, string name, int min, int max)
    {
        // TryGetInt32只处理Number；先判类型，字符串/null等非法请求应得到明确400，
        // 不能以InvalidOperationException进入服务器内部异常分支。
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var number) || number < min || number > max)
        {
            throw new CompanionDeviceAuthorityException(400, $"共享配置字段 {name} 超出允许范围。");
        }
    }

    private static string RequireString(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String)
        {
            throw new CompanionDeviceAuthorityException(400, $"字段 {name} 必须是字符串。");
        }
        return property.GetString() ?? "";
    }

    private static void RequireStringChoice(JsonElement value, string name, params string[] choices)
    {
        var text = RequireString(value, name);
        if (!choices.Contains(text, StringComparer.Ordinal))
        {
            throw new CompanionDeviceAuthorityException(400, $"字段 {name} 的值无效。");
        }
    }

    private static void RequireColor(JsonElement value, string name)
    {
        var color = RequireString(value, name);
        if (!ColorPattern.IsMatch(color))
        {
            throw new CompanionDeviceAuthorityException(400, $"字段 {name} 必须是规范的大写 #RRGGBB 颜色。");
        }
    }

    private static string NormalizePlatform(string value)
    {
        return value switch
        {
            "windows" => value,
            "android" => value,
            "browser" => value,
            _ => throw new CompanionDeviceAuthorityException(400, "platform 必须是 windows、android 或 browser。"),
        };
    }

    private static string NormalizeMetadata(string value, int maxLength, string field)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Length == 0 || normalized.Length > maxLength
            || normalized.Any(character => char.IsControl(character)))
        {
            throw new CompanionDeviceAuthorityException(400, $"{field} 格式无效。");
        }
        return normalized;
    }

    private static string NormalizeLabel(string value)
    {
        return NormalizeMetadata(value, 48, "设备名称");
    }

    private static string ComputeProfileHash(JsonElement profile)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalJson(writer, profile);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var value in element.EnumerateArray()) WriteCanonicalJson(writer, value);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static DeviceAuthorityData CreateEmptyData()
    {
        return new DeviceAuthorityData
        {
            Version = StoreSchemaVersion,
            RegistryId = Guid.NewGuid().ToString("N"),
            StateRevision = 0,
            AuthorityRevision = 0,
        };
    }

    private static DeviceAuthorityData CloneData(DeviceAuthorityData data)
    {
        return JsonSerializer.Deserialize<DeviceAuthorityData>(JsonSerializer.Serialize(data, JsonOptions), JsonOptions)
            ?? throw new InvalidDataException("Failed to clone companion device authority state.");
    }

    private static void ValidateStoredData(DeviceAuthorityData data, int schemaVersion = StoreSchemaVersion)
    {
        if (data.Version != schemaVersion) throw new InvalidDataException($"Unsupported device authority schema version: {data.Version}.");
        if (data.RegistryId.Length != 32 || data.RegistryId.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Device registry ID is invalid.");
        }
        data.Devices ??= new List<CompanionDeviceRecord>();
        if (data.Devices.Count > MaxDevices) throw new InvalidDataException("Too many companion devices are stored.");
        if (data.StateRevision < 0 || data.AuthorityRevision < 0) throw new InvalidDataException("Device authority revisions are invalid.");
        if (data.Devices.Count == 0)
        {
            if (!string.IsNullOrEmpty(data.PrimaryDeviceId) || data.AuthorityRevision != 0)
            {
                throw new InvalidDataException("Empty device registry has a primary device.");
            }
            return;
        }
        if (data.Devices.Select(device => device.DeviceId).Distinct(StringComparer.Ordinal).Count() != data.Devices.Count)
        {
            throw new InvalidDataException("Duplicate device IDs are stored.");
        }
        if (data.Devices.Count(device => string.Equals(device.DeviceId, data.PrimaryDeviceId, StringComparison.Ordinal)) != 1
            || data.AuthorityRevision <= 0)
        {
            throw new InvalidDataException("Device authority primary identity is invalid.");
        }
        foreach (var device in data.Devices)
        {
            if (!IsValidDeviceId(device.DeviceId)) throw new InvalidDataException("Stored device ID is invalid.");
            _ = NormalizeLabel(device.Label);
            _ = NormalizePlatform(device.Platform);
            _ = NormalizeMetadata(device.AppVersion, 32, "appVersion");
            if (device.ProfileRevision <= 0
                || device.AppliedProfileRevision <= 0
                || device.AppliedProfileRevision > device.ProfileRevision)
            {
                throw new InvalidDataException("Stored device profile revision is invalid.");
            }
            var profile = ValidateAndCloneProfile(device.Profile, schemaVersion);
            if (!string.Equals(device.ProfileHash, ComputeProfileHash(profile), StringComparison.Ordinal))
            {
                throw new InvalidDataException("Stored device profile hash is invalid.");
            }
            if (!string.IsNullOrWhiteSpace(device.PendingSyncId)
                && (device.PendingSyncId.Length != 32 || device.PendingSyncId.Any(character => !Uri.IsHexDigit(character))))
            {
                throw new InvalidDataException("Stored pending sync ID is invalid.");
            }
            if (string.IsNullOrWhiteSpace(device.PendingSyncId)
                && device.AppliedProfileRevision != device.ProfileRevision)
            {
                throw new InvalidDataException("Stored device has an unacknowledged profile without a sync ID.");
            }
        }
    }

    private static bool IsValidDeviceId(string value)
    {
        return value.Length is >= 16 and <= 64 && value.All(character =>
            character is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '-');
    }
}

internal sealed class CompanionDeviceAuthorityException : Exception
{
    public CompanionDeviceAuthorityException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

internal sealed class CompanionDeviceAuthorityMutationResult
{
    public bool Changed { get; init; }
    public CompanionDeviceAuthorityStateDto State { get; init; } = new();
}

internal sealed class DeviceAuthorityData
{
    // 缺少版本的文件不作为 v1 猜测，必须拒绝加载并保留原始内容。
    public int Version { get; set; }
    public string RegistryId { get; set; } = "";
    public long AuthorityRevision { get; set; }
    public long StateRevision { get; set; }
    public string PrimaryDeviceId { get; set; } = "";
    public List<CompanionDeviceRecord> Devices { get; set; } = new();
}

internal sealed class CompanionDeviceRecord
{
    public string DeviceId { get; set; } = "";
    public string Label { get; set; } = "";
    public string Platform { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public long ProfileRevision { get; set; }
    public long AppliedProfileRevision { get; set; }
    public string ProfileHash { get; set; } = "";
    public JsonElement Profile { get; set; }
    public string PendingSyncId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

internal sealed class CompanionDeviceRegisterRequest
{
    public int ProtocolVersion { get; init; }
    public int ProfileSchemaVersion { get; init; }
    public string Platform { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public JsonElement Profile { get; init; }
}

internal sealed class CompanionDeviceProfileUpdateRequest
{
    public int ProtocolVersion { get; init; }
    public int ProfileSchemaVersion { get; init; }
    public long ExpectedAuthorityRevision { get; init; }
    public long ExpectedProfileRevision { get; init; }
    public JsonElement Profile { get; init; }
}

internal sealed class CompanionDeviceSetPrimaryRequest
{
    public int ProtocolVersion { get; init; }
    public long ExpectedAuthorityRevision { get; init; }
    public string DeviceId { get; init; } = "";
}

internal sealed class CompanionDeviceSyncRequest
{
    public int ProtocolVersion { get; init; }
    public long ExpectedAuthorityRevision { get; init; }
    public string DeviceId { get; init; } = "";
}

internal sealed class CompanionDeviceSyncAckRequest
{
    public int ProtocolVersion { get; init; }
    public string SyncId { get; init; } = "";
    public long ProfileRevision { get; init; }
    public string ProfileHash { get; init; } = "";
}

internal sealed class CompanionDeviceRenameRequest
{
    public int ProtocolVersion { get; init; }
    public string Label { get; init; } = "";
}

internal sealed class CompanionDeviceForgetRequest
{
    public int ProtocolVersion { get; init; }
    public long ExpectedAuthorityRevision { get; init; }
    public string DeviceId { get; init; } = "";
}

internal sealed class CompanionDeviceAuthorityStateDto
{
    public bool Ok { get; init; }
    public int ProtocolVersion { get; init; }
    public int ProfileSchemaVersion { get; init; }
    public string RegistryId { get; init; } = "";
    public long AuthorityRevision { get; init; }
    public long StateRevision { get; init; }
    public string PrimaryDeviceId { get; init; } = "";
    public string CurrentDeviceId { get; init; } = "";
    public bool CurrentDeviceIsPrimary { get; init; }
    public long ActiveProfileRevision { get; init; }
    public string ActiveProfileHash { get; init; } = "";
    public JsonElement ActiveProfile { get; init; }
    public long CurrentDeviceProfileRevision { get; init; }
    public string CurrentDeviceProfileHash { get; init; } = "";
    public JsonElement CurrentDeviceProfile { get; init; }
    public string? PendingSyncId { get; init; }
    public IReadOnlyList<CompanionDeviceDto> Devices { get; init; } = Array.Empty<CompanionDeviceDto>();
    public string? Error { get; init; }
}

internal sealed class CompanionDeviceDto
{
    public string DeviceId { get; init; } = "";
    public string Label { get; init; } = "";
    public string Platform { get; init; } = "";
    public string AppVersion { get; init; } = "";
    public bool IsCurrent { get; init; }
    public bool IsPrimary { get; init; }
    public bool Online { get; init; }
    public string LastSeenAtUtc { get; init; } = "";
    public long ProfileRevision { get; init; }
    public long AppliedProfileRevision { get; init; }
    public string ProfileHash { get; init; } = "";
    public bool SyncPending { get; init; }
    public string CreatedAtUtc { get; init; } = "";
    public string UpdatedAtUtc { get; init; } = "";
}
