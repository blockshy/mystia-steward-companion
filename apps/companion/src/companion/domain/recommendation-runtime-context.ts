import { buildRecommendationCookerNameSet } from '@/companion/domain/cookers';
import type { CompanionPreferences } from '@/companion/preferences';
import type { RecommendationStateSnapshot, RuntimeSets } from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { RecommendationRuntimeContext, RecommendationBudgetContext } from '@/recommendation-engine';

export function buildRecommendationRuntimeContext(
  runtime: RecommendationStateSnapshot,
  runtimeSets: RuntimeSets,
  preferences: CompanionPreferences,
  data: RecommendationDataSet,
  options: { budget?: RecommendationBudgetContext | null } = {},
): RecommendationRuntimeContext {
  const hasRuntimeUnavailableCookers =
    runtimeSets.hasCookerSnapshot && runtimeSets.runtimeUnavailableCookerNames.size > 0;
  return {
    availableRecipeIds: runtimeSets.recipeIds,
    availableIngredientIds: runtimeSets.ingredientIds,
    availableBeverageIds: runtimeSets.beverageIds,
    disabledIngredientIds: new Set<number>(),
    excludedIngredientIds: new Set(preferences.recommendationExclusions.excludedIngredientIds),
    excludedBeverageIds: new Set(preferences.recommendationExclusions.excludedBeverageIds),
    ownedIngredientQty: runtimeSets.ownedIngredientQty,
    ownedBeverageQty: runtimeSets.ownedBeverageQty,
    placedCookerNames: buildRecommendationCookerNameSet(
      runtimeSets,
      preferences.filterMissingCookers,
    ),
    hasCookerSnapshot: runtimeSets.hasCookerSnapshot,
    popularFoodTag: runtime.popularFoodTag,
    popularHateFoodTag: runtime.popularHateFoodTag,
    famousShopEnabled: runtime.famousShopEnabled,
    tagPriorityRules: data.tagPriorityRules,
    maxExtraIngredients: 4,
    filterMissingCookers: preferences.filterMissingCookers || hasRuntimeUnavailableCookers,
    budget: options.budget ?? null,
    budgetPolicy: preferences.recommendationBudgetPolicy,
  };
}
