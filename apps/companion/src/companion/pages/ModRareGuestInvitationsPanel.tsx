import { useState } from 'react';
import { IconRefresh } from '@tabler/icons-react';
import { Badge, Button, EmptyRow, InfoLine, Input, ListPanel, SegmentedControl } from '@/components/ui-kit';
import { toggleNumberInList } from '@/companion/storage';
import type { RareGuestInvitationEntry, RareGuestInvitationResponse, RareGuestInvitationScope } from '@/companion/types';
import { RuntimeUnavailable } from '@/companion/pages/shared';
import { DENSE_TWO_COLUMN_GRID_TIGHT } from '@/companion/pages/shared-constants';

function RareGuestInvitationPanel({
  runtimeLoaded,
  runtimeDaySceneReady,
  invitationContextReady,
  activeDayMapName,
  activeDayMapLabel,
  inviteScope,
  inviteLevels,
  inviteBusyKey,
  inviteAllResult,
  inviteAllError,
  showDebugDetails,
  onInviteScopeChange,
  onInviteLevelsChange,
  onRefreshRareGuestInvitations,
  onInviteAllRareGuests,
  onInviteRareGuest,
}: {
  runtimeLoaded: boolean;
  runtimeDaySceneReady: boolean;
  invitationContextReady: boolean;
  activeDayMapName: string;
  activeDayMapLabel: string;
  inviteScope: RareGuestInvitationScope;
  inviteLevels: number[];
  inviteBusyKey: string;
  inviteAllResult: RareGuestInvitationResponse | null;
  inviteAllError: string;
  showDebugDetails: boolean;
  onInviteScopeChange: (scope: RareGuestInvitationScope) => void;
  onInviteLevelsChange: (levels: number[]) => void;
  onRefreshRareGuestInvitations: () => void;
  onInviteAllRareGuests: () => void;
  onInviteRareGuest: (guestId: number) => void;
}) {
  const [inviteSearch, setInviteSearch] = useState('');
  const availableEntries = inviteAllResult?.available ?? [];
  const sourceEntries = inviteAllResult?.candidates?.length ? inviteAllResult.candidates : availableEntries;
  const normalizedInviteSearch = normalizeSearchText(inviteSearch);
  const levelOptions = getInvitationKizunaLevelOptions(sourceEntries);
  const levelMatchedCandidateEntries = sourceEntries
    .filter((entry) => matchesInvitationKizunaLevels(entry, inviteLevels))
    .slice();
  const candidateEntries = levelMatchedCandidateEntries
    .filter((entry) => matchesInvitationSearch(entry, normalizedInviteSearch))
    .slice()
    .sort(compareInvitationEntries);
  const filteredAvailableEntries = availableEntries.filter((entry) => matchesInvitationKizunaLevels(entry, inviteLevels));
  const visibleAvailableEntries = filteredAvailableEntries.filter((entry) => matchesInvitationSearch(entry, normalizedInviteSearch));
  const currentInvitedEntries = inviteAllResult
    ? deduplicateInvitationEntries([
      ...(inviteAllResult.existingInvited ?? []),
      ...inviteAllResult.invited,
      ...sourceEntries.filter((entry) => entry.status === 'invited'),
    ])
      .filter((entry) => matchesInvitationKizunaLevels(entry, inviteLevels))
      .filter((entry) => matchesInvitationSearch(entry, normalizedInviteSearch))
      .sort(compareInvitationEntries)
    : [];
  const skippedEntries = inviteAllResult?.skipped.filter((entry) => entry.status !== 'invited') ?? [];
  const isBusy = inviteBusyKey !== '';
  const isListBusy = inviteBusyKey === 'list';
  const isAllBusy = inviteBusyKey === 'all';
  const invitationRuntimeReady = runtimeLoaded && runtimeDaySceneReady && invitationContextReady;
  const currentMapText = inviteAllResult?.currentMapName || activeDayMapName || inviteAllResult?.currentMapLabel || activeDayMapLabel || '未知';

  return (
    <ListPanel
      title={`稀客邀请 (${visibleAvailableEntries.length}/${candidateEntries.length})`}
      action={(
        <div className="flex min-w-0 flex-wrap items-center justify-end gap-1.5" data-gamepad-axis="x">
          <SegmentedControl<RareGuestInvitationScope>
            value={inviteScope}
            options={[
              { value: 'current', label: '当前场景' },
              { value: 'all', label: '全部场景' },
            ]}
            onValueChange={onInviteScopeChange}
            disabled={isBusy}
            aria-label="稀客邀请范围"
            className="h-8 min-w-0"
          />
          <Button
            type="button"
            size="sm"
            className="h-8 px-2.5"
            onClick={onRefreshRareGuestInvitations}
            disabled={!invitationRuntimeReady || isBusy}
            data-gamepad-clickable="true"
            data-gamepad-focus-key="rare-invitations:refresh"
          >
            <IconRefresh className={isListBusy ? 'size-4 animate-spin' : 'size-4'} />
            刷新
          </Button>
        </div>
      )}
    >
      <div className="grid min-w-0 gap-3 text-sm">
        <div className={DENSE_TWO_COLUMN_GRID_TIGHT}>
          <InfoLine label="范围" value={inviteScope === 'all' ? '所有日间场景' : `当前: ${currentMapText}`} />
          <InfoLine
            label="状态"
            value={invitationRuntimeReady ? '按原生羁绊条件判定' : '等待日间场景稳定'}
          />
        </div>
        {inviteAllError && <EmptyRow text={inviteAllError} />}
        {inviteAllResult ? (
          <div className="max-w-full min-w-0 overflow-hidden steward-muted-surface-25 p-2">
            <div className="grid gap-1 text-xs text-muted-foreground sm:grid-cols-2">
              <span className="truncate">
                新增 {inviteAllResult.invitedCount}
                {' · '}
                可邀请 {formatFilteredCount(visibleAvailableEntries.length, filteredAvailableEntries.length)}
                {' · '}
                候选 {formatFilteredCount(candidateEntries.length, levelMatchedCandidateEntries.length)}
              </span>
              <span className="truncate sm:text-right">{inviteAllResult.status || (inviteAllResult.ok ? '已完成' : '失败')}</span>
            </div>
            <div className="mt-2">
              <Input
                value={inviteSearch}
                onChange={(event) => setInviteSearch(event.target.value)}
                placeholder="搜索稀客名称"
                className="w-full sm:w-56"
                aria-label="搜索稀客邀请候选"
              />
            </div>
            {levelOptions.length > 0 && (
              <div className="mt-2 flex flex-wrap items-center gap-1.5" data-gamepad-axis="x">
                <Button
                  type="button"
                  size="xs"
                  variant={inviteLevels.length === 0 ? 'default' : 'outline'}
                  className="h-7 px-2"
                  onClick={() => onInviteLevelsChange([])}
                  disabled={isBusy}
                  data-gamepad-clickable="true"
                  data-gamepad-focus-key="rare-invitations:level:all"
                >
                  全部羁绊
                </Button>
                {levelOptions.map((level) => (
                  <Button
                    key={level}
                    type="button"
                    size="xs"
                    variant={inviteLevels.includes(level) ? 'default' : 'outline'}
                    className="h-7 px-2"
                    onClick={() => onInviteLevelsChange(toggleNumberInList(inviteLevels, level))}
                    disabled={isBusy}
                    data-gamepad-clickable="true"
                    data-gamepad-focus-key={`rare-invitations:level:${level}`}
                  >
                    羁绊 {level}
                  </Button>
                ))}
                <Button
                  type="button"
                  size="xs"
                  className="ml-auto h-7 px-2"
                  onClick={onInviteAllRareGuests}
                  disabled={!invitationRuntimeReady || isBusy || filteredAvailableEntries.length === 0}
                  data-gamepad-clickable="true"
                  data-gamepad-focus-key="rare-invitations:invite-all"
                >
                  {isAllBusy ? '邀请中...' : '邀请全部'}
                </Button>
              </div>
            )}
            <div className="mt-2 grid min-w-0 gap-1.5">
              {candidateEntries.map((entry) => {
                const busy = inviteBusyKey === `guest:${entry.id}`;
                const canInvite = entry.canInvite ?? availableEntries.some((item) => item.id === entry.id);
                const sceneText = formatInvitationScenes(entry);
                const detailText = entry.reason || (showDebugDetails ? entry.runtimeName || `#${entry.id}` : '');
                return (
                  <div
                    key={`${entry.id}-${entry.runtimeName || entry.name}`}
                    className="grid min-w-0 grid-cols-[minmax(0,1fr)_auto] items-center gap-2 steward-background-surface-45 px-2 py-1.5"
                    data-gamepad-row="true"
                    data-gamepad-row-key={`rare-invitation:${entry.id}:${entry.runtimeName || entry.name}`}
                  >
                    <div className="min-w-0">
                      <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5">
                        <span className="truncate text-sm font-medium">{entry.name || entry.runtimeName || `#${entry.id}`}</span>
                        <span className="text-xs text-muted-foreground">{formatInvitationStatus(entry)}</span>
                        {sceneText && <span className="truncate text-xs text-muted-foreground">{sceneText}</span>}
                      </div>
                      {detailText && <div className="truncate text-xs text-muted-foreground">{detailText}</div>}
                    </div>
                    <Button
                      type="button"
                      size="xs"
                      onClick={() => onInviteRareGuest(entry.id)}
                      disabled={!invitationRuntimeReady || isBusy || !canInvite}
                      data-gamepad-clickable="true"
                      data-gamepad-focus-key={`rare-invitations:guest:${entry.id}`}
                    >
                      {busy ? '邀请中' : '邀请'}
                    </Button>
                  </div>
                );
              })}
              {candidateEntries.length === 0 && (
                <EmptyRow text={getInvitationEmptyText(isListBusy, inviteScope, normalizedInviteSearch)} />
              )}
            </div>
            {currentInvitedEntries.length > 0 && (
              <div className="mt-2 max-w-full">
                <div className="mb-1 text-xs text-muted-foreground">当前已邀请</div>
                <div className="flex flex-wrap gap-1">
                  {currentInvitedEntries.slice(0, 12).map((entry) => (
                    <Badge key={`${entry.id}-${entry.runtimeName || entry.name}`} variant="secondary" className="max-w-full truncate">
                      {entry.name || entry.runtimeName || `#${entry.id}`}
                    </Badge>
                  ))}
                  {currentInvitedEntries.length > 12 && (
                    <Badge variant="outline">+{currentInvitedEntries.length - 12}</Badge>
                  )}
                </div>
              </div>
            )}
            {skippedEntries.length > 0 && (
              <div className="mt-2 max-w-full break-words text-xs text-muted-foreground">
                跳过：{summarizeInvitationSkipped(skippedEntries)}
              </div>
            )}
          </div>
        ) : !inviteAllError && (
          <EmptyRow text={invitationRuntimeReady ? '正在读取稀客候选' : '等待日间场景稳定'} />
        )}
      </div>
    </ListPanel>
  );
}

export function ModRareGuestInvitationsPanel({
  runtimeLoaded,
  runtimeDaySceneReady,
  invitationContextReady,
  activeDayMapName,
  activeDayMapLabel,
  inviteScope,
  inviteLevels,
  inviteBusyKey,
  inviteAllResult,
  inviteAllError,
  showDebugDetails,
  onInviteScopeChange,
  onInviteLevelsChange,
  onRefreshRareGuestInvitations,
  onInviteAllRareGuests,
  onInviteRareGuest,
}: {
  runtimeLoaded: boolean;
  runtimeDaySceneReady: boolean;
  invitationContextReady: boolean;
  activeDayMapName: string;
  activeDayMapLabel: string;
  inviteScope: RareGuestInvitationScope;
  inviteLevels: number[];
  inviteBusyKey: string;
  inviteAllResult: RareGuestInvitationResponse | null;
  inviteAllError: string;
  showDebugDetails: boolean;
  onInviteScopeChange: (scope: RareGuestInvitationScope) => void;
  onInviteLevelsChange: (levels: number[]) => void;
  onRefreshRareGuestInvitations: () => void;
  onInviteAllRareGuests: () => void;
  onInviteRareGuest: (guestId: number) => void;
}) {
  if (!runtimeLoaded) {
    return <RuntimeUnavailable />;
  }

  return (
    <RareGuestInvitationPanel
      runtimeLoaded={runtimeLoaded}
      runtimeDaySceneReady={runtimeDaySceneReady}
      invitationContextReady={invitationContextReady}
      activeDayMapName={activeDayMapName}
      activeDayMapLabel={activeDayMapLabel}
      inviteScope={inviteScope}
      inviteLevels={inviteLevels}
      inviteBusyKey={inviteBusyKey}
      inviteAllResult={inviteAllResult}
      inviteAllError={inviteAllError}
      showDebugDetails={showDebugDetails}
      onInviteScopeChange={onInviteScopeChange}
      onInviteLevelsChange={onInviteLevelsChange}
      onRefreshRareGuestInvitations={onRefreshRareGuestInvitations}
      onInviteAllRareGuests={onInviteAllRareGuests}
      onInviteRareGuest={onInviteRareGuest}
    />
  );
}

function formatFilteredCount(visibleCount: number, totalCount: number): string {
  return visibleCount === totalCount ? String(totalCount) : `${visibleCount}/${totalCount}`;
}

function getInvitationEmptyText(isListBusy: boolean, inviteScope: RareGuestInvitationScope, normalizedSearch: string): string {
  if (isListBusy) return '正在读取稀客候选';
  if (normalizedSearch) return '没有匹配的稀客候选';
  return inviteScope === 'all' ? '暂无稀客候选' : '当前场景暂无稀客候选';
}

function summarizeInvitationSkipped(entries: RareGuestInvitationEntry[]): string {
  const counts = new Map<string, number>();
  for (const entry of entries) {
    const reason = entry.reason || '未知原因';
    counts.set(reason, (counts.get(reason) ?? 0) + 1);
  }

  return Array.from(counts.entries())
    .map(([reason, count]) => `${reason} ${count}`)
    .join(' · ');
}

function compareInvitationEntries(left: RareGuestInvitationEntry, right: RareGuestInvitationEntry): number {
  const leftCanInvite = left.canInvite ? 1 : 0;
  const rightCanInvite = right.canInvite ? 1 : 0;
  if (leftCanInvite !== rightCanInvite) return rightCanInvite - leftCanInvite;

  const leftCurrent = left.isCurrentScene ? 1 : 0;
  const rightCurrent = right.isCurrentScene ? 1 : 0;
  if (leftCurrent !== rightCurrent) return rightCurrent - leftCurrent;

  const sceneCompare = formatInvitationScenes(left).localeCompare(formatInvitationScenes(right), 'zh-Hans-CN');
  if (sceneCompare !== 0) return sceneCompare;

  const levelCompare = normalizeInvitationKizunaLevel(left) - normalizeInvitationKizunaLevel(right);
  if (levelCompare !== 0) return levelCompare;

  return (left.name || left.runtimeName || `#${left.id}`).localeCompare(
    right.name || right.runtimeName || `#${right.id}`,
    'zh-Hans-CN',
  );
}

function deduplicateInvitationEntries(entries: RareGuestInvitationEntry[]): RareGuestInvitationEntry[] {
  const byKey = new Map<string, RareGuestInvitationEntry>();
  for (const entry of entries) {
    const key = entry.id >= 0
      ? `id:${entry.id}`
      : `name:${entry.runtimeName || entry.name}`;
    if (!byKey.has(key)) byKey.set(key, entry);
  }
  return Array.from(byKey.values());
}

function normalizeInvitationKizunaLevel(entry: RareGuestInvitationEntry): number {
  return typeof entry.kizunaLevel === 'number' && Number.isFinite(entry.kizunaLevel) ? entry.kizunaLevel : 999;
}

function getInvitationKizunaLevelOptions(entries: RareGuestInvitationEntry[]): number[] {
  return Array.from(new Set(entries
    .map(normalizeInvitationKizunaLevel)
    .filter((level) => level !== 999)))
    .sort((a, b) => a - b);
}

function matchesInvitationKizunaLevels(entry: RareGuestInvitationEntry, levels: number[]): boolean {
  if (levels.length === 0) return true;
  return levels.includes(normalizeInvitationKizunaLevel(entry));
}

function matchesInvitationSearch(entry: RareGuestInvitationEntry, normalizedSearch: string): boolean {
  if (!normalizedSearch) return true;
  const text = normalizeSearchText([
    entry.name,
    entry.runtimeName,
    entry.id >= 0 ? `#${entry.id}` : '',
    entry.id >= 0 ? String(entry.id) : '',
  ].filter(Boolean).join(' '));
  return text.includes(normalizedSearch) || isOrderedFuzzyMatch(normalizedSearch, text);
}

function normalizeSearchText(value: string): string {
  return value.trim().toLocaleLowerCase('zh-Hans-CN').replace(/\s+/g, '');
}

function isOrderedFuzzyMatch(needle: string, haystack: string): boolean {
  if (!needle) return true;
  let index = 0;
  for (const char of haystack) {
    if (char === needle[index]) index++;
    if (index === needle.length) return true;
  }
  return false;
}

function formatInvitationScenes(entry: RareGuestInvitationEntry): string {
  const scenes = (entry.sceneNames?.length ? entry.sceneNames : entry.sceneLabels ?? [])
    .filter(Boolean);
  if (scenes.length === 0) return '';
  return scenes.slice(0, 2).join(' / ') + (scenes.length > 2 ? ` +${scenes.length - 2}` : '');
}

function formatInvitationStatus(entry: RareGuestInvitationEntry): string {
  if (entry.canInvite) return '可邀请';
  if (entry.status === 'invited') return '已邀请';
  if (entry.status === 'low-kizuna' && typeof entry.kizunaLevel === 'number') return `羁绊 ${entry.kizunaLevel}`;
  if (entry.status === 'unavailable') return '不可见';
  if (entry.status === 'missing-dialog') return '无邀请对话';
  return entry.reason || '不可邀请';
}
