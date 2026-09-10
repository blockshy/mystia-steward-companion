import { useEffect, useMemo, useRef } from 'react';
import type { Dispatch, SetStateAction } from 'react';
import {
  Badge,
  Button,
  Card,
  CardContent,
  EmptyRow,
  EmptyState,
  ListPanel,
  MultiSelectBox,
  SegmentedControl,
  SelectBox,
  SwitchField,
} from '@/components/ui-kit';
import {
  compareCustomRecipeEntries,
  normalizeIdList,
} from '@/companion/domain/custom-recipes';
import {
  CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE,
  createEmptyCustomRecipeForm,
  type CustomRecipeFormState,
} from '@/companion/custom-recipe-editor';
import {
  isOrderableRareFoodTag,
  isUsableRareCustomer,
} from '@/companion/domain/service-recommendations';
import {
  formatIngredientNamesWithQty,
  formatIngredientWithQty,
} from '@/companion/formatters';
import type {
  CustomRecipeData,
  CustomRecipeEntry,
  CustomRecipeFlagUpdateInput,
  CustomRecipeGroupMode,
  CustomRecipeSelection,
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

const MAX_FOOD_INGREDIENT_COUNT = 5;

interface ModCustomRecipesPanelProps {
  apiToken: string;
  customRecipes: CustomRecipeData;
  customRecipeBusyKey: string;
  customRecipeError: string;
  form: CustomRecipeFormState;
  groupMode: CustomRecipeGroupMode;
  runtimeSets: RuntimeSets | null;
  data: RecommendationDataSet;
  onUpsertCustomRecipe: (input: CustomRecipeUpsertInput) => Promise<boolean>;
  onRemoveCustomRecipe: (id: string) => Promise<boolean>;
  onSetCustomRecipesEnabled: (enabled: boolean) => Promise<boolean>;
  onUpdateCustomRecipeFlags: (input: CustomRecipeFlagUpdateInput) => Promise<boolean>;
  onMoveCustomRecipe: (id: string, direction: 'up' | 'down') => Promise<boolean>;
  onFormChange: Dispatch<SetStateAction<CustomRecipeFormState>>;
  onGroupModeChange: (mode: CustomRecipeGroupMode) => void;
}

interface CustomRecipeGroup {
  key: string;
  label: string;
  selection: CustomRecipeSelection;
  entries: CustomRecipeEntry[];
}

export function ModCustomRecipesPanel({
  apiToken,
  customRecipes,
  customRecipeBusyKey,
  customRecipeError,
  form,
  groupMode,
  runtimeSets,
  data,
  onUpsertCustomRecipe,
  onRemoveCustomRecipe,
  onSetCustomRecipesEnabled,
  onUpdateCustomRecipeFlags,
  onMoveCustomRecipe,
  onFormChange,
  onGroupModeChange,
}: ModCustomRecipesPanelProps) {
  const panelRef = useRef<HTMLDivElement>(null);
  const editorHeadingRef = useRef<HTMLHeadingElement>(null);
  const returnFocusKeyRef = useRef<string | null>(null);
  const dataIndexes = useMemo(() => buildRecommendationDataIndexes(data), [data]);
  const customers = useMemo(
    () => getAllRareCustomers(data)
      .filter(isUsableRareCustomer)
      .sort((left, right) => left.name.localeCompare(right.name, 'zh-Hans-CN')),
    [data],
  );
  const selectedCustomer = form.customerId
    ? customers.find((customer) => String(customer.id) === form.customerId) ?? null
    : customers[0] ?? null;
  const selectedRecipe = form.foodId ? dataIndexes.recipeByFoodId.get(Number(form.foodId)) ?? null : null;
  const baseIngredientIds = useMemo(
    () => buildBaseIngredientIds(selectedRecipe, dataIndexes.ingredientIdByName),
    [dataIndexes.ingredientIdByName, selectedRecipe],
  );
  const extraCapacity = selectedRecipe
    ? Math.max(0, MAX_FOOD_INGREDIENT_COUNT - selectedRecipe.ingredients.length)
    : 0;
  const selectedExtraIds = useMemo(() =>
    normalizeIdList(form.extraIngredientIds.map((value) => Number(value))),
    [form.extraIngredientIds],
  );
  const selectedExtraValues = useMemo(
    () => selectedExtraIds.map(String),
    [selectedExtraIds],
  );
  const entries = useMemo(
    () => [...customRecipes.recipes].sort(compareCustomRecipeEntries),
    [customRecipes.recipes],
  );
  const groups = useMemo(
    () => buildCustomRecipeGroups(entries, groupMode, customers, dataIndexes.recipeByFoodId),
    [customers, dataIndexes.recipeByFoodId, entries, groupMode],
  );
  const recipeOptions = useMemo(
    () => {
      const options = buildRecipeOptions(data.recipes, runtimeSets);
      if (form.foodId && !options.some((option) => option.value === form.foodId)) {
        options.unshift({
          value: form.foodId,
          label: selectedRecipe ? `${selectedRecipe.name}（当前未解锁）` : `目录未识别的料理 #${form.foodId}`,
          disabled: true,
        });
      }
      return options;
    },
    [data.recipes, form.foodId, runtimeSets, selectedRecipe],
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
    () => {
      const options = [
        { value: CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE, label: '全部点单料理标签' },
        ...(selectedCustomer?.positiveTags ?? []).filter(isOrderableRareFoodTag)
          .map((tag) => ({ value: tag, label: tag })),
      ];
      if (!options.some((option) => option.value === form.foodTagValue)) {
        options.unshift({ value: form.foodTagValue, label: `${form.foodTagValue}（当前不适用）` });
      }
      return options;
    },
    [form.foodTagValue, selectedCustomer],
  );
  const totalIngredientCount = (selectedRecipe?.ingredients.length ?? 0) + selectedExtraIds.length;
  const busy = Boolean(customRecipeBusyKey);
  const summary = summarizeEntries(entries);
  const formError = buildFormError({
    apiToken,
    form,
    selectedCustomer,
    selectedRecipe,
    totalIngredientCount,
    selectedExtraIds,
    runtimeSets,
    dataIndexes,
  });

  useEffect(() => {
    if (!form.editingId) return;
    editorHeadingRef.current?.focus();
    editorHeadingRef.current?.scrollIntoView({ block: 'start' });
  }, [form.editingId]);

  const resetForm = () => {
    onFormChange(createInitialForm(selectedCustomer));
    const returnFocusKey = returnFocusKeyRef.current;
    returnFocusKeyRef.current = null;
    window.requestAnimationFrame(() => {
      const target = Array.from(panelRef.current?.querySelectorAll<HTMLElement>('[data-gamepad-focus-key]') ?? [])
        .find((element) => element.dataset.gamepadFocusKey === returnFocusKey);
      const focusTarget = target ?? editorHeadingRef.current;
      focusTarget?.focus();
      focusTarget?.scrollIntoView({ block: 'nearest' });
    });
  };
  const saveForm = async () => {
    if (!selectedCustomer || !selectedRecipe || formError) return;
    const creating = !form.editingId;
    const ok = await onUpsertCustomRecipe({
      id: form.editingId || undefined,
      customerId: selectedCustomer.id,
      customerName: selectedCustomer.name,
      foodTag: form.foodTagValue === CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE ? null : form.foodTagValue,
      foodId: selectedRecipe.id,
      recipeId: selectedRecipe.recipeId,
      recipeName: selectedRecipe.name,
      extraIngredientIds: selectedExtraIds,
      enabled: creating ? form.enabled : undefined,
      pinToTop: creating ? form.pinToTop : undefined,
      sortOrder: form.sortOrder,
    });
    if (ok) resetForm();
  };

  if (!runtimeSets) {
    return <EmptyState text="尚未读取到游戏实时数据。自定义推荐料理需要已解锁料理和当前材料信息。" />;
  }

  return (
    <div ref={panelRef} className="space-y-4">
      <div className="steward-inline-panel space-y-3 px-3 py-3">
        <div className="flex flex-wrap items-center justify-between gap-3">
          <SwitchField
            label="启用自定义推荐料理"
            checked={customRecipes.enabled}
            disabled={busy || !apiToken}
            onCheckedChange={(enabled) => void onSetCustomRecipesEnabled(enabled)}
          />
          <div className="flex flex-wrap items-center gap-2">
            <Badge variant={customRecipes.enabled ? 'secondary' : 'outline'}>
              {customRecipes.enabled ? '功能已启用' : '功能已停用'}
            </Badge>
            <Badge variant="outline">共 {summary.total}</Badge>
            <Badge variant="outline">启用 {summary.enabled}</Badge>
            <Badge variant="outline">置顶 {summary.pinned}</Badge>
          </div>
        </div>
        {customRecipeError && (
          <div className="border border-destructive/30 px-3 py-2 text-sm text-destructive">
            {customRecipeError}
          </div>
        )}
      </div>

      <Card>
        <CardContent className="space-y-4 text-sm">
          <h2 ref={editorHeadingRef} tabIndex={-1} className="font-semibold" data-custom-recipe-editor-heading="true">
            {form.editingId
              ? `编辑配方 · ${selectedCustomer?.name ?? `稀客 #${form.customerId}`} · ${selectedRecipe?.name ?? `料理 #${form.foodId}`} · ${form.foodTagValue === CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE ? '全部点单' : form.foodTagValue}`
              : '新增自定义配方'}
          </h2>
          <div className={DENSE_TWO_COLUMN_GRID}>
              <SelectBox
                label="稀客"
                value={form.customerId || (selectedCustomer ? String(selectedCustomer.id) : '')}
                options={[
                  ...(!selectedCustomer && form.customerId
                    ? [{ value: form.customerId, label: `目录未识别的稀客 #${form.customerId}`, disabled: true }]
                    : []),
                  ...customers.map((customer) => ({ value: String(customer.id), label: customer.name })),
                ]}
                searchable
                disabled={customers.length === 0 || busy}
                onValueChange={(value) => onFormChange((current) => ({
                  ...current,
                  customerId: value,
                  foodTagValue: CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE,
                }))}
              />
              <SelectBox
                label="点单料理标签"
                value={form.foodTagValue}
                options={foodTagOptions}
                searchable
                disabled={!selectedCustomer || busy}
                onValueChange={(value) => onFormChange((current) => ({ ...current, foodTagValue: value }))}
              />
              <SelectBox
                label="基础料理"
                value={form.foodId}
                options={recipeOptions}
                searchable
                disabled={recipeOptions.length === 0 || busy}
                onValueChange={(value) => onFormChange((current) => ({
                  ...current,
                  foodId: value,
                  extraIngredientIds: [],
                }))}
              />
              <MultiSelectBox
                label={`加料材料 (${selectedExtraIds.length}/${extraCapacity})`}
                value={selectedExtraValues}
                options={ingredientOptions}
                disabled={!selectedRecipe || extraCapacity <= 0 || busy}
                placeholder={!selectedRecipe ? '请先选择基础料理' : extraCapacity <= 0 ? '该料理已达到 5 个材料上限' : '选择额外材料'}
                onValueChange={(values) => {
                  const nextIds = normalizeIdList(values.map((value) => Number(value))).slice(0, extraCapacity);
                  onFormChange((current) => ({ ...current, extraIngredientIds: nextIds.map(String) }));
                }}
              />
          </div>

          <div className="flex flex-wrap items-center gap-4">
            {!form.editingId && (
              <>
                <SwitchField
                  label="保存后启用"
                  checked={form.enabled}
                  disabled={busy}
                  onCheckedChange={(enabled) => onFormChange((current) => ({ ...current, enabled }))}
                />
                <SwitchField
                  label="保存后推荐置顶"
                  checked={form.pinToTop}
                  disabled={busy}
                  onCheckedChange={(pinToTop) => onFormChange((current) => ({ ...current, pinToTop }))}
                />
              </>
            )}
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

          {formError && (
            <div className="border border-destructive/30 px-3 py-2 text-sm text-destructive">
              {formError}
            </div>
          )}

          <div className="flex flex-wrap items-center justify-end gap-2">
            {form.editingId && (
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={busy}
                data-gamepad-focus-key={`custom-recipes:form:${form.editingId}:cancel`}
                onClick={resetForm}
              >
                取消编辑
              </Button>
            )}
            <Button
              type="button"
              size="sm"
              disabled={Boolean(formError) || busy}
              data-gamepad-focus-key={`custom-recipes:form:${form.editingId || 'new'}:save`}
              onClick={saveForm}
            >
              {form.editingId ? '保存配方' : '新增配方'}
            </Button>
          </div>
        </CardContent>
      </Card>

      <ListPanel title={`自定义推荐料理 (${entries.length})`}>
        <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border px-3 py-2">
          <SegmentedControl
            value={groupMode}
            options={[
              { value: 'customer', label: '按稀客' },
              { value: 'recipe', label: '按基础料理' },
            ]}
            onValueChange={onGroupModeChange}
          />
          <FlagActions
            labelPrefix="全部"
            focusScope="custom-recipes:all"
            summary={summary}
            busy={busy}
            onUpdate={(flags) => void onUpdateCustomRecipeFlags({ selection: { scope: 'all' }, ...flags })}
          />
        </div>
        {entries.length === 0 && <EmptyRow text="暂无自定义推荐料理" />}
        <div>
          {groups.map((group) => {
            const groupSummary = summarizeEntries(group.entries);
            return (
              <section key={group.key} className="border-b border-border last:border-b-0">
                <div className="flex flex-wrap items-center justify-between gap-2 bg-muted/40 px-3 py-2">
                  <div className="flex min-w-0 flex-wrap items-center gap-2">
                    <span className="font-medium">{group.label}</span>
                    <Badge variant="outline">{groupSummary.total}</Badge>
                    <Badge variant="outline">启用 {groupSummary.enabled}</Badge>
                    <Badge variant="outline">置顶 {groupSummary.pinned}</Badge>
                  </div>
                  <FlagActions
                    labelPrefix="本组"
                    focusScope={`custom-recipes:group:${group.key}`}
                    summary={groupSummary}
                    busy={busy}
                    onUpdate={(flags) => void onUpdateCustomRecipeFlags({ selection: group.selection, ...flags })}
                  />
                </div>
                <div>
                  {group.entries.map((entry, index) => (
                    <CustomRecipeRow
                      key={entry.id}
                      entry={entry}
                      index={index}
                      total={group.entries.length}
                      groupMode={groupMode}
                      runtimeSets={runtimeSets}
                      dataIndexes={dataIndexes}
                      busy={busy}
                      onEdit={() => {
                        returnFocusKeyRef.current = `custom-recipe:${entry.id}:edit`;
                        onFormChange(entryToForm(entry));
                      }}
                      onRemove={() => void onRemoveCustomRecipe(entry.id).then((removed) => {
                        if (removed && form.editingId === entry.id) resetForm();
                      })}
                      onToggle={() => void onUpdateCustomRecipeFlags({
                        selection: { scope: 'entry', id: entry.id },
                        enabled: !entry.enabled,
                      })}
                      onTogglePin={() => void onUpdateCustomRecipeFlags({
                        selection: { scope: 'entry', id: entry.id },
                        pinToTop: !entry.pinToTop,
                      })}
                      onMove={onMoveCustomRecipe}
                    />
                  ))}
                </div>
              </section>
            );
          })}
        </div>
      </ListPanel>
    </div>
  );
}

function FlagActions({
  labelPrefix,
  focusScope,
  summary,
  busy,
  onUpdate,
}: {
  labelPrefix: '全部' | '本组';
  focusScope: string;
  summary: CustomRecipeSummary;
  busy: boolean;
  onUpdate: (flags: Pick<CustomRecipeFlagUpdateInput, 'enabled' | 'pinToTop'>) => void;
}) {
  return (
    <div className="flex flex-wrap justify-end gap-1.5" data-gamepad-axis="x">
      <Button
        type="button"
        size="xs"
        variant="outline"
        disabled={busy || summary.total === 0 || summary.enabled === summary.total}
        data-gamepad-focus-key={`${focusScope}:enable`}
        onClick={() => onUpdate({ enabled: true })}
      >
        {labelPrefix}启用
      </Button>
      <Button
        type="button"
        size="xs"
        variant="outline"
        disabled={busy || summary.enabled === 0}
        data-gamepad-focus-key={`${focusScope}:disable`}
        onClick={() => onUpdate({ enabled: false })}
      >
        {labelPrefix}停用
      </Button>
      <Button
        type="button"
        size="xs"
        variant="outline"
        disabled={busy || summary.total === 0 || summary.pinned === summary.total}
        data-gamepad-focus-key={`${focusScope}:pin`}
        onClick={() => onUpdate({ pinToTop: true })}
      >
        {labelPrefix}置顶
      </Button>
      <Button
        type="button"
        size="xs"
        variant="outline"
        disabled={busy || summary.pinned === 0}
        data-gamepad-focus-key={`${focusScope}:unpin`}
        onClick={() => onUpdate({ pinToTop: false })}
      >
        取消{labelPrefix}置顶
      </Button>
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
    <div className="steward-inline-panel px-3 py-2 text-sm text-muted-foreground">
      厨具 {recipe.cooker || '未知'} · 基础 {base} · 加料 {extras}
    </div>
  );
}

function CustomRecipeRow({
  entry,
  index,
  total,
  groupMode,
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
  groupMode: CustomRecipeGroupMode;
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
  const primaryLabel = groupMode === 'customer'
    ? recipe?.name ?? (entry.recipeName || `料理 #${entry.foodId}`)
    : entry.customerName || `稀客 #${entry.customerId}`;
  const actionTarget = `${entry.customerName || `稀客 #${entry.customerId}`}的${recipe?.name || entry.recipeName || `料理 #${entry.foodId}`}配方`;

  return (
    <div
      className="steward-data-row px-3 py-2 text-sm"
      data-gamepad-row="true"
      data-gamepad-row-key={`custom-recipe:${entry.id}`}
    >
      <div className="grid min-w-0 gap-2 min-[640px]:grid-cols-[minmax(10rem,1fr)_auto] min-[640px]:items-center">
        <div className="min-w-0" data-custom-recipe-row-content="true">
          <div className="flex flex-wrap items-center gap-1.5">
            <span className="font-medium">{primaryLabel}</span>
            <Badge variant={entry.foodTag === null ? 'secondary' : 'outline'}>
              {entry.foodTag === null ? '全部点单' : entry.foodTag}
            </Badge>
            <Badge variant={entry.enabled ? 'secondary' : 'outline'}>
              {entry.enabled ? '启用' : '停用'}
            </Badge>
            {entry.pinToTop && <Badge variant="secondary">置顶</Badge>}
          </div>
          <div className="mt-1 text-xs text-muted-foreground">
            {groupMode === 'customer' ? `优先级 ${index + 1} · ` : ''}
            厨具 {recipe?.cooker || '未知'} · 基础 {base} · 加料 {extras}
          </div>
        </div>
        <div className="flex min-w-0 flex-wrap justify-end gap-1.5 min-[640px]:max-w-[22rem]" data-gamepad-axis="x">
          {groupMode === 'customer' && (
            <>
              <Button
                type="button"
                size="xs"
                variant="outline"
                disabled={busy || index === 0}
                data-gamepad-focus-key={`custom-recipe:${entry.id}:up`}
                aria-label={`上移${actionTarget}`}
                onClick={() => void onMove(entry.id, 'up')}
              >
                上移
              </Button>
              <Button
                type="button"
                size="xs"
                variant="outline"
                disabled={busy || index === total - 1}
                data-gamepad-focus-key={`custom-recipe:${entry.id}:down`}
                aria-label={`下移${actionTarget}`}
                onClick={() => void onMove(entry.id, 'down')}
              >
                下移
              </Button>
            </>
          )}
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={busy}
            data-gamepad-focus-key={`custom-recipe:${entry.id}:pin`}
            aria-label={`${entry.pinToTop ? '取消置顶' : '置顶'}${actionTarget}`}
            onClick={onTogglePin}
          >
            {entry.pinToTop ? '取消置顶' : '置顶'}
          </Button>
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={busy}
            data-gamepad-focus-key={`custom-recipe:${entry.id}:toggle`}
            aria-label={`${entry.enabled ? '停用' : '启用'}${actionTarget}`}
            onClick={onToggle}
          >
            {entry.enabled ? '停用' : '启用'}
          </Button>
          <Button
            type="button"
            size="xs"
            variant="outline"
            disabled={busy}
            data-gamepad-focus-key={`custom-recipe:${entry.id}:edit`}
            aria-label={`编辑${actionTarget}`}
            onClick={onEdit}
          >
            编辑
          </Button>
          <Button
            type="button"
            size="xs"
            variant="destructive"
            disabled={busy}
            data-gamepad-focus-key={`custom-recipe:${entry.id}:remove`}
            aria-label={`删除${actionTarget}`}
            onClick={onRemove}
          >
            删除
          </Button>
        </div>
      </div>
    </div>
  );
}

function buildCustomRecipeGroups(
  entries: CustomRecipeEntry[],
  mode: CustomRecipeGroupMode,
  customers: RareCustomerCatalogItem[],
  recipesByFoodId: Map<number, RecipeCatalogItem>,
): CustomRecipeGroup[] {
  const customerNames = new Map(customers.map((customer) => [customer.id, customer.name]));
  const groups = new Map<string, CustomRecipeGroup>();
  for (const entry of entries) {
    const customerMode = mode === 'customer';
    const key = customerMode ? `customer:${entry.customerId}` : `recipe:${entry.foodId}`;
    let group = groups.get(key);
    if (!group) {
      const recipe = recipesByFoodId.get(entry.foodId);
      group = {
        key,
        label: customerMode
          ? (customerNames.get(entry.customerId) ?? entry.customerName) || `稀客 #${entry.customerId}`
          : (recipe?.name ?? entry.recipeName) || `料理 #${entry.foodId}`,
        selection: customerMode
          ? { scope: 'customer', customerId: entry.customerId }
          : { scope: 'recipe', foodId: entry.foodId },
        entries: [],
      };
      groups.set(key, group);
    }
    group.entries.push(entry);
  }

  for (const group of groups.values()) {
    group.entries.sort(mode === 'customer'
      ? compareCustomRecipeEntries
      : (left, right) =>
        (customerNames.get(left.customerId) ?? left.customerName).localeCompare(
          customerNames.get(right.customerId) ?? right.customerName,
          'zh-Hans-CN',
        )
        || (left.foodTag ?? '').localeCompare(right.foodTag ?? '', 'zh-Hans-CN')
        || left.sortOrder - right.sortOrder
        || left.id.localeCompare(right.id));
  }

  return [...groups.values()].sort((left, right) =>
    left.label.localeCompare(right.label, 'zh-Hans-CN') || left.key.localeCompare(right.key));
}

interface CustomRecipeSummary {
  total: number;
  enabled: number;
  pinned: number;
}

function summarizeEntries(entries: CustomRecipeEntry[]): CustomRecipeSummary {
  return entries.reduce<CustomRecipeSummary>((summary, entry) => ({
    total: summary.total + 1,
    enabled: summary.enabled + Number(entry.enabled),
    pinned: summary.pinned + Number(entry.pinToTop),
  }), { total: 0, enabled: 0, pinned: 0 });
}

function createInitialForm(customer: RareCustomerCatalogItem | null): CustomRecipeFormState {
  return {
    ...createEmptyCustomRecipeForm(),
    customerId: customer ? String(customer.id) : '',
  };
}

function entryToForm(entry: CustomRecipeEntry): CustomRecipeFormState {
  return {
    editingId: entry.id,
    customerId: String(entry.customerId),
    foodTagValue: entry.foodTag ?? CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE,
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
  const options = ingredients
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
  for (const id of selectedExtraIds) {
    if (options.some((option) => option.value === String(id))) continue;
    options.push({
      value: String(id),
      label: `${ingredientNameById.get(id) ?? `材料 #${id}`}（当前不可用）`,
      disabled: false,
    });
  }
  return options;
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
  form,
  selectedCustomer,
  selectedRecipe,
  totalIngredientCount,
  selectedExtraIds,
  runtimeSets,
  dataIndexes,
}: {
  apiToken: string;
  form: CustomRecipeFormState;
  selectedCustomer: RareCustomerCatalogItem | null;
  selectedRecipe: RecipeCatalogItem | null;
  totalIngredientCount: number;
  selectedExtraIds: number[];
  runtimeSets: RuntimeSets | null;
  dataIndexes: ReturnType<typeof buildRecommendationDataIndexes>;
}): string {
  if (!apiToken) return '未收到本地 API Token，无法保存自定义推荐料理。';
  if (!selectedCustomer) return form.customerId ? `当前目录未识别稀客 #${form.customerId}，请明确选择稀客后再保存。` : '请选择稀客。';
  if (!selectedRecipe) return form.foodId ? `当前目录未识别料理 #${form.foodId}，请明确选择基础料理后再保存。` : '请选择基础料理。';
  if (!runtimeSets?.recipeIds.has(selectedRecipe.id)) return '基础料理当前未解锁，请选择已解锁料理。';
  if (form.foodTagValue !== CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE
    && !selectedCustomer.positiveTags.filter(isOrderableRareFoodTag).includes(form.foodTagValue)) {
    return `当前稀客没有点单标签「${form.foodTagValue}」，请明确选择点单标签后再保存。`;
  }
  const unavailableExtraId = selectedExtraIds.find((id) => !dataIndexes.ingredientNameById.has(id)
    || !runtimeSets.ingredientIds.has(id));
  if (unavailableExtraId !== undefined) return `加料材料 #${unavailableExtraId} 当前不可用，请重新选择加料。`;
  if (selectedExtraIds.some((id) => selectedRecipe.ingredients.some((name) => dataIndexes.ingredientIdByName.get(name) === id))) {
    return '加料包含基础配方已有材料，请重新选择加料。';
  }
  if (selectedRecipe.ingredients.length > MAX_FOOD_INGREDIENT_COUNT) return '基础料理材料数量超过游戏上限。';
  if (totalIngredientCount > MAX_FOOD_INGREDIENT_COUNT) return '料理总材料数不能超过 5 个。';
  return '';
}
