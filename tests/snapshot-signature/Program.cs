using System.Text.Json;
using MystiaStewardCompanion.LocalApi;

try
{
    const string canonicalContent = "7:version|1|4:scene|";
    var first = LocalApiSnapshotSignature.Compute(canonicalContent);
    var second = LocalApiSnapshotSignature.Compute(canonicalContent);
    var changed = LocalApiSnapshotSignature.Compute(canonicalContent + "1|");
    var empty = LocalApiSnapshotSignature.Compute("");
    var largeCanonicalContent = string.Concat(Enumerable.Repeat("订单:料理/酒水|", 8_192));
    var largeSignature = LocalApiSnapshotSignature.Compute(largeCanonicalContent);

    AssertEqual(first, second, "The same canonical snapshot content produced an unstable signature.");
    AssertEqual(64, first.Length, "Snapshot signatures must have a fixed SHA-256 length.");
    AssertTrue(first.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'), "Snapshot signatures must use lowercase hexadecimal.");
    AssertNotEqual(first, changed, "Changing canonical snapshot content did not change the signature.");
    AssertTrue(largeCanonicalContent.Length > 40 * 1024, "The large-content regression fixture must exceed 40 KB.");
    AssertEqual(64, largeSignature.Length, "A large canonical snapshot escaped the fixed signature boundary.");
    AssertTrue(Uri.EscapeDataString(largeSignature).Length < 128, "The knownSignature query value grew with canonical snapshot content.");
    AssertEqual(
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        empty,
        "The signature helper does not match the SHA-256 reference vector.");

    var firstCatalog = BuildRuntimeDataCatalog("绿茶");
    var equivalentCatalog = BuildRuntimeDataCatalog("绿茶");
    var changedCatalog = BuildRuntimeDataCatalog("高级绿茶");
    var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var firstPayload = LocalApiRuntimeDataPayload.Create(firstCatalog, options);
    var equivalentPayload = LocalApiRuntimeDataPayload.Create(equivalentCatalog, options);
    var changedPayload = LocalApiRuntimeDataPayload.Create(changedCatalog, options);

    AssertEqual(
        firstPayload.Signature,
        equivalentPayload.Signature,
        "Equivalent runtime catalogs produced unstable payload signatures.");
    AssertNotEqual(
        firstPayload.Signature,
        changedPayload.Signature,
        "A same-count runtime catalog content change did not change the payload signature.");
    AssertNotEqual(
        firstPayload.Json,
        changedPayload.Json,
        "A runtime catalog content change did not change the published JSON.");
    AssertEqual(
        64,
        firstPayload.Signature.Length,
        "Runtime catalog payload signatures must have a fixed SHA-256 length.");

    Console.WriteLine("PASS: snapshot and full runtime-data payloads use stable content SHA-256 signatures.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FAIL: {ex}");
    return 1;
}

static object BuildRuntimeDataCatalog(string beverageName)
{
    return new
    {
        isComplete = true,
        source = "test-runtime",
        status = "ready",
        beverages = new[]
        {
            new
            {
                id = 0,
                name = beverageName,
                tags = new[] { "无酒精" },
                level = 1,
                price = 1,
            },
        },
        beverageTagIdMap = new Dictionary<string, string>
        {
            ["0"] = "无酒精",
        },
    };
}

static void AssertTrue(bool actual, string message)
{
    if (!actual) throw new InvalidOperationException(message);
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', actual '{actual}'.");
    }
}

static void AssertNotEqual<T>(T left, T right, string message)
{
    if (EqualityComparer<T>.Default.Equals(left, right))
    {
        throw new InvalidOperationException($"{message} Both values were '{left}'.");
    }
}
