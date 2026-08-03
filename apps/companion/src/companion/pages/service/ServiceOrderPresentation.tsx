import type { ReactNode } from 'react';

import { Badge, EmptyState, ListPanel } from '@/components/ui-kit';
import { composeClassNames } from '@/components/ui/style';

export type ServiceOrderCollectionState =
  | { kind: 'ready' }
  | { kind: 'empty'; message: string }
  | { kind: 'updating'; message: string; label?: string }
  | {
      kind: 'error';
      message: string;
      detail?: string;
      emptyLabel?: string;
      retainedLabel?: string;
      updating?: boolean;
      updatingLabel?: string;
    };

type ServiceOrderCollectionMode = 'rare' | 'normal' | 'rare-focus';

export function ServiceOrderCollectionPanel({
  mode,
  count,
  state,
  hasRows,
  action,
  notice,
  children,
  compact = false,
}: {
  mode: ServiceOrderCollectionMode;
  count: number;
  state: ServiceOrderCollectionState;
  hasRows: boolean;
  action?: ReactNode;
  notice?: ReactNode;
  children: ReactNode;
  compact?: boolean;
}) {
  const fillAvailableHeight = mode === 'rare-focus';
  const showRows = hasRows && state.kind !== 'empty';
  const visibleCount = showRows ? count : 0;
  const statusBadge = buildStatusBadge(state, showRows);
  const panelAction = (
    <div className="flex flex-wrap items-center justify-end gap-2">
      {statusBadge}
      <Badge variant="secondary">{visibleCount} 笔</Badge>
      {action}
    </div>
  );
  const gamepadScrollKey = mode === 'rare-focus'
    ? 'service-focus:recommendations'
    : mode === 'rare'
      ? 'service:recommendations'
      : 'service:recommendations:normal';
  const gamepadScrollLabel = mode === 'rare-focus'
    ? '专注模式当前订单方案'
    : `经营中${mode === 'rare' ? '稀客' : '普客'}当前订单方案`;
  const contentClassName = fillAvailableHeight
    ? 'min-h-0 flex-1 overflow-auto pb-4 pr-1'
    : compact
      ? 'min-h-[24rem] max-h-[calc(100vh-12rem)] overflow-auto pb-4 pr-1'
      : 'min-h-[32rem] max-h-[calc(100vh-20rem)] overflow-auto pb-4 pr-1';

  return (
    <ListPanel
      title="当前订单方案"
      action={panelAction}
      className={fillAvailableHeight ? 'min-h-0 flex-1' : undefined}
      gamepadScrollKey={gamepadScrollKey}
      gamepadScrollLabel={gamepadScrollLabel}
      contentClassName={contentClassName}
    >
      <div
        className="min-w-0"
        data-service-order-collection={mode}
        data-service-order-count={visibleCount}
        data-service-order-state={state.kind}
        data-service-order-retaining-rows={showRows ? 'true' : 'false'}
      >
        {notice}
        {!showRows && state.kind !== 'ready' && <EmptyState text={state.message} />}
        {showRows ? children : null}
      </div>
    </ListPanel>
  );
}

function buildStatusBadge(state: ServiceOrderCollectionState, hasRows: boolean): ReactNode {
  if (state.kind === 'updating') {
    return <Badge variant="outline">{state.label ?? '更新中'}</Badge>;
  }
  if (state.kind === 'error') {
    return (
      <>
        <Badge variant="destructive" title={state.detail}>
          {hasRows
            ? state.retainedLabel ?? '更新失败，当前为上次结果'
            : state.emptyLabel ?? '读取失败'}
        </Badge>
        {state.updating && <Badge variant="outline">{state.updatingLabel ?? '更新中'}</Badge>}
      </>
    );
  }
  return null;
}

export function ServiceOrderCardFrame({
  title,
  subtitle,
  badges,
  message,
  children,
  compact = false,
  optimizeRendering = false,
  pending = false,
  className,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  badges?: ReactNode;
  message?: ReactNode;
  children?: ReactNode;
  compact?: boolean;
  optimizeRendering?: boolean;
  pending?: boolean;
  className?: string;
}) {
  return (
    <div
      className={composeClassNames(
        'steward-data-row',
        compact ? 'p-2 text-xs' : 'p-3 text-sm',
        optimizeRendering ? '[contain-intrinsic-size:220px] [content-visibility:auto]' : undefined,
        className,
      )}
      data-service-order-card="true"
      data-recommendation-pending-order={pending ? 'true' : undefined}
    >
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <div className={compact ? 'font-medium' : 'text-sm font-semibold'}>{title}</div>
          {subtitle && <div className="mt-1 text-xs text-muted-foreground">{subtitle}</div>}
        </div>
        {badges && <div className="ml-auto flex min-w-0 max-w-full flex-wrap justify-end gap-1.5">{badges}</div>}
      </div>
      {message && <div className="mt-1 text-xs text-muted-foreground">{message}</div>}
      {children}
    </div>
  );
}
