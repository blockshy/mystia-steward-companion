import { Modal } from '@mantine/core';
import type { ModalProps } from '@mantine/core';

import { composeClassNames } from '@/components/ui/style';

type DialogProps = Omit<ModalProps, 'opened' | 'onClose' | 'title'> & {
  opened: boolean;
  onClose: () => void;
  title: string;
  returnFocusKey: string;
};

function Dialog({
  children,
  className,
  opened,
  onClose,
  returnFocusKey,
  title,
  ...props
}: DialogProps) {
  return (
    <Modal
      data-slot="dialog"
      opened={opened}
      onClose={onClose}
      title={title}
      centered
      closeOnClickOutside
      closeOnEscape
      lockScroll
      returnFocus
      trapFocus
      radius={0}
      size="sm"
      withCloseButton={false}
      className={composeClassNames('steward-dialog', className)}
      classNames={{
        body: 'space-y-4 text-sm',
        content: 'border border-border bg-background text-foreground shadow-xl',
        header: 'border-b border-border bg-background px-4 py-3',
        title: 'text-sm font-semibold',
      }}
      overlayProps={{ backgroundOpacity: 0.52 }}
      {...props}
    >
      <div
        data-gamepad-control="dialog"
        data-gamepad-scope="modal"
        data-gamepad-return-focus-key={returnFocusKey}
      >
        {children}
      </div>
    </Modal>
  );
}

export { Dialog };
export type { DialogProps };
