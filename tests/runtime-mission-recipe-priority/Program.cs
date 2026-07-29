using MystiaStewardCompanion.Core;
using MystiaStewardCompanion.Save;
using System.Text.Json;

try
{
    AssertValidSignalBindsToUniqueLiveOrder();
    AssertGlobalRuntimeGatesFailClosed();
    AssertOrderIdentityAndOwnershipAreExact();
    AssertRecipeMappingIsUniqueAndExact();
    AssertConflictingSignalsFailClosed();
    AssertObservedTimeIsNotATtl();
    AssertLifecycleReconciliationRetainsProjectedPriority();
    AssertRepeatedObservationDoesNotChangeBusinessProjection();
    AssertInvalidContextClearsExistingPriority();

    Console.WriteLine(
        "PASS: mission recipe priority binds only to one current ordinary-business order "
        + "with exact generations, identities, and a unique food-to-recipe mapping.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void AssertValidSignalBindsToUniqueLiveOrder()
{
    var sourceOrder = CreateOrder();
    var source = CreateContext(sourceOrder, CreateOrder(
        traceId: "R-0002",
        deskCode: 2,
        guestId: 10,
        runtimeGuestId: 1100));
    var projected = Project(source);
    var priority = projected.Orders[0].MissionRecipePriority;

    AssertNotSame(source, projected, "A projected priority mutated the source context.");
    AssertNull(sourceOrder.MissionRecipePriority, "The source order was mutated.");
    AssertNotNull(priority, "A valid signal did not bind to its live order.");
    AssertEqual("R-0001", priority!.TraceId, "The priority trace ID was not bound by the server.");
    AssertEqual(1, priority.DeskCode, "The priority desk was not bound by the server.");
    AssertEqual(3, priority.GuestId, "The canonical guest ID changed.");
    AssertEqual(1003, priority.RuntimeGuestId, "The raw runtime guest ID changed.");
    AssertEqual(1, priority.FoodId, "The mission food ID changed.");
    AssertEqual(1201, priority.RecipeId, "The mission food did not map to its game recipe ID.");
    AssertEqual(4L, priority.MissionGeneration, "The mission generation changed.");
    AssertEqual(7L, priority.BusinessGeneration, "The business generation changed.");
    AssertNull(
        projected.Orders[1].MissionRecipePriority,
        "A signal was copied to a different canonical/raw guest.");
    AssertOrderFieldsPreserved(sourceOrder, projected.Orders[0]);
}

static void AssertGlobalRuntimeGatesFailClosed()
{
    var context = CreateContext(CreateOrder());
    var catalog = CreateCatalog();
    var lifecycle = CreateLifecycle();
    var serve = CreateServeSnapshot();
    var ordinaryBusiness = CreateOrdinaryBusiness();

    AssertNoPriority(Project(
        context,
        catalog: CopyCatalogWithComplete(catalog, isComplete: false),
        lifecycle: lifecycle,
        missionGeneration: 4,
        serve: serve,
        specialBusiness: ordinaryBusiness), "An incomplete runtime catalog was accepted.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle,
        mission: CreateMissionBoundary() with { RuntimeAvailable = false },
        serve: serve,
        specialBusiness: ordinaryBusiness), "An unavailable mission runtime was accepted.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle,
        mission: CreateMissionBoundary() with
        {
            Ready = false,
        },
        serve: serve,
        specialBusiness: ordinaryBusiness), "A non-ready mission phase was accepted.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle with { Phase = NightBusinessLifecyclePhase.Closing },
        4,
        serve,
        ordinaryBusiness), "Closing retained a task priority.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle with { Generation = 8 },
        4,
        serve,
        ordinaryBusiness), "A stale business generation was accepted.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle,
        5,
        serve,
        ordinaryBusiness), "A stale mission generation was accepted.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle,
        4,
        serve with { HookAttached = false },
        ordinaryBusiness), "An unattached observer produced a priority.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle,
        4,
        serve with { NightPhase = "Closing" },
        ordinaryBusiness), "A non-active observer phase produced a priority.");
    AssertNoPriority(
        RuntimeMissionRecipePriorityProjection.Enrich(
            context,
            catalog,
            lifecycle,
            CreateMissionBoundary(),
            serve,
            specialBusiness: null)!,
        "An unknown special-business state was accepted.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle,
        4,
        serve,
        new SpecialBusinessContext
        {
            Active = true,
            ChallengeType = "Challenge_Yuyuko",
        }), "A special business produced a task priority.");
    AssertNoPriority(Project(
        context,
        catalog,
        lifecycle,
        4,
        serve,
        new SpecialBusinessContext
        {
            Active = false,
            ChallengeType = SpecialBusinessChallengeTypes.NotChallenge,
            Error = "challenge state unavailable",
        }), "An uncertain challenge state produced a task priority.");
}

static void AssertOrderIdentityAndOwnershipAreExact()
{
    AssertNoPriority(
        Project(CreateContext(CreateOrder(guestId: 4))),
        "A canonical guest mismatch was accepted.");
    AssertNoPriority(
        Project(CreateContext(CreateOrder(runtimeGuestId: 3))),
        "A raw runtime guest mismatch was accepted.");
    AssertNoPriority(
        Project(CreateContext(CreateOrder(traceId: " "))),
        "An order without a trace ID was accepted.");
    AssertNoPriority(
        Project(CreateContext(CreateOrder(deskCode: -1))),
        "An order without a live desk was accepted.");
    AssertNoPriority(
        Project(CreateContext(CreateOrder(hasServedFood: true))),
        "An order whose food was already served retained a task priority.");
    AssertNoPriority(
        Project(CreateContext(
            CreateOrder(),
            CreateOrder(traceId: "R-0002", deskCode: 2))),
        "One guest signal was fanned out across ambiguous live orders.");

    var uniqueUnserved = Project(CreateContext(
        CreateOrder(traceId: "R-served", deskCode: 1, hasServedFood: true),
        CreateOrder(traceId: "R-live", deskCode: 2)));
    AssertNull(
        uniqueUnserved.Orders[0].MissionRecipePriority,
        "A served order received a task priority.");
    AssertEqual(
        "R-live",
        uniqueUnserved.Orders[1].MissionRecipePriority?.TraceId,
        "The unique unserved order did not receive the task priority.");
}

static void AssertRecipeMappingIsUniqueAndExact()
{
    var context = CreateContext(CreateOrder());
    AssertNoPriority(
        Project(context, catalog: CreateCatalog(recipes: Array.Empty<Recipe>())),
        "A missing food mapping was accepted.");
    AssertNoPriority(
        Project(context, catalog: CreateCatalog(recipes: new[]
        {
            CreateRecipe(foodId: 1, recipeId: 1201),
            CreateRecipe(foodId: 1, recipeId: 2201),
        })),
        "A duplicate food mapping was accepted.");
    AssertNoPriority(
        Project(context, catalog: CreateCatalog(recipes: new[]
        {
            CreateRecipe(foodId: 1, recipeId: -1),
        })),
        "A negative game recipe ID was accepted.");

    var projected = Project(context, catalog: CreateCatalog(recipes: new[]
    {
        CreateRecipe(foodId: 1201, recipeId: 1),
        CreateRecipe(foodId: 1, recipeId: 1201),
    }));
    AssertEqual(
        1201,
        projected.Orders.Single().MissionRecipePriority?.RecipeId,
        "foodId and recipeId were reversed during mapping.");
}

static void AssertConflictingSignalsFailClosed()
{
    var serve = CreateServeSnapshot();
    var first = serve.Signals.Single();
    var conflicting = first with
    {
        FoodId = 2,
    };
    var catalog = CreateCatalog(recipes: new[]
    {
        CreateRecipe(foodId: 1, recipeId: 1201),
        CreateRecipe(foodId: 2, recipeId: 1202),
    });

    AssertNoPriority(
        Project(
            CreateContext(CreateOrder()),
            catalog: catalog,
            serve: serve with
            {
                Signals = new[]
                {
                    first,
                    conflicting,
                },
            }),
        "Conflicting foods were bound to one live order.");

    var staleSignal = first with
    {
        MissionGeneration = 3,
    };
    AssertNoPriority(
        Project(
            CreateContext(CreateOrder()),
            serve: serve with
            {
                Signals = new[]
                {
                    staleSignal,
                },
            }),
        "A stale signal inside a current observer snapshot was accepted.");
}

static void AssertObservedTimeIsNotATtl()
{
    var oldSignal = CreateServeSnapshot(observedAtUtc: DateTime.UnixEpoch);
    var projected = Project(
        CreateContext(CreateOrder()),
        serve: oldSignal);
    AssertNotNull(
        projected.Orders.Single().MissionRecipePriority,
        "A current-generation signal was rejected because its observation time was old.");
}

static void AssertRepeatedObservationDoesNotChangeBusinessProjection()
{
    var context = CreateContext(CreateOrder());
    var first = Project(
        context,
        serve: CreateServeSnapshot(observedAtUtc: Utc(4)));
    var repeated = Project(
        context,
        serve: CreateServeSnapshot(observedAtUtc: Utc(40)));
    var firstJson = JsonSerializer.Serialize(
        first.Orders.Single().MissionRecipePriority);
    var repeatedJson = JsonSerializer.Serialize(
        repeated.Orders.Single().MissionRecipePriority);

    AssertEqual(
        firstJson,
        repeatedJson,
        "A repeated native observation changed the stable business projection.");
    AssertTrue(
        typeof(MissionRecipePriority)
            .GetProperties()
            .All(property => !property.Name.Contains("Observed", StringComparison.Ordinal)),
        "The business projection exposed diagnostic observation time.");
}

static void AssertLifecycleReconciliationRetainsProjectedPriority()
{
    var state = CreateServeState();
    AssertTrue(
        state.ReconcileForMissionLifecycle(
            4,
            new[]
            {
                new RuntimeServeInWorkMissionSignalKey(3, 1),
                new RuntimeServeInWorkMissionSignalKey(10, 99),
            },
            Utc(5)),
        "An unrelated mission refresh could not reconcile the active task signal.");
    var projected = Project(
        CreateContext(CreateOrder()),
        serve: state.Snapshot());
    AssertNotNull(
        projected.Orders.Single().MissionRecipePriority,
        "An unrelated mission refresh removed the current task priority.");

    AssertTrue(
        state.ReconcileForMissionLifecycle(
            4,
            Array.Empty<RuntimeServeInWorkMissionSignalKey>(),
            Utc(6)),
        "The fulfilled task definition set could not be reconciled.");
    AssertNoPriority(
        Project(
            CreateContext(CreateOrder()),
            serve: state.Snapshot()),
        "A fulfilled or removed task retained its projected recipe priority.");
}

static void AssertInvalidContextClearsExistingPriority()
{
    var projected = Project(CreateContext(CreateOrder()));
    AssertNotNull(
        projected.Orders.Single().MissionRecipePriority,
        "The stale-priority test could not establish its baseline.");

    var cleared = Project(
        projected,
        lifecycle: CreateLifecycle() with
        {
            Phase = NightBusinessLifecyclePhase.Destroyed,
        });
    AssertNull(
        cleared.Orders.Single().MissionRecipePriority,
        "An invalid lifecycle retained a previously enriched priority.");
}

static NightBusinessContext Project(
    NightBusinessContext context,
    RuntimeDataCatalog? catalog = null,
    NightBusinessLifecycleSnapshot? lifecycle = null,
    long missionGeneration = 4,
    RuntimeServeInWorkMissionDiagnosticSnapshot? serve = null,
    SpecialBusinessContext? specialBusiness = null,
    RuntimeMissionRecipePriorityMissionBoundary? mission = null)
{
    return RuntimeMissionRecipePriorityProjection.Enrich(
        context,
        catalog ?? CreateCatalog(),
        lifecycle ?? CreateLifecycle(),
        mission ?? CreateMissionBoundary(missionGeneration),
        serve ?? CreateServeSnapshot(),
        specialBusiness ?? CreateOrdinaryBusiness())!;
}

static RuntimeMissionRecipePriorityMissionBoundary CreateMissionBoundary(
    long generation = 4)
{
    return new RuntimeMissionRecipePriorityMissionBoundary(
        generation,
        RuntimeAvailable: true,
        Ready: true);
}

static RuntimeServeInWorkMissionDiagnosticSnapshot CreateServeSnapshot(
    DateTime? observedAtUtc = null)
{
    return CreateServeState(observedAtUtc).Snapshot();
}

static RuntimeServeInWorkMissionDiagnosticState CreateServeState(
    DateTime? observedAtUtc = null)
{
    var state = new RuntimeServeInWorkMissionDiagnosticState();
    state.SetHookStatus("patched:1/1", attached: true, Utc(1));
    AssertTrue(
        state.ResetForMissionGeneration(4, Utc(2)),
        "Could not establish the smoke mission generation.");
    AssertTrue(
        state.ApplyBusinessBoundary(7, "Active", Utc(3)),
        "Could not establish the smoke business generation.");
    AssertTrue(
        state.ObserveResult(
            expectedMissionGeneration: 4,
            expectedBusinessGeneration: 7,
            rawGuestId: 1003,
            canonicalGuestId: 3,
            foodId: 1,
            result: true,
            RuntimeServeInWorkMissionDefinitionStatus.Matched,
            expectedFoodId: 1,
            observedAtUtc ?? Utc(4)),
        "Could not establish the smoke ServeInWork signal.");
    return state;
}

static RuntimeDataCatalog CreateCatalog(
    IReadOnlyList<Recipe>? recipes = null)
{
    return new RuntimeDataCatalog
    {
        IsComplete = true,
        Source = "smoke",
        Status = "complete",
        Recipes = (recipes ?? new[]
        {
            CreateRecipe(foodId: 1, recipeId: 1201),
        }).ToList(),
    };
}

static RuntimeDataCatalog CopyCatalogWithComplete(
    RuntimeDataCatalog source,
    bool isComplete)
{
    return new RuntimeDataCatalog
    {
        IsComplete = isComplete,
        Source = source.Source,
        Status = source.Status,
        Recipes = source.Recipes.ToList(),
        Ingredients = source.Ingredients.ToList(),
        Beverages = source.Beverages.ToList(),
        NormalCustomers = source.NormalCustomers.ToList(),
        RareCustomers = source.RareCustomers.ToList(),
        FoodTagIdMap = new Dictionary<string, string>(source.FoodTagIdMap),
        BeverageTagIdMap = new Dictionary<string, string>(source.BeverageTagIdMap),
        TagPriorityRules = source.TagPriorityRules.ToList(),
    };
}

static Recipe CreateRecipe(int foodId, int recipeId)
{
    return new Recipe
    {
        Id = foodId,
        RecipeId = recipeId,
        Name = $"food-{foodId}",
    };
}

static NightBusinessLifecycleSnapshot CreateLifecycle()
{
    return new NightBusinessLifecycleSnapshot(
        Generation: 7,
        Version: 1,
        Phase: NightBusinessLifecyclePhase.Active,
        Source: "smoke",
        ChangedAtUtc: Utc(3),
        ThreadId: 1);
}

static SpecialBusinessContext CreateOrdinaryBusiness()
{
    return new SpecialBusinessContext
    {
        Active = false,
        ChallengeType = SpecialBusinessChallengeTypes.NotChallenge,
        Source = "smoke",
        Error = null,
    };
}

static NightBusinessContext CreateContext(params NightBusinessOrder[] orders)
{
    return new NightBusinessContext
    {
        Place = "妖怪兽道",
        PlaceLabel = "BambooRoad",
        ActiveRareGuests = new List<NightBusinessGuest>
        {
            new()
            {
                DeskCode = 1,
                GuestId = 3,
                GuestName = "阿求",
                Source = "smoke",
            },
        },
        Orders = orders.ToList(),
        Source = "smoke",
        Error = null,
    };
}

static NightBusinessOrder CreateOrder(
    string traceId = "R-0001",
    int deskCode = 1,
    int guestId = 3,
    int runtimeGuestId = 1003,
    bool hasServedFood = false)
{
    return new NightBusinessOrder
    {
        TraceId = traceId,
        DeskCode = deskCode,
        GuestId = guestId,
        RuntimeGuestId = runtimeGuestId,
        GuestName = "阿求",
        SpecialBusinessRole = "",
        SpecialBusinessRoleLabel = "",
        AutomationAllowed = true,
        AutomationBlockReason = "",
        FoodTagId = 11,
        FoodTag = "家常",
        BeverageTagId = 21,
        BeverageTag = "清酒",
        Source = "smoke",
        FirstSeenAtUtc = Utc(10),
        LastSeenAtUtc = Utc(11),
        IsFreeOrder = false,
        Fund = 500,
        BaseFundCarry = 400,
        MaxFundCarry = 800,
        ExtraFundByBuff = 100,
        WillPayMoney = true,
        RemainingOrderCount = 2,
        HasServedFood = hasServedFood,
        HasServedBeverage = false,
    };
}

static void AssertOrderFieldsPreserved(
    NightBusinessOrder expected,
    NightBusinessOrder actual)
{
    foreach (var property in typeof(NightBusinessOrder)
                 .GetProperties()
                 .Where(property => property.Name != nameof(NightBusinessOrder.MissionRecipePriority)))
    {
        AssertEqual(
            property.GetValue(expected),
            property.GetValue(actual),
            $"Order field {property.Name} changed during enrichment.");
    }
}

static DateTime Utc(int second)
{
    return new DateTime(2026, 7, 28, 0, 0, second, DateTimeKind.Utc);
}

static void AssertNoPriority(
    NightBusinessContext context,
    string message)
{
    AssertTrue(
        context.Orders.All(order => order.MissionRecipePriority == null),
        message);
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertNull(object? value, string message)
{
    if (value != null)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertNotNull(object? value, string message)
{
    if (value == null)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertNotSame(object expected, object actual, string message)
{
    if (ReferenceEquals(expected, actual))
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException(
            $"{message} Expected={expected}; Actual={actual}.");
    }
}
