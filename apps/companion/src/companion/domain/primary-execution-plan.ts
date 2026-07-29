import type { CompanionPreferences } from '@/companion/preferences';
import type {
  RareOrderRecommendationPlan,
  RecommendationPlanSortContext,
} from '@/recommendation-engine';
// eslint-disable-next-line no-restricted-imports -- Node's type-strip audit cannot resolve the Vite alias.
import {
  getVerifiedMissionRecipeSortContext,
  isMissionRecipeExecutionPlan,
  type MissionRecipeOrderSnapshot,
} from '../../recommendation-engine/mission-recipe-priority.ts';

export interface PrimaryExecutionPlanPolicy {
  requireRecipeFavorite: boolean;
  requireBeverageFavorite: boolean;
}

interface PrimaryExecutionPlanRecommendation {
  order: MissionRecipeOrderSnapshot;
  executionPlans: readonly RareOrderRecommendationPlan[];
}

export interface PrimaryExecutionPlanRecommendationOptions {
  prioritizeMissionRecipe: boolean;
  requireExecutablePlan: boolean;
}

type PrimaryExecutionPlanPreferences = Pick<
  CompanionPreferences,
  | 'automationEnabled'
  | 'autoPrepStartCooking'
  | 'autoPrepTakeBeverage'
  | 'autoPrepRecipeFavoritesOnly'
  | 'autoPrepBeverageFavoritesOnly'
>;

export function buildPrimaryExecutionPlanPolicy(
  preferences: PrimaryExecutionPlanPreferences,
  automationAllowed = true,
): PrimaryExecutionPlanPolicy {
  const automationEnabled = automationAllowed && preferences.automationEnabled;
  return {
    requireRecipeFavorite: automationEnabled
      && preferences.autoPrepStartCooking
      && preferences.autoPrepRecipeFavoritesOnly,
    requireBeverageFavorite: automationEnabled
      && preferences.autoPrepTakeBeverage
      && preferences.autoPrepBeverageFavoritesOnly,
  };
}

export function serializePrimaryExecutionPlanPolicy(policy: PrimaryExecutionPlanPolicy): string {
  return `${policy.requireRecipeFavorite ? 'recipe' : ''}:${policy.requireBeverageFavorite ? 'beverage' : ''}`;
}

/**
 * Promotes the verified mission recipe when it also satisfies the active automation
 * favorite policy. If it cannot, the existing favorite-only policy remains authoritative.
 * Remaining plans retain their relative order.
 */
export function normalizePrimaryExecutionPlans(
  plans: readonly RareOrderRecommendationPlan[],
  sortContext: RecommendationPlanSortContext,
  policy: PrimaryExecutionPlanPolicy,
): RareOrderRecommendationPlan[] {
  const normalizedPlans = [...plans];
  const missionIndex = normalizedPlans.findIndex((plan) =>
    isMissionRecipeExecutionPlan(plan, sortContext)
    && satisfiesPrimaryPolicy(plan, sortContext, policy)
  );
  if (missionIndex >= 0) {
    return movePlanToFront(normalizedPlans, missionIndex);
  }
  if (!policy.requireRecipeFavorite && !policy.requireBeverageFavorite) {
    return normalizedPlans;
  }

  const primaryIndex = normalizedPlans.findIndex((plan) =>
    satisfiesPrimaryPolicy(plan, sortContext, policy)
  );
  return movePlanToFront(normalizedPlans, primaryIndex);
}

function movePlanToFront(
  plans: RareOrderRecommendationPlan[],
  index: number,
): RareOrderRecommendationPlan[] {
  if (index <= 0) return plans;
  const [primary] = plans.splice(index, 1);
  plans.unshift(primary);
  return plans;
}

function satisfiesPrimaryPolicy(
  plan: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
  policy: PrimaryExecutionPlanPolicy,
): boolean {
  return plan.bucket !== 'blocked'
    && (!policy.requireRecipeFavorite || isFavoriteRecipePlan(plan, sortContext))
    && (!policy.requireBeverageFavorite || isFavoriteBeveragePlan(plan, sortContext));
}

export function getPrimaryExecutionPlan(
  plans: readonly RareOrderRecommendationPlan[],
): RareOrderRecommendationPlan | null {
  return plans[0] ?? null;
}

export function isVerifiedMissionPrimaryExecutionPlan(
  recommendation: PrimaryExecutionPlanRecommendation,
): boolean {
  const sortContext = getVerifiedMissionRecipeSortContext(recommendation.order);
  const primaryPlan = getPrimaryExecutionPlan(recommendation.executionPlans);
  return sortContext != null
    && primaryPlan != null
    && isMissionRecipeExecutionPlan(primaryPlan, sortContext);
}

export function selectPrimaryExecutionPlanRecommendation<
  TRecommendation extends PrimaryExecutionPlanRecommendation,
>(
  recommendations: readonly TRecommendation[],
  options: PrimaryExecutionPlanRecommendationOptions,
): TRecommendation | null {
  if (options.prioritizeMissionRecipe) {
    const missionRecommendation = recommendations.find(isVerifiedMissionPrimaryExecutionPlan);
    if (missionRecommendation) return missionRecommendation;
  }
  if (options.requireExecutablePlan) {
    return recommendations.find((recommendation) =>
      getPrimaryExecutionPlan(recommendation.executionPlans) != null
    ) ?? null;
  }
  return recommendations[0] ?? null;
}

function isFavoriteRecipePlan(
  plan: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
): boolean {
  if (!plan.food) return false;
  return sortContext.favoriteRecipeKeys?.has(buildPlanRecipeKey(plan)) === true;
}

function isFavoriteBeveragePlan(
  plan: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
): boolean {
  if (!plan.beverage) return false;
  return sortContext.favoriteBeverageIds?.has(plan.beverage.beverage.id) === true;
}

function buildPlanRecipeKey(plan: RareOrderRecommendationPlan): string {
  const food = plan.food;
  if (!food) return '';
  const extraIngredientIds = [...new Set(food.extraIngredients
    .map((ingredient) => ingredient.id)
    .filter((id) => Number.isFinite(id) && id >= 0)
    .map((id) => Math.trunc(id)))]
    .sort((left, right) => left - right);
  return `${food.recipe.id}:${extraIngredientIds.join(',')}`;
}
