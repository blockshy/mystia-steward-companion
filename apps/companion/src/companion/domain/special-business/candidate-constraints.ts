import { buildSpecialBusinessOrderRule, buildWackyRejectedRecipeKeyForRareRecipe, matchesSpecialBusinessFoodTarget } from '@/companion/domain/special-business';
import { isYuyukoProgressPlan } from '@/companion/domain/special-business/yuyuko-challenge';
import { getYuyukoPositiveSpellNegativeTags, isYuyukoPositiveSpellPlan } from '@/companion/domain/special-business/yuyuko-positive-spell';
import type { BeverageCandidate, FoodCandidate, RareOrderRecommendationPlan } from '@/recommendation-engine';

export function filterSpecialBusinessFoodCandidates(
  candidates: FoodCandidate[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
  rejectedRecipeKeys: Set<string>,
  requiredFoodTag: string,
): FoodCandidate[] {
  return candidates.filter((candidate) => {
    if (!isSpecialBusinessFoodBaseMatchCandidate(candidate, rule)) return false;
    if (!isSpecialBusinessFoodNegativeSafeCandidate(candidate, rule, requiredFoodTag)) return false;
    if (!hasRequiredExtraIngredients(candidate, rule.requiredExtraIngredientIds)) return false;
    if (!hasNoForbiddenExtraIngredients(candidate, rule.forbiddenExtraIngredientIds)) return false;
    if (rule.blockingReason) return false;
    if (rule.foodTarget.enforcement !== 'require') return true;
    if (!matchesSpecialBusinessFoodTarget(candidate.activeTags, rule.foodTarget)) return false;
    const key = buildWackyRejectedRecipeKeyForRareRecipe(
      rule.foodTarget.tags,
      candidate.recipe.id,
      candidate.recipe.recipeId,
      candidate.extraIngredients.map((ingredient) => ingredient.id),
    );
    return !key || !rejectedRecipeKeys.has(key);
  });
}

export function isSpecialBusinessFoodBaseMatchCandidate(
  candidate: FoodCandidate,
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): boolean {
  return !rule.requiresBaseOrderMatch || candidate.meetsRequiredFood;
}

function hasRequiredExtraIngredients(
  candidate: FoodCandidate,
  requiredIds: readonly number[],
): boolean {
  if (requiredIds.some((id) => !Number.isInteger(id) || id < 0)) return false;
  if (new Set(requiredIds).size !== requiredIds.length) return false;
  const candidateIds = candidate.extraIngredients.map((ingredient) => ingredient.id);
  return requiredIds.every((id) => candidateIds.filter((candidateId) => candidateId === id).length === 1);
}

function hasNoForbiddenExtraIngredients(
  candidate: FoodCandidate,
  forbiddenIds: readonly number[],
): boolean {
  if (forbiddenIds.some((id) => !Number.isInteger(id) || id < 0)) return false;
  if (new Set(forbiddenIds).size !== forbiddenIds.length) return false;
  const candidateIds = new Set(candidate.extraIngredients.map((ingredient) => ingredient.id));
  return forbiddenIds.every((id) => !candidateIds.has(id));
}

export function isSpecialBusinessFoodNegativeSafeCandidate(
  candidate: FoodCandidate,
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
  requiredFoodTag: string,
): boolean {
  if (rule.preferYuyukoPositiveSpell) {
    return getYuyukoPositiveSpellNegativeTags(candidate, requiredFoodTag).length === 0;
  }
  if (rule.yuyukoProgressEvaluationMode === 'retake-tag-order') {
    return getYuyukoPositiveSpellNegativeTags(candidate, requiredFoodTag).length === 0;
  }
  if (rule.yuyukoProgressEvaluationMode === 'story-level-sum') return true;
  if ((rule.requiresHighEvaluation || rule.preferKoishiDamage)
    && candidate.matchedNegativeTags.length > 0) return false;
  return true;
}

export function filterSpecialBusinessBeverageCandidates(
  candidates: BeverageCandidate[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): BeverageCandidate[] {
  if (rule.blockingReason) return [];
  if (!rule.requiresBaseOrderMatch && !rule.requiresHighEvaluation) return candidates;
  return candidates.filter((candidate) => candidate.meetsRequiredBeverage);
}

export function filterSpecialBusinessExecutionPlans(
  plans: RareOrderRecommendationPlan[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): RareOrderRecommendationPlan[] {
  if (rule.blockingReason) return [];
  if (rule.foodTarget.enforcement !== 'require'
    && rule.requiredExtraIngredientIds.length === 0
    && rule.forbiddenExtraIngredientIds.length === 0
    && !rule.requiresBaseOrderMatch
    && !rule.requiresHighEvaluation) return plans;
  return plans.filter((plan) => isSpecialBusinessSafeExecutionPlan(plan, rule));
}

export function describeSpecialBusinessIngredient(id: number): string {
  switch (id) {
    case 5002:
      return '噗噗呦果';
    case 5005:
      return '辣椒水';
    default:
      return `#${id}`;
  }
}

function isSpecialBusinessSafeExecutionPlan(
  plan: RareOrderRecommendationPlan,
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): boolean {
  const food = plan.food;
  const beverage = plan.beverage;
  if (!food || !beverage || plan.bucket === 'blocked') return false;
  if (rule.blockingReason) return false;
  if (rule.foodTarget.enforcement === 'require'
    && !matchesSpecialBusinessFoodTarget(food.activeTags, rule.foodTarget)) return false;
  if (!hasRequiredExtraIngredients(food, rule.requiredExtraIngredientIds)) return false;
  if (!hasNoForbiddenExtraIngredients(food, rule.forbiddenExtraIngredientIds)) return false;
  if (rule.requiresBaseOrderMatch && (!food.meetsRequiredFood || !beverage.meetsRequiredBeverage)) return false;
  if (rule.yuyukoProgressEvaluationMode !== 'none') {
    return isYuyukoProgressPlan(plan, rule.yuyukoProgressEvaluationMode);
  }
  if (rule.preferYuyukoPositiveSpell) {
    return isYuyukoPositiveSpellPlan(plan);
  }
  if (!rule.requiresHighEvaluation) return true;
  if (food.matchedNegativeTags.length > 0) return false;

  const baseScore = (food.meetsRequiredFood ? 1 : 0) + (beverage.meetsRequiredBeverage ? 1 : 0);
  const preferenceMatches = food.matchedPositiveTags.length + beverage.matchedTags.length;
  return preferenceMatches >= rule.highEvaluationMinPreferenceMatches
    && baseScore + preferenceMatches >= 4;
}

export function candidateHasNoHardFailures(results: { status: string; severity: string }[]): boolean {
  return !results.some((result) => result.status === 'fail' && result.severity === 'hard');
}
