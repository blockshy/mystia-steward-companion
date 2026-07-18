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
import {
  buildAutomationLeaseConnectionKey,
  isAutomationLeaseOwnedForConnection,
} from '@/companion/connection-recovery';
import { useCustomRecipes } from '@/companion/hooks/useCustomRecipes';
import { useFavorites } from '@/companion/hooks/useFavorites';
import { useGameUiPinningPublisher } from '@/companion/hooks/useGameUiPinningPublisher';
import { useOrderAutomationIntervals } from '@/companion/hooks/useOrderAutomationIntervals';
import {
  canAdvanceAutomationRuntimeEventSequence,
  getAutomationStageFailureRetirement,
  isAutomationResponseCurrent,
  requiresManualAutomationResolution,
  resolveAutomationResponseStage,
  resolveAutomationWaitingStep,
  selectAutomationRequestStage,
  shouldRetireMissingManualBarrier,
  type AutomationRequestStage,
} from '@/companion/automation-machine';
import { useOrderRecommendations } from '@/companion/hooks/useOrderRecommendations';
import { useRareGuestInvitations } from '@/companion/hooks/useRareGuestInvitations';
import { ModCustomRecipesPanel } from '@/companion/pages/ModCustomRecipesPanel';
import { ModHelpPanel } from '@/companion/pages/ModHelpPanel';
import { ModInventoryPanel } from '@/companion/pages/ModInventoryPanel';
import { ModLogsPanel } from '@/companion/pages/ModLogsPanel';
import { ModNormalPanel } from '@/companion/pages/ModNormalPanel';
import { ModOverviewPanel } from '@/companion/pages/ModOverviewPanel';
import { ModRarePanel } from '@/companion/pages/ModRarePanel';
import {
  ModServicePanel,
  ServiceFocusPage,
  type ServicePanelView,
  type ServiceRecommendationTab,
} from '@/companion/pages/ModServicePanel';
import { ModSettingsPanel } from '@/companion/pages/ModSettingsPanel';
import { ModTasksPanel } from '@/companion/pages/ModTasksPanel';
import {
  acknowledgeAutomationSafetyBarrier,
  acquireAutomationLease,
  appendAutomationDecisionDiagnostic,
  cancelAutomationCookingJobs,
  completeFirstNormalOrder,
  completeFirstRareOrder,
  dismissRuntimeRareOrder,
  prepareNextRareOrder,
} from '@/companion/api';
import {
  didCookingMismatchStored,
  didCompleteStepCode,
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
  updateAutomationAfterResponse,
  type AutoFirstOrderState,
  type AutomationStep,
  type NormalAutoOrderState,
  type OrderPreparationResponse,
  type RareAutomationRecipeTarget,
} from '@/companion/automation-state';
import {
  applyRareServedStateFromResponse,
  buildAutoOrderKey,
  buildGameUiPinningTarget,
  buildNightBusinessOrderKey,
  buildNormalCookingTargetDecision,
  buildNormalAutoOrderDiagnostics,
  buildNormalAutoOrderKey,
  buildNormalCookerDemand,
  buildNormalOrderAutomationSignature,
  buildRareAutoOrderDiagnostic,
  formatRareAutomationMissingBeverageTargetMessage,
  formatRareAutomationMissingRecipeTargetMessage,
  formatOrderPreparationResponse,
  getSpecialBusinessRareCookingDeferral,
  hasAutomationActionEnabled,
  hasNormalOrderActionEnabled,
  lockRareAutomationTargets,
  reconcileRareRecipeTargetForSpecialBusiness,
  reserveAutomationCookerSlot,
  reserveRareCookerSlot,
  selectOrderPreparationCandidates,
  shouldAttemptNormalBeverage,
  shouldAttemptNormalCompletion,
  shouldAttemptNormalCooking,
  syncNormalOrderStateWithSnapshot,
  syncRareStateWithOrderServedState,
  type OrderPreparationCandidateResult,
  type ValidOrderPreparationSelection,
} from '@/companion/domain/automation';
import {
  buildAutomationCookerCapacity,
  buildRuntimeSets,
  getRareCookerRequirement,
} from '@/companion/domain/cookers';
import {
  isUsableRareCustomer,
  normalizePlace,
  toRuntimeRareCustomer,
} from '@/companion/domain/service-recommendations';
import { sortNormalOrders } from '@/companion/domain/sorting';
import {
  buildSpecialBusinessOrderRule,
  buildWackyRejectedRecipeKeyForRareRecipe,
  buildWackyRejectedRecipeKeyFromEvent,
  isWackyKoishiBossFullFeedContext,
  isWackyTargetTagMismatchEvent,
  WACKY_TARGET_TAG_COOKING_MIN_PROGRESS,
} from '@/companion/domain/special-business';
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
  persistAutomationCancellationEndpoint,
  persistCustomRecipeGroupMode,
  persistFocusBeverageLimit,
  persistFocusCompact,
  persistFocusRecipeLimit,
  persistTab,
  readStoredFocusBeverageLimit,
  readStoredFocusCompact,
  readStoredFocusRecipeLimit,
  readStoredCustomRecipeGroupMode,
  readStoredAutomationCancellationEndpoint,
  readStoredTab,
} from '@/companion/storage';
import type {
  AutomationSafetyBarrierAckResponse,
  AutomationSafetyBarrierDiagnostic,
  AutomationRuntimeEvent,
  AutomationCookingJobSnapshot,
  AutomationCookerCycle,
  CustomRecipeData,
  CustomRecipeGroupMode,
  FavoriteData,
  LocalApiAutomationLease,
  ModTab,
  NightBusinessOrder,
  NormalAutoOrderDiagnostic,
  NormalBusinessOrder,
  OrderRecommendation,
  RareAutoOrderDiagnostic,
  RecommendationStateSnapshot,
  SettingsTab,
  SpecialBusinessContext,
} from '@/companion/types';
import type {
  NormalExecutionTargetSelection,
  OrderRecommendationWorkerPayload,
} from '@/companion/workers/order-recommendations.types';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import {
  buildRecommendationDataIndexes,
  buildRecommendationDataSet,
  buildRecommendationDataSignature,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import { useThemeMode } from '@/lib/theme';
import type { PlaceName } from '@/lib/catalog-types';

const AUTO_FIRST_ORDER_TICK_MS = 1500;
const AUTO_NORMAL_ORDER_TICK_MS = 800;
const AUTOMATION_LEASE_RENEW_INTERVAL_MS = 3000;
const MAX_SPECIAL_BUSINESS_REJECTED_RECIPE_KEYS = 64;
const MOD_TAB_TRIGGER_CLASS = 'min-w-[4.75rem] flex-none min-[720px]:min-w-0 min-[720px]:flex-1';
type CompanionPlatform = 'desktop' | 'mobile';

const MOD_TABS: ModTab[] = ['overview', 'normal', 'rare', 'custom-recipes', 'service', 'tasks', 'inventory', 'help', 'logs', 'settings'];
const BASIC_MOD_TABS: ModTab[] = MOD_TABS.filter((tab) => tab !== 'logs');
const EMPTY_WORKER_FAVORITES: FavoriteData = { version: 0, recipes: [], beverages: [] };
const EMPTY_WORKER_CUSTOM_RECIPES: CustomRecipeData = { version: 0, enabled: false, recipes: [] };

interface NormalOrderDetailInput {
  include: boolean;
  normalOrders: NormalBusinessOrder[];
  runtime: RecommendationStateSnapshot | null;
  preferences: CompanionPreferences;
  specialBusiness: SpecialBusinessContext | null;
  rejectedRecipeKeys: string[];
}

interface NormalAutomationDecisionFlags {
  needsBeverage: boolean;
  needsCooking: boolean;
  needsCompletion: boolean;
  shouldHandleBeverage: boolean;
  shouldStartCooking: boolean;
  shouldCompleteOrder: boolean;
  forceKoishiFullFeedAutomation: boolean;
  targetBlockedCooking: boolean;
}

interface NormalAutomationDecisionDiagnosticInput {
  eventName: string;
  reason: string;
  order: NormalBusinessOrder;
  state: NormalAutoOrderState | null | undefined;
  targetSelection: NormalExecutionTargetSelection;
  requestPreferences: CompanionPreferences;
  flags: NormalAutomationDecisionFlags;
}

interface AutomationLeaseAcquireEntry {
  key: string;
  promise: Promise<LocalApiAutomationLease>;
}

interface AutomationBarrierAckEntry {
  key: string;
  sessionId: string;
  sequence: number;
}

function useSignedValue<T>(value: T, signature: string): T {
  // Signature covers the semantic fields that should invalidate this value.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  return useMemo(() => value, [signature]);
}

function buildNormalOrderDetailInputSignature(input: NormalOrderDetailInput): string {
  if (!input.include) return 'disabled';
  return [
    buildNormalOrderDetailOrdersSignature(input.normalOrders),
    buildNormalOrderDetailRuntimeSignature(input.runtime),
    buildNormalOrderDetailSpecialBusinessSignature(input.specialBusiness),
    buildNormalOrderDetailPreferenceSignature(input.preferences),
    stableStringArraySignature(input.rejectedRecipeKeys),
  ].join('\n');
}

function getNormalAutomationTargetSelection(
  order: NormalBusinessOrder,
  enabled: boolean,
  selections: ReadonlyMap<string, NormalExecutionTargetSelection>,
): NormalExecutionTargetSelection {
  const orderKey = buildNormalAutoOrderKey(order);
  if (!enabled) {
    return {
      orderKey,
      target: null,
      message: '',
    };
  }
  return selections.get(orderKey) ?? {
    orderKey,
    target: null,
    message: '特殊经营执行目标计算中，等待下一轮。',
  };
}

function buildNormalOrderWorkerPayload(
  input: NormalOrderDetailInput,
  data: RecommendationDataSet,
  {
    includeDetails = false,
    includeExecutionTargets = false,
    usage,
  }: {
    includeDetails?: boolean;
    includeExecutionTargets?: boolean;
    usage?: OrderRecommendationWorkerPayload['usage'];
  },
): OrderRecommendationWorkerPayload {
  const normalOrders = input.include ? input.normalOrders : [];
  return {
    orders: [],
    normalOrders,
    includeNormalOrderDetails: input.include && includeDetails,
    includeNormalExecutionTargets: input.include && includeExecutionTargets,
    runtime: input.runtime,
    runtimeRareCustomers: [],
    favorites: EMPTY_WORKER_FAVORITES,
    customRecipes: EMPTY_WORKER_CUSTOM_RECIPES,
    preferences: input.preferences,
    activeRareGuests: [],
    missionServeTargets: [],
    specialBusiness: input.specialBusiness,
    specialBusinessRejectedRecipeKeys: input.rejectedRecipeKeys,
    data,
    usage,
  };
}

function buildNormalOrderDetailOrdersSignature(orders: readonly NormalBusinessOrder[]): string {
  return sortNormalOrders([...orders])
    .map((order) => [
      buildNormalAutoOrderKey(order),
      order.traceId ?? '',
      order.deskCode,
      order.guestId ?? '',
      order.guestName,
      order.specialBusinessRole ?? '',
      order.specialBusinessRoleLabel ?? '',
      stableStringArraySignature(order.foodPreferenceTags),
      stableStringArraySignature(order.beveragePreferenceTags),
      order.fund ?? '',
      order.baseFundCarry ?? '',
      order.maxFundCarry ?? '',
      order.extraFundByBuff ?? '',
      order.willPayMoney ?? '',
      order.remainingOrderCount ?? '',
      order.foodId,
      order.foodName,
      order.beverageId,
      order.beverageName,
      order.hasServedFood ? 1 : 0,
      order.hasServedBeverage ? 1 : 0,
      order.readyToEvaluate ? 1 : 0,
      order.hasEvaluated ? 1 : 0,
      order.controllerAvailable === false ? 0 : 1,
      order.canAutomate === false ? 0 : 1,
      order.actionBlockReason ?? '',
      order.firstSeenAtUtc ?? '',
      order.source,
    ].join('~'))
    .join('|');
}

function buildNormalOrderDetailRuntimeSignature(runtime: RecommendationStateSnapshot | null): string {
  if (!runtime) return 'runtime:null';
  return [
    stableNumberArraySignature(runtime.availableRecipeIds),
    stableNumberArraySignature(runtime.availableBeverageIds),
    stableNumberArraySignature(runtime.availableIngredientIds),
    stableNumberRecordSignature(runtime.ownedIngredientQty),
    stableNumberRecordSignature(runtime.ownedBeverageQty),
    stableNumberArraySignature(runtime.placedCookerTypeIds),
    buildPlacedCookerSignature(runtime.placedCookers),
    runtime.popularFoodTag ?? '',
    runtime.popularHateFoodTag ?? '',
    runtime.famousShopEnabled ? 1 : 0,
  ].join('|');
}

function buildNormalOrderDetailSpecialBusinessSignature(specialBusiness: SpecialBusinessContext | null): string {
  if (!specialBusiness?.active) return 'special:none';
  return [
    specialBusiness.challengeType,
    specialBusiness.phase ?? '',
    stableStringArraySignature(specialBusiness.foodTargetTags),
    stableStringArraySignature(specialBusiness.beverageTargetTags),
    specialBusiness.targetFund ?? '',
    specialBusiness.currentValue ?? '',
    specialBusiness.maxValue ?? '',
    specialBusiness.targetValue ?? '',
    buildTargetTagProgressSignature(specialBusiness.targetTagTimeProgress),
    specialBusiness.wackyKoishiShieldBroken ?? '',
    stableStringArraySignature(specialBusiness.wackyKoishiFoodPreferenceTags),
    stableStringArraySignature(specialBusiness.wackyKoishiFoodHateTags),
    stableStringArraySignature(specialBusiness.wackyKoishiBeveragePreferenceTags),
    specialBusiness.recommendationPolicy,
    specialBusiness.automationPolicy,
  ].join('|');
}

function buildNormalOrderDetailPreferenceSignature(preferences: CompanionPreferences): string {
  return [
    preferences.filterMissingCookers ? 1 : 0,
    preferences.recommendationBudgetPolicy,
    preferences.recipeVariantLimitPerBase,
    stableNumberArraySignature(preferences.recommendationExclusions.excludedIngredientIds),
    stableNumberArraySignature(preferences.recommendationExclusions.excludedBeverageIds),
    JSON.stringify(preferences.recommendationSortProfile),
  ].join('|');
}

function buildTargetTagProgressSignature(value: number | null | undefined): string {
  if (!Number.isFinite(value)) return '';
  const progress = Math.max(0, Math.min(1, value ?? 0));
  if (progress >= WACKY_TARGET_TAG_COOKING_MIN_PROGRESS) return 'safe';
  return `wait:${Math.round(progress * 100)}`;
}

function buildOrderRecommendationPayloadSignature(payload: OrderRecommendationWorkerPayload): string {
  return [
    payload.usage ?? 'display',
    buildNightBusinessOrderSignature(payload.orders),
    buildNormalOrderDetailRuntimeSignature(payload.runtime),
    buildRuntimeRareCustomerSignature(payload.runtimeRareCustomers),
    buildFavoriteDataSignature(payload.favorites),
    buildCustomRecipeDataSignature(payload.customRecipes),
    buildOrderRecommendationPreferenceSignature(payload.preferences),
    buildActiveRareGuestSignature(payload.activeRareGuests),
    buildMissionServeTargetSignature(payload.missionServeTargets),
    buildNormalOrderDetailSpecialBusinessSignature(payload.specialBusiness ?? null),
    stableStringArraySignature(payload.specialBusinessRejectedRecipeKeys),
    buildRecommendationDataSignature(payload.data),
  ].join('\n');
}

function buildNightBusinessOrderSignature(orders: readonly NightBusinessOrder[]): string {
  return [...orders]
    .sort((left, right) =>
      left.deskCode - right.deskCode
      || (left.guestId ?? -1) - (right.guestId ?? -1)
      || left.guestName.localeCompare(right.guestName)
      || left.foodTagId - right.foodTagId
      || left.beverageTagId - right.beverageTagId
    )
    .map((order) => [
      order.traceId ?? '',
      order.deskCode,
      order.guestId ?? '',
      order.guestName,
      order.specialBusinessRole ?? '',
      order.specialBusinessRoleLabel ?? '',
      order.automationAllowed === false ? 0 : 1,
      order.automationBlockReason ?? '',
      order.foodTagId,
      order.foodTag,
      order.beverageTagId,
      order.beverageTag,
      order.source,
      order.firstSeenAtUtc ?? '',
      order.lastSeenAtUtc ?? '',
      order.isFreeOrder ? 1 : 0,
      order.fund ?? '',
      order.baseFundCarry ?? '',
      order.maxFundCarry ?? '',
      order.extraFundByBuff ?? '',
      order.willPayMoney ?? '',
      order.remainingOrderCount ?? '',
      order.hasServedFood ? 1 : 0,
      order.hasServedBeverage ? 1 : 0,
    ].join('~'))
    .join('|');
}

function buildRuntimeRareCustomerSignature(customers: readonly OrderRecommendationWorkerPayload['runtimeRareCustomers'][number][]): string {
  return [...customers]
    .sort((left, right) => left.id - right.id || left.name.localeCompare(right.name))
    .map((customer) => [
      customer.id,
      customer.name,
      stableStringArraySignature(customer.places),
      stableStringArraySignature(customer.positiveTags),
      stableStringArraySignature(customer.negativeTags),
      stableStringArraySignature(customer.beverageTags),
    ].join('~'))
    .join('|');
}

function buildFavoriteDataSignature(favorites: FavoriteData): string {
  return [
    favorites.version,
    [...favorites.recipes]
      .sort((left, right) =>
        left.customerId - right.customerId
        || left.foodTag.localeCompare(right.foodTag)
        || left.recipeId - right.recipeId
      )
      .map((favorite) => [
        favorite.customerId,
        favorite.foodTag,
        favorite.recipeId,
        stableNumberArraySignature(favorite.extraIngredientIds),
      ].join('~'))
      .join('|'),
    [...favorites.beverages]
      .sort((left, right) =>
        left.customerId - right.customerId
        || left.beverageTag.localeCompare(right.beverageTag)
        || left.beverageId - right.beverageId
      )
      .map((favorite) => [
        favorite.customerId,
        favorite.beverageTag,
        favorite.beverageId,
      ].join('~'))
      .join('|'),
  ].join('\n');
}

function buildCustomRecipeDataSignature(customRecipes: CustomRecipeData): string {
  return [
    customRecipes.version,
    customRecipes.enabled ? 1 : 0,
    [...customRecipes.recipes]
      .sort((left, right) =>
        left.customerId - right.customerId
        || (left.foodTag ?? '').localeCompare(right.foodTag ?? '')
        || left.foodId - right.foodId
        || left.recipeId - right.recipeId
      )
      .map((recipe) => [
        recipe.enabled ? 1 : 0,
        recipe.pinToTop ? 1 : 0,
        recipe.customerId,
        recipe.foodTag ?? '',
        recipe.foodId,
        recipe.recipeId,
        recipe.recipeName,
        stableNumberArraySignature(recipe.extraIngredientIds),
        recipe.sortOrder,
      ].join('~'))
      .join('|'),
  ].join('\n');
}

function buildOrderRecommendationPreferenceSignature(preferences: CompanionPreferences): string {
  return [
    preferences.serviceOrderSortMode,
    preferences.filterMissingCookers ? 1 : 0,
    preferences.pinMissionRecipeEnabled ? 1 : 0,
    preferences.pinFavoriteRecipeEnabled ? 1 : 0,
    preferences.pinFavoriteBeverageEnabled ? 1 : 0,
    preferences.autoPrepRecipeFavoritesOnly ? 1 : 0,
    preferences.autoPrepBeverageFavoritesOnly ? 1 : 0,
    preferences.recommendationBudgetPolicy,
    preferences.recipeVariantLimitPerBase,
    stableNumberArraySignature(preferences.recommendationExclusions.excludedIngredientIds),
    stableNumberArraySignature(preferences.recommendationExclusions.excludedBeverageIds),
    JSON.stringify(preferences.recommendationSortProfile),
  ].join('|');
}

function buildActiveRareGuestSignature(guests: readonly OrderRecommendationWorkerPayload['activeRareGuests'][number][]): string {
  return [...guests]
    .sort((left, right) =>
      left.deskCode - right.deskCode
      || (left.guestId ?? -1) - (right.guestId ?? -1)
      || left.guestName.localeCompare(right.guestName)
    )
    .map((guest) => [
      guest.deskCode,
      guest.guestId ?? '',
      guest.guestName,
      guest.source,
      guest.fund ?? '',
      guest.baseFundCarry ?? '',
      guest.maxFundCarry ?? '',
      guest.extraFundByBuff ?? '',
      guest.willPayMoney ?? '',
    ].join('~'))
    .join('|');
}

function buildMissionServeTargetSignature(targets: readonly OrderRecommendationWorkerPayload['missionServeTargets'][number][]): string {
  return [...targets]
    .sort((left, right) =>
      left.guestId - right.guestId
      || left.recipeId - right.recipeId
      || left.missionLabel.localeCompare(right.missionLabel)
    )
    .map((target) => [
      target.guestId,
      target.guestName,
      target.guestLabel,
      target.missionLabel,
      target.missionTitle,
      target.recipeId,
      target.recipeName,
      target.status,
      target.source,
    ].join('~'))
    .join('|');
}

function buildPlacedCookerSignature(cookers: RecommendationStateSnapshot['placedCookers']): string {
  return [...(cookers ?? [])]
    .sort((left, right) => left.controllerIndex - right.controllerIndex || left.name.localeCompare(right.name))
    .map((cooker) => [
      cooker.controllerIndex,
      cooker.isOpen ? 1 : 0,
      cooker.name,
      stableNumberArraySignature(cooker.typeIds),
      stableStringArraySignature(cooker.typeNames),
    ].join(':'))
    .join(',');
}

function stableNumberArraySignature(values: readonly number[] | undefined): string {
  return [...(values ?? [])]
    .filter((value) => Number.isFinite(value))
    .sort((left, right) => left - right)
    .join(',');
}

function stableStringArraySignature(values: readonly string[] | undefined): string {
  return [...(values ?? [])]
    .map((value) => value.trim())
    .filter(Boolean)
    .sort()
    .join(',');
}

function stableNumberRecordSignature(values: Record<string, number> | undefined): string {
  return Object.entries(values ?? {})
    .filter(([, value]) => Number.isFinite(value))
    .sort(([left], [right]) => Number(left) - Number(right))
    .map(([key, value]) => `${key}:${value}`)
    .join(',');
}

function buildAutomationDecisionDiagnosticSignature(
  eventName: string,
  message: string,
  specialBusiness: SpecialBusinessContext | null,
  snapshotSignature: string,
  orderLines: readonly string[],
  selectionLines: readonly string[],
  skipLines: readonly string[],
  preferences: CompanionPreferences,
  leaseOwned: boolean,
): string {
  return hashDiagnosticSignature([
    eventName,
    message,
    snapshotSignature,
    buildNormalOrderDetailSpecialBusinessSignature(specialBusiness),
    buildOrderRecommendationPreferenceSignature(preferences),
    leaseOwned ? 1 : 0,
    orderLines.join('|'),
    selectionLines.join('|'),
    skipLines.join('|'),
  ].join('\n'));
}

function buildAutomationDecisionOrderLine(item: OrderRecommendation): string {
  const order = item.order;
  return [
    `trace=${order.traceId ?? ''}`,
    `desk=${formatDesk(order.deskCode)}`,
    `guest=${order.guestName || '稀客'}`,
    `role=${order.specialBusinessRole ?? ''}`,
    `tags=${order.foodTag || '无'}/${order.beverageTag || '无'}`,
    `served=${order.hasServedFood ? 1 : 0}/${order.hasServedBeverage ? 1 : 0}`,
    `recommendations=${item.recipes.length}/${item.beverages.length}`,
    `plans=${item.executionPlans.length}`,
    `blocked=${item.blockedMessages.length}`,
    `blockedDetail=${formatRecommendationBlockedMessages(item)}`,
    `top=${formatRecommendationTopTarget(item)}`,
    `plan=${formatRecommendationPlanTarget(item.executionPlans[0] ?? item.preparationPlan ?? null)}`,
  ].join('; ');
}

function buildAutomationDecisionSelectionLine(selection: ValidOrderPreparationSelection): string {
  const order = selection.item.order;
  return [
    `trace=${order.traceId ?? ''}`,
    `desk=${formatDesk(order.deskCode)}`,
    `guest=${order.guestName || '稀客'}`,
    `role=${order.specialBusinessRole ?? ''}`,
    `recipe=${selection.recipeTarget?.recipeName ?? selection.recipe?.recipe.name ?? ''}`,
    `recipeId=${selection.recipeTarget?.recipeId ?? selection.recipe?.recipe.recipeId ?? -1}`,
    `foodId=${selection.recipeTarget?.foodId ?? selection.recipe?.recipe.id ?? -1}`,
    `extras=${selection.recipeTarget?.extraIngredientIds.join(',') ?? selection.recipe?.extraIngredients.map((ingredient) => ingredient.id).join(',') ?? ''}`,
    `beverage=${selection.beverageTarget?.beverageName ?? selection.beverage?.beverage.name ?? ''}`,
    `beverageId=${selection.beverageTarget?.beverageId ?? selection.beverage?.beverage.id ?? -1}`,
    `favorite=${selection.recipeFavorite ? 1 : 0}/${selection.beverageFavorite ? 1 : 0}`,
  ].join('; ');
}

function buildAutomationDecisionSkipLine(skip: OrderPreparationCandidateResult['skips'][number]): string {
  return [
    `orderKey=${skip.orderKey}`,
    `reason=${skip.reason}`,
    `recommendations=${skip.recipeRecommendationCount}/${skip.beverageRecommendationCount}`,
    `plans=${skip.executionPlanCount}`,
    `message=${compactDiagnosticText(skip.message)}`,
  ].join('; ');
}

function formatNormalAutomationTarget(target: NormalExecutionTargetSelection['target']): string {
  if (!target) return 'none';
  return [
    `${target.recipeName}#${target.recipeId}->${target.foodId}`,
    `${target.beverageName}#${target.beverageId}`,
    target.executionMode ? `mode=${target.executionMode}` : '',
    `match=${target.matchFoodId}/${target.matchBeverageId}`,
    `extras=${target.extraIngredientIds.join(',')}`,
    target.reason,
  ].filter(Boolean).join('/');
}

function buildNormalAutomationDecisionOrderLine(input: NormalAutomationDecisionDiagnosticInput): string {
  const { flags, order, state, targetSelection } = input;
  return [
    `trace=${order.traceId ?? ''}`,
    `orderKey=${targetSelection.orderKey}`,
    `desk=${formatDesk(order.deskCode)}`,
    `guest=${order.guestName || '普客'}`,
    `role=${order.specialBusinessRole ?? ''}`,
    `order=${order.foodName || `#${order.foodId}`}/${order.beverageName || `#${order.beverageId}`}`,
    `served=${order.hasServedFood ? 1 : 0}/${order.hasServedBeverage ? 1 : 0}`,
    `ready=${order.readyToEvaluate ? 1 : 0}`,
    `state=${state?.step ?? 'none'}/${state?.prepared ? 1 : 0}/${state?.beverageHandled ? 1 : 0}/${state?.foodDelivered ? 1 : 0}/${state?.completed ? 1 : 0}`,
    `needs=${flags.needsCooking ? 1 : 0}/${flags.needsBeverage ? 1 : 0}/${flags.needsCompletion ? 1 : 0}`,
    `actions=${flags.shouldStartCooking ? 1 : 0}/${flags.shouldHandleBeverage ? 1 : 0}/${flags.shouldCompleteOrder ? 1 : 0}`,
    `request=${input.requestPreferences.autoNormalStartCooking ? 1 : 0}/${input.requestPreferences.autoNormalTakeBeverage ? 1 : 0}/${input.requestPreferences.autoNormalDeliverFood ? 1 : 0}/${input.requestPreferences.autoNormalCompleteOrder ? 1 : 0}`,
    `forceKoishi=${flags.forceKoishiFullFeedAutomation ? 1 : 0}`,
    `targetBlockedCooking=${flags.targetBlockedCooking ? 1 : 0}`,
    `target=${formatNormalAutomationTarget(targetSelection.target)}`,
    `message=${compactDiagnosticText(targetSelection.message)}`,
  ].join('; ');
}

function formatRecommendationTopTarget(item: OrderRecommendation): string {
  const recipe = item.recipes[0] ?? null;
  const beverage = item.beverages[0] ?? null;
  return [
    recipe ? `${recipe.recipe.name}#${recipe.recipe.id}+${recipe.extraIngredients.map((ingredient) => ingredient.id).join(',')}` : '',
    beverage ? `${beverage.beverage.name}#${beverage.beverage.id}` : '',
  ].filter(Boolean).join('/');
}

function formatRecommendationPlanTarget(plan: OrderRecommendation['preparationPlan']): string {
  if (!plan) return '';
  return [
    plan.food ? `${plan.food.recipe.name}#${plan.food.recipe.id}+${plan.food.extraIngredients.map((ingredient) => ingredient.id).join(',')}` : '',
    plan.beverage ? `${plan.beverage.beverage.name}#${plan.beverage.beverage.id}` : '',
    plan.reasons[0] ?? '',
  ].filter(Boolean).join('/');
}

function formatRecommendationBlockedMessages(item: OrderRecommendation): string {
  return item.blockedMessages
    .slice(0, 3)
    .map(compactDiagnosticText)
    .join(' | ');
}

function compactDiagnosticText(value: string): string {
  return value.replace(/\s+/g, ' ').trim();
}

function hashDiagnosticSignature(value: string): string {
  let hash = 2166136261;
  for (let index = 0; index < value.length; index++) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(16).padStart(8, '0');
}

function buildRareAutomationDiagnosticsSignature(items: readonly RareAutoOrderDiagnostic[]): string {
  return items.map((item) => [
    item.orderKey,
    item.traceId ?? '',
    item.title,
    item.foodTag,
    item.beverageTag,
    item.recipeName,
    item.beverageName,
    item.stepLabel,
    item.stepSeconds,
    item.nextAction,
    item.retryCount,
    item.rollbackCount,
    item.lastError,
    item.detailMessage,
    item.detailUpdatedAtMs,
    item.prepared ? 1 : 0,
    item.beverageDeliveryRequested ? 1 : 0,
    item.hasServedFood ? 1 : 0,
    item.hasServedBeverage ? 1 : 0,
    item.paused ? 1 : 0,
    item.manualResolutionRequired ? 1 : 0,
  ].join('~')).join('|');
}

function buildNormalAutomationDiagnosticsSignature(items: readonly NormalAutoOrderDiagnostic[]): string {
  return items.map((item) => [
    item.orderKey,
    item.traceId ?? '',
    item.title,
    item.foodName,
    item.beverageName,
    item.source,
    item.stepLabel,
    item.stepSeconds,
    item.nextAction,
    item.retryCount,
    item.rollbackCount,
    item.lastError,
    item.detailMessage,
    item.detailUpdatedAtMs,
    item.prepared ? 1 : 0,
    item.beverageDeliveryRequested ? 1 : 0,
    item.foodDeliveryRequested ? 1 : 0,
    item.completed ? 1 : 0,
    item.paused ? 1 : 0,
    item.manualResolutionRequired ? 1 : 0,
    item.hasServedFood ? 1 : 0,
    item.hasServedBeverage ? 1 : 0,
    item.readyToEvaluate ? 1 : 0,
    item.hasEvaluated ? 1 : 0,
    item.controllerAvailable === false ? 0 : 1,
    item.canAutomate === false ? 0 : 1,
    item.actionBlockReason ?? '',
  ].join('~')).join('|');
}

function isCookingMismatchStoredEvent(event: AutomationRuntimeEvent): boolean {
  return event.code === 'cooking-mismatch-stored';
}

function isCookingTagsUnreadableStoredEvent(event: AutomationRuntimeEvent): boolean {
  return event.code === 'cooking-tags-unreadable-stored';
}

function isRecoverableCookingTerminalEvent(event: AutomationRuntimeEvent): boolean {
  return event.terminal && event.outcome === 'interrupted';
}

function isBlockingCookingTerminalEvent(event: AutomationRuntimeEvent): boolean {
  return event.terminal && (event.outcome === 'blocked' || event.outcome === 'fatal');
}

function isManualResolutionAutomationEvent(event: AutomationRuntimeEvent): boolean {
  return event.terminal && requiresManualAutomationResolution(event.reasonCode, [event.code]);
}

function resolveAutomationEventStage(event: AutomationRuntimeEvent): AutomationStep {
  const runtimeStage = event.code.startsWith('beverage-')
    ? 'beverage'
    : event.code.startsWith('order-')
      ? 'order'
      : event.code === 'cooking-start-unowned'
        ? 'cooking-start'
        : 'cooking-delivery';
  return resolveAutomationResponseStage(runtimeStage, 'ensure-cooking');
}

function isCookingAutomationEvent(event: AutomationRuntimeEvent): boolean {
  return event.code.startsWith('cooking-') || event.reasonCode.startsWith('cooking-');
}

function retainAutomationSafetyStates<T extends AutoFirstOrderState | NormalAutoOrderState>(
  states: Map<string, T>,
): void {
  for (const [orderKey, state] of states) {
    if (!state.manualResolutionRequired) states.delete(orderKey);
  }
}

function retainRareManualResolutionDiagnosticItems(
  states: ReadonlyMap<string, AutoFirstOrderState>,
  items: Map<string, ValidOrderPreparationSelection>,
): void {
  for (const orderKey of items.keys()) {
    if (!states.get(orderKey)?.manualResolutionRequired) items.delete(orderKey);
  }
}

function trimRejectedRecipeKeys(keys: readonly string[]): string[] {
  const merged: string[] = [];
  for (const key of keys) {
    if (!key || merged.includes(key)) continue;
    merged.push(key);
  }
  return merged.slice(-MAX_SPECIAL_BUSINESS_REJECTED_RECIPE_KEYS);
}

function rememberRejectedRecipeKey(keys: Set<string>, key: string): void {
  if (!key || keys.has(key)) return;
  keys.add(key);
  while (keys.size > MAX_SPECIAL_BUSINESS_REJECTED_RECIPE_KEYS) {
    const oldest = keys.values().next().value as string | undefined;
    if (!oldest) break;
    keys.delete(oldest);
  }
}

function mergeRejectedRecipeKeys(stateKeys: readonly string[], refKeys: Set<string>): string[] {
  return trimRejectedRecipeKeys([...stateKeys, ...refKeys]);
}

function buildRejectedRecipeKeyForRareTarget(
  specialBusiness: SpecialBusinessContext | null | undefined,
  order: NightBusinessOrder,
  target: RareAutomationRecipeTarget | null,
): string {
  if (!target) return '';
  const rule = buildSpecialBusinessOrderRule(specialBusiness, order.specialBusinessRole);
  if (!rule.requiresWackyFoodTarget || rule.foodTargetTags.length === 0) return '';
  return buildWackyRejectedRecipeKeyForRareRecipe(
    rule.foodTargetTags,
    target.foodId,
    target.recipeId,
    target.extraIngredientIds,
  );
}

function composeAutomationDetail(...parts: Array<string | null | undefined | false>): string {
  return parts
    .map((part) => typeof part === 'string' ? part.trim() : '')
    .filter(Boolean)
    .join('\n');
}

function withAutomationDetail<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  now: number,
  ...parts: Array<string | null | undefined | false>
): T {
  const detailMessage = composeAutomationDetail(...parts);
  if (!detailMessage) return state;
  return {
    ...state,
    detailMessage,
    detailUpdatedAtMs: now,
  };
}

function enforceAutomationRollbackLimit<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  maxRollbacks: number,
  now: number,
): T {
  if (state.paused || state.rollbackCount <= 0 || state.rollbackCount < maxRollbacks) return state;
  const limitMessage = `自动回退已达到上限 ${state.rollbackCount}/${maxRollbacks}，已暂停该订单。`;
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

function recordAutomationTransportFailure<T extends AutoFirstOrderState | NormalAutoOrderState>(
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

function retireDisabledRareAutomationFailure(
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
  if (preferences.autoPrepCompleteOrder && !forceFullFeed) enabledStages.push('complete-order');
  const retirement = getAutomationStageFailureRetirement({ ...state, enabledStages });
  if (!retirement.clearRetry && !retirement.clearPause) return state;

  const nextStep: AutomationStep = (preferences.autoPrepTakeBeverage || forceFullFeed)
    && !state.beverageHandled
    && !order.hasServedBeverage
    ? 'ensure-beverage'
    : (preferences.autoPrepStartCooking || forceFullFeed) && !state.prepared && !order.hasServedFood
      ? 'ensure-cooking'
      : (preferences.autoPrepCollectCooking || forceFullFeed) && state.prepared && !order.hasServedFood
        ? 'deliver-food'
        : preferences.autoPrepCompleteOrder && !forceFullFeed
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

function retireDisabledNormalAutomationFailure(
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
  if (preferences.autoNormalCompleteOrder && !forceFullFeed) enabledStages.push('complete-order');
  const retirement = getAutomationStageFailureRetirement({ ...state, enabledStages });
  if (!retirement.clearRetry && !retirement.clearPause) return state;

  const nextStep: AutomationStep = (preferences.autoNormalTakeBeverage || forceFullFeed)
    && !state.beverageHandled
    && !order.hasServedBeverage
    ? 'ensure-beverage'
    : (preferences.autoNormalStartCooking || forceFullFeed) && !state.prepared && !order.hasServedFood
      ? 'ensure-cooking'
      : (preferences.autoNormalDeliverFood || forceFullFeed) && state.prepared && !order.hasServedFood
        ? 'deliver-food'
        : preferences.autoNormalCompleteOrder && !forceFullFeed
          && (order.readyToEvaluate
            || ((state.foodDelivered || order.hasServedFood)
              && (state.beverageHandled || order.hasServedBeverage)))
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

function matchesRareAutomationEvent(
  event: AutomationRuntimeEvent,
  item: OrderRecommendation,
  state: AutoFirstOrderState,
): boolean {
  if (event.targetKind !== 'rare') return false;
  if (state.cookingJobId && event.jobId) return state.cookingJobId === event.jobId;
  const order = item.order;
  if (event.traceId && order.traceId) return event.traceId === order.traceId;
  if (event.foodId >= 0 && state.recipeTarget && state.recipeTarget.foodId !== event.foodId) return false;
  if (event.deskCode >= 0 && order.deskCode !== event.deskCode) return false;
  if (event.guestId != null && order.guestId != null && event.guestId !== order.guestId) return false;
  return true;
}

function isAutomationLeaseUnavailableResponse(response: OrderPreparationResponse): boolean {
  return response.automation.reasonCode === 'automation-lease-unavailable'
    || response.automation.stage === 'lease';
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

function resetRareOrderStateAfterRuntimeMismatch(
  state: AutoFirstOrderState,
  now: number,
  event: AutomationRuntimeEvent,
): AutoFirstOrderState {
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
    rollbackCount: state.rollbackCount + 1,
    nextAttemptAtMs: now + 500,
    lastError: event.message || '非目标成品已放入保温箱，重新制作目标料理。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: '',
    pauseReasonCode: '',
  };
}

function matchesNormalAutomationEvent(event: AutomationRuntimeEvent, order: NormalBusinessOrder): boolean {
  if (event.targetKind !== 'normal') return false;
  if (event.traceId && order.traceId) return event.traceId === order.traceId;
  const orderKey = buildNormalAutoOrderKey(order);
  if (event.orderKey && orderKey) return event.orderKey === orderKey;
  if (event.foodId >= 0 && order.foodId !== event.foodId) return false;
  if (event.deskCode >= 0 && order.deskCode !== event.deskCode) return false;
  if (event.guestName && order.guestName && event.guestName !== order.guestName) return false;
  return true;
}

function findRareAutomationCookingJob(
  jobs: readonly AutomationCookingJobSnapshot[],
  selection: ValidOrderPreparationSelection,
  state?: AutoFirstOrderState,
): AutomationCookingJobSnapshot | null {
  const order = selection.item.order;
  if (state?.cookingJobId) {
    return jobs.find((job) => job.targetKind === 'rare' && job.jobId === state.cookingJobId) ?? null;
  }

  const recipeTarget = state?.recipeTarget ?? selection.recipeTarget;
  return jobs.find((job) => {
    if (job.targetKind !== 'rare') return false;
    if (job.traceId && order.traceId) return job.traceId === order.traceId;
    return job.deskCode === order.deskCode
      && job.foodId === recipeTarget?.foodId
      && (job.guestId == null || order.guestId == null || job.guestId === order.guestId);
  }) ?? null;
}

function findNormalAutomationCookingJob(
  jobs: readonly AutomationCookingJobSnapshot[],
  order: NormalBusinessOrder,
  state?: NormalAutoOrderState,
): AutomationCookingJobSnapshot | null {
  if (state?.cookingJobId) {
    return jobs.find((job) => job.targetKind === 'normal' && job.jobId === state.cookingJobId) ?? null;
  }

  const orderKey = buildNormalAutoOrderKey(order);
  return jobs.find((job) => {
    if (job.targetKind !== 'normal') return false;
    if (job.traceId && order.traceId) return job.traceId === order.traceId;
    return Boolean(job.orderKey && job.orderKey === orderKey);
  }) ?? null;
}

function reconcileStateWithActiveCookingJob<T extends AutoFirstOrderState | NormalAutoOrderState>(
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
  return {
    ...state,
    prepared: true,
    cookingJobId: job.jobId,
    step: state.paused && !provesCookingProgress ? state.step : 'deliver-food',
    stepStartedAtMs: jobChanged && (!state.paused || provesCookingProgress) ? now : state.stepStartedAtMs,
    lastProgressAtMs: jobChanged ? now : state.lastProgressAtMs,
    retryCount: provesCookingProgress ? 0 : state.retryCount,
    retryStage: provesCookingProgress ? '' : state.retryStage,
    nextAttemptAtMs: provesCookingProgress ? 0 : state.nextAttemptAtMs,
    lastError: provesCookingProgress ? '' : state.lastError,
    paused: provesCookingProgress ? false : state.paused,
    pausedStage: provesCookingProgress ? '' : state.pausedStage,
    pauseReasonCode: provesCookingProgress ? '' : state.pauseReasonCode,
  };
}

function resetNormalOrderStateAfterRuntimeMismatch(
  state: NormalAutoOrderState,
  orderKey: string,
  now: number,
  event: AutomationRuntimeEvent,
): NormalAutoOrderState {
  return {
    ...state,
    orderKey,
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
    rollbackCount: state.rollbackCount + 1,
    nextAttemptAtMs: now + 500,
    lastError: event.message || '非目标成品已放入保温箱，重新制作目标料理。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: '',
    pauseReasonCode: '',
  };
}

function pauseRareOrderStateAfterRuntimeFailure(
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
    lastError: event.message || '运行时无法安全确认自动化副作用，已暂停该订单。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: manualResolutionRequired ? resolveAutomationEventStage(event) : state.step,
    pauseReasonCode: event.reasonCode || event.code,
  };
}

function pauseNormalOrderStateAfterRuntimeFailure(
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
    lastError: event.message || '运行时无法安全确认自动化副作用，已暂停该订单。',
    lastRuntimeEventSequence: event.sequence,
    pausedStage: manualResolutionRequired ? resolveAutomationEventStage(event) : state.step,
    pauseReasonCode: event.reasonCode || event.code,
  };
}

/**
 * 伴随窗口的根工作台组件。
 *
 * 这里汇总本地 API 连接、推荐数据、收藏、自动化状态、手柄导航和页面路由。组件本身不直接读取游戏对象；
 * 所有运行时输入来自 `useCompanionConnection` 的快照，所有回写操作通过 `api.ts` 发送到 Mod 本地 API。
 */
export function ModWorkbench() {
  const { mode: themeMode, setMode: setThemeMode } = useThemeMode();
  const [tab, setTab] = useState<ModTab>(() => readStoredTab());
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('window');
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
    setCustomRecipesEnabledState,
    updateCustomRecipeFlagsState,
    moveCustomRecipeEntry,
  } = useCustomRecipes({ apiToken, connectionPaused, normalizedEndpoint });
  const updateManager = useUpdateManager({
    endpoint: normalizedEndpoint,
    apiToken,
    connectionRevision,
    connected: Boolean(apiToken && !connectionPaused && !error && snapshot),
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
  const [serviceView, setServiceView] = useState<ServicePanelView>('recommendations');
  const [serviceRecommendationTab, setServiceRecommendationTab] = useState<ServiceRecommendationTab>('rare');
  const [automationLease, setAutomationLease] = useState<LocalApiAutomationLease | null>(null);
  const [automationLeaseBindingKey, setAutomationLeaseBindingKey] = useState('');
  const [automationLeaseError, setAutomationLeaseError] = useState('');
  const [automationBarrierAckBusyKey, setAutomationBarrierAckBusyKey] = useState('');
  const [automationBarrierAckErrors, setAutomationBarrierAckErrors] = useState<Record<number, string>>({});
  const [automationCancellationEndpoint, setAutomationCancellationEndpoint] = useState(
    readStoredAutomationCancellationEndpoint,
  );
  const [automationCancellationAttempt, setAutomationCancellationAttempt] = useState(0);
  const [specialBusinessRejectedRecipeKeys, setSpecialBusinessRejectedRecipeKeys] = useState<string[]>([]);
  const specialBusinessRejectedRecipeKeysRef = useRef(new Set<string>());
  // 自动化状态不放入 useState，是为了避免每个轮询 tick 都触发整页重渲染；页面只在诊断摘要变化时更新。
  const rareOrderStatesRef = useRef(new Map<string, AutoFirstOrderState>());
  const rareOrderDiagnosticItemsRef = useRef(new Map<string, ValidOrderPreparationSelection>());
  const autoFirstOrderBusyRef = useRef(false);
  const normalOrderStatesRef = useRef(new Map<string, NormalAutoOrderState>());
  const normalOrderBusyRef = useRef(false);
  const lastAutoFirstOrderAtRef = useRef(0);
  const lastAutoNormalOrderAtRef = useRef(0);
  const automationCookerCycleRef = useRef<AutomationCookerCycle | null>(null);
  const lastAutomationDecisionDiagnosticSignatureRef = useRef('');
  const automationRequestEpochRef = useRef(0);
  const automationLeaseAcquireRef = useRef<AutomationLeaseAcquireEntry | null>(null);
  const automationLeaseRevalidationRequiredRef = useRef(true);
  const automationStateSessionIdRef = useRef('');
  const automationLeaseOwnedRef = useRef(false);
  const automationBarrierAckRef = useRef<AutomationBarrierAckEntry | null>(null);
  const previousAutomationRuntimeEnabledRef = useRef(false);
  const automationRuntimeEnabledRef = useRef(false);
  const automationRefreshTimerRef = useRef<number | null>(null);
  const automationUiVisible = !serviceFocusMode && tab === 'service';
  const automationUiVisibleRef = useRef(automationUiVisible);
  const autoPrepBusyValueRef = useRef(false);
  const autoPrepMessageValueRef = useRef('');
  const autoPrepPausedValueRef = useRef(false);
  const rareOrderDiagnosticsValueRef = useRef<RareAutoOrderDiagnostic[]>([]);
  const rareOrderDiagnosticsSignatureRef = useRef('');
  const normalOrderBusyValueRef = useRef(false);
  const normalOrderMessageValueRef = useRef('');
  const normalOrderPausedCountValueRef = useRef(0);
  const normalOrderDiagnosticsValueRef = useRef<NormalAutoOrderDiagnostic[]>([]);
  const normalOrderDiagnosticsSignatureRef = useRef('');

  const publishAutoPrepBusy = useCallback((next: boolean) => {
    autoPrepBusyValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setAutoPrepBusy((current) => (current === next ? current : next));
  }, []);

  const publishAutoPrepMessage = useCallback((next: string) => {
    autoPrepMessageValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setAutoPrepMessage((current) => (current === next ? current : next));
  }, []);

  const publishAutoPrepPaused = useCallback((next: boolean) => {
    autoPrepPausedValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setAutoPrepPaused((current) => (current === next ? current : next));
  }, []);

  const publishRareOrderDiagnostics = useCallback((next: RareAutoOrderDiagnostic[]) => {
    const signature = buildRareAutomationDiagnosticsSignature(next);
    rareOrderDiagnosticsValueRef.current = next;
    if (rareOrderDiagnosticsSignatureRef.current === signature) return;
    rareOrderDiagnosticsSignatureRef.current = signature;
    if (!automationUiVisibleRef.current) return;
    setRareOrderDiagnostics(next);
  }, []);

  const publishNormalOrderBusy = useCallback((next: boolean) => {
    normalOrderBusyValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderBusy((current) => (current === next ? current : next));
  }, []);

  const publishNormalOrderMessage = useCallback((next: string) => {
    normalOrderMessageValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderMessage((current) => (current === next ? current : next));
  }, []);

  const publishNormalOrderPausedCount = useCallback((next: number) => {
    normalOrderPausedCountValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderPausedCount((current) => (current === next ? current : next));
  }, []);

  const publishNormalOrderDiagnostics = useCallback((next: NormalAutoOrderDiagnostic[]) => {
    const signature = buildNormalAutomationDiagnosticsSignature(next);
    normalOrderDiagnosticsValueRef.current = next;
    if (normalOrderDiagnosticsSignatureRef.current === signature) return;
    normalOrderDiagnosticsSignatureRef.current = signature;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderDiagnostics(next);
  }, []);

  useEffect(() => {
    automationUiVisibleRef.current = automationUiVisible;
    if (!automationUiVisible) return;
    setAutoPrepBusy((current) => (current === autoPrepBusyValueRef.current ? current : autoPrepBusyValueRef.current));
    setAutoPrepMessage((current) => (current === autoPrepMessageValueRef.current ? current : autoPrepMessageValueRef.current));
    setAutoPrepPaused((current) => (current === autoPrepPausedValueRef.current ? current : autoPrepPausedValueRef.current));
    setRareOrderDiagnostics(rareOrderDiagnosticsValueRef.current);
    setNormalOrderBusy((current) => (current === normalOrderBusyValueRef.current ? current : normalOrderBusyValueRef.current));
    setNormalOrderMessage((current) => (current === normalOrderMessageValueRef.current ? current : normalOrderMessageValueRef.current));
    setNormalOrderPausedCount((current) => (
      current === normalOrderPausedCountValueRef.current ? current : normalOrderPausedCountValueRef.current
    ));
    setNormalOrderDiagnostics(normalOrderDiagnosticsValueRef.current);
  }, [automationUiVisible]);

  const scheduleAutomationRefresh = useCallback(() => {
    if (automationRefreshTimerRef.current !== null) return;
    automationRefreshTimerRef.current = window.setTimeout(() => {
      automationRefreshTimerRef.current = null;
      void refresh();
    }, 180);
  }, [refresh]);

  const isAutomationRequestCurrent = useCallback((
    requestEpoch: number,
    responseStartEventSequence = 0,
    currentEventSequence = 0,
  ) => isAutomationResponseCurrent({
    requestEpoch,
    currentEpoch: automationRequestEpochRef.current,
    runtimeEnabled: automationRuntimeEnabledRef.current,
    responseStartEventSequence,
    currentEventSequence,
  }), []);

  const handleAutomationControlPlaneResponse = useCallback((response: OrderPreparationResponse): boolean => {
    if (!isAutomationLeaseUnavailableResponse(response)) return false;
    automationRequestEpochRef.current += 1;
    automationRuntimeEnabledRef.current = false;
    automationLeaseRevalidationRequiredRef.current = true;
    setAutomationLease(null);
    setAutomationLeaseBindingKey('');
    setAutomationLeaseError(response.error || '自动化控制权已失效，正在重新获取。');
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    return true;
  }, []);

  useEffect(() => () => {
    if (automationRefreshTimerRef.current === null) return;
    window.clearTimeout(automationRefreshTimerRef.current);
    automationRefreshTimerRef.current = null;
  }, []);

  const getSpecialBusinessRejectedRecipeKeys = useCallback(
    () => mergeRejectedRecipeKeys(specialBusinessRejectedRecipeKeys, specialBusinessRejectedRecipeKeysRef.current),
    [specialBusinessRejectedRecipeKeys],
  );

  const rememberSpecialBusinessRejectedRecipeKey = useCallback((rejectedKey: string) => {
    if (!rejectedKey) return;
    rememberRejectedRecipeKey(specialBusinessRejectedRecipeKeysRef.current, rejectedKey);
    setSpecialBusinessRejectedRecipeKeys((current) => trimRejectedRecipeKeys([
      ...current,
      ...specialBusinessRejectedRecipeKeysRef.current,
    ]));
  }, []);

  const updateCompanionPreferences = useCallback((next: Partial<CompanionPreferences>) => {
    if (next.automationEnabled === false) {
      automationRequestEpochRef.current += 1;
      persistAutomationCancellationEndpoint(normalizedEndpoint);
      setAutomationCancellationEndpoint(normalizedEndpoint);
      setAutomationCancellationAttempt(0);
    }
    setCompanionPreferences((current) => normalizeCompanionPreferences({ ...current, ...next }));
  }, [normalizedEndpoint]);

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
  const connectionReadyForActions = Boolean(apiToken && !connectionPaused && !error && snapshot);
  if (!connectionReadyForActions) automationLeaseRevalidationRequiredRef.current = true;
  const automationSessionId = snapshot?.automationSessionId.trim() ?? '';
  const automationLeaseConnectionKey = buildAutomationLeaseConnectionKey(
    { endpoint: normalizedEndpoint, apiToken },
    automationSessionId,
  );
  const automationCancellationPending = Boolean(automationCancellationEndpoint);
  const automationCancellationEndpointMatchesConnection = !automationCancellationPending
    || automationCancellationEndpoint === normalizedEndpoint;
  const automationLeaseOwned = isAutomationLeaseOwnedForConnection(
    automationLease,
    automationLeaseBindingKey,
    automationLeaseConnectionKey,
    automationLeaseRevalidationRequiredRef.current,
  );
  automationLeaseOwnedRef.current = automationLeaseOwned;
  const automationRuntimeEnabled = companionPreferences.automationEnabled
    && connectionReadyForActions
    && Boolean(automationSessionId)
    && automationLeaseOwned
    && !automationCancellationPending;
  if (previousAutomationRuntimeEnabledRef.current && !automationRuntimeEnabled) {
    automationRequestEpochRef.current += 1;
  }
  previousAutomationRuntimeEnabledRef.current = automationRuntimeEnabled;
  automationRuntimeEnabledRef.current = automationRuntimeEnabled;
  useEffect(() => {
    if (!automationCancellationPending || automationCancellationEndpointMatchesConnection) return;
    setAutomationLeaseError(`仍需连接 ${automationCancellationEndpoint} 完成上次关闭自动化的取消确认。`);
  }, [
    automationCancellationEndpoint,
    automationCancellationEndpointMatchesConnection,
    automationCancellationPending,
  ]);
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
  const runtimeRareCustomers = useMemo(
    () => (snapshot?.runtimeRareCustomers ?? [])
      .map(toRuntimeRareCustomer)
      .filter(isUsableRareCustomer),
    [snapshot?.runtimeRareCustomers],
  );

  const acquireAutomationLeaseSingleFlight = useCallback((): Promise<LocalApiAutomationLease> => {
    const key = automationLeaseConnectionKey;
    if (!key) return Promise.reject(new Error('自动化运行实例尚未就绪。'));
    const current = automationLeaseAcquireRef.current;
    if (current?.key === key) return current.promise;

    const promise = current
      ? current.promise.catch(() => undefined).then(() => acquireAutomationLease(normalizedEndpoint, apiToken))
      : acquireAutomationLease(normalizedEndpoint, apiToken);
    const entry: AutomationLeaseAcquireEntry = { key, promise };
    automationLeaseAcquireRef.current = entry;
    const clearEntry = () => {
      if (automationLeaseAcquireRef.current === entry) automationLeaseAcquireRef.current = null;
    };
    void promise.then(clearEntry, clearEntry);
    return promise;
  }, [apiToken, automationLeaseConnectionKey, normalizedEndpoint]);

  const waitForAutomationLeaseAcquire = useCallback(async (): Promise<void> => {
    while (automationLeaseAcquireRef.current) {
      const entry = automationLeaseAcquireRef.current;
      try {
        await entry.promise;
      } catch {
        // Cancellation still has to run after a failed acquire attempt.
      }
      if (automationLeaseAcquireRef.current === entry) return;
    }
  }, []);

  useEffect(() => {
    const shouldHoldLease = companionPreferences.automationEnabled || automationCancellationPending;
    if (!shouldHoldLease
      || !connectionReadyForActions
      || !automationLeaseConnectionKey
      || !automationCancellationEndpointMatchesConnection
      || (automationCancellationPending && automationLeaseOwned)) return undefined;

    let cancelled = false;
    const renewLease = async () => {
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
    automationCancellationPending,
    automationCancellationEndpointMatchesConnection,
    automationLeaseConnectionKey,
    automationLeaseOwned,
    companionPreferences.automationEnabled,
    connectionReadyForActions,
    normalizedEndpoint,
  ]);

  useEffect(() => {
    if (!automationCancellationPending
      || !automationLeaseOwned
      || !apiToken
      || !automationCancellationEndpointMatchesConnection
      || !connectionReadyForActions) return undefined;

    let disposed = false;
    const retryDelayMs = Math.min(5000, automationCancellationAttempt * 1000);
    const timer = window.setTimeout(() => {
      void (async () => {
        try {
          await waitForAutomationLeaseAcquire();
          if (disposed) return;
          const response = await cancelAutomationCookingJobs(automationCancellationEndpoint, apiToken);
          if (disposed) return;
          if (!response.ok || !response.leaseReleased) {
            setAutomationLease(null);
            setAutomationLeaseBindingKey('');
            throw new Error(response.error || 'Mod 未确认自动化取消屏障。');
          }
          persistAutomationCancellationEndpoint('');
          setAutomationCancellationEndpoint('');
          setAutomationCancellationAttempt(0);
          setAutomationLease(null);
          setAutomationLeaseBindingKey('');
          setAutomationLeaseError('');
          publishAutoPrepMessage(`自动化\n已取消 ${response.cancelledJobs} 个料理任务和 ${response.cancelledCommands} 个排队命令。`);
        } catch (err) {
          if (disposed) return;
          setAutomationLeaseError(err instanceof Error ? err.message : String(err));
          setAutomationCancellationAttempt((current) => current + 1);
        }
      })();
    }, retryDelayMs);
    return () => {
      disposed = true;
      window.clearTimeout(timer);
    };
  }, [
    apiToken,
    automationCancellationAttempt,
    automationCancellationEndpoint,
    automationCancellationEndpointMatchesConnection,
    automationLeaseOwned,
    automationCancellationPending,
    connectionReadyForActions,
    publishAutoPrepMessage,
    waitForAutomationLeaseAcquire,
  ]);

  useEffect(() => {
    if (!companionPreferences.automationEnabled) return;

    if (!connectionReadyForActions) {
      publishAutoPrepMessage('自动化\n连接不可用，已暂停执行。');
      publishNormalOrderMessage('');
      return;
    }

    if (!automationLease) {
      publishAutoPrepMessage(automationLeaseError
        ? `自动化控制权\n${automationLeaseError}`
        : '自动化\n正在获取本窗口自动化控制权。');
      publishNormalOrderMessage('');
      return;
    }

    if (!automationLeaseOwned) {
      const owner = automationLease.ownerLabel || '其他设备';
      publishAutoPrepMessage(`自动化控制权\n${automationLease.error || `自动化当前由 ${owner} 控制，本窗口仅查看。`}`);
      publishNormalOrderMessage('');
      return;
    }

    if (automationLeaseError) {
      publishAutoPrepMessage(`自动化控制权\n${automationLeaseError}`);
    }
  }, [
    automationLease,
    automationLeaseError,
    automationLeaseOwned,
    companionPreferences.automationEnabled,
    connectionReadyForActions,
    publishAutoPrepMessage,
    publishNormalOrderMessage,
  ]);

  const runtimeSets = useMemo(() => buildRuntimeSets(runtime, recommendationData), [recommendationData, runtime]);
  const normalOrderSignature = useMemo(
    () => `${buildNormalOrderAutomationSignature(snapshot?.normalBusiness?.orders ?? [])}|jobs:${(snapshot?.automationCookingJobs ?? [])
      .filter((job) => job.targetKind === 'normal')
      .map((job) => `${job.jobId}:${job.state}:${job.reasonCode}`)
      .join(',')}`,
    [snapshot?.automationCookingJobs, snapshot?.normalBusiness?.orders],
  );
  const automationSafetyBarriers = useMemo<AutomationSafetyBarrierDiagnostic[]>(() => {
    const latestByTarget = new Map<string, AutomationRuntimeEvent>();
    for (const event of snapshot?.automationEvents ?? []) {
      if (!isManualResolutionAutomationEvent(event)) continue;
      const targetIdentity = event.targetKind === 'normal' && event.orderKey
        ? `normal:${event.orderKey}`
        : event.traceId
          ? `${event.targetKind}:${event.traceId}`
          : `${event.targetKind}:sequence:${event.sequence}`;
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
        message: event.message || 'Mod 无法确认自动化副作用，请检查游戏现场。',
        error: automationBarrierAckErrors[event.sequence] ?? '',
      }));
  }, [automationBarrierAckErrors, snapshot?.automationEvents]);
  const specialBusinessFoodTargetSignature = useMemo(
    () => snapshot?.specialBusiness?.foodTargetTags.join('|') ?? '',
    [snapshot?.specialBusiness?.foodTargetTags],
  );
  const visibleTabs = companionPreferences.showDebugDetails ? MOD_TABS : BASIC_MOD_TABS;
  const serviceRecommendationsVisible = tab === 'service' && serviceView === 'recommendations';
  const includeNormalOrderDetails = serviceRecommendationsVisible && serviceRecommendationTab === 'normal';
  const normalAutomationTargetsEnabled = automationRuntimeEnabled
    && companionPreferences.autoNormalOrderEnabled
    && hasNormalOrderActionEnabled(companionPreferences)
    && Boolean(snapshot?.specialBusiness?.active);
  const rareAutomationNeedsRecommendations = automationRuntimeEnabled
    && hasAutomationActionEnabled(companionPreferences);
  const orderRecommendationUsage = tab === 'service' || serviceFocusMode ? 'display' : 'automation';
  const orderRecommendationPayloadValue = useMemo<OrderRecommendationWorkerPayload>(
    () => ({
      orders: night?.orders ?? [],
      runtime,
      runtimeRareCustomers,
      favorites,
      customRecipes,
      preferences: companionPreferences,
      activeRareGuests: night?.activeRareGuests ?? [],
      missionServeTargets: snapshot?.runtimeMissions?.serveTargets ?? [],
      specialBusiness: snapshot?.specialBusiness ?? null,
      specialBusinessRejectedRecipeKeys,
      data: recommendationData,
      usage: orderRecommendationUsage,
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
      specialBusinessRejectedRecipeKeys,
      snapshot?.specialBusiness,
      snapshot?.runtimeMissions?.serveTargets,
      orderRecommendationUsage,
    ],
  );
  const orderRecommendationPayloadSignature = useMemo(
    () => buildOrderRecommendationPayloadSignature(orderRecommendationPayloadValue),
    [orderRecommendationPayloadValue],
  );
  const orderRecommendationPayload = useSignedValue(
    orderRecommendationPayloadValue,
    orderRecommendationPayloadSignature,
  );
  const normalOrderDetailInputValue = useMemo<NormalOrderDetailInput>(
    () => ({
      include: includeNormalOrderDetails,
      normalOrders: includeNormalOrderDetails ? snapshot?.normalBusiness?.orders ?? [] : [],
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
  const normalOrderDetailInput = useSignedValue(
    normalOrderDetailInputValue,
    normalOrderDetailInputSignature,
  );
  const normalOrderDetailPayload = useMemo(
    () => buildNormalOrderWorkerPayload(
      normalOrderDetailInput,
      recommendationData,
      { includeDetails: true },
    ),
    [
      normalOrderDetailInput,
      recommendationData,
    ],
  );
  const normalAutomationTargetInputValue = useMemo<NormalOrderDetailInput>(
    () => ({
      include: normalAutomationTargetsEnabled,
      normalOrders: normalAutomationTargetsEnabled ? snapshot?.normalBusiness?.orders ?? [] : [],
      runtime,
      preferences: companionPreferences,
      specialBusiness: snapshot?.specialBusiness ?? null,
      rejectedRecipeKeys: specialBusinessRejectedRecipeKeys,
    }),
    [
      companionPreferences,
      normalAutomationTargetsEnabled,
      runtime,
      snapshot?.normalBusiness?.orders,
      snapshot?.specialBusiness,
      specialBusinessRejectedRecipeKeys,
    ],
  );
  const normalAutomationTargetInputSignature = useMemo(
    () => buildNormalOrderDetailInputSignature(normalAutomationTargetInputValue),
    [normalAutomationTargetInputValue],
  );
  const normalAutomationTargetInput = useSignedValue(
    normalAutomationTargetInputValue,
    normalAutomationTargetInputSignature,
  );
  const normalAutomationTargetPayload = useMemo(
    () => buildNormalOrderWorkerPayload(
      normalAutomationTargetInput,
      recommendationData,
      { includeExecutionTargets: true, usage: 'automation' },
    ),
    [
      normalAutomationTargetInput,
      recommendationData,
    ],
  );
  const orderRecommendationsEnabled = tab === 'service'
    || serviceFocusMode
    || rareAutomationNeedsRecommendations
    || companionPreferences.gameUiPinningEnabled
    || companionPreferences.cookerHighlightEnabled;
  const orderRecommendations = useOrderRecommendations(orderRecommendationPayload, {
    enabled: orderRecommendationsEnabled,
    inputSignature: orderRecommendationPayloadSignature,
  });
  const normalOrderDetails = useOrderRecommendations(normalOrderDetailPayload, {
    enabled: includeNormalOrderDetails,
  });
  const normalAutomationTargets = useOrderRecommendations(normalAutomationTargetPayload, {
    enabled: normalAutomationTargetsEnabled,
  });
  const normalAutomationTargetByKey = useMemo(
    () => new Map(normalAutomationTargets.normalExecutionTargets.map((selection) => [selection.orderKey, selection])),
    [normalAutomationTargets.normalExecutionTargets],
  );
  const orderRecommendationPerformanceMs = useMemo(
    () => ({
      ...(orderRecommendations.performanceMs ?? {}),
      ...(normalOrderDetails.performanceMs
        ? {
          normalDetails: normalOrderDetails.performanceMs.normalDetails ?? normalOrderDetails.performanceMs.total ?? 0,
        }
        : {}),
      ...(normalAutomationTargets.performanceMs
        ? {
          normalAutomationTargets: normalAutomationTargets.performanceMs.normalExecutionTargets
            ?? normalAutomationTargets.performanceMs.total
            ?? 0,
        }
        : {}),
    }),
    [
      normalAutomationTargets.performanceMs,
      normalOrderDetails.performanceMs,
      orderRecommendations.performanceMs,
    ],
  );
  const gameUiPinningTarget = useMemo(
    () => companionPreferences.gameUiPinningEnabled || companionPreferences.cookerHighlightEnabled
      ? buildGameUiPinningTarget(
        orderRecommendations.recommendations,
        companionPreferences.serviceOrderSortMode,
        recommendationIndexes,
        { requireExecutablePlan: Boolean(snapshot?.specialBusiness?.active) },
      )
      : null,
    [
      companionPreferences.cookerHighlightEnabled,
      companionPreferences.gameUiPinningEnabled,
      companionPreferences.serviceOrderSortMode,
      orderRecommendations.recommendations,
      recommendationIndexes,
      snapshot?.specialBusiness?.active,
    ],
  );
  useGameUiPinningPublisher({
    endpoint: normalizedEndpoint,
    apiToken,
    connectionRevision,
    sessionId: snapshot?.automationSessionId.trim() ?? '',
    connectionReady: connectionReadyForActions,
    pinningEnabled: companionPreferences.gameUiPinningEnabled,
    cookerHighlightEnabled: companionPreferences.cookerHighlightEnabled,
    target: gameUiPinningTarget,
    recommendationIsCurrent: orderRecommendations.isCurrent,
    recommendationPending: orderRecommendations.pending,
    recommendationError: Boolean(orderRecommendations.error),
    recommendationSuccessRevision: orderRecommendations.successRevision,
  });

  const refreshRareOrderDiagnostics = useCallback((now = Date.now()) => {
    const diagnostics = Array.from(rareOrderDiagnosticItemsRef.current.values()).map((selection) => {
      const orderKey = buildAutoOrderKey(selection.item);
      const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
      return buildRareAutoOrderDiagnostic(selection, state, now);
    });
    publishRareOrderDiagnostics(diagnostics);
    publishAutoPrepPaused(diagnostics.some((diagnostic) => diagnostic.paused));
  }, [publishAutoPrepPaused, publishRareOrderDiagnostics]);

  const refreshNormalOrderDiagnostics = useCallback((orders = snapshot?.normalBusiness?.orders ?? [], now = Date.now()) => {
    const diagnostics = buildNormalAutoOrderDiagnostics(orders, normalOrderStatesRef.current, now);
    publishNormalOrderDiagnostics(diagnostics);
    publishNormalOrderPausedCount(diagnostics.filter((diagnostic) => diagnostic.paused).length);
  }, [publishNormalOrderDiagnostics, publishNormalOrderPausedCount, snapshot?.normalBusiness?.orders]);

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
    if (!previousSessionId || previousSessionId === automationSessionId) return;

    automationRequestEpochRef.current += 1;
    automationRuntimeEnabledRef.current = false;
    rareOrderStatesRef.current.clear();
    rareOrderDiagnosticItemsRef.current.clear();
    normalOrderStatesRef.current.clear();
    automationBarrierAckRef.current = null;
    setAutomationBarrierAckBusyKey('');
    setAutomationBarrierAckErrors({});
    setAutomationLease(null);
    setAutomationLeaseBindingKey('');
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    refreshRareOrderDiagnostics();
    refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? []);
  }, [
    automationSessionId,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    snapshot?.normalBusiness?.orders,
  ]);

  const publishRareAutomationDecisionDiagnostic = useCallback((
    eventName: string,
    candidateResult: OrderPreparationCandidateResult,
    message: string,
    selectionPreferences: CompanionPreferences,
  ) => {
    if (!connectionReadyForActions || !apiToken) return;

    const specialBusiness = snapshot?.specialBusiness ?? null;
    if (!specialBusiness?.active && candidateResult.skips.length === 0 && candidateResult.selections.length > 0) return;

    const orderLines = orderRecommendations.recommendations
      .slice(0, 8)
      .map(buildAutomationDecisionOrderLine);
    const selectionLines = candidateResult.selections
      .slice(0, 8)
      .map(buildAutomationDecisionSelectionLine);
    const skipLines = candidateResult.skips
      .slice(0, 8)
      .map(buildAutomationDecisionSkipLine);
    const specialBusinessRole = orderRecommendations.recommendations
      .find((item) => item.order.specialBusinessRole)?.order.specialBusinessRole ?? '';
    const normalizedMessage = compactDiagnosticText(message || candidateResult.message);
    const signature = buildAutomationDecisionDiagnosticSignature(
      eventName,
      normalizedMessage,
      specialBusiness,
      snapshot?.snapshotSignature ?? '',
      orderLines,
      selectionLines,
      skipLines,
      selectionPreferences,
      automationLeaseOwned,
    );
    if (lastAutomationDecisionDiagnosticSignatureRef.current === signature) return;
    lastAutomationDecisionDiagnosticSignatureRef.current = signature;

    void appendAutomationDecisionDiagnostic(normalizedEndpoint, apiToken, {
      signature,
      eventName,
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
      if (lastAutomationDecisionDiagnosticSignatureRef.current === signature) {
        lastAutomationDecisionDiagnosticSignatureRef.current = '';
      }
    });
  }, [
    apiToken,
    automationLease,
    automationLeaseError,
    automationLeaseOwned,
    connectionReadyForActions,
    normalizedEndpoint,
    orderRecommendations.recommendations,
    snapshot?.activeSceneName,
    snapshot?.snapshotSignature,
    snapshot?.specialBusiness,
  ]);

  const publishNormalAutomationDecisionDiagnostic = useCallback((input: NormalAutomationDecisionDiagnosticInput) => {
    if (!connectionReadyForActions || !apiToken) return;

    const specialBusiness = snapshot?.specialBusiness ?? null;
    const orderLine = buildNormalAutomationDecisionOrderLine(input);
    const normalizedMessage = compactDiagnosticText(input.reason || input.targetSelection.message);
    const signature = buildAutomationDecisionDiagnosticSignature(
      input.eventName,
      normalizedMessage,
      specialBusiness,
      snapshot?.snapshotSignature ?? '',
      [orderLine],
      [],
      [],
      input.requestPreferences,
      automationLeaseOwned,
    );
    if (lastAutomationDecisionDiagnosticSignatureRef.current === signature) return;
    lastAutomationDecisionDiagnosticSignatureRef.current = signature;

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
      selectionLines: input.targetSelection.target ? [formatNormalAutomationTarget(input.targetSelection.target)] : [],
      skipLines: input.targetSelection.message ? [compactDiagnosticText(input.targetSelection.message)] : [],
    }).catch(() => {
      if (lastAutomationDecisionDiagnosticSignatureRef.current === signature) {
        lastAutomationDecisionDiagnosticSignatureRef.current = '';
      }
    });
  }, [
    apiToken,
    automationLease,
    automationLeaseError,
    automationLeaseOwned,
    connectionReadyForActions,
    normalizedEndpoint,
    snapshot?.activeSceneName,
    snapshot?.snapshotSignature,
    snapshot?.specialBusiness,
  ]);

  useEffect(() => {
    const events = snapshot?.automationEvents;
    if (!events || !automationSessionId || automationStateSessionIdRef.current !== automationSessionId) return;

    const unresolvedBarrierSequences = new Set(
      events.filter(isManualResolutionAutomationEvent).map((event) => event.sequence),
    );
    const now = Date.now();
    let rareChanged = false;
    let normalChanged = false;
    for (const [orderKey, state] of rareOrderStatesRef.current) {
      if (!shouldRetireMissingManualBarrier(
        state.manualResolutionRequired,
        state.lastRuntimeEventSequence,
        unresolvedBarrierSequences,
      )) continue;
      rareOrderStatesRef.current.set(orderKey, {
        ...emptyAutoFirstOrderState(orderKey, now),
        lastRuntimeEventSequence: state.lastRuntimeEventSequence,
        lastError: '安全栅栏已由其他自动化控制窗口确认，等待下一轮重新判断。',
      });
      rareChanged = true;
    }
    for (const [orderKey, state] of normalOrderStatesRef.current) {
      if (!shouldRetireMissingManualBarrier(
        state.manualResolutionRequired,
        state.lastRuntimeEventSequence,
        unresolvedBarrierSequences,
      )) continue;
      normalOrderStatesRef.current.set(orderKey, {
        ...emptyNormalAutoOrderState(orderKey, now),
        lastRuntimeEventSequence: state.lastRuntimeEventSequence,
        lastError: '安全栅栏已由其他自动化控制窗口确认，等待下一轮重新判断。',
      });
      normalChanged = true;
    }

    if (rareChanged) {
      lastAutoFirstOrderAtRef.current = 0;
      refreshRareOrderDiagnostics(now);
      publishAutoPrepMessage('自动化\n检测到安全栅栏已由其他控制窗口确认，本地稀客订单状态已重新同步。');
    }
    if (normalChanged) {
      lastAutoNormalOrderAtRef.current = 0;
      refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], now);
      publishNormalOrderMessage('普客自动化\n检测到安全栅栏已由其他控制窗口确认，本地订单状态已重新同步。');
    }
  }, [
    automationSessionId,
    publishAutoPrepMessage,
    publishNormalOrderMessage,
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
      const blocking = manualResolutionRequired
        || isBlockingCookingTerminalEvent(event)
        || isCookingTagsUnreadableStoredEvent(event);
      const recoverable = isRecoverableCookingTerminalEvent(event) || isCookingMismatchStoredEvent(event);
      if (!recoverable && !blocking) continue;
      if (!blocking && isWackyTargetTagMismatchEvent(event)) {
        const rejectedKey = buildWackyRejectedRecipeKeyFromEvent(event);
        if (rejectedKey) {
          rememberSpecialBusinessRejectedRecipeKey(rejectedKey);
        }
      }

      if (event.targetKind === 'rare') {
        if (!orderRecommendations.isCurrent) continue;
        for (const item of orderRecommendations.recommendations) {
          const orderKey = buildAutoOrderKey(item);
          const state = rareOrderStatesRef.current.get(orderKey)
            ?? emptyAutoFirstOrderState(orderKey, now);
          if (!matchesRareAutomationEvent(event, item, state)) continue;
          if (event.sequence <= state.lastRuntimeEventSequence) continue;
          if (state.manualResolutionRequired) {
            if (!canAdvanceAutomationRuntimeEventSequence(state.manualResolutionRequired, manualResolutionRequired)) continue;
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
          rareChanged = true;
          rarePaused ||= blocking;
          lastAutoFirstOrderAtRef.current = 0;
          break;
        }
        continue;
      }

      if (event.targetKind === 'normal') {
        let matchedKey = event.orderKey && normalOrderStatesRef.current.has(event.orderKey) ? event.orderKey : '';
        let matchedOrder = matchedKey
          ? normalOrders.find((order) => buildNormalAutoOrderKey(order) === matchedKey) ?? null
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

        const state = normalOrderStatesRef.current.get(matchedKey)
          ?? emptyNormalAutoOrderState(matchedKey, now);
        if (event.jobId && state.cookingJobId && event.jobId !== state.cookingJobId) continue;
        if (event.sequence <= state.lastRuntimeEventSequence) continue;
        if (state.manualResolutionRequired) {
          if (!canAdvanceAutomationRuntimeEventSequence(state.manualResolutionRequired, manualResolutionRequired)) continue;
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
      publishAutoPrepMessage(rarePaused
        ? '自动化\n料理任务已进入阻塞终态，当前订单自动化已暂停；请展开诊断，并按订单提示重试或确认已处理。'
        : '自动化\n料理任务被外部操作中断，下一轮将依据订单事实重新调度。');
    }
    if (normalChanged) {
      refreshNormalOrderDiagnostics(normalOrders, now);
      publishNormalOrderMessage(normalPaused
        ? '普客自动化\n料理任务已进入阻塞终态，当前订单自动化已暂停；请展开诊断，并按订单提示重试或确认已处理。'
        : '普客自动化\n料理任务被外部操作中断，下一轮将依据订单事实重新调度。');
    }
  }, [
    companionPreferences.autoMaxRollbacks,
    publishAutoPrepMessage,
    publishNormalOrderMessage,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    rememberSpecialBusinessRejectedRecipeKey,
    automationSessionId,
    orderRecommendations.isCurrent,
    orderRecommendations.recommendations,
    orderRecommendations.successRevision,
    snapshot?.automationEvents,
    snapshot?.normalBusiness?.orders,
  ]);

  useEffect(() => {
    specialBusinessRejectedRecipeKeysRef.current.clear();
    setSpecialBusinessRejectedRecipeKeys([]);
  }, [
    snapshot?.specialBusiness?.challengeType,
    snapshot?.specialBusiness?.phase,
    specialBusinessFoodTargetSignature,
  ]);

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
    if (state.manualResolutionRequired) {
      publishAutoPrepMessage('自动化\n该订单存在无法自动确认的游戏副作用，请检查游戏状态后点击“确认已处理”。');
      return;
    }
    rareOrderStatesRef.current.set(orderKey, {
      ...state,
      paused: false,
      pausedStage: '',
      pauseReasonCode: '',
      step: state.prepared || state.beverageHandled ? 'complete-order' : 'match-order',
      stepStartedAtMs: now,
      retryCount: 0,
      retryStage: '',
      nextAttemptAtMs: 0,
      lastError: '已手动重试，等待下一轮自动化继续。',
    });
    lastAutoFirstOrderAtRef.current = 0;
    publishAutoPrepMessage('自动化\n已重新启用该稀客订单，下一轮会继续处理。');
    refreshRareOrderDiagnostics(now);
  }, [publishAutoPrepMessage, refreshRareOrderDiagnostics]);

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
      const response = await acknowledgeAutomationSafetyBarrier(normalizedEndpoint, apiToken, sequence);
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
  }, [apiToken, normalizedEndpoint]);

  const clearAcknowledgedAutomationBarriers = useCallback((
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
  }, []);

  const resetRareAutomationOrder = useCallback((orderKey: string) => {
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
      const updatedAt = Date.now();
      const current = rareOrderStatesRef.current.get(orderKey);
      if (!current?.manualResolutionRequired) return;
      if (!response.ok) {
        const errorMessage = `确认失败：${response.error || '未知错误'}`;
        rareOrderStatesRef.current.set(orderKey, withAutomationDetail({
          ...current,
          lastError: errorMessage,
        }, updatedAt, errorMessage));
        publishAutoPrepMessage(`自动化\n${errorMessage}；安全栅栏仍保持。`);
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
        const errorMessage = `事件 #${sequence} 已确认，但检测到更新的安全栅栏 #${current.lastRuntimeEventSequence}，不会解除当前阻断。`;
        const latest = rareOrderStatesRef.current.get(orderKey) ?? current;
        rareOrderStatesRef.current.set(orderKey, withAutomationDetail({
          ...latest,
          lastError: errorMessage,
        }, updatedAt, errorMessage));
        publishAutoPrepMessage(`自动化\n${errorMessage}`);
        refreshRareOrderDiagnostics(updatedAt);
        return;
      }
      lastAutoFirstOrderAtRef.current = 0;
      publishAutoPrepMessage(`自动化\n${response.status || '安全栅栏已确认，下一轮会按游戏当前事实重新判断。'}`);
      refreshRareOrderDiagnostics(updatedAt);
      scheduleAutomationRefresh();
    })();
  }, [
    clearAcknowledgedAutomationBarriers,
    publishAutoPrepMessage,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    requestAutomationBarrierAck,
    scheduleAutomationRefresh,
    snapshot?.normalBusiness?.orders,
  ]);

  const retryNormalAutomationOrder = useCallback((orderKey: string) => {
    const now = Date.now();
    const state = normalOrderStatesRef.current.get(orderKey);
    if (!state) return;
    if (state.manualResolutionRequired) {
      publishNormalOrderMessage('普客自动化\n该订单存在无法自动确认的游戏副作用，请检查游戏状态后点击“确认已处理”。');
      return;
    }
    normalOrderStatesRef.current.set(orderKey, {
      ...state,
      paused: false,
      pausedStage: '',
      pauseReasonCode: '',
      step: state.prepared ? 'deliver-food' : 'match-order',
      stepStartedAtMs: now,
      retryCount: 0,
      retryStage: '',
      nextAttemptAtMs: 0,
      lastError: '已手动重试，等待下一轮自动化继续。',
    });
    lastAutoNormalOrderAtRef.current = 0;
    const orders = snapshot?.normalBusiness?.orders ?? [];
    refreshNormalOrderDiagnostics(orders, now);
    publishNormalOrderMessage('普客自动化\n已重新启用该普客订单，下一轮会继续处理。');
  }, [publishNormalOrderMessage, refreshNormalOrderDiagnostics, snapshot?.normalBusiness?.orders]);

  const resetNormalAutomationOrder = useCallback((orderKey: string) => {
    const now = Date.now();
    const state = normalOrderStatesRef.current.get(orderKey);
    if (!state?.manualResolutionRequired) {
      normalOrderStatesRef.current.delete(orderKey);
      lastAutoNormalOrderAtRef.current = 0;
      const orders = snapshot?.normalBusiness?.orders ?? [];
      refreshNormalOrderDiagnostics(orders, now);
      publishNormalOrderMessage('普客自动化\n已重置该普客订单状态，下一轮会按游戏当前订单事实重新判断。');
      return;
    }

    const sequence = state.lastRuntimeEventSequence;
    void (async () => {
      const response = await requestAutomationBarrierAck(`normal:${orderKey}`, sequence);
      const updatedAt = Date.now();
      const current = normalOrderStatesRef.current.get(orderKey);
      if (!current?.manualResolutionRequired) return;
      const orders = snapshot?.normalBusiness?.orders ?? [];
      if (!response.ok) {
        const errorMessage = `确认失败：${response.error || '未知错误'}`;
        normalOrderStatesRef.current.set(orderKey, withAutomationDetail({
          ...current,
          lastError: errorMessage,
        }, updatedAt, errorMessage));
        publishNormalOrderMessage(`普客自动化\n${errorMessage}；安全栅栏仍保持。`);
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
        const errorMessage = `事件 #${sequence} 已确认，但检测到更新的安全栅栏 #${current.lastRuntimeEventSequence}，不会解除当前阻断。`;
        const latest = normalOrderStatesRef.current.get(orderKey) ?? current;
        normalOrderStatesRef.current.set(orderKey, withAutomationDetail({
          ...latest,
          lastError: errorMessage,
        }, updatedAt, errorMessage));
        publishNormalOrderMessage(`普客自动化\n${errorMessage}`);
        refreshNormalOrderDiagnostics(orders, updatedAt);
        return;
      }

      lastAutoNormalOrderAtRef.current = 0;
      publishNormalOrderMessage(`普客自动化\n${response.status || '安全栅栏已确认，下一轮会按游戏当前订单事实重新判断。'}`);
      refreshNormalOrderDiagnostics(orders, updatedAt);
      scheduleAutomationRefresh();
    })();
  }, [
    clearAcknowledgedAutomationBarriers,
    publishNormalOrderMessage,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    requestAutomationBarrierAck,
    scheduleAutomationRefresh,
    snapshot?.normalBusiness?.orders,
  ]);

  const acknowledgeAutomationBarrierEvent = useCallback((sequence: number) => {
    void (async () => {
      const response = await requestAutomationBarrierAck(`barrier:${sequence}`, sequence);
      const updatedAt = Date.now();
      if (!response.ok) {
        const errorMessage = response.error || 'Mod 未确认安全栅栏 ACK。';
        setAutomationBarrierAckErrors((current) => ({ ...current, [sequence]: errorMessage }));
        publishAutoPrepMessage(`自动化\n确认事件 #${sequence} 失败：${errorMessage}`);
        return;
      }

      clearAcknowledgedAutomationBarriers(response.acknowledgedSequences, updatedAt, response.status);
      lastAutoFirstOrderAtRef.current = 0;
      lastAutoNormalOrderAtRef.current = 0;
      refreshRareOrderDiagnostics(updatedAt);
      refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], updatedAt);
      publishAutoPrepMessage(`自动化\n${response.status || `事件 #${sequence} 的安全栅栏已确认。`}`);
      scheduleAutomationRefresh();
    })();
  }, [
    clearAcknowledgedAutomationBarriers,
    publishAutoPrepMessage,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    requestAutomationBarrierAck,
    scheduleAutomationRefresh,
    snapshot?.normalBusiness?.orders,
  ]);

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
    if (!automationRuntimeEnabledRef.current || autoFirstOrderBusyRef.current) return;
    const requestEpoch = automationRequestEpochRef.current;
    const now = Date.now();
    if (now - lastAutoFirstOrderAtRef.current < AUTO_FIRST_ORDER_TICK_MS) return;
    if (!apiToken) {
      publishAutoPrepMessage('自动化已开启，但本地 API Token 不可用。');
      return;
    }

    if (!hasAutomationActionEnabled(companionPreferences)) {
      retainAutomationSafetyStates(rareOrderStatesRef.current);
      retainRareManualResolutionDiagnosticItems(rareOrderStatesRef.current, rareOrderDiagnosticItemsRef.current);
      refreshRareOrderDiagnostics(now);
      if (!companionPreferences.autoNormalOrderEnabled || !hasNormalOrderActionEnabled(companionPreferences)) {
        publishAutoPrepMessage('自动化已开启，请在经营中页面启用至少一个子选项。');
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
    const candidateResult = selectOrderPreparationCandidates(
      orderRecommendations.recommendations,
      favorites,
      selectionPreferences,
      companionPreferences.autoRareConcurrency,
      rareOrderStatesRef.current,
    );
    if (candidateResult.selections.length === 0) {
      publishRareAutomationDecisionDiagnostic('rare-candidate-empty', candidateResult, candidateResult.message, selectionPreferences);
      if ((snapshot?.automationCookingJobs ?? []).some((job) => job.targetKind === 'rare')) {
        publishAutoPrepMessage(`自动化\n${candidateResult.message}\nMod 中仍有活动料理任务，已保留订单状态并等待快照恢复。`);
        return;
      }
      retainAutomationSafetyStates(rareOrderStatesRef.current);
      retainRareManualResolutionDiagnosticItems(rareOrderStatesRef.current, rareOrderDiagnosticItemsRef.current);
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
    const activeKeys = new Set(candidateResult.selections.map((selection) => buildAutoOrderKey(selection.item)));
    for (const [orderKey, selection] of previousDiagnosticItems) {
      if (rareOrderStatesRef.current.get(orderKey)?.manualResolutionRequired || findRareAutomationCookingJob(
        snapshot?.automationCookingJobs ?? [],
        selection,
        rareOrderStatesRef.current.get(orderKey),
      )) {
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
      const cookerCapacity = buildAutomationCookerCapacity(runtime);
      const effectiveSpecialBusinessRejectedRecipeKeys = getSpecialBusinessRejectedRecipeKeys();
      const normalCookerDemand = buildNormalCookerDemand(
        snapshot?.normalBusiness?.orders ?? [],
        normalOrderStatesRef.current,
        companionPreferences,
        runtime,
        now,
        recommendationData,
        snapshot?.specialBusiness,
        effectiveSpecialBusinessRejectedRecipeKeys,
      );

      for (const selection of candidateResult.selections) {
        activeRequestSelection = selection;
        const orderKey = buildAutoOrderKey(selection.item);
        let currentState = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
        currentState = lockRareAutomationTargets(currentState, selection);
        const targetReconciliation = reconcileRareRecipeTargetForSpecialBusiness(
          snapshot?.specialBusiness,
          selection.item,
          currentState,
          selection.recipeTarget,
          now,
          effectiveSpecialBusinessRejectedRecipeKeys,
        );
        currentState = targetReconciliation.state;
        const targetReconciliationMessage = targetReconciliation.message;
        currentState = syncRareStateWithOrderServedState(currentState, selection.item.order, now);
        const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
          snapshot?.specialBusiness,
          selection.item.order.specialBusinessRole,
        );
        currentState = retireDisabledRareAutomationFailure(
          currentState,
          selection.item.order,
          companionPreferences,
          now,
          forceKoishiFullFeedAutomation,
        );
        const activeCookingJob = findRareAutomationCookingJob(
          snapshot?.automationCookingJobs ?? [],
          selection,
          currentState,
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
        if (currentState.paused) {
          rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
            currentState,
            now,
            targetReconciliationMessage,
            formatAutomationState(currentState, companionPreferences),
            currentState.manualResolutionRequired
              ? '游戏副作用无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
              : '稀客自动化已暂停该订单，订单事实变化或手动重试后会继续。',
          ));
          updatedOrderDetailCount += 1;
          continue;
        }
        if (currentState.nextAttemptAtMs > now) {
          rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
            currentState,
            now,
            targetReconciliationMessage,
            `当前阶段将在 ${Math.max(1, Math.ceil((currentState.nextAttemptAtMs - now) / 1000))} 秒后重试。`,
          ));
          updatedOrderDetailCount += 1;
          continue;
        }

        let preflightMessage = '';
        let preflightResponseStep: AutomationRequestStage | null = null;
        if (!forceKoishiFullFeedAutomation && companionPreferences.autoPrepCompleteOrder) {
          activeRequestEventSequence = currentState.lastRuntimeEventSequence;
          activeRequestStage = selectAutomationRequestStage({
            needsBeverage: companionPreferences.autoPrepTakeBeverage
              && !currentState.beverageHandled
              && !selection.item.order.hasServedBeverage,
            needsCooking: false,
            needsDelivery: false,
            needsCompletion: true,
          });
          const completeResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            currentState.recipeTarget,
            currentState.beverageTarget,
            companionPreferences,
          );
          const completeResponseAt = Date.now();
          if (!isAutomationRequestCurrent(requestEpoch)) return;
          if (handleAutomationControlPlaneResponse(completeResponse)) return;
          preflightResponseStep = resolveAutomationResponseStage(
            completeResponse.automation.stage,
            activeRequestStage,
          );
          const stateAfterCompleteRequest = rareOrderStatesRef.current.get(orderKey);
          if (!isAutomationRequestCurrent(
            requestEpoch,
            activeRequestEventSequence,
            stateAfterCompleteRequest?.lastRuntimeEventSequence ?? 0,
          )) {
            continue;
          }

          if (completeResponse.completedOrder) {
            rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
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
            ));
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
            rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
              currentState,
              now,
              targetReconciliationMessage,
              preflightMessage,
              formatAutomationState(currentState, companionPreferences),
              currentState.manualResolutionRequired
                ? '游戏副作用无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
                : '稀客自动化已暂停该订单，订单事实变化或手动重试后会继续。',
            ));
            updatedOrderDetailCount += 1;
            continue;
          }
          if (currentState.nextAttemptAtMs > completeResponseAt) {
            rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
              currentState,
              completeResponseAt,
              targetReconciliationMessage,
              preflightMessage,
              `当前阶段将在 ${Math.max(1, Math.ceil((currentState.nextAttemptAtMs - completeResponseAt) / 1000))} 秒后重试。`,
            ));
            updatedOrderDetailCount += 1;
            continue;
          }
        }

        let shouldPrepareFood = (companionPreferences.autoPrepStartCooking || forceKoishiFullFeedAutomation)
          && !currentState.prepared;
        let shouldPrepareBeverage = (companionPreferences.autoPrepTakeBeverage || forceKoishiFullFeedAutomation)
          && !currentState.beverageHandled;
        let cookingDeferralNote = '';
        let targetAvailabilityNote = '';
        const rejectedRecipeKey = buildRejectedRecipeKeyForRareTarget(
          snapshot?.specialBusiness,
          selection.item.order,
          currentState.recipeTarget,
        );
        if (shouldPrepareFood && rejectedRecipeKey && effectiveSpecialBusinessRejectedRecipeKeys.includes(rejectedRecipeKey)) {
          shouldPrepareFood = false;
          cookingDeferralNote = '当前目标 Tag 下该料理加料组合已被实机判定不匹配，等待推荐刷新或目标 Tag 更新后再制作。';
        }
        const specialBusinessCookingDeferral = getSpecialBusinessRareCookingDeferral(
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
          targetAvailabilityNote = targetReconciliationMessage
            || formatRareAutomationMissingRecipeTargetMessage(selection.item, companionPreferences.autoPrepRecipeFavoritesOnly);
        }
        if (shouldPrepareBeverage && !currentState.beverageTarget) {
          shouldPrepareBeverage = false;
          targetAvailabilityNote = formatRareAutomationMissingBeverageTargetMessage(selection.item, companionPreferences.autoPrepBeverageFavoritesOnly);
        }
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
          const waitingAt = Date.now();
          const waitingState = markAutomationWaiting(
            currentState,
            resolveAutomationWaitingStep({
              schedulerAvailable: schedulerNote.ok,
              authoritativeResponseStep: preflightResponseStep,
              completionEnabled: companionPreferences.autoPrepCompleteOrder,
            }),
            waitingAt,
            !schedulerNote.ok
              ? schedulerNote.message
              : cookingDeferralNote
              ? cookingDeferralNote
              : targetAvailabilityNote
              ? targetAvailabilityNote
              : companionPreferences.autoPrepCompleteOrder
              ? '等待料理出锅后直接送达，或等待下一轮完成订单。'
              : '已按当前设置完成可执行步骤；自动完成订单未开启。',
          );
          rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
            waitingState,
            waitingAt,
            targetReconciliationMessage,
            preflightMessage,
            formatAutomationState(waitingState, companionPreferences),
          ));
          updatedOrderDetailCount += 1;
          continue;
        }

        const preparePreferences = {
          ...companionPreferences,
          autoPrepTakeBeverage: shouldPrepareBeverage,
          autoPrepStartCooking: shouldPrepareFood,
          autoPrepCollectCooking: forceKoishiFullFeedAutomation
            || companionPreferences.autoPrepCollectCooking,
          autoPrepCompleteOrder: forceKoishiFullFeedAutomation
            ? false
            : companionPreferences.autoPrepCompleteOrder,
        };

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
          shouldPrepareFood ? currentState.recipeTarget : null,
          shouldPrepareBeverage ? currentState.beverageTarget : null,
          preparePreferences,
        );
        const prepareResponseAt = Date.now();
        if (!isAutomationRequestCurrent(requestEpoch)) return;
        if (handleAutomationControlPlaneResponse(prepareResponse)) return;
        const stateAfterPrepareRequest = rareOrderStatesRef.current.get(orderKey);
        if (!isAutomationRequestCurrent(
          requestEpoch,
          activeRequestEventSequence,
          stateAfterPrepareRequest?.lastRuntimeEventSequence ?? 0,
        )) {
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
        const cookingInterrupted = prepareResponse.automation.outcome === 'interrupted'
          || prepareResponse.automation.outcome === 'blocked'
          || prepareResponse.automation.outcome === 'fatal';
        const responseStage = resolveAutomationResponseStage(
          prepareResponse.automation.stage,
          activeRequestStage,
        );
        const manualCookingResolution = requiresManualAutomationResolution(
          prepareResponse.automation.reasonCode,
          prepareResponse.steps.map((step) => step.code ?? ''),
        ) && (responseStage === 'ensure-cooking' || responseStage === 'deliver-food');
        const nextPrepared = manualCookingResolution
          || (!cookingMismatchStored && !cookingInterrupted && (stateAfterPrepareDelivery.prepared
            || startedRareCooking
            || pendingRareCooking));
        const nextBeverageHandled = stateAfterPrepareDelivery.beverageHandled
          || didCompleteStepCode(prepareResponse, 'beverage-delivered')
          || Boolean(prepareResponse.servedBeverage);
        const transientFailure = !prepareResponse.ok && isTransientAutoPreparationFailure(prepareResponse);
        const beverageHandledAtMs = nextBeverageHandled && !currentState.beverageHandled
          ? prepareResponseAt
          : currentState.beverageHandledAtMs;
        const rollbackCount = currentState.rollbackCount;
        const nextState = enforceAutomationRollbackLimit(
          updateAutomationAfterResponse(
            {
              ...currentState,
              orderKey,
              prepared: nextPrepared,
              cookingJobId: nextPrepared
                ? prepareResponse.automation.jobId || currentState.cookingJobId
                : '',
              beverageHandled: nextBeverageHandled,
              beverageHandledAtMs,
              rollbackCount,
            },
            prepareResponse,
            prepareResponseAt,
            prepareResponse.automation.outcome === 'retryable-failure'
              || prepareResponse.automation.outcome === 'interrupted'
              || prepareResponse.automation.outcome === 'blocked'
              || prepareResponse.automation.outcome === 'fatal'
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
        if (!forceKoishiFullFeedAutomation
          && companionPreferences.autoPrepCompleteOrder
          && nextBeverageHandled
          && !currentState.beverageHandled
          && !finalState.paused
          && finalState.nextAttemptAtMs <= prepareResponseAt) {
          activeRequestEventSequence = finalState.lastRuntimeEventSequence;
          activeRequestStage = 'complete-order';
          const immediateCompleteResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            finalState.recipeTarget,
            finalState.beverageTarget,
            companionPreferences,
          );
          const immediateCompleteResponseAt = Date.now();
          finalStateUpdatedAt = immediateCompleteResponseAt;
          if (!isAutomationRequestCurrent(requestEpoch)) return;
          if (handleAutomationControlPlaneResponse(immediateCompleteResponse)) return;
          const stateAfterImmediateComplete = rareOrderStatesRef.current.get(orderKey);
          if (!isAutomationRequestCurrent(
            requestEpoch,
            activeRequestEventSequence,
            stateAfterImmediateComplete?.lastRuntimeEventSequence ?? 0,
          )) {
            continue;
          }
          if (immediateCompleteResponse.completedOrder) {
            rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
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
            ));
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
            ? '游戏副作用无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
            : '稀客自动化已暂停该订单，订单事实变化或手动重试后会继续。'
          : transientFailure
            ? '当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        const schedulerSuffix = schedulerNote.ok ? '' : schedulerNote.message;
        rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
          finalState,
          finalStateUpdatedAt,
          targetReconciliationMessage,
          preflightMessage,
          formatOrderPreparationResponse(prepareResponse),
          followUpMessage,
          formatAutomationState(finalState, companionPreferences),
          schedulerSuffix,
          suffix,
        ));
        updatedOrderDetailCount += 1;
      }

      if (candidateResult.messages.length > 0) {
        globalMessages.push(...candidateResult.messages.map((message) => `跳过\n${message}`));
      }

      refreshRareOrderDiagnostics(Date.now());
      publishAutoPrepMessage(updatedOrderDetailCount > 0 || globalMessages.length > 0
        ? `自动化\n${[
          updatedOrderDetailCount > 0 ? `已更新 ${updatedOrderDetailCount} 笔订单详情，可展开对应订单查看。` : '',
          ...globalMessages,
        ].filter(Boolean).join('\n\n')}`
        : '自动化\n当前没有需要执行的新步骤。');
      scheduleAutomationRefresh();
    } catch (err) {
      const failureAt = Date.now();
      const message = err instanceof Error ? err.message : String(err);
      if (!isAutomationRequestCurrent(requestEpoch)) return;
      let pausedCount = 0;
      const failedSelections = activeRequestSelection ? [activeRequestSelection] : [];
      for (const selection of failedSelections) {
        const orderKey = buildAutoOrderKey(selection.item);
        const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, failureAt);
        if (!isAutomationRequestCurrent(
          requestEpoch,
          activeRequestEventSequence,
          state.lastRuntimeEventSequence,
        )) {
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
        rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
          failedState,
          failureAt,
          message,
          failedState.paused ? '本阶段网络请求达到重试上限，当前订单已暂停。' : '本阶段网络请求失败，将按下一轮调度重试。',
        ));
      }
      refreshRareOrderDiagnostics(failureAt);
      publishAutoPrepMessage(`自动化\n${message}\n${pausedCount > 0 ? `达到重试上限并暂停 ${pausedCount} 笔订单。` : '请求失败，将自动重试。'}`);
    } finally {
      autoFirstOrderBusyRef.current = false;
      publishAutoPrepBusy(false);
    }
  }, [
    apiToken,
    companionPreferences,
    favorites,
    getSpecialBusinessRejectedRecipeKeys,
    normalizedEndpoint,
    orderRecommendations.error,
    orderRecommendations.isCurrent,
    orderRecommendations.pending,
    orderRecommendations.recommendations,
    publishAutoPrepBusy,
    publishAutoPrepMessage,
    publishRareAutomationDecisionDiagnostic,
    recommendationData,
    refreshRareOrderDiagnostics,
    scheduleAutomationRefresh,
    getAutomationCookerCycle,
    handleAutomationControlPlaneResponse,
    isAutomationRequestCurrent,
    runtime,
    snapshot?.specialBusiness,
    snapshot?.automationCookingJobs,
    snapshot?.normalBusiness?.orders,
  ]);

  const runAutoNormalOrder = useCallback(async () => {
    if (!automationRuntimeEnabledRef.current
      || !companionPreferences.autoNormalOrderEnabled
      || normalOrderBusyRef.current) return;
    const requestEpoch = automationRequestEpochRef.current;
    const now = Date.now();
    if (now - lastAutoNormalOrderAtRef.current < AUTO_NORMAL_ORDER_TICK_MS) return;
    if (!hasNormalOrderActionEnabled(companionPreferences)) {
      retainAutomationSafetyStates(normalOrderStatesRef.current);
      refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? [], now);
      publishNormalOrderMessage('普客自动化已开启，请至少启用一个处理阶段：送达酒水、自动制作料理、送达料理或完成订单。');
      return;
    }

    if (!apiToken) {
      publishNormalOrderMessage('普客自动化已开启，但本地 API Token 不可用。');
      return;
    }

    const orders = sortNormalOrders(snapshot?.normalBusiness?.orders ?? []).filter((item) => !item.hasEvaluated);
    const activeKeys = new Set(orders.map(buildNormalAutoOrderKey));
    for (const key of Array.from(normalOrderStatesRef.current.keys())) {
      const state = normalOrderStatesRef.current.get(key);
      if (!activeKeys.has(key) && !state?.manualResolutionRequired) {
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
      retainAutomationSafetyStates(normalOrderStatesRef.current);
      refreshNormalOrderDiagnostics([], now);
      publishNormalOrderMessage('普客自动化\n当前没有可处理的普客订单。');
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    if (normalAutomationTargetsEnabled && (normalAutomationTargets.pending || !normalAutomationTargets.isCurrent)) {
      publishNormalOrderMessage('普客自动化\n特殊经营执行目标计算中，等待下一轮。');
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    if (normalAutomationTargetsEnabled && normalAutomationTargets.error) {
      publishNormalOrderMessage(`普客自动化\n特殊经营执行目标计算失败：${normalAutomationTargets.error}`);
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
    const automationOrders = [...orders].sort((left, right) => {
      const leftState = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(left));
      const rightState = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(right));
      const leftCompletionReady = shouldAttemptNormalCompletion(left, leftState, companionPreferences, now) ? 1 : 0;
      const rightCompletionReady = shouldAttemptNormalCompletion(right, rightState, companionPreferences, now) ? 1 : 0;
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
      let state = syncNormalOrderStateWithSnapshot(order, storedState, now, companionPreferences) ?? storedState;
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
      const baseState = state ?? emptyNormalAutoOrderState(orderKey, now);
      const activeCookingJob = findNormalAutomationCookingJob(
        snapshot?.automationCookingJobs ?? [],
        order,
        baseState,
      );
      if (activeCookingJob && !order.hasServedFood) {
        state = reconcileStateWithActiveCookingJob(baseState, activeCookingJob, now);
        normalOrderStatesRef.current.set(orderKey, state);
      }
      if (state?.paused || (state?.nextAttemptAtMs ?? 0) > now) continue;
      const needsBeverage = shouldAttemptNormalBeverage(order, state, companionPreferences, now)
        || (forceKoishiFullFeedAutomation && !order.hasServedBeverage && state?.beverageHandled !== true);
      const needsCooking = shouldAttemptNormalCooking(order, state, companionPreferences, now)
        || (forceKoishiFullFeedAutomation && !order.hasServedFood && state?.prepared !== true && state?.foodDelivered !== true);
      const needsCompletion = forceKoishiFullFeedAutomation
        ? false
        : shouldAttemptNormalCompletion(order, state, companionPreferences, now);
      const specialTargetSelection = getNormalAutomationTargetSelection(
        order,
        normalAutomationTargetsEnabled,
        normalAutomationTargetByKey,
      );
      const cookingDecision = buildNormalCookingTargetDecision(order, recommendationData, specialTargetSelection);
      const specialBusinessCookingDeferral = cookingDecision.blockedReason;
      const targetBlockedCooking = needsCooking && Boolean(specialBusinessCookingDeferral);
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
          cookerCapacity,
        );
        if (!reservation.ok) {
          schedulerMessages.push(`${cookingDecision.label}\n${reservation.message}`);
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
        return state?.prepared && !order.hasServedFood;
      }).length;
      const schedulerText = schedulerMessages.length > 0 ? `\n${schedulerMessages.join('\n\n')}` : '';
      publishNormalOrderMessage(waitingCount > 0 || pausedCount > 0
        ? `普客自动化\n当前没有需要新开锅的普客订单。\n等待制作或送达 ${waitingCount} 笔，暂停 ${pausedCount} 笔。${blockedText}${schedulerText}`
        : `普客自动化\n当前没有需要执行的新步骤。${blockedText}${schedulerText}`);
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
        const storedState = normalOrderStatesRef.current.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, now);
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
        const currentState = activeCookingJob && !order.hasServedFood
          ? reconcileStateWithActiveCookingJob(syncedState, activeCookingJob, now)
          : syncedState;
        if (currentState.paused || currentState.nextAttemptAtMs > now) continue;
        const specialTargetSelection = getNormalAutomationTargetSelection(
          order,
          normalAutomationTargetsEnabled,
          normalAutomationTargetByKey,
        );
        const cookingDecision = buildNormalCookingTargetDecision(order, recommendationData, specialTargetSelection);
        const wantsCooking = shouldAttemptNormalCooking(order, currentState, companionPreferences, now)
          || (forceKoishiFullFeedAutomation && !order.hasServedFood && !currentState.prepared && !currentState.foodDelivered);
        const targetBlockedCooking = wantsCooking && Boolean(cookingDecision.blockedReason);
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
              needsBeverage: shouldAttemptNormalBeverage(order, currentState, companionPreferences, now)
                || (forceKoishiFullFeedAutomation && !order.hasServedBeverage && !currentState.beverageHandled),
              needsCooking: wantsCooking,
              needsCompletion: shouldAttemptNormalCompletion(order, currentState, companionPreferences, now),
              shouldHandleBeverage: false,
              shouldStartCooking: false,
              shouldCompleteOrder: false,
              forceKoishiFullFeedAutomation,
              targetBlockedCooking,
            },
          });
          continue;
        }
        const shouldHandleBeverage = shouldAttemptNormalBeverage(order, currentState, companionPreferences, now)
          || (forceKoishiFullFeedAutomation && !order.hasServedBeverage && !currentState.beverageHandled);
        const shouldStartCooking = wantsCooking;
        const shouldCompleteOrder = forceKoishiFullFeedAutomation
          ? false
          : shouldAttemptNormalCompletion(order, currentState, companionPreferences, now)
          || (companionPreferences.autoNormalCompleteOrder
            && !order.hasEvaluated
            && !currentState.paused
            && (order.readyToEvaluate || order.hasServedFood || currentState.foodDelivered)
            && (order.hasServedBeverage || currentState.beverageHandled || shouldHandleBeverage));

        const requestPreferences: CompanionPreferences = {
          ...companionPreferences,
          autoNormalTakeBeverage: (companionPreferences.autoNormalTakeBeverage || forceKoishiFullFeedAutomation) && shouldHandleBeverage,
          autoNormalStartCooking: (companionPreferences.autoNormalStartCooking || forceKoishiFullFeedAutomation) && shouldStartCooking,
          autoNormalDeliverFood: companionPreferences.autoNormalDeliverFood || forceKoishiFullFeedAutomation,
          autoNormalCompleteOrder: !forceKoishiFullFeedAutomation
            && companionPreferences.autoNormalCompleteOrder
            && shouldCompleteOrder,
        };

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
              forceKoishiFullFeedAutomation,
              targetBlockedCooking,
            },
          });
        }

        if (!requestPreferences.autoNormalTakeBeverage
          && !requestPreferences.autoNormalStartCooking
          && !requestPreferences.autoNormalDeliverFood
          && !requestPreferences.autoNormalCompleteOrder) {
          continue;
        }

        activeRequestEventSequence = currentState.lastRuntimeEventSequence;
        activeRequestStage = selectAutomationRequestStage({
          needsBeverage: shouldHandleBeverage,
          needsCooking: shouldStartCooking,
          needsDelivery: requestPreferences.autoNormalDeliverFood
            && currentState.prepared
            && !order.hasServedFood,
          needsCompletion: shouldCompleteOrder,
          fallback: currentState.step === 'ensure-beverage'
            || currentState.step === 'ensure-cooking'
            || currentState.step === 'deliver-food'
            || currentState.step === 'complete-order'
            ? currentState.step
            : 'match-order',
        });
        const response = await completeFirstNormalOrder(
          normalizedEndpoint,
          apiToken,
          order,
          requestPreferences,
          recommendationData,
          specialTargetSelection.target,
        );
        const responseAt = Date.now();
        if (!isAutomationRequestCurrent(requestEpoch)) return;
        if (handleAutomationControlPlaneResponse(response)) return;
        const stateAfterRequest = normalOrderStatesRef.current.get(orderKey);
        if (!isAutomationRequestCurrent(
          requestEpoch,
          activeRequestEventSequence,
          stateAfterRequest?.lastRuntimeEventSequence ?? 0,
        )) {
          continue;
        }
        const responseStage = resolveAutomationResponseStage(
          response.automation.stage,
          activeRequestStage,
        );
        const transientFailure = !response.ok && isTransientAutoPreparationFailure(response);
        const cookingMismatchStored = didCookingMismatchStored(response);
        const pendingCooking = didNormalOrderCookingStillPending(response);
        const startedCooking = didCompleteStepCode(response, 'cooking-started');
        const acknowledgedStart = !cookingMismatchStored && (startedCooking
          || pendingCooking
          || (responseStage === 'ensure-cooking' && response.automation.outcome === 'progressed'));
        const beverageHandledNow = didNormalOrderDeliverBeverage(response);
        const foodDeliveredNow = didNormalOrderDeliverFood(response);
        const completedNow = didNormalOrderComplete(response);
        const cookingInterrupted = response.automation.outcome === 'interrupted'
          || response.automation.outcome === 'blocked'
          || response.automation.outcome === 'fatal';
        const manualCookingResolution = requiresManualAutomationResolution(
          response.automation.reasonCode,
          response.steps.map((step) => step.code ?? ''),
        ) && (responseStage === 'ensure-cooking' || responseStage === 'deliver-food');
        const prepared = manualCookingResolution
          || (!(cookingMismatchStored || cookingInterrupted) && (currentState.prepared || acknowledgedStart));
        const beverageHandled = currentState.beverageHandled || order.hasServedBeverage || beverageHandledNow;
        const foodDelivered = cookingMismatchStored
          ? false
          : currentState.foodDelivered || order.hasServedFood || foodDeliveredNow;
        const completed = cookingMismatchStored
          ? false
          : currentState.completed || order.hasEvaluated || completedNow;
        const rollbackCount = currentState.rollbackCount;
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
              ...currentState,
              orderKey,
              prepared,
              cookingJobId: prepared
                ? response.automation.jobId || currentState.cookingJobId
                : '',
              beverageHandled,
              beverageHandledAtMs: beverageHandledNow && !currentState.beverageHandled
                ? responseAt
                : currentState.beverageHandledAtMs,
              foodDelivered,
              foodDeliveredAtMs: cookingMismatchStored
                ? 0
                : foodDeliveredNow && !currentState.foodDelivered ? responseAt : currentState.foodDeliveredAtMs,
              completed,
              completedAtMs: cookingMismatchStored
                ? 0
                : completedNow && !currentState.completed ? responseAt : currentState.completedAtMs,
              rollbackCount,
            },
            response,
            responseAt,
            response.automation.outcome === 'retryable-failure'
              || response.automation.outcome === 'interrupted'
              || response.automation.outcome === 'blocked'
              || response.automation.outcome === 'fatal'
              ? activeRequestStage
              : nextStep,
            companionPreferences.autoNormalStopOnError,
            companionPreferences.autoMaxStepRetries,
          ),
          companionPreferences.autoMaxRollbacks,
          responseAt,
        );
        const normalizedNextState = {
          ...nextState,
          beverageHandled,
          foodDelivered,
          completed,
        };

        const suffix = normalizedNextState.paused
          ? normalizedNextState.manualResolutionRequired
            ? '游戏副作用无法自动确认；请核对料理、托盘、保温箱和订单后点击“确认已处理”。'
            : '普客自动化已暂停该订单，订单事实变化或手动重试后会继续。'
          : transientFailure
            ? '当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        normalOrderStatesRef.current.set(orderKey, withAutomationDetail(
          normalizedNextState,
          responseAt,
          formatOrderPreparationResponse(response),
          formatAutomationState(normalizedNextState, companionPreferences),
          suffix,
        ));
        updatedOrderDetailCount += 1;
      }
      refreshNormalOrderDiagnostics(orders, Date.now());
      publishNormalOrderMessage(updatedOrderDetailCount > 0 || schedulerMessages.length > 0
        ? `普客自动化\n${[
          updatedOrderDetailCount > 0 ? `已更新 ${updatedOrderDetailCount} 笔订单详情，可展开对应订单查看。` : '',
          ...schedulerMessages,
        ].filter(Boolean).join('\n\n')}`
        : '普客自动化\n当前没有需要执行的新步骤。');
      scheduleAutomationRefresh();
    } catch (err) {
      const failureAt = Date.now();
      const message = err instanceof Error ? err.message : String(err);
      if (!isAutomationRequestCurrent(requestEpoch)) return;
      let pausedCount = 0;
      const failedOrders = activeRequestOrder ? [activeRequestOrder] : [];
      for (const order of failedOrders) {
        const orderKey = buildNormalAutoOrderKey(order);
        const state = normalOrderStatesRef.current.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, failureAt);
        if (!isAutomationRequestCurrent(
          requestEpoch,
          activeRequestEventSequence,
          state.lastRuntimeEventSequence,
        )) {
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
        normalOrderStatesRef.current.set(orderKey, withAutomationDetail(
          failedState,
          failureAt,
          message,
          failedState.paused ? '本阶段网络请求达到重试上限，当前订单已暂停。' : '本阶段网络请求失败，将按下一轮调度重试。',
        ));
      }
      refreshNormalOrderDiagnostics(orders, failureAt);
      publishNormalOrderPausedCount(orders.filter((order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order))?.paused).length);
      publishNormalOrderMessage(`普客自动化\n${message}\n${pausedCount > 0 ? `达到重试上限并暂停 ${pausedCount} 笔订单。` : '请求失败，将自动重试。'}`);
    } finally {
      normalOrderBusyRef.current = false;
      publishNormalOrderBusy(false);
    }
  }, [
    apiToken,
    companionPreferences,
    getAutomationCookerCycle,
    handleAutomationControlPlaneResponse,
    isAutomationRequestCurrent,
    normalizedEndpoint,
    normalAutomationTargetByKey,
    normalAutomationTargets.error,
    normalAutomationTargets.isCurrent,
    normalAutomationTargets.pending,
    normalAutomationTargetsEnabled,
    publishNormalAutomationDecisionDiagnostic,
    publishNormalOrderBusy,
    publishNormalOrderMessage,
    publishNormalOrderPausedCount,
    recommendationData,
    refreshNormalOrderDiagnostics,
    scheduleAutomationRefresh,
    runtime,
    snapshot?.specialBusiness,
    snapshot?.automationCookingJobs,
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
    retainAutomationSafetyStates(rareOrderStatesRef.current);
    retainRareManualResolutionDiagnosticItems(rareOrderStatesRef.current, rareOrderDiagnosticItemsRef.current);
    retainAutomationSafetyStates(normalOrderStatesRef.current);
    refreshRareOrderDiagnostics();
    refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? []);
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    publishAutoPrepBusy(false);
    publishNormalOrderBusy(false);
  }, [
    publishAutoPrepBusy,
    publishNormalOrderBusy,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    snapshot?.normalBusiness?.orders,
  ]);

  const handleNormalOrderSignatureChanged = useCallback(() => {
    lastAutoNormalOrderAtRef.current = 0;
  }, []);

  const handleNormalAutomationDisabled = useCallback(() => {
    retainAutomationSafetyStates(normalOrderStatesRef.current);
    refreshNormalOrderDiagnostics(snapshot?.normalBusiness?.orders ?? []);
    lastAutoNormalOrderAtRef.current = 0;
    publishNormalOrderBusy(false);
    publishNormalOrderMessage('');
  }, [
    publishNormalOrderBusy,
    publishNormalOrderMessage,
    refreshNormalOrderDiagnostics,
    snapshot?.normalBusiness?.orders,
  ]);

  useOrderAutomationIntervals({
    automationEnabled: automationRuntimeEnabled,
    resetStateWhenDisabled: !companionPreferences.automationEnabled,
    autoNormalOrderEnabled: companionPreferences.autoNormalOrderEnabled,
    resetNormalStateWhenDisabled: !companionPreferences.automationEnabled
      || !companionPreferences.autoNormalOrderEnabled,
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

        <TabsContent value="normal" data-gamepad-scope="content">
          {tab === 'normal' && (
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

        <TabsContent value="rare" data-gamepad-scope="content">
          {tab === 'rare' && (
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

        <TabsContent value="custom-recipes" data-gamepad-scope="content">
          {tab === 'custom-recipes' && (
            <ModCustomRecipesPanel
              apiToken={apiToken}
              customRecipes={customRecipes}
              customRecipeBusyKey={customRecipeBusyKey}
              customRecipeError={customRecipeError}
              form={customRecipeForm}
              groupMode={customRecipeGroupMode}
              runtimeSets={runtimeSets}
              runtimeRareCustomers={runtimeRareCustomers}
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

        <TabsContent value="service" data-gamepad-scope="content">
          {tab === 'service' && (
            <ModServicePanel
              runtime={runtime}
              night={night}
              specialBusiness={snapshot?.specialBusiness ?? null}
              detectedPlace={detectedPlace}
              recommendations={orderRecommendations.recommendations}
              recommendationIssues={orderRecommendations.recommendationIssues}
              data={recommendationData}
              performanceMs={snapshot?.performanceMs}
              orderRecommendationPerformanceMs={orderRecommendationPerformanceMs}
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
              automationSafetyBarriers={automationSafetyBarriers}
              automationBarrierAckBusyKey={automationBarrierAckBusyKey}
              normalExecutionTargets={normalAutomationTargets.normalExecutionTargets}
              normalExecutionTargetsEnabled={normalAutomationTargetsEnabled}
              normalExecutionTargetsPending={normalAutomationTargets.pending || !normalAutomationTargets.isCurrent}
              normalExecutionTargetsError={normalAutomationTargets.error}
              normalOrderDetailPlans={normalOrderDetails.normalOrderDetailPlans}
              normalOrderDetailsPending={includeNormalOrderDetails
                && (normalOrderDetails.pending || !normalOrderDetails.isCurrent)}
              normalOrderDetailsError={normalOrderDetails.error}
              onRecipeLimitChange={setServiceFocusRecipeLimit}
              onBeverageLimitChange={setServiceFocusBeverageLimit}
              onPreferenceChange={updateCompanionPreferences}
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

        <TabsContent value="tasks" data-gamepad-scope="content">
          {tab === 'tasks' && (
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
          )}
        </TabsContent>

        <TabsContent value="inventory" data-gamepad-scope="content">
          {tab === 'inventory' && (
            <ModInventoryPanel
              endpoint={normalizedEndpoint}
              apiToken={apiToken}
              runtimeSets={runtimeSets}
              runtimeLoaded={snapshot?.runtimeLoaded ?? false}
              data={recommendationData}
              onRefresh={refresh}
            />
          )}
        </TabsContent>

        <TabsContent value="help" data-gamepad-scope="content">
          {tab === 'help' && <ModHelpPanel />}
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
