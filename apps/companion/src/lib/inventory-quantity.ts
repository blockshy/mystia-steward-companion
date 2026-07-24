export const INFINITE_INVENTORY_QUANTITY = -1;

export function isInfiniteInventoryQuantity(quantity: number | null | undefined): boolean {
  return quantity === INFINITE_INVENTORY_QUANTITY;
}

export function formatInventoryQuantityValue(quantity: number | null | undefined): string {
  if (isInfiniteInventoryQuantity(quantity)) return '无限';
  if (quantity == null || quantity < 0) return '--';
  return String(quantity);
}

export function formatInventoryQuantitySuffix(quantity: number | null | undefined): string {
  if (isInfiniteInventoryQuantity(quantity)) return '(∞)';
  return `(${quantity == null || quantity < 0 ? '?' : quantity})`;
}

export function inventoryQuantityRankValue(quantity: number): number {
  return isInfiniteInventoryQuantity(quantity) ? Number.MAX_SAFE_INTEGER : quantity;
}

export function cappedInventoryQuantityRank(quantity: number, maximum: number): number {
  return isInfiniteInventoryQuantity(quantity) ? maximum : Math.min(quantity, maximum);
}

export function inventoryShortage(quantity: number, threshold: number): number {
  return isInfiniteInventoryQuantity(quantity) ? 0 : Math.max(0, threshold - quantity);
}
