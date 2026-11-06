import assert from 'node:assert/strict';
import {
  buildUpdateCatalog,
  compareUpdateVersions,
  MAX_RELEASE_NOTES_BYTES,
  parseUpdateVersion,
} from '../../scripts/generate-update-catalog.mjs';

// 固定预览数字排序和整数边界，防止按字符串比较或使用不精确数值生成错误更新顺序。
const stable = parseUpdateVersion('v1.3.0');
const preview2 = parseUpdateVersion('1.4.0-preview.2');
const preview10 = parseUpdateVersion('v1.4.0-preview.10');
assert.ok(stable);
assert.ok(preview2);
assert.ok(preview10);
assert.ok(compareUpdateVersions(preview10, preview2) > 0);
assert.ok(compareUpdateVersions(parseUpdateVersion('1.4.0'), preview10) > 0);
assert.equal(parseUpdateVersion('1.4.0-rc.1'), null);
assert.equal(parseUpdateVersion('2147483648.0.0'), null);
assert.equal(parseUpdateVersion('9007199254740992.0.0'), null);
assert.equal(parseUpdateVersion('1.4.0-preview.9007199254740992'), null);

// 人工分页结果同时包含合法历史、草稿和非法标签；目录构建不需要访问 GitHub。
const historicalReleases = [[
  release('v1.2.0', false, '2026-07-12T12:51:25Z', 'v1.2.0', '## 修复\n\n- 历史修复'),
  release('v1.3.0-preview.1', true, '2026-08-10T00:00:00Z', '预览版 1', '预览说明'),
  release('1.2.1', false, '2026-07-13T00:00:00Z', '非规范 tag', '不应进入目录'),
  { ...release('v9.9.9', false, '2026-08-11T03:00:00Z'), draft: true },
  release('not-an-update', false, '2026-08-11T04:00:00Z'),
]];

const built = buildUpdateCatalog({
  githubReleases: historicalReleases,
  repository: 'blockshy/mystia-steward-companion',
  ownerTag: 'v1.3.0',
  ownerVersion: '1.3.0',
  ownerChannel: 'stable',
  ownerTitle: 'v1.3.0 新标题\n',
  ownerNotes: '## 新增功能\n\n- 当前说明\n',
  ownerPublishedAtUtc: '2026-08-11T02:57:08Z',
  generatedAtUtc: '2026-08-12T00:00:00Z',
});

assert.equal(built.catalog.schemaVersion, 1);
assert.equal(built.catalog.ownerTag, 'v1.3.0');
assert.deepEqual(
  built.catalog.releases.map((entry) => entry.version),
  ['1.2.0', '1.3.0-preview.1', '1.3.0'],
  'Catalog releases did not preserve strict semantic-version order.',
);
const ownerRelease = built.catalog.releases.find((entry) => entry.tag === 'v1.3.0');
assert.equal(ownerRelease?.title, 'v1.3.0 新标题');
assert.equal(ownerRelease?.notesMarkdown, '## 新增功能\n\n- 当前说明\n');
assert.ok(built.serialized.endsWith('\n'));
// 相同语义输入必须得到相同序列化结果，确保后续清单摘要不受标题尾部空白影响。
assert.equal(
  built.serialized,
  buildUpdateCatalog({
    githubReleases: historicalReleases,
    repository: 'blockshy/mystia-steward-companion',
    ownerTag: 'v1.3.0',
    ownerVersion: '1.3.0',
    ownerChannel: 'stable',
    ownerTitle: 'v1.3.0 新标题',
    ownerNotes: '## 新增功能\n\n- 当前说明\n',
    ownerPublishedAtUtc: '2026-08-11T02:57:08Z',
    generatedAtUtc: '2026-08-12T00:00:00Z',
  }).serialized,
  'Catalog output is not deterministic.',
);

expectFailure(
  () => buildUpdateCatalog(baseOptions({ ownerNotes: '' })),
  'Current release notes',
);
expectFailure(
  () => buildUpdateCatalog(baseOptions({
    githubReleases: [[release('v1.2.0', false, '2026-07-01T00:00:00Z', 'v1.2.0', '   ')]],
  })),
  'Release v1.2.0 notes',
);
expectFailure(
  () => buildUpdateCatalog(baseOptions({ ownerVersion: '1.3.1' })),
  'do not match',
);
expectFailure(
  () => buildUpdateCatalog(baseOptions({ ownerChannel: 'preview' })),
  'channel',
);
expectFailure(
  () => buildUpdateCatalog(baseOptions({
    githubReleases: [[
      release('v1.2.0', false, '2026-07-01T00:00:00Z'),
      release('v1.2.0', false, '2026-07-02T00:00:00Z'),
    ]],
  })),
  'Duplicate',
);
expectFailure(
  () => buildUpdateCatalog(baseOptions({
    githubReleases: [[release('v1.4.0-preview.1', false, '2026-07-01T00:00:00Z')]],
  })),
  'prerelease flag',
);
expectFailure(
  () => buildUpdateCatalog(baseOptions({
    githubReleases: [[release('v1.4.0-preview.1', true, '2026-07-01T00:00:00Z')]],
  })),
  'must be strictly newer',
);
expectFailure(
  () => buildUpdateCatalog(baseOptions({
    githubReleases: [[release(
      'v1.2.0',
      false,
      '2026-07-01T00:00:00Z',
      'oversized',
      'x'.repeat(MAX_RELEASE_NOTES_BYTES + 1),
    )]],
  })),
  'notes exceed',
);

console.log('update catalog generation audit passed');

function baseOptions(overrides = {}) {
  return {
    githubReleases: [],
    repository: 'blockshy/mystia-steward-companion',
    ownerTag: 'v1.3.0',
    ownerVersion: '1.3.0',
    ownerChannel: 'stable',
    ownerTitle: 'v1.3.0',
    ownerNotes: 'release notes',
    ownerPublishedAtUtc: '2026-08-11T02:57:08Z',
    generatedAtUtc: '2026-08-12T00:00:00Z',
    ...overrides,
  };
}

function release(tag, prerelease, publishedAt, name = tag, body = 'release notes') {
  return {
    tag_name: tag,
    name,
    body,
    draft: false,
    prerelease,
    published_at: publishedAt,
  };
}

function expectFailure(action, expectedMessage) {
  assert.throws(action, (error) => (
    error instanceof Error && error.message.includes(expectedMessage)
  ));
}
