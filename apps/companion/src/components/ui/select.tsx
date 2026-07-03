import { Select as MantineSelect } from '@mantine/core';
import type { SelectProps as MantineSelectProps } from '@mantine/core';

import { composeClassNames } from '@/components/ui/style';

type SelectOption = {
  value: string;
  label: string;
  disabled?: boolean;
};

type SelectBoxProps = Omit<MantineSelectProps, 'data' | 'value' | 'onChange'> & {
  value: string;
  options: SelectOption[];
  onValueChange: (value: string) => void;
  inputClassName?: string;
  dropdownClassName?: string;
  optionClassName?: string;
};

function SelectBox({
  className,
  inputClassName,
  dropdownClassName,
  optionClassName,
  value,
  options,
  onValueChange,
  placeholder,
  size = 'sm',
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
      size={size}
      className={composeClassNames('steward-select', className)}
      classNames={{
        input: composeClassNames('steward-select-input', inputClassName),
        dropdown: composeClassNames('steward-select-dropdown', dropdownClassName),
        option: composeClassNames('steward-select-option', optionClassName),
      }}
      openOnFocus={false}
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
