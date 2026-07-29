using MystiaStewardCompanion.Core;

namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeMissionRecipePriorityMissionBoundary(
    long Generation,
    bool RuntimeAvailable,
    bool Ready);

internal static class RuntimeMissionRecipePriorityProjection
{
    private const string ActivePhase = "Active";

    public static NightBusinessContext? Enrich(
        NightBusinessContext? context,
        RuntimeDataCatalog catalog,
        NightBusinessLifecycleSnapshot business,
        RuntimeMissionRecipePriorityMissionBoundary mission,
        RuntimeServeInWorkMissionDiagnosticSnapshot serveInWork,
        SpecialBusinessContext? specialBusiness)
    {
        if (context == null)
        {
            return null;
        }

        if (!CanProject(
                catalog,
                business,
                mission,
                serveInWork,
                specialBusiness))
        {
            return WithoutPriorities(context);
        }

        var prioritiesByOrderIndex = new Dictionary<int, MissionRecipePriority>();
        var conflictedOrderIndexes = new HashSet<int>();
        foreach (var signal in serveInWork.Signals)
        {
            if (!TryBuildPriority(
                    context.Orders,
                    catalog.Recipes,
                    business.Generation,
                    mission.Generation,
                    signal,
                    out var orderIndex,
                    out var priority))
            {
                continue;
            }

            if (conflictedOrderIndexes.Contains(orderIndex))
            {
                continue;
            }

            if (prioritiesByOrderIndex.TryGetValue(orderIndex, out var existing))
            {
                if (!PriorityEquals(existing, priority))
                {
                    prioritiesByOrderIndex.Remove(orderIndex);
                    conflictedOrderIndexes.Add(orderIndex);
                }
                continue;
            }

            prioritiesByOrderIndex[orderIndex] = priority;
        }

        return BuildContext(context, prioritiesByOrderIndex);
    }

    private static bool CanProject(
        RuntimeDataCatalog catalog,
        NightBusinessLifecycleSnapshot business,
        RuntimeMissionRecipePriorityMissionBoundary mission,
        RuntimeServeInWorkMissionDiagnosticSnapshot serveInWork,
        SpecialBusinessContext? specialBusiness)
    {
        return catalog.IsComplete
            && business.IsActive
            && business.Generation > 0
            && mission.Ready
            && mission.RuntimeAvailable
            && mission.Generation > 0
            && serveInWork.HookAttached
            && serveInWork.MissionGeneration == mission.Generation
            && serveInWork.BusinessGeneration == business.Generation
            && string.Equals(serveInWork.NightPhase, ActivePhase, StringComparison.Ordinal)
            && specialBusiness is
            {
                Active: false,
                Error: null,
            }
            && string.Equals(
                specialBusiness.ChallengeType,
                SpecialBusinessChallengeTypes.NotChallenge,
                StringComparison.Ordinal);
    }

    private static bool TryBuildPriority(
        IReadOnlyList<NightBusinessOrder> orders,
        IReadOnlyList<Recipe> recipes,
        long businessGeneration,
        long missionGeneration,
        RuntimeServeInWorkMissionSignal signal,
        out int orderIndex,
        out MissionRecipePriority priority)
    {
        orderIndex = -1;
        priority = null!;
        if (signal.MissionGeneration != missionGeneration
            || signal.BusinessGeneration != businessGeneration
            || signal.CanonicalGuestId < 0
            || signal.FoodId < 0)
        {
            return false;
        }

        var matchingOrderIndex = -1;
        for (var index = 0; index < orders.Count; index++)
        {
            var order = orders[index];
            if (order.GuestId != signal.CanonicalGuestId
                || order.RuntimeGuestId != signal.RawGuestId
                || order.DeskCode < 0
                || order.HasServedFood
                || string.IsNullOrWhiteSpace(order.TraceId))
            {
                continue;
            }

            if (matchingOrderIndex >= 0)
            {
                return false;
            }

            matchingOrderIndex = index;
        }

        if (matchingOrderIndex < 0)
        {
            return false;
        }

        Recipe? matchedRecipe = null;
        foreach (var recipe in recipes)
        {
            if (recipe.Id != signal.FoodId)
            {
                continue;
            }

            if (matchedRecipe != null)
            {
                return false;
            }

            matchedRecipe = recipe;
        }

        if (matchedRecipe == null || matchedRecipe.RecipeId < 0)
        {
            return false;
        }

        var matchedOrder = orders[matchingOrderIndex];
        orderIndex = matchingOrderIndex;
        priority = new MissionRecipePriority
        {
            TraceId = matchedOrder.TraceId,
            DeskCode = matchedOrder.DeskCode,
            GuestId = signal.CanonicalGuestId,
            RuntimeGuestId = signal.RawGuestId,
            FoodId = signal.FoodId,
            RecipeId = matchedRecipe.RecipeId,
            MissionGeneration = missionGeneration,
            BusinessGeneration = businessGeneration,
        };
        return true;
    }

    private static NightBusinessContext BuildContext(
        NightBusinessContext context,
        IReadOnlyDictionary<int, MissionRecipePriority> prioritiesByOrderIndex)
    {
        var changed = false;
        var orders = new List<NightBusinessOrder>(context.Orders.Count);
        for (var index = 0; index < context.Orders.Count; index++)
        {
            var order = context.Orders[index];
            prioritiesByOrderIndex.TryGetValue(index, out var priority);
            if (PriorityEquals(order.MissionRecipePriority, priority))
            {
                orders.Add(order);
                continue;
            }

            changed = true;
            orders.Add(CopyOrder(order, priority));
        }

        if (!changed)
        {
            return context;
        }

        return new NightBusinessContext
        {
            Place = context.Place,
            PlaceLabel = context.PlaceLabel,
            ActiveRareGuests = context.ActiveRareGuests.ToList(),
            Orders = orders,
            Source = context.Source,
            Error = context.Error,
        };
    }

    private static NightBusinessContext WithoutPriorities(NightBusinessContext context)
    {
        return context.Orders.Any(order => order.MissionRecipePriority != null)
            ? BuildContext(
                context,
                new Dictionary<int, MissionRecipePriority>())
            : context;
    }

    private static NightBusinessOrder CopyOrder(
        NightBusinessOrder order,
        MissionRecipePriority? priority)
    {
        return new NightBusinessOrder
        {
            TraceId = order.TraceId,
            DeskCode = order.DeskCode,
            GuestId = order.GuestId,
            RuntimeGuestId = order.RuntimeGuestId,
            GuestName = order.GuestName,
            SpecialBusinessRole = order.SpecialBusinessRole,
            SpecialBusinessRoleLabel = order.SpecialBusinessRoleLabel,
            AutomationAllowed = order.AutomationAllowed,
            AutomationBlockReason = order.AutomationBlockReason,
            FoodTagId = order.FoodTagId,
            FoodTag = order.FoodTag,
            BeverageTagId = order.BeverageTagId,
            BeverageTag = order.BeverageTag,
            Source = order.Source,
            FirstSeenAtUtc = order.FirstSeenAtUtc,
            LastSeenAtUtc = order.LastSeenAtUtc,
            IsFreeOrder = order.IsFreeOrder,
            Fund = order.Fund,
            BaseFundCarry = order.BaseFundCarry,
            MaxFundCarry = order.MaxFundCarry,
            ExtraFundByBuff = order.ExtraFundByBuff,
            WillPayMoney = order.WillPayMoney,
            RemainingOrderCount = order.RemainingOrderCount,
            HasServedFood = order.HasServedFood,
            HasServedBeverage = order.HasServedBeverage,
            MissionRecipePriority = priority,
        };
    }

    private static bool PriorityEquals(
        MissionRecipePriority? left,
        MissionRecipePriority? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        return left != null
            && right != null
            && string.Equals(left.TraceId, right.TraceId, StringComparison.Ordinal)
            && left.DeskCode == right.DeskCode
            && left.GuestId == right.GuestId
            && left.RuntimeGuestId == right.RuntimeGuestId
            && left.FoodId == right.FoodId
            && left.RecipeId == right.RecipeId
            && left.MissionGeneration == right.MissionGeneration
            && left.BusinessGeneration == right.BusinessGeneration;
    }
}
