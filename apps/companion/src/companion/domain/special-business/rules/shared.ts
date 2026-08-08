import type {
  SpecialBusinessFoodTargetPolicy,
  SpecialBusinessTagMatch,
} from '@/recommendation-engine';

export const WACKY_CHALLENGE_TYPE = 'Story_WackyCookingCompetition';
export const WACKY_TARGET_TAG_COOKING_MIN_PROGRESS = 0.35;
export const STORY_YUYUKO_CHALLENGE_TYPE = 'Story_Yuyuko';
export const RETAKE_YUYUKO_CHALLENGE_TYPE = 'Challenge_Yuyuko';
export const YUYUKO_CHALLENGE_TYPES = new Set([
  STORY_YUYUKO_CHALLENGE_TYPE,
  RETAKE_YUYUKO_CHALLENGE_TYPE,
]);
export const KOISHI_BOSS_ROLE = 'wacky-koishi-boss';
export const WACKY_GHOST_ROLE = 'wacky-ghost-order';
export const WACKY_TARGET_ROLE = 'wacky-target-order';
export const YUYUKO_BOSS_ROLE = 'yuyuko-boss-order';
export const BLOOD_POND_HELL_CHALLENGE_TYPE = 'Story_BloodPondHell';
export const YUUMA_BOSS_ROLE = 'yuuma-boss-order';
export const YUUMA_UNVERIFIED_ROLE = 'yuuma-order-unverified';
export const YUUMA_CHARACTER_ID = 1003;
export const MIZUCHI_STORY_CHALLENGE_TYPE = 'Story_Mizuchi';
export const MIZUCHI_TRIAL_CHALLENGE_TYPES = [
  'Story_Mizuchi_1',
  'Story_Mizuchi_2',
  'Story_Mizuchi_3',
] as const;
export const MIZUCHI_TRIAL_CHALLENGE_TYPE_SET = new Set<string>(MIZUCHI_TRIAL_CHALLENGE_TYPES);
export const MIZUCHI_CHALLENGE_TYPES = [
  MIZUCHI_STORY_CHALLENGE_TYPE,
  ...MIZUCHI_TRIAL_CHALLENGE_TYPES,
] as const;
export const MIZUCHI_CHALLENGE_TYPE_SET = new Set<string>(MIZUCHI_CHALLENGE_TYPES);
export const MIZUCHI_STORY_PUYOYO_FRUIT_INGREDIENT_ID = 5002;
export const MIZUCHI_TRIAL_PEPPER_WATER_INGREDIENT_ID = 5005;
export const MIZUCHI_STORY_POSSESSED_ROLE = 'mizuchi-story-possessed-order';
export const MIZUCHI_STORY_ORDINARY_ROLE = 'mizuchi-story-ordinary-order';
export const MIZUCHI_STORY_UNVERIFIED_ROLE = 'mizuchi-story-unverified-order';
export const MIZUCHI_TRIAL_POSSESSED_ROLE = 'mizuchi-trial-possessed-order';
export const MIZUCHI_TRIAL_ORDINARY_ROLE = 'mizuchi-trial-ordinary-order';
export const MIZUCHI_TRIAL_UNVERIFIED_ROLE = 'mizuchi-trial-unverified-order';

export function matchesSpecialBusinessTags(
  tags: readonly string[],
  targetTags: readonly string[],
  match: SpecialBusinessTagMatch,
): boolean {
  if (targetTags.length === 0) return false;
  const normalized = new Set(normalizeSpecialBusinessTags(tags));
  const targets = normalizeSpecialBusinessTags(targetTags);
  if (targets.length === 0) return false;
  return match === 'all'
    ? targets.every((tag) => normalized.has(tag))
    : targets.some((tag) => normalized.has(tag));
}

export function matchesSpecialBusinessFoodTarget(
  tags: readonly string[],
  target: SpecialBusinessFoodTargetPolicy,
): boolean {
  if (target.enforcement === 'none') return true;
  return matchesSpecialBusinessTags(tags, target.tags, target.match);
}

export function normalizeSpecialBusinessTags(tags: readonly string[] | null | undefined): string[] {
  const normalized = new Set<string>();
  for (const tag of tags ?? []) {
    const trimmed = tag.trim();
    if (!trimmed) continue;
    normalized.add(normalizeSpecialBusinessTag(trimmed));
  }
  return [...normalized];
}

export function isPhaseThreeContext(phase: string | null | undefined): boolean {
  if (!phase) return false;
  return /phase\s*3|phase3|阶段\s*3|阶段三|third/i.test(phase);
}

export function isPhaseTwoContext(phase: string | null | undefined): boolean {
  if (!phase) return false;
  return /phase\s*2|phase2|阶段\s*2|阶段二|second/i.test(phase);
}

export function normalizeRole(role: string | null | undefined): string {
  return (role ?? '').trim();
}

function normalizeSpecialBusinessTag(tag: string): string {
  switch (tag) {
    case '流行·喜爱':
      return '流行喜爱';
    case '流行·厌恶':
      return '流行厌恶';
    default:
      return tag;
  }
}
