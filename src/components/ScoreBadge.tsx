/** 普客匹配分颜色：0灰、1~2橙、3+粉 */
function getScoreColor(score: number) {
  if (score >= 3) return 'bg-pink-100 text-pink-700 dark:bg-pink-500/15 dark:text-pink-200';
  if (score >= 1) return 'bg-orange-100 text-orange-700 dark:bg-orange-500/15 dark:text-orange-200';
  return 'bg-gray-100 text-gray-400 dark:bg-stone-800 dark:text-stone-400';
}

export function CustomerScoreBadges({
  scores,
}: {
  scores: Array<{ name: string; score: number }>;
}) {
  return (
    <span className="inline-flex gap-1 flex-wrap">
      {scores.map((s) => (
        <span
          key={s.name}
          className={`text-[10px] px-1.5 py-0.5 rounded ${getScoreColor(s.score)}`}
        >
          {s.name}:{s.score}
        </span>
      ))}
    </span>
  );
}
