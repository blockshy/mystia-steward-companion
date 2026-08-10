using System.Text.RegularExpressions;

var expectedGetRoutes = new HashSet<string>(StringComparer.Ordinal)
{
    "/automation/lease",
    "/custom-recipes",
    "/devices",
    "/favorites",
    "/health",
    "/local-api/config",
    "/logs/settings",
    "/missions/available",
    "/missions/tracked",
    "/rare-guests/invitations",
    "/runtime-data",
    "/snapshot",
};
var expectedPostRoutes = new HashSet<string>(StringComparer.Ordinal)
{
    "/automation/lease/acquire",
    "/automation/lease/release",
    "/automation/barriers/ack",
    "/custom-recipes/move",
    "/custom-recipes/remove",
    "/custom-recipes/settings",
    "/custom-recipes/upsert",
    "/custom-recipes/update-flags",
    "/diagnostics/automation-decision",
    "/devices/forget",
    "/devices/primary",
    "/devices/profile",
    "/devices/register",
    "/devices/rename",
    "/devices/sync",
    "/devices/sync-ack",
    "/favorites/add-beverage",
    "/favorites/add-recipe",
    "/favorites/remove-beverage",
    "/favorites/remove-recipe",
    "/inventory/bulk-set",
    "/inventory/set",
    "/local-api/config",
    "/local-api/token/regenerate",
    "/logs/config",
    "/logs/console",
    "/logs/export-diagnostics",
    "/logs/open-folder",
    "/orders/complete-first",
    "/orders/normal/complete-first",
    "/orders/prepare-next",
    "/orders/rare/dismiss",
    "/rare-guests/invite",
    "/rare-guests/invite-all",
    "/ui-pinning/targets",
    "/updates/check",
    "/updates/download",
    "/updates/install-on-exit",
    "/updates/status",
};

try
{
    var sourcePath = FindServerSource();
    var source = File.ReadAllText(sourcePath);
    AssertAbsent(source, "NormalizeApiPath", "Legacy API path normalization still exists.");
    AssertAbsent(source, "case \"/\":", "The root path still aliases another endpoint.");
    AssertAbsent(source, "StartsWith(\"/api/\"", "The /api/* path alias still exists.");
    AssertAbsent(source, "case \"/automation/cancel\":", "The destructive automation-cancellation route still exists.");
    AssertAbsent(source, "case \"/automation/jobs/cancel\":", "The obsolete job-cancellation route still exists.");
    AssertAbsent(source, "case \"/ui-pinning/target\":", "The obsolete singular UI target route still exists.");
    AssertContains(
        source,
        "ValidateUiPinningTargetParameters(query, targetCount);",
        "Unexpected or out-of-range UI target fields are not rejected.");
    foreach (var field in new[]
             {
                 "ListPinningEnabled",
                 "RecipeVariantEnabled",
                 "CookerHighlightEnabled",
                 "SeatHighlightEnabled",
                 "OrderHighlightEnabled",
             })
    {
        AssertContains(
            source,
            $"ReadRequiredExactBoolQuery(query, $\"{{prefix}}{field}\")",
            $"Per-target {field} is missing from the exact UI target wire contract.");
    }
    AssertContains(
        source,
        "throw new FormatException($\"Unexpected UI target parameter {name}.\");",
        "Obsolete collection-level UI feature fields are not rejected.");
    AssertAbsent(
        source,
        "ReadRequiredExactBoolQuery(query, \"enabled\")",
        "The obsolete collection-level enabled field remains in the UI target protocol.");
    AssertAbsent(
        source,
        "ReadRequiredExactBoolQuery(query, \"highlightEnabled\")",
        "The obsolete collection-level highlight field remains in the UI target protocol.");
    AssertAbsent(
        source,
        "ReadRequiredExactBoolQuery(query, \"extraIngredientFillEnabled\")",
        "The obsolete collection-level recipe-extra field remains in the UI target protocol.");
    AssertAbsent(
        source,
        "ReadRequiredExactBoolQuery(query, \"seatHighlightEnabled\")",
        "The obsolete collection-level seat-highlight field remains in the UI target protocol.");
    AssertAbsent(
        source,
        "ReadRequiredExactBoolQuery(query, \"orderHighlightEnabled\")",
        "The obsolete collection-level order-highlight field remains in the UI target protocol.");
    AssertContains(
        source,
        "RuntimeTargetHighlightColor.TryParseExactHex(colorValue, out var color)",
        "UI target colors no longer use the exact uppercase RGB parser.");
    AssertContains(
        source,
        "part.Any(character => character is < '0' or > '9')",
        "UI target id sequences no longer reject whitespace, signs, or non-ASCII digits.");
    AssertContains(
        source,
        "ReleaseAutomationLease(request)",
        "The exact automation lease-release route is not connected to its handler.");
    AssertAbsent(
        source,
        "AutomationCancellationTarget",
        "The deleted targeted-cancellation model is still reachable from the Local API.");
    var releaseLease = ExtractSourceBlock(
        source,
        "private LocalApiAutomationLeaseDto ReleaseAutomationLease(");
    AssertContains(
        releaseLease,
        "TryAuthorizeRuntimeWriter(request, clientId, now, out var authorityRevision, out var authorityError)",
        "Lease release does not require the exact current primary-device authority.");
    AssertContains(
        releaseLease,
        "RuntimeAutomationControlState.RevokeLease(",
        "Lease release does not suspend future active-job side effects.");
    AssertContains(
        releaseLease,
        "_advanceAutomationCommandEpoch(_automationCommandEpoch);",
        "Lease release does not fence already queued commands.");
    AssertAbsent(
        releaseLease,
        "ClearAutomationCookingJobs(",
        "Lease release can still delete an active cooking job instead of suspending it.");
    AssertContains(
        source,
        "var requestData = HttpRequestReader.Read(stream, MaxRequestHeaderBytes, MaxRequestBodyBytes);",
        "Local API requests no longer use the bounded header/body reader.");
    AssertContains(
        source,
        "ReadJsonRequest<CompanionDeviceRegisterRequest>",
        "Device registration no longer requires an exact JSON request body.");
    AssertContains(
        source,
        "TryAuthorizeRuntimeWriter(request, clientId, now, out authorityRevision, out authorityError)",
        "Runtime writers no longer validate the current device authority revision.");
    AssertContains(
        source,
        "AcknowledgeAutomationSafetyBarrier(request, query)",
        "Automation safety barriers do not expose an explicit lease-owned acknowledgement endpoint.");
    AssertContains(
        source,
        "return _ackAutomationSafetyBarrier(sequence);",
        "Safety barrier acknowledgement does not reach the authoritative Mod registry.");
    AssertContains(source, "AutomationEpoch = automationEpoch", "Order commands are not stamped with the validated automation epoch.");
    AssertContains(
        source,
        "_advanceAutomationCommandEpoch(_automationCommandEpoch);",
        "A newly acquired automation lease does not immediately invalidate commands from the previous owner.");
    AssertContains(
        source,
        "_automationCommandEpoch = Math.Max(1, automationCommandEpoch);",
        "A recreated Local API server does not inherit the Unity command epoch.");
    AssertContains(
        source,
        "ReadLongQuery(query, \"expectedDaySceneGeneration\", 0)",
        "Rare-guest invitation writes do not require the expected day-scene generation.");
    AssertContains(
        source,
        "ReadStringQuery(query, \"expectedMapLabel\")",
        "Rare-guest invitation writes do not require the expected map label.");

    var disposeStart = RequireIndex(source, "public void Dispose()", 0);
    var stopAcceptingIndex = RequireIndex(source, "_clientHandlers.StopAccepting();", disposeStart);
    var beginUpdateShutdownIndex = RequireIndex(source, "_updateService.BeginShutdown();", stopAcceptingIndex);
    var waitForHandlersIndex = RequireIndex(source, "_clientHandlers.WaitForIdle(ClientHandlerStopTimeout)", beginUpdateShutdownIndex);
    var disposeUpdateServiceIndex = RequireIndex(source, "_updateService.Dispose();", waitForHandlersIndex);
    if (!(stopAcceptingIndex < beginUpdateShutdownIndex
          && beginUpdateShutdownIndex < waitForHandlersIndex
          && waitForHandlersIndex < disposeUpdateServiceIndex))
    {
        throw new InvalidOperationException(
            "Local API shutdown must stop new clients, cancel update operations, wait for handlers, then release the update service.");
    }

    var handleStart = RequireIndex(source, "private void HandleClient(TcpClient client)", 0);
    var postBranchStart = RequireIndex(source, "if (isPost)", handleStart);
    var postSwitchStart = RequireIndex(source, "switch (path)", postBranchStart);
    var getSwitchStart = RequireIndex(source, "switch (path)", postSwitchStart + 1);
    var handlerCatchStart = RequireIndex(source, "catch (HttpRequestReadException", getSwitchStart);
    var postRoutes = ReadCaseRoutes(source[postSwitchStart..getSwitchStart]);
    var getRoutes = ReadCaseRoutes(source[getSwitchStart..handlerCatchStart]);

    AssertRouteSet("GET", expectedGetRoutes, getRoutes);
    AssertRouteSet("POST", expectedPostRoutes, postRoutes);
    AssertNoDuplicates("GET", getRoutes);
    AssertNoDuplicates("POST", postRoutes);

    var automationReleaseRouteStart = RequireIndex(source, "case \"/automation/lease/release\":", postSwitchStart);
    var automationBarrierRouteStart = RequireIndex(source, "case \"/automation/barriers/ack\":", automationReleaseRouteStart);
    var automationReleaseRoute = source[automationReleaseRouteStart..automationBarrierRouteStart];
    AssertContains(
        automationReleaseRoute,
        "ToJson(ReleaseAutomationLease(request))",
        "The release route does not return the canonical lease DTO.");
    AssertAbsent(
        automationReleaseRoute,
        "ReadStringQuery(query",
        "The release endpoint accepts an obsolete cancellation target or scope query.");

    var inviteAllRouteStart = RequireIndex(source, "case \"/rare-guests/invite-all\":", postSwitchStart);
    var inviteRouteStart = RequireIndex(source, "case \"/rare-guests/invite\":", inviteAllRouteStart);
    var nextPostRouteStart = RequireIndex(source, "case \"/ui-pinning/targets\":", inviteRouteStart);
    AssertContains(
        source[inviteAllRouteStart..inviteRouteStart],
        "ReadRareGuestInvitationWriteExpectation(query)",
        "Invite-all does not forward its captured day-scene context.");
    AssertContains(
        source[inviteRouteStart..nextPostRouteStart],
        "ReadRareGuestInvitationWriteExpectation(query)",
        "Invite-one does not forward its captured day-scene context.");

    var consoleRouteStart = RequireIndex(source, "case \"/logs/console\":", postSwitchStart);
    var consoleRouteEnd = RequireIndex(source, "case \"/logs/open-folder\":", consoleRouteStart);
    AssertContains(
        source[consoleRouteStart..consoleRouteEnd],
        "if (!isLoopbackClient)",
        "BepInEx console control is not restricted to the game PC.");
    AssertContains(
        source[consoleRouteStart..consoleRouteEnd],
        "BepInEx console control is only allowed from the game PC",
        "BepInEx console LAN rejection is not explicit.");

    var overlaySource = File.ReadAllText(FindOverlaySource());
    var modelSource = File.ReadAllText(Path.Combine(Path.GetDirectoryName(sourcePath)!, "LocalApiModels.cs"));
    AssertContains(
        modelSource,
        "public string ControlState { get; init; } = \"active\";",
        "The Local API cooking-job snapshot does not publish its live control state.");
    AssertContains(
        modelSource,
        "public long ControlAuthorityRevision { get; init; }",
        "The Local API cooking-job snapshot does not identify its observed authority revision.");
    AssertAbsent(
        modelSource,
        "AutomationCancellationAppliedEpoch",
        "The snapshot still publishes the deleted cancellation-ack epoch.");
    AssertContains(
        overlaySource,
        "AppendValue(builder, job.ControlState);",
        "The job control state is absent from the canonical snapshot content signature.");
    AssertContains(
        overlaySource,
        "AppendValue(builder, job.ControlAuthorityRevision);",
        "The observed control authority is absent from the canonical snapshot content signature.");
    AssertAbsent(
        overlaySource,
        "AutomationCancellation",
        "The Unity overlay still contains the deleted cancellation queue or acknowledgement path.");
    AssertContains(
        overlaySource,
        "pending.AutomationEpoch != currentEpoch",
        "Queued order commands are not rejected after the automation epoch advances.");
    AssertContains(
        overlaySource,
        "_automationCommandFence.RunExclusive(currentEpoch =>",
        "Order execution is not serialized with automation epoch changes.");
    AssertContains(
        overlaySource,
        "result.Automation.ReasonCode = string.IsNullOrWhiteSpace(reasonCode) ? \"runtime-unavailable\" : reasonCode;",
        "Runtime-unavailable order responses do not participate in structured retry handling.");
    AssertContains(
        overlaySource,
        ": \"retryable-failure\";",
        "Runtime-unavailable order responses no longer expose a structured retryable outcome.");

    var overlappingRoutes = expectedGetRoutes.Intersect(expectedPostRoutes, StringComparer.Ordinal).ToArray();
    AssertEqual(
        new[] { "/local-api/config" },
        overlappingRoutes,
        "Only the configuration resource may use GET for reading and POST for updating.");

    Console.WriteLine($"PASS: {getRoutes.Count} GET routes and {postRoutes.Count} POST routes match the strict method matrix; legacy path aliases are absent.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex.Message}");
    return 1;
}

static string FindServerSource()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
    {
        var candidate = Path.Combine(
            directory.FullName,
            "mods",
            "bepinex",
            "src",
            "LocalApi",
            "LocalApiServer.cs");
        if (File.Exists(candidate)) return candidate;
    }

    throw new FileNotFoundException("Could not locate mods/bepinex/src/LocalApi/LocalApiServer.cs.");
}

static string FindOverlaySource()
{
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
    {
        var candidate = Path.Combine(
            directory.FullName,
            "mods",
            "bepinex",
            "src",
            "Ui",
            "StewardOverlayController.cs");
        if (File.Exists(candidate)) return candidate;
    }

    throw new FileNotFoundException("Could not locate mods/bepinex/src/Ui/StewardOverlayController.cs.");
}

static int RequireIndex(string source, string marker, int startIndex)
{
    var index = source.IndexOf(marker, startIndex, StringComparison.Ordinal);
    return index >= 0
        ? index
        : throw new InvalidOperationException($"Route parser marker was not found: {marker}");
}

static string ExtractSourceBlock(string source, string marker)
{
    var markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
    if (markerIndex < 0)
    {
        throw new InvalidOperationException($"Source marker was not found: {marker}");
    }

    var openBrace = source.IndexOf('{', markerIndex);
    if (openBrace < 0)
    {
        throw new InvalidOperationException($"Source block has no opening brace: {marker}");
    }

    var depth = 0;
    for (var index = openBrace; index < source.Length; index++)
    {
        if (source[index] == '{') depth++;
        else if (source[index] == '}' && --depth == 0) return source[markerIndex..(index + 1)];
    }

    throw new InvalidOperationException($"Source block is not balanced: {marker}");
}

static List<string> ReadCaseRoutes(string source)
{
    return Regex.Matches(source, "case\\s+\\\"(?<path>/[^\\\"]*)\\\"\\s*:", RegexOptions.CultureInvariant)
        .Select(match => match.Groups["path"].Value)
        .ToList();
}

static void AssertRouteSet(string method, HashSet<string> expected, IReadOnlyCollection<string> actual)
{
    var actualSet = actual.ToHashSet(StringComparer.Ordinal);
    var missing = expected.Except(actualSet, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    var unexpected = actualSet.Except(expected, StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
    if (missing.Length == 0 && unexpected.Length == 0) return;

    throw new InvalidOperationException(
        $"{method} route matrix mismatch. Missing=[{string.Join(", ", missing)}], unexpected=[{string.Join(", ", unexpected)}].");
}

static void AssertNoDuplicates(string method, IReadOnlyCollection<string> routes)
{
    var duplicates = routes
        .GroupBy(path => path, StringComparer.Ordinal)
        .Where(group => group.Count() > 1)
        .Select(group => group.Key)
        .OrderBy(path => path, StringComparer.Ordinal)
        .ToArray();
    if (duplicates.Length > 0)
    {
        throw new InvalidOperationException($"{method} contains duplicate route cases: {string.Join(", ", duplicates)}.");
    }
}

static void AssertAbsent(string source, string value, string message)
{
    if (source.Contains(value, StringComparison.Ordinal)) throw new InvalidOperationException(message);
}

static void AssertContains(string source, string value, string message)
{
    if (!source.Contains(value, StringComparison.Ordinal)) throw new InvalidOperationException(message);
}

static void AssertEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string message)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"{message} Expected=[{string.Join(", ", expected)}], actual=[{string.Join(", ", actual)}].");
    }
}
