import type { NormalOrderExecutionTarget } from '@/companion/types';

export { selectWackyNormalExecutionTarget } from '@/companion/domain/special-business/normal-targets/wacky';
export { selectYuyukoNormalExecutionTarget } from '@/companion/domain/special-business/normal-targets/yuyuko';

export function getNormalExecutionCookerRequirement(
  target: NormalOrderExecutionTarget | null | undefined,
): { key: string; label: string } | null {
  const cookerName = target?.cookerName.trim();
  if (!cookerName) return null;
  return { key: cookerName, label: cookerName };
}
