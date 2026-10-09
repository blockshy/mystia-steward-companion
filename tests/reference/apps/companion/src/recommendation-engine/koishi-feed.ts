export interface KoishiBrokenShieldScoreInput {
  meetsRequiredFood: boolean;
  meetsRequiredBeverage: boolean;
  preferenceMatches: number;
  negativeMatches: number;
  foodLevel?: number | null;
  beverageLevel?: number | null;
  foodPrice?: number | null;
  beveragePrice?: number | null;
  estimatedPrice?: number | null;
}

export interface KoishiFeedPlanningInput {
  remainingScore: number | null | undefined;
  remainingBudget: number | null | undefined;
  remainingOrderCount?: number | null | undefined;
}

export interface KoishiFeedPlanningInfo {
  attemptsRemaining: number | null;
  requiredScoreThisOrder: number | null;
}

const KOISHI_MIN_FOLLOW_UP_BUDGET = 120;

export function estimateKoishiBrokenShieldEvaluationScore({
  meetsRequiredFood,
  meetsRequiredBeverage,
  preferenceMatches,
  negativeMatches,
}: KoishiBrokenShieldScoreInput): number {
  const requiredScore = (meetsRequiredFood ? 1 : 0) + (meetsRequiredBeverage ? 1 : 0);
  return Math.max(0, requiredScore + preferenceMatches - negativeMatches * 2);
}

export function estimateKoishiBrokenShieldDamageLevel({
  foodLevel,
  beverageLevel,
  negativeMatches,
}: Pick<KoishiBrokenShieldScoreInput, 'foodLevel' | 'beverageLevel' | 'negativeMatches'>): number {
  return Math.max(0, normalizeNonNegativeInt(foodLevel) + normalizeNonNegativeInt(beverageLevel) - negativeMatches * 3);
}

export function estimateKoishiBrokenShieldFeedScore(input: KoishiBrokenShieldScoreInput): number {
  const evaluationFeedScore = estimateKoishiFeedScoreFromEvaluation(estimateKoishiBrokenShieldEvaluationScore(input));
  if (evaluationFeedScore <= 0) return 0;

  const damageLevel = estimateKoishiBrokenShieldDamageLevel(input);
  const levelFeedScore = estimateKoishiFeedScoreFromDamageLevel(damageLevel);
  const priceFeedScore = estimateKoishiFeedScoreFromPrices(input);
  const damageFeedScore = levelFeedScore > 0 ? levelFeedScore : priceFeedScore;

  if (damageFeedScore <= 0) return evaluationFeedScore;
  return Math.min(evaluationFeedScore, damageFeedScore);
}

export function buildKoishiFeedPlanningInfo({
  remainingScore,
  remainingOrderCount,
}: KoishiFeedPlanningInput): KoishiFeedPlanningInfo {
  const score = normalizePositiveInt(remainingScore);
  if (score == null) {
    return {
      attemptsRemaining: null,
      requiredScoreThisOrder: null,
    };
  }

  const orderCount = normalizeNonNegativeIntOrNull(remainingOrderCount);
  const attemptsRemaining = orderCount == null ? null : orderCount + 1;
  return {
    attemptsRemaining,
    requiredScoreThisOrder: attemptsRemaining == null
      ? null
      : Math.max(1, Math.ceil(score / Math.max(1, attemptsRemaining))),
  };
}

export function isKoishiFeedPlanSustainable({
  estimatedPrice,
  estimatedFeedScore,
  remainingBudget,
  remainingScore,
  remainingOrderCount,
  minFollowUpBudget = KOISHI_MIN_FOLLOW_UP_BUDGET,
}: {
  estimatedPrice: number;
  estimatedFeedScore: number;
  remainingBudget: number | null | undefined;
  remainingScore: number | null | undefined;
  remainingOrderCount?: number | null | undefined;
  minFollowUpBudget?: number;
}): boolean {
  const budget = normalizeNonNegativeIntOrNull(remainingBudget);
  const score = normalizePositiveInt(remainingScore);
  if (budget == null || score == null || estimatedFeedScore <= 0) return false;
  if (estimatedPrice > budget) return false;
  if (estimatedFeedScore >= score) return true;
  if (estimatedPrice * score > budget * estimatedFeedScore) return false;

  const orderCount = normalizeNonNegativeIntOrNull(remainingOrderCount);
  if (orderCount != null && orderCount > 0 && budget - estimatedPrice < minFollowUpBudget) {
    return false;
  }
  return true;
}

function estimateKoishiFeedScoreFromEvaluation(evaluationHint: number): number {
  if (evaluationHint >= 6) return 7;
  if (evaluationHint >= 5) return 5;
  if (evaluationHint >= 4) return 4;
  return Math.max(0, Math.min(3, evaluationHint));
}

function estimateKoishiFeedScoreFromDamageLevel(level: number): number {
  return Math.max(0, Math.min(7, Math.trunc(level)));
}

function estimateKoishiFeedScoreFromPrices({
  foodPrice,
  beveragePrice,
  estimatedPrice,
}: Pick<KoishiBrokenShieldScoreInput, 'foodPrice' | 'beveragePrice' | 'estimatedPrice'>): number {
  const foodScore = estimateKoishiComponentLevelFromFoodPrice(foodPrice);
  const beverageScore = estimateKoishiComponentLevelFromBeveragePrice(beveragePrice);
  if (foodScore > 0 || beverageScore > 0) {
    return Math.max(0, Math.min(7, foodScore + beverageScore));
  }
  return estimateKoishiFeedScoreFromTotalPrice(estimatedPrice);
}

function estimateKoishiComponentLevelFromFoodPrice(price: number | null | undefined): number {
  if (!Number.isFinite(price)) return 0;
  const value = Math.max(0, Math.trunc(price ?? 0));
  if (value >= 80) return 4;
  if (value >= 30) return 3;
  if (value >= 10) return 2;
  if (value > 0) return 1;
  return 0;
}

function estimateKoishiComponentLevelFromBeveragePrice(price: number | null | undefined): number {
  if (!Number.isFinite(price)) return 0;
  const value = Math.max(0, Math.trunc(price ?? 0));
  if (value >= 120) return 3;
  if (value >= 40) return 2;
  if (value > 0) return 1;
  return 0;
}

function estimateKoishiFeedScoreFromTotalPrice(price: number | null | undefined): number {
  if (!Number.isFinite(price)) return 0;
  const value = Math.max(0, Math.trunc(price ?? 0));
  if (value >= 180) return 7;
  if (value >= 120) return 5;
  if (value >= 70) return 3;
  if (value > 0) return 2;
  return 0;
}

function normalizeNonNegativeInt(value: number | null | undefined): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.trunc(value ?? 0));
}

function normalizeNonNegativeIntOrNull(value: number | null | undefined): number | null {
  if (!Number.isFinite(value)) return null;
  return Math.max(0, Math.trunc(value ?? 0));
}

function normalizePositiveInt(value: number | null | undefined): number | null {
  if (!Number.isFinite(value)) return null;
  const normalized = Math.trunc(value ?? 0);
  return normalized > 0 ? normalized : null;
}
