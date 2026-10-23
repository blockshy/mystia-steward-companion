import {
  buildMizuchiOrderRule,
  getMizuchiOrderPriority,
  isMizuchiOrderRole,
  MIZUCHI_CHALLENGE_TYPES,
} from '@/companion/domain/special-business/rules';
import type { SpecialBusinessRuleModule } from '@/companion/domain/special-business/types';

export const mizuchiChallengesModule: SpecialBusinessRuleModule = {
  id: 'mizuchi-challenges',
  challengeTypes: MIZUCHI_CHALLENGE_TYPES,
  buildOrderRule: buildMizuchiOrderRule,
  isOrderRole: isMizuchiOrderRole,
  getOrderPriority: getMizuchiOrderPriority,
};
