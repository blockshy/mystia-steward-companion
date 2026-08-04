import type { NormalBusinessOrder } from '@/companion/types';

/**
 * 由后端原生订单身份和进程内生命周期构建普客自动化状态键。
 * 原生 trace/orderKey 仍单独用于运行时对象匹配，本键不得回传给 Mod。
 */
export function buildNormalLifecycleAutoOrderKey(
  rawOrderIdentity: string | null | undefined,
  orderLifecycleSequence: number,
): string {
  if (!rawOrderIdentity || orderLifecycleSequence <= 0) return '';
  return `${rawOrderIdentity}|lifecycle:${orderLifecycleSequence}`;
}

/**
 * 构建普客自动化状态键。
 */
export function buildNormalAutoOrderKey(order: NormalBusinessOrder): string {
  if (order.orderLifecycleSequence > 0) {
    const lifecycleKey = buildNormalLifecycleAutoOrderKey(
      order.orderKey || order.traceId,
      order.orderLifecycleSequence,
    );
    if (lifecycleKey) return lifecycleKey;
  }
  return [
    'unbound',
    order.firstSeenAtUtc ?? '',
    order.deskCode,
    order.runtimeGuestId ?? 'unknown-runtime-guest',
    order.guestName,
    order.foodId,
    order.beverageId,
  ].join('|');
}
