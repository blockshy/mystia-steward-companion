import { useCallback, useEffect, useRef, useState } from 'react';
import { publishGameUiTargets } from '@/companion/api';
import {
  reconcileGameUiTarget,
  type GameUiTargetSourceOrderState,
} from '@/companion/domain/game-ui-targets';
import type {
  GameUiTarget,
  GameUiTargetFeatureSlots,
  GameUiTargetFeatures,
  GameUiTargetKind,
  GameUiTargetSlots,
} from '@/companion/types';

const UI_TARGET_RETRY_DELAYS_MS = [750, 2000, 5000] as const;
const TARGET_KINDS: readonly GameUiTargetKind[] = ['rare', 'normal'];

export interface GameUiTargetLaneState {
  isCurrent: boolean;
  pending: boolean;
  error: boolean;
}

interface UseGameUiTargetPublisherOptions {
  endpoint: string;
  apiToken: string;
  connectionRevision: number;
  authorityRevision: number;
  sessionId: string;
  businessGeneration: number;
  businessActive: boolean;
  connectionReady: boolean;
  featureSlots: GameUiTargetFeatureSlots;
  targetSlots: GameUiTargetSlots;
  sourceOrders: readonly GameUiTargetSourceOrderState[];
  laneStates: Record<GameUiTargetKind, GameUiTargetLaneState>;
  colors: Record<GameUiTargetKind, string>;
  targetPolicySignature: string;
}

interface UiTargetPublication {
  signature: string;
  contextRevision: number;
  clearsPreviousContext: boolean;
  endpoint: string;
  apiToken: string;
  authorityRevision: number;
  businessGeneration: number;
  targetSlots: GameUiTargetSlots;
}

interface UiTargetPublisherState {
  active: boolean;
  activeAbortController: AbortController | null;
  contextClearPending: boolean;
  contextRevision: number;
  connectionKey: string;
  targetPolicySignature: string;
  desired: UiTargetPublication | null;
  disposed: boolean;
  failed: Record<GameUiTargetKind, boolean>;
  lastCurrentTargets: GameUiTargetSlots;
  lastSuccessfulSignature: string;
  retryAttempt: number;
  retryTimer: number | null;
}

export function useGameUiTargetPublisher({
  endpoint,
  apiToken,
  connectionRevision,
  authorityRevision,
  sessionId,
  businessGeneration,
  businessActive,
  connectionReady,
  featureSlots,
  targetSlots,
  sourceOrders,
  laneStates,
  colors,
  targetPolicySignature,
}: UseGameUiTargetPublisherOptions): void {
  const [contextClearRevision, setContextClearRevision] = useState(0);
  const stateRef = useRef<UiTargetPublisherState>({
    active: false,
    activeAbortController: null,
    contextClearPending: false,
    contextRevision: 0,
    connectionKey: '',
    targetPolicySignature: '',
    desired: null,
    disposed: false,
    failed: { rare: false, normal: false },
    lastCurrentTargets: { rare: null, normal: null },
    lastSuccessfulSignature: '',
    retryAttempt: 0,
    retryTimer: null,
  });

  const pump = useCallback(function publishLatestTargets(): void {
    const state = stateRef.current;
    if (state.disposed || state.active || state.retryTimer !== null) return;
    const publication = state.desired;
    if (!publication || publication.signature === state.lastSuccessfulSignature) return;

    const abortController = new AbortController();
    state.active = true;
    state.activeAbortController = abortController;
    void publishGameUiTargets(
      publication.endpoint,
      publication.apiToken,
      publication.businessGeneration,
      publication.targetSlots,
      publication.authorityRevision,
      abortController.signal,
    )
      .then(() => {
        if (state.disposed) return;
        state.lastSuccessfulSignature = publication.signature;
        state.retryAttempt = 0;
        if (publication.clearsPreviousContext
          && state.contextClearPending
          && state.contextRevision === publication.contextRevision
          && state.desired?.signature === publication.signature) {
          state.contextClearPending = false;
          setContextClearRevision((revision) => revision + 1);
        }
      })
      .catch(() => {
        if (state.disposed) return;
        if (state.desired?.signature !== publication.signature) {
          state.retryAttempt = 0;
          return;
        }
        const retryDelay = UI_TARGET_RETRY_DELAYS_MS[
          Math.min(state.retryAttempt, UI_TARGET_RETRY_DELAYS_MS.length - 1)
        ];
        state.retryAttempt += 1;
        state.retryTimer = window.setTimeout(() => {
          state.retryTimer = null;
          publishLatestTargets();
        }, retryDelay);
      })
      .finally(() => {
        state.active = false;
        state.activeAbortController = null;
        if (!state.disposed && state.retryTimer === null) publishLatestTargets();
      });
  }, []);

  useEffect(() => {
    const state = stateRef.current;
    state.disposed = false;
    return () => {
      state.disposed = true;
      state.desired = null;
      state.activeAbortController?.abort();
      if (state.retryTimer !== null) window.clearTimeout(state.retryTimer);
      state.retryTimer = null;
    };
  }, []);

  useEffect(() => {
    const state = stateRef.current;
    // A successful context-clear POST advances this value so current targets are
    // reconciled again even when no recommendation input changed in the meantime.
    void contextClearRevision;
    const connectionKey = [
      endpoint,
      apiToken,
      connectionRevision,
      authorityRevision,
      sessionId,
      businessGeneration,
    ].join('\n');
    const connectionChanged = state.connectionKey !== connectionKey;
    const targetPolicyChanged = state.targetPolicySignature !== targetPolicySignature;
    if (connectionChanged || targetPolicyChanged) {
      state.contextRevision += 1;
      state.contextClearPending = true;
      state.connectionKey = connectionKey;
      state.targetPolicySignature = targetPolicySignature;
      state.lastCurrentTargets = { rare: null, normal: null };
      state.failed = { rare: false, normal: false };
      state.lastSuccessfulSignature = connectionChanged ? '' : state.lastSuccessfulSignature;
      clearRetry(state);
    }

    if (!connectionReady || !businessActive || businessGeneration <= 0) {
      state.desired = null;
      state.lastCurrentTargets = { rare: null, normal: null };
      clearRetry(state);
      return;
    }

    const nextTargets: GameUiTargetSlots = { ...state.lastCurrentTargets };
    for (const kind of TARGET_KINDS) {
      const lane = laneStates[kind];
      if (lane.error) {
        state.failed[kind] = true;
      } else if (lane.isCurrent && !lane.pending) {
        state.failed[kind] = false;
      }

      const failed = state.failed[kind];
      const features = featureSlots[kind];
      const candidate = !hasAnyFeatureEnabled(features) || failed
        ? null
        : !state.contextClearPending && lane.isCurrent && !lane.pending
          ? targetSlots[kind]
          : nextTargets[kind];
      const reconciled = reconcileGameUiTarget(candidate, sourceOrders);
      nextTargets[kind] = reconciled && (
        reconciled.color !== colors[kind]
        || !hasSameFeatures(reconciled.features, features)
      )
        ? { ...reconciled, color: colors[kind], features: { ...features } }
        : reconciled;
    }
    state.lastCurrentTargets = nextTargets;

    const publicationSignature = [
      connectionKey,
      targetPolicySignature,
      state.contextRevision.toString(),
      serializeTarget(nextTargets.rare),
      serializeTarget(nextTargets.normal),
    ].join('\n');
    const previousDesiredSignature = state.desired?.signature ?? '';
    state.desired = {
      signature: publicationSignature,
      contextRevision: state.contextRevision,
      clearsPreviousContext: state.contextClearPending,
      endpoint,
      apiToken,
      authorityRevision,
      businessGeneration,
      targetSlots: nextTargets,
    };
    if (previousDesiredSignature !== publicationSignature) clearRetry(state);
    pump();
  }, [
    apiToken,
    authorityRevision,
    businessActive,
    businessGeneration,
    colors,
    connectionReady,
    connectionRevision,
    contextClearRevision,
    endpoint,
    featureSlots,
    laneStates,
    pump,
    sessionId,
    sourceOrders,
    targetPolicySignature,
    targetSlots,
  ]);
}

function serializeTarget(target: GameUiTarget | null): string {
  if (!target) return 'null';
  return JSON.stringify({
    kind: target.kind,
    features: target.features,
    targetRevision: target.targetRevision,
    color: target.color,
    traceId: target.traceId,
    orderKey: target.orderKey,
    orderLifecycleSequence: target.orderLifecycleSequence,
    recipeId: target.recipeId,
    ingredientIds: target.ingredientIds,
    extraIngredientIds: target.extraIngredientIds,
    beverageId: target.beverageId,
    cookerTypeId: target.cookerTypeId,
    deskCode: target.deskCode,
  });
}

function hasAnyFeatureEnabled(features: GameUiTargetFeatures): boolean {
  return features.listPinningEnabled
    || features.recipeVariantEnabled
    || features.cookerHighlightEnabled
    || features.seatHighlightEnabled
    || features.orderHighlightEnabled;
}

function hasSameFeatures(left: GameUiTargetFeatures, right: GameUiTargetFeatures): boolean {
  return left.listPinningEnabled === right.listPinningEnabled
    && left.recipeVariantEnabled === right.recipeVariantEnabled
    && left.cookerHighlightEnabled === right.cookerHighlightEnabled
    && left.seatHighlightEnabled === right.seatHighlightEnabled
    && left.orderHighlightEnabled === right.orderHighlightEnabled;
}

function clearRetry(state: UiTargetPublisherState): void {
  state.retryAttempt = 0;
  if (state.retryTimer !== null) window.clearTimeout(state.retryTimer);
  state.retryTimer = null;
}
