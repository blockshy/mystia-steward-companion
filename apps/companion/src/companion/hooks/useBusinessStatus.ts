import { useCallback, useEffect, useRef, useState } from 'react';
import { readLocalApiJson, writeLocalApiJsonWithTimeout } from '@/companion/local-api';
import type { OrderRecommendationResult } from '@/companion/business-types';
import { businessSourceContextKey, type BusinessSourceContext } from '@/companion/business-display-context';
import type {
  AutomationResourceOverview, AutomationSafetyBarrierDiagnostic, NormalAutoOrderDiagnostic,
  RareAutoOrderDiagnostic, RuntimeSets, GameUiTargetSlots,
} from '@/companion/types';

export const BUSINESS_PROTOCOL_VERSION = 1;
export const EMPTY_BUSINESS_RECOMMENDATIONS: OrderRecommendationResult = {
  recommendations: [], recommendationIssues: [], normalOrderDetailPlans: [], normalExecutionTargets: [],
};

/** 由 Mod 唯一编排器发布的诊断。客户端不根据这些字段推导或重新发起游戏动作。 */
export interface BusinessAutomationStatus {
  scopeVersion: number;
  runtimeEnabled: boolean;
  leaseOwned: boolean;
  message: string;
  states: Record<string, { lastRuntimeEventSequence: number; manualResolutionRequired: boolean }>;
  rareBusy: boolean;
  normalBusy: boolean;
  rareDiagnostics: RareAutoOrderDiagnostic[];
  normalDiagnostics: NormalAutoOrderDiagnostic[];
  safetyBarriers: AutomationSafetyBarrierDiagnostic[];
  rejectedRecipeKeys: string[];
  inFlightCount: number;
  resourceOverview: AutomationResourceOverview;
}
const EMPTY_AUTOMATION: BusinessAutomationStatus = {
  scopeVersion: 0, runtimeEnabled: false, leaseOwned: false, message: '等待 C# 业务状态。', states: {}, rareBusy: false, normalBusy: false,
  rareDiagnostics: [], normalDiagnostics: [], safetyBarriers: [], rejectedRecipeKeys: [], inFlightCount: 0,
  resourceOverview: { cookers: [], normalBlocked: [] },
};
type RuntimeSetsWire = { [K in keyof RuntimeSets]: RuntimeSets[K] extends Set<infer V> ? V[] : RuntimeSets[K] };
interface BusinessStatusWire {
  protocolVersion: number;
  inputVersion?: string;
  sourceSnapshotSignature?: string;
  sourceContext?: BusinessSourceContext;
  authorityRevision?: number;
  isCurrent: boolean;
  pending: boolean;
  error: string | null;
  calculationMs?: number;
  runtimeSets?: RuntimeSetsWire | null;
  recommendations?: OrderRecommendationResult;
  automation?: BusinessAutomationStatus;
  gameUiTargets?: GameUiTargetSlots;
}
export interface BusinessStatus extends Omit<BusinessStatusWire, 'recommendations' | 'automation' | 'runtimeSets'> {
  recommendations: OrderRecommendationResult;
  automation: BusinessAutomationStatus;
  runtimeSets: RuntimeSets | null;
  refresh: () => void;
  gameUiTargets: GameUiTargetSlots;
}

/**
 * 串行轮询服务端缓存，不创建 Worker，也不上传客户端计算结果。
 * 响应必须属于当前连接及快照；断线、版本不符和旧响应均明确标记为不可执行/非当前。
 */
export function useBusinessStatus(endpoint: string, apiToken: string, snapshotSignature: string,
  sourceContext: BusinessSourceContext | null, enabled = true): BusinessStatus {
  const contextKey = businessSourceContextKey(sourceContext);
  // 状态同时绑定连接与展示边界，render 阶段即可拒绝上一连接；无需等 effect 才清屏。
  const requestKey = JSON.stringify([endpoint, apiToken, enabled, contextKey]);
  const [stored, setStored] = useState<{ requestKey: string; state: BusinessStatusWire; display: BusinessStatusWire | null } | null>(null);
  const accepted = stored?.requestKey === requestKey ? stored : null;
  const state = accepted?.state;
  const display = accepted?.display;
  const refreshRef = useRef<() => void>(() => undefined);
  const refresh = useCallback(() => refreshRef.current(), []);
  useEffect(() => {
    let disposed = false;
    let active = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let abort: AbortController | undefined;
    queueMicrotask(() => { if (!disposed) setStored(null); });
    async function poll() {
      if (disposed || active || !enabled) return;
      clearTimeout(timer);
      active = true;
      // 浏览器 fetch 也需要期限；Tauri 代理的超时不能覆盖普通网页/mock 环境。
      const requestAbort = new AbortController();
      abort = requestAbort;
      const requestTimer = setTimeout(() => requestAbort.abort(new Error('C# 业务状态读取超时，请检查连接。')), 5000);
      try {
        const next = await readLocalApiJson<BusinessStatusWire>(endpoint, apiToken,
          `/business/status?protocolVersion=${BUSINESS_PROTOCOL_VERSION}`, { signal: requestAbort.signal, tauriTimeoutMs: 5000 });
        if (next.protocolVersion !== BUSINESS_PROTOCOL_VERSION) throw new Error('Mod 业务协议不兼容，请更新成套 Mod 和客户端。');
        if (!disposed) setStored(previous => {
          const sameContext = Boolean(contextKey) && businessSourceContextKey(next.sourceContext) === contextKey;
          return { requestKey, state: next,
            // 后台重算期间仍可展示同上下文最后成功投影；不同来源立即清空，不能授予 current/动作权限。
            display: sameContext ? !next.error && next.recommendations ? next
              : previous?.requestKey === requestKey ? previous.display : null : null };
        });
      } catch (error) {
        if (!disposed) setStored(previous => ({ requestKey, display: previous?.requestKey === requestKey ? previous.display : null,
          state: { protocolVersion: BUSINESS_PROTOCOL_VERSION, isCurrent: false, pending: false,
          // 传输层可能将 AbortError 翻译为连接错误；以本次请求的中止状态保留明确超时原因。
          error: requestAbort.signal.aborted ? 'C# 业务状态读取超时，请检查连接。'
            : error instanceof Error ? error.message : String(error) } }));
      } finally {
        clearTimeout(requestTimer);
        if (abort === requestAbort) abort = undefined;
        active = false;
        if (!disposed && enabled) timer = setTimeout(() => { void poll(); }, 750);
      }
    }
    refreshRef.current = () => { void poll(); };
    void poll();
    return () => { disposed = true; abort?.abort(); clearTimeout(timer); refreshRef.current = () => undefined; };
  }, [endpoint, apiToken, enabled, contextKey, requestKey]);
  const current = enabled && state?.isCurrent === true && Boolean(snapshotSignature)
    && state.sourceSnapshotSignature === snapshotSignature && Boolean(contextKey)
    && businessSourceContextKey(state.sourceContext) === contextKey;
  return {
    protocolVersion: BUSINESS_PROTOCOL_VERSION,
    ...state,
    isCurrent: current,
    // 展示等待状态遵循服务端计算状态；独立 /snapshot 轮询的时间差不代表需要重新计算。
    pending: enabled && !state?.error && (!display || state?.isCurrent !== true || state.pending),
    error: state?.error ?? null,
    sourceContext: display?.sourceContext,
    recommendations: enabled ? display?.recommendations ?? EMPTY_BUSINESS_RECOMMENDATIONS : EMPTY_BUSINESS_RECOMMENDATIONS,
    automation: state?.automation ?? EMPTY_AUTOMATION,
    runtimeSets: enabled ? decodeRuntimeSets(display?.runtimeSets) : null,
    gameUiTargets: current ? state?.gameUiTargets ?? { rare: null, normal: null } : { rare: null, normal: null },
    refresh,
  };
}

/** 仅恢复线路集合以便页面查表；这里不重新判定可用性、预算或主方案。 */
function decodeRuntimeSets(value: RuntimeSetsWire | null | undefined): RuntimeSets | null {
  return value ? {
    ...value,
    recipeIds: new Set(value.recipeIds), beverageIds: new Set(value.beverageIds), ingredientIds: new Set(value.ingredientIds),
    unavailableIngredientIds: new Set(value.unavailableIngredientIds), placedCookerTypeIds: new Set(value.placedCookerTypeIds),
    placedCookerNames: new Set(value.placedCookerNames), usableCookerNames: new Set(value.usableCookerNames),
    runtimeUnavailableCookerNames: new Set(value.runtimeUnavailableCookerNames),
  } : null;
}

/** 重试是用户意图，目标、资源与实际动作仍由当前服务端状态重新决定。 */
export async function retryBusinessAutomation(endpoint: string, apiToken: string, authorityRevision: number,
  kind: 'rare' | 'normal', key: string): Promise<void> {
  const result = await writeLocalApiJsonWithTimeout<{ ok: boolean; error?: string }>(endpoint, apiToken,
    '/business/automation/retry', 5000, { authorityRevision, body: { protocolVersion: BUSINESS_PROTOCOL_VERSION, kind, key } });
  if (!result.ok) throw new Error(result.error || '订单暂时不能重试。');
}
