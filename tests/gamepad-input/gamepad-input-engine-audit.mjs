import assert from 'node:assert/strict';
import {
  GAMEPAD_AXIS_PRESS_THRESHOLD,
  GAMEPAD_AXIS_RELEASE_THRESHOLD,
  GAMEPAD_BUTTON_PRESS_THRESHOLD,
  GAMEPAD_FIRST_REPEAT_DELAY_MS,
  GAMEPAD_REPEAT_DELAY_MS,
  GamepadInputEngine,
} from '../../apps/companion/src/companion/gamepad/gamepad-input-engine.ts';

const BUTTON_COUNT = 17;

function button(value = 0, pressed = value > GAMEPAD_BUTTON_PRESS_THRESHOLD) {
  return { pressed, value };
}

function gamepad(index, patch = {}) {
  const buttons = Array.from({ length: BUTTON_COUNT }, () => button());
  for (const [buttonIndex, value] of Object.entries(patch.buttonValues ?? {})) {
    buttons[Number(buttonIndex)] = typeof value === 'number' ? button(value) : value;
  }
  return {
    axes: patch.axes ?? [0, 0, 0, 0],
    buttons,
    connected: patch.connected ?? true,
    id: patch.id ?? `Xbox Wireless Controller ${index}`,
    index,
    mapping: patch.mapping ?? 'standard',
    timestamp: patch.timestamp ?? 0,
  };
}

function withButton(base, index, value = 1) {
  const buttons = base.buttons.map((current) => ({ ...current }));
  buttons[index] = button(value);
  return { ...base, buttons };
}

function withAxes(base, axes) {
  return { ...base, axes };
}

function neutralize(engine, gamepads, startAt = 0) {
  const first = engine.sample(gamepads, startAt);
  assert.equal(first.events.length, 0);
  const ready = engine.sample(gamepads, startAt + 50);
  assert.equal(ready.events.length, 0);
  assert.equal(ready.diagnostic.status, 'ready');
  return startAt + 50;
}

function eventNames(frame) {
  return frame.events.map(({ action, phase }) => `${action}:${phase}`);
}

assert.equal(GAMEPAD_AXIS_PRESS_THRESHOLD, 0.65);
assert.equal(GAMEPAD_AXIS_RELEASE_THRESHOLD, 0.4);
assert.equal(GAMEPAD_BUTTON_PRESS_THRESHOLD, 0.5);
assert.equal(GAMEPAD_FIRST_REPEAT_DELAY_MS, 360);
assert.equal(GAMEPAD_REPEAT_DELAY_MS, 140);

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const first = engine.sample([pad], 0);
  assert.equal(first.diagnostic.status, 'awaiting-neutral');
  assert.equal(first.diagnostic.awaitingNeutralReason, 'initial');
  const tooSoon = engine.sample([pad], 49);
  assert.equal(tooSoon.diagnostic.status, 'awaiting-neutral');
  const ready = engine.sample([pad], 50);
  assert.equal(ready.diagnostic.status, 'ready');
  assert.equal(ready.diagnostic.activeGamepad, null, 'A neutral device must not steal ownership.');

  const confirmed = engine.sample([withButton(pad, 0)], 60);
  assert.deepEqual(eventNames(confirmed), ['confirm:pressed']);
  assert.equal(confirmed.diagnostic.activeGamepad?.index, 0);
  assert.equal(engine.sample([withButton(pad, 0)], 61).events.length, 0, 'Edge actions must not repeat.');
  engine.sample([pad], 62);
  assert.deepEqual(eventNames(engine.sample([withButton(pad, 0)], 63)), ['confirm:pressed']);
}

{
  const mappings = [
    [0, 'confirm'],
    [1, 'back'],
    [2, 'favorite'],
    [3, 'compact'],
    [4, 'previousTab'],
    [5, 'nextTab'],
    [6, 'scrollUp'],
    [7, 'scrollDown'],
    [12, 'up'],
    [13, 'down'],
    [14, 'left'],
    [15, 'right'],
  ];
  for (const [buttonIndex, action] of mappings) {
    const engine = new GamepadInputEngine();
    const pad = gamepad(0);
    const readyAt = neutralize(engine, [pad]);
    assert.deepEqual(
      eventNames(engine.sample([withButton(pad, buttonIndex)], readyAt + 1)),
      [`${action}:pressed`],
      `Xbox button ${buttonIndex} must keep its existing ${action} mapping.`,
    );
  }
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  assert.equal(engine.sample([withButton(pad, 0, 0.5)], readyAt + 1).events.length, 0);
  assert.deepEqual(eventNames(engine.sample([withButton(pad, 0, 0.5001)], readyAt + 2)), ['confirm:pressed']);
  engine.sample([pad], readyAt + 3);
  const pressedFlag = withButton(pad, 0, 0);
  pressedFlag.buttons[0] = button(0, true);
  assert.deepEqual(eventNames(engine.sample([pressedFlag], readyAt + 4)), ['confirm:pressed']);
}

{
  const engine = new GamepadInputEngine();
  const first = gamepad(0);
  const second = gamepad(1);
  const readyAt = neutralize(engine, [first, second]);
  const claimed = engine.sample([first, withButton({ ...second, timestamp: 20 }, 0)], readyAt + 1);
  assert.equal(claimed.diagnostic.activeGamepad?.index, 1);
  assert.deepEqual(eventNames(claimed), ['confirm:pressed']);

  engine.sample([first, second], readyAt + 2);
  const secondaryActivity = engine.sample([withButton(first, 1), second], readyAt + 3);
  assert.equal(secondaryActivity.events.length, 0);
  assert.equal(secondaryActivity.diagnostic.activeGamepad?.index, 1, 'A second controller must not steal ownership.');

  const secondaryDisconnected = engine.sample([null, second], readyAt + 4);
  assert.equal(secondaryDisconnected.diagnostic.status, 'ready');
  assert.equal(secondaryDisconnected.diagnostic.activeGamepad?.index, 1, 'A secondary disconnect must be irrelevant.');
  assert.deepEqual(eventNames(engine.sample([null, withButton(second, 1)], readyAt + 5)), ['back:pressed']);
}

{
  const engine = new GamepadInputEngine();
  const first = gamepad(0, { timestamp: 10 });
  const second = gamepad(1, { timestamp: 30 });
  const readyAt = neutralize(engine, [first, second]);
  const simultaneous = engine.sample([withButton(first, 0), withButton(second, 0)], readyAt + 1);
  assert.equal(simultaneous.diagnostic.activeGamepad?.index, 1, 'The most recently active device must claim ownership.');
}

{
  const engine = new GamepadInputEngine();
  const first = gamepad(0);
  const second = gamepad(1);
  const readyAt = neutralize(engine, [first, second]);
  engine.sample([withButton(first, 0), second], readyAt + 1);
  engine.sample([first, second], readyAt + 2);

  const disconnected = engine.sample([null, withButton(second, 1)], readyAt + 3);
  assert.equal(disconnected.diagnostic.status, 'awaiting-neutral');
  assert.equal(disconnected.diagnostic.awaitingNeutralReason, 'reconnect');
  assert.equal(disconnected.events.length, 0);
  assert.equal(disconnected.diagnostic.activeGamepad, null);
  engine.sample([null, second], readyAt + 4);
  const ready = engine.sample([null, second], readyAt + 54);
  assert.equal(ready.diagnostic.status, 'ready');
  assert.deepEqual(eventNames(engine.sample([null, withButton(second, 1)], readyAt + 55)), ['back:pressed']);
}

{
  const engine = new GamepadInputEngine();
  const directInput = gamepad(0, { mapping: '', buttonValues: { 0: 1 } });
  const unsupported = engine.sample([directInput], 0);
  assert.equal(unsupported.diagnostic.status, 'unsupported-mapping');
  assert.equal(unsupported.diagnostic.unsupportedGamepads[0]?.index, 0);
  assert.equal(unsupported.events.length, 0);
  assert.equal(unsupported.rightStick.justPressed, false);
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const heldOtherButton = withButton(pad, 8);
  engine.sample([heldOtherButton], 0);
  const blockedByButton = engine.sample([heldOtherButton], 100);
  assert.equal(blockedByButton.diagnostic.status, 'awaiting-neutral', 'Every button must be neutral before activation.');
  engine.sample([pad], 101);
  assert.equal(engine.sample([pad], 151).diagnostic.status, 'ready');
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const heldOtherAxis = withAxes(pad, [0, 0, 0.7, 0]);
  engine.sample([heldOtherAxis], 0);
  assert.equal(engine.sample([heldOtherAxis], 100).diagnostic.status, 'awaiting-neutral');
  engine.sample([pad], 101);
  assert.equal(engine.sample([pad], 151).diagnostic.status, 'ready');
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  assert.equal(engine.sample([withAxes(pad, [0.64, 0])], readyAt + 1).events.length, 0);
  assert.deepEqual(eventNames(engine.sample([withAxes(pad, [0.66, 0])], readyAt + 2)), ['right:pressed']);
  assert.equal(engine.sample([withAxes(pad, [0.5, 0])], readyAt + 3).events.length, 0, 'Hysteresis must retain the direction above the release threshold.');
  engine.sample([withAxes(pad, [0.39, 0])], readyAt + 4);
  assert.equal(engine.sample([withAxes(pad, [0.55, 0])], readyAt + 5).events.length, 0, 'A released axis must cross the press threshold again.');
  assert.deepEqual(eventNames(engine.sample([withAxes(pad, [-0.66, 0])], readyAt + 6)), ['left:pressed']);
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  const axisRight = withAxes(pad, [0.9, 0]);
  assert.deepEqual(eventNames(engine.sample([withButton(axisRight, 12)], readyAt + 1)), ['up:pressed'], 'D-pad input must override analog input.');
  engine.sample([pad], readyAt + 2);

  const opposing = withButton(withButton(axisRight, 12), 13);
  assert.equal(engine.sample([opposing], readyAt + 3).events.length, 0, 'Opposing D-pad directions must cancel without falling through to analog input.');
  engine.sample([pad], readyAt + 4);

  const diagonal = withButton(withButton(pad, 12), 15);
  assert.equal(engine.sample([diagonal], readyAt + 5).events.length, 1, 'A diagonal must emit at most one direction per sample.');
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  assert.deepEqual(eventNames(engine.sample([withAxes(pad, [0.8, 0.9])], readyAt + 1)), ['down:pressed']);
  assert.equal(engine.sample([withAxes(pad, [0.99, 0.9])], readyAt + 2).events.length, 0, 'The primary axis must remain locked while held.');
  assert.deepEqual(eventNames(engine.sample([withAxes(pad, [0.9, 0.3])], readyAt + 3)), ['right:pressed']);
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  neutralize(engine, [pad], 0);
  const right = withAxes(pad, [1, 0]);
  assert.deepEqual(eventNames(engine.sample([right], 100)), ['right:pressed']);
  assert.equal(engine.sample([right], 459).events.length, 0);
  assert.deepEqual(eventNames(engine.sample([right], 460)), ['right:repeat']);
  assert.deepEqual(eventNames(engine.sample([right], 5000)), ['right:repeat'], 'A long frame pause must emit only one repeat.');
  assert.equal(engine.sample([right], 5139).events.length, 0);
  assert.deepEqual(eventNames(engine.sample([right], 5140)), ['right:repeat']);
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  const rightStick = withButton(pad, 11);
  const pressed = engine.sample([rightStick], readyAt + 1);
  assert.equal(pressed.rightStick.justPressed, true);
  assert.equal(pressed.events.length, 0);
  assert.equal(engine.sample([rightStick], readyAt + 2).rightStick.justPressed, false);
  assert.equal(engine.sample([pad], readyAt + 3).rightStick.justReleased, true);
  assert.equal(engine.sample([rightStick], readyAt + 4).rightStick.justPressed, true);
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  engine.sample([withButton(pad, 0)], readyAt + 1);
  engine.sample([pad], readyAt + 2);

  engine.setInputActive(false, 'focus');
  assert.equal(engine.sample([withButton(pad, 0)], readyAt + 3).diagnostic.status, 'suspended');
  engine.setInputActive(true, 'focus');
  assert.equal(engine.sample([withButton(pad, 0)], readyAt + 4).events.length, 0, 'A held button must not leak across focus recovery.');
  engine.sample([pad], readyAt + 5);
  engine.sample([pad], readyAt + 55);
  assert.deepEqual(eventNames(engine.sample([withButton(pad, 0)], readyAt + 56)), ['confirm:pressed']);
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const rightStick = withButton(pad, 11);
  const readyAt = neutralize(engine, [pad]);
  assert.equal(engine.sample([rightStick], readyAt + 1).rightStick.justPressed, true);

  engine.setInputActive(false, 'focus');
  assert.equal(engine.sample([rightStick], readyAt + 2).diagnostic.status, 'suspended');
  engine.setInputActive(true, 'focus');
  assert.equal(
    engine.sample([rightStick], readyAt + 3).rightStick.justPressed,
    false,
    'The RS press that opened the companion must not switch focus back during recovery.',
  );
  engine.sample([pad], readyAt + 4);
  assert.equal(engine.sample([pad], readyAt + 54).diagnostic.status, 'ready');
  assert.equal(
    engine.sample([rightStick], readyAt + 55).rightStick.justPressed,
    true,
    'RS must rearm after physical release and the neutral recovery interval.',
  );
  assert.equal(engine.sample([rightStick], readyAt + 56).rightStick.justPressed, false);
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  engine.sample([withButton(pad, 0)], readyAt + 1);
  engine.sample([pad], readyAt + 2);

  engine.suspend('visibility');
  const hidden = engine.sample([pad], readyAt + 3);
  assert.equal(hidden.diagnostic.status, 'suspended');
  assert.equal(hidden.diagnostic.awaitingNeutralReason, 'visibility');
  engine.resume('visibility');
  engine.sample([pad], readyAt + 4);
  assert.equal(engine.sample([pad], readyAt + 54).diagnostic.status, 'ready');
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  engine.sample([withButton(pad, 0)], readyAt + 1);
  engine.sample([pad], readyAt + 2);

  engine.setNavigationEnabled(false);
  engine.sample([pad], readyAt + 3);
  engine.sample([pad], readyAt + 53);
  assert.equal(engine.sample([withButton(pad, 0)], readyAt + 54).events.length, 0);
  engine.sample([pad], readyAt + 55);
  assert.equal(engine.sample([withButton(pad, 11)], readyAt + 56).rightStick.justPressed, true, 'RS must remain independent while page navigation is disabled.');

  engine.sample([pad], readyAt + 57);
  engine.setNavigationEnabled(true);
  engine.sample([pad], readyAt + 58);
  engine.sample([pad], readyAt + 108);
  assert.deepEqual(eventNames(engine.sample([withButton(pad, 0)], readyAt + 109)), ['confirm:pressed']);
}

{
  const engine = new GamepadInputEngine();
  const original = gamepad(0);
  const readyAt = neutralize(engine, [original]);
  engine.sample([withButton(original, 0)], readyAt + 1);
  engine.sample([original], readyAt + 2);

  const replacement = gamepad(0, { id: 'Replacement Xbox Controller' });
  const changed = engine.sample([replacement], readyAt + 3);
  assert.equal(changed.diagnostic.status, 'awaiting-neutral');
  assert.equal(changed.diagnostic.awaitingNeutralReason, 'device-change');
  assert.equal(changed.diagnostic.activeGamepad, null);

  const unsupportedReplacement = gamepad(0, { id: 'DirectInput Replacement', mapping: '' });
  const mappingChanged = engine.sample([unsupportedReplacement], readyAt + 4);
  assert.equal(mappingChanged.diagnostic.status, 'unsupported-mapping');
  assert.equal(mappingChanged.diagnostic.awaitingNeutralReason, 'device-change');
}

{
  const engine = new GamepadInputEngine();
  const pad = gamepad(0);
  const readyAt = neutralize(engine, [pad]);
  engine.sample([withButton(pad, 0)], readyAt + 1);
  engine.sample([pad], readyAt + 2);
  const disconnected = engine.sample([], readyAt + 3);
  assert.equal(disconnected.diagnostic.awaitingNeutralReason, 'reconnect');

  const heldOnReconnect = engine.sample([withButton(pad, 0)], readyAt + 4);
  assert.equal(heldOnReconnect.diagnostic.status, 'awaiting-neutral');
  assert.equal(heldOnReconnect.diagnostic.awaitingNeutralReason, 'reconnect');
  assert.equal(heldOnReconnect.events.length, 0);
  engine.sample([pad], readyAt + 5);
  engine.sample([pad], readyAt + 55);
  assert.deepEqual(eventNames(engine.sample([withButton(pad, 0)], readyAt + 56)), ['confirm:pressed']);
}

console.log('Gamepad input engine audit passed: ownership, neutral gating, mapping, hysteresis, arbitration, repeat cadence, suspension, and RS isolation verified.');
