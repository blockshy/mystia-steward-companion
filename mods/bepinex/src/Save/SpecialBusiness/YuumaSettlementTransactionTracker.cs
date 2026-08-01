namespace MystiaStewardCompanion.Save.SpecialBusiness;

internal enum YuumaSettlementTransactionStage
{
    Ready,
    FoodCommitAttempting,
    FoodCommitted,
    CleanupCommitted,
    EvaluationAttempting,
    EvaluationCommitted,
    BookkeepingAttempting,
    Completed,
    Uncertain,
}

/// <summary>
/// Tracks the irreversible steps of one Blood Pond Hell order settlement.
/// </summary>
internal sealed class YuumaSettlementTransactionTracker
{
    public YuumaSettlementTransactionStage Stage { get; private set; } =
        YuumaSettlementTransactionStage.Ready;

    public bool TryBeginFoodCommit()
    {
        return Advance(
            YuumaSettlementTransactionStage.Ready,
            YuumaSettlementTransactionStage.FoodCommitAttempting);
    }

    public bool MarkFoodCommitted()
    {
        return Advance(
            YuumaSettlementTransactionStage.FoodCommitAttempting,
            YuumaSettlementTransactionStage.FoodCommitted);
    }

    public bool MarkCleanupCommitted()
    {
        return Advance(
            YuumaSettlementTransactionStage.FoodCommitted,
            YuumaSettlementTransactionStage.CleanupCommitted);
    }

    public bool TryBeginEvaluation()
    {
        return Advance(
            YuumaSettlementTransactionStage.CleanupCommitted,
            YuumaSettlementTransactionStage.EvaluationAttempting);
    }

    public bool MarkEvaluationCommitted()
    {
        return Advance(
            YuumaSettlementTransactionStage.EvaluationAttempting,
            YuumaSettlementTransactionStage.EvaluationCommitted);
    }

    public bool TryBeginBookkeeping()
    {
        return Advance(
            YuumaSettlementTransactionStage.EvaluationCommitted,
            YuumaSettlementTransactionStage.BookkeepingAttempting);
    }

    public bool MarkBookkeepingCommitted()
    {
        return Advance(
            YuumaSettlementTransactionStage.BookkeepingAttempting,
            YuumaSettlementTransactionStage.Completed);
    }

    public bool MarkUncertain()
    {
        if (Stage is YuumaSettlementTransactionStage.Ready
            or YuumaSettlementTransactionStage.Completed
            or YuumaSettlementTransactionStage.Uncertain)
        {
            return false;
        }

        Stage = YuumaSettlementTransactionStage.Uncertain;
        return true;
    }

    private bool Advance(
        YuumaSettlementTransactionStage expected,
        YuumaSettlementTransactionStage next)
    {
        if (Stage != expected) return false;
        Stage = next;
        return true;
    }
}
