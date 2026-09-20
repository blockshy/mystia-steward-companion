import {
  buildOrderRecommendations,
  buildRareCustomerMap,
  createRecommendationCacheStore,
} from '@/companion/domain/service-recommendations';
import { buildNormalOrderDetailPlans } from '@/companion/domain/normal-order-details';
import { buildNormalAutoOrderKey } from '@/companion/domain/normal-order-key';
import { sortNormalOrders } from '@/companion/domain/sorting';
import { selectSpecialBusinessNormalExecutionTarget } from '@/companion/domain/special-business/registry';
import type {
  NormalExecutionTargetSelection,
  OrderRecommendationResult,
  OrderRecommendationWorkerPayload,
  OrderRecommendationWorkerRequest,
  OrderRecommendationWorkerResponse,
} from '@/companion/workers/order-recommendations.types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

type WorkerScope = {
  postMessage: (message: OrderRecommendationWorkerResponse) => void;
  onmessage: ((event: MessageEvent<OrderRecommendationWorkerRequest>) => void) | null;
};

const workerScope = self as unknown as WorkerScope;
const recommendationCaches = createRecommendationCacheStore();
let cachedData: RecommendationDataSet | null = null;
let cachedDataSignature = '';

class RecommendationDataCacheMiss extends Error {}

workerScope.onmessage = (event) => {
  const { requestId, payload: runtimePayload } = event.data;

  try {
    const data = resolveRecommendationData(runtimePayload);
    const payload: OrderRecommendationWorkerPayload = {
      ...runtimePayload,
      data,
    };
    const startedAt = now();
    const rareCustomersById = buildRareCustomerMap(payload.data);
    const recommendationStartedAt = now();
    const recommendationResult = buildOrderRecommendations(
      payload.orders,
      payload.runtime,
      rareCustomersById,
      recommendationCaches,
      payload.favorites,
      payload.customRecipes,
      payload.preferences,
      payload.specialBusiness ?? null,
      payload.specialBusinessRejectedRecipeKeys ?? [],
      payload.data,
      { usage: payload.usage ?? 'display' },
    );
    const recommendationFinishedAt = now();

    const detailStartedAt = now();
    const normalOrderDetailPlans = payload.includeNormalOrderDetails === true
      ? buildNormalOrderDetailPlans({
        orders: sortNormalOrders(payload.normalOrders ?? []),
        specialBusiness: payload.specialBusiness ?? null,
        runtime: payload.runtime,
        preferences: payload.preferences,
        dataSignature: runtimePayload.dataSignature,
        data: payload.data,
        rejectedRecipeKeys: payload.specialBusinessRejectedRecipeKeys ?? [],
      })
      : [];
    const detailFinishedAt = now();

    const executionTargetStartedAt = now();
    const normalExecutionTargets = payload.includeNormalExecutionTargets === true
      ? buildNormalExecutionTargets(payload, runtimePayload.dataSignature)
      : [];
    const finishedAt = now();

    const result: OrderRecommendationResult = {
      ...recommendationResult,
      normalOrderDetailPlans,
      normalExecutionTargets,
      performanceMs: {
        recommendations: recommendationFinishedAt - recommendationStartedAt,
        normalDetails: detailFinishedAt - detailStartedAt,
        normalExecutionTargets: finishedAt - executionTargetStartedAt,
        total: finishedAt - startedAt,
      },
    };

    workerScope.postMessage({
      requestId,
      ok: true,
      result,
    });
  } catch (error) {
    workerScope.postMessage({
      requestId,
      ok: false,
      code: error instanceof RecommendationDataCacheMiss ? 'data-cache-miss' : 'calculation-failed',
      error: error instanceof Error ? error.message : String(error),
    });
  }
};

function resolveRecommendationData(
  payload: OrderRecommendationWorkerRequest['payload'],
): RecommendationDataSet {
  if (payload.data) {
    cachedData = payload.data;
    cachedDataSignature = payload.dataSignature;
    return payload.data;
  }

  if (cachedData && cachedDataSignature === payload.dataSignature) {
    return cachedData;
  }

  throw new RecommendationDataCacheMiss('推荐数据尚未初始化，等待下一次游戏数据更新。');
}

function now(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now();
}

function buildNormalExecutionTargets(
  payload: OrderRecommendationWorkerPayload,
  dataSignature: string,
): NormalExecutionTargetSelection[] {
  return sortNormalOrders(payload.normalOrders ?? [])
    .filter((order) => !order.hasEvaluated)
    .map((order) => {
      const selection = selectSpecialBusinessNormalExecutionTarget({
        order,
        specialBusiness: payload.specialBusiness ?? null,
        runtime: payload.runtime,
        preferences: payload.preferences,
        dataSignature,
        data: payload.data,
        rejectedRecipeKeys: payload.specialBusinessRejectedRecipeKeys ?? [],
      });
      return {
        orderKey: buildNormalAutoOrderKey(order),
        target: selection.target,
        message: selection.message,
      };
    });
}


export {};
