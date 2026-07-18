const UPDATE_NOTICE_SNOOZE_PREFIX = 'mystia-steward-companion:update-notice-snooze:';

export const UPDATE_NOTICE_SNOOZE_MS = 24 * 60 * 60 * 1000;

export interface UpdateNoticeStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export function buildUpdateNoticeSnoozeKey(endpoint: string, tag: string): string {
  const identity = `${normalizeUpdateNoticeEndpoint(endpoint)}\n${tag.trim()}`;
  return `${UPDATE_NOTICE_SNOOZE_PREFIX}${encodeURIComponent(identity)}`;
}

export function readUpdateNoticeSnoozeUntil(
  storage: UpdateNoticeStorage,
  endpoint: string,
  tag: string,
): number {
  if (!endpoint.trim() || !tag.trim()) return 0;

  try {
    const key = buildUpdateNoticeSnoozeKey(endpoint, tag);
    const snoozedUntil = Number(storage.getItem(key));
    if (!Number.isFinite(snoozedUntil) || snoozedUntil <= Date.now()) {
      return 0;
    }
    return snoozedUntil;
  } catch {
    return 0;
  }
}

export function persistUpdateNoticeSnooze(
  storage: UpdateNoticeStorage,
  endpoint: string,
  tag: string,
  snoozedUntil: number,
): void {
  if (!endpoint.trim() || !tag.trim() || !Number.isFinite(snoozedUntil)) return;

  try {
    storage.setItem(buildUpdateNoticeSnoozeKey(endpoint, tag), String(snoozedUntil));
  } catch {
    // localStorage may be unavailable in hardened WebViews; the current-session state still suppresses the notice.
  }
}

export function normalizeUpdateNoticeEndpoint(endpoint: string): string {
  const trimmed = endpoint.trim();
  if (!trimmed) return '';

  try {
    const parsed = new URL(trimmed);
    parsed.hash = '';
    parsed.search = '';
    parsed.pathname = parsed.pathname.replace(/\/+$/, '') || '/';
    return parsed.toString().replace(/\/$/, '');
  } catch {
    return trimmed.replace(/\/+$/, '');
  }
}
