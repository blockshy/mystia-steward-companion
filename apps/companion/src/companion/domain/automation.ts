/** 客户端仅保留协议、表单和展示辅助；业务推荐与自动化决策由 C# 服务计算。 */
import type { NightBusinessOrder } from '@/companion/types';

/**
 * 构建夜间稀客订单快照键。
 */
export function buildNightBusinessOrderKey(order: NightBusinessOrder): string {
  if (order.orderLifecycleSequence > 0) {
    const runtimeIdentity = order.traceId?.trim()
      ? `trace:${order.traceId.trim()}`
      : [
        order.deskCode,
        order.runtimeGuestId ?? 'unknown-runtime-guest',
        order.foodTagId ?? 'unknown-food-tag',
        order.beverageTagId ?? 'unknown-beverage-tag',
      ].join('|');
    return `${runtimeIdentity}|lifecycle:${order.orderLifecycleSequence}`;
  }
  return [
    'unbound',
    order.firstSeenAtUtc ?? order.lastSeenAtUtc ?? '',
    order.deskCode,
    order.runtimeGuestId ?? 'unknown-runtime-guest',
    order.foodTagId,
    order.beverageTagId,
    order.source,
    order.isFreeOrder ? 'free' : 'paid',
  ].join('|');
}
