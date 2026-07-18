using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BepInEx;
using BepInEx.Logging;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Plugin;

namespace MystiaStewardCompanion.Updates;

/// <summary>
/// Mod 自动更新编排服务，负责检查 Release、下载校验更新包、暂存文件并启动独立 updater。
/// </summary>
/// <remarks>
/// BepInEx DLL 和伴随窗口 exe 在运行中可能被游戏或窗口进程锁定，因此本服务不直接替换当前插件目录。
/// 它只完成网络与校验阶段，真正替换由独立 updater 在游戏和伴随窗口退出后执行。
/// </remarks>
internal sealed class UpdateService : IDisposable
{
    private const string RepoWeb = "https://github.com/blockshy/mystia-steward-companion";
    private const string ManifestAssetName = "update-manifest.json";
    private const string PackageAssetName = "mystia-steward-companion-bepinex.zip";
    private const string ReleasesAtomUrl = RepoWeb + "/releases.atom";
    private const string AllReleasesUrl = RepoWeb + "/releases";
    private const string LatestManifestDownloadUrl = RepoWeb + "/releases/latest/download/" + ManifestAssetName;
    private const string LatestReleaseUrl = RepoWeb + "/releases/latest";
    private const string PackageRootDirectoryName = "mystia-steward-companion";
    private const string RequiredPluginDll = "MystiaStewardCompanion.BepInEx.dll";
    private const string RequiredWindowsCompanion = "companion/mystia-steward-companion.exe";
    private const string RequiredWindowsUpdater = "mystia-steward-companion-updater.exe";
    private const int SupportedManifestSchemaVersion = 1;
    private static readonly TimeSpan SchedulerMaximumWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan SchedulerOperationRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SchedulerStopTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PackageDownloadTimeout = TimeSpan.FromMinutes(5);
    private static readonly Regex PendingDownloadDirectoryPattern = new(
        @"^\.(?<version>.+)\.(?<operationId>[0-9a-fA-F]{32})\.tmp$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HttpClient Http = CreateHttpClient(TimeSpan.FromSeconds(12));
    private static readonly HttpClient DownloadHttp = CreateHttpClient(PackageDownloadTimeout);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly UpdateServiceSettings _settings;
    private readonly ManualLogSource _log;
    private readonly object _lock = new();
    private readonly object _operationLock = new();
    private readonly object _disposeLock = new();
    private readonly ManualResetEventSlim _operationsIdle = new(true);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly string _updatesRoot;
    private readonly string _statePath;
    private readonly string _installStatusPath;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, bool> _waitForScheduler;
    private readonly Func<CancellationToken, UpdateCandidate> _fetchUpdateCandidate;
    private readonly Action<string, string, long, CancellationToken> _downloadFile;
    private UpdateState _state;
    private Thread? _schedulerThread;
    private int _activeOperationCount;
    private bool _shutdownStarted;
    private bool _disposeCompleted;

    public UpdateService(UpdateServiceSettings settings, ManualLogSource log)
        : this(settings, log, ResolveUpdatesRoot(), static () => DateTime.UtcNow, null)
    {
    }

    internal UpdateService(
        UpdateServiceSettings settings,
        ManualLogSource log,
        string updatesRoot,
        Func<DateTime> utcNow,
        Func<CancellationToken, UpdateCandidate>? fetchUpdateCandidate,
        Func<TimeSpan, CancellationToken, bool>? waitForScheduler = null,
        Action<string, string, long, CancellationToken>? downloadFile = null,
        TimeSpan? shutdownTimeout = null)
    {
        _settings = settings;
        _log = log;
        _updatesRoot = updatesRoot;
        _statePath = Path.Combine(_updatesRoot, "update-state.json");
        _installStatusPath = Path.Combine(_updatesRoot, "install-status.json");
        _shutdownTimeout = shutdownTimeout ?? SchedulerStopTimeout;
        if (_shutdownTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        _utcNow = utcNow;
        _waitForScheduler = waitForScheduler ?? WaitForCancellation;
        _fetchUpdateCandidate = fetchUpdateCandidate ?? FetchUpdateCandidate;
        _downloadFile = downloadFile ?? DownloadFile;
        _state = LoadState();
        RefreshInstallStatus();
        RecoverStartupState();
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

    internal bool IsDisposeComplete
    {
        get
        {
            lock (_lock)
            {
                return _disposeCompleted;
            }
        }
    }

    /// <summary>
    /// 按配置启动持续运行的后台自动检查调度器。
    /// </summary>
    /// <remarks>
    /// 调度器只启动一个后台线程。成功后按配置间隔再次检查，失败后按指数退避重试；网络失败不会影响 Mod 主流程。
    /// </remarks>
    public void StartAutoCheckScheduler()
    {
        if (!_settings.Enabled || !_settings.AutoCheck) return;

        lock (_lock)
        {
            ThrowIfShuttingDown();
            if (_schedulerThread != null) return;

            EnsureNextCheckScheduled(_utcNow());
            var cancellationToken = _lifetimeCancellation.Token;
            _schedulerThread = new Thread(() => RunAutoCheckScheduler(cancellationToken))
            {
                IsBackground = true,
                Name = "mystia-steward-companion update scheduler",
            };
            try
            {
                _schedulerThread.Start();
            }
            catch
            {
                _schedulerThread = null;
                throw;
            }
        }

        _log.LogInfo("Automatic update scheduler started.");
    }

    /// <summary>
    /// 检查 GitHub Release 是否存在可安装的新版本。
    /// </summary>
    /// <returns>检查后的更新状态；网络、清单或校验信息异常时会返回失败状态而不是向 API 层抛出。</returns>
    /// <remarks>
    /// 稳定版默认读取 <c>releases/latest/download/update-manifest.json</c>，避免 GitHub REST API
    /// 未认证请求的 rate limit。开启预发布检查时读取 <c>releases.atom</c> 中的公开 tag，
    /// 再按固定资产下载地址读取 manifest，避免测试通道耗尽 GitHub REST API 限额。
    /// </remarks>
    public UpdateStatus CheckForUpdates()
    {
        var enterResult = TryEnterOperation(out var cancellationToken);
        if (enterResult == UpdateOperationEnterResult.Busy) return BusyStatus();
        if (enterResult == UpdateOperationEnterResult.ShuttingDown) return ShuttingDownStatus();
        try
        {
            return CheckForUpdatesCore(force: true, cancellationToken, "manual");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ShuttingDownStatus();
        }
        finally
        {
            ExitOperation();
        }
    }

    private UpdateStatus CheckForUpdatesCore(
        bool force,
        CancellationToken cancellationToken,
        string trigger)
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

        string previousState;
        string? previousError;
        lock (_lock)
        {
            var now = _utcNow().ToUniversalTime();
            if (!force && !IsCheckDue(now))
            {
                return BuildStatus(null);
            }

            previousState = _state.State;
            previousError = _state.Error;
            _state.State = "checking";
            _state.LastAttemptAtUtc = now;
            _state.NextCheckAtUtc = null;
            _state.Error = null;
            SaveState();
        }

        try
        {
            _log.LogInfo($"Update check started (trigger={trigger}).");
            var candidate = _fetchUpdateCandidate(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = candidate.Manifest;
            var hasUpdate = CompareVersion(manifest.Version, MystiaStewardCompanionPlugin.PluginVersion) > 0;
            lock (_lock)
            {
                var completedAtUtc = _utcNow().ToUniversalTime();
                _state.State = hasUpdate ? "available" : "current";
                _state.LastSuccessAtUtc = completedAtUtc;
                _state.ConsecutiveFailures = 0;
                _state.NextCheckAtUtc = CalculateNextCheckAtUtc(
                    completedAtUtc,
                    succeeded: true,
                    consecutiveFailures: 0,
                    checkIntervalHours: _settings.CheckIntervalHours);
                _state.LatestVersion = manifest.Version;
                _state.LatestTag = manifest.Tag;
                _state.ReleaseUrl = string.IsNullOrWhiteSpace(manifest.ReleaseUrl) ? candidate.ReleaseUrl : manifest.ReleaseUrl;
                _state.PublishedAtUtc = ParseDateOrNull(manifest.PublishedAtUtc) ?? candidate.PublishedAtUtc;
                _state.PackageAsset = manifest.PackageAsset;
                _state.PackageSha256 = manifest.PackageSha256.ToLowerInvariant();
                _state.PackageSize = manifest.PackageSize;
                _state.PackageDownloadUrl = candidate.PackageDownloadUrl;
                _state.Error = null;
                SaveState();
                _log.LogInfo(
                    $"Update check completed (trigger={trigger}, result={_state.State}, latest={manifest.Version}, next={_state.NextCheckAtUtc.Value:O}).");
                return BuildStatus(null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            RestoreStableStateAfterCancellation(
                scheduleImmediateCheck: true,
                previousState: previousState,
                previousError: previousError);
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                var completedAtUtc = _utcNow().ToUniversalTime();
                _state.State = "failed";
                _state.ConsecutiveFailures = Math.Max(0, _state.ConsecutiveFailures) + 1;
                _state.NextCheckAtUtc = CalculateNextCheckAtUtc(
                    completedAtUtc,
                    succeeded: false,
                    consecutiveFailures: _state.ConsecutiveFailures,
                    checkIntervalHours: _settings.CheckIntervalHours);
                _state.Error = FormatUpdateError(ex);
                SaveState();
                _log.LogWarning(
                    $"Update check failed (trigger={trigger}, failures={_state.ConsecutiveFailures}, next={_state.NextCheckAtUtc.Value:O}): {_state.Error}");
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
        var enterResult = TryEnterOperation(out var cancellationToken);
        if (enterResult == UpdateOperationEnterResult.Busy) return BusyStatus();
        if (enterResult == UpdateOperationEnterResult.ShuttingDown) return ShuttingDownStatus();
        try
        {
            return DownloadUpdateCore(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ShuttingDownStatus();
        }
        finally
        {
            ExitOperation();
        }
    }

    private UpdateStatus DownloadUpdateCore(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled)
        {
            return ErrorStatus("自动更新已关闭。");
        }

        UpdateState snapshot;
        lock (_lock)
        {
            RefreshInstallStatus();
            if (IsActiveInstall(_state))
            {
                return BuildStatus("更新程序正在运行，不能覆盖其暂存目录。");
            }

            snapshot = _state.Clone();
        }

        if (!HasAvailableUpdate(snapshot))
        {
            var checkedStatus = CheckForUpdatesCore(force: true, cancellationToken, "download");
            lock (_lock)
            {
                snapshot = _state.Clone();
            }
            if (!HasAvailableUpdate(snapshot))
            {
                return checkedStatus;
            }
        }

        string? pendingRoot = null;
        try
        {
            lock (_lock)
            {
                _state.State = "downloading";
                _state.Error = null;
                SaveState();
            }

            var version = SanitizePathSegment(snapshot.LatestVersion);
            var downloadsRoot = Path.Combine(_updatesRoot, "downloads");
            var versionRoot = Path.Combine(downloadsRoot, version);
            pendingRoot = Path.Combine(downloadsRoot, $".{version}.{Guid.NewGuid():N}.tmp");
            var packagePath = Path.Combine(pendingRoot, PackageAssetName);
            var extractRoot = Path.Combine(pendingRoot, "extract");
            Directory.CreateDirectory(pendingRoot);

            _downloadFile(snapshot.PackageDownloadUrl, packagePath, snapshot.PackageSize, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var actualHash = ComputeSha256(packagePath);
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(actualHash, snapshot.PackageSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"更新包校验失败：期望 {snapshot.PackageSha256}，实际 {actualHash}。");
            }

            ExtractPackage(packagePath, extractRoot, cancellationToken);
            var stagedPluginDirectory = Path.Combine(extractRoot, PackageRootDirectoryName);
            ValidatePackageDirectory(stagedPluginDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            PromoteDownloadDirectory(pendingRoot, versionRoot);
            pendingRoot = null;
            stagedPluginDirectory = Path.Combine(versionRoot, "extract", PackageRootDirectoryName);

            lock (_lock)
            {
                _state.State = "downloaded";
                _state.DownloadedVersion = snapshot.LatestVersion;
                _state.DownloadedAtUtc = DateTime.UtcNow;
                _state.StagedDirectory = stagedPluginDirectory;
                _state.Error = null;
                SaveState();
                return BuildStatus(null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryDeleteDirectory(pendingRoot);
            RestoreStableStateAfterCancellation(
                scheduleImmediateCheck: false,
                previousState: snapshot.State,
                previousError: snapshot.Error);
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteDirectory(pendingRoot);
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
    /// 该方法只启动进程和写入等待状态；Windows 下 updater 会显示独立窗口，由用户确认后再关闭游戏和替换文件。
    /// </remarks>
    public UpdateStatus InstallOnExit()
    {
        var enterResult = TryEnterOperation(out var cancellationToken);
        if (enterResult == UpdateOperationEnterResult.Busy) return BusyStatus();
        if (enterResult == UpdateOperationEnterResult.ShuttingDown) return ShuttingDownStatus();
        try
        {
            return InstallOnExitCore(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ShuttingDownStatus();
        }
        finally
        {
            ExitOperation();
        }
    }

    private UpdateStatus InstallOnExitCore(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_settings.Enabled)
        {
            return ErrorStatus("自动更新已关闭。");
        }

        UpdateState snapshot;
        lock (_lock)
        {
            RefreshInstallStatus();
            if (IsActiveInstall(_state))
            {
                return BuildStatus(null);
            }

            snapshot = _state.Clone();
        }

        if (string.IsNullOrWhiteSpace(snapshot.StagedDirectory) || !Directory.Exists(snapshot.StagedDirectory))
        {
            return ErrorStatus("尚未下载可安装的更新。");
        }

        if (CompareVersion(snapshot.DownloadedVersion, MystiaStewardCompanionPlugin.PluginVersion) <= 0)
        {
            return ErrorStatus(
                $"暂存版本 {snapshot.DownloadedVersion} 不高于当前版本 {MystiaStewardCompanionPlugin.PluginVersion}，已拒绝安装。");
        }

        var startedProcessId = 0;
        try
        {
            ValidatePackageDirectory(snapshot.StagedDirectory);
            var pluginDirectory = ResolvePluginDirectory();
            var updaterSource = ResolveUpdaterSource(snapshot.StagedDirectory);
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
            cancellationToken.ThrowIfCancellationRequested();

            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                const string waitingMessage = "已启动独立更新程序，请在弹窗中确认关闭游戏并完成安装。";
                AtomicFile.WriteAllText(_installStatusPath, JsonSerializer.Serialize(new UpdateInstallStatus
                {
                    State = "waiting",
                    Message = waitingMessage,
                    Progress = 0,
                }, JsonOptions));

                using var process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("updater 进程启动失败。");
                startedProcessId = process.Id;
                _state.InstallState = "waiting";
                _state.InstallMessage = waitingMessage;
                _state.InstallProcessId = startedProcessId;
                _state.Error = null;
                SaveState();
                return BuildStatus(null);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (startedProcessId > 0 && IsUpdaterProcessRunning(startedProcessId))
                {
                    _state.InstallState = "waiting";
                    _state.InstallMessage = "更新程序已启动，但 Mod 无法保存完整安装状态。请在更新窗口中完成或取消安装。";
                    _state.InstallProcessId = startedProcessId;
                    _state.Error = ex.Message;
                    _log.LogWarning($"Updater started but install state could not be persisted: {ex.Message}");
                    return BuildStatus(ex.Message);
                }

                _state.InstallState = "failed";
                _state.InstallMessage = ex.Message;
                _state.InstallProcessId = 0;
                _state.Error = ex.Message;
                try
                {
                    AtomicFile.WriteAllText(_installStatusPath, JsonSerializer.Serialize(new UpdateInstallStatus
                    {
                        State = "failed",
                        Message = ex.Message,
                        Progress = 0,
                    }, JsonOptions));
                }
                catch (Exception statusException)
                {
                    _log.LogWarning($"Write failed install status failed: {statusException.Message}");
                }
                SaveState();
                return BuildStatus(ex.Message);
            }
        }
    }

    /// <summary>
    /// 阻止新更新操作并取消当前检查或下载；调用方随后可等待请求处理器退出，再调用 <see cref="Dispose"/>。
    /// </summary>
    public void BeginShutdown()
    {
        var cancel = false;
        lock (_lock)
        {
            if (!_shutdownStarted)
            {
                _shutdownStarted = true;
                cancel = true;
            }
        }

        if (cancel) _lifetimeCancellation.Cancel();
    }

    public void Dispose()
    {
        lock (_disposeLock)
        {
            if (_disposeCompleted) return;
            BeginShutdown();

            Thread? thread;
            lock (_lock)
            {
                thread = _schedulerThread;
            }

            if (ReferenceEquals(thread, Thread.CurrentThread))
            {
                _log.LogWarning("Update service disposal was requested from its scheduler thread; final cleanup must be retried by the owner.");
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            if (thread != null && !thread.Join(_shutdownTimeout))
            {
                _log.LogWarning("Automatic update scheduler did not stop within the shutdown timeout; cleanup can be retried.");
                return;
            }

            var remaining = _shutdownTimeout - stopwatch.Elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            if (!_operationsIdle.Wait(remaining))
            {
                _log.LogWarning("Active update operations did not stop within the shutdown timeout; cleanup can be retried.");
                return;
            }

            lock (_lock)
            {
                _schedulerThread = null;
                _disposeCompleted = true;
            }
            _operationsIdle.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient
        {
            Timeout = timeout,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("mystia-steward-companion-updater/1.0");
        return client;
    }

    private UpdateOperationEnterResult TryEnterOperation(out CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_shutdownStarted)
            {
                cancellationToken = default;
                return UpdateOperationEnterResult.ShuttingDown;
            }
            cancellationToken = _lifetimeCancellation.Token;
        }

        if (!Monitor.TryEnter(_operationLock)) return UpdateOperationEnterResult.Busy;

        lock (_lock)
        {
            if (_shutdownStarted)
            {
                Monitor.Exit(_operationLock);
                cancellationToken = default;
                return UpdateOperationEnterResult.ShuttingDown;
            }

            _activeOperationCount += 1;
            if (_activeOperationCount == 1) _operationsIdle.Reset();
            return UpdateOperationEnterResult.Entered;
        }
    }

    private void ExitOperation()
    {
        try
        {
            lock (_lock)
            {
                _activeOperationCount -= 1;
                if (_activeOperationCount < 0)
                {
                    _activeOperationCount = 0;
                    throw new InvalidOperationException("Update operation ownership became unbalanced.");
                }
                if (_activeOperationCount == 0) _operationsIdle.Set();
            }
        }
        finally
        {
            Monitor.Exit(_operationLock);
        }
    }

    private void RunAutoCheckScheduler(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    DateTime nextCheckAtUtc;
                    var now = _utcNow().ToUniversalTime();
                    lock (_lock)
                    {
                        EnsureNextCheckScheduled(now);
                        nextCheckAtUtc = _state.NextCheckAtUtc ?? now;
                    }

                    var delay = nextCheckAtUtc - now;
                    if (delay > TimeSpan.Zero)
                    {
                        var wait = delay > SchedulerMaximumWait ? SchedulerMaximumWait : delay;
                        if (_waitForScheduler(wait, cancellationToken)) return;
                        continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();

                    var enterResult = TryEnterOperation(out var operationCancellationToken);
                    if (enterResult == UpdateOperationEnterResult.ShuttingDown) return;
                    if (enterResult == UpdateOperationEnterResult.Busy)
                    {
                        if (_waitForScheduler(SchedulerOperationRetryDelay, cancellationToken)) return;
                        continue;
                    }

                    try
                    {
                        CheckForUpdatesCore(force: false, operationCancellationToken, "automatic");
                    }
                    finally
                    {
                        ExitOperation();
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"Automatic update scheduler iteration failed: {ex.Message}");
                    if (_waitForScheduler(SchedulerOperationRetryDelay, cancellationToken)) return;
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_schedulerThread, Thread.CurrentThread))
                {
                    _schedulerThread = null;
                }
            }
            _log.LogInfo("Automatic update scheduler stopped.");
        }
    }

    private bool IsCheckDue(DateTime nowUtc)
    {
        return _state.NextCheckAtUtc == null
            || nowUtc.ToUniversalTime() >= _state.NextCheckAtUtc.Value.ToUniversalTime();
    }

    private void EnsureNextCheckScheduled(DateTime nowUtc)
    {
        if (_state.NextCheckAtUtc != null) return;

        if (_state.ConsecutiveFailures > 0 && _state.LastAttemptAtUtc != null)
        {
            _state.NextCheckAtUtc = CalculateNextCheckAtUtc(
                _state.LastAttemptAtUtc.Value,
                succeeded: false,
                consecutiveFailures: _state.ConsecutiveFailures,
                checkIntervalHours: _settings.CheckIntervalHours);
        }
        else if (_state.LastSuccessAtUtc != null)
        {
            _state.NextCheckAtUtc = CalculateNextCheckAtUtc(
                _state.LastSuccessAtUtc.Value,
                succeeded: true,
                consecutiveFailures: 0,
                checkIntervalHours: _settings.CheckIntervalHours);
        }
        else
        {
            _state.NextCheckAtUtc = nowUtc.ToUniversalTime();
        }

        SaveState();
    }

    internal static DateTime CalculateNextCheckAtUtc(
        DateTime completedAtUtc,
        bool succeeded,
        int consecutiveFailures,
        int checkIntervalHours)
    {
        var normalizedCompletedAtUtc = completedAtUtc.ToUniversalTime();
        var delay = succeeded
            ? TimeSpan.FromHours(Math.Clamp(checkIntervalHours, 1, 168))
            : CalculateFailureRetryDelay(consecutiveFailures);
        return normalizedCompletedAtUtc + delay;
    }

    internal static TimeSpan CalculateFailureRetryDelay(int consecutiveFailures)
    {
        return Math.Max(1, consecutiveFailures) switch
        {
            1 => TimeSpan.FromMinutes(15),
            2 => TimeSpan.FromMinutes(30),
            3 => TimeSpan.FromHours(1),
            4 => TimeSpan.FromHours(2),
            5 => TimeSpan.FromHours(4),
            _ => TimeSpan.FromHours(6),
        };
    }

    private static bool WaitForCancellation(TimeSpan delay, CancellationToken cancellationToken)
    {
        return cancellationToken.WaitHandle.WaitOne(delay);
    }

    private UpdateCandidate FetchUpdateCandidate(CancellationToken cancellationToken)
    {
        return _settings.IncludePrerelease
            ? FetchPrereleaseAwareCandidate(cancellationToken)
            : FetchStableCandidateFromLatestAssets(cancellationToken);
    }

    /// <summary>
    /// 获取稳定版更新候选。
    /// </summary>
    /// <remarks>
    /// 该路径直接访问 Release 固定资产下载地址，绕过 GitHub API 频率限制。下载更新包时会使用
    /// manifest 中 tag 推导出的版本固定地址，避免检查与下载之间 latest 指向发生变化。
    /// </remarks>
    private static UpdateCandidate FetchStableCandidateFromLatestAssets(CancellationToken cancellationToken)
    {
        var manifest = DownloadManifest(LatestManifestDownloadUrl, cancellationToken);
        return new UpdateCandidate
        {
            Manifest = manifest,
            ReleaseUrl = LatestReleaseUrl,
            PublishedAtUtc = ParseDateOrNull(manifest.PublishedAtUtc),
            PackageDownloadUrl = BuildVersionedAssetDownloadUrl(manifest.Tag, PackageAssetName),
        };
    }

    /// <summary>
    /// 获取包含 prerelease 的更新候选。
    /// </summary>
    /// <remarks>
    /// GitHub 的 latest asset 地址不会返回 prerelease；这里通过 releases.atom 获取公开 Release tag，
    /// 再按版本从高到低尝试读取每个 tag 下的 update-manifest.json。Atom 不走 REST API，
    /// 可以避免未认证请求触发 rate limit。
    /// </remarks>
    private static UpdateCandidate FetchPrereleaseAwareCandidate(CancellationToken cancellationToken)
    {
        var releases = FetchReleaseFeedCandidates(cancellationToken);
        foreach (var release in releases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestUrl = BuildVersionedAssetDownloadUrl(release.TagName, ManifestAssetName);
            UpdateManifest manifest;
            try
            {
                manifest = DownloadManifest(manifestUrl, cancellationToken);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            if (CompareVersion(manifest.Tag, release.TagName) != 0)
            {
                throw new InvalidOperationException(
                    $"Release Feed tag 与 update-manifest.json 不一致：{release.TagName} / {manifest.Tag}");
            }

            return new UpdateCandidate
            {
                Manifest = manifest,
                ReleaseUrl = string.IsNullOrWhiteSpace(manifest.ReleaseUrl) ? release.HtmlUrl : manifest.ReleaseUrl,
                PublishedAtUtc = ParseDateOrNull(manifest.PublishedAtUtc) ?? release.PublishedAtUtc,
                PackageDownloadUrl = BuildVersionedAssetDownloadUrl(manifest.Tag, PackageAssetName),
            };
        }

        throw new InvalidOperationException("未找到带自动更新清单的可用 Release。");
    }

    private static List<ReleaseInfo> FetchReleaseFeedCandidates(CancellationToken cancellationToken)
    {
        return ParseReleaseFeed(ReadString(ReleasesAtomUrl, cancellationToken));
    }

    internal static List<ReleaseInfo> ParseReleaseFeed(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.None);
        var atom = document.Root?.Name.Namespace
            ?? throw new InvalidOperationException("GitHub Release Feed 缺少根元素。");
        var releases = new List<(ReleaseInfo Release, SemanticVersion Version)>();
        foreach (var entry in document.Descendants(atom + "entry"))
        {
            var tag = entry.Element(atom + "title")?.Value.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(tag)) continue;
            if (!SemanticVersion.TryParse(tag, out var version)) continue;

            var href = entry.Elements(atom + "link")
                .FirstOrDefault(link => string.Equals(
                    link.Attribute("rel")?.Value,
                    "alternate",
                    StringComparison.OrdinalIgnoreCase))
                ?.Attribute("href")
                ?.Value;
            if (string.IsNullOrWhiteSpace(href))
            {
                href = $"{RepoWeb}/releases/tag/{Uri.EscapeDataString(tag)}";
            }
            var updatedAt = entry.Element(atom + "updated")?.Value ?? "";
            releases.Add((new ReleaseInfo
            {
                TagName = tag,
                HtmlUrl = href,
                PublishedAtUtc = ParseDateOrNull(updatedAt),
            }, version));
        }

        if (releases.Count == 0)
        {
            throw new InvalidOperationException("未从 GitHub Release Feed 读取到可用版本。");
        }

        return releases
            .OrderByDescending(item => item.Version)
            .Select(item => item.Release)
            .ToList();
    }

    private static UpdateManifest DownloadManifest(string url, CancellationToken cancellationToken)
    {
        var json = ReadString(url, cancellationToken);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
            ?? throw new InvalidOperationException("update-manifest.json 解析失败。");
        ValidateManifest(manifest);
        return manifest;
    }

    internal static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.SchemaVersion != SupportedManifestSchemaVersion)
        {
            throw new InvalidOperationException(
                $"update-manifest.json schemaVersion 不受支持：{manifest.SchemaVersion}，当前仅支持 {SupportedManifestSchemaVersion}。");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out var manifestVersion))
        {
            throw new InvalidOperationException($"update-manifest.json 中的版本号无效：{manifest.Version}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Tag))
        {
            throw new InvalidOperationException("update-manifest.json 缺少 tag。");
        }

        if (!SemanticVersion.TryParse(manifest.Tag, out var tagVersion))
        {
            throw new InvalidOperationException($"update-manifest.json 中的 tag 无效：{manifest.Tag}");
        }
        if (manifestVersion.CompareTo(tagVersion) != 0)
        {
            throw new InvalidOperationException($"update-manifest.json 的 version 与 tag 不一致：{manifest.Version} / {manifest.Tag}");
        }

        var expectedChannel = manifestVersion.IsPrerelease ? "preview" : "stable";
        if (!string.Equals(manifest.Channel, expectedChannel, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"update-manifest.json 的 channel 与版本不一致：期望 {expectedChannel}，实际 {manifest.Channel}。");
        }

        if (!string.Equals(manifest.PackageAsset, PackageAssetName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"更新清单引用了未知资产：{manifest.PackageAsset}");
        }

        if (string.IsNullOrWhiteSpace(manifest.PackageSha256)
            || manifest.PackageSha256.Length != 64
            || manifest.PackageSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException("update-manifest.json 的 packageSha256 必须是 64 位十六进制 SHA256。");
        }

        if (manifest.PackageSize <= 0)
        {
            throw new InvalidOperationException("update-manifest.json 的 packageSize 必须大于 0。");
        }
    }

    private static string ReadString(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("下载地址为空。");
        return Http.GetStringAsync(url, cancellationToken).GetAwaiter().GetResult();
    }

    private static void DownloadFile(
        string url,
        string path,
        long expectedSize,
        CancellationToken serviceCancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("更新包下载地址为空。");
        if (expectedSize <= 0) throw new InvalidOperationException("更新包声明大小必须大于 0。");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var timeoutCancellation = new CancellationTokenSource(PackageDownloadTimeout);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            serviceCancellationToken,
            timeoutCancellation.Token);
        DownloadFileAsync(url, path, expectedSize, cancellation.Token).GetAwaiter().GetResult();
    }

    private static async Task DownloadFileAsync(string url, string path, long expectedSize, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await DownloadHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength != expectedSize)
        {
            throw new InvalidOperationException(
                $"更新包大小不匹配：期望 {expectedSize} 字节，响应声明 {contentLength} 字节。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await CopyDownloadContentAsync(source, destination, expectedSize, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CopyDownloadContentAsync(
        Stream source,
        Stream destination,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (expectedSize <= 0) throw new ArgumentOutOfRangeException(nameof(expectedSize));

        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > expectedSize)
            {
                throw new InvalidOperationException(
                    $"更新包大小超过清单声明：期望 {expectedSize} 字节，已接收至少 {total} 字节。");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total != expectedSize)
        {
            throw new InvalidOperationException($"更新包大小不匹配：期望 {expectedSize} 字节，实际 {total} 字节。");
        }
    }

    private static string FormatUpdateError(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
        {
            return "GitHub 暂时拒绝了更新请求。请稍后再试，或点击发布页手动下载更新包。";
        }

        return ex.Message;
    }

    /// <summary>
    /// 根据 manifest 中的 tag 构造版本固定资产下载地址。
    /// </summary>
    /// <param name="tag">Release tag，例如 <c>v1.0.10</c>。</param>
    /// <param name="assetName">Release 资产文件名。</param>
    /// <returns>指向指定 tag 的下载 URL。</returns>
    private static string BuildVersionedAssetDownloadUrl(string tag, string assetName)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("Release tag is required.", nameof(tag));
        return $"{RepoWeb}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(assetName)}";
    }

    /// <summary>
    /// 将 zip 更新包解压到暂存目录，并拒绝越界路径。
    /// </summary>
    /// <param name="packagePath">已通过 SHA256 校验的 zip 文件。</param>
    /// <param name="extractRoot">解压目标根目录。</param>
    /// <exception cref="InvalidOperationException">当压缩包条目试图写出目标根目录时抛出。</exception>
    private static void ExtractPackage(
        string packagePath,
        string extractRoot,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(extractRoot)) Directory.Delete(extractRoot, recursive: true);
        Directory.CreateDirectory(extractRoot);
        var fullRoot = EnsureDirectorySeparator(Path.GetFullPath(extractRoot));
        using var archive = ZipFile.OpenRead(packagePath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static void PromoteDownloadDirectory(string pendingRoot, string versionRoot)
    {
        var backupRoot = $"{versionRoot}.{Guid.NewGuid():N}.old";
        var movedExisting = false;
        if (Directory.Exists(versionRoot))
        {
            Directory.Move(versionRoot, backupRoot);
            movedExisting = true;
        }

        try
        {
            Directory.Move(pendingRoot, versionRoot);
        }
        catch
        {
            if (movedExisting && !Directory.Exists(versionRoot) && Directory.Exists(backupRoot))
            {
                Directory.Move(backupRoot, versionRoot);
            }
            throw;
        }

        if (movedExisting) TryDeleteDirectory(backupRoot);
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Stale temporary directories do not invalidate a fully verified staged package.
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

    private static string ResolveUpdaterSource(string stagedDirectory)
    {
        var updaterPath = Path.Combine(stagedDirectory, RequiredWindowsUpdater);
        return File.Exists(updaterPath)
            ? updaterPath
            : throw new InvalidOperationException("已校验的暂存包缺少 updater 可执行程序。");
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
        var leftOk = SemanticVersion.TryParse(left, out var leftVersion);
        var rightOk = SemanticVersion.TryParse(right, out var rightVersion);
        if (!leftOk && !rightOk) return 0;
        if (!leftOk) return -1;
        if (!rightOk) return 1;
        return leftVersion.CompareTo(rightVersion);
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

    private void RecoverStartupState()
    {
        var cleanup = CleanupPendingDownloadDirectories();
        if (cleanup.Removed > 0)
        {
            _log.LogInfo($"Removed {cleanup.Removed} pending update download directories during startup.");
        }
        if (!cleanup.Completed)
        {
            _log.LogWarning("Pending update download cleanup was incomplete; matching directories will be retried on next startup.");
        }

        if (!IsTransientUpdateState(_state.State)) return;

        var interruptedState = _state.State;
        _state.State = ResolveStableState();
        _state.NextCheckAtUtc = _utcNow().ToUniversalTime();
        _state.Error = null;
        try
        {
            SaveState();
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Persist interrupted update recovery failed: {ex.Message}");
        }

        _log.LogInfo(
            $"Recovered interrupted update state (from={interruptedState}, to={_state.State}, pendingRemoved={cleanup.Removed}, next={_state.NextCheckAtUtc.Value:O}).");
    }

    private (int Removed, bool Completed) CleanupPendingDownloadDirectories()
    {
        var downloadsRoot = Path.Combine(_updatesRoot, "downloads");
        if (!Directory.Exists(downloadsRoot)) return (0, true);

        string[] directories;
        try
        {
            directories = Directory.GetDirectories(downloadsRoot, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Enumerate interrupted update downloads failed: {ex.Message}");
            return (0, false);
        }

        var removed = 0;
        var completed = true;
        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var match = PendingDownloadDirectoryPattern.Match(name);
            if (!match.Success || !SemanticVersion.TryParse(match.Groups["version"].Value, out _)) continue;

            try
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                Directory.Delete(directory, recursive: true);
                removed += 1;
            }
            catch (Exception ex)
            {
                completed = false;
                _log.LogWarning($"Delete interrupted update download '{name}' failed: {ex.Message}");
            }
        }
        return (removed, completed);
    }

    private void SaveState()
    {
        AtomicFile.WriteAllText(_statePath, JsonSerializer.Serialize(_state, JsonOptions));
    }

    private void RefreshInstallStatus()
    {
        try
        {
            if (!File.Exists(_installStatusPath)) return;
            var status = JsonSerializer.Deserialize<UpdateInstallStatus>(File.ReadAllText(_installStatusPath), JsonOptions);
            if (status == null || string.IsNullOrWhiteSpace(status.State)) return;
            var previousState = JsonSerializer.Serialize(_state, JsonOptions);
            _state.InstallState = status.State;
            _state.InstallMessage = status.Message;
            if (string.Equals(status.State, "succeeded", StringComparison.OrdinalIgnoreCase))
            {
                _state.Error = null;
                if (IsInstalledVersionRunning(_state))
                {
                    _state.State = "current";
                    _state.InstallState = "";
                    _state.InstallMessage = "";
                    _state.InstallProcessId = 0;
                    TryDeleteFile(_installStatusPath);
                }
                else
                {
                    _state.State = "installed";
                    _state.InstallMessage = string.IsNullOrWhiteSpace(status.Message)
                        ? "更新安装完成。请重新启动游戏。"
                        : status.Message;
                }
            }
            else if (string.Equals(status.State, "failed", StringComparison.OrdinalIgnoreCase))
            {
                _state.Error = status.Message;
            }
            else if (string.Equals(status.State, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                _state.Error = null;
                _state.InstallProcessId = 0;
                _state.InstallMessage = string.IsNullOrWhiteSpace(status.Message)
                    ? "安装已取消，可重新打开安装程序。"
                    : status.Message;
            }
            else if (IsInstallInProgress(status.State) && !IsUpdaterProcessRunning(_state.InstallProcessId))
            {
                _state.InstallState = "failed";
                _state.InstallMessage = "更新程序已退出但安装未完成，请重新打开安装程序。";
                _state.Error = _state.InstallMessage;
                AtomicFile.WriteAllText(_installStatusPath, JsonSerializer.Serialize(new UpdateInstallStatus
                {
                    State = _state.InstallState,
                    Message = _state.InstallMessage,
                    Progress = status.Progress,
                }, JsonOptions));
            }

            if (!string.Equals(previousState, JsonSerializer.Serialize(_state, JsonOptions), StringComparison.Ordinal))
            {
                SaveState();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning($"Read update install status failed: {ex.Message}");
        }
    }

    private static bool IsInstalledVersionRunning(UpdateState state)
    {
        var installedVersion = string.IsNullOrWhiteSpace(state.DownloadedVersion)
            ? state.LatestVersion
            : state.DownloadedVersion;
        return !string.IsNullOrWhiteSpace(installedVersion)
            && CompareVersion(MystiaStewardCompanionPlugin.PluginVersion, installedVersion) >= 0;
    }

    private static bool IsInstallInProgress(string state)
    {
        return state is
            "waiting" or
            "preparing" or
            "closing-companion" or
            "waiting-game" or
            "terminating-game" or
            "game-closed" or
            "backing-up" or
            "installing" or
            "verifying";
    }

    private static bool IsUpdaterProcessRunning(int processId)
    {
        if (processId <= 0) return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited) return false;
            return process.ProcessName.Contains(
                "mystia-steward-companion-updater",
                StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            // 进程仍可能存在但当前权限无法读取详情；保守地认为 updater 还在，避免误报失败。
            return true;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 删除状态文件失败不影响运行态展示；已清理后的 update-state 会覆盖下一次 API 状态。
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

    private UpdateStatus BusyStatus()
    {
        lock (_lock)
        {
            return BuildStatus("另一项更新操作正在进行，请等待完成后重试。");
        }
    }

    private UpdateStatus ShuttingDownStatus()
    {
        lock (_lock)
        {
            return BuildStatus("更新服务正在关闭，已取消当前操作。");
        }
    }

    private void RestoreStableStateAfterCancellation(
        bool scheduleImmediateCheck,
        string previousState,
        string? previousError)
    {
        lock (_lock)
        {
            _state.State = string.IsNullOrWhiteSpace(previousState) || IsTransientUpdateState(previousState)
                ? ResolveStableState()
                : previousState;
            if (scheduleImmediateCheck) _state.NextCheckAtUtc = _utcNow().ToUniversalTime();
            _state.Error = previousError;
            try
            {
                SaveState();
            }
            catch (Exception ex)
            {
                _log.LogWarning($"Persist cancelled update state failed: {ex.Message}");
            }
        }
    }

    private string ResolveStableState()
    {
        if (HasInstallableStagedUpdate(_state)) return "downloaded";
        if (HasAvailableUpdate(_state)) return "available";
        return _state.LastSuccessAtUtc != null ? "current" : "idle";
    }

    private static bool IsTransientUpdateState(string state)
    {
        return state is "checking" or "downloading";
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
            LastAttemptAtUtc = _state.LastAttemptAtUtc?.ToString("O") ?? "",
            LastSuccessAtUtc = _state.LastSuccessAtUtc?.ToString("O") ?? "",
            NextCheckAtUtc = _settings.Enabled && _settings.AutoCheck
                ? _state.NextCheckAtUtc?.ToString("O") ?? ""
                : "",
            ConsecutiveFailures = _state.ConsecutiveFailures,
            PublishedAtUtc = _state.PublishedAtUtc?.ToString("O") ?? "",
            ReleaseUrl = string.IsNullOrWhiteSpace(_state.ReleaseUrl) ? AllReleasesUrl : _state.ReleaseUrl,
            PackageAsset = _state.PackageAsset,
            PackageSize = _state.PackageSize,
            DownloadedVersion = _state.DownloadedVersion,
            DownloadedAtUtc = _state.DownloadedAtUtc?.ToString("O") ?? "",
            Staged = HasInstallableStagedUpdate(_state),
            InstallState = _state.InstallState,
            InstallMessage = _state.InstallMessage,
            Error = error ?? _state.Error,
        };
    }

    internal static bool HasAvailableUpdate(UpdateState state)
    {
        if (CompareVersion(state.LatestVersion, MystiaStewardCompanionPlugin.PluginVersion) <= 0
            || string.IsNullOrWhiteSpace(state.PackageDownloadUrl)
            || !SemanticVersion.TryParse(state.LatestVersion, out var version))
        {
            return false;
        }

        try
        {
            ValidateManifest(new UpdateManifest
            {
                SchemaVersion = SupportedManifestSchemaVersion,
                Version = state.LatestVersion,
                Tag = state.LatestTag,
                Channel = version.IsPrerelease ? "preview" : "stable",
                PackageAsset = state.PackageAsset,
                PackageSha256 = state.PackageSha256,
                PackageSize = state.PackageSize,
            });
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasInstallableStagedUpdate(UpdateState state)
    {
        return CompareVersion(state.DownloadedVersion, MystiaStewardCompanionPlugin.PluginVersion) > 0
            && !string.IsNullOrWhiteSpace(state.StagedDirectory)
            && Directory.Exists(state.StagedDirectory);
    }

    private static bool IsActiveInstall(UpdateState state)
    {
        return IsInstallInProgress(state.InstallState)
            && IsUpdaterProcessRunning(state.InstallProcessId);
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

    private static DateTime? ParseDateOrNull(string value)
    {
        return DateTime.TryParse(value, out var parsed) ? parsed.ToUniversalTime() : null;
    }

    private void ThrowIfShuttingDown()
    {
        if (_shutdownStarted) throw new ObjectDisposedException(nameof(UpdateService));
    }
}

internal enum UpdateOperationEnterResult
{
    Entered,
    Busy,
    ShuttingDown,
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
    public string LastAttemptAtUtc { get; init; } = "";
    public string LastSuccessAtUtc { get; init; } = "";
    public string NextCheckAtUtc { get; init; } = "";
    public int ConsecutiveFailures { get; init; }
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
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    public DateTime? NextCheckAtUtc { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string LatestVersion { get; set; } = "";
    public string LatestTag { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public DateTime? PublishedAtUtc { get; set; }
    public string PackageAsset { get; set; } = "";
    public string PackageSha256 { get; set; } = "";
    public long PackageSize { get; set; }
    public string PackageDownloadUrl { get; set; } = "";
    public string DownloadedVersion { get; set; } = "";
    public DateTime? DownloadedAtUtc { get; set; }
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
            LastAttemptAtUtc = LastAttemptAtUtc,
            LastSuccessAtUtc = LastSuccessAtUtc,
            NextCheckAtUtc = NextCheckAtUtc,
            ConsecutiveFailures = ConsecutiveFailures,
            LatestVersion = LatestVersion,
            LatestTag = LatestTag,
            ReleaseUrl = ReleaseUrl,
            PublishedAtUtc = PublishedAtUtc,
            PackageAsset = PackageAsset,
            PackageSha256 = PackageSha256,
            PackageSize = PackageSize,
            PackageDownloadUrl = PackageDownloadUrl,
            DownloadedVersion = DownloadedVersion,
            DownloadedAtUtc = DownloadedAtUtc,
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
    public string PackageDownloadUrl { get; init; } = "";
}

internal sealed class UpdateInstallStatus
{
    public string State { get; init; } = "";
    public string Message { get; init; } = "";
    public int Progress { get; init; }
}

internal sealed class ReleaseInfo
{
    public string TagName { get; init; } = "";
    public string HtmlUrl { get; init; } = "";
    public DateTime? PublishedAtUtc { get; init; }
}

internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Pattern = new(
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IReadOnlyList<PrereleaseIdentifier> _prerelease;

    private SemanticVersion(int major, int minor, int patch, IReadOnlyList<PrereleaseIdentifier> prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        _prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public bool IsPrerelease => _prerelease.Count > 0;

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Pattern.Match(value.Trim());
        if (!match.Success) return false;
        if (!int.TryParse(match.Groups["major"].Value, out var major)) return false;
        if (!int.TryParse(match.Groups["minor"].Value, out var minor)) return false;
        if (!int.TryParse(match.Groups["patch"].Value, out var patch)) return false;

        var prerelease = new List<PrereleaseIdentifier>();
        var pre = match.Groups["pre"].Value;
        if (!string.IsNullOrWhiteSpace(pre))
        {
            foreach (var part in pre.Split('.'))
            {
                if (string.IsNullOrWhiteSpace(part)) return false;
                prerelease.Add(PrereleaseIdentifier.Parse(part));
            }
        }

        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other == null) return 1;
        var core = Major.CompareTo(other.Major);
        if (core != 0) return core;
        core = Minor.CompareTo(other.Minor);
        if (core != 0) return core;
        core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;

        if (_prerelease.Count == 0 && other._prerelease.Count == 0) return 0;
        if (_prerelease.Count == 0) return 1;
        if (other._prerelease.Count == 0) return -1;

        for (var index = 0; index < Math.Min(_prerelease.Count, other._prerelease.Count); index++)
        {
            var diff = _prerelease[index].CompareTo(other._prerelease[index]);
            if (diff != 0) return diff;
        }

        return _prerelease.Count.CompareTo(other._prerelease.Count);
    }
}

internal readonly struct PrereleaseIdentifier : IComparable<PrereleaseIdentifier>
{
    private PrereleaseIdentifier(string text, long? number)
    {
        Text = text;
        Number = number;
    }

    private string Text { get; }
    private long? Number { get; }

    public static PrereleaseIdentifier Parse(string value)
    {
        return long.TryParse(value, out var number)
            ? new PrereleaseIdentifier(value, number)
            : new PrereleaseIdentifier(value, null);
    }

    public int CompareTo(PrereleaseIdentifier other)
    {
        if (Number.HasValue && other.Number.HasValue) return Number.Value.CompareTo(other.Number.Value);
        if (Number.HasValue) return -1;
        if (other.Number.HasValue) return 1;
        return string.Compare(Text, other.Text, StringComparison.Ordinal);
    }
}
