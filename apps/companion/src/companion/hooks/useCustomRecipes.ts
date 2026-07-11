import { useCallback, useEffect, useRef, useState } from 'react';
import {
  moveCustomRecipe,
  readCustomRecipes,
  removeCustomRecipe,
  setCustomRecipesEnabled,
  updateCustomRecipeFlags,
  upsertCustomRecipe,
} from '@/companion/api';
import {
  emptyCustomRecipeData,
  normalizeCustomRecipeData,
  normalizeCustomRecipeUpsertInput,
} from '@/companion/domain/custom-recipes';
import type {
  CustomRecipeData,
  CustomRecipeFlagUpdateInput,
  CustomRecipeMutationResponse,
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
  const mutationBusyRef = useRef(false);
  const refreshGenerationRef = useRef(0);

  const refreshCustomRecipes = useCallback(async () => {
    const refreshGeneration = ++refreshGenerationRef.current;
    if (!apiToken) {
      setCustomRecipes(emptyCustomRecipeData());
      return;
    }
    if (connectionPaused) return;

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);

    try {
      const data = await readCustomRecipes(normalizedEndpoint, apiToken, abortController.signal);
      if (refreshGeneration !== refreshGenerationRef.current) return;
      setCustomRecipes(normalizeCustomRecipeData(data));
      setCustomRecipeError('');
    } catch (err) {
      if (refreshGeneration !== refreshGenerationRef.current) return;
      setCustomRecipeError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
    }
  }, [apiToken, connectionPaused, normalizedEndpoint]);

  const runCustomRecipeMutation = useCallback(async (
    busyKey: string,
    errorMessage: string,
    mutation: () => Promise<CustomRecipeMutationResponse>,
  ) => {
    if (!apiToken || mutationBusyRef.current) return false;
    mutationBusyRef.current = true;
    refreshGenerationRef.current += 1;
    setCustomRecipeBusyKey(busyKey);
    setCustomRecipeError('');

    try {
      const response = await mutation();
      if (!response.ok) throw new Error(response.error || errorMessage);
      setCustomRecipes(normalizeCustomRecipeData(response.customRecipes));
      return true;
    } catch (err) {
      setCustomRecipeError(err instanceof Error ? err.message : String(err));
      return false;
    } finally {
      mutationBusyRef.current = false;
      setCustomRecipeBusyKey('');
    }
  }, [apiToken]);

  const upsertCustomRecipeEntry = useCallback(async (input: CustomRecipeUpsertInput) => {
    const normalized = normalizeCustomRecipeUpsertInput(input);
    const busyKey = normalized.id || `new:${normalized.customerId}:${normalized.foodId}`;
    return runCustomRecipeMutation(
      busyKey,
      '自定义推荐料理保存失败',
      () => upsertCustomRecipe(normalizedEndpoint, apiToken, normalized),
    );
  }, [apiToken, normalizedEndpoint, runCustomRecipeMutation]);

  const removeCustomRecipeEntry = useCallback(async (id: string) => {
    if (!id) return false;
    return runCustomRecipeMutation(
      `remove:${id}`,
      '自定义推荐料理删除失败',
      () => removeCustomRecipe(normalizedEndpoint, apiToken, id),
    );
  }, [apiToken, normalizedEndpoint, runCustomRecipeMutation]);

  const setCustomRecipesEnabledState = useCallback(async (enabled: boolean) =>
    runCustomRecipeMutation(
      'settings',
      '自定义推荐料理总开关更新失败',
      () => setCustomRecipesEnabled(normalizedEndpoint, apiToken, enabled),
    ), [apiToken, normalizedEndpoint, runCustomRecipeMutation]);

  const updateCustomRecipeFlagsState = useCallback(async (input: CustomRecipeFlagUpdateInput) =>
    runCustomRecipeMutation(
      `flags:${input.selection.scope}`,
      '自定义推荐料理状态更新失败',
      () => updateCustomRecipeFlags(normalizedEndpoint, apiToken, input),
    ), [apiToken, normalizedEndpoint, runCustomRecipeMutation]);

  const moveCustomRecipeEntry = useCallback(async (id: string, direction: 'up' | 'down') => {
    if (!id) return false;
    return runCustomRecipeMutation(
      `move:${id}`,
      '自定义推荐料理排序更新失败',
      () => moveCustomRecipe(normalizedEndpoint, apiToken, id, direction),
    );
  }, [apiToken, normalizedEndpoint, runCustomRecipeMutation]);

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
    setCustomRecipesEnabledState,
    updateCustomRecipeFlagsState,
    moveCustomRecipeEntry,
  };
}
