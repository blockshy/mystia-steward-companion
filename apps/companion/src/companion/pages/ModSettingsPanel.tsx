import { useCallback, useEffect, useMemo, useState } from 'react';
import { IconDownload, IconExternalLink, IconPackageImport, IconRefresh } from '@tabler/icons-react';
import { Button, InfoLine, ListPanel, MultiSelectBox, NumberInput, Slider, SwitchField, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import { checkForUpdates, downloadUpdate, installUpdateOnExit, readLogSettings, readUpdateStatus, writeLogSettings } from '@/companion/api';
import { buildInventorySelectOptions, type InventorySortMode } from '@/companion/domain/inventory-sorting';
import { formatBytes } from '@/companion/formatters';
import { openProjectReleaseUrl } from '@/lib/external-url';
import {
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
  type CompanionPreferences,
} from '@/companion/preferences';
import type { LocalApiLogSettings, RuntimeSets, SettingsTab, UpdateStatusResponse } from '@/companion/types';
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
  onPreferenceChange,
  onThemeModeChange,
  onServiceFocusCompactChange,
}: {
  endpoint: string;
  apiToken: string;
  preferences: CompanionPreferences;
  data: RecommendationDataSet;
  runtimeSets: RuntimeSets | null;
  themeMode: ThemeMode;
  serviceFocusCompact: boolean;
  onPreferenceChange: (next: Partial<CompanionPreferences>) => void;
  onThemeModeChange: (mode: ThemeMode) => void;
  onServiceFocusCompactChange: (value: boolean) => void;
}) {
  const [logSettings, setLogSettings] = useState<LocalApiLogSettings | null>(null);
  const [consoleBusy, setConsoleBusy] = useState(false);
  const [consoleError, setConsoleError] = useState('');
  const [updateStatus, setUpdateStatus] = useState<UpdateStatusResponse | null>(null);
  const [updateBusy, setUpdateBusy] = useState<'check' | 'download' | 'install' | null>(null);
  const [updateError, setUpdateError] = useState('');
  const [settingsTab, setSettingsTab] = useState<SettingsTab>('window');
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

  const refreshConsoleSettings = useCallback(async () => {
    if (!apiToken) {
      setLogSettings(null);
      return;
    }

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    try {
      const nextSettings = await readLogSettings(endpoint, apiToken, abortController.signal);
      setLogSettings(nextSettings);
      setConsoleError('');
    } catch (err) {
      setConsoleError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
    }
  }, [apiToken, endpoint]);

  const setNativeConsoleEnabled = useCallback(async (nativeConsole: boolean) => {
    if (!apiToken) return;

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    setConsoleBusy(true);
    try {
      const nextSettings = await writeLogSettings(
        endpoint,
        apiToken,
        { nativeConsole },
        abortController.signal,
      );
      setLogSettings(nextSettings);
      setConsoleError('');
    } catch (err) {
      setConsoleError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
      setConsoleBusy(false);
    }
  }, [apiToken, endpoint]);

  const refreshUpdateStatus = useCallback(async () => {
    if (!apiToken) {
      setUpdateStatus(null);
      return;
    }

    const abortController = new AbortController();
    const timeoutId = window.setTimeout(() => abortController.abort(), 2800);
    try {
      const status = await readUpdateStatus(endpoint, apiToken, abortController.signal);
      setUpdateStatus(status);
      setUpdateError('');
    } catch (err) {
      setUpdateError(err instanceof Error ? err.message : String(err));
    } finally {
      window.clearTimeout(timeoutId);
    }
  }, [apiToken, endpoint]);

  const runUpdateAction = useCallback(async (
    action: 'check' | 'download' | 'install',
    request: () => Promise<UpdateStatusResponse>,
  ) => {
    if (!apiToken || updateBusy) return;
    setUpdateBusy(action);
    try {
      const status = await request();
      setUpdateStatus(status);
      setUpdateError(status.error ?? '');
    } catch (err) {
      setUpdateError(err instanceof Error ? err.message : String(err));
    } finally {
      setUpdateBusy(null);
    }
  }, [apiToken, updateBusy]);

  const openReleasePage = useCallback(async () => {
    const url = updateStatus?.releaseUrl;
    if (!url) return;
    try {
      setUpdateError('');
      await openProjectReleaseUrl(url);
    } catch (err) {
      setUpdateError(`无法打开发布页：${err instanceof Error ? err.message : String(err)}`);
    }
  }, [updateStatus?.releaseUrl]);

  useEffect(() => {
    if (!preferences.showDebugDetails) return;
    refreshConsoleSettings();
  }, [preferences.showDebugDetails, refreshConsoleSettings]);

  useEffect(() => {
    refreshUpdateStatus();
  }, [refreshUpdateStatus]);

  useEffect(() => {
    if (!preferences.showDebugDetails && settingsTab === 'debug') {
      setSettingsTab('window');
    }
  }, [preferences.showDebugDetails, settingsTab]);

  const updateStateLabel = formatUpdateState(updateStatus);
  const updateDetail = updateError || updateStatus?.error || updateStatus?.installMessage || '';
  const canDownloadUpdate = Boolean(updateStatus?.hasUpdate && updateStatus.enabled);
  const canInstallUpdate = Boolean(updateStatus?.staged && updateStatus.enabled);

  return (
    <Tabs value={settingsTab} onValueChange={(value) => setSettingsTab(value as SettingsTab)} className="space-y-4">
      <TabsList className={preferences.showDebugDetails ? 'grid h-9 w-full grid-cols-5' : 'grid h-9 w-full grid-cols-4'}>
        <TabsTrigger value="window" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          窗口
        </TabsTrigger>
        <TabsTrigger value="recommendation" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          推荐
        </TabsTrigger>
        <TabsTrigger value="automation" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          自动化
        </TabsTrigger>
        <TabsTrigger value="updates" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          更新
        </TabsTrigger>
        {preferences.showDebugDetails && (
          <TabsTrigger value="debug" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
            调试
          </TabsTrigger>
        )}
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
              <SettingSegmentedControl
                label="焦点切换"
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
                checked={preferences.alwaysOnTop}
                onCheckedChange={(alwaysOnTop) => onPreferenceChange({ alwaysOnTop })}
              />
              <SwitchControl
                label="鼠标穿透锁定"
                checked={preferences.mousePassthroughEnabled}
                onCheckedChange={(mousePassthroughEnabled) => onPreferenceChange({ mousePassthroughEnabled })}
              />
              <div className="text-xs text-muted-foreground">
                开启后伴随窗口会忽略鼠标点击，点击会落到下方游戏或其他窗口；按 F10、F8/RS Click 或托盘菜单可恢复操作。
              </div>
            </div>
          </ListPanel>

          <ListPanel title="显示">
            <div className="space-y-4">
              <SettingSegmentedControl
                label="主题"
                value={themeMode}
                options={[
                  { value: 'system', label: '跟随系统' },
                  { value: 'light', label: '浅色' },
                  { value: 'dark', label: '深色' },
                ]}
                onChange={onThemeModeChange}
              />
              <SwitchControl
                label="手柄导航"
                checked={preferences.gamepadNavigationEnabled}
                onCheckedChange={(gamepadNavigationEnabled) => onPreferenceChange({ gamepadNavigationEnabled })}
              />
              <div className="text-xs text-muted-foreground">
                关闭手柄导航只影响伴随窗口内的手柄操作；F8 仍可在伴随窗口聚焦时切回游戏。
              </div>
              <SwitchControl
                label="显示调试信息"
                checked={preferences.showDebugDetails}
                onCheckedChange={(showDebugDetails) => onPreferenceChange({ showDebugDetails })}
              />
              <div className="text-xs text-muted-foreground">
                开启后显示日志页、扫描状态、运行时来源、性能耗时和订单内部来源；普通使用建议保持关闭。
              </div>
            </div>
          </ListPanel>

          <ListPanel title="稀客专注模式">
            <div className="space-y-4">
              <SwitchControl
                label="默认精简模式"
                checked={serviceFocusCompact}
                onCheckedChange={onServiceFocusCompactChange}
              />
              <div className="text-xs text-muted-foreground">
                料理和酒水显示数量在进入专注模式后直接调整，设置会自动记住。
              </div>
            </div>
          </ListPanel>

        </div>
      </TabsContent>

      <TabsContent value="updates" className="space-y-4">
        <ListPanel title="更新">
          <div className="space-y-4">
            <div className="grid gap-2 text-sm">
              <InfoLine label="当前版本" value={updateStatus?.currentVersion || '未知'} />
              <InfoLine label="最新版本" value={updateStatus?.latestVersion || '未检查'} />
              <InfoLine label="状态" value={updateStateLabel} />
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
                loading={updateBusy === 'check'}
                disabled={!apiToken || Boolean(updateBusy)}
                onClick={() => runUpdateAction('check', () => checkForUpdates(endpoint, apiToken))}
              >
                检查
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconDownload size={14} />}
                loading={updateBusy === 'download'}
                disabled={!apiToken || Boolean(updateBusy) || !canDownloadUpdate}
                onClick={() => runUpdateAction('download', () => downloadUpdate(endpoint, apiToken))}
              >
                下载
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconPackageImport size={14} />}
                loading={updateBusy === 'install'}
                disabled={!apiToken || Boolean(updateBusy) || !canInstallUpdate}
                onClick={() => runUpdateAction('install', () => installUpdateOnExit(endpoint, apiToken))}
              >
                打开安装程序
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                leftSection={<IconExternalLink size={14} />}
                disabled={!updateStatus?.releaseUrl}
                onClick={() => void openReleasePage()}
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
                value={preferences.serviceOrderSortMode}
                options={[
                  { value: 'ordered', label: '点单顺序' },
                  { value: 'guest', label: '稀客分组' },
                ]}
                onChange={(serviceOrderSortMode) => onPreferenceChange({ serviceOrderSortMode })}
              />
              <SwitchControl
                label="游戏界面置顶推荐（实验性）"
                checked={preferences.gameUiPinningEnabled}
                onCheckedChange={(gameUiPinningEnabled) => onPreferenceChange({ gameUiPinningEnabled })}
              />
              <div className="text-xs text-muted-foreground">
                打开料理或酒水选择界面时，尝试把当前第一笔订单的推荐材料、料理和酒水排到前面；失败时只记录诊断，不修改库存。
              </div>
              <SwitchControl
                label="目标厨具高亮（实验性）"
                checked={preferences.cookerHighlightEnabled}
                onCheckedChange={(cookerHighlightEnabled) => onPreferenceChange({ cookerHighlightEnabled })}
              />
              <div className="text-xs text-muted-foreground">
                经营中有推荐目标厨具时，尝试让对应已摆放厨具显示黄色脉冲高亮；只改变可见提示，不自动操作厨具。
              </div>
              <SettingSegmentedControl
                label="预算处理"
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
                checked={preferences.filterMissingCookers}
                onCheckedChange={(filterMissingCookers) => onPreferenceChange({ filterMissingCookers })}
              />
              <div className="text-xs text-muted-foreground">
                进入经营场景后，若读取到已摆放厨具，推荐列表会隐藏当前场景无法制作的料理。
              </div>
              <SwitchControl
                label="任务料理置顶"
                checked={preferences.pinMissionRecipeEnabled}
                onCheckedChange={(pinMissionRecipeEnabled) => onPreferenceChange({ pinMissionRecipeEnabled })}
              />
              <SwitchControl
                label="收藏料理置顶"
                checked={preferences.pinFavoriteRecipeEnabled}
                onCheckedChange={(pinFavoriteRecipeEnabled) => onPreferenceChange({ pinFavoriteRecipeEnabled })}
              />
              <SwitchControl
                label="收藏酒水置顶"
                checked={preferences.pinFavoriteBeverageEnabled}
                onCheckedChange={(pinFavoriteBeverageEnabled) => onPreferenceChange({ pinFavoriteBeverageEnabled })}
              />
              <div className="text-xs text-muted-foreground">
                置顶只在解锁、库存、预算和厨具等硬条件通过后生效；任务料理优先于收藏料理，收藏酒水独立影响酒水排序。
              </div>
              <label className="flex items-center justify-between gap-3 text-sm">
                <span className="min-w-0 text-muted-foreground">同基础料理显示</span>
                <NumberInput
                  min={MIN_RECIPE_VARIANT_LIMIT_PER_BASE}
                  max={MAX_RECIPE_VARIANT_LIMIT_PER_BASE}
                  value={preferences.recipeVariantLimitPerBase}
                  onValueChange={(recipeVariantLimitPerBase) => onPreferenceChange({
                    recipeVariantLimitPerBase: normalizeRecipeVariantLimitPerBase(recipeVariantLimitPerBase),
                  })}
                  className="h-8 w-16"
                />
              </label>
              <div className="text-xs text-muted-foreground">
                同一道基础料理只保留当前排序最靠前的指定数量，加料不同但排序靠后的变体会隐藏。
              </div>
              <div className="space-y-2">
                <div className="flex min-w-0 items-center justify-between gap-2">
                  <div className="text-sm font-medium">排除材料</div>
                  <InventorySortControl
                    value={ingredientExclusionSortMode}
                    onChange={setIngredientExclusionSortMode}
                    disabled={ingredientOptions.length === 0}
                    aria-label="排除材料排序"
                  />
                </div>
                <MultiSelectBox
                  value={preferences.recommendationExclusions.excludedIngredientIds.map(String)}
                  options={ingredientOptions}
                  placeholder={ingredientOptions.length > 0 ? '选择不参与推荐的材料' : '暂无运行时材料数据'}
                  disabled={ingredientOptions.length === 0}
                  onValueChange={(values) => updateExclusions({ excludedIngredientIds: parseSelectedIds(values) })}
                />
                <div className="text-xs text-muted-foreground">
                  推荐料理不会使用这些材料，基础配方和加料都会避开。
                </div>
              </div>
              <div className="space-y-2">
                <div className="flex min-w-0 items-center justify-between gap-2">
                  <div className="text-sm font-medium">排除酒水</div>
                  <InventorySortControl
                    value={beverageExclusionSortMode}
                    onChange={setBeverageExclusionSortMode}
                    disabled={beverageOptions.length === 0}
                    aria-label="排除酒水排序"
                  />
                </div>
                <MultiSelectBox
                  value={preferences.recommendationExclusions.excludedBeverageIds.map(String)}
                  options={beverageOptions}
                  placeholder={beverageOptions.length > 0 ? '选择不参与推荐的酒水' : '暂无运行时酒水数据'}
                  disabled={beverageOptions.length === 0}
                  onValueChange={(values) => updateExclusions({ excludedBeverageIds: parseSelectedIds(values) })}
                />
                <div className="text-xs text-muted-foreground">
                  推荐酒水会跳过这些项目。
                </div>
              </div>
              <Button
                type="button"
                size="sm"
                variant="outline"
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

      <TabsContent value="automation" className="space-y-4">
        <ListPanel title="自动化">
          <div className="space-y-4">
            <SwitchControl
              label="启用自动化（实验性）"
              checked={preferences.automationEnabled}
              onCheckedChange={(automationEnabled) => onPreferenceChange({ automationEnabled })}
            />
            <div className="text-xs text-muted-foreground">
              关闭时不会显示或执行任何自动化动作；开启后可在“经营中”页面配置具体子功能。
            </div>
            <div className="grid grid-cols-2 gap-4 max-[719px]:grid-cols-1">
              <AutomationSliderField
                label="稀客并发"
                value={preferences.autoRareConcurrency}
                min={MIN_AUTO_ORDER_CONCURRENCY}
                max={MAX_RARE_AUTO_ORDER_CONCURRENCY}
                onChange={(autoRareConcurrency) => onPreferenceChange({ autoRareConcurrency })}
              />
              <AutomationSliderField
                label="普客并发"
                value={preferences.autoNormalConcurrency}
                min={MIN_AUTO_ORDER_CONCURRENCY}
                max={MAX_NORMAL_AUTO_ORDER_CONCURRENCY}
                onChange={(autoNormalConcurrency) => onPreferenceChange({ autoNormalConcurrency })}
              />
              <AutomationSliderField
                label="最大重试"
                value={preferences.autoMaxStepRetries}
                min={MIN_AUTO_STEP_RETRIES}
                max={MAX_AUTO_STEP_RETRIES_LIMIT}
                onChange={(autoMaxStepRetries) => onPreferenceChange({ autoMaxStepRetries })}
              />
              <AutomationSliderField
                label="最大回退"
                value={preferences.autoMaxRollbacks}
                min={MIN_AUTO_ROLLBACKS}
                max={MAX_AUTO_ROLLBACKS_LIMIT}
                onChange={(autoMaxRollbacks) => onPreferenceChange({ autoMaxRollbacks })}
              />
            </div>
            <div className="text-xs text-muted-foreground">
              参数会在下一轮自动化轮询生效。并发过高可能抢占厨具；等待时间过短可能导致重复开锅。
            </div>
          </div>
        </ListPanel>
      </TabsContent>

      {preferences.showDebugDetails && (
        <TabsContent value="debug" className="space-y-4">
          <ListPanel title="BepInEx">
            <div className="space-y-3">
              <div className="flex flex-wrap items-center gap-2" data-gamepad-axis="x">
                <SwitchControl
                  label="原生日志窗口"
                  checked={logSettings?.nativeBepInExConsoleEnabled ?? false}
                  onCheckedChange={setNativeConsoleEnabled}
                  disabled={!apiToken || consoleBusy}
                />
                <Button size="sm" variant="outline" onClick={refreshConsoleSettings} disabled={!apiToken || consoleBusy}>
                  <IconRefresh className="size-4" />
                  刷新状态
                </Button>
              </div>
              <div className="grid grid-cols-2 gap-2 text-xs">
                <InfoLine label="下次启动" value={logSettings?.nativeBepInExConsoleEnabled ? '开启' : '关闭'} />
                <InfoLine label="当前窗口" value={logSettings?.nativeBepInExConsoleVisible ? '可见' : '未显示'} />
              </div>
              <div className="text-xs text-muted-foreground">
                关闭后会隐藏当前 BepInEx 控制台，并将 BepInEx.cfg 的原生 Console log 设为下次启动关闭；日志页仍可读取 LogOutput.log。
              </div>
              {consoleError && (
                <div className="border border-destructive/30 bg-destructive/10 px-3 py-2 text-xs text-destructive">
                  {consoleError}
                </div>
              )}
            </div>
          </ListPanel>
        </TabsContent>
      )}
    </Tabs>
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
            ? '已开启“排除缺失厨具”硬过滤，此软排序项当前不参与结果。'
            : definition.description;

          return (
            <div key={definition.key} className="steward-data-row p-2">
              <div className="grid min-w-0 gap-2">
                <div className="flex min-w-0 items-center justify-between gap-2">
                  <SwitchField
                    label={definition.label}
                    checked={rule.enabled}
                    disabled={controlDisabled}
                    onCheckedChange={(enabled) => updateObjective(definition.key, { enabled })}
                    title={definition.label}
                    className="min-w-0 flex-1"
                  />
                  <span className={rule.enabled && !controlDisabled ? 'shrink-0 text-right text-sm tabular-nums' : 'shrink-0 text-right text-sm tabular-nums text-muted-foreground'}>
                    {rule.weight}
                  </span>
                </div>
                <Slider
                  value={rule.weight}
                  min={0}
                  max={100}
                  step={5}
                  disabled={!rule.enabled || controlDisabled}
                  aria-label={`${definition.label}权重`}
                  className="min-w-0"
                  onValueChange={(weight) => updateObjective(definition.key, { weight })}
                />
              </div>
              <div className="mt-1 text-xs text-muted-foreground">{description}</div>
            </div>
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

function clampWeight(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, Math.trunc(value)));
}

function formatUpdateState(status: UpdateStatusResponse | null): string {
  if (!status) return '等待本地 API';
  if (!status.enabled) return '已关闭';
  switch (status.installState) {
    case 'waiting':
      return '更新程序已打开';
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
    case 'manifestMissing':
      return '等待首个自动更新版本';
    default:
      return '未检查';
  }
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
