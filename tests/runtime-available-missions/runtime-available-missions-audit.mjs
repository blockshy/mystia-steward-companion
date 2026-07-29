import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';

const repoRoot = process.cwd();
const availableFiles = [
  'mods/bepinex/src/Save/RuntimeAvailableMissionCapture.cs',
  'mods/bepinex/src/Save/RuntimeAvailableMissionSnapshot.cs',
  'mods/bepinex/src/Save/RuntimeAvailableMissionState.cs',
  'mods/bepinex/src/LocalApi/LocalApiAvailableMissionsPayload.cs',
];
const combined = availableFiles
  .map((relativePath) => fs.readFileSync(path.join(repoRoot, relativePath), 'utf8'))
  .join('\n');
const sourceReader = fs.readFileSync(
  path.join(repoRoot, 'mods/bepinex/src/Save/RuntimeScheduledMissionSourceReader.cs'),
  'utf8',
);
const controller = fs.readFileSync(
  path.join(repoRoot, 'mods/bepinex/src/Ui/StewardOverlayController.cs'),
  'utf8',
);
const localApi = fs.readFileSync(
  path.join(repoRoot, 'mods/bepinex/src/LocalApi/LocalApiServer.cs'),
  'utf8',
);
const presentationReader = fs.readFileSync(
  path.join(repoRoot, 'mods/bepinex/src/Save/RuntimeMissionPresentationReader.cs'),
  'utf8',
);

const forbidden = [
  'RuntimeScheduledEventDiagnosticCapture.',
  'RuntimeScheduledEventDiagnosticState',
  'ParseActiveMissionData',
  'GetAllMissionData',
  'GetAllNodes',
  'AllNodesMapping',
  '.CanContinue(',
  '.StartMission(',
  'CheckCharacterInteractEvent',
  'RefNPC',
  'RefOrGenerateSpecialRunTimeData',
  'FindUnityObject',
];

const violations = forbidden.filter((token) => combined.includes(token));
if (violations.length > 0) {
  throw new Error(
    `Available mission business code contains forbidden runtime/diagnostic dependencies: ${violations.join(', ')}`,
  );
}

for (const required of [
  'SupportedTriggerType = 5',
  'SupportedReferenceSource = "postMissionsAfterPerformance"',
  'EligibilityDisposition',
  'PreNodes',
  'LoopedMission',
  'representative.Active',
  'representative.Finished',
  'LocalApiSnapshotSignature.Compute(canonicalJson)',
]) {
  if (!combined.includes(required)) {
    throw new Error(`Available mission business contract is missing: ${required}`);
  }
}

for (const forbiddenReaderCall of [
  'ParseActiveMissionData',
  'GetAllMissionData',
  'GetAllNodes',
  'AllNodesMapping',
  'CheckCharacterInteractEvent',
  'RefOrGenerateSpecialRunTimeData',
  'GetOrGenerateSpecialNPCKizunaLevel',
  'FindUnityObject',
]) {
  if (sourceReader.includes(forbiddenReaderCall)) {
    throw new Error(`Fresh scheduled mission reader restored forbidden call: ${forbiddenReaderCall}`);
  }
}
for (const requiredReaderContract of [
  'ReadFresh(',
  'FinishedEvents: finishedEvents',
  'FinishedMissions: finishedMissions',
  'PreNodes',
  'LoopedMission',
  'scheduled-events-bucket-count-changed',
  'finished-labels-changed-during-capture',
  'runtime-player-corrected-day-changed',
]) {
  if (!sourceReader.includes(requiredReaderContract)) {
    throw new Error(`Fresh scheduled mission reader is missing: ${requiredReaderContract}`);
  }
}
for (const requiredControllerContract of [
  'PendingAvailableMissionRead : MainThreadCommand<RuntimeAvailableMissionSnapshot>',
  'ProcessPendingAvailableMissionReads();',
  'if (!pending.TryBegin()) continue;',
  'RuntimeScheduledMissionSourceReader.ReadFresh(',
  'MissionGeneration = mission.Generation',
  'missionAfter.ChangeVersion != missionBefore.ChangeVersion',
  'RuntimeSceneReadinessCapture.DaySceneGeneration != dayGeneration',
  'ReferenceEquals(',
  'FinishedEvents: source.FinishedEvents',
  'FinishedMissions: source.FinishedMissions',
  'AppendValue(builder, snapshot.MissionGeneration);',
  'CancelPendingMainThreadCommands(_pendingAvailableMissionReads',
]) {
  if (!controller.includes(requiredControllerContract)) {
    throw new Error(`Available mission main-thread contract is missing: ${requiredControllerContract}`);
  }
}
if (controller.includes('RuntimeScheduledEventDiagnosticCapture.Report()')) {
  throw new Error('Available mission controller must not consume the frozen scheduled diagnostic report.');
}
for (const forbiddenPresentationCall of [
  'RefNPC',
  'RefDaySceneName',
  'GetMapLabelFromSpawnMarker',
  'GetMapLanguageData',
  'FindUnityObject',
  'EnumerateObjects',
  'GetMemberValue',
]) {
  if (presentationReader.includes(forbiddenPresentationCall)) {
    throw new Error(
      `Mission presentation reader restored forbidden call: ${forbiddenPresentationCall}`,
    );
  }
}
for (const requiredPresentationContract of [
  'RuntimeMissionPresentationReader.ReadMany(',
  'ReceiverLabel: presentation.ReceiverLabel',
  'CharacterName: presentation.CharacterName',
  'SceneNames: presentation.SceneNames.ToArray()',
  'PresentationStatus: presentation.PresentationStatus',
  'RuntimeMissionPresentation.IsValid(',
]) {
  if (!combined.includes(requiredPresentationContract)
      && !controller.includes(requiredPresentationContract)) {
    throw new Error(
      `Available mission presentation contract is missing: ${requiredPresentationContract}`,
    );
  }
}
if ((localApi.match(/case "\/missions\/available":/g) ?? []).length !== 1
    || !localApi.includes('snapshot/runtime-available-missions.json')
    || !localApi.includes('_readAvailableMissions()')
    || !localApi.includes('LocalApiAvailableMissionsPayload.BuildJson(')) {
  throw new Error('Canonical available mission API or diagnostic export wiring is incomplete.');
}

console.log(
  'PASS: available mission business code remains isolated from frozen diagnostics, '
    + 'uses a cancellable Unity main-thread fresh read, and publishes one canonical GET payload.',
);
