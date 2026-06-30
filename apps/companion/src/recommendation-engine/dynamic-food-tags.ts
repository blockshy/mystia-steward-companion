import type { IngredientCatalogItem, RecipeCatalogItem } from '@/lib/catalog-types';

const ECONOMICAL_PRICE_LIMIT = 20;
const EXPENSIVE_PRICE_LIMIT = 60;
const LARGE_PORTION_INGREDIENT_COUNT = 5;

/**
 * 根据价格、材料槽位和额外加料推导游戏内动态料理 Tag。
 *
 * 这些 Tag 不总是直接存在于静态配方表中，但会影响稀客和普客的偏好匹配。
 */
export function buildDynamicFoodTags({
  recipe,
  extraIngredients,
}: {
  recipe: RecipeCatalogItem;
  extraIngredients: IngredientCatalogItem[];
}): string[] {
  const tags: string[] = [];
  if (!recipe.positiveTags.includes('不可加价')) {
    if (recipe.price < ECONOMICAL_PRICE_LIMIT) tags.push('实惠');
    if (recipe.price > EXPENSIVE_PRICE_LIMIT) tags.push('昂贵');
  }
  if (recipe.ingredients.length + extraIngredients.length >= LARGE_PORTION_INGREDIENT_COUNT) {
    tags.push('大份');
  }
  return tags;
}
