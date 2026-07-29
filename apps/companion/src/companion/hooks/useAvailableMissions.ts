import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { readAvailableMissions } from '@/companion/api';
import {
  AVAILABLE_MISSION_POLL_INTERVAL_MS,
  getAvailableMissionTransientRetryDelayMs,
  getAvailableMissionsResponseError,
  parseAvailableMissionsApiResponse,
} from '@/companion/available-missions';
import type { AvailableMissionsResponse } from '@/companion/types';

const AVAILABLE_MISSION_REQUEST_TIMEOUT_MS = 5_000;

interface UseAvailableMissionsOptions {
  active: boolean;
  apiToken: string;
  connected: boolean;
  connectionRevision: number;
  daySceneGeneration: number;
  daySceneReady: boolean;
  missionGeneration: number;
  normalizedEndpoint: string;
}

export function useAvailableMissions({
  active,
  apiToken,
  connected,
  connectionRevision,
  daySceneGeneration,
  daySceneReady,
  missionGeneration,
  normalizedEndpoint,
}: UseAvailableMissionsOptions) {
  const [result, setResult] = useState<AvailableMissionsResponse | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [refreshEpoch, setRefreshEpoch] = useState(0);
  const resultRef = useRef<AvailableMissionsResponse | null>(null);
  const latestIdentityRef = useRef<string | null>(null);
  const abortControllerRef = useRef<AbortController | null>(null);
  const requestGenerationRef = useRef(0);
  const activeRequestIdentityRef = useRef<string | null>(null);
  const retryAttemptRef = useRef(0);
  const refreshTimerRef = useRef<number | null>(null);
  const forceNextReadRef = useRef(false);

  const identity = useMemo(
    () => active
      && connected
      && apiToken
      && daySceneReady
      && daySceneGeneration > 0
      && missionGeneration > 0
      ? JSON.stringify([
        connectionRevision,
        normalizedEndpoint,
        apiToken,
        daySceneGeneration,
        missionGeneration,
      ])
      : null,
    [
      active,
      apiToken,
      connected,
      connectionRevision,
      daySceneGeneration,
      daySceneReady,
      missionGeneration,
      normalizedEndpoint,
    ],
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

  const clearResult = useCallback(() => {
    resultRef.current = null;
    setResult(null);
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
    const transientDelay = getAvailableMissionTransientRetryDelayMs(retryAttemptRef.current);
    const delay = transientDelay ?? AVAILABLE_MISSION_POLL_INTERVAL_MS;
    if (transientDelay != null) retryAttemptRef.current += 1;
    scheduleRead(requestIdentity, delay);
  }, [scheduleRead]);

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
      rawResponse = await readAvailableMissions(
        normalizedEndpoint,
        apiToken,
        {
          signal: abortController.signal,
          timeoutMs: AVAILABLE_MISSION_REQUEST_TIMEOUT_MS,
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
      response = parseAvailableMissionsApiResponse(rawResponse);
    } catch (protocolError) {
      clearResult();
      setError(`可接取任务接口响应无效：${protocolError instanceof Error ? protocolError.message : String(protocolError)}`);
      setLoading(false);
      clearRefreshTimer();
      return;
    }

    if ('unchanged' in response && response.unchanged) {
      const currentResult = resultRef.current;
      if (!currentResult || currentResult.contentSignature !== response.contentSignature) {
        clearResult();
        setError('可接取任务接口返回了无法对应当前数据的未变化响应。');
        setLoading(false);
        clearRefreshTimer();
        return;
      }

      retryAttemptRef.current = 0;
      setError('');
      setLoading(false);
      scheduleRead(requestIdentity, AVAILABLE_MISSION_POLL_INTERVAL_MS);
      return;
    }

    if (!response.ok || !response.runtimeAvailable) {
      clearResult();
      setError(getAvailableMissionsResponseError(response));
      setLoading(false);
      if (!response.runtimeAvailable) scheduleTransientRetry(requestIdentity);
      else clearRefreshTimer();
      return;
    }
    if (response.daySceneGeneration !== daySceneGeneration) {
      clearResult();
      setError('可接取任务响应与当前日间场景代际不一致，已拒绝旧结果。');
      setLoading(false);
      clearRefreshTimer();
      return;
    }
    if (response.missionGeneration !== missionGeneration) {
      clearResult();
      setError('可接取任务响应与当前任务代际不一致，已拒绝旧结果。');
      setLoading(false);
      clearRefreshTimer();
      return;
    }

    resultRef.current = response;
    setResult(response);
    setError('');
    setLoading(false);
    retryAttemptRef.current = 0;
    scheduleRead(requestIdentity, AVAILABLE_MISSION_POLL_INTERVAL_MS);
  }, [
    apiToken,
    cancelRequest,
    clearRefreshTimer,
    clearResult,
    daySceneGeneration,
    missionGeneration,
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
    availableMissions: result,
    availableMissionsError: error,
    availableMissionsLoading: loading,
    refreshAvailableMissions: refresh,
  };
}
