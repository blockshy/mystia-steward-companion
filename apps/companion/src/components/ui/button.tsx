import { Button as MantineButton } from '@mantine/core';
import type { ButtonProps as MantineButtonProps } from '@mantine/core';
import type { ButtonHTMLAttributes, ReactNode } from 'react';

import { cn } from '@/lib/utils';

type ButtonVariant = 'default' | 'outline' | 'secondary' | 'ghost' | 'destructive' | 'link';
type ButtonSize = 'default' | 'xs' | 'sm' | 'lg' | 'icon' | 'icon-xs' | 'icon-sm' | 'icon-lg';

type ButtonProps = Omit<ButtonHTMLAttributes<HTMLButtonElement>, 'color'> & {
  variant?: ButtonVariant;
  size?: ButtonSize;
  leftSection?: ReactNode;
  rightSection?: ReactNode;
  loading?: boolean;
  fullWidth?: boolean;
};

const buttonVariantMap: Record<ButtonVariant, MantineButtonProps['variant']> = {
  default: 'filled',
  outline: 'default',
  secondary: 'default',
  ghost: 'subtle',
  destructive: 'light',
  link: 'transparent',
};

const buttonSizeMap: Record<ButtonSize, MantineButtonProps['size']> = {
  default: 'compact-sm',
  xs: 'compact-xs',
  sm: 'compact-xs',
  lg: 'compact-md',
  icon: 'compact-sm',
  'icon-xs': 'compact-xs',
  'icon-sm': 'compact-xs',
  'icon-lg': 'compact-md',
};

function Button({ className, variant = 'default', size = 'default', ...props }: ButtonProps) {
  return (
    <MantineButton
      data-slot="button"
      data-ui-variant={variant}
      data-ui-size={size}
      color={variant === 'destructive' ? 'red' : 'steward'}
      variant={buttonVariantMap[variant]}
      size={buttonSizeMap[size]}
      className={cn(
        'steward-button',
        size.startsWith('icon') && 'steward-button-icon',
        variant === 'link' && 'steward-button-link',
        className,
      )}
      {...(props as MantineButtonProps)}
    />
  );
}

export { Button };
export type { ButtonProps };
