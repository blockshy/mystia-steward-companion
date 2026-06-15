import { Switch as SwitchPrimitive } from '@base-ui/react/switch';

import { cn } from '@/lib/utils';

function Switch({
  className,
  ...props
}: SwitchPrimitive.Root.Props) {
  return (
    <SwitchPrimitive.Root
      data-slot="switch"
      className={cn(
        'relative inline-flex h-5 w-9 shrink-0 items-center rounded-full border border-border bg-muted transition-colors outline-none select-none focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/35 data-checked:border-primary data-checked:bg-primary data-disabled:cursor-not-allowed data-disabled:opacity-50',
        className,
      )}
      {...props}
    >
      <SwitchPrimitive.Thumb
        data-slot="switch-thumb"
        className="block size-4 translate-x-0.5 rounded-full bg-background shadow-sm transition-transform data-checked:translate-x-4"
      />
    </SwitchPrimitive.Root>
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
