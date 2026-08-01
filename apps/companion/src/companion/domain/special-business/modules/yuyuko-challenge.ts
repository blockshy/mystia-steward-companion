import { selectYuyukoNormalExecutionTarget } from '@/companion/domain/special-business/normal-targets';
import {
  buildYuyukoChallengeOrderRule,
  isPhaseThreeContext,
  isYuyukoChallengeOrderRole,
  RETAKE_YUYUKO_CHALLENGE_TYPE,
  STORY_YUYUKO_CHALLENGE_TYPE,
} from '@/companion/domain/special-business/rules';
import type { SpecialBusinessRuleModule } from '@/companion/domain/special-business/types';

export const yuyukoChallengeModule: SpecialBusinessRuleModule = {
  id: 'yuyuko-challenge',
  challengeTypes: [STORY_YUYUKO_CHALLENGE_TYPE, RETAKE_YUYUKO_CHALLENGE_TYPE],
  buildOrderRule: buildYuyukoChallengeOrderRule,
  requiresNormalExecutionTarget: (specialBusiness, role) =>
    specialBusiness?.active === true
    && isPhaseThreeContext(specialBusiness.phase)
    && isYuyukoChallengeOrderRole(role),
  selectNormalExecutionTarget: selectYuyukoNormalExecutionTarget,
  isOrderRole: isYuyukoChallengeOrderRole,
};
