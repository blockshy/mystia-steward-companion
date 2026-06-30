export type RecommendationSortPresetId = 'balanced' | 'resources' | 'profit' | 'simple';
export type RecommendationObjectiveDirection = 'asc' | 'desc';

/**
 * 稀客推荐方案排序目标。
 *
 * 这些目标只参与同一分桶内的权重排序；置顶类开关由排序上下文单独处理，避免和权重规则混在一起。
 */
export type RecommendationObjectiveKey =
  | 'foodPreference'
  | 'beveragePreference'
  | 'negativeRisk'
  | 'extraCount'
  | 'resourcePressure'
  | 'totalCost'
  | 'profit'
  | 'beverageStock'
  | 'cookerAvailable';

/**
 * 用户可调整的单个排序目标定义。
 */
export interface RecommendationObjectiveDefinition {
  key: RecommendationObjectiveKey;
  label: string;
  description: string;
  direction: RecommendationObjectiveDirection;
}

/**
 * 一个排序目标在当前配置中的启用状态、方向和权重。
 */
export interface RecommendationObjectiveRule {
  key: RecommendationObjectiveKey;
  enabled: boolean;
  weight: number;
  direction: RecommendationObjectiveDirection;
}

/**
 * 推荐权重排序配置。
 */
export interface RecommendationSortProfile {
  preset: RecommendationSortPresetId;
  objectives: RecommendationObjectiveRule[];
}

/**
 * 排序时额外注入的运行时置顶上下文。
 *
 * 收藏、任务料理和酒水收藏置顶属于硬性排序边界，不写入 objective 权重，避免用户误以为调权重能覆盖置顶规则。
 */
export interface RecommendationPlanSortContext {
  favoriteRecipeKeys?: Set<string>;
  favoriteBeverageIds?: Set<number>;
  missionRecipeId?: number | null;
  pinMissionRecipe?: boolean;
  pinFavoriteRecipe?: boolean;
  pinFavoriteBeverage?: boolean;
}

export interface RecommendationSortPreset {
  id: RecommendationSortPresetId;
  label: string;
  profile: RecommendationSortProfile;
}

export const RECOMMENDATION_OBJECTIVE_DEFINITIONS: RecommendationObjectiveDefinition[] = [
  {
    key: 'foodPreference',
    label: '料理偏好命中',
    description: '命中更多稀客喜好料理标签。',
    direction: 'desc',
  },
  {
    key: 'beveragePreference',
    label: '酒水偏好命中',
    description: '命中更多稀客喜好酒水标签。',
    direction: 'desc',
  },
  {
    key: 'negativeRisk',
    label: '减少厌恶标签',
    description: '包含更少稀客厌恶料理标签。',
    direction: 'asc',
  },
  {
    key: 'extraCount',
    label: '减少加料数量',
    description: '更少额外食材，操作更快。',
    direction: 'asc',
  },
  {
    key: 'resourcePressure',
    label: '少用低库存食材',
    description: '降低低库存食材消耗压力。',
    direction: 'asc',
  },
  {
    key: 'totalCost',
    label: '降低食材成本',
    description: '优先更低基础配方和加料成本。',
    direction: 'asc',
  },
  {
    key: 'profit',
    label: '提高预计利润',
    description: '按料理、酒水价格扣除食材成本估算。',
    direction: 'desc',
  },
  {
    key: 'beverageStock',
    label: '优先酒水库存',
    description: '已有库存更多的酒水靠前。',
    direction: 'desc',
  },
  {
    key: 'cookerAvailable',
    label: '当前厨具可做',
    description: '厨具可用的料理方案靠前。',
    direction: 'desc',
  },
];

export const DEFAULT_RECOMMENDATION_SORT_PROFILE = buildRecommendationSortProfile('balanced');
export const RECOMMENDATION_SORT_PRESETS: RecommendationSortPreset[] = [
  {
    id: 'balanced',
    label: '均衡',
    profile: DEFAULT_RECOMMENDATION_SORT_PROFILE,
  },
  {
    id: 'resources',
    label: '省材料',
    profile: buildRecommendationSortProfile('resources'),
  },
  {
    id: 'profit',
    label: '高收益',
    profile: buildRecommendationSortProfile('profit'),
  },
  {
    id: 'simple',
    label: '少操作',
    profile: buildRecommendationSortProfile('simple'),
  },
];

export function buildDefaultRecommendationSortProfile(
  preset: RecommendationSortPresetId = 'balanced',
): RecommendationSortProfile {
  return buildRecommendationSortProfile(preset);
}

/**
 * 将外部存储或旧配置值归一化为当前排序配置。
 *
 * 未识别字段会被丢弃，只接受当前定义过的 objective，保证 localStorage 中的脏数据不会污染排序规则。
 */
export function normalizeRecommendationSortProfile(value: unknown): RecommendationSortProfile {
  const record = isRecord(value) ? value : {};
  const preset = isRecommendationSortPresetId(record.preset) ? record.preset : 'balanced';
  const baseProfile = buildRecommendationSortProfile(preset);
  const overrides = Array.isArray(record.objectives) ? record.objectives : [];
  const overrideByKey = new Map<string, Record<string, unknown>>();

  for (const item of overrides) {
    if (!isRecord(item) || typeof item.key !== 'string') continue;
    overrideByKey.set(item.key, item);
  }

  return {
    preset,
    objectives: baseProfile.objectives.map((rule) => {
      const override = overrideByKey.get(rule.key);
      return {
        key: rule.key,
        enabled: typeof override?.enabled === 'boolean' ? override.enabled : rule.enabled,
        weight: clampObjectiveWeight(typeof override?.weight === 'number' ? override.weight : rule.weight),
        direction: override?.direction === 'asc' || override?.direction === 'desc'
          ? override.direction
          : rule.direction,
      };
    }),
  };
}

/**
 * 序列化前重新归一化配置，避免持久化过期字段。
 */
export function serializeRecommendationSortProfile(profile: RecommendationSortProfile): string {
  const normalized = normalizeRecommendationSortProfile(profile);
  return JSON.stringify({
    preset: normalized.preset,
    objectives: normalized.objectives,
  });
}

function buildRecommendationSortProfile(preset: RecommendationSortPresetId): RecommendationSortProfile {
  return {
    preset,
    objectives: RECOMMENDATION_OBJECTIVE_DEFINITIONS.map((definition) => ({
      key: definition.key,
      enabled: true,
      weight: getPresetWeight(preset, definition.key),
      direction: definition.direction,
    })),
  };
}

function getPresetWeight(
  preset: RecommendationSortPresetId,
  key: RecommendationObjectiveKey,
): number {
  const weights: Record<RecommendationSortPresetId, Record<RecommendationObjectiveKey, number>> = {
    balanced: {
      foodPreference: 70,
      beveragePreference: 60,
      negativeRisk: 90,
      extraCount: 45,
      resourcePressure: 55,
      totalCost: 30,
      profit: 35,
      beverageStock: 35,
      cookerAvailable: 60,
    },
    resources: {
      foodPreference: 55,
      beveragePreference: 45,
      negativeRisk: 90,
      extraCount: 65,
      resourcePressure: 100,
      totalCost: 70,
      profit: 20,
      beverageStock: 80,
      cookerAvailable: 60,
    },
    profit: {
      foodPreference: 60,
      beveragePreference: 50,
      negativeRisk: 85,
      extraCount: 25,
      resourcePressure: 35,
      totalCost: 25,
      profit: 100,
      beverageStock: 25,
      cookerAvailable: 50,
    },
    simple: {
      foodPreference: 50,
      beveragePreference: 45,
      negativeRisk: 90,
      extraCount: 100,
      resourcePressure: 45,
      totalCost: 45,
      profit: 25,
      beverageStock: 40,
      cookerAvailable: 70,
    },
  };

  return weights[preset][key];
}

function clampObjectiveWeight(value: number): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(0, Math.min(100, Math.trunc(value)));
}

function isRecommendationSortPresetId(value: unknown): value is RecommendationSortPresetId {
  return value === 'balanced' || value === 'resources' || value === 'profit' || value === 'simple';
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object';
}
