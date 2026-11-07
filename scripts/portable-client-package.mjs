/**
 * 独立 Windows 分发的目录所有权与完整性清单。它描述包结构，不构成签名或可信发布者证明。
 * 同一二进制仅在独立包内改名；Mod 的 companion 树不加入标记，也不接受便携运行数据。
 */
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { manifestName, readPackageTree, requiredFiles, validatePackage, validatePackageRecords, validateRelativePath } from './package-manifest.mjs';

export const portableRootName = 'mystia-steward-companion-windows-x64';
export const portableManifestName = 'portable-client-v1.manifest';
export const portableHeader = 'mystia-portable-client-v1\n';
export const portableExecutableName = 'mystia-steward-companion-portable.exe';
const installedExecutableName = 'mystia-steward-companion.exe';
// 即使用户把已知运行文件拖出了 client-data，也不能将其重新认定为待发布资源。
const privateDataNames = ['client-data','preferences.json','window-state-v1.txt','client-id.dpapi','connection-token.dpapi'];
const limits = {count:8192, file:536870912, total:2147483648, manifest:4194304};
const requireValue = (condition, message) => { if (!condition) throw new Error(message); };
const ordinal = (a,b) => a < b ? -1 : a > b ? 1 : 0;
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
const portablePath = value => value === installedExecutableName ? portableExecutableName : value;
export const portableRequiredFiles = Object.freeze(requiredFiles.filter(name => name.startsWith('companion/'))
  .map(name => portablePath(name.slice('companion/'.length))));

/** v1 载荷名称限定为 ASCII，跨 JS/C++ 使用同一字节排序和大小写折叠；用户解压根名称不受此限制。 */
function validatePortablePath(value) {
  validateRelativePath(value);
  requireValue(/^[\x20-\x7e]+$/u.test(value), '便携清单 v1 的载荷路径必须为 ASCII。');
  const components = value.toLowerCase().split('/');
  requireValue(!components.some(part=>privateDataNames.includes(part)) && !components.includes(portableManifestName)
    && !components.includes(installedExecutableName), `便携载荷包含保留名称：${value}`);
  return value;
}

/** 规范文本只有 LF、三个字段与末尾换行，不接受 BOM、数字别名、乱序或大小写重复。 */
export function parsePortableManifest(text) {
  requireValue(typeof text === 'string' && Buffer.byteLength(text,'utf8') <= limits.manifest
    && text.startsWith(portableHeader) && text.endsWith('\n') && !text.includes('\r'), '便携清单编码、头部或换行无效。');
  const lines = text.slice(portableHeader.length,-1).split('\n');
  requireValue(lines.length > 0 && lines.length <= limits.count, '便携载荷数量无效。');
  const records = new Map(); const folded = new Set(); const directoryNames=new Map(); let previous=''; let total=0;
  for (const line of lines) {
    const parts=line.split('\t');
    requireValue(parts.length===3 && /^[0-9a-f]{64}$/u.test(parts[0]) && /^(?:0|[1-9][0-9]*)$/u.test(parts[1]), '便携清单字段或数字格式无效。');
    const [sha256,sizeText,name]=parts; validatePortablePath(name); const size=Number(sizeText);
    requireValue(Number.isSafeInteger(size) && size<=limits.file && (size>0 || (!portableRequiredFiles.includes(name) && !/\.(exe|dll)$/iu.test(name))), '便携载荷大小无效。');
    requireValue(ordinal(previous,name)<0 && !folded.has(name.toLowerCase()), '便携清单未按 ordinal 排序或包含重复路径。');
    previous=name; folded.add(name.toLowerCase()); total+=size; requireValue(total<=limits.total,'便携包总大小超过2GiB。');
    records.set(name,{path:name,size,sha256});
    for(let parent=path.posix.dirname(name); parent!=='.';parent=path.posix.dirname(parent)) {
      const existing=directoryNames.get(parent.toLowerCase());
      requireValue(existing===undefined || existing===parent,'便携清单目录大小写冲突。'); directoryNames.set(parent.toLowerCase(),parent);
    }
  }
  for(const name of portableRequiredFiles) requireValue(records.has(name),`便携包缺少必需组件：${name}`);
  // 不能把某一文件同时声明为另一载荷的父目录。
  for(const name of records.keys()) for(let parent=path.posix.dirname(name); parent!=='.';parent=path.posix.dirname(parent))
    requireValue(!folded.has(parent.toLowerCase()),'便携清单文件与目录冲突。');
  return records;
}

function compareRecords(actual,expected,label) {
  requireValue(actual.size===expected.size,`${label}文件集合不符。`);
  for(const [name,wanted] of expected) {
    const found=actual.get(name);
    requireValue(found && found.size===wanted.size && found.sha256===wanted.sha256,`${label}载荷不一致：${name}`);
  }
}

function projection(manifest) {
  return new Map(manifest.files.filter(item=>item.path.startsWith('companion/')).map(item=>{
    const name=portablePath(item.path.slice('companion/'.length)); validatePortablePath(name);
    return [name,{...item,path:name}];
  }));
}

/** 任何层级的运行数据/便携标记都不能被泛化的全树清单吸收为 Mod 托管载荷，包括空目录。 */
export function assertCleanCompanionTree(root) {
  // 先只枚举名称和元数据，遇到保留目录便停止，不为报错先读取其中可能存在的真实凭据。
  for(let current=path.resolve(root);;current=path.dirname(current)) {
    requireValue(!lstatSync(current).isSymbolicLink(),'Windows 构建根不能经过链接。');
    if(path.dirname(current)===current) break;
  }
  const visit=directory=>{
    for(const entry of readdirSync(directory,{withFileTypes:true})) {
      requireValue(![...privateDataNames,portableManifestName,portableExecutableName].includes(entry.name.toLowerCase()),
        'Windows 构建树包含 client-data 或便携专用文件；输入与旧 dist 均保留，请使用干净构建树。');
      const target=path.join(directory,entry.name); const stat=lstatSync(target);
      requireValue(!stat.isSymbolicLink(),'Windows 分发不接受链接载荷。');
      requireValue(stat.isDirectory() || stat.nlink===1,'Windows 分发不接受硬链接载荷。');
      if(stat.isDirectory()) visit(target);
    }
  }; visit(root);
  readPackageTree(root); // 保留名称已拒绝后，再复用全树的路径、数量、大小和内容读取边界。
}

function assertDirectoriesKnown(root, records) {
  const directories=new Set();
  for(const name of records.keys()) for(let parent=path.posix.dirname(name); parent!=='.';parent=path.posix.dirname(parent)) directories.add(parent);
  const visit=(directory,prefix)=>{
    for(const entry of readdirSync(directory,{withFileTypes:true})) {
      const name=prefix+entry.name; const target=path.join(directory,entry.name); const stat=lstatSync(target);
      requireValue(!stat.isSymbolicLink(),'便携包不接受链接载荷。');
      requireValue(stat.isDirectory() || stat.nlink===1,'便携包不接受硬链接载荷。');
      if(stat.isDirectory()) { requireValue(directories.has(name),`便携包含未知或空目录：${name}`); visit(target,name+'/'); }
      else requireValue(records.has(name) || name===portableManifestName,`便携包含未知文件：${name}`);
    }
  }; visit(root,'');
}

export function createPortableManifest(root, modPackage) {
  assertCleanCompanionTree(path.join(modPackage,'companion')); const manifest=validatePackage(modPackage);
  const expected=projection(manifest); assertDirectoriesKnown(root,expected); const actual=readPackageTree(root);
  requireValue(!actual.has(portableManifestName),'便携标记已存在，拒绝覆盖。');
  compareRecords(actual,expected,'独立包与 Mod'); assertDirectoriesKnown(root,expected);
  const text=portableHeader+[...expected.values()].sort((a,b)=>ordinal(a.path,b.path)).map(item=>`${item.sha256}\t${item.size}\t${item.path}\n`).join('');
  parsePortableManifest(text); // 生成器同样通过 Native 共同约定的严格规范。
  writeFileSync(path.join(root,portableManifestName),text,{encoding:'utf8',flag:'wx'});
  validatePortableDirectory(root); return text;
}

export function validatePortableDirectory(root) {
  // 清单先有界读取，未知条目仅检查名称；绝不把 client-data 吸收到完整性扫描或发布包。
  for(let current=path.resolve(root);;current=path.dirname(current)) {
    requireValue(!lstatSync(current).isSymbolicLink(),'便携根不能经过链接。');
    if(path.dirname(current)===current) break;
  }
  const marker=lstatSync(path.join(root,portableManifestName));
  requireValue(marker.isFile() && !marker.isSymbolicLink() && marker.nlink===1 && marker.size>0 && marker.size<=limits.manifest,'缺少或超长便携标记。');
  const records=parsePortableManifest(readFileSync(path.join(root,portableManifestName),'utf8'));
  assertDirectoriesKnown(root,records); const tree=readPackageTree(root);
  tree.delete(portableManifestName); compareRecords(tree,records,'便携目录');
  return records;
}

/** ZIP 条目由 PowerShell 以只读、有界流计算摘要；本函数只核对元数据，不提取或执行任何载荷。 */
function archiveRecords(archive,root,markerName) {
  requireValue(Array.isArray(archive?.entries) && archive.entries.length<=limits.count*2+2,'ZIP条目数量无效。');
  const files=new Map(); const names=new Map(); const directories=[]; let total=0; let markerText;
  for(const item of archive.entries) {
    requireValue(typeof item.path==='string' && item.path.startsWith(root+'/'),'ZIP 必须只有约定的唯一外层目录。');
    const directory=item.path.endsWith('/'); const full=directory?item.path.slice(0,-1):item.path;
    const name=full.slice(root.length+1);
    if(name) validateRelativePath(name);
    else requireValue(directory && full===root,'ZIP根条目不是目录。');
    const folded=full.toLowerCase(); requireValue(!names.has(folded),'ZIP存在重复或大小写冲突条目。'); names.set(folded,directory);
    const attributes=item.attributes>>>0; const unixType=(attributes>>>16)&0xf000;
    requireValue((attributes&0x400)===0 && ((attributes&0x10)===0 || directory)
      && (unixType===0 || unixType===(directory?0x4000:0x8000)),'ZIP含链接、特殊文件或类型冲突。');
    requireValue(Number.isSafeInteger(item.size) && item.size>=0 && item.size<=limits.file,'ZIP条目大小无效。');
    if(directory) { requireValue(item.size===0,'ZIP目录携带非空数据。'); directories.push(name); continue; }
    requireValue(name.length>0 && /^[0-9a-f]{64}$/u.test(item.sha256),'ZIP摘要记录无效。');
    total+=item.size; requireValue(total<=limits.total && files.size<limits.count+1,'ZIP载荷超出上限。');
    files.set(name,{path:name,size:item.size,sha256:item.sha256});
    if(name===markerName) { requireValue(typeof item.text==='string' && item.size<=limits.manifest && Buffer.byteLength(item.text,'utf8')===item.size
      && hash(Buffer.from(item.text,'utf8'))===item.sha256,'ZIP清单文本与其实际字节不一致。'); markerText=item.text; }
  }
  for(const [name,directory] of names) for(let parent=path.posix.dirname(name);parent!=='.';parent=path.posix.dirname(parent))
    requireValue(names.get(parent)!==false,'ZIP文件与目录冲突。');
  const parents=new Set(['']); for(const name of files.keys()) for(let parent=path.posix.dirname(name);parent!=='.';parent=path.posix.dirname(parent)) parents.add(parent);
  for(const directory of directories) requireValue(parents.has(directory),'ZIP包含未知或空目录。');
  requireValue(markerText!==undefined,'ZIP缺少分发清单。'); return {files,markerText};
}

export function validateArchivePair(input) {
  const mod=archiveRecords(input.mod,'mystia-steward-companion',manifestName);
  const manifest=validatePackageRecords(mod.files,mod.markerText);
  if(input.version) requireValue(manifest.version===input.version,'包内版本与发布版本不一致。');
  if(input.sourceCommit) requireValue(manifest.sourceCommit===input.sourceCommit,'包内源码身份与发布提交不一致。');
  for(const name of mod.files.keys()) requireValue(!name.toLowerCase().split('/').some(part=>[...privateDataNames,portableManifestName,portableExecutableName].includes(part)), 'Mod ZIP 含运行数据或便携专用文件。');
  const standalone=archiveRecords(input.standalone,portableRootName,portableManifestName);
  const records=parsePortableManifest(standalone.markerText); standalone.files.delete(portableManifestName);
  compareRecords(standalone.files,records,'独立 ZIP'); compareRecords(records,projection(manifest),'两 ZIP');
  return {version:manifest.version,files:records.size};
}

if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {
    const [operation,root,source]=process.argv.slice(2);
    if(operation==='create') createPortableManifest(root,source);
    else if(operation==='validate') validatePortableDirectory(root);
    else if(operation==='check-build-tree') assertCleanCompanionTree(root);
    else if(operation==='verify-zip-records') {
      const chunks=[]; let length=0;
      for await(const chunk of process.stdin) { length+=chunk.length; requireValue(length<=32*1024*1024,'ZIP记录输入超出上限。'); chunks.push(chunk); }
      validateArchivePair(JSON.parse(Buffer.concat(chunks).toString('utf8')));
    } else throw new Error('用法：portable-client-package.mjs create|validate|check-build-tree <目录> [Mod包]，或 verify-zip-records 从标准输入读取。');
    console.log('Portable client package validation passed.');
  } catch(error) { console.error(error.message); process.exitCode=1; }
}
