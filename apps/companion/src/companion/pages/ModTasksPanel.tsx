import { useMemo, useState } from 'react';
import { IconRefresh } from '@tabler/icons-react';
import { Badge, Button, Card, CardContent, EmptyRow, InfoLine, Input, ListPanel, SegmentedControl } from '@/components/ui-kit';
import { toggleNumberInList } from '@/companion/storage';
import type { MissionStatusFilter, RareGuestInvitationEntry, RareGuestInvitationResponse, RareGuestInvitationScope, RuntimeMissionContext, RuntimeMissionInfo } from '@/companion/types';
import { RuntimeUnavailable } from '@/companion/pages/shared';
import { DENSE_THREE_COLUMN_GRID, DENSE_TWO_COLUMN_GRID_TIGHT } from '@/companion/pages/shared-constants';
import { buildRecommendationDataIndexes, type RecommendationDataSet } from '@/lib/recommendation-data';

const DEFAULT_MISSION_STATUS_FILTERS: MissionStatusFilter[] = ['available', 'fulfilled'];
const MISSION_STATUS_FILTER_OPTIONS: MissionStatusFilter[] = ['available', 'tracking', 'fulfilled'];

function RareGuestInvitationPanel({
  runtimeLoaded,
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
            data-gamepad-clickable="true"
          />
          <Button
            type="button"
            size="sm"
            className="h-8 px-2.5"
            onClick={onRefreshRareGuestInvitations}
            disabled={!runtimeLoaded || isBusy}
            data-gamepad-clickable="true"
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
          <InfoLine label="状态" value={runtimeLoaded ? '按原生羁绊条件判定' : '等待存档加载'} />
        </div>
        {!inviteAllResult && inviteAllError && <EmptyRow text={inviteAllError} />}
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
                  >
                    羁绊 {level}
                  </Button>
                ))}
                <Button
                  type="button"
                  size="xs"
                  className="ml-auto h-7 px-2"
                  onClick={onInviteAllRareGuests}
                  disabled={!runtimeLoaded || isBusy || filteredAvailableEntries.length === 0}
                  data-gamepad-clickable="true"
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
                      disabled={!runtimeLoaded || isBusy || !canInvite}
                      data-gamepad-clickable="true"
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
        ) : (
          <EmptyRow text="尚未执行邀请" />
        )}
      </div>
    </ListPanel>
  );
}

export function ModTasksPanel({
  runtimeLoaded,
  activeDayMapName,
  activeDayMapLabel,
  missions,
  data,
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
  activeDayMapName: string;
  activeDayMapLabel: string;
  missions: RuntimeMissionContext | null;
  data: RecommendationDataSet;
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
  const [statusFilters, setStatusFilters] = useState<MissionStatusFilter[]>(DEFAULT_MISSION_STATUS_FILTERS);
  const [showExtraInfo, setShowExtraInfo] = useState(false);
  const recipeByFoodId = useMemo(() => buildRecommendationDataIndexes(data).recipeByFoodId, [data]);

  if (!runtimeLoaded) {
    return <RuntimeUnavailable />;
  }

  const rows = missions?.availableMissions ?? [];
  const statusCounts = countMissionStatuses(rows);
  const filteredRows = rows.filter((mission) => matchesMissionStatusFilter(mission, statusFilters));
  const toggleStatusFilter = (filter: MissionStatusFilter) => {
    setStatusFilters((current) => {
      if (current.includes(filter)) return current.filter((item) => item !== filter);
      return [...current, filter];
    });
  };

  return (
    <div className="space-y-4">
      <RareGuestInvitationPanel
        runtimeLoaded={runtimeLoaded}
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

      <Card>
        <CardContent className={`${DENSE_THREE_COLUMN_GRID} p-4 text-sm`}>
          <InfoLine label="任务数据" value={missions ? '已读取' : '暂不可用'} />
          <InfoLine label="可推进任务" value={`${filteredRows.length}/${rows.length} 个`} />
          {showDebugDetails && <InfoLine label="扫描状态" value={missions?.source || missions?.error || '暂无'} />}
        </CardContent>
      </Card>

      <ListPanel
        title={`可推进任务 (${filteredRows.length})`}
        action={(
          <div className="flex flex-wrap gap-1.5" data-gamepad-axis="x">
            {showDebugDetails && (
              <Button
                type="button"
                size="sm"
                variant={showExtraInfo ? 'default' : 'outline'}
                className="h-8 px-2.5"
                aria-pressed={showExtraInfo}
                data-gamepad-clickable="true"
                onClick={() => setShowExtraInfo((value) => !value)}
              >
                显示额外信息
              </Button>
            )}
            {MISSION_STATUS_FILTER_OPTIONS.map((filter) => (
              <Button
                key={filter}
                type="button"
                size="sm"
                variant={statusFilters.includes(filter) ? 'default' : 'outline'}
                className="h-8 px-2.5"
                data-gamepad-clickable="true"
                onClick={() => toggleStatusFilter(filter)}
              >
                {getMissionStatusFilterLabel(filter)} {statusCounts[filter]}
              </Button>
            ))}
          </div>
        )}
      >
        {!missions && <EmptyRow text="任务快照暂不可用" />}
        {missions?.error && <EmptyRow text={missions.error} />}
        {rows.length === 0 && missions && !missions.error && (
          <EmptyRow text="当前进度未读取到可接或正在推进的任务" />
        )}
        {rows.length > 0 && filteredRows.length === 0 && (
          <EmptyRow text="当前筛选条件下没有任务" />
        )}
        {filteredRows.map((mission) => {
          const places = mission.places?.filter(Boolean) ?? [];
          const status = normalizeMissionStatus(mission);
          const shouldShowMissingPlace = places.length === 0 && status === 'available';
          const displayTitle = getMissionDisplayTitle(mission, showDebugDetails && showExtraInfo);
          return (
          <div
            key={`${mission.characterLabel}-${mission.label}`}
            className="border-b py-2 text-sm last:border-b-0"
            data-gamepad-row="true"
            data-gamepad-row-key={`task:${mission.characterLabel}:${mission.label}`}
          >
            <div className="flex items-center justify-between gap-3">
              <span className="min-w-0 truncate font-medium" title={showDebugDetails && showExtraInfo ? mission.title || mission.label : displayTitle}>
                {displayTitle}
              </span>
              <span className="shrink-0 text-muted-foreground">{mission.characterName || mission.characterLabel}</span>
            </div>
            <div className="mt-1 flex flex-wrap gap-1.5">
              {showDebugDetails && showExtraInfo && (
                <>
                  <Badge variant="outline">{mission.label}</Badge>
                  <Badge variant="secondary">{mission.source}</Badge>
                </>
              )}
              <Badge variant={status === 'fulfilled' ? 'default' : status === 'tracking' ? 'secondary' : 'outline'}>
                {getMissionStatusFilterLabel(status)}
              </Badge>
              {mission.targetRecipeId != null && (
                <Badge variant="outline">料理 {mission.targetRecipeName || recipeByFoodId.get(mission.targetRecipeId)?.name || `#${mission.targetRecipeId}`}</Badge>
              )}
              {places.map((place) => <Badge key={place} variant="outline">场景 {place}</Badge>)}
              {shouldShowMissingPlace && <Badge variant="outline">场景 未读取</Badge>}
            </div>
          </div>
          );
        })}
      </ListPanel>
    </div>
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

function matchesMissionStatusFilter(mission: RuntimeMissionInfo, filters: MissionStatusFilter[]): boolean {
  if (filters.length === 0) return false;
  if (mission.finished || mission.status === 'finished') return false;
  return filters.includes(normalizeMissionStatus(mission));
}

function countMissionStatuses(rows: RuntimeMissionInfo[]): Record<MissionStatusFilter, number> {
  return rows.reduce<Record<MissionStatusFilter, number>>((counts, mission) => {
    if (mission.finished || mission.status === 'finished') return counts;
    counts[normalizeMissionStatus(mission)] += 1;
    return counts;
  }, { available: 0, tracking: 0, fulfilled: 0 });
}

function getMissionStatusFilterLabel(filter: MissionStatusFilter): string {
  switch (filter) {
    case 'available':
      return '可接取';
    case 'tracking':
      return '进行中';
    case 'fulfilled':
      return '可完成';
  }
}

function getMissionDisplayTitle(mission: RuntimeMissionInfo, showExtraInfo: boolean): string {
  const title = mission.title?.trim() || '';
  if (showExtraInfo || !isTechnicalMissionText(title)) {
    return title || mission.label || '未解析任务';
  }

  if (mission.targetRecipeName) return `料理任务：${mission.targetRecipeName}`;
  return '未解析任务';
}

function isTechnicalMissionText(value: string | null | undefined): boolean {
  const text = value?.trim();
  if (!text) return false;
  if (text.includes('ScheduledEventMission:')) return true;
  if (text.includes('_Mission') || text.includes('_Event')) return /^[A-Za-z0-9_:.+-]+$/.test(text);
  if (text.startsWith('DLC') && /^[A-Za-z0-9_:.+-]+$/.test(text)) return true;
  return false;
}

function normalizeMissionStatus(mission: RuntimeMissionInfo): MissionStatusFilter {
  if (mission.status === 'available' || mission.status === 'tracking' || mission.status === 'fulfilled') {
    return mission.status;
  }
  if (mission.finished || mission.status === 'finished') return 'fulfilled';
  return mission.started ? 'tracking' : 'available';
}
