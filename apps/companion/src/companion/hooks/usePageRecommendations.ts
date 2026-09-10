import { useEffect, useMemo, useRef, useState } from 'react';
import type {
  PageRecommendationPayload,
  PageRecommendationResult,
  PageRecommendationWorkerRequest,
  PageRecommendationWorkerResponse,
} from '@/companion/workers/page-recommendations.types';
import { buildRecommendationDataSignature } from '@/lib/recommendation-data';

interface RequestContext {
  payload: PageRecommendationPayload;
  dataSignature: string;
  selectionKey: string;
}

interface ResultState {
  source: RequestContext | null;
  result: PageRecommendationResult | null;
  settled: RequestContext | null;
  error: string | null;
}

/** 结果始终携带发起请求的上下文，不会在选择改变后重新归属到另一个客人。 */
export function usePageRecommendations(payload: PageRecommendationPayload | null, connectionRevision: number) {
  const data = payload?.data;
  const dataSignature = useMemo(() => data ? buildRecommendationDataSignature(data) : '', [data]);
  const context = useMemo<RequestContext | null>(() => payload ? {
    payload,
    dataSignature,
    selectionKey: JSON.stringify(payload.kind === 'normal'
      ? [connectionRevision, dataSignature, payload.kind, payload.selectedPlace]
      : [connectionRevision, dataSignature, payload.kind, payload.selectedCustomer.id, payload.foodTag, payload.beverageTag]),
  } : null, [payload, connectionRevision, dataSignature]);
  const [state, setState] = useState<ResultState>({ source: null, result: null, settled: null, error: null });
  const [retryRevision, setRetryRevision] = useState(0);
  const [workerFailure, setWorkerFailure] = useState<string | null>(null);
  const runnerRef = useRef<((context: RequestContext) => void) | null>(null);
  const enabled = context !== null;

  useEffect(() => {
    if (!enabled) return;
    let worker: Worker;
    try {
      worker = new Worker(new URL('../workers/page-recommendations.worker.ts', import.meta.url), { type: 'module' });
    } catch (error) {
      setWorkerFailure(`无法启动后台推荐计算：${String(error)}`);
      runnerRef.current = null;
      return;
    }
    setWorkerFailure(null);
    let disposed = false;
    let failed = false;
    let sequence = 0;
    let postedDataSignature = '';
    let active: { requestId: number; context: RequestContext; dataSignature: string } | null = null;
    let queued: RequestContext | null = null;

    const post = (next: RequestContext) => {
      const dataSignature = next.dataSignature;
      const { data, ...rest } = next.payload;
      const request: PageRecommendationWorkerRequest = {
        requestId: ++sequence,
        payload: { ...rest, dataSignature, ...(postedDataSignature === dataSignature ? {} : { data }) },
      };
      active = { requestId: request.requestId, context: next, dataSignature };
      try {
        worker.postMessage(request);
      } catch (error) {
        active = null;
        postedDataSignature = '';
        setState((current) => ({ ...current, settled: next, error: String(error) }));
      }
    };
    runnerRef.current = (next) => {
      if (failed) return;
      if (active) queued = next;
      else post(next);
    };
    worker.onmessage = (event: MessageEvent<PageRecommendationWorkerResponse>) => {
      const response = event.data;
      if (disposed || !active || response.requestId !== active.requestId) return;
      const completed = active;
      active = null;
      if (response.ok) {
        postedDataSignature = completed.dataSignature;
        setState({ source: completed.context, result: response.result, settled: completed.context, error: null });
      } else {
        postedDataSignature = '';
        setState((current) => ({ ...current, settled: completed.context, error: response.error }));
      }
      const next = queued;
      queued = null;
      if (next) post(next);
    };
    worker.onerror = (event) => {
      if (disposed) return;
      failed = true;
      worker.terminate();
      active = null;
      queued = null;
      postedDataSignature = '';
      setWorkerFailure(event.message || '后台推荐计算失败。');
    };
    return () => {
      disposed = true;
      runnerRef.current = null;
      worker.terminate();
    };
  }, [enabled, retryRevision]);

  useEffect(() => {
    if (context) runnerRef.current?.(context);
  }, [context, retryRevision]);

  const sameSelection = Boolean(context && state.source?.selectionKey === context.selectionKey);
  const error = context ? workerFailure || (state.settled === context ? state.error : null) : null;
  const isCurrent = Boolean(context && state.source === context && state.settled === context && !error);
  return {
    result: sameSelection ? state.result : null,
    pending: Boolean(context && !workerFailure && state.settled !== context),
    isCurrent,
    error,
    retry: () => {
      setWorkerFailure(null);
      setState((current) => ({ ...current, settled: null, error: null }));
      setRetryRevision((current) => current + 1);
    },
  };
}
