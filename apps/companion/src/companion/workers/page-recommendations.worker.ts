import { buildRuntimeSets } from '@/companion/domain/cookers';
import {
  buildCustomFoodCandidates,
  mergeCustomFoodCandidates,
} from '@/companion/domain/custom-recipes';
import {
  buildRecommendationPlanSortContext,
  buildRecommendationRuntimeContext,
  deriveBeverageRowsFromCandidates,
  deriveRecipeRowsFromCandidates,
} from '@/companion/domain/service-recommendations';
import { MAX_RECOMMENDATION_ROWS } from '@/companion/pages/shared-constants';
import {
  buildNormalBeverageRecommendations,
  buildNormalFoodRecommendations,
  buildRareBeverageCandidates,
  buildRareFoodCandidates,
} from '@/recommendation-engine';
import type {
  NormalPageRecommendationPayload,
  PageRecommendationWorkerRequest,
  PageRecommendationWorkerResponse,
  RarePageRecommendationPayload,
} from '@/companion/workers/page-recommendations.types';

type WorkerScope = {
  postMessage: (message: PageRecommendationWorkerResponse) => void;
  onmessage: ((event: MessageEvent<PageRecommendationWorkerRequest>) => void) | null;
};

const workerScope = self as unknown as WorkerScope;

workerScope.onmessage = (event) => {
  const { requestId, payload } = event.data;

  try {
    workerScope.postMessage({
      requestId,
      ok: true,
      result: payload.kind === 'normal'
        ? buildNormalPageRecommendations(payload)
        : buildRarePageRecommendations(payload),
    });
  } catch (error) {
    workerScope.postMessage({
      requestId,
      ok: false,
      error: error instanceof Error ? error.message : String(error),
    });
  }
};

function buildNormalPageRecommendations(payload: NormalPageRecommendationPayload) {
  const runtimeSets = buildRuntimeSets(payload.runtime, payload.data);
  if (!runtimeSets) {
    return {
      kind: 'normal' as const,
      recipes: [],
      beverages: [],
    };
  }

  return {
    kind: 'normal' as const,
    recipes: buildNormalFoodRecommendations({
      data: payload.data,
      place: payload.selectedPlace,
      context: {
        availableRecipeIds: runtimeSets.recipeIds,
        availableBeverageIds: runtimeSets.beverageIds,
        disabledIngredientIds: runtimeSets.unavailableIngredientIds,
        popularFoodTag: payload.runtime.popularFoodTag,
        popularHateFoodTag: payload.runtime.popularHateFoodTag,
        famousShopEnabled: payload.runtime.famousShopEnabled,
        tagPriorityRules: payload.data.tagPriorityRules,
      },
    }).slice(0, MAX_RECOMMENDATION_ROWS),
    beverages: buildNormalBeverageRecommendations({
      data: payload.data,
      place: payload.selectedPlace,
      context: {
        availableRecipeIds: runtimeSets.recipeIds,
        availableBeverageIds: runtimeSets.beverageIds,
        disabledIngredientIds: runtimeSets.unavailableIngredientIds,
        popularFoodTag: payload.runtime.popularFoodTag,
        popularHateFoodTag: payload.runtime.popularHateFoodTag,
        famousShopEnabled: payload.runtime.famousShopEnabled,
        tagPriorityRules: payload.data.tagPriorityRules,
      },
    }).slice(0, MAX_RECOMMENDATION_ROWS),
  };
}

function buildRarePageRecommendations(payload: RarePageRecommendationPayload) {
  const runtimeSets = buildRuntimeSets(payload.runtime, payload.data);
  if (!runtimeSets || !payload.foodTag || !payload.beverageTag) {
    return {
      kind: 'rare' as const,
      recipes: [],
      beverages: [],
    };
  }

  const candidateContext = buildRecommendationRuntimeContext(
    payload.runtime,
    runtimeSets,
    payload.preferences,
    payload.data,
  );
  const sortContext = buildRecommendationPlanSortContext(
    payload.favorites,
    payload.selectedCustomer.id,
    payload.foodTag,
    payload.beverageTag,
    null,
    payload.preferences,
  );
  const demand = {
    type: 'rare-tag-order' as const,
    customer: payload.selectedCustomer,
    requiredFoodTag: payload.foodTag,
    requiredBeverageTag: payload.beverageTag,
  };
  const foodCandidates = buildRareFoodCandidates(payload.data, demand, candidateContext);
  const customFoodCandidates = buildCustomFoodCandidates({
    customRecipes: payload.customRecipes,
    data: payload.data,
    customer: payload.selectedCustomer,
    requiredFoodTag: payload.foodTag,
    requiredBeverageTag: payload.beverageTag,
    context: candidateContext,
  });
  const combinedFoodCandidates = mergeCustomFoodCandidates(foodCandidates, customFoodCandidates);
  const beverageCandidates = buildRareBeverageCandidates(payload.data, demand, candidateContext);

  return {
    kind: 'rare' as const,
    recipes: deriveRecipeRowsFromCandidates(combinedFoodCandidates, beverageCandidates, {
      variantLimitPerBase: payload.preferences.recipeVariantLimitPerBase,
      limit: MAX_RECOMMENDATION_ROWS,
      budget: candidateContext.budget,
      budgetPolicy: candidateContext.budgetPolicy,
      sortProfile: payload.preferences.recommendationSortProfile,
      sortContext,
    }),
    beverages: deriveBeverageRowsFromCandidates(beverageCandidates, combinedFoodCandidates, {
      limit: MAX_RECOMMENDATION_ROWS,
      budget: candidateContext.budget,
      budgetPolicy: candidateContext.budgetPolicy,
      sortProfile: payload.preferences.recommendationSortProfile,
      sortContext,
    }),
  };
}

export {};
