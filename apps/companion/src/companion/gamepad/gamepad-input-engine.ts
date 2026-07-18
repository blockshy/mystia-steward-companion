export const GAMEPAD_AXIS_PRESS_THRESHOLD = 0.65;
export const GAMEPAD_AXIS_RELEASE_THRESHOLD = 0.4;
export const GAMEPAD_BUTTON_PRESS_THRESHOLD = 0.5;
export const GAMEPAD_FIRST_REPEAT_DELAY_MS = 360;
export const GAMEPAD_REPEAT_DELAY_MS = 140;
export const GAMEPAD_NEUTRAL_DURATION_MS = 50;

const BUTTON_A = 0;
const BUTTON_B = 1;
const BUTTON_X = 2;
const BUTTON_Y = 3;
const BUTTON_LB = 4;
const BUTTON_RB = 5;
const BUTTON_LT = 6;
const BUTTON_RT = 7;
const BUTTON_RIGHT_STICK = 11;
const BUTTON_DPAD_UP = 12;
const BUTTON_DPAD_DOWN = 13;
const BUTTON_DPAD_LEFT = 14;
const BUTTON_DPAD_RIGHT = 15;

const MINIMUM_NEUTRAL_SAMPLE_COUNT = 2;
const STANDARD_MAPPING = 'standard';

export type GamepadDirection = 'up' | 'down' | 'left' | 'right';

export type GamepadNavigationAction =
  | GamepadDirection
  | 'confirm'
  | 'back'
  | 'favorite'
  | 'compact'
  | 'previousTab'
  | 'nextTab'
  | 'scrollUp'
  | 'scrollDown';

export type GamepadInputEventPhase = 'pressed' | 'repeat';

export type GamepadNeutralReason =
  | 'initial'
  | 'focus'
  | 'visibility'
  | 'reconnect'
  | 'enabled'
  | 'device-change'
  | 'manual';

export type GamepadInputStatus =
  | 'suspended'
  | 'waiting-for-gamepad'
  | 'unsupported-mapping'
  | 'awaiting-neutral'
  | 'ready';

export interface GamepadButtonLike {
  readonly pressed: boolean;
  readonly value: number;
}

export interface GamepadLike {
  readonly axes: readonly number[];
  readonly buttons: readonly (GamepadButtonLike | null | undefined)[];
  readonly connected: boolean;
  readonly id: string;
  readonly index: number;
  readonly mapping: string;
  readonly timestamp?: number;
}

export interface GamepadInputEvent {
  readonly action: GamepadNavigationAction;
  readonly gamepadIndex: number;
  readonly phase: GamepadInputEventPhase;
}

export interface GamepadRightStickSnapshot {
  readonly pressed: boolean;
  readonly justPressed: boolean;
  readonly justReleased: boolean;
}

export interface GamepadDeviceDiagnostic {
  readonly id: string;
  readonly index: number;
  readonly mapping: string;
}

export interface GamepadInputDiagnostic {
  readonly activeGamepad: GamepadDeviceDiagnostic | null;
  readonly awaitingNeutralReason: GamepadNeutralReason | null;
  readonly connectedGamepads: readonly GamepadDeviceDiagnostic[];
  readonly lastEvent: GamepadNavigationAction | 'rightStick' | null;
  readonly navigationEnabled: boolean;
  readonly neutralSampleCount: number;
  readonly status: GamepadInputStatus;
  readonly unsupportedGamepads: readonly GamepadDeviceDiagnostic[];
}

export interface GamepadInputFrame {
  readonly diagnostic: GamepadInputDiagnostic;
  readonly events: readonly GamepadInputEvent[];
  readonly rightStick: GamepadRightStickSnapshot;
}

export interface GamepadInputEngineOptions {
  readonly axisPressThreshold?: number;
  readonly axisReleaseThreshold?: number;
  readonly buttonPressThreshold?: number;
  readonly firstRepeatDelayMs?: number;
  readonly navigationEnabled?: boolean;
  readonly neutralDurationMs?: number;
  readonly repeatDelayMs?: number;
}

interface NormalizedEngineOptions {
  axisPressThreshold: number;
  axisReleaseThreshold: number;
  buttonPressThreshold: number;
  firstRepeatDelayMs: number;
  neutralDurationMs: number;
  repeatDelayMs: number;
}

interface RepeatState {
  nextAt: number;
  pressed: boolean;
}

interface EdgeState {
  pressed: boolean;
}

interface ActivityState {
  active: boolean;
  lastActivityAt: number;
  timestamp: number;
}

interface ConnectedGamepad {
  device: GamepadLike;
  fingerprint: string;
}

const REPEATING_ACTIONS: readonly GamepadNavigationAction[] = [
  'up',
  'down',
  'left',
  'right',
  'scrollUp',
  'scrollDown',
];

const EDGE_ACTION_BUTTONS: readonly [GamepadNavigationAction, number][] = [
  ['confirm', BUTTON_A],
  ['back', BUTTON_B],
  ['favorite', BUTTON_X],
  ['compact', BUTTON_Y],
  ['previousTab', BUTTON_LB],
  ['nextTab', BUTTON_RB],
];

const DIRECTION_ACTIONS: readonly GamepadDirection[] = ['up', 'down', 'left', 'right'];

export class GamepadInputEngine {
  private readonly options: NormalizedEngineOptions;
  private readonly repeatStates = new Map<GamepadNavigationAction, RepeatState>();
  private readonly edgeStates = new Map<GamepadNavigationAction, EdgeState>();
  private readonly activityStates = new Map<string, ActivityState>();
  private readonly dpadPressedAt = new Map<GamepadDirection, number>();

  private activeFingerprint: string | null = null;
  private activeGamepad: GamepadDeviceDiagnostic | null = null;
  private awaitingNeutral = true;
  private awaitingNeutralReason: GamepadNeutralReason | null = 'initial';
  private connectedFingerprints: ReadonlySet<string> | null = null;
  private inputActive = true;
  private lastEvent: GamepadNavigationAction | 'rightStick' | null = null;
  private navigationEnabled: boolean;
  private neutralSampleCount = 0;
  private neutralStartedAt: number | null = null;
  private rightStickState: EdgeState = { pressed: false };
  private selectedDirection: GamepadDirection | null = null;
  private axisX: -1 | 0 | 1 = 0;
  private axisY: -1 | 0 | 1 = 0;

  constructor(options: GamepadInputEngineOptions = {}) {
    this.options = normalizeOptions(options);
    this.navigationEnabled = options.navigationEnabled ?? true;
    this.resetTransientState();
  }

  setNavigationEnabled(enabled: boolean) {
    if (this.navigationEnabled === enabled) return;
    this.navigationEnabled = enabled;
    this.enterAwaitingNeutral('enabled', true);
  }

  setInputActive(active: boolean, reason: Extract<GamepadNeutralReason, 'focus' | 'visibility'> = 'focus') {
    if (this.inputActive === active) return;
    this.inputActive = active;
    this.enterAwaitingNeutral(reason, true);
  }

  suspend(reason: Extract<GamepadNeutralReason, 'focus' | 'visibility'> = 'focus') {
    this.setInputActive(false, reason);
  }

  resume(reason: Extract<GamepadNeutralReason, 'focus' | 'visibility'> = 'focus') {
    this.setInputActive(true, reason);
  }

  reset(reason: GamepadNeutralReason = 'manual') {
    this.enterAwaitingNeutral(reason, true);
  }

  sample(gamepads: readonly (GamepadLike | null | undefined)[], now: number): GamepadInputFrame {
    const sampledAt = normalizeNow(now);
    const connected = collectConnectedGamepads(gamepads);
    const standard = connected.filter(({ device }) => device.mapping === STANDARD_MAPPING);
    const unsupported = connected.filter(({ device }) => device.mapping !== STANDARD_MAPPING);

    this.reconcileDevices(standard, connected);
    this.recordActivity(standard, sampledAt);

    const owner = this.resolveActiveGamepad(standard);
    if (!this.inputActive) return this.createFrame(connected, unsupported, null);

    if (this.awaitingNeutral) {
      this.advanceNeutralGate(owner ? [owner] : standard, sampledAt);
      return this.createFrame(connected, unsupported, null);
    }

    const active = owner ?? this.claimActiveGamepad(standard);
    if (!active) return this.createFrame(connected, unsupported, null);

    const events: GamepadInputEvent[] = [];
    const direction = this.resolveDirection(active.device, sampledAt);
    for (const action of DIRECTION_ACTIONS) {
      this.updateRepeatingAction(
        action,
        direction === action,
        sampledAt,
        active.device.index,
        events,
        this.navigationEnabled,
      );
    }

    const scrollUp = isButtonPressed(active.device, BUTTON_LT, this.options.buttonPressThreshold);
    const scrollDown = isButtonPressed(active.device, BUTTON_RT, this.options.buttonPressThreshold);
    this.updateRepeatingAction(
      'scrollUp',
      scrollUp && !scrollDown,
      sampledAt,
      active.device.index,
      events,
      this.navigationEnabled,
    );
    this.updateRepeatingAction(
      'scrollDown',
      scrollDown && !scrollUp,
      sampledAt,
      active.device.index,
      events,
      this.navigationEnabled,
    );

    const previousTab = isButtonPressed(active.device, BUTTON_LB, this.options.buttonPressThreshold);
    const nextTab = isButtonPressed(active.device, BUTTON_RB, this.options.buttonPressThreshold);
    for (const [action, button] of EDGE_ACTION_BUTTONS) {
      const rawPressed = isButtonPressed(active.device, button, this.options.buttonPressThreshold);
      const pressed = action === 'previousTab'
        ? rawPressed && !nextTab
        : action === 'nextTab'
          ? rawPressed && !previousTab
          : rawPressed;
      this.updateEdgeAction(action, pressed, active.device.index, events, this.navigationEnabled);
    }

    const rightStick = this.updateRightStick(
      isButtonPressed(active.device, BUTTON_RIGHT_STICK, this.options.buttonPressThreshold),
    );
    if (rightStick.justPressed) this.lastEvent = 'rightStick';
    return this.createFrame(connected, unsupported, { events, rightStick });
  }

  private reconcileDevices(
    standard: readonly ConnectedGamepad[],
    connected: readonly ConnectedGamepad[],
  ) {
    const fingerprints = new Set(standard.map(({ fingerprint }) => fingerprint));
    const active = this.activeFingerprint
      ? standard.find(({ fingerprint }) => fingerprint === this.activeFingerprint) ?? null
      : null;

    if (this.activeFingerprint && !active) {
      const sameIndex = connected.some(({ device }) => device.index === this.activeGamepad?.index);
      this.activeFingerprint = null;
      this.activeGamepad = null;
      this.enterAwaitingNeutral(sameIndex ? 'device-change' : 'reconnect', false);
    } else if (!this.activeFingerprint && this.connectedFingerprints && !setsEqual(fingerprints, this.connectedFingerprints)) {
      if (this.awaitingNeutralReason !== 'reconnect') this.enterAwaitingNeutral('device-change', false);
    }

    this.connectedFingerprints = fingerprints;
    for (const fingerprint of this.activityStates.keys()) {
      if (!fingerprints.has(fingerprint)) this.activityStates.delete(fingerprint);
    }
  }

  private recordActivity(gamepads: readonly ConnectedGamepad[], now: number) {
    for (const gamepad of gamepads) {
      const active = hasRealActivity(gamepad.device, this.options);
      const previous = this.activityStates.get(gamepad.fingerprint);
      const timestamp = normalizeTimestamp(gamepad.device.timestamp);
      const timestampAdvanced = previous ? timestamp > previous.timestamp : timestamp > 0;
      this.activityStates.set(gamepad.fingerprint, {
        active,
        lastActivityAt: active && (!previous?.active || timestampAdvanced)
          ? now
          : previous?.lastActivityAt ?? Number.NEGATIVE_INFINITY,
        timestamp,
      });
    }
  }

  private resolveActiveGamepad(gamepads: readonly ConnectedGamepad[]): ConnectedGamepad | null {
    if (!this.activeFingerprint) return null;
    return gamepads.find(({ fingerprint }) => fingerprint === this.activeFingerprint) ?? null;
  }

  private claimActiveGamepad(gamepads: readonly ConnectedGamepad[]): ConnectedGamepad | null {
    const candidate = gamepads
      .filter(({ fingerprint }) => this.activityStates.get(fingerprint)?.active)
      .sort((left, right) => {
        const leftActivity = this.activityStates.get(left.fingerprint);
        const rightActivity = this.activityStates.get(right.fingerprint);
        const activityDifference = (rightActivity?.lastActivityAt ?? Number.NEGATIVE_INFINITY)
          - (leftActivity?.lastActivityAt ?? Number.NEGATIVE_INFINITY);
        if (activityDifference !== 0) return activityDifference;

        const timestampDifference = (rightActivity?.timestamp ?? 0) - (leftActivity?.timestamp ?? 0);
        if (timestampDifference !== 0) return timestampDifference;
        return left.device.index - right.device.index;
      })[0] ?? null;

    if (!candidate) return null;
    this.activeFingerprint = candidate.fingerprint;
    this.activeGamepad = toDeviceDiagnostic(candidate.device);
    this.resetTransientState();
    return candidate;
  }

  private advanceNeutralGate(gamepads: readonly ConnectedGamepad[], now: number) {
    if (gamepads.length === 0 || gamepads.some(({ device }) => !isNeutral(device, this.options))) {
      this.neutralStartedAt = null;
      this.neutralSampleCount = 0;
      return;
    }

    if (this.neutralStartedAt === null) this.neutralStartedAt = now;
    this.neutralSampleCount += 1;
    if (
      this.neutralSampleCount < MINIMUM_NEUTRAL_SAMPLE_COUNT
      || now - this.neutralStartedAt < this.options.neutralDurationMs
    ) return;

    this.awaitingNeutral = false;
    this.awaitingNeutralReason = null;
    this.neutralStartedAt = null;
    this.neutralSampleCount = 0;
    this.resetTransientState();
  }

  private enterAwaitingNeutral(reason: GamepadNeutralReason, preserveOwner: boolean) {
    if (!preserveOwner) {
      this.activeFingerprint = null;
      this.activeGamepad = null;
    }
    this.awaitingNeutral = true;
    this.awaitingNeutralReason = reason;
    this.neutralStartedAt = null;
    this.neutralSampleCount = 0;
    this.resetTransientState();
  }

  private resolveDirection(gamepad: GamepadLike, now: number): GamepadDirection | null {
    this.axisX = updateAxisLatch(this.axisX, normalizeAxis(gamepad.axes[0]), this.options);
    this.axisY = updateAxisLatch(this.axisY, normalizeAxis(gamepad.axes[1]), this.options);

    const dpad = {
      up: isButtonPressed(gamepad, BUTTON_DPAD_UP, this.options.buttonPressThreshold),
      down: isButtonPressed(gamepad, BUTTON_DPAD_DOWN, this.options.buttonPressThreshold),
      left: isButtonPressed(gamepad, BUTTON_DPAD_LEFT, this.options.buttonPressThreshold),
      right: isButtonPressed(gamepad, BUTTON_DPAD_RIGHT, this.options.buttonPressThreshold),
    } satisfies Record<GamepadDirection, boolean>;

    for (const direction of DIRECTION_ACTIONS) {
      const state = this.edgeStates.get(direction);
      if (dpad[direction] && !state?.pressed) this.dpadPressedAt.set(direction, now);
      this.edgeStates.set(direction, { pressed: dpad[direction] });
    }

    let candidates: GamepadDirection[];
    if (DIRECTION_ACTIONS.some((direction) => dpad[direction])) {
      const horizontal = dpad.left === dpad.right ? null : dpad.left ? 'left' : 'right';
      const vertical = dpad.up === dpad.down ? null : dpad.up ? 'up' : 'down';
      candidates = [horizontal, vertical].filter((direction): direction is GamepadDirection => direction !== null);
    } else {
      candidates = [
        this.axisX < 0 ? 'left' : this.axisX > 0 ? 'right' : null,
        this.axisY < 0 ? 'up' : this.axisY > 0 ? 'down' : null,
      ].filter((direction): direction is GamepadDirection => direction !== null);
    }

    if (candidates.length === 0) {
      this.selectedDirection = null;
      return null;
    }
    if (this.selectedDirection && candidates.includes(this.selectedDirection)) return this.selectedDirection;

    if (candidates.length === 1) {
      this.selectedDirection = candidates[0];
      return this.selectedDirection;
    }

    const hasDpadInput = DIRECTION_ACTIONS.some((direction) => dpad[direction]);
    this.selectedDirection = hasDpadInput
      ? [...candidates].sort((left, right) =>
          (this.dpadPressedAt.get(right) ?? Number.NEGATIVE_INFINITY)
          - (this.dpadPressedAt.get(left) ?? Number.NEGATIVE_INFINITY)
        )[0]
      : Math.abs(normalizeAxis(gamepad.axes[1])) > Math.abs(normalizeAxis(gamepad.axes[0]))
        ? candidates.find((direction) => direction === 'up' || direction === 'down') ?? candidates[0]
        : candidates.find((direction) => direction === 'left' || direction === 'right') ?? candidates[0];
    return this.selectedDirection;
  }

  private updateRepeatingAction(
    action: GamepadNavigationAction,
    pressed: boolean,
    now: number,
    gamepadIndex: number,
    events: GamepadInputEvent[],
    emit: boolean,
  ) {
    const state = this.repeatStates.get(action) ?? { nextAt: 0, pressed: false };
    let phase: GamepadInputEventPhase | null = null;
    if (pressed && !state.pressed) {
      phase = 'pressed';
      state.nextAt = now + this.options.firstRepeatDelayMs;
    } else if (pressed && now >= state.nextAt) {
      phase = 'repeat';
      state.nextAt = now + this.options.repeatDelayMs;
    } else if (!pressed) {
      state.nextAt = 0;
    }
    state.pressed = pressed;
    this.repeatStates.set(action, state);

    if (!phase || !emit) return;
    events.push({ action, gamepadIndex, phase });
    this.lastEvent = action;
  }

  private updateEdgeAction(
    action: GamepadNavigationAction,
    pressed: boolean,
    gamepadIndex: number,
    events: GamepadInputEvent[],
    emit: boolean,
  ) {
    const state = this.edgeStates.get(action) ?? { pressed: false };
    const justPressed = pressed && !state.pressed;
    state.pressed = pressed;
    this.edgeStates.set(action, state);
    if (!justPressed || !emit) return;
    events.push({ action, gamepadIndex, phase: 'pressed' });
    this.lastEvent = action;
  }

  private updateRightStick(pressed: boolean): GamepadRightStickSnapshot {
    const wasPressed = this.rightStickState.pressed;
    this.rightStickState = { pressed };
    return {
      pressed,
      justPressed: pressed && !wasPressed,
      justReleased: !pressed && wasPressed,
    };
  }

  private resetTransientState() {
    this.repeatStates.clear();
    this.edgeStates.clear();
    for (const action of REPEATING_ACTIONS) {
      this.repeatStates.set(action, { nextAt: 0, pressed: false });
    }
    for (const [action] of EDGE_ACTION_BUTTONS) {
      this.edgeStates.set(action, { pressed: false });
    }
    for (const direction of DIRECTION_ACTIONS) {
      this.edgeStates.set(direction, { pressed: false });
    }
    this.rightStickState = { pressed: false };
    this.dpadPressedAt.clear();
    this.selectedDirection = null;
    this.axisX = 0;
    this.axisY = 0;
  }

  private createFrame(
    connected: readonly ConnectedGamepad[],
    unsupported: readonly ConnectedGamepad[],
    output: { events: readonly GamepadInputEvent[]; rightStick: GamepadRightStickSnapshot } | null,
  ): GamepadInputFrame {
    return {
      events: output?.events ?? [],
      rightStick: output?.rightStick ?? emptyRightStickSnapshot(),
      diagnostic: {
        activeGamepad: this.activeGamepad,
        awaitingNeutralReason: this.awaitingNeutralReason,
        connectedGamepads: connected.map(({ device }) => toDeviceDiagnostic(device)),
        lastEvent: this.lastEvent,
        navigationEnabled: this.navigationEnabled,
        neutralSampleCount: this.neutralSampleCount,
        status: this.resolveStatus(connected, unsupported),
        unsupportedGamepads: unsupported.map(({ device }) => toDeviceDiagnostic(device)),
      },
    };
  }

  private resolveStatus(
    connected: readonly ConnectedGamepad[],
    unsupported: readonly ConnectedGamepad[],
  ): GamepadInputStatus {
    if (!this.inputActive) return 'suspended';
    const supportedCount = connected.length - unsupported.length;
    if (supportedCount === 0) return unsupported.length > 0 ? 'unsupported-mapping' : 'waiting-for-gamepad';
    return this.awaitingNeutral ? 'awaiting-neutral' : 'ready';
  }
}

function normalizeOptions(options: GamepadInputEngineOptions): NormalizedEngineOptions {
  const axisReleaseThreshold = normalizeThreshold(
    options.axisReleaseThreshold,
    GAMEPAD_AXIS_RELEASE_THRESHOLD,
  );
  const axisPressThreshold = Math.max(
    axisReleaseThreshold + 0.05,
    normalizeThreshold(options.axisPressThreshold, GAMEPAD_AXIS_PRESS_THRESHOLD),
  );
  return {
    axisPressThreshold: Math.min(1, axisPressThreshold),
    axisReleaseThreshold,
    buttonPressThreshold: normalizeThreshold(
      options.buttonPressThreshold,
      GAMEPAD_BUTTON_PRESS_THRESHOLD,
    ),
    firstRepeatDelayMs: normalizeDelay(
      options.firstRepeatDelayMs,
      GAMEPAD_FIRST_REPEAT_DELAY_MS,
    ),
    neutralDurationMs: normalizeDelay(options.neutralDurationMs, GAMEPAD_NEUTRAL_DURATION_MS),
    repeatDelayMs: normalizeDelay(options.repeatDelayMs, GAMEPAD_REPEAT_DELAY_MS),
  };
}

function normalizeThreshold(value: number | undefined, fallback: number): number {
  if (!Number.isFinite(value)) return fallback;
  return Math.max(0, Math.min(1, value ?? fallback));
}

function normalizeDelay(value: number | undefined, fallback: number): number {
  if (!Number.isFinite(value)) return fallback;
  return Math.max(0, Math.trunc(value ?? fallback));
}

function collectConnectedGamepads(
  gamepads: readonly (GamepadLike | null | undefined)[],
): ConnectedGamepad[] {
  return gamepads
    .filter((gamepad): gamepad is GamepadLike => Boolean(gamepad?.connected))
    .map((device) => ({ device, fingerprint: getDeviceFingerprint(device) }))
    .sort((left, right) => left.device.index - right.device.index);
}

function getDeviceFingerprint(gamepad: GamepadLike): string {
  return `${gamepad.index}\u0000${gamepad.id}\u0000${gamepad.mapping}`;
}

function toDeviceDiagnostic(gamepad: GamepadLike): GamepadDeviceDiagnostic {
  return {
    id: gamepad.id,
    index: gamepad.index,
    mapping: gamepad.mapping,
  };
}

function isButtonPressed(gamepad: GamepadLike, index: number, threshold: number): boolean {
  const button = gamepad.buttons[index];
  return Boolean(button?.pressed || normalizeButtonValue(button?.value) > threshold);
}

function normalizeButtonValue(value: number | undefined): number {
  return Number.isFinite(value) ? value ?? 0 : 0;
}

function normalizeAxis(value: number | undefined): number {
  if (!Number.isFinite(value)) return 0;
  return Math.max(-1, Math.min(1, value ?? 0));
}

function normalizeNow(now: number): number {
  return Number.isFinite(now) ? now : 0;
}

function normalizeTimestamp(timestamp: number | undefined): number {
  return Number.isFinite(timestamp) ? timestamp ?? 0 : 0;
}

function updateAxisLatch(
  current: -1 | 0 | 1,
  value: number,
  options: NormalizedEngineOptions,
): -1 | 0 | 1 {
  if (current === 0) {
    if (value >= options.axisPressThreshold) return 1;
    if (value <= -options.axisPressThreshold) return -1;
    return 0;
  }
  if (current > 0) {
    if (value <= -options.axisPressThreshold) return -1;
    return value <= options.axisReleaseThreshold ? 0 : 1;
  }
  if (value >= options.axisPressThreshold) return 1;
  return value >= -options.axisReleaseThreshold ? 0 : -1;
}

function hasRealActivity(gamepad: GamepadLike, options: NormalizedEngineOptions): boolean {
  return gamepad.buttons.some((_, index) => isButtonPressed(gamepad, index, options.buttonPressThreshold))
    || gamepad.axes.some((axis) => Math.abs(normalizeAxis(axis)) >= options.axisPressThreshold);
}

function isNeutral(gamepad: GamepadLike, options: NormalizedEngineOptions): boolean {
  return !gamepad.buttons.some((_, index) => isButtonPressed(gamepad, index, options.buttonPressThreshold))
    && gamepad.axes.every((axis) => Math.abs(normalizeAxis(axis)) <= options.axisReleaseThreshold);
}

function setsEqual(left: ReadonlySet<string>, right: ReadonlySet<string>): boolean {
  if (left.size !== right.size) return false;
  for (const value of left) {
    if (!right.has(value)) return false;
  }
  return true;
}

function emptyRightStickSnapshot(): GamepadRightStickSnapshot {
  return {
    pressed: false,
    justPressed: false,
    justReleased: false,
  };
}
