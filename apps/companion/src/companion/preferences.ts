import { isTauriRuntime } from '@/lib/tauri-runtime';
import {
  normalizeRecommendationSortProfile,
  serializeRecommendationSortProfile,
  type RecommendationBudgetPolicy,
  type RecommendationExclusions,
  type RecommendationSortProfile,
} from '@/recommendation-engine';

/**
 * 伴随窗口用户偏好的 localStorage 读写与归一化。
 *
 * 该模块只负责浏览器/Tauri 窗口侧的持久化；真正影响游戏运行时的开关会通过 API 或 Tauri command 同步到 Mod。
 */
const STORAGE_PREFIX = 'mystia-steward-companion';

const BACKGROUND_OPACITY_STORAGE_KEY = `${STORAGE_PREFIX}-background-opacity`;
const CONTENT_OPACITY_STORAGE_KEY = `${STORAGE_PREFIX}-content-opacity`;
// v1.0.7 之前窗口透明度只有一个字段，读取时兼容、保存时删除旧 key。
const LEGACY_WINDOW_OPACITY_STORAGE_KEY = `${STORAGE_PREFIX}-window-opacity`;
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
const AUTO_PREP_RECIPE_FAVORITES_ONLY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-recipe-favorites-only`;
const AUTO_PREP_BEVERAGE_FAVORITES_ONLY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-beverage-favorites-only`;
// 旧版只有一个“只处理收藏配方”开关，读取时拆分到料理/酒水两个新语义，保存后删除旧 key。
const LEGACY_AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-favorites-only`;
const AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY = `${STORAGE_PREFIX}-auto-prep-stop-on-error`;
const AUTO_RARE_CONCURRENCY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-rare-concurrency`;
const AUTO_NORMAL_CONCURRENCY_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-concurrency`;
const AUTO_MAX_STEP_RETRIES_STORAGE_KEY = `${STORAGE_PREFIX}-auto-max-step-retries`;
const AUTO_MAX_ROLLBACKS_STORAGE_KEY = `${STORAGE_PREFIX}-auto-max-rollbacks`;
const FILTER_MISSING_COOKERS_STORAGE_KEY = `${STORAGE_PREFIX}-filter-missing-cookers`;
const PIN_MISSION_RECIPE_STORAGE_KEY = `${STORAGE_PREFIX}-pin-mission-recipe`;
const PIN_FAVORITE_RECIPE_STORAGE_KEY = `${STORAGE_PREFIX}-pin-favorite-recipe`;
const PIN_FAVORITE_BEVERAGE_STORAGE_KEY = `${STORAGE_PREFIX}-pin-favorite-beverage`;
const GAME_UI_PINNING_STORAGE_KEY = `${STORAGE_PREFIX}-game-ui-pinning`;
const COOKER_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-cooker-highlight`;
const SHOW_DEBUG_DETAILS_STORAGE_KEY = `${STORAGE_PREFIX}-show-debug-details`;
const SERVICE_ORDER_SORT_MODE_STORAGE_KEY = `${STORAGE_PREFIX}-service-order-sort-mode`;
const RECOMMENDATION_SORT_PROFILE_STORAGE_KEY = `${STORAGE_PREFIX}-recommendation-sort-profile`;
const RECOMMENDATION_BUDGET_POLICY_STORAGE_KEY = `${STORAGE_PREFIX}-recommendation-budget-policy`;
const RECIPE_VARIANT_LIMIT_PER_BASE_STORAGE_KEY = `${STORAGE_PREFIX}-recipe-variant-limit-per-base`;
const EXCLUDED_INGREDIENT_IDS_STORAGE_KEY = `${STORAGE_PREFIX}-excluded-ingredient-ids`;
const EXCLUDED_BEVERAGE_IDS_STORAGE_KEY = `${STORAGE_PREFIX}-excluded-beverage-ids`;

export const MAX_FOCUS_RECOMMENDATION_ROWS = 20;
export const DEFAULT_FOCUS_RECOMMENDATION_ROWS = 8;
export const DEFAULT_BACKGROUND_OPACITY = 0.96;
export const DEFAULT_CONTENT_OPACITY = 1;
export const MIN_BACKGROUND_OPACITY = 0.2;
export const MIN_CONTENT_OPACITY = 0.35;
export const DEFAULT_FOCUS_SWITCH_COOLDOWN_MS = 800;
export const MIN_FOCUS_SWITCH_COOLDOWN_MS = 250;
export const MAX_FOCUS_SWITCH_COOLDOWN_MS = 2000;
export const DEFAULT_RARE_AUTO_ORDERS_PER_TICK = 2;
export const DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK = 3;
export const MIN_AUTO_ORDER_CONCURRENCY = 1;
export const MAX_RARE_AUTO_ORDER_CONCURRENCY = 4;
export const MAX_NORMAL_AUTO_ORDER_CONCURRENCY = 6;
export const DEFAULT_AUTO_STEP_RETRIES = 3;
export const MIN_AUTO_STEP_RETRIES = 1;
export const MAX_AUTO_STEP_RETRIES_LIMIT = 10;
export const DEFAULT_AUTO_ROLLBACKS = 2;
export const MIN_AUTO_ROLLBACKS = 0;
export const MAX_AUTO_ROLLBACKS_LIMIT = 5;
export const DEFAULT_RECIPE_VARIANT_LIMIT_PER_BASE = 1;
export const MIN_RECIPE_VARIANT_LIMIT_PER_BASE = 1;
export const MAX_RECIPE_VARIANT_LIMIT_PER_BASE = 8;
export const DEFAULT_RECOMMENDATION_EXCLUSIONS: RecommendationExclusions = {
  excludedIngredientIds: [],
  excludedBeverageIds: [],
};

export type FocusSwitchBehavior = 'hide' | 'keep-visible';
export type ServiceOrderSortMode = 'ordered' | 'guest';

/**
 * 伴随窗口全部用户偏好。
 *
 * 字段按 UI 区块组织：窗口行为、自动化、推荐约束、调试和排序。新增字段必须同时更新读取、归一化和持久化。
 */
export interface CompanionPreferences {
  backgroundOpacity: number;
  contentOpacity: number;
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
  autoPrepRecipeFavoritesOnly: boolean;
  autoPrepBeverageFavoritesOnly: boolean;
  autoPrepStopOnError: boolean;
  autoRareConcurrency: number;
  autoNormalConcurrency: number;
  autoMaxStepRetries: number;
  autoMaxRollbacks: number;
  filterMissingCookers: boolean;
  pinMissionRecipeEnabled: boolean;
  pinFavoriteRecipeEnabled: boolean;
  pinFavoriteBeverageEnabled: boolean;
  gameUiPinningEnabled: boolean;
  cookerHighlightEnabled: boolean;
  showDebugDetails: boolean;
  serviceOrderSortMode: ServiceOrderSortMode;
  recommendationSortProfile: RecommendationSortProfile;
  recommendationBudgetPolicy: RecommendationBudgetPolicy;
  recipeVariantLimitPerBase: number;
  recommendationExclusions: RecommendationExclusions;
}

export function normalizeEditableQuantity(value: number) {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(9999, Math.trunc(value)));
}

export function normalizeFocusRecommendationLimit(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_FOCUS_RECOMMENDATION_ROWS;
  return Math.max(1, Math.min(MAX_FOCUS_RECOMMENDATION_ROWS, Math.trunc(value)));
}

/**
 * 从 localStorage 读取并归一化用户偏好。
 */
export function readStoredCompanionPreferences(): CompanionPreferences {
  return normalizeCompanionPreferences({
    backgroundOpacity: readStoredNumber(
      BACKGROUND_OPACITY_STORAGE_KEY,
      readStoredNumber(LEGACY_WINDOW_OPACITY_STORAGE_KEY, DEFAULT_BACKGROUND_OPACITY),
    ),
    contentOpacity: readStoredNumber(CONTENT_OPACITY_STORAGE_KEY, DEFAULT_CONTENT_OPACITY),
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
    autoPrepRecipeFavoritesOnly: readStoredBoolean(
      AUTO_PREP_RECIPE_FAVORITES_ONLY_STORAGE_KEY,
      readStoredBoolean(LEGACY_AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, false),
    ),
    autoPrepBeverageFavoritesOnly: readStoredBoolean(
      AUTO_PREP_BEVERAGE_FAVORITES_ONLY_STORAGE_KEY,
      readStoredBoolean(LEGACY_AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY, false),
    ),
    autoPrepStopOnError: readStoredBoolean(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, false),
    autoRareConcurrency: readStoredNumber(AUTO_RARE_CONCURRENCY_STORAGE_KEY, DEFAULT_RARE_AUTO_ORDERS_PER_TICK),
    autoNormalConcurrency: readStoredNumber(AUTO_NORMAL_CONCURRENCY_STORAGE_KEY, DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK),
    autoMaxStepRetries: readStoredNumber(AUTO_MAX_STEP_RETRIES_STORAGE_KEY, DEFAULT_AUTO_STEP_RETRIES),
    autoMaxRollbacks: readStoredNumber(AUTO_MAX_ROLLBACKS_STORAGE_KEY, DEFAULT_AUTO_ROLLBACKS),
    filterMissingCookers: readStoredBoolean(FILTER_MISSING_COOKERS_STORAGE_KEY, true),
    pinMissionRecipeEnabled: readStoredBoolean(PIN_MISSION_RECIPE_STORAGE_KEY, false),
    pinFavoriteRecipeEnabled: readStoredBoolean(PIN_FAVORITE_RECIPE_STORAGE_KEY, false),
    pinFavoriteBeverageEnabled: readStoredBoolean(PIN_FAVORITE_BEVERAGE_STORAGE_KEY, false),
    gameUiPinningEnabled: readStoredBoolean(GAME_UI_PINNING_STORAGE_KEY, false),
    cookerHighlightEnabled: readStoredBoolean(COOKER_HIGHLIGHT_STORAGE_KEY, false),
    showDebugDetails: readStoredBoolean(SHOW_DEBUG_DETAILS_STORAGE_KEY, false),
    serviceOrderSortMode: readStoredServiceOrderSortMode(),
    recommendationSortProfile: readStoredRecommendationSortProfile(),
    recommendationBudgetPolicy: readStoredRecommendationBudgetPolicy(),
    recipeVariantLimitPerBase: readStoredNumber(
      RECIPE_VARIANT_LIMIT_PER_BASE_STORAGE_KEY,
      DEFAULT_RECIPE_VARIANT_LIMIT_PER_BASE,
    ),
    recommendationExclusions: readStoredRecommendationExclusions(),
  });
}

/**
 * 将外部输入归一化为完整偏好对象。
 *
 * 该函数是所有偏好入口的唯一清洗层，负责处理旧字段、非法数值和缺省值。
 */
export function normalizeCompanionPreferences(
  value: Partial<CompanionPreferences> & { windowOpacity?: number; autoPrepFavoritesOnly?: boolean },
): CompanionPreferences {
  const legacyBackgroundOpacity = value.backgroundOpacity ?? value.windowOpacity ?? DEFAULT_BACKGROUND_OPACITY;
  const legacyFavoritesOnly = Boolean(value.autoPrepFavoritesOnly);

  return {
    backgroundOpacity: normalizeBackgroundOpacity(legacyBackgroundOpacity),
    contentOpacity: normalizeContentOpacity(value.contentOpacity ?? DEFAULT_CONTENT_OPACITY),
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
    autoPrepRecipeFavoritesOnly: Boolean(value.autoPrepRecipeFavoritesOnly ?? legacyFavoritesOnly),
    autoPrepBeverageFavoritesOnly: Boolean(value.autoPrepBeverageFavoritesOnly ?? legacyFavoritesOnly),
    autoPrepStopOnError: Boolean(value.autoPrepStopOnError),
    autoRareConcurrency: normalizeRareAutoConcurrency(value.autoRareConcurrency ?? DEFAULT_RARE_AUTO_ORDERS_PER_TICK),
    autoNormalConcurrency: normalizeNormalAutoConcurrency(value.autoNormalConcurrency ?? DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK),
    autoMaxStepRetries: normalizeAutoStepRetries(value.autoMaxStepRetries ?? DEFAULT_AUTO_STEP_RETRIES),
    autoMaxRollbacks: normalizeAutoRollbacks(value.autoMaxRollbacks ?? DEFAULT_AUTO_ROLLBACKS),
    filterMissingCookers: value.filterMissingCookers !== false,
    pinMissionRecipeEnabled: Boolean(value.pinMissionRecipeEnabled),
    pinFavoriteRecipeEnabled: Boolean(value.pinFavoriteRecipeEnabled),
    pinFavoriteBeverageEnabled: Boolean(value.pinFavoriteBeverageEnabled),
    gameUiPinningEnabled: Boolean(value.gameUiPinningEnabled),
    cookerHighlightEnabled: Boolean(value.cookerHighlightEnabled),
    showDebugDetails: Boolean(value.showDebugDetails),
    serviceOrderSortMode: value.serviceOrderSortMode === 'guest' ? 'guest' : 'ordered',
    recommendationSortProfile: normalizeRecommendationSortProfile(value.recommendationSortProfile),
    recommendationBudgetPolicy: normalizeRecommendationBudgetPolicy(value.recommendationBudgetPolicy),
    recipeVariantLimitPerBase: normalizeRecipeVariantLimitPerBase(value.recipeVariantLimitPerBase),
    recommendationExclusions: normalizeRecommendationExclusions(value.recommendationExclusions),
  };
}

export function normalizeBackgroundOpacity(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_BACKGROUND_OPACITY;
  return Math.max(MIN_BACKGROUND_OPACITY, Math.min(1, value));
}

export function normalizeContentOpacity(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_CONTENT_OPACITY;
  return Math.max(MIN_CONTENT_OPACITY, Math.min(1, value));
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

export function normalizeAutoStepRetries(value: number) {
  return clampInteger(value, MIN_AUTO_STEP_RETRIES, MAX_AUTO_STEP_RETRIES_LIMIT, DEFAULT_AUTO_STEP_RETRIES);
}

export function normalizeAutoRollbacks(value: number) {
  return clampInteger(value, MIN_AUTO_ROLLBACKS, MAX_AUTO_ROLLBACKS_LIMIT, DEFAULT_AUTO_ROLLBACKS);
}

export function normalizeRecipeVariantLimitPerBase(value: number | undefined) {
  return clampInteger(
    value ?? DEFAULT_RECIPE_VARIANT_LIMIT_PER_BASE,
    MIN_RECIPE_VARIANT_LIMIT_PER_BASE,
    MAX_RECIPE_VARIANT_LIMIT_PER_BASE,
    DEFAULT_RECIPE_VARIANT_LIMIT_PER_BASE,
  );
}

/**
 * 持久化伴随窗口偏好。
 *
 * 保存前会重新归一化，确保 localStorage 中不会长期保留越界数值或过期结构。
 */
export function persistCompanionPreferences(preferences: CompanionPreferences) {
  const normalized = normalizeCompanionPreferences(preferences);
  localStorage.setItem(BACKGROUND_OPACITY_STORAGE_KEY, String(normalized.backgroundOpacity));
  localStorage.setItem(CONTENT_OPACITY_STORAGE_KEY, String(normalized.contentOpacity));
  localStorage.removeItem(LEGACY_WINDOW_OPACITY_STORAGE_KEY);
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
  localStorage.setItem(AUTO_PREP_RECIPE_FAVORITES_ONLY_STORAGE_KEY, normalized.autoPrepRecipeFavoritesOnly ? '1' : '0');
  localStorage.setItem(AUTO_PREP_BEVERAGE_FAVORITES_ONLY_STORAGE_KEY, normalized.autoPrepBeverageFavoritesOnly ? '1' : '0');
  localStorage.removeItem(LEGACY_AUTO_PREP_FAVORITES_ONLY_STORAGE_KEY);
  localStorage.setItem(AUTO_PREP_STOP_ON_ERROR_STORAGE_KEY, normalized.autoPrepStopOnError ? '1' : '0');
  localStorage.setItem(AUTO_RARE_CONCURRENCY_STORAGE_KEY, String(normalized.autoRareConcurrency));
  localStorage.setItem(AUTO_NORMAL_CONCURRENCY_STORAGE_KEY, String(normalized.autoNormalConcurrency));
  localStorage.setItem(AUTO_MAX_STEP_RETRIES_STORAGE_KEY, String(normalized.autoMaxStepRetries));
  localStorage.setItem(AUTO_MAX_ROLLBACKS_STORAGE_KEY, String(normalized.autoMaxRollbacks));
  localStorage.setItem(FILTER_MISSING_COOKERS_STORAGE_KEY, normalized.filterMissingCookers ? '1' : '0');
  localStorage.setItem(PIN_MISSION_RECIPE_STORAGE_KEY, normalized.pinMissionRecipeEnabled ? '1' : '0');
  localStorage.setItem(PIN_FAVORITE_RECIPE_STORAGE_KEY, normalized.pinFavoriteRecipeEnabled ? '1' : '0');
  localStorage.setItem(PIN_FAVORITE_BEVERAGE_STORAGE_KEY, normalized.pinFavoriteBeverageEnabled ? '1' : '0');
  localStorage.setItem(GAME_UI_PINNING_STORAGE_KEY, normalized.gameUiPinningEnabled ? '1' : '0');
  localStorage.setItem(COOKER_HIGHLIGHT_STORAGE_KEY, normalized.cookerHighlightEnabled ? '1' : '0');
  localStorage.setItem(SHOW_DEBUG_DETAILS_STORAGE_KEY, normalized.showDebugDetails ? '1' : '0');
  localStorage.setItem(SERVICE_ORDER_SORT_MODE_STORAGE_KEY, normalized.serviceOrderSortMode);
  localStorage.setItem(
    RECOMMENDATION_SORT_PROFILE_STORAGE_KEY,
    serializeRecommendationSortProfile(normalized.recommendationSortProfile),
  );
  localStorage.setItem(RECOMMENDATION_BUDGET_POLICY_STORAGE_KEY, normalized.recommendationBudgetPolicy);
  localStorage.setItem(RECIPE_VARIANT_LIMIT_PER_BASE_STORAGE_KEY, String(normalized.recipeVariantLimitPerBase));
  localStorage.setItem(
    EXCLUDED_INGREDIENT_IDS_STORAGE_KEY,
    JSON.stringify(normalized.recommendationExclusions.excludedIngredientIds),
  );
  localStorage.setItem(
    EXCLUDED_BEVERAGE_IDS_STORAGE_KEY,
    JSON.stringify(normalized.recommendationExclusions.excludedBeverageIds),
  );
}

/**
 * 将视觉透明度偏好写入 CSS 变量。
 */
export function applyCompanionVisualPreferences(preferences: CompanionPreferences) {
  const backgroundOpacity = normalizeBackgroundOpacity(preferences.backgroundOpacity);
  const backgroundPercent = `${Math.round(backgroundOpacity * 100)}%`;
  const contentOpacity = normalizeContentOpacity(preferences.contentOpacity);
  const contentPercent = `${Math.round(contentOpacity * 100)}%`;

  document.documentElement.style.setProperty('--companion-background-opacity-percent', backgroundPercent);
  document.documentElement.style.setProperty('--companion-window-opacity-percent', backgroundPercent);
  document.documentElement.style.setProperty('--companion-content-opacity', String(contentOpacity));
  document.documentElement.style.setProperty('--companion-content-opacity-percent', contentPercent);
}

/**
 * 将窗口行为偏好同步到 Tauri 壳。
 *
 * 浏览器模式或旧版本伴随窗口缺少命令时静默忽略，避免设置页在开发预览中报错。
 */
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
    // 浏览器模式和旧版伴随窗口不一定暴露这些 command，偏好仍会保存在前端本地。
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

function readStoredRecommendationSortProfile(): RecommendationSortProfile {
  const raw = localStorage.getItem(RECOMMENDATION_SORT_PROFILE_STORAGE_KEY);
  if (!raw) return normalizeRecommendationSortProfile(null);

  try {
    return normalizeRecommendationSortProfile(JSON.parse(raw) as unknown);
  } catch {
    return normalizeRecommendationSortProfile(null);
  }
}

function readStoredRecommendationBudgetPolicy(): RecommendationBudgetPolicy {
  return normalizeRecommendationBudgetPolicy(localStorage.getItem(RECOMMENDATION_BUDGET_POLICY_STORAGE_KEY));
}

export function normalizeRecommendationBudgetPolicy(value: unknown): RecommendationBudgetPolicy {
  return value === 'warn' || value === 'ignore' ? value : 'block';
}

function readStoredRecommendationExclusions(): RecommendationExclusions {
  return normalizeRecommendationExclusions({
    excludedIngredientIds: readStoredIdArray(EXCLUDED_INGREDIENT_IDS_STORAGE_KEY),
    excludedBeverageIds: readStoredIdArray(EXCLUDED_BEVERAGE_IDS_STORAGE_KEY),
  });
}

/**
 * 归一化推荐排除项，过滤非法 ID 并稳定排序。
 */
export function normalizeRecommendationExclusions(value: unknown): RecommendationExclusions {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return DEFAULT_RECOMMENDATION_EXCLUSIONS;
  const exclusions = value as Partial<RecommendationExclusions>;
  return {
    excludedIngredientIds: normalizeStoredIds(exclusions.excludedIngredientIds),
    excludedBeverageIds: normalizeStoredIds(exclusions.excludedBeverageIds),
  };
}

function readStoredIdArray(key: string): number[] {
  const raw = localStorage.getItem(key);
  if (!raw) return [];

  try {
    return normalizeStoredIds(JSON.parse(raw) as unknown);
  } catch {
    return [];
  }
}

function normalizeStoredIds(value: unknown): number[] {
  if (!Array.isArray(value)) return [];
  const seen = new Set<number>();
  const ids: number[] = [];
  for (const raw of value) {
    const id = Number(raw);
    if (!Number.isFinite(id) || id < 0) continue;
    const normalized = Math.trunc(id);
    if (seen.has(normalized)) continue;
    seen.add(normalized);
    ids.push(normalized);
  }
  return ids.sort((left, right) => left - right);
}

export function clampInteger(value: number, min: number, max: number, fallback: number) {
  if (!Number.isFinite(value)) return fallback;
  return Math.max(min, Math.min(max, Math.trunc(value)));
}
