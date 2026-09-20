import { acknowledgeAutomationSafetyBarrier, appendAutomationDecisionDiagnostic } from '@/companion/api';
import {
  canAdvanceAutomationRuntimeEventSequence,
  isRecoverableCookingTerminalEvent,
  reduceAutomationCookingRollbackBudget,
  reduceAutomationManualRetry,
  shouldRetireMissingManualBarrier,
} from '@/companion/automation-machine';
import { emptyAutoFirstOrderState, emptyNormalAutoOrderState } from '@/companion/automation-state';
import {
  buildAutoOrderKey,
  buildNormalAutoOrderDiagnostics,
  buildNormalAutoOrderKey,
  buildNormalLifecycleAutoOrderKey,
  buildRareAutoOrderDiagnostic,
  type OperationalOrderRecommendation,
  type OrderPreparationCandidateResult,
} from '@/companion/domain/automation';
import {
  buildWackyRejectedRecipeKeyFromEvent,
  isWackyTargetTagMismatchEvent,
  WACKY_CHALLENGE_TYPE,
} from '@/companion/domain/special-business';
import { formatDesk } from '@/companion/formatters';
import { useAutomationSchedulers } from '@/companion/hooks/useAutomationSchedulers';
import { useOrderRecommendations } from '@/companion/hooks/useOrderRecommendations';
import { useRareOrderParticipation } from '@/companion/hooks/useRareOrderParticipation';
import { type CompanionPreferences } from '@/companion/preferences';
import type {
  AutomationRuntimeEvent,
  AutomationSafetyBarrierAckResponse,
  AutomationSafetyBarrierDiagnostic,
  FavoriteData,
  RecommendationStateSnapshot,
} from '@/companion/types';
import { type RecommendationDataSet } from '@/lib/recommendation-data';
import { useCallback, useEffect, useMemo } from 'react';

import {
  buildAutomationDecisionDiagnosticSignature,
  buildAutomationDecisionOrderLine,
  buildAutomationDecisionSelectionLine,
  buildAutomationDecisionSkipLine,
  buildNormalAutomationDecisionOrderLine,
  compactDiagnosticText,
  formatNormalAutomationTarget,
  hasPrimaryRecommendationMismatch,
  rememberBoundedDiagnosticSignature,
  type NormalAutomationDecisionDiagnosticInput,
} from '@/companion/domain/automation-diagnostics';
import {
  automationBarrierAckFailure,
  clearAutomationCookingJobControlDetail,
  enforceAutomationRollbackLimit,
  findNormalAutomationCookingJob,
  findRareAutomationCookingJob,
  isBlockingCookingTerminalEvent,
  isCookingTagsUnreadableStoredEvent,
  isManualResolutionAutomationEvent,
  matchesNormalAutomationEvent,
  matchesRareAutomationEvent,
  pauseNormalOrderStateAfterRuntimeFailure,
  pauseRareOrderStateAfterRuntimeFailure,
  reconcileStateWithActiveCookingJob,
  resetNormalOrderStateAfterRuntimeMismatch,
  resetRareOrderStateAfterRuntimeMismatch,
  withAutomationDetail,
} from '@/companion/domain/automation-lifecycle';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';
import type { LocalApiSnapshot } from '@/companion/types';

import type { AutomationControl } from '@/companion/hooks/useAutomationControl';
import type { AutomationBarrierAckEntry, AutomationState } from '@/companion/hooks/useAutomationState';
export interface OrderAutomationOptions {
  state: AutomationState;
  control: AutomationControl;
  automationUiVisible: boolean;
  apiToken: string;
  normalizedEndpoint: string;
  companionPreferences: CompanionPreferences;
  companionDeviceAuthority: CompanionDeviceAuthorityController;
  snapshot: LocalApiSnapshot | null;
  runtime: RecommendationStateSnapshot | null;
  recommendationData: RecommendationDataSet;
  recommendationDataSignature: string;
  favorites: FavoriteData;
  orderRecommendations: ReturnType<typeof useOrderRecommendations>;
  operationalOrderRecommendations: readonly OperationalOrderRecommendation[] | null;
  normalExecutionTargets: ReturnType<typeof useOrderRecommendations>;
  normalExecutionTargetsEnabled: boolean;
  rareOrderParticipation: ReturnType<typeof useRareOrderParticipation>;
}
/** Persistent execution and event controller. Its lifetime is independent of page selection. */
export function useOrderAutomation(options: OrderAutomationOptions) {
  const {
    state,
    control,
    automationUiVisible,
    apiToken,
    normalizedEndpoint,
    companionPreferences,
    companionDeviceAuthority,
    snapshot,
    orderRecommendations,
    rareOrderParticipation,
  } = options;
  const {
    automationLease,
    setAutomationLease,
    setAutomationLeaseBindingKey,
    automationLeaseError,
    setAutomationBarrierAckBusyKey,
    automationBarrierAckErrors,
    setAutomationBarrierAckErrors,
    setSpecialBusinessRejectedRecipeKeys,
    specialBusinessRejectedRecipeKeysRef,
    rareOrderStatesRef,
    rareOrderDiagnosticItemsRef,
    normalOrderStatesRef,
    lastAutoFirstOrderAtRef,
    lastAutoNormalOrderAtRef,
    rareAutomationDecisionDiagnosticSignaturesRef,
    normalAutomationDecisionDiagnosticSignaturesRef,
    automationRequestEpochRef,
    rareParticipationMutationBusyRef,
    automationStateSessionIdRef,
    automationLeaseOwnedRef,
    automationBarrierAckRef,
    automationRuntimeEnabledRef,
    publishAutoPrepMessage,
    publishAutoPrepPaused,
    publishRareOrderDiagnostics,
    publishNormalOrderMessage,
    publishNormalOrderPausedCount,
    publishNormalOrderDiagnostics,
    scheduleAutomationRefresh,
    rememberSpecialBusinessRejectedRecipeKey,
  } = state;
  const {
    connectionReadyForActions,
    automationSessionId,
    automationLeaseOwned,
    automationConnectionScope,
    automationConnectionScopeRef,
  } = control;
  useEffect(() => {
    setAutomationBarrierAckErrors({});
  }, [automationConnectionScope, setAutomationBarrierAckErrors]);
  useEffect(() => {
    rareParticipationMutationBusyRef.current = rareOrderParticipation.busyMutationKey !== null;
  }, [rareOrderParticipation.busyMutationKey, rareParticipationMutationBusyRef]);
  const automationSafetyBarriers = useMemo<AutomationSafetyBarrierDiagnostic[]>(() => {
    const latestByTarget = new Map<string, AutomationRuntimeEvent>();
    for (const event of snapshot?.automationEvents ?? []) {
      if (!isManualResolutionAutomationEvent(event)) continue;
      if (
        event.orderLifecycleSequence <= 0 ||
        !event.orderRuntimeKind ||
        !event.orderId ||
        !event.orderControllerId
      )
        continue;
      const targetIdentity = [
        event.targetKind,
        event.orderRuntimeKind,
        event.orderId,
        event.orderControllerId,
        event.orderLifecycleSequence,
      ].join(':');
      const current = latestByTarget.get(targetIdentity);
      if (!current || current.sequence < event.sequence) latestByTarget.set(targetIdentity, event);
    }

    return [...latestByTarget.values()]
      .sort((left, right) => right.sequence - left.sequence)
      .map((event) => ({
        sequence: event.sequence,
        targetKind: event.targetKind,
        title: `${event.targetKind === 'normal' ? '普客' : '稀客'} · ${event.guestName || '未知客人'}${event.deskCode >= 0 ? ` · 桌 ${formatDesk(event.deskCode)}` : ''}`,
        code: event.reasonCode || event.code,
        message: event.message || 'Mod 无法确认自动化操作的结果，请检查游戏当前状态。',
        error: automationBarrierAckErrors[event.sequence] ?? '',
      }));
  }, [automationBarrierAckErrors, snapshot?.automationEvents]);
  const specialBusinessFoodTargetSignature = useMemo(
    () => snapshot?.specialBusiness?.foodTargetTags.join('|') ?? '',
    [snapshot?.specialBusiness?.foodTargetTags],
  );
  const refreshRareOrderDiagnostics = useCallback(
    (now = Date.now()) => {
      const diagnostics = Array.from(rareOrderDiagnosticItemsRef.current.values()).map((selection) => {
        const orderKey = buildAutoOrderKey(selection.item);
        const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
        return buildRareAutoOrderDiagnostic(selection, state, now);
      });
      publishRareOrderDiagnostics(diagnostics);
      publishAutoPrepPaused(diagnostics.some((diagnostic) => diagnostic.paused));
    },
    [publishAutoPrepPaused, publishRareOrderDiagnostics, rareOrderDiagnosticItemsRef, rareOrderStatesRef],
  );

  const refreshNormalOrderDiagnostics = useCallback(
    (orders = snapshot?.normalBusiness?.orders ?? [], now = Date.now()) => {
      const diagnostics = buildNormalAutoOrderDiagnostics(orders, normalOrderStatesRef.current, now);
      publishNormalOrderDiagnostics(diagnostics);
      publishNormalOrderPausedCount(diagnostics.filter((diagnostic) => diagnostic.paused).length);
    },
    [
      normalOrderStatesRef,
      publishNormalOrderDiagnostics,
      publishNormalOrderPausedCount,
      snapshot?.normalBusiness?.orders,
    ],
  );

  useEffect(() => {
    const jobs = snapshot?.automationCookingJobs ?? [];
    const now = Date.now();
    for (const selection of rareOrderDiagnosticItemsRef.current.values()) {
      const orderKey = buildAutoOrderKey(selection.item);
      const state = rareOrderStatesRef.current.get(orderKey);
      if (!state) continue;
      const job = findRareAutomationCookingJob(jobs, selection, state);
      rareOrderStatesRef.current.set(
        orderKey,
        job
          ? reconcileStateWithActiveCookingJob(state, job, now)
          : clearAutomationCookingJobControlDetail(state, now),
      );
    }
    for (const order of snapshot?.normalBusiness?.orders ?? []) {
      const orderKey = buildNormalAutoOrderKey(order);
      const state = normalOrderStatesRef.current.get(orderKey);
      if (!state) continue;
      const job = findNormalAutomationCookingJob(jobs, order, state);
      normalOrderStatesRef.current.set(
        orderKey,
        job
          ? reconcileStateWithActiveCookingJob(state, job, now)
          : clearAutomationCookingJobControlDetail(state, now),
      );
    }
    refreshRareOrderDiagnostics(now);
    refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], now);
  }, [
    normalOrderStatesRef,
    rareOrderDiagnosticItemsRef,
    rareOrderStatesRef,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    snapshot?.automationCookingJobs,
    snapshot?.normalBusiness?.orders,
  ]);

  useEffect(() => {
    if (!automationUiVisible) return;
    const refreshDiagnosticClock = () => {
      const diagnosticNow = Date.now();
      refreshRareOrderDiagnostics(diagnosticNow);
      refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], diagnosticNow);
    };
    refreshDiagnosticClock();
    const timer = window.setInterval(refreshDiagnosticClock, 1000);
    return () => window.clearInterval(timer);
  }, [
    automationUiVisible,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    snapshot?.normalBusiness?.orders,
  ]);

  useEffect(() => {
    if (!automationSessionId) return;
    const previousSessionId = automationStateSessionIdRef.current;
    automationStateSessionIdRef.current = automationSessionId;
    if (previousSessionId !== automationSessionId) {
      rareAutomationDecisionDiagnosticSignaturesRef.current.clear();
      normalAutomationDecisionDiagnosticSignaturesRef.current.clear();
    }
    if (!previousSessionId || previousSessionId === automationSessionId) return;

    automationRequestEpochRef.current += 1;
    automationRuntimeEnabledRef.current = false;
    rareOrderStatesRef.current.clear();
    rareOrderDiagnosticItemsRef.current.clear();
    normalOrderStatesRef.current.clear();
    setAutomationBarrierAckErrors({});
    setAutomationLease(null);
    setAutomationLeaseBindingKey('');
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    refreshRareOrderDiagnostics();
    refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? []);
  }, [
    automationBarrierAckRef,
    automationRequestEpochRef,
    automationRuntimeEnabledRef,
    automationSessionId,
    automationStateSessionIdRef,
    lastAutoFirstOrderAtRef,
    lastAutoNormalOrderAtRef,
    normalAutomationDecisionDiagnosticSignaturesRef,
    normalOrderStatesRef,
    rareAutomationDecisionDiagnosticSignaturesRef,
    rareOrderDiagnosticItemsRef,
    rareOrderStatesRef,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    setAutomationBarrierAckBusyKey,
    setAutomationBarrierAckErrors,
    setAutomationLease,
    setAutomationLeaseBindingKey,
    snapshot?.normalBusiness?.orders,
  ]);

  const publishRareAutomationDecisionDiagnostic = useCallback(
    (
      eventName: string,
      candidateResult: OrderPreparationCandidateResult,
      message: string,
      selectionPreferences: CompanionPreferences,
    ) => {
      if (!connectionReadyForActions || !apiToken) return;

      const specialBusiness = snapshot?.specialBusiness ?? null;
      const primaryTargetMismatch = hasPrimaryRecommendationMismatch(orderRecommendations.recommendations);
      if (
        !specialBusiness?.active &&
        candidateResult.skips.length === 0 &&
        candidateResult.selections.length > 0 &&
        !primaryTargetMismatch
      )
        return;

      const diagnosticEventName = primaryTargetMismatch ? 'rare-primary-target-mismatch' : eventName;
      const diagnosticMessage = primaryTargetMismatch
        ? `页面首项与唯一主执行计划不一致；${message || candidateResult.message}`
        : message;

      const orderLines = orderRecommendations.recommendations
        .slice(0, 8)
        .map(buildAutomationDecisionOrderLine);
      const selectionLines = candidateResult.selections.slice(0, 8).map(buildAutomationDecisionSelectionLine);
      const skipLines = candidateResult.skips.slice(0, 8).map(buildAutomationDecisionSkipLine);
      const specialBusinessRole =
        orderRecommendations.recommendations.find((item) => item.order.specialBusinessRole)?.order
          .specialBusinessRole ?? '';
      const normalizedMessage = compactDiagnosticText(diagnosticMessage || candidateResult.message);
      const signature = buildAutomationDecisionDiagnosticSignature(
        diagnosticEventName,
        normalizedMessage,
        specialBusiness,
        orderLines,
        selectionLines,
        skipLines,
        selectionPreferences,
        automationLeaseOwned,
      );
      if (
        !rememberBoundedDiagnosticSignature(rareAutomationDecisionDiagnosticSignaturesRef.current, signature)
      )
        return;

      void appendAutomationDecisionDiagnostic(normalizedEndpoint, apiToken, {
        signature,
        eventName: diagnosticEventName,
        message: normalizedMessage,
        scene: snapshot?.activeSceneName ?? '',
        challengeType: specialBusiness?.challengeType ?? '',
        phase: specialBusiness?.phase ?? '',
        specialBusinessRole,
        orderCount: orderRecommendations.recommendations.length,
        selectionCount: candidateResult.selections.length,
        skipCount: candidateResult.skips.length,
        automationEnabled: selectionPreferences.automationEnabled,
        leaseOwned: automationLeaseOwned,
        autoCompleteOrder: selectionPreferences.autoPrepCompleteOrder,
        autoTakeBeverage: selectionPreferences.autoPrepTakeBeverage,
        autoStartCooking: selectionPreferences.autoPrepStartCooking,
        autoCollectCooking: selectionPreferences.autoPrepCollectCooking,
        recipeFavoritesOnly: selectionPreferences.autoPrepRecipeFavoritesOnly,
        beverageFavoritesOnly: selectionPreferences.autoPrepBeverageFavoritesOnly,
        rareConcurrency: selectionPreferences.autoRareConcurrency,
        leaseMessage: automationLeaseError || automationLease?.error || '',
        orderLines,
        selectionLines,
        skipLines,
      }).catch(() => {
        rareAutomationDecisionDiagnosticSignaturesRef.current.delete(signature);
      });
    },
    [
      apiToken,
      automationLease?.error,
      automationLeaseError,
      automationLeaseOwned,
      connectionReadyForActions,
      normalizedEndpoint,
      orderRecommendations.recommendations,
      rareAutomationDecisionDiagnosticSignaturesRef,
      snapshot?.activeSceneName,
      snapshot?.specialBusiness,
    ],
  );

  const publishNormalAutomationDecisionDiagnostic = useCallback(
    (input: NormalAutomationDecisionDiagnosticInput) => {
      if (!connectionReadyForActions || !apiToken) return;

      const specialBusiness = snapshot?.specialBusiness ?? null;
      const orderLine = buildNormalAutomationDecisionOrderLine(input);
      const normalizedMessage = compactDiagnosticText(input.reason || input.targetSelection.message);
      const signature = buildAutomationDecisionDiagnosticSignature(
        input.eventName,
        normalizedMessage,
        specialBusiness,
        [orderLine],
        [],
        [],
        input.requestPreferences,
        automationLeaseOwned,
      );
      if (
        !rememberBoundedDiagnosticSignature(
          normalAutomationDecisionDiagnosticSignaturesRef.current,
          signature,
        )
      )
        return;

      void appendAutomationDecisionDiagnostic(normalizedEndpoint, apiToken, {
        signature,
        eventName: input.eventName,
        message: normalizedMessage,
        scene: snapshot?.activeSceneName ?? '',
        challengeType: specialBusiness?.challengeType ?? '',
        phase: specialBusiness?.phase ?? '',
        specialBusinessRole: input.order.specialBusinessRole ?? '',
        orderCount: 1,
        selectionCount: input.targetSelection.target ? 1 : 0,
        skipCount: input.targetSelection.message ? 1 : 0,
        automationEnabled: input.requestPreferences.automationEnabled,
        leaseOwned: automationLeaseOwned,
        autoCompleteOrder: input.requestPreferences.autoNormalCompleteOrder,
        autoTakeBeverage: input.requestPreferences.autoNormalTakeBeverage,
        autoStartCooking: input.requestPreferences.autoNormalStartCooking,
        autoCollectCooking: input.requestPreferences.autoNormalDeliverFood,
        recipeFavoritesOnly: input.requestPreferences.autoPrepRecipeFavoritesOnly,
        beverageFavoritesOnly: input.requestPreferences.autoPrepBeverageFavoritesOnly,
        rareConcurrency: input.requestPreferences.autoRareConcurrency,
        leaseMessage: automationLeaseError || automationLease?.error || '',
        orderLines: [orderLine],
        selectionLines: input.targetSelection.target
          ? [formatNormalAutomationTarget(input.targetSelection.target)]
          : [],
        skipLines: input.targetSelection.message
          ? [compactDiagnosticText(input.targetSelection.message)]
          : [],
      }).catch(() => {
        normalAutomationDecisionDiagnosticSignaturesRef.current.delete(signature);
      });
    },
    [
      apiToken,
      automationLease?.error,
      automationLeaseError,
      automationLeaseOwned,
      connectionReadyForActions,
      normalAutomationDecisionDiagnosticSignaturesRef,
      normalizedEndpoint,
      snapshot?.activeSceneName,
      snapshot?.specialBusiness,
    ],
  );

  const publishAutomationRollbackDiagnostic = useCallback(
    (
      event: AutomationRuntimeEvent,
      previousRollbackCount: number,
      nextRollbackCount: number,
      specialBusinessRole: string,
    ) => {
      const rollback = reduceAutomationCookingRollbackBudget(previousRollbackCount, event);
      if (rollback.action === 'deferred') return;
      if (!connectionReadyForActions || !apiToken) return;
      const eventName = 'automation-rollback-budget-consumed';
      const message = `同一料理目标发生可恢复中断，重新制作计数 ${previousRollbackCount} → ${nextRollbackCount}。`;
      const specialBusiness = snapshot?.specialBusiness ?? null;
      const orderLine = [
        `sequence=${event.sequence}`,
        `targetKind=${event.targetKind}`,
        `trace=${event.traceId ?? ''}`,
        `orderKey=${event.orderKey ?? ''}`,
        `job=${event.jobId}`,
        `desk=${event.deskCode}`,
        `guestId=${event.guestId ?? ''}`,
        `code=${event.code}`,
        `reason=${event.reasonCode}`,
        `rollback=${previousRollbackCount}->${nextRollbackCount}`,
        'action=consumed',
      ].join('; ');
      const signature = buildAutomationDecisionDiagnosticSignature(
        eventName,
        message,
        specialBusiness,
        [orderLine],
        [],
        [],
        companionPreferences,
        automationLeaseOwned,
      );
      const signatures =
        event.targetKind === 'rare'
          ? rareAutomationDecisionDiagnosticSignaturesRef.current
          : normalAutomationDecisionDiagnosticSignaturesRef.current;
      if (!rememberBoundedDiagnosticSignature(signatures, signature)) return;

      void appendAutomationDecisionDiagnostic(normalizedEndpoint, apiToken, {
        signature,
        eventName,
        message,
        scene: snapshot?.activeSceneName ?? '',
        challengeType: specialBusiness?.challengeType ?? '',
        phase: specialBusiness?.phase ?? '',
        specialBusinessRole,
        orderCount: 1,
        selectionCount: 0,
        skipCount: 0,
        automationEnabled: companionPreferences.automationEnabled,
        leaseOwned: automationLeaseOwned,
        autoCompleteOrder:
          event.targetKind === 'rare'
            ? companionPreferences.autoPrepCompleteOrder
            : companionPreferences.autoNormalCompleteOrder,
        autoTakeBeverage:
          event.targetKind === 'rare'
            ? companionPreferences.autoPrepTakeBeverage
            : companionPreferences.autoNormalTakeBeverage,
        autoStartCooking:
          event.targetKind === 'rare'
            ? companionPreferences.autoPrepStartCooking
            : companionPreferences.autoNormalStartCooking,
        autoCollectCooking:
          event.targetKind === 'rare'
            ? companionPreferences.autoPrepCollectCooking
            : companionPreferences.autoNormalDeliverFood,
        recipeFavoritesOnly: companionPreferences.autoPrepRecipeFavoritesOnly,
        beverageFavoritesOnly: companionPreferences.autoPrepBeverageFavoritesOnly,
        rareConcurrency: companionPreferences.autoRareConcurrency,
        leaseMessage: automationLeaseError || automationLease?.error || '',
        orderLines: [orderLine],
        selectionLines: [],
        skipLines: [],
      }).catch(() => {
        signatures.delete(signature);
      });
    },
    [
      apiToken,
      automationLease?.error,
      automationLeaseError,
      automationLeaseOwned,
      companionPreferences,
      connectionReadyForActions,
      normalAutomationDecisionDiagnosticSignaturesRef,
      normalizedEndpoint,
      rareAutomationDecisionDiagnosticSignaturesRef,
      snapshot?.activeSceneName,
      snapshot?.specialBusiness,
    ],
  );

  const publishAutomationTargetRotationDiagnostic = useCallback(
    (input: {
      targetKind: 'rare' | 'normal';
      orderIdentity: string;
      previousSignature: string;
      previousRevision: number;
      nextSignature: string;
      nextRevision: number;
      previousRollbackCount: number;
      specialBusinessRole: string;
    }) => {
      if (!connectionReadyForActions || !apiToken) return;
      const eventName = 'automation-rollback-budget-retired';
      const message = `特殊经营料理目标已经变化，旧目标的重新制作计数为 ${input.previousRollbackCount}，新目标从 0 开始。`;
      const specialBusiness = snapshot?.specialBusiness ?? null;
      const orderLine = [
        `targetKind=${input.targetKind}`,
        `order=${input.orderIdentity}`,
        `previousTarget=${input.previousSignature}; previousRevision=${input.previousRevision}`,
        `nextTarget=${input.nextSignature}; nextRevision=${input.nextRevision}`,
        `rollback=${input.previousRollbackCount}->0`,
        'source=target-signature-reconciliation',
      ].join('; ');
      const signature = buildAutomationDecisionDiagnosticSignature(
        eventName,
        message,
        specialBusiness,
        [orderLine],
        [],
        [],
        companionPreferences,
        automationLeaseOwned,
      );
      const signatures =
        input.targetKind === 'rare'
          ? rareAutomationDecisionDiagnosticSignaturesRef.current
          : normalAutomationDecisionDiagnosticSignaturesRef.current;
      if (!rememberBoundedDiagnosticSignature(signatures, signature)) return;

      void appendAutomationDecisionDiagnostic(normalizedEndpoint, apiToken, {
        signature,
        eventName,
        message,
        scene: snapshot?.activeSceneName ?? '',
        challengeType: specialBusiness?.challengeType ?? '',
        phase: specialBusiness?.phase ?? '',
        specialBusinessRole: input.specialBusinessRole,
        orderCount: 1,
        selectionCount: 0,
        skipCount: 0,
        automationEnabled: companionPreferences.automationEnabled,
        leaseOwned: automationLeaseOwned,
        autoCompleteOrder:
          input.targetKind === 'rare'
            ? companionPreferences.autoPrepCompleteOrder
            : companionPreferences.autoNormalCompleteOrder,
        autoTakeBeverage:
          input.targetKind === 'rare'
            ? companionPreferences.autoPrepTakeBeverage
            : companionPreferences.autoNormalTakeBeverage,
        autoStartCooking:
          input.targetKind === 'rare'
            ? companionPreferences.autoPrepStartCooking
            : companionPreferences.autoNormalStartCooking,
        autoCollectCooking:
          input.targetKind === 'rare'
            ? companionPreferences.autoPrepCollectCooking
            : companionPreferences.autoNormalDeliverFood,
        recipeFavoritesOnly: companionPreferences.autoPrepRecipeFavoritesOnly,
        beverageFavoritesOnly: companionPreferences.autoPrepBeverageFavoritesOnly,
        rareConcurrency: companionPreferences.autoRareConcurrency,
        leaseMessage: automationLeaseError || automationLease?.error || '',
        orderLines: [orderLine],
        selectionLines: [],
        skipLines: [],
      }).catch(() => {
        signatures.delete(signature);
      });
    },
    [
      apiToken,
      automationLease?.error,
      automationLeaseError,
      automationLeaseOwned,
      companionPreferences,
      connectionReadyForActions,
      normalAutomationDecisionDiagnosticSignaturesRef,
      normalizedEndpoint,
      rareAutomationDecisionDiagnosticSignaturesRef,
      snapshot?.activeSceneName,
      snapshot?.specialBusiness,
    ],
  );

  useEffect(() => {
    const events = snapshot?.automationEvents;
    if (!events || !automationSessionId || automationStateSessionIdRef.current !== automationSessionId)
      return;

    const unresolvedBarrierSequences = new Set(
      events.filter(isManualResolutionAutomationEvent).map((event) => event.sequence),
    );
    const now = Date.now();
    let rareChanged = false;
    let normalChanged = false;
    for (const [orderKey, state] of rareOrderStatesRef.current) {
      if (
        !shouldRetireMissingManualBarrier(
          state.manualResolutionRequired,
          state.lastRuntimeEventSequence,
          unresolvedBarrierSequences,
        )
      )
        continue;
      rareOrderStatesRef.current.set(orderKey, {
        ...emptyAutoFirstOrderState(orderKey, now),
        lastRuntimeEventSequence: state.lastRuntimeEventSequence,
        lastError: '这项待人工确认状态已由其他自动化控制窗口处理，等待下一轮重新判断。',
      });
      rareChanged = true;
    }
    for (const [orderKey, state] of normalOrderStatesRef.current) {
      if (
        !shouldRetireMissingManualBarrier(
          state.manualResolutionRequired,
          state.lastRuntimeEventSequence,
          unresolvedBarrierSequences,
        )
      )
        continue;
      normalOrderStatesRef.current.set(orderKey, {
        ...emptyNormalAutoOrderState(orderKey, now),
        lastRuntimeEventSequence: state.lastRuntimeEventSequence,
        lastError: '这项待人工确认状态已由其他自动化控制窗口处理，等待下一轮重新判断。',
      });
      normalChanged = true;
    }

    if (rareChanged) {
      lastAutoFirstOrderAtRef.current = 0;
      refreshRareOrderDiagnostics(now);
      publishAutoPrepMessage(
        '自动化\n检测到待人工确认状态已由其他控制窗口处理，本地稀客订单状态已重新同步。',
      );
    }
    if (normalChanged) {
      lastAutoNormalOrderAtRef.current = 0;
      refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], now);
      publishNormalOrderMessage(
        '普客自动化\n检测到待人工确认状态已由其他控制窗口处理，本地订单状态已重新同步。',
      );
    }
  }, [
    automationSessionId,
    automationStateSessionIdRef,
    lastAutoFirstOrderAtRef,
    lastAutoNormalOrderAtRef,
    normalOrderStatesRef,
    publishAutoPrepMessage,
    publishNormalOrderMessage,
    rareOrderStatesRef,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    snapshot?.automationEvents,
    snapshot?.normalBusiness?.orders,
  ]);

  useEffect(() => {
    const events = snapshot?.automationEvents ?? [];
    if (events.length === 0) return;

    const nextEvents = [...events].sort((left, right) => left.sequence - right.sequence);
    if (nextEvents.length === 0) return;

    const now = Date.now();
    let rareChanged = false;
    let normalChanged = false;
    let rarePaused = false;
    let normalPaused = false;
    const normalOrders = snapshot?.normalBusiness?.orders ?? [];

    for (const event of nextEvents) {
      const manualResolutionRequired = isManualResolutionAutomationEvent(event);
      const blocking =
        manualResolutionRequired ||
        isBlockingCookingTerminalEvent(event) ||
        isCookingTagsUnreadableStoredEvent(event);
      const recoverable = isRecoverableCookingTerminalEvent(event);
      if (!recoverable && !blocking) continue;
      if (
        !blocking &&
        snapshot?.specialBusiness?.active === true &&
        snapshot.specialBusiness.challengeType === WACKY_CHALLENGE_TYPE &&
        isWackyTargetTagMismatchEvent(event)
      ) {
        const rejectedKey = buildWackyRejectedRecipeKeyFromEvent(event);
        if (rejectedKey) {
          rememberSpecialBusinessRejectedRecipeKey(rejectedKey);
        }
      }

      if (event.targetKind === 'rare') {
        if (!orderRecommendations.isCurrent) continue;
        for (const item of orderRecommendations.recommendations) {
          const orderKey = buildAutoOrderKey(item);
          const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
          if (!matchesRareAutomationEvent(event, item, state)) continue;
          if (event.sequence <= state.lastRuntimeEventSequence) continue;
          if (state.manualResolutionRequired) {
            if (
              !canAdvanceAutomationRuntimeEventSequence(
                state.manualResolutionRequired,
                manualResolutionRequired,
              )
            )
              continue;
            rareOrderStatesRef.current.set(orderKey, {
              ...state,
              lastRuntimeEventSequence: event.sequence,
            });
            rareChanged = true;
            rarePaused = true;
            break;
          }

          const nextState = blocking
            ? pauseRareOrderStateAfterRuntimeFailure(state, now, event)
            : enforceAutomationRollbackLimit(
                resetRareOrderStateAfterRuntimeMismatch(state, now, event),
                companionPreferences.autoMaxRollbacks,
                now,
              );
          rareOrderStatesRef.current.set(orderKey, nextState);
          if (!blocking) {
            publishAutomationRollbackDiagnostic(
              event,
              state.rollbackCount,
              nextState.rollbackCount,
              item.order.specialBusinessRole ?? '',
            );
          }
          rareChanged = true;
          rarePaused ||= blocking;
          lastAutoFirstOrderAtRef.current = 0;
          break;
        }
        continue;
      }

      if (event.targetKind === 'normal') {
        const eventStateKey = buildNormalLifecycleAutoOrderKey(
          event.orderKey || event.traceId,
          event.orderLifecycleSequence,
        );
        let matchedKey =
          eventStateKey && normalOrderStatesRef.current.has(eventStateKey) ? eventStateKey : '';
        let matchedOrder = matchedKey
          ? (normalOrders.find((order) => buildNormalAutoOrderKey(order) === matchedKey) ?? null)
          : null;
        if (matchedKey && matchedOrder && !matchesNormalAutomationEvent(event, matchedOrder)) {
          matchedKey = '';
          matchedOrder = null;
        } else if (matchedKey && !matchedOrder) {
          const keyedState = normalOrderStatesRef.current.get(matchedKey);
          if (!event.jobId || keyedState?.cookingJobId !== event.jobId) {
            matchedKey = '';
          }
        }
        if (!matchedKey) {
          for (const order of normalOrders) {
            if (!matchesNormalAutomationEvent(event, order)) continue;
            matchedKey = buildNormalAutoOrderKey(order);
            matchedOrder = order;
            break;
          }
        }
        if (!matchedKey) continue;

        const state =
          normalOrderStatesRef.current.get(matchedKey) ?? emptyNormalAutoOrderState(matchedKey, now);
        if (event.jobId && state.cookingJobId && event.jobId !== state.cookingJobId) continue;
        if (event.sequence <= state.lastRuntimeEventSequence) continue;
        if (state.manualResolutionRequired) {
          if (
            !canAdvanceAutomationRuntimeEventSequence(
              state.manualResolutionRequired,
              manualResolutionRequired,
            )
          )
            continue;
          normalOrderStatesRef.current.set(matchedKey, {
            ...state,
            lastRuntimeEventSequence: event.sequence,
          });
          normalChanged = true;
          normalPaused = true;
          continue;
        }
        const nextState = blocking
          ? pauseNormalOrderStateAfterRuntimeFailure(state, matchedKey, now, event)
          : enforceAutomationRollbackLimit(
              resetNormalOrderStateAfterRuntimeMismatch(state, matchedKey, now, event),
              companionPreferences.autoMaxRollbacks,
              now,
            );
        normalOrderStatesRef.current.set(matchedKey, nextState);
        if (!blocking) {
          publishAutomationRollbackDiagnostic(
            event,
            state.rollbackCount,
            nextState.rollbackCount,
            matchedOrder?.specialBusinessRole ?? '',
          );
        }
        normalChanged = true;
        normalPaused ||= blocking;
        lastAutoNormalOrderAtRef.current = 0;
        if (matchedOrder && matchedOrder.hasEvaluated && !nextState.manualResolutionRequired) {
          normalOrderStatesRef.current.delete(matchedKey);
        }
      }
    }

    if (rareChanged) {
      refreshRareOrderDiagnostics(now);
      publishAutoPrepMessage(
        rarePaused
          ? '自动化\n料理任务需要人工处理，当前订单自动化已暂停；请展开诊断，并按订单提示重试或确认已处理。'
          : '自动化\n料理任务被外部操作中断，下一轮将根据当前订单状态重新调度。',
      );
    }
    if (normalChanged) {
      refreshNormalOrderDiagnostics(normalOrders, now);
      publishNormalOrderMessage(
        normalPaused
          ? '普客自动化\n料理任务需要人工处理，当前订单自动化已暂停；请展开诊断，并按订单提示重试或确认已处理。'
          : '普客自动化\n料理任务被外部操作中断，下一轮将根据当前订单状态重新调度。',
      );
    }
  }, [
    companionPreferences.autoMaxRollbacks,
    publishAutoPrepMessage,
    publishNormalOrderMessage,
    publishAutomationRollbackDiagnostic,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    rememberSpecialBusinessRejectedRecipeKey,
    automationSessionId,
    orderRecommendations.isCurrent,
    orderRecommendations.recommendations,
    orderRecommendations.successRevision,
    snapshot?.automationEvents,
    snapshot?.normalBusiness?.orders,
    snapshot?.specialBusiness?.active,
    snapshot?.specialBusiness?.challengeType,
    rareOrderStatesRef,
    lastAutoFirstOrderAtRef,
    normalOrderStatesRef,
    lastAutoNormalOrderAtRef,
  ]);

  useEffect(() => {
    specialBusinessRejectedRecipeKeysRef.current.clear();
    setSpecialBusinessRejectedRecipeKeys([]);
  }, [
    setSpecialBusinessRejectedRecipeKeys,
    snapshot?.specialBusiness?.challengeType,
    snapshot?.specialBusiness?.phase,
    specialBusinessFoodTargetSignature,
    specialBusinessRejectedRecipeKeysRef,
  ]);

  const retryRareAutomationOrder = useCallback(
    (orderKey: string) => {
      const now = Date.now();
      const state = rareOrderStatesRef.current.get(orderKey);
      if (!state) return;
      const transition = reduceAutomationManualRetry(
        state,
        state.prepared || state.beverageHandled ? 'complete-order' : 'match-order',
        now,
      );
      if (!transition.resumed) {
        if (!state.manualResolutionRequired) return;
        publishAutoPrepMessage(
          '自动化\n该订单的游戏操作结果无法自动确认，请检查游戏状态后点击“确认已处理”。',
        );
        return;
      }
      rareOrderStatesRef.current.set(orderKey, transition.state);
      lastAutoFirstOrderAtRef.current = 0;
      publishAutoPrepMessage(
        transition.rollbackBudgetReset
          ? '自动化\n已重新启用该稀客订单，并重置自动重新制作次数。'
          : '自动化\n已重新启用该稀客订单，下一轮会继续处理。',
      );
      refreshRareOrderDiagnostics(now);
    },
    [lastAutoFirstOrderAtRef, publishAutoPrepMessage, rareOrderStatesRef, refreshRareOrderDiagnostics],
  );

  const requestAutomationBarrierAck = useCallback(
    async (busyKey: string, sequence: number): Promise<AutomationSafetyBarrierAckResponse | null> => {
      const currentScope = () => automationConnectionScopeRef.current === automationConnectionScope;
      if (!currentScope()) return null;
      const sessionId = automationStateSessionIdRef.current;
      if (sequence <= 0) {
        return automationBarrierAckFailure(sequence, '该订单没有可供确认的事件编号。');
      }
      if (!sessionId || !connectionReadyForActions || !automationLeaseOwnedRef.current) {
        return automationBarrierAckFailure(
          sequence,
          '当前未持有本游戏实例的自动化控制权，不能确认处理结果。',
        );
      }
      if (automationBarrierAckRef.current) {
        return automationBarrierAckFailure(sequence, '另一项人工确认正在处理中，请稍后重试。');
      }

      const entry: AutomationBarrierAckEntry = { key: busyKey, sessionId, sequence };
      automationBarrierAckRef.current = entry;
      setAutomationBarrierAckBusyKey(busyKey);
      setAutomationBarrierAckErrors((current) => {
        if (!(sequence in current)) return current;
        const next = { ...current };
        delete next[sequence];
        return next;
      });
      try {
        const response = await acknowledgeAutomationSafetyBarrier(
          normalizedEndpoint,
          apiToken,
          sequence,
          companionDeviceAuthority.authorityRevision,
        );
        if (!currentScope() || automationStateSessionIdRef.current !== sessionId) return null;
        if (!response.ok) {
          return automationBarrierAckFailure(sequence, response.error || 'Mod 未确认这项处理结果。');
        }
        if (response.sequence !== sequence || response.acknowledgedCount <= 0) {
          return automationBarrierAckFailure(sequence, 'Mod 返回的确认结果与当前事件编号不一致。');
        }
        if (
          !response.acknowledgedSequences.includes(sequence) ||
          response.acknowledgedSequences.length !== response.acknowledgedCount
        ) {
          return automationBarrierAckFailure(sequence, 'Mod 返回的已确认事件编号无效。');
        }
        return response;
      } catch (err) {
        if (!currentScope()) return null;
        return automationBarrierAckFailure(sequence, err instanceof Error ? err.message : String(err));
      } finally {
        if (automationBarrierAckRef.current === entry) {
          automationBarrierAckRef.current = null;
          setAutomationBarrierAckBusyKey('');
        }
      }
    },
    [
      apiToken,
      automationConnectionScope,
      automationConnectionScopeRef,
      connectionReadyForActions,
      automationBarrierAckRef,
      automationLeaseOwnedRef,
      automationStateSessionIdRef,
      companionDeviceAuthority.authorityRevision,
      normalizedEndpoint,
      setAutomationBarrierAckBusyKey,
      setAutomationBarrierAckErrors,
    ],
  );

  const clearAcknowledgedAutomationBarriers = useCallback(
    (
      acknowledgedSequences: readonly number[],
      updatedAt: number,
      status: string,
    ): { rareChanged: boolean; normalChanged: boolean } => {
      const acknowledged = new Set(acknowledgedSequences);
      let rareChanged = false;
      let normalChanged = false;

      for (const [orderKey, state] of rareOrderStatesRef.current) {
        if (!state.manualResolutionRequired || !acknowledged.has(state.lastRuntimeEventSequence)) continue;
        rareOrderStatesRef.current.set(orderKey, {
          ...emptyAutoFirstOrderState(orderKey, updatedAt),
          lastRuntimeEventSequence: state.lastRuntimeEventSequence,
          lastError: status || '已确认游戏状态，等待下一轮重新判断。',
        });
        rareChanged = true;
      }
      for (const [orderKey, state] of normalOrderStatesRef.current) {
        if (!state.manualResolutionRequired || !acknowledged.has(state.lastRuntimeEventSequence)) continue;
        normalOrderStatesRef.current.set(orderKey, {
          ...emptyNormalAutoOrderState(orderKey, updatedAt),
          lastRuntimeEventSequence: state.lastRuntimeEventSequence,
          lastError: status || '已确认游戏状态，等待下一轮重新判断。',
        });
        normalChanged = true;
      }
      setAutomationBarrierAckErrors((current) => {
        const next = { ...current };
        let changed = false;
        for (const sequence of acknowledged) {
          if (!(sequence in next)) continue;
          delete next[sequence];
          changed = true;
        }
        return changed ? next : current;
      });
      return { rareChanged, normalChanged };
    },
    [normalOrderStatesRef, rareOrderStatesRef, setAutomationBarrierAckErrors],
  );

  const resetRareAutomationOrder = useCallback(
    (orderKey: string) => {
      const now = Date.now();
      const state = rareOrderStatesRef.current.get(orderKey);
      if (!state?.manualResolutionRequired) {
        rareOrderStatesRef.current.delete(orderKey);
        lastAutoFirstOrderAtRef.current = 0;
        publishAutoPrepMessage('自动化\n已重置该稀客订单状态，下一轮会重新判断料理、酒水和完成状态。');
        refreshRareOrderDiagnostics(now);
        return;
      }

      const sequence = state.lastRuntimeEventSequence;
      void (async () => {
        const response = await requestAutomationBarrierAck(`rare:${orderKey}`, sequence);
        if (!response || automationConnectionScopeRef.current !== automationConnectionScope) return;
        const updatedAt = Date.now();
        const current = rareOrderStatesRef.current.get(orderKey);
        if (!current?.manualResolutionRequired) return;
        if (!response.ok) {
          const errorMessage = `确认失败：${response.error || '未知错误'}`;
          rareOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(
              {
                ...current,
                lastError: errorMessage,
              },
              updatedAt,
              errorMessage,
            ),
          );
          publishAutoPrepMessage(`自动化\n${errorMessage}；该订单仍需人工确认。`);
          refreshRareOrderDiagnostics(updatedAt);
          return;
        }
        const acknowledged = new Set(response.acknowledgedSequences);
        const cleared = clearAcknowledgedAutomationBarriers(
          response.acknowledgedSequences,
          updatedAt,
          response.status,
        );
        if (cleared.normalChanged) {
          refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], updatedAt);
        }
        if (!acknowledged.has(current.lastRuntimeEventSequence)) {
          const errorMessage = `事件 #${sequence} 已确认，但检测到较新的待确认事件 #${current.lastRuntimeEventSequence}，当前订单仍保持暂停。`;
          const latest = rareOrderStatesRef.current.get(orderKey) ?? current;
          rareOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(
              {
                ...latest,
                lastError: errorMessage,
              },
              updatedAt,
              errorMessage,
            ),
          );
          publishAutoPrepMessage(`自动化\n${errorMessage}`);
          refreshRareOrderDiagnostics(updatedAt);
          return;
        }
        lastAutoFirstOrderAtRef.current = 0;
        publishAutoPrepMessage(
          `自动化\n${response.status || '处理结果已确认，下一轮会根据当前游戏状态重新判断。'}`,
        );
        refreshRareOrderDiagnostics(updatedAt);
        scheduleAutomationRefresh();
      })();
    },
    [
      clearAcknowledgedAutomationBarriers,
      automationConnectionScope,
      automationConnectionScopeRef,
      lastAutoFirstOrderAtRef,
      publishAutoPrepMessage,
      rareOrderStatesRef,
      refreshNormalOrderDiagnostics,
      refreshRareOrderDiagnostics,
      requestAutomationBarrierAck,
      scheduleAutomationRefresh,
      snapshot?.normalBusiness?.orders,
    ],
  );

  const retryNormalAutomationOrder = useCallback(
    (orderKey: string) => {
      const now = Date.now();
      const state = normalOrderStatesRef.current.get(orderKey);
      if (!state) return;
      const transition = reduceAutomationManualRetry(
        state,
        state.prepared ? 'deliver-food' : 'match-order',
        now,
      );
      if (!transition.resumed) {
        if (!state.manualResolutionRequired) return;
        publishNormalOrderMessage(
          '普客自动化\n该订单的游戏操作结果无法自动确认，请检查游戏状态后点击“确认已处理”。',
        );
        return;
      }
      normalOrderStatesRef.current.set(orderKey, transition.state);
      lastAutoNormalOrderAtRef.current = 0;
      const orders = snapshot?.normalBusiness?.orders ?? [];
      refreshNormalOrderDiagnostics(orders, now);
      publishNormalOrderMessage(
        transition.rollbackBudgetReset
          ? '普客自动化\n已重新启用该普客订单，并重置自动重新制作次数。'
          : '普客自动化\n已重新启用该普客订单，下一轮会继续处理。',
      );
    },
    [
      lastAutoNormalOrderAtRef,
      normalOrderStatesRef,
      publishNormalOrderMessage,
      refreshNormalOrderDiagnostics,
      snapshot?.normalBusiness?.orders,
    ],
  );

  const resetNormalAutomationOrder = useCallback(
    (orderKey: string) => {
      const now = Date.now();
      const state = normalOrderStatesRef.current.get(orderKey);
      if (!state?.manualResolutionRequired) {
        normalOrderStatesRef.current.delete(orderKey);
        lastAutoNormalOrderAtRef.current = 0;
        const orders = snapshot?.normalBusiness?.orders ?? [];
        refreshNormalOrderDiagnostics(orders, now);
        publishNormalOrderMessage('普客自动化\n已重置该普客订单状态，下一轮会根据当前游戏订单状态重新判断。');
        return;
      }

      const sequence = state.lastRuntimeEventSequence;
      void (async () => {
        const response = await requestAutomationBarrierAck(`normal:${orderKey}`, sequence);
        if (!response || automationConnectionScopeRef.current !== automationConnectionScope) return;
        const updatedAt = Date.now();
        const current = normalOrderStatesRef.current.get(orderKey);
        if (!current?.manualResolutionRequired) return;
        const orders = snapshot?.normalBusiness?.orders ?? [];
        if (!response.ok) {
          const errorMessage = `确认失败：${response.error || '未知错误'}`;
          normalOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(
              {
                ...current,
                lastError: errorMessage,
              },
              updatedAt,
              errorMessage,
            ),
          );
          publishNormalOrderMessage(`普客自动化\n${errorMessage}；该订单仍需人工确认。`);
          refreshNormalOrderDiagnostics(orders, updatedAt);
          return;
        }
        const acknowledged = new Set(response.acknowledgedSequences);
        const cleared = clearAcknowledgedAutomationBarriers(
          response.acknowledgedSequences,
          updatedAt,
          response.status,
        );
        if (cleared.rareChanged) refreshRareOrderDiagnostics(updatedAt);
        if (!acknowledged.has(current.lastRuntimeEventSequence)) {
          const errorMessage = `事件 #${sequence} 已确认，但检测到较新的待确认事件 #${current.lastRuntimeEventSequence}，当前订单仍保持暂停。`;
          const latest = normalOrderStatesRef.current.get(orderKey) ?? current;
          normalOrderStatesRef.current.set(
            orderKey,
            withAutomationDetail(
              {
                ...latest,
                lastError: errorMessage,
              },
              updatedAt,
              errorMessage,
            ),
          );
          publishNormalOrderMessage(`普客自动化\n${errorMessage}`);
          refreshNormalOrderDiagnostics(orders, updatedAt);
          return;
        }

        lastAutoNormalOrderAtRef.current = 0;
        publishNormalOrderMessage(
          `普客自动化\n${response.status || '处理结果已确认，下一轮会根据当前游戏订单状态重新判断。'}`,
        );
        refreshNormalOrderDiagnostics(orders, updatedAt);
        scheduleAutomationRefresh();
      })();
    },
    [
      clearAcknowledgedAutomationBarriers,
      automationConnectionScope,
      automationConnectionScopeRef,
      lastAutoNormalOrderAtRef,
      normalOrderStatesRef,
      publishNormalOrderMessage,
      refreshNormalOrderDiagnostics,
      refreshRareOrderDiagnostics,
      requestAutomationBarrierAck,
      scheduleAutomationRefresh,
      snapshot?.normalBusiness?.orders,
    ],
  );

  const acknowledgeAutomationBarrierEvent = useCallback(
    (sequence: number) => {
      void (async () => {
        const response = await requestAutomationBarrierAck(`barrier:${sequence}`, sequence);
        if (!response || automationConnectionScopeRef.current !== automationConnectionScope) return;
        const updatedAt = Date.now();
        if (!response.ok) {
          const errorMessage = response.error || 'Mod 未确认这项处理结果。';
          setAutomationBarrierAckErrors((current) => ({ ...current, [sequence]: errorMessage }));
          publishAutoPrepMessage(`自动化\n确认事件 #${sequence} 失败：${errorMessage}`);
          return;
        }

        clearAcknowledgedAutomationBarriers(response.acknowledgedSequences, updatedAt, response.status);
        lastAutoFirstOrderAtRef.current = 0;
        lastAutoNormalOrderAtRef.current = 0;
        refreshRareOrderDiagnostics(updatedAt);
        refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], updatedAt);
        publishAutoPrepMessage(`自动化\n${response.status || `事件 #${sequence} 的处理结果已确认。`}`);
        scheduleAutomationRefresh();
      })();
    },
    [
      clearAcknowledgedAutomationBarriers,
      automationConnectionScope,
      automationConnectionScopeRef,
      lastAutoFirstOrderAtRef,
      lastAutoNormalOrderAtRef,
      publishAutoPrepMessage,
      refreshNormalOrderDiagnostics,
      refreshRareOrderDiagnostics,
      requestAutomationBarrierAck,
      scheduleAutomationRefresh,
      setAutomationBarrierAckErrors,
      snapshot?.normalBusiness?.orders,
    ],
  );

  useAutomationSchedulers(options, {
    refreshRareOrderDiagnostics,
    refreshNormalOrderDiagnostics,
    publishAutomationTargetRotationDiagnostic,
    publishRareAutomationDecisionDiagnostic,
    publishNormalAutomationDecisionDiagnostic,
  });
  return {
    automationSafetyBarriers,
    retryRareAutomationOrder,
    resetRareAutomationOrder,
    retryNormalAutomationOrder,
    resetNormalAutomationOrder,
    acknowledgeAutomationBarrierEvent,
  };
}
