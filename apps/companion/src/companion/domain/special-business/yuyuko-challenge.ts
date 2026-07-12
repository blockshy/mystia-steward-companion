import type { SpecialBusinessContext } from '@/companion/types';
import {
  isPhaseThreeContext,
  YUYUKO_CHALLENGE_TYPES,
} from '@/companion/domain/special-business/rules/shared';
import type {
  BeverageCandidate,
  FoodCandidate,
  RareOrderRecommendationPlan,
} from '@/recommendation-engine';

export const YUYUKO_GOOD_EVALUATION_SCORE = 3;
export const YUYUKO_EXGOOD_EVALUATION_SCORE = 4;
export const YUYUKO_GOOD_LEVEL_SUM = 5;
export const YUYUKO_EXGOOD_LEVEL_SUM = 8;

const YUYUKO_CHALLENGE_FOOD_HATE_TAGS = ['素', '小巧', '清淡'] as const;
const YUYUKO_CHALLENGE_FOOD_HATE_TAG_SET = new Set<string>(YUYUKO_CHALLENGE_FOOD_HATE_TAGS);

export function isYuyukoPhaseThreeContext(
  specialBusiness: SpecialBusinessContext | null | undefined,
): boolean {
  return specialBusiness?.active === true
    && YUYUKO_CHALLENGE_TYPES.has(specialBusiness.challengeType)
    && isPhaseThreeContext(specialBusiness.phase);
}

export function estimateYuyukoEvaluationScore(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): number {
  if (!food || !beverage) return 0;
  if (getYuyukoChallengeNegativeTags(food).length > 0) return 0;

  const levelSum = getYuyukoLevelSum(food, beverage);
  if (levelSum >= YUYUKO_EXGOOD_LEVEL_SUM) return YUYUKO_EXGOOD_EVALUATION_SCORE;
  if (levelSum >= YUYUKO_GOOD_LEVEL_SUM) return YUYUKO_GOOD_EVALUATION_SCORE;
  if (levelSum >= 2) return 2;
  return levelSum > 0 ? 1 : 0;
}

export function getYuyukoLevelSum(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): number {
  return Math.max(0, food?.recipe.level ?? 0) + Math.max(0, beverage?.beverage.level ?? 0);
}

export function getYuyukoPreferenceScore(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): number {
  return (food?.matchedPositiveTags.length ?? 0) + (beverage?.matchedTags.length ?? 0);
}

export function isYuyukoProgressPair(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): boolean {
  return isYuyukoSafeEvaluationPair(food, beverage);
}

export function isYuyukoProgressEvaluationPair(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): boolean {
  if (!food || !beverage) return false;
  if (getYuyukoChallengeNegativeTags(food).length > 0) return false;

  return estimateYuyukoEvaluationScore(food, beverage) >= YUYUKO_GOOD_EVALUATION_SCORE;
}

export function isYuyukoSafeEvaluationPair(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): boolean {
  if (!food || !beverage) return false;
  if (!food.meetsRequiredFood || !beverage.meetsRequiredBeverage) return false;
  return isYuyukoProgressEvaluationPair(food, beverage);
}

export function isYuyukoProgressPlan(
  plan: RareOrderRecommendationPlan,
): boolean {
  if (plan.bucket === 'blocked') return false;
  return isYuyukoProgressPair(plan.food, plan.beverage);
}

export function isYuyukoSafeEvaluationPlan(
  plan: RareOrderRecommendationPlan,
): boolean {
  if (plan.bucket === 'blocked') return false;
  return isYuyukoSafeEvaluationPair(plan.food, plan.beverage);
}

export function scoreYuyukoPair(
  food: FoodCandidate,
  beverage: BeverageCandidate,
): number {
  const negativeTags = getYuyukoChallengeNegativeTags(food);
  const evaluationScore = estimateYuyukoEvaluationScore(food, beverage);
  const levelSum = getYuyukoLevelSum(food, beverage);
  const progressReady = isYuyukoProgressPair(food, beverage) ? 1 : 0;
  const exGoodReady = evaluationScore >= YUYUKO_EXGOOD_EVALUATION_SCORE ? 1 : 0;
  const baseDemandScore = getYuyukoBaseDemandScore(food, beverage);
  const preferenceScore = getYuyukoPreferenceScore(food, beverage);

  return progressReady * 1_000_000_000
    + exGoodReady * 100_000_000
    + evaluationScore * 10_000_000
    + levelSum * 1_000_000
    + baseDemandScore * 100_000
    + preferenceScore * 10_000
    + Math.min(food.recipe.price + beverage.beverage.price, 999) * 10
    + Math.min(beverage.ownedQuantity, 99)
    - negativeTags.length * 100_000_000
    - Math.ceil(food.resourcePressure * 100)
    - food.extraIngredients.length;
}

export function compareYuyukoPlans(
  left: RareOrderRecommendationPlan,
  right: RareOrderRecommendationPlan,
): number {
  const leftScore = scoreYuyukoPlan(left);
  const rightScore = scoreYuyukoPlan(right);
  if (leftScore !== rightScore) return rightScore - leftScore;
  return left.estimatedPrice - right.estimatedPrice;
}

export function buildYuyukoPlanReason(
  plan: RareOrderRecommendationPlan,
): string {
  return buildYuyukoReasonCore(plan.food, plan.beverage, '幽幽子三阶段');
}

export function buildYuyukoSafeEvaluationPlanReason(
  plan: RareOrderRecommendationPlan,
): string {
  return buildYuyukoReasonCore(plan.food, plan.beverage, '幽幽子二阶段安全评价', '安全评价阈值');
}

export function buildYuyukoProgressBlockedMessages(
  plans: readonly RareOrderRecommendationPlan[],
  limit = 3,
): string[] {
  const messages = [...plans]
    .filter((plan) => !isYuyukoProgressPlan(plan))
    .sort((left, right) => compareYuyukoPlans(left, right))
    .map(buildYuyukoProgressBlockReason)
    .filter((message): message is string => Boolean(message));
  const uniqueMessages = uniqueTags(messages);
  if (uniqueMessages.length > 0) return uniqueMessages.slice(0, Math.max(1, limit));
  if (plans.length === 0) return [];
  return ['幽幽子第三阶段没有可预测推进进度的执行方案。'];
}

export function buildYuyukoSafeEvaluationBlockedMessages(
  plans: readonly RareOrderRecommendationPlan[],
  limit = 3,
): string[] {
  const messages = [...plans]
    .filter((plan) => !isYuyukoSafeEvaluationPlan(plan))
    .sort((left, right) => compareYuyukoPlans(left, right))
    .map(buildYuyukoSafeEvaluationBlockReason)
    .filter((message): message is string => Boolean(message));
  const uniqueMessages = uniqueTags(messages);
  if (uniqueMessages.length > 0) return uniqueMessages.slice(0, Math.max(1, limit));
  if (plans.length === 0) return [];
  return ['幽幽子第二阶段没有可预测稳定高评价的执行方案。'];
}

export function buildYuyukoProgressBlockReason(
  plan: RareOrderRecommendationPlan,
): string | null {
  if (isYuyukoProgressPlan(plan)) return null;
  const details = buildYuyukoProgressBlockDetails(plan);
  if (details.length === 0) return null;
  return `${formatYuyukoPlanTarget(plan)}：${details.join('；')}`;
}

export function buildYuyukoSafeEvaluationBlockReason(
  plan: RareOrderRecommendationPlan,
): string | null {
  if (isYuyukoSafeEvaluationPlan(plan)) return null;
  const details = buildYuyukoProgressBlockDetails(plan);
  if (details.length === 0) return null;
  return `${formatYuyukoPlanTarget(plan)}：${details.join('；')}`;
}

export function buildYuyukoTargetReason(
  food: FoodCandidate,
  beverage: BeverageCandidate,
): string {
  return buildYuyukoReasonCore(food, beverage, '幽幽子三阶段执行方案');
}

export function getYuyukoChallengeNegativeTags(food: FoodCandidate | null | undefined): string[] {
  if (!food) return [];
  return uniqueTags([
    ...food.matchedNegativeTags,
    ...food.activeTags.filter((tag) => YUYUKO_CHALLENGE_FOOD_HATE_TAG_SET.has(tag)),
  ]);
}

function scoreYuyukoPlan(plan: RareOrderRecommendationPlan): number {
  if (!plan.food || !plan.beverage || plan.bucket === 'blocked') return Number.NEGATIVE_INFINITY;
  return scoreYuyukoPair(plan.food, plan.beverage);
}

function buildYuyukoProgressBlockDetails(plan: RareOrderRecommendationPlan): string[] {
  const food = plan.food;
  const beverage = plan.beverage;
  const details: string[] = [];

  if (!food) {
    details.push('缺少料理候选');
  } else {
    if (!food.meetsRequiredFood) details.push(`料理未满足点单 ${plan.demand.requiredFoodTag || '未知'}`);
    const negativeTags = getYuyukoChallengeNegativeTags(food);
    if (negativeTags.length > 0) details.push(`包含幽幽子厌恶 Tag ${negativeTags.join('、')}`);
  }

  if (!beverage) {
    details.push('缺少酒水候选');
  } else if (!beverage.meetsRequiredBeverage) {
    details.push(`酒水未满足点单 ${plan.demand.requiredBeverageTag || '未知'}`);
  }

  if (food && beverage) {
    const evaluationScore = estimateYuyukoEvaluationScore(food, beverage);
    if (evaluationScore < YUYUKO_GOOD_EVALUATION_SCORE) {
      details.push(
        `预计${formatYuyukoEvaluationScore(evaluationScore)}，未达满意（Good）/完美（ExGood）`
        + `（等级合计 ${getYuyukoLevelSum(food, beverage)}，喜好命中 ${getYuyukoPreferenceScore(food, beverage)}）`,
      );
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

function getYuyukoBaseDemandScore(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
): number {
  return (food?.meetsRequiredFood ? 1 : 0) + (beverage?.meetsRequiredBeverage ? 1 : 0);
}

function buildYuyukoReasonCore(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
  prefix: string,
  thresholdLabel = '推进阈值',
): string {
  const evaluationScore = estimateYuyukoEvaluationScore(food, beverage);
  const evaluationText = evaluationScore >= YUYUKO_EXGOOD_EVALUATION_SCORE
    ? '完美（ExGood）'
    : evaluationScore >= YUYUKO_GOOD_EVALUATION_SCORE
      ? '满意（Good）'
      : '未达稳定推进评价';
  const foodLevel = food?.recipe.level ?? 0;
  const beverageLevel = beverage?.beverage.level ?? 0;
  const preferenceScore = getYuyukoPreferenceScore(food, beverage);
  const negativeTags = getYuyukoChallengeNegativeTags(food);

  return [
    `${prefix}：预计 ${evaluationText}`,
    `${thresholdLabel} 等级合计 >= ${YUYUKO_GOOD_LEVEL_SUM}`,
    `料理 Lv.${foodLevel}`,
    `酒水 Lv.${beverageLevel}`,
    `等级合计 ${foodLevel + beverageLevel}`,
    `喜好命中 ${preferenceScore}（排序参考）`,
    negativeTags.length > 0 ? `厌恶 ${negativeTags.join('、')}` : '无厌恶 Tag',
  ].join('，');
}

function uniqueTags(tags: string[]): string[] {
  return Array.from(new Set(tags.filter(Boolean)));
}
