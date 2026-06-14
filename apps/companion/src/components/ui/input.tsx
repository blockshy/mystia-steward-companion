import * as React from "react"
import { Input as MantineInput } from '@mantine/core';
import type { MantineSize } from '@mantine/core';

import { cn } from "@/lib/utils"

type InputProps = Omit<React.InputHTMLAttributes<HTMLInputElement>, 'size'> & {
  size?: MantineSize | (string & {});
};

function Input({ className, type, size = 'sm', ...props }: InputProps) {
  return (
    <MantineInput
      type={type}
      data-slot="input"
      size={size}
      className={cn('steward-input', className)}
      {...props}
    />
  )
}

export { Input }
export type { InputProps }
