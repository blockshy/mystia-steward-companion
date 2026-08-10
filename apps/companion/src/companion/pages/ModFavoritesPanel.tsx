import { useMemo, useState } from 'react';
import { IconRefresh, IconTrash } from '@tabler/icons-react';

import {
  Badge,
  Button,
  EmptyRow,
  Input,
  ListPanel,
  SegmentedControl,
} from '@/components/ui-kit';
import {
  filterFavoriteManagementEntries,
  groupFavoriteManagementEntries,
  resolveFavoriteManagementEntries,
  type FavoriteManagementEntry,
  type FavoriteManagementFilter,
} from '@/companion/domain/favorite-management';
import type { FavoriteData } from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

interface ModFavoritesPanelProps {
  apiToken: string;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  favoriteRefreshing: boolean;
  data: RecommendationDataSet;
  onRefresh: () => Promise<void>;
  onRemoveRecipe: (id: string) => Promise<boolean>;
  onRemoveBeverage: (id: string) => Promise<boolean>;
}

export function ModFavoritesPanel({
  apiToken,
  favorites,
  favoriteBusyKey,
  favoriteError,
  favoriteRefreshing,
  data,
  onRefresh,
  onRemoveRecipe,
  onRemoveBeverage,
}: ModFavoritesPanelProps) {
  const [filter, setFilter] = useState<FavoriteManagementFilter>('recipe');
  const [query, setQuery] = useState('');
  const entries = useMemo(
    () => resolveFavoriteManagementEntries(favorites, data),
    [data, favorites],
  );
  const filteredEntries = useMemo(
    () => filterFavoriteManagementEntries(entries, filter, query),
    [entries, filter, query],
  );
  const groups = useMemo(
    () => groupFavoriteManagementEntries(filteredEntries),
    [filteredEntries],
  );
  const recipeCount = favorites.recipes.length;
  const beverageCount = favorites.beverages.length;
  const customerCount = new Set(entries.map((entry) => entry.customerId)).size;
  const busy = Boolean(favoriteBusyKey);
  const hasQuery = Boolean(query.trim());

  return (
    <div className="space-y-4" data-favorite-management="true">
      <div className="steward-inline-panel space-y-3 px-3 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <div>
            <div className="text-sm font-semibold">收藏概览</div>
            <div className="mt-0.5 text-xs text-muted-foreground">
              集中查看和取消料理方案、酒水收藏；修改配方请前往“自定义推荐料理”。
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant="outline">料理 {recipeCount}</Badge>
            <Badge variant="outline">酒水 {beverageCount}</Badge>
            <Badge variant="outline">稀客 {customerCount}</Badge>
          </div>
        </div>
        {!apiToken && (
          <div className="border border-border px-3 py-2 text-sm text-muted-foreground">
            尚未连接 Mod 本地 API，无法读取或修改收藏。
          </div>
        )}
        {favoriteError && (
          <div className="border border-destructive/30 px-3 py-2 text-sm text-destructive">
            {favoriteError}
          </div>
        )}
      </div>

      <ListPanel
        title={`收藏列表 (${filteredEntries.length})`}
        action={(
          <Button
            type="button"
            size="xs"
            variant="outline"
            leftSection={<IconRefresh size={14} aria-hidden="true" />}
            loading={favoriteRefreshing}
            disabled={!apiToken || busy || favoriteRefreshing}
            data-gamepad-focus-key="favorites:refresh"
            onClick={() => void onRefresh()}
          >
            刷新
          </Button>
        )}
        gamepadScrollKey="favorites:list"
        gamepadScrollLabel="收藏列表"
      >
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border pb-3">
          <SegmentedControl
            value={filter}
            options={[
              { value: 'recipe', label: `料理 ${recipeCount}` },
              { value: 'beverage', label: `酒水 ${beverageCount}` },
              { value: 'all', label: `全部 ${recipeCount + beverageCount}` },
            ]}
            onValueChange={setFilter}
          />
          <Input
            value={query}
            className="min-w-52 flex-1 min-[720px]:max-w-sm"
            aria-label="搜索收藏"
            placeholder="搜索稀客、料理/酒水、Tag、加料或 ID"
            disabled={entries.length === 0}
            onChange={(event) => setQuery(event.currentTarget.value)}
          />
        </div>

        {entries.length === 0 && (
          <EmptyRow text={apiToken ? '暂无料理或酒水收藏' : '连接 Mod 后显示收藏'} />
        )}
        {entries.length > 0 && filteredEntries.length === 0 && (
          <EmptyRow text={hasQuery ? '没有符合搜索条件的收藏' : '当前分类暂无收藏'} />
        )}
        {groups.map((group) => {
          const groupRecipeCount = group.entries.filter((entry) => entry.kind === 'recipe').length;
          const groupBeverageCount = group.entries.length - groupRecipeCount;
          return (
            <section
              key={group.customerId}
              className="border-b border-border last:border-b-0"
              data-favorite-customer-id={group.customerId}
            >
              <div className="flex flex-wrap items-center justify-between gap-2 bg-muted/40 px-3 py-2">
                <h3 className="font-medium">{group.customerName}</h3>
                <div className="flex flex-wrap gap-1.5">
                  {groupRecipeCount > 0 && <Badge variant="outline">料理 {groupRecipeCount}</Badge>}
                  {groupBeverageCount > 0 && <Badge variant="outline">酒水 {groupBeverageCount}</Badge>}
                  <Badge variant="outline">ID {group.customerId}</Badge>
                </div>
              </div>
              {group.entries.map((entry) => (
                <FavoriteManagementRow
                  key={`${entry.kind}:${entry.id}`}
                  entry={entry}
                  busy={busy}
                  currentBusy={favoriteBusyKey === entry.id}
                  onRemove={() => void (entry.kind === 'recipe'
                    ? onRemoveRecipe(entry.id)
                    : onRemoveBeverage(entry.id))}
                />
              ))}
            </section>
          );
        })}
      </ListPanel>
    </div>
  );
}

function FavoriteManagementRow({
  entry,
  busy,
  currentBusy,
  onRemove,
}: {
  entry: FavoriteManagementEntry;
  busy: boolean;
  currentBusy: boolean;
  onRemove: () => void;
}) {
  const detail = entry.kind === 'recipe'
    ? `厨具 ${entry.cookerName} · 基础 ${entry.baseIngredientNames.join('、') || (entry.catalogMissing ? '目录未解析' : '无')} · 加料 ${entry.extraIngredientNames.join('、') || '不加料'}`
    : `价格 ${entry.price ?? '目录未解析'} · 酒水标签 ${entry.beverageTags.join('、') || (entry.catalogMissing ? '目录未解析' : '无')}`;
  const kindLabel = entry.kind === 'recipe' ? '料理' : '酒水';

  return (
    <div
      className="steward-data-row px-3 py-2 text-sm"
      data-favorite-entry-id={entry.id}
      data-favorite-entry-kind={entry.kind}
      data-favorite-catalog-missing={entry.catalogMissing ? 'true' : 'false'}
      data-gamepad-row="true"
      data-gamepad-row-key={`favorite:${entry.kind}:${entry.id}`}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-medium">{entry.itemName}</span>
            <Badge variant="secondary">{kindLabel}</Badge>
            <Badge variant="outline">{entry.orderTag}</Badge>
            {entry.catalogMissing && <Badge variant="outline">目录项缺失</Badge>}
          </div>
          <div className="mt-1 text-xs text-muted-foreground">{detail}</div>
          <div className="mt-1 text-xs text-muted-foreground">
            内容 ID {entry.itemId} · 更新于 {formatFavoriteTimestamp(entry.updatedAtUtc || entry.createdAtUtc)}
          </div>
        </div>
        <Button
          type="button"
          size="xs"
          variant="destructive"
          leftSection={<IconTrash size={14} aria-hidden="true" />}
          disabled={busy}
          aria-label={`取消收藏${kindLabel} ${entry.itemName}`}
          data-gamepad-focus-key={`favorite:${entry.kind}:${entry.id}:remove`}
          onClick={onRemove}
        >
          {currentBusy ? '处理中…' : '取消收藏'}
        </Button>
      </div>
    </div>
  );
}

function formatFavoriteTimestamp(value: string): string {
  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) return '时间未知';
  return new Intl.DateTimeFormat('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    hour12: false,
  }).format(timestamp);
}
