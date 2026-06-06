using System.Text;
using BepInEx;
using MystiaSteward.Core;

namespace MystiaSteward.Save;

public sealed class NightBusinessDiagnosticSink
{
    private const long MaxLogBytes = 512 * 1024;
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, DateTime> LastWriteByPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _path;
    private readonly TimeSpan _minInterval;

    public NightBusinessDiagnosticSink(string path, TimeSpan minInterval)
    {
        _path = ResolvePath(path);
        _minInterval = minInterval;
    }

    public string Path => _path;

    public static string ResolvePath(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return configuredPath.Trim();
        return System.IO.Path.Combine(Paths.ConfigPath, "MystiaSteward", "night-business-diagnostics.log");
    }

    public static void Clear(string? configuredPath)
    {
        var path = ResolvePath(configuredPath);
        lock (SyncRoot)
        {
            if (File.Exists(path)) File.Delete(path);
            LastWriteByPath.Remove(path);
        }
    }

    public void Write(NightBusinessDiagnosticSnapshot snapshot)
    {
        var now = DateTime.UtcNow;
        lock (SyncRoot)
        {
            if (LastWriteByPath.TryGetValue(_path, out var lastWrite) && now - lastWrite < _minInterval) return;
            LastWriteByPath[_path] = now;

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            RotateIfNeeded();
            File.AppendAllText(_path, Format(snapshot), Encoding.UTF8);
        }
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path)) return;
        if (new FileInfo(_path).Length <= MaxLogBytes) return;

        var backupPath = _path + ".bak";
        if (File.Exists(backupPath)) File.Delete(backupPath);
        File.Move(_path, backupPath);
    }

    private static string Format(NightBusinessDiagnosticSnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("==== Mystia Steward Night Business Diagnostic ====");
        builder.AppendLine($"Utc: {snapshot.CapturedAtUtc:O}");
        builder.AppendLine($"Scene: {snapshot.SceneName}");
        builder.AppendLine($"Place: {snapshot.Place ?? ""}");
        builder.AppendLine($"PlaceLabel: {snapshot.PlaceLabel ?? ""}");
        builder.AppendLine($"Manager: {snapshot.ManagerStatus}");
        builder.AppendLine($"Queue: {snapshot.QueueStatus}");
        builder.AppendLine($"Sources: {string.Join("; ", snapshot.SourceStats)}");
        if (snapshot.Errors.Count > 0) builder.AppendLine($"Errors: {string.Join("; ", snapshot.Errors)}");

        AppendGuests(builder, "RawGuests", snapshot.RawGuests);
        AppendOrders(builder, "RawLiveOrders", snapshot.RawLiveOrders);
        AppendOrders(builder, "AcceptedRuntimeOrders", snapshot.AcceptedRuntimeOrders);
        AppendOrders(builder, "AcceptedLogOrders", snapshot.AcceptedLogOrders);
        AppendGuests(builder, "ActiveGuests", snapshot.ActiveGuests);
        AppendOrders(builder, "FinalOrders", snapshot.FinalOrders);
        builder.AppendLine();
        return builder.ToString();
    }

    private static void AppendGuests(StringBuilder builder, string title, IReadOnlyList<NightBusinessGuest> guests)
    {
        builder.AppendLine($"{title}: {guests.Count}");
        foreach (var guest in guests.Take(24))
        {
            builder.AppendLine(
                $"  - source={guest.Source}; desk={guest.DeskCode}; guestId={FormatNullable(guest.GuestId)}; guest={guest.GuestName}");
        }
    }

    private static void AppendOrders(StringBuilder builder, string title, IReadOnlyList<NightBusinessOrder> orders)
    {
        builder.AppendLine($"{title}: {orders.Count}");
        foreach (var order in orders.Take(24))
        {
            builder.AppendLine(
                $"  - source={order.Source}; desk={order.DeskCode}; guestId={FormatNullable(order.GuestId)}; guest={order.GuestName}; food={order.FoodTag}({order.FoodTagId}); beverage={order.BeverageTag}({order.BeverageTagId})");
        }
    }

    private static string FormatNullable(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "";
    }
}

public sealed class NightBusinessDiagnosticSnapshot
{
    public DateTime CapturedAtUtc { get; init; }
    public string SceneName { get; init; } = "";
    public string? Place { get; init; }
    public string? PlaceLabel { get; init; }
    public string ManagerStatus { get; init; } = "";
    public string QueueStatus { get; init; } = "";
    public IReadOnlyList<string> SourceStats { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<NightBusinessGuest> RawGuests { get; init; } = Array.Empty<NightBusinessGuest>();
    public IReadOnlyList<NightBusinessOrder> RawLiveOrders { get; init; } = Array.Empty<NightBusinessOrder>();
    public IReadOnlyList<NightBusinessOrder> AcceptedRuntimeOrders { get; init; } = Array.Empty<NightBusinessOrder>();
    public IReadOnlyList<NightBusinessOrder> AcceptedLogOrders { get; init; } = Array.Empty<NightBusinessOrder>();
    public IReadOnlyList<NightBusinessGuest> ActiveGuests { get; init; } = Array.Empty<NightBusinessGuest>();
    public IReadOnlyList<NightBusinessOrder> FinalOrders { get; init; } = Array.Empty<NightBusinessOrder>();
}
