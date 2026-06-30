import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { readSnapshot } from '@/companion/api';
import {
  normalizeEndpoint,
  persistApiToken,
  persistEndpoint,
  readStoredApiToken,
  readStoredEndpoint,
} from '@/companion/storage';
import type { LocalApiSnapshot } from '@/companion/types';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import type { RuntimeDataCatalogSnapshot } from '@/lib/recommendation-data';

export const CONNECTION_RETRY_DELAYS_MS = [2000, 5000, 10000, 30000];
const INITIAL_PROBE_TIMEOUT_MS = 700;
const AUTO_POLL_TIMEOUT_MS = 1800;
const MANUAL_REFRESH_TIMEOUT_MS = 2800;
const CONNECTION_UPDATED_EVENT = 'connection-updated';

/**
 * 维护伴随窗口与游戏内本地 API 的连接状态。
 *
 * Hook 负责读取 Tauri 启动参数、持久化 endpoint/token、轮询快照、失败退避和手动暂停。
 * 运行时完整数据会单独缓存，避免游戏场景短暂不可读时推荐数据立即退化为空目录。
 */
export function useCompanionConnection(snapshotRefreshIntervalMs: number) {
  const [endpoint, setEndpoint] = useState(readStoredEndpoint);
  const [endpointDraft, setEndpointDraft] = useState(endpoint);
  const [apiToken, setApiToken] = useState(readStoredApiToken);
  const [snapshot, setSnapshot] = useState<LocalApiSnapshot | null>(null);
  const [cachedRuntimeData, setCachedRuntimeData] = useState<RuntimeDataCatalogSnapshot | null>(null);
  const [error, setError] = useState('');
  const [manualRefreshing, setManualRefreshing] = useState(false);
  const [connectionProbing, setConnectionProbing] = useState(false);
  const [connectionPaused, setConnectionPaused] = useState(false);
  const [connectionFailureCount, setConnectionFailureCount] = useState(0);
  const [connectionRevision, setConnectionRevision] = useState(0);
  const [lastConnectedAt, setLastConnectedAt] = useState<Date | null>(null);
  const latestRequestIdRef = useRef(0);
  const inFlightRequestIdRef = useRef<number | null>(null);

  const normalizedEndpoint = useMemo(() => normalizeEndpoint(endpoint), [endpoint]);
  const normalizedEndpointDraft = useMemo(() => normalizeEndpoint(endpointDraft), [endpointDraft]);

  const applyRuntimeConnection = useCallback((launchEndpoint?: string | null, launchToken?: string | null) => {
    if (!launchEndpoint && !launchToken) return;

    // 启动参数或控制端口带来的连接信息代表当前游戏进程状态，收到后立即废弃旧请求和旧快照。
    latestRequestIdRef.current += 1;
    inFlightRequestIdRef.current = null;
    if (launchEndpoint) {
      const normalizedLaunchEndpoint = normalizeEndpoint(launchEndpoint);
      setEndpoint(normalizedLaunchEndpoint);
      setEndpointDraft(normalizedLaunchEndpoint);
    }
    if (launchToken) {
      setApiToken(launchToken);
    }
    setSnapshot(null);
    setCachedRuntimeData(null);
    setConnectionPaused(false);
    setConnectionFailureCount(0);
    setError('');
    setManualRefreshing(false);
    setConnectionProbing(false);
    setConnectionRevision((current) => current + 1);
  }, []);

  const readLaunchConnection = useCallback(async (shouldSkip?: () => boolean) => {
    const { invoke } = await import('@tauri-apps/api/core');
    const [launchEndpoint, launchToken] = await Promise.all([
      invoke<string | null>('launch_api_endpoint'),
      invoke<string | null>('launch_api_token'),
    ]);
    if (shouldSkip?.()) return;
    applyRuntimeConnection(launchEndpoint, launchToken);
  }, [applyRuntimeConnection]);

  const applyEndpointConnection = useCallback(() => {
    // 递增请求序号会让已发出的旧请求响应失效，避免切换 endpoint 后旧响应覆盖新连接状态。
    latestRequestIdRef.current += 1;
    inFlightRequestIdRef.current = null;
    setEndpoint(normalizedEndpointDraft);
    setEndpointDraft(normalizedEndpointDraft);
    setConnectionPaused(false);
    setConnectionFailureCount(0);
    setError('');
    setSnapshot(null);
    setCachedRuntimeData(null);
    setManualRefreshing(false);
    setConnectionProbing(false);
  }, [normalizedEndpointDraft]);

  const pauseConnection = useCallback(() => {
    latestRequestIdRef.current += 1;
    inFlightRequestIdRef.current = null;
    setConnectionPaused(true);
    setManualRefreshing(false);
    setConnectionProbing(false);
    setError('已停止自动重连。');
  }, []);

  const refresh = useCallback(async (manual = false) => {
    if (!apiToken) {
      setError('未收到本地 API Token。请从游戏内启动或按 F8 唤起伴随窗口。');
      setManualRefreshing(false);
      setConnectionProbing(false);
      return;
    }
    if (!manual && connectionPaused) return;
    if (inFlightRequestIdRef.current !== null && !manual) return;

    const requestId = latestRequestIdRef.current + 1;
    latestRequestIdRef.current = requestId;
    inFlightRequestIdRef.current = requestId;
    const timeoutMs = manual
      ? MANUAL_REFRESH_TIMEOUT_MS
      : snapshot
        ? AUTO_POLL_TIMEOUT_MS
        : INITIAL_PROBE_TIMEOUT_MS;
    if (manual) {
      setManualRefreshing(true);
    } else if (!snapshot) {
      setConnectionProbing(true);
    }
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), timeoutMs);

    try {
      const data = await readSnapshot(normalizedEndpoint, apiToken, {
        signal: abortController.signal,
        timeoutMs,
      });
      if (latestRequestIdRef.current !== requestId) return;
      setSnapshot(data);
      setError('');
      setConnectionPaused(false);
      setConnectionFailureCount(0);
      setLastConnectedAt(new Date());
    } catch (err) {
      if (latestRequestIdRef.current !== requestId) return;
      setError(err instanceof Error ? err.message : String(err));
      setConnectionFailureCount((current) => Math.min(current + 1, CONNECTION_RETRY_DELAYS_MS.length));
    } finally {
      window.clearTimeout(timeoutId);
      if (inFlightRequestIdRef.current === requestId) {
        inFlightRequestIdRef.current = null;
      }
      if (latestRequestIdRef.current === requestId) {
        if (manual) setManualRefreshing(false);
        if (!manual && !snapshot) setConnectionProbing(false);
      }
    }
  }, [apiToken, connectionPaused, normalizedEndpoint, snapshot]);

  useEffect(() => {
    persistEndpoint(normalizedEndpoint);
  }, [normalizedEndpoint]);

  useEffect(() => {
    persistApiToken(apiToken);
  }, [apiToken]);

  useEffect(() => {
    if (!snapshot) return;
    if (snapshot.runtimeData?.isComplete) {
      setCachedRuntimeData(snapshot.runtimeData);
    }
  }, [snapshot]);

  useEffect(() => {
    if (!isTauriRuntime()) return;

    let disposed = false;
    readLaunchConnection(() => disposed)
      .catch(() => {
        // 浏览器开发模式没有 Tauri 启动参数，连接信息由 localStorage 或页面输入提供。
      });

    return () => {
      disposed = true;
    };
  }, [readLaunchConnection]);

  useEffect(() => {
    if (!isTauriRuntime()) return;

    let disposed = false;
    let unlisten: (() => void) | undefined;
    import('@tauri-apps/api/event')
      .then(({ listen }) => listen<boolean>(CONNECTION_UPDATED_EVENT, () => {
        if (!disposed) void readLaunchConnection(() => disposed);
      }))
      .then((nextUnlisten) => {
        if (disposed) {
          nextUnlisten();
          return;
        }
        unlisten = nextUnlisten;
      })
      .catch(() => {
        // 浏览器开发模式没有 Tauri 事件通道，连接参数仍由 localStorage 或页面输入提供。
      });

    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [readLaunchConnection]);

  useEffect(() => {
    if (connectionRevision === 0 || !apiToken || connectionPaused) return;
    const timer = window.setTimeout(() => {
      void refresh();
    }, 0);
    return () => window.clearTimeout(timer);
  }, [apiToken, connectionPaused, connectionRevision, refresh]);

  useEffect(() => {
    if (!apiToken || connectionPaused) return;
    // 有错误时按固定退避序列重连；已连接后使用调用方传入的刷新间隔，经营中页面会传入更短间隔。
    const retryIndex = Math.max(0, Math.min(connectionFailureCount - 1, CONNECTION_RETRY_DELAYS_MS.length - 1));
    const delay = error
      ? CONNECTION_RETRY_DELAYS_MS[retryIndex]
      : snapshot
        ? snapshotRefreshIntervalMs
        : 0;
    const timer = window.setTimeout(() => {
      void refresh();
    }, delay);
    return () => window.clearTimeout(timer);
  }, [
    apiToken,
    connectionFailureCount,
    connectionPaused,
    error,
    refresh,
    snapshot,
    snapshotRefreshIntervalMs,
  ]);

  return {
    endpointDraft,
    setEndpointDraft,
    apiToken,
    setApiToken,
    snapshot,
    cachedRuntimeData,
    error,
    loading: manualRefreshing,
    connectionProbing,
    connectionPaused,
    connectionFailureCount,
    lastConnectedAt,
    normalizedEndpoint,
    applyEndpointConnection,
    pauseConnection,
    refresh,
  };
}
