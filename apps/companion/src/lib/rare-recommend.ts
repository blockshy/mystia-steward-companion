/**
 * 稀客推荐算法：组合搜索料理加料方案
 */
import type {
  IRecipe,
  IIngredient,
  ICustomerRare,
  IRareRecipeResult,
  IRareBeverageResult,
  TPlace,
} from '@/lib/types';
import {
  resolveTagConflicts,
  getDynamicTags,
  hasForbiddenTag,
  mergeAllTags,
  scoreFoodForRare,
  getRating,
  canCancelNegativeByConflict,
  countConflictCancellations,
} from '@/lib/tags';
import {
  DEFAULT_RECOMMENDATION_DATA,
  type RecommendationDataSet,
} from '@/lib/recommendation-data';

type RareEasterEffect = 'priority-exgood' | 'ban';

interface RareEasterRule {
  customerIds: number[];
  recipeIds?: number[];
  ingredientIds?: number[];
  effect: RareEasterEffect;
  reason: string;
  scoreFloor?: number;
  pinOnQualified?: boolean;
  recipeHighlight?: boolean;
}

interface ResolvedRareEasterEffect {
  effect: RareEasterEffect | null;
  reason: string | null;
  scoreFloor: number | null;
  pinOnQualified: boolean;
  recipeHighlight: boolean;
  ingredientHighlightIds: number[];
}

const EMPTY_EASTER_EFFECT: ResolvedRareEasterEffect = {
  effect: null,
  reason: null,
  scoreFloor: null,
  pinOnQualified: false,
  recipeHighlight: false,
  ingredientHighlightIds: [],
};

const RARE_EASTER_RULES: RareEasterRule[] = [
  {
    customerIds: [4008], // 蕾米莉亚
    recipeIds: [69], // 猩红恶魔蛋糕
    effect: 'priority-exgood',
    reason: '蕾米莉亚 × 猩红恶魔蛋糕（彩蛋）',
    scoreFloor: 4,
  },
  {
    customerIds: [1003], // 饕餮尤魔
    recipeIds: [35], // 油豆腐
    effect: 'priority-exgood',
    reason: '饕餮尤魔 × 油豆腐（彩蛋）',
    scoreFloor: 3,
  },
  {
    customerIds: [10], // 雾雨魔理沙
    recipeIds: [5002], // 牛肉鸳鸯火锅
    effect: 'priority-exgood',
    reason: '雾雨魔理沙 × 牛肉鸳鸯火锅（彩蛋）',
    scoreFloor: 4,
  },
  {
    customerIds: [1000], // 河城荷取
    ingredientIds: [1000], // 黄瓜
    effect: 'priority-exgood',
    reason: '河城荷取 × 黄瓜（彩蛋）',
    scoreFloor: 3,
    pinOnQualified: false,
    recipeHighlight: false,
  },
  {
    customerIds: [2006], // 古明地恋
    recipeIds: [70], // 无意识妖怪慕斯
    effect: 'ban',
    reason: '古明地恋 × 无意识妖怪慕斯（禁推）',
  },
  {
    customerIds: [5001, 5002], // 绵月丰姬/绵月依姬
    recipeIds: [4001], // 蜜桃红烧肉
    effect: 'ban',
    reason: '绵月丰姬/绵月依姬 × 蜜桃红烧肉（禁推）',
  },
  {
    customerIds: [1001], // 犬走椛
    ingredientIds: [5000], // 可可豆
    effect: 'ban',
    reason: '犬走椛 × 可可豆（禁推）',
  },
];

/** 获取指定地区的稀客 */
export function getRareCustomersByPlace(
  place: TPlace,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): ICustomerRare[] {
  return data.rareCustomers.filter((c) =>
    c.places.includes(place),
  );
}

/** 获取全部稀客 */
export function getAllRareCustomers(
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): ICustomerRare[] {
  return data.rareCustomers;
}

interface IngredientTagReasonResult {
  reasonTagsByIngredient: Record<number, string[]>;
  assignedBaseReuseScore: number;
  assignedQtyScore: number;
  assignedPriceScore: number;
}

function getIngredientOwnedQty(
  ingredientId: number,
  ownedIngredientQty: Record<number, number>,
): number {
  return ownedIngredientQty[ingredientId] ?? 0;
}

function compareIngredientByOwnedThenPrice(
  a: IIngredient,
  b: IIngredient,
  ownedIngredientQty: Record<number, number>,
): number {
  const aQty = getIngredientOwnedQty(a.id, ownedIngredientQty);
  const bQty = getIngredientOwnedQty(b.id, ownedIngredientQty);
  if (aQty !== bQty) return bQty - aQty;
  if (a.price !== b.price) return a.price - b.price;
  return a.id - b.id;
}

function buildExtraIngredientTagReasons(
  selectedIngredients: IIngredient[],
  baseActiveTags: string[],
  finalActiveTags: string[],
  customerPreferredTags: string[],
  requiredFoodTag: string,
  baseIngredientNames: Set<string>,
  ownedIngredientQty: Record<number, number>,
): IngredientTagReasonResult {
  const reasonTagsByIngredient: Record<number, string[]> = {};
  const neededTags: string[] = [];

  if (!baseActiveTags.includes(requiredFoodTag) && finalActiveTags.includes(requiredFoodTag)) {
    neededTags.push(requiredFoodTag);
  }

  for (const tag of customerPreferredTags) {
    if (tag === requiredFoodTag) continue;
    if (!baseActiveTags.includes(tag) && finalActiveTags.includes(tag)) {
      neededTags.push(tag);
    }
  }

  let assignedBaseReuseScore = 0;
  let assignedQtyScore = 0;
  let assignedPriceScore = 0;

  for (const tag of neededTags) {
    const carriers = selectedIngredients
      .filter((ingredient) => ingredient.tags.includes(tag))
      .sort((a, b) => {
        const aBaseReuse = baseIngredientNames.has(a.name) ? 1 : 0;
        const bBaseReuse = baseIngredientNames.has(b.name) ? 1 : 0;
        if (aBaseReuse !== bBaseReuse) return bBaseReuse - aBaseReuse;
        return compareIngredientByOwnedThenPrice(a, b, ownedIngredientQty);
      });

    if (carriers.length === 0) continue;
    const chosen = carriers[0];
    if (!reasonTagsByIngredient[chosen.id]) reasonTagsByIngredient[chosen.id] = [];
    reasonTagsByIngredient[chosen.id].push(tag);
    if (baseIngredientNames.has(chosen.name)) assignedBaseReuseScore++;
    assignedQtyScore += getIngredientOwnedQty(chosen.id, ownedIngredientQty);
    assignedPriceScore += chosen.price;
  }

  return {
    reasonTagsByIngredient,
    assignedBaseReuseScore,
    assignedQtyScore,
    assignedPriceScore,
  };
}

function isReasonDataPreferred(
  nextReason: IngredientTagReasonResult,
  prevReason: IngredientTagReasonResult | null,
  nextCost: number,
  prevCost: number,
): boolean {
  if (!prevReason) return true;
  if (nextReason.assignedBaseReuseScore !== prevReason.assignedBaseReuseScore) {
    return nextReason.assignedBaseReuseScore > prevReason.assignedBaseReuseScore;
  }
  if (nextReason.assignedQtyScore !== prevReason.assignedQtyScore) {
    return nextReason.assignedQtyScore > prevReason.assignedQtyScore;
  }
  if (nextReason.assignedPriceScore !== prevReason.assignedPriceScore) {
    return nextReason.assignedPriceScore < prevReason.assignedPriceScore;
  }
  return nextCost < prevCost;
}

function resolveRareEasterEffect(
  customerId: number,
  recipeId: number,
  ingredientIds: Set<number>,
): ResolvedRareEasterEffect {
  let priorityReason: string | null = null;
  let priorityScoreFloor: number | null = null;
  let priorityPinOnQualified = false;
  let priorityRecipeHighlight = false;
  const ingredientHighlightIds = new Set<number>();

  for (const rule of RARE_EASTER_RULES) {
    if (!rule.customerIds.includes(customerId)) continue;
    if (rule.recipeIds && !rule.recipeIds.includes(recipeId)) continue;
    if (rule.ingredientIds && !rule.ingredientIds.every((id) => ingredientIds.has(id))) continue;

    if (rule.effect === 'ban') {
      return {
        effect: 'ban',
        reason: rule.reason,
        scoreFloor: null,
        pinOnQualified: false,
        recipeHighlight: false,
        ingredientHighlightIds: [],
      };
    }
    if (!priorityReason) {
      priorityReason = rule.reason;
    }
    if (rule.scoreFloor !== undefined) {
      priorityScoreFloor = Math.max(priorityScoreFloor ?? Number.NEGATIVE_INFINITY, rule.scoreFloor);
    }
    priorityPinOnQualified = priorityPinOnQualified || rule.pinOnQualified !== false;
    priorityRecipeHighlight = priorityRecipeHighlight || rule.recipeHighlight !== false;
    if (rule.ingredientIds) {
      for (const id of rule.ingredientIds) ingredientHighlightIds.add(id);
    }
  }

  if (priorityReason) {
    return {
      effect: 'priority-exgood',
      reason: priorityReason,
      scoreFloor: priorityScoreFloor ?? null,
      pinOnQualified: priorityPinOnQualified,
      recipeHighlight: priorityRecipeHighlight,
      ingredientHighlightIds: [...ingredientHighlightIds],
    };
  }

  return EMPTY_EASTER_EFFECT;
}

function getEasterIngredientIdsByEffect(customerId: number, effect: RareEasterEffect): Set<number> {
  const ids = new Set<number>();
  for (const rule of RARE_EASTER_RULES) {
    if (rule.effect !== effect) continue;
    if (!rule.customerIds.includes(customerId)) continue;
    if (!rule.ingredientIds) continue;
    for (const id of rule.ingredientIds) ids.add(id);
  }
  return ids;
}

function resolveFinalFoodTags(
  recipe: IRecipe,
  extraIngredients: IIngredient[],
  popularFoodTag: string | null,
  popularHateFoodTag: string | null,
  isFamousShop: boolean,
): { activeTags: string[]; cancelledTags: string[] } {
  const totalIngCount = recipe.ingredients.length + extraIngredients.length;
  const baseTags = new Set(recipe.positiveTags);
  for (const ingredient of extraIngredients) {
    for (const tag of ingredient.tags) baseTags.add(tag);
  }

  // 先计算静态/明星店动态标签与互斥，再根据互斥后的有效标签计算流行趋势标签。
  const dynamicTags = getDynamicTags(
    recipe.price,
    totalIngCount,
    null,
    null,
    [...baseTags],
    isFamousShop,
  );
  const allTags = mergeAllTags(
    recipe.positiveTags,
    extraIngredients.map((i) => i.tags),
    dynamicTags,
  );
  const { activeTags, cancelledTags } = resolveTagConflicts(allTags);

  const finalTagSet = new Set(activeTags);
  if (popularFoodTag && activeTags.includes(popularFoodTag)) {
    finalTagSet.add('流行喜爱');
  }
  if (popularHateFoodTag && activeTags.includes(popularHateFoodTag)) {
    finalTagSet.add('流行厌恶');
  }

  return {
    activeTags: [...finalTagSet],
    cancelledTags,
  };
}

/** 评估一组额外食材的食物得分与是否满足点单 */
function evaluateCombo(
  recipe: IRecipe,
  extraIngredients: IIngredient[],
  customer: ICustomerRare,
  requiredFoodTag: string,
  popularFoodTag: string | null,
  popularHateFoodTag: string | null,
  isFamousShop: boolean,
): { foodScore: number; meetsRequiredFood: boolean; activeTags: string[]; cancelledTags: string[] } {
  const { activeTags, cancelledTags } = resolveFinalFoodTags(
    recipe,
    extraIngredients,
    popularFoodTag,
    popularHateFoodTag,
    isFamousShop,
  );
  const foodScore = scoreFoodForRare(activeTags, customer.positiveTags, customer.negativeTags);
  const meetsRequiredFood = activeTags.includes(requiredFoodTag);
  return { foodScore, meetsRequiredFood, activeTags, cancelledTags };
}

interface RareRecipeRankOptions {
  allowPreferenceFallback?: boolean;
  minFoodScore?: number;
  forcedRecipeIds?: Set<number>;
}

/** 稀客料理推荐 */
export function rankRecipesForRare(
  customer: ICustomerRare,
  requiredFoodTag: string,
  _requiredBevTag: string,
  availableRecipeIds: Set<number>,
  availableIngredientIds: Set<number>,
  disabledIngredientIds: Set<number>,
  popularFoodTag: string | null,
  popularHateFoodTag: string | null,
  maxExtraIngredients = 4,
  ownedIngredientQty: Record<number, number> = {},
  isFamousShop = false,
  options: RareRecipeRankOptions = {},
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): IRareRecipeResult[] {
  const results: IRareRecipeResult[] = [];

  // 实现细节：默认用户酒水满足1个条件（+1 且满足点单），
  // 因此料理仅追求3分即可，超过3分不再产生额外收益。
  const ASSUMED_BEV_SCORE = 1;
  const ASSUMED_BEV_MEETS = true;
  const TARGET_FOOD_SCORE = 3;
  const allowPreferenceFallback = options.allowPreferenceFallback ?? false;
  const minFoodScore = Math.max(1, options.minFoodScore ?? TARGET_FOOD_SCORE);
  const forcedRecipeIds = options.forcedRecipeIds ?? new Set<number>();
  const ingredientsByName = new Map(data.ingredients.map((i) => [i.name, i]));
  const ingredientsById = new Map(data.ingredients.map((i) => [i.id, i]));

  // 构建可用食材列表
  const usableIngredients: IIngredient[] = [];
  for (const id of availableIngredientIds) {
    if (disabledIngredientIds.has(id)) continue;
    const ing = ingredientsById.get(id);
    if (!ing) continue;
    usableIngredients.push(ing);
  }

  // 预计算客人喜好tag集合（用于筛选候选食材）
  const customerPreferredTagSet = new Set(customer.positiveTags);
  const customerBannedIngredientIds = getEasterIngredientIdsByEffect(customer.id, 'ban');
  const customerPriorityIngredientIds = getEasterIngredientIdsByEffect(customer.id, 'priority-exgood');
  const MAX_CANDIDATES = 18;

  for (const recipe of data.recipes) {
    if (!availableRecipeIds.has(recipe.id)) continue;

    // 基础食材可用性检查：必须在可用食材列表中，且未被禁用
    const hasUnavailableBaseIngredient = recipe.ingredients.some((name) => {
      const ing = ingredientsByName.get(name);
      if (!ing) return true;
      if (!availableIngredientIds.has(ing.id)) return true;
      return disabledIngredientIds.has(ing.id);
    });
    if (hasUnavailableBaseIngredient) continue;

    const baseIngredientIdSet = new Set<number>();
    for (const name of recipe.ingredients) {
      const ing = ingredientsByName.get(name);
      if (ing) baseIngredientIdSet.add(ing.id);
    }

    const baseEasterEffect = resolveRareEasterEffect(customer.id, recipe.id, baseIngredientIdSet);
    if (baseEasterEffect.effect === 'ban') continue;

    const recipeExtraSlots = 5 - recipe.ingredients.length;
    const extraSlots = Math.max(0, Math.min(recipeExtraSlots, maxExtraIngredients));

    const { activeTags: baseActiveTags } = resolveFinalFoodTags(
      recipe,
      [],
      popularFoodTag,
      popularHateFoodTag,
      isFamousShop,
    );

    // 筛选此料理可用的候选食材
    const allCandidates = usableIngredients.filter(
      (ing) => !hasForbiddenTag(ing.tags, recipe.negativeTags) && !customerBannedIngredientIds.has(ing.id),
    );

    // === 性能优化：仅保留相关候选并限制数量 ===
    // 相关定义：
    // 1) 可匹配点单Tag/顾客喜好Tag
    // 2) 可通过互斥规则抵消当前已激活的顾客厌恶Tag
    const relevant: IIngredient[] = [];
    for (const c of allCandidates) {
      const matchesPreferredOrRequired = c.tags.some((t) => customerPreferredTagSet.has(t) || t === requiredFoodTag);
      const canCancelNegative = canCancelNegativeByConflict(
        baseActiveTags,
        c.tags,
        customer.negativeTags,
      );
      const forcePriorityCandidate = customerPriorityIngredientIds.has(c.id);
      if (matchesPreferredOrRequired || canCancelNegative || forcePriorityCandidate) {
        relevant.push(c);
      }
    }

    // 按相关性排序：点单Tag > 喜好Tag匹配 > 厌恶Tag相消。
    // 在相关性相同的情况下，优先选择持有数更高的食材；再同则价格更低。
    const baseIngNames = new Set(recipe.ingredients);
    const candidateComparator = (a: IIngredient, b: IIngredient) => {
      const aRequiredHit = a.tags.includes(requiredFoodTag) ? 1 : 0;
      const bRequiredHit = b.tags.includes(requiredFoodTag) ? 1 : 0;
      if (aRequiredHit !== bRequiredHit) return bRequiredHit - aRequiredHit;

      const aPreferredHits = a.tags.filter((t) => customerPreferredTagSet.has(t)).length;
      const bPreferredHits = b.tags.filter((t) => customerPreferredTagSet.has(t)).length;
      if (aPreferredHits !== bPreferredHits) return bPreferredHits - aPreferredHits;

      const aCancelHits = countConflictCancellations(baseActiveTags, a.tags, customer.negativeTags);
      const bCancelHits = countConflictCancellations(baseActiveTags, b.tags, customer.negativeTags);
      if (aCancelHits !== bCancelHits) return bCancelHits - aCancelHits;

      const aBaseReuse = baseIngNames.has(a.name) ? 1 : 0;
      const bBaseReuse = baseIngNames.has(b.name) ? 1 : 0;
      if (aBaseReuse !== bBaseReuse) return bBaseReuse - aBaseReuse;

      return compareIngredientByOwnedThenPrice(a, b, ownedIngredientQty);
    };

    const candidates = [...relevant]
      .sort(candidateComparator)
      .slice(0, MAX_CANDIDATES);

    // Step b: 组合搜索加料方案（以最少加料为先，再按库存优先）
    let bestCombo: IIngredient[] | null = null;
    let bestEval = evaluateCombo(
      recipe,
      [],
      customer,
      requiredFoodTag,
      popularFoodTag,
      popularHateFoodTag,
      isFamousShop,
    );
    let bestReasonData: IngredientTagReasonResult = {
      reasonTagsByIngredient: {},
      assignedBaseReuseScore: 0,
      assignedQtyScore: 0,
      assignedPriceScore: 0,
    };
    let bestEasterEffect =
      baseEasterEffect.effect === 'priority-exgood' ? baseEasterEffect : EMPTY_EASTER_EFFECT;

    // 先评估不加料的情况
    const baseEval = bestEval;

    if (baseEval.foodScore >= minFoodScore && (baseEval.meetsRequiredFood || allowPreferenceFallback)) {
      bestCombo = [];
    } else if (extraSlots > 0) {
      const n = candidates.length;
      let bestRequiredFallbackCombo: IIngredient[] | null = baseEval.meetsRequiredFood ? [] : null;
      let bestRequiredFallbackEval: ReturnType<typeof evaluateCombo> | null =
        baseEval.meetsRequiredFood ? baseEval : null;
      let bestRequiredFallbackReasonData: IngredientTagReasonResult | null = baseEval.meetsRequiredFood
        ? {
            reasonTagsByIngredient: {},
            assignedBaseReuseScore: 0,
            assignedQtyScore: 0,
            assignedPriceScore: 0,
          }
        : null;
      let bestRequiredFallbackCost = 0;
      let bestRequiredFallbackEasterEffect =
        baseEasterEffect.effect === 'priority-exgood' && baseEval.meetsRequiredFood
          ? baseEasterEffect
          : EMPTY_EASTER_EFFECT;

      const shouldReplaceRequiredFallback = (
        prevEval: ReturnType<typeof evaluateCombo>,
        prevCombo: IIngredient[],
        prevReason: IngredientTagReasonResult,
        prevCost: number,
        nextEval: ReturnType<typeof evaluateCombo>,
        nextCombo: IIngredient[],
        nextReason: IngredientTagReasonResult,
        nextCost: number,
      ) => {
        if (nextEval.foodScore !== prevEval.foodScore) return nextEval.foodScore > prevEval.foodScore;
        if (nextCombo.length !== prevCombo.length) return nextCombo.length < prevCombo.length;
        if (nextReason.assignedBaseReuseScore !== prevReason.assignedBaseReuseScore) {
          return nextReason.assignedBaseReuseScore > prevReason.assignedBaseReuseScore;
        }
        if (nextReason.assignedQtyScore !== prevReason.assignedQtyScore) {
          return nextReason.assignedQtyScore > prevReason.assignedQtyScore;
        }
        if (nextReason.assignedPriceScore !== prevReason.assignedPriceScore) {
          return nextReason.assignedPriceScore < prevReason.assignedPriceScore;
        }
        return nextCost < prevCost;
      };

      const emptyReasonData: IngredientTagReasonResult = {
        reasonTagsByIngredient: {},
        assignedBaseReuseScore: 0,
        assignedQtyScore: 0,
        assignedPriceScore: 0,
      };

      outer: for (let k = 1; k <= Math.min(extraSlots, n); k++) {
        let bestComboForK: IIngredient[] | null = null;
        let bestEvalForK: ReturnType<typeof evaluateCombo> | null = null;
        let bestReasonForK: IngredientTagReasonResult | null = null;
        let bestCostForK = Infinity;
        let bestPriorityComboForK: IIngredient[] | null = null;
        let bestPriorityEvalForK: ReturnType<typeof evaluateCombo> | null = null;
        let bestPriorityReasonForK: IngredientTagReasonResult | null = null;
        let bestPriorityCostForK = Infinity;
        let bestPriorityEasterReasonForK: string | null = null;
        let bestPriorityEasterScoreFloorForK: number | null = null;
        let bestPriorityEasterEffectForK: ResolvedRareEasterEffect | null = null;
        const indices = Array.from({ length: k }, (_, i) => i);
        while (true) {
          const combo = indices.map((i) => candidates[i]);
          const ev = evaluateCombo(
            recipe,
            combo,
            customer,
            requiredFoodTag,
            popularFoodTag,
            popularHateFoodTag,
            isFamousShop,
          );
          const cost = combo.reduce((sum, ingredient) => sum + ingredient.price, 0);
          const reasonData = buildExtraIngredientTagReasons(
            combo,
            baseEval.activeTags,
            ev.activeTags,
            customer.positiveTags,
            requiredFoodTag,
            baseIngNames,
            ownedIngredientQty,
          );

          const comboIngredientIds = new Set(baseIngredientIdSet);
          for (const ingredient of combo) comboIngredientIds.add(ingredient.id);
          const comboEasterEffect = resolveRareEasterEffect(customer.id, recipe.id, comboIngredientIds);

          if (comboEasterEffect.effect !== 'ban') {
            if (
              ev.meetsRequiredFood &&
              (
                bestRequiredFallbackCombo === null ||
                bestRequiredFallbackEval === null ||
                bestRequiredFallbackReasonData === null ||
                shouldReplaceRequiredFallback(
                  bestRequiredFallbackEval,
                  bestRequiredFallbackCombo,
                  bestRequiredFallbackReasonData,
                  bestRequiredFallbackCost,
                  ev,
                  combo,
                  reasonData,
                  cost,
                )
              )
            ) {
              bestRequiredFallbackCombo = combo;
              bestRequiredFallbackEval = ev;
              bestRequiredFallbackReasonData = reasonData;
              bestRequiredFallbackCost = cost;
              bestRequiredFallbackEasterEffect =
                comboEasterEffect.effect === 'priority-exgood'
                  ? comboEasterEffect
                  : baseEasterEffect.effect === 'priority-exgood'
                    ? baseEasterEffect
                    : EMPTY_EASTER_EFFECT;
            }

            if (comboEasterEffect.effect === 'priority-exgood' && (ev.meetsRequiredFood || allowPreferenceFallback)) {
              const shouldReplacePriority =
                bestPriorityComboForK === null ||
                isReasonDataPreferred(reasonData, bestPriorityReasonForK, cost, bestPriorityCostForK);
              if (shouldReplacePriority) {
                bestPriorityComboForK = combo;
                bestPriorityEvalForK = ev;
                bestPriorityReasonForK = reasonData;
                bestPriorityCostForK = cost;
                bestPriorityEasterReasonForK = comboEasterEffect.reason;
                bestPriorityEasterScoreFloorForK = comboEasterEffect.scoreFloor;
                bestPriorityEasterEffectForK = comboEasterEffect;
              }
            }

            if (ev.foodScore >= minFoodScore && (ev.meetsRequiredFood || allowPreferenceFallback)) {
              const shouldReplace =
                bestComboForK === null ||
                isReasonDataPreferred(reasonData, bestReasonForK, cost, bestCostForK);

              if (shouldReplace) {
                bestComboForK = combo;
                bestEvalForK = ev;
                bestReasonForK = reasonData;
                bestCostForK = cost;
              }
            }
          }

          // 下一个组合
          let i = k - 1;
          while (i >= 0 && indices[i] === n - k + i) i--;
          if (i < 0) break;
          indices[i]++;
          for (let j = i + 1; j < k; j++) indices[j] = indices[j - 1] + 1;
        }

        if (
          bestPriorityComboForK !== null &&
          bestPriorityEvalForK &&
          bestPriorityReasonForK &&
          bestPriorityEasterReasonForK
        ) {
          bestCombo = bestPriorityComboForK;
          bestEval = bestPriorityEvalForK;
          bestReasonData = bestPriorityReasonForK;
          bestEasterEffect = {
            effect: 'priority-exgood',
            reason: bestPriorityEasterReasonForK,
            scoreFloor: bestPriorityEasterScoreFloorForK,
            pinOnQualified: bestPriorityEasterEffectForK?.pinOnQualified ?? false,
            recipeHighlight: bestPriorityEasterEffectForK?.recipeHighlight ?? false,
            ingredientHighlightIds: bestPriorityEasterEffectForK?.ingredientHighlightIds ?? [],
          };
          break outer;
        }

        if (bestComboForK !== null && bestEvalForK && bestReasonForK) {
          bestCombo = bestComboForK;
          bestEval = bestEvalForK;
          bestReasonData = bestReasonForK;
          break outer;
        }
      }

      if (
        bestCombo === null &&
        bestRequiredFallbackCombo !== null &&
        bestRequiredFallbackEval !== null
      ) {
        bestCombo = bestRequiredFallbackCombo;
        bestEval = bestRequiredFallbackEval;
        bestReasonData = bestRequiredFallbackReasonData ?? emptyReasonData;
        bestEasterEffect = bestRequiredFallbackEasterEffect;
      }
    }

    // 计算最终结果
    const selectedIngredients = bestCombo ?? [];
    const finalEval = bestCombo !== null ? bestEval : baseEval;

    const extraIngredientReasonTags = selectedIngredients.length > 0
      ? bestReasonData.reasonTagsByIngredient
      : {};

    let finalFoodScore = finalEval.foodScore;
    const finalMeetsRequiredFood = finalEval.meetsRequiredFood;
    let rating = getRating(finalFoodScore, ASSUMED_BEV_SCORE, finalMeetsRequiredFood, ASSUMED_BEV_MEETS);

    if (bestEasterEffect.effect === 'priority-exgood') {
      finalFoodScore = Math.max(finalFoodScore, bestEasterEffect.scoreFloor ?? finalFoodScore);
      rating = getRating(finalFoodScore, ASSUMED_BEV_SCORE, finalMeetsRequiredFood, ASSUMED_BEV_MEETS);
    }

    const forceInclude = forcedRecipeIds.has(recipe.id);
    if (!forceInclude && !finalMeetsRequiredFood && !allowPreferenceFallback) continue;
    if (!forceInclude && !finalMeetsRequiredFood && finalFoodScore <= 0) continue;

    const easterHighlightExtraIngredientIds = selectedIngredients
      .filter((ingredient) => bestEasterEffect.ingredientHighlightIds.includes(ingredient.id))
      .map((ingredient) => ingredient.id);

    const baseCost = recipe.ingredients.reduce((sum, name) => {
      const ing = ingredientsByName.get(name);
      return sum + (ing ? ing.price : 0);
    }, 0);
    const extraCost = selectedIngredients.reduce((sum, i) => sum + i.price, 0);

    results.push({
      recipe,
      extraIngredients: selectedIngredients,
      extraIngredientReasonTags,
      isEasterPriority: bestEasterEffect.effect === 'priority-exgood',
      isEasterPinned: bestEasterEffect.effect === 'priority-exgood' && bestEasterEffect.pinOnQualified,
      isEasterRecipeHighlight:
        bestEasterEffect.effect === 'priority-exgood' && bestEasterEffect.recipeHighlight,
      easterHighlightExtraIngredientIds,
      easterReason: bestEasterEffect.reason,
      easterScoreFloor: bestEasterEffect.scoreFloor,
      allTags: finalEval.activeTags,
      cancelledTags: finalEval.cancelledTags,
      foodScore: finalFoodScore,
      meetsRequiredFood: finalMeetsRequiredFood,
      rating,
      baseCost,
      extraCost,
    });
  }

  // Step c: 排序 —— ExGood优先，然后按价格降序
  results.sort((a, b) => {
    const aPerfect = a.rating === 'ExGood' ? 0 : 1;
    const bPerfect = b.rating === 'ExGood' ? 0 : 1;
    if (aPerfect !== bPerfect) return aPerfect - bPerfect;
    return b.recipe.price - a.recipe.price;
  });

  return results;
}

export function rankPreferenceRecipesForRare(
  customer: ICustomerRare,
  requiredFoodTag: string,
  requiredBevTag: string,
  availableRecipeIds: Set<number>,
  availableIngredientIds: Set<number>,
  disabledIngredientIds: Set<number>,
  popularFoodTag: string | null,
  popularHateFoodTag: string | null,
  maxExtraIngredients = 4,
  ownedIngredientQty: Record<number, number> = {},
  isFamousShop = false,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): IRareRecipeResult[] {
  return rankRecipesForRare(
    customer,
    requiredFoodTag,
    requiredBevTag,
    availableRecipeIds,
    availableIngredientIds,
    disabledIngredientIds,
    popularFoodTag,
    popularHateFoodTag,
    maxExtraIngredients,
    ownedIngredientQty,
    isFamousShop,
    { allowPreferenceFallback: true, minFoodScore: 1 },
    data,
  ).filter((row) => !row.meetsRequiredFood);
}

/** 稀客酒水推荐 */
export function rankBeveragesForRare(
  customer: ICustomerRare,
  requiredBevTag: string,
  availableBeverageIds: Set<number>,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): IRareBeverageResult[] {
  const results: IRareBeverageResult[] = [];

  for (const bev of data.beverages) {
    if (!availableBeverageIds.has(bev.id)) continue;

    const matchedTags = bev.tags.filter((t) =>
      customer.beverageTags.includes(t),
    );
    const bevScore = matchedTags.length;
    const meetsRequiredBev = bev.tags.includes(requiredBevTag);

    if (!meetsRequiredBev) continue;

    results.push({ beverage: bev, bevScore, meetsRequiredBev, matchedTags });
  }

  results.sort((a, b) => {
    if (a.meetsRequiredBev !== b.meetsRequiredBev)
      return a.meetsRequiredBev ? -1 : 1;
    return b.beverage.price - a.beverage.price;
  });

  return results;
}

export function rankPreferenceBeveragesForRare(
  customer: ICustomerRare,
  requiredBevTag: string,
  availableBeverageIds: Set<number>,
  data: RecommendationDataSet = DEFAULT_RECOMMENDATION_DATA,
): IRareBeverageResult[] {
  const results: IRareBeverageResult[] = [];

  for (const bev of data.beverages) {
    if (!availableBeverageIds.has(bev.id)) continue;
    if (bev.tags.includes(requiredBevTag)) continue;

    const matchedTags = bev.tags.filter((tag) => customer.beverageTags.includes(tag));
    if (matchedTags.length === 0) continue;

    results.push({
      beverage: bev,
      bevScore: matchedTags.length,
      meetsRequiredBev: false,
      matchedTags,
    });
  }

  results.sort((a, b) => {
    if (a.bevScore !== b.bevScore) return b.bevScore - a.bevScore;
    if (a.beverage.price !== b.beverage.price) return b.beverage.price - a.beverage.price;
    return a.beverage.id - b.beverage.id;
  });

  return results;
}
