import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('../..', import.meta.url));
const sources = Object.fromEntries(await Promise.all([
  ['build', 'mods/bepinex/tools/build-release.ps1'],
  ['packagePowerShell', 'mods/bepinex/tools/package-release.ps1'],
  ['packageBash', 'mods/bepinex/tools/package-release.sh'],
  ['releaseCommon', 'mods/bepinex/tools/release-common.ps1'],
  ['prepare', 'mods/bepinex/tools/prepare-release-assets.ps1'],
  ['publish', 'mods/bepinex/tools/publish-release.ps1'],
  ['releaseRuntime', 'tests/build-artifacts/release-powershell-runtime-audit.ps1'],
  ['android', 'scripts/build-android-signed-apk.mjs'],
  ['tauriApp', 'apps/companion/src-tauri/src/app.rs'],
  ['packageJson', 'package.json'],
].map(async ([name, relativePath]) => [name, await readFile(path.join(repoRoot, relativePath), 'utf8')])));

const packageScripts = JSON.parse(sources.packageJson).scripts;
const extractPowerShellFunction = (source, name) => {
  const startMarker = `function ${name} {`;
  const start = source.indexOf(startMarker);
  assert.notEqual(start, -1, `Missing PowerShell function: ${name}.`);
  const nextFunction = source.indexOf('\nfunction ', start + startMarker.length);
  const nextMain = source.indexOf('\nPush-Location ', start + startMarker.length);
  const ends = [nextFunction, nextMain].filter((value) => value >= 0);
  const end = ends.length === 0 ? source.length : Math.min(...ends);
  return source.slice(start, end);
};
for (const scriptName of [
  'tauri:dev',
  'tauri:build',
]) {
  assert.match(
    packageScripts[scriptName],
    /^node scripts\/check-build-toolchain\.mjs tauri --require-corepack-invocation && node scripts\/manage-build-artifacts\.mjs prune && /u,
    `${scriptName} does not enforce the toolchain and cache policies before building.`,
  );
}
for (const scriptName of ['tauri:android:dev', 'tauri:android:build', 'tauri:android:apk']) {
  assert.match(
    packageScripts[scriptName],
    /^node scripts\/check-build-toolchain\.mjs android --require-corepack-invocation && node scripts\/manage-build-artifacts\.mjs prune && /u,
    `${scriptName} does not enforce the Android toolchain and cache policies before building.`,
  );
}

assert.match(sources.publish, /\[Parameter\(Mandatory = \$true\)\]\[string\]\$TargetCommitSha/u);
for (const scriptName of ['build', 'prepare', 'publish']) {
  assert.match(
    sources[scriptName],
    /\$PSNativeCommandUseErrorActionPreference = \$false/u,
    `${scriptName} must preserve explicit native exit-code handling regardless of caller profiles.`,
  );
}
assert.match(sources.publish, /\[switch\]\$OfficialRelease/u);
assert.match(sources.publish, /if \(\$SkipBuild -and \$BuildAndroidApk\)/u);
assert.match(sources.publish, /Assert-MystiaOfficialTarget/u);
assert.match(sources.publish, /origin\/main moved or does not match the locked release commit/u);
assert.match(sources.publish, /Release already exists and will not be overwritten/u);
assert.match(sources.publish, /Git tag already exists and will not be reused or overwritten/u);
assert.match(sources.publish, /Prepared update manifest hashes or sizes do not match the release payload/u);
assert.doesNotMatch(sources.publish, /\$Clobber|SkipVersionCheck|AndroidApkPath/u);
assert.doesNotMatch(sources.publish, /release",\s*"create"|release",\s*"edit"|delete-asset|--clobber/u);
assert.doesNotMatch(sources.publish, /release",\s*"upload"/u);
assert.doesNotMatch(sources.publish, /release",\s*"delete"|-Method "DELETE"/u);
assert.doesNotMatch(sources.publish, /"--notes",\s*\$Notes/u);
assert.doesNotMatch(sources.build, /SkipPreflight|Assert-BuildReferences|RequiredReferenceFiles/u);
assert.match(sources.build, /\(Resolve-Path -LiteralPath \$ReferenceDir\)\.Path/u);
assert.match(sources.build, /& \$PreflightScript -ReferenceDir \$EffectiveReferenceDir/u);

const repositoryReleases = extractPowerShellFunction(sources.publish, 'Get-MystiaRepositoryReleases');
assert.match(repositoryReleases, /api --paginate --slurp "repos\/\$Repo\/releases\?per_page=100"/u);
assert.match(repositoryReleases, /if \(\$LASTEXITCODE -ne 0\)[\s\S]*Unable to read the complete GitHub Release list/u);
assert.match(repositoryReleases, /ConvertFrom-Json[\s\S]*if \(\$null -eq \$Pages -or/u);
assert.match(repositoryReleases, /-DateKind String/u);
assert.match(repositoryReleases, /\$Pages -isnot \[System\.Array\][\s\S]*\$Pages\.Count -eq 0/u);
assert.match(repositoryReleases, /invalid paginated Release-list shape/u);
assert.match(repositoryReleases, /invalid Release entry/u);
assert.match(repositoryReleases, /Assert-MystiaJsonBoolean -Value \$Release\.draft/u);

const releaseByTag = extractPowerShellFunction(sources.publish, 'Get-MystiaReleaseByTag');
assert.match(releaseByTag, /\[AllowEmptyCollection\(\)\]\[object\[\]\]\$Releases/u);
assert.match(releaseByTag, /\$Matches\.Count -gt 1[\s\S]*duplicate Release entries/u);
assert.match(releaseByTag, /\$Matches\.Count -eq 0[\s\S]*return \$null/u);

const remoteTag = extractPowerShellFunction(sources.publish, 'Get-MystiaRemoteTagRef');
assert.match(remoteTag, /api "repos\/\$Repo\/git\/matching-refs\/tags\/\$EncodedTag"/u);
assert.match(remoteTag, /if \(\$LASTEXITCODE -ne 0\)[\s\S]*Unable to prove whether Git tag/u);
assert.match(remoteTag, /ConvertFrom-Json[\s\S]*if \(\$null -eq \$Refs\)/u);
assert.match(remoteTag, /\$Refs -isnot \[System\.Array\]/u);
assert.match(remoteTag, /\$Ref\.object -is \[System\.Array\]/u);
assert.match(remoteTag, /\[string\]\$_\.ref -ceq \$ExpectedRef/u);
assert.match(remoteTag, /\$Matches\.Count -gt 1[\s\S]*duplicate refs/u);
assert.match(remoteTag, /\$Matches\.Count -eq 0[\s\S]*return \$null/u);
assert.match(remoteTag, /\$ObjectType -cnotin @\("commit", "tag"\)[\s\S]*\^\[0-9a-f\]\{40\}\$/u);

const exactRemoteTag = extractPowerShellFunction(sources.publish, 'Get-MystiaExactRemoteTagRef');
assert.match(exactRemoteTag, /api "repos\/\$Repo\/git\/ref\/tags\/\$EncodedTag"/u);
assert.match(exactRemoteTag, /'\\bHTTP 404\\b'/u);
assert.doesNotMatch(exactRemoteTag, /matching-refs|--paginate/u);
const waitExactRemoteTag = extractPowerShellFunction(sources.publish, 'Wait-MystiaExactRemoteTagRef');
assert.match(waitExactRemoteTag, /\[ValidateRange\(1, 30\)\]\[int\]\$MaxAttempts = 20/u);
assert.match(waitExactRemoteTag, /Get-MystiaExactRemoteTagRef/u);
assert.match(waitExactRemoteTag, /ObjectType -cne "commit"[\s\S]*Sha -cne \$ExpectedSha/u);

const remoteAbsence = extractPowerShellFunction(
  sources.publish,
  'Assert-MystiaRemoteReleaseIdentityAbsent',
);
assert.match(remoteAbsence, /Get-MystiaRepositoryReleases/u);
assert.match(remoteAbsence, /Get-MystiaReleaseByTag/u);
assert.match(remoteAbsence, /Get-MystiaRemoteTagRef/u);

const createTag = extractPowerShellFunction(sources.publish, 'New-MystiaRemoteTag');
assert.match(createTag, /-Method "POST"/u);
assert.match(createTag, /-Endpoint "repos\/\$Repo\/git\/refs"/u);
assert.match(createTag, /ref = "refs\/tags\/\$Tag"; sha = \$ExpectedSha/u);
assert.match(createTag, /\$CreatedTag = Invoke-MystiaGitHubJsonRequest/u);
assert.match(createTag, /ConvertTo-MystiaExactTagRef/u);
assert.match(createTag, /Wait-MystiaExactRemoteTagRef/u);
assert.doesNotMatch(createTag, /Get-MystiaRemoteTagRef|matching-refs/u);

const createDraft = extractPowerShellFunction(sources.publish, 'New-MystiaDraftRelease');
assert.match(createDraft, /-Method "POST"/u);
assert.match(createDraft, /-Endpoint "repos\/\$Repo\/releases"/u);
assert.match(createDraft, /target_commitish = \$ExpectedSha/u);
assert.match(createDraft, /body = \$Notes/u);
assert.match(createDraft, /draft = \$true/u);
assert.match(createDraft, /make_latest = "false"/u);
assert.doesNotMatch(createDraft, /assets?|upload/u);

const releaseById = extractPowerShellFunction(sources.publish, 'Get-MystiaRemoteReleaseById');
assert.match(releaseById, /api "repos\/\$Repo\/releases\/\$ReleaseId"/u);
assert.match(releaseById, /'\\bHTTP 404\\b'/u);
assert.match(releaseById, /Assert-MystiaRemoteReleaseIdentity/u);
assert.doesNotMatch(releaseById, /--paginate|Get-MystiaRepositoryReleases|Get-MystiaReleaseByTag/u);

const waitReleaseState = extractPowerShellFunction(sources.publish, 'Wait-MystiaRemoteReleaseState');
assert.match(waitReleaseState, /ValidateSet\("Created", "Uploaded", "Published"\)/u);
assert.match(waitReleaseState, /\[ValidateRange\(1, 30\)\]\[int\]\$MaxAttempts = 20/u);
assert.match(waitReleaseState, /Get-MystiaRemoteReleaseById/u);
assert.match(waitReleaseState, /valid incomplete uploaded asset state was visible/u);
assert.match(waitReleaseState, /exact pre-publication draft was still visible/u);
assert.match(waitReleaseState, /published Release was not yet reported immutable/u);
assert.doesNotMatch(waitReleaseState, /Get-MystiaRepositoryReleases|Get-MystiaReleaseByTag/u);

const uploadAssets = extractPowerShellFunction(sources.publish, 'Invoke-MystiaReleaseAssetUpload');
assert.match(uploadAssets, /https:\/\/uploads\.github\.com\/repos\/\$Repo\/releases\/\$ReleaseId\/assets\{\?name,label\}/u);
assert.match(uploadAssets, /Get-MystiaReleaseAssetContentType -AssetName \$AssetName/u);
assert.match(uploadAssets, /\$PreparedAssets[\s\S]*foreach \(\$PreparedAsset in \$PreparedAssets\)/u);
assert.match(uploadAssets, /--method POST/u);
assert.match(uploadAssets, /--input \$AssetPath/u);
assert.match(uploadAssets, /--raw-field "name=\$AssetName"/u);
assert.match(uploadAssets, /Content-Type: \$ExpectedContentType/u);
assert.match(uploadAssets, /Wait-MystiaGitHubMutationInterval/u);
assert.match(uploadAssets, /Uploaded Release asset id/u);
assert.match(uploadAssets, /\$Response\.content_type -cne \$ExpectedContentType/u);
assert.doesNotMatch(uploadAssets, /& \$Gh release\s+upload|--clobber|Get-MystiaReleaseByTag/u);
assert.doesNotMatch(uploadAssets, /application\/octet-stream/u);
const mutationInterval = extractPowerShellFunction(sources.publish, 'Wait-MystiaGitHubMutationInterval');
assert.match(mutationInterval, /Start-Sleep -Milliseconds 1000/u);
assert.equal(
  [...sources.publish.matchAll(/Wait-MystiaGitHubMutationInterval/gu)].length,
  4,
  'Draft, every asset, and publication mutations must remain explicitly serialized after the preceding write.',
);

assert.match(sources.publish, /\$DraftReleaseId = Get-MystiaJsonPositiveInt64/u);
assert.match(sources.publish, /-Value \$CreatedDraft\.id[\s\S]*-Label "New draft Release id"/u);
assert.match(sources.publish, /\$DraftUploadUrl = \[string\]\$CreatedDraft\.upload_url/u);
assert.match(sources.publish, /-Phase "Created"/u);
assert.ok(
  [...sources.publish.matchAll(/-Phase "Uploaded"/gu)].length >= 2,
  'The uploaded draft must be verified by exact numeric id after upload and before publication.',
);
assert.match(sources.publish, /-Phase "Published"/u);
assert.match(
  sources.publish,
  /One or more assets may already exist in the exact remote draft id[\s\S]*Do not retry this release/u,
);
assert.match(
  sources.publish,
  /returned invalid JSON after the GitHub mutation request\.[\s\S]*The remote operation may already have succeeded\.[\s\S]*Do not retry this release/u,
);

const verifyRemote = extractPowerShellFunction(sources.publish, 'Assert-MystiaRemoteRelease');
assert.match(verifyRemote, /\[long\]\$ExpectedReleaseId/u);
assert.match(verifyRemote, /\[AllowEmptyCollection\(\)\]\[string\[\]\]\$ExpectedAssetNames/u);
assert.match(verifyRemote, /Assert-MystiaRemoteReleaseMetadata/u);
assert.match(verifyRemote, /Assert-MystiaRemoteReleaseAssets/u);
const verifyRemoteMetadata = extractPowerShellFunction(sources.publish, 'Assert-MystiaRemoteReleaseMetadata');
for (const exactMetadataCheck of [
  /\[string\]\$Release\.tag_name -cne \$Tag/u,
  /\[string\]\$Release\.name -cne \$ExpectedTitle/u,
  /\[string\]\$Release\.body -cne \$ExpectedNotes/u,
  /\$Release\.draft -ne \$ExpectedDraft/u,
  /\$Release\.prerelease -ne \$ExpectedPrerelease/u,
]) {
  assert.match(verifyRemoteMetadata, exactMetadataCheck);
}
assert.match(verifyRemoteMetadata, /Assert-MystiaRemoteReleaseIdentity/u);
assert.match(verifyRemoteMetadata, /Assert-MystiaJsonBoolean -Value \$Release\.draft/u);
assert.match(verifyRemoteMetadata, /Assert-MystiaJsonBoolean -Value \$Release\.prerelease/u);
assert.match(verifyRemoteMetadata, /Assert-MystiaJsonBoolean -Value \$Release\.immutable/u);
assert.match(verifyRemoteMetadata, /\$RequireImmutable[\s\S]*\$Release\.immutable/u);
const verifyRemoteAssets = extractPowerShellFunction(sources.publish, 'Assert-MystiaRemoteReleaseAssets');
assert.match(verifyRemoteAssets, /\$RemoteAssets\.Count -gt \$ExpectedPaths\.Count/u);
assert.match(verifyRemoteAssets, /Get-MystiaReleaseAssetContentType -AssetName \$Name/u);
assert.match(verifyRemoteAssets, /\[string\]\$Asset\.state -cne "uploaded"/u);
assert.match(verifyRemoteAssets, /\[string\]\$Asset\.content_type -cne \$ExpectedContentType/u);
assert.match(verifyRemoteAssets, /Get-MystiaJsonPositiveInt64 -Value \$Asset\.size/u);
assert.match(verifyRemoteAssets, /\$RemoteSize -ne \$Item\.Length/u);
assert.match(verifyRemoteAssets, /\[string\]\$Asset\.digest -cne \$ExpectedDigest/u);
assert.doesNotMatch(verifyRemoteAssets, /application\/octet-stream/u);

const publishDraft = extractPowerShellFunction(sources.publish, 'Publish-MystiaDraftRelease');
assert.match(publishDraft, /\[long\]\$ReleaseId/u);
assert.match(publishDraft, /-Method "PATCH"/u);
assert.match(publishDraft, /-Endpoint "repos\/\$Repo\/releases\/\$ReleaseId"/u);
assert.match(publishDraft, /draft = \$false/u);
assert.match(publishDraft, /make_latest = \$\(if \(\$MakeLatest\) \{ "true" \} else \{ "false" \}\)/u);
assert.match(sources.publish, /-ReleaseId \$DraftReleaseId/u);

const verifyLatest = extractPowerShellFunction(sources.publish, 'Wait-MystiaLatestRelease');
assert.match(verifyLatest, /Get-MystiaLatestRelease/u);
assert.match(verifyLatest, /\[ValidateRange\(1, 30\)\]\[int\]\$MaxAttempts = 20/u);
assert.match(verifyLatest, /Latest Release id and tag identify different release transactions/u);
assert.match(verifyLatest, /Assert-MystiaRemoteReleaseMetadata/u);
assert.match(verifyLatest, /Assert-MystiaRemoteReleaseAssets/u);

const immutablePolicy = extractPowerShellFunction(
  sources.publish,
  'Assert-MystiaImmutableReleasesEnabled',
);
assert.match(immutablePolicy, /api "repos\/\$Repo\/immutable-releases"/u);
assert.match(immutablePolicy, /Assert-MystiaJsonBoolean -Value \$Policy\.enabled/u);
assert.match(immutablePolicy, /\$Policy\.enabled -ne \$true/u);
assert.ok(
  [...sources.publish.matchAll(/Assert-MystiaImmutableReleasesEnabled -Gh \$Gh/gu)].length >= 2,
  'Official publication must verify Immutable Releases both before preparation and immediately before mutation.',
);

assert.match(sources.publish, /\$Channel -ceq "stable" -and -not \$OfficialRelease/u);
assert.match(sources.publish, /Stable releases may only use the GitHub Actions OfficialRelease path/u);
assert.match(sources.publish, /Assert-MystiaOfficialActionsContext -ExpectedSha \$TargetCommitSha/u);
assert.match(sources.publish, /\$env:GITHUB_ACTIONS -cne "true"/u);
assert.match(sources.publish, /\$env:GITHUB_REF -cne "refs\/heads\/main"/u);
assert.ok(
  [...sources.publish.matchAll(/Assert-MystiaCleanWorktree -Git \$Git/gu)].length >= 3,
  'The release worktree must be clean before build, after build, and immediately before remote mutation.',
);
assert.match(sources.publish, /Release builds require a clean tracked and untracked worktree/u);

const preparedMetadata = extractPowerShellFunction(sources.publish, 'Assert-MystiaPreparedMetadata');
assert.match(preparedMetadata, /\[AllowEmptyCollection\(\)\]\[object\[\]\]\$RemoteReleases/u);
assert.match(preparedMetadata, /\[string\]\$ExpectedTargetSha/u);
assert.match(preparedMetadata, /\$Manifest\.targetCommitSha -cne \$ExpectedTargetSha/u);
assert.match(preparedMetadata, /ConvertFrom-Json -DateKind String/u);
assert.match(preparedMetadata, /Get-MystiaJsonPositiveInt64[\s\S]*Update manifest schemaVersion/u);
assert.match(preparedMetadata, /\$Catalog\.releases -isnot \[System\.Array\]/u);
assert.match(preparedMetadata, /Assert-MystiaCatalogEntryShape -Entry \$Entry/u);
assert.match(preparedMetadata, /\$Manifest\.version -isnot \[string\]/u);
assert.match(preparedMetadata, /\$RemoteHistory\.Count -ne \$CatalogHistory\.Count/u);
assert.match(preparedMetadata, /Published Release history changed after update-catalog\.json was prepared/u);
for (const historyField of [
  /\$Entry\.version -cne \$ReleaseTag\.Substring\(1\)/u,
  /\$Entry\.title -cne \$ExpectedTitle/u,
  /\$Entry\.channel -cne \$ExpectedChannel/u,
  /\$Entry\.publishedAtUtc -cne \$ExpectedPublishedAt/u,
  /\$Entry\.releaseUrl -cne/u,
  /\$Entry\.notesMarkdown -cne \[string\]\$Release\.body/u,
]) {
  assert.match(preparedMetadata, historyField);
}
assert.equal(
  [...sources.publish.matchAll(/-RemoteReleases \$RemoteReleases/gu)].length,
  3,
  'Prepared release history must be revalidated twice before mutation and once immediately before publication.',
);
assert.match(sources.publish, /mystia-release-upload-\$\(\[Guid\]::NewGuid\(\)\.ToString\('N'\)\)/u);
assert.match(sources.publish, /Copy-Item -LiteralPath \$SourcePath -Destination \(Join-Path \$UploadRoot \$AssetName\)/u);
assert.match(sources.publish, /-ExpectedAssetNames \(\[string\[\]\]@\(\)\)/u);
assert.match(sources.publish, /-RequireImmutable \$OfficialRelease/u);
const publicationTransaction = extractPowerShellFunction(
  sources.publish,
  'Invoke-MystiaReleasePublicationTransaction',
);
assert.match(publicationTransaction, /\[ValidateRange\(1, 30\)\]\[int\]\$ReadMaxAttempts = 20/u);
assert.match(publicationTransaction, /\$DraftReleaseId = Get-MystiaJsonPositiveInt64/u);
assert.match(publicationTransaction, /\$DraftUploadUrl = \[string\]\$CreatedDraft\.upload_url/u);
assert.match(publicationTransaction, /Get-MystiaRepositoryReleases -Gh \$Gh/u);
assert.match(publicationTransaction, /Assert-MystiaPreparedMetadata/u);
assert.match(publicationTransaction, /Assert-MystiaOfficialTarget -Gh \$Gh/u);
assert.match(publicationTransaction, /Assert-MystiaImmutableReleasesEnabled -Gh \$Gh/u);
const transactionBeforeFirstMutation = publicationTransaction.slice(
  0,
  publicationTransaction.indexOf('New-MystiaRemoteTag'),
);
assert.match(transactionBeforeFirstMutation, /Get-MystiaReleaseAssetContentType -AssetName \$AssetName/u);
assert.match(transactionBeforeFirstMutation, /Get-MystiaReleaseAssetNames -IncludeAndroid \$IncludeAndroid/u);
assert.match(transactionBeforeFirstMutation, /asset allowlist does not match the canonical asset set/u);
const postCreateTransaction = publicationTransaction.slice(
  publicationTransaction.indexOf('$CreatedDraft = New-MystiaDraftRelease'),
);
assert.doesNotMatch(
  postCreateTransaction,
  /Get-MystiaReleaseByTag|Get-MystiaRemoteTagRef|releases\/tags\/|matching-refs|graphql/u,
  'The current Release transaction must never be rediscovered by tag after Draft creation.',
);
const mutationSequence = [
  'New-MystiaRemoteTag',
  '$CreatedDraft = New-MystiaDraftRelease',
  'Invoke-MystiaReleaseAssetUpload',
  '$PublishedResponse = Publish-MystiaDraftRelease',
];
let previousMutation = -1;
for (const marker of mutationSequence) {
  const currentMutation = publicationTransaction.indexOf(marker);
  assert.ok(currentMutation > previousMutation, `Release transaction step is missing or out of order: ${marker}.`);
  previousMutation = currentMutation;
}
const publishMain = sources.publish.slice(sources.publish.indexOf('Push-Location $RepoRoot'));
assert.equal(
  [...publishMain.matchAll(/Invoke-MystiaReleasePublicationTransaction/gu)].length,
  1,
  'The publish entry point must call the single remote publication transaction exactly once.',
);
assert.doesNotMatch(
  publishMain,
  /New-MystiaRemoteTag|New-MystiaDraftRelease|Invoke-MystiaReleaseAssetUpload|Publish-MystiaDraftRelease|Wait-MystiaRemoteReleaseState|Wait-MystiaLatestRelease/u,
  'The publish entry point must not duplicate remote transaction orchestration outside its single function.',
);

assert.match(sources.prepare, /generate-update-catalog\.mjs/u);
assert.match(sources.prepare, /\[switch\]\$RequireAndroid/u);
assert.match(sources.prepare, /Official releases require exactly the arm64-v8a and armeabi-v7a APK assets/u);
assert.match(sources.prepare, /catalogSha256/u);
assert.match(sources.prepare, /catalogSize/u);
assert.match(sources.prepare, /targetCommitSha = \$TargetCommitSha/u);
assert.match(sources.prepare, /Assert-MystiaPreparedChecksums/u);
assert.match(sources.prepare, /\.release-metadata-\$\(\[Guid\]::NewGuid\(\)\.ToString\('N'\)\)/u);
assert.match(sources.prepare, /Commit-MystiaPreparedMetadata/u);
assert.match(sources.prepare, /rollback was incomplete/u);
assert.match(sources.prepare, /Pending release metadata transaction requires manual inspection/u);
assert.match(sources.prepare, /check-build-toolchain\.mjs"\),\s*"release-tools"/u);
assert.match(sources.prepare, /api --paginate --slurp "repos\/\$Repo\/releases\?per_page=100"/u);
assert.match(sources.releaseCommon, /SHA256SUMS\.txt/u);
assert.match(sources.releaseCommon, /TargetCommitSha must be a full lowercase 40-character Git commit SHA/u);
assert.match(sources.releaseCommon, /\$Tag -cnotmatch/u);
assert.match(sources.releaseCommon, /\$Version -cmatch/u);
assert.match(sources.releaseCommon, /mystia-steward-companion-android-arm64-v8a\.apk/u);
assert.match(sources.releaseCommon, /mystia-steward-companion-android-armeabi-v7a\.apk/u);
assert.match(sources.releaseCommon, /MystiaReleaseAssetContentTypes/u);
assert.match(sources.releaseCommon, /\[System\.StringComparer\]::Ordinal/u);
assert.match(sources.releaseCommon, /MystiaReleaseAssetContentTypes\.Count -ne 7/u);
for (const canonicalContentType of [
  'application/zip',
  'application/x-msdownload',
  'application/vnd.android.package-archive',
  'application/json',
  'text/plain; charset=utf-8',
]) {
  assert.ok(
    sources.releaseCommon.includes(`"${canonicalContentType}"`),
    `Missing canonical Release asset MIME type: ${canonicalContentType}.`,
  );
}
const releaseAssetContentType = extractPowerShellFunction(
  sources.releaseCommon,
  'Get-MystiaReleaseAssetContentType',
);
assert.match(releaseAssetContentType, /MystiaReleaseAssetContentTypes\.ContainsKey\(\$AssetName\)/u);
assert.match(releaseAssetContentType, /Unknown canonical Release asset name/u);
assert.doesNotMatch(sources.publish, /application\/octet-stream/u);
assert.match(sources.releaseRuntime, /function Get-OracleContentType/u);
assert.match(
  sources.releaseRuntime,
  /\$OracleContentType = Get-OracleContentType -AssetName \$AssetName/u,
);
assert.match(
  sources.releaseRuntime,
  /\$ExpectedContentTypeHeader = "Content-Type: \$OracleContentType"/u,
);
assert.match(
  sources.releaseRuntime,
  /content_type = \$\(if \([\s\S]*?wrong-mime-response[\s\S]*?else \{\s*\$OracleContentType/u,
);
assert.doesNotMatch(
  sources.releaseRuntime,
  /content_type\s*=\s*\$ContentTypeHeaders|content_type\s*=\s*\$ExpectedContentTypeHeader/u,
  'The stateful fake-gh must derive response MIME from its independent asset-name oracle.',
);

const prebuildPruneCondition = sources.build.match(
  /if \(-not \$SkipBuildCacheCleanup[^\n]+\) \{\s*Invoke-BuildCachePrune -Title "Prune stale build artifacts before compilation"/u,
)?.[0] ?? '';
assert.ok(prebuildPruneCondition, 'Missing prebuild Tauri cache prune condition.');
assert.match(prebuildPruneCondition, /-not \$SkipTauriBuild/u);
assert.doesNotMatch(prebuildPruneCondition, /SkipFrontendBuild/u);

assert.match(sources.packagePowerShell, /function Assert-NoPendingReleaseTransactions/u);
assert.match(sources.packagePowerShell, /\^dist\\\.\(staging\|backup\)-/u);
assert.match(sources.packageBash, /assert_no_pending_release_transactions/u);
assert.match(sources.packageBash, /cleanup_transaction 130/u);
assert.match(sources.packageBash, /cleanup_transaction 143/u);

assert.match(sources.android, /assertRealDirectory\(distDir, 'Android release dist'\)/u);
assert.match(sources.android, /const stagingDir = mkdtempSync\(path\.join\(distDir, '\.android-apk-stage-'\)\)/u);
const androidMainBody = sources.android.match(/function main\(\) \{[\s\S]+?\n\}/u)?.[0] ?? '';
assert.ok(androidMainBody, 'Missing signed Android APK main function.');
assert.ok(
  androidMainBody.indexOf('verifyBuildToolchain();') < androidMainBody.indexOf('mkdirSync(distDir'),
  'Signed Android APK builds do not verify the locked toolchain before writing build artifacts.',
);
assert.ok(
  androidMainBody.indexOf('pruneBuildArtifacts();') < androidMainBody.indexOf('runTauriAndroidApkBuild();'),
  'Signed Android APK builds do not prune stale caches before compiling.',
);
assert.match(sources.android, /check-build-toolchain\.mjs/u);
assert.match(sources.android, /\[buildToolchainCheck, 'android'\]/u);
assert.match(sources.android, /signingCertificateSha256/u);
assert.match(sources.android, /Android APK signing certificate mismatch/u);
for (const helperName of ['is_main_window_focused', 'hide_main_window']) {
  const definitions = [...sources.tauriApp.matchAll(new RegExp(
    `(?<attributes>(?:#\\[cfg\\([^\\n]+\\)\\]\\s*)*)fn ${helperName}\\(`,
    'gu',
  ))];
  assert.equal(definitions.length, 1, `${helperName} must have one canonical implementation.`);
  assert.match(
    definitions[0].groups?.attributes ?? '',
    /#\[cfg\(desktop\)\]/u,
    `${helperName} must remain excluded from Android and other mobile targets.`,
  );
}
const pruneThenCommitBody = sources.android.match(
  /function pruneThenCommitAndroidApks[\s\S]+?\n\}/u,
)?.[0] ?? '';
assert.ok(pruneThenCommitBody, 'Missing Android prune-then-commit transaction function.');
assert.ok(
  pruneThenCommitBody.indexOf('prune();') < pruneThenCommitBody.indexOf('commitStagedAndroidApks'),
  'Android APK assets are committed before build artifact pruning.',
);

console.log('Release script policy audit passed.');
