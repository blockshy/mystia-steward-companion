import { useMemo, useState } from 'react';

import {
  buildRareGuestRosterSections,
  countCurrentRareOrdersByGuestId,
  updateManagedRareGuestIds,
} from '@/companion/domain/rare-order-participation';
import type { ExtensionModuleControlModel } from '@/companion/domain/extension-module-control';
import { ModuleControlPanel } from '@/companion/pages/ModuleControlPanel';
import type { NightBusinessOrder } from '@/companion/types';
import { Badge, Button, Dialog, EmptyRow, EmptyState, Input, ListPanel } from '@/components/ui-kit';
import type { RareCustomerCatalogItem } from '@/lib/catalog-types';

export interface ModRareGuestParticipationPanelProps {
  control: ExtensionModuleControlModel;
  customers: readonly Pick<RareCustomerCatalogItem, 'id' | 'name' | 'places' | 'dlc'>[];
  managedGuestIds: readonly number[];
  currentOrders: readonly NightBusinessOrder[];
  error?: string | null;
  onModuleEnabledChange: (enabled: boolean) => void;
  onManagedGuestIdsChange: (nextGuestIds: readonly number[]) => void;
}

/**
 * “扩展功能 · 稀客调度”模块。
 *
 * 模块开关和名单始终来自主设备共享配置。关闭模块只停用运行时调度，不删除已经保存的名单。
 */
export function ModRareGuestParticipationPanel({
  control,
  customers,
  managedGuestIds,
  currentOrders,
  error,
  onModuleEnabledChange,
  onManagedGuestIdsChange,
}: ModRareGuestParticipationPanelProps) {
  return (
    <div className="space-y-4" data-rare-guest-participation-module="true">
      <ModuleControlPanel
        moduleId="rare-guest-participation"
        label="启用稀客调度模块"
        description="开启后，保存名单内的当前稀客订单默认暂停，并可在经营中手动启用；关闭时保留名单，所有稀客恢复自动参与。"
        control={control}
        focusKey="extensions:rare-participation:module-toggle"
        onEnabledChange={onModuleEnabledChange}
      />

      {error && (
        <div className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {error}
        </div>
      )}

      {!control.enabled ? (
        <EmptyState
          text={`稀客调度模块已停用。已保存 ${managedGuestIds.length} 名稀客；启用模块后名单才会影响高亮和自动化。`}
        />
      ) : (
        <RareGuestRosterPanel
          customers={customers}
          managedGuestIds={managedGuestIds}
          currentOrders={currentOrders}
          control={control}
          onManagedGuestIdsChange={onManagedGuestIdsChange}
        />
      )}
    </div>
  );
}

function RareGuestRosterPanel({
  control,
  customers,
  managedGuestIds,
  currentOrders,
  onManagedGuestIdsChange,
}: Pick<ModRareGuestParticipationPanelProps,
  'control' | 'customers' | 'managedGuestIds' | 'currentOrders' | 'onManagedGuestIdsChange'>) {
  const [query, setQuery] = useState('');
  const [pendingRemovalGuestId, setPendingRemovalGuestId] = useState<number | null>(null);
  const sections = useMemo(
    () => buildRareGuestRosterSections({ customers, managedGuestIds, query }),
    [customers, managedGuestIds, query],
  );
  const allRows = useMemo(
    () => buildRareGuestRosterSections({ customers, managedGuestIds }),
    [customers, managedGuestIds],
  );
  const currentOrderCounts = useMemo(
    () => countCurrentRareOrdersByGuestId(currentOrders),
    [currentOrders],
  );
  const pendingRemoval = pendingRemovalGuestId === null
    ? null
    : allRows.managed.find((row) => row.guestId === pendingRemovalGuestId) ?? null;
  const pendingRemovalOrderCount = pendingRemovalGuestId === null
    ? 0
    : currentOrderCounts.get(pendingRemovalGuestId) ?? 0;
  const disabled = !control.writable;
  const busy = control.pending;

  const changeRoster = (guestId: number, managed: boolean) => {
    if (disabled) return;
    onManagedGuestIdsChange(updateManagedRareGuestIds(managedGuestIds, guestId, managed));
  };
  const requestRemoval = (guestId: number) => {
    if (disabled) return;
    if ((currentOrderCounts.get(guestId) ?? 0) > 0) {
      setPendingRemovalGuestId(guestId);
      return;
    }
    changeRoster(guestId, false);
  };

  return (
    <>
      <div className="space-y-4" data-rare-guest-participation-roster="true">
        <ListPanel
          title="参与随时启用/暂停的稀客列表"
          action={<Badge variant="secondary">已选 {allRows.managed.length}</Badge>}
        >
          <div className="space-y-3">
            <p className="text-sm text-muted-foreground">
              名单内稀客的每一笔新订单都默认暂停，只有在“经营中 · 稀客队列”手动启用后才参与
              高亮、自动化和资源预约。未入名单的稀客保持原有行为。
            </p>
            {!control.writable && (
              <div className="border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-xs text-amber-800 dark:text-amber-200">
                {control.reason || '当前稀客调度配置只读。'}
              </div>
            )}
            <label className="block space-y-1.5 text-sm">
              <span className="text-muted-foreground">搜索稀客</span>
              <Input
                value={query}
                placeholder="输入姓名、ID或地区"
                data-gamepad-focus-key="extensions:rare-participation:search"
                onChange={(event) => setQuery(event.currentTarget.value)}
              />
            </label>
          </div>
        </ListPanel>

        <RosterSection
          title={`已加入名单 (${sections.managed.length})`}
          emptyText={query ? '没有匹配的名单内稀客。' : '当前名单为空，所有稀客保持原有行为。'}
          rows={sections.managed}
          currentOrderCounts={currentOrderCounts}
          disabled={disabled}
          busy={busy}
          actionLabel="移出名单"
          actionVariant="outline"
          onAction={requestRemoval}
        />

        <RosterSection
          title={`可添加 (${sections.available.length})`}
          emptyText={query.trim()
            ? '没有匹配的可添加稀客。'
            : customers.length === 0
              ? '当前游戏稀客目录为空。'
              : '目录中的稀客均已加入调度名单。'}
          rows={sections.available}
          currentOrderCounts={currentOrderCounts}
          disabled={disabled}
          busy={busy}
          actionLabel="加入名单"
          actionVariant="default"
          onAction={(guestId) => changeRoster(guestId, true)}
        />
      </div>

      <Dialog
        id="rare-guest-roster-removal-dialog"
        opened={Boolean(pendingRemoval)}
        onClose={() => setPendingRemovalGuestId(null)}
        returnFocusKey={pendingRemovalGuestId === null
          ? 'extensions:rare-participation:search'
          : `extensions:rare-participation:guest:${pendingRemovalGuestId}:remove`}
        title="移出调度名单"
      >
        <div className="space-y-3 text-muted-foreground">
          <p>
            “{pendingRemoval?.name ?? ''}”当前还有 {pendingRemovalOrderCount} 笔订单。移出名单后，这些订单将由 Mod
            自动加入稀客队列末尾，并恢复高亮和自动化资格。
          </p>
          <p className="text-xs">已经开始且无法撤销的游戏操作仍会由 Mod 处理到下一个可安全停止的位置。</p>
        </div>
        <div className="flex justify-end gap-2" data-gamepad-axis="x">
          <Button
            type="button"
            size="sm"
            variant="outline"
            data-autofocus
            data-gamepad-dialog-default="true"
            data-gamepad-focus-key="extensions:rare-participation:remove:cancel"
            onClick={() => setPendingRemovalGuestId(null)}
          >
            取消
          </Button>
          <Button
            type="button"
            size="sm"
            variant="destructive"
            disabled={disabled || !pendingRemoval}
            loading={busy}
            data-gamepad-focus-key="extensions:rare-participation:remove:confirm"
            onClick={() => {
              if (!pendingRemoval) return;
              changeRoster(pendingRemoval.guestId, false);
              setPendingRemovalGuestId(null);
            }}
          >
            移出并自动排尾
          </Button>
        </div>
      </Dialog>
    </>
  );
}

function RosterSection({
  title,
  emptyText,
  rows,
  currentOrderCounts,
  disabled,
  busy,
  actionLabel,
  actionVariant,
  onAction,
}: {
  title: string;
  emptyText: string;
  rows: ReturnType<typeof buildRareGuestRosterSections>['managed'];
  currentOrderCounts: ReadonlyMap<number, number>;
  disabled: boolean;
  busy: boolean;
  actionLabel: string;
  actionVariant: 'default' | 'outline';
  onAction: (guestId: number) => void;
}) {
  return (
    <ListPanel
      title={title}
      gamepadScrollKey={`extensions:rare-participation:${actionVariant === 'default' ? 'available' : 'managed'}`}
      gamepadScrollLabel={title}
      contentClassName="max-h-[28rem] overflow-auto pr-1"
    >
      {rows.length === 0 && <EmptyRow text={emptyText} />}
      <div className="space-y-2">
        {rows.map((row) => {
          const orderCount = currentOrderCounts.get(row.guestId) ?? 0;
          const action = actionVariant === 'default' ? 'add' : 'remove';
          return (
            <div
              key={row.guestId}
              className="steward-data-row flex flex-wrap items-center justify-between gap-3 p-2.5 text-sm"
              data-managed-rare-guest-id={row.guestId}
              data-managed-rare-guest-selected={row.managed ? 'true' : 'false'}
            >
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-1.5">
                  <span className="font-medium">{row.name}</span>
                  <Badge variant="outline">ID {row.guestId}</Badge>
                  {!row.catalogAvailable && <Badge variant="destructive">目录已缺失</Badge>}
                  {orderCount > 0 && <Badge variant="secondary">当前 {orderCount} 笔</Badge>}
                </div>
                <div className="mt-1 text-xs text-muted-foreground">
                  {row.places.length > 0 ? row.places.join(' / ') : '暂无地区信息'}
                  <span className="mx-1">·</span>
                  {row.dlc === 0 ? '本体' : `DLC ${row.dlc}`}
                </div>
              </div>
              <Button
                type="button"
                size="sm"
                variant={actionVariant}
                disabled={disabled}
                loading={busy}
                data-gamepad-clickable="true"
                data-gamepad-focus-key={`extensions:rare-participation:guest:${row.guestId}:${action}`}
                onClick={() => onAction(row.guestId)}
              >
                {actionLabel}
              </Button>
            </div>
          );
        })}
      </div>
    </ListPanel>
  );
}
