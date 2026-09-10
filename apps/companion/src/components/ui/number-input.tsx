import { NumberInput as MantineNumberInput } from '@mantine/core';
import type { MantineSize, NumberInputHandlers, NumberInputProps as MantineNumberInputProps } from '@mantine/core';
import { useRef, useState } from 'react';
import { IconChevronDown, IconChevronUp } from '@tabler/icons-react';

import { composeClassNames } from '@/components/ui/style';

type NumberInputProps = Omit<MantineNumberInputProps, 'value' | 'onChange' | 'onValueChange' | 'size' | 'hideControls' | 'rightSection' | 'handlersRef'> & {
  value: number;
  onValueChange: (value: number) => void;
  size?: MantineSize | (string & {});
  inputClassName?: string;
  'aria-label': string;
};

function NumberInput({
  className,
  inputClassName,
  value,
  onValueChange,
  size = 'sm',
  attributes,
  onBlur,
  'aria-label': ariaLabel,
  'aria-describedby': ariaDescribedBy,
  ...props
}: NumberInputProps) {
  const [emptyAtValue, setEmptyAtValue] = useState<number | null>(null);
  const handlersRef = useRef<NumberInputHandlers>(undefined);
  if (emptyAtValue !== null && emptyAtValue !== value) setEmptyAtValue(null);
  return (
    <MantineNumberInput
      data-slot="number-input"
      data-gamepad-control="number-input"
      aria-label={ariaLabel}
      value={emptyAtValue === value ? '' : value}
      size={size}
      allowDecimal={false}
      clampBehavior="strict"
      className={composeClassNames('steward-number-input-root', className)}
      classNames={{ input: composeClassNames('steward-input steward-number-input', inputClassName) }}
      onChange={(nextValue) => {
        if (nextValue === '') {
          setEmptyAtValue(value);
          return;
        }
        setEmptyAtValue(null);
        const parsed = typeof nextValue === 'number' ? nextValue : Number(nextValue);
        if (Number.isFinite(parsed)) {
          onValueChange(parsed);
        }
      }}
      onBlur={(event) => {
        setEmptyAtValue(null);
        onBlur?.(event);
      }}
      {...props}
      handlersRef={handlersRef}
      hideControls
      rightSection={(
        <span className="steward-number-steppers">
          <button type="button" aria-label={`增加${ariaLabel}`}
            disabled={props.disabled || props.readOnly || (typeof props.max === 'number' && value >= props.max)}
            onClick={() => handlersRef.current?.increment()}><IconChevronUp size={12} aria-hidden="true" /></button>
          <button type="button" aria-label={`减少${ariaLabel}`}
            disabled={props.disabled || props.readOnly || (typeof props.min === 'number' && value <= props.min)}
            onClick={() => handlersRef.current?.decrement()}><IconChevronDown size={12} aria-hidden="true" /></button>
        </span>
      )}
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
