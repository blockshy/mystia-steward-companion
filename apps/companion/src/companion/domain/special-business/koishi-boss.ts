import type {
  BeverageCandidate,
  FoodCandidate,
  RareOrderRecommendationPlan,
} from '@/recommendation-engine';
import type { SpecialBusinessContext } from '@/companion/types';
import {
  estimateKoishiBrokenShieldDamageLevel as estimateKoishiDamageLevel,
  estimateKoishiBrokenShieldEvaluationScore,
  estimateKoishiBrokenShieldFeedScore as estimateKoishiFeedScore,
  buildKoishiFeedPlanningInfo,
  isKoishiFeedPlanSustainable,
} from '@/recommendation-engine/koishi-feed';

export interface KoishiBrokenShieldPlanContext {
  remainingBudget: number | null | undefined;
  remainingScore: number | null | undefined;
  remainingOrderCount: number | null | undefined;
}

export function buildKoishiBrokenShieldPlanReason(plan: RareOrderRecommendationPlan): string {
  const foodLevel = plan.food?.recipe.level ?? 0;
  const beverageLevel = plan.beverage?.beverage.level ?? 0;
  const preferenceMatches = (plan.food?.matchedPositiveTags.length ?? 0)
    + (plan.beverage?.matchedTags.length ?? 0);
  const negativeMatches = plan.food?.matchedNegativeTags.length ?? 0;
  const estimatedFeedScore = estimateKoishiBrokenShieldFeedScore({
    meetsRequiredFood: plan.food?.meetsRequiredFood === true,
    meetsRequiredBeverage: plan.beverage?.meetsRequiredBeverage === true,
    preferenceMatches,
    negativeMatches,
    foodLevel,
    beverageLevel,
    foodPrice: plan.food?.recipe.price,
    beveragePrice: plan.beverage?.beverage.price,
    estimatedPrice: plan.estimatedPrice,
  });
  const levelHint = estimateKoishiBrokenShieldDamageLevel({
    foodLevel,
    beverageLevel,
    negativeMatches,
  });
  const evaluationHint = estimateKoishiBrokenShieldScore({
    meetsRequiredFood: plan.food?.meetsRequiredFood === true,
    meetsRequiredBeverage: plan.beverage?.meetsRequiredBeverage === true,
    preferenceMatches,
    negativeMatches,
  });
  const budgetText = buildKoishiBrokenShieldBudgetText(plan);

  return [
    `古明地恋破防：先满足原订单料理/酒水要求，再按预算内总分规划；料理 Lv.${foodLevel}`,
    `酒水 Lv.${beverageLevel}`,
    `等级参考 ${levelHint}`,
    `投食分估算 ${estimatedFeedScore}`,
    `喜好命中 ${preferenceMatches}`,
    `厌恶 ${negativeMatches}`,
    `评价参考 ${evaluationHint}`,
    budgetText,
  ].join('，');
}

export function estimateKoishiBrokenShieldScore({
  meetsRequiredFood,
  meetsRequiredBeverage,
  preferenceMatches,
  negativeMatches,
}: {
  meetsRequiredFood: boolean;
  meetsRequiredBeverage: boolean;
  preferenceMatches: number;
  negativeMatches: number;
}): number {
  return estimateKoishiBrokenShieldEvaluationScore({
    meetsRequiredFood,
    meetsRequiredBeverage,
    preferenceMatches,
    negativeMatches,
  });
}

export function estimateKoishiBrokenShieldFeedScore({
  meetsRequiredFood,
  meetsRequiredBeverage,
  preferenceMatches,
  negativeMatches,
  foodLevel,
  beverageLevel,
  foodPrice,
  beveragePrice,
  estimatedPrice,
}: {
  meetsRequiredFood: boolean;
  meetsRequiredBeverage: boolean;
  preferenceMatches: number;
  negativeMatches: number;
  foodLevel?: number | null;
  beverageLevel?: number | null;
  foodPrice?: number | null;
  beveragePrice?: number | null;
  estimatedPrice?: number | null;
}): number {
  return estimateKoishiFeedScore({
    meetsRequiredFood,
    meetsRequiredBeverage,
    preferenceMatches,
    negativeMatches,
    foodLevel,
    beverageLevel,
    foodPrice,
    beveragePrice,
    estimatedPrice,
  });
}

export function estimateKoishiBrokenShieldDamageLevel({
  foodLevel,
  beverageLevel,
  negativeMatches,
}: {
  foodLevel: number;
  beverageLevel: number;
  negativeMatches: number;
}): number {
  return estimateKoishiDamageLevel({
    foodLevel,
    beverageLevel,
    negativeMatches,
  });
}

export function scoreKoishiBrokenShieldCandidatePair(
  food: FoodCandidate,
  beverage: BeverageCandidate,
  context: KoishiBrokenShieldPlanContext,
): number {
  const estimatedPrice = estimateKoishiPairPrice(food, beverage);
  return scoreKoishiBrokenShieldPlanCore({
    estimatedPrice,
    feedScore: estimateKoishiBrokenShieldCandidatePairFeedScore(food, beverage, estimatedPrice),
    preferenceMatches: food.matchedPositiveTags.length + beverage.matchedTags.length,
    negativeMatches: food.matchedNegativeTags.length,
    resourcePressure: food.resourcePressure,
    extraIngredientCount: food.extraIngredients.length,
  }, context);
}

export function scoreKoishiBrokenShieldRecommendationPlan(
  plan: RareOrderRecommendationPlan,
  context: KoishiBrokenShieldPlanContext,
): number {
  if (!plan.food || !plan.beverage || plan.bucket === 'blocked') return Number.NEGATIVE_INFINITY;
  return scoreKoishiBrokenShieldPlanCore({
    estimatedPrice: Math.max(0, plan.estimatedPrice),
    feedScore: estimateKoishiBrokenShieldRecommendationPlanFeedScore(plan),
    preferenceMatches: (plan.food.matchedPositiveTags.length ?? 0) + (plan.beverage.matchedTags.length ?? 0),
    negativeMatches: plan.food.matchedNegativeTags.length,
    resourcePressure: plan.food.resourcePressure,
    extraIngredientCount: plan.food.extraIngredients.length,
  }, context);
}

export function compareKoishiBrokenShieldRecommendationPlans(
  left: RareOrderRecommendationPlan,
  right: RareOrderRecommendationPlan,
  context: KoishiBrokenShieldPlanContext,
): number {
  const scoreDiff = scoreKoishiBrokenShieldRecommendationPlan(right, context)
    - scoreKoishiBrokenShieldRecommendationPlan(left, context);
  if (scoreDiff !== 0) return scoreDiff;
  return left.estimatedPrice - right.estimatedPrice;
}

export function getKoishiRemainingTargetScore(specialBusiness: SpecialBusinessContext | null | undefined): number | null {
  if (!specialBusiness?.active) return null;
  const max = normalizeNonNegativeInt(specialBusiness.maxValue);
  const achieved = normalizeNonNegativeInt(specialBusiness.targetValue)
    ?? normalizeNonNegativeInt(specialBusiness.currentValue)
    ?? 0;
  if (max != null && max > 0) return Math.max(0, max - achieved);

  const target = normalizeNonNegativeInt(specialBusiness.targetValue);
  const current = normalizeNonNegativeInt(specialBusiness.currentValue) ?? 0;
  if (target == null || target <= 0) return null;
  return Math.max(0, target - current);
}

function scoreKoishiBrokenShieldPlanCore({
  estimatedPrice,
  feedScore,
  preferenceMatches,
  negativeMatches,
  resourcePressure,
  extraIngredientCount,
}: {
  estimatedPrice: number;
  feedScore: number;
  preferenceMatches: number;
  negativeMatches: number;
  resourcePressure: number;
  extraIngredientCount: number;
}, context: KoishiBrokenShieldPlanContext): number {
  const remainingBudget = normalizeNonNegativeInt(context.remainingBudget);
  const remainingScore = normalizeNonNegativeInt(context.remainingScore);
  const remainingOrderCount = normalizeNonNegativeInt(context.remainingOrderCount);
  const budgetFitScore = remainingBudget == null
    ? 0
    : estimatedPrice <= remainingBudget ? 1_000_000_000 : -Math.max(0, estimatedPrice - remainingBudget) * 10_000;
  const budgetPlanScore = scoreKoishiBudgetPlan(estimatedPrice, feedScore, remainingBudget, remainingScore, remainingOrderCount);
  const planning = buildKoishiFeedPlanningInfo({ remainingScore, remainingBudget, remainingOrderCount });
  const meetsAttemptFloor = planning.requiredScoreThisOrder == null || feedScore >= planning.requiredScoreThisOrder;
  const attemptPlanScore = planning.requiredScoreThisOrder == null
    ? 0
    : meetsAttemptFloor ? 2_000_000_000 : -Math.max(0, planning.requiredScoreThisOrder - feedScore) * 500_000_000;
  return budgetFitScore
    + attemptPlanScore
    + budgetPlanScore
    + feedScore * 10_000_000
    + preferenceMatches * 100
    - negativeMatches * 100_000
    - Math.ceil(resourcePressure * 10)
    - estimatedPrice
    - extraIngredientCount;
}

function scoreKoishiBudgetPlan(
  estimatedPrice: number,
  estimatedFeedScore: number,
  remainingBudget: number | null,
  remainingScore: number | null,
  remainingOrderCount: number | null,
): number {
  if (remainingBudget == null || remainingScore == null || remainingScore <= 0 || estimatedFeedScore <= 0) return 0;
  if (estimatedPrice > remainingBudget) return -Math.max(0, estimatedPrice - remainingBudget) * 1_000_000;
  const completesTarget = estimatedFeedScore >= remainingScore;
  const sustainable = isKoishiFeedPlanSustainable({
    estimatedPrice,
    estimatedFeedScore,
    remainingBudget,
    remainingScore,
    remainingOrderCount,
  });
  const efficiency = Math.round((estimatedFeedScore * 1_000_000) / Math.max(1, estimatedPrice));
  return (sustainable ? 3_000_000_000 : 0)
    + (completesTarget ? 1_000_000_000 : 0)
    + efficiency;
}

function estimateKoishiBrokenShieldCandidatePairFeedScore(
  food: FoodCandidate,
  beverage: BeverageCandidate,
  estimatedPrice: number,
): number {
  return estimateKoishiBrokenShieldFeedScore({
    meetsRequiredFood: food.meetsRequiredFood,
    meetsRequiredBeverage: beverage.meetsRequiredBeverage,
    preferenceMatches: food.matchedPositiveTags.length + beverage.matchedTags.length,
    negativeMatches: food.matchedNegativeTags.length,
    foodLevel: food.recipe.level,
    beverageLevel: beverage.beverage.level,
    foodPrice: food.recipe.price,
    beveragePrice: beverage.beverage.price,
    estimatedPrice,
  });
}

function estimateKoishiBrokenShieldRecommendationPlanFeedScore(plan: RareOrderRecommendationPlan): number {
  return estimateKoishiBrokenShieldFeedScore({
    meetsRequiredFood: plan.food?.meetsRequiredFood === true,
    meetsRequiredBeverage: plan.beverage?.meetsRequiredBeverage === true,
    preferenceMatches: (plan.food?.matchedPositiveTags.length ?? 0) + (plan.beverage?.matchedTags.length ?? 0),
    negativeMatches: plan.food?.matchedNegativeTags.length ?? 0,
    foodLevel: plan.food?.recipe.level,
    beverageLevel: plan.beverage?.beverage.level,
    foodPrice: plan.food?.recipe.price,
    beveragePrice: plan.beverage?.beverage.price,
    estimatedPrice: plan.estimatedPrice,
  });
}

function estimateKoishiPairPrice(food: FoodCandidate, beverage: BeverageCandidate): number {
  return Math.max(0, food.recipe.price) + Math.max(0, beverage.beverage.price);
}

function buildKoishiBrokenShieldBudgetText(plan: RareOrderRecommendationPlan): string {
  const budget = plan.budget;
  if (!budget) return '预算未读取';
  if (budget.remainingBudget == null) return `预算未知，预计花费 ${budget.estimatedPrice}`;
  if (budget.overBudget > 0) return `预计花费 ${budget.estimatedPrice}，超预算 ${budget.overBudget} / 剩余 ${budget.remainingBudget}`;
  return `预计花费 ${budget.estimatedPrice} / 剩余预算 ${budget.remainingBudget}`;
}

function normalizeNonNegativeInt(value: number | null | undefined): number | null {
  if (!Number.isFinite(value)) return null;
  return Math.max(0, Math.trunc(value ?? 0));
}
