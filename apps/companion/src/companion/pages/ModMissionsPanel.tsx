import { IconRefresh } from '@tabler/icons-react';
import { useMemo, useState } from 'react';

import {
  Badge,
  Button,
  EmptyRow,
  InfoLine,
  ListPanel,
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from '@/components/ui-kit';
import type {
  AvailableMissionEntry,
  AvailableMissionsResponse,
  MissionPresentationMetadata,
  MissionPanelView,
  TrackedMissionEntry,
  TrackedMissionsResponse,
} from '@/companion/types';
import {
  ModRareGuestInvitationsPanel,
  type ModRareGuestInvitationsPanelProps,
} from '@/companion/pages/ModRareGuestInvitationsPanel';
import {
  DENSE_THREE_COLUMN_GRID,
  INNER_TAB_TRIGGER_CLASS,
} from '@/companion/pages/shared-constants';

type MissionStatusView = 'all' | 'available' | TrackedMissionEntry['status'];
type MissionListEntry =
  | {
    kind: 'available';
    label: string;
    title: string;
    status: 'available';
    mission: AvailableMissionEntry;
  }
  | {
    kind: 'tracked';
    label: string;
    title: string;
    status: TrackedMissionEntry['status'];
    mission: TrackedMissionEntry;
  };

const MISSION_STATUS_VIEWS = [
  { value: 'all', label: '全部' },
  { value: 'available', label: '可接取' },
  { value: 'fulfilled', label: '可完成' },
  { value: 'tracking', label: '进行中' },
  { value: 'unverified', label: '待确认' },
] as const satisfies ReadonlyArray<{ value: MissionStatusView; label: string }>;

interface ModMissionsPanelProps extends ModRareGuestInvitationsPanelProps {
  view: MissionPanelView;
  connected: boolean;
  availableContextReady: boolean;
  availableMissions: AvailableMissionsResponse | null;
  availableMissionsError: string;
  availableMissionsLoading: boolean;
  trackedMissions: TrackedMissionsResponse | null;
  trackedMissionsError: string;
  trackedMissionsLoading: boolean;
  onViewChange: (view: MissionPanelView) => void;
  onRefreshMissions: () => void;
}

export function ModMissionsPanel({
  view,
  connected,
  availableContextReady,
  availableMissions,
  availableMissionsError,
  availableMissionsLoading,
  trackedMissions,
  trackedMissionsError,
  trackedMissionsLoading,
  onViewChange,
  onRefreshMissions,
  ...invitationProps
}: ModMissionsPanelProps) {
  return (
    <Tabs
      value={view}
      onValueChange={(value) => {
        if (value === 'tasks' || value === 'invitations') onViewChange(value);
      }}
      className="space-y-4"
    >
      <TabsList className="grid h-9 w-full grid-cols-2">
        <TabsTrigger value="tasks" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          任务列表
        </TabsTrigger>
        <TabsTrigger value="invitations" className={INNER_TAB_TRIGGER_CLASS} data-gamepad-clickable="true">
          稀客邀请
        </TabsTrigger>
      </TabsList>

      <TabsContent value="tasks" className="space-y-4">
        {view === 'tasks' && (
          <MissionListPanel
            connected={connected}
            availableContextReady={availableContextReady}
            availableResult={availableMissions}
            availableError={availableMissionsError}
            availableLoading={availableMissionsLoading}
            trackedResult={trackedMissions}
            trackedError={trackedMissionsError}
            trackedLoading={trackedMissionsLoading}
            showDebugDetails={invitationProps.showDebugDetails}
            onRefresh={onRefreshMissions}
          />
        )}
      </TabsContent>

      <TabsContent value="invitations" className="space-y-4">
        {view === 'invitations' && (
          <ModRareGuestInvitationsPanel {...invitationProps} />
        )}
      </TabsContent>
    </Tabs>
  );
}

function MissionListPanel({
  connected,
  availableContextReady,
  availableResult,
  availableError,
  availableLoading,
  trackedResult,
  trackedError,
  trackedLoading,
  showDebugDetails,
  onRefresh,
}: {
  connected: boolean;
  availableContextReady: boolean;
  availableResult: AvailableMissionsResponse | null;
  availableError: string;
  availableLoading: boolean;
  trackedResult: TrackedMissionsResponse | null;
  trackedError: string;
  trackedLoading: boolean;
  showDebugDetails: boolean;
  onRefresh: () => void;
}) {
  const [statusView, setStatusView] = useState<MissionStatusView>('all');
  const missions = useMemo(
    () => mergeMissionEntries(availableResult?.missions ?? [], trackedResult?.missions ?? []),
    [availableResult, trackedResult],
  );
  const statusViews = useMemo(
    () => MISSION_STATUS_VIEWS.map((status) => ({
      ...status,
      missions: status.value === 'all'
        ? missions
        : missions.filter((mission) => mission.status === status.value),
    })),
    [missions],
  );
  const activeStatusView = statusViews.find((status) => status.value === statusView)
    ?? statusViews[0];
  const loading = availableLoading || trackedLoading;
  const hasResult = availableResult !== null || trackedResult !== null;
  const showAvailableStatus = statusView === 'all' || statusView === 'available';
  const showTrackedStatus = statusView !== 'available';
  const canShowEmptyState = statusView === 'all'
    ? availableResult !== null && trackedResult !== null
    : statusView === 'available'
      ? availableResult !== null
      : trackedResult !== null;

  return (
    <ListPanel
      title={`任务列表 (${missions.length})`}
      action={(
        <Button
          type="button"
          size="sm"
          className="h-8 px-2.5"
          onClick={onRefresh}
          disabled={!connected || loading}
          data-gamepad-clickable="true"
          data-gamepad-focus-key="missions:tasks:refresh"
        >
          <IconRefresh className={loading ? 'size-4 animate-spin' : 'size-4'} />
          刷新
        </Button>
      )}
      gamepadScrollKey="missions:tasks"
      gamepadScrollLabel="任务列表"
    >
      <div className="grid min-w-0 gap-3 text-sm">
        {showDebugDetails && (availableResult || trackedResult) && (
          <div className={DENSE_THREE_COLUMN_GRID}>
            <InfoLine
              label="任务代际"
              value={availableResult?.missionGeneration ?? trackedResult?.generation ?? 0}
              mono
            />
            <InfoLine
              label="日间代际"
              value={availableResult?.daySceneGeneration ?? 0}
              mono
            />
            <InfoLine
              label="读取状态"
              value={`available=${availableResult?.status ?? 'none'}; tracked=${trackedResult?.status ?? 'none'}`}
              mono
            />
          </div>
        )}

        {!connected && <EmptyRow text="等待 Mod 本地 API 连接。" />}
        {connected && loading && !hasResult && <EmptyRow text="正在读取任务列表。" />}
        {connected && !availableContextReady && showAvailableStatus && (
          <EmptyRow text="进入日间场景并完成存档初始化后可读取可接取任务。" />
        )}
        {connected && showAvailableStatus && availableError && (
          <EmptyRow text={`可接取任务：${availableError}`} />
        )}
        {connected && showTrackedStatus && trackedError && (
          <EmptyRow text={`已追踪任务：${trackedError}`} />
        )}
        {connected && hasResult && (
          <Tabs
            value={statusView}
            onValueChange={(value) => {
              if (isMissionStatusView(value)) setStatusView(value);
            }}
            className="min-w-0 gap-3"
            data-mission-status-tabs="true"
          >
            <TabsList
              scrollable
              aria-label="任务状态筛选"
              className="h-9 w-full max-w-full flex-nowrap justify-start"
            >
              {statusViews.map((status) => (
                <TabsTrigger
                  key={status.value}
                  value={status.value}
                  className="min-w-[5rem] flex-none px-2.5 min-[720px]:min-w-0 min-[720px]:flex-1"
                  data-gamepad-clickable="true"
                  data-gamepad-focus-key={`missions:tasks:status:${status.value}`}
                  data-mission-status-tab={status.value}
                >
                  <span>{status.label}</span>
                  <span
                    className="ml-1 tabular-nums text-xs text-muted-foreground"
                    data-mission-status-tab-count={status.value}
                  >
                    {status.missions.length}
                  </span>
                </TabsTrigger>
              ))}
            </TabsList>

            <TabsContent
              value={activeStatusView.value}
              className="min-w-0"
              data-mission-status-list={activeStatusView.value}
            >
              <div className="min-w-0 px-2">
                {activeStatusView.missions.map((mission) => (
                  <MissionRow
                    key={mission.label}
                    entry={mission}
                    showDebugDetails={showDebugDetails}
                  />
                ))}
                {activeStatusView.missions.length === 0
                  && canShowEmptyState
                  && !(activeStatusView.value === 'available' && !availableContextReady)
                  && (
                    <EmptyRow text={getMissionEmptyText(activeStatusView.value)} />
                  )}
              </div>
            </TabsContent>
          </Tabs>
        )}
      </div>
    </ListPanel>
  );
}

function MissionRow({
  entry,
  showDebugDetails,
}: {
  entry: MissionListEntry;
  showDebugDetails: boolean;
}) {
  if (entry.kind === 'available') {
    return (
      <div
        className="min-w-0 border-b py-2.5 text-sm last:border-b-0"
        data-gamepad-row="true"
        data-gamepad-row-key={`mission:${entry.label}`}
        data-mission-status="available"
      >
        <div className="flex min-w-0 flex-wrap items-start justify-between gap-x-3 gap-y-1">
          <span className="min-w-0 break-words font-medium">{entry.title}</span>
          <Badge variant="outline" className="shrink-0">可接取</Badge>
        </div>
        <MissionPresentationDetails
          mission={entry.mission}
          missionLabel={entry.label}
          showDebugDetails={showDebugDetails}
        />
      </div>
    );
  }

  return (
    <TrackedMissionRow
      mission={entry.mission}
      showDebugDetails={showDebugDetails}
    />
  );
}

function TrackedMissionRow({
  mission,
  showDebugDetails,
}: {
  mission: TrackedMissionEntry;
  showDebugDetails: boolean;
}) {
  const verified = mission.status !== 'unverified';
  const completedCount = mission.completedConditionCount ?? 0;
  const progressText = verified
    ? mission.conditionCount > 0
      ? `${completedCount}/${mission.conditionCount} 项条件已完成`
      : '任务状态已验证'
    : '任务进度尚未完成原生校验';

  return (
    <div
      className="min-w-0 border-b py-2.5 text-sm last:border-b-0"
      data-gamepad-row="true"
      data-gamepad-row-key={`mission:${mission.label}`}
      data-mission-status={mission.status}
    >
      <div className="flex min-w-0 flex-wrap items-start justify-between gap-x-3 gap-y-1">
        <span className="min-w-0 break-words font-medium">{mission.title}</span>
        <Badge variant={getTrackedMissionBadgeVariant(mission.status)} className="shrink-0">
          {getTrackedMissionStatusLabel(mission.status)}
        </Badge>
      </div>
      <div className="mt-1 text-xs text-muted-foreground">{progressText}</div>
      <MissionPresentationDetails
        mission={mission}
        missionLabel={mission.label}
        showDebugDetails={showDebugDetails}
      />
      {verified && mission.conditionCount > 0 && (
        <div
          className="mt-2 flex min-w-0 flex-wrap gap-1"
          aria-label={`${mission.title}：${progressText}`}
        >
          {mission.conditionStates.map((completed, index) => (
            <span
              key={`${mission.label}:condition:${index}`}
              className={completed
                ? 'size-2 border border-primary bg-primary'
                : 'size-2 border border-border bg-muted'}
              title={`条件 ${index + 1}：${completed ? '已完成' : '未完成'}`}
              aria-hidden="true"
            />
          ))}
        </div>
      )}
    </div>
  );
}

function MissionPresentationDetails({
  mission,
  missionLabel,
  showDebugDetails,
}: {
  mission: MissionPresentationMetadata;
  missionLabel: string;
  showDebugDetails: boolean;
}) {
  return (
    <>
      {mission.characterName && (
        <div
          className="mt-1 flex min-w-0 flex-wrap items-baseline gap-x-2 gap-y-0.5 text-xs text-muted-foreground"
          data-mission-character-name={mission.characterName}
        >
          <span className="shrink-0">任务角色</span>
          <span className="min-w-0 break-words text-foreground">{mission.characterName}</span>
        </div>
      )}
      {mission.sceneNames.length > 0 && (
        <div
          className="mt-1 flex min-w-0 flex-wrap items-start gap-x-2 gap-y-1 text-xs text-muted-foreground"
          data-mission-related-scenes="true"
        >
          <span className="shrink-0 pt-0.5">相关场景</span>
          <div className="flex min-w-0 flex-1 flex-wrap gap-1">
            {mission.sceneNames.map((sceneName) => (
              <span
                key={sceneName}
                className="inline-flex max-w-full items-center overflow-hidden border border-primary/30 bg-primary/10 px-1.5 py-0.5 text-left text-xs font-semibold leading-tight text-primary tracking-normal"
                data-mission-scene-name={sceneName}
              >
                <span className="min-w-0 break-words" data-mission-scene-label="true">
                  {sceneName}
                </span>
              </span>
            ))}
          </div>
        </div>
      )}
      {showDebugDetails && (
        <div
          className="mt-1 break-all font-mono text-xs text-muted-foreground"
          data-mission-presentation-debug="true"
        >
          {missionLabel};
          {' '}
          receiverLabel=
          {mission.receiverLabel || '<none>'}
          ; presentationStatus=
          {mission.presentationStatus}
        </div>
      )}
    </>
  );
}

function mergeMissionEntries(
  availableMissions: readonly AvailableMissionEntry[],
  trackedMissions: readonly TrackedMissionEntry[],
): MissionListEntry[] {
  const trackedLabels = new Set(trackedMissions.map((mission) => mission.label));
  const entries: MissionListEntry[] = [
    ...availableMissions
      .filter((mission) => !trackedLabels.has(mission.label))
      .map((mission): MissionListEntry => ({
        kind: 'available',
        label: mission.label,
        title: mission.title,
        status: 'available',
        mission,
      })),
    ...trackedMissions.map((mission): MissionListEntry => ({
      kind: 'tracked',
      label: mission.label,
      title: mission.title,
      status: mission.status,
      mission,
    })),
  ];
  const statusOrder: ReadonlyArray<MissionListEntry['status']> = [
    'available',
    'fulfilled',
    'tracking',
    'unverified',
  ];
  return entries.sort((left, right) => (
    statusOrder.indexOf(left.status) - statusOrder.indexOf(right.status)
    || left.title.localeCompare(right.title, 'zh-Hans-CN')
    || left.label.localeCompare(right.label, 'en')
  ));
}

function isMissionStatusView(value: string | null): value is MissionStatusView {
  return value !== null
    && MISSION_STATUS_VIEWS.some((candidate) => candidate.value === value);
}

function getTrackedMissionStatusLabel(status: TrackedMissionEntry['status']): string {
  switch (status) {
    case 'fulfilled':
      return '可完成';
    case 'tracking':
      return '进行中';
    case 'unverified':
      return '待确认';
  }
}

function getMissionEmptyText(status: MissionStatusView): string {
  switch (status) {
    case 'all':
      return '当前没有任务。';
    case 'available':
      return '当前没有可接取任务。';
    case 'fulfilled':
      return '当前没有可完成任务。';
    case 'tracking':
      return '当前没有进行中的任务。';
    case 'unverified':
      return '当前没有待确认任务。';
  }
}

function getTrackedMissionBadgeVariant(
  status: TrackedMissionEntry['status'],
): 'default' | 'secondary' | 'outline' {
  switch (status) {
    case 'fulfilled':
      return 'default';
    case 'tracking':
      return 'secondary';
    case 'unverified':
      return 'outline';
  }
}
