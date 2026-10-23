import { selectYuumaNormalExecutionTarget } from '@/companion/domain/special-business/normal-targets';
import {
  BLOOD_POND_HELL_CHALLENGE_TYPE,
  buildYuumaChallengeOrderRule,
  isYuumaChallengeOrderRole,
} from '@/companion/domain/special-business/rules';
import type { SpecialBusinessRuleModule } from '@/companion/domain/special-business/types';

export const yuumaChallengeModule: SpecialBusinessRuleModule = {
  id: 'yuuma-challenge',
  challengeTypes: [BLOOD_POND_HELL_CHALLENGE_TYPE],
  buildOrderRule: buildYuumaChallengeOrderRule,
  requiresNormalExecutionTarget: (specialBusiness, role) =>
    specialBusiness?.active === true && isYuumaChallengeOrderRole(role),
  selectNormalExecutionTarget: selectYuumaNormalExecutionTarget,
  isOrderRole: isYuumaChallengeOrderRole,
};
