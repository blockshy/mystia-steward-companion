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
      <CardContent className="px-3 py-2.5">
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className={composeClassNames('mt-0.5 text-base font-semibold', toneClass)}>{value}</div>
        <div className="mt-0.5 truncate text-xs text-muted-foreground" title={detail}>{detail}</div>
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
        className={composeClassNames('mt-1 truncate text-sm', mono ? 'font-mono text-xs' : 'font-medium')}
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
  children,
  contentClassName = '',
  className,
}: {
  title: string;
  action?: ReactNode;
  children: ReactNode;
  contentClassName?: string;
  className?: string;
}) {
  return (
    <Card className={composeClassNames('steward-list-panel min-w-0', className)}>
      <CardContent className="flex min-h-0 min-w-0 flex-1 flex-col p-0">
        <div className="steward-panel-header flex flex-wrap items-center justify-between gap-3 px-3 py-2">
          <h2 className="min-w-0 text-sm font-semibold">{title}</h2>
          {action}
        </div>
        {contentClassName
          ? <div className={composeClassNames('min-w-0 px-3 py-3', contentClassName)}>{children}</div>
          : <div className="min-w-0 px-3 py-3">{children}</div>}
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
