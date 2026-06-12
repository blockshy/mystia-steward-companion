import allBeverages from '@/data/beverages.json';
import allIngredients from '@/data/ingredients.json';
import allNormalCustomers from '@/data/customer_normal.json';
import allRareCustomers from '@/data/customer_rare.json';
import allRecipes from '@/data/recipes.json';
import type {
  IBeverage,
  ICustomerNormal,
  ICustomerRare,
  IIngredient,
  IRecipe,
  TPlace,
} from '@/lib/types';
import { ALL_PLACES } from '@/lib/types';

export interface RecommendationDataSet {
  recipes: IRecipe[];
  ingredients: IIngredient[];
  beverages: IBeverage[];
  normalCustomers: ICustomerNormal[];
  rareCustomers: ICustomerRare[];
  source: 'bundled' | 'runtime';
  status: string;
}

export const DEFAULT_RECOMMENDATION_DATA: RecommendationDataSet = {
  recipes: allRecipes as IRecipe[],
  ingredients: allIngredients as IIngredient[],
  beverages: allBeverages as IBeverage[],
  normalCustomers: allNormalCustomers as ICustomerNormal[],
  rareCustomers: allRareCustomers as unknown as ICustomerRare[],
  source: 'bundled',
  status: 'bundled json',
};

export interface RuntimeDataCatalogSnapshot {
  isComplete: boolean;
  source: string;
  status: string;
  recipes: Array<Partial<IRecipe> & { id: number; recipeId: number; name: string }>;
  ingredients: Array<Partial<IIngredient> & { id: number; name: string }>;
  beverages: Array<Partial<IBeverage> & { id: number; name: string }>;
  normalCustomers: Array<Partial<ICustomerNormal> & { id: number; name: string }>;
  rareCustomers: Array<Partial<ICustomerRare> & { id: number; name: string }>;
  foodTagIdMap?: Record<string, string>;
}

export function buildRecommendationDataSet(
  runtimeData: RuntimeDataCatalogSnapshot | null | undefined,
): RecommendationDataSet {
  if (!runtimeData?.isComplete) return DEFAULT_RECOMMENDATION_DATA;

  const recipes = runtimeData.recipes
    .map(normalizeRuntimeRecipe)
    .filter((item): item is IRecipe => item !== null);
  const ingredients = runtimeData.ingredients
    .map(normalizeRuntimeIngredient)
    .filter((item): item is IIngredient => item !== null);
  const beverages = runtimeData.beverages
    .map(normalizeRuntimeBeverage)
    .filter((item): item is IBeverage => item !== null);
  const normalCustomers = runtimeData.normalCustomers
    .map(normalizeRuntimeNormalCustomer)
    .filter((item): item is ICustomerNormal => item !== null);
  const rareCustomers = runtimeData.rareCustomers
    .map(normalizeRuntimeRareCustomerData)
    .filter((item): item is ICustomerRare => item !== null);

  if (
    recipes.length === 0
    || ingredients.length === 0
    || beverages.length === 0
    || normalCustomers.length === 0
    || rareCustomers.length === 0
  ) {
    return DEFAULT_RECOMMENDATION_DATA;
  }

  return {
    recipes,
    ingredients,
    beverages,
    normalCustomers,
    rareCustomers,
    source: 'runtime',
    status: runtimeData.status || runtimeData.source || 'game runtime',
  };
}

export function buildRecommendationDataIndexes(data: RecommendationDataSet) {
  return {
    ingredientByName: new Map(data.ingredients.map((ingredient) => [ingredient.name, ingredient])),
    ingredientIdByName: new Map(data.ingredients.map((ingredient) => [ingredient.name, ingredient.id])),
    ingredientNameById: new Map(data.ingredients.map((ingredient) => [ingredient.id, ingredient.name])),
    beverageNameById: new Map(data.beverages.map((beverage) => [beverage.id, beverage.name])),
    recipeByFoodId: new Map(data.recipes.map((recipe) => [recipe.id, recipe])),
  };
}

function normalizeRuntimeRecipe(value: RuntimeDataCatalogSnapshot['recipes'][number]): IRecipe | null {
  if (!Number.isFinite(value.id) || !Number.isFinite(value.recipeId) || !value.name) return null;
  return {
    id: value.id,
    recipeId: value.recipeId,
    name: value.name,
    description: value.description ?? '',
    ingredients: normalizeStringArray(value.ingredients),
    positiveTags: normalizeStringArray(value.positiveTags),
    negativeTags: normalizeStringArray(value.negativeTags),
    cooker: value.cooker ?? '',
    baseCookTime: value.baseCookTime ?? 0,
    dlc: value.dlc ?? 0,
    level: value.level ?? 0,
    price: value.price ?? 0,
    from: value.from ?? {},
  };
}

function normalizeRuntimeIngredient(value: RuntimeDataCatalogSnapshot['ingredients'][number]): IIngredient | null {
  if (!Number.isFinite(value.id) || !value.name) return null;
  return {
    id: value.id,
    name: value.name,
    description: value.description ?? '',
    type: value.type ?? '',
    tags: normalizeStringArray(value.tags),
    dlc: value.dlc ?? 0,
    level: value.level ?? 0,
    price: value.price ?? 0,
    from: value.from ?? {},
  };
}

function normalizeRuntimeBeverage(value: RuntimeDataCatalogSnapshot['beverages'][number]): IBeverage | null {
  if (!Number.isFinite(value.id) || !value.name) return null;
  return {
    id: value.id,
    name: value.name,
    description: value.description ?? '',
    tags: normalizeStringArray(value.tags),
    dlc: value.dlc ?? 0,
    level: value.level ?? 0,
    price: value.price ?? 0,
    from: value.from ?? {},
  };
}

function normalizeRuntimeNormalCustomer(
  value: RuntimeDataCatalogSnapshot['normalCustomers'][number],
): ICustomerNormal | null {
  if (!Number.isFinite(value.id) || !value.name) return null;
  return {
    id: value.id,
    name: value.name,
    description: value.description ?? '',
    dlc: value.dlc ?? 0,
    places: normalizePlaces(value.places),
    positiveTags: normalizeStringArray(value.positiveTags),
    beverageTags: normalizeStringArray(value.beverageTags),
  };
}

function normalizeRuntimeRareCustomerData(
  value: RuntimeDataCatalogSnapshot['rareCustomers'][number],
): ICustomerRare | null {
  if (!Number.isFinite(value.id) || !value.name) return null;
  return {
    id: value.id,
    name: value.name,
    description: value.description ?? '',
    dlc: value.dlc ?? 0,
    places: normalizePlaces(value.places),
    price: value.price ?? [0, 0],
    enduranceLimit: value.enduranceLimit ?? 1,
    positiveTags: normalizeStringArray(value.positiveTags),
    negativeTags: normalizeStringArray(value.negativeTags),
    beverageTags: normalizeStringArray(value.beverageTags),
    positiveTagMapping: value.positiveTagMapping ?? {},
    beverageTagMapping: value.beverageTagMapping ?? {},
    collection: value.collection ?? false,
    evaluation: value.evaluation ?? {},
    spellCards: value.spellCards ?? { positive: [], negative: [] },
  };
}

function normalizeStringArray(value: unknown): string[] {
  return Array.isArray(value)
    ? [...new Set(value.map((item) => String(item).trim()).filter(Boolean))]
    : [];
}

function normalizePlaces(value: unknown): TPlace[] {
  const places = normalizeStringArray(value)
    .filter((place): place is TPlace => (ALL_PLACES as string[]).includes(place));
  return places.length > 0 ? places : [...ALL_PLACES];
}
