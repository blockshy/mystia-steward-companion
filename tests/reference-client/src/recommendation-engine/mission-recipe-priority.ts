import type { RecommendationPlanSortContext } from '@/recommendation-engine/sort-profile';
import type {
  ConditionResult,
  FoodCandidate,
  RareOrderRecommendationPlan,
} from '@/recommendation-engine/types';

interface MissionRecipePrioritySnapshot {
  traceId: string;
  deskCode: number;
  guestId: number;
  runtimeGuestId: number;
  foodId: number;
  recipeId: number;
  missionGeneration: number;
  businessGeneration: number;
}

export interface MissionRecipeOrderSnapshot {
  traceId?: string | null;
  deskCode: number;
  guestId: number | null;
  runtimeGuestId: number | null;
  missionRecipePriority?: MissionRecipePrioritySnapshot | null;
}

export function getVerifiedMissionRecipeSortContext(
  order: MissionRecipeOrderSnapshot,
): Pick<RecommendationPlanSortContext, 'missionRecipeFoodId' | 'missionRecipeId'> | null {
  const priority = order.missionRecipePriority;
  if (!priority
    || !Number.isInteger(priority.foodId)
    || priority.foodId < 0
    || !Number.isInteger(priority.recipeId)
    || priority.recipeId < 0
    || !Number.isSafeInteger(priority.missionGeneration)
    || priority.missionGeneration < 1
    || !Number.isSafeInteger(priority.businessGeneration)
    || priority.businessGeneration < 1
    || !order.traceId
    || priority.traceId !== order.traceId
    || priority.deskCode !== order.deskCode
    || priority.guestId !== order.guestId
    || priority.runtimeGuestId !== order.runtimeGuestId) {
    return null;
  }

  return {
    missionRecipeFoodId: priority.foodId,
    missionRecipeId: priority.recipeId,
  };
}

export function isMissionRecipeFoodCandidate(
  food: FoodCandidate,
  sortContext: RecommendationPlanSortContext,
): boolean {
  return sortContext.missionRecipeFoodId != null
    && sortContext.missionRecipeId != null
    && food.recipe.id === sortContext.missionRecipeFoodId
    && food.recipe.recipeId === sortContext.missionRecipeId;
}

export function isMissionRecipeExecutionPlan(
  plan: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
): boolean {
  return plan.bucket !== 'blocked'
    && plan.food != null
    && plan.beverage != null
    && isMissionRecipeFoodCandidate(plan.food, sortContext)
    && hasNoHardFailures(plan.food.conditionResults)
    && hasNoHardFailures(plan.beverage.conditionResults)
    && hasNoHardFailures(plan.conditionResults)
    && plan.beverage.meetsRequiredBeverage;
}

function hasNoHardFailures(results: readonly ConditionResult[]): boolean {
  return !results.some((result) => result.status === 'fail' && result.severity === 'hard');
}
