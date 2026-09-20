import { Button } from '@/components/ui-kit';

type RecommendationRecoveryPanelProps = {
  computations: readonly {
    label: string;
    error: string | null;
    retry: () => void;
  }[];
};

export function RecommendationRecoveryPanel({ computations }: RecommendationRecoveryPanelProps) {
  const failed = computations.filter((computation) => computation.error);
  if (failed.length === 0) return null;

  return (
    <div className="space-y-2" role="alert">
      {failed.map(({ label, error, retry }) => (
        <div key={label} className="flex flex-wrap items-center justify-between gap-2 border border-destructive p-3">
          <p className="min-w-0 flex-1 break-words text-sm text-destructive">{label}：{error}</p>
          <Button variant="outline" size="sm" onClick={retry}>重试{label}</Button>
        </div>
      ))}
    </div>
  );
}
