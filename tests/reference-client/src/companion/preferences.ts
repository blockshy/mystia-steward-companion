import {
  normalizeRecommendationSortProfile,
  serializeRecommendationSortProfile,
  type RecommendationBudgetPolicy,
  type RecommendationExclusions,
  type RecommendationSortProfile,
} from '@/recommendation-engine';

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
  mousePassthroughEnabled: boolean;
  gamepadNavigationEnabled: boolean;
  automationEnabled: boolean;
  autoRareOrderEnabled: boolean;
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

export const SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION = 1;

export function normalizeEditableQuantity(value: number) {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(9999, Math.trunc(value)));
}

export function normalizeFocusRecommendationLimit(value: number) {
  if (!Number.isFinite(value)) return DEFAULT_FOCUS_RECOMMENDATION_ROWS;
  return Math.max(1, Math.min(MAX_FOCUS_RECOMMENDATION_ROWS, Math.trunc(value)));
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
    mousePassthroughEnabled: Boolean(value.mousePassthroughEnabled),
    gamepadNavigationEnabled: Boolean(value.gamepadNavigationEnabled),
    automationEnabled: Boolean(value.automationEnabled),
    autoRareOrderEnabled: value.autoRareOrderEnabled !== false,
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

export function normalizeRecommendationBudgetPolicy(value: unknown): RecommendationBudgetPolicy {
  return value === 'warn' || value === 'ignore' ? value : 'block';
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
