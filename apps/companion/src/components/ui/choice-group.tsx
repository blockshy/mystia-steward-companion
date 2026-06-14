import { Radio } from '@mantine/core';

import { cn } from '@/lib/utils';

type ChoiceOption<TValue extends string> = {
  value: TValue;
  label: string;
  description: string;
};

function ChoiceGroup<TValue extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: TValue;
  options: ChoiceOption<TValue>[];
  onChange: (value: TValue) => void;
}) {
  return (
    <Radio.Group value={value} onChange={(nextValue) => onChange(nextValue as TValue)}>
      <div className="mb-2 text-sm font-medium">{label}</div>
      <div className={cn('grid gap-2', options.length > 2 ? 'grid-cols-3' : 'grid-cols-2')}>
        {options.map((option) => (
          <Radio.Card
            key={option.value}
            value={option.value}
            withBorder
            className={cn(
              'steward-choice-card rounded-md p-2 text-left text-sm transition-colors outline-none focus-visible:border-ring focus-visible:ring-2 focus-visible:ring-ring/35',
              value === option.value
                ? 'steward-choice-card-active'
                : '',
            )}
          >
            <div className="font-medium">{option.label}</div>
            <div className="mt-1 text-xs text-muted-foreground">{option.description}</div>
          </Radio.Card>
        ))}
      </div>
    </Radio.Group>
  );
}

export { ChoiceGroup };
export type { ChoiceOption };
