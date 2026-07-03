import { useEffect, useRef } from 'react';

const FIRST_REPEAT_DELAY_MS = 360;
const REPEAT_DELAY_MS = 140;
const DEFAULT_TOGGLE_COOLDOWN_MS = 800;
const AXIS_THRESHOLD = 0.55;
const BUTTON_PRESS_THRESHOLD = 0.5;
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
  '[data-slot="segmented-control"] label',
  '[data-gamepad-focusable="true"]',
  '[data-gamepad-clickable="true"]',
].join(',');
const GAMEPAD_SCOPE_SELECTOR = '[data-gamepad-scope]';
const GAMEPAD_ROW_SELECTOR = '[data-gamepad-row="true"]';
const GAMEPAD_SLIDER_SELECTOR = '[data-gamepad-slider="true"]';
const GAMEPAD_AXIS_X_SELECTOR = '[data-gamepad-axis="x"]';
const COMBOBOX_ROOT_SELECTOR = '[data-slot="select"], [data-slot="multi-select"]';
const SEGMENTED_CONTROL_SELECTOR = '[data-slot="segmented-control"]';
const TABS_LIST_SELECTOR = '[data-slot="tabs-list"]';
const TABS_ROOT_SELECTOR = '[data-slot="tabs"]';
const TABS_TRIGGER_SELECTOR = '[data-slot="tabs-trigger"]';
const TABS_CONTENT_SELECTOR = '[data-slot="tabs-content"]';
const TAB_SELECTOR = '[data-gamepad-tab="true"]';

type GamepadDirection = 'up' | 'down' | 'left' | 'right';
type GamepadAction = GamepadDirection | 'confirm' | 'back' | 'favorite' | 'focus' | 'compact' | 'previousTab' | 'nextTab' | 'scrollUp' | 'scrollDown';

const GAMEPAD_ACTIONS: readonly GamepadAction[] = [
  'up',
  'down',
  'left',
  'right',
  'confirm',
  'back',
  'favorite',
  'focus',
  'compact',
  'previousTab',
  'nextTab',
  'scrollUp',
  'scrollDown',
];

interface GamepadActionState {
  pressed: boolean;
  nextAt: number;
}

interface GamepadActionSnapshot {
  pressed: boolean;
  justPressed: boolean;
  justReleased: boolean;
  shouldRepeat: boolean;
}

interface GamepadInputState {
  actions: Record<GamepadAction, GamepadActionState>;
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
    const inputState = createGamepadInputState();

    const setGamepadMode = () => {
      document.body.dataset.gamepadNavigation = 'active';
    };

    const clearHighlight = () => {
      if (highlightedElement) {
        highlightedElement.removeAttribute('data-gamepad-focus');
        if (highlightedElement.dataset.gamepadManagedTabindex === 'true') {
          highlightedElement.removeAttribute('tabindex');
          delete highlightedElement.dataset.gamepadManagedTabindex;
        }
        highlightedElement = null;
      }
    };

    const focusElement = (element: HTMLElement) => {
      setGamepadMode();
      clearHighlight();
      ensureProgrammaticFocusTarget(element);
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
          activateFocusedElement(focusElement, () => highlightedElement);
          return;
        case 'favorite':
          activateFavoriteAction(focusElement, () => highlightedElement);
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
      if (updateRepeatingAction(inputState, action, pressed, now).shouldRepeat) runAction(action);
    };

    const triggerEdgeAction = (action: GamepadAction, pressed: boolean) => {
      if (updateEdgeAction(inputState, action, pressed).justPressed) runAction(action);
    };

    const poll = () => {
      if (disposed) return;

      const gamepad = getPrimaryGamepad();
      if (enabled && gamepad && document.hasFocus() && document.visibilityState === 'visible') {
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

        if (updateEdgeAction(inputState, 'focus', isButtonPressed(gamepad, BUTTON_RIGHT_STICK)).justPressed) {
          requestWindowToggle();
        }
      } else {
        resetGamepadInputState(inputState);
      }

      animationFrame = window.requestAnimationFrame(poll);
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'F8' || event.repeat) return;
      event.preventDefault();
      requestWindowToggle();
    };

    const handlePointerDown = () => {
      resetGamepadInputState(inputState);
      document.body.removeAttribute('data-gamepad-navigation');
      clearHighlight();
    };

    const resetTransientInputState = () => {
      resetGamepadInputState(inputState);
    };

    const handleVisibilityChange = () => {
      if (document.visibilityState !== 'visible') resetTransientInputState();
    };

    window.addEventListener('keydown', handleKeyDown);
    window.addEventListener('pointerdown', handlePointerDown, true);
    window.addEventListener('blur', resetTransientInputState);
    window.addEventListener('gamepaddisconnected', resetTransientInputState);
    document.addEventListener('visibilitychange', handleVisibilityChange);
    animationFrame = window.requestAnimationFrame(poll);

    return () => {
      disposed = true;
      window.removeEventListener('keydown', handleKeyDown);
      window.removeEventListener('pointerdown', handlePointerDown, true);
      window.removeEventListener('blur', resetTransientInputState);
      window.removeEventListener('gamepaddisconnected', resetTransientInputState);
      document.removeEventListener('visibilitychange', handleVisibilityChange);
      if (animationFrame) window.cancelAnimationFrame(animationFrame);
      document.body.removeAttribute('data-gamepad-navigation');
      clearHighlight();
    };
  }, [enabled]);
}

function createGamepadInputState(): GamepadInputState {
  const actions = Object.fromEntries(
    GAMEPAD_ACTIONS.map((action) => [action, { pressed: false, nextAt: 0 }]),
  ) as Record<GamepadAction, GamepadActionState>;
  return { actions };
}

function resetGamepadInputState(inputState: GamepadInputState) {
  for (const action of GAMEPAD_ACTIONS) {
    inputState.actions[action].pressed = false;
    inputState.actions[action].nextAt = 0;
  }
}

function updateEdgeAction(
  inputState: GamepadInputState,
  action: GamepadAction,
  pressed: boolean,
): GamepadActionSnapshot {
  const state = inputState.actions[action];
  const wasPressed = state.pressed;
  state.pressed = pressed;
  state.nextAt = pressed ? Number.POSITIVE_INFINITY : 0;
  return {
    pressed,
    justPressed: pressed && !wasPressed,
    justReleased: !pressed && wasPressed,
    shouldRepeat: false,
  };
}

function updateRepeatingAction(
  inputState: GamepadInputState,
  action: GamepadAction,
  pressed: boolean,
  now: number,
): GamepadActionSnapshot {
  const state = inputState.actions[action];
  const wasPressed = state.pressed;
  let shouldRepeat = false;

  if (pressed) {
    shouldRepeat = !wasPressed || now >= state.nextAt;
    if (shouldRepeat) {
      state.nextAt = now + (wasPressed ? REPEAT_DELAY_MS : FIRST_REPEAT_DELAY_MS);
    }
  } else {
    state.nextAt = 0;
  }

  state.pressed = pressed;
  return {
    pressed,
    justPressed: pressed && !wasPressed,
    justReleased: !pressed && wasPressed,
    shouldRepeat,
  };
}

function getPrimaryGamepad(): Gamepad | null {
  const gamepads = navigator.getGamepads?.() ?? [];
  return Array.from(gamepads).find((gamepad) => gamepad?.connected) ?? null;
}

function isButtonPressed(gamepad: Gamepad, index: number): boolean {
  const button = gamepad.buttons[index];
  return Boolean(button?.pressed || (button?.value ?? 0) > BUTTON_PRESS_THRESHOLD);
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
    .filter((element) => isElementVisible(element) && !isElementDisabled(element) && !isRedundantClickableWrapper(element));
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

function isUsableFocusedElement(element: HTMLElement | null): element is HTMLElement {
  return Boolean(
    element
      && element !== document.body
      && element !== document.documentElement
      && isElementVisible(element)
      && !isElementDisabled(element),
  );
}

function getActionTargetElement(getFallbackElement?: () => HTMLElement | null): HTMLElement | null {
  const active = getActiveHTMLElement();
  if (isUsableFocusedElement(active)) return active;

  const fallback = getFallbackElement?.() ?? null;
  if (isUsableFocusedElement(fallback)) return fallback;

  return null;
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
  if (!isUsableFocusedElement(active)) {
    focusFirstVisibleElement(focusElement);
    return;
  }

  if (adjustGamepadSlider(active, direction)) return;
  if (moveWithinCombobox(active, direction)) return;
  if (moveWithinTabsList(active, direction, focusElement)) return;
  if (moveWithinSegmentedControl(active, direction, focusElement)) return;
  if (moveWithinAxisGroup(active, direction, focusElement)) return;
  if (moveWithinTabsContent(active, direction, focusElement)) return;
  if (moveWithinContent(active, direction, focusElement)) return;

  const elements = getVisibleFocusableElements();
  if (moveGeometrically(active, direction, elements, focusElement)) return;

  scrollActiveContainer(direction === 'left' ? -SCROLL_STEP : SCROLL_STEP);
}

function moveWithinCombobox(
  active: HTMLElement,
  direction: GamepadDirection,
): boolean {
  if (!isComboboxControl(active)) return false;
  const expanded = isComboboxExpanded(active);
  if (!expanded) return false;

  if (direction === 'up' || direction === 'down') {
    dispatchComboboxKey(active, direction === 'up' ? 'ArrowUp' : 'ArrowDown');
    return true;
  }

  return true;
}

function moveWithinTabsList(
  active: HTMLElement,
  direction: GamepadDirection,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const tabsList = active.closest<HTMLElement>(TABS_LIST_SELECTOR);
  if (!tabsList || !active.matches(TABS_TRIGGER_SELECTOR)) return false;

  if (direction === 'left' || direction === 'right') {
    return moveWithinElementList(active, getTabTriggersWithin(tabsList), direction, focusElement);
  }

  if (direction === 'down') {
    return focusActiveTabsPanel(tabsList, focusElement) || focusFirstContentElement(focusElement);
  }

  return false;
}

function moveWithinSegmentedControl(
  active: HTMLElement,
  direction: GamepadDirection,
  focusElement: (element: HTMLElement) => void,
): boolean {
  if (direction !== 'left' && direction !== 'right') return false;

  const root = active.closest<HTMLElement>(SEGMENTED_CONTROL_SELECTOR);
  if (!root) return false;

  const options = getSegmentedControlOptions(root);
  if (options.length < 2) return false;

  const activeOption = getSegmentedControlOption(active, root);
  const currentIndex = activeOption ? options.indexOf(activeOption) : -1;
  if (currentIndex < 0) return false;

  const nextIndex = direction === 'left'
    ? Math.max(0, currentIndex - 1)
    : Math.min(options.length - 1, currentIndex + 1);
  const next = options[nextIndex];
  if (!next || next === activeOption) return true;

  next.click();
  focusElement(next);
  return true;
}

function moveWithinAxisGroup(
  active: HTMLElement,
  direction: GamepadDirection,
  focusElement: (element: HTMLElement) => void,
): boolean {
  if (direction !== 'left' && direction !== 'right') return false;

  const group = active.closest<HTMLElement>(GAMEPAD_AXIS_X_SELECTOR);
  if (!group) return false;

  const groupElements = getFocusableElementsWithin(group)
    .filter((element) => element.closest<HTMLElement>(GAMEPAD_AXIS_X_SELECTOR) === group);
  return moveWithinElementList(active, groupElements, direction, focusElement);
}

function moveWithinTabsContent(
  active: HTMLElement,
  direction: GamepadDirection,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const panel = active.closest<HTMLElement>(TABS_CONTENT_SELECTOR);
  if (!panel || !isElementVisible(panel)) return false;

  const panelElements = getTabsContentElements(panel);
  if (moveGeometrically(active, direction, panelElements, focusElement)) return true;

  if (direction === 'up') return focusTabForPanel(panel, focusElement);
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

  if (direction === 'up') return focusNearestActiveTab(active, focusElement) || focusActiveTab(focusElement);

  scrollActiveContainer(direction === 'left' ? -SCROLL_STEP : SCROLL_STEP);
  return true;
}

function adjustGamepadSlider(active: HTMLElement, direction: GamepadDirection): boolean {
  if (direction !== 'left' && direction !== 'right') return false;

  if (active.getAttribute('role') === 'slider') {
    dispatchElementKey(active, direction === 'left' ? 'ArrowLeft' : 'ArrowRight');
    return true;
  }

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

function moveWithinElementList(
  active: HTMLElement,
  elements: HTMLElement[],
  direction: 'left' | 'right',
  focusElement: (element: HTMLElement) => void,
): boolean {
  if (elements.length < 2) return false;

  const currentIndex = elements.indexOf(active);
  if (currentIndex < 0) return false;

  const nextIndex = direction === 'left'
    ? Math.max(0, currentIndex - 1)
    : Math.min(elements.length - 1, currentIndex + 1);
  const next = elements[nextIndex];
  if (!next || next === active) return true;
  focusElement(next);
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
  const candidates = elements
    .filter((element) => element !== active)
    .map((element) => {
      const rect = element.getBoundingClientRect();
      return {
        element,
        score: directionalScore(direction, activeRect, rect),
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
    .filter((element) => isElementVisible(element) && !isElementDisabled(element) && !isRedundantClickableWrapper(element));
}

function getScopeRoot(element: HTMLElement): HTMLElement | null {
  return element.closest<HTMLElement>(GAMEPAD_SCOPE_SELECTOR);
}

function getTabElements(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>(TAB_SELECTOR))
    .filter((element) => isElementVisible(element) && !isElementDisabled(element));
}

function getTabTriggersWithin(tabsList: HTMLElement): HTMLElement[] {
  return Array.from(tabsList.querySelectorAll<HTMLElement>(TABS_TRIGGER_SELECTOR))
    .filter((element) =>
      element.closest<HTMLElement>(TABS_LIST_SELECTOR) === tabsList
      && isElementVisible(element)
      && !isElementDisabled(element)
    );
}

function getSegmentedControlOptions(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>('label'))
    .filter((element) =>
      element.closest<HTMLElement>(SEGMENTED_CONTROL_SELECTOR) === root
      && getSegmentedControlInput(element, root)
      && isElementVisible(element)
      && !isElementDisabled(element)
    );
}

function getSegmentedControlOption(element: HTMLElement, root: HTMLElement): HTMLElement | null {
  const option = element.closest<HTMLElement>('label');
  if (!option || option.closest<HTMLElement>(SEGMENTED_CONTROL_SELECTOR) !== root) return null;
  return getSegmentedControlInput(option, root) ? option : null;
}

function getSegmentedControlInput(option: HTMLElement, root: HTMLElement): HTMLInputElement | null {
  const input = option instanceof HTMLLabelElement && option.control instanceof HTMLInputElement
    ? option.control
    : null;
  if (!input || input.disabled || input.closest<HTMLElement>(SEGMENTED_CONTROL_SELECTOR) !== root) return null;
  return input;
}

function focusActiveTabsPanel(
  tabsList: HTMLElement,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const selectedTab = getSelectedTabWithin(tabsList);
  if (!selectedTab) return false;

  const controlledPanel = getControlledTabsPanel(selectedTab);
  if (controlledPanel && focusFirstWithinPanel(controlledPanel, focusElement)) return true;

  const tabsRoot = tabsList.closest<HTMLElement>(TABS_ROOT_SELECTOR);
  if (!tabsRoot) return false;

  const visiblePanel = Array.from(tabsRoot.querySelectorAll<HTMLElement>(TABS_CONTENT_SELECTOR))
    .find((panel) =>
      panel.closest<HTMLElement>(TABS_ROOT_SELECTOR) === tabsRoot
      && isElementVisible(panel)
    );
  return visiblePanel ? focusFirstWithinPanel(visiblePanel, focusElement) : false;
}

function getSelectedTabWithin(tabsList: HTMLElement): HTMLElement | null {
  return getTabTriggersWithin(tabsList).find(isSelectedTab) ?? null;
}

function isSelectedTab(element: HTMLElement): boolean {
  return element.hasAttribute('data-active') || element.getAttribute('aria-selected') === 'true';
}

function getControlledTabsPanel(tab: HTMLElement): HTMLElement | null {
  const controls = tab.getAttribute('aria-controls');
  if (!controls) return null;

  const panel = document.getElementById(controls);
  if (!(panel instanceof HTMLElement)) return null;
  return isElementVisible(panel) ? panel : null;
}

function focusFirstWithinPanel(
  panel: HTMLElement,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const first = getTabsContentElements(panel)[0];
  if (!first) return false;
  focusElement(first);
  return true;
}

function getTabsContentElements(panel: HTMLElement): HTMLElement[] {
  return getFocusableElementsWithin(panel)
    .filter((element) => element.closest<HTMLElement>(TABS_CONTENT_SELECTOR) === panel);
}

function focusNearestActiveTab(
  element: HTMLElement,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const panel = element.closest<HTMLElement>(TABS_CONTENT_SELECTOR);
  if (panel && focusTabForPanel(panel, focusElement)) return true;

  const tabsRoot = element.closest<HTMLElement>(TABS_ROOT_SELECTOR);
  const tabsList = tabsRoot?.querySelector<HTMLElement>(TABS_LIST_SELECTOR) ?? null;
  const selectedTab = tabsList ? getSelectedTabWithin(tabsList) : null;
  if (!selectedTab) return false;
  focusElement(selectedTab);
  return true;
}

function focusTabForPanel(
  panel: HTMLElement,
  focusElement: (element: HTMLElement) => void,
): boolean {
  const panelId = panel.id;
  if (!panelId) return false;

  const tabsRoot = panel.closest<HTMLElement>(TABS_ROOT_SELECTOR);
  if (!tabsRoot) return false;

  const tab = Array.from(tabsRoot.querySelectorAll<HTMLElement>(TABS_TRIGGER_SELECTOR))
    .find((element) =>
      element.closest<HTMLElement>(TABS_ROOT_SELECTOR) === tabsRoot
      && element.getAttribute('aria-controls') === panelId
      && isElementVisible(element)
      && !isElementDisabled(element)
    );
  if (!tab) return false;
  focusElement(tab);
  return true;
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
  fromRect: DOMRect,
  toRect: DOMRect,
): number {
  const from = rectCenter(fromRect);
  const to = rectCenter(toRect);
  const primaryThreshold = 4;
  const dx = to.x - from.x;
  const dy = to.y - from.y;
  const major = Math.abs(direction === 'left' || direction === 'right' ? dx : dy);
  const cross = Math.abs(direction === 'left' || direction === 'right' ? dy : dx);
  const aligned = direction === 'left' || direction === 'right'
    ? isCrossAxisAligned(fromRect.top, fromRect.bottom, toRect.top, toRect.bottom, from.y, to.y)
    : isCrossAxisAligned(fromRect.left, fromRect.right, toRect.left, toRect.right, from.x, to.x);
  const alignmentPenalty = aligned ? 0 : 1_000_000;

  switch (direction) {
    case 'up':
      return dy < -primaryThreshold ? alignmentPenalty + major * 3 + cross : Number.POSITIVE_INFINITY;
    case 'down':
      return dy > primaryThreshold ? alignmentPenalty + major * 3 + cross : Number.POSITIVE_INFINITY;
    case 'left':
      return dx < -primaryThreshold ? alignmentPenalty + major * 3 + cross : Number.POSITIVE_INFINITY;
    case 'right':
      return dx > primaryThreshold ? alignmentPenalty + major * 3 + cross : Number.POSITIVE_INFINITY;
  }
}

function isCrossAxisAligned(
  fromStart: number,
  fromEnd: number,
  toStart: number,
  toEnd: number,
  fromCenter: number,
  toCenter: number,
): boolean {
  const overlap = Math.min(fromEnd, toEnd) - Math.max(fromStart, toStart);
  if (overlap > 0) return true;

  const fromSize = Math.max(1, fromEnd - fromStart);
  const toSize = Math.max(1, toEnd - toStart);
  return Math.abs(toCenter - fromCenter) <= Math.max(fromSize, toSize) * 0.75;
}

function activateFocusedElement(
  focusElement: (element: HTMLElement) => void,
  getFallbackElement?: () => HTMLElement | null,
) {
  const active = getActionTargetElement(getFallbackElement);
  if (!active) {
    focusFirstVisibleElement(focusElement);
    return;
  }

  if (getActiveHTMLElement() !== active) focusElement(active);

  if (isComboboxControl(active)) {
    activateComboboxControl(active);
    return;
  }

  const favoriteButton = active.dataset.gamepadRow === 'true'
    ? active.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])')
    : null;

  const target = favoriteButton ?? active;
  restoreFocusAfterAction(target, focusElement);
  target.click();
}

function ensureProgrammaticFocusTarget(element: HTMLElement) {
  if (canReceiveProgrammaticFocus(element)) return;
  element.tabIndex = -1;
  element.dataset.gamepadManagedTabindex = 'true';
}

function canReceiveProgrammaticFocus(element: HTMLElement): boolean {
  return element.matches([
    'button:not([disabled])',
    'input:not([disabled])',
    'textarea:not([disabled])',
    'select:not([disabled])',
    'a[href]',
    '[tabindex]',
    '[contenteditable="true"]',
  ].join(','));
}

function isRedundantClickableWrapper(element: HTMLElement): boolean {
  if (element.dataset.gamepadClickable !== 'true' || canReceiveProgrammaticFocus(element)) return false;
  return Array.from(element.querySelectorAll<HTMLElement>([
    'button:not([disabled])',
    'input:not([disabled])',
    'textarea:not([disabled])',
    'select:not([disabled])',
    'a[href]',
    '[tabindex]:not([tabindex="-1"])',
  ].join(','))).some((child) => isElementVisible(child) && !isElementDisabled(child));
}

function isComboboxControl(element: HTMLElement): boolean {
  return Boolean(element.closest(COMBOBOX_ROOT_SELECTOR));
}

function isComboboxExpanded(element: HTMLElement): boolean {
  const root = element.closest<HTMLElement>(COMBOBOX_ROOT_SELECTOR);
  if (!root) return false;
  if (root.getAttribute('aria-expanded') === 'true' && isElementVisible(root)) return true;

  const expandedControl = root.querySelector<HTMLElement>('[aria-expanded="true"]');
  if (expandedControl && isElementVisible(expandedControl)) return true;

  return Array.from(document.querySelectorAll<HTMLElement>('[role="listbox"]')).some(isElementVisible);
}

function activateComboboxControl(element: HTMLElement) {
  const root = element.closest<HTMLElement>(COMBOBOX_ROOT_SELECTOR);
  const target = root?.querySelector<HTMLElement>('[aria-haspopup="listbox"], input, button') ?? element;
  target.focus({ preventScroll: true });
  dispatchComboboxKey(target, 'Enter');
}

function dispatchComboboxKey(element: HTMLElement, key: 'ArrowUp' | 'ArrowDown' | 'Enter') {
  dispatchElementKey(element, key);
}

function dispatchElementKey(element: HTMLElement, key: 'ArrowLeft' | 'ArrowRight' | 'ArrowUp' | 'ArrowDown' | 'Enter') {
  const code = key === 'Enter' ? 'Enter' : key;
  for (const type of ['keydown', 'keyup']) {
    element.dispatchEvent(new KeyboardEvent(type, {
      key,
      code,
      bubbles: true,
      cancelable: true,
    }));
  }
}

function activateFavoriteAction(
  focusElement?: (element: HTMLElement) => void,
  getFallbackElement?: () => HTMLElement | null,
) {
  const active = getActionTargetElement(getFallbackElement);
  if (active && focusElement && getActiveHTMLElement() !== active) focusElement(active);

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

  const tryRestore = () => {
    const next = findRestorableElement(focusKey, rowKey) ?? findStillUsableElement(target);
    if (!next) return false;
    focusElement(next);
    return true;
  };

  window.setTimeout(tryRestore, 0);
  window.setTimeout(tryRestore, 120);
  window.setTimeout(tryRestore, 320);
}

function findStillUsableElement(element: HTMLElement): HTMLElement | null {
  if (!element.isConnected || !isUsableFocusedElement(element)) return null;
  return element;
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
