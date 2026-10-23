import type { ServiceOrderSortMode } from '@/companion/preferences';
import { getSpecialBusinessOrderPriority } from '@/companion/domain/special-business/registry';
import type {
  NightBusinessOrder,
  NormalBusinessOrder,
  SpecialBusinessContext,
} from '@/companion/types';

export function sortNightOrders(
  orders: NightBusinessOrder[],
  mode: ServiceOrderSortMode = 'ordered',
  specialBusiness: SpecialBusinessContext | null | undefined = null,
): NightBusinessOrder[] {
  const groupFirstSeen = buildOrderGroupFirstSeen(orders);
  return [...orders].sort((left, right) => compareNightOrders(
    left,
    right,
    mode,
    groupFirstSeen,
    specialBusiness,
  ));
}

export function sortNightOrderRows<T extends { order: NightBusinessOrder }>(
  rows: T[],
  mode: ServiceOrderSortMode,
  specialBusiness: SpecialBusinessContext | null | undefined = null,
): T[] {
  const groupFirstSeen = buildOrderGroupFirstSeen(rows.map((row) => row.order));
  return [...rows].sort((left, right) => compareNightOrders(
    left.order,
    right.order,
    mode,
    groupFirstSeen,
    specialBusiness,
  ));
}

export function sortNormalOrders(orders: NormalBusinessOrder[]): NormalBusinessOrder[] {
  return [...orders].sort(compareNormalOrdersByTime);
}

function compareNormalOrdersByTime(left: NormalBusinessOrder, right: NormalBusinessOrder): number {
  const leftSeenAt = getNormalOrderSeenTime(left);
  const rightSeenAt = getNormalOrderSeenTime(right);
  if (leftSeenAt !== rightSeenAt) return leftSeenAt - rightSeenAt;
  if (left.deskCode !== right.deskCode) return left.deskCode - right.deskCode;
  const foodCompare = left.foodName.localeCompare(right.foodName, 'zh-Hans-CN');
  if (foodCompare !== 0) return foodCompare;
  return left.beverageName.localeCompare(right.beverageName, 'zh-Hans-CN');
}

function getNormalOrderSeenTime(order: NormalBusinessOrder): number {
  if (!order.firstSeenAtUtc) return Number.MAX_SAFE_INTEGER;
  const time = Date.parse(order.firstSeenAtUtc);
  return Number.isFinite(time) ? time : Number.MAX_SAFE_INTEGER;
}

function compareNightOrders(
  left: NightBusinessOrder,
  right: NightBusinessOrder,
  mode: ServiceOrderSortMode = 'ordered',
  groupFirstSeen: Map<string, number> | null = null,
  specialBusiness: SpecialBusinessContext | null | undefined = null,
): number {
  const priorityDifference = getSpecialBusinessOrderPriority(
    specialBusiness,
    left.specialBusinessRole,
  ) - getSpecialBusinessOrderPriority(
    specialBusiness,
    right.specialBusinessRole,
  );
  if (priorityDifference !== 0) return priorityDifference;

  if (mode === 'guest') {
    const leftGroupKey = getOrderGuestGroupKey(left);
    const rightGroupKey = getOrderGuestGroupKey(right);
    if (leftGroupKey !== rightGroupKey) {
      const leftGroupSeenAt = groupFirstSeen?.get(leftGroupKey) ?? getOrderSeenTime(left);
      const rightGroupSeenAt = groupFirstSeen?.get(rightGroupKey) ?? getOrderSeenTime(right);
      if (leftGroupSeenAt !== rightGroupSeenAt) return leftGroupSeenAt - rightGroupSeenAt;
      const groupCompare = compareOrderGroupIdentity(left, right);
      if (groupCompare !== 0) return groupCompare;
    }
  }

  return compareNightOrdersByTime(left, right);
}

function compareNightOrdersByTime(left: NightBusinessOrder, right: NightBusinessOrder): number {
  const leftSeenAt = getOrderSeenTime(left);
  const rightSeenAt = getOrderSeenTime(right);
  if (leftSeenAt !== rightSeenAt) return leftSeenAt - rightSeenAt;
  if (left.deskCode !== right.deskCode) return left.deskCode - right.deskCode;
  return left.guestName.localeCompare(right.guestName, 'zh-Hans-CN');
}

function buildOrderGroupFirstSeen(orders: NightBusinessOrder[]): Map<string, number> {
  const result = new Map<string, number>();
  for (const order of orders) {
    const key = getOrderGuestGroupKey(order);
    const seenAt = getOrderSeenTime(order);
    const current = result.get(key);
    if (current === undefined || seenAt < current) result.set(key, seenAt);
  }
  return result;
}

function getOrderGuestGroupKey(order: NightBusinessOrder): string {
  if (order.guestId !== null && order.guestId !== undefined && order.guestId >= 0) {
    return `id:${order.guestId}`;
  }
  return `name:${order.guestName.trim()}|desk:${order.deskCode}`;
}

function compareOrderGroupIdentity(left: NightBusinessOrder, right: NightBusinessOrder): number {
  const nameCompare = left.guestName.localeCompare(right.guestName, 'zh-Hans-CN');
  if (nameCompare !== 0) return nameCompare;
  const leftGuestId = left.guestId ?? Number.MAX_SAFE_INTEGER;
  const rightGuestId = right.guestId ?? Number.MAX_SAFE_INTEGER;
  if (leftGuestId !== rightGuestId) return leftGuestId - rightGuestId;
  return left.deskCode - right.deskCode;
}

function getOrderSeenTime(order: NightBusinessOrder): number {
  const value = order.firstSeenAtUtc ?? order.lastSeenAtUtc;
  if (!value) return Number.MAX_SAFE_INTEGER;
  const time = Date.parse(value);
  return Number.isFinite(time) ? time : Number.MAX_SAFE_INTEGER;
}
