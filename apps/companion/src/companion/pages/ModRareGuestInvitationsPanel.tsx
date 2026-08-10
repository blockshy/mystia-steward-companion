import { useState } from 'react';
import { IconRefresh } from '@tabler/icons-react';

import {
  Badge,
  Button,
  EmptyRow,
  EmptyState,
  Input,
  ListPanel,
  SegmentedControl,
} from '@/components/ui-kit';
import { ModuleControlPanel } from '@/companion/pages/ModuleControlPanel';
import { RuntimeUnavailable } from '@/companion/pages/shared';
import { DENSE_FOUR_COLUMN_GRID } from '@/companion/pages/shared-constants';
import { toggleNumberInList } from '@/companion/storage';
import type {
  RareGuestInvitationEntry,
  RareGuestInvitationResponse,
  RareGuestInvitationScope,
} from '@/companion/types';

export interface ModRareGuestInvitationsPanelProps {
  runtimeLoaded: boolean;
  runtimeDaySceneReady: boolean;
  rareGuestInvitationModuleEnabled: boolean;
  rareGuestInvitationModuleToggleDisabled: boolean;
  invitationContextReady: boolean;
  activeDayMapName: string;
  activeDayMapLabel: string;
  inviteScope: RareGuestInvitationScope;
  inviteLevels: number[];
  inviteBusyKey: string;
  inviteAllResult: RareGuestInvitationResponse | null;
  inviteAllError: string;
  showDebugDetails: boolean;
  onRareGuestInvitationModuleEnabledChange: (enabled: boolean) => void;
  onInviteScopeChange: (scope: RareGuestInvitationScope) => void;
  onInviteLevelsChange: (levels: number[]) => void;
  onRefreshRareGuestInvitations: () => void;
  onInviteAllRareGuests: () => void;
  onInviteRareGuest: (guestId: number) => void;
}

interface RareGuestInvitationPanelProps {
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
}

export function ModRareGuestInvitationsPanel({
  runtimeLoaded,
  runtimeDaySceneReady,
  rareGuestInvitationModuleEnabled,
  rareGuestInvitationModuleToggleDisabled,
  invitationContextReady,
  activeDayMapName,
  activeDayMapLabel,
  inviteScope,
  inviteLevels,
  inviteBusyKey,
  inviteAllResult,
  inviteAllError,
  showDebugDetails,
  onRareGuestInvitationModuleEnabledChange,
  onInviteScopeChange,
  onInviteLevelsChange,
  onRefreshRareGuestInvitations,
  onInviteAllRareGuests,
  onInviteRareGuest,
}: ModRareGuestInvitationsPanelProps) {
  return (
    <div className="space-y-4">
      <ModuleControlPanel
        moduleId="rare-guest-invitations"
        label="启用稀客邀请模块"
        description={rareGuestInvitationModuleToggleDisabled
          ? '邀请写入已经提交，需等待 Mod 返回确定结果后才能关闭模块。'
          : '开启后才会读取日间稀客候选并开放单独或批量邀请；关闭时不会发起邀请读取或写入。'}
        enabled={rareGuestInvitationModuleEnabled}
        disabled={rareGuestInvitationModuleToggleDisabled}
        focusKey="rare-invitations:module-toggle"
        onEnabledChange={onRareGuestInvitationModuleEnabledChange}
      />
      {!rareGuestInvitationModuleEnabled ? (
        <EmptyState text="稀客邀请模块已停用。手动开启总控后才会读取候选或执行邀请。" />
      ) : !runtimeLoaded ? (
        <RuntimeUnavailable />
      ) : (
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
      )}
    </div>
  );
}

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
}: RareGuestInvitationPanelProps) {
  const [inviteSearch, setInviteSearch] = useState('');
  const availableEntries = inviteAllResult?.available ?? [];
  const sourceEntries = inviteAllResult?.candidates ?? [];
  const availableIds = new Set(availableEntries.map((entry) => entry.id));
  const normalizedInviteSearch = normalizeSearchText(inviteSearch);
  const invitationCandidates = sourceEntries.filter((entry) => entry.status !== 'invited');
  const levelOptions = getInvitationKizunaLevelOptions(invitationCandidates);
  const levelMatchedCandidateEntries = invitationCandidates
    .filter((entry) => matchesInvitationKizunaLevels(entry, inviteLevels))
    .sort(compareInvitationEntries);
  const visibleCandidateEntries = levelMatchedCandidateEntries
    .filter((entry) => matchesInvitationSearch(entry, normalizedInviteSearch));
  const levelMatchedAvailableEntries = levelMatchedCandidateEntries
    .filter((entry) => canInviteEntry(entry, availableIds));
  const levelMatchedUnavailableEntries = levelMatchedCandidateEntries
    .filter((entry) => !canInviteEntry(entry, availableIds));
  const visibleAvailableEntries = visibleCandidateEntries
    .filter((entry) => canInviteEntry(entry, availableIds));
  const visibleUnavailableEntries = visibleCandidateEntries
    .filter((entry) => !canInviteEntry(entry, availableIds));
  const batchEligibleEntries = availableEntries
    .filter((entry) => matchesInvitationKizunaLevels(entry, inviteLevels));
  const currentInvitedEntries = inviteAllResult
    ? deduplicateInvitationEntries([
      ...inviteAllResult.existingInvited,
      ...inviteAllResult.invited,
      ...sourceEntries.filter((entry) => entry.status === 'invited'),
    ]).sort(compareInvitationEntries)
    : [];
  const isBusy = inviteBusyKey !== '';
  const isListBusy = inviteBusyKey === 'list';
  const isAllBusy = inviteBusyKey === 'all';
  const invitationRuntimeReady = runtimeLoaded && runtimeDaySceneReady && invitationContextReady;
  const currentMapText = inviteAllResult?.currentMapName
    || activeDayMapName
    || inviteAllResult?.currentMapLabel
    || activeDayMapLabel
    || '未知';

  return (
    <ListPanel
      title="稀客邀请"
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
      gamepadScrollKey="rare-invitations"
      gamepadScrollLabel="稀客邀请"
    >
      <div className="grid min-w-0 gap-3 text-sm">
        <div className={DENSE_FOUR_COLUMN_GRID} data-rare-invitation-metrics="true">
          <InvitationMetric
            label={inviteScope === 'all' ? '当前场景' : '邀请范围'}
            value={inviteScope === 'all' ? currentMapText : `当前 · ${currentMapText}`}
          />
          <InvitationMetric
            label="运行状态"
            value={invitationRuntimeReady ? '日间场景已就绪' : '等待日间场景稳定'}
          />
          <InvitationMetric label="候选总数" value={inviteAllResult?.candidateCount ?? '—'} />
          <InvitationMetric label="当前可邀请" value={inviteAllResult ? availableEntries.length : '—'} />
        </div>

        {inviteAllError && <EmptyRow text={inviteAllError} />}
        {inviteAllResult ? (
          <div className="grid max-w-full min-w-0 gap-3" data-rare-invitation-content="true">
            <div
              className="flex min-w-0 flex-wrap items-center gap-2 border-l-2 border-primary/50 bg-primary/5 px-3 py-2 text-xs"
              data-rare-invitation-result-status="true"
            >
              <Badge variant={inviteAllResult.ok ? 'secondary' : 'outline'}>
                {invitationRuntimeReady ? '读取已就绪' : '等待运行时'}
              </Badge>
              <span className="min-w-0 flex-1 break-words text-muted-foreground">
                {inviteAllResult.status || (inviteAllResult.ok ? '候选读取完成' : '候选读取失败')}
              </span>
              {inviteAllResult.invitedCount > 0 && (
                <span className="font-medium text-foreground">本次新增 {inviteAllResult.invitedCount}</span>
              )}
            </div>

            <section
              className="steward-inline-panel min-w-0 space-y-2 px-3 py-3"
              data-rare-invitation-section="invited"
            >
              <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
                <h3 className="text-sm font-semibold">今晚已邀请</h3>
                <Badge variant="secondary" data-rare-invitation-invited-count={currentInvitedEntries.length}>
                  {currentInvitedEntries.length} 位
                </Badge>
              </div>
              {currentInvitedEntries.length > 0 ? (
                <div className="flex min-w-0 flex-wrap gap-1.5" data-rare-invitation-invited-list="true">
                  {currentInvitedEntries.map((entry) => (
                    <Badge
                      key={`${entry.id}-${entry.runtimeName || entry.name}`}
                      variant="secondary"
                      className="h-auto max-w-full whitespace-normal break-words py-1 text-left"
                      data-rare-invitation-invited-id={entry.id}
                    >
                      {getInvitationEntryName(entry)}
                    </Badge>
                  ))}
                </div>
              ) : (
                <EmptyRow text="今晚还没有已邀请的稀客。" />
              )}
            </section>

            <section
              className="steward-muted-surface-25 min-w-0 space-y-3 p-3"
              data-rare-invitation-section="filters"
            >
              <div className="grid min-w-0 gap-3 min-[720px]:grid-cols-[minmax(12rem,1fr)_minmax(0,2fr)]">
                <label className="grid min-w-0 gap-1.5 text-xs font-medium">
                  搜索候选
                  <Input
                    value={inviteSearch}
                    onChange={(event) => setInviteSearch(event.target.value)}
                    placeholder="搜索名称或 ID"
                    className="w-full"
                    aria-label="搜索稀客邀请候选"
                    data-rare-invitation-search="true"
                  />
                </label>
                <div className="grid min-w-0 gap-1.5">
                  <span className="text-xs font-medium">羁绊筛选</span>
                  <div className="flex min-w-0 flex-wrap items-center gap-1.5" data-gamepad-axis="x">
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
                  </div>
                </div>
              </div>
              <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
                <p className="min-w-0 flex-1 break-words text-xs text-muted-foreground">
                  搜索只改变下方列表展示；羁绊筛选同时决定批量邀请范围。
                </p>
                <Button
                  type="button"
                  size="sm"
                  className="w-full min-[520px]:w-auto"
                  onClick={onInviteAllRareGuests}
                  disabled={!invitationRuntimeReady || isBusy || batchEligibleEntries.length === 0}
                  data-gamepad-clickable="true"
                  data-gamepad-focus-key="rare-invitations:invite-all"
                >
                  {isAllBusy ? '邀请中...' : `邀请全部匹配项 (${batchEligibleEntries.length})`}
                </Button>
              </div>
            </section>

            <InvitationCandidateSection
              kind="available"
              title="可邀请"
              entries={visibleAvailableEntries}
              totalAfterLevelFilter={levelMatchedAvailableEntries.length}
              isListBusy={isListBusy}
              inviteScope={inviteScope}
              normalizedSearch={normalizedInviteSearch}
              invitationRuntimeReady={invitationRuntimeReady}
              inviteBusyKey={inviteBusyKey}
              showDebugDetails={showDebugDetails}
              onInviteRareGuest={onInviteRareGuest}
            />
            <InvitationCandidateSection
              kind="unavailable"
              title="暂不可邀请"
              entries={visibleUnavailableEntries}
              totalAfterLevelFilter={levelMatchedUnavailableEntries.length}
              isListBusy={isListBusy}
              inviteScope={inviteScope}
              normalizedSearch={normalizedInviteSearch}
              invitationRuntimeReady={invitationRuntimeReady}
              inviteBusyKey={inviteBusyKey}
              showDebugDetails={showDebugDetails}
              onInviteRareGuest={onInviteRareGuest}
            />

            {showDebugDetails && (
              <div className="break-all font-mono text-xs text-muted-foreground" data-rare-invitation-debug="true">
                source=
                {inviteAllResult.source || '<none>'}
                ; diagnostics=
                {inviteAllResult.diagnostics || '<none>'}
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

function InvitationMetric({ label, value }: { label: string; value: string | number }) {
  return (
    <div className="steward-muted-surface-25 min-w-0 px-3 py-2.5">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-1 min-w-0 break-words text-sm font-semibold">{value}</div>
    </div>
  );
}

function InvitationCandidateSection({
  kind,
  title,
  entries,
  totalAfterLevelFilter,
  isListBusy,
  inviteScope,
  normalizedSearch,
  invitationRuntimeReady,
  inviteBusyKey,
  showDebugDetails,
  onInviteRareGuest,
}: {
  kind: 'available' | 'unavailable';
  title: string;
  entries: RareGuestInvitationEntry[];
  totalAfterLevelFilter: number;
  isListBusy: boolean;
  inviteScope: RareGuestInvitationScope;
  normalizedSearch: string;
  invitationRuntimeReady: boolean;
  inviteBusyKey: string;
  showDebugDetails: boolean;
  onInviteRareGuest: (guestId: number) => void;
}) {
  const available = kind === 'available';

  return (
    <section
      className="min-w-0 space-y-2"
      data-rare-invitation-section={kind}
      data-rare-invitation-visible-count={entries.length}
      data-rare-invitation-filtered-count={totalAfterLevelFilter}
    >
      <div className="flex min-w-0 flex-wrap items-center justify-between gap-2">
        <h3 className="text-sm font-semibold">{title}</h3>
        <Badge variant={available ? 'secondary' : 'outline'}>
          {normalizedSearch && entries.length !== totalAfterLevelFilter
            ? `显示 ${entries.length} / ${totalAfterLevelFilter}`
            : `${entries.length} 位`}
        </Badge>
      </div>
      <div className="grid min-w-0 gap-2">
        {entries.map((entry) => (
          <InvitationCandidateRow
            key={`${entry.id}-${entry.runtimeName || entry.name}`}
            entry={entry}
            canInvite={available}
            busy={inviteBusyKey === `guest:${entry.id}`}
            disabled={!invitationRuntimeReady || inviteBusyKey !== ''}
            showDebugDetails={showDebugDetails}
            onInviteRareGuest={onInviteRareGuest}
          />
        ))}
        {entries.length === 0 && (
          <EmptyRow
            text={getInvitationSectionEmptyText({
              kind,
              isListBusy,
              inviteScope,
              normalizedSearch,
              totalAfterLevelFilter,
            })}
          />
        )}
      </div>
    </section>
  );
}

function InvitationCandidateRow({
  entry,
  canInvite,
  busy,
  disabled,
  showDebugDetails,
  onInviteRareGuest,
}: {
  entry: RareGuestInvitationEntry;
  canInvite: boolean;
  busy: boolean;
  disabled: boolean;
  showDebugDetails: boolean;
  onInviteRareGuest: (guestId: number) => void;
}) {
  const sceneText = formatInvitationScenes(entry);

  return (
    <div
      className="grid min-w-0 gap-3 steward-background-surface-45 px-3 py-2.5 min-[520px]:grid-cols-[minmax(0,1fr)_auto] min-[520px]:items-center"
      data-gamepad-row={canInvite ? 'true' : undefined}
      data-gamepad-row-key={canInvite ? `rare-invitation:${entry.id}:${entry.runtimeName || entry.name}` : undefined}
      data-rare-invitation-candidate={entry.id}
      data-rare-invitation-candidate-status={canInvite ? 'available' : 'unavailable'}
    >
      <div className="min-w-0 space-y-1">
        <div className="flex min-w-0 flex-wrap items-center gap-1.5">
          <span className="min-w-0 break-words text-sm font-medium">{getInvitationEntryName(entry)}</span>
          <Badge variant={canInvite ? 'secondary' : 'outline'}>
            {canInvite ? '可邀请' : formatInvitationStatus(entry)}
          </Badge>
          {typeof entry.kizunaLevel === 'number' && Number.isFinite(entry.kizunaLevel) && (
            <Badge variant="outline">羁绊 {entry.kizunaLevel}</Badge>
          )}
          {entry.isCurrentScene && <Badge variant="secondary">当前场景</Badge>}
        </div>
        {sceneText && (
          <div className="min-w-0 break-words text-xs text-muted-foreground">
            出现地点：
            <span className="text-foreground">{sceneText}</span>
          </div>
        )}
        {entry.reason && (
          <div className="min-w-0 break-words text-xs text-muted-foreground">{entry.reason}</div>
        )}
        {showDebugDetails && (
          <div className="break-all font-mono text-xs text-muted-foreground">
            id=
            {entry.id}
            ; runtimeName=
            {entry.runtimeName || '<none>'}
            ; status=
            {entry.status || '<none>'}
          </div>
        )}
      </div>
      {canInvite && (
        <Button
          type="button"
          size="sm"
          className="w-full min-[520px]:w-auto"
          onClick={() => onInviteRareGuest(entry.id)}
          disabled={disabled}
          data-gamepad-clickable="true"
          data-gamepad-focus-key={`rare-invitations:guest:${entry.id}`}
        >
          {busy ? '邀请中...' : '邀请'}
        </Button>
      )}
    </div>
  );
}

function getInvitationSectionEmptyText({
  kind,
  isListBusy,
  inviteScope,
  normalizedSearch,
  totalAfterLevelFilter,
}: {
  kind: 'available' | 'unavailable';
  isListBusy: boolean;
  inviteScope: RareGuestInvitationScope;
  normalizedSearch: string;
  totalAfterLevelFilter: number;
}): string {
  if (isListBusy) return '正在读取稀客候选。';
  if (normalizedSearch && totalAfterLevelFilter > 0) {
    return kind === 'available' ? '没有匹配的可邀请稀客。' : '没有匹配的暂不可邀请稀客。';
  }
  if (kind === 'unavailable') return '当前筛选下没有暂不可邀请的稀客。';
  if (inviteScope === 'all') return '当前筛选下没有可邀请稀客。';
  return '当前场景与筛选条件下没有可邀请稀客。';
}

function canInviteEntry(entry: RareGuestInvitationEntry, availableIds: ReadonlySet<number>): boolean {
  return entry.canInvite ?? availableIds.has(entry.id);
}

function compareInvitationEntries(left: RareGuestInvitationEntry, right: RareGuestInvitationEntry): number {
  const leftCurrent = left.isCurrentScene ? 1 : 0;
  const rightCurrent = right.isCurrentScene ? 1 : 0;
  if (leftCurrent !== rightCurrent) return rightCurrent - leftCurrent;

  const sceneCompare = formatInvitationScenes(left).localeCompare(formatInvitationScenes(right), 'zh-Hans-CN');
  if (sceneCompare !== 0) return sceneCompare;

  const levelCompare = normalizeInvitationKizunaLevel(left) - normalizeInvitationKizunaLevel(right);
  if (levelCompare !== 0) return levelCompare;

  return getInvitationEntryName(left).localeCompare(getInvitationEntryName(right), 'zh-Hans-CN');
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
  return typeof entry.kizunaLevel === 'number' && Number.isFinite(entry.kizunaLevel)
    ? entry.kizunaLevel
    : 999;
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

function getInvitationEntryName(entry: RareGuestInvitationEntry): string {
  return entry.name || entry.runtimeName || `#${entry.id}`;
}

function formatInvitationScenes(entry: RareGuestInvitationEntry): string {
  return Array.from(new Set(
    (entry.sceneNames?.length ? entry.sceneNames : entry.sceneLabels ?? []).filter(Boolean),
  )).join(' / ');
}

function formatInvitationStatus(entry: RareGuestInvitationEntry): string {
  if (entry.status === 'low-kizuna') return '羁绊不足';
  if (entry.status === 'unavailable') return '当前不可见';
  if (entry.status === 'missing-dialog') return '无邀请对话';
  return '暂不可邀请';
}
