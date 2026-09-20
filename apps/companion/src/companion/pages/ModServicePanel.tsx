import type { FavoriteAvailability } from '@/companion/domain/favorite-availability';
import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { IconX } from '@tabler/icons-react';
import {
  Accordion,
  AccordionContent,
  AccordionItem,
  AccordionTrigger,
  Badge,
  Button,
  EmptyRow,
  InfoLine,
  ListPanel,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@/components/ui-kit';
import {
  buildAutomationResourceOverview,
  buildNightBusinessOrderKey,
  type OperationalOrderRecommendation,
} from '@/companion/domain/automation';
import { getNightBusinessAutomationSummary } from '@/companion/domain/automation-runtime';
import type { NormalOrderDetailPlan } from '@/companion/domain/normal-order-details';
import { buildParticipatingRareOrderPresentationRows } from '@/companion/domain/order-recommendation-presentation';
import type {
  RareOrderExactIdentity,
  RareOrderParticipationResolution,
  RareOrderParticipationSnapshotView,
} from '@/companion/domain/rare-order-participation';
import { sortNightOrderRows, sortNightOrders } from '@/companion/domain/sorting';
import { formatDesk, formatGuestFund, formatPerformanceMs } from '@/companion/formatters';
import type { CompanionPreferences, ServiceOrderSortMode } from '@/companion/preferences';
import type {
  AutomationSafetyBarrierDiagnostic,
  CustomRecipeData,
  FavoriteData,
  GameUiTargetSlots,
  NightBusinessContext,
  NightBusinessOrder,
  NormalAutoOrderDiagnostic,
  NormalBusinessContext,
  OrderRecommendation,
  RareAutoOrderDiagnostic,
  RareGuestParticipationMutationAction,
  RecommendationIssue,
  RecommendationStateSnapshot,
  RuntimeSets,
  SpecialBusinessContext,
  ToggleBeverageFavorite,
  ToggleRecipeFavorite,
} from '@/companion/types';
import type { NormalExecutionTargetSelection } from '@/companion/workers/order-recommendations.types';
import {
  DENSE_MINIMUM_THREE_COLUMN_GRID,
  DENSE_TWO_COLUMN_GRID,
  MAX_RECOMMENDATION_ROWS,
  MOD_TAB_TRIGGER_CLASS,
} from '@/companion/pages/shared-constants';
import { FocusLimitInput, SwitchControl } from '@/companion/pages/shared';
import {
  AutomationResourceDiagnosticPanel,
  OrderTraceBadge,
  SpecialBusinessNotice,
  SpecialBusinessOrderList,
} from '@/companion/pages/service/ServiceContextPanels';
import { NormalOrderDetailCard } from '@/companion/pages/service/NormalOrderDetailCard';
import { RareOrderRecommendationCard } from '@/companion/pages/service/RareOrderRecommendationCard';
import { RareOrderParticipationPanel } from '@/companion/pages/service/RareOrderParticipationPanel';
import {
  buildRareOrderRecommendationCollectionState,
  type ServiceOrderCollectionState,
} from '@/companion/pages/service/service-order-collection-state';
import {
  ServiceOrderCardFrame,
  ServiceOrderCollectionPanel,
} from '@/companion/pages/service/ServiceOrderPresentation';
import { buildRecommendationDataIndexes, type RecommendationDataSet } from '@/lib/recommendation-data';
import type { PlaceName } from '@/lib/catalog-types';

export type ServicePanelView = 'recommendations' | 'participation' | 'game-ui' | 'diagnostics';

export type ServiceRecommendationTab = 'rare' | 'normal';

type RareOrderPresentationRow = (
  | { kind: 'issue'; order: NightBusinessOrder; issue: RecommendationIssue }
  | { kind: 'recommendation'; order: NightBusinessOrder; item: OrderRecommendation }
  | { kind: 'pending'; order: NightBusinessOrder }
) & { participation: RareOrderParticipationResolution | null };

function formatGameUiTargetCookers(targets: GameUiTargetSlots): string {
  const values = [
    targets.rare?.features.cookerHighlightEnabled && targets.rare.cookerName
      ? `稀客：${targets.rare.cookerName}`
      : '',
    targets.normal?.features.cookerHighlightEnabled && targets.normal.cookerName
      ? `普客：${targets.normal.cookerName}`
      : '',
  ].filter(Boolean);
  return values.length > 0 ? values.join('；') : '暂无';
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
    const locked =
      runtime.placedCookerLockedControllerCount > 0
        ? `${runtime.placedCookerLockedControllerCount} 个厨具被事件锁定`
        : '';
    if (runtime.placedCookers.length === 0) return `已读取 · ${locked || '未摆放'}`;
    return ['已读取', names || `${runtime.placedCookers.length} 个厨具类型未识别`, locked]
      .filter(Boolean)
      .join(' · ');
  }

  return `读取不可用${runtime.placedCookerStatus ? ` · ${runtime.placedCookerStatus}` : ''}`;
}

function ServiceSummaryAccordion({
  runtime,

  nightBusinessActive,

  night,

  specialBusiness,

  detectedPlace,

  runtimeSets,

  uiTargetSlots,

  automationStatus,
}: {
  runtime: RecommendationStateSnapshot | null;
  nightBusinessActive: boolean;
  night: NightBusinessContext | null;
  specialBusiness: SpecialBusinessContext | null;
  detectedPlace: PlaceName | null;
  runtimeSets: RuntimeSets | null;
  uiTargetSlots: GameUiTargetSlots;
  automationStatus: string;
}) {
  const scene = detectedPlace ?? night?.placeLabel ?? '无经营场景';
  const recommendationStatus = runtime ? '已就绪' : '暂不可用';
  const collapsedSummary = `${scene} · 推荐：${recommendationStatus} · 自动化：${automationStatus}`;

  return (
    <Accordion data-service-summary-accordion="true">
      <AccordionItem value="service-summary">
        <AccordionTrigger
          density="compact"
          data-service-summary-trigger="true"
          data-gamepad-clickable="true"
          data-gamepad-focus-key="service:summary:toggle"
        >
          <span className="flex min-w-0 items-center gap-2 text-left">
            <span className="shrink-0 font-medium">经营概况</span>
            <span
              className="min-w-0 truncate text-xs font-normal text-muted-foreground"
              title={collapsedSummary}
            >
              {collapsedSummary}
            </span>
          </span>
        </AccordionTrigger>
        <AccordionContent data-service-summary-content="true">
          <div className={`${DENSE_MINIMUM_THREE_COLUMN_GRID} text-sm`} data-service-summary-grid="true">
            <InfoLine label="经营场景" value={scene} />
            <InfoLine label="推荐数据" value={recommendationStatus} />
            <InfoLine label="自动化" value={automationStatus} />
            <InfoLine label="特殊经营" value={formatServiceSpecialBusinessSummary(specialBusiness)} />
            <InfoLine
              label="已摆放厨具"
              value={formatPlacedCookerSummary(runtime, runtimeSets, nightBusinessActive)}
            />
            <InfoLine label="目标厨具" value={formatGameUiTargetCookers(uiTargetSlots)} />
          </div>
        </AccordionContent>
      </AccordionItem>
    </Accordion>
  );
}

function buildNormalOrderDetailPlanKey(plan: NormalOrderDetailPlan): string {
  const { order } = plan;
  return (
    order.orderKey ??
    order.traceId ??
    `${order.deskCode}-${order.guestName}-${order.foodId}-${order.beverageId}-${order.source}`
  );
}

function buildNormalOrderCollectionState({
  normalBusiness,
  detailPlanCount,
  detailsPending,
  detailsError,
}: {
  normalBusiness: NormalBusinessContext | null;
  detailPlanCount: number;
  detailsPending: boolean;
  detailsError: string | null;
}): ServiceOrderCollectionState {
  if (!normalBusiness) {
    return { kind: 'empty', message: '普客订单只在经营场景中读取' };
  }
  if (normalBusiness.error) {
    return {
      kind: 'error',
      message: normalBusiness.error,
      detail: normalBusiness.error,
      emptyLabel: '订单读取失败',
      updating: detailsPending,
    };
  }
  if (normalBusiness.orders.length === 0) {
    return { kind: 'empty', message: normalBusiness.source || '暂无普客订单' };
  }
  if (detailsError) {
    return {
      kind: 'error',
      message: `普客订单详情计算失败：${detailsError}`,
      detail: detailsError,
      emptyLabel: '方案计算失败',
      updating: detailsPending,
    };
  }
  if (detailsPending) {
    return {
      kind: 'updating',
      message: '普客订单详情计算中',
      label: detailPlanCount > 0 ? '更新中，当前为上次结果' : '更新中',
    };
  }
  if (detailPlanCount === 0) {
    return { kind: 'empty', message: '暂无普客订单详情' };
  }
  return { kind: 'ready' };
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

  uiTargetSlots,

  favorites,

  favoriteBusyKey,

  favoriteAvailability,

  favoriteError,

  customRecipes,

  rareOrderDiagnostics,

  autoPrepPreferences,

  recipeLimit,

  beverageLimit,

  normalOrderDiagnostics,

  automationRuntimeAllowed,

  automationRuntimeBlockReason,

  automationRuntimeStatus,

  automationSafetyBarriers,

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

  operationalRecommendations,

  rareParticipationModuleEnabled,

  managedRareGuestIds,

  rareGuestParticipationSnapshot,

  rareParticipationBusinessGeneration,

  rareParticipationCollectionComplete,

  rareParticipationEnabled,

  rareParticipationReady,

  rareParticipationReadOnly,

  rareParticipationReadOnlyReason,

  rareParticipationBusyMutationKey,

  rareParticipationError,

  resolveRareOrderParticipation,

  onRecipeLimitChange,

  onBeverageLimitChange,

  onToggleRecipeFavorite,

  onToggleBeverageFavorite,

  onMutateRareGuestOrders,

  onMutateRareOrder,

  onEnterFocusMode,

  onServiceViewChange,

  participationSettings,

  gameUiSettings,

  displaySettings,

  onOpenAutomation,

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
  uiTargetSlots: GameUiTargetSlots;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteAvailability: FavoriteAvailability;
  favoriteError: string;
  customRecipes: CustomRecipeData;
  rareOrderDiagnostics: RareAutoOrderDiagnostic[];
  autoPrepPreferences: CompanionPreferences;
  recipeLimit: number;
  beverageLimit: number;
  normalOrderDiagnostics: NormalAutoOrderDiagnostic[];
  automationRuntimeAllowed: boolean;
  automationRuntimeBlockReason: string;
  automationRuntimeStatus: string;
  automationSafetyBarriers: AutomationSafetyBarrierDiagnostic[];
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
  operationalRecommendations: readonly OperationalOrderRecommendation[] | null;
  rareParticipationModuleEnabled: boolean;
  managedRareGuestIds: readonly number[];
  rareGuestParticipationSnapshot: RareOrderParticipationSnapshotView | null;
  rareParticipationBusinessGeneration: number;
  rareParticipationCollectionComplete: boolean;
  rareParticipationEnabled: boolean;
  rareParticipationReady: boolean;
  rareParticipationReadOnly: boolean;
  rareParticipationReadOnlyReason: string;
  rareParticipationBusyMutationKey: string | null;
  rareParticipationError: string;
  resolveRareOrderParticipation: (order: NightBusinessOrder) => RareOrderParticipationResolution | null;
  onRecipeLimitChange: (value: number) => void;
  onBeverageLimitChange: (value: number) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onMutateRareGuestOrders: (
    guestId: number,
    targets: readonly RareOrderExactIdentity[],
    action: RareGuestParticipationMutationAction,
  ) => void;
  onMutateRareOrder: (order: RareOrderExactIdentity, action: RareGuestParticipationMutationAction) => void;
  onEnterFocusMode: () => void;
  onServiceViewChange: (value: ServicePanelView) => void;
  participationSettings: (queue: ReactNode) => ReactNode;
  gameUiSettings: ReactNode;
  displaySettings: ReactNode;
  onOpenAutomation: () => void;
  onServiceRecommendationTabChange: (value: ServiceRecommendationTab) => void;
  showDebugDetails: boolean;
}) {
  const dataIndexes = useMemo(() => buildRecommendationDataIndexes(data), [data]);
  const activeGuests = night?.activeRareGuests ?? [];
  const orders = useMemo(
    () => sortNightOrders(night?.orders ?? [], autoPrepPreferences.serviceOrderSortMode, specialBusiness),
    [autoPrepPreferences.serviceOrderSortMode, night?.orders, specialBusiness],
  );
  const activeServiceView =
    showDebugDetails || serviceView !== 'diagnostics' ? serviceView : 'recommendations';
  const automationResources = useMemo(() => {
    if (activeServiceView !== 'diagnostics' || !showDebugDetails) {
      return { cookers: [], normalBlocked: [] };
    }

    return buildAutomationResourceOverview({
      runtime,
      recommendations,
      operationalRecommendations,
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
  }, [
    activeServiceView,
    autoPrepPreferences,
    favorites,
    normalExecutionTargets,
    normalExecutionTargetsEnabled,
    normalExecutionTargetsError,
    normalExecutionTargetsPending,
    normalBusiness?.orders,
    normalOrderDiagnostics,
    operationalRecommendations,
    rareOrderDiagnostics,
    recommendations,
    runtime,
    showDebugDetails,
    specialBusiness,
    data,
  ]);
  const automationTrackedCount = rareOrderDiagnostics.length + normalOrderDiagnostics.length;
  const automationStatus = getNightBusinessAutomationSummary({
    configured: autoPrepPreferences.automationEnabled,
    allowed: automationRuntimeAllowed,
    blockReason: automationRuntimeBlockReason,
    trackedCount: automationTrackedCount,
  });
  const normalOrderCollectionState = buildNormalOrderCollectionState({
    normalBusiness,
    detailPlanCount: normalOrderDetailPlans.length,
    detailsPending: normalOrderDetailsPending,
    detailsError: normalOrderDetailsError,
  });
  return (
    <Tabs
      value={activeServiceView === 'recommendations' ? serviceRecommendationTab : activeServiceView}
      onValueChange={(value) => {
        if (value === 'rare' || value === 'normal') {
          onServiceViewChange('recommendations');
          onServiceRecommendationTabChange(value);
        } else onServiceViewChange(value as ServicePanelView);
      }}
      className="space-y-4"
    >
      <ServiceSummaryAccordion
        runtime={runtime}
        nightBusinessActive={nightBusinessActive}
        night={night}
        specialBusiness={specialBusiness}
        detectedPlace={detectedPlace}
        runtimeSets={runtimeSets}
        uiTargetSlots={uiTargetSlots}
        automationStatus={automationStatus}
      />
      <div className="flex flex-wrap items-center justify-between gap-2 text-sm" role="status">
        <span>
          自动化：{automationStatus} · 待人工确认 {automationSafetyBarriers.length}
        </span>
        <Button variant="outline" size="sm" onClick={onOpenAutomation}>
          查看自动化
        </Button>
      </div>
      <TabsList scrollable className="flex h-auto w-full" data-service-tabs="true">
        <TabsTrigger value="rare" className={MOD_TAB_TRIGGER_CLASS} data-service-order-tab-trigger="rare">
          稀客
        </TabsTrigger>
        <TabsTrigger value="normal" className={MOD_TAB_TRIGGER_CLASS} data-service-order-tab-trigger="normal">
          普客
        </TabsTrigger>
        <TabsTrigger value="participation" className={MOD_TAB_TRIGGER_CLASS}>
          稀客调度
        </TabsTrigger>
        <TabsTrigger value="game-ui" className={MOD_TAB_TRIGGER_CLASS}>
          游戏辅助
        </TabsTrigger>
        {showDebugDetails && (
          <TabsTrigger value="diagnostics" className={MOD_TAB_TRIGGER_CLASS}>
            诊断
          </TabsTrigger>
        )}
      </TabsList>
      {activeServiceView === 'recommendations' && (
        <Accordion>
          <AccordionItem value="display">
            <AccordionTrigger>订单显示</AccordionTrigger>
            <AccordionContent>{displaySettings}</AccordionContent>
          </AccordionItem>
        </Accordion>
      )}
      <TabsContent value="rare" data-service-order-tab="rare">
        {activeServiceView === 'recommendations' && serviceRecommendationTab === 'rare' && (
          <>
            <RareOrderRecommendationList
              recommendations={recommendations}
              recommendationIssues={recommendationIssues}
              pendingOrders={recommendationPendingOrders}
              pending={recommendationsPending}
              updateError={recommendationUpdateError}
              runtimeSets={runtimeSets}
              dataIndexes={dataIndexes}
              orderSortMode={autoPrepPreferences.serviceOrderSortMode}
              specialBusiness={specialBusiness}
              showDebugDetails={showDebugDetails}
              favorites={favorites}
              customRecipes={customRecipes}
              favoriteBusyKey={favoriteBusyKey}
              favoriteAvailability={favoriteAvailability}
              favoriteError={favoriteError}
              participationEnabled={rareParticipationEnabled}
              participationReady={rareParticipationReady}
              resolveRareOrderParticipation={resolveRareOrderParticipation}
              toolbar={
                <ServiceRecommendationHeaderActions
                  recipeLimit={recipeLimit}
                  beverageLimit={beverageLimit}
                  onRecipeLimitChange={onRecipeLimitChange}
                  onBeverageLimitChange={onBeverageLimitChange}
                  onEnterFocusMode={onEnterFocusMode}
                />
              }
              recipeLimit={recipeLimit}
              beverageLimit={beverageLimit}
              onToggleRecipeFavorite={onToggleRecipeFavorite}
              onToggleBeverageFavorite={onToggleBeverageFavorite}
            />
          </>
        )}
      </TabsContent>
      <TabsContent value="normal" data-service-order-tab="normal">
        {activeServiceView === 'recommendations' && serviceRecommendationTab === 'normal' && (
          <>
            <ServiceOrderCollectionPanel
              mode="normal"
              count={normalBusiness?.orders.length ?? 0}
              state={normalOrderCollectionState}
              hasRows={normalOrderDetailPlans.length > 0}
            >
              <div className="space-y-4">
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
              </div>
            </ServiceOrderCollectionPanel>
          </>
        )}
      </TabsContent>
      <TabsContent value="participation">
        {activeServiceView === 'participation' &&
          participationSettings(
            rareParticipationModuleEnabled ? (
              <RareOrderParticipationPanel
                orders={night?.orders ?? []}
                managedGuestIds={managedRareGuestIds}
                snapshot={rareGuestParticipationSnapshot}
                businessGeneration={rareParticipationBusinessGeneration}
                collectionComplete={rareParticipationCollectionComplete}
                businessActive={nightBusinessActive}
                readOnly={rareParticipationReadOnly}
                readOnlyReason={rareParticipationReadOnlyReason}
                showDebugDetails={showDebugDetails}
                busyMutationKey={rareParticipationBusyMutationKey}
                error={rareParticipationError}
                onMutateGuest={onMutateRareGuestOrders}
                onMutateOrder={onMutateRareOrder}
              />
            ) : null,
          )}
      </TabsContent>
      <TabsContent value="game-ui">{activeServiceView === 'game-ui' && gameUiSettings}</TabsContent>
      {showDebugDetails && (
        <TabsContent value="diagnostics">
          {' '}
          {activeServiceView === 'diagnostics' && showDebugDetails && (
            <div className="space-y-4">
              <div className={DENSE_TWO_COLUMN_GRID}>
                <ListPanel title="当前稀客" contentClassName="min-h-[9rem]">
                  {activeGuests.length === 0 && <EmptyRow text="暂无稀客" />}
                  {activeGuests.map((guest) => {
                    const fund = formatGuestFund(guest);
                    return (
                      <div
                        key={`${guest.deskCode}-${guest.guestId}-${guest.source}`}
                        className="flex items-center justify-between border-b py-2 text-sm last:border-b-0"
                      >
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
                  {orders.map((order) => {
                    const orderKey = buildNightBusinessOrderKey(order);
                    return (
                      <div key={orderKey} className="border-b py-2 text-sm last:border-b-0">
                        <div className="min-w-0">
                          <div className="flex items-center justify-between gap-3">
                            <span className="truncate font-medium" title={order.guestName}>
                              {order.guestName}
                            </span>
                            <span className="shrink-0 text-muted-foreground">
                              桌 {formatDesk(order.deskCode)}
                            </span>
                          </div>
                          <div className="mt-1 flex flex-wrap gap-1.5">
                            <Badge variant="outline">
                              料理 {order.foodTag || '无'} ({order.foodTagId ?? '未读取'})
                            </Badge>
                            <Badge variant="outline">
                              酒水 {order.beverageTag || '无'} ({order.beverageTagId ?? '未读取'})
                            </Badge>
                            <OrderTraceBadge traceId={order.traceId} showDebugDetails={showDebugDetails} />
                            {order.specialBusinessRoleLabel && (
                              <Badge variant="secondary">{order.specialBusinessRoleLabel}</Badge>
                            )}
                            {order.automationAllowed === false && (
                              <Badge variant="outline">暂不可自动处理</Badge>
                            )}
                            {order.isFreeOrder && <Badge variant="secondary">免费订单</Badge>}
                            <Badge variant="secondary">{order.source}</Badge>
                          </div>
                          {order.automationAllowed === false && order.automationBlockReason && (
                            <div className="mt-1 text-xs text-muted-foreground">
                              {order.automationBlockReason}
                            </div>
                          )}
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
                  <InfoLine
                    label="前端推荐耗时"
                    value={formatPerformanceMs(orderRecommendationPerformanceMs)}
                    mono
                  />
                  <InfoLine label="界面置顶" value={uiPinningStatus || '暂无'} />
                  <InfoLine label="自动化可用状态" value={automationRuntimeStatus || '暂无'} mono />
                  <InfoLine
                    label="普客来源"
                    value={normalBusiness?.source || normalBusiness?.error || '暂无'}
                  />
                </div>
              </ListPanel>
            </div>
          )}
        </TabsContent>
      )}
    </Tabs>
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
    <div
      className="flex min-w-0 flex-nowrap items-center justify-end gap-2"
      role="group"
      aria-label="稀客推荐显示控制"
      data-service-recommendation-toolbar="true"
    >
      <div className="min-w-0" data-service-recommendation-limit="recipe">
        <FocusLimitInput label="料理" value={recipeLimit} onChange={onRecipeLimitChange} density="compact" />
      </div>
      <div className="min-w-0" data-service-recommendation-limit="beverage">
        <FocusLimitInput
          label="酒水"
          value={beverageLimit}
          onChange={onBeverageLimitChange}
          density="compact"
        />
      </div>
      <Button size="sm" data-gamepad-focus-key="service:focus:enter" onClick={onEnterFocusMode}>
        专注模式
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

  specialBusiness,

  showDebugDetails,

  favorites,

  customRecipes,

  favoriteBusyKey,

  favoriteAvailability,

  favoriteError,

  participationEnabled,

  participationReady,

  resolveRareOrderParticipation,

  compact,

  recipeLimit,

  beverageLimit,

  onCompactChange,

  onRecipeLimitChange,

  onBeverageLimitChange,

  onToggleRecipeFavorite,

  onToggleBeverageFavorite,

  onExit,

  safetyNotice,
}: {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  recommendationPendingOrders: NightBusinessOrder[];
  recommendationsPending: boolean;
  recommendationUpdateError: string | null;
  runtimeSets: RuntimeSets | null;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
  orderSortMode: ServiceOrderSortMode;
  specialBusiness: SpecialBusinessContext | null;
  showDebugDetails: boolean;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  favoriteBusyKey: string;
  favoriteAvailability: FavoriteAvailability;
  favoriteError: string;
  participationEnabled: boolean;
  participationReady: boolean;
  resolveRareOrderParticipation: (order: NightBusinessOrder) => RareOrderParticipationResolution | null;
  compact: boolean;
  recipeLimit: number;
  beverageLimit: number;
  onCompactChange: (value: boolean) => void;
  onRecipeLimitChange: (value: number) => void;
  onBeverageLimitChange: (value: number) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onExit: () => void;
  safetyNotice?: ReactNode;
}) {
  return (
    <div
      className="flex min-h-[calc(100dvh-1rem)] flex-col gap-4"
      role="region"
      aria-label="稀客订单专注模式"
      data-gamepad-scope="content"
      data-service-focus-page="true"
    >
      <div className="w-full shrink-0 space-y-2" data-service-focus-toolbar="true">
        {safetyNotice && <div className="flex justify-end">{safetyNotice}</div>}
        <div
          className="flex min-w-0 flex-nowrap items-center justify-end gap-2"
          role="group"
          aria-label="专注模式显示控制"
          data-service-focus-controls="true"
        >
          <SwitchControl
            label="精简模式"
            checked={compact}
            onCheckedChange={onCompactChange}
            density="compact"
          />
          <FocusLimitInput
            label="料理"
            value={recipeLimit}
            onChange={onRecipeLimitChange}
            density="compact"
          />
          <FocusLimitInput
            label="酒水"
            value={beverageLimit}
            onChange={onBeverageLimitChange}
            density="compact"
          />
          <Button
            type="button"
            size="icon-sm"
            aria-label="退出专注模式"
            density="compact"
            title="退出专注模式"
            data-gamepad-focus-key="service-focus:exit"
            onClick={onExit}
          >
            <IconX size={14} aria-hidden="true" />
          </Button>
        </div>
      </div>

      <RareOrderRecommendationList
        recommendations={recommendations}
        recommendationIssues={recommendationIssues}
        pendingOrders={recommendationPendingOrders}
        pending={recommendationsPending}
        updateError={recommendationUpdateError}
        runtimeSets={runtimeSets}
        dataIndexes={dataIndexes}
        orderSortMode={orderSortMode}
        specialBusiness={specialBusiness}
        showDebugDetails={showDebugDetails}
        favorites={favorites}
        customRecipes={customRecipes}
        favoriteBusyKey={favoriteBusyKey}
        favoriteAvailability={favoriteAvailability}
        favoriteError={favoriteError}
        participationEnabled={participationEnabled}
        participationReady={participationReady}
        resolveRareOrderParticipation={resolveRareOrderParticipation}
        compact={compact}
        fillAvailableHeight
        recipeLimit={recipeLimit}
        beverageLimit={beverageLimit}
        onToggleRecipeFavorite={onToggleRecipeFavorite}
        onToggleBeverageFavorite={onToggleBeverageFavorite}
      />
    </div>
  );
}

function RareOrderRecommendationList({
  recommendations,

  recommendationIssues,

  pendingOrders,

  pending = false,

  updateError,

  runtimeSets,

  dataIndexes,

  orderSortMode,

  specialBusiness,

  showDebugDetails = false,

  favorites,

  customRecipes,

  favoriteBusyKey,

  favoriteAvailability,

  favoriteError,

  participationEnabled = false,

  participationReady = true,

  resolveRareOrderParticipation,

  toolbar,

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
  specialBusiness: SpecialBusinessContext | null;
  showDebugDetails?: boolean;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  favoriteBusyKey: string;
  favoriteAvailability: FavoriteAvailability;
  favoriteError: string;
  participationEnabled?: boolean;
  participationReady?: boolean;
  resolveRareOrderParticipation?: (order: NightBusinessOrder) => RareOrderParticipationResolution | null;
  toolbar?: ReactNode;
  compact?: boolean;
  fillAvailableHeight?: boolean;
  recipeLimit?: number;
  beverageLimit?: number;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
}) {
  const candidateRows = useMemo<RareOrderPresentationRow[]>(
    () => [
      ...recommendationIssues.map((issue) => ({
        kind: 'issue' as const,
        order: issue.order,
        issue,
        participation: null,
      })),
      ...recommendations.map((item) => ({
        kind: 'recommendation' as const,
        order: item.order,
        item,
        participation: null,
      })),
      ...pendingOrders.map((order) => ({ kind: 'pending' as const, order, participation: null })),
    ],
    [pendingOrders, recommendationIssues, recommendations],
  );
  const rows = useMemo(() => {
    if (!participationEnabled) {
      return sortNightOrderRows(candidateRows, orderSortMode, specialBusiness);
    }
    if (!resolveRareOrderParticipation) return [];
    return buildParticipatingRareOrderPresentationRows(candidateRows, resolveRareOrderParticipation);
  }, [candidateRows, orderSortMode, participationEnabled, resolveRareOrderParticipation, specialBusiness]);
  const collectionState = buildRareOrderRecommendationCollectionState({
    participationEnabled,
    participationReady,
    rowCount: rows.length,
    updateError,
    pending,
  });

  return (
    <ServiceOrderCollectionPanel
      mode={fillAvailableHeight ? 'rare-focus' : 'rare'}
      count={rows.length}
      state={collectionState}
      hasRows={rows.length > 0}
      toolbar={toolbar}
      compact={compact}
      notice={
        favoriteError || favoriteAvailability.reason ? (
          <div className="mb-2 border border-destructive/30 px-3 py-2 text-sm text-destructive">
            {favoriteError || favoriteAvailability.reason}
          </div>
        ) : undefined
      }
    >
      <div className={compact ? 'space-y-2' : 'space-y-4'}>
        {rows.map((row) => {
          if (row.kind === 'pending') {
            const orderKey = buildNightBusinessOrderKey(row.order);
            return (
              <ServiceOrderCardFrame
                key={`${orderKey}:pending`}
                compact={compact}
                pending
                title={`${row.order.guestName || '稀客'} · 桌 ${formatDesk(row.order.deskCode)}`}
                badges={<Badge variant="outline">推荐计算中</Badge>}
              >
                <div className="mt-1 flex flex-wrap gap-1.5">
                  <Badge variant="outline">料理 {row.order.foodTag || '无'}</Badge>
                  <Badge variant="outline">酒水 {row.order.beverageTag || '无'}</Badge>
                </div>
              </ServiceOrderCardFrame>
            );
          }
          if (row.kind === 'issue') {
            const issue = row.issue;
            const issueOccurrenceKey =
              issue.order.traceId ||
              `${issue.order.deskCode}:${issue.order.runtimeGuestId ?? 'unknown'}:${issue.order.foodTagId ?? 'missing'}:${issue.order.beverageTagId ?? 'missing'}`;
            return (
              <ServiceOrderCardFrame
                key={`${issueOccurrenceKey}:issue`}
                compact={compact}
                title={`${issue.order.guestName} · 桌 ${formatDesk(issue.order.deskCode)}`}
                message={issue.message}
              />
            );
          }

          const orderOccurrenceKey =
            row.item.order.traceId ||
            `${row.item.order.deskCode}:${row.item.order.runtimeGuestId ?? 'unknown'}:${row.item.order.foodTagId ?? 'missing'}:${row.item.order.beverageTagId ?? 'missing'}`;
          return (
            <RareOrderRecommendationCard
              key={orderOccurrenceKey}
              item={row.item}
              runtimeSets={runtimeSets}
              dataIndexes={dataIndexes}
              favorites={favorites}
              customRecipes={customRecipes}
              gamepadOccurrenceKey={`${fillAvailableHeight ? 'service-focus' : 'service'}:order:${orderOccurrenceKey}`}
              favoriteBusyKey={favoriteBusyKey}
              favoriteAvailability={{
                ...favoriteAvailability,
                canWrite: favoriteAvailability.canWrite && !pending && !updateError,
              }}
              compact={compact}
              recipeLimit={recipeLimit}
              beverageLimit={beverageLimit}
              showDebugDetails={showDebugDetails}
              participationEnabled={participationEnabled}
              participation={row.participation}
              onToggleRecipeFavorite={onToggleRecipeFavorite}
              onToggleBeverageFavorite={onToggleBeverageFavorite}
            />
          );
        })}
      </div>
    </ServiceOrderCollectionPanel>
  );
}
