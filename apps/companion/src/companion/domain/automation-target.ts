import {
  getCurrentNormalOrderExecutionTarget,
  type NormalAutoOrderState,
} from '@/companion/automation-state';
import { buildNormalAutoOrderKey } from '@/companion/domain/normal-order-key';
import {
  buildSpecialBusinessOrderRule,
  buildSpecialFoodTargetWirePolicy,
  requiresSpecialBusinessNormalExecutionTarget,
} from '@/companion/domain/special-business';
import type {
  NormalBusinessOrder,
  SpecialBusinessContext,
  SpecialFoodTargetWirePolicy,
} from '@/companion/types';
import type { NormalExecutionTargetSelection } from '@/companion/workers/order-recommendations.types';

export interface NormalAutomationTargetSelection extends NormalExecutionTargetSelection {
  specialTargetPolicy: SpecialFoodTargetWirePolicy;
  policyError: string;
}

export interface AutomationExecutionResultReadiness {
  isCurrent: boolean;
  pending: boolean;
  error: string | null;
}

/** New commands require a current computation; an admitted task retains its exact target. */
export function getNormalAutomationTargetSelection(
  order: NormalBusinessOrder,
  state: NormalAutoOrderState | undefined,
  enabled: boolean,
  selections: ReadonlyMap<string, NormalExecutionTargetSelection>,
  specialBusiness: SpecialBusinessContext | null | undefined,
  businessGeneration: number,
  requiresRecipeTarget: boolean,
  readiness: AutomationExecutionResultReadiness,
): NormalAutomationTargetSelection {
  const orderKey = buildNormalAutoOrderKey(order);
  const targetPolicy = buildSpecialFoodTargetWirePolicy(
    specialBusiness,
    order.specialBusinessRole,
    businessGeneration,
  );
  const rule = buildSpecialBusinessOrderRule(specialBusiness, order.specialBusinessRole);
  const requiresSpecialTarget = requiresSpecialBusinessNormalExecutionTarget(
    specialBusiness,
    order.specialBusinessRole,
  );
  const unavailable = (message: string, policyError = ''): NormalAutomationTargetSelection => ({
    orderKey,
    target: null,
    message,
    specialTargetPolicy: targetPolicy,
    policyError,
  });
  const policyError =
    specialBusiness?.active === true && specialBusiness.challengeTypeAvailable !== true
      ? specialBusiness.error?.trim() || '特殊经营类型暂时无法读取，自动化已暂停该订单。'
      : requiresSpecialTarget && businessGeneration <= 0
        ? '特殊经营料理目标缺少本场经营编号，自动化已暂停该订单。'
        : requiresSpecialTarget &&
            rule.foodTarget.enforcement === 'require' &&
            rule.foodTarget.tags.length > 0 &&
            !targetPolicy.specialTargetSignature
          ? '特殊经营料理目标缺少本场经营编号或目标标识，自动化已暂停该订单。'
          : '';
  if (policyError) return unavailable(policyError, policyError);
  if (!requiresSpecialTarget) return unavailable('');

  const currentExecutionTarget = getCurrentNormalOrderExecutionTarget(
    state,
    businessGeneration,
    targetPolicy.specialTargetSignature,
    targetPolicy.specialTargetRevision,
  );
  if (currentExecutionTarget) {
    return {
      orderKey,
      target: currentExecutionTarget,
      message: '',
      specialTargetPolicy: targetPolicy,
      policyError: '',
    };
  }
  if (!requiresRecipeTarget) {
    const message = '特殊经营料理目标未在执行前固定，自动化已暂停该订单。';
    return unavailable(message, message);
  }
  if (!enabled) return unavailable('特殊经营料理执行目标尚未启用，等待下一轮。');
  if (readiness.error) return unavailable(`特殊经营执行目标计算失败：${readiness.error}`);
  if (readiness.pending || !readiness.isCurrent) return unavailable('特殊经营执行目标计算中，等待下一轮。');

  const selected = selections.get(orderKey);
  if (!selected) return unavailable('特殊经营执行目标暂不可用，等待下一轮。');
  if (selected.target && !matchesSpecialTargetPolicy(selected.target, targetPolicy)) {
    return unavailable('特殊经营执行目标与当前经营目标不一致，等待重新计算。');
  }
  return { ...selected, specialTargetPolicy: targetPolicy, policyError: '' };
}

function matchesSpecialTargetPolicy(
  target: SpecialFoodTargetWirePolicy,
  policy: SpecialFoodTargetWirePolicy,
): boolean {
  return (
    target.specialTargetChallenge === policy.specialTargetChallenge &&
    target.specialTargetOwner === policy.specialTargetOwner &&
    target.specialTargetGeneration === policy.specialTargetGeneration &&
    target.specialTargetRevision === policy.specialTargetRevision &&
    target.specialTargetSignature === policy.specialTargetSignature &&
    target.specialTargetMatchMode === policy.specialTargetMatchMode &&
    target.specialTargetFoodTags.length === policy.specialTargetFoodTags.length &&
    target.specialTargetFoodTags.every((tag, index) => tag === policy.specialTargetFoodTags[index])
  );
}
