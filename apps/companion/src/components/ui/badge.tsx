import { Badge as MantineBadge } from '@mantine/core';
import type { BadgeProps as MantineBadgeProps } from '@mantine/core';
import type { HTMLAttributes } from 'react';

import { cn } from '@/lib/utils';

type BadgeVariant = 'default' | 'secondary' | 'destructive' | 'outline' | 'ghost' | 'link';

type BadgeProps = Omit<HTMLAttributes<HTMLDivElement>, 'color'> & {
  variant?: BadgeVariant;
};

function Badge({ className, variant = 'default', ...props }: BadgeProps) {
  const mantineVariant: MantineBadgeProps['variant'] =
    variant === 'outline' ? 'outline' : variant === 'default' ? 'filled' : 'light';
  const color = variant === 'destructive' ? 'red' : variant === 'ghost' || variant === 'link' ? 'gray' : 'steward';

  return (
    <MantineBadge
      data-slot="badge"
      data-ui-variant={variant}
      color={color}
      variant={mantineVariant}
      radius="sm"
      size="sm"
      className={cn('steward-badge', className)}
      {...(props as MantineBadgeProps)}
    />
  );
}

export { Badge };
export type { BadgeProps };
