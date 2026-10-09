import assert from 'node:assert/strict';
import { createServer } from 'vite';

// 运行真实前端偏好模块，使用内存 localStorage；不读取用户浏览器资料或游戏配置。
const values = new Map();
Object.defineProperty(globalThis, 'localStorage', { configurable: true, value: {
  getItem: (key) => values.get(key) ?? null,
  setItem: (key, value) => values.set(key, String(value)),
  removeItem: (key) => values.delete(key),
} });
const vite = await createServer({ configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom', logLevel: 'silent' });
let checks = 0;
function check(actual, expected, message) { assert.deepEqual(actual, expected, message); checks += 1; }
try {
  const preferences = await vite.ssrLoadModule('/src/companion/preferences.ts');
  check(preferences.SHARED_COMPANION_PREFERENCES_SCHEMA_VERSION, 5, '客户端必须只请求当前共享配置版本。');
  const prefix = 'mystia-steward-companion';
  values.set(`${prefix}-rare-guest-participation-module-enabled`, '1');
  values.set(`${prefix}-managed-rare-guest-ids`, '[23,7,23,2147483647]');
  const defaults = preferences.normalizeCompanionPreferences({});
  const previousSort = { ...defaults.recommendationSortProfile,
    objectives: defaults.recommendationSortProfile.objectives.filter((item) => item.key !== 'cookerAvailable') };
  values.set(`${prefix}-recommendation-sort-profile`, JSON.stringify(previousSort));
  const loaded = preferences.readStoredCompanionPreferences();
  check(loaded.rareGuestParticipationModuleEnabled, true, '必须继承历史模块开关。');
  check(loaded.managedRareGuestIds, [7, 23, 2147483647], '本地名单必须保留有效 ID，独立于当前目录。');
  check(loaded.recommendationSortProfile.objectives.find((item) => item.key === 'cookerAvailable').enabled, false,
    '本地八项目标升级不能悄悄开启新的厨具排序。');
  preferences.persistCompanionPreferences({ ...loaded, fontScalePercent: 110 });
  check(values.get(`${prefix}-rare-guest-participation-module-enabled`), '1', '保存窗口偏好不得丢弃参与模块。');
  check(values.get(`${prefix}-managed-rare-guest-ids`), '[7,23,2147483647]', '保存其他偏好不得清空旧名单。');
  const shared = preferences.readSharedCompanionPreferences(loaded);
  const before = JSON.stringify(shared);
  check(preferences.readWireSharedCompanionPreferences(shared), shared, '当前线路配置必须完整保留。');
  check(JSON.stringify(shared), before, '线路校验不能修改原始配置。');
  const disabled = preferences.normalizeSharedCompanionPreferences({ ...shared, rareGuestParticipationModuleEnabled: false });
  check(disabled.managedRareGuestIds, shared.managedRareGuestIds, '关闭模块不能清除历史名单。');
  check(preferences.readWireSharedCompanionPreferences({ ...shared,
    managedRareGuestIds: Array.from({ length: 512 }, (_, index) => index) }).managedRareGuestIds.length, 512,
  '512 个 ID 的合法边界必须保留。');
  // 服务端数据损坏必须显式报错；不能像本地编辑输入一样“修正”为更宽松的空名单。
  for (const ids of [null, {}, '7', [2, 1], [1, 1], [-1], [0.5], [2147483648], ['7'],
    Array.from({ length: 513 }, (_, index) => index)]) {
    assert.throws(() => preferences.readWireSharedCompanionPreferences({ ...shared, managedRareGuestIds: ids })); checks += 1;
  }
  for (const enabled of [undefined, null, 0, 'true']) {
    assert.throws(() => preferences.readWireSharedCompanionPreferences({ ...shared, rareGuestParticipationModuleEnabled: enabled })); checks += 1;
  }
  check(preferences.normalizeCompanionPreferences({}).rareGuestParticipationModuleEnabled, false,
    '没有历史配置的新设备不能默认开启新模块。');
  console.log(`PASS shared profile schema5: ${checks} assertions; historical keys, roster preservation, disabled objective and strict wire checks.`);
} finally {
  await vite.close();
  delete globalThis.localStorage;
}
