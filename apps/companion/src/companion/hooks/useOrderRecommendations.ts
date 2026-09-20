import { useEffect, useState, useSyncExternalStore } from 'react';
import {
  hasOrderRecommendationWork,
  OrderRecommendationController,
} from '@/companion/workers/order-recommendations-controller';
import type { OrderRecommendationWorkerPayload } from '@/companion/workers/order-recommendations.types';

interface UseOrderRecommendationsOptions {
  enabled?: boolean;
  inputSignature: string;
  contextSignature: string;
}

/** Keeps the transport alive independently of rendering; only exact current inputs are executable. */
export function useOrderRecommendations(
  payload: OrderRecommendationWorkerPayload,
  { enabled = true, inputSignature, contextSignature }: UseOrderRecommendationsOptions,
) {
  const [controller] = useState(() => new OrderRecommendationController(() =>
    new Worker(new URL('../workers/order-recommendations.worker.ts', import.meta.url), { type: 'module' }),
  ));
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot, controller.getSnapshot);

  useEffect(() => {
    controller.update(enabled ? { payload, sourceSignature: inputSignature, contextSignature } : null);
  }, [controller, enabled, payload, inputSignature, contextSignature]);
  useEffect(() => () => controller.dispose(), [controller]);

  return {
    ...state,
    isCurrent: enabled && hasOrderRecommendationWork(payload) && state.isCurrent
      && controller.matchesLatestPayload(payload)
      && state.sourceSignature === inputSignature && state.resultContextSignature === contextSignature,
    retry: controller.retry,
  };
}
