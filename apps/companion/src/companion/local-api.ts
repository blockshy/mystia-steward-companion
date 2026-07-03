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
  if (isTauriRuntime() && !isAndroidRuntime()) {
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

  validateDirectFetchEndpoint(targetEndpoint);

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

function isAndroidRuntime(): boolean {
  return typeof navigator !== 'undefined' && /android/i.test(navigator.userAgent);
}

function validateDirectFetchEndpoint(endpoint: string): void {
  const url = new URL(endpoint);
  if (url.protocol !== 'http:') {
    throw new Error('Local API endpoint must use HTTP');
  }

  const hostname = url.hostname.toLowerCase();
  const address = hostname === 'localhost' ? '127.0.0.1' : hostname;
  const octets = parseIpv4Octets(address);
  if (!octets || address === '0.0.0.0') {
    throw new Error('Local API endpoint must be loopback or a private LAN IPv4 address');
  }

  const [first, second] = octets;
  const allowed =
    first === 127 ||
    first === 10 ||
    (first === 172 && second >= 16 && second <= 31) ||
    (first === 192 && second === 168) ||
    (first === 169 && second === 254);

  if (!allowed) {
    throw new Error('Local API endpoint must be loopback or a private LAN IPv4 address');
  }
}

function parseIpv4Octets(address: string): [number, number, number, number] | null {
  const parts = address.split('.');
  if (parts.length !== 4) return null;

  const octets = parts.map((part) => {
    if (!/^\d{1,3}$/.test(part)) return Number.NaN;
    const value = Number(part);
    return value >= 0 && value <= 255 ? value : Number.NaN;
  });

  return octets.every(Number.isInteger) ? octets as [number, number, number, number] : null;
}
