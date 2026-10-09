/** 客户端仅保留协议、表单和展示辅助；业务推荐与自动化决策由 C# 服务计算。 */
import type { NormalCustomerCatalogItem, PlaceName } from '@/lib/catalog-types';
import type { RecommendationDataSet, RuntimeTagPriorityRule } from '@/lib/recommendation-data';

/**
 * 普客覆盖推荐所需的运行时上下文。
 *
 * 该上下文只关心“当前能做什么”和流行 Tag，不处理稀客订单、预算和库存排序。
 */
export interface NormalCoverageRuntimeContext {
  availableRecipeIds: Set<number>;
  availableBeverageIds: Set<number>;
  disabledIngredientIds: Set<number>;
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
  tagPriorityRules: RuntimeTagPriorityRule[];
}

/**
 * 读取指定地区会出现的普客目录。
 */
export function getNormalCustomersByPlace(
  data: RecommendationDataSet,
  place: PlaceName,
): NormalCustomerCatalogItem[] {
  return data.normalCustomers.filter((customer) => customer.places.includes(place));
}
