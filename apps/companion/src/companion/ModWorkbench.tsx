import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useGamepadNavigation } from '@/companion/use-gamepad-navigation';
import { WorkbenchHeader } from '@/companion/features/workbench/WorkbenchHeader';
import { useCompanionConnection } from '@/companion/hooks/useCompanionConnection';
import { useCustomRecipes } from '@/companion/hooks/useCustomRecipes';
import { useFavorites } from '@/companion/hooks/useFavorites';
import { useOrderAutomationIntervals } from '@/companion/hooks/useOrderAutomationIntervals';
import { useOrderRecommendations } from '@/companion/hooks/useOrderRecommendations';
import { useRareGuestInvitations } from '@/companion/hooks/useRareGuestInvitations';
import { ModCustomRecipesPanel } from '@/companion/pages/ModCustomRecipesPanel';
import { ModHelpPanel } from '@/companion/pages/ModHelpPanel';
import { ModInventoryPanel } from '@/companion/pages/ModInventoryPanel';
import { ModLogsPanel } from '@/companion/pages/ModLogsPanel';
import { ModNormalPanel } from '@/companion/pages/ModNormalPanel';
import { ModOverviewPanel } from '@/companion/pages/ModOverviewPanel';
import { ModRarePanel } from '@/companion/pages/ModRarePanel';
import { ModServicePanel, ServiceFocusPage } from '@/companion/pages/ModServicePanel';
import { ModSettingsPanel } from '@/companion/pages/ModSettingsPanel';
import { ModTasksPanel } from '@/companion/pages/ModTasksPanel';
import {
  completeFirstNormalOrder,
  completeFirstRareOrder,
  dismissRuntimeRareOrder,
  prepareNextRareOrder,
  publishGameUiPinningTarget,
} from '@/companion/api';
import {
  didAcknowledgeStep,
  didCompleteStep,
  didNormalOrderComplete,
  didNormalOrderCookingStillPending,
  didNormalOrderDeliverBeverage,
  didNormalOrderDeliverFood,
  didOrderCookingStillPending,
  emptyAutoFirstOrderState,
  emptyNormalAutoOrderState,
  formatAutomationState,
  isTransientAutoPreparationFailure,
  markAutomationWaiting,
  pauseAutomationState,
  updateAutomationAfterResponse,
  type AutoFirstOrderState,
  type AutomationStep,
  type NormalAutoOrderState,
} from '@/companion/automation-state';
import {
  applyRareServedStateFromResponse,
  buildAutoOrderKey,
  buildCompleteOrderPreferences,
  buildGameUiPinningTarget,
  buildNightBusinessOrderKey,
  buildNormalAutoOrderDiagnostics,
  buildNormalAutoOrderKey,
  buildNormalCookerDemand,
  buildNormalOrderAutomationSignature,
  buildRareAutoOrderDiagnostic,
  formatOrderPreparationResponse,
  formatRareAutomationPrefix,
  hasAutomationActionEnabled,
  hasNormalOrderActionEnabled,
  isNormalOrderCollected,
  isNormalOrderPreparedStale,
  isRecoverableNormalPausedState,
  lockRareAutomationTargets,
  reserveAutomationCookerSlot,
  reserveRareCookerSlot,
  selectOrderPreparationCandidates,
  shouldAttemptNormalBeverage,
  shouldAttemptNormalCompletion,
  shouldAttemptNormalCooking,
  syncNormalOrderStateWithSnapshot,
  syncRareStateWithOrderServedState,
  type ValidOrderPreparationSelection,
} from '@/companion/domain/automation';
import {
  buildAutomationCookerCapacity,
  buildRuntimeSets,
  getNormalCookerRequirement,
  getRareCookerRequirement,
} from '@/companion/domain/cookers';
import {
  isUsableRareCustomer,
  normalizePlace,
  toRuntimeRareCustomer,
} from '@/companion/domain/service-recommendations';
import { sortNormalOrders } from '@/companion/domain/sorting';
import { formatDesk } from '@/companion/formatters';
import {
  applyCompanionPreferencesToTauri,
  applyCompanionVisualPreferences,
  normalizeCompanionPreferences,
  normalizeFocusSwitchCooldownMs,
  persistCompanionPreferences,
  readStoredCompanionPreferences,
  type CompanionPreferences,
  type FocusSwitchBehavior,
} from '@/companion/preferences';
import {
  normalizeRareGuestInvitationLevels,
  persistFocusBeverageLimit,
  persistFocusCompact,
  persistFocusRecipeLimit,
  persistTab,
  readStoredFocusBeverageLimit,
  readStoredFocusCompact,
  readStoredFocusRecipeLimit,
  readStoredTab,
} from '@/companion/storage';
import type {
  AutomationCookerCycle,
  ModTab,
  NightBusinessOrder,
  NormalAutoOrderDiagnostic,
  NormalBusinessOrder,
  RareAutoOrderDiagnostic,
} from '@/companion/types';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import {
  buildRecommendationDataIndexes,
  buildRecommendationDataSet,
} from '@/lib/recommendation-data';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import { useThemeMode } from '@/lib/theme';
import type { PlaceName } from '@/lib/catalog-types';

const AUTO_FIRST_ORDER_TICK_MS = 1500;
const AUTO_NORMAL_ORDER_TICK_MS = 500;
const MOD_TAB_TRIGGER_CLASS = 'min-w-0 flex-1';

const MOD_TABS: ModTab[] = ['overview', 'normal', 'rare', 'custom-recipes', 'service', 'tasks', 'inventory', 'help', 'logs', 'settings'];
const BASIC_MOD_TABS: ModTab[] = MOD_TABS.filter((tab) => tab !== 'logs');

/**
 * 伴随窗口的根工作台组件。
 *
 * 这里汇总本地 API 连接、推荐数据、收藏、自动化状态、手柄导航和页面路由。组件本身不直接读取游戏对象；
 * 所有运行时输入来自 `useCompanionConnection` 的快照，所有回写操作通过 `api.ts` 发送到 Mod 本地 API。
 */
export function ModWorkbench() {
  const { mode: themeMode, setMode: setThemeMode } = useThemeMode();
  const [tab, setTab] = useState<ModTab>(() => readStoredTab());
  const [serviceFocusMode, setServiceFocusMode] = useState(false);
  const [serviceFocusCompact, setServiceFocusCompact] = useState(readStoredFocusCompact);
  const [serviceFocusRecipeLimit, setServiceFocusRecipeLimit] = useState(readStoredFocusRecipeLimit);
  const [serviceFocusBeverageLimit, setServiceFocusBeverageLimit] = useState(readStoredFocusBeverageLimit);
  const [companionPreferences, setCompanionPreferences] = useState<CompanionPreferences>(() =>
    readStoredCompanionPreferences(),
  );
  // 经营中页面需要尽快响应订单变化和自动化结果；其他页面使用较低频率，减少本地 API 与反射快照压力。
  const snapshotRefreshIntervalMs = tab === 'service' || serviceFocusMode ? 750 : 2000;
  const {
    endpointDraft,
    setEndpointDraft,
    apiToken,
    apiTokenDraft,
    setApiTokenDraft,
    snapshot,
    cachedRuntimeData,
    error,
    loading,
    connectionPaused,
    connectionFailureCount,
    lastConnectedAt,
    normalizedEndpoint,
    applyEndpointConnection,
    applyConnectionDetails,
    pauseConnection,
    refresh,
  } = useCompanionConnection(snapshotRefreshIntervalMs);
  const {
    favorites,
    favoriteError,
    favoriteBusyKey,
    toggleRecipeFavorite,
    toggleBeverageFavorite,
  } = useFavorites({ apiToken, connectionPaused, normalizedEndpoint });
  const {
    customRecipes,
    customRecipeError,
    customRecipeBusyKey,
    upsertCustomRecipeEntry,
    removeCustomRecipeEntry,
    toggleCustomRecipeEntry,
    moveCustomRecipeEntry,
  } = useCustomRecipes({ apiToken, connectionPaused, normalizedEndpoint });
  const {
    rareGuestInvitationScope,
    setRareGuestInvitationScope,
    rareGuestInvitationLevels,
    setRareGuestInvitationLevels,
    rareGuestInvitationResult,
    rareGuestInvitationError,
    rareGuestInvitationBusyKey,
    loadRareGuestInvitations,
    inviteAllRareGuests,
    inviteRareGuest,
  } = useRareGuestInvitations({
    apiToken,
    normalizedEndpoint,
    snapshot,
    tab,
    refresh,
  });
  const [manualPlace, setManualPlace] = useState<PlaceName | null>(null);
  const [rareCustomerId, setRareCustomerId] = useState<number | null>(null);
  const [requiredFoodTag, setRequiredFoodTag] = useState('');
  const [requiredBeverageTag, setRequiredBeverageTag] = useState('');
  const [dismissRareOrderBusyKey, setDismissRareOrderBusyKey] = useState('');
  const [dismissRareOrderError, setDismissRareOrderError] = useState('');
  const [autoPrepBusy, setAutoPrepBusy] = useState(false);
  const [autoPrepMessage, setAutoPrepMessage] = useState('');
  const [autoPrepPaused, setAutoPrepPaused] = useState(false);
  const [rareOrderDiagnostics, setRareOrderDiagnostics] = useState<RareAutoOrderDiagnostic[]>([]);
  const [normalOrderBusy, setNormalOrderBusy] = useState(false);
  const [normalOrderMessage, setNormalOrderMessage] = useState('');
  const [normalOrderPausedCount, setNormalOrderPausedCount] = useState(0);
  const [normalOrderDiagnostics, setNormalOrderDiagnostics] = useState<NormalAutoOrderDiagnostic[]>([]);
  // 自动化状态不放入 useState，是为了避免每个轮询 tick 都触发整页重渲染；页面只在诊断摘要变化时更新。
  const rareOrderStatesRef = useRef(new Map<string, AutoFirstOrderState>());
  const rareOrderDiagnosticItemsRef = useRef(new Map<string, ValidOrderPreparationSelection>());
  const autoFirstOrderBusyRef = useRef(false);
  const normalOrderStatesRef = useRef(new Map<string, NormalAutoOrderState>());
  const normalOrderBusyRef = useRef(false);
  const lastAutoFirstOrderAtRef = useRef(0);
  const lastAutoNormalOrderAtRef = useRef(0);
  const automationCookerCycleRef = useRef<AutomationCookerCycle | null>(null);
  const lastUiPinningSignatureRef = useRef('');

  const updateCompanionPreferences = useCallback((next: Partial<CompanionPreferences>) => {
    setCompanionPreferences((current) => normalizeCompanionPreferences({ ...current, ...next }));
  }, []);

  useEffect(() => {
    if (!companionPreferences.showDebugDetails && tab === 'logs') {
      setTab('overview');
    }
  }, [companionPreferences.showDebugDetails, tab]);

  const runtime = snapshot?.recommendationState ?? null;
  const night = snapshot?.nightBusiness ?? null;
  const detectedPlace = normalizePlace(night?.place);
  const selectedPlace = manualPlace ?? detectedPlace;
  const effectiveRuntimeData = snapshot?.runtimeData?.isComplete
    ? snapshot.runtimeData
    : cachedRuntimeData ?? snapshot?.runtimeData;
  // 运行时目录数据较大且不是每次快照都完整发布；优先使用完整快照，否则复用上一次完整缓存。
  const recommendationData = useMemo(
    () => buildRecommendationDataSet(effectiveRuntimeData),
    [effectiveRuntimeData],
  );
  const recommendationIndexes = useMemo(
    () => buildRecommendationDataIndexes(recommendationData),
    [recommendationData],
  );
  const runtimeRareCustomers = useMemo(
    () => (snapshot?.runtimeRareCustomers ?? [])
      .map(toRuntimeRareCustomer)
      .filter(isUsableRareCustomer),
    [snapshot?.runtimeRareCustomers],
  );

  const runtimeSets = useMemo(() => buildRuntimeSets(runtime, recommendationData), [recommendationData, runtime]);
  const normalOrderSignature = useMemo(
    () => buildNormalOrderAutomationSignature(snapshot?.normalBusiness?.orders ?? []),
    [snapshot?.normalBusiness?.orders],
  );
  const visibleTabs = companionPreferences.showDebugDetails ? MOD_TABS : BASIC_MOD_TABS;
  const orderRecommendationPayload = useMemo(
    () => ({
      orders: night?.orders ?? [],
      runtime,
      runtimeRareCustomers,
      favorites,
      customRecipes,
      preferences: companionPreferences,
      activeRareGuests: night?.activeRareGuests ?? [],
      missionServeTargets: snapshot?.runtimeMissions?.serveTargets ?? [],
      data: recommendationData,
    }),
    [
      companionPreferences,
      customRecipes,
      favorites,
      night?.activeRareGuests,
      night?.orders,
      recommendationData,
      runtime,
      runtimeRareCustomers,
      snapshot?.runtimeMissions?.serveTargets,
    ],
  );
  const orderRecommendations = useOrderRecommendations(orderRecommendationPayload);
  const gameUiPinningTarget = useMemo(
    () => companionPreferences.gameUiPinningEnabled || companionPreferences.cookerHighlightEnabled
      ? buildGameUiPinningTarget(
        orderRecommendations.recommendations,
        companionPreferences.serviceOrderSortMode,
        recommendationIndexes,
      )
      : null,
    [
      companionPreferences.cookerHighlightEnabled,
      companionPreferences.gameUiPinningEnabled,
      companionPreferences.serviceOrderSortMode,
      orderRecommendations.recommendations,
      recommendationIndexes,
    ],
  );
  useEffect(() => {
    if (!apiToken || connectionPaused) return;
    const signature = `${companionPreferences.gameUiPinningEnabled ? '1' : '0'}|${companionPreferences.cookerHighlightEnabled ? '1' : '0'}|${gameUiPinningTarget?.signature ?? 'disabled'}`;
    if (lastUiPinningSignatureRef.current === signature) return;

    let cancelled = false;
    publishGameUiPinningTarget(
      normalizedEndpoint,
      apiToken,
      companionPreferences.gameUiPinningEnabled,
      companionPreferences.cookerHighlightEnabled,
      gameUiPinningTarget,
    )
      .then(() => {
        if (!cancelled) lastUiPinningSignatureRef.current = signature;
      })
      .catch(() => {
        if (!cancelled) lastUiPinningSignatureRef.current = '';
        // 游戏尚未进入标题或存档前，本地 API 可能还没有启动。
      });

    return () => {
      cancelled = true;
    };
  }, [
    apiToken,
    connectionPaused,
    companionPreferences.cookerHighlightEnabled,
    companionPreferences.gameUiPinningEnabled,
    gameUiPinningTarget,
    normalizedEndpoint,
  ]);

  const refreshRareOrderDiagnostics = useCallback((now = Date.now()) => {
    const diagnostics = Array.from(rareOrderDiagnosticItemsRef.current.values()).map((selection) => {
      const orderKey = buildAutoOrderKey(selection.item);
      const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
      return buildRareAutoOrderDiagnostic(selection, state, now);
    });
    setRareOrderDiagnostics(diagnostics);
    setAutoPrepPaused(diagnostics.some((diagnostic) => diagnostic.paused));
  }, []);

  const refreshNormalOrderDiagnostics = useCallback((orders = snapshot?.normalBusiness?.orders ?? [], now = Date.now()) => {
    const diagnostics = buildNormalAutoOrderDiagnostics(orders, normalOrderStatesRef.current, now);
    setNormalOrderDiagnostics(diagnostics);
    setNormalOrderPausedCount(diagnostics.filter((diagnostic) => diagnostic.paused).length);
  }, [snapshot?.normalBusiness?.orders]);

  const getAutomationCookerCycle = useCallback((now: number): AutomationCookerCycle => {
    const bucket = Math.floor(now / AUTO_FIRST_ORDER_TICK_MS);
    if (!automationCookerCycleRef.current || automationCookerCycleRef.current.bucket !== bucket) {
      automationCookerCycleRef.current = {
        bucket,
        used: new Map<string, number>(),
        labels: new Map<string, string[]>(),
      };
    }

    return automationCookerCycleRef.current;
  }, []);

  const retryRareAutomationOrder = useCallback((orderKey: string) => {
    const now = Date.now();
    const state = rareOrderStatesRef.current.get(orderKey);
    if (!state) return;
    rareOrderStatesRef.current.set(orderKey, {
      ...state,
      paused: false,
      step: state.prepared || state.beverageHandled ? 'complete-order' : 'match-order',
      stepStartedAtMs: now,
      retryCount: 0,
      lastError: '已手动重试，等待下一轮自动化继续。',
    });
    lastAutoFirstOrderAtRef.current = 0;
    setAutoPrepMessage('自动化\n已重新启用该稀客订单，下一轮会继续处理。');
    refreshRareOrderDiagnostics(now);
  }, [refreshRareOrderDiagnostics]);

  const resetRareAutomationOrder = useCallback((orderKey: string) => {
    const now = Date.now();
    rareOrderStatesRef.current.delete(orderKey);
    lastAutoFirstOrderAtRef.current = 0;
    setAutoPrepMessage('自动化\n已重置该稀客订单状态，下一轮会重新判断料理、酒水和完成状态。');
    refreshRareOrderDiagnostics(now);
  }, [refreshRareOrderDiagnostics]);

  const dismissRareOrder = useCallback(async (order: NightBusinessOrder) => {
    if (!apiToken) {
      setDismissRareOrderError('未收到本地 API Token。请从游戏内启动或按 F8 唤起伴随窗口。');
      return;
    }

    const orderKey = buildNightBusinessOrderKey(order);
    setDismissRareOrderBusyKey(orderKey);
    setDismissRareOrderError('');
    try {
      const response = await dismissRuntimeRareOrder(normalizedEndpoint, apiToken, order);
      if (!response.ok) {
        throw new Error(response.error || response.status || '删除稀客订单失败');
      }

      await refresh(true);
    } catch (err) {
      setDismissRareOrderError(err instanceof Error ? err.message : String(err));
    } finally {
      setDismissRareOrderBusyKey('');
    }
  }, [apiToken, normalizedEndpoint, refresh]);

  const runAutoFirstOrder = useCallback(async () => {
    if (!companionPreferences.automationEnabled || autoFirstOrderBusyRef.current || autoPrepBusy) return;
    const now = Date.now();
    if (now - lastAutoFirstOrderAtRef.current < AUTO_FIRST_ORDER_TICK_MS) return;
    if (!apiToken) {
      setAutoPrepMessage('自动化已开启，但本地 API Token 不可用。');
      return;
    }

    if (!hasAutomationActionEnabled(companionPreferences)) {
      rareOrderStatesRef.current.clear();
      rareOrderDiagnosticItemsRef.current.clear();
      setRareOrderDiagnostics([]);
      setAutoPrepPaused(false);
      if (!companionPreferences.autoNormalOrderEnabled || !hasNormalOrderActionEnabled(companionPreferences)) {
        setAutoPrepMessage('自动化已开启，请在经营中页面启用至少一个子选项。');
      } else {
        setAutoPrepMessage('');
      }
      return;
    }

    if (orderRecommendations.pending || !orderRecommendations.isCurrent) {
      setAutoPrepMessage('自动化\n推荐计算中，等待下一次结果。');
      return;
    }

    if (orderRecommendations.error) {
      setAutoPrepMessage(`自动化\n${orderRecommendations.error}`);
      return;
    }

    const selectionPreferences = companionPreferences.autoPrepCompleteOrder
      ? buildCompleteOrderPreferences(companionPreferences)
      : companionPreferences;
    const candidateResult = selectOrderPreparationCandidates(
      orderRecommendations.recommendations,
      favorites,
      selectionPreferences,
      companionPreferences.autoRareConcurrency,
      rareOrderStatesRef.current,
    );
    if (candidateResult.selections.length === 0) {
      rareOrderStatesRef.current.clear();
      rareOrderDiagnosticItemsRef.current.clear();
      setRareOrderDiagnostics([]);
      setAutoPrepPaused(false);
      setAutoPrepMessage(`自动化\n${candidateResult.message}`);
      return;
    }

    const activeKeys = new Set(candidateResult.selections.map((selection) => buildAutoOrderKey(selection.item)));
    rareOrderDiagnosticItemsRef.current.clear();
    for (const selection of candidateResult.selections) {
      rareOrderDiagnosticItemsRef.current.set(buildAutoOrderKey(selection.item), selection);
    }
    for (const key of Array.from(rareOrderStatesRef.current.keys())) {
      if (!activeKeys.has(key)) rareOrderStatesRef.current.delete(key);
    }

    autoFirstOrderBusyRef.current = true;
    lastAutoFirstOrderAtRef.current = now;
    setAutoPrepBusy(true);
    try {
      const messages: string[] = [];
      let completedOrderThisTick = false;
      const cookerCycle = getAutomationCookerCycle(now);
      const cookerCapacity = buildAutomationCookerCapacity(runtime);
      const normalCookerDemand = buildNormalCookerDemand(
        snapshot?.normalBusiness?.orders ?? [],
        normalOrderStatesRef.current,
        companionPreferences,
        runtime,
        now,
        recommendationData,
      );

      for (const selection of candidateResult.selections) {
        const orderKey = buildAutoOrderKey(selection.item);
        const prefix = formatRareAutomationPrefix(selection.item);
        let currentState = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
        currentState = lockRareAutomationTargets(currentState, selection);
        currentState = syncRareStateWithOrderServedState(currentState, selection.item.order, now);
        if (currentState.paused) {
          messages.push(`${prefix}\n${formatAutomationState(currentState, companionPreferences)}\n稀客自动化已暂停该订单，订单变化或重新开启后会继续。`);
          continue;
        }

        let preflightMessage = '';
        if (companionPreferences.autoPrepCompleteOrder && !completedOrderThisTick) {
          const completeResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            currentState.recipeTarget,
            currentState.beverageTarget,
            buildCompleteOrderPreferences(companionPreferences),
          );

          if (completeResponse.completedOrder) {
            rareOrderStatesRef.current.delete(orderKey);
            completedOrderThisTick = true;
            messages.push(`${prefix}\n${formatOrderPreparationResponse(completeResponse)}`);
            continue;
          }

          currentState = applyRareServedStateFromResponse(currentState, selection.item.order, completeResponse, now);
          const nextState = updateAutomationAfterResponse(
            currentState,
            completeResponse,
            now,
            'complete-order',
            companionPreferences.autoPrepStopOnError,
            companionPreferences.autoMaxStepRetries,
          );
          currentState = nextState;
          preflightMessage = formatOrderPreparationResponse(completeResponse);
          if (nextState.paused) {
            rareOrderStatesRef.current.set(orderKey, nextState);
            messages.push(`${prefix}\n${preflightMessage}\n${formatAutomationState(nextState, companionPreferences)}\n稀客自动化已暂停该订单，订单变化或重新开启后会继续。`);
            continue;
          }
        } else if (companionPreferences.autoPrepCompleteOrder && completedOrderThisTick && currentState.prepared && currentState.beverageHandled) {
          const waitingState = markAutomationWaiting(currentState, 'complete-order', now, '本轮已完成一笔稀客订单，等待下一轮完成。');
          rareOrderStatesRef.current.set(orderKey, waitingState);
          messages.push(`${prefix}\n${formatAutomationState(waitingState, companionPreferences)}`);
          continue;
        }

        let shouldPrepareFood = companionPreferences.autoPrepStartCooking && !currentState.prepared;
        const shouldPrepareBeverage = companionPreferences.autoPrepTakeBeverage && !currentState.beverageHandled;
        const schedulerNote = shouldPrepareFood
          ? reserveRareCookerSlot(
            cookerCycle,
            getRareCookerRequirement(currentState.recipeTarget),
            `稀客 ${selection.item.order.guestName || '当前订单'} · 桌 ${formatDesk(selection.item.order.deskCode)}`,
            cookerCapacity,
            normalCookerDemand,
          )
          : { ok: true, message: '' };
        if (!schedulerNote.ok) {
          shouldPrepareFood = false;
        }

        if (!shouldPrepareFood && !shouldPrepareBeverage) {
          const waitingState = markAutomationWaiting(
            currentState,
            schedulerNote.ok
              ? companionPreferences.autoPrepCompleteOrder ? 'complete-order' : 'idle'
              : 'ensure-cooking',
            now,
            !schedulerNote.ok
              ? schedulerNote.message
              : companionPreferences.autoPrepCompleteOrder
              ? '等待料理出锅后直接送达，或等待下一轮完成订单。'
              : '已按当前设置完成可执行步骤；自动完成订单未开启。',
          );
          rareOrderStatesRef.current.set(orderKey, waitingState);
          messages.push(`${prefix}${preflightMessage ? `\n${preflightMessage}` : ''}\n${formatAutomationState(waitingState, companionPreferences)}`);
          continue;
        }

        const preparePreferences = {
          ...companionPreferences,
          autoPrepTakeBeverage: shouldPrepareBeverage,
          autoPrepStartCooking: shouldPrepareFood,
          autoPrepCollectCooking: true,
        };

        const prepareResponse = await prepareNextRareOrder(
          normalizedEndpoint,
          apiToken,
          selection.item,
          shouldPrepareFood ? currentState.recipeTarget : null,
          shouldPrepareBeverage ? currentState.beverageTarget : null,
          preparePreferences,
        );

        const stateAfterPrepareDelivery = applyRareServedStateFromResponse(currentState, selection.item.order, prepareResponse, now);
        const pendingRareCooking = didOrderCookingStillPending(prepareResponse, '自动开始料理');
        const startedRareCooking = didCompleteStep(prepareResponse, '自动开始料理');
        const nextPrepared = stateAfterPrepareDelivery.prepared
          || startedRareCooking
          || pendingRareCooking;
        const nextBeverageHandled = stateAfterPrepareDelivery.beverageHandled
          || didCompleteStep(prepareResponse, '自动送达酒水');
        const transientFailure = !prepareResponse.ok && isTransientAutoPreparationFailure(prepareResponse);
        const preparedAtMs = startedRareCooking || pendingRareCooking || (nextPrepared && !currentState.prepared) ? now : currentState.preparedAtMs;
        const beverageHandledAtMs = nextBeverageHandled && !currentState.beverageHandled ? now : currentState.beverageHandledAtMs;
        const rollbackCount = startedRareCooking || pendingRareCooking ? 0 : currentState.rollbackCount;
        const nextState = updateAutomationAfterResponse(
          {
            ...currentState,
            orderKey,
            prepared: nextPrepared,
            preparedAtMs,
            beverageHandled: nextBeverageHandled,
            beverageHandledAtMs,
            rollbackCount,
          },
          prepareResponse,
          now,
          shouldPrepareFood ? 'ensure-cooking' : shouldPrepareBeverage ? 'ensure-beverage' : 'match-order',
          companionPreferences.autoPrepStopOnError,
          companionPreferences.autoMaxStepRetries,
        );
        let finalState = nextState;
        let followUpMessage = '';
        if (companionPreferences.autoPrepCompleteOrder
          && !completedOrderThisTick
          && nextBeverageHandled
          && !currentState.beverageHandled) {
          const immediateCompleteResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            finalState.recipeTarget,
            finalState.beverageTarget,
            buildCompleteOrderPreferences(companionPreferences),
          );
          if (immediateCompleteResponse.completedOrder) {
            rareOrderStatesRef.current.delete(orderKey);
            completedOrderThisTick = true;
            messages.push(`${prefix}${preflightMessage ? `\n${preflightMessage}` : ''}\n${formatOrderPreparationResponse(prepareResponse)}\n${formatOrderPreparationResponse(immediateCompleteResponse)}`);
            continue;
          }

          finalState = applyRareServedStateFromResponse(finalState, selection.item.order, immediateCompleteResponse, now);
          followUpMessage = `\n${formatOrderPreparationResponse(immediateCompleteResponse)}`;
        }

        rareOrderStatesRef.current.set(orderKey, finalState);
        const suffix = finalState.paused
          ? '\n稀客自动化已暂停该订单，订单变化或重新开启后会继续。'
          : transientFailure
            ? '\n当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        const schedulerSuffix = schedulerNote.ok ? '' : `\n${schedulerNote.message}`;
        messages.push(`${prefix}${preflightMessage ? `\n${preflightMessage}` : ''}\n${formatOrderPreparationResponse(prepareResponse)}${followUpMessage}\n${formatAutomationState(finalState, companionPreferences)}${schedulerSuffix}${suffix}`);
      }

      if (candidateResult.messages.length > 0) {
        messages.push(...candidateResult.messages.map((message) => `跳过\n${message}`));
      }

      refreshRareOrderDiagnostics(now);
      setAutoPrepMessage(messages.length > 0
        ? `自动化\n${messages.join('\n\n')}`
        : '自动化\n当前没有需要执行的新步骤。');
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      if (companionPreferences.autoPrepStopOnError) {
        for (const selection of candidateResult.selections) {
          const orderKey = buildAutoOrderKey(selection.item);
          const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
          rareOrderStatesRef.current.set(orderKey, pauseAutomationState(state, now, message));
        }
        refreshRareOrderDiagnostics(now);
        setAutoPrepMessage(`自动化\n${message}\n稀客自动化已暂停，订单变化或重新开启后会继续。`);
      } else {
        setAutoPrepPaused(false);
        refreshRareOrderDiagnostics(now);
        setAutoPrepMessage(`自动化\n${message}`);
      }
    } finally {
      autoFirstOrderBusyRef.current = false;
      setAutoPrepBusy(false);
    }
  }, [
    apiToken,
    autoPrepBusy,
    companionPreferences,
    favorites,
    normalizedEndpoint,
    orderRecommendations.error,
    orderRecommendations.isCurrent,
    orderRecommendations.pending,
    orderRecommendations.recommendations,
    recommendationData,
    refresh,
    refreshRareOrderDiagnostics,
    getAutomationCookerCycle,
    runtime,
    snapshot?.normalBusiness?.orders,
  ]);

  const runAutoNormalOrder = useCallback(async () => {
    if (!companionPreferences.automationEnabled || !companionPreferences.autoNormalOrderEnabled || normalOrderBusyRef.current) return;
    const now = Date.now();
    if (now - lastAutoNormalOrderAtRef.current < AUTO_NORMAL_ORDER_TICK_MS) return;
    if (!hasNormalOrderActionEnabled(companionPreferences)) {
      normalOrderStatesRef.current.clear();
      setNormalOrderDiagnostics([]);
      setNormalOrderPausedCount(0);
      setNormalOrderMessage('普客自动化已开启，请至少启用一个处理阶段：送达酒水、自动制作料理、送达料理或完成订单。');
      return;
    }

    if (!apiToken) {
      setNormalOrderMessage('普客自动化已开启，但本地 API Token 不可用。');
      return;
    }

    const orders = sortNormalOrders(snapshot?.normalBusiness?.orders ?? []).filter((item) => !item.hasEvaluated);
    const activeKeys = new Set(orders.map(buildNormalAutoOrderKey));
    for (const key of Array.from(normalOrderStatesRef.current.keys())) {
      if (!activeKeys.has(key)) normalOrderStatesRef.current.delete(key);
    }
    for (const order of orders) {
      const orderKey = buildNormalAutoOrderKey(order);
      const syncedState = syncNormalOrderStateWithSnapshot(
        order,
        normalOrderStatesRef.current.get(orderKey),
        now,
        companionPreferences,
      );
      if (syncedState) normalOrderStatesRef.current.set(orderKey, syncedState);
    }
    refreshNormalOrderDiagnostics(orders, now);

    if (orders.length === 0) {
      normalOrderStatesRef.current.clear();
      setNormalOrderDiagnostics([]);
      setNormalOrderPausedCount(0);
      setNormalOrderMessage('普客自动化\n当前没有可处理的普客订单。');
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    const cookerCycle = getAutomationCookerCycle(now);
    const cookerCapacity = buildAutomationCookerCapacity(runtime);
    const schedulerMessages: string[] = [];
    const blockedOrders = orders.filter((order) => order.canAutomate === false);
    const blockedText = blockedOrders.length > 0
      ? `\n暂不可自动处理 ${blockedOrders.length} 笔：${blockedOrders
        .slice(0, 2)
        .map((order) => `桌 ${formatDesk(order.deskCode)} · ${order.actionBlockReason || '未读取到可执行客人控制器'}`)
        .join('；')}${blockedOrders.length > 2 ? '；…' : ''}`
      : '';
    const runnableOrders: NormalBusinessOrder[] = [];
    for (const order of orders) {
      if (order.canAutomate === false) continue;

      const state = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order));
      const needsBeverage = shouldAttemptNormalBeverage(order, state, companionPreferences, now);
      const needsCooking = shouldAttemptNormalCooking(order, state, companionPreferences, now);
      const needsCompletion = shouldAttemptNormalCompletion(order, state, companionPreferences, now);
      if (!needsBeverage && !needsCooking && !needsCompletion) continue;

      if (needsCooking) {
        const reservation = reserveAutomationCookerSlot(
          cookerCycle,
          getNormalCookerRequirement(order, recommendationData),
          `普客 桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`,
          cookerCapacity,
        );
        if (!reservation.ok) {
          schedulerMessages.push(`桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}\n${reservation.message}`);
          continue;
        }
      }

      runnableOrders.push(order);
      if (runnableOrders.length >= companionPreferences.autoNormalConcurrency) break;
    }
    const pausedCount = orders.filter((order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order))?.paused).length;
    if (runnableOrders.length === 0) {
      const waitingCount = orders.filter((order) => {
        const state = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order));
        return state?.prepared && !isNormalOrderCollected(order, state);
      }).length;
      const waitingState = orders
        .map((order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order)))
        .find((state) => state && (state.prepared || state.collected || state.paused));
      const schedulerText = schedulerMessages.length > 0 ? `\n${schedulerMessages.join('\n\n')}` : '';
      setNormalOrderMessage(waitingCount > 0 || pausedCount > 0
        ? `普客自动化\n当前没有需要新开锅的普客订单。\n等待制作或送达 ${waitingCount} 笔，暂停 ${pausedCount} 笔。${waitingState ? `\n${formatAutomationState(waitingState, companionPreferences)}` : ''}${blockedText}${schedulerText}`
        : `普客自动化\n当前没有需要执行的新步骤。${blockedText}${schedulerText}`);
      refreshNormalOrderDiagnostics(orders, now);
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    normalOrderBusyRef.current = true;
    lastAutoNormalOrderAtRef.current = now;
    setNormalOrderBusy(true);
    try {
      const messages: string[] = [];
      for (const order of runnableOrders) {
        const orderKey = buildNormalAutoOrderKey(order);
        const storedState = normalOrderStatesRef.current.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, now);
        const syncedState = syncNormalOrderStateWithSnapshot(order, storedState, now, companionPreferences) ?? storedState;
        const currentState = isRecoverableNormalPausedState(syncedState, now)
          ? {
            ...syncedState,
            paused: false,
            step: 'deliver-food' as const,
            stepStartedAtMs: now,
            lastProgressAtMs: now,
            retryCount: 0,
            rollbackCount: 0,
            lastError: '等待料理直接送达超时后已自动恢复，继续确认料理制作状态。',
          }
          : syncedState;
        const shouldRetryPrepared = isNormalOrderPreparedStale(currentState, now, companionPreferences);
        const shouldHandleBeverage = shouldAttemptNormalBeverage(order, currentState, companionPreferences, now);
        const shouldStartCooking = shouldAttemptNormalCooking(order, currentState, companionPreferences, now);
        const shouldCompleteOrder = shouldAttemptNormalCompletion(order, currentState, companionPreferences, now)
          || (companionPreferences.autoNormalCompleteOrder
            && !order.hasEvaluated
            && !(currentState.paused && !isRecoverableNormalPausedState(currentState, now))
            && (order.readyToEvaluate || order.hasServedFood || currentState.foodDelivered)
            && (order.hasServedBeverage || currentState.beverageHandled || shouldHandleBeverage));

        const requestPreferences = {
          ...companionPreferences,
          autoNormalTakeBeverage: companionPreferences.autoNormalTakeBeverage && shouldHandleBeverage,
          autoNormalStartCooking: companionPreferences.autoNormalStartCooking && shouldStartCooking,
          autoNormalCollectCooking: companionPreferences.autoNormalDeliverFood && shouldStartCooking,
          autoNormalDeliverFood: companionPreferences.autoNormalDeliverFood,
          autoNormalCompleteOrder: companionPreferences.autoNormalCompleteOrder
            && shouldCompleteOrder,
        };

        if (!requestPreferences.autoNormalTakeBeverage
          && !requestPreferences.autoNormalStartCooking
          && !requestPreferences.autoNormalCollectCooking
          && !requestPreferences.autoNormalDeliverFood
          && !requestPreferences.autoNormalCompleteOrder) {
          continue;
        }

        const response = await completeFirstNormalOrder(
          normalizedEndpoint,
          apiToken,
          order,
          requestPreferences,
          recommendationData,
        );
        const transientFailure = !response.ok && isTransientAutoPreparationFailure(response);
        const pendingCooking = didNormalOrderCookingStillPending(response);
        const startedCooking = didCompleteStep(response, '普客开始料理');
        const acknowledgedStart = startedCooking
          || pendingCooking
          || didAcknowledgeStep(response, '普客料理');
        const beverageHandledNow = didNormalOrderDeliverBeverage(response);
        const foodDeliveredNow = didNormalOrderDeliverFood(response);
        const completedNow = didNormalOrderComplete(response);
        const collected = false;
        const prepared = currentState.prepared || acknowledgedStart;
        const beverageHandled = currentState.beverageHandled || order.hasServedBeverage || beverageHandledNow;
        const foodDelivered = currentState.foodDelivered || order.hasServedFood || foodDeliveredNow;
        const completed = currentState.completed || order.hasEvaluated || completedNow;
        const rollbackCount = collected || pendingCooking || startedCooking || beverageHandledNow || foodDeliveredNow || completedNow
          ? 0
          : currentState.rollbackCount;
        const nextStep: AutomationStep = completed
          ? 'done'
          : foodDelivered || order.readyToEvaluate
            ? 'complete-order'
            : requestPreferences.autoNormalTakeBeverage && !beverageHandled
              ? 'ensure-beverage'
              : prepared && !foodDelivered
                ? 'deliver-food'
                : 'ensure-cooking';
        const nextState = updateAutomationAfterResponse(
          {
            ...currentState,
            orderKey,
            prepared,
            preparedAtMs: acknowledgedStart || (shouldRetryPrepared && transientFailure)
              ? now
              : prepared
                ? currentState.preparedAtMs
                : 0,
            beverageHandled,
            beverageHandledAtMs: beverageHandledNow && !currentState.beverageHandled ? now : currentState.beverageHandledAtMs,
            collected,
            foodDelivered,
            foodDeliveredAtMs: foodDeliveredNow && !currentState.foodDelivered ? now : currentState.foodDeliveredAtMs,
            completed,
            completedAtMs: completedNow && !currentState.completed ? now : currentState.completedAtMs,
            step: nextStep,
            rollbackCount,
          },
          response,
          now,
          nextStep,
          companionPreferences.autoNormalStopOnError,
          companionPreferences.autoMaxStepRetries,
        );
        const normalizedNextState = {
          ...nextState,
          beverageHandled,
          collected,
          foodDelivered,
          completed,
        };
        normalOrderStatesRef.current.set(orderKey, normalizedNextState);

        const prefix = `桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`;
        const suffix = normalizedNextState.paused
          ? '\n普客自动化已暂停该订单，订单变化或重新开启后会继续。'
          : transientFailure
            ? '\n当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        messages.push(`${prefix}\n${formatOrderPreparationResponse(response)}\n${formatAutomationState(normalizedNextState, companionPreferences)}${suffix}`);
      }
      refreshNormalOrderDiagnostics(orders, now);
      setNormalOrderMessage(messages.length > 0
        ? `普客自动化\n${messages.join('\n\n')}${schedulerMessages.length > 0 ? `\n\n${schedulerMessages.join('\n\n')}` : ''}`
        : '普客自动化\n当前没有需要执行的新步骤。');
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      if (companionPreferences.autoNormalStopOnError) {
        refreshNormalOrderDiagnostics(orders, now);
        setNormalOrderMessage(`普客自动化\n${message}\n普客自动化已暂停，订单变化或重新开启后会继续。`);
      } else {
        setNormalOrderMessage(`普客自动化\n${message}`);
      }
    } finally {
      normalOrderBusyRef.current = false;
      setNormalOrderBusy(false);
    }
  }, [
    apiToken,
    companionPreferences,
    getAutomationCookerCycle,
    normalizedEndpoint,
    recommendationData,
    refresh,
    refreshNormalOrderDiagnostics,
    runtime,
    snapshot?.normalBusiness?.orders,
  ]);

  useEffect(() => {
    persistTab(tab);
  }, [tab]);

  useEffect(() => {
    persistFocusCompact(serviceFocusCompact);
  }, [serviceFocusCompact]);

  useEffect(() => {
    persistFocusRecipeLimit(serviceFocusRecipeLimit);
  }, [serviceFocusRecipeLimit]);

  useEffect(() => {
    persistFocusBeverageLimit(serviceFocusBeverageLimit);
  }, [serviceFocusBeverageLimit]);

  useEffect(() => {
    persistCompanionPreferences(companionPreferences);
    applyCompanionVisualPreferences(companionPreferences);
  }, [companionPreferences]);

  useEffect(() => {
    void applyCompanionPreferencesToTauri(
      companionPreferences.focusSwitchBehavior,
      companionPreferences.alwaysOnTop,
      companionPreferences.focusSwitchCooldownMs,
      companionPreferences.mousePassthroughEnabled,
    );
  }, [
    companionPreferences.alwaysOnTop,
    companionPreferences.focusSwitchBehavior,
    companionPreferences.focusSwitchCooldownMs,
    companionPreferences.mousePassthroughEnabled,
  ]);

  useEffect(() => {
    if (!isTauriRuntime()) return undefined;

    let disposed = false;
    let unlisten: (() => void) | undefined;
    import('@tauri-apps/api/event')
      .then(async ({ listen }) => {
        unlisten = await listen<boolean>('mouse-passthrough-changed', (event) => {
          if (disposed) return;
          const mousePassthroughEnabled = Boolean(event.payload);
          setCompanionPreferences((current) => (
            current.mousePassthroughEnabled === mousePassthroughEnabled
              ? current
              : normalizeCompanionPreferences({ ...current, mousePassthroughEnabled })
          ));
        });
      })
      .catch(() => {
        // 浏览器开发模式和旧版伴随窗口不一定暴露该事件。
      });

    return () => {
      disposed = true;
      unlisten?.();
    };
  }, []);

  useEffect(() => {
    if (isTauriRuntime()) return undefined;

    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'F10') return;
      event.preventDefault();
      updateCompanionPreferences({
        mousePassthroughEnabled: !companionPreferences.mousePassthroughEnabled,
      });
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [
    companionPreferences.mousePassthroughEnabled,
    updateCompanionPreferences,
  ]);

  const handleAutomationDisabled = useCallback(() => {
    rareOrderStatesRef.current.clear();
    rareOrderDiagnosticItemsRef.current.clear();
    setRareOrderDiagnostics([]);
    normalOrderStatesRef.current.clear();
    setNormalOrderDiagnostics([]);
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    setAutoPrepPaused(false);
    setNormalOrderPausedCount(0);
  }, []);

  const handleNormalOrderSignatureChanged = useCallback(() => {
    lastAutoNormalOrderAtRef.current = 0;
  }, []);

  const handleNormalAutomationDisabled = useCallback(() => {
    normalOrderStatesRef.current.clear();
    setNormalOrderDiagnostics([]);
    lastAutoNormalOrderAtRef.current = 0;
    setNormalOrderPausedCount(0);
    setNormalOrderMessage('');
  }, []);

  useOrderAutomationIntervals({
    automationEnabled: companionPreferences.automationEnabled,
    autoNormalOrderEnabled: companionPreferences.autoNormalOrderEnabled,
    normalOrderSignature,
    rareTickMs: AUTO_FIRST_ORDER_TICK_MS,
    normalTickMs: AUTO_NORMAL_ORDER_TICK_MS,
    runAutoFirstOrder,
    runAutoNormalOrder,
    onAutomationDisabled: handleAutomationDisabled,
    onNormalOrderSignatureChanged: handleNormalOrderSignatureChanged,
    onNormalAutomationDisabled: handleNormalAutomationDisabled,
  });

  useGamepadNavigation({
    enabled: companionPreferences.gamepadNavigationEnabled,
    toggleCooldownMs: companionPreferences.focusSwitchCooldownMs,
    activeTab: tab,
    tabs: visibleTabs,
    focusMode: serviceFocusMode,
    onTabChange: setTab,
    onToggleWindow: () => {
      void toggleCompanionFocus(
        companionPreferences.focusSwitchBehavior,
        companionPreferences.focusSwitchCooldownMs,
      );
    },
    onEnterFocusMode: () => {
      setTab('service');
      setServiceFocusMode(true);
    },
    onExitFocusMode: () => setServiceFocusMode(false),
    onToggleCompactMode: () => setServiceFocusCompact((current) => !current),
  });

  if (serviceFocusMode) {
    return (
      <ServiceFocusPage
        recommendations={orderRecommendations.recommendations}
        recommendationIssues={orderRecommendations.recommendationIssues}
        runtimeSets={runtimeSets}
        dataIndexes={recommendationIndexes}
        favorites={favorites}
        customRecipes={customRecipes}
        favoriteBusyKey={favoriteBusyKey}
        favoriteError={favoriteError}
        orderSortMode={companionPreferences.serviceOrderSortMode}
        showDebugDetails={companionPreferences.showDebugDetails}
        compact={serviceFocusCompact}
        recipeLimit={serviceFocusRecipeLimit}
        beverageLimit={serviceFocusBeverageLimit}
        onCompactChange={setServiceFocusCompact}
        onRecipeLimitChange={setServiceFocusRecipeLimit}
        onBeverageLimitChange={setServiceFocusBeverageLimit}
        onToggleRecipeFavorite={toggleRecipeFavorite}
        onToggleBeverageFavorite={toggleBeverageFavorite}
        onExit={() => setServiceFocusMode(false)}
      />
    );
  }

  return (
    <div className="space-y-3">
      <WorkbenchHeader
        endpointDraft={endpointDraft}
        onEndpointDraftChange={setEndpointDraft}
        apiTokenDraft={apiTokenDraft}
        onApiTokenDraftChange={setApiTokenDraft}
        onApplyEndpointConnection={applyEndpointConnection}
        onPauseConnection={pauseConnection}
        onRefresh={() => void refresh(true)}
        apiToken={apiToken}
        connectionPaused={connectionPaused}
        connectionFailureCount={connectionFailureCount}
        error={error}
        lastConnectedAt={lastConnectedAt}
        loading={loading}
        normalizedEndpoint={normalizedEndpoint}
        mousePassthroughEnabled={companionPreferences.mousePassthroughEnabled}
        night={night}
        snapshot={snapshot}
      />

      <Tabs value={tab} onValueChange={(value) => setTab(value as ModTab)} className="space-y-3">
        <TabsList
          className="h-9 !w-full max-w-full justify-stretch overflow-x-auto overflow-y-hidden [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
          data-gamepad-scope="tabs"
        >
          <TabsTrigger value="overview" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="overview">
            概览
          </TabsTrigger>
          <TabsTrigger value="normal" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="normal">
            普客
          </TabsTrigger>
          <TabsTrigger value="rare" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="rare">
            稀客
          </TabsTrigger>
          <TabsTrigger value="custom-recipes" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="custom-recipes">
            自定义推荐料理
          </TabsTrigger>
          <TabsTrigger value="service" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="service">
            经营中
          </TabsTrigger>
          <TabsTrigger value="tasks" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="tasks">
            任务
          </TabsTrigger>
          <TabsTrigger value="inventory" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="inventory">
            修改
          </TabsTrigger>
          <TabsTrigger value="help" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="help">
            帮助
          </TabsTrigger>
          {companionPreferences.showDebugDetails && (
            <TabsTrigger value="logs" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="logs">
              日志
            </TabsTrigger>
          )}
          <TabsTrigger value="settings" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="settings">
            设置
          </TabsTrigger>
        </TabsList>

        <TabsContent value="overview" data-gamepad-scope="content">
          <ModOverviewPanel
            endpoint={normalizedEndpoint}
            snapshot={snapshot}
            runtime={runtime}
            night={night}
            data={recommendationData}
            indexes={recommendationIndexes}
            error={error}
            lastConnectedAt={lastConnectedAt}
            showDebugDetails={companionPreferences.showDebugDetails}
          />
        </TabsContent>

        <TabsContent value="normal" data-gamepad-scope="content">
          <ModNormalPanel
            runtime={runtime}
            runtimeSets={runtimeSets}
            selectedPlace={selectedPlace}
            detectedPlace={detectedPlace}
            data={recommendationData}
            onPlaceChange={setManualPlace}
            onFollowDetectedPlace={() => setManualPlace(null)}
          />
        </TabsContent>

        <TabsContent value="rare" data-gamepad-scope="content">
          <ModRarePanel
            runtime={runtime}
            runtimeSets={runtimeSets}
            runtimeRareCustomers={runtimeRareCustomers}
            selectedPlace={selectedPlace}
            detectedPlace={detectedPlace}
            data={recommendationData}
            rareCustomerId={rareCustomerId}
            requiredFoodTag={requiredFoodTag}
            requiredBeverageTag={requiredBeverageTag}
            favorites={favorites}
            customRecipes={customRecipes}
            favoriteBusyKey={favoriteBusyKey}
            favoriteError={favoriteError}
            preferences={companionPreferences}
            onPlaceChange={(place) => {
              setManualPlace(place);
              setRareCustomerId(null);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onFollowDetectedPlace={() => {
              setManualPlace(null);
              setRareCustomerId(null);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onRareCustomerChange={(customerId) => {
              setRareCustomerId(customerId);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onFoodTagChange={setRequiredFoodTag}
            onBeverageTagChange={setRequiredBeverageTag}
            onToggleRecipeFavorite={toggleRecipeFavorite}
            onToggleBeverageFavorite={toggleBeverageFavorite}
          />
        </TabsContent>

        <TabsContent value="custom-recipes" data-gamepad-scope="content">
          <ModCustomRecipesPanel
            apiToken={apiToken}
            customRecipes={customRecipes}
            customRecipeBusyKey={customRecipeBusyKey}
            customRecipeError={customRecipeError}
            runtimeSets={runtimeSets}
            runtimeRareCustomers={runtimeRareCustomers}
            data={recommendationData}
            onUpsertCustomRecipe={upsertCustomRecipeEntry}
            onRemoveCustomRecipe={removeCustomRecipeEntry}
            onToggleCustomRecipe={toggleCustomRecipeEntry}
            onMoveCustomRecipe={moveCustomRecipeEntry}
          />
        </TabsContent>

        <TabsContent value="service" data-gamepad-scope="content">
          <ModServicePanel
            runtime={runtime}
            night={night}
            detectedPlace={detectedPlace}
            recommendations={orderRecommendations.recommendations}
            recommendationIssues={orderRecommendations.recommendationIssues}
            data={recommendationData}
            performanceMs={snapshot?.performanceMs}
            runtimeSets={runtimeSets}
            uiPinningStatus={snapshot?.runtimeUiPinningStatus ?? ''}
            uiPinningTarget={gameUiPinningTarget}
            favorites={favorites}
            customRecipes={customRecipes}
            favoriteBusyKey={favoriteBusyKey}
            favoriteError={favoriteError}
            autoPrepBusy={autoPrepBusy}
            autoPrepMessage={autoPrepMessage}
            autoPrepPaused={autoPrepPaused}
            rareOrderDiagnostics={rareOrderDiagnostics}
            autoPrepPreferences={companionPreferences}
            recipeLimit={serviceFocusRecipeLimit}
            beverageLimit={serviceFocusBeverageLimit}
            normalOrderBusy={normalOrderBusy}
            normalOrderMessage={normalOrderMessage}
            normalOrderPausedCount={normalOrderPausedCount}
            normalOrderDiagnostics={normalOrderDiagnostics}
            onRecipeLimitChange={setServiceFocusRecipeLimit}
            onBeverageLimitChange={setServiceFocusBeverageLimit}
            onPreferenceChange={updateCompanionPreferences}
            onToggleRecipeFavorite={toggleRecipeFavorite}
            onToggleBeverageFavorite={toggleBeverageFavorite}
            onRetryRareAutomationOrder={retryRareAutomationOrder}
            onResetRareAutomationOrder={resetRareAutomationOrder}
            dismissRareOrderBusyKey={dismissRareOrderBusyKey}
            dismissRareOrderError={dismissRareOrderError}
            onDismissRareOrder={dismissRareOrder}
            onEnterFocusMode={() => setServiceFocusMode(true)}
            normalBusiness={snapshot?.normalBusiness ?? null}
            showDebugDetails={companionPreferences.showDebugDetails}
          />
        </TabsContent>

        <TabsContent value="tasks" data-gamepad-scope="content">
          <ModTasksPanel
            runtimeLoaded={snapshot?.runtimeLoaded ?? false}
            activeDayMapName={snapshot?.activeDayMapName ?? ''}
            activeDayMapLabel={snapshot?.activeDayMapLabel ?? ''}
            missions={snapshot?.runtimeMissions ?? null}
            data={recommendationData}
            inviteScope={rareGuestInvitationScope}
            inviteLevels={rareGuestInvitationLevels}
            inviteBusyKey={rareGuestInvitationBusyKey}
            inviteAllResult={rareGuestInvitationResult}
            inviteAllError={rareGuestInvitationError}
            showDebugDetails={companionPreferences.showDebugDetails}
            onInviteScopeChange={(scope) => {
              setRareGuestInvitationScope(scope);
            }}
            onInviteLevelsChange={(levels) => {
              setRareGuestInvitationLevels(normalizeRareGuestInvitationLevels(levels));
            }}
            onRefreshRareGuestInvitations={loadRareGuestInvitations}
            onInviteAllRareGuests={inviteAllRareGuests}
            onInviteRareGuest={inviteRareGuest}
          />
        </TabsContent>

        <TabsContent value="inventory" data-gamepad-scope="content">
          <ModInventoryPanel
            endpoint={normalizedEndpoint}
            apiToken={apiToken}
            runtimeSets={runtimeSets}
            runtimeLoaded={snapshot?.runtimeLoaded ?? false}
            data={recommendationData}
            onRefresh={refresh}
          />
        </TabsContent>

        <TabsContent value="help" data-gamepad-scope="content">
          <ModHelpPanel />
        </TabsContent>

        {companionPreferences.showDebugDetails && (
          <TabsContent value="logs" data-gamepad-scope="content">
            <ModLogsPanel endpoint={normalizedEndpoint} apiToken={apiToken} />
          </TabsContent>
        )}

        <TabsContent value="settings" data-gamepad-scope="content">
          <ModSettingsPanel
            endpoint={normalizedEndpoint}
            apiToken={apiToken}
            preferences={companionPreferences}
            data={recommendationData}
            runtimeSets={runtimeSets}
            themeMode={themeMode}
            serviceFocusCompact={serviceFocusCompact}
            onPreferenceChange={updateCompanionPreferences}
            onConnectionConfigApplied={applyConnectionDetails}
            onThemeModeChange={setThemeMode}
            onServiceFocusCompactChange={setServiceFocusCompact}
          />
        </TabsContent>
      </Tabs>
    </div>
  );
}

async function toggleCompanionFocus(
  focusSwitchBehavior: FocusSwitchBehavior,
  focusSwitchCooldownMs: number,
) {
  if (!isTauriRuntime()) return;

  try {
    const { invoke } = await import('@tauri-apps/api/core');
    await invoke('toggle_companion_focus', {
      keepVisibleWhenFocused: focusSwitchBehavior === 'keep-visible',
      windowSwitchCooldownMs: normalizeFocusSwitchCooldownMs(focusSwitchCooldownMs),
    });
  } catch {
    // 浏览器开发模式和旧版伴随窗口不一定暴露该 command。
  }
}
