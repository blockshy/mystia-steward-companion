/**
 * 将服务端已识别的特殊订单角色映射为页面样式。
 * 此名单只决定是否显示特殊经营徽章，不推导经营规则、目标食材、评分或自动化执行顺序。
 */
const SPECIAL_ORDER_ROLES = new Set([
  'wacky-koishi-boss', 'wacky-ghost-order', 'wacky-target-order', 'yuyuko-boss-order',
  'yuuma-boss-order', 'yuuma-order-unverified',
  'mizuchi-story-possessed-order', 'mizuchi-story-ordinary-order', 'mizuchi-story-unverified-order',
  'mizuchi-trial-possessed-order', 'mizuchi-trial-ordinary-order', 'mizuchi-trial-unverified-order',
]);

export function isSpecialBusinessOrderRole(role: string | null | undefined): boolean {
  return SPECIAL_ORDER_ROLES.has((role ?? '').trim());
}
