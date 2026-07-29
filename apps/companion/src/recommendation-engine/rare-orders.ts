import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { RareCustomerCatalogItem, IngredientCatalogItem, RecipeCatalogItem } from '@/lib/catalog-types';
import {
  inventoryQuantityRankValue,
  inventoryShortage,
} from '@/lib/inventory-quantity';
import {
  normalizeRecommendationSortProfile,
  type RecommendationObjectiveKey,
  type RecommendationPlanSortContext,
  type RecommendationSortProfile,
} from '@/recommendation-engine/sort-profile';
import {
  findTagsThatCanSuppress,
  hasForbiddenIngredientTag,
  resolveFoodTags,
  resolveTagPriority,
} from '@/recommendation-engine/tag-resolution';
import {
  buildKoishiFeedPlanningInfo,
  estimateKoishiBrokenShieldFeedScore,
  isKoishiFeedPlanSustainable,
} from '@/recommendation-engine/koishi-feed';
import type {
  BeverageCandidate,
  ConditionResult,
  FoodCandidate,
  RecommendationBudgetContext,
  RecommendationBudgetPolicy,
  RecommendationBudgetResult,
  RareBeverageCandidateSearchDiagnostic,
  RareFoodCandidateSearchDiagnostic,
  RareOrderRecommendationPlan,
  RareTagOrderDemand,
  RecommendationBucket,
  RecommendationRuntimeContext,
} from '@/recommendation-engine/types';
import { isMissionRecipeExecutionPlan } from '@/recommendation-engine/mission-recipe-priority';

const DEFAULT_BEAM_WIDTH = 64;
const DEFAULT_FOOD_PLAN_CANDIDATE_LIMIT = 80;
const DEFAULT_BEVERAGE_PLAN_CANDIDATE_LIMIT = 40;
const MAX_FOOD_INGREDIENT_COUNT = 5;
const LOW_STOCK_THRESHOLD = 5;

interface BuildRareOrderPlansOptions {
  data: RecommendationDataSet;
  customer: RareCustomerCatalogItem;
  requiredFoodTag: string;
  requiredBeverageTag: string;
  specialFoodTargetTags?: string[];
  context: RecommendationRuntimeContext;
  limit?: number;
  sortProfile?: RecommendationSortProfile;
  sortContext?: RecommendationPlanSortContext;
}

interface BuildRareOrderPlansFromCandidatesOptions extends BuildRareOrderPlansOptions {
  foodCandidates: FoodCandidate[];
  beverageCandidates: BeverageCandidate[];
}

interface IngredientSearchState {
  ingredients: IngredientCatalogItem[];
  activeTags: string[];
  suppressedTags: string[];
  matchedPositiveTags: string[];
  matchedNegativeTags: string[];
  matchedSpecialFoodTargetTags: string[];
  meetsSpecialFoodTarget: boolean;
  meetsRequiredFood: boolean;
  extraCost: number;
  resourcePressure: number;
}

/**
 * 为单个稀客点单构建料理与酒水的组合推荐方案。
 *
 * 函数先分别生成料理候选和酒水候选，再组合成完整方案并按推荐权重排序。候选数量会先被截断，
 * 避免稀客经营中同时处理多笔订单时组合爆炸影响 UI 和 Worker 响应。
 */
export function buildRareOrderPlans({
  data,
  customer,
  requiredFoodTag,
  requiredBeverageTag,
  specialFoodTargetTags = [],
  context,
  limit,
  sortProfile,
  sortContext,
}: BuildRareOrderPlansOptions): RareOrderRecommendationPlan[] {
  const demand: RareTagOrderDemand = {
    type: 'rare-tag-order',
    customer,
    requiredFoodTag,
    requiredBeverageTag,
    specialFoodTargetTags,
  };
  const foodCandidates = buildRareFoodCandidates(data, demand, context);
  const beverageCandidates = buildRareBeverageCandidates(data, demand, context);

  return buildRareOrderPlansFromCandidates({
    data,
    customer,
    requiredFoodTag,
    requiredBeverageTag,
    specialFoodTargetTags,
    context,
    limit,
    sortProfile,
    sortContext,
    foodCandidates: foodCandidates.slice(0, DEFAULT_FOOD_PLAN_CANDIDATE_LIMIT),
    beverageCandidates: beverageCandidates.slice(0, DEFAULT_BEVERAGE_PLAN_CANDIDATE_LIMIT),
  });
}

/**
 * 从已筛选的料理/酒水候选构造完整推荐方案。
 *
 * 该入口供经营中订单复用已算好的候选，保证收藏、自定义料理置顶和兜底候选走同一套组合排序。
 */
export function buildRareOrderPlansFromCandidates({
  data,
  customer,
  requiredFoodTag,
  requiredBeverageTag,
  specialFoodTargetTags = [],
  context,
  limit,
  sortProfile,
  sortContext,
  foodCandidates,
  beverageCandidates,
}: BuildRareOrderPlansFromCandidatesOptions): RareOrderRecommendationPlan[] {
  const demand: RareTagOrderDemand = {
    type: 'rare-tag-order',
    customer,
    requiredFoodTag,
    requiredBeverageTag,
    specialFoodTargetTags,
  };
  const plans: RareOrderRecommendationPlan[] = [];
  for (const food of foodCandidates) {
    for (const beverage of beverageCandidates) {
      plans.push(buildRarePlan(demand, food, beverage, context));
    }
  }

  if (plans.length === 0) {
    return [buildBlockedPlan(demand, foodCandidates[0] ?? null, beverageCandidates[0] ?? null, context, data)];
  }

  const sortedPlans = sortRareOrderPlans(plans, sortProfile, sortContext);
  if (limit == null || !Number.isFinite(limit)) return sortedPlans;
  return sortedPlans.slice(0, Math.max(0, Math.trunc(limit)));
}

export function sortRareOrderPlans(
  plans: RareOrderRecommendationPlan[],
  sortProfile?: RecommendationSortProfile,
  sortContext: RecommendationPlanSortContext = {},
): RareOrderRecommendationPlan[] {
  const profile = normalizeRecommendationSortProfile(sortProfile);
  const ranges = buildObjectiveRanges(plans);

  return [...plans].sort((left, right) => compareRarePlans(left, right, profile, sortContext, ranges));
}

/**
 * 构建稀客料理候选。
 *
 * 基础配方必须已解锁、材料可用且满足厨具硬过滤；额外食材通过有限宽度搜索补齐点单 Tag 和稀客偏好。
 * 游戏限制料理最多五种材料，因此重复基础材料会按配方原始长度占用槽位。
 */
export function buildRareFoodCandidates(
  data: RecommendationDataSet,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
): FoodCandidate[] {
  const ingredientsByName = new Map(data.ingredients.map((ingredient) => [ingredient.name, ingredient]));
  const ingredientsById = new Map(data.ingredients.map((ingredient) => [ingredient.id, ingredient]));
  const usableIngredients = [...context.availableIngredientIds]
    .filter((id) => !isIngredientExcluded(id, context))
    .map((id) => ingredientsById.get(id))
    .filter((ingredient): ingredient is IngredientCatalogItem => Boolean(ingredient));

  const candidates: FoodCandidate[] = [];
  for (const recipe of data.recipes) {
    if (!context.availableRecipeIds.has(recipe.id)) continue;
    if (!hasAvailableBaseIngredients(recipe, ingredientsByName, context)) continue;
    if (context.filterMissingCookers && !isCookerAvailable(recipe, context)) continue;
    candidates.push(...buildFoodCandidatesForRecipe(
      recipe,
      usableIngredients,
      ingredientsByName,
      demand,
      context,
    ));
  }

  return candidates.sort(compareFoodCandidates);
}

/**
 * 在推荐已无可执行方案时定位料理候选首次归零的运行时边界。
 *
 * 诊断只对配方做轻量资格检查，并复用已经生成的正式候选。未解锁、缺基础材料
 * 或缺厨具的配方绝不会为了诊断执行加料 beam search。
 */
export function diagnoseRareFoodCandidateSearch(
  data: RecommendationDataSet,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
  generatedCandidates: readonly FoodCandidate[],
): RareFoodCandidateSearchDiagnostic {
  const ingredientsByName = new Map(data.ingredients.map((ingredient) => [ingredient.name, ingredient]));
  const missingIngredientNames = new Set<string>();
  const missingCookerNames = new Set<string>();
  let requiredTagReachableRecipeCount = 0;
  let requiredTagReachableUnlockedRecipeCount = 0;
  let requiredTagReachableBaseIngredientsReadyRecipeCount = 0;
  let requiredTagReachableCookerReadyRecipeCount = 0;

  for (const recipe of data.recipes) {
    if (!canRecipeReachRequiredTagWithoutSearch(
      recipe,
      data.ingredients,
      ingredientsByName,
      demand,
      context,
    )) continue;
    requiredTagReachableRecipeCount += 1;

    const unlocked = context.availableRecipeIds.has(recipe.id);
    if (!unlocked) continue;
    requiredTagReachableUnlockedRecipeCount += 1;

    const baseIngredientsAvailable = hasAvailableBaseIngredients(recipe, ingredientsByName, context);
    if (!baseIngredientsAvailable) {
      for (const ingredientName of findUnavailableBaseIngredientNames(recipe, ingredientsByName, context)) {
        missingIngredientNames.add(ingredientName);
      }
      continue;
    }
    requiredTagReachableBaseIngredientsReadyRecipeCount += 1;

    const cookerAvailable = isCookerAvailable(recipe, context);
    if (!cookerAvailable) {
      if (recipe.cooker.trim()) missingCookerNames.add(recipe.cooker.trim());
      continue;
    }
    requiredTagReachableCookerReadyRecipeCount += 1;
  }

  return {
    catalogRecipeCount: data.recipes.length,
    requiredTagReachableRecipeCount,
    requiredTagReachableUnlockedRecipeCount,
    requiredTagReachableBaseIngredientsReadyRecipeCount,
    requiredTagReachableCookerReadyRecipeCount,
    generatedCandidateCount: generatedCandidates.length,
    generatedRequiredTagMatchedCandidateCount: generatedCandidates
      .filter((candidate) => candidate.meetsRequiredFood).length,
    missingIngredientNames: [...missingIngredientNames].sort(),
    missingCookerNames: [...missingCookerNames].sort(),
  };
}

function canRecipeReachRequiredTagWithoutSearch(
  recipe: RecipeCatalogItem,
  catalogIngredients: readonly IngredientCatalogItem[],
  ingredientsByName: Map<string, IngredientCatalogItem>,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
): boolean {
  const baseState = evaluateIngredientState(recipe, [], demand, context);
  if (baseState.meetsRequiredFood) return true;

  const extraSlots = getAvailableExtraIngredientSlots(recipe, context);
  if (extraSlots <= 0) return false;
  const baseIngredientIds = new Set(recipe.ingredients
    .map((name) => ingredientsByName.get(name)?.id ?? -1)
    .filter((id) => id >= 0));
  const allowedCatalogIngredients = catalogIngredients.filter((ingredient) =>
    !baseIngredientIds.has(ingredient.id)
    && !hasForbiddenIngredientTag(ingredient, recipe)
  );
  for (const ingredient of allowedCatalogIngredients) {
    if (evaluateIngredientState(recipe, [ingredient], demand, context).meetsRequiredFood) return true;
  }

  const largePortionExtraCount = MAX_FOOD_INGREDIENT_COUNT - recipe.ingredients.length;
  if (demand.requiredFoodTag !== '大份'
    || largePortionExtraCount <= 1
    || largePortionExtraCount > extraSlots
    || allowedCatalogIngredients.length < largePortionExtraCount) return false;
  return evaluateIngredientState(
    recipe,
    allowedCatalogIngredients.slice(0, largePortionExtraCount),
    demand,
    context,
  ).meetsRequiredFood;
}

/**
 * 构建稀客酒水候选。
 *
 * 酒水没有“加料”搜索，只根据已解锁、排除列表、点单 Tag、稀客偏好和库存压力生成排序信号。
 */
export function buildRareBeverageCandidates(
  data: RecommendationDataSet,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
): BeverageCandidate[] {
  const rows: BeverageCandidate[] = [];
  for (const beverage of data.beverages) {
    if (!context.availableBeverageIds.has(beverage.id)) continue;
    if (context.excludedBeverageIds.has(beverage.id)) continue;
    const resolved = resolveTagPriority(beverage.tags, context.tagPriorityRules);
    const matchedTags = resolved.activeTags.filter((tag) => demand.customer.beverageTags.includes(tag));
    const meetsRequiredBeverage = resolved.activeTags.includes(demand.requiredBeverageTag);
    const conditionResults: ConditionResult[] = [
      {
        id: 'beverage.required-tag',
        target: 'beverage',
        status: meetsRequiredBeverage ? 'pass' : 'warn',
        severity: meetsRequiredBeverage ? 'hard' : 'soft',
        label: '酒水点单',
        detail: meetsRequiredBeverage
          ? `满足点单酒水 ${demand.requiredBeverageTag}`
          : `未满足点单酒水 ${demand.requiredBeverageTag}`,
      },
    ];
    if (matchedTags.length > 0) {
      conditionResults.push({
        id: 'beverage.preference',
        target: 'beverage',
        status: 'boost',
        severity: 'soft',
        label: '酒水偏好',
        detail: `命中 ${matchedTags.join('、')}`,
      });
    }
    rows.push({
      beverage,
      activeTags: resolved.activeTags,
      matchedTags,
      meetsRequiredBeverage,
      ownedQuantity: context.ownedBeverageQty[beverage.id] ?? 0,
      conditionResults,
    });
  }

  return rows.sort(compareBeverageCandidates);
}

/**
 * 酒水候选的只读分阶段计数。筛选条件与 `buildRareBeverageCandidates` 保持一致。
 */
export function diagnoseRareBeverageCandidateSearch(
  data: RecommendationDataSet,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
): RareBeverageCandidateSearchDiagnostic {
  let availableBeverageCount = 0;
  let allowedBeverageCount = 0;
  let requiredTagBeverageCount = 0;

  for (const beverage of data.beverages) {
    if (!context.availableBeverageIds.has(beverage.id)) continue;
    availableBeverageCount += 1;
    if (context.excludedBeverageIds.has(beverage.id)) continue;
    allowedBeverageCount += 1;
    const resolved = resolveTagPriority(beverage.tags, context.tagPriorityRules);
    if (resolved.activeTags.includes(demand.requiredBeverageTag)) {
      requiredTagBeverageCount += 1;
    }
  }

  return {
    catalogBeverageCount: data.beverages.length,
    availableBeverageCount,
    allowedBeverageCount,
    requiredTagBeverageCount,
  };
}

function buildFoodCandidatesForRecipe(
  recipe: RecipeCatalogItem,
  usableIngredients: IngredientCatalogItem[],
  ingredientsByName: Map<string, IngredientCatalogItem>,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
): FoodCandidate[] {
  // extraSlots 使用配方原始材料数量计算，不能用去重后的材料集合，否则重复材料配方会错误地允许继续加料。
  const extraSlots = getAvailableExtraIngredientSlots(recipe, context);
  const baseIngredientIds = new Set(recipe.ingredients
    .map((name) => ingredientsByName.get(name)?.id ?? -1)
    .filter((id) => id >= 0));
  const baseState = evaluateIngredientState(recipe, [], demand, context);
  const ingredientPool = buildRelevantIngredientPool({
    recipe,
    usableIngredients,
    baseState,
    demand,
    baseIngredientIds,
    tagPriorityRules: context.tagPriorityRules,
  });
  const bestStates = searchIngredientStates({
    recipe,
    baseState,
    ingredientPool,
    extraSlots,
    demand,
    context,
  });

  return bestStates.map((state) =>
    buildFoodCandidate(recipe, state, demand, context, ingredientsByName)
  );
}

function buildRarePlan(
  demand: RareTagOrderDemand,
  food: FoodCandidate,
  beverage: BeverageCandidate,
  context: RecommendationRuntimeContext,
): RareOrderRecommendationPlan {
  const estimatedPrice = calculatePlanEstimatedPrice(food, beverage);
  const budget = buildBudgetResult(estimatedPrice, context.budget, context.budgetPolicy);
  const budgetCondition = buildBudgetCondition(budget);
  const conditionResults = [
    ...food.conditionResults,
    ...beverage.conditionResults,
    ...(budgetCondition ? [budgetCondition] : []),
  ];
  const bucket = resolvePlanBucket(food, beverage, conditionResults);
  const reasons = [
    food.meetsRequiredFood ? `料理满足 ${demand.requiredFoodTag}` : '',
    food.matchedSpecialFoodTargetTags.length > 0 ? `特殊目标 ${food.matchedSpecialFoodTargetTags.join('、')}` : '',
    beverage.meetsRequiredBeverage ? `酒水满足 ${demand.requiredBeverageTag}` : '',
    food.matchedPositiveTags.length > 0 ? `料理偏好 ${food.matchedPositiveTags.join('、')}` : '',
    beverage.matchedTags.length > 0 ? `酒水偏好 ${beverage.matchedTags.join('、')}` : '',
  ].filter(Boolean);
  const warnings = conditionResults
    .filter((result) => result.status === 'warn' || result.status === 'fail')
    .map((result) => result.detail);

  return {
    demand,
    food,
    beverage,
    bucket,
    estimatedPrice,
    budget,
    conditionResults,
    reasons,
    warnings,
  };
}

function buildBlockedPlan(
  demand: RareTagOrderDemand,
  food: FoodCandidate | null,
  beverage: BeverageCandidate | null,
  context: RecommendationRuntimeContext,
  data: RecommendationDataSet,
): RareOrderRecommendationPlan {
  const conditionResults: ConditionResult[] = [];
  if (!food) {
    conditionResults.push(...buildMissingFoodConditions(context, data));
    conditionResults.push({
      id: 'plan.missing-food',
      target: 'plan',
      status: 'fail',
      severity: 'hard',
      label: '料理方案',
      detail: '没有可执行的料理候选。',
    });
  }
  if (!beverage) {
    conditionResults.push(...buildMissingBeverageConditions(context, data));
    conditionResults.push({
      id: 'plan.missing-beverage',
      target: 'plan',
      status: 'fail',
      severity: 'hard',
      label: '酒水方案',
      detail: '没有可执行的酒水候选。',
    });
  }

  return {
    demand,
    food,
    beverage,
    bucket: 'blocked',
    estimatedPrice: food && beverage ? calculatePlanEstimatedPrice(food, beverage) : 0,
    budget: null,
    conditionResults,
    reasons: [],
    warnings: conditionResults.map((result) => result.detail),
  };
}

function resolvePlanBucket(
  food: FoodCandidate,
  beverage: BeverageCandidate,
  results: ConditionResult[],
): RecommendationBucket {
  if (results.some((result) => result.status === 'fail' && result.severity === 'hard')) return 'blocked';
  if (food.meetsRequiredFood && beverage.meetsRequiredBeverage) {
    return results.some((result) => result.status === 'warn') ? 'tradeoff' : 'complete';
  }
  return 'preference';
}

function hasAvailableBaseIngredients(
  recipe: RecipeCatalogItem,
  ingredientsByName: Map<string, IngredientCatalogItem>,
  context: RecommendationRuntimeContext,
): boolean {
  return recipe.ingredients.every((name) => {
    const ingredient = ingredientsByName.get(name);
    return ingredient !== undefined
      && context.availableIngredientIds.has(ingredient.id)
      && !isIngredientExcluded(ingredient.id, context);
  });
}

function findUnavailableBaseIngredientNames(
  recipe: RecipeCatalogItem,
  ingredientsByName: Map<string, IngredientCatalogItem>,
  context: RecommendationRuntimeContext,
): string[] {
  return [...new Set(recipe.ingredients.filter((name) => {
    const ingredient = ingredientsByName.get(name);
    return ingredient === undefined
      || !context.availableIngredientIds.has(ingredient.id)
      || isIngredientExcluded(ingredient.id, context);
  }))];
}

function isIngredientExcluded(id: number, context: RecommendationRuntimeContext): boolean {
  return context.disabledIngredientIds.has(id) || context.excludedIngredientIds.has(id);
}

function isCookerAvailable(recipe: RecipeCatalogItem, context: RecommendationRuntimeContext): boolean {
  if (!context.hasCookerSnapshot) return true;
  return context.placedCookerNames.has(recipe.cooker);
}

function getAvailableExtraIngredientSlots(
  recipe: RecipeCatalogItem,
  context: RecommendationRuntimeContext,
): number {
  // 游戏本身最多允许五种材料。这里必须使用配方原始材料数量，重复材料也会占用真实槽位。
  const remainingRecipeSlots = MAX_FOOD_INGREDIENT_COUNT - recipe.ingredients.length;
  return Math.max(0, Math.min(remainingRecipeSlots, context.maxExtraIngredients));
}

/**
 * 缩小额外食材搜索池。
 *
 * 全量材料组合会迅速膨胀；这里仅保留能满足点单 Tag、稀客正向偏好，或能压制负面 Tag 的食材。
 * 禁用/排除材料和与配方冲突的禁忌 Tag 已在进入搜索前过滤。
 */
function buildRelevantIngredientPool({
  recipe,
  usableIngredients,
  baseState,
  demand,
  baseIngredientIds,
  tagPriorityRules,
}: {
  recipe: RecipeCatalogItem;
  usableIngredients: IngredientCatalogItem[];
  baseState: IngredientSearchState;
  demand: RareTagOrderDemand;
  baseIngredientIds: Set<number>;
  tagPriorityRules: RecommendationRuntimeContext['tagPriorityRules'];
}): IngredientCatalogItem[] {
  const usefulTags = new Set([
    demand.requiredFoodTag,
    ...demand.customer.positiveTags,
    ...getSpecialFoodTargetTags(demand),
    ...findTagsThatCanSuppress(baseState.activeTags, demand.customer.negativeTags, tagPriorityRules),
  ]);

  return usableIngredients
    .filter((ingredient) => !baseIngredientIds.has(ingredient.id))
    .filter((ingredient) => !hasForbiddenIngredientTag(ingredient, recipe))
    .filter((ingredient) => ingredient.tags.some((tag) => usefulTags.has(tag)))
    .sort((left, right) => left.id - right.id);
}

function searchIngredientStates({
  recipe,
  baseState,
  ingredientPool,
  extraSlots,
  demand,
  context,
}: {
  recipe: RecipeCatalogItem;
  baseState: IngredientSearchState;
  ingredientPool: IngredientCatalogItem[];
  extraSlots: number;
  demand: RareTagOrderDemand;
  context: RecommendationRuntimeContext;
}): IngredientSearchState[] {
  const states = new Map<string, IngredientSearchState>([[stateKey(baseState), baseState]]);
  let frontier = [baseState];

  for (let depth = 1; depth <= extraSlots; depth++) {
    const expanded: IngredientSearchState[] = [];
    for (const state of frontier) {
      const used = new Set(state.ingredients.map((ingredient) => ingredient.id));
      for (const ingredient of ingredientPool) {
        if (used.has(ingredient.id)) continue;
        expanded.push(evaluateIngredientState(recipe, [...state.ingredients, ingredient], demand, context));
      }
    }

    if (expanded.length === 0) break;
    // Beam search 保留每层最优的一小批状态，兼顾推荐质量和经营中多订单实时计算性能。
    const nextFrontier = keepBestStates(expanded, DEFAULT_BEAM_WIDTH);
    for (const state of nextFrontier) states.set(stateKey(state), state);
    frontier = nextFrontier;
  }

  return keepBestStates([...states.values()], 16);
}

function evaluateIngredientState(
  recipe: RecipeCatalogItem,
  extraIngredients: IngredientCatalogItem[],
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
): IngredientSearchState {
  const resolved = resolveFoodTags({
    recipe,
    extraIngredients,
    popularFoodTag: context.popularFoodTag,
    popularHateFoodTag: context.popularHateFoodTag,
    famousShopEnabled: context.famousShopEnabled,
    tagPriorityRules: context.tagPriorityRules,
  });
  const matchedPositiveTags = resolved.activeTags.filter((tag) => demand.customer.positiveTags.includes(tag));
  const matchedNegativeTags = resolved.activeTags.filter((tag) => demand.customer.negativeTags.includes(tag));
  const matchedSpecialFoodTargetTags = getSpecialFoodTargetTags(demand)
    .filter((tag) => resolved.activeTags.includes(tag));
  const meetsSpecialFoodTarget = getSpecialFoodTargetTags(demand).length === 0 || matchedSpecialFoodTargetTags.length > 0;
  return {
    ingredients: extraIngredients,
    activeTags: resolved.activeTags,
    suppressedTags: resolved.suppressedTags,
    matchedPositiveTags,
    matchedNegativeTags,
    matchedSpecialFoodTargetTags,
    meetsSpecialFoodTarget,
    meetsRequiredFood: resolved.activeTags.includes(demand.requiredFoodTag),
    extraCost: extraIngredients.reduce((sum, ingredient) => sum + ingredient.price, 0),
    resourcePressure: calculateResourcePressure(extraIngredients, context.ownedIngredientQty),
  };
}

function buildFoodCandidate(
  recipe: RecipeCatalogItem,
  state: IngredientSearchState,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
  ingredientsByName: Map<string, IngredientCatalogItem>,
): FoodCandidate {
  const baseCost = recipe.ingredients.reduce((sum, name) => {
    const ingredient = ingredientsByName.get(name);
    return sum + (ingredient?.price ?? 0);
  }, 0);
  const cookerAvailable = isCookerAvailable(recipe, context);
  const conditionResults = buildFoodConditionResults(recipe, state, demand, cookerAvailable);

  return {
    recipe,
    extraIngredients: state.ingredients,
    extraIngredientReasonTags: buildExtraIngredientReasons(state.ingredients, demand),
    activeTags: state.activeTags,
    suppressedTags: state.suppressedTags,
    matchedPositiveTags: state.matchedPositiveTags,
    matchedNegativeTags: state.matchedNegativeTags,
    matchedSpecialFoodTargetTags: state.matchedSpecialFoodTargetTags,
    meetsRequiredFood: state.meetsRequiredFood,
    baseCost,
    extraCost: state.extraCost,
    resourcePressure: state.resourcePressure,
    cookerAvailable,
    conditionResults,
  };
}

function buildFoodConditionResults(
  recipe: RecipeCatalogItem,
  state: IngredientSearchState,
  demand: RareTagOrderDemand,
  cookerAvailable: boolean,
): ConditionResult[] {
  const specialTargetTags = getSpecialFoodTargetTags(demand);
  const results: ConditionResult[] = [
    {
      id: 'food.required-tag',
      target: 'food',
      status: state.meetsRequiredFood ? 'pass' : 'warn',
      severity: state.meetsRequiredFood ? 'hard' : 'soft',
      label: '料理点单',
      detail: state.meetsRequiredFood
        ? `满足点单料理 ${demand.requiredFoodTag}`
        : `未满足点单料理 ${demand.requiredFoodTag}`,
    },
  ];

  if (specialTargetTags.length > 0) {
    results.push({
      id: 'food.special-target-tag',
      target: 'food',
      status: state.meetsSpecialFoodTarget ? 'pass' : 'fail',
      severity: 'hard',
      label: '特殊目标 Tag',
      detail: state.meetsSpecialFoodTarget
        ? `满足特殊目标 Tag ${state.matchedSpecialFoodTargetTags.join('、')}`
        : `未满足特殊目标 Tag ${specialTargetTags.join('、')}`,
    });
  }

  if (!cookerAvailable) {
    results.push({
      id: 'food.cooker',
      target: 'food',
      status: 'fail',
      severity: 'hard',
      label: '厨具',
      detail: `缺少厨具 ${recipe.cooker || '未知'}`,
    });
  }
  if (state.matchedPositiveTags.length > 0) {
    results.push({
      id: 'food.preference',
      target: 'food',
      status: 'boost',
      severity: 'soft',
      label: '料理偏好',
      detail: `命中 ${state.matchedPositiveTags.join('、')}`,
    });
  }
  if (state.matchedNegativeTags.length > 0) {
    results.push({
      id: 'food.negative-tags',
      target: 'food',
      status: 'warn',
      severity: 'soft',
      label: '厌恶标签',
      detail: `包含 ${state.matchedNegativeTags.join('、')}`,
    });
  }
  if (state.suppressedTags.length > 0) {
    results.push({
      id: 'food.suppressed-tags',
      target: 'food',
      status: 'info',
      severity: 'info',
      label: '标签优先级',
      detail: `压制 ${state.suppressedTags.join('、')}`,
    });
  }

  return results;
}

function calculatePlanEstimatedPrice(food: FoodCandidate, beverage: BeverageCandidate): number {
  return Math.max(0, food.recipe.price) + Math.max(0, beverage.beverage.price);
}

function buildBudgetResult(
  estimatedPrice: number,
  budget: RecommendationBudgetContext | null,
  policy: RecommendationBudgetPolicy,
): RecommendationBudgetResult | null {
  if (!budget) return null;
  const remainingBudget = Number.isFinite(budget.remainingBudget) ? Math.max(0, Math.trunc(budget.remainingBudget ?? 0)) : null;
  return {
    estimatedPrice,
    remainingBudget,
    overBudget: remainingBudget === null ? 0 : Math.max(0, estimatedPrice - remainingBudget),
    policy,
    source: budget.source,
    willPayMoney: budget.willPayMoney,
  };
}

function buildBudgetCondition(budget: RecommendationBudgetResult | null): ConditionResult | null {
  if (!budget) return null;
  if (budget.policy === 'ignore') {
    return {
      id: 'plan.budget',
      target: 'plan',
      status: 'info',
      severity: 'info',
      label: '预算',
      detail: buildBudgetDetail(budget, '预算约束未启用'),
    };
  }
  if (budget.willPayMoney === false) {
    return {
      id: 'plan.budget',
      target: 'plan',
      status: budget.policy === 'block' ? 'fail' : 'warn',
      severity: budget.policy === 'block' ? 'hard' : 'soft',
      label: '预算',
      detail: '稀客当前不会付款。',
    };
  }
  if (budget.remainingBudget === null) {
    return {
      id: 'plan.budget',
      target: 'plan',
      status: 'info',
      severity: 'info',
      label: '预算',
      detail: `预算未知，方案预估 ${budget.estimatedPrice}。`,
    };
  }
  if (budget.overBudget > 0) {
    return {
      id: 'plan.budget',
      target: 'plan',
      status: budget.policy === 'block' ? 'fail' : 'warn',
      severity: budget.policy === 'block' ? 'hard' : 'soft',
      label: '预算',
      detail: buildBudgetDetail(budget, `超出预算 ${budget.overBudget}`),
    };
  }

  return {
    id: 'plan.budget',
    target: 'plan',
    status: 'pass',
    severity: 'hard',
    label: '预算',
    detail: buildBudgetDetail(budget, '未超预算'),
  };
}

function buildBudgetDetail(budget: RecommendationBudgetResult, prefix: string): string {
  const remaining = budget.remainingBudget === null ? '未知' : String(budget.remainingBudget);
  return `${prefix}，预估 ${budget.estimatedPrice} / 剩余预算 ${remaining}。`;
}

function buildMissingFoodConditions(
  context: RecommendationRuntimeContext,
  data: RecommendationDataSet,
): ConditionResult[] {
  if (context.excludedIngredientIds.size === 0) return [];
  const ingredientsById = new Map(data.ingredients.map((ingredient) => [ingredient.id, ingredient]));
  const names = [...context.excludedIngredientIds]
    .map((id) => ingredientsById.get(id)?.name ?? `#${id}`)
    .filter(Boolean);

  return [{
    id: 'food.excluded-ingredients',
    target: 'food',
    status: 'fail',
    severity: 'hard',
    label: '排除材料',
    detail: `没有可用料理能避开排除材料：${names.join('、')}。`,
  }];
}

function buildMissingBeverageConditions(
  context: RecommendationRuntimeContext,
  data: RecommendationDataSet,
): ConditionResult[] {
  if (context.excludedBeverageIds.size === 0) return [];
  const beveragesById = new Map(data.beverages.map((beverage) => [beverage.id, beverage]));
  const names = [...context.excludedBeverageIds]
    .map((id) => beveragesById.get(id)?.name ?? `#${id}`)
    .filter(Boolean);

  return [{
    id: 'beverage.excluded',
    target: 'beverage',
    status: 'fail',
    severity: 'hard',
    label: '排除酒水',
    detail: `没有可用酒水能避开排除酒水：${names.join('、')}。`,
  }];
}

function buildExtraIngredientReasons(
  extraIngredients: IngredientCatalogItem[],
  demand: RareTagOrderDemand,
): Record<number, string[]> {
  const result: Record<number, string[]> = {};
  const relevantTags = new Set([
    demand.requiredFoodTag,
    ...demand.customer.positiveTags,
    ...getSpecialFoodTargetTags(demand),
  ]);
  for (const ingredient of extraIngredients) {
    const reasons = ingredient.tags.filter((tag) => relevantTags.has(tag));
    if (reasons.length > 0) result[ingredient.id] = reasons;
  }
  return result;
}

function getSpecialFoodTargetTags(demand: RareTagOrderDemand): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const tag of demand.specialFoodTargetTags ?? []) {
    const text = tag.trim();
    if (!text || seen.has(text)) continue;
    seen.add(text);
    result.push(text);
  }
  return result;
}

function calculateResourcePressure(
  ingredients: IngredientCatalogItem[],
  ownedIngredientQty: Record<number, number>,
): number {
  return ingredients.reduce((sum, ingredient) => {
    const qty = ownedIngredientQty[ingredient.id] ?? 0;
    return sum + inventoryShortage(qty, LOW_STOCK_THRESHOLD);
  }, 0);
}

function stateKey(state: IngredientSearchState): string {
  return state.ingredients.map((ingredient) => ingredient.id).sort((left, right) => left - right).join(',');
}

function keepBestStates(states: IngredientSearchState[], limit: number): IngredientSearchState[] {
  return [...new Map(states.map((state) => [stateKey(state), state])).values()]
    .sort(compareIngredientStates)
    .slice(0, limit);
}

function compareIngredientStates(left: IngredientSearchState, right: IngredientSearchState): number {
  const leftSpecial = left.meetsSpecialFoodTarget ? 1 : 0;
  const rightSpecial = right.meetsSpecialFoodTarget ? 1 : 0;
  if (leftSpecial !== rightSpecial) return rightSpecial - leftSpecial;
  const leftRequired = left.meetsRequiredFood ? 1 : 0;
  const rightRequired = right.meetsRequiredFood ? 1 : 0;
  if (leftRequired !== rightRequired) return rightRequired - leftRequired;
  if (left.matchedNegativeTags.length !== right.matchedNegativeTags.length) {
    return left.matchedNegativeTags.length - right.matchedNegativeTags.length;
  }
  if (left.matchedPositiveTags.length !== right.matchedPositiveTags.length) {
    return right.matchedPositiveTags.length - left.matchedPositiveTags.length;
  }
  if (left.ingredients.length !== right.ingredients.length) return left.ingredients.length - right.ingredients.length;
  if (left.resourcePressure !== right.resourcePressure) return left.resourcePressure - right.resourcePressure;
  if (left.extraCost !== right.extraCost) return left.extraCost - right.extraCost;
  return stateKey(left).localeCompare(stateKey(right));
}

export function compareFoodCandidates(left: FoodCandidate, right: FoodCandidate): number {
  if (left.matchedSpecialFoodTargetTags.length !== right.matchedSpecialFoodTargetTags.length) {
    return right.matchedSpecialFoodTargetTags.length - left.matchedSpecialFoodTargetTags.length;
  }
  const leftRequired = left.meetsRequiredFood ? 1 : 0;
  const rightRequired = right.meetsRequiredFood ? 1 : 0;
  if (leftRequired !== rightRequired) return rightRequired - leftRequired;
  if (left.matchedNegativeTags.length !== right.matchedNegativeTags.length) {
    return left.matchedNegativeTags.length - right.matchedNegativeTags.length;
  }
  if (left.matchedPositiveTags.length !== right.matchedPositiveTags.length) {
    return right.matchedPositiveTags.length - left.matchedPositiveTags.length;
  }
  if (left.extraIngredients.length !== right.extraIngredients.length) {
    return left.extraIngredients.length - right.extraIngredients.length;
  }
  if (left.resourcePressure !== right.resourcePressure) return left.resourcePressure - right.resourcePressure;
  const leftTotalCost = left.baseCost + left.extraCost;
  const rightTotalCost = right.baseCost + right.extraCost;
  if (leftTotalCost !== rightTotalCost) return leftTotalCost - rightTotalCost;
  return left.recipe.id - right.recipe.id;
}

export function compareBeverageCandidates(left: BeverageCandidate, right: BeverageCandidate): number {
  const leftRequired = left.meetsRequiredBeverage ? 1 : 0;
  const rightRequired = right.meetsRequiredBeverage ? 1 : 0;
  if (leftRequired !== rightRequired) return rightRequired - leftRequired;
  if (left.matchedTags.length !== right.matchedTags.length) return right.matchedTags.length - left.matchedTags.length;
  const stockDiff = inventoryQuantityRankValue(right.ownedQuantity)
    - inventoryQuantityRankValue(left.ownedQuantity);
  if (stockDiff !== 0) return stockDiff;
  if (left.beverage.price !== right.beverage.price) return right.beverage.price - left.beverage.price;
  return left.beverage.id - right.beverage.id;
}

function compareRarePlans(
  left: RareOrderRecommendationPlan,
  right: RareOrderRecommendationPlan,
  profile: RecommendationSortProfile,
  sortContext: RecommendationPlanSortContext,
  ranges: Map<RecommendationObjectiveKey, ObjectiveRange>,
): number {
  // 强制置顶和方案分桶属于硬优先级，必须先于权重分数，避免收藏或自定义料理置顶被权重冲掉。
  const pinDiff = getPlanPinRank(right, sortContext) - getPlanPinRank(left, sortContext);
  if (pinDiff !== 0) return pinDiff;
  const customOrderDiff = comparePinnedCustomPlanOrder(left, right);
  if (customOrderDiff !== 0) return customOrderDiff;
  const bucketDiff = getBucketRank(right.bucket) - getBucketRank(left.bucket);
  if (bucketDiff !== 0) return bucketDiff;
  if (left.warnings.length !== right.warnings.length) return left.warnings.length - right.warnings.length;
  const specialBusinessDiff = compareSpecialBusinessPlanPriority(left, right, sortContext);
  if (specialBusinessDiff !== 0) return specialBusinessDiff;
  const scoreDiff = calculatePlanScore(right, profile, ranges)
    - calculatePlanScore(left, profile, ranges);
  if (scoreDiff !== 0) return scoreDiff;
  if (left.food && right.food) {
    const foodDiff = compareFoodCandidates(left.food, right.food);
    if (foodDiff !== 0) return foodDiff;
  }
  if (left.beverage && right.beverage) {
    const beverageDiff = compareBeverageCandidates(left.beverage, right.beverage);
    if (beverageDiff !== 0) return beverageDiff;
  }
  return 0;
}

interface ObjectiveRange {
  min: number;
  max: number;
}

function buildObjectiveRanges(
  plans: RareOrderRecommendationPlan[],
): Map<RecommendationObjectiveKey, ObjectiveRange> {
  const ranges = new Map<RecommendationObjectiveKey, ObjectiveRange>();
  // 权重分数按当前候选集归一化，不使用固定全局范围，避免某一项数值量纲过大压制其他目标。
  const keys: RecommendationObjectiveKey[] = [
    'foodPreference',
    'beveragePreference',
    'negativeRisk',
    'extraCount',
    'resourcePressure',
    'totalCost',
    'profit',
    'beverageStock',
    'cookerAvailable',
  ];

  for (const key of keys) {
    const values = plans.map((plan) => getPlanObjectiveValue(plan, key));
    if (values.length === 0) {
      ranges.set(key, { min: 0, max: 0 });
      continue;
    }
    ranges.set(key, {
      min: Math.min(...values),
      max: Math.max(...values),
    });
  }

  return ranges;
}

function calculatePlanScore(
  plan: RareOrderRecommendationPlan,
  profile: RecommendationSortProfile,
  ranges: Map<RecommendationObjectiveKey, ObjectiveRange>,
): number {
  return profile.objectives.reduce((sum, rule) => {
    if (!rule.enabled || rule.weight <= 0) return sum;
    const range = ranges.get(rule.key);
    const rawValue = getPlanObjectiveValue(plan, rule.key);
    return sum + normalizeObjectiveValue(rawValue, range, rule.direction) * rule.weight;
  }, 0);
}

function normalizeObjectiveValue(
  value: number,
  range: ObjectiveRange | undefined,
  direction: 'asc' | 'desc',
): number {
  if (!range || range.max === range.min) return 0;
  const normalized = (value - range.min) / (range.max - range.min);
  return direction === 'desc' ? normalized : 1 - normalized;
}

function getPlanObjectiveValue(
  plan: RareOrderRecommendationPlan,
  key: RecommendationObjectiveKey,
): number {
  const food = plan.food;
  const beverage = plan.beverage;

  switch (key) {
    case 'foodPreference':
      return food?.matchedPositiveTags.length ?? 0;
    case 'beveragePreference':
      return beverage?.matchedTags.length ?? 0;
    case 'negativeRisk':
      return food?.matchedNegativeTags.length ?? 0;
    case 'extraCount':
      return food?.extraIngredients.length ?? 0;
    case 'resourcePressure':
      return food?.resourcePressure ?? 0;
    case 'totalCost':
      return food ? food.baseCost + food.extraCost : 0;
    case 'profit':
      return (food ? food.recipe.price - food.baseCost - food.extraCost : 0)
        + (beverage?.beverage.price ?? 0);
    case 'beverageStock':
      return beverage ? inventoryQuantityRankValue(beverage.ownedQuantity) : 0;
    case 'cookerAvailable':
      return food?.cookerAvailable ? 1 : 0;
  }
}

function getPlanPinRank(
  plan: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
): number {
  if (plan.bucket === 'blocked') return 0;
  let rank = 0;
  if (sortContext.specialPreferDamageLevel) rank = Math.max(rank, getPlanDamagePinRank(plan));
  if (isMissionRecipeExecutionPlan(plan, sortContext)) rank = Math.max(rank, 50);
  if (plan.food?.customRecipePinned) rank = Math.max(rank, 40);
  if (sortContext.pinFavoriteRecipe && plan.food && sortContext.favoriteRecipeKeys?.has(buildPlanRecipeKey(plan.food))) rank = Math.max(rank, 20);
  if (sortContext.pinFavoriteBeverage && plan.beverage && sortContext.favoriteBeverageIds?.has(plan.beverage.beverage.id)) rank = Math.max(rank, 20);
  return rank;
}

function getPlanSpecialBusinessRank(
  plan: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
): number {
  return getFoodSpecialBusinessRank(plan.food, sortContext)
    + getBeverageSpecialBusinessRank(plan.beverage, sortContext);
}

function getFoodSpecialBusinessRank(
  food: FoodCandidate | null,
  sortContext: RecommendationPlanSortContext,
): number {
  if (!food || !sortContext.specialTargetFoodTags || sortContext.specialTargetFoodTags.size === 0) return 0;
  return countTagMatches(food.activeTags, sortContext.specialTargetFoodTags);
}

function getBeverageSpecialBusinessRank(
  beverage: BeverageCandidate | null,
  sortContext: RecommendationPlanSortContext,
): number {
  if (!beverage || !sortContext.specialTargetBeverageTags || sortContext.specialTargetBeverageTags.size === 0) return 0;
  return countTagMatches(beverage.activeTags, sortContext.specialTargetBeverageTags);
}

function countTagMatches(tags: string[], targetTags: Set<string>): number {
  return tags.reduce((count, tag) => count + (targetTags.has(tag) ? 1 : 0), 0);
}

function compareSpecialBusinessPlanPriority(
  left: RareOrderRecommendationPlan,
  right: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
): number {
  const rankDiff = getPlanSpecialBusinessRank(right, sortContext) - getPlanSpecialBusinessRank(left, sortContext);
  if (rankDiff !== 0) return rankDiff;

  if (sortContext.specialPreferDamageLevel) {
    const negativeDiff = (left.food?.matchedNegativeTags.length ?? 0) - (right.food?.matchedNegativeTags.length ?? 0);
    if (negativeDiff !== 0) return negativeDiff;
    const budgetFitDiff = getPlanBudgetFitRank(right) - getPlanBudgetFitRank(left);
    if (budgetFitDiff !== 0) return budgetFitDiff;
    const koishiBudgetPlanDiff = compareKoishiFeedBudgetPlanPriority(left, right, sortContext);
    if (koishiBudgetPlanDiff !== 0) return koishiBudgetPlanDiff;
    const levelDiff = getPlanDamageLevel(right) - getPlanDamageLevel(left);
    if (levelDiff !== 0) return levelDiff;
    const damageProxyDiff = getPlanDamageProxyScore(right) - getPlanDamageProxyScore(left);
    if (damageProxyDiff !== 0) return damageProxyDiff;
    const preferenceDiff = getHighEvaluationPreferenceScore(right) - getHighEvaluationPreferenceScore(left);
    if (preferenceDiff !== 0) return preferenceDiff;
    const budgetEfficiencyDiff = getPlanBudgetEfficiencyTieScore(right) - getPlanBudgetEfficiencyTieScore(left);
    if (budgetEfficiencyDiff !== 0) return budgetEfficiencyDiff;
    const priceDiff = left.estimatedPrice - right.estimatedPrice;
    if (priceDiff !== 0) return priceDiff;
  }

  if (sortContext.specialPreferHighFoodLevel || sortContext.specialPreferHighBeverageLevel) {
    const negativeDiff = (left.food?.matchedNegativeTags.length ?? 0) - (right.food?.matchedNegativeTags.length ?? 0);
    if (negativeDiff !== 0) return negativeDiff;
    const preferenceDiff = getHighEvaluationPreferenceScore(right) - getHighEvaluationPreferenceScore(left);
    if (preferenceDiff !== 0) return preferenceDiff;
  }

  if (sortContext.specialPreferHighFoodLevel) {
    const diff = (right.food?.recipe.level ?? 0) - (left.food?.recipe.level ?? 0);
    if (diff !== 0) return diff;
  }

  if (sortContext.specialPreferHighBeverageLevel) {
    const diff = (right.beverage?.beverage.level ?? 0) - (left.beverage?.beverage.level ?? 0);
    if (diff !== 0) return diff;
  }

  return 0;
}

function getPlanDamageLevel(plan: RareOrderRecommendationPlan): number {
  return (plan.food?.recipe.level ?? 0) + (plan.beverage?.beverage.level ?? 0);
}

function getPlanBudgetFitRank(plan: RareOrderRecommendationPlan): number {
  const remainingBudget = plan.budget?.remainingBudget;
  if (remainingBudget == null) return 0;
  return plan.budget && plan.budget.overBudget <= 0 ? 1 : -1;
}

function getPlanDamageProxyScore(plan: RareOrderRecommendationPlan): number {
  const negativePenalty = (plan.food?.matchedNegativeTags.length ?? 0) * 100_000;
  return getPlanKoishiFeedScore(plan) * 10_000
    + getHighEvaluationPreferenceScore(plan) * 10
    - Math.max(0, plan.estimatedPrice)
    - negativePenalty;
}

function getPlanBudgetEfficiencyTieScore(plan: RareOrderRecommendationPlan): number {
  const feedScore = getPlanKoishiFeedScore(plan);
  if (feedScore <= 0) return 0;
  const price = Math.max(1, plan.estimatedPrice);
  const remainingBudget = plan.budget?.remainingBudget;
  if (remainingBudget == null) return 0;

  if (plan.budget && plan.budget.overBudget > 0) return -plan.budget.overBudget * 1_000;
  return Math.round((feedScore * 100_000) / price);
}

function getPlanDamagePinRank(plan: RareOrderRecommendationPlan): number {
  if ((plan.food?.matchedNegativeTags.length ?? 0) > 0) return 0;
  return 10_000;
}

function getHighEvaluationPreferenceScore(plan: RareOrderRecommendationPlan): number {
  return (plan.food?.matchedPositiveTags.length ?? 0)
    + (plan.beverage?.matchedTags.length ?? 0);
}

function compareKoishiFeedBudgetPlanPriority(
  left: RareOrderRecommendationPlan,
  right: RareOrderRecommendationPlan,
  sortContext: RecommendationPlanSortContext,
): number {
  const remainingScore = normalizePositiveInt(sortContext.specialKoishiRemainingScore);
  if (remainingScore == null) return 0;

  const leftInfo = buildKoishiFeedBudgetPlanInfo(left, remainingScore, sortContext.specialKoishiRemainingOrderCount);
  const rightInfo = buildKoishiFeedBudgetPlanInfo(right, remainingScore, sortContext.specialKoishiRemainingOrderCount);
  if (!leftInfo || !rightInfo) return 0;

  const sustainableDiff = Number(rightInfo.sustainable) - Number(leftInfo.sustainable);
  if (sustainableDiff !== 0) return sustainableDiff;

  const completionDiff = Number(rightInfo.completesTarget) - Number(leftInfo.completesTarget);
  if (completionDiff !== 0) return completionDiff;

  const attemptFloorDiff = Number(rightInfo.meetsAttemptFloor) - Number(leftInfo.meetsAttemptFloor);
  if (attemptFloorDiff !== 0) return attemptFloorDiff;

  if (leftInfo.sustainable && rightInfo.sustainable) {
    const scoreDiff = rightInfo.feedScore - leftInfo.feedScore;
    if (scoreDiff !== 0) return scoreDiff;
  }

  const efficiencyDiff = rightInfo.efficiency - leftInfo.efficiency;
  if (Math.abs(efficiencyDiff) >= 1) return efficiencyDiff;

  const scoreDiff = rightInfo.feedScore - leftInfo.feedScore;
  if (scoreDiff !== 0) return scoreDiff;

  return left.estimatedPrice - right.estimatedPrice;
}

function buildKoishiFeedBudgetPlanInfo(
  plan: RareOrderRecommendationPlan,
  remainingScore: number,
  remainingOrderCount: number | null | undefined,
): {
  feedScore: number;
  efficiency: number;
  sustainable: boolean;
  completesTarget: boolean;
  meetsAttemptFloor: boolean;
} | null {
  const remainingBudget = plan.budget?.remainingBudget;
  if (remainingBudget == null || remainingBudget <= 0 || plan.bucket === 'blocked') return null;
  const feedScore = getPlanKoishiFeedScore(plan);
  const planning = buildKoishiFeedPlanningInfo({ remainingScore, remainingBudget, remainingOrderCount });
  const meetsAttemptFloor = planning.requiredScoreThisOrder == null || feedScore >= planning.requiredScoreThisOrder;
  if (feedScore <= 0) return null;
  const estimatedPrice = Math.max(0, plan.estimatedPrice);
  if (estimatedPrice > remainingBudget) {
    return {
      feedScore,
      efficiency: -Math.max(0, estimatedPrice - remainingBudget),
      sustainable: false,
      completesTarget: false,
      meetsAttemptFloor,
    };
  }
  const completesTarget = feedScore >= remainingScore;
  const sustainable = isKoishiFeedPlanSustainable({
    estimatedPrice,
    estimatedFeedScore: feedScore,
    remainingBudget,
    remainingScore,
    remainingOrderCount,
  });
  return {
    feedScore,
    efficiency: Math.round((feedScore * 1_000_000) / Math.max(1, estimatedPrice)),
    sustainable,
    completesTarget,
    meetsAttemptFloor,
  };
}

function getPlanKoishiFeedScore(plan: RareOrderRecommendationPlan): number {
  return estimateKoishiBrokenShieldFeedScore({
    meetsRequiredFood: plan.food?.meetsRequiredFood === true,
    meetsRequiredBeverage: plan.beverage?.meetsRequiredBeverage === true,
    preferenceMatches: getHighEvaluationPreferenceScore(plan),
    negativeMatches: plan.food?.matchedNegativeTags.length ?? 0,
    foodLevel: plan.food?.recipe.level,
    beverageLevel: plan.beverage?.beverage.level,
    foodPrice: plan.food?.recipe.price,
    beveragePrice: plan.beverage?.beverage.price,
    estimatedPrice: plan.estimatedPrice,
  });
}

function normalizePositiveInt(value: number | null | undefined): number | null {
  if (!Number.isFinite(value)) return null;
  const normalized = Math.trunc(value ?? 0);
  return normalized > 0 ? normalized : null;
}

function getPlanCustomSortOrder(plan: RareOrderRecommendationPlan): number {
  return plan.food?.customRecipePinned ? plan.food.customRecipeSortOrder ?? Number.MAX_SAFE_INTEGER : Number.MAX_SAFE_INTEGER;
}

function comparePinnedCustomPlanOrder(left: RareOrderRecommendationPlan, right: RareOrderRecommendationPlan): number {
  if (!left.food?.customRecipePinned || !right.food?.customRecipePinned) return 0;
  return getPlanCustomSortOrder(left) - getPlanCustomSortOrder(right);
}

function buildPlanRecipeKey(food: FoodCandidate): string {
  const extraIds = food.extraIngredients
    .map((ingredient) => ingredient.id)
    .sort((left, right) => left - right)
    .join(',');
  return `${food.recipe.id}:${extraIds}`;
}

function getBucketRank(bucket: RecommendationBucket): number {
  switch (bucket) {
    case 'complete':
      return 4;
    case 'tradeoff':
      return 3;
    case 'preference':
      return 2;
    case 'blocked':
      return 1;
  }
}
