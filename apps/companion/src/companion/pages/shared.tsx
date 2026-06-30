import { useMemo, useState } from 'react';
import { PlaceSelect } from '@/components/controls/PlaceSelect';
import { RecommendationItem, RecommendationMetaBadge, RecommendationTagPills } from '@/components/RecommendationItem';
import { CustomerCoverageBadges } from '@/components/recommendation/CustomerCoverageBadges';
import { TagPill, TagPillGroup } from '@/components/recommendation/TagPillGroup';
import { Badge, Button, EmptyRow, EmptyState, NumberInput, SegmentedControl, SliderField, SwitchField } from '@/components/ui-kit';
import { getEffectiveCustomRecipeEntries } from '@/companion/domain/custom-recipes';
import { findBeverageFavorite, findRecipeFavorite, beverageFavoriteKey, recipeFavoriteKey } from '@/companion/domain/favorites';
import { INVENTORY_SORT_OPTIONS, type InventorySortMode } from '@/companion/domain/inventory-sorting';
import { formatDesk, formatIngredientNamesWithQty, formatIngredientWithQty, formatQtySuffix } from '@/companion/formatters';
import {
  MAX_FOCUS_RECOMMENDATION_ROWS,
  MAX_FOCUS_SWITCH_COOLDOWN_MS,
  MIN_BACKGROUND_OPACITY,
  MIN_CONTENT_OPACITY,
  MIN_FOCUS_SWITCH_COOLDOWN_MS,
  clampInteger,
  normalizeBackgroundOpacity,
  normalizeContentOpacity,
  normalizeFocusRecommendationLimit,
  normalizeFocusSwitchCooldownMs,
} from '@/companion/preferences';
import type {
  FavoriteBeverageEntry,
  CustomRecipeData,
  FavoriteData,
  FavoriteRecipeEntry,
  OrderRecommendation,
  RuntimeSets,
  ToggleBeverageFavorite,
  ToggleRecipeFavorite,
} from '@/companion/types';
import type { buildRecommendationDataIndexes } from '@/lib/recommendation-data';
import { ALL_PLACES, type PlaceName, type RareCustomerCatalogItem } from '@/lib/catalog-types';
import type {
  NormalBeverageRecommendation,
  NormalRecipeRecommendation,
  RareBeverageRecommendation,
  RareRecipeRecommendation,
  RecommendationBudgetResult,
} from '@/recommendation-engine';
import {
  AUTOMATION_SWITCH_CELL,
  DENSE_TWO_COLUMN_GRID,
  DENSE_TWO_COLUMN_GRID_TIGHT,
  MAX_RECOMMENDATION_ROWS,
  type LowStockEntry,
} from '@/companion/pages/shared-constants';

export function LowStockColumn({
  title,
  entries,
}: {
  title: string;
  entries: LowStockEntry[];
}) {
  return (
    <div>
      <h3 className="mb-1 text-sm font-medium">{title}</h3>
      {entries.length === 0 && <EmptyRow text="暂无库存数据" />}
      {entries.map((item) => (
        <div key={item.id} className="flex items-center justify-between border-b py-2 text-sm last:border-b-0">
          <span>{item.name}</span>
          <span className="text-muted-foreground">{item.qty}</span>
        </div>
      ))}
    </div>
  );
}

function TagSummary({
  tags,
  cancelledTags,
}: {
  tags: string[];
  cancelledTags: string[];
}) {
  if (tags.length === 0 && cancelledTags.length === 0) return null;

  return (
    <div className="mt-1 flex flex-wrap gap-1">
      <TagPillGroup tags={tags} />
      {cancelledTags.map((tag) => (
        <TagPill key={`cancelled-${tag}`} tone="suppressed">
          已抵消 {tag}
        </TagPill>
      ))}
    </div>
  );
}

export function RuntimeUnavailable() {
  return <EmptyState text="尚未读取到游戏实时数据。请确认游戏已加载存档，且 Mod 本地 API 已连接。" />;
}

export function SwitchControl({
  label,
  checked,
  onCheckedChange,
  disabled,
  title,
}: {
  label: string;
  checked: boolean;
  onCheckedChange: (value: boolean) => void;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <SwitchField label={label} checked={checked} onCheckedChange={onCheckedChange} disabled={disabled} title={title} />
  );
}

export function AutomationSwitchCell({
  label,
  checked,
  onCheckedChange,
  disabled,
  title,
}: {
  label: string;
  checked: boolean;
  onCheckedChange: (value: boolean) => void;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <div className={AUTOMATION_SWITCH_CELL}>
      <SwitchControl label={label} checked={checked} onCheckedChange={onCheckedChange} disabled={disabled} title={title} />
    </div>
  );
}

export function FocusLimitInput({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number;
  onChange: (value: number) => void;
}) {
  return (
    <label className="flex items-center gap-1.5 text-sm">
      <span className="whitespace-nowrap text-muted-foreground">{label}</span>
      <NumberInput
        min={1}
        max={MAX_FOCUS_RECOMMENDATION_ROWS}
        value={value}
        onValueChange={(nextValue) => onChange(normalizeFocusRecommendationLimit(nextValue))}
        className="h-8 w-16"
      />
    </label>
  );
}

export function FocusSwitchCooldownInput({
  value,
  onChange,
}: {
  value: number;
  onChange: (value: number) => void;
}) {
  return (
      <SliderField
          label="切换冷却时间"
          value={value}
          min={MIN_FOCUS_SWITCH_COOLDOWN_MS}
          max={MAX_FOCUS_SWITCH_COOLDOWN_MS}
          step={50}
          valueText={`${value}ms`}
          description={`单位毫秒，范围 ${MIN_FOCUS_SWITCH_COOLDOWN_MS} - ${MAX_FOCUS_SWITCH_COOLDOWN_MS}。调低后切换更快，过低可能重复触发。`}
          onChange={(nextValue) => onChange(normalizeFocusSwitchCooldownMs(nextValue))}
      />
  );
}

export function AutomationSliderField({
  label,
  value,
  min,
  max,
  unit = '',
  onChange,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  unit?: string;
  onChange: (value: number) => void;
}) {
  return (
    <SliderField
      label={label}
      value={value}
      min={min}
      max={max}
      step={1}
      valueText={`${value}${unit}`}
      description={`${min}${unit} - ${max}${unit}`}
      onChange={(nextValue) => onChange(clampInteger(nextValue, min, max, value))}
    />
  );
}

export function BackgroundOpacitySlider({
  value,
  onChange,
}: {
  value: number;
  onChange: (value: number) => void;
}) {
  const percent = Math.round(normalizeBackgroundOpacity(value) * 100);

  return (
    <SliderField
      label="背景透明度"
      value={percent}
      min={Math.round(MIN_BACKGROUND_OPACITY * 100)}
      max={100}
      step={1}
      valueText={`${percent}%`}
      description="调整窗口背景、面板、弹层和滚动条轨道透明度。"
      onChange={(nextPercent) => onChange(normalizeBackgroundOpacity(nextPercent / 100))}
    />
  );
}

export function ContentOpacitySlider({
  value,
  onChange,
}: {
  value: number;
  onChange: (value: number) => void;
}) {
  const percent = Math.round(normalizeContentOpacity(value) * 100);

  return (
    <SliderField
      label="文字透明度"
      value={percent}
      min={Math.round(MIN_CONTENT_OPACITY * 100)}
      max={100}
      step={1}
      valueText={`${percent}%`}
      description="调整普通文字、图标和辅助徽章内容透明度；主操作按钮保持清晰。"
      onChange={(nextPercent) => onChange(normalizeContentOpacity(nextPercent / 100))}
    />
  );
}

export type SettingSegmentedOption<TValue extends string> = {
  value: TValue;
  label: string;
};

export function SettingSegmentedControl<TValue extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: TValue;
  options: SettingSegmentedOption<TValue>[];
  onChange: (value: TValue) => void;
}) {
  return (
    <div className="space-y-2">
      <div className="text-sm font-medium">{label}</div>
      <SegmentedControl
        value={value}
        options={options}
        onValueChange={onChange}
        className="max-w-full"
      />
    </div>
  );
}

export function InventorySortControl({
  value,
  onChange,
  disabled = false,
  ariaLabel = '库存排序',
  className = '',
}: {
  value: InventorySortMode;
  onChange: (value: InventorySortMode) => void;
  disabled?: boolean;
  ariaLabel?: string;
  className?: string;
}) {
  return (
    <SegmentedControl<InventorySortMode>
      value={value}
      options={INVENTORY_SORT_OPTIONS}
      onValueChange={onChange}
      disabled={disabled}
      aria-label={ariaLabel}
      className={`h-7 shrink-0 ${className}`.trim()}
    />
  );
}

export function PlaceToolbar({
  selectedPlace,
  detectedPlace,
  onPlaceChange,
  onFollowDetectedPlace,
}: {
  selectedPlace: PlaceName | null;
  detectedPlace: PlaceName | null;
  onPlaceChange: (place: PlaceName) => void;
  onFollowDetectedPlace: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <PlaceSelect value={selectedPlace} places={ALL_PLACES} onChange={onPlaceChange} />
      {detectedPlace && (
        <Button size="sm" variant="outline" onClick={onFollowDetectedPlace}>
          跟随经营场景: {detectedPlace}
        </Button>
      )}
    </div>
  );
}

export function NormalRecipeRow({
  recipe,
  index,
  ownedIngredientQty,
  ingredientIdByName,
}: {
  recipe: NormalRecipeRecommendation;
  index: number;
  ownedIngredientQty: Record<number, number>;
  ingredientIdByName: Map<string, number>;
}) {
  const baseRecipe = formatIngredientNamesWithQty(
    recipe.recipe.ingredients,
    ownedIngredientQty,
    ingredientIdByName,
  ) || '无';

  return (
    <RecommendationItem
      index={index}
      title={recipe.recipe.name}
      summary={`覆盖 ${recipe.totalCoverage} · 成本 ${recipe.ingredientCost} · 利润 ${recipe.profit} · 价格 ${recipe.recipe.price}`}
      inlineMeta={<RecommendationMetaBadge label="厨具" value={recipe.recipe.cooker || '未知'} tone="cooker" />}
      meta={<RecommendationMetaBadge label="基础配方" value={baseRecipe} tone="base" />}
    >
      <div className="mt-1 flex flex-wrap gap-1">
        {recipe.matchedTags.map((tag) => <TagPill key={tag} tone="match">{tag}</TagPill>)}
      </div>
      <div className="mt-1">
        <CustomerCoverageBadges
          coverage={recipe.customerCoverage}
        />
      </div>
    </RecommendationItem>
  );
}

export function NormalBeverageRow({
  beverage,
  index,
  ownedBeverageQty,
}: {
  beverage: NormalBeverageRecommendation;
  index: number;
  ownedBeverageQty: Record<number, number>;
}) {
  return (
    <RecommendationItem
      index={index}
      title={beverage.beverage.name}
      titleSuffix={formatQtySuffix(ownedBeverageQty[beverage.beverage.id])}
      summary={`覆盖 ${beverage.totalCoverage} · 价格 ${beverage.beverage.price}`}
    >
      <RecommendationTagPills tags={beverage.beverage.tags} matchedTags={beverage.matchedTags} />
      <div className="mt-1">
        <CustomerCoverageBadges
          coverage={beverage.customerCoverage}
        />
      </div>
    </RecommendationItem>
  );
}

export function OrderRecommendationPanel({
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
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
}: {
  item: OrderRecommendation;
  runtimeSets: RuntimeSets | null;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
  favorites: FavoriteData;
  customRecipes: CustomRecipeData;
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

  return (
    <div className={compact ? 'rounded-md border border-border p-2' : 'rounded-md border border-border p-3'}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-sm font-semibold">{item.customer.name} · 桌 {formatDesk(item.order.deskCode)}</div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            <Badge variant="outline">料理 {item.order.foodTag || '无'}</Badge>
            <Badge variant="outline">酒水 {item.order.beverageTag || '无'}</Badge>
            {targetCookerName && (
              <Badge className="steward-tag-extra">
                目标厨具 {targetCookerName}
              </Badge>
            )}
            {item.budget && <BudgetBadge budget={item.budget} />}
            {showDebugDetails && <Badge variant="secondary">{item.order.source}</Badge>}
          </div>
          {item.blockedMessages.length > 0 && (
            <div className="mt-1 text-xs text-muted-foreground">
              {item.blockedMessages.join('；')}
            </div>
          )}
        </div>
      </div>

      <div className={compact ? `mt-2 ${DENSE_TWO_COLUMN_GRID_TIGHT}` : `mt-3 ${DENSE_TWO_COLUMN_GRID}`}>
        <div>
          <h3 className={compact ? 'mb-1 text-xs font-semibold' : 'mb-2 text-sm font-semibold'}>推荐料理</h3>
          {visibleRecipes.length === 0 && <EmptyRow text="暂无可推荐料理" />}
          <EffectiveCustomRecipesViewer
            customer={item.customer}
            foodTag={item.order.foodTag}
            customRecipes={customRecipes}
            runtimeSets={runtimeSets}
            dataIndexes={dataIndexes}
            compact={compact}
          />
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
                onToggleFavorite={() => onToggleBeverageFavorite(item.customer, item.order.beverageTag, beverage)}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

export function EffectiveCustomRecipesViewer({
  customer,
  foodTag,
  customRecipes,
  runtimeSets,
  dataIndexes,
  compact = false,
}: {
  customer: RareCustomerCatalogItem;
  foodTag: string;
  customRecipes: CustomRecipeData;
  runtimeSets: RuntimeSets | null;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
  compact?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const entries = useMemo(
    () => getEffectiveCustomRecipeEntries(customRecipes, customer.id, foodTag),
    [customRecipes, customer.id, foodTag],
  );

  if (!foodTag) return null;

  return (
    <div className={compact ? 'mb-1.5' : 'mb-2'}>
      <Button
        type="button"
        variant="outline"
        size="xs"
        onClick={() => setOpen((value) => !value)}
      >
        {open ? '收起自定义配方' : `查看生效的自定义配方 (${entries.length})`}
      </Button>
      {open && (
        <div className="mt-2 rounded-md border border-border p-2">
          <div className="mt-2 max-h-72 space-y-1.5 overflow-auto pr-1">
            {entries.length === 0 && <EmptyRow text="当前稀客和点单料理 Tag 没有生效的自定义配方" />}
            {entries.map((entry) => {
              const recipe = dataIndexes.recipeByFoodId.get(entry.foodId);
              const extras = entry.extraIngredientIds.length === 0
                ? '不加料'
                : entry.extraIngredientIds
                  .map((id) => formatIngredientWithQty(
                    dataIndexes.ingredientNameById.get(id) ?? `#${id}`,
                    runtimeSets?.ownedIngredientQty ?? {},
                    dataIndexes.ingredientIdByName,
                  ))
                  .join(', ');
              const baseRecipe = formatIngredientNamesWithQty(
                recipe?.ingredients ?? [],
                runtimeSets?.ownedIngredientQty ?? {},
                dataIndexes.ingredientIdByName,
              ) || '无';

              return (
                <div
                  key={entry.id}
                  className="flex flex-wrap items-start justify-between gap-2 rounded-md border border-border px-2 py-2 text-sm"
                >
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <span className="font-medium">{recipe?.name ?? `料理 #${entry.foodId}`}</span>
                      <Badge variant={entry.foodTag === null ? 'secondary' : 'outline'}>
                        {entry.foodTag === null ? '全部点单' : entry.foodTag}
                      </Badge>
                      {entry.pinToTop && <Badge variant="secondary">置顶</Badge>}
                    </div>
                    <div className="mt-1 text-xs text-muted-foreground">
                      排序 {entry.sortOrder} · 厨具 {recipe?.cooker || '未知'} · 基础 {baseRecipe} · 加料 {extras}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        </div>
      )}
    </div>
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

export function RecipeRecommendationRow({
  recipe,
  index,
  ownedIngredientQty,
  ingredientIdByName,
  favorite,
  favoriteKey = '',
  favoriteBusyKey = '',
  compact = false,
  onToggleFavorite,
}: {
  recipe: RareRecipeRecommendation;
  index: number;
  ownedIngredientQty: Record<number, number>;
  ingredientIdByName: Map<string, number>;
  favorite?: FavoriteRecipeEntry | null;
  favoriteKey?: string;
  favoriteBusyKey?: string;
  compact?: boolean;
  onToggleFavorite?: () => void;
}) {
  const totalCost = recipe.baseCost + recipe.extraCost;
  const extras = recipe.extraIngredients.length === 0
    ? '不加料'
    : recipe.extraIngredients
      .map((ingredient) => formatIngredientWithQty(ingredient.name, ownedIngredientQty, ingredientIdByName))
      .join(', ');
  const baseRecipe = formatIngredientNamesWithQty(
    recipe.recipe.ingredients,
    ownedIngredientQty,
    ingredientIdByName,
  ) || '无';
  const busy = favoriteBusyKey === (favorite?.id ?? favoriteKey);

  return (
    <RecommendationItem
      index={index}
      title={recipe.recipe.name}
      badges={(
        <>
          {recipe.missionPriority && (
            <Badge className="steward-meta-cooker">
              任务
            </Badge>
          )}
          {recipe.customRecipe && (
            <Badge variant={recipe.customRecipePinned ? 'secondary' : 'outline'}>
              {recipe.customRecipePinned ? '自定义置顶' : '自定义'}
            </Badge>
          )}
          {recipe.customRecipeScope === 'all' && (
            <Badge variant="outline">全部点单</Badge>
          )}
          <Badge variant={recipe.meetsRequiredFood ? 'secondary' : 'outline'}>
            {recipe.meetsRequiredFood ? '满足点单' : '偏好备选'}
          </Badge>
        </>
      )}
      summary={`加料 ${recipe.extraIngredients.length} 项 · 成本 ${totalCost}`}
      inlineMeta={<RecommendationMetaBadge label="厨具" value={recipe.recipe.cooker || '未知'} tone="cooker" />}
      meta={(
        <>
          <RecommendationMetaBadge label="基础配方" value={baseRecipe} tone="base" />
          <RecommendationMetaBadge label="加料" value={extras} tone="extra" />
        </>
      )}
      compact={compact}
      favorite={onToggleFavorite ? {
        active: Boolean(favorite),
        disabled: busy,
        activeLabel: '取消收藏该料理方案',
        inactiveLabel: '收藏该料理方案',
        focusKey: `recipe-favorite:${favoriteKey}`,
        onToggle: onToggleFavorite,
      } : undefined}
      gamepadRowKey={`recipe:${favoriteKey}`}
    >
      {!compact && <TagSummary tags={recipe.allTags} cancelledTags={recipe.cancelledTags} />}
    </RecommendationItem>
  );
}

export function BeverageRecommendationRow({
  beverage,
  index,
  ownedBeverageQty,
  favorite,
  favoriteKey = '',
  favoriteBusyKey = '',
  compact = false,
  onToggleFavorite,
}: {
  beverage: RareBeverageRecommendation;
  index: number;
  ownedBeverageQty: Record<number, number>;
  favorite?: FavoriteBeverageEntry | null;
  favoriteKey?: string;
  favoriteBusyKey?: string;
  compact?: boolean;
  onToggleFavorite?: () => void;
}) {
  const busy = favoriteBusyKey === (favorite?.id ?? favoriteKey);

  return (
    <RecommendationItem
      index={index}
      title={beverage.beverage.name}
      titleSuffix={formatQtySuffix(ownedBeverageQty[beverage.beverage.id])}
      badges={(
        <Badge variant={beverage.meetsRequiredBev ? 'secondary' : 'outline'}>
          {beverage.meetsRequiredBev ? '满足点单' : '偏好备选'}
        </Badge>
      )}
      summary={`匹配 ${beverage.matchedTags.length} 项 · 价格 ${beverage.beverage.price}`}
      compact={compact}
      favorite={onToggleFavorite ? {
        active: Boolean(favorite),
        disabled: busy,
        activeLabel: '取消收藏该酒水',
        inactiveLabel: '收藏该酒水',
        focusKey: `beverage-favorite:${favoriteKey}`,
        onToggle: onToggleFavorite,
      } : undefined}
      gamepadRowKey={`beverage:${favoriteKey}`}
    >
      {!compact && <RecommendationTagPills tags={beverage.beverage.tags} matchedTags={beverage.matchedTags} />}
    </RecommendationItem>
  );
}
