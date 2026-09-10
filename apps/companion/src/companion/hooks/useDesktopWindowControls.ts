import { useEffect, useState, useSyncExternalStore } from 'react';
import {
  DesktopWindowController,
  type DesktopWindowPreferences,
  type DesktopWindowTransport,
} from '@/companion/domain/desktop-window-control';

export interface DesktopWindowControlsOptions extends DesktopWindowPreferences {
  enabled: boolean;
}

export function useDesktopWindowControls({
  enabled, focusSwitchBehavior, alwaysOnTop, focusSwitchCooldownMs,
}: DesktopWindowControlsOptions) {
  const [controller] = useState(() => {
    let transport: Promise<DesktopWindowTransport> | undefined;
    return new DesktopWindowController(() => {
      transport ??= Promise.all([import('@tauri-apps/api/core'), import('@tauri-apps/api/event')])
        .then(([{ invoke }, { listen }]) => ({
          invoke,
          listen: <T,>(event: string, callback: (payload: T) => void) => listen<T>(event, (message) => callback(message.payload)),
        }));
      return transport;
    });
  });
  const state = useSyncExternalStore(controller.subscribe, controller.getSnapshot);

  useEffect(() => {
    if (enabled) controller.start();
    else controller.stop();
    return () => controller.stop();
  }, [controller, enabled]);

  useEffect(() => {
    if (!enabled) return;
    controller.applyPreferences({ focusSwitchBehavior, alwaysOnTop, focusSwitchCooldownMs });
  }, [controller, enabled, focusSwitchBehavior, alwaysOnTop, focusSwitchCooldownMs]);

  return { ...state, setMousePassthrough: controller.setMousePassthrough };
}
