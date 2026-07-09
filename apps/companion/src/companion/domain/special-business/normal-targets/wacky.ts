import {
  estimateKoishiBrokenShieldDamageLevel,
  estimateKoishiBrokenShieldFeedScore,
  estimateKoishiBrokenShieldScore,
  getKoishiRemainingTargetScore,
  scoreKoishiBrokenShieldCandidatePair,
} from '@/companion/domain/special-business/koishi-boss';
import {
  buildKoishiFeedPlanningInfo,
  isKoishiFeedPlanSustainable,
} from '@/recommendation-engine/koishi-feed';
import {
  buildWackyRejectedRecipeKeyForRareRecipe,
  getWackyTargetTagCountdownDeferral,
  hasMatchingSpecialBusinessTag,
  isPhaseTwoContext,
  isPhaseThreeContext,
  KOISHI_BOSS_ROLE,
  normalizeSpecialBusinessTags,
} from '@/companion/domain/special-business/rules';
import {
  buildExecutionTarget,
  buildNormalTargetRuntimeContext,
  buildSyntheticCustomer,
  buildSyntheticDemand,
  emptySelection,
  estimateOrderPrice,
  findOrderRecipe,
  firstTag,
  hasNoHardFailures,
  hasNoHardFailuresExcept,
  selectBestPair,
} from '@/companion/domain/special-business/normal-targets/shared';
import type {
  SpecialBusinessNormalTargetArgs,
  SpecialBusinessNormalTargetSelection,
} from '@/companion/domain/special-business/types';
import type { CompanionPreferences } from '@/companion/preferences';
import type {
  NormalBusinessOrder,
  RecommendationStateSnapshot,
  SpecialBusinessContext,
} from '@/companion/types';
import { ALL_PLACES, type RareCustomerCatalogItem } from '@/lib/catalog-types';
import {
  DEFAULT_RECOMMENDATION_DATA,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import {
  buildRareBeverageCandidates,
  buildRareFoodCandidates,
  type BeverageCandidate,
  type FoodCandidate,
  type RecommendationRuntimeContext,
} from '@/recommendation-engine';

const WACKY_PHASE_ONE_MIN_MATCH_COUNT = 3;
const WACKY_KOISHI_BODY_MIN_FOOD_MATCHES = 3;
const WACKY_KOISHI_BODY_MIN_BEVERAGE_MATCHES = 1;

export function selectWackyNormalExecutionTarget({
  order,
  specialBusiness,
  runtime,
  preferences,
  data = DEFAULT_RECOMMENDATION_DATA,
  rejectedRecipeKeys = [],
}: SpecialBusinessNormalTargetArgs): SpecialBusinessNormalTargetSelection {
  if (!specialBusiness?.active) return emptySelection();
  if (!runtime || data.source !== 'runtime') {
    return { target: null, message: '特殊经营自动化等待运行时推荐数据后再选择普客执行目标。' };
  }

  return selectWackyNormalTarget(order, specialBusiness, runtime, preferences, data, rejectedRecipeKeys);
}

function selectWackyNormalTarget(
  order: NormalBusinessOrder,
  specialBusiness: SpecialBusinessContext,
  runtime: RecommendationStateSnapshot,
  preferences: CompanionPreferences,
  data: RecommendationDataSet,
  rejectedRecipeKeys: readonly string[],
): SpecialBusinessNormalTargetSelection {
  const targetTags = normalizeSpecialBusinessTags(specialBusiness.foodTargetTags);
  const context = buildNormalTargetRuntimeContext(runtime, preferences, data);
  if (!context) return emptySelection();

  if (isWackyKoishiBossOrder(order, specialBusiness)) {
    return selectWackyKoishiBossNormalTarget(order, specialBusiness, context, data);
  }

  const countdownMessage = targetTags.length > 0 ? getWackyTargetTagCountdownDeferral(specialBusiness) : '';
  if (countdownMessage) return { target: null, message: countdownMessage };
  const requiresHighEvaluation = isWackyHighEvaluationPhase(specialBusiness.phase);
  if (requiresHighEvaluation) {
    return selectWackyExactNormalTarget(order, specialBusiness, context, data, rejectedRecipeKeys);
  }

  const syntheticCustomer = buildSyntheticCustomer(order, data, {
    preferRareCustomer: false,
    fallbackFoodTags: [],
  });
  const demand = buildSyntheticDemand(
    syntheticCustomer,
    firstTag(syntheticCustomer.positiveTags) || targetTags[0] || '',
    firstTag(syntheticCustomer.beverageTags),
    targetTags,
  );
  let foodCandidates = buildRareFoodCandidates(data, demand, context)
    .filter(hasNoHardFailures);
  const beverageCandidates = buildRareBeverageCandidates(data, demand, context)
    .filter(hasNoHardFailures);

  if (targetTags.length > 0) {
    const rejected = new Set(rejectedRecipeKeys);
    foodCandidates = foodCandidates.filter((candidate) => {
      if (!hasMatchingSpecialBusinessTag(candidate.activeTags, targetTags)) return false;
      const key = buildWackyRejectedRecipeKeyForRareRecipe(
        targetTags,
        candidate.recipe.id,
        candidate.recipe.recipeId,
        candidate.extraIngredients.map((ingredient) => ingredient.id),
      );
      return !key || !rejected.has(key);
    });
  }

  const best = selectBestPair({
    foodCandidates,
    beverageCandidates,
    scoreFood: (candidate) => scoreWackyFood(candidate, targetTags),
    scoreBeverage: (candidate) => scorePreferenceBeverage(candidate),
    scorePair: (food, beverage) => scoreWackyPair(food, beverage, targetTags),
  });
  if (!best) {
    return {
      target: null,
      message: targetTags.length > 0
        ? `当前怪诞料理目标 Tag 为 ${targetTags.join('、')}，没有可制作且未被实机判定失败的普客替代料理。`
        : '当前没有可用于怪诞料理大赛高评价的普客替代料理/酒水组合。',
    };
  }

  const matchCount = best.food.matchedPositiveTags.length + best.beverage.matchedTags.length;
  if ((targetTags.length === 0 || requiresHighEvaluation) && matchCount < WACKY_PHASE_ONE_MIN_MATCH_COUNT) {
    return {
      target: null,
      message: targetTags.length > 0
        ? `怪诞料理大赛当前目标 Tag 为 ${targetTags.join('、')}，但普客没有同时满足目标 Tag 且命中至少 ${WACKY_PHASE_ONE_MIN_MATCH_COUNT} 个喜好 Tag 的稳定高评价组合。`
        : `怪诞料理大赛第一阶段需要至少 ${WACKY_PHASE_ONE_MIN_MATCH_COUNT} 个喜好 Tag 命中，当前普客没有稳定高评价组合。`,
    };
  }

  return {
    target: buildExecutionTarget(
      order,
      best.food,
      best.beverage,
      targetTags.length > 0
        ? `怪诞目标 Tag：${targetTags.join('、')}，高评价命中 ${matchCount} 个喜好 Tag`
        : `怪诞高评价命中 ${matchCount} 个喜好 Tag`,
      { wackyTargetFoodTags: targetTags },
    ),
    message: '',
  };
}

function selectWackyKoishiBossNormalTarget(
  order: NormalBusinessOrder,
  specialBusiness: SpecialBusinessContext,
  context: RecommendationRuntimeContext,
  data: RecommendationDataSet,
): SpecialBusinessNormalTargetSelection {
  if (specialBusiness.wackyKoishiShieldBroken === true) {
    return selectWackyKoishiBrokenShieldTarget(order, specialBusiness, context, data);
  }

  const foodPreferenceTags = normalizeSpecialBusinessTags(specialBusiness.wackyKoishiFoodPreferenceTags);
  const foodHateTags = normalizeSpecialBusinessTags(specialBusiness.wackyKoishiFoodHateTags);
  const beveragePreferenceTags = normalizeSpecialBusinessTags(specialBusiness.wackyKoishiBeveragePreferenceTags);
  if (foodPreferenceTags.length === 0 || beveragePreferenceTags.length === 0) {
    return {
      target: null,
      message: [
        '怪诞料理三阶段小石本体需要先读取场上揭示的正面料理 Tag 和酒水 Tag，暂不自动提交，避免继续触发差评。',
        `已读取：正面料理 ${foodPreferenceTags.length ? foodPreferenceTags.join('、') : '无'}；厌恶料理 ${foodHateTags.length ? foodHateTags.join('、') : '无'}；酒水 ${beveragePreferenceTags.length ? beveragePreferenceTags.join('、') : '无'}。`,
      ].join('\n'),
    };
  }

  const syntheticCustomer = buildKoishiBossSyntheticCustomer(order, foodPreferenceTags, foodHateTags, beveragePreferenceTags);
  const demand = buildSyntheticDemand(
    syntheticCustomer,
    firstTag(foodPreferenceTags),
    firstTag(beveragePreferenceTags),
  );
  const minFoodMatches = Math.min(WACKY_KOISHI_BODY_MIN_FOOD_MATCHES, foodPreferenceTags.length);
  const foodCandidates = buildRareFoodCandidates(data, demand, context)
    .filter((candidate) => hasNoHardFailuresExcept(candidate, 'food.required-tag'))
    .filter((candidate) => candidate.matchedNegativeTags.length === 0)
    .filter((candidate) => candidate.matchedPositiveTags.length >= minFoodMatches);
  const beverageCandidates = buildRareBeverageCandidates(data, demand, context)
    .filter((candidate) => hasNoHardFailuresExcept(candidate, 'beverage.required-tag'))
    .filter((candidate) => candidate.matchedTags.length >= WACKY_KOISHI_BODY_MIN_BEVERAGE_MATCHES);
  const best = selectBestPair({
    foodCandidates,
    beverageCandidates,
    scoreFood: scoreKoishiBodyFood,
    scoreBeverage: scoreKoishiBodyBeverage,
    scorePair: scoreKoishiBodyPair,
  });

  if (!best) {
    return {
      target: null,
      message: `怪诞料理三阶段小石本体当前揭示：正面料理 ${foodPreferenceTags.join('、')}，厌恶料理 ${foodHateTags.length ? foodHateTags.join('、') : '无'}，酒水 ${beveragePreferenceTags.join('、')}；没有可制作且可稳定高评价的料理/酒水组合。`,
    };
  }

  return {
    target: buildExecutionTarget(
      order,
      best.food,
      best.beverage,
      `小石本体：命中正面料理 ${best.food.matchedPositiveTags.join('、')}，酒水 ${best.beverage.matchedTags.join('、')}，避开厌恶 Tag${foodHateTags.length ? ` ${foodHateTags.join('、')}` : ''}`,
    ),
    message: '',
  };
}

function selectWackyKoishiBrokenShieldTarget(
  order: NormalBusinessOrder,
  specialBusiness: SpecialBusinessContext,
  context: RecommendationRuntimeContext,
  data: RecommendationDataSet,
): SpecialBusinessNormalTargetSelection {
  const originalRecipe = findOrderRecipe(order, data);
  if (!originalRecipe) {
    return { target: null, message: `怪诞料理三阶段小石本体已破防，但无法找到原订单料理 ${order.foodName || `#${order.foodId}`} 的配方数据。` };
  }

  const originalBeverage = data.beverages.find((beverage) => beverage.id === order.beverageId) ?? null;
  if (!originalBeverage) {
    return { target: null, message: `怪诞料理三阶段小石本体已破防，但无法找到原订单酒水 ${order.beverageName || `#${order.beverageId}`} 的数据。` };
  }

  const syntheticCustomer = buildSyntheticCustomer(order, data, {
    preferRareCustomer: true,
    fallbackFoodTags: originalRecipe.positiveTags,
  });
  const demand = buildSyntheticDemand(
    syntheticCustomer,
    firstTag(syntheticCustomer.positiveTags) || originalRecipe.positiveTags[0] || '',
    firstTag(syntheticCustomer.beverageTags) || originalBeverage.tags[0] || '',
  );
  const foodCandidates = buildRareFoodCandidates(data, demand, context)
    .filter((candidate) => hasNoHardFailuresExcept(candidate, 'food.required-tag'))
    .filter((candidate) => candidate.recipe.id === originalRecipe.id)
    .filter((candidate) => candidate.matchedNegativeTags.length === 0);
  const beverageCandidates = buildRareBeverageCandidates(data, demand, context)
    .filter((candidate) => hasNoHardFailuresExcept(candidate, 'beverage.required-tag'))
    .filter((candidate) => candidate.beverage.id === originalBeverage.id);
  const remainingBudget = getNormalOrderRemainingBudget(order);
  const remainingScore = getKoishiRemainingTargetScore(specialBusiness);
  const remainingOrderCount = getNormalOrderRemainingOrderCount(order);
  const best = selectBestPair({
    foodCandidates,
    beverageCandidates,
    scoreFood: scoreKoishiBrokenShieldFood,
    scoreBeverage: scoreKoishiBrokenShieldBeverage,
    scorePair: (food, beverage) => scoreKoishiBrokenShieldPair(food, beverage, remainingBudget, remainingScore, remainingOrderCount),
  });

  if (!best) {
    return { target: null, message: `怪诞料理三阶段小石本体已破防，但 ${originalRecipe.name} / ${originalBeverage.name} 当前没有可制作且满足原订单的方案。` };
  }

  const estimatedPrice = estimateOrderPrice(best.food, best.beverage);
  const estimatedScore = estimateKoishiBrokenShieldPairScore(best.food, best.beverage);
  const feedScore = estimateKoishiBrokenShieldPairFeedScore(best.food, best.beverage);
  const levelHint = estimateKoishiBrokenShieldPairDamageLevel(best.food, best.beverage);
  const budgetReason = buildKoishiBrokenShieldBudgetReason(
    estimatedPrice,
    remainingBudget,
    levelHint,
    estimatedScore,
    feedScore,
    remainingScore,
    remainingOrderCount,
  );

  return {
    target: buildExecutionTarget(
      order,
      best.food,
      best.beverage,
      `小石破防：保持原订单 ${originalRecipe.name} / ${originalBeverage.name}，按预算内总分规划，料理 Lv.${best.food.recipe.level} / 酒水 Lv.${best.beverage.beverage.level}${budgetReason}`,
    ),
    message: '',
  };
}

function selectWackyExactNormalTarget(
  order: NormalBusinessOrder,
  specialBusiness: SpecialBusinessContext,
  context: RecommendationRuntimeContext,
  data: RecommendationDataSet,
  rejectedRecipeKeys: readonly string[],
): SpecialBusinessNormalTargetSelection {
  const targetTags = normalizeSpecialBusinessTags(specialBusiness.foodTargetTags);
  const originalRecipe = findOrderRecipe(order, data);
  if (!originalRecipe) {
    return { target: null, message: `怪诞料理大赛无法找到原订单料理 ${order.foodName || `#${order.foodId}`} 的配方数据。` };
  }

  const originalBeverage = data.beverages.find((beverage) => beverage.id === order.beverageId) ?? null;
  if (!originalBeverage) {
    return { target: null, message: `怪诞料理大赛无法找到原订单酒水 ${order.beverageName || `#${order.beverageId}`} 的数据。` };
  }

  const syntheticCustomer = buildSyntheticCustomer(order, data, {
    preferRareCustomer: true,
    fallbackFoodTags: originalRecipe.positiveTags,
  });
  const demand = buildSyntheticDemand(
    syntheticCustomer,
    firstTag(syntheticCustomer.positiveTags) || originalRecipe.positiveTags[0] || targetTags[0] || '',
    firstTag(syntheticCustomer.beverageTags) || originalBeverage.tags[0] || '',
    targetTags,
  );
  const rejected = new Set(rejectedRecipeKeys);
  const foodCandidates = buildRareFoodCandidates(data, demand, context)
    .filter(hasNoHardFailures)
    .filter((candidate) => candidate.recipe.id === originalRecipe.id)
    .filter((candidate) => candidate.matchedNegativeTags.length === 0)
    .filter((candidate) => {
      if (targetTags.length > 0 && !hasMatchingSpecialBusinessTag(candidate.activeTags, targetTags)) return false;
      const key = buildWackyRejectedRecipeKeyForRareRecipe(
        targetTags,
        candidate.recipe.id,
        candidate.recipe.recipeId,
        candidate.extraIngredients.map((ingredient) => ingredient.id),
      );
      return !key || !rejected.has(key);
    });
  const beverageCandidates = buildRareBeverageCandidates(data, demand, context)
    .filter(hasNoHardFailures)
    .filter((candidate) => candidate.beverage.id === originalBeverage.id);
  const best = selectBestPair({
    foodCandidates,
    beverageCandidates,
    scoreFood: (candidate) => scoreWackyExactFood(candidate, targetTags),
    scoreBeverage: scorePreferenceBeverage,
    scorePair: scoreWackyExactPair,
  });

  if (!best) {
    const targetText = targetTags.length > 0 ? `并满足当前怪诞 Tag ${targetTags.join('、')}` : '';
    return {
      target: null,
      message: `怪诞料理大赛需要按原订单制作 ${originalRecipe.name} / ${originalBeverage.name}${targetText}，当前没有安全的高评价加料方案。`,
    };
  }

  if (!isWackyHighEvaluationPair(best.food, best.beverage)) {
    const matchCount = best.food.matchedPositiveTags.length + best.beverage.matchedTags.length;
    return {
      target: null,
      message: `怪诞料理大赛需要原订单最高评价，${originalRecipe.name} / ${originalBeverage.name} 当前仅命中 ${matchCount} 个喜好 Tag，等待更安全的订单或目标刷新。`,
    };
  }

  const matchCount = best.food.matchedPositiveTags.length + best.beverage.matchedTags.length;
  return {
    target: buildExecutionTarget(
      order,
      best.food,
      best.beverage,
      targetTags.length > 0
        ? `怪诞高评价：保持原订单，目标 Tag ${targetTags.join('、')}，命中 ${matchCount} 个喜好 Tag`
        : `怪诞高评价：保持原订单，命中 ${matchCount} 个喜好 Tag`,
      { wackyTargetFoodTags: targetTags },
    ),
    message: '',
  };
}

function isWackyKoishiBossOrder(order: NormalBusinessOrder, specialBusiness: SpecialBusinessContext): boolean {
  return isPhaseThreeContext(specialBusiness.phase)
    && (order.specialBusinessRole ?? '').trim() === KOISHI_BOSS_ROLE;
}

function buildKoishiBossSyntheticCustomer(
  order: NormalBusinessOrder,
  positiveTags: readonly string[],
  negativeTags: readonly string[],
  beverageTags: readonly string[],
): RareCustomerCatalogItem {
  return {
    id: order.guestId ?? -1,
    name: order.guestName || '怪诞料理大赛 · 小石本体',
    description: '',
    dlc: 0,
    places: ALL_PLACES,
    price: [0, 0],
    enduranceLimit: 1,
    positiveTags: normalizeSpecialBusinessTags(positiveTags),
    negativeTags: normalizeSpecialBusinessTags(negativeTags),
    beverageTags: normalizeSpecialBusinessTags(beverageTags),
    collection: false,
    evaluation: {},
    spellCards: { positive: [], negative: [] },
  };
}

function scoreWackyFood(candidate: FoodCandidate, targetTags: readonly string[]): number {
  const targetScore = targetTags.length > 0 && hasMatchingSpecialBusinessTag(candidate.activeTags, targetTags) ? 10000 : 0;
  return targetScore
    + candidate.matchedPositiveTags.length * 600
    - candidate.matchedNegativeTags.length * 5000
    + candidate.recipe.level * 40
    + candidate.recipe.price
    - candidate.resourcePressure;
}

function scorePreferenceBeverage(candidate: BeverageCandidate): number {
  return candidate.matchedTags.length * 300
    + candidate.beverage.level * 20
    + candidate.beverage.price
    + Math.min(candidate.ownedQuantity, 20);
}

function scoreWackyPair(food: FoodCandidate, beverage: BeverageCandidate, targetTags: readonly string[]): number {
  const matchCount = food.matchedPositiveTags.length + beverage.matchedTags.length;
  const targetScore = targetTags.length > 0 && hasMatchingSpecialBusinessTag(food.activeTags, targetTags) ? 20000 : 0;
  return targetScore
    + matchCount * 1200
    - food.matchedNegativeTags.length * 8000
    + scoreWackyFood(food, targetTags)
    + scorePreferenceBeverage(beverage);
}

function scoreKoishiBodyFood(candidate: FoodCandidate): number {
  return candidate.matchedPositiveTags.length * 4000
    - candidate.matchedNegativeTags.length * 20000
    + candidate.recipe.level * 120
    + candidate.recipe.price
    + candidate.extraIngredients.length * 40
    - candidate.resourcePressure;
}

function scoreKoishiBodyBeverage(candidate: BeverageCandidate): number {
  return candidate.matchedTags.length * 3500
    + candidate.beverage.level * 100
    + candidate.beverage.price
    + Math.min(candidate.ownedQuantity, 20);
}

function scoreKoishiBodyPair(food: FoodCandidate, beverage: BeverageCandidate): number {
  return food.matchedPositiveTags.length * 6000
    + beverage.matchedTags.length * 5000
    + scoreKoishiBodyFood(food)
    + scoreKoishiBodyBeverage(beverage);
}

function scoreKoishiBrokenShieldFood(candidate: FoodCandidate): number {
  return candidate.matchedPositiveTags.length * 10_000
    + candidate.recipe.level * 500
    - candidate.resourcePressure;
}

function scoreKoishiBrokenShieldBeverage(candidate: BeverageCandidate): number {
  return candidate.matchedTags.length * 10_000
    + candidate.beverage.level * 500
    - candidate.beverage.price
    + Math.min(candidate.ownedQuantity, 20);
}

function scoreKoishiBrokenShieldPair(
  food: FoodCandidate,
  beverage: BeverageCandidate,
  remainingBudget: number | null,
  remainingScore: number | null,
  remainingOrderCount: number | null,
): number {
  return scoreKoishiBrokenShieldCandidatePair(food, beverage, {
    remainingBudget,
    remainingScore,
    remainingOrderCount,
  });
}

function estimateKoishiBrokenShieldPairDamageLevel(food: FoodCandidate, beverage: BeverageCandidate): number {
  return estimateKoishiBrokenShieldDamageLevel({
    foodLevel: food.recipe.level,
    beverageLevel: beverage.beverage.level,
    negativeMatches: food.matchedNegativeTags.length,
  });
}

function estimateKoishiBrokenShieldPairScore(food: FoodCandidate, beverage: BeverageCandidate): number {
  return estimateKoishiBrokenShieldScore({
    meetsRequiredFood: food.meetsRequiredFood,
    meetsRequiredBeverage: beverage.meetsRequiredBeverage,
    preferenceMatches: food.matchedPositiveTags.length + beverage.matchedTags.length,
    negativeMatches: food.matchedNegativeTags.length,
  });
}

function estimateKoishiBrokenShieldPairFeedScore(food: FoodCandidate, beverage: BeverageCandidate): number {
  const estimatedPrice = estimateOrderPrice(food, beverage);
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

function getNormalOrderRemainingBudget(order: NormalBusinessOrder): number | null {
  if (!Number.isFinite(order.fund)) return null;
  return Math.max(0, Math.trunc(order.fund ?? 0));
}

function getNormalOrderRemainingOrderCount(order: NormalBusinessOrder): number | null {
  if (!Number.isFinite(order.remainingOrderCount)) return null;
  return Math.max(0, Math.trunc(order.remainingOrderCount ?? 0));
}

function buildKoishiBrokenShieldBudgetReason(
  estimatedPrice: number,
  remainingBudget: number | null,
  levelHint: number,
  estimatedScore: number,
  estimatedFeedScore: number,
  remainingScore: number | null,
  remainingOrderCount: number | null,
): string {
  const planning = buildKoishiFeedPlanningInfo({ remainingScore, remainingBudget, remainingOrderCount });
  const scoreText = remainingScore == null
    ? `等级参考 ${levelHint}，评价参考 ${estimatedScore}，投食分估算 ${estimatedFeedScore}`
    : `等级参考 ${levelHint}，评价参考 ${estimatedScore}，投食分估算 ${estimatedFeedScore} / 还需 ${remainingScore}`;
  const budgetText = remainingBudget == null
    ? `预算未知，预计花费 ${estimatedPrice}`
    : `预计花费 ${estimatedPrice} / 当前预算 ${remainingBudget}`;
  const planText = planning.attemptsRemaining == null || planning.requiredScoreThisOrder == null
    ? ''
    : `，剩余提交 ${planning.attemptsRemaining} 次，本轮至少 ${planning.requiredScoreThisOrder} 分`;
  const riskText = remainingScore != null && remainingBudget != null && estimatedFeedScore > 0 && !isKoishiFeedPlanSustainable({
    estimatedPrice,
    estimatedFeedScore,
    remainingBudget,
    remainingScore,
    remainingOrderCount,
  })
    ? '，该组合偏贵，仅在没有更省预算方案时使用'
    : '';
  return `，${scoreText}，${budgetText}${planText}${riskText}`;
}

function scoreWackyExactFood(candidate: FoodCandidate, targetTags: readonly string[]): number {
  const targetScore = targetTags.length > 0 && hasMatchingSpecialBusinessTag(candidate.activeTags, targetTags) ? 10000 : 0;
  return targetScore
    + estimateWackyExactEvaluationScore(candidate, null) * 1000
    + candidate.recipe.level * 40
    + candidate.recipe.price
    - candidate.resourcePressure;
}

function scoreWackyExactPair(food: FoodCandidate, beverage: BeverageCandidate): number {
  return estimateWackyExactEvaluationScore(food, beverage) * 2000
    + scoreWackyExactFood(food, [])
    + scorePreferenceBeverage(beverage);
}

function isWackyHighEvaluationPair(food: FoodCandidate, beverage: BeverageCandidate): boolean {
  return food.matchedNegativeTags.length === 0
    && food.matchedPositiveTags.length + beverage.matchedTags.length >= 2
    && estimateWackyExactEvaluationScore(food, beverage) >= 4;
}

function estimateWackyExactEvaluationScore(food: FoodCandidate, beverage: BeverageCandidate | null): number {
  return 2
    + food.matchedPositiveTags.length
    + (beverage?.matchedTags.length ?? 0)
    - food.matchedNegativeTags.length * 2;
}

function isWackyHighEvaluationPhase(phase: string | null | undefined): boolean {
  return isPhaseTwoContext(phase) || isPhaseThreeContext(phase);
}
