namespace MystiaStewardCompanion.Save;

internal sealed record RuntimeScheduledEventKizunaEvidence(
    bool CharacterIdentityResolved,
    int? RuntimeGuestId,
    int? CanonicalCharacterId,
    bool? CharacterIsSpecial,
    bool? RecordedSpecialNpc,
    int? CurrentBondLevel,
    int? CurrentBondExp,
    bool? Level5Gate);

internal sealed record RuntimeScheduledEventEligibilityDiagnostic(
    string Disposition,
    string Reason,
    bool? CharacterIdentityResolved,
    int? RuntimeGuestId,
    int? CanonicalCharacterId,
    bool? CharacterIsSpecial,
    bool? RecordedSpecialNpc,
    int? CurrentBondLevel,
    int? CurrentBondExp,
    int? RequiredBondExp,
    bool? Level5Gate);

internal static class RuntimeScheduledEventEligibility
{
    public const int OnTalkWithCharacterTrigger = 3;
    public const int KizunaCheckPointTrigger = 5;

    public static RuntimeScheduledEventEligibilityDiagnostic Evaluate(
        int triggerType,
        string? triggerId,
        bool eventFinished,
        RuntimeScheduledEventKizunaEvidence? kizunaEvidence)
    {
        if (eventFinished)
        {
            RequireNoEvidence(kizunaEvidence, "finished-event");
            return Result("excluded", "event-finished");
        }

        if (triggerType == OnTalkWithCharacterTrigger)
        {
            RequireNoEvidence(kizunaEvidence, "on-talk");
            return string.IsNullOrEmpty(triggerId)
                ? Result("ineligible", "trigger-id-missing")
                : Result("eligible", "on-talk-with-character");
        }

        if (triggerType != KizunaCheckPointTrigger)
        {
            RequireNoEvidence(kizunaEvidence, "non-character-interact");
            return Result("not-applicable", "trigger-not-character-interact");
        }

        if (string.IsNullOrEmpty(triggerId))
        {
            RequireNoEvidence(kizunaEvidence, "missing-kizuna-trigger-id");
            return Result("ineligible", "trigger-id-missing");
        }
        if (kizunaEvidence == null)
        {
            throw new InvalidOperationException("kizuna-evidence-missing");
        }

        ValidateKizunaEvidence(kizunaEvidence);
        if (!kizunaEvidence.CharacterIdentityResolved)
        {
            return ResultFromEvidence(
                "ineligible",
                "character-identity-unresolved",
                kizunaEvidence,
                requiredBondExp: null);
        }
        if (kizunaEvidence.CharacterIsSpecial != true)
        {
            return ResultFromEvidence(
                "ineligible",
                "character-not-special",
                kizunaEvidence,
                requiredBondExp: null);
        }
        if (kizunaEvidence.RecordedSpecialNpc != true)
        {
            return ResultFromEvidence(
                "ineligible",
                "kizuna-not-recorded",
                kizunaEvidence,
                requiredBondExp: null);
        }

        var requiredBondExp = RequiredBondExp(kizunaEvidence.CurrentBondLevel!.Value);
        if (!requiredBondExp.HasValue)
        {
            return ResultFromEvidence(
                "ineligible",
                "bond-level-unsupported",
                kizunaEvidence,
                requiredBondExp: null);
        }
        if (kizunaEvidence.CurrentBondExp != requiredBondExp.Value)
        {
            return ResultFromEvidence(
                "ineligible",
                "bond-exp-not-full",
                kizunaEvidence,
                requiredBondExp);
        }
        if (kizunaEvidence.CurrentBondLevel == 4
            && kizunaEvidence.Level5Gate != true)
        {
            return ResultFromEvidence(
                "ineligible",
                "level-5-event-gate-closed",
                kizunaEvidence,
                requiredBondExp);
        }

        return ResultFromEvidence(
            "eligible",
            "kizuna-checkpoint",
            kizunaEvidence,
            requiredBondExp);
    }

    public static int? RequiredBondExp(int currentBondLevel)
    {
        return currentBondLevel switch
        {
            1 => 6,
            2 => 17,
            3 => 30,
            4 => 45,
            _ => null,
        };
    }

    public static RuntimeScheduledEventEligibilityDiagnostic Invalid(
        string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            throw new ArgumentException(
                "An eligibility failure reason is required.",
                nameof(reason));
        }
        return Result("invalid", reason);
    }

    private static void ValidateKizunaEvidence(
        RuntimeScheduledEventKizunaEvidence evidence)
    {
        if (!evidence.CharacterIdentityResolved)
        {
            if (evidence.RuntimeGuestId.HasValue
                || evidence.CanonicalCharacterId.HasValue
                || evidence.CharacterIsSpecial.HasValue
                || evidence.RecordedSpecialNpc.HasValue
                || evidence.CurrentBondLevel.HasValue
                || evidence.CurrentBondExp.HasValue
                || evidence.Level5Gate.HasValue)
            {
                throw new InvalidOperationException(
                    "unresolved-character-evidence-not-empty");
            }
            return;
        }

        if (evidence.CanonicalCharacterId is not >= 0
            || !evidence.CharacterIsSpecial.HasValue)
        {
            throw new InvalidOperationException(
                "resolved-character-evidence-incomplete");
        }
        if (evidence.CharacterIsSpecial == false)
        {
            if (evidence.RuntimeGuestId.HasValue
                || evidence.RecordedSpecialNpc.HasValue
                || evidence.CurrentBondLevel.HasValue
                || evidence.CurrentBondExp.HasValue
                || evidence.Level5Gate.HasValue)
            {
                throw new InvalidOperationException(
                    "normal-character-kizuna-evidence-present");
            }
            return;
        }
        if (evidence.RuntimeGuestId is not >= 0)
        {
            throw new InvalidOperationException(
                "special-character-runtime-identity-missing");
        }
        if (!evidence.RecordedSpecialNpc.HasValue)
        {
            throw new InvalidOperationException("recorded-kizuna-state-missing");
        }
        if (evidence.RecordedSpecialNpc == false)
        {
            if (evidence.CurrentBondLevel.HasValue
                || evidence.CurrentBondExp.HasValue
                || evidence.Level5Gate.HasValue)
            {
                throw new InvalidOperationException(
                    "unrecorded-character-kizuna-values-present");
            }
            return;
        }
        if (!evidence.CurrentBondLevel.HasValue
            || !evidence.CurrentBondExp.HasValue)
        {
            throw new InvalidOperationException("recorded-kizuna-values-incomplete");
        }
        if (evidence.CurrentBondLevel < 0 || evidence.CurrentBondExp < 0)
        {
            throw new InvalidOperationException("recorded-kizuna-values-negative");
        }
        if (evidence.CurrentBondLevel == 4)
        {
            if (!evidence.Level5Gate.HasValue)
            {
                throw new InvalidOperationException("level-5-event-gate-missing");
            }
        }
        else if (evidence.Level5Gate.HasValue)
        {
            throw new InvalidOperationException(
                "non-level-4-event-gate-present");
        }
    }

    private static void RequireNoEvidence(
        RuntimeScheduledEventKizunaEvidence? evidence,
        string source)
    {
        if (evidence != null)
        {
            throw new InvalidOperationException(
                $"unexpected-kizuna-evidence:{source}");
        }
    }

    private static RuntimeScheduledEventEligibilityDiagnostic Result(
        string disposition,
        string reason)
    {
        return new RuntimeScheduledEventEligibilityDiagnostic(
            disposition,
            reason,
            CharacterIdentityResolved: null,
            RuntimeGuestId: null,
            CanonicalCharacterId: null,
            CharacterIsSpecial: null,
            RecordedSpecialNpc: null,
            CurrentBondLevel: null,
            CurrentBondExp: null,
            RequiredBondExp: null,
            Level5Gate: null);
    }

    private static RuntimeScheduledEventEligibilityDiagnostic ResultFromEvidence(
        string disposition,
        string reason,
        RuntimeScheduledEventKizunaEvidence evidence,
        int? requiredBondExp)
    {
        return new RuntimeScheduledEventEligibilityDiagnostic(
            disposition,
            reason,
            evidence.CharacterIdentityResolved,
            evidence.RuntimeGuestId,
            evidence.CanonicalCharacterId,
            evidence.CharacterIsSpecial,
            evidence.RecordedSpecialNpc,
            evidence.CurrentBondLevel,
            evidence.CurrentBondExp,
            requiredBondExp,
            evidence.Level5Gate);
    }
}
