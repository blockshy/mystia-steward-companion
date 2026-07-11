export const CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE = '__all_food_tags__';

export interface CustomRecipeFormState {
  editingId: string;
  customerId: string;
  foodTagValue: string;
  foodId: string;
  extraIngredientIds: string[];
  enabled: boolean;
  pinToTop: boolean;
  sortOrder?: number;
}

export function createEmptyCustomRecipeForm(): CustomRecipeFormState {
  return {
    editingId: '',
    customerId: '',
    foodTagValue: CUSTOM_RECIPE_ALL_FOOD_TAG_VALUE,
    foodId: '',
    extraIngredientIds: [],
    enabled: true,
    pinToTop: true,
  };
}
