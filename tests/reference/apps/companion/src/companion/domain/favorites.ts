import type { FavoriteBeverageEntry, FavoriteData, FavoriteRecipeEntry } from '@/companion/types';
import type { RareBeverageRecommendation, RareRecipeRecommendation } from '@/recommendation-engine';

export function findRecipeFavorite(
  favorites: FavoriteData,
  customerId: number,
  foodTag: string,
  recipe: RareRecipeRecommendation,
): FavoriteRecipeEntry | null {
  return favorites.recipes.find((favorite) =>
    favorite.customerId === customerId
    && favorite.foodTag === foodTag
    && isRecipeFavoriteMatch(favorite, recipe)
  ) ?? null;
}

export function findBeverageFavorite(
  favorites: FavoriteData,
  customerId: number,
  beverageTag: string,
  beverage: RareBeverageRecommendation,
): FavoriteBeverageEntry | null {
  return favorites.beverages.find((favorite) =>
    favorite.customerId === customerId
    && favorite.beverageTag === beverageTag
    && favorite.beverageId === beverage.beverage.id
  ) ?? null;
}

export function recipeFavoriteKey(customerId: number, foodTag: string, recipe: RareRecipeRecommendation) {
  return `recipe:${customerId}:${foodTag}:${recipeResultKey(recipe)}`;
}

export function beverageFavoriteKey(customerId: number, beverageTag: string, beverage: RareBeverageRecommendation) {
  return `beverage:${customerId}:${beverageTag}:${beverage.beverage.id}`;
}

export function recipeResultKey(recipe: RareRecipeRecommendation) {
  return `${recipe.recipe.id}:${normalizeIdList(recipe.extraIngredients.map((ingredient) => ingredient.id)).join(',')}`;
}

export function emptyFavoriteData(): FavoriteData {
  return {
    version: 1,
    recipes: [],
    beverages: [],
  };
}

export function normalizeFavoriteData(data: FavoriteData | null | undefined): FavoriteData {
  return {
    version: Math.max(1, data?.version ?? 1),
    recipes: (data?.recipes ?? []).map((entry) => ({
      ...entry,
      extraIngredientIds: normalizeIdList(entry.extraIngredientIds ?? []),
    })),
    beverages: data?.beverages ?? [],
  };
}

export function normalizeIdList(ids: number[]): number[] {
  return [...new Set(ids.filter((id) => Number.isFinite(id) && id >= 0).map((id) => Math.trunc(id)))].sort((a, b) => a - b);
}

function isRecipeFavoriteMatch(favorite: FavoriteRecipeEntry, recipe: RareRecipeRecommendation): boolean {
  return favorite.recipeId === recipe.recipe.id
    && normalizeIdList(favorite.extraIngredientIds).join(',') === normalizeIdList(recipe.extraIngredients.map((ingredient) => ingredient.id)).join(',');
}
