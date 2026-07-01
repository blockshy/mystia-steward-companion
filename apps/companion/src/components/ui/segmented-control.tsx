import { SegmentedControl as MantineSegmentedControl } from '@mantine/core';
import type { SegmentedControlProps as MantineSegmentedControlProps } from '@mantine/core';

import { composeClassNames } from '@/components/ui/style';

type SegmentedControlOption<TValue extends string> = {
  value: TValue;
  label: string;
};

type SegmentedControlProps<TValue extends string> = Omit<
  MantineSegmentedControlProps,
  'data' | 'value' | 'onChange'
> & {
  value: TValue;
  options: SegmentedControlOption<TValue>[];
  onValueChange: (value: TValue) => void;
};

function SegmentedControl<TValue extends string>({
  className,
  value,
  options,
  onValueChange,
  ...props
}: SegmentedControlProps<TValue>) {
  return (
    <MantineSegmentedControl
      data-slot="segmented-control"
      data-gamepad-axis="x"
      value={value}
      data={options}
      color="steward"
      size="sm"
      radius={0}
      className={composeClassNames('steward-segmented-control', className)}
      onChange={(nextValue) => onValueChange(nextValue as TValue)}
      {...props}
    />
  );
}

export { SegmentedControl };
export type { SegmentedControlOption, SegmentedControlProps };
