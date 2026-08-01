import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { readRuntimeData, readSnapshot } from '@/companion/api';
import {
  getConnectionRetryDelayMs,
  resolveCompanionConnectionIdentity,
  updateUnavailableRuntimeData,
} from '@/companion/connection-recovery';
import { validateRecommendationCookerSnapshot } from '@/companion/domain/cookers';
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

export { CONNECTION_RETRY_DELAYS_MS } from '@/companion/connection-recovery';
const INITIAL_SNAPSHOT_TIMEOUT_MS = 700;
const AUTO_POLL_TIMEOUT_MS = 1800;
const MANUAL_REFRESH_TIMEOUT_MS = 2800;
const RUNTIME_DATA_TIMEOUT_MS = 6000;
const CONNECTED_AT_UPDATE_INTERVAL_MS = 30_000;
const CONNECTION_UPDATED_EVENT = 'connection-updated';
const CONNECTION_ACTIVATED_EVENT = 'connection-activation-requested';

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
  const [apiTokenDraft, setApiTokenDraft] = useState(apiToken);
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
  const lastConnectedAtUpdateMsRef = useRef(0);
  const cachedRuntimeDataSignatureRef = useRef('');
  const snapshotSignatureRef = useRef('');
  const runtimeDataRequestIdRef = useRef(0);
  const runtimeDataInFlightSignatureRef = useRef('');
  const snapshotRef = useRef<LocalApiSnapshot | null>(null);
  const connectionPausedRef = useRef(false);
  const connectionIdentityRef = useRef({
    endpoint: normalizeEndpoint(endpoint),
    apiToken,
  });

  const normalizedEndpoint = useMemo(() => normalizeEndpoint(endpoint), [endpoint]);
  const normalizedEndpointDraft = useMemo(() => normalizeEndpoint(endpointDraft), [endpointDraft]);

  const clearSnapshotCache = useCallback(() => {
    lastConnectedAtUpdateMsRef.current = 0;
    cachedRuntimeDataSignatureRef.current = '';
    snapshotSignatureRef.current = '';
    runtimeDataRequestIdRef.current += 1;
    runtimeDataInFlightSignatureRef.current = '';
    snapshotRef.current = null;
    setSnapshot(null);
    setCachedRuntimeData(null);
  }, []);

  const markConnected = useCallback((force = false) => {
    const now = Date.now();
    if (!force && now - lastConnectedAtUpdateMsRef.current < CONNECTED_AT_UPDATE_INTERVAL_MS) return;
    lastConnectedAtUpdateMsRef.current = now;
    setLastConnectedAt(new Date(now));
  }, []);

  const resetConnection = useCallback((nextEndpoint: string, nextToken: string) => {
    connectionIdentityRef.current = { endpoint: nextEndpoint, apiToken: nextToken };
    latestRequestIdRef.current += 1;
    inFlightRequestIdRef.current = null;
    setEndpoint(nextEndpoint);
    setEndpointDraft(nextEndpoint);
    setApiToken(nextToken);
    setApiTokenDraft(nextToken);
    clearSnapshotCache();
    connectionPausedRef.current = false;
    setConnectionPaused(false);
    setConnectionFailureCount(0);
    setError('');
    setManualRefreshing(false);
    setConnectionProbing(false);
    setConnectionRevision((current) => current + 1);
  }, [clearSnapshotCache]);

  const applyRuntimeConnection = useCallback((launchEndpoint?: string | null, launchToken?: string | null) => {
    const resolution = resolveCompanionConnectionIdentity(connectionIdentityRef.current, {
      endpoint: launchEndpoint ? normalizeEndpoint(launchEndpoint) : null,
      apiToken: launchToken,
    });
    if (!resolution.changed) return;

    // 启动参数或控制端口确实切换连接身份时，旧请求和旧快照才失效。
    resetConnection(resolution.identity.endpoint, resolution.identity.apiToken);
  }, [resetConnection]);

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
    resetConnection(normalizedEndpointDraft, apiTokenDraft.trim());
  }, [apiTokenDraft, normalizedEndpointDraft, resetConnection]);

  const applyConnectionDetails = useCallback((nextEndpoint: string, nextToken: string) => {
    const normalizedNextEndpoint = normalizeEndpoint(nextEndpoint);
    const normalizedNextToken = nextToken.trim();
    resetConnection(normalizedNextEndpoint, normalizedNextToken);
  }, [resetConnection]);
  const pauseConnection = useCallback(() => {
    latestRequestIdRef.current += 1;
    inFlightRequestIdRef.current = null;
    connectionPausedRef.current = true;
    setConnectionPaused(true);
    setManualRefreshing(false);
    setConnectionProbing(false);
    setError('已停止自动重连。');
  }, []);

  const ensureRuntimeDataCache = useCallback((sourceSnapshot: LocalApiSnapshot) => {
    const runtimeDataSignature = sourceSnapshot.runtimeDataSignature ?? '';
    if (sourceSnapshot.runtimeDataComplete && runtimeDataSignature) {
      if (cachedRuntimeDataSignatureRef.current === runtimeDataSignature) return;
      if (runtimeDataInFlightSignatureRef.current === runtimeDataSignature) return;

      const requestId = runtimeDataRequestIdRef.current + 1;
      runtimeDataRequestIdRef.current = requestId;
      runtimeDataInFlightSignatureRef.current = runtimeDataSignature;
      const runtimeDataAbortController = new AbortController();
      const runtimeDataTimeoutId = window.setTimeout(
        () => runtimeDataAbortController.abort(),
        RUNTIME_DATA_TIMEOUT_MS,
      );

      void readRuntimeData(normalizedEndpoint, apiToken, {
        signal: runtimeDataAbortController.signal,
        timeoutMs: RUNTIME_DATA_TIMEOUT_MS,
      })
        .then((runtimeData) => {
          if (runtimeDataRequestIdRef.current !== requestId) return;
          if (!runtimeData.isComplete) {
            throw new Error(runtimeData.status || '运行时目录尚未完整加载。');
          }
          cachedRuntimeDataSignatureRef.current = runtimeDataSignature;
          setCachedRuntimeData(runtimeData);
        })
        .catch((runtimeDataError) => {
          if (runtimeDataRequestIdRef.current !== requestId) return;
          const runtimeDataStatus = runtimeDataError instanceof Error
            ? runtimeDataError.message
            : String(runtimeDataError);
          setCachedRuntimeData((current) => updateUnavailableRuntimeData(
            current,
            sourceSnapshot.runtimeDataSource || sourceSnapshot.runtimeSource || '',
            runtimeDataStatus || sourceSnapshot.runtimeDataStatus || '运行时目录读取失败，等待下一轮重试。',
          ));
        })
        .finally(() => {
          window.clearTimeout(runtimeDataTimeoutId);
          if (runtimeDataRequestIdRef.current === requestId) {
            runtimeDataInFlightSignatureRef.current = '';
          }
        });
      return;
    }

    if (!sourceSnapshot.runtimeDataComplete && !cachedRuntimeDataSignatureRef.current) {
      const status = sourceSnapshot.runtimeDataStatus || sourceSnapshot.status || '等待游戏运行时数据';
      setCachedRuntimeData((current) => updateUnavailableRuntimeData(
        current,
        sourceSnapshot.runtimeDataSource || sourceSnapshot.runtimeSource || '',
        status,
      ));
    }
  }, [apiToken, normalizedEndpoint]);

  const refresh = useCallback(async (manual = false) => {
    if (!apiToken) {
      setError('未收到本地 API Token。请从游戏内启动或按 F8 唤起伴随窗口。');
      setManualRefreshing(false);
      setConnectionProbing(false);
      return;
    }
    if (!manual && connectionPausedRef.current) return;
    if (inFlightRequestIdRef.current !== null) return;

    const requestId = latestRequestIdRef.current + 1;
    latestRequestIdRef.current = requestId;
    inFlightRequestIdRef.current = requestId;
    const currentSnapshot = snapshotRef.current;
    const timeoutMs = manual
      ? MANUAL_REFRESH_TIMEOUT_MS
      : currentSnapshot
        ? AUTO_POLL_TIMEOUT_MS
        : INITIAL_SNAPSHOT_TIMEOUT_MS;
    if (manual) {
      setManualRefreshing(true);
    } else if (!currentSnapshot) {
      setConnectionProbing(true);
    }
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), timeoutMs);

    try {
      const data = await readSnapshot(normalizedEndpoint, apiToken, {
        signal: abortController.signal,
        timeoutMs,
        knownSignature: snapshotSignatureRef.current,
      });
      if (latestRequestIdRef.current !== requestId) return;
      window.clearTimeout(timeoutId);
      if (isSnapshotUnchanged(data)) {
        snapshotSignatureRef.current = data.snapshotSignature;
        if (currentSnapshot) ensureRuntimeDataCache(currentSnapshot);
        setError('');
        connectionPausedRef.current = false;
        setConnectionPaused(false);
        setConnectionFailureCount(0);
        markConnected();
        return;
      }

      if (data.recommendationState) {
        const cookerSnapshotError = validateRecommendationCookerSnapshot(data.recommendationState);
        if (cookerSnapshotError) {
          throw new Error(`游戏快照中的厨具字段不完整：${cookerSnapshotError}`);
        }
      }
      const nextSnapshotSignature = data.snapshotSignature ?? '';
      snapshotSignatureRef.current = nextSnapshotSignature;
      snapshotRef.current = data;
      setSnapshot(data);
      ensureRuntimeDataCache(data);
      setError('');
      connectionPausedRef.current = false;
      setConnectionPaused(false);
      setConnectionFailureCount(0);
      markConnected(true);
    } catch (err) {
      if (latestRequestIdRef.current !== requestId) return;
      const nextError = err instanceof Error ? err.message : String(err);
      setError((current) => current === nextError ? current : nextError);
      setConnectionFailureCount((current) => current + 1);
    } finally {
      window.clearTimeout(timeoutId);
      if (inFlightRequestIdRef.current === requestId) {
        inFlightRequestIdRef.current = null;
      }
      if (latestRequestIdRef.current === requestId) {
        if (manual) setManualRefreshing(false);
        if (!manual && !snapshotRef.current) setConnectionProbing(false);
      }
    }
  }, [apiToken, ensureRuntimeDataCache, markConnected, normalizedEndpoint]);

  const resumePausedConnection = useCallback(() => {
    if (!connectionPausedRef.current) return;
    connectionPausedRef.current = false;
    setConnectionPaused(false);
    setConnectionFailureCount(0);
    setError('正在验证游戏快照。');
    setManualRefreshing(false);
    setConnectionRevision((current) => current + 1);
    void refresh();
  }, [refresh]);

  useEffect(() => {
    persistEndpoint(normalizedEndpoint);
  }, [normalizedEndpoint]);

  useEffect(() => {
    persistApiToken(apiToken);
  }, [apiToken]);

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
      .then(({ listen }) => listen<boolean>(CONNECTION_ACTIVATED_EVENT, async () => {
        if (disposed) return;
        try {
          await readLaunchConnection(() => disposed);
        } catch {
          return;
        }
        if (!disposed) resumePausedConnection();
      }))
      .then((nextUnlisten) => {
        if (disposed) {
          nextUnlisten();
          return;
        }
        unlisten = nextUnlisten;
      })
      .catch(() => {
        // 浏览器开发模式没有 Tauri 控制事件，暂停状态由页面上的连接开关恢复。
      });

    return () => {
      disposed = true;
      unlisten?.();
    };
  }, [readLaunchConnection, resumePausedConnection]);

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
    if (!apiToken || connectionPaused) return;
    if (!snapshot && !error) {
      const timer = window.setTimeout(() => {
        void refresh();
      }, 0);
      return () => window.clearTimeout(timer);
    }

    if (error) {
      // 失败状态只允许这一条定时器按固定退避序列重试完整快照。
      const timer = window.setTimeout(() => {
        void refresh();
      }, getConnectionRetryDelayMs(connectionFailureCount));
      return () => window.clearTimeout(timer);
    }

    // 已连接后使用独立 interval 保持轮询。不能依赖每次请求后的 React 状态变更来续约 timer；
    // /snapshot 返回 unchanged 时通常不会触发重新渲染，但仍必须继续观察后续经营状态变化。
    const timer = window.setInterval(() => {
      void refresh();
    }, snapshotRefreshIntervalMs);
    return () => window.clearInterval(timer);
  }, [
    apiToken,
    connectionFailureCount,
    connectionPaused,
    connectionRevision,
    error,
    refresh,
    snapshot,
    snapshotRefreshIntervalMs,
  ]);

  return {
    endpointDraft,
    setEndpointDraft,
    apiToken,
    apiTokenDraft,
    setApiTokenDraft,
    snapshot,
    cachedRuntimeData,
    error,
    loading: manualRefreshing,
    connectionProbing,
    connectionPaused,
    connectionFailureCount,
    connectionRevision,
    lastConnectedAt,
    normalizedEndpoint,
    applyEndpointConnection,
    applyConnectionDetails,
    pauseConnection,
    refresh,
  };
}

function isSnapshotUnchanged(
  data: Awaited<ReturnType<typeof readSnapshot>>,
): data is { unchanged: true; snapshotSignature: string } {
  return 'unchanged' in data && data.unchanged === true;
}
