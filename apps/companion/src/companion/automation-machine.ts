import type { AutomationJobOutcome } from '@/companion/types';

export type AutomationRequestStage =
  | 'match-order'
  | 'ensure-beverage'
  | 'ensure-cooking'
  | 'deliver-food'
  | 'complete-order';

const MANUAL_RESOLUTION_REASON_CODES = new Set([
  'beverage-delivery-commit-uncertain',
  'food-delivery-commit-uncertain',
  'cooking-start-unowned',
  'cooking-progress-stalled',
  'cooking-progress-regressed',
  'cooking-result-unreadable',
  'cooking-tags-unreadable-stored',
  'cooking-delivery-blocked',
  'cooking-delivery-commit-uncertain',
  'cooking-warmer-commit-uncertain',
  'cooking-warmer-reset-blocked',
  'cooking-delivery-timeout',
  'cooking-warmer-reset-failed',
  'cooking-job-exception',
  'cooking-manual-handoff-unreadable',
  'cooking-delivery-cleanup-blocked',
  'cooking-delivery-cleanup-failed',
  'order-evaluation-commit-uncertain',
]);

export interface AutomationStageCounters {
  retryCount: number;
  lastProgressAtMs: number;
  retryStage: string;
}

export interface AutomationStageTransition extends AutomationStageCounters {
  progressed: boolean;
  paused: boolean;
}

export function resolveAutomationStepStartedAtMs(
  currentStep: string,
  nextStep: string,
  currentStartedAtMs: number,
  transitionAtMs: number,
): number {
  return currentStep === nextStep && currentStartedAtMs > 0
    ? currentStartedAtMs
    : transitionAtMs;
}

export function resolveAutomationStepSeconds(startedAtMs: number, now: number): number {
  return startedAtMs > 0
    ? Math.max(0, Math.floor((now - startedAtMs) / 1000))
    : 0;
}

export function resolveAutomationWaitingStep(input: {
  schedulerAvailable: boolean;
  authoritativeResponseStep: AutomationRequestStage | null;
  completionEnabled: boolean;
}): AutomationRequestStage | 'idle' {
  if (!input.schedulerAvailable) return 'ensure-cooking';
  if (input.authoritativeResponseStep) return input.authoritativeResponseStep;
  return input.completionEnabled ? 'complete-order' : 'idle';
}

export function canAdvanceAutomationRuntimeEventSequence(
  manualResolutionRequired: boolean,
  nextEventRequiresManualResolution: boolean,
): boolean {
  return !manualResolutionRequired || nextEventRequiresManualResolution;
}

export function shouldRetireMissingManualBarrier(
  manualResolutionRequired: boolean,
  lastRuntimeEventSequence: number,
  unresolvedBarrierSequences: ReadonlySet<number>,
): boolean {
  return manualResolutionRequired
    && lastRuntimeEventSequence > 0
    && !unresolvedBarrierSequences.has(lastRuntimeEventSequence);
}

export function isAutomationResponseCurrent(input: {
  requestEpoch: number;
  currentEpoch: number;
  runtimeEnabled: boolean;
  responseStartEventSequence?: number;
  currentEventSequence?: number;
}): boolean {
  return input.runtimeEnabled
    && input.requestEpoch === input.currentEpoch
    && (input.currentEventSequence ?? 0) <= (input.responseStartEventSequence ?? 0);
}

export function resolveAutomationNextAttemptAtMs(
  current: number,
  outcome: AutomationJobOutcome | '',
  now: number,
  retryAfterMs: number,
): number {
  if (outcome === 'retryable-failure' || outcome === 'interrupted') {
    return now + Math.max(250, retryAfterMs);
  }
  return outcome === 'progressed' || outcome === 'completed' ? 0 : current;
}

/**
 * Reduces one structured Mod outcome without inspecting localized messages.
 */
export function reduceAutomationStageOutcome(
  current: AutomationStageCounters,
  outcome: AutomationJobOutcome | '',
  stage: string,
  now: number,
  stopOnError: boolean,
  maxStepRetries: number,
): AutomationStageTransition {
  const stageChanged = Boolean(current.retryStage && current.retryStage !== stage);
  const currentRetryCount = stageChanged ? 0 : current.retryCount;
  const progressed = outcome === 'progressed' || outcome === 'completed';
  const retryCount = outcome === 'retryable-failure'
    ? currentRetryCount + 1
    : progressed
      ? 0
      : currentRetryCount;
  const paused = outcome === 'blocked'
    || outcome === 'fatal'
    || (outcome === 'retryable-failure' && stopOnError && retryCount >= maxStepRetries);
  return {
    retryCount,
    lastProgressAtMs: progressed ? now : current.lastProgressAtMs,
    retryStage: progressed
      ? ''
      : outcome === 'retryable-failure' || outcome === 'interrupted'
        ? stage
        : stageChanged
          ? ''
          : current.retryStage,
    progressed,
    paused,
  };
}

export function getAutomationStageFailureRetirement(input: {
  retryStage: string;
  paused: boolean;
  pausedStage: string;
  manualResolutionRequired?: boolean;
  enabledStages: readonly string[];
}): { clearRetry: boolean; clearPause: boolean } {
  if (input.manualResolutionRequired) {
    return { clearRetry: false, clearPause: false };
  }
  const enabled = new Set(input.enabledStages);
  return {
    clearRetry: Boolean(input.retryStage && !enabled.has(input.retryStage)),
    clearPause: Boolean(input.paused && input.pausedStage && !enabled.has(input.pausedStage)),
  };
}

/**
 * Maps the Mod's runtime stage vocabulary to the frontend state-machine stages.
 * Explicit C# stages win over the stage inferred before sending a combined request.
 */
export function resolveAutomationResponseStage(
  runtimeStage: string,
  fallback: AutomationRequestStage,
): AutomationRequestStage {
  switch (runtimeStage.trim().toLowerCase()) {
    case 'beverage':
      return 'ensure-beverage';
    case 'cooking-start':
      return 'ensure-cooking';
    case 'cooking-delivery':
      return 'deliver-food';
    case 'order':
      return 'complete-order';
    case 'validation':
      return 'match-order';
    default:
      return fallback;
  }
}

export function requiresManualAutomationResolution(
  reasonCode: string,
  stepCodes: readonly string[] = [],
): boolean {
  return isManualResolutionCode(reasonCode)
    || stepCodes.some(isManualResolutionCode);
}

function isManualResolutionCode(code: string): boolean {
  return MANUAL_RESOLUTION_REASON_CODES.has(code);
}

export function selectAutomationRequestStage(input: {
  needsBeverage: boolean;
  needsCooking: boolean;
  needsDelivery: boolean;
  needsCompletion: boolean;
  fallback?: AutomationRequestStage;
}): AutomationRequestStage {
  if (input.needsBeverage) return 'ensure-beverage';
  if (input.needsCooking) return 'ensure-cooking';
  if (input.needsDelivery) return 'deliver-food';
  if (input.needsCompletion) return 'complete-order';
  return input.fallback ?? 'match-order';
}
