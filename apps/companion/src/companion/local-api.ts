import { isTauriRuntime } from '@/lib/tauri-runtime';
import { readCompanionClientId, readCompanionClientLabel } from '@/companion/client-identity';

/**
 * 本地 API 请求参数。
 *
 * `tauriTimeoutMs` 只在桌面运行时生效，用于传给 Rust 侧 TCP 代理；浏览器开发模式使用
 * `AbortSignal` 控制超时。
 */
interface LocalApiRequestOptions {
  signal?: AbortSignal;
  tauriTimeoutMs?: number;
  method?: 'GET' | 'POST';
}

export async function readLocalApiJson<T>(
  endpoint: string,
  apiToken: string,
  path: string,
  options?: AbortSignal | LocalApiRequestOptions,
): Promise<T> {
  const targetEndpoint = `${endpoint}${path}`;
  const requestOptions = normalizeRequestOptions(options);
  const method = requestOptions.method ?? 'GET';
  const clientId = readCompanionClientId();
  const clientLabel = readCompanionClientLabel();
  if (isTauriRuntime()) {
    // 生产环境通过 Tauri command 访问回环 API，避免 WebView 直接访问 localhost 时受代理、CORS 或平台策略影响。
    const { invoke } = await import('@tauri-apps/api/core');
    const payload = await invoke<string>('request_local_api', {
      endpoint: targetEndpoint,
      token: apiToken,
      method,
      timeoutMs: requestOptions.tauriTimeoutMs,
      clientId,
      clientLabel,
    });
    return JSON.parse(payload) as T;
  }

  const headers = new Headers();
  if (apiToken) headers.set('X-Mystia-Steward-Companion-Token', apiToken);
  headers.set('X-Mystia-Steward-Companion-Client-Id', clientId);
  headers.set('X-Mystia-Steward-Companion-Client-Label', clientLabel);
  const response = await fetch(targetEndpoint, {
    cache: 'no-store',
    headers,
    method,
    signal: requestOptions.signal,
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
    return await readLocalApiJson<T>(endpoint, apiToken, path, {
      signal: abortController.signal,
      tauriTimeoutMs: timeoutMs,
    });
  } finally {
    window.clearTimeout(timeoutId);
  }
}

export async function writeLocalApiJsonWithTimeout<T>(
  endpoint: string,
  apiToken: string,
  path: string,
  timeoutMs: number,
): Promise<T> {
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), timeoutMs);

  try {
    return await readLocalApiJson<T>(endpoint, apiToken, path, {
      method: 'POST',
      signal: abortController.signal,
      tauriTimeoutMs: timeoutMs,
    });
  } finally {
    window.clearTimeout(timeoutId);
  }
}

function normalizeRequestOptions(options: AbortSignal | LocalApiRequestOptions | undefined): LocalApiRequestOptions {
  if (!options) return {};
  if (options instanceof AbortSignal) return { signal: options };
  return options;
}
