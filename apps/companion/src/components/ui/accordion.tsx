import { Accordion as MantineAccordion } from '@mantine/core';
import type { ReactNode } from 'react';

import { cn } from '@/lib/utils';

type AccordionProps = React.ComponentProps<typeof MantineAccordion>;

function Accordion({ className, defaultValue, value, ...props }: AccordionProps) {
  const multiple = Array.isArray(defaultValue) || Array.isArray(value);
  return (
    <MantineAccordion
      data-slot="accordion"
      className={cn('steward-accordion', className)}
      defaultValue={defaultValue}
      value={value}
      multiple={multiple}
      {...props}
    />
  );
}

function AccordionItem({ className, ...props }: React.ComponentProps<typeof MantineAccordion.Item>) {
  return (
    <MantineAccordion.Item
      data-slot="accordion-item"
      className={cn('steward-accordion-item', className)}
      {...props}
    />
  );
}

function AccordionTrigger({
  className,
  children,
  ...props
}: React.ComponentProps<typeof MantineAccordion.Control> & { children: ReactNode }) {
  return (
    <MantineAccordion.Control
      data-slot="accordion-trigger"
      className={cn('steward-accordion-trigger', className)}
      {...props}
    >
      {children}
    </MantineAccordion.Control>
  );
}

function AccordionContent({
  className,
  ...props
}: React.ComponentProps<typeof MantineAccordion.Panel>) {
  return (
    <MantineAccordion.Panel
      data-slot="accordion-content"
      className={cn('steward-accordion-content text-sm', className)}
      {...props}
    />
  );
}

export { Accordion, AccordionContent, AccordionItem, AccordionTrigger };
