using System.IO.Pipes;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace MystiaStewardCompanion.FocusProbe;

[BepInPlugin("com.tyukki.mystia-steward-companion.focus-probe", "mystia-steward-companion-focus-probe", "0.0.1")]
public sealed class FocusProbePlugin : BasePlugin
{
    public override void Load()
    {
        // Reject a misplaced plugin before registering any Unity component.
        var context = Cooperator.ValidateContext();
        var heartbeat = new HeartbeatState(NativeMethods.CurrentThreadId(), Stopwatch.Frequency);
        try
        {
            FocusHeartbeatBehaviour.Configure(heartbeat);
            // This is the only Unity operation: registration remains on BepInEx's Load thread.
            // Discard the returned wrapper; the worker receives only the managed heartbeat state.
            AddComponent<FocusHeartbeatBehaviour>();
        }
        catch (Exception ex) { heartbeat.Stop("Heartbeat component registration failed: " + ex.Message); }
        _ = Task.Run(async () =>
        {
            try { await Cooperator.RunAsync(context, heartbeat); }
            catch (Exception ex) { Log.LogError("P0 foreground cooperator stopped: " + ex); }
        });
    }
}

internal sealed record CooperatorContext(string RunId, string RunPath, string GitSha, string GamePath, string ProbePath);

internal static class Cooperator
{
    private const string PluginRelative = @"workspace\game\BepInEx\plugins\mystia-steward-companion-focus-probe\MystiaStewardCompanion.FocusProbe.dll";
    internal static CooperatorContext ValidateContext()
    {
        Guard.That(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "This isolated probe requires Windows x64.");
        var assembly = typeof(FocusProbePlugin).Assembly;
        var sha = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == "ProbeGitSha").Value ?? "";
        Guard.That(Regex.IsMatch(sha, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant), "Compiled probe SHA is invalid.");
        var pluginPath = Path.GetFullPath(assembly.Location);
        var match = Regex.Match(pluginPath, @"^D:\\dev\\mystia-node\\runs\\([A-Za-z0-9][A-Za-z0-9_-]{0,79})\\" + Regex.Escape(PluginRelative) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Guard.That(match.Success, "Cooperator is outside an isolated node run; no file will be written.");
        var runId = match.Groups[1].Value;
        var run = @"D:\dev\mystia-node\runs\" + runId;
        PlainPath(pluginPath, false); PlainPath(run, true);
        var gamePath = Path.Combine(run, "workspace", "game", "Touhou Mystia Izakaya.exe");
        var probePath = Path.Combine(run, "payload", "mystia-steward-companion-window-probe.exe");
        using var own = NativeMethods.OpenRetained(checked((ulong)Environment.ProcessId));
        Guard.That(string.Equals(NativeMethods.ObserveProcess(own).ImagePath, gamePath, StringComparison.OrdinalIgnoreCase), "Plugin process is not the exact copied game.");
        return new CooperatorContext(runId, run, sha, gamePath, probePath);
    }

    internal static async Task RunAsync(CooperatorContext context, HeartbeatState heartbeat)
    {
        var (runId, run, sha, gamePath, probePath) = context;
        using var own = NativeMethods.OpenRetained(checked((ulong)Environment.ProcessId));
        Guard.That(string.Equals(NativeMethods.ObserveProcess(own).ImagePath, gamePath, StringComparison.OrdinalIgnoreCase), "Worker process is not the exact copied game.");
        var evidence = new GameEvidence { RunId = runId, GitSha = sha, GamePid = checked((uint)Environment.ProcessId),
            HeartbeatThreadId = heartbeat.ThreadId, HeartbeatFrequency = heartbeat.Frequency };
        Exception? failure = null;
        try
        {
            PlainPath(Path.Combine(run, "foreground-session.json"), false);
            using var descriptorFile = new FileStream(Path.Combine(run, "foreground-session.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
            Guard.That(descriptorFile.Length is > 0 and <= 8192, "Descriptor byte count is invalid.");
            var descriptorBytes = new byte[checked((int)descriptorFile.Length)];
            await GrantFrame.ReadExactlyAsync(descriptorFile, descriptorBytes, CancellationToken.None, false);
            evidence.DescriptorSha256 = Convert.ToHexString(SHA256.HashData(descriptorBytes)).ToLowerInvariant();
            var descriptor = SessionDescriptor.Parse(descriptorBytes, runId, sha);
            evidence.GameCreationHex = descriptor.GameCreation.ToString("x"); evidence.ProbePid = checked((uint)descriptor.ProbePid);
            evidence.ProbeCreationHex = descriptor.ProbeCreation.ToString("x");
            PlainPath(probePath, false);
            using var probeFile = new FileStream(probePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hasher = SHA256.Create();
            evidence.ProbeExecutableSha256 = Convert.ToHexString(hasher.ComputeHash(probeFile)).ToLowerInvariant();
            Guard.That(evidence.ProbeExecutableSha256 == descriptor.ProbeExeSha256, "Flutter executable hash differs from the pinned descriptor.");
            using var peer = NativeMethods.OpenRetained(descriptor.ProbePid);
            using var pipeHandle = NativeMethods.OpenPipe(descriptor.PipeName);
            using var pipe = new NamedPipeClientStream(PipeDirection.InOut, true, true, pipeHandle);
            evidence.PipeServerPid = NativeMethods.PipeServerPid(pipeHandle);
            var session = new GrantSession(descriptor, gamePath, probePath);
            GrantObservation Observe(bool includeGameWindow)
            {
                var game = NativeMethods.ObserveProcess(own); var probe = NativeMethods.ObserveProcess(peer);
                var peerPid = NativeMethods.PipeServerPid(pipeHandle);
                var probeWindow = NativeMethods.ProbeWindow(descriptor);
                var windows = includeGameWindow ? NativeMethods.GameWindows(descriptor.GamePid) : Array.Empty<WindowObservation>();
                var gameWindow = windows.Count == 1 ? windows[0] : new WindowObservation(0, 0, 0);
                return new GrantObservation(game, probe, peerPid, gameWindow, windows.Count, probeWindow, NativeMethods.Foreground());
            }
            session.ValidateProcesses(Observe(false));
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var readyHeartbeat = await heartbeat.WaitForAdvanceAsync(
                new HeartbeatSnapshot(0, 0, heartbeat.ThreadId, null), TimeSpan.FromSeconds(100), timeout.Token);
            var readyObservation = Observe(true); session.ValidateProcesses(readyObservation);
            Guard.That(readyObservation.GameWindowCount == 1, "First Unity heartbeat has no unique game window.");
            heartbeat.RequireCurrent(readyHeartbeat, checked((uint)readyObservation.GameWindow.ThreadId));
            evidence.ReadyHeartbeatSequence = readyHeartbeat.Sequence; evidence.ReadyHeartbeatTicks = readyHeartbeat.Ticks;
            var ready = GrantFrame.Ready(descriptor).Encode();
            await pipe.WriteAsync(ready.AsMemory(), timeout.Token); await pipe.FlushAsync(timeout.Token);
            evidence.ReadySent = true;
            while (true)
            {
                var bytes = new byte[GrantFrame.ByteLength];
                if (!await GrantFrame.ReadExactlyAsync(pipe, bytes, timeout.Token, true))
                {
                    evidence.PipeEof = true; session.Complete();
                    var baseline = heartbeat.Snapshot;
                    evidence.EofBaselineSequence = baseline.Sequence; evidence.EofBaselineTicks = baseline.Ticks;
                    var advanced = await heartbeat.WaitForAdvanceAsync(baseline, TimeSpan.FromSeconds(3), timeout.Token);
                    var windows = NativeMethods.GameWindows(descriptor.GamePid);
                    Guard.That(windows.Count == 1, "EOF heartbeat has no unique game window.");
                    heartbeat.RequireCurrent(advanced, checked((uint)windows[0].ThreadId));
                    evidence.EofHeartbeatSequence = advanced.Sequence; evidence.EofHeartbeatTicks = advanced.Ticks;
                    break;
                }
                var item = new RequestEvidence { RequestFrameHex = Convert.ToHexString(bytes).ToLowerInvariant() };
                evidence.Requests.Add(item);
                Guard.That(evidence.Requests.Count <= 4, "Too many request frames.");
                try
                {
                    var request = GrantFrame.Decode(bytes);
                    var baseline = heartbeat.Snapshot;
                    item.BaselineSequence = baseline.Sequence; item.BaselineTicks = baseline.Ticks;
                    var advanced = await heartbeat.WaitForAdvanceAsync(baseline, TimeSpan.FromSeconds(3), timeout.Token);
                    item.HeartbeatSequence = advanced.Sequence; item.HeartbeatTicks = advanced.Ticks;
                    var before = Observe(true);
                    heartbeat.RequireCurrent(advanced, checked((uint)before.GameWindow.ThreadId));
                    var reply = session.Grant(request, before, pid =>
                    {
                        // Re-read process/pipe identity and exact foreground immediately before the sole ASFW call.
                        var current = Observe(true); session.ValidateProcesses(current);
                        Guard.That(current.GameWindowCount == 1 && current.GameWindow == before.GameWindow && current.Foreground == before.Foreground, "Game foreground/window changed before ASFW; grant refused.");
                        heartbeat.RequireCurrent(advanced, checked((uint)current.GameWindow.ThreadId));
                        return NativeMethods.Allow(pid);
                    }, NativeMethods.Foreground);
                    item.ReplyFrameHex = Convert.ToHexString(reply.Encode()).ToLowerInvariant();
                    evidence.SuccessfulGrants = checked((int)session.SuccessfulGrants);
                    await pipe.WriteAsync(reply.Encode().AsMemory(), timeout.Token); await pipe.FlushAsync(timeout.Token);
                    Guard.That(!session.IsFailed, "ASFW failed or the foreground changed during the grant; no request will be replayed.");
                }
                catch (Exception ex) { item.Error = ex.Message; throw; }
            }
            evidence.Outcome = "PASS";
        }
        catch (Exception ex) { failure = ex; evidence.Error = ex.ToString(); }
        finally
        {
            evidence.FinishedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            PlainPath(run, true);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            Guard.That(bytes.Length <= 65536, "Game evidence exceeded the fixed bound.");
            var stagingPath = Path.Combine(run, "game-foreground-evidence.pending.json");
            using (var output = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Flush(true); }
            // No overwrite: final readers only see a complete, durably flushed JSON file.
            File.Move(stagingPath, Path.Combine(run, "game-foreground-evidence.json"));
        }
        if (failure != null) throw new InvalidOperationException("Foreground cooperator failed; immutable evidence was written.", failure);
    }

    private static void PlainPath(string path, bool directory)
    {
        var full = Path.GetFullPath(path);
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            Guard.That((attributes & FileAttributes.ReparsePoint) == 0, "Probe path contains a reparse point.");
            if (current == full) Guard.That(((attributes & FileAttributes.Directory) != 0) == directory, "Probe path type differs.");
        }
    }
    private sealed class GameEvidence
    {
        public int SchemaVersion { get; } = 2;
        public string Kind { get; } = "mystia-game-foreground-evidence";
        public string RunId { get; init; } = "";
        public string GitSha { get; init; } = "";
        public string Outcome { get; set; } = "FAIL";
        public uint GamePid { get; init; }
        public string GameCreationHex { get; set; } = "";
        public uint ProbePid { get; set; }
        public string ProbeCreationHex { get; set; } = "";
        public string ProbeExecutableSha256 { get; set; } = "";
        public string DescriptorSha256 { get; set; } = "";
        public uint PipeServerPid { get; set; }
        public bool ReadySent { get; set; }
        public bool PipeEof { get; set; }
        public int SuccessfulGrants { get; set; }
        public string StartedAtUtc { get; } = DateTimeOffset.UtcNow.ToString("O");
        public string FinishedAtUtc { get; set; } = "";
        public List<RequestEvidence> Requests { get; } = new();
        public string? Error { get; set; }
        public uint HeartbeatThreadId { get; init; }
        public long HeartbeatFrequency { get; init; }
        public long ReadyHeartbeatSequence { get; set; }
        public long ReadyHeartbeatTicks { get; set; }
        public long EofBaselineSequence { get; set; }
        public long EofBaselineTicks { get; set; }
        public long EofHeartbeatSequence { get; set; }
        public long EofHeartbeatTicks { get; set; }
    }
    private sealed class RequestEvidence
    {
        public string RequestFrameHex { get; init; } = "";
        public string? ReplyFrameHex { get; set; }
        public string? Error { get; set; }
        public long BaselineSequence { get; set; }
        public long BaselineTicks { get; set; }
        public long HeartbeatSequence { get; set; }
        public long HeartbeatTicks { get; set; }
    }
}
