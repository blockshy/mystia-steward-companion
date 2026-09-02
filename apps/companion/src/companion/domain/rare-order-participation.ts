import type {
  NightBusinessOrder,
  RareGuestParticipationMutationAction,
} from '@/companion/types';
import type { RareCustomerCatalogItem } from '@/lib/catalog-types';

/**
 * Mod 返回的稀客订单参与状态。
 *
 * `automatic` 表示订单未加入调度名单且正常参与；`queued` 表示名单内订单已手动
 * 启用；`paused` 表示它只在稀客队列和诊断中保留，不进入经营推荐，也不会触发新的游戏操作。
 */
export type RareOrderParticipationState = 'automatic' | 'paused' | 'queued';

/**
 * 在状态读取和写请求间传递的订单实例标识。
 *
 * 桌位、标签和游戏内稀客信息只用于展示和诊断，不属于公开的订单实例标识。
 */
export interface RareOrderExactIdentity {
  businessGeneration: number;
  traceId: string;
  orderLifecycleSequence: number;
  guestId: number;
}

/** Mod 当前状态中单笔订单的调度结果。 */
export interface RareOrderParticipationEntryView
  extends Omit<RareOrderExactIdentity, 'businessGeneration'> {
  managed: boolean;
  participating: boolean;
  reasonCode: string;
  queuePosition: number | null;
}

/**
 * 前端领域层所需的最小 Mod 当前状态。
 *
 * 具体 Local API DTO 可以直接满足该结构，也可由 hook 做一次明确适配；组件不自建另一份
 * 参与状态。
 */
export interface RareOrderParticipationSnapshotView {
  active: boolean;
  businessGeneration: number;
  participationRevision: number;
  managedGuestIds: readonly number[];
  entries: readonly RareOrderParticipationEntryView[];
}

export type RareOrderParticipationDisplayState =
  | RareOrderParticipationState
  | 'unavailable';

export interface RareOrderParticipationResolution {
  configuredAsManaged: boolean;
  configurationAligned: boolean;
  displayState: RareOrderParticipationDisplayState;
  operationallyParticipating: boolean;
  exactIdentity: RareOrderExactIdentity | null;
  queuePosition: number | null;
  reason: string;
}

export interface RareOrderParticipationProjection {
  /** 可交给经营推荐、高亮、自动化和资源预约等功能使用的订单。 */
  operationalOrders: readonly NightBusinessOrder[];
  resolutions: readonly RareOrderParticipationResolution[];
}

export interface RareOrderParticipationResolutionIndex {
  byExactIdentityKey: ReadonlyMap<string, RareOrderParticipationResolution>;
  ambiguousExactIdentityKeys: ReadonlySet<string>;
}

export interface ManagedRareOrderRow {
  key: string;
  order: NightBusinessOrder;
  exactIdentity: RareOrderExactIdentity | null;
  state: Extract<RareOrderParticipationDisplayState, 'paused' | 'queued' | 'unavailable'>;
  queuePosition: number | null;
  reason: string;
}

export interface ManagedRareOrderGroup {
  guestId: number;
  guestName: string;
  rows: readonly ManagedRareOrderRow[];
  allCurrentTargets: readonly RareOrderExactIdentity[];
  pausedTargets: readonly RareOrderExactIdentity[];
  queuedTargets: readonly RareOrderExactIdentity[];
  hasUnavailableRows: boolean;
  firstQueuePosition: number | null;
}

export interface RareGuestRosterRow {
  guestId: number;
  name: string;
  places: readonly string[];
  dlc: number;
  managed: boolean;
  catalogAvailable: boolean;
}

export interface RareGuestRosterSections {
  managed: readonly RareGuestRosterRow[];
  available: readonly RareGuestRosterRow[];
}

interface ParticipationEntryIndex {
  entries: ReadonlyMap<string, RareOrderParticipationEntryView>;
  queuePositions: ReadonlyMap<string, number>;
  snapshotAvailable: boolean;
}

const EMPTY_ENTRY_INDEX: ParticipationEntryIndex = {
  entries: new Map(),
  queuePositions: new Map(),
  snapshotAvailable: false,
};
const EXACT_RARE_TRACE_PATTERN = /^R-[0-9]{1,16}$/;

/**
 * 从当前订单构造可用于修改请求的订单实例标识。任一关键字段缺失都返回 null，不使用
 * 名称、桌位或标签组合做模糊匹配。
 */
export function buildRareOrderExactIdentity(
  order: NightBusinessOrder,
  businessGeneration: number,
): RareOrderExactIdentity | null {
  const traceId = order.traceId;
  if (typeof traceId !== 'string'
    || !EXACT_RARE_TRACE_PATTERN.test(traceId)
    || !isPositiveSafeInteger(businessGeneration)
    || !isPositiveSafeInteger(order.orderLifecycleSequence)
    || !isNonNegativeSafeInteger(order.guestId)) {
    return null;
  }

  return {
    businessGeneration,
    traceId,
    orderLifecycleSequence: order.orderLifecycleSequence,
    guestId: order.guestId,
  };
}

/** JSON 元组避免 trace 中的分隔符与其他标识字段产生键冲突。 */
export function buildRareOrderExactIdentityKey(identity: RareOrderExactIdentity): string {
  return JSON.stringify([
    identity.businessGeneration,
    identity.traceId,
    identity.orderLifecycleSequence,
    identity.guestId,
  ]);
}

/** UI 与修改 hook 共用的稳定忙碌键；只含规范的范围、操作和标识字段。 */
export function buildRareOrderParticipationMutationKey(
  action: RareGuestParticipationMutationAction,
  target:
    | { type: 'guest'; guestId: number }
    | { type: 'order'; order: RareOrderExactIdentity },
): string {
  return target.type === 'guest'
    ? `guest:${target.guestId}:${action}`
    : `order:${buildRareOrderExactIdentityKey(target.order)}:${action}`;
}

/**
 * 一次生成完整参与集合和逐订单解析结果。订单捕获与后台计算仍保留全部订单，
 * 经营推荐和游戏自动化等功能只使用这里的参与集合。
 */
export function buildRareOrderParticipationProjection({
  orders,
  collectionComplete,
  managedGuestIds,
  businessGeneration,
  snapshot,
}: {
  orders: readonly NightBusinessOrder[];
  collectionComplete: boolean;
  managedGuestIds: readonly number[];
  businessGeneration: number;
  snapshot: RareOrderParticipationSnapshotView | null;
}): RareOrderParticipationProjection {
  if (managedGuestIds.length === 0) {
    return {
      operationalOrders: [...orders],
      resolutions: orders.map(() => ({
        configuredAsManaged: false,
        configurationAligned: true,
        displayState: 'automatic',
        operationallyParticipating: true,
        exactIdentity: null,
        queuePosition: null,
        reason: '',
      })),
    };
  }
  const managedIds = buildGuestIdSet(managedGuestIds);
  const index = isCanonicalGuestIdList(managedGuestIds)
    ? buildParticipationEntryIndex(
        snapshot,
        businessGeneration,
        managedIds,
        orders,
        collectionComplete,
      )
    : EMPTY_ENTRY_INDEX;
  const resolutions = orders.map((order) => resolveRareOrderParticipation(
    order,
    managedIds,
    snapshot,
    index,
  ));

  return {
    operationalOrders: orders.filter((_, orderIndex) => resolutions[orderIndex].operationallyParticipating),
    resolutions,
  };
}

/** 为后台计算返回的推荐行建立订单实例索引；重复标识一律不关联。 */
export function buildRareOrderParticipationResolutionIndex(
  resolutions: readonly RareOrderParticipationResolution[],
): RareOrderParticipationResolutionIndex {
  const byExactIdentityKey = new Map<string, RareOrderParticipationResolution>();
  const ambiguousExactIdentityKeys = new Set<string>();
  for (const resolution of resolutions) {
    if (!resolution.exactIdentity) continue;
    const key = buildRareOrderExactIdentityKey(resolution.exactIdentity);
    if (byExactIdentityKey.has(key)) {
      byExactIdentityKey.delete(key);
      ambiguousExactIdentityKeys.add(key);
      continue;
    }
    if (!ambiguousExactIdentityKeys.has(key)) byExactIdentityKey.set(key, resolution);
  }
  return { byExactIdentityKey, ambiguousExactIdentityKeys };
}

/** 只用规范的订单实例标识把任意推荐或展示订单绑定回调度状态。 */
export function findRareOrderParticipationResolution(
  order: NightBusinessOrder,
  businessGeneration: number,
  index: RareOrderParticipationResolutionIndex,
): RareOrderParticipationResolution | null {
  const identity = buildRareOrderExactIdentity(order, businessGeneration);
  if (!identity) return null;
  const key = buildRareOrderExactIdentityKey(identity);
  if (index.ambiguousExactIdentityKeys.has(key)) return null;
  return index.byExactIdentityKey.get(key) ?? null;
}

/**
 * 按稀客分组当前名单内订单。分组操作仍返回每笔订单的实例标识，后端不需要重新
 * 根据 guest 名称或桌位查找。
 */
export function buildManagedRareOrderGroups({
  orders,
  collectionComplete,
  managedGuestIds,
  businessGeneration,
  snapshot,
}: {
  orders: readonly NightBusinessOrder[];
  collectionComplete: boolean;
  managedGuestIds: readonly number[];
  businessGeneration: number;
  snapshot: RareOrderParticipationSnapshotView | null;
}): readonly ManagedRareOrderGroup[] {
  const managedIds = buildGuestIdSet(managedGuestIds);
  const index = isCanonicalGuestIdList(managedGuestIds)
    ? buildParticipationEntryIndex(
        snapshot,
        businessGeneration,
        managedIds,
        orders,
        collectionComplete,
      )
    : EMPTY_ENTRY_INDEX;
  const groups = new Map<number, { guestName: string; rows: ManagedRareOrderRow[] }>();

  for (const order of orders) {
    if (!isNonNegativeSafeInteger(order.guestId) || !managedIds.has(order.guestId)) continue;
    const resolution = resolveRareOrderParticipation(order, managedIds, snapshot, index);
    const exactKey = resolution.exactIdentity
      ? buildRareOrderExactIdentityKey(resolution.exactIdentity)
      : buildUnavailableOrderKey(order);
    const state = resolution.configurationAligned
      && (resolution.displayState === 'paused' || resolution.displayState === 'queued')
      ? resolution.displayState
      : 'unavailable';
    const row: ManagedRareOrderRow = {
      key: exactKey,
      order,
      exactIdentity: state === 'unavailable' ? null : resolution.exactIdentity,
      state,
      queuePosition: state === 'queued' ? index.queuePositions.get(exactKey) ?? null : null,
      reason: state === 'unavailable'
        ? resolution.reason || '订单调度状态不可用。'
        : resolution.reason,
    };
    const existing = groups.get(order.guestId);
    if (existing) {
      existing.rows.push(row);
      if (!existing.guestName && order.guestName) existing.guestName = order.guestName;
    } else {
      groups.set(order.guestId, {
        guestName: order.guestName || `稀客 #${order.guestId}`,
        rows: [row],
      });
    }
  }

  return [...groups.entries()]
    .map(([guestId, group]): ManagedRareOrderGroup => {
      const rows = [...group.rows].sort(compareManagedRareOrderRows);
      const hasUnavailableRows = rows.some((row) => row.state === 'unavailable');
      return {
        guestId,
        guestName: group.guestName,
        rows,
        allCurrentTargets: hasUnavailableRows
          ? []
          : rows.flatMap((row) => row.exactIdentity ? [row.exactIdentity] : []),
        pausedTargets: hasUnavailableRows
          ? []
          : rows.flatMap((row) => row.state === 'paused' && row.exactIdentity ? [row.exactIdentity] : []),
        queuedTargets: hasUnavailableRows
          ? []
          : rows.flatMap((row) => row.state === 'queued' && row.exactIdentity ? [row.exactIdentity] : []),
        hasUnavailableRows,
        firstQueuePosition: rows.reduce<number | null>(
          (minimum, row) => row.queuePosition === null
            ? minimum
            : minimum === null ? row.queuePosition : Math.min(minimum, row.queuePosition),
          null,
        ),
      };
    })
    .sort(compareManagedRareOrderGroups);
}

/** 生成设置页的已加入名单/可添加两个稳定分组，并保留目录已不存在的已存储 ID。 */
export function buildRareGuestRosterSections({
  customers,
  managedGuestIds,
  query = '',
}: {
  customers: readonly Pick<RareCustomerCatalogItem, 'id' | 'name' | 'places' | 'dlc'>[];
  managedGuestIds: readonly number[];
  query?: string;
}): RareGuestRosterSections {
  const managedIds = buildGuestIdSet(managedGuestIds);
  const catalog = new Map<number, Pick<RareCustomerCatalogItem, 'id' | 'name' | 'places' | 'dlc'>>();
  for (const customer of customers) {
    if (!isNonNegativeSafeInteger(customer.id) || catalog.has(customer.id)) continue;
    catalog.set(customer.id, customer);
  }

  const rows: RareGuestRosterRow[] = [...catalog.values()].map((customer) => ({
    guestId: customer.id,
    name: customer.name || `稀客 #${customer.id}`,
    places: [...customer.places],
    dlc: customer.dlc,
    managed: managedIds.has(customer.id),
    catalogAvailable: true,
  }));
  for (const guestId of managedIds) {
    if (catalog.has(guestId)) continue;
    rows.push({
      guestId,
      name: `稀客 #${guestId}`,
      places: [],
      dlc: 0,
      managed: true,
      catalogAvailable: false,
    });
  }

  const normalizedQuery = query.trim().toLocaleLowerCase();
  const visibleRows = normalizedQuery
    ? rows.filter((row) => buildRosterSearchText(row).includes(normalizedQuery))
    : rows;
  const compareRows = (left: RareGuestRosterRow, right: RareGuestRosterRow) =>
    compareText(left.name, right.name) || left.guestId - right.guestId;

  return {
    managed: visibleRows.filter((row) => row.managed).sort(compareRows),
    available: visibleRows.filter((row) => !row.managed).sort(compareRows),
  };
}

/** 调度名单由上层组件控制；该函数只产生下一个规范值，不在组件内提前保存。 */
export function updateManagedRareGuestIds(
  current: readonly number[],
  guestId: number,
  managed: boolean,
): readonly number[] {
  const next = buildGuestIdSet(current);
  if (!isNonNegativeSafeInteger(guestId)) return [...next].sort((left, right) => left - right);
  if (managed) next.add(guestId);
  else next.delete(guestId);
  return [...next].sort((left, right) => left - right);
}

/** 设置页删除确认所需的当前订单数；仅使用规范的 guestId。 */
export function countCurrentRareOrdersByGuestId(
  orders: readonly NightBusinessOrder[],
): ReadonlyMap<number, number> {
  const counts = new Map<number, number>();
  for (const order of orders) {
    if (!isNonNegativeSafeInteger(order.guestId)) continue;
    counts.set(order.guestId, (counts.get(order.guestId) ?? 0) + 1);
  }
  return counts;
}

function resolveRareOrderParticipation(
  order: NightBusinessOrder,
  managedIds: ReadonlySet<number>,
  snapshot: RareOrderParticipationSnapshotView | null,
  index: ParticipationEntryIndex,
): RareOrderParticipationResolution {
  const configuredAsManaged = isNonNegativeSafeInteger(order.guestId) && managedIds.has(order.guestId);
  const businessGeneration = index.snapshotAvailable ? snapshot!.businessGeneration : 0;
  const exactIdentity = buildRareOrderExactIdentity(order, businessGeneration);
  if (!exactIdentity) {
    return {
      configuredAsManaged,
      configurationAligned: false,
      displayState: configuredAsManaged ? 'unavailable' : 'automatic',
      operationallyParticipating: false,
      exactIdentity: null,
      queuePosition: null,
      reason: '订单缺少追踪编号、订单序号或稀客编号，无法确认具体订单。',
    };
  }

  const key = buildRareOrderExactIdentityKey(exactIdentity);
  const entry = index.entries.get(key);
  if (!entry) {
    return {
      configuredAsManaged,
      configurationAligned: false,
      displayState: configuredAsManaged ? 'unavailable' : 'automatic',
      operationallyParticipating: false,
      exactIdentity,
      queuePosition: null,
      reason: index.snapshotAvailable
        ? 'Mod 当前调度状态中没有这笔订单。'
        : 'Mod 当前调度状态尚不可用。',
    };
  }

  const configurationAligned = entry.managed === configuredAsManaged;
  const entryStateValid = isParticipationEntryStateValid(entry);
  if (!entryStateValid) {
    return {
      configuredAsManaged,
      configurationAligned: false,
      displayState: 'unavailable',
      operationallyParticipating: false,
      exactIdentity,
      queuePosition: null,
      reason: 'Mod 返回的调度状态或队列位置无效。',
    };
  }

  const entryState = getParticipationEntryState(entry);
  const stateMatchesManagement = entry.managed
    ? entryState !== 'automatic'
    : entryState === 'automatic';
  const authorityAligned = configurationAligned && stateMatchesManagement;
  return {
    configuredAsManaged,
    configurationAligned: authorityAligned,
    displayState: entryState,
    operationallyParticipating: authorityAligned && entry.participating,
    exactIdentity,
    queuePosition: entry.participating ? entry.queuePosition : null,
    reason: authorityAligned ? '' : '调度名单与 Mod 当前状态尚未同步一致。',
  };
}

function buildParticipationEntryIndex(
  snapshot: RareOrderParticipationSnapshotView | null,
  expectedBusinessGeneration: number,
  expectedManagedIds: ReadonlySet<number>,
  orders: readonly NightBusinessOrder[],
  collectionComplete: boolean,
): ParticipationEntryIndex {
  if (!collectionComplete
    || !snapshot
    || snapshot.active !== true
    || !isPositiveSafeInteger(snapshot.businessGeneration)
    || snapshot.businessGeneration !== expectedBusinessGeneration
    || !isPositiveSafeInteger(snapshot.participationRevision)
    || !isCanonicalGuestIdList(snapshot.managedGuestIds)
    || !sameGuestIdSet(snapshot.managedGuestIds, expectedManagedIds)
    || !Array.isArray(snapshot.entries)
    || snapshot.entries.length > 512
    || orders.length > 512) {
    return EMPTY_ENTRY_INDEX;
  }

  const currentOrderKeys = new Set<string>();
  for (const order of orders) {
    const identity = buildRareOrderExactIdentity(order, expectedBusinessGeneration);
    if (!identity) return EMPTY_ENTRY_INDEX;
    const key = buildRareOrderExactIdentityKey(identity);
    if (currentOrderKeys.has(key)) return EMPTY_ENTRY_INDEX;
    currentOrderKeys.add(key);
  }

  const entries = new Map<string, RareOrderParticipationEntryView>();
  const queuePositionsSeen = new Set<number>();
  for (const entry of snapshot.entries) {
    const identity = buildEntryExactIdentity(entry, snapshot.businessGeneration);
    if (!identity
      || !isParticipationEntryStateValid(entry)
      || entry.managed !== expectedManagedIds.has(identity.guestId)
      || entry.reasonCode.trim().length === 0) {
      return EMPTY_ENTRY_INDEX;
    }
    const key = buildRareOrderExactIdentityKey(identity);
    if (entries.has(key) || !currentOrderKeys.has(key)) return EMPTY_ENTRY_INDEX;
    if (entry.participating) {
      if (queuePositionsSeen.has(entry.queuePosition!)) return EMPTY_ENTRY_INDEX;
      queuePositionsSeen.add(entry.queuePosition!);
    }
    entries.set(key, entry);
  }
  if (entries.size !== currentOrderKeys.size) return EMPTY_ENTRY_INDEX;
  for (let expectedPosition = 1; expectedPosition <= queuePositionsSeen.size; expectedPosition += 1) {
    if (!queuePositionsSeen.has(expectedPosition)) return EMPTY_ENTRY_INDEX;
  }

  const queuePositions = new Map<string, number>();
  for (const [key, entry] of entries) {
    if (entry.participating) queuePositions.set(key, entry.queuePosition!);
  }

  return {
    entries,
    queuePositions,
    snapshotAvailable: true,
  };
}

/** 供修改响应复用的完整状态一致性检查。 */
export function isRareOrderParticipationSnapshotAligned({
  snapshot,
  businessGeneration,
  managedGuestIds,
  orders,
  collectionComplete,
}: {
  snapshot: RareOrderParticipationSnapshotView | null;
  businessGeneration: number;
  managedGuestIds: readonly number[];
  orders: readonly NightBusinessOrder[];
  collectionComplete: boolean;
}): boolean {
  if (!snapshot || !isCanonicalGuestIdList(managedGuestIds)) return false;
  const expectedManagedIds = buildGuestIdSet(managedGuestIds);
  return buildParticipationEntryIndex(
    snapshot,
    businessGeneration,
    expectedManagedIds,
    orders,
    collectionComplete,
  ).snapshotAvailable;
}

function buildEntryExactIdentity(
  entry: RareOrderParticipationEntryView,
  businessGeneration: number,
): RareOrderExactIdentity | null {
  if (entry == null || typeof entry !== 'object' || Array.isArray(entry)) return null;
  const traceId = entry.traceId;
  if (typeof traceId !== 'string'
    || !EXACT_RARE_TRACE_PATTERN.test(traceId)
    || !isPositiveSafeInteger(entry.orderLifecycleSequence)
    || !isNonNegativeSafeInteger(entry.guestId)) {
    return null;
  }
  return {
    businessGeneration,
    traceId,
    orderLifecycleSequence: entry.orderLifecycleSequence,
    guestId: entry.guestId,
  };
}

function isParticipationEntryStateValid(entry: RareOrderParticipationEntryView): boolean {
  if (entry == null
    || typeof entry !== 'object'
    || Array.isArray(entry)
    || typeof entry.managed !== 'boolean'
    || typeof entry.participating !== 'boolean'
    || typeof entry.reasonCode !== 'string') return false;
  return entry.participating
    ? isPositiveSafeInteger(entry.queuePosition)
    : entry.managed && entry.queuePosition === null;
}

function getParticipationEntryState(
  entry: RareOrderParticipationEntryView,
): RareOrderParticipationState {
  if (!entry.participating) return 'paused';
  return entry.managed ? 'queued' : 'automatic';
}

function compareManagedRareOrderRows(left: ManagedRareOrderRow, right: ManagedRareOrderRow): number {
  if (left.queuePosition !== null || right.queuePosition !== null) {
    if (left.queuePosition === null) return 1;
    if (right.queuePosition === null) return -1;
    if (left.queuePosition !== right.queuePosition) return left.queuePosition - right.queuePosition;
  }
  if (left.state !== right.state) {
    if (left.state === 'paused') return -1;
    if (right.state === 'paused') return 1;
  }
  const seenComparison = compareText(
    left.order.firstSeenAtUtc ?? left.order.lastSeenAtUtc ?? '',
    right.order.firstSeenAtUtc ?? right.order.lastSeenAtUtc ?? '',
  );
  return seenComparison
    || left.order.orderLifecycleSequence - right.order.orderLifecycleSequence
    || compareText(left.key, right.key);
}

function compareManagedRareOrderGroups(
  left: ManagedRareOrderGroup,
  right: ManagedRareOrderGroup,
): number {
  if (left.firstQueuePosition !== null || right.firstQueuePosition !== null) {
    if (left.firstQueuePosition === null) return 1;
    if (right.firstQueuePosition === null) return -1;
    if (left.firstQueuePosition !== right.firstQueuePosition) {
      return left.firstQueuePosition - right.firstQueuePosition;
    }
  }
  return compareText(left.guestName, right.guestName) || left.guestId - right.guestId;
}

function buildGuestIdSet(ids: readonly number[]): Set<number> {
  return new Set(ids.filter(isNonNegativeSafeInteger));
}

function isCanonicalGuestIdList(ids: readonly number[]): boolean {
  if (!Array.isArray(ids) || ids.length > 512) return false;
  let previous = -1;
  for (const guestId of ids) {
    if (!isNonNegativeSafeInteger(guestId) || guestId <= previous) return false;
    previous = guestId;
  }
  return true;
}

function sameGuestIdSet(ids: readonly number[], expected: ReadonlySet<number>): boolean {
  return ids.length === expected.size && ids.every((guestId) => expected.has(guestId));
}

function buildRosterSearchText(row: RareGuestRosterRow): string {
  return [
    row.name,
    row.guestId,
    ...row.places,
    row.dlc === 0 ? '本体' : `dlc${row.dlc}`,
  ].join(' ').toLocaleLowerCase();
}

function buildUnavailableOrderKey(order: NightBusinessOrder): string {
  return JSON.stringify([
    'unavailable',
    order.traceId ?? '',
    order.orderLifecycleSequence,
    order.deskCode,
    order.guestId,
    order.runtimeGuestId,
    order.foodTagId,
    order.beverageTagId,
    order.firstSeenAtUtc ?? order.lastSeenAtUtc ?? '',
  ]);
}

function compareText(left: string, right: string): number {
  if (left === right) return 0;
  return left < right ? -1 : 1;
}

function isPositiveSafeInteger(value: number | null | undefined): value is number {
  return Number.isSafeInteger(value) && value! > 0;
}

function isNonNegativeSafeInteger(value: number | null | undefined): value is number {
  return Number.isSafeInteger(value) && value! >= 0;
}
