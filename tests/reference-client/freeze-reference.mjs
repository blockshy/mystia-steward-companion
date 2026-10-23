import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

// 仅从已固定的迁移前提交提取差分入口的依赖闭包；默认只核验，不会用当前实现刷新预期值。
// preferences 是旧文件中唯一混合平台副作用的模块：按语法声明裁去存储/DOM/Tauri 部分，
// 保留声明的原始源码片段。清单同时保存原文件和纯值投影哈希，可重现每个删除决定。
const revision = '098f414a4ddacc2471e3ca04119f13e2f0733ba8';
const base = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(base, '../..');
const prefix = 'apps/companion/src/';
const git = (...args) => execFileSync('git', ['-c', `safe.directory=${root.replaceAll('\\', '/')}`, ...args],
  { cwd: root, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024, windowsHide: true }).replaceAll('\r\n', '\n');
const digest = text => createHash('sha256').update(text).digest('hex');
const tracked = new Set(git('ls-tree', '-r', '--name-only', revision, prefix).trim().split('\n'));
const entrypoints = [
  'companion/automation-machine.ts', 'companion/automation-state.ts', 'companion/preferences.ts',
  'companion/domain/service-recommendations.ts', 'companion/domain/normal-order-details.ts',
  'companion/domain/special-business/registry.ts', 'companion/domain/game-ui-targets.ts',
  'companion/domain/sorting.ts', 'companion/domain/normal-order-key.ts',
  'companion/domain/special-business/yuyuko-challenge.ts', 'companion/domain/special-business/yuyuko-positive-spell.ts',
  'companion/domain/custom-recipes.ts', 'companion/domain/cookers.ts', 'companion/domain/primary-execution-plan.ts',
  'lib/recommendation-data.ts', 'recommendation-engine/index.ts',
];
const pending = entrypoints.map(file => prefix + file);
const files = new Map();
function purePreferences(source) {
  const parsed = ts.createSourceFile('preferences.ts', source, ts.ScriptTarget.Latest, true);
  const excluded = new Set(['persistCompanionPreferences', 'applyCompanionVisualPreferences', 'applyCompanionPreferencesToTauri']);
  return parsed.statements.filter(statement => {
    if (ts.isImportDeclaration(statement)) return statement.moduleSpecifier.text !== '@/lib/tauri-runtime';
    if (ts.isFunctionDeclaration(statement)) return !statement.name.text.startsWith('readStored') && !excluded.has(statement.name.text);
    if (ts.isVariableStatement(statement)) return statement.declarationList.declarations.every(item => !item.name.text.includes('STORAGE_'));
    return true;
  }).map(statement => statement.getFullText(parsed)).join('').trim() + '\n';
}
while (pending.length) {
  const sourcePath = pending.pop();
  if (files.has(sourcePath)) continue;
  const source = git('show', `${revision}:${sourcePath}`);
  const projected = sourcePath === `${prefix}companion/preferences.ts`;
  const content = projected ? purePreferences(source) : source;
  assert.doesNotMatch(content, /from ['"](?:react|@tauri-apps)|import\(['"]@tauri-apps|new Worker\(/,
    `差分闭包不得引入旧 UI 或平台执行入口：${sourcePath}`);
  const record = { path: `src/${sourcePath.slice(prefix.length)}`, sourcePath,
    projection: projected ? 'pure-preferences-declarations-v1' : 'exact-source',
    sourceSha256: digest(source), sha256: digest(content) };
  files.set(sourcePath, { record, content });
  for (const dependency of ts.preProcessFile(content, true, true).importedFiles) {
    const name = dependency.fileName;
    if (!name.startsWith('@/') && !name.startsWith('.')) throw new Error(`冻结纯业务不得依赖外部运行库：${name}`);
    const resolved = name.startsWith('@/') ? prefix + name.slice(2) : path.posix.join(path.posix.dirname(sourcePath), name);
    const target = [resolved, `${resolved}.ts`, `${resolved}/index.ts`].find(candidate => tracked.has(candidate));
    assert.ok(target, `缺失冻结依赖：${sourcePath} -> ${name}`);
    pending.push(target);
  }
}
const sorted = [...files.values()].sort((left, right) => left.record.path.localeCompare(right.record.path, 'en'));
const manifest = { revision, purpose: 'Frozen pure TypeScript oracle for offline C# differential tests only.', entrypoints,
  files: sorted.map(item => item.record) };
if (process.argv.includes('--write')) {
  for (const { record, content } of sorted) {
    const destination = path.join(base, record.path);
    mkdirSync(path.dirname(destination), { recursive: true });
    writeFileSync(destination, content);
  }
  writeFileSync(path.join(base, 'manifest.json'), `${JSON.stringify(manifest, null, 2)}\n`);
} else {
  assert.deepEqual(JSON.parse(readFileSync(path.join(base, 'manifest.json'), 'utf8')), manifest, '冻结清单或投影与固定提交不一致。');
  const list = directory => readdirSync(directory, { withFileTypes: true }).flatMap(entry => entry.isDirectory()
    ? list(path.join(directory, entry.name)) : [path.relative(base, path.join(directory, entry.name)).replaceAll('\\', '/')]);
  assert.deepEqual(list(path.join(base, 'src')).sort(), sorted.map(item => item.record.path).sort(), '冻结目录包含闭包外文件。');
  for (const { record, content } of sorted) assert.equal(readFileSync(path.join(base, record.path), 'utf8').replaceAll('\r\n', '\n'), content,
    `冻结源码偏离已记录提交：${record.path}`);
}
console.log(`PASS: ${sorted.length} 个纯业务 oracle 文件与固定提交 ${revision} 的声明投影一致；无旧 React/Tauri 执行代码。`);
