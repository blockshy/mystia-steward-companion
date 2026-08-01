using System.Reflection;

namespace MystiaStewardCompanion.Save;

internal sealed class YuumaChallengeOrderModule : ISpecialBusinessOrderModule
{
    public bool MatchesChallenge(string challengeType)
    {
        return string.Equals(challengeType, SpecialBusinessChallengeTypes.BloodPondHell, StringComparison.Ordinal);
    }

    public SpecialBusinessOrderClassification Classify(
        string challengeType,
        object? order,
        object? controller,
        string source)
    {
        var identity = YuumaChallengeOrderIdentity.Read(order, controller);
        if (!identity.Verified)
        {
            var blocked = SpecialBusinessModuleRegistry.Blocked(
                SpecialBusinessOrderRoles.YuumaUnverified,
                "血池地狱订单",
                $"血池地狱订单身份无法精确确认，已阻止自动化接管：{identity.Reason}");
            SpecialBusinessDiagnostics.AppendYuumaOrderClassification(
                challengeType,
                blocked,
                identity,
                order,
                controller,
                source);
            return blocked;
        }

        if (identity.OrderGuestId != SpecialBusinessGuestIds.YuumaBoss)
        {
            return SpecialBusinessOrderClassification.Standard;
        }

        var classification = SpecialBusinessModuleRegistry.AllowedSpecialOrder(
            SpecialBusinessOrderRoles.YuumaBoss,
            "血池地狱 BOSS 订单",
            identity.OrderGuestId.Value);
        SpecialBusinessDiagnostics.AppendYuumaOrderClassification(
            challengeType,
            classification,
            identity,
            order,
            controller,
            source);
        return classification;
    }
}

internal sealed record YuumaChallengeOrderIdentity(
    bool Verified,
    string OrderKind,
    int? OrderGuestId,
    int? ControllerGuestId,
    string Reason)
{
    public static YuumaChallengeOrderIdentity Read(object? order, object? controller)
    {
        var resolution = RuntimeOrderTypeResolver.Resolve(order);
        if (!resolution.Resolved || resolution.ReadableOrder == null)
        {
            return new YuumaChallengeOrderIdentity(false, "", null, null, resolution.Reason);
        }

        var orderKind = resolution.KindName;
        var guestProperty = resolution.Kind == RuntimeOrderKind.Normal ? "Guest" : "SpecialGuests";
        if (!TryReadDeclaredGuestId(resolution.ReadableOrder, guestProperty, out var orderGuestId, out var orderError))
        {
            return new YuumaChallengeOrderIdentity(false, orderKind, null, null, orderError);
        }

        if (controller == null)
        {
            return orderGuestId == SpecialBusinessGuestIds.YuumaBoss
                ? new YuumaChallengeOrderIdentity(
                    false,
                    orderKind,
                    orderGuestId,
                    null,
                    "boss order controller is unavailable")
                : new YuumaChallengeOrderIdentity(
                    true,
                    orderKind,
                    orderGuestId,
                    null,
                    "verified non-boss order; controller unavailable");
        }

        if (!TryReadControllerGuestId(controller, out var controllerGuestId, out var controllerError))
        {
            return orderGuestId == SpecialBusinessGuestIds.YuumaBoss
                ? new YuumaChallengeOrderIdentity(false, orderKind, orderGuestId, null, controllerError)
                : new YuumaChallengeOrderIdentity(true, orderKind, orderGuestId, null, controllerError);
        }

        if (controllerGuestId != orderGuestId)
        {
            return new YuumaChallengeOrderIdentity(
                false,
                orderKind,
                orderGuestId,
                controllerGuestId,
                $"order guest ID {orderGuestId} does not match controller OrderingGuest ID {controllerGuestId}");
        }

        return new YuumaChallengeOrderIdentity(
            true,
            orderKind,
            orderGuestId,
            controllerGuestId,
            "order and controller identities match");
    }

    private static bool TryReadDeclaredGuestId(
        object order,
        string propertyName,
        out int guestId,
        out string error)
    {
        guestId = -1;
        error = "";
        try
        {
            var property = order.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (property == null || property.GetIndexParameters().Length != 0)
            {
                error = $"{order.GetType().FullName}.{propertyName} exact property not found";
                return false;
            }

            return TryReadGuestId(property.GetValue(order), $"{order.GetType().FullName}.{propertyName}", out guestId, out error);
        }
        catch (Exception ex)
        {
            error = $"{order.GetType().FullName}.{propertyName} read failed: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
            return false;
        }
    }

    private static bool TryReadControllerGuestId(
        object? controller,
        out int guestId,
        out string error)
    {
        guestId = -1;
        error = "";
        if (controller == null)
        {
            error = "controller is null";
            return false;
        }

        try
        {
            var property = controller.GetType().GetProperty(
                "OrderingGuest",
                BindingFlags.Public | BindingFlags.Instance);
            if (property == null || property.GetIndexParameters().Length != 0)
            {
                error = $"{controller.GetType().FullName}.OrderingGuest exact property not found";
                return false;
            }

            return TryReadGuestId(
                property.GetValue(controller),
                $"{controller.GetType().FullName}.OrderingGuest",
                out guestId,
                out error);
        }
        catch (Exception ex)
        {
            error = $"{controller.GetType().FullName}.OrderingGuest read failed: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
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
        error = "";
        if (guest == null)
        {
            error = $"{source} is null";
            return false;
        }

        try
        {
            var idProperty = guest.GetType().GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);
            if (idProperty == null
                || idProperty.PropertyType != typeof(int)
                || !string.Equals(
                    idProperty.DeclaringType?.FullName,
                    "GameData.Core.Collections.NightSceneUtility.GuestBase",
                    StringComparison.Ordinal)
                || idProperty.GetIndexParameters().Length != 0)
            {
                error = $"{source}.Id exact int property not found";
                return false;
            }

            var value = idProperty.GetValue(guest);
            if (value is not int id)
            {
                error = $"{source}.Id did not return System.Int32";
                return false;
            }

            guestId = id;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{source}.Id read failed: {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
            return false;
        }
    }
}
