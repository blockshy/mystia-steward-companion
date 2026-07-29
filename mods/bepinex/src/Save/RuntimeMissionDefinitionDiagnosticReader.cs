using System.Globalization;
using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeMissionDefinitionDiagnosticCondition(
    int Type,
    int Amount);

internal sealed record RuntimeMissionDefinitionDiagnostic(
    string Label,
    string Title,
    string TitleStatus,
    bool HasReceiver,
    string Receiver,
    int ConditionCount,
    IReadOnlyList<RuntimeMissionDefinitionDiagnosticCondition> Conditions,
    IReadOnlyList<int> ServeInWorkFoodIds);

internal sealed record RuntimeMissionDefinitionDiagnosticReadResult(
    bool Success,
    RuntimeMissionDefinitionDiagnostic? Definition,
    string Failure)
{
    public static RuntimeMissionDefinitionDiagnosticReadResult Failed(string failure)
    {
        return new RuntimeMissionDefinitionDiagnosticReadResult(false, null, failure);
    }
}

internal static class RuntimeMissionDefinitionDiagnosticReader
{
    private const string DataBaseSchedulerTypeName = "GameData.Core.Collections.DataBaseScheduler";
    private const string DataBaseLanguageTypeName = "GameData.CoreLanguage.Collections.DataBaseLanguage";
    private const string MissionNodeTypeName = "GameData.Profile.SchedulerNodeCollection.MissionNode";
    private const string FinishConditionTypeName =
        "GameData.Profile.SchedulerNodeCollection.MissionNode+FinishCondition";
    private const string ConditionTypeName =
        "GameData.Profile.SchedulerNodeCollection.MissionNode+FinishCondition+ConditionType";
    private const string LanguageBaseTypeName = "GameData.CoreLanguage.LanguageBase";
    private const string Il2CppDictionaryTypeName = "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppReferenceArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1";
    private const int ServeInWorkConditionType = 4;
    private const int MaxConditionCount = 256;

    private static readonly object ShapeRoot = new();
    private static RuntimeMissionDefinitionShape? _definitionShape;
    private static RuntimeMissionLanguageShape? _languageShape;

    // Callers must invoke this reader on the Unity main thread. Native objects never escape the method.
    public static RuntimeMissionDefinitionDiagnosticReadResult Read(string trustedLabel)
    {
        if (string.IsNullOrWhiteSpace(trustedLabel))
        {
            return RuntimeMissionDefinitionDiagnosticReadResult.Failed("mission-label-missing");
        }

        try
        {
            var shape = GetDefinitionShape();
            if (shape.TargetNodeExists.Invoke(
                    null,
                    new object?[] { trustedLabel }) is not bool exists)
            {
                throw new InvalidOperationException(
                    $"{DataBaseSchedulerTypeName}.TargetNodeExists returned a non-Boolean value.");
            }

            if (!exists)
            {
                return RuntimeMissionDefinitionDiagnosticReadResult.Failed("mission-label-not-found");
            }

            var mission = shape.RefMission.Invoke(
                null,
                new object?[] { trustedLabel });
            if (mission == null || mission.GetType() != shape.MissionNodeType)
            {
                throw new InvalidOperationException(
                    $"{DataBaseSchedulerTypeName}.RefMission returned an unexpected mission type.");
            }

            if (shape.HasReceiver.GetValue(mission) is not bool hasReceiver)
            {
                throw new InvalidOperationException(
                    $"{MissionNodeTypeName}.hasReciever returned a non-Boolean value.");
            }

            var rawReceiver = shape.Receiver.GetValue(mission);
            if (rawReceiver != null && rawReceiver is not string)
            {
                throw new InvalidOperationException(
                    $"{MissionNodeTypeName}.reciever returned a non-String value.");
            }

            var receiver = rawReceiver as string ?? "";
            if (hasReceiver && string.IsNullOrWhiteSpace(receiver))
            {
                throw new InvalidOperationException(
                    $"{MissionNodeTypeName}.reciever is empty while hasReciever is true.");
            }

            var rawConditions = shape.FinishConditions.GetValue(mission);
            if (!RuntimeConcreteCollectionReader.TryReadReferenceArray(
                    rawConditions,
                    out var conditionObjects,
                    out var arrayFailure))
            {
                throw new InvalidOperationException(
                    $"{MissionNodeTypeName}.finishCondition is unreadable: {arrayFailure}.");
            }

            if (conditionObjects.Count > MaxConditionCount)
            {
                throw new InvalidOperationException(
                    $"{MissionNodeTypeName}.finishCondition exceeds the {MaxConditionCount}-item limit.");
            }

            var conditions = new RuntimeMissionDefinitionDiagnosticCondition[conditionObjects.Count];
            var serveInWorkFoodIds = new List<int>();
            for (var index = 0; index < conditionObjects.Count; index++)
            {
                var condition = conditionObjects[index];
                if (condition == null || condition.GetType() != shape.FinishConditionType)
                {
                    throw new InvalidOperationException(
                        $"{MissionNodeTypeName}.finishCondition[{index}] has an unexpected element type.");
                }

                var rawConditionType = shape.ConditionType.GetValue(condition);
                if (rawConditionType == null
                    || rawConditionType.GetType() != shape.ConditionTypeType)
                {
                    throw new InvalidOperationException(
                        $"{FinishConditionTypeName}.conditionType returned an unexpected enum type.");
                }

                var rawAmount = shape.Amount.GetValue(condition);
                if (rawAmount is not int amount)
                {
                    throw new InvalidOperationException(
                        $"{FinishConditionTypeName}.amount returned a non-Int32 value.");
                }

                var typeValue = Convert.ToInt32(rawConditionType, CultureInfo.InvariantCulture);
                conditions[index] = new RuntimeMissionDefinitionDiagnosticCondition(typeValue, amount);
                if (typeValue == ServeInWorkConditionType)
                {
                    if (amount < 0)
                    {
                        throw new InvalidOperationException(
                            $"{FinishConditionTypeName}.amount contains an invalid ServeInWork food ID.");
                    }

                    serveInWorkFoodIds.Add(amount);
                }
            }

            var title = TryReadTitle(trustedLabel);
            var definition = new RuntimeMissionDefinitionDiagnostic(
                trustedLabel,
                title.Title,
                title.Status,
                hasReceiver,
                receiver,
                conditions.Length,
                conditions,
                serveInWorkFoodIds.ToArray());
            return new RuntimeMissionDefinitionDiagnosticReadResult(true, definition, "");
        }
        catch (Exception ex)
        {
            return RuntimeMissionDefinitionDiagnosticReadResult.Failed(
                $"mission-definition-read-failed:{DescribeException(ex)}");
        }
    }

    private static RuntimeMissionTitleReadResult TryReadTitle(string label)
    {
        try
        {
            var shape = TryGetLanguageShape();
            if (shape == null)
            {
                return RuntimeMissionTitleReadResult.Unavailable("unavailable:language-types-not-loaded");
            }

            var missions = shape.Missions.GetValue(null);
            if (missions == null)
            {
                return RuntimeMissionTitleReadResult.Unavailable("unavailable:missions-not-loaded");
            }

            if (!RuntimeConcreteCollectionReader.TryGetDictionaryValue(
                    missions,
                    label,
                    out var rawLanguage,
                    out var found,
                    out var dictionaryFailure))
            {
                return RuntimeMissionTitleReadResult.Unavailable(
                    $"unavailable:missions-{DescribeCollectionFailure(dictionaryFailure)}");
            }

            if (!found)
            {
                return RuntimeMissionTitleReadResult.Unavailable("unavailable:title-key-missing");
            }

            if (rawLanguage == null || rawLanguage.GetType() != shape.LanguageBaseType)
            {
                return RuntimeMissionTitleReadResult.Unavailable("unavailable:title-value-type");
            }

            var rawName = shape.Name.GetValue(rawLanguage);
            if (rawName is not string title || string.IsNullOrWhiteSpace(title))
            {
                return RuntimeMissionTitleReadResult.Unavailable("unavailable:title-empty");
            }

            return new RuntimeMissionTitleReadResult(title.Trim(), "available");
        }
        catch
        {
            return RuntimeMissionTitleReadResult.Unavailable("unavailable:title-read-failed");
        }
    }

    private static RuntimeMissionDefinitionShape GetDefinitionShape()
    {
        lock (ShapeRoot)
        {
            if (_definitionShape != null) return _definitionShape;

            var schedulerType = RequireType(DataBaseSchedulerTypeName);
            var missionNodeType = RequireType(MissionNodeTypeName);
            var finishConditionType = RequireType(FinishConditionTypeName);
            var conditionType = RequireType(ConditionTypeName);
            ValidateServeInWorkLiteral(conditionType);

            _definitionShape = new RuntimeMissionDefinitionShape(
                missionNodeType,
                finishConditionType,
                conditionType,
                RequireExactStaticMethod(
                    schedulerType,
                    "TargetNodeExists",
                    typeof(bool),
                    typeof(string)),
                RequireExactStaticMethod(
                    schedulerType,
                    "RefMission",
                    missionNodeType,
                    typeof(string)),
                RequireExactInstanceProperty(
                    missionNodeType,
                    "hasReciever",
                    typeof(bool)),
                RequireExactInstanceProperty(
                    missionNodeType,
                    "reciever",
                    typeof(string)),
                RequireExactInstanceProperty(
                    missionNodeType,
                    "finishCondition",
                    propertyType => IsExactClosedGeneric(
                        propertyType,
                        Il2CppReferenceArrayTypeName,
                        finishConditionType)),
                RequireExactInstanceProperty(
                    finishConditionType,
                    "conditionType",
                    conditionType),
                RequireExactInstanceProperty(
                    finishConditionType,
                    "amount",
                    typeof(int)));
            return _definitionShape;
        }
    }

    private static RuntimeMissionLanguageShape? TryGetLanguageShape()
    {
        lock (ShapeRoot)
        {
            if (_languageShape != null) return _languageShape;

            var languageType = RuntimeReflectionUtility.FindType(DataBaseLanguageTypeName);
            var languageBaseType = RuntimeReflectionUtility.FindType(LanguageBaseTypeName);
            if (languageType == null || languageBaseType == null) return null;

            _languageShape = new RuntimeMissionLanguageShape(
                languageBaseType,
                RequireExactStaticProperty(
                    languageType,
                    "Missions",
                    propertyType => IsExactClosedGeneric(
                        propertyType,
                        Il2CppDictionaryTypeName,
                        typeof(string),
                        languageBaseType)),
                RequireExactInstanceProperty(
                    languageBaseType,
                    "Name",
                    typeof(string)));
            return _languageShape;
        }
    }

    private static Type RequireType(string fullName)
    {
        return RuntimeReflectionUtility.FindType(fullName)
            ?? throw new InvalidOperationException($"{fullName} is not loaded.");
    }

    private static MethodInfo RequireExactStaticMethod(
        Type declaringType,
        string methodName,
        Type returnType,
        params Type[] parameterTypes)
    {
        var matches = declaringType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method =>
                string.Equals(method.Name, methodName, StringComparison.Ordinal)
                && !method.IsGenericMethod
                && method.ReturnType == returnType
                && method.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(parameterTypes))
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new MissingMethodException(declaringType.FullName, methodName);
    }

    private static PropertyInfo RequireExactInstanceProperty(
        Type declaringType,
        string propertyName,
        Type propertyType)
    {
        return RequireExactProperty(
            declaringType,
            propertyName,
            isStatic: false,
            candidateType => candidateType == propertyType);
    }

    private static PropertyInfo RequireExactInstanceProperty(
        Type declaringType,
        string propertyName,
        Func<Type, bool> propertyTypePredicate)
    {
        return RequireExactProperty(
            declaringType,
            propertyName,
            isStatic: false,
            propertyTypePredicate);
    }

    private static PropertyInfo RequireExactStaticProperty(
        Type declaringType,
        string propertyName,
        Func<Type, bool> propertyTypePredicate)
    {
        return RequireExactProperty(
            declaringType,
            propertyName,
            isStatic: true,
            propertyTypePredicate);
    }

    private static PropertyInfo RequireExactProperty(
        Type declaringType,
        string propertyName,
        bool isStatic,
        Func<Type, bool> propertyTypePredicate)
    {
        var flags = BindingFlags.Public
            | BindingFlags.DeclaredOnly
            | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        var matches = declaringType
            .GetProperties(flags)
            .Where(property =>
            {
                if (!string.Equals(property.Name, propertyName, StringComparison.Ordinal)
                    || property.GetIndexParameters().Length != 0
                    || !propertyTypePredicate(property.PropertyType))
                {
                    return false;
                }

                var getter = property.GetGetMethod(nonPublic: false);
                return getter != null && getter.IsStatic == isStatic;
            })
            .Take(2)
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new MissingMemberException(declaringType.FullName, propertyName);
    }

    private static bool IsExactClosedGeneric(
        Type candidate,
        string genericDefinitionFullName,
        params Type[] genericArguments)
    {
        return candidate.IsGenericType
            && string.Equals(
                candidate.GetGenericTypeDefinition().FullName,
                genericDefinitionFullName,
                StringComparison.Ordinal)
            && candidate.GetGenericArguments().SequenceEqual(genericArguments);
    }

    private static void ValidateServeInWorkLiteral(Type conditionType)
    {
        if (!conditionType.IsEnum || Enum.GetUnderlyingType(conditionType) != typeof(int))
        {
            throw new InvalidOperationException($"{ConditionTypeName} is not an Int32 enum.");
        }

        var field = conditionType.GetField(
            "ServeInWork",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (field == null
            || !field.IsLiteral
            || field.FieldType != conditionType
            || Convert.ToInt32(field.GetRawConstantValue(), CultureInfo.InvariantCulture)
                != ServeInWorkConditionType)
        {
            throw new InvalidOperationException(
                $"{ConditionTypeName}.ServeInWork does not equal {ServeInWorkConditionType}.");
        }
    }

    private static string DescribeException(Exception exception)
    {
        var current = exception;
        while (current is TargetInvocationException { InnerException: not null })
        {
            current = current.InnerException;
        }

        return $"{current.GetType().Name}:{current.Message}";
    }

    private static string DescribeCollectionFailure(RuntimeCollectionReadFailure failure)
    {
        return failure switch
        {
            RuntimeCollectionReadFailure.None => "none",
            RuntimeCollectionReadFailure.Missing => "missing",
            RuntimeCollectionReadFailure.UnsupportedShape => "unsupported-shape",
            RuntimeCollectionReadFailure.InvocationFailed => "invocation-failed",
            RuntimeCollectionReadFailure.CountMismatch => "count-mismatch",
            RuntimeCollectionReadFailure.ElementTypeMismatch => "element-type-mismatch",
            _ => "unknown",
        };
    }

    private sealed record RuntimeMissionDefinitionShape(
        Type MissionNodeType,
        Type FinishConditionType,
        Type ConditionTypeType,
        MethodInfo TargetNodeExists,
        MethodInfo RefMission,
        PropertyInfo HasReceiver,
        PropertyInfo Receiver,
        PropertyInfo FinishConditions,
        PropertyInfo ConditionType,
        PropertyInfo Amount);

    private sealed record RuntimeMissionLanguageShape(
        Type LanguageBaseType,
        PropertyInfo Missions,
        PropertyInfo Name);

    private readonly record struct RuntimeMissionTitleReadResult(string Title, string Status)
    {
        public static RuntimeMissionTitleReadResult Unavailable(string status)
        {
            return new RuntimeMissionTitleReadResult("", status);
        }
    }
}
