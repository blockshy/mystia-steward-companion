import { useEffect, useRef, useState } from 'react';
import type {
  PageRecommendationPayload,
  PageRecommendationResult,
  PageRecommendationWorkerRequest,
  PageRecommendationWorkerResponse,
} from '@/companion/workers/page-recommendations.types';

interface PageRecommendationState {
  result: PageRecommendationResult | null;
  pending: boolean;
  isCurrent: boolean;
  error: string | null;
}

const INITIAL_STATE: PageRecommendationState = {
  result: null,
  pending: false,
  isCurrent: true,
  error: null,
};

export function usePageRecommendations(payload: PageRecommendationPayload | null): PageRecommendationState {
  const [state, setState] = useState<PageRecommendationState>(INITIAL_STATE);
  const workerRef = useRef<Worker | null>(null);
  const latestRequestIdRef = useRef(0);
  const settledRequestIdRef = useRef(0);

  useEffect(() => {
    const worker = new Worker(new URL('../workers/page-recommendations.worker.ts', import.meta.url), {
      type: 'module',
    });
    workerRef.current = worker;

    worker.onmessage = (event: MessageEvent<PageRecommendationWorkerResponse>) => {
      const response = event.data;
      if (response.requestId !== latestRequestIdRef.current) return;
      settledRequestIdRef.current = response.requestId;

      if (response.ok) {
        setState({
          result: response.result,
          pending: false,
          isCurrent: true,
          error: null,
        });
        return;
      }

      setState({
        result: null,
        pending: false,
        isCurrent: true,
        error: response.error,
      });
    };

    worker.onerror = (event) => {
      const message = event.message || '推荐计算 Worker 运行失败。';
      setState({
        result: null,
        pending: false,
        isCurrent: true,
        error: message,
      });
    };

    return () => {
      worker.terminate();
      workerRef.current = null;
    };
  }, []);

  useEffect(() => {
    const requestId = latestRequestIdRef.current + 1;
    latestRequestIdRef.current = requestId;
    const scheduleCurrentState = (
      buildNextState: (current: PageRecommendationState) => PageRecommendationState,
    ) => {
      queueMicrotask(() => {
        if (latestRequestIdRef.current !== requestId) return;
        setState(buildNextState);
      });
    };

    if (!payload) {
      settledRequestIdRef.current = requestId;
      scheduleCurrentState(() => INITIAL_STATE);
      return;
    }

    const worker = workerRef.current;
    if (!worker) {
      settledRequestIdRef.current = requestId;
      scheduleCurrentState(() => ({
        result: null,
        pending: false,
        isCurrent: true,
        error: '推荐计算 Worker 尚未初始化。',
      }));
      return;
    }

    const request: PageRecommendationWorkerRequest = {
      requestId,
      payload,
    };

    scheduleCurrentState((current) => {
      if (settledRequestIdRef.current === requestId) return current;
      return {
        result: null,
        pending: true,
        isCurrent: false,
        error: null,
      };
    });
    worker.postMessage(request);
  }, [payload]);

  return state;
}
