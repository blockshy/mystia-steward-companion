import { IconAlertTriangle, IconClock, IconDownload } from '@tabler/icons-react';
import { getUpdateNoticeContent } from '@/companion/features/updates/update-notice-content';
import type { UpdateManager } from '@/companion/features/updates/useUpdateManager';
import { Button } from '@/components/ui-kit';

export function UpdateNoticeBar({
  manager,
  onViewUpdate,
}: {
  manager: UpdateManager;
  onViewUpdate: () => void;
}) {
  const { status } = manager;
  if (!manager.noticeVisible || !status) return null;

  const content = getUpdateNoticeContent(status);
  const failed = content.kind === 'install-failed';

  return (
    <div
      role="status"
      aria-live="polite"
      className={failed
        ? 'border border-destructive/40 bg-destructive/10 px-3 py-2'
        : 'border border-primary/35 bg-primary/10 px-3 py-2'}
      data-update-notice="visible"
    >
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        {failed
          ? <IconAlertTriangle size={18} className="shrink-0 text-destructive" aria-hidden="true" />
          : <IconDownload size={18} className="shrink-0 text-primary" aria-hidden="true" />}
        <div className="min-w-[12rem] flex-1">
          <div className="text-sm font-semibold text-foreground">{content.title}</div>
          <div className="text-xs text-muted-foreground">{content.detail}</div>
        </div>
        <div className="flex flex-wrap items-center gap-2" data-gamepad-axis="x">
          <Button type="button" size="xs" onClick={onViewUpdate}>
            查看更新
          </Button>
          <Button
            type="button"
            size="xs"
            variant="ghost"
            leftSection={<IconClock size={14} />}
            onClick={manager.snoozeNotice}
          >
            24 小时后提醒
          </Button>
        </div>
      </div>
    </div>
  );
}
