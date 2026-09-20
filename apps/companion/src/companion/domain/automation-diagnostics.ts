import { type NormalAutoOrderState } from '@/companion/automation-state';
import {
  type OrderPreparationCandidateResult,
  type ValidOrderPreparationSelection,
} from '@/companion/domain/automation';
import { formatDesk } from '@/companion/formatters';
import { type CompanionPreferences } from '@/companion/preferences';
import type {
  NormalAutoOrderDiagnostic,
  NormalBusinessOrder,
  OrderRecommendation,
  RareAutoOrderDiagnostic,
  SpecialBusinessContext,
} from '@/companion/types';
import type { NormalExecutionTargetSelection } from '@/companion/workers/order-recommendations.types';

import { MAX_AUTOMATION_DECISION_DIAGNOSTIC_SIGNATURES } from '@/companion/domain/automation-constants';
import {
  buildNormalOrderDetailSpecialBusinessSignature,
  buildOrderRecommendationPreferenceSignature,
} from '@/companion/domain/recommendation-input';

export interface NormalAutomationDecisionFlags {
  needsBeverage: boolean;
  needsCooking: boolean;
  needsCompletion: boolean;
  shouldHandleBeverage: boolean;
  shouldStartCooking: boolean;
  shouldCompleteOrder: boolean;
  completionConfigured: boolean;
  yuumaCompletionIntent: boolean;
  forceKoishiFullFeedAutomation: boolean;
  targetBlockedCooking: boolean;
}

export interface NormalAutomationDecisionDiagnosticInput {
  eventName: string;
  reason: string;
  order: NormalBusinessOrder;
  state: NormalAutoOrderState | null | undefined;
  targetSelection: NormalExecutionTargetSelection;
  requestPreferences: CompanionPreferences;
  flags: NormalAutomationDecisionFlags;
}

export function buildAutomationDecisionDiagnosticSignature(
  eventName: string,
  message: string,
  specialBusiness: SpecialBusinessContext | null,
  orderLines: readonly string[],
  selectionLines: readonly string[],
  skipLines: readonly string[],
  preferences: CompanionPreferences,
  leaseOwned: boolean,
): string {
  return hashDiagnosticSignature(
    [
      eventName,
      message,
      buildNormalOrderDetailSpecialBusinessSignature(specialBusiness),
      buildOrderRecommendationPreferenceSignature(preferences),
      leaseOwned ? 1 : 0,
      orderLines.join('|'),
      selectionLines.join('|'),
      skipLines.join('|'),
    ].join('\n'),
  );
}

export function buildAutomationDecisionOrderLine(item: OrderRecommendation): string {
  const order = item.order;
  const diagnostic = item.blockedDiagnostic;
  return [
    `trace=${order.traceId ?? ''}`,
    `desk=${formatDesk(order.deskCode)}`,
    `guest=${order.guestName || '稀客'}`,
    `role=${order.specialBusinessRole ?? ''}`,
    `tags=${order.foodTag || '无'}/${order.beverageTag || '无'}`,
    `served=${order.hasServedFood ? 1 : 0}/${order.hasServedBeverage ? 1 : 0}`,
    `recommendations=${item.recipes.length}/${item.beverages.length}`,
    `plans=${item.executionPlans.length}`,
    `blocked=${item.blockedMessages.length}`,
    `blockedDetail=${formatRecommendationBlockedMessages(item)}`,
    `blockedStage=${diagnostic ? `${diagnostic.code}/${diagnostic.firstEmptyStage}` : ''}`,
    `blockedState=${diagnostic ? hashDiagnosticSignature(diagnostic.stateSignature) : ''}`,
    `candidateStages=${formatRecommendationCandidateStages(diagnostic)}`,
    `blockedResources=${formatRecommendationBlockedResources(diagnostic)}`,
    `top=${formatRecommendationTopTarget(item)}`,
    `primary=${formatRecommendationPlanTarget(item.executionPlans[0] ?? null)}`,
  ].join('; ');
}

export function formatRecommendationCandidateStages(
  diagnostic: OrderRecommendation['blockedDiagnostic'],
): string {
  if (!diagnostic) return '';
  const counts = diagnostic.counts;
  const recipes = counts.foodRecipeEligibility;
  const food = counts.foodCandidates;
  const beverage = counts.beverageCandidates;
  const plans = counts.plans;
  return [
    `foodRecipes=catalog:${recipes.catalog},tagReachable:${recipes.requiredTagReachable},` +
      `unlocked:${recipes.requiredTagReachableUnlocked},` +
      `baseReady:${recipes.requiredTagReachableBaseIngredientsReady},` +
      `cookerReady:${recipes.requiredTagReachableCookerReady}`,
    `foodCandidates=generated:${food.generated},requiredTag:${food.generatedRequiredTagMatched},` +
      `merged:${food.merged},baseMatch:${food.baseOrderMatched},` +
      `negativeSafe:${food.negativeSafe},specialRule:${food.specialRuleMatched},` +
      `executable:${food.executable}`,
    `beverageCandidates=catalog:${beverage.catalog},available:${beverage.available},` +
      `allowed:${beverage.allowed},requiredTag:${beverage.requiredTagMatched},` +
      `specialRule:${beverage.specialRuleMatched}`,
    `plans=rawExecutable:${plans.rawExecutable},specialRuleSafe:${plans.specialRuleSafe},` +
      `executable:${plans.executable}`,
  ].join('/');
}

export function formatRecommendationBlockedResources(
  diagnostic: OrderRecommendation['blockedDiagnostic'],
): string {
  if (!diagnostic) return '';
  return [
    diagnostic.missingIngredientNames.length > 0
      ? `ingredients=${diagnostic.missingIngredientNames.join(',')}`
      : '',
    diagnostic.requiredCookerNames.length > 0
      ? `requiredCookers=${diagnostic.requiredCookerNames.join(',')}`
      : '',
    `placedCookers=${diagnostic.placedCookerNames.join(',') || 'none'}`,
    `budget=${diagnostic.remainingBudget ?? 'unknown'}`,
    `minimumPair=${diagnostic.minimumPairPrice ?? 'unknown'}`,
  ]
    .filter(Boolean)
    .join('/');
}

export function buildAutomationDecisionSelectionLine(selection: ValidOrderPreparationSelection): string {
  const order = selection.item.order;
  return [
    `trace=${order.traceId ?? ''}`,
    `desk=${formatDesk(order.deskCode)}`,
    `guest=${order.guestName || '稀客'}`,
    `role=${order.specialBusinessRole ?? ''}`,
    `recipe=${selection.recipeTarget?.recipeName ?? selection.recipe?.recipe.name ?? ''}`,
    `recipeId=${selection.recipeTarget?.recipeId ?? selection.recipe?.recipe.recipeId ?? -1}`,
    `foodId=${selection.recipeTarget?.foodId ?? selection.recipe?.recipe.id ?? -1}`,
    `extras=${selection.recipeTarget?.extraIngredientIds.join(',') ?? selection.recipe?.extraIngredients.map((ingredient) => ingredient.id).join(',') ?? ''}`,
    `beverage=${selection.beverageTarget?.beverageName ?? selection.beverage?.beverage.name ?? ''}`,
    `beverageId=${selection.beverageTarget?.beverageId ?? selection.beverage?.beverage.id ?? -1}`,
    `favorite=${selection.recipeFavorite ? 1 : 0}/${selection.beverageFavorite ? 1 : 0}`,
  ].join('; ');
}

export function buildAutomationDecisionSkipLine(
  skip: OrderPreparationCandidateResult['skips'][number],
): string {
  return [
    `orderKey=${skip.orderKey}`,
    `reason=${skip.reason}`,
    `recommendations=${skip.recipeRecommendationCount}/${skip.beverageRecommendationCount}`,
    `plans=${skip.executionPlanCount}`,
    `message=${compactDiagnosticText(skip.message)}`,
  ].join('; ');
}

export function formatNormalAutomationTarget(target: NormalExecutionTargetSelection['target']): string {
  if (!target) return 'none';
  return [
    `${target.recipeName}#${target.recipeId}->${target.foodId}`,
    `${target.beverageName}#${target.beverageId}`,
    target.executionMode ? `mode=${target.executionMode}` : '',
    `yuumaControlled=${target.allowYuumaControlledProgression ? 1 : 0}`,
    `match=${target.matchFoodId}/${target.matchBeverageId}`,
    `extras=${target.extraIngredientIds.join(',')}`,
    `modifiers=${target.expectedFoodModifierTags.join(',')}`,
    target.reason,
  ]
    .filter(Boolean)
    .join('/');
}

export function buildNormalAutomationDecisionOrderLine(
  input: NormalAutomationDecisionDiagnosticInput,
): string {
  const { flags, order, state, targetSelection } = input;
  return [
    `trace=${order.traceId ?? ''}`,
    `orderKey=${targetSelection.orderKey}`,
    `desk=${formatDesk(order.deskCode)}`,
    `guest=${order.guestName || '普客'}`,
    `role=${order.specialBusinessRole ?? ''}`,
    `order=${order.foodName || `#${order.foodId}`}/${order.beverageName || `#${order.beverageId}`}`,
    `served=${order.hasServedFood ? 1 : 0}/${order.hasServedBeverage ? 1 : 0}`,
    `ready=${order.readyToEvaluate ? 1 : 0}`,
    `state=${state?.step ?? 'none'}/${state?.prepared ? 1 : 0}/${state?.beverageHandled ? 1 : 0}/${state?.foodDelivered ? 1 : 0}/${state?.completed ? 1 : 0}`,
    `needs=${flags.needsCooking ? 1 : 0}/${flags.needsBeverage ? 1 : 0}/${flags.needsCompletion ? 1 : 0}`,
    `actions=${flags.shouldStartCooking ? 1 : 0}/${flags.shouldHandleBeverage ? 1 : 0}/${flags.shouldCompleteOrder ? 1 : 0}`,
    `request=${input.requestPreferences.autoNormalStartCooking ? 1 : 0}/${input.requestPreferences.autoNormalTakeBeverage ? 1 : 0}/${input.requestPreferences.autoNormalDeliverFood ? 1 : 0}/${input.requestPreferences.autoNormalCompleteOrder ? 1 : 0}`,
    `completion=${flags.completionConfigured ? 1 : 0}/${flags.yuumaCompletionIntent ? 1 : 0}/${input.requestPreferences.autoNormalCompleteOrder ? 1 : 0}`,
    `forceKoishi=${flags.forceKoishiFullFeedAutomation ? 1 : 0}`,
    `targetBlockedCooking=${flags.targetBlockedCooking ? 1 : 0}`,
    `target=${formatNormalAutomationTarget(targetSelection.target)}`,
    `message=${compactDiagnosticText(targetSelection.message)}`,
  ].join('; ');
}

export function formatRecommendationTopTarget(item: OrderRecommendation): string {
  const recipe = item.recipes[0] ?? null;
  const beverage = item.beverages[0] ?? null;
  return [
    recipe
      ? `${recipe.recipe.name}#${recipe.recipe.id}+${recipe.extraIngredients.map((ingredient) => ingredient.id).join(',')}`
      : '',
    beverage ? `${beverage.beverage.name}#${beverage.beverage.id}` : '',
  ]
    .filter(Boolean)
    .join('/');
}

export function formatRecommendationPrimaryTarget(item: OrderRecommendation): string {
  const plan = item.executionPlans[0] ?? null;
  if (!plan) return '';
  return [
    plan.food
      ? `${plan.food.recipe.name}#${plan.food.recipe.id}+${plan.food.extraIngredients.map((ingredient) => ingredient.id).join(',')}`
      : '',
    plan.beverage ? `${plan.beverage.beverage.name}#${plan.beverage.beverage.id}` : '',
  ]
    .filter(Boolean)
    .join('/');
}

export function hasPrimaryRecommendationMismatch(recommendations: readonly OrderRecommendation[]): boolean {
  return recommendations.some((item) => {
    const primaryTarget = formatRecommendationPrimaryTarget(item);
    return primaryTarget.length > 0 && formatRecommendationTopTarget(item) !== primaryTarget;
  });
}

export function formatRecommendationPlanTarget(
  plan: OrderRecommendation['executionPlans'][number] | null,
): string {
  if (!plan) return '';
  return [
    plan.food
      ? `${plan.food.recipe.name}#${plan.food.recipe.id}+${plan.food.extraIngredients.map((ingredient) => ingredient.id).join(',')}`
      : '',
    plan.beverage ? `${plan.beverage.beverage.name}#${plan.beverage.beverage.id}` : '',
    plan.reasons[0] ?? '',
  ]
    .filter(Boolean)
    .join('/');
}

export function formatRecommendationBlockedMessages(item: OrderRecommendation): string {
  return item.blockedMessages.slice(0, 3).map(compactDiagnosticText).join(' | ');
}

export function compactDiagnosticText(value: string): string {
  return value.replace(/\s+/g, ' ').trim();
}

export function hashDiagnosticSignature(value: string): string {
  let hash = 2166136261;
  for (let index = 0; index < value.length; index++) {
    hash ^= value.charCodeAt(index);
    hash = Math.imul(hash, 16777619);
  }
  return (hash >>> 0).toString(16).padStart(8, '0');
}

export function rememberBoundedDiagnosticSignature(signatures: Set<string>, signature: string): boolean {
  if (signatures.has(signature)) return false;
  signatures.add(signature);
  while (signatures.size > MAX_AUTOMATION_DECISION_DIAGNOSTIC_SIGNATURES) {
    const oldest = signatures.values().next().value;
    if (oldest == null) break;
    signatures.delete(oldest);
  }
  return true;
}

export function buildRareAutomationDiagnosticsSignature(items: readonly RareAutoOrderDiagnostic[]): string {
  return items
    .map((item) =>
      [
        item.orderKey,
        item.traceId ?? '',
        item.title,
        item.foodTag,
        item.beverageTag,
        item.recipeName,
        item.beverageName,
        item.stepLabel,
        item.stepSeconds,
        item.nextAction,
        item.retryCount,
        item.rollbackCount,
        item.lastError,
        item.detailMessage,
        item.detailUpdatedAtMs,
        item.prepared ? 1 : 0,
        item.beverageDeliveryRequested ? 1 : 0,
        item.hasServedFood ? 1 : 0,
        item.hasServedBeverage ? 1 : 0,
        item.paused ? 1 : 0,
        item.manualResolutionRequired ? 1 : 0,
      ].join('~'),
    )
    .join('|');
}

export function buildNormalAutomationDiagnosticsSignature(
  items: readonly NormalAutoOrderDiagnostic[],
): string {
  return items
    .map((item) =>
      [
        item.orderKey,
        item.traceId ?? '',
        item.title,
        item.foodName,
        item.beverageName,
        item.source,
        item.stepLabel,
        item.stepSeconds,
        item.nextAction,
        item.retryCount,
        item.rollbackCount,
        item.lastError,
        item.detailMessage,
        item.detailUpdatedAtMs,
        item.prepared ? 1 : 0,
        item.beverageDeliveryRequested ? 1 : 0,
        item.foodDeliveryRequested ? 1 : 0,
        item.completed ? 1 : 0,
        item.paused ? 1 : 0,
        item.manualResolutionRequired ? 1 : 0,
        item.hasServedFood ? 1 : 0,
        item.hasServedBeverage ? 1 : 0,
        item.readyToEvaluate ? 1 : 0,
        item.hasEvaluated ? 1 : 0,
        item.controllerAvailable === false ? 0 : 1,
        item.canAutomate === false ? 0 : 1,
        item.actionBlockReason ?? '',
      ].join('~'),
    )
    .join('|');
}
