/**
 * Windows 分发边界的跨平台单元验证：不要求 VS、不执行 PE、不读取系统 DLL。
 * 覆盖架构、导入、来源元数据及复制冲突；真实签名和加载路径另由 Windows 完整包校验证明。
 */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, renameSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { copyVerifiedFile, parsePeImports, requireX64Pe, runtimeFileNames, validateRuntimeImports, validateRuntimeMetadata, validateWindowsCriticalFiles } from '../../scripts/windows-runtime.mjs';
import { requiredFiles } from '../../scripts/package-manifest.mjs';

const root = fileURLToPath(new URL('../..', import.meta.url));
let checks = 0;
function pass(action) { action(); checks++; }
function reject(action, pattern) { assert.throws(action, pattern); checks++; }
const pe = Buffer.alloc(128); pe.writeUInt16LE(0x5a4d); pe.writeUInt32LE(64,0x3c);
pe.writeUInt32LE(0x4550,64); pe.writeUInt16LE(0x8664,68); pe.writeUInt16LE(0x20b,88);
pass(() => requireX64Pe(pe));
reject(() => requireX64Pe(Buffer.from('not-pe')), /PE/u);
const wrongArchitecture=Buffer.from(pe); wrongArchitecture.writeUInt16LE(0x14c,68);
reject(() => requireX64Pe(wrongArchitecture), /x64/u);
const badOffset=Buffer.from(pe); badOffset.writeUInt32LE(0xffffffff,0x3c);
reject(() => requireX64Pe(badOffset), /x64/u);
pass(() => assert.deepEqual(parsePeImports('Image has dependencies:\n    KERNEL32.dll\n    VCRUNTIME140.dll\nDelay loads:\n    VCRUNTIME140_1.dll\n    KERNEL32.dll\n'), ['kernel32.dll','vcruntime140.dll','vcruntime140_1.dll']));
reject(() => parsePeImports('unrecognized tool output'), /导入表/u);
pass(() => validateRuntimeImports([{path:'runner.exe',imports:runtimeFileNames}], ['kernel32.dll','api-ms-win-crt-runtime-l1-1-0.dll']));
reject(() => validateRuntimeImports([{path:'plugin.dll',imports:['concrt140.dll']}]), /未审查/u);
reject(() => validateRuntimeImports([], ['vcruntime140.dll']), /独立 updater/u);
const metadata = runtimeFileNames.map(name => ({name,fileVersion:'14.50.35719.0',signatureStatus:'Valid',signer:'CN=Microsoft Corporation, O=Microsoft Corporation, C=US'}));
pass(() => validateRuntimeMetadata(metadata, '14.50.35710'));
pass(() => validateRuntimeMetadata(metadata.map(record=>({...record,signer:'CN=Microsoft Windows, O=Microsoft Corporation, C=US'})), '14.50.35710'));
reject(() => validateRuntimeMetadata(metadata.slice(1), '14.50.35710'), /不完整/u);
for (const [key,value,pattern] of [
  ['fileVersion','14.44.35719.0',/版本/u], ['fileVersion','14.50.35000.0',/版本/u],
  ['fileVersion','14.50.35720.0',/混装/u], ['signatureStatus','NotSigned',/微软签名/u],
  ['signer','CN=Untrusted Corporation',/微软签名/u], ['name','msvcp140.dll',/重复/u],
]) {
  const mutated=structuredClone(metadata); mutated[1][key]=value;
  reject(() => validateRuntimeMetadata(mutated,'14.50.35710'),pattern);
}
mkdirSync(path.join(root,'temp'),{recursive:true});
const temporary=mkdtempSync(path.join(root,'temp/windows-runtime-audit-'));
const source=path.join(temporary,'source.dll'), destination=path.join(temporary,'destination.dll');
writeFileSync(source,'controlled-fixture'); const hash=createHash('sha256').update(readFileSync(source)).digest('hex');
pass(() => copyVerifiedFile(source,destination,hash));
pass(() => copyVerifiedFile(source,destination,hash));
writeFileSync(destination,'existing-file-must-not-change');
reject(() => copyVerifiedFile(source,destination,hash), /冲突/u);
pass(() => assert.equal(readFileSync(destination,'utf8'),'existing-file-must-not-change'));
reject(() => copyVerifiedFile(source,destination,'0'.repeat(64)), /来源摘要/u);
// 所有关键运行文件在创建包事务前逐项检查遗漏和零长度；不解析人工资源文件内部格式。
const bundle=path.join(temporary,'companion');
const critical=requiredFiles.filter(name=>name.startsWith('companion/')).map(name=>path.join(temporary,name));
for(const file of critical) {mkdirSync(path.dirname(file),{recursive:true});writeFileSync(file,'fixture');}
pass(()=>validateWindowsCriticalFiles(bundle));
for(const file of critical) {
  renameSync(file,`${file}.saved`);
  reject(()=>validateWindowsCriticalFiles(bundle),/缺少必需文件/u);
  renameSync(`${file}.saved`,file);writeFileSync(file,'');
  reject(()=>validateWindowsCriticalFiles(bundle),/非空普通文件/u);
  writeFileSync(file,'fixture');
}
console.log(`PASS: ${checks} Windows runtime boundary checks; real PE/signature checks require the Windows package validator. Evidence: ${temporary}`);
