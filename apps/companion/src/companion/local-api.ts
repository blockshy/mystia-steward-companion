import { isTauriRuntime } from '@/lib/tauri-runtime';

export async function readLocalApiJson<T>(
  endpoint: string,
  apiToken: string,
  path: string,
  signal?: AbortSignal,
): Promise<T> {
  const targetEndpoint = `${endpoint}${path}`;
  if (isTauriRuntime()) {
    const { invoke } = await import('@tauri-apps/api/core');
    const payload = await invoke<string>('fetch_snapshot', { endpoint: targetEndpoint, token: apiToken });
    return JSON.parse(payload) as T;
  }

  const headers = new Headers();
  if (apiToken) headers.set('X-Mystia-Steward-Companion-Token', apiToken);
  const response = await fetch(targetEndpoint, {
    cache: 'no-store',
    headers,
    signal,
  });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);

  return await response.json() as T;
}

export async function readLocalApiJsonWithTimeout<T>(
  endpoint: string,
  apiToken: string,
  path: string,
  timeoutMs: number,
): Promise<T> {
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), timeoutMs);

  try {
    return await readLocalApiJson<T>(endpoint, apiToken, path, abortController.signal);
  } finally {
    window.clearTimeout(timeoutId);
  }
}
