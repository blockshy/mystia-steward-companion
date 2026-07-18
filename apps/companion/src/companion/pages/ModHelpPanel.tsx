import type { ReactNode } from 'react';
import { useCallback, useMemo, useState } from 'react';
import {
  IconChevronRight,
  IconFileText,
  IconFolder,
  IconFolderOpen,
} from '@tabler/icons-react';
import {
  Card,
  CardContent,
  EmptyState,
  Input,
  Tree,
  useTree,
} from '@/components/ui-kit';
import type { RenderTreeNodePayload, TreeNodeData } from '@/components/ui-kit';
import helpContent from '@/data/help-content.json';
import { composeClassNames } from '@/components/ui/style';

interface HelpContent {
  version: number;
  updatedAt: string;
  intro: string;
  categories: HelpCategory[];
}

interface HelpCategory {
  id: string;
  title: string;
  description?: string;
  items: HelpItem[];
}

interface HelpItem {
  id: string;
  title: string;
  summary?: string;
  steps?: string[];
  notes?: string[];
  warnings?: string[];
}

const HELP_CONTENT = helpContent as HelpContent;

type HelpNodeMeta =
  | { type: 'category'; category: HelpCategory }
  | { type: 'item'; category: HelpCategory; item: HelpItem };

interface SelectedHelpItem {
  category: HelpCategory;
  item: HelpItem;
}

export function ModHelpPanel() {
  const [query, setQuery] = useState('');
  const [selectedItemId, setSelectedItemId] = useState(
    () => HELP_CONTENT.categories[0]?.items[0]?.id ?? '',
  );
  const normalizedQuery = normalizeHelpSearchText(query);
  const filteredCategories = useMemo(
    () => filterHelpCategories(HELP_CONTENT.categories, normalizedQuery),
    [normalizedQuery],
  );
  const { treeData, metaByValue } = useMemo(
    () => buildHelpTree(filteredCategories),
    [filteredCategories],
  );
  const selectedContext = useMemo(
    () => findVisibleHelpItem(filteredCategories, selectedItemId) ?? getFirstVisibleHelpItem(filteredCategories),
    [filteredCategories, selectedItemId],
  );
  const selectedTreeValue = selectedContext ? getItemTreeValue(selectedContext.item.id) : undefined;
  const totalItems = HELP_CONTENT.categories.reduce((sum, category) => sum + category.items.length, 0);
  const visibleItems = filteredCategories.reduce((sum, category) => sum + category.items.length, 0);

  const renderNode = useCallback(
    ({ node, expanded, hasChildren, selected, elementProps }: RenderTreeNodePayload) => {
      const { className, onClick, ...labelProps } = elementProps;
      const meta = metaByValue.get(node.value);
      const isCategory = meta?.type === 'category';
      const FolderIcon = expanded ? IconFolderOpen : IconFolder;
      const NodeIcon = isCategory ? FolderIcon : IconFileText;
      const itemCount = isCategory ? meta.category.items.length : undefined;

      return (
        <div
          {...labelProps}
          className={composeClassNames(
            className,
            'steward-tree-label flex min-w-0 items-center gap-2 px-2 py-1 text-sm',
            selected && 'font-medium',
          )}
          onClick={(event) => {
            onClick(event);
            if (meta?.type === 'item') {
              setSelectedItemId(meta.item.id);
            }
          }}
        >
          <IconChevronRight
            aria-hidden="true"
            className={composeClassNames(
              'size-3.5 flex-none transition-transform',
              expanded && 'rotate-90',
              !hasChildren && 'opacity-0',
            )}
            stroke={1.8}
          />
          <NodeIcon aria-hidden="true" className="size-4 flex-none text-muted-foreground" stroke={1.7} />
          <span className="min-w-0 flex-1 truncate">{node.label}</span>
          {itemCount !== undefined && (
            <span className="flex-none text-xs text-muted-foreground">{itemCount}</span>
          )}
        </div>
      );
    },
    [metaByValue],
  );

  return (
    <div className="space-y-4">
      <Card>
        <CardContent className="space-y-3 p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div className="min-w-0">
              <h2 className="text-base font-semibold">帮助</h2>
              <p className="mt-1 max-w-3xl text-sm text-muted-foreground">{HELP_CONTENT.intro}</p>
            </div>
            <div className="flex w-full items-center justify-between gap-3 text-xs text-muted-foreground">
              <span>条目 {visibleItems}/{totalItems}</span>
              <span>更新 {HELP_CONTENT.updatedAt}</span>
            </div>
          </div>
          <Input
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="搜索功能、问题或关键词"
            data-gamepad-clickable="true"
          />
        </CardContent>
      </Card>

      {filteredCategories.length === 0 ? (
        <EmptyState text="没有匹配的帮助内容" />
      ) : (
        <div className="grid gap-3 lg:grid-cols-[18rem_minmax(0,1fr)]">
          <Card className="min-w-0">
            <CardContent className="p-3">
              <div className="mb-2 flex items-center justify-between gap-3">
                <h3 className="text-sm font-semibold">目录</h3>
                <span className="text-xs text-muted-foreground">{filteredCategories.length} 类</span>
              </div>
              <HelpTreeNavigation
                key={normalizedQuery}
                data={treeData}
                renderNode={renderNode}
                selectedTreeValue={selectedTreeValue}
              />
            </CardContent>
          </Card>

          <Card className="min-w-0">
            <CardContent className="p-4">
              {selectedContext ? (
                <HelpDetail selected={selectedContext} />
              ) : (
                <EmptyState text="请选择一个帮助条目" />
              )}
            </CardContent>
          </Card>
        </div>
      )}
    </div>
  );
}

function HelpTreeNavigation({
  data,
  renderNode,
  selectedTreeValue,
}: {
  data: TreeNodeData[];
  renderNode: (payload: RenderTreeNodePayload) => ReactNode;
  selectedTreeValue?: string;
}) {
  const initialExpandedState = useMemo(() => getTreeExpandedStateFromData(data), [data]);
  const tree = useTree({
    initialExpandedState,
    selectedState: selectedTreeValue ? [selectedTreeValue] : [],
  });

  return (
    <Tree
      tree={tree}
      data={data}
      renderNode={renderNode}
      withLines
      className="max-h-[min(58vh,34rem)] overflow-y-auto pr-1"
      aria-label="帮助目录"
      data-gamepad-scroll-key="help:navigation"
      data-gamepad-scroll-region="true"
      tabIndex={-1}
    />
  );
}

function HelpDetail({ selected }: { selected: SelectedHelpItem }) {
  const { category, item } = selected;
  return (
    <div className="min-w-0">
      <div className="text-xs font-medium text-muted-foreground">{category.title}</div>
      <h3 className="mt-1 text-base font-semibold leading-snug">{item.title}</h3>
      {item.summary && <p className="mt-2 text-sm text-muted-foreground">{item.summary}</p>}
      {category.description && (
        <p className="mt-2 text-xs text-muted-foreground">{category.description}</p>
      )}
      <div className="mt-4 space-y-4 text-sm">
        {item.steps && item.steps.length > 0 && (
          <HelpTextBlock title="操作" items={item.steps} ordered />
        )}
        {item.notes && item.notes.length > 0 && (
          <HelpTextBlock title="说明" items={item.notes} />
        )}
        {item.warnings && item.warnings.length > 0 && (
          <HelpTextBlock title="注意" items={item.warnings} tone="warning" />
        )}
      </div>
    </div>
  );
}

function HelpTextBlock({
  title,
  items,
  ordered = false,
  tone = 'default',
}: {
  title: string;
  items: string[];
  ordered?: boolean;
  tone?: 'default' | 'warning';
}) {
  const List = ordered ? 'ol' : 'ul';
  return (
    <div>
      <div className={tone === 'warning' ? 'text-sm font-medium text-destructive' : 'text-sm font-medium'}>
        {title}
      </div>
      <List className={`mt-1 space-y-1 pl-5 ${ordered ? 'list-decimal' : 'list-disc'} text-muted-foreground`}>
        {items.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </List>
    </div>
  );
}

function filterHelpCategories(categories: HelpCategory[], query: string): HelpCategory[] {
  if (!query) return categories;

  return categories
    .map((category) => ({
      ...category,
      items: category.items.filter((item) => helpItemMatchesQuery(category, item, query)),
    }))
    .filter((category) => category.items.length > 0);
}

function helpItemMatchesQuery(category: HelpCategory, item: HelpItem, query: string): boolean {
  const chunks = [
    category.title,
    category.description ?? '',
    item.title,
    item.summary ?? '',
    ...(item.steps ?? []),
    ...(item.notes ?? []),
    ...(item.warnings ?? []),
  ];
  return normalizeHelpSearchText(chunks.join('\n')).includes(query);
}

function normalizeHelpSearchText(value: string): string {
  return value.trim().toLowerCase();
}

function buildHelpTree(categories: HelpCategory[]): {
  treeData: TreeNodeData[];
  metaByValue: Map<string, HelpNodeMeta>;
} {
  const metaByValue = new Map<string, HelpNodeMeta>();
  const treeData = categories.map((category) => {
    const categoryValue = getCategoryTreeValue(category.id);
    metaByValue.set(categoryValue, { type: 'category', category });

    return {
      label: category.title,
      value: categoryValue,
      children: category.items.map((item) => {
        const itemValue = getItemTreeValue(item.id);
        metaByValue.set(itemValue, { type: 'item', category, item });

        return {
          label: item.title,
          value: itemValue,
        };
      }),
    };
  });

  return { treeData, metaByValue };
}

function getTreeExpandedStateFromData(data: TreeNodeData[]): Record<string, boolean> {
  return Object.fromEntries(data.map((node) => [node.value, true]));
}

function findVisibleHelpItem(categories: HelpCategory[], itemId: string): SelectedHelpItem | undefined {
  for (const category of categories) {
    const item = category.items.find((candidate) => candidate.id === itemId);
    if (item) {
      return { category, item };
    }
  }

  return undefined;
}

function getFirstVisibleHelpItem(categories: HelpCategory[]): SelectedHelpItem | undefined {
  const category = categories.find((candidate) => candidate.items.length > 0);
  const item = category?.items[0];
  return category && item ? { category, item } : undefined;
}

function getCategoryTreeValue(id: string): string {
  return `category:${id}`;
}

function getItemTreeValue(id: string): string {
  return `item:${id}`;
}
