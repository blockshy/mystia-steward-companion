var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var sourcePath = Path.Combine(
    repositoryRoot,
    "mods",
    "bepinex",
    "src",
    "Save",
    "RuntimeMissionDefinitionDiagnosticReader.cs");
var source = File.ReadAllText(sourcePath);

RequireContains(
    source,
    "GameData.Core.Collections.DataBaseScheduler",
    "GameData.Profile.SchedulerNodeCollection.MissionNode",
    "GameData.Profile.SchedulerNodeCollection.MissionNode+FinishCondition",
    "GameData.Profile.SchedulerNodeCollection.MissionNode+FinishCondition+ConditionType",
    "GameData.CoreLanguage.Collections.DataBaseLanguage",
    "GameData.CoreLanguage.LanguageBase",
    "\"TargetNodeExists\"",
    "\"RefMission\"",
    "\"hasReciever\"",
    "\"reciever\"",
    "\"finishCondition\"",
    "\"conditionType\"",
    "\"amount\"",
    "\"Missions\"",
    "\"Name\"",
    "BindingFlags.DeclaredOnly",
    "RuntimeConcreteCollectionReader.TryReadReferenceArray",
    "RuntimeConcreteCollectionReader.TryGetDictionaryValue",
    "private const int ServeInWorkConditionType = 4",
    "private const int MaxConditionCount = 256",
    "condition.GetType() != shape.FinishConditionType",
    "rawLanguage.GetType() != shape.LanguageBaseType",
    "private static readonly object ShapeRoot = new()",
    "GetDefinitionShape()",
    "TryGetLanguageShape()");

RequireAbsent(
    source,
    "GetMissionLanguage",
    "GetAllNodes",
    "AllNodesMapping",
    "GetAllMissionData",
    "GetTrackedMissionData",
    "ParseActiveMissionData",
    "HasFulfilled",
    "UpdateFinishStates",
    "GetMemberValue",
    "m_Name",
    ".ToString(");

var titleRead = source.IndexOf("var title = TryReadTitle(trustedLabel);", StringComparison.Ordinal);
var definitionRead = source.IndexOf(
    "var definition = new RuntimeMissionDefinitionDiagnostic(",
    StringComparison.Ordinal);
if (titleRead < 0 || definitionRead <= titleRead)
{
    throw new InvalidOperationException(
        "The structural mission result must remain independent from title availability.");
}

Console.WriteLine("Runtime mission definition source audit passed.");
return;

static string FindRepositoryRoot(string startPath)
{
    var current = new DirectoryInfo(startPath);
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "package.json"))
            && Directory.Exists(Path.Combine(current.FullName, "mods", "bepinex")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new DirectoryNotFoundException("Repository root was not found.");
}

static void RequireContains(string source, params string[] values)
{
    foreach (var value in values)
    {
        if (!source.Contains(value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Required source contract is missing: {value}");
        }
    }
}

static void RequireAbsent(string source, params string[] values)
{
    foreach (var value in values)
    {
        if (source.Contains(value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Forbidden source contract is present: {value}");
        }
    }
}
