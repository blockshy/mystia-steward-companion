/** 合成目录覆盖便携协议与两包映射；ZIP流读取另由 PowerShell 专项验证，不执行合成 EXE。 */
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { cpSync, linkSync, mkdirSync, mkdtempSync, readFileSync, renameSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { readPackageTree } from '../../scripts/package-manifest.mjs';
import { assertCleanCompanionTree, parsePortableManifest, portableHeader, portableManifestName,
  portableRootName, portableExecutableName, portableRequiredFiles, validateArchivePair, validatePortableDirectory } from '../../scripts/portable-client-package.mjs';
import { createSyntheticWindowsTrees } from './windows-package-fixture.mjs';

const repository=fileURLToPath(new URL('../..',import.meta.url));
const temporary=path.join(repository,'temp'); mkdirSync(temporary,{recursive:true});
const root=mkdtempSync(path.join(temporary,'portable-package-audit-')); let checks=0;
const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
const reject=(action,label)=>{assert.throws(action,undefined,label);checks++;};
try {
  const trees=createSyntheticWindowsTrees(path.join(root,'base'));
  const marker=readFileSync(path.join(trees.standalone,portableManifestName),'utf8');
  const records=parsePortableManifest(marker); assert.equal(records.size,portableRequiredFiles.length+1); checks++;
  assert.equal(validatePortableDirectory(trees.standalone).size,records.size); checks++;
  assert.equal(records.get('data/optional-empty.asset').size,0); checks++;
  assert.deepEqual(readFileSync(path.join(trees.standalone,portableExecutableName)),readFileSync(path.join(trees.mod,'companion/mystia-steward-companion.exe'))); checks++;
  const rows=marker.slice(portableHeader.length,-1).split('\n');
  for(const [label,text] of [
    ['BOM','\uFEFF'+marker],['CRLF',marker.replaceAll('\n','\r\n')],['无末尾LF',marker.slice(0,-1)],
    ['错header',marker.replace('v1','v2')],['无载荷',portableHeader],['乱序',portableHeader+[...rows].reverse().join('\n')+'\n'],
    ['重复路径',marker+rows.at(-1)+'\n'],['大小写hash',marker.replace(/[0-9a-f]{64}\t/u,value=>value.toUpperCase())],
    ['负数',marker.replace(/\t[0-9]+\t/u,'\t-1\t')],['数字前导零',marker.replace(/\t([1-9][0-9]*)\t/u,'\t0$1\t')],
    ['指数',marker.replace(/\t[0-9]+\t/u,'\t1e2\t')],['超长清单','x'.repeat(4194305)],
  ]) reject(()=>parsePortableManifest(text),label);
  for(const name of portableRequiredFiles) reject(()=>parsePortableManifest(portableHeader+rows.filter(row=>!row.endsWith('\t'+name)).join('\n')+'\n'),'遗漏必需'+name);
  for(const value of ['../escape','/absolute','data\\wrong','data/a:b','data/NUL','data/trailing.','data/ trailing ',
    'client-data/preferences.json','data/Client-Data/private','preferences.json','data/window-state-v1.txt','CLIENT-ID.dpapi','connection-token.dpapi',
    'portable-client-v1.manifest','mystia-steward-companion.exe','data/中文']) {
    const edited=[...rows,'0'.repeat(64)+'\t1\t'+value].sort((a,b)=>a.split('\t')[2]<b.split('\t')[2]?-1:1);
    reject(()=>parsePortableManifest(portableHeader+edited.join('\n')+'\n'),value);
  }
  for(const size of ['0','536870913']) {
    const changed=rows.map(row=>row.endsWith('\t'+portableExecutableName)?row.replace(/\t[0-9]+\t/u,'\t'+size+'\t'):row);
    reject(()=>parsePortableManifest(portableHeader+changed.join('\n')+'\n'),'必需程序大小'+size);
  }
  const added=['Data/new.asset','data/new.asset','data'];
  for(const value of added) {
    const changed=[...rows,'0'.repeat(64)+'\t1\t'+value].sort((a,b)=>a.split('\t')[2]<b.split('\t')[2]?-1:1);
    if(value!=='data/new.asset') reject(()=>parsePortableManifest(portableHeader+changed.join('\n')+'\n'),'目录别名或文件冲突'+value);
  }
  // 所有目录变体均只复制本次合成树；未知空目录与污染内容都不被清单生成器默认为合法。
  for(const kind of ['missing-marker','damaged-marker','extra-file','empty-directory','client-data','wrong-bytes','hardlink']) {
    const copy=path.join(root,kind); cpSync(trees.standalone,copy,{recursive:true});
    if(kind==='missing-marker') rmSync(path.join(copy,portableManifestName));
    else if(kind==='damaged-marker') writeFileSync(path.join(copy,portableManifestName),marker+'\n');
    else if(kind==='extra-file') writeFileSync(path.join(copy,'user.txt'),'not packaged');
    else if(kind==='empty-directory' || kind==='client-data') mkdirSync(path.join(copy,kind));
    else if(kind==='wrong-bytes') writeFileSync(path.join(copy,'flutter_windows.dll'),'mixed release');
    else { const file=path.join(copy,'flutter_windows.dll'); const preserved=path.join(root,'hardlink-original'); renameSync(file,preserved); linkSync(preserved,file); }
    reject(()=>validatePortableDirectory(copy),kind);
  }
  for(const name of ['client-data','data/CLIENT-DATA','preferences.json','window-state-v1.txt','client-id.dpapi','data/CONNECTION-TOKEN.dpapi',portableManifestName,portableExecutableName]) {
    const copy=path.join(root,'build-'+checks); cpSync(path.join(trees.mod,'companion'),copy,{recursive:true});
    mkdirSync(path.join(copy,name),{recursive:true}); reject(()=>assertCleanCompanionTree(copy),'构建污染'+name);
  }
  // 这里的元数据记录由本测试的真实合成目录读取；不声称这些数组本身验证过 ZIP 解码。
  const archive=(directory,prefix,markerName)=>({entries:[...readPackageTree(directory).values()].map(file=>({
    ...file,path:prefix+'/'+file.path,attributes:0,...(file.path===markerName?{text:readFileSync(path.join(directory,file.path),'utf8')}:{})}))});
  const pair={mod:archive(trees.mod,'mystia-steward-companion','package-manifest.json'),standalone:archive(trees.standalone,portableRootName,portableManifestName),version:'9.9.9',sourceCommit:'9'.repeat(40)};
  assert.equal(validateArchivePair(pair).files,records.size);checks++;
  const change=(label,mutate)=>{const value=structuredClone(pair);mutate(value);reject(()=>validateArchivePair(value),label);};
  change('错发布版本',value=>value.version='9.9.8'); change('错发布提交',value=>value.sourceCommit='8'.repeat(40));
  change('独立ZIP平铺',value=>value.standalone.entries.forEach(item=>item.path=item.path.slice(portableRootName.length+1)));
  change('ZIP重复',value=>value.standalone.entries.push({...value.standalone.entries[0]}));
  change('ZIP链接',value=>value.standalone.entries[0].attributes=0xa0000000);
  change('ZIP多余目录',value=>value.standalone.entries.push({path:portableRootName+'/unknown/',size:0,sha256:digest(''),attributes:16}));
  change('ZIP变更字节',value=>value.standalone.entries[0].sha256='0'.repeat(64));
  change('自洽但不是同包',value=>{
    const payload=value.standalone.entries.find(item=>item.path.endsWith('/'+portableExecutableName));
    const entry=value.standalone.entries.find(item=>item.path.endsWith('/'+portableManifestName));
    const previous=payload.sha256;payload.sha256='0'.repeat(64); entry.text=entry.text.replace(previous,payload.sha256);
    entry.sha256=digest(entry.text);entry.size=Buffer.byteLength(entry.text);
  });
  console.log(`PASS: ${checks} 项便携清单、目录所有权、污染输入和两包投影边界断言。`);
} finally {
  assert.equal(path.dirname(root),temporary); assert.match(path.basename(root),/^portable-package-audit-/u);
  rmSync(root,{recursive:true,force:true});
}
