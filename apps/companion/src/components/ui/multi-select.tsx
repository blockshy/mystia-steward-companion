import { MultiSelect as MantineMultiSelect } from '@mantine/core';
import type { MultiSelectProps as MantineMultiSelectProps } from '@mantine/core';

import { composeClassNames } from '@/components/ui/style';

type MultiSelectOption = {
  value: string;
  label: string;
  disabled?: boolean;
};

type MultiSelectBoxProps = Omit<MantineMultiSelectProps, 'data' | 'value' | 'onChange'> & {
  value: string[];
  options: MultiSelectOption[];
  onValueChange: (value: string[]) => void;
  inputClassName?: string;
  dropdownClassName?: string;
  optionClassName?: string;
};

function MultiSelectBox({
  className,
  inputClassName,
  dropdownClassName,
  optionClassName,
  value,
  options,
  onValueChange,
  placeholder,
  size = 'sm',
  searchable = true,
  clearable = true,
  ...props
}: MultiSelectBoxProps) {
  return (
    <MantineMultiSelect
      data-slot="multi-select"
      value={value}
      data={options}
      placeholder={placeholder}
      searchable={searchable}
      clearable={clearable}
      checkIconPosition="right"
      maxDropdownHeight={280}
      nothingFoundMessage="无匹配项"
      size={size}
      className={composeClassNames('steward-multi-select', className)}
      classNames={{
        input: composeClassNames('steward-select-input', inputClassName),
        inputField: 'steward-multi-select-field',
        pill: 'steward-multi-select-pill',
        pillsList: 'steward-multi-select-pills',
        dropdown: composeClassNames('steward-select-dropdown', dropdownClassName),
        option: composeClassNames('steward-select-option', optionClassName),
      }}
      openOnFocus={false}
      comboboxProps={{
        withinPortal: true,
        middlewares: { flip: true, shift: true },
      }}
      onChange={(nextValue) => onValueChange(nextValue.map(String))}
      {...props}
    />
  );
}

export { MultiSelectBox };
export type { MultiSelectBoxProps, MultiSelectOption };
