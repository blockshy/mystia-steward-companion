import { useCallback, useMemo, useState } from 'react';
import { getEffectiveCustomRecipeEntries } from '@/companion/domain/custom-recipes';
import type { CustomRecipeData } from '@/companion/types';

export function useEffectiveCustomRecipesDisclosure(
  customerId: number | null,
  foodTag: string,
  customRecipes: CustomRecipeData,
) {
  const [open, setOpen] = useState(false);
  const available = customerId !== null && foodTag.length > 0;
  const entries = useMemo(
    () => (customerId === null || foodTag.length === 0
      ? []
      : getEffectiveCustomRecipeEntries(customRecipes, customerId, foodTag)),
    [customRecipes, customerId, foodTag],
  );
  const toggle = useCallback(() => setOpen((value) => !value), []);

  return {
    available,
    entries,
    open: available && open,
    toggle,
  };
}
