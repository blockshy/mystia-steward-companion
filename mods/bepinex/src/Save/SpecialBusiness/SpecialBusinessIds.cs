namespace MystiaStewardCompanion.Save;

internal static class SpecialBusinessChallengeTypes
{
    public const string NotChallenge = "NotChallenge";
    public const string WackyCookingCompetition = "Story_WackyCookingCompetition";
    public const string StoryYuyuko = "Story_Yuyuko";
    public const string RetakeYuyuko = "Challenge_Yuyuko";
    public const string BloodPondHell = "Story_BloodPondHell";
    public const string StoryMizuchi = "Story_Mizuchi";
    public const string MizuchiTrial1 = "Story_Mizuchi_1";
    public const string MizuchiTrial2 = "Story_Mizuchi_2";
    public const string MizuchiTrial3 = "Story_Mizuchi_3";

    public static bool IsMizuchiTrial(string challengeType)
    {
        return string.Equals(challengeType, MizuchiTrial1, StringComparison.Ordinal)
            || string.Equals(challengeType, MizuchiTrial2, StringComparison.Ordinal)
            || string.Equals(challengeType, MizuchiTrial3, StringComparison.Ordinal);
    }

    public static bool IsMizuchiChallenge(string challengeType)
    {
        return string.Equals(challengeType, StoryMizuchi, StringComparison.Ordinal)
            || IsMizuchiTrial(challengeType);
    }
}

internal static class SpecialBusinessOrderRoles
{
    public const string ContextUnavailable = "special-business-context-unavailable";
    public const string YuumaBoss = "yuuma-boss-order";
    public const string YuumaUnverified = "yuuma-order-unverified";
    public const string WackyGhost = "wacky-ghost-order";
    public const string WackyKoishiBoss = "wacky-koishi-boss";
    public const string WackyTarget = "wacky-target-order";
    public const string YuyukoBoss = "yuyuko-boss-order";
    public const string MizuchiStoryPossessed = "mizuchi-story-possessed-order";
    public const string MizuchiStoryOrdinary = "mizuchi-story-ordinary-order";
    public const string MizuchiStoryUnverified = "mizuchi-story-unverified-order";
    public const string MizuchiTrialPossessed = "mizuchi-trial-possessed-order";
    public const string MizuchiTrialOrdinary = "mizuchi-trial-ordinary-order";
    public const string MizuchiTrialUnverified = "mizuchi-trial-unverified-order";
}

internal static class SpecialBusinessGuestIds
{
    public const int YuumaBoss = 1003;
}

internal readonly record struct MizuchiChallengeContract(
    int TargetIngredientId,
    bool IsBaseChallenge,
    int? ExpectedControlType);

internal static class MizuchiConstants
{
    public const int PuyoyoFruitIngredientId = 5002;
    public const int PepperWaterIngredientId = 5005;
    public const int NoControlledGuestId = -1;
    public const int WrongBeverageTagControl = 0;
    public const int WrongFoodOrderControl = 1;
    public const int WrongTalkingDialogControl = 2;
    public const int NoControlType = 3;

    public static bool TryGetChallengeContract(
        string challengeType,
        out MizuchiChallengeContract contract)
    {
        contract = challengeType switch
        {
            SpecialBusinessChallengeTypes.StoryMizuchi => new(
                PuyoyoFruitIngredientId,
                IsBaseChallenge: true,
                ExpectedControlType: null),
            SpecialBusinessChallengeTypes.MizuchiTrial1 => new(
                PepperWaterIngredientId,
                IsBaseChallenge: false,
                ExpectedControlType: WrongFoodOrderControl),
            SpecialBusinessChallengeTypes.MizuchiTrial2 => new(
                PepperWaterIngredientId,
                IsBaseChallenge: false,
                ExpectedControlType: WrongBeverageTagControl),
            SpecialBusinessChallengeTypes.MizuchiTrial3 => new(
                PepperWaterIngredientId,
                IsBaseChallenge: false,
                ExpectedControlType: WrongTalkingDialogControl),
            _ => default,
        };
        return contract.TargetIngredientId > 0;
    }

    public static bool IsActiveControlType(int controlType)
    {
        return controlType is WrongBeverageTagControl
            or WrongFoodOrderControl
            or WrongTalkingDialogControl;
    }
}
