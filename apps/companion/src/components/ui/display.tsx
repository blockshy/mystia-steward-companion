import type { ReactNode } from 'react';

import { cn } from '@/lib/utils';
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
    ? 'text-emerald-700 dark:text-emerald-300'
    : tone === 'bad'
      ? 'text-destructive'
      : 'text-foreground';

  return (
    <Card className="steward-status-card">
      <CardContent className="p-3.5">
        <div className="text-xs text-muted-foreground">{label}</div>
        <div className={cn('mt-1 text-lg font-semibold', toneClass)}>{value}</div>
        <div className="mt-1 truncate text-xs text-muted-foreground" title={detail}>{detail}</div>
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
    <div className={cn('min-w-0', className)}>
      <div className="text-xs text-muted-foreground">{label}</div>
      <div
        className={cn('mt-1 truncate text-sm', mono ? 'font-mono text-xs' : 'font-medium')}
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
    <Card className={cn('steward-list-panel min-w-0', className)}>
      <CardContent className="min-w-0 p-3.5">
        <div className="mb-3 flex flex-wrap items-center justify-between gap-3">
          <h2 className="min-w-0 text-base font-semibold">{title}</h2>
          {action}
        </div>
        {contentClassName ? <div className={contentClassName}>{children}</div> : children}
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
