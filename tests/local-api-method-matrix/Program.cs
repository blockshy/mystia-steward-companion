using System.Text.RegularExpressions;

var expectedGetRoutes = new HashSet<string>(StringComparer.Ordinal)
{
    "/automation/lease",
    "/custom-recipes",
    "/favorites",
    "/health",
    "/local-api/config",
    "/logs/settings",
    "/runtime-data",
    "/snapshot",
};
var expectedPostRoutes = new HashSet<string>(StringComparer.Ordinal)
{
    "/automation/lease/acquire",
    "/automation/lease/release",
    "/custom-recipes/move",
    "/custom-recipes/remove",
    "/custom-recipes/settings",
    "/custom-recipes/upsert",
    "/custom-recipes/update-flags",
    "/diagnostics/automation-decision",
    "/favorites/add-beverage",
    "/favorites/add-recipe",
    "/favorites/remove-beverage",
    "/favorites/remove-recipe",
    "/inventory/bulk-set",
    "/inventory/set",
    "/local-api/config",
    "/local-api/token/regenerate",
    "/logs/config",
    "/logs/export-diagnostics",
    "/logs/open-folder",
    "/orders/complete-first",
    "/orders/normal/complete-first",
    "/orders/prepare-next",
    "/orders/rare/dismiss",
    "/rare-guests/invitations",
    "/rare-guests/invite",
    "/rare-guests/invite-all",
    "/ui-pinning/target",
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

static int RequireIndex(string source, string marker, int startIndex)
{
    var index = source.IndexOf(marker, startIndex, StringComparison.Ordinal);
    return index >= 0
        ? index
        : throw new InvalidOperationException($"Route parser marker was not found: {marker}");
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

static void AssertEqual(IReadOnlyList<string> expected, IReadOnlyList<string> actual, string message)
{
    if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"{message} Expected=[{string.Join(", ", expected)}], actual=[{string.Join(", ", actual)}].");
    }
}
