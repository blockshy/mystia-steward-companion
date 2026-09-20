import { shouldRequestNormalOrderCompletion } from '@/companion/automation-machine';
import type { CompanionPreferences } from '@/companion/preferences';

interface RarePreparationActions {
  shouldPrepareBeverage: boolean;
  shouldPrepareFood: boolean;
  forceKoishiFullFeedAutomation: boolean;
}

/** Request projection after target and exact-cooker admission; does not mutate controller state. */
export function buildRarePreparationPreferences(
  companionPreferences: CompanionPreferences,
  { shouldPrepareBeverage, shouldPrepareFood, forceKoishiFullFeedAutomation }: RarePreparationActions,
): CompanionPreferences {
  return {
    ...companionPreferences,
    autoPrepTakeBeverage: shouldPrepareBeverage,
    autoPrepStartCooking: shouldPrepareFood,
    autoPrepCollectCooking: forceKoishiFullFeedAutomation || companionPreferences.autoPrepCollectCooking,
    autoPrepCompleteOrder: forceKoishiFullFeedAutomation || companionPreferences.autoPrepCompleteOrder,
  };
}

interface NormalRequestActions {
  shouldHandleBeverage: boolean;
  shouldStartCooking: boolean;
  shouldCompleteOrder: boolean;
  forceKoishiFullFeedAutomation: boolean;
}

export function buildNormalRequestPreferences(
  companionPreferences: CompanionPreferences,
  {
    shouldHandleBeverage,
    shouldStartCooking,
    shouldCompleteOrder,
    forceKoishiFullFeedAutomation,
  }: NormalRequestActions,
): CompanionPreferences {
  return {
    ...companionPreferences,
    autoNormalTakeBeverage:
      (companionPreferences.autoNormalTakeBeverage || forceKoishiFullFeedAutomation) && shouldHandleBeverage,
    autoNormalStartCooking:
      (companionPreferences.autoNormalStartCooking || forceKoishiFullFeedAutomation) && shouldStartCooking,
    autoNormalDeliverFood: companionPreferences.autoNormalDeliverFood || forceKoishiFullFeedAutomation,
    autoNormalCompleteOrder: shouldRequestNormalOrderCompletion({
      beverageDeliveryEnabled:
        (companionPreferences.autoNormalTakeBeverage || forceKoishiFullFeedAutomation) &&
        shouldHandleBeverage,
      completionEnabled: companionPreferences.autoNormalCompleteOrder,
      completionReady: shouldCompleteOrder,
      foodDeliveryEnabled: companionPreferences.autoNormalDeliverFood,
      forceKoishiFullFeedAutomation,
    }),
  };
}
