using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using BepInEx;
using BepInEx.Logging;
using MystiaStewardCompanion.Plugin;

namespace MystiaStewardCompanion.Updates;

/// <summary>
/// Mod 自动更新编排服务，负责检查 Release、下载校验更新包、暂存文件并启动独立 updater。
/// </summary>
/// <remarks>
/// BepInEx DLL 和伴随窗口 exe 在运行中可能被游戏或窗口进程锁定，因此本服务不直接替换当前插件目录。
/// 它只完成网络与校验阶段，真正替换由独立 updater 在游戏和伴随窗口退出后执行。
/// </remarks>
internal sealed class UpdateService
{
    private const string RepoApi = "https://api.github.com/repos/blockshy/mystia-steward-companion";
    private const string RepoWeb = "https://github.com/blockshy/mystia-steward-companion";
    private const string ManifestAssetName = "update-manifest.json";
    private const string PackageAssetName = "mystia-steward-companion-bepinex.zip";
    private const string LatestManifestDownloadUrl = RepoWeb + "/releases/latest/download/" + ManifestAssetName;
    private const string LatestPackageDownloadUrl = RepoWeb + "/releases/latest/download/" + PackageAssetName;
    private const string LatestReleaseUrl = RepoWeb + "/releases/latest";
    private const string PackageRootDirectoryName = "mystia-steward-companion";
    private const string RequiredPluginDll = "MystiaStewardCompanion.BepInEx.dll";
    private const string RequiredWindowsCompanion = "companion/mystia-steward-companion.exe";
    private const string RequiredWindowsUpdater = "mystia-steward-companion-updater.exe";
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly UpdateServiceSettings _settings;
    private readonly ManualLogSource _log;
    private readonly object _lock = new();
    private readonly string _updatesRoot;
    private readonly string _statePath;
    private readonly string _installStatusPath;
    private UpdateState _state;
    private bool _autoCheckStarted;

    public UpdateService(UpdateServiceSettings settings, ManualLogSource log)
    {
        _settings = settings;
        _log = log;
        _updatesRoot = ResolveUpdatesRoot();
        _statePath = Path.Combine(_updatesRoot, "update-state.json");
        _installStatusPath = Path.Combine(_updatesRoot, "install-status.json");
        _state = LoadState();
        RefreshInstallStatus();
    }

    /// <summary>
    /// 获取当前更新状态，并同步读取 updater 写回的安装结果。
    /// </summary>
    /// <returns>适合本地 API 直接序列化给伴随窗口的更新状态。</returns>
    public UpdateStatus GetStatus()
    {
        lock (_lock)
        {
            RefreshInstallStatus();
            return BuildStatus(null);
        }
    }

    /// <summary>
    /// 按配置启动一次后台自动检查。
    /// </summary>
    /// <remarks>
    /// 自动检查只启动一个后台线程，并受上次检查时间限制。网络失败不会影响 Mod 主流程，只写入状态供 UI 展示。
    /// </remarks>
    public void StartAutoCheck()
    {
        if (!_settings.Enabled || !_settings.AutoCheck) return;
        lock (_lock)
        {
            if (_autoCheckStarted) return;
            _autoCheckStarted = true;
        }

        var thread = new Thread(() =>
        {
            try
            {
                if (ShouldSkipAutoCheck()) return;
                CheckForUpdates(force: false);
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Update auto-check failed: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "mystia-steward-companion update check",
        };
        thread.Start();
    }

    /// <summary>
    /// 检查 GitHub Release 是否存在可安装的新版本。
    /// </summary>
    /// <param name="force">为 <c>true</c> 时忽略检查间隔，通常由用户手动点击触发。</param>
    /// <returns>检查后的更新状态；网络、清单或校验信息异常时会返回失败状态而不是向 API 层抛出。</returns>
    /// <remarks>
    /// 稳定版默认读取 <c>releases/latest/download/update-manifest.json</c>，避免 GitHub REST API
    /// 未认证请求的 rate limit。只有开启预发布检查时才需要调用 Release API，因为 latest download
    /// 无法表达“包含 prerelease”的语义。
    /// </remarks>
    public UpdateStatus CheckForUpdates(bool force)
    {
        if (!_settings.Enabled)
        {
            lock (_lock)
            {
                _state.Error = "自动更新已关闭。";
                _state.State = "disabled";
                SaveState();
                return BuildStatus(_state.Error);
            }
        }

        lock (_lock)
        {
            if (!force && !IsCheckDue())
            {
                return BuildStatus(null);
            }

            _state.State = "checking";
            _state.Error = null;
            SaveState();
        }

        try
        {
            var candidate = FetchUpdateCandidate();
            var manifest = candidate.Manifest;
            if (!string.Equals(manifest.PackageAsset, PackageAssetName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"更新清单引用了未知资产：{manifest.PackageAsset}");
            }
            if (string.IsNullOrWhiteSpace(manifest.PackageSha256))
            {
                throw new InvalidOperationException("更新清单缺少 packageSha256。");
            }

            var hasUpdate = CompareVersion(manifest.Version, MystiaStewardCompanionPlugin.PluginVersion) > 0;
            lock (_lock)
            {
                _state.State = hasUpdate ? "available" : "current";
                _state.CheckedAtUtc = DateTime.UtcNow;
                _state.LatestVersion = manifest.Version;
                _state.LatestTag = manifest.Tag;
                _state.Channel = manifest.Channel;
                _state.ReleaseUrl = string.IsNullOrWhiteSpace(manifest.ReleaseUrl) ? candidate.ReleaseUrl : manifest.ReleaseUrl;
                _state.PublishedAtUtc = ParseDateOrNull(manifest.PublishedAtUtc) ?? candidate.PublishedAtUtc;
                _state.PackageAsset = manifest.PackageAsset;
                _state.PackageSha256 = manifest.PackageSha256.ToLowerInvariant();
                _state.PackageSize = manifest.PackageSize > 0 ? manifest.PackageSize : candidate.PackageSize;
                _state.PackageDownloadUrl = candidate.PackageDownloadUrl;
                _state.ManifestDownloadUrl = candidate.ManifestDownloadUrl;
                _state.Error = null;
                SaveState();
                return BuildStatus(null);
            }
        }
        catch (UpdateManifestMissingException ex)
        {
            lock (_lock)
            {
                // 线上最新版本尚未带 manifest 时不视为功能故障；这通常发生在首次引入自动更新后的过渡版本。
                _state.State = "manifestMissing";
                _state.CheckedAtUtc = DateTime.UtcNow;
                _state.Error = ex.Message;
                SaveState();
                return BuildStatus(null);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _state.State = "failed";
                _state.CheckedAtUtc = DateTime.UtcNow;
                _state.Error = FormatUpdateError(ex);
                SaveState();
                return BuildStatus(_state.Error);
            }
        }
    }

    /// <summary>
    /// 下载并校验当前已发现的新版本更新包。
    /// </summary>
    /// <returns>下载完成后的状态，包含暂存目录和安装可用性。</returns>
    /// <remarks>
    /// 下载后会执行 SHA256 校验、Zip Slip 路径检查和包结构检查。只有全部通过才把目录标记为可安装。
    /// </remarks>
    public UpdateStatus DownloadUpdate()
    {
        if (!_settings.Enabled)
        {
            return ErrorStatus("自动更新已关闭。");
        }

        UpdateState snapshot;
        lock (_lock)
        {
            snapshot = _state.Clone();
        }

        if (!HasAvailableUpdate(snapshot))
        {
            var checkedStatus = CheckForUpdates(force: true);
            lock (_lock)
            {
                snapshot = _state.Clone();
            }
            if (!HasAvailableUpdate(snapshot))
            {
                return checkedStatus;
            }
        }

        try
        {
            lock (_lock)
            {
                _state.State = "downloading";
                _state.Error = null;
                SaveState();
            }

            var version = SanitizePathSegment(snapshot.LatestVersion);
            var versionRoot = Path.Combine(_updatesRoot, "downloads", version);
            var packagePath = Path.Combine(versionRoot, PackageAssetName);
            var extractRoot = Path.Combine(versionRoot, "extract");
            if (Directory.Exists(versionRoot)) Directory.Delete(versionRoot, recursive: true);
            Directory.CreateDirectory(versionRoot);

            DownloadFile(snapshot.PackageDownloadUrl, packagePath);
            var actualHash = ComputeSha256(packagePath);
            if (!string.Equals(actualHash, snapshot.PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"更新包校验失败：期望 {snapshot.PackageSha256}，实际 {actualHash}。");
            }

            ExtractPackage(packagePath, extractRoot);
            var stagedPluginDirectory = Path.Combine(extractRoot, PackageRootDirectoryName);
            ValidatePackageDirectory(stagedPluginDirectory);

            lock (_lock)
            {
                _state.State = "downloaded";
                _state.DownloadedVersion = snapshot.LatestVersion;
                _state.DownloadedAtUtc = DateTime.UtcNow;
                _state.PackagePath = packagePath;
                _state.StagedDirectory = stagedPluginDirectory;
                _state.Error = null;
                SaveState();
                return BuildStatus(null);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _state.State = "failed";
                _state.Error = ex.Message;
                SaveState();
                return BuildStatus(ex.Message);
            }
        }
    }

    /// <summary>
    /// 启动独立 updater，并排程在游戏退出后替换插件目录。
    /// </summary>
    /// <returns>安装排程状态。</returns>
    /// <remarks>
    /// updater 会先被复制到配置目录下的 runner 子目录再启动，避免从即将被替换的插件目录运行自身。
    /// 该方法只启动进程和写入等待状态，不会关闭游戏或直接移动当前 DLL。
    /// </remarks>
    public UpdateStatus InstallOnExit()
    {
        if (!_settings.Enabled)
        {
            return ErrorStatus("自动更新已关闭。");
        }

        UpdateState snapshot;
        lock (_lock)
        {
            snapshot = _state.Clone();
        }

        if (string.IsNullOrWhiteSpace(snapshot.StagedDirectory) || !Directory.Exists(snapshot.StagedDirectory))
        {
            return ErrorStatus("尚未下载可安装的更新。");
        }

        try
        {
            ValidatePackageDirectory(snapshot.StagedDirectory);
            var pluginDirectory = ResolvePluginDirectory();
            var updaterSource = ResolveUpdaterSource(pluginDirectory, snapshot.StagedDirectory);
            var runnerDirectory = Path.Combine(_updatesRoot, "runner", DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(runnerDirectory);
            var runnerPath = Path.Combine(runnerDirectory, Path.GetFileName(updaterSource));
            File.Copy(updaterSource, runnerPath, overwrite: true);

            var backupDirectory = Path.Combine(
                _updatesRoot,
                "backups",
                $"{PackageRootDirectoryName}-{MystiaStewardCompanionPlugin.PluginVersion}-{DateTime.UtcNow:yyyyMMddHHmmss}");
            Directory.CreateDirectory(Path.GetDirectoryName(backupDirectory)!);

            var startInfo = new ProcessStartInfo
            {
                FileName = runnerPath,
                WorkingDirectory = runnerDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--game-pid");
            startInfo.ArgumentList.Add(Process.GetCurrentProcess().Id.ToString());
            startInfo.ArgumentList.Add("--plugin-dir");
            startInfo.ArgumentList.Add(pluginDirectory);
            startInfo.ArgumentList.Add("--staged-dir");
            startInfo.ArgumentList.Add(snapshot.StagedDirectory);
            startInfo.ArgumentList.Add("--backup-dir");
            startInfo.ArgumentList.Add(backupDirectory);
            startInfo.ArgumentList.Add("--status-file");
            startInfo.ArgumentList.Add(_installStatusPath);
            startInfo.ArgumentList.Add("--control-port");
            startInfo.ArgumentList.Add("32146");

            File.WriteAllText(_installStatusPath, JsonSerializer.Serialize(new UpdateInstallStatus
            {
                State = "waiting",
                Message = "等待游戏和伴随窗口退出后安装更新。",
            }, JsonOptions));

            var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException("updater 进程启动失败。");
            }

            lock (_lock)
            {
                _state.InstallState = "waiting";
                _state.InstallMessage = "等待游戏和伴随窗口退出后安装更新。";
                _state.InstallProcessId = process.Id;
                _state.Error = null;
                SaveState();
                return BuildStatus(null);
            }
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _state.InstallState = "failed";
                _state.InstallMessage = ex.Message;
                _state.Error = ex.Message;
                SaveState();
                return BuildStatus(ex.Message);
            }
        }
    }

    public string OpenReleasePage()
    {
        lock (_lock)
        {
            return _state.ReleaseUrl;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mystia-steward-companion-updater/1.0");
        return client;
    }

    private bool ShouldSkipAutoCheck()
    {
        lock (_lock)
        {
            return !IsCheckDue();
        }
    }

    private bool IsCheckDue()
    {
        if (_state.CheckedAtUtc == null) return true;
        var interval = TimeSpan.FromHours(Math.Clamp(_settings.CheckIntervalHours, 1, 168));
        return DateTime.UtcNow - _state.CheckedAtUtc.Value.ToUniversalTime() >= interval;
    }

    private UpdateCandidate FetchUpdateCandidate()
    {
        return _settings.IncludePrerelease
            ? FetchPrereleaseAwareCandidate()
            : FetchStableCandidateFromLatestAssets();
    }

    /// <summary>
    /// 获取稳定版更新候选。
    /// </summary>
    /// <remarks>
    /// 该路径直接访问 Release 固定资产下载地址，绕过 GitHub API 频率限制。下载更新包时会使用
    /// manifest 中 tag 推导出的版本固定地址，避免检查与下载之间 latest 指向发生变化。
    /// </remarks>
    private static UpdateCandidate FetchStableCandidateFromLatestAssets()
    {
        try
        {
            var manifest = DownloadManifest(LatestManifestDownloadUrl);
            return new UpdateCandidate
            {
                Manifest = manifest,
                ReleaseUrl = LatestReleaseUrl,
                PublishedAtUtc = ParseDateOrNull(manifest.PublishedAtUtc),
                PackageSize = manifest.PackageSize,
                PackageDownloadUrl = BuildVersionedAssetDownloadUrl(manifest.Tag, PackageAssetName, LatestPackageDownloadUrl),
                ManifestDownloadUrl = LatestManifestDownloadUrl,
            };
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new UpdateManifestMissingException("最新正式 Release 尚未提供 update-manifest.json。请先手动更新到支持自动更新的版本，之后即可使用内置更新。", ex);
        }
    }

    /// <summary>
    /// 获取包含 prerelease 的更新候选。
    /// </summary>
    /// <remarks>
    /// GitHub 的 latest asset 地址不会返回 prerelease，因此该路径必须调用 Release API 并从返回资产中选择
    /// manifest 与 zip。只有用户显式开启预发布检查时才走此分支。
    /// </remarks>
    private UpdateCandidate FetchPrereleaseAwareCandidate()
    {
        var release = FetchLatestReleaseFromApi();
        var manifestAsset = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, ManifestAssetName, StringComparison.Ordinal));
        var packageAsset = release.Assets.FirstOrDefault(asset => string.Equals(asset.Name, PackageAssetName, StringComparison.Ordinal));
        if (manifestAsset == null)
        {
            throw new InvalidOperationException("最新 Release 未提供 update-manifest.json，无法自动更新。");
        }
        if (packageAsset == null)
        {
            throw new InvalidOperationException("最新 Release 未提供 Mod 更新包，无法自动更新。");
        }

        return new UpdateCandidate
        {
            Manifest = DownloadManifest(manifestAsset.DownloadUrl),
            ReleaseUrl = release.HtmlUrl,
            PublishedAtUtc = release.PublishedAtUtc,
            PackageSize = packageAsset.Size,
            PackageDownloadUrl = packageAsset.DownloadUrl,
            ManifestDownloadUrl = manifestAsset.DownloadUrl,
        };
    }

    private ReleaseInfo FetchLatestReleaseFromApi()
    {
        var url = _settings.IncludePrerelease ? $"{RepoApi}/releases" : $"{RepoApi}/releases/latest";
        var json = ReadString(url);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Array
            ? ParseReleaseArray(document.RootElement)
            : ParseRelease(document.RootElement);
    }

    private ReleaseInfo ParseReleaseArray(JsonElement root)
    {
        foreach (var item in root.EnumerateArray())
        {
            if (ReadBool(item, "draft")) continue;
            if (!_settings.IncludePrerelease && ReadBool(item, "prerelease")) continue;
            return ParseRelease(item);
        }

        throw new InvalidOperationException("未找到可用 Release。");
    }

    private static ReleaseInfo ParseRelease(JsonElement root)
    {
        var assets = new List<ReleaseAssetInfo>();
        if (root.TryGetProperty("assets", out var assetsElement) && assetsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assetsElement.EnumerateArray())
            {
                assets.Add(new ReleaseAssetInfo
                {
                    Name = ReadString(asset, "name"),
                    DownloadUrl = ReadString(asset, "browser_download_url"),
                    Size = ReadLong(asset, "size"),
                });
            }
        }

        return new ReleaseInfo
        {
            TagName = ReadString(root, "tag_name"),
            HtmlUrl = ReadString(root, "html_url"),
            PublishedAtUtc = ParseDateOrNull(ReadString(root, "published_at")),
            Assets = assets,
        };
    }

    private static UpdateManifest DownloadManifest(string url)
    {
        var json = ReadString(url);
        return JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("update-manifest.json 解析失败。");
    }

    private static string ReadString(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("下载地址为空。");
        return Http.GetStringAsync(url).GetAwaiter().GetResult();
    }

    private static void DownloadFile(string url, string path)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("更新包下载地址为空。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        File.WriteAllBytes(path, bytes);
    }

    private static string FormatUpdateError(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
        {
            return "GitHub 请求达到频率限制。稳定版更新检查已改用 Release 固定下载地址；如果仍看到该提示，请稍后再试，或关闭预发布版本检查。";
        }

        return ex.InnerException is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
            ? ex.Message
            : ex.Message;
    }

    /// <summary>
    /// 根据 manifest 中的 tag 构造版本固定资产下载地址。
    /// </summary>
    /// <param name="tag">Release tag，例如 <c>v1.0.10</c>。</param>
    /// <param name="assetName">Release 资产文件名。</param>
    /// <param name="fallback">tag 缺失时使用的 latest 下载地址。</param>
    /// <returns>优先指向指定 tag 的下载 URL。</returns>
    private static string BuildVersionedAssetDownloadUrl(string tag, string assetName, string fallback)
    {
        return string.IsNullOrWhiteSpace(tag)
            ? fallback
            : $"{RepoWeb}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";
    }

    /// <summary>
    /// 将 zip 更新包解压到暂存目录，并拒绝越界路径。
    /// </summary>
    /// <param name="packagePath">已通过 SHA256 校验的 zip 文件。</param>
    /// <param name="extractRoot">解压目标根目录。</param>
    /// <exception cref="InvalidOperationException">当压缩包条目试图写出目标根目录时抛出。</exception>
    private static void ExtractPackage(string packagePath, string extractRoot)
    {
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);
        var fullRoot = EnsureDirectorySeparator(Path.GetFullPath(extractRoot));
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(extractRoot, entry.FullName));
            if (!destination.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"更新包包含非法路径：{entry.FullName}");
            }

            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    /// <summary>
    /// 校验暂存插件目录是否包含自动更新所需的最小文件集合。
    /// </summary>
    /// <remarks>
    /// 这里不尝试执行 DLL 或 exe，只做结构校验。真正替换前 updater 会再次检查目标目录，降低半包安装风险。
    /// </remarks>
    private static void ValidatePackageDirectory(string directory)
    {
        if (!Directory.Exists(directory)) throw new InvalidOperationException($"更新暂存目录不存在：{directory}");
        RequireFile(directory, RequiredPluginDll);
        RequireFile(directory, RequiredWindowsCompanion);
        RequireFile(directory, RequiredWindowsUpdater);
    }

    private static void RequireFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new InvalidOperationException($"更新包缺少文件：{relativePath}");
    }

    private static string ResolvePluginDirectory()
    {
        var directory = Path.GetDirectoryName(typeof(MystiaStewardCompanionPlugin).Assembly.Location);
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidOperationException("无法定位当前插件目录。");
        return Path.GetFullPath(directory);
    }

    private static string ResolveUpdaterSource(string pluginDirectory, string stagedDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(pluginDirectory, RequiredWindowsUpdater),
            Path.Combine(stagedDirectory, RequiredWindowsUpdater),
            Path.Combine(stagedDirectory, "mystia-steward-companion-updater"),
        };

        return candidates.FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("未找到 updater 可执行程序。");
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int CompareVersion(string left, string right)
    {
        var leftParts = ParseVersionParts(left);
        var rightParts = ParseVersionParts(right);
        for (var index = 0; index < Math.Max(leftParts.Count, rightParts.Count); index++)
        {
            var l = index < leftParts.Count ? leftParts[index] : 0;
            var r = index < rightParts.Count ? rightParts[index] : 0;
            if (l != r) return l.CompareTo(r);
        }

        return 0;
    }

    private static List<int> ParseVersionParts(string value)
    {
        var trimmed = value.Trim().TrimStart('v', 'V');
        var suffixStart = trimmed.IndexOfAny(new[] { '-', '+' });
        if (suffixStart >= 0) trimmed = trimmed[..suffixStart];
        return trimmed
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, out var number) ? number : 0)
            .ToList();
    }

    private UpdateState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new UpdateState();
            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_statePath), JsonOptions) ?? new UpdateState();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Read update state failed: {ex.Message}");
            return new UpdateState();
        }
    }

    private void SaveState()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
    }

    private void RefreshInstallStatus()
    {
        try
        {
            if (!File.Exists(_installStatusPath)) return;
            var status = JsonSerializer.Deserialize<UpdateInstallStatus>(File.ReadAllText(_installStatusPath), JsonOptions);
            if (status == null || string.IsNullOrWhiteSpace(status.State)) return;
            _state.InstallState = status.State;
            _state.InstallMessage = status.Message;
            if (string.Equals(status.State, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                _state.State = "installed";
                _state.Error = null;
            }
            else if (string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase))
            {
                _state.Error = status.Message;
            }
            SaveState();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Read update install status failed: {ex.Message}");
        }
    }

    private UpdateStatus ErrorStatus(string message)
    {
        lock (_lock)
        {
            _state.Error = message;
            SaveState();
            return BuildStatus(message);
        }
    }

    private UpdateStatus BuildStatus(string? error)
    {
        var hasUpdate = HasAvailableUpdate(_state);
        return new UpdateStatus
        {
            Ok = string.IsNullOrWhiteSpace(error),
            CurrentVersion = MystiaStewardCompanionPlugin.PluginVersion,
            Enabled = _settings.Enabled,
            AutoCheck = _settings.AutoCheck,
            IncludePrerelease = _settings.IncludePrerelease,
            State = _settings.Enabled ? _state.State : "disabled",
            LatestVersion = _state.LatestVersion,
            LatestTag = _state.LatestTag,
            HasUpdate = hasUpdate,
            CheckedAtUtc = _state.CheckedAtUtc?.ToString("O") ?? "",
            PublishedAtUtc = _state.PublishedAtUtc?.ToString("O") ?? "",
            ReleaseUrl = _state.ReleaseUrl,
            PackageAsset = _state.PackageAsset,
            PackageSize = _state.PackageSize,
            DownloadedVersion = _state.DownloadedVersion,
            DownloadedAtUtc = _state.DownloadedAtUtc?.ToString("O") ?? "",
            Staged = !string.IsNullOrWhiteSpace(_state.StagedDirectory) && Directory.Exists(_state.StagedDirectory),
            InstallState = _state.InstallState,
            InstallMessage = _state.InstallMessage,
            Error = error ?? _state.Error,
        };
    }

    private static bool HasAvailableUpdate(UpdateState state)
    {
        return CompareVersion(state.LatestVersion, MystiaStewardCompanionPlugin.PluginVersion) > 0
            && !string.IsNullOrWhiteSpace(state.PackageDownloadUrl)
            && !string.IsNullOrWhiteSpace(state.PackageSha256);
    }

    private static string ResolveUpdatesRoot()
    {
        return Path.Combine(Paths.ConfigPath, "MystiaStewardCompanion", "updates");
    }

    private static string EnsureDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? ""
            : "";
    }

    private static long ReadLong(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out var value) ? value : 0;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.True;
    }

    private static DateTime? ParseDateOrNull(string value)
    {
        return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
    }
}

internal sealed class UpdateServiceSettings
{
    public bool Enabled { get; init; }
    public bool AutoCheck { get; init; }
    public int CheckIntervalHours { get; init; }
    public bool IncludePrerelease { get; init; }
}

internal sealed class UpdateStatus
{
    public bool Ok { get; init; }
    public string CurrentVersion { get; init; } = "";
    public bool Enabled { get; init; }
    public bool AutoCheck { get; init; }
    public bool IncludePrerelease { get; init; }
    public string State { get; init; } = "";
    public string LatestVersion { get; init; } = "";
    public string LatestTag { get; init; } = "";
    public bool HasUpdate { get; init; }
    public string CheckedAtUtc { get; init; } = "";
    public string PublishedAtUtc { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
    public string PackageAsset { get; init; } = "";
    public long PackageSize { get; init; }
    public string DownloadedVersion { get; init; } = "";
    public string DownloadedAtUtc { get; init; } = "";
    public bool Staged { get; init; }
    public string InstallState { get; init; } = "";
    public string InstallMessage { get; init; } = "";
    public string? Error { get; init; }
}

internal sealed class UpdateState
{
    public string State { get; set; } = "idle";
    public DateTime? CheckedAtUtc { get; set; }
    public string LatestVersion { get; set; } = "";
    public string LatestTag { get; set; } = "";
    public string Channel { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public DateTime? PublishedAtUtc { get; set; }
    public string PackageAsset { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public long PackageSize { get; set; }
    public string PackageDownloadUrl { get; set; } = "";
    public string ManifestDownloadUrl { get; set; } = "";
    public string DownloadedVersion { get; set; } = "";
    public DateTime? DownloadedAtUtc { get; set; }
    public string PackagePath { get; set; } = "";
    public string StagedDirectory { get; set; } = "";
    public string InstallState { get; set; } = "";
    public string InstallMessage { get; set; } = "";
    public int InstallProcessId { get; set; }
    public string? Error { get; set; }

    public UpdateState Clone()
    {
        return new UpdateState
        {
            State = State,
            CheckedAtUtc = CheckedAtUtc,
            LatestVersion = LatestVersion,
            LatestTag = LatestTag,
            Channel = Channel,
            ReleaseUrl = ReleaseUrl,
            PublishedAtUtc = PublishedAtUtc,
            PackageAsset = PackageAsset,
            PackageSha256 = PackageSha256,
            PackageSize = PackageSize,
            PackageDownloadUrl = PackageDownloadUrl,
            ManifestDownloadUrl = ManifestDownloadUrl,
            DownloadedVersion = DownloadedVersion,
            DownloadedAtUtc = DownloadedAtUtc,
            PackagePath = PackagePath,
            StagedDirectory = StagedDirectory,
            InstallState = InstallState,
            InstallMessage = InstallMessage,
            InstallProcessId = InstallProcessId,
            Error = Error,
        };
    }
}

internal sealed class UpdateManifest
{
    public int SchemaVersion { get; init; }
    public string Version { get; init; } = "";
    public string Tag { get; init; } = "";
    public string Channel { get; init; } = "";
    public string PackageAsset { get; init; } = "";
    public string PackageSha256 { get; init; } = "";
    public long PackageSize { get; init; }
    public string ReleaseUrl { get; init; } = "";
    public string PublishedAtUtc { get; init; } = "";
}

internal sealed class UpdateCandidate
{
    public UpdateManifest Manifest { get; init; } = new();
    public string ReleaseUrl { get; init; } = "";
    public DateTime? PublishedAtUtc { get; init; }
    public long PackageSize { get; init; }
    public string PackageDownloadUrl { get; init; } = "";
    public string ManifestDownloadUrl { get; init; } = "";
}

internal sealed class UpdateInstallStatus
{
    public string State { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class ReleaseInfo
{
    public string TagName { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public DateTime? PublishedAtUtc { get; init; }
    public IReadOnlyList<ReleaseAssetInfo> Assets { get; init; } = Array.Empty<ReleaseAssetInfo>();
}

internal sealed class ReleaseAssetInfo
{
    public string Name { get; init; } = "";
    public string DownloadUrl { get; init; } = "";
    public long Size { get; init; }
}

internal sealed class UpdateManifestMissingException : Exception
{
    public UpdateManifestMissingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
