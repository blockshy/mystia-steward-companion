import type { RareAutomationRecipeTarget } from '@/companion/automation-state';
import type {
  CookerRequirement,
  NormalBusinessOrder,
  RecommendationStateSnapshot,
  RuntimeSets,
} from '@/companion/types';
import {
  DEFAULT_RECOMMENDATION_DATA,
  buildRecommendationDataIndexes,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type { RecipeCatalogItem } from '@/lib/catalog-types';

const COOKER_TYPE_NAME_BY_ID = new Map<number, string>([
  [1, '煮锅'],
  [2, '烧烤架'],
  [3, '油锅'],
  [4, '蒸锅'],
  [5, '料理台'],
]);

const COOKER_NAME_ALIASES = new Map<string, string>([
  ['烤架', '烧烤架'],
  ['烧烤台', '烧烤架'],
  ['锅', '煮锅'],
  ['炸锅', '油锅'],
]);

export function buildRuntimeSets(
  runtime: RecommendationStateSnapshot | null,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): RuntimeSets | null {
  if (!runtime) return null;
  const ingredientIds = new Set(runtime.availableIngredientIds);
  const allIngredientIds = data.ingredients.map((ingredient) => ingredient.id);
  const unavailableIngredientIds = new Set(allIngredientIds.filter((id) => !ingredientIds.has(id)));

  return {
    recipeIds: new Set(runtime.availableRecipeIds),
    beverageIds: new Set(runtime.availableBeverageIds),
    ingredientIds,
    unavailableIngredientIds,
    ownedIngredientQty: normalizeOwnedIngredientQty(runtime.ownedIngredientQty),
    ownedBeverageQty: normalizeOwnedIngredientQty(runtime.ownedBeverageQty ?? {}),
    placedCookerTypeIds: new Set(runtime.placedCookerTypeIds ?? []),
    placedCookerNames: buildPlacedCookerNameSet(runtime),
    hasCookerSnapshot: (runtime.placedCookers?.length ?? 0) > 0 || (runtime.placedCookerTypeIds?.length ?? 0) > 0,
  };
}

export function buildAutomationCookerCapacity(runtime: RecommendationStateSnapshot | null | undefined): Map<string, number> {
  const capacity = new Map<string, number>();
  if (!runtime) return capacity;

  for (const cooker of runtime.placedCookers ?? []) {
    const keys = new Set<string>();
    for (const typeName of cooker.typeNames ?? []) {
      const normalized = normalizeCookerName(typeName);
      if (normalized) keys.add(normalized);
    }

    for (const typeId of cooker.typeIds ?? []) {
      const mapped = COOKER_TYPE_NAME_BY_ID.get(typeId);
      const normalized = normalizeCookerName(mapped);
      if (normalized) keys.add(normalized);
    }

    const name = normalizeCookerName(cooker.name);
    if (name) keys.add(name);

    for (const key of keys) {
      capacity.set(key, (capacity.get(key) ?? 0) + 1);
    }
  }

  if (capacity.size === 0) {
    for (const typeId of runtime.placedCookerTypeIds ?? []) {
      const key = normalizeCookerName(COOKER_TYPE_NAME_BY_ID.get(typeId));
      if (!key) continue;
      capacity.set(key, Math.max(1, capacity.get(key) ?? 0));
    }
  }

  return capacity;
}

export function getCookerSlotCapacity(key: string, capacity: Map<string, number>): number {
  return Math.max(1, capacity.get(key) ?? 1);
}

export function getRareCookerRequirement(target: RareAutomationRecipeTarget | null): CookerRequirement | null {
  const key = normalizeCookerName(target?.cookerName);
  if (!key) return null;
  return {
    key,
    label: key,
  };
}

export function getNormalCookerRequirement(
  order: NormalBusinessOrder,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): CookerRequirement | null {
  const recipe = getNormalOrderRecipe(order, data);
  if (!recipe) return null;
  return getRecipeCookerRequirement(recipe);
}

function getRecipeCookerRequirement(recipe: RecipeCatalogItem | null | undefined): CookerRequirement | null {
  const key = normalizeCookerName(recipe?.cooker);
  if (!key) return null;
  return {
    key,
    label: key,
  };
}

function getNormalOrderRecipe(
  order: NormalBusinessOrder,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): RecipeCatalogItem | null {
  const indexes = buildRecommendationDataIndexes(data);
  return indexes.recipeByFoodId.get(order.foodId) ?? null;
}

export function resolveCookerTypeId(value: string | null | undefined): number {
  const normalized = normalizeCookerName(value);
  if (!normalized) return -1;

  for (const [typeId, name] of COOKER_TYPE_NAME_BY_ID) {
    if (normalizeCookerName(name) === normalized) return typeId;
  }

  return -1;
}

function normalizeCookerName(value: string | null | undefined): string {
  const normalized = (value ?? '').trim();
  if (!normalized) return '';
  return COOKER_NAME_ALIASES.get(normalized) ?? normalized;
}

function buildPlacedCookerNameSet(runtime: RecommendationStateSnapshot): Set<string> {
  const names = new Set<string>();
  for (const typeId of runtime.placedCookerTypeIds ?? []) {
    const mapped = COOKER_TYPE_NAME_BY_ID.get(typeId);
    if (mapped) names.add(normalizeCookerName(mapped));
  }
  for (const cooker of runtime.placedCookers ?? []) {
    for (const name of [cooker.name, ...(cooker.typeNames ?? [])]) {
      const normalized = normalizeCookerName(name);
      if (normalized) names.add(normalized);
    }
  }
  return names;
}

function normalizeOwnedIngredientQty(ownedIngredientQty: Record<string, number>): Record<number, number> {
  return Object.fromEntries(
    Object.entries(ownedIngredientQty).map(([id, qty]) => [Number(id), qty]),
  ) as Record<number, number>;
}
