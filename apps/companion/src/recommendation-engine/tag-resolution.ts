import type { IngredientCatalogItem, RecipeCatalogItem } from '@/lib/catalog-types';
import type { RuntimeTagPriorityRule } from '@/lib/recommendation-data';
import { buildDynamicFoodTags } from '@/recommendation-engine/dynamic-food-tags';
import type { ResolvedTags } from '@/recommendation-engine/types';

/**
 * 项目内已验证的基础 Tag 压制规则。
 *
 * 当 Mod 暂时无法从游戏运行时读取 Tag 优先级时，推荐引擎使用这组规则保持结果稳定。
 */
export const PROJECT_VERIFIED_TAG_PRIORITY_RULES: RuntimeTagPriorityRule[] = [
  { id: 1, tagIds: [], tags: ['肉', '素'] },
  { id: 2, tagIds: [], tags: ['重油', '清淡'] },
  { id: 3, tagIds: [], tags: ['饱腹', '下酒'] },
  { id: 4, tagIds: [], tags: ['大份', '小巧'] },
  { id: 5, tagIds: [], tags: ['灼热', '凉爽'] },
];

/**
 * 按游戏 Tag 优先级规则过滤互斥 Tag。
 *
 * 同一条规则中靠前的 Tag 会保留，后续命中的 Tag 会进入 suppressedTags，供 UI 解释为什么该 Tag 未生效。
 */
export function resolveTagPriority(
  rawTags: string[],
  runtimeRules: RuntimeTagPriorityRule[],
): ResolvedTags {
  const uniqueRawTags = uniqueStrings(rawTags);
  const active = new Set(uniqueRawTags);
  const suppressed = new Set<string>();

  for (const rule of getEffectiveTagPriorityRules(runtimeRules)) {
    const matchingTags = rule.tags.filter((tag) => active.has(tag));
    if (matchingTags.length <= 1) continue;
    const strongest = matchingTags[0];
    for (const tag of matchingTags) {
      if (tag === strongest) continue;
      active.delete(tag);
      suppressed.add(tag);
    }
  }

  return {
    activeTags: uniqueRawTags.filter((tag) => active.has(tag)),
    suppressedTags: uniqueRawTags.filter((tag) => suppressed.has(tag)),
  };
}

/**
 * 解析料理最终生效的 Tag。
 *
 * 结果包含配方基础正面 Tag、动态 Tag、额外食材 Tag，以及流行喜爱/流行厌恶等运行时派生 Tag。
 */
export function resolveFoodTags({
  recipe,
  extraIngredients,
  popularFoodTag,
  popularHateFoodTag,
  famousShopEnabled,
  tagPriorityRules,
}: {
  recipe: RecipeCatalogItem;
  extraIngredients: IngredientCatalogItem[];
  popularFoodTag: string | null;
  popularHateFoodTag: string | null;
  famousShopEnabled: boolean;
  tagPriorityRules: RuntimeTagPriorityRule[];
}): ResolvedTags {
  const rawTags = [
    ...recipe.positiveTags,
    ...buildDynamicFoodTags({ recipe, extraIngredients }),
    ...extraIngredients.flatMap((ingredient) => ingredient.tags),
  ];
  const resolved = resolveTagPriority(rawTags, tagPriorityRules);
  const active = new Set(resolved.activeTags);
  if (famousShopEnabled && active.has('招牌')) active.add('流行喜爱');
  if (popularFoodTag && active.has(popularFoodTag)) active.add('流行喜爱');
  if (popularHateFoodTag && active.has(popularHateFoodTag)) active.add('流行厌恶');

  return {
    activeTags: [...active],
    suppressedTags: resolved.suppressedTags,
  };
}

/**
 * 查找哪些高优先级 Tag 可以压制目标 Tag。
 *
 * 用于稀客加料搜索：当候选含有稀客厌恶 Tag 时，搜索可通过更高优先级 Tag 将其压制。
 */
export function findTagsThatCanSuppress(
  activeTags: string[],
  tagsToSuppress: string[],
  runtimeRules: RuntimeTagPriorityRule[],
): string[] {
  const active = new Set(activeTags);
  const target = new Set(tagsToSuppress);
  const candidates: string[] = [];

  for (const rule of getEffectiveTagPriorityRules(runtimeRules)) {
    for (let index = 1; index < rule.tags.length; index += 1) {
      const suppressedTag = rule.tags[index];
      if (!active.has(suppressedTag) || !target.has(suppressedTag)) continue;
      candidates.push(...rule.tags.slice(0, index));
    }
  }

  return uniqueStrings(candidates);
}

/**
 * 判断某个额外食材是否会直接带来配方负面 Tag。
 */
export function hasForbiddenIngredientTag(
  ingredient: IngredientCatalogItem,
  recipe: RecipeCatalogItem,
): boolean {
  return ingredient.tags.some((tag) => recipe.negativeTags.includes(tag));
}

function getEffectiveTagPriorityRules(runtimeRules: RuntimeTagPriorityRule[]): RuntimeTagPriorityRule[] {
  return runtimeRules.length > 0 ? runtimeRules : PROJECT_VERIFIED_TAG_PRIORITY_RULES;
}

function uniqueStrings(values: string[]): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const value of values) {
    const text = value.trim();
    if (!text || seen.has(text)) continue;
    seen.add(text);
    result.push(text);
  }
  return result;
}
