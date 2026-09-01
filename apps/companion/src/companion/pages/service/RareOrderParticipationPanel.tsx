import { useEffect, useMemo, useRef } from 'react';

import {
  buildManagedRareOrderGroups,
  buildRareOrderParticipationMutationKey,
  type ManagedRareOrderRow,
  type RareOrderExactIdentity,
  type RareOrderParticipationSnapshotView,
} from '@/companion/domain/rare-order-participation';
import type {
  NightBusinessOrder,
  RareGuestParticipationMutationAction,
} from '@/companion/types';
import {
  Badge,
  Button,
  EmptyRow,
  EmptyState,
  ListPanel,
} from '@/components/ui-kit';

export interface RareOrderParticipationPanelProps {
  orders: readonly NightBusinessOrder[];
  managedGuestIds: readonly number[];
  snapshot: RareOrderParticipationSnapshotView | null;
  businessGeneration: number;
  collectionComplete: boolean;
  businessActive: boolean;
  readOnly: boolean;
  busyMutationKey?: string | null;
  error?: string | null;
  onMutateGuest: (
    guestId: number,
    targets: readonly RareOrderExactIdentity[],
    action: RareGuestParticipationMutationAction,
  ) => void;
  onMutateOrder: (
    order: RareOrderExactIdentity,
    action: RareGuestParticipationMutationAction,
  ) => void;
}

/**
 * “经营中 · 稀客队列”独立 Tab 内容。
 *
 * 分组按 canonical guestId 展示，但操作参数是当前快照中的全部 exact lifecycle。新订单在
 * 下一次快照中仍默认暂停，不会被过去的稀客级点击意外授权。
 */
export function RareOrderParticipationPanel({
  orders,
  managedGuestIds,
  snapshot,
  businessGeneration,
  collectionComplete,
  businessActive,
  readOnly,
  busyMutationKey = null,
  error,
  onMutateGuest,
  onMutateOrder,
}: RareOrderParticipationPanelProps) {
  const panelRef = useRef<HTMLDivElement>(null);
  const previousBusyMutationKeyRef = useRef<string | null>(busyMutationKey);
  const pendingFocusScopeRef = useRef<string | null>(null);
  const groups = useMemo(
    () => buildManagedRareOrderGroups({
      orders,
      collectionComplete,
      managedGuestIds,
      businessGeneration,
      snapshot,
    }),
    [businessGeneration, collectionComplete, managedGuestIds, orders, snapshot],
  );
  const managedCount = useMemo(
    () => new Set(managedGuestIds.filter((guestId) => Number.isSafeInteger(guestId) && guestId >= 0)).size,
    [managedGuestIds],
  );
  const pausedCount = groups.reduce(
    (count, group) => count + group.rows.filter((row) => row.state === 'paused').length,
    0,
  );
  const queuedCount = groups.reduce(
    (count, group) => count + group.rows.filter((row) => row.state === 'queued').length,
    0,
  );
  useEffect(() => {
    const wasBusy = previousBusyMutationKeyRef.current !== null;
    previousBusyMutationKeyRef.current = busyMutationKey;
    if (!wasBusy || busyMutationKey !== null) return undefined;

    const focusScope = pendingFocusScopeRef.current;
    pendingFocusScopeRef.current = null;
    if (!focusScope) return undefined;

    const animationFrame = window.requestAnimationFrame(() => {
      const activeElement = document.activeElement;
      if (activeElement && activeElement !== document.body && activeElement !== document.documentElement) return;

      const scope = Array.from(
        panelRef.current?.querySelectorAll<HTMLElement>('[data-rare-order-participation-focus-scope]') ?? [],
      ).find((element) => element.dataset.rareOrderParticipationFocusScope === focusScope);
      scope?.querySelector<HTMLButtonElement>(
        '[data-rare-order-participation-action="true"]:not(:disabled):not([aria-disabled="true"])',
      )?.focus({ preventScroll: true });
    });

    return () => window.cancelAnimationFrame(animationFrame);
  }, [busyMutationKey]);

  const runMutationInFocusScope = (focusScope: string, mutate: () => void) => {
    pendingFocusScopeRef.current = focusScope;
    mutate();
  };

  return (
    <div
      ref={panelRef}
      className="space-y-4"
      data-rare-order-participation-panel="true"
      data-busy={busyMutationKey !== null ? 'true' : 'false'}
    >
      <ListPanel
        title="稀客参与队列"
        action={(
          <div className="flex flex-wrap items-center justify-end gap-1.5">
            <Badge variant="outline">暂停 {pausedCount}</Badge>
            <Badge variant="secondary">已启用 {queuedCount}</Badge>
          </div>
        )}
        children={error
          ? (
              <div
                className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive"
                role="alert"
              >
                {error}
              </div>
            )
          : undefined}
      />

      {!businessActive && (
        <EmptyState text="当前未进入夜间经营，开始经营后这里会显示受控稀客订单。" />
      )}
      {businessActive && managedCount === 0 && (
        <EmptyState text="受控稀客名单为空。请先在“扩展功能 → 稀客调度”中添加稀客。" />
      )}
      {businessActive && managedCount > 0 && groups.length === 0 && (
        <EmptyState text="当前订单中没有受控名单内的稀客。" />
      )}

      {businessActive && groups.map((group) => {
        const frontKey = buildRareOrderParticipationMutationKey('enable-front', {
          type: 'guest',
          guestId: group.guestId,
        });
        const tailKey = buildRareOrderParticipationMutationKey('enable-tail', {
          type: 'guest',
          guestId: group.guestId,
        });
        const pauseKey = buildRareOrderParticipationMutationKey('pause', {
          type: 'guest',
          guestId: group.guestId,
        });
        const focusScope = `guest:${group.guestId}`;
        const groupMutationDisabled = readOnly || busyMutationKey !== null || group.hasUnavailableRows;
        return (
          <ListPanel
            key={group.guestId}
            title={`${group.guestName} · ${group.rows.length} 笔`}
            action={(
              <div
                className="flex flex-wrap items-center justify-end gap-1.5"
                data-gamepad-axis="x"
                data-rare-order-participation-focus-scope={focusScope}
              >
                <Button
                  type="button"
                  size="xs"
                  disabled={groupMutationDisabled || group.pausedTargets.length === 0}
                  loading={busyMutationKey === frontKey}
                  data-gamepad-clickable="true"
                  data-gamepad-focus-key={`service:rare-participation:guest:${group.guestId}:enable-front`}
                  data-rare-order-participation-action="true"
                  aria-label={`优先启用${group.guestName}的全部暂停订单`}
                  onClick={() => runMutationInFocusScope(
                    focusScope,
                    () => onMutateGuest(group.guestId, group.allCurrentTargets, 'enable-front'),
                  )}
                >
                  全部优先启用
                </Button>
                <Button
                  type="button"
                  size="xs"
                  variant="outline"
                  disabled={groupMutationDisabled || group.pausedTargets.length === 0}
                  loading={busyMutationKey === tailKey}
                  data-gamepad-clickable="true"
                  data-gamepad-focus-key={`service:rare-participation:guest:${group.guestId}:enable-tail`}
                  data-rare-order-participation-action="true"
                  aria-label={`队尾启用${group.guestName}的全部暂停订单`}
                  onClick={() => runMutationInFocusScope(
                    focusScope,
                    () => onMutateGuest(group.guestId, group.allCurrentTargets, 'enable-tail'),
                  )}
                >
                  全部队尾启用
                </Button>
                <Button
                  type="button"
                  size="xs"
                  variant="outline"
                  disabled={groupMutationDisabled || group.queuedTargets.length === 0}
                  loading={busyMutationKey === pauseKey}
                  data-gamepad-clickable="true"
                  data-gamepad-focus-key={`service:rare-participation:guest:${group.guestId}:pause`}
                  data-rare-order-participation-action="true"
                  aria-label={`暂停${group.guestName}的全部已启用订单`}
                  onClick={() => runMutationInFocusScope(
                    focusScope,
                    () => onMutateGuest(group.guestId, group.allCurrentTargets, 'pause'),
                  )}
                >
                  暂停全部
                </Button>
              </div>
            )}
            gamepadScrollKey={`service:rare-participation:guest:${group.guestId}:orders`}
            gamepadScrollLabel={`${group.guestName}受控订单`}
          >
            {group.hasUnavailableRows && (
              <EmptyRow text="存在缺少精确身份或未与 Mod 权威快照对齐的订单；为避免部分授权，本组暂不可操作。" />
            )}
            <div className="space-y-2">
              {group.rows.map((row) => (
                <ParticipationOrderRow
                  key={row.key}
                  row={row}
                  guestName={group.guestName}
                  readOnly={readOnly}
                  busyMutationKey={busyMutationKey}
                  onMutateOrder={onMutateOrder}
                  onRunMutation={runMutationInFocusScope}
                />
              ))}
            </div>
          </ListPanel>
        );
      })}
      <div
        className="sr-only"
        role="status"
        aria-live="polite"
        data-rare-order-participation-status="true"
      >
        {busyMutationKey ? '稀客参与队列更新中。' : ''}
      </div>
    </div>
  );
}

function ParticipationOrderRow({
  row,
  guestName,
  readOnly,
  busyMutationKey,
  onMutateOrder,
  onRunMutation,
}: {
  row: ManagedRareOrderRow;
  guestName: string;
  readOnly: boolean;
  busyMutationKey: string | null;
  onMutateOrder: (
    order: RareOrderExactIdentity,
    action: RareGuestParticipationMutationAction,
  ) => void;
  onRunMutation: (focusScope: string, mutate: () => void) => void;
}) {
  const identity = row.exactIdentity;
  const frontKey = identity
    ? buildRareOrderParticipationMutationKey('enable-front', { type: 'order', order: identity })
    : '';
  const tailKey = identity
    ? buildRareOrderParticipationMutationKey('enable-tail', { type: 'order', order: identity })
    : '';
  const pauseKey = identity
    ? buildRareOrderParticipationMutationKey('pause', { type: 'order', order: identity })
    : '';
  const actionDisabled = readOnly || busyMutationKey !== null || !identity;
  const orderLabel = `${guestName}桌 ${formatDesk(row.order.deskCode)} 的 lifecycle ${row.order.orderLifecycleSequence}`;
  const focusScope = `order:${row.key}`;
  return (
    <div
      className="steward-data-row p-2.5 text-sm"
      data-rare-order-participation-state={row.state}
      data-rare-order-participation-key={row.key}
    >
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="font-medium">桌 {formatDesk(row.order.deskCode)} · lifecycle {row.order.orderLifecycleSequence}</div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            <Badge variant="outline">料理 {row.order.foodTag || '无'} ({row.order.foodTagId ?? 'missing'})</Badge>
            <Badge variant="outline">酒水 {row.order.beverageTag || '无'} ({row.order.beverageTagId ?? 'missing'})</Badge>
            {row.order.missionRecipePriority && <Badge variant="secondary">任务料理优先</Badge>}
            {row.order.specialBusinessRoleLabel && (
              <Badge variant="secondary">{row.order.specialBusinessRoleLabel}</Badge>
            )}
          </div>
        </div>
        <div className="flex flex-wrap justify-end gap-1.5">
          {row.state === 'paused' && <Badge variant="outline">已暂停</Badge>}
          {row.state === 'queued' && (
            <>
              <Badge variant="secondary">已启用</Badge>
              <Badge variant="outline">队列 #{row.queuePosition ?? '?'}</Badge>
            </>
          )}
          {row.state === 'unavailable' && <Badge variant="destructive">状态不可用</Badge>}
        </div>
      </div>
      {row.state === 'unavailable' && row.reason && (
        <div className="mt-1 text-xs text-muted-foreground">{row.reason}</div>
      )}
      {identity && (
        <div className="mt-1 truncate font-mono text-[0.7rem] text-muted-foreground" title={identity.traceId}>
          trace {identity.traceId}
        </div>
      )}
      {identity && row.state !== 'unavailable' && (
        <div
          className="mt-2 flex flex-wrap justify-end gap-1.5"
          data-gamepad-axis="x"
          data-rare-order-participation-focus-scope={focusScope}
        >
          {row.state === 'paused' ? (
            <>
              <Button
                type="button"
                size="xs"
                disabled={actionDisabled}
                loading={busyMutationKey === frontKey}
                data-gamepad-clickable="true"
                data-gamepad-focus-key={buildOrderFocusKey(row, 'enable-front')}
                data-rare-order-participation-action="true"
                aria-label={`优先启用${orderLabel}`}
                onClick={() => onRunMutation(
                  focusScope,
                  () => onMutateOrder(identity, 'enable-front'),
                )}
              >
                优先启用
              </Button>
              <Button
                type="button"
                size="xs"
                variant="outline"
                disabled={actionDisabled}
                loading={busyMutationKey === tailKey}
                data-gamepad-clickable="true"
                data-gamepad-focus-key={buildOrderFocusKey(row, 'enable-tail')}
                data-rare-order-participation-action="true"
                aria-label={`队尾启用${orderLabel}`}
                onClick={() => onRunMutation(
                  focusScope,
                  () => onMutateOrder(identity, 'enable-tail'),
                )}
              >
                队尾启用
              </Button>
            </>
          ) : (
            <Button
              type="button"
              size="xs"
              variant="outline"
              disabled={actionDisabled}
              loading={busyMutationKey === pauseKey}
              data-gamepad-clickable="true"
              data-gamepad-focus-key={buildOrderFocusKey(row, 'pause')}
              data-rare-order-participation-action="true"
              aria-label={`暂停${orderLabel}`}
              onClick={() => onRunMutation(
                focusScope,
                () => onMutateOrder(identity, 'pause'),
              )}
            >
              暂停该订单
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

function buildOrderFocusKey(
  row: ManagedRareOrderRow,
  action: RareGuestParticipationMutationAction,
): string {
  return `service:rare-participation:order:${row.order.traceId ?? 'missing'}:${row.order.orderLifecycleSequence}:${action}`;
}

function formatDesk(deskCode: number): string {
  return deskCode >= 0 ? String(deskCode + 1) : '未知';
}
