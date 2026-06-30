import { useEffect, useRef } from 'react';

interface UseOrderAutomationIntervalsOptions {
  automationEnabled: boolean;
  autoNormalOrderEnabled: boolean;
  normalOrderSignature: string;
  rareTickMs: number;
  normalTickMs: number;
  runAutoFirstOrder: () => Promise<void>;
  runAutoNormalOrder: () => Promise<void>;
  onAutomationDisabled: () => void;
  onNormalOrderSignatureChanged: () => void;
  onNormalAutomationDisabled: () => void;
}

/**
 * 管理稀客和普客自动化的轮询节奏。
 *
 * 稀客按固定间隔尝试处理第一笔可行动订单；普客除固定轮询外，还会在订单快照签名变化时立即重试一次，
 * 以便料理收取、送达或新订单出现后尽快推进下一步。
 */
export function useOrderAutomationIntervals({
  automationEnabled,
  autoNormalOrderEnabled,
  normalOrderSignature,
  rareTickMs,
  normalTickMs,
  runAutoFirstOrder,
  runAutoNormalOrder,
  onAutomationDisabled,
  onNormalOrderSignatureChanged,
  onNormalAutomationDisabled,
}: UseOrderAutomationIntervalsOptions) {
  const lastNormalOrderSignatureRef = useRef('');

  useEffect(() => {
    if (!automationEnabled) {
      onAutomationDisabled();
      return undefined;
    }

    void runAutoFirstOrder();
    const timer = window.setInterval(() => {
      void runAutoFirstOrder();
    }, rareTickMs);
    return () => window.clearInterval(timer);
  }, [automationEnabled, onAutomationDisabled, rareTickMs, runAutoFirstOrder]);

  useEffect(() => {
    if (!automationEnabled) return undefined;

    void runAutoNormalOrder();
    const timer = window.setInterval(() => {
      void runAutoNormalOrder();
    }, normalTickMs);
    return () => window.clearInterval(timer);
  }, [automationEnabled, normalTickMs, runAutoNormalOrder]);

  useEffect(() => {
    // 普客订单状态由 Mod 快照驱动，签名变化通常表示订单进度或列表发生变化，需要重置本轮判断。
    if (!automationEnabled || !autoNormalOrderEnabled) {
      lastNormalOrderSignatureRef.current = normalOrderSignature;
      return;
    }

    if (lastNormalOrderSignatureRef.current === normalOrderSignature) return;
    lastNormalOrderSignatureRef.current = normalOrderSignature;
    onNormalOrderSignatureChanged();
    void runAutoNormalOrder();
  }, [
    automationEnabled,
    autoNormalOrderEnabled,
    normalOrderSignature,
    onNormalOrderSignatureChanged,
    runAutoNormalOrder,
  ]);

  useEffect(() => {
    if (automationEnabled && autoNormalOrderEnabled) return;
    onNormalAutomationDisabled();
  }, [automationEnabled, autoNormalOrderEnabled, onNormalAutomationDisabled]);
}
