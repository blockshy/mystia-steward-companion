import { useCallback, useEffect, useRef, useState } from 'react';
import {
  addBeverageFavorite,
  addRecipeFavorite,
  readFavorites,
  removeBeverageFavorite,
  removeRecipeFavorite,
} from '@/companion/api';
import {
  beverageFavoriteKey,
  emptyFavoriteData,
  findBeverageFavorite,
  findRecipeFavorite,
  normalizeFavoriteData,
  recipeFavoriteKey,
} from '@/companion/domain/favorites';
import { getConnectionRetryDelayMs } from '@/companion/connection-recovery';
import type {
  FavoriteData,
  FavoriteMutationResponse,
  ToggleBeverageFavorite,
  ToggleRecipeFavorite,
} from '@/companion/types';

interface UseFavoritesOptions {
  apiToken: string;
  connected: boolean;
  connectionRevision: number;
  normalizedEndpoint: string;
}

const FAVORITE_READ_TIMEOUT_MS = 2800;

export function useFavorites({
  apiToken,
  connected,
  connectionRevision,
  normalizedEndpoint,
}: UseFavoritesOptions) {
  const [favorites, setFavorites] = useState<FavoriteData>(() => emptyFavoriteData());
  const [favoriteReadError, setFavoriteReadError] = useState('');
  const [favoriteMutationError, setFavoriteMutationError] = useState('');
  const [favoriteBusyKey, setFavoriteBusyKey] = useState('');
  const [favoriteRefreshing, setFavoriteRefreshing] = useState(false);
  const [favoriteRefreshFailureCount, setFavoriteRefreshFailureCount] = useState(0);
  const [favoriteRefreshRequired, setFavoriteRefreshRequired] = useState(true);
  const mutationBusyRef = useRef(false);
  const mutationGenerationRef = useRef(0);
  const activeMutationGenerationRef = useRef<number | null>(null);
  const refreshGenerationRef = useRef(0);
  const refreshAbortControllerRef = useRef<AbortController | null>(null);
  const mountedRef = useRef(true);
  const resourceIdentity = `${normalizedEndpoint}\n${apiToken}`;
  const connectionIdentity = `${resourceIdentity}\n${connectionRevision}`;
  const connectionIdentityRef = useRef(connectionIdentity);
  const previousResourceIdentityRef = useRef(resourceIdentity);
  connectionIdentityRef.current = connectionIdentity;

  useEffect(() => {
    const resourceIdentityChanged = previousResourceIdentityRef.current !== resourceIdentity;
    previousResourceIdentityRef.current = resourceIdentity;
    mutationGenerationRef.current += 1;
    refreshGenerationRef.current += 1;
    refreshAbortControllerRef.current?.abort();
    refreshAbortControllerRef.current = null;
    setFavoriteRefreshing(false);
    setFavoriteRefreshRequired(true);
    if (resourceIdentityChanged || !apiToken) {
      setFavorites(emptyFavoriteData());
      setFavoriteReadError('');
      setFavoriteMutationError('');
      setFavoriteRefreshFailureCount(0);
    }
  }, [apiToken, connectionIdentity, resourceIdentity]);

  useEffect(() => {
    if (connected) return;
    refreshGenerationRef.current += 1;
    refreshAbortControllerRef.current?.abort();
    refreshAbortControllerRef.current = null;
    setFavoriteRefreshing(false);
    setFavoriteRefreshRequired(true);
  }, [connected]);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      mutationGenerationRef.current += 1;
      refreshGenerationRef.current += 1;
      refreshAbortControllerRef.current?.abort();
      refreshAbortControllerRef.current = null;
    };
  }, []);

  const refreshFavorites = useCallback(async () => {
    if (!apiToken || !connected || mutationBusyRef.current || refreshAbortControllerRef.current) return;

    const refreshGeneration = ++refreshGenerationRef.current;
    const requestConnectionIdentity = connectionIdentityRef.current;
    const abortController = new AbortController();
    refreshAbortControllerRef.current = abortController;
    const timeoutId = window.setTimeout(() => abortController.abort(), FAVORITE_READ_TIMEOUT_MS);
    setFavoriteRefreshRequired(false);
    setFavoriteRefreshing(true);

    try {
      const data = await readFavorites(normalizedEndpoint, apiToken, {
        signal: abortController.signal,
        timeoutMs: FAVORITE_READ_TIMEOUT_MS,
      });
      if (refreshGeneration !== refreshGenerationRef.current
        || requestConnectionIdentity !== connectionIdentityRef.current) return;
      setFavorites(normalizeFavoriteData(data));
      setFavoriteReadError('');
      setFavoriteRefreshFailureCount(0);
      setFavoriteRefreshRequired(false);
    } catch (err) {
      if (refreshGeneration !== refreshGenerationRef.current
        || requestConnectionIdentity !== connectionIdentityRef.current) return;
      setFavoriteReadError(err instanceof Error ? err.message : String(err));
      setFavoriteRefreshFailureCount((current) => current + 1);
    } finally {
      window.clearTimeout(timeoutId);
      if (refreshAbortControllerRef.current === abortController) {
        refreshAbortControllerRef.current = null;
      }
      if (refreshGeneration === refreshGenerationRef.current
        && requestConnectionIdentity === connectionIdentityRef.current) {
        setFavoriteRefreshing(false);
      }
    }
  }, [apiToken, connected, normalizedEndpoint]);

  const runFavoriteMutation = useCallback(async (
    busyKey: string,
    errorMessage: string,
    mutation: () => Promise<FavoriteMutationResponse>,
  ) => {
    if (!apiToken || !connected || mutationBusyRef.current) return false;
    mutationBusyRef.current = true;
    const mutationGeneration = ++mutationGenerationRef.current;
    activeMutationGenerationRef.current = mutationGeneration;
    const requestConnectionIdentity = connectionIdentityRef.current;
    refreshGenerationRef.current += 1;
    if (refreshAbortControllerRef.current) setFavoriteRefreshRequired(true);
    refreshAbortControllerRef.current?.abort();
    refreshAbortControllerRef.current = null;
    setFavoriteRefreshing(false);
    setFavoriteBusyKey(busyKey);
    setFavoriteMutationError('');

    try {
      const response = await mutation();
      if (mutationGeneration !== mutationGenerationRef.current
        || requestConnectionIdentity !== connectionIdentityRef.current) return false;
      if (!response.ok) throw new Error(response.error || errorMessage);
      setFavorites(normalizeFavoriteData(response.favorites));
      setFavoriteReadError('');
      setFavoriteMutationError('');
      setFavoriteRefreshFailureCount(0);
      setFavoriteRefreshRequired(false);
      return true;
    } catch (err) {
      if (mutationGeneration === mutationGenerationRef.current
        && requestConnectionIdentity === connectionIdentityRef.current) {
        setFavoriteMutationError(err instanceof Error ? err.message : String(err));
      }
      return false;
    } finally {
      if (activeMutationGenerationRef.current === mutationGeneration) {
        activeMutationGenerationRef.current = null;
        mutationBusyRef.current = false;
        if (mountedRef.current) setFavoriteBusyKey('');
      }
    }
  }, [apiToken, connected]);

  const toggleRecipeFavorite = useCallback<ToggleRecipeFavorite>(async (customer, foodTag, recipe) => {
    if (!apiToken || !foodTag) return;
    const existing = findRecipeFavorite(favorites, customer.id, foodTag, recipe);
    const busyKey = existing?.id ?? recipeFavoriteKey(customer.id, foodTag, recipe);
    await runFavoriteMutation(
      busyKey,
      '收藏更新失败',
      () => existing
        ? removeRecipeFavorite(normalizedEndpoint, apiToken, existing.id)
        : addRecipeFavorite(normalizedEndpoint, apiToken, customer, foodTag, recipe),
    );
  }, [apiToken, favorites, normalizedEndpoint, runFavoriteMutation]);

  const toggleBeverageFavorite = useCallback<ToggleBeverageFavorite>(async (customer, beverageTag, beverage) => {
    if (!apiToken || !beverageTag) return;
    const existing = findBeverageFavorite(favorites, customer.id, beverageTag, beverage);
    const busyKey = existing?.id ?? beverageFavoriteKey(customer.id, beverageTag, beverage);
    await runFavoriteMutation(
      busyKey,
      '收藏更新失败',
      () => existing
        ? removeBeverageFavorite(normalizedEndpoint, apiToken, existing.id)
        : addBeverageFavorite(normalizedEndpoint, apiToken, customer, beverageTag, beverage),
    );
  }, [apiToken, favorites, normalizedEndpoint, runFavoriteMutation]);

  const removeRecipeFavoriteById = useCallback(async (id: string) => {
    if (!id) return false;
    return runFavoriteMutation(
      id,
      '取消料理收藏失败',
      () => removeRecipeFavorite(normalizedEndpoint, apiToken, id),
    );
  }, [apiToken, normalizedEndpoint, runFavoriteMutation]);

  const removeBeverageFavoriteById = useCallback(async (id: string) => {
    if (!id) return false;
    return runFavoriteMutation(
      id,
      '取消酒水收藏失败',
      () => removeBeverageFavorite(normalizedEndpoint, apiToken, id),
    );
  }, [apiToken, normalizedEndpoint, runFavoriteMutation]);

  useEffect(() => {
    if (!apiToken || !connected || favoriteBusyKey || !favoriteRefreshRequired) return;
    void refreshFavorites();
  }, [
    apiToken,
    connected,
    connectionIdentity,
    favoriteBusyKey,
    favoriteRefreshRequired,
    refreshFavorites,
  ]);

  useEffect(() => {
    if (!apiToken
      || !connected
      || !favoriteReadError
      || favoriteRefreshing
      || favoriteBusyKey
      || favoriteRefreshFailureCount < 1) return;
    const timer = window.setTimeout(() => {
      void refreshFavorites();
    }, getConnectionRetryDelayMs(favoriteRefreshFailureCount));
    return () => window.clearTimeout(timer);
  }, [
    apiToken,
    connected,
    favoriteBusyKey,
    favoriteReadError,
    favoriteRefreshFailureCount,
    favoriteRefreshing,
    refreshFavorites,
  ]);

  const favoriteError = favoriteMutationError
    || (favoriteReadError ? `收藏数据同步失败：${favoriteReadError}` : '');

  return {
    favorites,
    favoriteError,
    favoriteBusyKey,
    favoriteRefreshing,
    refreshFavorites,
    toggleRecipeFavorite,
    toggleBeverageFavorite,
    removeRecipeFavoriteById,
    removeBeverageFavoriteById,
  };
}
