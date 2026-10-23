using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Business.Domain.Recommendation;

var root = Path.Combine(Path.GetTempPath(), $"mystia-local-api-storage-{Guid.NewGuid():N}");
Directory.CreateDirectory(root);
var log = Logger.CreateLogSource("local-api-storage-smoke");

try
{
    VerifyCorruptFavoriteIsPreserved(root, log);
    VerifyNullableFavoriteExtras(root, log);
    VerifyMutationJsonEscaping(root, log);
    VerifyFavoriteManagementMutations(root, log);
    VerifyCustomRecipeReadDoesNotWrite(root, log);
    VerifyCustomRecipeManagement(root, log);
    VerifyCustomRecipeModes(root, log);
    VerifyCustomRecipeMigration(root, log);
    VerifyCorruptCustomRecipeIsPreserved(root, log);
    VerifyFutureSchemasArePreserved(root, log);
    VerifyCompanionDeviceAuthority(root, log);
    foreach (var version in new[] { 1, 2, 3 }) VerifyDeviceAuthorityLegacyMigration(root, log, version);
    VerifyScoringModes(root, log);
    VerifyCorruptDeviceAuthorityIsPreserved(root, log);
    Console.WriteLine("PASS: local API file stores and companion device configuration authority passed storage, CAS and corruption checks.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}
finally
{
    Logger.Sources.Remove(log);
    Directory.Delete(root, recursive: true);
}

static void VerifyCompanionDeviceAuthority(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "companion-devices.json");
    var store = new CompanionDeviceAuthorityStore(path, log);
    var now = DateTime.UtcNow;
    var primaryProfile = BuildSharedProfile(automationEnabled: true, rareConcurrency: 2);
    var secondaryProfile = BuildSharedProfile(automationEnabled: false, rareConcurrency: 3);

    var primary = store.Register(
        "11111111-1111-1111-1111-111111111111",
        "Windows 主设备",
        RegisterRequest("windows", primaryProfile),
        now);
    AssertEqual(true, primary.CurrentDeviceIsPrimary, "The first registered device did not become primary.");
    AssertEqual(1L, primary.AuthorityRevision, "Initial authority revision is invalid.");
    AssertEqual(1, primary.Devices.Count, "Initial device registry count is invalid.");

    var secondary = store.Register(
        "22222222-2222-2222-2222-222222222222",
        "Android 设备",
        RegisterRequest("android", secondaryProfile),
        now.AddSeconds(1));
    AssertEqual(false, secondary.CurrentDeviceIsPrimary, "The second registered device unexpectedly became primary.");
    AssertEqual(
        true,
        secondary.ActiveProfile.GetProperty("automationEnabled").GetBoolean(),
        "A secondary device did not receive the primary active profile.");
    AssertEqual(
        false,
        secondary.CurrentDeviceProfile.GetProperty("automationEnabled").GetBoolean(),
        "A secondary device's own stored profile was overwritten during registration.");

    ExpectAuthorityError(
        403,
        () => store.UpdatePrimaryProfile(
            secondary.CurrentDeviceId,
            new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1,
                ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                ExpectedAuthorityRevision = secondary.AuthorityRevision,
                ExpectedProfileRevision = secondary.CurrentDeviceProfileRevision,
                Profile = secondaryProfile,
            },
            now.AddSeconds(2)));

    var updatedProfile = BuildSharedProfile(automationEnabled: true, rareConcurrency: 4);
    var updated = store.UpdatePrimaryProfile(
        primary.CurrentDeviceId,
        new CompanionDeviceProfileUpdateRequest
        {
            ProtocolVersion = 1,
            ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
            ExpectedAuthorityRevision = secondary.AuthorityRevision,
            ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
            Profile = updatedProfile,
        },
        now.AddSeconds(3));
    AssertEqual(2L, updated.AuthorityRevision, "Changing the active profile did not advance authority.");
    AssertEqual(4, updated.ActiveProfile.GetProperty("autoRareConcurrency").GetInt32(), "The active profile update was not persisted.");
    ExpectAuthorityError(
        409,
        () => store.UpdatePrimaryProfile(
            primary.CurrentDeviceId,
            new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1,
                ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                ExpectedAuthorityRevision = secondary.AuthorityRevision,
                ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
                Profile = primaryProfile,
            },
            now.AddSeconds(4)));

    AssertEqual(
        true,
        store.TryAuthorizePrimary(primary.CurrentDeviceId, updated.AuthorityRevision, now.AddSeconds(4), out var primaryWriterError),
        $"The current primary was not authorized as runtime writer: {primaryWriterError}");
    AssertEqual(
        false,
        store.TryAuthorizePrimary(secondary.CurrentDeviceId, updated.AuthorityRevision, now.AddSeconds(4), out _),
        "A secondary device was authorized as runtime writer.");

    var synced = store.SyncFromPrimary(
        secondary.CurrentDeviceId,
        new CompanionDeviceSyncRequest
        {
            ProtocolVersion = 1,
            ExpectedAuthorityRevision = updated.AuthorityRevision,
            DeviceId = secondary.CurrentDeviceId,
        },
        now.AddSeconds(5));
    var syncedDevice = synced.Devices.Single(device => device.DeviceId == secondary.CurrentDeviceId);
    AssertEqual(true, syncedDevice.SyncPending, "One-way profile synchronization did not create a pending acknowledgement.");
    var secondaryPending = store.Read(secondary.CurrentDeviceId, now.AddSeconds(6));
    AssertEqual(true, !string.IsNullOrWhiteSpace(secondaryPending.PendingSyncId), "Pending sync ID was not exposed to its target device.");
    var acknowledged = store.AcknowledgeSync(
        secondary.CurrentDeviceId,
        new CompanionDeviceSyncAckRequest
        {
            ProtocolVersion = 1,
            SyncId = secondaryPending.PendingSyncId!,
            ProfileRevision = secondaryPending.CurrentDeviceProfileRevision,
            ProfileHash = secondaryPending.CurrentDeviceProfileHash,
        },
        now.AddSeconds(7));
    AssertEqual(null, acknowledged.PendingSyncId, "Acknowledged sync was not retired.");

    var switched = store.SetPrimary(
        primary.CurrentDeviceId,
        new CompanionDeviceSetPrimaryRequest
        {
            ProtocolVersion = 1,
            ExpectedAuthorityRevision = acknowledged.AuthorityRevision,
            DeviceId = secondary.CurrentDeviceId,
        },
        now.AddSeconds(8));
    AssertEqual(true, switched.Changed, "Primary transfer did not report a state change.");
    AssertEqual(secondary.CurrentDeviceId, switched.State.PrimaryDeviceId, "Primary transfer selected the wrong device.");
    AssertEqual(3L, switched.State.AuthorityRevision, "Primary transfer did not advance authority.");
    AssertEqual(
        switched.State.ActiveProfileHash,
        switched.State.CurrentDeviceProfileHash,
        "The new primary did not activate its exact stored profile.");
    AssertEqual(
        false,
        store.TryAuthorizePrimary(primary.CurrentDeviceId, updated.AuthorityRevision, now.AddSeconds(8), out _),
        "The former primary retained runtime-writer authority after transfer.");
    AssertEqual(
        true,
        store.TryAuthorizePrimary(secondary.CurrentDeviceId, switched.State.AuthorityRevision, now.AddSeconds(8), out var secondaryWriterError),
        $"The new primary did not receive runtime-writer authority: {secondaryWriterError}");

    var reloaded = new CompanionDeviceAuthorityStore(path, log).Register(
        secondary.CurrentDeviceId,
        "Ignored registration label",
        RegisterRequest("android", secondaryProfile),
        now.AddSeconds(9));
    AssertEqual(secondary.CurrentDeviceId, reloaded.PrimaryDeviceId, "Primary identity did not survive a store reload.");
    AssertEqual("Android 设备", reloaded.Devices.Single(device => device.IsCurrent).Label, "Registration unexpectedly renamed an existing device.");

    using var invalidDocument = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["automationEnabled"] = true,
    }));
    ExpectAuthorityError(
        400,
        () => new CompanionDeviceAuthorityStore(Path.Combine(root, "invalid-profile.json"), log).Register(
            "33333333-3333-3333-3333-333333333333",
            "Invalid",
            RegisterRequest("browser", invalidDocument.RootElement.Clone()),
            now));
}

static void VerifyCorruptDeviceAuthorityIsPreserved(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "companion-devices-corrupt.json");
    const string corrupt = "{ broken device registry";
    File.WriteAllText(path, corrupt, Encoding.UTF8);
    var store = new CompanionDeviceAuthorityStore(path, log);
    ExpectAuthorityError(
        503,
        () => store.Register(
            "44444444-4444-4444-4444-444444444444",
            "Device",
            RegisterRequest("windows", BuildSharedProfile(false, 2)),
            DateTime.UtcNow));
    AssertEqual(corrupt, File.ReadAllText(path, Encoding.UTF8), "A corrupt device registry was overwritten.");
}

/// <summary>
/// 独立构造旧磁盘记录验证一次性迁移。哈希按历史规范生成，不能先用新版存储写出
/// 当前版本再删字段冒充旧输入；所有文件仅位于本测试创建的临时根目录。
/// </summary>
static void VerifyDeviceAuthorityLegacyMigration(string root, ManualLogSource log, int legacyVersion)
{
    var checks = 0;
    void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    var now = DateTime.UtcNow;
    const string primaryId = "11111111-1111-1111-1111-111111111111";
    const string secondaryId = "22222222-2222-2222-2222-222222222222";
    const string oldPending = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    var first = JsonNode.Parse(BuildSharedProfile(true, 2).GetRawText())!.AsObject();
    if (legacyVersion == 1) first.Remove("gameUiEnabled");
    if (legacyVersion < 3) first["serviceOrderSortMode"] = "guest";
    // 旧界面允许名称仍是 balanced 时改九项参数；迁移必须保留这些数值并标记 custom。
    first["recommendationSortProfile"]!["preset"] = "balanced";
    first["rareSeatHighlightEnabled"] = true;
    first["rareTargetHighlightColor"] = "#12ABEF";
    var second = JsonNode.Parse(BuildSharedProfile(true, 4).GetRawText())!.AsObject();
    if (legacyVersion == 1) second.Remove("gameUiEnabled");
    if (legacyVersion < 3) second["serviceOrderSortMode"] = "ordered";
    // 另一设备保持真正的固定预设，证明迁移不会一律把所有旧记录改成自定义。
    second["recommendationSortProfile"] = RecommendationEngine.BuildDefaultRecommendationSortProfile("profit");
    var legacy = new DeviceAuthorityData
    {
        Version = legacyVersion, RegistryId = "0123456789abcdef0123456789abcdef",
        AuthorityRevision = 7, StateRevision = 12, PrimaryDeviceId = primaryId,
        Devices = new()
        {
            new()
            {
                DeviceId = primaryId, Label = "旧主设备", Platform = "windows", AppVersion = "legacy-test",
                Profile = JsonSerializer.SerializeToElement(first), ProfileRevision = 5, AppliedProfileRevision = 5,
                CreatedAtUtc = now.AddDays(-2), UpdatedAtUtc = now.AddDays(-1),
            },
            new()
            {
                DeviceId = secondaryId, Label = "旧查看设备", Platform = "android", AppVersion = "legacy-test",
                Profile = JsonSerializer.SerializeToElement(second), ProfileRevision = 9, AppliedProfileRevision = 8,
                PendingSyncId = oldPending, CreatedAtUtc = now.AddDays(-2), UpdatedAtUtc = now.AddDays(-1),
            },
        },
    };
    foreach (var record in legacy.Devices) record.ProfileHash = CanonicalFixtureHash(record.Profile);
    var original = JsonSerializer.Serialize(legacy, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var path = Path.Combine(root, $"devices-v{legacyVersion}-migration.json");
    File.WriteAllText(path, original, new UTF8Encoding(false));
    var store = new CompanionDeviceAuthorityStore(path, log);
    var primary = store.Read(primaryId, now);
    var secondary = store.Read(secondaryId, now);
    Check(primary.ProfileSchemaVersion == CompanionDeviceAuthorityStore.ProfileSchemaVersion, "旧配置未升级为当前 schema。");
    Check(primary.RegistryId == legacy.RegistryId && primary.PrimaryDeviceId == primaryId, "迁移改变了注册表或主设备身份。");
    Check(primary.AuthorityRevision == 8 && primary.StateRevision == 13, "迁移必须使旧权威失效且仅推进一次状态。");
    Check(primary.CurrentDeviceProfileRevision == 6 && secondary.CurrentDeviceProfileRevision == 10, "迁移必须为每个设备生成新配置修订。");
    Check(primary.ActiveProfile.GetProperty("gameUiEnabled").GetBoolean() == (legacyVersion == 1),
        "仅 v1 按旧分项推导总开关；v2 显式关闭不得被重新开启。");
    Check(!secondary.CurrentDeviceProfile.GetProperty("gameUiEnabled").GetBoolean(), "仅自动化开启不能误开启游戏界面辅助。");
    foreach (var before in legacy.Devices)
    {
        var state = before.DeviceId == primaryId ? primary : secondary;
        Check(!state.CurrentDeviceProfile.TryGetProperty("serviceOrderSortMode", out _), "迁移后仍保留已删除的订单排序字段。");
        foreach (var field in before.Profile.EnumerateObject().Where(field => field.Name is not "serviceOrderSortMode" and not "recommendationSortProfile"))
            Check(field.Value.GetRawText() == state.CurrentDeviceProfile.GetProperty(field.Name).GetRawText(), "迁移改写了既有偏好：" + field.Name);
        var oldSort = before.Profile.GetProperty("recommendationSortProfile");
        var newSort = state.CurrentDeviceProfile.GetProperty("recommendationSortProfile");
        Check(oldSort.GetProperty("objectives").GetRawText() == newSort.GetProperty("objectives").GetRawText(), "迁移改变了目标数值或顺序。");
        Check(newSort.GetProperty("preset").GetString() == (before.DeviceId == primaryId ? "custom" : "profit"), "迁移没有准确区分固定预设和历史自定义值。");
        if (legacyVersion < 3 || before.DeviceId == primaryId)
            Check(state.CurrentDeviceProfileHash != before.ProfileHash, "内容改变后的配置必须使用新哈希。");
        Check(state.CurrentDeviceProfileHash == CanonicalFixtureHash(state.CurrentDeviceProfile), "迁移后哈希与精确配置内容不一致。");
        Check(!string.IsNullOrEmpty(state.PendingSyncId) && state.PendingSyncId != before.PendingSyncId, "迁移必须撤销旧 ACK 并生成新 pending 标识。");
        Check(state.Devices.Single(device => device.IsCurrent).AppliedProfileRevision == before.AppliedProfileRevision, "客户端持久化确认前不得推进 applied 修订。");
    }
    var migrated = File.ReadAllText(path, Encoding.UTF8);
    using (var document = JsonDocument.Parse(migrated))
        Check(document.RootElement.GetProperty("version").GetInt32() == CompanionDeviceAuthorityStore.ProfileSchemaVersion, "新磁盘格式未原子落盘。");
    var reloaded = new CompanionDeviceAuthorityStore(path, log).Read(primaryId, now);
    Check(File.ReadAllText(path, Encoding.UTF8) == migrated, "第二次启动不应重写已迁移文件。");
    Check(reloaded.AuthorityRevision == primary.AuthorityRevision
        && reloaded.CurrentDeviceProfileRevision == primary.CurrentDeviceProfileRevision
        && reloaded.PendingSyncId == primary.PendingSyncId, "第二次启动重复迁移或更换待确认配置。");

    // 新权威修订不能代替客户端对新配置的持久化确认；写入口和普通配置更新均须等待 ACK。
    Check(!store.TryAuthorizePrimary(primaryId, primary.AuthorityRevision, now, out _), "迁移后未 ACK 的主设备获得了 writer 权限。");
    ExpectAuthorityError(409, () => store.UpdatePrimaryProfile(primaryId, new CompanionDeviceProfileUpdateRequest
    {
        ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
        ExpectedAuthorityRevision = primary.AuthorityRevision, ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
        Profile = primary.CurrentDeviceProfile,
    }, now));
    checks++;
    ExpectAuthorityError(409, () => store.AcknowledgeSync(secondaryId, new CompanionDeviceSyncAckRequest
    {
        ProtocolVersion = 1, SyncId = oldPending,
        ProfileRevision = legacy.Devices[1].ProfileRevision, ProfileHash = legacy.Devices[1].ProfileHash,
    }, now));
    checks++;
    ExpectAuthorityError(409, () => store.AcknowledgeSync(primaryId, new CompanionDeviceSyncAckRequest
    {
        ProtocolVersion = 1, SyncId = primary.PendingSyncId!,
        ProfileRevision = primary.CurrentDeviceProfileRevision, ProfileHash = legacy.Devices[0].ProfileHash,
    }, now));
    checks++;
    foreach (var state in new[] { primary, secondary })
    {
        var acknowledged = store.AcknowledgeSync(state.CurrentDeviceId, new CompanionDeviceSyncAckRequest
        {
            ProtocolVersion = 1, SyncId = state.PendingSyncId!,
            ProfileRevision = state.CurrentDeviceProfileRevision, ProfileHash = state.CurrentDeviceProfileHash,
        }, now);
        Check(acknowledged.PendingSyncId == null, "精确新配置 ACK 应完成同步。");
        Check(acknowledged.Devices.Single(device => device.IsCurrent).AppliedProfileRevision == state.CurrentDeviceProfileRevision,
            "ACK 后 applied 修订应等于已确认新配置。");
    }
    Check(store.TryAuthorizePrimary(primaryId, primary.AuthorityRevision, now, out _), "精确新配置 ACK 后未恢复主设备 writer 权限。");
    Check(new CompanionDeviceAuthorityStore(path, log).Read(primaryId, now).PendingSyncId == null, "ACK 状态未持久保存。");

    // 迁移仅限已验证磁盘记录；新 HTTP 更新不能接受旧 schema 或已删除字段，
    // 否则客户端会把无效排序开关误认作保存成功。拒绝必须发生在任何写盘之前。
    var obsolete = JsonNode.Parse(primary.CurrentDeviceProfile.GetRawText())!.AsObject();
    obsolete["serviceOrderSortMode"] = "guest";
    var savedBeforeRejection = File.ReadAllText(path, Encoding.UTF8);
    foreach (var version in new[] { 3, CompanionDeviceAuthorityStore.ProfileSchemaVersion })
    {
        ExpectAuthorityError(version == 3 ? 409 : 400, () => store.UpdatePrimaryProfile(primaryId,
            new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1, ProfileSchemaVersion = version,
                ExpectedAuthorityRevision = primary.AuthorityRevision, ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
                Profile = JsonSerializer.SerializeToElement(obsolete),
            }, now));
        checks++;
        Check(File.ReadAllText(path, Encoding.UTF8) == savedBeforeRejection, "旧配置请求改写了当前存储。");
    }

    // 历史迁移和当前版本普通读取采用相同的严格外层边界。除 profile 外，还必须验证
    // root/device 的字段全集与非空字符串类型，避免 POCO 默认值吞掉缺字段后重写原件。
    foreach (var source in new[] { (Version: legacyVersion, Json: original), (Version: CompanionDeviceAuthorityStore.ProfileSchemaVersion, Json: migrated) })
    foreach (var corruption in new[]
    {
        "hash", "field", "missing-field", "future", "missing-version", "missing-state",
        "missing-pending", "unknown-root", "unknown-device", "duplicate-root", "duplicate-device",
        "null-registry", "null-label", "null-pending",
    })
    {
        var broken = JsonNode.Parse(source.Json)!.AsObject();
        var record = broken["devices"]![0]!.AsObject();
        if (corruption == "hash") record["profileHash"] = new string('0', 64);
        if (corruption is "field" or "missing-field")
        {
            var invalidProfile = record["profile"]!.AsObject();
            if (corruption == "field") invalidProfile["unknownPreference"] = true;
            else invalidProfile.Remove("autoRareOrderEnabled");
            // 即使重算了哈希，也必须按旧 schema 的精确字段集合拒绝。
            record["profileHash"] = CanonicalFixtureHash(JsonSerializer.SerializeToElement(invalidProfile));
        }
        if (corruption == "future") broken["version"] = 999;
        if (corruption == "missing-version") broken.Remove("version");
        if (corruption == "missing-state") broken.Remove("stateRevision");
        if (corruption == "missing-pending") record.Remove("pendingSyncId");
        if (corruption == "unknown-root") broken["unexpected"] = true;
        if (corruption == "unknown-device") record["unexpected"] = true;
        if (corruption == "null-registry") broken["registryId"] = null;
        if (corruption == "null-label") record["label"] = null;
        if (corruption == "null-pending") record["pendingSyncId"] = null;
        var contents = broken.ToJsonString();
        // JsonObject 不支持重复键，所以仅在序列化后的离线夹具中注入重复字段。
        // 使用与当前值相同的重复项，证明拒绝来自严格结构而非值恰好不合法。
        if (corruption == "duplicate-root")
        {
            var fragment = "\"stateRevision\":" + broken["stateRevision"]!.ToJsonString();
            contents = contents.Replace(fragment, fragment + "," + fragment, StringComparison.Ordinal);
        }
        if (corruption == "duplicate-device")
        {
            var fragment = "\"pendingSyncId\":" + record["pendingSyncId"]!.ToJsonString();
            contents = contents.Replace(fragment, fragment + "," + fragment, StringComparison.Ordinal);
        }
        var corruptPath = Path.Combine(root, "devices-v" + source.Version + "-" + corruption + ".json");
        File.WriteAllText(corruptPath, contents, new UTF8Encoding(false));
        var invalid = new CompanionDeviceAuthorityStore(corruptPath, log);
        ExpectAuthorityError(503, () => invalid.Read(primaryId, now));
        checks++;
        Check(File.ReadAllText(corruptPath, Encoding.UTF8) == contents, "迁移覆盖了异常原文件：" + corruption);
    }
    Console.WriteLine($"PASS shared profile v{legacyVersion} to v4 migration: {checks} assertions; explicit scoring mode, exact old hashes, preserved values, pending ACK and idempotent reload.");
}

/// <summary>
/// 在线 schema 只允许固定预设的精确九项，或显式 custom；覆盖写盘拒绝、重启保留与
/// 领域归一化，防止仅界面隐藏编辑器而 HTTP 仍接受名不副实的固定预设。
/// </summary>
static void VerifyScoringModes(string root, ManualLogSource log)
{
    var checks = 0;
    void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    const string id = "55555555-5555-5555-5555-555555555555";
    var now = DateTime.UtcNow;
    foreach (var preset in new[] { "balanced", "resources", "profit", "simple" })
    {
        var profile = JsonNode.Parse(BuildSharedProfile(false, 2).GetRawText())!.AsObject();
        profile["recommendationSortProfile"] = RecommendationEngine.BuildDefaultRecommendationSortProfile(preset);
        var path = Path.Combine(root, "scoring-" + preset + ".json");
        var store = new CompanionDeviceAuthorityStore(path, log);
        var state = store.Register(id, "评分测试", RegisterRequest("windows", JsonSerializer.SerializeToElement(profile)), now);
        Check(state.CurrentDeviceProfile.GetProperty("recommendationSortProfile").GetProperty("preset").GetString() == preset,
            "固定预设未保留显式模式。");
        var saved = File.ReadAllText(path);
        // 同时覆盖权重、方向和启用位，三种自由修改都不能继续自称固定预设。
        foreach (var field in new[] { "weight", "direction", "enabled", "unknown-mode" })
        {
            var edited = JsonNode.Parse(profile.ToJsonString())!.AsObject();
            var sort = edited["recommendationSortProfile"]!.AsObject();
            var first = sort["objectives"]![0]!.AsObject();
            if (field == "weight") first[field] = 1;
            if (field == "direction") first[field] = "asc";
            if (field == "enabled") first[field] = false;
            if (field == "unknown-mode") sort["preset"] = "unknown";
            ExpectAuthorityError(400, () => store.UpdatePrimaryProfile(id, new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                ExpectedAuthorityRevision = state.AuthorityRevision, ExpectedProfileRevision = state.CurrentDeviceProfileRevision,
                Profile = JsonSerializer.SerializeToElement(edited),
            }, now));
            checks++;
            Check(File.ReadAllText(path) == saved, "非法评分修改写入了磁盘。");
        }

        // 自定义数值即使暂时与预设相同，也必须保留用户明确选择的模式。
        profile["recommendationSortProfile"]!["preset"] = "custom";
        state = store.UpdatePrimaryProfile(id, new CompanionDeviceProfileUpdateRequest
        {
            ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
            ExpectedAuthorityRevision = state.AuthorityRevision, ExpectedProfileRevision = state.CurrentDeviceProfileRevision,
            Profile = JsonSerializer.SerializeToElement(profile),
        }, now);
        var reloaded = new CompanionDeviceAuthorityStore(path, log).Read(id, now);
        Check(reloaded.CurrentDeviceProfile.GetProperty("recommendationSortProfile").GetProperty("preset").GetString() == "custom",
            "自定义与预设数值相同不应在重启时被反向改回预设。");
        var originalSort = profile["recommendationSortProfile"]!.AsObject();
        var normalized = RecommendationEngine.NormalizeSortProfile(originalSort);
        Check(normalized["preset"]!.GetValue<string>() == "custom", "领域归一化丢失 custom 模式。");
        Check(normalized["objectives"]!.ToJsonString() == originalSort["objectives"]!.ToJsonString(),
            "领域归一化改变了完整九项目标。");
    }
    Console.WriteLine($"PASS scoring modes: {checks} assertions; exact presets, custom persistence and rejection without write.");
}

/// <summary>夹具按历史哈希规范递归排序对象键，保留数组顺序，不调用被测迁移函数。</summary>
static string CanonicalFixtureHash(JsonElement profile)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream)) Write(writer, profile);
    return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    static void Write(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(field.Name);
                Write(writer, field.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) Write(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }
}

static CompanionDeviceRegisterRequest RegisterRequest(string platform, JsonElement profile)
{
    return new CompanionDeviceRegisterRequest
    {
        ProtocolVersion = 1,
        ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
        Platform = platform,
        AppVersion = "1.2.0",
        Profile = profile,
    };
}

static JsonElement BuildSharedProfile(bool automationEnabled, int rareConcurrency)
{
    var booleanFields = new[]
    {
        "automationEnabled", "gameUiEnabled", "autoRareOrderEnabled", "autoNormalOrderEnabled",
        "autoNormalTakeBeverage", "autoNormalStartCooking", "autoNormalDeliverFood",
        "autoNormalCompleteOrder", "autoNormalStopOnError", "autoPrepCompleteOrder",
        "autoPrepTakeBeverage", "autoPrepStartCooking", "autoPrepCollectCooking",
        "autoPrepRecipeFavoritesOnly", "autoPrepBeverageFavoritesOnly", "autoPrepStopOnError",
        "filterMissingCookers", "missionRecipePriorityEnabled", "pinFavoriteRecipeEnabled",
        "pinFavoriteBeverageEnabled", "rareGameUiPinningEnabled", "normalGameUiPinningEnabled",
        "rareRecipeVariantEnabled", "normalRecipeVariantEnabled", "rareCookerHighlightEnabled",
        "normalCookerHighlightEnabled", "rareSeatHighlightEnabled", "normalSeatHighlightEnabled",
        "rareOrderHighlightEnabled", "normalOrderHighlightEnabled",
    };
    var profile = booleanFields.ToDictionary(field => field, _ => (object)false, StringComparer.Ordinal);
    profile["automationEnabled"] = automationEnabled;
    profile["autoRareOrderEnabled"] = true;
    profile["filterMissingCookers"] = true;
    profile["missionRecipePriorityEnabled"] = true;
    profile["autoRareConcurrency"] = rareConcurrency;
    profile["autoNormalConcurrency"] = 3;
    profile["autoMaxStepRetries"] = 3;
    profile["autoMaxRollbacks"] = 2;
    profile["rareTargetHighlightColor"] = "#FFDB2E";
    profile["normalTargetHighlightColor"] = "#5FACD3";
    profile["recommendationBudgetPolicy"] = "block";
    profile["recipeVariantLimitPerBase"] = 1;
    var objectiveKeys = new[]
    {
        "foodPreference", "beveragePreference", "negativeRisk", "extraCount", "resourcePressure",
        "totalCost", "profit", "beverageStock", "cookerAvailable",
    };
    profile["recommendationSortProfile"] = new Dictionary<string, object?>
    {
        ["preset"] = "custom",
        ["objectives"] = objectiveKeys.Select(key => new Dictionary<string, object?>
        {
            ["key"] = key,
            ["enabled"] = true,
            ["weight"] = 50,
            ["direction"] = key is "negativeRisk" or "extraCount" or "resourcePressure" or "totalCost" ? "asc" : "desc",
        }).ToArray(),
    };
    profile["recommendationExclusions"] = new Dictionary<string, object?>
    {
        ["excludedIngredientIds"] = Array.Empty<int>(),
        ["excludedBeverageIds"] = Array.Empty<int>(),
    };
    return JsonSerializer.SerializeToElement(profile);
}

static void ExpectAuthorityError(int statusCode, Action action)
{
    try
    {
        action();
        throw new InvalidOperationException($"Expected device authority error {statusCode} was not thrown.");
    }
    catch (CompanionDeviceAuthorityException ex) when (ex.StatusCode == statusCode)
    {
    }
}

static void VerifyCorruptFavoriteIsPreserved(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-corrupt.json");
    const string corruptJson = "{ not valid json";
    File.WriteAllText(path, corruptJson, Encoding.UTF8);
    var store = new FavoriteStore(path, log);

    ExpectInvalidData(store.GetJson);
    ExpectInvalidData(() => store.AddRecipe(1, "guest", "tag", 2, Array.Empty<int>()));
    AssertEqual(corruptJson, File.ReadAllText(path, Encoding.UTF8), "A failed favorite read changed the source file.");
}

static void VerifyNullableFavoriteExtras(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-null-extras.json");
    File.WriteAllText(
        path,
        "{\"version\":1,\"recipes\":[{\"id\":\"r\",\"customerId\":1,\"customerName\":\"g\",\"foodTag\":\"t\",\"recipeId\":2,\"extraIngredientIds\":null}],\"beverages\":[]}",
        Encoding.UTF8);

    using var document = JsonDocument.Parse(new FavoriteStore(path, log).GetJson());
    var extras = document.RootElement.GetProperty("recipes")[0].GetProperty("extraIngredientIds");
    AssertEqual(0, extras.GetArrayLength(), "Null favorite extraIngredientIds was not normalized.");
}

static void VerifyMutationJsonEscaping(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-roundtrip.json");
    var response = new FavoriteStore(path, log).AddRecipe(3, "guest \"quoted\"\nline", "tag\\name", 4, new[] { 9, 9, -1 });
    using var document = JsonDocument.Parse(response);
    var rootElement = document.RootElement;
    AssertEqual(true, rootElement.GetProperty("ok").GetBoolean(), "Mutation response did not report success.");
    var recipe = rootElement.GetProperty("favorites").GetProperty("recipes")[0];
    AssertEqual("guest \"quoted\"\nline", recipe.GetProperty("customerName").GetString(), "Customer name did not round-trip through JSON.");
    AssertEqual("tag\\name", recipe.GetProperty("foodTag").GetString(), "Food tag did not round-trip through JSON.");
    AssertEqual(1, recipe.GetProperty("extraIngredientIds").GetArrayLength(), "Extra ingredient IDs were not normalized.");
}

static void VerifyFavoriteManagementMutations(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "favorites-management.json");
    var store = new FavoriteStore(path, log);
    using var firstRecipeResponse = JsonDocument.Parse(store.AddRecipe(3, "guest", "sweet", 4, new[] { 9 }));
    var firstRecipeId = firstRecipeResponse.RootElement
        .GetProperty("favorites")
        .GetProperty("recipes")[0]
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The first recipe favorite has no ID.");
    using var secondRecipeResponse = JsonDocument.Parse(store.AddRecipe(3, "guest", "fresh", 5, Array.Empty<int>()));
    var secondRecipeId = secondRecipeResponse.RootElement
        .GetProperty("favorites")
        .GetProperty("recipes")
        .EnumerateArray()
        .Single(entry => entry.GetProperty("recipeId").GetInt32() == 5)
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The second recipe favorite has no ID.");
    using var beverageResponse = JsonDocument.Parse(store.AddBeverage(3, "guest", "fruit", 6));
    var beverageId = beverageResponse.RootElement
        .GetProperty("favorites")
        .GetProperty("beverages")[0]
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The beverage favorite has no ID.");

    using (var removedRecipe = JsonDocument.Parse(store.RemoveRecipe(firstRecipeId)))
    {
        var favorites = removedRecipe.RootElement.GetProperty("favorites");
        var recipes = favorites.GetProperty("recipes");
        AssertEqual(1, recipes.GetArrayLength(), "Removing one recipe favorite changed the wrong number of recipes.");
        AssertEqual(secondRecipeId, recipes[0].GetProperty("id").GetString(), "Removing one recipe favorite changed an unrelated recipe.");
        AssertEqual(beverageId, favorites.GetProperty("beverages")[0].GetProperty("id").GetString(), "Removing a recipe favorite changed the beverage favorite.");
    }

    using (var removedBeverage = JsonDocument.Parse(store.RemoveBeverage(beverageId)))
    {
        var favorites = removedBeverage.RootElement.GetProperty("favorites");
        AssertEqual(0, favorites.GetProperty("beverages").GetArrayLength(), "Removing the beverage favorite did not remove its exact entry.");
        AssertEqual(secondRecipeId, favorites.GetProperty("recipes")[0].GetProperty("id").GetString(), "Removing a beverage favorite changed the recipe favorite.");
    }
}

static void VerifyCustomRecipeReadDoesNotWrite(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "custom-read.json");
    const string original = "{\n  \"version\": 2,\n  \"enabled\": true,\n  \"activeMode\": \"customer\",\n  \"modeRevision\": 0,\n  \"recipes\": []\n}\n";
    File.WriteAllText(path, original, new UTF8Encoding(false));

    _ = new CustomRecipeStore(path, log).GetJson();
    AssertEqual(original, File.ReadAllText(path, Encoding.UTF8), "Reading custom recipes rewrote the file.");
}

static void VerifyCustomRecipeManagement(string root, ManualLogSource log)
{
    var customPath = Path.Combine(root, "custom-management.json");
    var store = new CustomRecipeStore(customPath, log);

    using (var initial = JsonDocument.Parse(store.GetJson()))
    {
        AssertEqual(true, initial.RootElement.GetProperty("enabled").GetBoolean(), "Custom recipes were not enabled by default.");
    }
    AssertMutationOk(store.SetSettings(false, null, 0), "Disabling all custom recipes failed.");
    using (var disabled = JsonDocument.Parse(store.GetJson()))
    {
        AssertEqual(false, disabled.RootElement.GetProperty("enabled").GetBoolean(), "The global custom recipe setting was not persisted.");
    }

    var firstCustomerFirst = AddCustomRecipe(store, 1, 10, 100);
    var secondCustomer = AddCustomRecipe(store, 2, 10, 200);
    var firstCustomerSecond = AddCustomRecipe(store, 1, 20, 300);

    AssertMutationOk(store.Move("customer", 0, secondCustomer, "down"), "Moving a single-entry customer group failed.");
    AssertEqual(200, ReadCustomRecipe(store, secondCustomer).GetProperty("sortOrder").GetInt32(), "Move crossed a customer boundary.");
    AssertMutationOk(store.Move("customer", 0, firstCustomerFirst, "down"), "Moving within a customer group failed.");
    AssertEqual(300, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("sortOrder").GetInt32(), "The source recipe was not moved within its customer group.");
    AssertEqual(100, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("sortOrder").GetInt32(), "The target recipe was not moved within its customer group.");
    AssertEqual(200, ReadCustomRecipe(store, secondCustomer).GetProperty("sortOrder").GetInt32(), "Moving another customer changed an unrelated sort order.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Mode = "customer", ExpectedModeRevision = 0, Kind = CustomRecipeSelectionKind.Customer, CustomerId = 1 },
        enabled: false,
        pinToTop: null), "Customer bulk disable failed.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("enabled").GetBoolean(), "Customer bulk disable missed the first entry.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("enabled").GetBoolean(), "Customer bulk disable missed the second entry.");
    AssertEqual(true, ReadCustomRecipe(store, secondCustomer).GetProperty("enabled").GetBoolean(), "Customer bulk disable changed another customer.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Mode = "customer", ExpectedModeRevision = 0, Kind = CustomRecipeSelectionKind.Recipe, FoodId = 10 },
        enabled: null,
        pinToTop: false), "Recipe bulk unpin failed.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin missed the first entry.");
    AssertEqual(false, ReadCustomRecipe(store, secondCustomer).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin missed the second entry.");
    AssertEqual(true, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin changed another recipe.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Mode = "customer", ExpectedModeRevision = 0, Kind = CustomRecipeSelectionKind.All },
        enabled: true,
        pinToTop: true), "Updating all custom recipe flags failed.");
    AssertEqual(true, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("enabled").GetBoolean(), "Update-all did not restore enabled state.");
    AssertEqual(true, ReadCustomRecipe(store, secondCustomer).GetProperty("pinToTop").GetBoolean(), "Update-all did not restore pin state.");

    var beforeInvalidMutation = File.ReadAllText(customPath, Encoding.UTF8);
    AssertMutationFailed(store.Move("customer", 0, firstCustomerFirst, "sideways"), "An invalid move direction unexpectedly succeeded.");
    AssertMutationFailed(store.UpdateFlags(
        new CustomRecipeSelection { Mode = "customer", ExpectedModeRevision = 0, Kind = CustomRecipeSelectionKind.Entry, Id = "missing" },
        enabled: false,
        pinToTop: null), "A missing entry update unexpectedly succeeded.");
    AssertMutationFailed(store.UpdateFlags(
        new CustomRecipeSelection { Mode = "customer", ExpectedModeRevision = 0, Kind = CustomRecipeSelectionKind.Entry, Id = firstCustomerFirst },
        enabled: null,
        pinToTop: null), "A flagless update unexpectedly succeeded.");
    AssertEqual(beforeInvalidMutation, File.ReadAllText(customPath, Encoding.UTF8), "A rejected bulk mutation changed the custom recipe file.");

    AssertMutationOk(store.SetSettings(true, null, 0), "Re-enabling all custom recipes failed.");
}

static string AddCustomRecipe(CustomRecipeStore store, int customerId, int foodId, int sortOrder, string mode = "customer", long revision = 0)
{
    using var response = JsonDocument.Parse(store.Upsert(new CustomRecipeMutation
    {
        Mode = mode,
        ExpectedModeRevision = revision,
        CustomerId = customerId,
        CustomerName = $"guest-{customerId}",
        FoodId = foodId,
        RecipeId = foodId + 1000,
        RecipeName = $"recipe-{foodId}",
        Enabled = true,
        PinToTop = true,
        SortOrder = sortOrder,
    }));
    AssertEqual(true, response.RootElement.GetProperty("ok").GetBoolean(), "Adding a custom recipe failed.");
    return response.RootElement
        .GetProperty("customRecipes")
        .GetProperty("recipes")
        .EnumerateArray()
        .Single(recipe => recipe.GetProperty("customerId").GetInt32() == customerId
            && recipe.GetProperty("mode").GetString() == mode
            && recipe.GetProperty("foodId").GetInt32() == foodId)
        .GetProperty("id")
        .GetString() ?? throw new InvalidOperationException("The added custom recipe has no ID.");
}

static JsonElement ReadCustomRecipe(CustomRecipeStore store, string id)
{
    using var document = JsonDocument.Parse(store.GetJson());
    return document.RootElement
        .GetProperty("recipes")
        .EnumerateArray()
        .Single(recipe => string.Equals(recipe.GetProperty("id").GetString(), id, StringComparison.Ordinal))
        .Clone();
}

/// <summary>真实文件存储验证模式隔离、组内排序及切换往返后的旧编辑拒绝。</summary>
static void VerifyCustomRecipeModes(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "custom-modes.json");
    var store = new CustomRecipeStore(path, log);
    var customerEntry = AddCustomRecipe(store, 1, 10, 100);
    var beforeCustomer = ReadCustomRecipe(store, customerEntry).GetRawText();
    AssertMutationOk(store.SetSettings(null, "food", 0), "Switching to food mode failed.");
    var foodFirst = AddCustomRecipe(store, 1, 10, 100, "food", 1);
    var foodSecond = AddCustomRecipe(store, 2, 10, 200, "food", 1);
    var otherFood = AddCustomRecipe(store, 1, 20, 300, "food", 1);
    AssertMutationOk(store.Move("food", 1, foodFirst, "down"), "Food bindings did not move within their food group.");
    AssertEqual(200, ReadCustomRecipe(store, foodFirst).GetProperty("sortOrder").GetInt32(), "Food source order was not changed.");
    AssertEqual(100, ReadCustomRecipe(store, foodSecond).GetProperty("sortOrder").GetInt32(), "Food target order was not changed.");
    AssertEqual(300, ReadCustomRecipe(store, otherFood).GetProperty("sortOrder").GetInt32(), "Move crossed food boundary.");
    AssertEqual(beforeCustomer, ReadCustomRecipe(store, customerEntry).GetRawText(), "Food move changed customer-mode data.");
    AssertMutationOk(store.UpdateFlags(new CustomRecipeSelection
    {
        Mode = "food", ExpectedModeRevision = 1, Kind = CustomRecipeSelectionKind.All,
    }, false, null), "Food-mode bulk update failed.");
    AssertEqual(true, ReadCustomRecipe(store, customerEntry).GetProperty("enabled").GetBoolean(), "Food all-scope changed customer mode.");
    foreach (var id in new[] { foodFirst, foodSecond, otherFood })
        AssertEqual(false, ReadCustomRecipe(store, id).GetProperty("enabled").GetBoolean(), "Food all-scope missed a binding.");

    var beforeRejected = File.ReadAllText(path, Encoding.UTF8);
    AssertMutationFailed(store.Remove("food", 1, customerEntry), "Cross-mode delete succeeded.");
    AssertMutationFailed(store.Move("customer", 0, customerEntry, "down"), "Stale mode move succeeded.");
    AssertMutationFailed(store.UpdateFlags(new CustomRecipeSelection
    {
        Mode = "food", ExpectedModeRevision = 1, Kind = CustomRecipeSelectionKind.Customer, CustomerId = 1,
    }, true, null), "Food mode accepted customer-group mutation.");
    AssertMutationFailed(store.Upsert(new CustomRecipeMutation
    {
        Mode = "food", ExpectedModeRevision = 1, Id = customerEntry, CustomerId = 1,
        CustomerName = "客人", FoodId = 10, RecipeId = 1010, RecipeName = "料理",
    }), "Cross-mode upsert reassigned an existing ID.");
    AssertMutationFailed(store.Upsert(new CustomRecipeMutation
    {
        Mode = "food", ExpectedModeRevision = 1, Id = "missing", CustomerId = 1,
        CustomerName = "客人", FoodId = 10, RecipeId = 1010, RecipeName = "料理",
    }), "Unknown edit ID silently inserted a new binding.");
    AssertEqual(beforeRejected, File.ReadAllText(path, Encoding.UTF8), "Rejected mutations wrote the source file.");
    AssertMutationOk(store.SetSettings(null, "customer", 1), "Returning to customer mode failed.");
    AssertMutationFailed(store.Remove("customer", 0, customerEntry), "An old editor survived customer-food-customer ABA.");
    AssertMutationFailed(store.SetSettings(false, null, 0), "Stale enabled update crossed mode revision.");
    AssertMutationOk(store.SetSettings(false, null, 2), "Current enabled update failed.");
    using (var current = JsonDocument.Parse(store.GetJson()))
    {
        AssertEqual(2L, current.RootElement.GetProperty("modeRevision").GetInt64(), "Enabled-only change incorrectly advanced mode revision.");
        AssertEqual(4, current.RootElement.GetProperty("recipes").GetArrayLength(), "Switching modes lost saved entries.");
    }
    AssertMutationOk(store.Remove("customer", 2, customerEntry), "Current exact-mode delete failed.");
    AssertEqual("food", ReadCustomRecipe(store, foodFirst).GetProperty("mode").GetString(), "Deleting customer binding changed food mode.");
}

/// <summary>旧配置仅添加模式元数据；损坏、未知字段及缺失新边界必须保留原字节。</summary>
static void VerifyCustomRecipeMigration(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "custom-migrate.json");
    var source = new CustomRecipeStore(path, log);
    _ = AddCustomRecipe(source, 3, 7, 270);
    var legacy = JsonNode.Parse(source.GetJson())!.AsObject();
    legacy["version"] = 1;
    legacy.Remove("activeMode");
    legacy.Remove("modeRevision");
    foreach (var entry in legacy["recipes"]!.AsArray().OfType<JsonObject>())
    {
        entry.Remove("mode");
        entry["customerName"] = "  原客人名称  ";
        entry["foodTag"] = "  鲜  ";
        entry["extraIngredientIds"] = new JsonArray(9, 4, 9);
    }
    var previousEntry = legacy["recipes"]![0]!.ToJsonString();
    File.WriteAllText(path, legacy.ToJsonString(), Encoding.UTF8);
    var legacyBytes = File.ReadAllBytes(path);
    var migrated = JsonNode.Parse(new CustomRecipeStore(path, log).GetJson())!.AsObject();
    AssertEqual(true, legacyBytes.SequenceEqual(File.ReadAllBytes(path + ".v1.backup")), "Migration backup did not preserve original bytes including encoding.");
    AssertEqual(2, migrated["version"]!.GetValue<int>(), "Legacy store was not migrated to v2.");
    AssertEqual("customer", migrated["activeMode"]!.GetValue<string>(), "Legacy store did not default to customer mode.");
    AssertEqual(0L, migrated["modeRevision"]!.GetValue<long>(), "Migration did not initialize the mode revision.");
    var migratedEntry = migrated["recipes"]![0]!.AsObject();
    AssertEqual("customer", migratedEntry["mode"]!.GetValue<string>(), "Legacy entry mode was not assigned.");
    migratedEntry.Remove("mode");
    AssertEqual(previousEntry, migratedEntry.ToJsonString(), "Migration rewrote legacy binding values or ordering.");
    var afterMigration = File.ReadAllText(path, Encoding.UTF8);
    _ = new CustomRecipeStore(path, log).GetJson();
    AssertEqual(afterMigration, File.ReadAllText(path, Encoding.UTF8), "Repeated v2 read rewrote the file.");
    AssertEqual(true, legacyBytes.SequenceEqual(File.ReadAllBytes(path + ".v1.backup")), "Repeated read overwrote the migration backup.");
    // 备份发布中断后允许按同一原文恢复；不同备份或不可写目标都必须先停下，源文件不变。
    foreach (var kind in new[] { "same-backup", "different-backup", "blocked-backup" })
    {
        var backupCase = Path.Combine(root, $"custom-migrate-{kind}.json");
        File.WriteAllBytes(backupCase, legacyBytes);
        if (kind == "same-backup") File.WriteAllBytes(backupCase + ".v1.backup", legacyBytes);
        if (kind == "different-backup") File.WriteAllText(backupCase + ".v1.backup", "existing-history", Encoding.UTF8);
        if (kind == "blocked-backup") Directory.CreateDirectory(backupCase + ".v1.backup");
        if (kind == "same-backup") _ = new CustomRecipeStore(backupCase, log).GetJson();
        else
        {
            ExpectInvalidData(new CustomRecipeStore(backupCase, log).GetJson);
            AssertEqual(true, legacyBytes.SequenceEqual(File.ReadAllBytes(backupCase)), "Backup failure changed the legacy source.");
            if (kind == "different-backup") AssertEqual("existing-history", File.ReadAllText(backupCase + ".v1.backup", Encoding.UTF8), "Existing backup was overwritten.");
        }
    }
    foreach (var kind in new[] { "missing-mode", "missing-revision", "invalid-mode", "duplicate-id", "null-name", "unknown-field", "duplicate-property", "string-id", "negative-id", "negative-extra", "fractional-extra", "string-flag", "null-extras" })
    {
        var broken = JsonNode.Parse(afterMigration)!.AsObject();
        if (kind == "missing-mode") broken["recipes"]![0]!.AsObject().Remove("mode");
        if (kind == "missing-revision") broken.Remove("modeRevision");
        if (kind == "invalid-mode") broken["activeMode"] = "all";
        if (kind == "duplicate-id") broken["recipes"]!.AsArray().Add(JsonNode.Parse(broken["recipes"]![0]!.ToJsonString()));
        if (kind == "null-name") broken["recipes"]![0]!["customerName"] = null;
        if (kind == "unknown-field") broken["futureField"] = true;
        if (kind == "string-id") broken["recipes"]![0]!["foodId"] = "7";
        if (kind == "negative-id") broken["recipes"]![0]!["customerId"] = -1;
        if (kind == "negative-extra") broken["recipes"]![0]!["extraIngredientIds"] = new JsonArray(-1);
        if (kind == "fractional-extra") broken["recipes"]![0]!["extraIngredientIds"] = new JsonArray(1.5);
        if (kind == "string-flag") broken["recipes"]![0]!["enabled"] = "true";
        if (kind == "null-extras") broken["recipes"]![0]!["extraIngredientIds"] = null;
        var json = broken.ToJsonString();
        if (kind == "duplicate-property") json = json.Replace("\"version\":2", "\"version\":2,\"version\":2", StringComparison.Ordinal);
        var damagedPath = Path.Combine(root, $"custom-invalid-{kind}.json");
        File.WriteAllText(damagedPath, json, Encoding.UTF8);
        ExpectInvalidData(new CustomRecipeStore(damagedPath, log).GetJson);
        AssertEqual(json, File.ReadAllText(damagedPath, Encoding.UTF8), $"Invalid {kind} file was overwritten.");
    }
}

static void AssertMutationOk(string response, string message)
{
    using var document = JsonDocument.Parse(response);
    AssertEqual(true, document.RootElement.GetProperty("ok").GetBoolean(), message);
}

static void AssertMutationFailed(string response, string message)
{
    using var document = JsonDocument.Parse(response);
    AssertEqual(false, document.RootElement.GetProperty("ok").GetBoolean(), message);
}

static void VerifyCorruptCustomRecipeIsPreserved(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "custom-corrupt.json");
    const string corruptJson = "[ not an object";
    File.WriteAllText(path, corruptJson, Encoding.UTF8);
    var store = new CustomRecipeStore(path, log);

    ExpectInvalidData(store.GetJson);
    AssertEqual(corruptJson, File.ReadAllText(path, Encoding.UTF8), "A failed custom recipe read changed the source file.");
}

static void VerifyFutureSchemasArePreserved(string root, ManualLogSource log)
{
    var favoritePath = Path.Combine(root, "favorites-future.json");
    const string futureFavorites = "{\"version\":2,\"recipes\":[],\"beverages\":[],\"futureField\":true}";
    File.WriteAllText(favoritePath, futureFavorites, Encoding.UTF8);
    ExpectInvalidData(new FavoriteStore(favoritePath, log).GetJson);
    AssertEqual(futureFavorites, File.ReadAllText(favoritePath, Encoding.UTF8), "A future favorites schema was rewritten.");

    var invalidFavoritePath = Path.Combine(root, "favorites-invalid-version.json");
    const string invalidFavorites = "{\"version\":0,\"recipes\":[],\"beverages\":[]}";
    File.WriteAllText(invalidFavoritePath, invalidFavorites, Encoding.UTF8);
    ExpectInvalidData(new FavoriteStore(invalidFavoritePath, log).GetJson);
    AssertEqual(invalidFavorites, File.ReadAllText(invalidFavoritePath, Encoding.UTF8), "An invalid favorites schema was rewritten.");

    var customPath = Path.Combine(root, "custom-future.json");
    const string futureCustom = "{\"version\":3,\"recipes\":[],\"futureField\":true}";
    File.WriteAllText(customPath, futureCustom, Encoding.UTF8);
    var customStore = new CustomRecipeStore(customPath, log);
    ExpectInvalidData(customStore.GetJson);
    AssertEqual(futureCustom, File.ReadAllText(customPath, Encoding.UTF8), "A future custom recipe schema was rewritten.");
}

static void ExpectInvalidData<T>(Func<T> action)
{
    try
    {
        _ = action();
        throw new InvalidOperationException("Expected InvalidDataException was not thrown.");
    }
    catch (InvalidDataException)
    {
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
