/** 客户端仅保留协议、表单和展示辅助；业务推荐与自动化决策由 C# 服务计算。 */
import type { CustomRecipeData, CustomRecipeEntry, CustomRecipeUpsertInput } from '@/companion/types';

export function emptyCustomRecipeData(): CustomRecipeData {
  return {
    version: 1,
    enabled: true,
    recipes: [],
  };
}

export function normalizeCustomRecipeData(data: CustomRecipeData | null | undefined): CustomRecipeData {
  return {
    version: Math.max(1, data?.version ?? 1),
    enabled: data?.enabled !== false,
    recipes: (data?.recipes ?? [])
      .map(normalizeCustomRecipeEntry)
      .filter((entry): entry is CustomRecipeEntry => Boolean(entry))
      .sort(compareCustomRecipeEntries),
  };
}

export function normalizeCustomRecipeUpsertInput(input: CustomRecipeUpsertInput): CustomRecipeUpsertInput {
  return {
    ...input,
    id: input.id?.trim() || undefined,
    customerId: normalizeNonNegativeInteger(input.customerId, -1),
    customerName: input.customerName.trim(),
    foodTag: normalizeOptionalTag(input.foodTag),
    foodId: normalizeNonNegativeInteger(input.foodId, -1),
    recipeId: normalizeNonNegativeInteger(input.recipeId, -1),
    recipeName: input.recipeName.trim(),
    extraIngredientIds: normalizeIdList(input.extraIngredientIds),
    enabled: input.enabled == null ? undefined : Boolean(input.enabled),
    pinToTop: input.pinToTop == null ? undefined : Boolean(input.pinToTop),
    sortOrder: input.sortOrder == null ? undefined : normalizeNonNegativeInteger(input.sortOrder, 0),
  };
}

export function compareCustomRecipeEntries(left: CustomRecipeEntry, right: CustomRecipeEntry): number {
  if (left.sortOrder !== right.sortOrder) return left.sortOrder - right.sortOrder;
  if (left.customerId !== right.customerId) return left.customerId - right.customerId;
  const tagDiff = (left.foodTag ?? '').localeCompare(right.foodTag ?? '');
  if (tagDiff !== 0) return tagDiff;
  return left.id.localeCompare(right.id);
}

export function getEffectiveCustomRecipeEntries(
  customRecipes: CustomRecipeData,
  customerId: number,
  foodTag: string,
): CustomRecipeEntry[] {
  const normalized = normalizeCustomRecipeData(customRecipes);
  if (!normalized.enabled) return [];
  return normalized.recipes.filter((entry) =>
    entry.enabled
    && entry.customerId === customerId
    && (entry.foodTag === null || entry.foodTag === foodTag)
  );
}

export function normalizeIdList(ids: number[]): number[] {
  return [...new Set(ids.filter((id) => Number.isFinite(id) && id >= 0).map((id) => Math.trunc(id)))].sort((a, b) => a - b);
}

function normalizeCustomRecipeEntry(entry: CustomRecipeEntry): CustomRecipeEntry | null {
  const id = entry.id?.trim();
  const customerId = normalizeNonNegativeInteger(entry.customerId, -1);
  const foodId = normalizeNonNegativeInteger(entry.foodId, -1);
  if (!id || customerId < 0 || foodId < 0) return null;

  return {
    id,
    customerId,
    customerName: (entry.customerName ?? '').trim(),
    foodTag: normalizeOptionalTag(entry.foodTag),
    foodId,
    recipeId: normalizeNonNegativeInteger(entry.recipeId, -1),
    recipeName: (entry.recipeName ?? '').trim(),
    extraIngredientIds: normalizeIdList(entry.extraIngredientIds ?? []),
    enabled: entry.enabled !== false,
    pinToTop: entry.pinToTop !== false,
    sortOrder: normalizeNonNegativeInteger(entry.sortOrder, 0),
    createdAtUtc: entry.createdAtUtc || '',
    updatedAtUtc: entry.updatedAtUtc || '',
  };
}

function normalizeOptionalTag(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

function normalizeNonNegativeInteger(value: number, fallback: number): number {
  return Number.isFinite(value) && value >= 0 ? Math.trunc(value) : fallback;
}
