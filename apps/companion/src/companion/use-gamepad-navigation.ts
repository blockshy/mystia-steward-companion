import { useEffect, useRef } from 'react';

import { GamepadFocusManager } from '@/companion/gamepad/gamepad-focus-manager';
import {
  GamepadInputEngine,
  type GamepadInputDiagnostic,
  type GamepadNavigationAction,
} from '@/companion/gamepad/gamepad-input-engine';

const SCROLL_STEP = 320;

export interface GamepadNavigationOptions<TTab extends string> {
  enabled?: boolean;
  activeTab: TTab;
  tabs: readonly TTab[];
  focusMode: boolean;
  onTabChange: (tab: TTab) => void;
  onToggleWindow: () => void;
  onEnterFocusMode: () => void;
  onExitFocusMode: () => void;
  onToggleCompactMode: () => void;
}

export function useGamepadNavigation<TTab extends string>({
  enabled = true,
  activeTab,
  tabs,
  focusMode,
  onTabChange,
  onToggleWindow,
  onEnterFocusMode,
  onExitFocusMode,
  onToggleCompactMode,
}: GamepadNavigationOptions<TTab>) {
  const optionsRef = useRef({
    enabled,
    activeTab,
    tabs,
    focusMode,
    onTabChange,
    onToggleWindow,
    onEnterFocusMode,
    onExitFocusMode,
    onToggleCompactMode,
  });

  useEffect(() => {
    optionsRef.current = {
      enabled,
      activeTab,
      tabs,
      focusMode,
      onTabChange,
      onToggleWindow,
      onEnterFocusMode,
      onExitFocusMode,
      onToggleCompactMode,
    };
  }, [
    activeTab,
    enabled,
    focusMode,
    onEnterFocusMode,
    onExitFocusMode,
    onTabChange,
    onToggleCompactMode,
    onToggleWindow,
    tabs,
  ]);

  useEffect(() => {
    const input = new GamepadInputEngine({ navigationEnabled: optionsRef.current.enabled });
    const focus = new GamepadFocusManager();
    let animationFrame = 0;
    let disposed = false;
    let diagnosticKey = '';

    const focusAfterRender = (callback: () => boolean) => {
      window.requestAnimationFrame(() => {
        if (!disposed && !callback()) focus.focusFirstVisibleElement();
      });
    };

    const changeTab = (direction: -1 | 1) => {
      const options = optionsRef.current;
      if (options.focusMode || options.tabs.length === 0) return;
      const currentIndex = Math.max(0, options.tabs.indexOf(options.activeTab));
      const nextTab = options.tabs[(currentIndex + direction + options.tabs.length) % options.tabs.length];
      options.onTabChange(nextTab);
      focusAfterRender(() => focus.focusTabByValue(nextTab));
    };

    const enterFocusMode = () => {
      optionsRef.current.onEnterFocusMode();
      focusAfterRender(() => focus.focusFirstVisibleElement());
    };

    const exitFocusMode = () => {
      optionsRef.current.onExitFocusMode();
      focusAfterRender(() => focus.focusActiveTab());
    };

    const runAction = (action: GamepadNavigationAction) => {
      switch (action) {
        case 'up':
        case 'down':
        case 'left':
        case 'right':
          focus.move(action);
          return;
        case 'confirm':
          focus.confirm();
          return;
        case 'back':
          if (focus.back()) return;
          if (optionsRef.current.focusMode) exitFocusMode();
          return;
        case 'favorite':
          focus.favorite();
          return;
        case 'compact':
          if (optionsRef.current.focusMode) {
            optionsRef.current.onToggleCompactMode();
          } else {
            enterFocusMode();
          }
          return;
        case 'previousTab':
          changeTab(-1);
          return;
        case 'nextTab':
          changeTab(1);
          return;
        case 'scrollUp':
          focus.scroll(-SCROLL_STEP);
          return;
        case 'scrollDown':
          focus.scroll(SCROLL_STEP);
          return;
      }
    };

    const updateDiagnostic = (diagnostic: GamepadInputDiagnostic) => {
      const nextKey = [
        diagnostic.status,
        diagnostic.activeGamepad?.index ?? '',
        diagnostic.activeGamepad?.id ?? '',
        diagnostic.activeGamepad?.mapping ?? '',
        diagnostic.awaitingNeutralReason ?? '',
        diagnostic.lastEvent ?? '',
      ].join(':');
      if (nextKey === diagnosticKey) return;
      diagnosticKey = nextKey;
      document.body.dataset.gamepadStatus = diagnostic.status;
      setOptionalDataset('gamepadIndex', diagnostic.activeGamepad?.index.toString());
      setOptionalDataset('gamepadId', diagnostic.activeGamepad?.id);
      setOptionalDataset('gamepadMapping', diagnostic.activeGamepad?.mapping);
      setOptionalDataset('gamepadNeutralReason', diagnostic.awaitingNeutralReason ?? undefined);
      setOptionalDataset('gamepadLastAction', diagnostic.lastEvent ?? undefined);
    };

    const syncInputActivity = (reason: 'focus' | 'visibility') => {
      input.setInputActive(
        document.hasFocus() && document.visibilityState === 'visible',
        reason,
      );
    };

    const poll = () => {
      if (disposed) return;
      input.setNavigationEnabled(optionsRef.current.enabled);
      const frame = input.sample(navigator.getGamepads?.() ?? [], performance.now());
      updateDiagnostic(frame.diagnostic);
      for (const event of frame.events) runAction(event.action);
      if (frame.rightStick.justPressed) optionsRef.current.onToggleWindow();
      animationFrame = window.requestAnimationFrame(poll);
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'F8' && !event.repeat) {
        event.preventDefault();
        optionsRef.current.onToggleWindow();
        return;
      }
      if (event.isTrusted) focus.handleKeyboardInput();
    };
    const handlePointerDown = () => focus.handlePointerInput();
    const handleFocus = () => syncInputActivity('focus');
    const handleBlur = () => syncInputActivity('focus');
    const handleVisibilityChange = () => syncInputActivity('visibility');

    window.addEventListener('keydown', handleKeyDown);
    window.addEventListener('pointerdown', handlePointerDown, true);
    window.addEventListener('focus', handleFocus);
    window.addEventListener('blur', handleBlur);
    document.addEventListener('visibilitychange', handleVisibilityChange);
    syncInputActivity('focus');
    animationFrame = window.requestAnimationFrame(poll);

    return () => {
      disposed = true;
      window.removeEventListener('keydown', handleKeyDown);
      window.removeEventListener('pointerdown', handlePointerDown, true);
      window.removeEventListener('focus', handleFocus);
      window.removeEventListener('blur', handleBlur);
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      if (animationFrame) window.cancelAnimationFrame(animationFrame);
      focus.dispose();
      for (const key of [
        'gamepadStatus',
        'gamepadIndex',
        'gamepadId',
        'gamepadMapping',
        'gamepadNeutralReason',
        'gamepadLastAction',
      ]) delete document.body.dataset[key];
    };
  }, []);
}

function setOptionalDataset(key: string, value: string | undefined) {
  if (value === undefined) {
    delete document.body.dataset[key];
  } else {
    document.body.dataset[key] = value;
  }
}
