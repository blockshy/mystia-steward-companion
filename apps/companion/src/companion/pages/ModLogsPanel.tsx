import { useCallback, useEffect, useState } from 'react';
import { IconArchive, IconFolderOpen, IconPower, IconRefresh } from '@tabler/icons-react';
import { Button, Card, CardContent, InfoLine } from '@/components/ui-kit';
import { exportDiagnosticPackage, openLogFolder, readLogSettings, writeLogSettings } from '@/companion/api';
import { formatBytes } from '@/companion/formatters';
import type { DiagnosticPackageResponse, LocalApiLogSettings } from '@/companion/types';

export function ModLogsPanel({ endpoint, apiToken }: { endpoint: string; apiToken: string }) {
  const [settings, setSettings] = useState<LocalApiLogSettings | null>(null);
  const [diagnosticPackage, setDiagnosticPackage] = useState<DiagnosticPackageResponse | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);

  const refreshLogSettings = useCallback(async () => {
    if (!apiToken) {
      setSettings(null);
      setError('未收到本地 API Token。');
      return;
    }

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(true);
    try {
      const nextSettings = await readLogSettings(endpoint, apiToken, abortController.signal);
      setSettings(nextSettings);
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setLoading(false);
    }
  }, [apiToken, endpoint]);

  const setAggregateLogEnabled = useCallback(async (aggregateLog: boolean) => {
    if (!apiToken) return;

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);
    try {
      const nextSettings = await writeLogSettings(endpoint, apiToken, { aggregateLog }, abortController.signal);
      setSettings(nextSettings);
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const openAggregateFolder = useCallback(async () => {
    if (!apiToken) return;

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);
    try {
      const result = await openLogFolder(endpoint, apiToken, 'aggregate', abortController.signal);
      if (!result.ok) throw new Error(result.error || '打开总日志目录失败');
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const exportDiagnostics = useCallback(async () => {
    if (!apiToken) return;

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 8000);
    setActionLoading(true);
    try {
      const result = await exportDiagnosticPackage(endpoint, apiToken, abortController.signal);
      if (!result.ok) throw new Error(result.error || '导出诊断包失败');
      setDiagnosticPackage(result);
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  useEffect(() => {
    if (!apiToken) return;
    refreshLogSettings();
    const timer = window.setInterval(refreshLogSettings, 3000);
    return () => window.clearInterval(timer);
  }, [apiToken, refreshLogSettings]);

  const aggregatePath = settings?.aggregateModLogPath || '';
  const aggregateDirectory = settings?.aggregateModLogDirectory || '';
  const aggregateEnabled = settings?.aggregateModLogEnabled ?? false;

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
              disabled={!apiToken || actionLoading}
            >
              <IconPower className="size-4" />
              {aggregateEnabled ? '关闭总日志' : '开启总日志'}
            </Button>
            <Button size="sm" variant="outline" onClick={openAggregateFolder} disabled={!apiToken || actionLoading}>
              <IconFolderOpen className="size-4" />
              打开目录
            </Button>
            <Button size="sm" variant="outline" onClick={exportDiagnostics} disabled={!apiToken || actionLoading}>
              <IconArchive className="size-4" />
              导出诊断包
            </Button>
            <Button size="sm" variant="outline" onClick={refreshLogSettings} disabled={loading}>
              <IconRefresh className={loading ? 'size-4 animate-spin' : 'size-4'} />
              刷新
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="grid grid-cols-2 gap-x-4 gap-y-3 p-4 text-sm max-[719px]:grid-cols-1">
          <InfoLine label="本地 API 授权" value={apiToken ? '已接收' : '未收到'} />
          <InfoLine label="总日志" value={aggregateEnabled ? '开启' : '关闭'} />
          <InfoLine label="总日志分片" value={formatBytes(settings?.aggregateModLogMaxFileBytes ?? 10 * 1024 * 1024)} />
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
