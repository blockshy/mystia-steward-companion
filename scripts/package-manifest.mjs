#!/usr/bin/env node
/**
 * 新版三件套唯一整包清单规范。文件名、大小与 SHA-256 描述全部受管理载荷；清单自身
 * 由外层 ZIP 的摘要与发布来源验证保护，不做自引用 hash。拒绝旧 business-bundle 规范。
 * 创建和验证均不执行载荷。目录遍历先检查链接，再进入子目录，且限制文件数和总字节数。
 */
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { existsSync, lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const manifestName = 'package-manifest.json';
export const requiredFiles = Object.freeze([
  'MystiaStewardCompanion.BepInEx.dll', 'MystiaStewardCompanion.Contracts.dll',
  'MystiaStewardCompanion.Business.dll', 'mystia-steward-companion-updater.exe',
  'companion/mystia-steward-companion.exe', 'companion/flutter_windows.dll',
  'companion/msvcp140.dll', 'companion/vcruntime140.dll', 'companion/vcruntime140_1.dll',
  'companion/data/app.so', 'companion/data/icudtl.dat', 'companion/data/flutter_assets/AssetManifest.bin',
  'companion/data/flutter_assets/FontManifest.json', 'companion/data/flutter_assets/NOTICES.Z',
  'companion/THIRD-PARTY-NOTICES.txt',
]);
export const defaultPreservedPaths = Object.freeze(['logs', 'diagnostics', 'exports', 'user-data']);
const limit = { count: 8192, file: 536870912, total: 2147483648, manifest: 4194304 };
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const requireValue = (condition, message) => { if (!condition) throw new Error(message); };

/** 只接受可跨 Windows/Android 工具处理的规范相对路径，不允许别名、ADS或设备名。 */
export function validateRelativePath(value) {
  requireValue(typeof value === 'string' && value.length > 0 && value.length <= 240, '清单路径长度无效。');
  requireValue(!/[\\:\x00-\x1f\x7f<>"|?*]/u.test(value) && !value.startsWith('/'), `清单路径含危险字符：${value}`);
  for (const part of value.split('/')) {
    requireValue(part && part !== '.' && part !== '..' && !/[. ]$/u.test(part), `清单路径未规范化：${value}`);
    requireValue(!/^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)/iu.test(part), `清单路径是 Windows 保留设备：${value}`);
  }
  return value;
}

function assertNoLinkAncestors(target) {
  let cursor = path.resolve(target);
  while (true) {
    const stat = lstatSync(cursor);
    requireValue(!stat.isSymbolicLink(), `拒绝链接或重解析路径：${cursor}`);
    const parent = path.dirname(cursor); if (parent === cursor) return; cursor = parent;
  }
}

/** 读取物理文件树；空目录不属于受管理载荷，链接与特殊设备始终拒绝。 */
export function readPackageTree(root) {
  assertNoLinkAncestors(root);
  requireValue(lstatSync(root).isDirectory(), '包根不是目录。');
  const files = new Map(); const names = new Set(); let total = 0;
  function visit(directory, prefix) {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const relative = validateRelativePath(`${prefix}${entry.name}`);
      const full = path.join(directory, entry.name); const stat = lstatSync(full);
      requireValue(!stat.isSymbolicLink(), `包内不允许链接：${relative}`);
      const folded = relative.toLowerCase(); requireValue(!names.has(folded), `包内路径大小写冲突：${relative}`); names.add(folded);
      if (stat.isDirectory()) visit(full, `${relative}/`);
      else {
        requireValue(stat.isFile(), `包内不是普通文件：${relative}`);
        requireValue(stat.size <= limit.file && files.size < limit.count + 1, '包超出文件大小/数量上限。');
        total += stat.size; requireValue(total <= limit.total, '包总大小超过2GiB。');
        files.set(relative, { path: relative, size: stat.size, sha256: sha256(readFileSync(full)) });
      }
    }
  }
  visit(path.resolve(root), ''); return files;
}

function keys(value, expected, label) {
  requireValue(value && typeof value === 'object' && !Array.isArray(value)
    && Object.keys(value).sort().join('|') === [...expected].sort().join('|'), `${label}字段集合无效。`);
}
function isManaged(relative) { return requiredFiles.includes(relative) || relative.startsWith('companion/'); }
function isPreserved(relative, prefixes) { return prefixes.some(prefix => relative === prefix || relative.startsWith(`${prefix}/`)); }

/** JSON.parse 会静默覆盖重复字段；先按对象作用域检查 key，和 C#/serde 的拒绝语义一致。 */
function parseManifest(text) {
  const stack = [];
  for (let index = 0; index < text.length; index++) {
    const character = text[index];
    if (character === '{') stack.push(new Set());
    else if (character === '[') stack.push(null);
    else if (character === '}' || character === ']') stack.pop();
    else if (character === '-' || /[0-9]/u.test(character)) {
      const token = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/u.exec(text.slice(index))?.[0] ?? '';
      // 清单仅定义非负十进制整数，拒绝 JSON 的浮点/指数/-0 别名，避免语言解析差异。
      requireValue(/^(?:0|[1-9][0-9]*)$/u.test(token), '清单整数必须是规范非负十进制。');
      index += token.length - 1;
    }
    else if (character === '"') {
      const start = index++;
      while (index < text.length) { if (text[index] === '\\') index += 2; else if (text[index] === '"') break; else index++; }
      const token = text.slice(start, index + 1); let next = index + 1;
      while (/\s/u.test(text[next] ?? '') && next < text.length) next++;
      if (text[next] === ':' && stack.at(-1) instanceof Set) {
        const name = JSON.parse(token); const keys = stack.at(-1);
        requireValue(!keys.has(name), `清单包含重复JSON字段：${name}`); keys.add(name);
      }
    }
  }
  return JSON.parse(text);
}

/** 验证结构和完整载荷；仅安装后的显式附加文件白名单可以位于清单外。 */
export function validatePackage(root, { allowPreserved = false, preservePaths = defaultPreservedPaths } = {}) {
  const tree = readPackageTree(root); const record = tree.get(manifestName);
  requireValue(record && record.size > 0 && record.size <= limit.manifest, '缺少或超长 package-manifest.json。');
  const manifest = parseManifest(readFileSync(path.join(root, manifestName), 'utf8'));
  keys(manifest, ['schemaVersion','product','version','sourceCommit','sourceDigest','toolchainSha256','businessProtocolVersion','clientProtocolVersion','files'], '包清单');
  requireValue(manifest.schemaVersion === 1 && manifest.product === 'mystia-steward-companion', '不支持的包规范或产品。');
  requireValue(manifest.businessProtocolVersion === 1 && manifest.clientProtocolVersion === 2, '成套协议版本不一致。');
  requireValue(typeof manifest.version === 'string' && /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(manifest.version), '无效产品版本。');
  requireValue(typeof manifest.sourceCommit === 'string' && /^[0-9a-f]{40}$/u.test(manifest.sourceCommit), '无效源码提交身份。');
  for (const key of ['sourceDigest','toolchainSha256']) requireValue(typeof manifest[key] === 'string' && /^[0-9a-f]{64}$/u.test(manifest[key]), `无效${key}。`);
  requireValue(Array.isArray(manifest.files) && manifest.files.length >= requiredFiles.length && manifest.files.length <= limit.count, '载荷数量无效。');
  const managed = new Set(); const folded = new Set(); let total = 0;
  for (const file of manifest.files) {
    keys(file, ['path','size','sha256'], '载荷'); validateRelativePath(file.path);
    requireValue(isManaged(file.path) && !folded.has(file.path.toLowerCase()), `未知或重复托管路径：${file.path}`);
    managed.add(file.path); folded.add(file.path.toLowerCase());
    requireValue(Number.isSafeInteger(file.size) && file.size >= 0 && file.size <= limit.file && typeof file.sha256 === 'string' && /^[0-9a-f]{64}$/u.test(file.sha256), `无效文件元数据：${file.path}`);
    // AOT、ICU、索引和许可即使使用非程序扩展名也是必需载荷；普通空资源仍可合法存在。
    requireValue(file.size > 0 || (!requiredFiles.includes(file.path) && !/\.(exe|dll)$/iu.test(file.path)), `必需或程序组件为空：${file.path}`);
    total += file.size; requireValue(total <= limit.total, '载荷总大小超过2GiB。');
    const actual = tree.get(file.path); requireValue(actual && actual.size === file.size && actual.sha256 === file.sha256, `载荷缺失/大小/摘要不匹配：${file.path}`);
  }
  for (const name of requiredFiles) requireValue(managed.has(name), `缺少必需组件：${name}`);
  for (const prefix of preservePaths) { validateRelativePath(prefix); requireValue(!isManaged(prefix) && prefix !== 'companion' && prefix !== manifestName, '附加保留路径不能覆盖托管命名空间。'); }
  for (const name of tree.keys()) requireValue(name === manifestName || managed.has(name)
    || (allowPreserved && !isManaged(name) && isPreserved(name, preservePaths)), `发现清单外文件：${name}`);
  return manifest;
}

/** 元数据由构建仓库生成，sourceDigest覆盖当前工作树源文件，而非仅记录可能dirty的HEAD。 */
export function repositoryMetadata(repository) {
  repository = path.resolve(repository); // 去掉 URL 转本地路径后的尾分隔符，确保 Git 安全目录匹配精确工作区。
  const git = args => { const r = spawnSync('git', ['-c', `safe.directory=${repository.replaceAll('\\','/')}`, ...args], {cwd:repository,encoding:'utf8',windowsHide:true}); if(r.status!==0)throw new Error(r.stderr);return r.stdout.trim(); };
  const names = git(['ls-files','-coz','--exclude-standard']).split('\0').filter(name =>
    (/^(apps\/companion-flutter|packages|modules|mods\/bepinex\/(?:src|tools)|native|scripts|assets)\//u.test(name)
      || ['toolchain.lock.json','package.json','pnpm-lock.yaml','global.json','rust-toolchain.toml','.cargo/config.toml','Directory.Build.props','Directory.Build.targets','NOTICE','LICENSE','mods/bepinex/MystiaStewardCompanion.BepInEx.csproj','mods/bepinex/References/references.lock.json'].includes(name))
    && existsSync(path.join(repository,name)));
  const source = [...new Set(names)].sort().map(name => {
    const file=path.join(repository,name); requireValue(lstatSync(file).isFile() && !lstatSync(file).isSymbolicLink(),'源身份不接受链接或非文件。');
    return `${sha256(readFileSync(file))}  ${name}`;
  }).join('\n');
  return { schemaVersion:1, product:'mystia-steward-companion', version:JSON.parse(readFileSync(path.join(repository,'package.json'),'utf8')).version,
    sourceCommit:git(['rev-parse','HEAD']), sourceDigest:sha256(source), toolchainSha256:sha256(readFileSync(path.join(repository,'toolchain.lock.json'))), businessProtocolVersion:1, clientProtocolVersion:2 };
}
export function createManifest(root, metadata) {
  const files = [...readPackageTree(root).values()].filter(file => file.path !== manifestName).sort((a,b)=>a.path.localeCompare(b.path,'en'));
  const manifest = {...metadata,files}; writeFileSync(path.join(root,manifestName),`${JSON.stringify(manifest,null,2)}\n`,'utf8');
  return validatePackage(root);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    const [operation, directory, ...extra] = process.argv.slice(2);
    requireValue(['create','validate'].includes(operation) && directory, '用法：package-manifest.mjs create|validate <包目录> [保留路径...]');
    const repository = fileURLToPath(new URL('..',import.meta.url));
    const manifest = operation === 'create' ? createManifest(directory,repositoryMetadata(repository)) : validatePackage(directory,{allowPreserved:extra.length>0,preservePaths:extra});
    console.log(JSON.stringify(manifest));
  } catch(error) { console.error(error.message); process.exitCode=1; }
}
