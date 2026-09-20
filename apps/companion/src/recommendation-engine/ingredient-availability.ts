import type { IngredientCatalogItem, RecipeCatalogItem } from '@/lib/catalog-types';
import { isInfiniteInventoryQuantity } from '@/lib/inventory-quantity';
import type { RecommendationRuntimeContext } from '@/recommendation-engine/types';

type IngredientContext = Pick<RecommendationRuntimeContext,
  'availableIngredientIds' | 'disabledIngredientIds' | 'excludedIngredientIds' | 'ownedIngredientQty'>;

/** Availability and quantity must agree; only the exact -1 sentinel means unlimited stock. */
export function isIngredientAvailable(id: number, context: IngredientContext, required = 1): boolean {
  if (!Number.isSafeInteger(id) || id < 0 || !Number.isSafeInteger(required) || required < 1
    || !context.availableIngredientIds.has(id)
    || context.disabledIngredientIds.has(id) || context.excludedIngredientIds.has(id)) return false;
  const quantity = context.ownedIngredientQty[id];
  return isInfiniteInventoryQuantity(quantity)
    || (Number.isSafeInteger(quantity) && quantity >= required);
}

/** Count repeated base slots by exact catalog ID before accepting a recipe or explaining its absence. */
export function resolveRecipeIngredientAvailability(
  recipe: Pick<RecipeCatalogItem, 'ingredients'>,
  ingredientsByName: ReadonlyMap<string, IngredientCatalogItem>,
  context: IngredientContext,
): { available: boolean; missingIngredientNames: string[] } {
  const required = new Map<number, { count: number; names: Set<string> }>();
  const missing = new Set<string>();
  for (const name of recipe.ingredients) {
    const ingredient = ingredientsByName.get(name);
    if (!ingredient) { missing.add(name); continue; }
    const entry = required.get(ingredient.id) ?? { count: 0, names: new Set<string>() };
    entry.count += 1;
    entry.names.add(name);
    required.set(ingredient.id, entry);
  }
  for (const [id, entry] of required) {
    if (!isIngredientAvailable(id, context, entry.count)) {
      for (const name of entry.names) missing.add(name);
    }
  }
  return { available: missing.size === 0, missingIngredientNames: [...missing] };
}
