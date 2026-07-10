import { useCallback, useEffect, useRef } from 'react';
import { publishGameUiPinningTarget } from '@/companion/api';
import type { GameUiPinningTarget } from '@/companion/types';

const UI_PINNING_RETRY_DELAYS_MS = [750, 2000, 5000] as const;

interface UseGameUiPinningPublisherOptions {
  endpoint: string;
  apiToken: string;
  connectionRevision: number;
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
  pinningEnabled: boolean;
  cookerHighlightEnabled: boolean;
  target: GameUiPinningTarget | null;
}

interface UiPinningPublisherState {
  active: boolean;
  activeAbortController: AbortController | null;
  connectionEpoch: number;
  connectionKey: string;
  desired: UiPinningPublication | null;
  disposed: boolean;
  failedAtSuccessRevision: number | null;
  lastCurrentTarget: string | null;
  lastSuccessfulSignature: string;
  retryAttempt: number;
  retryTimer: number | null;
  wasConnectionReady: boolean;
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
    connectionEpoch: 0,
    connectionKey: '',
    desired: null,
    disposed: false,
    failedAtSuccessRevision: null,
    lastCurrentTarget: null,
    lastSuccessfulSignature: '',
    retryAttempt: 0,
    retryTimer: null,
    wasConnectionReady: false,
  });

  const serializedTarget = JSON.stringify(target);

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
    const connectionKey = `${endpoint}\n${apiToken}\n${connectionRevision}`;
    if (state.connectionKey !== connectionKey) {
      state.connectionKey = connectionKey;
      state.connectionEpoch += 1;
      state.lastCurrentTarget = null;
      state.lastSuccessfulSignature = '';
      state.retryAttempt = 0;
    }

    if (!connectionReady) {
      if (state.wasConnectionReady) state.connectionEpoch += 1;
      state.wasConnectionReady = false;
      state.desired = null;
      state.lastCurrentTarget = null;
      state.lastSuccessfulSignature = '';
      state.retryAttempt = 0;
      if (state.retryTimer !== null) {
        window.clearTimeout(state.retryTimer);
        state.retryTimer = null;
      }
      return;
    }
    state.wasConnectionReady = true;

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
      state.lastCurrentTarget = serializedTarget;
    }

    const publicationTarget = state.lastCurrentTarget === null
      ? null
      : JSON.parse(state.lastCurrentTarget) as GameUiPinningTarget;
    const publicationTargetSignature = state.lastCurrentTarget ?? 'null';
    const publicationSignature = [
      connectionKey,
      state.connectionEpoch,
      pinningEnabled ? '1' : '0',
      cookerHighlightEnabled ? '1' : '0',
      publicationTargetSignature,
    ].join('\n');
    const previousDesiredSignature = state.desired?.signature ?? '';
    state.desired = {
      signature: publicationSignature,
      endpoint,
      apiToken,
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
  ]);
}
