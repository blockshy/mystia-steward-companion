import type { ReactNode } from 'react';

import { composeClassNames } from '@/components/ui/style';
import { Card, CardContent } from '@/components/ui/card';

type StatusTone = 'good' | 'bad' | 'neutral';

function textTitle(value: ReactNode): string | undefined {
  if (typeof value === 'string' || typeof value === 'number') {
    return String(value);
  }
  return undefined;
}

function StatusCard({
  label,
  value,
  detail,
  tone,
}: {
  label: string;
  value: string;
  detail: string;
  tone: StatusTone;
}) {
  const toneClass = tone === 'good'
    ? 'text-[#4f6d38] dark:text-[#c6d59b]'
    : tone === 'bad'
      ? 'text-destructive'
      : 'text-foreground';

  return (
    <Card className="steward-status-card">
      <CardContent>
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className={composeClassNames('mt-0.5 text-base font-semibold', toneClass)}>{value}</div>
        <div className="mt-0.5 break-words text-xs text-muted-foreground">{detail}</div>
      </CardContent>
    </Card>
  );
}

function Metric({ label, value }: { label: string; value: number | string }) {
  return (
    <div>
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-1 text-base font-semibold">{value}</div>
    </div>
  );
}

function InfoLine({
  label,
  value,
  mono = false,
  className,
}: {
  label: string;
  value: ReactNode;
  mono?: boolean;
  className?: string;
}) {
  return (
    <div className={composeClassNames('min-w-0', className)}>
      <div className="text-xs text-muted-foreground">{label}</div>
      <div
        className={composeClassNames('mt-1 break-words text-sm', mono ? 'font-mono text-xs' : 'font-medium')}
        title={textTitle(value)}
      >
        {value}
      </div>
    </div>
  );
}

function ListPanel({
  title,
  action,
  toolbar,
  children,
  contentClassName = '',
  className,
  gamepadScrollKey,
  gamepadScrollLabel,
}: {
  title: string;
  action?: ReactNode;
  toolbar?: ReactNode;
  children: ReactNode;
  contentClassName?: string;
  className?: string;
  gamepadScrollKey?: string;
  gamepadScrollLabel?: string;
}) {
  const hasContent = children !== undefined && children !== null;
  const scrollRegionProps = gamepadScrollKey
    ? {
      'aria-label': gamepadScrollLabel ?? title,
      'data-gamepad-scroll-key': gamepadScrollKey,
      'data-gamepad-scroll-region': 'true',
      role: 'region' as const,
      tabIndex: -1,
    }
    : {};

  return (
    <Card padding={0} className={composeClassNames('steward-list-panel min-w-0', className)}>
      <CardContent className="flex min-h-0 min-w-0 flex-1 flex-col p-0">
        <div
          className="steward-panel-header flex flex-wrap items-center justify-between gap-3 px-3 py-2"
          data-list-panel-header-only={!toolbar && !hasContent ? 'true' : undefined}
        >
          <h2 className="min-w-0 text-sm font-semibold">{title}</h2>
          {action}
        </div>
        {toolbar && (
          <div
            className="min-w-0 border-b border-border/40 bg-background/30 px-3 py-2"
            data-list-panel-toolbar="true"
          >
            {toolbar}
          </div>
        )}
        {hasContent && (contentClassName
          ? (
              <div
                className={composeClassNames('min-w-0 px-3 py-3', contentClassName)}
                data-list-panel-content="true"
                {...scrollRegionProps}
              >
                {children}
              </div>
            )
          : (
              <div className="min-w-0 px-3 py-3" data-list-panel-content="true" {...scrollRegionProps}>
                {children}
              </div>
            ))}
      </CardContent>
    </Card>
  );
}

function EmptyRow({ text }: { text: string }) {
  return <div className="steward-empty-row text-sm text-muted-foreground">{text}</div>;
}

function EmptyState({ text }: { text: string }) {
  return (
    <div className="steward-empty-state text-center text-sm text-muted-foreground">
      <span aria-hidden="true" className="steward-empty-state-mark" />
      <span>{text}</span>
    </div>
  );
}

export { EmptyRow, EmptyState, InfoLine, ListPanel, Metric, StatusCard };
export type { StatusTone };
