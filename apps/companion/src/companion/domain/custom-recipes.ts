import type {
  CustomRecipeData,
  CustomRecipeEntry,
  CustomRecipeUpsertInput,
} from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { IngredientCatalogItem, RareCustomerCatalogItem, RecipeCatalogItem } from '@/lib/catalog-types';
import {
  compareFoodCandidates,
  hasForbiddenIngredientTag,
  resolveFoodTags,
  type ConditionResult,
  type FoodCandidate,
  type RareTagOrderDemand,
  type RecommendationRuntimeContext,
} from '@/recommendation-engine';

const MAX_FOOD_INGREDIENT_COUNT = 5;
const LOW_STOCK_THRESHOLD = 5;

interface BuildCustomFoodCandidatesOptions {
  customRecipes: CustomRecipeData;
  data: RecommendationDataSet;
  customer: RareCustomerCatalogItem;
  requiredFoodTag: string;
  requiredBeverageTag: string;
  context: RecommendationRuntimeContext;
}

export function emptyCustomRecipeData(): CustomRecipeData {
  return {
    version: 1,
    recipes: [],
  };
}

export function normalizeCustomRecipeData(data: CustomRecipeData | null | undefined): CustomRecipeData {
  return {
    version: Math.max(1, data?.version ?? 1),
    recipes: (data?.recipes ?? [])
      .map(normalizeCustomRecipeEntry)
      .filter((entry): entry is CustomRecipeEntry => Boolean(entry))
      .sort(compareCustomRecipeEntries),
  };
}

export function normalizeCustomRecipeUpsertInput(input: CustomRecipeUpsertInput): CustomRecipeUpsertInput {
  return {
    ...input,
    id: input.id?.trim() || undefined,
    customerId: normalizeNonNegativeInteger(input.customerId, -1),
    customerName: input.customerName.trim(),
    foodTag: normalizeOptionalTag(input.foodTag),
    foodId: normalizeNonNegativeInteger(input.foodId, -1),
    recipeId: normalizeNonNegativeInteger(input.recipeId, -1),
    recipeName: input.recipeName.trim(),
    extraIngredientIds: normalizeIdList(input.extraIngredientIds),
    enabled: Boolean(input.enabled),
    pinToTop: Boolean(input.pinToTop),
    sortOrder: input.sortOrder == null ? undefined : normalizeNonNegativeInteger(input.sortOrder, 0),
  };
}

export function customRecipeScopeKey(entry: Pick<CustomRecipeEntry, 'customerId' | 'foodTag'>): string {
  return `${entry.customerId}:${entry.foodTag ?? '*'}`;
}

export function customRecipeResultKey(entry: Pick<CustomRecipeEntry, 'foodId' | 'extraIngredientIds'>): string {
  return `${entry.foodId}:${normalizeIdList(entry.extraIngredientIds).join(',')}`;
}

export function compareCustomRecipeEntries(left: CustomRecipeEntry, right: CustomRecipeEntry): number {
  if (left.sortOrder !== right.sortOrder) return left.sortOrder - right.sortOrder;
  if (left.customerId !== right.customerId) return left.customerId - right.customerId;
  const tagDiff = (left.foodTag ?? '').localeCompare(right.foodTag ?? '');
  if (tagDiff !== 0) return tagDiff;
  return left.id.localeCompare(right.id);
}

export function getEffectiveCustomRecipeEntries(
  customRecipes: CustomRecipeData,
  customerId: number,
  foodTag: string,
): CustomRecipeEntry[] {
  return normalizeCustomRecipeData(customRecipes).recipes.filter((entry) =>
    entry.enabled
    && entry.customerId === customerId
    && (entry.foodTag === null || entry.foodTag === foodTag)
  );
}

export function serializeCustomRecipeContext(
  customRecipes: CustomRecipeData,
  customerId: number,
  foodTag: string,
): string {
  return [
    'customRecipes',
    ...getEffectiveCustomRecipeEntries(customRecipes, customerId, foodTag)
      .map((entry) => [
        entry.id,
        entry.foodTag ?? '*',
        entry.foodId,
        normalizeIdList(entry.extraIngredientIds).join(','),
        entry.enabled ? '1' : '0',
        entry.pinToTop ? '1' : '0',
        entry.sortOrder,
      ].join(':')),
  ].join('|');
}

export function buildCustomFoodCandidates({
  customRecipes,
  data,
  customer,
  requiredFoodTag,
  requiredBeverageTag,
  context,
}: BuildCustomFoodCandidatesOptions): FoodCandidate[] {
  const demand: RareTagOrderDemand = {
    type: 'rare-tag-order',
    customer,
    requiredFoodTag,
    requiredBeverageTag,
  };
  const recipesById = new Map(data.recipes.map((recipe) => [recipe.id, recipe]));
  const ingredientsByName = new Map(data.ingredients.map((ingredient) => [ingredient.name, ingredient]));
  const ingredientsById = new Map(data.ingredients.map((ingredient) => [ingredient.id, ingredient]));
  const candidates: FoodCandidate[] = [];
  const seen = new Set<string>();

  for (const entry of getEffectiveCustomRecipeEntries(customRecipes, customer.id, requiredFoodTag)) {
    const recipe = recipesById.get(entry.foodId);
    if (!recipe) continue;
    const extraIngredients = entry.extraIngredientIds
      .map((id) => ingredientsById.get(id))
      .filter((ingredient): ingredient is IngredientCatalogItem => Boolean(ingredient));
    if (extraIngredients.length !== entry.extraIngredientIds.length) continue;
    if (!isCustomRecipeFoodCandidateAllowed(recipe, extraIngredients, ingredientsByName, context)) continue;

    const candidate = buildCustomFoodCandidate(recipe, extraIngredients, entry, demand, context, ingredientsByName);
    const key = foodCandidateResultKey(candidate);
    if (seen.has(key)) continue;
    seen.add(key);
    candidates.push(candidate);
  }

  return candidates.sort(compareCustomFoodCandidates);
}

export function mergeCustomFoodCandidates(
  foodCandidates: FoodCandidate[],
  customFoodCandidates: FoodCandidate[],
): FoodCandidate[] {
  if (customFoodCandidates.length === 0) return foodCandidates;
  const seen = new Set<string>();
  const merged: FoodCandidate[] = [];

  for (const candidate of customFoodCandidates) {
    const key = foodCandidateResultKey(candidate);
    if (seen.has(key)) continue;
    seen.add(key);
    merged.push(candidate);
  }
  for (const candidate of foodCandidates) {
    const key = foodCandidateResultKey(candidate);
    if (seen.has(key)) continue;
    seen.add(key);
    merged.push(candidate);
  }

  return merged;
}

export function normalizeIdList(ids: number[]): number[] {
  return [...new Set(ids.filter((id) => Number.isFinite(id) && id >= 0).map((id) => Math.trunc(id)))].sort((a, b) => a - b);
}

function isCustomRecipeFoodCandidateAllowed(
  recipe: RecipeCatalogItem,
  extraIngredients: IngredientCatalogItem[],
  ingredientsByName: Map<string, IngredientCatalogItem>,
  context: RecommendationRuntimeContext,
): boolean {
  if (!context.availableRecipeIds.has(recipe.id)) return false;
  if (recipe.ingredients.length + extraIngredients.length > MAX_FOOD_INGREDIENT_COUNT) return false;
  if (!hasAvailableBaseIngredients(recipe, ingredientsByName, context)) return false;
  if (context.filterMissingCookers && !isCookerAvailable(recipe, context)) return false;

  const baseIngredientIds = new Set(recipe.ingredients
    .map((name) => ingredientsByName.get(name)?.id ?? -1)
    .filter((id) => id >= 0));
  return extraIngredients.every((ingredient) =>
    context.availableIngredientIds.has(ingredient.id)
    && !isIngredientExcluded(ingredient.id, context)
    && !baseIngredientIds.has(ingredient.id)
    && !hasForbiddenIngredientTag(ingredient, recipe)
  );
}

function buildCustomFoodCandidate(
  recipe: RecipeCatalogItem,
  extraIngredients: IngredientCatalogItem[],
  entry: CustomRecipeEntry,
  demand: RareTagOrderDemand,
  context: RecommendationRuntimeContext,
  ingredientsByName: Map<string, IngredientCatalogItem>,
): FoodCandidate {
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
  const meetsRequiredFood = resolved.activeTags.includes(demand.requiredFoodTag);
  const cookerAvailable = isCookerAvailable(recipe, context);
  const baseCost = recipe.ingredients.reduce((sum, name) => sum + (ingredientsByName.get(name)?.price ?? 0), 0);
  const extraCost = extraIngredients.reduce((sum, ingredient) => sum + ingredient.price, 0);

  return {
    recipe,
    extraIngredients,
    customRecipe: true,
    customRecipePinned: entry.pinToTop,
    customRecipeSortOrder: entry.sortOrder,
    customRecipeScope: entry.foodTag === null ? 'all' : 'tag',
    customRecipeId: entry.id,
    extraIngredientReasonTags: buildExtraIngredientReasons(extraIngredients, demand),
    activeTags: resolved.activeTags,
    suppressedTags: resolved.suppressedTags,
    matchedPositiveTags,
    matchedNegativeTags,
    meetsRequiredFood,
    baseCost,
    extraCost,
    resourcePressure: calculateResourcePressure(extraIngredients, context.ownedIngredientQty),
    cookerAvailable,
    conditionResults: buildCustomFoodConditionResults({
      recipe,
      entry,
      demand,
      meetsRequiredFood,
      matchedPositiveTags,
      matchedNegativeTags,
      suppressedTags: resolved.suppressedTags,
      cookerAvailable,
    }),
  };
}

function buildCustomFoodConditionResults({
  recipe,
  entry,
  demand,
  meetsRequiredFood,
  matchedPositiveTags,
  matchedNegativeTags,
  suppressedTags,
  cookerAvailable,
}: {
  recipe: RecipeCatalogItem;
  entry: CustomRecipeEntry;
  demand: RareTagOrderDemand;
  meetsRequiredFood: boolean;
  matchedPositiveTags: string[];
  matchedNegativeTags: string[];
  suppressedTags: string[];
  cookerAvailable: boolean;
}): ConditionResult[] {
  const results: ConditionResult[] = [
    {
      id: 'food.custom-recipe',
      target: 'food',
      status: 'info',
      severity: 'info',
      label: '自定义配方',
      detail: entry.foodTag === null
        ? '该自定义配方适用于该稀客的所有点单料理 Tag。'
        : `该自定义配方绑定点单料理 ${entry.foodTag}。`,
    },
    {
      id: 'food.required-tag',
      target: 'food',
      status: meetsRequiredFood ? 'pass' : 'warn',
      severity: meetsRequiredFood ? 'hard' : 'soft',
      label: '料理点单',
      detail: meetsRequiredFood
        ? `满足点单料理 ${demand.requiredFoodTag}`
        : `未满足点单料理 ${demand.requiredFoodTag}`,
    },
  ];

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
  if (matchedPositiveTags.length > 0) {
    results.push({
      id: 'food.preference',
      target: 'food',
      status: 'boost',
      severity: 'soft',
      label: '料理偏好',
      detail: `命中 ${matchedPositiveTags.join('、')}`,
    });
  }
  if (matchedNegativeTags.length > 0) {
    results.push({
      id: 'food.negative-tags',
      target: 'food',
      status: 'warn',
      severity: 'soft',
      label: '厌恶标签',
      detail: `包含 ${matchedNegativeTags.join('、')}`,
    });
  }
  if (suppressedTags.length > 0) {
    results.push({
      id: 'food.suppressed-tags',
      target: 'food',
      status: 'info',
      severity: 'info',
      label: '标签优先级',
      detail: `压制 ${suppressedTags.join('、')}`,
    });
  }

  return results;
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

function isIngredientExcluded(id: number, context: RecommendationRuntimeContext): boolean {
  return context.disabledIngredientIds.has(id) || context.excludedIngredientIds.has(id);
}

function isCookerAvailable(recipe: RecipeCatalogItem, context: RecommendationRuntimeContext): boolean {
  if (!context.hasCookerSnapshot) return true;
  return context.placedCookerNames.has(recipe.cooker);
}

function buildExtraIngredientReasons(
  extraIngredients: IngredientCatalogItem[],
  demand: RareTagOrderDemand,
): Record<number, string[]> {
  const result: Record<number, string[]> = {};
  const relevantTags = new Set([demand.requiredFoodTag, ...demand.customer.positiveTags]);
  for (const ingredient of extraIngredients) {
    const reasons = ingredient.tags.filter((tag) => relevantTags.has(tag));
    if (reasons.length > 0) result[ingredient.id] = reasons;
  }
  return result;
}

function calculateResourcePressure(
  ingredients: IngredientCatalogItem[],
  ownedIngredientQty: Record<number, number>,
): number {
  return ingredients.reduce((sum, ingredient) => {
    const qty = ownedIngredientQty[ingredient.id] ?? 0;
    return sum + Math.max(0, LOW_STOCK_THRESHOLD - qty);
  }, 0);
}

function compareCustomFoodCandidates(left: FoodCandidate, right: FoodCandidate): number {
  const leftOrder = left.customRecipeSortOrder ?? Number.MAX_SAFE_INTEGER;
  const rightOrder = right.customRecipeSortOrder ?? Number.MAX_SAFE_INTEGER;
  if (leftOrder !== rightOrder) return leftOrder - rightOrder;
  return compareFoodCandidates(left, right);
}

function foodCandidateResultKey(candidate: FoodCandidate): string {
  return `${candidate.recipe.id}:${normalizeIdList(candidate.extraIngredients.map((ingredient) => ingredient.id)).join(',')}`;
}

function normalizeCustomRecipeEntry(entry: CustomRecipeEntry): CustomRecipeEntry | null {
  const id = entry.id?.trim();
  const customerId = normalizeNonNegativeInteger(entry.customerId, -1);
  const foodId = normalizeNonNegativeInteger(entry.foodId, -1);
  if (!id || customerId < 0 || foodId < 0) return null;

  return {
    id,
    customerId,
    customerName: (entry.customerName ?? '').trim(),
    foodTag: normalizeOptionalTag(entry.foodTag),
    foodId,
    recipeId: normalizeNonNegativeInteger(entry.recipeId, -1),
    recipeName: (entry.recipeName ?? '').trim(),
    extraIngredientIds: normalizeIdList(entry.extraIngredientIds ?? []),
    enabled: entry.enabled !== false,
    pinToTop: entry.pinToTop !== false,
    sortOrder: normalizeNonNegativeInteger(entry.sortOrder, 0),
    createdAtUtc: entry.createdAtUtc || '',
    updatedAtUtc: entry.updatedAtUtc || '',
  };
}

function normalizeOptionalTag(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

function normalizeNonNegativeInteger(value: number, fallback: number): number {
  return Number.isFinite(value) && value >= 0 ? Math.trunc(value) : fallback;
}
