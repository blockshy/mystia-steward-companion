import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useGamepadNavigation } from '@/companion/use-gamepad-navigation';
import {
  createEmptyCustomRecipeForm,
  type CustomRecipeFormState,
} from '@/companion/custom-recipe-editor';
import { WorkbenchHeader } from '@/companion/features/workbench/WorkbenchHeader';
import { useCompanionConnection } from '@/companion/hooks/useCompanionConnection';
import { useCustomRecipes } from '@/companion/hooks/useCustomRecipes';
import { useFavorites } from '@/companion/hooks/useFavorites';
import { useGameUiPinningPublisher } from '@/companion/hooks/useGameUiPinningPublisher';
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
import {
  ModServicePanel,
  ServiceFocusPage,
  type ServicePanelView,
  type ServiceRecommendationTab,
} from '@/companion/pages/ModServicePanel';
import { ModSettingsPanel } from '@/companion/pages/ModSettingsPanel';
import { ModTasksPanel } from '@/companion/pages/ModTasksPanel';
import {
  acquireAutomationLease,
  appendAutomationDecisionDiagnostic,
  completeFirstNormalOrder,
  completeFirstRareOrder,
  dismissRuntimeRareOrder,
  prepareNextRareOrder,
  releaseAutomationLease,
} from '@/companion/api';
import {
  didAcknowledgeStep,
  didCookingMismatchStored,
  didCompleteStep,
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
  pauseAutomationState,
  updateAutomationAfterResponse,
  type AutoFirstOrderState,
  type AutomationStep,
  type NormalAutoOrderState,
  type RareAutomationRecipeTarget,
} from '@/companion/automation-state';
import {
  applyRareServedStateFromResponse,
  buildAutoOrderKey,
  buildCompleteOrderPreferences,
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
  isNormalOrderCollected,
  isNormalOrderPreparedStale,
  isRareOrderPreparedStale,
  isRecoverableNormalPausedState,
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
  persistCustomRecipeGroupMode,
  persistFocusBeverageLimit,
  persistFocusCompact,
  persistFocusRecipeLimit,
  persistTab,
  readStoredFocusBeverageLimit,
  readStoredFocusCompact,
  readStoredFocusRecipeLimit,
  readStoredCustomRecipeGroupMode,
  readStoredTab,
} from '@/companion/storage';
import type {
  AutomationRuntimeEvent,
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
    `request=${input.requestPreferences.autoNormalStartCooking ? 1 : 0}/${input.requestPreferences.autoNormalTakeBeverage ? 1 : 0}/${input.requestPreferences.autoNormalCollectCooking ? 1 : 0}/${input.requestPreferences.autoNormalDeliverFood ? 1 : 0}/${input.requestPreferences.autoNormalCompleteOrder ? 1 : 0}`,
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
    step: 'paused',
    stepStartedAtMs: now,
    lastError: state.lastError ? `${state.lastError}；${limitMessage}` : limitMessage,
  };
}

function matchesRareAutomationEvent(
  event: AutomationRuntimeEvent,
  selection: ValidOrderPreparationSelection,
  state: AutoFirstOrderState,
): boolean {
  if (event.targetKind !== 'rare') return false;
  const order = selection.item.order;
  if (event.traceId && order.traceId) return event.traceId === order.traceId;
  const recipeTarget = state.recipeTarget ?? selection.recipeTarget;
  if (event.foodId >= 0 && recipeTarget?.foodId !== event.foodId) return false;
  if (event.deskCode >= 0 && order.deskCode !== event.deskCode) return false;
  if (event.guestId != null && order.guestId != null && event.guestId !== order.guestId) return false;
  return true;
}

function resetRareOrderStateAfterRuntimeMismatch(
  state: AutoFirstOrderState,
  now: number,
  event: AutomationRuntimeEvent,
): AutoFirstOrderState {
  return {
    ...state,
    prepared: false,
    preparedAtMs: 0,
    paused: false,
    step: 'ensure-cooking',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    rollbackCount: state.rollbackCount + 1,
    lastError: event.message || '非目标成品已放入保温箱，重新制作目标料理。',
  };
}

function matchesNormalAutomationEvent(event: AutomationRuntimeEvent, order: NormalBusinessOrder): boolean {
  if (event.targetKind !== 'normal') return false;
  if (event.traceId && order.traceId) return event.traceId === order.traceId;
  const orderKey = buildNormalAutoOrderKey(order);
  if (event.orderKey && event.orderKey === orderKey) return true;
  if (event.foodId >= 0 && order.foodId !== event.foodId) return false;
  if (event.deskCode >= 0 && order.deskCode !== event.deskCode) return false;
  if (event.guestName && order.guestName && event.guestName !== order.guestName) return false;
  return true;
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
    preparedAtMs: 0,
    collected: false,
    foodDelivered: false,
    foodDeliveredAtMs: 0,
    completed: false,
    completedAtMs: 0,
    paused: false,
    step: 'ensure-cooking',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    rollbackCount: state.rollbackCount + 1,
    lastError: event.message || '非目标成品已放入保温箱，重新制作目标料理。',
  };
}

function pauseRareOrderStateAfterRuntimeFailure(
  state: AutoFirstOrderState,
  now: number,
  event: AutomationRuntimeEvent,
): AutoFirstOrderState {
  return {
    ...state,
    prepared: false,
    preparedAtMs: 0,
    paused: true,
    step: 'paused',
    stepStartedAtMs: now,
    retryCount: 0,
    lastError: event.message || '无法读取成品 Tag，已暂停自动化。',
  };
}

function pauseNormalOrderStateAfterRuntimeFailure(
  state: NormalAutoOrderState,
  orderKey: string,
  now: number,
  event: AutomationRuntimeEvent,
): NormalAutoOrderState {
  return {
    ...state,
    orderKey,
    prepared: false,
    preparedAtMs: 0,
    collected: false,
    foodDelivered: false,
    foodDeliveredAtMs: 0,
    completed: false,
    completedAtMs: 0,
    paused: true,
    step: 'paused',
    stepStartedAtMs: now,
    retryCount: 0,
    lastError: event.message || '无法读取成品 Tag，已暂停自动化。',
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
  const [automationLeaseError, setAutomationLeaseError] = useState('');
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
  const lastAutomationRuntimeEventSequenceRef = useRef(0);
  const automationCookerCycleRef = useRef<AutomationCookerCycle | null>(null);
  const lastAutomationDecisionDiagnosticSignatureRef = useRef('');
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
    setCompanionPreferences((current) => normalizeCompanionPreferences({ ...current, ...next }));
  }, []);

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
  const automationLeaseOwned = Boolean(automationLease?.ok && automationLease.owned);
  const automationRuntimeEnabled = companionPreferences.automationEnabled
    && connectionReadyForActions
    && automationLeaseOwned;
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

  useEffect(() => {
    if (!companionPreferences.automationEnabled || !connectionReadyForActions) {
      setAutomationLease(null);
      setAutomationLeaseError('');
      return undefined;
    }

    let cancelled = false;
    const renewLease = async () => {
      try {
        const nextLease = await acquireAutomationLease(normalizedEndpoint, apiToken);
        if (cancelled) return;
        setAutomationLease(nextLease);
        setAutomationLeaseError(nextLease.owned ? '' : nextLease.error || '自动化控制权当前不可用。');
      } catch (err) {
        if (cancelled) return;
        setAutomationLease(null);
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
    companionPreferences.automationEnabled,
    connectionReadyForActions,
    normalizedEndpoint,
  ]);

  useEffect(() => {
    if (companionPreferences.automationEnabled) return;

    const lease = automationLease;
    setAutomationLease(null);
    setAutomationLeaseError('');
    if (!lease?.owned || !apiToken || connectionPaused) return;

    releaseAutomationLease(normalizedEndpoint, apiToken).catch(() => {
      // 关闭自动化时释放租约是优化路径；失败后后端 TTL 会自动过期。
    });
  }, [
    apiToken,
    automationLease,
    companionPreferences.automationEnabled,
    connectionPaused,
    normalizedEndpoint,
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
    () => buildNormalOrderAutomationSignature(snapshot?.normalBusiness?.orders ?? []),
    [snapshot?.normalBusiness?.orders],
  );
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
      autoCollectCooking: input.requestPreferences.autoNormalCollectCooking,
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
    const events = snapshot?.automationEvents ?? [];
    if (events.length === 0) return;

    const maxSequence = Math.max(...events.map((event) => event.sequence));
    if (!automationRuntimeEnabled) {
      lastAutomationRuntimeEventSequenceRef.current = Math.max(lastAutomationRuntimeEventSequenceRef.current, maxSequence);
      return;
    }

    const nextEvents = events
      .filter((event) => event.sequence > lastAutomationRuntimeEventSequenceRef.current)
      .sort((left, right) => left.sequence - right.sequence);
    if (nextEvents.length === 0) return;

    const now = Date.now();
    let rareChanged = false;
    let normalChanged = false;
    let rarePaused = false;
    let normalPaused = false;
    const normalOrders = snapshot?.normalBusiness?.orders ?? [];

    for (const event of nextEvents) {
      const tagsUnreadable = isCookingTagsUnreadableStoredEvent(event);
      if (!isCookingMismatchStoredEvent(event) && !tagsUnreadable) continue;
      if (!tagsUnreadable && isWackyTargetTagMismatchEvent(event)) {
        const rejectedKey = buildWackyRejectedRecipeKeyFromEvent(event);
        if (rejectedKey) {
          rememberSpecialBusinessRejectedRecipeKey(rejectedKey);
        }
      }

      if (event.targetKind === 'rare') {
        for (const [orderKey, selection] of rareOrderDiagnosticItemsRef.current.entries()) {
          const state = rareOrderStatesRef.current.get(orderKey);
          if (!state || !matchesRareAutomationEvent(event, selection, state)) continue;

          const nextState = tagsUnreadable
            ? pauseRareOrderStateAfterRuntimeFailure(state, now, event)
            : enforceAutomationRollbackLimit(
              resetRareOrderStateAfterRuntimeMismatch(state, now, event),
              companionPreferences.autoMaxRollbacks,
              now,
            );
          rareOrderStatesRef.current.set(orderKey, nextState);
          rareChanged = true;
          rarePaused ||= tagsUnreadable;
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
        const nextState = tagsUnreadable
          ? pauseNormalOrderStateAfterRuntimeFailure(state, matchedKey, now, event)
          : enforceAutomationRollbackLimit(
            resetNormalOrderStateAfterRuntimeMismatch(state, matchedKey, now, event),
            companionPreferences.autoMaxRollbacks,
            now,
          );
        normalOrderStatesRef.current.set(matchedKey, nextState);
        normalChanged = true;
        normalPaused ||= tagsUnreadable;
        lastAutoNormalOrderAtRef.current = 0;
        if (matchedOrder && matchedOrder.hasEvaluated) {
          normalOrderStatesRef.current.delete(matchedKey);
        }
      }
    }

    lastAutomationRuntimeEventSequenceRef.current = Math.max(
      lastAutomationRuntimeEventSequenceRef.current,
      maxSequence,
    );

    if (rareChanged) {
      refreshRareOrderDiagnostics(now);
      publishAutoPrepMessage(rarePaused
        ? '自动化\n无法读取成品 Tag，成品已放入保温箱并暂停当前订单自动化。'
        : '自动化\n非目标成品已放入保温箱，下一轮将重新制作目标料理。');
    }
    if (normalChanged) {
      refreshNormalOrderDiagnostics(normalOrders, now);
      publishNormalOrderMessage(normalPaused
        ? '普客自动化\n无法读取成品 Tag，成品已放入保温箱并暂停当前订单自动化。'
        : '普客自动化\n非目标成品已放入保温箱，下一轮将重新制作目标料理。');
    }
  }, [
    automationRuntimeEnabled,
    companionPreferences.autoMaxRollbacks,
    publishAutoPrepMessage,
    publishNormalOrderMessage,
    refreshNormalOrderDiagnostics,
    refreshRareOrderDiagnostics,
    rememberSpecialBusinessRejectedRecipeKey,
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
    rareOrderStatesRef.current.set(orderKey, {
      ...state,
      paused: false,
      step: state.prepared || state.beverageHandled ? 'complete-order' : 'match-order',
      stepStartedAtMs: now,
      retryCount: 0,
      lastError: '已手动重试，等待下一轮自动化继续。',
    });
    lastAutoFirstOrderAtRef.current = 0;
    publishAutoPrepMessage('自动化\n已重新启用该稀客订单，下一轮会继续处理。');
    refreshRareOrderDiagnostics(now);
  }, [publishAutoPrepMessage, refreshRareOrderDiagnostics]);

  const resetRareAutomationOrder = useCallback((orderKey: string) => {
    const now = Date.now();
    rareOrderStatesRef.current.delete(orderKey);
    lastAutoFirstOrderAtRef.current = 0;
    publishAutoPrepMessage('自动化\n已重置该稀客订单状态，下一轮会重新判断料理、酒水和完成状态。');
    refreshRareOrderDiagnostics(now);
  }, [publishAutoPrepMessage, refreshRareOrderDiagnostics]);

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
    if (!companionPreferences.automationEnabled || autoFirstOrderBusyRef.current) return;
    const now = Date.now();
    if (now - lastAutoFirstOrderAtRef.current < AUTO_FIRST_ORDER_TICK_MS) return;
    if (!apiToken) {
      publishAutoPrepMessage('自动化已开启，但本地 API Token 不可用。');
      return;
    }

    if (!hasAutomationActionEnabled(companionPreferences)) {
      rareOrderStatesRef.current.clear();
      rareOrderDiagnosticItemsRef.current.clear();
      publishRareOrderDiagnostics([]);
      publishAutoPrepPaused(false);
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
      publishRareAutomationDecisionDiagnostic('rare-candidate-empty', candidateResult, candidateResult.message, selectionPreferences);
      rareOrderStatesRef.current.clear();
      rareOrderDiagnosticItemsRef.current.clear();
      publishRareOrderDiagnostics([]);
      publishAutoPrepPaused(false);
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
    publishAutoPrepBusy(true);
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
        if (isRareOrderPreparedStale(currentState, now, companionPreferences)) {
          currentState = {
            ...currentState,
            prepared: false,
            preparedAtMs: 0,
            step: 'ensure-cooking',
            stepStartedAtMs: now,
            lastProgressAtMs: now,
            retryCount: 0,
            rollbackCount: currentState.rollbackCount + 1,
            lastError: '目标料理长时间未直接送达，已自动恢复并重新确认料理制作状态。',
          };
        }
        currentState = enforceAutomationRollbackLimit(
          currentState,
          companionPreferences.autoMaxRollbacks,
          now,
        );
        if (currentState.paused) {
          rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
            currentState,
            now,
            targetReconciliationMessage,
            formatAutomationState(currentState, companionPreferences),
            '稀客自动化已暂停该订单，订单变化或重新开启后会继续。',
          ));
          updatedOrderDetailCount += 1;
          continue;
        }

        const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
          snapshot?.specialBusiness,
          selection.item.order.specialBusinessRole,
        );
        let preflightMessage = '';
        if (!forceKoishiFullFeedAutomation && companionPreferences.autoPrepCompleteOrder) {
          const completeResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            currentState.recipeTarget,
            currentState.beverageTarget,
            buildCompleteOrderPreferences(companionPreferences),
          );

          if (completeResponse.completedOrder) {
            rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
              {
                ...currentState,
                step: 'done',
                stepStartedAtMs: now,
                lastProgressAtMs: now,
                retryCount: 0,
                lastError: '',
                paused: false,
              },
              now,
              targetReconciliationMessage,
              formatOrderPreparationResponse(completeResponse),
            ));
            updatedOrderDetailCount += 1;
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
          if (currentState.paused) {
            rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
              currentState,
              now,
              targetReconciliationMessage,
              preflightMessage,
              formatAutomationState(currentState, companionPreferences),
              '稀客自动化已暂停该订单，订单变化或重新开启后会继续。',
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
          const waitingState = markAutomationWaiting(
            currentState,
            schedulerNote.ok
              ? companionPreferences.autoPrepCompleteOrder ? 'complete-order' : 'idle'
              : 'ensure-cooking',
            now,
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
            now,
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
          autoPrepCollectCooking: true,
          autoPrepCompleteOrder: forceKoishiFullFeedAutomation
            ? false
            : companionPreferences.autoPrepCompleteOrder,
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
        const startedRareCooking = didCompleteStepCode(prepareResponse, 'cooking-started')
          || didCompleteStep(prepareResponse, '自动开始料理');
        const cookingMismatchStored = didCookingMismatchStored(prepareResponse);
        const nextPrepared = !cookingMismatchStored && (stateAfterPrepareDelivery.prepared
          || startedRareCooking
          || pendingRareCooking);
        const nextBeverageHandled = stateAfterPrepareDelivery.beverageHandled
          || didCompleteStepCode(prepareResponse, 'beverage-delivered')
          || didCompleteStep(prepareResponse, '自动送达酒水');
        const transientFailure = !prepareResponse.ok && isTransientAutoPreparationFailure(prepareResponse);
        const preparedAtMs = cookingMismatchStored
          ? 0
          : startedRareCooking || pendingRareCooking || (nextPrepared && !currentState.prepared) ? now : currentState.preparedAtMs;
        const beverageHandledAtMs = nextBeverageHandled && !currentState.beverageHandled ? now : currentState.beverageHandledAtMs;
        const rollbackCount = cookingMismatchStored
          ? currentState.rollbackCount
          : startedRareCooking || pendingRareCooking ? 0 : currentState.rollbackCount;
        const nextState = enforceAutomationRollbackLimit(
          updateAutomationAfterResponse(
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
          ),
          companionPreferences.autoMaxRollbacks,
          now,
        );
        let finalState = nextState;
        let followUpMessage = '';
        if (!forceKoishiFullFeedAutomation
          && companionPreferences.autoPrepCompleteOrder
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
            rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
              {
                ...finalState,
                step: 'done',
                stepStartedAtMs: now,
                lastProgressAtMs: now,
                retryCount: 0,
                lastError: '',
                paused: false,
              },
              now,
              targetReconciliationMessage,
              preflightMessage,
              formatOrderPreparationResponse(prepareResponse),
              formatOrderPreparationResponse(immediateCompleteResponse),
            ));
            updatedOrderDetailCount += 1;
            continue;
          }

          finalState = applyRareServedStateFromResponse(finalState, selection.item.order, immediateCompleteResponse, now);
          followUpMessage = formatOrderPreparationResponse(immediateCompleteResponse);
        }

        const suffix = finalState.paused
          ? '稀客自动化已暂停该订单，订单变化或重新开启后会继续。'
          : transientFailure
            ? '当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        const schedulerSuffix = schedulerNote.ok ? '' : schedulerNote.message;
        rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
          finalState,
          now,
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

      refreshRareOrderDiagnostics(now);
      publishAutoPrepMessage(updatedOrderDetailCount > 0 || globalMessages.length > 0
        ? `自动化\n${[
          updatedOrderDetailCount > 0 ? `已更新 ${updatedOrderDetailCount} 笔订单详情，可展开对应订单查看。` : '',
          ...globalMessages,
        ].filter(Boolean).join('\n\n')}`
        : '自动化\n当前没有需要执行的新步骤。');
      scheduleAutomationRefresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      if (companionPreferences.autoPrepStopOnError) {
        for (const selection of candidateResult.selections) {
          const orderKey = buildAutoOrderKey(selection.item);
          const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
          rareOrderStatesRef.current.set(orderKey, withAutomationDetail(
            pauseAutomationState(state, now, message),
            now,
            message,
            '稀客自动化已暂停，订单变化或重新开启后会继续。',
          ));
        }
        refreshRareOrderDiagnostics(now);
        publishAutoPrepMessage(`自动化\n${message}\n稀客自动化已暂停，订单变化或重新开启后会继续。`);
      } else {
        publishAutoPrepPaused(false);
        refreshRareOrderDiagnostics(now);
        publishAutoPrepMessage(`自动化\n${message}`);
      }
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
    publishAutoPrepPaused,
    publishRareAutomationDecisionDiagnostic,
    publishRareOrderDiagnostics,
    recommendationData,
    refreshRareOrderDiagnostics,
    scheduleAutomationRefresh,
    getAutomationCookerCycle,
    runtime,
    snapshot?.specialBusiness,
    snapshot?.normalBusiness?.orders,
  ]);

  const runAutoNormalOrder = useCallback(async () => {
    if (!companionPreferences.automationEnabled || !companionPreferences.autoNormalOrderEnabled || normalOrderBusyRef.current) return;
    const now = Date.now();
    if (now - lastAutoNormalOrderAtRef.current < AUTO_NORMAL_ORDER_TICK_MS) return;
    if (!hasNormalOrderActionEnabled(companionPreferences)) {
      normalOrderStatesRef.current.clear();
      publishNormalOrderDiagnostics([]);
      publishNormalOrderPausedCount(0);
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
      if (syncedState) {
        normalOrderStatesRef.current.set(
          orderKey,
          enforceAutomationRollbackLimit(syncedState, companionPreferences.autoMaxRollbacks, now),
        );
      }
    }
    refreshNormalOrderDiagnostics(orders, now);

    if (orders.length === 0) {
      normalOrderStatesRef.current.clear();
      publishNormalOrderDiagnostics([]);
      publishNormalOrderPausedCount(0);
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

      const state = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order));
      if (state?.paused) continue;
      const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
        snapshot?.specialBusiness,
        order.specialBusinessRole,
      );
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
          autoNormalCollectCooking: false,
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
        return state?.prepared && !isNormalOrderCollected(order, state);
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
    try {
      let updatedOrderDetailCount = 0;
      for (const order of runnableOrders) {
        const orderKey = buildNormalAutoOrderKey(order);
        const storedState = normalOrderStatesRef.current.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, now);
        const syncedState = syncNormalOrderStateWithSnapshot(order, storedState, now, companionPreferences) ?? storedState;
        const recoveredState = isRecoverableNormalPausedState(syncedState, now)
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
        const currentState = recoveredState;
        const shouldRetryPrepared = isNormalOrderPreparedStale(currentState, now, companionPreferences);
        const forceKoishiFullFeedAutomation = isWackyKoishiBossFullFeedContext(
          snapshot?.specialBusiness,
          order.specialBusinessRole,
        );
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
            autoNormalCollectCooking: false,
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
            && !(currentState.paused && !isRecoverableNormalPausedState(currentState, now))
            && (order.readyToEvaluate || order.hasServedFood || currentState.foodDelivered)
            && (order.hasServedBeverage || currentState.beverageHandled || shouldHandleBeverage));

        const requestPreferences: CompanionPreferences = {
          ...companionPreferences,
          autoNormalTakeBeverage: (companionPreferences.autoNormalTakeBeverage || forceKoishiFullFeedAutomation) && shouldHandleBeverage,
          autoNormalStartCooking: (companionPreferences.autoNormalStartCooking || forceKoishiFullFeedAutomation) && shouldStartCooking,
          autoNormalCollectCooking: (companionPreferences.autoNormalDeliverFood || forceKoishiFullFeedAutomation) && shouldStartCooking,
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
          specialTargetSelection.target,
        );
        const transientFailure = !response.ok && isTransientAutoPreparationFailure(response);
        const cookingMismatchStored = didCookingMismatchStored(response);
        const pendingCooking = didNormalOrderCookingStillPending(response);
        const startedCooking = didCompleteStepCode(response, 'cooking-started')
          || didCompleteStep(response, '普客开始料理');
        const acknowledgedStart = !cookingMismatchStored && (startedCooking
          || pendingCooking
          || didAcknowledgeStep(response, '普客料理'));
        const beverageHandledNow = didNormalOrderDeliverBeverage(response);
        const foodDeliveredNow = didNormalOrderDeliverFood(response);
        const completedNow = didNormalOrderComplete(response);
        const collected = false;
        const prepared = cookingMismatchStored
          ? false
          : currentState.prepared || acknowledgedStart;
        const beverageHandled = currentState.beverageHandled || order.hasServedBeverage || beverageHandledNow;
        const foodDelivered = cookingMismatchStored
          ? false
          : currentState.foodDelivered || order.hasServedFood || foodDeliveredNow;
        const completed = cookingMismatchStored
          ? false
          : currentState.completed || order.hasEvaluated || completedNow;
        const rollbackCount = cookingMismatchStored
          ? currentState.rollbackCount
          : collected || pendingCooking || startedCooking || beverageHandledNow || foodDeliveredNow || completedNow
          ? 0
          : currentState.rollbackCount;
        let nextStep: AutomationStep = 'ensure-cooking';
        if (completed) {
          nextStep = 'done';
        } else if (!cookingMismatchStored && (foodDelivered || order.readyToEvaluate)) {
          nextStep = 'complete-order';
        } else if (!cookingMismatchStored && requestPreferences.autoNormalTakeBeverage && !beverageHandled) {
          nextStep = 'ensure-beverage';
        } else if (!cookingMismatchStored && prepared && !foodDelivered) {
          nextStep = 'deliver-food';
        }
        const nextState = enforceAutomationRollbackLimit(
          updateAutomationAfterResponse(
            {
              ...currentState,
              orderKey,
              prepared,
              preparedAtMs: cookingMismatchStored
                ? 0
                : acknowledgedStart || (shouldRetryPrepared && transientFailure)
                ? now
                : prepared
                  ? currentState.preparedAtMs
                  : 0,
              beverageHandled,
              beverageHandledAtMs: beverageHandledNow && !currentState.beverageHandled ? now : currentState.beverageHandledAtMs,
              collected,
              foodDelivered,
              foodDeliveredAtMs: cookingMismatchStored
                ? 0
                : foodDeliveredNow && !currentState.foodDelivered ? now : currentState.foodDeliveredAtMs,
              completed,
              completedAtMs: cookingMismatchStored
                ? 0
                : completedNow && !currentState.completed ? now : currentState.completedAtMs,
              step: nextStep,
              rollbackCount,
            },
            response,
            now,
            nextStep,
            companionPreferences.autoNormalStopOnError,
            companionPreferences.autoMaxStepRetries,
          ),
          companionPreferences.autoMaxRollbacks,
          now,
        );
        const normalizedNextState = {
          ...nextState,
          beverageHandled,
          collected,
          foodDelivered,
          completed,
        };

        const suffix = normalizedNextState.paused
          ? '普客自动化已暂停该订单，订单变化或重新开启后会继续。'
          : transientFailure
            ? '当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        normalOrderStatesRef.current.set(orderKey, withAutomationDetail(
          normalizedNextState,
          now,
          formatOrderPreparationResponse(response),
          formatAutomationState(normalizedNextState, companionPreferences),
          suffix,
        ));
        updatedOrderDetailCount += 1;
      }
      refreshNormalOrderDiagnostics(orders, now);
      publishNormalOrderMessage(updatedOrderDetailCount > 0 || schedulerMessages.length > 0
        ? `普客自动化\n${[
          updatedOrderDetailCount > 0 ? `已更新 ${updatedOrderDetailCount} 笔订单详情，可展开对应订单查看。` : '',
          ...schedulerMessages,
        ].filter(Boolean).join('\n\n')}`
        : '普客自动化\n当前没有需要执行的新步骤。');
      scheduleAutomationRefresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      if (companionPreferences.autoNormalStopOnError) {
        refreshNormalOrderDiagnostics(orders, now);
        publishNormalOrderMessage(`普客自动化\n${message}\n普客自动化已暂停，订单变化或重新开启后会继续。`);
      } else {
        publishNormalOrderMessage(`普客自动化\n${message}`);
      }
    } finally {
      normalOrderBusyRef.current = false;
      publishNormalOrderBusy(false);
    }
  }, [
    apiToken,
    companionPreferences,
    getAutomationCookerCycle,
    normalizedEndpoint,
    normalAutomationTargetByKey,
    normalAutomationTargets.error,
    normalAutomationTargets.isCurrent,
    normalAutomationTargets.pending,
    normalAutomationTargetsEnabled,
    publishNormalAutomationDecisionDiagnostic,
    publishNormalOrderBusy,
    publishNormalOrderDiagnostics,
    publishNormalOrderMessage,
    publishNormalOrderPausedCount,
    recommendationData,
    refreshNormalOrderDiagnostics,
    scheduleAutomationRefresh,
    runtime,
    snapshot?.specialBusiness,
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
    publishRareOrderDiagnostics([]);
    normalOrderStatesRef.current.clear();
    publishNormalOrderDiagnostics([]);
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    publishAutoPrepBusy(false);
    publishAutoPrepPaused(false);
    publishNormalOrderBusy(false);
    publishNormalOrderPausedCount(0);
  }, [
    publishAutoPrepBusy,
    publishAutoPrepPaused,
    publishNormalOrderBusy,
    publishNormalOrderDiagnostics,
    publishNormalOrderPausedCount,
    publishRareOrderDiagnostics,
  ]);

  const handleNormalOrderSignatureChanged = useCallback(() => {
    lastAutoNormalOrderAtRef.current = 0;
  }, []);

  const handleNormalAutomationDisabled = useCallback(() => {
    normalOrderStatesRef.current.clear();
    publishNormalOrderDiagnostics([]);
    lastAutoNormalOrderAtRef.current = 0;
    publishNormalOrderBusy(false);
    publishNormalOrderPausedCount(0);
    publishNormalOrderMessage('');
  }, [
    publishNormalOrderBusy,
    publishNormalOrderDiagnostics,
    publishNormalOrderMessage,
    publishNormalOrderPausedCount,
  ]);

  useOrderAutomationIntervals({
    automationEnabled: automationRuntimeEnabled,
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

      <Tabs value={tab} onValueChange={(value) => setTab(value as ModTab)} className="space-y-3">
        <TabsList
          scrollable
          className="h-9 !w-full max-w-full justify-stretch"
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
              onPreferenceChange={updateCompanionPreferences}
              onConnectionConfigApplied={applyConnectionDetails}
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
    await invoke('toggle_companion_focus', {
      keepVisibleWhenFocused: focusSwitchBehavior === 'keep-visible',
      windowSwitchCooldownMs: normalizeFocusSwitchCooldownMs(focusSwitchCooldownMs),
    });
  } catch {
    // 浏览器开发模式和旧版伴随窗口不一定暴露该 command。
  }
}
