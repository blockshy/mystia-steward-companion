import type { CompanionPreferences } from '@/companion/preferences';
import type {
  CustomRecipeData,
  FavoriteData,
  NightBusinessGuest,
  NightBusinessOrder,
  NormalBusinessOrder,
  OrderRecommendation,
  RecommendationIssue,
  RecommendationStateSnapshot,
  RuntimeMissionServeTarget,
  NormalOrderExecutionTarget,
  SpecialBusinessContext,
} from '@/companion/types';
import type { NormalOrderDetailPlan } from '@/companion/domain/normal-order-details';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { RareCustomerCatalogItem } from '@/lib/catalog-types';
import type { OrderRecommendationUsage } from '@/companion/domain/service-recommendations';

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

export interface OrderRecommendationWorkerPayload {
  orders: NightBusinessOrder[];
  runtime: RecommendationStateSnapshot | null;
  runtimeRareCustomers: RareCustomerCatalogItem[];
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  preferences: CompanionPreferences;
  activeRareGuests: NightBusinessGuest[];
  normalOrders?: NormalBusinessOrder[];
  includeNormalOrderDetails?: boolean;
  includeNormalExecutionTargets?: boolean;
  missionServeTargets: RuntimeMissionServeTarget[];
  specialBusiness?: SpecialBusinessContext | null;
  specialBusinessRejectedRecipeKeys?: string[];
  data: RecommendationDataSet;
  usage?: OrderRecommendationUsage;
}

export type OrderRecommendationWorkerRuntimePayload =
  Omit<OrderRecommendationWorkerPayload, 'data'> & {
    data?: RecommendationDataSet;
    dataSignature: string;
  };

export interface OrderRecommendationWorkerRequest {
  requestId: number;
  sourceSignature: string;
  payload: OrderRecommendationWorkerRuntimePayload;
}

export type OrderRecommendationWorkerResponse =
  | {
    requestId: number;
    ok: true;
    result: OrderRecommendationResult;
    signature: string;
  }
  | {
    requestId: number;
    ok: false;
    error: string;
  };
