import type { ReactNode } from 'react';
import { Card, CardContent, InfoLine, ListPanel, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import { formatPerformanceMs } from '@/companion/formatters';
import type { LocalApiSnapshot, NightBusinessContext, OverviewTab, RecommendationStateSnapshot } from '@/companion/types';
import { OverviewConnectionPanel } from '@/companion/pages/overview/OverviewConnectionPanel';
import { DENSE_TWO_COLUMN_GRID_TIGHT } from '@/companion/pages/shared-constants';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

const OVERVIEW_TAB_TRIGGER_CLASS = 'min-w-[6rem] flex-none min-[640px]:min-w-0 min-[640px]:flex-1';

export function ModOverviewPanel({
  endpointDraft,
  onEndpointDraftChange,
  apiTokenDraft,
  onApiTokenDraftChange,
  onApplyEndpointConnection,
  onPauseConnection,
  onResumeConnection,
  onDiscardConnectionDraft,
  connectionDraftDirty,
  overviewTab,
  onOverviewTabChange,
  networkPanel,
  devicesPanel,
  onRefresh,
  apiToken,
  connectionPaused,
  connectionFailureCount,
  loading,
  normalizedEndpoint,
  snapshot,
  runtime,
  night,
  data,
  error,
  lastConnectedAt,
  showDebugDetails,
}: {
  endpointDraft: string;
  onEndpointDraftChange: (value: string) => void;
  apiTokenDraft: string;
  onApiTokenDraftChange: (value: string) => void;
  onApplyEndpointConnection: () => void;
  onPauseConnection: () => void;
  onResumeConnection: () => void;
  onDiscardConnectionDraft: () => void;
  connectionDraftDirty: boolean;
  overviewTab: OverviewTab;
  onOverviewTabChange: (tab: OverviewTab) => void;
  networkPanel: ReactNode;
  devicesPanel: ReactNode;
  onRefresh: () => void;
  apiToken: string;
  connectionPaused: boolean;
  connectionFailureCount: number;
  loading: boolean;
  normalizedEndpoint: string;
  snapshot: LocalApiSnapshot | null;
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  data: RecommendationDataSet;
  error: string;
  lastConnectedAt: Date | null;
  showDebugDetails: boolean;
}) {

  return (
    <div className="space-y-4">
      <Tabs value={overviewTab} onValueChange={(value) => onOverviewTabChange(value as OverviewTab)} className="space-y-4">
        <TabsList
          scrollable
          className="grid h-9 w-full grid-cols-4"
          data-overview-tabs="true"
        >
          <TabsTrigger
            value="connection"
            className={OVERVIEW_TAB_TRIGGER_CLASS}
            data-gamepad-clickable="true"
          >
            客户端
          </TabsTrigger>
          <TabsTrigger value="network" className={OVERVIEW_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
            主机网络
          </TabsTrigger>
          <TabsTrigger value="devices" className={OVERVIEW_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
            设备共享
          </TabsTrigger>
          <TabsTrigger value="status" className={OVERVIEW_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
            运行状态
          </TabsTrigger>
        </TabsList>

        <TabsContent value="connection" className="space-y-4">
          <OverviewConnectionPanel
            endpointDraft={endpointDraft}
            onEndpointDraftChange={onEndpointDraftChange}
            apiTokenDraft={apiTokenDraft}
            onApiTokenDraftChange={onApiTokenDraftChange}
            onApplyEndpointConnection={onApplyEndpointConnection}
            onPauseConnection={onPauseConnection}
            onResumeConnection={onResumeConnection}
            onDiscardConnectionDraft={onDiscardConnectionDraft}
            connectionDraftDirty={connectionDraftDirty}
            onRefresh={onRefresh}
            apiToken={apiToken}
            connectionPaused={connectionPaused}
            connectionFailureCount={connectionFailureCount}
            error={error}
            lastConnectedAt={lastConnectedAt}
            loading={loading}
            normalizedEndpoint={normalizedEndpoint}
            night={night}
            snapshot={snapshot}
          />
        </TabsContent>

        <TabsContent value="network">{overviewTab === 'network' && networkPanel}</TabsContent>
        <TabsContent value="devices">{overviewTab === 'devices' && devicesPanel}</TabsContent>

        <TabsContent value="status" className="space-y-4">
          <Card>
            <CardContent className={`${DENSE_TWO_COLUMN_GRID_TIGHT} text-sm`}>
              <InfoLine label="数据来源" value="游戏实时 API，不读取 .memory 存档" />
              <InfoLine label="场景" value={snapshot?.activeSceneName || '未知'} />
              <InfoLine
                label="游戏状态"
                value={!snapshot ? '暂无游戏数据' : snapshot.runtimeLoaded ? '游戏数据已加载' : '等待游戏数据加载'}
              />
              {showDebugDetails && <InfoLine label="游戏数据来源" value={snapshot?.runtimeSource || '未知'} />}
              {showDebugDetails && <InfoLine label="场景就绪" value={snapshot?.runtimeSceneReadinessStatus || '暂无'} mono />}
              {showDebugDetails && <InfoLine label="本场经营状态" value={snapshot?.runtimeNightBusinessLifecycleStatus || '暂无'} mono />}
              {showDebugDetails && <InfoLine label="自动化可用状态" value={snapshot?.runtimeNightBusinessAutomationStatus || '暂无'} mono />}
              <InfoLine
                label="推荐数据"
                value={data.source === 'runtime' ? '游戏实时数据已就绪' : '等待游戏实时数据'}
              />
              {showDebugDetails && <InfoLine label="性能耗时" value={formatPerformanceMs(snapshot?.performanceMs)} mono />}
            </CardContent>
          </Card>

          <ListPanel title="实时标签">
            <div className={DENSE_TWO_COLUMN_GRID_TIGHT}>
              <InfoLine label="流行喜爱" value={runtime?.popularFoodTag || '无'} />
              <InfoLine label="流行厌恶" value={runtime?.popularHateFoodTag || '无'} />
              <InfoLine label="当前经营场景" value={night?.place || night?.placeLabel || '无经营场景'} />
              {showDebugDetails && <InfoLine label="经营扫描" value={night?.source || '暂无'} />}
            </div>
          </ListPanel>
        </TabsContent>

      </Tabs>
    </div>
  );
}
