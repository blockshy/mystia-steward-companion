import http from 'node:http';

/**
 * 本地 API 模拟服务。
 *
 * 用于前端预览和 Playwright UI 巡检，不连接真实游戏进程。数据形态尽量贴近 Mod loopback API，
 * 但所有库存、收藏、订单和更新状态都只保存在当前 Node 进程内。
 */
const DEFAULT_HOST = '127.0.0.1';
const DEFAULT_PORT = 32145;
const MOCK_TOKEN = 'mock-token';
const MOCK_TRACKED_MISSIONS_SIGNATURE = 'a'.repeat(64);
const MOCK_AVAILABLE_MISSIONS_SIGNATURE = 'b'.repeat(64);
const MOCK_LAN_ENDPOINTS = [
  {
    address: '192.168.1.20',
    interfaceName: 'Wi-Fi',
    interfaceType: 'Wireless80211',
    hasGateway: true,
    linkLocal: false,
    recommended: true,
  },
  {
    address: '172.24.112.1',
    interfaceName: 'vEthernet (WSL)',
    interfaceType: 'Ethernet',
    hasGateway: false,
    linkLocal: false,
    recommended: false,
  },
];
const AUTOMATION_LEASE_TTL_MS = 15000;

const host = process.env.MOCK_API_HOST || DEFAULT_HOST;
const port = Number(process.env.MOCK_API_PORT || DEFAULT_PORT);
const automationSessionId = process.env.MOCK_AUTOMATION_SESSION_ID?.trim() || 'mock-automation-session';
let mockToken = MOCK_TOKEN;

const ingredients = [
  ingredient(1, '鸡蛋', ['家常', '甜'], 8, '禽蛋'),
  ingredient(2, '蜂蜜', ['甜', '适合拍照'], 18, '调味'),
  ingredient(3, '鲑鱼', ['水产', '清淡', '鲜'], 24, '水产'),
  ingredient(4, '黄瓜', ['素', '清爽'], 6, '蔬菜'),
  ingredient(5, '牛肉', ['肉', '力量涌现'], 30, '肉类'),
  ingredient(6, '蘑菇', ['鲜', '菌类'], 12, '菌类'),
  ingredient(7, '月光草', ['梦幻', '高级'], 42, '香草'),
  ingredient(8, '辣椒', ['灼热'], 10, '调味'),
];

const beverages = [
  beverage(0, '绿茶', ['无酒精', '可加热'], 1),
  beverage(101, '果味米酒', ['水果', '低酒精'], 18),
  beverage(102, '冰镇啤酒', ['可加冰', '中酒精'], 24),
  beverage(103, '月都清酒', ['高级', '清酒'], 58),
  beverage(104, '热茶', ['无酒精', '可加热'], 10),
  beverage(105, '蜂蜜气泡水', ['甜', '无酒精'], 16),
];

const recipes = [
  // foodId 与 Recipe.Id 故意不同，避免 Mock 再次掩盖运行时 ID 契约错误。
  recipe(201, 1201, '豆腐味噌', ['黄瓜', '蘑菇'], ['家常', '素', '清淡'], '煮锅', 26),
  recipe(202, 1202, '蜂蜜蛋糕', ['鸡蛋', '蜂蜜'], ['甜', '适合拍照', '招牌'], '料理台', 38),
  recipe(203, 1203, '烤鲑鱼', ['鲑鱼', '辣椒'], ['水产', '鲜', '清淡'], '烧烤架', 42),
  // 五种基础材料不留加料槽；作为任务目标时，明确无法补出 Mock 订单的“甜”Tag，且命中米斯蒂娅的“肉”厌恶。
  recipe(204, 1204, '牛肉火锅', ['牛肉', '辣椒', '蘑菇', '黄瓜', '鲑鱼'], ['肉', '灼热', '力量涌现', '昂贵'], '煮锅', 62),
  recipe(205, 1205, '蘑菇拼盘', ['蘑菇', '黄瓜'], ['菌类', '家常', '鲜'], '蒸锅', 31),
  recipe(206, 1206, '月光团子', ['月光草', '蜂蜜'], ['梦幻', '甜', '高级'], '料理台', 78),
  recipe(207, 1207, '香辣烤肉', ['牛肉', '辣椒'], ['肉', '灼热', '力量涌现'], '烧烤架', 55),
  recipe(208, 1208, '清爽沙拉', ['黄瓜', '蜂蜜'], ['清爽', '素', '适合拍照'], '料理台', 22),
];

const normalCustomers = [
  normalCustomer(301, '妖怪鼠客', ['妖怪兽道'], ['家常', '鲜', '素'], ['低酒精', '无酒精']),
  normalCustomer(302, '兽道旅人', ['妖怪兽道', '人间之里'], ['清淡', '甜', '适合拍照'], ['水果', '可加冰']),
  normalCustomer(303, '村里常客', ['人间之里'], ['肉', '家常', '灼热'], ['中酒精', '可加热']),
  normalCustomer(304, '山脚商人', ['妖怪兽道'], ['昂贵', '高级', '梦幻'], ['清酒', '高级']),
];

const rareCustomers = [
  rareCustomer(1001, '米斯蒂娅', ['妖怪兽道'], ['甜', '梦幻', '适合拍照'], ['水果', '无酒精'], ['肉']),
  rareCustomer(1002, '露米娅', ['妖怪兽道'], ['肉', '灼热', '力量涌现'], ['中酒精', '可加冰'], ['素']),
  rareCustomer(1003, '慧音', ['人间之里', '妖怪兽道'], ['清淡', '家常', '高级'], ['可加热', '清酒'], ['灼热']),
  rareCustomer(1004, '莉格露', ['妖怪兽道'], ['菌类', '鲜', '清爽'], ['低酒精', '水果'], ['昂贵']),
];

const favoriteData = {
  version: 1,
  recipes: [
    {
      id: 'mock-recipe-1001-甜-202',
      customerId: 1001,
      customerName: '米斯蒂娅',
      foodTag: '甜',
      recipeId: 202,
      extraIngredientIds: [7],
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    },
  ],
  beverages: [
    {
      id: 'mock-beverage-1001-水果-101',
      customerId: 1001,
      customerName: '米斯蒂娅',
      beverageTag: '水果',
      beverageId: 101,
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    },
  ],
};

const customRecipeData = {
  version: 1,
  enabled: true,
  recipes: [
    {
      id: 'mock-custom-1001-all-1202',
      customerId: 1001,
      customerName: '米斯蒂娅',
      foodTag: null,
      foodId: 202,
      recipeId: 1202,
      recipeName: '蜂蜜蛋糕',
      extraIngredientIds: [7],
      enabled: true,
      pinToTop: true,
      sortOrder: 100,
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    },
    {
      id: 'mock-custom-1001-all-1206',
      customerId: 1001,
      customerName: '米斯蒂娅',
      foodTag: null,
      foodId: 206,
      recipeId: 1206,
      recipeName: '月光团子',
      extraIngredientIds: [],
      enabled: false,
      pinToTop: false,
      sortOrder: 200,
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    },
    {
      id: 'mock-custom-1002-tag-1202',
      customerId: 1002,
      customerName: '露米娅',
      foodTag: '肉',
      foodId: 202,
      recipeId: 1202,
      recipeName: '蜂蜜蛋糕',
      extraIngredientIds: [],
      enabled: true,
      pinToTop: false,
      sortOrder: 300,
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    },
    {
      id: 'mock-custom-1002-all-1207',
      customerId: 1002,
      customerName: '露米娅',
      foodTag: null,
      foodId: 207,
      recipeId: 1207,
      recipeName: '香辣烤肉',
      extraIngredientIds: [],
      enabled: true,
      pinToTop: true,
      sortOrder: 400,
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    },
  ],
};

const inventory = {
  ingredient: {
    1: 12,
    2: 7,
    3: 5,
    4: 3,
    5: 8,
    6: 2,
    7: 1,
    8: 16,
  },
  beverage: {
    0: -1,
    101: 9,
    102: 4,
    103: 2,
    104: 14,
    105: 6,
  },
};

const logSettings = {
  aggregateModLogEnabled: false,
  aggregateModLogPath: '/tmp/mystia-steward-companion/mock/aggregate-mod.log',
  aggregateModLogDirectory: '/tmp/mystia-steward-companion/mock',
  aggregateModLogMaxFileBytes: 10 * 1024 * 1024,
  aggregateModLogMaxFileCount: 30,
  aggregateModLogMaxTotalBytes: 300 * 1024 * 1024,
  bepInExConsoleSupported: true,
  bepInExConsoleConfiguredVisible: false,
  bepInExConsoleActive: false,
  bepInExConsoleVisible: false,
  bepInExConsoleStatus: 'inactive',
};
let nextBepInExConsoleFailure = null;

const updateStatus = {
  ok: true,
  currentVersion: '1.0.9-mock',
  enabled: true,
  autoCheck: true,
  includePrerelease: false,
  state: 'available',
  latestVersion: '1.0.10',
  latestTag: 'v1.0.10',
  hasUpdate: true,
  lastAttemptAtUtc: nowIso(),
  lastSuccessAtUtc: nowIso(),
  nextCheckAtUtc: new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString(),
  consecutiveFailures: 0,
  publishedAtUtc: nowIso(),
  releaseUrl: 'https://github.com/blockshy/mystia-steward-companion/releases/tag/v1.0.10',
  packageAsset: 'mystia-steward-companion-bepinex.zip',
  packageSize: 27 * 1024 * 1024,
  downloadedVersion: '',
  downloadedAtUtc: '',
  staged: false,
  installState: '',
  installMessage: '',
  error: null,
};

const connectionConfig = {
  lanEnabled: process.env.MOCK_LAN_ENABLED === '1',
  lanBindHost: 'auto',
};
let automationLease = null;
let automationCommandEpoch = 1;
let automationCookingJobs = [];
const mockAutomationBarrierTarget = {
  targetIdentity: 'rare:mock-trace-barrier',
  traceId: 'mock-trace-barrier',
  targetKind: 'rare',
  orderKey: '',
  deskCode: 1,
  guestId: 1001,
  guestName: '米斯蒂娅',
  foodId: 202,
  foodName: '蜂蜜蛋糕',
  beverageId: 101,
  beverageName: '果味米酒',
};
const automationSafetyBarriers = new Map([
  [9000, {
    ...mockAutomationBarrierTarget,
    sequence: 9000,
    code: 'cooking-manual-handoff-unreadable',
    message: 'mock 无法确认手动接管后的托盘状态，请核对游戏现场。',
  }],
  [9001, {
    ...mockAutomationBarrierTarget,
    sequence: 9001,
    code: 'order-evaluation-commit-uncertain',
    message: 'mock 无法确认订单评价是否提交，请核对游戏现场。',
  }],
]);

const server = http.createServer((request, response) => {
  setCorsHeaders(response);

  if (request.method === 'OPTIONS') {
    response.writeHead(204);
    response.end();
    return;
  }

  const requestUrl = new URL(request.url || '/', `http://${host}:${port}`);
  const path = requestUrl.pathname;

  if (request.method === 'POST') {
    try {
      if (path === '/automation/lease/acquire') {
        sendJson(response, 200, acquireAutomationLease(request));
        return;
      }

      if (path === '/automation/barriers/ack') {
        sendJson(response, 200, acknowledgeAutomationSafetyBarrier(request, requestUrl.searchParams));
        return;
      }

      if (path === '/automation/jobs/cancel') {
        sendJson(response, 200, cancelAutomationAndReleaseLease(request));
        return;
      }

      if (path === '/local-api/config') {
        connectionConfig.lanEnabled = normalizeBoolean(requestUrl.searchParams.get('lanEnabled'), connectionConfig.lanEnabled);
        connectionConfig.lanBindHost = normalizeLanHost(requestUrl.searchParams.get('lanHost') || connectionConfig.lanBindHost);
        sendJson(response, 200, buildConnectionConfig());
        return;
      }

      if (path === '/local-api/token/regenerate') {
        mockToken = `mock-token-${Date.now().toString(36)}`;
        sendJson(response, 200, buildConnectionConfig());
        return;
      }

      if (path === '/diagnostics/automation-decision') {
        sendJson(response, 200, { ok: true, status: 'mock automation diagnostic recorded', error: null });
        return;
      }

      if (path === '/logs/config') {
        applyLogSettings(requestUrl.searchParams);
        sendJson(response, 200, logSettings);
        return;
      }

      if (path === '/mock/logs/console-failure') {
        nextBepInExConsoleFailure = {
          error: requestUrl.searchParams.get('message') || 'mock console action failed',
          reportedVisible: readOptionalMockBoolean(requestUrl.searchParams.get('reportedVisible')),
        };
        sendJson(response, 200, { ok: true });
        return;
      }

      if (path === '/logs/console') {
        if (nextBepInExConsoleFailure) {
          const failure = nextBepInExConsoleFailure;
          nextBepInExConsoleFailure = null;
          if (failure.reportedVisible !== null) {
            applyBepInExConsoleVisibility(
              new URLSearchParams({ visible: String(failure.reportedVisible) }),
            );
          }
          sendJson(response, 200, buildBepInExConsoleVisibilityResponse(false, failure.error));
          return;
        }
        sendJson(response, 200, applyBepInExConsoleVisibility(requestUrl.searchParams));
        return;
      }

      if (path === '/logs/open-folder') {
        sendJson(response, 200, { ok: true, directory: logSettings.aggregateModLogDirectory, error: null });
        return;
      }

      if (path === '/logs/export-diagnostics') {
        sendJson(response, 200, {
          ok: true,
          path: '/tmp/mystia-steward-companion/mock/diagnostics.zip',
          directory: '/tmp/mystia-steward-companion/mock',
          files: ['manifest.json', 'snapshot/current-snapshot.json', 'snapshot/runtime-data.json', 'logs/aggregate-mod.log'],
          error: null,
        });
        return;
      }

      if (path === '/favorites/add-recipe') {
        sendJson(response, 200, mutateRecipeFavorite(requestUrl.searchParams));
        return;
      }

      if (path === '/favorites/remove-recipe') {
        removeFavorite(favoriteData.recipes, requestUrl.searchParams.get('id'));
        sendJson(response, 200, { ok: true, favorites: favoriteData, error: null });
        return;
      }

      if (path === '/favorites/add-beverage') {
        sendJson(response, 200, mutateBeverageFavorite(requestUrl.searchParams));
        return;
      }

      if (path === '/favorites/remove-beverage') {
        removeFavorite(favoriteData.beverages, requestUrl.searchParams.get('id'));
        sendJson(response, 200, { ok: true, favorites: favoriteData, error: null });
        return;
      }

      if (path === '/custom-recipes/upsert') {
        sendJson(response, 200, upsertCustomRecipe(requestUrl.searchParams));
        return;
      }

      if (path === '/custom-recipes/remove') {
        removeFavorite(customRecipeData.recipes, requestUrl.searchParams.get('id'));
        normalizeCustomRecipeSortOrders();
        sendJson(response, 200, { ok: true, customRecipes: customRecipeData, error: null });
        return;
      }

      if (path === '/custom-recipes/settings') {
        sendJson(response, 200, setCustomRecipesEnabled(requestUrl.searchParams));
        return;
      }

      if (path === '/custom-recipes/update-flags') {
        sendJson(response, 200, updateCustomRecipeFlags(requestUrl.searchParams));
        return;
      }

      if (path === '/custom-recipes/move') {
        sendJson(response, 200, moveCustomRecipe(requestUrl.searchParams));
        return;
      }

      if (path === '/rare-guests/invite-all' || path === '/rare-guests/invite') {
        sendJson(response, 200, buildInvitationResponse(path, requestUrl.searchParams));
        return;
      }

      if (path === '/inventory/set') {
        sendJson(response, 200, setInventoryQuantity(requestUrl.searchParams));
        return;
      }

      if (path === '/inventory/bulk-set') {
        sendJson(response, 200, setBulkInventoryQuantity(requestUrl.searchParams));
        return;
      }

      if (path === '/orders/rare/dismiss') {
        sendJson(response, 200, { ok: true, removed: 1, status: 'mock rare order dismissed', error: null });
        return;
      }

      if (path === '/orders/prepare-next' || path === '/orders/complete-first' || path === '/orders/normal/complete-first') {
        const lease = readAutomationLease(request);
        if (!lease.owned) {
          sendJson(response, 200, {
            ok: false,
            prepared: false,
            error: lease.error || (lease.ownerLabel ? `自动化当前由 ${lease.ownerLabel} 控制，本窗口仅查看。` : '自动化控制权不可用。'),
            order: {
              traceId: '',
              deskCode: -1,
              guestId: null,
              guestName: '',
              foodTag: '',
              beverageTag: '',
            },
            recipeId: -1,
            recipeName: '',
            beverageId: -1,
            beverageName: '',
            automation: {
              outcome: 'retryable-failure',
              stage: 'lease',
              reasonCode: 'automation-lease-unavailable',
              jobId: '',
              retryAfterMs: 1000,
            },
            steps: [],
          });
          return;
        }
        const actionResponse = buildOrderActionResponse(requestUrl.searchParams);
        automationCookingJobs = [buildMockAutomationCookingJob(actionResponse, path, requestUrl.searchParams)];
        sendJson(response, 200, actionResponse);
        return;
      }

      if (path === '/ui-pinning/target') {
        sendJson(response, 200, { ok: true, status: 'mock target accepted' });
        return;
      }

      if (path === '/updates/check') {
        updateStatus.state = 'available';
        updateStatus.lastAttemptAtUtc = nowIso();
        updateStatus.lastSuccessAtUtc = nowIso();
        updateStatus.nextCheckAtUtc = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
        updateStatus.consecutiveFailures = 0;
        updateStatus.hasUpdate = true;
        updateStatus.error = null;
        sendJson(response, 200, updateStatus);
        return;
      }

      if (path === '/updates/status') {
        sendJson(response, 200, updateStatus);
        return;
      }

      if (path === '/updates/download') {
        updateStatus.state = 'downloaded';
        updateStatus.downloadedVersion = updateStatus.latestVersion;
        updateStatus.downloadedAtUtc = nowIso();
        updateStatus.staged = true;
        updateStatus.error = null;
        sendJson(response, 200, updateStatus);
        return;
      }

      if (path === '/updates/install-on-exit') {
        updateStatus.installState = 'waiting';
        updateStatus.installMessage = '等待游戏和伴随窗口退出后安装更新。';
        updateStatus.error = null;
        sendJson(response, 200, updateStatus);
        return;
      }

      sendJson(response, 404, { ok: false, error: `Unknown mock endpoint: ${path}` });
      return;
    } catch (error) {
      sendJson(response, 500, { ok: false, error: error instanceof Error ? error.message : String(error) });
      return;
    }
  }

  if (request.method !== 'GET') {
    sendJson(response, 405, { ok: false, error: 'Method not allowed by the mock local API.' });
    return;
  }

  try {
    if (path === '/health') {
      sendJson(response, 200, buildHealth());
      return;
    }

    if (path === '/automation/lease') {
      sendJson(response, 200, readAutomationLease(request));
      return;
    }

    if (path === '/local-api/config') {
      sendJson(response, 200, buildConnectionConfig());
      return;
    }

    if (path === '/snapshot') {
      const snapshot = buildSnapshot();
      if (requestUrl.searchParams.get('knownSignature') === snapshot.snapshotSignature) {
        sendJson(response, 200, {
          unchanged: true,
          snapshotSignature: snapshot.snapshotSignature,
        });
        return;
      }

      sendJson(response, 200, snapshot);
      return;
    }

    if (path === '/runtime-data') {
      sendJson(response, 200, buildRuntimeData());
      return;
    }

    if (path === '/favorites') {
      sendJson(response, 200, favoriteData);
      return;
    }

    if (path === '/custom-recipes') {
      sendJson(response, 200, customRecipeData);
      return;
    }

    if (path === '/missions/tracked') {
      sendJson(response, 200, buildTrackedMissionsResponse(requestUrl.searchParams));
      return;
    }

    if (path === '/missions/available') {
      sendJson(response, 200, buildAvailableMissionsResponse(requestUrl.searchParams));
      return;
    }

    if (path === '/rare-guests/invitations') {
      sendJson(response, 200, buildInvitationResponse(path, requestUrl.searchParams));
      return;
    }

    if (path === '/logs/settings') {
      sendJson(response, 200, logSettings);
      return;
    }

    sendJson(response, 404, { ok: false, error: `Unknown mock endpoint: ${path}` });
  } catch (error) {
    sendJson(response, 500, { ok: false, error: error instanceof Error ? error.message : String(error) });
  }
});

server.listen(port, host, () => {
  console.log(`mock local API listening on http://${host}:${port}`);
  console.log(`token for browser localStorage: ${MOCK_TOKEN}`);
});

for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => {
    server.close(() => process.exit(0));
  });
}

function buildSnapshot() {
  const snapshot = {
    pluginVersion: '1.0.5-mock',
    nightBusinessGeneration: 1,
    nightBusinessLifecyclePhase: 'Active',
    runtimeNightBusinessLifecycleStatus: 'mock active generation=1',
    automationSessionId,
    capturedAtUtc: nowIso(),
    activeSceneName: 'NightScene.MockBusiness',
    activeDayMapLabel: '妖怪兽道',
    activeDayMapName: '妖怪兽道',
    runtimeLoaded: true,
    runtimeDaySceneGeneration: 1,
    runtimeDaySceneReady: true,
    missionGeneration: 1,
    status: 'mock runtime snapshot',
    runtimeSource: 'mock-local-api',
    runtimeSceneReadinessStatus: 'ready',
    runtimeUiPinningStatus: 'patches=checkPinnedPrefix:patched, cookingScope:patched, beverageScope:patched; pinning=on; cookerHighlight=on; target=recipe:1202/蜂蜜蛋糕, beverage:101/果味米酒, cooker:5/料理台, ingredients:2,7; highlight=active; listHighlight=hooks=patched; state=active; tracked=recipe:1, ingredients:2, beverage:1; missingImage=0; bindingErrors=0; visualErrors=0; restoreErrors=0; forcedTotal=recipe:1, ingredients:2, beverage:1; scopeImbalance=0',
    recommendationState: {
      availableRecipeIds: recipes.map((item) => item.id),
      availableBeverageIds: beverages.map((item) => item.id),
      availableIngredientIds: ingredients.map((item) => item.id),
      availableRareCustomerIds: rareCustomers.map((item) => item.id),
      ownedIngredientQty: inventory.ingredient,
      ownedBeverageQty: inventory.beverage,
      placedCookerTypeIds: [1, 2, 3, 4, 5],
      placedCookers: [
        { controllerIndex: 0, gridPosition: { x: 0, y: 0, z: 0 }, controllerIdentity: '0x1000', typeIds: [1], typeNames: ['煮锅'], name: '煮锅', challengeLocked: false, couldOpen: true, automationAvailable: false, automationAvailability: 'Unavailable', automationAvailabilityDiagnostic: 'mock busy', source: 'mock' },
        { controllerIndex: 1, gridPosition: { x: 1, y: 0, z: 0 }, controllerIdentity: '0x1001', typeIds: [2], typeNames: ['烧烤架'], name: '烧烤架', challengeLocked: false, couldOpen: true, automationAvailable: true, automationAvailability: 'StrictIdle', automationAvailabilityDiagnostic: 'mock strict idle', source: 'mock' },
        { controllerIndex: 2, gridPosition: { x: 2, y: 0, z: 0 }, controllerIdentity: '0x1002', typeIds: [3], typeNames: ['油锅'], name: '油锅', challengeLocked: false, couldOpen: true, automationAvailable: false, automationAvailability: 'Unavailable', automationAvailabilityDiagnostic: 'mock busy', source: 'mock' },
        { controllerIndex: 3, gridPosition: { x: 3, y: 0, z: 0 }, controllerIdentity: '0x1003', typeIds: [4], typeNames: ['蒸锅'], name: '蒸锅', challengeLocked: false, couldOpen: true, automationAvailable: false, automationAvailability: 'Unavailable', automationAvailabilityDiagnostic: 'mock busy', source: 'mock' },
        { controllerIndex: 4, gridPosition: { x: 4, y: 0, z: 0 }, controllerIdentity: '0x1004', typeIds: [5], typeNames: ['料理台'], name: '料理台', challengeLocked: false, couldOpen: true, automationAvailable: false, automationAvailability: 'Unavailable', automationAvailabilityDiagnostic: 'mock busy', source: 'mock' },
      ],
      placedCookerSnapshotComplete: true,
      placedCookerControllerCount: 5,
      placedCookerEmptyControllerCount: 0,
      placedCookerLockedControllerCount: 0,
      placedCookerReadFailureCount: 0,
      placedCookerStatus: 'mock cookers ready',
      popularFoodTag: '甜',
      popularHateFoodTag: '肉',
      famousShopEnabled: true,
    },
    nightBusiness: {
      place: '妖怪兽道',
      placeLabel: '妖怪兽道',
      activeRareGuests: [
        { deskCode: 1, guestId: 1001, guestName: '米斯蒂娅', source: 'mock', fund: 420, baseFundCarry: 240, maxFundCarry: 520, extraFundByBuff: 80, willPayMoney: true },
        { deskCode: 3, guestId: 1002, guestName: '露米娅', source: 'mock', fund: 0, baseFundCarry: 220, maxFundCarry: 430, extraFundByBuff: 40, willPayMoney: false },
      ],
      orders: [
        {
          traceId: 'R-MOCK-0001',
          deskCode: 1,
          guestId: 1001,
          runtimeGuestId: 1001,
          guestName: '米斯蒂娅',
          foodTagId: 11,
          foodTag: '甜',
          beverageTagId: 21,
          beverageTag: '水果',
          source: 'mock',
          firstSeenAtUtc: nowIso(-240),
          lastSeenAtUtc: nowIso(-12),
          isFreeOrder: false,
          hasServedFood: false,
          hasServedBeverage: true,
          missionRecipePriority: {
            traceId: 'R-MOCK-0001',
            deskCode: 1,
            guestId: 1001,
            runtimeGuestId: 1001,
            foodId: 204,
            recipeId: 1204,
            missionGeneration: 1,
            businessGeneration: 1,
          },
        },
        {
          deskCode: 3,
          guestId: 1002,
          runtimeGuestId: 1002,
          guestName: '露米娅',
          foodTagId: 12,
          foodTag: '肉',
          beverageTagId: 22,
          beverageTag: '中酒精',
          source: 'mock',
          firstSeenAtUtc: nowIso(-120),
          lastSeenAtUtc: nowIso(-5),
          isFreeOrder: true,
          hasServedFood: false,
          hasServedBeverage: false,
        },
      ],
      source: 'mock-night-business',
      error: null,
    },
    normalBusiness: {
      orders: [
        {
          orderKey: 'mock-normal-1',
          deskCode: 2,
          runtimeGuestId: null,
          guestName: '妖怪鼠客',
          foodId: 205,
          foodName: '蘑菇拼盘',
          beverageId: 104,
          beverageName: '热茶',
          hasServedFood: false,
          hasServedBeverage: true,
          readyToEvaluate: false,
          hasEvaluated: false,
          firstSeenAtUtc: nowIso(-75),
          source: 'mock',
        },
        {
          orderKey: 'mock-normal-2',
          deskCode: 4,
          runtimeGuestId: null,
          guestName: '兽道旅人',
          foodId: 208,
          foodName: '清爽沙拉',
          beverageId: 101,
          beverageName: '果味米酒',
          hasServedFood: false,
          hasServedBeverage: false,
          readyToEvaluate: false,
          hasEvaluated: false,
          firstSeenAtUtc: nowIso(-40),
          source: 'mock',
        },
      ],
      source: 'mock-normal-business',
      error: null,
    },
    automationEvents: [...automationSafetyBarriers.values()].map((barrier) => ({
      sequence: barrier.sequence,
      createdAtUtc: nowIso(-5),
      code: barrier.code,
      jobId: '',
      outcome: 'blocked',
      reasonCode: barrier.code,
      terminal: true,
      generation: 0,
      cookerPhase: -1,
      cookerProgress: -1,
      traceId: barrier.traceId,
      targetKind: barrier.targetKind,
      orderKey: barrier.orderKey,
      deskCode: barrier.deskCode,
      guestId: barrier.guestId,
      guestName: barrier.guestName,
      foodId: barrier.foodId,
      foodName: barrier.foodName,
      beverageId: barrier.beverageId,
      beverageName: barrier.beverageName,
      message: barrier.message,
    })),
    automationCookingJobs: automationCookingJobs.map((job) => ({ ...job })),
    runtimeDataComplete: true,
    runtimeDataSource: 'mock-local-api',
    runtimeDataStatus: 'mock runtime data complete',
    runtimeDataSignature: buildRuntimeDataSignature(),
    performanceMs: {
      snapshot: 3,
      runtimeData: 6,
      recommendations: 4,
    },
  };
  snapshot.snapshotSignature = buildSnapshotSignature(snapshot);
  return snapshot;
}

function buildSnapshotSignature(snapshot) {
  return [
    snapshot.pluginVersion,
    snapshot.activeSceneName,
    snapshot.runtimeLoaded ? '1' : '0',
    snapshot.status,
    snapshot.runtimeDataSignature,
    snapshot.nightBusiness?.orders?.length ?? 0,
    snapshot.normalBusiness?.orders?.length ?? 0,
    snapshot.specialBusiness?.challengeType ?? '',
    snapshot.specialBusiness?.phase ?? '',
    snapshot.automationCookingJobs.map((job) => `${job.jobId}:${job.state}:${job.reasonCode}`).join(','),
    snapshot.automationEvents.map((event) => event.sequence).join(','),
  ].join('|');
}

function buildRuntimeData() {
  return {
    isComplete: true,
    source: 'mock-local-api',
    status: 'mock runtime data complete',
    recipes,
    ingredients,
    beverages,
    normalCustomers,
    rareCustomers,
    foodTagIdMap: {
      甜: '11',
      肉: '12',
      家常: '13',
      清淡: '14',
      菌类: '15',
      梦幻: '16',
    },
  };
}

function buildRuntimeDataSignature() {
  return [
    '1',
    'mock-local-api',
    'mock runtime data complete',
    recipes.length,
    ingredients.length,
    beverages.length,
    normalCustomers.length,
    rareCustomers.length,
    6,
  ].join('|');
}

function buildTrackedMissionsResponse(params) {
  if (params.get('knownSignature') === MOCK_TRACKED_MISSIONS_SIGNATURE) {
    return {
      unchanged: true,
      contentSignature: MOCK_TRACKED_MISSIONS_SIGNATURE,
    };
  }

  return {
    ok: true,
    runtimeAvailable: true,
    generation: 1,
    status: 'ready',
    contentSignature: MOCK_TRACKED_MISSIONS_SIGNATURE,
    unverifiedCount: 1,
    trackingCount: 1,
    fulfilledCount: 1,
    missions: [
      {
        label: 'CORE_MOCK_FULFILLED',
        title: '琪露诺的招待练习',
        receiverLabel: 'Cirno',
        characterName: '琪露诺',
        sceneNames: ['雾之湖'],
        presentationStatus: 'ready',
        status: 'fulfilled',
        conditionCount: 2,
        completedConditionCount: 2,
        conditionStates: [true, true],
      },
      {
        label: 'CORE_MOCK_TRACKING',
        title: '阿求的料理委托',
        receiverLabel: 'Akyuu',
        characterName: '稗田阿求',
        sceneNames: ['人间之里', '博丽神社'],
        presentationStatus: 'ready',
        status: 'tracking',
        conditionCount: 3,
        completedConditionCount: 1,
        conditionStates: [true, false, false],
      },
      {
        label: 'CORE_MOCK_UNVERIFIED',
        title: '待确认的夜间任务',
        receiverLabel: '',
        characterName: '',
        sceneNames: [],
        presentationStatus: 'no-receiver',
        status: 'unverified',
        conditionCount: 2,
        completedConditionCount: null,
        conditionStates: [null, null],
      },
    ],
  };
}

function buildAvailableMissionsResponse(params) {
  if (params.get('knownSignature') === MOCK_AVAILABLE_MISSIONS_SIGNATURE) {
    return {
      unchanged: true,
      contentSignature: MOCK_AVAILABLE_MISSIONS_SIGNATURE,
    };
  }

  return {
    ok: true,
    runtimeAvailable: true,
    status: 'ready',
    missionGeneration: 1,
    daySceneGeneration: 1,
    contentSignature: MOCK_AVAILABLE_MISSIONS_SIGNATURE,
    availableCount: 2,
    missions: [
      {
        label: 'Kizuna_Meirin_LV2_Upgrade_Mission',
        title: '请美铃小姐品尝一下「白果萝卜排骨汤」吧！',
        receiverLabel: 'Meirin',
        characterName: '红美铃',
        sceneNames: ['红魔馆'],
        presentationStatus: 'ready',
      },
      {
        label: 'CORE_MOCK_TRACKING',
        title: '这条可接取记录应由已追踪任务覆盖',
        receiverLabel: 'Akyuu',
        characterName: '稗田阿求',
        sceneNames: ['人间之里', '博丽神社'],
        presentationStatus: 'ready',
      },
    ],
    error: null,
  };
}

function buildInvitationResponse(path, params) {
  const scope = normalizeScope(params.get('scope'));
  const targetGuestId = Number(params.get('guestId') || 0);
  const allCandidates = [
    invitation(1001, '米斯蒂娅', true, 4, '已在座位上，可重复校验', false),
    invitation(1002, '露米娅', true, 3, '当前场景满足羁绊条件', true),
    invitation(1003, '慧音', false, 5, '非当前场景，但全部场景可邀请', true),
    invitation(1004, '莉格露', true, 2, '当前场景满足羁绊条件', true),
    invitation(10, '雾雨魔理沙', false, 5, '映射身份使用原生角色 ID', true, 'DLC1_Marisa'),
  ];
  const candidates = scope === 'all' ? allCandidates : allCandidates.filter((entry) => entry.isCurrentScene);
  const available = candidates.filter((entry) => entry.canInvite);
  const invited = path === '/rare-guests/invite'
    ? available.filter((entry) => entry.id === targetGuestId)
    : path === '/rare-guests/invite-all'
      ? available
      : [];
  const skipped = candidates.filter((entry) => !entry.canInvite || (path === '/rare-guests/invite' && entry.id !== targetGuestId));

  return {
    ok: true,
    runtimeAvailable: true,
    status: path.endsWith('invitations') ? 'mock invitation candidates loaded' : `mock invited ${invited.length}`,
    error: null,
    candidateCount: candidates.length,
    usableCount: available.length,
    existingSlotCount: 1,
    existingControlledCount: 1,
    scheduledSlotCount: 0,
    invitedCount: invited.length,
    skippedCount: skipped.length,
    source: 'mock-local-api',
    diagnostics: 'mock response for Playwright UI audit',
    scope,
    currentMapLabel: '妖怪兽道',
    currentMapName: '妖怪兽道',
    candidates,
    available,
    invited,
    skipped,
  };
}

function buildOrderActionResponse(params) {
  const recipeName = params.get('recipeName') || params.get('food') || '蜂蜜蛋糕';
  const beverageName = params.get('beverageName') || '果味米酒';
  const deskCode = Number(params.get('deskCode') || 1);
  const guestIdText = params.get('guestId') || '';
  return {
    ok: true,
    prepared: true,
    servedFood: false,
    servedBeverage: true,
    completedOrder: false,
    error: null,
    order: {
      deskCode: Number.isFinite(deskCode) ? deskCode : 1,
      guestId: guestIdText ? Number(guestIdText) : null,
      guestName: params.get('guestName') || 'Mock Guest',
      foodTag: params.get('foodTag') || '',
      beverageTag: params.get('beverageTag') || '',
    },
    recipeId: Number(params.get('recipeId') || params.get('foodId') || -1),
    recipeName,
    beverageId: Number(params.get('beverageId') || -1),
    beverageName,
    automation: {
      outcome: 'progressed',
      stage: 'cooking-start',
      reasonCode: 'cooking-started',
      jobId: 'CJ-MOCK-000001',
      retryAfterMs: 0,
    },
    steps: [
      { code: 'beverage-delivered', name: 'ensure-beverage', ok: true, skipped: false, message: `mock served ${beverageName}` },
      { code: 'cooking-started', name: 'ensure-cooking', ok: true, skipped: false, message: `mock started ${recipeName}` },
    ],
  };
}

function buildMockAutomationCookingJob(response, path, params) {
  const now = nowIso();
  return {
    jobId: response.automation.jobId,
    targetKind: path === '/orders/normal/complete-first' ? 'normal' : 'rare',
    traceId: params.get('traceId') || '',
    orderKey: params.get('orderKey') || '',
    deskCode: response.order.deskCode,
    guestId: response.order.guestId,
    guestName: response.order.guestName,
    foodId: Number(params.get('foodId') || -1),
    foodName: response.recipeName,
    recipeId: response.recipeId,
    state: 'cooking',
    outcome: 'progressed',
    reasonCode: 'cooking-started',
    specialTargetRevision: 0,
    autoDeliverFood: params.get('autoCollectCooking') === 'true' || params.get('autoDeliverFood') === 'true',
    controllerId: 'mock-cooker-1',
    resultId: 'mock-result-1',
    generation: 1,
    contentRevision: 1,
    cookerPhase: 1,
    cookerProgress: 0.25,
    ownershipObservationFailures: 0,
    regressiveObservations: 0,
    deliveryFailureAttempts: 0,
    manualHandoffReadFailures: 0,
    warmerStoreCommitted: false,
    warmerStoreCommitUncertain: false,
    warmerResetAttempts: 0,
    foodDeliveryCommitted: false,
    foodDeliveryCommitUncertain: false,
    foodDeliveryCleanupAttempts: 0,
    startedAtUtc: now,
    lastObservedAtUtc: now,
    lastProgressAtUtc: now,
  };
}

function mutateRecipeFavorite(params) {
  const customerId = Number(params.get('customerId') || 0);
  const foodTag = params.get('foodTag') || '';
  const recipeId = Number(params.get('recipeId') || 0);
  const extraIngredientIds = (params.get('extraIngredientIds') || '')
    .split(',')
    .map((value) => Number(value))
    .filter(Number.isFinite);
  const id = `mock-recipe-${customerId}-${foodTag}-${recipeId}-${extraIngredientIds.join('-')}`;
  if (!favoriteData.recipes.some((entry) => entry.id === id)) {
    favoriteData.recipes.push({
      id,
      customerId,
      customerName: params.get('customerName') || `#${customerId}`,
      foodTag,
      recipeId,
      extraIngredientIds,
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    });
  }
  return { ok: true, favorites: favoriteData, error: null };
}

function mutateBeverageFavorite(params) {
  const customerId = Number(params.get('customerId') || 0);
  const beverageTag = params.get('beverageTag') || '';
  const beverageId = Number(params.get('beverageId') || 0);
  const id = `mock-beverage-${customerId}-${beverageTag}-${beverageId}`;
  if (!favoriteData.beverages.some((entry) => entry.id === id)) {
    favoriteData.beverages.push({
      id,
      customerId,
      customerName: params.get('customerName') || `#${customerId}`,
      beverageTag,
      beverageId,
      createdAtUtc: nowIso(),
      updatedAtUtc: nowIso(),
    });
  }
  return { ok: true, favorites: favoriteData, error: null };
}

function upsertCustomRecipe(params) {
  const id = (params.get('id') || '').trim();
  const customerId = Number(params.get('customerId'));
  const foodId = Number(params.get('foodId'));
  if (!Number.isFinite(customerId) || !Number.isFinite(foodId)) {
    return { ok: false, customRecipes: customRecipeData, error: 'invalid custom recipe parameters' };
  }

  const now = nowIso();
  const existingIndex = customRecipeData.recipes.findIndex((item) => item.id === id);
  const existing = existingIndex >= 0 ? customRecipeData.recipes[existingIndex] : null;
  const enabled = parseOptionalBoolean(params.get('enabled'));
  const pinToTop = parseOptionalBoolean(params.get('pinToTop'));
  const entry = {
    id: id || `mock-custom-${Date.now()}`,
    customerId,
    customerName: params.get('customerName') || '',
    foodTag: normalizeOptionalText(params.get('foodTag')),
    foodId,
    recipeId: Number(params.get('recipeId') || -1),
    recipeName: params.get('recipeName') || '',
    extraIngredientIds: parseIdList(params.get('extraIngredientIds') || ''),
    enabled: enabled ?? existing?.enabled ?? true,
    pinToTop: pinToTop ?? existing?.pinToTop ?? true,
    sortOrder: Number(params.get('sortOrder') || nextCustomRecipeSortOrder()),
    createdAtUtc: now,
    updatedAtUtc: now,
  };

  if (existingIndex >= 0) {
    entry.createdAtUtc = customRecipeData.recipes[existingIndex].createdAtUtc;
    customRecipeData.recipes[existingIndex] = entry;
  } else {
    customRecipeData.recipes.push(entry);
  }
  normalizeCustomRecipeSortOrders();
  return { ok: true, customRecipes: customRecipeData, error: null };
}

function setCustomRecipesEnabled(params) {
  const enabled = parseOptionalBoolean(params.get('enabled'));
  if (enabled === null) {
    return { ok: false, customRecipes: customRecipeData, error: 'invalid custom recipe enabled setting' };
  }
  customRecipeData.enabled = enabled;
  return { ok: true, customRecipes: customRecipeData, error: null };
}

function updateCustomRecipeFlags(params) {
  const scope = params.get('scope');
  const entries = customRecipeData.recipes.filter((entry) => {
    if (scope === 'all') return true;
    if (scope === 'entry') return entry.id === params.get('id');
    if (scope === 'customer') return entry.customerId === Number(params.get('customerId'));
    if (scope === 'recipe') return entry.foodId === Number(params.get('foodId'));
    return false;
  });
  const enabled = parseOptionalBoolean(params.get('enabled'));
  const pinToTop = parseOptionalBoolean(params.get('pinToTop'));
  if (entries.length === 0) {
    return { ok: false, customRecipes: customRecipeData, error: 'custom recipe selection matched no entries' };
  }
  if (enabled === null && pinToTop === null) {
    return { ok: false, customRecipes: customRecipeData, error: 'no custom recipe flags specified' };
  }

  const updatedAtUtc = nowIso();
  for (const entry of entries) {
    if (enabled !== null) entry.enabled = enabled;
    if (pinToTop !== null) entry.pinToTop = pinToTop;
    entry.updatedAtUtc = updatedAtUtc;
  }
  return { ok: true, customRecipes: customRecipeData, error: null };
}

function moveCustomRecipe(params) {
  const entry = customRecipeData.recipes.find((item) => item.id === params.get('id'));
  if (!entry) return { ok: false, customRecipes: customRecipeData, error: 'custom recipe not found' };
  const ordered = customRecipeData.recipes
    .filter((item) => item.customerId === entry.customerId)
    .sort(compareCustomRecipeEntries);
  const index = ordered.findIndex((item) => item.id === entry.id);
  if (index < 0) return { ok: false, customRecipes: customRecipeData, error: 'custom recipe not found' };
  const direction = params.get('direction');
  if (direction !== 'up' && direction !== 'down') {
    return { ok: false, customRecipes: customRecipeData, error: 'invalid custom recipe move direction' };
  }
  const targetIndex = direction === 'up' ? index - 1 : index + 1;
  if (targetIndex >= 0 && targetIndex < ordered.length) {
    [ordered[index].sortOrder, ordered[targetIndex].sortOrder] = [ordered[targetIndex].sortOrder, ordered[index].sortOrder];
    ordered[index].updatedAtUtc = nowIso();
    ordered[targetIndex].updatedAtUtc = nowIso();
  }
  normalizeCustomRecipeSortOrders();
  return { ok: true, customRecipes: customRecipeData, error: null };
}

function parseOptionalBoolean(value) {
  if (value === 'true' || value === '1') return true;
  if (value === 'false' || value === '0') return false;
  return null;
}

function nextCustomRecipeSortOrder() {
  return customRecipeData.recipes.length === 0
    ? 100
    : Math.max(...customRecipeData.recipes.map((entry) => entry.sortOrder || 0)) + 100;
}

function normalizeCustomRecipeSortOrders() {
  customRecipeData.recipes.sort(compareCustomRecipeEntries);
}

function compareCustomRecipeEntries(left, right) {
  if (left.sortOrder !== right.sortOrder) return left.sortOrder - right.sortOrder;
  return String(left.id).localeCompare(String(right.id));
}

function setInventoryQuantity(params) {
  const type = normalizeInventoryType(params.get('type'));
  const id = Number(params.get('id') || 0);
  const quantity = normalizeQuantity(params.get('qty'));
  const previousQuantity = Number(inventory[type][id] || 0);
  inventory[type][id] = quantity;
  return {
    ok: true,
    type,
    id,
    requestedQuantity: quantity,
    previousQuantity,
    quantity,
    changed: previousQuantity !== quantity,
    error: null,
  };
}

function setBulkInventoryQuantity(params) {
  const type = normalizeInventoryType(params.get('type'));
  const quantity = normalizeQuantity(params.get('qty'));
  const ids = (params.get('ids') || '')
    .split(',')
    .map((value) => Number(value))
    .filter(Number.isFinite);
  let changed = 0;
  let unchanged = 0;
  for (const id of ids) {
    if (Number(inventory[type][id] || 0) === quantity) {
      unchanged += 1;
    } else {
      inventory[type][id] = quantity;
      changed += 1;
    }
  }
  return {
    ok: true,
    type,
    requestedQuantity: quantity,
    total: ids.length,
    changed,
    unchanged,
    failed: 0,
    errors: [],
    error: null,
  };
}

function applyLogSettings(params) {
  if (params.has('aggregateLog')) logSettings.aggregateModLogEnabled = params.get('aggregateLog') === 'true';
  if (params.has('aggregateLogMaxFiles')) {
    const nextMaxFiles = Number.parseInt(params.get('aggregateLogMaxFiles') || '', 10);
    if (Number.isFinite(nextMaxFiles)) {
      logSettings.aggregateModLogMaxFileCount = Math.max(1, Math.min(9999, nextMaxFiles));
      logSettings.aggregateModLogMaxTotalBytes = logSettings.aggregateModLogMaxFileBytes * logSettings.aggregateModLogMaxFileCount;
    }
  }
}

function readOptionalMockBoolean(value) {
  if (value === 'true') return true;
  if (value === 'false') return false;
  return null;
}

function applyBepInExConsoleVisibility(params) {
  const visible = params.get('visible');
  if (visible !== 'true' && visible !== 'false') {
    return buildBepInExConsoleVisibilityResponse(
      false,
      'visible 必须为 true 或 false。',
    );
  }

  if (!logSettings.bepInExConsoleSupported) {
    return buildBepInExConsoleVisibilityResponse(
      false,
      '当前平台不支持 BepInEx 控制台窗口。',
    );
  }

  const nextVisible = visible === 'true';
  logSettings.bepInExConsoleConfiguredVisible = nextVisible;
  logSettings.bepInExConsoleActive ||= nextVisible;
  logSettings.bepInExConsoleVisible = nextVisible;
  logSettings.bepInExConsoleStatus = nextVisible
    ? 'visible'
    : logSettings.bepInExConsoleActive
      ? 'hidden'
      : 'inactive';
  return buildBepInExConsoleVisibilityResponse(true, null);
}

function buildBepInExConsoleVisibilityResponse(ok, error) {
  return {
    ok,
    supported: logSettings.bepInExConsoleSupported,
    configuredVisible: logSettings.bepInExConsoleConfiguredVisible,
    active: logSettings.bepInExConsoleActive,
    visible: logSettings.bepInExConsoleVisible,
    status: logSettings.bepInExConsoleStatus,
    error,
  };
}

function ingredient(id, name, tags, price, type) {
  return {
    id,
    name,
    description: `Mock ingredient: ${name}`,
    type,
    tags,
    dlc: 0,
    level: 1,
    price,
    from: { mock: true },
  };
}

function beverage(id, name, tags, price) {
  return {
    id,
    name,
    description: `Mock beverage: ${name}`,
    tags,
    dlc: 0,
    level: 1,
    price,
    from: { mock: true },
  };
}

function recipe(id, recipeId, name, requiredIngredients, positiveTags, cooker, price) {
  return {
    id,
    recipeId,
    name,
    description: `Mock recipe: ${name}`,
    ingredients: requiredIngredients,
    positiveTags,
    negativeTags: [],
    cooker,
    baseCookTime: 7,
    dlc: 0,
    level: 1,
    price,
    from: { mock: true },
  };
}

function normalCustomer(id, name, places, positiveTags, beverageTags) {
  return {
    id,
    name,
    description: `Mock normal customer: ${name}`,
    dlc: 0,
    places,
    positiveTags,
    beverageTags,
  };
}

function rareCustomer(id, name, places, positiveTags, beverageTags, negativeTags) {
  return {
    id,
    name,
    description: `Mock rare customer: ${name}`,
    dlc: 0,
    places,
    price: [120, 380],
    enduranceLimit: 3,
    positiveTags,
    negativeTags,
    beverageTags,
    positiveTagMapping: {},
    beverageTagMapping: {},
    collection: false,
    evaluation: {},
    spellCards: { positive: [], negative: [] },
  };
}

function invitation(id, name, isCurrentScene, kizunaLevel, reason, canInvite, runtimeName = name) {
  return {
    id,
    name,
    runtimeName,
    reason,
    status: canInvite ? '可邀请' : '已在队列/座位中',
    canInvite,
    isCurrentScene,
    kizunaLevel,
    sceneLabels: isCurrentScene ? ['妖怪兽道'] : ['人间之里'],
    sceneNames: isCurrentScene ? ['妖怪兽道'] : ['人间之里'],
  };
}

function removeFavorite(entries, id) {
  const index = entries.findIndex((entry) => entry.id === id);
  if (index >= 0) entries.splice(index, 1);
}

function normalizeScope(value) {
  return value === 'all' ? 'all' : 'current';
}

function normalizeInventoryType(value) {
  return value === 'beverage' ? 'beverage' : 'ingredient';
}

function normalizeQuantity(value) {
  const quantity = Number(value || 0);
  if (!Number.isFinite(quantity)) return 0;
  return Math.max(0, Math.min(999, Math.trunc(quantity)));
}

function parseIdList(value) {
  return [...new Set(String(value)
    .split(',')
    .map((part) => Number(part.trim()))
    .filter((id) => Number.isFinite(id) && id >= 0))]
    .sort((left, right) => left - right);
}

function normalizeOptionalText(value) {
  const trimmed = String(value || '').trim();
  return trimmed ? trimmed : null;
}

function normalizeBoolean(value, fallback) {
  if (value === 'true' || value === '1') return true;
  if (value === 'false' || value === '0') return false;
  return fallback;
}

function normalizeLanHost(value) {
  const normalized = String(value || '').trim();
  if (!normalized) return 'auto';
  return normalized.toLowerCase() === 'auto' ? 'auto' : normalized;
}

function readAutomationLease(request) {
  const identity = readClientIdentity(request);
  if (identity.error) {
    return {
      ok: false,
      owned: false,
      clientId: identity.clientId,
      clientLabel: identity.clientLabel,
      ownerClientId: '',
      ownerLabel: '',
      ownerLastSeenUtc: '',
      expiresAtUtc: '',
      ttlMs: AUTOMATION_LEASE_TTL_MS,
      error: identity.error,
    };
  }

  pruneAutomationLease();
  return buildAutomationLease(identity.clientId, identity.clientLabel, null);
}

function acquireAutomationLease(request) {
  const identity = readClientIdentity(request);
  if (identity.error) {
    return {
      ok: false,
      owned: false,
      clientId: identity.clientId,
      clientLabel: identity.clientLabel,
      ownerClientId: '',
      ownerLabel: '',
      ownerLastSeenUtc: '',
      expiresAtUtc: '',
      ttlMs: AUTOMATION_LEASE_TTL_MS,
      error: identity.error,
    };
  }

  pruneAutomationLease();
  if (automationLease && automationLease.clientId !== identity.clientId) {
    return buildAutomationLease(
      identity.clientId,
      identity.clientLabel,
      `自动化当前由 ${automationLease.clientLabel} 控制，本窗口仅查看。`,
    );
  }

  const now = Date.now();
  if (!automationLease) automationCommandEpoch += 1;
  automationLease = {
    clientId: identity.clientId,
    clientLabel: identity.clientLabel,
    lastSeenAt: now,
    expiresAt: now + AUTOMATION_LEASE_TTL_MS,
  };
  return buildAutomationLease(identity.clientId, identity.clientLabel, null);
}

function acknowledgeAutomationSafetyBarrier(request, params) {
  const identity = readClientIdentity(request);
  const sequence = Number(params.get('sequence') || 0);
  pruneAutomationLease();
  if (identity.error || automationLease?.clientId !== identity.clientId) {
    return {
      ok: false,
      sequence,
      acknowledgedCount: 0,
      acknowledgedSequences: [],
      status: '',
      error: identity.error || 'automation lease is not owned',
    };
  }
  if (!Number.isSafeInteger(sequence) || sequence <= 0) {
    return {
      ok: false,
      sequence,
      acknowledgedCount: 0,
      acknowledgedSequences: [],
      status: '',
      error: 'automation barrier sequence must be a positive integer',
    };
  }

  const selected = automationSafetyBarriers.get(sequence);
  if (!selected) {
    return {
      ok: false,
      sequence,
      acknowledgedCount: 0,
      acknowledgedSequences: [],
      status: '',
      error: 'automation safety barrier was not found',
    };
  }

  const acknowledgedSequences = [...automationSafetyBarriers.values()]
    .filter((barrier) => barrier.targetIdentity === selected.targetIdentity && barrier.sequence <= sequence)
    .map((barrier) => barrier.sequence)
    .sort((left, right) => left - right);
  for (const acknowledgedSequence of acknowledgedSequences) {
    automationSafetyBarriers.delete(acknowledgedSequence);
  }
  return {
    ok: true,
    sequence,
    acknowledgedCount: acknowledgedSequences.length,
    acknowledgedSequences,
    status: `mock acknowledged ${acknowledgedSequences.length} automation safety barriers`,
    error: null,
  };
}

function cancelAutomationAndReleaseLease(request) {
  const identity = readClientIdentity(request);
  pruneAutomationLease();
  if (identity.error || automationLease?.clientId !== identity.clientId) {
    return {
      ok: false,
      status: '',
      error: identity.error || 'automation lease is not owned',
      commandEpoch: automationCommandEpoch,
      cancelledJobs: 0,
      cancelledCommands: 0,
      leaseReleased: false,
    };
  }

  automationCommandEpoch += 1;
  const cancelledJobs = automationCookingJobs.length;
  automationCookingJobs = [];
  automationLease = null;
  return {
    ok: true,
    status: 'automation cancelled and lease released',
    error: null,
    commandEpoch: automationCommandEpoch,
    cancelledJobs,
    cancelledCommands: 0,
    leaseReleased: true,
  };
}

function buildAutomationLease(clientId, clientLabel, error) {
  return {
    ok: !error,
    owned: automationLease?.clientId === clientId,
    clientId,
    clientLabel,
    ownerClientId: automationLease?.clientId || '',
    ownerLabel: automationLease?.clientLabel || '',
    ownerLastSeenUtc: automationLease ? new Date(automationLease.lastSeenAt).toISOString() : '',
    expiresAtUtc: automationLease ? new Date(automationLease.expiresAt).toISOString() : '',
    ttlMs: AUTOMATION_LEASE_TTL_MS,
    error,
  };
}

function pruneAutomationLease() {
  if (automationLease && automationLease.expiresAt <= Date.now()) {
    automationLease = null;
  }
}

function readClientIdentity(request) {
  const clientId = String(request.headers['x-mystia-steward-companion-client-id'] || '').trim();
  if (!/^[a-zA-Z0-9-]{16,64}$/.test(clientId)) {
    return {
      clientId: '',
      clientLabel: '伴随窗口',
      error: '自动化请求缺少有效客户端 ID。',
    };
  }

  const rawLabel = String(request.headers['x-mystia-steward-companion-client-label'] || '').trim();
  return {
    clientId,
    clientLabel: rawLabel ? rawLabel.slice(0, 48) : '伴随窗口',
    error: null,
  };
}

function buildConnectionConfig() {
  const lanState = resolveMockLanState();
  return {
    ok: true,
    localEndpoint: `http://127.0.0.1:${port}`,
    lanEnabled: connectionConfig.lanEnabled,
    lanRunning: lanState.lanRunning,
    lanBindHost: connectionConfig.lanBindHost,
    port,
    token: mockToken,
    lanEndpoints: lanState.lanEndpoints,
    lanError: lanState.lanError,
    error: null,
  };
}

function buildHealth() {
  const lanState = resolveMockLanState();
  return {
    ok: true,
    pluginVersion: '0.0.0-mock',
    nightBusinessGeneration: 0,
    nightBusinessLifecyclePhase: 'Inactive',
    runtimeNightBusinessLifecycleStatus: 'mock inactive',
    bindAddress: '127.0.0.1',
    port,
    authRequired: true,
    localEndpoint: `http://127.0.0.1:${port}`,
    lanEnabled: connectionConfig.lanEnabled,
    lanRunning: lanState.lanRunning,
    lanError: lanState.lanError,
  };
}

function resolveMockLanState() {
  const candidates = connectionConfig.lanBindHost === 'auto'
    ? MOCK_LAN_ENDPOINTS
    : MOCK_LAN_ENDPOINTS.filter((candidate) => candidate.address === connectionConfig.lanBindHost);
  const lanRunning = connectionConfig.lanEnabled && candidates.length > 0;
  const lanEndpoints = lanRunning
    ? candidates.map((candidate, index) => ({
      ...candidate,
      endpoint: `http://${candidate.address}:${port}`,
      recommended: index === 0,
    }))
    : [];
  return {
    lanRunning,
    lanEndpoints,
    lanError: connectionConfig.lanEnabled && !lanRunning
      ? `LAN host '${connectionConfig.lanBindHost}' is not assigned to an active network interface.`
      : null,
  };
}

function nowIso(offsetSeconds = 0) {
  return new Date(Date.now() + offsetSeconds * 1000).toISOString();
}

function setCorsHeaders(response) {
  response.setHeader('Access-Control-Allow-Origin', '*');
  response.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
  response.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Mystia-Steward-Companion-Token, X-Mystia-Steward-Companion-Client-Id, X-Mystia-Steward-Companion-Client-Label');
  response.setHeader('Access-Control-Max-Age', '86400');
}

function sendJson(response, statusCode, body) {
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
  });
  response.end(JSON.stringify(body));
}
