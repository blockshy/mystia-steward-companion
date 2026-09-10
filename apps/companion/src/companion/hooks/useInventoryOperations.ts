import { useCallback, useEffect, useRef, useState } from 'react';

import { writeInventoryBulkQuantity, writeInventoryQuantity } from '@/companion/api';
import { normalizeEditableQuantity } from '@/companion/preferences';
import type { InventoryBulkEditResponse, InventoryEditResponse, LocalApiSnapshot } from '@/companion/types';

type InventoryKind = 'ingredient' | 'beverage';
type InventoryOperationPhase = 'idle' | 'posting' | 'confirming' | 'refreshing' | 'succeeded' | 'partial' | 'failed' | 'unconfirmed';
interface InventoryOperationState {
  identity: string;
  phase: InventoryOperationPhase;
  message: string;
}

export interface InventoryOperationsController {
  busy: boolean;
  phase: InventoryOperationPhase;
  message: string;
  writable: boolean;
  refreshable: boolean;
  refresh: () => Promise<void>;
  applyQuantity: (kind: InventoryKind, id: number, quantity: number, label: string) => Promise<void>;
  applyBulkQuantity: (kind: InventoryKind, ids: readonly number[], quantity: number) => Promise<void>;
}

/** A single workbench-owned transport owner covers both writes and confirmation. */
export function useInventoryOperations({ endpoint, apiToken, connectionRevision, connected, runtimeReady, onRefresh }: {
  endpoint: string;
  apiToken: string;
  connectionRevision: number;
  connected: boolean;
  runtimeReady: boolean;
  onRefresh: () => Promise<LocalApiSnapshot | null>;
}): InventoryOperationsController {
  const identity = JSON.stringify([connectionRevision, endpoint, apiToken]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const mountedRef = useRef(false);
  const ownerRef = useRef<object | null>(null);
  const [busy, setBusy] = useState(false);
  const [state, setState] = useState<InventoryOperationState | null>(null);
  const currentState = state?.identity === identity ? state : null;
  const connectedReady = Boolean(apiToken && connected);
  const readable = connectedReady && !busy;
  const writable = Boolean(readable && runtimeReady && currentState?.phase !== 'unconfirmed');

  useEffect(() => {
    mountedRef.current = true;
    return () => { mountedRef.current = false; };
  }, []);

  const execute = useCallback(async (
    label: string,
    request: (() => Promise<InventoryEditResponse | InventoryBulkEditResponse>) | null,
  ) => {
    if (!mountedRef.current || ownerRef.current || (request ? !writable : !readable)) return;
    const owner = {};
    const requestIdentity = identity;
    ownerRef.current = owner;
    setBusy(true);
    const current = () => mountedRef.current && identityRef.current === requestIdentity;
    const publish = (phase: InventoryOperationPhase, message: string) => {
      if (current()) setState({ identity: requestIdentity, phase, message });
    };
    publish(request ? 'posting' : 'refreshing', request ? `正在提交：${label}。` : '正在重新读取当前库存。');
    try {
      const result = request ? await request() : null;
      if (!current()) return;
      if (request) publish('confirming', `${label}：Mod 已返回，正在刷新确认当前库存。`);
      const snapshot = await onRefresh();
      if (!current()) return;
      if (!snapshot?.recommendationState || !snapshot.runtimeLoaded) {
        publish('unconfirmed', request
          ? `${label}：写入请求已返回，但当前库存刷新未确认。请先刷新核对；不会自动重发写入。`
          : '未能确认当前库存，请刷新后再修改；上次写入不会自动重发。');
        return;
      }
      if (!result) {
        publish('succeeded', '当前库存已重新读取；上次写入不会自动重发。');
      } else if ('failed' in result) {
        const details = result.errors.length > 0 ? ` 详细原因：${result.errors.slice(0, 3).join('；')}` : result.error ? ` ${result.error}` : '';
        const phase = result.failed > 0 ? 'partial' : result.ok ? 'succeeded' : 'failed';
        publish(phase, `${label}：变更 ${result.changed} 项，未变 ${result.unchanged} 项，失败 ${result.failed} 项；当前库存已刷新。${details}`);
      } else if (!result.ok) {
        publish('failed', `${label}：${result.error || 'Mod 拒绝了本次修改'}；当前库存已刷新。`);
      } else {
        publish('succeeded', `${label}：Mod 返回 ${result.previousQuantity} → ${result.quantity}；当前库存已刷新。`);
      }
    } catch (reason) {
      publish('unconfirmed', `${label}：${reason instanceof Error ? reason.message : String(reason)}。结果尚未确认，请先刷新核对；不会自动重发写入。`);
    } finally {
      if (ownerRef.current === owner) ownerRef.current = null;
      if (mountedRef.current) {
        setBusy(false);
        if (identityRef.current !== requestIdentity) {
          // Even a same-address reconnect may precede completion of the old POST.
          // Require a fresh explicit read instead of trusting its earlier poll.
          setState({ identity: identityRef.current, phase: 'unconfirmed', message: '上一连接的库存请求已结束；请刷新当前库存后再修改。' });
        }
      }
    }
  }, [identity, onRefresh, readable, writable]);

  const applyQuantity = useCallback((kind: InventoryKind, id: number, quantity: number, name: string) => {
    if (!Number.isSafeInteger(id) || id < 0 || !Number.isFinite(quantity)) return Promise.resolve();
    const target = normalizeEditableQuantity(quantity);
    return execute(`${kind === 'ingredient' ? '材料' : '酒水'} ${name} 设为 ${target}`,
      () => writeInventoryQuantity(endpoint, apiToken, kind, id, target));
  }, [apiToken, endpoint, execute]);
  const applyBulkQuantity = useCallback((kind: InventoryKind, ids: readonly number[], quantity: number) => {
    if (ids.length === 0 || !Number.isFinite(quantity)
      || ids.some((id) => !Number.isSafeInteger(id) || id < 0)
      || new Set(ids).size !== ids.length) return Promise.resolve();
    const target = normalizeEditableQuantity(quantity);
    const frozenIds = [...ids];
    return execute(`全部已解锁${kind === 'ingredient' ? '材料' : '酒水'}设为 ${target}（${frozenIds.length} 项）`,
      () => writeInventoryBulkQuantity(endpoint, apiToken, kind, frozenIds, target));
  }, [apiToken, endpoint, execute]);
  const refresh = useCallback(() => execute('读取当前库存', null), [execute]);

  const message = busy && !currentState
    ? '上一连接的库存请求仍在处理；完成前暂停新的库存写入。'
    : currentState?.message || (!connectedReady ? '等待 Mod 本地 API 连接。' : !runtimeReady ? '等待完整库存数据。' : '');
  return {
    busy,
    phase: currentState?.phase || (busy ? 'posting' : 'idle'),
    message,
    writable,
    refreshable: readable,
    refresh,
    applyQuantity,
    applyBulkQuantity,
  };
}
