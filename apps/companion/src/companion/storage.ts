import {
  DEFAULT_FOCUS_RECOMMENDATION_ROWS,
  normalizeFocusRecommendationLimit,
} from '@/companion/preferences';
import type { ModTab, RareGuestInvitationScope } from '@/companion/types';

export const DEFAULT_ENDPOINT = 'http://127.0.0.1:32145';

const STORAGE_PREFIX = 'mystia-steward-companion';
const LEGACY_STORAGE_PREFIX = 'mystia-steward';
const ENDPOINT_STORAGE_KEY = `${STORAGE_PREFIX}-mod-api-endpoint`;
const TOKEN_STORAGE_KEY = `${STORAGE_PREFIX}-mod-api-token`;
const TAB_STORAGE_KEY = `${STORAGE_PREFIX}-mod-tab`;
const RARE_GUEST_INVITATION_SCOPE_STORAGE_KEY = `${STORAGE_PREFIX}-rare-guest-invitation-scope`;
const RARE_GUEST_INVITATION_LEVELS_STORAGE_KEY = `${STORAGE_PREFIX}-rare-guest-invitation-levels`;
const FOCUS_COMPACT_STORAGE_KEY = `${STORAGE_PREFIX}-service-focus-compact`;
const FOCUS_RECIPE_LIMIT_STORAGE_KEY = `${STORAGE_PREFIX}-service-focus-recipe-limit`;
const FOCUS_BEVERAGE_LIMIT_STORAGE_KEY = `${STORAGE_PREFIX}-service-focus-beverage-limit`;
const LEGACY_ENDPOINT_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-api-endpoint`;
const LEGACY_TOKEN_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-api-token`;
const LEGACY_TAB_STORAGE_KEY = `${LEGACY_STORAGE_PREFIX}-mod-tab`;

const VALID_MOD_TABS: ModTab[] = ['overview', 'normal', 'rare', 'custom-recipes', 'service', 'tasks', 'inventory', 'help', 'logs', 'settings'];

export function readStoredEndpoint(): string {
  return normalizeEndpoint(readMigratedStorage(ENDPOINT_STORAGE_KEY, LEGACY_ENDPOINT_STORAGE_KEY, DEFAULT_ENDPOINT));
}

export function persistEndpoint(endpoint: string) {
  localStorage.setItem(ENDPOINT_STORAGE_KEY, normalizeEndpoint(endpoint));
}

export function readStoredApiToken(): string {
  return readMigratedStorage(TOKEN_STORAGE_KEY, LEGACY_TOKEN_STORAGE_KEY, '');
}

export function persistApiToken(apiToken: string) {
  if (apiToken) localStorage.setItem(TOKEN_STORAGE_KEY, apiToken);
}

export function readStoredTab(): ModTab {
  const value = readMigratedStorage(TAB_STORAGE_KEY, LEGACY_TAB_STORAGE_KEY, '');
  return VALID_MOD_TABS.includes(value as ModTab) ? value as ModTab : 'service';
}

export function persistTab(tab: ModTab) {
  localStorage.setItem(TAB_STORAGE_KEY, tab);
}

export function readStoredRareGuestInvitationScope(): RareGuestInvitationScope {
  return localStorage.getItem(RARE_GUEST_INVITATION_SCOPE_STORAGE_KEY) === 'all' ? 'all' : 'current';
}

export function persistRareGuestInvitationScope(scope: RareGuestInvitationScope) {
  localStorage.setItem(RARE_GUEST_INVITATION_SCOPE_STORAGE_KEY, scope);
}

export function readStoredRareGuestInvitationLevels(): number[] {
  return normalizeRareGuestInvitationLevels(parseNumberList(localStorage.getItem(RARE_GUEST_INVITATION_LEVELS_STORAGE_KEY)));
}

export function persistRareGuestInvitationLevels(levels: number[]) {
  localStorage.setItem(RARE_GUEST_INVITATION_LEVELS_STORAGE_KEY, serializeRareGuestInvitationLevels(levels));
}

export function readStoredFocusCompact(): boolean {
  return readStoredBoolean(FOCUS_COMPACT_STORAGE_KEY, false);
}

export function persistFocusCompact(compact: boolean) {
  localStorage.setItem(FOCUS_COMPACT_STORAGE_KEY, compact ? '1' : '0');
}

export function readStoredFocusRecipeLimit(): number {
  return readStoredFocusLimit(FOCUS_RECIPE_LIMIT_STORAGE_KEY);
}

export function persistFocusRecipeLimit(limit: number) {
  localStorage.setItem(FOCUS_RECIPE_LIMIT_STORAGE_KEY, String(normalizeFocusRecommendationLimit(limit)));
}

export function readStoredFocusBeverageLimit(): number {
  return readStoredFocusLimit(FOCUS_BEVERAGE_LIMIT_STORAGE_KEY);
}

export function persistFocusBeverageLimit(limit: number) {
  localStorage.setItem(FOCUS_BEVERAGE_LIMIT_STORAGE_KEY, String(normalizeFocusRecommendationLimit(limit)));
}

export function normalizeEndpoint(value: string) {
  const trimmed = value.trim() || DEFAULT_ENDPOINT;
  const withProtocol = /^https?:\/\//i.test(trimmed) ? trimmed : `http://${trimmed}`;
  try {
    const url = new URL(withProtocol);
    if (url.hostname === 'localhost') url.hostname = '127.0.0.1';
    return url.toString().replace(/\/+$/, '');
  } catch {
    return trimmed.replace(/\/+$/, '');
  }
}

export function normalizeRareGuestInvitationLevels(levels: number[]): number[] {
  return Array.from(new Set(levels
    .map((level) => Math.trunc(level))
    .filter((level) => Number.isFinite(level) && level >= 0)))
    .sort((a, b) => a - b);
}

export function serializeRareGuestInvitationLevels(levels: number[]): string {
  return normalizeRareGuestInvitationLevels(levels).join(',');
}

export function toggleNumberInList(values: number[], value: number): number[] {
  return values.includes(value)
    ? values.filter((item) => item !== value)
    : normalizeRareGuestInvitationLevels([...values, value]);
}

function readStoredFocusLimit(key: string) {
  return normalizeFocusRecommendationLimit(Number(localStorage.getItem(key) ?? DEFAULT_FOCUS_RECOMMENDATION_ROWS));
}

function readStoredBoolean(key: string, fallback: boolean) {
  const value = localStorage.getItem(key);
  if (value === null) return fallback;
  return value === '1' || value === 'true';
}

function readMigratedStorage(key: string, legacyKey: string, fallback: string) {
  const value = localStorage.getItem(key);
  if (value !== null) return value;

  const legacyValue = localStorage.getItem(legacyKey);
  if (legacyValue === null) return fallback;

  localStorage.setItem(key, legacyValue);
  localStorage.removeItem(legacyKey);
  return legacyValue;
}

function parseNumberList(value: string | null | undefined): number[] {
  if (!value?.trim()) return [];
  return value
    .split(/[,\s;|]+/)
    .map((part) => Number(part))
    .filter((level) => Number.isFinite(level));
}
