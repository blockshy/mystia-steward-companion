import { useMemo, useState } from 'react';
import { Accordion, AccordionContent, AccordionItem, AccordionTrigger } from '@/components/ui/accordion';
import { Card, CardContent } from '@/components/ui/card';
import { EmptyState, ListPanel } from '@/components/ui/display';
import { Input } from '@/components/ui/input';
import helpContent from '@/data/help-content.json';

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

export function ModHelpPanel() {
  const [query, setQuery] = useState('');
  const normalizedQuery = normalizeHelpSearchText(query);
  const filteredCategories = useMemo(
    () => filterHelpCategories(HELP_CONTENT.categories, normalizedQuery),
    [normalizedQuery],
  );
  const totalItems = HELP_CONTENT.categories.reduce((sum, category) => sum + category.items.length, 0);
  const visibleItems = filteredCategories.reduce((sum, category) => sum + category.items.length, 0);

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
        <div className="space-y-3">
          {filteredCategories.map((category) => (
            <ListPanel
              key={category.id}
              title={category.title}
              action={<span className="text-xs text-muted-foreground">{category.items.length} 项</span>}
            >
              {category.description && (
                <p className="mb-3 text-sm text-muted-foreground">{category.description}</p>
              )}
              <div className="space-y-2">
                {category.items.map((item) => (
                  <HelpDisclosure key={item.id} item={item} />
                ))}
              </div>
            </ListPanel>
          ))}
        </div>
      )}
    </div>
  );
}

function HelpDisclosure({ item }: { item: HelpItem }) {
  return (
    <Accordion defaultValue={[]}>
      <AccordionItem value={item.id}>
        <AccordionTrigger data-gamepad-clickable="true">
          <div className="min-w-0">
            <div className="font-medium">{item.title}</div>
            {item.summary && <div className="mt-1 text-xs text-muted-foreground">{item.summary}</div>}
          </div>
        </AccordionTrigger>
        <AccordionContent className="space-y-3">
          {item.steps && item.steps.length > 0 && (
            <HelpTextBlock title="操作" items={item.steps} ordered />
          )}
          {item.notes && item.notes.length > 0 && (
            <HelpTextBlock title="说明" items={item.notes} />
          )}
          {item.warnings && item.warnings.length > 0 && (
            <HelpTextBlock title="注意" items={item.warnings} tone="warning" />
          )}
        </AccordionContent>
      </AccordionItem>
    </Accordion>
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
