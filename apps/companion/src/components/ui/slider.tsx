import { Slider as MantineSlider } from '@mantine/core';
import type { SliderProps as MantineSliderProps } from '@mantine/core';

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
    'aria-valuetext': ariaValueText,
    thumbLabel,
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
  description,
  onChange,
}: {
  label: string;
  value: number;
  min: number;
  max: number;
  step?: number;
  valueText?: string;
  description?: string;
  onChange: (value: number) => void;
}) {
  return (
    <div>
      <div className="mb-1 flex items-center justify-between gap-3 text-sm">
        <span className="font-medium">{label}</span>
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
      />
      {description && <div className="mt-1 text-xs text-muted-foreground">{description}</div>}
    </div>
  );
}

export { Slider, SliderField };
