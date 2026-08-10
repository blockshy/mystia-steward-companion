using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 持久保存伴随设备及唯一生效的功能配置权威。
/// </summary>
/// <remarks>
/// 设备本地的窗口、连接和页面偏好不进入此存储。所有 mutation 都先构建新状态并原子落盘，
/// 文件损坏或未来 schema 会使配置写入与运行时 writer fail-closed，且不会覆盖原文件。
/// </remarks>
internal sealed class CompanionDeviceAuthorityStore
{
    public const int ProtocolVersion = 1;
    public const int ProfileSchemaVersion = 1;
    private const int StoreSchemaVersion = 1;
    private const int MaxDevices = 32;
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(20);
    private static readonly Regex ColorPattern = new("^#[0-9A-F]{6}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ProfileBooleanFields = new(StringComparer.Ordinal)
    {
        "automationEnabled",
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
            "rareTargetHighlightColor",
            "normalTargetHighlightColor",
            "serviceOrderSortMode",
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
        "cookerAvailable",
    };
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
        out string error)
    {
        lock (_lock)
        {
            if (_data == null)
            {
                error = string.IsNullOrWhiteSpace(_loadError)
                    ? "伴随设备权威尚未初始化，请先完成设备注册。"
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
                error = "配置权威版本已经变化，请刷新设备状态后重试。";
                return false;
            }

            _lastSeenUtc[clientId] = nowUtc.ToUniversalTime();
            error = "";
            return true;
        }
    }

    public long ReadAuthorityRevision()
    {
        lock (_lock) return _data?.AuthorityRevision ?? 0;
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _data = CreateEmptyData();
                return;
            }

            try
            {
                var data = JsonFileStore.LoadOrCreate<DeviceAuthorityData>(_path, JsonOptions);
                ValidateStoredData(data);
                _data = data;
                _loadError = "";
            }
            catch (Exception ex)
            {
                _data = null;
                _loadError = "设备配置文件无法读取，原文件已保留；请检查 companion-devices.json。";
                _log?.LogError($"Failed to load companion device authority from '{_path}': {ex}");
            }
        }
    }

    private void SaveAndCommit(DeviceAuthorityData next)
    {
        ValidateStoredData(next);
        JsonFileStore.Save(_path, next, JsonOptions);
        _data = next;
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
            string.IsNullOrWhiteSpace(_loadError) ? "设备配置权威不可用。" : _loadError);
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
            throw Conflict("配置权威版本已经变化，请刷新设备状态后重试。");
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

    private static JsonElement ValidateAndCloneProfile(JsonElement profile)
    {
        if (profile.ValueKind != JsonValueKind.Object)
        {
            throw new CompanionDeviceAuthorityException(400, "共享配置必须是 JSON 对象。");
        }
        RequireExactProperties(profile, ProfileFields, "共享配置");
        foreach (var field in ProfileBooleanFields)
        {
            RequireBoolean(profile, field);
        }

        RequireInteger(profile, "autoRareConcurrency", 1, 4);
        RequireInteger(profile, "autoNormalConcurrency", 1, 6);
        RequireInteger(profile, "autoMaxStepRetries", 1, 10);
        RequireInteger(profile, "autoMaxRollbacks", 0, 5);
        RequireInteger(profile, "recipeVariantLimitPerBase", 1, 8);
        RequireStringChoice(profile, "serviceOrderSortMode", "ordered", "guest");
        RequireStringChoice(profile, "recommendationBudgetPolicy", "block", "warn", "ignore");
        RequireColor(profile, "rareTargetHighlightColor");
        RequireColor(profile, "normalTargetHighlightColor");
        ValidateSortProfile(profile.GetProperty("recommendationSortProfile"));
        ValidateExclusions(profile.GetProperty("recommendationExclusions"));
        ValidatePreferenceDependencies(profile);
        return profile.Clone();
    }

    private static void ValidateSortProfile(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new CompanionDeviceAuthorityException(400, "推荐排序配置必须是对象。");
        }
        RequireExactProperties(value, new HashSet<string>(new[] { "preset", "objectives" }, StringComparer.Ordinal), "推荐排序配置");
        RequireStringChoice(value, "preset", "balanced", "resources", "profit", "simple");
        var objectives = value.GetProperty("objectives");
        if (objectives.ValueKind != JsonValueKind.Array || objectives.GetArrayLength() != ObjectiveKeys.Count)
        {
            throw new CompanionDeviceAuthorityException(400, "推荐排序目标必须包含当前版本定义的全部 9 项。");
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
            if (!ObjectiveKeys.Contains(key) || !seen.Add(key))
            {
                throw new CompanionDeviceAuthorityException(400, "推荐排序目标包含未知项或重复项。");
            }
            RequireBoolean(objective, "enabled");
            RequireInteger(objective, "weight", 0, 100);
            RequireStringChoice(objective, "direction", "asc", "desc");
        }
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

    private static void ValidateIdArray(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 4096)
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
        if (!value.GetProperty(name).TryGetInt32(out var number) || number < min || number > max)
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

    private static void ValidateStoredData(DeviceAuthorityData data)
    {
        if (data.Version != StoreSchemaVersion) throw new InvalidDataException($"Unsupported device authority schema version: {data.Version}.");
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
            var profile = ValidateAndCloneProfile(device.Profile);
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
    public int Version { get; set; } = 1;
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
