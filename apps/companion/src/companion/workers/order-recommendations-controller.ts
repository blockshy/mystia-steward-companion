import { buildRecommendationDataSignature, type RecommendationDataSet } from '@/lib/recommendation-data';
import type {
  OrderRecommendationResult,
  OrderRecommendationWorkerPayload,
  OrderRecommendationWorkerRequest,
  OrderRecommendationWorkerResponse,
} from '@/companion/workers/order-recommendations.types';

export interface AsyncOrderRecommendationResult extends OrderRecommendationResult {
  pending: boolean;
  isCurrent: boolean;
  sourceSignature: string;
  resultContextSignature: string;
  successRevision: number;
  retainedAfterError: boolean;
  error: string | null;
}

export interface OrderRecommendationInput {
  payload: OrderRecommendationWorkerPayload;
  sourceSignature: string;
  contextSignature: string;
}

export interface OrderRecommendationTransport {
  postMessage(request: OrderRecommendationWorkerRequest): void;
  terminate(): void;
  onmessage: ((event: MessageEvent<OrderRecommendationWorkerResponse>) => void) | null;
  onerror: ((event: ErrorEvent) => void) | null;
  onmessageerror: ((event: MessageEvent) => void) | null;
}

const EMPTY_RESULT: OrderRecommendationResult = {
  recommendations: [], recommendationIssues: [], normalOrderDetailPlans: [], normalExecutionTargets: [],
};

function emptyState(successRevision = 0): AsyncOrderRecommendationResult {
  return {
    ...EMPTY_RESULT, pending: false, isCurrent: false, sourceSignature: '', resultContextSignature: '',
    successRevision, retainedAfterError: false, error: null,
  };
}

interface ActiveRequest {
  requestId: number;
  input: OrderRecommendationInput;
  dataSignature: string;
  includedData: boolean;
}

/** Owns one transport, its acknowledged catalog, and at most one latest queued input. */
export class OrderRecommendationController {
  private state = emptyState();
  private lastSuccess: AsyncOrderRecommendationResult | null = null;
  private readonly listeners = new Set<() => void>();
  private worker: OrderRecommendationTransport | null = null;
  private latest: OrderRecommendationInput | null = null;
  private active: ActiveRequest | null = null;
  private queued: OrderRecommendationInput | null = null;
  private sequence = 0;
  private postedDataSignature = '';
  private catalog: RecommendationDataSet | null = null;
  private catalogSignature = '';
  private fatal = false;
  private readonly createWorker: () => OrderRecommendationTransport;

  constructor(createWorker: () => OrderRecommendationTransport) {
    this.createWorker = createWorker;
  }

  getSnapshot = (): AsyncOrderRecommendationResult => this.state;
  matchesLatestPayload(payload: OrderRecommendationWorkerPayload): boolean {
    return this.latest?.payload === payload;
  }
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => this.listeners.delete(listener);
  };

  update(input: OrderRecommendationInput | null): void {
    if (!input || !hasOrderRecommendationWork(input.payload)) {
      this.latest = null;
      this.lastSuccess = null;
      this.disposeTransport();
      this.fatal = false;
      this.publish(emptyState(this.state.successRevision));
      return;
    }
    const previous = this.latest;
    if (previous?.payload === input.payload
      && previous.sourceSignature === input.sourceSignature
      && previous.contextSignature === input.contextSignature) return;
    this.latest = input;
    if (this.fatal) {
      this.fail(input, this.state.error ?? '后台推荐计算失败。');
      return;
    }
    if (!this.ensureWorker()) return;
    if (this.active) this.queued = input;
    else this.post(input);
    if (this.active) this.publish({ ...this.state, pending: true, isCurrent: false, retainedAfterError: false, error: null });
  }

  retry = (): void => {
    const input = this.latest;
    if (!input) return;
    this.disposeTransport();
    this.fatal = false;
    if (!this.ensureWorker()) return;
    this.post(input);
    if (this.active) this.publish({ ...this.state, pending: true, isCurrent: false, retainedAfterError: false, error: null });
  };

  dispose(): void {
    this.latest = null;
    this.fatal = false;
    this.disposeTransport();
  }

  private ensureWorker(): boolean {
    if (this.worker) return true;
    try {
      const worker = this.createWorker();
      this.worker = worker;
      worker.onmessage = (event) => {
        if (this.worker === worker) this.receive(event.data);
      };
      worker.onerror = (event) => {
        if (this.worker === worker) this.failTransport(event.message || '后台推荐计算失败。');
      };
      worker.onmessageerror = () => {
        if (this.worker === worker) this.failTransport('后台推荐结果无法读取，请重新计算。');
      };
      return true;
    } catch (error) {
      this.failTransport(`无法启动后台推荐计算：${errorMessage(error)}`);
      return false;
    }
  }

  private post(input: OrderRecommendationInput, forceData = false): void {
    const worker = this.worker;
    if (!worker) return;
    const { data, ...runtimePayload } = input.payload;
    if (this.catalog !== data) {
      this.catalog = data;
      this.catalogSignature = buildRecommendationDataSignature(data);
    }
    const dataSignature = this.catalogSignature;
    // Resolve against what this transport has acknowledged at send time, never at queue time.
    const includedData = forceData || this.postedDataSignature !== dataSignature;
    const requestId = ++this.sequence;
    this.active = { requestId, input, dataSignature, includedData };
    try {
      worker.postMessage({
        requestId, sourceSignature: input.sourceSignature, contextSignature: input.contextSignature,
        payload: { ...runtimePayload, dataSignature, ...(includedData ? { data } : {}) },
      });
    } catch (error) {
      this.active = null;
      this.postedDataSignature = '';
      this.fail(input, errorMessage(error));
    }
  }

  private receive(response: OrderRecommendationWorkerResponse): void {
    const active = this.active;
    if (!active || response.requestId !== active.requestId) return;
    this.active = null;
    const queued = this.queued;
    this.queued = null;

    if (!response.ok) {
      this.postedDataSignature = '';
      if (response.code === 'data-cache-miss' && !active.includedData) {
        this.post(queued ?? this.latest ?? active.input, true);
        if (this.active) this.publish({ ...this.state, pending: true, isCurrent: false, retainedAfterError: false, error: null });
        return;
      }
      this.fail(active.input, response.error);
    } else {
      this.postedDataSignature = active.dataSignature;
      // Even identical choices can carry changed budgets, order permissions, or task identities.
      const state: AsyncOrderRecommendationResult = {
        ...response.result, pending: queued !== null, isCurrent: queued === null && this.latest === active.input,
        sourceSignature: active.input.sourceSignature, resultContextSignature: active.input.contextSignature,
        successRevision: this.state.successRevision + 1, retainedAfterError: false, error: null,
      };
      this.lastSuccess = state;
      this.publish(state);
    }
    if (queued) {
      this.post(queued);
      if (this.active) this.publish({ ...this.state, pending: true, isCurrent: false, retainedAfterError: false, error: null });
    }
  }

  private fail(input: OrderRecommendationInput, message: string): void {
    const retained = this.lastSuccess?.resultContextSignature === input.contextSignature ? this.lastSuccess : null;
    this.publish({
      ...(retained ?? {
        ...EMPTY_RESULT, recommendationIssues: input.payload.orders.map((order) => ({ order, message })),
        sourceSignature: input.sourceSignature, resultContextSignature: input.contextSignature,
        successRevision: this.state.successRevision,
      }),
      pending: false, isCurrent: false, retainedAfterError: retained !== null, error: message,
    });
  }

  private failTransport(message: string): void {
    this.disposeTransport();
    this.fatal = true;
    if (this.latest) this.fail(this.latest, message);
  }

  private disposeTransport(): void {
    const worker = this.worker;
    this.worker = null;
    this.active = null;
    this.queued = null;
    this.postedDataSignature = '';
    if (worker) {
      worker.onmessage = null;
      worker.onerror = null;
      worker.onmessageerror = null;
      worker.terminate();
    }
  }

  private publish(state: AsyncOrderRecommendationResult): void {
    this.state = state;
    for (const listener of this.listeners) listener();
  }
}

export function hasOrderRecommendationWork(payload: OrderRecommendationWorkerPayload): boolean {
  return payload.orders.length > 0
    || (payload.includeNormalOrderDetails === true && (payload.normalOrders?.length ?? 0) > 0)
    || (payload.includeNormalExecutionTargets === true && (payload.normalOrders?.length ?? 0) > 0);
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
