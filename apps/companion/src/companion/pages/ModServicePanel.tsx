import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { IconTrash } from '@tabler/icons-react';
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
  Badge,
  Button,
  Card,
  CardContent,
  EmptyRow,
  EmptyState,
  InfoLine,
  ListPanel,
  SegmentedControl,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@/components/ui-kit';
import { buildAutomationResourceOverview, buildNightBusinessOrderKey } from '@/companion/domain/automation';
import {
  getNightBusinessAutomationPauseLabel,
  getNightBusinessAutomationSummary,
} from '@/companion/domain/automation-runtime';
import type { NormalOrderDetailPlan } from '@/companion/domain/normal-order-details';
import { sortNightOrderRows, sortNightOrders } from '@/companion/domain/sorting';
import { formatDesk, formatGuestFund, formatPerformanceMs } from '@/companion/formatters';
import type { CompanionPreferences, ServiceOrderSortMode } from '@/companion/preferences';
import type {
  AutomationSafetyBarrierDiagnostic,
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
  SpecialBusinessContext,
  ToggleBeverageFavorite,
  ToggleRecipeFavorite,
} from '@/companion/types';
import type { NormalExecutionTargetSelection } from '@/companion/workers/order-recommendations.types';
import {
  DENSE_THREE_COLUMN_GRID,
  DENSE_TWO_COLUMN_GRID,
  MAX_RECOMMENDATION_ROWS,
  MOD_TAB_TRIGGER_CLASS,
  SCROLL_FADE_CLASS,
} from '@/companion/pages/shared-constants';
import {
  FocusLimitInput,
  OrderRecommendationPanel,
  SwitchControl,
} from '@/companion/pages/shared';
import {
  AutomationResourceDiagnosticPanel,
  OrderTraceBadge,
  SpecialBusinessNotice,
  SpecialBusinessOrderList,
} from '@/companion/pages/service/ServiceContextPanels';
import { NormalOrderDetailCard } from '@/companion/pages/service/NormalOrderDetailCard';
import { buildRecommendationDataIndexes, type RecommendationDataSet } from '@/lib/recommendation-data';
import type { PlaceName } from '@/lib/catalog-types';

export type ServicePanelView = 'recommendations' | 'automation' | 'diagnostics';
export type ServiceRecommendationTab = 'rare' | 'normal';

const SERVICE_PANEL_VIEW_OPTIONS: { value: ServicePanelView; label: string }[] = [
  { value: 'recommendations', label: '推荐' },
  { value: 'automation', label: '自动化' },
  { value: 'diagnostics', label: '诊断' },
];

const SERVICE_PANEL_DEFAULT_VIEW_OPTIONS = SERVICE_PANEL_VIEW_OPTIONS.filter((option) => option.value !== 'diagnostics');

function formatAutomationDetailTime(value: number): string {
  if (value <= 0) return '';
  return new Date(value).toLocaleTimeString('zh-CN', {
    hour12: false,
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
}

function AutomationDetailAccordion({
  id,
  detailMessage,
  detailUpdatedAtMs,
}: {
  id: string;
  detailMessage: string;
  detailUpdatedAtMs: number;
}) {
  const detail = detailMessage.trim();
  if (!detail) return null;
  const updatedAt = formatAutomationDetailTime(detailUpdatedAtMs);

  return (
    <Accordion className="mt-2 text-xs">
      <AccordionItem value={id}>
        <AccordionTrigger
          className="px-2 py-1.5 text-xs"
          data-gamepad-focus-key={`automation-detail:${id}`}
        >
          <span className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
            <span>自动化详情</span>
            {updatedAt && <span className="text-muted-foreground">更新 {updatedAt}</span>}
          </span>
        </AccordionTrigger>
        <AccordionContent className="text-xs text-muted-foreground">
          <div className="whitespace-pre-line leading-relaxed">{detail}</div>
        </AccordionContent>
      </AccordionItem>
    </Accordion>
  );
}

function formatServiceSpecialBusinessSummary(context: SpecialBusinessContext | null): string {
  if (!context?.active) return '无';
  const parts = [
    context.displayName || context.challengeType || '特殊经营',
    context.phase ? `阶段 ${context.phase}` : '',
    context.foodTargetTags.length > 0 ? `料理 ${context.foodTargetTags.join('、')}` : '',
    context.beverageTargetTags.length > 0 ? `酒水 ${context.beverageTargetTags.join('、')}` : '',
  ].filter(Boolean);
  return parts.join(' · ');
}

function formatPlacedCookerSummary(
  runtime: RecommendationStateSnapshot | null,
  runtimeSets: RuntimeSets | null,
  applicable: boolean,
): string {
  if (!applicable) return '不适用';
  if (!runtime) return '未读取';

  const names = [...(runtimeSets?.placedCookerNames ?? [])].join('、');
  if (runtime.placedCookerSnapshotComplete) {
    const locked = runtime.placedCookerLockedControllerCount > 0
      ? `${runtime.placedCookerLockedControllerCount} 个厨具被事件锁定`
      : '';
    if (runtime.placedCookers.length === 0) return `已读取 · ${locked || '未摆放'}`;
    return ['已读取', names || `${runtime.placedCookers.length} 个厨具类型未识别`, locked]
      .filter(Boolean)
      .join(' · ');
  }

  return `读取不可用${runtime.placedCookerStatus ? ` · ${runtime.placedCookerStatus}` : ''}`;
}

function buildNormalOrderDetailPlanKey(plan: NormalOrderDetailPlan): string {
  const { order } = plan;
  return order.orderKey
    ?? order.traceId
    ?? `${order.deskCode}-${order.guestName}-${order.foodId}-${order.beverageId}-${order.source}`;
}

export function ModServicePanel({
  runtime,
  nightBusinessActive,
  night,
  specialBusiness,
  detectedPlace,
  recommendations,
  recommendationIssues,
  recommendationPendingOrders,
  recommendationsPending,
  recommendationUpdateError,
  data,
  performanceMs,
  orderRecommendationPerformanceMs,
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
  automationRuntimeAllowed,
  automationRuntimeBlockReason,
  automationRuntimeStatus,
  automationSafetyBarriers,
  automationBarrierAckBusyKey,
  normalExecutionTargets,
  normalExecutionTargetsEnabled,
  normalExecutionTargetsPending,
  normalExecutionTargetsError,
  normalOrderDetailPlans,
  normalOrderDetailsPending,
  normalOrderDetailsError,
  normalBusiness,
  serviceView,
  serviceRecommendationTab,
  dismissRareOrderBusyKey,
  dismissRareOrderError,
  onRecipeLimitChange,
  onBeverageLimitChange,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
  onRetryRareAutomationOrder,
  onResetRareAutomationOrder,
  onRetryNormalAutomationOrder,
  onResetNormalAutomationOrder,
  onAcknowledgeAutomationBarrier,
  onDismissRareOrder,
  onEnterFocusMode,
  onServiceViewChange,
  onServiceRecommendationTabChange,
  showDebugDetails,
}: {
  runtime: RecommendationStateSnapshot | null;
  nightBusinessActive: boolean;
  night: NightBusinessContext | null;
  specialBusiness: SpecialBusinessContext | null;
  detectedPlace: PlaceName | null;
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  recommendationPendingOrders: NightBusinessOrder[];
  recommendationsPending: boolean;
  recommendationUpdateError: string | null;
  data: RecommendationDataSet;
  performanceMs?: Record<string, number>;
  orderRecommendationPerformanceMs?: Record<string, number>;
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
  automationRuntimeAllowed: boolean;
  automationRuntimeBlockReason: string;
  automationRuntimeStatus: string;
  automationSafetyBarriers: AutomationSafetyBarrierDiagnostic[];
  automationBarrierAckBusyKey: string;
  normalExecutionTargets: NormalExecutionTargetSelection[];
  normalExecutionTargetsEnabled: boolean;
  normalExecutionTargetsPending: boolean;
  normalExecutionTargetsError: string | null;
  normalOrderDetailPlans: NormalOrderDetailPlan[];
  normalOrderDetailsPending: boolean;
  normalOrderDetailsError: string | null;
  normalBusiness: NormalBusinessContext | null;
  serviceView: ServicePanelView;
  serviceRecommendationTab: ServiceRecommendationTab;
  dismissRareOrderBusyKey: string;
  dismissRareOrderError: string;
  onRecipeLimitChange: (value: number) => void;
  onBeverageLimitChange: (value: number) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onRetryRareAutomationOrder: (orderKey: string) => void;
  onResetRareAutomationOrder: (orderKey: string) => void;
  onRetryNormalAutomationOrder: (orderKey: string) => void;
  onResetNormalAutomationOrder: (orderKey: string) => void;
  onAcknowledgeAutomationBarrier: (sequence: number) => void;
  onDismissRareOrder: (order: NightBusinessOrder) => void;
  onEnterFocusMode: () => void;
  onServiceViewChange: (value: ServicePanelView) => void;
  onServiceRecommendationTabChange: (value: ServiceRecommendationTab) => void;
  showDebugDetails: boolean;
}) {
  const dataIndexes = useMemo(() => buildRecommendationDataIndexes(data), [data]);
  const activeGuests = night?.activeRareGuests ?? [];
  const orders = useMemo(
    () => sortNightOrders(night?.orders ?? [], autoPrepPreferences.serviceOrderSortMode),
    [autoPrepPreferences.serviceOrderSortMode, night?.orders],
  );
  const activeServiceView = showDebugDetails || serviceView !== 'diagnostics'
    ? serviceView
    : 'recommendations';
  const automationResources = useMemo(
    () => {
      if (activeServiceView !== 'diagnostics' || !showDebugDetails) {
        return { cookers: [], normalBlocked: [] };
      }

      return buildAutomationResourceOverview({
        runtime,
        recommendations,
        favorites,
        preferences: autoPrepPreferences,
        normalOrders: normalBusiness?.orders ?? [],
        specialBusiness,
        normalExecutionTargets,
        normalExecutionTargetsEnabled,
        normalExecutionTargetsPending,
        normalExecutionTargetsError,
        rareDiagnostics: rareOrderDiagnostics,
        normalDiagnostics: normalOrderDiagnostics,
        data,
      });
    },
    [
      activeServiceView,
      autoPrepPreferences,
      favorites,
      normalExecutionTargets,
      normalExecutionTargetsEnabled,
      normalExecutionTargetsError,
      normalExecutionTargetsPending,
      normalBusiness?.orders,
      normalOrderDiagnostics,
      rareOrderDiagnostics,
      recommendations,
      runtime,
      showDebugDetails,
      specialBusiness,
      data,
    ],
  );
  const serviceViewOptions = showDebugDetails ? SERVICE_PANEL_VIEW_OPTIONS : SERVICE_PANEL_DEFAULT_VIEW_OPTIONS;
  const automationTrackedCount = rareOrderDiagnostics.length + normalOrderDiagnostics.length;
  const automationStatus = getNightBusinessAutomationSummary({
    configured: autoPrepPreferences.automationEnabled,
    allowed: automationRuntimeAllowed,
    blockReason: automationRuntimeBlockReason,
    trackedCount: automationTrackedCount,
  });
  const automationRuntimePauseLabel = getNightBusinessAutomationPauseLabel(automationRuntimeBlockReason);
  return (
    <div className="space-y-4">
      <Card>
        <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
          <InfoLine label="经营场景" value={detectedPlace ?? night?.placeLabel ?? '无经营场景'} />
          <InfoLine label="推荐数据" value={runtime ? '已就绪' : '暂不可用'} />
          <InfoLine label="自动化" value={automationStatus} />
          <InfoLine label="特殊经营" value={formatServiceSpecialBusinessSummary(specialBusiness)} />
          <InfoLine
            label="已摆放厨具"
            value={formatPlacedCookerSummary(runtime, runtimeSets, nightBusinessActive)}
          />
          <InfoLine label="目标厨具" value={uiPinningTarget?.cookerName || '暂无'} />
        </CardContent>
      </Card>

      <div className="flex flex-wrap items-center justify-between gap-3">
        <SegmentedControl
          value={activeServiceView}
          options={serviceViewOptions}
          onValueChange={(value) => onServiceViewChange(value as ServicePanelView)}
          className="w-full sm:w-auto"
        />
        {activeServiceView === 'recommendations' && autoPrepPreferences.automationEnabled && (
          <Badge variant="secondary">
            自动化{automationStatus}
          </Badge>
        )}
      </div>

      {activeServiceView === 'recommendations' && (
      <Tabs
        value={serviceRecommendationTab}
        onValueChange={(value) => {
          if (value === 'rare' || value === 'normal') onServiceRecommendationTabChange(value);
        }}
        className="space-y-4"
      >
        <TabsList className="grid h-9 w-full grid-cols-2">
          <TabsTrigger value="rare" className={MOD_TAB_TRIGGER_CLASS}>
            稀客
          </TabsTrigger>
          <TabsTrigger value="normal" className={MOD_TAB_TRIGGER_CLASS}>
            普客
          </TabsTrigger>
        </TabsList>

        <TabsContent value="rare" className="space-y-4">
          <CurrentOrderRecommendations
            recommendations={recommendations}
            recommendationIssues={recommendationIssues}
            pendingOrders={recommendationPendingOrders}
            pending={recommendationsPending}
            updateError={recommendationUpdateError}
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
          <ListPanel title={`普客订单 (${normalBusiness?.orders.length ?? 0})`} contentClassName="min-h-[18rem]">
            {!normalBusiness && <EmptyRow text="普客订单只在经营场景中读取" />}
            {normalBusiness?.error && <EmptyRow text={normalBusiness.error} />}
            {normalBusiness?.orders.length === 0 && !normalBusiness.error && (
              <EmptyRow text={normalBusiness.source || '暂无普客订单'} />
            )}
            {normalOrderDetailsPending && normalOrderDetailPlans.length === 0 && (normalBusiness?.orders.length ?? 0) > 0 && (
              <EmptyRow text="普客订单详情计算中" />
            )}
            {normalOrderDetailsPending && normalOrderDetailPlans.length > 0 && (
              <div className="border-b py-2 text-xs text-muted-foreground">
                普客订单详情正在更新，当前显示上一轮结果。
              </div>
            )}
            {normalOrderDetailsError && normalOrderDetailPlans.length === 0 && (normalBusiness?.orders.length ?? 0) > 0 && (
              <EmptyRow text={`普客订单详情计算失败：${normalOrderDetailsError}`} />
            )}
            {!normalOrderDetailsPending
              && !normalOrderDetailsError
              && normalOrderDetailPlans.length === 0
              && (normalBusiness?.orders.length ?? 0) > 0
              && <EmptyRow text="暂无普客订单详情" />}
            {normalOrderDetailPlans.map((plan) => (
              <NormalOrderDetailCard
                key={buildNormalOrderDetailPlanKey(plan)}
                plan={plan}
                ownedIngredientQty={runtimeSets?.ownedIngredientQty ?? {}}
                ownedBeverageQty={runtimeSets?.ownedBeverageQty ?? {}}
                ingredientIdByName={dataIndexes.ingredientIdByName}
                showDebugDetails={showDebugDetails}
              />
            ))}
          </ListPanel>
        </TabsContent>
      </Tabs>
      )}

      {activeServiceView === 'automation' && (
        <div className="space-y-4">
          {automationSafetyBarriers.length > 0 && (
            <AutomationSafetyBarrierPanel
              diagnostics={automationSafetyBarriers}
              busyKey={automationBarrierAckBusyKey}
              onAcknowledge={onAcknowledgeAutomationBarrier}
            />
          )}
          {autoPrepPreferences.automationEnabled ? (
            <>
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
                  <RareServiceAutomationPanel
                    preferences={autoPrepPreferences}
                    busy={autoPrepBusy}
                    message={autoPrepMessage}
                    paused={autoPrepPaused}
                    runtimePauseLabel={automationRuntimePauseLabel}
                    diagnostics={rareOrderDiagnostics}
                    automationBarrierAckBusyKey={automationBarrierAckBusyKey}
                    showDebugDetails={showDebugDetails}
                    onRetryOrder={onRetryRareAutomationOrder}
                    onResetOrder={onResetRareAutomationOrder}
                  />
                </TabsContent>

                <TabsContent value="normal" className="space-y-4">
                  <NormalServiceAutomationPanel
                    preferences={autoPrepPreferences}
                    busy={normalOrderBusy}
                    message={normalOrderMessage}
                    pausedCount={normalOrderPausedCount}
                    runtimePauseLabel={automationRuntimePauseLabel}
                    diagnostics={normalOrderDiagnostics}
                    automationBarrierAckBusyKey={automationBarrierAckBusyKey}
                    showDebugDetails={showDebugDetails}
                    onRetryOrder={onRetryNormalAutomationOrder}
                    onResetOrder={onResetNormalAutomationOrder}
                  />
                </TabsContent>
              </Tabs>
            </>
          ) : (
            <ListPanel title="自动化">
              <EmptyRow text="设置页开启“启用自动化（实验性）”后，这里会显示自动化执行状态。" />
            </ListPanel>
          )}
        </div>
      )}

      {activeServiceView === 'diagnostics' && showDebugDetails && (
        <div className="space-y-4">
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
                      桌 {formatDesk(guest.deskCode)} · {guest.source}
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
                            料理 {order.foodTag || '无'} ({order.foodTagId ?? 'missing'})
                          </Badge>
                          <Badge variant="outline">
                            酒水 {order.beverageTag || '无'} ({order.beverageTagId ?? 'missing'})
                          </Badge>
                          <OrderTraceBadge traceId={order.traceId} />
                          {order.specialBusinessRoleLabel && (
                            <Badge variant="secondary">{order.specialBusinessRoleLabel}</Badge>
                          )}
                          {order.automationAllowed === false && <Badge variant="outline">暂不可自动处理</Badge>}
                          {order.isFreeOrder && <Badge variant="secondary">免费订单</Badge>}
                          <Badge variant="secondary">{order.source}</Badge>
                        </div>
                        {order.automationAllowed === false && order.automationBlockReason && (
                          <div className="mt-1 text-xs text-muted-foreground">
                            {order.automationBlockReason}
                          </div>
                        )}
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
          {autoPrepPreferences.automationEnabled && (
            <AutomationResourceDiagnosticPanel overview={automationResources} />
          )}
          {specialBusiness?.active && (
            <SpecialBusinessNotice context={specialBusiness} showDebugDetails={showDebugDetails} />
          )}
          {specialBusiness?.active && (
            <SpecialBusinessOrderList
              night={night}
              normalBusiness={normalBusiness}
              showDebugDetails={showDebugDetails}
            />
          )}
          <ListPanel title="经营诊断">
            <div className={DENSE_TWO_COLUMN_GRID}>
              <InfoLine label="扫描状态" value={night?.source || '暂无'} />
              <InfoLine label="性能耗时" value={formatPerformanceMs(performanceMs)} mono />
              <InfoLine label="前端推荐耗时" value={formatPerformanceMs(orderRecommendationPerformanceMs)} mono />
              <InfoLine label="界面置顶" value={uiPinningStatus || '暂无'} />
              <InfoLine label="自动化门禁" value={automationRuntimeStatus || '暂无'} mono />
              <InfoLine label="普客来源" value={normalBusiness?.source || normalBusiness?.error || '暂无'} />
            </div>
          </ListPanel>
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
      <Button size="sm" data-gamepad-focus-key="service:focus:enter" onClick={onEnterFocusMode}>
        稀客订单专注模式
      </Button>
    </div>
  );
}

export function ServiceFocusPage({
  recommendations,
  recommendationIssues,
  recommendationPendingOrders,
  recommendationsPending,
  recommendationUpdateError,
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
  recommendationPendingOrders: NightBusinessOrder[];
  recommendationsPending: boolean;
  recommendationUpdateError: string | null;
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
  const hasOrders = recommendationsPending
    || recommendations.length > 0
    || recommendationIssues.length > 0
    || recommendationPendingOrders.length > 0;

  return (
    <div
      className="flex min-h-[calc(100dvh-1rem)] flex-col gap-4"
      role="region"
      aria-label="稀客订单专注模式"
      data-gamepad-scope="content"
      data-service-focus-page="true"
    >
      <div
        className="flex w-full shrink-0 flex-wrap items-center justify-end gap-3"
        data-service-focus-toolbar="true"
      >
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
        <Button size="sm" data-gamepad-focus-key="service-focus:exit" onClick={onExit}>退出专注模式</Button>
      </div>

      {hasOrders ? (
        <CurrentOrderRecommendations
          recommendations={recommendations}
          recommendationIssues={recommendationIssues}
          pendingOrders={recommendationPendingOrders}
          pending={recommendationsPending}
          updateError={recommendationUpdateError}
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
  pendingOrders,
  pending = false,
  updateError,
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
  pendingOrders: NightBusinessOrder[];
  pending?: boolean;
  updateError: string | null;
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
      ...pendingOrders.map((order) => ({ kind: 'pending' as const, order })),
    ], orderSortMode),
    [orderSortMode, pendingOrders, recommendationIssues, recommendations],
  );
  const panelAction = pending || updateError || action
    ? (
        <div className="flex flex-wrap items-center justify-end gap-2">
          {updateError && (
            <Badge variant="destructive" title={updateError}>
              {rows.length > 0 ? '更新失败，当前为上次结果' : '推荐更新失败'}
            </Badge>
          )}
          {pending && <Badge variant="outline">更新中</Badge>}
          {action}
        </div>
      )
    : undefined;

  return (
    <ListPanel
      title="当前点单推荐"
      action={panelAction}
      className={fillAvailableHeight ? 'min-h-0 flex-1' : undefined}
      gamepadScrollKey={fillAvailableHeight ? 'service-focus:recommendations' : 'service:recommendations'}
      gamepadScrollLabel={fillAvailableHeight ? '专注模式当前点单推荐' : '经营中当前点单推荐'}
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
      {rows.length === 0 && (
        <EmptyRow text={updateError ? '推荐更新失败' : pending ? '推荐计算中' : '暂无当前稀客点单推荐'} />
      )}
      <div className={compact ? 'space-y-2' : 'space-y-4'}>
        {rows.map((row) => {
          if (row.kind === 'pending') {
            const orderKey = buildNightBusinessOrderKey(row.order);
            return (
              <div
                key={`${orderKey}:pending`}
                className={compact ? 'steward-data-row p-2 text-xs' : 'steward-data-row p-3 text-sm'}
                data-recommendation-pending-order="true"
              >
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <div className="font-medium">{row.order.guestName || '稀客'} · 桌 {formatDesk(row.order.deskCode)}</div>
                  <Badge variant="outline">推荐计算中</Badge>
                </div>
                <div className="mt-1 flex flex-wrap gap-1.5">
                  <Badge variant="outline">料理 {row.order.foodTag || '无'}</Badge>
                  <Badge variant="outline">酒水 {row.order.beverageTag || '无'}</Badge>
                </div>
              </div>
            );
          }
          if (row.kind === 'issue') {
            const issue = row.issue;
            const issueOccurrenceKey = issue.order.traceId
              || `${issue.order.deskCode}:${issue.order.runtimeGuestId ?? 'unknown'}:${issue.order.foodTagId ?? 'missing'}:${issue.order.beverageTagId ?? 'missing'}`;
            return (
              <div
                key={`${issueOccurrenceKey}:issue`}
                className={compact ? 'steward-data-row p-2 text-xs' : 'steward-data-row p-3 text-sm'}
              >
                <div className="font-medium">{issue.order.guestName} · 桌 {formatDesk(issue.order.deskCode)}</div>
                <div className="mt-1 text-xs text-muted-foreground">{issue.message}</div>
              </div>
            );
          }

          const orderOccurrenceKey = row.item.order.traceId
            || `${row.item.order.deskCode}:${row.item.order.runtimeGuestId ?? 'unknown'}:${row.item.order.foodTagId ?? 'missing'}:${row.item.order.beverageTagId ?? 'missing'}`;
          return (
            <OrderRecommendationPanel
              key={orderOccurrenceKey}
              item={row.item}
              runtimeSets={runtimeSets}
              dataIndexes={dataIndexes}
              favorites={favorites}
              customRecipes={customRecipes}
              gamepadOccurrenceKey={`${fillAvailableHeight ? 'service-focus' : 'service'}:order:${orderOccurrenceKey}`}
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

function AutomationSafetyBarrierPanel({
  diagnostics,
  busyKey,
  onAcknowledge,
}: {
  diagnostics: AutomationSafetyBarrierDiagnostic[];
  busyKey: string;
  onAcknowledge: (sequence: number) => void;
}) {
  return (
    <ListPanel title={`待人工确认 (${diagnostics.length})`}>
      <div className="space-y-2">
        {diagnostics.map((diagnostic) => {
          const itemBusyKey = `barrier:${diagnostic.sequence}`;
          const isBusy = busyKey === itemBusyKey;
          const targetLabel = diagnostic.targetKind === 'normal' ? '普客' : diagnostic.targetKind === 'rare' ? '稀客' : diagnostic.targetKind;
          return (
            <div key={diagnostic.sequence} className="steward-data-row px-2.5 py-2 text-sm">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="font-medium text-foreground">{diagnostic.title}</div>
                  <div className="mt-1 flex flex-wrap gap-1.5 text-xs">
                    <Badge variant="destructive">{targetLabel || '未知目标'}</Badge>
                    <Badge variant="outline">事件 #{diagnostic.sequence}</Badge>
                    <Badge variant="outline">{diagnostic.code || '未知原因'}</Badge>
                  </div>
                </div>
                <Button
                  size="sm"
                  variant="outline"
                  disabled={Boolean(busyKey)}
                  onClick={() => onAcknowledge(diagnostic.sequence)}
                  data-gamepad-focus-key={`automation-barrier:${diagnostic.sequence}:ack`}
                >
                  {isBusy ? '确认中' : '确认已处理'}
                </Button>
              </div>
              <div className="mt-2 whitespace-pre-line text-xs text-muted-foreground">
                {diagnostic.message}
              </div>
              {diagnostic.error && (
                <div className="mt-1 text-xs text-destructive">确认失败：{diagnostic.error}</div>
              )}
            </div>
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
  runtimePauseLabel,
  diagnostics,
  automationBarrierAckBusyKey,
  showDebugDetails,
  onRetryOrder,
  onResetOrder,
}: {
  preferences: CompanionPreferences;
  busy: boolean;
  message: string;
  paused: boolean;
  runtimePauseLabel: string;
  diagnostics: RareAutoOrderDiagnostic[];
  automationBarrierAckBusyKey: string;
  showDebugDetails: boolean;
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  return (
    <ListPanel title="稀客自动化状态">
      <RareAutoPrepStatus
        busy={busy}
        paused={paused}
        runtimePauseLabel={runtimePauseLabel}
        message={message}
        preferences={preferences}
        diagnostics={diagnostics}
        automationBarrierAckBusyKey={automationBarrierAckBusyKey}
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
  runtimePauseLabel,
  diagnostics,
  automationBarrierAckBusyKey,
  showDebugDetails,
  onRetryOrder,
  onResetOrder,
}: {
  preferences: CompanionPreferences;
  busy: boolean;
  message: string;
  pausedCount: number;
  runtimePauseLabel: string;
  diagnostics: NormalAutoOrderDiagnostic[];
  automationBarrierAckBusyKey: string;
  showDebugDetails: boolean;
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  return (
    <ListPanel title="普客自动化状态">
      <NormalAutoPrepStatus
        busy={busy}
        pausedCount={pausedCount}
        runtimePauseLabel={runtimePauseLabel}
        message={message}
        preferences={preferences}
        diagnostics={diagnostics}
        automationBarrierAckBusyKey={automationBarrierAckBusyKey}
        showDebugDetails={showDebugDetails}
        onRetryOrder={onRetryOrder}
        onResetOrder={onResetOrder}
      />
    </ListPanel>
  );
}

function RareAutoPrepStatus({
  busy,
  paused,
  runtimePauseLabel,
  message,
  preferences,
  diagnostics,
  automationBarrierAckBusyKey,
  showDebugDetails,
  onRetryOrder,
  onResetOrder,
}: {
  busy: boolean;
  paused: boolean;
  runtimePauseLabel: string;
  message: string;
  preferences: CompanionPreferences;
  diagnostics: RareAutoOrderDiagnostic[];
  automationBarrierAckBusyKey: string;
  showDebugDetails: boolean;
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  return (
    <div className="steward-inline-panel px-3 py-2 text-sm">
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
                    disabled={busy || !diagnostic.paused || diagnostic.manualResolutionRequired}
                    data-gamepad-focus-key={`rare-auto:${diagnostic.orderKey}:retry`}
                  >
                    重试
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => onResetOrder(diagnostic.orderKey)}
                    disabled={busy || (diagnostic.manualResolutionRequired && Boolean(automationBarrierAckBusyKey))}
                    data-gamepad-focus-key={`rare-auto:${diagnostic.orderKey}:reset`}
                  >
                    {diagnostic.manualResolutionRequired && automationBarrierAckBusyKey === `rare:${diagnostic.orderKey}`
                      ? '确认中'
                      : diagnostic.manualResolutionRequired ? '确认已处理' : '重置'}
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
                <OrderTraceBadge traceId={diagnostic.traceId} />
                <Badge variant={diagnostic.paused ? 'destructive' : 'secondary'}>
                  {diagnostic.paused ? '订单暂停' : '订单可执行'}
                </Badge>
                {diagnostic.manualResolutionRequired && (
                  <Badge variant="destructive">需人工确认</Badge>
                )}
                <Badge variant={diagnostic.prepared ? 'secondary' : 'outline'}>
                  料理{diagnostic.prepared ? '已开锅' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.beverageDeliveryRequested ? 'secondary' : 'outline'}>
                  酒水处理{diagnostic.hasServedBeverage ? '已确认' : diagnostic.beverageDeliveryRequested ? '待确认' : '待处理'}
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
              <AutomationDetailAccordion
                id={`rare:${diagnostic.orderKey}`}
                detailMessage={diagnostic.detailMessage}
                detailUpdatedAtMs={diagnostic.detailUpdatedAtMs}
              />
            </div>
          ))}
        </div>
      )}
      <div className="mt-2 whitespace-pre-line text-muted-foreground">
        {message || '等待稀客订单或自动化条件。'}
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
        {runtimePauseLabel && <Badge variant="destructive">{runtimePauseLabel}</Badge>}
        <Badge variant={paused ? 'destructive' : 'secondary'}>{paused ? '订单存在暂停' : '订单无暂停'}</Badge>
        <Badge variant="outline">每轮最多 {preferences.autoRareConcurrency}</Badge>
        <Badge variant={preferences.autoRareOrderEnabled ? 'secondary' : 'outline'}>启用 {preferences.autoRareOrderEnabled ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepTakeBeverage ? 'secondary' : 'outline'}>送酒 {preferences.autoPrepTakeBeverage ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepStartCooking ? 'secondary' : 'outline'}>料理 {preferences.autoPrepStartCooking ? '开' : '关'}</Badge>
        {preferences.autoPrepStartCooking && <Badge variant="secondary">QTE 自动完成</Badge>}
        <Badge variant={preferences.autoPrepCollectCooking ? 'secondary' : 'outline'}>直送 {preferences.autoPrepCollectCooking ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepCompleteOrder ? 'secondary' : 'outline'}>完成 {preferences.autoPrepCompleteOrder ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepRecipeFavoritesOnly ? 'secondary' : 'outline'}>收藏料理 {preferences.autoPrepRecipeFavoritesOnly ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepBeverageFavoritesOnly ? 'secondary' : 'outline'}>收藏酒水 {preferences.autoPrepBeverageFavoritesOnly ? '开' : '关'}</Badge>
      </div>
    </div>
  );
}

function NormalAutoPrepStatus({
  busy,
  pausedCount,
  runtimePauseLabel,
  message,
  preferences,
  diagnostics,
  automationBarrierAckBusyKey,
  showDebugDetails,
  onRetryOrder,
  onResetOrder,
}: {
  busy: boolean;
  pausedCount: number;
  runtimePauseLabel: string;
  message: string;
  preferences: CompanionPreferences;
  diagnostics: NormalAutoOrderDiagnostic[];
  automationBarrierAckBusyKey: string;
  showDebugDetails: boolean;
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  return (
    <div className="steward-inline-panel px-3 py-2 text-sm">
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
                <div className="flex shrink-0 items-center gap-1.5" data-gamepad-axis="x">
                  <Badge variant={diagnostic.paused ? 'destructive' : 'secondary'}>
                    {diagnostic.paused ? '订单暂停' : '订单可执行'}
                  </Badge>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => onRetryOrder(diagnostic.orderKey)}
                    disabled={busy || !diagnostic.paused || diagnostic.manualResolutionRequired}
                    data-gamepad-focus-key={`normal-auto:${diagnostic.orderKey}:retry`}
                  >
                    重试
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => onResetOrder(diagnostic.orderKey)}
                    disabled={busy || (diagnostic.manualResolutionRequired && Boolean(automationBarrierAckBusyKey))}
                    data-gamepad-focus-key={`normal-auto:${diagnostic.orderKey}:reset`}
                  >
                    {diagnostic.manualResolutionRequired && automationBarrierAckBusyKey === `normal:${diagnostic.orderKey}`
                      ? '确认中'
                      : diagnostic.manualResolutionRequired ? '确认已处理' : '重置'}
                  </Button>
                </div>
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
                <OrderTraceBadge traceId={diagnostic.traceId} />
                {diagnostic.manualResolutionRequired && (
                  <Badge variant="destructive">需人工确认</Badge>
                )}
                <Badge variant={diagnostic.beverageDeliveryRequested ? 'secondary' : 'outline'}>
                  酒水处理{diagnostic.hasServedBeverage ? '已确认' : diagnostic.beverageDeliveryRequested ? '待确认' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.prepared ? 'secondary' : 'outline'}>
                  料理{diagnostic.prepared ? '已开锅' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.foodDeliveryRequested ? 'secondary' : 'outline'}>
                  料理送达{diagnostic.hasServedFood ? '已确认' : diagnostic.foodDeliveryRequested ? '待确认' : '未请求'}
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
              <AutomationDetailAccordion
                id={`normal:${diagnostic.orderKey}`}
                detailMessage={diagnostic.detailMessage}
                detailUpdatedAtMs={diagnostic.detailUpdatedAtMs}
              />
            </div>
          ))}
        </div>
      )}
      <div className="mt-1 whitespace-pre-line text-muted-foreground">
        {message || '等待普客订单或自动化条件。'}
      </div>
      <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
        {runtimePauseLabel && <Badge variant="destructive">{runtimePauseLabel}</Badge>}
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
