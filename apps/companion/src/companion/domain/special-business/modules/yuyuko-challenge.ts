import { selectYuyukoNormalExecutionTarget } from '@/companion/domain/special-business/normal-targets';
import {
  buildYuyukoChallengeOrderRule,
  isYuyukoChallengeOrderRole,
  RETAKE_YUYUKO_CHALLENGE_TYPE,
  STORY_YUYUKO_CHALLENGE_TYPE,
} from '@/companion/domain/special-business/rules';
import type { SpecialBusinessRuleModule } from '@/companion/domain/special-business/types';

export const yuyukoChallengeModule: SpecialBusinessRuleModule = {
  id: 'yuyuko-challenge',
  challengeTypes: [STORY_YUYUKO_CHALLENGE_TYPE, RETAKE_YUYUKO_CHALLENGE_TYPE],
  buildOrderRule: buildYuyukoChallengeOrderRule,
  selectNormalExecutionTarget: selectYuyukoNormalExecutionTarget,
  isOrderRole: isYuyukoChallengeOrderRole,
};
