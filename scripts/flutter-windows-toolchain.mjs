import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, realpathSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const versionPattern = /^(?:0|[1-9]\d*)(?:\.(?:0|[1-9]\d*)){2,3}$/u;
const environmentNames = ['PATH', 'INCLUDE', 'LIB', 'LIBPATH', 'VSINSTALLDIR', 'VCINSTALLDIR', 'VCToolsInstallDir',
  'VCToolsVersion', 'WindowsSdkDir', 'WindowsSDKVersion', 'WindowsSDKLibVersion', 'UniversalCRTSdkDir', 'UCRTVersion'];
function requireValue(condition, message) { if (!condition) throw new Error(message); }
function keys(value, names, label) {
  requireValue(value && typeof value === 'object' && !Array.isArray(value) &&
    JSON.stringify(Object.keys(value).sort()) === JSON.stringify([...names].sort()), `Invalid ${label} schema.`);
}
function version(value) {
  requireValue(typeof value === 'string' && versionPattern.test(value), `Invalid Windows tool version: ${value}`);
  const parts = value.split('.').map(Number);
  requireValue(parts.every(Number.isSafeInteger), 'Windows version component exceeds exact integer range.');
  return parts;
}
function compare(left, right) {
  const a = version(left), b = version(right);
  for (let index = 0; index < Math.max(a.length, b.length); index++) {
    if ((a[index] ?? 0) !== (b[index] ?? 0)) return (a[index] ?? 0) - (b[index] ?? 0);
  }
  return 0;
}
function range(actual, allowed, label) {
  requireValue(compare(actual, allowed.minInclusive) >= 0 && compare(actual, allowed.maxExclusive) < 0,
    `${label} ${actual} is outside the root lock [${allowed.minInclusive}, ${allowed.maxExclusive}).`);
}
export function validateWindowsLock(value) {
  keys(value, ['profiles'], 'flutterWindows');
  requireValue(Array.isArray(value.profiles) && value.profiles.length > 0, 'Windows profiles are missing.');
  const ids = new Set(), installations = new Set();
  for (const profile of value.profiles) {
    keys(profile, ['id', 'visualStudio', 'generator', 'msvcTools', 'compiler', 'cmake', 'windowsSdk', 'vcRuntime'], 'Windows profile');
    requireValue(typeof profile.id === 'string' && /^[a-z0-9-]+$/u.test(profile.id) && !ids.has(profile.id), 'Invalid/duplicate Windows profile ID.');
    ids.add(profile.id); version(profile.visualStudio); version(profile.windowsSdk); version(profile.vcRuntime);
    requireValue(!installations.has(profile.visualStudio), 'A VS version must identify exactly one profile.');
    installations.add(profile.visualStudio);
    requireValue(['Visual Studio 17 2022', 'Visual Studio 18 2026'].includes(profile.generator), 'Unsupported Windows CMake generator.');
    for (const name of ['msvcTools', 'compiler', 'cmake']) {
      keys(profile[name], ['minInclusive', 'maxExclusive'], `${name} range`);
      const low = version(profile[name].minInclusive), high = version(profile[name].maxExclusive);
      requireValue(compare(profile[name].minInclusive, profile[name].maxExclusive) < 0 &&
        low[0] === high[0] && (low[1] === high[1] || high[1] === low[1] + 1 && high.slice(2).every(v => v === 0)),
      `${name} must have a nonempty range no wider than one minor release.`);
    }
  }
  return value;
}
export function validateWindowsIdentity(identity, policy) {
  validateWindowsLock(policy);
  const profile = policy.profiles.find(item => item.visualStudio === identity.visualStudioVersion);
  requireValue(profile, `Unapproved Visual Studio: ${identity.visualStudioVersion}. Update the root lock after verification.`);
  requireValue(identity.generator === profile.generator && identity.platform === 'x64', 'Unexpected Windows generator/target.');
  range(identity.msvcToolsVersion, profile.msvcTools, 'MSVC tools');
  range(identity.compilerVersion, profile.compiler, 'C++ compiler');
  range(identity.cmakeVersion.replace(/-msvc\d+$/u, ''), profile.cmake, 'VS-bundled CMake');
  requireValue(identity.windowsSdkVersion === profile.windowsSdk, `Windows SDK ${identity.windowsSdkVersion} differs from ${profile.windowsSdk}.`);
  requireValue(identity.vcRuntimeVersion === profile.vcRuntime, `VC runtime ${identity.vcRuntimeVersion} differs from ${profile.vcRuntime}.`);
  return profile;
}
export function parseVswhere(text) {
  // The locked Flutter implementation removes this unused field as well: some
  // localized VS installers emit an unescaped quote inside description.
  let value;
  try { value = JSON.parse(text); }
  catch { value = JSON.parse(text.replace(/^\s*"description"\s*:\s*".*",?\r?\n/gmu, '')); }
  requireValue(Array.isArray(value) && value.length <= 1, 'vswhere did not select one candidate.');
  return value[0] ?? null;
}
export function mergeWindowsEnvironment(base, additions) {
  const names = new Set(Object.keys(additions).map(name => name.toLowerCase()));
  return { ...Object.fromEntries(Object.entries(base).filter(([name]) => !names.has(name.toLowerCase()))), ...additions };
}
function invoke(command, args, env, extra = {}) {
  const result = spawnSync(command, args, { cwd: root, env, shell: false, windowsHide: true, encoding: 'utf8',
    timeout: 120000, maxBuffer: 4 * 1024 * 1024, ...extra });
  if (result.error || result.status !== 0) throw new Error(`${path.basename(command)} failed: ${result.error?.message ?? result.stderr ?? result.status}`);
  return result.stdout;
}
function samePath(left, right) {
  const normalized = value => path.win32.normalize(value).replace(/\\$/u, '').toLowerCase();
  return normalized(left) === normalized(right);
}
function safeBatchPath(value) {
  requireValue(typeof value === 'string' && path.win32.isAbsolute(value) && !/["%!^&|<>\r\n]/u.test(value), 'Unsupported Windows tool path.');
  return value;
}
function cacheEntries(text) {
  const result = new Map();
  for (const line of text.split(/\r?\n/u)) {
    const match = /^([^/#][^:]*):[^=]+=(.*)$/u.exec(line);
    if (!match) continue;
    requireValue(!result.has(match[1]), 'Duplicate CMake cache field.'); result.set(match[1], match[2]);
  }
  return result;
}
function cmakeValue(text, name) {
  const matches = [...text.matchAll(new RegExp(`^set\\(${name} "([^"\\r\\n]*)"\\)$`, 'gmu'))];
  requireValue(matches.length === 1, `Missing/duplicated actual ${name}.`); return matches[0][1];
}
export function validateCmakeBuild(directory, prepared) {
  const cache = cacheEntries(readFileSync(path.join(directory, 'CMakeCache.txt'), 'utf8'));
  const expected = prepared.identity;
  requireValue(samePath(cache.get('CMAKE_COMMAND') ?? '', expected.cmakePath) &&
    samePath(cache.get('CMAKE_GENERATOR_INSTANCE') ?? '', expected.visualStudioPath) &&
    cache.get('CMAKE_GENERATOR') === expected.generator && cache.get('CMAKE_GENERATOR_PLATFORM') === 'x64' &&
    cache.get('CMAKE_GENERATOR_TOOLSET') === prepared.environment.CMAKE_GENERATOR_TOOLSET,
  'Actual Flutter CMake command/instance/generator/toolset differs from the verified toolchain.');
  const files = path.join(directory, 'CMakeFiles');
  const compilerFiles = readdirSync(files).map(name => path.join(files, name, 'CMakeCXXCompiler.cmake')).filter(existsSync);
  requireValue(compilerFiles.length === 1, 'Ambiguous/stale CMake compiler identification; use a new build directory.');
  const compiler = readFileSync(compilerFiles[0], 'utf8');
  requireValue(samePath(cmakeValue(compiler, 'CMAKE_CXX_COMPILER'), expected.compilerPath) &&
    cmakeValue(compiler, 'CMAKE_CXX_COMPILER_VERSION') === expected.compilerVersion &&
    cmakeValue(compiler, 'CMAKE_CXX_COMPILER_ID') === 'MSVC', 'Actual Flutter compiler differs from preflight.');
  requireValue(samePath(cmakeValue(compiler, 'CMAKE_CXX_COMPILER_LINKER'), expected.linkerPath), 'Actual CMake linker differs from preflight.');
  const allBuild = readFileSync(path.join(directory, 'ALL_BUILD.vcxproj'), 'utf8');
  const sdk = [...allBuild.matchAll(/<WindowsTargetPlatformVersion>([^<]+)<\/WindowsTargetPlatformVersion>/gu)];
  requireValue(sdk.length === 1 && sdk[0][1] === expected.windowsSdkVersion, 'Actual Flutter Windows SDK differs from preflight.');
}
export function verifyProjectToolchain(project, prepared) {
  const directory = path.join(project, 'build/windows/x64');
  validateCmakeBuild(directory, prepared);
  const evidence = { schemaVersion: 1, profile: prepared.profile, identity: prepared.identity, actualFlutterBuildVerified: true };
  writeFileSync(path.join(directory, 'windows-toolchain-evidence.json'), `${JSON.stringify(evidence, null, 2)}\n`);
  return evidence;
}
export function prepareWindowsToolchain(baseEnvironment = process.env) {
  requireValue(process.platform === 'win32' && process.arch === 'x64', 'Windows x64 host required.');
  const lock = JSON.parse(readFileSync(path.join(root, 'toolchain.lock.json'), 'utf8'));
  requireValue(process.versions.node === lock.node, 'Node differs from the root lock.');
  const policy = validateWindowsLock(lock.flutterWindows);
  const getEnv = name => Object.entries(baseEnvironment).find(([key]) => key.toLowerCase() === name.toLowerCase())?.[1];
  const vswhere = path.join(getEnv('ProgramFiles(x86)'), 'Microsoft Visual Studio/Installer/vswhere.exe');
  let selected = null;
  // Match the locked Flutter stable VS, then stable Build Tools preference.
  // Never silently choose an older installation when its preferred one is unapproved.
  for (const workload of ['Microsoft.VisualStudio.Workload.NativeDesktop', 'Microsoft.VisualStudio.Workload.VCTools']) {
    selected = parseVswhere(invoke(vswhere, ['-format', 'json', '-products', '*', '-utf8', '-latest', '-version', '16',
      '-requires', workload, 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64', 'Microsoft.VisualStudio.Component.VC.CMake.Project'], baseEnvironment));
    if (selected) break;
  }
  requireValue(selected && selected.isComplete === true && selected.isLaunchable === true && !selected.isPrerelease && !selected.isRebootRequired,
    'A complete, launchable stable VS installation without pending reboot is required.');
  const profile = policy.profiles.find(item => item.visualStudio === selected.installationVersion);
  requireValue(profile, `Unapproved Visual Studio candidate: ${selected.installationVersion}.`);
  const visualStudioPath = realpathSync(safeBatchPath(selected.installationPath));
  const msvcToolsVersion = readFileSync(path.join(visualStudioPath, 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt'), 'utf8').trim();
  const vcRuntimeVersion = readFileSync(path.join(visualStudioPath, 'VC/Auxiliary/Build/Microsoft.VCRedistVersion.default.txt'), 'utf8').trim();
  range(msvcToolsVersion, profile.msvcTools, 'MSVC tools');
  requireValue(vcRuntimeVersion === profile.vcRuntime, 'Default VC runtime differs from lock.');
  const cmakePath = path.join(visualStudioPath, 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe');
  const cmakeOutput = invoke(cmakePath, ['--version'], baseEnvironment);
  const cmakeVersion = /^cmake version ([\d.]+(?:-msvc\d+)?)\r?\n/u.exec(cmakeOutput)?.[1];
  requireValue(cmakeVersion, 'Unrecognized VS-bundled CMake version.');
  range(cmakeVersion.replace(/-msvc\d+$/u, ''), profile.cmake, 'VS-bundled CMake');
  const toolsPath = path.join(visualStudioPath, 'VC/Tools/MSVC', msvcToolsVersion);
  const compilerPath = path.join(toolsPath, 'bin/Hostx64/x64/cl.exe');
  const linkerPath = path.join(toolsPath, 'bin/Hostx64/x64/link.exe');
  requireValue(existsSync(compilerPath) && existsSync(linkerPath), 'Locked x64 compiler/linker missing.');
  const commandProcessor = path.join(getEnv('SystemRoot'), 'System32/cmd.exe');
  const batch = safeBatchPath(path.join(visualStudioPath, 'VC/Auxiliary/Build/vcvarsall.bat'));
  const command = `""${batch}" x64 ${profile.windowsSdk} -vcvars_ver=${msvcToolsVersion} >nul && set"`;
  const exported = invoke(commandProcessor, ['/d', '/s', '/c', command], baseEnvironment, { windowsVerbatimArguments: true });
  const variables = new Map(exported.split(/\r?\n/u).map(line => {
    const split = line.indexOf('='); return [line.slice(0, split).toLowerCase(), line.slice(split + 1)];
  }));
  const environment = {};
  for (const name of environmentNames) {
    const value = variables.get(name.toLowerCase()); requireValue(value, `vcvarsall omitted ${name}.`); environment[name] = value;
  }
  requireValue(samePath(environment.VSINSTALLDIR, visualStudioPath) && samePath(environment.VCToolsInstallDir, toolsPath) &&
    environment.VCToolsVersion === msvcToolsVersion && environment.WindowsSDKVersion.replace(/\\$/u, '') === profile.windowsSdk &&
    environment.UCRTVersion === profile.windowsSdk, 'vcvarsall selected different VS/MSVC/Windows SDK/UCRT.');
  const firstTool = file => environment.PATH.split(';').map(dir => path.join(dir, file)).find(existsSync);
  requireValue(samePath(firstTool('cl.exe') ?? '', compilerPath) && samePath(firstTool('link.exe') ?? '', linkerPath), 'vcvarsall PATH selects another compiler/linker.');
  environment.CMAKE_GENERATOR_INSTANCE = visualStudioPath;
  environment.CMAKE_GENERATOR = profile.generator;
  // Flutter passes -G explicitly and does not pass -T. Its selected default
  // tools were checked above; bind the actual compiler path/version again after
  // generation instead of claiming an environment -T override was applied.
  environment.CMAKE_GENERATOR_TOOLSET = '';
  environment.CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER = linkerPath;
  const env = mergeWindowsEnvironment(baseEnvironment, environment);
  const temporary = path.join(root, 'temp/flutter-windows-toolchain'); mkdirSync(temporary, { recursive: true });
  const probe = mkdtempSync(path.join(temporary, 'identity-'));
  writeFileSync(path.join(probe, 'CMakeLists.txt'), `cmake_minimum_required(VERSION 3.25)\nproject(mystia_toolchain_identity LANGUAGES CXX)\nfile(WRITE "\${CMAKE_BINARY_DIR}/identity.txt" "compilerVersion=\${CMAKE_CXX_COMPILER_VERSION}\\ncompilerPath=\${CMAKE_CXX_COMPILER}\\nwindowsSdkVersion=\${CMAKE_VS_WINDOWS_TARGET_PLATFORM_VERSION}\\nmsvcToolsVersion=\${CMAKE_VS_PLATFORM_TOOLSET_VERSION}\\n")\n`);
  let output;
  try { output = invoke(cmakePath, ['-S', probe, '-B', path.join(probe, 'build'), '-G', profile.generator, '-A', 'x64'], env); }
  catch (error) { throw new Error(`Windows toolchain preflight configure failed: ${error.message}`); }
  writeFileSync(path.join(probe, 'configure.txt'), output);
  const actual = Object.fromEntries(readFileSync(path.join(probe, 'build/identity.txt'), 'utf8').trim().split(/\r?\n/u).map(line => {
    const split = line.indexOf('='); return [line.slice(0, split), line.slice(split + 1)];
  }));
  // CMake leaves CMAKE_VS_PLATFORM_TOOLSET_VERSION empty when the requested
  // version is also the default. The actual compiler path still binds its tools directory.
  requireValue(samePath(actual.compilerPath, compilerPath) && (!actual.msvcToolsVersion || actual.msvcToolsVersion === msvcToolsVersion),
    'CMake selected another compiler/toolset.');
  const identity = { visualStudioVersion: selected.installationVersion, visualStudioPath, generator: profile.generator, platform: 'x64',
    msvcToolsVersion, compilerVersion: actual.compilerVersion, compilerPath: actual.compilerPath,
    cmakeVersion, cmakePath, windowsSdkVersion: actual.windowsSdkVersion, vcRuntimeVersion, linkerPath,
    windowsSdkPath: environment.WindowsSdkDir };
  validateWindowsIdentity(identity, policy);
  validateCmakeBuild(path.join(probe, 'build'), { identity, environment });
  writeFileSync(path.join(probe, 'verified-identity.json'), `${JSON.stringify({ profile: profile.id, identity }, null, 2)}\n`);
  return { profile: profile.id, identity, environment };
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    requireValue(process.argv.length === 3 && process.argv[2] === '--prepare', 'Usage: node scripts/flutter-windows-toolchain.mjs --prepare');
    // Only the explicit build-path environment allowlist is returned, never the
    // inherited environment (which can contain CI credentials).
    console.log(JSON.stringify(prepareWindowsToolchain()));
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
