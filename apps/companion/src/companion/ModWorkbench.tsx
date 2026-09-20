import { AutomationRuntimePanel } from '@/companion/pages/automation/AutomationRuntimePanel';
import { RecommendationRecoveryPanel } from '@/companion/pages/RecommendationRecoveryPanel';
import { InventoryOverviewPanel } from '@/companion/pages/overview/InventoryOverviewPanel';
import { HostNetworkPanel } from '@/companion/pages/settings/HostNetworkPanel';
import { DeviceSettingsPanel } from '@/companion/pages/settings/DeviceSettingsPanel';
import { RecommendationSettingsPanel } from '@/companion/pages/settings/RecommendationSettingsPanel';
import { AutomationSettingsPanel } from '@/companion/pages/settings/AutomationSettingsPanel';
import { GameUiSettingsPanel } from '@/companion/pages/settings/GameUiSettingsPanel';
import { ServiceDisplaySettingsPanel } from '@/companion/pages/settings/ServiceDisplaySettingsPanel';
import { createEmptyCustomRecipeForm, type CustomRecipeFormState } from '@/companion/custom-recipe-editor';
import {
  hasAutomationActionEnabled,
  hasNormalOrderActionEnabled,
  type OperationalOrderRecommendation,
} from '@/companion/domain/automation';
import { buildRuntimeSets } from '@/companion/domain/cookers';
import { resolvePrimaryExtensionModuleControl } from '@/companion/domain/extension-module-control';
import {
  buildNormalGameUiTarget,
  buildNormalGameUiTargetSource,
  buildRareGameUiTarget,
  buildRareGameUiTargetFromParticipationQueue,
  buildRareGameUiTargetSource,
} from '@/companion/domain/game-ui-targets';
import { buildOrderRecommendationPresentation } from '@/companion/domain/order-recommendation-presentation';
import {
  buildNormalOrderDetailInputSignature,
  buildNormalOrderWorkerPayload,
  buildOrderRecommendationPayloadSignature,
  buildOrderRecommendationPresentationContextSignature,
  buildUiPinningSpecialBusinessSignature,
  type NormalOrderDetailInput,
} from '@/companion/domain/recommendation-input';
import { normalizePlace } from '@/companion/domain/service-recommendations';
import { requiresSpecialBusinessNormalExecutionTarget } from '@/companion/domain/special-business';
import { UpdateNoticeBar } from '@/companion/features/updates/UpdateNoticeBar';
import { useUpdateManager } from '@/companion/features/updates/useUpdateManager';
import { useAutomationControl } from '@/companion/hooks/useAutomationControl';
import { useAutomationState } from '@/companion/hooks/useAutomationState';
import { useAvailableMissions } from '@/companion/hooks/useAvailableMissions';
import { useCompanionConnection } from '@/companion/hooks/useCompanionConnection';
import { useCompanionDeviceAuthority } from '@/companion/hooks/useCompanionDeviceAuthority';
import { useCustomRecipes } from '@/companion/hooks/useCustomRecipes';
import { useDesktopWindowControls } from '@/companion/hooks/useDesktopWindowControls';
import { useFavorites } from '@/companion/hooks/useFavorites';
import { useGameUiTargetPublisher } from '@/companion/hooks/useGameUiTargetPublisher';
import { useInventoryOperations } from '@/companion/hooks/useInventoryOperations';
import { useLocalApiConnectionSettings } from '@/companion/hooks/useLocalApiConnectionSettings';
import { useOrderAutomation } from '@/companion/hooks/useOrderAutomation';
import { useOrderRecommendations } from '@/companion/hooks/useOrderRecommendations';
import { useRareGuestInvitations } from '@/companion/hooks/useRareGuestInvitations';
import { useRareOrderParticipation } from '@/companion/hooks/useRareOrderParticipation';
import { useTrackedMissions } from '@/companion/hooks/useTrackedMissions';
import { ModCustomRecipesPanel } from '@/companion/pages/ModCustomRecipesPanel';
import { ModFavoritesPanel } from '@/companion/pages/ModFavoritesPanel';
import { ModInventoryPanel } from '@/companion/pages/ModInventoryPanel';
import { ModMissionListPanel } from '@/companion/pages/ModMissionListPanel';
import { ModNormalPanel } from '@/companion/pages/ModNormalPanel';
import { ModOverviewPanel } from '@/companion/pages/ModOverviewPanel';
import { ModRareGuestInvitationsPanel } from '@/companion/pages/ModRareGuestInvitationsPanel';
import { ModRareGuestParticipationPanel } from '@/companion/pages/ModRareGuestParticipationPanel';
import { ModRarePanel } from '@/companion/pages/ModRarePanel';
import {
  ModServicePanel,
  ServiceFocusPage,
  type ServicePanelView,
  type ServiceRecommendationTab,
} from '@/companion/pages/ModServicePanel';
import { ModSettingsPanel } from '@/companion/pages/ModSettingsPanel';
import { INNER_TAB_TRIGGER_CLASS } from '@/companion/pages/shared-constants';
import {
  applyCompanionVisualPreferences,
  applySharedCompanionPreferences,
  normalizeCompanionPreferences,
  normalizeFocusSwitchCooldownMs,
  persistCompanionPreferences,
  readSharedCompanionPreferences,
  readStoredCompanionPreferences,
  type CompanionPreferences,
  type FocusSwitchBehavior,
  type LocalCompanionPreferences,
  type SharedCompanionPreferences,
} from '@/companion/preferences';
import {
  normalizeRareGuestInvitationLevels,
  persistCustomRecipeGroupMode,
  persistFocusBeverageLimit,
  persistFocusCompact,
  persistFocusRecipeLimit,
  persistMissionListModuleEnabled,
  persistRareGuestInvitationModuleEnabled,
  persistNavigation,
  readStoredCustomRecipeGroupMode,
  readStoredFocusBeverageLimit,
  readStoredFocusCompact,
  readStoredFocusRecipeLimit,
  readStoredMissionListModuleEnabled,
  readStoredRareGuestInvitationModuleEnabled,
  readStoredNavigation,
} from '@/companion/storage';
import type {
  CompanionDevicePlatform,
  CustomRecipeGroupMode,
  ExtensionTab,
  GameUiTargetFeatures,
  GameUiTargetFeatureSlots,
  GameUiTargetSlots,
  ModTab,
  OverviewTab,
  RecommendationTab,
  SettingsTab,
} from '@/companion/types';
import { useGamepadNavigation } from '@/companion/use-gamepad-navigation';
import type { OrderRecommendationWorkerPayload } from '@/companion/workers/order-recommendations.types';
import { Badge, Button, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import type { PlaceName } from '@/lib/catalog-types';
import {
  buildRecommendationDataIndexes,
  buildRecommendationDataSet,
  buildRecommendationDataSignature,
} from '@/lib/recommendation-data';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import { useThemeMode } from '@/lib/theme';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

const MOD_TAB_TRIGGER_CLASS = 'min-w-[4.75rem] flex-none min-[640px]:w-full min-[640px]:min-w-0';
type CompanionPlatform = 'desktop' | 'mobile';

const MOD_TABS: ModTab[] = ['overview', 'recommendations', 'service', 'automation', 'extensions', 'settings'];

function useSignedValue<T>(value: T, signature: string): T {
  // Signature covers the semantic fields that should invalidate this value.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  return useMemo(() => value, [signature]);
}

/**
 * 伴随窗口的根工作台组件。
 *
 * 这里汇总本地 API 连接、推荐数据、收藏、自动化状态、手柄导航和页面路由。组件本身不直接读取游戏对象；
 * 所有运行时输入来自 `useCompanionConnection` 的快照，所有回写操作通过 `api.ts` 发送到 Mod 本地 API。
 */
export function ModWorkbench() {
  const { mode: themeMode, setMode: setThemeMode } = useThemeMode();
  const [initialNavigation] = useState(readStoredNavigation);
  const [tab, setTab] = useState<ModTab>(initialNavigation.tab);
  const [overviewTab, setOverviewTab] = useState<OverviewTab>('connection');
  const [automationTab, setAutomationTab] = useState<'runtime' | 'configuration'>('runtime');
  const openDevices = useCallback(() => {
    setOverviewTab('devices');
    setTab('overview');
  }, []);
  const openConnection = useCallback(() => {
    setOverviewTab('connection');
    setTab('overview');
  }, []);
  const [recommendationTab, setRecommendationTab] = useState<RecommendationTab>('normal');
  const [extensionTab, setExtensionTab] = useState<ExtensionTab>('missions');
  const [settingsTab, setSettingsTab] = useState<SettingsTab>(initialNavigation.settingsTab);
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
  const [customRecipeForm, setCustomRecipeForm] =
    useState<CustomRecipeFormState>(createEmptyCustomRecipeForm);
  const [companionPreferences, setCompanionPreferences] = useState<CompanionPreferences>(() =>
    readStoredCompanionPreferences(),
  );
  const [companionPlatform, setCompanionPlatform] = useState<CompanionPlatform>('desktop');
  // 经营中页面需要尽快响应订单变化和自动化结果；其他页面使用较低频率，减少本地 API 与反射快照压力。
  const snapshotRefreshIntervalMs =
    tab === 'service' || (tab === 'automation' && automationTab === 'runtime') || serviceFocusMode
      ? 750
      : 2000;
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
    resumeConnection,
    discardConnectionDraft,
    connectionDraftDirty,
    refresh,
  } = useCompanionConnection(snapshotRefreshIntervalMs);
  const companionConnected = Boolean(apiToken && !connectionPaused && !error && snapshot);
  const refreshInventorySnapshot = useCallback(() => refresh(false, true), [refresh]);
  const inventoryOperations = useInventoryOperations({
    endpoint: normalizedEndpoint,
    apiToken,
    connectionRevision,
    connected: companionConnected,
    runtimeReady: Boolean(snapshot?.runtimeLoaded && snapshot.recommendationState),
    onRefresh: refreshInventorySnapshot,
  });
  const connectionSettings = useLocalApiConnectionSettings({
    endpoint: normalizedEndpoint,
    apiToken,
    connectionRevision,
    connected: companionConnected,
    active: tab === 'overview' && overviewTab === 'network',
    onConnectionConfigApplied: applyConnectionDetails,
  });
  const {
    favorites,
    favoriteError,
    favoriteBusyKey,
    favoriteRefreshing,
    favoriteAvailability,
    refreshFavorites,
    toggleRecipeFavorite,
    toggleBeverageFavorite,
    removeRecipeFavoriteById,
    removeBeverageFavoriteById,
  } = useFavorites({
    apiToken,
    connected: companionConnected,
    connectionRevision,
    normalizedEndpoint,
  });
  const {
    customRecipes,
    customRecipeError,
    customRecipeBusyKey,
    customRecipeAvailability,
    refreshCustomRecipes,
    upsertCustomRecipeEntry,
    removeCustomRecipeEntry,
    setCustomRecipesEnabledState,
    updateCustomRecipeFlagsState,
    moveCustomRecipeEntry,
  } = useCustomRecipes({ apiToken, connected: companionConnected, connectionRevision, normalizedEndpoint });
  const updateManager = useUpdateManager({
    endpoint: normalizedEndpoint,
    apiToken,
    connectionRevision,
    connected: companionConnected,
  });
  const customRecipeDraftIdentity = JSON.stringify([normalizedEndpoint, apiToken, connectionRevision]);
  const customRecipeDraftIdentityRef = useRef(customRecipeDraftIdentity);

  useEffect(() => {
    if (customRecipeDraftIdentityRef.current === customRecipeDraftIdentity) return;
    customRecipeDraftIdentityRef.current = customRecipeDraftIdentity;
    setCustomRecipeForm(createEmptyCustomRecipeForm());
  }, [customRecipeDraftIdentity]);

  const updateCustomRecipeGroupMode = useCallback((mode: CustomRecipeGroupMode) => {
    setCustomRecipeGroupMode(mode);
    persistCustomRecipeGroupMode(mode);
  }, []);
  const missionListVisible = tab === 'extensions' && extensionTab === 'missions';
  const rareGuestInvitationVisible = tab === 'extensions' && extensionTab === 'rare-invitations';
  const { trackedMissions, trackedMissionsError, trackedMissionsLoading, refreshTrackedMissions } =
    useTrackedMissions({
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
  const updateRareGuestInvitationModuleEnabled = useCallback(
    (enabled: boolean) => {
      if (!enabled && rareGuestInvitationWriteBusy) return;
      setRareGuestInvitationModuleEnabled(enabled);
      persistRareGuestInvitationModuleEnabled(enabled);
    },
    [rareGuestInvitationWriteBusy],
  );
  const [manualPlace, setManualPlace] = useState<PlaceName | null>(null);
  const [rareCustomerId, setRareCustomerId] = useState<number | null>(null);
  const [requiredFoodTag, setRequiredFoodTag] = useState('');
  const [requiredBeverageTag, setRequiredBeverageTag] = useState('');
  const [serviceView, setServiceView] = useState<ServicePanelView>('recommendations');
  const [serviceRecommendationTab, setServiceRecommendationTab] = useState<ServiceRecommendationTab>('rare');
  const companionPreferencesRef = useRef(companionPreferences);
  const automationUiVisible =
    !serviceFocusMode && (tab === 'service' || (tab === 'automation' && automationTab === 'runtime'));
  const automationState = useAutomationState({ automationUiVisible, refresh });
  const {
    autoPrepBusy,
    autoPrepMessage,
    autoPrepPaused,
    rareOrderDiagnostics,
    normalOrderBusy,
    normalOrderMessage,
    normalOrderPausedCount,
    normalOrderDiagnostics,
    automationBarrierAckBusyKey,
    specialBusinessRejectedRecipeKeys,
  } = automationState;

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
  const companionDevicePlatform: CompanionDevicePlatform = !isTauriRuntime()
    ? 'browser'
    : companionPlatform === 'mobile'
      ? 'android'
      : 'windows';
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
  const primaryProfileDraft = companionDeviceAuthority.profileDraft;
  const stagePrimaryProfile = companionDeviceAuthority.stagePrimaryProfile;
  const editableCompanionPreferences = useMemo(
    () =>
      primaryProfileDraft
        ? applySharedCompanionPreferences(companionPreferences, primaryProfileDraft)
        : companionPreferences,
    [companionPreferences, primaryProfileDraft],
  );

  const updateLocalCompanionPreferences = useCallback(
    (next: Partial<LocalCompanionPreferences>) => {
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
    },
    [
      companionConnected,
      companionDeviceAuthority.currentDeviceIsPrimary,
      companionDeviceAuthority.state?.activeProfile,
    ],
  );

  const updateSharedCompanionPreferences = useCallback(
    (next: Partial<SharedCompanionPreferences>) => {
      stagePrimaryProfile(next);
    },
    [stagePrimaryProfile],
  );

  useEffect(() => {
    if (!companionPreferences.showDebugDetails && settingsTab === 'logs') {
      setSettingsTab('window');
    }
    if (!companionPreferences.showDebugDetails && serviceView === 'diagnostics') {
      setServiceView('recommendations');
    }
  }, [companionPreferences.showDebugDetails, settingsTab, serviceView]);

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
  const automationControl = useAutomationControl({
    state: automationState,
    connectionRevision,
    apiToken,
    normalizedEndpoint,
    connectionPaused,
    error,
    snapshot,
    companionConnected,
    companionPreferences,
    sharedCompanionPreferences,
    companionDeviceAuthority,
  });
  const {
    connectionReadyForActions,
    automationRuntimeEnabled,
    nightBusinessAutomationAllowed,
    nightBusinessAutomationBlockReason,
    markRareParticipationMutationBoundary,
  } = automationControl;
  const night = snapshot?.nightBusiness ?? null;
  const nightBusinessActive =
    snapshot?.nightBusinessLifecyclePhase === 'Active' || snapshot?.nightBusinessLifecyclePhase === 'Closing';
  const rareOrderCollectionComplete = night !== null && !night.error?.trim();
  const refreshRareParticipationSnapshot = useCallback(async () => {
    await refresh(true);
  }, [refresh]);
  const rareOrderParticipation = useRareOrderParticipation({
    endpoint: normalizedEndpoint,
    apiToken,
    connected: companionConnected,
    connectionRevision,
    authorityReady: companionDeviceAuthority.ready,
    currentDeviceIsPrimary: companionDeviceAuthority.currentDeviceIsPrimary,
    runtimeWriterReady: companionDeviceAuthority.runtimeWriterReady,
    authorityRevision: companionDeviceAuthority.authorityRevision,
    businessActive: nightBusinessActive,
    businessGeneration: snapshot?.nightBusinessGeneration ?? 0,
    collectionComplete: rareOrderCollectionComplete,
    orders: night?.orders ?? [],
    moduleEnabled: companionPreferences.rareGuestParticipationModuleEnabled,
    managedGuestIds: companionPreferences.managedRareGuestIds,
    snapshot: snapshot?.rareGuestParticipation ?? null,
    refreshSnapshot: refreshRareParticipationSnapshot,
    refreshAuthority: companionDeviceAuthority.refresh,
    onMutationBoundary: markRareParticipationMutationBoundary,
  });
  const rareGuestParticipationModuleControl = resolvePrimaryExtensionModuleControl({
    enabled: editableCompanionPreferences.rareGuestParticipationModuleEnabled,
    connected: companionConnected,
    authorityReady: companionDeviceAuthority.ready,
    currentDeviceIsPrimary: companionDeviceAuthority.currentDeviceIsPrimary,
    primaryDeviceLabel: companionDeviceAuthority.state?.devices.find((device) => device.isPrimary)?.label,
    profileUpdatePending: companionDeviceAuthority.profileUpdatePending,
    authorityBusy: companionDeviceAuthority.busy !== null,
    operationInFlight: rareOrderParticipation.busyMutationKey !== null,
  });
  const detectedPlace = normalizePlace(night?.place);
  const selectedPlace = manualPlace ?? detectedPlace;
  const effectiveRuntimeData = cachedRuntimeData;
  // 运行时目录数据较大，完整目录通过 /runtime-data 按签名单独缓存，避免进入高频快照热路径。
  const recommendationData = useMemo(
    () => buildRecommendationDataSet(effectiveRuntimeData),
    [effectiveRuntimeData],
  );
  const recommendationDataSignature = useMemo(
    () => buildRecommendationDataSignature(recommendationData),
    [recommendationData],
  );
  const recommendationIndexes = useMemo(
    () => buildRecommendationDataIndexes(recommendationData),
    [recommendationData],
  );

  const runtimeSets = useMemo(
    () => buildRuntimeSets(runtime, recommendationData),
    [recommendationData, runtime],
  );
  const visibleTabs = MOD_TABS;
  const serviceRecommendationsVisible = tab === 'service' && serviceView === 'recommendations';
  const includeNormalOrderDetails = serviceRecommendationsVisible && serviceRecommendationTab === 'normal';
  const normalOrdersRequireSpecialExecutionTarget = (snapshot?.normalBusiness?.orders ?? []).some((order) =>
    requiresSpecialBusinessNormalExecutionTarget(snapshot?.specialBusiness, order.specialBusinessRole),
  );
  const rareGameUiTargetFeatures = useMemo<GameUiTargetFeatures>(
    () => ({
      listPinningEnabled: companionPreferences.rareGameUiPinningEnabled,
      recipeVariantEnabled:
        companionPreferences.rareGameUiPinningEnabled && companionPreferences.rareRecipeVariantEnabled,
      cookerHighlightEnabled: companionPreferences.rareCookerHighlightEnabled,
      seatHighlightEnabled: companionPreferences.rareSeatHighlightEnabled,
      orderHighlightEnabled: companionPreferences.rareOrderHighlightEnabled,
    }),
    [
      companionPreferences.rareCookerHighlightEnabled,
      companionPreferences.rareGameUiPinningEnabled,
      companionPreferences.rareOrderHighlightEnabled,
      companionPreferences.rareRecipeVariantEnabled,
      companionPreferences.rareSeatHighlightEnabled,
    ],
  );
  const normalGameUiTargetFeatures = useMemo<GameUiTargetFeatures>(
    () => ({
      listPinningEnabled: companionPreferences.normalGameUiPinningEnabled,
      recipeVariantEnabled:
        companionPreferences.normalGameUiPinningEnabled && companionPreferences.normalRecipeVariantEnabled,
      cookerHighlightEnabled: companionPreferences.normalCookerHighlightEnabled,
      seatHighlightEnabled: companionPreferences.normalSeatHighlightEnabled,
      orderHighlightEnabled: companionPreferences.normalOrderHighlightEnabled,
    }),
    [
      companionPreferences.normalCookerHighlightEnabled,
      companionPreferences.normalGameUiPinningEnabled,
      companionPreferences.normalOrderHighlightEnabled,
      companionPreferences.normalRecipeVariantEnabled,
      companionPreferences.normalSeatHighlightEnabled,
    ],
  );
  const gameUiTargetFeatureSlots = useMemo<GameUiTargetFeatureSlots>(
    () => ({
      rare: rareGameUiTargetFeatures,
      normal: normalGameUiTargetFeatures,
    }),
    [normalGameUiTargetFeatures, rareGameUiTargetFeatures],
  );
  const rareGameUiTargetFeaturesEnabled = Object.values(rareGameUiTargetFeatures).some(Boolean);
  const normalGameUiTargetFeaturesEnabled = Object.values(normalGameUiTargetFeatures).some(Boolean);
  const normalAutomationNeedsExecutionTargets =
    automationRuntimeEnabled &&
    companionPreferences.autoNormalOrderEnabled &&
    hasNormalOrderActionEnabled(companionPreferences);
  const normalExecutionTargetsEnabled =
    normalOrdersRequireSpecialExecutionTarget &&
    (normalAutomationNeedsExecutionTargets || normalGameUiTargetFeaturesEnabled);
  const rareAutomationNeedsRecommendations =
    automationRuntimeEnabled &&
    companionPreferences.autoRareOrderEnabled &&
    hasAutomationActionEnabled(companionPreferences);
  const orderRecommendationUsage = tab === 'service' || serviceFocusMode ? 'display' : 'automation';
  const orderRecommendationPayloadValue = useMemo<OrderRecommendationWorkerPayload>(
    () => ({
      orders: night?.orders ?? [],
      runtime,
      favorites,
      customRecipes,
      preferences: companionPreferences,
      specialBusiness: snapshot?.specialBusiness ?? null,
      specialBusinessRejectedRecipeKeys,
      data: recommendationData,
      usage: orderRecommendationUsage,
    }),
    [
      companionPreferences,
      customRecipes,
      favorites,
      night?.orders,
      recommendationData,
      runtime,
      specialBusinessRejectedRecipeKeys,
      snapshot?.specialBusiness,
      orderRecommendationUsage,
    ],
  );
  const orderRecommendationPayloadSignature = useMemo(
    () => buildOrderRecommendationPayloadSignature(orderRecommendationPayloadValue),
    [orderRecommendationPayloadValue],
  );
  const orderRecommendationPresentationContextSignature = useMemo(
    () =>
      buildOrderRecommendationPresentationContextSignature(
        connectionRevision,
        snapshot?.automationSessionId ?? '',
        snapshot?.nightBusinessGeneration ?? 0,
        snapshot?.nightBusinessLifecyclePhase ?? 'Inactive',
        snapshot?.specialBusiness,
        recommendationDataSignature,
      ),
    [
      connectionRevision,
      recommendationDataSignature,
      snapshot?.automationSessionId,
      snapshot?.nightBusinessGeneration,
      snapshot?.nightBusinessLifecyclePhase,
      snapshot?.specialBusiness,
    ],
  );
  const orderRecommendationPayload = useSignedValue(
    orderRecommendationPayloadValue,
    orderRecommendationPayloadSignature,
  );
  const normalOrderDetailInputValue = useMemo<NormalOrderDetailInput>(
    () => ({
      include: includeNormalOrderDetails,
      normalOrders: includeNormalOrderDetails ? (snapshot?.normalBusiness?.orders ?? []) : [],
      runtime,
      preferences: companionPreferences,
      specialBusiness: snapshot?.specialBusiness ?? null,
      rejectedRecipeKeys: specialBusinessRejectedRecipeKeys,
    }),
    [
      companionPreferences,
      includeNormalOrderDetails,
      runtime,
      snapshot?.normalBusiness?.orders,
      snapshot?.specialBusiness,
      specialBusinessRejectedRecipeKeys,
    ],
  );
  const normalOrderDetailInputSignature = useMemo(
    () => buildNormalOrderDetailInputSignature(normalOrderDetailInputValue),
    [normalOrderDetailInputValue],
  );
  const normalOrderDetailInput = useSignedValue(normalOrderDetailInputValue, normalOrderDetailInputSignature);
  const normalOrderDetailPayload = useMemo(
    () => buildNormalOrderWorkerPayload(normalOrderDetailInput, recommendationData, { includeDetails: true }),
    [normalOrderDetailInput, recommendationData],
  );
  const normalExecutionTargetInputValue = useMemo<NormalOrderDetailInput>(
    () => ({
      include: normalExecutionTargetsEnabled,
      normalOrders: normalExecutionTargetsEnabled ? (snapshot?.normalBusiness?.orders ?? []) : [],
      runtime,
      preferences: companionPreferences,
      specialBusiness: snapshot?.specialBusiness ?? null,
      rejectedRecipeKeys: specialBusinessRejectedRecipeKeys,
    }),
    [
      companionPreferences,
      normalExecutionTargetsEnabled,
      runtime,
      snapshot?.normalBusiness?.orders,
      snapshot?.specialBusiness,
      specialBusinessRejectedRecipeKeys,
    ],
  );
  const normalExecutionTargetInputSignature = useMemo(
    () => buildNormalOrderDetailInputSignature(normalExecutionTargetInputValue),
    [normalExecutionTargetInputValue],
  );
  const normalExecutionTargetInput = useSignedValue(
    normalExecutionTargetInputValue,
    normalExecutionTargetInputSignature,
  );
  const normalExecutionTargetPayload = useMemo(
    () =>
      buildNormalOrderWorkerPayload(normalExecutionTargetInput, recommendationData, {
        includeExecutionTargets: true,
        usage: 'automation',
      }),
    [normalExecutionTargetInput, recommendationData],
  );
  const orderRecommendationsEnabled =
    tab === 'service' ||
    serviceFocusMode ||
    rareAutomationNeedsRecommendations ||
    rareGameUiTargetFeaturesEnabled;
  const orderRecommendations = useOrderRecommendations(orderRecommendationPayload, {
    enabled: orderRecommendationsEnabled,
    inputSignature: orderRecommendationPayloadSignature,
    contextSignature: orderRecommendationPresentationContextSignature,
  });
  const rareParticipationActive = rareOrderParticipation.participationActive;
  const rareParticipationProjectionReady = rareOrderParticipation.projectionReady;
  const resolveRareOrderParticipation = rareOrderParticipation.resolveOrder;
  const rareRecommendationPresentationOrders = useMemo(
    () =>
      rareParticipationActive
        ? rareParticipationProjectionReady
          ? (rareOrderParticipation.projection?.operationalOrders ?? [])
          : []
        : (night?.orders ?? []),
    [
      night?.orders,
      rareOrderParticipation.projection?.operationalOrders,
      rareParticipationActive,
      rareParticipationProjectionReady,
    ],
  );
  const orderRecommendationPresentation = useMemo(
    () =>
      buildOrderRecommendationPresentation({
        orders: rareRecommendationPresentationOrders,
        recommendations: orderRecommendations.recommendations,
        recommendationIssues: orderRecommendations.recommendationIssues,
        pending: orderRecommendations.pending,
        isCurrent: orderRecommendations.isCurrent,
        resultContextSignature: orderRecommendations.resultContextSignature,
        currentContextSignature: orderRecommendationPresentationContextSignature,
        error: orderRecommendations.error,
        retainedAfterError: orderRecommendations.retainedAfterError,
      }),
    [
      rareRecommendationPresentationOrders,
      orderRecommendationPresentationContextSignature,
      orderRecommendations.isCurrent,
      orderRecommendations.pending,
      orderRecommendations.error,
      orderRecommendations.recommendationIssues,
      orderRecommendations.recommendations,
      orderRecommendations.retainedAfterError,
      orderRecommendations.resultContextSignature,
    ],
  );
  const visibleOrderRecommendations = orderRecommendationPresentation.recommendations;
  const visibleOrderRecommendationIssues = orderRecommendationPresentation.recommendationIssues;
  const visibleOrderRecommendationPendingOrders = orderRecommendationPresentation.pendingOrders;
  const visibleOrderRecommendationsUpdating = orderRecommendationPresentation.updating;
  const visibleOrderRecommendationUpdateError = orderRecommendationPresentation.updateError;
  const operationalOrderRecommendations = useMemo<readonly OperationalOrderRecommendation[] | null>(() => {
    if (!rareParticipationActive) return null;
    if (!rareParticipationProjectionReady) return [];
    return orderRecommendations.recommendations.flatMap((recommendation) => {
      const participation = resolveRareOrderParticipation(recommendation.order);
      return participation?.operationallyParticipating ? [{ recommendation, participation }] : [];
    });
  }, [
    orderRecommendations.recommendations,
    rareParticipationActive,
    rareParticipationProjectionReady,
    resolveRareOrderParticipation,
  ]);
  const normalOrderDetails = useOrderRecommendations(normalOrderDetailPayload, {
    enabled: includeNormalOrderDetails,
    inputSignature: `${normalOrderDetailInputSignature}\n${recommendationDataSignature}`,
    contextSignature: orderRecommendationPresentationContextSignature,
  });
  const normalExecutionTargets = useOrderRecommendations(normalExecutionTargetPayload, {
    enabled: normalExecutionTargetsEnabled,
    inputSignature: `${normalExecutionTargetInputSignature}\n${recommendationDataSignature}`,
    contextSignature: orderRecommendationPresentationContextSignature,
  });
  const orderRecommendationPerformanceMs = useMemo(
    () => ({
      ...(orderRecommendations.performanceMs ?? {}),
      ...(normalOrderDetails.performanceMs
        ? {
            normalDetails:
              normalOrderDetails.performanceMs.normalDetails ?? normalOrderDetails.performanceMs.total ?? 0,
          }
        : {}),
      ...(normalExecutionTargets.performanceMs
        ? {
            normalAutomationTargets:
              normalExecutionTargets.performanceMs.normalExecutionTargets ??
              normalExecutionTargets.performanceMs.total ??
              0,
          }
        : {}),
    }),
    [
      normalExecutionTargets.performanceMs,
      normalOrderDetails.performanceMs,
      orderRecommendations.performanceMs,
    ],
  );
  const rareGameUiTarget = useMemo(() => {
    if (!rareGameUiTargetFeaturesEnabled) return null;
    if (operationalOrderRecommendations !== null) {
      return buildRareGameUiTargetFromParticipationQueue(
        operationalOrderRecommendations,
        companionPreferences.serviceOrderSortMode,
        companionPreferences.rareTargetHighlightColor,
        rareGameUiTargetFeatures,
        recommendationIndexes,
        { specialBusiness: snapshot?.specialBusiness ?? null },
      );
    }
    return buildRareGameUiTarget(
      orderRecommendations.recommendations,
      companionPreferences.serviceOrderSortMode,
      companionPreferences.rareTargetHighlightColor,
      rareGameUiTargetFeatures,
      recommendationIndexes,
      {
        prioritizeMissionRecipe:
          companionPreferences.missionRecipePriorityEnabled && !snapshot?.specialBusiness?.active,
        specialBusiness: snapshot?.specialBusiness ?? null,
      },
    );
  }, [
    companionPreferences.rareTargetHighlightColor,
    companionPreferences.missionRecipePriorityEnabled,
    companionPreferences.serviceOrderSortMode,
    orderRecommendations.recommendations,
    operationalOrderRecommendations,
    rareGameUiTargetFeatures,
    rareGameUiTargetFeaturesEnabled,
    recommendationIndexes,
    snapshot?.specialBusiness,
  ]);
  const normalGameUiTarget = useMemo(
    () =>
      normalGameUiTargetFeaturesEnabled
        ? buildNormalGameUiTarget({
            orders: snapshot?.normalBusiness?.orders ?? [],
            executionTargets: normalExecutionTargets.normalExecutionTargets,
            executionTargetsCurrent:
              normalExecutionTargets.isCurrent &&
              !normalExecutionTargets.pending &&
              !normalExecutionTargets.error,
            specialBusiness: snapshot?.specialBusiness,
            businessGeneration: snapshot?.nightBusinessGeneration ?? 0,
            color: companionPreferences.normalTargetHighlightColor,
            features: normalGameUiTargetFeatures,
            data: recommendationData,
          })
        : null,
    [
      companionPreferences.normalTargetHighlightColor,
      normalGameUiTargetFeatures,
      normalGameUiTargetFeaturesEnabled,
      normalExecutionTargets.error,
      normalExecutionTargets.isCurrent,
      normalExecutionTargets.normalExecutionTargets,
      normalExecutionTargets.pending,
      recommendationData,
      snapshot?.nightBusinessGeneration,
      snapshot?.normalBusiness?.orders,
      snapshot?.specialBusiness,
    ],
  );
  const gameUiTargetSlots = useMemo<GameUiTargetSlots>(
    () => ({ rare: rareGameUiTarget, normal: normalGameUiTarget }),
    [normalGameUiTarget, rareGameUiTarget],
  );
  const gameUiTargetSourceOrders = useMemo(
    () => [
      ...(rareOrderParticipation.participationActive
        ? rareOrderParticipation.projectionReady
          ? (rareOrderParticipation.projection?.operationalOrders ?? [])
          : []
        : (night?.orders ?? [])
      ).map(buildRareGameUiTargetSource),
      ...(snapshot?.normalBusiness?.orders ?? []).map(buildNormalGameUiTargetSource),
    ],
    [
      night?.orders,
      rareOrderParticipation.participationActive,
      rareOrderParticipation.projectionReady,
      rareOrderParticipation.projection?.operationalOrders,
      snapshot?.normalBusiness?.orders,
    ],
  );
  const gameUiTargetLaneStates = useMemo(
    () => ({
      rare: {
        isCurrent: rareOrderParticipation.projectionReady && orderRecommendations.isCurrent,
        pending: rareOrderParticipation.projectionReady && orderRecommendations.pending,
        error: !rareOrderParticipation.projectionReady || Boolean(orderRecommendations.error),
      },
      normal: {
        isCurrent:
          normalGameUiTarget !== null ||
          !normalOrdersRequireSpecialExecutionTarget ||
          (normalExecutionTargets.isCurrent && !normalExecutionTargets.pending),
        pending:
          normalGameUiTarget === null &&
          normalOrdersRequireSpecialExecutionTarget &&
          !normalExecutionTargets.error &&
          (normalExecutionTargets.pending || !normalExecutionTargets.isCurrent),
        error:
          normalGameUiTarget === null &&
          normalOrdersRequireSpecialExecutionTarget &&
          Boolean(normalExecutionTargets.error),
      },
    }),
    [
      normalExecutionTargets.error,
      normalExecutionTargets.isCurrent,
      normalExecutionTargets.pending,
      normalGameUiTarget,
      normalOrdersRequireSpecialExecutionTarget,
      orderRecommendations.error,
      orderRecommendations.isCurrent,
      orderRecommendations.pending,
      rareOrderParticipation.projectionReady,
    ],
  );
  const gameUiTargetColors = useMemo(
    () => ({
      rare: companionPreferences.rareTargetHighlightColor,
      normal: companionPreferences.normalTargetHighlightColor,
    }),
    [companionPreferences.normalTargetHighlightColor, companionPreferences.rareTargetHighlightColor],
  );
  useGameUiTargetPublisher({
    endpoint: normalizedEndpoint,
    apiToken,
    connectionRevision,
    authorityRevision: companionDeviceAuthority.authorityRevision,
    sessionId: snapshot?.automationSessionId.trim() ?? '',
    businessGeneration: snapshot?.nightBusinessGeneration ?? 0,
    businessActive: snapshot?.nightBusinessLifecyclePhase === 'Active',
    connectionReady: connectionReadyForActions,
    featureSlots: gameUiTargetFeatureSlots,
    targetSlots: gameUiTargetSlots,
    sourceOrders: gameUiTargetSourceOrders,
    laneStates: gameUiTargetLaneStates,
    colors: gameUiTargetColors,
    targetPolicySignature: buildUiPinningSpecialBusinessSignature(snapshot?.specialBusiness),
  });

  const {
    automationSafetyBarriers,
    retryRareAutomationOrder,
    resetRareAutomationOrder,
    retryNormalAutomationOrder,
    resetNormalAutomationOrder,
    acknowledgeAutomationBarrierEvent,
  } = useOrderAutomation({
    state: automationState,
    control: automationControl,
    automationUiVisible,
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
    rareOrderParticipation,
  });

  useEffect(() => {
    persistNavigation(tab, settingsTab);
  }, [tab, settingsTab]);

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

  const desktopWindowControls = useDesktopWindowControls({
    enabled: isTauriRuntime() && companionPlatform === 'desktop',
    focusSwitchBehavior: companionPreferences.focusSwitchBehavior,
    alwaysOnTop: companionPreferences.alwaysOnTop,
    focusSwitchCooldownMs: companionPreferences.focusSwitchCooldownMs,
  });

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

  const mousePassthroughSafetyNotice =
    companionPlatform === 'desktop' && desktopWindowControls.mousePassthroughEnabled ? (
      <Badge variant="secondary" data-mouse-passthrough-safety="true">
        鼠标穿透中 ·{' '}
        {desktopWindowControls.hotkeyStatus?.status === 'available'
          ? 'F10 解除'
          : '请用 F8、RS Click 或托盘解除'}
      </Badge>
    ) : null;

  if (serviceFocusMode) {
    return (
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
        favoriteAvailability={favoriteAvailability}
        favoriteError={favoriteError}
        participationEnabled={rareOrderParticipation.participationActive}
        participationReady={rareOrderParticipation.projectionReady}
        resolveRareOrderParticipation={rareOrderParticipation.resolveOrder}
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
        safetyNotice={mousePassthroughSafetyNotice}
      />
    );
  }

  return (
    <div className="space-y-3" data-companion-surface="workbench">
      {mousePassthroughSafetyNotice && <div className="flex justify-end">{mousePassthroughSafetyNotice}</div>}

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
          <TabsTrigger
            value="overview"
            className={MOD_TAB_TRIGGER_CLASS}
            data-gamepad-tab="true"
            data-gamepad-tab-value="overview"
          >
            概览
          </TabsTrigger>
          <TabsTrigger
            value="recommendations"
            className={MOD_TAB_TRIGGER_CLASS}
            data-gamepad-tab="true"
            data-gamepad-tab-value="recommendations"
          >
            推荐
          </TabsTrigger>
          <TabsTrigger
            value="service"
            className={MOD_TAB_TRIGGER_CLASS}
            data-gamepad-tab="true"
            data-gamepad-tab-value="service"
          >
            经营
          </TabsTrigger>
          <TabsTrigger
            value="automation"
            className={MOD_TAB_TRIGGER_CLASS}
            data-gamepad-tab="true"
            data-gamepad-tab-value="automation"
          >
            自动化
          </TabsTrigger>
          <TabsTrigger
            value="extensions"
            className={MOD_TAB_TRIGGER_CLASS}
            data-gamepad-tab="true"
            data-gamepad-tab-value="extensions"
          >
            工具
          </TabsTrigger>
          <TabsTrigger
            value="settings"
            className={MOD_TAB_TRIGGER_CLASS}
            data-gamepad-tab="true"
            data-gamepad-tab-value="settings"
          >
            设置
          </TabsTrigger>
        </TabsList>

        {!companionConnected && tab !== 'overview' && (
          <div role="status" className="steward-inline-panel flex flex-wrap items-center gap-2 p-3 text-sm">
            <span className="min-w-0 flex-1 break-words">
              {!apiToken
                ? '请先配置 API 地址和 Token 连接 Mod。'
                : connectionPaused
                  ? '自动连接已暂停，已有数据为上次读取结果。'
                  : error || '正在确认 Mod 连接。'}
            </span>
            <Button size="sm" onClick={openConnection}>
              查看连接
            </Button>
          </div>
        )}

        <TabsContent value="overview" data-gamepad-scope="content">
          {tab === 'overview' && (
            <ModOverviewPanel
              endpointDraft={endpointDraft}
              onEndpointDraftChange={setEndpointDraft}
              apiTokenDraft={apiTokenDraft}
              onApiTokenDraftChange={setApiTokenDraft}
              onApplyEndpointConnection={applyEndpointConnection}
              onPauseConnection={pauseConnection}
              onResumeConnection={resumeConnection}
              onDiscardConnectionDraft={discardConnectionDraft}
              connectionDraftDirty={connectionDraftDirty}
              overviewTab={overviewTab}
              onOverviewTabChange={setOverviewTab}
              networkPanel={
                <HostNetworkPanel
                  endpoint={normalizedEndpoint}
                  apiToken={apiToken}
                  connectionSettings={connectionSettings}
                />
              }
              devicesPanel={
                <DeviceSettingsPanel
                  endpoint={normalizedEndpoint}
                  apiToken={apiToken}
                  deviceAuthority={companionDeviceAuthority}
                />
              }
              onRefresh={() => void refresh(true)}
              apiToken={apiToken}
              connectionPaused={connectionPaused}
              connectionFailureCount={connectionFailureCount}
              loading={loading}
              normalizedEndpoint={normalizedEndpoint}
              snapshot={snapshot}
              runtime={runtime}
              night={night}
              data={recommendationData}
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
              <TabsList scrollable className="grid h-9 w-full grid-cols-5" data-recommendation-tabs="true">
                <TabsTrigger value="normal" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  普客
                </TabsTrigger>
                <TabsTrigger value="rare" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  稀客
                </TabsTrigger>
                <TabsTrigger
                  value="custom-recipes"
                  className={INNER_TAB_TRIGGER_CLASS}
                  data-gamepad-clickable="true"
                >
                  自定义
                </TabsTrigger>
                <TabsTrigger
                  value="favorites"
                  className={INNER_TAB_TRIGGER_CLASS}
                  data-gamepad-clickable="true"
                >
                  收藏
                </TabsTrigger>
                <TabsTrigger value="rules" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
                  推荐规则
                </TabsTrigger>
              </TabsList>

              <TabsContent value="normal" className="space-y-4">
                {recommendationTab === 'normal' && (
                  <ModNormalPanel
                    connectionRevision={connectionRevision}
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
                    connectionRevision={connectionRevision}
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
                    favoriteAvailability={favoriteAvailability}
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
                    customRecipeAvailability={customRecipeAvailability}
                    onRefreshCustomRecipes={refreshCustomRecipes}
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
                    favoriteAvailability={favoriteAvailability}
                    showDebugDetails={companionPreferences.showDebugDetails}
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
              <TabsContent value="rules">
                {recommendationTab === 'rules' && (
                  <RecommendationSettingsPanel
                    preferences={editableCompanionPreferences}
                    deviceAuthority={companionDeviceAuthority}
                    onSharedPreferenceChange={updateSharedCompanionPreferences}
                    onOpenDevices={openDevices}
                    data={recommendationData}
                    runtimeSets={runtimeSets}
                  />
                )}
              </TabsContent>
            </Tabs>
          )}
        </TabsContent>

        <TabsContent value="service" data-gamepad-scope="content">
          {tab === 'service' && (
            <RecommendationRecoveryPanel
              computations={[
                { label: '稀客推荐', error: orderRecommendations.error, retry: orderRecommendations.retry },
                { label: '普客详情', error: normalOrderDetails.error, retry: normalOrderDetails.retry },
                {
                  label: '普客执行计划',
                  error: normalExecutionTargets.error,
                  retry: normalExecutionTargets.retry,
                },
              ]}
            />
          )}
          {tab === 'service' && (
            <ModServicePanel
              runtime={runtime}
              nightBusinessActive={nightBusinessActive}
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
              favoriteAvailability={favoriteAvailability}
              favoriteError={favoriteError}
              rareOrderDiagnostics={rareOrderDiagnostics}
              autoPrepPreferences={companionPreferences}
              recipeLimit={serviceFocusRecipeLimit}
              beverageLimit={serviceFocusBeverageLimit}
              normalOrderDiagnostics={normalOrderDiagnostics}
              automationRuntimeAllowed={nightBusinessAutomationAllowed}
              automationRuntimeBlockReason={nightBusinessAutomationBlockReason}
              automationRuntimeStatus={snapshot?.runtimeNightBusinessAutomationStatus ?? ''}
              automationSafetyBarriers={automationSafetyBarriers}
              normalExecutionTargets={normalExecutionTargets.normalExecutionTargets}
              normalExecutionTargetsEnabled={normalExecutionTargetsEnabled}
              normalExecutionTargetsPending={
                normalExecutionTargets.pending || !normalExecutionTargets.isCurrent
              }
              normalExecutionTargetsError={normalExecutionTargets.error}
              normalOrderDetailPlans={normalOrderDetails.normalOrderDetailPlans}
              normalOrderDetailsPending={
                includeNormalOrderDetails && (normalOrderDetails.pending || !normalOrderDetails.isCurrent)
              }
              normalOrderDetailsError={normalOrderDetails.error}
              onRecipeLimitChange={setServiceFocusRecipeLimit}
              onBeverageLimitChange={setServiceFocusBeverageLimit}
              onToggleRecipeFavorite={toggleRecipeFavorite}
              onToggleBeverageFavorite={toggleBeverageFavorite}
              onEnterFocusMode={() => setServiceFocusMode(true)}
              normalBusiness={snapshot?.normalBusiness ?? null}
              serviceView={serviceView}
              serviceRecommendationTab={serviceRecommendationTab}
              operationalRecommendations={operationalOrderRecommendations}
              rareParticipationModuleEnabled={rareOrderParticipation.moduleEnabled}
              managedRareGuestIds={companionPreferences.managedRareGuestIds}
              rareGuestParticipationSnapshot={rareOrderParticipation.snapshot}
              rareParticipationBusinessGeneration={snapshot?.nightBusinessGeneration ?? 0}
              rareParticipationCollectionComplete={rareOrderCollectionComplete}
              rareParticipationEnabled={rareOrderParticipation.participationActive}
              rareParticipationReady={rareOrderParticipation.projectionReady}
              rareParticipationReadOnly={rareOrderParticipation.readOnly}
              rareParticipationReadOnlyReason={rareOrderParticipation.readOnlyReason}
              rareParticipationBusyMutationKey={rareOrderParticipation.busyMutationKey}
              rareParticipationError={rareOrderParticipation.error}
              resolveRareOrderParticipation={rareOrderParticipation.resolveOrder}
              onServiceViewChange={setServiceView}
              onServiceRecommendationTabChange={setServiceRecommendationTab}
              onMutateRareGuestOrders={(guestId, targets, action) => {
                void rareOrderParticipation.mutateGuest(guestId, targets, action);
              }}
              onMutateRareOrder={(order, action) => {
                void rareOrderParticipation.mutateOrder(order, action);
              }}
              showDebugDetails={companionPreferences.showDebugDetails}
              onOpenAutomation={() => {
                setAutomationTab('runtime');
                setTab('automation');
              }}
              participationSettings={(queue) => (
                <ModRareGuestParticipationPanel
                  queue={queue}
                  control={rareGuestParticipationModuleControl}
                  customers={recommendationData.rareCustomers}
                  managedGuestIds={editableCompanionPreferences.managedRareGuestIds}
                  currentOrders={night?.orders ?? []}
                  error={companionDeviceAuthority.error || rareOrderParticipation.error}
                  onModuleEnabledChange={(rareGuestParticipationModuleEnabled) => {
                    if (rareOrderParticipation.busyMutationKey !== null) return;
                    updateSharedCompanionPreferences({ rareGuestParticipationModuleEnabled });
                  }}
                  onManagedGuestIdsChange={(managedRareGuestIds) => {
                    updateSharedCompanionPreferences({ managedRareGuestIds: [...managedRareGuestIds] });
                  }}
                />
              )}
              gameUiSettings={
                <GameUiSettingsPanel
                  preferences={editableCompanionPreferences}
                  deviceAuthority={companionDeviceAuthority}
                  onSharedPreferenceChange={updateSharedCompanionPreferences}
                  onOpenDevices={openDevices}
                />
              }
              displaySettings={
                <ServiceDisplaySettingsPanel
                  preferences={editableCompanionPreferences}
                  deviceAuthority={companionDeviceAuthority}
                  onSharedPreferenceChange={updateSharedCompanionPreferences}
                  onOpenDevices={openDevices}
                  serviceFocusCompact={serviceFocusCompact}
                  onServiceFocusCompactChange={setServiceFocusCompact}
                />
              }
            />
          )}
        </TabsContent>

        <TabsContent value="automation" data-gamepad-scope="content">
          {tab === 'automation' && automationTab === 'runtime' && (
            <RecommendationRecoveryPanel
              computations={[
                { label: '稀客推荐', error: orderRecommendations.error, retry: orderRecommendations.retry },
                {
                  label: '普客执行计划',
                  error: normalExecutionTargets.error,
                  retry: normalExecutionTargets.retry,
                },
              ]}
            />
          )}
          {tab === 'automation' && (
            <Tabs
              value={automationTab}
              onValueChange={(value) => setAutomationTab(value as 'runtime' | 'configuration')}
              className="space-y-4"
            >
              <TabsList className="grid h-auto w-full grid-cols-2" data-automation-tabs="true">
                <TabsTrigger value="runtime">运行状态</TabsTrigger>
                <TabsTrigger value="configuration">执行配置</TabsTrigger>
              </TabsList>
              <TabsContent value="runtime">
                {automationTab === 'runtime' && (
                  <AutomationRuntimePanel
                    autoPrepBusy={autoPrepBusy}
                    autoPrepMessage={autoPrepMessage}
                    autoPrepPaused={autoPrepPaused}
                    rareOrderDiagnostics={rareOrderDiagnostics}
                    autoPrepPreferences={companionPreferences}
                    normalOrderBusy={normalOrderBusy}
                    normalOrderMessage={normalOrderMessage}
                    normalOrderPausedCount={normalOrderPausedCount}
                    normalOrderDiagnostics={normalOrderDiagnostics}
                    automationRuntimeBlockReason={nightBusinessAutomationBlockReason}
                    automationSafetyBarriers={automationSafetyBarriers}
                    automationBarrierAckBusyKey={automationBarrierAckBusyKey}
                    onRetryRareAutomationOrder={retryRareAutomationOrder}
                    onResetRareAutomationOrder={resetRareAutomationOrder}
                    onRetryNormalAutomationOrder={retryNormalAutomationOrder}
                    onResetNormalAutomationOrder={resetNormalAutomationOrder}
                    onAcknowledgeAutomationBarrier={acknowledgeAutomationBarrierEvent}
                    showDebugDetails={companionPreferences.showDebugDetails}
                  />
                )}
              </TabsContent>
              <TabsContent value="configuration">
                {automationTab === 'configuration' && (
                  <AutomationSettingsPanel
                    preferences={editableCompanionPreferences}
                    deviceAuthority={companionDeviceAuthority}
                    onSharedPreferenceChange={updateSharedCompanionPreferences}
                    onOpenDevices={openDevices}
                  />
                )}
              </TabsContent>
            </Tabs>
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
                <TabsTrigger
                  value="missions"
                  className={INNER_TAB_TRIGGER_CLASS}
                  data-gamepad-clickable="true"
                >
                  任务列表
                </TabsTrigger>
                <TabsTrigger
                  value="rare-invitations"
                  className={INNER_TAB_TRIGGER_CLASS}
                  data-gamepad-clickable="true"
                >
                  稀客邀请
                </TabsTrigger>

                <TabsTrigger
                  value="inventory"
                  className={INNER_TAB_TRIGGER_CLASS}
                  data-gamepad-clickable="true"
                >
                  库存
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
                    connected={companionConnected}
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
                  <div className="space-y-4">
                    <InventoryOverviewPanel runtime={runtime} indexes={recommendationIndexes} />
                    <ModInventoryPanel
                      operations={inventoryOperations}
                      runtimeSets={runtimeSets}
                      runtimeLoaded={snapshot?.runtimeLoaded ?? false}
                      data={recommendationData}
                    />
                  </div>
                )}
              </TabsContent>
            </Tabs>
          )}
        </TabsContent>

        <TabsContent value="settings" data-gamepad-scope="content">
          {tab === 'settings' && (
            <ModSettingsPanel
              endpoint={normalizedEndpoint}
              apiToken={apiToken}
              preferences={editableCompanionPreferences}
              themeMode={themeMode}
              settingsTab={settingsTab}
              updateManager={updateManager}
              onLocalPreferenceChange={updateLocalCompanionPreferences}
              desktopWindowControls={desktopWindowControls}
              onSettingsTabChange={setSettingsTab}
              onThemeModeChange={setThemeMode}
              supportsDesktopWindowControls={companionPlatform === 'desktop'}
            />
          )}
        </TabsContent>
      </Tabs>
    </div>
  );
}

async function toggleCompanionFocus(focusSwitchBehavior: FocusSwitchBehavior, focusSwitchCooldownMs: number) {
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
