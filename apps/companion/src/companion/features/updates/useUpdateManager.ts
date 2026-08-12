import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import {
  checkForUpdates,
  downloadUpdate,
  installUpdateOnExit,
  refreshUpdateStatus,
} from '@/companion/api';
import type { UpdateStatusResponse } from '@/companion/types';
import { openProjectReleaseUrl } from '@/lib/external-url';
import {
  buildUpdateNoticeSnoozeKey,
  persistUpdateNoticeSnooze,
  readUpdateNoticeSnoozeUntil,
  UPDATE_NOTICE_SNOOZE_MS,
} from '@/companion/features/updates/update-notice-storage';
import { getUpdateStatusPollInterval } from '@/companion/features/updates/update-polling';
import {
  UpdateRequestCoordinator,
  type UpdateManagerBusyAction,
} from '@/companion/features/updates/update-request-coordinator';

export type { UpdateManagerBusyAction } from '@/companion/features/updates/update-request-coordinator';

export interface UpdateManager {
  status: UpdateStatusResponse | null;
  busy: UpdateManagerBusyAction | null;
  error: string;
  connected: boolean;
  noticeVisible: boolean;
  check: () => Promise<void>;
  download: () => Promise<void>;
  install: () => Promise<void>;
  openReleasePage: (releaseUrl?: string) => Promise<void>;
  snoozeNotice: () => void;
}

interface UseUpdateManagerOptions {
  endpoint: string;
  apiToken: string;
  connectionRevision: number;
  connected: boolean;
}

export function useUpdateManager({
  endpoint,
  apiToken,
  connectionRevision,
  connected,
}: UseUpdateManagerOptions): UpdateManager {
  const connectionIdentity = `${connectionRevision}\n${endpoint}\n${apiToken}`;
  const identityRef = useRef(connectionIdentity);
  const requestCoordinatorRef = useRef(new UpdateRequestCoordinator());
  const statusAbortRef = useRef<AbortController | null>(null);
  const actionAbortRef = useRef<AbortController | null>(null);
  const [status, setStatus] = useState<UpdateStatusResponse | null>(null);
  const [statusIdentity, setStatusIdentity] = useState(connectionIdentity);
  const [busy, setBusy] = useState<UpdateManagerBusyAction | null>(null);
  const [error, setError] = useState('');
  const [statusFailureCount, setStatusFailureCount] = useState(0);
  const [sessionSnooze, setSessionSnooze] = useState<{ identity: string; until: number } | null>(null);
  const [, setSnoozeRevision] = useState(0);

  const currentStatus = statusIdentity === connectionIdentity ? status : null;
  const noticeTag = currentStatus?.latestTag.trim() || currentStatus?.latestVersion.trim() || '';
  const noticeIdentity = noticeTag ? buildUpdateNoticeSnoozeKey(endpoint, noticeTag) : '';
  const storedSnoozedUntil = noticeTag
    ? readUpdateNoticeSnoozeUntil(window.localStorage, endpoint, noticeTag)
    : 0;
  const snoozedUntil = sessionSnooze?.identity === noticeIdentity
    ? Math.max(storedSnoozedUntil, sessionSnooze.until)
    : storedSnoozedUntil;

  useEffect(() => {
    identityRef.current = connectionIdentity;
    requestCoordinatorRef.current.cancelAll();
    statusAbortRef.current?.abort();
    actionAbortRef.current?.abort();
    statusAbortRef.current = null;
    actionAbortRef.current = null;
    setStatus(null);
    setStatusIdentity(connectionIdentity);
    setBusy(null);
    setError('');
    setStatusFailureCount(0);
  }, [connectionIdentity]);

  useEffect(() => {
    if (snoozedUntil <= Date.now()) return;
    const scheduledIdentity = noticeIdentity;
    const timer = window.setTimeout(
      () => {
        setSessionSnooze((current) => current?.identity === scheduledIdentity ? null : current);
        setSnoozeRevision((current) => current + 1);
      },
      Math.min(snoozedUntil - Date.now(), 2_147_483_647),
    );
    return () => window.clearTimeout(timer);
  }, [noticeIdentity, snoozedUntil]);

  const refreshStatus = useCallback(async () => {
    if (!connected || !apiToken) return;

    const requestIdentity = connectionIdentity;
    const requestGeneration = requestCoordinatorRef.current.beginStatus();
    if (requestGeneration === null) return;
    const abortController = new AbortController();
    statusAbortRef.current = abortController;

    try {
      const nextStatus = await refreshUpdateStatus(endpoint, apiToken, abortController.signal);
      if (identityRef.current !== requestIdentity
        || !requestCoordinatorRef.current.isStatusCurrent(requestGeneration)) return;
      setStatus(nextStatus);
      setStatusIdentity(requestIdentity);
      setError('');
      setStatusFailureCount(0);
    } catch (requestError) {
      if (abortController.signal.aborted
        || identityRef.current !== requestIdentity
        || !requestCoordinatorRef.current.isStatusCurrent(requestGeneration)) return;
      setError(requestError instanceof Error ? requestError.message : String(requestError));
      setStatusFailureCount((current) => current + 1);
    } finally {
      if (statusAbortRef.current === abortController) statusAbortRef.current = null;
      requestCoordinatorRef.current.finishStatus(requestGeneration);
    }
  }, [apiToken, connected, connectionIdentity, endpoint]);

  useEffect(() => {
    if (!connected || !apiToken) {
      requestCoordinatorRef.current.cancelAll();
      statusAbortRef.current?.abort();
      actionAbortRef.current?.abort();
      statusAbortRef.current = null;
      actionAbortRef.current = null;
      setBusy(null);
      setStatusFailureCount(0);
      return;
    }
    void refreshStatus();
  }, [apiToken, connected, connectionIdentity, refreshStatus]);

  useEffect(() => {
    if (!connected || !apiToken) return;
    const interval = window.setInterval(() => {
      if (document.visibilityState === 'visible') void refreshStatus();
    }, getUpdateStatusPollInterval(currentStatus, statusFailureCount));
    return () => window.clearInterval(interval);
  }, [apiToken, connected, connectionIdentity, currentStatus, refreshStatus, statusFailureCount]);

  useEffect(() => {
    if (!connected || !apiToken) return;
    const handleVisibilityChange = () => {
      if (document.visibilityState === 'visible') void refreshStatus();
    };
    document.addEventListener('visibilitychange', handleVisibilityChange);
    return () => document.removeEventListener('visibilitychange', handleVisibilityChange);
  }, [apiToken, connected, connectionIdentity, refreshStatus]);

  useEffect(() => () => {
    requestCoordinatorRef.current.cancelAll();
    statusAbortRef.current?.abort();
    actionAbortRef.current?.abort();
  }, []);

  const runAction = useCallback(async (
    action: UpdateManagerBusyAction,
    request: (signal: AbortSignal) => Promise<UpdateStatusResponse>,
  ) => {
    if (!connected || !apiToken || requestCoordinatorRef.current.busy) return;

    statusAbortRef.current?.abort();
    statusAbortRef.current = null;
    requestCoordinatorRef.current.cancelStatus();
    const requestIdentity = connectionIdentity;
    const requestGeneration = requestCoordinatorRef.current.beginAction(action);
    if (requestGeneration === null) return;
    const abortController = new AbortController();
    actionAbortRef.current = abortController;
    setBusy(action);
    setError('');

    try {
      const nextStatus = await request(abortController.signal);
      if (identityRef.current !== requestIdentity
        || !requestCoordinatorRef.current.isActionCurrent(requestGeneration)) return;
      setStatus(nextStatus);
      setStatusIdentity(requestIdentity);
      setError(nextStatus.error ?? '');
      setStatusFailureCount(0);
    } catch (requestError) {
      if (abortController.signal.aborted
        || identityRef.current !== requestIdentity
        || !requestCoordinatorRef.current.isActionCurrent(requestGeneration)) return;
      setError(requestError instanceof Error ? requestError.message : String(requestError));
    } finally {
      if (actionAbortRef.current === abortController) actionAbortRef.current = null;
      if (requestCoordinatorRef.current.finishAction(requestGeneration)) {
        setBusy(null);
      }
    }
  }, [apiToken, connected, connectionIdentity]);

  const check = useCallback(
    () => runAction('check', (signal) => checkForUpdates(endpoint, apiToken, signal)),
    [apiToken, endpoint, runAction],
  );
  const download = useCallback(
    () => runAction('download', (signal) => downloadUpdate(endpoint, apiToken, signal)),
    [apiToken, endpoint, runAction],
  );
  const install = useCallback(
    () => runAction('install', (signal) => installUpdateOnExit(endpoint, apiToken, signal)),
    [apiToken, endpoint, runAction],
  );

  const openReleasePage = useCallback(async (releaseUrl?: string) => {
    const targetUrl = releaseUrl?.trim() || currentStatus?.releaseUrl;
    if (!targetUrl) return;
    try {
      setError('');
      await openProjectReleaseUrl(targetUrl);
    } catch (openError) {
      setError(`无法打开发布页：${openError instanceof Error ? openError.message : String(openError)}`);
    }
  }, [currentStatus?.releaseUrl]);

  const snoozeNotice = useCallback(() => {
    if (!noticeTag || !noticeIdentity) return;
    const nextSnoozedUntil = Date.now() + UPDATE_NOTICE_SNOOZE_MS;
    persistUpdateNoticeSnooze(window.localStorage, endpoint, noticeTag, nextSnoozedUntil);
    setSessionSnooze({ identity: noticeIdentity, until: nextSnoozedUntil });
  }, [endpoint, noticeIdentity, noticeTag]);

  const noticeVisible = useMemo(
    () => Boolean(currentStatus?.enabled && currentStatus.hasUpdate && noticeTag && snoozedUntil <= Date.now()),
    [currentStatus?.enabled, currentStatus?.hasUpdate, noticeTag, snoozedUntil],
  );

  return {
    status: currentStatus,
    busy,
    error,
    connected: connected && Boolean(apiToken),
    noticeVisible,
    check,
    download,
    install,
    openReleasePage,
    snoozeNotice,
  };
}
