import { lazy, Suspense, type ComponentProps } from 'react';
import { ListPanel, Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui-kit';
import type { UpdateManager } from '@/companion/features/updates/useUpdateManager';
import { ModHelpPanel } from '@/companion/pages/ModHelpPanel';
import { ModLogsPanel } from '@/companion/pages/ModLogsPanel';
import { InputSettingsPanel } from '@/companion/pages/settings/InputSettingsPanel';
import { WindowSettingsPanel } from '@/companion/pages/settings/WindowSettingsPanel';
import { INNER_TAB_TRIGGER_CLASS } from '@/companion/pages/shared-constants';
import type { SettingsTab } from '@/companion/types';

const UpdateSettingsPanel = lazy(async () => {
  const module = await import('@/companion/features/updates/UpdateSettingsPanel');
  return { default: module.UpdateSettingsPanel };
});

export function ModSettingsPanel({
  endpoint,
  apiToken,
  settingsTab,
  onSettingsTabChange,
  updateManager,
  ...windowProps
}: ComponentProps<typeof WindowSettingsPanel> & {
  endpoint: string;
  apiToken: string;
  settingsTab: SettingsTab;
  onSettingsTabChange: (tab: SettingsTab) => void;
  updateManager: UpdateManager;
}) {
  const showLogs = windowProps.preferences.showDebugDetails;
  const activeTab = settingsTab === 'logs' && !showLogs ? 'window' : settingsTab;
  const sections: { value: SettingsTab; label: string }[] = [
    { value: 'window', label: '外观窗口' },
    { value: 'input', label: '输入' },
    { value: 'updates', label: '更新' },
    { value: 'help', label: '帮助' },
    ...(showLogs ? [{ value: 'logs' as const, label: '日志' }] : []),
  ];
  return (
    <Tabs
      value={activeTab}
      onValueChange={(value) => onSettingsTabChange(value as SettingsTab)}
      className="space-y-4"
    >
      <TabsList scrollable className="flex h-auto w-full" data-settings-tabs="true">
        {sections.map(({ value, label }) => (
          <TabsTrigger
            key={value}
            value={value}
            className={INNER_TAB_TRIGGER_CLASS}
            data-gamepad-clickable="true"
          >
            {label}
          </TabsTrigger>
        ))}
      </TabsList>
      <TabsContent value="window">
        {activeTab === 'window' && <WindowSettingsPanel {...windowProps} />}
      </TabsContent>
      <TabsContent value="input">
        {activeTab === 'input' && (
          <InputSettingsPanel
            preferences={windowProps.preferences}
            onLocalPreferenceChange={windowProps.onLocalPreferenceChange}
            supportsDesktopWindowControls={windowProps.supportsDesktopWindowControls}
          />
        )}
      </TabsContent>
      <TabsContent value="updates">
        {activeTab === 'updates' && (
          <Suspense fallback={<ListPanel title="更新">正在加载更新页面…</ListPanel>}>
            <UpdateSettingsPanel updateManager={updateManager} />
          </Suspense>
        )}
      </TabsContent>
      <TabsContent value="help">{activeTab === 'help' && <ModHelpPanel />}</TabsContent>
      {showLogs && (
        <TabsContent value="logs">
          {activeTab === 'logs' && <ModLogsPanel endpoint={endpoint} apiToken={apiToken} />}
        </TabsContent>
      )}
    </Tabs>
  );
}
