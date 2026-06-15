import { isTauriRuntime } from '@/lib/tauri-runtime';

const STORAGE_PREFIX = 'mystia-steward-companion';

const WINDOW_OPACITY_STORAGE_KEY = `${STORAGE_PREFIX}-window-opacity`;
const FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY = `${STORAGE_PREFIX}-focus-switch-behavior`;
const FOCUS_SWITCH_COOLDOWN_STORAGE_KEY = `${STORAGE_PREFIX}-focus-switch-cooldown-ms`;
const ALWAYS_ON_TOP_STORAGE_KEY = `${STORAGE_PREFIX}-always-on-top`;
const MOUSE_PASSTHROUGH_STORAGE_KEY = `${STORAGE_PREFIX}-mouse-passthrough`;
const GAMEPAD_NAVIGATION_STORAGE_KEY = `${STORAGE_PREFIX}-gamepad-navigation`;
const AUTOMATION_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-automation-enabled`;
const AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-order-enabled`;
const AUTO_NORMAL_TAKE_BEVERAGE_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-take-beverage`;
const AUTO_NORMAL_START_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-start-cooking`;
const AUTO_NORMAL_COLLECT_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-collect-cooking`;
const AUTO_NORMAL_DELIVER_FOOD_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-deliver-food`;
const AUTO_NORMAL_COMPLETE_ORDER_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-complete-order`;
const AUTO_NORMAL_STOP_ON_ERROR_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-stop-on-error`;
const AUTO_PREP_COMPLETE_ORDER_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-complete-order`;
const AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-take-beverage`;
const AUTO_PREP_START_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-start-cooking`;
const AUTO_PREP_COLLECT_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-collect-cooking`;
const AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-favorites-only`;
const AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-stop-on-error`;
const AUTO_RARE_CONCURRENCY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-rare-concurrency`;
const AUTO_NORMAL_CONCURRENCY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-concurrency`;
const AUTO_RARE_TRAY_WAIT_SECONDS_STORAGE_KEY = `${STORAGE_PREFIX}-auto-rare-tray-wait-seconds`;
const AUTO_NORMAL_STORAGE_WAIT_SECONDS_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-storage-wait-seconds`;
const AUTO_MAX_STEP_RETRIES_STORAGE_KEY = `${STORAGE_PREFIX}-auto-max-step-retries`;
const AUTO_MAX_ROLLBACKS_STORAGE_KEY = `${STORAGE_PREFIX}-auto-max-rollbacks`;
const FILTER_MISSING_COOKERS_STORAGE_KEY = `${STORAGE_PREFIX}-filter-missing-cookers`;
const PRIORITIZE_MISSION_RECIPES_STORAGE_KEY = `${STORAGE_PREFIX}-prioritize-mission-recipes`;
const GAME_UI_PINNING_STORAGE_KEY = `${STORAGE_PREFIX}-game-ui-pinning`;
const COOKER_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-cooker-highlight`;
const SHOW_DEBUG_DETAILS_STORAGE_KEY = `${STORAGE_PREFIX}-show-debug-details`;
const RECIPE_SORT_RULES_STORAGE_KEY = `${STORAGE_PREFIX}-recipe-sort-rules`;
const BEVERAGE_SORT_RULES_STORAGE_KEY = `${STORAGE_PREFIX}-beverage-sort-rules`;
const SERVICE_ORDER_SORT_MODE_STORAGE_KEY = `${STORAGE_PREFIX}-service-order-sort-mode`;

export const MAX_FOCUS_RECOMMENDATION_ROWS = 20;
export const DEFAULT_FOCUS_RECOMMENDATION_ROWS = 8;
export const DEFAULT_WINDOW_OPACITY = 0.96;
export const MIN_WINDOW_OPACITY = 0.2;
export const DEFAULT_FOCUS_SWITCH_COOLDOWN_MS = 800;
export const MIN_FOCUS_SWITCH_COOLDOWN_MS = 250;
export const MAX_FOCUS_SWITCH_COOLDOWN_MS = 2000;
export const DEFAULT_RARE_AUTO_ORDERS_PER_TICK = 2;
export const DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK = 3;
export const MIN_AUTO_ORDER_CONCURRENCY = 1;
export const MAX_RARE_AUTO_ORDER_CONCURRENCY = 4;
export const MAX_NORMAL_AUTO_ORDER_CONCURRENCY = 6;
export const DEFAULT_NORMAL_AUTO_STORAGE_WAIT_SECONDS = 45;
export const MIN_AUTO_WAIT_SECONDS = 10;
export const MAX_AUTO_WAIT_SECONDS = 180;
export const DEFAULT_RARE_AUTO_TRAY_WAIT_SECONDS = 30;
export const DEFAULT_AUTO_STEP_RETRIES = 3;
export const MIN_AUTO_STEP_RETRIES = 1;
export const MAX_AUTO_STEP_RETRIES_LIMIT = 10;
export const DEFAULT_AUTO_ROLLBACKS = 2;
export const MIN_AUTO_ROLLBACKS = 0;
export const MAX_AUTO_ROLLBACKS_LIMIT = 5;

export type FocusSwitchBehavior = 'hide' | 'keep-visible';
export type ServiceOrderSortMode = 'ordered' | 'guest';
export type SortDirection = 'asc' | 'desc';
export type RecipeSortKey =
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
export type BeverageSortKey =
  | 'requiredTag'
  | 'bevScore'
  | 'beveragePrice'
  | 'ownedQuantity'
  | 'beverageId';

export interface SortRule<K extends string> {
  key: K;
  direction: SortDirection;
  enabled: boolean;
}

export interface SortOption<K extends string> {
  key: K;
  label: string;
  defaultDirection: SortDirection;
  defaultEnabled: boolean;
}

export const RECIPE_SORT_OPTIONS: SortOption<RecipeSortKey>[] = [
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

export const BEVERAGE_SORT_OPTIONS: SortOption<BeverageSortKey>[] = [
  { key: 'requiredTag', label: '满足点单 Tag', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'bevScore', label: '酒水分数', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'beveragePrice', label: '酒水售价', defaultDirection: 'desc', defaultEnabled: true },
  { key: 'beverageId', label: '酒水 ID', defaultDirection: 'asc', defaultEnabled: true },
  { key: 'ownedQuantity', label: '当前库存数量', defaultDirection: 'desc', defaultEnabled: false },
];

export const DEFAULT_RECIPE_SORT_RULES = buildDefaultSortRules(RECIPE_SORT_OPTIONS);
export const DEFAULT_BEVERAGE_SORT_RULES = buildDefaultSortRules(BEVERAGE_SORT_OPTIONS);

export interface CompanionPreferences {
  windowOpacity: number;
  focusSwitchBehavior: FocusSwitchBehavior;
  focusSwitchCooldownMs: number;
  alwaysOnTop: boolean;
  mousePassthroughEnabled: boolean;
  gamepadNavigationEnabled: boolean;
  automationEnabled: boolean;
  autoNormalOrderEnabled: boolean;
  autoNormalTakeBeverage: boolean;
  autoNormalStartCooking: boolean;
  autoNormalCollectCooking: boolean;
  autoNormalDeliverFood: boolean;
  autoNormalCompleteOrder: boolean;
  autoNormalStopOnError: boolean;
  autoPrepCompleteOrder: boolean;
  autoPrepTakeBeverage: boolean;
  autoPrepStartCooking: boolean;
  autoPrepCollectCooking: boolean;
  autoPrepFavoritesOnly: boolean;
  autoPrepStopOnError: boolean;
  autoRareConcurrency: number;
  autoNormalConcurrency: number;
  autoRareTrayWaitSeconds: number;
  autoNormalStorageWaitSeconds: number;
  autoMaxStepRetries: number;
  autoMaxRollbacks: number;
  filterMissingCookers: boolean;
  prioritizeMissionRecipes: boolean;
  gameUiPinningEnabled: boolean;
  cookerHighlightEnabled: boolean;
  showDebugDetails: boolean;
  recipeSortRules: SortRule<RecipeSortKey>[];
  beverageSortRules: SortRule<BeverageSortKey>[];
  serviceOrderSortMode: ServiceOrderSortMode;
}

export function normalizeEditableQuantity(value: number) {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(9999, Math.trunc(value)));
}

export function normalizeFocusRecommendationLimit(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_FOCUS_RECOMMENDATION_ROWS;
  return Math.max(1, Math.min(MAX_FOCUS_RECOMMENDATION_ROWS, Math.trunc(value)));
}

export function readStoredCompanionPreferences(): CompanionPreferences {
  return normalizeCompanionPreferences({
    windowOpacity: Number(localStorage.getItem(WINDOW_OPACITY_STORAGE_KEY) ?? DEFAULT_WINDOW_OPACITY),
    focusSwitchBehavior: readStoredFocusSwitchBehavior(),
    focusSwitchCooldownMs: Number(
      localStorage.getItem(FOCUS_SWITCH_COOLDOWN_STORAGE_KEY) ?? DEFAULT_FOCUS_SWITCH_COOLDOWN_MS,
    ),
    alwaysOnTop: readStoredBoolean(ALWAYS_ON_TOP_STORAGE_KEY, true),
    mousePassthroughEnabled: readStoredBoolean(MOUSE_PASSTHROUGH_STORAGE_KEY, false),
    gamepadNavigationEnabled: readStoredBoolean(GAMEPAD_NAVIGATION_STORAGE_KEY, true),
    automationEnabled: readStoredBoolean(AUTOMATION_ENABLED_STORAGE_KEY, false),
    autoNormalOrderEnabled: readStoredBoolean(AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY, false),
    autoNormalTakeBeverage: readStoredBoolean(AUTO_NORMAL_TAKE_BEVERAGE_STORAGE_KEY, false),
    autoNormalStartCooking: readStoredBoolean(AUTO_NORMAL_START_COOKING_STORAGE_KEY, false),
    autoNormalCollectCooking: readStoredBoolean(AUTO_NORMAL_COLLECT_COOKING_STORAGE_KEY, false),
    autoNormalDeliverFood: readStoredBoolean(AUTO_NORMAL_DELIVER_FOOD_STORAGE_KEY, false),
    autoNormalCompleteOrder: readStoredBoolean(AUTO_NORMAL_COMPLETE_ORDER_STORAGE_KEY, false),
    autoNormalStopOnError: readStoredBoolean(AUTO_NORMAL_STOP_ON_ERROR_STORAGE_KEY, false),
    autoPrepCompleteOrder: readStoredBoolean(AUTO_PREP_COMPLETE_ORDER_STORAGE_KEY, false),
    autoPrepTakeBeverage: readStoredBoolean(AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY, false),
    autoPrepStartCooking: readStoredBoolean(AUTO_PREP_START_COOKING_STORAGE_KEY, false),
    autoPrepCollectCooking: readStoredBoolean(AUTO_PREP_COLLECT_COOKING_STORAGE_KEY, false),
    autoPrepFavoritesOnly: readStoredBoolean(AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, false),
    autoPrepStopOnError: readStoredBoolean(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, false),
    autoRareConcurrency: readStoredNumber(AUTO_RARE_CONCURRENCY_STORAGE_KEY, DEFAULT_RARE_AUTO_ORDERS_PER_TICK),
    autoNormalConcurrency: readStoredNumber(AUTO_NORMAL_CONCURRENCY_STORAGE_KEY, DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK),
    autoRareTrayWaitSeconds: readStoredNumber(AUTO_RARE_TRAY_WAIT_SECONDS_STORAGE_KEY, DEFAULT_RARE_AUTO_TRAY_WAIT_SECONDS),
    autoNormalStorageWaitSeconds: readStoredNumber(AUTO_NORMAL_STORAGE_WAIT_SECONDS_STORAGE_KEY, DEFAULT_NORMAL_AUTO_STORAGE_WAIT_SECONDS),
    autoMaxStepRetries: readStoredNumber(AUTO_MAX_STEP_RETRIES_STORAGE_KEY, DEFAULT_AUTO_STEP_RETRIES),
    autoMaxRollbacks: readStoredNumber(AUTO_MAX_ROLLBACKS_STORAGE_KEY, DEFAULT_AUTO_ROLLBACKS),
    filterMissingCookers: readStoredBoolean(FILTER_MISSING_COOKERS_STORAGE_KEY, true),
    prioritizeMissionRecipes: readStoredBoolean(PRIORITIZE_MISSION_RECIPES_STORAGE_KEY, false),
    gameUiPinningEnabled: readStoredBoolean(GAME_UI_PINNING_STORAGE_KEY, false),
    cookerHighlightEnabled: readStoredBoolean(COOKER_HIGHLIGHT_STORAGE_KEY, false),
    showDebugDetails: readStoredBoolean(SHOW_DEBUG_DETAILS_STORAGE_KEY, false),
    recipeSortRules: readStoredSortRules(RECIPE_SORT_RULES_STORAGE_KEY, RECIPE_SORT_OPTIONS),
    beverageSortRules: readStoredSortRules(BEVERAGE_SORT_RULES_STORAGE_KEY, BEVERAGE_SORT_OPTIONS),
    serviceOrderSortMode: readStoredServiceOrderSortMode(),
  });
}

export function buildDefaultSortRules<K extends string>(options: SortOption<K>[]): SortRule<K>[] {
  return options.map((option) => ({
    key: option.key,
    direction: option.defaultDirection,
    enabled: option.defaultEnabled,
  }));
}

export function normalizeSortRules<K extends string>(
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

export function serializeSortRules<K extends string>(rules: SortRule<K>[]): string {
  return rules.map((rule) => `${rule.key}:${rule.enabled ? '1' : '0'}:${rule.direction}`).join(',');
}

export function getSortOptionLabel<K extends string>(options: SortOption<K>[], key: K): string {
  return options.find((option) => option.key === key)?.label ?? key;
}

export function normalizeCompanionPreferences(value: Partial<CompanionPreferences>): CompanionPreferences {
  return {
    windowOpacity: normalizeWindowOpacity(value.windowOpacity ?? DEFAULT_WINDOW_OPACITY),
    focusSwitchBehavior: value.focusSwitchBehavior === 'keep-visible' ? 'keep-visible' : 'hide',
    focusSwitchCooldownMs: normalizeFocusSwitchCooldownMs(value.focusSwitchCooldownMs ?? DEFAULT_FOCUS_SWITCH_COOLDOWN_MS),
    alwaysOnTop: Boolean(value.alwaysOnTop),
    mousePassthroughEnabled: Boolean(value.mousePassthroughEnabled),
    gamepadNavigationEnabled: Boolean(value.gamepadNavigationEnabled),
    automationEnabled: Boolean(value.automationEnabled),
    autoNormalOrderEnabled: Boolean(value.autoNormalOrderEnabled),
    autoNormalTakeBeverage: Boolean(value.autoNormalTakeBeverage),
    autoNormalStartCooking: Boolean(value.autoNormalStartCooking),
    autoNormalCollectCooking: Boolean(value.autoNormalCollectCooking),
    autoNormalDeliverFood: Boolean(value.autoNormalDeliverFood),
    autoNormalCompleteOrder: Boolean(value.autoNormalCompleteOrder),
    autoNormalStopOnError: Boolean(value.autoNormalStopOnError),
    autoPrepCompleteOrder: Boolean(value.autoPrepCompleteOrder),
    autoPrepTakeBeverage: Boolean(value.autoPrepTakeBeverage),
    autoPrepStartCooking: Boolean(value.autoPrepStartCooking),
    autoPrepCollectCooking: Boolean(value.autoPrepCollectCooking),
    autoPrepFavoritesOnly: Boolean(value.autoPrepFavoritesOnly),
    autoPrepStopOnError: Boolean(value.autoPrepStopOnError),
    autoRareConcurrency: normalizeRareAutoConcurrency(value.autoRareConcurrency ?? DEFAULT_RARE_AUTO_ORDERS_PER_TICK),
    autoNormalConcurrency: normalizeNormalAutoConcurrency(value.autoNormalConcurrency ?? DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK),
    autoRareTrayWaitSeconds: normalizeAutomationWaitSeconds(value.autoRareTrayWaitSeconds ?? DEFAULT_RARE_AUTO_TRAY_WAIT_SECONDS, DEFAULT_RARE_AUTO_TRAY_WAIT_SECONDS),
    autoNormalStorageWaitSeconds: normalizeAutomationWaitSeconds(value.autoNormalStorageWaitSeconds ?? DEFAULT_NORMAL_AUTO_STORAGE_WAIT_SECONDS, DEFAULT_NORMAL_AUTO_STORAGE_WAIT_SECONDS),
    autoMaxStepRetries: normalizeAutoStepRetries(value.autoMaxStepRetries ?? DEFAULT_AUTO_STEP_RETRIES),
    autoMaxRollbacks: normalizeAutoRollbacks(value.autoMaxRollbacks ?? DEFAULT_AUTO_ROLLBACKS),
    filterMissingCookers: value.filterMissingCookers !== false,
    prioritizeMissionRecipes: Boolean(value.prioritizeMissionRecipes),
    gameUiPinningEnabled: Boolean(value.gameUiPinningEnabled),
    cookerHighlightEnabled: Boolean(value.cookerHighlightEnabled),
    showDebugDetails: Boolean(value.showDebugDetails),
    recipeSortRules: normalizeSortRules(value.recipeSortRules, RECIPE_SORT_OPTIONS),
    beverageSortRules: normalizeSortRules(value.beverageSortRules, BEVERAGE_SORT_OPTIONS),
    serviceOrderSortMode: value.serviceOrderSortMode === 'guest' ? 'guest' : 'ordered',
  };
}

export function normalizeWindowOpacity(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_WINDOW_OPACITY;
  return Math.max(MIN_WINDOW_OPACITY, Math.min(1, value));
}

export function normalizeFocusSwitchCooldownMs(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_FOCUS_SWITCH_COOLDOWN_MS;
  return Math.max(
    MIN_FOCUS_SWITCH_COOLDOWN_MS,
    Math.min(MAX_FOCUS_SWITCH_COOLDOWN_MS, Math.trunc(value)),
  );
}

export function normalizeRareAutoConcurrency(value: number) {
  return clampInteger(value, MIN_AUTO_ORDER_CONCURRENCY, MAX_RARE_AUTO_ORDER_CONCURRENCY, DEFAULT_RARE_AUTO_ORDERS_PER_TICK);
}

export function normalizeNormalAutoConcurrency(value: number) {
  return clampInteger(value, MIN_AUTO_ORDER_CONCURRENCY, MAX_NORMAL_AUTO_ORDER_CONCURRENCY, DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK);
}

export function normalizeAutomationWaitSeconds(value: number, fallback: number) {
  return clampInteger(value, MIN_AUTO_WAIT_SECONDS, MAX_AUTO_WAIT_SECONDS, fallback);
}

export function normalizeAutoStepRetries(value: number) {
  return clampInteger(value, MIN_AUTO_STEP_RETRIES, MAX_AUTO_STEP_RETRIES_LIMIT, DEFAULT_AUTO_STEP_RETRIES);
}

export function normalizeAutoRollbacks(value: number) {
  return clampInteger(value, MIN_AUTO_ROLLBACKS, MAX_AUTO_ROLLBACKS_LIMIT, DEFAULT_AUTO_ROLLBACKS);
}

export function persistCompanionPreferences(preferences: CompanionPreferences) {
  const normalized = normalizeCompanionPreferences(preferences);
  localStorage.setItem(WINDOW_OPACITY_STORAGE_KEY, String(normalized.windowOpacity));
  localStorage.setItem(FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY, normalized.focusSwitchBehavior);
  localStorage.setItem(FOCUS_SWITCH_COOLDOWN_STORAGE_KEY, String(normalized.focusSwitchCooldownMs));
  localStorage.setItem(ALWAYS_ON_TOP_STORAGE_KEY, normalized.alwaysOnTop ? '1' : '0');
  localStorage.setItem(MOUSE_PASSTHROUGH_STORAGE_KEY, normalized.mousePassthroughEnabled ? '1' : '0');
  localStorage.setItem(GAMEPAD_NAVIGATION_STORAGE_KEY, normalized.gamepadNavigationEnabled ? '1' : '0');
  localStorage.setItem(AUTOMATION_ENABLED_STORAGE_KEY, normalized.automationEnabled ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY, normalized.autoNormalOrderEnabled ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_TAKE_BEVERAGE_STORAGE_KEY, normalized.autoNormalTakeBeverage ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_START_COOKING_STORAGE_KEY, normalized.autoNormalStartCooking ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_COLLECT_COOKING_STORAGE_KEY, normalized.autoNormalCollectCooking ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_DELIVER_FOOD_STORAGE_KEY, normalized.autoNormalDeliverFood ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_COMPLETE_ORDER_STORAGE_KEY, normalized.autoNormalCompleteOrder ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_STOP_ON_ERROR_STORAGE_KEY, normalized.autoNormalStopOnError ? '1' : '0');
  localStorage.setItem(AUTO_PREP_COMPLETE_ORDER_STORAGE_KEY, normalized.autoPrepCompleteOrder ? '1' : '0');
  localStorage.setItem(AUTO_PREP_TAKE_BEVERAGE_STORAGE_KEY, normalized.autoPrepTakeBeverage ? '1' : '0');
  localStorage.setItem(AUTO_PREP_START_COOKING_STORAGE_KEY, normalized.autoPrepStartCooking ? '1' : '0');
  localStorage.setItem(AUTO_PREP_COLLECT_COOKING_STORAGE_KEY, normalized.autoPrepCollectCooking ? '1' : '0');
  localStorage.setItem(AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, normalized.autoPrepFavoritesOnly ? '1' : '0');
  localStorage.setItem(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, normalized.autoPrepStopOnError ? '1' : '0');
  localStorage.setItem(AUTO_RARE_CONCURRENCY_STORAGE_KEY, String(normalized.autoRareConcurrency));
  localStorage.setItem(AUTO_NORMAL_CONCURRENCY_STORAGE_KEY, String(normalized.autoNormalConcurrency));
  localStorage.setItem(AUTO_RARE_TRAY_WAIT_SECONDS_STORAGE_KEY, String(normalized.autoRareTrayWaitSeconds));
  localStorage.setItem(AUTO_NORMAL_STORAGE_WAIT_SECONDS_STORAGE_KEY, String(normalized.autoNormalStorageWaitSeconds));
  localStorage.setItem(AUTO_MAX_STEP_RETRIES_STORAGE_KEY, String(normalized.autoMaxStepRetries));
  localStorage.setItem(AUTO_MAX_ROLLBACKS_STORAGE_KEY, String(normalized.autoMaxRollbacks));
  localStorage.setItem(FILTER_MISSING_COOKERS_STORAGE_KEY, normalized.filterMissingCookers ? '1' : '0');
  localStorage.setItem(PRIORITIZE_MISSION_RECIPES_STORAGE_KEY, normalized.prioritizeMissionRecipes ? '1' : '0');
  localStorage.setItem(GAME_UI_PINNING_STORAGE_KEY, normalized.gameUiPinningEnabled ? '1' : '0');
  localStorage.setItem(COOKER_HIGHLIGHT_STORAGE_KEY, normalized.cookerHighlightEnabled ? '1' : '0');
  localStorage.setItem(SHOW_DEBUG_DETAILS_STORAGE_KEY, normalized.showDebugDetails ? '1' : '0');
  localStorage.setItem(RECIPE_SORT_RULES_STORAGE_KEY, JSON.stringify(normalized.recipeSortRules));
  localStorage.setItem(BEVERAGE_SORT_RULES_STORAGE_KEY, JSON.stringify(normalized.beverageSortRules));
  localStorage.setItem(SERVICE_ORDER_SORT_MODE_STORAGE_KEY, normalized.serviceOrderSortMode);
}

export function applyCompanionVisualPreferences(preferences: CompanionPreferences) {
  const opacity = normalizeWindowOpacity(preferences.windowOpacity);
  const percent = `${Math.round(opacity * 100)}%`;
  document.documentElement.style.setProperty('--companion-window-opacity', String(opacity));
  document.documentElement.style.setProperty('--companion-window-opacity-percent', percent);
}

export async function applyCompanionPreferencesToTauri(
  focusSwitchBehavior: FocusSwitchBehavior,
  alwaysOnTop: boolean,
  focusSwitchCooldownMs: number,
  mousePassthroughEnabled: boolean,
) {
  if (!isTauriRuntime()) return;

  try {
    const { invoke } = await import('@tauri-apps/api/core');
    await invoke('apply_companion_preferences', {
      keepVisibleWhenFocused: focusSwitchBehavior === 'keep-visible',
      alwaysOnTop,
      windowSwitchCooldownMs: normalizeFocusSwitchCooldownMs(focusSwitchCooldownMs),
    });
    await invoke('set_mouse_passthrough', { enabled: mousePassthroughEnabled });
  } catch {
    // Browser mode and older companion builds do not expose this command.
  }
}

function readStoredBoolean(key: string, fallback: boolean) {
  const value = localStorage.getItem(key);
  if (value === null) return fallback;
  return value === '1' || value === 'true';
}

function readStoredNumber(key: string, fallback: number) {
  const raw = localStorage.getItem(key);
  if (raw === null) return fallback;
  const value = Number(raw);
  return Number.isFinite(value) ? value : fallback;
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

export function clampInteger(value: number, min: number, max: number, fallback: number) {
  if (!Number.isFinite(value)) return fallback;
  return Math.max(min, Math.min(max, Math.trunc(value)));
}
