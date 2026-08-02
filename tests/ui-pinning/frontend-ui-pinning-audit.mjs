import { chromium } from 'playwright';
import { readFileSync } from 'node:fs';

const APP_URL = process.env.MYSTIA_APP_URL || 'http://127.0.0.1:4173/';
const API_URL = process.env.MYSTIA_API_URL || 'http://127.0.0.1:32145';
const API_TOKEN = process.env.MYSTIA_API_TOKEN || 'mock-token';
const STORAGE_PREFIX = 'mystia-steward-companion';
const TARGET_PATH = '/ui-pinning/target';

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 900, height: 760 } });
const targetRequests = [];
const completedTargetRequests = [];
let activeTargetRequests = 0;
let maxActiveTargetRequests = 0;
let delayNextTargetMs = 0;
let delayedTargetStartedAt = 0;
let rejectedTarget = false;
let abortNextSnapshot = false;
let snapshotAbortedAt = 0;
let recoveredSnapshotAt = 0;
let mutateSnapshots = false;
let mutatedSnapshotAt = 0;
let mutatedSnapshot = null;
let mutatedSnapshotServeCount = 0;
let deferredSecondOrder = null;

const preferencesSource = readFileSync('apps/companion/src/companion/preferences.ts', 'utf8');
assert(
  /recommendedExtraIngredientFillEnabled:\s*readStoredBoolean\([^,]+,\s*false\)/.test(preferencesSource),
  '推荐料理自动加料必须默认关闭',
);
assert(
  /seatHighlightEnabled:\s*readStoredBoolean\([^,]+,\s*false\)/.test(preferencesSource),
  '目标桌位高亮必须默认关闭',
);
assert(
  /orderHighlightEnabled:\s*readStoredBoolean\([^,]+,\s*false\)/.test(preferencesSource),
  '目标订单高亮必须默认关闭',
);

try {
  await page.route(`${API_URL}/**`, async (route) => {
    const request = route.request();
    const url = new URL(request.url());

    if (url.pathname === TARGET_PATH) {
      const entry = {
        method: request.method(),
        at: Date.now(),
        params: Object.fromEntries(url.searchParams.entries()),
      };
      targetRequests.push(entry);
      activeTargetRequests += 1;
      maxActiveTargetRequests = Math.max(maxActiveTargetRequests, activeTargetRequests);
      try {
        if (!rejectedTarget && Number(entry.params.recipeId) >= 0) {
          rejectedTarget = true;
          await fulfillJson(route, { ok: false, error: 'mock target rejection' });
          return;
        }
        if (delayNextTargetMs > 0) {
          const delayMs = delayNextTargetMs;
          delayNextTargetMs = 0;
          delayedTargetStartedAt = Date.now();
          await new Promise((resolve) => setTimeout(resolve, delayMs));
        }
        await route.continue();
        entry.completedAt = Date.now();
        completedTargetRequests.push(entry);
      } finally {
        activeTargetRequests -= 1;
      }
      return;
    }

    if (url.pathname === '/snapshot' && abortNextSnapshot) {
      abortNextSnapshot = false;
      snapshotAbortedAt = Date.now();
      await route.abort('connectionrefused');
      return;
    }

    if (url.pathname === '/snapshot' && mutateSnapshots) {
      if (mutatedSnapshot && url.searchParams.get('knownSignature') === mutatedSnapshot.snapshotSignature) {
        await fulfillJson(route, {
          unchanged: true,
          snapshotSignature: mutatedSnapshot.snapshotSignature,
        });
        return;
      }
      if (mutatedSnapshot) {
        mutatedSnapshotServeCount += 1;
        await fulfillJson(route, mutatedSnapshot);
        return;
      }
      const apiResponse = await route.fetch();
      const snapshot = await apiResponse.json();
      assert(!snapshot.unchanged && snapshot.nightBusiness?.orders?.[0], '连接身份巡检需要完整 Mock 快照');
      snapshot.snapshotSignature = `${snapshot.snapshotSignature}|ui-pinning-stale-audit`;
      mutatedSnapshot = snapshot;
      if (mutatedSnapshotAt === 0) mutatedSnapshotAt = Date.now();
      mutatedSnapshotServeCount += 1;
      await route.fulfill({ response: apiResponse, json: snapshot });
      return;
    }

    if (url.pathname === '/snapshot' && snapshotAbortedAt > 0) {
      recoveredSnapshotAt = Date.now();
    }

    await route.continue();
  });

  await page.addInitScript(seedLocalStorage, {
    apiUrl: API_URL,
    apiToken: API_TOKEN,
    storagePrefix: STORAGE_PREFIX,
  });
  await page.goto(APP_URL, { waitUntil: 'domcontentloaded' });

  await waitFor(() => targetRequests.filter(hasRecipeTarget).length >= 2, 12_000, '目标被拒绝后没有按固定退避重试');
  const recipeRequests = targetRequests.filter(hasRecipeTarget);
  const rejectedRequest = recipeRequests[0];
  const acceptedRetry = recipeRequests[1];

  assert(rejectedRequest.method === 'POST', `置顶目标应使用 POST，实际为 ${rejectedRequest.method}`);
  assert(sameTarget(rejectedRequest, acceptedRetry), '重试请求修改了待发布的目标');
  assert(acceptedRetry.at - rejectedRequest.at >= 650, '目标失败后未执行固定退避');

  const runtimeData = await readRuntimeData();
  const selectedRecipe = runtimeData.recipes.find((recipe) => recipe.name === acceptedRetry.params.recipeName);
  assert(selectedRecipe, `Mock 运行时目录中找不到料理 ${acceptedRetry.params.recipeName}`);
  assert(selectedRecipe.id !== selectedRecipe.recipeId, 'Mock foodId 与 recipeId 仍然相同，无法检测 ID 契约回归');
  assert(Number(acceptedRetry.params.recipeId) === selectedRecipe.recipeId,
    `下发的 recipeId=${acceptedRetry.params.recipeId}，期望 ${selectedRecipe.recipeId}`);
  assert(acceptedRetry.params.enabled === 'true', '定向巡检未启用游戏界面置顶');
  assert(acceptedRetry.params.extraIngredientFillEnabled === 'true', '定向巡检未启用推荐加料自动加入');
  assert(acceptedRetry.params.seatHighlightEnabled === 'true', '定向巡检未启用目标桌位高亮');
  assert(acceptedRetry.params.orderHighlightEnabled === 'true', '定向巡检未启用目标订单高亮');
  assert(acceptedRetry.params.targetRevision, '置顶目标缺少稳定订单/执行计划 revision');
  assert(acceptedRetry.params.orderTraceId === 'R-0001', '置顶目标缺少精确订单 trace');
  assert(acceptedRetry.params.ingredientIds, '置顶目标缺少材料 ID');
  assert('extraIngredientIds' in acceptedRetry.params, '置顶目标缺少独立的推荐加料字段');
  assert(Number(acceptedRetry.params.deskCode) >= 0, '置顶目标缺少有效桌位');
  assert(acceptedRetry.params.businessGeneration === '1', '置顶目标缺少当前经营 generation');
  assert(Number(acceptedRetry.params.beverageId) < 0, '已经送达的酒水仍进入了游戏界面目标');
  assert(!acceptedRetry.params.beverageName, '已经送达的酒水仍保留了目标名称');

  await page.locator('[data-gamepad-tab-value="service"]').click();
  const firstRecipeRow = page.locator('[data-gamepad-row-key*="service:order:"][data-gamepad-row-key*=":recipe:"]').first();
  const firstBeverageRow = page.locator('[data-gamepad-row-key*="service:order:"][data-gamepad-row-key*=":beverage:"]').first();
  await waitFor(async () => await firstRecipeRow.count() > 0 && await firstBeverageRow.count() > 0,
    8_000, '经营中页面未显示稀客推荐首项');
  const firstRecipeText = await firstRecipeRow.innerText();
  const firstBeverageText = await firstBeverageRow.innerText();
  assert(firstRecipeText.includes('#1') && firstRecipeText.includes(acceptedRetry.params.recipeName),
    `游戏内料理目标未对应页面料理首项：${firstRecipeText}`);
  assert(firstBeverageText.includes('#1'), `经营中页面酒水首项缺失：${firstBeverageText}`);

  const acceptedTargetCount = recipeRequests.length;
  abortNextSnapshot = true;
  await waitFor(() => snapshotAbortedAt > 0, 8_000, '未触发模拟断线');
  await new Promise((resolve) => setTimeout(resolve, 500));
  assert(
    targetRequests.filter(hasRecipeTarget).length === acceptedTargetCount,
    '快照连接尚未恢复时发布了置顶目标',
  );
  await waitFor(() => recoveredSnapshotAt > snapshotAbortedAt, 8_000, '模拟断线后未恢复快照');
  await new Promise((resolve) => setTimeout(resolve, 1200));
  assert(
    targetRequests.filter(hasRecipeTarget).length === acceptedTargetCount,
    '同一游戏会话的短暂断线清除了成功发布签名并重复 POST',
  );

  const identityTargetCount = targetRequests.filter(hasRecipeTarget).length;
  const connectionIsolationStartCount = targetRequests.length;
  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 2200;
  });
  mutateSnapshots = true;
  await page.locator('.steward-workbench-header input').first().press('Enter');
  await waitFor(
    () => targetRequests.slice(connectionIsolationStartCount).some(isEnabledClearTarget),
    2_800,
    '仅连接 revision 变化时未先发布空目标隔离旧连接目标',
  );
  await waitFor(() => mutatedSnapshotAt > 0, 5_000, '应用连接身份后未获取新快照');
  await new Promise((resolve) => setTimeout(resolve, 1000));
  assert(
    targetRequests.filter(hasRecipeTarget).length === identityTargetCount,
    '推荐 Worker 结果过期时发布了旧置顶目标',
  );
  try {
    await waitFor(
      () => targetRequests.filter(hasRecipeTarget).length > identityTargetCount,
      8_000,
      '同 endpoint/token 的新连接身份未在 Worker 结果就绪后重发目标',
    );
  } catch (error) {
    console.error(`定向巡检请求记录：${JSON.stringify(targetRequests)}`);
    console.error(`Worker 记录：${JSON.stringify(await page.evaluate(() => window.__uiPinningWorkerEvents))}`);
    console.error(`页面状态：${(await page.locator('.steward-workbench-header').innerText()).replaceAll('\n', ' | ')}`);
    throw error;
  }
  const identityRequest = targetRequests.filter(hasRecipeTarget).at(-1);
  assert(identityRequest.at - mutatedSnapshotAt >= 1900, '推荐 Worker 结果就绪前发布了目标');
  assert(
    sameTarget(acceptedRetry, identityRequest),
    `新连接身份未重发当前目标：before=${JSON.stringify(acceptedRetry.params)}, after=${JSON.stringify(identityRequest.params)}`,
  );

  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 0;
  });
  const signatureOnlyTargetCount = targetRequests.filter(hasRecipeTarget).length;
  const signatureOnlySuccessCount = await page.evaluate(() =>
    window.__uiPinningWorkerEvents.filter((event) => event.deliveredOk === true).length);
  const signatureOnlyMutationServeCount = mutatedSnapshotServeCount;
  mutateInternalTargetSignature();
  await waitFor(
    () => mutatedSnapshotServeCount > signatureOnlyMutationServeCount,
    5_000,
    '未获取仅修改内部 target.signature 的快照',
  );
  await waitFor(
    async () => (await page.evaluate(() =>
      window.__uiPinningWorkerEvents.filter((event) => event.deliveredOk === true).length)) > signatureOnlySuccessCount,
    5_000,
    '内部 target.signature 变化后推荐 Worker 未产生新成功结果',
  );
  await waitFor(
    () => targetRequests.filter(hasRecipeTarget).length > signatureOnlyTargetCount,
    5_000,
    '源订单 revision 变化后没有重新发布相同可见目标',
  );
  const signatureOnlyRequest = targetRequests.filter(hasRecipeTarget).at(-1);
  assert(
    sameVisibleTarget(identityRequest, signatureOnlyRequest),
    '源订单 revision 变化时意外修改了可见目标字段',
  );
  assert(
    identityRequest.params.targetRevision !== signatureOnlyRequest.params.targetRevision,
    '源订单 revision 变化后仍发布旧 targetRevision',
  );

  const singleOrderMutationServeCount = mutatedSnapshotServeCount;
  const singleOrderSuccessCount = await deliveredWorkerSuccessCount(page);
  retainOnlyFirstPendingOrder();
  await waitFor(
    () => mutatedSnapshotServeCount > singleOrderMutationServeCount,
    5_000,
    '未获取用于订单完成空窗巡检的单订单快照',
  );
  await waitFor(
    async () => await deliveredWorkerSuccessCount(page) > singleOrderSuccessCount,
    5_000,
    '单订单快照的推荐 Worker 未完成',
  );
  await waitFor(
    () => targetRequests.some((entry) =>
      hasRecipeTarget(entry) && Number(entry.params.beverageId) >= 0),
    5_000,
    '单订单快照未发布料理与酒水均待处理的 A 目标',
  );
  const activeOrderTarget = targetRequests
    .filter((entry) => hasRecipeTarget(entry) && Number(entry.params.beverageId) >= 0)
    .at(-1);
  assert(activeOrderTarget, '缺少用于订单完成空窗巡检的 A 目标');

  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 3200;
  });
  const validPendingMutationServeCount = mutatedSnapshotServeCount;
  const validPendingRequestCount = targetRequests.length;
  mutateSnapshot('mock-ui-pinning-valid-source-pending-audit');
  await waitFor(
    () => mutatedSnapshotServeCount > validPendingMutationServeCount,
    5_000,
    '未获取源订单仍有效的 pending 快照',
  );
  await waitFor(
    async () => (await page.evaluate(() => window.__uiPinningWorkerEvents.at(-1)?.delayMs)) === 3200,
    3_000,
    '源订单仍有效时推荐 Worker 未进入延迟 pending 状态',
  );
  await new Promise((resolve) => setTimeout(resolve, 450));
  assert(
    targetRequests.length === validPendingRequestCount,
    '源订单仍有效的普通 pending 错误清空或重复发布了目标',
  );

  const unrelatedOrderMutationServeCount = mutatedSnapshotServeCount;
  showDeferredSecondOrderAlongsideActive();
  await waitFor(
    () => mutatedSnapshotServeCount > unrelatedOrderMutationServeCount,
    5_000,
    '未获取无关 B 订单新增快照',
  );
  await new Promise((resolve) => setTimeout(resolve, 450));
  assert(
    targetRequests.length === validPendingRequestCount,
    '无关 B 订单新增导致 A 的有效目标被清空或重复发布',
  );
  const unrelatedOrderRemovalServeCount = mutatedSnapshotServeCount;
  hideDeferredSecondOrder();
  await waitFor(
    () => mutatedSnapshotServeCount > unrelatedOrderRemovalServeCount,
    5_000,
    '未获取无关 B 订单移除快照',
  );
  await new Promise((resolve) => setTimeout(resolve, 450));
  assert(
    targetRequests.length === validPendingRequestCount,
    '无关 B 订单移除导致 A 的有效目标被清空或重复发布',
  );

  const foodDeliveryMutationServeCount = mutatedSnapshotServeCount;
  const foodDeliveryStartCount = targetRequests.length;
  serveOnlyOrderFood();
  await waitFor(
    () => mutatedSnapshotServeCount > foodDeliveryMutationServeCount,
    5_000,
    '未获取 A 订单料理单独送达快照',
  );
  await waitFor(
    () => targetRequests.slice(foodDeliveryStartCount).some(isBeverageOnlyTarget),
    2_800,
    'A 订单料理送达后未在 Worker pending 期间保留酒水并移除料理目标',
  );
  const beverageOnlyTarget = targetRequests.slice(foodDeliveryStartCount).find(isBeverageOnlyTarget);
  assert(beverageOnlyTarget, '缺少料理送达后的酒水单组件目标');
  assert(!beverageOnlyTarget.params.ingredientIds, '料理送达后仍保留材料目标');
  assert(!beverageOnlyTarget.params.extraIngredientIds, '料理送达后仍保留自动加料目标');
  assert(Number(beverageOnlyTarget.params.cookerTypeId) < 0, '料理送达后仍保留厨具目标');
  assert(
    !targetRequests.slice(foodDeliveryStartCount).some(isEnabledClearTarget),
    '料理单独送达时错误清空了仍未送达的酒水目标',
  );

  const completionMutationServeCount = mutatedSnapshotServeCount;
  const completionStartCount = targetRequests.length;
  const completionBaseSuccessCount = await deliveredWorkerSuccessCount(page);
  const completionStartedAt = Date.now();
  completeOnlyOrder();
  await waitFor(
    () => mutatedSnapshotServeCount > completionMutationServeCount,
    5_000,
    '未获取 A 订单双送达快照',
  );
  await waitFor(
    () => targetRequests.slice(completionStartCount).some(isEnabledClearTarget),
    2_800,
    'A 订单双送达后未在 Worker pending 期间立即下发空目标',
  );
  const completionClearRequest = targetRequests
    .slice(completionStartCount)
    .find(isEnabledClearTarget);
  assert(completionClearRequest, '缺少 A 订单双送达后的空目标请求');
  assert(
    completionClearRequest.at - completionStartedAt < 2800,
    `A 订单空目标发布过慢：${completionClearRequest.at - completionStartedAt}ms`,
  );

  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 0;
  });
  await waitFor(
    async () => await deliveredWorkerSuccessCount(page) >= completionBaseSuccessCount + 2,
    8_000,
    'A 订单完成前后的延迟 Worker 响应未全部派发',
  );
  await new Promise((resolve) => setTimeout(resolve, 350));
  assert(
    !targetRequests
      .slice(targetRequests.indexOf(completionClearRequest) + 1)
      .some(hasRecipeTarget),
    '迟到 Worker 响应重新发布了已经双送达的 A 目标',
  );

  const secondOrderMutationServeCount = mutatedSnapshotServeCount;
  const secondOrderStartCount = targetRequests.length;
  injectDeferredSecondOrder();
  await waitFor(
    () => mutatedSnapshotServeCount > secondOrderMutationServeCount,
    5_000,
    '未获取后续 B 订单快照',
  );
  await waitFor(
    () => targetRequests.slice(secondOrderStartCount).some(hasRecipeTarget),
    5_000,
    'B 订单出现后未发布新的游戏界面目标',
  );
  const secondOrderTarget = targetRequests.slice(secondOrderStartCount).find(hasRecipeTarget);
  assert(secondOrderTarget, '缺少 B 订单出现后的目标请求');
  assert(secondOrderTarget.params.orderTraceId === 'R-0002', 'B 订单未携带自身的精确订单 trace');
  assert(
    !sameTarget(activeOrderTarget, secondOrderTarget),
    'B 订单出现后仍发布了 A 订单的旧目标',
  );

  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 3200;
  });
  const pendingMutationServeCount = mutatedSnapshotServeCount;
  const pendingStartCount = targetRequests.length;
  mutateSnapshot('mock-ui-pinning-pending-audit');
  await waitFor(
    () => mutatedSnapshotServeCount > pendingMutationServeCount,
    5_000,
    '未获取用于 pending 开关巡检的新快照',
  );
  await waitFor(
    async () => (await page.evaluate(() => window.__uiPinningWorkerEvents.at(-1)?.delayMs)) === 3200,
    3_000,
    '推荐 Worker 未进入延迟 pending 状态',
  );
  await new Promise((resolve) => setTimeout(resolve, 450));
  assert(
    targetRequests.length === pendingStartCount,
    'B 源订单仍有效的普通 pending 错误清空或重复发布了目标',
  );

  const beverageDeliveryMutationServeCount = mutatedSnapshotServeCount;
  const beverageDeliveryStartCount = targetRequests.length;
  serveOnlyOrderBeverage();
  await waitFor(
    () => mutatedSnapshotServeCount > beverageDeliveryMutationServeCount,
    5_000,
    '未获取 B 订单酒水单独送达快照',
  );
  await waitFor(
    () => targetRequests.slice(beverageDeliveryStartCount).some(isRecipeOnlyTarget),
    2_800,
    'B 订单酒水送达后未在 Worker pending 期间保留料理并移除酒水目标',
  );
  assert(
    !targetRequests.slice(beverageDeliveryStartCount).some(isEnabledClearTarget),
    '酒水单独送达时错误清空了仍未送达的料理和厨具目标',
  );

  await page.locator('[data-gamepad-tab-value="settings"]').first().click();
  await page.getByRole('tab', { name: '推荐', exact: true }).first().click();
  const pinningSwitchLabel = page.getByText('游戏界面置顶推荐（实验性）', { exact: true }).first();
  assert(await pinningSwitchLabel.count(), '未找到游戏界面置顶开关');

  delayNextTargetMs = 1600;
  const coalescingStartCount = targetRequests.length;
  await pinningSwitchLabel.click();
  await waitFor(() => delayedTargetStartedAt > 0, 3_000, '未启动不可取消的延迟目标请求');
  await pinningSwitchLabel.click();
  await new Promise((resolve) => setTimeout(resolve, 350));
  assert(targetRequests.length === coalescingStartCount + 1, '单写者忙碌时仍并发发送了新目标');
  await waitFor(
    () => targetRequests.length >= coalescingStartCount + 2,
    5_000,
    '延迟目标完成后未合并补发最新开关状态',
  );
  const coalescedRequests = targetRequests.slice(coalescingStartCount, coalescingStartCount + 2);
  assert(coalescedRequests[0].params.enabled === 'false', 'pending 期间关闭置顶未立即进入发布队列');
  assert(coalescedRequests[0].params.highlightEnabled === 'true', '关闭置顶时错误关闭了厨具高亮');
  assert(coalescedRequests[0].params.extraIngredientFillEnabled === 'false', '关闭置顶时仍允许自动加入加料');
  assert(coalescedRequests[0].params.seatHighlightEnabled === 'true', '关闭置顶时错误关闭了独立桌位高亮');
  assert(coalescedRequests[0].params.orderHighlightEnabled === 'true', '关闭置顶时错误关闭了独立订单高亮');
  assert(coalescedRequests[1].params.enabled === 'true', '延迟请求完成后未补发最新置顶开关');
  assert(coalescedRequests[1].params.extraIngredientFillEnabled === 'true', '重新开启置顶后未恢复自动加入加料');
  assert(maxActiveTargetRequests === 1, `置顶目标存在并发写入：max=${maxActiveTargetRequests}`);
  await waitFor(
    () => completedTargetRequests.includes(coalescedRequests[1]),
    3_000,
    '最新合并目标未完成写入',
  );
  await waitFor(
    async () => page.evaluate(() => {
      const events = window.__uiPinningWorkerEvents;
      const received = events.filter((event) => 'receivedAt' in event).length;
      const dispatched = events.filter((event) => 'dispatchedAt' in event).length;
      return received > 0 && received === dispatched;
    }),
    5_000,
    'pending 巡检的 Worker 响应未完成派发',
  );

  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 0;
    window.__uiPinningWorkerRejectNext = true;
    window.__uiPinningWorkerHoldSuccess = true;
  });
  const errorMutationServeCount = mutatedSnapshotServeCount;
  const errorStartCount = targetRequests.length;
  mutateSnapshot('mock-ui-pinning-error-audit');
  await waitFor(
    () => mutatedSnapshotServeCount > errorMutationServeCount,
    5_000,
    '未获取用于 Worker error 巡检的新快照',
  );
  await waitFor(
    () => targetRequests.slice(errorStartCount).some(isEnabledClearTarget),
    5_000,
    '推荐 Worker error 后未清空 Mod 旧目标',
  );
  const errorClearRequest = targetRequests.slice(errorStartCount).find(isEnabledClearTarget);
  const heldRecoveryMutationServeCount = mutatedSnapshotServeCount;
  mutateSnapshot('mock-ui-pinning-held-recovery-audit');
  await waitFor(
    () => mutatedSnapshotServeCount > heldRecoveryMutationServeCount,
    5_000,
    '未获取用于暂存 Worker error 恢复响应的新快照',
  );
  await waitFor(
    async () => page.evaluate(() => window.__uiPinningWorkerHeldResponses.length > 0),
    3_000,
    'Worker error 后没有暂存自动排队的成功响应',
  );
  const errorFlagStartCount = targetRequests.length;
  await pinningSwitchLabel.click();
  try {
    await waitFor(
      () => targetRequests.slice(errorFlagStartCount).some((entry) =>
        entry.params.enabled === 'false'
        && entry.params.highlightEnabled === 'true'
        && Number(entry.params.recipeId) < 0),
      3_000,
      'Worker error 期间关闭置顶未下发最新 flags',
    );
  } catch (error) {
    console.error(`Worker error 开关请求记录：${JSON.stringify(targetRequests.slice(errorFlagStartCount))}`);
    console.error(`置顶开关状态：${await page.getByRole('switch', { name: '游戏界面置顶推荐（实验性）' }).isChecked()}`);
    console.error(`Worker 记录：${JSON.stringify(await page.evaluate(() => window.__uiPinningWorkerEvents))}`);
    throw error;
  }

  const recoverySuccessCount = await page.evaluate(() =>
    window.__uiPinningWorkerEvents.filter((event) => event.deliveredOk === true).length);
  const recoveryStartCount = targetRequests.length;
  assert(
    !targetRequests.slice(recoveryStartCount).some(hasRecipeTarget),
    'Worker 成功响应派发前提前恢复了旧置顶目标',
  );
  await page.evaluate(() => window.__releaseUiPinningWorkerHeldResponses());
  await waitFor(
    async () => (await page.evaluate(() =>
      window.__uiPinningWorkerEvents.filter((event) => event.deliveredOk === true).length)) > recoverySuccessCount,
    3_000,
    'Worker error 恢复响应未成功派发',
  );
  await waitFor(
    () => targetRequests.slice(recoveryStartCount).some((entry) =>
      entry.params.enabled === 'false'
      && entry.params.highlightEnabled === 'true'
      && Number(entry.params.recipeId) >= 0),
    5_000,
    'Worker 成功恢复后未解除空目标锁存',
  );

  const policyBaselineTarget = targetRequests.filter(hasRecipeTarget).at(-1);
  assert(policyBaselineTarget, '缺少用于特殊经营策略 revision 巡检的基线目标');
  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 3200;
    window.__uiPinningWorkerHoldSuccess = true;
  });
  const policyStartCount = targetRequests.length;
  const policyStartedAt = Date.now();
  const policyRevisionOneServeCount = mutatedSnapshotServeCount;
  setYuumaTargetPolicyRevision(1);
  await waitFor(
    () => mutatedSnapshotServeCount > policyRevisionOneServeCount,
    5_000,
    '未获取血池地狱目标策略 revision 1 快照',
  );
  await waitFor(
    () => targetRequests.slice(policyStartCount).some(isHighlightOnlyClearTarget),
    2_800,
    '目标策略变化后未立即清空旧目标',
  );
  const policyClearRequest = targetRequests
    .slice(policyStartCount)
    .find(isHighlightOnlyClearTarget);
  assert(policyClearRequest, '缺少目标策略变化后的空目标请求');
  assert(
    policyClearRequest.at - policyStartedAt < 2800,
    `目标策略空目标发布过慢：${policyClearRequest.at - policyStartedAt}ms`,
  );
  await waitFor(
    async () => page.evaluate(() => window.__uiPinningWorkerHeldResponses.length > 0),
    6_000,
    '目标策略 revision 1 的延迟 Worker 响应未被暂存',
  );

  const policyRevisionTwoServeCount = mutatedSnapshotServeCount;
  setYuumaTargetPolicyRevision(2);
  await waitFor(
    () => mutatedSnapshotServeCount > policyRevisionTwoServeCount,
    5_000,
    '未获取血池地狱目标策略 revision 2 快照',
  );
  const policySuccessBeforeRelease = await deliveredWorkerSuccessCount(page);
  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 1200;
    window.__releaseUiPinningWorkerHeldResponses();
  });
  await waitFor(
    async () => await deliveredWorkerSuccessCount(page) > policySuccessBeforeRelease,
    3_000,
    '迟到的 revision 1 Worker 响应未完成派发',
  );
  await new Promise((resolve) => setTimeout(resolve, 350));
  assert(
    !targetRequests
      .slice(targetRequests.indexOf(policyClearRequest) + 1)
      .some(hasRecipeTarget),
    '迟到的 revision 1 Worker 响应恢复了旧策略目标',
  );
  await waitFor(
    () => targetRequests
      .slice(targetRequests.indexOf(policyClearRequest) + 1)
      .some(hasRecipeTarget),
    6_000,
    'revision 2 的 current Worker 结果未恢复目标',
  );
  const policyRecoveredTarget = targetRequests
    .slice(targetRequests.indexOf(policyClearRequest) + 1)
    .find(hasRecipeTarget);
  assert(policyRecoveredTarget, '缺少 revision 2 恢复目标');
  assert(
    sameWireTarget(policyBaselineTarget, policyRecoveredTarget),
    '目标策略 revision 更新后的普通订单 wire 目标发生了非预期变化',
  );
  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 0;
  });

  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 3200;
  });
  const ambiguousMutationServeCount = mutatedSnapshotServeCount;
  const ambiguousStartCount = targetRequests.length;
  duplicateCurrentSourceOrder();
  await waitFor(
    () => mutatedSnapshotServeCount > ambiguousMutationServeCount,
    5_000,
    '未获取源订单歧义快照',
  );
  await waitFor(
    () => targetRequests.slice(ambiguousStartCount).some(isHighlightOnlyClearTarget),
    2_800,
    '源订单出现歧义后未立即清空目标',
  );

  const uniqueMutationServeCount = mutatedSnapshotServeCount;
  const uniqueStartCount = targetRequests.length;
  restoreUniqueSourceOrder();
  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 0;
  });
  await waitFor(
    () => mutatedSnapshotServeCount > uniqueMutationServeCount,
    5_000,
    '未获取恢复唯一源订单的快照',
  );
  await waitFor(
    () => targetRequests.slice(uniqueStartCount).some(hasRecipeTarget),
    8_000,
    '唯一源订单恢复后未发布新的当前目标',
  );

  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 3200;
  });
  const identityMutationServeCount = mutatedSnapshotServeCount;
  const identityStartCount = targetRequests.length;
  mutateCurrentSourceIdentity();
  await waitFor(
    () => mutatedSnapshotServeCount > identityMutationServeCount,
    5_000,
    '未获取源订单身份变化快照',
  );
  await waitFor(
    () => targetRequests.slice(identityStartCount).some(isHighlightOnlyClearTarget),
    2_800,
    '源订单不可变身份变化后未立即清空旧目标',
  );
  await page.evaluate(() => {
    window.__uiPinningWorkerDelayMs = 0;
  });

  console.log([
    '游戏界面置顶定向巡检通过：',
    `- POST 方法：${acceptedRetry.method}`,
    `- ID 契约：foodId=${selectedRecipe.id}, recipeId=${selectedRecipe.recipeId}`,
    `- 失败重试：${acceptedRetry.at - rejectedRequest.at}ms`,
    `- 短暂断线：快照恢复后保留成功签名，目标 POST 仍为 ${acceptedTargetCount} 次`,
    `- 连接 revision：先发布空目标隔离旧连接，${identityRequest.at - mutatedSnapshotAt}ms 后重发同一 wire 目标`,
    '- 发布身份：可见目标不变但 targetRevision 变化时重新 POST，隔离连续订单事务',
    '- 无关订单变化：A 的源身份与未送达组件有效时不清空目标',
    '- 组件归约：料理送达保留酒水，酒水送达保留料理与厨具',
    `- 订单完成空窗：${completionClearRequest.at - completionStartedAt}ms 内清空 A 目标，迟到结果未复活`,
    '- 后续订单切换：B 订单就绪后发布新目标，普通 Worker pending 保留有效目标',
    `- 单写者合并：最大并发 ${maxActiveTargetRequests}，延迟请求后补发最新 flags`,
    `- Worker error 清理：recipeId=${errorClearRequest.params.recipeId}, beverageId=${errorClearRequest.params.beverageId}`,
    '- Worker error 恢复：新成功 revision 后恢复当前厨具目标',
    `- 策略 revision：${policyClearRequest.at - policyStartedAt}ms 内清空，迟到旧结果未恢复，新 current 恢复`,
    '- 源订单边界：重复和不可变身份变化立即清空，唯一身份恢复后重新发布',
  ].join('\n'));
} finally {
  await page.close();
  await browser.close();
}

function seedLocalStorage({ apiUrl, apiToken, storagePrefix }) {
  const NativeWorker = window.Worker;
  window.__uiPinningWorkerDelayMs = 0;
  window.__uiPinningWorkerEvents = [];
  window.__uiPinningWorkerHeldResponses = [];
  window.__uiPinningWorkerHoldSuccess = false;
  window.__uiPinningWorkerRejectNext = false;
  window.__releaseUiPinningWorkerHeldResponses = () => {
    window.__uiPinningWorkerHoldSuccess = false;
    const heldResponses = window.__uiPinningWorkerHeldResponses.splice(0);
    for (const held of heldResponses) {
      if (held.worker.delayedOnMessage !== held.listener) continue;
      window.__uiPinningWorkerEvents.push({
        requestId: held.data?.requestId,
        dispatchedAt: Date.now(),
        deliveredOk: Boolean(held.data?.ok),
      });
      held.listener?.call(held.worker, new MessageEvent('message', { data: held.data }));
    }
  };
  window.Worker = class DelayedWorker extends NativeWorker {
    set onmessage(listener) {
      this.delayedOnMessage = listener;
      super.onmessage = (event) => {
        const delayMs = Number(window.__uiPinningWorkerDelayMs || 0);
        window.__uiPinningWorkerEvents.push({ requestId: event.data?.requestId, delayMs, receivedAt: Date.now() });
        const dispatch = () => {
          if (this.delayedOnMessage !== listener) return;
          const reject = Boolean(window.__uiPinningWorkerRejectNext && event.data?.ok);
          const hold = Boolean(window.__uiPinningWorkerHoldSuccess && event.data?.ok && !reject);
          if (hold) {
            window.__uiPinningWorkerEvents.push({
              requestId: event.data?.requestId,
              heldAt: Date.now(),
              deliveredOk: false,
            });
            window.__uiPinningWorkerHeldResponses.push({ worker: this, listener, data: event.data });
            return;
          }
          window.__uiPinningWorkerEvents.push({
            requestId: event.data?.requestId,
            dispatchedAt: Date.now(),
            deliveredOk: Boolean(event.data?.ok) && !reject,
          });
          if (reject) {
            window.__uiPinningWorkerRejectNext = false;
            listener?.call(this, new MessageEvent('message', {
              data: {
                requestId: event.data.requestId,
                ok: false,
                error: 'mock worker failure',
              },
            }));
            return;
          }
          listener?.call(this, event);
        };
        if (delayMs > 0) window.setTimeout(dispatch, delayMs);
        else dispatch();
      };
    }

    get onmessage() {
      return this.delayedOnMessage ?? null;
    }
  };
  localStorage.setItem(`${storagePrefix}-mod-api-endpoint`, apiUrl);
  localStorage.setItem(`${storagePrefix}-mod-api-token`, apiToken);
  localStorage.setItem(`${storagePrefix}-game-ui-pinning`, '1');
  localStorage.setItem(`${storagePrefix}-recommended-extra-ingredient-fill`, '1');
  localStorage.setItem(`${storagePrefix}-cooker-highlight`, '1');
  localStorage.setItem(`${storagePrefix}-seat-highlight`, '1');
  localStorage.setItem(`${storagePrefix}-order-highlight`, '1');
}

function hasRecipeTarget(entry) {
  return Number(entry.params.recipeId) >= 0;
}

function isEnabledClearTarget(entry) {
  return entry.params.enabled === 'true'
    && entry.params.highlightEnabled === 'true'
    && entry.params.orderHighlightEnabled === 'true'
    && entry.params.targetRevision === ''
    && entry.params.orderTraceId === ''
    && Number(entry.params.recipeId) < 0
    && Number(entry.params.beverageId) < 0
    && entry.params.deskCode === '-1'
    && !entry.params.ingredientIds
    && !entry.params.extraIngredientIds;
}

function isHighlightOnlyClearTarget(entry) {
  return entry.params.enabled === 'false'
    && entry.params.highlightEnabled === 'true'
    && entry.params.orderHighlightEnabled === 'true'
    && entry.params.targetRevision === ''
    && entry.params.orderTraceId === ''
    && Number(entry.params.recipeId) < 0
    && Number(entry.params.beverageId) < 0
    && entry.params.deskCode === '-1'
    && !entry.params.ingredientIds
    && !entry.params.extraIngredientIds;
}

function isBeverageOnlyTarget(entry) {
  return entry.params.enabled === 'true'
    && entry.params.highlightEnabled === 'true'
    && Number(entry.params.recipeId) < 0
    && Number(entry.params.beverageId) >= 0;
}

function isRecipeOnlyTarget(entry) {
  return entry.params.enabled === 'true'
    && entry.params.highlightEnabled === 'true'
    && Number(entry.params.recipeId) >= 0
    && Number(entry.params.beverageId) < 0;
}

function mutateSnapshot(recommendationSignal) {
  assert(mutatedSnapshot?.nightBusiness?.orders?.[0], '缺少可变 Mock 快照');
  mutatedSnapshot.nightBusiness.orders[0].automationBlockReason = recommendationSignal;
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|${recommendationSignal}`;
}

function mutateInternalTargetSignature() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.[0], '缺少可变 Mock 稀客订单');
  const order = mutatedSnapshot.nightBusiness.orders[0];
  order.firstSeenAtUtc = new Date(Date.parse(order.firstSeenAtUtc) + 1000).toISOString();
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|target-signature-only`;
}

function retainOnlyFirstPendingOrder() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length >= 2, '缺少用于单订单巡检的 Mock 稀客订单');
  const [firstOrder, secondOrder] = mutatedSnapshot.nightBusiness.orders;
  deferredSecondOrder = structuredClone(secondOrder);
  firstOrder.hasServedFood = false;
  firstOrder.hasServedBeverage = false;
  mutatedSnapshot.nightBusiness.orders = [firstOrder];
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|single-pending-order`;
}

function completeOnlyOrder() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 1, '订单完成巡检要求只有一个 Mock 稀客订单');
  const [order] = mutatedSnapshot.nightBusiness.orders;
  order.hasServedFood = true;
  order.hasServedBeverage = true;
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|single-order-completed`;
}

function serveOnlyOrderFood() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 1, '料理送达巡检要求只有一个 Mock 稀客订单');
  const [order] = mutatedSnapshot.nightBusiness.orders;
  order.hasServedFood = true;
  order.hasServedBeverage = false;
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|single-order-food-served`;
}

function serveOnlyOrderBeverage() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 1, '酒水送达巡检要求只有一个 Mock 稀客订单');
  const [order] = mutatedSnapshot.nightBusiness.orders;
  order.hasServedFood = false;
  order.hasServedBeverage = true;
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|single-order-beverage-served`;
}

function showDeferredSecondOrderAlongsideActive() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 1, '无关订单新增巡检要求只有一个活动订单');
  assert(deferredSecondOrder, '缺少暂存的无关 B 订单');
  mutatedSnapshot.nightBusiness.orders = [
    ...mutatedSnapshot.nightBusiness.orders,
    structuredClone(deferredSecondOrder),
  ];
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|unrelated-order-added`;
}

function hideDeferredSecondOrder() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 2, '无关订单移除巡检缺少两笔订单');
  mutatedSnapshot.nightBusiness.orders = [mutatedSnapshot.nightBusiness.orders[0]];
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|unrelated-order-removed`;
}

function duplicateCurrentSourceOrder() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 1, '源订单歧义巡检要求唯一活动订单');
  mutatedSnapshot.nightBusiness.orders.push(structuredClone(mutatedSnapshot.nightBusiness.orders[0]));
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|source-order-duplicated`;
}

function restoreUniqueSourceOrder() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 2, '恢复唯一源订单巡检要求重复订单');
  mutatedSnapshot.nightBusiness.orders = [mutatedSnapshot.nightBusiness.orders[0]];
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|source-order-unique`;
}

function mutateCurrentSourceIdentity() {
  assert(mutatedSnapshot?.nightBusiness?.orders?.length === 1, '源订单身份巡检要求唯一活动订单');
  const [order] = mutatedSnapshot.nightBusiness.orders;
  order.firstSeenAtUtc = new Date(Date.parse(order.firstSeenAtUtc) + 5000).toISOString();
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|source-order-identity-changed`;
}

function setYuumaTargetPolicyRevision(revision) {
  assert(mutatedSnapshot, '缺少可变 Mock 快照');
  mutatedSnapshot.specialBusiness = {
    active: true,
    challengeTypeAvailable: true,
    challengeType: 'Story_BloodPondHell',
    displayName: '血池地狱',
    category: 'challenge',
    ruleSummary: 'mock target policy revision audit',
    foodTargetTags: ['甜', '肉'],
    beverageTargetTags: [],
    yuumaFoodTargetRevision: revision,
    phase: 'Phase 1',
    currentAnger: 0,
    maxAnger: 100,
    targetAnger: 80,
    recommendationPolicy: 'yuuma-target',
    automationPolicy: 'manual',
    source: 'mock-ui-pinning-policy-audit',
    error: null,
  };
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|yuuma-policy:${revision}`;
}

function injectDeferredSecondOrder() {
  assert(mutatedSnapshot?.nightBusiness?.orders, '缺少可变 Mock 夜间订单');
  assert(deferredSecondOrder, '缺少暂存的后续 B 订单');
  mutatedSnapshot.nightBusiness.orders = [structuredClone(deferredSecondOrder)];
  mutatedSnapshot.snapshotSignature = `${mutatedSnapshot.snapshotSignature}|second-order-arrived`;
}

async function deliveredWorkerSuccessCount(page) {
  return page.evaluate(() =>
    window.__uiPinningWorkerEvents.filter((event) => event.deliveredOk === true).length);
}

function sameTarget(left, right) {
  return left.params.enabled === right.params.enabled
    && left.params.highlightEnabled === right.params.highlightEnabled
    && left.params.extraIngredientFillEnabled === right.params.extraIngredientFillEnabled
    && left.params.seatHighlightEnabled === right.params.seatHighlightEnabled
    && left.params.orderHighlightEnabled === right.params.orderHighlightEnabled
    && left.params.targetRevision === right.params.targetRevision
    && left.params.orderTraceId === right.params.orderTraceId
    && left.params.recipeId === right.params.recipeId
    && left.params.recipeName === right.params.recipeName
    && left.params.ingredientIds === right.params.ingredientIds
    && left.params.extraIngredientIds === right.params.extraIngredientIds
    && left.params.beverageId === right.params.beverageId
    && left.params.beverageName === right.params.beverageName
    && left.params.cookerTypeId === right.params.cookerTypeId
    && left.params.cookerName === right.params.cookerName
    && left.params.deskCode === right.params.deskCode;
}

function sameWireTarget(left, right) {
  return left.params.targetRevision === right.params.targetRevision
    && left.params.orderTraceId === right.params.orderTraceId
    && left.params.recipeId === right.params.recipeId
    && left.params.recipeName === right.params.recipeName
    && left.params.ingredientIds === right.params.ingredientIds
    && left.params.extraIngredientIds === right.params.extraIngredientIds
    && left.params.beverageId === right.params.beverageId
    && left.params.beverageName === right.params.beverageName
    && left.params.cookerTypeId === right.params.cookerTypeId
    && left.params.cookerName === right.params.cookerName
    && left.params.deskCode === right.params.deskCode;
}

function sameVisibleTarget(left, right) {
  return left.params.orderTraceId === right.params.orderTraceId
    && left.params.recipeId === right.params.recipeId
    && left.params.recipeName === right.params.recipeName
    && left.params.ingredientIds === right.params.ingredientIds
    && left.params.extraIngredientIds === right.params.extraIngredientIds
    && left.params.beverageId === right.params.beverageId
    && left.params.beverageName === right.params.beverageName
    && left.params.cookerTypeId === right.params.cookerTypeId
    && left.params.cookerName === right.params.cookerName
    && left.params.deskCode === right.params.deskCode;
}

async function fulfillJson(route, body) {
  await route.fulfill({
    status: 200,
    contentType: 'application/json',
    headers: { 'Access-Control-Allow-Origin': '*' },
    body: JSON.stringify(body),
  });
}

async function readRuntimeData() {
  const response = await fetch(`${API_URL}/runtime-data`, {
    headers: { 'X-Mystia-Steward-Companion-Token': API_TOKEN },
  });
  assert(response.ok, `无法读取 Mock 运行时目录：HTTP ${response.status}`);
  return response.json();
}

async function waitFor(predicate, timeoutMs, message) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (await predicate()) return;
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  throw new Error(message);
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}
