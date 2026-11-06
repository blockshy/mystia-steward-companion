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
    VerifyDefaultAndSavedHighlightPalettes(root, log);
    foreach (var version in new[] { 1, 2, 3, 4 }) VerifyDeviceAuthorityLegacyMigration(root, log, version);
    foreach (var version in new[] { 5, 6 }) VerifyIndependentHighlightPalettes(root, log, version);
    VerifyThreeColorPaletteMigration(root, log);
    VerifyKindGameUiSwitchMigration(root, log);
    VerifyPatienceAutoResumeMigration(root, log);
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

/// <summary>
/// 新注册使用两类默认两色；已有 schema 10 文件只读重开必须原样保留自定义两色及单色，
/// 不能因为产品默认更新而补齐、重排、重算修订或要求一次新的同步确认。
/// </summary>
static void VerifyDefaultAndSavedHighlightPalettes(string root, ManualLogSource log)
{
    var checks = 0;
    void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    Check(CompanionDeviceAuthorityStore.ProfileSchemaVersion == 10, "类别辅助开关必须通过 schema 10 显式迁移。");
    Check(CompanionDeviceAuthorityStore.DefaultRareHighlightPalette.SequenceEqual(
        new[] { "#F2CA5C", "#EF856D" }), "稀客默认暖色或顺序错误。");
    Check(CompanionDeviceAuthorityStore.DefaultNormalHighlightPalette.SequenceEqual(
        new[] { "#5AA9E6", "#57C7B6" }), "普客默认冷色或顺序错误。");
    const string id = "77777777-7777-7777-7777-777777777777";
    var path = Path.Combine(root, "saved-current-palettes.json");
    var store = new CompanionDeviceAuthorityStore(path, log);
    var now = DateTime.UtcNow;
    var state = store.Register(id, "保留已有颜色", RegisterRequest("windows", BuildSharedProfile(false, 2)), now);
    foreach (var kind in new[] { "rare", "normal" })
    {
        var expected = kind == "rare" ? CompanionDeviceAuthorityStore.DefaultRareHighlightPalette
            : CompanionDeviceAuthorityStore.DefaultNormalHighlightPalette;
        Check(state.CurrentDeviceProfile.GetProperty(kind + "OrderHighlightPalette").EnumerateArray()
            .Select(color => color.GetString()).SequenceEqual(expected), "新注册没有保留默认两色。");
    }
    var customized = JsonNode.Parse(state.CurrentDeviceProfile.GetRawText())!.AsObject();
    customized["rareOrderHighlightPalette"] = new JsonArray("#FFDB2E", "#F08C78");
    customized["normalOrderHighlightPalette"] = new JsonArray("#5FACD3");
    state = store.UpdatePrimaryProfile(id, new CompanionDeviceProfileUpdateRequest {
        ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, ExpectedAuthorityRevision = state.AuthorityRevision,
        ExpectedProfileRevision = state.CurrentDeviceProfileRevision,
        Profile = JsonSerializer.SerializeToElement(customized),
    }, now);
    var savedFile = File.ReadAllText(path);
    var reopened = new CompanionDeviceAuthorityStore(path, log).Read(id, now);
    Check(File.ReadAllText(path) == savedFile, "读取 schema 10 已有颜色改写了存储文件。");
    // 内存序列化与磁盘缩进可能不同，按夹具独立规范化比较内容，不把空白差异当成值改变。
    Check(CanonicalFixtureHash(reopened.CurrentDeviceProfile) == CanonicalFixtureHash(state.CurrentDeviceProfile),
        "已有自定义颜色被新版默认替换。");
    Check(reopened.CurrentDeviceProfileRevision == state.CurrentDeviceProfileRevision
        && reopened.CurrentDeviceProfileHash == state.CurrentDeviceProfileHash
        && reopened.PendingSyncId == state.PendingSyncId, "读取已有颜色不应推进修订或制造新的同步。");
    Console.WriteLine($"PASS default and saved highlight palettes: {checks} assertions; exact new defaults and unchanged schema 10 reload.");
}

/// <summary>
/// v9旧文件没有低耐心策略，升级必须保持名单/手动暂停含义并显式补为关闭。
/// 真实存储重开、配置哈希、修订、ACK和无效输入均走生产仓库，避免只测字段复制。
/// </summary>
static void VerifyPatienceAutoResumeMigration(string root, ManualLogSource log)
{
    var checks = 0;
    void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    const string id = "88888888-8888-8888-8888-888888888888";
    var now = DateTime.UtcNow;
    var old = JsonNode.Parse(BuildSharedProfile(true, 2).GetRawText())!.AsObject();
    old.Remove("rareGuestParticipationAutoResumeEnabled");
    old.Remove("rareGuestParticipationAutoResumeThresholdPercent");
    old.Remove("rareGuestParticipationAutoResumePosition");
    old["rareGuestParticipationModuleEnabled"] = true;
    old["managedRareGuestIds"] = new JsonArray(30, 36);
    old["rareGameUiEnabled"] = false;
    var oldProfile = JsonSerializer.SerializeToElement(old);
    var data = new DeviceAuthorityData {
        Version = 9, RegistryId = "0123456789abcdef0123456789abcdef", AuthorityRevision = 7,
        StateRevision = 12, PrimaryDeviceId = id, Devices = new() { new() {
            DeviceId = id, Label = "旧调度配置", Platform = "windows", AppVersion = "v9-test",
            Profile = oldProfile, ProfileHash = CanonicalFixtureHash(oldProfile), ProfileRevision = 5,
            AppliedProfileRevision = 5, CreatedAtUtc = now.AddDays(-2), UpdatedAtUtc = now.AddDays(-1),
        } },
    };
    var path = Path.Combine(root, "auto-resume-v9.json");
    File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var store = new CompanionDeviceAuthorityStore(path, log);
    var state = store.Read(id, now);
    Check(state.ProfileSchemaVersion == 10 && state.CurrentDeviceProfileRevision == 6
        && state.AuthorityRevision == 8 && state.StateRevision == 13, "等待条策略迁移必须只推进一次修订。");
    var profile = state.CurrentDeviceProfile;
    Check(!profile.GetProperty("rareGuestParticipationAutoResumeEnabled").GetBoolean()
        && profile.GetProperty("rareGuestParticipationAutoResumeThresholdPercent").GetInt32() == 30
        && profile.GetProperty("rareGuestParticipationAutoResumePosition").GetString() == "tail", "升级不得擅自启用暂停订单。");
    foreach (var field in oldProfile.EnumerateObject())
        Check(CanonicalFixtureHash(field.Value) == CanonicalFixtureHash(profile.GetProperty(field.Name)),
            "迁移改写了已有共享偏好：" + field.Name);
    Check(!string.IsNullOrEmpty(state.PendingSyncId) && state.CurrentDeviceProfileHash != data.Devices[0].ProfileHash,
        "新增策略必须有新摘要及持久化确认。");
    var saved = File.ReadAllText(path);
    var reopened = new CompanionDeviceAuthorityStore(path, log).Read(id, now);
    Check(File.ReadAllText(path) == saved && reopened.PendingSyncId == state.PendingSyncId,
        "同一已迁移配置重开不可再次迁移。");
    Check(!store.IsCurrentPrimaryOnline(state.AuthorityRevision, now), "未确认迁移不能自动启用订单。");
    state = store.AcknowledgeSync(id, new() {
        ProtocolVersion = 1, SyncId = state.PendingSyncId!, ProfileRevision = state.CurrentDeviceProfileRevision,
        ProfileHash = state.CurrentDeviceProfileHash,
    }, now);
    Check(store.IsCurrentPrimaryOnline(state.AuthorityRevision, now), "已确认且在线的主设备应可评估自动启用。");
    Check(!store.IsCurrentPrimaryOnline(0, now) && !store.IsCurrentPrimaryOnline(state.AuthorityRevision - 1, now),
        "无效或过期主配置修订不得自动启用。");
    Check(!store.IsCurrentPrimaryOnline(state.AuthorityRevision, now.AddSeconds(21))
        && !store.IsCurrentPrimaryOnline(state.AuthorityRevision, now.AddSeconds(22)), "只读检查不能延长主设备在线时间。");

    foreach (var position in new[] { "front", "tail" })
    foreach (var threshold in new[] { 1, 30, 100 })
    {
        var updated = JsonNode.Parse(state.CurrentDeviceProfile.GetRawText())!.AsObject();
        updated["rareGuestParticipationAutoResumeEnabled"] = true;
        updated["rareGuestParticipationAutoResumeThresholdPercent"] = threshold;
        updated["rareGuestParticipationAutoResumePosition"] = position;
        state = store.UpdatePrimaryProfile(id, new() {
            ProtocolVersion = 1, ProfileSchemaVersion = 10, ExpectedAuthorityRevision = state.AuthorityRevision,
            ExpectedProfileRevision = state.CurrentDeviceProfileRevision, Profile = JsonSerializer.SerializeToElement(updated),
        }, now);
        var stored = new CompanionDeviceAuthorityStore(path, log).Read(id, now).CurrentDeviceProfile;
        Check(stored.GetProperty("rareGuestParticipationAutoResumeEnabled").GetBoolean()
            && stored.GetProperty("rareGuestParticipationAutoResumeThresholdPercent").GetInt32() == threshold
            && stored.GetProperty("rareGuestParticipationAutoResumePosition").GetString() == position,
            "启用/阈值/位置必须共同耐久保存。");
    }
    foreach (var (field, invalid) in new (string, JsonNode?)[] {
        ("rareGuestParticipationAutoResumeEnabled", null), ("rareGuestParticipationAutoResumeEnabled", JsonValue.Create("true")),
        ("rareGuestParticipationAutoResumeThresholdPercent", JsonValue.Create(0)), ("rareGuestParticipationAutoResumeThresholdPercent", JsonValue.Create(101)),
        ("rareGuestParticipationAutoResumeThresholdPercent", JsonValue.Create(30.5)), ("rareGuestParticipationAutoResumeThresholdPercent", JsonValue.Create("30")),
        ("rareGuestParticipationAutoResumePosition", JsonValue.Create("middle")), ("rareGuestParticipationAutoResumePosition", null),
    })
    {
        var broken = JsonNode.Parse(state.CurrentDeviceProfile.GetRawText())!.AsObject(); broken[field] = invalid;
        var before = File.ReadAllText(path);
        ExpectAuthorityError(400, () => store.UpdatePrimaryProfile(id, new() {
            ProtocolVersion = 1, ProfileSchemaVersion = 10, ExpectedAuthorityRevision = state.AuthorityRevision,
            ExpectedProfileRevision = state.CurrentDeviceProfileRevision, Profile = JsonSerializer.SerializeToElement(broken),
        }, now));
        Check(File.ReadAllText(path) == before, "无效策略写入不得覆盖已有文件。");
    }
    ExpectAuthorityError(409, () => store.Register("77777777-7777-7777-7777-777777777778", "旧客户端", new() {
        ProtocolVersion = 1, ProfileSchemaVersion = 9, Platform = "windows", AppVersion = "old", Profile = oldProfile,
    }, now));
    Console.WriteLine($"PASS patience automatic resume profile migration: {checks} assertions; old settings preserved, default disabled and current writes validated.");
}

/// <summary>
/// 旧版完整四色先验证再截断；只替换已知旧默认前缀，自定义前三色及其他偏好保持。
/// 同时验证迁移修订、ACK屏障、再次读取幂等和非法尾色不能被截断掩盖。
/// </summary>
static void VerifyThreeColorPaletteMigration(string root, ManualLogSource log)
{
    var assertions = 0;
    void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
    const string id = "88888888-8888-8888-8888-888888888888";
    var now = DateTime.UtcNow;
    var oldRare = new[] { "#BA9CE8", "#FFFFFF", "#000000", "#F08C78" };
    var oldNormal = new[] { "#FFDB2E", "#5FACD3", "#F97373", "#7CCB8A" };
    var warm = new[] { "#F2CA5C", "#EF856D", "#E785B4" };
    var cool = new[] { "#5AA9E6", "#57C7B6", "#9A8FE8" };
    var custom = new[] { "#123456", "#ABCDEF", "#654321", "#CDEFAB" };
    foreach (var version in new[] { 6, 7 })
    foreach (var count in new[] { 1, 2, 3, 4 })
    foreach (var isDefault in new[] { false, true })
    {
        var profile = JsonNode.Parse(BuildSharedProfile(true, 3).GetRawText())!.AsObject();
        profile.Remove("rareGameUiEnabled"); profile.Remove("normalGameUiEnabled");
    profile.Remove("rareGuestParticipationAutoResumeEnabled"); profile.Remove("rareGuestParticipationAutoResumeThresholdPercent"); profile.Remove("rareGuestParticipationAutoResumePosition");
        profile["rareOrderHighlightPalette"] = JsonSerializer.SerializeToNode((isDefault ? oldRare : custom).Take(count));
        profile["normalOrderHighlightPalette"] = JsonSerializer.SerializeToNode((isDefault ? oldNormal : custom).Take(count));
        if (version == 6) { profile["rareHighlightOrderCount"] = 8; profile["normalHighlightOrderCount"] = 1; }
        var oldProfile = JsonSerializer.SerializeToElement(profile);
        var data = new DeviceAuthorityData {
            Version = version, RegistryId = "0123456789abcdef0123456789abcdef", AuthorityRevision = 7,
            StateRevision = 12, PrimaryDeviceId = id, Devices = new() { new() {
                DeviceId = id, Label = "旧配置", Platform = "windows", AppVersion = "migration-test",
                Profile = oldProfile, ProfileHash = CanonicalFixtureHash(oldProfile), ProfileRevision = 5,
                AppliedProfileRevision = 5, CreatedAtUtc = now.AddDays(-2), UpdatedAtUtc = now.AddDays(-1),
            } },
        };
        var original = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var path = Path.Combine(root, $"three-colors-v{version}-{count}-{isDefault}.json");
        File.WriteAllText(path, original);
        var store = new CompanionDeviceAuthorityStore(path, log);
        var state = store.Read(id, now);
        Check(state.ProfileSchemaVersion == CompanionDeviceAuthorityStore.ProfileSchemaVersion && state.CurrentDeviceProfileRevision == 6
            && state.AuthorityRevision == 8 && state.StateRevision == 13, "颜色迁移修订错误。");
        foreach (var kind in new[] { "rare", "normal" })
        {
            var source = !isDefault ? custom : version == 7 ? (kind == "rare" ? warm : cool) : (kind == "rare" ? oldRare : oldNormal);
            Check(state.CurrentDeviceProfile.GetProperty(kind + "OrderHighlightPalette").EnumerateArray()
                .Select(item => item.GetString()).SequenceEqual(source.Take(Math.Min(count, 3))), "颜色迁移没有保持同数量/自定义优先级。");
        }
        foreach (var field in oldProfile.EnumerateObject().Where(item => !item.Name.EndsWith("OrderHighlightPalette", StringComparison.Ordinal)
            && item.Name is not "rareHighlightOrderCount" and not "normalHighlightOrderCount"))
            Check(field.Value.GetRawText() == state.CurrentDeviceProfile.GetProperty(field.Name).GetRawText(), "颜色迁移修改了无关偏好。");
        Check(state.PendingSyncId != null && !store.TryAuthorizePrimary(id, state.AuthorityRevision, now, out _), "迁移不能沿用旧ACK授权。");
        var migrated = File.ReadAllText(path);
        var reopened = new CompanionDeviceAuthorityStore(path, log).Read(id, now);
        Check(File.ReadAllText(path) == migrated && reopened.CurrentDeviceProfileRevision == 6, "迁移不应重复执行。");
        if (count == 4)
        {
            var broken = JsonNode.Parse(original)!.AsObject();
            var device = broken["devices"]![0]!.AsObject();
            device["profile"]!["rareOrderHighlightPalette"]![3] = "#abcdef";
            device["profileHash"] = CanonicalFixtureHash(JsonSerializer.SerializeToElement(device["profile"]));
            var badPath = path + ".bad";
            var badText = broken.ToJsonString();
            File.WriteAllText(badPath, badText);
            var badStore = new CompanionDeviceAuthorityStore(badPath, log);
            ExpectAuthorityError(503, () => badStore.Read(id, now));
            Check(File.ReadAllText(badPath) == badText, "非法第四色被截断修复或原文件被覆盖。");
        }
    }
    Console.WriteLine($"PASS three-color palette migration: {assertions} assertions; v6/v7 strict migration and custom preservation.");
}

/// <summary>
/// schema 8 的实际磁盘结构不含类别总开关。迁移只补两个 true，不能重推全局开关、
/// 覆盖分项或重置自定义颜色；新 schema 必须完整提供两个布尔值并可独立持久化关闭。
/// </summary>
static void VerifyKindGameUiSwitchMigration(string root, ManualLogSource log)
{
    var checks = 0;
    void Check(bool value, string message) { checks++; if (!value) throw new InvalidOperationException(message); }
    const string id = "99999999-9999-9999-9999-999999999999";
    var now = DateTime.UtcNow;
    foreach (var master in new[] { false, true })
    {
        var profile = JsonNode.Parse(BuildSharedProfile(false, 2).GetRawText())!.AsObject();
        profile.Remove("rareGameUiEnabled"); profile.Remove("normalGameUiEnabled");
    profile.Remove("rareGuestParticipationAutoResumeEnabled"); profile.Remove("rareGuestParticipationAutoResumeThresholdPercent"); profile.Remove("rareGuestParticipationAutoResumePosition");
        profile["gameUiEnabled"] = master;
        profile["rareGameUiPinningEnabled"] = true;
        profile["rareRecipeVariantEnabled"] = true;
        profile["normalSeatHighlightEnabled"] = true;
        profile["rareOrderHighlightPalette"] = new JsonArray("#A1B2C3", "#D4E5F6", "#102030");
        profile["normalOrderHighlightPalette"] = new JsonArray("#123456");
        var oldProfile = JsonSerializer.SerializeToElement(profile);
        var data = new DeviceAuthorityData {
            Version = 8, RegistryId = "0123456789abcdef0123456789abcdef", AuthorityRevision = 7,
            StateRevision = 12, PrimaryDeviceId = id, Devices = new() { new() {
                DeviceId = id, Label = "旧类别配置", Platform = "windows", AppVersion = "v8-test",
                Profile = oldProfile, ProfileHash = CanonicalFixtureHash(oldProfile), ProfileRevision = 5,
                AppliedProfileRevision = 5, CreatedAtUtc = now.AddDays(-2), UpdatedAtUtc = now.AddDays(-1),
            } },
        };
        var path = Path.Combine(root, $"kind-switch-v8-{master}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var store = new CompanionDeviceAuthorityStore(path, log);
        var state = store.Read(id, now);
        Check(state.ProfileSchemaVersion == 10 && state.CurrentDeviceProfileRevision == 6
            && state.AuthorityRevision == 8 && state.StateRevision == 13, "类别开关迁移没有推进一次完整修订。");
        foreach (var kind in new[] { "rare", "normal" })
            Check(state.CurrentDeviceProfile.GetProperty(kind + "GameUiEnabled").GetBoolean(), "迁移必须默认允许原类别行为。");
        foreach (var field in oldProfile.EnumerateObject())
            Check(CanonicalFixtureHash(field.Value) == CanonicalFixtureHash(state.CurrentDeviceProfile.GetProperty(field.Name)),
                "类别开关迁移改写了原有字段：" + field.Name);
        Check(state.CurrentDeviceProfileHash != data.Devices[0].ProfileHash
            && state.CurrentDeviceProfileHash == CanonicalFixtureHash(state.CurrentDeviceProfile), "新字段必须纳入配置摘要。");
        Check(!store.TryAuthorizePrimary(id, state.AuthorityRevision, now, out _), "旧ACK不得授权新的类别开关配置。");
        var migrated = File.ReadAllText(path);
        var reopened = new CompanionDeviceAuthorityStore(path, log).Read(id, now);
        Check(File.ReadAllText(path) == migrated && reopened.PendingSyncId == state.PendingSyncId,
            "读取schema10不得重复迁移或更新待确认标识。");
        state = store.AcknowledgeSync(id, new CompanionDeviceSyncAckRequest {
            ProtocolVersion = 1, SyncId = state.PendingSyncId!, ProfileRevision = state.CurrentDeviceProfileRevision,
            ProfileHash = state.CurrentDeviceProfileHash,
        }, now);
        foreach (var toggles in new[] { (Rare: false, Normal: true), (Rare: true, Normal: false), (Rare: false, Normal: false), (Rare: true, Normal: true) })
        {
            var updated = JsonNode.Parse(state.CurrentDeviceProfile.GetRawText())!.AsObject();
            updated["rareGameUiEnabled"] = toggles.Rare; updated["normalGameUiEnabled"] = toggles.Normal;
            state = store.UpdatePrimaryProfile(id, new CompanionDeviceProfileUpdateRequest {
                ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, ExpectedAuthorityRevision = state.AuthorityRevision,
                ExpectedProfileRevision = state.CurrentDeviceProfileRevision, Profile = JsonSerializer.SerializeToElement(updated),
            }, now);
            var saved = new CompanionDeviceAuthorityStore(path, log).Read(id, now).CurrentDeviceProfile;
            Check(saved.GetProperty("rareGameUiEnabled").GetBoolean() == toggles.Rare
                && saved.GetProperty("normalGameUiEnabled").GetBoolean() == toggles.Normal, "两类总开关无法独立保存并重载。");
            foreach (var field in oldProfile.EnumerateObject())
                Check(CanonicalFixtureHash(field.Value) == CanonicalFixtureHash(saved.GetProperty(field.Name)),
                    "切换类别总开关破坏原分项或配色：" + field.Name);
        }
        foreach (var field in new[] { "rareGameUiEnabled", "normalGameUiEnabled" })
        foreach (var invalidValue in new[] { "missing", "string", "null" })
        {
            var invalid = JsonNode.Parse(state.CurrentDeviceProfile.GetRawText())!.AsObject();
            if (invalidValue == "missing") invalid.Remove(field);
            else invalid[field] = invalidValue == "null" ? null : JsonValue.Create("false");
            var before = File.ReadAllText(path);
            ExpectAuthorityError(400, () => store.UpdatePrimaryProfile(id, new CompanionDeviceProfileUpdateRequest {
                ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, ExpectedAuthorityRevision = state.AuthorityRevision,
                ExpectedProfileRevision = state.CurrentDeviceProfileRevision, Profile = JsonSerializer.SerializeToElement(invalid),
            }, now));
            Check(File.ReadAllText(path) == before, "非法类别总开关不得被补默认并写盘。");
        }
        ExpectAuthorityError(409, () => store.Register(id, "旧在线协议", new CompanionDeviceRegisterRequest {
            ProtocolVersion = 1, ProfileSchemaVersion = 8, Platform = "windows", AppVersion = "old", Profile = oldProfile,
        }, now));
    }
    Console.WriteLine($"PASS kind game UI switches: {checks} assertions; strict v8-to-current migration, independent persistence and preserved preferences.");
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
    var first = BuildLegacySharedProfile(true, 2, legacyVersion);
    if (legacyVersion == 1) first.Remove("gameUiEnabled");
    if (legacyVersion < 3) first["serviceOrderSortMode"] = "guest";
    // 旧界面允许名称仍是 balanced 时改九项参数；迁移必须保留这些数值并标记 custom。
    first["recommendationSortProfile"]!["preset"] = legacyVersion < 4 ? "balanced" : "custom";
    first["rareSeatHighlightEnabled"] = true;
    first["rareTargetHighlightColor"] = "#12ABEF";
    // 两个旧通道可以同色；分别保留各自原色并补为两色，不能跨通道相互覆盖。
    first["normalTargetHighlightColor"] = "#12ABEF";
    var second = BuildLegacySharedProfile(true, 4, legacyVersion);
    if (legacyVersion == 1) second.Remove("gameUiEnabled");
    if (legacyVersion < 3) second["serviceOrderSortMode"] = "ordered";
    // 另一设备保持真正的固定预设，证明迁移不会一律把所有旧记录改成自定义。
    second["recommendationSortProfile"] = BuildLegacySortPreset("profit");
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
        foreach (var field in before.Profile.EnumerateObject().Where(field => field.Name is not "serviceOrderSortMode" and not "recommendationSortProfile" and not "rareTargetHighlightColor" and not "normalTargetHighlightColor"))
            Check(field.Value.GetRawText() == state.CurrentDeviceProfile.GetProperty(field.Name).GetRawText(), "迁移改写了既有偏好：" + field.Name);
        var oldSort = before.Profile.GetProperty("recommendationSortProfile");
        var newSort = state.CurrentDeviceProfile.GetProperty("recommendationSortProfile");
        Check(oldSort.GetProperty("objectives").EnumerateArray().Where(item => item.GetProperty("key").GetString() != "cookerAvailable")
            .Select(item => item.GetRawText()).SequenceEqual(newSort.GetProperty("objectives").EnumerateArray().Select(item => item.GetRawText())),
            "迁移只能删除厨具评分项，其他目标数值与顺序必须原样保留。");
        Check(!state.CurrentDeviceProfile.TryGetProperty("rareTargetHighlightColor", out _)
            && !state.CurrentDeviceProfile.TryGetProperty("normalTargetHighlightColor", out _), "旧单通道颜色字段未删除。");
        foreach (var kind in new[] { "rare", "normal" })
        {
            var expectedPalette = new[] { before.Profile.GetProperty(kind + "TargetHighlightColor").GetString() }
                .Concat(kind == "rare" ? new[] { "#FFDB2E", "#F08C78" } : new[] { "#5FACD3", "#97C878" }).Distinct().Take(2);
            Check(state.CurrentDeviceProfile.GetProperty(kind + "OrderHighlightPalette").EnumerateArray().Select(item => item.GetString()).SequenceEqual(expectedPalette),
                "历史颜色必须分别保留到自己的通道，再补齐两色。");
        }
        Check(!state.CurrentDeviceProfile.TryGetProperty("rareHighlightOrderCount", out _)
            && !state.CurrentDeviceProfile.TryGetProperty("normalHighlightOrderCount", out _), "迁移后不得保留独立辅助数量。");
        Check(!state.CurrentDeviceProfile.GetProperty("rareGuestParticipationModuleEnabled").GetBoolean()
            && state.CurrentDeviceProfile.GetProperty("managedRareGuestIds").GetArrayLength() == 0, "迁移不得自动暂停既有客人。");
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
        "null-registry", "null-label", "null-pending", "invalid-fixed-preset",
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
        if (corruption == "invalid-fixed-preset")
        {
            // v1-v3 本就允许名称与参数不一致；仅已收紧的 v4/当前 schema 必须拒绝。
            if (source.Version < 4) continue;
            record["profile"]!["recommendationSortProfile"]!["preset"] = "balanced";
            record["profileHash"] = CanonicalFixtureHash(JsonSerializer.SerializeToElement(record["profile"]));
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
    Console.WriteLine($"PASS shared profile v{legacyVersion} to current v10 migration: {checks} assertions; explicit scoring mode, exact old hashes, preserved values, pending ACK and idempotent reload.");
}

/// <summary>
/// 在线 schema 只允许固定预设的精确八项，或显式 custom；覆盖写盘拒绝、重启保留与
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
            "领域归一化改变了完整八项目标。");
    }
    Console.WriteLine($"PASS scoring modes: {checks} assertions; exact presets, custom persistence and rejection without write.");
}

/// <summary>
/// 验证 v5 公共八色拆分、v6 保留独立配色，以及两版本删除独立辅助数量的一次性迁移。
/// 使用真实磁盘存储、修订和 ACK 流程，确保迁移不重置调度名单，也不授权未确认的写入。
/// </summary>
static void VerifyIndependentHighlightPalettes(string root, ManualLogSource log, int legacyVersion)
{
    var checks = 0;
    void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new InvalidOperationException(message);
    }
    const string id = "66666666-6666-6666-6666-666666666666";
    var now = DateTime.UtcNow;
    var colors = new[] { "#112233", "#223344", "#334455", "#445566", "#556677", "#667788", "#778899", "#8899AA" };
    var profile = JsonNode.Parse(BuildSharedProfile(true, 3).GetRawText())!.AsObject();
    profile.Remove("rareGameUiEnabled"); profile.Remove("normalGameUiEnabled");
    profile.Remove("rareGuestParticipationAutoResumeEnabled"); profile.Remove("rareGuestParticipationAutoResumeThresholdPercent"); profile.Remove("rareGuestParticipationAutoResumePosition");
    if (legacyVersion == 5)
    {
        profile.Remove("rareOrderHighlightPalette");
        profile.Remove("normalOrderHighlightPalette");
        profile["orderHighlightPalette"] = JsonSerializer.SerializeToNode(colors);
    }
    else
    {
        // 旧数量故意不同于颜色数，迁移只能删除旧数量，不能反过来截断或补齐原配色。
        profile["rareOrderHighlightPalette"] = new JsonArray(colors[0], colors[1]);
        profile["normalOrderHighlightPalette"] = new JsonArray(colors[2]);
    }
    profile["rareHighlightOrderCount"] = 3;
    profile["normalHighlightOrderCount"] = 6;
    profile["rareGuestParticipationModuleEnabled"] = true;
    profile["managedRareGuestIds"] = new JsonArray(7, 8);
    var oldProfile = JsonSerializer.SerializeToElement(profile);
    var data = new DeviceAuthorityData
    {
        Version = legacyVersion, RegistryId = "0123456789abcdef0123456789abcdef",
        AuthorityRevision = 7, StateRevision = 12, PrimaryDeviceId = id,
        Devices = new() { new() {
            DeviceId = id, Label = "旧主设备", Platform = "windows", AppVersion = $"v{legacyVersion}-test",
            Profile = oldProfile, ProfileHash = CanonicalFixtureHash(oldProfile),
            ProfileRevision = 5, AppliedProfileRevision = 5,
            CreatedAtUtc = now.AddDays(-2), UpdatedAtUtc = now.AddDays(-1),
        } },
    };
    var original = JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    var path = Path.Combine(root, $"palettes-v{legacyVersion}.json");
    File.WriteAllText(path, original, new UTF8Encoding(false));
    var store = new CompanionDeviceAuthorityStore(path, log);
    var state = store.Read(id, now);
    Check(state.ProfileSchemaVersion == CompanionDeviceAuthorityStore.ProfileSchemaVersion && state.AuthorityRevision == 8 && state.StateRevision == 13
        && state.CurrentDeviceProfileRevision == 6, "历史配置必须仅推进一次迁移修订。");
    Check(state.CurrentDeviceProfileHash == CanonicalFixtureHash(state.CurrentDeviceProfile)
        && state.CurrentDeviceProfileHash != data.Devices[0].ProfileHash, "调色板迁移必须重算精确哈希。");
    foreach (var field in oldProfile.EnumerateObject().Where(field => field.Name is not "orderHighlightPalette" and not "rareHighlightOrderCount" and not "normalHighlightOrderCount"))
        Check(field.Value.GetRawText() == state.CurrentDeviceProfile.GetProperty(field.Name).GetRawText(), "调色板迁移改写其他偏好：" + field.Name);
    foreach (var kind in new[] { "rare", "normal" })
    {
        var expected = legacyVersion == 5 ? colors.Take(3) : oldProfile.GetProperty(kind + "OrderHighlightPalette").EnumerateArray().Select(color => color.GetString());
        Check(state.CurrentDeviceProfile.GetProperty(kind + "OrderHighlightPalette").EnumerateArray()
            .Select(color => color.GetString()).SequenceEqual(expected), "旧配色迁移顺序或数量错误。");
        Check(!state.CurrentDeviceProfile.TryGetProperty(kind + "HighlightOrderCount", out _), "历史独立辅助数量必须移除。");
    }
    Check(!state.CurrentDeviceProfile.TryGetProperty("orderHighlightPalette", out _), "公共调色板字段不应保留到新配置。");
    Check(state.PendingSyncId != null && !store.TryAuthorizePrimary(id, state.AuthorityRevision, now, out _), "迁移后必须等待客户端确认新配置。");
    var migrated = File.ReadAllText(path);
    var reopened = new CompanionDeviceAuthorityStore(path, log).Read(id, now);
    Check(migrated == File.ReadAllText(path) && reopened.PendingSyncId == state.PendingSyncId, "已迁移记录不得在重启时重复迁移。");
    state = store.AcknowledgeSync(id, new CompanionDeviceSyncAckRequest {
        ProtocolVersion = 1, SyncId = state.PendingSyncId!, ProfileRevision = state.CurrentDeviceProfileRevision,
        ProfileHash = state.CurrentDeviceProfileHash,
    }, now);
    Check(store.TryAuthorizePrimary(id, state.AuthorityRevision, now, out _), "确认后的主设备应恢复配置权限。");
    var changed = JsonNode.Parse(state.CurrentDeviceProfile.GetRawText())!.AsObject();
    changed["normalOrderHighlightPalette"] = new JsonArray(colors[0]);
    state = store.UpdatePrimaryProfile(id, new CompanionDeviceProfileUpdateRequest {
        ProtocolVersion = 1, ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion, ExpectedAuthorityRevision = state.AuthorityRevision,
        ExpectedProfileRevision = state.CurrentDeviceProfileRevision, Profile = JsonSerializer.SerializeToElement(changed),
    }, now);
    Check(state.CurrentDeviceProfile.GetProperty("normalOrderHighlightPalette").GetArrayLength() == 1
        && state.CurrentDeviceProfile.GetProperty("rareOrderHighlightPalette").GetArrayLength() == (legacyVersion == 5 ? 3 : 2),
        "两类可分别选择一至三色，且跨类同色必须合法。");
    // 注册入口与更新入口共用严格校验，分别验证两类的非法长度、重复色和格式。
    foreach (var kind in new[] { "rare", "normal" })
    foreach (var invalidColors in new[] { Array.Empty<string>(), colors.Take(4).ToArray(), new[] { colors[0], colors[0] }, new[] { "#abcdef" } })
    {
        var invalid = JsonNode.Parse(BuildSharedProfile(false, 2).GetRawText())!.AsObject();
        invalid[kind + "OrderHighlightPalette"] = JsonSerializer.SerializeToNode(invalidColors);
        var rejectPath = Path.Combine(root, $"palette-v{legacyVersion}-invalid.json");
        var rejectStore = new CompanionDeviceAuthorityStore(rejectPath, log);
        ExpectAuthorityError(400, () => rejectStore.Register(id, "无效颜色", RegisterRequest("windows", JsonSerializer.SerializeToElement(invalid)), now));
        Check(!File.Exists(rejectPath), "非法颜色不得生成设备配置文件。");
    }
    // 被删除的旧数量仍先严格校验；非法历史结构不能因移除字段而被静默修复。
    // 当前 HTTP 即使收到合法旧字段也必须拒绝，防止仅界面隐藏设置却继续保存独立数量。
    foreach (var kind in new[] { "rare", "normal" })
    {
        var invalid = JsonNode.Parse(BuildSharedProfile(false, 2).GetRawText())!.AsObject();
        invalid[kind + "HighlightOrderCount"] = 2;
        var rejectPath = Path.Combine(root, $"palette-v{legacyVersion}-{kind}-obsolete.json");
        var rejectStore = new CompanionDeviceAuthorityStore(rejectPath, log);
        ExpectAuthorityError(400, () => rejectStore.Register(id, "旧数量", RegisterRequest("windows", JsonSerializer.SerializeToElement(invalid)), now));
        Check(!File.Exists(rejectPath), "当前 HTTP 的旧数量字段不得写盘。");
        var oldRequest = new CompanionDeviceRegisterRequest {
            ProtocolVersion = 1, ProfileSchemaVersion = legacyVersion, Platform = "windows", AppVersion = "legacy-test", Profile = oldProfile,
        };
        ExpectAuthorityError(409, () => rejectStore.Register(id, "旧协议", oldRequest, now));
        Check(!File.Exists(rejectPath), "旧 HTTP 协议不得写盘。");
    }
    foreach (var corrupt in new[] { "hash", "count", "duplicate", "old-count", "missing-old-count" })
    {
        var broken = JsonNode.Parse(original)!.AsObject();
        var device = broken["devices"]![0]!.AsObject();
        if (corrupt == "hash") device["profileHash"] = new string('0', 64);
        else
        {
            if (corrupt == "old-count") device["profile"]!["rareHighlightOrderCount"] = 9;
            else if (corrupt == "missing-old-count") device["profile"]!.AsObject().Remove("normalHighlightOrderCount");
            else device["profile"]![legacyVersion == 5 ? "orderHighlightPalette" : "rareOrderHighlightPalette"] = JsonSerializer.SerializeToNode(
                corrupt == "count" ? colors.Take(legacyVersion == 5 ? 4 : 5).ToArray() : Enumerable.Repeat(colors[0], legacyVersion == 5 ? 8 : 2).ToArray());
            device["profileHash"] = CanonicalFixtureHash(JsonSerializer.SerializeToElement(device["profile"]));
        }
        var corruptPath = Path.Combine(root, $"palettes-v{legacyVersion}-" + corrupt + ".json");
        var contents = broken.ToJsonString();
        File.WriteAllText(corruptPath, contents, new UTF8Encoding(false));
        var corruptStore = new CompanionDeviceAuthorityStore(corruptPath, log);
        ExpectAuthorityError(503, () => corruptStore.Read(id, now));
        Check(File.ReadAllText(corruptPath) == contents, "异常历史配置不得被迁移覆盖。");
    }
    Console.WriteLine($"PASS independent highlight palettes v{legacyVersion}: {checks} assertions; removed counts, per-kind bounds, preserved preferences and restart.");
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
        "automationEnabled", "gameUiEnabled", "rareGuestParticipationModuleEnabled", "autoRareOrderEnabled", "autoNormalOrderEnabled",
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
    profile["rareGameUiEnabled"] = true;
    profile["normalGameUiEnabled"] = true;
    profile["automationEnabled"] = automationEnabled;
    profile["autoRareOrderEnabled"] = true;
    profile["filterMissingCookers"] = true;
    profile["missionRecipePriorityEnabled"] = true;
    profile["autoRareConcurrency"] = rareConcurrency;
    profile["autoNormalConcurrency"] = 3;
    profile["autoMaxStepRetries"] = 3;
    profile["autoMaxRollbacks"] = 2;
    profile["rareOrderHighlightPalette"] = CompanionDeviceAuthorityStore.DefaultRareHighlightPalette;
    profile["normalOrderHighlightPalette"] = CompanionDeviceAuthorityStore.DefaultNormalHighlightPalette;
    profile["managedRareGuestIds"] = Array.Empty<int>();
    profile["rareGuestParticipationAutoResumeEnabled"] = false;
    profile["rareGuestParticipationAutoResumeThresholdPercent"] = 30;
    profile["rareGuestParticipationAutoResumePosition"] = "tail";
    profile["recommendationBudgetPolicy"] = "block";
    profile["recipeVariantLimitPerBase"] = 1;
    var objectiveKeys = new[]
    {
        "foodPreference", "beveragePreference", "negativeRisk", "extraCount", "resourcePressure",
        "totalCost", "profit", "beverageStock",
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

/// <summary>明确构造历史九项/双颜色结构，不能仅把当前 schema 的版本号改旧。</summary>
static JsonObject BuildLegacySharedProfile(bool automationEnabled, int rareConcurrency, int version)
{
    var value = JsonNode.Parse(BuildSharedProfile(automationEnabled, rareConcurrency).GetRawText())!.AsObject();
    value.Remove("rareGameUiEnabled"); value.Remove("normalGameUiEnabled");
    value.Remove("rareGuestParticipationAutoResumeEnabled"); value.Remove("rareGuestParticipationAutoResumeThresholdPercent"); value.Remove("rareGuestParticipationAutoResumePosition");
    foreach (var key in new[] { "rareOrderHighlightPalette", "normalOrderHighlightPalette", "rareHighlightOrderCount", "normalHighlightOrderCount", "rareGuestParticipationModuleEnabled", "managedRareGuestIds" }) value.Remove(key);
    value["rareTargetHighlightColor"] = "#FFDB2E";
    value["normalTargetHighlightColor"] = "#5FACD3";
    value["recommendationSortProfile"]!["objectives"]!.AsArray().Add(new JsonObject
    { ["key"] = "cookerAvailable", ["enabled"] = true, ["weight"] = 50, ["direction"] = "desc" });
    if (version == 1) value.Remove("gameUiEnabled");
    if (version < 3) value["serviceOrderSortMode"] = "ordered";
    return value;
}

/// <summary>历史权重来自迁移前 v4 定义，保留已移除厨具项用于严格迁移验证。</summary>
static JsonObject BuildLegacySortPreset(string preset)
{
    var value = RecommendationEngine.BuildDefaultRecommendationSortProfile(preset);
    var weight = preset switch { "profit" => 50, "simple" => 70, _ => 60 };
    value["objectives"]!.AsArray().Add(new JsonObject
    { ["key"] = "cookerAvailable", ["enabled"] = true, ["weight"] = weight, ["direction"] = "desc" });
    return value;
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
