import { useMemo } from 'react';
import { TagPillGroup } from '@/components/recommendation/TagPillGroup';
import { EmptyRow, EmptyState, ListPanel } from '@/components/ui-kit';
import { usePageRecommendations } from '@/companion/hooks/usePageRecommendations';
import type { RecommendationStateSnapshot, RuntimeSets } from '@/companion/types';
import { NormalBeverageRow, NormalRecipeRow, PlaceToolbar, RuntimeUnavailable } from '@/companion/pages/shared';
import { DENSE_ITEM_GRID, DENSE_TWO_COLUMN_GRID, RECOMMENDATION_SCROLL_AREA } from '@/companion/pages/shared-constants';
import { buildRecommendationDataIndexes, type RecommendationDataSet } from '@/lib/recommendation-data';
import type { PlaceName } from '@/lib/catalog-types';
import { getNormalCustomersByPlace } from '@/recommendation-engine';

export function ModNormalPanel({
  runtime,
  runtimeSets,
  businessError,
  selectedPlace,
  detectedPlace,
  data,
  active,
  onPlaceChange,
  onFollowDetectedPlace,
}: {
  runtime: RecommendationStateSnapshot | null;
  runtimeSets: RuntimeSets | null;
  businessError: string | null;
  selectedPlace: PlaceName | null;
  detectedPlace: PlaceName | null;
  data: RecommendationDataSet;
  active: boolean;
  onPlaceChange: (place: PlaceName) => void;
  onFollowDetectedPlace: () => void;
}) {
  const dataIndexes = useMemo(() => buildRecommendationDataIndexes(data), [data]);
  const customers = useMemo(
    () => (selectedPlace ? getNormalCustomersByPlace(data, selectedPlace) : []),
    [data, selectedPlace],
  );
  const recommendationPayload = useMemo(
    () => (active && runtime && selectedPlace
      ? {
        kind: 'normal' as const,
        runtime,
        selectedPlace,
        data,
      }
      : null),
    [active, data, runtime, selectedPlace],
  );
  const pageRecommendations = usePageRecommendations(recommendationPayload);
  const normalResult = pageRecommendations.result?.kind === 'normal'
    ? pageRecommendations.result
    : null;
  const recipes = normalResult?.recipes ?? [];
  const beverages = normalResult?.beverages ?? [];
  const recipeEmptyText = pageRecommendations.error
    || (pageRecommendations.pending && recipes.length === 0 ? '推荐计算中' : '暂无可推荐料理');
  const beverageEmptyText = pageRecommendations.error
    || (pageRecommendations.pending && beverages.length === 0 ? '推荐计算中' : '暂无可推荐酒水');

  if (!runtime) return <RuntimeUnavailable />;
  // 首次请求尚未建立展示缓存时，也必须报告业务读取错误，不能伪装成游戏运行时未就绪。
  if (!runtimeSets) return businessError
    ? <EmptyState text={`业务数据读取失败：${businessError}`} /> : <RuntimeUnavailable />;

  return (
    <div className="space-y-4">
      <PlaceToolbar
        selectedPlace={selectedPlace}
        detectedPlace={detectedPlace}
        onPlaceChange={onPlaceChange}
        onFollowDetectedPlace={onFollowDetectedPlace}
      />

      {!selectedPlace && <EmptyState text="请选择地区后查看普客推荐" />}

      {normalResult && (pageRecommendations.pending || pageRecommendations.error) && (
        <p role="status" className="text-sm text-muted-foreground">
          {pageRecommendations.error ? `推荐更新失败：${pageRecommendations.error}；当前显示上次结果。`
            : '推荐更新中，当前显示上次结果。'}
        </p>
      )}

      {selectedPlace && (
        <div className={DENSE_TWO_COLUMN_GRID}>
          <ListPanel
            title={`料理推荐 (${recipes.length})`}
            contentClassName={RECOMMENDATION_SCROLL_AREA}
            gamepadScrollKey={`normal:${selectedPlace}:recipes`}
            gamepadScrollLabel={`${selectedPlace}普客料理推荐`}
          >
            {recipes.length === 0 && <EmptyRow text={recipeEmptyText} />}
            <div className="space-y-2">
              {recipes.map((recipe, index) => (
                <NormalRecipeRow
                  key={recipe.recipe.id}
                  recipe={recipe}
                  index={index}
                  ownedIngredientQty={runtimeSets.ownedIngredientQty}
                  ingredientIdByName={dataIndexes.ingredientIdByName}
                />
              ))}
            </div>
          </ListPanel>

          <ListPanel
            title={`酒水推荐 (${beverages.length})`}
            contentClassName={RECOMMENDATION_SCROLL_AREA}
            gamepadScrollKey={`normal:${selectedPlace}:beverages`}
            gamepadScrollLabel={`${selectedPlace}普客酒水推荐`}
          >
            {beverages.length === 0 && <EmptyRow text={beverageEmptyText} />}
            <div className="space-y-2">
              {beverages.map((beverage, index) => (
                <NormalBeverageRow
                  key={beverage.beverage.id}
                  beverage={beverage}
                  index={index}
                  ownedBeverageQty={runtimeSets.ownedBeverageQty}
                />
              ))}
            </div>
          </ListPanel>
        </div>
      )}

      {selectedPlace && (
        <ListPanel title={`地区普客 (${customers.length})`} contentClassName="min-h-[8rem]">
          <div className={DENSE_ITEM_GRID}>
            {customers.map((customer) => (
              <div key={customer.id} className="steward-data-row p-2 text-sm">
                <div className="font-medium">{customer.name}</div>
                <div className="mt-1 space-y-1">
                  <TagPillGroup tags={customer.positiveTags} tone="positive" />
                  <TagPillGroup tags={customer.beverageTags} />
                </div>
              </div>
            ))}
          </div>
        </ListPanel>
      )}
    </div>
  );
}
