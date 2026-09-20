import assert from 'node:assert/strict';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { createServer, transformWithOxc } from 'vite';
import react from '@vitejs/plugin-react';
import { chromium } from 'playwright';

// Exercise the production Hook and editor with real React/Chromium, independently of navigation.
const fixture = `
import React, { useLayoutEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import { MantineProvider } from '@mantine/core';
import '@mantine/core/styles.css';
import { useCustomRecipes } from '@/companion/hooks/useCustomRecipes';
import { ModCustomRecipesPanel } from '@/companion/pages/ModCustomRecipesPanel';
import { createEmptyCustomRecipeForm } from '@/companion/custom-recipe-editor';
import { DEFAULT_RECOMMENDATION_DATA } from '@/lib/recommendation-data';
const data = {
  ...DEFAULT_RECOMMENDATION_DATA, source: 'runtime', status: 'ready',
  recipes: [201, 202].map(id => ({ id, recipeId: id + 1000, name: '料理' + id, description: '', ingredients: ['材料'],
    positiveTags: ['肉'], negativeTags: [], cooker: '锅', baseCookTime: 1, dlc: 0, level: 0, price: 1, from: {} })),
  ingredients: [{ id: 1, name: '材料', description: '', type: '', tags: ['肉'], dlc: 0, level: 0, price: 1, from: {} }],
  rareCustomers: [{ id: 1001, name: '露米娅', description: '', dlc: 0, places: ['妖怪兽道'], price: [0, 100],
    enduranceLimit: 1, positiveTags: ['肉'], negativeTags: [], beverageTags: ['低酒精'], collection: false,
    evaluation: {}, spellCards: { positive: [], negative: [] } }],
};
const runtimeSets = {
  recipeIds: new Set([201, 202]), ingredientIds: new Set([1]), beverageIds: new Set(), unavailableIngredientIds: new Set(),
  ownedIngredientQty: {1: 99}, ownedBeverageQty: {}, placedCookerTypeIds: new Set(), placedCookerNames: new Set(['锅']),
  usableCookerNames: new Set(['锅']), runtimeUnavailableCookerNames: new Set(), hasCookerSnapshot: true,
};
function Harness() {
  const [identity, setIdentity] = useState({ apiToken: 'token-a', connected: true, connectionRevision: 1, normalizedEndpoint: 'http://127.0.0.1:39001' });
  const [form, setForm] = useState(createEmptyCustomRecipeForm);
  const [visible, setVisible] = useState(true);
  const [groupMode, setGroupMode] = useState('customer');
  const controller = useCustomRecipes(identity);
  useLayoutEffect(() => {
    window.customRecipeTest = { identity, setIdentity, form, setForm, visible, setVisible, ...controller };
  });
  return <MantineProvider>{visible && <ModCustomRecipesPanel
    customRecipes={controller.customRecipes} customRecipeAvailability={controller.customRecipeAvailability}
    customRecipeBusyKey={controller.customRecipeBusyKey} customRecipeError={controller.customRecipeError}
    onRefreshCustomRecipes={controller.refreshCustomRecipes} form={form} groupMode={groupMode} runtimeSets={runtimeSets} data={data}
    onUpsertCustomRecipe={controller.upsertCustomRecipeEntry} onRemoveCustomRecipe={controller.removeCustomRecipeEntry}
    onSetCustomRecipesEnabled={controller.setCustomRecipesEnabledState} onUpdateCustomRecipeFlags={controller.updateCustomRecipeFlagsState}
    onMoveCustomRecipe={controller.moveCustomRecipeEntry} onFormChange={setForm} onGroupModeChange={setGroupMode}
  />}</MantineProvider>;
}
createRoot(document.getElementById('root')).render(<React.StrictMode><Harness /></React.StrictMode>);
`;

const server = await createServer({
  configFile: false, root: process.cwd(), logLevel: 'error',
  cacheDir: path.join(tmpdir(), 'mystia-custom-recipe-lifecycle-vite'),
  resolve: { alias: { '@': path.resolve('apps/companion/src') }, dedupe: ['react', 'react-dom'] },
  optimizeDeps: { include: ['react', 'react-dom/client', '@mantine/core', '@mantine/hooks', '@tabler/icons-react'] },
  server: { host: '127.0.0.1', port: 0, hmr: false, watch: null },
  plugins: [react(), {
    name: 'custom-recipe-lifecycle-fixture',
    resolveId(id) { if (id === '/custom-recipe-lifecycle.jsx') return '\0custom-recipe-lifecycle.jsx'; },
    async load(id) {
      if (id === '\0custom-recipe-lifecycle.jsx') return (await transformWithOxc(fixture, 'custom-recipe-lifecycle.jsx')).code;
    },
    configureServer(vite) {
      vite.middlewares.use(async (request, response, next) => {
        if (request.url !== '/') return next();
        const html = await vite.transformIndexHtml('/', '<html><body><div id="root"></div><script type="module" src="/custom-recipe-lifecycle.jsx"></script></body></html>');
        response.setHeader('content-type', 'text/html');
        response.end(html);
      });
    },
  }],
});
await server.listen();
const browser = await chromium.launch({ headless: true,
  ...(process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH } : {}),
});
const page = await browser.newPage({ viewport: { width: 640, height: 900 } });
page.setDefaultTimeout(12000);
const errors = [];
page.on('pageerror', (error) => errors.push(String(error)));
const headers = { 'access-control-allow-origin': '*', 'access-control-allow-headers': '*', 'access-control-allow-methods': 'GET,POST,OPTIONS' };
const entry = { id: 'recipe-one', customerId: 1001, customerName: '露米娅', foodTag: null, foodId: 201, recipeId: 1201,
  recipeName: '料理201', extraIngredientIds: [], enabled: true, pinToTop: true, sortOrder: 1,
  createdAtUtc: '2026-09-21T00:00:00Z', updatedAtUtc: '2026-09-21T00:00:00Z' };
let currentData = { version: 1, enabled: true, recipes: [entry] };
let readCount = 0;
let writeCount = 0;
let holdRead = false;
let failRead = true;
let heldRead;
let heldWrite;
let loseWrite = false;

try {
  await page.route(/^http:\/\/127\.0\.0\.1:3900[12]\//, async (route) => {
    const request = route.request();
    if (request.method() === 'OPTIONS') return route.fulfill({ status: 204, headers });
    if (request.method() === 'GET') {
      readCount++;
      if (failRead) {
        failRead = false;
        holdRead = true;
        return route.fulfill({ status: 503, headers, json: { error: '受控读取失败' } });
      }
      if (holdRead) { holdRead = false; heldRead = route; return; }
      return route.fulfill({ status: 200, headers, json: currentData });
    }
    writeCount++;
    if (loseWrite) {
      loseWrite = false;
      currentData = { ...currentData, enabled: false };
      holdRead = true;
      return route.fulfill({ status: 502, headers, json: { error: '受控修改响应丢失' } });
    }
    heldWrite = route;
  });
  await page.goto('http://127.0.0.1:' + server.httpServer.address().port);
  await waitFor(() => heldRead, 'Initial read failure did not retry.');
  assert.equal(await capability('canWrite'), false);
  assert.equal(await page.evaluate(() => window.customRecipeTest.setCustomRecipesEnabledState(false)), false);
  assert.equal(writeCount, 0);
  await releaseRead(currentData);
  await ready();

  // Each connection component independently invalidates stale POST results; no new write may overlap.
  for (const change of [
    { normalizedEndpoint: 'http://127.0.0.1:39002' },
    { apiToken: 'token-b' },
    { connectionRevision: 2 },
  ]) {
    await beginWrite();
    const count = writeCount;
    await page.evaluate((next) => {
      const test = window.customRecipeTest;
      test.setIdentity({ ...test.identity, ...next });
    }, change);
    await page.waitForFunction(() => !window.customRecipeTest.customRecipeAvailability.current);
    assert.equal(await page.evaluate(() => window.customRecipeTest.setCustomRecipesEnabledState(false)), false);
    assert.equal(writeCount, count);
    await releaseWrite({ ...currentData, enabled: false });
    await ready();
    assert.equal(await page.evaluate(() => window.customRecipeTest.customRecipes.enabled), true, 'Old POST overwrote the new connection.');
  }

  // Pausing blocks every mutation, and pausing/reconnecting cannot release an in-flight transport.
  await beginWrite();
  await setConnected(false);
  const writesBeforePause = writeCount;
  assert.deepEqual(await page.evaluate(async () => {
    const test = window.customRecipeTest;
    return Promise.all([
      test.setCustomRecipesEnabledState(false), test.removeCustomRecipeEntry('recipe-one'),
      test.moveCustomRecipeEntry('recipe-one', 'down'), test.updateCustomRecipeFlagsState({ selection: { scope: 'all' }, enabled: false }),
      test.upsertCustomRecipeEntry({ customerId: 1001, customerName: '露米娅', foodTag: null, foodId: 201, recipeId: 1201,
        recipeName: '料理201', extraIngredientIds: [] }),
    ]);
  }), [false, false, false, false, false]);
  assert.equal(writeCount, writesBeforePause);
  await setConnected(true);
  assert.equal(await capability('canWrite'), false);
  await releaseWrite({ ...currentData, enabled: false });
  await ready();
  assert.equal(await page.evaluate(() => window.customRecipeTest.customRecipes.enabled), true);

  // Server executed a write but its response was lost: only GET may confirm, never replay POST.
  loseWrite = true;
  await page.evaluate(() => { void window.customRecipeTest.setCustomRecipesEnabledState(false); });
  await waitFor(() => heldRead, 'An uncertain write did not start verification.');
  const writesAfterLoss = writeCount;
  assert.equal(await capability('canWrite'), false);
  assert.equal(await page.getByRole('switch', { name: '启用自定义推荐料理', exact: true }).isDisabled(), true);
  await releaseRead({ version: 1, enabled: false }); // A partial collection cannot grant write capability.
  await page.waitForFunction(() => !window.customRecipeTest.customRecipeRefreshing);
  assert.equal(await capability('canWrite'), false);
  await ready();
  assert.equal(await page.evaluate(() => window.customRecipeTest.customRecipes.enabled), false);
  assert.equal(writeCount, writesAfterLoss);

  // Only the known v1 collection and a boolean success envelope can confirm a mutation.
  for (const invalid of [
    { ok: 'true', data: currentData },
    { ok: 1, data: currentData },
    { ok: true, data: { ...currentData, version: 2 } },
  ]) {
    await page.evaluate(() => {
      window.customRecipeMutationResult = undefined;
      void window.customRecipeTest.setCustomRecipesEnabledState(true).then((result) => {
        window.customRecipeMutationResult = result;
      });
    });
    await waitFor(() => heldWrite, 'Malformed-response mutation did not start.');
    holdRead = true;
    const writesBeforeVerification = writeCount;
    await releaseWrite(invalid.data, invalid.ok);
    await waitFor(() => heldRead, 'Malformed response did not require a verification GET.');
    assert.equal(await page.evaluate(() => window.customRecipeMutationResult), false);
    assert.equal(await capability('canWrite'), false);
    await releaseRead(currentData);
    await ready();
    assert.equal(writeCount, writesBeforeVerification, 'Malformed response must never cause a replayed POST.');
  }

  holdRead = true;
  await page.evaluate(() => { void window.customRecipeTest.refreshCustomRecipes(); });
  await waitFor(() => heldRead, 'Version-validation refresh did not start.');
  await releaseRead({ ...currentData, version: 2 });
  await page.waitForFunction(() => !window.customRecipeTest.customRecipeRefreshing);
  assert.equal(await capability('canWrite'), false, 'A future GET schema must not confirm current data.');
  await ready();

  // A late save must not reset a newer draft, even while still mounted on the same connection.
  await editOriginal();
  await page.getByRole('button', { name: '保存配方', exact: true }).click();
  await waitFor(() => heldWrite, 'Save request missing.');
  await replaceDraft();
  await releaseWrite(currentData);
  await ready();
  assert.equal(await page.evaluate(() => window.customRecipeTest.form.foodId), '202');

  // An old editor completion after unmount/remount must not reset the shared draft either.
  await editOriginal();
  await page.getByRole('button', { name: '保存配方', exact: true }).click();
  await waitFor(() => heldWrite, 'Unmount save request missing.');
  await page.evaluate(() => window.customRecipeTest.setVisible(false));
  await page.waitForFunction(() => !window.customRecipeTest.visible);
  await replaceDraft();
  await page.evaluate(() => window.customRecipeTest.setVisible(true));
  await page.getByRole('button', { name: '新增配方', exact: true }).waitFor();
  await releaseWrite(currentData);
  await ready();
  assert.equal(await page.evaluate(() => window.customRecipeTest.form.foodId), '202');

  // Deletion has the same asynchronous draft ownership requirement as saving.
  await editOriginal();
  await page.locator('[data-gamepad-focus-key="custom-recipe:recipe-one:remove"]').click();
  await waitFor(() => heldWrite, 'Delete request missing.');
  await replaceDraft();
  await releaseWrite({ ...currentData, recipes: [] });
  await ready();
  assert.equal(await page.evaluate(() => window.customRecipeTest.form.foodId), '202');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: true, readCount, writeCount, pageErrors: errors,
    cases: ['read-retry', 'endpoint-token-revision', 'single-write', 'pause-reconnect', 'uncertain-write-get-only',
      'partial-collection', 'strict-success-envelope', 'known-schema-only', 'newer-draft', 'unmounted-editor', 'delete-draft'] }, null, 2));
} catch (error) {
  console.error({ pageErrors: errors, readCount, writeCount,
    state: await page.evaluate(() => ({ availability: window.customRecipeTest?.customRecipeAvailability,
      error: window.customRecipeTest?.customRecipeError, body: document.body.innerText.slice(0, 1000) })) });
  throw error;
} finally {
  await page.close();
  await browser.close();
  await server.close();
}

async function waitFor(predicate, message) {
  const deadline = Date.now() + 12000;
  while (!predicate()) {
    if (Date.now() > deadline) throw new Error(message);
    await new Promise((resolve) => setTimeout(resolve, 15));
  }
}
async function capability(name) { return page.evaluate((key) => window.customRecipeTest.customRecipeAvailability[key], name); }
async function ready() { await page.waitForFunction(() => window.customRecipeTest?.customRecipeAvailability.canWrite); }
async function releaseRead(data) {
  const route = heldRead; heldRead = undefined;
  await route.fulfill({ status: 200, headers, json: data });
}
async function releaseWrite(data, ok = true) {
  const route = heldWrite; heldWrite = undefined;
  await route.fulfill({ status: 200, headers, json: { ok, customRecipes: data, error: null } });
}
async function beginWrite() {
  await page.evaluate(() => { void window.customRecipeTest.setCustomRecipesEnabledState(false); });
  await waitFor(() => heldWrite, 'Mutation did not start.');
}
async function setConnected(connected) {
  await page.evaluate((value) => {
    const test = window.customRecipeTest;
    test.setIdentity({ ...test.identity, connected: value });
  }, connected);
  await page.waitForFunction((value) => window.customRecipeTest.identity.connected === value, connected);
}
async function editOriginal() {
  await page.locator('[data-gamepad-focus-key="custom-recipe:recipe-one:edit"]').click();
  await page.getByRole('button', { name: '保存配方', exact: true }).waitFor();
}
async function replaceDraft() {
  await page.evaluate(() => window.customRecipeTest.setForm({
    editingId: '', customerId: '1001', foodTagValue: '__all_food_tags__', foodId: '202',
    extraIngredientIds: [], enabled: false, pinToTop: false,
  }));
  await page.waitForFunction(() => window.customRecipeTest.form.foodId === '202');
}
