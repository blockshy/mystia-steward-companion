using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MystiaStewardCompanion.GameMedia;

/// <summary>
/// 纯托管的图片代次存储。每代图片/清单写入全新目录并校验，最后仅原子替换 current.json。
/// 中断或失败最多留下未引用的新目录，绝不覆盖仍可恢复的旧代；路径只由已验证种类和整数生成。
/// </summary>
internal sealed class GameMediaStore
{
    internal const int MaximumItems = 2048;
    internal const int MaximumImageBytes = 256 * 1024;
    internal const long MaximumTotalBytes = 32L * 1024 * 1024;
    internal const int MaximumManifestBytes = 2 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
        { "food", "beverage", "ingredient", "cooker", "rare-guest", "normal-group" };
    private readonly string _root;
    private readonly object _ioGate = new();
    private sealed record Current(string Generation, string SourceFingerprint, string ManifestSha256);
    private sealed record StartupReceipt(int Version, string StartupFingerprint, string ManifestSha256, GameMediaPlan Plan);

    internal GameMediaStore(string root)
    {
        _root = Path.GetFullPath(root);
        // I/O 边界在后台加载/发布时验证。构造阶段不因损坏路径中止整个插件启动，
        // 使连接诊断和资源重试入口仍能显示明确 failed 状态。
    }

    /// <summary>
    /// 仅用于启动状态文案的缓存候选提示：只查询固定 current.json 是否存在，不枚举目录、
    /// 不读取图片或清单内容，也不获取后台写盘锁。文件存在绝不代表可信；所有路径、版本、
    /// 全集、摘要与 PNG 校验仍必须由 TryLoad 完成后才能 ready。不存在或无权查询时按首次加载。
    /// </summary>
    internal bool HasCacheCandidate => File.Exists(Path.Combine(_root, "current.json"));

    internal static void ValidatePlan(GameMediaPlan plan)
    {
        if (!HashValid(plan.SourceFingerprint) || plan.StartupFingerprint != null && !HashValid(plan.StartupFingerprint)
            || plan.Items == null || plan.Recipes == null
            || plan.Items.Count == 0 || plan.Items.Count > MaximumItems || plan.Recipes.Count > MaximumItems)
            throw new InvalidDataException("图片目录计划的版本或数量无效。");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in plan.Items)
            if (item == null || !KeyValid(item.Kind, item.Id) || string.IsNullOrWhiteSpace(item.Name)
                || item.Name.Length > 200 || !keys.Add(item.Key)) throw new InvalidDataException("图片目录身份不唯一或无效。");
        ValidateRecipes(plan.Recipes);
    }

    /// <summary>启动时逐张核对摘要、PNG边界、目录全集；损坏缓存不得成为 ready，也不会在这里改写。</summary>
    internal GameMediaManifest? TryLoad(GameMediaPlan plan)
    {
        lock (_ioGate)
        {
            var manifest = TryLoadCore(plan);
            if (manifest != null && !CanReuseStartupIdentity(plan, manifest)) return null;
            // 旧版清单无需重新提图：只有真实目录与整代 PNG 都通过原校验后，才补写启动凭据。
            // 此私有凭据不进入 HTTP 清单，也不保存账号、许可令牌或本机绝对路径。
            if (manifest != null)
            {
                try { WriteStartupReceipt(plan, manifest); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 这里失败的仅是已有合法代的可选加速凭据：真实目录与整代 PNG 已通过，
                    // 不应因此阻断本次使用。保留原文件/目录；下次无有效凭据仍走完整目录验证。
                    // 新代 Commit 的凭据写入不经过此降级分支，仍必须原子建立成功才发布。
                }
            }
            return manifest;
        }
    }

    private bool CanReuseStartupIdentity(GameMediaPlan plan, GameMediaManifest manifest)
    {
        if (plan.StartupFingerprint == null) return true;
        var path = Path.Combine(_root, manifest.Generation, "startup.json");
        // 旧版本确实没有凭据时，可用本次真实目录升级。已有凭据则必须比对，防止资源文件
        // 元数据或 DLC 改变后先 startup-miss，接着又因旧 source 公式相同错误复用旧 PNG。
        if (!File.Exists(path)) return true;
        try
        {
            using var document = JsonDocument.Parse(ReadBounded(path, MaximumManifestBytes));
            ExactFields(document.RootElement, "version", "startupFingerprint", "manifestSha256", "plan");
            return document.RootElement.GetProperty("version").GetInt32() == 1
                && document.RootElement.GetProperty("startupFingerprint").GetString() == plan.StartupFingerprint;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or InvalidOperationException or FormatException) { return false; }
    }

    /// <summary>
    /// 未读档时仅凭当前精确启动指纹恢复已经验证过的图片计划。旧缓存缺少凭据、指纹变化或
    /// 任一文件损坏均返回不可复用；不猜目录全集，不通过缓存构造业务 RuntimeDataCatalog。
    /// 每个服务启动最多调用一次，且调用方必须在后台执行全部磁盘与 PNG 校验。
    /// </summary>
    internal GameMediaCachedPlan? TryLoadStartup(string startupFingerprint)
        => TryLoadStartup(startupFingerprint, out _);

    /// <summary>返回固定失败码，区分旧代缺凭据、身份变化和文件损坏；不回传路径或原始异常。</summary>
    internal GameMediaCachedPlan? TryLoadStartup(string startupFingerprint, out string reason)
    {
        reason = "startup-identity-invalid";
        if (!HashValid(startupFingerprint)) return null;
        lock (_ioGate)
        {
            try
            {
                reason = "startup-receipt-invalid";
                var pointerBytes = ReadBounded(Path.Combine(_root, "current.json"), 4096);
                using var pointerDocument = JsonDocument.Parse(pointerBytes);
                ExactFields(pointerDocument.RootElement, "generation", "sourceFingerprint", "manifestSha256");
                var pointer = JsonSerializer.Deserialize<Current>(pointerBytes, JsonOptions)!;
                if (!GenerationValid(pointer.Generation) || !HashValid(pointer.ManifestSha256)) return null;
                var receiptPath = Path.Combine(_root, pointer.Generation, "startup.json");
                if (!File.Exists(receiptPath))
                {
                    reason = "startup-receipt-missing";
                    return null;
                }
                var receiptBytes = ReadBounded(receiptPath, MaximumManifestBytes);
                using var receiptDocument = JsonDocument.Parse(receiptBytes);
                var root = receiptDocument.RootElement;
                ExactFields(root, "version", "startupFingerprint", "manifestSha256", "plan");
                var rawPlan = root.GetProperty("plan");
                ExactFields(rawPlan, "sourceFingerprint", "items", "recipes");
                foreach (var item in rawPlan.GetProperty("items").EnumerateArray()) ExactFields(item, "kind", "id", "name");
                foreach (var recipe in rawPlan.GetProperty("recipes").EnumerateArray())
                    ExactFields(recipe, "foodId", "recipeId", "ingredientIds", "cookerType");
                var receipt = JsonSerializer.Deserialize<StartupReceipt>(receiptBytes, JsonOptions)!;
                if (receipt.Version != 1 || receipt.ManifestSha256 != pointer.ManifestSha256 || receipt.Plan == null
                    || receipt.Plan.SourceFingerprint != pointer.SourceFingerprint) return null;
                if (receipt.StartupFingerprint != startupFingerprint)
                {
                    reason = "startup-identity-changed";
                    return null;
                }
                var plan = receipt.Plan with { StartupFingerprint = startupFingerprint };
                var manifest = TryLoadCore(plan);
                reason = manifest == null ? "cache-content-invalid" : "ready";
                return manifest == null ? null : new(plan, manifest);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
                or JsonException or InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException)
            {
                return null;
            }
        }
    }

    private GameMediaManifest? TryLoadCore(GameMediaPlan plan)
    {
        ValidatePlan(plan);
        var pointerPath = Path.Combine(_root, "current.json");
        if (!File.Exists(pointerPath)) return null;
        try
        {
            var pointerBytes = ReadBounded(pointerPath, 4096);
            using var pointerDocument = JsonDocument.Parse(pointerBytes);
            ExactFields(pointerDocument.RootElement, "generation", "sourceFingerprint", "manifestSha256");
            var pointer = JsonSerializer.Deserialize<Current>(pointerBytes, JsonOptions)!;
            if (!GenerationValid(pointer.Generation) || !HashValid(pointer.ManifestSha256)) return null;
            if (pointer.SourceFingerprint != plan.SourceFingerprint) return null;
            var manifestBytes = ReadBounded(Path.Combine(_root, pointer.Generation, "manifest.json"), MaximumManifestBytes);
            if (Hash(manifestBytes) != pointer.ManifestSha256) return null;
            using var document = JsonDocument.Parse(manifestBytes);
            ValidateManifestDocument(document.RootElement);
            var manifest = JsonSerializer.Deserialize<GameMediaManifest>(manifestBytes, JsonOptions)!;
            ValidateManifest(manifest, plan);
            if (manifest.Generation != pointer.Generation) return null;
            foreach (var item in manifest.Items) _ = ReadImage(manifest, item.Key);
            return manifest;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or NullReferenceException)
        {
            // 只将当前缓存视为不可复用，调用方会重新提取；不删除原始损坏证据，也不返回半份图片。
            return null;
        }
    }

    /// <summary>调用方已结束主线程提取；此方法仅消费独占的托管字节，可在后台执行。</summary>
    internal GameMediaManifest Commit(GameMediaPlan plan, string generation,
        IReadOnlyDictionary<string, GameMediaImage> images, IReadOnlyList<GameMediaMissing> missing,
        CancellationToken cancellationToken = default)
    {
        lock (_ioGate) return CommitCore(plan, generation, images, missing, cancellationToken);
    }

    private GameMediaManifest CommitCore(GameMediaPlan plan, string generation,
        IReadOnlyDictionary<string, GameMediaImage> images, IReadOnlyList<GameMediaMissing> missing,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePlan(plan);
        if (!GenerationValid(generation)) throw new InvalidDataException("图片代次无效。");
        var items = new List<GameMediaItem>(); long total = 0;
        foreach (var key in plan.Items)
        {
            if (!images.TryGetValue(key.Key, out var image)) continue;
            ValidatePng(image.Png, image.Width, image.Height);
            total += image.Png.Length;
            if (total > MaximumTotalBytes) throw new InvalidDataException("图片总大小超过限制。");
            items.Add(new GameMediaItem(key.Key, key.Kind, key.Id, key.Name, Hash(image.Png), image.Png.Length, image.Width, image.Height));
        }
        if (items.Count != images.Count) throw new InvalidDataException("图片包含计划外身份。");
        var manifest = new GameMediaManifest(1, generation, plan.SourceFingerprint, items,
            plan.Recipes.ToArray(), missing.ToArray());
        ValidateManifest(manifest, plan);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("图片清单超过限制。");
        CheckPath(_root);
        Directory.CreateDirectory(_root);
        var stage = Path.Combine(_root, ".pending-" + generation);
        var target = Path.Combine(_root, generation);
        CheckPath(stage); CheckPath(target);
        if (Directory.Exists(stage) || Directory.Exists(target)) throw new IOException("图片代次目录已存在。");
        Directory.CreateDirectory(stage);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteNew(Path.Combine(stage, FileName(item.Kind, item.Id)), images[item.Key].Png);
        }
        WriteNew(Path.Combine(stage, "manifest.json"), bytes);
        // 凭据与新代文件一起建立，current 指针仍然是最后唯一发布点。首次没有可信启动
        // 身份时不写凭据，保留原有读档提取流程；下次不能用不完整身份直接恢复图片。
        WriteStartupReceiptAt(stage, plan, Hash(bytes));
        Directory.Move(stage, target);
        var pointer = JsonSerializer.SerializeToUtf8Bytes(new Current(generation, plan.SourceFingerprint, Hash(bytes)), JsonOptions);
        var pendingPointer = Path.Combine(_root, ".current-" + generation + ".tmp");
        WriteNew(pendingPointer, pointer);
        CheckPath(Path.Combine(_root, "current.json"));
        cancellationToken.ThrowIfCancellationRequested();
        File.Move(pendingPointer, Path.Combine(_root, "current.json"), overwrite: true);
        PruneOldGenerations(generation);
        return manifest;
    }

    private void WriteStartupReceipt(GameMediaPlan plan, GameMediaManifest manifest)
    {
        if (!HashValid(plan.StartupFingerprint)) return;
        var directory = Path.Combine(_root, manifest.Generation);
        var manifestHash = Hash(ReadBounded(Path.Combine(directory, "manifest.json"), MaximumManifestBytes));
        WriteStartupReceiptAt(directory, plan, manifestHash);
    }

    /// <summary>仅在整代验证或发布事务中原子写入凭据，不覆盖图片、不修改已有 current 指针。</summary>
    private void WriteStartupReceiptAt(string directory, GameMediaPlan plan, string manifestHash)
    {
        if (!HashValid(plan.StartupFingerprint)) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StartupReceipt(1, plan.StartupFingerprint!, manifestHash, plan), JsonOptions);
        if (bytes.Length > MaximumManifestBytes) throw new InvalidDataException("图片启动凭据超过限制。");
        var path = Path.Combine(directory, "startup.json");
        CheckPath(path);
        if (File.Exists(path) && ReadBounded(path, MaximumManifestBytes).SequenceEqual(bytes)) return;
        var pending = Path.Combine(directory, ".startup-" + Guid.NewGuid().ToString("N") + ".tmp");
        WriteNew(pending, bytes);
        File.Move(pending, path, overwrite: true);
    }

    /// <summary>成功切代后回收自身的旧平面图片目录；未知文件/子目录整代保留，不递归删除用户内容。</summary>
    private void PruneOldGenerations(string current)
    {
        // 枚举本身也可能因权限/并发删除失败；current.json 已发布后，回收不能改变成功结果。
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_root))
            {
                var name = Path.GetFileName(directory);
                if (!GenerationValid(name) || name == current) continue;
                try
                {
                    CheckPath(directory);
                    var entries = Directory.GetFileSystemEntries(directory);
                    if (entries.Any(path => Directory.Exists(path)
                        || (Path.GetFileName(path) is not ("manifest.json" or "startup.json") && !Regex.IsMatch(Path.GetFileName(path),
                            "^(food|beverage|ingredient|cooker|rare-guest|normal-group)-[0-9]+\\.png$")))) continue;
                    foreach (var path in entries) CheckPath(path);
                    foreach (var path in entries) File.Delete(path);
                    Directory.Delete(directory, recursive: false);
                }
                catch (IOException) { /* 已发布新代，回收失败不能撤销成功或删除更多目录。 */ }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal byte[] ReadImage(GameMediaManifest manifest, string key)
    {
        if (!GenerationValid(manifest.Generation)) throw new InvalidDataException("图片代次无效。");
        var item = manifest.Items.SingleOrDefault(value => value.Key == key)
            ?? throw new GameMediaException(404, "game-media-image-not-found");
        var bytes = ReadBounded(Path.Combine(_root, manifest.Generation, FileName(item.Kind, item.Id)), MaximumImageBytes);
        if (bytes.Length != item.ByteLength || Hash(bytes) != item.Sha256) throw new InvalidDataException("图片缓存校验失败。");
        ValidatePng(bytes, item.Width, item.Height);
        return bytes;
    }

    private static void ValidateManifest(GameMediaManifest manifest, GameMediaPlan plan)
    {
        if (manifest.Version != 1 || !GenerationValid(manifest.Generation) || manifest.SourceFingerprint != plan.SourceFingerprint
            || manifest.Items == null || manifest.Missing == null || manifest.Recipes == null
            || manifest.Items.Count > MaximumItems || manifest.Missing.Count > MaximumItems)
            throw new InvalidDataException("图片清单头部无效。");
        var expected = plan.Items.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal); long total = 0;
        foreach (var item in manifest.Items)
        {
            if (item == null || !KeyValid(item.Kind, item.Id) || item.Key != new GameMediaKey(item.Kind, item.Id, "").Key
                || !expected.TryGetValue(item.Key, out var source) || source.Name != item.Name || !seen.Add(item.Key)
                || !HashValid(item.Sha256) || item.ByteLength <= 0 || item.ByteLength > MaximumImageBytes
                || item.Width <= 0 || item.Height <= 0 || item.Width > 512 || item.Height > 512)
                throw new InvalidDataException("图片清单条目无效。");
            total += item.ByteLength;
        }
        foreach (var item in manifest.Missing)
            if (item == null || !expected.ContainsKey(item.Key) || !seen.Add(item.Key)
                || string.IsNullOrWhiteSpace(item.Reason) || item.Reason.Length > 500)
                throw new InvalidDataException("图片缺失记录无效。");
        if (seen.Count != expected.Count || total > MaximumTotalBytes) throw new InvalidDataException("图片清单不完整或总大小超限。");
        ValidateRecipes(manifest.Recipes);
        if (JsonSerializer.Serialize(manifest.Recipes, JsonOptions) != JsonSerializer.Serialize(plan.Recipes, JsonOptions))
            throw new InvalidDataException("配方目录与当前游戏不一致。");
    }

    private static void ValidateRecipes(IReadOnlyList<GameMediaRecipe> recipes)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recipe in recipes)
            if (recipe == null || recipe.FoodId < 0 || recipe.RecipeId < 0 || recipe.CookerType < 1 || recipe.CookerType > 5
                || recipe.IngredientIds == null || recipe.IngredientIds.Length == 0 || recipe.IngredientIds.Length > 32
                || recipe.IngredientIds.Any(id => id < 0) || !keys.Add(recipe.FoodId + ":" + recipe.RecipeId))
                throw new InvalidDataException("配方身份或材料引用无效。");
    }

    private static bool KeyValid(string kind, int id) => kind != null && Kinds.Contains(kind) && id >= 0;
    private static bool GenerationValid(string? value) => value != null && Regex.IsMatch(value, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static bool HashValid(string? value) => value != null && Regex.IsMatch(value, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static string FileName(string kind, int id) => KeyValid(kind, id)
        ? kind + "-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png"
        : throw new InvalidDataException("图片键无效。");
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>拒绝全部重解析祖先，防止缓存目录或单文件链接把读写引向游戏资源/其它用户文件。</summary>
    private static void CheckPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("图片缓存路径不允许重解析点。");
            current = Path.GetDirectoryName(current);
        }
    }
    private static byte[] ReadBounded(string path, int maximum)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = stream.Length;
        if (length <= 0 || length > maximum) throw new InvalidDataException("图片缓存文件大小无效。");
        // 按已验证长度分配，禁止 ReadAllBytes 在文件并发增长时产生未受限的内存分配。
        var bytes = new byte[(int)length]; var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new InvalidDataException("图片缓存文件在读取期间缩短。");
            offset += count;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("图片缓存文件在读取期间超限。");
        return bytes;
    }
    private static void WriteNew(string path, byte[] bytes)
    {
        CheckPath(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void ExactFields(JsonElement value, params string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("图片清单字段类型无效。");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || names.Distinct(StringComparer.Ordinal).Count() != fields.Length
            || names.Except(fields, StringComparer.Ordinal).Any()) throw new InvalidDataException("图片清单字段不完整或重复。");
    }
    private static void ValidateManifestDocument(JsonElement root)
    {
        ExactFields(root, "version", "generation", "sourceFingerprint", "items", "recipes", "missing");
        foreach (var item in root.GetProperty("items").EnumerateArray())
            ExactFields(item, "key", "kind", "id", "name", "sha256", "byteLength", "width", "height");
        foreach (var recipe in root.GetProperty("recipes").EnumerateArray())
            ExactFields(recipe, "foodId", "recipeId", "ingredientIds", "cookerType");
        foreach (var item in root.GetProperty("missing").EnumerateArray()) ExactFields(item, "key", "reason");
    }

    /// <summary>验证有界 PNG 的尺寸、块边界和 CRC，不调用图像库或 Unity 解码器。</summary>
    internal static void ValidatePng(byte[] bytes, int width, int height)
    {
        if (bytes == null || bytes.Length < 45 || bytes.Length > MaximumImageBytes || width < 1 || height < 1
            || width > 512 || height > 512 || !bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidDataException("PNG 头部或大小无效。");
        var cursor = 8; var count = 0; var data = false; var ended = false;
        while (cursor < bytes.Length)
        {
            if (++count > 1024 || bytes.Length - cursor < 12) throw new InvalidDataException("PNG 块数量或长度无效。");
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor, 4));
            if (length > bytes.Length - cursor - 12) throw new InvalidDataException("PNG 块越界。");
            var size = (int)length; var type = Encoding.ASCII.GetString(bytes, cursor + 4, 4);
            var expected = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 8 + size, 4));
            uint crc = uint.MaxValue;
            for (var index = cursor + 4; index < cursor + 8 + size; index++)
            {
                crc ^= bytes[index];
                for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320U ^ (crc >> 1) : crc >> 1;
            }
            if (~crc != expected) throw new InvalidDataException("PNG CRC 校验失败。");
            if (count == 1 && (type != "IHDR" || size != 13
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 8, 4)) != width
                || BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 12, 4)) != height))
                throw new InvalidDataException("PNG 尺寸与清单不一致。");
            if (type == "IDAT") data = true;
            cursor += size + 12;
            if (type == "IEND") { ended = size == 0 && cursor == bytes.Length; break; }
        }
        if (!data || !ended) throw new InvalidDataException("PNG 缺少图像数据或终止块。");
    }
}
