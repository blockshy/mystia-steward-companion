import {
  DEFAULT_AUTO_ROLLBACKS,
  DEFAULT_AUTO_STEP_RETRIES,
  type CompanionPreferences,
} from '@/companion/preferences';

const AUTO_JOB_STALL_MS = 90000;

export type AutomationStep =
  | 'idle'
  | 'match-order'
  | 'ensure-beverage'
  | 'ensure-cooking'
  | 'wait-food-tray'
  | 'wait-food-stored'
  | 'deliver-food'
  | 'complete-order'
  | 'done'
  | 'paused';

export interface RareAutomationRecipeTarget {
  recipeId: number;
  foodId: number;
  recipeName: string;
  cookerName: string;
  extraIngredientIds: number[];
  acceptableFoodIds: number[];
  favorite: boolean;
  preferenceFallback: boolean;
}

export interface RareAutomationBeverageTarget {
  beverageId: number;
  beverageName: string;
  favorite: boolean;
}

export interface AutoFirstOrderState {
  orderKey: string;
  recipeTarget: RareAutomationRecipeTarget | null;
  beverageTarget: RareAutomationBeverageTarget | null;
  prepared: boolean;
  preparedAtMs: number;
  beverageHandled: boolean;
  beverageHandledAtMs: number;
  step: AutomationStep;
  stepStartedAtMs: number;
  lastProgressAtMs: number;
  retryCount: number;
  rollbackCount: number;
  lastError: string;
  paused: boolean;
}

export interface NormalAutoOrderState {
  orderKey: string;
  prepared: boolean;
  preparedAtMs: number;
  beverageHandled: boolean;
  beverageHandledAtMs: number;
  collected: boolean;
  foodDelivered: boolean;
  foodDeliveredAtMs: number;
  completed: boolean;
  completedAtMs: number;
  step: AutomationStep;
  stepStartedAtMs: number;
  lastProgressAtMs: number;
  retryCount: number;
  rollbackCount: number;
  lastError: string;
  paused: boolean;
}

export interface OrderPreparationStep {
  name: string;
  ok: boolean;
  skipped: boolean;
  message: string;
}

export interface OrderPreparationResponse {
  ok: boolean;
  prepared: boolean;
  servedFood?: boolean;
  servedBeverage?: boolean;
  completedOrder?: boolean;
  error: string | null;
  order: {
    deskCode: number;
    guestId: number | null;
    guestName: string;
    foodTag: string;
    beverageTag: string;
  };
  recipeId: number;
  recipeName: string;
  beverageId: number;
  beverageName: string;
  steps: OrderPreparationStep[];
}

export function emptyAutoFirstOrderState(orderKey = '', now = 0): AutoFirstOrderState {
  return {
    orderKey,
    recipeTarget: null,
    beverageTarget: null,
    prepared: false,
    preparedAtMs: 0,
    beverageHandled: false,
    beverageHandledAtMs: 0,
    step: 'idle',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    rollbackCount: 0,
    lastError: '',
    paused: false,
  };
}

export function emptyNormalAutoOrderState(orderKey: string, now = 0): NormalAutoOrderState {
  return {
    orderKey,
    prepared: false,
    preparedAtMs: 0,
    beverageHandled: false,
    beverageHandledAtMs: 0,
    collected: false,
    foodDelivered: false,
    foodDeliveredAtMs: 0,
    completed: false,
    completedAtMs: 0,
    step: 'match-order',
    stepStartedAtMs: now,
    lastProgressAtMs: now,
    retryCount: 0,
    rollbackCount: 0,
    lastError: '',
    paused: false,
  };
}

export function isAutomationTimestampStale(value: number, now: number, timeoutMs: number): boolean {
  return value > 0 && now - value >= timeoutMs;
}

export function markAutomationWaiting<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  step: AutomationStep,
  now: number,
  message: string,
): T {
  return {
    ...state,
    step,
    stepStartedAtMs: state.step === step ? state.stepStartedAtMs : now,
    lastError: message,
  };
}

export function pauseAutomationState<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  now: number,
  message: string,
): T {
  return {
    ...state,
    paused: true,
    step: 'paused',
    stepStartedAtMs: now,
    lastError: message,
  };
}

export function updateAutomationAfterResponse<T extends AutoFirstOrderState | NormalAutoOrderState>(
  state: T,
  response: OrderPreparationResponse,
  now: number,
  step: AutomationStep,
  stopOnError: boolean,
  maxStepRetries = DEFAULT_AUTO_STEP_RETRIES,
): T {
  const failed = !response.ok;
  const transientFailure = failed && isTransientAutoPreparationFailure(response);
  const hardFailure = failed && isHardAutoPreparationFailure(response);
  const nextRetryCount = failed ? state.retryCount + 1 : 0;
  const stalled = failed && state.lastProgressAtMs > 0 && now - state.lastProgressAtMs >= AUTO_JOB_STALL_MS;
  const shouldPause = failed
    && stopOnError
    && (hardFailure || (!transientFailure && (stalled || nextRetryCount >= maxStepRetries)));
  const progressed = response.ok || response.steps.some(isMeaningfulAutomationProgressStep);
  const nextStep = response.ok
    ? step
    : shouldPause
      ? 'paused'
      : step;

  return {
    ...state,
    step: nextStep,
    stepStartedAtMs: state.step === nextStep ? state.stepStartedAtMs : now,
    lastProgressAtMs: progressed ? now : state.lastProgressAtMs,
    retryCount: nextRetryCount,
    lastError: failed
      ? stalled
        ? `${summarizeOrderPreparationFailure(response)}；超过 ${Math.round(AUTO_JOB_STALL_MS / 1000)} 秒没有进展`
        : summarizeOrderPreparationFailure(response)
      : '',
    paused: shouldPause,
  };
}

export function formatAutomationState(
  state: AutoFirstOrderState | NormalAutoOrderState,
  preferences?: CompanionPreferences,
): string {
  const now = Date.now();
  const maxStepRetries = preferences?.autoMaxStepRetries ?? DEFAULT_AUTO_STEP_RETRIES;
  const maxRollbacks = preferences?.autoMaxRollbacks ?? DEFAULT_AUTO_ROLLBACKS;
  const parts = [
    `状态 ${getAutomationStepLabel(state.step)}`,
    state.stepStartedAtMs > 0 ? `${Math.max(0, Math.round((now - state.stepStartedAtMs) / 1000))}秒` : '',
    state.retryCount > 0 ? `重试 ${state.retryCount}/${maxStepRetries}` : '',
    state.rollbackCount > 0 ? `回退 ${state.rollbackCount}/${maxRollbacks}` : '',
    state.lastError ? `最近 ${state.lastError}` : '',
  ].filter(Boolean);
  return parts.join(' · ');
}

export function getAutomationStepLabel(step: AutomationStep): string {
  switch (step) {
    case 'match-order':
      return '匹配订单';
    case 'ensure-beverage':
      return '确认酒水';
    case 'ensure-cooking':
      return '确认料理';
    case 'wait-food-tray':
      return '等待送餐盘';
    case 'wait-food-stored':
      return '等待保温箱';
    case 'complete-order':
      return '完成订单';
    case 'done':
      return '完成';
    case 'paused':
      return '暂停';
    default:
      return '待命';
  }
}

export function isMeaningfulAutomationProgressStep(step: OrderPreparationStep): boolean {
  if (!step.ok || step.skipped) return false;
  if (step.name.includes('选择') || step.name.includes('匹配')) return false;
  return step.name.includes('自动取酒')
    || step.name.includes('自动开始料理')
    || step.name.includes('自动收取料理')
    || step.name.includes('送达料理')
    || step.name.includes('送达酒水')
    || step.name.includes('普客开始料理')
    || step.name.includes('普客保温箱')
    || step.name.includes('普客送达酒水')
    || step.name.includes('普客送达料理')
    || step.name.includes('触发普客评价')
    || step.name.includes('写入订单')
    || step.name.includes('触发上菜评价');
}

export function emptyMissingTrayParts() {
  return { food: false, beverage: false };
}

export function getMissingTrayParts(response: OrderPreparationResponse) {
  const missing = emptyMissingTrayParts();
  if (response.ok) return missing;
  for (const step of response.steps) {
    if (step.ok || step.skipped) continue;
    if (step.name.includes('匹配送餐盘料理')) missing.food = true;
    if (step.name.includes('匹配送餐盘酒水')) missing.beverage = true;
  }
  return missing;
}

export function didCompleteStep(response: OrderPreparationResponse, name: string): boolean {
  return response.steps.some((step) => step.name === name && step.ok && !step.skipped);
}

export function didAcknowledgeStep(response: OrderPreparationResponse, name: string): boolean {
  return response.steps.some((step) => step.name === name && step.ok && !isInactiveSkippedStep(step));
}

export function didNormalOrderCollectToWarmer(response: OrderPreparationResponse): boolean {
  return response.steps.some((step) => step.ok
    && (step.message.includes('已在普客保温箱')
      || step.message.includes('已自动收至普客保温箱')
      || step.message.includes('该订单已经送达料理')
      || step.message.includes('目标普客订单已有料理')));
}

export function didNormalOrderDeliverBeverage(response: OrderPreparationResponse): boolean {
  return Boolean(response.servedBeverage)
    || didCompleteStep(response, '普客送达酒水')
    || response.steps.some((step) => step.name === '普客送达酒水' && step.ok && !isInactiveSkippedStep(step));
}

export function didNormalOrderDeliverFood(response: OrderPreparationResponse): boolean {
  return Boolean(response.servedFood)
    || didCompleteStep(response, '普客送达料理')
    || response.steps.some((step) => step.name === '普客送达料理' && step.ok && !isInactiveSkippedStep(step));
}

export function didNormalOrderComplete(response: OrderPreparationResponse): boolean {
  return Boolean(response.completedOrder) || didCompleteStep(response, '触发普客评价');
}

export function didNormalOrderWarmerMissing(response: OrderPreparationResponse): boolean {
  return response.steps.some((step) => step.name === '普客保温箱复查'
    && (step.message.includes('未读取到该料理')
      || step.message.includes('已撤销本地回执')
      || step.message.includes('目标料理数量 0')));
}

export function didNormalOrderCookingStillPending(response: OrderPreparationResponse): boolean {
  return didOrderCookingStillPending(response, '普客开始料理');
}

export function didOrderCookingStillPending(response: OrderPreparationResponse, stepName: string): boolean {
  return response.steps.some((step) => step.name === stepName
    && step.ok
    && step.skipped
    && (step.message.includes('已在制作中')
      || step.message.includes('等待完成后会自动收至普客保温箱')
      || step.message.includes('等待完成后会自动收入送餐盘')));
}

export function isInactiveSkippedStep(step: OrderPreparationStep): boolean {
  if (!step.skipped) return false;
  return step.message.includes('设置已关闭')
    || step.message.includes('尚未获得')
    || step.message.includes('订单尚未同时满足');
}

export function isTransientAutoPreparationFailure(response: OrderPreparationResponse): boolean {
  const text = [
    response.error ?? '',
    ...response.steps.map((step) => `${step.name} ${step.message}`),
  ].join('\n');
  return text.includes('当前没有空闲厨具')
    || text.includes('当前没有读取到任何厨具')
    || text.includes('厨具被占用')
    || text.includes('送餐盘已满')
    || text.includes('送餐盘对象不可用')
    || text.includes('厨具管理器不可用')
    || text.includes('运行时对象')
    || text.includes('经营状态刚刷新')
    || text.includes('未找到当前第一笔')
    || text.includes('暂存容器不可用')
    || text.includes('普客保温箱中没有找到目标料理')
    || text.includes('等待下一轮重试')
    || text.includes('已有待收取任务')
    || text.includes('已在制作中')
    || text.includes('长时间未读取到成品对象');
}

export function isHardAutoPreparationFailure(response: OrderPreparationResponse): boolean {
  const text = [
    response.error ?? '',
    ...response.steps.map((step) => `${step.name} ${step.message}`),
  ].join('\n');
  return text.includes('材料不足')
    || text.includes('当前库存为 0')
    || text.includes('没有可用的推荐')
    || text.includes('没有有效的料理 ID')
    || text.includes('无法从游戏数据库读取料理配方')
    || text.includes('未找到料理')
    || text.includes('成品不是目标料理')
    || text.includes('订单已有其他待送达料理')
    || text.includes('收藏限定已开启');
}

export function summarizeOrderPreparationFailure(response: OrderPreparationResponse): string {
  const failed = response.steps.find((step) => !step.ok && !step.skipped);
  return failed ? `${failed.name}: ${failed.message}` : response.error ?? '未知状态';
}
