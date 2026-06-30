import type {
  BeverageCatalogItem,
  IngredientCatalogItem,
  PlaceName,
  RareCustomerCatalogItem,
  RecipeCatalogItem,
} from '@/lib/catalog-types';
import type { RuntimeTagPriorityRule } from '@/lib/recommendation-data';

export type RecommendationDemand =
  | RareTagOrderDemand
  | NormalExactOrderDemand
  | NormalCoverageDemand;

/**
 * 稀客点单需求：料理与酒水都以 Tag 形式表达，推荐引擎需要寻找可满足 Tag 的候选。
 */
export interface RareTagOrderDemand {
  type: 'rare-tag-order';
  customer: RareCustomerCatalogItem;
  requiredFoodTag: string;
  requiredBeverageTag: string;
}

/**
 * 普客点单需求：游戏运行时已经给出明确的料理和酒水 ID。
 */
export interface NormalExactOrderDemand {
  type: 'normal-exact-order';
  foodId: number;
  beverageId: number;
}

/**
 * 普客覆盖需求：用于地区页推荐能覆盖更多普客偏好的料理和酒水。
 */
export interface NormalCoverageDemand {
  type: 'normal-coverage';
  place: PlaceName;
  customerIds: number[];
}

/**
 * 推荐引擎运行时上下文。
 *
 * 该对象由 Mod 快照、用户偏好和设置面板共同构成，所有集合都应在调用推荐函数前完成归一化。
 */
export interface RecommendationRuntimeContext {
  availableRecipeIds: Set<number>;
  availableIngredientIds: Set<number>;
  availableBeverageIds: Set<number>;
  disabledIngredientIds: Set<number>;
  excludedIngredientIds: Set<number>;
  excludedBeverageIds: Set<number>;
  ownedIngredientQty: Record<number, number>;
  ownedBeverageQty: Record<number, number>;
  placedCookerNames: Set<string>;
  hasCookerSnapshot: boolean;
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
  tagPriorityRules: RuntimeTagPriorityRule[];
  maxExtraIngredients: number;
  filterMissingCookers: boolean;
  budget: RecommendationBudgetContext | null;
  budgetPolicy: RecommendationBudgetPolicy;
}

/**
 * 预算约束策略。
 *
 * `block` 会把超预算候选降为不可用，`warn` 只给出警告，`ignore` 完全不参与推荐判断。
 */
export type RecommendationBudgetPolicy = 'block' | 'warn' | 'ignore';

/**
 * 用户在设置页维护的推荐排除项。
 */
export interface RecommendationExclusions {
  excludedIngredientIds: number[];
  excludedBeverageIds: number[];
}

/**
 * 订单预算上下文，通常来自当前客人可支付预算或用户手动指定预算。
 */
export interface RecommendationBudgetContext {
  remainingBudget: number | null;
  source: 'runtime-active-guest' | 'manual' | 'unknown';
  willPayMoney?: boolean | null;
}

/**
 * 某个候选方案的预算估算结果。
 */
export interface RecommendationBudgetResult {
  estimatedPrice: number;
  remainingBudget: number | null;
  overBudget: number;
  policy: RecommendationBudgetPolicy;
  source: RecommendationBudgetContext['source'];
  willPayMoney?: boolean | null;
}

export type ConditionStatus = 'pass' | 'fail' | 'warn' | 'boost' | 'info';
export type ConditionSeverity = 'hard' | 'soft' | 'info';
export type ConditionTarget = 'food' | 'beverage' | 'plan';

/**
 * 推荐候选的可解释条件。
 *
 * UI 通过该结构展示命中、警告、硬性失败和信息提示，推荐排序也会读取其中的硬性条件。
 */
export interface ConditionResult {
  id: string;
  target: ConditionTarget;
  status: ConditionStatus;
  severity: ConditionSeverity;
  label: string;
  detail: string;
}

/**
 * 经过 Tag 优先级压制后的料理 Tag 集合。
 */
export interface ResolvedTags {
  activeTags: string[];
  suppressedTags: string[];
}

/**
 * 一个推荐项覆盖单个普客偏好的摘要。
 */
export interface CustomerCoverageSummary {
  customerId: number;
  customerName: string;
  matchedTagCount: number;
  matchedTags: string[];
}

/**
 * 普客地区推荐中的料理候选。
 */
export interface NormalRecipeRecommendation {
  recipe: RecipeCatalogItem;
  activeTags: string[];
  suppressedTags: string[];
  customerCoverage: CustomerCoverageSummary[];
  totalCoverage: number;
  coveredCustomerCount: number;
  profit: number;
  matchedTags: string[];
  ingredientCost: number;
  conditionResults: ConditionResult[];
}

/**
 * 普客地区推荐中的酒水候选。
 */
export interface NormalBeverageRecommendation {
  beverage: BeverageCatalogItem;
  activeTags: string[];
  customerCoverage: CustomerCoverageSummary[];
  totalCoverage: number;
  coveredCustomerCount: number;
  matchedTags: string[];
  conditionResults: ConditionResult[];
}

/**
 * 稀客订单推荐中的料理候选。
 */
export interface RareRecipeRecommendation {
  recipe: RecipeCatalogItem;
  extraIngredients: IngredientCatalogItem[];
  missionPriority?: boolean;
  customRecipe?: boolean;
  customRecipePinned?: boolean;
  customRecipeSortOrder?: number;
  customRecipeScope?: 'tag' | 'all';
  customRecipeId?: string;
  extraIngredientReasonTags: Record<number, string[]>;
  allTags: string[];
  cancelledTags: string[];
  meetsRequiredFood: boolean;
  baseCost: number;
  extraCost: number;
}

/**
 * 稀客订单推荐中的酒水候选。
 */
export interface RareBeverageRecommendation {
  beverage: BeverageCatalogItem;
  meetsRequiredBev: boolean;
  matchedTags: string[];
}

/**
 * 稀客料理搜索阶段的候选项。
 */
export interface FoodCandidate {
  recipe: RecipeCatalogItem;
  extraIngredients: IngredientCatalogItem[];
  customRecipe?: boolean;
  customRecipePinned?: boolean;
  customRecipeSortOrder?: number;
  customRecipeScope?: 'tag' | 'all';
  customRecipeId?: string;
  extraIngredientReasonTags: Record<number, string[]>;
  activeTags: string[];
  suppressedTags: string[];
  matchedPositiveTags: string[];
  matchedNegativeTags: string[];
  meetsRequiredFood: boolean;
  baseCost: number;
  extraCost: number;
  resourcePressure: number;
  cookerAvailable: boolean;
  conditionResults: ConditionResult[];
}

/**
 * 稀客酒水搜索阶段的候选项。
 */
export interface BeverageCandidate {
  beverage: BeverageCatalogItem;
  activeTags: string[];
  matchedTags: string[];
  meetsRequiredBeverage: boolean;
  ownedQuantity: number;
  conditionResults: ConditionResult[];
}

/**
 * 稀客推荐方案分桶。
 *
 * `preference` 代表喜好备选兜底方案，已并入主推荐列表而不是单独维护一套旧机制。
 */
export type RecommendationBucket = 'complete' | 'tradeoff' | 'preference' | 'blocked';

/**
 * 稀客订单的一组料理加酒水推荐方案。
 */
export interface RareOrderRecommendationPlan {
  demand: RareTagOrderDemand;
  food: FoodCandidate | null;
  beverage: BeverageCandidate | null;
  bucket: RecommendationBucket;
  estimatedPrice: number;
  budget: RecommendationBudgetResult | null;
  conditionResults: ConditionResult[];
  reasons: string[];
  warnings: string[];
}
