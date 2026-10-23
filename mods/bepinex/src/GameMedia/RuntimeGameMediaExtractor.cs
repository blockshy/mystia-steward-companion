using System.Reflection;
using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Save;
using UnityEngine;

namespace MystiaStewardCompanion.GameMedia;

/// <summary>
/// 从已经初始化的游戏目录提取图片。此类只在 Controller 主线程中执行，字段仅保留托管目录、
/// 精确身份和线程编号；Sprite、字典及所有 IL2CPP wrapper 都限制在一次调用内。
/// 不创建游戏 UI，不调用客群随机选皮肤方法，也不修改游戏原纹理的可读性或过滤模式。
/// </summary>
internal sealed partial class RuntimeGameMediaExtractor : IRuntimeGameMediaExtractor
{
    private const string LanguageType = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string CharacterType = "GameData.Core.Collections.CharacterUtility.DataBaseCharacter";
    private const string CoreType = "GameData.Core.Collections.DataBaseCore";
    private const string CookerTypeName = "GameData.Core.Collections.Cooker+CookerType";
    private readonly RuntimeDataCatalog _catalog;
    private readonly int _mainThreadId;
    private readonly HashSet<string> _plannedKeys = new(StringComparer.Ordinal);
    private Task<GameMediaSourceIdentity>? _sourceFingerprintTask;
    private readonly Action<string, int>? _reportIdentity;
    private bool _identityReported;
    public Task? PendingPlanPreparation { get; private set; }

    internal RuntimeGameMediaExtractor(RuntimeDataCatalog catalog, Action<string, int>? reportIdentity = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _reportIdentity = reportIdentity;
        // 构造者必须是 Controller 主线程；后续的后台缓存写入不得反向调用此对象。
        _mainThreadId = Environment.CurrentManagedThreadId;
    }

    public bool TryCreatePlan(out GameMediaPlan? plan, out string error)
    {
        plan = null;
        error = "";
        // 每次尝试先撤销上次唤醒信号。指纹已完成而原生目录仍未就绪时，不能把同一个已完成
        // Task 留给服务，否则服务会每帧重试原生目录，绕过原有五秒节流。
        PendingPlanPreparation = null;
        try
        {
            RequireMainThread();
            if (!_catalog.IsComplete) throw new MediaPendingException("运行时目录尚未完整。");

            // Unity 属性只在主线程读取一次并立即投影成字符串。文件打开、读取与摘要运算交给
            // 后台任务；任务闭包仅捕获这三个托管字符串，不捕获 this、目录或任何 Unity wrapper。
            // 在配方循环之前等待结果，避免任务尚未完成时每次重试都重复遍历完整游戏配方。
            if (_sourceFingerprintTask == null)
            {
                // 成功构建计划后会释放任务；显式重读创建新任务时重新允许一条来源诊断。
                // Pending 重试不进入此分支，因而不会重复输出同一轮日志。
                _identityReported = false;
                var sourcePath = Path.Combine(Application.streamingAssetsPath, "aa", "catalog.bundle");
                var unityVersion = Application.unityVersion;
                var applicationVersion = Application.version;
                // 许可标签只读取游戏已确认的缓存；未知不阻断真实目录提取，但不能签发提前复用凭据。
                var labels = RuntimeGameMediaStartupProbe.ReadConfirmedDlcLabels();
                _sourceFingerprintTask = Task.Run(() => GameMediaSourceIdentity.Read(sourcePath, unityVersion, applicationVersion, labels));
            }
            if (!_sourceFingerprintTask.IsCompleted)
            {
                PendingPlanPreparation = _sourceFingerprintTask;
                throw new MediaPendingException("正在后台核验游戏资源版本。");
            }
            // 只有 IsCompleted 后取结果，不在主线程阻塞等待；任务失败按普通失败路径报告。
            var fingerprint = _sourceFingerprintTask.GetAwaiter().GetResult();
            if (!_identityReported)
            {
                // 完整目录恢复后也记录一次启动凭据能力，避免“本次图片命中”掩盖“下次仍无法
                // 提前复用”。只交付固定原因和计数，重试配方读取不重复记录，不泄露磁盘路径。
                _identityReported = true;
                try { _reportIdentity?.Invoke(fingerprint.StartupFailureReason ?? "ready", fingerprint.BundleCount); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
            }

            // 目录构建成功不等于语言图片和角色像素资源同时可用。只检查精确的已加载字典，
            // 不进行场景扫描、Resources 全量查找或 Addressables 阻塞加载。
            foreach (var name in new[] { "Foods", "Ingredients", "Beverages", "StandardCookers" })
                RequireDictionary(LanguageType, name);
            RequireDictionary(CharacterType, "NormalGuestVisual");
            RequireDictionary(CharacterType, "SpecialGuestVisual");

            var items = new Dictionary<string, GameMediaKey>(StringComparer.Ordinal);
            foreach (var value in _catalog.Recipes) AddItem(items, new("food", value.Id, value.Name));
            foreach (var value in _catalog.Ingredients) AddItem(items, new("ingredient", value.Id, value.Name));
            foreach (var value in _catalog.Beverages) AddItem(items, new("beverage", value.Id, value.Name));
            foreach (var value in _catalog.NormalCustomers) AddItem(items, new("normal-group", value.Id, value.Name));
            foreach (var value in _catalog.RareCustomers) AddItem(items, new("rare-guest", value.Id, value.Name));

            var recipes = new List<GameMediaRecipe>();
            var recipeKeys = new HashSet<string>(StringComparer.Ordinal);
            var core = RequireRuntimeType(CoreType);
            var refRecipe = core.GetMethod("RefRecipe", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(int) }, null)
                ?? throw new MissingMethodException("DataBaseCore.RefRecipe(Int32) 缺失。");
            foreach (var value in _catalog.Recipes)
            {
                var raw = refRecipe.Invoke(null, new object[] { value.RecipeId })
                    ?? throw new InvalidOperationException("真实配方引用为空。");
                if (ReadInt(raw, "id") != value.RecipeId || ReadInt(raw, "foodID") != value.Id)
                    throw new InvalidOperationException("料理与配方双身份不一致，拒绝生成依赖映射。");
                if (!recipeKeys.Add(value.Id + ":" + value.RecipeId))
                    throw new InvalidOperationException("目录包含重复的料理/配方身份。");
                if (!RuntimeConcreteCollectionReader.TryReadIntArray(ReadMember(raw, "ingredients"),
                        out var ingredients, out _) || ingredients.Any(id => id < 0))
                    throw new InvalidOperationException("真实配方食材 ID 数组不可读。");
                if (ingredients.Any(id => !items.ContainsKey("ingredient:" + id)))
                    throw new InvalidOperationException("真实配方食材不在完整目录内。");
                var cooker = ReadMember(raw, "cookerType");
                if (cooker == null || !cooker.GetType().IsEnum || cooker.GetType().FullName != CookerTypeName)
                    throw new InvalidOperationException("真实配方厨具枚举类型不符。");
                var cookerId = Convert.ToInt32(cooker, System.Globalization.CultureInfo.InvariantCulture);
                recipes.Add(new(value.Id, value.RecipeId, ingredients.ToArray(), cookerId));
            }

            // 标准厨具来自实际语言字典的枚举键，而非通过中文名称推断 cooker ID。
            var cookers = RequireDictionary(LanguageType, "StandardCookers");
            if (!RuntimeConcreteCollectionReader.TryReadDictionary(cookers, out var cookerEntries, out _))
                throw new InvalidOperationException("标准厨具字典不可完整读取。");
            foreach (var entry in cookerEntries)
            {
                if (entry.Key == null || !entry.Key.GetType().IsEnum || entry.Key.GetType().FullName != CookerTypeName
                    || entry.Value == null) throw new InvalidOperationException("标准厨具字典身份不完整。");
                var id = Convert.ToInt32(entry.Key, System.Globalization.CultureInfo.InvariantCulture);
                AddItem(items, new("cooker", id, ReadMember(entry.Value, "Name") as string ?? ""));
            }
            if (recipes.Any(value => !items.ContainsKey("cooker:" + value.CookerType)))
                throw new InvalidOperationException("真实配方厨具不在完整目录内。");

            var ordered = items.Values.OrderBy(value => value.Key, StringComparer.Ordinal).ToArray();
            _plannedKeys.Clear();
            foreach (var value in ordered) _plannedKeys.Add(value.Key);
            plan = new(fingerprint.SourceFingerprint, ordered, recipes.OrderBy(value => value.FoodId)
                .ThenBy(value => value.RecipeId).ToArray(), fingerprint.StartupFingerprint);
            // 下一次显式重读必须重新核验索引字节，不能沿用本次成功任务的旧指纹。
            _sourceFingerprintTask = null;
            return true;
        }
        catch (MediaPendingException ex)
        {
            _plannedKeys.Clear();
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            _plannedKeys.Clear();
            // 修复磁盘/资源问题后的“重新读取”可以重开任务，不永久缓存一次失败的 Task。
            _sourceFingerprintTask = null;
            // false 仅表示合法的“尚未就绪”。损坏目录、身份冲突或索引读取失败必须让服务
            // 进入 failed，而不能每五秒永久等待；异常文字先去除可能暴露本机路径的 I/O 信息。
            throw new InvalidOperationException(DescribeError(ex));
        }
    }

    public GameMediaExtractState Extract(GameMediaKey item, out GameMediaImage? image, out string message)
    {
        image = null;
        message = "";
        try
        {
            RequireMainThread();
            if (!_plannedKeys.Contains(item.Key)) throw new InvalidOperationException("图片身份不属于当前提取计划。");
            var layers = item.Kind is "rare-guest" or "normal-group"
                ? ReadNotebookLayers(item)
                : ReadObjectLayer(item);
            image = RenderImage(layers, item.Kind is "rare-guest" or "normal-group");
            return GameMediaExtractState.Ready;
        }
        catch (MediaPendingException ex) { message = ex.Message; return GameMediaExtractState.Pending; }
        catch (MediaMissingException ex) { message = ex.Message; return GameMediaExtractState.Missing; }
        catch (Exception ex) { message = DescribeError(ex); return GameMediaExtractState.Failed; }
    }

    private static IReadOnlyList<MediaLayer> ReadObjectLayer(GameMediaKey item)
    {
        var dictionaryName = item.Kind switch
        {
            "food" => "Foods", "beverage" => "Beverages", "ingredient" => "Ingredients",
            "cooker" => "StandardCookers", _ => throw new InvalidOperationException("未知图片命名空间。"),
        };
        object key = item.Kind == "cooker" ? Enum.ToObject(RequireRuntimeType(CookerTypeName), item.Id) : item.Id;
        var language = ReadDictionaryValue(RequireDictionary(LanguageType, dictionaryName), key)
            ?? throw new MediaMissingException("该精确 ID 没有语言图片条目。");
        var sprite = RequireSprite(ReadMember(language, "Visual"), "该条目的 Visual 未配置图片。");
        return new[] { new MediaLayer(sprite, 0, 0, sprite.rect.width, sprite.rect.height) };
    }

    private static IReadOnlyList<MediaLayer> ReadNotebookLayers(GameMediaKey item)
    {
        object pixel;
        if (item.Kind == "normal-group")
        {
            var skins = ReadDictionaryValue(RequireDictionary(CharacterType, "NormalGuestVisual"), item.Id);
            // RefNormalGuestVisual 会 RandomSelectOne，改变游戏 RNG。客群并无唯一随机顾客头像；
            // 固定展示该精确客群声明顺序中的第一个皮肤作为代表，绝不随机或按名称猜选。
            pixel = ReadArray(skins).FirstOrDefault()
                ?? throw new MediaMissingException("该客群未配置第一个代表皮肤。");
        }
        else
        {
            var pair = ReadDictionaryValue(RequireDictionary(CharacterType, "SpecialGuestVisual"), item.Id)
                ?? throw new MediaMissingException("该稀客没有精确的视觉条目。");
            if (ReadInt(pair, "ID") != item.Id) throw new InvalidOperationException("稀客视觉条目 ID 不一致。");
            var skins = ReadMember(pair, "CharacterPixel")
                ?? throw new MediaMissingException("该稀客未配置像素皮肤集。");
            pixel = ReadMember(skins, "defaultSkin")
                ?? throw new MediaMissingException("该稀客未配置默认像素皮肤。");
        }
        if (pixel is UnityEngine.Object unityPixel && !unityPixel)
            throw new MediaMissingException("角色像素皮肤已卸载。");

        // 当前版本图鉴 OnElementEnabled 的 b__8/b__9（RVA E45080/E45100）明确读取
        // MainSprite[1]/EyeSprite[1]，并把两个图层的位置均设为 SpriteOffsetInNoteBook。
        // faceInNoteBook 是全身立绘表情索引，不能将其误当成图鉴头像。
        var body = RequireArraySprite(pixel, "MainSprite", 1);
        var eye = RequireArraySprite(pixel, "EyeSprite", 1, allowNull: true);
        var rawOffset = ReadMember(pixel, "SpriteOffsetInNoteBook");
        if (rawOffset is not Vector2 offset || !float.IsFinite(offset.x) || !float.IsFinite(offset.y))
            throw new InvalidOperationException("图鉴像素偏移无效。");

        // Prefab NoteBook_MainPannel 的 AvatarMask/OffsetRect：95×126，中心偏移(-1,+1)。
        // Image.useSpriteMesh=false、preserveAspect=false：body/eye 占128方框；前后饰品占256方框。
        // 饰品 card getter（RVA 72DDA0/72DDE0）明确取索引0；不要读动态帧或创建真实 UI。
        var layers = new List<MediaLayer>();
        AddTrim("TrimSpriteDisplayBackInCard");
        layers.Add(new(body!, -1 + offset.x, 1 + offset.y, 128, 128));
        if (eye != null) layers.Add(new(eye, -1 + offset.x, 1 + offset.y, 128, 128));
        AddTrim("TrimSpriteDisplayFrontInCard");
        return layers;

        void AddTrim(string name)
        {
            var raw = ReadMember(pixel, name);
            if (raw == null) return;
            var trim = RequireSprite(raw, "图鉴饰品图片已卸载。");
            layers.Add(new(trim, -1, 1, 256 * 0.9998401f, 256 * 0.9998401f));
        }
    }

    private static Sprite? RequireArraySprite(object owner, string name, int index, bool allowNull = false)
    {
        var array = ReadArray(ReadMember(owner, name));
        if (index >= array.Count) throw new MediaMissingException("图鉴所需的精确像素帧不存在。");
        if (allowNull && array[index] == null) return null;
        return RequireSprite(array[index], "图鉴所需的精确像素帧未配置图片。");
    }

    private static IReadOnlyList<object?> ReadArray(object? value)
    {
        if (value == null) throw new MediaMissingException("视觉资源数组未配置。");
        if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(value, out var result, out _) || result.Count > 4096)
            throw new InvalidOperationException("视觉资源数组形状不符。");
        return result;
    }

    private static object RequireDictionary(string typeName, string name)
    {
        var value = ReadMember(RequireRuntimeType(typeName), name);
        if (value == null) throw new MediaPendingException("游戏图片目录尚未初始化。");
        if (!RuntimeConcreteCollectionReader.TryReadDictionaryCount(value, out _, out _))
            throw new InvalidOperationException("游戏图片目录不是可验证的精确字典。");
        return value;
    }

    private static object? ReadDictionaryValue(object dictionary, object key)
    {
        if (!RuntimeConcreteCollectionReader.TryGetDictionaryValue(dictionary, key, out var value, out var found, out _))
            throw new InvalidOperationException("游戏图片目录读取失败。");
        return found ? value : null;
    }

    private static object? ReadMember(object owner, string name)
    {
        var type = owner as Type ?? owner.GetType();
        var flags = BindingFlags.Public | BindingFlags.NonPublic | (owner is Type ? BindingFlags.Static : BindingFlags.Instance);
        var property = type.GetProperty(name, flags);
        if (property != null && property.GetIndexParameters().Length == 0)
            return property.GetValue(owner is Type ? null : owner);
        var field = type.GetField(name, flags);
        if (field != null) return field.GetValue(owner is Type ? null : owner);
        throw new MissingMemberException(type.FullName, name);
    }

    private static int ReadInt(object owner, string name) => ReadMember(owner, name) is int value
        ? value : throw new InvalidOperationException("游戏资源身份不是 Int32。");

    private static Sprite RequireSprite(object? value, string missing)
    {
        if (value == null) throw new MediaMissingException(missing);
        var sprite = value as Sprite ?? RuntimeReflectionUtility.TryCastRuntimeObject(value, "UnityEngine.Sprite") as Sprite;
        if (sprite == null || !sprite) throw new MediaMissingException(missing);
        return sprite;
    }

    private static Type RequireRuntimeType(string name) => RuntimeReflectionUtility.FindType(name)
        ?? throw new MediaPendingException("游戏图片运行时类型尚未加载。");

    private static void AddItem(IDictionary<string, GameMediaKey> items, GameMediaKey value)
    {
        if (value.Id < 0 || string.IsNullOrWhiteSpace(value.Name))
            throw new InvalidOperationException("图片目录包含无效身份或名称。");
        if (items.TryGetValue(value.Key, out var previous) && previous != value)
            throw new InvalidOperationException("同一图片身份存在冲突目录条目。");
        items[value.Key] = value;
    }

    private void RequireMainThread()
    {
        if (Environment.CurrentManagedThreadId != _mainThreadId)
            throw new InvalidOperationException("图片提取只能在创建提取器的 Controller 主线程执行。");
    }

    private static string DescribeError(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: { } inner }) exception = inner;
        // 文件异常会在 Message 中包含本机路径，不能穿透到提供给局域网客户端的状态接口。
        return exception is IOException or UnauthorizedAccessException
            ? "游戏资源索引读取失败。" : exception.Message;
    }

    private sealed class MediaPendingException : Exception { internal MediaPendingException(string message) : base(message) { } }
    private sealed class MediaMissingException : Exception { internal MediaMissingException(string message) : base(message) { } }
    private sealed record MediaLayer(Sprite Sprite, float X, float Y, float Width, float Height);
}
