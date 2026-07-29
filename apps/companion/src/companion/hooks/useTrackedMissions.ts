import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { readTrackedMissions } from '@/companion/api';
import {
  getTrackedMissionTransientRetryDelayMs,
  getTrackedMissionsResponseError,
  parseTrackedMissionsApiResponse,
  TRACKED_MISSION_POLL_INTERVAL_MS,
} from '@/companion/tracked-missions';
import type { TrackedMissionsResponse } from '@/companion/types';

const TRACKED_MISSION_REQUEST_TIMEOUT_MS = 5_000;

interface UseTrackedMissionsOptions {
  active: boolean;
  apiToken: string;
  connected: boolean;
  connectionRevision: number;
  normalizedEndpoint: string;
}

export function useTrackedMissions({
  active,
  apiToken,
  connected,
  connectionRevision,
  normalizedEndpoint,
}: UseTrackedMissionsOptions) {
  const [result, setResult] = useState<TrackedMissionsResponse | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [refreshEpoch, setRefreshEpoch] = useState(0);
  const resultRef = useRef<TrackedMissionsResponse | null>(null);
  const latestIdentityRef = useRef<string | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  const requestGenerationRef = useRef(0);
  const activeRequestIdentityRef = useRef<string | null>(null);
  const retryAttemptRef = useRef(0);
  const refreshTimerRef = useRef<number | null>(null);
  const forceNextReadRef = useRef(false);

  const identity = useMemo(
    () => active && connected && apiToken
      ? JSON.stringify([connectionRevision, normalizedEndpoint, apiToken])
      : null,
    [active, apiToken, connected, connectionRevision, normalizedEndpoint],
  );
  latestIdentityRef.current = identity;

  const clearRefreshTimer = useCallback(() => {
    if (refreshTimerRef.current == null) return;
    window.clearTimeout(refreshTimerRef.current);
    refreshTimerRef.current = null;
  }, []);

  const cancelRequest = useCallback(() => {
    requestGenerationRef.current += 1;
    abortControllerRef.current?.abort();
    abortControllerRef.current = null;
    activeRequestIdentityRef.current = null;
  }, []);

  const scheduleRead = useCallback((requestIdentity: string, delayMs: number) => {
    clearRefreshTimer();
    refreshTimerRef.current = window.setTimeout(() => {
      refreshTimerRef.current = null;
      if (latestIdentityRef.current !== requestIdentity) return;
      setRefreshEpoch((current) => current + 1);
    }, delayMs);
  }, [clearRefreshTimer]);

  const scheduleTransientRetry = useCallback((requestIdentity: string) => {
    const transientDelay = getTrackedMissionTransientRetryDelayMs(retryAttemptRef.current);
    const delay = transientDelay ?? TRACKED_MISSION_POLL_INTERVAL_MS;
    if (transientDelay != null) retryAttemptRef.current += 1;
    scheduleRead(requestIdentity, delay);
  }, [scheduleRead]);

  const clearResult = useCallback(() => {
    resultRef.current = null;
    setResult(null);
  }, []);

  const runRead = useCallback(async (requestIdentity: string, force: boolean) => {
    if (latestIdentityRef.current !== requestIdentity
        || activeRequestIdentityRef.current === requestIdentity) {
      return;
    }

    cancelRequest();
    const requestGeneration = requestGenerationRef.current + 1;
    requestGenerationRef.current = requestGeneration;
    const abortController = new AbortController();
    abortControllerRef.current = abortController;
    activeRequestIdentityRef.current = requestIdentity;
    const previousResult = resultRef.current;
    const knownSignature = force ? undefined : previousResult?.contentSignature;
    if (force || previousResult === null) setLoading(true);

    let rawResponse: unknown;
    try {
      rawResponse = await readTrackedMissions(
        normalizedEndpoint,
        apiToken,
        {
          signal: abortController.signal,
          timeoutMs: TRACKED_MISSION_REQUEST_TIMEOUT_MS,
          knownSignature,
        },
      );
    } catch (requestError) {
      if (abortController.signal.aborted
          || requestGenerationRef.current !== requestGeneration
          || latestIdentityRef.current !== requestIdentity) {
        return;
      }
      clearResult();
      setError(requestError instanceof Error ? requestError.message : String(requestError));
      setLoading(false);
      scheduleTransientRetry(requestIdentity);
      return;
    } finally {
      if (requestGenerationRef.current === requestGeneration) {
        abortControllerRef.current = null;
        activeRequestIdentityRef.current = null;
      }
    }

    if (requestGenerationRef.current !== requestGeneration
        || latestIdentityRef.current !== requestIdentity) {
      return;
    }

    let response;
    try {
      response = parseTrackedMissionsApiResponse(rawResponse);
    } catch (protocolError) {
      clearResult();
      setError(`任务接口响应无效：${protocolError instanceof Error ? protocolError.message : String(protocolError)}`);
      setLoading(false);
      clearRefreshTimer();
      return;
    }

    if ('unchanged' in response && response.unchanged) {
      const currentResult = resultRef.current;
      if (!currentResult || currentResult.contentSignature !== response.contentSignature) {
        clearResult();
        setError('任务接口返回了无法对应当前数据的未变化响应。');
        setLoading(false);
        clearRefreshTimer();
        return;
      }

      retryAttemptRef.current = 0;
      setError('');
      setLoading(false);
      scheduleRead(requestIdentity, TRACKED_MISSION_POLL_INTERVAL_MS);
      return;
    }

    if (!response.ok || !response.runtimeAvailable) {
      clearResult();
      setError(getTrackedMissionsResponseError(response));
      setLoading(false);
      if (!response.runtimeAvailable) scheduleTransientRetry(requestIdentity);
      else clearRefreshTimer();
      return;
    }

    resultRef.current = response;
    setResult(response);
    setError('');
    setLoading(false);
    retryAttemptRef.current = 0;
    scheduleRead(requestIdentity, TRACKED_MISSION_POLL_INTERVAL_MS);
  }, [
    apiToken,
    cancelRequest,
    clearRefreshTimer,
    clearResult,
    normalizedEndpoint,
    scheduleRead,
    scheduleTransientRetry,
  ]);

  useEffect(() => {
    cancelRequest();
    clearRefreshTimer();
    retryAttemptRef.current = 0;
    forceNextReadRef.current = false;
    clearResult();
    setError('');
    setLoading(identity !== null);
  }, [cancelRequest, clearRefreshTimer, clearResult, identity]);

  useEffect(() => {
    if (!identity) return;
    const force = forceNextReadRef.current;
    forceNextReadRef.current = false;
    void runRead(identity, force);
  }, [identity, refreshEpoch, runRead]);

  useEffect(() => () => {
    cancelRequest();
    clearRefreshTimer();
  }, [cancelRequest, clearRefreshTimer]);

  const refresh = useCallback(() => {
    if (!identity) return;
    cancelRequest();
    clearRefreshTimer();
    retryAttemptRef.current = 0;
    forceNextReadRef.current = true;
    clearResult();
    setError('');
    setLoading(true);
    setRefreshEpoch((current) => current + 1);
  }, [cancelRequest, clearRefreshTimer, clearResult, identity]);

  return {
    trackedMissions: result,
    trackedMissionsError: error,
    trackedMissionsLoading: loading,
    refreshTrackedMissions: refresh,
  };
}
