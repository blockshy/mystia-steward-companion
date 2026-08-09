import { Badge, Switch } from '@/components/ui-kit';

export function MissionModuleControl({
  description,
  disabled = false,
  enabled,
  focusKey,
  label,
  moduleId,
  onEnabledChange,
}: {
  description: string;
  disabled?: boolean;
  enabled: boolean;
  focusKey: string;
  label: string;
  moduleId: 'task-list' | 'rare-guest-invitations';
  onEnabledChange: (enabled: boolean) => void;
}) {
  const descriptionId = `mission-module-${moduleId}-description`;

  return (
    <div
      className="steward-inline-panel space-y-2 px-3 py-3"
      data-mission-module={moduleId}
      data-module-enabled={enabled ? 'true' : 'false'}
    >
      <div className="flex min-w-0 flex-wrap items-center justify-between gap-3">
        <Switch
          checked={enabled}
          disabled={disabled}
          label={label}
          aria-describedby={descriptionId}
          data-gamepad-focus-key={focusKey}
          onCheckedChange={onEnabledChange}
        />
        <Badge variant={enabled ? 'secondary' : 'outline'}>
          {enabled ? '模块已启用' : '模块已停用'}
        </Badge>
      </div>
      <p id={descriptionId} className="text-xs text-muted-foreground">
        {description}
      </p>
    </div>
  );
}
