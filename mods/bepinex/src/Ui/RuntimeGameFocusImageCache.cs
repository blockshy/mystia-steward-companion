using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.Save;
using UnityEngine;

namespace MystiaStewardCompanion.Ui;

/// <summary>
/// 专注层独占的有界图像缓存。后台仅调用已验证的PNG读取入口，Unity纹理仅在主线程创建和销毁。
/// 每代最多128个条目、同时最多2次文件读取、每帧最多解码1张128px图片；稳定帧不读文件。
/// 资源代次失效后旧读取即使完成也只丢弃托管字节，绝不写入新代RawImage。
/// </summary>
internal sealed class RuntimeGameFocusImageCache : IDisposable
{
    private const int MaximumEntries = 128;
    private readonly Func<string, string, byte[]?> _read;
    private readonly Action<string>? _failure;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Pending> _pending = new();
    private HashSet<string> _wanted = new(StringComparer.Ordinal);
    private string _generation = "";
    private long _stamp;
    private MethodInfo? _decode;
    private bool _disposed, _reportedFailure;

    public RuntimeGameFocusImageCache(Func<string, string, byte[]?> read, Action<string>? failure)
    { _read = read; _failure = failure; }

    /// <summary>
    /// 集合只在卡片数据改变时生成。此处先过滤精确命名空间和非负整数键，
    /// 清单成员、代次与摘要仍由注入的GameMediaService读取入口验证，不按名称猜测图片。
    /// </summary>
    public void Apply(string generation, IEnumerable<string> keys)
    {
        if (_disposed) return;
        if (_generation != generation)
        {
            ReleaseEntries();
            _generation = generation;
            _reportedFailure = false;
        }
        _wanted = new(keys.Where(ValidKey).Distinct(StringComparer.Ordinal).Take(MaximumEntries), StringComparer.Ordinal);
        if (string.IsNullOrEmpty(_generation)) return;
        foreach (var key in _wanted)
        {
            if (_entries.TryGetValue(key, out var existing)) { existing.Stamp = ++_stamp; continue; }
            if (_entries.Count >= MaximumEntries)
            {
                var oldest = _entries.Values.Where(entry => !_wanted.Contains(entry.Key)).OrderBy(entry => entry.Stamp).FirstOrDefault();
                if (oldest == null) break;
                Release(oldest); _entries.Remove(oldest.Key);
            }
            _entries.Add(key, new(key, ++_stamp));
        }
        StartReads();
    }

    public Texture2D? Get(string? key) => key != null && _entries.TryGetValue(key, out var entry) ? entry.Texture : null;

    /// <summary>由Unity主线程调用；返回true仅表示一张新图片可用，不导致全卡片重测量或重新排版。</summary>
    public bool Tick()
    {
        if (_disposed || _pending.Count == 0) return false;
        var changed = false;
        for (var index = 0; index < _pending.Count; index++)
        {
            var pending = _pending[index];
            if (!pending.Task.IsCompleted) continue;
            _pending.RemoveAt(index);
            byte[]? bytes = null;
            try { bytes = pending.Task.GetAwaiter().GetResult(); }
            catch { if (pending.Generation == _generation) ReportFailure(); }
            if (pending.Generation == _generation && _entries.TryGetValue(pending.Key, out var entry) && ReferenceEquals(entry, pending.Entry))
            {
                entry.Finished = true;
                if (bytes != null)
                {
                    try { entry.Texture = Decode(bytes); changed = true; }
                    catch { ReportFailure(); }
                }
            }
            // 包括失败/旧代完成在内，每帧只消化一个作业，防止资源刷新在单帧集中解码。
            break;
        }
        StartReads();
        return changed;
    }

    private void StartReads()
    {
        if (_disposed || string.IsNullOrEmpty(_generation) || _pending.Count >= 2) return;
        foreach (var entry in _entries.Values)
        {
            if (entry.Started || entry.Finished || !_wanted.Contains(entry.Key)) continue;
            entry.Started = true;
            var generation = _generation; var key = entry.Key; var read = _read;
            // 闭包只携带托管委托、代次与键，不携带任何Unity对象，也不在线程池执行解码或回调UI。
            _pending.Add(new(generation, key, entry, Task.Run(() => read(generation, key))));
            if (_pending.Count >= 2) break;
        }
    }

    private Texture2D Decode(byte[] bytes)
    {
        // 二次检查只是解码边界保护；摘要/目录身份由GameMediaService读取入口统一验证。
        if (bytes.Length is < 45 or > 256 * 1024 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71
            || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10
            || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82)
            throw new InvalidDataException("专注图片PNG头无效");
        static int Dimension(byte[] data, int offset) => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        var width = Dimension(bytes, 16); var height = Dimension(bytes, 20);
        if (width is < 1 or > 128 || height is < 1 or > 128) throw new InvalidDataException("专注图片尺寸超限");
        if (_decode == null)
        {
            // 与既有提取器相同，只加载明确的ImageConversion模块；不额外引用或搜索游戏程序集。
            var conversion = RuntimeReflectionUtility.FindType("UnityEngine.ImageConversion")
                ?? Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", true)!;
            _decode = conversion.GetMethod("LoadImage", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(Texture2D), typeof(Il2CppStructArray<byte>), typeof(bool) }, null)
                ?? throw new MissingMethodException("精确PNG解码入口缺失");
        }
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        try
        {
            var native = new Il2CppStructArray<byte>(bytes.Length);
            for (var index = 0; index < bytes.Length; index++) native[index] = bytes[index];
            if (_decode.Invoke(null, new object[] { texture, native, true }) is not true
                || texture.width != width || texture.height != height) throw new InvalidDataException("专注图片解码失败");
            return texture;
        }
        catch { UnityEngine.Object.Destroy(texture); throw; }
    }

    private void ReportFailure()
    {
        if (_reportedFailure) return;
        _reportedFailure = true;
        _failure?.Invoke("部分专注图片读取失败，已保留名称与图片占位。");
    }
    private static bool ValidKey(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var split = key.IndexOf(':');
        if (split <= 0 || key.IndexOf(':', split + 1) >= 0) return false;
        var kind = key[..split]; var id = key[(split + 1)..];
        return kind is "food" or "beverage" or "ingredient" or "cooker" or "rare-guest" or "normal-group"
            && int.TryParse(id, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number >= 0 && id == number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
    private static void Release(Entry entry) { if (entry.Texture != null) UnityEngine.Object.Destroy(entry.Texture); }
    private void ReleaseEntries() { foreach (var entry in _entries.Values) Release(entry); _entries.Clear(); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ReleaseEntries(); _wanted.Clear();
        // 已开始的有界读取只产生托管字节；观察异步异常，避免Dispose后出现未观察任务异常。
        foreach (var pending in _pending) _ = pending.Task.ContinueWith(task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        _pending.Clear();
    }
    private sealed class Entry
    {
        public Entry(string key, long stamp) { Key = key; Stamp = stamp; }
        public readonly string Key;
        public long Stamp;
        public bool Started, Finished;
        public Texture2D? Texture;
    }
    private sealed record Pending(string Generation, string Key, Entry Entry, Task<byte[]?> Task);
}
