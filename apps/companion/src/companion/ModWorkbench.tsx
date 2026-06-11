import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { ArrowDown, ArrowUp, FolderOpen, Power, RefreshCw, RotateCcw, Star } from 'lucide-react';
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
  rankPreferenceBeveragesForRare,
  rankPreferenceRecipesForRare,
  rankRecipesForRare,
} from '@/lib/rare-recommend';
import { isTauriRuntime } from '@/lib/tauri-runtime';
import { useThemeMode } from '@/lib/theme';
import type { ThemeMode } from '@/lib/theme';
import type {
  IBeverage,
  ICustomerRare,
  IIngredient,
  IRecipe,
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
import allRecipes from '@/data/recipes.json';

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
const AUTOMATION_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-automation-enabled`;
const AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-order-enabled`;
const AUTO_NORMAL_START_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-start-cooking`;
const AUTO_NORMAL_COLLECT_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-collect-cooking`;
const AUTO_NORMAL_STOP_ON_ERROR_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-stop-on-error`;
const AUTO_PREP_COMPLETE_ORDER_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-complete-order`;
const AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-take-beverage`;
const AUTO_PREP_START_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-start-cooking`;
const AUTO_PREP_COLLECT_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-collect-cooking`;
const AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-favorites-only`;
const AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-stop-on-error`;
const FILTER_MISSING_COOKERS_STORAGE_KEY = `${STORAGE_PREFIX}-filter-missing-cookers`;
const GAME_UI_PINNING_STORAGE_KEY = `${STORAGE_PREFIX}-game-ui-pinning`;
const COOKER_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-cooker-highlight`;
const RECIPE_SORT_RULES_STORAGE_KEY = `${STORAGE_PREFIX}-recipe-sort-rules`;
const BEVERAGE_SORT_RULES_STORAGE_KEY = `${STORAGE_PREFIX}-beverage-sort-rules`;
const SERVICE_ORDER_SORT_MODE_STORAGE_KEY = `${STORAGE_PREFIX}-service-order-sort-mode`;
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
const CONNECTION_RETRY_DELAYS_MS = [2000, 5000, 10000, 30000];
const AUTO_FIRST_ORDER_TICK_MS = 1500;
const MAX_RARE_AUTO_ORDERS_PER_TICK = 2;
const MAX_NORMAL_AUTO_ORDERS_PER_TICK = 3;
const NORMAL_AUTO_PREPARED_RETRY_MS = 45000;
const NORMAL_AUTO_RECOVERABLE_PAUSE_RETRY_MS = 10000;
const AUTO_STEP_ROLLBACK_MS = 30000;
const AUTO_JOB_STALL_MS = 90000;
const MAX_AUTO_STEP_RETRIES = 3;
const MAX_AUTO_ROLLBACKS = 2;
const NON_ORDERABLE_RARE_FOOD_TAGS = new Set(['流行喜爱', '流行厌恶']);
const COOKER_TYPE_NAME_BY_ID = new Map<number, string>([
  [1, '煮锅'],
  [2, '烧烤架'],
  [3, '油锅'],
  [4, '蒸锅'],
  [5, '料理台'],
]);
const COOKER_NAME_ALIASES = new Map<string, string>([
  ['烤架', '烧烤架'],
  ['烧烤台', '烧烤架'],
  ['锅', '煮锅'],
  ['炸锅', '油锅'],
]);
const INGREDIENTS = allIngredients as IIngredient[];
const INGREDIENT_BY_NAME = new Map(INGREDIENTS.map((ingredient) => [ingredient.name, ingredient]));
const INGREDIENT_ID_BY_NAME = new Map(INGREDIENTS.map((ingredient) => [ingredient.name, ingredient.id]));
const INGREDIENT_NAME_BY_ID = new Map(INGREDIENTS.map((ingredient) => [ingredient.id, ingredient.name]));
const BEVERAGES = allBeverages as IBeverage[];
const BEVERAGE_NAME_BY_ID = new Map(BEVERAGES.map((beverage) => [beverage.id, beverage.name]));
const RECIPES = allRecipes as IRecipe[];
const RECIPE_BY_FOOD_ID = new Map(RECIPES.map((recipe) => [recipe.id, recipe]));
const LOW_STOCK_RESOURCE_THRESHOLD = 5;
const EXTRA_INGREDIENT_RESOURCE_WEIGHT = 2;
const DENSE_TWO_COLUMN_GRID = 'grid grid-cols-2 gap-4';
const DENSE_TWO_COLUMN_GRID_TIGHT = 'grid grid-cols-2 gap-2';
const DENSE_THREE_COLUMN_GRID = 'grid grid-cols-3 gap-3';
const DENSE_FOUR_COLUMN_GRID = 'grid grid-cols-4 gap-3';
const DENSE_CARD_HEADER_GRID = 'grid grid-cols-[minmax(0,1fr)_auto] gap-3';
const DENSE_ITEM_GRID = 'grid grid-cols-[repeat(auto-fit,minmax(11rem,1fr))] gap-2';
const MOD_TAB_TRIGGER_CLASS = 'min-w-0 flex-1 data-active:bg-primary data-active:text-primary-foreground dark:data-active:bg-primary dark:data-active:text-primary-foreground';

type ModTab = 'overview' | 'normal' | 'rare' | 'service' | 'tasks' | 'inventory' | 'logs' | 'settings';
const MOD_TABS: ModTab[] = ['overview', 'normal', 'rare', 'service', 'tasks', 'inventory', 'logs', 'settings'];
type FocusSwitchBehavior = 'hide' | 'keep-visible';
type ServiceOrderSortMode = 'ordered' | 'guest';
type MissionStatusFilter = 'all' | 'not-started' | 'started' | 'finished';
type SortDirection = 'asc' | 'desc';
type RecipeSortKey =
  | 'requiredTag'
  | 'foodScore'
  | 'rating'
  | 'extraCount'
  | 'resourcePressure'
  | 'recipePrice'
  | 'extraCost'
  | 'baseCost'
  | 'totalCost'
  | 'profit'
  | 'cookerAvailable'
  | 'recipeId';
type BeverageSortKey =
  | 'requiredTag'
  | 'bevScore'
  | 'beveragePrice'
  | 'ownedQuantity'
  | 'beverageId';

interface SortRule<K extends string> {
  key: K;
  direction: SortDirection;
  enabled: boolean;
}

interface SortOption<K extends string> {
  key: K;
  label: string;
  defaultDirection: SortDirection;
  defaultEnabled: boolean;
}

const RECIPE_SORT_OPTIONS: SortOption<RecipeSortKey>[] = [
  { key: 'requiredTag', label: '满足点单 Tag', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'foodScore', label: '料理分数', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'extraCount', label: '加料数量', defaultDirection: 'asc', defaultEnabled: true },
  { key: 'resourcePressure', label: '资源压力', defaultDirection: 'asc', defaultEnabled: true },
  { key: 'recipePrice', label: '料理售价', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'extraCost', label: '加料成本', defaultDirection: 'asc', defaultEnabled: true },
  { key: 'recipeId', label: '料理 ID', defaultDirection: 'asc', defaultEnabled: true },
  { key: 'rating', label: '推荐评级', defaultDirection: 'desc', defaultEnabled: false },
  { key: 'baseCost', label: '基础成本', defaultDirection: 'asc', defaultEnabled: false },
  { key: 'totalCost', label: '总成本', defaultDirection: 'asc', defaultEnabled: false },
  { key: 'profit', label: '预计利润', defaultDirection: 'desc', defaultEnabled: false },
  { key: 'cookerAvailable', label: '当前厨具可制作', defaultDirection: 'desc', defaultEnabled: false },
];
const BEVERAGE_SORT_OPTIONS: SortOption<BeverageSortKey>[] = [
  { key: 'requiredTag', label: '满足点单 Tag', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'bevScore', label: '酒水分数', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'beveragePrice', label: '酒水售价', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'beverageId', label: '酒水 ID', defaultDirection: 'asc', defaultEnabled: true },
  { key: 'ownedQuantity', label: '当前库存数量', defaultDirection: 'desc', defaultEnabled: false },
];
const DEFAULT_RECIPE_SORT_RULES = buildDefaultSortRules(RECIPE_SORT_OPTIONS);
const DEFAULT_BEVERAGE_SORT_RULES = buildDefaultSortRules(BEVERAGE_SORT_OPTIONS);

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
  placedCookerTypeIds?: number[];
  placedCookers?: PlacedCookerSnapshot[];
  placedCookerStatus?: string;
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
}

interface PlacedCookerSnapshot {
  controllerIndex: number;
  typeIds: number[];
  typeNames: string[];
  name: string;
  isOpen: boolean;
  source: string;
}

interface NightBusinessGuest {
  deskCode: number;
  guestId: number | null;
  guestName: string;
  source: string;
  fund?: number | null;
  baseFundCarry?: number | null;
  maxFundCarry?: number | null;
  extraFundByBuff?: number | null;
  willPayMoney?: boolean | null;
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

interface RuntimeMissionInfo {
  label: string;
  title: string;
  characterLabel: string;
  characterName: string;
  places?: string[];
  source: string;
  started: boolean;
  finished: boolean;
}

interface RuntimeMissionContext {
  availableMissions: RuntimeMissionInfo[];
  source: string;
  error: string | null;
}

interface NormalBusinessOrder {
  orderKey?: string;
  deskCode: number;
  guestName: string;
  foodId: number;
  foodName: string;
  beverageId: number;
  beverageName: string;
  hasServedFood: boolean;
  hasServedBeverage: boolean;
  isFulfilled: boolean;
  firstSeenAtUtc?: string | null;
  source: string;
}

interface NormalBusinessContext {
  orders: NormalBusinessOrder[];
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
  runtimeUiPinningStatus?: string;
  recommendationState: RecommendationStateSnapshot | null;
  nightBusiness: NightBusinessContext | null;
  runtimeMissions?: RuntimeMissionContext | null;
  normalBusiness?: NormalBusinessContext | null;
  runtimeRareCustomers?: RuntimeRareCustomer[];
}

interface RuntimeSets {
  recipeIds: Set<number>;
  beverageIds: Set<number>;
  ingredientIds: Set<number>;
  unavailableIngredientIds: Set<number>;
  ownedIngredientQty: Record<number, number>;
  ownedBeverageQty: Record<number, number>;
  placedCookerTypeIds: Set<number>;
  placedCookerNames: Set<string>;
  hasCookerSnapshot: boolean;
}

interface CachedRecommendation {
  customer: ICustomerRare;
  recipes: IRareRecipeResult[];
  beverages: IRareBeverageResult[];
  preferenceRecipes: IRareRecipeResult[];
  preferenceBeverages: IRareBeverageResult[];
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

interface GameUiPinningTarget {
  signature: string;
  recipeId: number;
  recipeName: string;
  ingredientIds: number[];
  beverageId: number;
  beverageName: string;
  cookerTypeId: number;
  cookerName: string;
}

interface CompanionPreferences {
  windowOpacity: number;
  focusSwitchBehavior: FocusSwitchBehavior;
  focusSwitchCooldownMs: number;
  alwaysOnTop: boolean;
  gamepadNavigationEnabled: boolean;
  automationEnabled: boolean;
  autoNormalOrderEnabled: boolean;
  autoNormalStartCooking: boolean;
  autoNormalCollectCooking: boolean;
  autoNormalStopOnError: boolean;
  autoPrepCompleteOrder: boolean;
  autoPrepTakeBeverage: boolean;
  autoPrepStartCooking: boolean;
  autoPrepCollectCooking: boolean;
  autoPrepFavoritesOnly: boolean;
  autoPrepStopOnError: boolean;
  filterMissingCookers: boolean;
  gameUiPinningEnabled: boolean;
  cookerHighlightEnabled: boolean;
  recipeSortRules: SortRule<RecipeSortKey>[];
  beverageSortRules: SortRule<BeverageSortKey>[];
  serviceOrderSortMode: ServiceOrderSortMode;
}

interface AutoFirstOrderState {
  orderKey: string;
  prepared: boolean;
  preparedAtMs: number;
  beverageHandled: boolean;
  beverageHandledAtMs: number;
  step: AutomationStep;
  stepStartedAtMs: number;
  lastProgressAtMs: number;
  retryCount: number;
  rollbackCount: number;
  lastError: string;
  paused: boolean;
}

interface RareAutoOrderDiagnostic {
  orderKey: string;
  title: string;
  foodTag: string;
  beverageTag: string;
  recipeName: string;
  beverageName: string;
  stepLabel: string;
  stepSeconds: number;
  retryCount: number;
  rollbackCount: number;
  lastError: string;
  prepared: boolean;
  beverageHandled: boolean;
  paused: boolean;
}

interface NormalAutoOrderState {
  orderKey: string;
  prepared: boolean;
  preparedAtMs: number;
  collected: boolean;
  step: AutomationStep;
  stepStartedAtMs: number;
  lastProgressAtMs: number;
  retryCount: number;
  rollbackCount: number;
  lastError: string;
  paused: boolean;
}

interface AutomationCookerCycle {
  bucket: number;
  used: Map<string, number>;
  labels: Map<string, string[]>;
}

interface CookerRequirement {
  key: string;
  label: string;
}

interface CookerReservationResult {
  ok: boolean;
  message: string;
}

interface NormalCookerDemand {
  counts: Map<string, number>;
  labels: Map<string, string[]>;
}

type AutomationStep =
  | 'idle'
  | 'match-order'
  | 'ensure-beverage'
  | 'ensure-cooking'
  | 'wait-food-tray'
  | 'wait-food-stored'
  | 'complete-order'
  | 'done'
  | 'paused';

type ToggleRecipeFavorite = (customer: ICustomerRare, foodTag: string, recipe: IRareRecipeResult) => Promise<void>;
type ToggleBeverageFavorite = (customer: ICustomerRare, beverageTag: string, beverage: IRareBeverageResult) => Promise<void>;

export function ModWorkbench() {
  const { mode: themeMode, setMode: setThemeMode } = useThemeMode();
  const [endpoint, setEndpoint] = useState(() =>
    readMigratedStorage(ENDPOINT_STORAGE_KEY, LEGACY_ENDPOINT_STORAGE_KEY, DEFAULT_ENDPOINT),
  );
  const [endpointDraft, setEndpointDraft] = useState(endpoint);
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
  const [connectionPaused, setConnectionPaused] = useState(false);
  const [connectionFailureCount, setConnectionFailureCount] = useState(0);
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
  const [autoPrepPaused, setAutoPrepPaused] = useState(false);
  const [rareOrderDiagnostics, setRareOrderDiagnostics] = useState<RareAutoOrderDiagnostic[]>([]);
  const [normalOrderBusy, setNormalOrderBusy] = useState(false);
  const [normalOrderMessage, setNormalOrderMessage] = useState('');
  const [normalOrderPausedCount, setNormalOrderPausedCount] = useState(0);
  const rareOrderStatesRef = useRef(new Map<string, AutoFirstOrderState>());
  const rareOrderDiagnosticItemsRef = useRef(new Map<string, ValidOrderPreparationSelection>());
  const autoFirstOrderBusyRef = useRef(false);
  const normalOrderStatesRef = useRef(new Map<string, NormalAutoOrderState>());
  const normalOrderBusyRef = useRef(false);
  const lastAutoFirstOrderAtRef = useRef(0);
  const lastAutoNormalOrderAtRef = useRef(0);
  const automationCookerCycleRef = useRef<AutomationCookerCycle | null>(null);
  const recommendationCacheRef = useRef(new Map<string, CachedRecommendation>());
  const refreshInFlightRef = useRef(false);
  const lastUiPinningSignatureRef = useRef('');

  const updateCompanionPreferences = useCallback((next: Partial<CompanionPreferences>) => {
    setCompanionPreferences((current) => normalizeCompanionPreferences({ ...current, ...next }));
  }, []);

  const normalizedEndpoint = useMemo(() => normalizeEndpoint(endpoint), [endpoint]);
  const normalizedEndpointDraft = useMemo(() => normalizeEndpoint(endpointDraft), [endpointDraft]);
  const applyEndpointConnection = useCallback(() => {
    setEndpoint(normalizedEndpointDraft);
    setEndpointDraft(normalizedEndpointDraft);
    setConnectionPaused(false);
    setConnectionFailureCount(0);
    setError('');
    setSnapshot(null);
  }, [normalizedEndpointDraft]);
  const pauseConnection = useCallback(() => {
    setConnectionPaused(true);
    setLoading(false);
    setError('已停止自动重连。');
  }, []);
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
    () => buildOrderRecommendations(
      night?.orders ?? [],
      runtime,
      rareCustomersById,
      recommendationCacheRef.current,
      favorites,
      companionPreferences,
    ),
    [night?.orders, runtime, rareCustomersById, favorites, companionPreferences],
  );
  const gameUiPinningTarget = useMemo(
    () => companionPreferences.gameUiPinningEnabled || companionPreferences.cookerHighlightEnabled
      ? buildGameUiPinningTarget(orderRecommendations.recommendations, companionPreferences.serviceOrderSortMode)
      : null,
    [
      companionPreferences.cookerHighlightEnabled,
      companionPreferences.gameUiPinningEnabled,
      companionPreferences.serviceOrderSortMode,
      orderRecommendations.recommendations,
    ],
  );
  const snapshotRefreshIntervalMs = tab === 'service' || serviceFocusMode ? 750 : 2000;

  useEffect(() => {
    if (!apiToken || connectionPaused) return;
    const signature = `${companionPreferences.gameUiPinningEnabled ? '1' : '0'}|${companionPreferences.cookerHighlightEnabled ? '1' : '0'}|${gameUiPinningTarget?.signature ?? 'disabled'}`;
    if (lastUiPinningSignatureRef.current === signature) return;

    let cancelled = false;
    publishGameUiPinningTarget(
      normalizedEndpoint,
      apiToken,
      companionPreferences.gameUiPinningEnabled,
      companionPreferences.cookerHighlightEnabled,
      gameUiPinningTarget,
    )
      .then(() => {
        if (!cancelled) lastUiPinningSignatureRef.current = signature;
      })
      .catch(() => {
        if (!cancelled) lastUiPinningSignatureRef.current = '';
        // Local API may be unavailable before the game reaches the title screen.
      });

    return () => {
      cancelled = true;
    };
  }, [
    apiToken,
    connectionPaused,
    companionPreferences.cookerHighlightEnabled,
    companionPreferences.gameUiPinningEnabled,
    gameUiPinningTarget,
    normalizedEndpoint,
  ]);

  const refresh = useCallback(async (manual = false) => {
    if (!apiToken) {
      setError('未收到本地 API Token。请从游戏内启动或按 F8 唤起伴随窗口。');
      setLoading(false);
      return;
    }
    if (!manual && connectionPaused) return;
    if (refreshInFlightRef.current) return;
    refreshInFlightRef.current = true;
    const showLoading = manual || !snapshot;
    if (showLoading) setLoading(true);
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);

    try {
      const data = await readSnapshot(normalizedEndpoint, apiToken, abortController.signal);
      setSnapshot(data);
      setError('');
      setConnectionPaused(false);
      setConnectionFailureCount(0);
      setLastConnectedAt(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
      setConnectionFailureCount((current) => Math.min(current + 1, CONNECTION_RETRY_DELAYS_MS.length));
    } finally {
      window.clearTimeout(timeoutId);
      refreshInFlightRef.current = false;
      if (showLoading) setLoading(false);
    }
  }, [apiToken, connectionPaused, normalizedEndpoint, snapshot]);

  const refreshFavorites = useCallback(async () => {
    if (!apiToken) {
      setFavorites(emptyFavoriteData());
      return;
    }
    if (connectionPaused) return;

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
  }, [apiToken, connectionPaused, normalizedEndpoint]);

  const refreshRareOrderDiagnostics = useCallback((now = Date.now()) => {
    const diagnostics = Array.from(rareOrderDiagnosticItemsRef.current.values()).map((selection) => {
      const orderKey = buildAutoOrderKey(selection.item);
      const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
      return buildRareAutoOrderDiagnostic(selection, state, now);
    });
    setRareOrderDiagnostics(diagnostics);
    setAutoPrepPaused(diagnostics.some((diagnostic) => diagnostic.paused));
  }, []);

  const getAutomationCookerCycle = useCallback((now: number): AutomationCookerCycle => {
    const bucket = Math.floor(now / AUTO_FIRST_ORDER_TICK_MS);
    if (!automationCookerCycleRef.current || automationCookerCycleRef.current.bucket !== bucket) {
      automationCookerCycleRef.current = {
        bucket,
        used: new Map<string, number>(),
        labels: new Map<string, string[]>(),
      };
    }

    return automationCookerCycleRef.current;
  }, []);

  const retryRareAutomationOrder = useCallback((orderKey: string) => {
    const now = Date.now();
    const state = rareOrderStatesRef.current.get(orderKey);
    if (!state) return;
    rareOrderStatesRef.current.set(orderKey, {
      ...state,
      paused: false,
      step: state.prepared || state.beverageHandled ? 'complete-order' : 'match-order',
      stepStartedAtMs: now,
      retryCount: 0,
      lastError: '已手动重试，等待下一轮自动化继续。',
    });
    lastAutoFirstOrderAtRef.current = 0;
    setAutoPrepMessage('自动化\n已重新启用该稀客订单，下一轮会继续处理。');
    refreshRareOrderDiagnostics(now);
  }, [refreshRareOrderDiagnostics]);

  const resetRareAutomationOrder = useCallback((orderKey: string) => {
    const now = Date.now();
    rareOrderStatesRef.current.delete(orderKey);
    lastAutoFirstOrderAtRef.current = 0;
    setAutoPrepMessage('自动化\n已重置该稀客订单状态，下一轮会重新判断料理、酒水和完成状态。');
    refreshRareOrderDiagnostics(now);
  }, [refreshRareOrderDiagnostics]);

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

  const runAutoFirstOrder = useCallback(async () => {
    if (!companionPreferences.automationEnabled || autoFirstOrderBusyRef.current || autoPrepBusy) return;
    const now = Date.now();
    if (now - lastAutoFirstOrderAtRef.current < AUTO_FIRST_ORDER_TICK_MS) return;
    if (!apiToken) {
      setAutoPrepMessage('自动化已开启，但本地 API Token 不可用。');
      return;
    }

    if (!hasAutomationActionEnabled(companionPreferences)) {
      rareOrderStatesRef.current.clear();
      rareOrderDiagnosticItemsRef.current.clear();
      setRareOrderDiagnostics([]);
      setAutoPrepPaused(false);
      if (!companionPreferences.autoNormalOrderEnabled || !hasNormalOrderActionEnabled(companionPreferences)) {
        setAutoPrepMessage('自动化已开启，请在经营中页面启用至少一个子选项。');
      } else {
        setAutoPrepMessage('');
      }
      return;
    }

    const selectionPreferences = companionPreferences.autoPrepCompleteOrder
      ? buildCompleteOrderPreferences(companionPreferences)
      : companionPreferences;
    const candidateResult = selectOrderPreparationCandidates(
      orderRecommendations.recommendations,
      favorites,
      selectionPreferences,
      MAX_RARE_AUTO_ORDERS_PER_TICK,
    );
    if (candidateResult.selections.length === 0) {
      rareOrderStatesRef.current.clear();
      rareOrderDiagnosticItemsRef.current.clear();
      setRareOrderDiagnostics([]);
      setAutoPrepPaused(false);
      setAutoPrepMessage(`自动化\n${candidateResult.message}`);
      return;
    }

    const activeKeys = new Set(candidateResult.selections.map((selection) => buildAutoOrderKey(selection.item)));
    rareOrderDiagnosticItemsRef.current.clear();
    for (const selection of candidateResult.selections) {
      rareOrderDiagnosticItemsRef.current.set(buildAutoOrderKey(selection.item), selection);
    }
    for (const key of Array.from(rareOrderStatesRef.current.keys())) {
      if (!activeKeys.has(key)) rareOrderStatesRef.current.delete(key);
    }

    autoFirstOrderBusyRef.current = true;
    lastAutoFirstOrderAtRef.current = now;
    setAutoPrepBusy(true);
    try {
      const messages: string[] = [];
      let completedOrderThisTick = false;
      const cookerCycle = getAutomationCookerCycle(now);
      const cookerCapacity = buildAutomationCookerCapacity(runtime);
      const normalCookerDemand = buildNormalCookerDemand(
        snapshot?.normalBusiness?.orders ?? [],
        normalOrderStatesRef.current,
        companionPreferences,
        runtime,
        now,
      );

      for (const selection of candidateResult.selections) {
        const orderKey = buildAutoOrderKey(selection.item);
        const prefix = formatRareAutomationPrefix(selection.item);
        let currentState = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
        if (currentState.paused) {
          messages.push(`${prefix}\n${formatAutomationState(currentState)}\n稀客自动化已暂停该订单，订单变化或重新开启后会继续。`);
          continue;
        }

        let missingTrayParts = emptyMissingTrayParts();
        if (companionPreferences.autoPrepCompleteOrder && !completedOrderThisTick) {
          const completeResponse = await completeFirstRareOrder(
            normalizedEndpoint,
            apiToken,
            selection.item,
            selection.recipe,
            selection.beverage,
            selection.recipeFavorite,
            selection.beverageFavorite,
            buildCompleteOrderPreferences(companionPreferences),
          );

          if (completeResponse.ok) {
            rareOrderStatesRef.current.delete(orderKey);
            completedOrderThisTick = true;
            messages.push(`${prefix}\n${formatOrderPreparationResponse(completeResponse)}`);
            continue;
          }

          missingTrayParts = getMissingTrayParts(completeResponse);
          if (!missingTrayParts.food && !missingTrayParts.beverage) {
            const nextState = updateAutomationAfterResponse(
              currentState,
              completeResponse,
              now,
              'complete-order',
              companionPreferences.autoPrepStopOnError,
            );
            rareOrderStatesRef.current.set(orderKey, nextState);
            messages.push(`${prefix}\n${formatOrderPreparationResponse(completeResponse)}\n${formatAutomationState(nextState)}${nextState.paused ? '\n稀客自动化已暂停该订单，订单变化或重新开启后会继续。' : '\n当前步骤会继续重试。'}`);
            continue;
          }

          if (missingTrayParts.beverage && currentState.beverageHandled) {
            currentState = {
              ...currentState,
              beverageHandled: false,
              beverageHandledAtMs: 0,
              step: 'ensure-beverage',
              stepStartedAtMs: now,
              retryCount: currentState.retryCount + 1,
              lastError: '目标酒水未在送餐盘中，重新校验取酒。',
            };
          }

          if (missingTrayParts.food && currentState.prepared) {
            const shouldRollback = isAutomationTimestampStale(currentState.preparedAtMs, now, AUTO_STEP_ROLLBACK_MS);
            if (shouldRollback && currentState.rollbackCount >= MAX_AUTO_ROLLBACKS) {
              const pausedState = pauseAutomationState(
                currentState,
                now,
                `目标料理长时间未进入送餐盘，已达到回退上限 ${MAX_AUTO_ROLLBACKS} 次。`,
              );
              rareOrderStatesRef.current.set(orderKey, pausedState);
              messages.push(`${prefix}\n${formatOrderPreparationResponse(completeResponse)}\n${formatAutomationState(pausedState)}\n稀客自动化已暂停该订单，订单变化或重新开启后会继续。`);
              continue;
            }

            if (!shouldRollback) {
              const waitingState = markAutomationWaiting(currentState, 'wait-food-tray', now, '等待目标料理进入送餐盘。');
              rareOrderStatesRef.current.set(orderKey, waitingState);
              messages.push(`${prefix}\n${formatOrderPreparationResponse(completeResponse)}\n${formatAutomationState(waitingState)}`);
              if (!missingTrayParts.beverage) continue;
              currentState = waitingState;
            }

            if (shouldRollback) {
              currentState = {
                ...currentState,
                prepared: false,
                preparedAtMs: 0,
                beverageHandled: false,
                beverageHandledAtMs: 0,
                step: 'ensure-cooking',
                stepStartedAtMs: now,
                rollbackCount: currentState.rollbackCount + 1,
                retryCount: 0,
                lastError: '目标料理未进入送餐盘，回退到重新开始料理，并重新校验酒水。',
              };
            }
          }
        } else if (companionPreferences.autoPrepCompleteOrder && completedOrderThisTick && currentState.prepared && currentState.beverageHandled) {
          const waitingState = markAutomationWaiting(currentState, 'complete-order', now, '本轮已完成一笔稀客订单，等待下一轮完成。');
          rareOrderStatesRef.current.set(orderKey, waitingState);
          messages.push(`${prefix}\n${formatAutomationState(waitingState)}`);
          continue;
        }

        let shouldPrepareFood = companionPreferences.autoPrepStartCooking && !currentState.prepared;
        const shouldPrepareBeverage = companionPreferences.autoPrepTakeBeverage && !currentState.beverageHandled;
        const schedulerNote = shouldPrepareFood
          ? reserveRareCookerSlot(
            cookerCycle,
            getRareCookerRequirement(selection.recipe),
            `稀客 ${selection.item.order.guestName || '当前订单'} · 桌 ${formatDesk(selection.item.order.deskCode)}`,
            cookerCapacity,
            normalCookerDemand,
          )
          : { ok: true, message: '' };
        if (!schedulerNote.ok) {
          shouldPrepareFood = false;
        }

        if (!shouldPrepareFood && !shouldPrepareBeverage) {
          const waitingState = markAutomationWaiting(
            currentState,
            schedulerNote.ok
              ? companionPreferences.autoPrepCompleteOrder ? 'complete-order' : 'idle'
              : 'ensure-cooking',
            now,
            !schedulerNote.ok
              ? schedulerNote.message
              : companionPreferences.autoPrepCompleteOrder
              ? '等待送餐盘出现目标料理或酒水。'
              : '已按当前设置完成可执行步骤；自动完成订单未开启。',
          );
          rareOrderStatesRef.current.set(orderKey, waitingState);
          messages.push(`${prefix}\n${formatAutomationState(waitingState)}`);
          continue;
        }

        const preparePreferences = {
          ...companionPreferences,
          autoPrepTakeBeverage: shouldPrepareBeverage,
          autoPrepStartCooking: shouldPrepareFood,
          autoPrepCollectCooking: true,
        };

        const prepareResponse = await prepareNextRareOrder(
          normalizedEndpoint,
          apiToken,
          selection.item,
          shouldPrepareFood ? selection.recipe : null,
          shouldPrepareBeverage ? selection.beverage : null,
          shouldPrepareFood ? selection.recipeFavorite : null,
          shouldPrepareBeverage ? selection.beverageFavorite : null,
          preparePreferences,
        );

        const pendingRareCooking = didOrderCookingStillPending(prepareResponse, '自动开始料理');
        const startedRareCooking = didCompleteStep(prepareResponse, '自动开始料理');
        const nextPrepared = currentState.prepared
          || startedRareCooking
          || pendingRareCooking;
        const nextBeverageHandled = currentState.beverageHandled
          || didCompleteStep(prepareResponse, '自动取酒');
        const transientFailure = !prepareResponse.ok && isTransientAutoPreparationFailure(prepareResponse);
        const preparedAtMs = startedRareCooking || pendingRareCooking || (nextPrepared && !currentState.prepared) ? now : currentState.preparedAtMs;
        const beverageHandledAtMs = nextBeverageHandled && !currentState.beverageHandled ? now : currentState.beverageHandledAtMs;
        const rollbackCount = startedRareCooking || pendingRareCooking ? 0 : currentState.rollbackCount;
        const nextState = updateAutomationAfterResponse(
          {
            ...currentState,
            orderKey,
            prepared: nextPrepared,
            preparedAtMs,
            beverageHandled: nextBeverageHandled,
            beverageHandledAtMs,
            rollbackCount,
          },
          prepareResponse,
          now,
          shouldPrepareFood ? 'ensure-cooking' : shouldPrepareBeverage ? 'ensure-beverage' : 'match-order',
          companionPreferences.autoPrepStopOnError,
        );
        rareOrderStatesRef.current.set(orderKey, nextState);
        const suffix = nextState.paused
          ? '\n稀客自动化已暂停该订单，订单变化或重新开启后会继续。'
          : transientFailure
            ? '\n当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        const schedulerSuffix = schedulerNote.ok ? '' : `\n${schedulerNote.message}`;
        messages.push(`${prefix}\n${formatOrderPreparationResponse(prepareResponse)}\n${formatAutomationState(nextState)}${schedulerSuffix}${suffix}`);
      }

      if (candidateResult.messages.length > 0) {
        messages.push(...candidateResult.messages.map((message) => `跳过\n${message}`));
      }

      refreshRareOrderDiagnostics(now);
      setAutoPrepMessage(messages.length > 0
        ? `自动化\n${messages.join('\n\n')}`
        : '自动化\n当前没有需要执行的新步骤。');
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      if (companionPreferences.autoPrepStopOnError) {
        for (const selection of candidateResult.selections) {
          const orderKey = buildAutoOrderKey(selection.item);
          const state = rareOrderStatesRef.current.get(orderKey) ?? emptyAutoFirstOrderState(orderKey, now);
          rareOrderStatesRef.current.set(orderKey, pauseAutomationState(state, now, message));
        }
        refreshRareOrderDiagnostics(now);
        setAutoPrepMessage(`自动化\n${message}\n稀客自动化已暂停，订单变化或重新开启后会继续。`);
      } else {
        setAutoPrepPaused(false);
        refreshRareOrderDiagnostics(now);
        setAutoPrepMessage(`自动化\n${message}`);
      }
    } finally {
      autoFirstOrderBusyRef.current = false;
      setAutoPrepBusy(false);
    }
  }, [
    apiToken,
    autoPrepBusy,
    companionPreferences,
    favorites,
    normalizedEndpoint,
    orderRecommendations.recommendations,
    refresh,
    refreshRareOrderDiagnostics,
    getAutomationCookerCycle,
    runtime,
    snapshot?.normalBusiness?.orders,
  ]);

  const runAutoNormalOrder = useCallback(async () => {
    if (!companionPreferences.automationEnabled || !companionPreferences.autoNormalOrderEnabled || normalOrderBusyRef.current) return;
    const now = Date.now();
    if (now - lastAutoNormalOrderAtRef.current < AUTO_FIRST_ORDER_TICK_MS) return;
    if (!hasNormalOrderActionEnabled(companionPreferences)) {
      normalOrderStatesRef.current.clear();
      setNormalOrderPausedCount(0);
      setNormalOrderMessage('普客自动化已开启，请至少启用一个处理阶段：自动制作料理或收至保温箱。');
      return;
    }

    if (!apiToken) {
      setNormalOrderMessage('普客自动化已开启，但本地 API Token 不可用。');
      return;
    }

    const orders = sortNormalOrders(snapshot?.normalBusiness?.orders ?? []).filter((item) => !item.isFulfilled);
    const activeKeys = new Set(orders.map(buildNormalAutoOrderKey));
    for (const key of Array.from(normalOrderStatesRef.current.keys())) {
      if (!activeKeys.has(key)) normalOrderStatesRef.current.delete(key);
    }

    if (orders.length === 0) {
      normalOrderStatesRef.current.clear();
      setNormalOrderPausedCount(0);
      setNormalOrderMessage('普客自动化\n当前没有可处理的普客订单。');
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    const cookerCycle = getAutomationCookerCycle(now);
    const cookerCapacity = buildAutomationCookerCapacity(runtime);
    const schedulerMessages: string[] = [];
    const runnableOrders: NormalBusinessOrder[] = [];
    for (const order of orders) {
      const state = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order));
      const needsCooking = shouldAttemptNormalCooking(order, state, companionPreferences, now);
      const needsCollectionCheck = shouldConfirmNormalCollection(order, state, companionPreferences, now);
      if (!needsCooking && !needsCollectionCheck) continue;

      if (needsCooking) {
        const reservation = reserveAutomationCookerSlot(
          cookerCycle,
          getNormalCookerRequirement(order),
          `普客 桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`,
          cookerCapacity,
        );
        if (!reservation.ok) {
          schedulerMessages.push(`桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}\n${reservation.message}`);
          continue;
        }
      }

      runnableOrders.push(order);
      if (runnableOrders.length >= MAX_NORMAL_AUTO_ORDERS_PER_TICK) break;
    }
    const pausedCount = orders.filter((order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order))?.paused).length;
    setNormalOrderPausedCount(pausedCount);
    if (runnableOrders.length === 0) {
      const waitingCount = orders.filter((order) => {
        const state = normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order));
        return state?.prepared && !state.collected;
      }).length;
      const collectedCount = orders.filter((order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order))?.collected).length;
      const waitingState = orders
        .map((order) => normalOrderStatesRef.current.get(buildNormalAutoOrderKey(order)))
        .find((state) => state && (state.prepared || state.collected || state.paused));
      const schedulerText = schedulerMessages.length > 0 ? `\n${schedulerMessages.join('\n\n')}` : '';
      setNormalOrderMessage(waitingCount > 0 || collectedCount > 0 || pausedCount > 0
        ? `普客自动化\n当前没有需要新开锅的普客订单。\n等待制作或送达 ${waitingCount} 笔，已收至保温箱 ${collectedCount} 笔，暂停 ${pausedCount} 笔。${waitingState ? `\n${formatAutomationState(waitingState)}` : ''}${schedulerText}`
        : `普客自动化\n当前没有需要执行的新步骤。${schedulerText}`);
      lastAutoNormalOrderAtRef.current = now;
      return;
    }

    normalOrderBusyRef.current = true;
    lastAutoNormalOrderAtRef.current = now;
    setNormalOrderBusy(true);
    try {
      const messages: string[] = [];
      for (const order of runnableOrders) {
        const orderKey = buildNormalAutoOrderKey(order);
        const storedState = normalOrderStatesRef.current.get(orderKey) ?? emptyNormalAutoOrderState(orderKey, now);
        const currentState = isRecoverableNormalPausedState(storedState, now)
          ? {
            ...storedState,
            paused: false,
            step: 'wait-food-stored' as const,
            stepStartedAtMs: now,
            lastProgressAtMs: now,
            retryCount: 0,
            rollbackCount: 0,
            lastError: '等待普客暂存容器超时后已自动恢复，继续确认料理制作状态。',
          }
          : storedState;
        const shouldRetryPrepared = isNormalOrderPreparedStale(currentState, now);

        const requestPreferences = {
          ...companionPreferences,
          autoNormalStartCooking: companionPreferences.autoNormalStartCooking
            && shouldAttemptNormalCooking(order, currentState, companionPreferences, now),
          autoNormalCollectCooking: companionPreferences.autoNormalCollectCooking && !currentState.collected,
        };

        if (!requestPreferences.autoNormalStartCooking
          && !requestPreferences.autoNormalCollectCooking) {
          continue;
        }

        const response = await completeFirstNormalOrder(
          normalizedEndpoint,
          apiToken,
          order,
          requestPreferences,
        );
        const transientFailure = !response.ok && isTransientAutoPreparationFailure(response);
        const pendingCooking = didNormalOrderCookingStillPending(response);
        const startedCooking = didCompleteStep(response, '普客开始料理');
        const acknowledgedStart = startedCooking
          || pendingCooking
          || didAcknowledgeStep(response, '普客料理')
          || didNormalOrderCollectToWarmer(response);
        const collected = currentState.collected || didNormalOrderCollectToWarmer(response);
        const prepared = currentState.prepared || acknowledgedStart;
        const rollbackCount = collected || pendingCooking || startedCooking
          ? 0
          : currentState.rollbackCount;
        const nextState = updateAutomationAfterResponse(
          {
            ...currentState,
            orderKey,
            prepared,
            preparedAtMs: acknowledgedStart || (shouldRetryPrepared && transientFailure)
              ? now
              : currentState.preparedAtMs,
            collected,
            step: collected ? 'done' : prepared ? 'wait-food-stored' : 'ensure-cooking',
            rollbackCount,
          },
          response,
          now,
          collected ? 'done' : requestPreferences.autoNormalStartCooking ? 'ensure-cooking' : 'wait-food-stored',
          companionPreferences.autoNormalStopOnError,
        );
        const normalizedNextState = {
          ...nextState,
          collected,
        };
        normalOrderStatesRef.current.set(orderKey, normalizedNextState);

        const prefix = `桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`;
        const suffix = normalizedNextState.paused
          ? '\n普客自动化已暂停该订单，订单变化或重新开启后会继续。'
          : transientFailure
            ? '\n当前条件暂不可执行，将继续等待并自动重试。'
            : '';
        messages.push(`${prefix}\n${formatOrderPreparationResponse(response)}\n${formatAutomationState(normalizedNextState)}${suffix}`);
      }
      setNormalOrderPausedCount(Array.from(normalOrderStatesRef.current.values()).filter((state) => state.paused).length);
      setNormalOrderMessage(messages.length > 0
        ? `普客自动化\n${messages.join('\n\n')}${schedulerMessages.length > 0 ? `\n\n${schedulerMessages.join('\n\n')}` : ''}`
        : '普客自动化\n当前没有需要执行的新步骤。');
      await refresh();
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      if (companionPreferences.autoNormalStopOnError) {
        setNormalOrderPausedCount(Array.from(normalOrderStatesRef.current.values()).filter((state) => state.paused).length);
        setNormalOrderMessage(`普客自动化\n${message}\n普客自动化已暂停，订单变化或重新开启后会继续。`);
      } else {
        setNormalOrderMessage(`普客自动化\n${message}`);
      }
    } finally {
      normalOrderBusyRef.current = false;
      setNormalOrderBusy(false);
    }
  }, [
    apiToken,
    companionPreferences,
    getAutomationCookerCycle,
    normalizedEndpoint,
    refresh,
    runtime,
    snapshot?.normalBusiness?.orders,
  ]);

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
    if (!companionPreferences.automationEnabled) {
      rareOrderStatesRef.current.clear();
      rareOrderDiagnosticItemsRef.current.clear();
      setRareOrderDiagnostics([]);
      normalOrderStatesRef.current.clear();
      lastAutoFirstOrderAtRef.current = 0;
      lastAutoNormalOrderAtRef.current = 0;
      setAutoPrepPaused(false);
      setNormalOrderPausedCount(0);
      return undefined;
    }

    void runAutoFirstOrder();
    void runAutoNormalOrder();
    const timer = window.setInterval(() => {
      void runAutoFirstOrder();
      void runAutoNormalOrder();
    }, AUTO_FIRST_ORDER_TICK_MS);
    return () => window.clearInterval(timer);
  }, [companionPreferences.automationEnabled, runAutoFirstOrder, runAutoNormalOrder]);

  useEffect(() => {
    if (companionPreferences.automationEnabled && companionPreferences.autoNormalOrderEnabled) return;
    normalOrderStatesRef.current.clear();
    lastAutoNormalOrderAtRef.current = 0;
    setNormalOrderPausedCount(0);
    setNormalOrderMessage('');
  }, [companionPreferences.automationEnabled, companionPreferences.autoNormalOrderEnabled]);

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
        if (!disposed && launchEndpoint) {
          const normalizedLaunchEndpoint = normalizeEndpoint(launchEndpoint);
          setEndpoint(normalizedLaunchEndpoint);
          setEndpointDraft(normalizedLaunchEndpoint);
        }
        if (!disposed && launchToken) {
          setApiToken(launchToken);
          setConnectionPaused(false);
          setConnectionFailureCount(0);
        }
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
    if (!apiToken || connectionPaused) return;
    const retryIndex = Math.max(0, Math.min(connectionFailureCount - 1, CONNECTION_RETRY_DELAYS_MS.length - 1));
    const delay = error
      ? CONNECTION_RETRY_DELAYS_MS[retryIndex]
      : snapshot
        ? snapshotRefreshIntervalMs
        : 0;
    const timer = window.setTimeout(() => {
      void refresh();
    }, delay);
    return () => window.clearTimeout(timer);
  }, [
    apiToken,
    connectionFailureCount,
    connectionPaused,
    error,
    refresh,
    snapshot,
    snapshotRefreshIntervalMs,
  ]);

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
        orderSortMode={companionPreferences.serviceOrderSortMode}
        compact={serviceFocusCompact}
        recipeLimit={serviceFocusRecipeLimit}
        beverageLimit={serviceFocusBeverageLimit}
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
        <div className="flex w-full max-w-2xl items-center gap-2">
          <Input
            value={endpointDraft}
            onChange={(event) => setEndpointDraft(event.target.value)}
            onKeyDown={(event) => {
              if (event.key === 'Enter') applyEndpointConnection();
            }}
            spellCheck={false}
            className="font-mono text-xs"
          />
          <Button size="sm" variant="outline" onClick={applyEndpointConnection}>
            连接
          </Button>
          <Button size="sm" variant="outline" onClick={pauseConnection} disabled={connectionPaused}>
            <Power className="size-4" />
            停止
          </Button>
          <Button size="sm" onClick={() => void refresh(true)} disabled={loading || !apiToken}>
            <RefreshCw className={loading ? 'size-4 animate-spin' : 'size-4'} />
            刷新
          </Button>
        </div>
      </div>

      <div className={DENSE_THREE_COLUMN_GRID}>
        <StatusCard
          label="连接状态"
          value={!apiToken ? '未授权' : connectionPaused ? '已停止' : error ? '重试中' : snapshot ? '已连接' : '连接中'}
          detail={!apiToken
            ? '未收到游戏启动参数 Token'
            : connectionPaused
              ? '点击连接恢复自动重连'
              : error
                ? `${error}；${formatRetryDelay(connectionFailureCount)} 后重试`
                : lastConnectedAt
                  ? `最近响应 ${formatTime(lastConnectedAt)}`
                  : normalizedEndpoint}
          tone={!apiToken || connectionPaused || error ? 'bad' : snapshot ? 'good' : 'neutral'}
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
          <TabsTrigger value="tasks" className={MOD_TAB_TRIGGER_CLASS} data-gamepad-tab="true" data-gamepad-tab-value="tasks">
            任务
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
            preferences={companionPreferences}
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
            uiPinningStatus={snapshot?.runtimeUiPinningStatus ?? ''}
            uiPinningTarget={gameUiPinningTarget}
            favorites={favorites}
            favoriteBusyKey={favoriteBusyKey}
            favoriteError={favoriteError}
            autoPrepBusy={autoPrepBusy}
            autoPrepMessage={autoPrepMessage}
            autoPrepPaused={autoPrepPaused}
            rareOrderDiagnostics={rareOrderDiagnostics}
            autoPrepPreferences={companionPreferences}
            normalOrderBusy={normalOrderBusy}
            normalOrderMessage={normalOrderMessage}
            normalOrderPausedCount={normalOrderPausedCount}
            onPreferenceChange={updateCompanionPreferences}
            onToggleRecipeFavorite={toggleRecipeFavorite}
            onToggleBeverageFavorite={toggleBeverageFavorite}
            onRetryRareAutomationOrder={retryRareAutomationOrder}
            onResetRareAutomationOrder={resetRareAutomationOrder}
            onEnterFocusMode={() => setServiceFocusMode(true)}
            normalBusiness={snapshot?.normalBusiness ?? null}
          />
        </TabsContent>

        <TabsContent value="tasks" data-gamepad-scope="content">
          <ModTasksPanel
            runtimeLoaded={snapshot?.runtimeLoaded ?? false}
            missions={snapshot?.runtimeMissions ?? null}
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
            onPreferenceChange={updateCompanionPreferences}
            onThemeModeChange={setThemeMode}
            onServiceFocusCompactChange={setServiceFocusCompact}
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
  preferences,
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
  preferences: CompanionPreferences;
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
      .filter((recipe) => shouldKeepRecipeForCooker(recipe, runtimeSets, preferences.filterMissingCookers))
      .sort((a, b) => compareRareRecipesForService(
        a,
        b,
        runtimeSets.ownedIngredientQty,
        preferences.recipeSortRules,
        runtimeSets,
      ))
      .sort((a, b) => compareFavoriteRecipeResults(a, b, favorites, selectedCustomer.id, foodTag))
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [beverageTag, favorites, foodTag, preferences, runtime, runtimeSets, selectedCustomer]);

  const beverages = useMemo(() => {
    if (!runtimeSets || !selectedCustomer || !beverageTag) return [];
    return rankBeveragesForRare(selectedCustomer, beverageTag, runtimeSets.beverageIds)
      .sort((a, b) => compareRareBeveragesForService(
        a,
        b,
        runtimeSets.ownedBeverageQty,
        preferences.beverageSortRules,
      ))
      .sort((a, b) => compareFavoriteBeverageResults(a, b, favorites, selectedCustomer.id, beverageTag))
      .slice(0, MAX_RECOMMENDATION_ROWS);
  }, [beverageTag, favorites, preferences.beverageSortRules, runtimeSets, selectedCustomer]);

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
  uiPinningStatus,
  uiPinningTarget,
  favorites,
  favoriteBusyKey,
  favoriteError,
  autoPrepBusy,
  autoPrepMessage,
  autoPrepPaused,
  rareOrderDiagnostics,
  autoPrepPreferences,
  normalOrderBusy,
  normalOrderMessage,
  normalOrderPausedCount,
  normalBusiness,
  onPreferenceChange,
  onToggleRecipeFavorite,
  onToggleBeverageFavorite,
  onRetryRareAutomationOrder,
  onResetRareAutomationOrder,
  onEnterFocusMode,
}: {
  runtime: RecommendationStateSnapshot | null;
  night: NightBusinessContext | null;
  detectedPlace: TPlace | null;
  recommendations: OrderRecommendation[];
  recommendationIssues: RecommendationIssue[];
  runtimeSets: RuntimeSets | null;
  uiPinningStatus: string;
  uiPinningTarget: GameUiPinningTarget | null;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  autoPrepBusy: boolean;
  autoPrepMessage: string;
  autoPrepPaused: boolean;
  rareOrderDiagnostics: RareAutoOrderDiagnostic[];
  autoPrepPreferences: CompanionPreferences;
  normalOrderBusy: boolean;
  normalOrderMessage: string;
  normalOrderPausedCount: number;
  normalBusiness: NormalBusinessContext | null;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onToggleRecipeFavorite: ToggleRecipeFavorite;
  onToggleBeverageFavorite: ToggleBeverageFavorite;
  onRetryRareAutomationOrder: (orderKey: string) => void;
  onResetRareAutomationOrder: (orderKey: string) => void;
  onEnterFocusMode: () => void;
}) {
  const activeGuests = night?.activeRareGuests ?? [];
  const orders = useMemo(
    () => sortNightOrders(night?.orders ?? [], autoPrepPreferences.serviceOrderSortMode),
    [autoPrepPreferences.serviceOrderSortMode, night?.orders],
  );

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
          <InfoLine label="经营场景" value={detectedPlace ?? night?.placeLabel ?? '无经营场景'} />
          <InfoLine label="扫描状态" value={night?.source || '暂无'} />
          <InfoLine label="推荐数据" value={runtime ? '已就绪' : '暂不可用'} />
          <InfoLine
            label="已摆放厨具"
            value={runtimeSets?.hasCookerSnapshot
              ? [...runtimeSets.placedCookerNames].join('、') || '已读取'
              : runtime?.placedCookerStatus ? `未读取 · ${runtime.placedCookerStatus}` : '未读取'}
          />
          <InfoLine label="目标厨具" value={uiPinningTarget?.cookerName || '暂无'} />
          <InfoLine label="界面置顶" value={uiPinningStatus || '暂无'} />
        </CardContent>
      </Card>

      <Tabs defaultValue="rare" className="space-y-4">
        <TabsList className="grid h-9 w-full grid-cols-2">
          <TabsTrigger value="rare" className={MOD_TAB_TRIGGER_CLASS}>
            稀客
          </TabsTrigger>
          <TabsTrigger value="normal" className={MOD_TAB_TRIGGER_CLASS}>
            普客
          </TabsTrigger>
        </TabsList>

        <TabsContent value="rare" className="space-y-4">
          <div className="flex flex-wrap justify-end gap-2">
            <Button size="sm" onClick={onEnterFocusMode}>
              稀客订单专注模式
            </Button>
          </div>

          {autoPrepPreferences.automationEnabled && (
            <RareServiceAutomationPanel
              preferences={autoPrepPreferences}
              busy={autoPrepBusy}
              message={autoPrepMessage}
              paused={autoPrepPaused}
              diagnostics={rareOrderDiagnostics}
              onPreferenceChange={onPreferenceChange}
              onRetryOrder={onRetryRareAutomationOrder}
              onResetOrder={onResetRareAutomationOrder}
            />
          )}

          <div className={DENSE_TWO_COLUMN_GRID}>
            <ListPanel title="当前稀客">
              {activeGuests.length === 0 && <EmptyRow text="暂无稀客" />}
              {activeGuests.map((guest) => {
                const fund = formatGuestFund(guest);
                return (
                  <div key={`${guest.deskCode}-${guest.guestId}-${guest.source}`} className="flex items-center justify-between border-b py-2 text-sm last:border-b-0">
                    <span className="min-w-0 font-medium">
                      <span>{guest.guestName}</span>
                      {fund && <span className="ml-1 text-muted-foreground">· 金钱 {fund}</span>}
                    </span>
                    <span className="text-muted-foreground">桌 {formatDesk(guest.deskCode)} · {guest.source}</span>
                  </div>
                );
              })}
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
              orderSortMode={autoPrepPreferences.serviceOrderSortMode}
              favorites={favorites}
              favoriteBusyKey={favoriteBusyKey}
              favoriteError={favoriteError}
              onToggleRecipeFavorite={onToggleRecipeFavorite}
              onToggleBeverageFavorite={onToggleBeverageFavorite}
            />
          )}
        </TabsContent>

        <TabsContent value="normal" className="space-y-4">
          {autoPrepPreferences.automationEnabled && (
            <NormalServiceAutomationPanel
              preferences={autoPrepPreferences}
              busy={normalOrderBusy}
              message={normalOrderMessage}
              pausedCount={normalOrderPausedCount}
              onPreferenceChange={onPreferenceChange}
            />
          )}

          <ListPanel title={`普客订单诊断 (${normalBusiness?.orders.length ?? 0})`}>
            {autoPrepPreferences.automationEnabled && autoPrepPreferences.autoNormalOrderEnabled ? (
              <div className="mb-3 flex flex-wrap items-center justify-between gap-2 rounded-md border border-border bg-muted/30 px-3 py-2 text-sm">
                <span className="text-muted-foreground">
                  普客自动化会自动制作最早出现的未完成普客料理，并在完成后收至保温箱。
                </span>
                {normalOrderBusy && <Badge variant="secondary">处理中</Badge>}
              </div>
            ) : autoPrepPreferences.automationEnabled ? (
              <div className="mb-3 rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
                开启“启用普客处理”后，可自动制作普客料理并收至保温箱。
              </div>
            ) : (
              <div className="mb-3 rounded-md border border-border bg-muted/30 px-3 py-2 text-sm text-muted-foreground">
                设置页开启“启用自动化（实验性）”后，可启用普客订单自动处理。
              </div>
            )}
            {normalOrderMessage && !autoPrepPreferences.automationEnabled && (
              <div className="mb-3 whitespace-pre-line rounded-md border border-border bg-muted/40 px-3 py-2 text-sm text-muted-foreground">
                {normalOrderMessage}
              </div>
            )}
            {!normalBusiness && <EmptyRow text="普客订单只在经营场景中读取" />}
            {normalBusiness?.error && <EmptyRow text={normalBusiness.error} />}
            {normalBusiness?.orders.length === 0 && !normalBusiness.error && (
              <EmptyRow text={normalBusiness.source || '暂无普客订单'} />
            )}
            {sortNormalOrders(normalBusiness?.orders ?? []).map((order) => (
              <div
                key={`${order.deskCode}-${order.guestName}-${order.foodId}-${order.beverageId}-${order.source}`}
                className="border-b py-2 text-sm last:border-b-0"
              >
                <div className="flex items-center justify-between gap-3">
                  <span className="min-w-0 truncate font-medium" title={order.guestName || '普客'}>
                    {order.guestName || '普客'}
                  </span>
                  <span className="shrink-0 text-muted-foreground">桌 {formatDesk(order.deskCode)}</span>
                </div>
                <div className="mt-1 flex flex-wrap gap-1.5">
                  <Badge variant="outline">料理 {order.foodName || `#${order.foodId}`}</Badge>
                  <Badge variant="outline">酒水 {order.beverageName || `#${order.beverageId}`}</Badge>
                  {order.hasServedFood && <Badge variant="secondary">已有料理</Badge>}
                  {order.hasServedBeverage && <Badge variant="secondary">已有酒水</Badge>}
                  {order.isFulfilled && <Badge variant="secondary">已满足</Badge>}
                  <Badge variant="secondary">{order.source}</Badge>
                </div>
              </div>
            ))}
          </ListPanel>
        </TabsContent>
      </Tabs>
    </div>
  );
}

function ServiceFocusPage({
  recommendations,
  recommendationIssues,
  runtimeSets,
  orderSortMode,
  favorites,
  favoriteBusyKey,
  favoriteError,
  compact,
  recipeLimit,
  beverageLimit,
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
  orderSortMode: ServiceOrderSortMode;
  favorites: FavoriteData;
  favoriteBusyKey: string;
  favoriteError: string;
  compact: boolean;
  recipeLimit: number;
  beverageLimit: number;
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

      {hasOrders ? (
        <CurrentOrderRecommendations
          recommendations={recommendations}
          recommendationIssues={recommendationIssues}
          runtimeSets={runtimeSets}
          orderSortMode={orderSortMode}
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
  orderSortMode,
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
  orderSortMode: ServiceOrderSortMode;
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
    () => sortNightOrderRows([
      ...recommendationIssues.map((issue) => ({ kind: 'issue' as const, order: issue.order, issue })),
      ...recommendations.map((item) => ({ kind: 'recommendation' as const, order: item.order, item })),
    ], orderSortMode),
    [orderSortMode, recommendationIssues, recommendations],
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
          busyKey={busyKey}
          apiToken={apiToken}
          onApply={applyQuantity}
        />
        <InventoryEditColumn
          title="酒水"
          kind="beverage"
          items={beverageRows}
          ownedQty={runtimeSets.ownedBeverageQty}
          busyKey={busyKey}
          apiToken={apiToken}
          onApply={applyQuantity}
        />
      </div>
    </div>
  );
}

function ModTasksPanel({
  runtimeLoaded,
  missions,
}: {
  runtimeLoaded: boolean;
  missions: RuntimeMissionContext | null;
}) {
  const [statusFilter, setStatusFilter] = useState<MissionStatusFilter>('all');

  if (!runtimeLoaded) {
    return <RuntimeUnavailable />;
  }

  const rows = missions?.availableMissions ?? [];
  const filteredRows = rows.filter((mission) => matchesMissionStatusFilter(mission, statusFilter));

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
          <InfoLine label="任务数据" value={missions ? '已读取' : '暂不可用'} />
          <InfoLine label="可推进任务" value={`${filteredRows.length}/${rows.length} 个`} />
          <InfoLine label="扫描状态" value={missions?.source || missions?.error || '暂无'} />
        </CardContent>
      </Card>

      <ListPanel
        title={`可接任务 (${filteredRows.length})`}
        action={(
          <Select value={statusFilter} onValueChange={(value) => setStatusFilter(value as MissionStatusFilter)}>
            <SelectTrigger className="h-8 w-28" data-gamepad-clickable="true">
              <SelectValue>{getMissionStatusFilterLabel(statusFilter)}</SelectValue>
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">全部</SelectItem>
              <SelectItem value="not-started">未接取</SelectItem>
              <SelectItem value="started">已开始</SelectItem>
              <SelectItem value="finished">已完成</SelectItem>
            </SelectContent>
          </Select>
        )}
      >
        {!missions && <EmptyRow text="任务快照暂不可用" />}
        {missions?.error && <EmptyRow text={missions.error} />}
        {rows.length === 0 && missions && !missions.error && (
          <EmptyRow text="当前进度未读取到可接或正在推进的任务" />
        )}
        {rows.length > 0 && filteredRows.length === 0 && (
          <EmptyRow text="当前筛选条件下没有任务" />
        )}
        {filteredRows.map((mission) => {
          const places = mission.places?.filter(Boolean) ?? [];
          const shouldShowMissingPlace = places.length === 0 && !mission.started && !mission.finished;
          return (
          <div
            key={`${mission.characterLabel}-${mission.label}`}
            className="border-b py-2 text-sm last:border-b-0"
            data-gamepad-row="true"
            data-gamepad-row-key={`task:${mission.characterLabel}:${mission.label}`}
          >
            <div className="flex items-center justify-between gap-3">
              <span className="min-w-0 truncate font-medium" title={mission.title || mission.label}>
                {mission.title || mission.label}
              </span>
              <span className="shrink-0 text-muted-foreground">{mission.characterName || mission.characterLabel}</span>
            </div>
            <div className="mt-1 flex flex-wrap gap-1.5">
              <Badge variant="outline">{mission.label}</Badge>
              <Badge variant="secondary">{mission.source}</Badge>
              <Badge variant={mission.started ? 'default' : 'outline'}>{mission.started ? '已开始' : '未接取'}</Badge>
              {mission.finished && <Badge variant="secondary">已完成</Badge>}
              {places.map((place) => <Badge key={place} variant="outline">场景 {place}</Badge>)}
              {shouldShowMissingPlace && <Badge variant="outline">场景 未读取</Badge>}
            </div>
          </div>
          );
        })}
      </ListPanel>
    </div>
  );
}

function InventoryEditColumn<TItem extends IIngredient | IBeverage>({
  title,
  kind,
  items,
  ownedQty,
  busyKey,
  apiToken,
  onApply,
}: {
  title: string;
  kind: 'ingredient' | 'beverage';
  items: TItem[];
  ownedQty: Record<number, number>;
  busyKey: string;
  apiToken: string;
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
          const busy = busyKey === key;

          return (
            <div
              key={key}
              className="rounded-md border border-border/80 px-2 py-1.5 text-sm"
              data-gamepad-row="true"
              data-gamepad-row-key={`inventory:${key}`}
            >
              <div className="grid grid-cols-[minmax(0,1fr)_auto] items-center gap-2">
                <div className="min-w-0 pr-1">
                  <div className="truncate font-medium" title={item.name}>{item.name}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    ID {item.id} · 当前 {quantity < 0 ? '无限' : quantity} · 单价 {item.price}
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable || busy}
                    data-gamepad-focus-key={`inventory:${key}:sub10`}
                    onClick={() => onApply(kind, item.id, quantity - 10)}
                  >
                    -10
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable || busy}
                    data-gamepad-focus-key={`inventory:${key}:add10`}
                    onClick={() => onApply(kind, item.id, quantity + 10)}
                  >
                    +10
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    disabled={!editable || busy}
                    data-gamepad-focus-key={`inventory:${key}:set99`}
                    onClick={() => onApply(kind, item.id, 99)}
                  >
                    99
                  </Button>
                </div>
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
    if (!apiToken) {
      setSettings(null);
      setLogs(null);
      setError('未收到本地 API Token。');
      return;
    }
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
    if (!apiToken) return;
    refreshLogs();
    const timer = window.setInterval(refreshLogs, 2000);
    return () => window.clearInterval(timer);
  }, [apiToken, refreshLogs]);

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
  onPreferenceChange,
  onThemeModeChange,
  onServiceFocusCompactChange,
}: {
  preferences: CompanionPreferences;
  themeMode: ThemeMode;
  serviceFocusCompact: boolean;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onThemeModeChange: (mode: ThemeMode) => void;
  onServiceFocusCompactChange: (value: boolean) => void;
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
          <div className="text-xs text-muted-foreground">
            料理和酒水显示数量在进入专注模式后直接调整，设置会自动记住。
          </div>
        </div>
      </ListPanel>

      <ListPanel title="推荐">
        <div className="space-y-4">
          <SwitchControl
            label="排除缺失厨具"
            checked={preferences.filterMissingCookers}
            onCheckedChange={(filterMissingCookers) => onPreferenceChange({ filterMissingCookers })}
          />
          <div className="text-xs text-muted-foreground">
            进入经营场景后，若读取到已摆放厨具，推荐列表会隐藏当前场景无法制作的料理。
          </div>
          <SettingChoice
            label="经营中订单排序"
            value={preferences.serviceOrderSortMode}
            options={[
              { value: 'ordered', label: '点单顺序', description: '按订单首次出现时间排列，保持当前默认行为。' },
              { value: 'guest', label: '稀客分组', description: '同一稀客的订单放在一起，组内仍按点单先后排列。' },
            ]}
            onChange={(serviceOrderSortMode) => onPreferenceChange({ serviceOrderSortMode })}
          />
          <SwitchControl
            label="游戏界面置顶推荐（实验性）"
            checked={preferences.gameUiPinningEnabled}
            onCheckedChange={(gameUiPinningEnabled) => onPreferenceChange({ gameUiPinningEnabled })}
          />
          <div className="text-xs text-muted-foreground">
            打开料理或酒水选择界面时，尝试把当前第一笔订单的推荐材料、料理和酒水排到前面；失败时只记录诊断，不修改库存。
          </div>
          <SwitchControl
            label="目标厨具高亮（实验性）"
            checked={preferences.cookerHighlightEnabled}
            onCheckedChange={(cookerHighlightEnabled) => onPreferenceChange({ cookerHighlightEnabled })}
          />
          <div className="text-xs text-muted-foreground">
            经营中有推荐目标厨具时，尝试让对应已摆放厨具显示黄色脉冲高亮；只改变可见提示，不自动操作厨具。
          </div>
        </div>
      </ListPanel>

      <ListPanel title="料理排序">
        <SortRulesControl
          rules={preferences.recipeSortRules}
          options={RECIPE_SORT_OPTIONS}
          onChange={(recipeSortRules) => onPreferenceChange({ recipeSortRules })}
          onReset={() => onPreferenceChange({ recipeSortRules: buildDefaultSortRules(RECIPE_SORT_OPTIONS) })}
        />
      </ListPanel>

      <ListPanel title="酒水排序">
        <SortRulesControl
          rules={preferences.beverageSortRules}
          options={BEVERAGE_SORT_OPTIONS}
          onChange={(beverageSortRules) => onPreferenceChange({ beverageSortRules })}
          onReset={() => onPreferenceChange({ beverageSortRules: buildDefaultSortRules(BEVERAGE_SORT_OPTIONS) })}
        />
      </ListPanel>

      <ListPanel title="自动化">
        <div className="space-y-4">
          <SwitchControl
            label="启用自动化（实验性）"
            checked={preferences.automationEnabled}
            onCheckedChange={(automationEnabled) => onPreferenceChange({ automationEnabled })}
          />
          <div className="text-xs text-muted-foreground">
            关闭时不会显示或执行任何自动化动作；开启后可在“经营中”页面配置具体子功能。
          </div>
        </div>
      </ListPanel>
    </div>
  );
}

function RareServiceAutomationPanel({
  preferences,
  busy,
  message,
  paused,
  diagnostics,
  onPreferenceChange,
  onRetryOrder,
  onResetOrder,
}: {
  preferences: CompanionPreferences;
  busy: boolean;
  message: string;
  paused: boolean;
  diagnostics: RareAutoOrderDiagnostic[];
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  return (
    <ListPanel title="稀客自动化（实验性）">
      <div className="space-y-2">
        <SwitchControl
          label="自动完成订单"
          checked={preferences.autoPrepCompleteOrder}
          onCheckedChange={(autoPrepCompleteOrder) => onPreferenceChange({ autoPrepCompleteOrder })}
        />
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
      </div>
      <RareAutoPrepStatus
        busy={busy}
        paused={paused}
        message={message}
        preferences={preferences}
        diagnostics={diagnostics}
        onRetryOrder={onRetryOrder}
        onResetOrder={onResetOrder}
      />
    </ListPanel>
  );
}

function NormalServiceAutomationPanel({
  preferences,
  busy,
  message,
  pausedCount,
  onPreferenceChange,
}: {
  preferences: CompanionPreferences;
  busy: boolean;
  message: string;
  pausedCount: number;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
}) {
  return (
    <ListPanel title="普客自动化（实验性）">
      <div className="space-y-2">
        <SwitchControl
          label="启用普客处理"
          checked={preferences.autoNormalOrderEnabled}
          onCheckedChange={(autoNormalOrderEnabled) => onPreferenceChange({ autoNormalOrderEnabled })}
        />
        {preferences.autoNormalOrderEnabled && (
          <>
            <SwitchControl
              label="自动开始料理"
              checked={preferences.autoNormalStartCooking}
              onCheckedChange={(autoNormalStartCooking) => onPreferenceChange({ autoNormalStartCooking })}
            />
            <SwitchControl
              label="自动收取料理"
              checked={preferences.autoNormalCollectCooking}
              onCheckedChange={(autoNormalCollectCooking) => onPreferenceChange({ autoNormalCollectCooking })}
            />
            <SwitchControl
              label="出错时暂停"
              checked={preferences.autoNormalStopOnError}
              onCheckedChange={(autoNormalStopOnError) => onPreferenceChange({ autoNormalStopOnError })}
            />
          </>
        )}
      </div>
      <NormalAutoPrepStatus busy={busy} pausedCount={pausedCount} message={message} preferences={preferences} />
    </ListPanel>
  );
}

function RareAutoPrepStatus({
  busy,
  paused,
  message,
  preferences,
  diagnostics,
  onRetryOrder,
  onResetOrder,
}: {
  busy: boolean;
  paused: boolean;
  message: string;
  preferences: CompanionPreferences;
  diagnostics: RareAutoOrderDiagnostic[];
  onRetryOrder: (orderKey: string) => void;
  onResetOrder: (orderKey: string) => void;
}) {
  if (!message && !paused && diagnostics.length === 0) return null;

  return (
    <div className="mt-3 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
      <div className="font-medium text-foreground">稀客自动化{busy ? '处理中' : '状态'}</div>
      {diagnostics.length > 0 && (
        <div className="mt-2 space-y-2">
          {diagnostics.map((diagnostic) => (
            <div key={diagnostic.orderKey} className="rounded-md border border-border bg-background/70 px-2.5 py-2">
              <div className="flex flex-wrap items-start justify-between gap-2">
                <div className="min-w-0">
                  <div className="truncate font-medium text-foreground">{diagnostic.title}</div>
                  <div className="mt-0.5 text-xs text-muted-foreground">
                    料理 {diagnostic.foodTag || '无'} · 酒水 {diagnostic.beverageTag || '无'}
                  </div>
                </div>
                <div className="flex shrink-0 gap-1.5">
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => onRetryOrder(diagnostic.orderKey)}
                    disabled={busy || !diagnostic.paused}
                    data-gamepad-focus-key={`rare-auto:${diagnostic.orderKey}:retry`}
                  >
                    重试
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => onResetOrder(diagnostic.orderKey)}
                    disabled={busy}
                    data-gamepad-focus-key={`rare-auto:${diagnostic.orderKey}:reset`}
                  >
                    重置
                  </Button>
                </div>
              </div>
              <div className="mt-2 grid grid-cols-2 gap-x-3 gap-y-1 text-xs text-muted-foreground md:grid-cols-4">
                <InfoLine label="料理" value={diagnostic.recipeName || '未选择'} />
                <InfoLine label="酒水" value={diagnostic.beverageName || '未选择'} />
                <InfoLine label="步骤" value={`${diagnostic.stepLabel} · ${diagnostic.stepSeconds}秒`} />
                <InfoLine
                  label="计数"
                  value={`重试 ${diagnostic.retryCount}/${MAX_AUTO_STEP_RETRIES} · 回退 ${diagnostic.rollbackCount}/${MAX_AUTO_ROLLBACKS}`}
                />
              </div>
              <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
                <Badge variant={diagnostic.paused ? 'destructive' : 'secondary'}>
                  {diagnostic.paused ? '暂停' : '运行'}
                </Badge>
                <Badge variant={diagnostic.prepared ? 'secondary' : 'outline'}>
                  料理{diagnostic.prepared ? '已开锅' : '待处理'}
                </Badge>
                <Badge variant={diagnostic.beverageHandled ? 'secondary' : 'outline'}>
                  酒水{diagnostic.beverageHandled ? '已处理' : '待处理'}
                </Badge>
              </div>
              {diagnostic.lastError && (
                <div className="mt-1 text-xs text-muted-foreground">最近：{diagnostic.lastError}</div>
              )}
            </div>
          ))}
        </div>
      )}
      {message && <div className="mt-2 whitespace-pre-line text-muted-foreground">{message}</div>}
      <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
        <Badge variant={paused ? 'destructive' : 'secondary'}>{paused ? '已暂停' : '运行中'}</Badge>
        <Badge variant="outline">每轮最多 {MAX_RARE_AUTO_ORDERS_PER_TICK}</Badge>
        <Badge variant={preferences.autoPrepCompleteOrder ? 'secondary' : 'outline'}>完成 {preferences.autoPrepCompleteOrder ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepTakeBeverage ? 'secondary' : 'outline'}>取酒 {preferences.autoPrepTakeBeverage ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepStartCooking ? 'secondary' : 'outline'}>料理 {preferences.autoPrepStartCooking ? '开' : '关'}</Badge>
        {preferences.autoPrepStartCooking && <Badge variant="secondary">QTE 自动完成</Badge>}
        <Badge variant={preferences.autoPrepCollectCooking ? 'secondary' : 'outline'}>收取 {preferences.autoPrepCollectCooking ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoPrepFavoritesOnly ? 'secondary' : 'outline'}>收藏限定 {preferences.autoPrepFavoritesOnly ? '开' : '关'}</Badge>
      </div>
    </div>
  );
}

function NormalAutoPrepStatus({
  busy,
  pausedCount,
  message,
  preferences,
}: {
  busy: boolean;
  pausedCount: number;
  message: string;
  preferences: CompanionPreferences;
}) {
  if (!message && pausedCount === 0) return null;

  return (
    <div className="mt-3 rounded-md border border-border bg-muted/40 px-3 py-2 text-sm">
      <div className="font-medium text-foreground">普客自动化{busy ? '处理中' : '状态'}</div>
      {message && <div className="mt-1 whitespace-pre-line text-muted-foreground">{message}</div>}
      <div className="mt-2 flex flex-wrap gap-1.5 text-xs">
        <Badge variant={pausedCount > 0 ? 'destructive' : 'secondary'}>暂停订单 {pausedCount}</Badge>
        <Badge variant="outline">每轮最多 {MAX_NORMAL_AUTO_ORDERS_PER_TICK}</Badge>
        <Badge variant={preferences.autoNormalOrderEnabled ? 'secondary' : 'outline'}>启用 {preferences.autoNormalOrderEnabled ? '开' : '关'}</Badge>
        <Badge variant={preferences.autoNormalStartCooking ? 'secondary' : 'outline'}>料理 {preferences.autoNormalStartCooking ? '开' : '关'}</Badge>
        {preferences.autoNormalStartCooking && <Badge variant="secondary">QTE 自动完成</Badge>}
        <Badge variant={preferences.autoNormalCollectCooking ? 'secondary' : 'outline'}>收取 {preferences.autoNormalCollectCooking ? '开' : '关'}</Badge>
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

function formatGuestFund(guest: NightBusinessGuest): string {
  if (typeof guest.fund !== 'number' || !Number.isFinite(guest.fund)) return '';
  return String(Math.trunc(guest.fund));
}

function ListPanel({ title, action, children }: { title: string; action?: ReactNode; children: ReactNode }) {
  return (
    <Card>
      <CardContent className="p-4">
        <div className="mb-2 flex items-center justify-between gap-3">
          <h2 className="text-base font-semibold">{title}</h2>
          {action}
        </div>
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
        aria-label="窗口透明度"
        data-gamepad-slider="true"
        data-gamepad-step="1"
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

function SortRulesControl<K extends string>({
  rules,
  options,
  onChange,
  onReset,
}: {
  rules: SortRule<K>[];
  options: SortOption<K>[];
  onChange: (rules: SortRule<K>[]) => void;
  onReset: () => void;
}) {
  const normalizedRules = normalizeSortRules(rules, options);
  const updateRule = (key: K, next: Partial<SortRule<K>>) => {
    onChange(normalizedRules.map((rule) => (rule.key === key ? { ...rule, ...next } : rule)));
  };
  const moveRule = (index: number, offset: number) => {
    const nextIndex = index + offset;
    if (nextIndex < 0 || nextIndex >= normalizedRules.length) return;
    const next = [...normalizedRules];
    const [item] = next.splice(index, 1);
    next.splice(nextIndex, 0, item);
    onChange(next);
  };

  return (
    <div className="space-y-2">
      {normalizedRules.map((rule, index) => {
        const label = getSortOptionLabel(options, rule.key);
        return (
          <div
            key={rule.key}
            className="grid grid-cols-[minmax(0,1fr)_auto_auto] items-center gap-2 rounded-md border border-border bg-background/50 p-2 text-sm"
          >
            <label className="flex min-w-0 items-center gap-2">
              <button
                type="button"
                role="switch"
                aria-checked={rule.enabled}
                onClick={() => updateRule(rule.key, { enabled: !rule.enabled })}
                className={`relative h-5 w-9 shrink-0 rounded-full border transition-colors ${
                  rule.enabled ? 'border-primary bg-primary' : 'border-border bg-muted'
                }`}
              >
                <span
                  className={`absolute left-0 top-1/2 size-4 -translate-y-1/2 rounded-full bg-background shadow-sm transition-transform ${
                    rule.enabled ? 'translate-x-4' : 'translate-x-0.5'
                  }`}
                />
              </button>
              <span className="truncate">{label}</span>
            </label>
            <Button
              type="button"
              size="sm"
              variant="outline"
              className="h-8 px-2"
              onClick={() => updateRule(rule.key, { direction: rule.direction === 'desc' ? 'asc' : 'desc' })}
            >
              {rule.direction === 'desc' ? '降序' : '升序'}
            </Button>
            <div className="flex gap-1">
              <Button
                type="button"
                size="sm"
                variant="outline"
                className="h-8 w-8 p-0"
                title="上移"
                disabled={index === 0}
                onClick={() => moveRule(index, -1)}
              >
                <ArrowUp className="size-3.5" />
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                className="h-8 w-8 p-0"
                title="下移"
                disabled={index === normalizedRules.length - 1}
                onClick={() => moveRule(index, 1)}
              >
                <ArrowDown className="size-3.5" />
              </Button>
            </div>
          </div>
        );
      })}
      <Button type="button" size="sm" variant="outline" className="mt-1 gap-1.5" onClick={onReset}>
        <RotateCcw className="size-3.5" />
        恢复默认排序
      </Button>
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
  const visiblePreferenceRecipes = visibleRecipes.length >= 3
    ? []
    : item.preferenceRecipes.slice(0, Math.max(0, normalizeFocusRecommendationLimit(recipeLimit) - visibleRecipes.length));
  const visiblePreferenceBeverages = visibleBeverages.length >= 3
    ? []
    : item.preferenceBeverages.slice(0, Math.max(0, normalizeFocusRecommendationLimit(beverageLimit) - visibleBeverages.length));
  const targetCookerName = visibleRecipes[0]?.recipe.cooker
    ?? visiblePreferenceRecipes[0]?.recipe.cooker
    ?? '';

  return (
    <div className={compact ? 'rounded-md border border-border p-2' : 'rounded-md border border-border p-3'}>
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div>
          <div className="text-sm font-semibold">{item.customer.name} · 桌 {formatDesk(item.order.deskCode)}</div>
          <div className="mt-1 flex flex-wrap gap-1.5">
            <Badge variant="outline">料理 {item.order.foodTag || '无'}</Badge>
            <Badge variant="outline">酒水 {item.order.beverageTag || '无'}</Badge>
            {targetCookerName && (
              <Badge className="border-amber-300/70 bg-amber-300/18 text-amber-100">
                目标厨具 {targetCookerName}
              </Badge>
            )}
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
            {visiblePreferenceRecipes.length > 0 && (
              <div className={compact ? 'pt-1' : 'pt-2'}>
                <div className="mb-1 text-xs font-medium text-muted-foreground">喜好备选（不满足点单）</div>
                <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
                  {visiblePreferenceRecipes.map((recipe, index) => (
                    <RecipeRecommendationRow
                      key={`fallback-${recipe.recipe.id}-${index}`}
                      recipe={recipe}
                      index={visibleRecipes.length + index}
                      ownedIngredientQty={runtimeSets?.ownedIngredientQty ?? {}}
                      compact={compact}
                    />
                  ))}
                </div>
              </div>
            )}
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
            {visiblePreferenceBeverages.length > 0 && (
              <div className={compact ? 'pt-1' : 'pt-2'}>
                <div className="mb-1 text-xs font-medium text-muted-foreground">喜好备选（不满足点单）</div>
                <div className={compact ? 'space-y-1.5' : 'space-y-2'}>
                  {visiblePreferenceBeverages.map((beverage, index) => (
                    <BeverageRecommendationRow
                      key={`fallback-${beverage.beverage.id}`}
                      beverage={beverage}
                      index={visibleBeverages.length + index}
                      ownedBeverageQty={runtimeSets?.ownedBeverageQty ?? {}}
                      compact={compact}
                    />
                  ))}
                </div>
              </div>
            )}
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
      data-gamepad-row-key={onToggleFavorite ? `recipe:${favoriteKey}` : undefined}
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
        </div>
        {onToggleFavorite && (
          <Button
            type="button"
            size="icon-xs"
            variant={favorite ? 'default' : 'outline'}
            disabled={busy}
            aria-label={favorite ? '取消收藏该料理方案' : '收藏该料理方案'}
            data-gamepad-favorite="true"
            data-gamepad-focus-key={`recipe-favorite:${favoriteKey}`}
            title={favorite ? '取消收藏该料理方案' : '收藏该料理方案'}
            onClick={onToggleFavorite}
          >
            <Star className={favorite ? 'size-3 fill-current' : 'size-3'} />
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
      data-gamepad-row-key={onToggleFavorite ? `beverage:${favoriteKey}` : undefined}
      tabIndex={onToggleFavorite ? 0 : undefined}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs text-muted-foreground">#{index + 1}</span>
          <span className="font-medium">
            {beverage.beverage.name}{formatQtySuffix(ownedBeverageQty[beverage.beverage.id])}
          </span>
          {beverage.meetsRequiredBev && <Badge variant="secondary">满足点单</Badge>}
        </div>
        {onToggleFavorite && (
          <Button
            type="button"
            size="icon-xs"
            variant={favorite ? 'default' : 'outline'}
            disabled={busy}
            aria-label={favorite ? '取消收藏该酒水' : '收藏该酒水'}
            data-gamepad-favorite="true"
            data-gamepad-focus-key={`beverage-favorite:${favoriteKey}`}
            title={favorite ? '取消收藏该酒水' : '收藏该酒水'}
            onClick={onToggleFavorite}
          >
            <Star className={favorite ? 'size-3 fill-current' : 'size-3'} />
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

async function publishGameUiPinningTarget(
  endpoint: string,
  apiToken: string,
  enabled: boolean,
  highlightEnabled: boolean,
  target: GameUiPinningTarget | null,
): Promise<void> {
  const params = new URLSearchParams({
    enabled: String(enabled),
    highlightEnabled: String(highlightEnabled),
    recipeId: target ? String(target.recipeId) : '-1',
    recipeName: target?.recipeName ?? '',
    ingredientIds: target ? target.ingredientIds.join(',') : '',
    beverageId: target ? String(target.beverageId) : '-1',
    beverageName: target?.beverageName ?? '',
    cookerTypeId: target ? String(target.cookerTypeId) : '-1',
    cookerName: target?.cookerName ?? '',
  });
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), 2200);

  try {
    await readLocalApiJson<{ ok: boolean }>(
      endpoint,
      apiToken,
      `/ui-pinning/target?${params.toString()}`,
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

async function completeFirstNormalOrder(
  endpoint: string,
  apiToken: string,
  order: NormalBusinessOrder,
  preferences: CompanionPreferences,
): Promise<OrderPreparationResponse> {
  const recipe = RECIPE_BY_FOOD_ID.get(order.foodId)
    ?? RECIPES.find((item) => item.recipeId === order.foodId)
    ?? null;
  const params = new URLSearchParams({
    orderKey: order.orderKey ?? '',
    deskCode: String(order.deskCode),
    guestName: order.guestName || '普客',
    foodId: String(order.foodId),
    recipeId: recipe ? String(recipe.recipeId) : '-1',
    recipeName: order.foodName || recipe?.name || '',
    beverageId: String(order.beverageId),
    beverageName: order.beverageName || BEVERAGE_NAME_BY_ID.get(order.beverageId) || '',
    autoStartCooking: String(preferences.autoNormalStartCooking),
    autoCollectCooking: String(preferences.autoNormalCollectCooking),
    stopOnError: String(preferences.autoNormalStopOnError),
  });
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), 5000);

  try {
    return await readLocalApiJson<OrderPreparationResponse>(
      endpoint,
      apiToken,
      `/orders/normal/complete-first?${params.toString()}`,
      abortController.signal,
    );
  } finally {
    window.clearTimeout(timeoutId);
  }
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
    placedCookerTypeIds: new Set(runtime.placedCookerTypeIds ?? []),
    placedCookerNames: buildPlacedCookerNameSet(runtime),
    hasCookerSnapshot: (runtime.placedCookers?.length ?? 0) > 0 || (runtime.placedCookerTypeIds?.length ?? 0) > 0,
  };
}

function buildAutomationCookerCapacity(runtime: RecommendationStateSnapshot | null | undefined): Map<string, number> {
  const capacity = new Map<string, number>();
  if (!runtime) return capacity;

  for (const cooker of runtime.placedCookers ?? []) {
    const keys = new Set<string>();
    for (const typeName of cooker.typeNames ?? []) {
      const normalized = normalizeCookerName(typeName);
      if (normalized) keys.add(normalized);
    }

    for (const typeId of cooker.typeIds ?? []) {
      const mapped = COOKER_TYPE_NAME_BY_ID.get(typeId);
      const normalized = normalizeCookerName(mapped);
      if (normalized) keys.add(normalized);
    }

    const name = normalizeCookerName(cooker.name);
    if (name) keys.add(name);

    for (const key of keys) {
      capacity.set(key, (capacity.get(key) ?? 0) + 1);
    }
  }

  if (capacity.size === 0) {
    for (const typeId of runtime.placedCookerTypeIds ?? []) {
      const key = normalizeCookerName(COOKER_TYPE_NAME_BY_ID.get(typeId));
      if (!key) continue;
      capacity.set(key, Math.max(1, capacity.get(key) ?? 0));
    }
  }

  return capacity;
}

function getCookerSlotCapacity(key: string, capacity: Map<string, number>): number {
  return Math.max(1, capacity.get(key) ?? 1);
}

function getRareCookerRequirement(recipe: IRareRecipeResult | null): CookerRequirement | null {
  if (!recipe) return null;
  return getRecipeCookerRequirement(recipe.recipe);
}

function getNormalCookerRequirement(order: NormalBusinessOrder): CookerRequirement | null {
  const recipe = getNormalOrderRecipe(order);
  if (!recipe) return null;
  return getRecipeCookerRequirement(recipe);
}

function getRecipeCookerRequirement(recipe: IRecipe | null | undefined): CookerRequirement | null {
  const key = normalizeCookerName(recipe?.cooker);
  if (!key) return null;
  return {
    key,
    label: key,
  };
}

function getNormalOrderRecipe(order: NormalBusinessOrder): IRecipe | null {
  return RECIPE_BY_FOOD_ID.get(order.foodId)
    ?? RECIPES.find((item) => item.recipeId === order.foodId)
    ?? null;
}

function buildNormalCookerDemand(
  orders: NormalBusinessOrder[],
  states: Map<string, NormalAutoOrderState>,
  preferences: CompanionPreferences,
  runtime: RecommendationStateSnapshot | null | undefined,
  now: number,
): NormalCookerDemand {
  const counts = new Map<string, number>();
  const labels = new Map<string, string[]>();
  if (!preferences.automationEnabled || !preferences.autoNormalOrderEnabled || !preferences.autoNormalStartCooking) {
    return { counts, labels };
  }

  const capacity = buildAutomationCookerCapacity(runtime);
  let reservedOrders = 0;
  for (const order of sortNormalOrders(orders).filter((item) => !item.isFulfilled)) {
    const state = states.get(buildNormalAutoOrderKey(order));
    if (!shouldAttemptNormalCooking(order, state, preferences, now)) continue;

    const cooker = getNormalCookerRequirement(order);
    if (!cooker) continue;

    const limit = getCookerSlotCapacity(cooker.key, capacity);
    const used = counts.get(cooker.key) ?? 0;
    if (used >= limit) continue;

    counts.set(cooker.key, used + 1);
    const items = labels.get(cooker.key) ?? [];
    items.push(`桌 ${formatDesk(order.deskCode)} · ${order.foodName || `#${order.foodId}`}`);
    labels.set(cooker.key, items);
    reservedOrders += 1;
    if (reservedOrders >= MAX_NORMAL_AUTO_ORDERS_PER_TICK) break;
  }

  return { counts, labels };
}

function shouldAttemptNormalCooking(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalStartCooking) return false;
  if (order.hasServedFood || order.foodId < 0) return false;
  if (state?.collected) return false;
  if (state?.paused && !isRecoverableNormalPausedState(state, now)) return false;
  return !state?.prepared || isNormalOrderPreparedStale(state, now);
}

function shouldConfirmNormalCollection(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  preferences: CompanionPreferences,
  now: number,
): boolean {
  if (!preferences.autoNormalCollectCooking) return false;
  if (order.hasServedFood || order.foodId < 0) return false;
  if (!state?.prepared || state.collected) return false;
  if (state.paused && !isRecoverableNormalPausedState(state, now)) return false;
  return isNormalOrderPreparedStale(state, now);
}

function reserveAutomationCookerSlot(
  cycle: AutomationCookerCycle,
  cooker: CookerRequirement | null,
  label: string,
  capacity: Map<string, number>,
): CookerReservationResult {
  if (!cooker) return { ok: true, message: '' };
  const limit = getCookerSlotCapacity(cooker.key, capacity);
  const used = cycle.used.get(cooker.key) ?? 0;
  if (used >= limit) {
    const owners = cycle.labels.get(cooker.key) ?? [];
    return {
      ok: false,
      message: `等待厨具 ${cooker.label}：本轮可用容量 ${limit} 已预约${owners.length > 0 ? `（${owners.join('、')}）` : ''}。`,
    };
  }

  cycle.used.set(cooker.key, used + 1);
  cycle.labels.set(cooker.key, [...(cycle.labels.get(cooker.key) ?? []), label]);
  return { ok: true, message: '' };
}

function reserveRareCookerSlot(
  cycle: AutomationCookerCycle,
  cooker: CookerRequirement | null,
  label: string,
  capacity: Map<string, number>,
  normalDemand: NormalCookerDemand,
): CookerReservationResult {
  if (!cooker) return { ok: true, message: '' };
  const limit = getCookerSlotCapacity(cooker.key, capacity);
  const used = cycle.used.get(cooker.key) ?? 0;
  const normalReserved = normalDemand.counts.get(cooker.key) ?? 0;
  if (normalReserved > 0 && used + normalReserved >= limit) {
    const normalLabels = normalDemand.labels.get(cooker.key) ?? [];
    return {
      ok: false,
      message: `等待厨具 ${cooker.label}：本轮优先给普客订单使用${normalLabels.length > 0 ? `（${normalLabels.join('、')}）` : ''}。`,
    };
  }

  return reserveAutomationCookerSlot(cycle, cooker, label, capacity);
}

function buildPlacedCookerNameSet(runtime: RecommendationStateSnapshot): Set<string> {
  const names = new Set<string>();
  for (const typeId of runtime.placedCookerTypeIds ?? []) {
    const mapped = COOKER_TYPE_NAME_BY_ID.get(typeId);
    if (mapped) names.add(normalizeCookerName(mapped));
  }
  for (const cooker of runtime.placedCookers ?? []) {
    for (const name of [cooker.name, ...(cooker.typeNames ?? [])]) {
      const normalized = normalizeCookerName(name);
      if (normalized) names.add(normalized);
    }
  }
  return names;
}

function shouldKeepRecipeForCooker(
  recipe: IRareRecipeResult,
  runtimeSets: RuntimeSets | null,
  filterMissingCookers: boolean,
): boolean {
  if (!filterMissingCookers || !runtimeSets?.hasCookerSnapshot) return true;
  const requiredCooker = normalizeCookerName(recipe.recipe.cooker);
  if (!requiredCooker) return true;
  return runtimeSets.placedCookerNames.has(requiredCooker);
}

function normalizeCookerName(value: string | null | undefined): string {
  const normalized = (value ?? '').trim();
  if (!normalized) return '';
  return COOKER_NAME_ALIASES.get(normalized) ?? normalized;
}

function resolveCookerTypeId(value: string | null | undefined): number {
  const normalized = normalizeCookerName(value);
  if (!normalized) return -1;

  for (const [typeId, name] of COOKER_TYPE_NAME_BY_ID) {
    if (normalizeCookerName(name) === normalized) return typeId;
  }

  return -1;
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

function sortNightOrders(
  orders: NightBusinessOrder[],
  mode: ServiceOrderSortMode = 'ordered',
): NightBusinessOrder[] {
  const groupFirstSeen = buildOrderGroupFirstSeen(orders);
  return [...orders].sort((left, right) => compareNightOrders(left, right, mode, groupFirstSeen));
}

function sortNightOrderRows<T extends { order: NightBusinessOrder }>(
  rows: T[],
  mode: ServiceOrderSortMode,
): T[] {
  const groupFirstSeen = buildOrderGroupFirstSeen(rows.map((row) => row.order));
  return [...rows].sort((left, right) => compareNightOrders(left.order, right.order, mode, groupFirstSeen));
}

function sortNormalOrders(orders: NormalBusinessOrder[]): NormalBusinessOrder[] {
  return [...orders].sort(compareNormalOrdersByTime);
}

function compareNormalOrdersByTime(left: NormalBusinessOrder, right: NormalBusinessOrder): number {
  const leftSeenAt = getNormalOrderSeenTime(left);
  const rightSeenAt = getNormalOrderSeenTime(right);
  if (leftSeenAt !== rightSeenAt) return leftSeenAt - rightSeenAt;
  if (left.deskCode !== right.deskCode) return left.deskCode - right.deskCode;
  const foodCompare = left.foodName.localeCompare(right.foodName, 'zh-Hans-CN');
  if (foodCompare !== 0) return foodCompare;
  return left.beverageName.localeCompare(right.beverageName, 'zh-Hans-CN');
}

function getNormalOrderSeenTime(order: NormalBusinessOrder): number {
  if (!order.firstSeenAtUtc) return Number.MAX_SAFE_INTEGER;
  const time = Date.parse(order.firstSeenAtUtc);
  return Number.isFinite(time) ? time : Number.MAX_SAFE_INTEGER;
}

function compareNightOrders(
  left: NightBusinessOrder,
  right: NightBusinessOrder,
  mode: ServiceOrderSortMode = 'ordered',
  groupFirstSeen: Map<string, number> | null = null,
): number {
  if (mode === 'guest') {
    const leftGroupKey = getOrderGuestGroupKey(left);
    const rightGroupKey = getOrderGuestGroupKey(right);
    if (leftGroupKey !== rightGroupKey) {
      const leftGroupSeenAt = groupFirstSeen?.get(leftGroupKey) ?? getOrderSeenTime(left);
      const rightGroupSeenAt = groupFirstSeen?.get(rightGroupKey) ?? getOrderSeenTime(right);
      if (leftGroupSeenAt !== rightGroupSeenAt) return leftGroupSeenAt - rightGroupSeenAt;
      const groupCompare = compareOrderGroupIdentity(left, right);
      if (groupCompare !== 0) return groupCompare;
    }
  }

  return compareNightOrdersByTime(left, right);
}

function compareNightOrdersByTime(left: NightBusinessOrder, right: NightBusinessOrder): number {
  const leftSeenAt = getOrderSeenTime(left);
  const rightSeenAt = getOrderSeenTime(right);
  if (leftSeenAt !== rightSeenAt) return leftSeenAt - rightSeenAt;
  if (left.deskCode !== right.deskCode) return left.deskCode - right.deskCode;
  return left.guestName.localeCompare(right.guestName, 'zh-Hans-CN');
}

function buildOrderGroupFirstSeen(orders: NightBusinessOrder[]): Map<string, number> {
  const result = new Map<string, number>();
  for (const order of orders) {
    const key = getOrderGuestGroupKey(order);
    const seenAt = getOrderSeenTime(order);
    const current = result.get(key);
    if (current === undefined || seenAt < current) result.set(key, seenAt);
  }
  return result;
}

function getOrderGuestGroupKey(order: NightBusinessOrder): string {
  if (order.guestId !== null && order.guestId !== undefined && order.guestId >= 0) {
    return `id:${order.guestId}`;
  }
  return `name:${order.guestName.trim()}|desk:${order.deskCode}`;
}

function compareOrderGroupIdentity(left: NightBusinessOrder, right: NightBusinessOrder): number {
  const nameCompare = left.guestName.localeCompare(right.guestName, 'zh-Hans-CN');
  if (nameCompare !== 0) return nameCompare;
  const leftGuestId = left.guestId ?? Number.MAX_SAFE_INTEGER;
  const rightGuestId = right.guestId ?? Number.MAX_SAFE_INTEGER;
  if (leftGuestId !== rightGuestId) return leftGuestId - rightGuestId;
  return left.deskCode - right.deskCode;
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
  preferences: CompanionPreferences,
): { recommendations: OrderRecommendation[]; recommendationIssues: RecommendationIssue[] } {
  if (orders.length === 0) return { recommendations: [], recommendationIssues: [] };
  const sortedOrders = sortNightOrders(orders, preferences.serviceOrderSortMode);
  if (!runtime) {
    return {
      recommendations: [],
      recommendationIssues: sortedOrders.map((order) => ({ order, message: '运行时推荐数据暂不可用。' })),
    };
  }

  const runtimeSets = buildRuntimeSets(runtime);
  if (!runtimeSets) return { recommendations: [], recommendationIssues: [] };

  const stateSignature = buildRecommendationStateSignature(runtime, preferences);
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
        .filter((recipe) => shouldKeepRecipeForCooker(recipe, runtimeSets, preferences.filterMissingCookers))
        .sort((a, b) => compareRareRecipesForService(
          a,
          b,
          runtimeSets.ownedIngredientQty,
          preferences.recipeSortRules,
          runtimeSets,
        ));

      const preferenceRecipes = recipes.length >= 3
        ? []
        : rankPreferenceRecipesForRare(
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
          .filter((recipe) => shouldKeepRecipeForCooker(recipe, runtimeSets, preferences.filterMissingCookers))
          .sort((a, b) => compareRareRecipesForService(
            a,
            b,
            runtimeSets.ownedIngredientQty,
            preferences.recipeSortRules,
            runtimeSets,
          ));

      const beverages = rankBeveragesForRare(customer, beverageTag, runtimeSets.beverageIds)
        .sort((a, b) => compareRareBeveragesForService(
          a,
          b,
          runtimeSets.ownedBeverageQty,
          preferences.beverageSortRules,
        ));

      const preferenceBeverages = beverages.length >= 3
        ? []
        : rankPreferenceBeveragesForRare(customer, beverageTag, runtimeSets.beverageIds)
          .sort((a, b) => compareRareBeveragesForService(
            a,
            b,
            runtimeSets.ownedBeverageQty,
            preferences.beverageSortRules,
          ));

      cached = { customer, recipes, beverages, preferenceRecipes, preferenceBeverages };
      cache.set(cacheKey, cached);
      trimRecommendationCache(cache);
    }

    recommendations.push({
      order,
      customer: cached.customer,
      recipes: promoteFavoriteRecipes(cached.recipes, favorites, customer.id, foodTag).slice(0, MAX_FOCUS_RECOMMENDATION_ROWS),
      beverages: promoteFavoriteBeverages(cached.beverages, favorites, customer.id, beverageTag).slice(0, MAX_FOCUS_RECOMMENDATION_ROWS),
      preferenceRecipes: cached.preferenceRecipes.slice(0, MAX_FOCUS_RECOMMENDATION_ROWS),
      preferenceBeverages: cached.preferenceBeverages.slice(0, MAX_FOCUS_RECOMMENDATION_ROWS),
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

type ValidOrderPreparationSelection = Extract<OrderPreparationSelection, { ok: true }>;

function selectOrderPreparationCandidates(
  recommendations: OrderRecommendation[],
  favorites: FavoriteData,
  preferences: CompanionPreferences,
  limit: number,
): { selections: ValidOrderPreparationSelection[]; messages: string[]; message: string } {
  const rows = sortNightOrderRows(
    recommendations.map((item) => ({ order: item.order, item })),
    preferences.serviceOrderSortMode,
  );
  if (rows.length === 0) {
    return { selections: [], messages: [], message: '暂无可准备的稀客订单。' };
  }

  const selections: ValidOrderPreparationSelection[] = [];
  const messages: string[] = [];
  for (const row of rows) {
    const item = row.item;
    const label = formatRareAutomationPrefix(item);
    const recipePick = pickRecipeForPreparation(item, favorites, preferences);
    const beveragePick = pickBeverageForPreparation(item, favorites, preferences);
    if (!recipePick.ok && (preferences.autoPrepStartCooking || preferences.autoPrepFavoritesOnly)) {
      messages.push(`${label}\n${preferences.autoPrepFavoritesOnly ? '没有匹配的收藏料理。' : '没有可用的推荐料理。'}`);
      continue;
    }
    if (!beveragePick.ok && (preferences.autoPrepTakeBeverage || preferences.autoPrepFavoritesOnly)) {
      messages.push(`${label}\n${preferences.autoPrepFavoritesOnly ? '没有匹配的收藏酒水。' : '没有可用的推荐酒水。'}`);
      continue;
    }

    selections.push({
      ok: true,
      item,
      recipe: recipePick.ok ? recipePick.recipe : null,
      beverage: beveragePick.ok ? beveragePick.beverage : null,
      recipeFavorite: recipePick.ok ? recipePick.favorite : null,
      beverageFavorite: beveragePick.ok ? beveragePick.favorite : null,
    });
    if (selections.length >= limit) break;
  }

  return {
    selections,
    messages,
    message: selections.length > 0 ? '' : messages[0] ?? '当前稀客订单没有可执行的自动化候选。',
  };
}

function buildGameUiPinningTarget(
  recommendations: OrderRecommendation[],
  orderSortMode: ServiceOrderSortMode,
): GameUiPinningTarget | null {
  const item = sortNightOrderRows(
    recommendations.map((recommendation) => ({ order: recommendation.order, recommendation })),
    orderSortMode,
  )[0]?.recommendation;
  if (!item) return null;
  const recipe = item.recipes[0] ?? null;
  const beverage = item.beverages[0] ?? null;
  if (!recipe && !beverage) return null;

  const baseIngredientIds = recipe
    ? recipe.recipe.ingredients
      .map((name) => INGREDIENT_BY_NAME.get(name)?.id ?? -1)
      .filter((id) => id >= 0)
    : [];
  const ingredientIds = normalizeIdList([
    ...baseIngredientIds,
    ...(recipe?.extraIngredients.map((ingredient) => ingredient.id) ?? []),
  ]);
  const recipeId = recipe?.recipe.id ?? -1;
  const beverageId = beverage?.beverage.id ?? -1;
  const cookerName = recipe?.recipe.cooker ?? '';
  const cookerTypeId = resolveCookerTypeId(cookerName);

  return {
    signature: [
      item.order.firstSeenAtUtc ?? item.order.lastSeenAtUtc ?? '',
      item.order.deskCode,
      item.order.guestId ?? item.order.guestName,
      recipeId,
      ingredientIds.join(','),
      beverageId,
      cookerTypeId,
    ].join('|'),
    recipeId,
    recipeName: recipe?.recipe.name ?? '',
    ingredientIds,
    beverageId,
    beverageName: beverage?.beverage.name ?? '',
    cookerTypeId,
    cookerName,
  };
}

function buildCompleteOrderPreferences(preferences: CompanionPreferences): CompanionPreferences {
  return {
    ...preferences,
    autoPrepCompleteOrder: true,
    autoPrepTakeBeverage: true,
    autoPrepStartCooking: true,
    autoPrepCollectCooking: true,
  };
}

function hasAutomationActionEnabled(preferences: CompanionPreferences): boolean {
  return preferences.autoPrepCompleteOrder
    || preferences.autoPrepTakeBeverage
    || preferences.autoPrepStartCooking
    || preferences.autoPrepCollectCooking;
}

function hasNormalOrderActionEnabled(preferences: CompanionPreferences): boolean {
  return preferences.autoNormalStartCooking
    || preferences.autoNormalCollectCooking;
}

function buildAutoOrderKey(item: OrderRecommendation): string {
  const order = item.order;
  return [
    order.firstSeenAtUtc ?? order.lastSeenAtUtc ?? '',
    order.deskCode,
    order.guestId ?? order.guestName,
    order.foodTag,
    order.beverageTag,
  ].join('|');
}

function formatRareAutomationPrefix(item: OrderRecommendation): string {
  const order = item.order;
  return `${order.guestName || '稀客'} · 桌 ${formatDesk(order.deskCode)}\n料理 ${order.foodTag || '无'} / 酒水 ${order.beverageTag || '无'}`;
}

function buildRareAutoOrderDiagnostic(
  selection: ValidOrderPreparationSelection,
  state: AutoFirstOrderState,
  now: number,
): RareAutoOrderDiagnostic {
  const order = selection.item.order;
  return {
    orderKey: buildAutoOrderKey(selection.item),
    title: `${order.guestName || '稀客'} · 桌 ${formatDesk(order.deskCode)}`,
    foodTag: order.foodTag || '',
    beverageTag: order.beverageTag || '',
    recipeName: selection.recipe?.recipe.name ?? '',
    beverageName: selection.beverage?.beverage.name ?? '',
    stepLabel: getAutomationStepLabel(state.step),
    stepSeconds: state.stepStartedAtMs > 0 ? Math.max(0, Math.round((now - state.stepStartedAtMs) / 1000)) : 0,
    retryCount: state.retryCount,
    rollbackCount: state.rollbackCount,
    lastError: state.lastError,
    prepared: state.prepared,
    beverageHandled: state.beverageHandled,
    paused: state.paused,
  };
}

function buildNormalAutoOrderKey(order: NormalBusinessOrder): string {
  if (order.orderKey) return order.orderKey;
  return [
    order.firstSeenAtUtc ?? '',
    order.deskCode,
    order.guestName,
    order.foodId,
    order.beverageId,
  ].join('|');
}

function isNormalOrderPreparedStale(state: NormalAutoOrderState | undefined, now: number): boolean {
  if (!state?.prepared || state.collected) return false;
  if (state.paused && !isRecoverableNormalPausedState(state, now)) return false;
  return state.preparedAtMs > 0 && now - state.preparedAtMs >= NORMAL_AUTO_PREPARED_RETRY_MS;
}

function isRecoverableNormalPausedState(state: NormalAutoOrderState | undefined, now: number): boolean {
  if (!state?.paused) return false;
  if (!state.lastError.includes('目标料理长时间未进入普客暂存容器')) return false;
  return state.stepStartedAtMs <= 0 || now - state.stepStartedAtMs >= NORMAL_AUTO_RECOVERABLE_PAUSE_RETRY_MS;
}

function emptyAutoFirstOrderState(orderKey = '', now = 0): AutoFirstOrderState {
  return {
    orderKey,
    prepared: false,
    preparedAtMs: 0,
    beverageHandled: false,
    beverageHandledAtMs: 0,
    step: 'idle',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    rollbackCount: 0,
    lastError: '',
    paused: false,
  };
}

function emptyNormalAutoOrderState(orderKey: string, now = 0): NormalAutoOrderState {
  return {
    orderKey,
    prepared: false,
    preparedAtMs: 0,
    collected: false,
    step: 'match-order',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    rollbackCount: 0,
    lastError: '',
    paused: false,
  };
}

function isAutomationTimestampStale(value: number, now: number, timeoutMs: number): boolean {
  return value > 0 && now - value >= timeoutMs;
}

function markAutomationWaiting<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  step: AutomationStep,
  now: number,
  message: string,
): T {
  return {
    ...state,
    step,
    stepStartedAtMs: state.step === step ? state.stepStartedAtMs : now,
    lastError: message,
  };
}

function pauseAutomationState<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  now: number,
  message: string,
): T {
  return {
    ...state,
    paused: true,
    step: 'paused',
    stepStartedAtMs: now,
    lastError: message,
  };
}

function updateAutomationAfterResponse<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  response: OrderPreparationResponse,
  now: number,
  step: AutomationStep,
  stopOnError: boolean,
): T {
  const failed = !response.ok;
  const transientFailure = failed && isTransientAutoPreparationFailure(response);
  const hardFailure = failed && isHardAutoPreparationFailure(response);
  const nextRetryCount = failed ? state.retryCount + 1 : 0;
  const stalled = failed && state.lastProgressAtMs > 0 && now - state.lastProgressAtMs >= AUTO_JOB_STALL_MS;
  const shouldPause = failed
    && stopOnError
    && (hardFailure || (!transientFailure && (stalled || nextRetryCount >= MAX_AUTO_STEP_RETRIES)));
  const progressed = response.ok || response.steps.some(isMeaningfulAutomationProgressStep);
  const nextStep = response.ok
    ? step
    : shouldPause
      ? 'paused'
      : step;

  return {
    ...state,
    step: nextStep,
    stepStartedAtMs: state.step === nextStep ? state.stepStartedAtMs : now,
    lastProgressAtMs: progressed ? now : state.lastProgressAtMs,
    retryCount: nextRetryCount,
    lastError: failed
      ? stalled
        ? `${summarizeOrderPreparationFailure(response)}；超过 ${Math.round(AUTO_JOB_STALL_MS / 1000)} 秒没有进展`
        : summarizeOrderPreparationFailure(response)
      : '',
    paused: shouldPause,
  };
}

function formatAutomationState(state: AutoFirstOrderState | NormalAutoOrderState): string {
  const now = Date.now();
  const parts = [
    `状态 ${getAutomationStepLabel(state.step)}`,
    state.stepStartedAtMs > 0 ? `${Math.max(0, Math.round((now - state.stepStartedAtMs) / 1000))}秒` : '',
    state.retryCount > 0 ? `重试 ${state.retryCount}/${MAX_AUTO_STEP_RETRIES}` : '',
    state.rollbackCount > 0 ? `回退 ${state.rollbackCount}/${MAX_AUTO_ROLLBACKS}` : '',
    state.lastError ? `最近 ${state.lastError}` : '',
  ].filter(Boolean);
  return parts.join(' · ');
}

function getAutomationStepLabel(step: AutomationStep): string {
  switch (step) {
    case 'match-order':
      return '匹配订单';
    case 'ensure-beverage':
      return '确认酒水';
    case 'ensure-cooking':
      return '确认料理';
    case 'wait-food-tray':
      return '等待送餐盘';
    case 'wait-food-stored':
      return '等待保温箱';
    case 'complete-order':
      return '完成订单';
    case 'done':
      return '完成';
    case 'paused':
      return '暂停';
    default:
      return '待命';
  }
}

function isMeaningfulAutomationProgressStep(step: OrderPreparationStep): boolean {
  if (!step.ok || step.skipped) return false;
  if (step.name.includes('选择') || step.name.includes('匹配')) return false;
  return step.name.includes('自动取酒')
    || step.name.includes('自动开始料理')
    || step.name.includes('自动收取料理')
    || step.name.includes('普客开始料理')
    || step.name.includes('普客保温箱')
    || step.name.includes('写入订单')
    || step.name.includes('触发上菜评价');
}

function emptyMissingTrayParts() {
  return { food: false, beverage: false };
}

function getMissingTrayParts(response: OrderPreparationResponse) {
  const missing = emptyMissingTrayParts();
  if (response.ok) return missing;
  for (const step of response.steps) {
    if (step.ok || step.skipped) continue;
    if (step.name.includes('匹配送餐盘料理')) missing.food = true;
    if (step.name.includes('匹配送餐盘酒水')) missing.beverage = true;
  }
  return missing;
}

function didCompleteStep(response: OrderPreparationResponse, name: string): boolean {
  return response.steps.some((step) => step.name === name && step.ok && !step.skipped);
}

function didAcknowledgeStep(response: OrderPreparationResponse, name: string): boolean {
  return response.steps.some((step) => step.name === name && step.ok && !isInactiveSkippedStep(step));
}

function didNormalOrderCollectToWarmer(response: OrderPreparationResponse): boolean {
  return response.steps.some((step) => step.ok
    && (step.message.includes('已在普客保温箱')
      || step.message.includes('已自动收至普客保温箱')
      || step.message.includes('该订单已经送达料理')
      || step.message.includes('目标普客订单已有料理')));
}

function didNormalOrderCookingStillPending(response: OrderPreparationResponse): boolean {
  return didOrderCookingStillPending(response, '普客开始料理');
}

function didOrderCookingStillPending(response: OrderPreparationResponse, stepName: string): boolean {
  return response.steps.some((step) => step.name === stepName
    && step.ok
    && step.skipped
    && (step.message.includes('已在制作中')
      || step.message.includes('等待完成后会自动收至普客保温箱')
      || step.message.includes('等待完成后会自动收入送餐盘')));
}

function isInactiveSkippedStep(step: OrderPreparationStep): boolean {
  if (!step.skipped) return false;
  return step.message.includes('设置已关闭')
    || step.message.includes('尚未获得')
    || step.message.includes('订单尚未同时满足');
}

function isTransientAutoPreparationFailure(response: OrderPreparationResponse): boolean {
  const text = [
    response.error ?? '',
    ...response.steps.map((step) => `${step.name} ${step.message}`),
  ].join('\n');
  return text.includes('当前没有空闲厨具')
    || text.includes('当前没有读取到任何厨具')
    || text.includes('厨具被占用')
    || text.includes('送餐盘已满')
    || text.includes('送餐盘对象不可用')
    || text.includes('厨具管理器不可用')
    || text.includes('运行时对象')
    || text.includes('经营状态刚刷新')
    || text.includes('未找到当前第一笔')
    || text.includes('暂存容器不可用')
    || text.includes('等待下一轮重试')
    || text.includes('已有待收取任务')
    || text.includes('已在制作中')
    || text.includes('长时间未读取到成品对象');
}

function isHardAutoPreparationFailure(response: OrderPreparationResponse): boolean {
  const text = [
    response.error ?? '',
    ...response.steps.map((step) => `${step.name} ${step.message}`),
  ].join('\n');
  return text.includes('材料不足')
    || text.includes('当前库存为 0')
    || text.includes('没有可用的推荐')
    || text.includes('没有有效的料理 ID')
    || text.includes('无法从游戏数据库读取料理配方')
    || text.includes('未找到料理')
    || text.includes('成品不是目标料理')
    || text.includes('订单已有其他待送达料理')
    || text.includes('收藏限定已开启');
}

function summarizeOrderPreparationFailure(response: OrderPreparationResponse): string {
  const failed = response.steps.find((step) => !step.ok && !step.skipped);
  return failed ? `${failed.name}: ${failed.message}` : response.error ?? '未知状态';
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
  rules: SortRule<RecipeSortKey>[] = DEFAULT_RECIPE_SORT_RULES,
  runtimeSets: RuntimeSets | null = null,
) {
  const sortRules = rules.length > 0 ? rules : DEFAULT_RECIPE_SORT_RULES;
  for (const rule of sortRules) {
    if (!rule.enabled) continue;
    const diff = getRecipeSortValue(a, rule.key, ownedIngredientQty, runtimeSets)
      - getRecipeSortValue(b, rule.key, ownedIngredientQty, runtimeSets);
    if (diff !== 0) return rule.direction === 'asc' ? diff : -diff;
  }
  return a.recipe.id - b.recipe.id;
}

function getRecipeSortValue(
  result: IRareRecipeResult,
  key: RecipeSortKey,
  ownedIngredientQty: Record<number, number>,
  runtimeSets: RuntimeSets | null,
): number {
  switch (key) {
    case 'requiredTag':
      return result.meetsRequiredFood ? 1 : 0;
    case 'foodScore':
      return result.foodScore;
    case 'rating':
      return getRatingRank(result.rating);
    case 'extraCount':
      return result.extraIngredients.length;
    case 'resourcePressure':
      return getRareRecipeResourcePressure(result, ownedIngredientQty);
    case 'recipePrice':
      return result.recipe.price;
    case 'extraCost':
      return result.extraCost;
    case 'baseCost':
      return result.baseCost;
    case 'totalCost':
      return result.baseCost + result.extraCost;
    case 'profit':
      return result.recipe.price - result.baseCost - result.extraCost;
    case 'cookerAvailable':
      return isRecipeCookerAvailableForSort(result, runtimeSets) ? 1 : 0;
    case 'recipeId':
      return result.recipe.id;
  }
  return 0;
}

function getRatingRank(rating: TRating): number {
  switch (rating) {
    case 'ExGood':
      return 5;
    case 'Good':
      return 4;
    case 'Normal':
      return 3;
    case 'Bad':
      return 2;
    case 'ExBad':
      return 1;
  }
  return 0;
}

function isRecipeCookerAvailableForSort(
  result: IRareRecipeResult,
  runtimeSets: RuntimeSets | null,
): boolean {
  if (!runtimeSets?.hasCookerSnapshot) return true;
  return shouldKeepRecipeForCooker(result, runtimeSets, true);
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

function compareRareBeveragesForService(
  a: IRareBeverageResult,
  b: IRareBeverageResult,
  ownedBeverageQty: Record<number, number> = {},
  rules: SortRule<BeverageSortKey>[] = DEFAULT_BEVERAGE_SORT_RULES,
) {
  const sortRules = rules.length > 0 ? rules : DEFAULT_BEVERAGE_SORT_RULES;
  for (const rule of sortRules) {
    if (!rule.enabled) continue;
    const diff = getBeverageSortValue(a, rule.key, ownedBeverageQty)
      - getBeverageSortValue(b, rule.key, ownedBeverageQty);
    if (diff !== 0) return rule.direction === 'asc' ? diff : -diff;
  }
  return a.beverage.id - b.beverage.id;
}

function getBeverageSortValue(
  result: IRareBeverageResult,
  key: BeverageSortKey,
  ownedBeverageQty: Record<number, number>,
): number {
  switch (key) {
    case 'requiredTag':
      return result.meetsRequiredBev ? 1 : 0;
    case 'bevScore':
      return result.bevScore;
    case 'beveragePrice':
      return result.beverage.price;
    case 'ownedQuantity':
      return ownedBeverageQty[result.beverage.id] ?? 0;
    case 'beverageId':
      return result.beverage.id;
  }
  return 0;
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

function buildRecommendationStateSignature(runtime: RecommendationStateSnapshot, preferences: CompanionPreferences) {
  const ownedQty = Object.entries(runtime.ownedIngredientQty)
    .sort(([a], [b]) => Number(a) - Number(b))
    .map(([id, qty]) => `${id}:${qty}`)
    .join(',');
  const ownedBeverageQty = Object.entries(runtime.ownedBeverageQty)
    .sort(([a], [b]) => Number(a) - Number(b))
    .map(([id, qty]) => `${id}:${qty}`)
    .join(',');
  const placedCookers = [
    ...(runtime.placedCookerTypeIds ?? []).map((id) => `id:${id}`),
    ...(runtime.placedCookers ?? []).flatMap((cooker) =>
      [cooker.name, ...(cooker.typeNames ?? [])].map((name) => `name:${normalizeCookerName(name)}`),
    ),
  ].filter(Boolean).sort().join(',');

  return [
    runtime.availableRecipeIds.join(','),
    runtime.availableBeverageIds.join(','),
    runtime.availableIngredientIds.join(','),
    ownedQty,
    ownedBeverageQty,
    runtime.popularFoodTag ?? '',
    runtime.popularHateFoodTag ?? '',
    runtime.famousShopEnabled ? '1' : '0',
    preferences.filterMissingCookers ? 'filterCooker:1' : 'filterCooker:0',
    `recipeSort:${serializeSortRules(preferences.recipeSortRules)}`,
    `beverageSort:${serializeSortRules(preferences.beverageSortRules)}`,
    placedCookers,
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

function matchesMissionStatusFilter(mission: RuntimeMissionInfo, filter: MissionStatusFilter): boolean {
  switch (filter) {
    case 'not-started':
      return !mission.started && !mission.finished;
    case 'started':
      return mission.started && !mission.finished;
    case 'finished':
      return mission.finished;
    case 'all':
    default:
      return true;
  }
}

function getMissionStatusFilterLabel(filter: MissionStatusFilter): string {
  switch (filter) {
    case 'not-started':
      return '未接取';
    case 'started':
      return '已开始';
    case 'finished':
      return '已完成';
    case 'all':
    default:
      return '全部';
  }
}

function readStoredTab(): ModTab {
  const value = readMigratedStorage(TAB_STORAGE_KEY, LEGACY_TAB_STORAGE_KEY, '');
  return value === 'overview'
    || value === 'normal'
    || value === 'rare'
    || value === 'service'
    || value === 'tasks'
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
    automationEnabled: readStoredBoolean(AUTOMATION_ENABLED_STORAGE_KEY, false),
    autoNormalOrderEnabled: readStoredBoolean(AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY, false),
    autoNormalStartCooking: readStoredBoolean(AUTO_NORMAL_START_COOKING_STORAGE_KEY, false),
    autoNormalCollectCooking: readStoredBoolean(AUTO_NORMAL_COLLECT_COOKING_STORAGE_KEY, false),
    autoNormalStopOnError: readStoredBoolean(AUTO_NORMAL_STOP_ON_ERROR_STORAGE_KEY, false),
    autoPrepCompleteOrder: readStoredBoolean(AUTO_PREP_COMPLETE_ORDER_STORAGE_KEY, false),
    autoPrepTakeBeverage: readStoredBoolean(AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY, false),
    autoPrepStartCooking: readStoredBoolean(AUTO_PREP_START_COOKING_STORAGE_KEY, false),
    autoPrepCollectCooking: readStoredBoolean(AUTO_PREP_COLLECT_COOKING_STORAGE_KEY, false),
    autoPrepFavoritesOnly: readStoredBoolean(AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, false),
    autoPrepStopOnError: readStoredBoolean(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, false),
    filterMissingCookers: readStoredBoolean(FILTER_MISSING_COOKERS_STORAGE_KEY, true),
    gameUiPinningEnabled: readStoredBoolean(GAME_UI_PINNING_STORAGE_KEY, false),
    cookerHighlightEnabled: readStoredBoolean(COOKER_HIGHLIGHT_STORAGE_KEY, false),
    recipeSortRules: readStoredSortRules(RECIPE_SORT_RULES_STORAGE_KEY, RECIPE_SORT_OPTIONS),
    beverageSortRules: readStoredSortRules(BEVERAGE_SORT_RULES_STORAGE_KEY, BEVERAGE_SORT_OPTIONS),
    serviceOrderSortMode: readStoredServiceOrderSortMode(),
  });
}

function readStoredFocusSwitchBehavior(): FocusSwitchBehavior {
  const value = localStorage.getItem(FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY);
  return value === 'keep-visible' ? 'keep-visible' : 'hide';
}

function readStoredServiceOrderSortMode(): ServiceOrderSortMode {
  const value = localStorage.getItem(SERVICE_ORDER_SORT_MODE_STORAGE_KEY);
  return value === 'guest' ? 'guest' : 'ordered';
}

function readStoredSortRules<K extends string>(key: string, options: SortOption<K>[]): SortRule<K>[] {
  const raw = localStorage.getItem(key);
  if (!raw) return buildDefaultSortRules(options);

  try {
    return normalizeSortRules(JSON.parse(raw) as unknown, options);
  } catch {
    return buildDefaultSortRules(options);
  }
}

function buildDefaultSortRules<K extends string>(options: SortOption<K>[]): SortRule<K>[] {
  return options.map((option) => ({
    key: option.key,
    direction: option.defaultDirection,
    enabled: option.defaultEnabled,
  }));
}

function normalizeSortRules<K extends string>(
  value: unknown,
  options: SortOption<K>[],
): SortRule<K>[] {
  if (!Array.isArray(value)) return buildDefaultSortRules(options);

  const optionByKey = new Map(options.map((option) => [option.key, option]));
  const usedKeys = new Set<K>();
  const rules: SortRule<K>[] = [];

  for (const item of value) {
    if (!item || typeof item !== 'object') continue;
    const record = item as Record<string, unknown>;
    const key = record.key;
    if (typeof key !== 'string') continue;
    const option = optionByKey.get(key as K);
    if (!option || usedKeys.has(option.key)) continue;

    rules.push({
      key: option.key,
      direction: record.direction === 'asc' || record.direction === 'desc'
        ? record.direction
        : option.defaultDirection,
      enabled: typeof record.enabled === 'boolean' ? record.enabled : option.defaultEnabled,
    });
    usedKeys.add(option.key);
  }

  for (const option of options) {
    if (usedKeys.has(option.key)) continue;
    rules.push({
      key: option.key,
      direction: option.defaultDirection,
      enabled: option.defaultEnabled,
    });
  }

  return rules;
}

function serializeSortRules<K extends string>(rules: SortRule<K>[]): string {
  return rules.map((rule) => `${rule.key}:${rule.enabled ? '1' : '0'}:${rule.direction}`).join(',');
}

function getSortOptionLabel<K extends string>(options: SortOption<K>[], key: K): string {
  return options.find((option) => option.key === key)?.label ?? key;
}

function normalizeCompanionPreferences(value: Partial<CompanionPreferences>): CompanionPreferences {
  return {
    windowOpacity: normalizeWindowOpacity(value.windowOpacity ?? DEFAULT_WINDOW_OPACITY),
    focusSwitchBehavior: value.focusSwitchBehavior === 'keep-visible' ? 'keep-visible' : 'hide',
    focusSwitchCooldownMs: normalizeFocusSwitchCooldownMs(value.focusSwitchCooldownMs ?? DEFAULT_FOCUS_SWITCH_COOLDOWN_MS),
    alwaysOnTop: Boolean(value.alwaysOnTop),
    gamepadNavigationEnabled: Boolean(value.gamepadNavigationEnabled),
    automationEnabled: Boolean(value.automationEnabled),
    autoNormalOrderEnabled: Boolean(value.autoNormalOrderEnabled),
    autoNormalStartCooking: Boolean(value.autoNormalStartCooking),
    autoNormalCollectCooking: Boolean(value.autoNormalCollectCooking),
    autoNormalStopOnError: Boolean(value.autoNormalStopOnError),
    autoPrepCompleteOrder: Boolean(value.autoPrepCompleteOrder),
    autoPrepTakeBeverage: Boolean(value.autoPrepTakeBeverage),
    autoPrepStartCooking: Boolean(value.autoPrepStartCooking),
    autoPrepCollectCooking: Boolean(value.autoPrepCollectCooking),
    autoPrepFavoritesOnly: Boolean(value.autoPrepFavoritesOnly),
    autoPrepStopOnError: Boolean(value.autoPrepStopOnError),
    filterMissingCookers: value.filterMissingCookers !== false,
    gameUiPinningEnabled: Boolean(value.gameUiPinningEnabled),
    cookerHighlightEnabled: Boolean(value.cookerHighlightEnabled),
    recipeSortRules: normalizeSortRules(value.recipeSortRules, RECIPE_SORT_OPTIONS),
    beverageSortRules: normalizeSortRules(value.beverageSortRules, BEVERAGE_SORT_OPTIONS),
    serviceOrderSortMode: value.serviceOrderSortMode === 'guest' ? 'guest' : 'ordered',
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
  localStorage.setItem(AUTOMATION_ENABLED_STORAGE_KEY, normalized.automationEnabled ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY, normalized.autoNormalOrderEnabled ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_START_COOKING_STORAGE_KEY, normalized.autoNormalStartCooking ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_COLLECT_COOKING_STORAGE_KEY, normalized.autoNormalCollectCooking ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_STOP_ON_ERROR_STORAGE_KEY, normalized.autoNormalStopOnError ? '1' : '0');
  localStorage.setItem(AUTO_PREP_COMPLETE_ORDER_STORAGE_KEY, normalized.autoPrepCompleteOrder ? '1' : '0');
  localStorage.setItem(AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY, normalized.autoPrepTakeBeverage ? '1' : '0');
  localStorage.setItem(AUTO_PREP_START_COOKING_STORAGE_KEY, normalized.autoPrepStartCooking ? '1' : '0');
  localStorage.setItem(AUTO_PREP_COLLECT_COOKING_STORAGE_KEY, normalized.autoPrepCollectCooking ? '1' : '0');
  localStorage.setItem(AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, normalized.autoPrepFavoritesOnly ? '1' : '0');
  localStorage.setItem(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, normalized.autoPrepStopOnError ? '1' : '0');
  localStorage.setItem(FILTER_MISSING_COOKERS_STORAGE_KEY, normalized.filterMissingCookers ? '1' : '0');
  localStorage.setItem(GAME_UI_PINNING_STORAGE_KEY, normalized.gameUiPinningEnabled ? '1' : '0');
  localStorage.setItem(COOKER_HIGHLIGHT_STORAGE_KEY, normalized.cookerHighlightEnabled ? '1' : '0');
  localStorage.setItem(RECIPE_SORT_RULES_STORAGE_KEY, JSON.stringify(normalized.recipeSortRules));
  localStorage.setItem(BEVERAGE_SORT_RULES_STORAGE_KEY, JSON.stringify(normalized.beverageSortRules));
  localStorage.setItem(SERVICE_ORDER_SORT_MODE_STORAGE_KEY, normalized.serviceOrderSortMode);
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

function formatRetryDelay(failureCount: number) {
  if (failureCount <= 0) return '稍后';
  const index = Math.max(0, Math.min(failureCount - 1, CONNECTION_RETRY_DELAYS_MS.length - 1));
  return `${Math.round(CONNECTION_RETRY_DELAYS_MS[index] / 1000)} 秒`;
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
