/**
 * 完整 Windows ZIP 的应用本地 VC 运行库补齐与只读交付校验。
 * 仅从根工具锁选中的正式 VS Redist/x64/CRT 目录读取，绝不从 System32、PATH 或下载站取 DLL。
 * 不执行待分发程序；PE 导入由同一锁定 MSVC 的 dumpbin 读取，版本/签名由 Windows 元数据 API 读取。
 */
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { copyFileSync, constants, existsSync, lstatSync, readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { prepareWindowsToolchain, validateWindowsIdentity, verifyProjectToolchain } from './flutter-windows-toolchain.mjs';
import { requiredFiles } from './package-manifest.mjs';

const repository = fileURLToPath(new URL('..', import.meta.url));
export const runtimeFileNames = Object.freeze(['msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll']);
const noticeName = 'THIRD-PARTY-NOTICES.txt';
const sha256 = bytes => createHash('sha256').update(bytes).digest('hex');
const requireValue = (condition, message) => { if (!condition) throw new Error(message); };
const vcImport = /^(?:msvcp|vcruntime|concrt|vccorlib)\d[^/\\]*\.dll$/iu;

/** 打包创建 staging 之前核对最小启动与许可集合，不解析 Flutter 的具体资源格式。 */
export function validateWindowsCriticalFiles(directory) {
  for (const relative of requiredFiles.filter(name => name.startsWith('companion/')).map(name => name.slice('companion/'.length))) {
    const file = path.join(directory, relative);
    requireValue(existsSync(file), `完整 Windows 包缺少必需文件：${relative}`);
    realPath(file); const stat = lstatSync(file);
    requireValue(stat.isFile() && stat.size > 0, `Windows 必需文件不是非空普通文件：${relative}`);
  }
}

/** 检查每层物理路径后才遍历或复制，避免链接把来源或输出重定向到另一目录。 */
function realPath(target) {
  let cursor = path.resolve(target);
  while (true) {
    requireValue(!lstatSync(cursor).isSymbolicLink(), `拒绝运行库路径链接：${cursor}`);
    const parent = path.dirname(cursor); if (parent === cursor) break; cursor = parent;
  }
  return path.resolve(target);
}

/** 先验证 PE 签名及机器类型；同名的 x86 DLL 或文本占位不能成为正式运行库。 */
export function requireX64Pe(bytes) {
  requireValue(bytes.length >= 64 && bytes.readUInt16LE(0) === 0x5a4d, '不是有效 PE 文件。');
  const pe = bytes.readUInt32LE(0x3c);
  requireValue(pe <= bytes.length - 26 && bytes.readUInt32LE(pe) === 0x4550
    && bytes.readUInt16LE(pe + 4) === 0x8664 && bytes.readUInt16LE(pe + 24) === 0x20b, 'Windows 载荷必须为 x64 PE32+。');
}

/** 同时读取常规和 delay-load 导入。dumpbin 输出中的文件标题不会匹配整行 DLL 名。 */
export function parsePeImports(output) {
  const names = [...output.matchAll(/^\s+([a-z0-9_.-]+\.dll)\s*$/gimu)].map(match => match[1].toLowerCase());
  requireValue(names.length > 0, 'PE 导入表为空或无法识别。');
  return [...new Set(names)].sort();
}

function inspectPe(file, prepared) {
  realPath(file); const bytes = readFileSync(file); requireX64Pe(bytes);
  const dumpbin = path.join(path.dirname(prepared.identity.compilerPath), 'dumpbin.exe');
  const result = spawnSync(dumpbin, ['/nologo', '/dependents', file], {encoding:'utf8',windowsHide:true,timeout:30000,maxBuffer:1048576});
  requireValue(!result.error && result.status === 0, `PE 导入检查失败：${file}：${result.error?.message ?? result.stderr}`);
  return {sha256:sha256(bytes),bytes:bytes.length,imports:parsePeImports(result.stdout)};
}

/** 对实际导入设置交付门禁；新插件若增加另一种 VC 依赖，必须先审查再扩展交付集合。 */
export function validateRuntimeImports(records, updaterImports = null) {
  for (const record of records) for (const name of record.imports.filter(name => vcImport.test(name))) {
    requireValue(runtimeFileNames.includes(name), `未审查的 VC 运行库依赖：${record.path} -> ${name}`);
  }
  if (updaterImports) requireValue(!updaterImports.some(name => vcImport.test(name)),
    '独立 updater 仍动态依赖 VC 运行库；检查根 .cargo/config.toml 和覆盖它的 RUSTFLAGS。');
}

/** VS 的 Redist 目录版本与签名 DLL 的服务补丁号可能不同，不能把目录名冒充文件版本。
 * 文件必须属于同一锁定 14.x 系列且不早于目录基线；三项的实际版本必须完全一致。
 * 每次交付另验证微软签名及源/目标 SHA-256，记录实际补丁号供复核。
 */
export function validateRuntimeMetadata(records, directoryVersion) {
  requireValue(records.length === runtimeFileNames.length, 'VC 运行库集合不完整。');
  const minimum = directoryVersion.split('.').map(Number);
  const versions = new Set(); const names = new Set();
  for (const record of records) {
    requireValue(runtimeFileNames.includes(record.name) && !names.has(record.name), 'VC 运行库名称未知或重复。'); names.add(record.name);
    requireValue(/^\d+\.\d+\.\d+\.\d+$/u.test(record.fileVersion), 'VC 文件版本无效。');
    const actual = record.fileVersion.split('.').map(Number); versions.add(record.fileVersion);
    requireValue(actual[0] === minimum[0] && actual[1] === minimum[1] && actual[2] >= minimum[2], 'VC 文件版本不属于锁定系列或早于基线。');
    requireValue(record.signatureStatus === 'Valid'
      && /(?:^|,\s*)CN=Microsoft (?:Corporation|Windows)(?:,|$)/u.test(record.signer)
      && /(?:^|,\s*)O=Microsoft Corporation(?:,|$)/u.test(record.signer), 'VC DLL 缺少有效微软签名。');
  }
  requireValue(versions.size === 1, 'VC 运行库文件补丁版本混装。');
}

function readRuntimeSource(prepared) {
  const policy = JSON.parse(readFileSync(path.join(repository,'toolchain.lock.json'),'utf8')).flutterWindows;
  validateWindowsIdentity(prepared.identity, policy);
  const parent = realPath(path.join(prepared.identity.visualStudioPath,'VC/Redist/MSVC',prepared.identity.vcRuntimeVersion,'x64'));
  const crt = readdirSync(parent).filter(name => /^Microsoft\.VC14\d\.CRT$/u.test(name) && lstatSync(path.join(parent,name)).isDirectory());
  requireValue(crt.length === 1, '锁定 VS Redist x64 CRT 目录缺失或不唯一。');
  const directory = realPath(path.join(parent,crt[0]));
  const files = runtimeFileNames.map(name => realPath(path.join(directory,name)));
  // 文件路径通过环境 JSON 传递，绝不拼入 PowerShell 代码；不会输出继承环境或签名凭据。
  const script = `$ErrorActionPreference='Stop'; $paths=ConvertFrom-Json $env:MYSTIA_RUNTIME_FILES; $rows=@(foreach($file in $paths){ $v=[Diagnostics.FileVersionInfo]::GetVersionInfo($file); $s=Get-AuthenticodeSignature -LiteralPath $file; [pscustomobject]@{name=[IO.Path]::GetFileName($file);fileVersion=('{0}.{1}.{2}.{3}' -f $v.FileMajorPart,$v.FileMinorPart,$v.FileBuildPart,$v.FilePrivatePart);signatureStatus=$s.Status.ToString();signer=$s.SignerCertificate.Subject} }); ConvertTo-Json -InputObject $rows -Compress`;
  const executable = path.join(process.env.SystemRoot,'System32/WindowsPowerShell/v1.0/powershell.exe');
  const metadata = spawnSync(executable,['-NoProfile','-NonInteractive','-Command',script],{
    // PS7 父进程的 PSModulePath 不能交给系统 PS5，否则同名 Security 模块可能解析到不兼容版本。
    encoding:'utf8',windowsHide:true,timeout:30000,env:{...process.env,
      PSModulePath:path.join(process.env.SystemRoot,'System32/WindowsPowerShell/v1.0/Modules'),
      MYSTIA_RUNTIME_FILES:JSON.stringify(files)}});
  requireValue(!metadata.error && metadata.status === 0, `无法读取 VC 文件版本/签名：${metadata.error?.message ?? metadata.stderr}`);
  const records = JSON.parse(metadata.stdout); validateRuntimeMetadata(records,prepared.identity.vcRuntimeVersion);
  return records.map(record => ({...record,source:path.join(directory,record.name),...inspectPe(path.join(directory,record.name),prepared)}));
}

/** 只复制缺失的明确运行库或声明文件；现有同名文件必须逐字节相同，绝不覆盖运行中的组件。 */
export function copyVerifiedFile(source, destination, expectedHash) {
  realPath(source); realPath(path.dirname(destination));
  const current = sha256(readFileSync(source)); requireValue(current === expectedHash, '复制前来源摘要已变化。');
  if (existsSync(destination)) realPath(destination);
  else copyFileSync(source,destination,constants.COPYFILE_EXCL);
  requireValue(sha256(readFileSync(destination)) === expectedHash && sha256(readFileSync(source)) === expectedHash,
    `目标文件冲突或复制期间源摘要变化：${destination}`);
}

function inspectBundle(directory, prepared) {
  const records=[]; const queue=[realPath(directory)];
  // Windows 装载器只解析 EXE/DLL；Flutter data/app.so 是由 Dart 自行装载的 ELF AOT 快照，
  // 其完整字节仍由整包清单保护，不把该快照、JSON、字体或资源误当 Windows PE。
  while(queue.length) {
    const folder=queue.pop();
    for(const name of readdirSync(folder)) {
      const file=realPath(path.join(folder,name)); const stat=lstatSync(file);
      if(stat.isDirectory())queue.push(file);
      else if(/\.(?:exe|dll)$/iu.test(name))records.push({path:path.relative(directory,file).replaceAll('\\','/'),...inspectPe(file,prepared)});
    }
  }
  return records;
}

/** build-windows 调用补齐；打包调用只读模式复核当前工具链来源、DLL字节和真实PE导入。 */
export function verifyWindowsRuntime(directory, prepared, {complete=false,updater=null} = {}) {
  requireValue(process.platform === 'win32', 'Windows 运行库校验需要 Windows 主机。');
  realPath(directory); const sources=readRuntimeSource(prepared);
  const notice=path.join(repository,'NOTICE'); const noticeHash=sha256(readFileSync(notice));
  if(complete) {
    for(const entry of sources)copyVerifiedFile(entry.source,path.join(directory,entry.name),entry.sha256);
    copyVerifiedFile(notice,path.join(directory,noticeName),noticeHash);
  }
  validateWindowsCriticalFiles(directory);
  for(const entry of sources) {
    const file=path.join(directory,entry.name); requireValue(existsSync(file),`完整 Windows 包缺少 ${entry.name}`);realPath(file);
    requireValue(sha256(readFileSync(file)) === entry.sha256, `Windows 包中的 ${entry.name} 与锁定 VS 来源不一致。`);
  }
  requireValue(existsSync(path.join(directory,noticeName)) && sha256(readFileSync(path.join(directory,noticeName)))===noticeHash,'Windows 包缺少当前第三方许可声明。');
  const records=inspectBundle(directory,prepared); const updaterRecord=updater?inspectPe(updater,prepared):null;
  validateRuntimeImports(records,updaterRecord?.imports);
  return {schemaVersion:1,profile:prepared.profile,visualStudioVersion:prepared.identity.visualStudioVersion,
    redistDirectoryVersion:prepared.identity.vcRuntimeVersion,runtimeFiles:sources.map(({source,...record})=>record),
    noticeSha256:noticeHash,peFiles:records,updater:updaterRecord,executedPayloads:false};
}

if(process.argv[1] && path.resolve(process.argv[1])===fileURLToPath(import.meta.url)) {
  try {
    const [mode,directory,updater]=process.argv.slice(2);
    requireValue(['complete','verify'].includes(mode)&&directory&&process.argv.length<=5,'用法：windows-runtime.mjs complete|verify <Windows目录> [updater.exe]');
    const prepared=prepareWindowsToolchain();
    if(mode==='complete')verifyProjectToolchain(path.join(repository,'apps/companion-flutter'),prepared);
    const evidence=verifyWindowsRuntime(path.resolve(directory),prepared,{complete:mode==='complete',updater});
    console.log(JSON.stringify(evidence,null,2));
  }catch(error){console.error(error.message);process.exitCode=1;}
}
