import type { CompanionPreferences } from '@/companion/preferences';
import type {
  AutomationRuntimeEvent,
  NormalBusinessOrder,
  NormalOrderExecutionTarget,
  RecommendationStateSnapshot,
  SpecialBusinessContext,
} from '@/companion/types';
import type { RecommendationDataSet } from '@/lib/recommendation-data';
import type { SpecialBusinessOrderRule } from '@/companion/domain/special-business/rules';

export interface SpecialBusinessNormalTargetSelection {
  target: NormalOrderExecutionTarget | null;
  message: string;
}

export interface SpecialBusinessNormalTargetArgs {
  order: NormalBusinessOrder;
  specialBusiness: SpecialBusinessContext | null | undefined;
  runtime: RecommendationStateSnapshot | null | undefined;
  preferences: CompanionPreferences;
  dataSignature: string;
  data?: RecommendationDataSet;
  rejectedRecipeKeys?: readonly string[];
}

export interface SpecialBusinessRuleModule {
  id: string;
  challengeTypes: readonly string[];
  buildOrderRule: (
    specialBusiness: SpecialBusinessContext | null | undefined,
    role: string | null | undefined,
  ) => SpecialBusinessOrderRule;
  requiresNormalExecutionTarget?: (
    specialBusiness: SpecialBusinessContext | null | undefined,
    role: string | null | undefined,
  ) => boolean;
  selectNormalExecutionTarget?: (args: SpecialBusinessNormalTargetArgs) => SpecialBusinessNormalTargetSelection;
  isOrderRole?: (role: string | null | undefined) => boolean;
  buildRejectedRecipeKeyFromEvent?: (event: AutomationRuntimeEvent) => string;
}
