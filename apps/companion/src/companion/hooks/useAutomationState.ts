import { isAutomationResponseCurrent } from '@/companion/automation-machine';
import {
  type AutoFirstOrderState,
  type NormalAutoOrderState,
  type OrderPreparationResponse,
} from '@/companion/automation-state';
import { type ValidOrderPreparationSelection } from '@/companion/domain/automation';
import type {
  AutomationCookerCycle,
  LocalApiAutomationLease,
  NormalAutoOrderDiagnostic,
  RareAutoOrderDiagnostic,
} from '@/companion/types';
import { useCallback, useEffect, useRef, useState } from 'react';

import {
  buildNormalAutomationDiagnosticsSignature,
  buildRareAutomationDiagnosticsSignature,
} from '@/companion/domain/automation-diagnostics';
import {
  isAutomationLeaseUnavailableResponse,
  mergeRejectedRecipeKeys,
  rememberRejectedRecipeKey,
  trimRejectedRecipeKeys,
} from '@/companion/domain/automation-lifecycle';

export interface AutomationLeaseAcquireEntry {
  key: string;
  promise: Promise<LocalApiAutomationLease>;
}

export interface AutomationControlReleaseEntry {
  transition: number;
  promise: Promise<void>;
}

export interface AutomationBarrierAckEntry {
  key: string;
  sessionId: string;
  sequence: number;
}

interface AutomationStateOptions {
  automationUiVisible: boolean;
  refresh: () => Promise<unknown>;
}
export function useAutomationState({ automationUiVisible, refresh }: AutomationStateOptions) {
  const [autoPrepBusy, setAutoPrepBusy] = useState(false);
  const [autoPrepMessage, setAutoPrepMessage] = useState('');
  const [autoPrepPaused, setAutoPrepPaused] = useState(false);
  const [rareOrderDiagnostics, setRareOrderDiagnostics] = useState<RareAutoOrderDiagnostic[]>([]);
  const [normalOrderBusy, setNormalOrderBusy] = useState(false);
  const [normalOrderMessage, setNormalOrderMessage] = useState('');
  const [normalOrderPausedCount, setNormalOrderPausedCount] = useState(0);
  const [normalOrderDiagnostics, setNormalOrderDiagnostics] = useState<NormalAutoOrderDiagnostic[]>([]);
  const [automationLease, setAutomationLease] = useState<LocalApiAutomationLease | null>(null);
  const [automationLeaseBindingKey, setAutomationLeaseBindingKey] = useState('');
  const [automationLeaseError, setAutomationLeaseError] = useState('');
  const [automationBarrierAckBusyKey, setAutomationBarrierAckBusyKey] = useState('');
  const [automationBarrierAckErrors, setAutomationBarrierAckErrors] = useState<Record<number, string>>({});
  const [specialBusinessRejectedRecipeKeys, setSpecialBusinessRejectedRecipeKeys] = useState<string[]>([]);
  const specialBusinessRejectedRecipeKeysRef = useRef(new Set<string>());
  // 自动化状态不放入 useState，是为了避免每个轮询 tick 都触发整页重渲染；页面只在诊断摘要变化时更新。
  const rareOrderStatesRef = useRef(new Map<string, AutoFirstOrderState>());
  const rareOrderDiagnosticItemsRef = useRef(new Map<string, ValidOrderPreparationSelection>());
  const autoFirstOrderBusyRef = useRef(false);
  const normalOrderStatesRef = useRef(new Map<string, NormalAutoOrderState>());
  const normalOrderBusyRef = useRef(false);
  const lastAutoFirstOrderAtRef = useRef(0);
  const lastAutoNormalOrderAtRef = useRef(0);
  const automationCookerCycleRef = useRef<AutomationCookerCycle | null>(null);
  const rareAutomationDecisionDiagnosticSignaturesRef = useRef(new Set<string>());
  const normalAutomationDecisionDiagnosticSignaturesRef = useRef(new Set<string>());
  const automationRequestEpochRef = useRef(0);
  const rareParticipationMutationBusyRef = useRef(false);
  const automationLeaseAcquireRef = useRef<AutomationLeaseAcquireEntry | null>(null);
  const previousAutomationControlSignatureRef = useRef('');
  const automationControlTransitionRef = useRef(0);
  const automationControlReleaseRef = useRef<AutomationControlReleaseEntry | null>(null);
  const automationControlReleasePendingRef = useRef(false);
  const [automationControlReleasePending, setAutomationControlReleasePending] = useState(false);
  const automationLeaseRevalidationRequiredRef = useRef(true);
  const automationStateSessionIdRef = useRef('');
  const automationLeaseOwnedRef = useRef(false);
  const automationBarrierAckRef = useRef<AutomationBarrierAckEntry | null>(null);
  const previousAutomationRuntimeEnabledRef = useRef(false);
  const automationRuntimeEnabledRef = useRef(false);
  const previousAutomationRuntimePauseMessageRef = useRef('');
  const automationRefreshTimerRef = useRef<number | null>(null);
  const automationUiVisibleRef = useRef(automationUiVisible);
  const autoPrepBusyValueRef = useRef(false);
  const autoPrepMessageValueRef = useRef('');
  const autoPrepPausedValueRef = useRef(false);
  const rareOrderDiagnosticsValueRef = useRef<RareAutoOrderDiagnostic[]>([]);
  const rareOrderDiagnosticsSignatureRef = useRef('');
  const normalOrderBusyValueRef = useRef(false);
  const normalOrderMessageValueRef = useRef('');
  const normalOrderPausedCountValueRef = useRef(0);
  const normalOrderDiagnosticsValueRef = useRef<NormalAutoOrderDiagnostic[]>([]);
  const normalOrderDiagnosticsSignatureRef = useRef('');

  const publishAutoPrepBusy = useCallback((next: boolean) => {
    autoPrepBusyValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setAutoPrepBusy((current) => (current === next ? current : next));
  }, []);

  const publishAutoPrepMessage = useCallback((next: string) => {
    autoPrepMessageValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setAutoPrepMessage((current) => (current === next ? current : next));
  }, []);

  const publishAutoPrepPaused = useCallback((next: boolean) => {
    autoPrepPausedValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setAutoPrepPaused((current) => (current === next ? current : next));
  }, []);

  const publishRareOrderDiagnostics = useCallback((next: RareAutoOrderDiagnostic[]) => {
    const signature = buildRareAutomationDiagnosticsSignature(next);
    rareOrderDiagnosticsValueRef.current = next;
    if (rareOrderDiagnosticsSignatureRef.current === signature) return;
    rareOrderDiagnosticsSignatureRef.current = signature;
    if (!automationUiVisibleRef.current) return;
    setRareOrderDiagnostics(next);
  }, []);

  const publishNormalOrderBusy = useCallback((next: boolean) => {
    normalOrderBusyValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderBusy((current) => (current === next ? current : next));
  }, []);

  const publishNormalOrderMessage = useCallback((next: string) => {
    normalOrderMessageValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderMessage((current) => (current === next ? current : next));
  }, []);

  const publishNormalOrderPausedCount = useCallback((next: number) => {
    normalOrderPausedCountValueRef.current = next;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderPausedCount((current) => (current === next ? current : next));
  }, []);

  const publishNormalOrderDiagnostics = useCallback((next: NormalAutoOrderDiagnostic[]) => {
    const signature = buildNormalAutomationDiagnosticsSignature(next);
    normalOrderDiagnosticsValueRef.current = next;
    if (normalOrderDiagnosticsSignatureRef.current === signature) return;
    normalOrderDiagnosticsSignatureRef.current = signature;
    if (!automationUiVisibleRef.current) return;
    setNormalOrderDiagnostics(next);
  }, []);

  useEffect(() => {
    automationUiVisibleRef.current = automationUiVisible;
    if (!automationUiVisible) return;
    setAutoPrepBusy((current) =>
      current === autoPrepBusyValueRef.current ? current : autoPrepBusyValueRef.current,
    );
    setAutoPrepMessage((current) =>
      current === autoPrepMessageValueRef.current ? current : autoPrepMessageValueRef.current,
    );
    setAutoPrepPaused((current) =>
      current === autoPrepPausedValueRef.current ? current : autoPrepPausedValueRef.current,
    );
    setRareOrderDiagnostics(rareOrderDiagnosticsValueRef.current);
    setNormalOrderBusy((current) =>
      current === normalOrderBusyValueRef.current ? current : normalOrderBusyValueRef.current,
    );
    setNormalOrderMessage((current) =>
      current === normalOrderMessageValueRef.current ? current : normalOrderMessageValueRef.current,
    );
    setNormalOrderPausedCount((current) =>
      current === normalOrderPausedCountValueRef.current ? current : normalOrderPausedCountValueRef.current,
    );
    setNormalOrderDiagnostics(normalOrderDiagnosticsValueRef.current);
  }, [automationUiVisible]);

  const scheduleAutomationRefresh = useCallback(() => {
    if (automationRefreshTimerRef.current !== null) return;
    automationRefreshTimerRef.current = window.setTimeout(() => {
      automationRefreshTimerRef.current = null;
      void refresh();
    }, 180);
  }, [refresh]);

  const isAutomationRequestCurrent = useCallback(
    (requestEpoch: number, responseStartEventSequence = 0, currentEventSequence = 0) =>
      isAutomationResponseCurrent({
        requestEpoch,
        currentEpoch: automationRequestEpochRef.current,
        runtimeEnabled: automationRuntimeEnabledRef.current,
        responseStartEventSequence,
        currentEventSequence,
      }),
    [],
  );

  const handleAutomationControlPlaneResponse = useCallback((response: OrderPreparationResponse): boolean => {
    if (!isAutomationLeaseUnavailableResponse(response)) return false;
    automationRequestEpochRef.current += 1;
    automationRuntimeEnabledRef.current = false;
    automationLeaseRevalidationRequiredRef.current = true;
    setAutomationLease(null);
    setAutomationLeaseBindingKey('');
    setAutomationLeaseError(response.error || '自动化控制权已失效，正在重新获取。');
    lastAutoFirstOrderAtRef.current = 0;
    lastAutoNormalOrderAtRef.current = 0;
    return true;
  }, []);

  useEffect(
    () => () => {
      if (automationRefreshTimerRef.current === null) return;
      window.clearTimeout(automationRefreshTimerRef.current);
      automationRefreshTimerRef.current = null;
    },
    [],
  );

  const getSpecialBusinessRejectedRecipeKeys = useCallback(
    () =>
      mergeRejectedRecipeKeys(
        specialBusinessRejectedRecipeKeys,
        specialBusinessRejectedRecipeKeysRef.current,
      ),
    [specialBusinessRejectedRecipeKeys],
  );

  const rememberSpecialBusinessRejectedRecipeKey = useCallback((rejectedKey: string) => {
    if (!rejectedKey) return;
    rememberRejectedRecipeKey(specialBusinessRejectedRecipeKeysRef.current, rejectedKey);
    setSpecialBusinessRejectedRecipeKeys((current) =>
      trimRejectedRecipeKeys([...current, ...specialBusinessRejectedRecipeKeysRef.current]),
    );
  }, []);

  return {
    autoPrepBusy,
    autoPrepMessage,
    autoPrepPaused,
    rareOrderDiagnostics,
    normalOrderBusy,
    normalOrderMessage,
    normalOrderPausedCount,
    normalOrderDiagnostics,
    automationLease,
    setAutomationLease,
    automationLeaseBindingKey,
    setAutomationLeaseBindingKey,
    automationLeaseError,
    setAutomationLeaseError,
    automationBarrierAckBusyKey,
    setAutomationBarrierAckBusyKey,
    automationBarrierAckErrors,
    setAutomationBarrierAckErrors,
    specialBusinessRejectedRecipeKeys,
    setSpecialBusinessRejectedRecipeKeys,
    specialBusinessRejectedRecipeKeysRef,
    rareOrderStatesRef,
    rareOrderDiagnosticItemsRef,
    autoFirstOrderBusyRef,
    normalOrderStatesRef,
    normalOrderBusyRef,
    lastAutoFirstOrderAtRef,
    lastAutoNormalOrderAtRef,
    automationCookerCycleRef,
    rareAutomationDecisionDiagnosticSignaturesRef,
    normalAutomationDecisionDiagnosticSignaturesRef,
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
    automationStateSessionIdRef,
    automationLeaseOwnedRef,
    automationBarrierAckRef,
    previousAutomationRuntimeEnabledRef,
    automationRuntimeEnabledRef,
    previousAutomationRuntimePauseMessageRef,
    autoPrepMessageValueRef,
    normalOrderMessageValueRef,
    publishAutoPrepBusy,
    publishAutoPrepMessage,
    publishAutoPrepPaused,
    publishRareOrderDiagnostics,
    publishNormalOrderBusy,
    publishNormalOrderMessage,
    publishNormalOrderPausedCount,
    publishNormalOrderDiagnostics,
    scheduleAutomationRefresh,
    isAutomationRequestCurrent,
    handleAutomationControlPlaneResponse,
    getSpecialBusinessRejectedRecipeKeys,
    rememberSpecialBusinessRejectedRecipeKey,
  };
}
export type AutomationState = ReturnType<typeof useAutomationState>;
