// 本工程检查任务定义读取器的源码约束，不加载或执行游戏程序集。
// 字符串存在性只能保护已声明的读取入口，不能证明实际原生字段与反射调用可用。
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
    "TryGetLanguageShape()",
    "TryReadConditionDescription(condition)",
    "bool includeConditionDescriptions = false",
    "includeConditionDescriptions ? TryReadConditionDescription(condition) : \"\"",
    "Enum.ToObject(statusType, 2)",
    "method.Name == \"GetText\"",
    "finished.GetRawConstantValue()");

// 明确排除会重新解析任务、更新完成状态或扩大扫描范围的入口，保持诊断读取的边界。
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

// 标题属于可缺失的展示信息；结构结果的构建顺序不能被标题可用性替代。
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
