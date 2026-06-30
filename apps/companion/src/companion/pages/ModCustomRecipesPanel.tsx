import { useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import {
  Badge,
  Button,
  Card,
  CardContent,
  EmptyRow,
  EmptyState,
  ListPanel,
  MultiSelectBox,
  SelectBox,
  SwitchField,
} from '@/components/ui-kit';
import {
  compareCustomRecipeEntries,
  normalizeIdList,
} from '@/companion/domain/custom-recipes';
import {
  isOrderableRareFoodTag,
  isUsableRareCustomer,
  mergeRareCustomers,
} from '@/companion/domain/service-recommendations';
import {
  formatIngredientNamesWithQty,
  formatIngredientWithQty,
} from '@/companion/formatters';
import type {
  CustomRecipeData,
  CustomRecipeEntry,
  CustomRecipeUpsertInput,
  RuntimeSets,
} from '@/companion/types';
import { DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';
import {
  buildRecommendationDataIndexes,
  getAllRareCustomers,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';
import type { IngredientCatalogItem, RareCustomerCatalogItem, RecipeCatalogItem } from '@/lib/catalog-types';

const ALL_FOOD_TAG_VALUE = '__all_food_tags__';
const MAX_FOOD_INGREDIENT_COUNT = 5;

interface ModCustomRecipesPanelProps {
  apiToken: string;
  customRecipes: CustomRecipeData;
  customRecipeBusyKey: string;
  customRecipeError: string;
  runtimeSets: RuntimeSets | null;
  runtimeRareCustomers: RareCustomerCatalogItem[];
  data: RecommendationDataSet;
  onUpsertCustomRecipe: (input: CustomRecipeUpsertInput) => Promise<boolean>;
  onRemoveCustomRecipe: (id: string) => Promise<boolean>;
  onToggleCustomRecipe: (id: string, enabled: boolean) => Promise<boolean>;
  onMoveCustomRecipe: (id: string, direction: 'up' | 'down') => Promise<boolean>;
}

interface CustomRecipeFormState {
  editingId: string;
  customerId: string;
  foodTagValue: string;
  foodId: string;
  extraIngredientIds: string[];
  enabled: boolean;
  pinToTop: boolean;
  sortOrder?: number;
}

export function ModCustomRecipesPanel({
  apiToken,
  customRecipes,
  customRecipeBusyKey,
  customRecipeError,
  runtimeSets,
  runtimeRareCustomers,
  data,
  onUpsertCustomRecipe,
  onRemoveCustomRecipe,
  onToggleCustomRecipe,
  onMoveCustomRecipe,
}: ModCustomRecipesPanelProps) {
  const dataIndexes = useMemo(() => buildRecommendationDataIndexes(data), [data]);
  const customers = useMemo(
    () => mergeRareCustomers(
      getAllRareCustomers(data).filter(isUsableRareCustomer),
      runtimeRareCustomers.filter(isUsableRareCustomer),
    ).sort((left, right) => left.name.localeCompare(right.name, 'zh-Hans-CN')),
    [data, runtimeRareCustomers],
  );
  const [form, setForm] = useState<CustomRecipeFormState>(() => createInitialForm(customers[0] ?? null));
  const selectedCustomer = customers.find((customer) => String(customer.id) === form.customerId) ?? customers[0] ?? null;
  const selectedRecipe = dataIndexes.recipeByFoodId.get(Number(form.foodId)) ?? null;
  const baseIngredientIds = useMemo(
    () => buildBaseIngredientIds(selectedRecipe, dataIndexes.ingredientIdByName),
    [dataIndexes.ingredientIdByName, selectedRecipe],
  );
  const extraCapacity = selectedRecipe
    ? Math.max(0, MAX_FOOD_INGREDIENT_COUNT - selectedRecipe.ingredients.length)
    : 0;
  const selectedExtraIds = useMemo(() =>
    normalizeIdList(form.extraIngredientIds.map((value) => Number(value))).slice(0, extraCapacity),
    [extraCapacity, form.extraIngredientIds],
  );
  const selectedExtraValues = useMemo(
    () => selectedExtraIds.map(String),
    [selectedExtraIds],
  );
  const entries = useMemo(
    () => [...customRecipes.recipes].sort(compareCustomRecipeEntries),
    [customRecipes.recipes],
  );
  const recipeOptions = useMemo(
    () => buildRecipeOptions(data.recipes, runtimeSets),
    [data.recipes, runtimeSets],
  );
  const ingredientOptions = useMemo(
    () => buildIngredientOptions(
      data.ingredients,
      runtimeSets,
      dataIndexes.ingredientNameById,
      dataIndexes.ingredientIdByName,
      baseIngredientIds,
      selectedExtraIds,
      extraCapacity,
    ),
    [
      baseIngredientIds,
      data.ingredients,
      dataIndexes.ingredientIdByName,
      dataIndexes.ingredientNameById,
      extraCapacity,
      runtimeSets,
      selectedExtraIds,
    ],
  );
  const foodTagOptions = useMemo(
    () => [
      { value: ALL_FOOD_TAG_VALUE, label: '全部点单料理 Tag' },
      ...(selectedCustomer?.positiveTags ?? [])
        .filter(isOrderableRareFoodTag)
        .map((tag) => ({ value: tag, label: tag })),
    ],
    [selectedCustomer],
  );
  const totalIngredientCount = (selectedRecipe?.ingredients.length ?? 0) + selectedExtraIds.length;
  const formBusy = customRecipeBusyKey === (form.editingId || `new:${selectedCustomer?.id ?? form.customerId}:${form.foodId}`);
  const formError = buildFormError({
    apiToken,
    selectedCustomer,
    selectedRecipe,
    totalIngredientCount,
  });

  const resetForm = () => setForm(createInitialForm(selectedCustomer));
  const saveForm = async () => {
    if (!selectedCustomer || !selectedRecipe || formError) return;
    const ok = await onUpsertCustomRecipe({
      id: form.editingId || undefined,
      customerId: selectedCustomer.id,
      customerName: selectedCustomer.name,
      foodTag: form.foodTagValue === ALL_FOOD_TAG_VALUE ? null : form.foodTagValue,
      foodId: selectedRecipe.id,
      recipeId: selectedRecipe.recipeId,
      recipeName: selectedRecipe.name,
      extraIngredientIds: selectedExtraIds,
      enabled: form.enabled,
      pinToTop: form.pinToTop,
      sortOrder: form.sortOrder,
    });
    if (ok) resetForm();
  };

  if (!runtimeSets) {
    return <EmptyState text="尚未读取到游戏实时数据。自定义推荐料理需要已解锁料理和材料快照。" />;
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="space-y-4 p-4 text-sm">
          <div className={DENSE_TWO_COLUMN_GRID}>
            <LabeledControl label="稀客">
              <SelectBox
                value={selectedCustomer ? String(selectedCustomer.id) : ''}
                options={customers.map((customer) => ({ value: String(customer.id), label: customer.name }))}
                searchable
                disabled={customers.length === 0}
                onValueChange={(value) => setForm((current) => ({
                  ...current,
                  customerId: value,
                  foodTagValue: ALL_FOOD_TAG_VALUE,
                }))}
              />
            </LabeledControl>
            <LabeledControl label="点单料理 Tag">
              <SelectBox
                value={form.foodTagValue}
                options={foodTagOptions}
                searchable
                disabled={!selectedCustomer}
                onValueChange={(value) => setForm((current) => ({ ...current, foodTagValue: value }))}
              />
            </LabeledControl>
            <LabeledControl label="基础料理">
              <SelectBox
                value={form.foodId}
                options={recipeOptions}
                searchable
                disabled={recipeOptions.length === 0}
                onValueChange={(value) => setForm((current) => ({
                  ...current,
                  foodId: value,
                  extraIngredientIds: [],
                }))}
              />
            </LabeledControl>
            <LabeledControl label={`加料材料 (${selectedExtraIds.length}/${extraCapacity})`}>
              <MultiSelectBox
                value={selectedExtraValues}
                options={ingredientOptions}
                disabled={!selectedRecipe || extraCapacity <= 0}
                placeholder={extraCapacity <= 0 ? '该料理已达到 5 个材料上限' : '选择额外材料'}
                onValueChange={(values) => {
                  const nextIds = normalizeIdList(values.map((value) => Number(value))).slice(0, extraCapacity);
                  setForm((current) => ({ ...current, extraIngredientIds: nextIds.map(String) }));
                }}
              />
            </LabeledControl>
          </div>

          <div className="flex flex-wrap items-center gap-4">
            <SwitchField
              label="启用"
              checked={form.enabled}
              onCheckedChange={(enabled) => setForm((current) => ({ ...current, enabled }))}
            />
            <SwitchField
              label="推荐置顶"
              checked={form.pinToTop}
              onCheckedChange={(pinToTop) => setForm((current) => ({ ...current, pinToTop }))}
            />
            <Badge variant="outline">
              材料 {totalIngredientCount}/{MAX_FOOD_INGREDIENT_COUNT}
            </Badge>
          </div>

          <RecipeFormSummary
            recipe={selectedRecipe}
            extraIngredientIds={selectedExtraIds}
            runtimeSets={runtimeSets}
            ingredientNameById={dataIndexes.ingredientNameById}
            ingredientIdByName={dataIndexes.ingredientIdByName}
          />

          {(customRecipeError || formError) && (
            <div className="rounded-md border border-destructive/30 px-3 py-2 text-sm text-destructive">
              {formError || customRecipeError}
            </div>
          )}

          <div className="flex flex-wrap items-center justify-end gap-2">
            {form.editingId && (
              <Button type="button" size="sm" variant="outline" onClick={resetForm}>
                取消编辑
              </Button>
            )}
            <Button type="button" size="sm" disabled={Boolean(formError) || formBusy} onClick={saveForm}>
              {form.editingId ? '保存配方' : '新增配方'}
            </Button>
          </div>
        </CardContent>
      </Card>

      <ListPanel title={`自定义推荐料理 (${entries.length})`}>
        {entries.length === 0 && <EmptyRow text="暂无自定义推荐料理" />}
        <div className="space-y-2">
          {entries.map((entry, index) => (
            <CustomRecipeRow
              key={entry.id}
              entry={entry}
              index={index}
              total={entries.length}
              runtimeSets={runtimeSets}
              dataIndexes={dataIndexes}
              busy={customRecipeBusyKey === entry.id}
              onEdit={() => setForm(entryToForm(entry))}
              onRemove={() => void onRemoveCustomRecipe(entry.id)}
              onToggle={() => void onToggleCustomRecipe(entry.id, !entry.enabled)}
              onTogglePin={() => {
                const recipe = dataIndexes.recipeByFoodId.get(entry.foodId);
                void onUpsertCustomRecipe({
                  id: entry.id,
                  customerId: entry.customerId,
                  customerName: entry.customerName,
                  foodTag: entry.foodTag,
                  foodId: entry.foodId,
                  recipeId: recipe?.recipeId ?? entry.recipeId,
                  recipeName: recipe?.name ?? entry.recipeName,
                  extraIngredientIds: entry.extraIngredientIds,
                  enabled: entry.enabled,
                  pinToTop: !entry.pinToTop,
                  sortOrder: entry.sortOrder,
                });
              }}
              onMove={onMoveCustomRecipe}
            />
          ))}
        </div>
      </ListPanel>
    </div>
  );
}

function LabeledControl({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div>
      <div className="mb-1 text-xs text-muted-foreground">{label}</div>
      {children}
    </div>
  );
}

function RecipeFormSummary({
  recipe,
  extraIngredientIds,
  runtimeSets,
  ingredientNameById,
  ingredientIdByName,
}: {
  recipe: RecipeCatalogItem | null;
  extraIngredientIds: number[];
  runtimeSets: RuntimeSets;
  ingredientNameById: Map<number, string>;
  ingredientIdByName: Map<string, number>;
}) {
  if (!recipe) return <EmptyRow text="请选择基础料理" />;
  const base = formatIngredientNamesWithQty(recipe.ingredients, runtimeSets.ownedIngredientQty, ingredientIdByName) || '无';
  const extras = extraIngredientIds.length === 0
    ? '不加料'
    : extraIngredientIds
      .map((id) => formatIngredientWithQty(ingredientNameById.get(id) ?? `#${id}`, runtimeSets.ownedIngredientQty, ingredientIdByName))
      .join(', ');

  return (
    <div className="rounded-md border border-border px-3 py-2 text-sm text-muted-foreground">
      厨具 {recipe.cooker || '未知'} · 基础 {base} · 加料 {extras}
    </div>
  );
}

function CustomRecipeRow({
  entry,
  index,
  total,
  runtimeSets,
  dataIndexes,
  busy,
  onEdit,
  onRemove,
  onToggle,
  onTogglePin,
  onMove,
}: {
  entry: CustomRecipeEntry;
  index: number;
  total: number;
  runtimeSets: RuntimeSets;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
  busy: boolean;
  onEdit: () => void;
  onRemove: () => void;
  onToggle: () => void;
  onTogglePin: () => void;
  onMove: (id: string, direction: 'up' | 'down') => Promise<boolean>;
}) {
  const recipe = dataIndexes.recipeByFoodId.get(entry.foodId);
  const base = formatIngredientNamesWithQty(
    recipe?.ingredients ?? [],
    runtimeSets.ownedIngredientQty,
    dataIndexes.ingredientIdByName,
  ) || '无';
  const extras = entry.extraIngredientIds.length === 0
    ? '不加料'
    : entry.extraIngredientIds
      .map((id) => formatIngredientWithQty(
        dataIndexes.ingredientNameById.get(id) ?? `#${id}`,
        runtimeSets.ownedIngredientQty,
        dataIndexes.ingredientIdByName,
      ))
      .join(', ');

  return (
    <div className="rounded-md border border-border px-3 py-2 text-sm">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-medium">{entry.customerName || `稀客 #${entry.customerId}`}</span>
            <span className="text-muted-foreground">·</span>
            <span>{recipe?.name ?? (entry.recipeName || `料理 #${entry.foodId}`)}</span>
            <Badge variant={entry.foodTag === null ? 'secondary' : 'outline'}>
              {entry.foodTag === null ? '全部点单' : entry.foodTag}
            </Badge>
            <Badge variant={entry.enabled ? 'secondary' : 'outline'}>
              {entry.enabled ? '启用' : '停用'}
            </Badge>
            {entry.pinToTop && <Badge variant="secondary">置顶</Badge>}
          </div>
          <div className="mt-1 text-xs text-muted-foreground">
            排序 {entry.sortOrder} · 厨具 {recipe?.cooker || '未知'} · 基础 {base} · 加料 {extras}
          </div>
        </div>
        <div className="flex flex-wrap justify-end gap-1.5">
          <Button type="button" size="xs" variant="outline" disabled={busy || index === 0} onClick={() => void onMove(entry.id, 'up')}>
            上移
          </Button>
          <Button type="button" size="xs" variant="outline" disabled={busy || index === total - 1} onClick={() => void onMove(entry.id, 'down')}>
            下移
          </Button>
          <Button type="button" size="xs" variant="outline" disabled={busy} onClick={onTogglePin}>
            {entry.pinToTop ? '取消置顶' : '置顶'}
          </Button>
          <Button type="button" size="xs" variant="outline" disabled={busy} onClick={onToggle}>
            {entry.enabled ? '停用' : '启用'}
          </Button>
          <Button type="button" size="xs" variant="outline" disabled={busy} onClick={onEdit}>
            编辑
          </Button>
          <Button type="button" size="xs" variant="destructive" disabled={busy} onClick={onRemove}>
            删除
          </Button>
        </div>
      </div>
    </div>
  );
}

function createInitialForm(customer: RareCustomerCatalogItem | null): CustomRecipeFormState {
  return {
    editingId: '',
    customerId: customer ? String(customer.id) : '',
    foodTagValue: ALL_FOOD_TAG_VALUE,
    foodId: '',
    extraIngredientIds: [],
    enabled: true,
    pinToTop: true,
  };
}

function entryToForm(entry: CustomRecipeEntry): CustomRecipeFormState {
  return {
    editingId: entry.id,
    customerId: String(entry.customerId),
    foodTagValue: entry.foodTag ?? ALL_FOOD_TAG_VALUE,
    foodId: String(entry.foodId),
    extraIngredientIds: entry.extraIngredientIds.map(String),
    enabled: entry.enabled,
    pinToTop: entry.pinToTop,
    sortOrder: entry.sortOrder,
  };
}

function buildRecipeOptions(recipes: RecipeCatalogItem[], runtimeSets: RuntimeSets | null) {
  return recipes
    .filter((recipe) => runtimeSets?.recipeIds.has(recipe.id))
    .sort((left, right) => left.name.localeCompare(right.name, 'zh-Hans-CN'))
    .map((recipe) => ({
      value: String(recipe.id),
      label: `${recipe.name} (${recipe.ingredients.length}/${MAX_FOOD_INGREDIENT_COUNT})`,
      disabled: recipe.ingredients.length > MAX_FOOD_INGREDIENT_COUNT,
    }));
}

function buildIngredientOptions(
  ingredients: IngredientCatalogItem[],
  runtimeSets: RuntimeSets | null,
  ingredientNameById: Map<number, string>,
  ingredientIdByName: Map<string, number>,
  baseIngredientIds: Set<number>,
  selectedExtraIds: number[],
  extraCapacity: number,
) {
  const selected = new Set(selectedExtraIds);
  return ingredients
    .filter((ingredient) => runtimeSets?.ingredientIds.has(ingredient.id))
    .filter((ingredient) => !baseIngredientIds.has(ingredient.id))
    .sort((left, right) => left.name.localeCompare(right.name, 'zh-Hans-CN'))
    .map((ingredient) => ({
      value: String(ingredient.id),
      label: formatIngredientWithQty(
        ingredientNameById.get(ingredient.id) ?? ingredient.name,
        runtimeSets?.ownedIngredientQty ?? {},
        ingredientIdByName,
      ),
      disabled: selectedExtraIds.length >= extraCapacity && !selected.has(ingredient.id),
    }));
}

function buildBaseIngredientIds(
  recipe: RecipeCatalogItem | null,
  ingredientIdByName: Map<string, number>,
): Set<number> {
  return new Set((recipe?.ingredients ?? [])
    .map((name) => ingredientIdByName.get(name) ?? -1)
    .filter((id) => id >= 0));
}

function buildFormError({
  apiToken,
  selectedCustomer,
  selectedRecipe,
  totalIngredientCount,
}: {
  apiToken: string;
  selectedCustomer: RareCustomerCatalogItem | null;
  selectedRecipe: RecipeCatalogItem | null;
  totalIngredientCount: number;
}): string {
  if (!apiToken) return '未收到本地 API Token，无法保存自定义推荐料理。';
  if (!selectedCustomer) return '请选择稀客。';
  if (!selectedRecipe) return '请选择基础料理。';
  if (selectedRecipe.ingredients.length > MAX_FOOD_INGREDIENT_COUNT) return '基础料理材料数量超过游戏上限。';
  if (totalIngredientCount > MAX_FOOD_INGREDIENT_COUNT) return '料理总材料数不能超过 5 个。';
  return '';
}
