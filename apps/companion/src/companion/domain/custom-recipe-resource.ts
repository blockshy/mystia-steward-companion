import type { CustomRecipeData } from '@/companion/types';
import { normalizeCustomRecipeData } from '@/companion/domain/custom-recipes';

export interface CustomRecipeAvailability {
  canRead: boolean;
  canRefresh: boolean;
  canWrite: boolean;
  current: boolean;
  reason: string;
}

export function buildCustomRecipeAvailability(input: {
  connected: boolean;
  authorized: boolean;
  confirmed: boolean;
  busy: boolean;
  refreshing: boolean;
  readError: string;
}): CustomRecipeAvailability {
  const canRead = input.authorized && input.connected;
  const current = canRead && input.confirmed && !input.readError;
  return {
    canRead,
    canRefresh: canRead && !input.busy && !input.refreshing,
    canWrite: current && !input.busy && !input.refreshing,
    current,
    reason: !input.authorized ? '连接 Mod 后才能读取和修改自定义推荐料理。'
      : !input.connected ? '连接尚未确认；已有配方为上次读取结果，恢复连接后才能修改。'
        : input.busy ? '自定义推荐料理修改正在处理中。'
          : input.readError ? '配方数据未同步成功；确认当前配方后才能修改。'
            : !input.confirmed || input.refreshing ? '正在确认当前连接的自定义推荐料理。' : '',
  };
}

/** A partial response must never authorize another write as an empty/default collection. */
export function readConfirmedCustomRecipeData(value: CustomRecipeData): CustomRecipeData {
  const integer = (number: unknown) => typeof number === 'number' && Number.isSafeInteger(number) && number >= 0;
  if (!value || value.version !== 1
    || typeof value.enabled !== 'boolean' || !Array.isArray(value.recipes)) {
    throw new Error('自定义推荐料理响应不完整，请重新读取。');
  }
  const ids = new Set<string>();
  for (const entry of value.recipes) {
    if (!entry || typeof entry.id !== 'string' || !entry.id.trim() || ids.has(entry.id.trim())
      || !integer(entry.customerId) || !integer(entry.foodId) || !integer(entry.recipeId)
      || typeof entry.customerName !== 'string' || typeof entry.recipeName !== 'string'
      || (entry.foodTag !== null && typeof entry.foodTag !== 'string')
      || !Array.isArray(entry.extraIngredientIds) || !entry.extraIngredientIds.every(integer)
      || new Set(entry.extraIngredientIds).size !== entry.extraIngredientIds.length
      || typeof entry.enabled !== 'boolean' || typeof entry.pinToTop !== 'boolean'
      || !integer(entry.sortOrder) || typeof entry.createdAtUtc !== 'string' || typeof entry.updatedAtUtc !== 'string') {
      throw new Error('自定义推荐料理集合不完整或标识重复，请重新读取。');
    }
    ids.add(entry.id.trim());
  }
  return normalizeCustomRecipeData(value);
}
