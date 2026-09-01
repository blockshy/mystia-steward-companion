import { Badge, Switch } from '@/components/ui-kit';
import type { ExtensionModuleControlModel } from '@/companion/domain/extension-module-control';

export function ModuleControlPanel({
  control,
  description,
  focusKey,
  label,
  moduleId,
  onEnabledChange,
}: {
  control: ExtensionModuleControlModel;
  description: string;
  focusKey: string;
  label: string;
  moduleId: 'task-list' | 'rare-guest-invitations' | 'rare-guest-participation';
  onEnabledChange: (enabled: boolean) => void;
}) {
  const descriptionId = `module-control-${moduleId}-description`;
  const controlReason = control.reason.trim();
  const showControlReason = controlReason !== '' && controlReason !== description.trim();

  return (
    <div
      className="steward-inline-panel space-y-2 px-3 py-3"
      data-feature-module={moduleId}
      data-module-enabled={control.enabled ? 'true' : 'false'}
      data-module-scope={control.scope}
      data-module-status={control.status}
      data-module-writable={control.writable ? 'true' : 'false'}
    >
      <div className="flex min-w-0 flex-wrap items-center justify-between gap-3">
        <Switch
          checked={control.enabled}
          disabled={!control.writable}
          label={label}
          aria-describedby={descriptionId}
          data-gamepad-focus-key={focusKey}
          onCheckedChange={onEnabledChange}
        />
        <div className="flex min-w-0 flex-wrap items-center justify-end gap-1.5">
          <Badge variant="outline">{control.scopeLabel}</Badge>
          {control.status !== 'writable' && (
            <Badge variant="outline">{control.statusLabel}</Badge>
          )}
          <Badge variant={control.enabled ? 'secondary' : 'outline'}>
            {control.enabled ? '模块已启用' : '模块已停用'}
          </Badge>
        </div>
      </div>
      <div id={descriptionId} className="space-y-1 text-xs text-muted-foreground">
        <p>{description}</p>
        {showControlReason && <p data-module-control-reason="true">{controlReason}</p>}
      </div>
    </div>
  );
}
