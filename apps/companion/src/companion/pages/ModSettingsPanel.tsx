import { useCallback, useEffect, useMemo, useState } from 'react';
import { IconCopy, IconDownload, IconExternalLink, IconKey, IconPackageImport, IconRefresh } from '@tabler/icons-react';
import { Button, Dialog, InfoLine, Input, ListPanel, MultiSelectBox, NumberInput, SettingHelpField, SettingHelpProvider, Slider, SwitchField, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import {
  readLocalApiConnectionConfig,
  regenerateLocalApiToken,
  writeLocalApiConnectionConfig,
} from '@/companion/api';
import { buildInventorySelectOptions, type InventorySortMode } from '@/companion/domain/inventory-sorting';
import type { UpdateManager } from '@/companion/features/updates/useUpdateManager';
import { formatBytes } from '@/companion/formatters';
import {
  DEFAULT_FONT_SCALE_PERCENT,
  DEFAULT_NORMAL_TARGET_HIGHLIGHT_COLOR,
  DEFAULT_RARE_TARGET_HIGHLIGHT_COLOR,
  MAX_RECIPE_VARIANT_LIMIT_PER_BASE,
  MAX_AUTO_ROLLBACKS_LIMIT,
  MAX_AUTO_STEP_RETRIES_LIMIT,
  MAX_NORMAL_AUTO_ORDER_CONCURRENCY,
  MAX_RARE_AUTO_ORDER_CONCURRENCY,
  MIN_RECIPE_VARIANT_LIMIT_PER_BASE,
  MIN_AUTO_ORDER_CONCURRENCY,
  MIN_AUTO_ROLLBACKS,
  MIN_AUTO_STEP_RETRIES,
  normalizeRecipeVariantLimitPerBase,
  normalizeTargetHighlightColor,
  type CompanionPreferences,
} from '@/companion/preferences';
import type { LocalApiConnectionConfig, RuntimeSets, SettingsTab, UpdateStatusResponse } from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { ThemeMode } from '@/lib/theme';
import {
  RECOMMENDATION_OBJECTIVE_DEFINITIONS,
  RECOMMENDATION_SORT_PRESETS,
  buildDefaultRecommendationSortProfile,
  type RecommendationObjectiveKey,
  type RecommendationSortPresetId,
  type RecommendationSortProfile,
} from '@/recommendation-engine';
import {
  AutomationSliderField,
  BackgroundOpacitySlider,
  ContentOpacitySlider,
  FontScaleSlider,
  FocusSwitchCooldownInput,
  InventorySortControl,
  SettingSegmentedControl,
  SwitchControl,
} from '@/companion/pages/shared';
import { DENSE_TWO_COLUMN_GRID, INNER_TAB_TRIGGER_CLASS } from '@/companion/pages/shared-constants';

export function ModSettingsPanel({
  endpoint,
  apiToken,
  preferences,
  data,
  runtimeSets,
  themeMode,
  serviceFocusCompact,
  settingsTab,
  updateManager,
  onPreferenceChange,
  onConnectionConfigApplied,
  onSettingsTabChange,
  onThemeModeChange,
  onServiceFocusCompactChange,
  supportsDesktopWindowControls,
}: {
  endpoint: string;
  apiToken: string;
  preferences: CompanionPreferences;
  data: RecommendationDataSet;
  runtimeSets: RuntimeSets | null;
  themeMode: ThemeMode;
  serviceFocusCompact: boolean;
  settingsTab: SettingsTab;
  updateManager: UpdateManager;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onConnectionConfigApplied: (endpoint: string, apiToken: string) => void;
  onSettingsTabChange: (tab: SettingsTab) => void;
  onThemeModeChange: (mode: ThemeMode) => void;
  onServiceFocusCompactChange: (value: boolean) => void;
  supportsDesktopWindowControls: boolean;
}) {
  const [connectionConfig, setConnectionConfig] = useState<LocalApiConnectionConfig | null>(null);
  const [connectionLanEnabled, setConnectionLanEnabled] = useState(false);
  const [connectionLanHost, setConnectionLanHost] = useState('auto');
  const [connectionBusy, setConnectionBusy] = useState<'refresh' | 'apply' | 'token' | 'copy' | null>(null);
  const [connectionError, setConnectionError] = useState('');
  const [connectionTokenVisible, setConnectionTokenVisible] = useState(false);
  const [tokenResetDialogOpen, setTokenResetDialogOpen] = useState(false);
  const [ingredientExclusionSortMode, setIngredientExclusionSortMode] = useState<InventorySortMode>('name');
  const [beverageExclusionSortMode, setBeverageExclusionSortMode] = useState<InventorySortMode>('name');
  const ingredientOptions = useMemo(
    () => buildInventorySelectOptions(
      data.ingredients,
      runtimeSets?.ownedIngredientQty ?? null,
      ingredientExclusionSortMode,
    ),
    [data.ingredients, ingredientExclusionSortMode, runtimeSets?.ownedIngredientQty],
  );
  const beverageOptions = useMemo(
    () => buildInventorySelectOptions(
      data.beverages,
      runtimeSets?.ownedBeverageQty ?? null,
      beverageExclusionSortMode,
    ),
    [beverageExclusionSortMode, data.beverages, runtimeSets?.ownedBeverageQty],
  );

  const updateExclusions = useCallback((next: Partial<CompanionPreferences['recommendationExclusions']>) => {
    onPreferenceChange({
      recommendationExclusions: {
        ...preferences.recommendationExclusions,
        ...next,
      },
    });
  }, [onPreferenceChange, preferences.recommendationExclusions]);

  const setRareBeverageDelivery = useCallback((enabled: boolean) => {
    onPreferenceChange(enabled
      ? { autoPrepTakeBeverage: true, autoPrepCompleteOrder: true }
      : { autoPrepTakeBeverage: false });
  }, [onPreferenceChange]);

  const setRareFoodDelivery = useCallback((enabled: boolean) => {
    onPreferenceChange(enabled
      ? { autoPrepCollectCooking: true, autoPrepCompleteOrder: true }
      : { autoPrepCollectCooking: false });
  }, [onPreferenceChange]);

  const setRareOrderCompletion = useCallback((enabled: boolean) => {
    onPreferenceChange(enabled
      ? { autoPrepCompleteOrder: true }
      : {
          autoPrepCompleteOrder: false,
          autoPrepTakeBeverage: false,
          autoPrepCollectCooking: false,
        });
  }, [onPreferenceChange]);

  const setNormalBeverageDelivery = useCallback((enabled: boolean) => {
    onPreferenceChange(enabled
      ? { autoNormalTakeBeverage: true, autoNormalCompleteOrder: true }
      : { autoNormalTakeBeverage: false });
  }, [onPreferenceChange]);

  const setNormalFoodDelivery = useCallback((enabled: boolean) => {
    onPreferenceChange(enabled
      ? { autoNormalDeliverFood: true, autoNormalCompleteOrder: true }
      : { autoNormalDeliverFood: false });
  }, [onPreferenceChange]);

  const setNormalOrderCompletion = useCallback((enabled: boolean) => {
    onPreferenceChange(enabled
      ? { autoNormalCompleteOrder: true }
      : {
          autoNormalCompleteOrder: false,
          autoNormalTakeBeverage: false,
          autoNormalDeliverFood: false,
        });
  }, [onPreferenceChange]);

  const applyConnectionConfigState = useCallback((nextConfig: LocalApiConnectionConfig) => {
    setConnectionConfig(nextConfig);
    setConnectionLanEnabled(nextConfig.lanEnabled);
    setConnectionLanHost(nextConfig.lanBindHost || 'auto');
    setConnectionError(nextConfig.error ?? nextConfig.lanError ?? '');
  }, []);

  const refreshConnectionConfig = useCallback(async () => {
    if (!apiToken) {
      setConnectionConfig(null);
      setConnectionError('未收到 Mod API Token。');
      return;
    }

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setConnectionBusy('refresh');
    try {
      const nextConfig = await readLocalApiConnectionConfig(endpoint, apiToken, abortController.signal);
      applyConnectionConfigState(nextConfig);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setConnectionError(message.includes('403') ? '连接配置只能在游戏所在设备的本机窗口中修改。' : message);
    } finally {
      window.clearTimeout(timeoutId);
      setConnectionBusy(null);
    }
  }, [apiToken, applyConnectionConfigState, endpoint]);

  const submitConnectionConfig = useCallback(async (next: { lanEnabled: boolean; lanBindHost: string }) => {
    if (!apiToken || connectionBusy) return null;

    setConnectionBusy('apply');
    try {
      const nextConfig = await writeLocalApiConnectionConfig(endpoint, apiToken, {
        lanEnabled: next.lanEnabled,
        lanBindHost: next.lanBindHost,
      });
      applyConnectionConfigState(nextConfig);
      if (nextConfig.localEndpoint && nextConfig.token) {
        onConnectionConfigApplied(nextConfig.localEndpoint, nextConfig.token);
      }
      return nextConfig;
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setConnectionError(message.includes('403') ? '连接配置只能在游戏所在设备的本机窗口中修改。' : message);
      throw err;
    } finally {
      setConnectionBusy(null);
    }
  }, [
    apiToken,
    applyConnectionConfigState,
    connectionBusy,
    endpoint,
    onConnectionConfigApplied,
  ]);

  const applyConnectionConfig = useCallback(() => {
    void submitConnectionConfig({
      lanEnabled: connectionLanEnabled,
      lanBindHost: connectionLanHost,
    }).catch(() => undefined);
  }, [connectionLanEnabled, connectionLanHost, submitConnectionConfig]);

  const toggleConnectionLanEnabled = useCallback((lanEnabled: boolean) => {
    const previousLanEnabled = connectionConfig?.lanEnabled ?? connectionLanEnabled;
    setConnectionLanEnabled(lanEnabled);
    void submitConnectionConfig({
      lanEnabled,
      lanBindHost: connectionLanHost,
    }).catch(() => {
      setConnectionLanEnabled(previousLanEnabled);
    });
  }, [connectionConfig?.lanEnabled, connectionLanEnabled, connectionLanHost, submitConnectionConfig]);

  const regenerateConnectionToken = useCallback(async () => {
    if (!apiToken || connectionBusy) return;

    setConnectionBusy('token');
    try {
      const nextConfig = await regenerateLocalApiToken(endpoint, apiToken);
      applyConnectionConfigState(nextConfig);
      if (nextConfig.localEndpoint && nextConfig.token) {
        onConnectionConfigApplied(nextConfig.localEndpoint, nextConfig.token);
      }
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setConnectionError(message.includes('403') ? 'Token 只能在游戏所在设备的本机窗口中重置。' : message);
    } finally {
      setConnectionBusy(null);
    }
  }, [apiToken, applyConnectionConfigState, connectionBusy, endpoint, onConnectionConfigApplied]);

  const copyConnectionText = useCallback(async (value: string, fallbackMessage: string) => {
    if (!value || connectionBusy) return;
    setConnectionBusy('copy');
    try {
      await navigator.clipboard.writeText(value);
      setConnectionError('');
    } catch {
      setConnectionError(fallbackMessage);
    } finally {
      setConnectionBusy(null);
    }
  }, [connectionBusy]);

  useEffect(() => {
    if (settingsTab !== 'connection') return;
    refreshConnectionConfig();
  }, [refreshConnectionConfig, settingsTab]);

  const updateStatus = updateManager.status;
  const updateStateLabel = formatUpdateState(updateStatus);
  const updateDetail = updateManager.error || updateStatus?.error || updateStatus?.installMessage || '';
  const remoteUpdateBusy = updateStatus?.state === 'checking'
    || updateStatus?.state === 'downloading'
    || isActiveUpdateInstallState(updateStatus?.installState ?? '');
  const canDownloadUpdate = Boolean(
    updateStatus?.hasUpdate
    && updateStatus.enabled
    && !updateStatus.staged
    && !remoteUpdateBusy,
  );
  const canInstallUpdate = Boolean(
    updateStatus?.staged
    && updateStatus.enabled
    && !remoteUpdateBusy,
  );
  const hostDraftDirty = connectionConfig
    ? normalizeLanHostDraft(connectionLanHost) !== normalizeLanHostDraft(connectionConfig.lanBindHost)
    : false;
  const connectionDraftDirty = connectionConfig
    ? connectionLanEnabled !== connectionConfig.lanEnabled || hostDraftDirty
    : false;
  const lanEndpoints = connectionConfig?.lanEndpoints ?? [];
  const lanEndpointStatus = connectionBusy === 'apply' && connectionLanEnabled
    ? '应用中'
    : hostDraftDirty
      ? '应用后刷新'
      : lanEndpoints.length > 0
        ? `${lanEndpoints.length} 个可用地址`
        : '未生成';
  const lanStatusLabel = !connectionConfig
    ? '未读取'
    : connectionBusy === 'apply' && connectionDraftDirty
      ? '应用中'
      : hostDraftDirty
        ? '监听地址待应用'
        : connectionConfig.lanEnabled
          ? connectionConfig.lanRunning ? '已开启' : '未监听'
          : '未开启';
  const tokenValue = connectionConfig?.token || apiToken;
  const tokenDisplayValue = connectionTokenVisible ? tokenValue : maskToken(tokenValue);

  return (
    <>
      <SettingHelpProvider resetKey={settingsTab}>
      <Tabs value={settingsTab} onValueChange={(value) => onSettingsTabChange(value as SettingsTab)} className="space-y-4">
      <TabsList scrollable className="grid h-9 w-full grid-cols-5">
        <TabsTrigger value="window" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          窗口
        </TabsTrigger>
        <TabsTrigger value="connection" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          连接
        </TabsTrigger>
        <TabsTrigger value="recommendation" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          推荐
        </TabsTrigger>
        <TabsTrigger value="experimental" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          实验性功能
        </TabsTrigger>
        <TabsTrigger value="updates" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          更新
        </TabsTrigger>
      </TabsList>

      <TabsContent value="window" className="space-y-4">
        <div className={DENSE_TWO_COLUMN_GRID}>
          <ListPanel title="窗口">
            <div className="space-y-4">
              <BackgroundOpacitySlider
                value={preferences.backgroundOpacity}
                onChange={(backgroundOpacity) => onPreferenceChange({ backgroundOpacity })}
              />
              <ContentOpacitySlider
                value={preferences.contentOpacity}
                onChange={(contentOpacity) => onPreferenceChange({ contentOpacity })}
              />
              {supportsDesktopWindowControls ? (
                <>
                  <SettingSegmentedControl
                    label="焦点切换"
                    helpId="window-focus-switch-behavior"
                    description="使用 F8 或 RS Click 切回游戏时，可以隐藏伴随窗口，也可以让窗口保持悬浮显示。此设置只适用于桌面窗口。"
                    value={preferences.focusSwitchBehavior}
                    options={[
                      { value: 'hide', label: '隐藏窗口' },
                      { value: 'keep-visible', label: '保持悬浮' },
                    ]}
                    onChange={(focusSwitchBehavior) => onPreferenceChange({ focusSwitchBehavior })}
                  />
                  <FocusSwitchCooldownInput
                    value={preferences.focusSwitchCooldownMs}
                    onChange={(focusSwitchCooldownMs) => onPreferenceChange({ focusSwitchCooldownMs })}
                  />
                  <SwitchControl
                    label="始终置顶"
                    helpId="window-always-on-top"
                    description="让伴随窗口保持在普通窗口和无边框游戏上方。独占全屏仍可能覆盖伴随窗口。"
                    checked={preferences.alwaysOnTop}
                    onCheckedChange={(alwaysOnTop) => onPreferenceChange({ alwaysOnTop })}
                  />
                  <SwitchControl
                    label="鼠标穿透锁定"
                    helpId="window-mouse-passthrough"
                    description="开启后伴随窗口会忽略鼠标点击，点击会落到下方游戏或其他窗口；按 F10、F8、RS Click 或使用托盘菜单可恢复操作。"
                    checked={preferences.mousePassthroughEnabled}
                    onCheckedChange={(mousePassthroughEnabled) => onPreferenceChange({ mousePassthroughEnabled })}
                  />
                </>
              ) : (
                <div className="steward-inline-panel px-3 py-2 text-xs text-muted-foreground">
                  Android 端仅保留显示设置；置顶、鼠标穿透和焦点切换由桌面窗口提供。
                </div>
              )}
            </div>
          </ListPanel>

          <ListPanel title="显示">
            <div className="space-y-4">
              <SettingSegmentedControl
                label="主题"
                helpId="window-theme"
                description="选择浅色、深色，或跟随当前设备的系统外观。只影响伴随窗口显示。"
                value={themeMode}
                options={[
                  { value: 'system', label: '跟随系统' },
                  { value: 'light', label: '浅色' },
                  { value: 'dark', label: '深色' },
                ]}
                onChange={onThemeModeChange}
              />
              <div className="flex items-end gap-2">
                <div className="min-w-0 flex-1">
                  <FontScaleSlider
                    value={preferences.fontScalePercent}
                    onChange={(fontScalePercent) => onPreferenceChange({ fontScalePercent })}
                  />
                </div>
                <Button
                  type="button"
                  size="icon-sm"
                  variant="ghost"
                  aria-label="恢复默认字体大小"
                  title="恢复默认字体大小"
                  disabled={preferences.fontScalePercent === DEFAULT_FONT_SCALE_PERCENT}
                  onClick={() => onPreferenceChange({ fontScalePercent: DEFAULT_FONT_SCALE_PERCENT })}
                >
                  <IconRefresh size={14} aria-hidden="true" />
                </Button>
              </div>
              <SwitchControl
                label="手柄导航"
                helpId="window-gamepad-navigation"
                description="控制伴随窗口内的方向、确认、返回、切页、滚动和收藏操作。关闭后，F8 与 RS Click 的窗口焦点切换仍然有效。"
                checked={preferences.gamepadNavigationEnabled}
                onCheckedChange={(gamepadNavigationEnabled) => onPreferenceChange({ gamepadNavigationEnabled })}
              />
              <SwitchControl
                label="显示调试信息"
                helpId="window-debug-details"
                description="开启后显示日志页、扫描状态、运行时来源、性能耗时和订单内部来源。普通使用建议保持关闭。"
                checked={preferences.showDebugDetails}
                onCheckedChange={(showDebugDetails) => onPreferenceChange({ showDebugDetails })}
              />
            </div>
          </ListPanel>

        </div>
      </TabsContent>

      <TabsContent value="connection" className="space-y-4">
        <ListPanel title="连接">
          <div className="space-y-4">
            <div className="grid gap-2 text-sm">
              <InfoLine label="本机地址" value={connectionConfig?.localEndpoint || endpoint} mono />
              <InfoLine label="端口" value={String(connectionConfig?.port ?? 32145)} />
              <InfoLine label="LAN 状态" value={lanStatusLabel} />
              <InfoLine label="LAN 地址" value={lanEndpointStatus} />
            </div>

            <SwitchControl
              label="允许局域网设备连接"
              helpId="connection-lan-enabled"
              description="允许同一可信局域网中的 Windows 或 Android 伴随窗口连接本机 Mod API。本机回环地址始终保留；不要通过公网端口映射暴露此接口。"
              checked={connectionLanEnabled}
              onCheckedChange={toggleConnectionLanEnabled}
              disabled={!apiToken || Boolean(connectionBusy)}
            />

            <SettingHelpField
              id="connection-lan-bind-host"
              label="LAN 监听地址"
              description="修改监听地址后需要点击应用。填写 auto 会监听活动网卡的私网 IPv4，也可以填写本机活动网卡上的一个明确地址；本机回环地址始终保留。"
              disabledControl={!connectionLanEnabled || !apiToken || Boolean(connectionBusy)}
            >
              {({ helpTrigger, descriptionId }) => (
                <div className="grid gap-1 text-sm">
                  <div className="flex min-w-0 items-center gap-1.5">
                    <label htmlFor="settings-lan-bind-host" className="min-w-0 text-muted-foreground">
                      LAN 监听地址
                    </label>
                    {helpTrigger}
                  </div>
                  <Input
                    id="settings-lan-bind-host"
                    value={connectionLanHost}
                    onChange={(event) => setConnectionLanHost(event.target.value)}
                    placeholder="auto"
                    disabled={!connectionLanEnabled || !apiToken || Boolean(connectionBusy)}
                    inputClassName="font-mono"
                    aria-describedby={descriptionId}
                  />
                </div>
              )}
            </SettingHelpField>

            <div className="grid gap-1.5">
              <div className="text-xs text-muted-foreground">局域网连接地址</div>
              {lanEndpoints.length > 0 && !hostDraftDirty ? (
                <div className="divide-y divide-border/50 border-y border-border/50">
                  {lanEndpoints.map((lanEndpoint) => (
                    <div
                      key={`${lanEndpoint.address}-${lanEndpoint.interfaceName}`}
                      className="flex min-w-0 items-center gap-3 py-2"
                    >
                      <div className="min-w-0 flex-1">
                        <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
                          <code className="min-w-0 break-all text-xs font-medium text-foreground">
                            {lanEndpoint.endpoint}
                          </code>
                          {lanEndpoint.recommended && (
                            <span className="shrink-0 text-xs font-semibold text-primary">推荐</span>
                          )}
                        </div>
                        <div className="mt-0.5 text-xs text-muted-foreground">
                          {formatLanEndpointDetail(lanEndpoint)}
                        </div>
                      </div>
                      <Button
                        type="button"
                        size="icon-sm"
                        variant="ghost"
                        className="shrink-0"
                        aria-label={`复制 ${lanEndpoint.endpoint}`}
                        title="复制此地址"
                        disabled={Boolean(connectionBusy)}
                        data-gamepad-focus-key={`settings:connection:copy-lan:${lanEndpoint.address}`}
                        onClick={() => void copyConnectionText(lanEndpoint.endpoint, '无法复制 LAN 地址。')}
                      >
                        <IconCopy size={14} />
                      </Button>
                    </div>
                  ))}
                </div>
              ) : (
                <div className="border-y border-border/50 py-2 text-xs text-muted-foreground">
                  {hostDraftDirty ? '应用监听地址后生成连接地址。' : '开启局域网连接后生成可用地址。'}
                </div>
              )}
            </div>

            <label className="grid gap-1 text-sm">
              <span className="text-muted-foreground">Token</span>
              <Input
                value={tokenDisplayValue}
                readOnly
                type={connectionTokenVisible ? 'text' : 'password'}
                inputClassName="font-mono"
              />
            </label>

            <div className="flex flex-wrap gap-2" data-gamepad-axis="x">
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconRefresh size={14} />}
                loading={connectionBusy === 'refresh'}
                disabled={!apiToken || Boolean(connectionBusy)}
                data-gamepad-focus-key="settings:connection:refresh"
                onClick={refreshConnectionConfig}
              >
                刷新
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled={!apiToken || Boolean(connectionBusy) || !connectionDraftDirty}
                data-gamepad-focus-key="settings:connection:apply"
                onClick={applyConnectionConfig}
              >
                应用
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconCopy size={14} />}
                disabled={!tokenValue || Boolean(connectionBusy)}
                data-gamepad-focus-key="settings:connection:copy-token"
                onClick={() => void copyConnectionText(tokenValue, '无法复制 Token。')}
              >
                复制 Token
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                data-gamepad-focus-key="settings:connection:toggle-token-visibility"
                onClick={() => setConnectionTokenVisible((current) => !current)}
              >
                {connectionTokenVisible ? '隐藏 Token' : '显示 Token'}
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconKey size={14} />}
                loading={connectionBusy === 'token'}
                disabled={!apiToken || Boolean(connectionBusy)}
                aria-controls="token-reset-dialog"
                aria-expanded={tokenResetDialogOpen}
                aria-haspopup="dialog"
                data-gamepad-dialog-trigger="true"
                data-gamepad-focus-key="settings:connection:reset-token"
                onClick={() => setTokenResetDialogOpen(true)}
              >
                重置 Token
              </Button>
            </div>

            {connectionError && (
              <div className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
                {connectionError}
              </div>
            )}
          </div>
        </ListPanel>
      </TabsContent>

      <TabsContent value="updates" className="space-y-4">
        <ListPanel title="更新">
          <div className="space-y-4">
            <div className="grid gap-2 text-sm">
              <InfoLine label="当前版本" value={updateStatus?.currentVersion || '未知'} />
              <InfoLine label="最新版本" value={updateStatus?.latestVersion || '未检查'} />
              <InfoLine label="状态" value={updateStateLabel} />
              <InfoLine label="自动检查" value={!updateStatus ? '未读取' : updateStatus.autoCheck ? '已开启' : '已关闭'} />
              <InfoLine label="更新通道" value={!updateStatus ? '未读取' : updateStatus.includePrerelease ? '含预发布版本' : '仅正式版本'} />
              <InfoLine label="最近成功检查" value={!updateStatus ? '未读取' : formatUpdateDateTime(updateStatus.lastSuccessAtUtc)} />
              <InfoLine label="下次自动检查" value={formatNextUpdateCheck(updateStatus)} />
              {(updateStatus?.consecutiveFailures ?? 0) > 0 && (
                <InfoLine label="连续检查失败" value={`${updateStatus?.consecutiveFailures} 次`} />
              )}
              <InfoLine label="更新包" value={updateStatus?.packageSize ? formatBytes(updateStatus.packageSize) : '未知'} />
            </div>
            {updateDetail && (
              <div className="steward-inline-panel px-3 py-2 text-xs text-muted-foreground">
                {updateDetail}
              </div>
            )}
            {updateStatus?.installState === 'waiting' && (
              <div className="text-xs text-muted-foreground">
                已打开独立更新程序；请在弹窗中确认关闭游戏并完成安装。
              </div>
            )}
            <div className="flex flex-wrap gap-2" data-gamepad-axis="x">
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconRefresh size={14} />}
                loading={updateManager.busy === 'check'}
                disabled={!updateManager.connected || Boolean(updateManager.busy) || remoteUpdateBusy}
                data-gamepad-focus-key="settings:updates:check"
                onClick={() => void updateManager.check()}
              >
                检查
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconDownload size={14} />}
                loading={updateManager.busy === 'download'}
                disabled={!updateManager.connected || Boolean(updateManager.busy) || !canDownloadUpdate}
                data-gamepad-focus-key="settings:updates:download"
                onClick={() => void updateManager.download()}
              >
                下载
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconPackageImport size={14} />}
                loading={updateManager.busy === 'install'}
                disabled={!updateManager.connected || Boolean(updateManager.busy) || !canInstallUpdate}
                data-gamepad-focus-key="settings:updates:install"
                onClick={() => void updateManager.install()}
              >
                打开安装程序
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconExternalLink size={14} />}
                disabled={!updateStatus?.releaseUrl}
                data-gamepad-focus-key="settings:updates:release-page"
                onClick={() => void updateManager.openReleasePage()}
              >
                发布页
              </Button>
            </div>
            <div className="text-xs text-muted-foreground">
              更新包会先下载到配置目录；安装阶段由独立更新程序显示进度，并在游戏退出后替换插件目录。
            </div>
          </div>
        </ListPanel>
      </TabsContent>

      <TabsContent value="recommendation" className="space-y-4">
        <div className={DENSE_TWO_COLUMN_GRID}>
          <ListPanel title="推荐设置">
            <div className="space-y-4">
              <SettingSegmentedControl
                label="经营中订单排序"
                helpId="recommendation-service-order-sort"
                description="点单顺序按订单进入经营的先后排列；稀客分组会把同一稀客的订单集中显示。此设置只改变页面顺序，不改变订单本身。"
                value={preferences.serviceOrderSortMode}
                options={[
                  { value: 'ordered', label: '点单顺序' },
                  { value: 'guest', label: '稀客分组' },
                ]}
                onChange={(serviceOrderSortMode) => onPreferenceChange({ serviceOrderSortMode })}
              />
              <SwitchControl
                label="稀客专注模式默认精简"
                helpId="recommendation-focus-compact"
                description="进入稀客订单专注模式时默认使用精简显示。料理和酒水显示数量仍可在专注模式内直接调整，并会自动记住。"
                checked={serviceFocusCompact}
                onCheckedChange={onServiceFocusCompactChange}
              />
              <SettingSegmentedControl
                label="预算处理"
                helpId="recommendation-budget-policy"
                description="阻止超预算会排除顾客资金不足的方案；仅提示会保留方案并标记预算风险；忽略预算不参与筛选。免费订单不受付款预算限制。"
                value={preferences.recommendationBudgetPolicy}
                options={[
                  { value: 'block', label: '阻止超预算' },
                  { value: 'warn', label: '仅提示' },
                  { value: 'ignore', label: '忽略预算' },
                ]}
                onChange={(recommendationBudgetPolicy) => onPreferenceChange({ recommendationBudgetPolicy })}
              />
              <SwitchControl
                label="排除缺失厨具"
                helpId="recommendation-filter-missing-cookers"
                description="进入经营场景并读取到完整厨具快照后，推荐列表会隐藏当前已摆放厨具无法制作的料理。厨具快照不完整时不会使用部分数据猜测。"
                checked={preferences.filterMissingCookers}
                onCheckedChange={(filterMissingCookers) => onPreferenceChange({ filterMissingCookers })}
              />
              <SwitchControl
                label="任务料理置顶"
                helpId="recommendation-mission-recipe-priority"
                description="已追踪任务的目标料理通过库存、预算、厨具和酒水点单条件后置顶；若启用相应自动化，收藏限定也必须满足。任务料理可以跳过本次普通料理点单 Tag，游戏内列表仍由游戏界面置顶推荐开关单独控制。"
                checked={preferences.missionRecipePriorityEnabled}
                onCheckedChange={(missionRecipePriorityEnabled) => onPreferenceChange({
                  missionRecipePriorityEnabled,
                })}
              />
              <SwitchControl
                label="收藏料理置顶"
                helpId="recommendation-pin-favorite-recipe"
                description="收藏料理只有在解锁、库存、预算和厨具等硬条件通过后才会排到其他普通料理前面，只影响料理排序。"
                checked={preferences.pinFavoriteRecipeEnabled}
                onCheckedChange={(pinFavoriteRecipeEnabled) => onPreferenceChange({ pinFavoriteRecipeEnabled })}
              />
              <SwitchControl
                label="收藏酒水置顶"
                helpId="recommendation-pin-favorite-beverage"
                description="收藏酒水只有在库存、预算和点单等硬条件通过后才会排到其他普通酒水前面，只影响酒水排序。"
                checked={preferences.pinFavoriteBeverageEnabled}
                onCheckedChange={(pinFavoriteBeverageEnabled) => onPreferenceChange({ pinFavoriteBeverageEnabled })}
              />
              <SettingHelpField
                id="recommendation-recipe-variant-limit"
                label="同基础料理显示"
                description="同一道基础料理只保留当前排序最靠前的指定数量，加料不同但排序靠后的变体会隐藏。主执行方案不会因为展示数量限制而丢失。"
              >
                {({ helpTrigger, descriptionId }) => (
                  <div className="flex items-center justify-between gap-3 text-sm">
                    <div className="flex min-w-0 items-center gap-1.5 text-muted-foreground">
                      <label htmlFor="settings-recipe-variant-limit" className="min-w-0">同基础料理显示</label>
                      {helpTrigger}
                    </div>
                    <NumberInput
                      id="settings-recipe-variant-limit"
                      min={MIN_RECIPE_VARIANT_LIMIT_PER_BASE}
                      max={MAX_RECIPE_VARIANT_LIMIT_PER_BASE}
                      value={preferences.recipeVariantLimitPerBase}
                      onValueChange={(recipeVariantLimitPerBase) => onPreferenceChange({
                        recipeVariantLimitPerBase: normalizeRecipeVariantLimitPerBase(recipeVariantLimitPerBase),
                      })}
                      className="h-8 w-16"
                      aria-describedby={descriptionId}
                    />
                  </div>
                )}
              </SettingHelpField>
              <SettingHelpField
                id="recommendation-excluded-ingredients"
                label="排除材料"
                description="推荐料理不会使用所选材料，基础配方和加料都会避开。右侧排序只改变候选材料在设置列表中的显示顺序。"
                disabledControl={ingredientOptions.length === 0}
              >
                {({ helpTrigger, descriptionId }) => (
                  <div className="space-y-2">
                    <div className="flex min-w-0 items-center justify-between gap-2">
                      <div className="flex min-w-0 items-center gap-1.5 text-sm font-medium">
                        <span className="min-w-0">排除材料</span>
                        {helpTrigger}
                      </div>
                      <InventorySortControl
                        value={ingredientExclusionSortMode}
                        onChange={setIngredientExclusionSortMode}
                        disabled={ingredientOptions.length === 0}
                        ariaLabel="排除材料排序"
                        ariaDescribedBy={descriptionId}
                      />
                    </div>
                    <MultiSelectBox
                      value={preferences.recommendationExclusions.excludedIngredientIds.map(String)}
                      options={ingredientOptions}
                      placeholder={ingredientOptions.length > 0 ? '选择不参与推荐的材料' : '暂无运行时材料数据'}
                      disabled={ingredientOptions.length === 0}
                      aria-describedby={descriptionId}
                      onValueChange={(values) => updateExclusions({ excludedIngredientIds: parseSelectedIds(values) })}
                    />
                  </div>
                )}
              </SettingHelpField>
              <SettingHelpField
                id="recommendation-excluded-beverages"
                label="排除酒水"
                description="推荐酒水会跳过所选项目。右侧排序只改变候选酒水在设置列表中的显示顺序。"
                disabledControl={beverageOptions.length === 0}
              >
                {({ helpTrigger, descriptionId }) => (
                  <div className="space-y-2">
                    <div className="flex min-w-0 items-center justify-between gap-2">
                      <div className="flex min-w-0 items-center gap-1.5 text-sm font-medium">
                        <span className="min-w-0">排除酒水</span>
                        {helpTrigger}
                      </div>
                      <InventorySortControl
                        value={beverageExclusionSortMode}
                        onChange={setBeverageExclusionSortMode}
                        disabled={beverageOptions.length === 0}
                        ariaLabel="排除酒水排序"
                        ariaDescribedBy={descriptionId}
                      />
                    </div>
                    <MultiSelectBox
                      value={preferences.recommendationExclusions.excludedBeverageIds.map(String)}
                      options={beverageOptions}
                      placeholder={beverageOptions.length > 0 ? '选择不参与推荐的酒水' : '暂无运行时酒水数据'}
                      disabled={beverageOptions.length === 0}
                      aria-describedby={descriptionId}
                      onValueChange={(values) => updateExclusions({ excludedBeverageIds: parseSelectedIds(values) })}
                    />
                  </div>
                )}
              </SettingHelpField>
              <Button
                type="button"
                size="sm"
                variant="outline"
                data-gamepad-focus-key="settings:recommendation:clear-exclusions"
                onClick={() => updateExclusions({ excludedIngredientIds: [], excludedBeverageIds: [] })}
                disabled={
                  preferences.recommendationExclusions.excludedIngredientIds.length === 0
                  && preferences.recommendationExclusions.excludedBeverageIds.length === 0
                }
              >
                清空排除
              </Button>
            </div>
          </ListPanel>

          <ListPanel title="推荐权重">
            <RecommendationSortProfileControl
              profile={preferences.recommendationSortProfile}
              filterMissingCookers={preferences.filterMissingCookers}
              onChange={(recommendationSortProfile) => onPreferenceChange({ recommendationSortProfile })}
            />
          </ListPanel>
        </div>
      </TabsContent>

      <TabsContent value="experimental" className="space-y-4">
        <div className={DENSE_TWO_COLUMN_GRID}>
          <ListPanel title="自动化总控">
            <div className="space-y-4">
              <SwitchControl
                label="启用自动化（实验性）"
                helpId="automation-enabled"
                description="关闭后会取消 Mod 当前持有的料理任务和排队命令，但不会清空厨具、返还材料或改动玩家已经取得的成品。教学经营会保留开关设置但暂停全部自动化动作。"
                checked={preferences.automationEnabled}
                onCheckedChange={(automationEnabled) => onPreferenceChange({ automationEnabled })}
              />
              <div className="grid grid-cols-1 gap-4 min-[960px]:grid-cols-2">
                <AutomationSliderField
                  label="稀客并发"
                  helpId="automation-rare-concurrency"
                  description={`同时允许进入处理流程的稀客订单数量，范围 ${MIN_AUTO_ORDER_CONCURRENCY} - ${MAX_RARE_AUTO_ORDER_CONCURRENCY}。修改后在下一轮自动化调度生效。`}
                  value={preferences.autoRareConcurrency}
                  min={MIN_AUTO_ORDER_CONCURRENCY}
                  max={MAX_RARE_AUTO_ORDER_CONCURRENCY}
                  onChange={(autoRareConcurrency) => onPreferenceChange({ autoRareConcurrency })}
                />
                <AutomationSliderField
                  label="普客并发"
                  helpId="automation-normal-concurrency"
                  description={`同时允许进入处理流程的普客订单数量，范围 ${MIN_AUTO_ORDER_CONCURRENCY} - ${MAX_NORMAL_AUTO_ORDER_CONCURRENCY}。修改后在下一轮自动化调度生效。`}
                  value={preferences.autoNormalConcurrency}
                  min={MIN_AUTO_ORDER_CONCURRENCY}
                  max={MAX_NORMAL_AUTO_ORDER_CONCURRENCY}
                  onChange={(autoNormalConcurrency) => onPreferenceChange({ autoNormalConcurrency })}
                />
                <AutomationSliderField
                  label="最大重试"
                  helpId="automation-max-step-retries"
                  description={`同一订单阶段执行失败时允许自动重试的最大次数，范围 ${MIN_AUTO_STEP_RETRIES} - ${MAX_AUTO_STEP_RETRIES_LIMIT}。达到上限后会暂停该订单，避免无限重复副作用。`}
                  value={preferences.autoMaxStepRetries}
                  min={MIN_AUTO_STEP_RETRIES}
                  max={MAX_AUTO_STEP_RETRIES_LIMIT}
                  onChange={(autoMaxStepRetries) => onPreferenceChange({ autoMaxStepRetries })}
                />
                <AutomationSliderField
                  label="最大回退"
                  helpId="automation-max-rollbacks"
                  description={`同一执行目标因玩家操作、成品不符或运行时事实变化而重新制作的最大次数，范围 ${MIN_AUTO_ROLLBACKS} - ${MAX_AUTO_ROLLBACKS_LIMIT}。特殊经营目标真正轮换后使用新的回退预算。`}
                  value={preferences.autoMaxRollbacks}
                  min={MIN_AUTO_ROLLBACKS}
                  max={MAX_AUTO_ROLLBACKS_LIMIT}
                  onChange={(autoMaxRollbacks) => onPreferenceChange({ autoMaxRollbacks })}
                />
              </div>
            </div>
          </ListPanel>

          <ListPanel title="游戏界面辅助">
            <div className="grid grid-cols-1 gap-5 min-[900px]:grid-cols-2">
              <div className="space-y-4 border-l pl-3">
                <div className="text-sm font-medium">稀客目标</div>
                <SwitchControl
                  label="稀客游戏界面置顶推荐（实验性）"
                  helpId="recommendation-rare-game-ui-pinning"
                  description="打开游戏的料理或酒水选择界面时，把当前稀客目标的推荐材料、料理和酒水排到前面并显示稀客目标色。此功能不修改库存。"
                  checked={preferences.rareGameUiPinningEnabled}
                  onCheckedChange={(rareGameUiPinningEnabled) => onPreferenceChange({ rareGameUiPinningEnabled })}
                />
                <div className="border-l pl-3">
                  <SwitchControl
                    label="稀客加料料理选项（实验性）"
                    helpId="recommendation-rare-recipe-variant"
                    description="稀客目标料理含加料时，在制作页面显示独立选项。选择后只加入该方案的加料，并按游戏规则扣除材料；基础料理保持原配方，选项使用稀客目标色。"
                    checked={preferences.rareRecipeVariantEnabled}
                    disabled={!preferences.rareGameUiPinningEnabled}
                    status={!preferences.rareGameUiPinningEnabled ? '需先开启稀客游戏界面置顶推荐' : undefined}
                    onCheckedChange={(rareRecipeVariantEnabled) => onPreferenceChange({ rareRecipeVariantEnabled })}
                  />
                </div>
                <SwitchControl
                  label="稀客目标厨具高亮（实验性）"
                  helpId="recommendation-rare-cooker-highlight"
                  description="高亮当前稀客主方案需要的已摆放厨具。此功能只改变可见提示，不自动操作厨具。"
                  checked={preferences.rareCookerHighlightEnabled}
                  onCheckedChange={(rareCookerHighlightEnabled) => onPreferenceChange({ rareCookerHighlightEnabled })}
                />
                <SwitchControl
                  label="稀客目标桌位高亮（实验性）"
                  helpId="recommendation-rare-seat-highlight"
                  description="高亮当前稀客目标的桌位；不影响玩家原生选中效果，也不操作顾客。"
                  checked={preferences.rareSeatHighlightEnabled}
                  onCheckedChange={(rareSeatHighlightEnabled) => onPreferenceChange({ rareSeatHighlightEnabled })}
                />
                <SwitchControl
                  label="稀客目标订单高亮（实验性）"
                  helpId="recommendation-rare-order-highlight"
                  description="高亮游戏左下 HUD 稀客订单卡片和投掷送达面板中的稀客目标订单；不切换游戏原生焦点。"
                  checked={preferences.rareOrderHighlightEnabled}
                  onCheckedChange={(rareOrderHighlightEnabled) => onPreferenceChange({ rareOrderHighlightEnabled })}
                />
                <TargetHighlightColorField
                  kindLabel="稀客"
                  helpId="recommendation-rare-highlight-color"
                  value={preferences.rareTargetHighlightColor}
                  defaultValue={DEFAULT_RARE_TARGET_HIGHLIGHT_COLOR}
                  onChange={(rareTargetHighlightColor) => onPreferenceChange({ rareTargetHighlightColor })}
                />
              </div>
              <div className="space-y-4 border-l pl-3">
                <div className="text-sm font-medium">普客目标</div>
                <SwitchControl
                  label="普客游戏界面置顶推荐（实验性）"
                  helpId="recommendation-normal-game-ui-pinning"
                  description="打开游戏的料理或酒水选择界面时，把当前普客目标的推荐材料、料理和酒水排到前面并显示普客目标色。此功能不修改库存。"
                  checked={preferences.normalGameUiPinningEnabled}
                  onCheckedChange={(normalGameUiPinningEnabled) => onPreferenceChange({ normalGameUiPinningEnabled })}
                />
                <div className="border-l pl-3">
                  <SwitchControl
                    label="普客加料料理选项（实验性）"
                    helpId="recommendation-normal-recipe-variant"
                    description="普客目标料理含加料时，在制作页面显示独立选项。选择后只加入该方案的加料，并按游戏规则扣除材料；基础料理保持原配方，选项使用普客目标色。"
                    checked={preferences.normalRecipeVariantEnabled}
                    disabled={!preferences.normalGameUiPinningEnabled}
                    status={!preferences.normalGameUiPinningEnabled ? '需先开启普客游戏界面置顶推荐' : undefined}
                    onCheckedChange={(normalRecipeVariantEnabled) => onPreferenceChange({ normalRecipeVariantEnabled })}
                  />
                </div>
                <SwitchControl
                  label="普客目标厨具高亮（实验性）"
                  helpId="recommendation-normal-cooker-highlight"
                  description="高亮当前普客主方案需要的已摆放厨具。此功能只改变可见提示，不自动操作厨具。"
                  checked={preferences.normalCookerHighlightEnabled}
                  onCheckedChange={(normalCookerHighlightEnabled) => onPreferenceChange({ normalCookerHighlightEnabled })}
                />
                <SwitchControl
                  label="普客目标桌位高亮（实验性）"
                  helpId="recommendation-normal-seat-highlight"
                  description="高亮当前普客目标的桌位；不影响玩家原生选中效果，也不操作顾客。"
                  checked={preferences.normalSeatHighlightEnabled}
                  onCheckedChange={(normalSeatHighlightEnabled) => onPreferenceChange({ normalSeatHighlightEnabled })}
                />
                <SwitchControl
                  label="普客目标订单高亮（实验性）"
                  helpId="recommendation-normal-order-highlight"
                  description="高亮游戏左下 HUD 普客订单卡片和投掷送达面板中的普客目标订单；不切换游戏原生焦点。"
                  checked={preferences.normalOrderHighlightEnabled}
                  onCheckedChange={(normalOrderHighlightEnabled) => onPreferenceChange({ normalOrderHighlightEnabled })}
                />
                <TargetHighlightColorField
                  kindLabel="普客"
                  helpId="recommendation-normal-highlight-color"
                  value={preferences.normalTargetHighlightColor}
                  defaultValue={DEFAULT_NORMAL_TARGET_HIGHLIGHT_COLOR}
                  onChange={(normalTargetHighlightColor) => onPreferenceChange({ normalTargetHighlightColor })}
                />
              </div>
            </div>
          </ListPanel>
        </div>

        <div className={DENSE_TWO_COLUMN_GRID}>
          <ListPanel title="稀客自动化设置">
            <div className="space-y-4">
              <SwitchControl
                label="启用稀客处理"
                helpId="automation-rare-enabled"
                description="单独控制稀客订单是否进入自动化调度。关闭后保留各阶段设置，但会停止新的稀客处理并取消尚未进入不可逆阶段的稀客料理任务。"
                checked={preferences.autoRareOrderEnabled}
                onCheckedChange={(autoRareOrderEnabled) => onPreferenceChange({ autoRareOrderEnabled })}
              />
              <SwitchControl
                label="自动送达酒水"
                helpId="automation-rare-take-beverage"
                description="为稀客订单选择并直接送达推荐酒水。开启时会同时开启自动完成订单，避免酒水和料理均已送达后订单失去原生完成入口。"
                checked={preferences.autoPrepTakeBeverage}
                disabled={!preferences.autoRareOrderEnabled}
                onCheckedChange={setRareBeverageDelivery}
              />
              <SwitchControl
                label="自动开始料理"
                helpId="automation-rare-start-cooking"
                description="为稀客订单选择厨具、投入推荐料理及加料并自动完成 QTE。未开启自动送达料理时，成品会留给玩家自行取出和送达。"
                checked={preferences.autoPrepStartCooking}
                disabled={!preferences.autoRareOrderEnabled}
                onCheckedChange={(autoPrepStartCooking) => onPreferenceChange({ autoPrepStartCooking })}
              />
              <SwitchControl
                label="自动送达料理"
                helpId="automation-rare-deliver-food"
                description="料理完成后直接送达稀客订单。开启时会同时开启自动完成订单，避免 Mod 送入最后一项后订单停在未评价状态。"
                checked={preferences.autoPrepCollectCooking}
                disabled={!preferences.autoRareOrderEnabled}
                onCheckedChange={setRareFoodDelivery}
              />
              <SwitchControl
                label="自动完成订单"
                helpId="automation-rare-complete-order"
                description="酒水和料理均送达后调用游戏已验证的评价入口完成稀客订单。关闭时会同时关闭自动送达酒水和自动送达料理，仍可只自动开始料理后由玩家接管。"
                checked={preferences.autoPrepCompleteOrder}
                disabled={!preferences.autoRareOrderEnabled}
                onCheckedChange={setRareOrderCompletion}
              />
              <SwitchControl
                label="出错时暂停"
                helpId="automation-rare-stop-on-error"
                description="稀客自动化步骤失败时暂停对应订单，等待手动重试、重置或安全栅栏确认。关闭后仍受最大重试和最大回退次数限制。"
                checked={preferences.autoPrepStopOnError}
                disabled={!preferences.autoRareOrderEnabled}
                onCheckedChange={(autoPrepStopOnError) => onPreferenceChange({ autoPrepStopOnError })}
              />
              <div className="border-t pt-4">
                <div className="mb-3 text-sm font-medium text-foreground">稀客限定</div>
                <div className="space-y-4">
                  <SwitchControl
                    label="只处理收藏料理"
                    helpId="automation-rare-recipe-favorites-only"
                    description="稀客自动化只选择已收藏的料理。收藏中没有满足订单、库存和厨具硬门禁的料理时，该订单不会开始制作。"
                    checked={preferences.autoPrepRecipeFavoritesOnly}
                    disabled={!preferences.autoRareOrderEnabled}
                    onCheckedChange={(autoPrepRecipeFavoritesOnly) => onPreferenceChange({ autoPrepRecipeFavoritesOnly })}
                  />
                  <SwitchControl
                    label="只处理收藏酒水"
                    helpId="automation-rare-beverage-favorites-only"
                    description="稀客自动化只选择已收藏的酒水。收藏中没有满足点单与库存硬门禁的酒水时，该订单不会自动送达酒水。"
                    checked={preferences.autoPrepBeverageFavoritesOnly}
                    disabled={!preferences.autoRareOrderEnabled}
                    onCheckedChange={(autoPrepBeverageFavoritesOnly) => onPreferenceChange({ autoPrepBeverageFavoritesOnly })}
                  />
                </div>
              </div>
            </div>
          </ListPanel>

          <ListPanel title="普客自动化设置">
            <div className="space-y-4">
              <SwitchControl
                label="启用普客处理"
                helpId="automation-normal-enabled"
                description="单独控制普客订单是否进入自动化调度。关闭后保留各阶段设置，但会停止新的普客处理并取消尚未进入不可逆阶段的普客料理任务。"
                checked={preferences.autoNormalOrderEnabled}
                onCheckedChange={(autoNormalOrderEnabled) => onPreferenceChange({ autoNormalOrderEnabled })}
              />
              <SwitchControl
                label="自动送达酒水"
                helpId="automation-normal-take-beverage"
                description="为普客订单选择并直接送达指定酒水。开启时会同时开启自动完成订单，避免酒水和料理均已送达后订单失去原生完成入口。"
                checked={preferences.autoNormalTakeBeverage}
                disabled={!preferences.autoNormalOrderEnabled}
                onCheckedChange={setNormalBeverageDelivery}
              />
              <SwitchControl
                label="自动开始料理"
                helpId="automation-normal-start-cooking"
                description="为普客订单选择厨具、投入指定料理并自动完成 QTE。未开启自动送达料理时，成品会留给玩家自行取出和送达。"
                checked={preferences.autoNormalStartCooking}
                disabled={!preferences.autoNormalOrderEnabled}
                onCheckedChange={(autoNormalStartCooking) => onPreferenceChange({ autoNormalStartCooking })}
              />
              <SwitchControl
                label="自动送达料理"
                helpId="automation-normal-deliver-food"
                description="料理完成后直接送达普客订单。开启时会同时开启自动完成订单，避免 Mod 送入最后一项后订单停在未评价状态。"
                checked={preferences.autoNormalDeliverFood}
                disabled={!preferences.autoNormalOrderEnabled}
                onCheckedChange={setNormalFoodDelivery}
              />
              <SwitchControl
                label="自动完成订单"
                helpId="automation-normal-complete-order"
                description="酒水和料理均送达后调用游戏已验证的评价入口完成普客订单。关闭时会同时关闭自动送达酒水和自动送达料理，仍可只自动开始料理后由玩家接管。"
                checked={preferences.autoNormalCompleteOrder}
                disabled={!preferences.autoNormalOrderEnabled}
                onCheckedChange={setNormalOrderCompletion}
              />
              <SwitchControl
                label="出错时暂停"
                helpId="automation-normal-stop-on-error"
                description="普客自动化步骤失败时暂停对应订单，等待手动重试、重置或安全栅栏确认。关闭后仍受最大重试和最大回退次数限制。"
                checked={preferences.autoNormalStopOnError}
                disabled={!preferences.autoNormalOrderEnabled}
                onCheckedChange={(autoNormalStopOnError) => onPreferenceChange({ autoNormalStopOnError })}
              />
            </div>
          </ListPanel>
        </div>
      </TabsContent>
      </Tabs>
      </SettingHelpProvider>

      <Dialog
        id="token-reset-dialog"
        opened={tokenResetDialogOpen}
        onClose={() => setTokenResetDialogOpen(false)}
        returnFocusKey="settings:connection:reset-token"
        title="重置连接 Token"
      >
        <p className="text-muted-foreground">
          重置后，其他设备需要重新输入新 Token 才能连接。确定继续？
        </p>
        <div className="flex justify-end gap-2" data-gamepad-axis="x">
          <Button
            type="button"
            size="sm"
            variant="outline"
            data-autofocus
            data-gamepad-dialog-default="true"
            data-gamepad-focus-key="settings:connection:reset-token:cancel"
            onClick={() => setTokenResetDialogOpen(false)}
          >
            取消
          </Button>
          <Button
            type="button"
            size="sm"
            variant="destructive"
            data-gamepad-focus-key="settings:connection:reset-token:confirm"
            onClick={() => {
              setTokenResetDialogOpen(false);
              void regenerateConnectionToken();
            }}
          >
            重置 Token
          </Button>
        </div>
      </Dialog>
    </>
  );
}

function RecommendationSortProfileControl({
  profile,
  filterMissingCookers,
  onChange,
}: {
  profile: RecommendationSortProfile;
  filterMissingCookers: boolean;
  onChange: (profile: RecommendationSortProfile) => void;
}) {
  const updateObjective = (
    key: RecommendationObjectiveKey,
    next: Partial<{ enabled: boolean; weight: number }>,
  ) => {
    onChange({
      ...profile,
      objectives: profile.objectives.map((rule) => (
        rule.key === key
          ? {
            ...rule,
            ...next,
            weight: next.weight === undefined ? rule.weight : clampWeight(next.weight),
          }
          : rule
      )),
    });
  };

  return (
    <div className="space-y-4">
      <SettingSegmentedControl
        label="权重方案"
        helpId="recommendation-weight-preset"
        description="选择预设会重新载入该方案的默认权重；之后可以逐项启用、停用或调整权重。重置当前方案会恢复所选预设。"
        value={profile.preset}
        options={RECOMMENDATION_SORT_PRESETS.map((preset) => ({
          value: preset.id,
          label: preset.label,
        }))}
        onChange={(preset: RecommendationSortPresetId) => onChange(buildDefaultRecommendationSortProfile(preset))}
      />
      <div className="space-y-2">
        {RECOMMENDATION_OBJECTIVE_DEFINITIONS.map((definition) => {
          const rule = profile.objectives.find((item) => item.key === definition.key);
          if (!rule) return null;
          const disabledByHardFilter = definition.key === 'cookerAvailable' && filterMissingCookers;
          const controlDisabled = disabledByHardFilter;
          const description = disabledByHardFilter
            ? <>{definition.description} 当前已由“排除缺失厨具”硬过滤接管，此软排序项不参与结果。</>
            : definition.description;

          return (
            <SettingHelpField
              key={definition.key}
              id={`recommendation-weight-${definition.key}`}
              label={definition.label}
              description={description}
              disabledControl={controlDisabled}
            >
              {({ helpTrigger, descriptionId }) => (
                <div className="steward-data-row p-2">
                  <div className="grid min-w-0 gap-2">
                    <div className="flex min-w-0 items-center justify-between gap-2">
                      <div className="flex min-w-0 flex-1 items-center gap-1">
                        <SwitchField
                          label={definition.label}
                          checked={rule.enabled}
                          disabled={controlDisabled}
                          onCheckedChange={(enabled) => updateObjective(definition.key, { enabled })}
                          className="min-w-0 flex-1"
                          aria-describedby={descriptionId}
                        />
                        {helpTrigger}
                      </div>
                      <span className={rule.enabled && !controlDisabled ? 'shrink-0 text-right text-sm tabular-nums' : 'shrink-0 text-right text-sm tabular-nums text-muted-foreground'}>
                        {rule.weight}
                      </span>
                    </div>
                    {disabledByHardFilter && (
                      <div className="text-xs text-muted-foreground">硬过滤已接管</div>
                    )}
                    <Slider
                      value={rule.weight}
                      min={0}
                      max={100}
                      step={5}
                      disabled={!rule.enabled || controlDisabled}
                      aria-label={`${definition.label}权重`}
                      aria-describedby={descriptionId}
                      className="min-w-0"
                      onValueChange={(weight) => updateObjective(definition.key, { weight })}
                    />
                  </div>
                </div>
              )}
            </SettingHelpField>
          );
        })}
      </div>
      <Button
        type="button"
        size="sm"
        variant="outline"
        onClick={() => onChange(buildDefaultRecommendationSortProfile(profile.preset))}
      >
        重置当前方案
      </Button>
    </div>
  );
}

function TargetHighlightColorField({
  kindLabel,
  helpId,
  value,
  defaultValue,
  onChange,
}: {
  kindLabel: string;
  helpId: string;
  value: string;
  defaultValue: string;
  onChange: (value: string) => void;
}) {
  const [draft, setDraft] = useState<string | null>(null);
  const displayedValue = draft ?? value;

  const commitDraft = useCallback(() => {
    if (/^#[0-9A-Fa-f]{6}$/.test(displayedValue)) {
      onChange(normalizeTargetHighlightColor(displayedValue, defaultValue));
    }
    setDraft(null);
  }, [defaultValue, displayedValue, onChange]);

  return (
    <SettingHelpField
      id={helpId}
      label={`${kindLabel}高亮色`}
      description={`设置${kindLabel}目标在料理、材料、酒水、厨具、桌位和订单区域使用的基础颜色。格式固定为 #RRGGBB。`}
    >
      {({ helpTrigger, descriptionId }) => (
        <div className="space-y-2">
          <div className="flex min-w-0 items-center gap-1.5 text-sm font-medium">
            <span>{kindLabel}高亮色</span>
            {helpTrigger}
          </div>
          <div className="flex items-center gap-2">
            <Input
              type="color"
              value={value}
              aria-label={`${kindLabel}高亮色选择器`}
              aria-describedby={descriptionId}
              className="w-10 shrink-0"
              inputClassName="h-8 cursor-pointer p-1"
              onChange={(event) => {
                setDraft(null);
                onChange(event.currentTarget.value.toUpperCase());
              }}
            />
            <Input
              value={displayedValue}
              maxLength={7}
              spellCheck={false}
              aria-label={`${kindLabel}高亮色十六进制值`}
              aria-describedby={descriptionId}
              className="min-w-0 flex-1"
              inputClassName="h-8 font-mono uppercase"
              onChange={(event) => setDraft(event.currentTarget.value.toUpperCase())}
              onBlur={commitDraft}
              onKeyDown={(event) => {
                if (event.key === 'Enter') event.currentTarget.blur();
                if (event.key === 'Escape') {
                  event.preventDefault();
                  setDraft(null);
                }
              }}
            />
            <Button
              type="button"
              size="sm"
              variant="outline"
              className="shrink-0"
              disabled={value === defaultValue}
              onClick={() => {
                setDraft(null);
                onChange(defaultValue);
              }}
            >
              恢复
            </Button>
          </div>
        </div>
      )}
    </SettingHelpField>
  );
}

function clampWeight(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, Math.trunc(value)));
}

function normalizeLanHostDraft(value: string): string {
  const normalized = value.trim().toLowerCase();
  return normalized || 'auto';
}

function maskToken(value: string): string {
  if (!value) return '';
  if (value.length <= 8) return '*'.repeat(value.length);
  return `${value.slice(0, 4)}${'*'.repeat(Math.max(8, value.length - 8))}${value.slice(-4)}`;
}

function formatLanEndpointDetail(endpoint: LocalApiConnectionConfig['lanEndpoints'][number]): string {
  const interfaceType = formatLanInterfaceType(endpoint.interfaceType);
  const interfaceLabel = endpoint.interfaceName || interfaceType || '未知网络接口';
  const details = [interfaceLabel];
  if (endpoint.interfaceName && interfaceType && endpoint.interfaceName !== interfaceType) details.push(interfaceType);
  if (endpoint.hasGateway) details.push('默认网关');
  if (endpoint.linkLocal) details.push('链路本地');
  return details.join(' · ');
}

function formatLanInterfaceType(value: string): string {
  switch (value.trim().toLowerCase()) {
    case 'wireless80211':
      return '无线网卡';
    case 'ethernet':
    case 'fastethernett':
    case 'fastethernetfx':
    case 'gigabitethernet':
      return '以太网';
    case 'tunnel':
      return '隧道';
    case 'ppp':
      return 'PPP / VPN';
    default:
      return value;
  }
}

function formatUpdateState(status: UpdateStatusResponse | null): string {
  if (!status) return '等待本地 API';
  if (!status.enabled) return '已关闭';
  switch (status.installState) {
    case 'waiting':
      return '更新程序已打开';
    case 'preparing':
      return '正在准备安装';
    case 'closing-companion':
      return '正在关闭伴随窗口';
    case 'waiting-game':
      return '等待游戏退出';
    case 'terminating-game':
      return '正在关闭游戏';
    case 'game-closed':
      return '游戏已退出';
    case 'backing-up':
      return '正在备份';
    case 'installing':
      return '正在安装';
    case 'verifying':
      return '正在校验';
    case 'succeeded':
      return '安装完成';
    case 'failed':
      return '安装失败';
    case 'cancelled':
      return '已取消安装';
  }
  if (status.staged) return '已下载';
  if (status.hasUpdate) return '有新版本';
  switch (status.state) {
    case 'checking':
      return '检查中';
    case 'downloading':
      return '下载中';
    case 'current':
      return '已是最新';
    case 'installed':
      return '安装完成';
    case 'failed':
      return '检查失败';
    case 'disabled':
      return '已关闭';
    default:
      return '未检查';
  }
}

function isActiveUpdateInstallState(state: UpdateStatusResponse['installState']): boolean {
  return state !== '' && state !== 'succeeded' && state !== 'failed' && state !== 'cancelled';
}

function formatUpdateDateTime(value: string | null | undefined): string {
  if (!value) return '未记录';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return '未记录';
  return parsed.toLocaleString('zh-CN', { hour12: false });
}

function formatNextUpdateCheck(status: UpdateStatusResponse | null): string {
  if (!status) return '未读取';
  if (!status.enabled || !status.autoCheck) return '未计划';
  return formatUpdateDateTime(status.nextCheckAtUtc);
}

function parseSelectedIds(values: string[]): number[] {
  const seen = new Set<number>();
  const ids: number[] = [];
  for (const value of values) {
    const id = Number(value);
    if (!Number.isFinite(id) || id < 0) continue;
    const normalized = Math.trunc(id);
    if (seen.has(normalized)) continue;
    seen.add(normalized);
    ids.push(normalized);
  }
  return ids.sort((left, right) => left - right);
}
