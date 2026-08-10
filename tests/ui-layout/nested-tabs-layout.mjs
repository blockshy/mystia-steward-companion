export async function inspectMinimumNestedTabsLayout(
  page,
  selector = '[data-slot="tabs-list"]:not(.steward-primary-tabs-list)',
) {
  return page.evaluate(({ selector }) => {
    const isVisible = (element) => {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.display !== 'none'
        && style.visibility !== 'hidden'
        && Number(style.opacity || '1') > 0.05
        && rect.width > 0
        && rect.height > 0;
    };

    const lists = Array.from(document.querySelectorAll(selector))
      .filter((node) => node instanceof HTMLElement && isVisible(node));
    const summaries = lists.map((list) => {
      const triggers = Array.from(list.querySelectorAll(':scope > [data-slot="tabs-trigger"]'))
        .filter((node) => node instanceof HTMLElement && isVisible(node));
      const listRect = list.getBoundingClientRect();
      const triggerRects = triggers.map((trigger) => trigger.getBoundingClientRect());
      const widths = triggerRects.map((rect) => rect.width);
      const firstRect = triggerRects[0];
      const lastRect = triggerRects.at(-1);
      const startsAtLeft = Boolean(firstRect) && Math.abs(firstRect.left - listRect.left) <= 2;
      const endsAtRight = Boolean(lastRect) && Math.abs(lastRect.right - listRect.right) <= 2;
      const singleRow = triggerRects.every((rect) => Math.abs(rect.top - triggerRects[0].top) <= 2);
      const contiguous = triggerRects.slice(1).every(
        (rect, index) => Math.abs(rect.left - triggerRects[index].right) <= 2,
      );
      const equalWidths = widths.length > 0 && Math.max(...widths) - Math.min(...widths) <= 2;
      const contained = triggerRects.every((rect) => (
        rect.left >= listRect.left - 1
        && rect.right <= listRect.right + 1
        && rect.top >= listRect.top - 1
        && rect.bottom <= listRect.bottom + 1
      ));
      const textContained = triggers.every(
        (trigger) => trigger.scrollWidth <= trigger.clientWidth + 1
          && trigger.scrollHeight <= trigger.clientHeight + 1,
      );
      const noHorizontalOverflow = list.scrollWidth <= list.clientWidth + 1;

      return {
        ok: triggers.length > 0
          && startsAtLeft
          && endsAtRight
          && singleRow
          && contiguous
          && equalWidths
          && contained
          && textContained
          && noHorizontalOverflow,
        labels: triggers.map((trigger) => (trigger.textContent || '').replace(/\s+/g, ' ').trim()),
        triggerCount: triggers.length,
        startsAtLeft,
        endsAtRight,
        singleRow,
        contiguous,
        equalWidths,
        contained,
        textContained,
        noHorizontalOverflow,
        listWidth: Math.round(listRect.width),
        triggerWidths: widths.map((width) => Math.round(width)),
        clientWidth: list.clientWidth,
        scrollWidth: list.scrollWidth,
      };
    });

    return {
      ok: summaries.every((summary) => summary.ok),
      listCount: summaries.length,
      summaries,
      failures: summaries.filter((summary) => !summary.ok),
    };
  }, { selector });
}
