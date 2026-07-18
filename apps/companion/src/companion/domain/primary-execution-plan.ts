import type { CompanionPreferences } from '@/companion/preferences';
import type {
  RareOrderRecommendationPlan,
  RecommendationPlanSortContext,
} from '@/recommendation-engine';

export interface PrimaryExecutionPlanPolicy {
  requireRecipeFavorite: boolean;
  requireBeverageFavorite: boolean;
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
 * Moves the first plan that satisfies the active automation favorite policy to the front.
 * The remaining plans retain their relative order so the recommendation profile remains stable.
 */
export function normalizePrimaryExecutionPlans(
  plans: readonly RareOrderRecommendationPlan[],
  sortContext: RecommendationPlanSortContext,
  policy: PrimaryExecutionPlanPolicy,
): RareOrderRecommendationPlan[] {
  const normalizedPlans = [...plans];
  if (!policy.requireRecipeFavorite && !policy.requireBeverageFavorite) return normalizedPlans;

  const primaryIndex = normalizedPlans.findIndex((plan) =>
    plan.bucket !== 'blocked'
    && (!policy.requireRecipeFavorite || isFavoriteRecipePlan(plan, sortContext))
    && (!policy.requireBeverageFavorite || isFavoriteBeveragePlan(plan, sortContext))
  );
  if (primaryIndex <= 0) return normalizedPlans;

  const [primary] = normalizedPlans.splice(primaryIndex, 1);
  normalizedPlans.unshift(primary);
  return normalizedPlans;
}

export function getPrimaryExecutionPlan(
  plans: readonly RareOrderRecommendationPlan[],
): RareOrderRecommendationPlan | null {
  return plans[0] ?? null;
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
