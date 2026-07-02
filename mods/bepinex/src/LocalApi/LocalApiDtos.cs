using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

internal sealed class LocalApiHealthDto
{
    public bool Ok { get; init; }
    public string PluginVersion { get; init; } = "";
    public string BindAddress { get; init; } = "";
    public int Port { get; init; }
    public bool AuthRequired { get; init; }
    public string LocalEndpoint { get; init; } = "";
    public bool LanEnabled { get; init; }
    public bool LanRunning { get; init; }
    public IReadOnlyList<string> LanBindAddresses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LanEndpoints { get; init; } = Array.Empty<string>();
    public string? LanError { get; init; }
}

internal sealed class LocalApiErrorDto
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
}

internal sealed class LocalApiLogFileDto
{
    public string CapturedAtUtc { get; init; } = "";
    public string Path { get; init; } = "";
    public bool Exists { get; init; }
    public bool Enabled { get; init; }
    public int MaxLines { get; init; }
    public int MaxBytes { get; init; }
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

internal sealed class LocalApiLogSettingsDto
{
    public bool LogAccessEnabled { get; init; }
    public string LogOutputPath { get; init; } = "";
    public string LogOutputDirectory { get; init; } = "";
    public int MaxLogLines { get; init; }
    public int MaxLogBytes { get; init; }
    public bool NightBusinessDiagnosticsEnabled { get; init; }
    public string NightBusinessDiagnosticsPath { get; init; } = "";
    public string NightBusinessDiagnosticsDirectory { get; init; } = "";
    public bool AggregateModLogEnabled { get; init; }
    public string AggregateModLogPath { get; init; } = "";
    public string AggregateModLogDirectory { get; init; } = "";
    public long AggregateModLogMaxFileBytes { get; init; }
    public bool NativeBepInExConsoleEnabled { get; init; }
    public bool NativeBepInExConsoleVisible { get; init; }
}

internal sealed class LocalApiConnectionConfigDto
{
    public bool Ok { get; init; } = true;
    public string LocalEndpoint { get; init; } = "";
    public bool LanEnabled { get; init; }
    public bool LanRunning { get; init; }
    public string LanBindHost { get; init; } = "auto";
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public IReadOnlyList<string> LanBindAddresses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LanEndpoints { get; init; } = Array.Empty<string>();
    public string? LanError { get; init; }
    public string? Error { get; init; }
}

internal sealed class LocalApiConnectionConfigUpdate
{
    public bool? LanEnabled { get; init; }
    public string? LanBindHost { get; init; }
}

internal sealed class LocalApiAutomationLeaseDto
{
    public bool Ok { get; init; } = true;
    public bool Owned { get; init; }
    public string ClientId { get; init; } = "";
    public string ClientLabel { get; init; } = "";
    public string OwnerClientId { get; init; } = "";
    public string OwnerLabel { get; init; } = "";
    public string OwnerLastSeenUtc { get; init; } = "";
    public string ExpiresAtUtc { get; init; } = "";
    public int TtlMs { get; init; }
    public string? Error { get; init; }
}

internal sealed class LocalApiDirectoryActionDto
{
    public bool Ok { get; init; }
    public string Directory { get; init; } = "";
    public string? Error { get; init; }
}

internal sealed class LocalApiDiagnosticPackageDto
{
    public bool Ok { get; init; }
    public string Path { get; init; } = "";
    public string Directory { get; init; } = "";
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

internal sealed class LocalApiDiagnosticManifestDto
{
    public string GeneratedAtUtc { get; init; } = "";
    public string BaseUrl { get; init; } = "";
    public string LogOutputPath { get; init; } = "";
    public string AutomationLogPath { get; init; } = "";
    public string NightBusinessDiagnosticsPath { get; init; } = "";
    public string AggregateModLogPath { get; init; } = "";
    public long AggregateModLogMaxFileBytes { get; init; }
    public int MaxLogLines { get; init; }
    public int MaxLogBytes { get; init; }
}

internal sealed class LocalApiInventoryEditDto
{
    public bool Ok { get; init; }
    public string Type { get; init; } = "";
    public int Id { get; init; }
    public int RequestedQuantity { get; init; }
    public int PreviousQuantity { get; init; }
    public int Quantity { get; init; }
    public bool Changed { get; init; }
    public string? Error { get; init; }
}

internal sealed class LocalApiInventoryBulkEditDto
{
    public bool Ok { get; init; }
    public string Type { get; init; } = "";
    public int RequestedQuantity { get; init; }
    public int Total { get; init; }
    public int Changed { get; init; }
    public int Unchanged { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    public string? Error { get; init; }
}

internal sealed class LocalApiOrderActionErrorDto
{
    public bool Ok { get; init; }
    public bool Prepared { get; init; }
    public string Error { get; init; } = "";
    public OrderPreparationOrder Order { get; init; } = new()
    {
        DeskCode = -1,
        GuestName = "",
        FoodTag = "",
        BeverageTag = "",
    };
    public int RecipeId { get; init; } = -1;
    public string RecipeName { get; init; } = "";
    public int BeverageId { get; init; } = -1;
    public string BeverageName { get; init; } = "";
    public IReadOnlyList<OrderPreparationStep> Steps { get; init; } = Array.Empty<OrderPreparationStep>();
}

internal sealed class LocalApiRareGuestInvitationErrorDto
{
    public bool Ok { get; init; }
    public bool RuntimeAvailable { get; init; }
    public string Status { get; init; } = "";
    public string Error { get; init; } = "";
    public int CandidateCount { get; init; }
    public int UsableCount { get; init; }
    public int ExistingSlotCount { get; init; }
    public int ExistingControlledCount { get; init; }
    public int ScheduledSlotCount { get; init; }
    public int InvitedCount { get; init; }
    public int SkippedCount { get; init; }
    public string Scope { get; init; } = "current";
    public string CurrentMapLabel { get; init; } = "";
    public string CurrentMapName { get; init; } = "";
    public IReadOnlyList<object> Candidates { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Available { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Invited { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Skipped { get; init; } = Array.Empty<object>();
}

internal sealed class LocalApiRareOrderDismissDto
{
    public bool Ok { get; init; }
    public int Removed { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
}

internal sealed class LocalApiStatusDto
{
    public bool Ok { get; init; }
    public string Status { get; init; } = "";
    public string? Error { get; init; }
}

internal sealed class LocalApiFavoriteStoreDto
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<object> Recipes { get; init; } = Array.Empty<object>();
    public IReadOnlyList<object> Beverages { get; init; } = Array.Empty<object>();
}

internal sealed class LocalApiFavoriteErrorDto
{
    public bool Ok { get; init; }
    public LocalApiFavoriteStoreDto Favorites { get; init; } = new();
    public string Error { get; init; } = "";
}

internal sealed class LocalApiCustomRecipeStoreDto
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<object> Recipes { get; init; } = Array.Empty<object>();
}

internal sealed class LocalApiCustomRecipeErrorDto
{
    public bool Ok { get; init; }
    public LocalApiCustomRecipeStoreDto CustomRecipes { get; init; } = new();
    public string Error { get; init; } = "";
}
