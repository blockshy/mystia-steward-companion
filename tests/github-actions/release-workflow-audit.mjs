import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const testRoot = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(testRoot, '..', '..');
const workflowsRoot = join(repoRoot, '.github', 'workflows');
const releasePath = join(workflowsRoot, 'release.yml');
const ciPath = join(workflowsRoot, 'ci.yml');
const referencesLockPath = join(repoRoot, 'mods', 'bepinex', 'References', 'references.lock.json');
const toolchainLockPath = join(repoRoot, 'toolchain.lock.json');
const release = readFileSync(releasePath, 'utf8');
const ci = readFileSync(ciPath, 'utf8');
const referencesLock = JSON.parse(readFileSync(referencesLockPath, 'utf8'));
const toolchain = JSON.parse(readFileSync(toolchainLockPath, 'utf8'));

const fail = (message) => {
  throw new Error(`GitHub Actions release policy audit failed: ${message}`);
};

const requireMatch = (content, pattern, message) => {
  if (!pattern.test(content)) fail(message);
};

const requireCount = (content, pattern, expected, message) => {
  const count = [...content.matchAll(pattern)].length;
  if (count !== expected) fail(`${message} Expected ${expected}, actual ${count}.`);
};

const requireExactJobPermissions = (content, expected, label) => {
  const match = /^    permissions:\n((?:      [A-Za-z0-9_-]+: (?:read|write|none)\n?)*)/mu.exec(content);
  if (!match) fail(`${label} is missing an explicit permissions block.`);
  const actual = match[1]
    .trim()
    .split('\n')
    .map((line) => line.trim())
    .filter(Boolean)
    .sort();
  const normalizedExpected = [...expected].sort();
  if (JSON.stringify(actual) !== JSON.stringify(normalizedExpected)) {
    fail(`${label} permissions must be exactly ${normalizedExpected.join(', ')}; actual ${actual.join(', ')}.`);
  }
};

const extractJob = (content, jobName) => {
  const lines = content.split('\n');
  const start = lines.findIndex((line) => line === `  ${jobName}:`);
  if (start < 0) fail(`Missing job: ${jobName}.`);
  let end = lines.length;
  for (let index = start + 1; index < lines.length; index += 1) {
    if (/^  [A-Za-z0-9_-]+:\s*$/u.test(lines[index])) {
      end = index;
      break;
    }
  }
  return lines.slice(start, end).join('\n');
};

const extractLiteralRunBlocks = (content) => {
  const lines = content.split('\n');
  const blocks = [];
  for (let index = 0; index < lines.length; index += 1) {
    const match = lines[index].match(/^(\s*)run:\s*\|\s*$/u);
    if (!match) continue;
    const indentation = match[1].length;
    const body = [];
    for (index += 1; index < lines.length; index += 1) {
      const line = lines[index];
      if (line.trim() && line.match(/^\s*/u)[0].length <= indentation) {
        index -= 1;
        break;
      }
      body.push(line);
    }
    blocks.push(body.join('\n'));
  }
  return blocks;
};

const workflowFiles = readdirSync(workflowsRoot, { withFileTypes: true })
  .filter((entry) => entry.isFile() && /\.ya?ml$/u.test(entry.name))
  .map((entry) => join(workflowsRoot, entry.name));

for (const workflowPath of workflowFiles) {
  const workflow = readFileSync(workflowPath, 'utf8');
  for (const match of workflow.matchAll(/^\s*uses:\s*([^\s#]+)(?:\s+#.*)?$/gmu)) {
    const reference = match[1];
    if (reference.startsWith('./') || reference.startsWith('docker://')) continue;
    if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+(?:\/[A-Za-z0-9_.\/-]+)?@[0-9a-f]{40}$/u.test(reference)) {
      fail(`${workflowPath} contains an action that is not pinned to a full commit SHA: ${reference}`);
    }
  }
}

requireMatch(ci, /^permissions:\s*\{\}\s*$/mu, 'CI must deny all token permissions by default.');
requireExactJobPermissions(extractJob(ci, 'companion-check'), ['contents: read'], 'CI job');
requireMatch(
  ci,
  /actions\/checkout@d23441a48e516b6c34aea4fa41551a30e30af803 # v6/u,
  'CI checkout action pin drifted.',
);
requireMatch(
  ci,
  /actions\/setup-node@820762786026740c76f36085b0efc47a31fe5020 # v7/u,
  'CI setup-node action pin drifted.',
);
requireMatch(ci, /persist-credentials: false/u, 'CI checkout must not persist credentials.');
requireMatch(ci, /corepack pnpm audit:github-actions/u, 'CI must run the workflow policy audit.');

requireMatch(
  release,
  /^on:\n  workflow_dispatch:\n    inputs:/mu,
  'Official release must be triggered only through workflow_dispatch.',
);
for (const forbiddenTrigger of ['push:', 'pull_request:', 'pull_request_target:', 'release:', 'schedule:', 'workflow_run:']) {
  if (release.includes(`  ${forbiddenTrigger}`)) fail(`Forbidden automatic release trigger: ${forbiddenTrigger}`);
}
requireMatch(release, /^permissions:\s*\{\}\s*$/mu, 'Release workflow must deny permissions by default.');
requireMatch(
  release,
  /^concurrency:\n  group: official-release\n  cancel-in-progress: false\n  queue: max$/mu,
  'Release concurrency must be a static, non-cancelling serialized group.',
);
requireMatch(release, /\$GITHUB_REF" != "refs\/heads\/main"/u, 'Dispatches outside main must fail.');
requireMatch(
  release,
  /refs\/remotes\/origin\/main\)" != "\$GITHUB_SHA"/u,
  'The dispatched SHA must equal the current origin/main head.',
);
requireMatch(
  release,
  /\^v\(0\|\[1-9\]\[0-9\]\*\)\\\.\(0\|\[1-9\]\[0-9\]\*\)\\\.\(0\|\[1-9\]\[0-9\]\*\)\$/u,
  'The workflow must accept canonical stable tags only.',
);

const jobs = {
  validate: extractJob(release, 'validate'),
  build: extractJob(release, 'release-packages'),
  assemble: extractJob(release, 'assemble-attest'),
  publish: extractJob(release, 'publish'),
};

requireExactJobPermissions(jobs.validate, ['actions: read', 'contents: read'], 'Validate job');
requireExactJobPermissions(jobs.build, ['contents: read'], 'Release package build job');
requireExactJobPermissions(
  jobs.assemble,
  ['contents: read', 'id-token: write', 'attestations: write', 'artifact-metadata: write'],
  'Assemble job',
);
requireExactJobPermissions(jobs.publish, ['contents: write'], 'Publish job');
requireCount(release, /^\s+contents: write\s*$/gmu, 1, 'Only publish may receive contents write.');
requireCount(release, /^\s+id-token: write\s*$/gmu, 1, 'Only assemble may mint an OIDC token.');
requireCount(release, /^\s+attestations: write\s*$/gmu, 1, 'Only assemble may write attestations.');
requireCount(
  release,
  /^\s+artifact-metadata: write\s*$/gmu,
  1,
  'Only assemble may write artifact metadata.',
);

requireCount(
  release,
  /^    environment: official-release-build$/gmu,
  1,
  'All secret-using package builds must share one pre-build approval job.',
);
requireCount(release, /^    environment: official-release$/gmu, 1, 'Publish must use the final release environment.');
requireCount(release, /^    runs-on: windows-2022$/gmu, 1, 'The official package build must use Windows Server 2022.');
requireCount(release, /^    runs-on: ubuntu-24\.04$/gmu, 3, 'All non-binary official release jobs must use Ubuntu 24.04.');
if (release.includes('runs-on: ubuntu-latest')) fail('Official release jobs must not float on ubuntu-latest.');
if (jobs.validate.includes('environment:') || jobs.assemble.includes('environment:')) {
  fail('Validate and assemble jobs must not receive environment secrets.');
}

requireMatch(
  jobs.validate,
  /node scripts\/check-build-toolchain\.mjs release/u,
  'Validate must enforce the complete locked release toolchain.',
);
requireMatch(
  jobs.build,
  /shell: pwsh\n\s+run: node scripts\/check-build-toolchain\.mjs release/u,
  'Package building must enforce the complete locked release toolchain from PowerShell.',
);
requireMatch(
  jobs.build,
  /shell: pwsh\n\s+run: node scripts\/check-build-toolchain\.mjs android[\s\S]*shell: pwsh\n\s+run: node scripts\/check-build-toolchain\.mjs release-tools/u,
  'Package building must enforce both Android and release-tools profiles from PowerShell.',
);
requireMatch(
  jobs.assemble,
  /node scripts\/check-build-toolchain\.mjs release-tools/u,
  'Assemble must enforce the locked release tools.',
);
requireMatch(
  jobs.publish,
  /node scripts\/check-build-toolchain\.mjs release-tools/u,
  'Publish must enforce the locked release tools.',
);
requireCount(
  release,
  /node scripts[\\/]install-locked-release-tools\.mjs --install-root/gu,
  4,
  'Every official release job must install the hash-locked PowerShell and GitHub CLI archives.',
);
requireCount(
  release,
  /node scripts\/install-locked-corepack\.mjs --install-root/gu,
  2,
  'The validate and package jobs must install Corepack into an isolated prefix.',
);
if (/corepack enable/u.test(release)) {
  fail('Official release jobs must not overwrite runner package-manager shims with corepack enable.');
}
for (const [label, job] of [
  ['validate', jobs.validate],
  ['package build', jobs.build],
]) {
  const corepackInstallIndex = job.indexOf('install-locked-corepack.mjs --install-root');
  const toolchainCheckIndex = job.indexOf('node scripts/check-build-toolchain.mjs release');
  if (corepackInstallIndex < 0 || toolchainCheckIndex < 0 || corepackInstallIndex >= toolchainCheckIndex) {
    fail(`${label} must install isolated locked Corepack before enforcing its release toolchain.`);
  }
}
for (const [label, job, checkMarker] of [
  ['validate', jobs.validate, 'node scripts/check-build-toolchain.mjs release'],
  ['package build', jobs.build, 'node scripts/check-build-toolchain.mjs release'],
  ['assemble', jobs.assemble, 'node scripts/check-build-toolchain.mjs release-tools'],
  ['publish', jobs.publish, 'node scripts/check-build-toolchain.mjs release-tools'],
]) {
  const installIndex = job.indexOf('install-locked-release-tools.mjs');
  const checkIndex = job.indexOf(checkMarker);
  if (installIndex < 0 || checkIndex < 0 || installIndex >= checkIndex) {
    fail(`${label} must install locked release tools before enforcing its toolchain profile.`);
  }
}
requireMatch(
  jobs.build,
  /Install locked release tools\n\s+shell: cmd/u,
  'The Windows package job must bootstrap exact PowerShell without depending on the drifting preinstalled pwsh.',
);
requireCount(
  jobs.build,
  /Write-Host "  \$TrackedChange"/gu,
  2,
  'Windows and Android drift gates must report tracked filenames before stopping.',
);

requireMatch(
  jobs.publish,
  /gh api "repos\/\$GITHUB_REPOSITORY\/immutable-releases"/u,
  'Publish must fail closed unless repository Immutable Releases are enabled.',
);
requireMatch(
  jobs.publish,
  /policy\.enabled !== true/u,
  'Publish must inspect the enabled state returned by the Immutable Releases API.',
);
requireMatch(
  jobs.publish,
  /permission-administration: read/u,
  'Publish must request an App token with Administration read permission for the immutable policy check.',
);
requireMatch(
  jobs.publish,
  /repositories: mystia-steward-companion/u,
  'The release-policy App token must be scoped to the main repository only.',
);
requireMatch(
  jobs.validate,
  /for environment_name in official-release-build official-release; do/u,
  'Validate must inspect both approval environments.',
);
requireMatch(
  jobs.validate,
  /environments\/\$environment_name"[\s\S]*environments\/\$environment_name\/deployment-branch-policies"/u,
  'Validate must inspect each environment and its deployment branch policy.',
);
requireMatch(jobs.validate, /rule\?\.type === 'required_reviewers'/u, 'Both release environments need required reviewers.');
requireMatch(
  jobs.validate,
  /environment\.can_admins_bypass !== false/u,
  'Both release environments must reject administrator bypass.',
);
requireMatch(
  jobs.validate,
  /reviewerRule\.reviewers\.length === 0/u,
  'Both release environments must have at least one reviewer.',
);
requireMatch(
  jobs.validate,
  /custom_branch_policies !== true/u,
  'Both release environments must use explicit branch allowlists.',
);
requireMatch(
  jobs.validate,
  /branches\.total_count !== 1[\s\S]*branch_policies\[0\]\?\.name !== 'main'[\s\S]*branch_policies\[0\]\?\.type !== 'branch'/u,
  'Both release environments must allow exactly the main branch.',
);
requireMatch(
  jobs.build,
  /Private build-reference cleanup did not complete\./u,
  'The package job must fail closed when private build references cannot be removed.',
);
if (jobs.build.includes('SilentlyContinue')) {
  fail('The package job must not suppress private build-reference cleanup failures.');
}

requireCount(release, /^          ref: \$\{\{ github\.sha \}\}$/gmu, 4, 'Every checkout must use the same dispatch SHA.');
requireCount(release, /^          persist-credentials: false$/gmu, 4, 'Every release checkout must discard credentials.');

const pinnedActions = new Map([
  ['actions/checkout', 'd23441a48e516b6c34aea4fa41551a30e30af803'],
  ['actions/setup-node', '820762786026740c76f36085b0efc47a31fe5020'],
  ['actions/setup-dotnet', '26b0ec14cb23fa6904739307f278c14f94c95bf1'],
  ['actions/setup-java', 'b6effb05e454b25005698d916606bdc6ffcbf961'],
  ['actions/create-github-app-token', 'bcd2ba49218906704ab6c1aa796996da409d3eb1'],
  ['actions/upload-artifact', '043fb46d1a93c77aae656e7c1c64a875d1fc6a0a'],
  ['actions/download-artifact', '3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c'],
  ['actions/attest', '1e69f48acb82d1966a394da916b4c1698aa569d6'],
]);
for (const [action, sha] of pinnedActions) {
  requireMatch(release, new RegExp(`${action}@${sha}`, 'u'), `Missing approved action pin: ${action}@${sha}.`);
}
for (const match of release.matchAll(/^\s*uses:\s*([^\s#]+)(?:\s+#.*)?$/gmu)) {
  const reference = match[1];
  const separator = reference.lastIndexOf('@');
  const action = reference.slice(0, separator);
  const sha = reference.slice(separator + 1);
  const expectedSha = pinnedActions.get(action);
  if (!expectedSha || sha !== expectedSha) {
    fail(`Release workflow uses an action outside the reviewed SHA allowlist: ${reference}`);
  }
}

requireMatch(
  jobs.build,
  /BUILD_ASSETS_APP_ID[\s\S]*BUILD_ASSETS_APP_PRIVATE_KEY/u,
  'Private build assets must use the environment-scoped GitHub App credentials.',
);
requireCount(
  release,
  /^          client-id: \$\{\{ secrets\.BUILD_ASSETS_APP_ID \}\}$/gmu,
  2,
  'Both GitHub App token steps must use the non-deprecated client-id input.',
);
if (/^\s+app-id:/mu.test(release)) {
  fail('The deprecated create-github-app-token app-id input must not be restored.');
}
for (const secretName of ['BUILD_ASSETS_APP_ID', 'BUILD_ASSETS_APP_PRIVATE_KEY']) {
  requireCount(
    release,
    new RegExp(`secrets\\.${secretName}`, 'gu'),
    2,
    `${secretName} must be scoped only to the build-input and release-policy token steps.`,
  );
  requireCount(jobs.build, new RegExp(`secrets\\.${secretName}`, 'gu'), 1, `${secretName} must appear once in the package build job.`);
  requireCount(jobs.publish, new RegExp(`secrets\\.${secretName}`, 'gu'), 1, `${secretName} must appear once in the publish job.`);
}
requireMatch(
  jobs.build,
  /owner: blockshy\n\s+repositories: mystia-steward-build-assets\n\s+permission-contents: read/u,
  'The build-assets App token must be explicitly limited to read-only repository contents.',
);
const approvedBuildAssetsRepository = 'blockshy/mystia-steward-build-assets';
if (referencesLock?.bundle?.repository !== approvedBuildAssetsRepository) {
  fail('references.lock.json must bind the reference bundle to the approved private repository.');
}
if (!/^[A-Za-z0-9][A-Za-z0-9_.-]*$/u.test(referencesLock?.bundle?.tag ?? '')) {
  fail('references.lock.json must contain a safe flat release tag.');
}
if (!/^[A-Za-z0-9][A-Za-z0-9_.-]*\.zip$/u.test(referencesLock?.bundle?.asset ?? '')) {
  fail('references.lock.json must contain a safe flat ZIP asset name.');
}
requireMatch(
  release,
  /BUILD_ASSETS_REPOSITORY: blockshy\/mystia-steward-build-assets/u,
  'The workflow must hard-code the reviewed GitHub App repository scope.',
);
requireMatch(
  jobs.build,
  /References\/references\.lock\.json[\s\S]*bundle\.repository[\s\S]*BUILD_ASSETS_REPOSITORY[\s\S]*bundle\.tag[\s\S]*bundle\.asset/u,
  'Windows restore must derive tag and asset only from the reviewed reference lock.',
);
requireMatch(
  jobs.build,
  /releases\/tags\/\$EncodedReferenceTag[\s\S]*ReferenceRelease\.immutable -ne \$true[\s\S]*ReferenceAssets\.Count -ne 1[\s\S]*ReferenceAssets\[0\]\.size[\s\S]*ReferenceAssets\[0\]\.digest/u,
  'Windows restore must prove the private Release is immutable and matches the locked asset identity before download.',
);
requireMatch(
  jobs.build,
  /ConvertFrom-Json -DateKind String -NoEnumerate[\s\S]*ReferenceRelease\.assets -isnot \[System\.Array\][\s\S]*ReferenceRelease\.draft -isnot \[bool\]/u,
  'Private reference metadata must preserve JSON types and reject non-boolean flags or non-array assets.',
);
if (release.includes(referencesLock.bundle.tag) || release.includes(referencesLock.bundle.asset)) {
  fail('The workflow must not duplicate tag or asset coordinates from references.lock.json.');
}
requireMatch(
  jobs.build,
  /scripts\/restore-build-references\.mjs[\s\S]*--archive[\s\S]*--output mods\/bepinex\/References/u,
  'Windows build must restore references through the strict helper.',
);
requireMatch(jobs.build, /mods\/bepinex\/tools\/build-release\.ps1/u, 'Windows build must use the canonical build script.');
if (jobs.build.includes('-ReferenceDir')) {
  fail('The hosted build must use build-release.ps1 absolute default References path.');
}
if (jobs.build.includes('-BuildAndroidApk')) fail('The canonical Windows build step must not rebuild Android internally.');
requireMatch(
  jobs.build,
  /build-release\.ps1[\s\S]*node scripts\/manage-build-artifacts\.mjs clean[\s\S]*corepack pnpm tauri:android:apk:signed/u,
  'The single approved package job must release desktop build caches before the Android build.',
);
requireMatch(
  jobs.build,
  /id: windows-digests[\s\S]*Get-FileHash -Algorithm SHA256[\s\S]*mod_package_sha256[\s\S]*companion_sha256/u,
  'Windows build must export exact per-file digests before artifact upload.',
);
requireMatch(
  jobs.build,
  /git status --porcelain=v1 --untracked-files=no[\s\S]*Windows release build changed tracked source or lock files/u,
  'Windows build must reject tracked source or lock drift.',
);

for (const secretName of [
  'MYSTIA_ANDROID_KEYSTORE_BASE64',
  'MYSTIA_ANDROID_KEYSTORE_SHA256',
  'MYSTIA_ANDROID_KEY_ALIAS',
  'MYSTIA_ANDROID_STORE_PASSWORD',
  'MYSTIA_ANDROID_KEY_PASSWORD',
]) {
  requireMatch(jobs.build, new RegExp(`secrets\\.${secretName}`, 'u'), `Missing Android signing secret: ${secretName}.`);
  requireCount(release, new RegExp(`secrets\\.${secretName}`, 'gu'), 1, `${secretName} must be scoped to one step.`);
}
requireMatch(
  jobs.build,
  /scripts\/materialize-android-signing\.mjs[\s\S]*--keystore-output[\s\S]*--properties-output/u,
  'Android signing material must use the strict materializer.',
);
requireMatch(
  jobs.build,
  /if: \$\{\{ always\(\) \}\}[\s\S]*materialize-android-signing\.mjs[\s\S]*--cleanup/u,
  'Signing files need strict unconditional cleanup.',
);
requireMatch(jobs.build, /corepack pnpm tauri:android:apk:signed/u, 'The package job must build signed APKs.');
requireMatch(jobs.build, /java-version: "21\.0\.4"/u, 'The package job must use the locked Temurin 21.0.4 JDK.');
requireMatch(jobs.build, /"build-tools;35\.0\.0"/u, 'The package job must install the locked Android build tools.');
requireMatch(
  jobs.build,
  new RegExp(`"ndk;${toolchain.android.ndkPackage.replaceAll('.', '\\.')}"`, 'u'),
  'The package job must install the exact locked Android NDK package coordinate.',
);
requireMatch(
  jobs.build,
  new RegExp(`"ndk/${toolchain.android.ndkPackage.replaceAll('.', '\\.')}"`, 'u'),
  'The package job must derive NDK_HOME from the exact locked Android NDK package coordinate.',
);
requireMatch(
  jobs.build,
  /"ANDROID_SDK_ROOT=\$env:ANDROID_HOME"/u,
  'Android job must normalize ANDROID_SDK_ROOT to the locked SDK root.',
);
for (const ndkAlias of ['NDK_HOME', 'ANDROID_NDK', 'ANDROID_NDK_HOME', 'ANDROID_NDK_ROOT']) {
  requireMatch(jobs.build, new RegExp(`"${ndkAlias}=\\$NdkHome"`, 'u'), `The package job must lock ${ndkAlias}.`);
}
if (jobs.build.includes('Get-ChildItem -LiteralPath (Join-Path $env:ANDROID_HOME "cmdline-tools")')) {
  fail('Android sdkmanager discovery must not fall back to an arbitrary command-line tools version.');
}
requireMatch(
  jobs.build,
  /node scripts\/check-build-toolchain\.mjs android/u,
  'Android job must validate the Android toolchain profile.',
);
requireMatch(
  jobs.build,
  /id: android-digests[\s\S]*Get-FileHash -Algorithm SHA256[\s\S]*arm64_sha256[\s\S]*armv7_sha256/u,
  'Android build must export exact per-file digests before artifact upload.',
);
requireMatch(
  jobs.build,
  /git status --porcelain=v1 --untracked-files=no[\s\S]*Android release build changed tracked source or lock files/u,
  'Android build must reject tracked source or lock drift.',
);

requireMatch(
  jobs.assemble,
  /prepare-release-assets\.ps1[\s\S]*-NotesFile[\s\S]*-TargetCommitSha \$env:GITHUB_SHA[\s\S]*-RequireAndroid/u,
  'Assemble must prepare all assets for the exact commit and notes file.',
);
requireMatch(
  jobs.assemble,
  /EXPECTED_MOD_PACKAGE_SHA256: \$\{\{ needs\.release-packages\.outputs\.mod_package_sha256 \}\}[\s\S]*EXPECTED_ARM64_SHA256: \$\{\{ needs\.release-packages\.outputs\.arm64_sha256 \}\}[\s\S]*Cross-job artifact digest mismatch/u,
  'Assemble must compare every downloaded file with its producing job output digest.',
);
requireMatch(
  jobs.publish,
  /publish-release\.ps1[\s\S]*-NotesFile[\s\S]*-TargetCommitSha \$env:GITHUB_SHA[\s\S]*-OfficialRelease[\s\S]*-SkipBuild/u,
  'Publish must use create-only official mode with the exact commit and notes file.',
);
for (const forbiddenFlag of ['-Clobber', '--clobber', '-Prerelease', '-SkipVersionCheck']) {
  if (release.includes(forbiddenFlag)) fail(`Official workflow must not use ${forbiddenFlag}.`);
}

for (const runBlock of extractLiteralRunBlocks(release)) {
  if (runBlock.includes('${{ inputs.')) {
    fail('Untrusted dispatch inputs must enter commands only through environment variables and validated files.');
  }
}
requireMatch(
  release,
  /RELEASE_NOTES_INPUT: \$\{\{ inputs\.notes \}\}/u,
  'Release notes must first cross the expression boundary through env.',
);
requireCount(release, /-NotesFile /gu, 2, 'Prepare and publish must consume the immutable notes file.');
if (release.includes('secrets.GITHUB_TOKEN')) fail('Use the job-scoped github.token, not a user-managed token secret.');

requireMatch(
  jobs.validate,
  /git ls-remote --exit-code --tags origin "refs\/tags\/\$RELEASE_TAG_INPUT"[\s\S]*tag_status=\$\?[\s\S]*tag_status -eq 0[\s\S]*tag_status -ne 2[\s\S]*Unable to prove that the release tag is absent/u,
  'Validate must distinguish an existing tag, a proven-absent tag, and a failed tag query.',
);
requireMatch(
  jobs.validate,
  /gh api --paginate --slurp "repos\/\$GITHUB_REPOSITORY\/releases\?per_page=100"/u,
  'Validate must read the complete remote Release history.',
);
requireMatch(
  jobs.validate,
  /releases\.some\(\(release\) => release\?\.tag_name === tag\)/u,
  'Validate must reject an existing Release without treating API failure as absence.',
);
requireMatch(
  jobs.validate,
  /compareUpdateVersions\(parsed, ownerVersion\) >= 0/u,
  'Validate must reject owner versions that are not newer than every published canonical Release.',
);
for (const auditCommand of [
  'audit:release-policy',
  'audit:release-package',
  'audit:toolchain',
  'audit:build-references',
  'audit:android-apk-transaction',
]) {
  if (!jobs.validate.includes(`corepack pnpm ${auditCommand}`)) {
    fail(`Validate must run ${auditCommand}.`);
  }
}

const releaseAssets = [
  'mystia-steward-companion-bepinex.zip',
  'mystia-steward-companion-companion-windows-x64.exe',
  'mystia-steward-companion-android-arm64-v8a.apk',
  'mystia-steward-companion-android-armeabi-v7a.apk',
  'update-manifest.json',
  'update-catalog.json',
  'SHA256SUMS.txt',
];
for (const asset of releaseAssets) {
  requireMatch(jobs.assemble, new RegExp(asset.replaceAll('.', '\\.'), 'u'), `Assemble allowlist is missing ${asset}.`);
}
if (/gh release view|git fetch --force origin "refs\/tags\//u.test(jobs.publish)) {
  fail('Publish must not rediscover the just-published Release by tag in a later workflow step.');
}
requireCount(jobs.assemble, /^            mods\/bepinex\/dist\/(?:mystia|update|SHA256)/gmu, 14, 'Attestation and upload must each name seven exact assets.');
if (/mods\/bepinex\/dist\/\*/u.test(release)) fail('Release artifacts must not be collected with a dist wildcard.');

requireCount(release, /if-no-files-found: error/gu, 4, 'Every artifact upload must fail on missing files.');
requireCount(release, /include-hidden-files: false/gu, 4, 'Every artifact upload must exclude hidden files.');
requireCount(release, /overwrite: false/gu, 4, 'Workflow artifacts must be immutable.');
requireCount(
  release,
  /retention-days: 40/gu,
  4,
  'Release intermediates must survive both approvals and the 35-day workflow lifetime for 40 days.',
);
const retentionValues = [...release.matchAll(/^\s+retention-days: ([0-9]+)$/gmu)]
  .map((match) => Number.parseInt(match[1], 10));
if (retentionValues.length !== 4 || retentionValues.some((value) => value !== 40)) {
  fail('Release artifacts must use only the reviewed 40-day retention period.');
}
requireMatch(
  release,
  /release-assets-\$\{\{ github\.run_id \}\}-\$\{\{ github\.run_attempt \}\}/u,
  'Assembled artifact identity must include run ID and attempt.',
);

console.log('GitHub Actions release workflow audit passed.');
