import { Slider as MantineSlider } from '@mantine/core';
import type { SliderProps as MantineSliderProps } from '@mantine/core';
import type { ReactNode } from 'react';

import { composeClassNames } from '@/components/ui/style';

type SliderProps = Omit<MantineSliderProps, 'value' | 'onChange'> & {
  value: number;
  onValueChange: (value: number) => void;
};

function Slider({
  className,
  value,
  min = 0,
  max = 100,
  step = 1,
  disabled,
  onValueChange,
  ...props
}: SliderProps) {
  const {
    'aria-label': ariaLabel,
    'aria-describedby': ariaDescribedBy,
    'aria-valuetext': ariaValueText,
    attributes,
    thumbLabel,
    thumbProps,
    thumbValueText,
    ...sliderProps
  } = props;

  return (
    <div
      data-slot="slider"
      data-gamepad-control="slider"
      className={composeClassNames('relative', className)}
    >
      <MantineSlider
        color="steward"
        size="sm"
        thumbSize={16}
        label={null}
        value={value}
        min={min}
        max={max}
        step={step}
        disabled={disabled}
        thumbLabel={thumbLabel ?? ariaLabel}
        thumbProps={thumbProps}
        attributes={{
          ...attributes,
          thumb: {
            ...attributes?.thumb,
            ...(ariaDescribedBy ? { 'aria-describedby': ariaDescribedBy } : {}),
          },
        }}
        thumbValueText={thumbValueText ?? ariaValueText}
        onChange={onValueChange}
        className="steward-slider"
        {...sliderProps}
      />
    </div>
  );
}

function SliderField({
  label,
  value,
  min,
  max,
  step = 1,
  valueText,
  labelAccessory,
  'aria-describedby': ariaDescribedBy,
  onChange,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step?: number;
  valueText?: string;
  labelAccessory?: ReactNode;
  'aria-describedby'?: string;
  onChange: (value: number) => void;
}) {
  return (
    <div>
      <div className="mb-1 flex items-center justify-between gap-3 text-sm">
        <span className="flex min-w-0 items-center gap-1.5 font-medium">
          <span className="min-w-0">{label}</span>
          {labelAccessory}
        </span>
        {valueText && <span className="text-muted-foreground">{valueText}</span>}
      </div>
      <Slider
        min={min}
        max={max}
        step={step}
        value={value}
        onValueChange={onChange}
        aria-label={label}
        aria-valuetext={valueText}
        aria-describedby={ariaDescribedBy}
      />
    </div>
  );
}

export { Slider, SliderField };
