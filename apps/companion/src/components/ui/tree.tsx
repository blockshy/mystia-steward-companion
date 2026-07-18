import { Tree as MantineTree } from '@mantine/core';
import type {
  RenderTreeNodePayload,
  TreeNodeData,
  TreeProps as MantineTreeProps,
} from '@mantine/core';

import { composeClassNames } from '@/components/ui/style';

function Tree({ className, levelOffset = 'md', ...props }: MantineTreeProps) {
  return (
    <MantineTree
      data-slot="tree"
      data-gamepad-control="tree"
      levelOffset={levelOffset}
      className={composeClassNames('steward-tree', className)}
      {...props}
    />
  );
}

export { Tree };
export type { RenderTreeNodePayload, TreeNodeData };
