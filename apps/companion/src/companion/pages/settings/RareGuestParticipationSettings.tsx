import { useMemo } from 'react';
import { ListPanel, MultiSelectBox } from '@/components/ui-kit';
import { SwitchControl } from '@/companion/pages/shared';
import {
  MAX_MANAGED_RARE_GUEST_IDS,
  normalizeManagedRareGuestIds,
  type CompanionPreferences,
} from '@/companion/preferences';
import type { RecommendationDataSet } from '@/lib/recommendation-data';

/**
 * 历史稀客手动参与配置的查看与编辑入口。
 * 此组件只生成共享配置意图，不推断订单资格、不维护队列，也不向游戏发送执行目标。
 * 缺失目录的已保存 ID 仍作为选项显示，避免玩家编辑其他设置时丢失原名单。
 */
export function RareGuestParticipationSettings({ preferences, customers, disabled, onChange }: {
  preferences: CompanionPreferences;
  customers: RecommendationDataSet['rareCustomers'];
  disabled: boolean;
  onChange: (next: Partial<CompanionPreferences>) => void;
}) {
  const options = useMemo(() => {
    const byId = new Map(customers.map((customer) => [customer.id, `${customer.name} · ID ${customer.id}`]));
    for (const id of preferences.managedRareGuestIds) {
      if (!byId.has(id)) byId.set(id, `稀客 ID ${id}（当前目录未提供）`);
    }
    return [...byId.entries()].sort(([left], [right]) => left - right)
      .map(([id, label]) => ({ value: String(id), label }));
  }, [customers, preferences.managedRareGuestIds]);

  return (
    <ListPanel title="稀客手动参与设置">
      <div className="space-y-3" data-rare-participation-settings="true">
        <SwitchControl
          label="保留稀客手动参与模块"
          helpId="automation-rare-legacy-participation"
          description="历史版本中，名单内的新订单默认暂停，由玩家逐单启用；名单外自动参与。关闭模块会保留名单，并恢复全部稀客的默认参与行为。"
          checked={preferences.rareGuestParticipationModuleEnabled}
          disabled={disabled}
          onCheckedChange={(rareGuestParticipationModuleEnabled) => {
            if (!disabled) onChange({ rareGuestParticipationModuleEnabled });
          }}
        />
        <p className="text-xs leading-relaxed text-muted-foreground">
          当前分支尚未提供逐单参与队列。模块开启且名单非空时，Mod 会暂停全部稀客自动化和游戏辅助，
          继续显示推荐；普客处理不受影响。关闭模块或清空名单后可恢复稀客默认参与。
        </p>
        <label className="block space-y-1.5 text-sm" htmlFor="rare-participation-managed-guests">
          <span>已保存的手动参与名单 · {preferences.managedRareGuestIds.length} 名</span>
          <MultiSelectBox
            id="rare-participation-managed-guests"
            aria-label="稀客手动参与名单"
            value={preferences.managedRareGuestIds.map(String)}
            options={options}
            maxValues={MAX_MANAGED_RARE_GUEST_IDS}
            placeholder="搜索姓名或 ID，选择需手动管理的稀客"
            disabled={disabled}
            clearable={false}
            onValueChange={(values) => {
              if (!disabled) onChange({ managedRareGuestIds: normalizeManagedRareGuestIds(values.map(Number)) });
            }}
          />
        </label>
      </div>
    </ListPanel>
  );
}
