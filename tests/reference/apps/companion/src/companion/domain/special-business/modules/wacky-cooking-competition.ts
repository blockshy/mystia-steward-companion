import { selectWackyNormalExecutionTarget } from '@/companion/domain/special-business/normal-targets';
import {
  buildWackyCookingCompetitionOrderRule,
  buildWackyRejectedRecipeKeyFromEvent,
  isWackyCookingCompetitionOrderRole,
  WACKY_CHALLENGE_TYPE,
} from '@/companion/domain/special-business/rules';
import type { SpecialBusinessRuleModule } from '@/companion/domain/special-business/types';

export const wackyCookingCompetitionModule: SpecialBusinessRuleModule = {
  id: 'wacky-cooking-competition',
  challengeTypes: [WACKY_CHALLENGE_TYPE],
  buildOrderRule: buildWackyCookingCompetitionOrderRule,
  requiresNormalExecutionTarget: (specialBusiness, role) =>
    specialBusiness?.active === true && isWackyCookingCompetitionOrderRole(role),
  selectNormalExecutionTarget: selectWackyNormalExecutionTarget,
  isOrderRole: isWackyCookingCompetitionOrderRole,
  buildRejectedRecipeKeyFromEvent: buildWackyRejectedRecipeKeyFromEvent,
};
