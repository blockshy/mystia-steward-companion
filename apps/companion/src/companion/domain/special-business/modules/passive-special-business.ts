import { buildPassiveSpecialBusinessOrderRule } from '@/companion/domain/special-business/rules';
import type { SpecialBusinessRuleModule } from '@/companion/domain/special-business/types';

export const passiveSpecialBusinessModule: SpecialBusinessRuleModule = {
  id: 'passive-special-business',
  challengeTypes: [],
  buildOrderRule: buildPassiveSpecialBusinessOrderRule,
};
