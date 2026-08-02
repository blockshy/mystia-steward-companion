import { NumberInput as MantineNumberInput } from '@mantine/core';
import type { MantineSize, NumberInputProps as MantineNumberInputProps } from '@mantine/core';

import { composeClassNames } from '@/components/ui/style';

type NumberInputProps = Omit<MantineNumberInputProps, 'value' | 'onChange' | 'onValueChange' | 'size'> & {
  value: number;
  onValueChange: (value: number) => void;
  size?: MantineSize | (string & {});
  inputClassName?: string;
};

function NumberInput({
  className,
  inputClassName,
  value,
  onValueChange,
  size = 'sm',
  attributes,
  'aria-describedby': ariaDescribedBy,
  ...props
}: NumberInputProps) {
  return (
    <MantineNumberInput
      data-slot="number-input"
      data-gamepad-control="number-input"
      value={value}
      size={size}
      allowDecimal={false}
      clampBehavior="strict"
      className={composeClassNames('steward-number-input-root', className)}
      classNames={{ input: composeClassNames('steward-input steward-number-input', inputClassName) }}
      onChange={(nextValue) => {
        const parsed = typeof nextValue === 'number' ? nextValue : Number(nextValue);
        if (Number.isFinite(parsed)) {
          onValueChange(parsed);
        }
      }}
      {...props}
      attributes={{
        ...attributes,
        input: {
          ...attributes?.input,
          ...(ariaDescribedBy ? { 'aria-describedby': ariaDescribedBy } : {}),
        },
      }}
    />
  );
}

export { NumberInput };
export type { NumberInputProps };
