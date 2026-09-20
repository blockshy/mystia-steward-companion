import { buildNormalAutoOrderKey } from '@/companion/domain/automation';
import {
  buildPrimaryExecutionPlanPolicy,
  serializePrimaryExecutionPlanPolicy,
} from '@/companion/domain/primary-execution-plan';
import { sortNormalOrders } from '@/companion/domain/sorting';
import { buildSpecialBusinessRecommendationSignature } from '@/companion/domain/special-business';
import { type CompanionPreferences } from '@/companion/preferences';
import type {
  CustomRecipeData,
  FavoriteData,
  NightBusinessOrder,
  NormalBusinessOrder,
  RecommendationStateSnapshot,
  SpecialBusinessContext,
} from '@/companion/types';
import type { OrderRecommendationWorkerPayload } from '@/companion/workers/order-recommendations.types';
import { buildRecommendationDataSignature, type RecommendationDataSet } from '@/lib/recommendation-data';

const EMPTY_WORKER_FAVORITES: FavoriteData = { version: 0, recipes: [], beverages: [] };
const EMPTY_WORKER_CUSTOM_RECIPES: CustomRecipeData = { version: 0, enabled: false, recipes: [] };

export interface NormalOrderDetailInput {
  include: boolean;
  normalOrders: NormalBusinessOrder[];
  runtime: RecommendationStateSnapshot | null;
  preferences: CompanionPreferences;
  specialBusiness: SpecialBusinessContext | null;
  rejectedRecipeKeys: string[];
}

export function buildNormalOrderDetailInputSignature(input: NormalOrderDetailInput): string {
  if (!input.include) return 'disabled';
  return [
    buildNormalOrderDetailOrdersSignature(input.normalOrders),
    buildNormalOrderDetailRuntimeSignature(input.runtime),
    buildNormalOrderDetailSpecialBusinessSignature(input.specialBusiness),
    buildNormalOrderDetailPreferenceSignature(input.preferences),
    stableStringArraySignature(input.rejectedRecipeKeys),
  ].join('\n');
}

export function buildNormalOrderWorkerPayload(
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
    favorites: EMPTY_WORKER_FAVORITES,
    customRecipes: EMPTY_WORKER_CUSTOM_RECIPES,
    preferences: input.preferences,
    specialBusiness: input.specialBusiness,
    specialBusinessRejectedRecipeKeys: input.rejectedRecipeKeys,
    data,
    usage,
  };
}

export function buildNormalOrderDetailOrdersSignature(orders: readonly NormalBusinessOrder[]): string {
  return sortNormalOrders([...orders])
    .map((order) =>
      [
        buildNormalAutoOrderKey(order),
        order.traceId ?? '',
        order.orderLifecycleSequence ?? '',
        order.deskCode,
        order.guestId ?? '',
        order.runtimeGuestId ?? '',
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
      ].join('~'),
    )
    .join('|');
}

export function buildNormalOrderDetailRuntimeSignature(runtime: RecommendationStateSnapshot | null): string {
  if (!runtime) return 'runtime:null';
  return [
    stableNumberArraySignature(runtime.availableRecipeIds),
    stableNumberArraySignature(runtime.availableBeverageIds),
    stableNumberArraySignature(runtime.availableIngredientIds),
    stableNumberRecordSignature(runtime.ownedIngredientQty),
    stableNumberRecordSignature(runtime.ownedBeverageQty),
    stableNumberArraySignature(runtime.placedCookerTypeIds),
    buildPlacedCookerSignature(runtime.placedCookers),
    runtime.placedCookerSnapshotComplete ? 1 : 0,
    runtime.placedCookerControllerCount,
    runtime.placedCookerEmptyControllerCount,
    runtime.placedCookerLockedControllerCount,
    runtime.placedCookerReadFailureCount,
    runtime.popularFoodTag ?? '',
    runtime.popularHateFoodTag ?? '',
    runtime.famousShopEnabled ? 1 : 0,
  ].join('|');
}

export function buildNormalOrderDetailSpecialBusinessSignature(
  specialBusiness: SpecialBusinessContext | null,
): string {
  return buildSpecialBusinessRecommendationSignature(specialBusiness, true);
}

export function buildUiPinningSpecialBusinessSignature(
  specialBusiness: SpecialBusinessContext | null | undefined,
): string {
  return buildSpecialBusinessRecommendationSignature(specialBusiness);
}

export function buildNormalOrderDetailPreferenceSignature(preferences: CompanionPreferences): string {
  return [
    preferences.filterMissingCookers ? 1 : 0,
    preferences.recommendationBudgetPolicy,
    preferences.recipeVariantLimitPerBase,
    stableNumberArraySignature(preferences.recommendationExclusions.excludedIngredientIds),
    stableNumberArraySignature(preferences.recommendationExclusions.excludedBeverageIds),
    JSON.stringify(preferences.recommendationSortProfile),
  ].join('|');
}

export function buildOrderRecommendationPayloadSignature(payload: OrderRecommendationWorkerPayload): string {
  return [
    payload.usage ?? 'display',
    buildNightBusinessOrderSignature(payload.orders),
    buildNormalOrderDetailRuntimeSignature(payload.runtime),
    buildFavoriteDataSignature(payload.favorites),
    buildCustomRecipeDataSignature(payload.customRecipes),
    buildOrderRecommendationPreferenceSignature(payload.preferences),
    buildSpecialBusinessRecommendationSignature(payload.specialBusiness ?? null),
    stableStringArraySignature(payload.specialBusinessRejectedRecipeKeys),
    buildRecommendationDataSignature(payload.data),
  ].join('\n');
}

export function buildOrderRecommendationPresentationContextSignature(
  connectionRevision: number,
  automationSessionId: string,
  businessGeneration: number,
  lifecyclePhase: string,
  specialBusiness: SpecialBusinessContext | null | undefined,
  dataSignature: string,
): string {
  return [
    `connection:${connectionRevision}`,
    `session:${automationSessionId}`,
    `business:${businessGeneration}`,
    `lifecycle:${lifecyclePhase}`,
    buildSpecialBusinessRecommendationSignature(specialBusiness),
    `data:${dataSignature}`,
  ].join('\n');
}

export function buildNightBusinessOrderSignature(orders: readonly NightBusinessOrder[]): string {
  return [...orders]
    .sort(
      (left, right) =>
        left.deskCode - right.deskCode ||
        (left.guestId ?? -1) - (right.guestId ?? -1) ||
        (left.runtimeGuestId ?? Number.MIN_SAFE_INTEGER) -
          (right.runtimeGuestId ?? Number.MIN_SAFE_INTEGER) ||
        left.guestName.localeCompare(right.guestName) ||
        (left.foodTagId ?? Number.MIN_SAFE_INTEGER) - (right.foodTagId ?? Number.MIN_SAFE_INTEGER) ||
        (left.beverageTagId ?? Number.MIN_SAFE_INTEGER) - (right.beverageTagId ?? Number.MIN_SAFE_INTEGER),
    )
    .map((order) =>
      [
        order.traceId ?? '',
        order.orderLifecycleSequence ?? '',
        order.deskCode,
        order.guestId ?? '',
        order.runtimeGuestId ?? '',
        order.guestName,
        order.specialBusinessRole ?? '',
        order.automationAllowed === false ? 0 : 1,
        order.automationBlockReason ?? '',
        order.foodTagId,
        order.foodTag,
        order.beverageTagId,
        order.beverageTag,
        order.firstSeenAtUtc ?? '',
        order.isFreeOrder ? 1 : 0,
        order.fund ?? '',
        order.baseFundCarry ?? '',
        order.maxFundCarry ?? '',
        order.extraFundByBuff ?? '',
        order.willPayMoney ?? '',
        order.remainingOrderCount ?? '',
        order.hasServedFood ? 1 : 0,
        order.hasServedBeverage ? 1 : 0,
        order.missionRecipePriority
          ? [
              order.missionRecipePriority.traceId,
              order.missionRecipePriority.deskCode,
              order.missionRecipePriority.guestId,
              order.missionRecipePriority.runtimeGuestId,
              order.missionRecipePriority.foodId,
              order.missionRecipePriority.recipeId,
              order.missionRecipePriority.missionGeneration,
              order.missionRecipePriority.businessGeneration,
            ].join(':')
          : '',
      ].join('~'),
    )
    .join('|');
}

export function buildFavoriteDataSignature(favorites: FavoriteData): string {
  return [
    favorites.version,
    [...favorites.recipes]
      .sort(
        (left, right) =>
          left.customerId - right.customerId ||
          left.foodTag.localeCompare(right.foodTag) ||
          left.recipeId - right.recipeId,
      )
      .map((favorite) =>
        [
          favorite.customerId,
          favorite.foodTag,
          favorite.recipeId,
          stableNumberArraySignature(favorite.extraIngredientIds),
        ].join('~'),
      )
      .join('|'),
    [...favorites.beverages]
      .sort(
        (left, right) =>
          left.customerId - right.customerId ||
          left.beverageTag.localeCompare(right.beverageTag) ||
          left.beverageId - right.beverageId,
      )
      .map((favorite) => [favorite.customerId, favorite.beverageTag, favorite.beverageId].join('~'))
      .join('|'),
  ].join('\n');
}

export function buildCustomRecipeDataSignature(customRecipes: CustomRecipeData): string {
  return [
    customRecipes.version,
    customRecipes.enabled ? 1 : 0,
    [...customRecipes.recipes]
      .sort(
        (left, right) =>
          left.customerId - right.customerId ||
          (left.foodTag ?? '').localeCompare(right.foodTag ?? '') ||
          left.foodId - right.foodId ||
          left.recipeId - right.recipeId,
      )
      .map((recipe) =>
        [
          recipe.enabled ? 1 : 0,
          recipe.pinToTop ? 1 : 0,
          recipe.customerId,
          recipe.foodTag ?? '',
          recipe.foodId,
          recipe.recipeId,
          recipe.recipeName,
          stableNumberArraySignature(recipe.extraIngredientIds),
          recipe.sortOrder,
        ].join('~'),
      )
      .join('|'),
  ].join('\n');
}

export function buildOrderRecommendationPreferenceSignature(preferences: CompanionPreferences): string {
  return [
    preferences.serviceOrderSortMode,
    preferences.filterMissingCookers ? 1 : 0,
    preferences.missionRecipePriorityEnabled ? 1 : 0,
    preferences.pinFavoriteRecipeEnabled ? 1 : 0,
    preferences.pinFavoriteBeverageEnabled ? 1 : 0,
    serializePrimaryExecutionPlanPolicy(buildPrimaryExecutionPlanPolicy(preferences)),
    preferences.recommendationBudgetPolicy,
    preferences.recipeVariantLimitPerBase,
    stableNumberArraySignature(preferences.recommendationExclusions.excludedIngredientIds),
    stableNumberArraySignature(preferences.recommendationExclusions.excludedBeverageIds),
    JSON.stringify(preferences.recommendationSortProfile),
  ].join('|');
}

export function buildPlacedCookerSignature(cookers: RecommendationStateSnapshot['placedCookers']): string {
  return [
    ...new Set(
      (cookers ?? []).map((cooker) =>
        [
          cooker.controllerIndex,
          cooker.controllerIdentity,
          cooker.gridPosition.x,
          cooker.gridPosition.y,
          cooker.gridPosition.z,
          cooker.challengeLocked ? 1 : 0,
          cooker.couldOpen ? 1 : 0,
          cooker.name.trim(),
          stableNumberArraySignature(cooker.typeIds),
          stableStringArraySignature(cooker.typeNames),
        ].join(':'),
      ),
    ),
  ]
    .sort()
    .join(',');
}

export function stableNumberArraySignature(values: readonly number[] | undefined): string {
  return [...(values ?? [])]
    .filter((value) => Number.isFinite(value))
    .sort((left, right) => left - right)
    .join(',');
}

export function stableStringArraySignature(values: readonly string[] | undefined): string {
  return [...(values ?? [])]
    .map((value) => value.trim())
    .filter(Boolean)
    .sort()
    .join(',');
}

export function stableNumberRecordSignature(values: Record<string, number> | undefined): string {
  return Object.entries(values ?? {})
    .filter(([, value]) => Number.isFinite(value))
    .sort(([left], [right]) => Number(left) - Number(right))
    .map(([key, value]) => `${key}:${value}`)
    .join(',');
}
