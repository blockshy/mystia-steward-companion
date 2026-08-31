using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;

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
    VerifyCorruptCustomRecipeIsPreserved(root, log);
    VerifyFutureSchemasArePreserved(root, log);
    VerifyCompanionDeviceAuthorityV1Migration(root, log);
    VerifyCompanionDeviceAuthorityV2Migration(root, log);
    VerifyCompanionDeviceAuthorityExactStoredShape(root, log);
    VerifyCompanionDeviceAuthority(root, log);
    VerifyManagedRareGuestProfileValidation(root, log);
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
    var primaryProfile = BuildSharedProfile(
        automationEnabled: true,
        rareConcurrency: 2,
        managedRareGuestIds: new[] { 4, 9 },
        rareGuestParticipationModuleEnabled: true);
    var secondaryProfile = BuildSharedProfile(automationEnabled: false, rareConcurrency: 3, managedRareGuestIds: new[] { 2 });

    var primary = store.Register(
        "11111111-1111-1111-1111-111111111111",
        "Windows 主设备",
        RegisterRequest("windows", primaryProfile),
        now);
    AssertEqual(true, primary.CurrentDeviceIsPrimary, "The first registered device did not become primary.");
    AssertEqual(1L, primary.AuthorityRevision, "Initial authority revision is invalid.");
    AssertEqual(3, primary.ProfileSchemaVersion, "The device authority did not expose shared profile schema v3.");
    AssertEqual(true, primary.ActiveProfile.GetProperty("rareGuestParticipationModuleEnabled").GetBoolean(), "The enabled rare-guest participation module did not round-trip.");
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
                ProfileSchemaVersion = 3,
                ExpectedAuthorityRevision = secondary.AuthorityRevision,
                ExpectedProfileRevision = secondary.CurrentDeviceProfileRevision,
                Profile = secondaryProfile,
            },
            now.AddSeconds(2)));

    var unchanged = store.UpdatePrimaryProfile(
        primary.CurrentDeviceId,
        new CompanionDeviceProfileUpdateRequest
        {
            ProtocolVersion = 1,
            ProfileSchemaVersion = 3,
            ExpectedAuthorityRevision = secondary.AuthorityRevision,
            ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
            Profile = primaryProfile,
        },
        now.AddSeconds(3));
    AssertEqual(secondary.AuthorityRevision, unchanged.AuthorityRevision, "An identical canonical profile advanced authority.");
    AssertEqual(secondary.StateRevision, unchanged.StateRevision, "An identical canonical profile advanced state revision.");
    AssertEqual(primary.CurrentDeviceProfileRevision, unchanged.CurrentDeviceProfileRevision, "An identical canonical profile advanced profile revision.");

    var updatedProfile = BuildSharedProfile(
        automationEnabled: true,
        rareConcurrency: 2,
        managedRareGuestIds: new[] { 4, 9, 12 },
        rareGuestParticipationModuleEnabled: true);
    var updated = store.UpdatePrimaryProfile(
        primary.CurrentDeviceId,
        new CompanionDeviceProfileUpdateRequest
        {
            ProtocolVersion = 1,
            ProfileSchemaVersion = 3,
            ExpectedAuthorityRevision = secondary.AuthorityRevision,
            ExpectedProfileRevision = primary.CurrentDeviceProfileRevision,
            Profile = updatedProfile,
        },
        now.AddSeconds(3));
    AssertEqual(2L, updated.AuthorityRevision, "Changing the active profile did not advance authority.");
    AssertEqual(unchanged.StateRevision + 1, updated.StateRevision, "Changing the managed list did not advance state revision exactly once.");
    AssertEqual(2L, updated.CurrentDeviceProfileRevision, "Changing the managed list did not advance profile revision exactly once.");
    AssertEqual(
        3,
        updated.ActiveProfile.GetProperty("managedRareGuestIds").GetArrayLength(),
        "The active managed rare-guest list was not persisted.");
    ExpectAuthorityError(
        409,
        () => store.UpdatePrimaryProfile(
            primary.CurrentDeviceId,
            new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1,
                ProfileSchemaVersion = 3,
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
        store.TryAuthorizePrimary(primary.CurrentDeviceId, unchanged.AuthorityRevision, now.AddSeconds(4), out _),
        "The managed-list change left the previous runtime-writer authority valid.");
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
        3,
        switched.State.ActiveProfile.GetProperty("managedRareGuestIds").GetArrayLength(),
        "Primary transfer did not retain the synchronized managed rare-guest list.");
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

static void VerifyCompanionDeviceAuthorityV1Migration(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "companion-devices-v1.json");
    var now = DateTime.UtcNow;
    const string primaryId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    const string secondaryId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    const string pendingSyncId = "cccccccccccccccccccccccccccccccc";
    var primaryProfile = BuildSharedProfile(
        true,
        2,
        includeManagedRareGuestIds: false,
        includeRareGuestParticipationModuleEnabled: false);
    var secondaryProfile = BuildSharedProfile(
        false,
        3,
        includeManagedRareGuestIds: false,
        includeRareGuestParticipationModuleEnabled: false);
    var primaryLegacyHash = ComputeCanonicalProfileHash(primaryProfile);
    var secondaryLegacyHash = ComputeCanonicalProfileHash(secondaryProfile);
    var legacy = new DeviceAuthorityData
    {
        Version = 1,
        RegistryId = "0123456789abcdef0123456789abcdef",
        AuthorityRevision = 7,
        StateRevision = 11,
        PrimaryDeviceId = primaryId,
        Devices = new List<CompanionDeviceRecord>
        {
            new()
            {
                DeviceId = primaryId,
                Label = "Legacy primary",
                Platform = "windows",
                AppVersion = "1.1.0",
                ProfileRevision = 4,
                AppliedProfileRevision = 4,
                ProfileHash = primaryLegacyHash,
                Profile = primaryProfile,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1),
            },
            new()
            {
                DeviceId = secondaryId,
                Label = "Legacy secondary",
                Platform = "android",
                AppVersion = "1.1.0",
                ProfileRevision = 3,
                AppliedProfileRevision = 2,
                ProfileHash = secondaryLegacyHash,
                Profile = secondaryProfile,
                PendingSyncId = pendingSyncId,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1),
            },
        },
    };
    File.WriteAllText(path, JsonSerializer.Serialize(legacy, DeviceAuthorityJsonOptions()), new UTF8Encoding(false));

    var store = new CompanionDeviceAuthorityStore(path, log);
    var primary = store.Read(primaryId, now);
    var secondary = store.Read(secondaryId, now);
    AssertEqual(3, primary.ProfileSchemaVersion, "A legacy device store did not migrate to profile schema v3.");
    AssertEqual(7L, primary.AuthorityRevision, "Migration changed authority revision.");
    AssertEqual(11L, primary.StateRevision, "Migration changed state revision.");
    AssertEqual(4L, primary.CurrentDeviceProfileRevision, "Migration changed the primary profile revision.");
    AssertEqual(pendingSyncId, secondary.PendingSyncId, "Migration discarded a pending sync identity.");
    AssertEqual(3L, secondary.CurrentDeviceProfileRevision, "Migration changed the pending profile revision.");
    AssertEqual(
        2L,
        secondary.Devices.Single(device => device.IsCurrent).AppliedProfileRevision,
        "Migration changed the applied pending profile revision.");
    AssertEqual(0, primary.ActiveProfile.GetProperty("managedRareGuestIds").GetArrayLength(), "Migration did not add the empty v3 managed list.");
    AssertEqual(false, primary.ActiveProfile.GetProperty("rareGuestParticipationModuleEnabled").GetBoolean(), "A v1 profile did not migrate with rare-guest participation disabled.");
    AssertTrue(primary.ActiveProfileHash != primaryLegacyHash, "Migration did not recompute the primary canonical profile hash.");
    AssertTrue(secondary.CurrentDeviceProfileHash != secondaryLegacyHash, "Migration did not recompute the pending canonical profile hash.");

    using (var migratedDocument = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
    {
        AssertEqual(3, migratedDocument.RootElement.GetProperty("version").GetInt32(), "Migrated store schema was not persisted as v3.");
        foreach (var device in migratedDocument.RootElement.GetProperty("devices").EnumerateArray())
        {
            AssertEqual(0, device.GetProperty("profile").GetProperty("managedRareGuestIds").GetArrayLength(), "A migrated profile omitted the managed list.");
            AssertEqual(false, device.GetProperty("profile").GetProperty("rareGuestParticipationModuleEnabled").GetBoolean(), "A migrated profile enabled rare-guest participation.");
        }
    }

    var migratedBytes = File.ReadAllBytes(path);
    var reloaded = new CompanionDeviceAuthorityStore(path, log);
    _ = reloaded.Read(primaryId, now.AddSeconds(1));
    AssertTrue(migratedBytes.SequenceEqual(File.ReadAllBytes(path)), "A second v3 load rewrote the migrated store.");
    ExpectAuthorityError(
        409,
        () => reloaded.AcknowledgeSync(
            secondaryId,
            new CompanionDeviceSyncAckRequest
            {
                ProtocolVersion = 1,
                SyncId = pendingSyncId,
                ProfileRevision = 3,
                ProfileHash = secondaryLegacyHash,
            },
            now.AddSeconds(2)));
    var refreshed = reloaded.Read(secondaryId, now.AddSeconds(3));
    var acknowledged = reloaded.AcknowledgeSync(
        secondaryId,
        new CompanionDeviceSyncAckRequest
        {
            ProtocolVersion = 1,
            SyncId = pendingSyncId,
            ProfileRevision = refreshed.CurrentDeviceProfileRevision,
            ProfileHash = refreshed.CurrentDeviceProfileHash,
        },
        now.AddSeconds(4));
    AssertEqual(null, acknowledged.PendingSyncId, "A migrated pending sync could not be acknowledged with its v3 hash.");
}

static void VerifyCompanionDeviceAuthorityV2Migration(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "companion-devices-v2.json");
    var now = DateTime.UtcNow;
    const string deviceId = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    var previousProfile = BuildSharedProfile(
        automationEnabled: true,
        rareConcurrency: 2,
        managedRareGuestIds: new[] { 4, 9 },
        includeRareGuestParticipationModuleEnabled: false);
    var previousHash = ComputeCanonicalProfileHash(previousProfile);
    var previous = new DeviceAuthorityData
    {
        Version = 2,
        RegistryId = "fedcba9876543210fedcba9876543210",
        AuthorityRevision = 5,
        StateRevision = 8,
        PrimaryDeviceId = deviceId,
        Devices = new List<CompanionDeviceRecord>
        {
            new()
            {
                DeviceId = deviceId,
                Label = "Previous dev profile",
                Platform = "windows",
                AppVersion = "1.3.1",
                ProfileRevision = 3,
                AppliedProfileRevision = 3,
                ProfileHash = previousHash,
                Profile = previousProfile,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now.AddHours(-1),
            },
        },
    };
    File.WriteAllText(path, JsonSerializer.Serialize(previous, DeviceAuthorityJsonOptions()), new UTF8Encoding(false));

    var store = new CompanionDeviceAuthorityStore(path, log);
    var state = store.Read(deviceId, now);
    AssertEqual(3, state.ProfileSchemaVersion, "A v2 device store did not migrate to profile schema v3.");
    AssertEqual(5L, state.AuthorityRevision, "The v2 migration changed authority revision.");
    AssertEqual(8L, state.StateRevision, "The v2 migration changed state revision.");
    AssertEqual(3L, state.CurrentDeviceProfileRevision, "The v2 migration changed profile revision.");
    AssertEqual(
        "4,9",
        string.Join(",", state.ActiveProfile.GetProperty("managedRareGuestIds").EnumerateArray().Select(item => item.GetInt32())),
        "The v2 migration did not preserve the configured rare-guest roster.");
    AssertEqual(
        false,
        state.ActiveProfile.GetProperty("rareGuestParticipationModuleEnabled").GetBoolean(),
        "The v2 migration activated rare-guest participation instead of defaulting it off.");
    AssertTrue(state.ActiveProfileHash != previousHash, "The v2 migration did not recompute the canonical profile hash.");

    using (var migratedDocument = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
    {
        AssertEqual(3, migratedDocument.RootElement.GetProperty("version").GetInt32(), "The v2 store was not persisted as v3.");
    }
    var migratedBytes = File.ReadAllBytes(path);
    _ = new CompanionDeviceAuthorityStore(path, log).Read(deviceId, now.AddSeconds(1));
    AssertTrue(migratedBytes.SequenceEqual(File.ReadAllBytes(path)), "A second v3 load rewrote the migrated v2 store.");
}

static void VerifyCompanionDeviceAuthorityExactStoredShape(string root, ManualLogSource log)
{
    var corruptions = new (string Name, Action<JsonObject> Apply)[]
    {
        ("root-unknown", document => document["unexpected"] = true),
        ("root-missing", document => document.Remove("registryId")),
        ("root-null", document => document["devices"] = null),
        ("device-unknown", document => FirstStoredDevice(document)["unexpected"] = true),
        ("device-missing", document => FirstStoredDevice(document).Remove("label")),
        ("device-null", document => FirstStoredDevice(document)["profile"] = null),
    };

    foreach (var version in new[] { 1, 2, 3 })
    {
        foreach (var (name, apply) in corruptions)
        {
            var path = Path.Combine(root, $"companion-devices-v{version}-{name}.json");
            var document = JsonNode.Parse(BuildStoredAuthorityFixture(version))?.AsObject()
                ?? throw new InvalidOperationException("The stored authority fixture is not an object.");
            apply(document);
            var content = document.ToJsonString(DeviceAuthorityJsonOptions());
            File.WriteAllText(path, content, new UTF8Encoding(false));

            var store = new CompanionDeviceAuthorityStore(path, log);
            ExpectAuthorityError(
                503,
                () => store.Register(
                    "99999999-9999-9999-9999-999999999999",
                    "Rejected shape",
                    RegisterRequest("windows", BuildSharedProfile(false, 2)),
                    DateTime.UtcNow));
            AssertEqual(
                content,
                File.ReadAllText(path, Encoding.UTF8),
                $"Invalid v{version} stored shape '{name}' was rewritten.");
        }
    }
}

static JsonObject FirstStoredDevice(JsonObject document)
{
    return document["devices"]?.AsArray()[0]?.AsObject()
        ?? throw new InvalidOperationException("The stored authority fixture has no device.");
}

static string BuildStoredAuthorityFixture(int version)
{
    const string deviceId = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
    var profile = version switch
    {
        1 => BuildSharedProfile(
            true,
            2,
            includeManagedRareGuestIds: false,
            includeRareGuestParticipationModuleEnabled: false),
        2 => BuildSharedProfile(
            true,
            2,
            managedRareGuestIds: new[] { 4, 9 },
            includeRareGuestParticipationModuleEnabled: false),
        3 => BuildSharedProfile(
            true,
            2,
            managedRareGuestIds: new[] { 4, 9 },
            rareGuestParticipationModuleEnabled: false),
        _ => throw new ArgumentOutOfRangeException(nameof(version)),
    };
    var capturedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    var data = new DeviceAuthorityData
    {
        Version = version,
        RegistryId = "0123456789abcdef0123456789abcdef",
        AuthorityRevision = 1,
        StateRevision = 1,
        PrimaryDeviceId = deviceId,
        Devices = new List<CompanionDeviceRecord>
        {
            new()
            {
                DeviceId = deviceId,
                Label = "Exact stored device",
                Platform = "windows",
                AppVersion = "1.3.1",
                ProfileRevision = 1,
                AppliedProfileRevision = 1,
                ProfileHash = ComputeCanonicalProfileHash(profile),
                Profile = profile,
                PendingSyncId = "",
                CreatedAtUtc = capturedAtUtc,
                UpdatedAtUtc = capturedAtUtc,
            },
        },
    };
    return JsonSerializer.Serialize(data, DeviceAuthorityJsonOptions());
}

static void VerifyManagedRareGuestProfileValidation(string root, ManualLogSource log)
{
    var path = Path.Combine(root, "companion-devices-managed-validation.json");
    var store = new CompanionDeviceAuthorityStore(path, log);
    var now = DateTime.UtcNow;
    const string deviceId = "33333333-3333-3333-3333-333333333333";
    var valid = BuildSharedProfile(true, 2, Enumerable.Range(0, 511).Append(int.MaxValue).ToArray());
    var registered = store.Register(deviceId, "Valid", RegisterRequest("browser", valid), now);
    AssertEqual(512, registered.ActiveProfile.GetProperty("managedRareGuestIds").GetArrayLength(), "The exact 512-entry managed-list boundary did not round-trip.");

    foreach (var staleSchemaVersion in new[] { 1, 2 })
    {
        ExpectAuthorityError(
            409,
            () => new CompanionDeviceAuthorityStore(
                    Path.Combine(root, $"profile-v{staleSchemaVersion}-rejected.json"),
                    log)
                .Register(
                    "44444444-4444-4444-4444-444444444444",
                    $"Profile v{staleSchemaVersion}",
                    new CompanionDeviceRegisterRequest
                    {
                        ProtocolVersion = 1,
                        ProfileSchemaVersion = staleSchemaVersion,
                        Platform = "browser",
                        AppVersion = "1.3.1",
                        Profile = valid,
                    },
                    now));
    }

    var invalidProfiles = new[]
    {
        RewriteRareGuestParticipationModuleEnabled(valid, replacement: null),
        RewriteRareGuestParticipationModuleEnabled(valid, JsonSerializer.SerializeToElement("true")),
        RewriteManagedRareGuestIds(valid, replacement: null),
        RewriteManagedRareGuestIds(valid, JsonSerializer.SerializeToElement("3")),
        RewriteManagedRareGuestIds(valid, JsonSerializer.SerializeToElement(new[] { -1 })),
        RewriteManagedRareGuestIds(valid, JsonSerializer.SerializeToElement(new[] { 1.5 })),
        RewriteManagedRareGuestIds(valid, JsonSerializer.SerializeToElement(new long[] { (long)int.MaxValue + 1 })),
        RewriteManagedRareGuestIds(valid, JsonSerializer.SerializeToElement(new[] { 1, 1 })),
        RewriteManagedRareGuestIds(valid, JsonSerializer.SerializeToElement(new[] { 2, 1 })),
        RewriteManagedRareGuestIds(valid, JsonSerializer.SerializeToElement(Enumerable.Range(0, 513).ToArray())),
        RewriteManagedRareGuestIds(valid, valid.GetProperty("managedRareGuestIds"), addUnexpectedField: true),
    };
    foreach (var invalidProfile in invalidProfiles)
    {
        ExpectAuthorityError(
            400,
            () => store.UpdatePrimaryProfile(
                deviceId,
                new CompanionDeviceProfileUpdateRequest
                {
                    ProtocolVersion = 1,
                    ProfileSchemaVersion = 3,
                    ExpectedAuthorityRevision = registered.AuthorityRevision,
                    ExpectedProfileRevision = registered.CurrentDeviceProfileRevision,
                    Profile = invalidProfile,
                },
                now.AddSeconds(1)));
    }
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

    var legacyPath = Path.Combine(root, "companion-devices-v1-invalid-hash.json");
    var legacyProfile = BuildSharedProfile(
        true,
        2,
        includeManagedRareGuestIds: false,
        includeRareGuestParticipationModuleEnabled: false);
    var legacy = new DeviceAuthorityData
    {
        Version = 1,
        RegistryId = "1234567890abcdef1234567890abcdef",
        AuthorityRevision = 1,
        StateRevision = 1,
        PrimaryDeviceId = "55555555-5555-5555-5555-555555555555",
        Devices = new List<CompanionDeviceRecord>
        {
            new()
            {
                DeviceId = "55555555-5555-5555-5555-555555555555",
                Label = "Legacy invalid",
                Platform = "windows",
                AppVersion = "1.1.0",
                ProfileRevision = 1,
                AppliedProfileRevision = 1,
                ProfileHash = new string('0', 64),
                Profile = legacyProfile,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
            },
        },
    };
    var invalidLegacy = JsonSerializer.Serialize(legacy, DeviceAuthorityJsonOptions());
    File.WriteAllText(legacyPath, invalidLegacy, new UTF8Encoding(false));
    var invalidLegacyStore = new CompanionDeviceAuthorityStore(legacyPath, log);
    ExpectAuthorityError(
        503,
        () => invalidLegacyStore.Register(
            "66666666-6666-6666-6666-666666666666",
            "Device",
            RegisterRequest("windows", BuildSharedProfile(false, 2)),
            DateTime.UtcNow));
    AssertEqual(invalidLegacy, File.ReadAllText(legacyPath, Encoding.UTF8), "A v1 registry with an invalid legacy hash was migrated or overwritten.");
}

static CompanionDeviceRegisterRequest RegisterRequest(string platform, JsonElement profile)
{
    return new CompanionDeviceRegisterRequest
    {
        ProtocolVersion = 1,
        ProfileSchemaVersion = 3,
        Platform = platform,
        AppVersion = "1.2.0",
        Profile = profile,
    };
}

static JsonElement BuildSharedProfile(
    bool automationEnabled,
    int rareConcurrency,
    IReadOnlyList<int>? managedRareGuestIds = null,
    bool includeManagedRareGuestIds = true,
    bool includeRareGuestParticipationModuleEnabled = true,
    bool rareGuestParticipationModuleEnabled = false)
{
    var booleanFields = new[]
    {
        "automationEnabled", "autoRareOrderEnabled", "autoNormalOrderEnabled",
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
    if (includeRareGuestParticipationModuleEnabled)
    {
        profile["rareGuestParticipationModuleEnabled"] = rareGuestParticipationModuleEnabled;
    }
    profile["filterMissingCookers"] = true;
    profile["missionRecipePriorityEnabled"] = true;
    profile["autoRareConcurrency"] = rareConcurrency;
    profile["autoNormalConcurrency"] = 3;
    profile["autoMaxStepRetries"] = 3;
    profile["autoMaxRollbacks"] = 2;
    profile["rareTargetHighlightColor"] = "#FFDB2E";
    profile["normalTargetHighlightColor"] = "#5FACD3";
    profile["serviceOrderSortMode"] = "ordered";
    profile["recommendationBudgetPolicy"] = "block";
    profile["recipeVariantLimitPerBase"] = 1;
    var objectiveKeys = new[]
    {
        "foodPreference", "beveragePreference", "negativeRisk", "extraCount", "resourcePressure",
        "totalCost", "profit", "beverageStock", "cookerAvailable",
    };
    profile["recommendationSortProfile"] = new Dictionary<string, object?>
    {
        ["preset"] = "balanced",
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
    if (includeManagedRareGuestIds)
    {
        profile["managedRareGuestIds"] = managedRareGuestIds?.ToArray() ?? Array.Empty<int>();
    }
    return JsonSerializer.SerializeToElement(profile);
}

static JsonSerializerOptions DeviceAuthorityJsonOptions()
{
    return new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
}

static string ComputeCanonicalProfileHash(JsonElement profile)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        WriteCanonicalJson(writer, profile);
    }
    return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
}

static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
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

static JsonElement RewriteManagedRareGuestIds(
    JsonElement profile,
    JsonElement? replacement,
    bool addUnexpectedField = false)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        foreach (var property in profile.EnumerateObject())
        {
            if (string.Equals(property.Name, "managedRareGuestIds", StringComparison.Ordinal)) continue;
            property.WriteTo(writer);
        }
        if (replacement.HasValue)
        {
            writer.WritePropertyName("managedRareGuestIds");
            replacement.Value.WriteTo(writer);
        }
        if (addUnexpectedField) writer.WriteBoolean("unexpectedField", true);
        writer.WriteEndObject();
    }
    using var document = JsonDocument.Parse(stream.ToArray());
    return document.RootElement.Clone();
}

static JsonElement RewriteRareGuestParticipationModuleEnabled(
    JsonElement profile,
    JsonElement? replacement)
{
    using var stream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(stream))
    {
        writer.WriteStartObject();
        foreach (var property in profile.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    "rareGuestParticipationModuleEnabled",
                    StringComparison.Ordinal))
            {
                continue;
            }
            property.WriteTo(writer);
        }
        if (replacement.HasValue)
        {
            writer.WritePropertyName("rareGuestParticipationModuleEnabled");
            replacement.Value.WriteTo(writer);
        }
        writer.WriteEndObject();
    }
    using var document = JsonDocument.Parse(stream.ToArray());
    return document.RootElement.Clone();
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
    const string original = "{\n  \"version\": 1,\n  \"recipes\": []\n}\n";
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
    AssertMutationOk(store.SetEnabled(false), "Disabling all custom recipes failed.");
    using (var disabled = JsonDocument.Parse(store.GetJson()))
    {
        AssertEqual(false, disabled.RootElement.GetProperty("enabled").GetBoolean(), "The global custom recipe setting was not persisted.");
    }

    var firstCustomerFirst = AddCustomRecipe(store, 1, 10, 100);
    var secondCustomer = AddCustomRecipe(store, 2, 10, 200);
    var firstCustomerSecond = AddCustomRecipe(store, 1, 20, 300);

    AssertMutationOk(store.Move(secondCustomer, "down"), "Moving a single-entry customer group failed.");
    AssertEqual(200, ReadCustomRecipe(store, secondCustomer).GetProperty("sortOrder").GetInt32(), "Move crossed a customer boundary.");
    AssertMutationOk(store.Move(firstCustomerFirst, "down"), "Moving within a customer group failed.");
    AssertEqual(300, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("sortOrder").GetInt32(), "The source recipe was not moved within its customer group.");
    AssertEqual(100, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("sortOrder").GetInt32(), "The target recipe was not moved within its customer group.");
    AssertEqual(200, ReadCustomRecipe(store, secondCustomer).GetProperty("sortOrder").GetInt32(), "Moving another customer changed an unrelated sort order.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Customer, CustomerId = 1 },
        enabled: false,
        pinToTop: null), "Customer bulk disable failed.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("enabled").GetBoolean(), "Customer bulk disable missed the first entry.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("enabled").GetBoolean(), "Customer bulk disable missed the second entry.");
    AssertEqual(true, ReadCustomRecipe(store, secondCustomer).GetProperty("enabled").GetBoolean(), "Customer bulk disable changed another customer.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Recipe, FoodId = 10 },
        enabled: null,
        pinToTop: false), "Recipe bulk unpin failed.");
    AssertEqual(false, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin missed the first entry.");
    AssertEqual(false, ReadCustomRecipe(store, secondCustomer).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin missed the second entry.");
    AssertEqual(true, ReadCustomRecipe(store, firstCustomerSecond).GetProperty("pinToTop").GetBoolean(), "Recipe bulk unpin changed another recipe.");

    AssertMutationOk(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.All },
        enabled: true,
        pinToTop: true), "Updating all custom recipe flags failed.");
    AssertEqual(true, ReadCustomRecipe(store, firstCustomerFirst).GetProperty("enabled").GetBoolean(), "Update-all did not restore enabled state.");
    AssertEqual(true, ReadCustomRecipe(store, secondCustomer).GetProperty("pinToTop").GetBoolean(), "Update-all did not restore pin state.");

    var beforeInvalidMutation = File.ReadAllText(customPath, Encoding.UTF8);
    AssertMutationFailed(store.Move(firstCustomerFirst, "sideways"), "An invalid move direction unexpectedly succeeded.");
    AssertMutationFailed(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Entry, Id = "missing" },
        enabled: false,
        pinToTop: null), "A missing entry update unexpectedly succeeded.");
    AssertMutationFailed(store.UpdateFlags(
        new CustomRecipeSelection { Kind = CustomRecipeSelectionKind.Entry, Id = firstCustomerFirst },
        enabled: null,
        pinToTop: null), "A flagless update unexpectedly succeeded.");
    AssertEqual(beforeInvalidMutation, File.ReadAllText(customPath, Encoding.UTF8), "A rejected bulk mutation changed the custom recipe file.");

    AssertMutationOk(store.SetEnabled(true), "Re-enabling all custom recipes failed.");
}

static string AddCustomRecipe(CustomRecipeStore store, int customerId, int foodId, int sortOrder)
{
    using var response = JsonDocument.Parse(store.Upsert(new CustomRecipeMutation
    {
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
    const string futureCustom = "{\"version\":2,\"recipes\":[],\"futureField\":true}";
    File.WriteAllText(customPath, futureCustom, Encoding.UTF8);
    var customStore = new CustomRecipeStore(customPath, log);
    ExpectInvalidData(customStore.GetJson);
    AssertEqual(futureCustom, File.ReadAllText(customPath, Encoding.UTF8), "A future custom recipe schema was rewritten.");

    foreach (var (fileName, content) in new[]
    {
        (
            "companion-devices-future.json",
            "{\"version\":4,\"registryId\":\"0123456789abcdef0123456789abcdef\",\"authorityRevision\":0,\"stateRevision\":0,\"primaryDeviceId\":\"\",\"devices\":[]}"),
        (
            "companion-devices-missing-version.json",
            "{\"registryId\":\"0123456789abcdef0123456789abcdef\",\"authorityRevision\":0,\"stateRevision\":0,\"primaryDeviceId\":\"\",\"devices\":[]}"),
    })
    {
        var devicePath = Path.Combine(root, fileName);
        File.WriteAllText(devicePath, content, new UTF8Encoding(false));
        var deviceStore = new CompanionDeviceAuthorityStore(devicePath, log);
        ExpectAuthorityError(
            503,
            () => deviceStore.Register(
                "77777777-7777-7777-7777-777777777777",
                "Device",
                RegisterRequest("windows", BuildSharedProfile(false, 2)),
                DateTime.UtcNow));
        AssertEqual(content, File.ReadAllText(devicePath, Encoding.UTF8), "An unsupported device authority schema was rewritten.");
    }
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

static void AssertTrue(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
