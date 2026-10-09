import { useEffect, useState } from 'react';
import { useBusinessConnection } from '@/companion/business-connection';
import { writeLocalApiJsonWithTimeout } from '@/companion/local-api';
import { BUSINESS_PROTOCOL_VERSION } from '@/companion/hooks/useBusinessStatus';
import type { PageRecommendationPayload, PageRecommendationResult } from '@/companion/business-types';
import { businessSourceContextKey, type BusinessSourceContext } from '@/companion/business-display-context';

interface PageRecommendationState {
  result: PageRecommendationResult | null;
  pending: boolean;
  isCurrent: boolean;
  error: string | null;
}
interface PageResponse extends PageRecommendationState {
  protocolVersion: number;
  sourceSnapshotSignature: string;
  sourceContext?: BusinessSourceContext;
}

/**
 * 页面提交小型只读意图，由 C# 用当前权威目录、配置及库存计算。
 * 串行轮询有界查询缓存，旧连接、旧选择和旧快照的结果不会被标记为当前推荐。
 */
export function usePageRecommendations(payload: PageRecommendationPayload | null): PageRecommendationState {
  const { endpoint, apiToken, snapshotSignature, sourceContext, enabled } = useBusinessConnection();
  const contextKey = businessSourceContextKey(sourceContext);
  const intent = payload == null ? '' : JSON.stringify(payload.kind === 'normal'
    ? { protocolVersion: BUSINESS_PROTOCOL_VERSION, kind: 'normal', selectedPlace: payload.selectedPlace }
    : { protocolVersion: BUSINESS_PROTOCOL_VERSION, kind: 'rare', customerId: payload.selectedCustomer.id,
      foodTag: payload.foodTag, beverageTag: payload.beverageTag });
  const requestKey = JSON.stringify([endpoint, apiToken, enabled, contextKey, intent]);
  const [state, setState] = useState<{ requestKey: string; response: PageResponse; display: PageRecommendationResult | null } | null>(null);
  useEffect(() => {
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const abort = new AbortController();
    queueMicrotask(() => { if (!disposed) setState(null); });
    async function poll() {
      if (disposed || !enabled || !intent) return;
      try {
        const response = await writeLocalApiJsonWithTimeout<PageResponse>(endpoint, apiToken, '/business/query', 5000,
          { signal: abort.signal, body: JSON.parse(intent) as unknown });
        if (response.protocolVersion !== BUSINESS_PROTOCOL_VERSION) throw new Error('Mod 业务协议不兼容，请更新成套 Mod 和客户端。');
        if (!disposed) setState(previous => {
          const sameContext = Boolean(contextKey) && businessSourceContextKey(response.sourceContext) === contextKey;
          return { requestKey, response,
            // 服务端 pending 的空响应不能擦掉同一查询的已完成显示；返回不同上下文则立即清空。
            display: sameContext ? response.result ?? (previous?.requestKey === requestKey ? previous.display : null) : null };
        });
      } catch (error) {
        if (!disposed) setState(previous => ({ requestKey,
          display: previous?.requestKey === requestKey ? previous.display : null,
          response: { protocolVersion: BUSINESS_PROTOCOL_VERSION, result: null,
            isCurrent: false, pending: false, sourceSnapshotSignature: '', error: error instanceof Error ? error.message : String(error) } }));
      } finally {
        if (!disposed && enabled) timer = setTimeout(() => { void poll(); }, 750);
      }
    }
    void poll();
    return () => { disposed = true; abort.abort(); clearTimeout(timer); };
  }, [endpoint, apiToken, enabled, intent, contextKey, requestKey]);
  if (!intent || !enabled) return { result: null, pending: false, isCurrent: false, error: null };
  const accepted = state?.requestKey === requestKey ? state : null;
  const response = accepted?.response;
  const current = response?.isCurrent === true && response.sourceSnapshotSignature === snapshotSignature
    && Boolean(contextKey) && businessSourceContextKey(response.sourceContext) === contextKey;
  return { result: accepted?.display ?? null, isCurrent: current,
    pending: !response?.error && (!accepted?.display || response?.isCurrent !== true || response.pending), error: response?.error ?? null };
}
