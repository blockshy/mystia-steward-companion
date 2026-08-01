import { buildRuntimeSets } from '@/companion/domain/cookers';
import { buildRecommendationRuntimeContext } from '@/companion/domain/service-recommendations';
import { normalizeSpecialBusinessTags } from '@/companion/domain/special-business/rules';
import { emptySpecialFoodTargetWirePolicy } from '@/companion/domain/special-business/target-policy';
import type { CompanionPreferences } from '@/companion/preferences';
import type {
  NormalBusinessOrder,
  NormalOrderExecutionMode,
  NormalOrderExecutionTarget,
  RecommendationStateSnapshot,
} from '@/companion/types';
import { ALL_PLACES, type RareCustomerCatalogItem } from '@/lib/catalog-types';
import {
  buildRecommendationDataIndexes,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type {
  BeverageCandidate,
  ConditionResult,
  FoodCandidate,
  RareTagOrderDemand,
  RecommendationRuntimeContext,
  SpecialBusinessFoodTargetPolicy,
} from '@/recommendation-engine';

export const CANDIDATE_PAIR_FOOD_LIMIT = 48;
export const CANDIDATE_PAIR_BEVERAGE_LIMIT = 32;

interface BuildExecutionTargetOptions {
  executionMode?: NormalOrderExecutionMode;
  expectedFoodModifierTags?: readonly string[];
  specialTargetFoodTags?: readonly string[];
}

export function buildNormalTargetRuntimeContext(
  runtime: RecommendationStateSnapshot,
  preferences: CompanionPreferences,
  data: RecommendationDataSet,
): RecommendationRuntimeContext | null {
  const runtimeSets = buildRuntimeSets(runtime, data);
  if (!runtimeSets) return null;
  return buildRecommendationRuntimeContext(runtime, runtimeSets, preferences, data);
}

export function buildSyntheticCustomer(
  order: NormalBusinessOrder,
  data: RecommendationDataSet,
  {
    preferRareCustomer,
    fallbackFoodTags,
  }: {
    preferRareCustomer: boolean;
    fallbackFoodTags: readonly string[];
  },
): RareCustomerCatalogItem {
  const allowRareCustomer = isRareLikeSpecialBusinessRole(order.specialBusinessRole);
  const rareCustomer = allowRareCustomer && order.guestId != null
    ? data.rareCustomers.find((customer) => customer.id === order.guestId)
    : null;
  if (preferRareCustomer && rareCustomer) return rareCustomer;

  const normalCustomer = order.guestId != null
    ? data.normalCustomers.find((customer) => customer.id === order.guestId)
    : null;
  const foodTags = normalizeSpecialBusinessTags([
    ...(order.foodPreferenceTags ?? []),
    ...(normalCustomer?.positiveTags ?? []),
    ...(allowRareCustomer ? rareCustomer?.positiveTags ?? [] : []),
    ...fallbackFoodTags,
  ]);
  const beverageTags = normalizeSpecialBusinessTags([
    ...(order.beveragePreferenceTags ?? []),
    ...(normalCustomer?.beverageTags ?? []),
    ...(allowRareCustomer ? rareCustomer?.beverageTags ?? [] : []),
  ]);
  return {
    id: order.guestId ?? -1,
    name: order.guestName || '普客',
    description: '',
    dlc: 0,
    places: ALL_PLACES,
    price: [0, 0],
    enduranceLimit: 1,
    positiveTags: foodTags,
    negativeTags: rareCustomer?.negativeTags ?? [],
    beverageTags,
    collection: false,
    evaluation: {},
    spellCards: { positive: [], negative: [] },
  };
}

export function buildSyntheticDemand(
  customer: RareCustomerCatalogItem,
  requiredFoodTag: string,
  requiredBeverageTag: string,
  specialFoodTarget?: SpecialBusinessFoodTargetPolicy,
): RareTagOrderDemand {
  return {
    type: 'rare-tag-order',
    customer,
    requiredFoodTag,
    requiredBeverageTag,
    ...(specialFoodTarget
      ? {
        specialFoodTarget: {
          ...specialFoodTarget,
          tags: normalizeSpecialBusinessTags(specialFoodTarget.tags),
        },
      }
      : {}),
  };
}

export function selectBestPair({
  foodCandidates,
  beverageCandidates,
  scoreFood,
  scoreBeverage,
  scorePair,
}: {
  foodCandidates: FoodCandidate[];
  beverageCandidates: BeverageCandidate[];
  scoreFood: (candidate: FoodCandidate) => number;
  scoreBeverage: (candidate: BeverageCandidate) => number;
  scorePair: (food: FoodCandidate, beverage: BeverageCandidate) => number;
}): { food: FoodCandidate; beverage: BeverageCandidate } | null {
  const foods = [...foodCandidates]
    .sort((left, right) => scoreFood(right) - scoreFood(left))
    .slice(0, CANDIDATE_PAIR_FOOD_LIMIT);
  const beverages = [...beverageCandidates]
    .sort((left, right) => scoreBeverage(right) - scoreBeverage(left))
    .slice(0, CANDIDATE_PAIR_BEVERAGE_LIMIT);
  let best: { food: FoodCandidate; beverage: BeverageCandidate; score: number } | null = null;
  for (const food of foods) {
    for (const beverage of beverages) {
      const score = scorePair(food, beverage);
      if (!best || score > best.score) best = { food, beverage, score };
    }
  }
  return best ? { food: best.food, beverage: best.beverage } : null;
}

export function buildExecutionTarget(
  order: NormalBusinessOrder,
  food: FoodCandidate,
  beverage: BeverageCandidate,
  reason: string,
  options: BuildExecutionTargetOptions = {},
): NormalOrderExecutionTarget {
  const executionMode = options.executionMode;
  const expectedFoodModifierTags = options.expectedFoodModifierTags ?? [];
  const specialTargetFoodTags = options.specialTargetFoodTags ?? [];
  return {
    ...emptySpecialFoodTargetWirePolicy(),
    matchFoodId: order.foodId,
    matchBeverageId: order.beverageId,
    foodId: food.recipe.id,
    recipeId: food.recipe.recipeId,
    ...(executionMode ? { executionMode } : {}),
    recipeName: food.recipe.name,
    extraIngredientIds: food.extraIngredients.map((ingredient) => ingredient.id),
    beverageId: beverage.beverage.id,
    beverageName: beverage.beverage.name,
    cookerName: food.recipe.cooker,
    foodTags: food.activeTags,
    expectedFoodModifierTags: normalizeSpecialBusinessTags(expectedFoodModifierTags),
    beverageTags: beverage.activeTags,
    specialTargetFoodTags: normalizeSpecialBusinessTags(specialTargetFoodTags),
    reason,
  };
}

export function findOrderRecipe(order: NormalBusinessOrder, data: RecommendationDataSet) {
  const indexes = buildRecommendationDataIndexes(data);
  return indexes.recipeByFoodId.get(order.foodId) ?? null;
}

export function estimateOrderPrice(food: FoodCandidate, beverage: BeverageCandidate): number {
  return Math.max(0, food.recipe.price) + Math.max(0, beverage.beverage.price);
}

export function hasNoHardFailures(candidate: { conditionResults: ConditionResult[] }): boolean {
  return !candidate.conditionResults.some((result) => result.status === 'fail' && result.severity === 'hard');
}

export function hasNoHardFailuresExcept(candidate: { conditionResults: ConditionResult[] }, ignoredId: string): boolean {
  return !candidate.conditionResults.some((result) =>
    result.id !== ignoredId && result.status === 'fail' && result.severity === 'hard'
  );
}

export function firstTag(tags: readonly string[]): string {
  return tags.find((tag) => tag.trim().length > 0) ?? '';
}

export function emptySelection() {
  return { target: null, message: '' };
}

function isRareLikeSpecialBusinessRole(role: string | null | undefined): boolean {
  const value = role?.trim();
  if (!value) return false;
  return value !== 'wacky-target-order';
}
