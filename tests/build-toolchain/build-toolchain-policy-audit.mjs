import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const toolchain = JSON.parse(await readFile(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
const packageJson = JSON.parse(await readFile(path.join(repoRoot, 'package.json'), 'utf8'));
const [
  buildRelease,
  ciWorkflow,
  dotnet6Runner,
  analysisGenerator,
  preflightPowerShell,
  preflightBash,
] = await Promise.all([
  readFile(path.join(repoRoot, 'mods/bepinex/tools/build-release.ps1'), 'utf8'),
  readFile(path.join(repoRoot, '.github/workflows/ci.yml'), 'utf8'),
  readFile(path.join(repoRoot, 'scripts/run-dotnet6-harmony-smoke.mjs'), 'utf8'),
  readFile(path.join(repoRoot, 'mods/bepinex/tools/il2cpp-analysis/generate-analysis.sh'), 'utf8'),
  readFile(path.join(repoRoot, 'mods/bepinex/tools/preflight.ps1'), 'utf8'),
  readFile(path.join(repoRoot, 'mods/bepinex/tools/preflight.sh'), 'utf8'),
]);

const policyCheck = spawnSync(
  process.execPath,
  [path.join(repoRoot, 'scripts/check-build-toolchain.mjs'), '--policy-only'],
  { cwd: repoRoot, encoding: 'utf8' },
);
assert.equal(
  policyCheck.status,
  0,
  `Toolchain policy projections drifted:\n${policyCheck.stderr || policyCheck.stdout}`,
);

const globalPackageManagerCheck = spawnSync(
  process.execPath,
  [
    path.join(repoRoot, 'scripts/check-build-toolchain.mjs'),
    'frontend',
    '--require-corepack-invocation',
  ],
  {
    cwd: repoRoot,
    encoding: 'utf8',
    env: { ...process.env, COREPACK_ROOT: '' },
  },
);
assert.notEqual(globalPackageManagerCheck.status, 0, 'A global package-manager invocation was accepted.');
assert.match(globalPackageManagerCheck.stderr, /must be invoked through Corepack/u);

assert.equal(packageJson.scripts['toolchain:check'], 'node scripts/check-build-toolchain.mjs full');
assert.equal(packageJson.scripts['test:dotnet6-harmony'], 'node scripts/run-dotnet6-harmony-smoke.mjs all');
assert.match(
  packageJson.scripts.build,
  /^node scripts\/check-build-toolchain\.mjs frontend --require-corepack-invocation && /u,
);
assert.match(
  packageJson.scripts.lint,
  /^node scripts\/check-build-toolchain\.mjs frontend --require-corepack-invocation && /u,
);

for (const scriptName of [
  'tauri:dev',
  'tauri:build',
  'tauri:android:dev',
  'tauri:android:build',
  'tauri:android:apk',
]) {
  assert.match(
    packageJson.scripts[scriptName],
    /^node scripts\/check-build-toolchain\.mjs tauri --require-corepack-invocation && /u,
  );
}

assert.match(buildRelease, /Join-Path \$RepoRoot "scripts\/check-build-toolchain\.mjs"/u);
assert.match(buildRelease, /-Title "Validate locked build toolchain"/u);
assert.match(buildRelease, /-Arguments @\(\$ToolchainCheckScript, "full"\)/u);
assert.doesNotMatch(buildRelease, /Get-Command "pnpm"/u);
assert.match(buildRelease, /Get-Command "corepack"/u);

assert.match(ciWorkflow, /node-version-file: \.nvmrc/u);
assert.match(
  ciWorkflow,
  new RegExp(`npm install --global corepack@${escapeRegex(toolchain.corepack)}`, 'u'),
);
assert.match(ciWorkflow, /node scripts\/check-build-toolchain\.mjs frontend/u);

for (const smokeName of [
  'automation-cooking-job',
  'ui-pinning-runtime',
  'runtime-target-recipe-variant',
]) {
  assert.match(dotnet6Runner, new RegExp(`'${escapeRegex(smokeName)}'`, 'u'));
}
assert.match(dotnet6Runner, /target=\/workspace\/global\.json,readonly/u);
assert.doesNotMatch(dotnet6Runner, /mcr\.microsoft\.com\/dotnet\/sdk@sha256:/u);

assert.match(analysisGenerator, /build_toolchain_lock="\$repo_root\/toolchain\.lock\.json"/u);
assert.match(analysisGenerator, /dotnet_version=\$\(cd "\$repo_root" && dotnet --version\)/u);
assert.match(analysisGenerator, /"\$dotnet_version" != "\$expected_dotnet_version"/u);

assert.match(preflightPowerShell, /toolchain\.lock\.json/u);
assert.match(preflightPowerShell, /\$ActualDotnetSdk -ne \$ExpectedDotnetSdk/u);
assert.match(preflightBash, /toolchain\.lock\.json/u);
assert.match(preflightBash, /"\$ACTUAL_DOTNET_SDK" == "\$EXPECTED_DOTNET_SDK"/u);

console.log('Build toolchain policy audit passed.');

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}
