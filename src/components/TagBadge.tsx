import { Badge } from '@/components/ui/badge';
import { cn } from '@/lib/utils';

type TagVariant =
  | 'preferred'
  | 'disliked'
  | 'extra'
  | 'cancelled'
  | 'matched'
  | 'default';

const variantStyles: Record<TagVariant, string> = {
  preferred: 'bg-pink-100 text-pink-800 border-pink-200 dark:bg-pink-500/15 dark:text-pink-200 dark:border-pink-400/35',
  disliked: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-500/15 dark:text-red-200 dark:border-red-400/35',
  cancelled: 'bg-[#8B5E3C]/15 text-[#8B5E3C] border-[#8B5E3C]/40 line-through dark:bg-amber-700/20 dark:text-amber-200 dark:border-amber-500/35',

  extra: 'bg-orange-100 text-orange-800 border-orange-200 dark:bg-orange-500/15 dark:text-orange-200 dark:border-orange-400/35',
  matched: 'bg-green-100 text-green-800 border-green-200 dark:bg-emerald-500/15 dark:text-emerald-200 dark:border-emerald-400/35',
  default: 'bg-gray-100 text-gray-700 border-gray-200 dark:bg-stone-800 dark:text-stone-200 dark:border-stone-700',
};

interface TagBadgeProps {
  tag: string;
  variant?: TagVariant;
  className?: string;
}

export function TagBadge({ tag, variant = 'default', className }: TagBadgeProps) {
  return (
    <Badge
      variant="outline"
      className={cn('text-xs font-normal', variantStyles[variant], className)}
    >
      {tag}
    </Badge>
  );
}
