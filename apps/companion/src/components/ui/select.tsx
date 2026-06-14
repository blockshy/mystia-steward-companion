import { Select as MantineSelect } from '@mantine/core';
import type { SelectProps as MantineSelectProps } from '@mantine/core';

import { cn } from '@/lib/utils';

type SelectOption = {
  value: string;
  label: string;
  disabled?: boolean;
};

type SelectBoxProps = Omit<MantineSelectProps, 'data' | 'value' | 'onChange'> & {
  value: string;
  options: SelectOption[];
  onValueChange: (value: string) => void;
};

function SelectBox({
  className,
  value,
  options,
  onValueChange,
  placeholder,
  searchable = false,
  ...props
}: SelectBoxProps) {
  return (
    <MantineSelect
      data-slot="select"
      value={value}
      data={options}
      placeholder={placeholder}
      searchable={searchable}
      allowDeselect={false}
      checkIconPosition="right"
      maxDropdownHeight={280}
      nothingFoundMessage="无匹配项"
      className={cn('steward-select', className)}
      classNames={{
        input: 'steward-select-input',
        dropdown: 'steward-select-dropdown',
        option: 'steward-select-option',
      }}
      comboboxProps={{
        withinPortal: true,
        middlewares: { flip: true, shift: true },
      }}
      onChange={(nextValue) => {
        if (nextValue !== null) {
          onValueChange(String(nextValue));
        }
      }}
      {...props}
    />
  );
}

export { SelectBox };
export type { SelectBoxProps, SelectOption };
