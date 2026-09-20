import { completeFirstNormalOrder, completeFirstRareOrder, prepareNextRareOrder } from '@/companion/api';
import {
  reconcileAutomationRollbackTarget,
  requiresManualAutomationResolution,
  resolveAutomationResponseStage,
  resolveAutomationWaitingStep,
  selectAutomationRequestStage,
  type AutomationRequestStage,
} from '@/companion/automation-machine';
import {
  clearNormalOrderExecutionTarget,
  didCompleteStepCode,
  didCookingMismatchStored,
  didNormalOrderComplete,
  didNormalOrderCookingStillPending,
  didNormalOrderDeliverBeverage,
  didNormalOrderDeliverFood,
  didOrderCookingStillPending,
  emptyAutoFirstOrderState,
  emptyNormalAutoOrderState,
  formatAutomationState,
  isTransientAutoPreparationFailure,
  lockNormalOrderExecutionTarget,
  markAutomationWaiting,
  updateAutomationAfterResponse,
  type AutomationStep,
} from '@/companion/automation-state';
import {
  applyRareServedStateFromResponse,
  buildAutoOrderKey,
  buildNormalAutoOrderKey,
  buildNormalCookerDemand,
  buildNormalCookingTargetDecision,
  buildNormalOrderAutomationSignature,
  formatOrderPreparationResponse,
  formatRareAutomationMissingBeverageTargetMessage,
  formatRareAutomationMissingRecipeTargetMessage,
  getWackyRareCookingDeferral,
  hasAutomationActionEnabled,
  hasNormalOrderActionEnabled,
  lockRareAutomationTargets,
  reconcileRareRecipeTargetForSpecialBusiness,
  reserveAutomationCookerSlot,
  reserveRareCookerSlot,
  selectOperationalOrderPreparationCandidates,
  selectOrderPreparationCandidates,
  shouldAttemptNormalBeverage,
  shouldAttemptNormalCompletion,
  shouldAttemptNormalCooking,
  syncNormalOrderStateWithSnapshot,
  syncRareStateWithOrderServedState,
  type OrderPreparationCandidateResult,
  type ValidOrderPreparationSelection,
} from '@/companion/domain/automation';
import { getNormalAutomationTargetSelection } from '@/companion/domain/automation-target';
import {
  buildNormalRequestPreferences,
  buildRarePreparationPreferences,
} from '@/companion/domain/automation-request-plan';
import { buildAutomationCookerPool, getRareCookerRequirement } from '@/companion/domain/cookers';
import { sortNormalOrders } from '@/companion/domain/sorting';
import {
  buildSpecialFoodTargetWirePolicy,
  isWackyKoishiBossFullFeedContext,
} from '@/companion/domain/special-business';
import { formatDesk } from '@/companion/formatters';
import { useOrderAutomationIntervals } from '@/companion/hooks/useOrderAutomationIntervals';
import { type CompanionPreferences } from '@/companion/preferences';
import type {
  AutomationCookerCycle,
  CookerControllerReservation,
  CookerReservationResult,
  NormalBusinessOrder,
} from '@/companion/types';
import { useCallback, useMemo } from 'react';

import { AUTO_FIRST_ORDER_TICK_MS, AUTO_NORMAL_ORDER_TICK_MS } from '@/companion/domain/automation-constants';
import { type NormalAutomationDecisionDiagnosticInput } from '@/companion/domain/automation-diagnostics';
import {
  buildRejectedRecipeKeyForRareTarget,
  enforceAutomationRollbackLimit,
  findNormalAutomationCookingJob,
  findRareAutomationCookingJob,
  reconcileStateWithActiveCookingJob,
  recordAutomationTransportFailure,
  retainNormalAutomationExecutionStates,
  retainRareAutomationContinuityStates,
  retainRareAutomationExecutionDiagnosticItems,
  retainRareAutomationExecutionStates,
  retainRareManualResolutionDiagnosticItems,
  retireDisabledNormalAutomationFailure,
  retireDisabledRareAutomationFailure,
  toCookerControllerReservation,
  withAutomationDetail,
} from '@/companion/domain/automation-lifecycle';

import type { OrderAutomationOptions } from '@/companion/hooks/useOrderAutomation';
interface AutomationSchedulerServices {
  refreshRareOrderDiagnostics: (now?: number) => void;
  refreshNormalOrderDiagnostics: (orders?: NormalBusinessOrder[], now?: number) => void;
  publishAutomationTargetRotationDiagnostic: (input: {
    targetKind: 'rare' | 'normal';
    orderIdentity: string;
    previousSignature: string;
    previousRevision: number;
    nextSignature: string;
    nextRevision: number;
    previousRollbackCount: number;
    specialBusinessRole: string;
  }) => void;
  publishRareAutomationDecisionDiagnostic: (
    eventName: string,
    candidateResult: OrderPreparationCandidateResult,
    message: string,
    selectionPreferences: CompanionPreferences,
  ) => void;
  publishNormalAutomationDecisionDiagnostic: (input: NormalAutomationDecisionDiagnosticInput) => void;
}
type SchedulerOptions = Pick<
  OrderAutomationOptions,
  | 'state'
  | 'control'
  | 'apiToken'
  | 'normalizedEndpoint'
  | 'companionPreferences'
  | 'companionDeviceAuthority'
  | 'snapshot'
  | 'runtime'
  | 'recommendationData'
  | 'recommendationDataSignature'
  | 'favorites'
  | 'orderRecommendations'
  | 'operationalOrderRecommendations'
  | 'normalExecutionTargets'
  | 'normalExecutionTargetsEnabled'
>;
/** Owns both recurring schedulers; page changes only affect their diagnostic projection. */
export function useAutomationSchedulers(
  {
    state,
    control,
    apiToken,
    normalizedEndpoint,
    companionPreferences,
    companionDeviceAuthority,
    snapshot,
    runtime,
    recommendationData,
    recommendationDataSignature,
    favorites,
    orderRecommendations,
    operationalOrderRecommendations,
    normalExecutionTargets,
    normalExecutionTargetsEnabled,
  }: SchedulerOptions,
  {
    refreshRareOrderDiagnostics,
    refreshNormalOrderDiagnostics,
    publishAutomationTargetRotationDiagnostic,
    publishRareAutomationDecisionDiagnostic,
    publishNormalAutomationDecisionDiagnostic,
  }: AutomationSchedulerServices,
) {
  const {
    rareOrderStatesRef,
    rareOrderDiagnosticItemsRef,
    autoFirstOrderBusyRef,
    normalOrderStatesRef,
    normalOrderBusyRef,
    lastAutoFirstOrderAtRef,
    lastAutoNormalOrderAtRef,
    automationCookerCycleRef,
    automationRequestEpochRef,
    rareParticipationMutationBusyRef,
    automationRuntimeEnabledRef,
    publishAutoPrepBusy,
    publishAutoPrepMessage,
    publishNormalOrderBusy,
    publishNormalOrderMessage,
    publishNormalOrderPausedCount,
    scheduleAutomationRefresh,
    isAutomationRequestCurrent,
    handleAutomationControlPlaneResponse,
    getSpecialBusinessRejectedRecipeKeys,
  } = state;
  const { automationRuntimeEnabled } = control;
  const normalOrderSignature = useMemo(
    () =>
      `${buildNormalOrderAutomationSignature(snapshot?.normalBusiness?.orders ?? [])}|jobs:${(
        snapshot?.automationCookingJobs ?? []
      )
        .filter((job) => job.targetKind === 'normal')
        .map((job) =>
          [
            job.jobId,
            job.state,
            job.reasonCode,
            job.controlState,
            job.controlReasonCode,
            job.controlStage,
            job.controlAuthorityRevision,
          ].join(':'),
        )
        .join(',')}`,
    [snapshot?.automationCookingJobs, snapshot?.normalBusiness?.orders],
  );
  const normalAutomationTargetByKey = useMemo(
    () =>
      new Map(
        normalExecutionTargets.normalExecutionTargets.map((selection) => [selection.orderKey, selection]),
      ),
    [normalExecutionTargets.normalExecutionTargets],
  );
  const getAutomationCookerCycle = useCallback(
    (now: number): AutomationCookerCycle => {
      const bucket = Math.floor(now / AUTO_FIRST_ORDER_TICK_MS);
      if (!automationCookerCycleRef.current || automationCookerCycleRef.current.bucket !== bucket) {
        automationCookerCycleRef.current = {
          bucket,
          usedControllerIndexes: new Set<number>(),
          labelsByControllerIndex: new Map<number, string>(),
        };
      }

      return automationCookerCycleRef.current;
    },
    [automationCookerCycleRef],
  );

  const runAutoFirstOrder = useCallback(async () => {
    if (
      !automationRuntimeEnabledRef.current ||
      !companionPreferences.autoRareOrderEnabled ||
      rareParticipationMutationBusyRef.current ||
      autoFirstOrderBusyRef.current
    )
      return;
    const requestEpoch = automationRequestEpochRef.current;
    const now = Date.now();
    if (now - lastAutoFirstOrderAtRef.current < AUTO_FIRST_ORDER_TICK_MS) return;
    if (!apiToken) {
      publishAutoPrepMessage('自动化已开启，但本地 API Token 不可用。');
      return;
    }

    if (!hasAutomationActionEnabled(companionPreferences)) {
      retainRareAutomationExecutionStates(rareOrderStatesRef.current);
      retainRareAutomationExecutionDiagnosticItems(
        rareOrderStatesRef.current,
        rareOrderDiagnosticItemsRef.current,
      );
      refreshRareOrderDiagnostics(now);
      if (
        !companionPreferences.autoNormalOrderEnabled ||
        !hasNormalOrderActionEnabled(companionPreferences)
      ) {
        publishAutoPrepMessage('自动化已开启，请在“自动化 -> 执行配置”中启用至少一个处理阶段。');
      } else {
        publishAutoPrepMessage('');
      }
      return;
    }

    if (orderRecommendations.pending || !orderRecommendations.isCurrent) {
      publishAutoPrepMessage('自动化\n推荐计算中，等待下一次结果。');
      return;
    }

    if (orderRecommendations.error) {
      publishAutoPrepMessage(`自动化\n${orderRecommendations.error}`);
      return;
    }

    const selectionPreferences = companionPreferences;
    const candidateResult =
      operationalOrderRecommendations === null
        ? selectOrderPreparationCandidates(
            orderRecommendations.recommendations,
            favorites,
            selectionPreferences,
            rareOrderStatesRef.current,
            snapshot?.specialBusiness,
          )
        : selectOperationalOrderPreparationCandidates(
            operationalOrderRecommendations,
            favorites,
            selectionPreferences,
            rareOrderStatesRef.current,
            snapshot?.specialBusiness,
          );
    if (candidateResult.selections.length === 0) {
      publishRareAutomationDecisionDiagnostic(
        'rare-candidate-empty',
        candidateResult,
        candidateResult.message,
        selectionPreferences,
      );
      if ((snapshot?.automationCookingJobs ?? []).some((job) => job.targetKind === 'rare')) {
        publishAutoPrepMessage(
          `自动化\n${candidateResult.message}\nMod 中仍有活动料理任务，已保留订单状态并等待游戏状态更新。`,
        );
        return;
      }
      const activeOrderKeys = new Set(
        (operationalOrderRecommendations === null
          ? orderRecommendations.recommendations
          : operationalOrderRecommendations.map(({ recommendation }) => recommendation)
        ).map(buildAutoOrderKey),
      );
      retainRareAutomationContinuityStates(rareOrderStatesRef.current, activeOrderKeys);
      retainRareManualResolutionDiagnosticItems(
        rareOrderStatesRef.current,
        rareOrderDiagnosticItemsRef.current,
      );
      refreshRareOrderDiagnostics(now);
      publishAutoPrepMessage(`自动化\n${candidateResult.message}`);
      return;
    }
    if (candidateResult.skips.length > 0) {
      publishRareAutomationDecisionDiagnostic(
        'rare-candidate-partial',
        candidateResult,
        candidateResult.messages[0] ?? '部分稀客订单被跳过。',
        selectionPreferences,
      );
    }

    const previousDiagnosticItems = new Map(rareOrderDiagnosticItemsRef.current);
    const activeKeys = new Set(
      candidateResult.selections.map((selection) => buildAutoOrderKey(selection.item)),
    );
    for (const [orderKey, selection] of previousDiagnosticItems) {
      if (
        rareOrderStatesRef.current.get(orderKey)?.manualResolutionRequired ||
        findRareAutomationCookingJob(
          snapshot?.automationCookingJobs ?? [],
          selection,
          rareOrderStatesRef.current.get(orderKey),
        )
      ) {
        activeKeys.add(orderKey);
      }
    }
    rareOrderDiagnosticItemsRef.current.clear();
    for (const selection of candidateResult.selections) {
      rareOrderDiagnosticItemsRef.current.set(buildAutoOrderKey(selection.item), selection);
    }
    for (const [orderKey, selection] of previousDiagnosticItems) {
      if (activeKeys.has(orderKey) && !rareOrderDiagnosticItemsRef.current.has(orderKey)) {
        rareOrderDiagnosticItemsRef.current.set(orderKey, selection);
      }
    }
    for (const key of Array.from(rareOrderStatesRef.current.keys())) {
      const state = rareOrderStatesRef.current.get(key);
      if (!activeKeys.has(key) && !state?.manualResolutionRequired) {
        rareOrderStatesRef.current.delete(key);
      }
    }

    autoFirstOrderBusyRef.current = true;
    lastAutoFirstOrderAtRef.current = now;
    publishAutoPrepBusy(true);
    let activeRequestSelection: ValidOrderPreparationSelection | null = null;
    let activeRequestEventSequence = 0;
    let activeRequestStage: AutomationRequestStage = 'match-order';
    try {
      const globalMessages: string[] = [];
      let updatedOrderDetailCount = 0;
      const cookerCycle = getAutomationCookerCycle(now);
      const cookerPool = buildAutomationCookerPool(runtime);
      const effectiveSpecialBusinessRejectedRecipeKeys = getSpecialBusinessRejectedRecipeKeys();
      const normalCookerDemand = buildNormalCookerDemand(
        snapshot?.normalBusiness?.orders ?? [],
        normalOrderStatesRef.current,
        companionPreferences,
        runtime,
        now,
        recommendationData,
        recommendationDataSignature,
        snapshot?.nightBusinessGeneration ?? 0,
        snapshot?.specialBusiness,
        effectiveSpecialBusinessRejectedRecipeKeys,
      );

      let admittedCandidateCount = 0;
      for (const selection of candidateResult.selections) {
        activeRequestSelection = selection;
        const orderKey = buildAutoOrderKey(selection.item);
        let currentState =
          rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
        currentState = lockRareAutomationTargets(currentState, selection);
        const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
          snapshot?.specialBusiness,
          selection.item.order.specialBusinessRole,
        );
        const activeCookingJob = findRareAutomationCookingJob(
          snapshot?.automationCookingJobs ?? [],
          selection,
          currentState,
        );
        const requiresRecipeTarget =
          !selection.item.order.hasServedFood &&
          (activeCookingJob !== null ||
            Boolean(currentState.cookingJobId) ||
            ((companionPreferences.autoPrepStartCooking || forceKoishiFullFeedAutomation) &&
              !currentState.prepared) ||
            ((companionPreferences.autoPrepCollectCooking || forceKoishiFullFeedAutomation) &&
              currentState.prepared));
        const targetReconciliation = reconcileRareRecipeTargetForSpecialBusiness(
          snapshot?.specialBusiness,
          snapshot?.nightBusinessGeneration ?? 0,
          selection.item,
          currentState,
          selection.recipeTarget,
          requiresRecipeTarget,
          now,
          effectiveSpecialBusinessRejectedRecipeKeys,
        );
        if (targetReconciliation.rollbackTargetRotated) {
          publishAutomationTargetRotationDiagnostic({
            targetKind: 'rare',
            orderIdentity: orderKey,
            previousSignature: currentState.rollbackTargetSignature,
            previousRevision: currentState.rollbackTargetRevision,
            nextSignature: targetReconciliation.state.rollbackTargetSignature,
            nextRevision: targetReconciliation.state.rollbackTargetRevision,
            previousRollbackCount: currentState.rollbackCount,
            specialBusinessRole: selection.item.order.specialBusinessRole ?? '',
          });
        }
        currentState = targetReconciliation.state;
        const targetReconciliationMessage = targetReconciliation.message;
        const specialTargetPolicy = targetReconciliation.specialTargetPolicy;
        currentState = syncRareStateWithOrderServedState(currentState, selection.item.order, now);
        currentState = retireDisabledRareAutomationFailure(
          currentState,
          selection.item.order,
          companionPreferences,
          now,
          forceKoishiFullFeedAutomation,
        );
        if (activeCookingJob && !selection.item.order.hasServedFood) {
          currentState = reconcileStateWithActiveCookingJob(currentState, activeCookingJob, now);
        }
        currentState = enforceAutomationRollbackLimit(
          currentState,
          companionPreferences.autoMaxRollbacks,
          now,
        );
        activeRequestEventSequence = currentState.lastRuntimeEventSequence;
        rareOrderStatesRef.current.set(orderKey, currentState);
        if (activeCookingJob?.state === 'manual-handoff-expired' && !selection.item.order.hasServedFood) {
          const expiredHandoffState = withAutomationDetail(
            currentState,
            now,
            '旧目标成品仍在等待处理；同一订单不会按当前目标重复开锅。',
            '请先在游戏中处理该订单的过期交接成品。其他订单会继续自动化。',
          );
          if (expiredHandoffState !== currentState) {
            rareOrderStatesRef.current.set(orderKey, expiredHandoffState);
            updatedOrderDetailCount += 1;
          }
          continue;
        }
        if (currentState.paused) {
          rareOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(
              currentState,
              now,
              targetReconciliationMessage,
              formatAutomationState(currentState, companionPreferences),
              currentState.manualResolutionRequired
                ? '游戏操作结果无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
                : '稀客自动化已暂停该订单，订单状态变化或手动重试后会继续。',
            ),
          );
          updatedOrderDetailCount += 1;
          continue;
        }
        if (targetReconciliation.policyError) {
          rareOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(currentState, now, targetReconciliation.policyError),
          );
          updatedOrderDetailCount += 1;
          continue;
        }
        if (currentState.nextAttemptAtMs > now) {
          rareOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(
              currentState,
              now,
              targetReconciliationMessage,
              `当前阶段将在 ${Math.max(1, Math.ceil((currentState.nextAttemptAtMs - now) / 1000))} 秒后重试。`,
            ),
          );
          updatedOrderDetailCount += 1;
          continue;
        }

        let shouldPrepareFood =
          (companionPreferences.autoPrepStartCooking || forceKoishiFullFeedAutomation) &&
          !currentState.prepared;
        let shouldPrepareBeverage =
          (companionPreferences.autoPrepTakeBeverage || forceKoishiFullFeedAutomation) &&
          !currentState.beverageHandled;
        let cookingDeferralNote = '';
        let targetAvailabilityNote = '';
        const rejectedRecipeKey = buildRejectedRecipeKeyForRareTarget(
          snapshot?.specialBusiness,
          selection.item.order,
          currentState.recipeTarget,
        );
        if (
          shouldPrepareFood &&
          rejectedRecipeKey &&
          effectiveSpecialBusinessRejectedRecipeKeys.includes(rejectedRecipeKey)
        ) {
          shouldPrepareFood = false;
          cookingDeferralNote =
            '当前目标标签下，该料理加料组合已被游戏判定不匹配，等待推荐刷新或目标标签更新后再制作。';
        }
        const specialBusinessCookingDeferral = getWackyRareCookingDeferral(
          snapshot?.specialBusiness,
          selection.item,
          currentState.recipeTarget,
          selection.recipe,
        );
        if (shouldPrepareFood && specialBusinessCookingDeferral) {
          shouldPrepareFood = false;
          cookingDeferralNote = specialBusinessCookingDeferral;
        }
        if (shouldPrepareFood && !currentState.recipeTarget) {
          shouldPrepareFood = false;
          targetAvailabilityNote =
            targetReconciliationMessage ||
            formatRareAutomationMissingRecipeTargetMessage(
              selection.item,
              companionPreferences.autoPrepRecipeFavoritesOnly,
            );
        }
        if (shouldPrepareBeverage && !currentState.beverageTarget) {
          shouldPrepareBeverage = false;
          targetAvailabilityNote = formatRareAutomationMissingBeverageTargetMessage(
            selection.item,
            companionPreferences.autoPrepBeverageFavoritesOnly,
          );
        }
        const canAttemptCompletionPreflight =
          (forceKoishiFullFeedAutomation || companionPreferences.autoPrepCompleteOrder) &&
          (currentState.prepared || activeCookingJob !== null || selection.item.order.hasServedFood);
        if (
          admittedCandidateCount >= companionPreferences.autoRareConcurrency &&
          (shouldPrepareFood || shouldPrepareBeverage || canAttemptCompletionPreflight)
        ) {
          break;
        }
        const schedulerNote: CookerReservationResult = shouldPrepareFood
          ? reserveRareCookerSlot(
              cookerCycle,
              getRareCookerRequirement(currentState.recipeTarget),
              `稀客 ${selection.item.order.guestName || '当前订单'} · 桌 ${formatDesk(selection.item.order.deskCode)}`,
              cookerPool,
              normalCookerDemand,
            )
          : { ok: true, message: '' };
        let cookerReservation = shouldPrepareFood ? toCookerControllerReservation(schedulerNote) : null;
        if (!schedulerNote.ok || (shouldPrepareFood && cookerReservation == null)) {
          shouldPrepareFood = false;
          cookerReservation = null;
        }
        const hasExecutableCandidateAction =
          shouldPrepareFood || shouldPrepareBeverage || canAttemptCompletionPreflight;
        if (hasExecutableCandidateAction) {
          admittedCandidateCount += 1;
        }

        let preflightMessage = '';
        let preflightResponseStep: AutomationRequestStage | null = null;
        if (canAttemptCompletionPreflight) {
          activeRequestEventSequence = currentState.lastRuntimeEventSequence;
          activeRequestStage = selectAutomationRequestStage({
            needsBeverage: shouldPrepareBeverage,
            needsCooking: false,
            needsDelivery: false,
            needsCompletion: true,
          });
          const completeResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            specialTargetPolicy,
            currentState.recipeTarget,
            currentState.beverageTarget,
            {
              ...companionPreferences,
              autoPrepStartCooking: false,
              autoPrepTakeBeverage: shouldPrepareBeverage,
              autoPrepCollectCooking:
                forceKoishiFullFeedAutomation || companionPreferences.autoPrepCollectCooking,
              autoPrepCompleteOrder:
                forceKoishiFullFeedAutomation || companionPreferences.autoPrepCompleteOrder,
            },
            null,
            companionDeviceAuthority.authorityRevision,
          );
          const completeResponseAt = Date.now();
          if (!isAutomationRequestCurrent(requestEpoch)) return;
          if (handleAutomationControlPlaneResponse(completeResponse)) return;
          preflightResponseStep = resolveAutomationResponseStage(
            completeResponse.automation.stage,
            activeRequestStage,
          );
          const stateAfterCompleteRequest = rareOrderStatesRef.current.get(orderKey);
          if (
            !isAutomationRequestCurrent(
              requestEpoch,
              activeRequestEventSequence,
              stateAfterCompleteRequest?.lastRuntimeEventSequence ?? 0,
            )
          ) {
            continue;
          }

          if (completeResponse.completedOrder) {
            rareOrderStatesRef.current.set(
              orderKey,
              withAutomationDetail(
                {
                  ...currentState,
                  step: 'done',
                  stepStartedAtMs: completeResponseAt,
                  lastProgressAtMs: completeResponseAt,
                  retryCount: 0,
                  retryStage: '',
                  lastError: '',
                  paused: false,
                },
                completeResponseAt,
                targetReconciliationMessage,
                formatOrderPreparationResponse(completeResponse),
              ),
            );
            updatedOrderDetailCount += 1;
            continue;
          }

          currentState = applyRareServedStateFromResponse(
            currentState,
            selection.item.order,
            completeResponse,
            completeResponseAt,
          );
          const nextState = updateAutomationAfterResponse(
            currentState,
            completeResponse,
            completeResponseAt,
            activeRequestStage,
            companionPreferences.autoPrepStopOnError,
            companionPreferences.autoMaxStepRetries,
          );
          currentState = nextState;
          preflightMessage = formatOrderPreparationResponse(completeResponse);
          if (currentState.paused) {
            rareOrderStatesRef.current.set(
              orderKey,
              withAutomationDetail(
                currentState,
                now,
                targetReconciliationMessage,
                preflightMessage,
                formatAutomationState(currentState, companionPreferences),
                currentState.manualResolutionRequired
                  ? '游戏操作结果无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
                  : '稀客自动化已暂停该订单，订单状态变化或手动重试后会继续。',
              ),
            );
            updatedOrderDetailCount += 1;
            continue;
          }
          if (currentState.nextAttemptAtMs > completeResponseAt) {
            rareOrderStatesRef.current.set(
              orderKey,
              withAutomationDetail(
                currentState,
                completeResponseAt,
                targetReconciliationMessage,
                preflightMessage,
                `当前阶段将在 ${Math.max(1, Math.ceil((currentState.nextAttemptAtMs - completeResponseAt) / 1000))} 秒后重试。`,
              ),
            );
            updatedOrderDetailCount += 1;
            continue;
          }
          shouldPrepareFood &&= !currentState.prepared;
          shouldPrepareBeverage &&= !currentState.beverageHandled;
        }

        if (!shouldPrepareFood && !shouldPrepareBeverage) {
          const waitingAt = Date.now();
          const waitingState = markAutomationWaiting(
            currentState,
            resolveAutomationWaitingStep({
              schedulerAvailable: schedulerNote.ok,
              authoritativeResponseStep: preflightResponseStep,
              completionEnabled: forceKoishiFullFeedAutomation || companionPreferences.autoPrepCompleteOrder,
            }),
            waitingAt,
            !schedulerNote.ok
              ? schedulerNote.message
              : cookingDeferralNote
                ? cookingDeferralNote
                : targetAvailabilityNote
                  ? targetAvailabilityNote
                  : forceKoishiFullFeedAutomation || companionPreferences.autoPrepCompleteOrder
                    ? '等待料理出锅后直接送达，或等待下一轮完成订单。'
                    : '已按当前设置完成可执行步骤；自动完成订单未开启。',
          );
          rareOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(
              waitingState,
              waitingAt,
              targetReconciliationMessage,
              preflightMessage,
              formatAutomationState(waitingState, companionPreferences),
            ),
          );
          updatedOrderDetailCount += 1;
          continue;
        }

        const preparePreferences = buildRarePreparationPreferences(companionPreferences, {
          shouldPrepareBeverage,
          shouldPrepareFood,
          forceKoishiFullFeedAutomation,
        });

        activeRequestEventSequence = currentState.lastRuntimeEventSequence;
        activeRequestStage = selectAutomationRequestStage({
          needsBeverage: shouldPrepareBeverage,
          needsCooking: shouldPrepareFood,
          needsDelivery: false,
          needsCompletion: false,
        });
        const prepareResponse = await prepareNextRareOrder(
          normalizedEndpoint,
          apiToken,
          selection.item,
          specialTargetPolicy,
          currentState.recipeTarget,
          currentState.beverageTarget,
          preparePreferences,
          shouldPrepareFood ? cookerReservation : null,
          companionDeviceAuthority.authorityRevision,
        );
        const prepareResponseAt = Date.now();
        if (!isAutomationRequestCurrent(requestEpoch)) return;
        if (handleAutomationControlPlaneResponse(prepareResponse)) return;
        const stateAfterPrepareRequest = rareOrderStatesRef.current.get(orderKey);
        if (
          !isAutomationRequestCurrent(
            requestEpoch,
            activeRequestEventSequence,
            stateAfterPrepareRequest?.lastRuntimeEventSequence ?? 0,
          )
        ) {
          continue;
        }

        const stateAfterPrepareDelivery = applyRareServedStateFromResponse(
          currentState,
          selection.item.order,
          prepareResponse,
          prepareResponseAt,
        );
        const pendingRareCooking = didOrderCookingStillPending(prepareResponse);
        const startedRareCooking = didCompleteStepCode(prepareResponse, 'cooking-started');
        const cookingMismatchStored = didCookingMismatchStored(prepareResponse);
        const cookingInterrupted =
          prepareResponse.automation.outcome === 'interrupted' ||
          prepareResponse.automation.outcome === 'blocked' ||
          prepareResponse.automation.outcome === 'fatal';
        const responseStage = resolveAutomationResponseStage(
          prepareResponse.automation.stage,
          activeRequestStage,
        );
        const manualCookingResolution =
          requiresManualAutomationResolution(
            prepareResponse.automation.reasonCode,
            prepareResponse.steps.map((step) => step.code ?? ''),
          ) &&
          (responseStage === 'ensure-cooking' || responseStage === 'deliver-food');
        const nextPrepared =
          manualCookingResolution ||
          (!cookingMismatchStored &&
            !cookingInterrupted &&
            (stateAfterPrepareDelivery.prepared || startedRareCooking || pendingRareCooking));
        const nextBeverageHandled =
          stateAfterPrepareDelivery.beverageHandled ||
          didCompleteStepCode(prepareResponse, 'beverage-delivered') ||
          Boolean(prepareResponse.servedBeverage);
        const transientFailure = !prepareResponse.ok && isTransientAutoPreparationFailure(prepareResponse);
        const beverageHandledAtMs =
          nextBeverageHandled && !currentState.beverageHandled
            ? prepareResponseAt
            : currentState.beverageHandledAtMs;
        const rollbackCount = currentState.rollbackCount;
        const nextState = enforceAutomationRollbackLimit(
          updateAutomationAfterResponse(
            {
              ...currentState,
              orderKey,
              prepared: nextPrepared,
              cookingJobId: nextPrepared ? prepareResponse.automation.jobId || currentState.cookingJobId : '',
              beverageHandled: nextBeverageHandled,
              beverageHandledAtMs,
              rollbackCount,
            },
            prepareResponse,
            prepareResponseAt,
            prepareResponse.automation.outcome === 'retryable-failure' ||
              prepareResponse.automation.outcome === 'interrupted' ||
              prepareResponse.automation.outcome === 'blocked' ||
              prepareResponse.automation.outcome === 'fatal'
              ? activeRequestStage
              : shouldPrepareFood
                ? 'ensure-cooking'
                : shouldPrepareBeverage
                  ? 'ensure-beverage'
                  : 'match-order',
            companionPreferences.autoPrepStopOnError,
            companionPreferences.autoMaxStepRetries,
          ),
          companionPreferences.autoMaxRollbacks,
          prepareResponseAt,
        );
        let finalState = nextState;
        let finalStateUpdatedAt = prepareResponseAt;
        let followUpMessage = '';
        if (
          (forceKoishiFullFeedAutomation || companionPreferences.autoPrepCompleteOrder) &&
          nextBeverageHandled &&
          !currentState.beverageHandled &&
          !finalState.paused &&
          finalState.nextAttemptAtMs <= prepareResponseAt
        ) {
          activeRequestEventSequence = finalState.lastRuntimeEventSequence;
          activeRequestStage = 'complete-order';
          const immediateCompleteResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            specialTargetPolicy,
            finalState.recipeTarget,
            finalState.beverageTarget,
            {
              ...companionPreferences,
              autoPrepStartCooking: false,
              autoPrepCollectCooking:
                forceKoishiFullFeedAutomation || companionPreferences.autoPrepCollectCooking,
              autoPrepCompleteOrder:
                forceKoishiFullFeedAutomation || companionPreferences.autoPrepCompleteOrder,
            },
            null,
            companionDeviceAuthority.authorityRevision,
          );
          const immediateCompleteResponseAt = Date.now();
          finalStateUpdatedAt = immediateCompleteResponseAt;
          if (!isAutomationRequestCurrent(requestEpoch)) return;
          if (handleAutomationControlPlaneResponse(immediateCompleteResponse)) return;
          const stateAfterImmediateComplete = rareOrderStatesRef.current.get(orderKey);
          if (
            !isAutomationRequestCurrent(
              requestEpoch,
              activeRequestEventSequence,
              stateAfterImmediateComplete?.lastRuntimeEventSequence ?? 0,
            )
          ) {
            continue;
          }
          if (immediateCompleteResponse.completedOrder) {
            rareOrderStatesRef.current.set(
              orderKey,
              withAutomationDetail(
                {
                  ...finalState,
                  step: 'done',
                  stepStartedAtMs: immediateCompleteResponseAt,
                  lastProgressAtMs: immediateCompleteResponseAt,
                  retryCount: 0,
                  retryStage: '',
                  lastError: '',
                  paused: false,
                },
                immediateCompleteResponseAt,
                targetReconciliationMessage,
                preflightMessage,
                formatOrderPreparationResponse(prepareResponse),
                formatOrderPreparationResponse(immediateCompleteResponse),
              ),
            );
            updatedOrderDetailCount += 1;
            continue;
          }

          finalState = applyRareServedStateFromResponse(
            finalState,
            selection.item.order,
            immediateCompleteResponse,
            immediateCompleteResponseAt,
          );
          finalState = enforceAutomationRollbackLimit(
            updateAutomationAfterResponse(
              finalState,
              immediateCompleteResponse,
              immediateCompleteResponseAt,
              'complete-order',
              companionPreferences.autoPrepStopOnError,
              companionPreferences.autoMaxStepRetries,
            ),
            companionPreferences.autoMaxRollbacks,
            immediateCompleteResponseAt,
          );
          followUpMessage = formatOrderPreparationResponse(immediateCompleteResponse);
        }

        const suffix = finalState.paused
          ? finalState.manualResolutionRequired
            ? '游戏操作结果无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
            : '稀客自动化已暂停该订单，订单状态变化或手动重试后会继续。'
          : transientFailure
            ? '当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        const schedulerSuffix = schedulerNote.ok ? '' : schedulerNote.message;
        rareOrderStatesRef.current.set(
          orderKey,
          withAutomationDetail(
            finalState,
            finalStateUpdatedAt,
            targetReconciliationMessage,
            preflightMessage,
            formatOrderPreparationResponse(prepareResponse),
            followUpMessage,
            formatAutomationState(finalState, companionPreferences),
            schedulerSuffix,
            suffix,
          ),
        );
        updatedOrderDetailCount += 1;
      }

      if (candidateResult.messages.length > 0) {
        globalMessages.push(...candidateResult.messages.map((message) => `跳过\n${message}`));
      }

      refreshRareOrderDiagnostics(Date.now());
      publishAutoPrepMessage(
        updatedOrderDetailCount > 0 || globalMessages.length > 0
          ? `自动化\n${[
              updatedOrderDetailCount > 0
                ? `已更新 ${updatedOrderDetailCount} 笔订单详情，可展开对应订单查看。`
                : '',
              ...globalMessages,
            ]
              .filter(Boolean)
              .join('\n\n')}`
          : '自动化\n当前没有需要执行的新步骤。',
      );
      scheduleAutomationRefresh();
    } catch (err) {
      const failureAt = Date.now();
      const message = err instanceof Error ? err.message : String(err);
      if (!isAutomationRequestCurrent(requestEpoch)) return;
      let pausedCount = 0;
      const failedSelections = activeRequestSelection ? [activeRequestSelection] : [];
      for (const selection of failedSelections) {
        const orderKey = buildAutoOrderKey(selection.item);
        const state =
          rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, failureAt);
        if (
          !isAutomationRequestCurrent(
            requestEpoch,
            activeRequestEventSequence,
            state.lastRuntimeEventSequence,
          )
        ) {
          return;
        }
        const failedState = recordAutomationTransportFailure(
          state,
          failureAt,
          message,
          activeRequestStage,
          companionPreferences.autoPrepStopOnError,
          companionPreferences.autoMaxStepRetries,
        );
        if (failedState.paused) pausedCount += 1;
        rareOrderStatesRef.current.set(
          orderKey,
          withAutomationDetail(
            failedState,
            failureAt,
            message,
            failedState.paused
              ? '本阶段网络请求达到重试上限，当前订单已暂停。'
              : '本阶段网络请求失败，将按下一轮调度重试。',
          ),
        );
      }
      refreshRareOrderDiagnostics(failureAt);
      publishAutoPrepMessage(
        `自动化\n${message}\n${pausedCount > 0 ? `达到重试上限并暂停 ${pausedCount} 笔订单。` : '请求失败，将自动重试。'}`,
      );
    } finally {
      autoFirstOrderBusyRef.current = false;
      publishAutoPrepBusy(false);
    }
  }, [
    automationRuntimeEnabledRef,
    companionPreferences,
    rareParticipationMutationBusyRef,
    autoFirstOrderBusyRef,
    automationRequestEpochRef,
    lastAutoFirstOrderAtRef,
    apiToken,
    orderRecommendations.pending,
    orderRecommendations.isCurrent,
    orderRecommendations.error,
    orderRecommendations.recommendations,
    operationalOrderRecommendations,
    favorites,
    rareOrderStatesRef,
    snapshot?.specialBusiness,
    snapshot?.automationCookingJobs,
    snapshot?.normalBusiness?.orders,
    snapshot?.nightBusinessGeneration,
    rareOrderDiagnosticItemsRef,
    publishAutoPrepBusy,
    publishAutoPrepMessage,
    refreshRareOrderDiagnostics,
    publishRareAutomationDecisionDiagnostic,
    getAutomationCookerCycle,
    runtime,
    getSpecialBusinessRejectedRecipeKeys,
    normalOrderStatesRef,
    recommendationData,
    recommendationDataSignature,
    scheduleAutomationRefresh,
    normalizedEndpoint,
    companionDeviceAuthority.authorityRevision,
    isAutomationRequestCurrent,
    handleAutomationControlPlaneResponse,
    publishAutomationTargetRotationDiagnostic,
  ]);

  const runAutoNormalOrder = useCallback(async () => {
    if (
      !automationRuntimeEnabledRef.current ||
      !companionPreferences.autoNormalOrderEnabled ||
      rareParticipationMutationBusyRef.current ||
      normalOrderBusyRef.current
    )
      return;
    const requestEpoch = automationRequestEpochRef.current;
    const now = Date.now();
    if (now - lastAutoNormalOrderAtRef.current < AUTO_NORMAL_ORDER_TICK_MS) return;
    if (!hasNormalOrderActionEnabled(companionPreferences)) {
      retainNormalAutomationExecutionStates(normalOrderStatesRef.current);
      refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], now);
      publishNormalOrderMessage(
        '普客自动化已开启，请至少启用一个处理阶段：送达酒水、自动制作料理、送达料理或完成订单。',
      );
      return;
    }

    if (!apiToken) {
      publishNormalOrderMessage('普客自动化已开启，但本地 API Token 不可用。');
      return;
    }

    const orders = sortNormalOrders(snapshot?.normalBusiness?.orders ?? []).filter(
      (item) => !item.hasEvaluated,
    );
    const activeKeys = new Set(orders.map(buildNormalAutoOrderKey));
    for (const key of Array.from(normalOrderStatesRef.current.keys())) {
      const state = normalOrderStatesRef.current.get(key);
      const hasActiveCookingJob = Boolean(
        state?.cookingJobId &&
          (snapshot?.automationCookingJobs ?? []).some(
            (job) => job.targetKind === 'normal' && job.jobId === state.cookingJobId,
          ),
      );
      if (!activeKeys.has(key) && !state?.manualResolutionRequired && !hasActiveCookingJob) {
        normalOrderStatesRef.current.delete(key);
      }
    }
    for (const order of orders) {
      const orderKey = buildNormalAutoOrderKey(order);
      const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
        snapshot?.specialBusiness,
        order.specialBusinessRole,
      );
      const syncedState = syncNormalOrderStateWithSnapshot(
        order,
        normalOrderStatesRef.current.get(orderKey),
        now,
        companionPreferences,
      );
      if (syncedState) {
        normalOrderStatesRef.current.set(
          orderKey,
          enforceAutomationRollbackLimit(
            retireDisabledNormalAutomationFailure(
              syncedState,
              order,
              companionPreferences,
              now,
              forceKoishiFullFeedAutomation,
            ),
            companionPreferences.autoMaxRollbacks,
            now,
          ),
        );
      }
    }
    refreshNormalOrderDiagnostics(orders, now);

    if (orders.length === 0) {
      retainNormalAutomationExecutionStates(normalOrderStatesRef.current);
      refreshNormalOrderDiagnostics([], now);
      publishNormalOrderMessage('普客自动化\n当前没有可处理的普客订单。');
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    const cookerCycle = getAutomationCookerCycle(now);
    const cookerPool = buildAutomationCookerPool(runtime);
    const cookerReservationByOrderKey = new Map<string, CookerControllerReservation>();
    const schedulerMessages: string[] = [];
    const blockedOrders = orders.filter((order) => order.canAutomate === false);
    const blockedText =
      blockedOrders.length > 0
        ? `\n暂不可自动处理 ${blockedOrders.length} 笔：${blockedOrders
            .slice(0, 2)
            .map(
              (order) =>
                `桌 ${formatDesk(order.deskCode)} · ${order.actionBlockReason || '未读取到可执行客人控制器'}`,
            )
            .join('；')}${blockedOrders.length > 2 ? '；…' : ''}`
        : '';
    const automationOrders = [...orders].sort((left, right) => {
      const leftState = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(left));
      const rightState = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(right));
      const leftCompletionReady = shouldAttemptNormalCompletion(left, leftState, companionPreferences, now)
        ? 1
        : 0;
      const rightCompletionReady = shouldAttemptNormalCompletion(right, rightState, companionPreferences, now)
        ? 1
        : 0;
      return rightCompletionReady - leftCompletionReady;
    });
    const runnableOrders: NormalBusinessOrder[] = [];
    for (const order of automationOrders) {
      if (order.canAutomate === false) continue;

      const orderKey = buildNormalAutoOrderKey(order);
      const storedState = normalOrderStatesRef.current.get(orderKey);
      const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
        snapshot?.specialBusiness,
        order.specialBusinessRole,
      );
      let state =
        syncNormalOrderStateWithSnapshot(order, storedState, now, companionPreferences) ?? storedState;
      if (state) {
        state = retireDisabledNormalAutomationFailure(
          state,
          order,
          companionPreferences,
          now,
          forceKoishiFullFeedAutomation,
        );
        normalOrderStatesRef.current.set(orderKey, state);
      }
      let baseState = state ?? emptyNormalAutoOrderState(orderKey, now);
      const activeCookingJob = findNormalAutomationCookingJob(
        snapshot?.automationCookingJobs ?? [],
        order,
        baseState,
      );
      if (activeCookingJob && !order.hasServedFood) {
        baseState = reconcileStateWithActiveCookingJob(baseState, activeCookingJob, now);
      }
      const rollbackTarget = buildSpecialFoodTargetWirePolicy(
        snapshot?.specialBusiness,
        order.specialBusinessRole,
        snapshot?.nightBusinessGeneration ?? 0,
      );
      const rollbackReconciliation = reconcileAutomationRollbackTarget(
        baseState,
        rollbackTarget.specialTargetSignature,
        rollbackTarget.specialTargetRevision,
        now,
      );
      if (rollbackReconciliation.rotated) {
        publishAutomationTargetRotationDiagnostic({
          targetKind: 'normal',
          orderIdentity: orderKey,
          previousSignature: baseState.rollbackTargetSignature,
          previousRevision: baseState.rollbackTargetRevision,
          nextSignature: rollbackReconciliation.state.rollbackTargetSignature,
          nextRevision: rollbackReconciliation.state.rollbackTargetRevision,
          previousRollbackCount: baseState.rollbackCount,
          specialBusinessRole: order.specialBusinessRole ?? '',
        });
      }
      state = rollbackReconciliation.state;
      normalOrderStatesRef.current.set(orderKey, state);
      if (activeCookingJob?.state === 'manual-handoff-expired' && !order.hasServedFood) {
        normalOrderStatesRef.current.set(
          orderKey,
          withAutomationDetail(
            state,
            now,
            '旧目标成品仍在等待处理；同一订单不会按当前目标重复开锅。',
            '请先在游戏中处理该订单的过期交接成品。其他订单会继续自动化。',
          ),
        );
        schedulerMessages.push(
          `桌 ${formatDesk(order.deskCode)}\n旧目标成品等待人工处理；该订单不占用自动化并发名额。`,
        );
        continue;
      }
      if (state?.paused || (state?.nextAttemptAtMs ?? 0) > now) continue;
      const needsBeverage =
        shouldAttemptNormalBeverage(order, state, companionPreferences, now) ||
        (forceKoishiFullFeedAutomation && !order.hasServedBeverage && state?.beverageHandled !== true);
      const needsCooking =
        shouldAttemptNormalCooking(order, state, companionPreferences, now) ||
        (forceKoishiFullFeedAutomation &&
          !order.hasServedFood &&
          state?.prepared !== true &&
          state?.foodDelivered !== true);
      const needsCompletion = forceKoishiFullFeedAutomation
        ? !order.hasEvaluated &&
          (order.readyToEvaluate ||
            ((state?.foodDelivered || order.hasServedFood) &&
              (state?.beverageHandled || order.hasServedBeverage)))
        : shouldAttemptNormalCompletion(order, state, companionPreferences, now);
      const needsDelivery =
        (companionPreferences.autoNormalDeliverFood || forceKoishiFullFeedAutomation) &&
        !order.hasServedFood &&
        Boolean(state?.prepared || activeCookingJob);
      const requiresRecipeTarget =
        needsCooking || needsDelivery || activeCookingJob !== null || Boolean(state?.cookingJobId);
      const specialTargetSelection = getNormalAutomationTargetSelection(
        order,
        state,
        normalExecutionTargetsEnabled,
        normalAutomationTargetByKey,
        snapshot?.specialBusiness,
        snapshot?.nightBusinessGeneration ?? 0,
        requiresRecipeTarget,
        normalExecutionTargets,
      );
      const cookingDecision = buildNormalCookingTargetDecision(
        order,
        recommendationData,
        specialTargetSelection,
      );
      const specialBusinessCookingDeferral = cookingDecision.blockedReason;
      const targetBlockedCooking =
        Boolean(specialTargetSelection.policyError) ||
        (requiresRecipeTarget && Boolean(specialBusinessCookingDeferral));
      if (targetBlockedCooking) {
        schedulerMessages.push(`${cookingDecision.label}\n${specialBusinessCookingDeferral}`);
        const requestPreferences: CompanionPreferences = {
          ...companionPreferences,
          autoNormalTakeBeverage: false,
          autoNormalStartCooking: false,
          autoNormalDeliverFood: false,
          autoNormalCompleteOrder: false,
        };
        publishNormalAutomationDecisionDiagnostic({
          eventName: 'normal-target-blocked',
          reason: specialBusinessCookingDeferral,
          order,
          state,
          targetSelection: specialTargetSelection,
          requestPreferences,
          flags: {
            needsBeverage,
            needsCooking,
            needsCompletion,
            shouldHandleBeverage: false,
            shouldStartCooking: false,
            shouldCompleteOrder: false,
            completionConfigured: companionPreferences.autoNormalCompleteOrder,
            yuumaCompletionIntent: false,
            forceKoishiFullFeedAutomation,
            targetBlockedCooking,
          },
        });
        continue;
      }
      if (!needsBeverage && !needsCooking && !needsCompletion) continue;

      if (needsCooking) {
        const reservation = reserveAutomationCookerSlot(
          cookerCycle,
          cookingDecision.cooker,
          cookingDecision.label,
          cookerPool,
        );
        if (!reservation.ok) {
          schedulerMessages.push(`${cookingDecision.label}\n${reservation.message}`);
          continue;
        }
        const exactReservation = toCookerControllerReservation(reservation);
        if (exactReservation == null) {
          schedulerMessages.push(
            `${cookingDecision.label}\n等待厨具 ${cookingDecision.cooker?.label || '未知'}：本轮未取得明确的厨具预约。`,
          );
          continue;
        }
        cookerReservationByOrderKey.set(orderKey, exactReservation);
      }

      runnableOrders.push(order);
      if (runnableOrders.length >= companionPreferences.autoNormalConcurrency) break;
    }
    const pausedCount = orders.filter(
      (order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order))?.paused,
    ).length;
    if (runnableOrders.length === 0) {
      const waitingCount = orders.filter((order) => {
        const state = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order));
        return state?.prepared && !order.hasServedFood;
      }).length;
      const schedulerText = schedulerMessages.length > 0 ? `\n${schedulerMessages.join('\n\n')}` : '';
      publishNormalOrderMessage(
        waitingCount > 0 || pausedCount > 0
          ? `普客自动化\n当前没有需要新开锅的普客订单。\n等待制作或送达 ${waitingCount} 笔，暂停 ${pausedCount} 笔。${blockedText}${schedulerText}`
          : `普客自动化\n当前没有需要执行的新步骤。${blockedText}${schedulerText}`,
      );
      refreshNormalOrderDiagnostics(orders, now);
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    normalOrderBusyRef.current = true;
    lastAutoNormalOrderAtRef.current = now;
    publishNormalOrderBusy(true);
    let activeRequestOrder: NormalBusinessOrder | null = null;
    let activeRequestEventSequence = 0;
    let activeRequestStage: AutomationRequestStage = 'match-order';
    try {
      let updatedOrderDetailCount = 0;
      for (const order of runnableOrders) {
        activeRequestOrder = order;
        const orderKey = buildNormalAutoOrderKey(order);
        const storedState =
          normalOrderStatesRef.current.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, now);
        const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
          snapshot?.specialBusiness,
          order.specialBusinessRole,
        );
        const syncedState = retireDisabledNormalAutomationFailure(
          syncNormalOrderStateWithSnapshot(order, storedState, now, companionPreferences) ?? storedState,
          order,
          companionPreferences,
          now,
          forceKoishiFullFeedAutomation,
        );
        const activeCookingJob = findNormalAutomationCookingJob(
          snapshot?.automationCookingJobs ?? [],
          order,
          syncedState,
        );
        let currentState =
          activeCookingJob && !order.hasServedFood
            ? reconcileStateWithActiveCookingJob(syncedState, activeCookingJob, now)
            : syncedState;
        const rollbackTarget = buildSpecialFoodTargetWirePolicy(
          snapshot?.specialBusiness,
          order.specialBusinessRole,
          snapshot?.nightBusinessGeneration ?? 0,
        );
        currentState = reconcileAutomationRollbackTarget(
          currentState,
          rollbackTarget.specialTargetSignature,
          rollbackTarget.specialTargetRevision,
          now,
        ).state;
        normalOrderStatesRef.current.set(orderKey, currentState);
        if (currentState.paused || currentState.nextAttemptAtMs > now) continue;
        const wantsCooking =
          shouldAttemptNormalCooking(order, currentState, companionPreferences, now) ||
          (forceKoishiFullFeedAutomation &&
            !order.hasServedFood &&
            !currentState.prepared &&
            !currentState.foodDelivered);
        const shouldHandleBeverage =
          shouldAttemptNormalBeverage(order, currentState, companionPreferences, now) ||
          (forceKoishiFullFeedAutomation && !order.hasServedBeverage && !currentState.beverageHandled);
        const shouldStartCooking = wantsCooking;
        const shouldCompleteOrder = forceKoishiFullFeedAutomation
          ? !order.hasEvaluated &&
            (order.readyToEvaluate ||
              ((currentState.foodDelivered || order.hasServedFood) &&
                (currentState.beverageHandled || order.hasServedBeverage)))
          : shouldAttemptNormalCompletion(order, currentState, companionPreferences, now) ||
            (companionPreferences.autoNormalCompleteOrder &&
              !order.hasEvaluated &&
              !currentState.paused &&
              (order.readyToEvaluate || order.hasServedFood || currentState.foodDelivered) &&
              (order.hasServedBeverage || currentState.beverageHandled || shouldHandleBeverage));
        const shouldDeliverFood =
          (companionPreferences.autoNormalDeliverFood || forceKoishiFullFeedAutomation) &&
          currentState.prepared &&
          !order.hasServedFood;
        const requiresRecipeTarget =
          shouldStartCooking ||
          shouldDeliverFood ||
          activeCookingJob !== null ||
          Boolean(currentState.cookingJobId);
        const specialTargetSelection = getNormalAutomationTargetSelection(
          order,
          currentState,
          normalExecutionTargetsEnabled,
          normalAutomationTargetByKey,
          snapshot?.specialBusiness,
          snapshot?.nightBusinessGeneration ?? 0,
          requiresRecipeTarget,
          normalExecutionTargets,
        );
        const cookingDecision = buildNormalCookingTargetDecision(
          order,
          recommendationData,
          specialTargetSelection,
        );
        const targetBlockedCooking =
          Boolean(specialTargetSelection.policyError) ||
          (requiresRecipeTarget && Boolean(cookingDecision.blockedReason));
        const yuumaBossSettlement = specialTargetSelection.specialTargetPolicy.specialTargetOwner === 'yuuma';
        const yuumaCompletionIntent =
          !forceKoishiFullFeedAutomation &&
          yuumaBossSettlement &&
          companionPreferences.autoNormalDeliverFood &&
          companionPreferences.autoNormalCompleteOrder &&
          shouldStartCooking;
        if (targetBlockedCooking) {
          const requestPreferences: CompanionPreferences = {
            ...companionPreferences,
            autoNormalTakeBeverage: false,
            autoNormalStartCooking: false,
            autoNormalDeliverFood: false,
            autoNormalCompleteOrder: false,
          };
          publishNormalAutomationDecisionDiagnostic({
            eventName: 'normal-target-blocked',
            reason: cookingDecision.blockedReason,
            order,
            state: currentState,
            targetSelection: specialTargetSelection,
            requestPreferences,
            flags: {
              needsBeverage: shouldHandleBeverage,
              needsCooking: wantsCooking,
              needsCompletion: shouldCompleteOrder,
              shouldHandleBeverage: false,
              shouldStartCooking: false,
              shouldCompleteOrder: false,
              completionConfigured: companionPreferences.autoNormalCompleteOrder,
              yuumaCompletionIntent: false,
              forceKoishiFullFeedAutomation,
              targetBlockedCooking,
            },
          });
          continue;
        }

        const requestPreferences = buildNormalRequestPreferences(companionPreferences, {
          shouldHandleBeverage,
          shouldStartCooking,
          shouldCompleteOrder,
          forceKoishiFullFeedAutomation,
        });

        if (order.specialBusinessRole || specialTargetSelection.target || specialTargetSelection.message) {
          publishNormalAutomationDecisionDiagnostic({
            eventName: 'normal-request',
            reason: '普客自动化执行请求',
            order,
            state: currentState,
            targetSelection: specialTargetSelection,
            requestPreferences,
            flags: {
              needsBeverage: shouldHandleBeverage,
              needsCooking: wantsCooking,
              needsCompletion: shouldCompleteOrder,
              shouldHandleBeverage,
              shouldStartCooking,
              shouldCompleteOrder,
              completionConfigured: companionPreferences.autoNormalCompleteOrder,
              yuumaCompletionIntent,
              forceKoishiFullFeedAutomation,
              targetBlockedCooking,
            },
          });
        }

        if (
          !requestPreferences.autoNormalTakeBeverage &&
          !requestPreferences.autoNormalStartCooking &&
          !requestPreferences.autoNormalDeliverFood &&
          !requestPreferences.autoNormalCompleteOrder
        ) {
          continue;
        }

        if (requiresRecipeTarget && specialTargetSelection.target) {
          currentState = lockNormalOrderExecutionTarget(
            currentState,
            specialTargetSelection.target,
            snapshot?.nightBusinessGeneration ?? 0,
          );
          normalOrderStatesRef.current.set(orderKey, currentState);
        }
        activeRequestEventSequence = currentState.lastRuntimeEventSequence;
        activeRequestStage = selectAutomationRequestStage({
          needsBeverage: shouldHandleBeverage,
          needsCooking: shouldStartCooking,
          needsDelivery:
            requestPreferences.autoNormalDeliverFood && currentState.prepared && !order.hasServedFood,
          needsCompletion: shouldCompleteOrder,
          fallback:
            currentState.step === 'ensure-beverage' ||
            currentState.step === 'ensure-cooking' ||
            currentState.step === 'deliver-food' ||
            currentState.step === 'complete-order'
              ? currentState.step
              : 'match-order',
        });
        const response = await completeFirstNormalOrder(
          normalizedEndpoint,
          apiToken,
          order,
          specialTargetSelection.specialTargetPolicy,
          requestPreferences,
          requestPreferences.autoNormalStartCooking
            ? (cookerReservationByOrderKey.get(orderKey) ?? null)
            : null,
          companionDeviceAuthority.authorityRevision,
          recommendationData,
          specialTargetSelection.target,
        );
        const responseAt = Date.now();
        if (!isAutomationRequestCurrent(requestEpoch)) return;
        if (handleAutomationControlPlaneResponse(response)) return;
        const stateAfterRequest = normalOrderStatesRef.current.get(orderKey);
        if (
          !isAutomationRequestCurrent(
            requestEpoch,
            activeRequestEventSequence,
            stateAfterRequest?.lastRuntimeEventSequence ?? 0,
          )
        ) {
          continue;
        }
        const responseStage = resolveAutomationResponseStage(response.automation.stage, activeRequestStage);
        const transientFailure = !response.ok && isTransientAutoPreparationFailure(response);
        const cookingMismatchStored = didCookingMismatchStored(response);
        const pendingCooking = didNormalOrderCookingStillPending(response);
        const startedCooking = didCompleteStepCode(response, 'cooking-started');
        const acknowledgedStart =
          !cookingMismatchStored &&
          (startedCooking ||
            pendingCooking ||
            (responseStage === 'ensure-cooking' && response.automation.outcome === 'progressed'));
        const beverageHandledNow = didNormalOrderDeliverBeverage(response);
        const foodDeliveredNow = didNormalOrderDeliverFood(response);
        const completedNow = didNormalOrderComplete(response);
        const cookingInterrupted =
          response.automation.outcome === 'interrupted' ||
          response.automation.outcome === 'blocked' ||
          response.automation.outcome === 'fatal';
        const manualCookingResolution =
          requiresManualAutomationResolution(
            response.automation.reasonCode,
            response.steps.map((step) => step.code ?? ''),
          ) &&
          (responseStage === 'ensure-cooking' || responseStage === 'deliver-food');
        const prepared =
          manualCookingResolution ||
          (!(cookingMismatchStored || cookingInterrupted) && (currentState.prepared || acknowledgedStart));
        const beverageHandled = currentState.beverageHandled || order.hasServedBeverage || beverageHandledNow;
        const foodDelivered = cookingMismatchStored
          ? false
          : currentState.foodDelivered || order.hasServedFood || foodDeliveredNow;
        const completed = cookingMismatchStored
          ? false
          : currentState.completed || order.hasEvaluated || completedNow;
        const rollbackCount = currentState.rollbackCount;
        const responseState = cookingMismatchStored
          ? clearNormalOrderExecutionTarget(currentState)
          : currentState;
        let nextStep: AutomationStep = 'ensure-cooking';
        if (completed) {
          nextStep = 'done';
        } else if (!cookingMismatchStored && requestPreferences.autoNormalTakeBeverage && !beverageHandled) {
          nextStep = 'ensure-beverage';
        } else if (!cookingMismatchStored && (foodDelivered || order.readyToEvaluate)) {
          nextStep = 'complete-order';
        } else if (!cookingMismatchStored && prepared && !foodDelivered) {
          nextStep = 'deliver-food';
        }
        const nextState = enforceAutomationRollbackLimit(
          updateAutomationAfterResponse(
            {
              ...responseState,
              orderKey,
              prepared,
              cookingJobId: prepared ? response.automation.jobId || currentState.cookingJobId : '',
              beverageHandled,
              beverageHandledAtMs:
                beverageHandledNow && !currentState.beverageHandled
                  ? responseAt
                  : currentState.beverageHandledAtMs,
              foodDelivered,
              foodDeliveredAtMs: cookingMismatchStored
                ? 0
                : foodDeliveredNow && !currentState.foodDelivered
                  ? responseAt
                  : currentState.foodDeliveredAtMs,
              completed,
              completedAtMs: cookingMismatchStored
                ? 0
                : completedNow && !currentState.completed
                  ? responseAt
                  : currentState.completedAtMs,
              rollbackCount,
            },
            response,
            responseAt,
            response.automation.outcome === 'retryable-failure' ||
              response.automation.outcome === 'interrupted' ||
              response.automation.outcome === 'blocked' ||
              response.automation.outcome === 'fatal'
              ? activeRequestStage
              : nextStep,
            companionPreferences.autoNormalStopOnError,
            companionPreferences.autoMaxStepRetries,
          ),
          companionPreferences.autoMaxRollbacks,
          responseAt,
        );
        const stateWithSnapshotFacts = {
          ...nextState,
          beverageHandled,
          foodDelivered,
          completed,
        };
        const normalizedNextState = completed
          ? clearNormalOrderExecutionTarget(stateWithSnapshotFacts)
          : stateWithSnapshotFacts;

        const suffix = normalizedNextState.paused
          ? normalizedNextState.manualResolutionRequired
            ? '游戏操作结果无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
            : '普客自动化已暂停该订单，订单状态变化或手动重试后会继续。'
          : transientFailure
            ? '当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        normalOrderStatesRef.current.set(
          orderKey,
          withAutomationDetail(
            normalizedNextState,
            responseAt,
            formatOrderPreparationResponse(response),
            formatAutomationState(normalizedNextState, companionPreferences),
            suffix,
          ),
        );
        updatedOrderDetailCount += 1;
      }
      refreshNormalOrderDiagnostics(orders, Date.now());
      publishNormalOrderMessage(
        updatedOrderDetailCount > 0 || schedulerMessages.length > 0
          ? `普客自动化\n${[
              updatedOrderDetailCount > 0
                ? `已更新 ${updatedOrderDetailCount} 笔订单详情，可展开对应订单查看。`
                : '',
              ...schedulerMessages,
            ]
              .filter(Boolean)
              .join('\n\n')}`
          : '普客自动化\n当前没有需要执行的新步骤。',
      );
      scheduleAutomationRefresh();
    } catch (err) {
      const failureAt = Date.now();
      const message = err instanceof Error ? err.message : String(err);
      if (!isAutomationRequestCurrent(requestEpoch)) return;
      let pausedCount = 0;
      const failedOrders = activeRequestOrder ? [activeRequestOrder] : [];
      for (const order of failedOrders) {
        const orderKey = buildNormalAutoOrderKey(order);
        const state =
          normalOrderStatesRef.current.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, failureAt);
        if (
          !isAutomationRequestCurrent(
            requestEpoch,
            activeRequestEventSequence,
            state.lastRuntimeEventSequence,
          )
        ) {
          return;
        }
        const failedState = recordAutomationTransportFailure(
          state,
          failureAt,
          message,
          activeRequestStage,
          companionPreferences.autoNormalStopOnError,
          companionPreferences.autoMaxStepRetries,
        );
        if (failedState.paused) pausedCount += 1;
        normalOrderStatesRef.current.set(
          orderKey,
          withAutomationDetail(
            failedState,
            failureAt,
            message,
            failedState.paused
              ? '本阶段网络请求达到重试上限，当前订单已暂停。'
              : '本阶段网络请求失败，将按下一轮调度重试。',
          ),
        );
      }
      refreshNormalOrderDiagnostics(orders, failureAt);
      publishNormalOrderPausedCount(
        orders.filter((order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order))?.paused)
          .length,
      );
      publishNormalOrderMessage(
        `普客自动化\n${message}\n${pausedCount > 0 ? `达到重试上限并暂停 ${pausedCount} 笔订单。` : '请求失败，将自动重试。'}`,
      );
    } finally {
      normalOrderBusyRef.current = false;
      publishNormalOrderBusy(false);
    }
  }, [
    automationRuntimeEnabledRef,
    companionPreferences,
    rareParticipationMutationBusyRef,
    normalOrderBusyRef,
    automationRequestEpochRef,
    lastAutoNormalOrderAtRef,
    apiToken,
    snapshot?.normalBusiness?.orders,
    snapshot?.automationCookingJobs,
    snapshot?.specialBusiness,
    snapshot?.nightBusinessGeneration,
    refreshNormalOrderDiagnostics,
    getAutomationCookerCycle,
    runtime,
    publishNormalOrderBusy,
    normalOrderStatesRef,
    publishNormalOrderMessage,
    normalExecutionTargetsEnabled,
    normalAutomationTargetByKey,
    normalExecutionTargets,
    recommendationData,
    publishAutomationTargetRotationDiagnostic,
    publishNormalAutomationDecisionDiagnostic,
    scheduleAutomationRefresh,
    normalizedEndpoint,
    companionDeviceAuthority.authorityRevision,
    isAutomationRequestCurrent,
    handleAutomationControlPlaneResponse,
    publishNormalOrderPausedCount,
  ]);

  const handleAutomationDisabled = useCallback(() => {
    retainRareAutomationExecutionStates(rareOrderStatesRef.current);
    retainRareAutomationExecutionDiagnosticItems(
      rareOrderStatesRef.current,
      rareOrderDiagnosticItemsRef.current,
    );
    retainNormalAutomationExecutionStates(normalOrderStatesRef.current);
    refreshRareOrderDiagnostics();
    refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? []);
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    publishAutoPrepBusy(false);
    publishNormalOrderBusy(false);
  }, [
    lastAutoFirstOrderAtRef,
    lastAutoNormalOrderAtRef,
    normalOrderStatesRef,
    publishAutoPrepBusy,
    publishNormalOrderBusy,
    rareOrderDiagnosticItemsRef,
    rareOrderStatesRef,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    snapshot?.normalBusiness?.orders,
  ]);

  const handleNormalOrderSignatureChanged = useCallback(() => {
    lastAutoNormalOrderAtRef.current = 0;
  }, [lastAutoNormalOrderAtRef]);

  const handleRareAutomationDisabled = useCallback(() => {
    retainRareAutomationExecutionStates(rareOrderStatesRef.current);
    retainRareAutomationExecutionDiagnosticItems(
      rareOrderStatesRef.current,
      rareOrderDiagnosticItemsRef.current,
    );
    refreshRareOrderDiagnostics();
    lastAutoFirstOrderAtRef.current = 0;
    publishAutoPrepBusy(false);
    publishAutoPrepMessage('');
  }, [
    lastAutoFirstOrderAtRef,
    publishAutoPrepBusy,
    publishAutoPrepMessage,
    rareOrderDiagnosticItemsRef,
    rareOrderStatesRef,
    refreshRareOrderDiagnostics,
  ]);

  const handleNormalAutomationDisabled = useCallback(() => {
    retainNormalAutomationExecutionStates(normalOrderStatesRef.current);
    refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? []);
    lastAutoNormalOrderAtRef.current = 0;
    publishNormalOrderBusy(false);
    publishNormalOrderMessage('');
  }, [
    lastAutoNormalOrderAtRef,
    normalOrderStatesRef,
    publishNormalOrderBusy,
    publishNormalOrderMessage,
    refreshNormalOrderDiagnostics,
    snapshot?.normalBusiness?.orders,
  ]);

  useOrderAutomationIntervals({
    automationEnabled: automationRuntimeEnabled,
    resetStateWhenDisabled: !companionPreferences.automationEnabled,
    autoRareOrderEnabled: companionPreferences.autoRareOrderEnabled,
    resetRareStateWhenDisabled:
      !companionPreferences.automationEnabled || !companionPreferences.autoRareOrderEnabled,
    autoNormalOrderEnabled: companionPreferences.autoNormalOrderEnabled,
    resetNormalStateWhenDisabled:
      !companionPreferences.automationEnabled || !companionPreferences.autoNormalOrderEnabled,
    normalOrderSignature,
    rareTickMs: AUTO_FIRST_ORDER_TICK_MS,
    normalTickMs: AUTO_NORMAL_ORDER_TICK_MS,
    runAutoFirstOrder,
    runAutoNormalOrder,
    onAutomationDisabled: handleAutomationDisabled,
    onRareAutomationDisabled: handleRareAutomationDisabled,
    onNormalOrderSignatureChanged: handleNormalOrderSignatureChanged,
    onNormalAutomationDisabled: handleNormalAutomationDisabled,
  });
}
