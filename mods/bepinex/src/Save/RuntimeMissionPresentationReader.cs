using System.Reflection;

namespace MystiaStewardCompanion.Save;

/// <summary>将任务接收角色投影为名称和可能出现场景；展示元数据不可用时不替代或改写任务完成事实。</summary>
internal static class RuntimeMissionPresentationReader
{
    private const string DataBaseDayTypeName =
        "GameData.Core.Collections.DaySceneUtility.DataBaseDay";
    private const string NpcTypeName =
        "GameData.Core.Collections.DaySceneUtility.Collections.NPC";
    private const string DestinationTypeName =
        "GameData.Core.Collections.DaySceneUtility.Collections.NPC+Destination";
    private const string SchedulerCharacterTypeName =
        "GameData.Profile.SchedulerNode+Character";
    private const string MapNodeTypeName =
        "GameData.Profile.DaySceneMapProfile+MapNode";
    private const string DaySceneLanguageTypeName =
        "GameData.CoreLanguage.Collections.DaySceneLanguage";
    private const string LanguageBaseTypeName = "GameData.CoreLanguage.LanguageBase";
    private const string Il2CppDictionaryTypeName =
        "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppReferenceArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1";
    private const string Il2CppStringArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray";
    private const int MaxReceiverCount = 4096;
    private const int MaxMapCount = 4096;
    private const int MaxDestinationCount = 4096;
    private const int MaxMarkerCount = 65_536;
    private const int MaxIdentityLength = RuntimeMissionPresentation.MaxReceiverLength;

    private static readonly object ShapeRoot = new();
    private static readonly object MapCatalogCacheRoot = new();
    private static RuntimeMissionPresentationShape? _shape;
    private static ManagedMapCatalogCache? _mapCatalogCache;

    /// <summary>按精确角色标签去重读取展示数据，逐角色返回成功或不可用状态；公共反射形状缺失时整批降级。</summary>
    /// <remarks>调用方必须位于 Unity 主线程，本方法不切换线程；返回及长期缓存只包含托管值。</remarks>
    public static IReadOnlyDictionary<string, RuntimeMissionPresentation> ReadMany(
        IReadOnlyList<string> receiverLabels,
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot,
        long missionGeneration,
        long daySceneGeneration)
    {
        ArgumentNullException.ThrowIfNull(receiverLabels);
        ArgumentNullException.ThrowIfNull(mappedGuestSnapshot);

        var receivers = receiverLabels
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static receiver => receiver, StringComparer.Ordinal)
            .ToArray();
        if (missionGeneration < 1
            || daySceneGeneration < 1
            || receivers.Length > MaxReceiverCount
            || receivers.Any(receiver =>
                string.IsNullOrWhiteSpace(receiver)
                || receiver.Length > MaxIdentityLength))
        {
            throw new InvalidOperationException("mission-presentation-receiver-domain-invalid");
        }

        if (receivers.Length == 0)
        {
            return new Dictionary<string, RuntimeMissionPresentation>(
                StringComparer.Ordinal);
        }

        RuntimeMissionPresentationShape shape;
        try
        {
            shape = GetShape();
        }
        catch
        {
            return UnavailableForAll(
                receivers,
                RuntimeMissionPresentation.ShapeUnavailableStatus);
        }

        object? allNpcs = null;
        object? normalNpcLanguage = null;
        object? mapLanguageData = null;
        MapCatalogReadResult mapCatalog;
        try
        {
            allNpcs = shape.AllNpcs.GetValue(null);
        }
        catch
        {
            // NPC 目录缺失不直接终止整批，后续仍可保留能够独立从映射目录读取的角色名称。
        }
        try
        {
            normalNpcLanguage = shape.NormalNpcLanguage.GetValue(null);
        }
        catch
        {
            // 普通角色语言表不可用不影响从已映射目录读取稀客名称。
        }
        try
        {
            mapLanguageData = shape.MapLanguageData.GetValue(null);
        }
        catch
        {
            // 缺少精确本地化名称时不以内部场景标签代替显示名称。
        }
        try
        {
            mapCatalog = ReadMapCatalogCached(
                shape,
                missionGeneration,
                daySceneGeneration,
                mappedGuestSnapshot.CapturedAtUtc);
        }
        catch
        {
            mapCatalog = MapCatalogReadResult.Unavailable;
        }

        var result = new Dictionary<string, RuntimeMissionPresentation>(
            receivers.Length,
            StringComparer.Ordinal);
        foreach (var receiver in receivers)
        {
            try
            {
                result.Add(
                    receiver,
                    ReadOne(
                        shape,
                        receiver,
                        mappedGuestSnapshot,
                        allNpcs,
                        normalNpcLanguage,
                        mapLanguageData,
                        mapCatalog));
            }
            catch
            {
                result.Add(
                    receiver,
                    new RuntimeMissionPresentation(
                        receiver,
                        CharacterName: "",
                        SceneNames: Array.Empty<string>(),
                        PresentationStatus:
                            RuntimeMissionPresentation.EntryReadUnavailableStatus));
            }
        }
        return result;
    }

    /// <summary>分别解析角色名和场景名，并保留可独立确认的名称；整体状态仍明确反映任一部分的失败。</summary>
    /// <remarks>NPC 键或类型冲突时不继续从该对象读取目的地，避免将同名但身份不符的对象用于展示。</remarks>
    private static RuntimeMissionPresentation ReadOne(
        RuntimeMissionPresentationShape shape,
        string receiver,
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot,
        object? allNpcs,
        object? normalNpcLanguage,
        object? mapLanguageData,
        MapCatalogReadResult mapCatalog)
    {
        object? npc = null;
        var npcFailure = "";
        if (allNpcs == null)
        {
            npcFailure = "npc-catalog";
        }
        else if (!TryGetDictionaryValue(
                allNpcs,
                receiver,
                out npc,
                out var npcFound)
            || !npcFound
            || npc == null)
        {
            npcFailure = "npc-missing";
            npc = null;
        }
        else
        {
            try
            {
                if (npc.GetType() != shape.NpcType
                    || shape.NpcKey.GetValue(npc) is not string npcKey
                    || !string.Equals(
                        npcKey,
                        receiver,
                        StringComparison.Ordinal))
                {
                    npcFailure = "npc-identity";
                    npc = null;
                }
            }
            catch
            {
                npcFailure = "npc-identity";
                npc = null;
            }
        }

        string characterName;
        string characterFailure;
        try
        {
            characterName = ReadCharacterName(
                shape,
                receiver,
                npc,
                mappedGuestSnapshot,
                normalNpcLanguage,
                out characterFailure);
        }
        catch
        {
            characterName = "";
            characterFailure = "entry-read";
        }

        IReadOnlyList<string> sceneNames;
        string sceneFailure;
        try
        {
            sceneNames = ReadSceneNames(
                shape,
                npc,
                mapLanguageData,
                mapCatalog,
                out sceneFailure);
        }
        catch
        {
            sceneNames = Array.Empty<string>();
            sceneFailure = "entry-read";
        }
        if (sceneFailure.Length == 0 && npcFailure.Length > 0)
        {
            sceneFailure = npcFailure;
        }

        var status = characterFailure.Length > 0
            ? $"unavailable:{characterFailure}"
            : sceneFailure.Length > 0
                ? $"unavailable:{sceneFailure}"
                : RuntimeMissionPresentation.ReadyStatus;
        return new RuntimeMissionPresentation(
            receiver,
            characterName,
            sceneNames,
            status);
    }

    /// <summary>优先使用唯一映射目录项；存在 NPC 时交叉核对规范角色 ID，没有映射项才查普通角色语言表。</summary>
    /// <remarks>重复或不完整的映射属于身份错误，不能回退到普通角色语言表掩盖冲突。</remarks>
    private static string ReadCharacterName(
        RuntimeMissionPresentationShape shape,
        string receiver,
        object? npc,
        RuntimeMappedGuestCatalogSnapshot mappedGuestSnapshot,
        object? normalNpcLanguage,
        out string failure)
    {
        failure = "";
        var mappedMatches = mappedGuestSnapshot.Entries
            .Where(entry => string.Equals(
                entry.RuntimeStringId,
                receiver,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        if (mappedMatches.Length > 0)
        {
            var mapped = mappedMatches[0];
            if (mappedMatches.Length != 1
                || mapped.SourceGuestId is not int sourceGuestId
                || sourceGuestId < 0
                || string.IsNullOrWhiteSpace(mapped.SourceDisplayName)
                || mapped.SourceDisplayName.Length
                    > RuntimeMissionPresentation.MaxDisplayNameLength)
            {
                failure = "mapped-identity";
                return "";
            }
            if (npc != null
                && (shape.NpcIdentity.GetValue(npc) is not { } boxedIdentity
                    || boxedIdentity.GetType() != shape.SchedulerCharacterType
                    || shape.CharacterId.GetValue(boxedIdentity) is not int characterId
                    || characterId != sourceGuestId))
            {
                failure = "mapped-identity";
                return "";
            }

            return mapped.SourceDisplayName.Trim();
        }

        if (normalNpcLanguage == null
            || !TryGetDictionaryValue(
                normalNpcLanguage,
                receiver,
                out var rawName,
                out var found)
            || !found
            || rawName is not string name
            || string.IsNullOrWhiteSpace(name)
            || name.Length > RuntimeMissionPresentation.MaxDisplayNameLength)
        {
            failure = "character-name";
            return "";
        }

        return name.Trim();
    }

    /// <summary>将所有可能目的地的出生标记映射到精确地图及本地化名称，按地图标签排序后消除重名。</summary>
    /// <remarks>任一目的地未知、歧义或缺少名称时返回空场景列表及失败原因，不发布看似完整的部分列表。</remarks>
    private static IReadOnlyList<string> ReadSceneNames(
        RuntimeMissionPresentationShape shape,
        object? npc,
        object? mapLanguageData,
        MapCatalogReadResult mapCatalog,
        out string failure)
    {
        failure = "";
        if (npc == null)
        {
            return Array.Empty<string>();
        }

        object? rawDestinations;
        try
        {
            rawDestinations = shape.PossibleDestinations.GetValue(npc);
        }
        catch
        {
            failure = "destinations";
            return Array.Empty<string>();
        }
        if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(
                rawDestinations,
                out var destinations,
                out _)
            || destinations.Count > MaxDestinationCount)
        {
            failure = "destinations";
            return Array.Empty<string>();
        }
        // 已成功读取的空目的地数组是有效结果，不要求地图目录和语言表也已就绪。
        if (destinations.Count == 0)
        {
            return Array.Empty<string>();
        }
        if (!mapCatalog.Available)
        {
            failure = "map-catalog";
            return Array.Empty<string>();
        }
        if (mapLanguageData == null)
        {
            failure = "scene-language";
            return Array.Empty<string>();
        }

        var namesByLabel = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < destinations.Count; index++)
        {
            var destination = destinations[index];
            if (destination == null
                || destination.GetType() != shape.DestinationType
                || shape.SpawnMarker.GetValue(destination) is not string marker
                || string.IsNullOrWhiteSpace(marker))
            {
                failure = FirstFailure(failure, "destination-marker");
                continue;
            }

            if (mapCatalog.AmbiguousMarkers.Contains(marker))
            {
                failure = FirstFailure(failure, "scene-marker-ambiguous");
                continue;
            }
            if (!mapCatalog.LabelByMarker.TryGetValue(marker, out var mapLabel))
            {
                failure = FirstFailure(failure, "scene-marker");
                continue;
            }
            if (!TryGetDictionaryValue(
                    mapLanguageData,
                    mapLabel,
                    out var rawLanguage,
                    out var languageFound)
                || !languageFound
                || rawLanguage == null
                || rawLanguage.GetType() != shape.LanguageBaseType
                || shape.LanguageName.GetValue(rawLanguage) is not string sceneName
                || string.IsNullOrWhiteSpace(sceneName)
                || sceneName.Length > RuntimeMissionPresentation.MaxDisplayNameLength)
            {
                failure = FirstFailure(failure, "scene-name");
                continue;
            }

            namesByLabel[mapLabel] = sceneName.Trim();
            if (namesByLabel.Count > RuntimeMissionPresentation.MaxSceneCount)
            {
                failure = FirstFailure(failure, "scene-count");
            }
        }

        if (failure.Length > 0)
        {
            return Array.Empty<string>();
        }
        return namesByLabel.Values
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>完整读取地图及出生标记，建立托管反向索引；字段无效或累计数量超限使整个目录不可用。</summary>
    private static MapCatalogReadResult ReadMapCatalog(
        RuntimeMissionPresentationShape shape)
    {
        var mapData = shape.MapData.GetValue(null);
        if (!RuntimeConcreteCollectionReader.TryReadDictionary(
                mapData,
                out var maps,
                out _)
            || maps.Count > MaxMapCount)
        {
            return MapCatalogReadResult.Unavailable;
        }

        var labelsByMarker = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguousMarkers = new HashSet<string>(StringComparer.Ordinal);
        var markerCount = 0;
        foreach (var pair in maps)
        {
            if (pair.Key is not string mapLabel
                || string.IsNullOrWhiteSpace(mapLabel)
                || mapLabel.Length > MaxIdentityLength
                || pair.Value == null
                || pair.Value.GetType() != shape.MapNodeType)
            {
                return MapCatalogReadResult.Unavailable;
            }
            if (!RuntimeConcreteCollectionReader.TryReadStringArray(
                    shape.MapSpawnMarkerLabels.GetValue(pair.Value),
                    out var markers,
                    out _))
            {
                return MapCatalogReadResult.Unavailable;
            }
            markerCount = checked(markerCount + markers.Count);
            if (markerCount > MaxMarkerCount)
            {
                return MapCatalogReadResult.Unavailable;
            }

            foreach (var marker in markers)
            {
                if (string.IsNullOrWhiteSpace(marker)
                    || marker.Length > MaxIdentityLength)
                {
                    return MapCatalogReadResult.Unavailable;
                }
                if (ambiguousMarkers.Contains(marker))
                {
                    continue;
                }
                // 同地图重复标记可合并；跨地图冲突必须从唯一映射中删除并持续记为歧义。
                if (labelsByMarker.TryGetValue(marker, out var existing)
                    && !string.Equals(existing, mapLabel, StringComparison.Ordinal))
                {
                    labelsByMarker.Remove(marker);
                    ambiguousMarkers.Add(marker);
                    continue;
                }
                labelsByMarker[marker] = mapLabel;
            }
        }

        return new MapCatalogReadResult(
            Available: true,
            labelsByMarker,
            ambiguousMarkers);
    }

    /// <summary>按任务代次、日间代次和角色目录采集时间缓存成功的托管地图索引，避免同一绑定重复扫描。</summary>
    /// <remarks>失败结果不进入缓存，允许后续展示重试再次读取；锁保护缓存引用，不授予后台访问 Unity 的权限。</remarks>
    private static MapCatalogReadResult ReadMapCatalogCached(
        RuntimeMissionPresentationShape shape,
        long missionGeneration,
        long daySceneGeneration,
        DateTime mappedCapturedAtUtc)
    {
        lock (MapCatalogCacheRoot)
        {
            if (_mapCatalogCache is
                {
                    MissionGeneration: var cachedMissionGeneration,
                    DaySceneGeneration: var cachedDaySceneGeneration,
                    MappedCapturedAtUtc: var cachedMappedCapturedAtUtc,
                } cached
                && cachedMissionGeneration == missionGeneration
                && cachedDaySceneGeneration == daySceneGeneration
                && cachedMappedCapturedAtUtc == mappedCapturedAtUtc)
            {
                return cached.Catalog;
            }
        }

        var catalog = ReadMapCatalog(shape);
        if (!catalog.Available)
        {
            return catalog;
        }
        lock (MapCatalogCacheRoot)
        {
            _mapCatalogCache = new ManagedMapCatalogCache(
                missionGeneration,
                daySceneGeneration,
                mappedCapturedAtUtc,
                catalog);
        }
        return catalog;
    }

    private static bool TryGetDictionaryValue(
        object dictionary,
        object key,
        out object? value,
        out bool found)
    {
        return RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            dictionary,
            key,
            out value,
            out found,
            out _);
    }

    private static IReadOnlyDictionary<string, RuntimeMissionPresentation>
        UnavailableForAll(
            IReadOnlyList<string> receivers,
            string status)
    {
        return receivers.ToDictionary(
            static receiver => receiver,
            receiver => new RuntimeMissionPresentation(
                receiver,
                CharacterName: "",
                SceneNames: Array.Empty<string>(),
                PresentationStatus: status),
            StringComparer.Ordinal);
    }

    private static string FirstFailure(string current, string next)
    {
        return current.Length == 0 ? next : current;
    }

    private static RuntimeMissionPresentationShape GetShape()
    {
        lock (ShapeRoot)
        {
            if (_shape != null) return _shape;

            var dataBaseDayType = RequireType(DataBaseDayTypeName);
            var npcType = RequireType(NpcTypeName);
            var destinationType = RequireType(DestinationTypeName);
            var schedulerCharacterType = RequireType(SchedulerCharacterTypeName);
            var mapNodeType = RequireType(MapNodeTypeName);
            var daySceneLanguageType = RequireType(DaySceneLanguageTypeName);
            var languageBaseType = RequireType(LanguageBaseTypeName);

            _shape = new RuntimeMissionPresentationShape(
                npcType,
                destinationType,
                schedulerCharacterType,
                mapNodeType,
                languageBaseType,
                RequireExactStaticProperty(
                    dataBaseDayType,
                    "allNPCs",
                    propertyType => IsExactClosedGeneric(
                        propertyType,
                        Il2CppDictionaryTypeName,
                        typeof(string),
                        npcType)),
                RequireExactStaticProperty(
                    dataBaseDayType,
                    "mapData",
                    propertyType => IsExactClosedGeneric(
                        propertyType,
                        Il2CppDictionaryTypeName,
                        typeof(string),
                        mapNodeType)),
                RequireExactInstanceProperty(npcType, "key", typeof(string)),
                RequireExactInstanceProperty(
                    npcType,
                    "identity",
                    schedulerCharacterType),
                RequireExactInstanceProperty(
                    npcType,
                    "possibleDestinations",
                    propertyType => IsExactClosedGeneric(
                        propertyType,
                        Il2CppReferenceArrayTypeName,
                        destinationType)),
                RequireExactInstanceProperty(
                    destinationType,
                    "spawnMarker",
                    typeof(string)),
                RequireExactInstanceField(
                    schedulerCharacterType,
                    "characterId",
                    typeof(int)),
                RequireExactInstanceProperty(
                    mapNodeType,
                    "mapSpawnMarkerLabels",
                    propertyType => string.Equals(
                        propertyType.FullName,
                        Il2CppStringArrayTypeName,
                        StringComparison.Ordinal)),
                RequireExactStaticProperty(
                    daySceneLanguageType,
                    "DaySceneNPCLanguage",
                    propertyType => IsExactClosedGeneric(
                        propertyType,
                        Il2CppDictionaryTypeName,
                        typeof(string),
                        typeof(string))),
                RequireExactStaticProperty(
                    daySceneLanguageType,
                    "MapLanguageData",
                    propertyType => IsExactClosedGeneric(
                        propertyType,
                        Il2CppDictionaryTypeName,
                        typeof(string),
                        languageBaseType)),
                RequireExactInstanceProperty(
                    languageBaseType,
                    "Name",
                    typeof(string)));
            return _shape;
        }
    }

    private static Type RequireType(string fullName)
    {
        return RuntimeReflectionUtility.FindType(fullName)
            ?? throw new InvalidOperationException($"{fullName} is not loaded.");
    }

    private static PropertyInfo RequireExactInstanceProperty(
        Type declaringType,
        string propertyName,
        Type propertyType)
    {
        return RequireExactProperty(
            declaringType,
            propertyName,
            isStatic: false,
            candidate => candidate == propertyType);
    }

    private static PropertyInfo RequireExactInstanceProperty(
        Type declaringType,
        string propertyName,
        Func<Type, bool> propertyType)
    {
        return RequireExactProperty(
            declaringType,
            propertyName,
            isStatic: false,
            propertyType);
    }

    private static PropertyInfo RequireExactStaticProperty(
        Type declaringType,
        string propertyName,
        Func<Type, bool> propertyType)
    {
        return RequireExactProperty(
            declaringType,
            propertyName,
            isStatic: true,
            propertyType);
    }

    private static PropertyInfo RequireExactProperty(
        Type declaringType,
        string propertyName,
        bool isStatic,
        Func<Type, bool> propertyType)
    {
        var flags = BindingFlags.Public
            | BindingFlags.DeclaredOnly
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var matches = declaringType
            .GetProperties(flags)
            .Where(property =>
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal)
                    || property.GetIndexParameters().Length != 0
                    || !propertyType(property.PropertyType))
                {
                    return false;
                }
                var getter = property.GetGetMethod(nonPublic: false);
                return getter != null && getter.IsStatic == isStatic;
            })
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new MissingMemberException(
                declaringType.FullName,
                propertyName);
    }

    private static FieldInfo RequireExactInstanceField(
        Type declaringType,
        string fieldName,
        Type fieldType)
    {
        var matches = declaringType
            .GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(field =>
                string.Equals(field.Name, fieldName, StringComparison.Ordinal)
                && field.FieldType == fieldType
                && !field.IsStatic)
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new MissingFieldException(declaringType.FullName, fieldName);
    }

    private static bool IsExactClosedGeneric(
        Type candidate,
        string genericDefinitionFullName,
        params Type[] genericArguments)
    {
        return candidate.IsGenericType
            && string.Equals(
                candidate.GetGenericTypeDefinition().FullName,
                genericDefinitionFullName,
                StringComparison.Ordinal)
            && candidate.GetGenericArguments().SequenceEqual(genericArguments);
    }

    private sealed record RuntimeMissionPresentationShape(
        Type NpcType,
        Type DestinationType,
        Type SchedulerCharacterType,
        Type MapNodeType,
        Type LanguageBaseType,
        PropertyInfo AllNpcs,
        PropertyInfo MapData,
        PropertyInfo NpcKey,
        PropertyInfo NpcIdentity,
        PropertyInfo PossibleDestinations,
        PropertyInfo SpawnMarker,
        FieldInfo CharacterId,
        PropertyInfo MapSpawnMarkerLabels,
        PropertyInfo NormalNpcLanguage,
        PropertyInfo MapLanguageData,
        PropertyInfo LanguageName);

    private sealed record MapCatalogReadResult(
        bool Available,
        IReadOnlyDictionary<string, string> LabelByMarker,
        IReadOnlySet<string> AmbiguousMarkers)
    {
        public static MapCatalogReadResult Unavailable { get; } = new(
            Available: false,
            new Dictionary<string, string>(StringComparer.Ordinal),
            new HashSet<string>(StringComparer.Ordinal));
    }

    private sealed record ManagedMapCatalogCache(
        long MissionGeneration,
        long DaySceneGeneration,
        DateTime MappedCapturedAtUtc,
        MapCatalogReadResult Catalog);
}
