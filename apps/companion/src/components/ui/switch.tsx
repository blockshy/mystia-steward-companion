import { Switch as MantineSwitch } from '@mantine/core';
import type { SwitchProps as MantineSwitchProps } from '@mantine/core';
import type { LabelHTMLAttributes } from 'react';

import { composeClassNames } from '@/components/ui/style';

type SwitchProps = Omit<MantineSwitchProps, 'checked' | 'onChange'> & {
  checked?: boolean;
  onCheckedChange?: (checked: boolean) => void;
};

function Switch({ className, checked, onCheckedChange, ...props }: SwitchProps) {
  return (
    <MantineSwitch
      data-slot="switch"
      data-checked={checked ? 'true' : 'false'}
      data-disabled={props.disabled ? 'true' : undefined}
      className={composeClassNames('steward-switch', className)}
      checked={checked}
      color="steward"
      size="sm"
      onChange={(event) => onCheckedChange?.(event.currentTarget.checked)}
      {...props}
    />
  );
}

type SwitchFieldProps = Omit<LabelHTMLAttributes<HTMLLabelElement>, 'onChange'> & {
  label: string;
  checked: boolean;
  onCheckedChange: (checked: boolean) => void;
  disabled?: boolean;
};

function SwitchField({
  label,
  checked,
  onCheckedChange,
  className,
  disabled,
  title,
  ...props
}: SwitchFieldProps) {
  return (
    <label
      className={composeClassNames('steward-switch-field flex items-center gap-2.5 text-sm', disabled && 'text-muted-foreground', className)}
      data-disabled={disabled ? 'true' : undefined}
      title={title}
      {...props}
    >
      <Switch checked={checked} onCheckedChange={onCheckedChange} disabled={disabled} />
      <span className="min-w-0 truncate">{label}</span>
    </label>
  );
}

export { Switch, SwitchField };
export type { SwitchFieldProps, SwitchProps };
