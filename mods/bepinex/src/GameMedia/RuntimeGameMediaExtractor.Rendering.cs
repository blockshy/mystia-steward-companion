using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace MystiaStewardCompanion.GameMedia;

internal sealed partial class RuntimeGameMediaExtractor
{
    private const int MaximumImageEdge = 128;

    /// <summary>
    /// 使用 Sprite 本身的顶点、三角形与 UV 绘制到 Mod 独占的小型离屏纹理。
    /// 不读取整个图集，也不假设 textureRect 一定可用：紧密打包和旋转图集仍保留精确 UV。
    /// 每次调用最多读回128×128像素；服务只在首次生成/明确刷新期间逐项调用，完成后停止。
    /// </summary>
    private static GameMediaImage RenderImage(IReadOnlyList<MediaLayer> layers, bool notebookAvatar)
    {
        if (layers.Count is < 1 or > 4) throw new InvalidOperationException("图片图层数量无效。");
        var width = notebookAvatar ? 95f : layers[0].Width;
        var height = notebookAvatar ? 126f : layers[0].Height;
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new InvalidOperationException("图片画布尺寸无效。");
        var scale = Math.Min(1f, MaximumImageEdge / Math.Max(width, height));
        var outputWidth = Math.Max(1, (int)Math.Ceiling(width * scale));
        var outputHeight = Math.Max(1, (int)Math.Ceiling(height * scale));

        // 浮点离屏纹理避免半透明边缘先量化成8位再反预乘所产生的暗边。
        // Shader/RT 格式缺失属于提取失败，不能把一张透明空图写成成功图片。
        var shader = Shader.Find("Sprites/Default");
        if (shader == null || !shader || !shader.isSupported
            || !SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))
            throw new InvalidOperationException("当前图形设备不支持图片离屏提取。");

        Material? material = null;
        RenderTexture? target = null;
        Texture2D? readback = null;
        Texture2D? output = null;
        var previousTarget = RenderTexture.active;
        var previousSrgb = GL.sRGBWrite;
        var matrixPushed = false;
        try
        {
            material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            material.SetColor("_Color", Color.white);
            material.SetColor("_RendererColor", Color.white);
            material.SetVector("_Flip", new Vector4(1, 1, 1, 1));
            target = RenderTexture.GetTemporary(outputWidth, outputHeight, 0,
                RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            target.filterMode = FilterMode.Point;
            RenderTexture.active = target;
            GL.sRGBWrite = false;
            GL.Clear(true, true, Color.clear);
            GL.PushMatrix();
            matrixPushed = true;
            // Unity ReadPixels 的原点在左下。头像仍使用图鉴遮罩中心坐标；物品使用精灵完整矩形。
            GL.LoadPixelMatrix(notebookAvatar ? -width / 2 : 0, notebookAvatar ? width / 2 : width,
                notebookAvatar ? -height / 2 : 0, notebookAvatar ? height / 2 : height);
            foreach (var layer in layers) DrawLayer(material, layer, notebookAvatar);

            readback = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBAFloat, false, true)
                { hideFlags = HideFlags.HideAndDontSave };
            readback.ReadPixels(new Rect(0, 0, outputWidth, outputHeight), 0, 0, false);
            var premultiplied = readback.GetPixels();
            if (premultiplied.Length != outputWidth * outputHeight)
                throw new InvalidOperationException("离屏图片读回像素数量不符。");
            var straight = new Il2CppStructArray<Color32>(premultiplied.Length);
            var hasVisiblePixel = false;
            var convertLinear = QualitySettings.activeColorSpace == ColorSpace.Linear;
            for (var index = 0; index < premultiplied.Length; index++)
            {
                var color = premultiplied[index];
                if (!float.IsFinite(color.r) || !float.IsFinite(color.g) || !float.IsFinite(color.b)
                    || !float.IsFinite(color.a)) throw new InvalidOperationException("离屏图片包含非有限颜色。");
                var alpha = Mathf.Clamp01(color.a);
                if (alpha <= 0) { straight[index] = new Color32(0, 0, 0, 0); continue; }
                // Sprites/Default 使用预乘 alpha 混合；PNG 使用直通 alpha。
                // 线性项目先反预乘，再转回 sRGB，以免 Flutter 显示发灰或出现暗色轮廓。
                var red = Mathf.Clamp01(color.r / alpha);
                var green = Mathf.Clamp01(color.g / alpha);
                var blue = Mathf.Clamp01(color.b / alpha);
                if (convertLinear)
                {
                    red = Mathf.LinearToGammaSpace(red);
                    green = Mathf.LinearToGammaSpace(green);
                    blue = Mathf.LinearToGammaSpace(blue);
                }
                var byteAlpha = ToByte(alpha);
                hasVisiblePixel |= byteAlpha > 0;
                straight[index] = new Color32(ToByte(red), ToByte(green), ToByte(blue), byteAlpha);
            }
            if (!hasVisiblePixel) throw new MediaMissingException("该图片在图鉴显示范围内没有可见像素。");

            output = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false)
                { hideFlags = HideFlags.HideAndDontSave };
            output.SetPixels32(straight);
            output.Apply(false, false);
            // ImageConversion 属于独立 Unity 模块。按当前 #783 interop 的精确静态签名调用，
            // 不增加编译期游戏 DLL，也不尝试其它方法或替代解码器。
            // CLR 按需加载 wrapper 程序集；此前没有托管调用 EncodeToPNG 时，它可能还不在
            // AppDomain 列表里。明确加载这个已安装的 interop 模块，不能把“尚未加载 CLR DLL”
            // 当成 Unity 资源 Pending 后无限等待，也不搜索任意文件或下载其它编码器。
            var imageConversion = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", throwOnError: true)
                ?? throw new TypeLoadException("UnityEngine.ImageConversion 类型缺失。");
            var encode = imageConversion.GetMethod("EncodeToPNG", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(Texture2D) }, null)
                ?? throw new MissingMethodException("UnityEngine.ImageConversion.EncodeToPNG(Texture2D) 缺失。");
            if (encode.Invoke(null, new object[] { output }) is not Il2CppStructArray<byte> png
                || png.Length is <= 0 or > 256 * 1024)
                throw new InvalidOperationException("PNG 编码结果无效。");
            var bytes = new byte[png.Length];
            for (var index = 0; index < bytes.Length; index++) bytes[index] = png[index];
            return new(bytes, outputWidth, outputHeight);
        }
        finally
        {
            // 即使图层、读回或编码抛错，也恢复进入时的渲染状态，并仅销毁本方法自己创建的资源。
            // 不销毁或改写 Sprite.texture，也不把任何 Unity wrapper 留给后台存储线程。
            if (matrixPushed) GL.PopMatrix();
            GL.sRGBWrite = previousSrgb;
            RenderTexture.active = previousTarget;
            if (output != null) UnityEngine.Object.Destroy(output);
            if (readback != null) UnityEngine.Object.Destroy(readback);
            if (material != null) UnityEngine.Object.Destroy(material);
            if (target != null) RenderTexture.ReleaseTemporary(target);
        }
    }

    private static void DrawLayer(Material material, MediaLayer layer, bool notebookAvatar)
    {
        var sprite = layer.Sprite;
        var texture = sprite.texture;
        var vertices = sprite.vertices;
        var triangles = sprite.triangles;
        var uv = sprite.uv;
        var rect = sprite.rect;
        var pivot = sprite.pivot;
        var pixelsPerUnit = sprite.pixelsPerUnit;
        if (texture == null || !texture || vertices == null || triangles == null || uv == null
            || vertices.Length is < 3 or > 65536 || uv.Length != vertices.Length
            || triangles.Length is < 3 or > 196608 || triangles.Length % 3 != 0
            || !float.IsFinite(rect.width) || !float.IsFinite(rect.height) || rect.width <= 0 || rect.height <= 0
            || !float.IsFinite(pixelsPerUnit) || pixelsPerUnit <= 0
            || !float.IsFinite(pivot.x) || !float.IsFinite(pivot.y))
            throw new InvalidOperationException("精灵几何或纹理无效。");
        material.mainTexture = texture;
        if (!material.SetPass(0)) throw new InvalidOperationException("精灵离屏材质不可绘制。");
        GL.Begin(GL.TRIANGLES);
        try
        {
            GL.Color(Color.white);
            for (var index = 0; index < triangles.Length; index++)
            {
                var vertexIndex = triangles[index];
                if (vertexIndex >= vertices.Length) throw new InvalidOperationException("精灵三角形索引越界。");
                var vertex = vertices[vertexIndex];
                var tex = uv[vertexIndex];
                var x = (vertex.x * pixelsPerUnit + pivot.x) / rect.width * layer.Width;
                var y = (vertex.y * pixelsPerUnit + pivot.y) / rect.height * layer.Height;
                if (notebookAvatar) { x += layer.X - layer.Width / 2; y += layer.Y - layer.Height / 2; }
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(tex.x) || !float.IsFinite(tex.y))
                    throw new InvalidOperationException("精灵顶点或 UV 包含非有限数值。");
                GL.TexCoord2(tex.x, tex.y);
                GL.Vertex3(x, y, 0);
            }
        }
        finally { GL.End(); }
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)Math.Round(Math.Clamp(value, 0, 1) * 255), 0, 255);
}
