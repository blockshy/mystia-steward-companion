import { useEffect, useRef } from 'react';

const FIRST_REPEAT_DELAY_MS = 360;
const REPEAT_DELAY_MS = 140;
const DEFAULT_TOGGLE_COOLDOWN_MS = 800;
const AXIS_THRESHOLD = 0.55;
const SCROLL_STEP = 320;

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

const FOCUSABLE_SELECTOR = [
  'button:not([disabled])',
  'input:not([disabled])',
  'textarea:not([disabled])',
  'select:not([disabled])',
  'a[href]',
  '[tabindex]:not([tabindex="-1"])',
  '[role="button"]:not([aria-disabled="true"])',
  '[role="switch"]:not([aria-disabled="true"])',
  '[data-gamepad-focusable="true"]',
].join(',');
const GAMEPAD_SCOPE_SELECTOR = '[data-gamepad-scope]';
const GAMEPAD_ROW_SELECTOR = '[data-gamepad-row="true"]';
const GAMEPAD_SLIDER_SELECTOR = '[data-gamepad-slider="true"]';
const SELECT_ROOT_SELECTOR = '[data-slot="select"]';
const TAB_SELECTOR = '[data-gamepad-tab="true"]';

type GamepadDirection = 'up' | 'down' | 'left' | 'right';
type GamepadAction = GamepadDirection | 'confirm' | 'back' | 'favorite' | 'focus' | 'compact' | 'previousTab' | 'nextTab' | 'scrollUp' | 'scrollDown';

interface GamepadActionState {
  pressed: boolean;
  nextAt: number;
}

export interface GamepadNavigationOptions<TTab extends string> {
  enabled?: boolean;
  toggleCooldownMs?: number;
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
  toggleCooldownMs = DEFAULT_TOGGLE_COOLDOWN_MS,
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
    activeTab,
    tabs,
    focusMode,
    toggleCooldownMs,
    onTabChange,
    onToggleWindow,
    onEnterFocusMode,
    onExitFocusMode,
    onToggleCompactMode,
  });

  useEffect(() => {
    optionsRef.current = {
      activeTab,
      tabs,
      focusMode,
      toggleCooldownMs,
      onTabChange,
      onToggleWindow,
      onEnterFocusMode,
      onExitFocusMode,
      onToggleCompactMode,
    };
  }, [
    activeTab,
    focusMode,
    onEnterFocusMode,
    onExitFocusMode,
    onTabChange,
    onToggleCompactMode,
    onToggleWindow,
    tabs,
    toggleCooldownMs,
  ]);

  useEffect(() => {
    let disposed = false;
    let animationFrame = 0;
    let lastToggleAt = 0;
    let highlightedElement: HTMLElement | null = null;
    const actionStates = new Map<GamepadAction, GamepadActionState>();

    const setGamepadMode = () => {
      document.body.dataset.gamepadNavigation = 'active';
    };

    const clearHighlight = () => {
      if (highlightedElement) {
        highlightedElement.removeAttribute('data-gamepad-focus');
        highlightedElement = null;
      }
    };

    const focusElement = (element: HTMLElement) => {
      setGamepadMode();
      clearHighlight();
      element.focus({ preventScroll: true });
      element.dataset.gamepadFocus = 'true';
      highlightedElement = element;
      element.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    };

    const runAction = (action: GamepadAction) => {
      switch (action) {
        case 'up':
        case 'down':
        case 'left':
        case 'right':
          moveFocus(action, focusElement);
          return;
        case 'confirm':
          activateFocusedElement(focusElement);
          return;
        case 'favorite':
          activateFavoriteAction(focusElement);
          return;
        case 'back':
          if (optionsRef.current.focusMode) {
            optionsRef.current.onExitFocusMode();
          } else {
            dispatchEscape();
          }
          return;
        case 'focus':
          if (optionsRef.current.focusMode) {
            optionsRef.current.onExitFocusMode();
          } else {
            optionsRef.current.onEnterFocusMode();
          }
          return;
        case 'compact':
          if (optionsRef.current.focusMode) {
            optionsRef.current.onToggleCompactMode();
          } else {
            optionsRef.current.onEnterFocusMode();
          }
          return;
        case 'previousTab':
          changeTab(-1);
          return;
        case 'nextTab':
          changeTab(1);
          return;
        case 'scrollUp':
          scrollActiveContainer(-SCROLL_STEP);
          return;
        case 'scrollDown':
          scrollActiveContainer(SCROLL_STEP);
          return;
      }
    };

    const changeTab = (direction: -1 | 1) => {
      if (optionsRef.current.focusMode) return;

      const { activeTab: currentTab, tabs: currentTabs } = optionsRef.current;
      const currentIndex = Math.max(0, currentTabs.indexOf(currentTab));
      const nextIndex = (currentIndex + direction + currentTabs.length) % currentTabs.length;
      const nextTab = currentTabs[nextIndex];
      optionsRef.current.onTabChange(nextTab);
      window.setTimeout(() => focusTabByValue(nextTab, focusElement) || focusActiveTab(focusElement), 0);
    };

    const requestWindowToggle = () => {
      const now = performance.now();
      if (now - lastToggleAt < normalizeToggleCooldownMs(optionsRef.current.toggleCooldownMs)) return;
      lastToggleAt = now;
      optionsRef.current.onToggleWindow();
    };

    const triggerRepeatingAction = (action: GamepadAction, pressed: boolean, now: number) => {
      const state = actionStates.get(action) ?? { pressed: false, nextAt: 0 };
      if (!pressed) {
        state.pressed = false;
        state.nextAt = 0;
        actionStates.set(action, state);
        return;
      }

      if (!state.pressed || now >= state.nextAt) {
        runAction(action);
        state.nextAt = now + (state.pressed ? REPEAT_DELAY_MS : FIRST_REPEAT_DELAY_MS);
        state.pressed = true;
      }

      actionStates.set(action, state);
    };

    const triggerEdgeAction = (action: GamepadAction, pressed: boolean) => {
      const state = actionStates.get(action) ?? { pressed: false, nextAt: 0 };
      if (pressed && !state.pressed) {
        runAction(action);
      }
      state.pressed = pressed;
      actionStates.set(action, state);
    };

    const poll = () => {
      if (disposed) return;

      const gamepad = getPrimaryGamepad();
      if (enabled && gamepad && document.hasFocus()) {
        const now = performance.now();
        const horizontal = normalizeAxis(gamepad.axes[0] ?? 0);
        const vertical = normalizeAxis(gamepad.axes[1] ?? 0);

        triggerRepeatingAction('left', isButtonPressed(gamepad, BUTTON_DPAD_LEFT) || horizontal < -AXIS_THRESHOLD, now);
        triggerRepeatingAction('right', isButtonPressed(gamepad, BUTTON_DPAD_RIGHT) || horizontal > AXIS_THRESHOLD, now);
        triggerRepeatingAction('up', isButtonPressed(gamepad, BUTTON_DPAD_UP) || vertical < -AXIS_THRESHOLD, now);
        triggerRepeatingAction('down', isButtonPressed(gamepad, BUTTON_DPAD_DOWN) || vertical > AXIS_THRESHOLD, now);
        triggerRepeatingAction('scrollUp', isButtonPressed(gamepad, BUTTON_LT), now);
        triggerRepeatingAction('scrollDown', isButtonPressed(gamepad, BUTTON_RT), now);

        triggerEdgeAction('confirm', isButtonPressed(gamepad, BUTTON_A));
        triggerEdgeAction('back', isButtonPressed(gamepad, BUTTON_B));
        triggerEdgeAction('favorite', isButtonPressed(gamepad, BUTTON_X));
        triggerEdgeAction('compact', isButtonPressed(gamepad, BUTTON_Y));
        triggerEdgeAction('previousTab', isButtonPressed(gamepad, BUTTON_LB));
        triggerEdgeAction('nextTab', isButtonPressed(gamepad, BUTTON_RB));

        const rightStickPressed = isButtonPressed(gamepad, BUTTON_RIGHT_STICK);
        const rightStickState = actionStates.get('focus') ?? { pressed: false, nextAt: 0 };
        if (rightStickPressed && !rightStickState.pressed) {
          requestWindowToggle();
        }
        rightStickState.pressed = rightStickPressed;
        actionStates.set('focus', rightStickState);
      }

      animationFrame = window.requestAnimationFrame(poll);
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'F8' || event.repeat) return;
      event.preventDefault();
      requestWindowToggle();
    };

    const handlePointerDown = () => {
      document.body.removeAttribute('data-gamepad-navigation');
      clearHighlight();
    };

    window.addEventListener('keydown', handleKeyDown);
    window.addEventListener('pointerdown', handlePointerDown, true);
    animationFrame = window.requestAnimationFrame(poll);

    return () => {
      disposed = true;
      window.removeEventListener('keydown', handleKeyDown);
      window.removeEventListener('pointerdown', handlePointerDown, true);
      if (animationFrame) window.cancelAnimationFrame(animationFrame);
      document.body.removeAttribute('data-gamepad-navigation');
      clearHighlight();
    };
  }, [enabled]);
}

function getPrimaryGamepad(): Gamepad | null {
  const gamepads = navigator.getGamepads?.() ?? [];
  return Array.from(gamepads).find((gamepad) => gamepad?.connected) ?? null;
}

function isButtonPressed(gamepad: Gamepad, index: number): boolean {
  return Boolean(gamepad.buttons[index]?.pressed);
}

function normalizeAxis(value: number): number {
  if (!Number.isFinite(value) || Math.abs(value) < AXIS_THRESHOLD) return 0;
  return value;
}

function normalizeToggleCooldownMs(value: number): number {
  if (!Number.isFinite(value)) return DEFAULT_TOGGLE_COOLDOWN_MS;
  return Math.max(100, Math.min(3000, Math.trunc(value)));
}

function getVisibleFocusableElements(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR))
    .filter((element) => isElementVisible(element) && !isElementDisabled(element));
}

function isElementVisible(element: HTMLElement): boolean {
  const rect = element.getBoundingClientRect();
  const style = window.getComputedStyle(element);
  return rect.width > 0
    && rect.height > 0
    && style.visibility !== 'hidden'
    && style.display !== 'none';
}

function isElementDisabled(element: HTMLElement): boolean {
  return element.hasAttribute('disabled')
    || element.getAttribute('aria-disabled') === 'true'
    || element.dataset.disabled === 'true';
}

function getActiveHTMLElement(): HTMLElement | null {
  return document.activeElement instanceof HTMLElement ? document.activeElement : null;
}

function focusFirstVisibleElement(focusElement: (element: HTMLElement) => void): boolean {
  if (focusActiveTab(focusElement)) return true;
  const first = getVisibleFocusableElements()[0];
  if (!first) return false;
  focusElement(first);
  return true;
}

function moveFocus(direction: GamepadDirection, focusElement: (element: HTMLElement) => void) {
  const active = getActiveHTMLElement();
  if (!active || !isElementVisible(active) || isElementDisabled(active)) {
    focusFirstVisibleElement(focusElement);
    return;
  }

  if (adjustGamepadSlider(active, direction)) return;
  if (moveWithinSelect(active, direction)) return;
  if (moveFromTabs(active, direction, focusElement)) return;
  if (moveWithinContent(active, direction, focusElement)) return;

  const elements = getVisibleFocusableElements();
  if (moveGeometrically(active, direction, elements, focusElement)) return;

  scrollActiveContainer(direction === 'left' ? -SCROLL_STEP : SCROLL_STEP);
}

function moveWithinSelect(
  active: HTMLElement,
  direction: GamepadDirection,
): boolean {
  if (!isSelectControl(active)) return false;

  if (direction === 'up' || direction === 'down') {
    dispatchSelectKey(active, direction === 'up' ? 'ArrowUp' : 'ArrowDown');
    return true;
  }

  return isSelectExpanded(active);
}

function moveFromTabs(
  active: HTMLElement,
  direction: GamepadDirection,
  focusElement: (element: HTMLElement) => void,
): boolean {
  if (getScopeName(active) !== 'tabs') return false;

  if (direction === 'left' || direction === 'right') {
    const tabs = getTabElements();
    const currentIndex = tabs.indexOf(active);
    if (currentIndex < 0) return false;
    const nextIndex = direction === 'left'
      ? Math.max(0, currentIndex - 1)
      : Math.min(tabs.length - 1, currentIndex + 1);
    const next = tabs[nextIndex];
    if (next) focusElement(next);
    return true;
  }

  if (direction === 'down') {
    return focusFirstContentElement(focusElement);
  }

  return false;
}

function moveWithinContent(
  active: HTMLElement,
  direction: GamepadDirection,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const scope = getScopeRoot(active);
  if (!scope || scope.dataset.gamepadScope !== 'content') return false;

  if (moveWithinGamepadRow(active, direction, focusElement)) return true;

  const scopedElements = getFocusableElementsWithin(scope);
  if (moveGeometrically(active, direction, scopedElements, focusElement)) return true;

  if (direction === 'up') return focusActiveTab(focusElement);

  scrollActiveContainer(direction === 'left' ? -SCROLL_STEP : SCROLL_STEP);
  return true;
}

function adjustGamepadSlider(active: HTMLElement, direction: GamepadDirection): boolean {
  if (direction !== 'left' && direction !== 'right') return false;
  if (!(active instanceof HTMLInputElement) || active.type !== 'range' || !active.matches(GAMEPAD_SLIDER_SELECTOR)) {
    return false;
  }

  const current = Number(active.value);
  const min = Number(active.min || 0);
  const max = Number(active.max || 100);
  const rawStep = Number(active.dataset.gamepadStep || active.step || 1);
  const step = Number.isFinite(rawStep) && rawStep > 0 ? rawStep : 1;
  const next = direction === 'left' ? current - step : current + step;
  active.value = String(Math.max(min, Math.min(max, next)));
  active.dispatchEvent(new Event('input', { bubbles: true }));
  active.dispatchEvent(new Event('change', { bubbles: true }));
  return true;
}

function moveWithinGamepadRow(
  active: HTMLElement,
  direction: GamepadDirection,
  focusElement: (element: HTMLElement) => void,
): boolean {
  if (direction !== 'left' && direction !== 'right') return false;

  const row = active.closest<HTMLElement>(GAMEPAD_ROW_SELECTOR);
  if (!row) return false;

  const rowElements = getFocusableElementsWithin(row)
    .filter((element) => element.closest<HTMLElement>(GAMEPAD_ROW_SELECTOR) === row);
  if (rowElements.length < 2) return false;

  const currentIndex = rowElements.indexOf(active);
  if (currentIndex < 0) return false;

  const nextIndex = direction === 'left'
    ? Math.max(0, currentIndex - 1)
    : Math.min(rowElements.length - 1, currentIndex + 1);
  const next = rowElements[nextIndex];
  if (!next || next === active) return true;
  focusElement(next);
  return true;
}

function moveGeometrically(
  active: HTMLElement,
  direction: GamepadDirection,
  elements: HTMLElement[],
  focusElement: (element: HTMLElement) => void,
): boolean {
  const activeRect = active.getBoundingClientRect();
  const activeCenter = rectCenter(activeRect);
  const candidates = elements
    .filter((element) => element !== active)
    .map((element) => {
      const rect = element.getBoundingClientRect();
      const center = rectCenter(rect);
      return {
        element,
        center,
        score: directionalScore(direction, activeCenter, center),
      };
    })
    .filter((candidate) => Number.isFinite(candidate.score))
    .sort((a, b) => a.score - b.score);

  const next = candidates[0]?.element;
  if (!next) return false;
  focusElement(next);
  return true;
}

function getFocusableElementsWithin(root: HTMLElement, selector = FOCUSABLE_SELECTOR): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(selector))
    .filter((element) => isElementVisible(element) && !isElementDisabled(element));
}

function getScopeRoot(element: HTMLElement): HTMLElement | null {
  return element.closest<HTMLElement>(GAMEPAD_SCOPE_SELECTOR);
}

function getScopeName(element: HTMLElement): string | undefined {
  return getScopeRoot(element)?.dataset.gamepadScope;
}

function getTabElements(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>(TAB_SELECTOR))
    .filter((element) => isElementVisible(element) && !isElementDisabled(element));
}

function focusTabByValue(
  value: string,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const tab = getTabElements().find((element) => element.dataset.gamepadTabValue === value);
  if (!tab) return false;
  focusElement(tab);
  return true;
}

function focusActiveTab(focusElement: (element: HTMLElement) => void): boolean {
  const activeTab = getTabElements().find((element) =>
    element.hasAttribute('data-active') || element.getAttribute('aria-selected') === 'true'
  );
  if (!activeTab) return false;
  focusElement(activeTab);
  return true;
}

function focusFirstContentElement(focusElement: (element: HTMLElement) => void): boolean {
  const contentScopes = Array.from(document.querySelectorAll<HTMLElement>('[data-gamepad-scope="content"]'))
    .filter(isElementVisible);
  for (const scope of contentScopes) {
    const first = getFocusableElementsWithin(scope)[0];
    if (!first) continue;
    focusElement(first);
    return true;
  }
  return false;
}

function rectCenter(rect: DOMRect) {
  return {
    x: rect.left + rect.width / 2,
    y: rect.top + rect.height / 2,
  };
}

function directionalScore(
  direction: GamepadDirection,
  from: { x: number; y: number },
  to: { x: number; y: number },
): number {
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const primaryThreshold = 4;

  switch (direction) {
    case 'up':
      return dy < -primaryThreshold ? Math.abs(dy) * 3 + Math.abs(dx) : Number.POSITIVE_INFINITY;
    case 'down':
      return dy > primaryThreshold ? Math.abs(dy) * 3 + Math.abs(dx) : Number.POSITIVE_INFINITY;
    case 'left':
      return dx < -primaryThreshold ? Math.abs(dx) * 3 + Math.abs(dy) : Number.POSITIVE_INFINITY;
    case 'right':
      return dx > primaryThreshold ? Math.abs(dx) * 3 + Math.abs(dy) : Number.POSITIVE_INFINITY;
  }
}

function activateFocusedElement(focusElement: (element: HTMLElement) => void) {
  const active = getActiveHTMLElement();
  if (!active || !isElementVisible(active)) {
    focusFirstVisibleElement(focusElement);
    return;
  }

  if (isSelectControl(active)) {
    dispatchSelectKey(active, 'Enter');
    return;
  }

  const favoriteButton = active.dataset.gamepadRow === 'true'
    ? active.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])')
    : null;

  const target = favoriteButton ?? active;
  restoreFocusAfterAction(target, focusElement);
  target.click();
}

function isSelectControl(element: HTMLElement): boolean {
  return Boolean(element.closest(SELECT_ROOT_SELECTOR));
}

function isSelectExpanded(element: HTMLElement): boolean {
  const root = element.closest<HTMLElement>(SELECT_ROOT_SELECTOR);
  return Boolean(root?.querySelector('[aria-expanded="true"]'));
}

function dispatchSelectKey(element: HTMLElement, key: 'ArrowUp' | 'ArrowDown' | 'Enter') {
  const code = key === 'Enter' ? 'Enter' : key;
  element.dispatchEvent(new KeyboardEvent('keydown', {
    key,
    code,
    bubbles: true,
    cancelable: true,
  }));
}

function activateFavoriteAction(focusElement?: (element: HTMLElement) => void) {
  const active = getActiveHTMLElement();
  const favoriteButton = active?.matches('[data-gamepad-favorite="true"]')
    ? active
    : active?.closest(GAMEPAD_ROW_SELECTOR)?.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])')
      ?? active?.closest('[data-gamepad-favorite-scope="true"]')?.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])');

  if (!favoriteButton) return;
  if (focusElement) restoreFocusAfterAction(favoriteButton, focusElement);
  favoriteButton.click();
}

function restoreFocusAfterAction(target: HTMLElement, focusElement: (element: HTMLElement) => void) {
  const focusKey = target.dataset.gamepadFocusKey;
  const rowKey = target.closest<HTMLElement>(GAMEPAD_ROW_SELECTOR)?.dataset.gamepadRowKey;
  if (!focusKey && !rowKey) return;

  const tryRestore = () => {
    const next = findRestorableElement(focusKey, rowKey);
    if (!next) return false;
    focusElement(next);
    return true;
  };

  window.setTimeout(tryRestore, 0);
  window.setTimeout(tryRestore, 120);
  window.setTimeout(tryRestore, 320);
}

function findRestorableElement(focusKey?: string, rowKey?: string): HTMLElement | null {
  if (focusKey) {
    const byKey = document.querySelector<HTMLElement>(
      `[data-gamepad-focus-key="${escapeCssAttributeValue(focusKey)}"]:not([disabled])`,
    );
    if (byKey && isElementVisible(byKey) && !isElementDisabled(byKey)) return byKey;
  }

  if (!rowKey) return null;
  const row = document.querySelector<HTMLElement>(
    `${GAMEPAD_ROW_SELECTOR}[data-gamepad-row-key="${escapeCssAttributeValue(rowKey)}"]`,
  );
  if (!row || !isElementVisible(row) || isElementDisabled(row)) return null;

  const favorite = row.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])');
  if (favorite && isElementVisible(favorite) && !isElementDisabled(favorite)) return favorite;
  return row;
}

function escapeCssAttributeValue(value: string): string {
  return typeof CSS !== 'undefined' && typeof CSS.escape === 'function'
    ? CSS.escape(value)
    : value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}

function dispatchEscape() {
  const target = getActiveHTMLElement() ?? document.body;
  target.dispatchEvent(new KeyboardEvent('keydown', {
    key: 'Escape',
    code: 'Escape',
    bubbles: true,
    cancelable: true,
  }));
}

function scrollActiveContainer(delta: number) {
  const active = getActiveHTMLElement();
  const scrollTarget = findScrollableParent(active) ?? document.scrollingElement ?? document.documentElement;
  scrollTarget.scrollBy({ top: delta, behavior: 'smooth' });
}

function findScrollableParent(element: HTMLElement | null): HTMLElement | null {
  let current = element?.parentElement ?? null;
  while (current && current !== document.body) {
    const style = window.getComputedStyle(current);
    const canScroll = /(auto|scroll)/.test(style.overflowY) && current.scrollHeight > current.clientHeight;
    if (canScroll) return current;
    current = current.parentElement;
  }
  return null;
}
