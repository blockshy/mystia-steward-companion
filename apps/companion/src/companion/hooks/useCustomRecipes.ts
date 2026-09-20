import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  moveCustomRecipe,
  readCustomRecipes,
  removeCustomRecipe,
  setCustomRecipesEnabled,
  updateCustomRecipeFlags,
  upsertCustomRecipe,
} from '@/companion/api';
import { emptyCustomRecipeData, normalizeCustomRecipeUpsertInput } from '@/companion/domain/custom-recipes';
import { buildCustomRecipeAvailability, readConfirmedCustomRecipeData } from '@/companion/domain/custom-recipe-resource';
import { getConnectionRetryDelayMs } from '@/companion/connection-recovery';
import type { CustomRecipeFlagUpdateInput, CustomRecipeMutationResponse, CustomRecipeUpsertInput } from '@/companion/types';

interface UseCustomRecipesOptions {
  apiToken: string;
  connected: boolean;
  connectionRevision: number;
  normalizedEndpoint: string;
}

const READ_TIMEOUT_MS = 2800;

/** Owned by the workbench: leaving a page or changing connection never releases a pending POST. */
export function useCustomRecipes({ apiToken, connected, connectionRevision, normalizedEndpoint }: UseCustomRecipesOptions) {
  const resourceIdentity = JSON.stringify([normalizedEndpoint, apiToken]);
  const scope = useMemo(() => ({ resourceIdentity, connectionRevision, connected }), [resourceIdentity, connectionRevision, connected]);
  const scopeRef = useRef(scope);
  scopeRef.current = scope;
  const mountedRef = useRef(false);
  const mutationOwnerRef = useRef<object | null>(null);
  const readOwnerRef = useRef<AbortController | null>(null);
  const [collection, setCollection] = useState(() => ({ resourceIdentity, data: emptyCustomRecipeData() }));
  const [confirmedScope, setConfirmedScope] = useState<object | null>(null);
  const [readError, setReadError] = useState('');
  const [mutationError, setMutationError] = useState('');
  const [readFailureCount, setReadFailureCount] = useState(0);
  const [customRecipeRefreshing, setCustomRecipeRefreshing] = useState(false);
  const [customRecipeBusyKey, setCustomRecipeBusyKey] = useState('');
  const customRecipeAvailability = buildCustomRecipeAvailability({
    authorized: Boolean(apiToken), connected, confirmed: confirmedScope === scope,
    busy: Boolean(customRecipeBusyKey), refreshing: customRecipeRefreshing, readError,
  });
  const availabilityRef = useRef(customRecipeAvailability);
  availabilityRef.current = customRecipeAvailability;
  const customRecipes = collection.resourceIdentity === resourceIdentity ? collection.data : emptyCustomRecipeData();

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      readOwnerRef.current?.abort();
      readOwnerRef.current = null;
    };
  }, []);

  useEffect(() => {
    readOwnerRef.current?.abort();
    readOwnerRef.current = null;
    setCustomRecipeRefreshing(false);
    setConfirmedScope(null);
    setReadFailureCount(0);
    setReadError('');
    setMutationError('');
  }, [scope]);

  const refreshCustomRecipes = useCallback(async () => {
    if (!apiToken || !connected || scopeRef.current !== scope || !mountedRef.current
      || mutationOwnerRef.current || readOwnerRef.current) return;
    const owner = new AbortController();
    readOwnerRef.current = owner;
    const current = () => mountedRef.current && scopeRef.current === scope && readOwnerRef.current === owner;
    const timeout = window.setTimeout(() => owner.abort(), READ_TIMEOUT_MS);
    setCustomRecipeRefreshing(true);
    setConfirmedScope(null);
    try {
      const data = readConfirmedCustomRecipeData(await readCustomRecipes(normalizedEndpoint, apiToken, owner.signal));
      if (!current()) return;
      setCollection({ resourceIdentity, data });
      setConfirmedScope(scope);
      setReadError('');
      setReadFailureCount(0);
      // A successful GET confirms current data, but does not pretend an uncertain POST succeeded.
    } catch (error) {
      if (!current()) return;
      setReadError(error instanceof Error ? error.message : String(error));
      setReadFailureCount((count) => count + 1);
    } finally {
      window.clearTimeout(timeout);
      if (current()) setCustomRecipeRefreshing(false);
      if (readOwnerRef.current === owner) readOwnerRef.current = null;
    }
  }, [apiToken, connected, normalizedEndpoint, resourceIdentity, scope]);

  useEffect(() => {
    if (!apiToken || !connected || customRecipeBusyKey || customRecipeRefreshing || confirmedScope === scope) return;
    if (readFailureCount === 0) {
      void refreshCustomRecipes();
      return;
    }
    const timer = window.setTimeout(() => void refreshCustomRecipes(), getConnectionRetryDelayMs(readFailureCount));
    return () => window.clearTimeout(timer);
  }, [apiToken, connected, customRecipeBusyKey, customRecipeRefreshing, confirmedScope, scope, readFailureCount, refreshCustomRecipes]);

  const runCustomRecipeMutation = useCallback(async (
    busyKey: string,
    errorMessage: string,
    mutation: () => Promise<CustomRecipeMutationResponse>,
  ) => {
    if (!mountedRef.current || scopeRef.current !== scope || !availabilityRef.current.canWrite
      || mutationOwnerRef.current || readOwnerRef.current) return false;
    const owner = {};
    mutationOwnerRef.current = owner;
    const current = () => mountedRef.current && scopeRef.current === scope && mutationOwnerRef.current === owner;
    setCustomRecipeBusyKey(busyKey);
    setMutationError('');
    try {
      const response = await mutation();
      if (!current()) return false;
      if (response.ok !== true) throw new Error(response.error || errorMessage);
      const data = readConfirmedCustomRecipeData(response.customRecipes);
      setCollection({ resourceIdentity, data });
      setConfirmedScope(scope);
      setReadError('');
      setReadFailureCount(0);
      return true;
    } catch (error) {
      if (current()) {
        const detail = error instanceof Error ? error.message : String(error);
        availabilityRef.current = { ...availabilityRef.current, current: false, canWrite: false };
        setMutationError(detail);
        setConfirmedScope(null);
        setReadFailureCount(0);
      }
      return false;
    } finally {
      if (mutationOwnerRef.current === owner) {
        mutationOwnerRef.current = null;
        if (mountedRef.current) setCustomRecipeBusyKey('');
      }
    }
  }, [resourceIdentity, scope]);

  const upsertCustomRecipeEntry = useCallback(async (input: CustomRecipeUpsertInput) => {
    const normalized = normalizeCustomRecipeUpsertInput(input);
    return runCustomRecipeMutation(normalized.id || `new:${normalized.customerId}:${normalized.foodId}`,
      '自定义推荐料理保存失败', () => upsertCustomRecipe(normalizedEndpoint, apiToken, normalized));
  }, [apiToken, normalizedEndpoint, runCustomRecipeMutation]);
  const removeCustomRecipeEntry = useCallback(async (id: string) => id
    ? runCustomRecipeMutation(`remove:${id}`, '自定义推荐料理删除失败', () => removeCustomRecipe(normalizedEndpoint, apiToken, id))
    : false, [apiToken, normalizedEndpoint, runCustomRecipeMutation]);
  const setCustomRecipesEnabledState = useCallback(async (enabled: boolean) =>
    runCustomRecipeMutation('settings', '自定义推荐料理总开关更新失败', () => setCustomRecipesEnabled(normalizedEndpoint, apiToken, enabled)),
  [apiToken, normalizedEndpoint, runCustomRecipeMutation]);
  const updateCustomRecipeFlagsState = useCallback(async (input: CustomRecipeFlagUpdateInput) =>
    runCustomRecipeMutation(`flags:${input.selection.scope}`, '自定义推荐料理状态更新失败', () => updateCustomRecipeFlags(normalizedEndpoint, apiToken, input)),
  [apiToken, normalizedEndpoint, runCustomRecipeMutation]);
  const moveCustomRecipeEntry = useCallback(async (id: string, direction: 'up' | 'down') => id
    ? runCustomRecipeMutation(`move:${id}`, '自定义推荐料理排序更新失败', () => moveCustomRecipe(normalizedEndpoint, apiToken, id, direction))
    : false, [apiToken, normalizedEndpoint, runCustomRecipeMutation]);

  return {
    customRecipes,
    customRecipeError: mutationError
      ? `${mutationError} ${confirmedScope === scope ? '已重新读取当前配方，请核对修改结果；不会自动重试修改。' : '本次修改结果需要重新读取确认，不会自动重试修改。'}`
      : (readError ? `自定义推荐料理同步失败：${readError}` : ''),
    customRecipeBusyKey, customRecipeRefreshing, customRecipeAvailability,
    refreshCustomRecipes, upsertCustomRecipeEntry, removeCustomRecipeEntry,
    setCustomRecipesEnabledState, updateCustomRecipeFlagsState, moveCustomRecipeEntry,
  };
}
