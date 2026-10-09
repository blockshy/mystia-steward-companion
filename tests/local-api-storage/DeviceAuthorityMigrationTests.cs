using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;

/// <summary>
/// 使用人工历史文件验证设备配置的前向迁移、原文件备份和失败保护；所有副作用仅发生于测试目录。
/// 第四版必须保留原来的八项排序及参与设置，不能通过重置设备或忽略旧摘要来消除加载错误。
/// </summary>
internal static class DeviceAuthorityMigrationTests
{
    private static int _assertions;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private const string PrimaryId = "11111111-1111-1111-1111-111111111111";
    private const string SecondaryId = "22222222-2222-2222-2222-222222222222";

    public static void Run(string root, ManualLogSource log, JsonElement currentProfile)
    {
        _assertions = 0;
        for (var version = 1; version <= 5; version++)
        {
            var source = BuildHistorical(version, currentProfile);
            var path = Path.Combine(root, $"device-schema-{version}.json");
            var originalBytes = Encoding.UTF8.GetBytes(source.ToJsonString(JsonOptions));
            File.WriteAllBytes(path, originalBytes);
            var store = new CompanionDeviceAuthorityStore(path, log);
            var state = store.ReadBusinessState(DateTime.UtcNow);
            var migrated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            Check(state.ProfileSchemaVersion == 5, $"v{version}未发布第五版配置协议。");
            Check(migrated["version"]!.GetValue<int>() == 5, $"v{version}未迁移到第五版。");
            VerifyPreserved(source, migrated, version);
            var backups = Directory.GetFiles(root, Path.GetFileName(path) + ".schema-v*.bak");
            Check(backups.Length == (version < 5 ? 1 : 0), $"v{version}备份数量错误。");
            if (version < 5) Check(File.ReadAllBytes(backups[0]).SequenceEqual(originalBytes), "备份未保留完整原始字节。");
            else Check(File.ReadAllBytes(path).SequenceEqual(originalBytes), "当前格式只读加载不应重写文件。");

            // 再次读取不能重新迁移、推进修订或新增备份；后台读取也不能伪造客户端在线活动。
            var afterFirstLoad = File.ReadAllBytes(path);
            var reloaded = new CompanionDeviceAuthorityStore(path, log).ReadBusinessState(DateTime.UtcNow);
            Check(File.ReadAllBytes(path).SequenceEqual(afterFirstLoad), "重复加载不是幂等的。");
            Check(reloaded.Devices.All(device => !device.Online), "加载配置错误地标记设备在线。");

            // 迁移中的待同步记录仍能按新摘要确认；随后编辑主配置和重启必须保持两个设备身份。
            var secondary = store.Read(SecondaryId, DateTime.UtcNow);
            var acknowledged = store.AcknowledgeSync(SecondaryId, new CompanionDeviceSyncAckRequest
            {
                ProtocolVersion = 1,
                SyncId = secondary.PendingSyncId ?? throw new InvalidOperationException("测试待同步记录丢失。"),
                ProfileRevision = secondary.CurrentDeviceProfileRevision,
                ProfileHash = secondary.CurrentDeviceProfileHash,
            }, DateTime.UtcNow);
            Check(acknowledged.Devices.Single(device => device.DeviceId == SecondaryId).AppliedProfileRevision == secondary.CurrentDeviceProfileRevision,
                "迁移后待同步配置不能确认。");
            var profile = JsonNode.Parse(state.ActiveProfile.GetRawText())!.AsObject();
            profile["autoRareConcurrency"] = 4;
            var updated = store.UpdatePrimaryProfile(PrimaryId, new CompanionDeviceProfileUpdateRequest
            {
                ProtocolVersion = 1,
                ProfileSchemaVersion = CompanionDeviceAuthorityStore.ProfileSchemaVersion,
                ExpectedAuthorityRevision = state.AuthorityRevision,
                ExpectedProfileRevision = state.ActiveProfileRevision,
                Profile = JsonSerializer.SerializeToElement(profile),
            }, DateTime.UtcNow);
            Check(updated.ActiveProfile.GetProperty("autoRareConcurrency").GetInt32() == 4, "迁移后不能编辑主配置。");
            Check(new CompanionDeviceAuthorityStore(path, log).ReadBusinessState(DateTime.UtcNow).Devices.Count == 2,
                "迁移后保存丢失设备。");
        }

        // 对确实损坏或未来格式仍应拒绝读取，禁止重置、降级或覆盖文件。
        VerifyRejected(root, log, currentProfile, "bad-hash", node => node["devices"]![0]!["profileHash"] = new string('0', 64));
        VerifyRejected(root, log, currentProfile, "future", node => node["version"] = 6);
        VerifyRejected(root, log, currentProfile, "missing-version", node => node.Remove("version"));
        VerifyRejected(root, log, currentProfile, "unknown-field", node => node["futureField"] = true);
        VerifyRejected(root, log, currentProfile, "bad-type", node => node["devices"] = "invalid");
        VerifyRejected(root, log, currentProfile, "duplicate-id", node => node["devices"]![1]!["deviceId"] = PrimaryId);
        VerifyRejected(root, log, currentProfile, "unknown-profile-field", node =>
        {
            var profile = node["devices"]![0]!["profile"]!.AsObject();
            profile["unknownRule"] = true;
            node["devices"]![0]!["profileHash"] = Hash(profile);
        });
        VerifyRejected(root, log, currentProfile, "invalid-v4-objectives", node =>
        {
            var profile = node["devices"]![0]!["profile"]!.AsObject();
            profile["recommendationSortProfile"]!["objectives"]!.AsArray().RemoveAt(0);
            node["devices"]![0]!["profileHash"] = Hash(profile);
        });
        VerifyBackupFailure(root, log, currentProfile, useDirectory: false);
        VerifyBackupFailure(root, log, currentProfile, useDirectory: true);
        VerifyDuplicateField(root, log, currentProfile);
        Console.WriteLine($"PASS: device schema 1–5 migration, preservation and rejection: {_assertions} assertions.");
    }

    /// <summary>实际配置只作为只读输入复制；结果仅输出格式、设备数量和断言数量，不输出设备标识或配置内容。</summary>
    public static void ReplayFile(string sourcePath, string root, ManualLogSource log)
    {
        _assertions = 0;
        var originalBytes = File.ReadAllBytes(sourcePath);
        var source = JsonNode.Parse(Encoding.UTF8.GetString(originalBytes).TrimStart('\uFEFF'))!.AsObject();
        var version = source["version"]!.GetValue<int>();
        var path = Path.Combine(root, "actual-device-copy.json");
        File.WriteAllBytes(path, originalBytes);
        var state = new CompanionDeviceAuthorityStore(path, log).ReadBusinessState(DateTime.UtcNow);
        var migrated = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        VerifyPreserved(source, migrated, version);
        Check(state.ProfileSchemaVersion == 5, "实际配置副本未能加载。");
        Check(File.ReadAllBytes(sourcePath).SequenceEqual(originalBytes), "测试改动了来源配置。");
        Check(Directory.GetFiles(root, "actual-device-copy.json.schema-v*.bak").Length == (version < 5 ? 1 : 0),
            "实际副本的迁移备份数量错误。");
        Console.WriteLine($"PASS: read-only actual configuration replay schema {version} -> 5; devices={state.Devices.Count}; preserved assertions={_assertions}; source unchanged.");
    }

    private static JsonObject BuildHistorical(int version, JsonElement currentProfile)
    {
        var profile = JsonNode.Parse(currentProfile.GetRawText())!.AsObject();
        profile["rareGuestParticipationModuleEnabled"] = true;
        profile["managedRareGuestIds"] = new JsonArray(1, 7, 12);
        if (version <= 2) profile.Remove("rareGuestParticipationModuleEnabled");
        if (version == 1) profile.Remove("managedRareGuestIds");
        if (version == 4)
        {
            var objectives = profile["recommendationSortProfile"]!["objectives"]!.AsArray();
            var cooker = objectives.First(item => item!["key"]!.GetValue<string>() == "cookerAvailable");
            objectives.Remove(cooker);
        }
        JsonObject Device(string id, bool pending) => new()
        {
            ["deviceId"] = id, ["label"] = pending ? "测试手机" : "测试电脑", ["platform"] = pending ? "android" : "windows",
            ["appVersion"] = "1.3.1", ["profileRevision"] = 7L, ["appliedProfileRevision"] = pending ? 6L : 7L,
            ["profileHash"] = Hash(profile), ["profile"] = JsonNode.Parse(profile.ToJsonString()),
            ["pendingSyncId"] = pending ? new string('b', 32) : "",
            ["createdAtUtc"] = "2026-01-01T00:00:00Z", ["updatedAtUtc"] = "2026-01-02T00:00:00Z",
        };
        return new JsonObject
        {
            ["version"] = version, ["registryId"] = new string('a', 32), ["authorityRevision"] = 86L, ["stateRevision"] = 94L,
            ["primaryDeviceId"] = PrimaryId, ["devices"] = new JsonArray(Device(PrimaryId, false), Device(SecondaryId, true)),
        };
    }

    private static void VerifyPreserved(JsonObject before, JsonObject after, int version)
    {
        foreach (var field in new[] { "registryId", "primaryDeviceId", "authorityRevision", "stateRevision" })
            Check(before[field]!.ToJsonString() == after[field]!.ToJsonString(), $"迁移改变了{field}。");
        var oldDevices = before["devices"]!.AsArray();
        var newDevices = after["devices"]!.AsArray();
        Check(oldDevices.Count == newDevices.Count, "迁移改变设备数量。");
        for (var i = 0; i < oldDevices.Count; i++)
        {
            var oldDevice = oldDevices[i]!.AsObject();
            var newDevice = newDevices[i]!.AsObject();
            foreach (var field in oldDevice.Where(field => field.Key is not "profile" and not "profileHash"))
                Check(field.Value!.ToJsonString() == newDevice[field.Key]!.ToJsonString(), $"迁移改变了设备{field.Key}。");
            var oldProfile = oldDevice["profile"]!.AsObject();
            var newProfile = newDevice["profile"]!.AsObject();
            foreach (var field in oldProfile)
            {
                if (field.Key == "recommendationSortProfile" && version == 4) continue;
                Check(field.Value!.ToJsonString() == newProfile[field.Key]!.ToJsonString(), $"迁移改变了既有配置{field.Key}。");
            }
            Check(newProfile["rareGuestParticipationModuleEnabled"]!.GetValue<bool>() ==
                  (version >= 3 && oldProfile["rareGuestParticipationModuleEnabled"]!.GetValue<bool>()), "参与开关迁移不正确。");
            Check(version != 1 || newProfile["managedRareGuestIds"]!.AsArray().Count == 0, "旧版缺失名单未初始化为空。");
            var objectives = newProfile["recommendationSortProfile"]!["objectives"]!.AsArray();
            Check(objectives.Count == 9, "当前配置排序项数量不正确。");
            if (version == 4)
            {
                Check(oldProfile["recommendationSortProfile"]!["preset"]!.ToJsonString() ==
                      newProfile["recommendationSortProfile"]!["preset"]!.ToJsonString(), "排序预设丢失。");
                var oldObjectives = oldProfile["recommendationSortProfile"]!["objectives"]!.AsArray();
                Check(oldObjectives.Select(item => item!.ToJsonString()).SequenceEqual(objectives.Take(8).Select(item => item!.ToJsonString())),
                    "第四版已有八项排序被改变。");
                var cooker = objectives.Single(item => item!["key"]!.GetValue<string>() == "cookerAvailable")!;
                Check(!cooker["enabled"]!.GetValue<bool>() && cooker["weight"]!.GetValue<int>() == 0,
                    "第四版迁移额外启用了厨具排序。");
            }
            Check(newDevice["profileHash"]!.GetValue<string>() == Hash(newProfile), "迁移后摘要不匹配。");
        }
    }

    private static void VerifyRejected(string root, ManualLogSource log, JsonElement profile, string name, Action<JsonObject> corrupt)
    {
        var source = BuildHistorical(4, profile);
        corrupt(source);
        var path = Path.Combine(root, "device-reject-" + name + ".json");
        var original = Encoding.UTF8.GetBytes(source.ToJsonString(JsonOptions));
        File.WriteAllBytes(path, original);
        var rejected = false;
        try { new CompanionDeviceAuthorityStore(path, log).ReadBusinessState(DateTime.UtcNow); }
        catch (CompanionDeviceAuthorityException ex) when (ex.StatusCode == 503) { rejected = true; }
        Check(rejected, $"损坏或未来配置{name}没有被拒绝。");
        Check(File.ReadAllBytes(path).SequenceEqual(original), $"拒绝{name}时改写了原文件。");
        Check(Directory.GetFiles(root, Path.GetFileName(path) + ".schema-v*.bak").Length == 0, "校验失败时不应开始迁移备份。");
    }

    private static string Hash(JsonNode node)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(writer, JsonSerializer.SerializeToElement(node));
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    /// <summary>已有备份损坏或目标不可创建时必须停止迁移，不能以“已经有备份”为由覆盖原配置。</summary>
    private static void VerifyBackupFailure(string root, ManualLogSource log, JsonElement profile, bool useDirectory)
    {
        var path = Path.Combine(root, useDirectory ? "backup-directory.json" : "backup-mismatch.json");
        var original = Encoding.UTF8.GetBytes(BuildHistorical(4, profile).ToJsonString(JsonOptions));
        File.WriteAllBytes(path, original);
        var digest = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        var backupPath = $"{path}.schema-v4-{digest}.bak";
        if (useDirectory) Directory.CreateDirectory(backupPath);
        else File.WriteAllText(backupPath, "人工损坏备份");
        var rejected = false;
        try { new CompanionDeviceAuthorityStore(path, log).ReadBusinessState(DateTime.UtcNow); }
        catch (CompanionDeviceAuthorityException ex) when (ex.StatusCode == 503) { rejected = true; }
        Check(rejected, "备份失败后仍继续迁移。");
        Check(File.ReadAllBytes(path).SequenceEqual(original), "备份失败修改了原配置。");
        if (!useDirectory) Check(File.ReadAllText(backupPath) == "人工损坏备份", "覆盖了已存在的异常备份。");
    }

    /// <summary>普通JSON反序列化会接受重复键；显式文档形状检查必须在迁移前拒绝它。</summary>
    private static void VerifyDuplicateField(string root, ManualLogSource log, JsonElement profile)
    {
        var source = BuildHistorical(4, profile).ToJsonString();
        source = source.Replace("\"version\":4", "\"version\":4,\"version\":4", StringComparison.Ordinal);
        var path = Path.Combine(root, "device-duplicate-field.json");
        File.WriteAllText(path, source);
        var rejected = false;
        try { new CompanionDeviceAuthorityStore(path, log).ReadBusinessState(DateTime.UtcNow); }
        catch (CompanionDeviceAuthorityException ex) when (ex.StatusCode == 503) { rejected = true; }
        Check(rejected, "重复的存储字段未被拒绝。");
        Check(File.ReadAllText(path) == source, "重复字段被静默规范化并覆盖。");
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var field in value.EnumerateObject().OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(field.Name);
                WriteCanonical(writer, field.Value);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
            writer.WriteEndArray();
        }
        else value.WriteTo(writer);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _assertions++;
    }
}
