const SCROLL_STEP = 320;

const FOCUSABLE_SELECTOR = [
  'button',
  'input',
  'textarea',
  'select',
  'a[href]',
  '[tabindex]',
  '[role="button"]',
  '[role="switch"]',
  '[role="slider"]',
  '[role="treeitem"]',
  '[data-gamepad-focusable="true"]',
  '[data-gamepad-clickable="true"]',
  '[data-gamepad-scroll-region="true"]',
  '[data-slot="segmented-control"] label',
].join(',');
const GAMEPAD_SCOPE_SELECTOR = '[data-gamepad-scope]';
const GAMEPAD_ROW_SELECTOR = '[data-gamepad-row="true"]';
const GAMEPAD_AXIS_X_SELECTOR = '[data-gamepad-axis="x"]';
const GAMEPAD_SCROLL_REGION_SELECTOR = '[data-gamepad-scroll-region="true"]';
const COMBOBOX_ROOT_SELECTOR = '[data-slot="select"], [data-slot="multi-select"]';
const SEGMENTED_CONTROL_SELECTOR = '[data-slot="segmented-control"]';
const TABS_LIST_SELECTOR = '[data-slot="tabs-list"]';
const TABS_ROOT_SELECTOR = '[data-slot="tabs"]';
const TABS_TRIGGER_SELECTOR = '[data-slot="tabs-trigger"]';
const TABS_CONTENT_SELECTOR = '[data-slot="tabs-content"]';
const TAB_SELECTOR = '[data-gamepad-tab="true"]';
const MODAL_SCOPE_SELECTOR = '[data-gamepad-scope="modal"]';

export type GamepadDirection = 'up' | 'down' | 'left' | 'right';

interface FocusAnchor {
  element: HTMLElement;
  focusKey?: string;
  rowKey?: string;
  scrollKey?: string;
  scope: HTMLElement | null;
  scopeIndex: number;
}

interface PendingActionAnchor {
  anchor: FocusAnchor;
  epoch: number;
}

export class GamepadFocusManager {
  private highlightedElement: HTMLElement | null = null;
  private lastAnchor: FocusAnchor | null = null;
  private actionEpoch = 0;
  private reconciliationFrame = 0;
  private activeModalReturnFocusKey: string | null = null;
  private pendingActionAnchor: PendingActionAnchor | null = null;
  private readonly observer: MutationObserver;

  constructor() {
    this.observer = new MutationObserver(() => this.scheduleReconciliation());
    this.observer.observe(document.body, {
      subtree: true,
      childList: true,
      attributes: true,
      attributeFilter: ['disabled', 'aria-disabled', 'aria-hidden', 'data-disabled', 'hidden'],
    });
  }

  dispose() {
    this.actionEpoch += 1;
    this.observer.disconnect();
    if (this.reconciliationFrame) window.cancelAnimationFrame(this.reconciliationFrame);
    this.clearHighlight();
    document.body.removeAttribute('data-gamepad-navigation');
  }

  handlePointerInput() {
    this.leaveGamepadMode();
  }

  handleKeyboardInput() {
    this.leaveGamepadMode();
  }

  move(direction: GamepadDirection) {
    this.actionEpoch += 1;
    let active = this.getActionTarget();
    if (!active) {
      if (!this.focusFirstVisibleElement()) return;
      active = this.getActionTarget();
      if (!active) return;
    }

    if (this.adjustSlider(active, direction)) return;
    if (this.adjustNumberInput(active, direction)) return;
    if (this.moveWithinCombobox(active, direction)) return;
    if (this.moveWithinTree(active, direction)) return;
    if (this.moveWithinTabsList(active, direction)) return;
    if (this.moveWithinSegmentedControl(active, direction)) return;
    if (this.moveWithinAxisGroup(active, direction)) return;
    if (this.moveWithinScrollRegion(active, direction)) return;
    if (this.moveWithinTabsContent(active, direction)) return;
    if (this.moveWithinContent(active, direction)) return;

    this.moveGeometrically(active, direction, this.getVisibleFocusableElements());
  }

  confirm() {
    this.actionEpoch += 1;
    let active = this.getActionTarget();
    if (!active) {
      if (!this.focusFirstVisibleElement()) return;
      active = this.getActionTarget();
      if (!active) return;
    }
    if (this.getActiveElement() !== active) this.focus(active);

    if (this.isComboboxControl(active)) {
      this.activateComboboxControl(active);
      return;
    }
    if (active.matches('[role="treeitem"]')) {
      const label = active.querySelector<HTMLElement>('[data-value]');
      this.activateElement(label ?? active);
      return;
    }
    if (active.matches(GAMEPAD_SCROLL_REGION_SELECTOR)) return;

    const favoriteButton = active.dataset.gamepadRow === 'true'
      ? active.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])')
      : null;
    this.activateElement(favoriteButton ?? active);
  }

  favorite() {
    this.actionEpoch += 1;
    const active = this.getActionTarget();
    if (active && this.getActiveElement() !== active) this.focus(active);
    const favoriteButton = active?.matches('[data-gamepad-favorite="true"]')
      ? active
      : active?.closest(GAMEPAD_ROW_SELECTOR)?.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])')
        ?? active?.closest('[data-gamepad-favorite-scope="true"]')?.querySelector<HTMLElement>('[data-gamepad-favorite="true"]:not([disabled])');
    if (favoriteButton) this.activateElement(favoriteButton);
  }

  back(): boolean {
    this.actionEpoch += 1;
    const active = this.getActionTarget();
    if (active && this.isComboboxControl(active) && this.isComboboxExpanded(active)) {
      dispatchElementKey(getComboboxControl(active) ?? active, 'Escape');
      return true;
    }

    const modal = getVisibleModalScope();
    if (modal) {
      dispatchElementKey(this.getActiveElement() ?? modal, 'Escape');
      return true;
    }

    const panel = active?.closest<HTMLElement>(TABS_CONTENT_SELECTOR) ?? null;
    if (panel && this.focusTabForPanel(panel)) return true;
    if (active?.closest('[data-gamepad-scope="content"]') && this.focusActiveTab()) return true;
    return false;
  }

  scroll(delta: number) {
    this.actionEpoch += 1;
    const active = this.getActionTarget();
    const target = active?.matches(GAMEPAD_SCROLL_REGION_SELECTOR)
      ? active
      : active?.closest<HTMLElement>(GAMEPAD_SCROLL_REGION_SELECTOR)
        ?? findScrollableParent(active)
        ?? document.scrollingElement
        ?? document.documentElement;
    target.scrollBy({ top: delta, behavior: 'auto' });
  }

  focusTabByValue(value: string): boolean {
    const tab = getTabElements().find((element) => element.dataset.gamepadTabValue === value);
    if (!tab) return false;
    this.focus(tab);
    return true;
  }

  focusActiveTab(): boolean {
    const activeTab = getTabElements().find(isSelectedTab);
    if (!activeTab) return false;
    this.focus(activeTab);
    return true;
  }

  focusFirstVisibleElement(): boolean {
    const modal = getVisibleModalScope();
    if (modal) {
      const preferred = modal.querySelector<HTMLElement>('[data-gamepad-dialog-default="true"]');
      if (preferred && isEligibleFocusable(preferred)) {
        this.focus(preferred);
        return true;
      }
      const firstModalElement = getFocusableElementsWithin(modal)[0];
      if (firstModalElement) {
        this.focus(firstModalElement);
        return true;
      }
    }
    if (this.focusActiveTab()) return true;
    const first = this.getVisibleFocusableElements()[0];
    if (!first) return false;
    this.focus(first);
    return true;
  }

  private leaveGamepadMode() {
    this.actionEpoch += 1;
    this.pendingActionAnchor = null;
    this.activeModalReturnFocusKey = null;
    this.clearHighlight();
    document.body.removeAttribute('data-gamepad-navigation');
  }

  private focus(element: HTMLElement) {
    if (!isEligibleFocusable(element)) return;
    document.body.dataset.gamepadNavigation = 'active';
    this.clearHighlight();
    ensureProgrammaticFocusTarget(element);
    element.focus({ preventScroll: true });
    if (document.activeElement !== element) return;
    element.dataset.gamepadFocus = 'true';
    this.highlightedElement = element;
    this.lastAnchor = createFocusAnchor(element);
    element.scrollIntoView({ block: 'nearest', inline: 'nearest' });
  }

  private clearHighlight() {
    if (!this.highlightedElement) return;
    this.highlightedElement.removeAttribute('data-gamepad-focus');
    if (this.highlightedElement.dataset.gamepadManagedTabindex === 'true') {
      this.highlightedElement.removeAttribute('tabindex');
      delete this.highlightedElement.dataset.gamepadManagedTabindex;
    }
    this.highlightedElement = null;
  }

  private getActionTarget(): HTMLElement | null {
    const active = this.getActiveElement();
    if (isUsableFocusedElement(active)) return active;
    if (isUsableFocusedElement(this.highlightedElement)) return this.highlightedElement;
    return null;
  }

  private getActiveElement(): HTMLElement | null {
    return document.activeElement instanceof HTMLElement ? document.activeElement : null;
  }

  private getVisibleFocusableElements(): HTMLElement[] {
    const modal = getVisibleModalScope();
    return modal ? getFocusableElementsWithin(modal) : getFocusableElementsWithin(document.body);
  }

  private activateElement(target: HTMLElement) {
    const epoch = this.actionEpoch;
    const anchor = createFocusAnchor(target);
    const confirmFocusKey = target.dataset.gamepadConfirmFocusKey;
    this.pendingActionAnchor = { anchor, epoch };
    target.click();
    window.requestAnimationFrame(() => {
      if (epoch !== this.actionEpoch) return;
      if (getVisibleModalScope()) return;
      if (confirmFocusKey) {
        const root = anchor.scope?.isConnected ? anchor.scope : document.body;
        const confirmTarget = root.querySelector<HTMLElement>(
          `[data-gamepad-focus-key="${escapeCssAttributeValue(confirmFocusKey)}"]`,
        );
        if (confirmTarget && isEligibleFocusable(confirmTarget)) {
          this.pendingActionAnchor = null;
          this.focus(confirmTarget);
          return;
        }
      }
      const active = this.getActiveElement();
      if (isUsableFocusedElement(active)) {
        if (active === target) this.pendingActionAnchor = null;
        if (active !== this.highlightedElement) this.focus(active);
        return;
      }
      const next = resolveFocusAnchor(anchor);
      if (next) {
        this.focus(next);
      } else {
        if (anchor.scope && !anchor.scope.isConnected) this.pendingActionAnchor = null;
        this.focusFirstVisibleElement();
      }
    });
  }

  private scheduleReconciliation() {
    if (this.reconciliationFrame) return;
    this.reconciliationFrame = window.requestAnimationFrame(() => {
      this.reconciliationFrame = 0;
      if (document.body.dataset.gamepadNavigation !== 'active') return;
      const modal = getVisibleModalScope();
      if (modal) {
        this.activeModalReturnFocusKey = modal.dataset.gamepadReturnFocusKey ?? null;
      } else if (this.activeModalReturnFocusKey) {
        const returnTarget = document.querySelector<HTMLElement>(
          `[data-gamepad-focus-key="${escapeCssAttributeValue(this.activeModalReturnFocusKey)}"]`,
        );
        this.activeModalReturnFocusKey = null;
        this.pendingActionAnchor = null;
        if (returnTarget && isEligibleFocusable(returnTarget)) {
          this.focus(returnTarget);
          return;
        }
      }

      if (this.pendingActionAnchor?.epoch !== this.actionEpoch) {
        this.pendingActionAnchor = null;
      }
      if (!modal && this.pendingActionAnchor) {
        const pendingTarget = resolveExactFocusAnchor(this.pendingActionAnchor.anchor);
        if (pendingTarget) {
          this.pendingActionAnchor = null;
          this.focus(pendingTarget);
          return;
        }
        if (this.pendingActionAnchor.anchor.scope && !this.pendingActionAnchor.anchor.scope.isConnected) {
          this.pendingActionAnchor = null;
        }
      }

      const active = this.getActiveElement();
      if (isUsableFocusedElement(active)) {
        if (active !== this.highlightedElement) this.focus(active);
        return;
      }
      const next = this.lastAnchor ? resolveFocusAnchor(this.lastAnchor) : null;
      if (next) {
        this.focus(next);
      } else {
        this.focusFirstVisibleElement();
      }
    });
  }

  private adjustSlider(active: HTMLElement, direction: GamepadDirection): boolean {
    if (direction !== 'left' && direction !== 'right') return false;
    if (active.getAttribute('role') !== 'slider') return false;
    dispatchElementKey(active, direction === 'left' ? 'ArrowLeft' : 'ArrowRight');
    return true;
  }

  private adjustNumberInput(active: HTMLElement, direction: GamepadDirection): boolean {
    if (direction !== 'left' && direction !== 'right') return false;
    const control = active.closest<HTMLElement>('[data-gamepad-control="number-input"]');
    if (!control) return false;
    const input = active instanceof HTMLInputElement
      ? active
      : control.querySelector<HTMLInputElement>('input');
    if (!input) return false;
    dispatchElementKey(input, direction === 'left' ? 'ArrowDown' : 'ArrowUp');
    return true;
  }

  private moveWithinCombobox(active: HTMLElement, direction: GamepadDirection): boolean {
    if (!this.isComboboxControl(active) || !this.isComboboxExpanded(active)) return false;
    if (direction === 'up' || direction === 'down') {
      dispatchElementKey(
        getComboboxControl(active) ?? active,
        direction === 'up' ? 'ArrowUp' : 'ArrowDown',
      );
    }
    return true;
  }

  private moveWithinTree(active: HTMLElement, direction: GamepadDirection): boolean {
    const treeItem = active.closest<HTMLElement>('[role="treeitem"]');
    const tree = treeItem?.closest<HTMLElement>('[role="tree"]');
    if (!treeItem || !tree) return false;
    const items = Array.from(tree.querySelectorAll<HTMLElement>('[role="treeitem"]'))
      .filter(isEligibleFocusable);
    if (direction === 'up' || direction === 'down') {
      return this.moveWithinElementList(treeItem, items, direction === 'up' ? 'left' : 'right');
    }
    dispatchElementKey(treeItem, direction === 'left' ? 'ArrowLeft' : 'ArrowRight');
    return true;
  }

  private moveWithinTabsList(active: HTMLElement, direction: GamepadDirection): boolean {
    const tabsList = active.closest<HTMLElement>(TABS_LIST_SELECTOR);
    if (!tabsList || !active.matches(TABS_TRIGGER_SELECTOR)) return false;
    if (direction === 'left' || direction === 'right') {
      return this.moveWithinElementList(active, getTabTriggersWithin(tabsList), direction);
    }
    if (direction === 'down') {
      if (!isSelectedTab(active)) return true;
      return this.focusActiveTabsPanel(tabsList) || this.focusFirstContentElement();
    }
    return false;
  }

  private moveWithinSegmentedControl(active: HTMLElement, direction: GamepadDirection): boolean {
    if (direction !== 'left' && direction !== 'right') return false;
    const root = active.closest<HTMLElement>(SEGMENTED_CONTROL_SELECTOR);
    if (!root) return false;
    const options = getSegmentedControlOptions(root);
    const activeOption = active.closest<HTMLElement>('label');
    if (!activeOption || !options.includes(activeOption)) return false;
    const index = options.indexOf(activeOption);
    const next = options[direction === 'left' ? Math.max(0, index - 1) : Math.min(options.length - 1, index + 1)];
    if (!next || next === activeOption) return true;
    next.click();
    this.focus(next);
    return true;
  }

  private moveWithinAxisGroup(active: HTMLElement, direction: GamepadDirection): boolean {
    if (direction !== 'left' && direction !== 'right') return false;
    const group = active.closest<HTMLElement>(GAMEPAD_AXIS_X_SELECTOR);
    if (!group) return false;
    const elements = getFocusableElementsWithin(group)
      .filter((element) => element.closest<HTMLElement>(GAMEPAD_AXIS_X_SELECTOR) === group);
    return this.moveWithinElementList(active, elements, direction);
  }

  private moveWithinScrollRegion(active: HTMLElement, direction: GamepadDirection): boolean {
    if (!active.matches(GAMEPAD_SCROLL_REGION_SELECTOR)) return false;
    if (direction === 'left' || direction === 'right') return false;
    const delta = direction === 'up' ? -SCROLL_STEP : SCROLL_STEP;
    const atStart = active.scrollTop <= 0;
    const atEnd = active.scrollTop + active.clientHeight >= active.scrollHeight - 1;
    if ((direction === 'up' && atStart) || (direction === 'down' && atEnd)) return false;
    active.scrollBy({ top: delta, behavior: 'auto' });
    return true;
  }

  private moveWithinTabsContent(active: HTMLElement, direction: GamepadDirection): boolean {
    const panel = active.closest<HTMLElement>(TABS_CONTENT_SELECTOR);
    if (!panel || !isElementVisible(panel)) return false;
    if (this.moveGeometrically(active, direction, getTabsContentElements(panel))) return true;
    if (direction === 'up') return this.focusTabForPanel(panel);
    return false;
  }

  private moveWithinContent(active: HTMLElement, direction: GamepadDirection): boolean {
    const scope = active.closest<HTMLElement>(GAMEPAD_SCOPE_SELECTOR);
    if (!scope || scope.dataset.gamepadScope !== 'content') return false;
    if (this.moveWithinGamepadRow(active, direction)) return true;
    if (this.moveGeometrically(active, direction, getFocusableElementsWithin(scope))) return true;
    if (direction === 'up') return this.focusNearestActiveTab(active) || this.focusActiveTab();
    return true;
  }

  private moveWithinGamepadRow(active: HTMLElement, direction: GamepadDirection): boolean {
    if (direction !== 'left' && direction !== 'right') return false;
    const row = active.closest<HTMLElement>(GAMEPAD_ROW_SELECTOR);
    if (!row) return false;
    const elements = getFocusableElementsWithin(row)
      .filter((element) => element.closest<HTMLElement>(GAMEPAD_ROW_SELECTOR) === row);
    return this.moveWithinElementList(active, elements, direction);
  }

  private moveWithinElementList(
    active: HTMLElement,
    elements: HTMLElement[],
    direction: 'left' | 'right',
  ): boolean {
    const index = elements.indexOf(active);
    if (index < 0 || elements.length < 2) return false;
    const nextIndex = direction === 'left' ? Math.max(0, index - 1) : Math.min(elements.length - 1, index + 1);
    const next = elements[nextIndex];
    if (!next || next === active) return true;
    this.focus(next);
    return true;
  }

  private moveGeometrically(
    active: HTMLElement,
    direction: GamepadDirection,
    elements: HTMLElement[],
  ): boolean {
    const activeRect = active.getBoundingClientRect();
    const next = elements
      .filter((element) => element !== active)
      .map((element) => ({ element, score: directionalScore(direction, activeRect, element.getBoundingClientRect()) }))
      .filter((candidate) => Number.isFinite(candidate.score))
      .sort((left, right) => left.score - right.score)[0]?.element;
    if (!next) return false;
    this.focus(next);
    return true;
  }

  private focusActiveTabsPanel(tabsList: HTMLElement): boolean {
    const selected = getTabTriggersWithin(tabsList).find(isSelectedTab);
    if (!selected) return false;
    const controls = selected.getAttribute('aria-controls');
    const panel = controls ? document.getElementById(controls) : null;
    if (panel instanceof HTMLElement && isElementVisible(panel)) {
      const first = getTabsContentElements(panel)[0];
      if (first) {
        this.focus(first);
        return true;
      }
    }
    return false;
  }

  private focusFirstContentElement(): boolean {
    const scopes = Array.from(document.querySelectorAll<HTMLElement>('[data-gamepad-scope="content"]'))
      .filter(isElementVisible);
    for (const scope of scopes) {
      const first = getFocusableElementsWithin(scope)[0];
      if (!first) continue;
      this.focus(first);
      return true;
    }
    return false;
  }

  private focusTabForPanel(panel: HTMLElement): boolean {
    const root = panel.closest<HTMLElement>(TABS_ROOT_SELECTOR);
    if (!root || !panel.id) return false;
    const tab = Array.from(root.querySelectorAll<HTMLElement>(TABS_TRIGGER_SELECTOR))
      .find((element) => element.getAttribute('aria-controls') === panel.id && isEligibleFocusable(element));
    if (!tab) return false;
    this.focus(tab);
    return true;
  }

  private focusNearestActiveTab(active: HTMLElement): boolean {
    const panel = active.closest<HTMLElement>(TABS_CONTENT_SELECTOR);
    if (panel && this.focusTabForPanel(panel)) return true;
    return false;
  }

  private isComboboxControl(element: HTMLElement): boolean {
    return Boolean(element.closest(COMBOBOX_ROOT_SELECTOR));
  }

  private isComboboxExpanded(element: HTMLElement): boolean {
    const control = getComboboxControl(element);
    return Boolean(
      control
      && (control.dataset.expanded === 'true' || control.getAttribute('aria-expanded') === 'true'),
    );
  }

  private activateComboboxControl(element: HTMLElement) {
    const target = getComboboxControl(element) ?? element;
    if (this.getActiveElement() !== target) this.focus(target);
    const root = target.closest<HTMLElement>(COMBOBOX_ROOT_SELECTOR);
    const key = root?.matches('[data-slot="multi-select"]') && !this.isComboboxExpanded(target)
      ? 'ArrowDown'
      : 'Enter';
    dispatchElementKey(target, key);
  }
}

function getComboboxControl(element: HTMLElement): HTMLElement | null {
  const root = element.closest<HTMLElement>(COMBOBOX_ROOT_SELECTOR);
  if (!root) return null;
  if (root.matches('input, button, [role="combobox"]')) return root;
  return root.querySelector<HTMLElement>('input, button, [role="combobox"]');
}

function createFocusAnchor(element: HTMLElement): FocusAnchor {
  const scope = element.closest<HTMLElement>(GAMEPAD_SCOPE_SELECTOR);
  const elements = scope ? getFocusableElementsWithin(scope) : getFocusableElementsWithin(document.body);
  return {
    element,
    focusKey: element.dataset.gamepadFocusKey,
    rowKey: element.closest<HTMLElement>(GAMEPAD_ROW_SELECTOR)?.dataset.gamepadRowKey,
    scrollKey: element.closest<HTMLElement>(GAMEPAD_SCROLL_REGION_SELECTOR)?.dataset.gamepadScrollKey,
    scope,
    scopeIndex: Math.max(0, elements.indexOf(element)),
  };
}

function resolveFocusAnchor(anchor: FocusAnchor): HTMLElement | null {
  const modal = getVisibleModalScope();
  const connectedScope = anchor.scope?.isConnected ? anchor.scope : null;
  const root = modal ?? connectedScope ?? document.body;
  if (anchor.focusKey) {
    const target = root.querySelector<HTMLElement>(
      `[data-gamepad-focus-key="${escapeCssAttributeValue(anchor.focusKey)}"]`,
    );
    if (target && isEligibleFocusable(target)) return target;
  }
  if (anchor.rowKey) {
    const row = root.querySelector<HTMLElement>(
      `${GAMEPAD_ROW_SELECTOR}[data-gamepad-row-key="${escapeCssAttributeValue(anchor.rowKey)}"]`,
    );
    if (row && isEligibleFocusable(row)) return row;
  }
  if (anchor.scrollKey) {
    const region = root.querySelector<HTMLElement>(
      `${GAMEPAD_SCROLL_REGION_SELECTOR}[data-gamepad-scroll-key="${escapeCssAttributeValue(anchor.scrollKey)}"]`,
    );
    if (region && isEligibleFocusable(region)) return region;
  }
  if (anchor.element.isConnected && isEligibleFocusable(anchor.element)) return anchor.element;
  if (modal || !connectedScope) return null;
  const elements = getFocusableElementsWithin(connectedScope);
  return elements[Math.min(anchor.scopeIndex, Math.max(0, elements.length - 1))] ?? null;
}

function resolveExactFocusAnchor(anchor: FocusAnchor): HTMLElement | null {
  const root = anchor.scope?.isConnected ? anchor.scope : document.body;
  if (anchor.focusKey) {
    const target = root.querySelector<HTMLElement>(
      `[data-gamepad-focus-key="${escapeCssAttributeValue(anchor.focusKey)}"]`,
    );
    if (target && isEligibleFocusable(target)) return target;
  }
  if (anchor.element.isConnected && isEligibleFocusable(anchor.element)) return anchor.element;
  return null;
}

function getFocusableElementsWithin(root: HTMLElement): HTMLElement[] {
  const elements = Array.from(root.querySelectorAll<HTMLElement>(FOCUSABLE_SELECTOR));
  if (root.matches(FOCUSABLE_SELECTOR)) elements.unshift(root);
  return elements.filter(isEligibleFocusable).filter((element, index, all) => all.indexOf(element) === index);
}

function isEligibleFocusable(element: HTMLElement): boolean {
  if (!isElementVisible(element) || isElementDisabled(element)) return false;
  if (element.closest('[inert], [aria-hidden="true"]')) return false;
  if (element.getAttribute('aria-hidden') === 'true') return false;

  const explicit = element.matches([
    '[role="treeitem"]',
    TABS_TRIGGER_SELECTOR,
    '[data-gamepad-focusable="true"]',
    '[data-gamepad-clickable="true"]',
    GAMEPAD_SCROLL_REGION_SELECTOR,
    `${SEGMENTED_CONTROL_SELECTOR} label`,
  ].join(','));
  if (!explicit && element.tabIndex < 0) return false;
  if (element.dataset.gamepadClickable === 'true' && isRedundantClickableWrapper(element)) return false;
  if (element.matches(`${SEGMENTED_CONTROL_SELECTOR} label`)) {
    const input = element instanceof HTMLLabelElement ? element.control : null;
    if (!(input instanceof HTMLInputElement) || input.disabled) return false;
  }
  return true;
}

function isElementVisible(element: HTMLElement): boolean {
  const rect = element.getBoundingClientRect();
  const style = window.getComputedStyle(element);
  return rect.width > 0 && rect.height > 0 && style.visibility !== 'hidden' && style.display !== 'none';
}

function isElementDisabled(element: HTMLElement): boolean {
  return element.hasAttribute('disabled')
    || element.getAttribute('aria-disabled') === 'true'
    || element.dataset.disabled === 'true'
    || Boolean(element.closest('[data-disabled="true"], [aria-disabled="true"]'));
}

function isUsableFocusedElement(element: HTMLElement | null): element is HTMLElement {
  return Boolean(element && element !== document.body && element !== document.documentElement && isEligibleFocusable(element));
}

function isRedundantClickableWrapper(element: HTMLElement): boolean {
  if (element.matches(GAMEPAD_ROW_SELECTOR)) return false;
  return Array.from(element.querySelectorAll<HTMLElement>('button, input, textarea, select, a[href], [tabindex]'))
    .some((child) => child !== element && isEligibleFocusable(child));
}

function ensureProgrammaticFocusTarget(element: HTMLElement) {
  if (element.matches([
    'button',
    'input',
    'textarea',
    'select',
    'a[href]',
    '[tabindex]',
    '[contenteditable="true"]',
  ].join(','))) return;
  element.tabIndex = -1;
  element.dataset.gamepadManagedTabindex = 'true';
}

function getTabElements(): HTMLElement[] {
  return Array.from(document.querySelectorAll<HTMLElement>(TAB_SELECTOR)).filter(isEligibleFocusable);
}

function getTabTriggersWithin(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>(TABS_TRIGGER_SELECTOR))
    .filter((element) => element.closest<HTMLElement>(TABS_LIST_SELECTOR) === root && isEligibleFocusable(element));
}

function getTabsContentElements(panel: HTMLElement): HTMLElement[] {
  return getFocusableElementsWithin(panel)
    .filter((element) => element.closest<HTMLElement>(TABS_CONTENT_SELECTOR) === panel);
}

function getSegmentedControlOptions(root: HTMLElement): HTMLElement[] {
  return Array.from(root.querySelectorAll<HTMLElement>('label'))
    .filter((element) => element.closest<HTMLElement>(SEGMENTED_CONTROL_SELECTOR) === root && isEligibleFocusable(element));
}

function getVisibleModalScope(): HTMLElement | null {
  return Array.from(document.querySelectorAll<HTMLElement>(MODAL_SCOPE_SELECTOR)).find(isElementVisible) ?? null;
}

function isSelectedTab(element: HTMLElement): boolean {
  return element.hasAttribute('data-active') || element.getAttribute('aria-selected') === 'true';
}

function findScrollableParent(element: HTMLElement | null): HTMLElement | null {
  let current = element?.parentElement ?? null;
  while (current && current !== document.body) {
    const style = window.getComputedStyle(current);
    if (/(auto|scroll)/.test(style.overflowY) && current.scrollHeight > current.clientHeight) return current;
    current = current.parentElement;
  }
  return null;
}

function directionalScore(direction: GamepadDirection, fromRect: DOMRect, toRect: DOMRect): number {
  const fromX = fromRect.left + fromRect.width / 2;
  const fromY = fromRect.top + fromRect.height / 2;
  const toX = toRect.left + toRect.width / 2;
  const toY = toRect.top + toRect.height / 2;
  const dx = toX - fromX;
  const dy = toY - fromY;
  const horizontal = direction === 'left' || direction === 'right';
  const major = Math.abs(horizontal ? dx : dy);
  const cross = Math.abs(horizontal ? dy : dx);
  const aligned = horizontal
    ? isCrossAxisAligned(fromRect.top, fromRect.bottom, toRect.top, toRect.bottom, fromY, toY)
    : isCrossAxisAligned(fromRect.left, fromRect.right, toRect.left, toRect.right, fromX, toX);
  const penalty = aligned ? 0 : 1_000_000;
  if (direction === 'up') return dy < -4 ? penalty + major * 3 + cross : Number.POSITIVE_INFINITY;
  if (direction === 'down') return dy > 4 ? penalty + major * 3 + cross : Number.POSITIVE_INFINITY;
  if (direction === 'left') return dx < -4 ? penalty + major * 3 + cross : Number.POSITIVE_INFINITY;
  return dx > 4 ? penalty + major * 3 + cross : Number.POSITIVE_INFINITY;
}

function isCrossAxisAligned(
  fromStart: number,
  fromEnd: number,
  toStart: number,
  toEnd: number,
  fromCenter: number,
  toCenter: number,
): boolean {
  if (Math.min(fromEnd, toEnd) - Math.max(fromStart, toStart) > 0) return true;
  return Math.abs(toCenter - fromCenter) <= Math.max(fromEnd - fromStart, toEnd - toStart, 1) * 0.75;
}

function dispatchElementKey(element: HTMLElement, key: string) {
  for (const type of ['keydown', 'keyup']) {
    element.dispatchEvent(new KeyboardEvent(type, { key, code: key, bubbles: true, cancelable: true }));
  }
}

function escapeCssAttributeValue(value: string): string {
  return typeof CSS !== 'undefined' && typeof CSS.escape === 'function'
    ? CSS.escape(value)
    : value.replace(/\\/g, '\\\\').replace(/"/g, '\\"');
}
