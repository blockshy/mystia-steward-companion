import { BusinessConnectionProvider } from '@/companion/BusinessContext';
import { retryBusinessAutomation, useBusinessStatus } from '@/companion/hooks/useBusinessStatus';
import { buildNightBusinessOrderKey } from '@/companion/domain/automation';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useGamepadNavigation } from '@/companion/use-gamepad-navigation';
import {
  createEmptyCustomRecipeForm,
  type CustomRecipeFormState,
} from '@/companion/custom-recipe-editor';
import { WorkbenchHeader } from '@/companion/features/workbench/WorkbenchHeader';
import { UpdateNoticeBar } from '@/companion/features/updates/UpdateNoticeBar';
import { useUpdateManager } from '@/companion/features/updates/useUpdateManager';
import { useCompanionConnection } from '@/companion/hooks/useCompanionConnection';
import { useCompanionDeviceAuthority } from '@/companion/hooks/useCompanionDeviceAuthority';
import {
  buildAutomationLeaseConnectionKey,
  isAutomationLeaseOwnedForConnection,
} from '@/companion/connection-recovery';
import { useCustomRecipes } from '@/companion/hooks/useCustomRecipes';
import { useFavorites } from '@/companion/hooks/useFavorites';
import { getNightBusinessAutomationPauseMessage } from '@/companion/domain/automation-runtime';
import { useRareGuestInvitations } from '@/companion/hooks/useRareGuestInvitations';
import { useTrackedMissions } from '@/companion/hooks/useTrackedMissions';
import { useAvailableMissions } from '@/companion/hooks/useAvailableMissions';
import { ModCustomRecipesPanel } from '@/companion/pages/ModCustomRecipesPanel';
import { ModFavoritesPanel } from '@/companion/pages/ModFavoritesPanel';
import { ModInventoryPanel } from '@/companion/pages/ModInventoryPanel';
import { ModLogsPanel } from '@/companion/pages/ModLogsPanel';
import { ModMissionListPanel } from '@/companion/pages/ModMissionListPanel';
import { ModNormalPanel } from '@/companion/pages/ModNormalPanel';
import { ModOverviewPanel } from '@/companion/pages/ModOverviewPanel';
import { ModRarePanel } from '@/companion/pages/ModRarePanel';
import { ModRareGuestInvitationsPanel } from '@/companion/pages/ModRareGuestInvitationsPanel';
import {
  ModServicePanel,
  ServiceFocusPage,
  type ServicePanelView,
  type ServiceRecommendationTab,
} from '@/companion/pages/ModServicePanel';
import { ModSettingsPanel } from '@/companion/pages/ModSettingsPanel';
import {
  acknowledgeAutomationSafetyBarrier,
  acquireAutomationLease,
  dismissRuntimeRareOrder,
  releaseAutomationLease,
} from '@/companion/api';
import {
  normalizePlace,
} from '@/companion/domain/service-recommendations';
import { buildOrderRecommendationPresentation } from '@/companion/domain/order-recommendation-presentation';
import {
  applyCompanionPreferencesToTauri,
  applySharedCompanionPreferences,
  applyCompanionVisualPreferences,
  normalizeCompanionPreferences,
  normalizeFocusSwitchCooldownMs,
  persistCompanionPreferences,
  readStoredCompanionPreferences,
  readSharedCompanionPreferences,
  type CompanionPreferences,
  type SharedCompanionPreferences,
  type FocusSwitchBehavior,
} from '@/companion/preferences';
import {
  normalizeRareGuestInvitationLevels,
  persistCustomRecipeGroupMode,
  persistFocusBeverageLimit,
  persistFocusCompact,
  persistFocusRecipeLimit,
  persistMissionListModuleEnabled,
  persistRareGuestInvitationModuleEnabled,
  persistTab,
  readStoredCustomRecipeGroupMode,
  readStoredFocusBeverageLimit,
  readStoredFocusCompact,
  readStoredFocusRecipeLimit,
  readStoredMissionListModuleEnabled,
  readStoredRareGuestInvitationModuleEnabled,
  readStoredTab,
} from '@/companion/storage';
import type {
  AutomationSafetyBarrierAckResponse,
  AutomationSafetyBarrierDiagnostic,
  CompanionDevicePlatform,
  CustomRecipeGroupMode,
  ExtensionTab,
  LocalApiAutomationLease,
  ModTab,
  NightBusinessOrder,
  RecommendationTab,
  SettingsTab,
} from '@/companion/types';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import {
  buildRecommendationDataIndexes,
  buildRecommendationDataSet,
} from '@/lib/recommendation-data';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import { useThemeMode } from '@/lib/theme';
import type { PlaceName } from '@/lib/catalog-types';
import { INNER_TAB_TRIGGER_CLASS } from '@/companion/pages/shared-constants';

const AUTOMATION_LEASE_RENEW_INTERVAL_MS = 3000;
const MOD_TAB_TRIGGER_CLASS = 'min-w-[4.75rem] flex-none min-[640px]:w-full min-[640px]:min-w-0';
type CompanionPlatform = 'desktop' | 'mobile';

const MOD_TABS: ModTab[] = [
  'overview',
  'recommendations',
  'service',
  'extensions',
  'logs',
  'settings',
];
const BASIC_MOD_TABS: ModTab[] = MOD_TABS.filter((tab) => tab !== 'logs');
interface AutomationLeaseAcquireEntry {
  key: string;
  promise: Promise<LocalApiAutomationLease>;
}

interface AutomationControlReleaseEntry {
  transition: number;
  promise: Promise<void>;
}

function buildAutomationControlSignature(preferences: SharedCompanionPreferences): string {
  const switches = [
    preferences.automationEnabled,
    preferences.autoRareOrderEnabled,
    preferences.rareGuestParticipationModuleEnabled,
    preferences.autoPrepTakeBeverage,
    preferences.autoPrepStartCooking,
    preferences.autoPrepCollectCooking,
    preferences.autoPrepCompleteOrder,
    preferences.autoNormalOrderEnabled,
    preferences.autoNormalTakeBeverage,
    preferences.autoNormalStartCooking,
    preferences.autoNormalDeliverFood,
    preferences.autoNormalCompleteOrder,
  ].map((value) => (value ? '1' : '0')).join('');
  // 名单修改与开关修改同样撤销旧租约，等待主设备配置落盘后再申请新的执行权。
  return `${switches}|${preferences.managedRareGuestIds.join(',')}`;
}

interface AutomationBarrierAckEntry {
  key: string;
  sessionId: string;
  sequence: number;
}

function automationBarrierAckFailure(sequence: number, error: string): AutomationSafetyBarrierAckResponse {
  return {
    ok: false,
    sequence,
    acknowledgedCount: 0,
    acknowledgedSequences: [],
    status: '',
    error,
  };
}

export function ModWorkbench() {
  const { mode: themeMode, setMode: setThemeMode } = useThemeMode();
  const [tab, setTab] = useState<ModTab>(() => readStoredTab());
  const [recommendationTab, setRecommendationTab] = useState<RecommendationTab>('normal');
  const [extensionTab, setExtensionTab] = useState<ExtensionTab>('missions');
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('window');
  const [missionListModuleEnabled, setMissionListModuleEnabled] = useState(
    readStoredMissionListModuleEnabled,
  );
  const [rareGuestInvitationModuleEnabled, setRareGuestInvitationModuleEnabled] = useState(
    readStoredRareGuestInvitationModuleEnabled,
  );
  const [serviceFocusMode, setServiceFocusMode] = useState(false);
  const [serviceFocusCompact, setServiceFocusCompact] = useState(readStoredFocusCompact);
  const [serviceFocusRecipeLimit, setServiceFocusRecipeLimit] = useState(readStoredFocusRecipeLimit);
  const [serviceFocusBeverageLimit, setServiceFocusBeverageLimit] = useState(readStoredFocusBeverageLimit);
  const [customRecipeGroupMode, setCustomRecipeGroupMode] = useState<CustomRecipeGroupMode>(
    readStoredCustomRecipeGroupMode,
  );
  const [customRecipeForm, setCustomRecipeForm] = useState<CustomRecipeFormState>(createEmptyCustomRecipeForm);
  const [companionPreferences, setCompanionPreferences] = useState<CompanionPreferences>(() =>
    readStoredCompanionPreferences(),
  );
  const [companionPlatform, setCompanionPlatform] = useState<CompanionPlatform>('desktop');
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
    connectionRevision,
    lastConnectedAt,
    normalizedEndpoint,
    applyEndpointConnection,
    applyConnectionDetails,
    pauseConnection,
    refresh,
  } = useCompanionConnection(snapshotRefreshIntervalMs);
  const companionConnected = Boolean(apiToken && !connectionPaused && !error && snapshot);
  const {
    favorites,
    favoriteError,
    favoriteBusyKey,
    favoriteRefreshing,
    refreshFavorites,
    toggleRecipeFavorite,
    toggleBeverageFavorite,
    removeRecipeFavoriteById,
    removeBeverageFavoriteById,
  } = useFavorites({ apiToken, connectionPaused, normalizedEndpoint });
  const {
    customRecipes,
    customRecipeError,
    customRecipeBusyKey,
    upsertCustomRecipeEntry,
    removeCustomRecipeEntry,
    setCustomRecipesEnabledState,
    updateCustomRecipeFlagsState,
    moveCustomRecipeEntry,
  } = useCustomRecipes({ apiToken, connectionPaused, normalizedEndpoint });
  const updateManager = useUpdateManager({
    endpoint: normalizedEndpoint,
    apiToken,
    connectionRevision,
    connected: companionConnected,
  });
  const customRecipeDraftEndpointRef = useRef(normalizedEndpoint);

  useEffect(() => {
    if (customRecipeDraftEndpointRef.current === normalizedEndpoint) return;
    customRecipeDraftEndpointRef.current = normalizedEndpoint;
    setCustomRecipeForm(createEmptyCustomRecipeForm());
  }, [normalizedEndpoint]);

  const updateCustomRecipeGroupMode = useCallback((mode: CustomRecipeGroupMode) => {
    setCustomRecipeGroupMode(mode);
    persistCustomRecipeGroupMode(mode);
  }, []);
  const missionListVisible = tab === 'extensions' && extensionTab === 'missions';
  const rareGuestInvitationVisible = tab === 'extensions' && extensionTab === 'rare-invitations';
  const {
    trackedMissions,
    trackedMissionsError,
    trackedMissionsLoading,
    refreshTrackedMissions,
  } = useTrackedMissions({
    active: missionListModuleEnabled && missionListVisible,
    apiToken,
    connected: companionConnected,
    connectionRevision,
    normalizedEndpoint,
  });
  const {
    availableMissions,
    availableMissionsError,
    availableMissionsLoading,
    refreshAvailableMissions,
  } = useAvailableMissions({
    active: missionListModuleEnabled && missionListVisible,
    apiToken,
    connected: companionConnected,
    connectionRevision,
    missionGeneration: snapshot?.missionGeneration ?? 0,
    normalizedEndpoint,
  });
  const {
    rareGuestInvitationScope,
    setRareGuestInvitationScope,
    rareGuestInvitationLevels,
    setRareGuestInvitationLevels,
    rareGuestInvitationResult,
    rareGuestInvitationError,
    rareGuestInvitationBusyKey,
    rareGuestInvitationContextReady,
    rareGuestInvitationWriteBusy,
    loadRareGuestInvitations,
    inviteAllRareGuests,
    inviteRareGuest,
  } = useRareGuestInvitations({
    apiToken,
    connected: companionConnected,
    connectionRevision,
    enabled: rareGuestInvitationModuleEnabled,
    normalizedEndpoint,
    refresh,
    snapshot,
    visible: rareGuestInvitationVisible,
  });
  const updateMissionListModuleEnabled = useCallback((enabled: boolean) => {
    setMissionListModuleEnabled(enabled);
    persistMissionListModuleEnabled(enabled);
  }, []);
  const updateRareGuestInvitationModuleEnabled = useCallback((enabled: boolean) => {
    if (!enabled && rareGuestInvitationWriteBusy) return;
    setRareGuestInvitationModuleEnabled(enabled);
    persistRareGuestInvitationModuleEnabled(enabled);
  }, [rareGuestInvitationWriteBusy]);
  const [manualPlace, setManualPlace] = useState<PlaceName | null>(null);
  const [rareCustomerId, setRareCustomerId] = useState<number | null>(null);
  const [requiredFoodTag, setRequiredFoodTag] = useState('');
  const [requiredBeverageTag, setRequiredBeverageTag] = useState('');
  const [dismissRareOrderBusyKey, setDismissRareOrderBusyKey] = useState('');
  const [dismissRareOrderError, setDismissRareOrderError] = useState('');
  // 客户端仅保存交互反馈和租约连接状态；订单状态机由 C# 宿主唯一维护。
  const [automationActionMessage, setAutomationActionMessage] = useState('');
  const [serviceView, setServiceView] = useState<ServicePanelView>('recommendations');
  const [serviceRecommendationTab, setServiceRecommendationTab] = useState<ServiceRecommendationTab>('rare');
  const [automationLease, setAutomationLease] = useState<LocalApiAutomationLease | null>(null);
  const [automationLeaseBindingKey, setAutomationLeaseBindingKey] = useState('');
  const [automationLeaseError, setAutomationLeaseError] = useState('');
  const [automationBarrierAckBusyKey, setAutomationBarrierAckBusyKey] = useState('');
  const [automationBarrierAckErrors, setAutomationBarrierAckErrors] = useState<Record<number, string>>({});
  const companionPreferencesRef = useRef(companionPreferences);
  const automationLeaseAcquireRef = useRef<AutomationLeaseAcquireEntry | null>(null);
  const previousAutomationControlSignatureRef = useRef('');
  const automationControlTransitionRef = useRef(0);
  const automationControlReleaseRef = useRef<AutomationControlReleaseEntry | null>(null);
  const automationControlReleasePendingRef = useRef(false);
  const [automationControlReleasePending, setAutomationControlReleasePending] = useState(false);
  const automationLeaseRevalidationRequiredRef = useRef(true);
  const automationStateSessionIdRef = useRef('');
  const automationLeaseOwnedRef = useRef(false);
  const automationBarrierAckRef = useRef<AutomationBarrierAckEntry | null>(null);

  const applyAuthoritativeSharedPreferences = useCallback((profile: SharedCompanionPreferences) => {
    setCompanionPreferences((current) => {
      const next = applySharedCompanionPreferences(current, profile);
      companionPreferencesRef.current = next;
      persistCompanionPreferences(next);
      return next;
    });
  }, []);
  const sharedCompanionPreferences = useMemo(
    () => readSharedCompanionPreferences(companionPreferences),
    [companionPreferences],
  );
  const automationControlSignature = useMemo(
    () => buildAutomationControlSignature(sharedCompanionPreferences),
    [sharedCompanionPreferences],
  );
  const companionDevicePlatform: CompanionDevicePlatform = !isTauriRuntime()
    ? 'browser'
    : companionPlatform === 'mobile' ? 'android' : 'windows';
  const companionDeviceAuthority = useCompanionDeviceAuthority({
    endpoint: normalizedEndpoint,
    apiToken,
    connected: companionConnected,
    connectionRevision,
    platform: companionDevicePlatform,
    appVersion: __APP_VERSION__,
    sharedPreferences: sharedCompanionPreferences,
    applySharedPreferences: applyAuthoritativeSharedPreferences,
  });

  const updateCompanionPreferences = useCallback((next: Partial<CompanionPreferences>) => {
    const current = companionPreferencesRef.current;
    let normalized = normalizeCompanionPreferences({ ...current, ...next });
    if (companionConnected && !companionDeviceAuthority.currentDeviceIsPrimary) {
      normalized = applySharedCompanionPreferences(
        normalized,
        companionDeviceAuthority.state?.activeProfile ?? readSharedCompanionPreferences(current),
      );
    }
    companionPreferencesRef.current = normalized;
    setCompanionPreferences(normalized);
  }, [
    companionConnected,
    companionDeviceAuthority.currentDeviceIsPrimary,
    companionDeviceAuthority.state?.activeProfile,
  ]);

  useEffect(() => {
    if (!companionPreferences.showDebugDetails && tab === 'logs') {
      setTab('overview');
    }
  }, [companionPreferences.showDebugDetails, tab]);

  useEffect(() => {
    if (!isTauriRuntime()) return;
    let cancelled = false;
    import('@tauri-apps/api/core')
      .then(({ invoke }) => invoke<string>('companion_platform'))
      .then((platform) => {
        if (!cancelled) setCompanionPlatform(platform === 'mobile' ? 'mobile' : 'desktop');
      })
      .catch(() => {
        if (!cancelled) setCompanionPlatform('desktop');
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const runtime = snapshot?.recommendationState ?? null;
  const connectionReadyForActions = Boolean(
    apiToken
    && !connectionPaused
    && !error
    && snapshot
    && companionDeviceAuthority.runtimeWriterReady,
  );
  if (!connectionReadyForActions) automationLeaseRevalidationRequiredRef.current = true;
  const automationSessionId = snapshot?.automationSessionId.trim() ?? '';
  const automationLeaseConnectionKey = buildAutomationLeaseConnectionKey(
    { endpoint: normalizedEndpoint, apiToken },
    automationSessionId,
    companionDeviceAuthority.authorityRevision,
  );
  const automationLeaseOwned = isAutomationLeaseOwnedForConnection(
    automationLease,
    automationLeaseBindingKey,
    automationLeaseConnectionKey,
    automationLeaseRevalidationRequiredRef.current,
  );
  automationLeaseOwnedRef.current = automationLeaseOwned;
  const nightBusinessAutomationAllowed = snapshot?.nightBusinessAutomationAllowed === true;
  const nightBusinessAutomationBlockReason = snapshot?.nightBusinessAutomationBlockReason ?? '';
  const automationRuntimePauseMessage = getNightBusinessAutomationPauseMessage(
    nightBusinessAutomationBlockReason,
  );
  const night = snapshot?.nightBusiness ?? null;
  const detectedPlace = normalizePlace(night?.place);
  const selectedPlace = manualPlace ?? detectedPlace;
  const effectiveRuntimeData = cachedRuntimeData;
  // 运行时目录数据较大，完整目录通过 /runtime-data 按签名单独缓存，避免进入高频快照热路径。
  const recommendationData = useMemo(
    () => buildRecommendationDataSet(effectiveRuntimeData),
    [effectiveRuntimeData],
  );
  const recommendationIndexes = useMemo(
    () => buildRecommendationDataIndexes(recommendationData),
    [recommendationData],
  );
  const acquireAutomationLeaseSingleFlight = useCallback((): Promise<LocalApiAutomationLease> => {
    const key = automationLeaseConnectionKey;
    if (!key) return Promise.reject(new Error('自动化运行实例尚未就绪。'));
    const current = automationLeaseAcquireRef.current;
    if (current?.key === key) return current.promise;

    const promise = current
      ? current.promise.catch(() => undefined).then(() => acquireAutomationLease(
          normalizedEndpoint,
          apiToken,
          companionDeviceAuthority.authorityRevision,
        ))
      : acquireAutomationLease(
          normalizedEndpoint,
          apiToken,
          companionDeviceAuthority.authorityRevision,
        );
    const entry: AutomationLeaseAcquireEntry = { key, promise };
    automationLeaseAcquireRef.current = entry;
    const clearEntry = () => {
      if (automationLeaseAcquireRef.current === entry) automationLeaseAcquireRef.current = null;
    };
    void promise.then(clearEntry, clearEntry);
    return promise;
  }, [
    apiToken,
    automationLeaseConnectionKey,
    companionDeviceAuthority.authorityRevision,
    normalizedEndpoint,
  ]);

  const waitForAutomationLeaseAcquire = useCallback(async (): Promise<void> => {
    while (automationLeaseAcquireRef.current) {
      const entry = automationLeaseAcquireRef.current;
      try {
        await entry.promise;
      } catch {
        // A control-state release still has to run after a failed acquire attempt.
      }
      if (automationLeaseAcquireRef.current === entry) return;
    }
  }, []);

  useEffect(() => {
    const previousSignature = previousAutomationControlSignatureRef.current;
    previousAutomationControlSignatureRef.current = automationControlSignature;
    if (!previousSignature || previousSignature === automationControlSignature) return undefined;

    const transition = automationControlTransitionRef.current + 1;
    automationControlTransitionRef.current = transition;
    automationControlReleasePendingRef.current = true;
    setAutomationControlReleasePending(true);
    automationLeaseRevalidationRequiredRef.current = true;
    setAutomationLease(null);
    setAutomationLeaseBindingKey('');

    if (!apiToken
      || !companionConnected
      || !companionDeviceAuthority.currentDeviceIsPrimary
      || companionDeviceAuthority.authorityRevision <= 0) {
      automationControlReleasePendingRef.current = false;
      setAutomationControlReleasePending(false);
      return undefined;
    }

    const previousRelease = automationControlReleaseRef.current?.promise;
    const release = (async () => {
      try {
        if (previousRelease) await previousRelease;
        await waitForAutomationLeaseAcquire();
        const response = await releaseAutomationLease(
          normalizedEndpoint,
          apiToken,
          companionDeviceAuthority.authorityRevision,
        );
        if (!response.ok) {
          throw new Error(response.error || 'Mod 未确认自动化控制权释放。');
        }
        if (automationControlTransitionRef.current === transition) setAutomationLeaseError('');
      } catch (err) {
        if (automationControlTransitionRef.current === transition) {
          setAutomationLeaseError(err instanceof Error ? err.message : String(err));
        }
      } finally {
        if (automationControlTransitionRef.current === transition) {
          if (automationControlReleaseRef.current?.transition === transition) {
            automationControlReleaseRef.current = null;
          }
          automationControlReleasePendingRef.current = false;
          setAutomationControlReleasePending(false);
        }
      }
    })();
    automationControlReleaseRef.current = { transition, promise: release };

    return undefined;
  }, [
    apiToken,
    automationControlSignature,
    companionConnected,
    companionDeviceAuthority.authorityRevision,
    companionDeviceAuthority.currentDeviceIsPrimary,
    normalizedEndpoint,
    waitForAutomationLeaseAcquire,
  ]);

  useEffect(() => {
    if (!companionPreferences.automationEnabled
      || !connectionReadyForActions
      || !automationLeaseConnectionKey
      || automationControlReleasePending) return undefined;

    let cancelled = false;
    const renewLease = async () => {
      if (automationControlReleasePendingRef.current) return;
      try {
        const nextLease = await acquireAutomationLeaseSingleFlight();
        if (cancelled) return;
        automationLeaseRevalidationRequiredRef.current = false;
        setAutomationLease(nextLease);
        setAutomationLeaseBindingKey(nextLease.owned ? automationLeaseConnectionKey : '');
        setAutomationLeaseError(nextLease.owned ? '' : nextLease.error || '自动化控制权当前不可用。');
      } catch (err) {
        if (cancelled) return;
        automationLeaseRevalidationRequiredRef.current = true;
        setAutomationLease(null);
        setAutomationLeaseBindingKey('');
        setAutomationLeaseError(err instanceof Error ? err.message : String(err));
      }
    };

    void renewLease();
    const timer = window.setInterval(() => {
      void renewLease();
    }, AUTOMATION_LEASE_RENEW_INTERVAL_MS);

    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [
    apiToken,
    acquireAutomationLeaseSingleFlight,
    automationLeaseConnectionKey,
    automationControlReleasePending,
    companionPreferences.automationEnabled,
    connectionReadyForActions,
    normalizedEndpoint,
  ]);

  const visibleTabs = companionPreferences.showDebugDetails ? MOD_TABS : BASIC_MOD_TABS;
  const includeNormalOrderDetails = tab === 'service' && serviceView === 'recommendations' && serviceRecommendationTab === 'normal';
  const snapshotSignature = snapshot?.snapshotSignature ?? '';
  const business = useBusinessStatus(normalizedEndpoint, apiToken, snapshotSignature, companionConnected);
  const refreshBusiness = business.refresh;
  const runtimeSets = business.runtimeSets;
  const orderRecommendations = business.recommendations;
  const rareParticipationMessage = business.isCurrent
    ? (orderRecommendations.rareGuestParticipation ?? business.automation.rareGuestParticipation)?.message ?? '' : '';
  const orderRecommendationPresentation = useMemo(() => buildOrderRecommendationPresentation({
    orders: night?.orders ?? [],
    recommendations: orderRecommendations.recommendations,
    recommendationIssues: orderRecommendations.recommendationIssues,
    pending: business.pending,
    isCurrent: business.isCurrent,
    resultContextSignature: business.sourceSnapshotSignature ?? '',
    currentContextSignature: snapshotSignature,
    error: business.error,
    retainedAfterError: Boolean(business.error && orderRecommendations.recommendations.length),
  }), [business.error, business.isCurrent, business.pending, business.sourceSnapshotSignature, night?.orders, orderRecommendations, snapshotSignature]);
  const visibleOrderRecommendations = orderRecommendationPresentation.recommendations;
  const visibleOrderRecommendationIssues = orderRecommendationPresentation.recommendationIssues;
  const visibleOrderRecommendationPendingOrders = business.error ? [] : orderRecommendationPresentation.pendingOrders;
  const visibleOrderRecommendationsUpdating = !business.error && orderRecommendationPresentation.updating;
  const visibleOrderRecommendationUpdateError = business.error || orderRecommendationPresentation.updateError;
  const orderRecommendationPerformanceMs = orderRecommendations.performanceMs;
  const gameUiTargetSlots = business.gameUiTargets;
  const rareOrderDiagnostics = business.automation.rareDiagnostics;
  const normalOrderDiagnostics = business.automation.normalDiagnostics;
  const autoPrepBusy = business.automation.rareBusy;
  const normalOrderBusy = business.automation.normalBusy;
  const autoPrepPaused = rareOrderDiagnostics.some((item) => item.paused);
  const normalOrderPausedCount = normalOrderDiagnostics.filter((item) => item.paused).length;
  const automationSafetyBarriers = useMemo<AutomationSafetyBarrierDiagnostic[]>(() => business.automation.safetyBarriers.map((item) => ({
    ...item, error: automationBarrierAckErrors[item.sequence] ?? '',
  })), [automationBarrierAckErrors, business.automation.safetyBarriers]);
  // 连接状态只决定界面提示；是否允许继续执行仍由服务端租约及权威轮次校验决定。
  const automationConnectionMessage = !companionPreferences.automationEnabled ? ''
    : !connectionReadyForActions ? '自动化\n连接不可用，已暂停执行。'
      : automationLeaseError ? `自动化控制权\n${automationLeaseError}`
        : !automationLeaseOwned ? '自动化控制权\n当前窗口未持有控制权，仅查看服务端状态。'
          : automationRuntimePauseMessage ? `自动化\n${automationRuntimePauseMessage}` : '';
  const autoPrepMessage = automationConnectionMessage || automationActionMessage || rareParticipationMessage || business.automation.message || business.error || '';
  const normalOrderMessage = automationConnectionMessage || automationActionMessage;

  useEffect(() => {
    const previousSessionId = automationStateSessionIdRef.current;
    automationStateSessionIdRef.current = automationSessionId;
    if (!previousSessionId || previousSessionId === automationSessionId) return;
    automationBarrierAckRef.current = null;
    setAutomationBarrierAckBusyKey('');
    setAutomationBarrierAckErrors({});
    setAutomationActionMessage('');
    setAutomationLease(null);
    setAutomationLeaseBindingKey('');
  }, [automationSessionId]);

  const requestAutomationBarrierAck = useCallback(async (
    busyKey: string,
    sequence: number,
  ): Promise<AutomationSafetyBarrierAckResponse> => {
    const sessionId = automationStateSessionIdRef.current;
    if (sequence <= 0) {
      return automationBarrierAckFailure(sequence, '该订单没有可确认的安全栅栏 sequence。');
    }
    if (!sessionId || !automationLeaseOwnedRef.current) {
      return automationBarrierAckFailure(sequence, '当前未持有本游戏实例的自动化控制权，不能确认安全栅栏。');
    }
    if (automationBarrierAckRef.current) {
      return automationBarrierAckFailure(sequence, '另一笔安全栅栏确认正在处理中，请稍后重试。');
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
      if (automationStateSessionIdRef.current !== sessionId) {
        return automationBarrierAckFailure(sequence, '游戏自动化实例已切换，旧 sequence 的确认结果已作废。');
      }
      if (!response.ok) {
        return automationBarrierAckFailure(sequence, response.error || 'Mod 未确认安全栅栏 ACK。');
      }
      if (response.sequence !== sequence || response.acknowledgedCount <= 0) {
        return automationBarrierAckFailure(sequence, 'Mod 返回的安全栅栏 ACK 与当前 sequence 不一致。');
      }
      if (!response.acknowledgedSequences.includes(sequence)
        || response.acknowledgedSequences.length !== response.acknowledgedCount) {
        return automationBarrierAckFailure(sequence, 'Mod 返回的安全栅栏 ACK 序号集合无效。');
      }
      return response;
    } catch (err) {
      return automationBarrierAckFailure(sequence, err instanceof Error ? err.message : String(err));
    } finally {
      if (automationBarrierAckRef.current === entry) {
        automationBarrierAckRef.current = null;
        setAutomationBarrierAckBusyKey('');
      }
    }
  }, [apiToken, companionDeviceAuthority.authorityRevision, normalizedEndpoint]);


  /** 人工确认仅提交事件序号；本地不解除屏障，等待 Mod 回填后的权威诊断。 */
  const confirmAutomationBarrier = useCallback((busyKey: string, sequence: number) => {
    void (async () => {
      const response = await requestAutomationBarrierAck(busyKey, sequence);
      if (!response.ok) {
        const message = response.error || 'Mod 未确认安全屏障。';
        setAutomationBarrierAckErrors((current) => ({ ...current, [sequence]: message }));
        setAutomationActionMessage(`自动化\n${message}`);
        return;
      }
      setAutomationActionMessage(response.status || `事件 #${sequence} 的安全屏障已确认。`);
      refreshBusiness();
      void refresh(true);
    })();
  }, [refreshBusiness, refresh, requestAutomationBarrierAck]);
  const acknowledgeAutomationBarrierEvent = useCallback((sequence: number) => {
    confirmAutomationBarrier(`barrier:${sequence}`, sequence);
  }, [confirmAutomationBarrier]);
  const resetRareAutomationOrder = useCallback((key: string) => {
    confirmAutomationBarrier(`rare:${key}`, business.automation.states[`rare:${key}`]?.lastRuntimeEventSequence ?? 0);
  }, [business.automation.states, confirmAutomationBarrier]);
  const resetNormalAutomationOrder = useCallback((key: string) => {
    confirmAutomationBarrier(`normal:${key}`, business.automation.states[`normal:${key}`]?.lastRuntimeEventSequence ?? 0);
  }, [business.automation.states, confirmAutomationBarrier]);

  /** 重试只表达人工意图，目标锁定、预算恢复与任务准入全部由服务端完成。 */
  const retryAutomationOrder = useCallback((kind: 'rare' | 'normal', key: string) => {
    void (async () => {
      try {
        await retryBusinessAutomation(normalizedEndpoint, apiToken, companionDeviceAuthority.authorityRevision, kind, key);
        setAutomationActionMessage('已提交重试，等待服务端重新判断。');
        refreshBusiness();
      } catch (err) {
        setAutomationActionMessage(err instanceof Error ? err.message : String(err));
      }
    })();
  }, [apiToken, refreshBusiness, companionDeviceAuthority.authorityRevision, normalizedEndpoint]);
  const retryRareAutomationOrder = useCallback((key: string) => retryAutomationOrder('rare', key), [retryAutomationOrder]);
  const retryNormalAutomationOrder = useCallback((key: string) => retryAutomationOrder('normal', key), [retryAutomationOrder]);

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
    companionPreferencesRef.current = companionPreferences;
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

  useGamepadNavigation({
    enabled: companionPreferences.gamepadNavigationEnabled,
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
      <BusinessConnectionProvider endpoint={normalizedEndpoint} apiToken={apiToken} snapshotSignature={snapshotSignature} enabled={companionConnected}>
      <ServiceFocusPage
        recommendations={visibleOrderRecommendations}
        recommendationIssues={visibleOrderRecommendationIssues}
        recommendationPendingOrders={visibleOrderRecommendationPendingOrders}
        recommendationsPending={visibleOrderRecommendationsUpdating}
        recommendationUpdateError={visibleOrderRecommendationUpdateError}
        runtimeSets={runtimeSets}
        dataIndexes={recommendationIndexes}
        favorites={favorites}
        customRecipes={customRecipes}
        favoriteBusyKey={favoriteBusyKey}
        favoriteError={favoriteError}
        orderSortMode={companionPreferences.serviceOrderSortMode}
        specialBusiness={snapshot?.specialBusiness ?? null}
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
      </BusinessConnectionProvider>
    );
  }

  return (
    <BusinessConnectionProvider endpoint={normalizedEndpoint} apiToken={apiToken} snapshotSignature={snapshotSignature} enabled={companionConnected}>
    <div className="space-y-3" data-companion-surface="workbench">
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
        mousePassthroughEnabled={companionPlatform === 'desktop' && companionPreferences.mousePassthroughEnabled}
        night={night}
        snapshot={snapshot}
      />

      <UpdateNoticeBar
        manager={updateManager}
        onViewUpdate={() => {
          setSettingsTab('updates');
          setTab('settings');
        }}
      />

      <Tabs value={tab} onValueChange={(value) => setTab(value as ModTab)} className="space-y-3">
        <TabsList
          scrollable
          className="steward-primary-tabs-list h-9 !w-full max-w-full justify-stretch"
          data-gamepad-scope="tabs"
          style={{ gridTemplateColumns: `repeat(${visibleTabs.length}, minmax(0, 1fr))` }}
        >
          <TabsTrigger value="overview" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="overview">
            概览
          </TabsTrigger>
          <TabsTrigger value="recommendations" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="recommendations">
            推荐料理
          </TabsTrigger>
          <TabsTrigger value="service" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="service">
            经营中
          </TabsTrigger>
          <TabsTrigger value="extensions" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="extensions">
            扩展功能
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
          {tab === 'overview' && (
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
          )}
        </TabsContent>

        <TabsContent value="recommendations" data-gamepad-scope="content">
          {tab === 'recommendations' && (
            <Tabs
              value={recommendationTab}
              onValueChange={(value) => setRecommendationTab(value as RecommendationTab)}
              className="space-y-4"
            >
              <TabsList scrollable className="grid h-9 w-full grid-cols-4" data-recommendation-tabs="true">
                <TabsTrigger value="normal" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  普客
                </TabsTrigger>
                <TabsTrigger value="rare" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  稀客
                </TabsTrigger>
                <TabsTrigger value="custom-recipes" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  自定义推荐料理
                </TabsTrigger>
                <TabsTrigger value="favorites" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  收藏管理
                </TabsTrigger>
              </TabsList>

              <TabsContent value="normal" className="space-y-4">
                {recommendationTab === 'normal' && (
                  <ModNormalPanel
                    runtime={runtime}
                    runtimeSets={runtimeSets}
                    selectedPlace={selectedPlace}
                    detectedPlace={detectedPlace}
                    data={recommendationData}
                    active
                    onPlaceChange={setManualPlace}
                    onFollowDetectedPlace={() => setManualPlace(null)}
                  />
                )}
              </TabsContent>

              <TabsContent value="rare" className="space-y-4">
                {recommendationTab === 'rare' && (
                  <ModRarePanel
                    runtime={runtime}
                    runtimeSets={runtimeSets}
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
                    active
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
                )}
              </TabsContent>

              <TabsContent value="custom-recipes" className="space-y-4">
                {recommendationTab === 'custom-recipes' && (
                  <ModCustomRecipesPanel
                    apiToken={apiToken}
                    customRecipes={customRecipes}
                    customRecipeBusyKey={customRecipeBusyKey}
                    customRecipeError={customRecipeError}
                    form={customRecipeForm}
                    groupMode={customRecipeGroupMode}
                    runtimeSets={runtimeSets}
                    data={recommendationData}
                    onUpsertCustomRecipe={upsertCustomRecipeEntry}
                    onRemoveCustomRecipe={removeCustomRecipeEntry}
                    onSetCustomRecipesEnabled={setCustomRecipesEnabledState}
                    onUpdateCustomRecipeFlags={updateCustomRecipeFlagsState}
                    onMoveCustomRecipe={moveCustomRecipeEntry}
                    onFormChange={setCustomRecipeForm}
                    onGroupModeChange={updateCustomRecipeGroupMode}
                  />
                )}
              </TabsContent>

              <TabsContent value="favorites" className="space-y-4">
                {recommendationTab === 'favorites' && (
                  <ModFavoritesPanel
                    apiToken={apiToken}
                    favorites={favorites}
                    favoriteBusyKey={favoriteBusyKey}
                    favoriteError={favoriteError}
                    favoriteRefreshing={favoriteRefreshing}
                    data={recommendationData}
                    onRefresh={refreshFavorites}
                    onRemoveRecipe={removeRecipeFavoriteById}
                    onRemoveBeverage={removeBeverageFavoriteById}
                  />
                )}
              </TabsContent>
            </Tabs>
          )}
        </TabsContent>

        <TabsContent value="service" data-gamepad-scope="content">
          {tab === 'service' && (
            <ModServicePanel
              runtime={runtime}
              nightBusinessActive={snapshot?.nightBusinessLifecyclePhase === 'Active'
                || snapshot?.nightBusinessLifecyclePhase === 'Closing'}
              night={night}
              specialBusiness={snapshot?.specialBusiness ?? null}
              detectedPlace={detectedPlace}
              recommendations={visibleOrderRecommendations}
              recommendationIssues={visibleOrderRecommendationIssues}
              recommendationPendingOrders={visibleOrderRecommendationPendingOrders}
              recommendationsPending={visibleOrderRecommendationsUpdating}
              recommendationUpdateError={visibleOrderRecommendationUpdateError}
              data={recommendationData}
              performanceMs={snapshot?.performanceMs}
              orderRecommendationPerformanceMs={orderRecommendationPerformanceMs}
              runtimeSets={runtimeSets}
              uiPinningStatus={snapshot?.runtimeUiPinningStatus ?? ''}
              uiTargetSlots={gameUiTargetSlots}
              favorites={favorites}
              customRecipes={customRecipes}
              favoriteBusyKey={favoriteBusyKey}
              favoriteError={favoriteError}
              autoPrepBusy={autoPrepBusy}
              autoPrepMessage={autoPrepMessage}
              rareParticipationMessage={rareParticipationMessage}
              autoPrepPaused={autoPrepPaused}
              rareOrderDiagnostics={rareOrderDiagnostics}
              autoPrepPreferences={companionPreferences}
              recipeLimit={serviceFocusRecipeLimit}
              beverageLimit={serviceFocusBeverageLimit}
              normalOrderBusy={normalOrderBusy}
              normalOrderMessage={normalOrderMessage}
              normalOrderPausedCount={normalOrderPausedCount}
              normalOrderDiagnostics={normalOrderDiagnostics}
              automationRuntimeAllowed={nightBusinessAutomationAllowed}
              automationRuntimeBlockReason={nightBusinessAutomationBlockReason}
              automationRuntimeStatus={snapshot?.runtimeNightBusinessAutomationStatus ?? ''}
              automationSafetyBarriers={automationSafetyBarriers}
              automationBarrierAckBusyKey={automationBarrierAckBusyKey}
              automationResources={business.automation.resourceOverview}
              normalOrderDetailPlans={orderRecommendations.normalOrderDetailPlans}
              normalOrderDetailsPending={includeNormalOrderDetails && business.pending}
              normalOrderDetailsError={business.error}
              onRecipeLimitChange={setServiceFocusRecipeLimit}
              onBeverageLimitChange={setServiceFocusBeverageLimit}
              onToggleRecipeFavorite={toggleRecipeFavorite}
              onToggleBeverageFavorite={toggleBeverageFavorite}
              onRetryRareAutomationOrder={retryRareAutomationOrder}
              onResetRareAutomationOrder={resetRareAutomationOrder}
              onRetryNormalAutomationOrder={retryNormalAutomationOrder}
              onResetNormalAutomationOrder={resetNormalAutomationOrder}
              onAcknowledgeAutomationBarrier={acknowledgeAutomationBarrierEvent}
              dismissRareOrderBusyKey={dismissRareOrderBusyKey}
              dismissRareOrderError={dismissRareOrderError}
              onDismissRareOrder={dismissRareOrder}
              onEnterFocusMode={() => setServiceFocusMode(true)}
              normalBusiness={snapshot?.normalBusiness ?? null}
              serviceView={serviceView}
              serviceRecommendationTab={serviceRecommendationTab}
              onServiceViewChange={setServiceView}
              onServiceRecommendationTabChange={setServiceRecommendationTab}
              showDebugDetails={companionPreferences.showDebugDetails}
            />
          )}
        </TabsContent>

        <TabsContent value="extensions" data-gamepad-scope="content">
          {tab === 'extensions' && (
            <Tabs
              value={extensionTab}
              onValueChange={(value) => setExtensionTab(value as ExtensionTab)}
              className="space-y-4"
            >
              <TabsList scrollable className="grid h-9 w-full grid-cols-3" data-extension-tabs="true">
                <TabsTrigger value="missions" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  任务列表
                </TabsTrigger>
                <TabsTrigger value="rare-invitations" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  稀客邀请
                </TabsTrigger>
                <TabsTrigger value="inventory" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  修改
                </TabsTrigger>
              </TabsList>

              <TabsContent value="missions" className="space-y-4">
                {extensionTab === 'missions' && (
                  <ModMissionListPanel
                    connected={companionConnected}
                    missionListModuleEnabled={missionListModuleEnabled}
                    availableRuntimeReady={(snapshot?.missionGeneration ?? 0) > 0}
                    availableMissions={availableMissions}
                    availableMissionsError={availableMissionsError}
                    availableMissionsLoading={availableMissionsLoading}
                    trackedMissions={trackedMissions}
                    trackedMissionsError={trackedMissionsError}
                    trackedMissionsLoading={trackedMissionsLoading}
                    showDebugDetails={companionPreferences.showDebugDetails}
                    onMissionListModuleEnabledChange={updateMissionListModuleEnabled}
                    onRefreshMissions={() => {
                      refreshAvailableMissions();
                      refreshTrackedMissions();
                    }}
                  />
                )}
              </TabsContent>

              <TabsContent value="rare-invitations" className="space-y-4">
                {extensionTab === 'rare-invitations' && (
                  <ModRareGuestInvitationsPanel
                    runtimeLoaded={snapshot?.runtimeLoaded ?? false}
                    runtimeDaySceneReady={snapshot?.runtimeDaySceneReady ?? false}
                    rareGuestInvitationModuleEnabled={rareGuestInvitationModuleEnabled}
                    rareGuestInvitationModuleToggleDisabled={rareGuestInvitationWriteBusy}
                    invitationContextReady={rareGuestInvitationContextReady}
                    activeDayMapName={snapshot?.activeDayMapName ?? ''}
                    activeDayMapLabel={snapshot?.activeDayMapLabel ?? ''}
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
                    onRareGuestInvitationModuleEnabledChange={updateRareGuestInvitationModuleEnabled}
                    onRefreshRareGuestInvitations={loadRareGuestInvitations}
                    onInviteAllRareGuests={inviteAllRareGuests}
                    onInviteRareGuest={inviteRareGuest}
                  />
                )}
              </TabsContent>

              <TabsContent value="inventory" className="space-y-4">
                {extensionTab === 'inventory' && (
                  <ModInventoryPanel
                    endpoint={normalizedEndpoint}
                    apiToken={apiToken}
                    runtimeSets={runtimeSets}
                    runtimeLoaded={snapshot?.runtimeLoaded ?? false}
                    data={recommendationData}
                    onRefresh={async () => {
                      await refresh(true);
                    }}
                  />
                )}
              </TabsContent>
            </Tabs>
          )}
        </TabsContent>

        {companionPreferences.showDebugDetails && (
          <TabsContent value="logs" data-gamepad-scope="content">
            {tab === 'logs' && <ModLogsPanel endpoint={normalizedEndpoint} apiToken={apiToken} />}
          </TabsContent>
        )}

        <TabsContent value="settings" data-gamepad-scope="content">
          {tab === 'settings' && (
            <ModSettingsPanel
              endpoint={normalizedEndpoint}
              apiToken={apiToken}
              preferences={companionPreferences}
              data={recommendationData}
              runtimeSets={runtimeSets}
              themeMode={themeMode}
              serviceFocusCompact={serviceFocusCompact}
              settingsTab={settingsTab}
              updateManager={updateManager}
              deviceAuthority={companionDeviceAuthority}
              onPreferenceChange={updateCompanionPreferences}
              onConnectionConfigApplied={applyConnectionDetails}
              onSettingsTabChange={setSettingsTab}
              onThemeModeChange={setThemeMode}
              onServiceFocusCompactChange={setServiceFocusCompact}
              supportsDesktopWindowControls={companionPlatform === 'desktop'}
            />
          )}
        </TabsContent>
      </Tabs>
    </div>
    </BusinessConnectionProvider>
  );
}

async function toggleCompanionFocus(
  focusSwitchBehavior: FocusSwitchBehavior,
  focusSwitchCooldownMs: number,
) {
  if (!isTauriRuntime()) return;

  try {
    const { invoke } = await import('@tauri-apps/api/core');
    const outcome = await invoke<WindowSwitchOutcome>('toggle_companion_focus', {
      keepVisibleWhenFocused: focusSwitchBehavior === 'keep-visible',
      windowSwitchCooldownMs: normalizeFocusSwitchCooldownMs(focusSwitchCooldownMs),
    });
    if (!['applied', 'busy', 'throttled'].includes(outcome.status)) {
      console.warn(`Window focus switch rejected: ${outcome.status}`);
    }
  } catch (error) {
    console.warn('Window focus switch command failed.', error);
  }
}

interface WindowSwitchOutcome {
  applied: boolean;
  status:
    | 'applied'
    | 'throttled'
    | 'busy'
    | 'no-game-pid'
    | 'focus-failed'
    | 'show-failed'
    | 'hide-failed'
    | 'state-unavailable'
    | 'unsupported';
}
