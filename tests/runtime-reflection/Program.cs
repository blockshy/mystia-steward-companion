using System.Reflection;
using System.Reflection.Emit;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MystiaStewardCompanion.Save;

const string typeName = "MystiaStewardCompanion.Tests.LateLoadedProbe";

try
{
    AssertBepInEx783CollectionMetadata();
    AssertRuntimeCoreMappingProjection();
    AssertRuntimeStorageStateProjection();
    AssertConcreteCollectionReader();
    AssertDaySceneReadinessState();

    AssertEqual<Type?>(null, RuntimeReflectionUtility.FindType(typeName), "Unknown type unexpectedly resolved.");

    var assembly = AssemblyBuilder.DefineDynamicAssembly(
        new AssemblyName($"runtime-reflection-smoke-{Guid.NewGuid():N}"),
        AssemblyBuilderAccess.Run);
    var module = assembly.DefineDynamicModule("main");
    var expected = module.DefineType(typeName, TypeAttributes.Public).CreateType();

    AssertEqual(expected, RuntimeReflectionUtility.FindType(typeName), "A type loaded after the first lookup remained negatively cached.");
    AssertEqual(expected, RuntimeReflectionUtility.FindType(typeName), "Successful type lookup was not stable.");

    Console.WriteLine("PASS: BepInEx 783 dictionary/array metadata is exact, core/storage reads fail closed, day-scene readiness is gated, and reflection lookups retry late types.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertBepInEx783CollectionMetadata()
{
    var enumerableType = typeof(Il2CppSystem.Collections.Generic.IEnumerable<int>);
    AssertEqual(
        "Il2CppSystem.Collections.Generic.IEnumerable`1",
        enumerableType.GetGenericTypeDefinition().FullName,
        "BepInEx 783 generic IEnumerable wrapper changed.");
    AssertTrue(
        enumerableType.GetConstructor(new[] { typeof(IntPtr) }) != null,
        "Generic IEnumerable wrapper no longer exposes its native-pointer constructor.");

    var genericEnumeratorType = RequireMethod(enumerableType, "GetEnumerator", Type.EmptyTypes).ReturnType;
    AssertEqual(
        "Il2CppSystem.Collections.Generic.IEnumerator`1",
        genericEnumeratorType.GetGenericTypeDefinition().FullName,
        "Generic IEnumerable did not return the matching generic IEnumerator.");
    AssertTypeArguments(genericEnumeratorType, typeof(int));
    AssertTrue(
        genericEnumeratorType.GetConstructor(new[] { typeof(IntPtr) }) != null,
        "Generic IEnumerator wrapper no longer exposes its native-pointer constructor.");
    AssertEqual(
        typeof(int),
        RequireMethod(genericEnumeratorType, "get_Current", Type.EmptyTypes).ReturnType,
        "Generic IEnumerator Current no longer returns its exact element type.");

    var nonGenericEnumeratorType = typeof(Il2CppSystem.Collections.IEnumerator);
    AssertTrue(
        nonGenericEnumeratorType.GetConstructor(new[] { typeof(IntPtr) }) != null,
        "Non-generic IEnumerator wrapper no longer exposes its native-pointer constructor.");
    AssertEqual(
        typeof(bool),
        RequireMethod(nonGenericEnumeratorType, "MoveNext", Type.EmptyTypes).ReturnType,
        "Non-generic IEnumerator MoveNext is not Boolean.");

    var disposableType = typeof(Il2CppSystem.IDisposable);
    AssertTrue(
        disposableType.GetConstructor(new[] { typeof(IntPtr) }) != null,
        "IL2CPP IDisposable wrapper no longer exposes its native-pointer constructor.");
    AssertEqual(
        typeof(void),
        RequireMethod(disposableType, "Dispose", Type.EmptyTypes).ReturnType,
        "IL2CPP IDisposable Dispose is not Void.");

    var dictionaryType = typeof(Il2CppSystem.Collections.Generic.Dictionary<int, string>);
    AssertEqual(
        "Il2CppSystem.Collections.Generic.Dictionary`2",
        dictionaryType.GetGenericTypeDefinition().FullName,
        "BepInEx 783 dictionary wrapper changed.");
    AssertEqual(typeof(int), RequireProperty(dictionaryType, "Count").PropertyType, "Dictionary Count is not Int32.");

    var getEnumerator = RequireMethod(dictionaryType, "GetEnumerator", Type.EmptyTypes);
    var enumeratorType = getEnumerator.ReturnType;
    AssertEqual(
        "Il2CppSystem.Collections.Generic.Dictionary`2+Enumerator",
        enumeratorType.GetGenericTypeDefinition().FullName,
        "Dictionary GetEnumerator did not return its concrete nested Enumerator.");
    AssertTypeArguments(enumeratorType, typeof(int), typeof(string));
    AssertEqual(typeof(bool), RequireMethod(enumeratorType, "MoveNext", Type.EmptyTypes).ReturnType, "Enumerator MoveNext is not Boolean.");
    AssertEqual(typeof(void), RequireMethod(enumeratorType, "Dispose", Type.EmptyTypes).ReturnType, "Enumerator Dispose is not Void.");

    var keyValuePairType = RequireProperty(enumeratorType, "Current").PropertyType;
    AssertEqual(
        "Il2CppSystem.Collections.Generic.KeyValuePair`2",
        keyValuePairType.GetGenericTypeDefinition().FullName,
        "Enumerator Current did not return the matching concrete KeyValuePair.");
    AssertTypeArguments(keyValuePairType, typeof(int), typeof(string));
    AssertEqual(typeof(int), RequireProperty(keyValuePairType, "Key").PropertyType, "KeyValuePair Key type changed.");
    AssertEqual(typeof(string), RequireProperty(keyValuePairType, "Value").PropertyType, "KeyValuePair Value type changed.");

    AssertArrayMetadata(
        typeof(Il2CppReferenceArray<Il2CppSystem.Object>),
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1",
        typeof(Il2CppSystem.Object));
    AssertArrayMetadata(
        typeof(Il2CppStructArray<int>),
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1",
        typeof(int));
}

static void AssertRuntimeCoreMappingProjection()
{
    var ids = RuntimeCoreMappingProjection.ReadIds(
        new[]
        {
            new RuntimeDictionaryEntry(9, "nine"),
            new RuntimeDictionaryEntry(-1, "internal"),
            new RuntimeDictionaryEntry(1, "one"),
            new RuntimeDictionaryEntry(4, "four"),
        },
        "FoodsMapping",
        RuntimeCoreMappingIdDomain.NonNegativeContent);
    AssertSequenceEqual(
        new[] { 1, 4, 9 },
        ids,
        "Core mapping IDs were not projected in stable order or retained an internal negative ID.");

    var signedIds = RuntimeCoreMappingProjection.ReadIds(
        new[]
        {
            new RuntimeDictionaryEntry(4, "four"),
            new RuntimeDictionaryEntry(-1, "signed"),
        },
        "IzakayasMapping",
        RuntimeCoreMappingIdDomain.Signed);
    AssertSequenceEqual(
        new[] { -1, 4 },
        signedIds,
        "A signed core mapping did not retain its complete ID domain.");

    var maximum = Enumerable.Range(0, 4096)
        .Select(id => new RuntimeDictionaryEntry(id, $"value-{id}"))
        .ToList();
    AssertEqual(
        4096,
        RuntimeCoreMappingProjection.ReadIds(
            maximum,
            "RecipesMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent).Count,
        "The documented core mapping limit was not accepted.");

    AssertThrows<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            Array.Empty<RuntimeDictionaryEntry>(),
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "An empty core mapping was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            maximum.Append(new RuntimeDictionaryEntry(4096, "overflow")).ToList(),
            "RecipesMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "An oversized core mapping was accepted.");
    AssertThrowsContains<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            new[] { new RuntimeDictionaryEntry("1", "one") },
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "System.String instead of Int32",
        "A non-Int32 core mapping key was accepted.");
    AssertThrowsContains<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            new[] { new RuntimeDictionaryEntry(-1, "negative") },
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "no non-negative content IDs",
        "A mapping containing only internal negative IDs was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            new[] { new RuntimeDictionaryEntry(1, " ") },
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "A blank core mapping value was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            new[] { new RuntimeDictionaryEntry(1, 1) },
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "A non-String core mapping value was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            new[]
            {
                new RuntimeDictionaryEntry(1, "one"),
                new RuntimeDictionaryEntry(1, "duplicate"),
            },
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "A duplicate core mapping ID was accepted.");
    AssertThrows<ArgumentException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            ids.Select(id => new RuntimeDictionaryEntry(id, id.ToString())).ToList(),
            " ",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "A blank core mapping name was accepted.");
    AssertThrows<ArgumentNullException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            null!,
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "A null core mapping was accepted.");
    AssertThrows<ArgumentOutOfRangeException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            new[] { new RuntimeDictionaryEntry(1, "one") },
            "FoodsMapping",
            (RuntimeCoreMappingIdDomain)99),
        "An unknown core mapping ID domain was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeCoreMappingProjection.ReadIds(
            new[] { new RuntimeDictionaryEntry(-1, null) },
            "FoodsMapping",
            RuntimeCoreMappingIdDomain.NonNegativeContent),
        "A filtered negative ID bypassed core mapping value validation.");
}

static void AssertRuntimeStorageStateProjection()
{
    var recipeCalls = new List<int>();
    var recipes = RuntimeStorageStateProjection.ReadAvailableRecipeIds(
        new[] { 3, 1, 3, 2 },
        id =>
        {
            recipeCalls.Add(id);
            return id != 1;
        });
    AssertSequenceEqual(new[] { 3, 2 }, recipes,
        "Available recipes did not preserve the catalog order.");
    AssertSequenceEqual(new[] { 3, 1, 2 }, recipeCalls,
        "Duplicate recipe IDs caused repeated runtime reads.");

    var ingredientCalls = new List<int>();
    var ingredients = RuntimeStorageStateProjection.ReadIngredientQuantities(
        new[] { 8, 4, 8, 2 },
        id =>
        {
            ingredientCalls.Add(id);
            return id switch
            {
                8 => -1,
                4 => 0,
                _ => 7,
            };
        });
    AssertEqual(-1, ingredients[8], "Infinite ingredient inventory was not preserved.");
    AssertFalse(ingredients.ContainsKey(4), "Zero ingredient inventory was published.");
    AssertEqual(7, ingredients[2], "Positive ingredient inventory was not published.");
    AssertSequenceEqual(new[] { 8, 4, 2 }, ingredientCalls,
        "Duplicate ingredient IDs caused repeated runtime reads.");

    var beverages = RuntimeStorageStateProjection.ReadBeverageQuantities(
        new[] { 0, 5, 6 },
        id => id switch
        {
            0 => -1,
            5 => 0,
            _ => 9,
        });
    AssertEqual(-1, beverages[0], "Infinite beverage inventory was not preserved.");
    AssertFalse(beverages.ContainsKey(5), "Zero beverage inventory was published.");
    AssertEqual(9, beverages[6], "Positive beverage inventory was not published.");

    AssertThrows<InvalidOperationException>(
        () => RuntimeStorageStateProjection.ReadIngredientQuantities(new[] { 1 }, _ => -2),
        "An ingredient quantity below the exact -1 infinite sentinel was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeStorageStateProjection.ReadBeverageQuantities(new[] { 1 }, _ => -2),
        "A beverage quantity below the exact -1 infinite sentinel was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeStorageStateProjection.ReadAvailableRecipeIds(new[] { -1 }, _ => true),
        "A negative recipe ID was accepted.");
    AssertThrows<InvalidOperationException>(
        () => RuntimeStorageStateProjection.ReadAvailableRecipeIds(Enumerable.Range(0, 4097), _ => false),
        "An oversized runtime recipe catalog was accepted.");
    AssertEqual(
        0,
        RuntimeStorageStateProjection.ReadAvailableRecipeIds(Enumerable.Range(0, 4096), _ => false).Count,
        "The documented runtime catalog limit was not accepted.");
    AssertThrows<ArgumentNullException>(
        () => RuntimeStorageStateProjection.ReadAvailableRecipeIds(null!, _ => true),
        "A null recipe catalog was accepted.");
    AssertThrows<ArgumentNullException>(
        () => RuntimeStorageStateProjection.ReadAvailableRecipeIds(Array.Empty<int>(), null!),
        "A null recipe probe was accepted.");
    AssertThrows<ArgumentNullException>(
        () => RuntimeStorageStateProjection.ReadIngredientQuantities(null!, _ => 0),
        "A null ingredient catalog was accepted.");
    AssertThrows<ArgumentNullException>(
        () => RuntimeStorageStateProjection.ReadBeverageQuantities(Array.Empty<int>(), null!),
        "A null beverage quantity probe was accepted.");
}

static void AssertConcreteCollectionReader()
{
    var dictionary = new Dictionary<int, string>
    {
        [7] = "seven",
    };
    AssertTrue(
        RuntimeConcreteCollectionReader.TryReadDictionary(
            dictionary,
            out var entries,
            out var dictionaryFailure),
        "A concrete dictionary was rejected.");
    AssertEqual(RuntimeCollectionReadFailure.None, dictionaryFailure, "A concrete dictionary reported a failure.");
    AssertEqual(1, entries.Count, "A concrete dictionary returned the wrong entry count.");
    AssertEqual(7, entries[0].Key, "A concrete dictionary changed its key.");
    AssertEqual("seven", entries[0].Value, "A concrete dictionary changed its value.");

    AssertTrue(
        RuntimeConcreteCollectionReader.TryReadDictionary(
            new Dictionary<int, string>(),
            out var emptyEntries,
            out var emptyFailure),
        "An empty concrete dictionary was rejected.");
    AssertEqual(RuntimeCollectionReadFailure.None, emptyFailure, "An empty concrete dictionary reported a failure.");
    AssertEqual(0, emptyEntries.Count, "An empty concrete dictionary returned stale entries.");

    AssertTrue(
        RuntimeConcreteCollectionReader.TryReadIntArray(
            new[] { -1, 0, 7 },
            out var integers,
            out var integerFailure),
        "A concrete Int32 array was rejected.");
    AssertEqual(RuntimeCollectionReadFailure.None, integerFailure, "A concrete Int32 array reported a failure.");
    AssertSequenceEqual(new[] { -1, 0, 7 }, integers, "A concrete Int32 array changed its values.");
    AssertFalse(
        RuntimeConcreteCollectionReader.TryReadIntArray(
            new long[] { 7 },
            out _,
            out var longFailure),
        "A non-Int32 struct array was accepted as an Int32 array.");
    AssertEqual(
        RuntimeCollectionReadFailure.UnsupportedShape,
        longFailure,
        "A non-Int32 struct array reported the wrong failure.");
    AssertFalse(
        RuntimeConcreteCollectionReader.TryReadIntArray(
            null,
            out var nullIntegers,
            out var nullIntegerFailure),
        "A missing Int32 array was accepted.");
    AssertEqual(0, nullIntegers.Count, "A missing Int32 array returned stale values.");
    AssertEqual(
        RuntimeCollectionReadFailure.Missing,
        nullIntegerFailure,
        "A missing Int32 array reported the wrong failure.");

    var first = new FakeReference("first");
    var second = new FakeReference("second");
    FakeReference?[] referenceArray = { first, null, second };
    AssertTrue(
        RuntimeConcreteCollectionReader.TryReadReferenceArray(
            referenceArray,
            out var referenceValues,
            out var referenceFailure),
        "A concrete reference array was rejected.");
    AssertEqual(RuntimeCollectionReadFailure.None, referenceFailure, "A reference array reported a failure.");
    AssertEqual(3, referenceValues.Count, "A reference array returned the wrong item count.");
    AssertSame(first, referenceValues[0], "A reference array changed its first object.");
    AssertEqual<object?>(null, referenceValues[1], "A reference array dropped or replaced a null slot.");
    AssertSame(second, referenceValues[2], "A reference array changed its last object.");

    var boxedStructArray = new[]
    {
        new FakeNonBlittableStruct(1, "first"),
        new FakeNonBlittableStruct(2, "second"),
    };
    AssertTrue(
        RuntimeConcreteCollectionReader.TryReadReferenceArray(
            boxedStructArray,
            out var boxedStructValues,
            out var boxedStructFailure),
        "A non-blittable struct array was rejected instead of being boxed as a reference array.");
    AssertEqual(
        RuntimeCollectionReadFailure.None,
        boxedStructFailure,
        "A non-blittable struct array reported a failure.");
    AssertEqual(
        boxedStructArray[1],
        (FakeNonBlittableStruct)boxedStructValues[1]!,
        "A boxed non-blittable struct changed its value.");

    AssertRejectedReferenceArray(
        null,
        RuntimeCollectionReadFailure.Missing,
        "A missing reference array was accepted.");
    AssertRejectedReferenceArray(
        new object(),
        RuntimeCollectionReadFailure.UnsupportedShape,
        "An arbitrary object was accepted as a reference array.");
    AssertRejectedReferenceArray(
        new[] { 1, 2 },
        RuntimeCollectionReadFailure.UnsupportedShape,
        "A blittable struct array was accepted as a reference array.");
    AssertRejectedReferenceArray(
        new[] { "first", "second" },
        RuntimeCollectionReadFailure.UnsupportedShape,
        "A string array was accepted as a reference array.");

    AssertFalse(
        RuntimeConcreteCollectionReader.TryReadDictionary(
            new SortedDictionary<int, string>(),
            out _,
            out var sortedDictionaryFailure),
        "A SortedDictionary compatibility shape was accepted.");
    AssertEqual(
        RuntimeCollectionReadFailure.UnsupportedShape,
        sortedDictionaryFailure,
        "An unsupported dictionary shape reported the wrong failure.");
    AssertFalse(
        RuntimeConcreteCollectionReader.TryReadDictionary(
            null,
            out var nullEntries,
            out var nullFailure),
        "A missing dictionary was accepted.");
    AssertEqual(0, nullEntries.Count, "A missing dictionary returned stale entries.");
    AssertEqual(RuntimeCollectionReadFailure.Missing, nullFailure, "A missing dictionary reported the wrong failure.");
}

static void AssertRejectedReferenceArray(
    object? source,
    RuntimeCollectionReadFailure expectedFailure,
    string message)
{
    AssertFalse(
        RuntimeConcreteCollectionReader.TryReadReferenceArray(
            source,
            out var values,
            out var failure),
        message);
    AssertEqual(0, values.Count, $"{message} Rejected source returned stale values.");
    AssertEqual(expectedFailure, failure, $"{message} Rejected source reported the wrong failure.");
}

static void AssertDaySceneReadinessState()
{
    var state = new RuntimeDaySceneReadinessState();
    state.OpenPanel();
    AssertFalse(CanReadDaySceneState(state, (nint)101), "Opening the panel unlocked day runtime reads.");
    state.BeginFirstEnter((nint)101, manualWorkReturn: false);
    state.CaptureEnterDayAction((nint)201);
    state.EndFirstEnter();
    AssertFalse(CanReadDaySceneState(state, (nint)101), "The outer day action was not awaited.");

    var enterToken = state.BeginSchedulerFinish((nint)201);
    AssertEqual(RuntimeDaySceneFinishKind.EnterDay, enterToken.Kind, "The outer day action identity was not recognized.");
    var unrelatedToken = state.BeginSchedulerFinish((nint)999);
    AssertEqual(RuntimeDaySceneFinishKind.None, unrelatedToken.Kind, "An unrelated scheduler action was accepted.");
    state.CaptureEnterDayMapAction(
        (nint)999,
        (nint)300,
        hasPendingEnterDayEvent: false,
        mapLabel: "Stale");
    AssertEqual(
        RuntimeDaySceneReadinessPhase.WaitingForMapAction,
        state.Phase,
        "A map action from another manager was accepted.");
    state.CaptureEnterDayMapAction(
        (nint)101,
        (nint)300,
        hasPendingEnterDayEvent: true,
        mapLabel: "Premature");
    AssertEqual(
        RuntimeDaySceneReadinessPhase.WaitingForMapAction,
        state.Phase,
        "A map action captured while the enter-day event was still pending.");
    state.CaptureEnterDayMapAction(
        (nint)101,
        (nint)301,
        hasPendingEnterDayEvent: false,
        mapLabel: "Home");
    var mapToken = state.BeginSchedulerFinish((nint)301);
    AssertEqual(RuntimeDaySceneFinishKind.EnterDayMap, mapToken.Kind, "The final map action identity was not recognized.");
    AssertFalse(CanReadDaySceneState(state, (nint)101), "The final map action unlocked reads before returning.");

    state.CompleteSchedulerFinish(mapToken);
    AssertTrue(CanReadDaySceneState(state, (nint)101), "The matching final map action did not unlock reads.");
    AssertFalse(CanReadDaySceneState(state, (nint)999), "A stale scene manager identity was accepted.");
    AssertFalse(CanReadDaySceneState(state, (nint)101, isMapSwapping: true), "A running map swap was accepted.");
    AssertFalse(CanReadDaySceneState(state, (nint)101, hasPendingEnterDayEvent: true), "An unfinished day event was accepted.");
    AssertFalse(CanReadDaySceneState(state, (nint)101, hasValidMap: false), "An empty current map was accepted.");
    AssertFalse(CanReadDaySceneState(state, (nint)101, runTimeSchedulerIsExecuting: true), "An executing scheduler was accepted.");
    AssertFalse(CanReadDaySceneState(state, (nint)101, sceneDirectorIsInEvent: true), "An active scene event was accepted.");
    AssertFalse(
        CanReadDaySceneState(state, (nint)101, daySceneManagerIsExecutingScheduledActions: true),
        "Executing day-scene actions were accepted.");
    AssertFalse(
        CanReadDaySceneState(state, (nint)101, universalGameManagerIsSwitchScene: true),
        "An active global scene switch was accepted.");

    state.CaptureEnterDayMapAction(
        (nint)101,
        (nint)302,
        hasPendingEnterDayEvent: false,
        mapLabel: "YoukaiMountain");
    var staleMapToken = state.BeginSchedulerFinish((nint)302);
    state.BeginFirstEnter((nint)102, manualWorkReturn: false);
    state.CompleteSchedulerFinish(staleMapToken);
    AssertFalse(CanReadDaySceneState(state, (nint)102), "A stale map callback unlocked a new generation.");

    state.Reset("closed state");
    state.CaptureEnterDayMapAction(
        (nint)103,
        (nint)401,
        hasPendingEnterDayEvent: false,
        mapLabel: "Closed");
    AssertEqual(
        RuntimeDaySceneReadinessPhase.Closed,
        state.Phase,
        "A map action opened a closed panel generation.");

    state.OpenPanel();
    state.BeginFirstEnter((nint)103, manualWorkReturn: false);
    state.EndFirstEnter();
    AssertFalse(
        CanReadDaySceneState(state, (nint)103),
        "A missing scheduler action was treated as a manual-work return.");

    state.Reset("manual work return");
    state.OpenPanel();
    state.BeginFirstEnter((nint)103, manualWorkReturn: true);
    state.EndFirstEnter();
    AssertTrue(CanReadDaySceneState(state, (nint)103), "An explicit manual-work return remained locked.");
    state.Reset("panel destroyed");
    AssertFalse(CanReadDaySceneState(state, (nint)103), "A destroyed panel retained runtime readiness.");

    state.BeginFirstEnter((nint)104, manualWorkReturn: false);
    state.CaptureEnterDayAction((nint)204);
    state.EndFirstEnter();
    var prePanelEnterToken = state.BeginSchedulerFinish((nint)204);
    AssertEqual(
        RuntimeDaySceneFinishKind.EnterDay,
        prePanelEnterToken.Kind,
        "A pre-panel outer action was not recognized.");
    state.CaptureEnterDayMapAction(
        (nint)104,
        (nint)304,
        hasPendingEnterDayEvent: false,
        mapLabel: "Home");
    var prePanelToken = state.BeginSchedulerFinish((nint)304);
    state.CompleteSchedulerFinish(prePanelToken);
    AssertFalse(CanReadDaySceneState(state, (nint)104), "A completed map action unlocked reads before the panel opened.");
    state.OpenPanel();
    AssertTrue(CanReadDaySceneState(state, (nint)104), "A valid pre-panel map action was discarded.");
}

static bool CanReadDaySceneState(
    RuntimeDaySceneReadinessState state,
    nint currentManagerPointer,
    bool isMapSwapping = false,
    bool hasPendingEnterDayEvent = false,
    bool hasValidMap = true,
    bool runTimeSchedulerIsExecuting = false,
    bool sceneDirectorIsInEvent = false,
    bool daySceneManagerIsExecutingScheduledActions = false,
    bool universalGameManagerIsSwitchScene = false)
{
    return state.CanRead(
        currentManagerPointer,
        isMapSwapping,
        hasPendingEnterDayEvent,
        hasValidMap,
        runTimeSchedulerIsExecuting,
        sceneDirectorIsInEvent,
        daySceneManagerIsExecutingScheduledActions,
        universalGameManagerIsSwitchScene);
}

static void AssertArrayMetadata(Type arrayType, string expectedTypeName, Type expectedElementType)
{
    var typeName = arrayType.IsGenericType
        ? arrayType.GetGenericTypeDefinition().FullName
        : arrayType.FullName;
    AssertEqual(expectedTypeName, typeName, "BepInEx 783 array wrapper changed.");
    AssertEqual(typeof(int), RequireProperty(arrayType, "Length").PropertyType, "Array Length is not Int32.");
    AssertEqual(
        expectedElementType,
        RequireMethod(arrayType, "get_Item", new[] { typeof(int) }).ReturnType,
        "Array indexer does not return its exact element type.");
}

static void AssertTypeArguments(Type type, params Type[] expected)
{
    var actual = type.GetGenericArguments();
    AssertEqual(expected.Length, actual.Length, "Closed generic argument count changed.");
    for (var index = 0; index < expected.Length; index++)
    {
        AssertEqual(expected[index], actual[index], $"Closed generic argument {index} changed.");
    }
}

static PropertyInfo RequireProperty(Type type, string name)
{
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    try
    {
        return type.GetProperty(name, flags)
            ?? throw new InvalidOperationException($"Missing property {type.FullName}.{name}.");
    }
    catch (AmbiguousMatchException ex)
    {
        throw new InvalidOperationException($"Ambiguous property {type.FullName}.{name}.", ex);
    }
}

static MethodInfo RequireMethod(Type type, string name, IReadOnlyList<Type> parameterTypes)
{
    const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    var methods = type
        .GetMethods(flags)
        .Where(candidate => candidate.Name == name)
        .Where(candidate =>
        {
            var parameters = candidate.GetParameters();
            if (parameters.Length != parameterTypes.Count) return false;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].ParameterType != parameterTypes[index]) return false;
            }

            return true;
        })
        .ToList();
    return methods.Count == 1
        ? methods[0]
        : throw new InvalidOperationException($"Expected exactly one {type.FullName}.{name}, found {methods.Count}.");
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertSame(object expected, object? actual, string message)
{
    if (!ReferenceEquals(expected, actual))
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertSequenceEqual<T>(IReadOnlyList<T> expected, IReadOnlyList<T> actual, string message)
{
    if (!expected.SequenceEqual(actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected '[{string.Join(",", expected)}]', actual '[{string.Join(",", actual)}]'.");
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

static void AssertThrowsContains<TException>(Action action, string expected, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException ex)
    {
        AssertContains(ex.Message, expected, message);
        return;
    }

    throw new InvalidOperationException(message);
}

static void AssertFalse(bool value, string message)
{
    if (value) throw new InvalidOperationException(message);
}

static void AssertTrue(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}

static void AssertContains(string value, string expected, string message)
{
    if (!value.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{message} Expected '{value}' to contain '{expected}'.");
    }
}

internal sealed record FakeReference(string Label);

internal readonly record struct FakeNonBlittableStruct(int Id, string Label);
