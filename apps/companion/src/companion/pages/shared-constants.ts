export const MAX_RECOMMENDATION_ROWS = 8;
export const DENSE_TWO_COLUMN_GRID = 'grid grid-cols-2 gap-3 max-[719px]:grid-cols-1';
export const DENSE_TWO_COLUMN_GRID_TIGHT = 'grid grid-cols-2 gap-2 max-[719px]:grid-cols-1';
export const DENSE_THREE_COLUMN_GRID = 'grid grid-cols-3 gap-3';
export const DENSE_FOUR_COLUMN_GRID = 'grid grid-cols-4 gap-3';
export const DENSE_CARD_HEADER_GRID = 'grid grid-cols-[minmax(0,1fr)_auto] gap-3';
export const DENSE_ITEM_GRID = 'grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-2';
export const AUTOMATION_SWITCH_GRID = 'grid grid-cols-2 gap-2 xl:grid-cols-3';
export const AUTOMATION_SWITCH_CELL = 'steward-inline-panel min-w-0 px-2.5 py-1.5';
export const MOD_TAB_TRIGGER_CLASS = 'min-w-0 flex-1';
export const INNER_TAB_TRIGGER_CLASS = 'min-w-0 flex-1';
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
