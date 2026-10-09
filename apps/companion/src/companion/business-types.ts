import type { CompanionPreferences } from '@/companion/preferences';
import type { NormalOrderDetailPlan } from '@/companion/domain/normal-order-details';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { PlaceName, RareCustomerCatalogItem } from '@/lib/catalog-types';
import type {
  CustomRecipeData, FavoriteData, NormalOrderExecutionTarget, OrderRecommendation, RecommendationIssue,
  RecommendationStateSnapshot,
} from '@/companion/types';
import type {
  NormalBeverageRecommendation, NormalRecipeRecommendation, RareBeverageRecommendation, RareRecipeRecommendation,
} from '@/recommendation-engine';

/** C# 业务协议的展示模型；不包含 Worker 消息、客户端计算缓存或游戏写入指令。 */
export interface OrderRecommendationResult {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  normalOrderDetailPlans: NormalOrderDetailPlan[];
  normalExecutionTargets: NormalExecutionTargetSelection[];
  performanceMs?: Record<string, number>;
}
export interface NormalExecutionTargetSelection {
  orderKey: string;
  target: NormalOrderExecutionTarget | null;
  message: string;
}

/** 页面组合参数仅用于保持现有展示组件边界；网络层只提取地点、客人编号和两个标签。 */
export type PageRecommendationPayload = {
  kind: 'normal'; runtime: RecommendationStateSnapshot; selectedPlace: PlaceName; data: RecommendationDataSet;
} | {
  kind: 'rare'; runtime: RecommendationStateSnapshot; selectedCustomer: RareCustomerCatalogItem;
  foodTag: string; beverageTag: string; favorites: FavoriteData; customRecipes: CustomRecipeData;
  preferences: CompanionPreferences; data: RecommendationDataSet;
};
export type PageRecommendationResult = {
  kind: 'normal'; recipes: NormalRecipeRecommendation[]; beverages: NormalBeverageRecommendation[];
} | {
  kind: 'rare'; recipes: RareRecipeRecommendation[]; beverages: RareBeverageRecommendation[];
};
