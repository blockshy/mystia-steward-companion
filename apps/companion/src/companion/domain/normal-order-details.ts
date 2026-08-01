import { selectSpecialBusinessNormalExecutionTarget } from '@/companion/domain/special-business/registry';
import type { CompanionPreferences } from '@/companion/preferences';
import type {
  NormalBusinessOrder,
  RecommendationStateSnapshot,
  SpecialBusinessContext,
} from '@/companion/types';
import type {
  BeverageCatalogItem,
  IngredientCatalogItem,
  RecipeCatalogItem,
} from '@/lib/catalog-types';
import { buildRecommendationDataIndexes, type RecommendationDataSet } from '@/lib/recommendation-data';
import { resolveFoodTags, resolveTagPriority } from '@/recommendation-engine/tag-resolution';

export interface NormalOrderFoodDetail {
  recipe: RecipeCatalogItem | null;
  foodId: number;
  recipeId: number | null;
  name: string;
  cookerName: string;
  baseIngredientNames: string[];
  extraIngredients: IngredientCatalogItem[];
  extraIngredientIds: number[];
  activeTags: string[];
  suppressedTags: string[];
  targetTags: string[];
}

export interface NormalOrderBeverageDetail {
  beverage: BeverageCatalogItem | null;
  beverageId: number;
  name: string;
  activeTags: string[];
  suppressedTags: string[];
}

export interface NormalOrderDetailPlan {
  order: NormalBusinessOrder;
  originalFood: NormalOrderFoodDetail;
  originalBeverage: NormalOrderBeverageDetail;
  executionFood: NormalOrderFoodDetail;
  executionBeverage: NormalOrderBeverageDetail;
  executionReason: string;
  selectionMessage: string;
  usesSpecialExecution: boolean;
  hasExecutionOverride: boolean;
}

export function buildNormalOrderDetailPlans({
  orders,
  specialBusiness,
  runtime,
  preferences,
  dataSignature,
  data,
  rejectedRecipeKeys = [],
}: {
  orders: NormalBusinessOrder[];
  specialBusiness: SpecialBusinessContext | null | undefined;
  runtime: RecommendationStateSnapshot | null | undefined;
  preferences: CompanionPreferences;
  dataSignature: string;
  data: RecommendationDataSet;
  rejectedRecipeKeys?: readonly string[];
}): NormalOrderDetailPlan[] {
  const indexes = buildRecommendationDataIndexes(data);
  const beverageById = new Map(data.beverages.map((beverage) => [beverage.id, beverage]));
  const ingredientById = new Map(data.ingredients.map((ingredient) => [ingredient.id, ingredient]));

  return orders.map((order) => {
    const originalRecipe = resolveRecipe(data, indexes.recipeByFoodId, order.foodId, null);
    const originalBeverage = beverageById.get(order.beverageId) ?? null;
    const originalFood = buildFoodDetail({
      data,
      runtime,
      recipe: originalRecipe,
      foodId: order.foodId,
      recipeId: originalRecipe?.recipeId ?? null,
      name: order.foodName || originalRecipe?.name || `料理 #${order.foodId}`,
      extraIngredientIds: [],
      ingredientById,
      fallbackTags: originalRecipe?.positiveTags ?? [],
      targetTags: [],
    });
    const originalBeverageDetail = buildBeverageDetail({
      data,
      beverage: originalBeverage,
      beverageId: order.beverageId,
      name: order.beverageName || originalBeverage?.name || `酒水 #${order.beverageId}`,
      fallbackTags: originalBeverage?.tags ?? [],
    });

    const selection = selectSpecialBusinessNormalExecutionTarget({
      order,
      specialBusiness,
      runtime,
      preferences,
      dataSignature,
      data,
      rejectedRecipeKeys,
    });

    if (!selection.target) {
      return {
        order,
        originalFood,
        originalBeverage: originalBeverageDetail,
        executionFood: originalFood,
        executionBeverage: originalBeverageDetail,
        executionReason: '',
        selectionMessage: selection.message,
        usesSpecialExecution: false,
        hasExecutionOverride: false,
      };
    }

    const target = selection.target;
    const executionRecipe = resolveRecipe(data, indexes.recipeByFoodId, target.foodId, target.recipeId);
    const executionBeverage = beverageById.get(target.beverageId) ?? null;
    const executionFood = buildFoodDetail({
      data,
      runtime,
      recipe: executionRecipe,
      foodId: target.foodId,
      recipeId: target.recipeId,
      name: target.recipeName || executionRecipe?.name || `料理 #${target.foodId}`,
      extraIngredientIds: target.extraIngredientIds,
      ingredientById,
      fallbackTags: target.foodTags,
      targetTags: target.specialTargetFoodTags,
    });
    const executionBeverageDetail = buildBeverageDetail({
      data,
      beverage: executionBeverage,
      beverageId: target.beverageId,
      name: target.beverageName || executionBeverage?.name || `酒水 #${target.beverageId}`,
      fallbackTags: target.beverageTags,
    });

    return {
      order,
      originalFood,
      originalBeverage: originalBeverageDetail,
      executionFood,
      executionBeverage: executionBeverageDetail,
      executionReason: target.reason,
      selectionMessage: selection.message,
      usesSpecialExecution: true,
      hasExecutionOverride: isExecutionOverride(order, target.extraIngredientIds, executionFood, executionBeverageDetail),
    };
  });
}

function resolveRecipe(
  data: RecommendationDataSet,
  recipeByFoodId: Map<number, RecipeCatalogItem>,
  foodId: number,
  recipeId: number | null,
): RecipeCatalogItem | null {
  return recipeByFoodId.get(foodId)
    ?? (recipeId == null ? null : data.recipes.find((recipe) => recipe.recipeId === recipeId))
    ?? null;
}

function buildFoodDetail({
  data,
  runtime,
  recipe,
  foodId,
  recipeId,
  name,
  extraIngredientIds,
  ingredientById,
  fallbackTags,
  targetTags,
}: {
  data: RecommendationDataSet;
  runtime: RecommendationStateSnapshot | null | undefined;
  recipe: RecipeCatalogItem | null;
  foodId: number;
  recipeId: number | null;
  name: string;
  extraIngredientIds: readonly number[];
  ingredientById: Map<number, IngredientCatalogItem>;
  fallbackTags: readonly string[];
  targetTags: readonly string[];
}): NormalOrderFoodDetail {
  const extraIngredients = extraIngredientIds
    .map((id) => ingredientById.get(id))
    .filter((ingredient): ingredient is IngredientCatalogItem => Boolean(ingredient));
  const resolved = recipe
    ? resolveFoodTags({
      recipe,
      extraIngredients,
      popularFoodTag: runtime?.popularFoodTag ?? null,
      popularHateFoodTag: runtime?.popularHateFoodTag ?? null,
      famousShopEnabled: runtime?.famousShopEnabled ?? false,
      tagPriorityRules: data.tagPriorityRules,
    })
    : { activeTags: normalizeTags(fallbackTags), suppressedTags: [] };

  return {
    recipe,
    foodId,
    recipeId,
    name,
    cookerName: recipe?.cooker ?? '',
    baseIngredientNames: recipe?.ingredients ?? [],
    extraIngredients,
    extraIngredientIds: [...extraIngredientIds],
    activeTags: resolved.activeTags,
    suppressedTags: resolved.suppressedTags,
    targetTags: normalizeTags(targetTags),
  };
}

function buildBeverageDetail({
  data,
  beverage,
  beverageId,
  name,
  fallbackTags,
}: {
  data: RecommendationDataSet;
  beverage: BeverageCatalogItem | null;
  beverageId: number;
  name: string;
  fallbackTags: readonly string[];
}): NormalOrderBeverageDetail {
  const resolved = resolveTagPriority(beverage?.tags ?? [...fallbackTags], data.tagPriorityRules);
  return {
    beverage,
    beverageId,
    name,
    activeTags: resolved.activeTags,
    suppressedTags: resolved.suppressedTags,
  };
}

function isExecutionOverride(
  order: NormalBusinessOrder,
  extraIngredientIds: readonly number[],
  executionFood: NormalOrderFoodDetail,
  executionBeverage: NormalOrderBeverageDetail,
): boolean {
  return executionFood.foodId !== order.foodId
    || executionBeverage.beverageId !== order.beverageId
    || extraIngredientIds.length > 0;
}

function normalizeTags(tags: readonly string[]): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const tag of tags) {
    const text = tag.trim();
    if (!text || seen.has(text)) continue;
    seen.add(text);
    result.push(text);
  }
  return result;
}
