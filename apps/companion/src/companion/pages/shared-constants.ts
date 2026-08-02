export const MAX_RECOMMENDATION_ROWS = 8;
export const MINIMUM_MULTICOLUMN_GRID_CLASS = 'steward-minimum-multicolumn-grid';
export const DENSE_TWO_COLUMN_GRID = `${MINIMUM_MULTICOLUMN_GRID_CLASS} grid grid-cols-1 gap-3 min-[640px]:grid-cols-2`;
export const DENSE_TWO_COLUMN_GRID_TIGHT = `${MINIMUM_MULTICOLUMN_GRID_CLASS} grid grid-cols-1 gap-2 min-[640px]:grid-cols-2`;
export const DENSE_THREE_COLUMN_GRID = `${MINIMUM_MULTICOLUMN_GRID_CLASS} grid grid-cols-1 gap-3 min-[640px]:grid-cols-2 min-[720px]:grid-cols-3`;
export const DENSE_FOUR_COLUMN_GRID = `${MINIMUM_MULTICOLUMN_GRID_CLASS} grid grid-cols-1 gap-3 min-[480px]:grid-cols-2 min-[720px]:grid-cols-4`;
export const DENSE_CARD_HEADER_GRID = 'grid grid-cols-[minmax(0,1fr)_auto] gap-3';
export const DENSE_ITEM_GRID = 'grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-2';
export const MOD_TAB_TRIGGER_CLASS = 'min-w-0 flex-1';
export const INNER_TAB_TRIGGER_CLASS = 'min-w-[4.25rem] flex-none min-[720px]:min-w-0 min-[720px]:flex-1';
export const SCROLL_FADE_CLASS = 'steward-scroll-fade';
export const RECOMMENDATION_SCROLL_AREA = `${SCROLL_FADE_CLASS} min-h-[28rem] max-h-[calc(100vh-18rem)] overflow-auto pb-4 pr-1`;

export interface LowStockEntry {
  id: number;
  name: string;
  qty: number;
}

export function buildLowStockEntries(
  qtyById: Record<string, number>,
  nameById: Map<number, string>,
  limit = 8,
): LowStockEntry[] {
  return Object.entries(qtyById)
    .map(([id, qty]) => {
      const numericId = Number(id);
      return {
        id: numericId,
        name: nameById.get(numericId) ?? `#${id}`,
        qty,
      };
    })
    .filter((item) => Number.isFinite(item.id) && item.qty >= 0)
    .sort((a, b) => a.qty - b.qty || a.id - b.id)
    .slice(0, limit);
}
