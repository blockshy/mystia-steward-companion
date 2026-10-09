/** 客户端推荐协议与设置编辑入口；推荐计算只由 C# 业务服务提供。 */
export { getNormalCustomersByPlace } from '@/recommendation-engine/normal-coverage';
export {
  DEFAULT_RECOMMENDATION_SORT_PROFILE,
  RECOMMENDATION_OBJECTIVE_DEFINITIONS,
  RECOMMENDATION_SORT_PRESETS,
  buildDefaultRecommendationSortProfile,
  normalizeRecommendationSortProfile,
  serializeRecommendationSortProfile,
} from '@/recommendation-engine/sort-profile';
export type {
  BeverageCandidate,
  ConditionResult,
  FoodCandidate,
  CustomerCoverageSummary,
  NormalBeverageRecommendation,
  NormalRecipeRecommendation,
  RareBeverageRecommendation,
  RareBeverageCandidateSearchDiagnostic,
  RareFoodCandidateSearchDiagnostic,
  RareOrderRecommendationPlan,
  RareRecipeRecommendation,
  RareTagOrderDemand,
  RecommendationBudgetContext,
  RecommendationBudgetPolicy,
  RecommendationBudgetResult,
  RecommendationBucket,
  RecommendationDemand,
  RecommendationExclusions,
  RecommendationRuntimeContext,
  ResolvedTags,
  SpecialBusinessFoodTargetPolicy,
  SpecialBusinessTagMatch,
  SpecialBusinessTargetEnforcement,
} from '@/recommendation-engine/types';
export type {
  NormalCoverageRuntimeContext,
} from '@/recommendation-engine/normal-coverage';
export type {
  RecommendationObjectiveDefinition,
  RecommendationObjectiveDirection,
  RecommendationObjectiveKey,
  RecommendationObjectiveRule,
  RecommendationPlanSortContext,
  RecommendationSortPreset,
  RecommendationSortPresetId,
  RecommendationSortProfile,
} from '@/recommendation-engine/sort-profile';
