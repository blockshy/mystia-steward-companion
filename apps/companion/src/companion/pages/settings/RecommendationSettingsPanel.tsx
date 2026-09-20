import { useCallback, useMemo, useState } from 'react';
import {
  Button,
  ListPanel,
  MultiSelectBox,
  NumberInput,
  SettingHelpField,
  SettingHelpProvider,
  Slider,
  SwitchField,
} from '@/components/ui-kit';
import { buildInventorySelectOptions, type InventorySortMode } from '@/companion/domain/inventory-sorting';
import type { CompanionDeviceAuthorityController } from '@/companion/hooks/useCompanionDeviceAuthority';
import {
  MAX_RECIPE_VARIANT_LIMIT_PER_BASE,
  MIN_RECIPE_VARIANT_LIMIT_PER_BASE,
  normalizeRecipeVariantLimitPerBase,
  type CompanionPreferences,
  type SharedCompanionPreferences,
} from '@/companion/preferences';
import type { RuntimeSets } from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import {
  RECOMMENDATION_OBJECTIVE_DEFINITIONS,
  RECOMMENDATION_SORT_PRESETS,
  buildDefaultRecommendationSortProfile,
  type RecommendationObjectiveKey,
  type RecommendationSortPresetId,
  type RecommendationSortProfile,
} from '@/recommendation-engine';
import { InventorySortControl, SettingSegmentedControl, SwitchControl } from '@/companion/pages/shared';
import { DENSE_TWO_COLUMN_GRID } from '@/companion/pages/shared-constants';
import { SharedSettingsStatus } from '@/companion/pages/settings/SharedSettingsStatus';

export function RecommendationSettingsPanel({
  preferences,
  data,
  runtimeSets,
  deviceAuthority,
  onSharedPreferenceChange,
  onOpenDevices,
}: {
  preferences: CompanionPreferences;
  data: RecommendationDataSet;
  runtimeSets: RuntimeSets | null;
  deviceAuthority: CompanionDeviceAuthorityController;
  onSharedPreferenceChange: (next: Partial<SharedCompanionPreferences>) => void;
  onOpenDevices: () => void;
}) {
  const [ingredientExclusionSortMode, setIngredientExclusionSortMode] = useState<InventorySortMode>('name');
  const [beverageExclusionSortMode, setBeverageExclusionSortMode] = useState<InventorySortMode>('name');
  const ingredientOptions = useMemo(
    () =>
      buildInventorySelectOptions(
        data.ingredients,
        runtimeSets?.ownedIngredientQty ?? null,
        ingredientExclusionSortMode,
      ),
    [data.ingredients, ingredientExclusionSortMode, runtimeSets?.ownedIngredientQty],
  );
  const beverageOptions = useMemo(
    () =>
      buildInventorySelectOptions(
        data.beverages,
        runtimeSets?.ownedBeverageQty ?? null,
        beverageExclusionSortMode,
      ),
    [beverageExclusionSortMode, data.beverages, runtimeSets?.ownedBeverageQty],
  );
  const sharedSettingsDisabled = !deviceAuthority.profileEditWritable;
  const updateExclusions = useCallback(
    (next: Partial<CompanionPreferences['recommendationExclusions']>) => {
      onSharedPreferenceChange({
        recommendationExclusions: {
          ...preferences.recommendationExclusions,
          ...next,
        },
      });
    },
    [onSharedPreferenceChange, preferences.recommendationExclusions],
  );
  return (
    <SettingHelpProvider resetKey="RecommendationSettingsPanel">
      <div className="space-y-4">
        <SharedSettingsStatus
          authority={deviceAuthority}
          focusKey="settings:recommendation:open-connection"

          onOpenDevices={onOpenDevices}
        />
        <fieldset disabled={sharedSettingsDisabled} className="m-0 min-w-0 border-0 p-0 ">
          <div className={DENSE_TWO_COLUMN_GRID}>
            <ListPanel title="推荐设置">
              <div className="space-y-4">
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
                  onChange={(recommendationBudgetPolicy) =>
                    onSharedPreferenceChange({ recommendationBudgetPolicy })
                  }
                />
                <SwitchControl
                  label="排除缺失厨具"
                  helpId="recommendation-filter-missing-cookers"
                  description="进入经营场景并读取到完整厨具状态后，推荐列表会隐藏当前已摆放厨具无法制作的料理。厨具信息不完整时不会使用部分数据猜测。"
                  checked={preferences.filterMissingCookers}
                  onCheckedChange={(filterMissingCookers) =>
                    onSharedPreferenceChange({ filterMissingCookers })
                  }
                />
                <SwitchControl
                  label="任务料理置顶"
                  helpId="recommendation-mission-recipe-priority"
                  description="已追踪任务的目标料理通过库存、预算、厨具和酒水点单条件后置顶；若启用相应自动化，收藏限定也必须满足。任务料理可以跳过本次普通料理点单标签，游戏内列表仍由游戏界面置顶推荐开关单独控制。"
                  checked={preferences.missionRecipePriorityEnabled}
                  onCheckedChange={(missionRecipePriorityEnabled) =>
                    onSharedPreferenceChange({
                      missionRecipePriorityEnabled,
                    })
                  }
                />
                <SwitchControl
                  label="收藏料理置顶"
                  helpId="recommendation-pin-favorite-recipe"
                  description="收藏料理只有在解锁、库存、预算和厨具等硬条件通过后才会排到其他普通料理前面，只影响料理排序。"
                  checked={preferences.pinFavoriteRecipeEnabled}
                  onCheckedChange={(pinFavoriteRecipeEnabled) =>
                    onSharedPreferenceChange({ pinFavoriteRecipeEnabled })
                  }
                />
                <SwitchControl
                  label="收藏酒水置顶"
                  helpId="recommendation-pin-favorite-beverage"
                  description="收藏酒水只有在库存、预算和点单等硬条件通过后才会排到其他普通酒水前面，只影响酒水排序。"
                  checked={preferences.pinFavoriteBeverageEnabled}
                  onCheckedChange={(pinFavoriteBeverageEnabled) =>
                    onSharedPreferenceChange({ pinFavoriteBeverageEnabled })
                  }
                />
                <SettingHelpField
                  id="recommendation-recipe-variant-limit"
                  label="同基础料理显示"
                  description="同一道基础料理只保留当前排序最靠前的指定数量，加料不同但排序靠后的变体会隐藏。主执行方案不会因为展示数量限制而丢失。"
                >
                  {({ helpTrigger, descriptionId }) => (
                    <div className="flex items-center justify-between gap-3 text-sm">
                      <div className="flex min-w-0 items-center gap-1.5 text-muted-foreground">
                        <label htmlFor="settings-recipe-variant-limit" className="min-w-0">
                          同基础料理显示
                        </label>
                        {helpTrigger}
                      </div>
                      <NumberInput
                        aria-label="同基础料理显示"
                        id="settings-recipe-variant-limit"
                        min={MIN_RECIPE_VARIANT_LIMIT_PER_BASE}
                        max={MAX_RECIPE_VARIANT_LIMIT_PER_BASE}
                        value={preferences.recipeVariantLimitPerBase}
                        onValueChange={(recipeVariantLimitPerBase) =>
                          onSharedPreferenceChange({
                            recipeVariantLimitPerBase:
                              normalizeRecipeVariantLimitPerBase(recipeVariantLimitPerBase),
                          })
                        }
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
                        placeholder={
                          ingredientOptions.length > 0 ? '选择不参与推荐的材料' : '暂无游戏材料数据'
                        }
                        disabled={ingredientOptions.length === 0}
                        aria-describedby={descriptionId}
                        onValueChange={(values) =>
                          updateExclusions({
                            excludedIngredientIds: parseSelectedIds(values),
                          })
                        }
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
                        placeholder={beverageOptions.length > 0 ? '选择不参与推荐的酒水' : '暂无游戏酒水数据'}
                        disabled={beverageOptions.length === 0}
                        aria-describedby={descriptionId}
                        onValueChange={(values) =>
                          updateExclusions({
                            excludedBeverageIds: parseSelectedIds(values),
                          })
                        }
                      />
                    </div>
                  )}
                </SettingHelpField>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  data-gamepad-focus-key="settings:recommendation:clear-exclusions"
                  onClick={() =>
                    updateExclusions({
                      excludedIngredientIds: [],
                      excludedBeverageIds: [],
                    })
                  }
                  disabled={
                    preferences.recommendationExclusions.excludedIngredientIds.length === 0 &&
                    preferences.recommendationExclusions.excludedBeverageIds.length === 0
                  }
                >
                  清空排除
                </Button>
              </div>
            </ListPanel>

            <ListPanel title="推荐权重">
              <RecommendationSortProfileControl
                profile={preferences.recommendationSortProfile}
                onChange={(recommendationSortProfile) =>
                  onSharedPreferenceChange({ recommendationSortProfile })
                }
              />
            </ListPanel>
          </div>
        </fieldset>
      </div>
    </SettingHelpProvider>
  );
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

function RecommendationSortProfileControl({
  profile,
  onChange,
}: {
  profile: RecommendationSortProfile;
  onChange: (profile: RecommendationSortProfile) => void;
}) {
  const updateObjective = (
    key: RecommendationObjectiveKey,
    next: Partial<{ enabled: boolean; weight: number }>,
  ) => {
    onChange({
      ...profile,
      objectives: profile.objectives.map((rule) =>
        rule.key === key
          ? {
              ...rule,
              ...next,
              weight: next.weight === undefined ? rule.weight : clampWeight(next.weight),
            }
          : rule,
      ),
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
        onChange={(preset: RecommendationSortPresetId) =>
          onChange(buildDefaultRecommendationSortProfile(preset))
        }
      />
      <div className="grid grid-cols-1 gap-x-5 min-[960px]:grid-cols-2">
        {RECOMMENDATION_OBJECTIVE_DEFINITIONS.map((definition) => {
          const rule = profile.objectives.find((item) => item.key === definition.key);
          if (!rule) return null;

          return (
            <SettingHelpField
              key={definition.key}
              id={`recommendation-weight-${definition.key}`}
              label={definition.label}
              description={definition.description}
            >
              {({ helpTrigger, descriptionId }) => (
                <div
                  className="min-w-0 border-b border-border py-2"
                  data-recommendation-weight-row={definition.key}
                >
                  <div className="grid min-w-0 gap-1">
                    <div className="flex min-w-0 items-center justify-between gap-2">
                      <div className="flex min-w-0 flex-1 items-center gap-1">
                        <SwitchField
                          label={definition.label}
                          checked={rule.enabled}
                          onCheckedChange={(enabled) => updateObjective(definition.key, { enabled })}
                          className="min-w-0 flex-1"
                          aria-describedby={descriptionId}
                        />
                        {helpTrigger}
                      </div>
                      <span
                        className={
                          rule.enabled
                            ? 'shrink-0 text-right text-sm tabular-nums'
                            : 'shrink-0 text-right text-sm tabular-nums text-muted-foreground'
                        }
                      >
                        {rule.weight}
                      </span>
                    </div>
                    <Slider
                      value={rule.weight}
                      min={0}
                      max={100}
                      step={5}
                      disabled={!rule.enabled}
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

function clampWeight(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, Math.trunc(value)));
}
