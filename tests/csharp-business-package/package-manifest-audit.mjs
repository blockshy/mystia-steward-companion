import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createManifest, requiredFiles, validatePackage, manifestName } from '../../scripts/package-manifest.mjs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';

// 人工载荷位于仓库 temp，不加载任何 EXE/DLL，测试不访问游戏目录。
const repository=fileURLToPath(new URL('../..',import.meta.url));
mkdirSync(path.join(repository,'temp'),{recursive:true});
const root=mkdtempSync(path.join(repository,'temp/package-manifest-test-'));
const metadata={schemaVersion:1,product:'mystia-steward-companion',version:'1.3.1',sourceCommit:'a'.repeat(40),sourceDigest:'b'.repeat(64),toolchainSha256:'c'.repeat(64),businessProtocolVersion:1,clientProtocolVersion:2};
let checks=0;
// 可选的矩阵模式使用同一目录同一字节调用生产验证器；不是各语言各自生成相似夹具。
const external = process.argv.slice(2);
if (external.length && (external.length !== 4 || external[0] !== '--rust' || external[2] !== '--dotnet')) throw new Error('用法：[--rust updater.exe --dotnet CSharpBusinessPackageSmoke.dll]');
function parity(expected,label) {
  if (!external.length) return;
  for (const [command,args] of [[external[1],['--validate-package',root]],['dotnet',[external[3],'--validate',root]]]) {
    const result=spawnSync(command,args,{encoding:'utf8',windowsHide:true});
    assert.equal(result.error,undefined,`${label}: ${result.error?.message}`);
    assert.equal(result.status===0,expected,`${label}:跨语言判断不一致 ${result.stderr}`);
  }
}
function reset(){
  // 先核对绝对目标始终是本次独占的 temp 直接子目录，再递归清理。
  assert.equal(path.dirname(root),path.join(repository,'temp'));
  assert.match(path.basename(root),/^package-manifest-test-/u);
  rmSync(root,{recursive:true,force:true});
  for(const name of [...requiredFiles,'companion/data/empty.asset']){
    mkdirSync(path.dirname(path.join(root,name)),{recursive:true});
    writeFileSync(path.join(root,name),name.endsWith('empty.asset')?'':'人工组件:'+name);
  }
  createManifest(root,metadata);
}
function edit(callback){const file=path.join(root,manifestName);const value=JSON.parse(readFileSync(file,'utf8'));callback(value);writeFileSync(file,JSON.stringify(value));}
function reject(label,change){reset();change();assert.throws(()=>validatePackage(root),undefined,label);parity(false,label);checks++;}
reset();validatePackage(root);parity(true,'有效完整包');checks++;
for(const name of requiredFiles){
  reject('缺失'+name,()=>rmSync(path.join(root,name)));
  reject('混版'+name,()=>writeFileSync(path.join(root,name),'changed'));
  // 同步删除清单条目，模拟在生成清单之前已经漏装；零长度同时同步大小/摘要，避免只测到 hash 不符。
  reject('未打包'+name,()=>{rmSync(path.join(root,name));edit(m=>{m.files=m.files.filter(file=>file.path!==name);});});
  reject('必需项零长度'+name,()=>{writeFileSync(path.join(root,name),'');edit(m=>{const file=m.files.find(file=>file.path===name);file.size=0;file.sha256=createHash('sha256').update('').digest('hex');});});
}
for(const value of ['../escape','/absolute','companion/../escape','companion/a:b','companion/NUL','companion/trailing.','companion\\wrong'])
  reject(value,()=>edit(m=>{m.files[0].path=value;}));
reject('重复JSON字段',()=>{const file=path.join(root,manifestName);writeFileSync(file,readFileSync(file,'utf8').replace('"schemaVersion": 1','"schemaVersion": 1, "schemaVersion": 1'));});
reject('大小写重复',()=>edit(m=>{m.files[1]={...m.files[0],path:m.files[0].path.toUpperCase()};}));
reject('额外DLL',()=>writeFileSync(path.join(root,'extra.dll'),'bad'));
reject('额外资源',()=>writeFileSync(path.join(root,'companion/data/extra'),'bad'));
reject('错误协议',()=>edit(m=>{m.clientProtocolVersion=1;}));
reject('非整数大小',()=>edit(m=>{m.files[0].size=0.5;}));
reject('附加字段',()=>edit(m=>{m.extra=true;}));
reject('空版本后缀',()=>edit(m=>{m.version='1.3.1-';}));
for(const key of ['version','sourceCommit','sourceDigest','toolchainSha256']) reject('字符串字段数组别名'+key,()=>edit(m=>{m[key]=[m[key]];}));
for(const token of ['1.0','1e0','-0']) reject('非规范整数字面量'+token,()=>{const file=path.join(root,manifestName);writeFileSync(file,readFileSync(file,'utf8').replace('"schemaVersion": 1',`"schemaVersion": ${token}`));});
reset();mkdirSync(path.join(root,'user-data'));writeFileSync(path.join(root,'user-data/note.txt'),'keep');
assert.throws(()=>validatePackage(root));checks++;
validatePackage(root,{allowPreserved:true});checks++;
console.log('PASS: '+checks+' 项 Node 完整包边界与附加文件断言。');
rmSync(root,{recursive:true,force:true});
