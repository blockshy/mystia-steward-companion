import type {
  NightBusinessOrder,
  OrderRecommendation,
  RecommendationIssue,
} from '@/companion/types';

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
