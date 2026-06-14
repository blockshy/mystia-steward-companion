import { Tabs as MantineTabs } from '@mantine/core';
import type { ComponentProps } from 'react';

import { cn } from "@/lib/utils"

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
      variant="pills"
      color="steward"
      orientation={orientation}
      className={cn("group/tabs flex gap-2", orientation === "vertical" ? "flex-row" : "flex-col", className)}
      onChange={onValueChange}
      {...props}
    />
  )
}

function TabsList({
  className,
  variant = "default",
  ...props
}: ComponentProps<typeof MantineTabs.List> & { variant?: 'default' | 'line' }) {
  return (
    <MantineTabs.List
      data-slot="tabs-list"
      data-variant={variant}
      className={cn('steward-tabs-list group/tabs-list', variant === 'line' && 'steward-tabs-list-line', className)}
      {...props}
    />
  )
}

function TabsTrigger({ className, ...props }: ComponentProps<typeof MantineTabs.Tab>) {
  return (
    <MantineTabs.Tab
      data-slot="tabs-trigger"
      className={cn('steward-tabs-trigger', className)}
      {...props}
    />
  )
}

function TabsContent({ className, ...props }: ComponentProps<typeof MantineTabs.Panel>) {
  return (
    <MantineTabs.Panel
      data-slot="tabs-content"
      className={cn("flex-1 text-sm outline-none", className)}
      {...props}
    />
  )
}

export { Tabs, TabsList, TabsTrigger, TabsContent }
