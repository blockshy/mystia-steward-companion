import { useCallback, useEffect, useRef } from 'react';
import { publishGameUiPinningTarget } from '@/companion/api';
import type { GameUiPinningTarget } from '@/companion/types';

const UI_PINNING_RETRY_DELAYS_MS = [750, 2000, 5000] as const;

interface UseGameUiPinningPublisherOptions {
  endpoint: string;
  apiToken: string;
  connectionRevision: number;
  sessionId: string;
  businessGeneration: number;
  businessActive: boolean;
  connectionReady: boolean;
  pinningEnabled: boolean;
  cookerHighlightEnabled: boolean;
  target: GameUiPinningTarget | null;
  recommendationIsCurrent: boolean;
  recommendationPending: boolean;
  recommendationError: boolean;
  recommendationSuccessRevision: number;
}

interface UiPinningPublication {
  signature: string;
  endpoint: string;
  apiToken: string;
  businessGeneration: number;
  pinningEnabled: boolean;
  cookerHighlightEnabled: boolean;
  target: GameUiPinningTarget | null;
}

interface UiPinningPublisherState {
  active: boolean;
  activeAbortController: AbortController | null;
  connectionKey: string;
  desired: UiPinningPublication | null;
  disposed: boolean;
  failedAtSuccessRevision: number | null;
  lastCurrentTarget: GameUiPinningTarget | null;
  lastSuccessfulSignature: string;
  retryAttempt: number;
  retryTimer: number | null;
}

/**
 * 将当前推荐目标发布到 Mod，并将去重边界限定在同一个连接实例内。
 *
 * Tauri 原生请求不能由前端 AbortSignal 取消，因此发布器使用单写者队列合并目标变化：任一时刻
 * 最多一个写请求，请求结束后只补发最新目标。失败使用固定的上限间隔持续重试。
 */
export function useGameUiPinningPublisher({
  endpoint,
  apiToken,
  connectionRevision,
  sessionId,
  businessGeneration,
  businessActive,
  connectionReady,
  pinningEnabled,
  cookerHighlightEnabled,
  target,
  recommendationIsCurrent,
  recommendationPending,
  recommendationError,
  recommendationSuccessRevision,
}: UseGameUiPinningPublisherOptions): void {
  const stateRef = useRef<UiPinningPublisherState>({
    active: false,
    activeAbortController: null,
    connectionKey: '',
    desired: null,
    disposed: false,
    failedAtSuccessRevision: null,
    lastCurrentTarget: null,
    lastSuccessfulSignature: '',
    retryAttempt: 0,
    retryTimer: null,
  });

  const serializedTarget = serializeGameUiPinningWireTarget(target);

  const pump = useCallback(function publishLatestTarget(): void {
    const state = stateRef.current;
    if (state.disposed || state.active || state.retryTimer !== null) return;

    const publication = state.desired;
    if (!publication || publication.signature === state.lastSuccessfulSignature) return;

    const abortController = new AbortController();
    state.active = true;
    state.activeAbortController = abortController;

    void publishGameUiPinningTarget(
      publication.endpoint,
      publication.apiToken,
      publication.businessGeneration,
      publication.pinningEnabled,
      publication.cookerHighlightEnabled,
      publication.target,
      abortController.signal,
    )
      .then(() => {
        state.lastSuccessfulSignature = publication.signature;
        state.retryAttempt = 0;
      })
      .catch(() => {
        if (state.disposed) return;
        if (state.desired?.signature !== publication.signature) {
          state.retryAttempt = 0;
          return;
        }

        const retryDelay = UI_PINNING_RETRY_DELAYS_MS[
          Math.min(state.retryAttempt, UI_PINNING_RETRY_DELAYS_MS.length - 1)
        ];
        state.retryAttempt += 1;
        state.retryTimer = window.setTimeout(() => {
          state.retryTimer = null;
          publishLatestTarget();
        }, retryDelay);
      })
      .finally(() => {
        state.active = false;
        state.activeAbortController = null;
        if (!state.disposed && state.retryTimer === null) publishLatestTarget();
      });
  }, []);

  useEffect(() => {
    const state = stateRef.current;
    state.disposed = false;
    return () => {
      state.disposed = true;
      state.desired = null;
      state.activeAbortController?.abort();
      if (state.retryTimer !== null) {
        window.clearTimeout(state.retryTimer);
        state.retryTimer = null;
      }
    };
  }, []);

  useEffect(() => {
    const state = stateRef.current;
    const connectionKey = `${endpoint}\n${apiToken}\n${connectionRevision}\n${sessionId}\n${businessGeneration}`;
    if (state.connectionKey !== connectionKey) {
      state.connectionKey = connectionKey;
      state.lastCurrentTarget = null;
      state.lastSuccessfulSignature = '';
      state.failedAtSuccessRevision = null;
      state.retryAttempt = 0;
      if (state.retryTimer !== null) {
        window.clearTimeout(state.retryTimer);
        state.retryTimer = null;
      }
    }

    if (!connectionReady || !businessActive || businessGeneration <= 0) {
      state.desired = null;
      state.lastCurrentTarget = null;
      state.activeAbortController?.abort();
      if (state.retryTimer !== null) {
        window.clearTimeout(state.retryTimer);
        state.retryTimer = null;
      }
      return;
    }

    if (recommendationError) {
      state.failedAtSuccessRevision = recommendationSuccessRevision;
    } else if (state.failedAtSuccessRevision !== null
      && recommendationSuccessRevision > state.failedAtSuccessRevision
      && recommendationIsCurrent
      && !recommendationPending) {
      state.failedAtSuccessRevision = null;
    }

    const featureEnabled = pinningEnabled || cookerHighlightEnabled;
    const recommendationFailed = recommendationError || state.failedAtSuccessRevision !== null;
    if (!featureEnabled || recommendationFailed) {
      state.lastCurrentTarget = null;
    } else if (recommendationIsCurrent && !recommendationPending) {
      state.lastCurrentTarget = deserializeGameUiPinningWireTarget(serializedTarget);
    }

    const publicationTarget = state.lastCurrentTarget;
    const publicationTargetSignature = serializeGameUiPinningWireTarget(publicationTarget);
    const publicationSignature = [
      connectionKey,
      pinningEnabled ? '1' : '0',
      cookerHighlightEnabled ? '1' : '0',
      publicationTargetSignature,
    ].join('\n');
    const previousDesiredSignature = state.desired?.signature ?? '';
    state.desired = {
      signature: publicationSignature,
      endpoint,
      apiToken,
      businessGeneration,
      pinningEnabled,
      cookerHighlightEnabled,
      target: publicationTarget,
    };

    if (previousDesiredSignature !== publicationSignature) {
      state.retryAttempt = 0;
      if (state.retryTimer !== null) {
        window.clearTimeout(state.retryTimer);
        state.retryTimer = null;
      }
    }
    pump();
  }, [
    apiToken,
    businessActive,
    businessGeneration,
    connectionReady,
    connectionRevision,
    cookerHighlightEnabled,
    endpoint,
    pinningEnabled,
    pump,
    recommendationError,
    recommendationIsCurrent,
    recommendationPending,
    recommendationSuccessRevision,
    serializedTarget,
    sessionId,
  ]);
}

function serializeGameUiPinningWireTarget(target: GameUiPinningTarget | null): string {
  if (!target) return 'null';
  return JSON.stringify({
    recipeId: target.recipeId,
    recipeName: target.recipeName,
    ingredientIds: target.ingredientIds,
    beverageId: target.beverageId,
    beverageName: target.beverageName,
    cookerTypeId: target.cookerTypeId,
    cookerName: target.cookerName,
  });
}

function deserializeGameUiPinningWireTarget(serialized: string): GameUiPinningTarget | null {
  if (serialized === 'null') return null;
  const target = JSON.parse(serialized) as Omit<GameUiPinningTarget, 'signature'>;
  return { signature: '', ...target };
}
