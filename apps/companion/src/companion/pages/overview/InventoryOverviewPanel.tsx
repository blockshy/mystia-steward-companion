import { useMemo } from 'react';
import { Card, CardContent, ListPanel, Metric } from '@/components/ui-kit';
import { LowStockColumn } from '@/companion/pages/shared';
import { buildLowStockEntries, DENSE_FOUR_COLUMN_GRID, DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';
import type { RecommendationStateSnapshot } from '@/companion/types';
import type { buildRecommendationDataIndexes } from '@/lib/recommendation-data';

export function InventoryOverviewPanel({ runtime, indexes }: {
  runtime: RecommendationStateSnapshot | null;
  indexes: ReturnType<typeof buildRecommendationDataIndexes>;
}) {
  const ownedIngredientEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedIngredientQty ?? {}, indexes.ingredientNameById),
    [indexes.ingredientNameById, runtime?.ownedIngredientQty],
  );
  const ownedBeverageEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedBeverageQty ?? {}, indexes.beverageNameById),
    [indexes.beverageNameById, runtime?.ownedBeverageQty],
  );
  return <div className="space-y-4">
          <Card>
            <CardContent className={`${DENSE_FOUR_COLUMN_GRID} text-sm`}>
              <Metric label="可用料理" value={runtime?.availableRecipeIds.length ?? '未读取'} />
              <Metric label="可用酒水" value={runtime?.availableBeverageIds.length ?? '未读取'} />
              <Metric label="可用食材" value={runtime?.availableIngredientIds.length ?? '未读取'} />
              <Metric label="明星店" value={!runtime ? '未读取' : runtime.famousShopEnabled ? '开启' : '关闭'} />
            </CardContent>
          </Card>

          <ListPanel title="低库存概览">
            <div className={DENSE_TWO_COLUMN_GRID}>
              <LowStockColumn title="材料" entries={ownedIngredientEntries} />
              <LowStockColumn title="酒水" entries={ownedBeverageEntries} />
            </div>
          </ListPanel>
        </div>;
}
