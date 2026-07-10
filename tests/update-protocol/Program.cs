using MystiaStewardCompanion.Updates;

try
{
    VerifySemanticVersionOrdering();
    VerifyManifestValidation();
    VerifyAtomParsing();
    VerifyCachedStateValidation();
    VerifyBoundedDownloadCopy();
    Console.WriteLine("PASS: semantic versions, strict manifest/cached-state validation, Atom parsing, and bounded download copies are correct.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static void VerifySemanticVersionOrdering()
{
    AssertEqual(true, SemanticVersion.TryParse("v1.2.0-preview.2", out var preview2), "preview.2 did not parse.");
    AssertEqual(true, SemanticVersion.TryParse("1.2.0-preview.10", out var preview10), "preview.10 did not parse.");
    AssertEqual(true, SemanticVersion.TryParse("1.2.0", out var stable), "stable version did not parse.");
    AssertEqual(true, preview10.CompareTo(preview2) > 0, "Numeric prerelease identifiers were compared lexically.");
    AssertEqual(true, stable.CompareTo(preview10) > 0, "Stable release did not sort after prerelease.");
}

static void VerifyManifestValidation()
{
    UpdateService.ValidateManifest(Manifest());
    ExpectInvalidManifest(Manifest(schemaVersion: 2), "schemaVersion");
    ExpectInvalidManifest(Manifest(channel: "preview"), "channel");
    ExpectInvalidManifest(Manifest(packageSha256: "abc"), "packageSha256");
    ExpectInvalidManifest(new UpdateManifest
    {
        SchemaVersion = 1,
        Version = "1.2.3",
        Tag = "v1.2.3",
        Channel = "stable",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = null!,
        PackageSize = 42,
    }, "packageSha256");
    ExpectInvalidManifest(Manifest(packageSize: 0), "packageSize");
    ExpectInvalidManifest(Manifest(tag: "v1.2.4"), "version 与 tag");
}

static void VerifyCachedStateValidation()
{
    var state = new UpdateState
    {
        LatestVersion = "1.2.4",
        LatestTag = "v1.2.4",
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = new string('a', 64),
        PackageSize = 42,
        PackageDownloadUrl = "https://example.test/package.zip",
    };
    AssertEqual(true, UpdateService.HasAvailableUpdate(state), "A valid cached candidate was rejected.");
    state.PackageSize = 0;
    AssertEqual(false, UpdateService.HasAvailableUpdate(state), "A cached candidate without package size was accepted.");
}

static void VerifyBoundedDownloadCopy()
{
    var content = new byte[] { 1, 2, 3, 4 };
    using var source = new MemoryStream(content);
    using var destination = new MemoryStream();
    UpdateService.CopyDownloadContentAsync(source, destination, content.Length, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(true, content.SequenceEqual(destination.ToArray()), "Exact-size download content did not round-trip.");

    ExpectInvalidDownloadSize(content, content.Length - 1);
    ExpectInvalidDownloadSize(content, content.Length + 1);
}

static void ExpectInvalidDownloadSize(byte[] content, long expectedSize)
{
    using var source = new MemoryStream(content);
    using var destination = new MemoryStream();
    try
    {
        UpdateService.CopyDownloadContentAsync(source, destination, expectedSize, CancellationToken.None).GetAwaiter().GetResult();
        throw new InvalidOperationException("Expected an invalid download size error.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("大小", StringComparison.Ordinal))
    {
    }
}

static void VerifyAtomParsing()
{
    const string xml = "<?xml version=\"1.0\"?><feed xmlns=\"http://www.w3.org/2005/Atom\">"
        + "<entry><title>v1.2.0-preview.10</title><link href=\"https://example.test/preview?a=1&amp;b=2\" rel=\"alternate\"/><updated>2026-07-01T00:00:00Z</updated></entry>"
        + "<entry><title>v1.2.0</title><link rel=\"alternate\" href=\"https://example.test/stable\"/><updated>2026-07-02T00:00:00Z</updated></entry>"
        + "<entry><title>not-a-version</title></entry></feed>";
    var releases = UpdateService.ParseReleaseFeed(xml);
    AssertEqual(2, releases.Count, "Unexpected number of parsed releases.");
    AssertEqual("v1.2.0", releases[0].TagName, "Stable release was not sorted first.");
    AssertEqual("https://example.test/preview?a=1&b=2", releases[1].HtmlUrl, "Atom link attributes/entities were not parsed structurally.");
}

static UpdateManifest Manifest(
    int schemaVersion = 1,
    string tag = "v1.2.3",
    string channel = "stable",
    string? packageSha256 = null,
    long packageSize = 42)
{
    return new UpdateManifest
    {
        SchemaVersion = schemaVersion,
        Version = "1.2.3",
        Tag = tag,
        Channel = channel,
        PackageAsset = "mystia-steward-companion-bepinex.zip",
        PackageSha256 = packageSha256 ?? new string('a', 64),
        PackageSize = packageSize,
    };
}

static void ExpectInvalidManifest(UpdateManifest manifest, string expectedMessage)
{
    try
    {
        UpdateService.ValidateManifest(manifest);
        throw new InvalidOperationException($"Manifest containing invalid {expectedMessage} was accepted.");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains(expectedMessage, StringComparison.Ordinal))
    {
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}
