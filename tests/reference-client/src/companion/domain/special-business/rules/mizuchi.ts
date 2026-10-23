import type { SpecialBusinessContext } from '@/companion/types';
import {
  MIZUCHI_CHALLENGE_TYPE_SET,
  MIZUCHI_STORY_CHALLENGE_TYPE,
  MIZUCHI_STORY_ORDINARY_ROLE,
  MIZUCHI_STORY_POSSESSED_ROLE,
  MIZUCHI_STORY_PUYOYO_FRUIT_INGREDIENT_ID,
  MIZUCHI_STORY_UNVERIFIED_ROLE,
  MIZUCHI_TRIAL_ORDINARY_ROLE,
  MIZUCHI_TRIAL_PEPPER_WATER_INGREDIENT_ID,
  MIZUCHI_TRIAL_POSSESSED_ROLE,
  MIZUCHI_TRIAL_UNVERIFIED_ROLE,
  normalizeRole,
} from '@/companion/domain/special-business/rules/shared';
import {
  emptySpecialBusinessOrderRule,
  type SpecialBusinessOrderRule,
} from '@/companion/domain/special-business/rules/types';

type MizuchiOrderKind = 'possessed' | 'ordinary' | 'unverified';

interface MizuchiRoleContract {
  challengeKind: 'story' | 'trial';
  orderKind: MizuchiOrderKind;
  targetIngredientId: number;
  targetIngredientName: string;
}

export function buildMizuchiOrderRule(
  specialBusiness: SpecialBusinessContext | null | undefined,
  role: string | null | undefined,
): SpecialBusinessOrderRule {
  if (!specialBusiness?.active
    || !MIZUCHI_CHALLENGE_TYPE_SET.has(specialBusiness.challengeType)) {
    return emptySpecialBusinessOrderRule();
  }

  const challengeLabel = specialBusiness.displayName.trim() || specialBusiness.challengeType;
  const challengeKind = specialBusiness.challengeType === MIZUCHI_STORY_CHALLENGE_TYPE
    ? 'story'
    : 'trial';
  const contract = resolveMizuchiRoleContract(role);
  if (!contract || contract.challengeKind !== challengeKind) {
    return {
      ...emptySpecialBusinessOrderRule(),
      blockingReason: `${challengeLabel}订单角色不属于当前已验证的瑞灵场景，推荐与自动化已暂停。`,
    };
  }

  if (contract.orderKind === 'unverified') {
    return {
      ...emptySpecialBusinessOrderRule(),
      blockingReason: `${challengeLabel}订单附身身份尚未确认，推荐与自动化已暂停。`,
    };
  }

  if (contract.orderKind === 'ordinary') {
    return {
      ...emptySpecialBusinessOrderRule(),
      forbiddenExtraIngredientIds: [contract.targetIngredientId],
      requiresBaseOrderMatch: true,
      reason: `${challengeLabel}普通订单需要满足原始料理与酒水 Tag，且不得把${contract.targetIngredientName}作为额外材料。`,
    };
  }

  const requiredIds = normalizeRequiredExtraIngredientIds(
    specialBusiness.requiredExtraIngredientIds,
  );
  const requirementReady = requiredIds.length === 1
    && requiredIds[0] === contract.targetIngredientId;
  return {
    ...emptySpecialBusinessOrderRule(),
    requiredExtraIngredientIds: requirementReady ? requiredIds : [],
    forbiddenExtraIngredientIds: [],
    blockingReason: requirementReady
      ? ''
      : `${challengeLabel}附身订单需要精确读取额外材料 ${contract.targetIngredientId}，当前上下文不一致。`,
    requiresBaseOrderMatch: true,
    reason: requirementReady
      ? `${challengeLabel}附身订单需要满足原始料理与酒水 Tag，并把${contract.targetIngredientName}作为额外材料加入料理。`
      : `${challengeLabel}附身订单的额外材料要求尚未确认。`,
  };
}

export function isMizuchiOrderRole(role: string | null | undefined): boolean {
  return resolveMizuchiRoleContract(role) !== null;
}

export function getMizuchiOrderPriority(role: string | null | undefined): number {
  switch (resolveMizuchiRoleContract(role)?.orderKind) {
    case 'possessed':
      return 0;
    case 'ordinary':
      return 1;
    case 'unverified':
      return 2;
    default:
      return 3;
  }
}

function resolveMizuchiRoleContract(
  role: string | null | undefined,
): MizuchiRoleContract | null {
  switch (normalizeRole(role)) {
    case MIZUCHI_STORY_POSSESSED_ROLE:
      return storyContract('possessed');
    case MIZUCHI_STORY_ORDINARY_ROLE:
      return storyContract('ordinary');
    case MIZUCHI_STORY_UNVERIFIED_ROLE:
      return storyContract('unverified');
    case MIZUCHI_TRIAL_POSSESSED_ROLE:
      return trialContract('possessed');
    case MIZUCHI_TRIAL_ORDINARY_ROLE:
      return trialContract('ordinary');
    case MIZUCHI_TRIAL_UNVERIFIED_ROLE:
      return trialContract('unverified');
    default:
      return null;
  }
}

function storyContract(orderKind: MizuchiOrderKind): MizuchiRoleContract {
  return {
    challengeKind: 'story',
    orderKind,
    targetIngredientId: MIZUCHI_STORY_PUYOYO_FRUIT_INGREDIENT_ID,
    targetIngredientName: '噗噗呦果',
  };
}

function trialContract(orderKind: MizuchiOrderKind): MizuchiRoleContract {
  return {
    challengeKind: 'trial',
    orderKind,
    targetIngredientId: MIZUCHI_TRIAL_PEPPER_WATER_INGREDIENT_ID,
    targetIngredientName: '辣椒水',
  };
}

function normalizeRequiredExtraIngredientIds(values: readonly number[]): number[] {
  if (values.some((value) => !Number.isInteger(value) || value < 0)) return [];
  const normalized = [...new Set(values)].sort((left, right) => left - right);
  return normalized.length === values.length ? normalized : [];
}
