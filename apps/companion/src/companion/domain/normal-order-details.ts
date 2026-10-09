/** 客户端仅保留协议、表单和展示辅助；业务推荐与自动化决策由 C# 服务计算。 */
import type { NormalBusinessOrder } from '@/companion/types';
import type { BeverageCatalogItem, IngredientCatalogItem, RecipeCatalogItem } from '@/lib/catalog-types';

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
