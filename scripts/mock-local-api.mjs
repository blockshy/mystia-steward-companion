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

const host = process.env.MOCK_API_HOST || DEFAULT_HOST;
const port = Number(process.env.MOCK_API_PORT || DEFAULT_PORT);

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
  beverage(101, '果味米酒', ['水果', '低酒精'], 18),
  beverage(102, '冰镇啤酒', ['可加冰', '中酒精'], 24),
  beverage(103, '月都清酒', ['高级', '清酒'], 58),
  beverage(104, '热茶', ['无酒精', '可加热'], 10),
  beverage(105, '蜂蜜气泡水', ['甜', '无酒精'], 16),
];

const recipes = [
  recipe(201, '豆腐味噌', ['黄瓜', '蘑菇'], ['家常', '素', '清淡'], '煮锅', 26),
  recipe(202, '蜂蜜蛋糕', ['鸡蛋', '蜂蜜'], ['甜', '适合拍照', '招牌'], '料理台', 38),
  recipe(203, '烤鲑鱼', ['鲑鱼', '辣椒'], ['水产', '鲜', '清淡'], '烧烤架', 42),
  recipe(204, '牛肉火锅', ['牛肉', '辣椒', '蘑菇'], ['肉', '灼热', '力量涌现', '昂贵'], '煮锅', 62),
  recipe(205, '蘑菇拼盘', ['蘑菇', '黄瓜'], ['菌类', '家常', '鲜'], '蒸锅', 31),
  recipe(206, '月光团子', ['月光草', '蜂蜜'], ['梦幻', '甜', '高级'], '料理台', 78),
  recipe(207, '香辣烤肉', ['牛肉', '辣椒'], ['肉', '灼热', '力量涌现'], '烧烤架', 55),
  recipe(208, '清爽沙拉', ['黄瓜', '蜂蜜'], ['清爽', '素', '适合拍照'], '料理台', 22),
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
    101: 9,
    102: 4,
    103: 2,
    104: 14,
    105: 6,
  },
};

const logSettings = {
  logAccessEnabled: true,
  logOutputPath: '/tmp/mystia-steward-companion/mock/BepInEx.log',
  logOutputDirectory: '/tmp/mystia-steward-companion/mock',
  maxLogLines: 400,
  maxLogBytes: 131072,
  nightBusinessDiagnosticsEnabled: true,
  nightBusinessDiagnosticsPath: '/tmp/mystia-steward-companion/mock/night-business-diagnostics.log',
  nightBusinessDiagnosticsDirectory: '/tmp/mystia-steward-companion/mock',
  nativeBepInExConsoleEnabled: false,
  nativeBepInExConsoleVisible: false,
};

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
  checkedAtUtc: nowIso(),
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

const server = http.createServer((request, response) => {
  setCorsHeaders(response);

  if (request.method === 'OPTIONS') {
    response.writeHead(204);
    response.end();
    return;
  }

  const requestUrl = new URL(request.url || '/', `http://${host}:${port}`);
  const path = normalizePath(requestUrl.pathname);

  if (request.method === 'POST') {
    try {
      if (path === '/updates/check') {
        updateStatus.state = 'available';
        updateStatus.checkedAtUtc = nowIso();
        updateStatus.hasUpdate = true;
        updateStatus.error = null;
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
    sendJson(response, 405, { ok: false, error: 'Only GET is supported by the mock local API.' });
    return;
  }

  try {
    if (path === '/snapshot') {
      sendJson(response, 200, buildSnapshot());
      return;
    }

    if (path === '/favorites') {
      sendJson(response, 200, favoriteData);
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

    if (path === '/rare-guests/invitations' || path === '/rare-guests/invite-all' || path === '/rare-guests/invite') {
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

    if (path === '/logs') {
      sendJson(response, 200, buildLogResponse('BepInEx.log', buildRuntimeLogLines()));
      return;
    }

    if (path === '/logs/automation') {
      sendJson(response, 200, buildLogResponse('automation-jobs.log', buildAutomationLogLines()));
      return;
    }

    if (path === '/logs/settings') {
      sendJson(response, 200, logSettings);
      return;
    }

    if (path === '/logs/config') {
      applyLogSettings(requestUrl.searchParams);
      sendJson(response, 200, logSettings);
      return;
    }

    if (path === '/logs/open-folder') {
      const target = requestUrl.searchParams.get('target') || 'log';
      sendJson(response, 200, { ok: true, directory: `/tmp/mystia-steward-companion/mock/${target}`, error: null });
      return;
    }

    if (path === '/logs/export-diagnostics') {
      sendJson(response, 200, {
        ok: true,
        path: '/tmp/mystia-steward-companion/mock/diagnostics.zip',
        directory: '/tmp/mystia-steward-companion/mock',
        files: ['BepInEx.log', 'night-business-diagnostics.log', 'automation-jobs.log'],
        error: null,
      });
      return;
    }

    if (path === '/updates/status') {
      sendJson(response, 200, updateStatus);
      return;
    }

    if (path === '/orders/rare/dismiss') {
      sendJson(response, 200, { ok: true, removed: 1, status: 'mock rare order dismissed', error: null });
      return;
    }

    if (path === '/orders/prepare-next' || path === '/orders/complete-first' || path === '/orders/normal/complete-first') {
      sendJson(response, 200, buildOrderActionResponse(requestUrl.searchParams));
      return;
    }

    if (path === '/ui-pinning/target') {
      sendJson(response, 200, { ok: true, status: 'mock target accepted' });
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
  return {
    pluginVersion: '1.0.5-mock',
    capturedAtUtc: nowIso(),
    activeSceneName: 'NightScene.MockBusiness',
    activeDayMapLabel: '妖怪兽道',
    activeDayMapName: '妖怪兽道',
    runtimeLoaded: true,
    status: 'mock runtime snapshot',
    runtimeSource: 'mock-local-api',
    runtimeSceneReadinessStatus: 'ready',
    runtimeUiPinningStatus: 'mock pinned target accepted',
    recommendationState: {
      availableRecipeIds: recipes.map((item) => item.id),
      availableBeverageIds: beverages.map((item) => item.id),
      availableIngredientIds: ingredients.map((item) => item.id),
      availableRareCustomerIds: rareCustomers.map((item) => item.id),
      ownedIngredientQty: inventory.ingredient,
      ownedBeverageQty: inventory.beverage,
      placedCookerTypeIds: [1, 2, 3, 4, 5],
      placedCookers: [
        { controllerIndex: 0, typeIds: [1], typeNames: ['煮锅'], name: '煮锅 A', isOpen: false, source: 'mock' },
        { controllerIndex: 1, typeIds: [2], typeNames: ['烧烤架'], name: '烧烤架 B', isOpen: true, source: 'mock' },
        { controllerIndex: 2, typeIds: [4], typeNames: ['蒸锅'], name: '蒸锅 C', isOpen: false, source: 'mock' },
        { controllerIndex: 3, typeIds: [5], typeNames: ['料理台'], name: '料理台 D', isOpen: false, source: 'mock' },
      ],
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
        { deskCode: 3, guestId: 1002, guestName: '露米娅', source: 'mock', fund: 360, baseFundCarry: 220, maxFundCarry: 430, extraFundByBuff: 40, willPayMoney: true },
      ],
      orders: [
        {
          deskCode: 1,
          guestId: 1001,
          guestName: '米斯蒂娅',
          foodTagId: 11,
          foodTag: '甜',
          beverageTagId: 21,
          beverageTag: '水果',
          source: 'mock',
          firstSeenAtUtc: nowIso(-240),
          lastSeenAtUtc: nowIso(-12),
          hasServedFood: false,
          hasServedBeverage: true,
        },
        {
          deskCode: 3,
          guestId: 1002,
          guestName: '露米娅',
          foodTagId: 12,
          foodTag: '肉',
          beverageTagId: 22,
          beverageTag: '中酒精',
          source: 'mock',
          firstSeenAtUtc: nowIso(-120),
          lastSeenAtUtc: nowIso(-5),
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
          guestName: '妖怪鼠客',
          foodId: 205,
          foodName: '蘑菇拼盘',
          beverageId: 104,
          beverageName: '热茶',
          hasServedFood: false,
          hasServedBeverage: true,
          hasStoredFood: true,
          hasStoredFoodReceipt: false,
          storedFoodCount: 1,
          storedFoodStatus: '保温箱内已有同名料理',
          isFulfilled: false,
          firstSeenAtUtc: nowIso(-75),
          source: 'mock',
        },
        {
          orderKey: 'mock-normal-2',
          deskCode: 4,
          guestName: '兽道旅人',
          foodId: 208,
          foodName: '清爽沙拉',
          beverageId: 101,
          beverageName: '果味米酒',
          hasServedFood: false,
          hasServedBeverage: false,
          hasStoredFood: false,
          storedFoodCount: 0,
          storedFoodStatus: '',
          isFulfilled: false,
          firstSeenAtUtc: nowIso(-40),
          source: 'mock',
        },
      ],
      source: 'mock-normal-business',
      error: null,
    },
    runtimeMissions: {
      availableMissions: [
        mission('兽道试营业', '米斯蒂娅', ['妖怪兽道'], 'available', 202, '蜂蜜蛋糕'),
        mission('夜间巡回', '慧音', ['人间之里', '妖怪兽道'], 'tracking', 201, '豆腐味噌'),
        mission('食材宣传', '莉格露', ['妖怪兽道'], 'fulfilled', 205, '蘑菇拼盘'),
      ],
      serveTargets: [
        {
          guestId: 1001,
          guestName: '米斯蒂娅',
          guestLabel: '米斯蒂娅',
          missionLabel: '兽道试营业',
          missionTitle: '兽道试营业',
          recipeId: 202,
          recipeName: '蜂蜜蛋糕',
          status: 'available',
          source: 'mock',
        },
      ],
      source: 'mock-runtime-missions',
      error: null,
    },
    runtimeRareCustomers: rareCustomers.map((customer) => ({
      id: customer.id,
      runtimeStringId: `mock-${customer.id}`,
      name: customer.name,
      places: customer.places,
      positiveTags: customer.positiveTags,
      negativeTags: customer.negativeTags,
      beverageTags: customer.beverageTags,
      source: 'mock',
    })),
    runtimeData: {
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
    },
    performanceMs: {
      snapshot: 3,
      runtimeData: 6,
      recommendations: 4,
    },
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
    steps: [
      { name: 'ensure-beverage', ok: true, skipped: false, message: `mock served ${beverageName}` },
      { name: 'ensure-cooking', ok: true, skipped: false, message: `mock started ${recipeName}` },
    ],
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

function buildLogResponse(fileName, lines) {
  return {
    capturedAtUtc: nowIso(),
    path: `/tmp/mystia-steward-companion/mock/${fileName}`,
    exists: true,
    enabled: logSettings.logAccessEnabled,
    maxLines: logSettings.maxLogLines,
    maxBytes: logSettings.maxLogBytes,
    lines,
    error: null,
  };
}

function buildRuntimeLogLines() {
  return [
    '[Info   :MystiaStewardCompanion] Mock local API snapshot served.',
    '[Debug  :MystiaStewardCompanion] Night business scanner found 2 rare orders and 2 normal orders.',
    '[Info   :MystiaStewardCompanion] Runtime catalog includes 8 recipes, 8 ingredients, 5 beverages.',
    '[Warning:MystiaStewardCompanion] Mock low-stock warning: 月光草 <= 1.',
    '[Debug  :MystiaStewardCompanion] UI pinning target changed to 蜂蜜蛋糕 / 料理台.',
  ];
}

function buildAutomationLogLines() {
  return [
    '2026-06-14 19:30:01.125 prepare target=rare desk=1 orderKey=mock-rare-1 food=蜂蜜蛋糕 guest=米斯蒂娅 start mock preparation',
    '2026-06-14 19:30:02.412 beverage target=rare desk=1 orderKey=mock-rare-1 food=果味米酒 guest=米斯蒂娅 served beverage',
    '2026-06-14 19:30:04.018 prepare target=normal desk=2 orderKey=mock-normal-1 food=蘑菇拼盘 guest=妖怪鼠客 pending cooker',
    '2026-06-14 19:30:05.772 complete target=rare desk=3 orderKey=mock-rare-2 food=香辣烤肉 guest=露米娅 waiting tray',
  ];
}

function applyLogSettings(params) {
  if (params.has('logAccess')) logSettings.logAccessEnabled = params.get('logAccess') === 'true';
  if (params.has('diagnostics')) logSettings.nightBusinessDiagnosticsEnabled = params.get('diagnostics') === 'true';
  if (params.has('nativeConsole')) logSettings.nativeBepInExConsoleEnabled = params.get('nativeConsole') === 'true';
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

function recipe(id, name, requiredIngredients, positiveTags, cooker, price) {
  return {
    id,
    recipeId: id,
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

function mission(title, characterName, places, status, targetRecipeId, targetRecipeName) {
  return {
    label: title,
    title,
    characterLabel: characterName,
    characterName,
    places,
    source: 'mock',
    status,
    started: status !== 'available',
    finished: status === 'fulfilled',
    targetRecipeId,
    targetRecipeName,
  };
}

function invitation(id, name, isCurrentScene, kizunaLevel, reason, canInvite) {
  return {
    id,
    name,
    runtimeName: name,
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

function normalizePath(pathname) {
  if (pathname === '/') return '/';
  return pathname.replace(/\/+$/, '');
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

function nowIso(offsetSeconds = 0) {
  return new Date(Date.now() + offsetSeconds * 1000).toISOString();
}

function setCorsHeaders(response) {
  response.setHeader('Access-Control-Allow-Origin', '*');
  response.setHeader('Access-Control-Allow-Methods', 'GET, OPTIONS');
  response.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-Mystia-Steward-Companion-Token');
  response.setHeader('Access-Control-Max-Age', '86400');
}

function sendJson(response, statusCode, body) {
  response.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Cache-Control': 'no-store',
  });
  response.end(JSON.stringify(body));
}
