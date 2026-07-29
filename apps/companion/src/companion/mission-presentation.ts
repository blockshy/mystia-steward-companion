import type { MissionPresentationMetadata } from '@/companion/types';

export const MAX_MISSION_RECEIVER_LABEL_LENGTH = 512;
export const MAX_MISSION_CHARACTER_NAME_LENGTH = 256;
export const MAX_MISSION_PRESENTATION_STATUS_LENGTH = 256;
export const MAX_MISSION_SCENE_COUNT = 64;
export const MAX_MISSION_SCENE_NAME_LENGTH = 256;
const MISSION_PRESENTATION_UNAVAILABLE_STATUSES = new Set([
  'unavailable:pending',
  'unavailable:entry-read',
  'unavailable:shape',
  'unavailable:npc-catalog',
  'unavailable:npc-missing',
  'unavailable:npc-identity',
  'unavailable:mapped-identity',
  'unavailable:character-name',
  'unavailable:destinations',
  'unavailable:map-catalog',
  'unavailable:scene-language',
  'unavailable:destination-marker',
  'unavailable:scene-marker-ambiguous',
  'unavailable:scene-marker',
  'unavailable:scene-name',
  'unavailable:scene-count',
]);

export function parseMissionPresentationMetadata(
  mission: Record<string, unknown>,
  label: string,
): MissionPresentationMetadata {
  const receiverLabel = requireBoundedString(
    mission.receiverLabel,
    `${label}.receiverLabel`,
    MAX_MISSION_RECEIVER_LABEL_LENGTH,
    true,
  );
  const characterName = requireBoundedString(
    mission.characterName,
    `${label}.characterName`,
    MAX_MISSION_CHARACTER_NAME_LENGTH,
    true,
  );
  if (characterName !== '' && !characterName.trim()) {
    throw new Error(`任务响应中的 ${label}.characterName 仅包含空白字符。`);
  }
  const presentationStatus = requireBoundedString(
    mission.presentationStatus,
    `${label}.presentationStatus`,
    MAX_MISSION_PRESENTATION_STATUS_LENGTH,
    false,
  );
  const sceneNames = parseSceneNames(mission.sceneNames, `${label}.sceneNames`);

  validatePresentationState(
    receiverLabel,
    characterName,
    sceneNames,
    presentationStatus,
    label,
  );
  return {
    receiverLabel,
    characterName,
    sceneNames,
    presentationStatus,
  };
}

function validatePresentationState(
  receiverLabel: string,
  characterName: string,
  sceneNames: readonly string[],
  presentationStatus: string,
  label: string,
) {
  if (presentationStatus === 'no-receiver') {
    if (receiverLabel !== '' || characterName !== '' || sceneNames.length !== 0) {
      throw new Error(`任务响应中的 ${label} 无接收者状态与展示字段不一致。`);
    }
    return;
  }

  if (presentationStatus === 'ready') {
    if (!receiverLabel.trim() || !characterName.trim()) {
      throw new Error(`任务响应中的 ${label} 展示数据完整状态缺少必需字段。`);
    }
    return;
  }

  if (MISSION_PRESENTATION_UNAVAILABLE_STATUSES.has(presentationStatus)) {
    if (!receiverLabel.trim()) {
      throw new Error(`任务响应中的 ${label} 展示数据不可用状态缺少接收者或原因。`);
    }
    return;
  }

  throw new Error(`任务响应中的 ${label}.presentationStatus 无效。`);
}

function parseSceneNames(value: unknown, label: string): string[] {
  if (!Array.isArray(value) || value.length > MAX_MISSION_SCENE_COUNT) {
    throw new Error(`任务响应中的 ${label} 数量或类型无效。`);
  }

  const sceneNames = value.map((sceneName, index) => requireBoundedString(
    sceneName,
    `${label}[${index}]`,
    MAX_MISSION_SCENE_NAME_LENGTH,
    false,
  ));
  if (new Set(sceneNames).size !== sceneNames.length) {
    throw new Error(`任务响应中的 ${label} 包含重复场景。`);
  }
  return sceneNames;
}

function requireBoundedString(
  value: unknown,
  label: string,
  maximumLength: number,
  allowEmpty: boolean,
): string {
  if (typeof value !== 'string'
      || value.length > maximumLength
      || (!allowEmpty && !value.trim())) {
    throw new Error(`任务响应中的 ${label} 为空、过长或类型无效。`);
  }
  return value;
}
