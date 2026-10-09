import {
  buildExecutionTarget,
  buildNormalTargetRuntimeContext,
  buildSyntheticDemand,
  emptySelection,
  findOrderRecipe,
  hasNoHardFailures,
  selectBestPair,
} from '@/companion/domain/special-business/normal-targets/shared';
import {
  evaluateYuyukoNormalOrderPair,
  type YuyukoNormalOrderModifierPreferences,
  YUYUKO_EXGOOD_EVALUATION_SCORE,
  YUYUKO_GOOD_EVALUATION_SCORE,
} from '@/companion/domain/special-business/yuyuko-challenge';
import {
  isPhaseThreeContext,
  RETAKE_YUYUKO_CHALLENGE_TYPE,
  STORY_YUYUKO_CHALLENGE_TYPE,
  YUYUKO_CHALLENGE_TYPES,
} from '@/companion/domain/special-business/rules';
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
import type { RareCustomerCatalogItem } from '@/lib/catalog-types';
import { cappedInventoryQuantityRank } from '@/lib/inventory-quantity';

const YUYUKO_REFRESH_EVALUATION_SCORE = 2;
const YUYUKO_CHARACTER_ID = 23;

interface YuyukoNormalExecutionTargetInput {
  order: SpecialBusinessNormalTargetArgs['order'];
  challengeType: string;
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null;
  context: RecommendationRuntimeContext;
  data: RecommendationDataSet;
}

interface YuyukoNormalCandidateCounts {
  searchedFood: number;
  baseFood: number;
  executableFood: number;
  searchedBeverage: number;
  executableBeverage: number;
}

export function selectYuyukoNormalExecutionTarget({
  order,
  specialBusiness,
  runtime,
  preferences,
  data = DEFAULT_RECOMMENDATION_DATA,
}: SpecialBusinessNormalTargetArgs): SpecialBusinessNormalTargetSelection {
  if (!specialBusiness?.active || !isPhaseThreeContext(specialBusiness.phase)) return emptySelection();
  if (!YUYUKO_CHALLENGE_TYPES.has(specialBusiness.challengeType)) return emptySelection();
  if (!runtime || data.source !== 'runtime') {
    return { target: null, message: '幽幽子第三阶段等待运行时推荐数据后再选择高评价执行目标。' };
  }

  const context = buildNormalTargetRuntimeContext(runtime, preferences, data);
  if (!context) {
    return { target: null, message: '幽幽子第三阶段缺少完整库存、厨具或菜单运行时数据，暂不选择自动化执行目标。' };
  }

  return selectStrictOriginalOrderExecutionTarget({
    order,
    challengeType: specialBusiness.challengeType,
    modifierPreferences: resolveYuyukoNormalOrderModifierPreferences(
      specialBusiness.challengeType,
      data,
    ),
    context,
    data,
  });
}

function selectStrictOriginalOrderExecutionTarget({
  order,
  challengeType,
  modifierPreferences,
  context,
  data,
}: YuyukoNormalExecutionTargetInput): SpecialBusinessNormalTargetSelection {
  if (challengeType === RETAKE_YUYUKO_CHALLENGE_TYPE && !modifierPreferences) {
    return {
      target: null,
      message: `幽幽子重修第三阶段缺少运行时 characterId=${YUYUKO_CHARACTER_ID} 的料理喜好/厌恶档案，暂不处理分身精确订单。`,
    };
  }

  const originalRecipe = findOrderRecipe(order, data);
  if (!originalRecipe) {
    return { target: null, message: `幽幽子第三阶段无法找到 ${order.foodName || `料理 #${order.foodId}`} 的配方数据。` };
  }

  const originalBeverage = data.beverages.find((beverage) => beverage.id === order.beverageId);
  if (!originalBeverage) {
    return { target: null, message: `幽幽子第三阶段无法找到 ${order.beverageName || `酒水 #${order.beverageId}`} 的酒水数据。` };
  }

  const searchCustomer = buildYuyukoNormalOrderSearchCustomer(
    modifierPreferences,
    originalRecipe.positiveTags,
  );
  const demand = buildSyntheticDemand(searchCustomer, '', '');
  const exactOrderData: RecommendationDataSet = {
    ...data,
    recipes: [originalRecipe],
    beverages: [originalBeverage],
  };
  const searchContext = challengeType === STORY_YUYUKO_CHALLENGE_TYPE
    ? { ...context, maxExtraIngredients: 0 }
    : context;
  const searchedFoodCandidates = buildRareFoodCandidates(
    exactOrderData,
    demand,
    searchContext,
  );
  const baseFoodCandidates = challengeType === RETAKE_YUYUKO_CHALLENGE_TYPE
    ? buildRareFoodCandidates(
        exactOrderData,
        demand,
        { ...context, maxExtraIngredients: 0 },
      )
    : searchedFoodCandidates;
  const foodCandidates = mergeYuyukoFoodCandidates(
    searchedFoodCandidates,
    baseFoodCandidates,
  ).filter(hasNoHardFailures);
  const searchedBeverageCandidates = buildRareBeverageCandidates(
    exactOrderData,
    demand,
    context,
  );
  const preferredBeverages = searchedBeverageCandidates.filter(hasNoHardFailures);
  const candidateCounts: YuyukoNormalCandidateCounts = {
    searchedFood: searchedFoodCandidates.length,
    baseFood: baseFoodCandidates.length,
    executableFood: foodCandidates.length,
    searchedBeverage: searchedBeverageCandidates.length,
    executableBeverage: preferredBeverages.length,
  };
  const progressTarget = selectBestPair({
    foodCandidates,
    beverageCandidates: preferredBeverages,
    scoreFood: (food) => scoreYuyukoFood(
      food,
      challengeType,
      modifierPreferences,
      preferredBeverages[0],
    ),
    scoreBeverage: (beverage) => scoreYuyukoBeverage(beverage, challengeType),
    scorePair: (food, beverage) => (
      isYuyukoNormalProgressPair(challengeType, modifierPreferences, food, beverage)
        ? scoreYuyukoExactNormalPair(challengeType, modifierPreferences, food, beverage)
        : Number.NEGATIVE_INFINITY
    ),
  });
  if (
    progressTarget
    && isYuyukoNormalProgressPair(
      challengeType,
      modifierPreferences,
      progressTarget.food,
      progressTarget.beverage,
    )
  ) {
    const evaluation = evaluateYuyukoNormalOrderPair(
      challengeType,
      progressTarget.food,
      progressTarget.beverage,
      modifierPreferences,
    );
    return {
      target: buildExecutionTarget(
        order,
        progressTarget.food,
        progressTarget.beverage,
        buildYuyukoNormalTargetReason(
          challengeType,
          modifierPreferences,
          progressTarget.food,
          progressTarget.beverage,
          'progress',
        ),
        {
          executionMode: 'progress',
          expectedFoodModifierTags: evaluation.effectiveModifierTags,
        },
      ),
      message: '',
    };
  }

  const refreshTarget = selectBestPair({
    foodCandidates,
    beverageCandidates: preferredBeverages,
    scoreFood: (food) => scoreYuyukoFood(
      food,
      challengeType,
      modifierPreferences,
      preferredBeverages[0],
    ),
    scoreBeverage: (beverage) => scoreYuyukoBeverage(beverage, challengeType),
    scorePair: (food, beverage) => (
      isYuyukoRefreshEvaluationPair(challengeType, modifierPreferences, food, beverage)
        ? scoreYuyukoExactNormalPair(challengeType, modifierPreferences, food, beverage)
        : Number.NEGATIVE_INFINITY
    ),
  });
  if (
    refreshTarget
    && isYuyukoRefreshEvaluationPair(
      challengeType,
      modifierPreferences,
      refreshTarget.food,
      refreshTarget.beverage,
    )
  ) {
    const evaluation = evaluateYuyukoNormalOrderPair(
      challengeType,
      refreshTarget.food,
      refreshTarget.beverage,
      modifierPreferences,
    );
    return {
      target: buildExecutionTarget(
        order,
        refreshTarget.food,
        refreshTarget.beverage,
        buildYuyukoNormalTargetReason(
          challengeType,
          modifierPreferences,
          refreshTarget.food,
          refreshTarget.beverage,
          'refresh',
        ),
        {
          executionMode: 'refresh',
          expectedFoodModifierTags: evaluation.effectiveModifierTags,
        },
      ),
      message: '',
    };
  }

  return {
    target: null,
    message: buildYuyukoNormalBlockMessage({
      order,
      challengeType,
      modifierPreferences,
      originalRecipeName: originalRecipe.name,
      foodCandidates,
      beverageCandidates: preferredBeverages,
      candidateCounts,
    }),
  };
}

function resolveYuyukoNormalOrderModifierPreferences(
  challengeType: string,
  data: RecommendationDataSet,
): YuyukoNormalOrderModifierPreferences | null {
  if (challengeType !== RETAKE_YUYUKO_CHALLENGE_TYPE) return null;
  const profile = data.rareCustomerProfiles.find((candidate) => candidate.id === YUYUKO_CHARACTER_ID);
  if (!profile) return null;
  return {
    positiveTags: profile.positiveTags,
    negativeTags: profile.negativeTags,
  };
}

function buildYuyukoNormalOrderSearchCustomer(
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
  recipeBaseTags: readonly string[],
): RareCustomerCatalogItem {
  const baseTags = new Set(recipeBaseTags);
  return {
    id: YUYUKO_CHARACTER_ID,
    name: '幽幽子第三阶段精确订单',
    description: '',
    dlc: 0,
    places: [],
    price: [0, 0],
    enduranceLimit: 1,
    positiveTags: (modifierPreferences?.positiveTags ?? [])
      .filter((tag) => !baseTags.has(tag)),
    negativeTags: (modifierPreferences?.negativeTags ?? [])
      .filter((tag) => !baseTags.has(tag)),
    beverageTags: [],
    collection: false,
    evaluation: {},
    spellCards: { positive: [], negative: [] },
  };
}

function mergeYuyukoFoodCandidates(
  ...candidateGroups: FoodCandidate[][]
): FoodCandidate[] {
  const candidatesByKey = new Map<string, FoodCandidate>();
  for (const candidate of candidateGroups.flat()) {
    const extraIngredientIds = candidate.extraIngredients
      .map((ingredient) => ingredient.id)
      .sort((left, right) => left - right);
    candidatesByKey.set(
      `${candidate.recipe.id}:${extraIngredientIds.join(',')}`,
      candidate,
    );
  }
  return [...candidatesByKey.values()];
}

function isYuyukoNormalProgressPair(
  challengeType: string,
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): boolean {
  const evaluation = evaluateYuyukoNormalOrderPair(
    challengeType,
    food,
    beverage,
    modifierPreferences,
  );
  return evaluation.mode !== 'unsupported'
    && evaluation.evaluationScore >= YUYUKO_GOOD_EVALUATION_SCORE;
}

function isYuyukoRefreshEvaluationPair(
  challengeType: string,
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): boolean {
  const evaluation = evaluateYuyukoNormalOrderPair(
    challengeType,
    food,
    beverage,
    modifierPreferences,
  );
  return evaluation.mode !== 'unsupported'
    && evaluation.evaluationScore >= YUYUKO_REFRESH_EVALUATION_SCORE
    && evaluation.evaluationScore < YUYUKO_GOOD_EVALUATION_SCORE;
}

function buildYuyukoNormalTargetReason(
  challengeType: string,
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
  food: FoodCandidate,
  beverage: BeverageCandidate,
  executionMode: 'progress' | 'refresh',
): string {
  const evaluation = evaluateYuyukoNormalOrderPair(
    challengeType,
    food,
    beverage,
    modifierPreferences,
  );
  const evaluationText = formatYuyukoEvaluationScore(evaluation.evaluationScore);
  const actionText = executionMode === 'progress'
    ? '用于推进挑战进度'
    : '不推进进度，仅清理当前普客订单';

  if (evaluation.mode === 'story-level-sum') {
    return [
      `幽幽子剧情版三阶段${executionMode === 'progress' ? '执行' : '清理'}方案：预计 ${evaluationText}`,
      actionText,
      `料理 Lv.${food.recipe.level}`,
      `酒水 Lv.${beverage.beverage.level}`,
      `等级合计 ${evaluation.levelSum}`,
    ].join('，');
  }

  return [
    `幽幽子重修三阶段${executionMode === 'progress' ? '执行' : '清理'}方案：预计 ${evaluationText}`,
    actionText,
    '原生普通评价基准 Normal',
    formatYuyukoModifierTags('生效修饰 Tag', evaluation.effectiveModifierTags),
    formatYuyukoModifierTags('喜好修饰', evaluation.positiveModifierTags),
    formatYuyukoModifierTags('厌恶修饰', evaluation.negativeModifierTags),
  ].join('，');
}

function scoreYuyukoFood(
  candidate: FoodCandidate,
  challengeType: string,
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
  beverage: BeverageCandidate | undefined,
): number {
  if (beverage) {
    return scoreYuyukoExactNormalPair(
      challengeType,
      modifierPreferences,
      candidate,
      beverage,
    );
  }
  return (challengeType === STORY_YUYUKO_CHALLENGE_TYPE ? candidate.recipe.level * 1_000 : 0)
    + candidate.recipe.price
    - candidate.resourcePressure
    - candidate.extraIngredients.length;
}

function scoreYuyukoBeverage(candidate: BeverageCandidate, challengeType: string): number {
  return (challengeType === STORY_YUYUKO_CHALLENGE_TYPE ? candidate.beverage.level * 1_000 : 0)
    + candidate.beverage.price
    + cappedInventoryQuantityRank(candidate.ownedQuantity, 20);
}

function scoreYuyukoExactNormalPair(
  challengeType: string,
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
  food: FoodCandidate,
  beverage: BeverageCandidate,
): number {
  const evaluation = evaluateYuyukoNormalOrderPair(
    challengeType,
    food,
    beverage,
    modifierPreferences,
  );
  const modeTieBreak = evaluation.mode === 'story-level-sum'
    ? evaluation.levelSum * 1_000_000
    : evaluation.positiveModifierTags.length * 100_000
      - evaluation.negativeModifierTags.length * 1_000_000;
  return evaluation.evaluationScore * 10_000_000
    + modeTieBreak
    + Math.min(food.recipe.price + beverage.beverage.price, 999) * 10
    + cappedInventoryQuantityRank(beverage.ownedQuantity, 99)
    - Math.ceil(food.resourcePressure * 100)
    - food.extraIngredients.length;
}

function buildYuyukoNormalBlockMessage({
  order,
  challengeType,
  modifierPreferences,
  originalRecipeName,
  foodCandidates,
  beverageCandidates,
  candidateCounts,
}: {
  order: YuyukoNormalExecutionTargetInput['order'];
  challengeType: string;
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null;
  originalRecipeName: string;
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

  const diagnosticPair = selectDiagnosticPair(
    challengeType,
    modifierPreferences,
    foodCandidates,
    beverageCandidates,
  );
  if (diagnosticPair) {
    const { food, beverage } = diagnosticPair;
    const evaluation = evaluateYuyukoNormalOrderPair(
      challengeType,
      food,
      beverage,
      modifierPreferences,
    );
    if (evaluation.negativeModifierTags.length > 0) {
      details.push(`生效料理修饰包含幽幽子厌恶 Tag ${evaluation.negativeModifierTags.join('、')}`);
    }

    if (evaluation.evaluationScore < YUYUKO_GOOD_EVALUATION_SCORE) {
      const evaluationEvidence = evaluation.mode === 'story-level-sum'
        ? `料理 Lv.${food.recipe.level}，酒水 Lv.${beverage.beverage.level}，等级合计 ${evaluation.levelSum}`
        : [
            '原生普通评价基准 Normal',
            formatYuyukoModifierTags('生效修饰 Tag', evaluation.effectiveModifierTags),
            formatYuyukoModifierTags('喜好修饰', evaluation.positiveModifierTags),
            formatYuyukoModifierTags('厌恶修饰', evaluation.negativeModifierTags),
          ].join('，');
      details.push(
        `预计${formatYuyukoEvaluationScore(evaluation.evaluationScore)}，未达满意（Good）/完美（ExGood）`
        + `（${evaluationEvidence}）`,
      );
    }

  }

  if (details.length === 0) details.push('未找到可预测推进进度或安全清理的原订单料理/酒水组合');

  return [
    `幽幽子第三阶段原订单 ${originalRecipeName} / ${order.beverageName || `#${order.beverageId}`} 暂不能推进或安全清理`,
    details.join('；'),
    `候选统计：原料理搜索 ${candidateCounts.searchedFood}、无加料 ${candidateCounts.baseFood}、可执行 ${candidateCounts.executableFood}，原酒水搜索 ${candidateCounts.searchedBeverage}、可执行 ${candidateCounts.executableBeverage}`,
  ].join('；');
}

function selectDiagnosticPair(
  challengeType: string,
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
  foodCandidates: FoodCandidate[],
  beverageCandidates: BeverageCandidate[],
): { food: FoodCandidate; beverage: BeverageCandidate } | null {
  const firstBeverage = beverageCandidates[0];
  const food = [...foodCandidates].sort((left, right) => (
    scoreYuyukoFood(right, challengeType, modifierPreferences, firstBeverage)
      - scoreYuyukoFood(left, challengeType, modifierPreferences, firstBeverage)
  ))[0];
  const beverage = [...beverageCandidates].sort((left, right) => (
    scoreYuyukoBeverage(right, challengeType) - scoreYuyukoBeverage(left, challengeType)
  ))[0];
  if (!food || !beverage) return null;
  return { food, beverage };
}

function formatYuyukoModifierTags(label: string, tags: readonly string[]): string {
  return tags.length > 0 ? `${label} ${tags.join('、')}` : `${label} 无`;
}

function formatYuyukoEvaluationScore(score: number): string {
  if (score >= YUYUKO_EXGOOD_EVALUATION_SCORE) return '完美（ExGood）';
  if (score >= YUYUKO_GOOD_EVALUATION_SCORE) return '满意（Good）';
  if (score >= 2) return '普通（Normal）';
  return '未形成可推进评价';
}
