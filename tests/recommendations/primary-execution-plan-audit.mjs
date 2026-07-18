import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  buildPrimaryExecutionPlanPolicy,
  getPrimaryExecutionPlan,
  normalizePrimaryExecutionPlans,
} from '../../apps/companion/src/companion/domain/primary-execution-plan.ts';

const root = new URL('../../', import.meta.url);
const basePreferences = {
  automationEnabled: true,
  autoPrepStartCooking: true,
  autoPrepTakeBeverage: true,
  autoPrepRecipeFavoritesOnly: true,
  autoPrepBeverageFavoritesOnly: true,
};

assert.deepEqual(buildPrimaryExecutionPlanPolicy(basePreferences), {
  requireRecipeFavorite: true,
  requireBeverageFavorite: true,
});
assert.deepEqual(buildPrimaryExecutionPlanPolicy(basePreferences, false), {
  requireRecipeFavorite: false,
  requireBeverageFavorite: false,
}, 'An order that forbids automation must retain its recommendation ordering.');
assert.deepEqual(buildPrimaryExecutionPlanPolicy({
  ...basePreferences,
  autoPrepStartCooking: false,
}), {
  requireRecipeFavorite: false,
  requireBeverageFavorite: true,
}, 'A disabled automation stage must not affect primary-plan ordering.');
assert.deepEqual(buildPrimaryExecutionPlanPolicy({
  ...basePreferences,
  automationEnabled: false,
}), {
  requireRecipeFavorite: false,
  requireBeverageFavorite: false,
}, 'Favorite-only settings must not affect display ordering while automation is disabled.');

const plans = Array.from({ length: 40 }, (_, index) => buildPlan(index + 1, index + 101));
const lateJointFavorite = plans[37];
const recipeKey = `${lateJointFavorite.food.recipe.id}:11,29`;
const normalized = normalizePrimaryExecutionPlans(plans, {
  favoriteRecipeKeys: new Set([recipeKey]),
  favoriteBeverageIds: new Set([lateJointFavorite.beverage.beverage.id]),
}, buildPrimaryExecutionPlanPolicy(basePreferences));
assert.equal(getPrimaryExecutionPlan(normalized), lateJointFavorite,
  'The eligible joint favorite must become the single primary plan.');
assert.ok(normalized.slice(0, 32).includes(lateJointFavorite),
  'Favorite normalization must happen before the automation plan limit is applied.');
assert.deepEqual(
  normalized.slice(1).map(planIdentity),
  plans.filter((plan) => plan !== lateJointFavorite).map(planIdentity),
  'Promoting the primary plan must preserve the relative order of all remaining plans.',
);

const splitFavorites = [
  buildPlan(1, 101),
  buildPlan(2, 102),
  buildPlan(3, 103),
  buildPlan(4, 104),
];
const jointOnly = normalizePrimaryExecutionPlans(splitFavorites, {
  favoriteRecipeKeys: new Set(['2:11,29', '4:11,29']),
  favoriteBeverageIds: new Set([103, 104]),
}, buildPrimaryExecutionPlanPolicy(basePreferences));
assert.equal(getPrimaryExecutionPlan(jointOnly), splitFavorites[3],
  'Recipe and beverage favorite restrictions must match the same executable plan.');

const noMatch = normalizePrimaryExecutionPlans(splitFavorites, {
  favoriteRecipeKeys: new Set(['99:']),
  favoriteBeverageIds: new Set([999]),
}, buildPrimaryExecutionPlanPolicy(basePreferences));
assert.deepEqual(noMatch.map(planIdentity), splitFavorites.map(planIdentity),
  'A missing favorite plan must not invent a fallback ordering.');

await assertSourceContracts();

console.log('PASS: one primary execution plan owns display, automation, pinning, and pre-limit favorite normalization.');

function buildPlan(recipeId, beverageId) {
  return {
    bucket: 'complete',
    food: {
      recipe: { id: recipeId },
      extraIngredients: [{ id: 29 }, { id: 11 }, { id: 29 }],
    },
    beverage: {
      beverage: { id: beverageId },
    },
  };
}

function planIdentity(plan) {
  return `${plan.food.recipe.id}/${plan.beverage.beverage.id}`;
}

async function assertSourceContracts() {
  const [service, automation, types, workbench] = await Promise.all([
    readFile(new URL('apps/companion/src/companion/domain/service-recommendations.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/domain/automation.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/types.ts', root), 'utf8'),
    readFile(new URL('apps/companion/src/companion/ModWorkbench.tsx', root), 'utf8'),
  ]);

  const normalizeIndex = service.indexOf('normalizePrimaryExecutionPlans(');
  const truncateIndex = service.indexOf('executionPlans: executionPlans.slice(0, executionPlanLimit)');
  assert.ok(normalizeIndex >= 0 && truncateIndex > normalizeIndex,
    'Primary favorite normalization must happen before execution-plan truncation.');
  assert.ok(service.includes('projectPrimaryExecutionPlanRows('),
    'The primary plan must be projected into both display lists.');
  assert.ok(service.includes('order.automationAllowed !== false'),
    'Per-order automation policy must gate favorite-only primary normalization.');
  assert.equal(service.includes('pinSpecialBusinessExecutionPlanRows'), false,
    'The special-business-only row projection must be removed.');
  assert.equal(`${service}\n${automation}\n${types}\n${workbench}`.includes('preparationPlan'), false,
    'The removed duplicate preparation-plan contract must not remain.');

  const picker = functionSlice(automation, 'pickPlanForPreparation', 'emptyPlanPick');
  assert.ok(picker.includes('getPrimaryExecutionPlan(item.executionPlans)'),
    'Rare automation must read the shared primary plan.');
  assert.equal(picker.includes('for (const plan of'), false,
    'Rare automation must not scan later plans and create a second target.');
  const pinning = functionSlice(automation, 'buildGameUiPinningTarget', 'hasAutomationActionEnabled');
  assert.ok(pinning.includes('getPrimaryExecutionPlan(item.executionPlans)'),
    'Game UI pinning and cooker highlight must read the shared primary plan.');
  assert.equal(pinning.includes('item.recipes[0]'), false,
    'Game UI pinning must not fall back to independently paired display rows.');

  assert.ok(
    workbench.includes('serializePrimaryExecutionPlanPolicy(buildPrimaryExecutionPlanPolicy(preferences))'),
    'Worker preference signature must use the normalized primary policy.',
  );
  assert.equal(
    workbench.includes('preferences.autoPrepRecipeFavoritesOnly ? 1 : 0'),
    false,
    'Inactive favorite-only flags must not invalidate the recommendation worker signature.',
  );
  assert.ok(
    workbench.includes("primaryTargetMismatch ? 'rare-primary-target-mismatch' : eventName"),
    'A low-noise diagnostic must identify any regression that separates the displayed first row from the primary plan.',
  );
}

function functionSlice(source, methodName, nextMethodName) {
  const start = source.indexOf(`function ${methodName}(`);
  const end = source.indexOf(`function ${nextMethodName}(`, start + 1);
  assert.ok(start >= 0, `Method not found: ${methodName}`);
  assert.ok(end > start, `Method boundary not found: ${methodName} -> ${nextMethodName}`);
  return source.slice(start, end);
}
