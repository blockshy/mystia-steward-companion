import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const readSource = (relativePath) => fs.readFileSync(path.join(root, relativePath), 'utf8');

function listFilesRecursively(directory) {
  return fs.readdirSync(directory, { withFileTypes: true })
    .sort((left, right) => left.name.localeCompare(right.name))
    .flatMap((entry) => {
      const absolutePath = path.join(directory, entry.name);
      return entry.isDirectory() ? listFilesRecursively(absolutePath) : [absolutePath];
    });
}

const catalog = readSource('mods/bepinex/src/Save/RuntimeStaticDataCatalog.cs');
const mappedGuests = readSource('mods/bepinex/src/Save/RuntimeMappedGuestCatalog.cs');
const recommendationState = readSource(
  'mods/bepinex/src/Save/RuntimeReflectionRecommendationStateProvider.cs',
);
const nightBusinessProvider = readSource(
  'mods/bepinex/src/Save/NightBusinessReflectionProvider.cs',
);
const overlayController = readSource(
  'mods/bepinex/src/Ui/StewardOverlayController.cs',
);
const rareGuestInvitations = readSource(
  'mods/bepinex/src/Save/RuntimeRareGuestInvitationService.cs',
);
const rareGuestInvitationHook = readSource(
  'apps/companion/src/companion/hooks/useRareGuestInvitations.ts',
);
const companionConnection = readSource(
  'apps/companion/src/companion/hooks/useCompanionConnection.ts',
);
const coreProjection = readSource('mods/bepinex/src/Save/RuntimeCoreMappingProjection.cs');
const storageProjection = readSource('mods/bepinex/src/Save/RuntimeStorageStateProjection.cs');
const modCSharpSources = listFilesRecursively(path.join(root, 'mods/bepinex/src'))
  .filter((filePath) => filePath.endsWith('.cs'))
  .map((filePath) => ({
    filePath: path.relative(root, filePath),
    source: fs.readFileSync(filePath, 'utf8'),
  }));

const runtimeMissionDiagnosticCapturePath =
  'mods/bepinex/src/Save/RuntimeMissionDiagnosticCapture.cs';
const runtimeScheduledEventDiagnosticCapturePath =
  'mods/bepinex/src/Save/RuntimeScheduledEventDiagnosticCapture.cs';
const runtimeScheduledMissionSourceReaderPath =
  'mods/bepinex/src/Save/RuntimeScheduledMissionSourceReader.cs';
const runtimeMissionRecipePriorityProjectionPath =
  'mods/bepinex/src/Save/RuntimeMissionRecipePriorityProjection.cs';
const runtimeMissionDiagnosticFiles = new Set([
  runtimeMissionDiagnosticCapturePath,
  runtimeScheduledEventDiagnosticCapturePath,
  runtimeScheduledMissionSourceReaderPath,
  'mods/bepinex/src/Save/RuntimeMissionDiagnosticState.cs',
  'mods/bepinex/src/Save/RuntimeMissionLoadSeedParser.cs',
  'mods/bepinex/src/Save/RuntimeMissionDefinitionDiagnosticReader.cs',
  'mods/bepinex/src/Save/RuntimeServeInWorkMissionDiagnosticCapture.cs',
  'mods/bepinex/src/Save/RuntimeServeInWorkMissionDiagnosticState.cs',
]);
const runtimeMissionBusinessFiles = new Set([
  runtimeMissionRecipePriorityProjectionPath,
  'mods/bepinex/src/LocalApi/LocalApiAvailableMissionsPayload.cs',
  'mods/bepinex/src/LocalApi/LocalApiTrackedMissionsPayload.cs',
  'mods/bepinex/src/Save/RuntimeAvailableMissionCapture.cs',
  'mods/bepinex/src/Save/RuntimeMissionPresentation.cs',
  'mods/bepinex/src/Save/RuntimeMissionPresentationReader.cs',
  'mods/bepinex/src/Ui/StewardOverlayController.cs',
]);
for (const forbiddenRuntimeMissionPath of [
  'AllNodesMapping',
  'GetAllNodes',
  'GetAllMissionData',
  'scheduledEvents',
]) {
  const allowedFiles = forbiddenRuntimeMissionPath === 'scheduledEvents'
    ? new Set([runtimeScheduledMissionSourceReaderPath])
    : new Set();
  const offendingFiles = modCSharpSources
    .filter(({ filePath, source }) => source.includes(forbiddenRuntimeMissionPath)
      && !allowedFiles.has(filePath))
    .map(({ filePath }) => filePath);
  assert.deepEqual(
    offendingFiles,
    [],
    `Mod source restored forbidden runtime mission path ${forbiddenRuntimeMissionPath}: ${offendingFiles.join(', ')}`,
  );
}

const unauthorizedRuntimeMissionFiles = modCSharpSources
  .filter(({ filePath, source }) => source.includes('RuntimeMission')
    && !runtimeMissionDiagnosticFiles.has(filePath)
    && !runtimeMissionBusinessFiles.has(filePath)
    && source
      .replaceAll(/RuntimeMissionDiagnostic[A-Za-z0-9_]*/g, '')
      .includes('RuntimeMission'))
  .map(({ filePath }) => filePath);
assert.deepEqual(
  unauthorizedRuntimeMissionFiles,
  [],
  `Mod source added an unauthorized runtime mission business path: ${unauthorizedRuntimeMissionFiles.join(', ')}`,
);

const runtimeMissionRecipePriorityProjection = readSource(
  runtimeMissionRecipePriorityProjectionPath,
);
for (const requiredPriorityGate of [
  'catalog.IsComplete',
  'business.IsActive',
  'business.Generation > 0',
  'mission.Ready',
  'mission.RuntimeAvailable',
  'mission.Generation > 0',
  'serveInWork.MissionGeneration == mission.Generation',
  'serveInWork.BusinessGeneration == business.Generation',
  'serveInWork.NightPhase, ActivePhase',
  'Active: false',
  'Error: null',
  'SpecialBusinessChallengeTypes.NotChallenge',
  'order.GuestId != signal.CanonicalGuestId',
  'order.RuntimeGuestId != signal.RawGuestId',
  'order.HasServedFood',
  'string.IsNullOrWhiteSpace(order.TraceId)',
  'if (matchingOrderIndex >= 0)',
  'matchedRecipe.RecipeId < 0',
]) {
  assert.ok(
    runtimeMissionRecipePriorityProjection.includes(requiredPriorityGate),
    `Mission recipe priority must retain gate: ${requiredPriorityGate}.`,
  );
}
assert.ok(
  overlayController.includes('RuntimeMissionRecipePriorityProjection.Enrich('),
  'The snapshot publisher must use the single mission recipe priority projection.',
);
for (const forbiddenActiveMissionCall of [
  'ContainsSpecialNPCServeInWorkMission',
  'UpdateFinishStates',
  'HasFulfilled',
]) {
  assert.ok(
    !runtimeMissionRecipePriorityProjection.includes(forbiddenActiveMissionCall),
    `Mission recipe projection must not call ${forbiddenActiveMissionCall}.`,
  );
  assert.ok(
    !overlayController.includes(forbiddenActiveMissionCall),
    `Snapshot publication must not call ${forbiddenActiveMissionCall}.`,
  );
}

for (const countOnlyRuntimeMember of [
  'trackingMissions',
  'trackingMissionBuffer',
]) {
  const offendingFiles = modCSharpSources
    .filter(({ filePath, source }) => source.includes(countOnlyRuntimeMember)
      && filePath !== runtimeMissionDiagnosticCapturePath)
    .map(({ filePath }) => filePath);
  assert.deepEqual(
    offendingFiles,
    [],
    `Only the diagnostic capture may inspect ${countOnlyRuntimeMember}: ${offendingFiles.join(', ')}`,
  );
}

const runtimeMissionDiagnosticCapture = readSource(runtimeMissionDiagnosticCapturePath);
assert.ok(
  runtimeMissionDiagnosticCapture.includes(
    'RuntimeConcreteCollectionReader.TryReadDictionaryCount(\n'
      + '                trackingMissions,',
  ),
  'Runtime mission diagnostics must read only the tracking dictionary Count.',
);
assert.ok(
  !runtimeMissionDiagnosticCapture.includes(
    'RuntimeConcreteCollectionReader.TryReadDictionary(\n'
      + '                trackingMissions,',
  ),
  'Runtime mission diagnostics must never enumerate the complex tracking dictionary.',
);
for (const forbiddenDiagnosticCall of [
  'GetEnumerator',
  'ParseActiveMissionData',
  'HasFulfilled',
  'GetAllMissionData',
  'GetAllNodes',
  'AllNodesMapping',
]) {
  assert.ok(
    !runtimeMissionDiagnosticCapture.includes(forbiddenDiagnosticCall),
    `Runtime mission diagnostics restored forbidden call ${forbiddenDiagnosticCall}.`,
  );
}

for (const mappingName of [
  'IngredientsMapping',
  'BeveragesMapping',
  'FoodsMapping',
  'RecipesMapping',
]) {
  assert.ok(
    catalog.includes(
      `"${mappingName}",\n                RuntimeCoreMappingIdDomain.NonNegativeContent)`,
    ),
    `Runtime catalog must project DataBaseCore.${mappingName} through the non-negative content domain.`,
  );
}
assert.ok(
  catalog.includes(
    '"IzakayasMapping",\n                RuntimeCoreMappingIdDomain.Signed)',
  ),
  'Runtime catalog must preserve the complete signed DataBaseCore.IzakayasMapping domain.',
);

for (const requiredContract of [
  [catalog, 'RuntimeCoreMappingProjection.ReadIds(entries, memberName, idDomain)'],
  [mappedGuests, 'InvokeRequiredStatic(methods.GetAllMappedGuests)'],
  [mappedGuests, 'ReadBaseGuestIdentities(methods.GetAllSpecialGuests)'],
  [mappedGuests, 'AliasSource = "base-identity"'],
  [mappedGuests, 'ResolveCanonicalSourceId(mapping, mappingsById, baseGuestsById)'],
  [mappedGuests, 'private static RuntimeMappedGuestMethodSet? _cachedMethods'],
  [catalog, 'allowNegativeIds: true'],
  [catalog, 'ReadRequiredIntArrayMember(runtime, "RawTags")'],
  [catalog, 'private static RuntimeStaticMethodSet? _cachedMethods'],
  [recommendationState, 'RuntimeStorageStateProjection.ReadAvailableRecipeIds('],
  [recommendationState, 'RuntimeStorageStateProjection.ReadIngredientQuantities('],
  [recommendationState, 'RuntimeStorageStateProjection.ReadBeverageQuantities('],
  [recommendationState, '"HaveRecipe"'],
  [recommendationState, '"GetIngredientCountById"'],
  [recommendationState, '"GetBeverageCountById"'],
  [coreProjection, 'entries.Count > MaxMappingItems'],
  [
    coreProjection,
    'idDomain == RuntimeCoreMappingIdDomain.NonNegativeContent && id < 0',
  ],
  [storageProjection, 'quantity < -1'],
]) {
  const [source, marker] = requiredContract;
  assert.ok(source.includes(marker), `Runtime catalog contract is missing: ${marker}`);
}

assert.ok(
  !storageProjection.includes('allowInfinite')
  && !storageProjection.includes('var minimum'),
  'Ingredient and beverage inventory must share the exact -1 native infinite sentinel.',
);
assert.ok(
  overlayController.includes('LocalApiRuntimeDataPayload.Create(_runtimeDataCatalog, LocalApiJsonOptions)')
  && overlayController.includes('ReferenceEquals(_runtimeDataPayloadCatalog, _runtimeDataCatalog)'),
  'Runtime data publication must hash the complete serialized catalog once per catalog generation.',
);
assert.ok(
  !overlayController.includes('BuildRuntimeDataSignature')
  && !companionConnection.includes('buildRuntimeDataCatalogCacheSignature'),
  'Count/edge runtime-data signatures must not replace the authoritative payload hash.',
);

assert.ok(
  !catalog.includes('ReadRequiredIntArrayMember(runtime, "tags")'),
  'Runtime catalog must not read the computed Sellable.Tags projection.',
);

const forbiddenSources = [catalog, mappedGuests, recommendationState].join('\n');
for (const forbiddenPath of [
  'GetSpecialGuestsAndMappedGuests',
  'GenerateDummy',
  'GenerateSaveData',
  'GetAllIngredients',
  'GetAllBeverages',
  'GetAllFoods',
  'GetAllRecipes',
  'GetAllIzakayas',
]) {
  assert.ok(
    !forbiddenSources.includes(forbiddenPath),
    `Runtime catalog restored a forbidden whole-database or save-generation path: ${forbiddenPath}`,
  );
}

assert.ok(
  nightBusinessProvider.includes('ResolveCachedMappedGuestIdentity('),
  'Night-business identity must resolve through the verified mapped identity snapshot.',
);

const currentPlaceStart = nightBusinessProvider.indexOf(
  'private CurrentPlaceIdentity ReadCurrentPlaceIdentity()',
);
const currentPlaceEnd = nightBusinessProvider.indexOf(
  'private static object? ReadIzakayaData()',
  currentPlaceStart,
);
assert.ok(
  currentPlaceStart >= 0 && currentPlaceEnd > currentPlaceStart,
  'Night-business place identity must use one explicit read boundary.',
);
const currentPlaceSource = nightBusinessProvider.slice(currentPlaceStart, currentPlaceEnd);
const currentPlaceLabelRead = currentPlaceSource.indexOf('"DaySceneMapLabel"');
const currentPlaceBlankGate = currentPlaceSource.indexOf(
  'if (string.IsNullOrWhiteSpace(label))',
);
const currentPlaceNameRead = currentPlaceSource.indexOf('"DaySceneMapName"');
assert.ok(
  currentPlaceLabelRead >= 0
    && currentPlaceBlankGate > currentPlaceLabelRead
    && currentPlaceNameRead > currentPlaceBlankGate,
  'Night-business place reads must enforce label -> blank gate -> localized name ordering.',
);
assert.equal(
  currentPlaceSource.match(/ReadIzakayaData\(\)/g)?.length ?? 0,
  1,
  'Night-business place identity must reuse one IzakayaData object.',
);
assert.ok(
  !nightBusinessProvider.includes('ReadCurrentPlaceLabel'),
  'The removed independent place-label read path must not return.',
);
assert.ok(
  nightBusinessProvider.includes(
    'var placeIdentity = Measure("place.identity", ReadCurrentPlaceIdentity);',
  ),
  'Night-business context must acquire one measured place identity.',
);

const catalogPlaceStart = catalog.indexOf(
  'private static string ResolveIzakayaPlaceName(object izakaya)',
);
const catalogPlaceEnd = catalog.indexOf(
  'private static string? ReadExactStringProperty(',
  catalogPlaceStart,
);
assert.ok(
  catalogPlaceStart >= 0 && catalogPlaceEnd > catalogPlaceStart,
  'Runtime catalog must keep one explicit Izakaya place-name boundary.',
);
const catalogPlaceSource = catalog.slice(catalogPlaceStart, catalogPlaceEnd);
const catalogPlaceLabelRead = catalogPlaceSource.indexOf('"DaySceneMapLabel"');
const catalogPlaceBlankGate = catalogPlaceSource.indexOf(
  'if (string.IsNullOrWhiteSpace(mapLabel))',
);
const catalogPlaceNameRead = catalogPlaceSource.indexOf('"DaySceneMapName"');
assert.ok(
  catalogPlaceLabelRead >= 0
    && catalogPlaceBlankGate > catalogPlaceLabelRead
    && catalogPlaceNameRead > catalogPlaceBlankGate,
  'Runtime catalog place reads must retain label -> blank gate -> localized name ordering.',
);
for (const forbiddenIdentityPath of [
  'RareCustomerIdentityResolver',
  '"IsSpecialGuestMapped"',
  '"MappedID2TargetID"',
  '"RefSGuest"',
  'ReadGuestDisplayName(',
  'Tewi_HardSell',
  'Remilia',
]) {
  assert.ok(
    !nightBusinessProvider.includes(forbiddenIdentityPath),
    `Night-business refresh restored a side-effecting or hard-coded identity path: ${forbiddenIdentityPath}`,
  );
}

for (const forbiddenMappedGuestGetter of [
  '"LikeFoodTag"',
  '"LikeFoodTagOriginal"',
  '"HateFoodTag"',
  '"HateFoodTagOriginal"',
  '"LikeBevTag"',
  '"LikeBevTagOriginal"',
]) {
  assert.ok(
    !mappedGuests.includes(forbiddenMappedGuestGetter),
    `Mapped guest identity must not evaluate preference getter: ${forbiddenMappedGuestGetter}`,
  );
}

for (const requiredInvitationContract of [
  'new RuntimeMappedGuestCatalog(repository).Snapshot()',
  'mappedGuestSnapshot.Entries',
  'RequireExactStaticMethod(\n            dataBaseCharacterType,\n            "GetAllSpecialGuests"',
  'RuntimeConcreteCollectionReader.TryReadReferenceArray(source, out var guests, out var failure)',
  'RuntimeRareGuestInvitationIdentity.Resolve(',
  'baseGuestsById.TryGetValue(invitationIdentity.CanonicalGuestId, out var guest)',
  'RuntimeRareGuestInvitationCandidates.Deduplicate(candidates)',
  'ReadRequiredStaticDictionaryProperty(',
  '"allNPCs",',
  '"trackedNPCs",',
  '"RecordedSpecialNPCs",',
  'IsClosedIl2CppDictionary(valueType, typeof(string), trackedNpcType)',
  'RuntimeTrackedNpcAvailability.Evaluate',
  '"defaultDestination"',
  'RuntimeConcreteCollectionReader.TryGetDictionaryValue',
]) {
  assert.ok(
    rareGuestInvitations.includes(requiredInvitationContract),
    `Rare-guest invitations must consume verified base and mapped identity data: ${requiredInvitationContract}`,
  );
}

for (const forbiddenInvitationPath of [
  '"RefSGuest"',
  'GetSpecialGuestsAndMappedGuests',
  'GenerateDummy',
  'TryRefreshNPCs',
  'GetMapNPCs',
  'RefTrackedNPCAvailability',
  'GetOrGenerateSpecialNPCKizunaLevel',
  'RuntimeReflectionUtility.EnumerateObjects',
  '"ShouldShown"',
  '"RefNPC"',
  '"None"',
  'ReadRequiredStaticField',
  'GetMapLabelFromSpawnMarker',
]) {
  assert.ok(
    !rareGuestInvitations.includes(forbiddenInvitationPath),
    `Rare-guest invitations restored a side-effecting identity path: ${forbiddenInvitationPath}`,
  );
}

assert.ok(
  rareGuestInvitations.includes('HasNpcDayDestination(\n                    npc,')
    && rareGuestInvitations.includes('RuntimeConcreteCollectionReader.TryReadReferenceArray(\n                possibleDestinations'),
  'All-scene invitation candidates must require a non-empty exact day-destination array without reverse lookup.',
);

for (const requiredAutomaticInvitationLoad of [
  'buildRareGuestInvitationRefreshIdentity',
  'requestGenerationRef',
  'listAbortControllerRef',
  'snapshot: LocalApiSnapshot',
  'active: boolean',
]) {
  assert.ok(
    rareGuestInvitationHook.includes(requiredAutomaticInvitationLoad),
    `Passive invitation refresh is missing context or stale-request isolation: ${requiredAutomaticInvitationLoad}`,
  );
}

assert.ok(
  overlayController.includes('if (_runtimeMappedGuestSnapshot?.IsComplete != true)')
    && overlayController.includes('_runtimeMappedGuestSnapshot = null;\n        RuntimeMappedGuestCatalog.ResetSnapshot();'),
  'Completed mapped identity must survive ordinary scene changes and be invalidated with loaded runtime state.',
);

for (const removedProbe of [
  'GetSpecialFoodTagLang',
  'GetSpecialBevTagLang',
  'specialFoodText=',
  'specialBevText=',
  'ResolveLanguageDictionary(',
]) {
  assert.ok(
    !catalog.includes(removedProbe),
    `Runtime catalog must not call the unused warning-producing probe: ${removedProbe}`,
  );
}

console.log(
  'PASS: runtime catalog and passive invitation reads avoid broad, synthetic, refreshing, or generating guest paths.',
);
