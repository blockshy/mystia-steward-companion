import {
  formatInventoryQuantityValue,
  inventoryQuantityRankValue,
  isInfiniteInventoryQuantity,
} from '@/lib/inventory-quantity';

export type InventorySortMode = 'name' | 'stock';

export const INVENTORY_SORT_OPTIONS: Array<{ value: InventorySortMode; label: string }> = [
  { value: 'name', label: '名称' },
  { value: 'stock', label: '库存' },
];

export type InventorySelectOption = {
  value: string;
  label: string;
  name: string;
  quantity: number | null;
};

type InventorySortableItem = {
  id: number;
  name: string;
};

export function sortInventoryItems<TItem extends InventorySortableItem>(
  items: TItem[],
  ownedQty: Record<number, number> | null | undefined,
  sortMode: InventorySortMode,
): TItem[] {
  return [...items]
    .filter((item) => item.id >= 0)
    .sort((left, right) => compareInventoryItems(left, right, ownedQty, sortMode));
}

export function buildInventorySelectOptions<TItem extends InventorySortableItem>(
  items: TItem[],
  ownedQty: Record<number, number> | null | undefined,
  sortMode: InventorySortMode,
): InventorySelectOption[] {
  return sortInventoryItems(items, ownedQty, sortMode)
    .map((item) => {
      const quantity = resolveInventoryQuantity(ownedQty, item.id);
      return {
        value: String(item.id),
        label: `${item.name} · 库存 ${formatInventoryQuantity(quantity)}`,
        name: item.name,
        quantity,
      };
    });
}

export function resolveInventoryQuantity(
  ownedQty: Record<number, number> | null | undefined,
  id: number,
): number | null {
  if (!ownedQty) return null;
  const value = ownedQty[id] ?? 0;
  return Number.isFinite(value) ? Math.trunc(value) : 0;
}

export function formatInventoryQuantity(quantity: number | null): string {
  return formatInventoryQuantityValue(quantity);
}

function compareInventoryItems<TItem extends InventorySortableItem>(
  left: TItem,
  right: TItem,
  ownedQty: Record<number, number> | null | undefined,
  sortMode: InventorySortMode,
): number {
  if (sortMode === 'stock') {
    const quantityCompare = compareInventoryQuantities(
      resolveInventoryQuantity(ownedQty, left.id),
      resolveInventoryQuantity(ownedQty, right.id),
    );
    if (quantityCompare !== 0) return quantityCompare;
  }

  const nameCompare = left.name.localeCompare(right.name, 'zh-Hans-CN');
  if (nameCompare !== 0) return nameCompare;
  return left.id - right.id;
}

function compareInventoryQuantities(left: number | null, right: number | null): number {
  const leftValue = normalizeQuantitySortValue(left);
  const rightValue = normalizeQuantitySortValue(right);
  return leftValue - rightValue;
}

function normalizeQuantitySortValue(quantity: number | null): number {
  if (quantity == null || quantity < 0 && !isInfiniteInventoryQuantity(quantity)) {
    return Number.POSITIVE_INFINITY;
  }
  return inventoryQuantityRankValue(quantity);
}
