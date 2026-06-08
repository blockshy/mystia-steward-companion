import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode, SetStateAction } from 'react';
import { FolderOpen, Power, RefreshCw, Star } from 'lucide-react';
import { CustomerScoreBadges } from '@/components/ScoreBadge';
import { RegionSelector } from '@/components/RegionSelector';
import { TagBadge } from '@/components/TagBadge';
import { useGamepadNavigation } from '@/companion/use-gamepad-navigation';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Card, CardContent } from '@/components/ui/card';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import {
  computeNormalBeverageResults,
  computeNormalRecipeResults,
  getNormalCustomersByPlace,
} from '@/lib/normal-recommend';
import {
  getAllRareCustomers,
  getRareCustomersByPlace,
  rankBeveragesForRare,
  rankRecipesForRare,
} from '@/lib/rare-recommend';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import { useThemeMode } from '@/lib/theme';
import type { ThemeMode } from '@/lib/theme';
import type {
  IBeverage,
  ICustomerRare,
  IIngredient,
  INormalBeverageResult,
  INormalRecipeResult,
  IRareBeverageResult,
  IRareRecipeResult,
  TPlace,
  TRating,
} from '@/lib/types';
import { ALL_PLACES } from '@/lib/types';
import allIngredients from '@/data/ingredients.json';
import allBeverages from '@/data/beverages.json';

const DEFAULT_ENDPOINT = 'http://127.0.0.1:32145';
const STORAGE_PREFIX = 'mystia-steward-companion';
const LEGACY_STORAGE_PREFIX = 'mystia-steward';
const ENDPOINT_STORAGE_KEY = `${STORAGE_PREFIX}-mod-api-endpoint`;
const TOKEN_STORAGE_KEY = `${STORAGE_PREFIX}-mod-api-token`;
const TAB_STORAGE_KEY = `${STORAGE_PREFIX}-mod-tab`;
const FOCUS_COMPACT_STORAGE_KEY = `${STORAGE_PREFIX}-service-focus-compact`;
const FOCUS_RECIPE_LIMIT_STORAGE_KEY = `${STORAGE_PREFIX}-service-focus-recipe-limit`;
const FOCUS_BEVERAGE_LIMIT_STORAGE_KEY = `${STORAGE_PREFIX}-service-focus-beverage-limit`;
const WINDOW_OPACITY_STORAGE_KEY = `${STORAGE_PREFIX}-window-opacity`;
const FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY = `${STORAGE_PREFIX}-focus-switch-behavior`;
const FOCUS_SWITCH_COOLDOWN_STORAGE_KEY = `${STORAGE_PREFIX}-focus-switch-cooldown-ms`;
const ALWAYS_ON_TOP_STORAGE_KEY = `${STORAGE_PREFIX}-always-on-top`;
const GAMEPAD_NAVIGATION_STORAGE_KEY = `${STORAGE_PREFIX}-gamepad-navigation`;
const AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-take-beverage`;
const AUTO_PREP_START_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-start-cooking`;
const AUTO_PREP_COLLECT_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-collect-cooking`;
const AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-favorites-only`;
const AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-stop-on-error`;
const LEGACY_ENDPOINT_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-api-endpoint`;
const LEGACY_TOKEN_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-api-token`;
const LEGACY_TAB_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-tab`;
const MAX_RECOMMENDATION_ROWS = 8;
const MAX_FOCUS_RECOMMENDATION_ROWS = 20;
const DEFAULT_FOCUS_RECOMMENDATION_ROWS = 8;
const DEFAULT_WINDOW_OPACITY = 0.96;
const MIN_WINDOW_OPACITY = 0.2;
const DEFAULT_FOCUS_SWITCH_COOLDOWN_MS = 800;
const MIN_FOCUS_SWITCH_COOLDOWN_MS = 250;
const MAX_FOCUS_SWITCH_COOLDOWN_MS = 2000;
const MAX_LOG_LINES_IN_VIEW = 400;
const NON_ORDERABLE_RARE_FOOD_TAGS = new Set(['流行喜爱', '流行厌恶']);
const INGREDIENTS = allIngredients as IIngredient[];
const INGREDIENT_BY_NAME = new Map(INGREDIENTS.map((ingredient) => [ingredient.name, ingredient]));
const INGREDIENT_ID_BY_NAME = new Map(INGREDIENTS.map((ingredient) => [ingredient.name, ingredient.id]));
const INGREDIENT_NAME_BY_ID = new Map(INGREDIENTS.map((ingredient) => [ingredient.id, ingredient.name]));
const BEVERAGES = allBeverages as IBeverage[];
const BEVERAGE_NAME_BY_ID = new Map(BEVERAGES.map((beverage) => [beverage.id, beverage.name]));
const LOW_STOCK_RESOURCE_THRESHOLD = 5;
const EXTRA_INGREDIENT_RESOURCE_WEIGHT = 2;
const DENSE_TWO_COLUMN_GRID = 'grid grid-cols-2 gap-4';
const DENSE_TWO_COLUMN_GRID_TIGHT = 'grid grid-cols-2 gap-2';
const DENSE_THREE_COLUMN_GRID = 'grid grid-cols-3 gap-3';
const DENSE_FOUR_COLUMN_GRID = 'grid grid-cols-4 gap-3';
const DENSE_CARD_HEADER_GRID = 'grid grid-cols-[minmax(0,1fr)_auto] gap-3';
const DENSE_ITEM_GRID = 'grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-2';
const MOD_TAB_TRIGGER_CLASS = 'min-w-0 flex-1 data-active:bg-primary data-active:text-primary-foreground dark:data-active:bg-primary dark:data-active:text-primary-foreground';

type ModTab = 'overview' | 'normal' | 'rare' | 'service' | 'inventory' | 'logs' | 'settings';
const MOD_TABS: ModTab[] = ['overview', 'normal', 'rare', 'service', 'inventory', 'logs', 'settings'];
type FocusSwitchBehavior = 'hide' | 'keep-visible';

const RATING_LABELS: Record<TRating, string> = {
  ExGood: '完美',
  Good: '满意',
  Normal: '普通',
  Bad: '不满',
  ExBad: '极差',
};

interface RecommendationStateSnapshot {
  availableRecipeIds: number[];
  availableBeverageIds: number[];
  availableIngredientIds: number[];
  ownedIngredientQty: Record<string, number>;
  ownedBeverageQty: Record<string, number>;
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
}

interface NightBusinessGuest {
  deskCode: number;
  guestId: number | null;
  guestName: string;
  source: string;
}

interface NightBusinessOrder {
  deskCode: number;
  guestId: number | null;
  guestName: string;
  foodTagId: number;
  foodTag: string;
  beverageTagId: number;
  beverageTag: string;
  source: string;
  firstSeenAtUtc?: string | null;
  lastSeenAtUtc?: string | null;
}

interface NightBusinessContext {
  place: string | null;
  placeLabel: string | null;
  activeRareGuests: NightBusinessGuest[];
  orders: NightBusinessOrder[];
  source: string;
  error: string | null;
}

interface RuntimeRareCustomer {
  id: number;
  runtimeStringId: string;
  name: string;
  places: string[];
  positiveTags: string[];
  negativeTags: string[];
  beverageTags: string[];
  source: string;
}

interface LocalApiSnapshot {
  pluginVersion: string;
  capturedAtUtc: string;
  activeSceneName: string;
  runtimeLoaded: boolean;
  status: string;
  runtimeSource: string;
  dataDirectory: string;
  recommendationState: RecommendationStateSnapshot | null;
  nightBusiness: NightBusinessContext | null;
  runtimeRareCustomers?: RuntimeRareCustomer[];
}

interface RuntimeSets {
  recipeIds: Set<number>;
  beverageIds: Set<number>;
  ingredientIds: Set<number>;
  unavailableIngredientIds: Set<number>;
  ownedIngredientQty: Record<number, number>;
  ownedBeverageQty: Record<number, number>;
}

interface CachedRecommendation {
  customer: ICustomerRare;
  recipes: IRareRecipeResult[];
  beverages: IRareBeverageResult[];
}

interface OrderRecommendation extends CachedRecommendation {
  order: NightBusinessOrder;
}

interface RecommendationIssue {
  order: NightBusinessOrder;
  message: string;
}

interface LocalApiLogs {
  capturedAtUtc: string;
  path: string;
  exists: boolean;
  enabled: boolean;
  maxLines?: number;
  maxBytes?: number;
  lines: string[];
  error: string | null;
}

interface LocalApiLogSettings {
  logAccessEnabled: boolean;
  logOutputPath: string;
  logOutputDirectory: string;
  maxLogLines?: number;
  maxLogBytes?: number;
  nightBusinessDiagnosticsEnabled: boolean;
  nightBusinessDiagnosticsPath: string;
  nightBusinessDiagnosticsDirectory: string;
}

interface LocalApiFolderResponse {
  ok: boolean;
  directory: string;
  error: string | null;
}

interface InventoryEditResponse {
  ok: boolean;
  type: 'ingredient' | 'beverage';
  id: number;
  requestedQuantity: number;
  previousQuantity: number;
  quantity: number;
  changed: boolean;
  error: string | null;
}

interface OrderPreparationStep {
  name: string;
  ok: boolean;
  skipped: boolean;
  message: string;
}

interface OrderPreparationResponse {
  ok: boolean;
  prepared: boolean;
  error: string | null;
  order: {
    deskCode: number;
    guestId: number | null;
    guestName: string;
    foodTag: string;
    beverageTag: string;
  };
  recipeId: number;
  recipeName: string;
  beverageId: number;
  beverageName: string;
  steps: OrderPreparationStep[];
}

interface FavoriteData {
  version: number;
  recipes: FavoriteRecipeEntry[];
  beverages: FavoriteBeverageEntry[];
}

interface FavoriteRecipeEntry {
  id: string;
  customerId: number;
  customerName: string;
  foodTag: string;
  recipeId: number;
  extraIngredientIds: number[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

interface FavoriteBeverageEntry {
  id: string;
  customerId: number;
  customerName: string;
  beverageTag: string;
  beverageId: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

interface FavoriteMutationResponse {
  ok: boolean;
  favorites: FavoriteData;
  error: string | null;
}

interface CompanionPreferences {
  windowOpacity: number;
  focusSwitchBehavior: FocusSwitchBehavior;
  focusSwitchCooldownMs: number;
  alwaysOnTop: boolean;
  gamepadNavigationEnabled: boolean;
  autoPrepTakeBeverage: boolean;
  autoPrepStartCooking: boolean;
  autoPrepCollectCooking: boolean;
  autoPrepFavoritesOnly: boolean;
  autoPrepStopOnError: boolean;
}

type ToggleRecipeFavorite = (customer: ICustomerRare, foodTag: string, recipe: IRareRecipeResult) => Promise<void>;
type ToggleBeverageFavorite = (customer: ICustomerRare, beverageTag: string, beverage: IRareBeverageResult) => Promise<void>;

export function ModWorkbench() {
  const { mode: themeMode, setMode: setThemeMode } = useThemeMode();
  const [endpoint, setEndpoint] = useState(() =>
    readMigratedStorage(ENDPOINT_STORAGE_KEY, LEGACY_ENDPOINT_STORAGE_KEY, DEFAULT_ENDPOINT),
  );
  const [apiToken, setApiToken] = useState(() =>
    readMigratedStorage(TOKEN_STORAGE_KEY, LEGACY_TOKEN_STORAGE_KEY, ''),
  );
  const [tab, setTab] = useState<ModTab>(() => readStoredTab());
  const [serviceFocusMode, setServiceFocusMode] = useState(false);
  const [serviceFocusCompact, setServiceFocusCompact] = useState(() =>
    readStoredBoolean(FOCUS_COMPACT_STORAGE_KEY, false),
  );
  const [serviceFocusRecipeLimit, setServiceFocusRecipeLimit] = useState(() =>
    readStoredFocusLimit(FOCUS_RECIPE_LIMIT_STORAGE_KEY),
  );
  const [serviceFocusBeverageLimit, setServiceFocusBeverageLimit] = useState(() =>
    readStoredFocusLimit(FOCUS_BEVERAGE_LIMIT_STORAGE_KEY),
  );
  const [companionPreferences, setCompanionPreferences] = useState<CompanionPreferences>(() =>
    readStoredCompanionPreferences(),
  );
  const [snapshot, setSnapshot] = useState<LocalApiSnapshot | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [lastConnectedAt, setLastConnectedAt] = useState<Date | null>(null);
  const [manualPlace, setManualPlace] = useState<TPlace | null>(null);
  const [rareCustomerId, setRareCustomerId] = useState<number | null>(null);
  const [requiredFoodTag, setRequiredFoodTag] = useState('');
  const [requiredBeverageTag, setRequiredBeverageTag] = useState('');
  const [favorites, setFavorites] = useState<FavoriteData>(() => emptyFavoriteData());
  const [favoriteError, setFavoriteError] = useState('');
  const [favoriteBusyKey, setFavoriteBusyKey] = useState('');
  const [autoPrepBusy, setAutoPrepBusy] = useState(false);
  const [autoPrepMessage, setAutoPrepMessage] = useState('');
  const recommendationCacheRef = useRef(new Map<string, CachedRecommendation>());
  const refreshInFlightRef = useRef(false);

  const updateCompanionPreferences = useCallback((next: Partial<CompanionPreferences>) => {
    setCompanionPreferences((current) => normalizeCompanionPreferences({ ...current, ...next }));
  }, []);

  const normalizedEndpoint = useMemo(() => normalizeEndpoint(endpoint), [endpoint]);
  const runtime = snapshot?.recommendationState ?? null;
  const night = snapshot?.nightBusiness ?? null;
  const detectedPlace = normalizePlace(night?.place);
  const selectedPlace = manualPlace ?? detectedPlace;
  const runtimeRareCustomers = useMemo(
    () => (snapshot?.runtimeRareCustomers ?? []).map(toRuntimeRareCustomer),
    [snapshot?.runtimeRareCustomers],
  );
  const rareCustomersById = useMemo(
    () => buildRareCustomerMap(runtimeRareCustomers),
    [runtimeRareCustomers],
  );

  const runtimeSets = useMemo(() => buildRuntimeSets(runtime), [runtime]);
  const orderRecommendations = useMemo(
    () => buildOrderRecommendations(night?.orders ?? [], runtime, rareCustomersById, recommendationCacheRef.current, favorites),
    [night?.orders, runtime, rareCustomersById, favorites],
  );
  const snapshotRefreshIntervalMs = tab === 'service' || serviceFocusMode ? 750 : 2000;

  const refresh = useCallback(async () => {
    if (refreshInFlightRef.current) return;
    refreshInFlightRef.current = true;
    setLoading(true);
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);

    try {
      const data = await readSnapshot(normalizedEndpoint, apiToken, abortController.signal);
      setSnapshot(data);
      setError('');
      setLastConnectedAt(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      refreshInFlightRef.current = false;
      setLoading(false);
    }
  }, [apiToken, normalizedEndpoint]);

  const refreshFavorites = useCallback(async () => {
    if (!apiToken) {
      setFavorites(emptyFavoriteData());
      return;
    }

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);

    try {
      const data = await readFavorites(normalizedEndpoint, apiToken, abortController.signal);
      setFavorites(normalizeFavoriteData(data));
      setFavoriteError('');
    } catch (err) {
      setFavoriteError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
    }
  }, [apiToken, normalizedEndpoint]);

  const toggleRecipeFavorite = useCallback<ToggleRecipeFavorite>(async (customer, foodTag, recipe) => {
    if (!apiToken || !foodTag) return;
    const existing = findRecipeFavorite(favorites, customer.id, foodTag, recipe);
    const busyKey = existing?.id ?? recipeFavoriteKey(customer.id, foodTag, recipe);
    setFavoriteBusyKey(busyKey);
    setFavoriteError('');

    try {
      const response = existing
        ? await removeRecipeFavorite(normalizedEndpoint, apiToken, existing.id)
        : await addRecipeFavorite(normalizedEndpoint, apiToken, customer, foodTag, recipe);
      if (!response.ok) throw new Error(response.error || '收藏更新失败');
      setFavorites(normalizeFavoriteData(response.favorites));
    } catch (err) {
      setFavoriteError(err instanceof Error ? err.message : String(err));
    } finally {
      setFavoriteBusyKey('');
    }
  }, [apiToken, favorites, normalizedEndpoint]);

  const toggleBeverageFavorite = useCallback<ToggleBeverageFavorite>(async (customer, beverageTag, beverage) => {
    if (!apiToken || !beverageTag) return;
    const existing = findBeverageFavorite(favorites, customer.id, beverageTag, beverage);
    const busyKey = existing?.id ?? beverageFavoriteKey(customer.id, beverageTag, beverage);
    setFavoriteBusyKey(busyKey);
    setFavoriteError('');

    try {
      const response = existing
        ? await removeBeverageFavorite(normalizedEndpoint, apiToken, existing.id)
        : await addBeverageFavorite(normalizedEndpoint, apiToken, customer, beverageTag, beverage);
      if (!response.ok) throw new Error(response.error || '收藏更新失败');
      setFavorites(normalizeFavoriteData(response.favorites));
    } catch (err) {
      setFavoriteError(err instanceof Error ? err.message : String(err));
    } finally {
      setFavoriteBusyKey('');
    }
  }, [apiToken, favorites, normalizedEndpoint]);

  const prepareNextOrder = useCallback(async () => {
    setAutoPrepMessage('');
    if (!apiToken) {
      setAutoPrepMessage('本地 API Token 不可用，无法执行准备下一单。');
      return;
    }

    const selection = selectNextOrderPreparation(orderRecommendations.recommendations, favorites, companionPreferences);
    if (!selection.ok) {
      setAutoPrepMessage(selection.message);
      return;
    }

    setAutoPrepBusy(true);
    try {
      const response = await prepareNextRareOrder(
        normalizedEndpoint,
        apiToken,
        selection.item,
        selection.recipe,
        selection.beverage,
        selection.recipeFavorite,
        selection.beverageFavorite,
        companionPreferences,
      );
      setAutoPrepMessage(formatOrderPreparationResponse(response));
      await refresh();
    } catch (err) {
      setAutoPrepMessage(err instanceof Error ? err.message : String(err));
    } finally {
      setAutoPrepBusy(false);
    }
  }, [apiToken, companionPreferences, favorites, normalizedEndpoint, orderRecommendations.recommendations, refresh]);

  const completeFirstOrder = useCallback(async () => {
    setAutoPrepMessage('');
    if (!apiToken) {
      setAutoPrepMessage('本地 API Token 不可用，无法执行完成第一单。');
      return;
    }

    const completePreferences = {
      ...companionPreferences,
      autoPrepTakeBeverage: true,
      autoPrepStartCooking: true,
    };
    const selection = selectNextOrderPreparation(orderRecommendations.recommendations, favorites, completePreferences);
    if (!selection.ok) {
      setAutoPrepMessage(selection.message);
      return;
    }

    setAutoPrepBusy(true);
    try {
      const response = await completeFirstRareOrder(
        normalizedEndpoint,
        apiToken,
        selection.item,
        selection.recipe,
        selection.beverage,
        selection.recipeFavorite,
        selection.beverageFavorite,
        completePreferences,
      );
      setAutoPrepMessage(formatOrderPreparationResponse(response));
      await refresh();
    } catch (err) {
      setAutoPrepMessage(err instanceof Error ? err.message : String(err));
    } finally {
      setAutoPrepBusy(false);
    }
  }, [apiToken, companionPreferences, favorites, normalizedEndpoint, orderRecommendations.recommendations, refresh]);

  useEffect(() => {
    localStorage.setItem(ENDPOINT_STORAGE_KEY, normalizedEndpoint);
  }, [normalizedEndpoint]);

  useEffect(() => {
    if (apiToken) localStorage.setItem(TOKEN_STORAGE_KEY, apiToken);
  }, [apiToken]);

  useEffect(() => {
    localStorage.setItem(TAB_STORAGE_KEY, tab);
  }, [tab]);

  useEffect(() => {
    localStorage.setItem(FOCUS_COMPACT_STORAGE_KEY, serviceFocusCompact ? '1' : '0');
  }, [serviceFocusCompact]);

  useEffect(() => {
    localStorage.setItem(FOCUS_RECIPE_LIMIT_STORAGE_KEY, String(serviceFocusRecipeLimit));
  }, [serviceFocusRecipeLimit]);

  useEffect(() => {
    localStorage.setItem(FOCUS_BEVERAGE_LIMIT_STORAGE_KEY, String(serviceFocusBeverageLimit));
  }, [serviceFocusBeverageLimit]);

  useEffect(() => {
    persistCompanionPreferences(companionPreferences);
    applyCompanionVisualPreferences(companionPreferences);
  }, [companionPreferences]);

  useEffect(() => {
    void applyCompanionPreferencesToTauri(
      companionPreferences.focusSwitchBehavior,
      companionPreferences.alwaysOnTop,
      companionPreferences.focusSwitchCooldownMs,
    );
  }, [
    companionPreferences.alwaysOnTop,
    companionPreferences.focusSwitchBehavior,
    companionPreferences.focusSwitchCooldownMs,
  ]);

  useEffect(() => {
    if (!isTauriRuntime()) return;

    let disposed = false;
    import('@tauri-apps/api/core')
      .then(async ({ invoke }) => {
        const [launchEndpoint, launchToken] = await Promise.all([
          invoke<string | null>('launch_api_endpoint'),
          invoke<string | null>('launch_api_token'),
        ]);
        return { launchEndpoint, launchToken };
      })
      .then(({ launchEndpoint, launchToken }) => {
        if (!disposed && launchEndpoint) setEndpoint(launchEndpoint);
        if (!disposed && launchToken) setApiToken(launchToken);
      })
      .catch(() => {
        // Browser mode does not expose launch arguments.
      });

    return () => {
      disposed = true;
    };
  }, []);

  useGamepadNavigation({
    enabled: companionPreferences.gamepadNavigationEnabled,
    toggleCooldownMs: companionPreferences.focusSwitchCooldownMs,
    activeTab: tab,
    tabs: MOD_TABS,
    focusMode: serviceFocusMode,
    onTabChange: setTab,
    onToggleWindow: () => {
      void toggleCompanionFocus(
        companionPreferences.focusSwitchBehavior,
        companionPreferences.focusSwitchCooldownMs,
      );
    },
    onEnterFocusMode: () => {
      setTab('service');
      setServiceFocusMode(true);
    },
    onExitFocusMode: () => setServiceFocusMode(false),
    onToggleCompactMode: () => setServiceFocusCompact((current) => !current),
  });

  useEffect(() => {
    refresh();
    const timer = window.setInterval(refresh, snapshotRefreshIntervalMs);
    return () => window.clearInterval(timer);
  }, [refresh, snapshotRefreshIntervalMs]);

  useEffect(() => {
    refreshFavorites();
  }, [refreshFavorites]);

  if (serviceFocusMode) {
    return (
      <ServiceFocusPage
        recommendations={orderRecommendations.recommendations}
        recommendationIssues={orderRecommendations.recommendationIssues}
        runtimeSets={runtimeSets}
        favorites={favorites}
        favoriteBusyKey={favoriteBusyKey}
        favoriteError={favoriteError}
        autoPrepBusy={autoPrepBusy}
        autoPrepMessage={autoPrepMessage}
        autoPrepPreferences={companionPreferences}
        compact={serviceFocusCompact}
        recipeLimit={serviceFocusRecipeLimit}
        beverageLimit={serviceFocusBeverageLimit}
        onPrepareNextOrder={prepareNextOrder}
        onCompleteFirstOrder={completeFirstOrder}
        onCompactChange={setServiceFocusCompact}
        onRecipeLimitChange={setServiceFocusRecipeLimit}
        onBeverageLimitChange={setServiceFocusBeverageLimit}
        onToggleRecipeFavorite={toggleRecipeFavorite}
        onToggleBeverageFavorite={toggleBeverageFavorite}
        onExit={() => setServiceFocusMode(false)}
      />
    );
  }

  return (
    <div className="space-y-5">
      <div className="flex flex-wrap items-end justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold text-foreground">Mod 工作台</h1>
          <p className="mt-1 text-sm text-muted-foreground">
            {snapshot ? `mystia-steward-companion ${snapshot.pluginVersion}` : '等待本地 API 响应'}
          </p>
        </div>
        <div className="flex w-full max-w-xl items-center gap-2">
          <Input
            value={endpoint}
            onChange={(event) => setEndpoint(event.target.value)}
            spellCheck={false}
            className="font-mono text-xs"
          />
          <Button size="sm" onClick={refresh} disabled={loading}>
            <RefreshCw className={loading ? 'size-4 animate-spin' : 'size-4'} />
            刷新
          </Button>
        </div>
      </div>

      <div className={DENSE_THREE_COLUMN_GRID}>
        <StatusCard
          label="连接状态"
          value={error ? '未连接' : snapshot ? '已连接' : '连接中'}
          detail={error || (lastConnectedAt ? `最近响应 ${formatTime(lastConnectedAt)}` : normalizedEndpoint)}
          tone={error ? 'bad' : snapshot ? 'good' : 'neutral'}
        />
        <StatusCard
          label="游戏运行态"
          value={snapshot?.runtimeLoaded ? '已加载' : '未加载'}
          detail={snapshot?.activeSceneName || snapshot?.status || '暂无快照'}
          tone={snapshot?.runtimeLoaded ? 'good' : 'neutral'}
        />
        <StatusCard
          label="经营数据"
          value={`${night?.activeRareGuests.length ?? 0} 稀客 / ${night?.orders.length ?? 0} 点单`}
          detail={night?.place || night?.placeLabel || '无经营场景'}
          tone={(night?.orders.length ?? 0) > 0 ? 'good' : 'neutral'}
        />
      </div>

      <Tabs value={tab} onValueChange={(value) => setTab(value as ModTab)} className="space-y-4">
        <TabsList
          className="h-9 !w-full max-w-full justify-stretch overflow-x-auto overflow-y-hidden [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
          data-gamepad-scope="tabs"
        >
          <TabsTrigger value="overview" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="overview">
            概览
          </TabsTrigger>
          <TabsTrigger value="normal" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="normal">
            普客
          </TabsTrigger>
          <TabsTrigger value="rare" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="rare">
            稀客
          </TabsTrigger>
          <TabsTrigger value="service" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="service">
            经营中
          </TabsTrigger>
          <TabsTrigger value="inventory" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="inventory">
            修改
          </TabsTrigger>
          <TabsTrigger value="logs" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="logs">
            日志
          </TabsTrigger>
          <TabsTrigger value="settings" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="settings">
            设置
          </TabsTrigger>
        </TabsList>

        <TabsContent value="overview" data-gamepad-scope="content">
          <ModOverviewPanel
            endpoint={normalizedEndpoint}
            snapshot={snapshot}
            runtime={runtime}
            night={night}
            error={error}
            lastConnectedAt={lastConnectedAt}
          />
        </TabsContent>

        <TabsContent value="normal" data-gamepad-scope="content">
          <ModNormalPanel
            runtime={runtime}
            runtimeSets={runtimeSets}
            selectedPlace={selectedPlace}
            detectedPlace={detectedPlace}
            onPlaceChange={setManualPlace}
            onFollowDetectedPlace={() => setManualPlace(null)}
          />
        </TabsContent>

        <TabsContent value="rare" data-gamepad-scope="content">
          <ModRarePanel
            runtime={runtime}
            runtimeSets={runtimeSets}
            runtimeRareCustomers={runtimeRareCustomers}
            selectedPlace={selectedPlace}
            detectedPlace={detectedPlace}
            rareCustomerId={rareCustomerId}
            requiredFoodTag={requiredFoodTag}
            requiredBeverageTag={requiredBeverageTag}
            favorites={favorites}
            favoriteBusyKey={favoriteBusyKey}
            favoriteError={favoriteError}
            onPlaceChange={(place) => {
              setManualPlace(place);
              setRareCustomerId(null);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onFollowDetectedPlace={() => {
              setManualPlace(null);
              setRareCustomerId(null);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onRareCustomerChange={(customerId) => {
              setRareCustomerId(customerId);
              setRequiredFoodTag('');
              setRequiredBeverageTag('');
            }}
            onFoodTagChange={setRequiredFoodTag}
            onBeverageTagChange={setRequiredBeverageTag}
            onToggleRecipeFavorite={toggleRecipeFavorite}
            onToggleBeverageFavorite={toggleBeverageFavorite}
          />
        </TabsContent>

        <TabsContent value="service" data-gamepad-scope="content">
          <ModServicePanel
            runtime={runtime}
            night={night}
            detectedPlace={detectedPlace}
            recommendations={orderRecommendations.recommendations}
            recommendationIssues={orderRecommendations.recommendationIssues}
            runtimeSets={runtimeSets}
            favorites={favorites}
            favoriteBusyKey={favoriteBusyKey}
            favoriteError={favoriteError}
            autoPrepBusy={autoPrepBusy}
            autoPrepMessage={autoPrepMessage}
            autoPrepPreferences={companionPreferences}
            onToggleRecipeFavorite={toggleRecipeFavorite}
            onToggleBeverageFavorite={toggleBeverageFavorite}
            onPrepareNextOrder={prepareNextOrder}
            onCompleteFirstOrder={completeFirstOrder}
            onEnterFocusMode={() => setServiceFocusMode(true)}
          />
        </TabsContent>

        <TabsContent value="inventory" data-gamepad-scope="content">
          <ModInventoryPanel
            endpoint={normalizedEndpoint}
            apiToken={apiToken}
            runtimeSets={runtimeSets}
            runtimeLoaded={snapshot?.runtimeLoaded ?? false}
            onRefresh={refresh}
          />
        </TabsContent>

        <TabsContent value="logs" data-gamepad-scope="content">
          <ModLogsPanel endpoint={normalizedEndpoint} apiToken={apiToken} />
        </TabsContent>

        <TabsContent value="settings" data-gamepad-scope="content">
          <ModSettingsPanel
            preferences={companionPreferences}
            themeMode={themeMode}
            serviceFocusCompact={serviceFocusCompact}
            serviceFocusRecipeLimit={serviceFocusRecipeLimit}
            serviceFocusBeverageLimit={serviceFocusBeverageLimit}
            onPreferenceChange={updateCompanionPreferences}
            onThemeModeChange={setThemeMode}
            onServiceFocusCompactChange={setServiceFocusCompact}
            onServiceFocusRecipeLimitChange={setServiceFocusRecipeLimit}
            onServiceFocusBeverageLimitChange={setServiceFocusBeverageLimit}
          />
        </TabsContent>
      </Tabs>
    </div>
  );
}

function ModOverviewPanel({
  endpoint,
  snapshot,
  runtime,
  night,
  error,
  lastConnectedAt,
}: {
  endpoint: string;
  snapshot: LocalApiSnapshot | null;
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  error: string;
  lastConnectedAt: Date | null;
}) {
  const ownedIngredientEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedIngredientQty ?? {}, INGREDIENT_NAME_BY_ID),
    [runtime?.ownedIngredientQty],
  );
  const ownedBeverageEntries = useMemo(
    () => buildLowStockEntries(runtime?.ownedBeverageQty ?? {}, BEVERAGE_NAME_BY_ID),
    [runtime?.ownedBeverageQty],
  );

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className={`${DENSE_TWO_COLUMN_GRID_TIGHT} p-4 text-sm`}>
          <InfoLine label="数据来源" value="游戏实时 API，不读取 .memory 存档" />
          <InfoLine label="API 地址" value={endpoint} mono />
          <InfoLine label="连接状态" value={error ? `未连接: ${error}` : snapshot ? '已连接' : '连接中'} />
          <InfoLine label="最近响应" value={lastConnectedAt ? formatTime(lastConnectedAt) : '暂无'} />
          <InfoLine label="场景" value={snapshot?.activeSceneName || '未知'} />
          <InfoLine label="运行时状态" value={snapshot?.status || '暂无快照'} />
          <InfoLine label="运行时来源" value={snapshot?.runtimeSource || '未知'} />
          <InfoLine label="数据目录" value={snapshot?.dataDirectory || '未知'} mono />
        </CardContent>
      </Card>

      <Card>
        <CardContent className={`${DENSE_FOUR_COLUMN_GRID} p-4 text-sm`}>
          <Metric label="可用料理" value={runtime?.availableRecipeIds.length ?? 0} />
          <Metric label="可用酒水" value={runtime?.availableBeverageIds.length ?? 0} />
          <Metric label="可用食材" value={runtime?.availableIngredientIds.length ?? 0} />
          <Metric label="明星店" value={runtime?.famousShopEnabled ? '开启' : '关闭'} />
        </CardContent>
      </Card>

      <div className={DENSE_TWO_COLUMN_GRID}>
        <ListPanel title="快捷键">
          <div className="grid gap-2 text-sm">
            <InfoLine label="F8" value="在游戏与独立窗口之间切换；若启用旧游戏内面板，则打开或关闭游戏内面板" />
            <InfoLine label="RS Click" value="手柄默认在游戏与独立窗口之间切换" />
            <InfoLine label="手柄导航" value="左摇杆/十字键移动，A 确认，B 返回，LB/RB 切换页面，LT/RT 滚动" />
            <InfoLine label="专注模式" value="Y 进入专注模式或切换精简模式，X 收藏当前推荐项" />
            <InfoLine label="F9" value="刷新游戏运行时数据检测" />
            <InfoLine label="窗口关闭" value="关闭按钮会隐藏到托盘；托盘菜单可重新显示或退出" />
          </div>
        </ListPanel>

        <ListPanel title="实时标签">
          <InfoLine label="流行喜爱" value={runtime?.popularFoodTag || '无'} />
          <InfoLine label="流行厌恶" value={runtime?.popularHateFoodTag || '无'} />
          <InfoLine label="当前经营场景" value={night?.place || night?.placeLabel || '无经营场景'} />
          <InfoLine label="经营扫描" value={night?.source || '暂无'} />
        </ListPanel>

        <ListPanel title="低库存概览">
          <div className={DENSE_TWO_COLUMN_GRID}>
            <LowStockColumn title="材料" entries={ownedIngredientEntries} />
            <LowStockColumn title="酒水" entries={ownedBeverageEntries} />
          </div>
        </ListPanel>
      </div>
    </div>
  );
}

function ModNormalPanel({
  runtime,
  runtimeSets,
  selectedPlace,
  detectedPlace,
  onPlaceChange,
  onFollowDetectedPlace,
}: {
  runtime: RecommendationStateSnapshot | null;
  runtimeSets: RuntimeSets | null;
  selectedPlace: TPlace | null;
  detectedPlace: TPlace | null;
  onPlaceChange: (place: TPlace) => void;
  onFollowDetectedPlace: () => void;
}) {
  const recipes = useMemo(() => {
    if (!runtime || !runtimeSets || !selectedPlace) return [];
    return computeNormalRecipeResults(
      selectedPlace,
      runtimeSets.recipeIds,
      runtimeSets.unavailableIngredientIds,
      runtime.popularFoodTag,
      runtime.popularHateFoodTag,
      runtime.famousShopEnabled,
    )
      .sort(compareNormalRecipesForMod)
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [runtime, runtimeSets, selectedPlace]);

  const beverages = useMemo(() => {
    if (!runtimeSets || !selectedPlace) return [];
    return computeNormalBeverageResults(selectedPlace, runtimeSets.beverageIds)
      .sort(compareNormalBeveragesForMod)
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [runtimeSets, selectedPlace]);

  const customers = useMemo(
    () => (selectedPlace ? getNormalCustomersByPlace(selectedPlace) : []),
    [selectedPlace],
  );

  if (!runtime || !runtimeSets) return <RuntimeUnavailable />;

  return (
    <div className="space-y-4">
      <PlaceToolbar
        selectedPlace={selectedPlace}
        detectedPlace={detectedPlace}
        onPlaceChange={onPlaceChange}
        onFollowDetectedPlace={onFollowDetectedPlace}
      />

      {!selectedPlace && <EmptyState text="请选择地区后查看普客推荐" />}

      {selectedPlace && (
        <div className={DENSE_TWO_COLUMN_GRID}>
          <ListPanel title={`料理推荐 (${recipes.length})`}>
            {recipes.length === 0 && <EmptyRow text="暂无可推荐料理" />}
            <div className="space-y-2">
              {recipes.map((recipe, index) => (
                <NormalRecipeRow
                  key={recipe.recipe.id}
                  recipe={recipe}
                  index={index}
                  ownedIngredientQty={runtimeSets.ownedIngredientQty}
                />
              ))}
            </div>
          </ListPanel>

          <ListPanel title={`酒水推荐 (${beverages.length})`}>
            {beverages.length === 0 && <EmptyRow text="暂无可推荐酒水" />}
            <div className="space-y-2">
              {beverages.map((beverage, index) => (
                <NormalBeverageRow
                  key={beverage.beverage.id}
                  beverage={beverage}
                  index={index}
                  ownedBeverageQty={runtimeSets.ownedBeverageQty}
                />
              ))}
            </div>
          </ListPanel>
        </div>
      )}

      {selectedPlace && (
        <ListPanel title={`地区普客 (${customers.length})`}>
          <div className={DENSE_ITEM_GRID}>
            {customers.map((customer) => (
              <div key={customer.id} className="rounded-md border border-border/80 p-2 text-sm">
                <div className="font-medium">{customer.name}</div>
                <div className="mt-1 flex flex-wrap gap-1">
                  {customer.positiveTags.map((tag) => <TagBadge key={tag} tag={tag} variant="preferred" />)}
                  {customer.beverageTags.map((tag) => <TagBadge key={tag} tag={tag} variant="default" />)}
                </div>
              </div>
            ))}
          </div>
        </ListPanel>
      )}
    </div>
  );
}

function ModRarePanel({
  runtime,
  runtimeSets,
  runtimeRareCustomers,
  selectedPlace,
  detectedPlace,
  rareCustomerId,
  requiredFoodTag,
  requiredBeverageTag,
  favorites,
  favoriteBusyKey,
  favoriteError,
  onPlaceChange,
  onFollowDetectedPlace,
  onRareCustomerChange,
  onFoodTagChange,
  onBeverageTagChange,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
}: {
  runtime: RecommendationStateSnapshot | null;
  runtimeSets: RuntimeSets | null;
  runtimeRareCustomers: ICustomerRare[];
  selectedPlace: TPlace | null;
  detectedPlace: TPlace | null;
  rareCustomerId: number | null;
  requiredFoodTag: string;
  requiredBeverageTag: string;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  onPlaceChange: (place: TPlace) => void;
  onFollowDetectedPlace: () => void;
  onRareCustomerChange: (customerId: number | null) => void;
  onFoodTagChange: (tag: string) => void;
  onBeverageTagChange: (tag: string) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
}) {
  const customers = useMemo(() => {
    if (!selectedPlace) return [];
    return mergeRareCustomers(
      getRareCustomersByPlace(selectedPlace),
      runtimeRareCustomers.filter((customer) => customer.places.includes(selectedPlace)),
    );
  }, [runtimeRareCustomers, selectedPlace]);
  const selectedCustomer = customers.find((customer) => customer.id === rareCustomerId) ?? customers[0] ?? null;
  const foodTag = requiredFoodTag || selectedCustomer?.positiveTags.find(isOrderableRareFoodTag) || '';
  const beverageTag = requiredBeverageTag || selectedCustomer?.beverageTags[0] || '';

  useEffect(() => {
    if (!selectedCustomer) {
      if (rareCustomerId !== null) onRareCustomerChange(null);
      return;
    }
    if (rareCustomerId !== selectedCustomer.id) onRareCustomerChange(selectedCustomer.id);
    if (!requiredFoodTag && foodTag) onFoodTagChange(foodTag);
    if (!requiredBeverageTag && beverageTag) onBeverageTagChange(beverageTag);
  }, [
    beverageTag,
    foodTag,
    onBeverageTagChange,
    onFoodTagChange,
    onRareCustomerChange,
    rareCustomerId,
    requiredBeverageTag,
    requiredFoodTag,
    selectedCustomer,
  ]);

  const recipes = useMemo(() => {
    if (!runtime || !runtimeSets || !selectedCustomer || !foodTag || !beverageTag) return [];
    return rankRecipesForRare(
      selectedCustomer,
      foodTag,
      beverageTag,
      runtimeSets.recipeIds,
      runtimeSets.ingredientIds,
      new Set<number>(),
      runtime.popularFoodTag,
      runtime.popularHateFoodTag,
      4,
      runtimeSets.ownedIngredientQty,
      runtime.famousShopEnabled,
    )
      .sort((a, b) => compareRareRecipesForService(a, b, runtimeSets.ownedIngredientQty))
      .sort((a, b) => compareFavoriteRecipeResults(a, b, favorites, selectedCustomer.id, foodTag))
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [beverageTag, favorites, foodTag, runtime, runtimeSets, selectedCustomer]);

  const beverages = useMemo(() => {
    if (!runtimeSets || !selectedCustomer || !beverageTag) return [];
    return rankBeveragesForRare(selectedCustomer, beverageTag, runtimeSets.beverageIds)
      .sort(compareRareBeveragesForService)
      .sort((a, b) => compareFavoriteBeverageResults(a, b, favorites, selectedCustomer.id, beverageTag))
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [beverageTag, favorites, runtimeSets, selectedCustomer]);

  if (!runtime || !runtimeSets) return <RuntimeUnavailable />;

  return (
    <div className="space-y-4">
      <PlaceToolbar
        selectedPlace={selectedPlace}
        detectedPlace={detectedPlace}
        onPlaceChange={onPlaceChange}
        onFollowDetectedPlace={onFollowDetectedPlace}
      />

      {!selectedPlace && <EmptyState text="请选择地区后查看稀客推荐" />}

      {selectedPlace && customers.length === 0 && <EmptyState text="该地区没有稀客" />}

      {selectedPlace && selectedCustomer && (
        <>
          <Card>
            <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
              <div>
                <div className="mb-1 text-xs text-muted-foreground">稀客</div>
                <Select value={String(selectedCustomer.id)} onValueChange={(value) => onRareCustomerChange(Number(value))}>
                  <SelectTrigger className="w-full">
                    <SelectValue>{selectedCustomer.name}</SelectValue>
                  </SelectTrigger>
                  <SelectContent>
                    {customers.map((customer) => (
                      <SelectItem key={customer.id} value={String(customer.id)}>{customer.name}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div>
                <div className="mb-1 text-xs text-muted-foreground">点单料理 Tag</div>
                <Select value={foodTag} onValueChange={(value) => onFoodTagChange(value ?? '')}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {selectedCustomer.positiveTags.filter(isOrderableRareFoodTag).map((tag) => (
                      <SelectItem key={tag} value={tag}>{tag}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div>
                <div className="mb-1 text-xs text-muted-foreground">点单酒水 Tag</div>
                <Select value={beverageTag} onValueChange={(value) => onBeverageTagChange(value ?? '')}>
                  <SelectTrigger className="w-full">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {selectedCustomer.beverageTags.map((tag) => (
                      <SelectItem key={tag} value={tag}>{tag}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            </CardContent>
          </Card>
          {favoriteError && (
            <div className="rounded-md border border-destructive/30 px-3 py-2 text-sm text-destructive">
              {favoriteError}
            </div>
          )}

          <div className={DENSE_TWO_COLUMN_GRID}>
            <ListPanel title={`料理推荐 (${recipes.length})`}>
              {recipes.length === 0 && <EmptyRow text="暂无满足点单的料理" />}
              <div className="space-y-2">
                {recipes.map((recipe, index) => (
                  <RecipeRecommendationRow
                    key={`${recipe.recipe.id}-${index}`}
                    recipe={recipe}
                    index={index}
                    ownedIngredientQty={runtimeSets.ownedIngredientQty}
                    favorite={findRecipeFavorite(favorites, selectedCustomer.id, foodTag, recipe)}
                    favoriteKey={recipeFavoriteKey(selectedCustomer.id, foodTag, recipe)}
                    favoriteBusyKey={favoriteBusyKey}
                    onToggleFavorite={() => onToggleRecipeFavorite(selectedCustomer, foodTag, recipe)}
                  />
                ))}
              </div>
            </ListPanel>

            <ListPanel title={`酒水推荐 (${beverages.length})`}>
              {beverages.length === 0 && <EmptyRow text="暂无满足点单的酒水" />}
              <div className="space-y-2">
                {beverages.map((beverage, index) => (
                  <BeverageRecommendationRow
                    key={beverage.beverage.id}
                    beverage={beverage}
                    index={index}
                    ownedBeverageQty={runtimeSets.ownedBeverageQty}
                    favorite={findBeverageFavorite(favorites, selectedCustomer.id, beverageTag, beverage)}
                    favoriteKey={beverageFavoriteKey(selectedCustomer.id, beverageTag, beverage)}
                    favoriteBusyKey={favoriteBusyKey}
                    onToggleFavorite={() => onToggleBeverageFavorite(selectedCustomer, beverageTag, beverage)}
                  />
                ))}
              </div>
            </ListPanel>
          </div>
        </>
      )}
    </div>
  );
}

function ModServicePanel({
  runtime,
  night,
  detectedPlace,
  recommendations,
  recommendationIssues,
  runtimeSets,
  favorites,
  favoriteBusyKey,
  favoriteError,
  autoPrepBusy,
  autoPrepMessage,
  autoPrepPreferences,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
  onPrepareNextOrder,
  onCompleteFirstOrder,
  onEnterFocusMode,
}: {
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  detectedPlace: TPlace | null;
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  autoPrepBusy: boolean;
  autoPrepMessage: string;
  autoPrepPreferences: CompanionPreferences;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onPrepareNextOrder: () => Promise<void>;
  onCompleteFirstOrder: () => Promise<void>;
  onEnterFocusMode: () => void;
}) {
  const activeGuests = night?.activeRareGuests ?? [];
  const orders = useMemo(() => sortNightOrders(night?.orders ?? []), [night?.orders]);

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap justify-end gap-2">
        <Button size="sm" variant="secondary" onClick={onPrepareNextOrder} disabled={autoPrepBusy || recommendations.length === 0}>
          {autoPrepBusy ? '准备中...' : '准备下一单'}
        </Button>
        <Button size="sm" variant="secondary" onClick={onCompleteFirstOrder} disabled={autoPrepBusy || recommendations.length === 0}>
          {autoPrepBusy ? '处理中...' : '完成第一单'}
        </Button>
        <Button size="sm" onClick={onEnterFocusMode}>
          稀客订单专注模式
        </Button>
      </div>

      <AutoPrepStatus message={autoPrepMessage} preferences={autoPrepPreferences} />

      <Card>
        <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
          <InfoLine label="经营场景" value={detectedPlace ?? night?.placeLabel ?? '无经营场景'} />
          <InfoLine label="扫描状态" value={night?.source || '暂无'} />
          <InfoLine label="推荐数据" value={runtime ? '已就绪' : '暂不可用'} />
        </CardContent>
      </Card>

      <div className={DENSE_TWO_COLUMN_GRID}>
        <ListPanel title="当前稀客">
          {activeGuests.length === 0 && <EmptyRow text="暂无稀客" />}
          {activeGuests.map((guest) => (
            <div key={`${guest.deskCode}-${guest.guestId}-${guest.source}`} className="flex items-center justify-between border-b py-2 text-sm last:border-b-0">
              <span className="font-medium">{guest.guestName}</span>
              <span className="text-muted-foreground">桌 {formatDesk(guest.deskCode)} · {guest.source}</span>
            </div>
          ))}
        </ListPanel>

        <ListPanel title="当前稀客点单">
          {orders.length === 0 && <EmptyRow text={night?.error || '暂无点单'} />}
          {orders.map((order) => (
            <div key={`${order.deskCode}-${order.guestId}-${order.foodTagId}-${order.beverageTagId}`} className="border-b py-2 text-sm last:border-b-0">
              <div className="flex items-center justify-between gap-3">
                <span className="font-medium">{order.guestName}</span>
                <span className="text-muted-foreground">桌 {formatDesk(order.deskCode)}</span>
              </div>
              <div className="mt-1 flex flex-wrap gap-1.5">
                <Badge variant="outline">料理 {order.foodTag || '无'} ({order.foodTagId})</Badge>
                <Badge variant="outline">酒水 {order.beverageTag || '无'} ({order.beverageTagId})</Badge>
                <Badge variant="secondary">{order.source}</Badge>
              </div>
            </div>
          ))}
        </ListPanel>
      </div>

      {(recommendations.length > 0 || recommendationIssues.length > 0) && (
        <CurrentOrderRecommendations
          recommendations={recommendations}
          recommendationIssues={recommendationIssues}
          runtimeSets={runtimeSets}
          favorites={favorites}
          favoriteBusyKey={favoriteBusyKey}
          favoriteError={favoriteError}
          onToggleRecipeFavorite={onToggleRecipeFavorite}
          onToggleBeverageFavorite={onToggleBeverageFavorite}
        />
      )}
    </div>
  );
}

function ServiceFocusPage({
  recommendations,
  recommendationIssues,
  runtimeSets,
  favorites,
  favoriteBusyKey,
  favoriteError,
  autoPrepBusy,
  autoPrepMessage,
  autoPrepPreferences,
  compact,
  recipeLimit,
  beverageLimit,
  onPrepareNextOrder,
  onCompleteFirstOrder,
  onCompactChange,
  onRecipeLimitChange,
  onBeverageLimitChange,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
  onExit,
}: {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  autoPrepBusy: boolean;
  autoPrepMessage: string;
  autoPrepPreferences: CompanionPreferences;
  compact: boolean;
  recipeLimit: number;
  beverageLimit: number;
  onPrepareNextOrder: () => Promise<void>;
  onCompleteFirstOrder: () => Promise<void>;
  onCompactChange: (value: boolean) => void;
  onRecipeLimitChange: (value: number) => void;
  onBeverageLimitChange: (value: number) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onExit: () => void;
}) {
  const hasOrders = recommendations.length > 0 || recommendationIssues.length > 0;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div>
          <h1 className="text-xl font-semibold text-foreground">稀客订单专注模式</h1>
          <p className="mt-1 text-sm text-muted-foreground">只显示当前稀客点单推荐。</p>
        </div>
        <div className="flex flex-wrap items-center justify-end gap-3">
          <Button size="sm" variant="secondary" onClick={onPrepareNextOrder} disabled={autoPrepBusy || recommendations.length === 0}>
            {autoPrepBusy ? '准备中...' : '准备下一单'}
          </Button>
          <Button size="sm" variant="secondary" onClick={onCompleteFirstOrder} disabled={autoPrepBusy || recommendations.length === 0}>
            {autoPrepBusy ? '处理中...' : '完成第一单'}
          </Button>
          <SwitchControl
            label="精简模式"
            checked={compact}
            onCheckedChange={onCompactChange}
          />
          <FocusLimitInput
            label="料理"
            value={recipeLimit}
            onChange={onRecipeLimitChange}
          />
          <FocusLimitInput
            label="酒水"
            value={beverageLimit}
            onChange={onBeverageLimitChange}
          />
          <Button size="sm" variant="outline" onClick={onExit}>退出专注模式</Button>
        </div>
      </div>

      <AutoPrepStatus message={autoPrepMessage} preferences={autoPrepPreferences} />

      {hasOrders ? (
        <CurrentOrderRecommendations
          recommendations={recommendations}
          recommendationIssues={recommendationIssues}
          runtimeSets={runtimeSets}
          favorites={favorites}
          favoriteBusyKey={favoriteBusyKey}
          favoriteError={favoriteError}
          compact={compact}
          recipeLimit={recipeLimit}
          beverageLimit={beverageLimit}
          onToggleRecipeFavorite={onToggleRecipeFavorite}
          onToggleBeverageFavorite={onToggleBeverageFavorite}
        />
      ) : (
        <EmptyState text="暂无当前稀客点单。检测到稀客点单后，这里会自动显示推荐料理和酒水。" />
      )}
    </div>
  );
}

function CurrentOrderRecommendations({
  recommendations,
  recommendationIssues,
  runtimeSets,
  favorites,
  favoriteBusyKey,
  favoriteError,
  compact = false,
  recipeLimit = MAX_RECOMMENDATION_ROWS,
  beverageLimit = MAX_RECOMMENDATION_ROWS,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
}: {
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  compact?: boolean;
  recipeLimit?: number;
  beverageLimit?: number;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
}) {
  const rows = useMemo(
    () => [
      ...recommendationIssues.map((issue) => ({ kind: 'issue' as const, order: issue.order, issue })),
      ...recommendations.map((item) => ({ kind: 'recommendation' as const, order: item.order, item })),
    ].sort((left, right) => compareNightOrders(left.order, right.order)),
    [recommendationIssues, recommendations],
  );

  return (
    <ListPanel title="当前点单推荐">
      {favoriteError && (
        <div className="mb-2 rounded-md border border-destructive/30 px-3 py-2 text-sm text-destructive">
          {favoriteError}
        </div>
      )}
      <div className={compact ? 'space-y-2' : 'space-y-4'}>
        {rows.map((row) => {
          if (row.kind === 'issue') {
            const issue = row.issue;
            return (
              <div
                key={`${issue.order.deskCode}-${issue.order.guestId}-issue`}
                className={compact ? 'rounded-md border border-border p-2 text-xs' : 'rounded-md border border-border p-3 text-sm'}
              >
                <div className="font-medium">{issue.order.guestName} · 桌 {formatDesk(issue.order.deskCode)}</div>
                <div className="mt-1 text-xs text-muted-foreground">{issue.message}</div>
              </div>
            );
          }

          return (
            <OrderRecommendationPanel
              key={`${row.item.order.deskCode}-${row.item.order.guestId}-${row.item.order.foodTagId}-${row.item.order.beverageTagId}`}
              item={row.item}
              runtimeSets={runtimeSets}
              favorites={favorites}
              favoriteBusyKey={favoriteBusyKey}
              compact={compact}
              recipeLimit={recipeLimit}
              beverageLimit={beverageLimit}
              onToggleRecipeFavorite={onToggleRecipeFavorite}
              onToggleBeverageFavorite={onToggleBeverageFavorite}
            />
          );
        })}
      </div>
    </ListPanel>
  );
}

function ModInventoryPanel({
  endpoint,
  apiToken,
  runtimeSets,
  runtimeLoaded,
  onRefresh,
}: {
  endpoint: string;
  apiToken: string;
  runtimeSets: RuntimeSets | null;
  runtimeLoaded: boolean;
  onRefresh: () => Promise<void>;
}) {
  const [search, setSearch] = useState('');
  const [draftQuantities, setDraftQuantities] = useState<Record<string, string>>({});
  const [busyKey, setBusyKey] = useState('');
  const [message, setMessage] = useState('');

  const normalizedSearch = search.trim().toLowerCase();
  const ingredientRows = useMemo(
    () => filterInventoryItems(INGREDIENTS, normalizedSearch),
    [normalizedSearch],
  );
  const beverageRows = useMemo(
    () => filterInventoryItems(BEVERAGES.filter((beverage) => beverage.id >= 0), normalizedSearch),
    [normalizedSearch],
  );

  const applyQuantity = useCallback(async (kind: 'ingredient' | 'beverage', id: number, quantity: number) => {
    const key = inventoryDraftKey(kind, id);
    const targetQuantity = normalizeEditableQuantity(quantity);
    setBusyKey(key);
    setMessage('');

    try {
      const result = await writeInventoryQuantity(endpoint, apiToken, kind, id, targetQuantity);
      if (!result.ok) throw new Error(result.error || '库存修改失败');
      setDraftQuantities((current) => ({ ...current, [key]: String(result.quantity) }));
      setMessage(`${kind === 'ingredient' ? '材料' : '酒水'} #${id}: ${result.previousQuantity} -> ${result.quantity}`);
      await onRefresh();
    } catch (err) {
      setMessage(err instanceof Error ? err.message : String(err));
    } finally {
      setBusyKey('');
    }
  }, [apiToken, endpoint, onRefresh]);

  if (!runtimeLoaded || !runtimeSets) {
    return <RuntimeUnavailable />;
  }

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className={`${DENSE_CARD_HEADER_GRID} p-4 text-sm`}>
          <div>
            <div className="font-semibold">库存数量修改</div>
            <div className="mt-1 text-xs text-muted-foreground">
              修改会写入当前游戏运行时库存；请在游戏内保存后再退出。经营中修改可能会和实时消耗同时发生。
            </div>
          </div>
          <div className="flex min-w-0 items-center gap-2">
            <Input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              placeholder="搜索名称或 ID"
              className="w-56"
            />
            <Button size="sm" variant="outline" onClick={onRefresh}>
              <RefreshCw className="size-4" />
              刷新
            </Button>
          </div>
          {message && (
            <div className="lg:col-span-2 text-xs text-muted-foreground">
              {message}
            </div>
          )}
        </CardContent>
      </Card>

      <div className={DENSE_TWO_COLUMN_GRID}>
        <InventoryEditColumn
          title="材料"
          kind="ingredient"
          items={ingredientRows}
          ownedQty={runtimeSets.ownedIngredientQty}
          draftQuantities={draftQuantities}
          busyKey={busyKey}
          apiToken={apiToken}
          onDraftChange={setDraftQuantities}
          onApply={applyQuantity}
        />
        <InventoryEditColumn
          title="酒水"
          kind="beverage"
          items={beverageRows}
          ownedQty={runtimeSets.ownedBeverageQty}
          draftQuantities={draftQuantities}
          busyKey={busyKey}
          apiToken={apiToken}
          onDraftChange={setDraftQuantities}
          onApply={applyQuantity}
        />
      </div>
    </div>
  );
}

function InventoryEditColumn<TItem extends IIngredient | IBeverage>({
  title,
  kind,
  items,
  ownedQty,
  draftQuantities,
  busyKey,
  apiToken,
  onDraftChange,
  onApply,
}: {
  title: string;
  kind: 'ingredient' | 'beverage';
  items: TItem[];
  ownedQty: Record<number, number>;
  draftQuantities: Record<string, string>;
  busyKey: string;
  apiToken: string;
  onDraftChange: (next: SetStateAction<Record<string, string>>) => void;
  onApply: (kind: 'ingredient' | 'beverage', id: number, quantity: number) => Promise<void>;
}) {
  return (
    <ListPanel title={`${title} (${items.length})`}>
      <div className="space-y-2">
        {items.length === 0 && <EmptyRow text="没有匹配项目" />}
        {items.map((item) => {
          const key = inventoryDraftKey(kind, item.id);
          const quantity = ownedQty[item.id] ?? 0;
          const editable = Boolean(apiToken) && item.id >= 0 && quantity >= 0;
          const draftValue = draftQuantities[key] ?? String(quantity);
          const draftQuantity = normalizeEditableQuantity(Number(draftValue));
          const busy = busyKey === key;

          return (
            <div key={key} className="rounded-md border border-border/80 p-2 text-sm">
              <div className="flex flex-wrap items-center justify-between gap-2">
                <div className="min-w-0">
                  <div className="truncate font-medium" title={item.name}>{item.name}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    ID {item.id} · 当前 {quantity < 0 ? '无限' : quantity} · 单价 {item.price}
                  </div>
                </div>
                <Input
                  type="number"
                  min={0}
                  max={9999}
                  value={draftValue}
                  onChange={(event) => {
                    const value = event.target.value;
                    onDraftChange((current) => ({ ...current, [key]: value }));
                  }}
                  disabled={!editable || busy}
                  className="h-8 w-24"
                />
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5">
                <Button size="sm" variant="outline" disabled={!editable || busy} onClick={() => onApply(kind, item.id, quantity + 1)}>
                  +1
                </Button>
                <Button size="sm" variant="outline" disabled={!editable || busy} onClick={() => onApply(kind, item.id, quantity + 10)}>
                  +10
                </Button>
                <Button size="sm" variant="outline" disabled={!editable || busy} onClick={() => onApply(kind, item.id, 99)}>
                  99
                </Button>
                <Button size="sm" disabled={!editable || busy} onClick={() => onApply(kind, item.id, draftQuantity)}>
                  {busy ? '修改中' : '应用'}
                </Button>
              </div>
            </div>
          );
        })}
      </div>
    </ListPanel>
  );
}

function ModLogsPanel({ endpoint, apiToken }: { endpoint: string; apiToken: string }) {
  const [settings, setSettings] = useState<LocalApiLogSettings | null>(null);
  const [logs, setLogs] = useState<LocalApiLogs | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);

  const refreshLogs = useCallback(async () => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(true);

    try {
      const nextSettings = await readLogSettings(endpoint, apiToken, abortController.signal);
      setSettings(nextSettings);
      setLogs(nextSettings.logAccessEnabled ? await readLogs(endpoint, apiToken, abortController.signal) : null);
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setLoading(false);
    }
  }, [apiToken, endpoint]);

  const updateSettings = useCallback(async (next: { logAccess?: boolean; diagnostics?: boolean }) => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);

    try {
      const nextSettings = await writeLogSettings(endpoint, apiToken, next, abortController.signal);
      setSettings(nextSettings);
      if (!nextSettings.logAccessEnabled) setLogs(null);
      setError('');
      if (nextSettings.logAccessEnabled) {
        const nextLogs = await readLogs(endpoint, apiToken, abortController.signal);
        setLogs(nextLogs);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const openFolder = useCallback(async (target: 'log' | 'diagnostics') => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);

    try {
      const result = await openLogFolder(endpoint, apiToken, target, abortController.signal);
      if (!result.ok) throw new Error(result.error || '打开文件夹失败');
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const visibleLogLines = useMemo(
    () => (logs?.lines ?? []).slice(-MAX_LOG_LINES_IN_VIEW),
    [logs?.lines],
  );
  const configuredLogLimit = settings
    ? `${settings.maxLogLines ?? MAX_LOG_LINES_IN_VIEW} 行 / ${formatBytes(settings.maxLogBytes ?? 0)}`
    : '未知';
  const responseLogLimit = logs
    ? `${logs.maxLines ?? settings?.maxLogLines ?? MAX_LOG_LINES_IN_VIEW} 行 / ${formatBytes(logs.maxBytes ?? settings?.maxLogBytes ?? 0)}`
    : configuredLogLimit;

  useEffect(() => {
    refreshLogs();
    const timer = window.setInterval(refreshLogs, 2000);
    return () => window.clearInterval(timer);
  }, [refreshLogs]);

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="space-y-3 p-4">
          <div className="min-w-0">
            <div className="text-sm font-semibold">Mod 实时日志</div>
            <div className="mt-1 truncate text-xs text-muted-foreground" title={logs?.path || settings?.logOutputPath || endpoint}>
              {error || logs?.path || settings?.logOutputPath || '等待日志响应'}
            </div>
          </div>
          <div className="flex flex-wrap gap-2">
            <Button
              size="sm"
              variant={settings?.logAccessEnabled ? 'default' : 'outline'}
              onClick={() => updateSettings({ logAccess: !settings?.logAccessEnabled })}
              disabled={!apiToken || actionLoading}
            >
              <Power className="size-4" />
              {settings?.logAccessEnabled ? '关闭日志读取' : '开启日志读取'}
            </Button>
            <Button
              size="sm"
              variant={settings?.nightBusinessDiagnosticsEnabled ? 'default' : 'outline'}
              onClick={() => updateSettings({ diagnostics: !settings?.nightBusinessDiagnosticsEnabled })}
              disabled={!apiToken || actionLoading}
            >
              <Power className="size-4" />
              {settings?.nightBusinessDiagnosticsEnabled ? '关闭经营诊断' : '开启经营诊断'}
            </Button>
            <Button size="sm" variant="outline" onClick={() => openFolder('log')} disabled={!apiToken || actionLoading}>
              <FolderOpen className="size-4" />
              打开日志文件夹
            </Button>
            <Button size="sm" variant="outline" onClick={() => openFolder('diagnostics')} disabled={!apiToken || actionLoading}>
              <FolderOpen className="size-4" />
              打开诊断文件夹
            </Button>
            <Button size="sm" variant="outline" onClick={refreshLogs} disabled={loading}>
              <RefreshCw className={loading ? 'size-4 animate-spin' : 'size-4'} />
              刷新
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className={`${DENSE_TWO_COLUMN_GRID_TIGHT} p-4 text-sm`}>
          <InfoLine label="本地 API 授权" value={apiToken ? '已通过启动参数接收' : '未收到 token，请从游戏内按 F8 重新显示窗口'} />
          <InfoLine label="日志读取" value={settings?.logAccessEnabled ? '开启' : '关闭'} />
          <InfoLine label="读取上限" value={responseLogLimit} />
          <InfoLine label="窗口缓存" value={`最多显示 ${MAX_LOG_LINES_IN_VIEW} 行`} />
          <InfoLine label="经营诊断" value={settings?.nightBusinessDiagnosticsEnabled ? '开启' : '关闭'} />
          <InfoLine label="诊断日志目录" value={settings?.nightBusinessDiagnosticsDirectory || '未知'} mono />
        </CardContent>
      </Card>

      <Card>
        <CardContent className="p-0">
          <pre className="max-h-[62vh] overflow-auto whitespace-pre-wrap break-words p-4 font-mono text-xs leading-relaxed text-foreground">
            {error
              || logs?.error
              || (!settings?.logAccessEnabled ? '日志读取已关闭。需要排查时点击“开启日志读取”，结束后建议关闭。' : null)
              || (logs?.exists === false ? '未找到 BepInEx/LogOutput.log。' : null)
              || (visibleLogLines.length ? visibleLogLines.join('\n') : '暂无日志内容。')}
          </pre>
        </CardContent>
      </Card>
    </div>
  );
}

function ModSettingsPanel({
  preferences,
  themeMode,
  serviceFocusCompact,
  serviceFocusRecipeLimit,
  serviceFocusBeverageLimit,
  onPreferenceChange,
  onThemeModeChange,
  onServiceFocusCompactChange,
  onServiceFocusRecipeLimitChange,
  onServiceFocusBeverageLimitChange,
}: {
  preferences: CompanionPreferences;
  themeMode: ThemeMode;
  serviceFocusCompact: boolean;
  serviceFocusRecipeLimit: number;
  serviceFocusBeverageLimit: number;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onThemeModeChange: (mode: ThemeMode) => void;
  onServiceFocusCompactChange: (value: boolean) => void;
  onServiceFocusRecipeLimitChange: (value: number) => void;
  onServiceFocusBeverageLimitChange: (value: number) => void;
}) {
  return (
    <div className={DENSE_TWO_COLUMN_GRID}>
      <ListPanel title="窗口">
        <div className="space-y-4">
          <OpacitySlider
            value={preferences.windowOpacity}
            onChange={(windowOpacity) => onPreferenceChange({ windowOpacity })}
          />
          <SettingChoice
            label="焦点切换"
            value={preferences.focusSwitchBehavior}
            options={[
              { value: 'hide', label: '隐藏窗口', description: '切回游戏时隐藏伴随窗口。' },
              { value: 'keep-visible', label: '保持悬浮', description: '只切回游戏焦点，窗口继续置顶显示。' },
            ]}
            onChange={(focusSwitchBehavior) => onPreferenceChange({ focusSwitchBehavior })}
          />
          <FocusSwitchCooldownInput
            value={preferences.focusSwitchCooldownMs}
            onChange={(focusSwitchCooldownMs) => onPreferenceChange({ focusSwitchCooldownMs })}
          />
          <SwitchControl
            label="始终置顶"
            checked={preferences.alwaysOnTop}
            onCheckedChange={(alwaysOnTop) => onPreferenceChange({ alwaysOnTop })}
          />
        </div>
      </ListPanel>

      <ListPanel title="显示">
        <div className="space-y-4">
          <SettingChoice
            label="主题"
            value={themeMode}
            options={[
              { value: 'system', label: '跟随系统', description: '使用系统浅色或深色主题。' },
              { value: 'light', label: '浅色', description: '固定使用浅色主题。' },
              { value: 'dark', label: '深色', description: '固定使用深色主题。' },
            ]}
            onChange={onThemeModeChange}
          />
          <SwitchControl
            label="手柄导航"
            checked={preferences.gamepadNavigationEnabled}
            onCheckedChange={(gamepadNavigationEnabled) => onPreferenceChange({ gamepadNavigationEnabled })}
          />
          <div className="text-xs text-muted-foreground">
            关闭手柄导航只影响伴随窗口内的手柄操作；F8 仍可在伴随窗口聚焦时切回游戏。
          </div>
        </div>
      </ListPanel>

      <ListPanel title="稀客专注模式">
        <div className="space-y-4">
          <SwitchControl
            label="默认精简模式"
            checked={serviceFocusCompact}
            onCheckedChange={onServiceFocusCompactChange}
          />
          <div className="grid grid-cols-2 gap-3">
            <FocusLimitInput
              label="料理显示数"
              value={serviceFocusRecipeLimit}
              onChange={onServiceFocusRecipeLimitChange}
            />
            <FocusLimitInput
              label="酒水显示数"
              value={serviceFocusBeverageLimit}
              onChange={onServiceFocusBeverageLimitChange}
            />
          </div>
          <div className="text-xs text-muted-foreground">
            显示数量包含收藏项；收藏项仍会优先出现在推荐列表前面。
          </div>
        </div>
      </ListPanel>

      <ListPanel title="一键准备">
        <div className="space-y-4">
          <SwitchControl
            label="自动取酒"
            checked={preferences.autoPrepTakeBeverage}
            onCheckedChange={(autoPrepTakeBeverage) => onPreferenceChange({ autoPrepTakeBeverage })}
          />
          <SwitchControl
            label="自动开始料理"
            checked={preferences.autoPrepStartCooking}
            onCheckedChange={(autoPrepStartCooking) => onPreferenceChange({ autoPrepStartCooking })}
          />
          <SwitchControl
            label="自动收取料理"
            checked={preferences.autoPrepCollectCooking}
            onCheckedChange={(autoPrepCollectCooking) => onPreferenceChange({ autoPrepCollectCooking })}
          />
          <SwitchControl
            label="只处理收藏配方"
            checked={preferences.autoPrepFavoritesOnly}
            onCheckedChange={(autoPrepFavoritesOnly) => onPreferenceChange({ autoPrepFavoritesOnly })}
          />
          <SwitchControl
            label="出错时暂停"
            checked={preferences.autoPrepStopOnError}
            onCheckedChange={(autoPrepStopOnError) => onPreferenceChange({ autoPrepStopOnError })}
          />
          <div className="text-xs text-muted-foreground">
            “准备下一单”和“完成第一单”都只处理当前排序第一笔稀客订单；未找到稳定游戏入口的步骤会显示失败原因，不会伪造扣库存。
          </div>
        </div>
      </ListPanel>
    </div>
  );
}

function AutoPrepStatus({
  message,
  preferences,
}: {
  message: string;
  preferences: CompanionPreferences;
}) {
  if (!message) return null;

  return (
    <div className="rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
      <div className="font-medium text-foreground">一键订单</div>
      <div className="mt-1 whitespace-pre-line text-muted-foreground">{message}</div>
      <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
        <Badge variant={preferences.autoPrepTakeBeverage ? 'secondary' : 'outline'}>取酒 {preferences.autoPrepTakeBeverage ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepStartCooking ? 'secondary' : 'outline'}>料理 {preferences.autoPrepStartCooking ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepCollectCooking ? 'secondary' : 'outline'}>收取 {preferences.autoPrepCollectCooking ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepFavoritesOnly ? 'secondary' : 'outline'}>收藏限定 {preferences.autoPrepFavoritesOnly ? '开' : '关'}</Badge>
      </div>
    </div>
  );
}

function StatusCard({
  label,
  value,
  detail,
  tone,
}: {
  label: string;
  value: string;
  detail: string;
  tone: 'good' | 'bad' | 'neutral';
}) {
  const toneClass = tone === 'good'
    ? 'text-emerald-700 dark:text-emerald-300'
    : tone === 'bad'
      ? 'text-destructive'
      : 'text-foreground';

  return (
    <Card>
      <CardContent className="p-4">
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className={`mt-1 text-lg font-semibold ${toneClass}`}>{value}</div>
        <div className="mt-1 truncate text-xs text-muted-foreground" title={detail}>{detail}</div>
      </CardContent>
    </Card>
  );
}

function Metric({ label, value }: { label: string; value: number | string }) {
  return (
    <div>
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-1 text-base font-semibold">{value}</div>
    </div>
  );
}

function InfoLine({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="min-w-0">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className={`mt-1 truncate text-sm ${mono ? 'font-mono text-xs' : 'font-medium'}`} title={value}>{value}</div>
    </div>
  );
}

function ListPanel({ title, children }: { title: string; children: ReactNode }) {
  return (
    <Card>
      <CardContent className="p-4">
        <h2 className="mb-2 text-base font-semibold">{title}</h2>
        {children}
      </CardContent>
    </Card>
  );
}

function LowStockColumn({
  title,
  entries,
}: {
  title: string;
  entries: LowStockEntry[];
}) {
  return (
    <div>
      <h3 className="mb-1 text-sm font-medium">{title}</h3>
      {entries.length === 0 && <EmptyRow text="暂无库存数据" />}
      {entries.map((item) => (
        <div key={item.id} className="flex items-center justify-between border-b py-2 text-sm last:border-b-0">
          <span>{item.name}</span>
          <span className="text-muted-foreground">{item.qty}</span>
        </div>
      ))}
    </div>
  );
}

function TagSummary({
  tags,
  cancelledTags,
}: {
  tags: string[];
  cancelledTags: string[];
}) {
  if (tags.length === 0 && cancelledTags.length === 0) return null;

  return (
    <div className="mt-1 flex flex-wrap gap-1">
      {tags.map((tag) => <TagBadge key={tag} tag={tag} variant="default" />)}
      {cancelledTags.map((tag) => (
        <Badge key={`cancelled-${tag}`} variant="outline" className="text-muted-foreground">
          已抵消 {tag}
        </Badge>
      ))}
    </div>
  );
}

function EmptyRow({ text }: { text: string }) {
  return <div className="py-6 text-center text-sm text-muted-foreground">{text}</div>;
}

function EmptyState({ text }: { text: string }) {
  return (
    <Card>
      <CardContent className="py-10 text-center text-sm text-muted-foreground">{text}</CardContent>
    </Card>
  );
}

function RuntimeUnavailable() {
  return <EmptyState text="尚未读取到游戏实时数据。请确认游戏已加载存档，且 Mod 本地 API 已连接。" />;
}

function SwitchControl({
  label,
  checked,
  onCheckedChange,
}: {
  label: string;
  checked: boolean;
  onCheckedChange: (value: boolean) => void;
}) {
  return (
    <label className="flex items-center gap-2 text-sm">
      <button
        type="button"
        role="switch"
        aria-checked={checked}
        onClick={() => onCheckedChange(!checked)}
        className={`relative h-5 w-9 shrink-0 rounded-full border transition-colors ${
          checked ? 'border-primary bg-primary' : 'border-border bg-muted'
        }`}
      >
        <span
          className={`absolute left-0 top-1/2 size-4 -translate-y-1/2 rounded-full bg-background shadow-sm transition-transform ${
            checked ? 'translate-x-4' : 'translate-x-0.5'
          }`}
        />
      </button>
      <span className="whitespace-nowrap">{label}</span>
    </label>
  );
}

function FocusLimitInput({
  label,
  value,
  onChange,
}: {
  label: string;
  value: number;
  onChange: (value: number) => void;
}) {
  return (
    <label className="flex items-center gap-1.5 text-sm">
      <span className="whitespace-nowrap text-muted-foreground">{label}</span>
      <Input
        type="number"
        min={1}
        max={MAX_FOCUS_RECOMMENDATION_ROWS}
        value={value}
        onChange={(event) => onChange(normalizeFocusRecommendationLimit(Number(event.target.value)))}
        className="h-8 w-16"
      />
    </label>
  );
}

function FocusSwitchCooldownInput({
  value,
  onChange,
}: {
  value: number;
  onChange: (value: number) => void;
}) {
  return (
    <div>
      <label className="flex items-center justify-between gap-3 text-sm">
        <span className="font-medium">切换冷却时间</span>
        <Input
          type="number"
          min={MIN_FOCUS_SWITCH_COOLDOWN_MS}
          max={MAX_FOCUS_SWITCH_COOLDOWN_MS}
          step={50}
          value={value}
          onChange={(event) => onChange(normalizeFocusSwitchCooldownMs(Number(event.target.value)))}
          className="h-8 w-24"
        />
      </label>
      <div className="mt-1 text-xs text-muted-foreground">
        单位毫秒，范围 {MIN_FOCUS_SWITCH_COOLDOWN_MS} - {MAX_FOCUS_SWITCH_COOLDOWN_MS}。调低后切换更快，过低可能重复触发。
      </div>
    </div>
  );
}

function OpacitySlider({
  value,
  onChange,
}: {
  value: number;
  onChange: (value: number) => void;
}) {
  const percent = Math.round(normalizeWindowOpacity(value) * 100);

  return (
    <div>
      <div className="mb-2 flex items-center justify-between gap-3 text-sm">
        <span className="font-medium">窗口透明度</span>
        <span className="text-muted-foreground">{percent}%</span>
      </div>
      <input
        type="range"
        min={Math.round(MIN_WINDOW_OPACITY * 100)}
        max={100}
        step={1}
        value={percent}
        onChange={(event) => onChange(normalizeWindowOpacity(Number(event.target.value) / 100))}
        className="h-2 w-full accent-primary"
      />
      <div className="mt-1 text-xs text-muted-foreground">
        仅调整窗口和面板背景，文字、按钮和标签不会整体变淡。
      </div>
    </div>
  );
}

function SettingChoice<TValue extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: TValue;
  options: { value: TValue; label: string; description: string }[];
  onChange: (value: TValue) => void;
}) {
  return (
    <div>
      <div className="mb-2 text-sm font-medium">{label}</div>
      <div className={`grid gap-2 ${options.length > 2 ? 'grid-cols-3' : 'grid-cols-2'}`}>
        {options.map((option) => (
          <button
            key={option.value}
            type="button"
            onClick={() => onChange(option.value)}
            className={`rounded-lg border p-2 text-left transition-colors ${
              value === option.value
                ? 'border-primary bg-primary/10 text-foreground'
                : 'border-border bg-background/50 text-foreground hover:bg-muted'
            }`}
          >
            <div className="text-sm font-medium">{option.label}</div>
            <div className="mt-1 text-xs text-muted-foreground">{option.description}</div>
          </button>
        ))}
      </div>
    </div>
  );
}

function PlaceToolbar({
  selectedPlace,
  detectedPlace,
  onPlaceChange,
  onFollowDetectedPlace,
}: {
  selectedPlace: TPlace | null;
  detectedPlace: TPlace | null;
  onPlaceChange: (place: TPlace) => void;
  onFollowDetectedPlace: () => void;
}) {
  return (
    <div className="flex flex-wrap items-center gap-2">
      <RegionSelector value={selectedPlace} onChange={onPlaceChange} />
      {detectedPlace && (
        <Button size="sm" variant="outline" onClick={onFollowDetectedPlace}>
          跟随经营场景: {detectedPlace}
        </Button>
      )}
    </div>
  );
}

function NormalRecipeRow({
  recipe,
  index,
  ownedIngredientQty,
}: {
  recipe: INormalRecipeResult;
  index: number;
  ownedIngredientQty: Record<number, number>;
}) {
  return (
    <div className="rounded-md border border-border/80 p-2 text-sm">
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-muted-foreground">#{index + 1}</span>
        <span className="font-medium">{recipe.recipe.name}</span>
        <Badge variant="secondary">{recipe.recipe.cooker || '未知厨具'}</Badge>
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        分数 {recipe.totalCoverage} · 成本 {recipe.ingredientCost} · 利润 {recipe.profit} · 价格 {recipe.recipe.price}
      </div>
      <div className="mt-1 flex flex-wrap gap-1">
        {recipe.matchedTags.map((tag) => <TagBadge key={tag} tag={tag} variant="matched" />)}
      </div>
      <div className="mt-1">
        <CustomerScoreBadges scores={recipe.customerScores} />
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        基础配方: {formatIngredientNamesWithQty(recipe.recipe.ingredients, ownedIngredientQty) || '无'}
      </div>
    </div>
  );
}

function NormalBeverageRow({
  beverage,
  index,
  ownedBeverageQty,
}: {
  beverage: INormalBeverageResult;
  index: number;
  ownedBeverageQty: Record<number, number>;
}) {
  return (
    <div className="rounded-md border border-border/80 p-2 text-sm">
      <div className="flex flex-wrap items-center gap-1.5">
        <span className="text-xs text-muted-foreground">#{index + 1}</span>
        <span className="font-medium">
          {beverage.beverage.name}{formatQtySuffix(ownedBeverageQty[beverage.beverage.id])}
        </span>
        <span className="text-primary">¥{beverage.beverage.price}</span>
      </div>
      <div className="mt-1 text-xs text-muted-foreground">分数 {beverage.totalCoverage}</div>
      <div className="mt-1 flex flex-wrap gap-1">
        {beverage.beverage.tags.map((tag) => (
          <TagBadge key={tag} tag={tag} variant={beverage.matchedTags.includes(tag) ? 'matched' : 'default'} />
        ))}
      </div>
      <div className="mt-1">
        <CustomerScoreBadges scores={beverage.customerScores} />
      </div>
    </div>
  );
}

function OrderRecommendationPanel({
  item,
  runtimeSets,
  favorites,
  favoriteBusyKey,
  compact = false,
  recipeLimit = MAX_RECOMMENDATION_ROWS,
  beverageLimit = MAX_RECOMMENDATION_ROWS,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
}: {
  item: OrderRecommendation;
  runtimeSets: RuntimeSets | null;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  compact?: boolean;
  recipeLimit?: number;
  beverageLimit?: number;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
}) {
  const visibleRecipes = item.recipes.slice(0, normalizeFocusRecommendationLimit(recipeLimit));
  const visibleBeverages = item.beverages.slice(0, normalizeFocusRecommendationLimit(beverageLimit));

  return (
    <div className={compact ? 'rounded-md border border-border p-2' : 'rounded-md border border-border p-3'}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-sm font-semibold">{item.customer.name} · 桌 {formatDesk(item.order.deskCode)}</div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            <Badge variant="outline">料理 {item.order.foodTag || '无'}</Badge>
            <Badge variant="outline">酒水 {item.order.beverageTag || '无'}</Badge>
            <Badge variant="secondary">{item.order.source}</Badge>
          </div>
        </div>
      </div>

      <div className={compact ? `mt-2 ${DENSE_TWO_COLUMN_GRID_TIGHT}` : `mt-3 ${DENSE_TWO_COLUMN_GRID}`}>
        <div>
          <h3 className={compact ? 'mb-1 text-xs font-semibold' : 'mb-2 text-sm font-semibold'}>推荐料理</h3>
          {visibleRecipes.length === 0 && <EmptyRow text="暂无满足点单的料理" />}
          <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
            {visibleRecipes.map((recipe, index) => (
              <RecipeRecommendationRow
                key={`${recipe.recipe.id}-${index}`}
                recipe={recipe}
                index={index}
                ownedIngredientQty={runtimeSets?.ownedIngredientQty ?? {}}
                favorite={findRecipeFavorite(favorites, item.customer.id, item.order.foodTag, recipe)}
                favoriteKey={recipeFavoriteKey(item.customer.id, item.order.foodTag, recipe)}
                favoriteBusyKey={favoriteBusyKey}
                compact={compact}
                onToggleFavorite={() => onToggleRecipeFavorite(item.customer, item.order.foodTag, recipe)}
              />
            ))}
          </div>
        </div>

        <div>
          <h3 className={compact ? 'mb-1 text-xs font-semibold' : 'mb-2 text-sm font-semibold'}>推荐酒水</h3>
          {visibleBeverages.length === 0 && <EmptyRow text="暂无满足点单的酒水" />}
          <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
            {visibleBeverages.map((beverage, index) => (
              <BeverageRecommendationRow
                key={beverage.beverage.id}
                beverage={beverage}
                index={index}
                ownedBeverageQty={runtimeSets?.ownedBeverageQty ?? {}}
                favorite={findBeverageFavorite(favorites, item.customer.id, item.order.beverageTag, beverage)}
                favoriteKey={beverageFavoriteKey(item.customer.id, item.order.beverageTag, beverage)}
                favoriteBusyKey={favoriteBusyKey}
                compact={compact}
                onToggleFavorite={() => onToggleBeverageFavorite(item.customer, item.order.beverageTag, beverage)}
              />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}

function RecipeRecommendationRow({
  recipe,
  index,
  ownedIngredientQty,
  favorite,
  favoriteKey = '',
  favoriteBusyKey = '',
  compact = false,
  onToggleFavorite,
}: {
  recipe: IRareRecipeResult;
  index: number;
  ownedIngredientQty: Record<number, number>;
  favorite?: FavoriteRecipeEntry | null;
  favoriteKey?: string;
  favoriteBusyKey?: string;
  compact?: boolean;
  onToggleFavorite?: () => void;
}) {
  const totalCost = recipe.baseCost + recipe.extraCost;
  const extras = recipe.extraIngredients.length === 0
    ? '不加料'
    : recipe.extraIngredients.map((ingredient) => formatIngredientWithQty(ingredient.name, ownedIngredientQty)).join(', ');
  const baseRecipe = formatIngredientNamesWithQty(recipe.recipe.ingredients, ownedIngredientQty) || '无';
  const busy = favoriteBusyKey === (favorite?.id ?? favoriteKey);

  return (
    <div
      className={compact ? 'rounded-md border border-border/80 p-1.5 text-xs' : 'rounded-md border border-border/80 p-2 text-sm'}
      data-gamepad-focusable={onToggleFavorite ? 'true' : undefined}
      data-gamepad-favorite-scope={onToggleFavorite ? 'true' : undefined}
      data-gamepad-row={onToggleFavorite ? 'true' : undefined}
      tabIndex={onToggleFavorite ? 0 : undefined}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex min-w-0 flex-1 flex-wrap items-center gap-1.5">
          <span className="text-xs text-muted-foreground">#{index + 1}</span>
          <span className="font-medium">{recipe.recipe.name}</span>
          <Badge variant="secondary">{RATING_LABELS[recipe.rating]}</Badge>
          <span className="text-xs text-muted-foreground">
            分数 {recipe.foodScore} · 成本 {totalCost}
          </span>
          <RecipeMetaHighlight label="厨具" value={recipe.recipe.cooker || '未知'} tone="cooker" />
          {favorite && <Badge variant="outline">已收藏</Badge>}
        </div>
        {onToggleFavorite && (
          <Button
            type="button"
            size={compact ? 'icon-xs' : 'xs'}
            variant={favorite ? 'default' : 'outline'}
            disabled={busy}
            data-gamepad-favorite="true"
            title={favorite ? '取消收藏该料理方案' : '收藏该料理方案'}
            onClick={onToggleFavorite}
          >
            <Star className={favorite ? 'size-3 fill-current' : 'size-3'} />
            {!compact && <span>{favorite ? '取消收藏' : '收藏'}</span>}
          </Button>
        )}
      </div>
      <div className="mt-1 flex flex-wrap gap-1.5">
        <RecipeMetaHighlight label="基础配方" value={baseRecipe} tone="base" />
        <RecipeMetaHighlight label="加料" value={extras} tone="extra" />
      </div>
      {!compact && <TagSummary tags={recipe.allTags} cancelledTags={recipe.cancelledTags} />}
    </div>
  );
}

function RecipeMetaHighlight({
  label,
  value,
  tone,
}: {
  label: string;
  value: string;
  tone: 'cooker' | 'base' | 'extra';
}) {
  const toneClass = tone === 'cooker'
    ? 'border-amber-300/70 bg-amber-50 text-amber-950 dark:border-amber-400/30 dark:bg-amber-400/10 dark:text-amber-100'
    : tone === 'base'
      ? 'border-emerald-300/70 bg-emerald-50 text-emerald-950 dark:border-emerald-400/30 dark:bg-emerald-400/10 dark:text-emerald-100'
      : 'border-sky-300/70 bg-sky-50 text-sky-950 dark:border-sky-400/30 dark:bg-sky-400/10 dark:text-sky-100';

  return (
    <span className={`inline-flex max-w-full items-center gap-1 rounded-md border px-1.5 py-0.5 text-xs ${toneClass}`}>
      <span className="shrink-0 font-medium">{label}</span>
      <span className="min-w-0 truncate" title={value}>{value}</span>
    </span>
  );
}

function BeverageRecommendationRow({
  beverage,
  index,
  ownedBeverageQty,
  favorite,
  favoriteKey = '',
  favoriteBusyKey = '',
  compact = false,
  onToggleFavorite,
}: {
  beverage: IRareBeverageResult;
  index: number;
  ownedBeverageQty: Record<number, number>;
  favorite?: FavoriteBeverageEntry | null;
  favoriteKey?: string;
  favoriteBusyKey?: string;
  compact?: boolean;
  onToggleFavorite?: () => void;
}) {
  const busy = favoriteBusyKey === (favorite?.id ?? favoriteKey);

  return (
    <div
      className={compact ? 'rounded-md border border-border/80 p-1.5 text-xs' : 'rounded-md border border-border/80 p-2 text-sm'}
      data-gamepad-focusable={onToggleFavorite ? 'true' : undefined}
      data-gamepad-favorite-scope={onToggleFavorite ? 'true' : undefined}
      data-gamepad-row={onToggleFavorite ? 'true' : undefined}
      tabIndex={onToggleFavorite ? 0 : undefined}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs text-muted-foreground">#{index + 1}</span>
          <span className="font-medium">
            {beverage.beverage.name}{formatQtySuffix(ownedBeverageQty[beverage.beverage.id])}
          </span>
          {beverage.meetsRequiredBev && <Badge variant="secondary">满足点单</Badge>}
          {favorite && <Badge variant="outline">已收藏</Badge>}
        </div>
        {onToggleFavorite && (
          <Button
            type="button"
            size={compact ? 'icon-xs' : 'xs'}
            variant={favorite ? 'default' : 'outline'}
            disabled={busy}
            data-gamepad-favorite="true"
            title={favorite ? '取消收藏该酒水' : '收藏该酒水'}
            onClick={onToggleFavorite}
          >
            <Star className={favorite ? 'size-3 fill-current' : 'size-3'} />
            {!compact && <span>{favorite ? '取消收藏' : '收藏'}</span>}
          </Button>
        )}
      </div>
      <div className="mt-1 text-xs text-muted-foreground">
        分数 {beverage.bevScore} · 价格 {beverage.beverage.price}
        {!compact && beverage.matchedTags.length > 0 ? ` · Tag: ${beverage.matchedTags.join(', ')}` : ''}
      </div>
    </div>
  );
}

async function readSnapshot(endpoint: string, apiToken: string, signal: AbortSignal): Promise<LocalApiSnapshot> {
  return readLocalApiJson<LocalApiSnapshot>(endpoint, apiToken, '/snapshot', signal);
}

async function readLogs(endpoint: string, apiToken: string, signal: AbortSignal): Promise<LocalApiLogs> {
  return readLocalApiJson<LocalApiLogs>(endpoint, apiToken, '/logs', signal);
}

async function readLogSettings(endpoint: string, apiToken: string, signal: AbortSignal): Promise<LocalApiLogSettings> {
  return readLocalApiJson<LocalApiLogSettings>(endpoint, apiToken, '/logs/settings', signal);
}

async function writeLogSettings(
  endpoint: string,
  apiToken: string,
  next: { logAccess?: boolean; diagnostics?: boolean },
  signal: AbortSignal,
): Promise<LocalApiLogSettings> {
  const params = new URLSearchParams();
  if (typeof next.logAccess === 'boolean') params.set('logAccess', String(next.logAccess));
  if (typeof next.diagnostics === 'boolean') params.set('diagnostics', String(next.diagnostics));
  return readLocalApiJson<LocalApiLogSettings>(endpoint, apiToken, `/logs/config?${params.toString()}`, signal);
}

async function openLogFolder(
  endpoint: string,
  apiToken: string,
  target: 'log' | 'diagnostics',
  signal: AbortSignal,
): Promise<LocalApiFolderResponse> {
  return readLocalApiJson<LocalApiFolderResponse>(endpoint, apiToken, `/logs/open-folder?target=${target}`, signal);
}

async function writeInventoryQuantity(
  endpoint: string,
  apiToken: string,
  itemType: 'ingredient' | 'beverage',
  itemId: number,
  quantity: number,
): Promise<InventoryEditResponse> {
  const params = new URLSearchParams({
    type: itemType,
    id: String(itemId),
    qty: String(normalizeEditableQuantity(quantity)),
  });
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), 3200);

  try {
    return await readLocalApiJson<InventoryEditResponse>(
      endpoint,
      apiToken,
      `/inventory/set?${params.toString()}`,
      abortController.signal,
    );
  } finally {
    window.clearTimeout(timeoutId);
  }
}

async function prepareNextRareOrder(
  endpoint: string,
  apiToken: string,
  item: OrderRecommendation,
  recipe: IRareRecipeResult | null,
  beverage: IRareBeverageResult | null,
  recipeFavorite: FavoriteRecipeEntry | null,
  beverageFavorite: FavoriteBeverageEntry | null,
  preferences: CompanionPreferences,
): Promise<OrderPreparationResponse> {
  return rareOrderAction(
    endpoint,
    apiToken,
    '/orders/prepare-next',
    item,
    recipe,
    beverage,
    recipeFavorite,
    beverageFavorite,
    preferences,
  );
}

async function completeFirstRareOrder(
  endpoint: string,
  apiToken: string,
  item: OrderRecommendation,
  recipe: IRareRecipeResult | null,
  beverage: IRareBeverageResult | null,
  recipeFavorite: FavoriteRecipeEntry | null,
  beverageFavorite: FavoriteBeverageEntry | null,
  preferences: CompanionPreferences,
): Promise<OrderPreparationResponse> {
  return rareOrderAction(
    endpoint,
    apiToken,
    '/orders/complete-first',
    item,
    recipe,
    beverage,
    recipeFavorite,
    beverageFavorite,
    preferences,
  );
}

async function rareOrderAction(
  endpoint: string,
  apiToken: string,
  path: string,
  item: OrderRecommendation,
  recipe: IRareRecipeResult | null,
  beverage: IRareBeverageResult | null,
  recipeFavorite: FavoriteRecipeEntry | null,
  beverageFavorite: FavoriteBeverageEntry | null,
  preferences: CompanionPreferences,
): Promise<OrderPreparationResponse> {
  const params = new URLSearchParams({
    deskCode: String(item.order.deskCode),
    guestId: item.order.guestId == null ? '' : String(item.order.guestId),
    guestName: item.order.guestName,
    foodTag: item.order.foodTag,
    beverageTag: item.order.beverageTag,
    recipeId: recipe ? String(recipe.recipe.recipeId) : '-1',
    recipeName: recipe?.recipe.name ?? '',
    extraIngredientIds: recipe ? recipe.extraIngredients.map((ingredient) => ingredient.id).join(',') : '',
    beverageId: beverage ? String(beverage.beverage.id) : '-1',
    beverageName: beverage?.beverage.name ?? '',
    autoTakeBeverage: String(preferences.autoPrepTakeBeverage),
    autoStartCooking: String(preferences.autoPrepStartCooking),
    autoCollectCooking: String(preferences.autoPrepCollectCooking),
    favoritesOnly: String(preferences.autoPrepFavoritesOnly),
    stopOnError: String(preferences.autoPrepStopOnError),
    recipeFavorite: String(Boolean(recipeFavorite)),
    beverageFavorite: String(Boolean(beverageFavorite)),
  });
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), 5000);

  try {
    return await readLocalApiJson<OrderPreparationResponse>(
      endpoint,
      apiToken,
      `${path}?${params.toString()}`,
      abortController.signal,
    );
  } finally {
    window.clearTimeout(timeoutId);
  }
}

async function readFavorites(endpoint: string, apiToken: string, signal: AbortSignal): Promise<FavoriteData> {
  return readLocalApiJson<FavoriteData>(endpoint, apiToken, '/favorites', signal);
}

async function addRecipeFavorite(
  endpoint: string,
  apiToken: string,
  customer: ICustomerRare,
  foodTag: string,
  recipe: IRareRecipeResult,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({
    customerId: String(customer.id),
    customerName: customer.name,
    foodTag,
    recipeId: String(recipe.recipe.id),
    extraIngredientIds: recipe.extraIngredients.map((ingredient) => ingredient.id).join(','),
  });
  return mutateFavorite(endpoint, apiToken, `/favorites/add-recipe?${params.toString()}`);
}

async function removeRecipeFavorite(
  endpoint: string,
  apiToken: string,
  id: string,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({ id });
  return mutateFavorite(endpoint, apiToken, `/favorites/remove-recipe?${params.toString()}`);
}

async function addBeverageFavorite(
  endpoint: string,
  apiToken: string,
  customer: ICustomerRare,
  beverageTag: string,
  beverage: IRareBeverageResult,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({
    customerId: String(customer.id),
    customerName: customer.name,
    beverageTag,
    beverageId: String(beverage.beverage.id),
  });
  return mutateFavorite(endpoint, apiToken, `/favorites/add-beverage?${params.toString()}`);
}

async function removeBeverageFavorite(
  endpoint: string,
  apiToken: string,
  id: string,
): Promise<FavoriteMutationResponse> {
  const params = new URLSearchParams({ id });
  return mutateFavorite(endpoint, apiToken, `/favorites/remove-beverage?${params.toString()}`);
}

async function mutateFavorite(
  endpoint: string,
  apiToken: string,
  path: string,
): Promise<FavoriteMutationResponse> {
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), 3200);

  try {
    return await readLocalApiJson<FavoriteMutationResponse>(endpoint, apiToken, path, abortController.signal);
  } finally {
    window.clearTimeout(timeoutId);
  }
}

async function readLocalApiJson<T>(endpoint: string, apiToken: string, path: string, signal: AbortSignal): Promise<T> {
  const targetEndpoint = `${endpoint}${path}`;
  if (isTauriRuntime()) {
    const { invoke } = await import('@tauri-apps/api/core');
    const payload = await invoke<string>('fetch_snapshot', { endpoint: targetEndpoint, token: apiToken });
    return JSON.parse(payload) as T;
  }

  const headers = new Headers();
  if (apiToken) headers.set('X-Mystia-Steward-Companion-Token', apiToken);
  const response = await fetch(targetEndpoint, {
    cache: 'no-store',
    headers,
    signal,
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  return await response.json() as T;
}

function buildRuntimeSets(runtime: RecommendationStateSnapshot | null): RuntimeSets | null {
  if (!runtime) return null;
  const ingredientIds = new Set(runtime.availableIngredientIds);
  const allIngredientIds = (allIngredients as IIngredient[]).map((ingredient) => ingredient.id);
  const unavailableIngredientIds = new Set(allIngredientIds.filter((id) => !ingredientIds.has(id)));

  return {
    recipeIds: new Set(runtime.availableRecipeIds),
    beverageIds: new Set(runtime.availableBeverageIds),
    ingredientIds,
    unavailableIngredientIds,
    ownedIngredientQty: normalizeOwnedIngredientQty(runtime.ownedIngredientQty),
    ownedBeverageQty: normalizeOwnedIngredientQty(runtime.ownedBeverageQty ?? {}),
  };
}

function toRuntimeRareCustomer(customer: RuntimeRareCustomer): ICustomerRare {
  return {
    id: customer.id,
    name: customer.name || customer.runtimeStringId || `运行时稀客 ${customer.id}`,
    description: `运行时稀客数据: ${customer.runtimeStringId || customer.source || customer.id}`,
    dlc: 0,
    places: normalizeRuntimePlaces(customer.places),
    price: [0, 0],
    enduranceLimit: 1,
    positiveTags: dedupeStrings(customer.positiveTags).filter(isOrderableRareFoodTag),
    negativeTags: dedupeStrings(customer.negativeTags),
    beverageTags: dedupeStrings(customer.beverageTags),
    positiveTagMapping: {},
    beverageTagMapping: {},
    collection: false,
    evaluation: {},
    spellCards: {
      positive: [],
      negative: [],
    },
  };
}

function normalizeRuntimePlaces(places: string[]): TPlace[] {
  const normalized = places
    .map((place) => normalizePlace(place))
    .filter((place): place is TPlace => Boolean(place));
  return normalized.length > 0 ? [...new Set(normalized)] : [...ALL_PLACES];
}

function dedupeStrings(values: string[]): string[] {
  return [...new Set(values.map((value) => value.trim()).filter(Boolean))];
}

function buildRareCustomerMap(runtimeRareCustomers: ICustomerRare[]): Map<number, ICustomerRare> {
  const map = new Map(getAllRareCustomers().map((customer) => [customer.id, customer]));
  for (const customer of runtimeRareCustomers) {
    if (!map.has(customer.id)) map.set(customer.id, customer);
  }
  return map;
}

function mergeRareCustomers(localCustomers: ICustomerRare[], runtimeRareCustomers: ICustomerRare[]): ICustomerRare[] {
  const seen = new Set<number>();
  const result: ICustomerRare[] = [];
  for (const customer of [...localCustomers, ...runtimeRareCustomers]) {
    if (seen.has(customer.id)) continue;
    seen.add(customer.id);
    result.push(customer);
  }
  return result;
}

function sortNightOrders(orders: NightBusinessOrder[]): NightBusinessOrder[] {
  return [...orders].sort(compareNightOrders);
}

function compareNightOrders(left: NightBusinessOrder, right: NightBusinessOrder): number {
  const leftSeenAt = getOrderSeenTime(left);
  const rightSeenAt = getOrderSeenTime(right);
  if (leftSeenAt !== rightSeenAt) return leftSeenAt - rightSeenAt;
  if (left.deskCode !== right.deskCode) return left.deskCode - right.deskCode;
  return left.guestName.localeCompare(right.guestName, 'zh-Hans-CN');
}

function getOrderSeenTime(order: NightBusinessOrder): number {
  const value = order.firstSeenAtUtc ?? order.lastSeenAtUtc;
  if (!value) return Number.MAX_SAFE_INTEGER;
  const time = Date.parse(value);
  return Number.isFinite(time) ? time : Number.MAX_SAFE_INTEGER;
}

function filterInventoryItems<TItem extends IIngredient | IBeverage>(items: TItem[], normalizedSearch: string): TItem[] {
  const rows = normalizedSearch
    ? items.filter((item) => item.name.toLowerCase().includes(normalizedSearch) || String(item.id).includes(normalizedSearch))
    : items;
  return rows
    .filter((item) => item.id >= 0)
    .sort((a, b) => a.id - b.id);
}

function inventoryDraftKey(kind: 'ingredient' | 'beverage', itemId: number) {
  return `${kind}:${itemId}`;
}

function normalizeEditableQuantity(value: number) {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(9999, Math.trunc(value)));
}

function normalizeFocusRecommendationLimit(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_FOCUS_RECOMMENDATION_ROWS;
  return Math.max(1, Math.min(MAX_FOCUS_RECOMMENDATION_ROWS, Math.trunc(value)));
}

function buildOrderRecommendations(
  orders: NightBusinessOrder[],
  runtime: RecommendationStateSnapshot | null | undefined,
  rareCustomersById: Map<number, ICustomerRare>,
  cache: Map<string, CachedRecommendation>,
  favorites: FavoriteData,
): { recommendations: OrderRecommendation[]; recommendationIssues: RecommendationIssue[] } {
  if (orders.length === 0) return { recommendations: [], recommendationIssues: [] };
  const sortedOrders = sortNightOrders(orders);
  if (!runtime) {
    return {
      recommendations: [],
      recommendationIssues: sortedOrders.map((order) => ({ order, message: '运行时推荐数据暂不可用。' })),
    };
  }

  const runtimeSets = buildRuntimeSets(runtime);
  if (!runtimeSets) return { recommendations: [], recommendationIssues: [] };

  const stateSignature = buildRecommendationStateSignature(runtime);
  const recommendations: OrderRecommendation[] = [];
  const recommendationIssues: RecommendationIssue[] = [];

  for (const order of sortedOrders) {
    const customer = findRareCustomer(order, rareCustomersById);
    const foodTag = order.foodTag.trim();
    const beverageTag = order.beverageTag.trim();

    if (!customer) {
      recommendationIssues.push({ order, message: '无法把该稀客映射到本地稀客数据。' });
      continue;
    }
    if (!foodTag || !beverageTag) {
      recommendationIssues.push({ order, message: '该点单缺少料理 Tag 或酒水 Tag。' });
      continue;
    }

    const cacheKey = `${stateSignature}|${customer.id}|${foodTag}|${beverageTag}`;
    let cached = cache.get(cacheKey);
    if (!cached) {
      const recipes = rankRecipesForRare(
        customer,
        foodTag,
        beverageTag,
        runtimeSets.recipeIds,
        runtimeSets.ingredientIds,
        new Set<number>(),
        runtime.popularFoodTag,
        runtime.popularHateFoodTag,
        4,
        runtimeSets.ownedIngredientQty,
        runtime.famousShopEnabled,
      )
        .sort((a, b) => compareRareRecipesForService(a, b, runtimeSets.ownedIngredientQty));

      const beverages = rankBeveragesForRare(customer, beverageTag, runtimeSets.beverageIds)
        .sort(compareRareBeveragesForService);

      cached = { customer, recipes, beverages };
      cache.set(cacheKey, cached);
      trimRecommendationCache(cache);
    }

    recommendations.push({
      order,
      customer: cached.customer,
      recipes: promoteFavoriteRecipes(cached.recipes, favorites, customer.id, foodTag).slice(0, MAX_FOCUS_RECOMMENDATION_ROWS),
      beverages: promoteFavoriteBeverages(cached.beverages, favorites, customer.id, beverageTag).slice(0, MAX_FOCUS_RECOMMENDATION_ROWS),
    });
  }

  return { recommendations, recommendationIssues };
}

type OrderPreparationSelection =
  | {
      ok: true;
      item: OrderRecommendation;
      recipe: IRareRecipeResult | null;
      beverage: IRareBeverageResult | null;
      recipeFavorite: FavoriteRecipeEntry | null;
      beverageFavorite: FavoriteBeverageEntry | null;
    }
  | {
      ok: false;
      message: string;
    };

function selectNextOrderPreparation(
  recommendations: OrderRecommendation[],
  favorites: FavoriteData,
  preferences: CompanionPreferences,
): OrderPreparationSelection {
  const rows = [...recommendations].sort((left, right) => compareNightOrders(left.order, right.order));
  if (rows.length === 0) {
    return { ok: false, message: '暂无可准备的稀客订单。' };
  }

  const item = rows[0];
  const recipePick = pickRecipeForPreparation(item, favorites, preferences);
  const beveragePick = pickBeverageForPreparation(item, favorites, preferences);
  if (!recipePick.ok && (preferences.autoPrepStartCooking || preferences.autoPrepFavoritesOnly)) {
    return {
      ok: false,
      message: preferences.autoPrepFavoritesOnly
        ? '当前第一笔稀客订单没有匹配的收藏料理。'
        : '当前第一笔稀客订单没有可用的推荐料理。',
    };
  }
  if (!beveragePick.ok && (preferences.autoPrepTakeBeverage || preferences.autoPrepFavoritesOnly)) {
    return {
      ok: false,
      message: preferences.autoPrepFavoritesOnly
        ? '当前第一笔稀客订单没有匹配的收藏酒水。'
        : '当前第一笔稀客订单没有可用的推荐酒水。',
    };
  }

  return {
    ok: true,
    item,
    recipe: recipePick.ok ? recipePick.recipe : null,
    beverage: beveragePick.ok ? beveragePick.beverage : null,
    recipeFavorite: recipePick.ok ? recipePick.favorite : null,
    beverageFavorite: beveragePick.ok ? beveragePick.favorite : null,
  };
}

function pickRecipeForPreparation(
  item: OrderRecommendation,
  favorites: FavoriteData,
  preferences: CompanionPreferences,
) {
  if (!preferences.autoPrepStartCooking && !preferences.autoPrepFavoritesOnly) {
    return { ok: false as const };
  }

  for (const recipe of item.recipes) {
    const favorite = findRecipeFavorite(favorites, item.customer.id, item.order.foodTag, recipe);
    if (preferences.autoPrepFavoritesOnly && !favorite) continue;
    return { ok: true as const, recipe, favorite };
  }

  return { ok: false as const };
}

function pickBeverageForPreparation(
  item: OrderRecommendation,
  favorites: FavoriteData,
  preferences: CompanionPreferences,
) {
  if (!preferences.autoPrepTakeBeverage && !preferences.autoPrepFavoritesOnly) {
    return { ok: false as const };
  }

  for (const beverage of item.beverages) {
    const favorite = findBeverageFavorite(favorites, item.customer.id, item.order.beverageTag, beverage);
    if (preferences.autoPrepFavoritesOnly && !favorite) continue;
    return { ok: true as const, beverage, favorite };
  }

  return { ok: false as const };
}

function formatOrderPreparationResponse(response: OrderPreparationResponse) {
  const title = response.ok
    ? `已处理：${response.order.guestName} · 桌 ${formatDesk(response.order.deskCode)}`
    : `未完成：${response.order.guestName || '当前订单'} · 桌 ${formatDesk(response.order.deskCode)}`;
  const target = [
    response.recipeName ? `料理 ${response.recipeName}` : '',
    response.beverageName ? `酒水 ${response.beverageName}` : '',
  ].filter(Boolean).join(' / ');
  const steps = response.steps.map((step) => {
    const prefix = step.skipped ? '跳过' : step.ok ? '完成' : '失败';
    return `${prefix} ${step.name}：${step.message}`;
  });
  return [title, target, ...steps, response.error ? `错误：${response.error}` : ''].filter(Boolean).join('\n');
}

function findRareCustomer(order: NightBusinessOrder, rareCustomersById: Map<number, ICustomerRare>) {
  if (order.guestId != null) {
    const byId = rareCustomersById.get(order.guestId);
    if (byId) return byId;
  }

  return [...rareCustomersById.values()].find((customer) => customer.name === order.guestName) ?? null;
}

function promoteFavoriteRecipes(
  rows: IRareRecipeResult[],
  favorites: FavoriteData,
  customerId: number,
  foodTag: string,
): IRareRecipeResult[] {
  const matchingFavorites = favorites.recipes
    .filter((favorite) => favorite.customerId === customerId && favorite.foodTag === foodTag)
    .sort(compareFavoriteUpdatedDesc);
  if (matchingFavorites.length === 0) return rows;

  const used = new Set<string>();
  const promoted: IRareRecipeResult[] = [];
  for (const favorite of matchingFavorites) {
    const row = rows.find((candidate) => isRecipeFavoriteMatch(favorite, candidate));
    if (!row) continue;
    const key = recipeResultKey(row);
    if (used.has(key)) continue;
    used.add(key);
    promoted.push(row);
  }

  if (promoted.length === 0) return rows;
  return [...promoted, ...rows.filter((row) => !used.has(recipeResultKey(row)))];
}

function promoteFavoriteBeverages(
  rows: IRareBeverageResult[],
  favorites: FavoriteData,
  customerId: number,
  beverageTag: string,
): IRareBeverageResult[] {
  const matchingFavorites = favorites.beverages
    .filter((favorite) => favorite.customerId === customerId && favorite.beverageTag === beverageTag)
    .sort(compareFavoriteUpdatedDesc);
  if (matchingFavorites.length === 0) return rows;

  const used = new Set<number>();
  const promoted: IRareBeverageResult[] = [];
  for (const favorite of matchingFavorites) {
    const row = rows.find((candidate) => candidate.beverage.id === favorite.beverageId);
    if (!row || used.has(row.beverage.id)) continue;
    used.add(row.beverage.id);
    promoted.push(row);
  }

  if (promoted.length === 0) return rows;
  return [...promoted, ...rows.filter((row) => !used.has(row.beverage.id))];
}

function compareFavoriteRecipeResults(
  left: IRareRecipeResult,
  right: IRareRecipeResult,
  favorites: FavoriteData,
  customerId: number,
  foodTag: string,
): number {
  const leftFavorite = findRecipeFavorite(favorites, customerId, foodTag, left);
  const rightFavorite = findRecipeFavorite(favorites, customerId, foodTag, right);
  if (!leftFavorite && !rightFavorite) return 0;
  if (leftFavorite && !rightFavorite) return -1;
  if (!leftFavorite && rightFavorite) return 1;
  if (!leftFavorite || !rightFavorite) return 0;
  return compareFavoriteUpdatedDesc(leftFavorite, rightFavorite);
}

function compareFavoriteBeverageResults(
  left: IRareBeverageResult,
  right: IRareBeverageResult,
  favorites: FavoriteData,
  customerId: number,
  beverageTag: string,
): number {
  const leftFavorite = findBeverageFavorite(favorites, customerId, beverageTag, left);
  const rightFavorite = findBeverageFavorite(favorites, customerId, beverageTag, right);
  if (!leftFavorite && !rightFavorite) return 0;
  if (leftFavorite && !rightFavorite) return -1;
  if (!leftFavorite && rightFavorite) return 1;
  if (!leftFavorite || !rightFavorite) return 0;
  return compareFavoriteUpdatedDesc(leftFavorite, rightFavorite);
}

function findRecipeFavorite(
  favorites: FavoriteData,
  customerId: number,
  foodTag: string,
  recipe: IRareRecipeResult,
): FavoriteRecipeEntry | null {
  return favorites.recipes.find((favorite) =>
    favorite.customerId === customerId
    && favorite.foodTag === foodTag
    && isRecipeFavoriteMatch(favorite, recipe)
  ) ?? null;
}

function findBeverageFavorite(
  favorites: FavoriteData,
  customerId: number,
  beverageTag: string,
  beverage: IRareBeverageResult,
): FavoriteBeverageEntry | null {
  return favorites.beverages.find((favorite) =>
    favorite.customerId === customerId
    && favorite.beverageTag === beverageTag
    && favorite.beverageId === beverage.beverage.id
  ) ?? null;
}

function isRecipeFavoriteMatch(favorite: FavoriteRecipeEntry, recipe: IRareRecipeResult): boolean {
  return favorite.recipeId === recipe.recipe.id
    && normalizeIdList(favorite.extraIngredientIds).join(',') === normalizeIdList(recipe.extraIngredients.map((ingredient) => ingredient.id)).join(',');
}

function recipeFavoriteKey(customerId: number, foodTag: string, recipe: IRareRecipeResult) {
  return `recipe:${customerId}:${foodTag}:${recipeResultKey(recipe)}`;
}

function beverageFavoriteKey(customerId: number, beverageTag: string, beverage: IRareBeverageResult) {
  return `beverage:${customerId}:${beverageTag}:${beverage.beverage.id}`;
}

function recipeResultKey(recipe: IRareRecipeResult) {
  return `${recipe.recipe.id}:${normalizeIdList(recipe.extraIngredients.map((ingredient) => ingredient.id)).join(',')}`;
}

function compareFavoriteUpdatedDesc<T extends { updatedAtUtc: string }>(left: T, right: T): number {
  const leftTime = Date.parse(left.updatedAtUtc || '');
  const rightTime = Date.parse(right.updatedAtUtc || '');
  return (Number.isFinite(rightTime) ? rightTime : 0) - (Number.isFinite(leftTime) ? leftTime : 0);
}

function emptyFavoriteData(): FavoriteData {
  return {
    version: 1,
    recipes: [],
    beverages: [],
  };
}

function normalizeFavoriteData(data: FavoriteData | null | undefined): FavoriteData {
  return {
    version: Math.max(1, data?.version ?? 1),
    recipes: (data?.recipes ?? []).map((entry) => ({
      ...entry,
      extraIngredientIds: normalizeIdList(entry.extraIngredientIds ?? []),
    })),
    beverages: data?.beverages ?? [],
  };
}

function normalizeIdList(ids: number[]): number[] {
  return [...new Set(ids.filter((id) => Number.isFinite(id) && id >= 0).map((id) => Math.trunc(id)))].sort((a, b) => a - b);
}

function compareNormalRecipesForMod(a: INormalRecipeResult, b: INormalRecipeResult) {
  if (a.totalCoverage !== b.totalCoverage) return b.totalCoverage - a.totalCoverage;
  if (a.ingredientCost !== b.ingredientCost) return b.ingredientCost - a.ingredientCost;
  return a.recipe.id - b.recipe.id;
}

function compareNormalBeveragesForMod(a: INormalBeverageResult, b: INormalBeverageResult) {
  if (a.totalCoverage !== b.totalCoverage) return b.totalCoverage - a.totalCoverage;
  if (a.beverage.price !== b.beverage.price) return b.beverage.price - a.beverage.price;
  return a.beverage.id - b.beverage.id;
}

function compareRareRecipesForService(
  a: IRareRecipeResult,
  b: IRareRecipeResult,
  ownedIngredientQty: Record<number, number> = {},
) {
  if (a.meetsRequiredFood !== b.meetsRequiredFood) return a.meetsRequiredFood ? -1 : 1;
  if (a.foodScore !== b.foodScore) return b.foodScore - a.foodScore;
  if (a.extraIngredients.length !== b.extraIngredients.length) {
    return a.extraIngredients.length - b.extraIngredients.length;
  }
  const aPressure = getRareRecipeResourcePressure(a, ownedIngredientQty);
  const bPressure = getRareRecipeResourcePressure(b, ownedIngredientQty);
  if (aPressure !== bPressure) return aPressure - bPressure;
  if (a.recipe.price !== b.recipe.price) return b.recipe.price - a.recipe.price;
  if (a.extraCost !== b.extraCost) return a.extraCost - b.extraCost;
  return a.recipe.id - b.recipe.id;
}

function getRareRecipeResourcePressure(
  result: IRareRecipeResult,
  ownedIngredientQty: Record<number, number>,
): number {
  const basePressure = result.recipe.ingredients.reduce((sum, ingredientName) => {
    const ingredient = INGREDIENT_BY_NAME.get(ingredientName);
    return sum + (ingredient ? getIngredientResourcePressure(ingredient, ownedIngredientQty) : 0);
  }, 0);

  const extraPressure = result.extraIngredients.reduce(
    (sum, ingredient) => sum + getIngredientResourcePressure(ingredient, ownedIngredientQty),
    0,
  );

  return basePressure + extraPressure * EXTRA_INGREDIENT_RESOURCE_WEIGHT;
}

function getIngredientResourcePressure(
  ingredient: IIngredient,
  ownedIngredientQty: Record<number, number>,
): number {
  const qty = Math.max(0, Math.trunc(ownedIngredientQty[ingredient.id] ?? 0));
  const stockPenalty = qty <= 0
    ? (LOW_STOCK_RESOURCE_THRESHOLD + 1) * 100
    : Math.max(0, LOW_STOCK_RESOURCE_THRESHOLD + 1 - qty) * 100;
  return stockPenalty + ingredient.price;
}

function compareRareBeveragesForService(a: IRareBeverageResult, b: IRareBeverageResult) {
  if (a.meetsRequiredBev !== b.meetsRequiredBev) return a.meetsRequiredBev ? -1 : 1;
  if (a.bevScore !== b.bevScore) return b.bevScore - a.bevScore;
  if (a.beverage.price !== b.beverage.price) return b.beverage.price - a.beverage.price;
  return a.beverage.id - b.beverage.id;
}

function normalizeOwnedIngredientQty(ownedIngredientQty: Record<string, number>): Record<number, number> {
  return Object.fromEntries(
    Object.entries(ownedIngredientQty).map(([id, qty]) => [Number(id), qty]),
  ) as Record<number, number>;
}

interface LowStockEntry {
  id: number;
  name: string;
  qty: number;
}

function buildLowStockEntries(
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

function buildRecommendationStateSignature(runtime: RecommendationStateSnapshot) {
  const ownedQty = Object.entries(runtime.ownedIngredientQty)
    .sort(([a], [b]) => Number(a) - Number(b))
    .map(([id, qty]) => `${id}:${qty}`)
    .join(',');

  return [
    runtime.availableRecipeIds.join(','),
    runtime.availableBeverageIds.join(','),
    runtime.availableIngredientIds.join(','),
    ownedQty,
    runtime.popularFoodTag ?? '',
    runtime.popularHateFoodTag ?? '',
    runtime.famousShopEnabled ? '1' : '0',
  ].join('|');
}

function trimRecommendationCache(cache: Map<string, CachedRecommendation>) {
  if (cache.size <= 24) return;
  const keysToDelete = [...cache.keys()].slice(0, cache.size - 24);
  for (const key of keysToDelete) cache.delete(key);
}

function normalizeEndpoint(value: string) {
  const trimmed = value.trim() || DEFAULT_ENDPOINT;
  const withProtocol = /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`;
  try {
    const url = new URL(withProtocol);
    if (url.hostname === 'localhost') url.hostname = '127.0.0.1';
    return url.toString().replace(/\/+$/, '');
  } catch {
    return trimmed.replace(/\/+$/, '');
  }
}

function normalizePlace(value: string | null | undefined): TPlace | null {
  return ALL_PLACES.includes(value as TPlace) ? value as TPlace : null;
}

function isOrderableRareFoodTag(tag: string): boolean {
  return !NON_ORDERABLE_RARE_FOOD_TAGS.has(tag);
}

function readStoredTab(): ModTab {
  const value = readMigratedStorage(TAB_STORAGE_KEY, LEGACY_TAB_STORAGE_KEY, '');
  return value === 'overview'
    || value === 'normal'
    || value === 'rare'
    || value === 'service'
    || value === 'inventory'
    || value === 'logs'
    || value === 'settings'
    ? value
    : 'service';
}

function readStoredBoolean(key: string, fallback: boolean) {
  const value = localStorage.getItem(key);
  if (value === null) return fallback;
  return value === '1' || value === 'true';
}

function readStoredFocusLimit(key: string) {
  return normalizeFocusRecommendationLimit(Number(localStorage.getItem(key) ?? DEFAULT_FOCUS_RECOMMENDATION_ROWS));
}

function readStoredCompanionPreferences(): CompanionPreferences {
  return normalizeCompanionPreferences({
    windowOpacity: Number(localStorage.getItem(WINDOW_OPACITY_STORAGE_KEY) ?? DEFAULT_WINDOW_OPACITY),
    focusSwitchBehavior: readStoredFocusSwitchBehavior(),
    focusSwitchCooldownMs: Number(
      localStorage.getItem(FOCUS_SWITCH_COOLDOWN_STORAGE_KEY) ?? DEFAULT_FOCUS_SWITCH_COOLDOWN_MS,
    ),
    alwaysOnTop: readStoredBoolean(ALWAYS_ON_TOP_STORAGE_KEY, true),
    gamepadNavigationEnabled: readStoredBoolean(GAMEPAD_NAVIGATION_STORAGE_KEY, true),
    autoPrepTakeBeverage: readStoredBoolean(AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY, true),
    autoPrepStartCooking: readStoredBoolean(AUTO_PREP_START_COOKING_STORAGE_KEY, true),
    autoPrepCollectCooking: readStoredBoolean(AUTO_PREP_COLLECT_COOKING_STORAGE_KEY, false),
    autoPrepFavoritesOnly: readStoredBoolean(AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, false),
    autoPrepStopOnError: readStoredBoolean(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, true),
  });
}

function readStoredFocusSwitchBehavior(): FocusSwitchBehavior {
  const value = localStorage.getItem(FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY);
  return value === 'keep-visible' ? 'keep-visible' : 'hide';
}

function normalizeCompanionPreferences(value: CompanionPreferences): CompanionPreferences {
  return {
    windowOpacity: normalizeWindowOpacity(value.windowOpacity),
    focusSwitchBehavior: value.focusSwitchBehavior === 'keep-visible' ? 'keep-visible' : 'hide',
    focusSwitchCooldownMs: normalizeFocusSwitchCooldownMs(value.focusSwitchCooldownMs),
    alwaysOnTop: Boolean(value.alwaysOnTop),
    gamepadNavigationEnabled: Boolean(value.gamepadNavigationEnabled),
    autoPrepTakeBeverage: Boolean(value.autoPrepTakeBeverage),
    autoPrepStartCooking: Boolean(value.autoPrepStartCooking),
    autoPrepCollectCooking: Boolean(value.autoPrepCollectCooking),
    autoPrepFavoritesOnly: Boolean(value.autoPrepFavoritesOnly),
    autoPrepStopOnError: Boolean(value.autoPrepStopOnError),
  };
}

function normalizeWindowOpacity(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_WINDOW_OPACITY;
  return Math.max(MIN_WINDOW_OPACITY, Math.min(1, value));
}

function normalizeFocusSwitchCooldownMs(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_FOCUS_SWITCH_COOLDOWN_MS;
  return Math.max(
    MIN_FOCUS_SWITCH_COOLDOWN_MS,
    Math.min(MAX_FOCUS_SWITCH_COOLDOWN_MS, Math.trunc(value)),
  );
}

function persistCompanionPreferences(preferences: CompanionPreferences) {
  const normalized = normalizeCompanionPreferences(preferences);
  localStorage.setItem(WINDOW_OPACITY_STORAGE_KEY, String(normalized.windowOpacity));
  localStorage.setItem(FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY, normalized.focusSwitchBehavior);
  localStorage.setItem(FOCUS_SWITCH_COOLDOWN_STORAGE_KEY, String(normalized.focusSwitchCooldownMs));
  localStorage.setItem(ALWAYS_ON_TOP_STORAGE_KEY, normalized.alwaysOnTop ? '1' : '0');
  localStorage.setItem(GAMEPAD_NAVIGATION_STORAGE_KEY, normalized.gamepadNavigationEnabled ? '1' : '0');
  localStorage.setItem(AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY, normalized.autoPrepTakeBeverage ? '1' : '0');
  localStorage.setItem(AUTO_PREP_START_COOKING_STORAGE_KEY, normalized.autoPrepStartCooking ? '1' : '0');
  localStorage.setItem(AUTO_PREP_COLLECT_COOKING_STORAGE_KEY, normalized.autoPrepCollectCooking ? '1' : '0');
  localStorage.setItem(AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, normalized.autoPrepFavoritesOnly ? '1' : '0');
  localStorage.setItem(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, normalized.autoPrepStopOnError ? '1' : '0');
}

function applyCompanionVisualPreferences(preferences: CompanionPreferences) {
  const opacity = normalizeWindowOpacity(preferences.windowOpacity);
  const percent = `${Math.round(opacity * 100)}%`;
  document.documentElement.style.setProperty('--companion-window-opacity', String(opacity));
  document.documentElement.style.setProperty('--companion-window-opacity-percent', percent);
}

async function applyCompanionPreferencesToTauri(
  focusSwitchBehavior: FocusSwitchBehavior,
  alwaysOnTop: boolean,
  focusSwitchCooldownMs: number,
) {
  if (!isTauriRuntime()) return;

  try {
    const { invoke } = await import('@tauri-apps/api/core');
    await invoke('apply_companion_preferences', {
      keepVisibleWhenFocused: focusSwitchBehavior === 'keep-visible',
      alwaysOnTop,
      windowSwitchCooldownMs: normalizeFocusSwitchCooldownMs(focusSwitchCooldownMs),
    });
  } catch {
    // Browser mode and older companion builds do not expose this command.
  }
}

function readMigratedStorage(key: string, legacyKey: string, fallback: string) {
  const value = localStorage.getItem(key);
  if (value !== null) return value;

  const legacyValue = localStorage.getItem(legacyKey);
  if (legacyValue === null) return fallback;

  localStorage.setItem(key, legacyValue);
  localStorage.removeItem(legacyKey);
  return legacyValue;
}

async function toggleCompanionFocus(
  focusSwitchBehavior: FocusSwitchBehavior,
  focusSwitchCooldownMs: number,
) {
  if (!isTauriRuntime()) return;

  try {
    const { invoke } = await import('@tauri-apps/api/core');
    await invoke('toggle_companion_focus', {
      keepVisibleWhenFocused: focusSwitchBehavior === 'keep-visible',
      windowSwitchCooldownMs: normalizeFocusSwitchCooldownMs(focusSwitchCooldownMs),
    });
  } catch {
    // Browser mode and older companion builds do not expose this command.
  }
}

function formatTime(date: Date) {
  return date.toLocaleTimeString('zh-CN', { hour12: false });
}

function formatBytes(value: number) {
  if (!Number.isFinite(value) || value <= 0) return '未知';
  if (value >= 1024 * 1024) return `${Math.round(value / 1024 / 1024)} MiB`;
  return `${Math.round(value / 1024)} KiB`;
}

function formatDesk(deskCode: number) {
  return deskCode >= 0 ? String(deskCode + 1) : String(deskCode);
}

function formatIngredientNamesWithQty(names: string[], ownedIngredientQty: Record<number, number>) {
  return names.map((name) => formatIngredientWithQty(name, ownedIngredientQty)).join(', ');
}

function formatIngredientWithQty(name: string, ownedIngredientQty: Record<number, number>) {
  const id = INGREDIENT_ID_BY_NAME.get(name);
  return `${name}${formatQtySuffix(id == null ? undefined : ownedIngredientQty[id])}`;
}

function formatQtySuffix(qty: number | undefined) {
  return `(${qty == null || qty < 0 ? '?' : qty})`;
}
