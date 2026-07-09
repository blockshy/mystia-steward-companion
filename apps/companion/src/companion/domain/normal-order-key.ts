import type { NormalBusinessOrder } from '@/companion/types';

/**
 * 构建普客自动化状态键。
 */
export function buildNormalAutoOrderKey(order: NormalBusinessOrder): string {
  if (order.orderKey) return order.orderKey;
  return [
    order.firstSeenAtUtc ?? '',
    order.deskCode,
    order.guestName,
    order.foodId,
    order.beverageId,
  ].join('|');
}
