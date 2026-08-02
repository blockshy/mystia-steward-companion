import { useCallback, useEffect, useRef } from 'react';

interface UseOrderAutomationIntervalsOptions {
  automationEnabled: boolean;
  resetStateWhenDisabled: boolean;
  autoRareOrderEnabled: boolean;
  resetRareStateWhenDisabled: boolean;
  autoNormalOrderEnabled: boolean;
  resetNormalStateWhenDisabled: boolean;
  normalOrderSignature: string;
  rareTickMs: number;
  normalTickMs: number;
  runAutoFirstOrder: () => Promise<void>;
  runAutoNormalOrder: () => Promise<void>;
  onAutomationDisabled: () => void;
  onRareAutomationDisabled: () => void;
  onNormalOrderSignatureChanged: () => void;
  onNormalAutomationDisabled: () => void;
}

/**
 * 管理稀客和普客自动化的轮询节奏。
 *
 * 稀客按固定间隔尝试处理第一笔可行动订单；普客除固定轮询外，还会在订单快照签名变化时立即重试一次，
 * 以便料理出锅直送、酒水送达或新订单出现后尽快推进下一步。
 */
export function useOrderAutomationIntervals({
  automationEnabled,
  resetStateWhenDisabled,
  autoRareOrderEnabled,
  resetRareStateWhenDisabled,
  autoNormalOrderEnabled,
  resetNormalStateWhenDisabled,
  normalOrderSignature,
  rareTickMs,
  normalTickMs,
  runAutoFirstOrder,
  runAutoNormalOrder,
  onAutomationDisabled,
  onRareAutomationDisabled,
  onNormalOrderSignatureChanged,
  onNormalAutomationDisabled,
}: UseOrderAutomationIntervalsOptions) {
  const lastNormalOrderSignatureRef = useRef('');
  const normalSignatureTimerRef = useRef<number | null>(null);

  const clearNormalSignatureTimer = useCallback(() => {
    if (normalSignatureTimerRef.current === null) return;
    window.clearTimeout(normalSignatureTimerRef.current);
    normalSignatureTimerRef.current = null;
  }, []);

  useEffect(() => {
    if (!automationEnabled) {
      clearNormalSignatureTimer();
      if (resetStateWhenDisabled) onAutomationDisabled();
      return undefined;
    }
    if (!autoRareOrderEnabled) {
      return undefined;
    }

    void runAutoFirstOrder();
    const timer = window.setInterval(() => {
      void runAutoFirstOrder();
    }, rareTickMs);
    return () => window.clearInterval(timer);
  }, [
    automationEnabled,
    autoRareOrderEnabled,
    clearNormalSignatureTimer,
    onAutomationDisabled,
    rareTickMs,
    resetStateWhenDisabled,
    runAutoFirstOrder,
  ]);

  useEffect(() => {
    if (!resetRareStateWhenDisabled) return;
    onRareAutomationDisabled();
  }, [
    automationEnabled,
    autoRareOrderEnabled,
    onRareAutomationDisabled,
    resetRareStateWhenDisabled,
  ]);

  useEffect(() => {
    if (!automationEnabled || !autoNormalOrderEnabled) {
      clearNormalSignatureTimer();
      return undefined;
    }

    void runAutoNormalOrder();
    const timer = window.setInterval(() => {
      void runAutoNormalOrder();
    }, normalTickMs);
    return () => window.clearInterval(timer);
  }, [
    automationEnabled,
    autoNormalOrderEnabled,
    clearNormalSignatureTimer,
    normalTickMs,
    runAutoNormalOrder,
  ]);

  useEffect(() => {
    // 普客订单状态由 Mod 快照驱动，签名变化通常表示订单进度或列表发生变化，需要重置本轮判断。
    if (!automationEnabled || !autoNormalOrderEnabled) {
      clearNormalSignatureTimer();
      lastNormalOrderSignatureRef.current = normalOrderSignature;
      return;
    }

    if (lastNormalOrderSignatureRef.current === normalOrderSignature) return;
    lastNormalOrderSignatureRef.current = normalOrderSignature;
    onNormalOrderSignatureChanged();
    clearNormalSignatureTimer();
    normalSignatureTimerRef.current = window.setTimeout(() => {
      normalSignatureTimerRef.current = null;
      void runAutoNormalOrder();
    }, Math.min(500, normalTickMs));
  }, [
    automationEnabled,
    autoNormalOrderEnabled,
    clearNormalSignatureTimer,
    normalOrderSignature,
    normalTickMs,
    onNormalOrderSignatureChanged,
    runAutoNormalOrder,
  ]);

  useEffect(() => {
    if (!resetNormalStateWhenDisabled) return;
    clearNormalSignatureTimer();
    onNormalAutomationDisabled();
  }, [
    automationEnabled,
    autoNormalOrderEnabled,
    clearNormalSignatureTimer,
    onNormalAutomationDisabled,
    resetNormalStateWhenDisabled,
  ]);

  useEffect(() => () => clearNormalSignatureTimer(), [clearNormalSignatureTimer]);
}
