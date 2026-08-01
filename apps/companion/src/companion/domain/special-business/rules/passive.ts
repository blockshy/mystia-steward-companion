import {
  emptySpecialBusinessOrderRule,
  type SpecialBusinessOrderRule,
} from '@/companion/domain/special-business/rules/types';

export function buildPassiveSpecialBusinessOrderRule(): SpecialBusinessOrderRule {
  return emptySpecialBusinessOrderRule();
}
