import { useCallback, useEffect, useRef, useState } from 'react';
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
  const requestSequenceRef = useRef(0);
  const stateVersionRef = useRef(0);
  const activeRequestIdRef = useRef<number | null>(null);
  const queuedRequestRef = useRef<PageRecommendationWorkerRequest | null>(null);

  const createRequest = useCallback((nextPayload: PageRecommendationPayload): PageRecommendationWorkerRequest => {
    requestSequenceRef.current += 1;
    return {
      requestId: requestSequenceRef.current,
      payload: nextPayload,
    };
  }, []);

  const postRequest = useCallback((worker: Worker, request: PageRecommendationWorkerRequest) => {
    activeRequestIdRef.current = request.requestId;
    try {
      worker.postMessage(request);
    } catch (error) {
      activeRequestIdRef.current = null;
      throw error;
    }
  }, []);

  useEffect(() => {
    const worker = new Worker(new URL('../workers/page-recommendations.worker.ts', import.meta.url), {
      type: 'module',
    });
    workerRef.current = worker;

    worker.onmessage = (event: MessageEvent<PageRecommendationWorkerResponse>) => {
      const response = event.data;
      if (response.requestId !== activeRequestIdRef.current) return;

      activeRequestIdRef.current = null;
      const queuedRequest = queuedRequestRef.current;
      queuedRequestRef.current = null;
      const hasQueuedRequest = queuedRequest !== null;
      let queueError: string | null = null;

      if (queuedRequest) {
        try {
          postRequest(worker, queuedRequest);
        } catch (error) {
          queueError = error instanceof Error ? error.message : String(error);
        }
      }

      if (response.ok) {
        setState({
          result: response.result,
          pending: hasQueuedRequest && !queueError,
          isCurrent: !hasQueuedRequest || queueError !== null,
          error: queueError,
        });
        return;
      }

      setState((current) => ({
        result: current.result,
        pending: hasQueuedRequest && !queueError,
        isCurrent: !hasQueuedRequest || queueError !== null,
        error: queueError ?? response.error,
      }));
    };

    worker.onerror = (event) => {
      const message = event.message || '推荐计算 Worker 运行失败。';
      activeRequestIdRef.current = null;
      queuedRequestRef.current = null;
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
      activeRequestIdRef.current = null;
      queuedRequestRef.current = null;
    };
  }, [postRequest]);

  useEffect(() => {
    const stateVersion = stateVersionRef.current + 1;
    stateVersionRef.current = stateVersion;
    const scheduleCurrentState = (
      buildNextState: (current: PageRecommendationState) => PageRecommendationState,
    ) => {
      queueMicrotask(() => {
        if (stateVersionRef.current !== stateVersion) return;
        setState(buildNextState);
      });
    };

    if (!payload) {
      activeRequestIdRef.current = null;
      queuedRequestRef.current = null;
      scheduleCurrentState(() => INITIAL_STATE);
      return;
    }

    const worker = workerRef.current;
    if (!worker) {
      scheduleCurrentState(() => ({
        result: null,
        pending: false,
        isCurrent: true,
        error: '推荐计算 Worker 尚未初始化。',
      }));
      return;
    }

    const request = createRequest(payload);
    if (activeRequestIdRef.current === null) {
      try {
        postRequest(worker, request);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        scheduleCurrentState((current) => ({
          result: current.result,
          pending: false,
          isCurrent: true,
          error: message,
        }));
        return;
      }
    } else {
      queuedRequestRef.current = request;
    }

    scheduleCurrentState((current) => {
      const requestStillPending = activeRequestIdRef.current === request.requestId
        || queuedRequestRef.current?.requestId === request.requestId;
      if (!requestStillPending) return current;
      return {
        result: current.result,
        pending: true,
        isCurrent: false,
        error: null,
      };
    });
  }, [createRequest, payload, postRequest]);

  return state;
}
