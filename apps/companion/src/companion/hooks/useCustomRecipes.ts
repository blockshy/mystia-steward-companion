import { useCallback, useEffect, useState } from 'react';
import {
  moveCustomRecipe,
  readCustomRecipes,
  removeCustomRecipe,
  toggleCustomRecipe,
  upsertCustomRecipe,
} from '@/companion/api';
import {
  emptyCustomRecipeData,
  normalizeCustomRecipeData,
  normalizeCustomRecipeUpsertInput,
} from '@/companion/domain/custom-recipes';
import type {
  CustomRecipeData,
  CustomRecipeUpsertInput,
} from '@/companion/types';

interface UseCustomRecipesOptions {
  apiToken: string;
  connectionPaused: boolean;
  normalizedEndpoint: string;
}

export function useCustomRecipes({ apiToken, connectionPaused, normalizedEndpoint }: UseCustomRecipesOptions) {
  const [customRecipes, setCustomRecipes] = useState<CustomRecipeData>(() => emptyCustomRecipeData());
  const [customRecipeError, setCustomRecipeError] = useState('');
  const [customRecipeBusyKey, setCustomRecipeBusyKey] = useState('');

  const refreshCustomRecipes = useCallback(async () => {
    if (!apiToken) {
      setCustomRecipes(emptyCustomRecipeData());
      return;
    }
    if (connectionPaused) return;

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);

    try {
      const data = await readCustomRecipes(normalizedEndpoint, apiToken, abortController.signal);
      setCustomRecipes(normalizeCustomRecipeData(data));
      setCustomRecipeError('');
    } catch (err) {
      setCustomRecipeError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
    }
  }, [apiToken, connectionPaused, normalizedEndpoint]);

  const upsertCustomRecipeEntry = useCallback(async (input: CustomRecipeUpsertInput) => {
    if (!apiToken) return false;

    const normalized = normalizeCustomRecipeUpsertInput(input);
    const busyKey = normalized.id || `new:${normalized.customerId}:${normalized.foodId}`;
    setCustomRecipeBusyKey(busyKey);
    setCustomRecipeError('');

    try {
      const response = await upsertCustomRecipe(normalizedEndpoint, apiToken, normalized);
      if (!response.ok) throw new Error(response.error || '自定义推荐料理保存失败');
      setCustomRecipes(normalizeCustomRecipeData(response.customRecipes));
      return true;
    } catch (err) {
      setCustomRecipeError(err instanceof Error ? err.message : String(err));
      return false;
    } finally {
      setCustomRecipeBusyKey('');
    }
  }, [apiToken, normalizedEndpoint]);

  const removeCustomRecipeEntry = useCallback(async (id: string) => {
    if (!apiToken || !id) return false;
    setCustomRecipeBusyKey(id);
    setCustomRecipeError('');

    try {
      const response = await removeCustomRecipe(normalizedEndpoint, apiToken, id);
      if (!response.ok) throw new Error(response.error || '自定义推荐料理删除失败');
      setCustomRecipes(normalizeCustomRecipeData(response.customRecipes));
      return true;
    } catch (err) {
      setCustomRecipeError(err instanceof Error ? err.message : String(err));
      return false;
    } finally {
      setCustomRecipeBusyKey('');
    }
  }, [apiToken, normalizedEndpoint]);

  const toggleCustomRecipeEntry = useCallback(async (id: string, enabled: boolean) => {
    if (!apiToken || !id) return false;
    setCustomRecipeBusyKey(id);
    setCustomRecipeError('');

    try {
      const response = await toggleCustomRecipe(normalizedEndpoint, apiToken, id, enabled);
      if (!response.ok) throw new Error(response.error || '自定义推荐料理启用状态更新失败');
      setCustomRecipes(normalizeCustomRecipeData(response.customRecipes));
      return true;
    } catch (err) {
      setCustomRecipeError(err instanceof Error ? err.message : String(err));
      return false;
    } finally {
      setCustomRecipeBusyKey('');
    }
  }, [apiToken, normalizedEndpoint]);

  const moveCustomRecipeEntry = useCallback(async (id: string, direction: 'up' | 'down') => {
    if (!apiToken || !id) return false;
    setCustomRecipeBusyKey(id);
    setCustomRecipeError('');

    try {
      const response = await moveCustomRecipe(normalizedEndpoint, apiToken, id, direction);
      if (!response.ok) throw new Error(response.error || '自定义推荐料理排序更新失败');
      setCustomRecipes(normalizeCustomRecipeData(response.customRecipes));
      return true;
    } catch (err) {
      setCustomRecipeError(err instanceof Error ? err.message : String(err));
      return false;
    } finally {
      setCustomRecipeBusyKey('');
    }
  }, [apiToken, normalizedEndpoint]);

  useEffect(() => {
    void refreshCustomRecipes();
  }, [refreshCustomRecipes]);

  return {
    customRecipes,
    customRecipeError,
    customRecipeBusyKey,
    refreshCustomRecipes,
    upsertCustomRecipeEntry,
    removeCustomRecipeEntry,
    toggleCustomRecipeEntry,
    moveCustomRecipeEntry,
  };
}
