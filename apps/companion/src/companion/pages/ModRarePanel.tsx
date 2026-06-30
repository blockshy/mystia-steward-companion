import { useEffect, useMemo } from 'react';
import { Card, CardContent, EmptyRow, EmptyState, ListPanel, SelectBox } from '@/components/ui-kit';
import {
  buildCustomFoodCandidates,
  mergeCustomFoodCandidates,
} from '@/companion/domain/custom-recipes';
import {
  beverageFavoriteKey,
  findBeverageFavorite,
  findRecipeFavorite,
  recipeFavoriteKey,
} from '@/companion/domain/favorites';
import {
  buildRecommendationPlanSortContext,
  buildRecommendationRuntimeContext,
  deriveBeverageRowsFromCandidates,
  deriveRecipeRowsFromCandidates,
  isOrderableRareFoodTag,
  isSelectableRareCustomer,
  mergeRareCustomers,
} from '@/companion/domain/service-recommendations';
import type { CompanionPreferences } from '@/companion/preferences';
import type { CustomRecipeData, FavoriteData, RecommendationStateSnapshot, RuntimeSets, ToggleBeverageFavorite, ToggleRecipeFavorite } from '@/companion/types';
import { BeverageRecommendationRow, EffectiveCustomRecipesViewer, PlaceToolbar, RecipeRecommendationRow, RuntimeUnavailable } from '@/companion/pages/shared';
import { DENSE_THREE_COLUMN_GRID, DENSE_TWO_COLUMN_GRID, MAX_RECOMMENDATION_ROWS, RECOMMENDATION_SCROLL_AREA } from '@/companion/pages/shared-constants';
import { buildRecommendationDataIndexes, getRareCustomersByPlace, type RecommendationDataSet } from '@/lib/recommendation-data';
import type { RareCustomerCatalogItem, PlaceName } from '@/lib/catalog-types';
import { buildRareBeverageCandidates, buildRareFoodCandidates } from '@/recommendation-engine';

export function ModRarePanel({
  runtime,
  runtimeSets,
  runtimeRareCustomers,
  selectedPlace,
  detectedPlace,
  data,
  rareCustomerId,
  requiredFoodTag,
  requiredBeverageTag,
  favorites,
  customRecipes,
  favoriteBusyKey,
  favoriteError,
  preferences,
  onPlaceChange,
  onFollowDetectedPlace,
  onRareCustomerChange,
  onFoodTagChange,
  onBeverageTagChange,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
}: {
  runtime: RecommendationStateSnapshot | null;
  runtimeSets: RuntimeSets | null;
  runtimeRareCustomers: RareCustomerCatalogItem[];
  selectedPlace: PlaceName | null;
  detectedPlace: PlaceName | null;
  data: RecommendationDataSet;
  rareCustomerId: number | null;
  requiredFoodTag: string;
  requiredBeverageTag: string;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
  favoriteBusyKey: string;
  favoriteError: string;
  preferences: CompanionPreferences;
  onPlaceChange: (place: PlaceName) => void;
  onFollowDetectedPlace: () => void;
  onRareCustomerChange: (customerId: number | null) => void;
  onFoodTagChange: (tag: string) => void;
  onBeverageTagChange: (tag: string) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
}) {
  const dataIndexes = useMemo(() => buildRecommendationDataIndexes(data), [data]);
  const customers = useMemo(() => {
    if (!selectedPlace) return [];
    return mergeRareCustomers(
      getRareCustomersByPlace(selectedPlace, data).filter((customer) =>
        isSelectableRareCustomer(customer),
      ),
      runtimeRareCustomers.filter((customer) => (
        customer.places.includes(selectedPlace) && isSelectableRareCustomer(customer)
      )),
    );
  }, [data, runtimeRareCustomers, selectedPlace]);
  const selectedCustomer = useMemo(
    () => customers.find((customer) => customer.id === rareCustomerId) ?? customers[0] ?? null,
    [customers, rareCustomerId],
  );
  const selectedFoodTags = useMemo(
    () => selectedCustomer?.positiveTags.filter(isOrderableRareFoodTag) ?? [],
    [selectedCustomer],
  );
  const selectedBeverageTags = useMemo(
    () => selectedCustomer?.beverageTags ?? [],
    [selectedCustomer],
  );
  const foodTag = requiredFoodTag && selectedFoodTags.includes(requiredFoodTag)
    ? requiredFoodTag
    : selectedFoodTags[0] ?? '';
  const beverageTag = requiredBeverageTag && selectedBeverageTags.includes(requiredBeverageTag)
    ? requiredBeverageTag
    : selectedBeverageTags[0] ?? '';
  const sortContext = useMemo(() => {
    if (!selectedCustomer || !foodTag || !beverageTag) return null;
    return buildRecommendationPlanSortContext(
      favorites,
      selectedCustomer.id,
      foodTag,
      beverageTag,
      null,
      preferences,
    );
  }, [beverageTag, favorites, foodTag, preferences, selectedCustomer]);

  useEffect(() => {
    if (!selectedCustomer) {
      if (rareCustomerId !== null) onRareCustomerChange(null);
      return;
    }
    if (rareCustomerId !== selectedCustomer.id) onRareCustomerChange(selectedCustomer.id);
    if (requiredFoodTag !== foodTag) onFoodTagChange(foodTag);
    if (requiredBeverageTag !== beverageTag) onBeverageTagChange(beverageTag);
  }, [
    beverageTag,
    foodTag,
    onBeverageTagChange,
    onFoodTagChange,
    onRareCustomerChange,
    rareCustomerId,
    requiredBeverageTag,
    requiredFoodTag,
    selectedCustomer,
  ]);

  const candidateContext = useMemo(() => {
    if (!runtime || !runtimeSets) return null;
    return buildRecommendationRuntimeContext(runtime, runtimeSets, preferences, data);
  }, [data, preferences, runtime, runtimeSets]);

  const demand = useMemo(() => {
    if (!selectedCustomer || !foodTag || !beverageTag) return null;
    return {
      type: 'rare-tag-order' as const,
      customer: selectedCustomer,
      requiredFoodTag: foodTag,
      requiredBeverageTag: beverageTag,
    };
  }, [beverageTag, foodTag, selectedCustomer]);

  const foodCandidates = useMemo(() => {
    if (!candidateContext || !demand) return [];
    return buildRareFoodCandidates(data, demand, candidateContext);
  }, [candidateContext, data, demand]);

  const combinedFoodCandidates = useMemo(() => {
    if (!candidateContext || !demand || !selectedCustomer) return foodCandidates;
    const customFoodCandidates = buildCustomFoodCandidates({
      customRecipes,
      data,
      customer: selectedCustomer,
      requiredFoodTag: foodTag,
      requiredBeverageTag: beverageTag,
      context: candidateContext,
    });
    return mergeCustomFoodCandidates(foodCandidates, customFoodCandidates);
  }, [
    beverageTag,
    candidateContext,
    customRecipes,
    data,
    demand,
    foodCandidates,
    foodTag,
    selectedCustomer,
  ]);

  const beverageCandidates = useMemo(() => {
    if (!candidateContext || !demand) return [];
    return buildRareBeverageCandidates(data, demand, candidateContext);
  }, [candidateContext, data, demand]);

  const recipes = useMemo(() => {
    if (!candidateContext || !selectedCustomer || !foodTag || !sortContext) return [];
    return deriveRecipeRowsFromCandidates(combinedFoodCandidates, beverageCandidates, {
      variantLimitPerBase: preferences.recipeVariantLimitPerBase,
      limit: MAX_RECOMMENDATION_ROWS,
      budget: candidateContext.budget,
      budgetPolicy: candidateContext.budgetPolicy,
      sortProfile: preferences.recommendationSortProfile,
      sortContext,
    });
  }, [
    beverageCandidates,
    candidateContext,
    combinedFoodCandidates,
    foodTag,
    preferences.recipeVariantLimitPerBase,
    preferences.recommendationSortProfile,
    selectedCustomer,
    sortContext,
  ]);

  const beverages = useMemo(() => {
    if (!candidateContext || !selectedCustomer || !beverageTag || !sortContext) return [];
    return deriveBeverageRowsFromCandidates(beverageCandidates, combinedFoodCandidates, {
      limit: MAX_RECOMMENDATION_ROWS,
      budget: candidateContext.budget,
      budgetPolicy: candidateContext.budgetPolicy,
      sortProfile: preferences.recommendationSortProfile,
      sortContext,
    });
  }, [
    beverageCandidates,
    beverageTag,
    candidateContext,
    combinedFoodCandidates,
    preferences.recommendationSortProfile,
    selectedCustomer,
    sortContext,
  ]);

  if (!runtime || !runtimeSets) return <RuntimeUnavailable />;

  return (
    <div className="space-y-4">
      <PlaceToolbar
        selectedPlace={selectedPlace}
        detectedPlace={detectedPlace}
        onPlaceChange={onPlaceChange}
        onFollowDetectedPlace={onFollowDetectedPlace}
      />

      {!selectedPlace && <EmptyState text="请选择地区后查看稀客推荐" />}

      {selectedPlace && customers.length === 0 && <EmptyState text="该地区没有稀客" />}

      {selectedPlace && selectedCustomer && (
        <>
          <Card>
            <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
              <div>
                <div className="mb-1 text-xs text-muted-foreground">稀客</div>
                <SelectBox
                  value={String(selectedCustomer.id)}
                  aria-label="稀客"
                  searchable
                  className="w-full"
                  options={customers.map((customer) => ({ value: String(customer.id), label: customer.name }))}
                  onValueChange={(value) => onRareCustomerChange(Number(value))}
                />
              </div>

              <div>
                <div className="mb-1 text-xs text-muted-foreground">点单料理 Tag</div>
                <SelectBox
                  value={foodTag}
                  aria-label="点单料理 Tag"
                  searchable
                  className="w-full"
                  options={selectedFoodTags.map((tag) => ({ value: tag, label: tag }))}
                  onValueChange={onFoodTagChange}
                />
              </div>

              <div>
                <div className="mb-1 text-xs text-muted-foreground">点单酒水 Tag</div>
                <SelectBox
                  value={beverageTag}
                  aria-label="点单酒水 Tag"
                  searchable
                  className="w-full"
                  options={selectedBeverageTags.map((tag) => ({ value: tag, label: tag }))}
                  onValueChange={onBeverageTagChange}
                />
              </div>
            </CardContent>
          </Card>
          {favoriteError && (
            <div className="rounded-md border border-destructive/30 px-3 py-2 text-sm text-destructive">
              {favoriteError}
            </div>
          )}

          <div className={DENSE_TWO_COLUMN_GRID}>
            <ListPanel title={`料理推荐 (${recipes.length})`} contentClassName={RECOMMENDATION_SCROLL_AREA}>
              {recipes.length === 0 && <EmptyRow text="暂无可推荐料理" />}
              <EffectiveCustomRecipesViewer
                customer={selectedCustomer}
                foodTag={foodTag}
                customRecipes={customRecipes}
                runtimeSets={runtimeSets}
                dataIndexes={dataIndexes}
              />
              <div className="space-y-2">
                {recipes.map((recipe, index) => (
                  <RecipeRecommendationRow
                    key={`${recipe.recipe.id}-${index}`}
                    recipe={recipe}
                    index={index}
                    ownedIngredientQty={runtimeSets.ownedIngredientQty}
                    ingredientIdByName={dataIndexes.ingredientIdByName}
                    favorite={findRecipeFavorite(favorites, selectedCustomer.id, foodTag, recipe)}
                    favoriteKey={recipeFavoriteKey(selectedCustomer.id, foodTag, recipe)}
                    favoriteBusyKey={favoriteBusyKey}
                    onToggleFavorite={() => onToggleRecipeFavorite(selectedCustomer, foodTag, recipe)}
                  />
                ))}
              </div>
            </ListPanel>

            <ListPanel title={`酒水推荐 (${beverages.length})`} contentClassName={RECOMMENDATION_SCROLL_AREA}>
              {beverages.length === 0 && <EmptyRow text="暂无可推荐酒水" />}
              <div className="space-y-2">
                {beverages.map((beverage, index) => (
                  <BeverageRecommendationRow
                    key={beverage.beverage.id}
                    beverage={beverage}
                    index={index}
                    ownedBeverageQty={runtimeSets.ownedBeverageQty}
                    favorite={findBeverageFavorite(favorites, selectedCustomer.id, beverageTag, beverage)}
                    favoriteKey={beverageFavoriteKey(selectedCustomer.id, beverageTag, beverage)}
                    favoriteBusyKey={favoriteBusyKey}
                    onToggleFavorite={() => onToggleBeverageFavorite(selectedCustomer, beverageTag, beverage)}
                  />
                ))}
              </div>
            </ListPanel>
          </div>
        </>
      )}
    </div>
  );
}
