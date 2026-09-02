import { useCallback, useEffect, useRef, useState } from 'react';
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
  parseSharedCompanionPreferences,
  serializeSharedCompanionPreferences,
  type SharedCompanionPreferences,
} from '@/companion/preferences';
import {
  capturePrimaryProfileTransactionBase,
  resolvePrimaryProfileObservation,
  type PrimaryProfileTransactionBase,
} from '@/companion/domain/primary-profile-transaction';
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

export type PrimaryProfileTransactionPhase = 'debouncing' | 'posting' | 'reconciling';

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
  profileSynchronized: boolean;
  profileUpdatePending: boolean;
  profileTransactionPhase: PrimaryProfileTransactionPhase | null;
  profileDraft: SharedCompanionPreferences | null;
  profileEditWritable: boolean;
  runtimeWriterReady: boolean;
  currentDeviceIsPrimary: boolean;
  authorityRevision: number;
  busy: CompanionDeviceAuthorityBusy;
  error: string;
  stagePrimaryProfile: (profile: Partial<SharedCompanionPreferences>) => boolean;
  refresh: () => Promise<void>;
  setPrimary: (deviceId: string) => Promise<void>;
  syncFromPrimary: (deviceId: string) => Promise<void>;
  renameCurrent: (label: string) => Promise<void>;
  forget: (deviceId: string) => Promise<void>;
}

interface PrimaryProfileTransaction {
  id: number;
  phase: PrimaryProfileTransactionPhase;
  connectionGeneration: number;
  base: PrimaryProfileTransactionBase;
  confirmedProfile: SharedCompanionPreferences;
  desiredProfile: SharedCompanionPreferences;
  desiredSignature: string;
  submittedSignature?: string;
}

interface AuthorityWriteOutcome {
  id: number;
  generation: number;
  settled: Promise<void>;
  settle: () => void;
}

interface AuthorityOperation {
  id: number;
  generation: number;
  kind: Exclude<CompanionDeviceAuthorityBusy, 'register' | 'profile' | null>;
}

interface PendingSyncApplication {
  id: number;
  generation: number;
  key: string;
  result: Promise<boolean>;
}

type AuthorityObservationSource =
  | 'register'
  | 'poll'
  | 'refresh'
  | 'profile-post'
  | 'profile-reconcile'
  | 'mutation'
  | 'sync-ack';

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
  const [profileTransactionPhase, setProfileTransactionPhase] =
    useState<PrimaryProfileTransactionPhase | null>(null);
  const [profileDraft, setProfileDraft] = useState<SharedCompanionPreferences | null>(null);
  const [pendingSyncApplying, setPendingSyncApplying] = useState(false);
  const generationRef = useRef(0);
  const mountedRef = useRef(true);
  const connectionActiveRef = useRef(false);
  const renderConnectionKeyRef = useRef('');
  const generationConnectionKeyRef = useRef('');
  const stateRef = useRef<CompanionDeviceAuthorityState | null>(null);
  const busyRef = useRef<CompanionDeviceAuthorityBusy>(null);
  const errorRef = useRef('');
  const sharedPreferencesRef = useRef(sharedPreferences);
  const applySharedPreferencesRef = useRef(applySharedPreferences);
  const profileTransactionRef = useRef<PrimaryProfileTransaction | null>(null);
  const profileTransactionIdRef = useRef(0);
  const profileDebounceTimerRef = useRef<number | null>(null);
  const scheduleProfilePostRef = useRef<(transactionId: number) => void>(() => undefined);
  const authorityWriteOutcomeRef = useRef<AuthorityWriteOutcome | null>(null);
  const authorityWriteOutcomeIdRef = useRef(0);
  const authorityOperationRef = useRef<AuthorityOperation | null>(null);
  const authorityOperationIdRef = useRef(0);
  const pendingSyncApplicationRef = useRef<PendingSyncApplication | null>(null);
  const pendingSyncApplicationIdRef = useRef(0);
  const connectionKey = `${connectionRevision}\n${endpoint}\n${apiToken}`;

  connectionActiveRef.current = connected && apiToken !== '';
  renderConnectionKeyRef.current = connectionKey;
  sharedPreferencesRef.current = sharedPreferences;
  applySharedPreferencesRef.current = applySharedPreferences;

  const isAuthorityGenerationCurrent = useCallback((generation: number): boolean => (
    mountedRef.current
    && connectionActiveRef.current
    && generationRef.current === generation
    && generationConnectionKeyRef.current === renderConnectionKeyRef.current
  ), []);

  const publishBusy = useCallback((next: CompanionDeviceAuthorityBusy) => {
    busyRef.current = next;
    setBusy(next);
  }, []);

  const publishError = useCallback((next: string) => {
    errorRef.current = next;
    setError(next);
  }, []);

  const beginAuthorityWriteOutcome = useCallback((generation: number) => {
    if (!isAuthorityGenerationCurrent(generation) || authorityWriteOutcomeRef.current) {
      return null;
    }
    let settle: () => void = () => undefined;
    const settled = new Promise<void>((resolve) => {
      settle = resolve;
    });
    const outcome: AuthorityWriteOutcome = {
      id: authorityWriteOutcomeIdRef.current + 1,
      generation,
      settled,
      settle,
    };
    authorityWriteOutcomeIdRef.current = outcome.id;
    authorityWriteOutcomeRef.current = outcome;
    return outcome;
  }, [isAuthorityGenerationCurrent]);

  const finishAuthorityWriteOutcome = useCallback((outcome: AuthorityWriteOutcome) => {
    outcome.settle();
    if (authorityWriteOutcomeRef.current?.id === outcome.id) {
      authorityWriteOutcomeRef.current = null;
    }
  }, []);

  const waitForAuthorityWriteOutcomes = useCallback(async () => {
    while (authorityWriteOutcomeRef.current) {
      await authorityWriteOutcomeRef.current.settled;
    }
  }, []);

  const acquireAuthorityOperation = useCallback((
    kind: AuthorityOperation['kind'],
    requireReadyState: boolean,
  ): AuthorityOperation | null => {
    const generation = generationRef.current;
    const current = stateRef.current;
    if (!isAuthorityGenerationCurrent(generation)
      || authorityOperationRef.current
      || authorityWriteOutcomeRef.current
      || pendingSyncApplicationRef.current
      || profileTransactionRef.current
      || busyRef.current !== null
      || (requireReadyState && (!current || errorRef.current !== ''))) {
      return null;
    }
    const operation: AuthorityOperation = {
      id: authorityOperationIdRef.current + 1,
      generation,
      kind,
    };
    authorityOperationIdRef.current = operation.id;
    authorityOperationRef.current = operation;
    publishBusy(kind);
    return operation;
  }, [isAuthorityGenerationCurrent, publishBusy]);

  const releaseAuthorityOperation = useCallback((operation: AuthorityOperation) => {
    if (authorityOperationRef.current?.id !== operation.id) return;
    authorityOperationRef.current = null;
    if (mountedRef.current && busyRef.current === operation.kind) publishBusy(null);
  }, [publishBusy]);

  const clearProfileDebounceTimer = useCallback(() => {
    if (profileDebounceTimerRef.current === null) return;
    window.clearTimeout(profileDebounceTimerRef.current);
    profileDebounceTimerRef.current = null;
  }, []);

  const publishProfileTransaction = useCallback((transaction: PrimaryProfileTransaction | null) => {
    profileTransactionRef.current = transaction;
    setProfileTransactionPhase(transaction?.phase ?? null);
    setProfileDraft(transaction?.desiredProfile ?? null);
  }, []);

  const clearProfileTransaction = useCallback(() => {
    clearProfileDebounceTimer();
    publishProfileTransaction(null);
    if (busyRef.current === 'profile') publishBusy(null);
  }, [clearProfileDebounceTimer, publishBusy, publishProfileTransaction]);

  const commitAuthorityObservation = useCallback((
    parsedState: CompanionDeviceAuthorityState,
    generation: number,
    source: AuthorityObservationSource,
    expectedTransactionId?: number,
  ): boolean => {
    if (!isAuthorityGenerationCurrent(generation)) return false;
    const transaction = profileTransactionRef.current;
    if (expectedTransactionId !== undefined
      && transaction?.id !== expectedTransactionId) return false;

    if (transaction) {
      if (transaction.connectionGeneration !== generation) return false;
      const submittedOrDesiredSignature = transaction.submittedSignature
        ?? transaction.desiredSignature;
      let decision = resolvePrimaryProfileObservation(
        transaction.base,
        submittedOrDesiredSignature,
        parsedState,
      );
      if (parsedState.pendingSyncId && decision.action === 'retain-draft') {
        decision = { action: 'rollback-draft', reason: 'authority-changed' };
      }
      if (decision.action === 'retain-draft'
        && (source === 'profile-post'
          || source === 'profile-reconcile'
          || transaction.phase === 'reconciling')) {
        decision = { action: 'rollback-draft', reason: 'profile-conflict' };
      }

      if (decision.action === 'retain-draft') {
        stateRef.current = parsedState;
        setState(parsedState);
        publishError('');
        return true;
      }

      if (decision.action === 'confirm-draft'
        && transaction.desiredSignature !== submittedOrDesiredSignature) {
        const nextTransaction: PrimaryProfileTransaction = {
          ...transaction,
          id: profileTransactionIdRef.current + 1,
          phase: 'debouncing',
          base: capturePrimaryProfileTransactionBase(parsedState),
          confirmedProfile: parsedState.activeProfile,
          submittedSignature: undefined,
        };
        profileTransactionIdRef.current = nextTransaction.id;
        sharedPreferencesRef.current = parsedState.activeProfile;
        applySharedPreferencesRef.current(parsedState.activeProfile);
        stateRef.current = parsedState;
        setState(parsedState);
        publishError('');
        publishProfileTransaction(nextTransaction);
        scheduleProfilePostRef.current(nextTransaction.id);
        return true;
      }

      clearProfileTransaction();
      sharedPreferencesRef.current = parsedState.activeProfile;
      applySharedPreferencesRef.current(parsedState.activeProfile);
      stateRef.current = parsedState;
      setState(parsedState);
      if (decision.action === 'confirm-draft') {
        publishError('');
      } else {
        publishError(decision.reason === 'authority-changed'
          ? '共享配置修改已取消：主设备配置在保存期间发生变化，已恢复当前生效配置。'
          : '共享配置修改未能基于保存前的配置版本完成，已恢复 Mod 返回的生效配置。');
      }
      return true;
    }

    const current = stateRef.current;
    if (current
      && current.registryId === parsedState.registryId
      && current.currentDeviceId === parsedState.currentDeviceId
      && (parsedState.stateRevision < current.stateRevision
        || parsedState.authorityRevision < current.authorityRevision)) {
      return false;
    }
    sharedPreferencesRef.current = parsedState.activeProfile;
    applySharedPreferencesRef.current(parsedState.activeProfile);
    stateRef.current = parsedState;
    setState(parsedState);
    publishError('');
    return true;
  }, [
    clearProfileTransaction,
    isAuthorityGenerationCurrent,
    publishError,
    publishProfileTransaction,
  ]);

  const acceptAuthorityState = useCallback(async (
    next: CompanionDeviceAuthorityState,
    generation: number,
    source: AuthorityObservationSource,
    isActive: () => boolean,
    expectedTransactionId?: number,
  ): Promise<boolean> => {
    if (!isActive() || !isAuthorityGenerationCurrent(generation)) return false;
    const parsedState = parseAuthorityState(next);
    if (!isActive() || !isAuthorityGenerationCurrent(generation)) return false;
    if (expectedTransactionId !== undefined
      && profileTransactionRef.current?.id !== expectedTransactionId) return false;

    const runningSync = pendingSyncApplicationRef.current;
    if (runningSync?.generation === generation) {
      if (parsedState.pendingSyncId
        && buildPendingSyncKey(
          generation,
          parsedState,
          parsedState.pendingSyncId,
        ) === runningSync.key) {
        return runningSync.result;
      }
      return false;
    }

    if (parsedState.pendingSyncId) {
      const pendingSyncId = parsedState.pendingSyncId;
      if (profileTransactionRef.current) {
        if (!commitAuthorityObservation(
          parsedState,
          generation,
          source,
          expectedTransactionId,
        )) return false;
      }
      if (!isActive() || !isAuthorityGenerationCurrent(generation)) return false;
      const current = stateRef.current;
      if (isOlderAuthorityObservation(current, parsedState)) return false;

      const applicationId = pendingSyncApplicationIdRef.current + 1;
      pendingSyncApplicationIdRef.current = applicationId;
      let resolveResult!: (result: boolean) => void;
      let rejectResult!: (cause: unknown) => void;
      const result = new Promise<boolean>((resolve, reject) => {
        resolveResult = resolve;
        rejectResult = reject;
      });
      const application: PendingSyncApplication = {
        id: applicationId,
        generation,
        key: buildPendingSyncKey(generation, parsedState, pendingSyncId),
        result,
      };
      const priorWriteOutcome = authorityWriteOutcomeRef.current?.settled;
      pendingSyncApplicationRef.current = application;
      setPendingSyncApplying(true);
      stateRef.current = parsedState;
      setState(parsedState);
      publishError('');

      void (async () => {
        try {
          if (priorWriteOutcome) await priorWriteOutcome;
          if (!isActive()
            || !isAuthorityGenerationCurrent(generation)
            || pendingSyncApplicationRef.current?.id !== application.id) {
            resolveResult(false);
            return;
          }

          sharedPreferencesRef.current = parsedState.currentDeviceProfile;
          applySharedPreferencesRef.current(parsedState.currentDeviceProfile);
          const writeOutcome = beginAuthorityWriteOutcome(generation);
          if (!writeOutcome) throw new Error('设备同步确认正在等待其他共享配置保存完成。');
          let acknowledged: CompanionDeviceAuthorityState;
          try {
            acknowledged = await acknowledgeCompanionDeviceSync(
              endpoint,
              apiToken,
              pendingSyncId,
              parsedState.currentDeviceProfileRevision,
              parsedState.currentDeviceProfileHash,
            );
          } finally {
            finishAuthorityWriteOutcome(writeOutcome);
          }
          if (!isActive()
            || !isAuthorityGenerationCurrent(generation)
            || pendingSyncApplicationRef.current?.id !== application.id) {
            resolveResult(false);
            return;
          }
          const acknowledgedState = parseAuthorityState(acknowledged);
          resolveResult(commitAuthorityObservation(
            acknowledgedState,
            generation,
            'sync-ack',
          ));
        } catch (cause) {
          rejectResult(cause);
        } finally {
          if (pendingSyncApplicationRef.current?.id === application.id) {
            pendingSyncApplicationRef.current = null;
            if (mountedRef.current) setPendingSyncApplying(false);
          }
        }
      })();
      return result;
    }

    return commitAuthorityObservation(
      parsedState,
      generation,
      source,
      expectedTransactionId,
    );
  }, [
    apiToken,
    beginAuthorityWriteOutcome,
    commitAuthorityObservation,
    endpoint,
    finishAuthorityWriteOutcome,
    isAuthorityGenerationCurrent,
    publishError,
  ]);

  const beginProfilePost = useCallback(async (transactionId: number) => {
    profileDebounceTimerRef.current = null;
    const transaction = profileTransactionRef.current;
    if (!isAuthorityGenerationCurrent(transaction?.connectionGeneration ?? -1)
      || !transaction
      || transaction.id !== transactionId
      || transaction.phase !== 'debouncing'
    ) return;

    const current = stateRef.current;
    if (!current
      || resolvePrimaryProfileObservation(
        transaction.base,
        transaction.desiredSignature,
        current,
      ).action !== 'retain-draft') {
      if (current) {
        commitAuthorityObservation(current, transaction.connectionGeneration, 'profile-reconcile');
      }
      return;
    }

    const postingTransaction: PrimaryProfileTransaction = {
      ...transaction,
      phase: 'posting',
      submittedSignature: transaction.desiredSignature,
    };
    const writeOutcome = beginAuthorityWriteOutcome(transaction.connectionGeneration);
    if (!writeOutcome) {
      publishProfileTransaction({ ...transaction, phase: 'reconciling' });
      publishError('另一台设备正在保存共享配置，请刷新后重试。');
      return;
    }
    publishProfileTransaction(postingTransaction);
    let next: CompanionDeviceAuthorityState | null = null;
    let writeFailure: unknown = null;
    try {
      next = await updatePrimaryCompanionProfile(
        endpoint,
        apiToken,
        transaction.base.authorityRevision,
        transaction.base.currentDeviceProfileRevision,
        transaction.desiredProfile,
      );
    } catch (cause) {
      writeFailure = cause;
    } finally {
      finishAuthorityWriteOutcome(writeOutcome);
    }

    if (next) {
      try {
        await acceptAuthorityState(
          next,
          transaction.connectionGeneration,
          'profile-post',
          () => mountedRef.current,
          transaction.id,
        );
        return;
      } catch (cause) {
        writeFailure = cause;
      }
    }

    const activeTransaction = profileTransactionRef.current;
    if (!isAuthorityGenerationCurrent(transaction.connectionGeneration)
      || activeTransaction?.id !== transaction.id) return;
    publishProfileTransaction({ ...activeTransaction, phase: 'reconciling' });
    const writeError = formatAuthorityError(writeFailure);
    publishError(`${writeError}正在重新确认 Mod 中的生效配置。`);
    try {
      const fresh = await readCompanionDevices(endpoint, apiToken);
      await acceptAuthorityState(
        fresh,
        transaction.connectionGeneration,
        'profile-reconcile',
        () => mountedRef.current,
        transaction.id,
      );
    } catch (refreshCause) {
      if (isAuthorityGenerationCurrent(transaction.connectionGeneration)
        && profileTransactionRef.current?.id === transaction.id) {
        publishError(`${writeError}重新读取生效配置也失败：${formatAuthorityError(refreshCause)}`);
      }
    }
  }, [
    acceptAuthorityState,
    apiToken,
    beginAuthorityWriteOutcome,
    commitAuthorityObservation,
    endpoint,
    finishAuthorityWriteOutcome,
    isAuthorityGenerationCurrent,
    publishError,
    publishProfileTransaction,
  ]);

  const scheduleProfilePost = useCallback((transactionId: number) => {
    clearProfileDebounceTimer();
    profileDebounceTimerRef.current = window.setTimeout(() => {
      void beginProfilePost(transactionId);
    }, PROFILE_WRITE_DEBOUNCE_MS);
  }, [beginProfilePost, clearProfileDebounceTimer]);
  scheduleProfilePostRef.current = scheduleProfilePost;

  const stagePrimaryProfile = useCallback((profile: Partial<SharedCompanionPreferences>): boolean => {
    const existing = profileTransactionRef.current;
    if (existing && (!isAuthorityGenerationCurrent(existing.connectionGeneration)
      || !['debouncing', 'posting'].includes(existing.phase)
      || existing.connectionGeneration !== generationRef.current)) return false;
    const current = stateRef.current;
    const baseProfile = existing?.desiredProfile ?? current?.activeProfile;
    if (!baseProfile) return false;
    let desiredProfile: SharedCompanionPreferences;
    try {
      desiredProfile = parseSharedCompanionPreferences(
        normalizeSharedCompanionPreferences({ ...baseProfile, ...profile }),
      );
    } catch (cause) {
      publishError(formatAuthorityError(cause));
      return false;
    }
    const desiredSignature = serializeSharedCompanionPreferences(desiredProfile);
    if (existing) {
      if (desiredSignature === existing.base.activeProfileSignature
        && existing.phase === 'debouncing') {
        clearProfileTransaction();
        publishError('');
        return true;
      }
      const updated: PrimaryProfileTransaction = {
        ...existing,
        desiredProfile,
        desiredSignature,
      };
      publishProfileTransaction(updated);
      if (updated.phase === 'debouncing') scheduleProfilePost(updated.id);
      return true;
    }

    const generation = generationRef.current;
    if (!isAuthorityGenerationCurrent(generation)
      || !apiToken
      || !current
      || !current.currentDeviceIsPrimary
      || authorityOperationRef.current
      || authorityWriteOutcomeRef.current
      || pendingSyncApplicationRef.current
      || busyRef.current !== null
      || errorRef.current !== '') return false;
    try {
      const base = capturePrimaryProfileTransactionBase(current);
      if (desiredSignature === base.activeProfileSignature) return true;
      const transaction: PrimaryProfileTransaction = {
        id: profileTransactionIdRef.current + 1,
        phase: 'debouncing',
        connectionGeneration: generation,
        base,
        confirmedProfile: current.activeProfile,
        desiredProfile,
        desiredSignature,
      };
      profileTransactionIdRef.current = transaction.id;
      publishProfileTransaction(transaction);
      publishBusy('profile');
      publishError('');
      scheduleProfilePost(transaction.id);
      return true;
    } catch (cause) {
      publishError(formatAuthorityError(cause));
      return false;
    }
  }, [
    apiToken,
    clearProfileTransaction,
    isAuthorityGenerationCurrent,
    publishBusy,
    publishError,
    publishProfileTransaction,
    scheduleProfilePost,
  ]);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      generationRef.current += 1;
      clearProfileDebounceTimer();
    };
  }, [clearProfileDebounceTimer]);

  useEffect(() => {
    const generation = generationRef.current + 1;
    generationRef.current = generation;
    generationConnectionKeyRef.current = connectionKey;
    authorityOperationRef.current = null;
    const pendingTransaction = profileTransactionRef.current;
    if (pendingTransaction) {
      clearProfileDebounceTimer();
      sharedPreferencesRef.current = pendingTransaction.confirmedProfile;
      applySharedPreferencesRef.current(pendingTransaction.confirmedProfile);
      publishProfileTransaction(null);
    }
    stateRef.current = null;
    setState(null);
    publishError('');
    publishBusy(connected && apiToken ? 'register' : null);
    if (!connected || !apiToken) return undefined;

    let disposed = false;
    let pollTimer: number | null = null;
    const schedulePoll = () => {
      if (disposed || !isAuthorityGenerationCurrent(generation)) return;
      pollTimer = window.setTimeout(() => {
        void poll();
      }, DEVICE_POLL_INTERVAL_MS);
    };
    const acceptState = async (next: CompanionDeviceAuthorityState) => {
      await acceptAuthorityState(next, generation, 'poll', () => !disposed);
    };
    const poll = async () => {
      try {
        const next = await readCompanionDevices(endpoint, apiToken);
        await acceptState(next);
      } catch (cause) {
        if (!disposed && isAuthorityGenerationCurrent(generation)) {
          publishError(formatAuthorityError(cause));
        }
      } finally {
        schedulePoll();
      }
    };

    void (async () => {
      try {
        await waitForAuthorityWriteOutcomes();
        if (disposed || !isAuthorityGenerationCurrent(generation)) return;
        const next = await registerCompanionDevice(
          endpoint,
          apiToken,
          platform,
          appVersion || 'unknown',
          sharedPreferencesRef.current,
        );
        await acceptAuthorityState(next, generation, 'register', () => !disposed);
      } catch (cause) {
        if (!disposed && isAuthorityGenerationCurrent(generation)) {
          publishError(formatAuthorityError(cause));
        }
      } finally {
        if (!disposed && isAuthorityGenerationCurrent(generation)) {
          if (busyRef.current === 'register') publishBusy(null);
          schedulePoll();
        }
      }
    })();

    return () => {
      disposed = true;
      if (pollTimer !== null) window.clearTimeout(pollTimer);
    };
  }, [
    apiToken,
    appVersion,
    acceptAuthorityState,
    clearProfileDebounceTimer,
    connected,
    connectionKey,
    endpoint,
    isAuthorityGenerationCurrent,
    platform,
    publishBusy,
    publishError,
    publishProfileTransaction,
    waitForAuthorityWriteOutcomes,
  ]);

  const runMutation = useCallback(async (
    kind: Exclude<CompanionDeviceAuthorityBusy, 'register' | 'profile' | 'refresh' | null>,
    mutation: (current: CompanionDeviceAuthorityState) => Promise<CompanionDeviceAuthorityState>,
  ) => {
    const operation = acquireAuthorityOperation(kind, true);
    if (!operation) throw new Error('主设备状态尚未就绪。');
    const generation = operation.generation;
    const current = stateRef.current;
    if (!current) {
      releaseAuthorityOperation(operation);
      throw new Error('主设备状态尚未就绪。');
    }
    publishError('');
    try {
      const writeOutcome = beginAuthorityWriteOutcome(generation);
      if (!writeOutcome) throw new Error('共享配置正在保存，请稍后重试。');
      let next: CompanionDeviceAuthorityState;
      try {
        next = await mutation(current);
      } finally {
        finishAuthorityWriteOutcome(writeOutcome);
      }
      await acceptAuthorityState(next, generation, 'mutation', () => mountedRef.current);
    } catch (cause) {
      const message = formatAuthorityError(cause);
      if (isAuthorityGenerationCurrent(generation)) publishError(message);
      throw new Error(message);
    } finally {
      releaseAuthorityOperation(operation);
    }
  }, [
    acceptAuthorityState,
    acquireAuthorityOperation,
    beginAuthorityWriteOutcome,
    finishAuthorityWriteOutcome,
    isAuthorityGenerationCurrent,
    publishError,
    releaseAuthorityOperation,
  ]);

  const refresh = useCallback(async () => {
    if (!apiToken) return;
    const operation = acquireAuthorityOperation('refresh', false);
    if (!operation) return;
    const generation = operation.generation;
    try {
      const next = await readCompanionDevices(endpoint, apiToken);
      await acceptAuthorityState(next, generation, 'refresh', () => mountedRef.current);
    } catch (cause) {
      if (isAuthorityGenerationCurrent(generation)) publishError(formatAuthorityError(cause));
    } finally {
      releaseAuthorityOperation(operation);
    }
  }, [
    acceptAuthorityState,
    acquireAuthorityOperation,
    apiToken,
    endpoint,
    isAuthorityGenerationCurrent,
    publishError,
    releaseAuthorityOperation,
  ]);

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

  const ready = Boolean(
    connected
    && apiToken
    && state
    && !state.pendingSyncId
    && !pendingSyncApplying
    && !error
    && busy !== 'register'
  );
  const currentDeviceIsPrimary = Boolean(ready && state?.currentDeviceIsPrimary);
  const profileUpdatePending = profileTransactionPhase !== null;
  const profileSynchronized = Boolean(ready && !profileUpdatePending);
  const profileEditWritable = Boolean(
    currentDeviceIsPrimary
    && !pendingSyncApplying
    && (profileTransactionPhase === 'debouncing'
      || profileTransactionPhase === 'posting'
      || (profileTransactionPhase === null && busy === null)),
  );
  const runtimeAuthorityMutationInFlight = busy !== null && busy !== 'refresh';
  return {
    state,
    ready,
    profileSynchronized,
    profileUpdatePending,
    profileTransactionPhase,
    profileDraft,
    profileEditWritable,
    runtimeWriterReady: Boolean(
      profileSynchronized && currentDeviceIsPrimary && !runtimeAuthorityMutationInFlight
    ),
    currentDeviceIsPrimary,
    authorityRevision: ready ? state?.authorityRevision ?? 0 : 0,
    busy,
    error,
    stagePrimaryProfile,
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
    throw new Error('Mod 返回的主设备状态与当前伴随窗口使用的协议不一致。');
  }
}

function parseAuthorityState(state: CompanionDeviceAuthorityState): CompanionDeviceAuthorityState {
  validateAuthorityState(state);
  return {
    ...state,
    activeProfile: parseSharedCompanionPreferences(state.activeProfile),
    currentDeviceProfile: parseSharedCompanionPreferences(state.currentDeviceProfile),
  };
}

function isOlderAuthorityObservation(
  current: CompanionDeviceAuthorityState | null,
  next: CompanionDeviceAuthorityState,
): boolean {
  return Boolean(current
    && current.registryId === next.registryId
    && current.currentDeviceId === next.currentDeviceId
    && (next.stateRevision < current.stateRevision
      || next.authorityRevision < current.authorityRevision));
}

function buildPendingSyncKey(
  generation: number,
  state: CompanionDeviceAuthorityState,
  pendingSyncId: string,
): string {
  return [
    generation,
    state.registryId,
    state.currentDeviceId,
    pendingSyncId,
    state.currentDeviceProfileRevision,
    state.currentDeviceProfileHash,
  ].join('\n');
}

function formatAuthorityError(cause: unknown): string {
  return cause instanceof Error ? cause.message : String(cause);
}
