import {
  buildOrderRecommendations,
  buildRareCustomerMap,
  createRecommendationCacheStore,
} from '@/companion/domain/service-recommendations';
import type {
  OrderRecommendationWorkerRequest,
  OrderRecommendationWorkerResponse,
} from '@/companion/workers/order-recommendations.types';

type WorkerScope = {
  postMessage: (message: OrderRecommendationWorkerResponse) => void;
  onmessage: ((event: MessageEvent<OrderRecommendationWorkerRequest>) => void) | null;
};

const workerScope = self as unknown as WorkerScope;
const recommendationCaches = createRecommendationCacheStore();

workerScope.onmessage = (event) => {
  const { requestId, payload } = event.data;

  try {
    const rareCustomersById = buildRareCustomerMap(payload.runtimeRareCustomers, payload.data);
    const result = buildOrderRecommendations(
      payload.orders,
      payload.runtime,
      rareCustomersById,
      recommendationCaches,
      payload.favorites,
      payload.customRecipes,
      payload.preferences,
      payload.activeRareGuests,
      payload.missionServeTargets,
      payload.data,
    );

    workerScope.postMessage({
      requestId,
      ok: true,
      result,
    });
  } catch (error) {
    workerScope.postMessage({
      requestId,
      ok: false,
      error: error instanceof Error ? error.message : String(error),
    });
  }
};

export {};
