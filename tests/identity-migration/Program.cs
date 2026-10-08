using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using BepInEx.Logging;
using MystiaStewardCompanion.LocalApi;
using MystiaStewardCompanion.Updates;

// Only synthetic identities and a newly created test store. No game process or user's credentials.
if (args.Length != 2 || args[0] is not ("recovery" or "full" or "corrupt" or "future")) return 2;
var scenario = args[0];
using var profileFile = JsonDocument.Parse(File.ReadAllText(args[1]));
var profile = profileFile.RootElement.Clone();
var root = Path.Combine(Path.GetTempPath(), "mystia-identity-p0-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var log = Logger.CreateLogSource("identity-migration-p0");
var path = Path.Combine(root, "companion-devices.json");
try
{
    var initial = new CompanionDeviceAuthorityStore(path, log);
    var count = scenario == "full" ? 32 : 1;
    for (var index = 1; index <= count; ++index)
        initial.Register($"p0-old-device-{index:D8}", "Synthetic old device", new CompanionDeviceRegisterRequest
        {
            ProtocolVersion = 1, ProfileSchemaVersion = 4, Platform = "windows", AppVersion = "1.3.1",
            Profile = profile,
        }, DateTime.UtcNow.AddMinutes(-5));
    if (scenario == "corrupt") File.WriteAllText(path, "{broken-preserved");
    if (scenario == "future") File.WriteAllText(path, "{\"version\":999,\"future\":true}");
    // Reload drops synthetic liveness, proving recovery does not require the old client online.
    var store = new CompanionDeviceAuthorityStore(path, log);
    using var update = new UpdateService(new UpdateServiceSettings { Enabled = false, AutoCheck = false }, log,
        Path.Combine(root, "updates"), () => DateTime.UtcNow, _ => throw Forbidden());
    var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start();
    var port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
    using var server = new LocalApiServer(false, "auto", port, "identity-p0", "identity-p0-synthetic-token",
        () => throw Forbidden(), (_, _) => throw Forbidden(), _ => throw Forbidden(),
        () => throw Forbidden(), _ => throw Forbidden(), () => throw Forbidden(), _ => throw Forbidden(),
        (_, _, _) => throw Forbidden(), (_, _, _) => throw Forbidden(),
        _ => throw Forbidden(), _ => throw Forbidden(), _ => throw Forbidden(),
        1, _ => 0, _ => throw Forbidden(), () => throw Forbidden(), () => throw Forbidden(),
        (_, _) => throw Forbidden(), (_, _, _) => throw Forbidden(), (_, _, _) => throw Forbidden(),
        update, new FavoriteStore(Path.Combine(root, "favorites.json"), log),
        new CustomRecipeStore(Path.Combine(root, "custom-recipes.json"), log), store, log);
    server.Start();
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "identity-p0-real-local-api", port, root, scenario }));
    Console.Out.Flush();
    // EOF owns cleanup; there is no remote shutdown endpoint or token echo.
    await Console.In.ReadLineAsync();
    server.Dispose();
    Console.WriteLine("STOPPED");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("identity-p0-host-failed: " + exception.GetType().Name + ": " + exception.Message);
    return 1;
}
finally
{
    Logger.Sources.Remove(log);
    Directory.Delete(root, true);
}

static Exception Forbidden() => new InvalidOperationException("Game/update operation is outside the identity P0 fixture.");
