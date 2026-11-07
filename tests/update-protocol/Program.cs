using MystiaStewardCompanion.Updates;
using BepInEx.Logging;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using System.Security.Cryptography;

try
{
    VerifySemanticVersionOrdering();
    VerifyManifestValidation();
    VerifyReleaseCatalogValidationAndFiltering();
    VerifyOptionalReleaseHistoryIsolation();
    VerifyCachedReleaseHistoryValidation();
    VerifyAtomParsing();
    VerifyCachedStateValidation();
    VerifyLegacyCacheInvalidation();
    VerifyActiveRollbackSurvivesCacheInvalidation();
    VerifySchemaTwoDownloadAndReload();
    VerifyBoundedDownloadCopy();
    VerifyUpdateSchedule();
    VerifySchedulerLifecycle();
    VerifyCancelledCheckRecovery();
    VerifyManualCheckCancellation();
    VerifyDownloadCancellation();
    VerifyRecurringSchedulerAndMutualExclusion();
    VerifyDisposeRetryAfterTimeout();
    VerifyAbruptOperationRecovery();
    Console.WriteLine("PASS: update protocol validation, optional release-history isolation, retry scheduling, recurring execution, mutual exclusion, lifecycle cancellation, abrupt recovery cleanup, and bounded downloads are correct.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifySemanticVersionOrdering()
{
    AssertEqual(true, SemanticVersion.TryParse("v1.2.0-preview.2", out var preview2), "preview.2 did not parse.");
    AssertEqual(true, SemanticVersion.TryParse("1.2.0-preview.10", out var preview10), "preview.10 did not parse.");
    AssertEqual(true, SemanticVersion.TryParse("1.2.0", out var stable), "stable version did not parse.");
    AssertEqual(true, preview10.CompareTo(preview2) > 0, "Numeric prerelease identifiers were compared lexically.");
    AssertEqual(true, stable.CompareTo(preview10) > 0, "Stable release did not sort after prerelease.");
}

static void VerifyManifestValidation()
{
    UpdateService.ValidateManifest(Manifest());
    UpdateService.ValidateManifest(Manifest(withCatalog: true));
    UpdateService.ValidateManifest(Manifest(version: "1.4.0-preview.1", tag: "v1.4.0-preview.1", channel: "preview"));
    foreach (var schema in new[] { 0, 1, 3 }) ExpectInvalidManifest(Manifest(schemaVersion: schema), "schemaVersion");
    ExpectInvalidManifest(Manifest(channel: "preview"), "channel");
    ExpectInvalidManifest(Manifest(packageSha256: "abc"), "packageSha256");
    ExpectInvalidManifest(new UpdateManifest
    {
        SchemaVersion = 2,
        Version = "1.2.3",
        Tag = "v1.2.3",
        Channel = "stable",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = null!,
        PackageSize = 42,
    }, "packageSha256");
    ExpectInvalidManifest(Manifest(packageSize: 0), "packageSize");
    ExpectInvalidManifest(Manifest(tag: "v1.2.4"), "version 与 tag");
    ExpectInvalidManifest(Manifest(catalogAsset: "update-catalog.json"), "版本说明目录字段必须同时提供");
    ExpectInvalidManifest(Manifest(withCatalog: true, catalogAsset: "catalog.json"), "catalogAsset");
    ExpectInvalidManifest(Manifest(withCatalog: true, catalogSha256: "abc"), "catalogSha256");
    ExpectInvalidManifest(Manifest(withCatalog: true, catalogSize: -1), "catalogSize");
}

static void VerifyReleaseCatalogValidationAndFiltering()
{
    var manifest = Manifest(version: "1.3.0", tag: "v1.3.0", withCatalog: true);
    var catalog = Catalog(
        ownerVersion: "1.3.0",
        releases: new[]
        {
            CatalogRelease("1.2.2", "stable"),
            CatalogRelease("1.2.4", "stable"),
            CatalogRelease("1.2.5-preview.1", "preview"),
            CatalogRelease("1.2.5", "stable"),
            CatalogRelease("1.3.0", "stable"),
        });

    UpdateService.ValidateCatalog(catalog, manifest);

    var stable = UpdateService.BuildAvailableReleases(catalog, "1.2.3", "1.3.0", includePrerelease: false);
    AssertEqual("1.2.4,1.2.5,1.3.0", string.Join(',', stable.Select(release => release.Version)),
        "Stable update history included an old or preview release, or lost SemVer order.");
    var preview = UpdateService.BuildAvailableReleases(catalog, "1.2.3", "1.3.0", includePrerelease: true);
    AssertEqual("1.2.4,1.2.5-preview.1,1.2.5,1.3.0", string.Join(',', preview.Select(release => release.Version)),
        "Preview update history did not expose every intermediate supported release.");

    ExpectInvalidCatalog(
        Catalog("1.3.1", new[] { CatalogRelease("1.3.1", "stable") }),
        manifest,
        "owner 与 manifest 不一致");
    ExpectInvalidCatalog(
        Catalog("1.3.0", new[]
        {
            CatalogRelease("1.2.4", "stable"),
            CatalogRelease("1.2.4", "stable"),
            CatalogRelease("1.3.0", "stable"),
        }),
        manifest,
        "重复版本或 tag");
    ExpectInvalidCatalog(
        Catalog("1.3.0", new[]
        {
            CatalogRelease("1.2.5", "stable"),
            CatalogRelease("1.2.4", "stable"),
            CatalogRelease("1.3.0", "stable"),
        }),
        manifest,
        "SemVer 严格升序");
    ExpectInvalidCatalog(
        Catalog("1.3.0", new[] { CatalogRelease("1.3.0", "preview") }),
        manifest,
        "channel 无效");
    ExpectInvalidCatalog(
        Catalog("1.3.0", new[] { CatalogRelease("1.3.0", "stable", releaseUrl: "https://example.test/v1.3.0") }),
        manifest,
        "release URL 无效");
    ExpectInvalidCatalog(
        Catalog("1.3.0", new[] { CatalogRelease("1.3.0", "stable", tag: "1.3.0") }),
        manifest,
        "版本或 tag 无效");
    ExpectInvalidCatalog(
        Catalog("1.3.0", new[] { CatalogRelease("1.3.0", "stable", notesMarkdown: "  ") }),
        manifest,
        "说明无效或过大");
    ExpectInvalidCatalog(
        Catalog("1.3.0", new[]
        {
            CatalogRelease("1.3.0", "stable"),
            CatalogRelease("1.3.1", "stable"),
        }),
        manifest,
        "晚于 owner");
}

// 版本说明目录是附属信息：其下载或验证失败应独立报告，不能隐藏已确认可用的更新包。
// 每个服务使用独立临时目录与合成下载委托，不访问正式安装或远端 Release。
static void VerifyOptionalReleaseHistoryIsolation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-history-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-history-smoke");
    var nowUtc = new DateTime(2026, 8, 12, 1, 2, 3, DateTimeKind.Utc);
    try
    {
        var candidateWithCatalog = Candidate(withCatalog: true);
        using (var service = new UpdateService(
                   Settings(),
                   log,
                   Path.Combine(root, "ready"),
                   () => nowUtc,
                   _ => candidateWithCatalog,
                   fetchReleaseCatalog: (_, _) => Catalog(
                       "1.2.4",
                       new[]
                       {
                           CatalogRelease("1.2.3", "stable"),
                           CatalogRelease("1.2.4", "stable"),
                       })))
        {
            var status = service.CheckForUpdates();
            AssertEqual(true, status.Ok, "A valid release catalog caused the core update check to fail.");
            AssertEqual(true, status.HasUpdate, "A valid release catalog hid an available update.");
            AssertEqual("ready", status.ReleaseHistoryState, "A valid release catalog was not published as ready.");
            AssertEqual(1, status.AvailableReleases.Count, "The release list did not filter out the current version.");
            AssertEqual("1.2.4", status.AvailableReleases[0].Version, "The available release version was incorrect.");
        }

        using (var service = new UpdateService(
                   Settings(),
                   log,
                   Path.Combine(root, "failed"),
                   () => nowUtc,
                   _ => candidateWithCatalog,
                   fetchReleaseCatalog: (_, _) => throw new HttpRequestException("expected catalog outage")))
        {
            var status = service.CheckForUpdates();
            AssertEqual(true, status.Ok, "A release-catalog outage failed the core update check.");
            AssertEqual(true, status.HasUpdate, "A release-catalog outage hid an available update.");
            AssertEqual("available", status.State, "A release-catalog outage changed the core update state.");
            AssertEqual("failed", status.ReleaseHistoryState, "A release-catalog outage was not isolated as a history failure.");
            AssertEqual(0, status.AvailableReleases.Count, "A failed release catalog exposed unverified release history.");
            AssertEqual(true, status.ReleaseHistoryError?.Contains("expected catalog outage", StringComparison.Ordinal) == true,
                "The isolated release-history error was not exposed.");
            AssertEqual<string?>(null, status.Error, "A release-history error leaked into the core update error.");
        }

        using (var service = new UpdateService(
                   Settings(),
                   log,
                   Path.Combine(root, "bounded-failure"),
                   () => nowUtc,
                   _ => candidateWithCatalog,
                   fetchReleaseCatalog: (_, _) => throw new HttpRequestException(new string('错', 5000))))
        {
            var status = service.CheckForUpdates();
            AssertEqual(true, status.Ok, "An oversized release-history error failed the core update check.");
            AssertEqual(true, Encoding.UTF8.GetByteCount(status.ReleaseHistoryError ?? "") <= 4 * 1024,
                "A release-history diagnostic exceeded its persisted/API UTF-8 bound.");
        }

        using (var service = new UpdateService(
                   Settings(),
                   log,
                   Path.Combine(root, "manifest-without-catalog"),
                   () => nowUtc,
                   _ => Candidate()))
        {
            var status = service.CheckForUpdates();
            AssertEqual(true, status.Ok, "A schema-2 manifest without catalog metadata failed the core update check.");
            AssertEqual(true, status.HasUpdate, "A schema-2 manifest without catalog metadata hid an available update.");
            AssertEqual("unavailable", status.ReleaseHistoryState,
                "A schema-2 manifest without catalog metadata did not report unavailable history.");
            AssertEqual(0, status.AvailableReleases.Count,
                "A schema-2 manifest without catalog metadata fabricated release history.");
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

// 缓存中的说明必须仍绑定当前清单的大小与摘要；仅保留相同版本号不足以建立信任。
static void VerifyCachedReleaseHistoryValidation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-history-cache-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-history-cache-smoke");
    var nowUtc = new DateTime(2026, 8, 12, 1, 2, 3, DateTimeKind.Utc);
    try
    {
        var validRoot = Path.Combine(root, "valid");
        var validState = AvailableState("available", nowUtc);
        validState.CatalogAsset = "update-catalog.json";
        validState.CatalogSha256 = new string('b', 64);
        validState.CatalogSize = 1024;
        validState.ReleaseHistoryState = "ready";
        validState.ReleaseHistoryTag = "v1.2.4";
        validState.ReleaseCatalog = Catalog(
            "1.2.4",
            new[]
            {
                CatalogRelease("1.2.3", "stable"),
                CatalogRelease("1.2.4", "stable"),
            });
        WriteUpdateState(validRoot, validState);
        using (var validService = NewRecoveryService(validRoot, nowUtc, log))
        {
            var status = validService.GetStatus();
            AssertEqual("ready", status.ReleaseHistoryState,
                "A catalog cache with complete manifest identity was rejected.");
            AssertEqual(1, status.AvailableReleases.Count,
                "A valid catalog cache did not expose the release after the current version.");
        }

        var unboundRoot = Path.Combine(root, "unbound");
        var unboundState = AvailableState("available", nowUtc);
        unboundState.ReleaseHistoryState = "ready";
        unboundState.ReleaseHistoryTag = "v1.2.4";
        unboundState.ReleaseCatalog = validState.ReleaseCatalog;
        WriteUpdateState(unboundRoot, unboundState);
        using (var unboundService = NewRecoveryService(unboundRoot, nowUtc, log))
        {
            var status = unboundService.GetStatus();
            AssertEqual("failed", status.ReleaseHistoryState,
                "A catalog cache without manifest size/hash identity was accepted.");
            AssertEqual(0, status.AvailableReleases.Count,
                "An unbound catalog cache exposed unverified release notes.");
            AssertEqual(true, status.ReleaseHistoryError?.Contains("缺少 catalog", StringComparison.Ordinal) == true,
                "The rejected catalog cache did not preserve a bounded diagnostic.");
        }
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyCachedStateValidation()
{
    var state = new UpdateState
    {
        LatestVersion = "1.2.4",
        LatestTag = "v1.2.4",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = new string('a', 64),
        PackageSize = 42,
        PackageDownloadUrl = "https://example.test/package.zip",
    };
    AssertEqual(false, UpdateService.HasAvailableUpdate(state), "An unmarked cache was silently promoted to the supported schema.");
    state.ManifestSchemaVersion = 1;
    AssertEqual(false, UpdateService.HasAvailableUpdate(state), "A legacy schema cache was accepted.");
    state.ManifestSchemaVersion = 3;
    AssertEqual(false, UpdateService.HasAvailableUpdate(state), "A future schema cache was accepted.");
    state.ManifestSchemaVersion = 2;
    AssertEqual(true, UpdateService.HasAvailableUpdate(state), "A valid cached candidate was rejected.");
    state.PackageSize = 0;
    AssertEqual(false, UpdateService.HasAvailableUpdate(state), "A cached candidate without package size was accepted.");
}

// 旧缓存没有真实 schema 字段；即使带有效版本、URL 和暂存目录，也必须重新检查而不能补填 schema 2。
// 测试不运行 updater，下载替身只观察新候选被采用后立即失败，原暂存哨兵必须保留。
static void VerifyLegacyCacheInvalidation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-schema-cache-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-schema-cache-smoke");
    var now = new DateTime(2026, 11, 7, 1, 2, 3, DateTimeKind.Utc);
    try
    {
        foreach (var schema in new[] { 0, 1, 3 })
        {
            var serviceRoot = Path.Combine(root, schema.ToString());
            var staged = Path.Combine(serviceRoot, "downloads", "legacy-staged");
            Directory.CreateDirectory(staged);
            var sentinel = Path.Combine(staged, "user-note.txt");
            File.WriteAllText(sentinel, "retained old staged data");
            var state = AvailableState("downloaded", now);
            state.ManifestSchemaVersion = schema;
            state.DownloadedManifestSchemaVersion = schema;
            state.DownloadedVersion = "1.2.4";
            state.StagedDirectory = staged;
            state.PackageDownloadUrl = "https://example.test/legacy-only.zip";
            WriteUpdateState(serviceRoot, state);
            if (schema == 0)
            {
                var path = Path.Combine(serviceRoot, "update-state.json");
                var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                json.Remove("manifestSchemaVersion");
                json.Remove("downloadedManifestSchemaVersion");
                File.WriteAllText(path, json.ToJsonString());
            }
            var fetched = 0;
            var downloaded = 0;
            using var service = new UpdateService(Settings(), log, serviceRoot, () => now,
                _ => { fetched++; return Candidate(); },
                downloadFile: (url, _, _, _) =>
                {
                    AssertEqual(Candidate().PackageDownloadUrl, url, "Download reused an invalidated legacy URL.");
                    downloaded++;
                    throw new InvalidOperationException("synthetic stop after fresh schema-2 selection");
                });
            var loaded = service.GetStatus();
            AssertEqual(false, loaded.HasUpdate, "Legacy cached metadata remained available before a fresh check.");
            AssertEqual(false, loaded.Staged, "Legacy cached staging remained installable.");
            AssertEqual(now.ToString("O"), loaded.NextCheckAtUtc, "Legacy cache kept an old automatic-check backoff.");
            AssertEqual(false, service.InstallOnExit().Ok, "Legacy staging reached the updater launch path.");
            service.DownloadUpdate();
            AssertEqual(1, fetched, "Legacy cache download did not recheck the remote manifest.");
            AssertEqual(1, downloaded, "A freshly accepted schema-2 candidate could not enter download.");
            AssertEqual("retained old staged data", File.ReadAllText(sentinel), "Cache invalidation deleted staged files.");
            var persisted = ReadUpdateState(serviceRoot);
            AssertEqual(2, persisted.ManifestSchemaVersion, "The accepted manifest schema was not persisted.");
            AssertEqual(0, persisted.DownloadedManifestSchemaVersion, "A fresh check retroactively authorized old staging.");
            AssertEqual("", persisted.StagedDirectory, "An invalidated staging reference was restored by a later check.");
        }

        // 已有 schema-2 候选并不能为缺少下载来源的旧暂存包背书。
        var mixedRoot = Path.Combine(root, "mixed");
        var mixed = AvailableState("downloaded", now);
        mixed.DownloadedVersion = "1.2.4";
        mixed.StagedDirectory = Path.Combine(mixedRoot, "staged");
        Directory.CreateDirectory(mixed.StagedDirectory);
        WriteUpdateState(mixedRoot, mixed);
        using (var service = NewRecoveryService(mixedRoot, now, log))
        {
            AssertEqual(true, service.GetStatus().HasUpdate, "Valid schema-2 candidate was lost with unrelated old staging.");
            AssertEqual(false, service.GetStatus().Staged, "Old staging inherited the latest candidate's schema.");
        }

        // 候选数据源本身也必须通过门禁，不能依赖某个网络读取实现曾经验证过。
        var invalidFetches = 0;
        using (var service = new UpdateService(Settings(), log, Path.Combine(root, "old-remote"), () => now,
                   _ => new UpdateCandidate { Manifest = Manifest(schemaVersion: 1, version: "1.2.4", tag: "v1.2.4"), PackageDownloadUrl = "https://example.test/old.zip" },
                   downloadFile: (_, _, _, _) => invalidFetches++))
        {
            AssertEqual(false, service.DownloadUpdate().Ok, "A schema-1 remote candidate was accepted.");
            AssertEqual(0, invalidFetches, "A schema-1 remote candidate started a package download.");
        }
    }
    finally { Logger.Sources.Remove(log); Directory.Delete(root, recursive: true); }
}

// 使用完整清单与合成载荷走真正的下载摘要/解压/暂存路径，验证 schema-2 更新可在重启后继续使用。
// EXE/DLL 只作为字节夹具，整个用例不调用安装入口或启动任何合成程序。
static void VerifySchemaTwoDownloadAndReload()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-schema-download-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-schema-download-smoke");
    var now = new DateTime(2026, 11, 7, 2, 3, 4, DateTimeKind.Utc);
    try
    {
        var source = Path.Combine(root, "source");
        var package = Path.Combine(source, "mystia-steward-companion");
        Directory.CreateDirectory(package);
        var files = new List<object>();
        foreach (var name in BusinessPackageManifest.Files)
        {
            var bytes = Encoding.UTF8.GetBytes("synthetic package payload: " + name);
            var path = Path.Combine(package, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            files.Add(new { path = name, size = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
        }
        File.WriteAllText(Path.Combine(package, "package-manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, product = "mystia-steward-companion", version = "1.2.4", sourceCommit = new string('a', 40),
            sourceDigest = new string('b', 64), toolchainSha256 = new string('c', 64), businessProtocolVersion = 1, clientProtocolVersion = 2, files,
        }));
        var zip = Path.Combine(root, "fixture.zip");
        ZipFile.CreateFromDirectory(source, zip);
        var manifest = Manifest(version: "1.2.4", tag: "v1.2.4", packageSize: new FileInfo(zip).Length,
            packageSha256: Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant());
        var serviceRoot = Path.Combine(root, "updates");
        using (var service = new UpdateService(Settings(), log, serviceRoot, () => now,
                   _ => new UpdateCandidate { Manifest = manifest, PackageDownloadUrl = "https://example.test/new.zip" },
                   downloadFile: (_, destination, _, _) => File.Copy(zip, destination)))
        {
            var status = service.DownloadUpdate();
            AssertEqual(true, status.Ok, "Schema-2 package failed the full download and staging flow.");
            AssertEqual(true, status.Staged, "Schema-2 staging was not reported as installable.");
        }
        var stored = ReadUpdateState(serviceRoot);
        AssertEqual(2, stored.ManifestSchemaVersion, "Fresh candidate did not preserve its schema.");
        AssertEqual(2, stored.DownloadedManifestSchemaVersion, "Successful download did not preserve its own schema.");
        using (var service = new UpdateService(Settings(), log, serviceRoot, () => now,
                   _ => new UpdateCandidate { Manifest = Manifest(version: "1.2.5", tag: "v1.2.5"), PackageDownloadUrl = "https://example.test/next.zip" }))
        {
            AssertEqual(true, service.GetStatus().Staged, "Restart discarded verified schema-2 staging.");
            service.CheckForUpdates();
            var latest = ReadUpdateState(serviceRoot);
            AssertEqual("1.2.5", latest.LatestVersion, "Next schema-2 online update could not be discovered.");
            AssertEqual("1.2.4", latest.DownloadedVersion, "Checking a newer release rewrote the staged package identity.");
            AssertEqual(2, latest.DownloadedManifestSchemaVersion, "Checking a newer release lost staging provenance.");
        }
    }
    finally { Logger.Sources.Remove(log); Directory.Delete(root, recursive: true); }
}

static UpdateState ReadUpdateState(string root) => JsonSerializer.Deserialize<UpdateState>(
    File.ReadAllText(Path.Combine(root, "update-state.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

// 使用可控的进程存活探针验证服务端互斥，不启动实际 updater，也不把此测试视为文件回滚事务验收。
// 清单缓存失效只能撤销下载来源，不能抹除仍在执行回滚的 PID 或允许覆盖该进程持有的暂存文件。
static void VerifyActiveRollbackSurvivesCacheInvalidation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-active-rollback-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-active-rollback-smoke");
    var now = new DateTime(2026, 11, 7, 3, 4, 5, DateTimeKind.Utc);
    try
    {
        foreach (var schema in new[] { 0, 1, 3 })
        {
            var serviceRoot = Path.Combine(root, schema.ToString());
            var state = AvailableState("downloaded", now);
            state.ManifestSchemaVersion = schema;
            state.DownloadedManifestSchemaVersion = schema;
            state.DownloadedVersion = "1.2.4";
            state.StagedDirectory = Path.Combine(serviceRoot, "downloads", "1.2.4", "staged");
            state.InstallState = "rolling-back";
            state.InstallProcessId = 12345;
            Directory.CreateDirectory(state.StagedDirectory);
            var sentinel = Path.Combine(state.StagedDirectory, "rollback-owned.txt");
            File.WriteAllText(sentinel, "active rollback owns this staging");
            WriteUpdateState(serviceRoot, state);
            var statusPath = Path.Combine(serviceRoot, "install-status.json");
            File.WriteAllText(statusPath, "{\"state\":\"rolling-back\",\"message\":\"synthetic active rollback\",\"progress\":75}");
            var running = true;
            var fetches = 0;
            var downloads = 0;
            using var service = new UpdateService(Settings(), log, serviceRoot, () => now,
                _ => { fetches++; return Candidate(); },
                downloadFile: (_, _, _, _) => downloads++,
                isUpdaterProcessRunning: pid =>
                {
                    AssertEqual(12345, pid, "Schema invalidation changed the active updater PID.");
                    return running;
                });
            var loaded = service.GetStatus();
            AssertEqual(false, loaded.HasUpdate, "Old candidate remained usable during rollback.");
            AssertEqual(false, loaded.Staged, "Old staging remained authorized after schema invalidation.");
            AssertEqual("rolling-back", loaded.InstallState, "Schema invalidation erased the active rollback state.");
            var rejectedDownload = service.DownloadUpdate();
            AssertEqual(false, rejectedDownload.Ok, "A running rollback allowed another download.");
            AssertEqual(true, rejectedDownload.Error?.Contains("不能覆盖", StringComparison.Ordinal) == true,
                "The download did not reject the active installation before attempting a fresh check.");
            var existingInstall = service.InstallOnExit();
            AssertEqual(true, existingInstall.Ok, "Repeated install should report the existing active operation.");
            AssertEqual("rolling-back", existingInstall.InstallState, "Repeated install did not preserve the running rollback.");
            AssertEqual(0, fetches, "Download began a fresh check while rollback still owned the staging area.");
            AssertEqual(0, downloads, "Download wrote a new package while rollback was active.");
            AssertEqual(12345, ReadUpdateState(serviceRoot).InstallProcessId, "The persisted active PID was lost.");
            AssertEqual("active rollback owns this staging", File.ReadAllText(sentinel), "Active staging was modified.");

            // 同一在途状态在进程退出后必须转为失败，不能永久显示为仍在回滚。
            running = false;
            AssertEqual("failed", service.GetStatus().InstallState, "An exited updater left rollback indefinitely active.");
            AssertEqual("failed", JsonNode.Parse(File.ReadAllText(statusPath))!["state"]!.GetValue<string>(),
                "The interrupted rollback status was not persisted as failed.");
        }
    }
    finally { Logger.Sources.Remove(log); Directory.Delete(root, recursive: true); }
}

// 校验流复制的实际字节数，覆盖长度不足和超出声明大小，防止只信任响应元数据。
static void VerifyBoundedDownloadCopy()
{
    var content = new byte[] { 1, 2, 3, 4 };
    using var source = new MemoryStream(content);
    using var destination = new MemoryStream();
    UpdateService.CopyDownloadContentAsync(source, destination, content.Length, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(true, content.SequenceEqual(destination.ToArray()), "Exact-size download content did not round-trip.");

    ExpectInvalidDownloadSize(content, content.Length - 1);
    ExpectInvalidDownloadSize(content, content.Length + 1);
}

static void VerifyUpdateSchedule()
{
    var completedAtUtc = new DateTime(2026, 7, 14, 1, 2, 3, DateTimeKind.Utc);
    var expectedFailureDelays = new[]
    {
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(4),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(6),
    };
    for (var index = 0; index < expectedFailureDelays.Length; index += 1)
    {
        var failureCount = index + 1;
        AssertEqual(
            expectedFailureDelays[index],
            UpdateService.CalculateFailureRetryDelay(failureCount),
            $"Unexpected retry delay for failure {failureCount}.");
        AssertEqual(
            completedAtUtc + expectedFailureDelays[index],
            UpdateService.CalculateNextCheckAtUtc(completedAtUtc, false, failureCount, 24),
            $"Unexpected retry timestamp for failure {failureCount}.");
    }

    AssertEqual(
        completedAtUtc + TimeSpan.FromHours(1),
        UpdateService.CalculateNextCheckAtUtc(completedAtUtc, true, 0, 0),
        "Successful checks did not clamp the minimum interval.");
    AssertEqual(
        completedAtUtc + TimeSpan.FromHours(168),
        UpdateService.CalculateNextCheckAtUtc(completedAtUtc, true, 0, 999),
        "Successful checks did not clamp the maximum interval.");
}

static void VerifySchedulerLifecycle()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-scheduler-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-scheduler-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 2, 0, 0, DateTimeKind.Utc);
        var fetchCount = 0;
        using var service = new UpdateService(
            Settings(),
            log,
            Path.Combine(root, "single"),
            () => nowUtc,
            _ =>
            {
                Interlocked.Increment(ref fetchCount);
                return Candidate();
            });

        service.StartAutoCheckScheduler();
        service.StartAutoCheckScheduler();
        if (!SpinWait.SpinUntil(
                () => !string.IsNullOrWhiteSpace(service.GetStatus().LastSuccessAtUtc),
                TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The automatic update scheduler did not complete its due check.");
        }

        Thread.Sleep(100);
        AssertEqual(1, fetchCount, "Starting the scheduler twice created duplicate checks.");
        var status = service.GetStatus();
        AssertEqual(nowUtc.ToString("O"), status.LastAttemptAtUtc, "Last attempt time was not exposed.");
        AssertEqual(nowUtc.ToString("O"), status.LastSuccessAtUtc, "Last success time was not exposed.");
        AssertEqual((nowUtc + TimeSpan.FromHours(24)).ToString("O"), status.NextCheckAtUtc, "Next successful check time was incorrect.");
        AssertEqual(0, status.ConsecutiveFailures, "A successful check retained a failure count.");
        service.Dispose();
        service.Dispose();
        ExpectDisposed(service.StartAutoCheckScheduler);

        var failureNowUtc = nowUtc;
        using (var failureService = new UpdateService(
                   Settings(),
                   log,
                   Path.Combine(root, "failure"),
                   () => failureNowUtc,
                   _ => throw new HttpRequestException("expected network failure")))
        {
            var firstFailure = failureService.CheckForUpdates();
            AssertEqual(1, firstFailure.ConsecutiveFailures, "The first failed check did not increment the failure count.");
            AssertEqual(
                (failureNowUtc + TimeSpan.FromMinutes(15)).ToString("O"),
                firstFailure.NextCheckAtUtc,
                "The first failed check did not schedule a fifteen-minute retry.");

            failureNowUtc += TimeSpan.FromMinutes(1);
            var secondFailure = failureService.CheckForUpdates();
            AssertEqual(2, secondFailure.ConsecutiveFailures, "The second failed check did not retain the failure sequence.");
            AssertEqual(
                (failureNowUtc + TimeSpan.FromMinutes(30)).ToString("O"),
                secondFailure.NextCheckAtUtc,
                "The second failed check did not schedule a thirty-minute retry.");
            AssertEqual("", secondFailure.LastSuccessAtUtc, "A failed check was recorded as successful.");
        }

        using var fetchStarted = new ManualResetEventSlim();
        using var fetchCancelled = new ManualResetEventSlim();
        using var cancellableService = new UpdateService(
            Settings(),
            log,
            Path.Combine(root, "cancellable"),
            () => nowUtc,
            cancellationToken =>
            {
                fetchStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                fetchCancelled.Set();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was not observed.");
            });
        cancellableService.StartAutoCheckScheduler();
        if (!fetchStarted.Wait(TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The cancellable scheduler did not start its check.");
        }

        cancellableService.Dispose();
        AssertEqual(true, fetchCancelled.IsSet, "Disposing the update service did not cancel an active automatic check.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyCancelledCheckRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-recovery-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-recovery-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 3, 0, 0, DateTimeKind.Utc);
        using (var failedService = new UpdateService(
                   Settings(),
                   log,
                   root,
                   () => nowUtc,
                   _ => throw new HttpRequestException("expected prior failure")))
        {
            AssertEqual(1, failedService.CheckForUpdates().ConsecutiveFailures, "The recovery fixture did not persist a prior failure.");
        }

        nowUtc += TimeSpan.FromMinutes(15);
        using var cancelledFetchStarted = new ManualResetEventSlim();
        using var cancelledFetchObserved = new ManualResetEventSlim();
        using (var interruptedService = new UpdateService(
                   Settings(),
                   log,
                   root,
                   () => nowUtc,
                   cancellationToken =>
                   {
                       cancelledFetchStarted.Set();
                       cancellationToken.WaitHandle.WaitOne();
                       cancelledFetchObserved.Set();
                       cancellationToken.ThrowIfCancellationRequested();
                       throw new InvalidOperationException("Cancellation was not observed.");
                   }))
        {
            interruptedService.StartAutoCheckScheduler();
            AssertEqual(true, cancelledFetchStarted.Wait(TimeSpan.FromSeconds(2)), "The due retry did not start.");
            interruptedService.BeginShutdown();
            interruptedService.Dispose();
        }
        AssertEqual(true, cancelledFetchObserved.IsSet, "The interrupted retry did not observe service shutdown.");

        var resumedFetchCount = 0;
        using var resumedService = new UpdateService(
            Settings(),
            log,
            root,
            () => nowUtc,
            _ =>
            {
                Interlocked.Increment(ref resumedFetchCount);
                return Candidate();
            });
        var recoveredStatus = resumedService.GetStatus();
        AssertEqual(false, recoveredStatus.State == "checking", "A cancelled check remained persisted as active.");
        AssertEqual(nowUtc.ToString("O"), recoveredStatus.NextCheckAtUtc, "A cancelled check retained its previous failure backoff.");

        resumedService.StartAutoCheckScheduler();
        if (!SpinWait.SpinUntil(
                () => !string.IsNullOrWhiteSpace(resumedService.GetStatus().LastSuccessAtUtc),
                TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The service did not immediately retry after a cancelled check restart.");
        }
        AssertEqual(1, resumedFetchCount, "Restart recovery started an unexpected number of checks.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyManualCheckCancellation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-manual-cancel-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-manual-cancel-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 4, 0, 0, DateTimeKind.Utc);
        using var fetchStarted = new ManualResetEventSlim();
        using var fetchCancelled = new ManualResetEventSlim();
        var fetchCount = 0;
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            () => nowUtc,
            cancellationToken =>
            {
                Interlocked.Increment(ref fetchCount);
                fetchStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                fetchCancelled.Set();
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cancellation was not observed.");
            });

        var checkTask = Task.Run(service.CheckForUpdates);
        AssertEqual(true, fetchStarted.Wait(TimeSpan.FromSeconds(2)), "The manual check did not start.");
        service.BeginShutdown();
        var rejectedStatus = service.CheckForUpdates();
        var status = WaitForTask(checkTask, "The manual check did not stop after service shutdown.");
        service.Dispose();

        AssertEqual(false, rejectedStatus.Ok, "A new update operation entered after shutdown began.");
        AssertEqual(1, fetchCount, "A new update fetch started after shutdown began.");
        AssertEqual(true, fetchCancelled.IsSet, "The manual check did not receive the service cancellation token.");
        AssertEqual(false, status.Ok, "A shutdown-cancelled manual check reported success.");
        AssertEqual("idle", status.State, "A shutdown-cancelled manual check did not restore a stable state.");
        AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "A shutdown-cancelled manual check was not immediately rescheduled.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyDownloadCancellation()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-download-cancel-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-download-cancel-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 5, 0, 0, DateTimeKind.Utc);
        using var downloadStarted = new ManualResetEventSlim();
        using var downloadCancelled = new ManualResetEventSlim();
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            () => nowUtc,
            _ => Candidate(),
            downloadFile: (_, _, _, cancellationToken) =>
            {
                downloadStarted.Set();
                cancellationToken.WaitHandle.WaitOne();
                downloadCancelled.Set();
                cancellationToken.ThrowIfCancellationRequested();
            });
        AssertEqual(true, service.CheckForUpdates().HasUpdate, "The download fixture did not discover an update.");

        var downloadTask = Task.Run(service.DownloadUpdate);
        AssertEqual(true, downloadStarted.Wait(TimeSpan.FromSeconds(2)), "The update download did not start.");
        service.BeginShutdown();
        var status = WaitForTask(downloadTask, "The update download did not stop after service shutdown.");
        service.Dispose();

        AssertEqual(true, downloadCancelled.IsSet, "The download did not receive the service cancellation token.");
        AssertEqual(false, status.Ok, "A shutdown-cancelled download reported success.");
        AssertEqual("available", status.State, "A shutdown-cancelled download did not restore the available update state.");
        var downloadsRoot = Path.Combine(root, "downloads");
        var pendingDirectories = Directory.Exists(downloadsRoot)
            ? Directory.EnumerateDirectories(downloadsRoot, ".*.tmp", SearchOption.TopDirectoryOnly).ToArray()
            : Array.Empty<string>();
        AssertEqual(0, pendingDirectories.Length, "A shutdown-cancelled download left a temporary directory behind.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyRecurringSchedulerAndMutualExclusion()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-recurring-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-recurring-smoke");
    try
    {
        var clock = new AdvancingSchedulerClock(new DateTime(2026, 7, 14, 6, 0, 0, DateTimeKind.Utc));
        using var secondCheckStarted = new ManualResetEventSlim();
        var fetchCount = 0;
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            clock.UtcNow,
            cancellationToken =>
            {
                var currentFetch = Interlocked.Increment(ref fetchCount);
                if (currentFetch == 2)
                {
                    secondCheckStarted.Set();
                    cancellationToken.WaitHandle.WaitOne();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return Candidate();
            },
            waitForScheduler: clock.Wait);

        service.StartAutoCheckScheduler();
        AssertEqual(true, secondCheckStarted.Wait(TimeSpan.FromSeconds(2)), "The scheduler did not run again after the successful interval.");
        var busyStatus = service.CheckForUpdates();
        AssertEqual(false, busyStatus.Ok, "A manual check entered while the automatic check owned the operation lock.");
        AssertEqual(true, busyStatus.Error?.Contains("另一项更新操作", StringComparison.Ordinal) == true, "The operation lock did not return the expected busy status.");
        AssertEqual(2, fetchCount, "The busy manual request executed another update fetch.");
        service.Dispose();
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static void VerifyDisposeRetryAfterTimeout()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-dispose-retry-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-dispose-retry-smoke");
    try
    {
        using var fetchStarted = new ManualResetEventSlim();
        using var releaseFetch = new ManualResetEventSlim();
        using var service = new UpdateService(
            Settings(),
            log,
            root,
            static () => new DateTime(2026, 7, 14, 7, 0, 0, DateTimeKind.Utc),
            _ =>
            {
                fetchStarted.Set();
                releaseFetch.Wait();
                return Candidate();
            },
            shutdownTimeout: TimeSpan.FromMilliseconds(25));

        service.StartAutoCheckScheduler();
        AssertEqual(true, fetchStarted.Wait(TimeSpan.FromSeconds(2)), "The timeout fixture did not start its scheduler check.");
        service.Dispose();
        AssertEqual(false, service.IsDisposeComplete, "A timed-out scheduler disposal reported complete cleanup.");

        releaseFetch.Set();
        if (!SpinWait.SpinUntil(
                () => service.GetStatus().State != "checking",
                TimeSpan.FromSeconds(2)))
        {
            throw new TimeoutException("The scheduler did not exit after the blocked fetch was released.");
        }
        service.Dispose();
        AssertEqual(true, service.IsDisposeComplete, "A second Dispose did not finish cleanup after the scheduler exited.");
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

// 从中断状态文件重建服务，验证未完成操作的清理与恢复规则；这不执行正式包安装事务。
static void VerifyAbruptOperationRecovery()
{
    var root = Path.Combine(Path.GetTempPath(), $"mystia-update-abrupt-recovery-{Guid.NewGuid():N}");
    Directory.CreateDirectory(root);
    var log = Logger.CreateLogSource("update-abrupt-recovery-smoke");
    try
    {
        var nowUtc = new DateTime(2026, 7, 14, 8, 0, 0, DateTimeKind.Utc);
        var idleRoot = Path.Combine(root, "idle");
        var downloadsRoot = Path.Combine(idleRoot, "downloads");
        var pendingDirectory = Path.Combine(downloadsRoot, $".1.2.4.{Guid.NewGuid():N}.tmp");
        var unrelatedDirectory = Path.Combine(downloadsRoot, "user-content");
        var invalidVersionDirectory = Path.Combine(downloadsRoot, $".not-a-version.{Guid.NewGuid():N}.tmp");
        var invalidGuidDirectory = Path.Combine(downloadsRoot, ".1.2.4.not-a-guid.tmp");
        var nestedPendingDirectory = Path.Combine(unrelatedDirectory, $".1.2.4.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(pendingDirectory);
        Directory.CreateDirectory(invalidVersionDirectory);
        Directory.CreateDirectory(invalidGuidDirectory);
        Directory.CreateDirectory(nestedPendingDirectory);
        WriteUpdateState(idleRoot, new UpdateState
        {
            State = "checking",
            LastAttemptAtUtc = nowUtc - TimeSpan.FromMinutes(1),
            NextCheckAtUtc = nowUtc + TimeSpan.FromHours(6),
            ConsecutiveFailures = 6,
            Error = "stale checking state",
        });

        var resumedFetchCount = 0;
        using (var idleService = new UpdateService(
                   Settings(),
                   log,
                   idleRoot,
                   () => nowUtc,
                   _ =>
                   {
                       Interlocked.Increment(ref resumedFetchCount);
                       return Candidate();
                   }))
        {
            var status = idleService.GetStatus();
            AssertEqual("idle", status.State, "Abrupt checking state without metadata did not recover to idle.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt checking recovery retained the old six-hour backoff.");
            AssertEqual(false, Directory.Exists(pendingDirectory), "A service-format pending download directory was not removed.");
            AssertEqual(true, Directory.Exists(unrelatedDirectory), "An unrelated downloads directory was removed.");
            AssertEqual(true, Directory.Exists(invalidVersionDirectory), "A non-version temporary directory was removed.");
            AssertEqual(true, Directory.Exists(invalidGuidDirectory), "A temporary directory without a service GUID was removed.");
            AssertEqual(true, Directory.Exists(nestedPendingDirectory), "Recovery scanned below the downloads root first level.");

            idleService.StartAutoCheckScheduler();
            if (!SpinWait.SpinUntil(
                    () => !string.IsNullOrWhiteSpace(idleService.GetStatus().LastSuccessAtUtc),
                    TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Abrupt checking recovery did not immediately run the due check.");
            }
            AssertEqual(1, resumedFetchCount, "Abrupt checking recovery ran an unexpected number of immediate checks.");
        }

        var currentRoot = Path.Combine(root, "current");
        WriteUpdateState(currentRoot, new UpdateState
        {
            ManifestSchemaVersion = 2,
            State = "checking",
            LastSuccessAtUtc = nowUtc - TimeSpan.FromHours(1),
            NextCheckAtUtc = nowUtc + TimeSpan.FromHours(24),
        });
        using (var currentService = NewRecoveryService(currentRoot, nowUtc, log))
        {
            var status = currentService.GetStatus();
            AssertEqual("current", status.State, "Abrupt checking state with a successful check did not recover to current.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt current recovery was not immediately due.");
        }

        var stableRoot = Path.Combine(root, "stable-cleanup");
        var stablePendingDirectory = Path.Combine(
            stableRoot,
            "downloads",
            $".1.2.4.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stablePendingDirectory);
        WriteUpdateState(stableRoot, new UpdateState
        {
            ManifestSchemaVersion = 2,
            State = "current",
            LastSuccessAtUtc = nowUtc - TimeSpan.FromHours(1),
            NextCheckAtUtc = nowUtc + TimeSpan.FromHours(24),
        });
        using (var stableService = NewRecoveryService(stableRoot, nowUtc, log))
        {
            var status = stableService.GetStatus();
            AssertEqual("current", status.State, "Startup cleanup changed an already stable update state.");
            AssertEqual(
                (nowUtc + TimeSpan.FromHours(24)).ToString("O"),
                status.NextCheckAtUtc,
                "Startup cleanup rescheduled an already stable update state.");
            AssertEqual(
                false,
                Directory.Exists(stablePendingDirectory),
                "A pending directory left by a normally cancelled download was not retried on startup.");
        }

        var availableRoot = Path.Combine(root, "available");
        WriteUpdateState(availableRoot, AvailableState("downloading", nowUtc));
        using (var availableService = NewRecoveryService(availableRoot, nowUtc, log))
        {
            var status = availableService.GetStatus();
            AssertEqual("available", status.State, "Abrupt downloading state with a valid candidate did not recover to available.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt available recovery was not immediately due.");
        }

        var downloadedRoot = Path.Combine(root, "downloaded");
        var stagedDirectory = Path.Combine(downloadedRoot, "staged", "plugin");
        Directory.CreateDirectory(stagedDirectory);
        var downloadedState = AvailableState("downloading", nowUtc);
        downloadedState.DownloadedVersion = "1.2.4";
        downloadedState.DownloadedManifestSchemaVersion = 2;
        downloadedState.StagedDirectory = stagedDirectory;
        WriteUpdateState(downloadedRoot, downloadedState);
        using (var downloadedService = NewRecoveryService(downloadedRoot, nowUtc, log))
        {
            var status = downloadedService.GetStatus();
            AssertEqual("downloaded", status.State, "Abrupt downloading state with a staged package did not recover to downloaded.");
            AssertEqual(nowUtc.ToString("O"), status.NextCheckAtUtc, "Abrupt downloaded recovery was not immediately due.");
        }
    }
    finally
    {
        Logger.Sources.Remove(log);
        Directory.Delete(root, recursive: true);
    }
}

static UpdateService NewRecoveryService(string root, DateTime nowUtc, ManualLogSource log)
{
    return new UpdateService(Settings(), log, root, () => nowUtc, _ => Candidate());
}

static UpdateState AvailableState(string state, DateTime nowUtc)
{
    return new UpdateState
    {
        State = state,
        ManifestSchemaVersion = 2,
        LastAttemptAtUtc = nowUtc - TimeSpan.FromMinutes(1),
        LastSuccessAtUtc = nowUtc - TimeSpan.FromHours(1),
        NextCheckAtUtc = nowUtc + TimeSpan.FromHours(24),
        LatestVersion = "1.2.4",
        LatestTag = "v1.2.4",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = new string('a', 64),
        PackageSize = 42,
        PackageDownloadUrl = "https://example.test/package.zip",
    };
}

static void WriteUpdateState(string root, UpdateState state)
{
    Directory.CreateDirectory(root);
    File.WriteAllText(
        Path.Combine(root, "update-state.json"),
        JsonSerializer.Serialize(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}

static UpdateStatus WaitForTask(Task<UpdateStatus> task, string timeoutMessage)
{
    if (!task.Wait(TimeSpan.FromSeconds(2))) throw new TimeoutException(timeoutMessage);
    return task.GetAwaiter().GetResult();
}

static UpdateServiceSettings Settings()
{
    return new UpdateServiceSettings
    {
        Enabled = true,
        AutoCheck = true,
        CheckIntervalHours = 24,
        IncludePrerelease = false,
    };
}

static UpdateCandidate Candidate(bool withCatalog = false)
{
    return new UpdateCandidate
    {
        Manifest = Manifest(version: "1.2.4", tag: "v1.2.4", withCatalog: withCatalog),
        ReleaseUrl = "https://example.test/releases/v1.2.4",
        PublishedAtUtc = new DateTime(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc),
        PackageDownloadUrl = "https://example.test/releases/v1.2.4/package.zip",
    };
}

static void ExpectDisposed(Action action)
{
    try
    {
        action();
        throw new InvalidOperationException("A disposed update service restarted its scheduler.");
    }
    catch (ObjectDisposedException)
    {
    }
}

static void ExpectInvalidDownloadSize(byte[] content, long expectedSize)
{
    using var source = new MemoryStream(content);
    using var destination = new MemoryStream();
    try
    {
        UpdateService.CopyDownloadContentAsync(source, destination, expectedSize, CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected an invalid download size error.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("大小", StringComparison.Ordinal))
    {
    }
}

static void VerifyAtomParsing()
{
    const string xml = "<?xml version=\"1.0\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">"
        + "<entry><title>v1.2.0-preview.10</title><link href=\"https://example.test/preview?a=1&amp;b=2\" rel=\"alternate\"/><updated>2026-07-01T00:00:00Z</updated></entry>"
        + "<entry><title>v1.2.0</title><link rel=\"alternate\" href=\"https://example.test/stable\"/><updated>2026-07-02T00:00:00Z</updated></entry>"
        + "<entry><title>not-a-version</title></entry></feed>";
    var releases = UpdateService.ParseReleaseFeed(xml);
    AssertEqual(2, releases.Count, "Unexpected number of parsed releases.");
    AssertEqual("v1.2.0", releases[0].TagName, "Stable release was not sorted first.");
    AssertEqual("https://example.test/preview?a=1&b=2", releases[1].HtmlUrl, "Atom link attributes/entities were not parsed structurally.");
}

static UpdateManifest Manifest(
    int schemaVersion = 2,
    string version = "1.2.3",
    string tag = "v1.2.3",
    string channel = "stable",
    string? packageSha256 = null,
    long packageSize = 42,
    bool withCatalog = false,
    string? catalogAsset = null,
    string? catalogSha256 = null,
    long? catalogSize = null)
{
    return new UpdateManifest
    {
        SchemaVersion = schemaVersion,
        Version = version,
        Tag = tag,
        Channel = channel,
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = packageSha256 ?? new string('a', 64),
        PackageSize = packageSize,
        CatalogAsset = catalogAsset ?? (withCatalog ? "update-catalog.json" : ""),
        CatalogSha256 = catalogSha256 ?? (withCatalog ? new string('b', 64) : ""),
        CatalogSize = catalogSize ?? (withCatalog ? 1024 : 0),
    };
}

static UpdateCatalog Catalog(string ownerVersion, IEnumerable<UpdateCatalogRelease> releases)
{
    return new UpdateCatalog
    {
        SchemaVersion = 1,
        GeneratedAtUtc = "2026-08-12T01:00:00Z",
        Repository = "blockshy/mystia-steward-companion",
        OwnerVersion = ownerVersion,
        OwnerTag = $"v{ownerVersion}",
        Releases = releases.ToList(),
    };
}

static UpdateCatalogRelease CatalogRelease(
    string version,
    string channel,
    string? releaseUrl = null,
    string? tag = null,
    string? notesMarkdown = null)
{
    var releaseTag = tag ?? $"v{version}";
    return new UpdateCatalogRelease
    {
        Version = version,
        Tag = releaseTag,
        Title = $"v{version}",
        Channel = channel,
        PublishedAtUtc = "2026-08-12T00:00:00Z",
        ReleaseUrl = releaseUrl ?? $"https://github.com/blockshy/mystia-steward-companion/releases/tag/v{version}",
        NotesMarkdown = notesMarkdown ?? $"## v{version}\n\n- smoke test",
    };
}

static void ExpectInvalidCatalog(
    UpdateCatalog catalog,
    UpdateManifest manifest,
    string expectedMessage)
{
    try
    {
        UpdateService.ValidateCatalog(catalog, manifest);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
        return;
    }
    throw new InvalidOperationException($"Catalog containing invalid {expectedMessage} was accepted.");
}

static void ExpectInvalidManifest(UpdateManifest manifest, string expectedMessage)
{
    try
    {
        UpdateService.ValidateManifest(manifest);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
        return;
    }
    // 断言错误必须在 catch 之外抛出，防止其自身文本被当成验证器的预期拒绝而产生假通过。
    throw new InvalidOperationException($"Manifest containing invalid {expectedMessage} was accepted.");
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

sealed class AdvancingSchedulerClock
{
    private readonly object _lock = new();
    private DateTime _utcNow;

    public AdvancingSchedulerClock(DateTime utcNow)
    {
        _utcNow = utcNow;
    }

    public DateTime UtcNow()
    {
        lock (_lock)
        {
            return _utcNow;
        }
    }

    public bool Wait(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return true;
        lock (_lock)
        {
            _utcNow += delay;
        }
        Thread.Yield();
        return cancellationToken.IsCancellationRequested;
    }
}
