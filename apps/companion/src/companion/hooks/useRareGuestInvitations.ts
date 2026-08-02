import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  fetchAvailableRareGuestInvitations,
  inviteAllAvailableRareGuests,
  inviteAvailableRareGuest,
} from '@/companion/api';
import {
  buildRareGuestInvitationRefreshIdentity,
  getRareGuestInvitationTransientRetryDelayMs,
} from '@/companion/rare-guest-invitation-refresh';
import {
  persistRareGuestInvitationLevels,
  persistRareGuestInvitationScope,
  readStoredRareGuestInvitationLevels,
  readStoredRareGuestInvitationScope,
} from '@/companion/storage';
import type {
  LocalApiSnapshot,
  RareGuestInvitationResponse,
  RareGuestInvitationScope,
  RareGuestInvitationWriteContext,
} from '@/companion/types';

interface UseRareGuestInvitationsOptions {
  active: boolean;
  apiToken: string;
  connected: boolean;
  connectionRevision: number;
  normalizedEndpoint: string;
  refresh: (manual?: boolean) => Promise<LocalApiSnapshot | null>;
  snapshot: LocalApiSnapshot | null;
}

export function useRareGuestInvitations({
  active,
  apiToken,
  connected,
  connectionRevision,
  normalizedEndpoint,
  refresh,
  snapshot,
}: UseRareGuestInvitationsOptions) {
  const [rareGuestInvitationScope, setRareGuestInvitationScopeState] = useState<RareGuestInvitationScope>(() =>
    readStoredRareGuestInvitationScope(),
  );
  const [rareGuestInvitationLevels, setRareGuestInvitationLevels] = useState<number[]>(() =>
    readStoredRareGuestInvitationLevels(),
  );
  const [rareGuestInvitationResult, setRareGuestInvitationResult] = useState<RareGuestInvitationResponse | null>(null);
  const [rareGuestInvitationError, setRareGuestInvitationError] = useState('');
  const [rareGuestInvitationBusyKey, setRareGuestInvitationBusyKey] = useState('');
  const [refreshEpoch, setRefreshEpoch] = useState(0);
  const requestGenerationRef = useRef(0);
  const listAbortControllerRef = useRef<AbortController | null>(null);
  const activeListIdentityRef = useRef<string | null>(null);
  const attemptedListIdentityRef = useRef<string | null>(null);
  const visibleIdentityRef = useRef<string | null>(null);
  const latestIdentityRef = useRef<string | null>(null);
  const transientRetryRef = useRef<{ identity: string | null; attemptIndex: number }>({
    identity: null,
    attemptIndex: 0,
  });
  const transientRetryTimerRef = useRef<number | null>(null);
  const nextOperationIdRef = useRef(0);
  const busyOperationRef = useRef({ id: 0, key: '' });

  const refreshIdentity = useMemo(
    () => buildRareGuestInvitationRefreshIdentity({
      active,
      connected,
      connectionRevision,
      normalizedEndpoint,
      scope: rareGuestInvitationScope,
      snapshot,
    }),
    [
      active,
      connected,
      connectionRevision,
      normalizedEndpoint,
      rareGuestInvitationScope,
      snapshot,
    ],
  );
  const writeContext = useMemo<RareGuestInvitationWriteContext | null>(() => {
    if (!refreshIdentity || !snapshot) return null;
    const expectedMapLabel = snapshot.activeDayMapLabel?.trim() ?? '';
    if (snapshot.runtimeDaySceneGeneration < 1 || !expectedMapLabel) return null;
    return {
      expectedDaySceneGeneration: snapshot.runtimeDaySceneGeneration,
      expectedMapLabel,
    };
  }, [refreshIdentity, snapshot]);
  latestIdentityRef.current = refreshIdentity;

  const clearTransientListRetry = useCallback(() => {
    if (transientRetryTimerRef.current != null) {
      window.clearTimeout(transientRetryTimerRef.current);
      transientRetryTimerRef.current = null;
    }
    transientRetryRef.current = { identity: null, attemptIndex: 0 };
  }, []);

  const scheduleTransientListRetry = useCallback((identity: string) => {
    if (latestIdentityRef.current !== identity) return;

    const previous = transientRetryRef.current;
    const attemptIndex = previous.identity === identity
      ? previous.attemptIndex
      : 0;
    const delay = getRareGuestInvitationTransientRetryDelayMs(attemptIndex);
    attemptedListIdentityRef.current = identity;

    if (transientRetryTimerRef.current != null) {
      window.clearTimeout(transientRetryTimerRef.current);
      transientRetryTimerRef.current = null;
    }
    if (delay == null) {
      transientRetryRef.current = { identity, attemptIndex };
      return;
    }

    transientRetryRef.current = {
      identity,
      attemptIndex: attemptIndex + 1,
    };
    transientRetryTimerRef.current = window.setTimeout(() => {
      transientRetryTimerRef.current = null;
      if (latestIdentityRef.current !== identity) return;
      attemptedListIdentityRef.current = null;
      setRefreshEpoch((current) => current + 1);
    }, delay);
  }, []);

  const beginOperation = useCallback((key: string) => {
    const id = nextOperationIdRef.current + 1;
    nextOperationIdRef.current = id;
    busyOperationRef.current = { id, key };
    setRareGuestInvitationBusyKey(key);
    return id;
  }, []);

  const finishOperation = useCallback((id: number) => {
    if (busyOperationRef.current.id !== id) return;
    busyOperationRef.current = { id, key: '' };
    setRareGuestInvitationBusyKey('');
  }, []);

  const cancelListRequest = useCallback(() => {
    requestGenerationRef.current += 1;
    listAbortControllerRef.current?.abort();
    listAbortControllerRef.current = null;
    activeListIdentityRef.current = null;
    if (busyOperationRef.current.key === 'list') {
      const id = nextOperationIdRef.current + 1;
      nextOperationIdRef.current = id;
      busyOperationRef.current = { id, key: '' };
      setRareGuestInvitationBusyKey('');
    }
  }, []);

  const setRareGuestInvitationScope = useCallback((scope: RareGuestInvitationScope) => {
    setRareGuestInvitationScopeState(scope);
    setRareGuestInvitationResult(null);
    setRareGuestInvitationError('');
  }, []);

  const runInvitationListRead = useCallback(async (identity: string, force: boolean) => {
    if (!apiToken) {
      setRareGuestInvitationResult(null);
      setRareGuestInvitationError('未收到本地 API Token。请从游戏内启动或按 F8 唤起伴随窗口。');
      return;
    }
    if (latestIdentityRef.current !== identity) return;
    if (busyOperationRef.current.key && busyOperationRef.current.key !== 'list') return;
    if (!force
        && (attemptedListIdentityRef.current === identity
          || activeListIdentityRef.current === identity)) {
      return;
    }

    if (force) clearTransientListRetry();
    cancelListRequest();
    const requestGeneration = requestGenerationRef.current + 1;
    requestGenerationRef.current = requestGeneration;
    const abortController = new AbortController();
    listAbortControllerRef.current = abortController;
    activeListIdentityRef.current = identity;
    if (force) attemptedListIdentityRef.current = null;
    const operationId = beginOperation('list');
    setRareGuestInvitationError('');
    try {
      const response = await fetchAvailableRareGuestInvitations(
        normalizedEndpoint,
        apiToken,
        rareGuestInvitationScope,
        abortController.signal,
      );
      if (requestGenerationRef.current !== requestGeneration
          || latestIdentityRef.current !== identity) {
        return;
      }
      if (!response.ok) {
        setRareGuestInvitationResult(null);
        setRareGuestInvitationError(getRareGuestInvitationError(response, '读取稀客邀请候选失败。'));
        if (!response.runtimeAvailable) {
          scheduleTransientListRetry(identity);
        } else {
          clearTransientListRetry();
          attemptedListIdentityRef.current = identity;
        }
        return;
      }

      setRareGuestInvitationResult(response);
      setRareGuestInvitationError('');
      clearTransientListRetry();
      attemptedListIdentityRef.current = identity;
    } catch (err) {
      if (abortController.signal.aborted
          || requestGenerationRef.current !== requestGeneration
          || latestIdentityRef.current !== identity) {
        return;
      }
      setRareGuestInvitationResult(null);
      setRareGuestInvitationError(err instanceof Error ? err.message : String(err));
      scheduleTransientListRetry(identity);
    } finally {
      if (requestGenerationRef.current === requestGeneration) {
        listAbortControllerRef.current = null;
        activeListIdentityRef.current = null;
      }
      finishOperation(operationId);
    }
  }, [
    apiToken,
    beginOperation,
    cancelListRequest,
    clearTransientListRetry,
    finishOperation,
    normalizedEndpoint,
    rareGuestInvitationScope,
    scheduleTransientListRetry,
  ]);

  const loadRareGuestInvitations = useCallback(async () => {
    if (!refreshIdentity) {
      setRareGuestInvitationError('当前日间场景尚未稳定，暂时不能读取稀客邀请候选。');
      return;
    }
    await runInvitationListRead(refreshIdentity, true);
  }, [refreshIdentity, runInvitationListRead]);

  const inviteAllRareGuests = useCallback(async () => {
    if (!apiToken) {
      setRareGuestInvitationError('未收到本地 API Token。请从游戏内启动或按 F8 唤起伴随窗口。');
      return;
    }
    if (!refreshIdentity || !writeContext) {
      setRareGuestInvitationError('当前日间场景尚未稳定，暂时不能邀请稀客。');
      return;
    }

    cancelListRequest();
    clearTransientListRetry();
    attemptedListIdentityRef.current = null;
    const operationId = beginOperation('all');
    setRareGuestInvitationError('');
    try {
      const response = await inviteAllAvailableRareGuests(
        normalizedEndpoint,
        apiToken,
        rareGuestInvitationScope,
        rareGuestInvitationLevels,
        writeContext,
      );
      if (busyOperationRef.current.id !== operationId
          || latestIdentityRef.current !== refreshIdentity) {
        return;
      }
      if (!response.ok) {
        attemptedListIdentityRef.current = refreshIdentity;
        setRareGuestInvitationError(getRareGuestInvitationError(response, '批量邀请稀客失败。'));
        return;
      }

      setRareGuestInvitationResult(response);
      setRareGuestInvitationError('');
      await refresh(true);
      if (busyOperationRef.current.id === operationId
          && latestIdentityRef.current === refreshIdentity) {
        setRefreshEpoch((current) => current + 1);
      }
    } catch (err) {
      if (busyOperationRef.current.id !== operationId
          || latestIdentityRef.current !== refreshIdentity) {
        return;
      }
      attemptedListIdentityRef.current = refreshIdentity;
      setRareGuestInvitationError(err instanceof Error ? err.message : String(err));
    } finally {
      finishOperation(operationId);
    }
  }, [
    apiToken,
    beginOperation,
    cancelListRequest,
    clearTransientListRetry,
    finishOperation,
    normalizedEndpoint,
    rareGuestInvitationLevels,
    rareGuestInvitationScope,
    refresh,
    refreshIdentity,
    writeContext,
  ]);

  const inviteRareGuest = useCallback(async (guestId: number) => {
    if (!apiToken) {
      setRareGuestInvitationError('未收到本地 API Token。请从游戏内启动或按 F8 唤起伴随窗口。');
      return;
    }
    if (!refreshIdentity || !writeContext) {
      setRareGuestInvitationError('当前日间场景尚未稳定，暂时不能邀请稀客。');
      return;
    }

    cancelListRequest();
    clearTransientListRetry();
    attemptedListIdentityRef.current = null;
    const busyKey = `guest:${guestId}`;
    const operationId = beginOperation(busyKey);
    setRareGuestInvitationError('');
    try {
      const response = await inviteAvailableRareGuest(
        normalizedEndpoint,
        apiToken,
        guestId,
        rareGuestInvitationScope,
        writeContext,
      );
      if (busyOperationRef.current.id !== operationId
          || latestIdentityRef.current !== refreshIdentity) {
        return;
      }
      if (!response.ok) {
        attemptedListIdentityRef.current = refreshIdentity;
        setRareGuestInvitationError(getRareGuestInvitationError(response, '邀请稀客失败。'));
        return;
      }

      setRareGuestInvitationResult(response);
      setRareGuestInvitationError('');
      await refresh(true);
      if (busyOperationRef.current.id === operationId
          && latestIdentityRef.current === refreshIdentity) {
        setRefreshEpoch((current) => current + 1);
      }
    } catch (err) {
      if (busyOperationRef.current.id !== operationId
          || latestIdentityRef.current !== refreshIdentity) {
        return;
      }
      attemptedListIdentityRef.current = refreshIdentity;
      setRareGuestInvitationError(err instanceof Error ? err.message : String(err));
    } finally {
      finishOperation(operationId);
    }
  }, [
    apiToken,
    beginOperation,
    cancelListRequest,
    clearTransientListRetry,
    finishOperation,
    normalizedEndpoint,
    rareGuestInvitationScope,
    refresh,
    refreshIdentity,
    writeContext,
  ]);

  useEffect(() => {
    if (visibleIdentityRef.current === refreshIdentity) return;
    visibleIdentityRef.current = refreshIdentity;
    cancelListRequest();
    clearTransientListRetry();
    const id = nextOperationIdRef.current + 1;
    nextOperationIdRef.current = id;
    busyOperationRef.current = { id, key: '' };
    setRareGuestInvitationBusyKey('');
    attemptedListIdentityRef.current = null;
    setRareGuestInvitationResult(null);
    setRareGuestInvitationError('');
  }, [cancelListRequest, clearTransientListRetry, refreshIdentity]);

  useEffect(() => {
    if (!refreshIdentity
        || rareGuestInvitationBusyKey
        || attemptedListIdentityRef.current === refreshIdentity
        || activeListIdentityRef.current === refreshIdentity) {
      return;
    }
    void runInvitationListRead(refreshIdentity, false);
  }, [
    rareGuestInvitationBusyKey,
    refreshEpoch,
    refreshIdentity,
    runInvitationListRead,
  ]);

  useEffect(() => {
    persistRareGuestInvitationScope(rareGuestInvitationScope);
  }, [rareGuestInvitationScope]);

  useEffect(() => {
    persistRareGuestInvitationLevels(rareGuestInvitationLevels);
  }, [rareGuestInvitationLevels]);

  useEffect(() => () => {
    requestGenerationRef.current += 1;
    listAbortControllerRef.current?.abort();
    listAbortControllerRef.current = null;
    if (transientRetryTimerRef.current != null) {
      window.clearTimeout(transientRetryTimerRef.current);
      transientRetryTimerRef.current = null;
    }
    const id = nextOperationIdRef.current + 1;
    nextOperationIdRef.current = id;
    busyOperationRef.current = { id, key: '' };
  }, []);

  return {
    rareGuestInvitationScope,
    setRareGuestInvitationScope,
    rareGuestInvitationLevels,
    setRareGuestInvitationLevels,
    rareGuestInvitationResult,
    rareGuestInvitationError,
    rareGuestInvitationBusyKey,
    rareGuestInvitationContextReady: refreshIdentity !== null,
    loadRareGuestInvitations,
    inviteAllRareGuests,
    inviteRareGuest,
  };
}

function getRareGuestInvitationError(
  response: RareGuestInvitationResponse,
  fallback: string,
): string {
  return response.error?.trim() || response.status?.trim() || fallback;
}
