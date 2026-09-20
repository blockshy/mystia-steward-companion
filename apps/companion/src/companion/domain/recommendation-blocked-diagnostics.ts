import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { RecommendationBlockedDiagnostic, RecommendationCandidateStageCounts, RuntimeSets } from '@/companion/types';
import { buildSpecialBusinessOrderRule } from '@/companion/domain/special-business';
import { candidateHasNoHardFailures, describeSpecialBusinessIngredient, isSpecialBusinessFoodBaseMatchCandidate, isSpecialBusinessFoodNegativeSafeCandidate } from '@/companion/domain/special-business/candidate-constraints';
import { diagnoseRareFoodCandidateSearch, diagnoseRareBeverageCandidateSearch, type FoodCandidate, type BeverageCandidate, type RareTagOrderDemand, type RecommendationRuntimeContext, type RareOrderRecommendationPlan } from '@/recommendation-engine';

interface BuildRecommendationBlockedDiagnosticOptions {
  data: RecommendationDataSet;
  demand: RareTagOrderDemand;
  context: RecommendationRuntimeContext;
  runtimeSets: RuntimeSets;
  generatedFoodCandidates: FoodCandidate[];
  combinedFoodCandidatesBeforeSpecialRule: FoodCandidate[];
  combinedFoodCandidates: FoodCandidate[];
  combinedBeverageCandidates: BeverageCandidate[];
  rawPlans: RareOrderRecommendationPlan[];
  safePlans: RareOrderRecommendationPlan[];
  executionPlans: RareOrderRecommendationPlan[];
  specialBusinessRule: ReturnType<typeof buildSpecialBusinessOrderRule>;
}

export function buildRecommendationBlockedDiagnostic({
  data,
  demand,
  context,
  runtimeSets,
  generatedFoodCandidates,
  combinedFoodCandidatesBeforeSpecialRule,
  combinedFoodCandidates,
  combinedBeverageCandidates,
  rawPlans,
  safePlans,
  executionPlans,
  specialBusinessRule,
}: BuildRecommendationBlockedDiagnosticOptions): RecommendationBlockedDiagnostic {
  const foodSearch = diagnoseRareFoodCandidateSearch(
    data,
    demand,
    context,
    generatedFoodCandidates,
  );
  const beverageSearch = diagnoseRareBeverageCandidateSearch(data, demand, context);
  const foodBaseMatchedCandidates = combinedFoodCandidatesBeforeSpecialRule.filter((candidate) =>
    isSpecialBusinessFoodBaseMatchCandidate(candidate, specialBusinessRule)
  );
  const foodNegativeSafeCandidates = foodBaseMatchedCandidates.filter((candidate) =>
    isSpecialBusinessFoodNegativeSafeCandidate(
      candidate,
      specialBusinessRule,
      demand.requiredFoodTag,
    )
  );
  const rawExecutablePlanCount = rawPlans.filter((plan) => plan.bucket !== 'blocked').length;
  const specialSafePlanCount = safePlans.filter((plan) => plan.bucket !== 'blocked').length;
  const counts: RecommendationCandidateStageCounts = {
    foodRecipeEligibility: {
      catalog: foodSearch.catalogRecipeCount,
      requiredTagReachable: foodSearch.requiredTagReachableRecipeCount,
      requiredTagReachableUnlocked: foodSearch.requiredTagReachableUnlockedRecipeCount,
      requiredTagReachableBaseIngredientsReady:
        foodSearch.requiredTagReachableBaseIngredientsReadyRecipeCount,
      requiredTagReachableCookerReady: foodSearch.requiredTagReachableCookerReadyRecipeCount,
    },
    foodCandidates: {
      generated: foodSearch.generatedCandidateCount,
      generatedRequiredTagMatched: foodSearch.generatedRequiredTagMatchedCandidateCount,
      merged: combinedFoodCandidatesBeforeSpecialRule.length,
      baseOrderMatched: foodBaseMatchedCandidates.length,
      negativeSafe: foodNegativeSafeCandidates.length,
      specialRuleMatched: combinedFoodCandidates.length,
      executable: combinedFoodCandidates.filter((candidate) =>
        candidateHasNoHardFailures(candidate.conditionResults)
      ).length,
    },
    beverageCandidates: {
      catalog: beverageSearch.catalogBeverageCount,
      available: beverageSearch.availableBeverageCount,
      allowed: beverageSearch.allowedBeverageCount,
      requiredTagMatched: beverageSearch.requiredTagBeverageCount,
      specialRuleMatched: combinedBeverageCandidates.length,
    },
    plans: {
      rawExecutable: rawExecutablePlanCount,
      specialRuleSafe: specialSafePlanCount,
      executable: executionPlans.length,
    },
  };
  const remainingBudget = normalizeDiagnosticBudget(context.budget?.remainingBudget);
  const minimumPairPrice = findMinimumExecutablePairPrice(
    combinedFoodCandidates,
    combinedBeverageCandidates,
  );
  const runtimeUnavailableCookerNames = foodSearch.missingCookerNames
    .filter((name) => runtimeSets.runtimeUnavailableCookerNames.has(name))
    .sort();
  const usableCookerNames = [...runtimeSets.usableCookerNames].sort();
  const reason = selectRecommendationBlockedReason({
    demand,
    context,
    counts,
    specialBusinessRule,
    missingIngredientNames: foodSearch.missingIngredientNames,
    missingCookerNames: foodSearch.missingCookerNames,
    placedCookerNames: [...runtimeSets.placedCookerNames].sort(),
    usableCookerNames,
    runtimeUnavailableCookerNames,
    remainingBudget,
    minimumPairPrice,
  });
  const diagnosticWithoutSignature = {
    ...reason,
    counts,
    missingIngredientNames: foodSearch.missingIngredientNames,
    requiredCookerNames: foodSearch.missingCookerNames,
    placedCookerNames: [...runtimeSets.placedCookerNames].sort(),
    usableCookerNames,
    runtimeUnavailableCookerNames,
    remainingBudget,
    minimumPairPrice,
  };

  return {
    ...diagnosticWithoutSignature,
    stateSignature: buildRecommendationBlockedStateSignature(diagnosticWithoutSignature),
  };
}

function selectRecommendationBlockedReason({
  demand,
  context,
  counts,
  specialBusinessRule,
  missingIngredientNames,
  missingCookerNames,
  placedCookerNames,
  usableCookerNames,
  runtimeUnavailableCookerNames,
  remainingBudget,
  minimumPairPrice,
}: {
  demand: RareTagOrderDemand;
  context: RecommendationRuntimeContext;
  counts: RecommendationCandidateStageCounts;
  specialBusinessRule: ReturnType<typeof buildSpecialBusinessOrderRule>;
  missingIngredientNames: string[];
  missingCookerNames: string[];
  placedCookerNames: string[];
  usableCookerNames: string[];
  runtimeUnavailableCookerNames: string[];
  remainingBudget: number | null;
  minimumPairPrice: number | null;
}): Pick<RecommendationBlockedDiagnostic, 'code' | 'firstEmptyStage' | 'message'> {
  const foodRecipes = counts.foodRecipeEligibility;
  const foodCandidates = counts.foodCandidates;
  const beverageCandidates = counts.beverageCandidates;
  const plans = counts.plans;

  if (specialBusinessRule.requiredExtraIngredientIds.length > 0
    && foodCandidates.generated === 0) {
    const labels = specialBusinessRule.requiredExtraIngredientIds
      .map(describeSpecialBusinessIngredient)
      .join('、');
    return {
      code: 'food-required-extra-unavailable',
      firstEmptyStage: 'food-required-extra',
      message: `特殊经营强制加料 ${labels} 无法用于当前订单；请检查材料目录、库存、排除设置、配方禁忌和剩余加料槽。`,
    };
  }

  if (specialBusinessRule.forbiddenExtraIngredientIds.length > 0
    && foodCandidates.generated === 0) {
    const labels = specialBusinessRule.forbiddenExtraIngredientIds
      .map(describeSpecialBusinessIngredient)
      .join('、');
    return {
      code: 'food-special-rule-mismatch',
      firstEmptyStage: 'food-special-rule',
      message: `当前订单不能把 ${labels} 作为额外加料，移除后没有可满足原订单的安全料理方案。`,
    };
  }

  if (foodCandidates.baseOrderMatched === 0) {
    if (foodRecipes.requiredTagReachable === 0) {
      return {
        code: 'food-tag-not-supported',
        firstEmptyStage: 'food-tag-reachability',
        message: `当前配方目录在现有加料上限与标签规则下无法构成料理点单标签「${demand.requiredFoodTag}」。`,
      };
    }
    if (foodRecipes.requiredTagReachableUnlocked === 0) {
      return {
        code: 'food-recipe-locked',
        firstEmptyStage: 'food-recipe-unlocked',
        message: `能满足料理点单标签「${demand.requiredFoodTag}」的配方尚未解锁。`,
      };
    }
    if (foodRecipes.requiredTagReachableBaseIngredientsReady === 0) {
      return {
        code: 'food-base-ingredient-missing',
        firstEmptyStage: 'food-base-ingredients',
        message: `满足料理点单标签「${demand.requiredFoodTag}」的已解锁配方缺少基础材料`
          + `${formatDiagnosticNameList(missingIngredientNames)}。`,
      };
    }
    if (foodRecipes.requiredTagReachableCookerReady === 0) {
      if (runtimeUnavailableCookerNames.length > 0) {
        return buildRuntimeUnavailableCookerReason(
          demand.requiredFoodTag,
          runtimeUnavailableCookerNames,
          usableCookerNames,
        );
      }
      return {
        code: 'food-cooker-missing',
        firstEmptyStage: 'food-cooker',
        message: `满足料理点单标签「${demand.requiredFoodTag}」的配方缺少可用厨具`
          + `${formatDiagnosticNameList(missingCookerNames)}；当前摆放`
          + `${formatDiagnosticNameList(placedCookerNames, '无')}。`,
      };
    }
    if (foodCandidates.generatedRequiredTagMatched === 0) {
      return {
        code: 'food-required-tag-not-generated',
        firstEmptyStage: 'food-candidate-generation',
        message: `满足料理点单标签「${demand.requiredFoodTag}」的配方已经满足执行条件，`
          + '但当前可用加料未生成对应料理候选。',
      };
    }
  }

  if (foodCandidates.executable === 0
    && foodRecipes.requiredTagReachableBaseIngredientsReady > 0
    && foodRecipes.requiredTagReachableCookerReady === 0
    && missingCookerNames.length > 0) {
    if (runtimeUnavailableCookerNames.length > 0) {
      return buildRuntimeUnavailableCookerReason(
        demand.requiredFoodTag,
        runtimeUnavailableCookerNames,
        usableCookerNames,
      );
    }
    return {
      code: 'food-cooker-missing',
      firstEmptyStage: 'food-cooker',
      message: `满足料理点单标签「${demand.requiredFoodTag}」的配方缺少可用厨具`
        + `${formatDiagnosticNameList(missingCookerNames)}；当前摆放`
        + `${formatDiagnosticNameList(placedCookerNames, '无')}。`,
    };
  }

  if (foodCandidates.negativeSafe === 0
    && foodCandidates.baseOrderMatched > 0) {
    return {
      code: 'food-negative-tag',
      firstEmptyStage: 'food-negative-safe',
      message: '满足原订单的料理候选均包含当前稀客厌恶标签，已停止自动执行。',
    };
  }
  if (foodCandidates.specialRuleMatched === 0) {
    return {
      code: 'food-special-rule-mismatch',
      firstEmptyStage: 'food-special-rule',
      message: `${specialBusinessRule.reason || '当前经营规则'}下没有可安全执行的料理候选。`,
    };
  }

  if (beverageCandidates.specialRuleMatched === 0) {
    if (beverageCandidates.available === 0) {
      return {
        code: 'beverage-unavailable',
        firstEmptyStage: 'beverage-available',
        message: '当前库存中没有可用酒水。',
      };
    }
    if (beverageCandidates.allowed === 0) {
      return {
        code: 'beverage-excluded',
        firstEmptyStage: 'beverage-allowed',
        message: '当前库存中的酒水均被推荐排除设置过滤。',
      };
    }
    if (beverageCandidates.requiredTagMatched === 0) {
      return {
        code: 'beverage-tag-mismatch',
        firstEmptyStage: 'beverage-required-tag',
        message: `当前可用酒水无法满足酒水点单标签「${demand.requiredBeverageTag}」。`,
      };
    }
    return {
      code: 'beverage-tag-mismatch',
      firstEmptyStage: 'beverage-required-tag',
      message: `${specialBusinessRule.reason || '当前经营规则'}下没有可安全执行的酒水候选。`,
    };
  }

  if (context.budgetPolicy === 'block'
    && (context.budget?.willPayMoney === false
      || (remainingBudget != null
        && minimumPairPrice != null
        && minimumPairPrice > remainingBudget))) {
    return {
      code: 'budget-unavailable',
      firstEmptyStage: 'budget',
      message: context.budget?.willPayMoney === false
        ? '稀客当前不会付款，预算阻止了自动执行。'
        : `最低可执行组合价格 ${minimumPairPrice}，超过剩余预算 ${remainingBudget}。`,
    };
  }

  if (plans.specialRuleSafe === 0
    && (specialBusinessRule.preferYuyukoPositiveSpell
      || specialBusinessRule.requiresHighEvaluation
      || specialBusinessRule.yuyukoProgressEvaluationMode !== 'none')) {
    return {
      code: 'special-evaluation-unmet',
      firstEmptyStage: 'special-evaluation',
      message: specialBusinessRule.preferYuyukoPositiveSpell
        ? '当前资源下没有可预测触发正面符卡的完美（ExGood）组合。'
        : `${specialBusinessRule.reason || '特殊经营'}下没有满足评价要求的安全组合。`,
    };
  }

  return {
    code: 'execution-plan-missing',
    firstEmptyStage: 'execution-plan',
    message: '候选已生成，但当前没有可直接执行的完整料理/酒水组合。',
  };
}

function buildRuntimeUnavailableCookerReason(
  requiredFoodTag: string,
  runtimeUnavailableCookerNames: string[],
  usableCookerNames: string[],
): Pick<RecommendationBlockedDiagnostic, 'code' | 'firstEmptyStage' | 'message'> {
  const orderLabel = requiredFoodTag.trim()
    ? `料理点单标签「${requiredFoodTag}」`
    : '当前订单';
  return {
    code: 'food-cooker-runtime-unavailable',
    firstEmptyStage: 'food-cooker',
    message: `满足${orderLabel}所需的已摆放厨具当前被游戏机制锁定`
      + `${formatDiagnosticNameList(runtimeUnavailableCookerNames)}；当前可开厨具`
      + `${formatDiagnosticNameList(usableCookerNames, '无')}。`,
  };
}

function findMinimumExecutablePairPrice(
  foodCandidates: FoodCandidate[],
  beverageCandidates: BeverageCandidate[],
): number | null {
  let minimum = Number.POSITIVE_INFINITY;
  for (const food of foodCandidates) {
    if (!candidateHasNoHardFailures(food.conditionResults)) continue;
    for (const beverage of beverageCandidates) {
      if (!candidateHasNoHardFailures(beverage.conditionResults)) continue;
      minimum = Math.min(
        minimum,
        Math.max(0, food.recipe.price) + Math.max(0, beverage.beverage.price),
      );
    }
  }
  return Number.isFinite(minimum) ? minimum : null;
}

function normalizeDiagnosticBudget(value: number | null | undefined): number | null {
  if (!Number.isFinite(value)) return null;
  return Math.max(0, Math.trunc(value ?? 0));
}

function formatDiagnosticNameList(values: readonly string[], empty = '未识别'): string {
  const normalized = [...new Set(values.map((value) => value.trim()).filter(Boolean))];
  if (normalized.length === 0) return `：${empty}`;
  const visible = normalized.slice(0, 4);
  const suffix = normalized.length > visible.length ? `等 ${normalized.length} 项` : '';
  return `：${visible.join('、')}${suffix}`;
}

function buildRecommendationBlockedStateSignature(
  diagnostic: Omit<RecommendationBlockedDiagnostic, 'stateSignature'>,
): string {
  return [
    diagnostic.code,
    diagnostic.firstEmptyStage,
    `foodRecipes:${serializeDiagnosticCounts(diagnostic.counts.foodRecipeEligibility)}`,
    `foodCandidates:${serializeDiagnosticCounts(diagnostic.counts.foodCandidates)}`,
    `beverageCandidates:${serializeDiagnosticCounts(diagnostic.counts.beverageCandidates)}`,
    `plans:${serializeDiagnosticCounts(diagnostic.counts.plans)}`,
    `ingredients:${diagnostic.missingIngredientNames.join(',')}`,
    `requiredCookers:${diagnostic.requiredCookerNames.join(',')}`,
    `placedCookers:${diagnostic.placedCookerNames.join(',')}`,
    `usableCookers:${diagnostic.usableCookerNames.join(',')}`,
    `runtimeUnavailableCookers:${diagnostic.runtimeUnavailableCookerNames.join(',')}`,
    `budget:${diagnostic.remainingBudget ?? ''}`,
    `minimum:${diagnostic.minimumPairPrice ?? ''}`,
  ].join('|');
}

function serializeDiagnosticCounts<TCounts extends { [Key in keyof TCounts]: number }>(
  values: TCounts,
): string {
  return Object.entries(values)
    .map(([key, value]) => `${key}:${value}`)
    .join(',');
}
