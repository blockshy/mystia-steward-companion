import { Slider as MantineSlider } from '@mantine/core';
import type { SliderProps as MantineSliderProps } from '@mantine/core';

import { cn } from '@/lib/utils';

type SliderProps = Omit<MantineSliderProps, 'value' | 'onChange'> & {
  value: number;
  onValueChange: (value: number) => void;
  gamepadStep?: number;
};

function Slider({
  className,
  value,
  min = 0,
  max = 100,
  step = 1,
  disabled,
  onValueChange,
  gamepadStep = step,
  ...props
}: SliderProps) {
  return (
    <div
      data-slot="slider"
      className={cn('relative py-2', className)}
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
        onChange={onValueChange}
        className="steward-slider"
        {...props}
      />
      <input
        type="range"
        min={min}
        max={max}
        step={step}
        value={value}
        disabled={disabled}
        aria-label={props['aria-label']}
        data-gamepad-slider="true"
        data-gamepad-step={gamepadStep}
        onChange={(event) => onValueChange(Number(event.target.value))}
        className="absolute inset-0 h-full w-full cursor-pointer opacity-0 disabled:cursor-not-allowed"
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
        gamepadStep={step}
      />
      {description && <div className="mt-1 text-xs text-muted-foreground">{description}</div>}
    </div>
  );
}

export { Slider, SliderField };
