using MystiaStewardCompanion.Save;

try
{
    AssertRareGuestInvitationIdentityProjection();
    AssertMappedAliasesShareOneCanonicalCandidate();
    AssertBepInEx783KeyedLookupMetadata();
    AssertExactDictionaryLookup();
    AssertSchedulerCharacterIdentityField();
    AssertTrackedNpcAvailabilityTruthTable();
    AssertQueuedWritesRejectStaleDaySceneContext();
    AssertRareGuestInvitationSourceIsPassive();
    Console.WriteLine(
        "PASS: rare guest invitation listing uses exact passive dictionary lookups, "
        + "queued writes reject stale day-scene contexts, and compatibility paths stay absent.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertMappedAliasesShareOneCanonicalCandidate()
{
    var guest = new object();
    var candidates = new[]
    {
        CreateCandidate(
            guest,
            id: 10,
            runtimeId: 10,
            runtimeName: "Marisa",
            sceneLabel: "",
            isCurrentScene: false,
            availabilityKnown: false,
            runtimeAvailable: true),
        CreateCandidate(
            guest,
            id: 10,
            runtimeId: 1100,
            runtimeName: "DLC1_Marisa",
            sceneLabel: "DLC1_MagicForest",
            isCurrentScene: true,
            availabilityKnown: true,
            runtimeAvailable: true),
        CreateCandidate(
            guest,
            id: 10,
            runtimeId: 2100,
            runtimeName: "DLC2_Marisa",
            sceneLabel: "DLC2_TestMap",
            isCurrentScene: false,
            availabilityKnown: true,
            runtimeAvailable: false),
    };

    var merged = RuntimeRareGuestInvitationCandidates.Deduplicate(candidates);
    AssertEqual(1, merged.Count, "Mapped aliases produced duplicate native invitation candidates.");
    AssertEqual(
        10,
        merged[0].CanonicalGuestId,
        "The merged candidate did not keep its canonical invitation ID.");
    AssertEqual(
        1100,
        merged[0].RuntimeId,
        "The visible current-scene alias was not selected for runtime presentation.");
    AssertEqual(
        "DLC1_Marisa",
        merged[0].RuntimeName,
        "The merged candidate lost its current runtime identity.");
    AssertTrue(merged[0].RuntimeAvailable, "An unavailable alias disabled an available canonical guest.");
    AssertEqual(
        2,
        merged[0].SceneLabels.Count,
        "Mapped alias scene labels were not merged deterministically.");

    var allSceneMerged = RuntimeRareGuestInvitationCandidates.Deduplicate(candidates
        .Select(candidate => candidate with
        {
            IsCurrentScene = false,
            AvailabilityKnown = false,
            RuntimeAvailable = true,
        }));
    AssertEqual(
        10,
        allSceneMerged[0].RuntimeId,
        "All-scenes deduplication did not prefer the base identity when availability was equal.");

    var mappedOnly = candidates
        .Where(candidate => candidate.RuntimeId != candidate.CanonicalGuestId)
        .Select(candidate => candidate with
        {
            IsCurrentScene = false,
            AvailabilityKnown = false,
            RuntimeAvailable = true,
        })
        .ToArray();
    var mappedOnlyForward = RuntimeRareGuestInvitationCandidates.Deduplicate(mappedOnly);
    var mappedOnlyReverse = RuntimeRareGuestInvitationCandidates.Deduplicate(mappedOnly.Reverse());
    AssertEqual(1, mappedOnlyForward.Count, "Mapped-only aliases were not merged.");
    AssertEqual(
        1100,
        mappedOnlyForward[0].RuntimeId,
        "Mapped-only deduplication did not select the stable lowest runtime ID.");
    AssertEqual(
        mappedOnlyForward[0].RuntimeId,
        mappedOnlyReverse[0].RuntimeId,
        "Mapped alias deduplication depends on catalog enumeration order.");
}

static RuntimeRareGuestInvitationCandidate CreateCandidate(
    object guest,
    int id,
    int runtimeId,
    string runtimeName,
    string sceneLabel,
    bool isCurrentScene,
    bool availabilityKnown,
    bool runtimeAvailable)
{
    return new RuntimeRareGuestInvitationCandidate(
        Guest: guest,
        CanonicalGuestId: id,
        RuntimeId: runtimeId,
        RuntimeName: runtimeName,
        DisplayName: "Guest",
        SceneLabels: string.IsNullOrWhiteSpace(sceneLabel)
            ? Array.Empty<string>()
            : new[] { sceneLabel },
        SceneNames: Array.Empty<string>(),
        IsCurrentScene: isCurrentScene,
        AvailabilityKnown: availabilityKnown,
        RuntimeAvailable: runtimeAvailable,
        AvailabilityReason: runtimeAvailable ? "" : "unavailable",
        KizunaStateKnown: true,
        KizunaLevel: 5);
}

static void AssertRareGuestInvitationIdentityProjection()
{
    var baseIdentity = RuntimeRareGuestInvitationIdentity.Resolve(5, 5, 5, "BaseGuest");
    AssertEqual(5, baseIdentity.RuntimeId, "A base guest changed its runtime ID.");
    AssertEqual(5, baseIdentity.CanonicalGuestId, "A base guest changed its canonical ID.");

    var mappedIdentity = RuntimeRareGuestInvitationIdentity.Resolve(
        1100,
        10,
        10,
        "DLC1_Marisa");
    AssertEqual(1100, mappedIdentity.RuntimeId, "A mapped guest lost its runtime ID.");
    AssertEqual(
        10,
        mappedIdentity.CanonicalGuestId,
        "A mapped guest did not retain its source/native invitation ID.");

    AssertThrows<InvalidOperationException>(
        () => RuntimeRareGuestInvitationIdentity.Resolve(1100, 10, 1100, "DLC1_Marisa"),
        "A mapped runtime ID was accepted as the native NPC character ID.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeRareGuestInvitationIdentity.Resolve(1100, 10, 11, "DLC1_Marisa"),
        "An unrelated NPC character ID was accepted for a mapped guest.");
}

static void AssertQueuedWritesRejectStaleDaySceneContext()
{
    foreach (var scope in new[] { "current", "all" })
    {
        var expected = new RareGuestInvitationWriteExpectation(7, "YoukaiTrail");
        AssertEqual(
            0,
            ExecuteQueuedWrite(scope, expected, 8, "YoukaiTrail"),
            $"{scope} invitation wrote after the day-scene generation changed.");
        AssertEqual(
            0,
            ExecuteQueuedWrite(scope, expected, 7, "HumanVillage"),
            $"{scope} invitation wrote after the current map changed.");
        AssertEqual(
            0,
            ExecuteQueuedWrite(scope, default, 7, "YoukaiTrail"),
            $"{scope} invitation accepted an old request without the context contract.");
        AssertEqual(
            1,
            ExecuteQueuedWrite(scope, expected, 7, "YoukaiTrail"),
            $"{scope} invitation rejected its exact current context.");
    }
}

static int ExecuteQueuedWrite(
    string scope,
    RareGuestInvitationWriteExpectation expected,
    long actualGeneration,
    string actualMapLabel)
{
    AssertTrue(
        scope is "current" or "all",
        "The queued-write smoke must exercise both supported invitation scopes.");
    var writes = 0;
    if (RuntimeRareGuestInvitationWriteGuard.Matches(
            expected,
            actualGeneration,
            actualMapLabel,
            out _))
    {
        writes++;
    }

    return writes;
}

static void AssertBepInEx783KeyedLookupMetadata()
{
    var dictionaryType = typeof(Il2CppSystem.Collections.Generic.Dictionary<string, string>);
    AssertEqual(
        "Il2CppSystem.Collections.Generic.Dictionary`2",
        dictionaryType.GetGenericTypeDefinition().FullName,
        "BepInEx 783 dictionary wrapper changed.");
    AssertEqual(
        typeof(bool),
        RequireMethod(dictionaryType, "ContainsKey", typeof(string)).ReturnType,
        "BepInEx 783 dictionary ContainsKey signature changed.");
    AssertEqual(
        typeof(string),
        RequireMethod(dictionaryType, "get_Item", typeof(string)).ReturnType,
        "BepInEx 783 dictionary indexer signature changed.");
}

static void AssertTrackedNpcAvailabilityTruthTable()
{
    var baseline = new RuntimeTrackedNpcAvailabilityInput(
        HasOverridePosition: false,
        HasNormalIdentity: true,
        ShouldShowSpecialGuestsInDay: false,
        CurrentSpawnMarker: "YoukaiTrail_Npc_01",
        HiddenSpawnMarker: "HiddenCharacterPosition",
        OpenStatus: true,
        RestDays: 0,
        ShowTimeStart: 10,
        ShowTimeEnd: 18,
        RemainActions: 8);

    AssertTrue(
        RuntimeTrackedNpcAvailability.Evaluate(baseline),
        "A visible tracked NPC inside its native action window was rejected.");
    AssertTrue(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with
        {
            HasOverridePosition = true,
            HasNormalIdentity = false,
            CurrentSpawnMarker = "HiddenCharacterPosition",
            OpenStatus = false,
            RestDays = 5,
            RemainActions = 99,
        }),
        "An override-position NPC must remain visible regardless of the normal gates.");
    AssertFalse(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with
        {
            HasNormalIdentity = false,
            ShouldShowSpecialGuestsInDay = false,
        }),
        "A special NPC bypassed the global special-guest visibility gate.");
    AssertTrue(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with
        {
            HasNormalIdentity = false,
            ShouldShowSpecialGuestsInDay = true,
        }),
        "The global special-guest visibility gate did not admit a mapped NPC.");
    AssertFalse(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with
        {
            CurrentSpawnMarker = "HiddenCharacterPosition",
        }),
        "NPC.defaultDestination was treated as a visible destination.");
    AssertFalse(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with { OpenStatus = false }),
        "A closed tracked NPC was treated as visible.");
    AssertFalse(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with { RestDays = 1 }),
        "A resting tracked NPC was treated as visible.");
    AssertFalse(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with { RemainActions = 17 }),
        "A tracked NPC was visible before its native show-time window.");
    AssertTrue(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with { RemainActions = 16 }),
        "The upper native show-time boundary was excluded.");
    AssertTrue(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with { RemainActions = 0 }),
        "The lower native show-time boundary was excluded.");
    AssertFalse(
        RuntimeTrackedNpcAvailability.Evaluate(baseline with { RemainActions = -1 }),
        "A tracked NPC was visible after its native show-time window.");
}

static void AssertSchedulerCharacterIdentityField()
{
    AssertFalse(
        RuntimeSchedulerCharacterIdentity.IsNormal(
            new FakeSchedulerCharacter(FakeSceneIdentity.Special, 10)),
        "SceneDirector.Identity.Special was treated as a normal NPC identity.");
    AssertTrue(
        RuntimeSchedulerCharacterIdentity.IsNormal(
            new FakeSchedulerCharacter(FakeSceneIdentity.Normal, 10)),
        "SceneDirector.Identity.Normal was not recognized.");
    AssertThrows<MissingMemberException>(
        () => RuntimeSchedulerCharacterIdentity.IsNormal(
            new FakeSchedulerCharacterProperty(FakeSceneIdentity.Normal)),
        "A characterIdentity property was accepted in place of the exact struct field.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeSchedulerCharacterIdentity.IsNormal(
            new FakeSchedulerCharacterWrongType(1)),
        "A non-enum characterIdentity field was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeSchedulerCharacterIdentity.IsNormal(
            new FakeSchedulerCharacter((FakeSceneIdentity)2, 10)),
        "An unknown SceneDirector.Identity value was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeSchedulerCharacterIdentity.IsNormal(
            new FakeSchedulerCharacterReference(FakeSceneIdentity.Normal)),
        "A reference wrapper was accepted in place of the boxed SchedulerNode.Character.");
}

static void AssertExactDictionaryLookup()
{
    var currentMap = new Dictionary<string, Dictionary<string, FakeTrackedNpc>>
    {
        ["YoukaiTrail"] = new Dictionary<string, FakeTrackedNpc>
        {
            ["Chen"] = new FakeTrackedNpc("Chen"),
        },
    };

    AssertTrue(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            currentMap,
            "YoukaiTrail",
            out var rawMap,
            out var foundMap,
            out var outerFailure),
        "A concrete outer dictionary was rejected.");
    AssertEqual(
        RuntimeCollectionReadFailure.None,
        outerFailure,
        "A concrete outer dictionary reported a failure.");
    AssertTrue(foundMap, "An existing outer dictionary key was not found.");
    AssertTrue(rawMap is Dictionary<string, FakeTrackedNpc>, "The outer dictionary value changed type.");

    var map = (Dictionary<string, FakeTrackedNpc>)rawMap!;
    AssertTrue(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            map,
            "Chen",
            out var rawNpc,
            out var foundNpc,
            out var innerFailure),
        "A concrete inner dictionary was rejected.");
    AssertEqual(
        RuntimeCollectionReadFailure.None,
        innerFailure,
        "A concrete inner dictionary reported a failure.");
    AssertTrue(foundNpc, "An existing inner dictionary key was not found.");
    AssertSame(map["Chen"], rawNpc, "The exact keyed lookup changed the dictionary value.");

    AssertTrue(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            map,
            "Missing",
            out var missingValue,
            out var foundMissing,
            out var missingFailure),
        "A missing key was treated as a dictionary read failure.");
    AssertEqual(
        RuntimeCollectionReadFailure.None,
        missingFailure,
        "A missing key reported a collection failure.");
    AssertFalse(foundMissing, "A missing key was reported as present.");
    AssertEqual<object?>(null, missingValue, "A missing key returned stale data.");

    AssertFalse(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            map,
            7,
            out _,
            out _,
            out var keyFailure),
        "A wrong CLR key type was accepted.");
    AssertEqual(
        RuntimeCollectionReadFailure.ElementTypeMismatch,
        keyFailure,
        "A wrong CLR key type reported the wrong failure.");

    AssertFalse(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            new SortedDictionary<string, FakeTrackedNpc>(),
            "Chen",
            out _,
            out _,
            out var shapeFailure),
        "A compatibility dictionary shape was accepted.");
    AssertEqual(
        RuntimeCollectionReadFailure.UnsupportedShape,
        shapeFailure,
        "A compatibility dictionary shape reported the wrong failure.");

    AssertFalse(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            null,
            "Chen",
            out _,
            out _,
            out var missingDictionaryFailure),
        "A missing dictionary was accepted.");
    AssertEqual(
        RuntimeCollectionReadFailure.Missing,
        missingDictionaryFailure,
        "A missing dictionary reported the wrong failure.");

    var npcDictionary = new Dictionary<string, FakeNpcStruct>
    {
        ["Chen"] = new FakeNpcStruct(12, "Chen"),
    };
    AssertTrue(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            npcDictionary,
            "Chen",
            out var boxedNpc,
            out var foundBoxedNpc,
            out var boxedNpcFailure),
        "A dictionary containing a non-blittable struct was rejected.");
    AssertEqual(
        RuntimeCollectionReadFailure.None,
        boxedNpcFailure,
        "A non-blittable struct dictionary reported a failure.");
    AssertTrue(foundBoxedNpc, "A non-blittable struct dictionary key was not found.");
    AssertEqual(
        npcDictionary["Chen"],
        (FakeNpcStruct)boxedNpc!,
        "A keyed read changed a boxed non-blittable struct value.");

    var kizunaDictionary = new Dictionary<int, FakeTrackedNpc>
    {
        [12] = new FakeTrackedNpc("Chen"),
    };
    AssertTrue(
        RuntimeConcreteCollectionReader.TryGetDictionaryValue(
            kizunaDictionary,
            12,
            out var kizunaData,
            out var foundKizuna,
            out var kizunaFailure),
        "An Int32-keyed dictionary was rejected.");
    AssertEqual(
        RuntimeCollectionReadFailure.None,
        kizunaFailure,
        "An Int32-keyed dictionary reported a failure.");
    AssertTrue(foundKizuna, "An existing Int32 dictionary key was not found.");
    AssertSame(kizunaDictionary[12], kizunaData, "An Int32-keyed lookup changed its value.");
}

static void AssertRareGuestInvitationSourceIsPassive()
{
    var source = File.ReadAllText(FindRepositoryFile(
        "mods",
        "bepinex",
        "src",
        "Save",
        "RuntimeRareGuestInvitationService.cs"));

    foreach (var forbidden in new[]
             {
                 "TryRefreshNPCs",
                 "GetMapNPCs",
                 "RefTrackedNPCAvailability",
                 "GetOrGenerateSpecialNPCKizunaLevel",
                 "GetAllNPCKeys",
                 "AllMappedNPCsMapping",
                 "AllNPCsMapping",
                 "FindUnityObjectsIncludingInactive",
                 "RuntimeReflectionUtility.EnumerateObjects",
                 "EnumerateViaEnumerator",
                 "RuntimeReflectionUtility.GetSingletonInstance",
                 "GetGenericSingletonInstance",
                 "RuntimeReflectionUtility.FindUnityObject",
                 "\"RefNPC\"",
                 "\"ShouldShown\"",
                 "\"None\"",
                 "ReadRequiredStaticField",
                 "GetMapLabelFromSpawnMarker",
             })
    {
        AssertAbsent(
            source,
            forbidden,
            $"Rare guest invitation runtime restored forbidden path '{forbidden}'.");
    }

    foreach (var required in new[]
             {
                 "mappedGuestSnapshot.Entries",
                 "ReadRequiredStaticDictionaryProperty(\n            dataBaseDayType,\n            \"allNPCs\"",
                 "trackedNpcs = ReadRequiredStaticDictionaryProperty(\n                runtimeDaySceneType,\n                \"trackedNPCs\"",
                 "ReadRequiredStaticDictionaryProperty(\n            albumType,\n            \"RecordedSpecialNPCs\"",
                 "IsClosedIl2CppDictionary(valueType, typeof(string), trackedNpcType)",
                 "RuntimeTrackedNpcAvailability.Evaluate",
                 "RuntimeSchedulerCharacterIdentity.IsNormal(identity!)",
                 "ReadRequiredExactStaticPropertyValue(\n            npc.GetType(),\n            \"defaultDestination\"",
                 "ReadRequiredExactStaticBooleanProperty(\n                runTimePlayerDataType",
                 "RuntimeConcreteCollectionReader.TryGetDictionaryValue",
                 "HasNpcDayDestination(\n                    npc,\n                    identity.RuntimeStringId,",
                 "RuntimeConcreteCollectionReader.TryReadReferenceArray(\n                possibleDestinations",
                 "possibleDestinations contains a null entry.",
                 "availabilityErrorCount == candidates.Count",
                 "\"kizuna-uninitialized\"",
                 "if (scope == RareGuestInvitationScope.CurrentScene)",
                 "\"all-day-npcs-keyed\"",
                 "availabilityErrorSamples",
                 "RuntimeRareGuestInvitationIdentity.Resolve(",
                 "RuntimeRareGuestInvitationCandidates.Deduplicate(candidates)",
                 "runtimeCandidates={candidates.Count}; candidates={canonicalCandidates.Count}",
                 "mappedIdentitySamples=",
                 "CanonicalGuestId: invitationIdentity.CanonicalGuestId",
                 "HasNpcInvited(statusTracker, candidate.CanonicalGuestId)",
                 "recordedSpecialNpcs,\n                invitationIdentity.CanonicalGuestId",
                 "candidate => candidate.CanonicalGuestId == guestId",
                 "Id = candidate.CanonicalGuestId",
                 "stage=read-context-or-candidates",
                 "SingletonTypeDefinitionName",
                 "MonoSingletonTypeDefinitionName",
                 "ReadExactSingletonInstance(",
                 "RuntimeRareGuestInvitationWriteGuard.Matches(",
             })
    {
        AssertContains(
            source,
            required,
            $"Rare guest invitation runtime is missing passive keyed-read marker '{required}'.");
    }

    AssertAbsent(
        source,
        "if (isCurrentScene)\n            {\n                availabilityKnown = true;",
        "All-scenes candidates must not inherit the current map's transient visibility filter.");
    AssertContains(
        source,
        "if (scope == RareGuestInvitationScope.CurrentScene && !trackedFound) continue;",
        "Current-scene candidates must be gated by the exact tracked-NPC key.");
    AssertContains(
        source,
        "catch (Exception ex)\n                {\n                    runtimeAvailable = false;",
        "A single tracked-NPC visibility failure must be isolated from the remaining candidates.");
    AssertAbsent(
        source,
        "characterId != runtimeId",
        "Mapped day-scene IDs must not be treated as native invitation character IDs.");
    AssertAbsent(
        source,
        "ReadRequiredExactInstancePropertyValue(identity!, \"characterIdentity\")",
        "The boxed SchedulerNode.Character field was restored as a property read.");
    AssertAbsent(
        source,
        "HasNpcInvited(statusTracker, candidate.RuntimeId)",
        "Mapped runtime IDs must not be used to query native invitation state.");
    AssertAbsent(
        source,
        "RecordInvitedGuest(statusTracker, candidate.RuntimeId)",
        "Mapped runtime IDs must not be written to native invitation state.");
    AssertAbsent(
        source,
        "Id = candidate.RuntimeId",
        "Mapped runtime IDs must not be exposed as native invitation API IDs.");

    var listStart = RequireIndex(source, "private static RareGuestInvitationResult ListAvailableCore(");
    var inviteStart = RequireIndex(source, "private static RareGuestInvitationResult InviteOneCore(", listStart);
    AssertAbsent(
        source[listStart..inviteStart],
        "RecordInvitedGuest",
        "The list-only path writes the invitation state.");
    AssertContains(
        source[inviteStart..],
        "RecordInvitedGuest(statusTracker, candidate.CanonicalGuestId);",
        "Explicit invitation no longer reaches the native write operation.");

    var readinessIndex = RequireIndex(
        source,
        "if (!RuntimeSceneReadinessCapture.CanReadDaySceneRuntime())",
        RequireIndex(source, "public static DaySceneMapInfo ReadCurrentDaySceneMapInfo()"));
    var sceneManagerTypeIndex = RequireIndex(
        source,
        "RuntimeReflectionUtility.FindType(DaySceneSceneManagerTypeName)",
        readinessIndex);
    if (readinessIndex >= sceneManagerTypeIndex)
    {
        throw new InvalidOperationException(
            "The snapshot map read reaches MonoSingleton.Instance before readiness is confirmed.");
    }

    var writeValidationIndex = RequireIndex(
        source,
        "if (!TryValidateWriteExpectation(writeExpectation, out var waitReason))",
        inviteStart);
    var recordIndex = RequireIndex(
        source,
        "RecordInvitedGuest(statusTracker, candidate.CanonicalGuestId);",
        writeValidationIndex);
    if (writeValidationIndex >= recordIndex)
    {
        throw new InvalidOperationException(
            "An invitation write can reach RecordInvitedGuest before its context is revalidated.");
    }

    var overlaySource = File.ReadAllText(FindRepositoryFile(
        "mods",
        "bepinex",
        "src",
        "Ui",
        "StewardOverlayController.cs"));
    foreach (var marker in new[]
             {
                 "public RareGuestInvitationWriteExpectation WriteExpectation { get; init; }",
                 "WriteExpectation = writeExpectation",
                 "pending.WriteExpectation",
                 "RuntimeRareGuestInvitationService.TryValidateWriteExpectation(",
             })
    {
        AssertContains(
            overlaySource,
            marker,
            $"Queued invitation writes are missing context marker '{marker}'.");
    }

    var serverSource = File.ReadAllText(FindRepositoryFile(
        "mods",
        "bepinex",
        "src",
        "LocalApi",
        "LocalApiServer.cs"));
    AssertContains(
        serverSource,
        "ReadLongQuery(query, \"expectedDaySceneGeneration\", 0)",
        "The Local API does not reject writes missing a positive day-scene generation.");
    AssertContains(
        serverSource,
        "ReadStringQuery(query, \"expectedMapLabel\")",
        "The Local API does not carry the expected day map.");
}

static string FindRepositoryFile(params string[] pathParts)
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
         directory != null;
         directory = directory.Parent)
    {
        var candidate = Path.Combine(new[] { directory.FullName }.Concat(pathParts).ToArray());
        if (File.Exists(candidate)) return candidate;
    }

    throw new FileNotFoundException(
        $"Could not locate repository file {Path.Combine(pathParts)}.");
}

static int RequireIndex(string source, string marker, int startIndex = 0)
{
    var index = source.IndexOf(marker, startIndex, StringComparison.Ordinal);
    return index >= 0
        ? index
        : throw new InvalidOperationException($"Missing source marker '{marker}'.");
}

static System.Reflection.MethodInfo RequireMethod(
    Type type,
    string methodName,
    params Type[] parameterTypes)
{
    var matches = type
        .GetMethods(System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Instance)
        .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
        .Where(method =>
        {
            var parameters = method.GetParameters();
            return parameters.Length == parameterTypes.Length
                && parameters
                    .Select(parameter => parameter.ParameterType)
                    .SequenceEqual(parameterTypes);
        })
        .ToList();
    return matches.Count == 1
        ? matches[0]
        : throw new InvalidOperationException(
            $"Expected one {type.FullName}.{methodName}, found {matches.Count}.");
}

static void AssertContains(string source, string value, string message)
{
    if (!source.Contains(value, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertAbsent(string source, string value, string message)
{
    if (source.Contains(value, StringComparison.Ordinal))
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertSame(object expected, object? actual, string message)
{
    if (!ReferenceEquals(expected, actual)) throw new InvalidOperationException(message);
}

static void AssertFalse(bool value, string message)
{
    if (value) throw new InvalidOperationException(message);
}

static void AssertTrue(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

internal sealed record FakeTrackedNpc(string Label);

internal readonly record struct FakeNpcStruct(int Id, string Label);

internal enum FakeSceneIdentity
{
    Special,
    Normal,
}

internal readonly struct FakeSchedulerCharacter
{
    public FakeSchedulerCharacter(FakeSceneIdentity identity, int id)
    {
        characterIdentity = identity;
        characterId = id;
    }

    public readonly FakeSceneIdentity characterIdentity;
    public readonly int characterId;
}

internal readonly struct FakeSchedulerCharacterProperty
{
    public FakeSchedulerCharacterProperty(FakeSceneIdentity identity)
    {
        characterIdentity = identity;
    }

    public FakeSceneIdentity characterIdentity { get; }
}

internal readonly struct FakeSchedulerCharacterWrongType
{
    public FakeSchedulerCharacterWrongType(int identity)
    {
        characterIdentity = identity;
    }

    public readonly int characterIdentity;
}

internal sealed class FakeSchedulerCharacterReference
{
    public FakeSchedulerCharacterReference(FakeSceneIdentity identity)
    {
        characterIdentity = identity;
    }

    public readonly FakeSceneIdentity characterIdentity;
}
