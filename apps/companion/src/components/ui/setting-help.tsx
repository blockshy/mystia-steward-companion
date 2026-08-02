import { Tooltip } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import type {
  FocusEvent as ReactFocusEvent,
  Key,
  MouseEvent as ReactMouseEvent,
  ReactElement,
  ReactNode,
} from 'react';

import { composeClassNames } from '@/components/ui/style';

const HELP_OPEN_DELAY_MS = 150;
const HELP_CLOSE_DELAY_MS = 100;

type ActiveSettingHelp = {
  id: string;
  pinned: boolean;
  target: HTMLElement;
  description: ReactNode;
};

type SettingHelpContextValue = {
  activeHelp: ActiveSettingHelp | null;
  openHelp: (id: string, target: HTMLElement, description: ReactNode, pinned?: boolean) => void;
  closeHelp: (id?: string, preservePinned?: boolean) => void;
  togglePinnedHelp: (id: string, target: HTMLElement, description: ReactNode) => void;
  shouldOpenHelpForFocus: (target: EventTarget | null) => boolean;
};

const SettingHelpContext = createContext<SettingHelpContextValue | null>(null);

type SettingHelpProviderProps = {
  children: ReactNode;
  resetKey?: Key;
};

type SettingHelpState = {
  resetGeneration: symbol;
  activeHelp: ActiveSettingHelp | null;
};

function SettingHelpProvider({ children, resetKey }: SettingHelpProviderProps) {
  const inputModalityRef = useRef<'keyboard' | 'pointer'>('keyboard');
  const resetGeneration = useMemo(() => Symbol(`setting-help-session:${String(resetKey)}`), [resetKey]);
  const [helpState, setHelpState] = useState<SettingHelpState>({ resetGeneration, activeHelp: null });
  const activeHelp = helpState.resetGeneration === resetGeneration ? helpState.activeHelp : null;

  const openHelp = useCallback((id: string, target: HTMLElement, description: ReactNode, pinned = false) => {
    setHelpState((state) => {
      const current = state.resetGeneration === resetGeneration ? state.activeHelp : null;
      const nextPinned = current?.id === id ? current.pinned || pinned : pinned;
      if (
        current?.id === id
        && current.pinned === nextPinned
        && current.target === target
        && current.description === description
      ) {
        return state;
      }

      return { resetGeneration, activeHelp: { id, pinned: nextPinned, target, description } };
    });
  }, [resetGeneration]);

  const closeHelp = useCallback((id?: string, preservePinned = false) => {
    setHelpState((state) => {
      const current = state.resetGeneration === resetGeneration ? state.activeHelp : null;
      if (!current || (id !== undefined && current.id !== id) || (preservePinned && current.pinned)) {
        return state;
      }

      return { resetGeneration, activeHelp: null };
    });
  }, [resetGeneration]);

  const togglePinnedHelp = useCallback((id: string, target: HTMLElement, description: ReactNode) => {
    setHelpState((state) => {
      const current = state.resetGeneration === resetGeneration ? state.activeHelp : null;
      if (current?.id === id && current.pinned) {
        return { resetGeneration, activeHelp: null };
      }

      return { resetGeneration, activeHelp: { id, pinned: true, target, description } };
    });
  }, [resetGeneration]);

  const shouldOpenHelpForFocus = useCallback((target: EventTarget | null) => (
    document.body.dataset.gamepadNavigation === 'active'
    || (
      inputModalityRef.current === 'keyboard'
      && target instanceof HTMLElement
      && target.matches(':focus-visible')
    )
  ), []);

  useEffect(() => {
    const handlePointerInput = () => {
      inputModalityRef.current = 'pointer';
    };
    const handleKeyboardInput = () => {
      inputModalityRef.current = 'keyboard';
    };

    document.addEventListener('pointerdown', handlePointerInput, true);
    document.addEventListener('keydown', handleKeyboardInput, true);
    return () => {
      document.removeEventListener('pointerdown', handlePointerInput, true);
      document.removeEventListener('keydown', handleKeyboardInput, true);
    };
  }, []);

  useEffect(() => {
    if (!activeHelp) {
      return undefined;
    }

    const handlePointerDown = (event: PointerEvent) => {
      const target = event.target;
      const owningField = target instanceof Element ? target.closest<HTMLElement>('[data-setting-help-id]') : null;
      if (owningField?.dataset.settingHelpId !== activeHelp.id) {
        closeHelp(activeHelp.id);
      }
    };

    const handleKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        closeHelp(activeHelp.id);
      }
    };

    document.addEventListener('pointerdown', handlePointerDown, true);
    document.addEventListener('keydown', handleKeyDown, true);
    return () => {
      document.removeEventListener('pointerdown', handlePointerDown, true);
      document.removeEventListener('keydown', handleKeyDown, true);
    };
  }, [activeHelp, closeHelp]);

  const value = useMemo<SettingHelpContextValue>(
    () => ({ activeHelp, openHelp, closeHelp, togglePinnedHelp, shouldOpenHelpForFocus }),
    [activeHelp, closeHelp, openHelp, shouldOpenHelpForFocus, togglePinnedHelp],
  );

  return (
    <SettingHelpContext.Provider value={value}>
      {children}
      {activeHelp && (
        <Tooltip
          target={activeHelp.target}
          data-setting-help-tooltip="true"
          data-setting-help-id={activeHelp.id}
          classNames={{
            tooltip: 'steward-setting-help-tooltip',
            arrow: 'steward-setting-help-tooltip-arrow',
          }}
          label={<div className="steward-setting-help-copy">{activeHelp.description}</div>}
          opened
          events={{ hover: false, focus: false, touch: false }}
          position="top-start"
          offset={8}
          withArrow
          multiline
          withinPortal
          middlewares={{ flip: true, shift: { padding: 12 } }}
        />
      )}
    </SettingHelpContext.Provider>
  );
}

type SettingHelpRenderProps = {
  helpTrigger: ReactElement;
  descriptionId: string;
};

type SettingHelpFieldProps = {
  id: string;
  label: string;
  description: ReactNode;
  disabledControl?: boolean;
  className?: string;
  children: (props: SettingHelpRenderProps) => ReactNode;
};

function SettingHelpField({
  id,
  label,
  description,
  disabledControl = false,
  className,
  children,
}: SettingHelpFieldProps) {
  const context = useContext(SettingHelpContext);
  if (!context) {
    throw new Error('SettingHelpField must be rendered inside SettingHelpProvider.');
  }

  const { activeHelp, closeHelp, openHelp, shouldOpenHelpForFocus, togglePinnedHelp } = context;
  const fieldRef = useRef<HTMLDivElement | null>(null);
  const openTimerRef = useRef<number | null>(null);
  const closeTimerRef = useRef<number | null>(null);
  const triggerPointerTypeRef = useRef<string | null>(null);
  const triggerRef = useRef<HTMLButtonElement | null>(null);
  const isOpen = activeHelp?.id === id;
  const descriptionId = `setting-help-${id}-description`;

  const clearOpenTimer = useCallback(() => {
    if (openTimerRef.current !== null) {
      window.clearTimeout(openTimerRef.current);
      openTimerRef.current = null;
    }
  }, []);

  const clearCloseTimer = useCallback(() => {
    if (closeTimerRef.current !== null) {
      window.clearTimeout(closeTimerRef.current);
      closeTimerRef.current = null;
    }
  }, []);

  const scheduleOpen = useCallback(() => {
    clearCloseTimer();
    clearOpenTimer();
    openTimerRef.current = window.setTimeout(() => {
      openTimerRef.current = null;
      if (triggerRef.current) {
        openHelp(id, triggerRef.current, description);
      }
    }, HELP_OPEN_DELAY_MS);
  }, [clearCloseTimer, clearOpenTimer, description, id, openHelp]);

  const scheduleClose = useCallback(() => {
    clearOpenTimer();
    clearCloseTimer();
    closeTimerRef.current = window.setTimeout(() => {
      closeTimerRef.current = null;
      const focusedElement = document.activeElement;
      if (
        focusedElement instanceof Node
        && fieldRef.current?.contains(focusedElement)
        && shouldOpenHelpForFocus(focusedElement)
      ) {
        return;
      }
      closeHelp(id, true);
    }, HELP_CLOSE_DELAY_MS);
  }, [clearCloseTimer, clearOpenTimer, closeHelp, id, shouldOpenHelpForFocus]);

  useEffect(
    () => () => {
      clearOpenTimer();
      clearCloseTimer();
      closeHelp(id);
    },
    [clearCloseTimer, clearOpenTimer, closeHelp, id],
  );

  const handleFocusCapture = (event: ReactFocusEvent<HTMLDivElement>) => {
    if (!shouldOpenHelpForFocus(event.target)) {
      return;
    }

    clearOpenTimer();
    clearCloseTimer();
    if (triggerRef.current) {
      openHelp(id, triggerRef.current, description);
    }
  };

  const handleBlurCapture = (event: ReactFocusEvent<HTMLDivElement>) => {
    if (event.relatedTarget instanceof Node && event.currentTarget.contains(event.relatedTarget)) {
      return;
    }

    scheduleClose();
  };

  const handleTriggerClick = (event: ReactMouseEvent<HTMLButtonElement>) => {
    event.stopPropagation();
    const shouldPin = triggerPointerTypeRef.current === 'touch' || triggerPointerTypeRef.current === 'pen';
    triggerPointerTypeRef.current = null;
    if (!shouldPin) {
      return;
    }

    clearOpenTimer();
    clearCloseTimer();
    if (triggerRef.current) {
      togglePinnedHelp(id, triggerRef.current, description);
    }
  };

  const helpTrigger = (
    <button
      ref={triggerRef}
      type="button"
      className="steward-setting-help-trigger"
      data-setting-help-trigger="true"
      data-setting-help-id={id}
      data-gamepad-focusable={disabledControl ? 'true' : undefined}
      tabIndex={disabledControl ? 0 : -1}
      aria-label={`查看“${label}”说明`}
      aria-describedby={descriptionId}
      data-active={isOpen ? 'true' : undefined}
      onMouseEnter={scheduleOpen}
      onMouseLeave={scheduleClose}
      onPointerDown={(event) => {
        triggerPointerTypeRef.current = event.pointerType;
        event.stopPropagation();
      }}
      onPointerCancel={() => {
        triggerPointerTypeRef.current = null;
      }}
      onClick={handleTriggerClick}
    >
      <IconInfoCircle size={16} stroke={1.8} aria-hidden="true" />
    </button>
  );

  return (
    <div
      ref={fieldRef}
      className={composeClassNames('steward-setting-help-field', className)}
      data-setting-field="true"
      data-setting-help-id={id}
      onFocusCapture={handleFocusCapture}
      onBlurCapture={handleBlurCapture}
    >
      {children({ helpTrigger, descriptionId })}
      <div id={descriptionId} className="steward-setting-help-sr-only">
        {description}
      </div>
    </div>
  );
}

export { SettingHelpField, SettingHelpProvider };
export type { SettingHelpFieldProps, SettingHelpProviderProps, SettingHelpRenderProps };
