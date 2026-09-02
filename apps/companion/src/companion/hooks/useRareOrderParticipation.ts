import { useCallback, useEffect, useMemo, useRef, useState } from 'react';

import { updateRareGuestParticipation } from '@/companion/api';
import {
  buildRareOrderExactIdentity,
  buildRareOrderExactIdentityKey,
  buildRareOrderParticipationMutationKey,
  buildRareOrderParticipationProjection,
  buildRareOrderParticipationResolutionIndex,
  findRareOrderParticipationResolution,
  isRareOrderParticipationSnapshotAligned,
  type RareOrderExactIdentity,
  type RareOrderParticipationProjection,
  type RareOrderParticipationResolution,
  type RareOrderParticipationResolutionIndex,
} from '@/companion/domain/rare-order-participation';
import type {
  NightBusinessOrder,
  RareGuestParticipationMutationAction,
  RareGuestParticipationMutationTarget,
  RareGuestParticipationSnapshot,
} from '@/companion/types';

interface RareOrderParticipationOverlay {
  bindingKey: string;
  snapshot: RareGuestParticipationSnapshot;
}

export interface UseRareOrderParticipationOptions {
  endpoint: string;
  apiToken: string;
  connected: boolean;
  connectionRevision: number;
  authorityReady: boolean;
  currentDeviceIsPrimary: boolean;
  runtimeWriterReady: boolean;
  authorityRevision: number;
  businessActive: boolean;
  businessGeneration: number;
  collectionComplete: boolean;
  orders: readonly NightBusinessOrder[];
  moduleEnabled: boolean;
  managedGuestIds: readonly number[];
  snapshot: RareGuestParticipationSnapshot | null;
  refreshSnapshot: () => Promise<unknown>;
  refreshAuthority: () => Promise<void>;
  onMutationBoundary: () => void;
}

export interface RareOrderParticipationController {
  moduleEnabled: boolean;
  participationActive: boolean;
  snapshot: RareGuestParticipationSnapshot | null;
  projection: RareOrderParticipationProjection | null;
  resolutionIndex: RareOrderParticipationResolutionIndex | null;
  projectionReady: boolean;
  readOnly: boolean;
  busyMutationKey: string | null;
  error: string;
  resolveOrder: (order: NightBusinessOrder) => RareOrderParticipationResolution | null;
  mutateGuest: (
    guestId: number,
    expectedCurrentOrders: readonly RareOrderExactIdentity[],
    action: RareGuestParticipationMutationAction,
  ) => Promise<void>;
  mutateOrder: (
    order: RareOrderExactIdentity,
    action: RareGuestParticipationMutationAction,
  ) => Promise<void>;
}

/**
 * 绑定主设备状态、本场经营编号和调度状态版本的稀客队列控制器。
 *
 * POST 成功响应只在状态版本更新时覆盖当前结果，不提前假定成功；连接、控制窗口、经营或
 * 原始 revision 改变会中止旧请求，晚到响应不能回写当前会话。
 */
export function useRareOrderParticipation({
  endpoint,
  apiToken,
  connected,
  connectionRevision,
  authorityReady,
  currentDeviceIsPrimary,
  runtimeWriterReady,
  authorityRevision,
  businessActive,
  businessGeneration,
  collectionComplete,
  orders,
  moduleEnabled,
  managedGuestIds,
  snapshot,
  refreshSnapshot,
  refreshAuthority,
  onMutationBoundary,
}: UseRareOrderParticipationOptions): RareOrderParticipationController {
  const [overlay, setOverlay] = useState<RareOrderParticipationOverlay | null>(null);
  const [busyMutationKey, setBusyMutationKey] = useState<string | null>(null);
  const [error, setError] = useState('');
  const requestEpochRef = useRef(0);
  const requestAbortRef = useRef<AbortController | null>(null);
  const participationActive = moduleEnabled && managedGuestIds.length > 0;
  const managedSignature = managedGuestIds.join(',');
  const orderCollectionSignature = buildOrderCollectionBindingSignature(orders);
  const bindingKey = [
    connectionRevision,
    endpoint,
    apiToken,
    connected ? 'connected' : 'disconnected',
    authorityReady ? 'authority-ready' : 'authority-pending',
    currentDeviceIsPrimary ? 'primary' : 'secondary',
    runtimeWriterReady ? 'writer-ready' : 'writer-pending',
    businessActive ? 'business-active' : 'business-inactive',
    moduleEnabled ? 'module-enabled' : 'module-disabled',
    authorityRevision,
    businessGeneration,
    collectionComplete ? 'complete' : 'incomplete',
    orderCollectionSignature,
    managedSignature,
  ].join('\n');

  useEffect(() => {
    requestEpochRef.current += 1;
    requestAbortRef.current?.abort();
    requestAbortRef.current = null;
    setOverlay(null);
    setBusyMutationKey(null);
    setError('');
  }, [bindingKey]);

  useEffect(() => () => {
    requestEpochRef.current += 1;
    requestAbortRef.current?.abort();
    requestAbortRef.current = null;
  }, []);

  useEffect(() => {
    if (!requestAbortRef.current) return;
    requestEpochRef.current += 1;
    requestAbortRef.current.abort();
    requestAbortRef.current = null;
    setBusyMutationKey(null);
  }, [snapshot?.participationRevision]);

  const effectiveSnapshot = useMemo(() => {
    if (!overlay || overlay.bindingKey !== bindingKey) return snapshot;
    if (snapshot
      && snapshot.businessGeneration === overlay.snapshot.businessGeneration
      && snapshot.participationRevision >= overlay.snapshot.participationRevision) {
      return snapshot;
    }
    return overlay.snapshot;
  }, [bindingKey, overlay, snapshot]);

  const projection = useMemo(
    () => participationActive
      ? buildRareOrderParticipationProjection({
          orders,
          collectionComplete,
          managedGuestIds,
          businessGeneration,
          snapshot: effectiveSnapshot,
        })
      : null,
    [businessGeneration, collectionComplete, effectiveSnapshot, managedGuestIds, orders, participationActive],
  );
  const resolutionIndex = useMemo(
    () => projection
      ? buildRareOrderParticipationResolutionIndex(projection.resolutions)
      : null,
    [projection],
  );
  const snapshotAligned = participationActive && isRareOrderParticipationSnapshotAligned({
    snapshot: effectiveSnapshot,
    businessGeneration,
    managedGuestIds,
    orders,
    collectionComplete,
  });
  // 修改期间继续发布上一个已确认状态，使 Mod 能把当前高亮订单作为非抢占式
  // front anchor；新自动化命令由工作台的 busy mutation gate 单独阻止。
  const projectionReady = !participationActive || snapshotAligned;
  const readOnly = !moduleEnabled
    || !authorityReady
    || !currentDeviceIsPrimary
    || !runtimeWriterReady
    || !connected
    || !apiToken
    || !businessActive
    || !collectionComplete
    || (participationActive && !snapshotAligned);
  const readOnlyReason = !moduleEnabled
    ? '稀客调度模块已停用。'
    : !authorityReady
      ? '正在确认主设备与生效配置。'
      : !currentDeviceIsPrimary
        ? '当前设备不是主设备，可查看队列但不能修改参与状态。'
        : !runtimeWriterReady
          ? '主设备配置正在同步，完成前不可修改参与状态。'
          : !connected || !apiToken
            ? '本地 API 连接尚未就绪。'
            : !businessActive
              ? '当前未进入夜间经营。'
              : !collectionComplete
                ? '当前稀客订单集合读取不完整，已暂停参与状态操作。'
                : participationActive && !snapshotAligned
                  ? 'Mod 返回的稀客调度状态尚未与本场经营和调度名单同步一致。'
                  : '';

  const resolveOrder = useCallback((order: NightBusinessOrder) => {
    if (!participationActive || !resolutionIndex || !effectiveSnapshot) return null;
    return findRareOrderParticipationResolution(
      order,
      effectiveSnapshot.businessGeneration,
      resolutionIndex,
    );
  }, [effectiveSnapshot, participationActive, resolutionIndex]);

  const mutateTarget = useCallback(async (
    requestedTarget: RareGuestParticipationMutationTarget,
    action: RareGuestParticipationMutationAction,
  ) => {
    if (requestAbortRef.current || busyMutationKey !== null) return;
    if (readOnly || !effectiveSnapshot) {
      setError(readOnlyReason || '当前参与状态不可修改。');
      return;
    }

    let target: RareGuestParticipationMutationTarget;
    if (requestedTarget.type === 'guest') {
      const currentTargets = buildCompleteGuestTargetSet(
        orders,
        requestedTarget.guestId,
        businessGeneration,
      );
      if (!currentTargets
        || !sameExactTargetSet(currentTargets, requestedTarget.expectedCurrentOrders)) {
        setError('当前稀客订单已经变化，请等待状态刷新后重试。');
        await Promise.allSettled([refreshSnapshot(), refreshAuthority()]);
        return;
      }
      target = {
        type: 'guest',
        guestId: requestedTarget.guestId,
        expectedCurrentOrders: currentTargets,
      };
    } else {
      const currentOrder = findCurrentExactOrderTarget(
        orders,
        requestedTarget.order,
        businessGeneration,
      );
      if (!currentOrder) {
        setError('所选订单已经变化，请等待状态刷新后重试。');
        await Promise.allSettled([refreshSnapshot(), refreshAuthority()]);
        return;
      }
      target = { type: 'order', order: currentOrder };
    }

    const mutationKey = buildRareOrderParticipationMutationKey(action, target);
    const abortController = new AbortController();
    const requestEpoch = requestEpochRef.current + 1;
    requestEpochRef.current = requestEpoch;
    requestAbortRef.current = abortController;
    setBusyMutationKey(mutationKey);
    setError('');
    onMutationBoundary();
    try {
      const response = await updateRareGuestParticipation(
        endpoint,
        apiToken,
        {
          expectedAuthorityRevision: authorityRevision,
          expectedBusinessGeneration: businessGeneration,
          expectedParticipationRevision: effectiveSnapshot.participationRevision,
          action,
          target,
        },
        abortController.signal,
      );
      if (requestEpochRef.current !== requestEpoch || requestAbortRef.current !== abortController) return;
      if (!response.ok) throw new Error(response.error || response.status || '修改稀客参与状态失败。');
      if (response.participation.participationRevision < effectiveSnapshot.participationRevision
        || !isRareOrderParticipationSnapshotAligned({
          snapshot: response.participation,
          businessGeneration,
          managedGuestIds,
          orders,
          collectionComplete,
        })) {
        throw new Error('Mod 返回的稀客调度状态未与本场经营、调度名单和当前订单同步一致。');
      }
      setOverlay({ bindingKey, snapshot: response.participation });
      await Promise.allSettled([refreshSnapshot()]);
    } catch (cause) {
      if (requestEpochRef.current !== requestEpoch || requestAbortRef.current !== abortController) return;
      setError(cause instanceof Error ? cause.message : String(cause));
      await Promise.allSettled([refreshSnapshot(), refreshAuthority()]);
    } finally {
      if (requestEpochRef.current === requestEpoch && requestAbortRef.current === abortController) {
        requestAbortRef.current = null;
        setBusyMutationKey(null);
      }
    }
  }, [
    apiToken,
    authorityRevision,
    bindingKey,
    businessGeneration,
    busyMutationKey,
    collectionComplete,
    effectiveSnapshot,
    endpoint,
    managedGuestIds,
    onMutationBoundary,
    orders,
    readOnly,
    readOnlyReason,
    refreshAuthority,
    refreshSnapshot,
  ]);

  const mutateGuest = useCallback((
    guestId: number,
    expectedCurrentOrders: readonly RareOrderExactIdentity[],
    action: RareGuestParticipationMutationAction,
  ) => mutateTarget({
    type: 'guest',
    guestId,
    expectedCurrentOrders,
  }, action), [mutateTarget]);

  const mutateOrder = useCallback((
    order: RareOrderExactIdentity,
    action: RareGuestParticipationMutationAction,
  ) => mutateTarget({ type: 'order', order }, action), [mutateTarget]);

  return {
    moduleEnabled,
    participationActive,
    snapshot: effectiveSnapshot,
    projection,
    resolutionIndex,
    projectionReady,
    readOnly,
    busyMutationKey,
    error,
    resolveOrder,
    mutateGuest,
    mutateOrder,
  };
}

function buildOrderCollectionBindingSignature(orders: readonly NightBusinessOrder[]): string {
  return JSON.stringify(orders.map((order) => [
    order.traceId ?? null,
    order.orderLifecycleSequence,
    order.guestId ?? null,
  ]).sort((left, right) => {
    const leftKey = JSON.stringify(left);
    const rightKey = JSON.stringify(right);
    return leftKey < rightKey ? -1 : leftKey > rightKey ? 1 : 0;
  }));
}

function buildCompleteGuestTargetSet(
  orders: readonly NightBusinessOrder[],
  guestId: number,
  businessGeneration: number,
): RareOrderExactIdentity[] | null {
  if (!Number.isSafeInteger(guestId) || guestId < 0) return null;
  const guestOrders = orders.filter((order) => order.guestId === guestId);
  if (guestOrders.length === 0) return null;
  const targets = guestOrders.map((order) => buildRareOrderExactIdentity(order, businessGeneration));
  if (targets.some((target) => target === null)) return null;
  const exactTargets = targets as RareOrderExactIdentity[];
  const keys = new Set(exactTargets.map(buildRareOrderExactIdentityKey));
  return keys.size === exactTargets.length ? exactTargets : null;
}

function sameExactTargetSet(
  left: readonly RareOrderExactIdentity[],
  right: readonly RareOrderExactIdentity[],
): boolean {
  if (left.length !== right.length) return false;
  const rightKeys = new Set(right.map(buildRareOrderExactIdentityKey));
  return rightKeys.size === right.length
    && left.every((target) => rightKeys.has(buildRareOrderExactIdentityKey(target)));
}

function findCurrentExactOrderTarget(
  orders: readonly NightBusinessOrder[],
  expected: RareOrderExactIdentity,
  businessGeneration: number,
): RareOrderExactIdentity | null {
  if (expected.businessGeneration !== businessGeneration) return null;
  const expectedKey = buildRareOrderExactIdentityKey(expected);
  let match: RareOrderExactIdentity | null = null;
  for (const order of orders) {
    const identity = buildRareOrderExactIdentity(order, businessGeneration);
    if (!identity || buildRareOrderExactIdentityKey(identity) !== expectedKey) continue;
    if (match) return null;
    match = identity;
  }
  return match;
}
