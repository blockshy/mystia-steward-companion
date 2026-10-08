import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import { mergeWindowsEnvironment, parseVswhere, validateWindowsIdentity, validateWindowsLock, verifyProjectToolchain } from '../../scripts/flutter-windows-toolchain.mjs';

const policy = JSON.parse(readFileSync(new URL('../../toolchain.lock.json', import.meta.url), 'utf8')).flutterWindows;
const identity = { visualStudioVersion: '17.14.37710.0', visualStudioPath: 'C:/VS', generator: 'Visual Studio 17 2022', platform: 'x64',
  msvcToolsVersion: '14.44.35207', compilerVersion: '19.44.35229.0', compilerPath: 'C:/VS/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/cl.exe',
  cmakeVersion: '3.31.6-msvc6', cmakePath: 'C:/VS/CMake/cmake.exe', windowsSdkVersion: '10.0.26100.0', vcRuntimeVersion: '14.44.35112',
  linkerPath: 'C:/VS/VC/Tools/MSVC/14.44.35207/bin/Hostx64/x64/link.exe' };

test('Windows root profiles accept their explicit identities, not an unrestricted latest', () => {
  assert.equal(validateWindowsIdentity(identity, policy).id, 'vs2022-ci');
  assert.equal(validateWindowsIdentity({ ...identity, visualStudioVersion: '18.3.11520.95', generator: 'Visual Studio 18 2026',
    msvcToolsVersion: '14.50.35717', compilerVersion: '19.50.35725.0', cmakeVersion: '4.1.2-msvc8', vcRuntimeVersion: '14.50.35710' }, policy).id, 'vs2026-local');
  for (const changes of [{ visualStudioVersion: '18.4.0.0' }, { generator: 'Visual Studio 18 2026' }, { platform: 'ARM64' },
    { msvcToolsVersion: '14.45.0' }, { compilerVersion: '19.43.99999' }, { cmakeVersion: '3.32.0' },
    { windowsSdkVersion: '10.0.26200.0' }, { vcRuntimeVersion: '14.51.0' }, { cmakeVersion: '3.31.6-unknown' }]) {
    assert.throws(() => validateWindowsIdentity({ ...identity, ...changes }, policy));
  }
});

test('Windows policy rejects missing fields, duplicate instances, malformed and unbounded ranges', () => {
  for (const mutate of [p => { delete p.profiles[0].compiler; }, p => { p.profiles.push(p.profiles[0]); },
    p => { p.profiles[0].cmake.maxExclusive = '5.0.0'; }, p => { p.profiles[0].compiler.minInclusive = 'latest'; },
    p => { p.profiles[0].msvcTools.maxExclusive = p.profiles[0].msvcTools.minInclusive; },
    p => { p.profiles[0].unreviewed = true; }]) {
    const value = structuredClone(policy); mutate(value); assert.throws(() => validateWindowsLock(value));
  }
});

test('localized VS description workaround does not accept ambiguous instances or other broken JSON', () => {
  assert.equal(parseVswhere('[{"installationVersion":"17.14.37710.0"}]').installationVersion, '17.14.37710.0');
  assert.equal(parseVswhere('[{\n"description": "Unescaped "text"",\n"installationVersion":"17.14.37710.0"\n}]').installationVersion, '17.14.37710.0');
  assert.equal(parseVswhere('[]'), null);
  assert.throws(() => parseVswhere('[{},{}]'));
  assert.throws(() => parseVswhere('[{"installationVersion":"broken"value"}]'));
});

test('Windows environment replaces case aliases without dropping unrelated state or leaking it into additions', () => {
  const additions = { PATH: 'C:/VS/bin', LIB: 'C:/SDK/lib' };
  assert.deepEqual(mergeWindowsEnvironment({ Path: 'old', Lib: 'old-lib', CI_SECRET: 'untouched' }, additions),
    { CI_SECRET: 'untouched', PATH: 'C:/VS/bin', LIB: 'C:/SDK/lib' });
  assert.deepEqual(additions, { PATH: 'C:/VS/bin', LIB: 'C:/SDK/lib' });
});

function fixture() {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'mystia-windows-tools-test-'));
  const build = path.join(directory, 'build/windows/x64'); mkdirSync(path.join(build, 'CMakeFiles/3.31.6-msvc6'), { recursive: true });
  const cache = path.join(build, 'CMakeCache.txt'), compiler = path.join(build, 'CMakeFiles/3.31.6-msvc6/CMakeCXXCompiler.cmake'), project = path.join(build, 'ALL_BUILD.vcxproj');
  writeFileSync(cache, 'CMAKE_COMMAND:INTERNAL=C:/VS/CMake/cmake.exe\nCMAKE_GENERATOR:INTERNAL=Visual Studio 17 2022\nCMAKE_GENERATOR_INSTANCE:INTERNAL=C:/VS\nCMAKE_GENERATOR_PLATFORM:INTERNAL=x64\nCMAKE_GENERATOR_TOOLSET:INTERNAL=\n');
  writeFileSync(compiler, `set(CMAKE_CXX_COMPILER "${identity.compilerPath}")\nset(CMAKE_CXX_COMPILER_ID "MSVC")\nset(CMAKE_CXX_COMPILER_VERSION "19.44.35229.0")\nset(CMAKE_CXX_COMPILER_LINKER "${identity.linkerPath}")\n`);
  writeFileSync(project, '<Project><WindowsTargetPlatformVersion>10.0.26100.0</WindowsTargetPlatformVersion></Project>');
  return { directory, build, cache, compiler, project, prepared: { profile: 'vs2022-ci', identity, environment: { CMAKE_GENERATOR_TOOLSET: '' } } };
}
test('actual Flutter generated build is independently bound to preflight', () => {
  const f = fixture();
  try {
    const result = verifyProjectToolchain(f.directory, f.prepared);
    assert.equal(result.actualFlutterBuildVerified, true);
    assert.deepEqual(JSON.parse(readFileSync(path.join(f.build, 'windows-toolchain-evidence.json'), 'utf8')), result);
  } finally { rmSync(f.directory, { recursive: true, force: true }); }
});
for (const [name, modify] of [
  ['another cmake', f => writeFileSync(f.cache, readFileSync(f.cache, 'utf8').replace('C:/VS/CMake/cmake.exe', 'C:/other/cmake.exe'))],
  ['another instance', f => writeFileSync(f.cache, readFileSync(f.cache, 'utf8').replace('INSTANCE:INTERNAL=C:/VS', 'INSTANCE:INTERNAL=C:/other'))],
  ['another target', f => writeFileSync(f.cache, readFileSync(f.cache, 'utf8').replace('PLATFORM:INTERNAL=x64', 'PLATFORM:INTERNAL=ARM64'))],
  ['another toolset', f => writeFileSync(f.cache, readFileSync(f.cache, 'utf8').replace('CMAKE_GENERATOR_TOOLSET:INTERNAL=', 'CMAKE_GENERATOR_TOOLSET:INTERNAL=version=14.43.10000'))],
  ['duplicate cache field', f => writeFileSync(f.cache, `${readFileSync(f.cache, 'utf8')}CMAKE_COMMAND:INTERNAL=C:/other/cmake.exe\n`)],
  ['another compiler', f => writeFileSync(f.compiler, readFileSync(f.compiler, 'utf8').replace(identity.compilerPath, 'C:/other/cl.exe'))],
  ['compiler drift', f => writeFileSync(f.compiler, readFileSync(f.compiler, 'utf8').replace('19.44.35229.0', '19.44.35230.0'))],
  ['another linker', f => writeFileSync(f.compiler, readFileSync(f.compiler, 'utf8').replace(identity.linkerPath, 'C:/other/link.exe'))],
  ['another SDK', f => writeFileSync(f.project, readFileSync(f.project, 'utf8').replace('26100', '22621'))],
  ['ambiguous SDK', f => writeFileSync(f.project, `${readFileSync(f.project, 'utf8')}<WindowsTargetPlatformVersion>10.0.26100.0</WindowsTargetPlatformVersion>`)],
  ['stale compiler configure', f => { const other = path.join(f.build, 'CMakeFiles/3.30.0'); mkdirSync(other); writeFileSync(path.join(other, 'CMakeCXXCompiler.cmake'), ''); }],
]) test(`reject actual Flutter ${name} before publishing build evidence`, () => {
  const f = fixture();
  try { modify(f); assert.throws(() => verifyProjectToolchain(f.directory, f.prepared)); assert.equal(existsSync(path.join(f.build, 'windows-toolchain-evidence.json')), false); }
  finally { rmSync(f.directory, { recursive: true, force: true }); }
});
