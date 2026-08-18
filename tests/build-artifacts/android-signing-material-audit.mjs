import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {
  cleanupAndroidSigningMaterial,
  decodeCanonicalBase64,
  escapeJavaProperty,
  materializeAndroidSigning,
  parseArguments,
  serializeProperties,
} from '../../scripts/materialize-android-signing.mjs';
import {
  assertSigningProperties,
  parseProperties,
} from '../../scripts/build-android-signed-apk.mjs';

const fixtureRoot = mkdtempSync(path.join(os.tmpdir(), 'mystia-android-signing-audit-'));
const androidGradle = readFileSync(
  new URL('../../apps/companion/src-tauri/gen/android/app/build.gradle.kts', import.meta.url),
  'utf8',
);

try {
  auditGradleSigningContract();
  auditArgumentContract();
  auditCanonicalBase64();
  auditPropertyEscaping();
  auditCanonicalSigningProperties();
  auditMaterializeAndCleanup();
  auditHashMismatchDoesNotWrite();
  auditExistingOutputIsNotOverwritten();
  auditCleanupRejectsSymlink();
  auditCleanupIsPreflightTransactional();
  console.log('Android signing material audit passed.');
} finally {
  rmSync(fixtureRoot, { recursive: true, force: true });
}

function auditGradleSigningContract() {
  assert.match(androidGradle, /setOf\("keyAlias", "storePassword", "keyPassword", "storeFile"\)/u);
  assert.match(androidGradle, /Unsupported Android signing properties/u);
  assert.match(
    androidGradle,
    /releaseKeystorePropertiesFile\.inputStream\(\)\.use \{ load\(it\) \}/u,
  );
  assert.doesNotMatch(androidGradle, /FileInputStream/u);
  assert.doesNotMatch(androidGradle, /getProperty\("password"\)/u);
}

function auditArgumentContract() {
  const parsed = parseArguments([
    '--keystore-output',
    'fixture-release.jks',
    '--properties-output',
    'fixture-keystore.properties',
  ]);
  assert.equal(parsed.cleanup, false);
  assert.equal(parsed.keystoreOutput, path.resolve('fixture-release.jks'));
  assert.equal(parsed.propertiesOutput, path.resolve('fixture-keystore.properties'));
  assert.equal(
    parseArguments([
      '--cleanup',
      '--keystore-output',
      'fixture-release.jks',
      '--properties-output',
      'fixture-keystore.properties',
    ]).cleanup,
    true,
  );
  assert.throws(() => parseArguments(['--cleanup']), /Usage:/u);
  assert.throws(
    () => parseArguments([
      '--keystore-output', 'same-file', '--properties-output', 'same-file',
    ]),
    /must be different files/u,
  );
}

function auditCanonicalBase64() {
  const bytes = Buffer.from([0, 1, 2, 253, 254, 255]);
  assert.deepEqual(decodeCanonicalBase64(bytes.toString('base64')), bytes);
  assert.throws(() => decodeCanonicalBase64('YWJj\n'), /canonical, single-line Base64/u);
  assert.throws(() => decodeCanonicalBase64('YWJj='), /canonical, single-line Base64/u);
  assert.throws(() => decodeCanonicalBase64('!!!!'), /canonical, single-line Base64/u);
}

function auditPropertyEscaping() {
  assert.equal(escapeJavaProperty('a b:c=d#e!f\\g'), 'a\\ b\\:c\\=d\\#e\\!f\\\\g');
  assert.equal(escapeJavaProperty('夜雀'), '\\u591c\\u96c0');
  assert.equal(
    serializeProperties({
      keyAlias: 'mystia alias',
      storePassword: 'store:=',
      keyPassword: 'key#!',
      storeFile: 'C:\\runner temp\\release.jks',
    }),
    [
      'keyAlias=mystia\\ alias',
      'storePassword=store\\:\\=',
      'keyPassword=key\\#\\!',
      'storeFile=C\\:\\\\runner\\ temp\\\\release.jks',
      '',
    ].join('\n'),
  );
  assert.deepEqual(
    parseProperties(serializeProperties({
      keyAlias: '夜雀 alias',
      storePassword: ' store:= password ',
      keyPassword: ' key#! password ',
      storeFile: 'C:\\runner temp\\release.jks',
    })),
    {
      keyAlias: '夜雀 alias',
      storePassword: ' store:= password ',
      keyPassword: ' key#! password ',
      storeFile: 'C:\\runner temp\\release.jks',
    },
  );
  assert.equal(parseProperties('storePassword=  literal trailing  \n').storePassword, 'literal trailing  ');
}

function auditCanonicalSigningProperties() {
  const canonical = {
    keyAlias: 'mystia-steward-companion',
    storePassword: 'same-password',
    keyPassword: 'same-password',
    storeFile: 'C:\\release\\mystia.jks',
  };
  assert.doesNotThrow(() => assertSigningProperties(canonical, 'fixture'));
  assert.throws(
    () => assertSigningProperties({
      keyAlias: canonical.keyAlias,
      password: 'legacy-password',
      storeFile: canonical.storeFile,
    }, 'fixture'),
    /Unsupported Android signing properties.*password/u,
  );
  assert.throws(
    () => assertSigningProperties({ ...canonical, keyPassword: '' }, 'fixture'),
    /Missing Android signing properties.*keyPassword/u,
  );
  assert.throws(
    () => parseProperties('keyAlias=first\nkeyAlias=second\n'),
    /Android signing property is duplicated: keyAlias/u,
  );
}

function auditMaterializeAndCleanup() {
  const fixture = createOutputFixture('success');
  const keystore = Buffer.from('fixture-keystore-binary\0content', 'utf8');
  const environment = createEnvironment(keystore);

  materializeAndroidSigning(fixture, environment);
  assert.deepEqual(readFileSync(fixture.keystoreOutput), keystore);
  const properties = readFileSync(fixture.propertiesOutput, 'utf8');
  assert.match(properties, /^keyAlias=mystia\\ alias$/mu);
  assert.match(properties, /^storePassword=store\\:\\=password$/mu);
  assert.match(properties, /^keyPassword=key\\#\\!password$/mu);
  assert.match(
    properties,
    new RegExp(`^storeFile=${escapeRegex(escapeJavaProperty(fixture.keystoreOutput))}$`, 'mu'),
  );
  if (process.platform !== 'win32') {
    assert.equal(lstatSync(fixture.keystoreOutput).mode & 0o777, 0o600);
    assert.equal(lstatSync(fixture.propertiesOutput).mode & 0o777, 0o600);
  }

  cleanupAndroidSigningMaterial({ ...fixture, cleanup: true });
  assert.equal(lstatExists(fixture.keystoreOutput), false);
  assert.equal(lstatExists(fixture.propertiesOutput), false);
  assert.doesNotThrow(() => cleanupAndroidSigningMaterial({ ...fixture, cleanup: true }));
}

function auditHashMismatchDoesNotWrite() {
  const fixture = createOutputFixture('hash-mismatch');
  const environment = createEnvironment(Buffer.from('wrong-key'));
  environment.MYSTIA_ANDROID_KEYSTORE_SHA256 = '0'.repeat(64);
  let mismatch;
  try {
    materializeAndroidSigning(fixture, environment);
    assert.fail('Expected the mismatched Android keystore hash to be rejected.');
  } catch (error) {
    mismatch = error;
  }
  assert.match(mismatch.message, /Android keystore SHA-256 does not match MYSTIA_ANDROID_KEYSTORE_SHA256/u);
  assert.doesNotMatch(mismatch.message, /0{64}/u);
  assert.doesNotMatch(
    mismatch.message,
    new RegExp(createHash('sha256').update(Buffer.from('wrong-key')).digest('hex'), 'u'),
  );
  assert.equal(lstatExists(fixture.keystoreOutput), false);
  assert.equal(lstatExists(fixture.propertiesOutput), false);
}

function auditExistingOutputIsNotOverwritten() {
  const fixture = createOutputFixture('existing-output');
  writeFileSync(fixture.propertiesOutput, 'local-owner');
  assert.throws(
    () => materializeAndroidSigning(fixture, createEnvironment(Buffer.from('fixture-key'))),
    /refusing to overwrite/u,
  );
  assert.equal(readFileSync(fixture.propertiesOutput, 'utf8'), 'local-owner');
  assert.equal(lstatExists(fixture.keystoreOutput), false);
}

function auditCleanupRejectsSymlink() {
  const fixture = createOutputFixture('cleanup-symlink');
  const target = path.join(path.dirname(fixture.keystoreOutput), 'target.jks');
  writeFileSync(target, 'do-not-delete');
  symlinkSync(target, fixture.keystoreOutput, 'file');
  assert.throws(
    () => cleanupAndroidSigningMaterial({ ...fixture, cleanup: true }),
    /cleanup target must be a regular file/u,
  );
  assert.equal(readFileSync(target, 'utf8'), 'do-not-delete');
}

function auditCleanupIsPreflightTransactional() {
  const fixture = createOutputFixture('cleanup-preflight');
  const target = path.join(path.dirname(fixture.keystoreOutput), 'target.jks');
  writeFileSync(fixture.propertiesOutput, 'generated-properties');
  writeFileSync(target, 'do-not-delete');
  symlinkSync(target, fixture.keystoreOutput, 'file');
  assert.throws(
    () => cleanupAndroidSigningMaterial({ ...fixture, cleanup: true }),
    /cleanup target must be a regular file/u,
  );
  assert.equal(readFileSync(fixture.propertiesOutput, 'utf8'), 'generated-properties');
  assert.equal(readFileSync(target, 'utf8'), 'do-not-delete');
}

function createOutputFixture(name) {
  const outputDirectory = path.join(fixtureRoot, name, 'signing files');
  mkdirSync(outputDirectory, { recursive: true });
  return {
    cleanup: false,
    keystoreOutput: path.join(outputDirectory, 'mystia-release.jks'),
    propertiesOutput: path.join(outputDirectory, 'keystore.properties'),
  };
}

function createEnvironment(keystore) {
  return {
    MYSTIA_ANDROID_KEYSTORE_BASE64: keystore.toString('base64'),
    MYSTIA_ANDROID_KEYSTORE_SHA256: createHash('sha256').update(keystore).digest('hex'),
    MYSTIA_ANDROID_KEY_ALIAS: 'mystia alias',
    MYSTIA_ANDROID_STORE_PASSWORD: 'store:=password',
    MYSTIA_ANDROID_KEY_PASSWORD: 'key#!password',
  };
}

function lstatExists(candidatePath) {
  try {
    lstatSync(candidatePath);
    return true;
  } catch (error) {
    if (error.code === 'ENOENT') return false;
    throw error;
  }
}

function escapeRegex(value) {
  return value.replace(/[.*+?^${}()|[\]\\]/gu, '\\$&');
}
