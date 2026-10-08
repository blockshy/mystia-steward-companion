import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
import { assertLockedNode } from './flutter-toolchain.mjs';
import { buildCurrentSharedProfileV4 } from '../tests/device-authority/current-v4-profile-fixture.mjs';

assertLockedNode();
const root = fileURLToPath(new URL('..', import.meta.url));
const output = process.argv[2];
assert(output && path.isAbsolute(output), 'Pass a new absolute fixture directory.');
await mkdir(output);
const source = await readFile(path.join(root, 'tests/identity-migration/legacy-export.mjs'), 'utf8');
const browser = await chromium.launch({ headless: true });
try {
  for (const platform of ['windows', 'android']) {
    const context = await browser.newContext();
    await context.route('**/*', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><meta charset=utf-8><title>isolated identity fixture</title>' }));
    const page = await context.newPage();
    await page.goto('http://tauri.localhost/identity-p0');
    await page.addScriptTag({ type: 'module', content: `${source}\nwindow.exportIdentityP0 = exportLegacySettings;` });
    await page.waitForFunction(() => typeof window.exportIdentityP0 === 'function');
    const result = await page.evaluate((platform) => {
      const seed = {
        'mod-api-endpoint': 'http://127.0.0.1:32145', 'theme-mode': 'dark', 'font-scale-percent': '115',
        'background-opacity': '0.96', 'content-opacity': '1', 'always-on-top': '1', 'gamepad-navigation': 'true',
        'mod-tab': 'settings:logs', 'client-id': 'p0-explicit-old-identity-001',
        'mod-api-token': 'P0-SYNTHETIC-NEVER-EXPORT', 'automation-enabled': '1', 'lease': 'P0-NOT-MIGRATED',
        'draft-endpoint': 'http://unapplied.invalid', 'unknown-future': 'must-not-export',
      };
      for (const [key, value] of Object.entries(seed)) localStorage.setItem(`mystia-steward-companion-${key}`, value);
      const reads = [];
      const storage = { getItem(key) {
        if (/token|lease|automation|draft|unknown/.test(key)) throw new Error('Export tried to read an excluded key.');
        reads.push(key); return localStorage.getItem(key);
      } };
      const settings = window.exportIdentityP0(storage, { platform, installationBinding: 'a'.repeat(64) });
      if (reads.some(key => key.endsWith('client-id'))) throw new Error('Default export read identity.');
      const identity = window.exportIdentityP0(storage, { platform, installationBinding: 'a'.repeat(64), includeIdentity: true });
      for (const [key, value] of Object.entries(seed)) if (localStorage.getItem(`mystia-steward-companion-${key}`) !== value) throw new Error('Export mutated old data.');
      return { settings, identity, reads: reads.length, origin: location.origin };
    }, platform);
    assert.equal(result.origin, 'http://tauri.localhost');
    assert(!result.settings.includes('P0-SYNTHETIC') && !result.settings.includes('deviceIdentity'));
    await writeFile(path.join(output, `${platform}.json`), result.settings, { flag: 'wx' });
    await writeFile(path.join(output, `${platform}-identity.json`), result.identity, { flag: 'wx' });
    await context.close();
  }
  await writeFile(path.join(output, 'old-profile.json'), JSON.stringify(buildCurrentSharedProfileV4({ automationEnabled: true, autoRareConcurrency: 4, managedRareGuestIds: [4, 9] })), { flag: 'wx' });
  await writeFile(path.join(output, 'defaults-profile.json'), JSON.stringify(buildCurrentSharedProfileV4()), { flag: 'wx' });
  await writeFile(path.join(output, 'export-evidence.json'), JSON.stringify({ schemaVersion: 1, kind: 'identity-export-browser-p0',
    result: 'PASS', platforms: ['windows', 'android'], runtime: 'isolated Chromium Storage API; not installed WebView2/Android',
    checks: ['explicit-whitelist', 'token-never-read', 'identity-opt-in', 'source-unchanged', 'same-origin-browser-storage'] }, null, 2), { flag: 'wx' });
  console.log('PASS: isolated browser Storage API whitelist export; no token read, default identity excluded, source unchanged.');
} finally {
  await browser.close();
}
