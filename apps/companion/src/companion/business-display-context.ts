import type { CompanionDeviceAuthorityState, LocalApiSnapshot } from '@/companion/types';

/** 只读展示所属的经营/配置边界；它不是库存版本，也不能授予任何游戏写入权限。 */
export interface BusinessSourceContext {
  snapshot: Record<string, unknown>;
  authority: Pick<CompanionDeviceAuthorityState, 'registryId' | 'authorityRevision' | 'activeProfileRevision' | 'activeProfileHash'>;
}

const SNAPSHOT_FIELDS = ['automationSessionId', 'nightBusinessGeneration', 'nightBusinessLifecyclePhase',
  'activeSceneName', 'runtimeLoaded', 'runtimeDaySceneGeneration', 'runtimeDaySceneReady', 'runtimeDataSignature'] as const;
const SPECIAL_FIELDS = ['active', 'challengeTypeAvailable', 'challengeType', 'phase', 'foodTargetTags',
  'beverageTargetTags', 'requiredExtraIngredientIds', 'yuumaFoodTargetRevision', 'wackyKoishiShieldBroken',
  'wackyKoishiFoodPreferenceTags', 'wackyKoishiFoodHateTags', 'wackyKoishiBeveragePreferenceTags'] as const;

/**
 * 与 C# sourceContext 契约做纯字段投影，不重新计算推荐或执行规则。
 * 捕获时间、倒计时、锅进度与诊断文本不属于展示边界；这些变化仍由服务端精确输入版本管控。
 */
export function buildBusinessSourceContext(snapshot: LocalApiSnapshot | null,
  authority: CompanionDeviceAuthorityState | null): BusinessSourceContext | null {
  if (!snapshot || !authority?.ok) return null;
  const fields: Record<string, unknown> = Object.fromEntries(SNAPSHOT_FIELDS.map(key => [key, snapshot[key] ?? null]));
  const special = snapshot.specialBusiness;
  fields.specialBusiness = special ? {
    ...Object.fromEntries(SPECIAL_FIELDS.map(key => [key, special[key] ?? null])),
    currentValue: special.wackyKoishiShieldBroken === true ? special.currentValue ?? null : null,
    maxValue: special.wackyKoishiShieldBroken === true ? special.maxValue ?? null : null,
    targetValue: special.wackyKoishiShieldBroken === true ? special.targetValue ?? null : null,
    error: special.challengeTypeAvailable !== true ? special.error ?? null : null,
  } : null;
  return { snapshot: fields, authority: { registryId: authority.registryId, authorityRevision: authority.authorityRevision,
    activeProfileRevision: authority.activeProfileRevision, activeProfileHash: authority.activeProfileHash } };
}

/** JSON 对象属性顺序不构成语义差异；缺失上下文必须拒绝保留，不能把两个空值当作同一经营。 */
export function businessSourceContextKey(context: BusinessSourceContext | null | undefined): string {
  if (!context?.snapshot || !context.authority?.registryId || !context.authority.activeProfileHash) return '';
  return JSON.stringify(canonicalValue(context));
}

function canonicalValue(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(canonicalValue);
  if (value && typeof value === 'object') return Object.fromEntries(Object.entries(value)
    .sort(([left], [right]) => left < right ? -1 : left > right ? 1 : 0)
    .map(([key, item]) => [key, canonicalValue(item)]));
  return value ?? null;
}
