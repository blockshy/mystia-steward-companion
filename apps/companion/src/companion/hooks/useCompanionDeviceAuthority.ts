import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  acknowledgeCompanionDeviceSync,
  forgetCompanionDevice,
  readCompanionDevices,
  registerCompanionDevice,
  renameCompanionDevice,
  setPrimaryCompanionDevice,
  syncCompanionDeviceProfile,
  updatePrimaryCompanionProfile,
} from '@/companion/api';
import {
  SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION,
  normalizeSharedCompanionPreferences,
  serializeSharedCompanionPreferences,
  type SharedCompanionPreferences,
} from '@/companion/preferences';
import type {
  CompanionDeviceAuthorityState,
  CompanionDevicePlatform,
} from '@/companion/types';

const DEVICE_PROTOCOL_VERSION = 1;
const DEVICE_POLL_INTERVAL_MS = 5000;
const PROFILE_WRITE_DEBOUNCE_MS = 300;

export type CompanionDeviceAuthorityBusy =
  | 'register'
  | 'profile'
  | 'refresh'
  | 'primary'
  | 'sync'
  | 'rename'
  | 'forget'
  | null;

interface UseCompanionDeviceAuthorityOptions {
  endpoint: string;
  apiToken: string;
  connected: boolean;
  connectionRevision: number;
  platform: CompanionDevicePlatform;
  appVersion: string;
  sharedPreferences: SharedCompanionPreferences;
  applySharedPreferences: (profile: SharedCompanionPreferences) => void;
}

export interface CompanionDeviceAuthorityController {
  state: CompanionDeviceAuthorityState | null;
  ready: boolean;
  runtimeWriterReady: boolean;
  currentDeviceIsPrimary: boolean;
  authorityRevision: number;
  busy: CompanionDeviceAuthorityBusy;
  error: string;
  refresh: () => Promise<void>;
  setPrimary: (deviceId: string) => Promise<void>;
  syncFromPrimary: (deviceId: string) => Promise<void>;
  renameCurrent: (label: string) => Promise<void>;
  forget: (deviceId: string) => Promise<void>;
}

export function useCompanionDeviceAuthority({
  endpoint,
  apiToken,
  connected,
  connectionRevision,
  platform,
  appVersion,
  sharedPreferences,
  applySharedPreferences,
}: UseCompanionDeviceAuthorityOptions): CompanionDeviceAuthorityController {
  const [state, setState] = useState<CompanionDeviceAuthorityState | null>(null);
  const [busy, setBusy] = useState<CompanionDeviceAuthorityBusy>(null);
  const [error, setError] = useState('');
  const generationRef = useRef(0);
  const activeConnectionKeyRef = useRef('');
  const stateRef = useRef<CompanionDeviceAuthorityState | null>(null);
  const sharedPreferencesRef = useRef(sharedPreferences);
  const applySharedPreferencesRef = useRef(applySharedPreferences);
  const profileWriteRef = useRef<Promise<void> | null>(null);
  const connectionKey = `${connectionRevision}\n${endpoint}\n${apiToken}`;
  const sharedSignature = useMemo(
    () => serializeSharedCompanionPreferences(sharedPreferences),
    [sharedPreferences],
  );

  sharedPreferencesRef.current = sharedPreferences;
  applySharedPreferencesRef.current = applySharedPreferences;

  const commitState = useCallback((next: CompanionDeviceAuthorityState, generation: number): boolean => {
    if (generationRef.current !== generation) return false;
    validateAuthorityState(next);
    const activeProfile = normalizeSharedCompanionPreferences(next.activeProfile);
    const currentDeviceProfile = normalizeSharedCompanionPreferences(next.currentDeviceProfile);
    const normalizedState: CompanionDeviceAuthorityState = {
      ...next,
      activeProfile,
      currentDeviceProfile,
    };
    applySharedPreferencesRef.current(activeProfile);
    stateRef.current = normalizedState;
    setState(normalizedState);
    setError('');
    return true;
  }, []);

  const applyPendingSync = useCallback(async (
    next: CompanionDeviceAuthorityState,
    generation: number,
  ): Promise<CompanionDeviceAuthorityState> => {
    if (!next.pendingSyncId) return next;
    applySharedPreferencesRef.current(normalizeSharedCompanionPreferences(next.currentDeviceProfile));
    const acknowledged = await acknowledgeCompanionDeviceSync(
      endpoint,
      apiToken,
      next.pendingSyncId,
      next.currentDeviceProfileRevision,
      next.currentDeviceProfileHash,
    );
    if (generationRef.current !== generation) return next;
    return acknowledged;
  }, [apiToken, endpoint]);

  useEffect(() => {
    const generation = generationRef.current + 1;
    generationRef.current = generation;
    const connectionIdentityChanged = activeConnectionKeyRef.current !== connectionKey;
    activeConnectionKeyRef.current = connectionKey;
    if (connectionIdentityChanged) {
      stateRef.current = null;
      setState(null);
    }
    setError('');
    setBusy(connected && apiToken ? 'register' : null);
    if (!connected || !apiToken) return undefined;

    let disposed = false;
    let pollTimer: number | null = null;
    const schedulePoll = () => {
      if (disposed || generationRef.current !== generation) return;
      pollTimer = window.setTimeout(() => {
        void poll();
      }, DEVICE_POLL_INTERVAL_MS);
    };
    const acceptState = async (next: CompanionDeviceAuthorityState) => {
      const acknowledged = await applyPendingSync(next, generation);
      if (!disposed) commitState(acknowledged, generation);
    };
    const poll = async () => {
      try {
        const next = await readCompanionDevices(endpoint, apiToken);
        await acceptState(next);
      } catch (cause) {
        if (!disposed && generationRef.current === generation) {
          setError(formatAuthorityError(cause));
        }
      } finally {
        schedulePoll();
      }
    };

    void registerCompanionDevice(
      endpoint,
      apiToken,
      platform,
      appVersion || 'unknown',
      sharedPreferencesRef.current,
    )
      .then(acceptState)
      .catch((cause) => {
        if (!disposed && generationRef.current === generation) {
          setError(formatAuthorityError(cause));
        }
      })
      .finally(() => {
        if (!disposed && generationRef.current === generation) {
          setBusy(null);
          schedulePoll();
        }
      });

    return () => {
      disposed = true;
      if (pollTimer !== null) window.clearTimeout(pollTimer);
    };
  }, [
    apiToken,
    appVersion,
    applyPendingSync,
    commitState,
    connected,
    connectionKey,
    endpoint,
    platform,
  ]);

  useEffect(() => {
    const current = stateRef.current;
    if (!current?.currentDeviceIsPrimary) return undefined;
    const activeSignature = serializeSharedCompanionPreferences(current.activeProfile);
    if (activeSignature === sharedSignature) return undefined;

    const generation = generationRef.current;
    const timer = window.setTimeout(() => {
      const write = async () => {
        const latest = stateRef.current;
        if (!latest?.currentDeviceIsPrimary || generationRef.current !== generation) return;
        setBusy('profile');
        try {
          const next = await updatePrimaryCompanionProfile(
            endpoint,
            apiToken,
            latest,
            sharedPreferencesRef.current,
          );
          commitState(next, generation);
        } catch (cause) {
          if (generationRef.current !== generation) return;
          setError(formatAuthorityError(cause));
          try {
            const fresh = await readCompanionDevices(endpoint, apiToken);
            commitState(fresh, generation);
          } catch {
            applySharedPreferencesRef.current(latest.activeProfile);
          }
        } finally {
          if (generationRef.current === generation) setBusy(null);
        }
      };
      const previous = profileWriteRef.current;
      const next = previous ? previous.catch(() => undefined).then(write) : write();
      const tracked = next.finally(() => {
        if (profileWriteRef.current === tracked) profileWriteRef.current = null;
      });
      profileWriteRef.current = tracked;
    }, PROFILE_WRITE_DEBOUNCE_MS);
    return () => window.clearTimeout(timer);
  }, [apiToken, commitState, endpoint, sharedSignature]);

  const runMutation = useCallback(async (
    kind: Exclude<CompanionDeviceAuthorityBusy, 'register' | 'profile' | 'refresh' | null>,
    mutation: (current: CompanionDeviceAuthorityState) => Promise<CompanionDeviceAuthorityState>,
  ) => {
    const generation = generationRef.current;
    const current = stateRef.current;
    if (!current) throw new Error('设备权威状态尚未就绪。');
    setBusy(kind);
    setError('');
    try {
      const next = await mutation(current);
      commitState(next, generation);
    } catch (cause) {
      const message = formatAuthorityError(cause);
      if (generationRef.current === generation) setError(message);
      throw new Error(message);
    } finally {
      if (generationRef.current === generation) setBusy(null);
    }
  }, [commitState]);

  const refresh = useCallback(async () => {
    const generation = generationRef.current;
    if (!apiToken) return;
    setBusy('refresh');
    try {
      const next = await readCompanionDevices(endpoint, apiToken);
      const acknowledged = await applyPendingSync(next, generation);
      commitState(acknowledged, generation);
    } catch (cause) {
      if (generationRef.current === generation) setError(formatAuthorityError(cause));
    } finally {
      if (generationRef.current === generation) setBusy(null);
    }
  }, [apiToken, applyPendingSync, commitState, endpoint]);

  const setPrimary = useCallback((deviceId: string) => runMutation(
    'primary',
    (current) => setPrimaryCompanionDevice(endpoint, apiToken, current.authorityRevision, deviceId),
  ), [apiToken, endpoint, runMutation]);
  const syncFromPrimary = useCallback((deviceId: string) => runMutation(
    'sync',
    (current) => syncCompanionDeviceProfile(endpoint, apiToken, current.authorityRevision, deviceId),
  ), [apiToken, endpoint, runMutation]);
  const renameCurrent = useCallback((label: string) => runMutation(
    'rename',
    () => renameCompanionDevice(endpoint, apiToken, label),
  ), [apiToken, endpoint, runMutation]);
  const forget = useCallback((deviceId: string) => runMutation(
    'forget',
    (current) => forgetCompanionDevice(endpoint, apiToken, current.authorityRevision, deviceId),
  ), [apiToken, endpoint, runMutation]);

  const ready = Boolean(connected && state && !error);
  const profileDirty = Boolean(
    state?.currentDeviceIsPrimary
    && serializeSharedCompanionPreferences(state.activeProfile) !== sharedSignature,
  );
  return {
    state,
    ready,
    runtimeWriterReady: Boolean(ready && state?.currentDeviceIsPrimary && !profileDirty && busy !== 'profile'),
    currentDeviceIsPrimary: Boolean(state?.currentDeviceIsPrimary),
    authorityRevision: state?.authorityRevision ?? 0,
    busy,
    error,
    refresh,
    setPrimary,
    syncFromPrimary,
    renameCurrent,
    forget,
  };
}

function validateAuthorityState(state: CompanionDeviceAuthorityState): void {
  if (!state.ok
    || state.protocolVersion !== DEVICE_PROTOCOL_VERSION
    || state.profileSchemaVersion !== SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION
    || state.authorityRevision <= 0
    || state.currentDeviceId.length < 16
    || !state.devices.some((device) => device.isCurrent && device.deviceId === state.currentDeviceId)
    || !state.devices.some((device) => device.isPrimary && device.deviceId === state.primaryDeviceId)) {
    throw new Error('Mod 返回的设备权威状态与当前伴随窗口协议不一致。');
  }
}

function formatAuthorityError(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
