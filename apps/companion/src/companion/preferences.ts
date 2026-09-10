import {
  normalizeRecommendationSortProfile,
  serializeRecommendationSortProfile,
  type RecommendationBudgetPolicy,
  type RecommendationExclusions,
  type RecommendationObjectiveKey,
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
const FONT_SCALE_PERCENT_STORAGE_KEY = `${STORAGE_PREFIX}-font-scale-percent`;
// v1.0.7 之前窗口透明度只有一个字段，读取时兼容、保存时删除旧 key。
const LEGACY_WINDOW_OPACITY_STORAGE_KEY = `${STORAGE_PREFIX}-window-opacity`;
const FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY = `${STORAGE_PREFIX}-focus-switch-behavior`;
const FOCUS_SWITCH_COOLDOWN_STORAGE_KEY = `${STORAGE_PREFIX}-focus-switch-cooldown-ms`;
const ALWAYS_ON_TOP_STORAGE_KEY = `${STORAGE_PREFIX}-always-on-top`;
const GAMEPAD_NAVIGATION_STORAGE_KEY = `${STORAGE_PREFIX}-gamepad-navigation`;
const AUTOMATION_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-automation-enabled`;
const AUTO_RARE_ORDER_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-auto-rare-order-enabled`;
const RARE_GUEST_PARTICIPATION_MODULE_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-rare-guest-participation-module-enabled`;
const MANAGED_RARE_GUEST_IDS_STORAGE_KEY = `${STORAGE_PREFIX}-managed-rare-guest-ids`;
const AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-order-enabled`;
const AUTO_NORMAL_TAKE_BEVERAGE_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-take-beverage`;
const AUTO_NORMAL_START_COOKING_STORAGE_KEY = `${STORAGE_PREFIX}-auto-normal-start-cooking`;
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
const MISSION_RECIPE_PRIORITY_STORAGE_KEY = `${STORAGE_PREFIX}-mission-recipe-priority`;
const PIN_FAVORITE_RECIPE_STORAGE_KEY = `${STORAGE_PREFIX}-pin-favorite-recipe`;
const PIN_FAVORITE_BEVERAGE_STORAGE_KEY = `${STORAGE_PREFIX}-pin-favorite-beverage`;
const RARE_GAME_UI_PINNING_STORAGE_KEY = `${STORAGE_PREFIX}-rare-game-ui-pinning`;
const NORMAL_GAME_UI_PINNING_STORAGE_KEY = `${STORAGE_PREFIX}-normal-game-ui-pinning`;
const RARE_RECIPE_VARIANT_STORAGE_KEY = `${STORAGE_PREFIX}-rare-recipe-variant`;
const NORMAL_RECIPE_VARIANT_STORAGE_KEY = `${STORAGE_PREFIX}-normal-recipe-variant`;
const RARE_COOKER_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-rare-cooker-highlight`;
const NORMAL_COOKER_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-normal-cooker-highlight`;
const RARE_SEAT_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-rare-seat-highlight`;
const NORMAL_SEAT_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-normal-seat-highlight`;
const RARE_ORDER_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-rare-order-highlight`;
const NORMAL_ORDER_HIGHLIGHT_STORAGE_KEY = `${STORAGE_PREFIX}-normal-order-highlight`;
const RARE_TARGET_HIGHLIGHT_COLOR_STORAGE_KEY = `${STORAGE_PREFIX}-rare-target-highlight-color`;
const NORMAL_TARGET_HIGHLIGHT_COLOR_STORAGE_KEY = `${STORAGE_PREFIX}-normal-target-highlight-color`;
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
export const DEFAULT_FONT_SCALE_PERCENT = 100;
export const MIN_FONT_SCALE_PERCENT = 90;
export const MAX_FONT_SCALE_PERCENT = 130;
export const FONT_SCALE_PERCENT_STEP = 5;
export const DEFAULT_FOCUS_SWITCH_COOLDOWN_MS = 800;
export const MIN_FOCUS_SWITCH_COOLDOWN_MS = 250;
export const MAX_FOCUS_SWITCH_COOLDOWN_MS = 2000;
export const DEFAULT_RARE_AUTO_ORDERS_PER_TICK = 2;
export const DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK = 3;
export const MIN_AUTO_ORDER_CONCURRENCY = 1;
export const MAX_RARE_AUTO_ORDER_CONCURRENCY = 4;
export const MAX_NORMAL_AUTO_ORDER_CONCURRENCY = 6;
export const MAX_MANAGED_RARE_GUEST_IDS = 512;
export const MAX_MANAGED_RARE_GUEST_ID = 2_147_483_647;
const MAX_RECOMMENDATION_EXCLUSION_IDS = 4096;
export const DEFAULT_AUTO_STEP_RETRIES = 3;
export const MIN_AUTO_STEP_RETRIES = 1;
export const MAX_AUTO_STEP_RETRIES_LIMIT = 10;
export const DEFAULT_AUTO_ROLLBACKS = 2;
export const MIN_AUTO_ROLLBACKS = 0;
export const MAX_AUTO_ROLLBACKS_LIMIT = 5;
export const DEFAULT_RECIPE_VARIANT_LIMIT_PER_BASE = 1;
export const DEFAULT_RARE_TARGET_HIGHLIGHT_COLOR = '#FFDB2E';
export const DEFAULT_NORMAL_TARGET_HIGHLIGHT_COLOR = '#5FACD3';
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
  fontScalePercent: number;
  focusSwitchBehavior: FocusSwitchBehavior;
  focusSwitchCooldownMs: number;
  alwaysOnTop: boolean;
  gamepadNavigationEnabled: boolean;
  automationEnabled: boolean;
  autoRareOrderEnabled: boolean;
  rareGuestParticipationModuleEnabled: boolean;
  managedRareGuestIds: number[];
  autoNormalOrderEnabled: boolean;
  autoNormalTakeBeverage: boolean;
  autoNormalStartCooking: boolean;
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
  missionRecipePriorityEnabled: boolean;
  pinFavoriteRecipeEnabled: boolean;
  pinFavoriteBeverageEnabled: boolean;
  rareGameUiPinningEnabled: boolean;
  normalGameUiPinningEnabled: boolean;
  rareRecipeVariantEnabled: boolean;
  normalRecipeVariantEnabled: boolean;
  rareCookerHighlightEnabled: boolean;
  normalCookerHighlightEnabled: boolean;
  rareSeatHighlightEnabled: boolean;
  normalSeatHighlightEnabled: boolean;
  rareOrderHighlightEnabled: boolean;
  normalOrderHighlightEnabled: boolean;
  rareTargetHighlightColor: string;
  normalTargetHighlightColor: string;
  showDebugDetails: boolean;
  serviceOrderSortMode: ServiceOrderSortMode;
  recommendationSortProfile: RecommendationSortProfile;
  recommendationBudgetPolicy: RecommendationBudgetPolicy;
  recipeVariantLimitPerBase: number;
  recommendationExclusions: RecommendationExclusions;
}

/**
 * 会影响推荐主方案、自动化或游戏界面辅助的跨设备功能配置。
 *
 * 窗口外观、平台能力、调试显示和纯页面状态不得进入该结构。字段顺序也是规范 JSON/hash 的一部分。
 */
export interface SharedCompanionPreferences {
  automationEnabled: boolean;
  autoRareOrderEnabled: boolean;
  rareGuestParticipationModuleEnabled: boolean;
  managedRareGuestIds: number[];
  autoNormalOrderEnabled: boolean;
  autoNormalTakeBeverage: boolean;
  autoNormalStartCooking: boolean;
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
  missionRecipePriorityEnabled: boolean;
  pinFavoriteRecipeEnabled: boolean;
  pinFavoriteBeverageEnabled: boolean;
  rareGameUiPinningEnabled: boolean;
  normalGameUiPinningEnabled: boolean;
  rareRecipeVariantEnabled: boolean;
  normalRecipeVariantEnabled: boolean;
  rareCookerHighlightEnabled: boolean;
  normalCookerHighlightEnabled: boolean;
  rareSeatHighlightEnabled: boolean;
  normalSeatHighlightEnabled: boolean;
  rareOrderHighlightEnabled: boolean;
  normalOrderHighlightEnabled: boolean;
  rareTargetHighlightColor: string;
  normalTargetHighlightColor: string;
  serviceOrderSortMode: ServiceOrderSortMode;
  recommendationSortProfile: RecommendationSortProfile;
  recommendationBudgetPolicy: RecommendationBudgetPolicy;
  recipeVariantLimitPerBase: number;
  recommendationExclusions: RecommendationExclusions;
}

/** 只保存在当前伴随窗口、不进入主设备共享 profile 的偏好。 */
export type LocalCompanionPreferences = Omit<
  CompanionPreferences,
  keyof SharedCompanionPreferences
>;

export const SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION = 4;

const SHARED_COMPANION_BOOLEAN_FIELDS = [
  'automationEnabled',
  'autoRareOrderEnabled',
  'rareGuestParticipationModuleEnabled',
  'autoNormalOrderEnabled',
  'autoNormalTakeBeverage',
  'autoNormalStartCooking',
  'autoNormalDeliverFood',
  'autoNormalCompleteOrder',
  'autoNormalStopOnError',
  'autoPrepCompleteOrder',
  'autoPrepTakeBeverage',
  'autoPrepStartCooking',
  'autoPrepCollectCooking',
  'autoPrepRecipeFavoritesOnly',
  'autoPrepBeverageFavoritesOnly',
  'autoPrepStopOnError',
  'filterMissingCookers',
  'missionRecipePriorityEnabled',
  'pinFavoriteRecipeEnabled',
  'pinFavoriteBeverageEnabled',
  'rareGameUiPinningEnabled',
  'normalGameUiPinningEnabled',
  'rareRecipeVariantEnabled',
  'normalRecipeVariantEnabled',
  'rareCookerHighlightEnabled',
  'normalCookerHighlightEnabled',
  'rareSeatHighlightEnabled',
  'normalSeatHighlightEnabled',
  'rareOrderHighlightEnabled',
  'normalOrderHighlightEnabled',
] as const satisfies readonly (keyof SharedCompanionPreferences)[];

const SHARED_COMPANION_PREFERENCE_FIELDS = [
  ...SHARED_COMPANION_BOOLEAN_FIELDS,
  'autoRareConcurrency',
  'autoNormalConcurrency',
  'autoMaxStepRetries',
  'autoMaxRollbacks',
  'rareTargetHighlightColor',
  'normalTargetHighlightColor',
  'serviceOrderSortMode',
  'recommendationSortProfile',
  'recommendationBudgetPolicy',
  'recipeVariantLimitPerBase',
  'recommendationExclusions',
  'managedRareGuestIds',
] as const satisfies readonly (keyof SharedCompanionPreferences)[];

const RECOMMENDATION_OBJECTIVE_KEYS = [
  'foodPreference',
  'beveragePreference',
  'negativeRisk',
  'extraCount',
  'resourcePressure',
  'totalCost',
  'profit',
  'beverageStock',
] as const satisfies readonly RecommendationObjectiveKey[];

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
    fontScalePercent: readStoredNumber(FONT_SCALE_PERCENT_STORAGE_KEY, DEFAULT_FONT_SCALE_PERCENT),
    focusSwitchBehavior: readStoredFocusSwitchBehavior(),
    focusSwitchCooldownMs: Number(
      localStorage.getItem(FOCUS_SWITCH_COOLDOWN_STORAGE_KEY) ?? DEFAULT_FOCUS_SWITCH_COOLDOWN_MS,
    ),
    alwaysOnTop: readStoredBoolean(ALWAYS_ON_TOP_STORAGE_KEY, true),
    gamepadNavigationEnabled: readStoredBoolean(GAMEPAD_NAVIGATION_STORAGE_KEY, true),
    automationEnabled: readStoredBoolean(AUTOMATION_ENABLED_STORAGE_KEY, false),
    autoRareOrderEnabled: readStoredBoolean(AUTO_RARE_ORDER_ENABLED_STORAGE_KEY, true),
    rareGuestParticipationModuleEnabled: readStoredBoolean(
      RARE_GUEST_PARTICIPATION_MODULE_ENABLED_STORAGE_KEY,
      false,
    ),
    managedRareGuestIds: readStoredManagedRareGuestIds(),
    autoNormalOrderEnabled: readStoredBoolean(AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY, false),
    autoNormalTakeBeverage: readStoredBoolean(AUTO_NORMAL_TAKE_BEVERAGE_STORAGE_KEY, false),
    autoNormalStartCooking: readStoredBoolean(AUTO_NORMAL_START_COOKING_STORAGE_KEY, false),
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
    missionRecipePriorityEnabled: readStoredBoolean(MISSION_RECIPE_PRIORITY_STORAGE_KEY, true),
    pinFavoriteRecipeEnabled: readStoredBoolean(PIN_FAVORITE_RECIPE_STORAGE_KEY, false),
    pinFavoriteBeverageEnabled: readStoredBoolean(PIN_FAVORITE_BEVERAGE_STORAGE_KEY, false),
    rareGameUiPinningEnabled: readStoredBoolean(RARE_GAME_UI_PINNING_STORAGE_KEY, false),
    normalGameUiPinningEnabled: readStoredBoolean(NORMAL_GAME_UI_PINNING_STORAGE_KEY, false),
    rareRecipeVariantEnabled: readStoredBoolean(RARE_RECIPE_VARIANT_STORAGE_KEY, false),
    normalRecipeVariantEnabled: readStoredBoolean(NORMAL_RECIPE_VARIANT_STORAGE_KEY, false),
    rareCookerHighlightEnabled: readStoredBoolean(RARE_COOKER_HIGHLIGHT_STORAGE_KEY, false),
    normalCookerHighlightEnabled: readStoredBoolean(NORMAL_COOKER_HIGHLIGHT_STORAGE_KEY, false),
    rareSeatHighlightEnabled: readStoredBoolean(RARE_SEAT_HIGHLIGHT_STORAGE_KEY, false),
    normalSeatHighlightEnabled: readStoredBoolean(NORMAL_SEAT_HIGHLIGHT_STORAGE_KEY, false),
    rareOrderHighlightEnabled: readStoredBoolean(RARE_ORDER_HIGHLIGHT_STORAGE_KEY, false),
    normalOrderHighlightEnabled: readStoredBoolean(NORMAL_ORDER_HIGHLIGHT_STORAGE_KEY, false),
    rareTargetHighlightColor: localStorage.getItem(RARE_TARGET_HIGHLIGHT_COLOR_STORAGE_KEY)
      ?? DEFAULT_RARE_TARGET_HIGHLIGHT_COLOR,
    normalTargetHighlightColor: localStorage.getItem(NORMAL_TARGET_HIGHLIGHT_COLOR_STORAGE_KEY)
      ?? DEFAULT_NORMAL_TARGET_HIGHLIGHT_COLOR,
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
  const autoPrepCompleteOrder = Boolean(value.autoPrepCompleteOrder);
  const autoNormalCompleteOrder = Boolean(value.autoNormalCompleteOrder);

  return {
    backgroundOpacity: normalizeBackgroundOpacity(legacyBackgroundOpacity),
    contentOpacity: normalizeContentOpacity(value.contentOpacity ?? DEFAULT_CONTENT_OPACITY),
    fontScalePercent: normalizeFontScalePercent(value.fontScalePercent ?? DEFAULT_FONT_SCALE_PERCENT),
    focusSwitchBehavior: value.focusSwitchBehavior === 'keep-visible' ? 'keep-visible' : 'hide',
    focusSwitchCooldownMs: normalizeFocusSwitchCooldownMs(value.focusSwitchCooldownMs ?? DEFAULT_FOCUS_SWITCH_COOLDOWN_MS),
    alwaysOnTop: Boolean(value.alwaysOnTop),
    gamepadNavigationEnabled: Boolean(value.gamepadNavigationEnabled),
    automationEnabled: Boolean(value.automationEnabled),
    autoRareOrderEnabled: value.autoRareOrderEnabled !== false,
    rareGuestParticipationModuleEnabled: Boolean(value.rareGuestParticipationModuleEnabled),
    managedRareGuestIds: normalizeManagedRareGuestIds(value.managedRareGuestIds),
    autoNormalOrderEnabled: Boolean(value.autoNormalOrderEnabled),
    autoNormalTakeBeverage: autoNormalCompleteOrder && Boolean(value.autoNormalTakeBeverage),
    autoNormalStartCooking: Boolean(value.autoNormalStartCooking),
    autoNormalDeliverFood: autoNormalCompleteOrder && Boolean(value.autoNormalDeliverFood),
    autoNormalCompleteOrder,
    autoNormalStopOnError: Boolean(value.autoNormalStopOnError),
    autoPrepCompleteOrder,
    autoPrepTakeBeverage: autoPrepCompleteOrder && Boolean(value.autoPrepTakeBeverage),
    autoPrepStartCooking: Boolean(value.autoPrepStartCooking),
    autoPrepCollectCooking: autoPrepCompleteOrder && Boolean(value.autoPrepCollectCooking),
    autoPrepRecipeFavoritesOnly: Boolean(value.autoPrepRecipeFavoritesOnly ?? legacyFavoritesOnly),
    autoPrepBeverageFavoritesOnly: Boolean(value.autoPrepBeverageFavoritesOnly ?? legacyFavoritesOnly),
    autoPrepStopOnError: Boolean(value.autoPrepStopOnError),
    autoRareConcurrency: normalizeRareAutoConcurrency(value.autoRareConcurrency ?? DEFAULT_RARE_AUTO_ORDERS_PER_TICK),
    autoNormalConcurrency: normalizeNormalAutoConcurrency(value.autoNormalConcurrency ?? DEFAULT_NORMAL_AUTO_ORDERS_PER_TICK),
    autoMaxStepRetries: normalizeAutoStepRetries(value.autoMaxStepRetries ?? DEFAULT_AUTO_STEP_RETRIES),
    autoMaxRollbacks: normalizeAutoRollbacks(value.autoMaxRollbacks ?? DEFAULT_AUTO_ROLLBACKS),
    filterMissingCookers: value.filterMissingCookers !== false,
    missionRecipePriorityEnabled: value.missionRecipePriorityEnabled !== false,
    pinFavoriteRecipeEnabled: Boolean(value.pinFavoriteRecipeEnabled),
    pinFavoriteBeverageEnabled: Boolean(value.pinFavoriteBeverageEnabled),
    rareGameUiPinningEnabled: Boolean(value.rareGameUiPinningEnabled),
    normalGameUiPinningEnabled: Boolean(value.normalGameUiPinningEnabled),
    rareRecipeVariantEnabled: Boolean(value.rareRecipeVariantEnabled),
    normalRecipeVariantEnabled: Boolean(value.normalRecipeVariantEnabled),
    rareCookerHighlightEnabled: Boolean(value.rareCookerHighlightEnabled),
    normalCookerHighlightEnabled: Boolean(value.normalCookerHighlightEnabled),
    rareSeatHighlightEnabled: Boolean(value.rareSeatHighlightEnabled),
    normalSeatHighlightEnabled: Boolean(value.normalSeatHighlightEnabled),
    rareOrderHighlightEnabled: Boolean(value.rareOrderHighlightEnabled),
    normalOrderHighlightEnabled: Boolean(value.normalOrderHighlightEnabled),
    rareTargetHighlightColor: normalizeTargetHighlightColor(
      value.rareTargetHighlightColor,
      DEFAULT_RARE_TARGET_HIGHLIGHT_COLOR,
    ),
    normalTargetHighlightColor: normalizeTargetHighlightColor(
      value.normalTargetHighlightColor,
      DEFAULT_NORMAL_TARGET_HIGHLIGHT_COLOR,
    ),
    showDebugDetails: Boolean(value.showDebugDetails),
    serviceOrderSortMode: value.serviceOrderSortMode === 'guest' ? 'guest' : 'ordered',
    recommendationSortProfile: normalizeRecommendationSortProfile(value.recommendationSortProfile),
    recommendationBudgetPolicy: normalizeRecommendationBudgetPolicy(value.recommendationBudgetPolicy),
    recipeVariantLimitPerBase: normalizeRecipeVariantLimitPerBase(value.recipeVariantLimitPerBase),
    recommendationExclusions: normalizeRecommendationExclusions(value.recommendationExclusions),
  };
}

export function readSharedCompanionPreferences(
  preferences: CompanionPreferences,
): SharedCompanionPreferences {
  const normalized = normalizeCompanionPreferences(preferences);
  return {
    automationEnabled: normalized.automationEnabled,
    autoRareOrderEnabled: normalized.autoRareOrderEnabled,
    rareGuestParticipationModuleEnabled: normalized.rareGuestParticipationModuleEnabled,
    managedRareGuestIds: normalized.managedRareGuestIds,
    autoNormalOrderEnabled: normalized.autoNormalOrderEnabled,
    autoNormalTakeBeverage: normalized.autoNormalTakeBeverage,
    autoNormalStartCooking: normalized.autoNormalStartCooking,
    autoNormalDeliverFood: normalized.autoNormalDeliverFood,
    autoNormalCompleteOrder: normalized.autoNormalCompleteOrder,
    autoNormalStopOnError: normalized.autoNormalStopOnError,
    autoPrepCompleteOrder: normalized.autoPrepCompleteOrder,
    autoPrepTakeBeverage: normalized.autoPrepTakeBeverage,
    autoPrepStartCooking: normalized.autoPrepStartCooking,
    autoPrepCollectCooking: normalized.autoPrepCollectCooking,
    autoPrepRecipeFavoritesOnly: normalized.autoPrepRecipeFavoritesOnly,
    autoPrepBeverageFavoritesOnly: normalized.autoPrepBeverageFavoritesOnly,
    autoPrepStopOnError: normalized.autoPrepStopOnError,
    autoRareConcurrency: normalized.autoRareConcurrency,
    autoNormalConcurrency: normalized.autoNormalConcurrency,
    autoMaxStepRetries: normalized.autoMaxStepRetries,
    autoMaxRollbacks: normalized.autoMaxRollbacks,
    filterMissingCookers: normalized.filterMissingCookers,
    missionRecipePriorityEnabled: normalized.missionRecipePriorityEnabled,
    pinFavoriteRecipeEnabled: normalized.pinFavoriteRecipeEnabled,
    pinFavoriteBeverageEnabled: normalized.pinFavoriteBeverageEnabled,
    rareGameUiPinningEnabled: normalized.rareGameUiPinningEnabled,
    normalGameUiPinningEnabled: normalized.normalGameUiPinningEnabled,
    rareRecipeVariantEnabled: normalized.rareRecipeVariantEnabled,
    normalRecipeVariantEnabled: normalized.normalRecipeVariantEnabled,
    rareCookerHighlightEnabled: normalized.rareCookerHighlightEnabled,
    normalCookerHighlightEnabled: normalized.normalCookerHighlightEnabled,
    rareSeatHighlightEnabled: normalized.rareSeatHighlightEnabled,
    normalSeatHighlightEnabled: normalized.normalSeatHighlightEnabled,
    rareOrderHighlightEnabled: normalized.rareOrderHighlightEnabled,
    normalOrderHighlightEnabled: normalized.normalOrderHighlightEnabled,
    rareTargetHighlightColor: normalized.rareTargetHighlightColor,
    normalTargetHighlightColor: normalized.normalTargetHighlightColor,
    serviceOrderSortMode: normalized.serviceOrderSortMode,
    recommendationSortProfile: normalized.recommendationSortProfile,
    recommendationBudgetPolicy: normalized.recommendationBudgetPolicy,
    recipeVariantLimitPerBase: normalized.recipeVariantLimitPerBase,
    recommendationExclusions: normalized.recommendationExclusions,
  };
}

export function normalizeSharedCompanionPreferences(
  value: Partial<SharedCompanionPreferences>,
): SharedCompanionPreferences {
  return readSharedCompanionPreferences(normalizeCompanionPreferences(value));
}

/**
 * 解析当前 JSON 协议格式下的完整共享配置。
 *
 * 这个边界不补默认值、不丢弃未知字段、不修正非规范数组，也不接受旧形状。
 * localStorage 容错和用户编辑仍由 `normalizeCompanionPreferences` 处理。
 */
export function parseSharedCompanionPreferences(value: unknown): SharedCompanionPreferences {
  const profile = requireExactWireRecord(
    value,
    SHARED_COMPANION_PREFERENCE_FIELDS,
    '共享配置',
  );
  const parsed: SharedCompanionPreferences = {
    automationEnabled: requireWireBoolean(profile, 'automationEnabled'),
    autoRareOrderEnabled: requireWireBoolean(profile, 'autoRareOrderEnabled'),
    rareGuestParticipationModuleEnabled: requireWireBoolean(
      profile,
      'rareGuestParticipationModuleEnabled',
    ),
    managedRareGuestIds: requireWireIdArray(
      profile.managedRareGuestIds,
      MAX_MANAGED_RARE_GUEST_IDS,
      '调度名单内稀客',
    ),
    autoNormalOrderEnabled: requireWireBoolean(profile, 'autoNormalOrderEnabled'),
    autoNormalTakeBeverage: requireWireBoolean(profile, 'autoNormalTakeBeverage'),
    autoNormalStartCooking: requireWireBoolean(profile, 'autoNormalStartCooking'),
    autoNormalDeliverFood: requireWireBoolean(profile, 'autoNormalDeliverFood'),
    autoNormalCompleteOrder: requireWireBoolean(profile, 'autoNormalCompleteOrder'),
    autoNormalStopOnError: requireWireBoolean(profile, 'autoNormalStopOnError'),
    autoPrepCompleteOrder: requireWireBoolean(profile, 'autoPrepCompleteOrder'),
    autoPrepTakeBeverage: requireWireBoolean(profile, 'autoPrepTakeBeverage'),
    autoPrepStartCooking: requireWireBoolean(profile, 'autoPrepStartCooking'),
    autoPrepCollectCooking: requireWireBoolean(profile, 'autoPrepCollectCooking'),
    autoPrepRecipeFavoritesOnly: requireWireBoolean(profile, 'autoPrepRecipeFavoritesOnly'),
    autoPrepBeverageFavoritesOnly: requireWireBoolean(profile, 'autoPrepBeverageFavoritesOnly'),
    autoPrepStopOnError: requireWireBoolean(profile, 'autoPrepStopOnError'),
    autoRareConcurrency: requireWireInteger(
      profile.autoRareConcurrency,
      MIN_AUTO_ORDER_CONCURRENCY,
      MAX_RARE_AUTO_ORDER_CONCURRENCY,
      'autoRareConcurrency',
    ),
    autoNormalConcurrency: requireWireInteger(
      profile.autoNormalConcurrency,
      MIN_AUTO_ORDER_CONCURRENCY,
      MAX_NORMAL_AUTO_ORDER_CONCURRENCY,
      'autoNormalConcurrency',
    ),
    autoMaxStepRetries: requireWireInteger(
      profile.autoMaxStepRetries,
      MIN_AUTO_STEP_RETRIES,
      MAX_AUTO_STEP_RETRIES_LIMIT,
      'autoMaxStepRetries',
    ),
    autoMaxRollbacks: requireWireInteger(
      profile.autoMaxRollbacks,
      MIN_AUTO_ROLLBACKS,
      MAX_AUTO_ROLLBACKS_LIMIT,
      'autoMaxRollbacks',
    ),
    filterMissingCookers: requireWireBoolean(profile, 'filterMissingCookers'),
    missionRecipePriorityEnabled: requireWireBoolean(profile, 'missionRecipePriorityEnabled'),
    pinFavoriteRecipeEnabled: requireWireBoolean(profile, 'pinFavoriteRecipeEnabled'),
    pinFavoriteBeverageEnabled: requireWireBoolean(profile, 'pinFavoriteBeverageEnabled'),
    rareGameUiPinningEnabled: requireWireBoolean(profile, 'rareGameUiPinningEnabled'),
    normalGameUiPinningEnabled: requireWireBoolean(profile, 'normalGameUiPinningEnabled'),
    rareRecipeVariantEnabled: requireWireBoolean(profile, 'rareRecipeVariantEnabled'),
    normalRecipeVariantEnabled: requireWireBoolean(profile, 'normalRecipeVariantEnabled'),
    rareCookerHighlightEnabled: requireWireBoolean(profile, 'rareCookerHighlightEnabled'),
    normalCookerHighlightEnabled: requireWireBoolean(profile, 'normalCookerHighlightEnabled'),
    rareSeatHighlightEnabled: requireWireBoolean(profile, 'rareSeatHighlightEnabled'),
    normalSeatHighlightEnabled: requireWireBoolean(profile, 'normalSeatHighlightEnabled'),
    rareOrderHighlightEnabled: requireWireBoolean(profile, 'rareOrderHighlightEnabled'),
    normalOrderHighlightEnabled: requireWireBoolean(profile, 'normalOrderHighlightEnabled'),
    rareTargetHighlightColor: requireWireColor(profile.rareTargetHighlightColor, 'rareTargetHighlightColor'),
    normalTargetHighlightColor: requireWireColor(
      profile.normalTargetHighlightColor,
      'normalTargetHighlightColor',
    ),
    serviceOrderSortMode: requireWireChoice(
      profile.serviceOrderSortMode,
      ['ordered', 'guest'] as const,
      'serviceOrderSortMode',
    ),
    recommendationSortProfile: parseWireRecommendationSortProfile(profile.recommendationSortProfile),
    recommendationBudgetPolicy: requireWireChoice(
      profile.recommendationBudgetPolicy,
      ['block', 'warn', 'ignore'] as const,
      'recommendationBudgetPolicy',
    ),
    recipeVariantLimitPerBase: requireWireInteger(
      profile.recipeVariantLimitPerBase,
      MIN_RECIPE_VARIANT_LIMIT_PER_BASE,
      MAX_RECIPE_VARIANT_LIMIT_PER_BASE,
      'recipeVariantLimitPerBase',
    ),
    recommendationExclusions: parseWireRecommendationExclusions(profile.recommendationExclusions),
  };

  if (!parsed.autoNormalCompleteOrder
    && (parsed.autoNormalTakeBeverage || parsed.autoNormalDeliverFood)) {
    throw new Error('共享配置中的普客送餐子步骤要求启用完成订单。');
  }
  if (!parsed.autoPrepCompleteOrder
    && (parsed.autoPrepTakeBeverage || parsed.autoPrepCollectCooking)) {
    throw new Error('共享配置中的稀客送餐子步骤要求启用完成订单。');
  }
  return parsed;
}

export function applySharedCompanionPreferences(
  current: CompanionPreferences,
  shared: SharedCompanionPreferences,
): CompanionPreferences {
  return normalizeCompanionPreferences({
    ...current,
    ...normalizeSharedCompanionPreferences(shared),
  });
}

export function serializeSharedCompanionPreferences(
  preferences: SharedCompanionPreferences,
): string {
  return JSON.stringify(normalizeSharedCompanionPreferences(preferences));
}

export function normalizeBackgroundOpacity(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_BACKGROUND_OPACITY;
  return Math.max(MIN_BACKGROUND_OPACITY, Math.min(1, value));
}

export function normalizeContentOpacity(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_CONTENT_OPACITY;
  return Math.max(MIN_CONTENT_OPACITY, Math.min(1, value));
}

export function normalizeFontScalePercent(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_FONT_SCALE_PERCENT;
  const stepped = Math.round(value / FONT_SCALE_PERCENT_STEP) * FONT_SCALE_PERCENT_STEP;
  return Math.max(MIN_FONT_SCALE_PERCENT, Math.min(MAX_FONT_SCALE_PERCENT, stepped));
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

export function normalizeTargetHighlightColor(value: unknown, fallback: string): string {
  return typeof value === 'string' && /^#[0-9A-Fa-f]{6}$/.test(value)
    ? value.toUpperCase()
    : fallback;
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
  localStorage.setItem(FONT_SCALE_PERCENT_STORAGE_KEY, String(normalized.fontScalePercent));
  localStorage.removeItem(LEGACY_WINDOW_OPACITY_STORAGE_KEY);
  localStorage.setItem(FOCUS_SWITCH_BEHAVIOR_STORAGE_KEY, normalized.focusSwitchBehavior);
  localStorage.setItem(FOCUS_SWITCH_COOLDOWN_STORAGE_KEY, String(normalized.focusSwitchCooldownMs));
  localStorage.setItem(ALWAYS_ON_TOP_STORAGE_KEY, normalized.alwaysOnTop ? '1' : '0');
  localStorage.setItem(GAMEPAD_NAVIGATION_STORAGE_KEY, normalized.gamepadNavigationEnabled ? '1' : '0');
  localStorage.setItem(AUTOMATION_ENABLED_STORAGE_KEY, normalized.automationEnabled ? '1' : '0');
  localStorage.setItem(AUTO_RARE_ORDER_ENABLED_STORAGE_KEY, normalized.autoRareOrderEnabled ? '1' : '0');
  localStorage.setItem(
    RARE_GUEST_PARTICIPATION_MODULE_ENABLED_STORAGE_KEY,
    normalized.rareGuestParticipationModuleEnabled ? '1' : '0',
  );
  localStorage.setItem(MANAGED_RARE_GUEST_IDS_STORAGE_KEY, JSON.stringify(normalized.managedRareGuestIds));
  localStorage.setItem(AUTO_NORMAL_ORDER_ENABLED_STORAGE_KEY, normalized.autoNormalOrderEnabled ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_TAKE_BEVERAGE_STORAGE_KEY, normalized.autoNormalTakeBeverage ? '1' : '0');
  localStorage.setItem(AUTO_NORMAL_START_COOKING_STORAGE_KEY, normalized.autoNormalStartCooking ? '1' : '0');
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
  localStorage.setItem(MISSION_RECIPE_PRIORITY_STORAGE_KEY, normalized.missionRecipePriorityEnabled ? '1' : '0');
  localStorage.setItem(PIN_FAVORITE_RECIPE_STORAGE_KEY, normalized.pinFavoriteRecipeEnabled ? '1' : '0');
  localStorage.setItem(PIN_FAVORITE_BEVERAGE_STORAGE_KEY, normalized.pinFavoriteBeverageEnabled ? '1' : '0');
  localStorage.setItem(RARE_GAME_UI_PINNING_STORAGE_KEY, normalized.rareGameUiPinningEnabled ? '1' : '0');
  localStorage.setItem(NORMAL_GAME_UI_PINNING_STORAGE_KEY, normalized.normalGameUiPinningEnabled ? '1' : '0');
  localStorage.setItem(RARE_RECIPE_VARIANT_STORAGE_KEY, normalized.rareRecipeVariantEnabled ? '1' : '0');
  localStorage.setItem(NORMAL_RECIPE_VARIANT_STORAGE_KEY, normalized.normalRecipeVariantEnabled ? '1' : '0');
  localStorage.setItem(RARE_COOKER_HIGHLIGHT_STORAGE_KEY, normalized.rareCookerHighlightEnabled ? '1' : '0');
  localStorage.setItem(NORMAL_COOKER_HIGHLIGHT_STORAGE_KEY, normalized.normalCookerHighlightEnabled ? '1' : '0');
  localStorage.setItem(RARE_SEAT_HIGHLIGHT_STORAGE_KEY, normalized.rareSeatHighlightEnabled ? '1' : '0');
  localStorage.setItem(NORMAL_SEAT_HIGHLIGHT_STORAGE_KEY, normalized.normalSeatHighlightEnabled ? '1' : '0');
  localStorage.setItem(RARE_ORDER_HIGHLIGHT_STORAGE_KEY, normalized.rareOrderHighlightEnabled ? '1' : '0');
  localStorage.setItem(NORMAL_ORDER_HIGHLIGHT_STORAGE_KEY, normalized.normalOrderHighlightEnabled ? '1' : '0');
  localStorage.setItem(RARE_TARGET_HIGHLIGHT_COLOR_STORAGE_KEY, normalized.rareTargetHighlightColor);
  localStorage.setItem(NORMAL_TARGET_HIGHLIGHT_COLOR_STORAGE_KEY, normalized.normalTargetHighlightColor);
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
 * 将视觉偏好写入 CSS 变量。
 */
export function applyCompanionVisualPreferences(preferences: CompanionPreferences) {
  const backgroundOpacity = normalizeBackgroundOpacity(preferences.backgroundOpacity);
  const backgroundPercent = `${Math.round(backgroundOpacity * 100)}%`;
  const contentPercent = `${Math.round(normalizeContentOpacity(preferences.contentOpacity) * 100)}%`;
  const fontScale = normalizeFontScalePercent(preferences.fontScalePercent) / 100;

  document.documentElement.style.setProperty('--companion-background-opacity-percent', backgroundPercent);
  document.documentElement.style.setProperty('--companion-content-opacity-percent', contentPercent);
  document.documentElement.style.setProperty('--companion-font-scale', String(fontScale));
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

function readStoredManagedRareGuestIds(): number[] {
  const raw = localStorage.getItem(MANAGED_RARE_GUEST_IDS_STORAGE_KEY);
  if (!raw) return [];

  try {
    return normalizeManagedRareGuestIds(JSON.parse(raw) as unknown);
  } catch {
    return [];
  }
}

/**
 * 将调度名单内稀客 ID 规范化为共享配置数组。
 */
export function normalizeManagedRareGuestIds(value: unknown): number[] {
  if (!Array.isArray(value)) return [];
  const ids = new Set<number>();
  for (const raw of value) {
    if (typeof raw !== 'number'
      || !Number.isInteger(raw)
      || raw < 0
      || raw > MAX_MANAGED_RARE_GUEST_ID) {
      continue;
    }
    ids.add(raw);
  }
  return [...ids]
    .sort((left, right) => left - right)
    .slice(0, MAX_MANAGED_RARE_GUEST_IDS);
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

function parseWireRecommendationSortProfile(value: unknown): RecommendationSortProfile {
  const profile = requireExactWireRecord(
    value,
    ['preset', 'objectives'] as const,
    '推荐排序配置',
  );
  const preset = requireWireChoice(
    profile.preset,
    ['balanced', 'resources', 'profit', 'simple'] as const,
    'recommendationSortProfile.preset',
  );
  if (!Array.isArray(profile.objectives)
    || profile.objectives.length !== RECOMMENDATION_OBJECTIVE_KEYS.length) {
    throw new Error('推荐排序配置必须完整包含当前版本要求的 8 项。');
  }

  const seen = new Set<RecommendationObjectiveKey>();
  const objectives = profile.objectives.map((value, index) => {
    const objective = requireExactWireRecord(
      value,
      ['key', 'enabled', 'weight', 'direction'] as const,
      `推荐排序目标 ${index + 1}`,
    );
    const key = requireWireChoice(
      objective.key,
      RECOMMENDATION_OBJECTIVE_KEYS,
      `recommendationSortProfile.objectives[${index}].key`,
    );
    if (seen.has(key)) throw new Error('推荐排序目标不得重复。');
    seen.add(key);
    return {
      key,
      enabled: requireWireBoolean(
        objective,
        'enabled',
        `recommendationSortProfile.objectives[${index}].enabled`,
      ),
      weight: requireWireInteger(
        objective.weight,
        0,
        100,
        `recommendationSortProfile.objectives[${index}].weight`,
      ),
      direction: requireWireChoice(
        objective.direction,
        ['asc', 'desc'] as const,
        `recommendationSortProfile.objectives[${index}].direction`,
      ),
    };
  });
  return { preset, objectives };
}

function parseWireRecommendationExclusions(value: unknown): RecommendationExclusions {
  const exclusions = requireExactWireRecord(
    value,
    ['excludedIngredientIds', 'excludedBeverageIds'] as const,
    '推荐排除项',
  );
  return {
    excludedIngredientIds: requireWireIdArray(
      exclusions.excludedIngredientIds,
      MAX_RECOMMENDATION_EXCLUSION_IDS,
      '排除食材',
    ),
    excludedBeverageIds: requireWireIdArray(
      exclusions.excludedBeverageIds,
      MAX_RECOMMENDATION_EXCLUSION_IDS,
      '排除酒水',
    ),
  };
}

function requireExactWireRecord(
  value: unknown,
  expectedFields: readonly string[],
  label: string,
): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new Error(`${label}必须是对象。`);
  }
  const record = value as Record<string, unknown>;
  const actualFields = Object.keys(record);
  const expected = new Set(expectedFields);
  if (actualFields.length !== expected.size || actualFields.some((field) => !expected.has(field))) {
    throw new Error(`${label}字段与当前配置格式不一致。`);
  }
  return record;
}

function requireWireBoolean(
  record: Record<string, unknown>,
  field: string,
  label = field,
): boolean {
  const value = record[field];
  if (typeof value !== 'boolean') throw new Error(`共享配置字段 ${label} 只能为开启或关闭。`);
  return value;
}

function requireWireInteger(value: unknown, min: number, max: number, label: string): number {
  if (typeof value !== 'number'
    || !Number.isInteger(value)
    || value < min
    || value > max) {
    throw new Error(`共享配置字段 ${label} 超出当前配置允许范围。`);
  }
  return value;
}

function requireWireChoice<const Choices extends readonly string[]>(
  value: unknown,
  choices: Choices,
  label: string,
): Choices[number] {
  if (typeof value !== 'string' || !(choices as readonly string[]).includes(value)) {
    throw new Error(`共享配置字段 ${label} 不是当前版本支持的选项。`);
  }
  return value as Choices[number];
}

function requireWireColor(value: unknown, label: string): string {
  if (typeof value !== 'string' || !/^#[0-9A-F]{6}$/.test(value)) {
    throw new Error(`共享配置字段 ${label} 必须是 #RRGGBB 格式的颜色。`);
  }
  return value;
}

function requireWireIdArray(value: unknown, maxCount: number, label: string): number[] {
  if (!Array.isArray(value) || value.length > maxCount) {
    throw new Error(`${label} ID 列表格式或数量与当前配置要求不一致。`);
  }
  const ids: number[] = [];
  let previous = -1;
  for (const id of value) {
    if (typeof id !== 'number'
      || !Number.isInteger(id)
      || id < 0
      || id > MAX_MANAGED_RARE_GUEST_ID
      || id <= previous) {
      throw new Error(`${label} ID 必须是 0 至 ${MAX_MANAGED_RARE_GUEST_ID} 的整数，并按从小到大排列且不能重复。`);
    }
    ids.push(id);
    previous = id;
  }
  return ids;
}

export function clampInteger(value: number, min: number, max: number, fallback: number) {
  if (!Number.isFinite(value)) return fallback;
  return Math.max(min, Math.min(max, Math.trunc(value)));
}
