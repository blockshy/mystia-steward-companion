import { isTauriRuntime } from '@/lib/tauri-runtime';
import { readCompanionClientId, readCompanionClientLabel } from '@/companion/client-identity';

const NATIVE_LOCAL_API_ERROR_PREFIX = 'local-api:';

type NativeLocalApiErrorCode =
  | 'invalid-endpoint'
  | 'invalid-request'
  | 'connect-timeout'
  | 'connection-refused'
  | 'connect-failed'
  | 'read-timeout'
  | 'read-failed'
  | 'write-timeout'
  | 'write-failed'
  | 'unauthorized'
  | 'forbidden'
  | 'http-status'
  | 'invalid-response'
  | 'internal-error';

/**
 * 本地 API 请求参数。
 *
 * `tauriTimeoutMs` 只在 Tauri 运行时生效，用于传给 Rust 侧 TCP 代理；浏览器开发模式使用
 * `AbortSignal` 控制超时。
 */
interface LocalApiRequestOptions {
  signal?: AbortSignal;
  tauriTimeoutMs?: number;
}

type LocalApiMethod = 'GET' | 'POST';

export async function readLocalApiJson<T>(
  endpoint: string,
  apiToken: string,
  path: string,
  options?: AbortSignal | LocalApiRequestOptions,
): Promise<T> {
  return requestLocalApiJson<T>(endpoint, apiToken, path, 'GET', normalizeRequestOptions(options));
}

async function requestLocalApiJson<T>(
  endpoint: string,
  apiToken: string,
  path: string,
  method: LocalApiMethod,
  requestOptions: LocalApiRequestOptions,
): Promise<T> {
  const targetEndpoint = `${endpoint}${path}`;
  const clientId = readCompanionClientId();
  const clientLabel = readCompanionClientLabel();
  if (isTauriRuntime()) {
    // 所有 Tauri 平台统一使用原生 TCP 代理，避免 WebView 网络策略、CORS 和系统代理影响本地 API。
    const { invoke } = await import('@tauri-apps/api/core');
    let payload: string;
    try {
      payload = await invoke<string>('request_local_api', {
        endpoint: targetEndpoint,
        token: apiToken,
        method,
        timeoutMs: requestOptions.tauriTimeoutMs,
        clientId,
        clientLabel,
      });
    } catch (error) {
      throw translateNativeLocalApiError(error);
    }
    return parseLocalApiJson<T>(payload);
  }

  validateDirectFetchEndpoint(targetEndpoint);

  const headers = new Headers();
  if (apiToken) headers.set('X-Mystia-Steward-Companion-Token', apiToken);
  headers.set('X-Mystia-Steward-Companion-Client-Id', clientId);
  headers.set('X-Mystia-Steward-Companion-Client-Label', clientLabel);
  let response: Response;
  try {
    response = await fetch(targetEndpoint, {
      cache: 'no-store',
      headers,
      method,
      signal: requestOptions.signal,
    });
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error(nativeLocalApiErrorMessage(method === 'POST' ? 'write-timeout' : 'read-timeout'));
    }
    throw new Error(nativeLocalApiErrorMessage('connect-failed'));
  }
  if (!response.ok) {
    throw new Error(httpStatusErrorMessage(response.status));
  }

  return parseLocalApiJson<T>(await response.text());
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
  signal?: AbortSignal,
): Promise<T> {
  const abortController = new AbortController();
  const timeoutId = window.setTimeout(() => abortController.abort(), timeoutMs);
  const forwardAbort = () => abortController.abort();
  if (signal?.aborted) abortController.abort();
  else signal?.addEventListener('abort', forwardAbort, { once: true });

  try {
    return await requestLocalApiJson<T>(endpoint, apiToken, path, 'POST', {
      signal: abortController.signal,
      tauriTimeoutMs: timeoutMs,
    });
  } finally {
    window.clearTimeout(timeoutId);
    signal?.removeEventListener('abort', forwardAbort);
  }
}

function normalizeRequestOptions(options: AbortSignal | LocalApiRequestOptions | undefined): LocalApiRequestOptions {
  if (!options) return {};
  if (options instanceof AbortSignal) return { signal: options };
  return options;
}

function validateDirectFetchEndpoint(endpoint: string): void {
  let url: URL;
  try {
    url = new URL(endpoint);
  } catch {
    throw new Error(nativeLocalApiErrorMessage('invalid-endpoint'));
  }
  if (url.protocol !== 'http:') {
    throw new Error(nativeLocalApiErrorMessage('invalid-endpoint'));
  }

  const hostname = url.hostname.toLowerCase();
  const address = hostname === 'localhost' ? '127.0.0.1' : hostname;
  const octets = parseIpv4Octets(address);
  if (!octets || address === '0.0.0.0') {
    throw new Error(nativeLocalApiErrorMessage('invalid-endpoint'));
  }

  const [first, second] = octets;
  const allowed =
    first === 127 ||
    first === 10 ||
    (first === 172 && second >= 16 && second <= 31) ||
    (first === 192 && second === 168) ||
    (first === 169 && second === 254);

  if (!allowed) {
    throw new Error(nativeLocalApiErrorMessage('invalid-endpoint'));
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

function translateNativeLocalApiError(error: unknown): Error {
  const rawMessage = error instanceof Error ? error.message : String(error);
  if (!rawMessage.startsWith(NATIVE_LOCAL_API_ERROR_PREFIX)) {
    return new Error(`本地 API 原生代理调用失败：${rawMessage}`);
  }

  const encoded = rawMessage.slice(NATIVE_LOCAL_API_ERROR_PREFIX.length);
  const separatorIndex = encoded.indexOf(':');
  const code = (separatorIndex >= 0 ? encoded.slice(0, separatorIndex) : encoded) as NativeLocalApiErrorCode;
  const detail = separatorIndex >= 0 ? encoded.slice(separatorIndex + 1) : '';
  return new Error(nativeLocalApiErrorMessage(code, detail));
}

function nativeLocalApiErrorMessage(code: NativeLocalApiErrorCode, detail = ''): string {
  switch (code) {
    case 'invalid-endpoint':
      return '本地 API 地址无效。请填写 http:// 开头的回环或局域网 IPv4 地址，并包含端口。';
    case 'invalid-request':
      return '本地 API 请求参数无效。请确认伴随窗口与 Mod 来自同一版本。';
    case 'connect-timeout':
      return '连接本地 API 超时。请确认手机和电脑位于同一局域网，并检查电脑防火墙和路由器的客户端隔离设置。';
    case 'connection-refused':
      return '本地 API 拒绝连接。请确认游戏和 Mod 正在运行，并已开启局域网连接。';
    case 'connect-failed':
      return '无法连接本地 API。请确认地址属于电脑当前使用的局域网，并检查网络和防火墙。';
    case 'read-timeout':
      return '本地 API 响应超时。请确认游戏仍在运行并检查网络连接。';
    case 'read-failed':
      return '读取本地 API 响应失败。请检查网络连接后重试。';
    case 'write-timeout':
      return '向本地 API 发送请求超时。请检查网络连接后重试。';
    case 'write-failed':
      return '向本地 API 发送请求失败。请检查网络连接后重试。';
    case 'unauthorized':
      return '本地 API Token 验证失败（HTTP 401）。请重新复制游戏内当前 Token。';
    case 'forbidden':
      return '本地 API 拒绝了该操作（HTTP 403）。连接配置和 Token 重置只能在游戏所在电脑上执行。';
    case 'http-status':
      return `本地 API 返回异常状态${detail ? `（HTTP ${detail}）` : ''}。请查看 Mod 日志确认服务状态。`;
    case 'invalid-response':
      return '本地 API 返回了无效响应。请确认伴随窗口与 Mod 来自同一版本。';
    case 'internal-error':
      return '本地 API 原生代理运行失败。请重新启动伴随窗口。';
    default:
      return `本地 API 原生代理返回了未知错误${detail ? `：${detail}` : '。'}`;
  }
}

function httpStatusErrorMessage(status: number): string {
  if (status === 401) return nativeLocalApiErrorMessage('unauthorized');
  if (status === 403) return nativeLocalApiErrorMessage('forbidden');
  return nativeLocalApiErrorMessage('http-status', String(status));
}

function parseLocalApiJson<T>(payload: string): T {
  try {
    return JSON.parse(payload) as T;
  } catch {
    throw new Error(nativeLocalApiErrorMessage('invalid-response'));
  }
}
