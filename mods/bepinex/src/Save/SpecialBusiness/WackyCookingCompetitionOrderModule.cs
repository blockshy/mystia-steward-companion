namespace MystiaStewardCompanion.Save;

internal sealed class WackyCookingCompetitionOrderModule : ISpecialBusinessOrderModule
{
    private const int KoishiBossGuestId = 2006;

    public bool MatchesChallenge(string challengeType)
    {
        return string.Equals(challengeType, SpecialBusinessChallengeTypes.WackyCookingCompetition, StringComparison.Ordinal);
    }

    public SpecialBusinessOrderClassification Classify(
        string challengeType,
        object? order,
        object? controller,
        string source)
    {
        var guest = SpecialBusinessOrderProbe.Read(order, controller);
        SpecialBusinessOrderClassification classification;
        var reason = "";
        if (SpecialBusinessOrderProbe.HasControllerSpawnType(controller, "GhostInChallenge"))
        {
            classification = SpecialBusinessModuleRegistry.AllowedSpecialOrder(
                SpecialBusinessOrderRoles.WackyGhost,
                "怪诞料理大赛 · 分身订单");
            reason = "controller GuestControllerSpawnType matched GhostInChallenge";
        }
        else if (IsPhase3ConfirmedKoishiBossOrder(guest, order, controller, source))
        {
            classification = SpecialBusinessModuleRegistry.AllowedSpecialOrder(
                SpecialBusinessOrderRoles.WackyKoishiBoss,
                "怪诞料理大赛 · 古明地恋订单");
            reason = "phase3 order has explicit Koishi boss evidence";
        }
        else if (order != null || controller != null)
        {
            classification = SpecialBusinessModuleRegistry.AllowedSpecialOrder(
                SpecialBusinessOrderRoles.WackyTarget,
                "怪诞料理大赛订单");
            reason = "active wacky challenge with readable order/controller";
        }
        else
        {
            return SpecialBusinessOrderClassification.Standard;
        }

        SpecialBusinessDiagnostics.AppendWackyOrderClassification(
            SpecialBusinessChallengeTypes.WackyCookingCompetition,
            classification.Role,
            classification.RoleLabel,
            guest,
            order,
            controller,
            source,
            reason);
        return classification;
    }

    private static bool IsPhase3ConfirmedKoishiBossOrder(
        SpecialBusinessOrderProbe guest,
        object? order,
        object? controller,
        string source)
    {
        if (!RuntimeSpecialBusinessContextService.IsActiveWackyPhase("Phase3")) return false;
        if (!guest.IsGuest(KoishiBossGuestId, "Koishi", "古明地恋")) return false;
        if (SpecialBusinessOrderProbe.HasControllerSpawnType(controller, "GhostInChallenge")) return false;

        if (ReadBoolMember(order, "ManualOrder", "manualOrder")) return true;
        if (IsManualOrderSource(source)) return true;
        if (LooksLikeKoishiSpecialOrder(order)) return true;

        return controller != null
            && SpecialBusinessOrderProbe.ReadControllerBool(controller, "IsControlled", "isControlled")
            && SpecialBusinessOrderProbe.ReadControllerBool(controller, "IsHerself", "isHerself");
    }

    private static bool LooksLikeKoishiSpecialOrder(object? order)
    {
        if (order == null) return false;

        var typeName = order.GetType().FullName ?? order.GetType().Name;
        return typeName.IndexOf("GuestsManager+SpecialOrder", StringComparison.OrdinalIgnoreCase) >= 0
            || typeName.EndsWith(".SpecialOrder", StringComparison.OrdinalIgnoreCase)
            || typeName.EndsWith("+SpecialOrder", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManualOrderSource(string source)
    {
        return source.IndexOf("ManualOrder", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("ManualController", StringComparison.OrdinalIgnoreCase) >= 0
            || source.IndexOf("ManualDesk", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool ReadBoolMember(object? value, params string[] members)
    {
        foreach (var member in members)
        {
            var raw = ReadFirstMember(value, member);
            if (raw is bool boolean) return boolean;

            var text = raw?.ToString()?.Trim();
            if (bool.TryParse(text, out var parsed)) return parsed;
        }

        return false;
    }

    private static object? ReadFirstMember(object? value, params string[] members)
    {
        foreach (var member in members)
        {
            var result = RuntimeReflectionUtility.GetMemberValue(value, member)
                ?? RuntimeReflectionUtility.InvokeMethod(value, $"get_{member}");
            if (result != null) return result;
        }

        return null;
    }
}
