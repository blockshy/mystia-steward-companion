import { useCallback, useEffect, useRef, useState } from 'react';

import {
  readLocalApiConnectionConfig,
  regenerateLocalApiToken,
  writeLocalApiConnectionConfig,
} from '@/companion/api';
import { isLoopbackLocalApiEndpoint } from '@/companion/local-api-endpoint';
import type { LocalApiConnectionConfig } from '@/companion/types';

type ConnectionWriteAction = 'apply' | 'token';
interface ConfirmedConfig {
  identity: string;
  config: LocalApiConnectionConfig;
}

export interface LocalApiConnectionSettingsController {
  config: LocalApiConnectionConfig | null;
  lanEnabled: boolean;
  lanHost: string;
  busy: 'refresh' | ConnectionWriteAction | null;
  error: string;
  status: string;
  writable: boolean;
  refreshable: boolean;
  dirty: boolean;
  hostDirty: boolean;
  setLanHost: (value: string) => void;
  refresh: () => Promise<void>;
  apply: () => Promise<void>;
  setLanEnabled: (value: boolean) => Promise<void>;
  regenerateToken: () => Promise<void>;
}

/** Owned by the workbench so an in-flight write survives settings-page unmounts. */
export function useLocalApiConnectionSettings({
  endpoint,
  apiToken,
  connectionRevision,
  connected,
  active,
  onConnectionConfigApplied,
}: {
  endpoint: string;
  apiToken: string;
  connectionRevision: number;
  connected: boolean;
  active: boolean;
  onConnectionConfigApplied: (endpoint: string, apiToken: string) => void;
}): LocalApiConnectionSettingsController {
  const identity = JSON.stringify([connectionRevision, endpoint, apiToken]);
  const identityRef = useRef(identity);
  identityRef.current = identity;
  const mountedRef = useRef(false);
  const readGenerationRef = useRef(0);
  const readAbortRef = useRef<AbortController | null>(null);
  // Do not reset this transport owner when the connection identity changes.
  const writeOwnerRef = useRef<object | null>(null);
  const [writeAction, setWriteAction] = useState<ConnectionWriteAction | null>(null);
  const [reading, setReading] = useState(false);
  const [confirmed, setConfirmed] = useState<ConfirmedConfig | null>(null);
  const [draft, setDraft] = useState<{ identity: string; host: string } | null>(null);
  const [failure, setFailure] = useState<{ identity: string; message: string } | null>(null);
  const [notice, setNotice] = useState<{ identity: string; message: string } | null>(null);
  const refreshRef = useRef<() => Promise<void>>(async () => undefined);
  const local = isLoopbackLocalApiEndpoint(endpoint);
  const config = confirmed?.identity === identity ? confirmed.config : null;
  const lanHost = draft?.identity === identity ? draft.host : config?.lanBindHost || 'auto';
  const lanEnabled = config?.lanEnabled ?? false;
  const hostDirty = config !== null && normalizeHost(lanHost) !== normalizeHost(config.lanBindHost);
  const error = failure?.identity === identity ? failure.message : '';
  const canRead = Boolean(active && connected && apiToken && local);
  const writable = Boolean(canRead && config && !writeAction && !reading);

  const invalidateRead = useCallback(() => {
    readGenerationRef.current += 1;
    readAbortRef.current?.abort();
    readAbortRef.current = null;
  }, []);

  useEffect(() => {
    mountedRef.current = true;
    return () => {
      mountedRef.current = false;
      invalidateRead();
    };
  }, [invalidateRead]);

  const acceptConfig = useCallback((requestIdentity: string, next: LocalApiConnectionConfig) => {
    setConfirmed(next.ok ? { identity: requestIdentity, config: next } : null);
    setFailure(!next.ok || next.error || next.lanError
      ? { identity: requestIdentity, message: next.error || next.lanError || 'Mod 未确认连接设置。' }
      : null);
  }, []);

  const refresh = useCallback(async () => {
    if (!mountedRef.current || !canRead || writeOwnerRef.current) return;
    invalidateRead();
    const generation = readGenerationRef.current;
    const requestIdentity = identity;
    const abort = new AbortController();
    readAbortRef.current = abort;
    const timeout = window.setTimeout(() => abort.abort(), 2800);
    setReading(true);
    const current = () => mountedRef.current
      && identityRef.current === requestIdentity
      && readGenerationRef.current === generation
      && !writeOwnerRef.current;
    try {
      const next = await readLocalApiConnectionConfig(endpoint, apiToken, abort.signal);
      if (!current()) return;
      acceptConfig(requestIdentity, next);
      setNotice(null);
    } catch (reason) {
      if (!current()) return;
      setConfirmed(null);
      setFailure({ identity: requestIdentity, message: formatConnectionError(reason) });
    } finally {
      window.clearTimeout(timeout);
      if (readAbortRef.current === abort) readAbortRef.current = null;
      if (current()) setReading(false);
    }
  }, [acceptConfig, apiToken, canRead, endpoint, identity, invalidateRead]);
  refreshRef.current = refresh;

  useEffect(() => {
    setConfirmed(null);
    setDraft(null);
    setFailure(null);
    setNotice(null);
  }, [identity]);

  useEffect(() => {
    invalidateRead();
    setReading(false);
    if (canRead) void refresh();
    return invalidateRead;
  }, [canRead, identity, invalidateRead, refresh]);

  const runWrite = useCallback(async (
    action: ConnectionWriteAction,
    request: () => Promise<LocalApiConnectionConfig>,
  ) => {
    if (!mountedRef.current || !writable || writeOwnerRef.current) return;
    const owner = {};
    const requestIdentity = identity;
    writeOwnerRef.current = owner;
    invalidateRead();
    setReading(false);
    setWriteAction(action);
    setFailure(null);
    setNotice(null);
    let accepted = false;
    const current = () => mountedRef.current && identityRef.current === requestIdentity;
    try {
      const next = await request();
      if (!current()) return;
      accepted = true;
      acceptConfig(requestIdentity, next);
      setDraft(null);
      setNotice(next.ok && !next.error && !next.lanError
        ? { identity: requestIdentity, message: action === 'token' ? 'Token 已重置。' : '连接设置已保存。' }
        : null);
      if (next.ok && next.localEndpoint && next.token
        && (next.localEndpoint !== endpoint || next.token !== apiToken)) {
        onConnectionConfigApplied(next.localEndpoint, next.token);
      }
    } catch (reason) {
      if (!current()) return;
      // A failed response cannot prove that the server did not perform the write.
      setConfirmed(null);
      setFailure({ identity: requestIdentity, message: `${formatConnectionError(reason)} 请刷新确认当前连接设置；本次写入不会自动重试。` });
    } finally {
      if (writeOwnerRef.current === owner) writeOwnerRef.current = null;
      if (mountedRef.current) {
        setWriteAction(null);
        // Only a changed connection gets an automatic read. A failed current write
        // keeps its explanation until the user explicitly refreshes.
        if (!accepted && identityRef.current !== requestIdentity) void refreshRef.current();
      }
    }
  }, [acceptConfig, apiToken, endpoint, identity, invalidateRead, onConnectionConfigApplied, writable]);

  const apply = useCallback(() => runWrite('apply', () => writeLocalApiConnectionConfig(endpoint, apiToken, {
    lanEnabled,
    lanBindHost: normalizeHost(lanHost),
  })), [apiToken, endpoint, lanEnabled, lanHost, runWrite]);
  const setLanEnabled = useCallback((enabled: boolean) => runWrite('apply', () => writeLocalApiConnectionConfig(endpoint, apiToken, {
    lanEnabled: enabled,
    lanBindHost: normalizeHost(lanHost),
  })), [apiToken, endpoint, lanHost, runWrite]);
  const regenerateToken = useCallback(() => runWrite('token', () => regenerateLocalApiToken(endpoint, apiToken)),
    [apiToken, endpoint, runWrite]);

  const status = writeAction
    ? '连接设置请求正在处理；完成前暂停其他写入。'
    : !apiToken ? '尚未收到 Mod API Token。'
      : !local ? '连接设置只能在游戏电脑的本机回环连接上修改。'
        : !connected ? '等待 Mod 本地 API 连接。'
          : reading ? '正在读取连接设置。'
            : error ? ''
              : notice?.identity === identity ? notice.message
                : !config ? '尚未读取连接设置，请刷新后再修改。'
                  : hostDirty ? '监听地址尚未应用。' : '';

  return {
    config,
    lanEnabled,
    lanHost,
    busy: writeAction || (reading ? 'refresh' : null),
    error,
    status,
    writable,
    refreshable: canRead && !writeAction && !reading,
    dirty: hostDirty,
    hostDirty,
    setLanHost: (host) => setDraft({ identity, host }),
    refresh,
    apply,
    setLanEnabled,
    regenerateToken,
  };
}

function normalizeHost(value: string): string {
  return value.trim().toLowerCase() || 'auto';
}

function formatConnectionError(reason: unknown): string {
  const message = reason instanceof Error ? reason.message : String(reason);
  return message.includes('403')
    ? '连接设置只能在游戏电脑的本机回环连接上修改。'
    : message;
}
