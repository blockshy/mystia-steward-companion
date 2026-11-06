using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MystiaStewardCompanion.Save;

/// <summary>
/// 为厨具背景提供“白色 RGB、保留原始 alpha”的小型裁剪遮罩。
/// 使用项目图片提取已经采用的显式 Sprite 绘制流程，避免依赖 Graphics.Blit 的隐式复制材质。
/// 所有资源操作限定在游戏主线程；成功缓存只持有自有纹理，失败缓存只持有有界纯值和诊断文本。
/// </summary>
internal static class RuntimeCookerAlphaMask
{
    private const int MaximumSourceEdge = 2048;
    private const int MaximumCropEdge = 256;
    private const int MaximumEntries = 16;
    private const string TextureName = "MystiaStewardCompanion.CookerAlphaMask";
    private static readonly Dictionary<MaskKey, MaskEntry> Entries = new();
    private static readonly Dictionary<MaskKey, string> Failures = new();
    private static long _failureSession = long.MinValue;

    /// <summary>
    /// 渲染增强入口：同一经营会话中的失败图集区域只尝试一次，后续直接返回诊断供调用者保留原图显示。
    /// 失败记录达到上限后也不淘汰旧记录，避免新旧区域反复轮换导致 GPU 读回重试；下一会话自动清空。
    /// 线程验证在异常捕获外，后台调用必须被明确拒绝，不能伪装成允许降级的普通纹理问题。
    /// </summary>
    internal static bool TryAcquireCached(Texture2D source, Vector2 minimumUv, Vector2 maximumUv,
        out Lease? lease, out string failure)
    {
        EnsureMainThread();
        lease = null;
        failure = "";
        // 源身份或 UV 非法属于绑定失败，不能降级为原图绘制；只缓存后续 GPU 增强创建的失败。
        var key = ReadKey(source, minimumUv, maximumUv);
        var session = RuntimeNightBusinessLifecycle.Snapshot.Generation;
        if (_failureSession != session)
        {
            Failures.Clear();
            _failureSession = session;
        }
        if (Failures.TryGetValue(key, out var cachedFailure))
        {
            failure = "cached failure: " + cachedFailure;
            return false;
        }
        // 已成功缓存的区域仍可共享；上限只阻止需要重新创建遮罩的未知区域。
        if (Failures.Count >= MaximumEntries
            && (!Entries.TryGetValue(key, out var existing) || existing.Texture == null))
        {
            failure = "cached failure capacity reached for current session; " + Describe(key);
            return false;
        }
        try
        {
            lease = AcquireCore(source, key);
            return true;
        }
        catch (Exception exception)
        {
            // 保留带阶段、图集尺寸和裁剪范围的外层消息，不能取 GetBaseException 后丢掉关键定位信息。
            failure = LimitFailure(exception.Message);
            if (Failures.Count < MaximumEntries) Failures.Add(key, failure);
            return false;
        }
    }

    /// <summary>
    /// 直接获取不使用失败缓存，供明确重试与隔离测试使用；创建失败仍正常抛出异常。
    /// 正常厨具刷新使用 TryAcquireCached，避免在纹理不可读取期间每次扫描重复执行 GPU 操作。
    /// </summary>
    internal static Lease Acquire(Texture2D source, Vector2 minimumUv, Vector2 maximumUv)
    {
        EnsureMainThread();
        return AcquireCore(source, ReadKey(source, minimumUv, maximumUv));
    }

    /// <summary>两个入口共享完全相同的 UV 校验及整数裁剪规则，不用名称、原始 Sprite rect 或固定坐标猜测。</summary>
    private static MaskKey ReadKey(Texture2D source, Vector2 minimumUv, Vector2 maximumUv)
    {
        if (source == null || source.Pointer == IntPtr.Zero)
            throw new InvalidOperationException("cooker alpha mask source unavailable");
        var width = source.width;
        var height = source.height;
        if (width is < 1 or > MaximumSourceEdge || height is < 1 or > MaximumSourceEdge)
            throw new InvalidOperationException("cooker alpha mask source exceeds bounded dimensions");
        if (!Finite(minimumUv) || !Finite(maximumUv)
            || minimumUv.x < 0 || minimumUv.y < 0 || maximumUv.x > 1 || maximumUv.y > 1
            || minimumUv.x >= maximumUv.x || minimumUv.y >= maximumUv.y)
            throw new InvalidOperationException("cooker alpha mask UV bounds invalid");
        // double 向外取整完整保留原图集像素范围，最终 UV 仅按该矩形平移和缩放。
        var left = (int)Math.Floor((double)minimumUv.x * width);
        var bottom = (int)Math.Floor((double)minimumUv.y * height);
        var right = (int)Math.Ceiling((double)maximumUv.x * width);
        var top = (int)Math.Ceiling((double)maximumUv.y * height);
        var cropWidth = right - left;
        var cropHeight = top - bottom;
        if (left < 0 || bottom < 0 || right > width || top > height
            || cropWidth is < 1 or > MaximumCropEdge || cropHeight is < 1 or > MaximumCropEdge)
            throw new InvalidOperationException("cooker alpha mask crop exceeds bounded dimensions");
        return new MaskKey(source.Pointer, width, height, left, bottom, cropWidth, cropHeight);
    }

    private static Lease AcquireCore(Texture2D source, MaskKey key)
    {
        if (Entries.TryGetValue(key, out var cached))
        {
            if (cached.Texture != null)
            {
                cached.References = checked(cached.References + 1);
                return new Lease(cached);
            }
            // 旧租约按条目对象身份释放，不会误删在相同键下重新生成的新缓存。
            Entries.Remove(key);
        }
        if (Entries.Count >= MaximumEntries)
            throw new InvalidOperationException("cooker alpha mask cache capacity exceeded; " + Describe(key));
        var texture = CreateMask(source, key);
        try
        {
            var entry = new MaskEntry(key, texture);
            var lease = new Lease(entry);
            Entries.Add(key, entry);
            return lease;
        }
        catch
        {
            UnityEngine.Object.Destroy(texture);
            throw;
        }
    }

    /// <summary>
    /// 将精确图集 UV 矩形显式绘制到同尺寸的小型浮点目标，不复制或读回完整图集。
    /// Sprites/Default 在透明目标上的预乘混合保留源 alpha；本方法只取 alpha，RGB 统一写白，
    /// 因而无需反预乘 RGB 或推测颜色空间，也不手工猜测 D3D 纹理翻转。
    /// </summary>
    private static Texture2D CreateMask(Texture2D source, MaskKey key)
    {
        Material? material = null;
        RenderTexture? target = null;
        Texture2D? readback = null;
        Texture2D? mask = null;
        Exception? failure = null;
        var previousTarget = RenderTexture.active;
        var previousSrgb = GL.sRGBWrite;
        var matrixPushed = false;
        var drawing = false;
        var stage = "shader";
        try
        {
            var shader = Shader.Find("Sprites/Default");
            if (shader == null || !shader.isSupported
                || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
                throw new InvalidOperationException("explicit sprite extraction is unsupported");
            stage = "material";
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetColor("_Color", Color.white);
            material.SetColor("_RendererColor", Color.white);
            material.SetVector("_Flip", new Vector4(1, 1, 1, 1));
            material.mainTexture = source;
            stage = "render-target";
            target = RenderTexture.GetTemporary(key.Width, key.Height, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            if (target == null) throw new InvalidOperationException("render target unavailable");
            target.filterMode = FilterMode.Point;
            target.wrapMode = TextureWrapMode.Clamp;
            RenderTexture.active = target;
            GL.sRGBWrite = false;
            stage = "clear";
            GL.Clear(true, true, Color.clear);
            stage = "push-matrix";
            GL.PushMatrix();
            matrixPushed = true;
            stage = "pixel-matrix";
            GL.LoadPixelMatrix(0, key.Width, 0, key.Height);
            stage = "set-pass";
            if (!material.SetPass(0)) throw new InvalidOperationException("sprite material pass unavailable");
            stage = "begin";
            GL.Begin(GL.TRIANGLES);
            drawing = true;
            stage = "draw";
            GL.Color(Color.white);
            var left = key.Left / (float)key.SourceWidth;
            var right = (key.Left + key.Width) / (float)key.SourceWidth;
            var bottom = key.Bottom / (float)key.SourceHeight;
            var top = (key.Bottom + key.Height) / (float)key.SourceHeight;
            // 与现有图片提取器相同，屏幕坐标和 ReadPixels 均从左下角起算。
            // 两个三角形恰好覆盖裁剪矩形，精灵的透明轮廓仍来自采样 alpha，最终世界网格保持原形状。
            Emit(0, 0, left, bottom);
            Emit(key.Width, 0, right, bottom);
            Emit(key.Width, key.Height, right, top);
            Emit(0, 0, left, bottom);
            Emit(key.Width, key.Height, right, top);
            Emit(0, key.Height, left, top);
            stage = "end";
            GL.End();
            drawing = false;
            stage = "readback-create";
            readback = new Texture2D(key.Width, key.Height, TextureFormat.RGBAFloat, false, true)
                { hideFlags = HideFlags.HideAndDontSave };
            stage = "read-pixels";
            readback.ReadPixels(new Rect(0, 0, key.Width, key.Height), 0, 0, false);
            stage = "get-pixels";
            var samples = readback.GetPixels();
            if (samples.Length != key.Width * key.Height)
                throw new InvalidOperationException("pixel count mismatch");
            var pixels = new Il2CppStructArray<Color32>(samples.Length);
            var hasVisibleAlpha = false;
            stage = "validate-alpha";
            for (var index = 0; index < samples.Length; index++)
            {
                var sourceAlpha = samples[index].a;
                if (!float.IsFinite(sourceAlpha)) throw new InvalidOperationException("non-finite alpha");
                var alpha = (byte)Math.Clamp((int)MathF.Round(Math.Clamp(sourceAlpha, 0, 1) * 255), 0, 255);
                hasVisibleAlpha |= alpha > 0;
                pixels[index] = new Color32(255, 255, 255, alpha);
            }
            if (!hasVisibleAlpha) throw new InvalidOperationException("no visible pixels");
            stage = "mask-create";
            mask = new Texture2D(key.Width, key.Height, TextureFormat.RGBA32, false, true)
            {
                name = TextureName,
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            stage = "mask-upload";
            mask.SetPixels32(pixels);
            mask.Apply(false, true);
        }
        catch (Exception exception) { failure = DescribeFailure(stage, key, exception); }
        finally
        {
            // 每项清理单独执行：某一步恢复失败也不阻止后续 RT、矩阵和自有对象的回收。
            // 保留第一处失败阶段，后续清理异常不会覆盖真正的绘制/读回失败原因。
            if (drawing) Cleanup(GL.End, "restore-end", key, ref failure);
            if (matrixPushed) Cleanup(GL.PopMatrix, "restore-matrix", key, ref failure);
            Cleanup(() => GL.sRGBWrite = previousSrgb, "restore-srgb", key, ref failure);
            Cleanup(() => RenderTexture.active = previousTarget, "restore-target", key, ref failure);
            if (readback != null) Cleanup(() => UnityEngine.Object.Destroy(readback), "dispose-readback", key, ref failure);
            if (material != null) Cleanup(() => UnityEngine.Object.Destroy(material), "dispose-material", key, ref failure);
            if (target != null) Cleanup(() => RenderTexture.ReleaseTemporary(target), "release-target", key, ref failure);
            if (failure != null && mask != null)
            {
                Cleanup(() => UnityEngine.Object.Destroy(mask), "dispose-mask", key, ref failure);
                mask = null;
            }
        }
        if (failure != null) throw failure;
        return mask ?? throw new InvalidOperationException("cooker alpha mask creation failed; " + Describe(key));
    }

    private static void Emit(float x, float y, float u, float v)
    {
        GL.TexCoord2(u, v);
        GL.Vertex3(x, y, 0);
    }

    private static string Describe(MaskKey key) => $"crop={key.Left},{key.Bottom},{key.Width}x{key.Height}; "
        + $"source={key.SourceWidth}x{key.SourceHeight}; texture={key.SourcePointer:x}";

    private static Exception DescribeFailure(string stage, MaskKey key, Exception exception) =>
        new InvalidOperationException(LimitFailure($"cooker alpha mask failed at {stage}; {Describe(key)}: {exception.GetBaseException().Message}"), exception);

    // 失败缓存只保留有界文本；阶段与裁剪信息位于前缀，截断长原生错误时仍能定位失败位置。
    private static string LimitFailure(string message) => message.Length <= 1024 ? message : message[..1024];

    private static void Cleanup(Action action, string stage, MaskKey key, ref Exception? failure)
    {
        try { action(); }
        catch (Exception exception) { failure ??= DescribeFailure(stage, key, exception); }
    }
    /// <summary>主线程独占缓存，无后台锁等待，也不允许后台线程读写任何 Unity 纹理包装。</summary>
    private static void EnsureMainThread()
    {
        if (Environment.CurrentManagedThreadId != RuntimeNightBusinessLifecycle.Snapshot.ThreadId)
            throw new InvalidOperationException("cooker alpha mask requires the game main thread");
    }

    private static bool Finite(Vector2 value) => float.IsFinite(value.x) && float.IsFinite(value.y);

    /// <summary>
    /// 租约只代表一个使用方；可重复释放，最后一个使用方释放时立即回收自有遮罩。
    /// 原纹理身份与裁剪参数按值保存，绝不将游戏图集包装跨帧保存在缓存中。
    /// </summary>
    internal sealed class Lease : IDisposable
    {
        private MaskEntry? _entry;

        internal Lease(MaskEntry entry) => _entry = entry;

        internal bool IsAlive
        {
            get
            {
                EnsureMainThread();
                return _entry != null && _entry.Texture != null;
            }
        }

        internal Texture2D Texture
        {
            get
            {
                EnsureMainThread();
                var entry = _entry ?? throw new ObjectDisposedException(nameof(Lease));
                return entry.Texture != null ? entry.Texture
                    : throw new InvalidOperationException("cooker alpha mask texture destroyed");
            }
        }

        /// <summary>
        /// 原图集 UV 先换算为像素坐标，再映射到裁剪遮罩。旋转图集已体现在原 UV 中，不另作旋转。
        /// 只容忍浮点插值在边缘产生的微小误差，拒绝范围外采样以防错误背景污染相邻图集条目。
        /// </summary>
        internal Vector2 MapUv(Vector2 originalUv)
        {
            EnsureMainThread();
            var entry = _entry ?? throw new ObjectDisposedException(nameof(Lease));
            if (!Finite(originalUv)) throw new InvalidOperationException("cooker alpha mask UV is not finite");
            var key = entry.Key;
            var x = (originalUv.x * key.SourceWidth - key.Left) / key.Width;
            var y = (originalUv.y * key.SourceHeight - key.Bottom) / key.Height;
            if (x < -.000001f || y < -.000001f || x > 1.000001f || y > 1.000001f)
                throw new InvalidOperationException("cooker alpha mask UV outside crop");
            return new Vector2(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        }

        public void Dispose()
        {
            EnsureMainThread();
            var entry = _entry;
            if (entry == null) return;
            _entry = null;
            entry.References--;
            if (entry.References != 0) return;
            if (Entries.TryGetValue(entry.Key, out var current) && ReferenceEquals(current, entry))
                Entries.Remove(entry.Key);
            if (entry.Texture != null) UnityEngine.Object.Destroy(entry.Texture);
        }
    }

    /// <summary>纹理尺寸计入键，避免同一指针资产在原位重建为不同尺寸时错误复用 UV 映射。</summary>
    internal readonly record struct MaskKey(nint SourcePointer, int SourceWidth, int SourceHeight,
        int Left, int Bottom, int Width, int Height);

    /// <summary>只持有 Mod 自有纹理。引用计数由主线程上的租约获取与释放成对维护。</summary>
    internal sealed class MaskEntry
    {
        internal MaskEntry(MaskKey key, Texture2D texture)
        {
            Key = key;
            Texture = texture;
        }

        internal MaskKey Key { get; }
        internal Texture2D Texture { get; }
        internal int References { get; set; } = 1;
    }
}
