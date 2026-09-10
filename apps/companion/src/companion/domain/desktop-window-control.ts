export interface MousePassthroughHotkeyStatus {
  revision: number;
  status: 'registering' | 'available' | 'unavailable' | 'unsupported';
  errorCode: number | null;
}

export interface DesktopWindowPreferences {
  focusSwitchBehavior: 'hide' | 'keep-visible';
  alwaysOnTop: boolean;
  focusSwitchCooldownMs: number;
}

export interface DesktopWindowSnapshot {
  ready: boolean;
  busy: boolean;
  error: string | null;
  hotkeyStatus: MousePassthroughHotkeyStatus | null;
  mousePassthroughEnabled: boolean;
}

export interface DesktopWindowTransport {
  invoke<T>(command: string, args?: Record<string, unknown>): Promise<T>;
  listen<T>(event: string, callback: (payload: T) => void): Promise<() => void>;
}

const INITIAL: DesktopWindowSnapshot = {
  ready: false, busy: false, error: null, hotkeyStatus: null, mousePassthroughEnabled: false,
};

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function parseHotkeyStatus(value: unknown): MousePassthroughHotkeyStatus {
  if (!value || typeof value !== 'object') throw new Error('桌面热键状态格式错误。');
  const state = value as Record<string, unknown>;
  if (!Number.isSafeInteger(state.revision) || (state.revision as number) < 0
    || typeof state.status !== 'string'
    || !['registering', 'available', 'unavailable', 'unsupported'].includes(state.status)
    || !(state.errorCode === null || (Number.isInteger(state.errorCode)
      && (state.errorCode as number) >= 0 && (state.errorCode as number) <= 0xffffffff))) {
    throw new Error('桌面热键状态格式错误。');
  }
  return state as unknown as MousePassthroughHotkeyStatus;
}

/** Owns native observations and serializes preferences across effect cleanup/restart. */
export class DesktopWindowController {
  private state = INITIAL;
  private listeners = new Set<() => void>();
  private generation = 0;
  private active = false;
  private unlisten: Array<() => void> = [];
  private initialized: Promise<void> = Promise.resolve();
  private preferenceTail: Promise<void> = Promise.resolve();
  private preferenceRevision = 0;
  private mouseEvents = 0;
  private mouseBusy = false;
  private preferenceBusy = false;
  private initializationError: string | null = null;
  private preferenceError: string | null = null;
  private mouseError: string | null = null;
  private readonly getTransport: () => Promise<DesktopWindowTransport>;

  constructor(getTransport: () => Promise<DesktopWindowTransport>) { this.getTransport = getTransport; }

  getSnapshot = (): DesktopWindowSnapshot => this.state;
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => { this.listeners.delete(listener); };
  };

  private publish(patch: Partial<DesktopWindowSnapshot> = {}) {
    this.state = {
      ...this.state, ...patch,
      busy: this.mouseBusy || this.preferenceBusy,
      error: [this.initializationError, this.preferenceError, this.mouseError].filter(Boolean).join('\n') || null,
    };
    this.listeners.forEach((listener) => listener());
  }

  start(): void {
    this.stop();
    this.active = true;
    const generation = this.generation;
    const current = () => this.active && this.generation === generation;
    this.state = INITIAL;
    this.initializationError = null;
    this.preferenceError = null;
    this.mouseError = null;
    this.mouseEvents = 0;
    this.publish();
    this.initialized = (async () => {
      try {
        const transport = await this.getTransport();
        if (!current()) return;
        const subscriptions = await Promise.allSettled([
          transport.listen<unknown>('mouse-passthrough-changed', (value) => {
            if (!current()) return;
            if (typeof value !== 'boolean') {
              this.initializationError = '桌面鼠标穿透状态格式错误。';
              this.publish({ ready: false });
              return;
            }
            this.mouseEvents += 1;
            this.publish({ mousePassthroughEnabled: value });
          }),
          transport.listen<unknown>('mouse-passthrough-hotkey-status-changed', (value) => {
            if (!current()) return;
            try { this.observeHotkey(value); }
            catch (error) { this.initializationError = errorText(error); this.publish({ ready: false }); }
          }),
        ]);
        for (const result of subscriptions) {
          if (result.status === 'fulfilled') {
            if (current()) this.unlisten.push(result.value);
            else result.value();
          }
        }
        if (!current()) return;
        const failure = subscriptions.find((result) => result.status === 'rejected');
        if (failure?.status === 'rejected') throw failure.reason;
        const eventCount = this.mouseEvents;
        const results = await Promise.allSettled([
          transport.invoke<unknown>('get_mouse_passthrough'),
          transport.invoke<unknown>('get_mouse_passthrough_hotkey_status'),
        ]);
        if (!current()) return;
        const [mouse, hotkey] = results;
        if (mouse.status === 'fulfilled') {
          if (typeof mouse.value !== 'boolean') throw new Error('桌面鼠标穿透状态格式错误。');
          if (this.mouseEvents === eventCount) this.publish({ mousePassthroughEnabled: mouse.value });
        }
        if (hotkey.status === 'fulfilled') this.observeHotkey(hotkey.value);
        const errors = results.filter((result) => result.status === 'rejected');
        if (errors.length > 0) throw new Error(errors.map((result) => errorText(result.reason)).join('\n'));
        if (!this.initializationError) this.publish({ ready: true });
      } catch (error) {
        if (!current()) return;
        this.initializationError = `读取桌面窗口状态失败：${errorText(error)}`;
        this.publish({ ready: false });
      }
    })();
  }

  stop(): void {
    this.active = false;
    this.generation += 1;
    this.unlisten.splice(0).forEach((unlisten) => unlisten());
    this.publish({ ready: false });
  }

  private observeHotkey(value: unknown) {
    const next = parseHotkeyStatus(value);
    if (!this.state.hotkeyStatus || next.revision > this.state.hotkeyStatus.revision) {
      this.publish({ hotkeyStatus: next });
    }
  }

  applyPreferences(preferences: DesktopWindowPreferences): void {
    const generation = this.generation;
    const revision = ++this.preferenceRevision;
    const current = () => this.active && generation === this.generation && revision === this.preferenceRevision;
    this.preferenceBusy = true;
    this.publish();
    this.preferenceTail = this.preferenceTail.then(async () => {
      await this.initialized;
      if (!current()) return;
      if (!this.state.ready) {
        this.preferenceBusy = false;
        this.publish();
        return;
      }
      try {
        const transport = await this.getTransport();
        if (!current()) return;
        await transport.invoke('apply_companion_preferences', {
          keepVisibleWhenFocused: preferences.focusSwitchBehavior === 'keep-visible',
          alwaysOnTop: preferences.alwaysOnTop,
          windowSwitchCooldownMs: preferences.focusSwitchCooldownMs,
        });
        if (current()) this.preferenceError = null;
      } catch (error) {
        if (current()) this.preferenceError = `应用桌面窗口设置失败：${errorText(error)}`;
      } finally {
        if (current()) { this.preferenceBusy = false; this.publish(); }
      }
    });
  }

  setMousePassthrough = async (enabled: boolean): Promise<void> => {
    if (!this.active || !this.state.ready || this.mouseBusy) return;
    const generation = this.generation;
    const current = () => this.active && generation === this.generation;
    const eventCount = this.mouseEvents;
    this.mouseBusy = true;
    this.mouseError = null;
    this.publish();
    try {
      const transport = await this.getTransport();
      if (!current()) return;
      const actual = await transport.invoke<unknown>('set_mouse_passthrough', { enabled });
      if (!current()) return;
      if (typeof actual !== 'boolean') throw new Error('桌面鼠标穿透响应格式错误。');
      if (this.mouseEvents === eventCount) this.publish({ mousePassthroughEnabled: actual });
    } catch (error) {
      if (current()) this.mouseError = `应用鼠标穿透失败：${errorText(error)}`;
    } finally {
      this.mouseBusy = false;
      if (this.active) this.publish();
    }
  };
}
