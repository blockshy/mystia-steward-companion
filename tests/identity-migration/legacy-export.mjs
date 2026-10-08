// Explicit, finite Storage API whitelist. Never enumerate a WebView database or read its token key.
const prefix = 'mystia-steward-companion-';
const strings = {
  endpoint: 'mod-api-endpoint', theme: 'theme-mode', navigation: 'mod-tab',
  focusSwitchBehavior: 'focus-switch-behavior', customRecipeGroupMode: 'custom-recipe-group-mode',
};
const numbers = {
  fontScalePercent: 'font-scale-percent', backgroundOpacity: 'background-opacity',
  contentOpacity: 'content-opacity', focusSwitchCooldownMs: 'focus-switch-cooldown-ms',
  focusRecipeLimit: 'service-focus-recipe-limit', focusBeverageLimit: 'service-focus-beverage-limit',
};
const booleans = {
  alwaysOnTop: 'always-on-top', gamepadNavigation: 'gamepad-navigation', showDebugDetails: 'show-debug-details',
  missionListModuleEnabled: 'mission-list-module-enabled',
  rareGuestInvitationModuleEnabled: 'rare-guest-invitation-module-enabled', focusCompact: 'service-focus-compact',
};
export function exportLegacySettings(storage, { platform, installationBinding, includeIdentity = false }) {
  if (!['windows', 'android'].includes(platform) || !/^[a-f0-9]{64}$/.test(installationBinding))
    throw new Error('An explicit source installation binding is required.');
  const settings = {};
  for (const [name, key] of Object.entries(strings)) {
    const value = storage.getItem(prefix + key);
    if (value !== null) settings[name] = value;
  }
  for (const [name, key] of Object.entries(numbers)) {
    const value = storage.getItem(prefix + key);
    if (value !== null) {
      if (!value.trim() || !Number.isFinite(Number(value))) throw new Error(`Malformed preference: ${name}`);
      settings[name] = Number(value);
    }
  }
  for (const [name, key] of Object.entries(booleans)) {
    const value = storage.getItem(prefix + key);
    if (value !== null) {
      if (!['1', '0', 'true', 'false'].includes(value)) throw new Error(`Malformed preference: ${name}`);
      settings[name] = value === '1' || value === 'true';
    }
  }
  const result = { schemaVersion: 1, kind: 'mystia-settings-export', source: { platform, origin: 'http://tauri.localhost', installationBinding }, settings };
  if (includeIdentity) {
    const clientId = storage.getItem(prefix + 'client-id');
    if (!clientId || !/^[A-Za-z0-9-]{16,64}$/.test(clientId)) throw new Error('Explicit identity export lacks a valid local identity.');
    result.deviceIdentity = { clientId };
  }
  const json = JSON.stringify(result);
  if (new TextEncoder().encode(json).length > 16384) throw new Error('Settings export exceeds the bound.');
  return json;
}
