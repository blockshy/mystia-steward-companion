import { Button } from '@/components/ui-kit';

export function PageRecommendationStatus({ pending, error, retained, onRetry }: {
  pending: boolean;
  error: string | null;
  retained: boolean;
  onRetry: () => void;
}) {
  if (!pending && !error) return null;
  return (
    <div className="steward-inline-panel flex flex-wrap items-center justify-between gap-2 px-3 py-2 text-sm" role={error ? 'alert' : 'status'}>
      <span>{error ? `推荐更新失败：${error}` : '正在计算当前条件的推荐。'}{retained ? ' 当前展示上次结果，确认更新前不能修改收藏。' : ''}</span>
      {error && <Button size="sm" variant="outline" onClick={onRetry}>重新计算</Button>}
    </div>
  );
}
