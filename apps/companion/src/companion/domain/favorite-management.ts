import type {
  FavoriteBeverageEntry,
  FavoriteData,
  FavoriteRecipeEntry,
} from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

export type FavoriteManagementFilter = 'all' | 'recipe' | 'beverage';

interface FavoriteManagementEntryBase {
  id: string;
  customerId: number;
  customerName: string;
  itemId: number;
  itemName: string;
  orderTag: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  catalogMissing: boolean;
  searchText: string;
}

export interface FavoriteManagementRecipeEntry extends FavoriteManagementEntryBase {
  kind: 'recipe';
  baseIngredientNames: string[];
  cookerName: string;
  extraIngredientNames: string[];
}

export interface FavoriteManagementBeverageEntry extends FavoriteManagementEntryBase {
  kind: 'beverage';
  beverageTags: string[];
  price: number | null;
}

export type FavoriteManagementEntry =
  | FavoriteManagementRecipeEntry
  | FavoriteManagementBeverageEntry;

export interface FavoriteManagementGroup {
  customerId: number;
  customerName: string;
  entries: FavoriteManagementEntry[];
}

export function resolveFavoriteManagementEntries(
  favorites: FavoriteData,
  data: RecommendationDataSet,
): FavoriteManagementEntry[] {
  const customerNames = new Map(data.rareCustomers.map((customer) => [customer.id, customer.name]));
  const recipes = new Map(data.recipes.map((recipe) => [recipe.id, recipe]));
  const ingredients = new Map(data.ingredients.map((ingredient) => [ingredient.id, ingredient.name]));
  const beverages = new Map(data.beverages.map((beverage) => [beverage.id, beverage]));

  const recipeEntries = favorites.recipes.map((favorite) =>
    resolveRecipeFavorite(favorite, customerNames, recipes, ingredients));
  const beverageEntries = favorites.beverages.map((favorite) =>
    resolveBeverageFavorite(favorite, customerNames, beverages));

  return [...recipeEntries, ...beverageEntries].sort(compareFavoriteManagementEntries);
}

export function filterFavoriteManagementEntries(
  entries: FavoriteManagementEntry[],
  filter: FavoriteManagementFilter,
  query: string,
): FavoriteManagementEntry[] {
  const normalizedQuery = normalizeSearchText(query);
  return entries.filter((entry) =>
    (filter === 'all' || entry.kind === filter)
    && (!normalizedQuery || entry.searchText.includes(normalizedQuery)));
}

export function groupFavoriteManagementEntries(
  entries: FavoriteManagementEntry[],
): FavoriteManagementGroup[] {
  const groups = new Map<number, FavoriteManagementGroup>();
  for (const entry of entries) {
    let group = groups.get(entry.customerId);
    if (!group) {
      group = {
        customerId: entry.customerId,
        customerName: entry.customerName,
        entries: [],
      };
      groups.set(entry.customerId, group);
    }
    group.entries.push(entry);
  }

  return [...groups.values()]
    .map((group) => ({
      ...group,
      entries: [...group.entries].sort(compareFavoriteManagementEntries),
    }))
    .sort((left, right) =>
      compareText(left.customerName, right.customerName)
      || left.customerId - right.customerId);
}

export function compareFavoriteManagementEntries(
  left: FavoriteManagementEntry,
  right: FavoriteManagementEntry,
): number {
  return compareText(left.customerName, right.customerName)
    || left.customerId - right.customerId
    || Number(left.kind === 'beverage') - Number(right.kind === 'beverage')
    || compareText(left.orderTag, right.orderTag)
    || compareText(left.itemName, right.itemName)
    || left.itemId - right.itemId
    || left.id.localeCompare(right.id);
}

function resolveRecipeFavorite(
  favorite: FavoriteRecipeEntry,
  customerNames: Map<number, string>,
  recipes: Map<number, RecommendationDataSet['recipes'][number]>,
  ingredients: Map<number, string>,
): FavoriteManagementRecipeEntry {
  const recipe = recipes.get(favorite.recipeId);
  const customerName = resolveCustomerName(customerNames, favorite.customerId, favorite.customerName);
  const itemName = recipe?.name || `未知料理 #${favorite.recipeId}`;
  let catalogMissing = !recipe;
  const extraIngredientNames = favorite.extraIngredientIds.map((id) => {
    const name = ingredients.get(id);
    if (name) return name;
    catalogMissing = true;
    return `未知食材 #${id}`;
  });
  const searchText = buildSearchText([
    customerName,
    favorite.customerId,
    favorite.foodTag,
    itemName,
    favorite.recipeId,
    ...(recipe?.ingredients ?? []),
    ...extraIngredientNames,
    ...favorite.extraIngredientIds,
  ]);

  return {
    kind: 'recipe',
    id: favorite.id,
    customerId: favorite.customerId,
    customerName,
    itemId: favorite.recipeId,
    itemName,
    orderTag: favorite.foodTag || '未记录点单 Tag',
    createdAtUtc: favorite.createdAtUtc,
    updatedAtUtc: favorite.updatedAtUtc,
    catalogMissing,
    searchText,
    baseIngredientNames: recipe?.ingredients ?? [],
    cookerName: recipe?.cooker || '未知',
    extraIngredientNames,
  };
}

function resolveBeverageFavorite(
  favorite: FavoriteBeverageEntry,
  customerNames: Map<number, string>,
  beverages: Map<number, RecommendationDataSet['beverages'][number]>,
): FavoriteManagementBeverageEntry {
  const beverage = beverages.get(favorite.beverageId);
  const customerName = resolveCustomerName(customerNames, favorite.customerId, favorite.customerName);
  const itemName = beverage?.name || `未知酒水 #${favorite.beverageId}`;
  const searchText = buildSearchText([
    customerName,
    favorite.customerId,
    favorite.beverageTag,
    itemName,
    favorite.beverageId,
    ...(beverage?.tags ?? []),
  ]);

  return {
    kind: 'beverage',
    id: favorite.id,
    customerId: favorite.customerId,
    customerName,
    itemId: favorite.beverageId,
    itemName,
    orderTag: favorite.beverageTag || '未记录点单 Tag',
    createdAtUtc: favorite.createdAtUtc,
    updatedAtUtc: favorite.updatedAtUtc,
    catalogMissing: !beverage,
    searchText,
    beverageTags: beverage?.tags ?? [],
    price: beverage?.price ?? null,
  };
}

function resolveCustomerName(
  customerNames: Map<number, string>,
  customerId: number,
  storedName: string,
): string {
  return customerNames.get(customerId) || storedName.trim() || `稀客 #${customerId}`;
}

function buildSearchText(values: Array<string | number>): string {
  return normalizeSearchText(values.join('\n'));
}

function normalizeSearchText(value: string): string {
  return value.normalize('NFKC').trim().toLocaleLowerCase('zh-CN');
}

function compareText(left: string, right: string): number {
  return left.localeCompare(right, 'zh-Hans-CN');
}
