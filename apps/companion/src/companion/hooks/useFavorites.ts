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
import type {
  FavoriteData,
  FavoriteMutationResponse,
  ToggleBeverageFavorite,
  ToggleRecipeFavorite,
} from '@/companion/types';

interface UseFavoritesOptions {
  apiToken: string;
  connectionPaused: boolean;
  normalizedEndpoint: string;
}

export function useFavorites({ apiToken, connectionPaused, normalizedEndpoint }: UseFavoritesOptions) {
  const [favorites, setFavorites] = useState<FavoriteData>(() => emptyFavoriteData());
  const [favoriteError, setFavoriteError] = useState('');
  const [favoriteBusyKey, setFavoriteBusyKey] = useState('');
  const [favoriteRefreshing, setFavoriteRefreshing] = useState(false);
  const mutationBusyRef = useRef(false);
  const mutationGenerationRef = useRef(0);
  const refreshGenerationRef = useRef(0);
  const connectionIdentity = `${normalizedEndpoint}\n${apiToken}\n${connectionPaused ? 'paused' : 'active'}`;
  const connectionIdentityRef = useRef(connectionIdentity);
  connectionIdentityRef.current = connectionIdentity;

  const refreshFavorites = useCallback(async () => {
    const refreshGeneration = ++refreshGenerationRef.current;
    const requestConnectionIdentity = connectionIdentityRef.current;
    if (!apiToken) {
      setFavorites(emptyFavoriteData());
      setFavoriteRefreshing(false);
      return;
    }
    if (connectionPaused) {
      setFavoriteRefreshing(false);
      return;
    }

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setFavoriteRefreshing(true);

    try {
      const data = await readFavorites(normalizedEndpoint, apiToken, abortController.signal);
      if (refreshGeneration !== refreshGenerationRef.current
        || requestConnectionIdentity !== connectionIdentityRef.current) return;
      setFavorites(normalizeFavoriteData(data));
      setFavoriteError('');
    } catch (err) {
      if (refreshGeneration !== refreshGenerationRef.current
        || requestConnectionIdentity !== connectionIdentityRef.current) return;
      setFavoriteError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      if (refreshGeneration === refreshGenerationRef.current
        && requestConnectionIdentity === connectionIdentityRef.current) {
        setFavoriteRefreshing(false);
      }
    }
  }, [apiToken, connectionPaused, normalizedEndpoint]);

  const runFavoriteMutation = useCallback(async (
    busyKey: string,
    errorMessage: string,
    mutation: () => Promise<FavoriteMutationResponse>,
  ) => {
    if (!apiToken || connectionPaused || mutationBusyRef.current) return false;
    mutationBusyRef.current = true;
    const mutationGeneration = ++mutationGenerationRef.current;
    const requestConnectionIdentity = connectionIdentityRef.current;
    refreshGenerationRef.current += 1;
    setFavoriteRefreshing(false);
    setFavoriteBusyKey(busyKey);
    setFavoriteError('');

    try {
      const response = await mutation();
      if (mutationGeneration !== mutationGenerationRef.current
        || requestConnectionIdentity !== connectionIdentityRef.current) return false;
      if (!response.ok) throw new Error(response.error || errorMessage);
      setFavorites(normalizeFavoriteData(response.favorites));
      return true;
    } catch (err) {
      if (mutationGeneration === mutationGenerationRef.current
        && requestConnectionIdentity === connectionIdentityRef.current) {
        setFavoriteError(err instanceof Error ? err.message : String(err));
      }
      return false;
    } finally {
      if (mutationGeneration === mutationGenerationRef.current
        && requestConnectionIdentity === connectionIdentityRef.current) {
        mutationBusyRef.current = false;
        setFavoriteBusyKey('');
      }
    }
  }, [apiToken, connectionPaused]);

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
    mutationGenerationRef.current += 1;
    refreshGenerationRef.current += 1;
    mutationBusyRef.current = false;
    setFavoriteBusyKey('');
    setFavoriteRefreshing(false);
  }, [connectionIdentity]);

  useEffect(() => {
    void refreshFavorites();
  }, [refreshFavorites]);

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
