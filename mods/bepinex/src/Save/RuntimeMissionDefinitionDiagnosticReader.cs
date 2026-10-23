using System.Globalization;
using System.Reflection;
using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeMissionDefinitionDiagnosticCondition(
    int Type,
    int Amount,
    string Description = "");

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
    private const string MissionStatusTypeName = "GameData.RunTime.Common.RunTimeScheduler+MissionStatus";
    private const string Il2CppDictionaryTypeName = "Il2CppSystem.Collections.Generic.Dictionary`2";
    private const string Il2CppReferenceArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1";
    private const int ServeInWorkConditionType = 4;
    private const int MaxConditionCount = 256;

    private static readonly object ShapeRoot = new();
    private static RuntimeMissionDefinitionShape? _definitionShape;
    private static RuntimeMissionLanguageShape? _languageShape;
    private static RuntimeMissionConditionTextShape? _conditionTextShape;

    /// <summary>
    /// 调用方必须位于 Unity 主线程，任何 native 对象均不逃出读取方法。
    /// 条件描述仅供活动任务在读档/新任务边界捕获；可接取来源的 fresh read 默认跳过，
    /// 避免每次浏览刷新都重复本地化不需要展示的条件，保持原有调度读取成本。
    /// </summary>
    public static RuntimeMissionDefinitionDiagnosticReadResult Read(
        string trustedLabel,
        bool includeConditionDescriptions = false)
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
                conditions[index] = new RuntimeMissionDefinitionDiagnosticCondition(
                    typeValue, amount,
                    includeConditionDescriptions ? TryReadConditionDescription(condition) : "");
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

    /// <summary>
    /// 使用游戏自己的本地化条件描述，不根据 amount/tag 的数值猜测语义。
    /// 当前 metadata / #783 interop 对应 GetText(MissionStatus)，RVA 0x7A9EA0；
    /// Native 的 Finished(2) 分支对组合任务/事件仅拼接名称，避免 Tracking 分支
    /// 进一步查询并重新评估子任务完成状态。这里的 Finished 仅选择纯文本格式，
    /// 绝不写回任务状态；完成事实仍由独立、已验证的 conditionFinishStates 提供。
    /// 此方法只在既有有界定义读取中执行，返回托管字符串，不增加轮询或全目录扫描。
    /// </summary>
    private static string TryReadConditionDescription(object condition)
    {
        try
        {
            RuntimeMissionConditionTextShape shape;
            lock (ShapeRoot)
            {
                if (_conditionTextShape == null)
                {
                    var conditionType = RequireType(FinishConditionTypeName);
                    var statusType = RequireType(MissionStatusTypeName);
                    var finished = statusType.GetField(
                        "Finished", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                    if (!statusType.IsEnum || Enum.GetUnderlyingType(statusType) != typeof(int)
                        || finished == null || !finished.IsLiteral || finished.FieldType != statusType
                        || Convert.ToInt32(finished.GetRawConstantValue(), CultureInfo.InvariantCulture) != 2)
                        return "";
                    var matches = conditionType.GetMethods(
                            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                        .Where(method => method.Name == "GetText" && !method.IsGenericMethod
                            && method.ReturnType == typeof(string)
                            && method.GetParameters().Select(parameter => parameter.ParameterType)
                                .SequenceEqual(new[] { statusType }))
                        .Take(2).ToArray();
                    if (matches.Length != 1) return "";
                    _conditionTextShape = new RuntimeMissionConditionTextShape(
                        matches[0], Enum.ToObject(statusType, 2));
                }
                shape = _conditionTextShape;
            }
            return NormalizeConditionDescription(
                shape.GetText.Invoke(condition, new[] { shape.DescriptionOnlyStatus }) as string);
        }
        catch
        {
            // 本地化资料暂不可用不能破坏已确认的条件状态，也不能回退到推测的任务文案。
            return "";
        }
    }

    /// <summary>仅移除游戏的常见富文本样式；保留任务内容、换行及普通比较符号。</summary>
    internal static string NormalizeConditionDescription(string? text)
        => GameDisplayText.Normalize(text);

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

    private sealed record RuntimeMissionConditionTextShape(
        MethodInfo GetText,
        object DescriptionOnlyStatus);

    private readonly record struct RuntimeMissionTitleReadResult(string Title, string Status)
    {
        public static RuntimeMissionTitleReadResult Unavailable(string status)
        {
            return new RuntimeMissionTitleReadResult("", status);
        }
    }
}
