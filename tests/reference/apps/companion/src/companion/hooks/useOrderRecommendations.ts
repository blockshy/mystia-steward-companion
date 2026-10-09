import { useCallback, useEffect, useRef, useState } from 'react';
import type {
  OrderRecommendationResult,
  OrderRecommendationWorkerPayload,
  OrderRecommendationWorkerRequest,
  OrderRecommendationWorkerRuntimePayload,
  OrderRecommendationWorkerResponse,
} from '@/companion/workers/order-recommendations.types';
import { buildRecommendationDataSignature } from '@/lib/recommendation-data';

interface AsyncOrderRecommendationResult extends OrderRecommendationResult {
  pending: boolean;
  isCurrent: boolean;
  sourceSignature: string;
  resultContextSignature: string;
  successRevision: number;
  retainedAfterError: boolean;
  error: string | null;
}

interface UseOrderRecommendationsOptions {
  enabled?: boolean;
  inputSignature?: string;
  contextSignature?: string;
}

const EMPTY_RECOMMENDATIONS: OrderRecommendationResult = {
  recommendations: [],
  recommendationIssues: [],
  normalOrderDetailPlans: [],
  normalExecutionTargets: [],
};

const EMPTY_ASYNC_RECOMMENDATIONS: AsyncOrderRecommendationResult = {
  ...EMPTY_RECOMMENDATIONS,
  pending: false,
  isCurrent: true,
  sourceSignature: '',
  resultContextSignature: '',
  successRevision: 0,
  retainedAfterError: false,
  error: null,
};
const DATA_CACHE_MISS_MESSAGE = '推荐数据集尚未初始化';

/**
 * 在 Web Worker 中异步计算订单推荐。
 *
 * 推荐搜索可能涉及稀客订单、加料组合、预算和排序权重，放在 Worker 中执行可以避免经营页 UI 卡顿。
 * Hook 同一时间只允许一个 active 请求在 worker 中执行，快照连续刷新时只保留最新 queued 请求。
 * 这样可以避免较重计算被 750ms 快照刷新连续冲掉，导致界面长期停留在 pending 状态。
 */
export function useOrderRecommendations(
  payload: OrderRecommendationWorkerPayload,
  {
    enabled = true,
    inputSignature = '',
    contextSignature = '',
  }: UseOrderRecommendationsOptions = {},
): AsyncOrderRecommendationResult {
  const [state, setState] = useState<AsyncOrderRecommendationResult>(EMPTY_ASYNC_RECOMMENDATIONS);
  const workerRef = useRef<Worker | null>(null);
  const requestSequenceRef = useRef(0);
  const stateVersionRef = useRef(0);
  const activeRequestIdRef = useRef<number | null>(null);
  const activeRequestRef = useRef<OrderRecommendationWorkerRequest | null>(null);
  const queuedRequestRef = useRef<OrderRecommendationWorkerRequest | null>(null);
  const payloadRef = useRef(payload);
  const inputSignatureRef = useRef(inputSignature);
  const contextSignatureRef = useRef(contextSignature);
  const lastResultSignatureRef = useRef('');
  const postedDataSignatureRef = useRef('');
  const successRevisionRef = useRef(0);

  useEffect(() => {
    payloadRef.current = payload;
    inputSignatureRef.current = inputSignature;
    contextSignatureRef.current = contextSignature;
  }, [contextSignature, inputSignature, payload]);

  const createRequest = useCallback((
    nextPayload: OrderRecommendationWorkerPayload,
    sourceSignature: string,
    requestContextSignature: string,
  ): OrderRecommendationWorkerRequest => {
    requestSequenceRef.current += 1;
    const dataSignature = buildRecommendationDataSignature(nextPayload.data);
    const includeData = postedDataSignatureRef.current !== dataSignature;
    return {
      requestId: requestSequenceRef.current,
      sourceSignature,
      contextSignature: requestContextSignature,
      payload: buildRuntimePayload(nextPayload, dataSignature, includeData),
    };
  }, []);

  const postRequest = useCallback((worker: Worker, request: OrderRecommendationWorkerRequest) => {
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
    if (!enabled) {
      workerRef.current?.terminate();
      workerRef.current = null;
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      queuedRequestRef.current = null;
      lastResultSignatureRef.current = '';
      postedDataSignatureRef.current = '';
      return undefined;
    }

    const worker = new Worker(new URL('../workers/order-recommendations.worker.ts', import.meta.url), {
      type: 'module',
    });
    workerRef.current = worker;

    worker.onmessage = (event: MessageEvent<OrderRecommendationWorkerResponse>) => {
      const response = event.data;
      if (response.requestId !== activeRequestIdRef.current) return;

      const activeRequest = activeRequestRef.current;
      if (!activeRequest) return;
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      let queuedRequest = queuedRequestRef.current;
      queuedRequestRef.current = null;
      const hasQueuedRequest = queuedRequest !== null;
      let queueError: string | null = null;

      if (response.ok) {
        if (activeRequest.payload.data) {
          postedDataSignatureRef.current = activeRequest.payload.dataSignature;
        }
      } else if (isDataCacheMiss(response.error) && !activeRequest.payload.data) {
        postedDataSignatureRef.current = '';
        queuedRequest = createRequest(
          payloadRef.current,
          inputSignatureRef.current,
          contextSignatureRef.current,
        );
      } else if (activeRequest.payload.data) {
        postedDataSignatureRef.current = '';
      }

      if (queuedRequest) {
        try {
          postRequest(worker, queuedRequest);
        } catch (error) {
          queueError = error instanceof Error ? error.message : String(error);
        }
      }

      if (response.ok) {
        successRevisionRef.current += 1;
        const successRevision = successRevisionRef.current;
        const pending = hasQueuedRequest && !queueError;
        const isCurrent = !hasQueuedRequest || queueError !== null;
        const sourceSignature = activeRequest.sourceSignature;
        const resultContextSignature = activeRequest.contextSignature;
        if (lastResultSignatureRef.current === response.signature) {
          setState((current) => ({
            ...current,
            pending,
            isCurrent,
            sourceSignature,
            resultContextSignature,
            successRevision,
            retainedAfterError: Boolean(queueError),
            error: queueError,
          }));
          return;
        }

        lastResultSignatureRef.current = response.signature;
        setState({
          ...response.result,
          pending,
          isCurrent,
          sourceSignature,
          resultContextSignature,
          successRevision,
          retainedAfterError: Boolean(queueError),
          error: queueError,
        });
        return;
      }

      setState((current) => {
        const retryPending = queuedRequest !== null && !queueError;
        if (retryPending) {
          return {
            ...current,
            pending: true,
            isCurrent: false,
            retainedAfterError: false,
            error: response.error,
          };
        }
        const canRetainPrevious = current.successRevision > 0
          && current.resultContextSignature === activeRequest.contextSignature;
        if (canRetainPrevious && !queueError) {
          return {
            ...current,
            pending: false,
            isCurrent: false,
            retainedAfterError: true,
            error: response.error,
          };
        }
        const failurePayload = queueError ? payloadRef.current : activeRequest.payload;
        return {
          ...buildFailureResult(failurePayload, queueError ?? response.error),
          pending: false,
          isCurrent: true,
          sourceSignature: queueError ? inputSignatureRef.current : activeRequest.sourceSignature,
          resultContextSignature: queueError ? contextSignatureRef.current : activeRequest.contextSignature,
          successRevision: current.successRevision,
          retainedAfterError: false,
          error: queueError ?? response.error,
        };
      });
    };

    worker.onerror = (event) => {
      const message = event.message || '推荐计算 Worker 运行失败。';
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      queuedRequestRef.current = null;
      postedDataSignatureRef.current = '';
      lastResultSignatureRef.current = '';
      setState({
        ...buildFailureResult(payloadRef.current, message),
        pending: false,
        isCurrent: true,
        sourceSignature: inputSignatureRef.current,
        resultContextSignature: contextSignatureRef.current,
        successRevision: successRevisionRef.current,
        retainedAfterError: false,
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
  }, [createRequest, enabled, postRequest]);

  useEffect(() => {
    const stateVersion = stateVersionRef.current + 1;
    stateVersionRef.current = stateVersion;
    // 状态切换延后到 microtask，避免 React 同步渲染阶段中连续 payload 变化造成过期 pending 状态闪烁。
    const scheduleCurrentState = (
      buildNextState: (
        current: AsyncOrderRecommendationResult,
      ) => AsyncOrderRecommendationResult,
    ) => {
      queueMicrotask(() => {
        if (stateVersionRef.current !== stateVersion) return;
        setState(buildNextState);
      });
    };

    if (!enabled || !hasOrderRecommendationWork(payload)) {
      activeRequestIdRef.current = null;
      activeRequestRef.current = null;
      queuedRequestRef.current = null;
      lastResultSignatureRef.current = '';
      scheduleCurrentState(() => ({
        ...EMPTY_ASYNC_RECOMMENDATIONS,
        successRevision: successRevisionRef.current,
      }));
      return;
    }

    const worker = workerRef.current;
    if (!worker) {
      scheduleCurrentState(() => ({
        ...buildFailureResult(payload, '推荐计算 Worker 尚未初始化。'),
        pending: false,
        isCurrent: true,
        sourceSignature: inputSignature,
        resultContextSignature: contextSignature,
        successRevision: successRevisionRef.current,
        retainedAfterError: false,
        error: '推荐计算 Worker 尚未初始化。',
      }));
      return;
    }

    const request = createRequest(payload, inputSignature, contextSignature);
    if (activeRequestIdRef.current === null) {
      try {
        postRequest(worker, request);
      } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        scheduleCurrentState(() => ({
          ...buildFailureResult(payload, message),
          pending: false,
          isCurrent: true,
          sourceSignature: inputSignature,
          resultContextSignature: contextSignature,
          successRevision: successRevisionRef.current,
          retainedAfterError: false,
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
        ...current,
        pending: true,
        isCurrent: false,
        retainedAfterError: false,
        error: null,
      };
    });
  }, [contextSignature, createRequest, enabled, inputSignature, payload, postRequest]);

  const sourceIsCurrent = !enabled
    || !hasOrderRecommendationWork(payload)
    || state.sourceSignature === inputSignature;
  return state.isCurrent && !sourceIsCurrent
    ? { ...state, isCurrent: false }
    : state;
}

function buildRuntimePayload(
  payload: OrderRecommendationWorkerPayload,
  dataSignature: string,
  includeData: boolean,
): OrderRecommendationWorkerRuntimePayload {
  const { data, ...rest } = payload;
  return includeData
    ? { ...rest, data, dataSignature }
    : { ...rest, dataSignature };
}

function buildFailureResult(
  payload: Pick<OrderRecommendationWorkerPayload, 'orders'>,
  message: string,
): OrderRecommendationResult {
  return {
    recommendations: [],
    recommendationIssues: payload.orders.map((order) => ({
      order,
      message,
    })),
    normalOrderDetailPlans: [],
    normalExecutionTargets: [],
  };
}

function hasOrderRecommendationWork(payload: OrderRecommendationWorkerPayload): boolean {
  return payload.orders.length > 0
    || (payload.includeNormalOrderDetails === true && (payload.normalOrders?.length ?? 0) > 0)
    || (payload.includeNormalExecutionTargets === true && (payload.normalOrders?.length ?? 0) > 0);
}

function isDataCacheMiss(error: string): boolean {
  return error.includes(DATA_CACHE_MISS_MESSAGE);
}
