import assert from 'node:assert/strict';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true, hmr: false },
  appType: 'custom',
  logLevel: 'silent',
});
try {
  const [selection, state, registry, normalKey, planning] = await Promise.all([
    vite.ssrLoadModule('/src/companion/domain/automation-target.ts'),
    vite.ssrLoadModule('/src/companion/automation-state.ts'),
    vite.ssrLoadModule('/src/companion/domain/special-business/registry.ts'),
    vite.ssrLoadModule('/src/companion/domain/normal-order-key.ts'),
    vite.ssrLoadModule('/src/companion/domain/automation-request-plan.ts'),
  ]);
  const order = { orderKey: 'ptr:abc', orderLifecycleSequence: 12, specialBusinessRole: 'yuuma-boss-order' };
  const oldContext = {
    active: true, challengeTypeAvailable: true, challengeType: 'Story_BloodPondHell',
    displayName: '血池地狱', ruleSummary: '', foodTargetTags: ['旧目标'], beverageTargetTags: [],
    yuumaFoodTargetRevision: 17, phase: 'Phase 1', recommendationPolicy: 'yuuma-target',
    automationPolicy: 'manual', source: 'test',
  };
  const newContext = { ...oldContext, foodTargetTags: ['新目标'], yuumaFoodTargetRevision: 18 };
  const oldPolicy = registry.buildSpecialFoodTargetWirePolicy(oldContext, order.specialBusinessRole, 7);
  const newPolicy = registry.buildSpecialFoodTargetWirePolicy(newContext, order.specialBusinessRole, 7);
  const orderKey = normalKey.buildNormalAutoOrderKey(order);
  const oldTarget = { ...oldPolicy, foodId: 101, recipeId: 201, recipeName: '旧料理', predictedFoodTags: ['旧目标'], extraIngredientIds: [], executionMode: 'standard' };
  const newTarget = { ...oldTarget, ...newPolicy, recipeId: 202, recipeName: '新料理', predictedFoodTags: ['新目标'] };
  const map = (target) => new Map([[orderKey, { orderKey, target, message: '' }]]);
  const current = { isCurrent: true, pending: false, error: null };
  const choose = (target, readiness = current, executionState, context = newContext, generation = 7) =>
    selection.getNormalAutomationTargetSelection(order, executionState, true, map(target), context, generation, true, readiness);

  assert.equal(choose(oldTarget, { ...current, pending: true, isCurrent: false }).target, null,
    'A pending computation must not authorize a retained execution plan.');
  assert.equal(choose(oldTarget, { ...current, isCurrent: false }).target, null);
  assert.match(choose(oldTarget, { ...current, error: 'worker failed' }).message, /worker failed/);
  assert.equal(choose(oldTarget).target, null,
    'Even a nominally current result must carry the exact current target identity.');
  assert.equal(oldTarget.specialTargetRevision, 17, 'Selection must not relabel an old target.');
  assert.equal(choose(newTarget).target, newTarget);

  const locked = state.lockNormalOrderExecutionTarget(state.emptyNormalAutoOrderState(orderKey, 1), newTarget, 7);
  const active = { ...locked, prepared: true, cookingJobId: 'job-1' };
  assert.equal(choose(oldTarget, { ...current, pending: true, isCurrent: false }, active).target, newTarget,
    'An admitted task must retain its exact target while another recommendation is pending.');
  assert.equal(choose(oldTarget, { ...current, error: 'worker failed' }, active).target, newTarget);
  assert.equal(choose(oldTarget, current, active, newContext, 8).target, null,
    'A locked target from another business generation must not authorize a new command.');
  const rotated = { ...newContext, yuumaFoodTargetRevision: 19 };
  assert.equal(choose(newTarget, current, active, rotated).target, null,
    'A target rotation must not stamp a new revision on a retained task target.');
  for (const field of ['specialTargetOwner', 'specialTargetChallenge', 'specialTargetMatchMode']) {
    assert.equal(choose({ ...newTarget, [field]: 'invalid' }).target, null, `${field} must match exactly.`);
  }
  assert.equal(choose({ ...newTarget, specialTargetFoodTags: ['旧目标'] }).target, null);
  assert.equal(choose(newTarget, current, { ...active, executionTargetBusinessGeneration: 6 }).target, newTarget,
    'After an old locked target becomes ineligible, a fresh current result can be admitted.');
  for (const force of [false, true]) {
    for (const deliverFood of [false, true]) {
      for (const beverage of [false, true]) {
        for (const food of [false, true]) {
          const preferences = Object.freeze({
            autoNormalTakeBeverage: true, autoNormalStartCooking: true,
            autoNormalDeliverFood: deliverFood, autoNormalCompleteOrder: true,
            autoPrepTakeBeverage: true, autoPrepStartCooking: true,
            autoPrepCollectCooking: deliverFood, autoPrepCompleteOrder: true,
          });
          const normal = planning.buildNormalRequestPreferences(preferences, {
            shouldHandleBeverage: beverage, shouldStartCooking: food, shouldCompleteOrder: false,
            forceKoishiFullFeedAutomation: force,
          });
          assert.equal(normal.autoNormalTakeBeverage, beverage);
          assert.equal(normal.autoNormalStartCooking, food);
          assert.equal(normal.autoNormalDeliverFood, force || deliverFood);
          assert.equal(normal.autoNormalCompleteOrder, force || deliverFood || beverage,
            'Every admitted direct delivery must carry completion intent.');
          const rare = planning.buildRarePreparationPreferences(preferences, {
            shouldPrepareBeverage: beverage, shouldPrepareFood: food, forceKoishiFullFeedAutomation: force,
          });
          assert.equal(rare.autoPrepTakeBeverage, beverage);
          assert.equal(rare.autoPrepStartCooking, food);
          assert.equal(rare.autoPrepCollectCooking, force || deliverFood);
          assert.equal(rare.autoPrepCompleteOrder, true);
          assert.equal(preferences.autoPrepTakeBeverage, true, 'Projection must not alter shared preferences.');
        }
      }
    }
  }
  const disabled = planning.buildNormalRequestPreferences({
    autoNormalTakeBeverage: false, autoNormalStartCooking: false,
    autoNormalDeliverFood: false, autoNormalCompleteOrder: false,
  }, { shouldHandleBeverage: true, shouldStartCooking: true, shouldCompleteOrder: true, forceKoishiFullFeedAutomation: false });
  assert.deepEqual(Object.values(disabled), [false, false, false, false], 'Demand must not override disabled stages.');
  console.log('PASS: new normal execution targets require current results and exact policy; admitted tasks retain their original identity.');
} finally {
  await vite.close();
}
