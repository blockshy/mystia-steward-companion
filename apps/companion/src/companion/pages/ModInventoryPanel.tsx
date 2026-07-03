import { useCallback, useMemo, useState } from 'react';
import { IconRefresh } from '@tabler/icons-react';
import { Button, Card, CardContent, EmptyRow, Input, ListPanel } from '@/components/ui-kit';
import { writeInventoryBulkQuantity, writeInventoryQuantity } from '@/companion/api';
import {
  formatInventoryQuantity,
  sortInventoryItems,
  type InventorySortMode,
} from '@/companion/domain/inventory-sorting';
import { normalizeEditableQuantity } from '@/companion/preferences';
import type { RuntimeSets } from '@/companion/types';
import { InventorySortControl, RuntimeUnavailable } from '@/companion/pages/shared';
import { DENSE_CARD_HEADER_GRID, DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { BeverageCatalogItem, IngredientCatalogItem } from '@/lib/catalog-types';

export function ModInventoryPanel({
  endpoint,
  apiToken,
  runtimeSets,
  runtimeLoaded,
  data,
  onRefresh,
}: {
  endpoint: string;
  apiToken: string;
  runtimeSets: RuntimeSets | null;
  runtimeLoaded: boolean;
  data: RecommendationDataSet;
  onRefresh: () => Promise<void>;
}) {
  const [search, setSearch] = useState('');
  const [ingredientSortMode, setIngredientSortMode] = useState<InventorySortMode>('name');
  const [beverageSortMode, setBeverageSortMode] = useState<InventorySortMode>('name');
  const [busyKey, setBusyKey] = useState('');
  const [message, setMessage] = useState('');

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

  const applyQuantity = useCallback(async (kind: 'ingredient' | 'beverage', id: number, quantity: number) => {
    const key = inventoryDraftKey(kind, id);
    const targetQuantity = normalizeEditableQuantity(quantity);
    setBusyKey(key);
    setMessage('');

    try {
      const result = await writeInventoryQuantity(endpoint, apiToken, kind, id, targetQuantity);
      if (!result.ok) throw new Error(result.error || '库存修改失败');
      setMessage(`${kind === 'ingredient' ? '材料' : '酒水'} #${id}: ${result.previousQuantity} -> ${result.quantity}`);
      await onRefresh();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : String(err));
    } finally {
      setBusyKey('');
    }
  }, [apiToken, endpoint, onRefresh]);

  const applyBulkQuantity = useCallback(async (
    kind: 'ingredient' | 'beverage',
    ids: number[],
    quantity: number,
  ) => {
    const key = inventoryBulkKey(kind);
    const targetQuantity = normalizeEditableQuantity(quantity);
    setBusyKey(key);
    setMessage('');

    try {
      const result = await writeInventoryBulkQuantity(endpoint, apiToken, kind, ids, targetQuantity);
      const label = kind === 'ingredient' ? '材料' : '酒水';
      const suffix = result.failed > 0 && result.errors.length > 0
        ? `；失败：${result.errors.slice(0, 3).join('；')}`
        : '';
      setMessage(`${label}批量设为 ${targetQuantity}：变更 ${result.changed}，未变 ${result.unchanged}，失败 ${result.failed}${suffix}`);
      await onRefresh();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : String(err));
    } finally {
      setBusyKey('');
    }
  }, [apiToken, endpoint, onRefresh]);

  if (!runtimeLoaded || !runtimeSets) {
    return <RuntimeUnavailable />;
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className={`${DENSE_CARD_HEADER_GRID} p-4 text-sm`}>
          <div>
            <div className="font-semibold">库存数量修改</div>
            <div className="mt-1 text-xs text-muted-foreground">
              修改会写入当前游戏运行时库存；请在游戏内保存后再退出。经营中修改可能会和实时消耗同时发生。
            </div>
          </div>
          <div className="flex min-w-0 flex-wrap items-center justify-end gap-2" data-gamepad-axis="x">
            <Button
              size="sm"
              variant="outline"
              disabled={!apiToken || busyKey !== '' || bulkIngredientIds.length === 0}
              data-gamepad-focus-key="inventory:bulk:ingredient"
              onClick={() => applyBulkQuantity('ingredient', bulkIngredientIds, 99)}
            >
              材料设为 99
            </Button>
            <Button
              size="sm"
              variant="outline"
              disabled={!apiToken || busyKey !== '' || bulkBeverageIds.length === 0}
              data-gamepad-focus-key="inventory:bulk:beverage"
              onClick={() => applyBulkQuantity('beverage', bulkBeverageIds, 99)}
            >
              酒水设为 99
            </Button>
            <Input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="搜索名称或 ID"
              className="w-56"
            />
            <Button size="sm" onClick={onRefresh}>
              <IconRefresh className="size-4" />
              刷新
            </Button>
          </div>
          {message && (
            <div className="lg:col-span-2 text-xs text-muted-foreground">
              {message}
            </div>
          )}
        </CardContent>
      </Card>

      <div className={DENSE_TWO_COLUMN_GRID}>
        <InventoryEditColumn
          title="材料"
          kind="ingredient"
          items={ingredientRows}
          ownedQty={runtimeSets.ownedIngredientQty}
          sortMode={ingredientSortMode}
          onSortModeChange={setIngredientSortMode}
          busyKey={busyKey}
          apiToken={apiToken}
          onApply={applyQuantity}
        />
        <InventoryEditColumn
          title="酒水"
          kind="beverage"
          items={beverageRows}
          ownedQty={runtimeSets.ownedBeverageQty}
          sortMode={beverageSortMode}
          onSortModeChange={setBeverageSortMode}
          busyKey={busyKey}
          apiToken={apiToken}
          onApply={applyQuantity}
        />
      </div>
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
  busyKey,
  apiToken,
  onApply,
}: {
  title: string;
  kind: 'ingredient' | 'beverage';
  items: TItem[];
  ownedQty: Record<number, number>;
  sortMode: InventorySortMode;
  onSortModeChange: (value: InventorySortMode) => void;
  busyKey: string;
  apiToken: string;
  onApply: (kind: 'ingredient' | 'beverage', id: number, quantity: number) => Promise<void>;
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
          const editable = Boolean(apiToken) && item.id >= 0 && quantity >= 0;
          const busy = busyKey === key || busyKey === inventoryBulkKey(kind);

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
                    disabled={!editable || busy}
                    data-gamepad-focus-key={`inventory:${key}:sub10`}
                    onClick={() => onApply(kind, item.id, quantity - 10)}
                  >
                    -10
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable || busy}
                    data-gamepad-focus-key={`inventory:${key}:add10`}
                    onClick={() => onApply(kind, item.id, quantity + 10)}
                  >
                    +10
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable || busy}
                    data-gamepad-focus-key={`inventory:${key}:set99`}
                    onClick={() => onApply(kind, item.id, 99)}
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

function inventoryBulkKey(kind: 'ingredient' | 'beverage') {
  return `bulk:${kind}`;
}
