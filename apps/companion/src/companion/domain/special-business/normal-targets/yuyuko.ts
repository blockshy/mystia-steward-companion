import {
  buildExecutionTarget,
  buildNormalTargetRuntimeContext,
  buildSyntheticCustomer,
  buildSyntheticDemand,
  emptySelection,
  findOrderRecipe,
  firstTag,
  hasNoHardFailures,
  selectBestPair,
} from '@/companion/domain/special-business/normal-targets/shared';
import {
  buildYuyukoTargetReason,
  estimateYuyukoEvaluationScore,
  getYuyukoLevelSum,
  getYuyukoChallengeNegativeTags,
  getYuyukoPreferenceScore,
  isYuyukoProgressEvaluationPair,
  YUYUKO_EXGOOD_EVALUATION_SCORE,
  YUYUKO_GOOD_EVALUATION_SCORE,
} from '@/companion/domain/special-business/yuyuko-challenge';
import { isPhaseThreeContext } from '@/companion/domain/special-business/rules';
import type {
  SpecialBusinessNormalTargetArgs,
  SpecialBusinessNormalTargetSelection,
} from '@/companion/domain/special-business/types';
import type {
  BeverageCandidate,
  FoodCandidate,
  RecommendationRuntimeContext,
} from '@/recommendation-engine';
import {
  buildRareBeverageCandidates,
  buildRareFoodCandidates,
} from '@/recommendation-engine';
import {
  DEFAULT_RECOMMENDATION_DATA,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';

const YUYUKO_REFRESH_EVALUATION_SCORE = 2;

interface YuyukoNormalExecutionTargetInput {
  order: SpecialBusinessNormalTargetArgs['order'];
  context: RecommendationRuntimeContext;
  data: RecommendationDataSet;
}

interface YuyukoNormalCandidateCounts {
  rawFood: number;
  executableFood: number;
  originalFood: number;
  rawBeverage: number;
  executableBeverage: number;
  originalBeverage: number;
}

export function selectYuyukoNormalExecutionTarget({
  order,
  specialBusiness,
  runtime,
  preferences,
  data = DEFAULT_RECOMMENDATION_DATA,
}: SpecialBusinessNormalTargetArgs): SpecialBusinessNormalTargetSelection {
  if (!specialBusiness?.active || !isPhaseThreeContext(specialBusiness.phase)) return emptySelection();
  if (!runtime || data.source !== 'runtime') {
    return { target: null, message: '幽幽子第三阶段等待运行时推荐数据后再选择高评价执行目标。' };
  }

  const context = buildNormalTargetRuntimeContext(runtime, preferences, data);
  if (!context) {
    return { target: null, message: '幽幽子第三阶段缺少完整库存、厨具或菜单运行时数据，暂不选择自动化执行目标。' };
  }

  return selectStrictOriginalOrderExecutionTarget({ order, context, data });
}

function selectStrictOriginalOrderExecutionTarget({
  order,
  context,
  data,
}: YuyukoNormalExecutionTargetInput): SpecialBusinessNormalTargetSelection {
  const syntheticCustomer = buildSyntheticCustomer(order, data, {
    preferRareCustomer: true,
    fallbackFoodTags: [],
  });
  const originalRecipe = findOrderRecipe(order, data);
  if (!originalRecipe) {
    return { target: null, message: `幽幽子第三阶段无法找到 ${order.foodName || `料理 #${order.foodId}`} 的配方数据。` };
  }

  const demand = buildSyntheticDemand(
    syntheticCustomer,
    firstTag(syntheticCustomer.positiveTags) || originalRecipe.positiveTags[0] || '',
    firstTag(syntheticCustomer.beverageTags),
  );
  const rawFoodCandidates = buildRareFoodCandidates(data, demand, context);
  const executableFoodCandidates = rawFoodCandidates.filter(hasNoHardFailures);
  const foodCandidates = executableFoodCandidates
    .filter((candidate) => candidate.recipe.id === originalRecipe.id);
  const rawBeverageCandidates = buildRareBeverageCandidates(data, demand, context);
  const executableBeverageCandidates = rawBeverageCandidates.filter(hasNoHardFailures);
  const preferredBeverages = executableBeverageCandidates.filter((candidate) => candidate.beverage.id === order.beverageId);
  const candidateCounts: YuyukoNormalCandidateCounts = {
    rawFood: rawFoodCandidates.length,
    executableFood: executableFoodCandidates.length,
    originalFood: foodCandidates.length,
    rawBeverage: rawBeverageCandidates.length,
    executableBeverage: executableBeverageCandidates.length,
    originalBeverage: preferredBeverages.length,
  };
  const progressTarget = selectBestPair({
    foodCandidates,
    beverageCandidates: preferredBeverages,
    scoreFood: scoreYuyukoFood,
    scoreBeverage: scoreYuyukoBeverage,
    scorePair: (food, beverage) => (
      isYuyukoProgressEvaluationPair(food, beverage)
        ? scoreYuyukoExactNormalPair(food, beverage)
        : Number.NEGATIVE_INFINITY
    ),
  });
  if (progressTarget && isYuyukoProgressEvaluationPair(progressTarget.food, progressTarget.beverage)) {
    return {
      target: buildExecutionTarget(
        order,
        progressTarget.food,
        progressTarget.beverage,
        buildYuyukoTargetReason(progressTarget.food, progressTarget.beverage),
        { executionMode: 'progress' },
      ),
      message: '',
    };
  }

  const refreshTarget = selectBestPair({
    foodCandidates,
    beverageCandidates: preferredBeverages,
    scoreFood: scoreYuyukoFood,
    scoreBeverage: scoreYuyukoBeverage,
    scorePair: (food, beverage) => (
      isYuyukoRefreshEvaluationPair(food, beverage)
        ? scoreYuyukoExactNormalPair(food, beverage)
        : Number.NEGATIVE_INFINITY
    ),
  });
  if (refreshTarget && isYuyukoRefreshEvaluationPair(refreshTarget.food, refreshTarget.beverage)) {
    return {
      target: buildExecutionTarget(
        order,
        refreshTarget.food,
        refreshTarget.beverage,
        buildYuyukoRefreshTargetReason(refreshTarget.food, refreshTarget.beverage),
        { executionMode: 'refresh' },
      ),
      message: '',
    };
  }

  return {
    target: null,
    message: buildYuyukoNormalBlockMessage({
      order,
      originalRecipeName: originalRecipe.name,
      demandFoodTag: demand.requiredFoodTag,
      demandBeverageTag: demand.requiredBeverageTag,
      foodCandidates,
      beverageCandidates: preferredBeverages,
      candidateCounts,
    }),
  };
}

function isYuyukoRefreshEvaluationPair(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): boolean {
  if (!food || !beverage) return false;
  const evaluationScore = estimateYuyukoEvaluationScore(food, beverage);
  return evaluationScore >= YUYUKO_REFRESH_EVALUATION_SCORE
    && evaluationScore < YUYUKO_GOOD_EVALUATION_SCORE;
}

function buildYuyukoRefreshTargetReason(
  food: FoodCandidate,
  beverage: BeverageCandidate,
): string {
  const evaluationScore = estimateYuyukoEvaluationScore(food, beverage);
  const negativeTags = getYuyukoChallengeNegativeTags(food);
  return [
    `幽幽子三阶段清理方案：预计 ${formatYuyukoEvaluationScore(evaluationScore)}`,
    '不推进进度，仅清理当前普客订单',
    `料理 Lv.${food.recipe.level}`,
    `酒水 Lv.${beverage.beverage.level}`,
    `等级合计 ${getYuyukoLevelSum(food, beverage)}`,
    negativeTags.length > 0 ? `厌恶 ${negativeTags.join('、')}` : '无厌恶 Tag',
  ].join('，');
}

function scoreYuyukoFood(candidate: FoodCandidate): number {
  const negativeTags = getYuyukoChallengeNegativeTags(candidate);
  return candidate.recipe.level * 1_000
    + candidate.matchedPositiveTags.length * 500
    + candidate.recipe.price
    - candidate.resourcePressure
    - negativeTags.length * 1_000_000
    - candidate.extraIngredients.length;
}

function scoreYuyukoBeverage(candidate: BeverageCandidate): number {
  return candidate.beverage.level * 1_000
    + candidate.matchedTags.length * 500
    + candidate.beverage.price
    + Math.min(candidate.ownedQuantity, 20);
}

function scoreYuyukoExactNormalPair(food: FoodCandidate, beverage: BeverageCandidate): number {
  const evaluationScore = estimateYuyukoEvaluationScore(food, beverage);
  const levelSum = getYuyukoLevelSum(food, beverage);
  const preferenceScore = getYuyukoPreferenceScore(food, beverage);
  return evaluationScore * 10_000_000
    + levelSum * 1_000_000
    + preferenceScore * 10_000
    + Math.min(food.recipe.price + beverage.beverage.price, 999) * 10
    + Math.min(beverage.ownedQuantity, 99)
    - Math.ceil(food.resourcePressure * 100)
    - food.extraIngredients.length;
}

function buildYuyukoNormalBlockMessage({
  order,
  originalRecipeName,
  demandFoodTag,
  demandBeverageTag,
  foodCandidates,
  beverageCandidates,
  candidateCounts,
}: {
  order: YuyukoNormalExecutionTargetInput['order'];
  originalRecipeName: string;
  demandFoodTag: string;
  demandBeverageTag: string;
  foodCandidates: FoodCandidate[];
  beverageCandidates: BeverageCandidate[];
  candidateCounts: YuyukoNormalCandidateCounts;
}): string {
  const details: string[] = [];
  if (foodCandidates.length === 0) {
    details.push(`原料理候选 0（可能未解锁、缺基础材料或厨具不可用）`);
  }
  if (beverageCandidates.length === 0) {
    details.push(`原酒水 ${order.beverageName || `#${order.beverageId}`} 候选 0（可能未持有、未解锁或被排除）`);
  }

  const diagnosticPair = selectDiagnosticPair(foodCandidates, beverageCandidates);
  if (diagnosticPair) {
    const { food, beverage } = diagnosticPair;
    const negativeTags = getYuyukoChallengeNegativeTags(food);
    if (negativeTags.length > 0) details.push(`原料理包含幽幽子厌恶 Tag ${negativeTags.join('、')}`);

    const evaluationScore = estimateYuyukoEvaluationScore(food, beverage);
    if (evaluationScore < YUYUKO_GOOD_EVALUATION_SCORE) {
      details.push(
        `预计${formatYuyukoEvaluationScore(evaluationScore)}，未达满意（Good）/完美（ExGood）`
        + `（料理 Lv.${food.recipe.level}，酒水 Lv.${beverage.beverage.level}，等级合计 ${getYuyukoLevelSum(food, beverage)}）`,
      );
    }

    if (!food.meetsRequiredFood && demandFoodTag) {
      details.push(`诊断：原料理未命中合成点单 Tag ${demandFoodTag}，该项不作为普客精确点单硬条件`);
    }
    if (!beverage.meetsRequiredBeverage && demandBeverageTag) {
      details.push(`诊断：原酒水未命中合成点单 Tag ${demandBeverageTag}，该项不作为普客精确点单硬条件`);
    }
  }

  if (details.length === 0) details.push('未找到可预测推进进度或安全清理的原订单料理/酒水组合');

  return [
    `幽幽子第三阶段原订单 ${originalRecipeName} / ${order.beverageName || `#${order.beverageId}`} 暂不能推进或安全清理`,
    details.join('；'),
    `候选统计：料理 ${candidateCounts.rawFood}/${candidateCounts.executableFood}/${candidateCounts.originalFood}，酒水 ${candidateCounts.rawBeverage}/${candidateCounts.executableBeverage}/${candidateCounts.originalBeverage}`,
  ].join('；');
}

function selectDiagnosticPair(
  foodCandidates: FoodCandidate[],
  beverageCandidates: BeverageCandidate[],
): { food: FoodCandidate; beverage: BeverageCandidate } | null {
  const food = [...foodCandidates].sort((left, right) => scoreYuyukoFood(right) - scoreYuyukoFood(left))[0];
  const beverage = [...beverageCandidates].sort((left, right) => scoreYuyukoBeverage(right) - scoreYuyukoBeverage(left))[0];
  if (!food || !beverage) return null;
  return { food, beverage };
}

function formatYuyukoEvaluationScore(score: number): string {
  if (score >= YUYUKO_EXGOOD_EVALUATION_SCORE) return '完美（ExGood）';
  if (score >= YUYUKO_GOOD_EVALUATION_SCORE) return '满意（Good）';
  if (score >= 2) return '普通（Normal）';
  return '未形成可推进评价';
}
