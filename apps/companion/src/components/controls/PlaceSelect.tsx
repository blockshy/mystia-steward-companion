import { SelectBox } from '@/components/ui-kit';

export function PlaceSelect<PlaceName extends string>({
  value,
  places,
  onChange,
  className = 'w-48',
}: {
  value: PlaceName | null;
  places: readonly PlaceName[];
  onChange: (place: PlaceName) => void;
  className?: string;
}) {
  return (
    <SelectBox
      aria-label="地区"
      value={value ?? ''}
      placeholder="选择地区"
      className={className}
      options={places.map((place) => ({ value: place, label: place }))}
      onValueChange={(nextValue) => onChange(nextValue as PlaceName)}
    />
  );
}
