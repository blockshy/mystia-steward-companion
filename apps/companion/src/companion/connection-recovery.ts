import type { RuntimeDataCatalogSnapshot } from '@/lib/recommendation-data';

export const CONNECTION_RETRY_DELAYS_MS = [2000, 5000, 10000, 30000] as const;

export interface CompanionConnectionIdentity {
  endpoint: string;
  apiToken: string;
}

interface CompanionConnectionIdentityUpdate {
  endpoint?: string | null;
  apiToken?: string | null;
}

export interface CompanionConnectionIdentityResolution {
  changed: boolean;
  identity: CompanionConnectionIdentity;
}

interface AutomationLeaseOwnership {
  ok: boolean;
  owned: boolean;
}

export function resolveCompanionConnectionIdentity(
  current: CompanionConnectionIdentity,
  update: CompanionConnectionIdentityUpdate,
): CompanionConnectionIdentityResolution {
  const endpoint = update.endpoint?.trim() || current.endpoint;
  const apiToken = update.apiToken?.trim() || current.apiToken;
  if (endpoint === current.endpoint && apiToken === current.apiToken) {
    return { changed: false, identity: current };
  }

  return {
    changed: true,
    identity: { endpoint, apiToken },
  };
}

export function getConnectionRetryDelayMs(failureCount: number): number {
  const retryIndex = Math.max(0, Math.min(failureCount - 1, CONNECTION_RETRY_DELAYS_MS.length - 1));
  return CONNECTION_RETRY_DELAYS_MS[retryIndex];
}

export function updateUnavailableRuntimeData(
  current: RuntimeDataCatalogSnapshot | null,
  source: string,
  status: string,
): RuntimeDataCatalogSnapshot {
  if (current?.isComplete) return current;
  if (current && current.source === source && current.status === status) return current;
  return {
    isComplete: false,
    source,
    status,
    recipes: [],
    ingredients: [],
    beverages: [],
    normalCustomers: [],
    rareCustomers: [],
  };
}

export function buildAutomationLeaseConnectionKey(
  identity: CompanionConnectionIdentity,
  automationSessionId: string,
): string {
  const sessionId = automationSessionId.trim();
  return sessionId ? `${identity.endpoint}\n${identity.apiToken}\n${sessionId}` : '';
}

export function isAutomationLeaseOwnedForConnection(
  lease: AutomationLeaseOwnership | null,
  leaseBindingKey: string,
  connectionKey: string,
  revalidationRequired: boolean,
): boolean {
  return Boolean(
    !revalidationRequired
    && connectionKey
    && lease?.ok
    && lease.owned
    && leaseBindingKey === connectionKey,
  );
}
