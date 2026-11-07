/** 合成可验证的两种 Windows 目录；文本载荷只验证打包契约，不能替代 PE、签名或真实运行验证。 */
import { cpSync, existsSync, mkdirSync, renameSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createManifest, requiredFiles } from '../../scripts/package-manifest.mjs';
import { createPortableManifest, portableRootName, portableExecutableName } from '../../scripts/portable-client-package.mjs';

export function createSyntheticWindowsTrees(root,version='9.9.9',sourceCommit='9'.repeat(40)) {
  if(existsSync(root)) throw new Error('合成两包夹具根必须不存在。');
  const mod=path.join(root,'mystia-steward-companion');
  for(const name of [...requiredFiles,'companion/data/optional-empty.asset']) {
    const file=path.join(mod,name); mkdirSync(path.dirname(file),{recursive:true});
    writeFileSync(file,name.endsWith('empty.asset')?'':'synthetic payload: '+name);
  }
  createManifest(mod,{schemaVersion:1,product:'mystia-steward-companion',version,sourceCommit,
    sourceDigest:'b'.repeat(64),toolchainSha256:'c'.repeat(64),businessProtocolVersion:1,clientProtocolVersion:2});
  const standalone=path.join(root,portableRootName); cpSync(path.join(mod,'companion'),standalone,{recursive:true});
  renameSync(path.join(standalone,'mystia-steward-companion.exe'),path.join(standalone,portableExecutableName));
  createPortableManifest(standalone,mod); return {mod,standalone};
}
if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) createSyntheticWindowsTrees(...process.argv.slice(2));
