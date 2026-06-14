import { ALL_PLACES, type TPlace } from '@/lib/types';
import { SelectBox } from '@/components/ui/select';

interface RegionSelectorProps {
  value: TPlace | null;
  onChange: (place: TPlace) => void;
}

export function RegionSelector({ value, onChange }: RegionSelectorProps) {
  return (
    <SelectBox
      value={value ?? ''}
      placeholder="选择地区"
      className="w-48"
      options={ALL_PLACES.map((place) => ({ value: place, label: place }))}
      onValueChange={(nextValue) => onChange(nextValue as TPlace)}
    />
  );
}
