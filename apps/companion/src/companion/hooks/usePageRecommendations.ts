import { useCallback, useEffect, useRef, useState } from 'react';
import type {
  PageRecommendationPayload,
  PageRecommendationResult,
  PageRecommendationWorkerRequest,
  PageRecommendationWorkerRuntimePayload,
  PageRecommendationWorkerResponse,
} from '@/companion/workers/page-recommendations.types';
import { buildRecommendationDataSignature } from '@/lib/recommendation-data';

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
const DATA_CACHE_MISS_MESSAGE = '推荐数据集尚未初始化';

export function usePageRecommendations(payload: PageRecommendationPayload | null): PageRecommendationState {
  const [state, setState] = useState<PageRecommendationState>(INITIAL_STATE);
  const workerRef = useRef<Worker | null>(null);
  const requestSequenceRef = useRef(0);
  const stateVersionRef = useRef(0);
  const activeRequestIdRef = useRef<number | null>(null);
  const activeRequestRef = useRef<PageRecommendationWorkerRequest | null>(null);
  const queuedRequestRef = useRef<PageRecommendationWorkerRequest | null>(null);
  const postedDataSignatureRef = useRef('');
  const payloadRef = useRef(payload);
  const workerEnabled = payload !== null;

  useEffect(() => {
    payloadRef.current = payload;
  }, [payload]);

  const createRequest = useCallback((nextPayload: PageRecommendationPayload): PageRecommendationWorkerRequest => {
    requestSequenceRef.current += 1;
    const dataSignature = buildRecommendationDataSignature(nextPayload.data);
    const includeData = postedDataSignatureRef.current !== dataSignature;
    return {
      requestId: requestSequenceRef.current,
      payload: buildRuntimePayload(nextPayload, dataSignature, includeData),
    };
  }, []);

  const postRequest = useCallback((worker: Worker, request: PageRecommendationWorkerRequest) => {
    activeRequestIdRef.current = request.requestId;
    activeRequestRef.current = request;
    try {
      worker.postMessage(request);
    } catch (error) {
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      throw error;
    }
  }, []);

  useEffect(() => {
    if (!workerEnabled) {
      workerRef.current?.terminate();
      workerRef.current = null;
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      queuedRequestRef.current = null;
      postedDataSignatureRef.current = '';
      return undefined;
    }

    const worker = new Worker(new URL('../workers/page-recommendations.worker.ts', import.meta.url), {
      type: 'module',
    });
    workerRef.current = worker;

    worker.onmessage = (event: MessageEvent<PageRecommendationWorkerResponse>) => {
      const response = event.data;
      if (response.requestId !== activeRequestIdRef.current) return;

      const activeRequest = activeRequestRef.current;
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      let queuedRequest = queuedRequestRef.current;
      queuedRequestRef.current = null;
      const hasQueuedRequest = queuedRequest !== null;
      let queueError: string | null = null;

      if (response.ok) {
        if (activeRequest?.payload.data) {
          postedDataSignatureRef.current = activeRequest.payload.dataSignature;
        }
      } else if (activeRequest?.payload.data || isDataCacheMiss(response.error)) {
        postedDataSignatureRef.current = '';
        queuedRequest = payloadRef.current ? createRequest(payloadRef.current) : null;
      }

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
        pending: queuedRequest !== null && !queueError,
        isCurrent: queuedRequest === null || queueError !== null,
        error: queueError ?? response.error,
      }));
    };

    worker.onerror = (event) => {
      const message = event.message || '推荐计算 Worker 运行失败。';
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      queuedRequestRef.current = null;
      postedDataSignatureRef.current = '';
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
      activeRequestRef.current = null;
      queuedRequestRef.current = null;
      postedDataSignatureRef.current = '';
    };
  }, [createRequest, postRequest, workerEnabled]);

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
      activeRequestRef.current = null;
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

function buildRuntimePayload(
  payload: PageRecommendationPayload,
  dataSignature: string,
  includeData: boolean,
): PageRecommendationWorkerRuntimePayload {
  const { data, ...rest } = payload;
  return includeData
    ? { ...rest, data, dataSignature }
    : { ...rest, dataSignature };
}

function isDataCacheMiss(error: string): boolean {
  return error.includes(DATA_CACHE_MISS_MESSAGE);
}
