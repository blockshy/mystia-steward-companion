import { buildPassiveSpecialBusinessOrderRule } from '@/companion/domain/special-business/rules';
import type { SpecialBusinessRuleModule } from '@/companion/domain/special-business/types';

const PASSIVE_SPECIAL_BUSINESS_CHALLENGE_TYPES = [
  'Story_Basic',
  'Story_Advanced',
  'AnyChallenge',
  'Story_Seiga_TempleCuisineCompetition',
  'Story_Futo_TempleCuisineCompetition',
  'Story_Tochiko_TempleCuisineCompetition',
  'Story_Ichirin_MusicCompetition',
  'Story_Minamitu_MusicCompetition',
  'Story_Toramaru_MusicCompetition',
  'Story_Flandre',
  'RogueLike',
  'Story_Mizuchi',
  'Story_Mizuchi_1',
  'Story_Mizuchi_2',
  'Story_Mizuchi_3',
] as const;

export const passiveSpecialBusinessModule: SpecialBusinessRuleModule = {
  id: 'passive-special-business',
  challengeTypes: PASSIVE_SPECIAL_BUSINESS_CHALLENGE_TYPES,
  buildOrderRule: buildPassiveSpecialBusinessOrderRule,
};
