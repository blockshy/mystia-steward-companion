export type ServiceOrderCollectionState =
  | { kind: 'ready' }
  | { kind: 'empty'; message: string }
  | { kind: 'updating'; message: string; label?: string }
  | {
      kind: 'error';
      message: string;
      detail?: string;
      emptyLabel?: string;
      retainedLabel?: string;
      updating?: boolean;
      updatingLabel?: string;
    };

/**
 * 归约稀客推荐集合的页面状态。
 *
 * participation 尚未对齐时优先隐藏所有行；对齐后，真实推荐错误必须先于“参与队列为空”，
 * 避免新增或刚启用的订单在 retained result 不匹配时把 Worker 失败误报为空队列。
 */
export function buildRareOrderRecommendationCollectionState({
  participationEnabled,
  participationReady,
  rowCount,
  updateError,
  pending,
}: {
  participationEnabled: boolean;
  participationReady: boolean;
  rowCount: number;
  updateError: string | null;
  pending: boolean;
}): ServiceOrderCollectionState {
  if (participationEnabled && !participationReady) {
    return {
      kind: 'updating',
      message: '稀客调度权威状态正在同步，当前稀客推荐暂不显示。',
      label: '调度同步中',
    };
  }
  if (updateError) {
    return {
      kind: 'error',
      message: '推荐更新失败',
      detail: updateError,
      emptyLabel: '推荐更新失败',
      updating: pending,
    };
  }
  if (participationEnabled && rowCount === 0) {
    return {
      kind: 'empty',
      message: '当前参与队列暂无已启用订单；请到“稀客队列”启用需要处理的订单。',
    };
  }
  if (pending) return { kind: 'updating', message: '推荐计算中' };
  if (rowCount === 0) return { kind: 'empty', message: '暂无当前稀客点单推荐' };
  return { kind: 'ready' };
}
