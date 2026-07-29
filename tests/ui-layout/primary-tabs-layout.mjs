export const EXPECTED_PRIMARY_TAB_VALUES = Object.freeze([
  'overview',
  'normal',
  'rare',
  'custom-recipes',
  'service',
  'missions',
  'inventory',
  'help',
  'logs',
  'settings',
]);

export async function inspectMinimumPrimaryTabsLayout(page, expectedValues = EXPECTED_PRIMARY_TAB_VALUES) {
  return page.evaluate(({ expectedValues }) => {
    const list = document.querySelector('.steward-primary-tabs-list[data-gamepad-scope="tabs"]');
    if (!(list instanceof HTMLElement)) return { ok: false, reason: 'missing-list' };

    const triggers = Array.from(list.querySelectorAll(':scope > [data-gamepad-tab="true"]'))
      .filter((node) => node instanceof HTMLElement);
    const listRect = list.getBoundingClientRect();
    const viewportWidth = document.documentElement.clientWidth;
    const rowTops = [];
    const failures = [];

    for (const trigger of triggers) {
      const rect = trigger.getBoundingClientRect();
      const range = document.createRange();
      range.selectNodeContents(trigger);
      const textRect = range.getBoundingClientRect();
      const triggerStyle = getComputedStyle(trigger);
      const visible = rect.width > 0
        && rect.height > 0
        && triggerStyle.display !== 'none'
        && triggerStyle.visibility !== 'hidden'
        && Number(triggerStyle.opacity || '1') > 0.05;
      const contained = rect.left >= listRect.left - 1
        && rect.right <= listRect.right + 1
        && rect.top >= listRect.top - 1
        && rect.bottom <= listRect.bottom + 1
        && rect.left >= -1
        && rect.right <= viewportWidth + 1;
      const textContained = textRect.width > 0
        && textRect.height > 0
        && textRect.left >= rect.left - 1
        && textRect.right <= rect.right + 1
        && textRect.top >= rect.top - 1
        && textRect.bottom <= rect.bottom + 1;
      if (!visible || !contained || !textContained) {
        failures.push({
          value: trigger.dataset.gamepadTabValue || '',
          visible,
          contained,
          textContained,
          rect: [Math.round(rect.left), Math.round(rect.top), Math.round(rect.right), Math.round(rect.bottom)],
          textRect: [
            Math.round(textRect.left),
            Math.round(textRect.top),
            Math.round(textRect.right),
            Math.round(textRect.bottom),
          ],
        });
      }
      if (!rowTops.some((top) => Math.abs(top - rect.top) <= 2)) rowTops.push(rect.top);
    }

    const values = triggers.map((trigger) => trigger.dataset.gamepadTabValue || '');
    const missingValues = expectedValues.filter((value) => !values.includes(value));
    const unexpectedValues = values.filter((value) => !expectedValues.includes(value));
    const orderMatches = values.every((value, index) => value === expectedValues[index]);
    const style = getComputedStyle(list);
    const columnCount = style.gridTemplateColumns.trim().split(/\s+/).filter(Boolean).length;
    const noInternalOverflow = list.scrollWidth <= list.clientWidth + 1
      && list.scrollHeight <= list.clientHeight + 1;
    return {
      ok: missingValues.length === 0
        && unexpectedValues.length === 0
        && orderMatches
        && triggers.length === expectedValues.length
        && failures.length === 0
        && style.display === 'grid'
        && columnCount === 5
        && rowTops.length === 2
        && noInternalOverflow,
      triggerCount: triggers.length,
      missingValues,
      unexpectedValues,
      orderMatches,
      failures,
      display: style.display,
      columnCount,
      rowCount: rowTops.length,
      noInternalOverflow,
      clientSize: [list.clientWidth, list.clientHeight],
      scrollSize: [list.scrollWidth, list.scrollHeight],
    };
  }, { expectedValues });
}
