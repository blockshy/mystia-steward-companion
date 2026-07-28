import { buildRuntimeSets } from '@/companion/domain/cookers';
import {
  buildCustomFoodCandidates,
  mergeCustomFoodCandidates,
  serializeCustomRecipeContext,
} from '@/companion/domain/custom-recipes';
import { normalizeIdList, recipeResultKey } from '@/companion/domain/favorites';
import {
  buildPrimaryExecutionPlanPolicy,
  getPrimaryExecutionPlan,
  normalizePrimaryExecutionPlans,
  serializePrimaryExecutionPlanPolicy,
} from '@/companion/domain/primary-execution-plan';
import {
  buildKoishiBrokenShieldPlanReason,
  compareKoishiBrokenShieldRecommendationPlans,
  getKoishiRemainingTargetScore,
} from '@/companion/domain/special-business/koishi-boss';
import {
  buildYuyukoProgressBlockedMessages,
  buildYuyukoPlanReason,
  compareYuyukoPlans,
  isYuyukoProgressPlan,
} from '@/companion/domain/special-business/yuyuko-challenge';
import {
  buildYuyukoPositiveSpellBlockedMessages,
  buildYuyukoPositiveSpellPlanReason,
  compareYuyukoPositiveSpellPlans,
  getYuyukoPositiveSpellBeverageCandidateRank,
  getYuyukoPositiveSpellFoodCandidateRank,
  getYuyukoPositiveSpellNegativeTags,
  isYuyukoPositiveSpellPlan,
} from '@/companion/domain/special-business/yuyuko-positive-spell';
import { sortNightOrders } from '@/companion/domain/sorting';
import {
  MAX_FOCUS_RECOMMENDATION_ROWS,
  type CompanionPreferences,
} from '@/companion/preferences';
import {
  cappedInventoryQuantityRank,
  inventoryQuantityRankValue,
} from '@/lib/inventory-quantity';
import {
  buildSpecialBusinessOrderRule,
  buildWackyRejectedRecipeKeyForRareRecipe,
  hasMatchingSpecialBusinessTag,
  normalizeSpecialBusinessTags,
} from '@/companion/domain/special-business';
import type {
  CachedRecommendation,
  CustomRecipeData,
  FavoriteData,
  NightBusinessGuest,
  NightBusinessOrder,
  OrderRecommendation,
  RecommendationBlockedDiagnostic,
  RecommendationCandidateStageCounts,
  RecommendationIssue,
  RecommendationStateSnapshot,
  RuntimeSets,
  SpecialBusinessContext,
} from '@/companion/types';
import {
  DEFAULT_RECOMMENDATION_DATA,
  getAllRareCustomers,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type { RareCustomerCatalogItem, PlaceName } from '@/lib/catalog-types';
import { ALL_PLACES } from '@/lib/catalog-types';
import {
  buildRareBeverageCandidates,
  buildRareFoodCandidates,
  buildRareOrderPlansFromCandidates,
  compareBeverageCandidates,
  compareFoodCandidates,
  diagnoseRareBeverageCandidateSearch,
  diagnoseRareFoodCandidateSearch,
  normalizeRecommendationSortProfile,
  RECOMMENDATION_OBJECTIVE_DEFINITIONS,
  serializeRecommendationSortProfile,
  type BeverageCandidate,
  type FoodCandidate,
  type RecommendationObjectiveKey,
  type RecommendationBudgetContext,
  type RecommendationBudgetPolicy,
  type RecommendationBudgetResult,
  type RareBeverageRecommendation,
  type RareTagOrderDemand,
  type RareOrderRecommendationPlan,
  type RareRecipeRecommendation,
  type RecommendationPlanSortContext,
  type RecommendationRuntimeContext,
  type RecommendationSortProfile,
} from '@/recommendation-engine';

const NON_ORDERABLE_RARE_FOOD_TAGS = new Set(['流行喜爱', '流行厌恶']);
const EXECUTION_FOOD_CANDIDATE_LIMIT = 24;
const EXECUTION_BEVERAGE_CANDIDATE_LIMIT = 16;
const EXPANDED_EXECUTION_FOOD_CANDIDATE_LIMIT = 96;
const EXPANDED_EXECUTION_BEVERAGE_CANDIDATE_LIMIT = 48;
const EXECUTION_PLAN_LIMIT = 80;
const AUTOMATION_EXECUTION_PLAN_LIMIT = 32;
const AUTOMATION_RECOMMENDATION_ROW_LIMIT = 4;
const ORDER_RECOMMENDATION_CACHE_LIMIT = 12;
const FOOD_CANDIDATE_CACHE_LIMIT = 12;
const BEVERAGE_CANDIDATE_CACHE_LIMIT = 12;

export type OrderRecommendationUsage = 'display' | 'automation';

export interface RecommendationCacheStore {
  orders: Map<string, CachedRecommendation>;
  foodCandidates: Map<string, FoodCandidate[]>;
  beverageCandidates: Map<string, BeverageCandidate[]>;
}

export interface BuildOrderRecommendationOptions {
  usage?: OrderRecommendationUsage;
}

export function createRecommendationCacheStore(): RecommendationCacheStore {
  return {
    orders: new Map<string, CachedRecommendation>(),
    foodCandidates: new Map<string, FoodCandidate[]>(),
    beverageCandidates: new Map<string, BeverageCandidate[]>(),
  };
}

export function buildOrderRecommendations(
  orders: NightBusinessOrder[],
  runtime: RecommendationStateSnapshot | null | undefined,
  rareCustomersById: Map<number, RareCustomerCatalogItem>,
  caches: RecommendationCacheStore,
  favorites: FavoriteData,
  customRecipes: CustomRecipeData,
  preferences: CompanionPreferences,
  activeRareGuests: NightBusinessGuest[] = [],
  specialBusiness: SpecialBusinessContext | null = null,
  specialBusinessRejectedRecipeKeys: readonly string[] = [],
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
  options: BuildOrderRecommendationOptions = {},
): { recommendations: OrderRecommendation[]; recommendationIssues: RecommendationIssue[] } {
  if (orders.length === 0) return { recommendations: [], recommendationIssues: [] };
  const sortedOrders = sortNightOrders(orders, preferences.serviceOrderSortMode);
  if (!runtime) {
    return {
      recommendations: [],
      recommendationIssues: sortedOrders.map((order) => ({ order, message: '运行时推荐数据暂不可用。' })),
    };
  }

  const runtimeSets = buildRuntimeSets(runtime, data);
  if (!runtimeSets) return { recommendations: [], recommendationIssues: [] };

  const recommendations: OrderRecommendation[] = [];
  const recommendationIssues: RecommendationIssue[] = [];
  const candidateContext = buildRecommendationRuntimeContext(runtime, runtimeSets, preferences, data);
  const rejectedRecipeKeys = new Set(specialBusinessRejectedRecipeKeys);
  const usage = options.usage ?? 'display';
  const executionPlanLimit = usage === 'automation' ? AUTOMATION_EXECUTION_PLAN_LIMIT : EXECUTION_PLAN_LIMIT;
  const recommendationRowLimit = usage === 'automation'
    ? AUTOMATION_RECOMMENDATION_ROW_LIMIT
    : MAX_FOCUS_RECOMMENDATION_ROWS;

  for (const order of sortedOrders) {
    const customer = findRareCustomer(order, rareCustomersById);
    const foodTag = order.foodTag.trim();
    const beverageTag = order.beverageTag.trim();

    if (!customer) {
      recommendationIssues.push({ order, message: '无法把该稀客映射到本地稀客数据。' });
      continue;
    }
    if (!foodTag || !beverageTag) {
      recommendationIssues.push({ order, message: '该点单缺少料理 Tag 或酒水 Tag。' });
      continue;
    }

    const specialBusinessRule = buildSpecialBusinessOrderRule(specialBusiness, order.specialBusinessRole);
    const specialFoodTargetTags = specialBusinessRule.requiresWackyFoodTarget ? specialBusinessRule.foodTargetTags : [];
    const rareDemand = buildRareTagOrderDemand(customer, foodTag, beverageTag, specialFoodTargetTags);
    const budgetContext = findBudgetContextForOrder(order, activeRareGuests);
    const sortContext = buildSpecialBusinessSortContext(
      buildRecommendationPlanSortContext(
        favorites,
        customer.id,
        foodTag,
        beverageTag,
        preferences,
      ),
      specialBusiness,
      order.specialBusinessRole,
      order,
      foodTag,
      beverageTag,
    );
    const primaryExecutionPlanPolicy = buildPrimaryExecutionPlanPolicy(
      preferences,
      order.automationAllowed !== false,
    );
    const foodCandidateKey = buildFoodCandidateCacheKey(data, customer, foodTag, candidateContext, specialFoodTargetTags);
    const beverageCandidateKey = buildBeverageCandidateCacheKey(data, customer, beverageTag, candidateContext);
    let foodCandidates = caches.foodCandidates.get(foodCandidateKey);
    if (!foodCandidates) {
      foodCandidates = buildRareFoodCandidates(
        data,
        rareDemand,
        candidateContext,
      );
      caches.foodCandidates.set(foodCandidateKey, foodCandidates);
      trimCache(caches.foodCandidates, FOOD_CANDIDATE_CACHE_LIMIT);
    }
    let beverageCandidates = caches.beverageCandidates.get(beverageCandidateKey);
    if (!beverageCandidates) {
      beverageCandidates = buildRareBeverageCandidates(
        data,
        buildRareTagOrderDemand(customer, foodTag, beverageTag),
        candidateContext,
      );
      caches.beverageCandidates.set(beverageCandidateKey, beverageCandidates);
      trimCache(caches.beverageCandidates, BEVERAGE_CANDIDATE_CACHE_LIMIT);
    }
    const customFoodCandidates = buildCustomFoodCandidates({
      customRecipes,
      data,
      customer,
      requiredFoodTag: foodTag,
      requiredBeverageTag: beverageTag,
      context: candidateContext,
    });
    const mergedFoodCandidates = mergeCustomFoodCandidates(foodCandidates, customFoodCandidates);
    const combinedFoodCandidates = filterSpecialBusinessFoodCandidates(
      mergedFoodCandidates,
      specialBusinessRule,
      rejectedRecipeKeys,
      foodTag,
    );
    const combinedBeverageCandidates = filterSpecialBusinessBeverageCandidates(
      beverageCandidates,
      specialBusinessRule,
    );
    const cacheKey = [
      foodCandidateKey,
      beverageCandidateKey,
      `sort:${serializeRecommendationSortProfile(preferences.recommendationSortProfile)}`,
      serializeRecommendationPlanSortContext(sortContext),
      serializeCustomRecipeContext(customRecipes, customer.id, foodTag),
      `budgetPolicy:${preferences.recommendationBudgetPolicy}`,
      serializeBudgetContext(budgetContext),
      `freeOrder:${order.isFreeOrder === true ? '1' : '0'}`,
      `recipeVariantLimit:${preferences.recipeVariantLimitPerBase}`,
      `specialRule:${serializeSpecialBusinessOrderRule(specialBusinessRule)}`,
      `specialRejected:${[...rejectedRecipeKeys].sort().join(';')}`,
      `primaryPolicy:${serializePrimaryExecutionPlanPolicy(primaryExecutionPlanPolicy)}`,
      `usage:${usage}`,
    ].join('|');
    let cached = caches.orders.get(cacheKey);
    if (!cached) {
      const orderRuntimeContext = buildRecommendationRuntimeContext(
        runtime,
        runtimeSets,
        preferences,
        data,
        { budget: budgetContext },
      );
      const planRuntimeContext = specialBusinessRule.preferKoishiDamage
        ? { ...orderRuntimeContext, budgetPolicy: 'warn' as RecommendationBudgetPolicy }
        : orderRuntimeContext;
      const executionFoodCandidates = selectExecutionFoodCandidates(
        combinedFoodCandidates,
        combinedBeverageCandidates,
        planRuntimeContext.budget,
        planRuntimeContext.budgetPolicy,
        sortContext,
      );
      const executionBeverageCandidates = selectExecutionBeverageCandidates(
        combinedBeverageCandidates,
        combinedFoodCandidates,
        planRuntimeContext.budget,
        planRuntimeContext.budgetPolicy,
        sortContext,
      );
      const rawPlans = withSpecialBusinessPlanReasons(buildRareOrderPlansFromCandidates({
        data,
        customer,
        requiredFoodTag: foodTag,
        requiredBeverageTag: beverageTag,
        context: planRuntimeContext,
        foodCandidates: executionFoodCandidates,
        beverageCandidates: executionBeverageCandidates,
        specialFoodTargetTags,
        sortProfile: preferences.recommendationSortProfile,
        sortContext,
      }), specialBusinessRule);
      const plans = sortSpecialBusinessExecutionPlans(
        filterSpecialBusinessExecutionPlans(rawPlans, specialBusinessRule),
        specialBusinessRule,
        specialBusiness,
        order,
        budgetContext,
      );
      const executionPlans = normalizePrimaryExecutionPlans(
        plans.filter((plan) => plan.bucket !== 'blocked'),
        sortContext,
        primaryExecutionPlanPolicy,
      );
      const primaryPlan = getPrimaryExecutionPlan(executionPlans);
      const recipeRows = deriveRecipeRowsFromCandidates(combinedFoodCandidates, combinedBeverageCandidates, {
        variantLimitPerBase: preferences.recipeVariantLimitPerBase,
        limit: recommendationRowLimit,
        budget: orderRuntimeContext.budget,
        budgetPolicy: orderRuntimeContext.budgetPolicy,
        sortProfile: preferences.recommendationSortProfile,
        sortContext,
      });
      const beverageRows = deriveBeverageRowsFromCandidates(combinedBeverageCandidates, combinedFoodCandidates, {
        limit: recommendationRowLimit,
        budget: orderRuntimeContext.budget,
        budgetPolicy: orderRuntimeContext.budgetPolicy,
        sortProfile: preferences.recommendationSortProfile,
        sortContext,
      });
      const primaryRows = projectPrimaryExecutionPlanRows(
        recipeRows,
        beverageRows,
        primaryPlan,
        recommendationRowLimit,
        preferences.recipeVariantLimitPerBase,
      );
      const blockedDiagnostic = executionPlans.length === 0
        ? buildRecommendationBlockedDiagnostic({
          data,
          demand: rareDemand,
          context: orderRuntimeContext,
          runtimeSets,
          generatedFoodCandidates: foodCandidates,
          combinedFoodCandidatesBeforeSpecialRule: mergedFoodCandidates,
          combinedFoodCandidates,
          combinedBeverageCandidates,
          rawPlans,
          safePlans: plans,
          executionPlans,
          specialBusinessRule,
        })
        : null;
      const blockedMessages = uniqueMessages([
        ...(blockedDiagnostic ? [blockedDiagnostic.message] : []),
        ...buildSpecialBusinessBlockedMessages(rawPlans, plans, specialBusinessRule),
        ...buildBlockedPlanMessages(plans, orderRuntimeContext.budget, orderRuntimeContext.budgetPolicy),
      ]);
      cached = {
        customer,
        executionPlans: executionPlans.slice(0, executionPlanLimit),
        budget: findRecommendationBudget(plans, primaryPlan),
        blockedMessages,
        blockedDiagnostic,
        recipes: primaryRows.recipes,
        beverages: primaryRows.beverages,
      };
      caches.orders.set(cacheKey, cached);
      trimCache(caches.orders, ORDER_RECOMMENDATION_CACHE_LIMIT);
    }

    recommendations.push({
      order,
      customer: cached.customer,
      executionPlans: cached.executionPlans,
      budget: cached.budget,
      blockedMessages: cached.blockedMessages,
      blockedDiagnostic: cached.blockedDiagnostic,
      recipes: cached.recipes,
      beverages: cached.beverages,
    });
  }

  return { recommendations, recommendationIssues };
}

export function isUsableRareCustomer(customer: RareCustomerCatalogItem): boolean {
  return isUsableRareCustomerName(customer.name)
    && customer.positiveTags.some(isOrderableRareFoodTag)
    && customer.beverageTags.length > 0;
}

export function isSelectableRareCustomer(customer: RareCustomerCatalogItem): boolean {
  return isUsableRareCustomer(customer) && customer.places.length > 0;
}

export function buildRareCustomerMap(
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): Map<number, RareCustomerCatalogItem> {
  return new Map(getAllRareCustomers(data).map((customer) => [customer.id, customer]));
}

export function normalizePlace(value: string | null | undefined): PlaceName | null {
  return ALL_PLACES.includes(value as PlaceName) ? value as PlaceName : null;
}

export function isOrderableRareFoodTag(tag: string): boolean {
  return !NON_ORDERABLE_RARE_FOOD_TAGS.has(tag);
}

export function buildRecommendationPlanSortContext(
  favorites: FavoriteData,
  customerId: number,
  foodTag: string,
  beverageTag: string,
  preferences: CompanionPreferences,
): RecommendationPlanSortContext {
  return {
    favoriteRecipeKeys: new Set(
      favorites.recipes
        .filter((favorite) => favorite.customerId === customerId && favorite.foodTag === foodTag)
        .map((favorite) => buildRecipeSortKey(favorite.recipeId, favorite.extraIngredientIds)),
    ),
    favoriteBeverageIds: new Set(
      favorites.beverages
        .filter((favorite) => favorite.customerId === customerId && favorite.beverageTag === beverageTag)
        .map((favorite) => favorite.beverageId),
    ),
    pinFavoriteRecipe: preferences.pinFavoriteRecipeEnabled,
    pinFavoriteBeverage: preferences.pinFavoriteBeverageEnabled,
  };
}

function buildSpecialBusinessSortContext(
  base: RecommendationPlanSortContext,
  specialBusiness: SpecialBusinessContext | null,
  role: string | null | undefined,
  order?: Pick<NightBusinessOrder, 'remainingOrderCount'> | null,
  requiredFoodTag?: string,
  requiredBeverageTag?: string,
): RecommendationPlanSortContext {
  if (!specialBusiness?.active) return base;

  const rule = buildSpecialBusinessOrderRule(specialBusiness, role);
  const foodTargetTags = rule.foodTargetTags;
  const beverageTargetTags = normalizeSpecialBusinessTags(specialBusiness.beverageTargetTags);
  if (foodTargetTags.length === 0
    && beverageTargetTags.length === 0
    && !rule.preferHighFoodLevel
    && !rule.preferHighBeverageLevel
    && !rule.preferKoishiDamage
    && !rule.preferYuyukoPositiveSpell
    && rule.yuyukoProgressEvaluationMode === 'none') {
    return base;
  }

  return {
    ...base,
    specialTargetFoodTags: foodTargetTags.length > 0 ? new Set(foodTargetTags) : undefined,
    specialTargetBeverageTags: beverageTargetTags.length > 0 ? new Set(beverageTargetTags) : undefined,
    specialPreferHighFoodLevel: rule.preferHighFoodLevel,
    specialPreferHighBeverageLevel: rule.preferHighBeverageLevel,
    specialPreferDamageLevel: rule.preferKoishiDamage,
    specialPreferYuyukoPositiveSpell: rule.preferYuyukoPositiveSpell,
    specialYuyukoProgressEvaluationMode: rule.yuyukoProgressEvaluationMode === 'none'
      ? undefined
      : rule.yuyukoProgressEvaluationMode,
    specialYuyukoRequiredFoodTag: rule.preferYuyukoPositiveSpell
      || rule.yuyukoProgressEvaluationMode === 'retake-tag-order'
      ? requiredFoodTag
      : undefined,
    specialYuyukoRequiredBeverageTag: rule.preferYuyukoPositiveSpell
      || rule.yuyukoProgressEvaluationMode === 'retake-tag-order'
      ? requiredBeverageTag
      : undefined,
    specialKoishiRemainingScore: rule.preferKoishiDamage ? getKoishiRemainingTargetScore(specialBusiness) : null,
    specialKoishiRemainingOrderCount: rule.preferKoishiDamage ? normalizeNonNegativeInt(order?.remainingOrderCount) : null,
  };
}

function normalizeNonNegativeInt(value: number | null | undefined): number | null {
  if (!Number.isFinite(value)) return null;
  return Math.max(0, Math.trunc(value ?? 0));
}

function filterSpecialBusinessFoodCandidates(
  candidates: FoodCandidate[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
  rejectedRecipeKeys: Set<string>,
  requiredFoodTag: string,
): FoodCandidate[] {
  return candidates.filter((candidate) => {
    if (!isSpecialBusinessFoodBaseMatchCandidate(candidate, rule)) return false;
    if (!isSpecialBusinessFoodNegativeSafeCandidate(candidate, rule, requiredFoodTag)) return false;
    if (!rule.requiresWackyFoodTarget || rule.foodTargetTags.length === 0) return true;
    if (!hasMatchingSpecialBusinessTag(candidate.activeTags, rule.foodTargetTags)) return false;
    const key = buildWackyRejectedRecipeKeyForRareRecipe(
      rule.foodTargetTags,
      candidate.recipe.id,
      candidate.recipe.recipeId,
      candidate.extraIngredients.map((ingredient) => ingredient.id),
    );
    return !key || !rejectedRecipeKeys.has(key);
  });
}

function isSpecialBusinessFoodBaseMatchCandidate(
  candidate: FoodCandidate,
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): boolean {
  return !rule.requiresBaseOrderMatch || candidate.meetsRequiredFood;
}

function isSpecialBusinessFoodNegativeSafeCandidate(
  candidate: FoodCandidate,
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
  requiredFoodTag: string,
): boolean {
  if (rule.preferYuyukoPositiveSpell) {
    return getYuyukoPositiveSpellNegativeTags(candidate, requiredFoodTag).length === 0;
  }
  if (rule.yuyukoProgressEvaluationMode === 'retake-tag-order') {
    return getYuyukoPositiveSpellNegativeTags(candidate, requiredFoodTag).length === 0;
  }
  if (rule.yuyukoProgressEvaluationMode === 'story-level-sum') return true;
  if ((rule.requiresHighEvaluation || rule.preferKoishiDamage)
    && candidate.matchedNegativeTags.length > 0) return false;
  return true;
}

function filterSpecialBusinessBeverageCandidates(
  candidates: BeverageCandidate[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): BeverageCandidate[] {
  if (!rule.requiresBaseOrderMatch && !rule.requiresHighEvaluation) return candidates;
  return candidates.filter((candidate) => candidate.meetsRequiredBeverage);
}

function filterSpecialBusinessExecutionPlans(
  plans: RareOrderRecommendationPlan[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): RareOrderRecommendationPlan[] {
  if (!rule.requiresBaseOrderMatch && !rule.requiresHighEvaluation) return plans;
  return plans.filter((plan) => isSpecialBusinessSafeExecutionPlan(plan, rule));
}

function sortSpecialBusinessExecutionPlans(
  plans: RareOrderRecommendationPlan[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
  specialBusiness: SpecialBusinessContext | null,
  order: Pick<NightBusinessOrder, 'fund' | 'remainingOrderCount'>,
  budgetContext: RecommendationBudgetContext | null,
): RareOrderRecommendationPlan[] {
  if (rule.yuyukoProgressEvaluationMode !== 'none') {
    return [...plans].sort((left, right) => compareYuyukoPlans(
      left,
      right,
      rule.yuyukoProgressEvaluationMode,
    ));
  }
  if (rule.preferYuyukoPositiveSpell) {
    return [...plans].sort((left, right) => compareYuyukoPositiveSpellPlans(left, right));
  }
  if (!rule.preferKoishiDamage) return plans;
  return [...plans].sort((left, right) => compareKoishiBrokenShieldRecommendationPlans(left, right, {
    remainingBudget: budgetContext?.remainingBudget ?? order.fund ?? null,
    remainingScore: getKoishiRemainingTargetScore(specialBusiness),
    remainingOrderCount: order.remainingOrderCount ?? null,
  }));
}

function projectPrimaryExecutionPlanRows(
  recipes: RareRecipeRecommendation[],
  beverages: RareBeverageRecommendation[],
  plan: RareOrderRecommendationPlan | null,
  limit: number,
  recipeVariantLimitPerBase: number,
): {
  recipes: RareRecipeRecommendation[];
  beverages: RareBeverageRecommendation[];
} {
  if (!plan || plan.bucket === 'blocked') {
    return { recipes, beverages };
  }

  const rowLimit = normalizeDerivedRowLimit(limit);
  const primaryRecipe = plan.food ? toRareRecipeResult(plan.food) : null;
  const primaryRecipeKey = primaryRecipe ? recipeResultKey(primaryRecipe) : '';
  const primaryBeverage = plan.beverage ? toRareBeverageResult(plan.beverage) : null;
  const nextRecipes = primaryRecipe
    ? limitProjectedRecipeRows(
      [primaryRecipe, ...recipes.filter((recipe) => recipeResultKey(recipe) !== primaryRecipeKey)],
      rowLimit,
      recipeVariantLimitPerBase,
    )
    : recipes;
  const nextBeverages = primaryBeverage
    ? [
      primaryBeverage,
      ...beverages.filter((beverage) => beverage.beverage.id !== primaryBeverage.beverage.id),
    ].slice(0, rowLimit)
    : beverages;

  return {
    recipes: nextRecipes,
    beverages: nextBeverages,
  };
}

function limitProjectedRecipeRows(
  recipes: RareRecipeRecommendation[],
  rowLimit: number,
  variantLimitPerBase: number,
): RareRecipeRecommendation[] {
  const variantLimit = normalizeDerivedRowLimit(variantLimitPerBase);
  if (rowLimit <= 0 || variantLimit <= 0) return [];
  const countsByBase = new Map<number, number>();
  const result: RareRecipeRecommendation[] = [];
  for (const recipe of recipes) {
    const currentCount = countsByBase.get(recipe.recipe.id) ?? 0;
    if (currentCount >= variantLimit) continue;
    countsByBase.set(recipe.recipe.id, currentCount + 1);
    result.push(recipe);
    if (result.length >= rowLimit) break;
  }
  return result;
}

function withSpecialBusinessPlanReasons(
  plans: RareOrderRecommendationPlan[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): RareOrderRecommendationPlan[] {
  if (rule.yuyukoProgressEvaluationMode !== 'none') {
    return plans.map((plan) => {
      if (!plan.food || !plan.beverage || plan.bucket === 'blocked') return plan;
      const reason = buildYuyukoPlanReason(plan, rule.yuyukoProgressEvaluationMode);
      return {
        ...plan,
        reasons: [reason, ...plan.reasons.filter((item) => item !== reason)],
      };
    });
  }
  if (rule.preferYuyukoPositiveSpell) {
    return plans.map((plan) => {
      if (!plan.food || !plan.beverage || plan.bucket === 'blocked') return plan;
      const reason = buildYuyukoPositiveSpellPlanReason(plan);
      return {
        ...plan,
        reasons: [reason, ...plan.reasons.filter((item) => item !== reason)],
      };
    });
  }
  if (!rule.preferKoishiDamage) return plans;
  return plans.map((plan) => {
    if (!plan.food || !plan.beverage || plan.bucket === 'blocked') return plan;
    const reason = buildKoishiBrokenShieldPlanReason(plan);
    return {
      ...plan,
      reasons: [reason, ...plan.reasons.filter((item) => item !== reason)],
    };
  });
}

function isSpecialBusinessSafeExecutionPlan(
  plan: RareOrderRecommendationPlan,
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): boolean {
  const food = plan.food;
  const beverage = plan.beverage;
  if (!food || !beverage || plan.bucket === 'blocked') return false;
  if (rule.requiresBaseOrderMatch && (!food.meetsRequiredFood || !beverage.meetsRequiredBeverage)) return false;
  if (rule.yuyukoProgressEvaluationMode !== 'none') {
    return isYuyukoProgressPlan(plan, rule.yuyukoProgressEvaluationMode);
  }
  if (rule.preferYuyukoPositiveSpell) {
    return isYuyukoPositiveSpellPlan(plan);
  }
  if (!rule.requiresHighEvaluation) return true;
  if (food.matchedNegativeTags.length > 0) return false;

  const baseScore = (food.meetsRequiredFood ? 1 : 0) + (beverage.meetsRequiredBeverage ? 1 : 0);
  const preferenceMatches = food.matchedPositiveTags.length + beverage.matchedTags.length;
  return preferenceMatches >= rule.highEvaluationMinPreferenceMatches
    && baseScore + preferenceMatches >= 4;
}

function buildSpecialBusinessBlockedMessages(
  rawPlans: RareOrderRecommendationPlan[],
  safePlans: RareOrderRecommendationPlan[],
  rule: ReturnType<typeof buildSpecialBusinessOrderRule>,
): string[] {
  if ((!rule.requiresBaseOrderMatch
      && !rule.requiresHighEvaluation
      && !rule.preferYuyukoPositiveSpell
      && rule.yuyukoProgressEvaluationMode === 'none')
    || safePlans.some((plan) => plan.bucket !== 'blocked')) return [];
  if (rawPlans.length === 0) return [];
  if (rule.yuyukoProgressEvaluationMode !== 'none') {
    return buildYuyukoProgressBlockedMessages(rawPlans, rule.yuyukoProgressEvaluationMode);
  }
  if (rule.preferYuyukoPositiveSpell) return buildYuyukoPositiveSpellBlockedMessages(rawPlans);

  const messages: string[] = [];
  if (rule.requiresBaseOrderMatch) {
    messages.push('特殊经营要求先满足原订单料理和酒水。');
  }
  if (rule.requiresHighEvaluation) {
    messages.push(`特殊经营要求最高评价，当前组合至少需要 ${rule.highEvaluationMinPreferenceMatches} 个喜好命中且不能包含厌恶 Tag。`);
  }
  return messages;
}

function serializeSpecialBusinessOrderRule(rule: ReturnType<typeof buildSpecialBusinessOrderRule>): string {
  return [
    rule.requiresWackyFoodTarget ? 'wacky=1' : 'wacky=0',
    `food=${rule.foodTargetTags.join(',')}`,
    rule.requiresBaseOrderMatch ? 'base=1' : 'base=0',
    rule.requiresHighEvaluation ? 'highest=1' : 'highest=0',
    `minPref=${rule.highEvaluationMinPreferenceMatches}`,
    rule.preferHighFoodLevel ? 'highFood=1' : 'highFood=0',
    rule.preferHighBeverageLevel ? 'highBev=1' : 'highBev=0',
    rule.preferKoishiDamage ? 'koishiDamage=1' : 'koishiDamage=0',
    rule.preferYuyukoPositiveSpell ? 'yuyukoPositiveSpell=1' : 'yuyukoPositiveSpell=0',
    `yuyukoProgress=${rule.yuyukoProgressEvaluationMode}`,
  ].join(';');
}

function findRareCustomer(order: NightBusinessOrder, rareCustomersById: Map<number, RareCustomerCatalogItem>) {
  if (order.guestId != null) {
    const byId = rareCustomersById.get(order.guestId);
    if (byId) return byId;
  }

  return [...rareCustomersById.values()].find((customer) => customer.name === order.guestName) ?? null;
}

function findBudgetContextForOrder(
  order: NightBusinessOrder,
  activeRareGuests: NightBusinessGuest[],
): RecommendationBudgetContext | null {
  if (order.isFreeOrder === true) return null;
  const guest = activeRareGuests.length > 0 ? findActiveRareGuestForOrder(order, activeRareGuests) : null;
  const remainingBudget = normalizeRemainingBudget(guest?.fund ?? order.fund);
  if (remainingBudget == null && guest?.willPayMoney == null && order.willPayMoney == null) return null;

  return {
    remainingBudget,
    source: guest ? 'runtime-active-guest' : 'unknown',
    willPayMoney: guest?.willPayMoney ?? order.willPayMoney ?? null,
  };
}

function findActiveRareGuestForOrder(
  order: NightBusinessOrder,
  activeRareGuests: NightBusinessGuest[],
): NightBusinessGuest | null {
  if (order.guestId != null) {
    const byId = activeRareGuests.find((guest) => guest.guestId === order.guestId);
    if (byId) return byId;
  }

  const orderGuestName = normalizeGuestName(order.guestName);
  const byDeskAndName = activeRareGuests.find((guest) =>
    guest.deskCode === order.deskCode && normalizeGuestName(guest.guestName) === orderGuestName,
  );
  if (byDeskAndName) return byDeskAndName;

  const sameDesk = activeRareGuests.filter((guest) => guest.deskCode === order.deskCode);
  return sameDesk.length === 1 ? sameDesk[0] : null;
}

function normalizeGuestName(value: string): string {
  return value.trim();
}

function normalizeRemainingBudget(value: number | null | undefined): number | null {
  if (!Number.isFinite(value)) return null;
  return Math.max(0, Math.trunc(value ?? 0));
}

export function buildRecommendationRuntimeContext(
  runtime: RecommendationStateSnapshot,
  runtimeSets: RuntimeSets,
  preferences: CompanionPreferences,
  data: RecommendationDataSet,
  options: { budget?: RecommendationBudgetContext | null } = {},
): RecommendationRuntimeContext {
  return {
    availableRecipeIds: runtimeSets.recipeIds,
    availableIngredientIds: runtimeSets.ingredientIds,
    availableBeverageIds: runtimeSets.beverageIds,
    disabledIngredientIds: new Set<number>(),
    excludedIngredientIds: new Set(preferences.recommendationExclusions.excludedIngredientIds),
    excludedBeverageIds: new Set(preferences.recommendationExclusions.excludedBeverageIds),
    ownedIngredientQty: runtimeSets.ownedIngredientQty,
    ownedBeverageQty: runtimeSets.ownedBeverageQty,
    placedCookerNames: runtimeSets.placedCookerNames,
    hasCookerSnapshot: runtimeSets.hasCookerSnapshot,
    popularFoodTag: runtime.popularFoodTag,
    popularHateFoodTag: runtime.popularHateFoodTag,
    famousShopEnabled: runtime.famousShopEnabled,
    tagPriorityRules: data.tagPriorityRules,
    maxExtraIngredients: 4,
    filterMissingCookers: preferences.filterMissingCookers,
    budget: options.budget ?? null,
    budgetPolicy: preferences.recommendationBudgetPolicy,
  };
}

function selectExecutionFoodCandidates(
  foodCandidates: FoodCandidate[],
  beverageCandidates: BeverageCandidate[],
  budget: RecommendationBudgetContext | null,
  budgetPolicy: RecommendationBudgetPolicy,
  sortContext: RecommendationPlanSortContext,
): FoodCandidate[] {
  const eligible = foodCandidates.filter((food) =>
    candidateHasNoHardFailures(food.conditionResults)
    && canPairFoodWithinBudget(food, beverageCandidates, budget, budgetPolicy),
  );
  const limit = usesExpandedExecutionCandidateSearch(sortContext)
    ? EXPANDED_EXECUTION_FOOD_CANDIDATE_LIMIT
    : EXECUTION_FOOD_CANDIDATE_LIMIT;
  return limitCandidatesByPinRank(
    eligible,
    limit,
    (food) => getFoodExecutionCandidateRank(food, sortContext),
  );
}

function selectExecutionBeverageCandidates(
  beverageCandidates: BeverageCandidate[],
  foodCandidates: FoodCandidate[],
  budget: RecommendationBudgetContext | null,
  budgetPolicy: RecommendationBudgetPolicy,
  sortContext: RecommendationPlanSortContext,
): BeverageCandidate[] {
  const eligible = beverageCandidates.filter((beverage) =>
    candidateHasNoHardFailures(beverage.conditionResults)
    && canPairBeverageWithinBudget(beverage, foodCandidates, budget, budgetPolicy),
  );
  const limit = usesExpandedExecutionCandidateSearch(sortContext)
    ? EXPANDED_EXECUTION_BEVERAGE_CANDIDATE_LIMIT
    : EXECUTION_BEVERAGE_CANDIDATE_LIMIT;
  return limitCandidatesByPinRank(
    eligible,
    limit,
    (beverage) => getBeverageExecutionCandidateRank(beverage, sortContext),
  );
}

function limitCandidatesByPinRank<TCandidate>(
  candidates: TCandidate[],
  limit: number,
  getPinRank: (candidate: TCandidate) => number,
): TCandidate[] {
  const pinned = candidates
    .map((candidate, index) => ({ candidate, index, rank: getPinRank(candidate) }))
    .filter((entry) => entry.rank > 0)
    .sort((left, right) => right.rank - left.rank || left.index - right.index);
  const selected = new Set<TCandidate>();
  const rows: TCandidate[] = [];

  for (const entry of pinned) {
    if (rows.length >= limit) return rows;
    if (selected.has(entry.candidate)) continue;
    selected.add(entry.candidate);
    rows.push(entry.candidate);
  }
  for (const candidate of candidates) {
    if (rows.length >= limit) break;
    if (selected.has(candidate)) continue;
    selected.add(candidate);
    rows.push(candidate);
  }

  return rows;
}

function usesExpandedExecutionCandidateSearch(sortContext: RecommendationPlanSortContext): boolean {
  return sortContext.specialPreferDamageLevel === true
    || sortContext.specialPreferYuyukoPositiveSpell === true
    || sortContext.specialYuyukoProgressEvaluationMode != null;
}

function getFoodCandidatePinRank(
  food: FoodCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  let rank = 0;
  if (sortContext.specialPreferDamageLevel) rank = Math.max(rank, getFoodDamageCandidateRank(food));
  if (sortContext.specialPreferYuyukoPositiveSpell) {
    rank = Math.max(rank, getYuyukoPositiveSpellFoodCandidateRank(
      food,
      sortContext.specialYuyukoRequiredFoodTag,
    ));
  }
  if (sortContext.specialYuyukoProgressEvaluationMode) {
    rank = Math.max(rank, getFoodYuyukoCandidateRank(
      food,
      sortContext.specialYuyukoProgressEvaluationMode,
      sortContext.specialYuyukoRequiredFoodTag,
    ));
  }
  if (food.customRecipePinned) rank = Math.max(rank, 40);
  const specialBusinessRank = getFoodSpecialBusinessRank(food, sortContext);
  if (specialBusinessRank > 0) rank = Math.max(rank, 30 + specialBusinessRank);
  if (sortContext.pinFavoriteRecipe && sortContext.favoriteRecipeKeys?.has(buildRecipeSortKey(
    food.recipe.id,
    food.extraIngredients.map((ingredient) => ingredient.id),
  ))) rank = Math.max(rank, 20);
  return rank;
}

function getFoodExecutionCandidateRank(
  food: FoodCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  const favoriteRank = sortContext.favoriteRecipeKeys?.has(buildRecipeSortKey(
    food.recipe.id,
    food.extraIngredients.map((ingredient) => ingredient.id),
  )) ? 1 : 0;
  return Math.max(getFoodCandidatePinRank(food, sortContext), favoriteRank);
}

function getBeverageCandidatePinRank(
  beverage: BeverageCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  let rank = 0;
  if (sortContext.specialPreferDamageLevel) rank = Math.max(rank, getBeverageDamageCandidateRank(beverage));
  if (sortContext.specialPreferYuyukoPositiveSpell) {
    rank = Math.max(rank, getYuyukoPositiveSpellBeverageCandidateRank(
      beverage,
      sortContext.specialYuyukoRequiredBeverageTag,
    ));
  }
  if (sortContext.specialYuyukoProgressEvaluationMode) {
    rank = Math.max(rank, getBeverageYuyukoCandidateRank(
      beverage,
      sortContext.specialYuyukoProgressEvaluationMode,
      sortContext.specialYuyukoRequiredBeverageTag,
    ));
  }
  const specialBusinessRank = getBeverageSpecialBusinessRank(beverage, sortContext);
  if (specialBusinessRank > 0) rank = Math.max(rank, 20 + specialBusinessRank);
  if (sortContext.pinFavoriteBeverage && sortContext.favoriteBeverageIds?.has(beverage.beverage.id)) rank = Math.max(rank, 10);
  return rank;
}

function getFoodDamageCandidateRank(food: FoodCandidate): number {
  if (food.matchedNegativeTags.length > 0) return 0;
  return 10_000
    + food.matchedPositiveTags.length * 1_000
    + food.recipe.level * 100
    - Math.min(food.recipe.price, 999)
    - Math.ceil(food.resourcePressure * 10)
    - food.extraIngredients.length;
}

function getBeverageDamageCandidateRank(beverage: BeverageCandidate): number {
  return 10_000
    + beverage.matchedTags.length * 1_000
    + beverage.beverage.level * 100
    - Math.min(beverage.beverage.price, 999)
    + cappedInventoryQuantityRank(beverage.ownedQuantity, 99);
}

function getFoodYuyukoCandidateRank(
  food: FoodCandidate,
  mode: NonNullable<RecommendationPlanSortContext['specialYuyukoProgressEvaluationMode']>,
  requiredFoodTag: string | null | undefined,
): number {
  if (mode === 'retake-tag-order') {
    return getYuyukoPositiveSpellFoodCandidateRank(food, requiredFoodTag);
  }
  if (!food.meetsRequiredFood) return 0;
  return 10_000
    + food.recipe.level * 1_000
    + Math.min(food.recipe.price, 999)
    - Math.ceil(food.resourcePressure * 10)
    - food.extraIngredients.length;
}

function getBeverageYuyukoCandidateRank(
  beverage: BeverageCandidate,
  mode: NonNullable<RecommendationPlanSortContext['specialYuyukoProgressEvaluationMode']>,
  requiredBeverageTag: string | null | undefined,
): number {
  if (mode === 'retake-tag-order') {
    return getYuyukoPositiveSpellBeverageCandidateRank(beverage, requiredBeverageTag);
  }
  if (!beverage.meetsRequiredBeverage) return 0;
  return 10_000
    + beverage.beverage.level * 1_000
    + Math.min(beverage.beverage.price, 999)
    + cappedInventoryQuantityRank(beverage.ownedQuantity, 99);
}

function getFoodSpecialBusinessRank(
  food: FoodCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  if (!sortContext.specialTargetFoodTags || sortContext.specialTargetFoodTags.size === 0) return 0;
  return countTagMatches(food.activeTags, sortContext.specialTargetFoodTags);
}

function getBeverageSpecialBusinessRank(
  beverage: BeverageCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  if (!sortContext.specialTargetBeverageTags || sortContext.specialTargetBeverageTags.size === 0) return 0;
  return countTagMatches(beverage.activeTags, sortContext.specialTargetBeverageTags);
}

function countTagMatches(tags: string[], targetTags: Set<string>): number {
  return tags.reduce((count, tag) => count + (targetTags.has(tag) ? 1 : 0), 0);
}

function getBeverageExecutionCandidateRank(
  beverage: BeverageCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  const favoriteRank = sortContext.favoriteBeverageIds?.has(beverage.beverage.id) ? 1 : 0;
  return Math.max(getBeverageCandidatePinRank(beverage, sortContext), favoriteRank);
}

function findRecommendationBudget(
  plans: RareOrderRecommendationPlan[],
  primaryPlan: RareOrderRecommendationPlan | null,
): RecommendationBudgetResult | null {
  return primaryPlan?.budget ?? plans.find((plan) => plan.budget)?.budget ?? null;
}

function buildBlockedPlanMessages(
  plans: RareOrderRecommendationPlan[],
  budget: RecommendationBudgetContext | null,
  budgetPolicy: RecommendationBudgetPolicy,
): string[] {
  if (plans.some((plan) => plan.bucket !== 'blocked')) return [];
  if (plans.length === 0) {
    if (budgetPolicy === 'block' && budget?.willPayMoney === false) return ['稀客当前不会付款。'];
    if (isBudgetBlockingPairing(budget, budgetPolicy)) return ['没有可搭配且不超预算的料理/酒水组合。'];
    return [];
  }

  const messages = plans.flatMap((plan) =>
    plan.conditionResults
      .filter((result) => result.status === 'fail' && result.severity === 'hard')
      .map((result) => result.detail),
  );
  return [...new Set(messages)].slice(0, 3);
}

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

function buildRecommendationBlockedDiagnostic({
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
  const reason = selectRecommendationBlockedReason({
    demand,
    context,
    counts,
    specialBusinessRule,
    missingIngredientNames: foodSearch.missingIngredientNames,
    missingCookerNames: foodSearch.missingCookerNames,
    placedCookerNames: [...runtimeSets.placedCookerNames].sort(),
    remainingBudget,
    minimumPairPrice,
  });
  const diagnosticWithoutSignature = {
    ...reason,
    counts,
    missingIngredientNames: foodSearch.missingIngredientNames,
    requiredCookerNames: foodSearch.missingCookerNames,
    placedCookerNames: [...runtimeSets.placedCookerNames].sort(),
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
  remainingBudget: number | null;
  minimumPairPrice: number | null;
}): Pick<RecommendationBlockedDiagnostic, 'code' | 'firstEmptyStage' | 'message'> {
  const foodRecipes = counts.foodRecipeEligibility;
  const foodCandidates = counts.foodCandidates;
  const beverageCandidates = counts.beverageCandidates;
  const plans = counts.plans;

  if (foodCandidates.baseOrderMatched === 0) {
    if (foodRecipes.requiredTagReachable === 0) {
      return {
        code: 'food-tag-not-supported',
        firstEmptyStage: 'food-tag-reachability',
        message: `当前配方目录在现有加料上限与 Tag 规则下无法构成料理点单 Tag「${demand.requiredFoodTag}」。`,
      };
    }
    if (foodRecipes.requiredTagReachableUnlocked === 0) {
      return {
        code: 'food-recipe-locked',
        firstEmptyStage: 'food-recipe-unlocked',
        message: `能满足料理点单 Tag「${demand.requiredFoodTag}」的配方尚未解锁。`,
      };
    }
    if (foodRecipes.requiredTagReachableBaseIngredientsReady === 0) {
      return {
        code: 'food-base-ingredient-missing',
        firstEmptyStage: 'food-base-ingredients',
        message: `满足料理点单 Tag「${demand.requiredFoodTag}」的已解锁配方缺少基础材料`
          + `${formatDiagnosticNameList(missingIngredientNames)}。`,
      };
    }
    if (foodRecipes.requiredTagReachableCookerReady === 0) {
      return {
        code: 'food-cooker-missing',
        firstEmptyStage: 'food-cooker',
        message: `满足料理点单 Tag「${demand.requiredFoodTag}」的配方缺少可用厨具`
          + `${formatDiagnosticNameList(missingCookerNames)}；当前摆放`
          + `${formatDiagnosticNameList(placedCookerNames, '无')}。`,
      };
    }
    if (foodCandidates.generatedRequiredTagMatched === 0) {
      return {
        code: 'food-required-tag-not-generated',
        firstEmptyStage: 'food-candidate-generation',
        message: `满足料理点单 Tag「${demand.requiredFoodTag}」的配方已具备运行资格，`
          + '但当前可用加料未生成对应料理候选。',
      };
    }
  }

  if (foodCandidates.executable === 0
    && foodRecipes.requiredTagReachableBaseIngredientsReady > 0
    && foodRecipes.requiredTagReachableCookerReady === 0
    && missingCookerNames.length > 0) {
    return {
      code: 'food-cooker-missing',
      firstEmptyStage: 'food-cooker',
      message: `满足料理点单 Tag「${demand.requiredFoodTag}」的配方缺少可用厨具`
        + `${formatDiagnosticNameList(missingCookerNames)}；当前摆放`
        + `${formatDiagnosticNameList(placedCookerNames, '无')}。`,
    };
  }

  if (foodCandidates.negativeSafe === 0
    && foodCandidates.baseOrderMatched > 0) {
    return {
      code: 'food-negative-tag',
      firstEmptyStage: 'food-negative-safe',
      message: '满足原订单的料理候选均包含当前稀客厌恶 Tag，已停止自动执行。',
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
        message: `当前可用酒水无法满足酒水点单 Tag「${demand.requiredBeverageTag}」。`,
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

function uniqueMessages(messages: readonly string[]): string[] {
  return [...new Set(messages.map((message) => message.trim()).filter(Boolean))];
}

export function deriveRecipeRowsFromCandidates(
  foodCandidates: FoodCandidate[],
  beverageCandidates: BeverageCandidate[],
  {
    variantLimitPerBase = Number.POSITIVE_INFINITY,
    limit = Number.POSITIVE_INFINITY,
    budget,
    budgetPolicy,
    sortProfile,
    sortContext = {},
  }: {
    variantLimitPerBase?: number;
    limit?: number;
    budget: RecommendationBudgetContext | null;
    budgetPolicy: RecommendationBudgetPolicy;
    sortProfile?: RecommendationSortProfile;
    sortContext?: RecommendationPlanSortContext;
  },
): RareRecipeRecommendation[] {
  const rows: RareRecipeRecommendation[] = [];
  const seen = new Set<string>();
  const baseRecipeCounts = new Map<number, number>();
  const rowLimit = normalizeDerivedRowLimit(limit);
  const baseLimit = normalizeDerivedRowLimit(variantLimitPerBase);
  if (rowLimit <= 0 || baseLimit <= 0) return rows;

  const rowSortContext = buildRecipeRowSortContext(sortContext);
  const displayCandidates = foodCandidates.filter((food) =>
    candidateHasNoHardFailures(food.conditionResults)
    && canPairFoodWithinBudget(food, beverageCandidates, budget, budgetPolicy)
    && isFoodRecommendationRowEligible(food, rowSortContext),
  );
  const sortedCandidates = sortFoodDisplayCandidates(displayCandidates, rowSortContext, sortProfile);
  for (const food of sortedCandidates) {
    const row = toRareRecipeResult(food);
    const key = recipeResultKey(row);
    if (seen.has(key)) continue;
    const currentBaseCount = baseRecipeCounts.get(row.recipe.id) ?? 0;
    if (currentBaseCount >= baseLimit) continue;
    seen.add(key);
    baseRecipeCounts.set(row.recipe.id, currentBaseCount + 1);
    rows.push(row);
    if (rows.length >= rowLimit) break;
  }
  return rows;
}

export function deriveBeverageRowsFromCandidates(
  beverageCandidates: BeverageCandidate[],
  foodCandidates: FoodCandidate[],
  {
    limit = Number.POSITIVE_INFINITY,
    budget,
    budgetPolicy,
    sortProfile,
    sortContext = {},
  }: {
    limit?: number;
    budget: RecommendationBudgetContext | null;
    budgetPolicy: RecommendationBudgetPolicy;
    sortProfile?: RecommendationSortProfile;
    sortContext?: RecommendationPlanSortContext;
  },
): RareBeverageRecommendation[] {
  const rows: RareBeverageRecommendation[] = [];
  const seen = new Set<number>();
  const rowLimit = normalizeDerivedRowLimit(limit);
  if (rowLimit <= 0) return rows;

  const rowSortContext = buildBeverageRowSortContext(sortContext);
  const displayCandidates = beverageCandidates.filter((beverage) =>
    candidateHasNoHardFailures(beverage.conditionResults)
    && canPairBeverageWithinBudget(beverage, foodCandidates, budget, budgetPolicy)
    && isBeverageRecommendationRowEligible(beverage, rowSortContext),
  );
  const sortedCandidates = sortBeverageDisplayCandidates(displayCandidates, rowSortContext, sortProfile);
  for (const beverage of sortedCandidates) {
    if (seen.has(beverage.beverage.id)) continue;
    seen.add(beverage.beverage.id);
    rows.push(toRareBeverageResult(beverage));
    if (rows.length >= rowLimit) break;
  }
  return rows;
}

function normalizeDerivedRowLimit(value: number): number {
  if (!Number.isFinite(value)) return Number.POSITIVE_INFINITY;
  return Math.max(0, Math.trunc(value));
}

function buildRecipeRowSortContext(sortContext: RecommendationPlanSortContext): RecommendationPlanSortContext {
  return {
    ...sortContext,
    pinFavoriteBeverage: false,
  };
}

function buildBeverageRowSortContext(sortContext: RecommendationPlanSortContext): RecommendationPlanSortContext {
  return {
    ...sortContext,
    pinFavoriteRecipe: false,
  };
}

function sortFoodDisplayCandidates(
  candidates: FoodCandidate[],
  sortContext: RecommendationPlanSortContext,
  sortProfile?: RecommendationSortProfile,
): FoodCandidate[] {
  const profile = normalizeRecommendationSortProfile(sortProfile);
  const ranges = buildCandidateObjectiveRanges(candidates, getFoodCandidateObjectiveValue);
  return [...candidates].sort((left, right) =>
    compareFoodDisplayCandidates(left, right, profile, sortContext, ranges),
  );
}

function sortBeverageDisplayCandidates(
  candidates: BeverageCandidate[],
  sortContext: RecommendationPlanSortContext,
  sortProfile?: RecommendationSortProfile,
): BeverageCandidate[] {
  const profile = normalizeRecommendationSortProfile(sortProfile);
  const ranges = buildCandidateObjectiveRanges(candidates, getBeverageCandidateObjectiveValue);
  return [...candidates].sort((left, right) =>
    compareBeverageDisplayCandidates(left, right, profile, sortContext, ranges),
  );
}

function compareFoodDisplayCandidates(
  left: FoodCandidate,
  right: FoodCandidate,
  profile: RecommendationSortProfile,
  sortContext: RecommendationPlanSortContext,
  ranges: Map<RecommendationObjectiveKey, ObjectiveRange>,
): number {
  const pinDiff = getFoodCandidatePinRank(right, sortContext) - getFoodCandidatePinRank(left, sortContext);
  if (pinDiff !== 0) return pinDiff;
  const requiredDiff = Number(right.meetsRequiredFood) - Number(left.meetsRequiredFood);
  if (requiredDiff !== 0) return requiredDiff;
  const customSortDiff = compareCustomRecipeOrder(left, right);
  if (customSortDiff !== 0) return customSortDiff;
  const specialBusinessDiff = compareFoodSpecialBusinessPriority(left, right, sortContext);
  if (specialBusinessDiff !== 0) return specialBusinessDiff;
  const scoreDiff = calculateCandidateScore(right, profile, ranges, getFoodCandidateObjectiveValue)
    - calculateCandidateScore(left, profile, ranges, getFoodCandidateObjectiveValue);
  if (scoreDiff !== 0) return scoreDiff;
  return compareFoodCandidates(left, right);
}

function compareBeverageDisplayCandidates(
  left: BeverageCandidate,
  right: BeverageCandidate,
  profile: RecommendationSortProfile,
  sortContext: RecommendationPlanSortContext,
  ranges: Map<RecommendationObjectiveKey, ObjectiveRange>,
): number {
  const pinDiff = getBeverageCandidatePinRank(right, sortContext) - getBeverageCandidatePinRank(left, sortContext);
  if (pinDiff !== 0) return pinDiff;
  const requiredDiff = Number(right.meetsRequiredBeverage) - Number(left.meetsRequiredBeverage);
  if (requiredDiff !== 0) return requiredDiff;
  const specialBusinessDiff = compareBeverageSpecialBusinessPriority(left, right, sortContext);
  if (specialBusinessDiff !== 0) return specialBusinessDiff;
  const scoreDiff = calculateCandidateScore(right, profile, ranges, getBeverageCandidateObjectiveValue)
    - calculateCandidateScore(left, profile, ranges, getBeverageCandidateObjectiveValue);
  if (scoreDiff !== 0) return scoreDiff;
  return compareBeverageCandidates(left, right);
}

function compareFoodSpecialBusinessPriority(
  left: FoodCandidate,
  right: FoodCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  const rankDiff = getFoodSpecialBusinessRank(right, sortContext) - getFoodSpecialBusinessRank(left, sortContext);
  if (rankDiff !== 0) return rankDiff;

  if (sortContext.specialPreferDamageLevel) {
    const negativeDiff = left.matchedNegativeTags.length - right.matchedNegativeTags.length;
    if (negativeDiff !== 0) return negativeDiff;
    const levelDiff = right.recipe.level - left.recipe.level;
    if (levelDiff !== 0) return levelDiff;
    const positiveDiff = right.matchedPositiveTags.length - left.matchedPositiveTags.length;
    if (positiveDiff !== 0) return positiveDiff;
    const priceDiff = right.recipe.price - left.recipe.price;
    if (priceDiff !== 0) return priceDiff;
    const pressureDiff = left.resourcePressure - right.resourcePressure;
    if (pressureDiff !== 0) return pressureDiff;
  }

  if (sortContext.specialPreferHighFoodLevel) {
    const negativeDiff = left.matchedNegativeTags.length - right.matchedNegativeTags.length;
    if (negativeDiff !== 0) return negativeDiff;
    const positiveDiff = right.matchedPositiveTags.length - left.matchedPositiveTags.length;
    if (positiveDiff !== 0) return positiveDiff;
    const levelDiff = right.recipe.level - left.recipe.level;
    if (levelDiff !== 0) return levelDiff;
  }

  if (sortContext.specialPreferYuyukoPositiveSpell) {
    const rankDiff = getYuyukoPositiveSpellFoodCandidateRank(right, sortContext.specialYuyukoRequiredFoodTag)
      - getYuyukoPositiveSpellFoodCandidateRank(left, sortContext.specialYuyukoRequiredFoodTag);
    if (rankDiff !== 0) return rankDiff;
  }

  if (sortContext.specialYuyukoProgressEvaluationMode) {
    const rankDiff = getFoodYuyukoCandidateRank(
      right,
      sortContext.specialYuyukoProgressEvaluationMode,
      sortContext.specialYuyukoRequiredFoodTag,
    ) - getFoodYuyukoCandidateRank(
      left,
      sortContext.specialYuyukoProgressEvaluationMode,
      sortContext.specialYuyukoRequiredFoodTag,
    );
    if (rankDiff !== 0) return rankDiff;
  }

  return 0;
}

function compareBeverageSpecialBusinessPriority(
  left: BeverageCandidate,
  right: BeverageCandidate,
  sortContext: RecommendationPlanSortContext,
): number {
  const rankDiff = getBeverageSpecialBusinessRank(right, sortContext) - getBeverageSpecialBusinessRank(left, sortContext);
  if (rankDiff !== 0) return rankDiff;

  if (sortContext.specialPreferDamageLevel) {
    const levelDiff = right.beverage.level - left.beverage.level;
    if (levelDiff !== 0) return levelDiff;
    const preferenceDiff = right.matchedTags.length - left.matchedTags.length;
    if (preferenceDiff !== 0) return preferenceDiff;
    const priceDiff = right.beverage.price - left.beverage.price;
    if (priceDiff !== 0) return priceDiff;
    const stockDiff = inventoryQuantityRankValue(right.ownedQuantity)
      - inventoryQuantityRankValue(left.ownedQuantity);
    if (stockDiff !== 0) return stockDiff;
  }

  if (sortContext.specialPreferHighBeverageLevel) {
    const preferenceDiff = right.matchedTags.length - left.matchedTags.length;
    if (preferenceDiff !== 0) return preferenceDiff;
    const levelDiff = right.beverage.level - left.beverage.level;
    if (levelDiff !== 0) return levelDiff;
  }

  if (sortContext.specialPreferYuyukoPositiveSpell) {
    const rankDiff = getYuyukoPositiveSpellBeverageCandidateRank(right, sortContext.specialYuyukoRequiredBeverageTag)
      - getYuyukoPositiveSpellBeverageCandidateRank(left, sortContext.specialYuyukoRequiredBeverageTag);
    if (rankDiff !== 0) return rankDiff;
  }

  if (sortContext.specialYuyukoProgressEvaluationMode) {
    const rankDiff = getBeverageYuyukoCandidateRank(
      right,
      sortContext.specialYuyukoProgressEvaluationMode,
      sortContext.specialYuyukoRequiredBeverageTag,
    ) - getBeverageYuyukoCandidateRank(
      left,
      sortContext.specialYuyukoProgressEvaluationMode,
      sortContext.specialYuyukoRequiredBeverageTag,
    );
    if (rankDiff !== 0) return rankDiff;
  }

  return 0;
}

interface ObjectiveRange {
  min: number;
  max: number;
}

function buildCandidateObjectiveRanges<TCandidate>(
  candidates: TCandidate[],
  getValue: (candidate: TCandidate, key: RecommendationObjectiveKey) => number,
): Map<RecommendationObjectiveKey, ObjectiveRange> {
  const ranges = new Map<RecommendationObjectiveKey, ObjectiveRange>();
  for (const { key } of RECOMMENDATION_OBJECTIVE_DEFINITIONS) {
    const values = candidates.map((candidate) => getValue(candidate, key));
    ranges.set(key, values.length === 0
      ? { min: 0, max: 0 }
      : { min: Math.min(...values), max: Math.max(...values) });
  }

  return ranges;
}

function calculateCandidateScore<TCandidate>(
  candidate: TCandidate,
  profile: RecommendationSortProfile,
  ranges: Map<RecommendationObjectiveKey, ObjectiveRange>,
  getValue: (candidate: TCandidate, key: RecommendationObjectiveKey) => number,
): number {
  return profile.objectives.reduce((sum, rule) => {
    if (!rule.enabled || rule.weight <= 0) return sum;
    const range = ranges.get(rule.key);
    const rawValue = getValue(candidate, rule.key);
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

function getFoodCandidateObjectiveValue(
  food: FoodCandidate,
  key: RecommendationObjectiveKey,
): number {
  switch (key) {
    case 'foodPreference':
      return food.matchedPositiveTags.length;
    case 'negativeRisk':
      return food.matchedNegativeTags.length;
    case 'extraCount':
      return food.extraIngredients.length;
    case 'resourcePressure':
      return food.resourcePressure;
    case 'totalCost':
      return food.baseCost + food.extraCost;
    case 'profit':
      return food.recipe.price - food.baseCost - food.extraCost;
    case 'cookerAvailable':
      return food.cookerAvailable ? 1 : 0;
    case 'beveragePreference':
    case 'beverageStock':
      return 0;
  }
}

function getBeverageCandidateObjectiveValue(
  beverage: BeverageCandidate,
  key: RecommendationObjectiveKey,
): number {
  switch (key) {
    case 'beveragePreference':
      return beverage.matchedTags.length;
    case 'profit':
      return beverage.beverage.price;
    case 'beverageStock':
      return inventoryQuantityRankValue(beverage.ownedQuantity);
    case 'foodPreference':
    case 'negativeRisk':
    case 'extraCount':
    case 'resourcePressure':
    case 'totalCost':
    case 'cookerAvailable':
      return 0;
  }
}

function isFoodRecommendationRowEligible(
  food: FoodCandidate,
  sortContext: RecommendationPlanSortContext,
): boolean {
  return food.meetsRequiredFood
    || food.matchedPositiveTags.length > 0
    || food.customRecipe === true
    || getFoodCandidatePinRank(food, sortContext) > 0;
}

function isBeverageRecommendationRowEligible(
  beverage: BeverageCandidate,
  sortContext: RecommendationPlanSortContext,
): boolean {
  return beverage.meetsRequiredBeverage
    || beverage.matchedTags.length > 0
    || getBeverageCandidatePinRank(beverage, sortContext) > 0;
}

function candidateHasNoHardFailures(results: { status: string; severity: string }[]): boolean {
  return !results.some((result) => result.status === 'fail' && result.severity === 'hard');
}

function canPairFoodWithinBudget(
  food: FoodCandidate,
  beverageCandidates: BeverageCandidate[],
  budget: RecommendationBudgetContext | null,
  budgetPolicy: RecommendationBudgetPolicy,
): boolean {
  if (budgetPolicy === 'block' && budget?.willPayMoney === false) return false;
  if (!isBudgetBlockingPairing(budget, budgetPolicy)) return true;
  return beverageCandidates.some((beverage) =>
    candidateHasNoHardFailures(beverage.conditionResults)
    && isWithinBlockingBudget(food.recipe.price + beverage.beverage.price, budget),
  );
}

function canPairBeverageWithinBudget(
  beverage: BeverageCandidate,
  foodCandidates: FoodCandidate[],
  budget: RecommendationBudgetContext | null,
  budgetPolicy: RecommendationBudgetPolicy,
): boolean {
  if (budgetPolicy === 'block' && budget?.willPayMoney === false) return false;
  if (!isBudgetBlockingPairing(budget, budgetPolicy)) return true;
  return foodCandidates.some((food) =>
    candidateHasNoHardFailures(food.conditionResults)
    && isWithinBlockingBudget(food.recipe.price + beverage.beverage.price, budget),
  );
}

function isBudgetBlockingPairing(
  budget: RecommendationBudgetContext | null,
  budgetPolicy: RecommendationBudgetPolicy,
): budget is RecommendationBudgetContext {
  return budgetPolicy === 'block'
    && budget != null
    && budget.willPayMoney !== false
    && Number.isFinite(budget.remainingBudget);
}

function isWithinBlockingBudget(estimatedPrice: number, budget: RecommendationBudgetContext): boolean {
  const remainingBudget = Math.max(0, Math.trunc(budget.remainingBudget ?? 0));
  return Math.max(0, estimatedPrice) <= remainingBudget;
}

export function toRareRecipeResult(food: FoodCandidate): RareRecipeRecommendation {
  return {
    recipe: food.recipe,
    extraIngredients: food.extraIngredients,
    customRecipe: food.customRecipe,
    customRecipePinned: food.customRecipePinned,
    customRecipeSortOrder: food.customRecipeSortOrder,
    customRecipeScope: food.customRecipeScope,
    customRecipeId: food.customRecipeId,
    extraIngredientReasonTags: food.extraIngredientReasonTags,
    allTags: food.activeTags,
    cancelledTags: food.suppressedTags,
    meetsRequiredFood: food.meetsRequiredFood,
    baseCost: food.baseCost,
    extraCost: food.extraCost,
  };
}

function toRareBeverageResult(beverage: BeverageCandidate): RareBeverageRecommendation {
  return {
    beverage: beverage.beverage,
    meetsRequiredBev: beverage.meetsRequiredBeverage,
    matchedTags: beverage.matchedTags,
  };
}

function buildRareTagOrderDemand(
  customer: RareCustomerCatalogItem,
  requiredFoodTag: string,
  requiredBeverageTag: string,
  specialFoodTargetTags: readonly string[] = [],
) {
  return {
    type: 'rare-tag-order' as const,
    customer,
    requiredFoodTag,
    requiredBeverageTag,
    specialFoodTargetTags: normalizeSpecialBusinessTags(specialFoodTargetTags),
  };
}

function buildFoodCandidateCacheKey(
  data: RecommendationDataSet,
  customer: RareCustomerCatalogItem,
  requiredFoodTag: string,
  context: RecommendationRuntimeContext,
  specialFoodTargetTags: readonly string[] = [],
): string {
  return [
    'foodCandidates',
    serializeDataSignature(data),
    serializeRareCustomerFoodProfile(customer),
    `requiredFood:${requiredFoodTag}`,
    `specialFood:${normalizeSpecialBusinessTags(specialFoodTargetTags).sort().join(';')}`,
    `recipes:${serializeNumberSet(context.availableRecipeIds)}`,
    `ingredients:${serializeNumberSet(context.availableIngredientIds)}`,
    `excludedIngredients:${serializeNumberSet(context.excludedIngredientIds)}`,
    `ownedIngredients:${serializeNumberRecord(context.ownedIngredientQty)}`,
    `cookers:${serializeStringSet(context.placedCookerNames)}`,
    `hasCookers:${context.hasCookerSnapshot ? '1' : '0'}`,
    `filterCookers:${context.filterMissingCookers ? '1' : '0'}`,
    `popular:${context.popularFoodTag ?? ''}`,
    `popularHate:${context.popularHateFoodTag ?? ''}`,
    `famous:${context.famousShopEnabled ? '1' : '0'}`,
    `tagRules:${serializeTagPriorityRules(context.tagPriorityRules)}`,
    `extraSlots:${context.maxExtraIngredients}`,
  ].join('|');
}

function buildBeverageCandidateCacheKey(
  data: RecommendationDataSet,
  customer: RareCustomerCatalogItem,
  requiredBeverageTag: string,
  context: RecommendationRuntimeContext,
): string {
  return [
    'beverageCandidates',
    serializeDataSignature(data),
    serializeRareCustomerBeverageProfile(customer),
    `requiredBeverage:${requiredBeverageTag}`,
    `beverages:${serializeNumberSet(context.availableBeverageIds)}`,
    `excludedBeverages:${serializeNumberSet(context.excludedBeverageIds)}`,
    `ownedBeverages:${serializeNumberRecord(context.ownedBeverageQty)}`,
  ].join('|');
}

function serializeDataSignature(data: RecommendationDataSet): string {
  return `${data.source}:${data.status}`;
}

function serializeRareCustomerFoodProfile(customer: RareCustomerCatalogItem): string {
  return [
    `customer:${customer.id}`,
    `positive:${serializeStringList(customer.positiveTags)}`,
    `negative:${serializeStringList(customer.negativeTags)}`,
  ].join(';');
}

function serializeRareCustomerBeverageProfile(customer: RareCustomerCatalogItem): string {
  return [
    `customer:${customer.id}`,
    `beverages:${serializeStringList(customer.beverageTags)}`,
  ].join(';');
}

function serializeTagPriorityRules(rules: RecommendationDataSet['tagPriorityRules']): string {
  return rules
    .map((rule) => [
      rule.id,
      serializeNumberList(rule.tagIds),
      serializeStringList(rule.tags),
    ].join(':'))
    .sort()
    .join(';');
}

function serializeNumberSet(values: Set<number>): string {
  return serializeNumberList([...values]);
}

function serializeNumberList(values: number[]): string {
  return [...values].sort((left, right) => left - right).join(',');
}

function serializeStringSet(values: Set<string>): string {
  return serializeStringList([...values]);
}

function serializeStringList(values: string[]): string {
  return [...values].sort().join(',');
}

function serializeNumberRecord(values: Record<number, number>): string {
  return Object.entries(values)
    .sort(([left], [right]) => Number(left) - Number(right))
    .map(([id, qty]) => `${id}:${qty}`)
    .join(',');
}

function serializeRecommendationPlanSortContext(context: RecommendationPlanSortContext): string {
  return [
    `recipeFav:${[...(context.favoriteRecipeKeys ?? [])].sort().join(';')}`,
    `bevFav:${[...(context.favoriteBeverageIds ?? [])].sort((left, right) => left - right).join(',')}`,
    `pinRecipeFav:${context.pinFavoriteRecipe ? '1' : '0'}`,
    `pinBevFav:${context.pinFavoriteBeverage ? '1' : '0'}`,
    `specialFood:${[...(context.specialTargetFoodTags ?? [])].sort().join(';')}`,
    `specialBeverage:${[...(context.specialTargetBeverageTags ?? [])].sort().join(';')}`,
    `specialHighFoodLevel:${context.specialPreferHighFoodLevel ? '1' : '0'}`,
    `specialHighBeverageLevel:${context.specialPreferHighBeverageLevel ? '1' : '0'}`,
    `specialDamageLevel:${context.specialPreferDamageLevel ? '1' : '0'}`,
    `specialYuyukoPositiveSpell:${context.specialPreferYuyukoPositiveSpell ? '1' : '0'}`,
    `specialYuyukoProgress:${context.specialYuyukoProgressEvaluationMode ?? 'none'}`,
    `specialYuyukoRequiredFoodTag:${context.specialYuyukoRequiredFoodTag ?? ''}`,
    `specialYuyukoRequiredBeverageTag:${context.specialYuyukoRequiredBeverageTag ?? ''}`,
    `specialKoishiRemainingScore:${context.specialKoishiRemainingScore ?? ''}`,
    `specialKoishiRemainingOrderCount:${context.specialKoishiRemainingOrderCount ?? ''}`,
  ].join('|');
}

function serializeBudgetContext(context: RecommendationBudgetContext | null): string {
  if (!context) return 'budget:none';
  return [
    'budget',
    context.source,
    context.remainingBudget ?? '',
    context.willPayMoney == null ? '' : context.willPayMoney ? '1' : '0',
  ].join(':');
}

function getFoodCandidateCustomSortOrder(food: FoodCandidate): number {
  return food.customRecipe ? food.customRecipeSortOrder ?? Number.MAX_SAFE_INTEGER : Number.MAX_SAFE_INTEGER;
}

function compareCustomRecipeOrder(left: FoodCandidate, right: FoodCandidate): number {
  if (!left.customRecipe || !right.customRecipe) return 0;
  return getFoodCandidateCustomSortOrder(left) - getFoodCandidateCustomSortOrder(right);
}

function buildRecipeSortKey(recipeId: number, extraIngredientIds: number[]): string {
  return `${recipeId}:${normalizeIdList(extraIngredientIds).join(',')}`;
}

function trimCache<TValue>(cache: Map<string, TValue>, maxSize: number) {
  if (cache.size <= maxSize) return;
  const overflow = cache.size - maxSize;
  const keys = cache.keys();
  for (let index = 0; index < overflow; index += 1) {
    const key = keys.next().value;
    if (key === undefined) return;
    cache.delete(key);
  }
}

function isUsableRareCustomerName(value: string): boolean {
  const name = value.trim();
  return Boolean(name)
    && name !== 'missing'
    && name !== 'null'
    && !name.includes('?')
    && !name.startsWith('#')
    && !/^[A-Za-z0-9_]+$/.test(name);
}
