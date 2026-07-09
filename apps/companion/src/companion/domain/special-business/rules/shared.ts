import type {
  NightBusinessOrder,
  NormalBusinessOrder,
} from '@/companion/types';

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

export function hasMatchingSpecialBusinessTag(tags: readonly string[], targetTags: readonly string[]): boolean {
  if (tags.length === 0 || targetTags.length === 0) return false;
  const normalized = new Set(normalizeSpecialBusinessTags(tags));
  return normalizeSpecialBusinessTags(targetTags).some((tag) => normalized.has(tag));
}

export function getOrderSpecialBusinessRole(order: NightBusinessOrder | NormalBusinessOrder): string {
  return normalizeRole(order.specialBusinessRole);
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
