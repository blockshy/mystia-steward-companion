import { useCallback, useEffect, useMemo, useState } from 'react';
import { IconArchive, IconFolderOpen, IconPower, IconRefresh } from '@tabler/icons-react';
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger, Badge, Button, Card, CardContent, EmptyRow, InfoLine, Input } from '@/components/ui-kit';
import { exportDiagnosticPackage, openLogFolder, readAutomationLogs, readLogs, readLogSettings, writeLogSettings } from '@/companion/api';
import { formatBytes, parseAutomationLogLine } from '@/companion/formatters';
import type { DiagnosticPackageResponse, LocalApiLogSettings, LocalApiLogs } from '@/companion/types';
import { DENSE_CARD_HEADER_GRID } from '@/companion/pages/shared-constants';

const MAX_LOG_LINES_IN_VIEW = 400;

export function ModLogsPanel({ endpoint, apiToken }: { endpoint: string; apiToken: string }) {
  const [settings, setSettings] = useState<LocalApiLogSettings | null>(null);
  const [logs, setLogs] = useState<LocalApiLogs | null>(null);
  const [automationLogs, setAutomationLogs] = useState<LocalApiLogs | null>(null);
  const [automationLogFilter, setAutomationLogFilter] = useState('');
  const [diagnosticPackage, setDiagnosticPackage] = useState<DiagnosticPackageResponse | null>(null);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [actionLoading, setActionLoading] = useState(false);

  const refreshLogs = useCallback(async () => {
    if (!apiToken) {
      setSettings(null);
      setLogs(null);
      setAutomationLogs(null);
      setError('未收到本地 API Token。');
      return;
    }
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setLoading(true);

    try {
      const nextSettings = await readLogSettings(endpoint, apiToken, abortController.signal);
      setSettings(nextSettings);
      if (nextSettings.logAccessEnabled) {
        const [nextLogs, nextAutomationLogs] = await Promise.all([
          readLogs(endpoint, apiToken, abortController.signal),
          readAutomationLogs(endpoint, apiToken, abortController.signal),
        ]);
        setLogs(nextLogs);
        setAutomationLogs(nextAutomationLogs);
      } else {
        setLogs(null);
        setAutomationLogs(null);
      }
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setLoading(false);
    }
  }, [apiToken, endpoint]);

  const updateSettings = useCallback(async (next: { logAccess?: boolean; diagnostics?: boolean }) => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);

    try {
      const nextSettings = await writeLogSettings(endpoint, apiToken, next, abortController.signal);
      setSettings(nextSettings);
      if (!nextSettings.logAccessEnabled) {
        setLogs(null);
        setAutomationLogs(null);
      }
      setError('');
      if (nextSettings.logAccessEnabled) {
        const [nextLogs, nextAutomationLogs] = await Promise.all([
          readLogs(endpoint, apiToken, abortController.signal),
          readAutomationLogs(endpoint, apiToken, abortController.signal),
        ]);
        setLogs(nextLogs);
        setAutomationLogs(nextAutomationLogs);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const openFolder = useCallback(async (target: 'log' | 'diagnostics' | 'automation') => {
    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setActionLoading(true);

    try {
      const result = await openLogFolder(endpoint, apiToken, target, abortController.signal);
      if (!result.ok) throw new Error(result.error || '打开文件夹失败');
      setError('');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setActionLoading(false);
    }
  }, [apiToken, endpoint]);

  const exportDiagnostics = useCallback(async () => {
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

  const visibleLogLines = useMemo(
    () => (logs?.lines ?? []).slice(-MAX_LOG_LINES_IN_VIEW),
    [logs?.lines],
  );
  const automationLogEntries = useMemo(
    () => (automationLogs?.lines ?? []).map(parseAutomationLogLine).slice(-MAX_LOG_LINES_IN_VIEW),
    [automationLogs?.lines],
  );
  const filteredAutomationLogEntries = useMemo(() => {
    const keyword = automationLogFilter.trim().toLowerCase();
    if (!keyword) return automationLogEntries.slice(-160);
    return automationLogEntries
      .filter((entry) => [
        entry.raw,
        entry.action,
        entry.target,
        entry.desk,
        entry.orderKey,
        entry.food,
        entry.guest,
        entry.message,
      ].join(' ').toLowerCase().includes(keyword))
      .slice(-160);
  }, [automationLogEntries, automationLogFilter]);
  const configuredLogLimit = settings
    ? `${settings.maxLogLines ?? MAX_LOG_LINES_IN_VIEW} 行 / ${formatBytes(settings.maxLogBytes ?? 0)}`
    : '未知';
  const responseLogLimit = logs
    ? `${logs.maxLines ?? settings?.maxLogLines ?? MAX_LOG_LINES_IN_VIEW} 行 / ${formatBytes(logs.maxBytes ?? settings?.maxLogBytes ?? 0)}`
    : configuredLogLimit;
  const automationLogNotice = automationLogs?.error
    || (settings && !settings.logAccessEnabled ? '日志读取已关闭。' : '')
    || (automationLogs?.exists === false ? '尚未生成 automation-jobs.log。自动化执行开锅、直送或 pending 变化后会写入。' : '');

  useEffect(() => {
    if (!apiToken) return;
    refreshLogs();
    const timer = window.setInterval(refreshLogs, 2000);
    return () => window.clearInterval(timer);
  }, [apiToken, refreshLogs]);

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="grid gap-3 p-4 lg:grid-cols-[minmax(0,1fr)_auto] lg:items-start">
          <div className="min-w-0">
            <div className="text-sm font-semibold">Mod 实时日志</div>
            <div className="mt-1 truncate text-xs text-muted-foreground" title={logs?.path || settings?.logOutputPath || endpoint}>
              {error || logs?.path || settings?.logOutputPath || '等待日志响应'}
            </div>
          </div>
          <div className="flex flex-wrap items-center gap-2 lg:justify-end">
            <Button
              size="sm"
              variant={settings?.logAccessEnabled ? 'default' : 'outline'}
              onClick={() => updateSettings({ logAccess: !settings?.logAccessEnabled })}
              disabled={!apiToken || actionLoading}
            >
              <IconPower className="size-4" />
              {settings?.logAccessEnabled ? '关闭日志读取' : '开启日志读取'}
            </Button>
            <Button
              size="sm"
              variant={settings?.nightBusinessDiagnosticsEnabled ? 'default' : 'outline'}
              onClick={() => updateSettings({ diagnostics: !settings?.nightBusinessDiagnosticsEnabled })}
              disabled={!apiToken || actionLoading}
            >
              <IconPower className="size-4" />
              {settings?.nightBusinessDiagnosticsEnabled ? '关闭经营诊断' : '开启经营诊断'}
            </Button>
            <Button size="sm" variant="outline" onClick={() => openFolder('log')} disabled={!apiToken || actionLoading}>
              <IconFolderOpen className="size-4" />
              打开日志文件夹
            </Button>
            <Button size="sm" variant="outline" onClick={() => openFolder('diagnostics')} disabled={!apiToken || actionLoading}>
              <IconFolderOpen className="size-4" />
              打开诊断文件夹
            </Button>
            <Button size="sm" variant="outline" onClick={() => openFolder('automation')} disabled={!apiToken || actionLoading}>
              <IconFolderOpen className="size-4" />
              打开自动化日志
            </Button>
            <Button size="sm" variant="outline" onClick={exportDiagnostics} disabled={!apiToken || actionLoading}>
              <IconArchive className="size-4" />
              导出诊断包
            </Button>
            <Button size="sm" variant="outline" onClick={refreshLogs} disabled={loading}>
              <IconRefresh className={loading ? 'size-4 animate-spin' : 'size-4'} />
              刷新
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardContent className="grid grid-cols-2 gap-x-4 gap-y-3 p-4 text-sm">
          <InfoLine label="本地 API 授权" value={apiToken ? '已通过启动参数接收' : '未收到 token，请从游戏内按 F8 重新显示窗口'} />
          <InfoLine label="日志读取" value={settings?.logAccessEnabled ? '开启' : '关闭'} />
          <InfoLine label="读取上限" value={responseLogLimit} />
          <InfoLine label="窗口缓存" value={`最多显示 ${MAX_LOG_LINES_IN_VIEW} 行`} />
          <InfoLine label="经营诊断" value={settings?.nightBusinessDiagnosticsEnabled ? '开启' : '关闭'} />
          <InfoLine label="诊断日志目录" value={settings?.nightBusinessDiagnosticsDirectory || '未知'} mono />
          <InfoLine label="最近诊断包" value={diagnosticPackage?.path || '未导出'} mono />
          <InfoLine label="打包内容" value={diagnosticPackage ? `${diagnosticPackage.files.length} 个文件` : '未导出'} />
        </CardContent>
      </Card>

      <Accordion defaultValue={['automation', 'bepinex']} className="space-y-3">
        <AccordionItem value="automation">
          <AccordionTrigger data-gamepad-clickable="true">
            <div className={DENSE_CARD_HEADER_GRID}>
              <div className="min-w-0">
                <div className="text-sm font-semibold">自动化作业日志</div>
                <div className="mt-1 truncate text-xs text-muted-foreground" title={automationLogs?.path || ''}>
                  {automationLogs?.path || '等待日志响应'}
                </div>
              </div>
              <Badge variant={automationLogs?.exists ? 'secondary' : 'outline'}>
                {automationLogs?.exists ? `${automationLogEntries.length} 行` : '未生成'}
              </Badge>
            </div>
          </AccordionTrigger>
          <AccordionContent className="space-y-3">
            <Input
              value={automationLogFilter}
              onChange={(event) => setAutomationLogFilter(event.target.value)}
              placeholder="过滤 action / 桌号 / 料理 / orderKey"
              size="xs"
              className="h-7"
            />
            {automationLogNotice ? (
              <div className="rounded-md border border-border steward-muted-surface-30 px-3 py-2 text-sm text-muted-foreground">
                {automationLogNotice}
              </div>
            ) : filteredAutomationLogEntries.length > 0 ? (
              <div className="max-h-[34vh] space-y-2 overflow-auto pr-1">
                {filteredAutomationLogEntries.map((entry, index) => (
                  <div key={`${entry.timestamp}-${index}`} className="rounded-sm border border-border steward-background-surface-70 px-3 py-2.5 text-xs leading-5">
                    <div className="flex flex-wrap items-center gap-1.5">
                      <Badge variant="outline">{entry.action || 'unknown'}</Badge>
                      {entry.target && <Badge variant="secondary">{entry.target}</Badge>}
                      {entry.desk && <span className="text-muted-foreground">桌 {entry.desk}</span>}
                      <span className="font-mono text-muted-foreground">{entry.timestamp}</span>
                    </div>
                    <div className="mt-1 grid grid-cols-2 gap-x-3 gap-y-1 text-muted-foreground">
                      <InfoLine label="料理" value={entry.food || '无'} />
                      <InfoLine label="客人" value={entry.guest || '无'} />
                    </div>
                    {entry.orderKey && <div className="mt-1 truncate font-mono text-[11px] text-muted-foreground" title={entry.orderKey}>key {entry.orderKey}</div>}
                    {entry.message && <div className="mt-1 whitespace-pre-wrap text-foreground">{entry.message}</div>}
                  </div>
                ))}
              </div>
            ) : (
              <EmptyRow text="暂无匹配的自动化日志" />
            )}
          </AccordionContent>
        </AccordionItem>

        <AccordionItem value="bepinex">
          <AccordionTrigger data-gamepad-clickable="true">
            <div className={DENSE_CARD_HEADER_GRID}>
              <div className="min-w-0">
                <div className="text-sm font-semibold">BepInEx LogOutput.log</div>
                <div className="mt-1 truncate text-xs text-muted-foreground" title={logs?.path || settings?.logOutputPath || ''}>
                  {logs?.path || settings?.logOutputPath || '等待日志响应'}
                </div>
              </div>
              <Badge variant={logs?.exists ? 'secondary' : 'outline'}>
                {logs?.exists ? `${visibleLogLines.length} 行` : '未读取'}
              </Badge>
            </div>
          </AccordionTrigger>
          <AccordionContent className="p-0">
            <pre className="max-h-[62vh] overflow-auto whitespace-pre-wrap break-words rounded-sm border border-border steward-background-surface-55 p-3.5 font-mono text-xs leading-6 text-foreground">
              {error
                || logs?.error
                || (!settings?.logAccessEnabled ? '日志读取已关闭。需要排查时点击“开启日志读取”，结束后建议关闭。' : null)
                || (logs?.exists === false ? '未找到 BepInEx/LogOutput.log。' : null)
                || (visibleLogLines.length ? visibleLogLines.join('\n') : '暂无日志内容。')}
            </pre>
          </AccordionContent>
        </AccordionItem>
      </Accordion>
    </div>
  );
}
