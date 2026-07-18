import { Accordion as MantineAccordion } from '@mantine/core';
import type { ReactNode } from 'react';

import { composeClassNames } from '@/components/ui/style';

type AccordionProps = React.ComponentProps<typeof MantineAccordion>;

function Accordion({ className, defaultValue, value, ...props }: AccordionProps) {
  const multiple = Array.isArray(defaultValue) || Array.isArray(value);
  return (
    <MantineAccordion
      data-slot="accordion"
      data-gamepad-control="accordion"
      className={composeClassNames('steward-accordion', className)}
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
      className={composeClassNames('steward-accordion-item', className)}
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
      data-gamepad-control="accordion-trigger"
      className={composeClassNames('steward-accordion-trigger', className)}
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
      className={composeClassNames('steward-accordion-content text-sm', className)}
      {...props}
    />
  );
}

export { Accordion, AccordionContent, AccordionItem, AccordionTrigger };
