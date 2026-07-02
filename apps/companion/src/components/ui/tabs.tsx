import { Tabs as MantineTabs } from '@mantine/core';
import { useCallback, useEffect, useRef } from 'react';
import type { ComponentProps } from 'react';

import { composeClassNames } from '@/components/ui/style';

function Tabs({
  className,
  orientation = "horizontal",
  onValueChange,
  ...props
}: Omit<ComponentProps<typeof MantineTabs>, 'onChange'> & {
  onValueChange?: (value: string | null) => void;
}) {
  return (
    <MantineTabs
      data-slot="tabs"
      data-orientation={orientation}
      variant="default"
      color="steward"
      orientation={orientation}
      className={composeClassNames("group/tabs flex gap-2", orientation === "vertical" ? "flex-row" : "flex-col", className)}
      onChange={onValueChange}
      {...props}
    />
  )
}

type TabsListProps = ComponentProps<typeof MantineTabs.List> & {
  scrollable?: boolean;
};

function TabsList({ className, onFocus, onPointerUp, scrollable = false, ...props }: TabsListProps) {
  const listRef = useRef<HTMLDivElement | null>(null);

  const scrollTabIntoView = useCallback((target: EventTarget | HTMLElement | null) => {
    const element = target instanceof HTMLElement
      ? target.closest<HTMLElement>('[data-slot="tabs-trigger"]')
      : null;
    const tab = element ?? listRef.current?.querySelector<HTMLElement>('[data-slot="tabs-trigger"][data-active]');
    if (!tab) return;

    window.requestAnimationFrame(() => {
      tab.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    });
  }, []);

  useEffect(() => {
    if (!scrollable) return;
    const list = listRef.current;
    if (!list) return;

    scrollTabIntoView(null);
    const observer = new MutationObserver((mutations) => {
      if (mutations.some((mutation) => mutation.attributeName === 'data-active')) {
        scrollTabIntoView(null);
      }
    });

    observer.observe(list, { attributes: true, attributeFilter: ['data-active'], subtree: true });
    return () => observer.disconnect();
  }, [scrollable, scrollTabIntoView]);

  return (
    <MantineTabs.List
      ref={listRef}
      data-slot="tabs-list"
      data-scrollable-tabs={scrollable ? 'true' : undefined}
      data-gamepad-axis="x"
      className={composeClassNames('steward-tabs-list group/tabs-list', className)}
      onFocus={(event) => {
        scrollTabIntoView(event.target);
        onFocus?.(event);
      }}
      onPointerUp={(event) => {
        scrollTabIntoView(event.target);
        onPointerUp?.(event);
      }}
      {...props}
    />
  )
}

function TabsTrigger({ className, ...props }: ComponentProps<typeof MantineTabs.Tab>) {
  return (
    <MantineTabs.Tab
      data-slot="tabs-trigger"
      className={composeClassNames('steward-tabs-trigger', className)}
      {...props}
    />
  )
}

function TabsContent({ className, ...props }: ComponentProps<typeof MantineTabs.Panel>) {
  return (
    <MantineTabs.Panel
      data-slot="tabs-content"
      data-gamepad-scope="content"
      className={composeClassNames("steward-tabs-content flex-1 text-sm outline-none", className)}
      {...props}
    />
  )
}

export { Tabs, TabsList, TabsTrigger, TabsContent }
