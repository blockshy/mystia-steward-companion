import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { IconTrash } from '@tabler/icons-react';
import { Badge, Button, Card, CardContent, EmptyRow, EmptyState, InfoLine, ListPanel, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import { buildAutomationResourceOverview, buildNightBusinessOrderKey } from '@/companion/domain/automation';
import { sortNightOrderRows, sortNightOrders, sortNormalOrders } from '@/companion/domain/sorting';
import { formatDesk, formatGuestFund, formatPerformanceMs } from '@/companion/formatters';
import type { CompanionPreferences, ServiceOrderSortMode } from '@/companion/preferences';
import type {
  AutomationResourceOverview,
  CustomRecipeData,
  FavoriteData,
  GameUiPinningTarget,
  NightBusinessContext,
  NightBusinessOrder,
  NormalAutoOrderDiagnostic,
  NormalBusinessContext,
  OrderRecommendation,
  RareAutoOrderDiagnostic,
  RecommendationIssue,
  RecommendationStateSnapshot,
  RuntimeSets,
  ToggleBeverageFavorite,
  ToggleRecipeFavorite,
} from '@/companion/types';
import {
  AUTOMATION_SWITCH_GRID,
  DENSE_THREE_COLUMN_GRID,
  DENSE_TWO_COLUMN_GRID,
  MAX_RECOMMENDATION_ROWS,
  MOD_TAB_TRIGGER_CLASS,
  SCROLL_FADE_CLASS,
} from '@/companion/pages/shared-constants';
import {
  AutomationSwitchCell,
  FocusLimitInput,
  OrderRecommendationPanel,
  SwitchControl,
} from '@/companion/pages/shared';
import { buildRecommendationDataIndexes, type RecommendationDataSet } from '@/lib/recommendation-data';
import type { PlaceName } from '@/lib/catalog-types';

export function ModServicePanel({
  runtime,
  night,
  detectedPlace,
  recommendations,
  recommendationIssues,
  data,
  performanceMs,
  runtimeSets,
  uiPinningStatus,
  uiPinningTarget,
  favorites,
  favoriteBusyKey,
  favoriteError,
  customRecipes,
  autoPrepBusy,
  autoPrepMessage,
  autoPrepPaused,
  rareOrderDiagnostics,
  autoPrepPreferences,
  recipeLimit,
  beverageLimit,
  normalOrderBusy,
  normalOrderMessage,
  normalOrderPausedCount,
  normalOrderDiagnostics,
  normalBusiness,
  dismissRareOrderBusyKey,
  dismissRareOrderError,
  onRecipeLimitChange,
  onBeverageLimitChange,
  onPreferenceChange,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
  onRetryRareAutomationOrder,
  onResetRareAutomationOrder,
  onDismissRareOrder,
  onEnterFocusMode,
  showDebugDetails,
}: {
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  detectedPlace: PlaceName | null;
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  data: RecommendationDataSet;
  performanceMs?: Record<string, number>;
  runtimeSets: RuntimeSets | null;
  uiPinningStatus: string;
  uiPinningTarget: GameUiPinningTarget | null;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  customRecipes: CustomRecipeData;
  autoPrepBusy: boolean;
  autoPrepMessage: string;
  autoPrepPaused: boolean;
  rareOrderDiagnostics: RareAutoOrderDiagnostic[];
  autoPrepPreferences: CompanionPreferences;
  recipeLimit: number;
  beverageLimit: number;
  normalOrderBusy: boolean;
  normalOrderMessage: string;
  normalOrderPausedCount: number;
  normalOrderDiagnostics: NormalAutoOrderDiagnostic[];
  normalBusiness: NormalBusinessContext | null;
  dismissRareOrderBusyKey: string;
  dismissRareOrderError: string;
  onRecipeLimitChange: (value: number) => void;
  onBeverageLimitChange: (value: number) => void;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onRetryRareAutomationOrder: (orderKey: string) => void;
  onResetRareAutomationOrder: (orderKey: string) => void;
  onDismissRareOrder: (order: NightBusinessOrder) => void;
  onEnterFocusMode: () => void;
  showDebugDetails: boolean;
}) {
  const dataIndexes = useMemo(() => buildRecommendationDataIndexes(data), [data]);
  const activeGuests = night?.activeRareGuests ?? [];
  const orders = useMemo(
    () => sortNightOrders(night?.orders ?? [], autoPrepPreferences.serviceOrderSortMode),
    [autoPrepPreferences.serviceOrderSortMode, night?.orders],
  );
  const automationResources = useMemo(
    () => buildAutomationResourceOverview({
      runtime,
      recommendations,
      favorites,
      preferences: autoPrepPreferences,
      normalOrders: normalBusiness?.orders ?? [],
      rareDiagnostics: rareOrderDiagnostics,
      normalDiagnostics: normalOrderDiagnostics,
      data,
    }),
    [
      autoPrepPreferences,
      favorites,
      normalBusiness?.orders,
      normalOrderDiagnostics,
      rareOrderDiagnostics,
      recommendations,
      runtime,
      data,
    ],
  );

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
          <InfoLine label="经营场景" value={detectedPlace ?? night?.placeLabel ?? '无经营场景'} />
          <InfoLine label="推荐数据" value={runtime ? '已就绪' : '暂不可用'} />
          {showDebugDetails && <InfoLine label="扫描状态" value={night?.source || '暂无'} />}
          {showDebugDetails && <InfoLine label="性能耗时" value={formatPerformanceMs(performanceMs)} mono />}
          <InfoLine
            label="已摆放厨具"
            value={runtimeSets?.hasCookerSnapshot
              ? [...runtimeSets.placedCookerNames].join('、') || '已读取'
              : runtime?.placedCookerStatus ? `未读取 · ${runtime.placedCookerStatus}` : '未读取'}
          />
          <InfoLine label="目标厨具" value={uiPinningTarget?.cookerName || '暂无'} />
          {showDebugDetails && <InfoLine label="界面置顶" value={uiPinningStatus || '暂无'} />}
        </CardContent>
      </Card>

      {autoPrepPreferences.automationEnabled && <AutomationResourcePanel overview={automationResources} />}

      <Tabs defaultValue="rare" className="space-y-4">
        <TabsList className="grid h-9 w-full grid-cols-2">
          <TabsTrigger value="rare" className={MOD_TAB_TRIGGER_CLASS}>
            稀客
          </TabsTrigger>
          <TabsTrigger value="normal" className={MOD_TAB_TRIGGER_CLASS}>
            普客
          </TabsTrigger>
        </TabsList>

        <TabsContent value="rare" className="space-y-4">
          {autoPrepPreferences.automationEnabled && (
            <RareServiceAutomationPanel
              preferences={autoPrepPreferences}
              busy={autoPrepBusy}
              message={autoPrepMessage}
              paused={autoPrepPaused}
              diagnostics={rareOrderDiagnostics}
              showDebugDetails={showDebugDetails}
              onPreferenceChange={onPreferenceChange}
              onRetryOrder={onRetryRareAutomationOrder}
              onResetOrder={onResetRareAutomationOrder}
            />
          )}

          <div className={DENSE_TWO_COLUMN_GRID}>
            <ListPanel title="当前稀客" contentClassName="min-h-[9rem]">
              {activeGuests.length === 0 && <EmptyRow text="暂无稀客" />}
              {activeGuests.map((guest) => {
                const fund = formatGuestFund(guest);
                return (
                  <div key={`${guest.deskCode}-${guest.guestId}-${guest.source}`} className="flex items-center justify-between border-b py-2 text-sm last:border-b-0">
                    <span className="min-w-0 font-medium">
                      <span>{guest.guestName}</span>
                      {fund && <span className="ml-1 text-muted-foreground">· 金钱 {fund}</span>}
                    </span>
                    <span className="text-muted-foreground">
                      桌 {formatDesk(guest.deskCode)}
                      {showDebugDetails ? ` · ${guest.source}` : ''}
                    </span>
                  </div>
                );
              })}
            </ListPanel>

            <ListPanel title="当前稀客点单" contentClassName="min-h-[9rem]">
              {orders.length === 0 && <EmptyRow text={night?.error || '暂无点单'} />}
              {dismissRareOrderError && <EmptyRow text={dismissRareOrderError} />}
              {orders.map((order) => {
                const orderKey = buildNightBusinessOrderKey(order);
                const busy = dismissRareOrderBusyKey === orderKey;
                return (
                  <div key={orderKey} className="border-b py-2 text-sm last:border-b-0">
                    <div className="grid grid-cols-[minmax(0,1fr)_auto] items-start gap-2">
                      <div className="min-w-0">
                        <div className="flex items-center justify-between gap-3">
                          <span className="truncate font-medium" title={order.guestName}>{order.guestName}</span>
                          <span className="shrink-0 text-muted-foreground">桌 {formatDesk(order.deskCode)}</span>
                        </div>
                        <div className="mt-1 flex flex-wrap gap-1.5">
                          <Badge variant="outline">
                            料理 {order.foodTag || '无'}{showDebugDetails ? ` (${order.foodTagId})` : ''}
                          </Badge>
                          <Badge variant="outline">
                            酒水 {order.beverageTag || '无'}{showDebugDetails ? ` (${order.beverageTagId})` : ''}
                          </Badge>
                          {showDebugDetails && <Badge variant="secondary">{order.source}</Badge>}
                        </div>
                      </div>
                      <Button
                        type="button"
                        size="icon"
                        variant="ghost"
                        className="size-8 text-muted-foreground hover:text-destructive"
                        title="删除这笔稀客订单缓存"
                        aria-label="删除这笔稀客订单缓存"
                        disabled={busy}
                        data-gamepad-clickable="true"
                        data-gamepad-focus-key={`rare-order-dismiss:${orderKey}`}
                        onClick={() => onDismissRareOrder(order)}
                      >
                        <IconTrash className="size-4" />
                      </Button>
                    </div>
                  </div>
                );
              })}
            </ListPanel>
          </div>

          <CurrentOrderRecommendations
            recommendations={recommendations}
            recommendationIssues={recommendationIssues}
            runtimeSets={runtimeSets}
            dataIndexes={dataIndexes}
            orderSortMode={autoPrepPreferences.serviceOrderSortMode}
            showDebugDetails={showDebugDetails}
            favorites={favorites}
            customRecipes={customRecipes}
            favoriteBusyKey={favoriteBusyKey}
            favoriteError={favoriteError}
            action={(
              <ServiceRecommendationHeaderActions
                recipeLimit={recipeLimit}
                beverageLimit={beverageLimit}
                onRecipeLimitChange={onRecipeLimitChange}
                onBeverageLimitChange={onBeverageLimitChange}
                onEnterFocusMode={onEnterFocusMode}
              />
            )}
            recipeLimit={recipeLimit}
            beverageLimit={beverageLimit}
            onToggleRecipeFavorite={onToggleRecipeFavorite}
            onToggleBeverageFavorite={onToggleBeverageFavorite}
          />
        </TabsContent>

        <TabsContent value="normal" className="space-y-4">
          {autoPrepPreferences.automationEnabled && (
            <NormalServiceAutomationPanel
              preferences={autoPrepPreferences}
              busy={normalOrderBusy}
              message={normalOrderMessage}
              pausedCount={normalOrderPausedCount}
              diagnostics={normalOrderDiagnostics}
              showDebugDetails={showDebugDetails}
              onPreferenceChange={onPreferenceChange}
            />
          )}

          <ListPanel title={`${showDebugDetails ? '普客订单诊断' : '普客订单'} (${normalBusiness?.orders.length ?? 0})`} contentClassName="min-h-[18rem]">
            {autoPrepPreferences.automationEnabled && autoPrepPreferences.autoNormalOrderEnabled ? (
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2 steward-muted-surface-35 px-3 py-2 text-sm">
                <span className="text-muted-foreground">
                  普客自动化会按开关处理普客订单，可执行送达酒水、制作料理、送达料理和完成订单。
                </span>
                {normalOrderBusy && <Badge variant="secondary">处理中</Badge>}
              </div>
            ) : autoPrepPreferences.automationEnabled ? (
              <div className="mb-3 steward-muted-surface-35 px-3 py-2 text-sm text-muted-foreground">
                开启“启用普客处理”后，可按阶段开关自动处理普客订单。
              </div>
            ) : (
              <div className="mb-3 steward-muted-surface-35 px-3 py-2 text-sm text-muted-foreground">
                设置页开启“启用自动化（实验性）”后，可启用普客订单自动处理。
              </div>
            )}
            {normalOrderMessage && !autoPrepPreferences.automationEnabled && (
              <div className="mb-3 whitespace-pre-line steward-muted-surface-40 px-3 py-2 text-sm text-muted-foreground">
                {normalOrderMessage}
              </div>
            )}
            {!normalBusiness && <EmptyRow text="普客订单只在经营场景中读取" />}
            {normalBusiness?.error && <EmptyRow text={normalBusiness.error} />}
            {normalBusiness?.orders.length === 0 && !normalBusiness.error && (
              <EmptyRow text={normalBusiness.source || '暂无普客订单'} />
            )}
            {sortNormalOrders(normalBusiness?.orders ?? []).map((order) => (
              <div
                key={`${order.deskCode}-${order.guestName}-${order.foodId}-${order.beverageId}-${order.source}`}
                className="border-b py-2 text-sm last:border-b-0"
              >
                <div className="flex items-center justify-between gap-3">
                  <span className="min-w-0 truncate font-medium" title={order.guestName || '普客'}>
                    {order.guestName || '普客'}
                  </span>
                  <span className="shrink-0 text-muted-foreground">桌 {formatDesk(order.deskCode)}</span>
                </div>
                <div className="mt-1 flex flex-wrap gap-1.5">
                  <Badge variant="outline">料理 {order.foodName || `#${order.foodId}`}</Badge>
                  <Badge variant="outline">酒水 {order.beverageName || `#${order.beverageId}`}</Badge>
                  {order.hasServedFood && <Badge variant="secondary">已有料理</Badge>}
                  {order.hasServedBeverage && <Badge variant="secondary">已有酒水</Badge>}
                  {order.readyToEvaluate && !order.hasEvaluated && <Badge variant="secondary">待评价</Badge>}
                  {order.hasEvaluated && <Badge variant="secondary">已评价</Badge>}
                  {order.canAutomate === false && <Badge variant="outline">暂不可自动处理</Badge>}
                  {showDebugDetails && <Badge variant="secondary">{order.source}</Badge>}
                </div>
                {order.canAutomate === false && order.actionBlockReason && (
                  <div className="mt-1 text-xs text-muted-foreground">
                    {order.actionBlockReason}
                  </div>
                )}
              </div>
            ))}
          </ListPanel>
        </TabsContent>
      </Tabs>
    </div>
  );
}

function AutomationResourcePanel({ overview }: { overview: AutomationResourceOverview }) {
  const hasCookerRows = overview.cookers.length > 0;

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
    </ListPanel>
  );
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

function ServiceRecommendationHeaderActions({
  recipeLimit,
  beverageLimit,
  onRecipeLimitChange,
  onBeverageLimitChange,
  onEnterFocusMode,
}: {
  recipeLimit: number;
  beverageLimit: number;
  onRecipeLimitChange: (value: number) => void;
  onBeverageLimitChange: (value: number) => void;
  onEnterFocusMode: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center justify-end gap-2">
      <FocusLimitInput label="料理" value={recipeLimit} onChange={onRecipeLimitChange} />
      <FocusLimitInput label="酒水" value={beverageLimit} onChange={onBeverageLimitChange} />
      <Button size="sm" onClick={onEnterFocusMode}>
        稀客订单专注模式
      </Button>
    </div>
  );
}

export function ServiceFocusPage({
  recommendations,
  recommendationIssues,
  runtimeSets,
  dataIndexes,
  orderSortMode,
  showDebugDetails,
  favorites,
  customRecipes,
  favoriteBusyKey,
  favoriteError,
  compact,
  recipeLimit,
  beverageLimit,
  onCompactChange,
  onRecipeLimitChange,
  onBeverageLimitChange,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
  onExit,
}: {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
  orderSortMode: ServiceOrderSortMode;
  showDebugDetails: boolean;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  favoriteBusyKey: string;
  favoriteError: string;
  compact: boolean;
  recipeLimit: number;
  beverageLimit: number;
  onCompactChange: (value: boolean) => void;
  onRecipeLimitChange: (value: number) => void;
  onBeverageLimitChange: (value: number) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onExit: () => void;
}) {
  const hasOrders = recommendations.length > 0 || recommendationIssues.length > 0;

  return (
    <div className="flex min-h-[calc(100dvh-1rem)] flex-col gap-4" data-gamepad-scope="content">
      <div className="flex shrink-0 flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-foreground">稀客订单专注模式</h1>
          <p className="mt-1 text-sm text-muted-foreground">只显示当前稀客点单推荐。</p>
        </div>
        <div className="flex flex-wrap items-center justify-end gap-3">
          <SwitchControl
            label="精简模式"
            checked={compact}
            onCheckedChange={onCompactChange}
          />
          <FocusLimitInput
            label="料理"
            value={recipeLimit}
            onChange={onRecipeLimitChange}
          />
          <FocusLimitInput
            label="酒水"
            value={beverageLimit}
            onChange={onBeverageLimitChange}
          />
          <Button size="sm" onClick={onExit}>退出专注模式</Button>
        </div>
      </div>

      {hasOrders ? (
        <CurrentOrderRecommendations
          recommendations={recommendations}
          recommendationIssues={recommendationIssues}
          runtimeSets={runtimeSets}
          dataIndexes={dataIndexes}
          orderSortMode={orderSortMode}
          showDebugDetails={showDebugDetails}
          favorites={favorites}
          customRecipes={customRecipes}
          favoriteBusyKey={favoriteBusyKey}
          favoriteError={favoriteError}
          compact={compact}
          fillAvailableHeight
          recipeLimit={recipeLimit}
          beverageLimit={beverageLimit}
          onToggleRecipeFavorite={onToggleRecipeFavorite}
          onToggleBeverageFavorite={onToggleBeverageFavorite}
        />
      ) : (
        <EmptyState text="暂无当前稀客点单。检测到稀客点单后，这里会自动显示推荐料理和酒水。" />
      )}
    </div>
  );
}

function CurrentOrderRecommendations({
  recommendations,
  recommendationIssues,
  runtimeSets,
  dataIndexes,
  orderSortMode,
  showDebugDetails = false,
  favorites,
  customRecipes,
  favoriteBusyKey,
  favoriteError,
  action,
  compact = false,
  fillAvailableHeight = false,
  recipeLimit = MAX_RECOMMENDATION_ROWS,
  beverageLimit = MAX_RECOMMENDATION_ROWS,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
}: {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
  orderSortMode: ServiceOrderSortMode;
  showDebugDetails?: boolean;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  favoriteBusyKey: string;
  favoriteError: string;
  action?: ReactNode;
  compact?: boolean;
  fillAvailableHeight?: boolean;
  recipeLimit?: number;
  beverageLimit?: number;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
}) {
  const rows = useMemo(
    () => sortNightOrderRows([
      ...recommendationIssues.map((issue) => ({ kind: 'issue' as const, order: issue.order, issue })),
      ...recommendations.map((item) => ({ kind: 'recommendation' as const, order: item.order, item })),
    ], orderSortMode),
    [orderSortMode, recommendationIssues, recommendations],
  );

  return (
    <ListPanel
      title="当前点单推荐"
      action={action}
      className={fillAvailableHeight ? 'min-h-0 flex-1' : undefined}
      contentClassName={
        fillAvailableHeight
          ? `${SCROLL_FADE_CLASS} min-h-0 flex-1 overflow-auto pb-4 pr-1`
          : compact
          ? `${SCROLL_FADE_CLASS} min-h-[24rem] max-h-[calc(100vh-12rem)] overflow-auto pb-4 pr-1`
          : `${SCROLL_FADE_CLASS} min-h-[32rem] max-h-[calc(100vh-20rem)] overflow-auto pb-4 pr-1`
      }
    >
      {favoriteError && (
        <div className="mb-2 border border-destructive/30 px-3 py-2 text-sm text-destructive">
          {favoriteError}
        </div>
      )}
      {rows.length === 0 && <EmptyRow text="暂无当前稀客点单推荐" />}
      <div className={compact ? 'space-y-2' : 'space-y-4'}>
        {rows.map((row) => {
          if (row.kind === 'issue') {
            const issue = row.issue;
            return (
              <div
                key={`${issue.order.deskCode}-${issue.order.guestId}-issue`}
                className={compact ? 'steward-data-row p-2 text-xs' : 'steward-data-row p-3 text-sm'}
              >
                <div className="font-medium">{issue.order.guestName} · 桌 {formatDesk(issue.order.deskCode)}</div>
                <div className="mt-1 text-xs text-muted-foreground">{issue.message}</div>
              </div>
            );
          }

          return (
            <OrderRecommendationPanel
              key={`${row.item.order.deskCode}-${row.item.order.guestId}-${row.item.order.foodTagId}-${row.item.order.beverageTagId}`}
              item={row.item}
              runtimeSets={runtimeSets}
              dataIndexes={dataIndexes}
              favorites={favorites}
              customRecipes={customRecipes}
              favoriteBusyKey={favoriteBusyKey}
              compact={compact}
              recipeLimit={recipeLimit}
              beverageLimit={beverageLimit}
              showDebugDetails={showDebugDetails}
              onToggleRecipeFavorite={onToggleRecipeFavorite}
              onToggleBeverageFavorite={onToggleBeverageFavorite}
            />
          );
        })}
      </div>
    </ListPanel>
  );
}

function RareServiceAutomationPanel({
  preferences,
  busy,
  message,
  paused,
  diagnostics,
  showDebugDetails,
  onPreferenceChange,
  onRetryOrder,
  onResetOrder,
}: {
  preferences: CompanionPreferences;
  busy: boolean;
  message: string;
  paused: boolean;
  diagnostics: RareAutoOrderDiagnostic[];
  showDebugDetails: boolean;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  return (
    <ListPanel title="稀客自动化（实验性）">
      <div className={AUTOMATION_SWITCH_GRID}>
        <AutomationSwitchCell
          label="自动完成订单"
          checked={preferences.autoPrepCompleteOrder}
          onCheckedChange={(autoPrepCompleteOrder) => onPreferenceChange({ autoPrepCompleteOrder })}
        />
        <AutomationSwitchCell
          label="自动送达酒水"
          checked={preferences.autoPrepTakeBeverage}
          onCheckedChange={(autoPrepTakeBeverage) => onPreferenceChange({ autoPrepTakeBeverage })}
        />
        <AutomationSwitchCell
          label="自动开始料理"
          checked={preferences.autoPrepStartCooking}
          onCheckedChange={(autoPrepStartCooking) => onPreferenceChange({ autoPrepStartCooking })}
        />
        <AutomationSwitchCell
          label="出锅后送达"
          checked={preferences.autoPrepCollectCooking}
          onCheckedChange={(autoPrepCollectCooking) => onPreferenceChange({ autoPrepCollectCooking })}
        />
        <AutomationSwitchCell
          label="只处理收藏料理"
          checked={preferences.autoPrepRecipeFavoritesOnly}
          onCheckedChange={(autoPrepRecipeFavoritesOnly) => onPreferenceChange({ autoPrepRecipeFavoritesOnly })}
        />
        <AutomationSwitchCell
          label="只处理收藏酒水"
          checked={preferences.autoPrepBeverageFavoritesOnly}
          onCheckedChange={(autoPrepBeverageFavoritesOnly) => onPreferenceChange({ autoPrepBeverageFavoritesOnly })}
        />
        <AutomationSwitchCell
          label="出错时暂停"
          checked={preferences.autoPrepStopOnError}
          onCheckedChange={(autoPrepStopOnError) => onPreferenceChange({ autoPrepStopOnError })}
        />
      </div>
      <RareAutoPrepStatus
        busy={busy}
        paused={paused}
        message={message}
        preferences={preferences}
        diagnostics={diagnostics}
        showDebugDetails={showDebugDetails}
        onRetryOrder={onRetryOrder}
        onResetOrder={onResetOrder}
      />
    </ListPanel>
  );
}

function NormalServiceAutomationPanel({
  preferences,
  busy,
  message,
  pausedCount,
  diagnostics,
  showDebugDetails,
  onPreferenceChange,
}: {
  preferences: CompanionPreferences;
  busy: boolean;
  message: string;
  pausedCount: number;
  diagnostics: NormalAutoOrderDiagnostic[];
  showDebugDetails: boolean;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
}) {
  return (
    <ListPanel title="普客自动化（实验性）">
      <div className={AUTOMATION_SWITCH_GRID}>
        <AutomationSwitchCell
          label="启用普客处理"
          checked={preferences.autoNormalOrderEnabled}
          onCheckedChange={(autoNormalOrderEnabled) => onPreferenceChange({ autoNormalOrderEnabled })}
        />
        {preferences.autoNormalOrderEnabled && (
          <>
            <AutomationSwitchCell
              label="自动送达酒水"
              checked={preferences.autoNormalTakeBeverage}
              onCheckedChange={(autoNormalTakeBeverage) => onPreferenceChange({ autoNormalTakeBeverage })}
            />
            <AutomationSwitchCell
              label="自动开始料理"
              checked={preferences.autoNormalStartCooking}
              onCheckedChange={(autoNormalStartCooking) => onPreferenceChange({ autoNormalStartCooking })}
            />
            <AutomationSwitchCell
              label="自动送达料理"
              checked={preferences.autoNormalDeliverFood}
              onCheckedChange={(autoNormalDeliverFood) => onPreferenceChange({
                autoNormalDeliverFood,
                autoNormalCollectCooking: autoNormalDeliverFood,
              })}
            />
            <AutomationSwitchCell
              label="自动完成订单"
              checked={preferences.autoNormalCompleteOrder}
              onCheckedChange={(autoNormalCompleteOrder) => onPreferenceChange({ autoNormalCompleteOrder })}
            />
            <AutomationSwitchCell
              label="出错时暂停"
              checked={preferences.autoNormalStopOnError}
              onCheckedChange={(autoNormalStopOnError) => onPreferenceChange({ autoNormalStopOnError })}
            />
          </>
        )}
      </div>
      <NormalAutoPrepStatus
        busy={busy}
        pausedCount={pausedCount}
        message={message}
        preferences={preferences}
        diagnostics={diagnostics}
        showDebugDetails={showDebugDetails}
      />
    </ListPanel>
  );
}

function RareAutoPrepStatus({
  busy,
  paused,
  message,
  preferences,
  diagnostics,
  showDebugDetails,
  onRetryOrder,
  onResetOrder,
}: {
  busy: boolean;
  paused: boolean;
  message: string;
  preferences: CompanionPreferences;
  diagnostics: RareAutoOrderDiagnostic[];
  showDebugDetails: boolean;
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  return (
    <div className="steward-inline-panel mt-3 px-3 py-2 text-sm">
      <div className="font-medium text-foreground">稀客自动化{busy ? '处理中' : '状态'}</div>
      {diagnostics.length === 0 ? (
        <div className="steward-data-row mt-2 px-2.5 py-2 text-xs text-muted-foreground">
          暂无正在处理的稀客订单。
        </div>
      ) : (
        <div className="mt-2 space-y-2">
          {diagnostics.map((diagnostic) => (
            <div key={diagnostic.orderKey} className="steward-data-row px-2.5 py-2">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="truncate font-medium text-foreground">{diagnostic.title}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    料理 {diagnostic.foodTag || '无'} · 酒水 {diagnostic.beverageTag || '无'}
                  </div>
                </div>
                <div className="flex shrink-0 gap-1.5" data-gamepad-axis="x">
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => onRetryOrder(diagnostic.orderKey)}
                    disabled={busy || !diagnostic.paused}
                    data-gamepad-focus-key={`rare-auto:${diagnostic.orderKey}:retry`}
                  >
                    重试
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => onResetOrder(diagnostic.orderKey)}
                    disabled={busy}
                    data-gamepad-focus-key={`rare-auto:${diagnostic.orderKey}:reset`}
                  >
                    重置
                  </Button>
                </div>
              </div>
              <div className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-muted-foreground max-[479px]:grid-cols-1 md:grid-cols-5">
                <InfoLine label="料理" value={diagnostic.recipeName || '未选择'} />
                <InfoLine label="酒水" value={diagnostic.beverageName || '未选择'} />
                <InfoLine label="步骤" value={`${diagnostic.stepLabel} · ${diagnostic.stepSeconds}秒`} />
                <InfoLine label="下次" value={diagnostic.nextAction} />
                {showDebugDetails && (
                  <InfoLine
                    label="计数"
                    value={`重试 ${diagnostic.retryCount}/${preferences.autoMaxStepRetries} · 回退 ${diagnostic.rollbackCount}/${preferences.autoMaxRollbacks}`}
                  />
                )}
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
                <Badge variant={diagnostic.paused ? 'destructive' : 'secondary'}>
                  {diagnostic.paused ? '暂停' : '运行'}
                </Badge>
                <Badge variant={diagnostic.prepared ? 'secondary' : 'outline'}>
                  料理{diagnostic.prepared ? '已开锅' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.beverageHandled ? 'secondary' : 'outline'}>
                  酒水{diagnostic.beverageHandled ? '已送达' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.hasServedFood ? 'secondary' : 'outline'}>
                  订单{diagnostic.hasServedFood ? '已有料理' : '未送料理'}
                </Badge>
                <Badge variant={diagnostic.hasServedBeverage ? 'secondary' : 'outline'}>
                  订单{diagnostic.hasServedBeverage ? '已有酒水' : '未送酒水'}
                </Badge>
              </div>
              {diagnostic.lastError && (
                <div className="mt-1 text-xs text-muted-foreground">最近：{diagnostic.lastError}</div>
              )}
            </div>
          ))}
        </div>
      )}
      <div className="mt-2 whitespace-pre-line text-muted-foreground">
        {message || '等待稀客订单或自动化条件。'}
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
        <Badge variant={paused ? 'destructive' : 'secondary'}>{paused ? '已暂停' : '运行中'}</Badge>
        <Badge variant="outline">每轮最多 {preferences.autoRareConcurrency}</Badge>
        <Badge variant={preferences.autoPrepCompleteOrder ? 'secondary' : 'outline'}>完成 {preferences.autoPrepCompleteOrder ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepTakeBeverage ? 'secondary' : 'outline'}>送酒 {preferences.autoPrepTakeBeverage ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepStartCooking ? 'secondary' : 'outline'}>料理 {preferences.autoPrepStartCooking ? '开' : '关'}</Badge>
        {preferences.autoPrepStartCooking && <Badge variant="secondary">QTE 自动完成</Badge>}
        <Badge variant={preferences.autoPrepCollectCooking ? 'secondary' : 'outline'}>直送 {preferences.autoPrepCollectCooking ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepRecipeFavoritesOnly ? 'secondary' : 'outline'}>收藏料理 {preferences.autoPrepRecipeFavoritesOnly ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepBeverageFavoritesOnly ? 'secondary' : 'outline'}>收藏酒水 {preferences.autoPrepBeverageFavoritesOnly ? '开' : '关'}</Badge>
      </div>
    </div>
  );
}

function NormalAutoPrepStatus({
  busy,
  pausedCount,
  message,
  preferences,
  diagnostics,
  showDebugDetails,
}: {
  busy: boolean;
  pausedCount: number;
  message: string;
  preferences: CompanionPreferences;
  diagnostics: NormalAutoOrderDiagnostic[];
  showDebugDetails: boolean;
}) {
  return (
    <div className="steward-inline-panel mt-3 px-3 py-2 text-sm">
      <div className="font-medium text-foreground">普客自动化{busy ? '处理中' : '状态'}</div>
      {diagnostics.length === 0 ? (
        <div className="steward-data-row mt-2 px-2.5 py-2 text-xs text-muted-foreground">
          暂无正在处理的普客订单。
        </div>
      ) : (
        <div className="mt-2 space-y-2">
          {diagnostics.map((diagnostic) => (
            <div key={diagnostic.orderKey} className="steward-data-row px-2.5 py-2">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="truncate font-medium text-foreground">{diagnostic.title}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    料理 {diagnostic.foodName || '无'} · 酒水 {diagnostic.beverageName || '无'}
                  </div>
                </div>
                <Badge variant={diagnostic.paused ? 'destructive' : 'secondary'}>
                  {diagnostic.paused ? '暂停' : '运行'}
                </Badge>
              </div>
              <div className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-muted-foreground max-[479px]:grid-cols-1 md:grid-cols-5">
                <InfoLine label="步骤" value={`${diagnostic.stepLabel} · ${diagnostic.stepSeconds}秒`} />
                <InfoLine label="下次" value={diagnostic.nextAction} />
                {showDebugDetails && (
                  <>
                    <InfoLine
                      label="计数"
                      value={`重试 ${diagnostic.retryCount}/${preferences.autoMaxStepRetries} · 回退 ${diagnostic.rollbackCount}/${preferences.autoMaxRollbacks}`}
                    />
                    <InfoLine label="来源" value={diagnostic.source || '未知'} />
                    <InfoLine label="Key" value={diagnostic.orderKey} mono />
                  </>
                )}
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
                <Badge variant={diagnostic.beverageHandled ? 'secondary' : 'outline'}>
                  酒水{diagnostic.beverageHandled ? '已送达' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.prepared ? 'secondary' : 'outline'}>
                  料理{diagnostic.prepared ? '已开锅' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.foodDelivered ? 'secondary' : 'outline'}>
                  料理{diagnostic.foodDelivered ? '已送达' : '未送达'}
                </Badge>
                <Badge variant={diagnostic.hasServedFood ? 'secondary' : 'outline'}>
                  订单{diagnostic.hasServedFood ? '已有料理' : '未送料理'}
                </Badge>
                <Badge variant={diagnostic.hasServedBeverage ? 'secondary' : 'outline'}>
                  订单{diagnostic.hasServedBeverage ? '已有酒水' : '未送酒水'}
                </Badge>
                <Badge variant={diagnostic.readyToEvaluate ? 'secondary' : 'outline'}>
                  评价{diagnostic.readyToEvaluate ? '待触发' : '未满足'}
                </Badge>
                <Badge variant={diagnostic.completed ? 'secondary' : 'outline'}>
                  订单{diagnostic.completed ? '已评价' : '未评价'}
                </Badge>
              </div>
              {diagnostic.lastError && (
                <div className="mt-1 text-xs text-muted-foreground">最近：{diagnostic.lastError}</div>
              )}
            </div>
          ))}
        </div>
      )}
      <div className="mt-1 whitespace-pre-line text-muted-foreground">
        {message || '等待普客订单或自动化条件。'}
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
        <Badge variant={pausedCount > 0 ? 'destructive' : 'secondary'}>暂停订单 {pausedCount}</Badge>
        <Badge variant="outline">每轮最多 {preferences.autoNormalConcurrency}</Badge>
        <Badge variant={preferences.autoNormalOrderEnabled ? 'secondary' : 'outline'}>启用 {preferences.autoNormalOrderEnabled ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoNormalTakeBeverage ? 'secondary' : 'outline'}>酒水 {preferences.autoNormalTakeBeverage ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoNormalStartCooking ? 'secondary' : 'outline'}>料理 {preferences.autoNormalStartCooking ? '开' : '关'}</Badge>
        {preferences.autoNormalStartCooking && <Badge variant="secondary">QTE 自动完成</Badge>}
        <Badge variant={preferences.autoNormalDeliverFood ? 'secondary' : 'outline'}>送料理 {preferences.autoNormalDeliverFood ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoNormalCompleteOrder ? 'secondary' : 'outline'}>完成 {preferences.autoNormalCompleteOrder ? '开' : '关'}</Badge>
      </div>
    </div>
  );
}
