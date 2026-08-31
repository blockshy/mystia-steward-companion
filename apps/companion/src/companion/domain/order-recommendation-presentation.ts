import type {
  NightBusinessOrder,
  OrderRecommendation,
  RecommendationIssue,
} from '@/companion/types';
import type { RareOrderParticipationResolution } from '@/companion/domain/rare-order-participation';

export interface OrderRecommendationPresentation {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  pendingOrders: NightBusinessOrder[];
  updating: boolean;
  updateError: string | null;
}

interface BuildOrderRecommendationPresentationOptions {
  orders: readonly NightBusinessOrder[];
  recommendations: readonly OrderRecommendation[];
  recommendationIssues: readonly RecommendationIssue[];
  pending: boolean;
  isCurrent: boolean;
  resultContextSignature: string;
  currentContextSignature: string;
  error: string | null;
  retainedAfterError: boolean;
}

/**
 * 将 Worker 的最后一次成功结果投影到当前订单快照。
 *
 * 该结果只供展示。自动化、置顶和高亮必须继续消费 Worker 的原始 current 结果。
 */
export function buildOrderRecommendationPresentation({
  orders,
  recommendations,
  recommendationIssues,
  pending,
  isCurrent,
  resultContextSignature,
  currentContextSignature,
  error,
  retainedAfterError,
}: BuildOrderRecommendationPresentationOptions): OrderRecommendationPresentation {
  const contextMatches = resultContextSignature.length > 0
    && resultContextSignature === currentContextSignature;
  if (!contextMatches) {
    return {
      recommendations: [],
      recommendationIssues: [],
      pendingOrders: [...orders],
      updating: orders.length > 0,
      updateError: null,
    };
  }
  const updating = orders.length > 0 && (pending || (!isCurrent && !retainedAfterError));

  const currentOrdersByIdentity = groupOrdersByIdentity(orders);
  const recommendationByIdentity = groupRowsByIdentity(recommendations, (item) => item.order);
  const issueByIdentity = groupRowsByIdentity(recommendationIssues, (issue) => issue.order);
  const visibleRecommendations: OrderRecommendation[] = [];
  const visibleIssues: RecommendationIssue[] = [];
  const pendingOrders: NightBusinessOrder[] = [];

  for (const order of orders) {
    const identity = buildOrderDemandIdentity(order);
    if ((currentOrdersByIdentity.get(identity)?.length ?? 0) !== 1) {
      if (updating) pendingOrders.push(order);
      continue;
    }

    const recommendationRows = recommendationByIdentity.get(identity) ?? [];
    const issueRows = issueByIdentity.get(identity) ?? [];
    if (recommendationRows.length === 1 && issueRows.length === 0) {
      visibleRecommendations.push({
        ...recommendationRows[0],
        order,
      });
      continue;
    }
    if (issueRows.length === 1 && recommendationRows.length === 0) {
      visibleIssues.push({
        ...issueRows[0],
        order,
      });
      continue;
    }
    if (updating) pendingOrders.push(order);
  }

  return {
    recommendations: visibleRecommendations,
    recommendationIssues: visibleIssues,
    pendingOrders,
    updating,
    updateError: retainedAfterError && error && orders.length > 0 ? error : null,
  };
}

/**
 * 把展示行绑定到同一份权威 participation resolution，并严格按公开队列位置排列。
 *
 * 特殊经营硬 lane 只改变 operational 选择，不改写玩家在稀客队列中建立的展示顺序；
 * 非正位置、未对齐或暂停行一律不进入结果，也不由前端压缩或补造位置。
 */
export function buildParticipatingRareOrderPresentationRows<
  T extends { order: NightBusinessOrder },
>(
  rows: readonly T[],
  resolveParticipation: (
    order: NightBusinessOrder,
  ) => RareOrderParticipationResolution | null,
): Array<T & { participation: RareOrderParticipationResolution }> {
  return rows.flatMap((row, originalIndex) => {
    const participation = resolveParticipation(row.order);
    const queuePosition = participation?.queuePosition ?? null;
    if (!participation?.configurationAligned
      || !participation.operationallyParticipating
      || !Number.isSafeInteger(queuePosition)
      || queuePosition === null
      || queuePosition <= 0) {
      return [];
    }
    return [{
      row: { ...row, participation },
      queuePosition,
      originalIndex,
    }];
  }).sort((left, right) => (
    left.queuePosition - right.queuePosition
    || left.originalIndex - right.originalIndex
  )).map(({ row }) => row);
}

export function buildOrderDemandIdentity(order: NightBusinessOrder): string {
  const traceId = order.traceId?.trim() ?? '';
  return JSON.stringify([
    traceId || null,
    order.deskCode,
    order.guestId ?? null,
    order.runtimeGuestId ?? null,
    order.specialBusinessRole?.trim() ?? '',
    order.foodTagId ?? null,
    order.foodTagId == null ? order.foodTag ?? null : null,
    order.beverageTagId ?? null,
    order.beverageTagId == null ? order.beverageTag ?? null : null,
    order.hasServedFood === true,
    order.hasServedBeverage === true,
    order.isFreeOrder === true,
    traceId ? null : order.firstSeenAtUtc ?? null,
  ]);
}

function groupOrdersByIdentity(
  orders: readonly NightBusinessOrder[],
): Map<string, NightBusinessOrder[]> {
  return groupRowsByIdentity(orders, (order) => order);
}

function groupRowsByIdentity<T>(
  rows: readonly T[],
  getOrder: (row: T) => NightBusinessOrder,
): Map<string, T[]> {
  const grouped = new Map<string, T[]>();
  for (const row of rows) {
    const identity = buildOrderDemandIdentity(getOrder(row));
    const existing = grouped.get(identity);
    if (existing) {
      existing.push(row);
    } else {
      grouped.set(identity, [row]);
    }
  }
  return grouped;
}
