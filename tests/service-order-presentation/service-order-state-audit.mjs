import assert from 'node:assert/strict';

import { MantineProvider } from '@mantine/core';
import React from 'react';
import { renderToStaticMarkup } from 'react-dom/server';
import { createServer } from 'vite';

const vite = await createServer({
  configFile: 'apps/companion/vite.config.ts',
  server: { middlewareMode: true },
  appType: 'custom',
  logLevel: 'error',
});

try {
  const { ServiceOrderCollectionPanel } = await vite.ssrLoadModule(
    '/src/companion/pages/service/ServiceOrderPresentation.tsx',
  );

  assertPresentation(ServiceOrderCollectionPanel, {
    state: { kind: 'empty', message: '暂无普客订单' },
    hasRows: true,
    expectedMessage: '暂无普客订单',
    expectedCount: 0,
    expectedRows: false,
  });
  assertPresentation(ServiceOrderCollectionPanel, {
    state: { kind: 'updating', message: '普客订单详情计算中' },
    hasRows: false,
    expectedMessage: '普客订单详情计算中',
    expectedCount: 0,
    expectedRows: false,
  });
  assertPresentation(ServiceOrderCollectionPanel, {
    state: { kind: 'updating', message: '普客订单详情计算中' },
    hasRows: true,
    expectedBadge: '更新中',
    expectedCount: 3,
    expectedRows: true,
  });
  assertPresentation(ServiceOrderCollectionPanel, {
    state: {
      kind: 'error',
      message: '普客订单详情计算失败',
      retainedLabel: '更新失败，当前为上次结果',
      updating: true,
    },
    hasRows: true,
    expectedBadge: '更新失败，当前为上次结果',
    expectedAdditionalBadge: '更新中',
    expectedCount: 3,
    expectedRows: true,
  });
  assertPresentation(ServiceOrderCollectionPanel, {
    state: { kind: 'error', message: '普客订单详情计算失败', emptyLabel: '方案计算失败' },
    hasRows: false,
    expectedMessage: '普客订单详情计算失败',
    expectedBadge: '方案计算失败',
    expectedCount: 0,
    expectedRows: false,
  });
  assertPresentation(ServiceOrderCollectionPanel, {
    state: { kind: 'ready' },
    hasRows: true,
    expectedCount: 3,
    expectedRows: true,
  });
} finally {
  await vite.close();
}

console.log('PASS: service order collection state matrix only retains rows during update and error states.');

function assertPresentation(Component, {
  state,
  hasRows,
  expectedMessage,
  expectedBadge,
  expectedAdditionalBadge,
  expectedCount,
  expectedRows,
}) {
  const markup = renderToStaticMarkup(React.createElement(
    MantineProvider,
    null,
    React.createElement(
      Component,
      {
        mode: 'normal',
        count: 3,
        state,
        hasRows,
      },
      React.createElement('div', { 'data-state-row-probe': 'true' }, '上一轮订单'),
    ),
  ));

  assert.equal(markup.includes('data-state-row-probe="true"'), expectedRows);
  assert.match(markup, new RegExp(`>${expectedCount} 笔<`));
  if (expectedMessage) assert.ok(markup.includes(expectedMessage));
  if (expectedBadge) assert.ok(markup.includes(expectedBadge));
  if (expectedAdditionalBadge) assert.ok(markup.includes(expectedAdditionalBadge));
}
