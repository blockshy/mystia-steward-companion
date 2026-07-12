import {
  Badge,
  Card,
  CardContent,
  EmptyRow,
  InfoLine,
  ListPanel,
} from '@/components/ui-kit';
import { buildNightBusinessOrderKey } from '@/companion/domain/automation';
import { sortNightOrders, sortNormalOrders } from '@/companion/domain/sorting';
import { isSpecialBusinessOrderRole } from '@/companion/domain/special-business';
import { formatDesk } from '@/companion/formatters';
import { DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';
import type {
  AutomationResourceOverview,
  NightBusinessContext,
  NormalBusinessContext,
  SpecialBusinessContext,
} from '@/companion/types';

type SpecialBusinessOrderSummary = {
  key: string;
  kind: 'rare' | 'normal';
  roleLabel: string;
  traceId?: string;
  deskCode: number;
  guestName: string;
  foodText: string;
  beverageText: string;
  source: string;
};

export function OrderTraceBadge({ traceId }: { traceId?: string }) {
  if (!traceId) return null;
  return (
    <Badge variant="secondary" title={`总日志标识 ${traceId}`}>
      日志 {traceId}
    </Badge>
  );
}

export function SpecialBusinessNotice({
  context,
  showDebugDetails,
}: {
  context: SpecialBusinessContext;
  showDebugDetails: boolean;
}) {
  const targets = formatSpecialBusinessTargets(context);
  const displayName = context.displayName || context.challengeType;

  return (
    <Card>
      <CardContent className="space-y-3 p-4 text-sm">
        <div className="flex flex-wrap items-center gap-2">
          <Badge variant="secondary">特殊经营</Badge>
          <span className="font-medium text-foreground">{displayName}</span>
          {context.challengeType && context.challengeType !== displayName && (
            <span className="text-xs text-muted-foreground">{context.challengeType}</span>
          )}
        </div>
        <div className={DENSE_TWO_COLUMN_GRID}>
          <InfoLine label="挑战目标" value={targets || '暂无已捕获目标'} />
          <InfoLine label="规则提示" value={context.ruleSummary || '暂无规则说明'} />
          <InfoLine label="推荐策略" value={context.recommendationPolicy || '不改变标准推荐排序'} />
          <InfoLine label="自动化策略" value={context.automationPolicy || '不改变自动化策略'} />
        </div>
        {showDebugDetails && (
          <div className="space-y-1 text-xs text-muted-foreground">
            {context.error && <div>读取状态：{context.error}</div>}
            <div>来源：{context.source || '暂无'}</div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}

export function SpecialBusinessOrderList({
  night,
  normalBusiness,
  showDebugDetails,
}: {
  night: NightBusinessContext | null;
  normalBusiness: NormalBusinessContext | null;
  showDebugDetails: boolean;
}) {
  const orders = buildSpecialBusinessOrderSummaries(night, normalBusiness);
  if (orders.length === 0) return null;

  return (
    <ListPanel title={`特殊经营订单 (${orders.length})`} contentClassName="min-h-[4rem]">
      {orders.map((order) => (
        <div key={order.key} className="border-b py-2 text-sm last:border-b-0">
          <div className="flex items-center justify-between gap-3">
            <span className="min-w-0 truncate font-medium" title={order.guestName}>{order.guestName}</span>
            <span className="shrink-0 text-muted-foreground">桌 {formatDesk(order.deskCode)}</span>
          </div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            <Badge variant="secondary">{order.roleLabel}</Badge>
            <Badge variant="outline">{order.kind === 'rare' ? '稀客链路' : '普客链路'}</Badge>
            <Badge variant="outline">{order.foodText}</Badge>
            <Badge variant="outline">{order.beverageText}</Badge>
            <OrderTraceBadge traceId={order.traceId} />
            {showDebugDetails && <Badge variant="secondary">{order.source}</Badge>}
          </div>
        </div>
      ))}
    </ListPanel>
  );
}

export function AutomationResourcePanel({ overview }: { overview: AutomationResourceOverview }) {
  const hasCookerRows = overview.cookers.length > 0;
  const blockedRows = overview.normalBlocked.slice(0, 3);
  const blockedOverflow = Math.max(0, overview.normalBlocked.length - blockedRows.length);

  return (
    <ListPanel title="厨具预约">
      {!hasCookerRows && <EmptyRow text="暂无厨具预约" />}
      <div className="space-y-2">
        {overview.cookers.map((row) => (
          <ResourceUsageRow
            key={row.key}
            label={row.label}
            value={`${row.normalReserved + row.rareReserved}/${row.capacity}`}
            status={row.normalReserved + row.rareReserved > row.capacity ? 'over' : row.normalReserved + row.rareReserved > 0 ? 'active' : 'idle'}
            details={[
              row.normalReserved > 0 ? `普客 ${row.normalReserved}` : '',
              row.rareReserved > 0 ? `稀客 ${row.rareReserved}` : '',
              ...row.labels.slice(0, 2),
            ].filter(Boolean)}
            overflow={Math.max(0, row.labels.length - 2)}
          />
        ))}
      </div>
      {blockedRows.length > 0 && (
        <div className="mt-3 space-y-2 border-t pt-3 text-xs">
          <div className="font-medium text-muted-foreground">未占用厨具</div>
          {blockedRows.map((row) => (
            <div key={row.orderKey} className="space-y-1 rounded border bg-muted/20 px-2 py-1.5">
              <div className="truncate font-medium text-foreground" title={row.label}>{row.label}</div>
              <div className="truncate text-muted-foreground" title={row.reason}>{row.reason}</div>
            </div>
          ))}
          {blockedOverflow > 0 && (
            <div className="text-muted-foreground">还有 {blockedOverflow} 笔未显示</div>
          )}
        </div>
      )}
    </ListPanel>
  );
}

function buildSpecialBusinessOrderSummaries(
  night: NightBusinessContext | null,
  normalBusiness: NormalBusinessContext | null,
): SpecialBusinessOrderSummary[] {
  const rareOrders = sortNightOrders(night?.orders ?? [], 'ordered')
    .filter((order) => isSpecialBusinessOrderRole(order.specialBusinessRole))
    .map((order) => ({
      key: `rare:${buildNightBusinessOrderKey(order)}`,
      kind: 'rare' as const,
      roleLabel: order.specialBusinessRoleLabel || '特殊经营订单',
      traceId: order.traceId,
      deskCode: order.deskCode,
      guestName: order.guestName || '稀客',
      foodText: `料理 ${order.foodTag || '无'}`,
      beverageText: `酒水 ${order.beverageTag || '无'}`,
      source: order.source,
    }));
  const normalOrders = sortNormalOrders(normalBusiness?.orders ?? [])
    .filter((order) => isSpecialBusinessOrderRole(order.specialBusinessRole))
    .map((order) => ({
      key: `normal:${order.orderKey || `${order.deskCode}-${order.foodId}-${order.beverageId}`}`,
      kind: 'normal' as const,
      roleLabel: order.specialBusinessRoleLabel || '特殊经营订单',
      traceId: order.traceId,
      deskCode: order.deskCode,
      guestName: order.guestName || '普客',
      foodText: `料理 ${order.foodName || `#${order.foodId}`}`,
      beverageText: `酒水 ${order.beverageName || `#${order.beverageId}`}`,
      source: order.source,
    }));

  return [...rareOrders, ...normalOrders]
    .sort((left, right) => left.deskCode - right.deskCode || left.kind.localeCompare(right.kind));
}

function formatSpecialBusinessTargets(context: SpecialBusinessContext): string {
  const progress = typeof context.currentValue === 'number' || typeof context.maxValue === 'number'
    ? `${context.targetLabel || '进度'} ${context.currentValue ?? '?'}/${context.maxValue ?? '?'}`
    : '';
  const targetProgress = typeof context.targetValue === 'number'
    ? `目标进度 ${context.targetValue}`
    : '';
  const spellCount = typeof context.currentSpellCount === 'number' || typeof context.targetSpellCount === 'number'
    ? `符卡 ${context.currentSpellCount ?? '?'}/${context.targetSpellCount ?? '?'}`
    : '';
  const targetTagTime = typeof context.targetTagTimeProgress === 'number'
    ? `Tag 剩余 ${Math.max(0, Math.round(context.targetTagTimeProgress * 100))}%`
    : '';
  const targetTime = typeof context.targetTimeProgress === 'number'
    ? `阶段剩余 ${Math.max(0, Math.round(context.targetTimeProgress * 100))}%`
    : '';
  const parts = [
    context.foodTargetTags.length > 0 ? `料理 Tag ${context.foodTargetTags.join('、')}` : '',
    context.beverageTargetTags.length > 0 ? `酒水 Tag ${context.beverageTargetTags.join('、')}` : '',
    typeof context.targetFund === 'number' ? `目标营业额 ${context.targetFund}¥` : '',
    targetTagTime,
    targetTime,
    progress,
    targetProgress,
    spellCount,
    context.phase ? `阶段 ${context.phase}` : '',
  ].filter(Boolean);

  return parts.join(' · ');
}

function ResourceUsageRow({
  label,
  value,
  status,
  details,
  overflow,
}: {
  label: string;
  value: string;
  status: 'active' | 'idle' | 'over';
  details: string[];
  overflow: number;
}) {
  const badgeVariant = status === 'over' ? 'destructive' : status === 'active' ? 'secondary' : 'outline';
  return (
    <div className="steward-data-row px-2.5 py-2 text-sm">
      <div className="flex items-center justify-between gap-3">
        <span className="font-medium text-foreground">{label}</span>
        <Badge variant={badgeVariant}>{value}</Badge>
      </div>
      {details.length > 0 && (
        <div className="mt-1 flex flex-wrap gap-1.5 text-xs text-muted-foreground">
          {details.map((item, index) => (
            <span key={`${item}-${index}`} className="max-w-full truncate border border-border/60 px-1.5 py-0.5">
              {item}
            </span>
          ))}
          {overflow > 0 && <span className="px-1.5 py-0.5">+{overflow}</span>}
        </div>
      )}
    </div>
  );
}
