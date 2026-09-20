import { acquireAutomationLease, releaseAutomationLease } from '@/companion/api';
import {
  buildAutomationLeaseConnectionKey,
  isAutomationLeaseOwnedForConnection,
} from '@/companion/connection-recovery';
import { getNightBusinessAutomationPauseMessage } from '@/companion/domain/automation-runtime';
import { type CompanionPreferences, type SharedCompanionPreferences } from '@/companion/preferences';
import type { LocalApiAutomationLease } from '@/companion/types';
import { useCallback, useEffect, useMemo, useRef } from 'react';

import { AUTOMATION_LEASE_RENEW_INTERVAL_MS } from '@/companion/domain/automation-constants';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';
import type { LocalApiSnapshot } from '@/companion/types';

import type { AutomationLeaseAcquireEntry, AutomationState } from '@/companion/hooks/useAutomationState';
function buildAutomationControlSignature(preferences: SharedCompanionPreferences): string {
  const switches = [
    preferences.automationEnabled,
    preferences.autoRareOrderEnabled,
    preferences.rareGuestParticipationModuleEnabled,
    preferences.autoPrepTakeBeverage,
    preferences.autoPrepStartCooking,
    preferences.autoPrepCollectCooking,
    preferences.autoPrepCompleteOrder,
    preferences.autoNormalOrderEnabled,
    preferences.autoNormalTakeBeverage,
    preferences.autoNormalStartCooking,
    preferences.autoNormalDeliverFood,
    preferences.autoNormalCompleteOrder,
  ]
    .map((value) => (value ? '1' : '0'))
    .join('');
  return `${switches}|${preferences.managedRareGuestIds.join(',')}`;
}

interface AutomationControlOptions {
  state: AutomationState;
  apiToken: string;
  normalizedEndpoint: string;
  connectionPaused: boolean;
  connectionRevision: number;
  error: string | null;
  snapshot: LocalApiSnapshot | null;
  companionConnected: boolean;
  companionPreferences: CompanionPreferences;
  sharedCompanionPreferences: SharedCompanionPreferences;
  companionDeviceAuthority: CompanionDeviceAuthorityController;
}
export function useAutomationControl({
  state,
  apiToken,
  normalizedEndpoint,
  connectionPaused,
  connectionRevision,
  error,
  snapshot,
  companionConnected,
  companionPreferences,
  sharedCompanionPreferences,
  companionDeviceAuthority,
}: AutomationControlOptions) {
  const {
    automationLease,
    setAutomationLease,
    automationLeaseBindingKey,
    setAutomationLeaseBindingKey,
    automationLeaseError,
    setAutomationLeaseError,
    automationRequestEpochRef,
    rareParticipationMutationBusyRef,
    automationLeaseAcquireRef,
    previousAutomationControlSignatureRef,
    automationControlTransitionRef,
    automationControlReleaseRef,
    automationControlReleasePendingRef,
    automationControlReleasePending,
    setAutomationControlReleasePending,
    automationLeaseRevalidationRequiredRef,
    automationLeaseOwnedRef,
    previousAutomationRuntimeEnabledRef,
    automationRuntimeEnabledRef,
    previousAutomationRuntimePauseMessageRef,
    autoPrepMessageValueRef,
    normalOrderMessageValueRef,
    publishAutoPrepBusy,
    publishAutoPrepMessage,
    publishNormalOrderBusy,
    publishNormalOrderMessage,
  } = state;
  const automationControlSignature = useMemo(
    () => buildAutomationControlSignature(sharedCompanionPreferences),
    [sharedCompanionPreferences],
  );
  const connectionReadyForActions = Boolean(
    apiToken && !connectionPaused && !error && snapshot && companionDeviceAuthority.runtimeWriterReady,
  );
  if (!connectionReadyForActions) automationLeaseRevalidationRequiredRef.current = true;
  const automationSessionId = snapshot?.automationSessionId.trim() ?? '';
  const automationConnectionScope = useMemo(
    () => ({
      normalizedEndpoint,
      apiToken,
      connectionRevision,
      automationSessionId,
      authorityRevision: companionDeviceAuthority.authorityRevision,
      connectionReadyForActions,
    }),
    [
      normalizedEndpoint,
      apiToken,
      connectionRevision,
      automationSessionId,
      companionDeviceAuthority.authorityRevision,
      connectionReadyForActions,
    ],
  );
  const automationConnectionScopeRef = useRef(automationConnectionScope);
  if (automationConnectionScopeRef.current !== automationConnectionScope) {
    automationLeaseRevalidationRequiredRef.current = true;
    automationRequestEpochRef.current += 1;
    automationConnectionScopeRef.current = automationConnectionScope;
  }
  const leaseRequestScope = useMemo(
    () => ({
      connection: automationConnectionScope,
      control: automationControlSignature,
      enabled: companionPreferences.automationEnabled,
    }),
    [automationConnectionScope, automationControlSignature, companionPreferences.automationEnabled],
  );
  const leaseRequestScopeRef = useRef(leaseRequestScope);
  const leaseRequestSequenceRef = useRef(0);
  if (leaseRequestScopeRef.current !== leaseRequestScope) {
    leaseRequestScopeRef.current = leaseRequestScope;
    leaseRequestSequenceRef.current += 1;
  }
  const automationLeaseConnectionKey = buildAutomationLeaseConnectionKey(
    { endpoint: normalizedEndpoint, apiToken },
    automationSessionId,
    companionDeviceAuthority.authorityRevision,
  );
  const automationLeaseOwned = isAutomationLeaseOwnedForConnection(
    automationLease,
    automationLeaseBindingKey,
    automationLeaseConnectionKey,
    automationLeaseRevalidationRequiredRef.current,
  );
  automationLeaseOwnedRef.current = automationLeaseOwned;
  const leaseRequestKey = `${automationLeaseConnectionKey}|request:${leaseRequestSequenceRef.current}`;
  const nightBusinessAutomationAllowed = snapshot?.nightBusinessAutomationAllowed === true;
  const nightBusinessAutomationBlockReason = snapshot?.nightBusinessAutomationBlockReason ?? '';
  const automationRuntimePauseMessage = getNightBusinessAutomationPauseMessage(
    nightBusinessAutomationBlockReason,
  );
  const automationRuntimeEnabled =
    companionPreferences.automationEnabled &&
    connectionReadyForActions &&
    Boolean(automationSessionId) &&
    automationLeaseOwned &&
    nightBusinessAutomationAllowed;
  if (previousAutomationRuntimeEnabledRef.current && !automationRuntimeEnabled) {
    automationRequestEpochRef.current += 1;
  }
  previousAutomationRuntimeEnabledRef.current = automationRuntimeEnabled;
  automationRuntimeEnabledRef.current = automationRuntimeEnabled;
  const markRareParticipationMutationBoundary = useCallback(() => {
    rareParticipationMutationBusyRef.current = true;
    automationRequestEpochRef.current += 1;
  }, [automationRequestEpochRef, rareParticipationMutationBusyRef]);
  const acquireAutomationLeaseSingleFlight = useCallback((): Promise<LocalApiAutomationLease> => {
    const key = leaseRequestKey;
    const acquireCurrent = () => {
      if (
        !automationLeaseConnectionKey ||
        leaseRequestScopeRef.current !== leaseRequestScope ||
        !leaseRequestScope.enabled ||
        !leaseRequestScope.connection.connectionReadyForActions ||
        automationControlReleasePendingRef.current
      ) {
        throw new Error('自动化控制上下文已改变，未发送过期的控制权请求。');
      }
      return acquireAutomationLease(normalizedEndpoint, apiToken, companionDeviceAuthority.authorityRevision);
    };
    const current = automationLeaseAcquireRef.current;
    if (current?.key === key) return current.promise;

    const promise = current
      ? current.promise.catch(() => undefined).then(acquireCurrent)
      : Promise.resolve().then(acquireCurrent);
    const entry: AutomationLeaseAcquireEntry = { key, promise };
    automationLeaseAcquireRef.current = entry;
    const clearEntry = () => {
      if (automationLeaseAcquireRef.current === entry) automationLeaseAcquireRef.current = null;
    };
    void promise.then(clearEntry, clearEntry);
    return promise;
  }, [
    apiToken,
    automationLeaseAcquireRef,
    automationLeaseConnectionKey,
    automationControlReleasePendingRef,
    leaseRequestKey,
    leaseRequestScope,
    companionDeviceAuthority.authorityRevision,
    normalizedEndpoint,
  ]);

  const waitForAutomationLeaseAcquire = useCallback(async (): Promise<void> => {
    while (automationLeaseAcquireRef.current) {
      const entry = automationLeaseAcquireRef.current;
      try {
        await entry.promise;
      } catch {
        // A control-state release still has to run after a failed acquire attempt.
      }
      if (automationLeaseAcquireRef.current === entry) return;
    }
  }, [automationLeaseAcquireRef]);

  useEffect(() => {
    const previousSignature = previousAutomationControlSignatureRef.current;
    previousAutomationControlSignatureRef.current = automationControlSignature;
    if (!previousSignature || previousSignature === automationControlSignature) return undefined;

    const transition = automationControlTransitionRef.current + 1;
    automationControlTransitionRef.current = transition;
    automationControlReleasePendingRef.current = true;
    setAutomationControlReleasePending(true);
    automationRequestEpochRef.current += 1;
    automationRuntimeEnabledRef.current = false;
    automationLeaseRevalidationRequiredRef.current = true;
    setAutomationLease(null);
    setAutomationLeaseBindingKey('');
    publishAutoPrepBusy(false);
    publishNormalOrderBusy(false);

    if (
      !apiToken ||
      !companionConnected ||
      !companionDeviceAuthority.currentDeviceIsPrimary ||
      companionDeviceAuthority.authorityRevision <= 0
    ) {
      automationControlReleasePendingRef.current = false;
      setAutomationControlReleasePending(false);
      return undefined;
    }

    const previousRelease = automationControlReleaseRef.current?.promise;
    const release = (async () => {
      try {
        if (previousRelease) await previousRelease;
        await waitForAutomationLeaseAcquire();
        if (automationConnectionScopeRef.current !== automationConnectionScope) return;
        const response = await releaseAutomationLease(
          normalizedEndpoint,
          apiToken,
          companionDeviceAuthority.authorityRevision,
        );
        if (!response.ok) {
          throw new Error(response.error || 'Mod 未确认自动化控制权释放。');
        }
        if (
          automationControlTransitionRef.current === transition &&
          automationConnectionScopeRef.current === automationConnectionScope
        )
          setAutomationLeaseError('');
      } catch (err) {
        if (
          automationControlTransitionRef.current === transition &&
          automationConnectionScopeRef.current === automationConnectionScope
        ) {
          setAutomationLeaseError(err instanceof Error ? err.message : String(err));
        }
      } finally {
        if (automationControlTransitionRef.current === transition) {
          if (automationControlReleaseRef.current?.transition === transition) {
            automationControlReleaseRef.current = null;
          }
          automationControlReleasePendingRef.current = false;
          setAutomationControlReleasePending(false);
        }
      }
    })();
    automationControlReleaseRef.current = { transition, promise: release };

    return undefined;
  }, [
    apiToken,
    automationControlReleasePendingRef,
    automationControlReleaseRef,
    automationControlSignature,
    automationConnectionScope,
    automationControlTransitionRef,
    automationLeaseRevalidationRequiredRef,
    automationRequestEpochRef,
    automationRuntimeEnabledRef,
    companionConnected,
    companionDeviceAuthority.authorityRevision,
    companionDeviceAuthority.currentDeviceIsPrimary,
    normalizedEndpoint,
    previousAutomationControlSignatureRef,
    publishAutoPrepBusy,
    publishNormalOrderBusy,
    setAutomationControlReleasePending,
    setAutomationLease,
    setAutomationLeaseBindingKey,
    setAutomationLeaseError,
    waitForAutomationLeaseAcquire,
  ]);

  useEffect(() => {
    if (
      !companionPreferences.automationEnabled ||
      !connectionReadyForActions ||
      !automationLeaseConnectionKey ||
      automationControlReleasePending
    )
      return undefined;

    let cancelled = false;
    const renewLease = async () => {
      if (automationControlReleasePendingRef.current) return;
      try {
        const nextLease = await acquireAutomationLeaseSingleFlight();
        if (cancelled || leaseRequestScopeRef.current !== leaseRequestScope) return;
        automationLeaseRevalidationRequiredRef.current = false;
        setAutomationLease(nextLease);
        setAutomationLeaseBindingKey(nextLease.owned ? automationLeaseConnectionKey : '');
        setAutomationLeaseError(nextLease.owned ? '' : nextLease.error || '自动化控制权当前不可用。');
      } catch (err) {
        if (cancelled || leaseRequestScopeRef.current !== leaseRequestScope) return;
        automationLeaseRevalidationRequiredRef.current = true;
        setAutomationLease(null);
        setAutomationLeaseBindingKey('');
        setAutomationLeaseError(err instanceof Error ? err.message : String(err));
      }
    };

    void renewLease();
    const timer = window.setInterval(() => {
      void renewLease();
    }, AUTOMATION_LEASE_RENEW_INTERVAL_MS);

    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, [
    apiToken,
    acquireAutomationLeaseSingleFlight,
    automationLeaseConnectionKey,
    leaseRequestScope,
    automationControlReleasePending,
    companionPreferences.automationEnabled,
    connectionReadyForActions,
    normalizedEndpoint,
    automationControlReleasePendingRef,
    automationLeaseRevalidationRequiredRef,
    setAutomationLease,
    setAutomationLeaseBindingKey,
    setAutomationLeaseError,
  ]);

  useEffect(() => {
    if (!companionPreferences.automationEnabled) return;

    if (!connectionReadyForActions) {
      publishAutoPrepMessage('自动化\n连接不可用，已暂停执行。');
      publishNormalOrderMessage('');
      return;
    }

    if (!automationLease) {
      publishAutoPrepMessage(
        automationLeaseError
          ? `自动化控制权\n${automationLeaseError}`
          : '自动化\n正在获取本窗口自动化控制权。',
      );
      publishNormalOrderMessage('');
      return;
    }

    if (!automationLeaseOwned) {
      const owner = automationLease.ownerLabel || '其他设备';
      publishAutoPrepMessage(
        `自动化控制权\n${automationLease.error || `自动化当前由 ${owner} 控制，本窗口仅查看。`}`,
      );
      publishNormalOrderMessage('');
      return;
    }

    if (automationLeaseError) {
      publishAutoPrepMessage(`自动化控制权\n${automationLeaseError}`);
    }
  }, [
    automationLease,
    automationLeaseError,
    automationLeaseOwned,
    companionPreferences.automationEnabled,
    connectionReadyForActions,
    publishAutoPrepMessage,
    publishNormalOrderMessage,
  ]);

  useEffect(() => {
    const previousPauseMessage = previousAutomationRuntimePauseMessageRef.current;
    previousAutomationRuntimePauseMessageRef.current = companionPreferences.automationEnabled
      ? automationRuntimePauseMessage
      : '';

    if (companionPreferences.automationEnabled && automationRuntimePauseMessage) {
      publishAutoPrepBusy(false);
      publishNormalOrderBusy(false);
      publishAutoPrepMessage(`自动化\n${automationRuntimePauseMessage}`);
      publishNormalOrderMessage(`普客自动化\n${automationRuntimePauseMessage}`);
      return;
    }

    if (!previousPauseMessage) return;
    if (autoPrepMessageValueRef.current === `自动化\n${previousPauseMessage}`) {
      publishAutoPrepMessage('');
    }
    if (normalOrderMessageValueRef.current === `普客自动化\n${previousPauseMessage}`) {
      publishNormalOrderMessage('');
    }
  }, [
    autoPrepMessageValueRef,
    automationRuntimePauseMessage,
    companionPreferences.automationEnabled,
    normalOrderMessageValueRef,
    previousAutomationRuntimePauseMessageRef,
    publishAutoPrepBusy,
    publishAutoPrepMessage,
    publishNormalOrderBusy,
    publishNormalOrderMessage,
  ]);

  return {
    automationConnectionScope,
    automationConnectionScopeRef,
    connectionReadyForActions,
    automationRuntimeEnabled,
    automationSessionId,
    automationLeaseOwned,
    automationRuntimePauseMessage,
    nightBusinessAutomationAllowed,
    nightBusinessAutomationBlockReason,
    markRareParticipationMutationBoundary,
  };
}
export type AutomationControl = ReturnType<typeof useAutomationControl>;
