using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal sealed record MizuchiOrderIdentity(
    bool Verified,
    bool IsPossessed,
    int? OrderGuestId,
    int? ControllerGuestId,
    int? GroupGuestId,
    int? SelectedGuestId,
    int? ControlledGuestId,
    int? ControlType,
    int? TargetIngredientId,
    bool? IsMizuchiChallenge,
    int? CatchCount,
    int? RequiredCatchCount,
    nint OrderPointer,
    nint ControllerPointer,
    nint CallbackPointer,
    nint ClosurePointer,
    nint ParentClosurePointer,
    string CallbackMethod,
    string Reason)
{
    private const string SpecialOrderTypeName =
        "NightScene.GuestManagementUtility.GuestsManager+SpecialOrder";
    private const string GuestBaseTypeName =
        "GameData.Core.Collections.NightSceneUtility.GuestBase";
    private const string SpecialGuestTypeName =
        "GameData.Core.Collections.NightSceneUtility.SpecialGuest";
    private const string GuestGroupControllerTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController";
    private const string SpecialGuestsControllerTypeName =
        "NightScene.GuestManagementUtility.SpecialGuestsController";
    private const string EvaluationDelegateTypeName =
        "NightScene.GuestManagementUtility.GuestGroupController+OverrideEvalResultDelegate";
    private const string Il2CppDelegateTypeName = "Il2CppSystem.Delegate";
    private const string Il2CppMethodInfoTypeName = "Il2CppSystem.Reflection.MethodInfo";
    private const string InvocationArrayTypeName =
        "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1";
    private const string ClosureTypeName =
        "GameData.Profile.DLC5_MizuchiChallengeBossData+__c__DisplayClass66_9";
    private const string ParentClosureTypeName =
        "GameData.Profile.DLC5_MizuchiChallengeBossData+__c__DisplayClass66_0";
    private const string ControlTypeName =
        "GameData.Profile.DLC5_MizuchiChallengeBossData+MizuchiControlType";
    private const string CallbackMethodName =
        "<MainChallengeLoop>g__GroupOverrideEvaluationCallback|74";

    public static MizuchiOrderIdentity Read(
        string challengeType,
        object? order,
        object? controller)
    {
        if (!MizuchiConstants.TryGetChallengeContract(challengeType, out var contract))
        {
            return Failed($"unsupported challenge type {challengeType}");
        }

        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.Resolved || resolution.Kind != RuntimeOrderKind.Special || resolution.ReadableOrder == null)
        {
            return Failed($"exact SpecialOrder is required: {resolution.Reason}");
        }

        var specialOrder = resolution.ReadableOrder;
        if (!TryReadExactProperty(
                specialOrder,
                "SpecialGuests",
                SpecialOrderTypeName,
                SpecialGuestTypeName,
                out var orderGuest,
                out var error)
            || !TryReadGuestId(orderGuest, "SpecialOrder.SpecialGuests", out var orderGuestId, out error))
        {
            return Failed(error);
        }

        if (controller == null)
        {
            return Failed("controller is null", orderGuestId: orderGuestId);
        }

        if (!TryReadExactProperty(
                controller,
                "OrderingGuest",
                GuestGroupControllerTypeName,
                GuestBaseTypeName,
                out var controllerGuest,
                out error)
            || !TryReadGuestId(controllerGuest, "GuestGroupController.OrderingGuest", out var controllerGuestId, out error))
        {
            return Failed(error, orderGuestId: orderGuestId);
        }

        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(specialOrder, out var orderPointer)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(controller, out var controllerPointer))
        {
            return Failed(
                "order/controller native pointer is unavailable",
                orderGuestId,
                controllerGuestId);
        }

        var specialController = RuntimeReflectionUtility.TryCastRuntimeObject(
            controller,
            SpecialGuestsControllerTypeName);
        if (specialController == null
            || !string.Equals(
                specialController.GetType().FullName,
                SpecialGuestsControllerTypeName,
                StringComparison.Ordinal))
        {
            return Failed(
                $"controller exact {SpecialGuestsControllerTypeName} cast failed from {controller.GetType().FullName}",
                orderGuestId,
                controllerGuestId,
                orderPointer: orderPointer,
                controllerPointer: controllerPointer);
        }

        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(specialController, out var specialControllerPointer)
            || specialControllerPointer != controllerPointer)
        {
            return Failed(
                $"controller cast native pointer mismatch: source=0x{(long)controllerPointer:X}, exact=0x{(long)specialControllerPointer:X}",
                orderGuestId,
                controllerGuestId,
                orderPointer: orderPointer,
                controllerPointer: controllerPointer);
        }

        if (!TryReadExactProperty(
                specialController,
                "SpecialGuest",
                SpecialGuestsControllerTypeName,
                SpecialGuestTypeName,
                out var controllerSpecialGuest,
                out error)
            || !TryReadGuestId(controllerSpecialGuest, "SpecialGuestsController.SpecialGuest", out var controllerSpecialGuestId, out error))
        {
            return Failed(error, orderGuestId: orderGuestId, controllerGuestId: controllerGuestId);
        }

        if (orderGuestId < 0
            || orderGuestId != controllerGuestId
            || orderGuestId != controllerSpecialGuestId)
        {
            return Failed(
                $"order/controller guest identity mismatch: order={orderGuestId}, ordering={controllerGuestId}, special={controllerSpecialGuestId}",
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId);
        }

        if (!TryReadExactProperty(
                specialController,
                "OverrideEvaluationCallback",
                GuestGroupControllerTypeName,
                EvaluationDelegateTypeName,
                out var callback,
                out error)
            || callback == null)
        {
            return Failed(
                error.Length == 0 ? "OverrideEvaluationCallback is null" : error,
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer);
        }

        if (!string.Equals(callback.GetType().FullName, EvaluationDelegateTypeName, StringComparison.Ordinal)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(callback, out var callbackPointer))
        {
            return Failed(
                $"OverrideEvaluationCallback exact delegate identity is invalid: {callback.GetType().FullName}",
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer);
        }

        if (!TryReadSingleInvocation(callback, out var invocation, out error)
            || invocation == null)
        {
            return Failed(
                error,
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer);
        }

        if (!TryReadExactProperty(
                invocation,
                "Method",
                Il2CppDelegateTypeName,
                Il2CppMethodInfoTypeName,
                out var callbackMethod,
                out error)
            || callbackMethod == null)
        {
            return Failed(
                error.Length == 0 ? "evaluation callback method is null" : error,
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                callbackMethod: "");
        }

        if (!TryReadExactProperty(
                callbackMethod,
                "Name",
                "Il2CppSystem.Reflection.MemberInfo",
                typeof(string).FullName,
                out var callbackMethodNameValue,
                out error)
            || callbackMethodNameValue is not string callbackMethodName
            || !string.Equals(callbackMethodName, CallbackMethodName, StringComparison.Ordinal))
        {
            var observed = callbackMethodNameValue?.ToString() ?? "";
            return Failed(
                error.Length == 0 ? $"evaluation callback method mismatch: {observed}" : error,
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                callbackMethod: observed);
        }

        if (!TryReadExactProperty(
                invocation,
                "Target",
                Il2CppDelegateTypeName,
                "Il2CppSystem.Object",
                out var rawClosure,
                out error)
            || rawClosure == null)
        {
            return Failed(
                error.Length == 0 ? "evaluation callback target is null" : error,
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                callbackMethod: callbackMethodName);
        }

        var closure = RuntimeReflectionUtility.TryCastRuntimeObject(rawClosure, ClosureTypeName);
        if (closure == null
            || !string.Equals(closure.GetType().FullName, ClosureTypeName, StringComparison.Ordinal)
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(closure, out var closurePointer))
        {
            return Failed(
                $"evaluation callback target is not exact {ClosureTypeName}",
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                callbackMethod: callbackMethodName);
        }

        if (!TryReadExactInt(closure, "selectedGuestGroup", ClosureTypeName, out var selectedGuestId, out error)
            || !TryReadExactProperty(
                closure,
                "group",
                ClosureTypeName,
                SpecialGuestsControllerTypeName,
                out var closureGroup,
                out error)
            || closureGroup == null)
        {
            return Failed(
                error,
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                callbackMethod: callbackMethodName);
        }

        if (!RuntimeReflectionUtility.TryReadNativeObjectPointer(closureGroup, out var closureGroupPointer)
            || closureGroupPointer != controllerPointer)
        {
            return Failed(
                $"closure group/controller pointer mismatch: group=0x{(long)closureGroupPointer:X}, controller=0x{(long)controllerPointer:X}",
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                callbackMethod: callbackMethodName);
        }

        if (!TryReadExactProperty(
                closureGroup,
                "SpecialGuest",
                SpecialGuestsControllerTypeName,
                SpecialGuestTypeName,
                out var groupGuest,
                out error)
            || !TryReadGuestId(groupGuest, "closure group.SpecialGuest", out var groupGuestId, out error))
        {
            return Failed(
                error,
                orderGuestId,
                controllerGuestId,
                controllerSpecialGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                callbackMethod: callbackMethodName);
        }

        if (selectedGuestId < 0
            || selectedGuestId != orderGuestId
            || groupGuestId != orderGuestId)
        {
            return Failed(
                $"closure guest identity mismatch: selected={selectedGuestId}, group={groupGuestId}, order={orderGuestId}",
                orderGuestId,
                controllerGuestId,
                groupGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                selectedGuestId,
                callbackMethod: callbackMethodName);
        }

        if (!TryReadExactProperty(
                closure,
                "field_Public___c__DisplayClass66_0_0",
                ClosureTypeName,
                ParentClosureTypeName,
                out var parentClosure,
                out error)
            || parentClosure == null
            || !RuntimeReflectionUtility.TryReadNativeObjectPointer(parentClosure, out var parentClosurePointer))
        {
            return Failed(
                error.Length == 0 ? "parent Mizuchi closure is unavailable" : error,
                orderGuestId,
                controllerGuestId,
                groupGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                selectedGuestId,
                callbackMethod: callbackMethodName);
        }

        if (!TryReadExactInt(parentClosure, "currentGuestWhoIsControlledByMizuchi", ParentClosureTypeName, out var controlledGuestId, out error)
            || !TryReadExactEnumInt(parentClosure, "typeOfMizuchi", ParentClosureTypeName, ControlTypeName, out var controlType, out error)
            || !TryReadExactInt(parentClosure, "targetIngredientId", ParentClosureTypeName, out var targetIngredientId, out error)
            || !TryReadExactBool(parentClosure, "isMizuchiChallenge", ParentClosureTypeName, out var isMizuchiChallenge, out error)
            || !TryReadExactInt(parentClosure, "catchMizuchiNum", ParentClosureTypeName, out var catchCount, out error)
            || !TryReadExactInt(parentClosure, "needCatchMizuchiTime", ParentClosureTypeName, out var requiredCatchCount, out error))
        {
            return Failed(
                error,
                orderGuestId,
                controllerGuestId,
                groupGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                selectedGuestId,
                parentClosurePointer,
                callbackMethod: callbackMethodName);
        }

        if (controlledGuestId < MizuchiConstants.NoControlledGuestId)
        {
            return Failed(
                $"controlled guest ID is outside the exact domain: {controlledGuestId}",
                orderGuestId,
                controllerGuestId,
                groupGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                selectedGuestId,
                parentClosurePointer,
                controlledGuestId,
                controlType,
                targetIngredientId,
                isMizuchiChallenge,
                catchCount,
                requiredCatchCount,
                callbackMethodName);
        }

        var hasControlledGuest = controlledGuestId != MizuchiConstants.NoControlledGuestId;
        var activeControlMatches = contract.ExpectedControlType.HasValue
            ? controlType == contract.ExpectedControlType.Value
            : MizuchiConstants.IsActiveControlType(controlType);
        var controlStateMatches = hasControlledGuest
            ? activeControlMatches
            : controlType == MizuchiConstants.NoControlType;
        if (!controlStateMatches
            || targetIngredientId != contract.TargetIngredientId
            || isMizuchiChallenge != contract.IsBaseChallenge
            || catchCount < 0
            || requiredCatchCount <= 0
            || catchCount > requiredCatchCount)
        {
            return Failed(
                $"Mizuchi closure invariants mismatch: challenge={challengeType}, controlled={controlledGuestId}, control={controlType}/{contract.ExpectedControlType?.ToString() ?? "0..2"}, ingredient={targetIngredientId}/{contract.TargetIngredientId}, baseChallenge={isMizuchiChallenge}/{contract.IsBaseChallenge}, catches={catchCount}/{requiredCatchCount}",
                orderGuestId,
                controllerGuestId,
                groupGuestId,
                orderPointer,
                controllerPointer,
                callbackPointer,
                closurePointer,
                selectedGuestId,
                parentClosurePointer,
                controlledGuestId,
                controlType,
                targetIngredientId,
                isMizuchiChallenge,
                catchCount,
                requiredCatchCount,
                callbackMethodName);
        }

        var isPossessed = hasControlledGuest && controlledGuestId == orderGuestId;
        return new MizuchiOrderIdentity(
            Verified: true,
            IsPossessed: isPossessed,
            OrderGuestId: orderGuestId,
            ControllerGuestId: controllerGuestId,
            GroupGuestId: groupGuestId,
            SelectedGuestId: selectedGuestId,
            ControlledGuestId: controlledGuestId,
            ControlType: controlType,
            TargetIngredientId: targetIngredientId,
            IsMizuchiChallenge: isMizuchiChallenge,
            CatchCount: catchCount,
            RequiredCatchCount: requiredCatchCount,
            OrderPointer: orderPointer,
            ControllerPointer: controllerPointer,
            CallbackPointer: callbackPointer,
            ClosurePointer: closurePointer,
            ParentClosurePointer: parentClosurePointer,
            CallbackMethod: callbackMethodName,
            Reason: isPossessed
                ? "exact Mizuchi closure identifies the order guest as possessed"
                : hasControlledGuest
                    ? "exact Mizuchi closure identifies the order guest as ordinary"
                    : "exact Mizuchi no-target state identifies the order guest as ordinary");
    }

    private static bool TryReadSingleInvocation(
        object callback,
        out object? invocation,
        out string error)
    {
        invocation = null;
        error = "";
        try
        {
            var method = callback.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(candidate =>
                    candidate.Name == "GetInvocationList"
                    && candidate.GetParameters().Length == 0
                    && candidate.ReturnType.IsGenericType
                    && string.Equals(
                        candidate.ReturnType.GetGenericTypeDefinition().FullName,
                        InvocationArrayTypeName,
                        StringComparison.Ordinal)
                    && candidate.ReturnType.GetGenericArguments().Length == 1
                    && string.Equals(
                        candidate.ReturnType.GetGenericArguments()[0].FullName,
                        Il2CppDelegateTypeName,
                        StringComparison.Ordinal));
            if (method == null)
            {
                error = $"{EvaluationDelegateTypeName}.GetInvocationList exact return shape not found";
                return false;
            }

            var list = method.Invoke(callback, Array.Empty<object?>());
            if (list == null)
            {
                error = "evaluation callback invocation list is null";
                return false;
            }

            var lengthProperty = list.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(property =>
                    property.Name == "Length"
                    && property.PropertyType == typeof(int)
                    && property.GetIndexParameters().Length == 0);
            var indexer = list.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(property =>
                {
                    var indexes = property.GetIndexParameters();
                    return indexes.Length == 1
                        && indexes[0].ParameterType == typeof(int)
                        && string.Equals(property.PropertyType.FullName, Il2CppDelegateTypeName, StringComparison.Ordinal);
                });
            if (lengthProperty == null || indexer == null)
            {
                error = "evaluation callback invocation list exact Length/indexer shape not found";
                return false;
            }

            var rawLength = lengthProperty.GetValue(list);
            if (rawLength is not int length || length != 1)
            {
                error = $"evaluation callback invocation count must be exactly 1, actual={rawLength?.ToString() ?? "null"}";
                return false;
            }

            invocation = indexer.GetValue(list, new object[] { 0 });
            if (invocation == null)
            {
                error = "evaluation callback single invocation entry is null";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"evaluation callback invocation list read failed: {DescribeException(ex)}";
            return false;
        }
    }

    private static bool TryReadGuestId(
        object? guest,
        string source,
        out int guestId,
        out string error)
    {
        guestId = -1;
        if (guest == null)
        {
            error = $"{source} is null";
            return false;
        }

        return TryReadExactInt(guest, "Id", GuestBaseTypeName, out guestId, out error);
    }

    private static bool TryReadExactInt(
        object source,
        string propertyName,
        string declaringTypeName,
        out int value,
        out string error)
    {
        value = 0;
        if (!TryReadExactProperty(
                source,
                propertyName,
                declaringTypeName,
                typeof(int).FullName,
                out var raw,
                out error)
            || raw is not int parsed)
        {
            if (error.Length == 0) error = $"{declaringTypeName}.{propertyName} did not return System.Int32";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadExactBool(
        object source,
        string propertyName,
        string declaringTypeName,
        out bool value,
        out string error)
    {
        value = false;
        if (!TryReadExactProperty(
                source,
                propertyName,
                declaringTypeName,
                typeof(bool).FullName,
                out var raw,
                out error)
            || raw is not bool parsed)
        {
            if (error.Length == 0) error = $"{declaringTypeName}.{propertyName} did not return System.Boolean";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadExactEnumInt(
        object source,
        string propertyName,
        string declaringTypeName,
        string enumTypeName,
        out int value,
        out string error)
    {
        value = -1;
        if (!TryReadExactProperty(
                source,
                propertyName,
                declaringTypeName,
                enumTypeName,
                out var raw,
                out error)
            || raw is not Enum enumValue)
        {
            if (error.Length == 0) error = $"{declaringTypeName}.{propertyName} did not return exact enum {enumTypeName}";
            return false;
        }

        try
        {
            value = Convert.ToInt32(enumValue, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{declaringTypeName}.{propertyName} enum conversion failed: {DescribeException(ex)}";
            return false;
        }
    }

    private static bool TryReadExactProperty(
        object source,
        string propertyName,
        string declaringTypeName,
        string? expectedPropertyTypeName,
        out object? value,
        out string error)
    {
        value = null;
        error = "";
        try
        {
            var matches = source.GetType()
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property =>
                    property.Name == propertyName
                    && property.GetIndexParameters().Length == 0
                    && property.GetMethod?.IsStatic == false
                    && string.Equals(property.DeclaringType?.FullName, declaringTypeName, StringComparison.Ordinal)
                    && (expectedPropertyTypeName == null
                        || string.Equals(property.PropertyType.FullName, expectedPropertyTypeName, StringComparison.Ordinal)))
                .ToArray();
            if (matches.Length != 1)
            {
                error = $"{declaringTypeName}.{propertyName} exact property count is {matches.Length}";
                return false;
            }

            value = matches[0].GetValue(source);
            return true;
        }
        catch (Exception ex)
        {
            error = $"{declaringTypeName}.{propertyName} read failed: {DescribeException(ex)}";
            return false;
        }
    }

    private static string DescribeException(Exception ex)
    {
        var root = ex.GetBaseException();
        return $"{root.GetType().Name}: {root.Message}";
    }

    private static MizuchiOrderIdentity Failed(
        string reason,
        int? orderGuestId = null,
        int? controllerGuestId = null,
        int? groupGuestId = null,
        nint orderPointer = 0,
        nint controllerPointer = 0,
        nint callbackPointer = 0,
        nint closurePointer = 0,
        int? selectedGuestId = null,
        nint parentClosurePointer = 0,
        int? controlledGuestId = null,
        int? controlType = null,
        int? targetIngredientId = null,
        bool? isMizuchiChallenge = null,
        int? catchCount = null,
        int? requiredCatchCount = null,
        string callbackMethod = "")
    {
        return new MizuchiOrderIdentity(
            Verified: false,
            IsPossessed: false,
            OrderGuestId: orderGuestId,
            ControllerGuestId: controllerGuestId,
            GroupGuestId: groupGuestId,
            SelectedGuestId: selectedGuestId,
            ControlledGuestId: controlledGuestId,
            ControlType: controlType,
            TargetIngredientId: targetIngredientId,
            IsMizuchiChallenge: isMizuchiChallenge,
            CatchCount: catchCount,
            RequiredCatchCount: requiredCatchCount,
            OrderPointer: orderPointer,
            ControllerPointer: controllerPointer,
            CallbackPointer: callbackPointer,
            ClosurePointer: closurePointer,
            ParentClosurePointer: parentClosurePointer,
            CallbackMethod: callbackMethod,
            Reason: reason);
    }
}
