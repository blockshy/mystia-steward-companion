import {
  DEFAULT_AUTO_ROLLBACKS,
  DEFAULT_AUTO_STEP_RETRIES,
  type CompanionPreferences,
} from '@/companion/preferences';
import {
  reduceAutomationStageOutcome,
  requiresManualAutomationResolution,
  resolveAutomationNextAttemptAtMs,
  resolveAutomationResponseStage,
  resolveAutomationStepSeconds,
  resolveAutomationStepStartedAtMs,
  type AutomationRequestStage,
} from '@/companion/automation-machine';
import type {
  NormalOrderExecutionTarget,
  SpecialFoodTargetWirePolicy,
} from '@/companion/types';

export type AutomationStep =
  | 'idle'
  | 'match-order'
  | 'ensure-beverage'
  | 'ensure-cooking'
  | 'deliver-food'
  | 'complete-order'
  | 'done'
  | 'paused';

export interface RareAutomationRecipeTarget extends SpecialFoodTargetWirePolicy {
  recipeId: number;
  foodId: number;
  recipeName: string;
  cookerName: string;
  extraIngredientIds: number[];
  foodTags: string[];
  favorite: boolean;
  preferenceFallback: boolean;
}

export interface RareAutomationBeverageTarget {
  beverageId: number;
  beverageName: string;
  favorite: boolean;
}

export interface AutoFirstOrderState {
  orderKey: string;
  recipeTarget: RareAutomationRecipeTarget | null;
  recipeTargetSignature: string;
  recipeTargetRevision: number;
  beverageTarget: RareAutomationBeverageTarget | null;
  prepared: boolean;
  cookingJobId: string;
  beverageHandled: boolean;
  beverageHandledAtMs: number;
  step: AutomationStep;
  stepStartedAtMs: number;
  lastProgressAtMs: number;
  retryCount: number;
  retryStage: AutomationStep | '';
  rollbackCount: number;
  rollbackTargetSignature: string;
  rollbackTargetRevision: number;
  nextAttemptAtMs: number;
  lastError: string;
  detailMessage: string;
  detailUpdatedAtMs: number;
  paused: boolean;
  manualResolutionRequired: boolean;
  pausedStage: AutomationStep | '';
  pauseReasonCode: string;
  lastRuntimeEventSequence: number;
}

export interface NormalAutoOrderState {
  orderKey: string;
  executionTarget: NormalOrderExecutionTarget | null;
  executionTargetBusinessGeneration: number;
  prepared: boolean;
  cookingJobId: string;
  beverageHandled: boolean;
  beverageHandledAtMs: number;
  foodDelivered: boolean;
  foodDeliveredAtMs: number;
  completed: boolean;
  completedAtMs: number;
  step: AutomationStep;
  stepStartedAtMs: number;
  lastProgressAtMs: number;
  retryCount: number;
  retryStage: AutomationStep | '';
  rollbackCount: number;
  rollbackTargetSignature: string;
  rollbackTargetRevision: number;
  nextAttemptAtMs: number;
  lastError: string;
  detailMessage: string;
  detailUpdatedAtMs: number;
  paused: boolean;
  manualResolutionRequired: boolean;
  pausedStage: AutomationStep | '';
  pauseReasonCode: string;
  lastRuntimeEventSequence: number;
}

export type OrderPreparationStepCode =
  | 'beverage-delivered'
  | 'beverage-delivery-commit-uncertain'
  | 'food-delivery-commit-uncertain'
  | 'cooking-started'
  | 'cooking-start-unowned'
  | 'cooking-pending'
  | 'cooking-cooker-waiting'
  | 'cooking-mismatch-stored'
  | 'cooking-tags-unreadable-stored'
  | 'cooking-ownership-lost'
  | 'cooking-controller-reused'
  | 'cooking-progress-stalled'
  | 'cooking-progress-regressed'
  | 'cooking-result-unreadable'
  | 'cooking-target-unavailable-stored'
  | 'cooking-target-already-served-stored'
  | 'cooking-delivery-blocked'
  | 'cooking-delivery-commit-uncertain'
  | 'cooking-delivery-cleanup-blocked'
  | 'cooking-warmer-commit-uncertain'
  | 'cooking-warmer-reset-blocked'
  | 'cooking-cancelled'
  | 'cooking-manual-handoff-completed'
  | 'cooking-manual-handoff-expired'
  | 'cooking-manual-handoff-resolved'
  | 'cooking-manual-handoff-unreadable'
  | 'order-evaluation-state-unreadable'
  | 'order-evaluation-commit-uncertain'
  | 'order-evaluation-target-mismatch'
  | 'order-evaluation-closeout-unresolved'
  | 'order-terminated-before-evaluation'
  | 'food-delivered'
  | 'order-completed';

export interface OrderPreparationStep {
  code?: OrderPreparationStepCode | '';
  name: string;
  ok: boolean;
  skipped: boolean;
  message: string;
}

export interface OrderPreparationResponse {
  ok: boolean;
  prepared: boolean;
  servedFood?: boolean;
  servedBeverage?: boolean;
  completedOrder?: boolean;
  error: string | null;
  order: {
    traceId?: string;
    deskCode: number;
    guestId: number | null;
    guestName: string;
    foodTag: string;
    beverageTag: string;
  };
  recipeId: number;
  recipeName: string;
  beverageId: number;
  beverageName: string;
  automation: {
    outcome: 'waiting' | 'progressed' | 'completed' | 'interrupted' | 'retryable-failure' | 'blocked' | 'fatal' | 'cancelled' | '';
    stage: string;
    reasonCode: string;
    jobId: string;
    retryAfterMs: number;
  };
  steps: OrderPreparationStep[];
}

export function emptyAutoFirstOrderState(orderKey = '', now = 0): AutoFirstOrderState {
  return {
    orderKey,
    recipeTarget: null,
    recipeTargetSignature: '',
    recipeTargetRevision: 0,
    beverageTarget: null,
    prepared: false,
    cookingJobId: '',
    beverageHandled: false,
    beverageHandledAtMs: 0,
    step: 'idle',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    retryStage: '',
    rollbackCount: 0,
    rollbackTargetSignature: '',
    rollbackTargetRevision: 0,
    nextAttemptAtMs: 0,
    lastError: '',
    detailMessage: '',
    detailUpdatedAtMs: 0,
    paused: false,
    manualResolutionRequired: false,
    pausedStage: '',
    pauseReasonCode: '',
    lastRuntimeEventSequence: 0,
  };
}

export function emptyNormalAutoOrderState(orderKey: string, now = 0): NormalAutoOrderState {
  return {
    orderKey,
    executionTarget: null,
    executionTargetBusinessGeneration: 0,
    prepared: false,
    cookingJobId: '',
    beverageHandled: false,
    beverageHandledAtMs: 0,
    foodDelivered: false,
    foodDeliveredAtMs: 0,
    completed: false,
    completedAtMs: 0,
    step: 'match-order',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    retryStage: '',
    rollbackCount: 0,
    rollbackTargetSignature: '',
    rollbackTargetRevision: 0,
    nextAttemptAtMs: 0,
    lastError: '',
    detailMessage: '',
    detailUpdatedAtMs: 0,
    paused: false,
    manualResolutionRequired: false,
    pausedStage: '',
    pauseReasonCode: '',
    lastRuntimeEventSequence: 0,
  };
}

export function lockNormalOrderExecutionTarget(
  state: NormalAutoOrderState,
  target: NormalOrderExecutionTarget,
  businessGeneration: number,
): NormalAutoOrderState {
  return {
    ...state,
    executionTarget: target,
    executionTargetBusinessGeneration: businessGeneration,
  };
}

export function clearNormalOrderExecutionTarget(
  state: NormalAutoOrderState,
): NormalAutoOrderState {
  if (!state.executionTarget && state.executionTargetBusinessGeneration === 0) return state;
  return {
    ...state,
    executionTarget: null,
    executionTargetBusinessGeneration: 0,
  };
}

export function getCurrentNormalOrderExecutionTarget(
  state: NormalAutoOrderState | undefined,
  businessGeneration: number,
  specialTargetSignature: string,
  specialTargetRevision: number,
): NormalOrderExecutionTarget | null {
  const target = state?.executionTarget;
  if (!target) return null;
  if (state.executionTargetBusinessGeneration !== businessGeneration) return null;
  return target.specialTargetSignature === specialTargetSignature
    && target.specialTargetRevision === specialTargetRevision
    ? target
    : null;
}

export function markAutomationWaiting<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  step: AutomationStep,
  now: number,
  message: string,
): T {
  return {
    ...state,
    step,
    stepStartedAtMs: resolveAutomationStepStartedAtMs(state.step, step, state.stepStartedAtMs, now),
    lastError: message,
  };
}

export function updateAutomationAfterResponse<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  response: OrderPreparationResponse,
  now: number,
  step: AutomationStep,
  stopOnError: boolean,
  maxStepRetries = DEFAULT_AUTO_STEP_RETRIES,
): T {
  const outcome = response.automation.outcome;
  const responseRequiresManualResolution = requiresManualAutomationResolution(
    response.automation.reasonCode,
    response.steps.map((item) => item.code ?? ''),
  );
  const responseStep = resolveAutomationResponseStage(
    response.automation.stage,
    isAutomationRequestStage(step) ? step : 'match-order',
  );
  const manualResolutionRequired = state.manualResolutionRequired || responseRequiresManualResolution;
  if (manualResolutionRequired) {
    const lastError = summarizeOrderPreparationFailure(response);
    return {
      ...state,
      prepared: state.prepared || responseStep === 'ensure-cooking' || responseStep === 'deliver-food',
      cookingJobId: response.automation.jobId || state.cookingJobId,
      step: 'paused',
      stepStartedAtMs: state.manualResolutionRequired ? state.stepStartedAtMs : now,
      retryCount: 0,
      retryStage: '',
      nextAttemptAtMs: 0,
      lastError: lastError === '未知状态' ? state.lastError : lastError,
      paused: true,
      manualResolutionRequired: true,
      pausedStage: state.manualResolutionRequired ? state.pausedStage : responseStep,
      pauseReasonCode: state.manualResolutionRequired
        ? state.pauseReasonCode
        : response.automation.reasonCode,
    };
  }
  const transition = reduceAutomationStageOutcome(
    state,
    outcome,
    responseStep,
    now,
    stopOnError,
    maxStepRetries,
  );
  const failed = outcome === 'retryable-failure';
  const fatalFailure = outcome === 'blocked' || outcome === 'fatal';
  const shouldPause = state.paused || transition.paused;
  const progressed = transition.progressed;
  const nextRetryCount = progressed ? 0 : transition.retryCount;
  const stageChanged = Boolean(state.retryStage && state.retryStage !== responseStep);
  const nextStep = shouldPause ? 'paused' : responseStep;

  return {
    ...state,
    step: nextStep,
    stepStartedAtMs: resolveAutomationStepStartedAtMs(
      state.step,
      nextStep,
      state.stepStartedAtMs,
      now,
    ),
    lastProgressAtMs: progressed ? now : transition.lastProgressAtMs,
    retryCount: nextRetryCount,
    retryStage: progressed ? '' : transition.retryStage as AutomationStep | '',
    nextAttemptAtMs: resolveAutomationNextAttemptAtMs(
      stageChanged ? 0 : state.nextAttemptAtMs,
      progressed ? 'progressed' : outcome,
      now,
      response.automation.retryAfterMs,
    ),
    lastError: failed
      ? summarizeOrderPreparationFailure(response)
      : fatalFailure
        ? summarizeOrderPreparationFailure(response)
        : progressed
          ? ''
          : state.lastError,
    paused: shouldPause,
    pausedStage: transition.paused ? responseStep : state.pausedStage,
    pauseReasonCode: transition.paused ? response.automation.reasonCode : state.pauseReasonCode,
  };
}

function isAutomationRequestStage(step: AutomationStep): step is AutomationRequestStage {
  return step === 'match-order'
    || step === 'ensure-beverage'
    || step === 'ensure-cooking'
    || step === 'deliver-food'
    || step === 'complete-order';
}

export function formatAutomationState(
  state: AutoFirstOrderState | NormalAutoOrderState,
  preferences?: CompanionPreferences,
): string {
  const now = Date.now();
  const maxStepRetries = preferences?.autoMaxStepRetries ?? DEFAULT_AUTO_STEP_RETRIES;
  const maxRollbacks = preferences?.autoMaxRollbacks ?? DEFAULT_AUTO_ROLLBACKS;
  const parts = [
    `状态 ${getAutomationStepLabel(state.step)}`,
    state.stepStartedAtMs > 0 ? `${resolveAutomationStepSeconds(state.stepStartedAtMs, now)}秒` : '',
    state.retryCount > 0 ? `重试 ${state.retryCount}/${maxStepRetries}` : '',
    state.rollbackCount > 0 ? `回退 ${state.rollbackCount}/${maxRollbacks}` : '',
    state.manualResolutionRequired ? '需要确认已处理' : '',
    state.lastError ? `最近 ${state.lastError}` : '',
  ].filter(Boolean);
  return parts.join(' · ');
}

export function getAutomationStepLabel(step: AutomationStep): string {
  switch (step) {
    case 'match-order':
      return '匹配订单';
    case 'ensure-beverage':
      return '确认酒水';
    case 'ensure-cooking':
      return '确认料理';
    case 'deliver-food':
      return '送达料理';
    case 'complete-order':
      return '完成订单';
    case 'done':
      return '完成';
    case 'paused':
      return '暂停';
    default:
      return '待命';
  }
}

export function didCompleteStepCode(response: OrderPreparationResponse, code: OrderPreparationStepCode): boolean {
  return response.steps.some((step) => step.code === code && step.ok && !step.skipped);
}

export function didNormalOrderDeliverBeverage(response: OrderPreparationResponse): boolean {
  return Boolean(response.servedBeverage)
    || didCompleteStepCode(response, 'beverage-delivered');
}

export function didNormalOrderDeliverFood(response: OrderPreparationResponse): boolean {
  return Boolean(response.servedFood)
    || didCompleteStepCode(response, 'food-delivered');
}

export function didNormalOrderComplete(response: OrderPreparationResponse): boolean {
  return Boolean(response.completedOrder)
    || didCompleteStepCode(response, 'order-completed');
}

export function didNormalOrderCookingStillPending(response: OrderPreparationResponse): boolean {
  return didOrderCookingStillPending(response);
}

export function didOrderCookingStillPending(response: OrderPreparationResponse): boolean {
  return response.automation.stage === 'cooking-delivery'
    && response.automation.outcome === 'waiting';
}

export function didCookingMismatchStored(response: OrderPreparationResponse): boolean {
  return response.steps.some((step) => step.code === 'cooking-mismatch-stored');
}

export function isTransientAutoPreparationFailure(response: OrderPreparationResponse): boolean {
  return response.automation.outcome === 'retryable-failure'
    || response.automation.outcome === 'interrupted';
}

function summarizeOrderPreparationFailure(response: OrderPreparationResponse): string {
  const failed = response.steps.find((step) => !step.ok && !step.skipped);
  return failed ? `${failed.name}: ${failed.message}` : response.error ?? '未知状态';
}
