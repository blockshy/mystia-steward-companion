import { useCallback, useEffect, useRef, useState } from 'react';
import { IconArchive, IconFolderOpen, IconPower, IconRefresh, IconTerminal2 } from '@tabler/icons-react';
import { Button, Card, CardContent, InfoLine, NumberInput } from '@/components/ui-kit';
import {
  exportDiagnosticPackage,
  openLogFolder,
  readLogSettings,
  setBepInExConsoleVisibility,
  writeLogSettings,
} from '@/companion/api';
import { formatBytes } from '@/companion/formatters';
import { isLoopbackLocalApiEndpoint } from '@/companion/local-api-endpoint';
import { MINIMUM_MULTICOLUMN_GRID_CLASS } from '@/companion/pages/shared-constants';
import type { DiagnosticPackageResponse, LocalApiLogSettings } from '@/companion/types';

const DEFAULT_AGGREGATE_LOG_MAX_FILE_COUNT = 30;
const MIN_AGGREGATE_LOG_MAX_FILE_COUNT = 1;
const MAX_AGGREGATE_LOG_MAX_FILE_COUNT = 9999;

export function ModLogsPanel({ endpoint, apiToken }: { endpoint: string; apiToken: string }) {
  const [settings, setSettings] = useState<LocalApiLogSettings | null>(null);
  const [diagnosticPackage, setDiagnosticPackage] = useState<DiagnosticPackageResponse | null>(null);
  const [refreshError, setRefreshError] = useState('');
  const [actionError, setActionError] = useState('');
  const [loading, setLoading] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);
  const [consoleActionLoading, setConsoleActionLoading] = useState(false);
  const refreshGenerationRef = useRef(0);
  const refreshAbortControllerRef = useRef<AbortController | null>(null);
  const mutationGenerationRef = useRef(0);
  const mutationInFlightRef = useRef(false);
  const actionGenerationRef = useRef(0);
  const connectionIdentity = `${endpoint}\n${apiToken}`;
  const connectionIdentityRef = useRef(connectionIdentity);
  connectionIdentityRef.current = connectionIdentity;

  const invalidateSettingsRefresh = useCallback(() => {
    refreshGenerationRef.current += 1;
    refreshAbortControllerRef.current?.abort();
    refreshAbortControllerRef.current = null;
  }, []);

  const refreshLogSettings = useCallback(async () => {
    if (!apiToken) {
      invalidateSettingsRefresh();
      setSettings(null);
      setRefreshError('未收到本地 API Token。');
      setLoading(false);
      return;
    }
    if (mutationInFlightRef.current) return;

    const requestIdentity = connectionIdentity;
    const requestGeneration = refreshGenerationRef.current + 1;
    refreshGenerationRef.current = requestGeneration;
    refreshAbortControllerRef.current?.abort();
    const abortController = new AbortController();
    refreshAbortControllerRef.current = abortController;
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(true);
    try {
      const nextSettings = await readLogSettings(endpoint, apiToken, abortController.signal);
      if (requestGeneration !== refreshGenerationRef.current
        || requestIdentity !== connectionIdentityRef.current
        || mutationInFlightRef.current) {
        return;
      }
      setSettings(nextSettings);
      setRefreshError('');
    } catch (err) {
      if (requestGeneration !== refreshGenerationRef.current
        || requestIdentity !== connectionIdentityRef.current
        || mutationInFlightRef.current) {
        return;
      }
      setRefreshError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      if (refreshAbortControllerRef.current === abortController) {
        refreshAbortControllerRef.current = null;
      }
      if (requestGeneration === refreshGenerationRef.current
        && requestIdentity === connectionIdentityRef.current
        && !mutationInFlightRef.current) {
        setLoading(false);
      }
    }
  }, [apiToken, connectionIdentity, endpoint, invalidateSettingsRefresh]);

  const setAggregateLogEnabled = useCallback(async (aggregateLog: boolean) => {
    if (!apiToken || mutationInFlightRef.current) return;

    const requestIdentity = connectionIdentity;
    const mutationGeneration = mutationGenerationRef.current + 1;
    const actionGeneration = actionGenerationRef.current + 1;
    mutationGenerationRef.current = mutationGeneration;
    actionGenerationRef.current = actionGeneration;
    mutationInFlightRef.current = true;
    invalidateSettingsRefresh();
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(false);
    setActionLoading(true);
    try {
      const nextSettings = await writeLogSettings(endpoint, apiToken, { aggregateLog }, abortController.signal);
      if (mutationGeneration !== mutationGenerationRef.current
        || requestIdentity !== connectionIdentityRef.current) {
        return;
      }
      setSettings(nextSettings);
      setRefreshError('');
      setActionError('');
    } catch (err) {
      if (mutationGeneration === mutationGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      window.clearTimeout(timeoutId);
      if (mutationGeneration === mutationGenerationRef.current) {
        mutationInFlightRef.current = false;
      }
      if (actionGeneration === actionGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionLoading(false);
      }
    }
  }, [apiToken, connectionIdentity, endpoint, invalidateSettingsRefresh]);

  const setAggregateLogMaxFileCount = useCallback(async (value: number) => {
    if (!apiToken || mutationInFlightRef.current) return;

    const aggregateLogMaxFileCount = Math.min(
      MAX_AGGREGATE_LOG_MAX_FILE_COUNT,
      Math.max(MIN_AGGREGATE_LOG_MAX_FILE_COUNT, Math.trunc(value)),
    );
    const requestIdentity = connectionIdentity;
    const mutationGeneration = mutationGenerationRef.current + 1;
    const actionGeneration = actionGenerationRef.current + 1;
    mutationGenerationRef.current = mutationGeneration;
    actionGenerationRef.current = actionGeneration;
    mutationInFlightRef.current = true;
    invalidateSettingsRefresh();
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(false);
    setActionLoading(true);
    try {
      const nextSettings = await writeLogSettings(
        endpoint,
        apiToken,
        { aggregateLogMaxFileCount },
        abortController.signal,
      );
      if (mutationGeneration !== mutationGenerationRef.current
        || requestIdentity !== connectionIdentityRef.current) {
        return;
      }
      setSettings(nextSettings);
      setRefreshError('');
      setActionError('');
    } catch (err) {
      if (mutationGeneration === mutationGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      window.clearTimeout(timeoutId);
      if (mutationGeneration === mutationGenerationRef.current) {
        mutationInFlightRef.current = false;
      }
      if (actionGeneration === actionGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionLoading(false);
      }
    }
  }, [apiToken, connectionIdentity, endpoint, invalidateSettingsRefresh]);

  const setConsoleVisible = useCallback(async (visible: boolean) => {
    if (!apiToken || mutationInFlightRef.current) return;

    const requestIdentity = connectionIdentity;
    const mutationGeneration = mutationGenerationRef.current + 1;
    mutationGenerationRef.current = mutationGeneration;
    mutationInFlightRef.current = true;
    invalidateSettingsRefresh();
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(false);
    setConsoleActionLoading(true);
    try {
      const result = await setBepInExConsoleVisibility(
        endpoint,
        apiToken,
        visible,
        abortController.signal,
      );
      if (mutationGeneration !== mutationGenerationRef.current
        || requestIdentity !== connectionIdentityRef.current) {
        return;
      }
      setSettings((current) => current === null ? current : {
        ...current,
        bepInExConsoleSupported: result.supported,
        bepInExConsoleConfiguredVisible: result.configuredVisible,
        bepInExConsoleActive: result.active,
        bepInExConsoleVisible: result.visible,
        bepInExConsoleStatus: result.status,
      });
      setRefreshError('');
      if (!result.ok) {
        setActionError(result.error || result.status || '切换 BepInEx 控制台失败');
        return;
      }
      setActionError('');
    } catch (err) {
      if (mutationGeneration === mutationGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      window.clearTimeout(timeoutId);
      if (mutationGeneration === mutationGenerationRef.current) {
        mutationInFlightRef.current = false;
      }
      if (mutationGeneration === mutationGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setConsoleActionLoading(false);
      }
    }
  }, [apiToken, connectionIdentity, endpoint, invalidateSettingsRefresh]);

  const openAggregateFolder = useCallback(async () => {
    if (!apiToken) return;

    const requestIdentity = connectionIdentity;
    const actionGeneration = actionGenerationRef.current + 1;
    actionGenerationRef.current = actionGeneration;
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);
    try {
      const result = await openLogFolder(endpoint, apiToken, 'aggregate', abortController.signal);
      if (!result.ok) throw new Error(result.error || '打开总日志目录失败');
      if (actionGeneration !== actionGenerationRef.current
        || requestIdentity !== connectionIdentityRef.current) {
        return;
      }
      setActionError('');
    } catch (err) {
      if (actionGeneration === actionGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      window.clearTimeout(timeoutId);
      if (actionGeneration === actionGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionLoading(false);
      }
    }
  }, [apiToken, connectionIdentity, endpoint]);

  const exportDiagnostics = useCallback(async () => {
    if (!apiToken) return;

    const requestIdentity = connectionIdentity;
    const actionGeneration = actionGenerationRef.current + 1;
    actionGenerationRef.current = actionGeneration;
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 8000);
    setActionLoading(true);
    try {
      const result = await exportDiagnosticPackage(endpoint, apiToken, abortController.signal);
      if (!result.ok) throw new Error(result.error || '导出诊断包失败');
      if (actionGeneration !== actionGenerationRef.current
        || requestIdentity !== connectionIdentityRef.current) {
        return;
      }
      setDiagnosticPackage(result);
      setActionError('');
    } catch (err) {
      if (actionGeneration === actionGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionError(err instanceof Error ? err.message : String(err));
      }
    } finally {
      window.clearTimeout(timeoutId);
      if (actionGeneration === actionGenerationRef.current
        && requestIdentity === connectionIdentityRef.current) {
        setActionLoading(false);
      }
    }
  }, [apiToken, connectionIdentity, endpoint]);

  useEffect(() => {
    invalidateSettingsRefresh();
    mutationGenerationRef.current += 1;
    mutationInFlightRef.current = false;
    actionGenerationRef.current += 1;
    setSettings(null);
    setDiagnosticPackage(null);
    setRefreshError(apiToken ? '' : '未收到本地 API Token。');
    setActionError('');
    setLoading(false);
    setActionLoading(false);
    setConsoleActionLoading(false);
    if (!apiToken) return undefined;

    refreshLogSettings();
    const timer = window.setInterval(refreshLogSettings, 3000);
    return () => {
      window.clearInterval(timer);
      invalidateSettingsRefresh();
      mutationGenerationRef.current += 1;
      mutationInFlightRef.current = false;
      actionGenerationRef.current += 1;
    };
  }, [apiToken, connectionIdentity, invalidateSettingsRefresh, refreshLogSettings]);

  const error = actionError || refreshError;
  const aggregatePath = settings?.aggregateModLogPath || '';
  const aggregateDirectory = settings?.aggregateModLogDirectory || '';
  const aggregateEnabled = settings?.aggregateModLogEnabled ?? false;
  const aggregateMaxFileCount = settings?.aggregateModLogMaxFileCount ?? DEFAULT_AGGREGATE_LOG_MAX_FILE_COUNT;
  const consoleSupported = settings?.bepInExConsoleSupported ?? false;
  const consoleControlLocal = isLoopbackLocalApiEndpoint(endpoint);
  const consoleConfiguredVisible = settings?.bepInExConsoleConfiguredVisible ?? false;
  const consoleVisible = settings?.bepInExConsoleVisible ?? false;
  const consoleStatus = consoleControlLocal
    ? formatBepInExConsoleStatus(settings?.bepInExConsoleStatus)
    : '仅可在游戏电脑本机控制 BepInEx 控制台。';
  const consoleWindowLabel = formatBepInExConsoleWindowLabel(settings);

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="grid gap-3 p-4 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start">
          <div className="min-w-0">
            <div className="text-sm font-semibold">Mod 总日志</div>
            <div className="mt-1 truncate text-xs text-muted-foreground" title={error || aggregatePath || endpoint}>
              {error || aggregatePath || '等待日志配置'}
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2 lg:justify-end" data-gamepad-axis="x">
            <Button
              size="sm"
              variant={aggregateEnabled ? 'default' : 'outline'}
              onClick={() => setAggregateLogEnabled(!aggregateEnabled)}
              disabled={!apiToken || actionLoading || consoleActionLoading}
              data-gamepad-focus-key="logs:toggle-aggregate"
            >
              <IconPower className="size-4" />
              {aggregateEnabled ? '关闭总日志' : '开启总日志'}
            </Button>
            <Button
              size="sm"
              variant="outline"
              disabled={!apiToken || actionLoading || consoleActionLoading}
              data-gamepad-focus-key="logs:open-folder"
              onClick={openAggregateFolder}
            >
              <IconFolderOpen className="size-4" />
              打开目录
            </Button>
            <Button
              size="sm"
              variant="outline"
              disabled={!apiToken || actionLoading || consoleActionLoading}
              data-gamepad-focus-key="logs:export-diagnostics"
              onClick={exportDiagnostics}
            >
              <IconArchive className="size-4" />
              导出诊断包
            </Button>
            <Button
              size="sm"
              variant="outline"
              disabled={loading || actionLoading || consoleActionLoading}
              data-gamepad-focus-key="logs:refresh"
              onClick={refreshLogSettings}
            >
              <IconRefresh className={loading ? 'size-4 animate-spin' : 'size-4'} />
              刷新
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="grid gap-3 p-4 min-[520px]:grid-cols-[minmax(0,1fr)_auto] min-[520px]:items-center">
          <div className="min-w-0">
            <div className="text-sm font-semibold">BepInEx 控制台</div>
            <div className="mt-1 truncate text-xs text-muted-foreground" title={consoleStatus}>
              {consoleStatus}
            </div>
          </div>
          <div className="flex items-center min-[520px]:justify-end" data-gamepad-axis="x">
            <Button
              size="sm"
              variant={consoleVisible ? 'default' : 'outline'}
              disabled={!apiToken
                || settings === null
                || !consoleSupported
                || !consoleControlLocal
                || actionLoading
                || consoleActionLoading}
              data-gamepad-focus-key="logs:toggle-bepinex-console"
              aria-busy={consoleActionLoading}
              title={consoleControlLocal ? consoleStatus : '仅可在游戏电脑本机控制'}
              onClick={() => setConsoleVisible(!consoleVisible)}
            >
              <IconTerminal2 className="size-4" />
              {consoleVisible ? '隐藏控制台' : '显示控制台'}
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className={`${MINIMUM_MULTICOLUMN_GRID_CLASS} grid grid-cols-1 gap-x-4 gap-y-3 p-4 text-sm min-[640px]:grid-cols-2`}>
          <InfoLine label="本地 API 授权" value={apiToken ? '已接收' : '未收到'} />
          <InfoLine label="总日志" value={aggregateEnabled ? '开启' : '关闭'} />
          <InfoLine label="启动自动显示" value={consoleConfiguredVisible ? '开启' : '关闭'} />
          <InfoLine label="控制台窗口" value={consoleWindowLabel} />
          <InfoLine label="单文件大小" value={formatBytes(settings?.aggregateModLogMaxFileBytes ?? 10 * 1024 * 1024)} />
          <InfoLine label="总容量上限" value={formatBytes(settings?.aggregateModLogMaxTotalBytes ?? 300 * 1024 * 1024)} />
          <label className="flex items-center justify-between gap-3 text-sm">
            <span className="min-w-0 text-muted-foreground">文件上限</span>
            <NumberInput
              min={MIN_AGGREGATE_LOG_MAX_FILE_COUNT}
              max={MAX_AGGREGATE_LOG_MAX_FILE_COUNT}
              value={aggregateMaxFileCount}
              onValueChange={setAggregateLogMaxFileCount}
              disabled={!apiToken || actionLoading || consoleActionLoading}
              className="h-8 w-20"
            />
          </label>
          <InfoLine label="写入范围" value="BepInEx / 自动化 / 经营诊断 / 运行时数据" />
          <InfoLine label="总日志目录" value={aggregateDirectory || '未知'} mono />
          <InfoLine label="总日志文件" value={aggregatePath || '未知'} mono />
          <InfoLine label="最近诊断包" value={diagnosticPackage?.path || '未导出'} mono />
          <InfoLine label="打包内容" value={diagnosticPackage ? `${diagnosticPackage.files.length} 个文件` : '未导出'} />
        </CardContent>
      </Card>

      {error && (
        <div className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {error}
        </div>
      )}
    </div>
  );
}

function formatBepInExConsoleWindowLabel(settings: LocalApiLogSettings | null): string {
  if (settings === null) return '等待读取';
  if (settings.bepInExConsoleStatus === 'state-read-failed') return '读取失败';
  if (!settings.bepInExConsoleSupported) {
    return settings.bepInExConsoleStatus === 'unsupported-platform'
      ? '不支持'
      : '不可用';
  }
  if (settings.bepInExConsoleVisible) return '已显示';
  return settings.bepInExConsoleActive ? '已隐藏' : '未创建';
}

function formatBepInExConsoleStatus(status: string | undefined): string {
  switch (status) {
    case 'visible':
      return 'BepInEx 控制台已显示。';
    case 'hidden':
      return 'BepInEx 控制台已隐藏，日志输出保持连接。';
    case 'inactive':
      return 'BepInEx 控制台未创建。';
    case 'unsupported-platform':
      return '当前平台不支持 BepInEx 控制台窗口。';
    case 'state-read-failed':
      return 'BepInEx 控制台状态读取失败。';
    case '':
    case undefined:
      return '等待控制台状态';
    default:
      return status;
  }
}
