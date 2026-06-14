import { Switch as MantineSwitch } from '@mantine/core';
import type { SwitchProps as MantineSwitchProps } from '@mantine/core';

import { cn } from '@/lib/utils';

type SwitchProps = Omit<MantineSwitchProps, 'checked' | 'onChange'> & {
  checked?: boolean;
  onCheckedChange?: (checked: boolean) => void;
};

function Switch({ className, checked, onCheckedChange, ...props }: SwitchProps) {
  return (
    <MantineSwitch
      data-slot="switch"
      className={cn('steward-switch', className)}
      checked={checked}
      color="steward"
      size="sm"
      onChange={(event) => onCheckedChange?.(event.currentTarget.checked)}
      {...props}
    />
  );
}

function SwitchField({
  label,
  checked,
  onCheckedChange,
  className,
  disabled,
  title,
}: {
  label: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  className?: string;
  disabled?: boolean;
  title?: string;
}) {
  return (
    <label className={cn('flex items-center gap-2 text-sm', disabled && 'text-muted-foreground', className)} title={title}>
      <Switch checked={checked} onCheckedChange={onCheckedChange} disabled={disabled} />
      <span className="whitespace-nowrap">{label}</span>
    </label>
  );
}

export { Switch, SwitchField };
export type { SwitchProps };
