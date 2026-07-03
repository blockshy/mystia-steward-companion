import {
  didCompleteStep,
  emptyNormalAutoOrderState,
  getAutomationStepLabel,
  isAutomationTimestampStale,
  type AutoFirstOrderState,
  type NormalAutoOrderState,
  type OrderPreparationResponse,
  type RareAutomationBeverageTarget,
  type RareAutomationRecipeTarget,
} from '@/companion/automation-state';
import {
  buildAutomationCookerCapacity,
  getCookerSlotCapacity,
  getNormalCookerRequirement,
  getRareCookerRequirement,
  resolveCookerTypeId,
} from '@/companion/domain/cookers';
import {
  findBeverageFavorite,
  findRecipeFavorite,
  normalizeIdList,
} from '@/companion/domain/favorites';
import { toRareRecipeResult } from '@/companion/domain/service-recommendations';
import {
  sortNightOrderRows,
  sortNormalOrders,
} from '@/companion/domain/sorting';
import { formatDesk } from '@/companion/formatters';
import type { CompanionPreferences, ServiceOrderSortMode } from '@/companion/preferences';
import type {
  AutomationCookerCycle,
  AutomationCookerResourceRow,
  AutomationResourceOverview,
  CookerRequirement,
  CookerReservationResult,
  FavoriteBeverageEntry,
  FavoriteData,
  FavoriteRecipeEntry,
  GameUiPinningTarget,
  NightBusinessOrder,
  NormalAutoOrderDiagnostic,
  NormalBusinessOrder,
  NormalCookerDemand,
  OrderRecommendation,
  RareAutoOrderDiagnostic,
  RecommendationStateSnapshot,
} from '@/companion/types';
import {
  DEFAULT_RECOMMENDATION_DATA,
  buildRecommendationDataIndexes,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type { RareBeverageRecommendation, RareOrderRecommendationPlan, RareRecipeRecommendation } from '@/recommendation-engine';

const NORMAL_AUTO_RECOVERABLE_PAUSE_RETRY_MS = 10000;
const DIRECT_DELIVERY_RETRY_WAIT_MS = 90000;
const DEFAULT_DATA_INDEXES = buildRecommendationDataIndexes(DEFAULT_RECOMMENDATION_DATA);

type OrderPreparationSelection =
  | {
      ok: true;
      item: OrderRecommendation;
      recipe: RareRecipeRecommendation | null;
      beverage: RareBeverageRecommendation | null;
      recipeTarget: RareAutomationRecipeTarget | null;
      beverageTarget: RareAutomationBeverageTarget | null;
      recipeFavorite: FavoriteRecipeEntry | null;
      beverageFavorite: FavoriteBeverageEntry | null;
    }
  | {
      ok: false;
      message: string;
    };

export type ValidOrderPreparationSelection = Extract<OrderPreparationSelection, { ok: true }>;

/**
 * 估算普客自动化本轮会占用的厨具需求。
 *
 * 稀客自动化在预约厨具时会先让出普客已经需要的容量，避免同一轮里两套自动化抢同一个灶台。
 */
export function buildNormalCookerDemand(
  orders: NormalBusinessOrder[],
  states: Map<string, NormalAutoOrderState>,
  preferences: CompanionPreferences,
  runtime: RecommendationStateSnapshot | null | undefined,
  now: number,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): NormalCookerDemand {
  const counts = new Map<string, number>();
  const labels = new Map<string, string[]>();
  if (!preferences.automationEnabled || !preferences.autoNormalOrderEnabled || !preferences.autoNormalStartCooking) {
    return { counts, labels };
  }

  const capacity = buildAutomationCookerCapacity(runtime);
  let reservedOrders = 0;
  for (const order of sortNormalOrders(orders).filter((item) => !item.hasEvaluated)) {
    const state = states.get(buildNormalAutoOrderKey(order));
    if (!shouldAttemptNormalCooking(order, state, preferences, now)) continue;

    const cooker = getNormalCookerRequirement(order, data);
    if (!cooker) continue;

    const limit = getCookerSlotCapacity(cooker.key, capacity);
    const used = counts.get(cooker.key) ?? 0;
    if (used >= limit) continue;

    counts.set(cooker.key, used + 1);
    const items = labels.get(cooker.key) ?? [];
    items.push(`桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`);
    labels.set(cooker.key, items);
    reservedOrders += 1;
    if (reservedOrders >= preferences.autoNormalConcurrency) break;
  }

  return { counts, labels };
}

/**
 * 构建自动化资源占用概览。
 *
 * UI 通过该结果展示本轮预计占用的厨具槽位，便于解释订单为什么等待。
 */
export function buildAutomationResourceOverview({
  runtime,
  recommendations,
  favorites,
  preferences,
  normalOrders,
  rareDiagnostics,
  normalDiagnostics,
  data,
}: {
  runtime: RecommendationStateSnapshot | null;
  recommendations: OrderRecommendation[];
  favorites: FavoriteData;
  preferences: CompanionPreferences;
  normalOrders: NormalBusinessOrder[];
  rareDiagnostics: RareAutoOrderDiagnostic[];
  normalDiagnostics: NormalAutoOrderDiagnostic[];
  data: RecommendationDataSet;
}): AutomationResourceOverview {
  if (!preferences.automationEnabled) {
    return { cookers: [] };
  }

  const capacity = buildAutomationCookerCapacity(runtime);
  const cookerRows = new Map<string, AutomationCookerResourceRow>();
  for (const [key, count] of capacity.entries()) {
    ensureCookerResourceRow(cookerRows, key, key, count);
  }

  const normalDiagnosticByKey = new Map(normalDiagnostics.map((item) => [item.orderKey, item]));
  if (preferences.autoNormalOrderEnabled && preferences.autoNormalStartCooking) {
    let normalReserved = 0;
    for (const order of sortNormalOrders(normalOrders).filter((item) => !item.hasEvaluated)) {
      if (normalReserved >= preferences.autoNormalConcurrency) break;
      const diagnostic = normalDiagnosticByKey.get(buildNormalAutoOrderKey(order));
      if (diagnostic?.prepared || diagnostic?.collected || diagnostic?.paused || diagnostic?.hasServedFood) continue;
      const cooker = getNormalCookerRequirement(order, data);
      if (!cooker) continue;
      const row = ensureCookerResourceRow(cookerRows, cooker.key, cooker.label, getCookerSlotCapacity(cooker.key, capacity));
      if (row.normalReserved + row.rareReserved >= row.capacity) continue;
      row.normalReserved += 1;
      row.labels.push(`普客 桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`);
      normalReserved += 1;
    }
  }

  const rareDiagnosticByKey = new Map(rareDiagnostics.map((item) => [item.orderKey, item]));
  if (preferences.autoPrepStartCooking) {
    const candidates = selectOrderPreparationCandidates(
      recommendations,
      favorites,
      preferences,
      preferences.autoRareConcurrency,
      new Map(),
    );
    for (const selection of candidates.selections) {
      const diagnostic = rareDiagnosticByKey.get(buildAutoOrderKey(selection.item));
      if (diagnostic?.prepared || diagnostic?.hasServedFood || diagnostic?.paused) continue;
      const cooker = getRareCookerRequirement(selection.recipeTarget);
      if (!cooker) continue;
      const row = ensureCookerResourceRow(cookerRows, cooker.key, cooker.label, getCookerSlotCapacity(cooker.key, capacity));
      if (row.normalReserved + row.rareReserved >= row.capacity) continue;
      row.rareReserved += 1;
      row.labels.push(`稀客 ${selection.item.order.guestName || '未知'} · 桌 ${formatDesk(selection.item.order.deskCode)}`);
    }
  }

  return {
    cookers: [...cookerRows.values()]
      .filter((row) => row.normalReserved + row.rareReserved > 0)
      .sort((left, right) => left.label.localeCompare(right.label, 'zh-CN')),
  };
}

/**
 * 判断普客订单是否已经拥有可送达料理。
 *
 * 游戏快照是直接送达后的事实来源，避免前端本地状态短暂落后时重复处理同一订单。
 */
export function isNormalOrderCollected(order: NormalBusinessOrder, state: NormalAutoOrderState | undefined): boolean {
  if (state?.collected) return true;
  return Boolean(order.hasServedFood);
}

/**
 * 将普客自动化本地状态与最新 Mod 快照同步。
 *
 * 快照是最终事实来源：如果游戏已经显示送达、可评价或已评价，就推进本地状态并重置重试计数。
 */
export function syncNormalOrderStateWithSnapshot(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  now: number,
  preferences: CompanionPreferences,
): NormalAutoOrderState | undefined {
  const snapshotCollected = isNormalOrderCollected(order, state);
  const snapshotFoodDelivered = order.hasServedFood;
  const snapshotBeverageDelivered = order.hasServedBeverage;
  const snapshotReadyToEvaluate = order.readyToEvaluate;
  const snapshotCompleted = order.hasEvaluated;
  if (!snapshotCollected && !snapshotFoodDelivered && !snapshotBeverageDelivered && !snapshotReadyToEvaluate && !snapshotCompleted) return state;

  const base = state ?? emptyNormalAutoOrderState(buildNormalAutoOrderKey(order), now);
  const collected = base.collected;
  const foodDelivered = base.foodDelivered || snapshotFoodDelivered;
  const beverageHandled = base.beverageHandled || snapshotBeverageDelivered;
  const completed = base.completed || snapshotCompleted;
  const prepared = base.prepared || collected || foodDelivered;
  let step = base.step;
  if (completed) {
    step = 'done';
  } else if (snapshotReadyToEvaluate && preferences.autoNormalCompleteOrder) {
    step = 'complete-order';
  } else if (foodDelivered && !beverageHandled && preferences.autoNormalTakeBeverage) {
    step = 'ensure-beverage';
  } else if (base.prepared && !foodDelivered) {
    step = 'deliver-food';
  }

  const madeProgress = prepared !== base.prepared
    || collected !== base.collected
    || foodDelivered !== base.foodDelivered
    || beverageHandled !== base.beverageHandled
    || completed !== base.completed
    || step !== base.step;

  return {
    ...base,
    prepared,
    preparedAtMs: prepared && base.preparedAtMs <= 0 ? now : base.preparedAtMs,
    beverageHandled,
    beverageHandledAtMs: beverageHandled && base.beverageHandledAtMs <= 0 ? now : base.beverageHandledAtMs,
    collected,
    foodDelivered,
    foodDeliveredAtMs: foodDelivered && base.foodDeliveredAtMs <= 0 ? now : base.foodDeliveredAtMs,
    completed,
    completedAtMs: completed && base.completedAtMs <= 0 ? now : base.completedAtMs,
    step,
    stepStartedAtMs: madeProgress ? now : base.stepStartedAtMs,
    lastProgressAtMs: madeProgress ? now : base.lastProgressAtMs,
    retryCount: madeProgress ? 0 : base.retryCount,
    rollbackCount: madeProgress ? 0 : base.rollbackCount,
    lastError: madeProgress ? '' : base.lastError,
  };
}

/**
 * 判断是否应尝试为普客订单开始料理。
 */
export function shouldAttemptNormalCooking(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalStartCooking) return false;
  if (order.hasServedFood || order.foodId < 0) return false;
  if (isNormalOrderCollected(order, state)) return false;
  if (state?.paused && !isRecoverableNormalPausedState(state, now)) return false;
  return !state?.prepared || isNormalOrderPreparedStale(state, now, preferences);
}

/**
 * 判断是否应尝试为普客订单处理酒水。
 */
export function shouldAttemptNormalBeverage(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalTakeBeverage) return false;
  if (order.hasServedBeverage || order.beverageId < 0) return false;
  if (state?.beverageHandled) return false;
  if (state?.paused && !isRecoverableNormalPausedState(state, now)) return false;
  return true;
}

/**
 * 判断普客订单是否具备触发完成评价的条件。
 */
export function shouldAttemptNormalCompletion(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalCompleteOrder) return false;
  if (order.hasEvaluated || state?.completed) return false;
  if (state?.paused && !isRecoverableNormalPausedState(state, now)) return false;
  const hasFood = order.hasServedFood || state?.foodDelivered;
  const hasBeverage = order.hasServedBeverage || state?.beverageHandled;
  return Boolean(order.readyToEvaluate || (hasFood && hasBeverage));
}

/**
 * 在单轮自动化中预约一个厨具槽位。
 *
 * 预约只影响前端本轮选择，不修改游戏状态；真正厨具占用由 Mod 开火后产生。
 */
export function reserveAutomationCookerSlot(
  cycle: AutomationCookerCycle,
  cooker: CookerRequirement | null,
  label: string,
  capacity: Map<string, number>,
): CookerReservationResult {
  if (!cooker) return { ok: true, message: '' };
  const limit = getCookerSlotCapacity(cooker.key, capacity);
  const used = cycle.used.get(cooker.key) ?? 0;
  if (used >= limit) {
    const owners = cycle.labels.get(cooker.key) ?? [];
    return {
      ok: false,
      message: `等待厨具 ${cooker.label}：本轮可用容量 ${limit} 已预约${owners.length > 0 ? `（${owners.join('、')}）` : ''}。`,
    };
  }

  cycle.used.set(cooker.key, used + 1);
  cycle.labels.set(cooker.key, [...(cycle.labels.get(cooker.key) ?? []), label]);
  return { ok: true, message: '' };
}

/**
 * 为稀客订单预约厨具槽位，并尊重普客本轮预留需求。
 */
export function reserveRareCookerSlot(
  cycle: AutomationCookerCycle,
  cooker: CookerRequirement | null,
  label: string,
  capacity: Map<string, number>,
  normalDemand: NormalCookerDemand,
): CookerReservationResult {
  if (!cooker) return { ok: true, message: '' };
  const limit = getCookerSlotCapacity(cooker.key, capacity);
  const used = cycle.used.get(cooker.key) ?? 0;
  const normalReserved = normalDemand.counts.get(cooker.key) ?? 0;
  if (normalReserved > 0 && used + normalReserved >= limit) {
    const normalLabels = normalDemand.labels.get(cooker.key) ?? [];
    return {
      ok: false,
      message: `等待厨具 ${cooker.label}：本轮优先给普客订单使用${normalLabels.length > 0 ? `（${normalLabels.join('、')}）` : ''}。`,
    };
  }

  return reserveAutomationCookerSlot(cycle, cooker, label, capacity);
}

/**
 * 从当前稀客订单推荐中选择可执行的自动化候选。
 *
 * 选择时会结合收藏限定、已锁定目标、推荐兜底方案和自动化并发上限，返回可执行项以及跳过原因。
 */
export function selectOrderPreparationCandidates(
  recommendations: OrderRecommendation[],
  favorites: FavoriteData,
  preferences: CompanionPreferences,
  limit: number,
  states: ReadonlyMap<string, AutoFirstOrderState>,
): { selections: ValidOrderPreparationSelection[]; messages: string[]; message: string } {
  const rows = sortNightOrderRows(
    recommendations.map((item) => ({ order: item.order, item })),
    preferences.serviceOrderSortMode,
  );
  if (rows.length === 0) {
    return { selections: [], messages: [], message: '暂无可准备的稀客订单。' };
  }

  const selections: ValidOrderPreparationSelection[] = [];
  const messages: string[] = [];
  for (const row of rows) {
    const item = row.item;
    const label = formatRareAutomationPrefix(item);
    const state = states.get(buildAutoOrderKey(item));
    const planPick = pickPlanForPreparation(item, favorites, preferences);
    const recipeTarget = state?.recipeTarget ?? (planPick.recipe
      ? buildRareRecipeTarget(item, planPick.recipe, planPick.recipeFavorite, planPick.preferenceFallback)
      : null);
    const beverageTarget = state?.beverageTarget ?? (planPick.beverage
      ? buildRareBeverageTarget(planPick.beverage, planPick.beverageFavorite)
      : null);

    if (!recipeTarget && preferences.autoPrepStartCooking) {
      messages.push(`${label}\n${preferences.autoPrepRecipeFavoritesOnly ? '没有匹配的收藏料理。' : '没有可用的推荐料理。'}`);
      continue;
    }
    if (!beverageTarget && preferences.autoPrepTakeBeverage) {
      messages.push(`${label}\n${preferences.autoPrepBeverageFavoritesOnly ? '没有匹配的收藏酒水。' : '没有可用的推荐酒水。'}`);
      continue;
    }

    selections.push({
      ok: true,
      item,
      recipe: planPick.recipe,
      beverage: planPick.beverage,
      recipeTarget,
      beverageTarget,
      recipeFavorite: planPick.recipeFavorite,
      beverageFavorite: planPick.beverageFavorite,
    });
    if (selections.length >= limit) break;
  }

  return {
    selections,
    messages,
    message: selections.length > 0 ? '' : messages[0] ?? '当前稀客订单没有可执行的自动化候选。',
  };
}

/**
 * 锁定一笔稀客订单的自动化料理和酒水目标。
 *
 * 锁定后即使推荐列表因库存或快照刷新重新排序，也继续处理最初选择的目标，避免自动化中途换菜。
 */
export function lockRareAutomationTargets(
  state: AutoFirstOrderState,
  selection: ValidOrderPreparationSelection,
): AutoFirstOrderState {
  const recipeTarget = state.recipeTarget ?? selection.recipeTarget;
  const beverageTarget = state.beverageTarget ?? selection.beverageTarget;
  if (recipeTarget === state.recipeTarget && beverageTarget === state.beverageTarget) return state;

  return {
    ...state,
    recipeTarget,
    beverageTarget,
  };
}

/**
 * 构建发送给 Mod 的游戏内目标厨具/材料高亮目标。
 *
 * 目标总是来自当前排序后的第一笔稀客订单，签名包含订单、料理、材料、酒水和厨具，便于 Mod 判断是否需要更新高亮。
 */
export function buildGameUiPinningTarget(
  recommendations: OrderRecommendation[],
  orderSortMode: ServiceOrderSortMode,
  indexes: ReturnType<typeof buildRecommendationDataIndexes> = DEFAULT_DATA_INDEXES,
): GameUiPinningTarget | null {
  const item = sortNightOrderRows(
    recommendations.map((recommendation) => ({ order: recommendation.order, recommendation })),
    orderSortMode,
  )[0]?.recommendation;
  if (!item) return null;
  const recipe = item.recipes[0] ?? null;
  const beverage = item.beverages[0] ?? null;
  if (!recipe && !beverage) return null;

  const baseIngredientIds = recipe
    ? recipe.recipe.ingredients
      .map((name) => indexes.ingredientByName.get(name)?.id ?? -1)
      .filter((id) => id >= 0)
    : [];
  const ingredientIds = normalizeIdList([
    ...baseIngredientIds,
    ...(recipe?.extraIngredients.map((ingredient) => ingredient.id) ?? []),
  ]);
  const recipeId = recipe?.recipe.id ?? -1;
  const beverageId = beverage?.beverage.id ?? -1;
  const cookerName = recipe?.recipe.cooker ?? '';
  const cookerTypeId = resolveCookerTypeId(cookerName);

  return {
    signature: [
      item.order.firstSeenAtUtc ?? item.order.lastSeenAtUtc ?? '',
      item.order.deskCode,
      item.order.guestId ?? item.order.guestName,
      recipeId,
      ingredientIds.join(','),
      beverageId,
      cookerTypeId,
    ].join('|'),
    recipeId,
    recipeName: recipe?.recipe.name ?? '',
    ingredientIds,
    beverageId,
    beverageName: beverage?.beverage.name ?? '',
    cookerTypeId,
    cookerName,
  };
}

/**
 * 构建“直接完成订单”动作需要的临时偏好。
 */
export function buildCompleteOrderPreferences(preferences: CompanionPreferences): CompanionPreferences {
  return {
    ...preferences,
    autoPrepCompleteOrder: true,
    autoPrepTakeBeverage: true,
    autoPrepStartCooking: true,
    autoPrepCollectCooking: true,
  };
}

/**
 * 判断稀客自动化是否至少启用了一个动作。
 */
export function hasAutomationActionEnabled(preferences: CompanionPreferences): boolean {
  return preferences.autoPrepCompleteOrder
    || preferences.autoPrepTakeBeverage
    || preferences.autoPrepStartCooking
    || preferences.autoPrepCollectCooking;
}

export function hasNormalOrderActionEnabled(preferences: CompanionPreferences): boolean {
  return preferences.autoNormalTakeBeverage
    || preferences.autoNormalStartCooking
    || preferences.autoNormalDeliverFood
    || preferences.autoNormalCompleteOrder;
}

/**
 * 构建稀客自动化状态键。
 *
 * 键中包含首次出现时间、桌号、稀客和 Tag，尽量避免同桌后续新订单复用旧状态。
 */
export function buildAutoOrderKey(item: OrderRecommendation): string {
  const order = item.order;
  return [
    order.firstSeenAtUtc ?? order.lastSeenAtUtc ?? '',
    order.deskCode,
    order.guestId ?? order.guestName,
    order.foodTag,
    order.beverageTag,
    order.isFreeOrder ? 'free' : 'paid',
  ].join('|');
}

/**
 * 构建夜间稀客订单快照键。
 */
export function buildNightBusinessOrderKey(order: NightBusinessOrder): string {
  return [
    order.firstSeenAtUtc ?? order.lastSeenAtUtc ?? '',
    order.deskCode,
    order.guestId ?? order.guestName,
    order.foodTagId,
    order.foodTag,
    order.beverageTagId,
    order.beverageTag,
    order.source,
    order.isFreeOrder ? 'free' : 'paid',
  ].join('|');
}

export function formatRareAutomationPrefix(item: OrderRecommendation): string {
  const order = item.order;
  return `${order.guestName || '稀客'} · 桌 ${formatDesk(order.deskCode)}\n料理 ${order.foodTag || '无'} / 酒水 ${order.beverageTag || '无'}`;
}

export function buildRareAutoOrderDiagnostic(
  selection: ValidOrderPreparationSelection,
  state: AutoFirstOrderState,
  now: number,
): RareAutoOrderDiagnostic {
  const order = selection.item.order;
  return {
    orderKey: buildAutoOrderKey(selection.item),
    title: `${order.guestName || '稀客'} · 桌 ${formatDesk(order.deskCode)}`,
    foodTag: order.foodTag || '',
    beverageTag: order.beverageTag || '',
    recipeName: formatRareAutomationRecipeName(state.recipeTarget, selection.recipeTarget, selection.recipe),
    beverageName: state.beverageTarget?.beverageName ?? selection.beverageTarget?.beverageName ?? selection.beverage?.beverage.name ?? '',
    stepLabel: getAutomationStepLabel(state.step),
    stepSeconds: state.stepStartedAtMs > 0 ? Math.max(0, Math.round((now - state.stepStartedAtMs) / 1000)) : 0,
    nextAction: getRareAutomationNextAction(state),
    retryCount: state.retryCount,
    rollbackCount: state.rollbackCount,
    lastError: state.lastError,
    prepared: state.prepared || Boolean(order.hasServedFood),
    beverageHandled: state.beverageHandled || Boolean(order.hasServedBeverage),
    hasServedFood: Boolean(order.hasServedFood),
    hasServedBeverage: Boolean(order.hasServedBeverage),
    paused: state.paused,
  };
}

/**
 * 构建普客自动化诊断行。
 */
export function buildNormalAutoOrderDiagnostics(
  orders: NormalBusinessOrder[],
  states: Map<string, NormalAutoOrderState>,
  now: number,
): NormalAutoOrderDiagnostic[] {
  return sortNormalOrders(orders)
    .filter((order) => !order.hasEvaluated)
    .map((order) => {
      const orderKey = buildNormalAutoOrderKey(order);
      const state = states.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, now);
      return buildNormalAutoOrderDiagnostic(order, state, now);
    });
}

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

/**
 * 构建普客订单快照签名，用于在订单状态变化时立即触发自动化复查。
 */
export function buildNormalOrderAutomationSignature(orders: NormalBusinessOrder[]): string {
  return sortNormalOrders(orders)
    .map((order) => [
      buildNormalAutoOrderKey(order),
      order.hasEvaluated ? 'evaluated' : order.readyToEvaluate ? 'ready' : 'open',
      order.hasServedFood ? 'food-served' : 'food-open',
      order.hasServedBeverage ? 'bev-served' : 'bev-open',
      order.canAutomate === false ? 'blocked' : 'runnable',
      order.controllerAvailable === false ? 'controller-missing' : 'controller-ok',
      order.foodId,
      order.beverageId,
      order.deskCode,
    ].join(':'))
    .join('|');
}

/**
 * 判断普客开火后是否等待过久，需要重新确认或重试。
 */
export function isNormalOrderPreparedStale(
  state: NormalAutoOrderState | undefined,
  now: number,
  preferences: CompanionPreferences,
): boolean {
  if (!state?.prepared || state.foodDelivered) return false;
  if (state.paused && !isRecoverableNormalPausedState(state, now)) return false;
  void preferences;
  return isAutomationTimestampStale(state.preparedAtMs, now, DIRECT_DELIVERY_RETRY_WAIT_MS);
}

/**
 * 判断普客暂停状态是否属于可自动恢复的临时失败。
 */
export function isRecoverableNormalPausedState(state: NormalAutoOrderState | undefined, now: number): boolean {
  if (!state?.paused) return false;
  if (!state.lastError.includes('目标料理长时间未直接送达')) return false;
  return state.stepStartedAtMs <= 0 || now - state.stepStartedAtMs >= NORMAL_AUTO_RECOVERABLE_PAUSE_RETRY_MS;
}

/**
 * 用订单快照中的已送达字段推进稀客自动化本地状态。
 */
export function syncRareStateWithOrderServedState(
  state: AutoFirstOrderState,
  order: NightBusinessOrder,
  now: number,
): AutoFirstOrderState {
  if (!order.hasServedFood && !order.hasServedBeverage) return state;
  return applyRareServedStateFromResponse(
    state,
    order,
    {
      ok: false,
      prepared: false,
      error: null,
      order: {
        deskCode: order.deskCode,
        guestId: order.guestId,
        guestName: order.guestName,
        foodTag: order.foodTag,
        beverageTag: order.beverageTag,
      },
      recipeId: -1,
      recipeName: '',
      beverageId: -1,
      beverageName: '',
      servedFood: order.hasServedFood,
      servedBeverage: order.hasServedBeverage,
      completedOrder: false,
      steps: [],
    },
    now,
  );
}

/**
 * 根据 Mod 返回的订单准备结果推进稀客自动化状态。
 */
export function applyRareServedStateFromResponse(
  state: AutoFirstOrderState,
  order: NightBusinessOrder,
  response: OrderPreparationResponse,
  now: number,
): AutoFirstOrderState {
  const servedFood = Boolean(response.servedFood)
    || Boolean(order.hasServedFood)
    || didCompleteStep(response, '送达料理');
  const servedBeverage = Boolean(response.servedBeverage)
    || Boolean(order.hasServedBeverage)
    || didCompleteStep(response, '送达酒水');
  if (!servedFood && !servedBeverage) return state;

  const nextPrepared = state.prepared || servedFood;
  const nextBeverageHandled = state.beverageHandled || servedBeverage;
  return {
    ...state,
    prepared: nextPrepared,
    preparedAtMs: nextPrepared && !state.prepared ? now : state.preparedAtMs,
    beverageHandled: nextBeverageHandled,
    beverageHandledAtMs: nextBeverageHandled && !state.beverageHandled ? now : state.beverageHandledAtMs,
    lastProgressAtMs: now,
    step: servedFood && servedBeverage ? 'complete-order' : servedFood ? 'ensure-beverage' : 'ensure-cooking',
    stepStartedAtMs: now,
  };
}

/**
 * 将 Mod 订单处理响应格式化为用户可读的多行文本。
 */
export function formatOrderPreparationResponse(response: OrderPreparationResponse) {
  const title = response.ok
    ? `已处理：${response.order.guestName} · 桌 ${formatDesk(response.order.deskCode)}`
    : `未完成：${response.order.guestName || '当前订单'} · 桌 ${formatDesk(response.order.deskCode)}`;
  const target = [
    response.recipeName ? `料理 ${response.recipeName}` : '',
    response.beverageName ? `酒水 ${response.beverageName}` : '',
  ].filter(Boolean).join(' / ');
  const steps = response.steps.map((step) => {
    const prefix = step.skipped ? '跳过' : step.ok ? '完成' : '失败';
    return `${prefix} ${step.name}：${step.message}`;
  });
  return [title, target, ...steps, response.error ? `错误：${response.error}` : ''].filter(Boolean).join('\n');
}

function ensureCookerResourceRow(
  rows: Map<string, AutomationCookerResourceRow>,
  key: string,
  label: string,
  capacity: number,
): AutomationCookerResourceRow {
  const existing = rows.get(key);
  if (existing) {
    existing.capacity = Math.max(existing.capacity, capacity);
    return existing;
  }

  const row: AutomationCookerResourceRow = {
    key,
    label,
    capacity: Math.max(1, capacity),
    normalReserved: 0,
    rareReserved: 0,
    labels: [],
  };
  rows.set(key, row);
  return row;
}

function buildRareRecipeTarget(
  _item: OrderRecommendation,
  recipe: RareRecipeRecommendation,
  favorite: FavoriteRecipeEntry | null,
  preferenceFallback = false,
): RareAutomationRecipeTarget {
  return {
    recipeId: recipe.recipe.recipeId,
    foodId: recipe.recipe.id,
    recipeName: recipe.recipe.name,
    cookerName: recipe.recipe.cooker,
    extraIngredientIds: recipe.extraIngredients.map((ingredient) => ingredient.id),
    favorite: Boolean(favorite),
    preferenceFallback,
  };
}

function buildRareBeverageTarget(
  beverage: RareBeverageRecommendation,
  favorite: FavoriteBeverageEntry | null,
): RareAutomationBeverageTarget {
  return {
    beverageId: beverage.beverage.id,
    beverageName: beverage.beverage.name,
    favorite: Boolean(favorite),
  };
}

function formatRareAutomationRecipeName(
  stateTarget: RareAutomationRecipeTarget | null,
  selectionTarget: RareAutomationRecipeTarget | null,
  selectedRecipe: RareRecipeRecommendation | null,
): string {
  const target = stateTarget ?? selectionTarget;
  const name = target?.recipeName ?? selectedRecipe?.recipe.name ?? '';
  if (!name) return '';
  return target?.preferenceFallback ? `${name}（喜好备选）` : name;
}

function buildNormalAutoOrderDiagnostic(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState,
  now: number,
): NormalAutoOrderDiagnostic {
  return {
    orderKey: buildNormalAutoOrderKey(order),
    title: `桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`,
    foodName: order.foodName || `#${order.foodId}`,
    beverageName: order.beverageName || `#${order.beverageId}`,
    source: order.source || '',
    stepLabel: getAutomationStepLabel(state.step),
    stepSeconds: state.stepStartedAtMs > 0 ? Math.max(0, Math.round((now - state.stepStartedAtMs) / 1000)) : 0,
    nextAction: getNormalAutomationNextAction(state, now),
    retryCount: state.retryCount,
    rollbackCount: state.rollbackCount,
    lastError: state.lastError,
    prepared: state.prepared || isNormalOrderCollected(order, state),
    beverageHandled: state.beverageHandled || order.hasServedBeverage,
    collected: isNormalOrderCollected(order, state),
    foodDelivered: state.foodDelivered || order.hasServedFood,
    completed: state.completed || order.hasEvaluated,
    paused: state.paused,
    hasServedFood: order.hasServedFood,
    hasServedBeverage: order.hasServedBeverage,
    readyToEvaluate: order.readyToEvaluate,
    hasEvaluated: order.hasEvaluated,
    controllerAvailable: order.controllerAvailable,
    canAutomate: order.canAutomate,
    actionBlockReason: order.actionBlockReason,
  };
}

function getRareAutomationNextAction(state: AutoFirstOrderState): string {
  if (state.paused) return '等待手动重试或订单变化';
  if (state.step === 'complete-order') return '下一轮尝试完成订单';
  if (state.step === 'ensure-beverage') return '下一轮校验酒水送达';
  if (state.step === 'ensure-cooking') return '下一轮校验厨具/开锅';
  if (state.step === 'match-order') return '下一轮匹配订单';
  if (state.step === 'done') return '等待订单从列表移除';
  return '下一轮刷新';
}

function getNormalAutomationNextAction(
  state: NormalAutoOrderState,
  now: number,
): string {
  if (state.paused) {
    if (isRecoverableNormalPausedState(state, now)) return '下一轮自动恢复';
    if (state.lastError.includes('目标料理长时间未直接送达')) {
      return formatRemainingAction(state.stepStartedAtMs, now, NORMAL_AUTO_RECOVERABLE_PAUSE_RETRY_MS, '自动恢复');
    }
    return '等待订单变化或手动处理';
  }
  if (state.completed || state.step === 'done') return '等待订单从列表移除';
  if (state.step === 'complete-order') return '下一轮尝试完成订单';
  if (state.step === 'deliver-food') return formatRemainingAction(state.preparedAtMs, now, DIRECT_DELIVERY_RETRY_WAIT_MS, '直接送达确认');
  if (state.step === 'ensure-beverage') return '下一轮校验酒水';
  if (state.prepared) {
    return formatRemainingAction(state.preparedAtMs, now, DIRECT_DELIVERY_RETRY_WAIT_MS, '直接送达确认');
  }
  if (state.step === 'ensure-cooking') return '下一轮校验厨具/开锅';
  if (state.step === 'match-order') return '下一轮匹配订单';
  return '下一轮刷新';
}

function formatRemainingAction(startedAtMs: number, now: number, timeoutMs: number, label: string): string {
  if (startedAtMs <= 0) return `${label}等待中`;
  const remainingMs = timeoutMs - (now - startedAtMs);
  if (remainingMs <= 0) return `下一轮${label}`;
  return `${label}约 ${Math.ceil(remainingMs / 1000)} 秒`;
}

function pickPlanForPreparation(
  item: OrderRecommendation,
  favorites: FavoriteData,
  preferences: CompanionPreferences,
): {
  recipe: RareRecipeRecommendation | null;
  beverage: RareBeverageRecommendation | null;
  recipeFavorite: FavoriteRecipeEntry | null;
  beverageFavorite: FavoriteBeverageEntry | null;
  preferenceFallback: boolean;
} {
  const needsRecipe = preferences.autoPrepStartCooking;
  const needsBeverage = preferences.autoPrepTakeBeverage;
  if (!needsRecipe && !needsBeverage) {
    return emptyPlanPick();
  }

  const plans = item.executionPlans.length > 0 ? item.executionPlans : [];
  if (plans.length === 0) {
    return emptyPlanPick();
  }

  for (const plan of plans) {
    const recipe = plan.food ? getRecipeRowForPlan(item, plan) : null;
    const beverage = plan.beverage ? getBeverageRowForPlan(item, plan) : null;
    const recipeFavorite = recipe ? findRecipeFavorite(favorites, item.customer.id, item.order.foodTag, recipe) : null;
    const beverageFavorite = beverage ? findBeverageFavorite(favorites, item.customer.id, item.order.beverageTag, beverage) : null;

    if (needsRecipe && !recipe) {
      continue;
    }
    if (needsBeverage && !beverage) {
      continue;
    }
    if (preferences.autoPrepRecipeFavoritesOnly && needsRecipe && !recipeFavorite) {
      continue;
    }
    if (preferences.autoPrepBeverageFavoritesOnly && needsBeverage && !beverageFavorite) {
      continue;
    }

    return {
      recipe: needsRecipe ? recipe : null,
      beverage: needsBeverage ? beverage : null,
      recipeFavorite,
      beverageFavorite,
      preferenceFallback: Boolean(recipe && !recipe.meetsRequiredFood),
    };
  }

  return emptyPlanPick();
}

function emptyPlanPick() {
  return {
    recipe: null,
    beverage: null,
    recipeFavorite: null,
    beverageFavorite: null,
    preferenceFallback: false,
  };
}

function findRecipeRowForPlan(
  item: OrderRecommendation,
  recipeId: number,
  extraIngredientIds: number[],
): RareRecipeRecommendation | null {
  const normalizedExtras = normalizeIdList(extraIngredientIds).join(',');
  return item.recipes.find((recipe) =>
    recipe.recipe.id === recipeId
    && normalizeIdList(recipe.extraIngredients.map((ingredient) => ingredient.id)).join(',') === normalizedExtras
  ) ?? null;
}

function getRecipeRowForPlan(
  item: OrderRecommendation,
  plan: RareOrderRecommendationPlan,
): RareRecipeRecommendation | null {
  if (!plan.food) return null;
  return findRecipeRowForPlan(
    item,
    plan.food.recipe.id,
    plan.food.extraIngredients.map((ingredient) => ingredient.id),
  ) ?? toRareRecipeResult(plan.food);
}

function findBeverageRowForPlan(
  item: OrderRecommendation,
  beverageId: number,
): RareBeverageRecommendation | null {
  return item.beverages.find((beverage) =>
    beverage.beverage.id === beverageId
  ) ?? null;
}

function getBeverageRowForPlan(
  item: OrderRecommendation,
  plan: RareOrderRecommendationPlan,
): RareBeverageRecommendation | null {
  if (!plan.beverage) return null;
  return findBeverageRowForPlan(item, plan.beverage.beverage.id) ?? {
    beverage: plan.beverage.beverage,
    meetsRequiredBev: plan.beverage.meetsRequiredBeverage,
    matchedTags: plan.beverage.matchedTags,
  };
}
