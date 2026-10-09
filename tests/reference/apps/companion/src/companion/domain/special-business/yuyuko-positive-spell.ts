import type {
  BeverageCandidate,
  FoodCandidate,
  RareOrderRecommendationPlan,
  RareTagOrderDemand,
} from '@/recommendation-engine';
import { cappedInventoryQuantityRank } from '@/lib/inventory-quantity';

export const YUYUKO_POSITIVE_SPELL_EXGOOD_SCORE = 4;
export const YUYUKO_POSITIVE_SPELL_MIN_EXTRA_PREFERENCE_MATCHES = YUYUKO_POSITIVE_SPELL_EXGOOD_SCORE - 2;

export interface YuyukoTagOrderEvaluation {
  baseDemandScore: number;
  extraPreferenceScore: number;
  evaluationScore: number;
  foodExtraPreferenceTags: string[];
  beverageExtraPreferenceTags: string[];
  negativeTags: string[];
}

export interface YuyukoPositiveSpellEvaluation extends YuyukoTagOrderEvaluation {
  canTriggerPositiveSpell: boolean;
}

/**
 * Mirrors the native SpecialOrder evaluation inputs used by Yuyuko's phase-two
 * spell and Retake phase-three progress orders.
 */
export function evaluateYuyukoTagOrderPair(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
  demand: Pick<RareTagOrderDemand, 'requiredFoodTag' | 'requiredBeverageTag'>,
): YuyukoTagOrderEvaluation {
  const baseDemandScore = Number(food?.meetsRequiredFood === true)
    + Number(beverage?.meetsRequiredBeverage === true);
  const foodExtraPreferenceTags = withoutRequiredTag(
    food?.matchedPositiveTags ?? [],
    demand.requiredFoodTag,
  );
  const beverageExtraPreferenceTags = withoutRequiredTag(
    beverage?.matchedTags ?? [],
    demand.requiredBeverageTag,
  );
  const negativeTags = getYuyukoPositiveSpellNegativeTags(food, demand.requiredFoodTag);
  const extraPreferenceScore = foodExtraPreferenceTags.length + beverageExtraPreferenceTags.length;
  const evaluationScore = baseDemandScore + extraPreferenceScore;

  return {
    baseDemandScore,
    extraPreferenceScore,
    evaluationScore,
    foodExtraPreferenceTags,
    beverageExtraPreferenceTags,
    negativeTags,
  };
}

export function evaluateYuyukoPositiveSpellPair(
  food: FoodCandidate | null | undefined,
  beverage: BeverageCandidate | null | undefined,
  demand: Pick<RareTagOrderDemand, 'requiredFoodTag' | 'requiredBeverageTag'>,
): YuyukoPositiveSpellEvaluation {
  const evaluation = evaluateYuyukoTagOrderPair(food, beverage, demand);
  return {
    ...evaluation,
    canTriggerPositiveSpell: food != null
      && beverage != null
      && evaluation.baseDemandScore === 2
      && evaluation.negativeTags.length === 0
      && evaluation.evaluationScore >= YUYUKO_POSITIVE_SPELL_EXGOOD_SCORE,
  };
}

export function isYuyukoPositiveSpellPlan(plan: RareOrderRecommendationPlan): boolean {
  if (plan.bucket === 'blocked') return false;
  return evaluateYuyukoPositiveSpellPair(plan.food, plan.beverage, plan.demand).canTriggerPositiveSpell;
}

export function compareYuyukoPositiveSpellPlans(
  left: RareOrderRecommendationPlan,
  right: RareOrderRecommendationPlan,
): number {
  const leftScore = scoreYuyukoPositiveSpellPlan(left);
  const rightScore = scoreYuyukoPositiveSpellPlan(right);
  if (leftScore !== rightScore) return rightScore - leftScore;
  return left.estimatedPrice - right.estimatedPrice;
}

export function buildYuyukoPositiveSpellPlanReason(plan: RareOrderRecommendationPlan): string {
  const evaluation = evaluateYuyukoPositiveSpellPair(plan.food, plan.beverage, plan.demand);
  const preferenceTags = [
    ...evaluation.foodExtraPreferenceTags,
    ...evaluation.beverageExtraPreferenceTags,
  ];
  return [
    '幽幽子二阶段正面符卡：预计完美（ExGood）',
    `点单基础 ${evaluation.baseDemandScore}`,
    `额外喜好 ${evaluation.extraPreferenceScore}${preferenceTags.length > 0 ? `（${preferenceTags.join('、')}）` : ''}`,
    evaluation.negativeTags.length > 0
      ? `当前稀客厌恶 ${evaluation.negativeTags.join('、')}`
      : '无当前稀客厌恶 Tag',
  ].join('，');
}

export function buildYuyukoPositiveSpellBlockedMessages(
  plans: readonly RareOrderRecommendationPlan[],
  limit = 3,
): string[] {
  const messages = [...plans]
    .filter((plan) => !isYuyukoPositiveSpellPlan(plan))
    .sort((left, right) => compareYuyukoPositiveSpellPlans(left, right))
    .map(buildYuyukoPositiveSpellBlockReason)
    .filter((message): message is string => Boolean(message));
  const uniqueMessages = uniqueTags(messages);
  if (uniqueMessages.length > 0) return uniqueMessages.slice(0, Math.max(1, limit));
  if (plans.length === 0) return [];
  return ['幽幽子第二阶段没有可预测触发正面符卡的完美（ExGood）执行方案。'];
}

export function getYuyukoPositiveSpellFoodCandidateRank(
  food: FoodCandidate,
  requiredFoodTag: string | null | undefined,
): number {
  if (!food.meetsRequiredFood || getYuyukoPositiveSpellNegativeTags(food, requiredFoodTag).length > 0) return 0;
  const extraPreferenceScore = withoutRequiredTag(food.matchedPositiveTags, requiredFoodTag).length;
  return 10_000
    + extraPreferenceScore * 10_000
    - Math.min(food.recipe.price, 999)
    - Math.ceil(food.resourcePressure * 10)
    - food.extraIngredients.length;
}

export function getYuyukoPositiveSpellNegativeTags(
  food: FoodCandidate | null | undefined,
  requiredFoodTag: string | null | undefined,
): string[] {
  return withoutRequiredTag(food?.matchedNegativeTags ?? [], requiredFoodTag);
}

export function getYuyukoPositiveSpellBeverageCandidateRank(
  beverage: BeverageCandidate,
  requiredBeverageTag: string | null | undefined,
): number {
  if (!beverage.meetsRequiredBeverage) return 0;
  const extraPreferenceScore = withoutRequiredTag(beverage.matchedTags, requiredBeverageTag).length;
  return 10_000
    + extraPreferenceScore * 10_000
    - Math.min(beverage.beverage.price, 999)
    + cappedInventoryQuantityRank(beverage.ownedQuantity, 99);
}

function scoreYuyukoPositiveSpellPlan(plan: RareOrderRecommendationPlan): number {
  if (!plan.food || !plan.beverage || plan.bucket === 'blocked') return Number.NEGATIVE_INFINITY;
  const evaluation = evaluateYuyukoPositiveSpellPair(plan.food, plan.beverage, plan.demand);
  return Number(evaluation.canTriggerPositiveSpell) * 1_000_000_000
    + evaluation.evaluationScore * 10_000_000
    + evaluation.extraPreferenceScore * 1_000_000
    + evaluation.baseDemandScore * 100_000
    + Math.min(plan.food.recipe.price + plan.beverage.beverage.price, 999) * 10
    + cappedInventoryQuantityRank(plan.beverage.ownedQuantity, 99)
    - evaluation.negativeTags.length * 100_000_000
    - Math.ceil(plan.food.resourcePressure * 100)
    - plan.food.extraIngredients.length;
}

function buildYuyukoPositiveSpellBlockReason(plan: RareOrderRecommendationPlan): string | null {
  if (isYuyukoPositiveSpellPlan(plan)) return null;
  const food = plan.food;
  const beverage = plan.beverage;
  const evaluation = evaluateYuyukoPositiveSpellPair(food, beverage, plan.demand);
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

  if (food && beverage
    && evaluation.baseDemandScore === 2
    && evaluation.extraPreferenceScore < YUYUKO_POSITIVE_SPELL_MIN_EXTRA_PREFERENCE_MATCHES) {
    details.push(
      `除点单 Tag 外仅命中 ${evaluation.extraPreferenceScore} 个当前稀客喜好`
      + `，触发正面符卡需要至少 ${YUYUKO_POSITIVE_SPELL_MIN_EXTRA_PREFERENCE_MATCHES} 个`,
    );
  }

  for (const result of plan.conditionResults) {
    if (result.status === 'fail' && result.severity === 'hard') details.push(result.detail);
  }
  if (details.length === 0 && plan.bucket === 'blocked') details.push('组合被推荐引擎标记为不可执行');
  if (details.length === 0) details.push('预计无法获得完美（ExGood）评价并触发正面符卡');
  return `${formatPlanTarget(plan)}：${uniqueTags(details).join('；')}`;
}

function formatPlanTarget(plan: RareOrderRecommendationPlan): string {
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

function withoutRequiredTag(tags: readonly string[], requiredTag: string | null | undefined): string[] {
  const normalizedRequiredTag = requiredTag?.trim() ?? '';
  return uniqueTags(tags.filter((tag) => tag.trim() !== normalizedRequiredTag));
}

function uniqueTags(tags: readonly string[]): string[] {
  return Array.from(new Set(tags.map((tag) => tag.trim()).filter(Boolean)));
}
