/**
 * 标签系统：互斥规则、动态标签、禁忌检查
 */

/** 互斥标签对：强方克制弱方 */
const TAG_CONFLICTS: [string, string][] = [
  ['肉', '素'],
  ['重油', '清淡'],
  ['饱腹', '下酒'],
  ['大份', '小巧'],
  ['灼热', '凉爽'],
];

/**
 * 统计新增标签可抵消的顾客厌恶弱标签数量。
 * 仅当弱标签当前处于激活状态，且顾客确实厌恶该标签时，才计为可抵消。
 */
export function countConflictCancellations(
  baseActiveTags: string[],
  addedTags: string[],
  customerDislikedTags: string[],
): number {
  let count = 0;
  for (const [strong, weak] of TAG_CONFLICTS) {
    if (
      addedTags.includes(strong) &&
      baseActiveTags.includes(weak) &&
      customerDislikedTags.includes(weak)
    ) {
      count += 1;
    }
  }
  return count;
}

/** 新增标签是否能通过互斥规则抵消顾客厌恶弱标签 */
export function canCancelNegativeByConflict(
  baseActiveTags: string[],
  addedTags: string[],
  customerDislikedTags: string[],
): boolean {
  return countConflictCancellations(baseActiveTags, addedTags, customerDislikedTags) > 0;
}

/** 计算互斥后的有效标签和被抵消的标签 */
export function resolveTagConflicts(tags: string[]): {
  activeTags: string[];
  cancelledTags: string[];
} {
  const cancelled = new Set<string>();

  for (const [strong, weak] of TAG_CONFLICTS) {
    if (tags.includes(strong) && tags.includes(weak)) {
      cancelled.add(weak);
    }
  }

  return {
    activeTags: tags.filter((t) => !cancelled.has(t)),
    cancelledTags: [...cancelled],
  };
}

/** 计算动态标签 */
export function getDynamicTags(
  recipePrice: number,
  totalIngredientCount: number,
  popularFoodTag: string | null,
  popularHateFoodTag: string | null,
  recipeTags: string[],
  isFamousShop = false,
): string[] {
  const dynamic: string[] = [];

  if (recipePrice < 20) dynamic.push('实惠');
  if (recipePrice > 60) dynamic.push('昂贵');
  if (totalIngredientCount >= 5) dynamic.push('大份');

  if (isFamousShop && recipeTags.includes('招牌')) {
    dynamic.push('流行喜爱');
  }

  if (popularFoodTag && recipeTags.includes(popularFoodTag)) {
    dynamic.push('流行喜爱');
  }
  if (popularHateFoodTag && recipeTags.includes(popularHateFoodTag)) {
    dynamic.push('流行厌恶');
  }

  return dynamic;
}

/** 检查食材是否触发料理禁忌（会导致黑暗物质） */
export function hasForbiddenTag(
  ingredientTags: string[],
  recipeNegativeTags: string[],
): boolean {
  return ingredientTags.some((t) => recipeNegativeTags.includes(t));
}

/** 合并料理基础标签 + 食材标签 + 动态标签，返回完整标签集 */
export function mergeAllTags(
  recipePositiveTags: string[],
  extraIngredientTags: string[][],
  dynamicTags: string[],
): string[] {
  const allTags = new Set<string>();

  for (const tag of recipePositiveTags) allTags.add(tag);
  for (const tags of extraIngredientTags) {
    for (const tag of tags) allTags.add(tag);
  }
  for (const tag of dynamicTags) allTags.add(tag);

  return [...allTags];
}

/** 计算料理对稀客的得分 */
export function scoreFoodForRare(
  activeTags: string[],
  customerPreferredTags: string[],
  customerDislikedTags: string[],
): number {
  let score = 0;
  for (const tag of activeTags) {
    if (customerPreferredTags.includes(tag)) score += 1;
    if (customerDislikedTags.includes(tag)) score -= 1;
  }
  return score;
}

/** 根据得分和需求满足情况判定评价等级 */
export function getRating(
  foodScore: number,
  bevScore: number,
  meetsRequiredFood: boolean,
  meetsRequiredBev: boolean,
): import('./types').TRating {
  const total = foodScore + bevScore;
  if (total >= 4 && meetsRequiredFood && meetsRequiredBev) return 'ExGood';
  if (total >= 3 && (meetsRequiredFood || meetsRequiredBev)) return 'Good';
  if (total >= 2 && (meetsRequiredFood || meetsRequiredBev)) return 'Normal';
  if (total >= 1) return 'Bad';
  return 'ExBad';
}
