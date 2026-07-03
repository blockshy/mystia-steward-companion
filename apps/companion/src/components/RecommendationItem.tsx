import type { ReactNode } from 'react';
import { IconStar, IconStarFilled } from '@tabler/icons-react';

import { TagPillGroup } from '@/components/recommendation/TagPillGroup';
import { Button } from '@/components/ui-kit';
import { composeClassNames } from '@/components/ui/style';

type RecommendationMetaTone = 'cooker' | 'base' | 'extra' | 'neutral';

interface RecommendationFavoriteAction {
  active: boolean;
  disabled?: boolean;
  activeLabel: string;
  inactiveLabel: string;
  focusKey: string;
  onToggle: () => void;
}

interface RecommendationItemProps {
  index: number;
  title: ReactNode;
  titleSuffix?: ReactNode;
  badges?: ReactNode;
  summary?: ReactNode;
  inlineMeta?: ReactNode;
  meta?: ReactNode;
  children?: ReactNode;
  compact?: boolean;
  favorite?: RecommendationFavoriteAction;
  gamepadRowKey?: string;
  className?: string;
}

interface RecommendationTagPillsProps {
  tags: readonly string[];
  matchedTags?: readonly string[];
  className?: string;
}

function RecommendationItem({
  index,
  title,
  titleSuffix,
  badges,
  summary,
  inlineMeta,
  meta,
  children,
  compact = false,
  favorite,
  gamepadRowKey,
  className,
}: RecommendationItemProps) {
  const favoriteLabel = favorite?.active ? favorite.activeLabel : favorite?.inactiveLabel;

  return (
    <div
      className={composeClassNames(
        'steward-data-row',
        compact ? 'px-1.5 py-1.5 text-xs' : 'px-2 py-2 text-sm',
        className,
      )}
      data-gamepad-focusable={favorite ? 'true' : undefined}
      data-gamepad-favorite-scope={favorite ? 'true' : undefined}
      data-gamepad-row={favorite ? 'true' : undefined}
      data-gamepad-row-key={favorite && gamepadRowKey ? gamepadRowKey : undefined}
      tabIndex={favorite ? 0 : undefined}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex min-w-0 flex-1 flex-wrap items-center gap-1.5">
          <span className="text-xs text-muted-foreground">#{index + 1}</span>
          <span className="font-medium">
            {title}
            {titleSuffix}
          </span>
          {badges}
          {summary && <span className="text-xs text-muted-foreground">{summary}</span>}
          {inlineMeta}
        </div>
        {favorite && (
          <Button
            type="button"
            size="icon-xs"
            variant={favorite.active ? 'default' : 'outline'}
            disabled={favorite.disabled}
            aria-label={favoriteLabel}
            data-gamepad-favorite="true"
            data-gamepad-focus-key={favorite.focusKey}
            title={favoriteLabel}
            onClick={favorite.onToggle}
          >
            {favorite.active ? <IconStarFilled className="size-3" /> : <IconStar className="size-3" />}
          </Button>
        )}
      </div>
      {meta && <div className="mt-1 flex flex-wrap gap-1.5">{meta}</div>}
      {children}
    </div>
  );
}

function RecommendationMetaBadge({
  label,
  value,
  tone = 'neutral',
  className,
}: {
  label: string;
  value: string;
  tone?: RecommendationMetaTone;
  className?: string;
}) {
  const toneClass = {
    cooker: 'steward-meta-cooker',
    base: 'steward-meta-base',
    extra: 'steward-meta-extra',
    neutral: 'steward-meta-neutral',
  }[tone];

  return (
    <span className={composeClassNames('inline-flex max-w-full items-center gap-1 border px-1.5 py-0.5 text-xs', toneClass, className)}>
      <span className="shrink-0 font-medium">{label}</span>
      <span className="min-w-0 truncate" title={value}>{value}</span>
    </span>
  );
}

function RecommendationTagPills({
  tags,
  matchedTags = [],
  className,
}: RecommendationTagPillsProps) {
  return <TagPillGroup tags={tags} matchedTags={matchedTags} className={composeClassNames('mt-1', className)} />;
}

export { RecommendationItem, RecommendationMetaBadge, RecommendationTagPills };
export type {
  RecommendationFavoriteAction,
  RecommendationItemProps,
  RecommendationMetaTone,
  RecommendationTagPillsProps,
};
