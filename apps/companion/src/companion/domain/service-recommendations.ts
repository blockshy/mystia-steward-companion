/** 客户端仅保留协议、表单和展示辅助；业务推荐与自动化决策由 C# 服务计算。 */
import type { RareCustomerCatalogItem, PlaceName } from '@/lib/catalog-types';
import { ALL_PLACES } from '@/lib/catalog-types';

const NON_ORDERABLE_RARE_FOOD_TAGS = new Set(['流行喜爱', '流行厌恶']);

export function isUsableRareCustomer(customer: RareCustomerCatalogItem): boolean {
  return isUsableRareCustomerName(customer.name)
    && customer.positiveTags.some(isOrderableRareFoodTag)
    && customer.beverageTags.length > 0;
}

export function isSelectableRareCustomer(customer: RareCustomerCatalogItem): boolean {
  return isUsableRareCustomer(customer) && customer.places.length > 0;
}

export function normalizePlace(value: string | null | undefined): PlaceName | null {
  return ALL_PLACES.includes(value as PlaceName) ? value as PlaceName : null;
}

export function isOrderableRareFoodTag(tag: string): boolean {
  return !NON_ORDERABLE_RARE_FOOD_TAGS.has(tag);
}

function isUsableRareCustomerName(value: string): boolean {
  const name = value.trim();
  return Boolean(name)
    && name !== 'missing'
    && name !== 'null'
    && !name.includes('?')
    && !name.startsWith('#')
    && !/^[A-Za-z0-9_]+$/.test(name);
}
