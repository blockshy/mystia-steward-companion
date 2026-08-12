#!/usr/bin/env node

import { readFile, writeFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';

export const UPDATE_CATALOG_SCHEMA_VERSION = 1;
export const MAX_UPDATE_CATALOG_RELEASES = 256;
export const MAX_RELEASE_NOTES_BYTES = 64 * 1024;
export const MAX_UPDATE_CATALOG_BYTES = 2 * 1024 * 1024;
const MAX_VERSION_COMPONENT = 2_147_483_647;

const REPOSITORY_PATTERN = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u;
const STABLE_VERSION_PATTERN = /^(?:v)?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/u;
const PREVIEW_VERSION_PATTERN = /^(?:v)?(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)-preview\.([1-9]\d*)$/u;

export function parseUpdateVersion(value) {
  if (typeof value !== 'string') return null;
  const normalized = value.trim();
  let match = STABLE_VERSION_PATTERN.exec(normalized);
  if (match) {
    const numericParts = match.slice(1, 4).map(Number);
    if (numericParts.some((part) => !Number.isSafeInteger(part) || part > MAX_VERSION_COMPONENT)) return null;
    return {
      version: `${match[1]}.${match[2]}.${match[3]}`,
      channel: 'stable',
      major: numericParts[0],
      minor: numericParts[1],
      patch: numericParts[2],
      preview: null,
    };
  }

  match = PREVIEW_VERSION_PATTERN.exec(normalized);
  if (!match) return null;
  const numericParts = match.slice(1, 5).map(Number);
  if (numericParts.some((part) => !Number.isSafeInteger(part))
    || numericParts.slice(0, 3).some((part) => part > MAX_VERSION_COMPONENT)) return null;
  return {
    version: `${match[1]}.${match[2]}.${match[3]}-preview.${match[4]}`,
    channel: 'preview',
    major: numericParts[0],
    minor: numericParts[1],
    patch: numericParts[2],
    preview: numericParts[3],
  };
}

export function compareUpdateVersions(left, right) {
  for (const key of ['major', 'minor', 'patch']) {
    if (left[key] !== right[key]) return left[key] - right[key];
  }
  if (left.preview === null && right.preview === null) return 0;
  if (left.preview === null) return 1;
  if (right.preview === null) return -1;
  return left.preview - right.preview;
}

function utf8Length(value) {
  return Buffer.byteLength(value, 'utf8');
}

function requireText(value, label, maxBytes) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`${label} must not be empty.`);
  }
  if (utf8Length(value) > maxBytes) {
    throw new Error(`${label} exceeds ${maxBytes} UTF-8 bytes.`);
  }
  return value;
}

function normalizePublishedAt(value, label) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    throw new Error(`${label} is missing published_at.`);
  }
  const parsed = new Date(value);
  if (!Number.isFinite(parsed.getTime())) {
    throw new Error(`${label} has an invalid published_at value: ${value}`);
  }
  return parsed.toISOString();
}

function flattenReleasePages(input) {
  if (!Array.isArray(input)) throw new Error('GitHub releases JSON must be an array.');
  if (input.length === 0) return [];
  if (input.every((entry) => Array.isArray(entry))) return input.flat();
  if (input.some((entry) => Array.isArray(entry))) {
    throw new Error('GitHub releases JSON mixes paginated arrays and release objects.');
  }
  return input;
}

function normalizeRelease(rawRelease, repository) {
  if (!rawRelease || typeof rawRelease !== 'object' || Array.isArray(rawRelease)) {
    throw new Error('GitHub releases JSON contains a non-object entry.');
  }
  if (rawRelease.draft === true) return null;

  const tag = typeof rawRelease.tag_name === 'string' ? rawRelease.tag_name.trim() : '';
  const parsedVersion = parseUpdateVersion(tag);
  if (!parsedVersion) return null;
  if (tag !== `v${parsedVersion.version}`) return null;

  const isPrerelease = rawRelease.prerelease === true;
  if ((parsedVersion.channel === 'preview') !== isPrerelease) {
    throw new Error(`Release ${tag} channel does not match its GitHub prerelease flag.`);
  }

  const titleCandidate = typeof rawRelease.name === 'string' && rawRelease.name.trim().length > 0
    ? rawRelease.name.trim()
    : tag;
  const notesMarkdown = requireText(
    typeof rawRelease.body === 'string' ? rawRelease.body : '',
    `Release ${tag} notes`,
    MAX_RELEASE_NOTES_BYTES,
  );
  if (utf8Length(titleCandidate) > 512) throw new Error(`Release ${tag} title is too large.`);
  if (utf8Length(notesMarkdown) > MAX_RELEASE_NOTES_BYTES) {
    throw new Error(`Release ${tag} notes exceed ${MAX_RELEASE_NOTES_BYTES} UTF-8 bytes.`);
  }

  return {
    version: parsedVersion.version,
    tag,
    title: titleCandidate,
    channel: parsedVersion.channel,
    publishedAtUtc: normalizePublishedAt(rawRelease.published_at, `Release ${tag}`),
    releaseUrl: `https://github.com/${repository}/releases/tag/${encodeURIComponent(tag)}`,
    notesMarkdown,
    parsedVersion,
  };
}

export function buildUpdateCatalog({
  githubReleases,
  repository,
  ownerTag,
  ownerVersion,
  ownerChannel,
  ownerTitle,
  ownerNotes,
  ownerPublishedAtUtc,
  generatedAtUtc,
}) {
  if (typeof repository !== 'string' || !REPOSITORY_PATTERN.test(repository)) {
    throw new Error(`Invalid repository slug: ${repository}`);
  }

  const parsedOwnerTag = parseUpdateVersion(ownerTag);
  const parsedOwnerVersion = parseUpdateVersion(ownerVersion);
  if (!parsedOwnerTag || !parsedOwnerVersion || compareUpdateVersions(parsedOwnerTag, parsedOwnerVersion) !== 0) {
    throw new Error(`Owner version and tag do not match: ${ownerVersion} / ${ownerTag}`);
  }
  if (parsedOwnerVersion.channel !== ownerChannel) {
    throw new Error(`Owner channel does not match its version: ${ownerChannel} / ${ownerVersion}`);
  }
  if (ownerTag !== `v${parsedOwnerVersion.version}`) {
    throw new Error(`Owner tag must use the canonical v-prefixed form: v${parsedOwnerVersion.version}`);
  }

  const currentTitle = requireText(ownerTitle, 'Current release title', 512).trim();
  const currentNotes = requireText(ownerNotes, 'Current release notes', MAX_RELEASE_NOTES_BYTES);
  const currentPublishedAt = normalizePublishedAt(ownerPublishedAtUtc, `Release ${ownerTag}`);
  const generatedAt = normalizePublishedAt(generatedAtUtc, 'Catalog');
  const normalizedReleases = flattenReleasePages(githubReleases)
    .map((release) => normalizeRelease(release, repository))
    .filter((release) => release && compareUpdateVersions(release.parsedVersion, parsedOwnerVersion) <= 0);

  const byVersion = new Map();
  const tags = new Set();
  for (const release of normalizedReleases) {
    if (release.tag === ownerTag || release.version === parsedOwnerVersion.version) continue;
    if (tags.has(release.tag)) throw new Error(`Duplicate Release tag: ${release.tag}`);
    if (byVersion.has(release.version)) throw new Error(`Duplicate Release version: ${release.version}`);
    tags.add(release.tag);
    byVersion.set(release.version, release);
  }

  const ownerRelease = {
    version: parsedOwnerVersion.version,
    tag: ownerTag,
    title: currentTitle,
    channel: ownerChannel,
    publishedAtUtc: currentPublishedAt,
    releaseUrl: `https://github.com/${repository}/releases/tag/${encodeURIComponent(ownerTag)}`,
    notesMarkdown: currentNotes,
    parsedVersion: parsedOwnerVersion,
  };
  tags.add(ownerTag);
  byVersion.set(ownerRelease.version, ownerRelease);

  const releases = [...byVersion.values()]
    .sort((left, right) => compareUpdateVersions(left.parsedVersion, right.parsedVersion))
    .map(({ parsedVersion: _parsedVersion, ...release }) => release);
  if (releases.length === 0 || releases.length > MAX_UPDATE_CATALOG_RELEASES) {
    throw new Error(`Catalog release count must be between 1 and ${MAX_UPDATE_CATALOG_RELEASES}.`);
  }

  const catalog = {
    schemaVersion: UPDATE_CATALOG_SCHEMA_VERSION,
    generatedAtUtc: generatedAt,
    repository,
    ownerVersion: parsedOwnerVersion.version,
    ownerTag,
    releases,
  };
  const serialized = `${JSON.stringify(catalog, null, 2)}\n`;
  if (utf8Length(serialized) > MAX_UPDATE_CATALOG_BYTES) {
    throw new Error(`Catalog exceeds ${MAX_UPDATE_CATALOG_BYTES} UTF-8 bytes.`);
  }
  return { catalog, serialized };
}

function parseArguments(argv) {
  const values = new Map();
  for (let index = 0; index < argv.length; index += 2) {
    const key = argv[index];
    const value = argv[index + 1];
    if (!key?.startsWith('--') || value === undefined) {
      throw new Error(`Invalid argument sequence near: ${key ?? '<end>'}`);
    }
    if (values.has(key)) throw new Error(`Duplicate argument: ${key}`);
    values.set(key, value);
  }
  return values;
}

function requiredArgument(argumentsMap, name) {
  const value = argumentsMap.get(name);
  if (!value) throw new Error(`Missing required argument: ${name}`);
  return value;
}

async function main() {
  const args = parseArguments(process.argv.slice(2));
  const inputPath = requiredArgument(args, '--input');
  const outputPath = requiredArgument(args, '--output');
  const titlePath = requiredArgument(args, '--title-file');
  const notesPath = requiredArgument(args, '--notes-file');
  const [inputJson, ownerTitle, ownerNotes] = await Promise.all([
    readFile(inputPath, 'utf8'),
    readFile(titlePath, 'utf8'),
    readFile(notesPath, 'utf8'),
  ]);
  const githubReleases = JSON.parse(inputJson);
  const { serialized } = buildUpdateCatalog({
    githubReleases,
    repository: requiredArgument(args, '--repository'),
    ownerTag: requiredArgument(args, '--tag'),
    ownerVersion: requiredArgument(args, '--version'),
    ownerChannel: requiredArgument(args, '--channel'),
    ownerTitle,
    ownerNotes,
    ownerPublishedAtUtc: requiredArgument(args, '--published-at'),
    generatedAtUtc: requiredArgument(args, '--generated-at'),
  });
  await writeFile(outputPath, serialized, 'utf8');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  main().catch((error) => {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  });
}
