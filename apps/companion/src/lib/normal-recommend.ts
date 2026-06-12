/**
 * 普客推荐算法：计算每道料理对每位普客的匹配分数
 */
import type {
  IRecipe,
  IIngredient,
  IBeverage,
  ICustomerNormal,
  INormalRecipeResult,
  INormalBeverageResult,
  ICustomerScore,
  TPlace,
} from '@/lib/types';

import allRecipes from '@/data/recipes.json';
import allIngredients from '@/data/ingredients.json';
import allNormalCustomers from '@/data/customer_normal.json';
import allBeverages from '@/data/beverages.json';

const ingredientsByName = new Map(
  (allIngredients as IIngredient[]).map((i) => [i.name, i]),
);

/** 获取指定地区的普客 */
export function getNormalCustomersByPlace(place: TPlace): ICustomerNormal[] {
  return (allNormalCustomers as ICustomerNormal[]).filter((c) =>
    c.places.includes(place),
  );
}

/** 计算料理的食材总成本 */
function getIngredientCost(recipe: IRecipe): number {
  return recipe.ingredients.reduce((sum, name) => {
    const ing = ingredientsByName.get(name);
    return sum + (ing ? ing.price : 0);
  }, 0);
}

/** 计算料理的有效标签 */
function getRecipeEffectiveTags(
  recipe: IRecipe,
  popularFoodTag: string | null,
  popularHateFoodTag: string | null,
  isFamousShop: boolean,
): string[] {
  const tags = [...recipe.positiveTags];
  if (recipe.price < 20 && !tags.includes('实惠')) tags.push('实惠');
  if (recipe.price > 60 && !tags.includes('昂贵')) tags.push('昂贵');
  if (isFamousShop && recipe.positiveTags.includes('招牌')) {
    if (!tags.includes('流行喜爱')) tags.push('流行喜爱');
  }
  if (popularFoodTag && recipe.positiveTags.includes(popularFoodTag)) {
    if (!tags.includes('流行喜爱')) tags.push('流行喜爱');
  }
  if (popularHateFoodTag && recipe.positiveTags.includes(popularHateFoodTag)) {
    if (!tags.includes('流行厌恶')) tags.push('流行厌恶');
  }
  return tags;
}

/** 普客料理排序（不排序，由调用方决定排序方式） */
export function computeNormalRecipeResults(
  place: TPlace,
  availableRecipeIds: Set<number>,
  disabledIngredientIds: Set<number>,
  popularFoodTag: string | null,
  popularHateFoodTag: string | null,
  isFamousShop = false,
): INormalRecipeResult[] {
  const customers = getNormalCustomersByPlace(place);
  if (customers.length === 0) return [];

  const results: INormalRecipeResult[] = [];

  for (const recipe of allRecipes as IRecipe[]) {
    if (!availableRecipeIds.has(recipe.id)) continue;

    const hasDisabledIngredient = recipe.ingredients.some((name) => {
      const ing = ingredientsByName.get(name);
      return ing && disabledIngredientIds.has(ing.id);
    });
    if (hasDisabledIngredient) continue;

    const effectiveTags = getRecipeEffectiveTags(
      recipe,
      popularFoodTag,
      popularHateFoodTag,
      isFamousShop,
    );
    const ingredientCost = getIngredientCost(recipe);
    const profit = recipe.price - ingredientCost;

    // 计算每位普客的匹配分数
    const customerScores: ICustomerScore[] = [];
    const matchedTags: string[] = [];

    for (const c of customers) {
      const matched = c.positiveTags.filter((t) => effectiveTags.includes(t));
      customerScores.push({ name: c.name, score: matched.length });
      for (const t of matched) {
        if (!matchedTags.includes(t)) matchedTags.push(t);
      }
    }

    const totalCoverage = customerScores.reduce((s, c) => s + c.score, 0);

    results.push({
      recipe,
      customerScores,
      totalCoverage,
      profit,
      matchedTags,
      ingredientCost,
    });
  }

  return results;
}

/** 普客酒水计算 */
export function computeNormalBeverageResults(
  place: TPlace,
  availableBeverageIds: Set<number>,
): INormalBeverageResult[] {
  const customers = getNormalCustomersByPlace(place);
  if (customers.length === 0) return [];

  const results: INormalBeverageResult[] = [];

  for (const bev of allBeverages as IBeverage[]) {
    if (!availableBeverageIds.has(bev.id)) continue;

    const customerScores: ICustomerScore[] = [];
    const matchedTags: string[] = [];

    for (const c of customers) {
      const matched = c.beverageTags.filter((t) => bev.tags.includes(t));
      customerScores.push({ name: c.name, score: matched.length });
      for (const t of matched) {
        if (!matchedTags.includes(t)) matchedTags.push(t);
      }
    }

    const totalCoverage = customerScores.reduce((s, c) => s + c.score, 0);

    results.push({ beverage: bev, customerScores, totalCoverage, matchedTags });
  }

  return results;
}
