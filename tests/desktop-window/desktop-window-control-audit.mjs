import assert from 'node:assert/strict';
import { DesktopWindowController } from '../../apps/companion/src/companion/domain/desktop-window-control.ts';

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}
const settle = () => new Promise((resolve) => setImmediate(resolve));
const hotkey = (revision, status = 'available', errorCode = null) => ({ revision, status, errorCode });
const preferences = (alwaysOnTop) => ({ focusSwitchBehavior: 'hide', alwaysOnTop, focusSwitchCooldownMs: 800 });

function fixture() {
  const listeners = new Map();
  const calls = [];
  const responses = new Map();
  const transport = {
    async listen(event, callback) { listeners.set(event, callback); return () => listeners.delete(event); },
    async invoke(command, args) {
      calls.push({ command, args });
      if (responses.has(command)) return responses.get(command)(args);
      if (command === 'get_mouse_passthrough') return false;
      if (command === 'get_mouse_passthrough_hotkey_status') return hotkey(1);
      return undefined;
    },
  };
  const controller = new DesktopWindowController(async () => transport);
  return { controller, listeners, calls, responses, transport };
}

{
  const { controller, listeners, responses } = fixture();
  const mouseRead = deferred();
  const hotkeyRead = deferred();
  responses.set('get_mouse_passthrough', () => mouseRead.promise);
  responses.set('get_mouse_passthrough_hotkey_status', () => hotkeyRead.promise);
  controller.start();
  await settle();
  assert.equal(controller.getSnapshot().ready, false);
  listeners.get('mouse-passthrough-changed')(true);
  listeners.get('mouse-passthrough-hotkey-status-changed')(hotkey(2, 'unavailable', 1409));
  mouseRead.resolve(false);
  hotkeyRead.resolve(hotkey(1));
  await settle();
  assert.equal(controller.getSnapshot().ready, true);
  assert.equal(controller.getSnapshot().mousePassthroughEnabled, true, 'Initial read must not replace a newer native event.');
  assert.deepEqual(controller.getSnapshot().hotkeyStatus, hotkey(2, 'unavailable', 1409));
  controller.stop();
  assert.equal(listeners.size, 0);
}

{
  const { controller, responses, calls } = fixture();
  const first = deferred();
  const last = deferred();
  let started = 0;
  responses.set('apply_companion_preferences', () => (++started === 1 ? first.promise : last.promise));
  controller.start();
  controller.applyPreferences(preferences(true));
  await settle();
  assert.equal(started, 1);
  controller.applyPreferences(preferences(false));
  controller.applyPreferences({ ...preferences(false), focusSwitchCooldownMs: 1200 });
  assert.equal(started, 1, 'A later write cannot pass the in-flight native request.');
  first.resolve();
  await settle();
  assert.equal(started, 2, 'Superseded queued preferences are not sent.');
  const writes = calls.filter((call) => call.command === 'apply_companion_preferences');
  assert.deepEqual(writes.map((call) => call.args.windowSwitchCooldownMs), [800, 1200]);
  assert.equal(controller.getSnapshot().busy, true);
  last.reject(new Error('window rejected preference'));
  await settle();
  assert.equal(controller.getSnapshot().busy, false);
  assert.match(controller.getSnapshot().error, /window rejected preference/);
  controller.stop();
}

{
  const { controller, listeners, responses, calls } = fixture();
  controller.start(); await settle();
  const write = deferred();
  responses.set('set_mouse_passthrough', () => write.promise);
  const pending = controller.setMousePassthrough(true);
  await settle();
  assert.equal(controller.getSnapshot().mousePassthroughEnabled, false, 'An unconfirmed request is not persisted as actual state.');
  await controller.setMousePassthrough(false);
  assert.equal(calls.filter((call) => call.command === 'set_mouse_passthrough').length, 1);
  listeners.get('mouse-passthrough-changed')(true);
  listeners.get('mouse-passthrough-changed')(false);
  write.resolve(true); await pending;
  assert.equal(controller.getSnapshot().mousePassthroughEnabled, false, 'Late command result must not replace the later hotkey event.');
  responses.set('set_mouse_passthrough', async () => { throw new Error('OS apply failed'); });
  await controller.setMousePassthrough(true);
  assert.equal(controller.getSnapshot().mousePassthroughEnabled, false);
  assert.match(controller.getSnapshot().error, /OS apply failed/);
  controller.stop();
}

{
  const { controller, responses } = fixture();
  controller.start(); await settle();
  const write = deferred();
  responses.set('set_mouse_passthrough', () => write.promise);
  const pending = controller.setMousePassthrough(true);
  await settle();
  controller.stop(); controller.start(); await settle();
  assert.equal(controller.getSnapshot().busy, true);
  write.resolve(true); await pending;
  assert.equal(controller.getSnapshot().busy, false, 'Old transport completion must release occupancy after effect restart.');
  assert.equal(controller.getSnapshot().mousePassthroughEnabled, false, 'Prior generation response cannot change the new initial observation.');
  controller.stop();
}

{
  const { controller, responses } = fixture();
  responses.set('get_mouse_passthrough_hotkey_status', async () => ({ revision: -1, status: 'available', errorCode: null }));
  controller.start(); await settle();
  assert.equal(controller.getSnapshot().ready, false);
  assert.match(controller.getSnapshot().error, /热键状态格式错误/);
  controller.stop();
}

{
  const { controller, transport } = fixture();
  const subscribe = deferred();
  let cleaned = 0;
  transport.listen = () => subscribe.promise;
  controller.start(); await settle(); controller.stop();
  subscribe.resolve(() => { cleaned += 1; });
  await settle();
  assert.equal(cleaned, 2, 'Subscriptions completing after cleanup must both be removed.');
  assert.equal(controller.getSnapshot().ready, false);
}

console.log('desktop window state and native request lifecycle audit passed');
