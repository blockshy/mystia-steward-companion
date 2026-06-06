import { ALL_PLACES, type TPlace } from '@/lib/types';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';

interface RegionSelectorProps {
  value: TPlace | null;
  onChange: (place: TPlace) => void;
}

export function RegionSelector({ value, onChange }: RegionSelectorProps) {
  return (
    <Select value={value ?? ''} onValueChange={(v) => onChange(v as TPlace)}>
      <SelectTrigger className="w-48">
        <SelectValue placeholder="选择地区" />
      </SelectTrigger>
      <SelectContent>
        {ALL_PLACES.map((place) => (
          <SelectItem key={place} value={place}>
            {place}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
