import {
  getAutomationStageFailureRetirement,
  reduceAutomationCookingRollbackBudget,
  requiresManualAutomationResolution,
  resolveAutomationResponseStage,
  shouldRetainAutomationStateWithoutCandidate,
} from '@/companion/automation-machine';
import {
  type AutoFirstOrderState,
  type AutomationStep,
  type NormalAutoOrderState,
  type OrderPreparationResponse,
  type RareAutomationRecipeTarget,
} from '@/companion/automation-state';
import { type ValidOrderPreparationSelection } from '@/companion/domain/automation';
import {
  buildSpecialBusinessOrderRule,
  buildWackyRejectedRecipeKeyForRareRecipe,
  WACKY_CHALLENGE_TYPE,
} from '@/companion/domain/special-business';
import { type CompanionPreferences } from '@/companion/preferences';
import type {
  AutomationCookingJobSnapshot,
  AutomationRuntimeEvent,
  AutomationSafetyBarrierAckResponse,
  CookerControllerReservation,
  CookerReservationResult,
  NightBusinessOrder,
  NormalBusinessOrder,
  OrderRecommendation,
  SpecialBusinessContext,
} from '@/companion/types';

import { MAX_SPECIAL_BUSINESS_REJECTED_RECIPE_KEYS } from '@/companion/domain/automation-constants';

const AUTOMATION_CONTROL_DETAIL_PREFIX = '自动化阶段暂停\n';

export function toCookerControllerReservation(
  result: CookerReservationResult,
): CookerControllerReservation | null {
  if (result.controllerIndex == null || !result.controllerIdentity || !result.gridPosition) {
    return null;
  }
  return {
    controllerIndex: result.controllerIndex,
    controllerIdentity: result.controllerIdentity,
    gridPosition: { ...result.gridPosition },
  };
}

export function isCookingTagsUnreadableStoredEvent(event: AutomationRuntimeEvent): boolean {
  return event.code === 'cooking-tags-unreadable-stored';
}

export function isBlockingCookingTerminalEvent(event: AutomationRuntimeEvent): boolean {
  return event.terminal && (event.outcome === 'blocked' || event.outcome === 'fatal');
}

export function isManualResolutionAutomationEvent(event: AutomationRuntimeEvent): boolean {
  return (
    event.terminal &&
    event.outcome === 'blocked' &&
    requiresManualAutomationResolution(event.reasonCode, [event.code])
  );
}

export function resolveAutomationEventStage(event: AutomationRuntimeEvent): AutomationStep {
  const runtimeStage =
    event.code === 'mizuchi-contract-mismatch'
      ? 'order'
      : event.code.startsWith('beverage-')
        ? 'beverage'
        : event.code.startsWith('order-')
          ? 'order'
          : event.code === 'cooking-start-unowned'
            ? 'cooking-start'
            : 'cooking-delivery';
  return resolveAutomationResponseStage(runtimeStage, 'ensure-cooking');
}

export function isCookingAutomationEvent(event: AutomationRuntimeEvent): boolean {
  return event.code.startsWith('cooking-') || event.reasonCode.startsWith('cooking-');
}

export function retainRareAutomationContinuityStates(
  states: Map<string, AutoFirstOrderState>,
  activeOrderKeys: ReadonlySet<string>,
): void {
  for (const [orderKey, state] of states) {
    if (!shouldRetainAutomationStateWithoutCandidate(state, activeOrderKeys.has(orderKey))) {
      states.delete(orderKey);
    }
  }
}

export function retainNormalAutomationExecutionStates(states: Map<string, NormalAutoOrderState>): void {
  for (const [orderKey, state] of states) {
    if (!state.manualResolutionRequired && !state.executionTarget && !state.cookingJobId) {
      states.delete(orderKey);
    }
  }
}

export function retainRareAutomationExecutionStates(states: Map<string, AutoFirstOrderState>): void {
  for (const [orderKey, state] of states) {
    if (!state.manualResolutionRequired && !state.cookingJobId) {
      states.delete(orderKey);
    }
  }
}

export function retainRareManualResolutionDiagnosticItems(
  states: ReadonlyMap<string, AutoFirstOrderState>,
  items: Map<string, ValidOrderPreparationSelection>,
): void {
  for (const orderKey of items.keys()) {
    if (!states.get(orderKey)?.manualResolutionRequired) items.delete(orderKey);
  }
}

export function retainRareAutomationExecutionDiagnosticItems(
  states: ReadonlyMap<string, AutoFirstOrderState>,
  items: Map<string, ValidOrderPreparationSelection>,
): void {
  for (const orderKey of items.keys()) {
    const state = states.get(orderKey);
    if (!state?.manualResolutionRequired && !state?.cookingJobId) items.delete(orderKey);
  }
}

export function trimRejectedRecipeKeys(keys: readonly string[]): string[] {
  const merged: string[] = [];
  for (const key of keys) {
    if (!key || merged.includes(key)) continue;
    merged.push(key);
  }
  return merged.slice(-MAX_SPECIAL_BUSINESS_REJECTED_RECIPE_KEYS);
}

export function rememberRejectedRecipeKey(keys: Set<string>, key: string): void {
  if (!key || keys.has(key)) return;
  keys.add(key);
  while (keys.size > MAX_SPECIAL_BUSINESS_REJECTED_RECIPE_KEYS) {
    const oldest = keys.values().next().value as string | undefined;
    if (!oldest) break;
    keys.delete(oldest);
  }
}

export function mergeRejectedRecipeKeys(stateKeys: readonly string[], refKeys: Set<string>): string[] {
  return trimRejectedRecipeKeys([...stateKeys, ...refKeys]);
}

export function buildRejectedRecipeKeyForRareTarget(
  specialBusiness: SpecialBusinessContext | null | undefined,
  order: NightBusinessOrder,
  target: RareAutomationRecipeTarget | null,
): string {
  if (!target) return '';
  if (specialBusiness?.challengeType !== WACKY_CHALLENGE_TYPE) return '';
  const rule = buildSpecialBusinessOrderRule(specialBusiness, order.specialBusinessRole);
  if (rule.foodTarget.enforcement !== 'require' || rule.foodTarget.tags.length === 0) return '';
  return buildWackyRejectedRecipeKeyForRareRecipe(
    rule.foodTarget.tags,
    target.foodId,
    target.recipeId,
    target.extraIngredientIds,
  );
}

export function composeAutomationDetail(...parts: Array<string | null | undefined | false>): string {
  return parts
    .map((part) => (typeof part === 'string' ? part.trim() : ''))
    .filter(Boolean)
    .join('\n');
}

export function withAutomationDetail<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  now: number,
  ...parts: Array<string | null | undefined | false>
): T {
  const detailMessage = composeAutomationDetail(...parts);
  if (!detailMessage) return state;
  if (detailMessage === state.detailMessage) return state;
  return {
    ...state,
    detailMessage,
    detailUpdatedAtMs: now,
  };
}

export function enforceAutomationRollbackLimit<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  maxRollbacks: number,
  now: number,
): T {
  if (state.paused || state.rollbackCount <= 0 || state.rollbackCount < maxRollbacks) return state;
  const limitMessage = `自动重新制作次数已达到上限 ${state.rollbackCount}/${maxRollbacks}，已暂停该订单。`;
  return {
    ...state,
    paused: true,
    pausedStage: state.step,
    pauseReasonCode: 'rollback-limit-reached',
    step: 'paused',
    stepStartedAtMs: now,
    lastError: state.lastError ? `${state.lastError}；${limitMessage}` : limitMessage,
  };
}

export function recordAutomationTransportFailure<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  now: number,
  message: string,
  requestStage: AutomationStep,
  stopOnError: boolean,
  maxStepRetries: number,
): T {
  const retryCount = (state.retryStage === requestStage ? state.retryCount : 0) + 1;
  const paused = stopOnError && retryCount >= maxStepRetries;
  return {
    ...state,
    retryCount,
    retryStage: requestStage,
    nextAttemptAtMs: now + 1000,
    paused,
    pausedStage: paused ? requestStage : state.pausedStage,
    pauseReasonCode: paused ? 'transport-failure' : state.pauseReasonCode,
    step: paused ? 'paused' : requestStage,
    stepStartedAtMs: paused || state.step !== requestStage ? now : state.stepStartedAtMs,
    lastError: message,
  };
}

export function retireDisabledRareAutomationFailure(
  state: AutoFirstOrderState,
  order: NightBusinessOrder,
  preferences: CompanionPreferences,
  now: number,
  forceFullFeed = false,
): AutoFirstOrderState {
  if (state.manualResolutionRequired) return state;
  const enabledStages: AutomationStep[] = ['idle', 'match-order', 'done'];
  if (preferences.autoPrepTakeBeverage || forceFullFeed) enabledStages.push('ensure-beverage');
  if (preferences.autoPrepStartCooking || forceFullFeed) enabledStages.push('ensure-cooking');
  if (preferences.autoPrepCollectCooking || forceFullFeed) enabledStages.push('deliver-food');
  if (preferences.autoPrepCompleteOrder || forceFullFeed) enabledStages.push('complete-order');
  const retirement = getAutomationStageFailureRetirement({ ...state, enabledStages });
  if (!retirement.clearRetry && !retirement.clearPause) return state;

  const nextStep: AutomationStep =
    (preferences.autoPrepTakeBeverage || forceFullFeed) && !state.beverageHandled && !order.hasServedBeverage
      ? 'ensure-beverage'
      : (preferences.autoPrepStartCooking || forceFullFeed) && !state.prepared && !order.hasServedFood
        ? 'ensure-cooking'
        : (preferences.autoPrepCollectCooking || forceFullFeed) && state.prepared && !order.hasServedFood
          ? 'deliver-food'
          : preferences.autoPrepCompleteOrder || forceFullFeed
            ? 'complete-order'
            : 'idle';
  return {
    ...state,
    retryCount: retirement.clearRetry || retirement.clearPause ? 0 : state.retryCount,
    retryStage: retirement.clearRetry || retirement.clearPause ? '' : state.retryStage,
    nextAttemptAtMs: retirement.clearRetry || retirement.clearPause ? 0 : state.nextAttemptAtMs,
    paused: retirement.clearPause ? false : state.paused,
    pausedStage: retirement.clearPause ? '' : state.pausedStage,
    pauseReasonCode: retirement.clearPause ? '' : state.pauseReasonCode,
    step: retirement.clearPause ? nextStep : state.step,
    stepStartedAtMs: retirement.clearPause ? now : state.stepStartedAtMs,
    lastError: retirement.clearPause || retirement.clearRetry ? '' : state.lastError,
  };
}

export function retireDisabledNormalAutomationFailure(
  state: NormalAutoOrderState,
  order: NormalBusinessOrder,
  preferences: CompanionPreferences,
  now: number,
  forceFullFeed = false,
): NormalAutoOrderState {
  if (state.manualResolutionRequired) return state;
  const enabledStages: AutomationStep[] = ['idle', 'match-order', 'done'];
  if (preferences.autoNormalTakeBeverage || forceFullFeed) enabledStages.push('ensure-beverage');
  if (preferences.autoNormalStartCooking || forceFullFeed) enabledStages.push('ensure-cooking');
  if (preferences.autoNormalDeliverFood || forceFullFeed) enabledStages.push('deliver-food');
  if (preferences.autoNormalCompleteOrder || forceFullFeed) enabledStages.push('complete-order');
  const retirement = getAutomationStageFailureRetirement({ ...state, enabledStages });
  if (!retirement.clearRetry && !retirement.clearPause) return state;

  const nextStep: AutomationStep =
    (preferences.autoNormalTakeBeverage || forceFullFeed) &&
    !state.beverageHandled &&
    !order.hasServedBeverage
      ? 'ensure-beverage'
      : (preferences.autoNormalStartCooking || forceFullFeed) && !state.prepared && !order.hasServedFood
        ? 'ensure-cooking'
        : (preferences.autoNormalDeliverFood || forceFullFeed) && state.prepared && !order.hasServedFood
          ? 'deliver-food'
          : (preferences.autoNormalCompleteOrder || forceFullFeed) &&
              (order.readyToEvaluate ||
                ((state.foodDelivered || order.hasServedFood) &&
                  (state.beverageHandled || order.hasServedBeverage)))
            ? 'complete-order'
            : 'idle';
  return {
    ...state,
    retryCount: retirement.clearRetry || retirement.clearPause ? 0 : state.retryCount,
    retryStage: retirement.clearRetry || retirement.clearPause ? '' : state.retryStage,
    nextAttemptAtMs: retirement.clearRetry || retirement.clearPause ? 0 : state.nextAttemptAtMs,
    paused: retirement.clearPause ? false : state.paused,
    pausedStage: retirement.clearPause ? '' : state.pausedStage,
    pauseReasonCode: retirement.clearPause ? '' : state.pauseReasonCode,
    step: retirement.clearPause ? nextStep : state.step,
    stepStartedAtMs: retirement.clearPause ? now : state.stepStartedAtMs,
    lastError: retirement.clearPause || retirement.clearRetry ? '' : state.lastError,
  };
}

export function matchesRareAutomationEvent(
  event: AutomationRuntimeEvent,
  item: OrderRecommendation,
  state: AutoFirstOrderState,
): boolean {
  if (event.targetKind !== 'rare') return false;
  if (
    event.orderLifecycleSequence <= 0 ||
    item.order.orderLifecycleSequence <= 0 ||
    event.orderLifecycleSequence !== item.order.orderLifecycleSequence
  )
    return false;
  if (state.cookingJobId && event.jobId) return state.cookingJobId === event.jobId;
  const order = item.order;
  if (event.traceId && order.traceId) return event.traceId === order.traceId;
  if (event.foodId >= 0 && state.recipeTarget && state.recipeTarget.foodId !== event.foodId) return false;
  if (event.deskCode >= 0 && order.deskCode !== event.deskCode) return false;
  if (event.guestId != null && order.guestId != null && event.guestId !== order.guestId) return false;
  return true;
}

export function isAutomationLeaseUnavailableResponse(response: OrderPreparationResponse): boolean {
  return (
    response.automation.reasonCode === 'automation-lease-unavailable' || response.automation.stage === 'lease'
  );
}

export function automationBarrierAckFailure(
  sequence: number,
  error: string,
): AutomationSafetyBarrierAckResponse {
  return {
    ok: false,
    sequence,
    acknowledgedCount: 0,
    acknowledgedSequences: [],
    status: '',
    error,
  };
}

export function resetRareOrderStateAfterRuntimeMismatch(
  state: AutoFirstOrderState,
  now: number,
  event: AutomationRuntimeEvent,
): AutoFirstOrderState {
  const rollback = reduceAutomationCookingRollbackBudget(state.rollbackCount, event);
  return {
    ...state,
    prepared: false,
    cookingJobId: '',
    paused: false,
    step: 'ensure-cooking',
    stepStartedAtMs: now,
    lastProgressAtMs: state.lastProgressAtMs,
    retryCount: 0,
    retryStage: '',
    rollbackCount: rollback.rollbackCount,
    nextAttemptAtMs: now + 500,
    lastError:
      rollback.action === 'deferred'
        ? `${event.message || '特殊经营料理目标暂时不可用或已变化。'} 已保留旧目标的重新制作计数，等待读取到新的目标标识后再确认变化。`
        : event.message || '非目标成品已放入保温箱，重新制作目标料理。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: '',
    pauseReasonCode: '',
  };
}

export function matchesNormalAutomationEvent(
  event: AutomationRuntimeEvent,
  order: NormalBusinessOrder,
): boolean {
  if (event.targetKind !== 'normal') return false;
  if (
    event.orderLifecycleSequence <= 0 ||
    order.orderLifecycleSequence <= 0 ||
    event.orderLifecycleSequence !== order.orderLifecycleSequence
  )
    return false;
  if (event.traceId && order.traceId) return event.traceId === order.traceId;
  if (event.orderKey && order.orderKey) return event.orderKey === order.orderKey;
  if (event.foodId >= 0 && order.foodId !== event.foodId) return false;
  if (event.deskCode >= 0 && order.deskCode !== event.deskCode) return false;
  if (event.guestName && order.guestName && event.guestName !== order.guestName) return false;
  return true;
}

export function findRareAutomationCookingJob(
  jobs: readonly AutomationCookingJobSnapshot[],
  selection: ValidOrderPreparationSelection,
  state?: AutoFirstOrderState,
): AutomationCookingJobSnapshot | null {
  const order = selection.item.order;
  if (state?.cookingJobId) {
    return (
      jobs.find(
        (job) =>
          job.targetKind === 'rare' &&
          job.jobId === state.cookingJobId &&
          job.orderLifecycleSequence > 0 &&
          job.orderLifecycleSequence === order.orderLifecycleSequence,
      ) ?? null
    );
  }

  const recipeTarget = state?.recipeTarget ?? selection.recipeTarget;
  return (
    jobs.find((job) => {
      if (job.targetKind !== 'rare') return false;
      if (job.orderLifecycleSequence <= 0 || job.orderLifecycleSequence !== order.orderLifecycleSequence)
        return false;
      if (job.traceId && order.traceId) return job.traceId === order.traceId;
      return (
        job.deskCode === order.deskCode &&
        job.foodId === recipeTarget?.foodId &&
        (job.guestId == null || order.guestId == null || job.guestId === order.guestId)
      );
    }) ?? null
  );
}

export function findNormalAutomationCookingJob(
  jobs: readonly AutomationCookingJobSnapshot[],
  order: NormalBusinessOrder,
  state?: NormalAutoOrderState,
): AutomationCookingJobSnapshot | null {
  if (state?.cookingJobId) {
    return (
      jobs.find(
        (job) =>
          job.targetKind === 'normal' &&
          job.jobId === state.cookingJobId &&
          job.orderLifecycleSequence > 0 &&
          job.orderLifecycleSequence === order.orderLifecycleSequence,
      ) ?? null
    );
  }

  return (
    jobs.find((job) => {
      if (job.targetKind !== 'normal') return false;
      if (job.orderLifecycleSequence <= 0 || job.orderLifecycleSequence !== order.orderLifecycleSequence)
        return false;
      if (job.traceId && order.traceId) return job.traceId === order.traceId;
      return Boolean(job.orderKey && order.orderKey && job.orderKey === order.orderKey);
    }) ?? null
  );
}

export function reconcileStateWithActiveCookingJob<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  job: AutomationCookingJobSnapshot,
  now: number,
): T {
  if (state.manualResolutionRequired) {
    return {
      ...state,
      prepared: true,
    };
  }
  const jobChanged = state.cookingJobId !== job.jobId;
  const provesCookingProgress = state.paused
    ? state.pausedStage === 'ensure-cooking' && jobChanged
    : jobChanged;
  const nextStep =
    job.transactionStage === 'evaluation-receipt' || job.controlStage === 'OrderEvaluation'
      ? 'complete-order'
      : 'deliver-food';
  const previousControlDetail = state.detailMessage.startsWith(AUTOMATION_CONTROL_DETAIL_PREFIX);
  const controlDetail =
    job.controlState === 'active'
      ? ''
      : `${AUTOMATION_CONTROL_DETAIL_PREFIX}${job.controlMessage || '当前生效配置或自动化控制权尚未就绪。'}`;
  const detailMessage = controlDetail || previousControlDetail ? controlDetail : state.detailMessage;
  const detailChanged = detailMessage !== state.detailMessage;
  return {
    ...state,
    prepared: true,
    cookingJobId: job.jobId,
    step: state.paused && !provesCookingProgress ? state.step : nextStep,
    stepStartedAtMs: jobChanged && (!state.paused || provesCookingProgress) ? now : state.stepStartedAtMs,
    lastProgressAtMs: jobChanged ? now : state.lastProgressAtMs,
    retryCount: provesCookingProgress ? 0 : state.retryCount,
    retryStage: provesCookingProgress ? '' : state.retryStage,
    nextAttemptAtMs: provesCookingProgress ? 0 : state.nextAttemptAtMs,
    lastError: provesCookingProgress ? '' : state.lastError,
    paused: provesCookingProgress ? false : state.paused,
    pausedStage: provesCookingProgress ? '' : state.pausedStage,
    pauseReasonCode: provesCookingProgress ? '' : state.pauseReasonCode,
    detailMessage,
    detailUpdatedAtMs: detailChanged ? now : state.detailUpdatedAtMs,
  };
}

export function clearAutomationCookingJobControlDetail<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  now: number,
): T {
  if (!state.detailMessage.startsWith(AUTOMATION_CONTROL_DETAIL_PREFIX)) return state;
  return {
    ...state,
    detailMessage: '',
    detailUpdatedAtMs: now,
  };
}

export function resetNormalOrderStateAfterRuntimeMismatch(
  state: NormalAutoOrderState,
  orderKey: string,
  now: number,
  event: AutomationRuntimeEvent,
): NormalAutoOrderState {
  const rollback = reduceAutomationCookingRollbackBudget(state.rollbackCount, event);
  return {
    ...state,
    orderKey,
    executionTarget: null,
    executionTargetBusinessGeneration: 0,
    prepared: false,
    cookingJobId: '',
    foodDelivered: false,
    foodDeliveredAtMs: 0,
    completed: false,
    completedAtMs: 0,
    paused: false,
    step: 'ensure-cooking',
    stepStartedAtMs: now,
    lastProgressAtMs: state.lastProgressAtMs,
    retryCount: 0,
    retryStage: '',
    rollbackCount: rollback.rollbackCount,
    nextAttemptAtMs: now + 500,
    lastError:
      rollback.action === 'deferred'
        ? `${event.message || '特殊经营料理目标暂时不可用或已变化。'} 已保留旧目标的重新制作计数，等待读取到新的目标标识后再确认变化。`
        : event.message || '非目标成品已放入保温箱，重新制作目标料理。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: '',
    pauseReasonCode: '',
  };
}

export function pauseRareOrderStateAfterRuntimeFailure(
  state: AutoFirstOrderState,
  now: number,
  event: AutomationRuntimeEvent,
): AutoFirstOrderState {
  const manualResolutionRequired = state.manualResolutionRequired || isManualResolutionAutomationEvent(event);
  return {
    ...state,
    prepared: manualResolutionRequired && isCookingAutomationEvent(event) ? true : state.prepared,
    cookingJobId: manualResolutionRequired ? event.jobId || state.cookingJobId : '',
    paused: true,
    manualResolutionRequired,
    step: 'paused',
    stepStartedAtMs: now,
    retryCount: 0,
    retryStage: '',
    nextAttemptAtMs: 0,
    lastError: event.message || '无法确认自动化对游戏状态的影响，已暂停该订单。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: manualResolutionRequired ? resolveAutomationEventStage(event) : state.step,
    pauseReasonCode: event.reasonCode || event.code,
  };
}

export function pauseNormalOrderStateAfterRuntimeFailure(
  state: NormalAutoOrderState,
  orderKey: string,
  now: number,
  event: AutomationRuntimeEvent,
): NormalAutoOrderState {
  const manualResolutionRequired = state.manualResolutionRequired || isManualResolutionAutomationEvent(event);
  return {
    ...state,
    orderKey,
    prepared: manualResolutionRequired && isCookingAutomationEvent(event) ? true : state.prepared,
    cookingJobId: manualResolutionRequired ? event.jobId || state.cookingJobId : '',
    foodDelivered: false,
    foodDeliveredAtMs: 0,
    completed: false,
    completedAtMs: 0,
    paused: true,
    manualResolutionRequired,
    step: 'paused',
    stepStartedAtMs: now,
    retryCount: 0,
    retryStage: '',
    nextAttemptAtMs: 0,
    lastError: event.message || '无法确认自动化对游戏状态的影响，已暂停该订单。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: manualResolutionRequired ? resolveAutomationEventStage(event) : state.step,
    pauseReasonCode: event.reasonCode || event.code,
  };
}
