import { useMemo, useState } from 'react';
import { IconRefresh } from '@tabler/icons-react';
import { Button, Card, CardContent, EmptyRow, Input, ListPanel } from '@/components/ui-kit';
import type { InventoryOperationsController } from '@/companion/hooks/useInventoryOperations';
import {
  formatInventoryQuantity,
  sortInventoryItems,
  type InventorySortMode,
} from '@/companion/domain/inventory-sorting';
import type { RuntimeSets } from '@/companion/types';
import { InventorySortControl, RuntimeUnavailable } from '@/companion/pages/shared';
import { DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { BeverageCatalogItem, IngredientCatalogItem } from '@/lib/catalog-types';

export function ModInventoryPanel({
  runtimeSets,
  runtimeLoaded,
  data,
  operations,
}: {
  runtimeSets: RuntimeSets | null;
  runtimeLoaded: boolean;
  data: RecommendationDataSet;
  operations: InventoryOperationsController;
}) {
  const [search, setSearch] = useState('');
  const [ingredientSortMode, setIngredientSortMode] = useState<InventorySortMode>('name');
  const [beverageSortMode, setBeverageSortMode] = useState<InventorySortMode>('name');

  const normalizedSearch = search.trim().toLocaleLowerCase('zh-Hans-CN');
  const ingredientRows = useMemo(
    () => filterInventoryItems(
      data.ingredients,
      normalizedSearch,
      runtimeSets?.ownedIngredientQty ?? null,
      ingredientSortMode,
    ),
    [data.ingredients, ingredientSortMode, normalizedSearch, runtimeSets?.ownedIngredientQty],
  );
  const beverageRows = useMemo(
    () => filterInventoryItems(
      data.beverages,
      normalizedSearch,
      runtimeSets?.ownedBeverageQty ?? null,
      beverageSortMode,
    ),
    [beverageSortMode, data.beverages, normalizedSearch, runtimeSets?.ownedBeverageQty],
  );
  const bulkIngredientIds = useMemo(
    () => runtimeSets
      ? data.ingredients
        .filter((ingredient) => ingredient.id >= 0 && runtimeSets.ingredientIds.has(ingredient.id))
        .map((ingredient) => ingredient.id)
      : [],
    [data.ingredients, runtimeSets],
  );
  const bulkBeverageIds = useMemo(
    () => runtimeSets
      ? data.beverages
        .filter((beverage) => {
          if (beverage.id < 0 || !runtimeSets.beverageIds.has(beverage.id)) return false;
          return (runtimeSets.ownedBeverageQty[beverage.id] ?? 0) >= 0;
        })
        .map((beverage) => beverage.id)
      : [],
    [data.beverages, runtimeSets],
  );

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="grid grid-cols-[minmax(14rem,1fr)_auto] gap-3 text-sm max-[719px]:grid-cols-1">
          <div className="min-w-0">
            <div className="font-semibold">库存数量修改</div>
            <div className="mt-1 text-xs text-muted-foreground">
              修改会直接写入游戏当前库存；请在游戏内保存后再退出。经营中修改可能会和实时消耗同时发生。
            </div>
            <div className="mt-1 text-xs text-muted-foreground">搜索只改变列表展示；批量修改包含全部已解锁项目。</div>
          </div>
          <div className="flex min-w-0 flex-wrap items-center justify-end gap-2 max-[719px]:justify-start" data-gamepad-axis="x">
            <Button
              size="sm"
              variant="outline"
              className="h-auto max-w-full"
              disabled={!operations.writable || bulkIngredientIds.length === 0}
              data-gamepad-focus-key="inventory:bulk:ingredient"
              onClick={() => void operations.applyBulkQuantity('ingredient', bulkIngredientIds, 99)}
            >
              <span className="whitespace-normal">全部已解锁材料设为 99（{bulkIngredientIds.length} 项）</span>
            </Button>
            <Button
              size="sm"
              variant="outline"
              className="h-auto max-w-full"
              disabled={!operations.writable || bulkBeverageIds.length === 0}
              data-gamepad-focus-key="inventory:bulk:beverage"
              onClick={() => void operations.applyBulkQuantity('beverage', bulkBeverageIds, 99)}
            >
              <span className="whitespace-normal">全部已解锁酒水设为 99（{bulkBeverageIds.length} 项）</span>
            </Button>
            <Input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="搜索名称或 ID"
              aria-label="搜索库存"
              className="min-w-[10rem] flex-1 basis-[12rem] max-[479px]:basis-full"
            />
            <Button
              size="sm"
              onClick={() => void operations.refresh()}
              disabled={!operations.refreshable}
              data-gamepad-focus-key="inventory:refresh"
            >
              <IconRefresh className="size-4" />
              刷新
            </Button>
          </div>
          {operations.message && (
            <div
              className="col-span-full text-xs text-muted-foreground"
              role={operations.phase === 'failed' || operations.phase === 'unconfirmed' || operations.phase === 'partial' ? 'alert' : 'status'}
              data-inventory-operation-state={operations.phase}
            >
              {operations.message}
            </div>
          )}
        </CardContent>
      </Card>

      {runtimeLoaded && runtimeSets ? <div className={DENSE_TWO_COLUMN_GRID}>
        <InventoryEditColumn
          title="材料"
          kind="ingredient"
          items={ingredientRows}
          ownedQty={runtimeSets.ownedIngredientQty}
          sortMode={ingredientSortMode}
          onSortModeChange={setIngredientSortMode}
          writable={operations.writable}
          onApply={operations.applyQuantity}
        />
        <InventoryEditColumn
          title="酒水"
          kind="beverage"
          items={beverageRows}
          ownedQty={runtimeSets.ownedBeverageQty}
          sortMode={beverageSortMode}
          onSortModeChange={setBeverageSortMode}
          writable={operations.writable}
          onApply={operations.applyQuantity}
        />
      </div> : <RuntimeUnavailable />}
    </div>
  );
}

function InventoryEditColumn<TItem extends IngredientCatalogItem | BeverageCatalogItem>({
  title,
  kind,
  items,
  ownedQty,
  sortMode,
  onSortModeChange,
  writable,
  onApply,
}: {
  title: string;
  kind: 'ingredient' | 'beverage';
  items: TItem[];
  ownedQty: Record<number, number>;
  sortMode: InventorySortMode;
  onSortModeChange: (value: InventorySortMode) => void;
  writable: boolean;
  onApply: InventoryOperationsController['applyQuantity'];
}) {
  return (
    <ListPanel
      title={`${title} (${items.length})`}
      action={(
        <InventorySortControl
          value={sortMode}
          onChange={onSortModeChange}
          disabled={items.length === 0}
          aria-label={`${title}排序`}
        />
      )}
    >
      <div className="space-y-2">
        {items.length === 0 && <EmptyRow text="没有匹配项目" />}
        {items.map((item) => {
          const key = inventoryDraftKey(kind, item.id);
          const quantity = ownedQty[item.id] ?? 0;
          const editable = writable && item.id >= 0 && quantity >= 0;

          return (
            <div
              key={key}
              className="steward-data-row px-2 py-1.5 text-sm"
              data-gamepad-row="true"
              data-gamepad-row-key={`inventory:${key}`}
            >
              <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2">
                <div className="min-w-0 pr-1">
                  <div className="truncate font-medium" title={item.name}>{item.name}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    ID {item.id} · 当前 {formatInventoryQuantity(quantity)} · 单价 {item.price}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable || quantity === 0}
                    data-gamepad-focus-key={`inventory:${key}:sub10`}
                    onClick={() => void onApply(kind, item.id, quantity - 10, item.name)}
                  >
                    -10
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable}
                    data-gamepad-focus-key={`inventory:${key}:add10`}
                    onClick={() => void onApply(kind, item.id, quantity + 10, item.name)}
                  >
                    +10
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable || quantity === 99}
                    data-gamepad-focus-key={`inventory:${key}:set99`}
                    onClick={() => void onApply(kind, item.id, 99, item.name)}
                  >
                    99
                  </Button>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </ListPanel>
  );
}

function filterInventoryItems<TItem extends IngredientCatalogItem | BeverageCatalogItem>(
  items: TItem[],
  normalizedSearch: string,
  ownedQty: Record<number, number> | null,
  sortMode: InventorySortMode,
): TItem[] {
  const rows = normalizedSearch
    ? items.filter((item) =>
      item.name.toLocaleLowerCase('zh-Hans-CN').includes(normalizedSearch) || String(item.id).includes(normalizedSearch))
    : items;
  return sortInventoryItems(rows, ownedQty, sortMode);
}

function inventoryDraftKey(kind: 'ingredient' | 'beverage', itemId: number) {
  return `${kind}:${itemId}`;
}
