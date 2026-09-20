import assert from 'node:assert/strict';
import { createServer } from 'vite';

const vite = await createServer({ configFile: 'apps/companion/vite.config.ts', server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
try {
  const { OrderRecommendationController } = await vite.ssrLoadModule('/src/companion/workers/order-recommendations-controller.ts');
  const { normalizeCompanionPreferences } = await vite.ssrLoadModule('/src/companion/preferences.ts');
  const { buildRecommendationDataSignature } = await vite.ssrLoadModule('/src/lib/recommendation-data.ts');
  const { buildOrderRecommendations, buildRareCustomerMap, createRecommendationCacheStore } = await vite.ssrLoadModule('/src/companion/domain/service-recommendations.ts');
  const { buildRecommendationRuntimeContext } = await vite.ssrLoadModule('/src/companion/domain/recommendation-runtime-context.ts');
  const { buildRuntimeSets } = await vite.ssrLoadModule('/src/companion/domain/cookers.ts');
  const { buildCustomFoodCandidates } = await vite.ssrLoadModule('/src/companion/domain/custom-recipes.ts');
  const { buildOrderRecommendationPresentation } = await vite.ssrLoadModule('/src/companion/domain/order-recommendation-presentation.ts');
  const { resolveExactSpecialBusinessCustomer } = await vite.ssrLoadModule('/src/companion/domain/special-business/customer-profile.ts');
  const messages = [];
  const previousSelf = globalThis.self;
  globalThis.self = { postMessage: (response) => messages.push(response), onmessage: null };
  await vite.ssrLoadModule('/src/companion/workers/order-recommendations.worker.ts');
  const actualWorker = globalThis.self;
  globalThis.self = previousSelf;

  const ingredient = { id: 11, name: '猪肉', description: '', type: '', tags: ['肉'], dlc: 0, level: 1, price: 20, from: {} };
  const recipe = { id: 101, recipeId: 201, name: '测试烤肉', description: '', ingredients: ['猪肉'], positiveTags: ['烧烤', '肉'], negativeTags: [], cooker: '烧烤架', baseCookTime: 1, dlc: 0, level: 2, price: 120, from: {} };
  const beverage = { id: 21, name: '测试果酒', description: '', tags: ['水果', '低酒精'], dlc: 0, level: 2, price: 80, from: {} };
  const customer = { id: 0, name: '测试稀客', description: '', dlc: 0, places: ['妖怪兽道'], price: [], enduranceLimit: 0, positiveTags: ['烧烤', '肉'], negativeTags: [], beverageTags: ['水果', '低酒精'], collection: false, evaluation: {}, spellCards: { positive: [], negative: [] } };
  const data = { recipes: [recipe], ingredients: [ingredient], beverages: [beverage], normalCustomers: [], rareCustomers: [customer], rareCustomerProfiles: [{ id: 0, name: customer.name, positiveTags: customer.positiveTags, negativeTags: [], beverageTags: customer.beverageTags }], foodTagIdMap: { 1: '烧烤' }, beverageTagIdMap: { 2: '水果' }, tagPriorityRules: [], source: 'runtime', status: 'test' };
  const order = { traceId: 'R-WORKER', orderLifecycleSequence: 1, deskCode: 1, guestId: 0, runtimeGuestId: 3003, guestName: customer.name, specialBusinessRole: '', automationAllowed: true, foodTagId: 1, foodTag: '烧烤', beverageTagId: 2, beverageTag: '水果', source: 'test', hasServedFood: false, hasServedBeverage: false, fund: 1000, willPayMoney: true };
  const runtime = {
    availableRecipeIds: [101], availableIngredientIds: [11], availableBeverageIds: [21], ownedIngredientQty: { 11: 10 }, ownedBeverageQty: { 21: 10 },
    placedCookerTypeIds: [2], placedCookers: [{ controllerIndex: 0, gridPosition: { x: 0, y: 0, z: 0 }, controllerIdentity: '0x2000', typeIds: [2], typeNames: ['烧烤架'], name: '烧烤架', challengeLocked: false, couldOpen: true, automationAvailable: true, automationAvailability: 'StrictIdle', automationAvailabilityDiagnostic: 'test', source: 'test' }],
    placedCookerSnapshotComplete: true, placedCookerControllerCount: 1, placedCookerEmptyControllerCount: 0, placedCookerLockedControllerCount: 0, placedCookerReadFailureCount: 0, placedCookerStatus: 'test', popularFoodTag: null, popularHateFoodTag: null, famousShopEnabled: false,
  };
  const preferences = normalizeCompanionPreferences({ recommendationBudgetPolicy: 'block', filterMissingCookers: true });
  const payload = { orders: [order], runtime, favorites: { version: 1, recipes: [], beverages: [] }, customRecipes: { version: 1, enabled: true, recipes: [] }, preferences, specialBusiness: null, specialBusinessRejectedRecipeKeys: [], usage: 'display', data };
  const input = (sourceSignature, overrides = {}, contextSignature = 'session-1') => ({ payload: { ...payload, ...overrides }, sourceSignature, contextSignature });

  class Transport {
    requests = [];
    terminated = false;
    throwNext = false;
    onmessage = null;
    onerror = null;
    onmessageerror = null;
    postMessage(request) {
      if (this.throwNext) { this.throwNext = false; throw new Error('post failed'); }
      this.requests.push(structuredClone(request));
    }
    terminate() { this.terminated = true; }
    respond(response) { this.onmessage?.({ data: response }); }
    complete() {
      actualWorker.onmessage({ data: this.requests.at(-1) });
      const response = messages.at(-1);
      this.respond(response);
      return response;
    }
  }
  const transports = [];
  let failConstructor = false;
  const controller = new OrderRecommendationController(() => {
    if (failConstructor) { failConstructor = false; throw new Error('constructor failed'); }
    const worker = new Transport(); transports.push(worker); return worker;
  });

  controller.update(input('first'));
  let worker = transports.at(-1);
  assert(worker.requests[0].payload.data);
  assert.equal(worker.complete().ok, true);
  assert.equal(controller.getSnapshot().recommendations[0].budget.remainingBudget, 1000);

  controller.update(input('fund', { orders: [{ ...order, fund: 900 }] }));
  assert.equal(worker.requests.at(-1).payload.data, undefined);
  worker.complete();
  assert.equal(controller.getSnapshot().recommendations[0].budget.remainingBudget, 900, 'Identical choices must still publish the new budget.');
  controller.update(input('permission', { orders: [{ ...order, automationAllowed: false, automationBlockReason: 'blocked', orderLifecycleSequence: 2 }] }));
  worker.complete();
  assert.equal(controller.getSnapshot().recommendations[0].order.automationAllowed, false);
  assert.equal(controller.getSnapshot().recommendations[0].order.automationBlockReason, 'blocked');
  assert.equal(controller.getSnapshot().recommendations[0].order.orderLifecycleSequence, 2);
  assert.equal(controller.getSnapshot().sourceSignature, 'permission');
  assert.equal(controller.getSnapshot().isCurrent, true);

  const dataB = { ...data, recipes: [{ ...recipe, price: 130 }] };
  controller.update(input('catalog-B', { data: dataB }));
  controller.update(input('discard-queued', { orders: [{ ...order, fund: 800 }] }));
  controller.update(input('catalog-A', { data }));
  const bRequest = worker.requests.at(-1);
  assert.equal(bRequest.sourceSignature, 'catalog-B');
  worker.complete();
  assert.equal(controller.getSnapshot().isCurrent, false);
  assert.equal(worker.requests.at(-1).sourceSignature, 'catalog-A');
  assert(worker.requests.at(-1).payload.data, 'A queued return to catalog A must include it after B replaced the transport cache.');
  worker.complete();
  assert.equal(controller.getSnapshot().sourceSignature, 'catalog-A');
  assert.equal(controller.getSnapshot().isCurrent, true);
  assert.equal(controller.getSnapshot().recommendations[0].executionPlans[0].estimatedPrice, 200);

  controller.update(input('cache-miss'));
  let request = worker.requests.at(-1);
  worker.respond({ requestId: request.requestId, ok: false, code: 'data-cache-miss', error: 'Localized message may change' });
  request = worker.requests.at(-1);
  assert(request.payload.data);
  worker.complete();
  assert.equal(controller.getSnapshot().isCurrent, true);
  controller.update(input('cache-miss-bounded'));
  request = worker.requests.at(-1);
  worker.respond({ requestId: request.requestId, ok: false, code: 'data-cache-miss', error: 'miss' });
  request = worker.requests.at(-1);
  const requestCount = worker.requests.length;
  worker.respond({ requestId: request.requestId, ok: false, code: 'data-cache-miss', error: 'still missing after full data' });
  assert.equal(worker.requests.length, requestCount, 'A full-data cache failure must not loop indefinitely.');
  assert.equal(controller.getSnapshot().pending, false);
  assert.equal(controller.getSnapshot().isCurrent, false);
  assert.equal(controller.getSnapshot().retainedAfterError, true);

  controller.retry();
  worker = transports.at(-1);
  worker.complete();
  controller.update(input('post-active'));
  controller.update(input('post-queued'));
  worker.throwNext = true;
  worker.complete();
  assert.equal(controller.getSnapshot().error, 'post failed');
  assert.equal(controller.getSnapshot().pending, false);
  assert.equal(controller.getSnapshot().isCurrent, false);
  assert.equal(controller.getSnapshot().retainedAfterError, true);

  controller.retry();
  worker = transports.at(-1);
  const lateCallback = worker.onmessage;
  request = worker.requests.at(-1);
  worker.onerror({ message: 'fatal failure' });
  assert(worker.terminated);
  assert.equal(controller.getSnapshot().isCurrent, false);
  assert.equal(controller.getSnapshot().pending, false);
  failConstructor = true;
  controller.retry();
  assert.match(controller.getSnapshot().error, /constructor failed/);
  controller.retry();
  worker = transports.at(-1);
  assert(worker.requests[0].payload.data);
  lateCallback({ data: { requestId: request.requestId, ok: true, result: { recommendations: [], recommendationIssues: [], normalOrderDetailPlans: [], normalExecutionTargets: [] } } });
  assert.equal(controller.getSnapshot().pending, true, 'A dead transport response cannot complete the new request.');
  worker.complete();
  assert.equal(controller.getSnapshot().isCurrent, true);
  controller.update(input('new-context', {}, 'session-2'));
  request = worker.requests.at(-1);
  worker.respond({ requestId: request.requestId, ok: false, code: 'calculation-failed', error: 'new context failed' });
  assert.equal(controller.getSnapshot().retainedAfterError, false);
  assert.deepEqual(controller.getSnapshot().recommendations, []);
  controller.update(null);
  assert(worker.terminated);
  assert.equal(controller.getSnapshot().pending, false);
  assert.deepEqual(controller.getSnapshot().recommendations, []);
  controller.dispose();

  failConstructor = true;
  controller.update(input('cold-start-failure', {}, 'cold-session'));
  const coldState = controller.getSnapshot();
  const coldPresentation = buildOrderRecommendationPresentation({
    orders: payload.orders,
    ...coldState,
    currentContextSignature: 'cold-session',
  });
  assert.equal(coldState.pending, false);
  assert.equal(coldState.isCurrent, false);
  assert.equal(coldPresentation.updating, false, 'A settled cold-start error is not an ongoing calculation.');
  assert.match(coldPresentation.recommendationIssues[0].message, /constructor failed/);
  controller.update(null);

  const delimiterDataA = {
    ...data,
    recipes: [{ ...recipe, ingredients: ['猪肉,猪肉'] }],
    ingredients: [ingredient, { ...ingredient, id: 12, name: '猪肉,猪肉' }],
  };
  const delimiterDataB = { ...delimiterDataA, recipes: [{ ...recipe, ingredients: ['猪肉', '猪肉'] }] };
  assert.notEqual(buildRecommendationDataSignature(delimiterDataA), buildRecommendationDataSignature(delimiterDataB),
    'A single ingredient name containing a comma must not collide with two ingredient slots.');
  assert.notEqual(
    buildRecommendationDataSignature({ ...data, recipes: [{ ...recipe, positiveTags: ['甲,乙'] }] }),
    buildRecommendationDataSignature({ ...data, recipes: [{ ...recipe, positiveTags: ['甲', '乙'] }] }),
    'Tag boundaries must survive serialization.');
  assert.notEqual(
    buildRecommendationDataSignature({ ...data, recipes: [{ ...recipe, name: '甲:乙', cooker: '丙' }] }),
    buildRecommendationDataSignature({ ...data, recipes: [{ ...recipe, name: '甲', cooker: '乙:丙' }] }),
    'Catalog field boundaries must survive serialization.');
  const delimiterRuntime = { ...runtime, availableIngredientIds: [11, 12], ownedIngredientQty: { 11: 2, 12: 1 } };
  controller.update(input('delimiter-A', { data: delimiterDataA, runtime: delimiterRuntime }));
  worker = transports.at(-1);
  worker.complete();
  assert.deepEqual(controller.getSnapshot().recommendations[0].executionPlans[0].food.recipe.ingredients, ['猪肉,猪肉']);
  controller.update(input('delimiter-B', { data: delimiterDataB, runtime: delimiterRuntime }));
  assert(worker.requests.at(-1).payload.data, 'A changed catalog must be transferred even when joined text is identical.');
  worker.complete();
  assert.deepEqual(controller.getSnapshot().recommendations[0].executionPlans[0].food.recipe.ingredients, ['猪肉', '猪肉'],
    'Worker and candidate caches must return the newly transferred ingredient slots.');
  controller.dispose();

  const rawResponse = (request) => { actualWorker.onmessage({ data: request }); return messages.at(-1); };
  assert.equal(rawResponse({ requestId: 100, sourceSignature: '', contextSignature: '', payload: { ...payload, data: undefined, dataSignature: 'missing' } }).code, 'data-cache-miss');
  const malformed = rawResponse({ requestId: 101, sourceSignature: '', contextSignature: '', payload: { ...payload, orders: null, dataSignature: buildRecommendationDataSignature(data) } });
  assert.equal(malformed.code, 'calculation-failed');

  const caches = createRecommendationCacheStore();
  const build = (overrides = {}) => {
    const p = { ...payload, ...overrides };
    return buildOrderRecommendations(p.orders, p.runtime, buildRareCustomerMap(p.data), caches, p.favorites, p.customRecipes, p.preferences, p.specialBusiness, [], p.data);
  };
  assert.equal(build().recommendations[0].executionPlans[0].estimatedPrice, 200);
  assert.equal(build({ data: dataB }).recommendations[0].executionPlans[0].estimatedPrice, 210, 'The cache owner must invalidate even outside a Worker.');
  assert.notEqual(buildRecommendationDataSignature(data), buildRecommendationDataSignature({ ...data, foodTagIdMap: { 1: '肉' } }));
  assert.notEqual(buildRecommendationDataSignature(data), buildRecommendationDataSignature({ ...data, beverageTagIdMap: { 2: '低酒精' } }));
  assert.equal(build({ orders: [{ ...order, guestId: null }] }).recommendations.length, 0, 'Matching display names cannot repair a missing guest identity.');
  assert.equal(build({ orders: [{ ...order, guestId: 999 }] }).recommendations.length, 0);
  assert.equal(build({ data: { ...data, rareCustomers: [customer, { ...customer }] } }).recommendations.length, 0, 'Duplicate IDs must not select the last catalog item.');
  assert.equal(resolveExactSpecialBusinessCustomer({ ...data, rareCustomerProfiles: [...data.rareCustomerProfiles, ...data.rareCustomerProfiles] }, 0), null);
  assert.equal(build({ orders: [{ ...order, fund: null, willPayMoney: null }] }).recommendations[0].budget, null, 'Missing order budget stays unknown.');

  const repeatedData = { ...data, recipes: [{ ...recipe, ingredients: ['猪肉', '猪肉'] }] };
  const customRecipes = { version: 1, enabled: true, recipes: [{ id: 'double-base', customerId: 0, customerName: customer.name, foodTag: '烧烤', foodId: 101, recipeId: 201, recipeName: recipe.name, extraIngredientIds: [], enabled: true, pinToTop: true, sortOrder: 0 }] };
  for (const [quantity, expected] of [[1, 0], [2, 1], [-1, 1], [0, 0], [-2, 0]]) {
    const repeatedRuntime = { ...runtime, ownedIngredientQty: { 11: quantity } };
    const result = build({ data: repeatedData, runtime: repeatedRuntime }).recommendations[0];
    assert.equal(result.executionPlans.length, expected, `Repeated base slots with stock ${quantity}`);
    if (!expected) {
      assert.equal(result.blockedDiagnostic.code, 'food-base-ingredient-missing');
      assert.deepEqual(result.blockedDiagnostic.missingIngredientNames, ['猪肉']);
    }
    const context = buildRecommendationRuntimeContext(repeatedRuntime, buildRuntimeSets(repeatedRuntime, repeatedData), preferences, repeatedData);
    const candidates = buildCustomFoodCandidates({ customRecipes, data: repeatedData, customer, requiredFoodTag: '烧烤', requiredBeverageTag: '水果', context });
    assert.equal(candidates.length, expected, `Custom recipe must use the same quantity rule for stock ${quantity}`);
  }
  console.log('PASS: complete Worker facts, latest-only queue, catalog A/B/A, typed recovery, fatal/constructor/post failures, stale response isolation, cache ownership, exact customer IDs, and ingredient quantities.');
} finally { await vite.close(); }
