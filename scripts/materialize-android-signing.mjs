import { createHash } from 'node:crypto';
import {
  closeSync,
  existsSync,
  fsyncSync,
  lstatSync,
  openSync,
  rmSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const MAX_BASE64_LENGTH = 48 * 1024;
const MAX_ALIAS_LENGTH = 256;
const MAX_PASSWORD_LENGTH = 4096;
const KEYSTORE_BASE64_ENV = 'MYSTIA_ANDROID_KEYSTORE_BASE64';
const KEYSTORE_SHA256_ENV = 'MYSTIA_ANDROID_KEYSTORE_SHA256';
const KEY_ALIAS_ENV = 'MYSTIA_ANDROID_KEY_ALIAS';
const STORE_PASSWORD_ENV = 'MYSTIA_ANDROID_STORE_PASSWORD';
const KEY_PASSWORD_ENV = 'MYSTIA_ANDROID_KEY_PASSWORD';

if (isMainModule()) {
  try {
    const options = parseArguments(process.argv.slice(2));
    if (options.cleanup) {
      cleanupAndroidSigningMaterial(options);
    } else {
      materializeAndroidSigning(options, process.env);
    }
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}

function isMainModule() {
  return Boolean(process.argv[1])
    && path.resolve(process.argv[1]) === path.resolve(fileURLToPath(import.meta.url));
}

function parseArguments(args) {
  let cleanup = false;
  let keystoreOutput = '';
  let propertiesOutput = '';

  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    if (argument === '--cleanup') {
      cleanup = true;
      continue;
    }
    if (argument === '--keystore-output' || argument === '--properties-output') {
      const value = args[index + 1];
      if (!value || value.startsWith('--')) failUsage();
      if (argument === '--keystore-output') {
        if (keystoreOutput) failUsage();
        keystoreOutput = value;
      } else {
        if (propertiesOutput) failUsage();
        propertiesOutput = value;
      }
      index += 1;
      continue;
    }
    failUsage();
  }

  if (!keystoreOutput || !propertiesOutput) failUsage();
  const resolvedKeystoreOutput = path.resolve(keystoreOutput);
  const resolvedPropertiesOutput = path.resolve(propertiesOutput);
  if (normalizePath(resolvedKeystoreOutput) === normalizePath(resolvedPropertiesOutput)) {
    throw new Error('Android keystore and properties outputs must be different files.');
  }

  return {
    cleanup,
    keystoreOutput: resolvedKeystoreOutput,
    propertiesOutput: resolvedPropertiesOutput,
  };
}

function failUsage() {
  throw new Error(
    'Usage: node scripts/materialize-android-signing.mjs '
      + '--keystore-output <path> --properties-output <path> [--cleanup]',
  );
}

function materializeAndroidSigning(options, environment) {
  assertOutputCanBeCreated(options.keystoreOutput, 'Android keystore');
  assertOutputCanBeCreated(options.propertiesOutput, 'Android signing properties');

  const keystoreBase64 = readRequiredEnvironment(environment, KEYSTORE_BASE64_ENV, MAX_BASE64_LENGTH);
  const expectedKeystoreSha256 = readSha256Environment(environment, KEYSTORE_SHA256_ENV);
  const keyAlias = readRequiredEnvironment(environment, KEY_ALIAS_ENV, MAX_ALIAS_LENGTH);
  const storePassword = readRequiredEnvironment(environment, STORE_PASSWORD_ENV, MAX_PASSWORD_LENGTH);
  const keyPassword = readRequiredEnvironment(environment, KEY_PASSWORD_ENV, MAX_PASSWORD_LENGTH);
  const keystore = decodeCanonicalBase64(keystoreBase64);
  if (keystore.length === 0) {
    throw new Error(`${KEYSTORE_BASE64_ENV} decoded to an empty keystore.`);
  }

  const actualKeystoreSha256 = createHash('sha256').update(keystore).digest('hex');
  if (actualKeystoreSha256 !== expectedKeystoreSha256) {
    throw new Error(`Android keystore SHA-256 does not match ${KEYSTORE_SHA256_ENV}.`);
  }

  const properties = serializeProperties({
    keyAlias,
    storePassword,
    keyPassword,
    storeFile: options.keystoreOutput,
  });

  let keystoreCreated = false;
  let propertiesCreated = false;
  let keystoreDescriptor;
  let propertiesDescriptor;
  try {
    keystoreDescriptor = openSync(options.keystoreOutput, 'wx', 0o600);
    keystoreCreated = true;
    writeFileSync(keystoreDescriptor, keystore);
    fsyncSync(keystoreDescriptor);
    closeSync(keystoreDescriptor);
    keystoreDescriptor = undefined;

    propertiesDescriptor = openSync(options.propertiesOutput, 'wx', 0o600);
    propertiesCreated = true;
    writeFileSync(propertiesDescriptor, properties);
    fsyncSync(propertiesDescriptor);
    closeSync(propertiesDescriptor);
    propertiesDescriptor = undefined;
  } catch (error) {
    for (const descriptor of [propertiesDescriptor, keystoreDescriptor]) {
      if (descriptor === undefined) continue;
      try {
        closeSync(descriptor);
      } catch {
        // Cleanup of the exclusively claimed paths remains the authoritative rollback.
      }
    }
    if (propertiesCreated) rmSync(options.propertiesOutput, { force: true });
    if (keystoreCreated) rmSync(options.keystoreOutput, { force: true });
    throw error;
  }

  console.log('Android signing material was created without exposing secret values.');
}

function cleanupAndroidSigningMaterial(options) {
  const cleanupTargets = [
    inspectCleanupTarget(options.propertiesOutput, 'Android signing properties'),
    inspectCleanupTarget(options.keystoreOutput, 'Android keystore'),
  ];
  for (const target of cleanupTargets) {
    if (target.exists) rmSync(target.filePath);
  }
  console.log('Android signing material was removed.');
}

function assertOutputCanBeCreated(outputPath, label) {
  if (tryLstat(outputPath)) {
    throw new Error(`${label} output already exists; refusing to overwrite it: ${outputPath}`);
  }
  const parent = path.dirname(outputPath);
  if (!existsSync(parent) || !statSync(parent).isDirectory()) {
    throw new Error(`${label} output directory does not exist: ${parent}`);
  }
}

function inspectCleanupTarget(filePath, label) {
  const stats = tryLstat(filePath);
  if (!stats) return { exists: false, filePath };
  if (!stats.isFile() || stats.isSymbolicLink()) {
    throw new Error(`${label} cleanup target must be a regular file: ${filePath}`);
  }
  return { exists: true, filePath };
}

function tryLstat(filePath) {
  try {
    return lstatSync(filePath);
  } catch (error) {
    if (error?.code === 'ENOENT') return undefined;
    throw error;
  }
}

function readRequiredEnvironment(environment, name, maximumLength) {
  const value = environment[name];
  if (typeof value !== 'string' || value.length === 0) {
    throw new Error(`Missing required Android signing environment variable: ${name}`);
  }
  if (value.length > maximumLength) {
    throw new Error(`${name} exceeds its ${maximumLength}-character limit.`);
  }
  if (/[\0\r\n]/u.test(value)) {
    throw new Error(`${name} must not contain NUL or newline characters.`);
  }
  return value;
}

function readSha256Environment(environment, name) {
  const value = readRequiredEnvironment(environment, name, 64);
  if (!/^[a-f0-9]{64}$/u.test(value)) {
    throw new Error(`${name} must contain exactly 64 lowercase hexadecimal characters.`);
  }
  return value;
}

function decodeCanonicalBase64(value) {
  if (value.length % 4 !== 0 || !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(value)) {
    throw new Error(`${KEYSTORE_BASE64_ENV} must be canonical, single-line Base64.`);
  }
  const decoded = Buffer.from(value, 'base64');
  if (decoded.toString('base64') !== value) {
    throw new Error(`${KEYSTORE_BASE64_ENV} is not canonical Base64.`);
  }
  return decoded;
}

function serializeProperties(values) {
  return [
    `keyAlias=${escapeJavaProperty(values.keyAlias)}`,
    `storePassword=${escapeJavaProperty(values.storePassword)}`,
    `keyPassword=${escapeJavaProperty(values.keyPassword)}`,
    `storeFile=${escapeJavaProperty(values.storeFile)}`,
    '',
  ].join('\n');
}

function escapeJavaProperty(value) {
  let escaped = '';
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    const character = value[index];
    switch (character) {
      case '\\': escaped += '\\\\'; break;
      case '\t': escaped += '\\t'; break;
      case '\f': escaped += '\\f'; break;
      case ' ': escaped += '\\ '; break;
      case '=': escaped += '\\='; break;
      case ':': escaped += '\\:'; break;
      case '#': escaped += '\\#'; break;
      case '!': escaped += '\\!'; break;
      default:
        if (code < 0x20 || code > 0x7e) {
          escaped += `\\u${code.toString(16).padStart(4, '0')}`;
        } else {
          escaped += character;
        }
        break;
    }
  }
  return escaped;
}

function normalizePath(value) {
  return process.platform === 'win32' ? value.toLowerCase() : value;
}

export {
  cleanupAndroidSigningMaterial,
  decodeCanonicalBase64,
  escapeJavaProperty,
  materializeAndroidSigning,
  parseArguments,
  serializeProperties,
};
