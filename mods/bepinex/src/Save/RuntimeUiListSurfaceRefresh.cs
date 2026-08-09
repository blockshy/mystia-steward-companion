using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal enum RuntimeUiListSurfaceKind
{
    Cooking,
    Beverage,
}

/// <summary>
/// Exact binding for the game's bounded list refresh stages. Cooking refreshes both ingredient and
/// recipe backing data before rebuilding either visible logical group; beverage refreshes its one
/// backing list and visible group. The binding deliberately has no member aliases or alternate
/// panel entry points.
/// </summary>
internal sealed class RuntimeUiListSurfaceRefreshBinding
{
    private const int BeverageOpenTypeValue = 1;
    private readonly RuntimeUiListSurfaceRefreshStep[] _steps;

    private RuntimeUiListSurfaceRefreshBinding(
        RuntimeUiListSurfaceKind kind,
        Type panelType,
        RuntimeUiListSurfaceRefreshStep[] steps,
        PropertyInfo? openTypeProperty)
    {
        Kind = kind;
        PanelType = panelType;
        _steps = steps;
        OpenTypeProperty = openTypeProperty;
    }

    internal RuntimeUiListSurfaceKind Kind { get; }

    internal Type PanelType { get; }

    internal IReadOnlyList<RuntimeUiListSurfaceRefreshStep> Steps => _steps;

    internal PropertyInfo? OpenTypeProperty { get; }

    internal static bool TryCreate(
        RuntimeUiListSurfaceKind kind,
        Type panelType,
        out RuntimeUiListSurfaceRefreshBinding binding,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(panelType);
        binding = null!;

        var definitions = kind switch
        {
            RuntimeUiListSurfaceKind.Cooking => new[]
            {
                RuntimeUiListSurfaceRefreshStepDefinition.Panel(
                    "ingredient-backing-data",
                    "UpdateIngField"),
                RuntimeUiListSurfaceRefreshStepDefinition.Panel(
                    "recipe-backing-data",
                    "UpdateRecipeField"),
                RuntimeUiListSurfaceRefreshStepDefinition.Group(
                    "ingredient-visible-elements",
                    "m_StaticIngredientsGroup"),
                RuntimeUiListSurfaceRefreshStepDefinition.Group(
                    "recipe-visible-elements",
                    "m_StaticRecipeGroup"),
            },
            RuntimeUiListSurfaceKind.Beverage => new[]
            {
                RuntimeUiListSurfaceRefreshStepDefinition.Panel(
                    "beverage-backing-data",
                    "UpdateBevField"),
                RuntimeUiListSurfaceRefreshStepDefinition.Group(
                    "beverage-visible-elements",
                    "m_BevsGroup"),
            },
            _ => Array.Empty<RuntimeUiListSurfaceRefreshStepDefinition>(),
        };
        if (definitions.Length == 0)
        {
            failure = $"unsupported surface kind {kind}";
            return false;
        }

        var steps = new RuntimeUiListSurfaceRefreshStep[definitions.Length];
        for (var index = 0; index < definitions.Length; index++)
        {
            if (!TryCreateStep(panelType, definitions[index], out steps[index], out failure))
            {
                return false;
            }
        }

        PropertyInfo? openTypeProperty = null;
        if (kind == RuntimeUiListSurfaceKind.Beverage)
        {
            openTypeProperty = panelType.GetProperty(
                "openType",
                BindingFlags.Public | BindingFlags.Instance);
            if (!IsExactReadableInstanceProperty(openTypeProperty)
                || openTypeProperty!.DeclaringType != panelType)
            {
                failure = "exact public property openType missing";
                return false;
            }
        }

        binding = new RuntimeUiListSurfaceRefreshBinding(
            kind,
            panelType,
            steps,
            openTypeProperty);
        failure = "";
        return true;
    }

    internal bool IsApplicablePanel(object panel, out string failure)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (!PanelType.IsInstanceOfType(panel))
        {
            failure = "panel wrapper type mismatch";
            return false;
        }

        if (Kind != RuntimeUiListSurfaceKind.Beverage)
        {
            failure = "";
            return true;
        }

        object? rawOpenType;
        try
        {
            rawOpenType = OpenTypeProperty!.GetValue(panel);
        }
        catch (Exception ex)
        {
            failure = $"openType read failed: {Unwrap(ex).Message}";
            return false;
        }

        if (!TryReadExactInt32(rawOpenType, out var openTypeValue))
        {
            failure = "openType was not an exact enum/int value";
            return false;
        }
        if (openTypeValue != BeverageOpenTypeValue)
        {
            failure = $"openType was not Beverage({BeverageOpenTypeValue})";
            return false;
        }

        failure = "";
        return true;
    }

    internal void Refresh(object panel)
    {
        if (!IsApplicablePanel(panel, out var applicabilityFailure))
        {
            throw new RuntimeUiListSurfaceRefreshException("panel-mode", applicabilityFailure);
        }

        foreach (var step in _steps) step.Invoke(panel);
    }

    private static bool TryCreateStep(
        Type panelType,
        RuntimeUiListSurfaceRefreshStepDefinition definition,
        out RuntimeUiListSurfaceRefreshStep step,
        out string failure)
    {
        step = null!;
        if (definition.PanelMethodName != null)
        {
            var methods = FindExactVoidInstanceMethods(panelType, definition.PanelMethodName);
            if (methods.Length != 1)
            {
                failure = $"{definition.Stage}: {definition.PanelMethodName}/0 exact count={methods.Length}";
                return false;
            }

            step = new RuntimeUiListSurfaceRefreshStep(
                definition.Stage,
                methods[0],
                receiverProperty: null);
            failure = "";
            return true;
        }

        var groupPropertyName = definition.GroupPropertyName!;
        var groupProperty = panelType.GetProperty(
            groupPropertyName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        if (!IsExactReadableInstanceProperty(groupProperty)
            || groupProperty!.DeclaringType != panelType)
        {
            failure = $"{definition.Stage}: exact public property {groupPropertyName} missing";
            return false;
        }

        var groupRefreshMethods = FindExactVoidInstanceMethods(
            groupProperty.PropertyType,
            "UpdateElements");
        if (groupRefreshMethods.Length != 1)
        {
            failure = $"{definition.Stage}: {groupPropertyName}.UpdateElements/0 exact count={groupRefreshMethods.Length}";
            return false;
        }

        step = new RuntimeUiListSurfaceRefreshStep(
            definition.Stage,
            groupRefreshMethods[0],
            groupProperty);
        failure = "";
        return true;
    }

    private static MethodInfo[] FindExactVoidInstanceMethods(Type type, string methodName)
    {
        return type
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName
                && !method.IsStatic
                && !method.IsGenericMethod
                && method.GetParameters().Length == 0
                && method.ReturnType == typeof(void)
                && method.DeclaringType == type)
            .ToArray();
    }

    private static bool IsExactReadableInstanceProperty(PropertyInfo? property)
    {
        return property != null
            && property.DeclaringType != null
            && property.GetIndexParameters().Length == 0
            && property.GetMethod is { IsPublic: true, IsStatic: false };
    }

    private static bool TryReadExactInt32(object? value, out int result)
    {
        if (value is int intValue)
        {
            result = intValue;
            return true;
        }
        if (value != null
            && value.GetType().IsEnum
            && Enum.GetUnderlyingType(value.GetType()) == typeof(int))
        {
            try
            {
                result = Convert.ToInt32(value);
                return true;
            }
            catch
            {
                // Only exact Int32-backed enum values are accepted.
            }
        }

        result = 0;
        return false;
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception.GetBaseException();
    }

}

internal sealed class RuntimeUiListSurfaceRefreshStep
{
    internal RuntimeUiListSurfaceRefreshStep(
        string stage,
        MethodInfo refreshMethod,
        PropertyInfo? receiverProperty)
    {
        Stage = stage;
        RefreshMethod = refreshMethod;
        ReceiverProperty = receiverProperty;
    }

    internal string Stage { get; }

    internal MethodInfo RefreshMethod { get; }

    internal PropertyInfo? ReceiverProperty { get; }

    internal void Invoke(object panel)
    {
        object receiver = panel;
        if (ReceiverProperty != null)
        {
            try
            {
                receiver = ReceiverProperty.GetValue(panel)
                    ?? throw new InvalidOperationException(
                        $"{ReceiverProperty.Name} returned a null logical group");
                if (!ReceiverProperty.PropertyType.IsInstanceOfType(receiver))
                {
                    throw new InvalidOperationException(
                        $"{ReceiverProperty.Name} logical group wrapper type mismatch");
                }

                var groupPointer = RuntimeReflectionUtility.ReadObjectPointer(receiver);
                if (groupPointer == 0)
                {
                    throw new InvalidOperationException(
                        $"{ReceiverProperty.Name} logical group native pointer was zero");
                }
            }
            catch (Exception ex)
            {
                var root = Unwrap(ex);
                throw new RuntimeUiListSurfaceRefreshException(Stage, root.Message, root);
            }
        }

        try
        {
            RefreshMethod.Invoke(receiver, Array.Empty<object?>());
        }
        catch (Exception ex)
        {
            var root = Unwrap(ex);
            throw new RuntimeUiListSurfaceRefreshException(Stage, root.Message, root);
        }
    }

    private static Exception Unwrap(Exception exception)
    {
        return exception is TargetInvocationException { InnerException: not null }
            ? exception.InnerException
            : exception.GetBaseException();
    }
}

internal readonly record struct RuntimeUiListSurfaceRefreshStepDefinition(
    string Stage,
    string? PanelMethodName,
    string? GroupPropertyName)
{
    internal static RuntimeUiListSurfaceRefreshStepDefinition Panel(
        string stage,
        string methodName) => new(stage, methodName, null);

    internal static RuntimeUiListSurfaceRefreshStepDefinition Group(
        string stage,
        string propertyName) => new(stage, null, propertyName);
}

internal sealed class RuntimeUiListSurfaceRefreshException : Exception
{
    internal RuntimeUiListSurfaceRefreshException(
        string stage,
        string message,
        Exception? innerException = null)
        : base($"stage={stage}: {message}", innerException)
    {
        Stage = stage;
    }

    internal string Stage { get; }
}
