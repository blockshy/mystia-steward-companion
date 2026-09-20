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
} from '@/components/ui-kit';
import { getNightBusinessAutomationPauseLabel } from '@/companion/domain/automation-runtime';
import type { CompanionPreferences } from '@/companion/preferences';
import type {
  AutomationSafetyBarrierDiagnostic,
  NormalAutoOrderDiagnostic,
  RareAutoOrderDiagnostic,
} from '@/companion/types';
import { OrderTraceBadge } from '@/companion/pages/service/ServiceContextPanels';

export function AutomationRuntimePanel({
  autoPrepBusy,
  autoPrepMessage,
  autoPrepPaused,
  rareOrderDiagnostics,
  autoPrepPreferences,
  normalOrderBusy,
  normalOrderMessage,
  normalOrderPausedCount,
  normalOrderDiagnostics,
  automationRuntimeBlockReason,
  automationSafetyBarriers,
  automationBarrierAckBusyKey,
  onRetryRareAutomationOrder,
  onResetRareAutomationOrder,
  onRetryNormalAutomationOrder,
  onResetNormalAutomationOrder,
  onAcknowledgeAutomationBarrier,
  showDebugDetails,
}: {
  autoPrepBusy: boolean;
  autoPrepMessage: string;
  autoPrepPaused: boolean;
  rareOrderDiagnostics: RareAutoOrderDiagnostic[];
  autoPrepPreferences: CompanionPreferences;
  normalOrderBusy: boolean;
  normalOrderMessage: string;
  normalOrderPausedCount: number;
  normalOrderDiagnostics: NormalAutoOrderDiagnostic[];
  automationRuntimeBlockReason: string;
  automationSafetyBarriers: AutomationSafetyBarrierDiagnostic[];
  automationBarrierAckBusyKey: string;
  onRetryRareAutomationOrder: (orderKey: string) => void;
  onResetRareAutomationOrder: (orderKey: string) => void;
  onRetryNormalAutomationOrder: (orderKey: string) => void;
  onResetNormalAutomationOrder: (orderKey: string) => void;
  onAcknowledgeAutomationBarrier: (sequence: number) => void;
  showDebugDetails: boolean;
}) {
  const automationRuntimePauseLabel = getNightBusinessAutomationPauseLabel(automationRuntimeBlockReason);
  return (
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
          <div className="grid items-start gap-4 min-[1000px]:grid-cols-2">
            <div className="space-y-4">
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
            </div>

            <div className="space-y-4">
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
            </div>
          </div>
        </>
      ) : (
        <ListPanel title="自动化">
          <EmptyRow text="在“执行配置”中开启“启用自动化（实验性）”后，这里会显示自动化执行状态。" />
        </ListPanel>
      )}
    </div>
  );
}
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
        <AccordionTrigger className="px-2 py-1.5 text-xs" data-gamepad-focus-key={`automation-detail:${id}`}>
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
          const targetLabel =
            diagnostic.targetKind === 'normal'
              ? '普客'
              : diagnostic.targetKind === 'rare'
                ? '稀客'
                : diagnostic.targetKind;
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
                    disabled={
                      busy || (diagnostic.manualResolutionRequired && Boolean(automationBarrierAckBusyKey))
                    }
                    data-gamepad-focus-key={`rare-auto:${diagnostic.orderKey}:reset`}
                  >
                    {diagnostic.manualResolutionRequired &&
                    automationBarrierAckBusyKey === `rare:${diagnostic.orderKey}`
                      ? '确认中'
                      : diagnostic.manualResolutionRequired
                        ? '确认已处理'
                        : '重置'}
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
                    value={`重试 ${diagnostic.retryCount}/${preferences.autoMaxStepRetries} · 重新制作 ${diagnostic.rollbackCount}/${preferences.autoMaxRollbacks}`}
                  />
                )}
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
                <OrderTraceBadge traceId={diagnostic.traceId} showDebugDetails={showDebugDetails} />
                <Badge variant={diagnostic.paused ? 'destructive' : 'secondary'}>
                  {diagnostic.paused ? '订单暂停' : '订单可执行'}
                </Badge>
                {diagnostic.manualResolutionRequired && <Badge variant="destructive">需人工确认</Badge>}
                <Badge variant={diagnostic.prepared ? 'secondary' : 'outline'}>
                  料理{diagnostic.prepared ? '已开锅' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.beverageDeliveryRequested ? 'secondary' : 'outline'}>
                  酒水处理
                  {diagnostic.hasServedBeverage
                    ? '已确认'
                    : diagnostic.beverageDeliveryRequested
                      ? '待确认'
                      : '待处理'}
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
        <Badge variant={preferences.autoRareOrderEnabled ? 'secondary' : 'outline'}>
          启用 {preferences.autoRareOrderEnabled ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoPrepTakeBeverage ? 'secondary' : 'outline'}>
          送酒 {preferences.autoPrepTakeBeverage ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoPrepStartCooking ? 'secondary' : 'outline'}>
          料理 {preferences.autoPrepStartCooking ? '开' : '关'}
        </Badge>
        {preferences.autoPrepStartCooking && <Badge variant="secondary">QTE 自动完成</Badge>}
        <Badge variant={preferences.autoPrepCollectCooking ? 'secondary' : 'outline'}>
          直送 {preferences.autoPrepCollectCooking ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoPrepCompleteOrder ? 'secondary' : 'outline'}>
          完成 {preferences.autoPrepCompleteOrder ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoPrepRecipeFavoritesOnly ? 'secondary' : 'outline'}>
          收藏料理 {preferences.autoPrepRecipeFavoritesOnly ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoPrepBeverageFavoritesOnly ? 'secondary' : 'outline'}>
          收藏酒水 {preferences.autoPrepBeverageFavoritesOnly ? '开' : '关'}
        </Badge>
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
                    disabled={
                      busy || (diagnostic.manualResolutionRequired && Boolean(automationBarrierAckBusyKey))
                    }
                    data-gamepad-focus-key={`normal-auto:${diagnostic.orderKey}:reset`}
                  >
                    {diagnostic.manualResolutionRequired &&
                    automationBarrierAckBusyKey === `normal:${diagnostic.orderKey}`
                      ? '确认中'
                      : diagnostic.manualResolutionRequired
                        ? '确认已处理'
                        : '重置'}
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
                      value={`重试 ${diagnostic.retryCount}/${preferences.autoMaxStepRetries} · 重新制作 ${diagnostic.rollbackCount}/${preferences.autoMaxRollbacks}`}
                    />
                    <InfoLine label="来源" value={diagnostic.source || '未知'} />
                    <InfoLine label="内部标识" value={diagnostic.orderKey} mono />
                  </>
                )}
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
                <OrderTraceBadge traceId={diagnostic.traceId} showDebugDetails={showDebugDetails} />
                {diagnostic.manualResolutionRequired && <Badge variant="destructive">需人工确认</Badge>}
                <Badge variant={diagnostic.beverageDeliveryRequested ? 'secondary' : 'outline'}>
                  酒水处理
                  {diagnostic.hasServedBeverage
                    ? '已确认'
                    : diagnostic.beverageDeliveryRequested
                      ? '待确认'
                      : '待处理'}
                </Badge>
                <Badge variant={diagnostic.prepared ? 'secondary' : 'outline'}>
                  料理{diagnostic.prepared ? '已开锅' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.foodDeliveryRequested ? 'secondary' : 'outline'}>
                  料理送达
                  {diagnostic.hasServedFood
                    ? '已确认'
                    : diagnostic.foodDeliveryRequested
                      ? '待确认'
                      : '未请求'}
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
        <Badge variant={preferences.autoNormalOrderEnabled ? 'secondary' : 'outline'}>
          启用 {preferences.autoNormalOrderEnabled ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoNormalTakeBeverage ? 'secondary' : 'outline'}>
          酒水 {preferences.autoNormalTakeBeverage ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoNormalStartCooking ? 'secondary' : 'outline'}>
          料理 {preferences.autoNormalStartCooking ? '开' : '关'}
        </Badge>
        {preferences.autoNormalStartCooking && <Badge variant="secondary">QTE 自动完成</Badge>}
        <Badge variant={preferences.autoNormalDeliverFood ? 'secondary' : 'outline'}>
          送料理 {preferences.autoNormalDeliverFood ? '开' : '关'}
        </Badge>
        <Badge variant={preferences.autoNormalCompleteOrder ? 'secondary' : 'outline'}>
          完成 {preferences.autoNormalCompleteOrder ? '开' : '关'}
        </Badge>
      </div>
    </div>
  );
}
