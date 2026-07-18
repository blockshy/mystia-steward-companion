import { useMemo, useState } from 'react';
import { Card, CardContent, InfoLine, ListPanel, Metric, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import { formatPerformanceMs, formatTime } from '@/companion/formatters';
import type { LocalApiSnapshot, NightBusinessContext, OverviewTab, RecommendationStateSnapshot } from '@/companion/types';
import { LowStockColumn } from '@/companion/pages/shared';
import { buildLowStockEntries, DENSE_FOUR_COLUMN_GRID, DENSE_TWO_COLUMN_GRID, DENSE_TWO_COLUMN_GRID_TIGHT, INNER_TAB_TRIGGER_CLASS } from '@/companion/pages/shared-constants';
import type { buildRecommendationDataIndexes, RecommendationDataSet } from '@/lib/recommendation-data';

export function ModOverviewPanel({
  endpoint,
  snapshot,
  runtime,
  night,
  data,
  indexes,
  error,
  lastConnectedAt,
  showDebugDetails,
}: {
  endpoint: string;
  snapshot: LocalApiSnapshot | null;
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  data: RecommendationDataSet;
  indexes: ReturnType<typeof buildRecommendationDataIndexes>;
  error: string;
  lastConnectedAt: Date | null;
  showDebugDetails: boolean;
}) {
  const ownedIngredientEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedIngredientQty ?? {}, indexes.ingredientNameById),
    [indexes.ingredientNameById, runtime?.ownedIngredientQty],
  );
  const ownedBeverageEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedBeverageQty ?? {}, indexes.beverageNameById),
    [indexes.beverageNameById, runtime?.ownedBeverageQty],
  );
  const [overviewTab, setOverviewTab] = useState<OverviewTab>('status');

  return (
    <div className="space-y-4">
      <Tabs value={overviewTab} onValueChange={(value) => setOverviewTab(value as OverviewTab)} className="space-y-4">
        <TabsList className="grid h-9 w-full grid-cols-3">
          <TabsTrigger value="status" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
            状态
          </TabsTrigger>
          <TabsTrigger value="inventory" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
            库存
          </TabsTrigger>
          <TabsTrigger value="actions" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
            操作
          </TabsTrigger>
        </TabsList>

        <TabsContent value="status" className="space-y-4">
          <Card>
            <CardContent className={`${DENSE_TWO_COLUMN_GRID_TIGHT} p-4 text-sm`}>
              <InfoLine label="数据来源" value="游戏实时 API，不读取 .memory 存档" />
              {showDebugDetails && <InfoLine label="API 地址" value={endpoint} mono />}
              <InfoLine label="连接状态" value={error ? `未连接: ${error}` : snapshot ? '已连接' : '连接中'} />
              <InfoLine label="最近响应" value={lastConnectedAt ? formatTime(lastConnectedAt) : '暂无'} />
              <InfoLine label="场景" value={snapshot?.activeSceneName || '未知'} />
              <InfoLine label="运行时状态" value={snapshot?.status || '暂无快照'} />
              {showDebugDetails && <InfoLine label="运行时来源" value={snapshot?.runtimeSource || '未知'} />}
              {showDebugDetails && <InfoLine label="场景就绪" value={snapshot?.runtimeSceneReadinessStatus || '暂无'} mono />}
              <InfoLine
                label="推荐数据"
                value={data.source === 'runtime' ? `游戏运行时 (${data.status})` : `等待游戏运行时数据 (${data.status})`}
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

        <TabsContent value="inventory" className="space-y-4">
          <Card>
            <CardContent className={`${DENSE_FOUR_COLUMN_GRID} p-4 text-sm`}>
              <Metric label="可用料理" value={runtime?.availableRecipeIds.length ?? 0} />
              <Metric label="可用酒水" value={runtime?.availableBeverageIds.length ?? 0} />
              <Metric label="可用食材" value={runtime?.availableIngredientIds.length ?? 0} />
              <Metric label="明星店" value={runtime?.famousShopEnabled ? '开启' : '关闭'} />
            </CardContent>
          </Card>

          <ListPanel title="低库存概览">
            <div className={DENSE_TWO_COLUMN_GRID}>
              <LowStockColumn title="材料" entries={ownedIngredientEntries} />
              <LowStockColumn title="酒水" entries={ownedBeverageEntries} />
            </div>
          </ListPanel>
        </TabsContent>

        <TabsContent value="actions" className="space-y-4">
          <ListPanel title="快捷键">
            <div className={`${DENSE_TWO_COLUMN_GRID_TIGHT} text-sm`}>
              <InfoLine label="F8" value="在游戏与独立窗口之间切换焦点或重新显示伴随窗口" />
              <InfoLine label="F10" value="开启或关闭鼠标穿透锁定；穿透后可用它恢复窗口操作" />
              <InfoLine label="RS Click" value="手柄默认在游戏与独立窗口之间切换" />
              <InfoLine label="手柄导航" value="左摇杆/十字键移动，A 确认，B 关闭/返回，X 收藏，Y 专注模式，LB/RB 切页，LT/RT 滚动" />
              <InfoLine label="专注模式" value="Y 进入专注模式或切换精简模式，X 收藏当前推荐项" />
              <InfoLine label="窗口关闭" value="关闭按钮会隐藏到托盘；托盘菜单可重新显示或退出" />
            </div>
          </ListPanel>
        </TabsContent>
      </Tabs>
    </div>
  );
}
