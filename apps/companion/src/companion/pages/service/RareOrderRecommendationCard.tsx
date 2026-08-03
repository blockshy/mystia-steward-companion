import { Badge, EmptyRow } from '@/components/ui-kit';
import {
  beverageFavoriteKey,
  findBeverageFavorite,
  findRecipeFavorite,
  recipeFavoriteKey,
} from '@/companion/domain/favorites';
import { formatDesk } from '@/companion/formatters';
import { useEffectiveCustomRecipesDisclosure } from '@/companion/hooks/useEffectiveCustomRecipesDisclosure';
import { normalizeFocusRecommendationLimit } from '@/companion/preferences';
import {
  BeverageRecommendationRow,
  EffectiveCustomRecipesDetails,
  EffectiveCustomRecipesTrigger,
  RecipeRecommendationRow,
} from '@/companion/pages/shared';
import {
  DENSE_TWO_COLUMN_GRID,
  DENSE_TWO_COLUMN_GRID_TIGHT,
  MAX_RECOMMENDATION_ROWS,
} from '@/companion/pages/shared-constants';
import { ServiceOrderCardFrame } from '@/companion/pages/service/ServiceOrderPresentation';
import type {
  CustomRecipeData,
  FavoriteData,
  OrderRecommendation,
  RuntimeSets,
  ToggleBeverageFavorite,
  ToggleRecipeFavorite,
} from '@/companion/types';
import type { buildRecommendationDataIndexes } from '@/lib/recommendation-data';
import type { RecommendationBudgetResult } from '@/recommendation-engine';

export function RareOrderRecommendationCard({
  item,
  runtimeSets,
  dataIndexes,
  favorites,
  favoriteBusyKey,
  compact = false,
  recipeLimit = MAX_RECOMMENDATION_ROWS,
  beverageLimit = MAX_RECOMMENDATION_ROWS,
  showDebugDetails = false,
  customRecipes,
  gamepadOccurrenceKey,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
}: {
  item: OrderRecommendation;
  runtimeSets: RuntimeSets | null;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  gamepadOccurrenceKey: string;
  favoriteBusyKey: string;
  compact?: boolean;
  recipeLimit?: number;
  beverageLimit?: number;
  showDebugDetails?: boolean;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
}) {
  const visibleRecipes = item.recipes.slice(0, normalizeFocusRecommendationLimit(recipeLimit));
  const visibleBeverages = item.beverages.slice(0, normalizeFocusRecommendationLimit(beverageLimit));
  const targetCookerName = visibleRecipes[0]?.recipe.cooker ?? '';
  const customRecipeDisclosure = useEffectiveCustomRecipesDisclosure(
    item.customer.id,
    item.order.foodTag,
    customRecipes,
  );
  const automationBlockReason = item.order.automationAllowed === false
    ? item.order.automationBlockReason
    : '';
  const hasMessage = item.blockedMessages.length > 0 || Boolean(automationBlockReason);

  return (
    <ServiceOrderCardFrame
      compact={compact}
      title={`${item.customer.name} · 桌 ${formatDesk(item.order.deskCode)}`}
      badges={(
        <>
          <Badge variant="outline">料理 {item.order.foodTag || '无'}</Badge>
          <Badge variant="outline">酒水 {item.order.beverageTag || '无'}</Badge>
          {item.order.specialBusinessRoleLabel && (
            <Badge variant="secondary">{item.order.specialBusinessRoleLabel}</Badge>
          )}
          {item.order.automationAllowed === false && <Badge variant="outline">暂不可自动处理</Badge>}
          {item.order.isFreeOrder && <Badge variant="secondary">免费订单</Badge>}
          {targetCookerName && <Badge className="steward-tag-extra">目标厨具 {targetCookerName}</Badge>}
          {item.budget && <BudgetBadge budget={item.budget} />}
          {showDebugDetails && <Badge variant="secondary">{item.order.source}</Badge>}
        </>
      )}
      message={hasMessage
        ? (
            <>
              {item.blockedMessages.length > 0 && <div>{item.blockedMessages.join('；')}</div>}
              {automationBlockReason && <div>{automationBlockReason}</div>}
            </>
          )
        : undefined}
    >
      <div className={compact ? `mt-2 ${DENSE_TWO_COLUMN_GRID_TIGHT}` : `mt-3 ${DENSE_TWO_COLUMN_GRID}`}>
        <div>
          <div
            className={compact
              ? 'mb-1 flex min-w-0 items-center justify-between gap-2'
              : 'mb-2 flex min-w-0 items-center justify-between gap-2'}
            data-effective-custom-recipes-header="true"
          >
            <h3 className={compact ? 'shrink-0 text-xs font-semibold' : 'shrink-0 text-sm font-semibold'}>
              推荐料理
            </h3>
            {customRecipeDisclosure.available && (
              <EffectiveCustomRecipesTrigger
                open={customRecipeDisclosure.open}
                count={customRecipeDisclosure.entries.length}
                gamepadFocusKey={`${gamepadOccurrenceKey}:custom-recipes:toggle`}
                gamepadConfirmFocusKey={`${gamepadOccurrenceKey}:custom-recipes`}
                onToggle={customRecipeDisclosure.toggle}
              />
            )}
          </div>
          <EffectiveCustomRecipesDetails
            open={customRecipeDisclosure.open}
            entries={customRecipeDisclosure.entries}
            customer={item.customer}
            runtimeSets={runtimeSets}
            dataIndexes={dataIndexes}
            compact={compact}
            gamepadScrollKey={`${gamepadOccurrenceKey}:custom-recipes`}
          />
          {visibleRecipes.length === 0 && <EmptyRow text="暂无可推荐料理" />}
          <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
            {visibleRecipes.map((recipe, index) => (
              <RecipeRecommendationRow
                key={`${recipe.recipe.id}-${index}`}
                recipe={recipe}
                index={index}
                ownedIngredientQty={runtimeSets?.ownedIngredientQty ?? {}}
                ingredientIdByName={dataIndexes.ingredientIdByName}
                favorite={findRecipeFavorite(favorites, item.customer.id, item.order.foodTag, recipe)}
                favoriteKey={recipeFavoriteKey(item.customer.id, item.order.foodTag, recipe)}
                favoriteBusyKey={favoriteBusyKey}
                compact={compact}
                gamepadOccurrenceKey={gamepadOccurrenceKey}
                onToggleFavorite={() => onToggleRecipeFavorite(item.customer, item.order.foodTag, recipe)}
              />
            ))}
          </div>
        </div>

        <div>
          <h3 className={compact ? 'mb-1 text-xs font-semibold' : 'mb-2 text-sm font-semibold'}>推荐酒水</h3>
          {visibleBeverages.length === 0 && <EmptyRow text="暂无可推荐酒水" />}
          <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
            {visibleBeverages.map((beverage, index) => (
              <BeverageRecommendationRow
                key={beverage.beverage.id}
                beverage={beverage}
                index={index}
                ownedBeverageQty={runtimeSets?.ownedBeverageQty ?? {}}
                favorite={findBeverageFavorite(favorites, item.customer.id, item.order.beverageTag, beverage)}
                favoriteKey={beverageFavoriteKey(item.customer.id, item.order.beverageTag, beverage)}
                favoriteBusyKey={favoriteBusyKey}
                compact={compact}
                gamepadOccurrenceKey={gamepadOccurrenceKey}
                onToggleFavorite={() => onToggleBeverageFavorite(item.customer, item.order.beverageTag, beverage)}
              />
            ))}
          </div>
        </div>
      </div>
    </ServiceOrderCardFrame>
  );
}

function BudgetBadge({ budget }: { budget: RecommendationBudgetResult }) {
  if (budget.policy === 'ignore') {
    return <Badge variant="outline">预估 {budget.estimatedPrice}</Badge>;
  }
  if (budget.remainingBudget === null) {
    return <Badge variant="outline">预估 {budget.estimatedPrice} · 预算未知</Badge>;
  }
  if (budget.overBudget > 0) {
    return <Badge variant="destructive">预估 {budget.estimatedPrice} · 超 {budget.overBudget}</Badge>;
  }
  return <Badge variant="secondary">预估 {budget.estimatedPrice} / 预算 {budget.remainingBudget}</Badge>;
}
