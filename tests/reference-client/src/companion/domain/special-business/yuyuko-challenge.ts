import type { SpecialBusinessContext } from '@/companion/types';
import {
  isPhaseThreeContext,
  RETAKE_YUYUKO_CHALLENGE_TYPE,
  STORY_YUYUKO_CHALLENGE_TYPE,
  YUYUKO_CHALLENGE_TYPES,
} from '@/companion/domain/special-business/rules/shared';
import {
  type BeverageCandidate,
  type FoodCandidate,
  type RareOrderRecommendationPlan,
} from '@/recommendation-engine';
import type { YuyukoProgressEvaluationMode } from '@/companion/domain/special-business/rules/types';
import { evaluateYuyukoTagOrderPair } from '@/companion/domain/special-business/yuyuko-positive-spell';
import { cappedInventoryQuantityRank } from '@/lib/inventory-quantity';

export const YUYUKO_GOOD_EVALUATION_SCORE = 3;
export const YUYUKO_EXGOOD_EVALUATION_SCORE = 4;
export const YUYUKO_GOOD_LEVEL_SUM = 5;
export const YUYUKO_EXGOOD_LEVEL_SUM = 8;

const YUYUKO_NORMAL_EVALUATION_SCORE = 2;

export type YuyukoNormalOrderEvaluationMode =
  | 'story-level-sum'
  | 'retake-food-modifiers'
  | 'unsupported';

export interface YuyukoNormalOrderEvaluation {
  mode: YuyukoNormalOrderEvaluationMode;
  evaluationScore: number;
  levelSum: number;
  effectiveModifierTags: string[];
  positiveModifierTags: string[];
  negativeModifierTags: string[];
}

export interface YuyukoNormalOrderModifierPreferences {
  positiveTags: readonly string[];
  negativeTags: readonly string[];
}

export interface YuyukoRareOrderEvaluation {
  mode: YuyukoProgressEvaluationMode;
  evaluationScore: number;
  levelSum: number;
  baseDemandScore: number;
  extraPreferenceScore: number;
  foodExtraPreferenceTags: string[];
  beverageExtraPreferenceTags: string[];
  negativeTags: string[];
  canProgress: boolean;
}

export function isYuyukoPhaseThreeContext(
  specialBusiness: SpecialBusinessContext | null | undefined,
): boolean {
  return specialBusiness?.active === true
    && YUYUKO_CHALLENGE_TYPES.has(specialBusiness.challengeType)
    && isPhaseThreeContext(specialBusiness.phase);
}

/**
 * 估算幽幽子第三阶段普通形态订单的原生评价。
 *
 * 剧情版回调只读取料理和酒水等级和；重修版分身先取得 Normal，
 * 再只用实际生效的料理修饰 Tag 按幽幽子偏好逐项增减评价。
 */
export function evaluateYuyukoNormalOrderPair(
  challengeType: string,
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
  modifierPreferences: YuyukoNormalOrderModifierPreferences | null,
): YuyukoNormalOrderEvaluation {
  const levelSum = getYuyukoLevelSum(food, beverage);
  if (challengeType === STORY_YUYUKO_CHALLENGE_TYPE) {
    return {
      mode: 'story-level-sum',
      evaluationScore: food && beverage ? estimateYuyukoStoryLevelEvaluationScore(levelSum) : 0,
      levelSum,
      effectiveModifierTags: [],
      positiveModifierTags: [],
      negativeModifierTags: [],
    };
  }

  if (challengeType === RETAKE_YUYUKO_CHALLENGE_TYPE) {
    if (!food || !beverage || !modifierPreferences) {
      return {
        mode: modifierPreferences ? 'retake-food-modifiers' : 'unsupported',
        evaluationScore: 0,
        levelSum,
        effectiveModifierTags: [],
        positiveModifierTags: [],
        negativeModifierTags: [],
      };
    }

    const effectiveModifierTags = getEffectiveYuyukoNormalOrderModifierTags(food);
    const positiveModifierTags = intersectTags(
      effectiveModifierTags,
      modifierPreferences.positiveTags,
    );
    const negativeModifierTags = intersectTags(
      effectiveModifierTags,
      modifierPreferences.negativeTags,
    );
    return {
      mode: 'retake-food-modifiers',
      evaluationScore: clampYuyukoEvaluationScore(
        YUYUKO_NORMAL_EVALUATION_SCORE
          + positiveModifierTags.length
          - negativeModifierTags.length,
      ),
      levelSum,
      effectiveModifierTags,
      positiveModifierTags,
      negativeModifierTags,
    };
  }

  return {
    mode: 'unsupported',
    evaluationScore: 0,
    levelSum,
    effectiveModifierTags: [],
    positiveModifierTags: [],
    negativeModifierTags: [],
  };
}

export function evaluateYuyukoRareOrderPair(
  mode: YuyukoProgressEvaluationMode,
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
  demand: RareOrderRecommendationPlan['demand'],
): YuyukoRareOrderEvaluation {
  const levelSum = getYuyukoLevelSum(food, beverage);
  const baseDemandScore = Number(food?.meetsRequiredFood === true)
    + Number(beverage?.meetsRequiredBeverage === true);
  if (mode === 'story-level-sum') {
    const evaluationScore = food && beverage
      ? estimateYuyukoStoryLevelEvaluationScore(levelSum)
      : 0;
    return {
      mode,
      evaluationScore,
      levelSum,
      baseDemandScore,
      extraPreferenceScore: 0,
      foodExtraPreferenceTags: [],
      beverageExtraPreferenceTags: [],
      negativeTags: [],
      canProgress: baseDemandScore === 2
        && evaluationScore >= YUYUKO_GOOD_EVALUATION_SCORE,
    };
  }

  if (mode === 'retake-tag-order') {
    const tagEvaluation = evaluateYuyukoTagOrderPair(food, beverage, demand);
    const evaluationScore = clampYuyukoEvaluationScore(
      tagEvaluation.evaluationScore - tagEvaluation.negativeTags.length,
    );
    return {
      mode,
      evaluationScore,
      levelSum,
      baseDemandScore: tagEvaluation.baseDemandScore,
      extraPreferenceScore: tagEvaluation.extraPreferenceScore,
      foodExtraPreferenceTags: tagEvaluation.foodExtraPreferenceTags,
      beverageExtraPreferenceTags: tagEvaluation.beverageExtraPreferenceTags,
      negativeTags: tagEvaluation.negativeTags,
      canProgress: food != null
        && beverage != null
        && tagEvaluation.baseDemandScore === 2
        && tagEvaluation.negativeTags.length === 0
        && evaluationScore >= YUYUKO_GOOD_EVALUATION_SCORE,
    };
  }

  return {
    mode,
    evaluationScore: 0,
    levelSum,
    baseDemandScore,
    extraPreferenceScore: 0,
    foodExtraPreferenceTags: [],
    beverageExtraPreferenceTags: [],
    negativeTags: [],
    canProgress: false,
  };
}

export function getYuyukoLevelSum(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): number {
  return Math.max(0, food?.recipe.level ?? 0) + Math.max(0, beverage?.beverage.level ?? 0);
}

export function isYuyukoProgressPlan(
  plan: RareOrderRecommendationPlan,
  mode: YuyukoProgressEvaluationMode,
): boolean {
  if (plan.bucket === 'blocked') return false;
  return evaluateYuyukoRareOrderPair(mode, plan.food, plan.beverage, plan.demand).canProgress;
}

export function scoreYuyukoPair(
  mode: YuyukoProgressEvaluationMode,
  food: FoodCandidate,
  beverage: BeverageCandidate,
  demand: RareOrderRecommendationPlan['demand'],
): number {
  const evaluation = evaluateYuyukoRareOrderPair(mode, food, beverage, demand);
  const exGoodReady = evaluation.evaluationScore >= YUYUKO_EXGOOD_EVALUATION_SCORE ? 1 : 0;

  return Number(evaluation.canProgress) * 1_000_000_000
    + exGoodReady * 100_000_000
    + evaluation.evaluationScore * 10_000_000
    + (mode === 'story-level-sum' ? evaluation.levelSum : evaluation.extraPreferenceScore) * 1_000_000
    + evaluation.baseDemandScore * 100_000
    + Math.min(food.recipe.price + beverage.beverage.price, 999) * 10
    + cappedInventoryQuantityRank(beverage.ownedQuantity, 99)
    - evaluation.negativeTags.length * 100_000_000
    - Math.ceil(food.resourcePressure * 100)
    - food.extraIngredients.length;
}

export function compareYuyukoPlans(
  left: RareOrderRecommendationPlan,
  right: RareOrderRecommendationPlan,
  mode: YuyukoProgressEvaluationMode,
): number {
  const leftScore = scoreYuyukoPlan(left, mode);
  const rightScore = scoreYuyukoPlan(right, mode);
  if (leftScore !== rightScore) return rightScore - leftScore;
  return left.estimatedPrice - right.estimatedPrice;
}

export function buildYuyukoPlanReason(
  plan: RareOrderRecommendationPlan,
  mode: YuyukoProgressEvaluationMode,
): string {
  return buildYuyukoReasonCore(plan, mode, '幽幽子三阶段');
}

export function buildYuyukoProgressBlockedMessages(
  plans: readonly RareOrderRecommendationPlan[],
  mode: YuyukoProgressEvaluationMode,
  limit = 3,
): string[] {
  const messages = [...plans]
    .filter((plan) => !isYuyukoProgressPlan(plan, mode))
    .sort((left, right) => compareYuyukoPlans(left, right, mode))
    .map((plan) => buildYuyukoProgressBlockReason(plan, mode))
    .filter((message): message is string => Boolean(message));
  const uniqueMessages = uniqueTags(messages);
  if (uniqueMessages.length > 0) return uniqueMessages.slice(0, Math.max(1, limit));
  if (plans.length === 0) return [];
  return ['幽幽子第三阶段没有可预测推进进度的执行方案。'];
}

export function buildYuyukoProgressBlockReason(
  plan: RareOrderRecommendationPlan,
  mode: YuyukoProgressEvaluationMode,
): string | null {
  if (isYuyukoProgressPlan(plan, mode)) return null;
  const details = buildYuyukoProgressBlockDetails(plan, mode);
  if (details.length === 0) return null;
  return `${formatYuyukoPlanTarget(plan)}：${details.join('；')}`;
}

function scoreYuyukoPlan(
  plan: RareOrderRecommendationPlan,
  mode: YuyukoProgressEvaluationMode,
): number {
  if (!plan.food || !plan.beverage || plan.bucket === 'blocked') return Number.NEGATIVE_INFINITY;
  return scoreYuyukoPair(mode, plan.food, plan.beverage, plan.demand);
}

function buildYuyukoProgressBlockDetails(
  plan: RareOrderRecommendationPlan,
  mode: YuyukoProgressEvaluationMode,
): string[] {
  const food = plan.food;
  const beverage = plan.beverage;
  const evaluation = evaluateYuyukoRareOrderPair(mode, food, beverage, plan.demand);
  const details: string[] = [];

  if (!food) {
    details.push('缺少料理候选');
  } else {
    if (!food.meetsRequiredFood) details.push(`料理未满足点单 ${plan.demand.requiredFoodTag || '未知'}`);
    if (evaluation.negativeTags.length > 0) {
      details.push(`包含当前稀客厌恶 Tag ${evaluation.negativeTags.join('、')}`);
    }
  }

  if (!beverage) {
    details.push('缺少酒水候选');
  } else if (!beverage.meetsRequiredBeverage) {
    details.push(`酒水未满足点单 ${plan.demand.requiredBeverageTag || '未知'}`);
  }

  if (food && beverage) {
    if (evaluation.evaluationScore < YUYUKO_GOOD_EVALUATION_SCORE) {
      const evidence = mode === 'story-level-sum'
        ? `等级合计 ${evaluation.levelSum}`
        : `点单基础 ${evaluation.baseDemandScore}，额外喜好 ${evaluation.extraPreferenceScore}`;
      details.push(`预计${formatYuyukoEvaluationScore(evaluation.evaluationScore)}，未达满意（Good）/完美（ExGood）（${evidence}）`);
    }
  }

  for (const result of plan.conditionResults) {
    if (result.status === 'fail' && result.severity === 'hard') details.push(result.detail);
  }
  if (details.length === 0 && plan.bucket === 'blocked') details.push('组合被推荐引擎标记为不可执行');
  return uniqueTags(details);
}

function formatYuyukoPlanTarget(plan: RareOrderRecommendationPlan): string {
  const foodText = plan.food
    ? `${plan.food.recipe.name}#${plan.food.recipe.id}${formatExtraIngredientIds(plan.food.extraIngredients.map((ingredient) => ingredient.id))}`
    : '无料理';
  const beverageText = plan.beverage
    ? `${plan.beverage.beverage.name}#${plan.beverage.beverage.id}`
    : '无酒水';
  return `${foodText} / ${beverageText}`;
}

function formatExtraIngredientIds(ids: number[]): string {
  if (ids.length === 0) return '';
  return `+${ids.join(',')}`;
}

function formatYuyukoEvaluationScore(score: number): string {
  if (score >= YUYUKO_EXGOOD_EVALUATION_SCORE) return '完美（ExGood）';
  if (score >= YUYUKO_GOOD_EVALUATION_SCORE) return '满意（Good）';
  if (score >= 2) return '普通（Normal）';
  return '未形成可推进评价';
}

function buildYuyukoReasonCore(
  plan: RareOrderRecommendationPlan,
  mode: YuyukoProgressEvaluationMode,
  prefix: string,
): string {
  const evaluation = evaluateYuyukoRareOrderPair(mode, plan.food, plan.beverage, plan.demand);
  const evaluationText = evaluation.evaluationScore >= YUYUKO_EXGOOD_EVALUATION_SCORE
    ? '完美（ExGood）'
    : evaluation.evaluationScore >= YUYUKO_GOOD_EVALUATION_SCORE
      ? '满意（Good）'
      : '未达稳定推进评价';
  if (mode === 'story-level-sum') {
    return [
      `${prefix}剧情版：预计 ${evaluationText}`,
      `推进阈值等级合计 >= ${YUYUKO_GOOD_LEVEL_SUM}`,
      `料理 Lv.${plan.food?.recipe.level ?? 0}`,
      `酒水 Lv.${plan.beverage?.beverage.level ?? 0}`,
      `等级合计 ${evaluation.levelSum}`,
    ].join('，');
  }

  const preferenceTags = [
    ...evaluation.foodExtraPreferenceTags,
    ...evaluation.beverageExtraPreferenceTags,
  ];
  return [
    `${prefix}重修版：预计 ${evaluationText}`,
    `点单基础 ${evaluation.baseDemandScore}`,
    `额外喜好 ${evaluation.extraPreferenceScore}${preferenceTags.length > 0 ? `（${preferenceTags.join('、')}）` : ''}`,
    evaluation.negativeTags.length > 0
      ? `当前稀客厌恶 ${evaluation.negativeTags.join('、')}`
      : '无当前稀客厌恶 Tag',
  ].join('，');
}

function estimateYuyukoStoryLevelEvaluationScore(levelSum: number): number {
  if (levelSum >= YUYUKO_EXGOOD_LEVEL_SUM) return YUYUKO_EXGOOD_EVALUATION_SCORE;
  if (levelSum >= YUYUKO_GOOD_LEVEL_SUM) return YUYUKO_GOOD_EVALUATION_SCORE;
  if (levelSum >= 2) return YUYUKO_NORMAL_EVALUATION_SCORE;
  return levelSum > 0 ? 1 : 0;
}

function getEffectiveYuyukoNormalOrderModifierTags(food: FoodCandidate): string[] {
  const baseTags = new Set(food.recipe.positiveTags);
  return uniqueTags([
    food.recipe.cooker,
    ...food.activeTags,
  ]).filter((tag) => !baseTags.has(tag));
}

function intersectTags(source: readonly string[], target: readonly string[]): string[] {
  const targetSet = new Set(target);
  return source.filter((tag) => targetSet.has(tag));
}

function clampYuyukoEvaluationScore(score: number): number {
  return Math.max(0, Math.min(YUYUKO_EXGOOD_EVALUATION_SCORE, score));
}

function uniqueTags(tags: string[]): string[] {
  return Array.from(new Set(tags.filter(Boolean)));
}
