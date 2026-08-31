import { createHash } from 'node:crypto';

export function buildCurrentSharedProfileV3(overrides = {}) {
  const profile = {
    automationEnabled: false,
    autoRareOrderEnabled: true,
    rareGuestParticipationModuleEnabled: false,
    managedRareGuestIds: [],
    autoNormalOrderEnabled: false,
    autoNormalTakeBeverage: false,
    autoNormalStartCooking: false,
    autoNormalDeliverFood: false,
    autoNormalCompleteOrder: false,
    autoNormalStopOnError: false,
    autoPrepCompleteOrder: false,
    autoPrepTakeBeverage: false,
    autoPrepStartCooking: false,
    autoPrepCollectCooking: false,
    autoPrepRecipeFavoritesOnly: false,
    autoPrepBeverageFavoritesOnly: false,
    autoPrepStopOnError: false,
    autoRareConcurrency: 2,
    autoNormalConcurrency: 3,
    autoMaxStepRetries: 3,
    autoMaxRollbacks: 2,
    filterMissingCookers: true,
    missionRecipePriorityEnabled: true,
    pinFavoriteRecipeEnabled: false,
    pinFavoriteBeverageEnabled: false,
    rareGameUiPinningEnabled: false,
    normalGameUiPinningEnabled: false,
    rareRecipeVariantEnabled: false,
    normalRecipeVariantEnabled: false,
    rareCookerHighlightEnabled: false,
    normalCookerHighlightEnabled: false,
    rareSeatHighlightEnabled: false,
    normalSeatHighlightEnabled: false,
    rareOrderHighlightEnabled: false,
    normalOrderHighlightEnabled: false,
    rareTargetHighlightColor: '#FFDB2E',
    normalTargetHighlightColor: '#5FACD3',
    serviceOrderSortMode: 'ordered',
    recommendationSortProfile: {
      preset: 'balanced',
      objectives: [
        objective('foodPreference', 'desc'),
        objective('beveragePreference', 'desc'),
        objective('negativeRisk', 'asc'),
        objective('extraCount', 'asc'),
        objective('resourcePressure', 'asc'),
        objective('totalCost', 'asc'),
        objective('profit', 'desc'),
        objective('beverageStock', 'desc'),
        objective('cookerAvailable', 'desc'),
      ],
    },
    recommendationBudgetPolicy: 'block',
    recipeVariantLimitPerBase: 1,
    recommendationExclusions: {
      excludedIngredientIds: [],
      excludedBeverageIds: [],
    },
  };
  return { ...profile, ...overrides };
}

export function hashCurrentSharedProfileV3(profile) {
  return createHash('sha256').update(canonicalJson(profile)).digest('hex');
}

function objective(key, direction) {
  return { key, enabled: true, weight: 50, direction };
}

function canonicalJson(value) {
  if (Array.isArray(value)) return `[${value.map(canonicalJson).join(',')}]`;
  if (value && typeof value === 'object') {
    return `{${Object.keys(value)
      .sort()
      .map((key) => `${JSON.stringify(key)}:${canonicalJson(value[key])}`)
      .join(',')}}`;
  }
  return JSON.stringify(value);
}
