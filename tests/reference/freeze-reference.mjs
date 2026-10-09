import { execFileSync } from 'node:child_process';
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';

// 只读 Git 基线并生成测试 oracle。绝不读取重构中的工作树，防止把 C# 迁移后的结果写回基线。
// 本脚本不参与应用构建，生产代码禁止导入 tests/reference 下的任何实现。
const revision = '098f414a';
const root = process.cwd();
const git = (...args) => execFileSync('git', ['-c', `safe.directory=${root.replaceAll('\\', '/')}`, ...args], { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 });
const tracked = new Set(git('ls-tree', '-r', '--name-only', revision, 'apps/companion/src').trim().split('\n'));
const pending = [...tracked].filter(file => file.startsWith('apps/companion/src/recommendation-engine/') || file.startsWith('apps/companion/src/companion/domain/') || /\/companion\/(automation-machine|automation-state)\.ts$/.test(file));
const visited = new Set();
while (pending.length) {
  const file = pending.pop();
  if (visited.has(file)) continue;
  visited.add(file);
  const source = git('show', `${revision}:${file}`);
  const target = path.join(root, 'tests/reference', file);
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, source);
  for (const match of source.matchAll(/(?:from\s+|import\s*)['"](@\/[^'"]+|\.[^'"]+)['"]/g)) {
    const base = match[1].startsWith('@/') ? `apps/companion/src/${match[1].slice(2)}` : path.posix.join(path.posix.dirname(file), match[1]);
    const dependency = [base, `${base}.ts`, `${base}.tsx`, `${base}/index.ts`].find(candidate => tracked.has(candidate));
    if (dependency) pending.push(dependency);
  }
}
// 旧审计包含组合根的源码约束；仅保存文本证据，不递归冻结 UI，也不执行此 TSX 文件。
for (const file of ['apps/companion/src/companion/ModWorkbench.tsx', 'apps/companion/src/companion/hooks/useGameUiTargetPublisher.ts', 'apps/companion/src/companion/workers/order-recommendations.worker.ts', 'apps/companion/src/companion/workers/order-recommendations.types.ts', 'apps/companion/src/data/help-content.json']) {
  const target = path.join(root, 'tests/reference', file);
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, git('show', `${revision}:${file}`));
  visited.add(file);
}
// 历史审计同时检查若干页面/Hook 的源码约束；将这些文字证据固定到同一提交，防止旧断言绑定新架构。
for (const directory of ['tests/recommendations', 'tests/automation', 'tests/ui-pinning']) {
  for (const entry of readdirSync(directory).filter(name => name.endsWith('.mjs'))) {
    const audit = readFileSync(path.join(directory, entry), 'utf8');
    for (const match of audit.matchAll(/apps\/companion\/src\/[A-Za-z0-9_./-]+/g)) {
      const file = match[0];
      if (!tracked.has(file) || visited.has(file)) continue;
      const target = path.join(root, 'tests/reference', file);
      mkdirSync(path.dirname(target), { recursive: true });
      writeFileSync(target, git('show', `${revision}:${file}`));
      visited.add(file);
    }
  }
}
const files = [...visited].sort();
const sha256 = Object.fromEntries(files.map(file => [file, createHash('sha256').update(readFileSync(path.join('tests/reference', file))).digest('hex')]));
writeFileSync('tests/reference/manifest.json', `${JSON.stringify({ revision: git('rev-parse', revision).trim(), files, sha256 }, null, 2)}\n`);
console.log(`Frozen ${visited.size} TypeScript reference files from ${revision}.`);
