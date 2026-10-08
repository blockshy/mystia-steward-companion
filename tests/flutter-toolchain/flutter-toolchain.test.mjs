import assert from 'node:assert/strict';
import './flutter-windows-toolchain.test.mjs';
import { createHash } from 'node:crypto';
import {
  chmodSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  readdirSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import test from 'node:test';
import {
  assertFlutterVersion,
  checkInstalledFlutter,
  flutterPlatformKey,
  readFlutterLock,
  resolveFlutterCommand,
  runFlutterTool,
  validateFlutterLock,
} from '../../scripts/flutter-toolchain.mjs';
import {
  assertInstallDestination,
  installLockedFlutter,
  validateFlutterArchiveListing,
  verifyFlutterArchive,
} from '../../scripts/install-locked-flutter.mjs';

const fixtureBytes = Buffer.from('A local archive fixture; never downloaded or executed.\n');
const digest = createHash('sha256').update(fixtureBytes).digest('hex');

function fixtureLock() {
  return {
    version: '3.47.6',
    channel: 'stable',
    frameworkRevision: 'a'.repeat(40),
    engineRevision: 'b'.repeat(40),
    dartVersion: '3.13.5',
    archives: {
      'linux-x64': {
        url: 'https://storage.googleapis.com/flutter_infra_release/releases/stable/linux/flutter_linux_3.47.6-stable.tar.xz',
        size: fixtureBytes.length,
        sha256: digest,
      },
      'win32-x64': {
        url: 'https://storage.googleapis.com/flutter_infra_release/releases/stable/windows/flutter_windows_3.47.6-stable.zip',
        size: fixtureBytes.length,
        sha256: digest,
      },
    },
  };
}

function fixtureReport(lock = fixtureLock()) {
  return {
    frameworkVersion: lock.version,
    channel: lock.channel,
    frameworkRevision: lock.frameworkRevision,
    engineRevision: lock.engineRevision,
    dartSdkVersion: lock.dartVersion,
    flutterVersion: lock.version,
  };
}

function temporaryDirectory(t) {
  const root = mkdtempSync(path.join(os.tmpdir(), 'mystia-flutter-test-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  return root;
}

function writeFakeSdk(root, report = fixtureReport(), {
  output = JSON.stringify(report),
  bootstrapExitCode = 0,
} = {}) {
  mkdirSync(path.join(root, 'bin', 'cache', 'dart-sdk'), { recursive: true });
  writeFileSync(path.join(root, 'bin', 'cache', 'dart-sdk', 'version'), fixtureLock().dartVersion);
  writeFileSync(path.join(root, 'bin', 'cache', 'flutter_tools.snapshot'), 'fixture');
  const executable = path.join(root, 'bin', process.platform === 'win32' ? 'flutter.bat' : 'flutter');
  const script = `const fs = require('node:fs');
const path = require('node:path');
const args = process.argv.slice(2).join(' ');
const initialized = path.join(__dirname, 'fixture-initialized');
fs.appendFileSync(path.join(__dirname, 'fixture-invocations'), args + '\\n');
if (args === '--version' && process.platform === 'win32') {
  process.stdout.write('Building flutter tool...\\r\\nResolving dependencies...\\r\\n');
  if (${bootstrapExitCode}) process.exit(${bootstrapExitCode});
  fs.writeFileSync(initialized, 'initialized');
} else if (args === '--version --machine') {
  if (process.platform === 'win32' && !fs.existsSync(initialized)) {
    process.stdout.write('Unexpected bootstrap output before JSON\\r\\n');
  }
  process.stdout.write(${JSON.stringify(output)});
} else {
  process.exit(9);
}
`;
  if (process.platform === 'win32') {
    writeFileSync(path.join(root, 'bin', 'flutter-fixture.cjs'), script);
    writeFileSync(executable, `@echo off\r\n"${process.execPath}" "%~dp0flutter-fixture.cjs" %*\r\nexit /b %errorlevel%\r\n`);
  } else {
    writeFileSync(executable, `#!${process.execPath}\n${script}`);
  }
  chmodSync(executable, 0o700);
}

test('the Flutter lock requires exact versions, revisions and both official archives', () => {
  const valid = fixtureLock();
  assert.equal(validateFlutterLock(valid), valid);
  for (const field of Object.keys(valid)) {
    const lock = structuredClone(valid);
    delete lock[field];
    assert.throws(() => validateFlutterLock(lock), /incomplete or unexpected schema/u);
  }
  for (const version of ['stable', 'latest', '3.47', '^3.47.6', '3.47.6-beta.1']) {
    assert.throws(() => validateFlutterLock({ ...valid, version }), /exact stable/u);
  }
  for (const revision of ['abcdef12', 'A'.repeat(40), 'g'.repeat(40)]) {
    assert.throws(() => validateFlutterLock({ ...valid, frameworkRevision: revision }), /complete lowercase/u);
  }
  for (const key of ['linux-x64', 'win32-x64']) {
    for (const field of ['url', 'size', 'sha256']) {
      const lock = structuredClone(valid);
      delete lock.archives[key][field];
      assert.throws(() => validateFlutterLock(lock), /incomplete or unexpected schema/u);
    }
    for (const replacement of [
      { url: `${valid.archives[key].url}?version=latest` },
      { url: valid.archives[key].url.replace('storage.googleapis.com', 'example.com') },
      { size: 0 },
      { size: Number.MAX_SAFE_INTEGER + 1 },
      { sha256: 'a'.repeat(63) },
    ]) {
      const lock = structuredClone(valid);
      Object.assign(lock.archives[key], replacement);
      assert.throws(() => validateFlutterLock(lock));
    }
  }
  assert.throws(() => flutterPlatformKey('linux', 'arm64'), /does not support/u);
  assert.throws(() => flutterPlatformKey('darwin', 'x64'), /does not support/u);
});

test('lock reading rejects missing Flutter policy instead of using a fallback', (t) => {
  const root = temporaryDirectory(t);
  const lockFile = path.join(root, 'toolchain.lock.json');
  writeFileSync(lockFile, JSON.stringify({ node: process.versions.node }));
  assert.throws(() => readFlutterLock(lockFile), /incomplete/u);
  writeFileSync(lockFile, JSON.stringify({ flutter: fixtureLock() }));
  assert.equal(readFlutterLock(lockFile).version, '3.47.6');
});

test('all installed machine-version fields are exact, including the complete engine revision', () => {
  const lock = fixtureLock();
  assert.deepEqual(assertFlutterVersion(fixtureReport(lock), lock), fixtureReport(lock));
  for (const field of Object.keys(fixtureReport(lock))) {
    const changed = { ...fixtureReport(lock), [field]: 'different' };
    assert.throws(() => assertFlutterVersion(changed, lock), /Installed Flutter/u);
  }
  assert.throws(() => assertFlutterVersion({ ...fixtureReport(lock), engineRevision: 'bbbbbbbbbb' }, lock));
  assert.throws(() => assertFlutterVersion(null, lock), /JSON object/u);
});

test('archive verification checks byte length and SHA-256 without deleting caller files', async (t) => {
  const root = temporaryDirectory(t);
  const archive = path.join(root, 'archive');
  writeFileSync(archive, fixtureBytes);
  const record = fixtureLock().archives['linux-x64'];
  await verifyFlutterArchive(archive, record);
  await assert.rejects(verifyFlutterArchive(archive, { ...record, size: record.size + 1 }), /size does not match/u);
  await assert.rejects(verifyFlutterArchive(archive, { ...record, sha256: '0'.repeat(64) }), /SHA-256/u);
  assert.deepEqual(readFileSync(archive), fixtureBytes);
});

test('existing destination is rejected before download and its contents are retained', async (t) => {
  const root = temporaryDirectory(t);
  const destination = path.join(root, 'existing');
  mkdirSync(destination);
  writeFileSync(path.join(destination, 'user-file'), 'preserve');
  let downloadCalled = false;
  await assert.rejects(installLockedFlutter(destination, {
    lock: fixtureLock(),
    download: async () => { downloadCalled = true; },
  }), /already exists/u);
  assert.equal(downloadCalled, false);
  assert.equal(readFileSync(path.join(destination, 'user-file'), 'utf8'), 'preserve');
  assert.deepEqual(readdirSync(root), ['existing']);
});

test('hash failure cleans only the owned incomplete installation, including offline mode', async (t) => {
  const root = temporaryDirectory(t);
  const archive = path.join(root, 'offline-archive');
  const corruptedBytes = Buffer.from(fixtureBytes);
  corruptedBytes[0] ^= 1;
  writeFileSync(archive, corruptedBytes);
  const destination = path.join(root, 'new-sdk');
  await assert.rejects(installLockedFlutter(destination, {
    lock: fixtureLock(),
    archive,
    download: async () => assert.fail('Offline installs must not download a fallback.'),
  }), /SHA-256/u);
  assert.equal(existsSync(destination), false);
  assert.deepEqual(readdirSync(root), ['offline-archive']);
  assert.deepEqual(readFileSync(archive), corruptedBytes);
});

test('incomplete lock cannot create a directory or start a download', async (t) => {
  const root = temporaryDirectory(t);
  await assert.rejects(installLockedFlutter(path.join(root, 'sdk'), {
    lock: { version: 'latest' },
    download: async () => assert.fail('An incomplete lock cannot start a download.'),
  }), /incomplete/u);
  assert.deepEqual(readdirSync(root), []);
});

test('destination and its ancestors cannot be symbolic links or junctions', (t) => {
  const root = temporaryDirectory(t);
  const real = path.join(root, 'real');
  const linked = path.join(root, 'linked');
  mkdirSync(real);
  symlinkSync(real, linked, process.platform === 'win32' ? 'junction' : 'dir');
  assert.throws(() => assertInstallDestination(linked), /already exists/u);
  assert.throws(() => assertInstallDestination(path.join(linked, 'sdk')), /symbolic links/u);
  assert.throws(() => assertInstallDestination(path.parse(root).root), /filesystem root/u);
});

test('archive inspection rejects link cycles, traversal, alternate roots and Windows path aliases', () => {
  assert.doesNotThrow(() => validateFlutterArchiveListing(
    'flutter/\nflutter/bin/\nflutter/bin/flutter\n',
    'drwxr-xr-x root/root 0 flutter/\ndrwxr-xr-x root/root 0 flutter/bin/\n-rwxr-xr-x root/root 1 flutter/bin/flutter\n',
  ));
  for (const name of ['other/bin/flutter', 'flutter/../outside', 'flutter/./file', 'flutter/C:drive', 'flutter/a\\b']) {
    assert.throws(() => validateFlutterArchiveListing(`${name}\n`, `-rw-r--r-- ${name}\n`), /unsafe/u);
  }
  for (const type of ['h', 'c', 'b', 'p']) {
    assert.throws(() => validateFlutterArchiveListing('flutter/link\n', `${type}rwxrwxrwx flutter/link\n`), /unsafe/u);
  }
  assert.throws(() => validateFlutterArchiveListing(
    'flutter/\nflutter/a\nflutter/b\n',
    'drwxr-xr-x flutter/\nlrwxrwxrwx flutter/a -> b\nlrwxrwxrwx flutter/b -> a\n',
  ), /cycle/u);
  assert.throws(() => validateFlutterArchiveListing(
    'flutter/\nflutter/link\n', 'drwxr-xr-x flutter/\nlrwxrwxrwx flutter/link -> ../../outside\n',
  ), /escapes/u);
  assert.throws(() => validateFlutterArchiveListing(
    'flutter/\nflutter/a/\nflutter/b\nflutter/b/child\n',
    'drwxr-xr-x flutter/\ndrwxr-xr-x flutter/a/\nlrwxrwxrwx flutter/b -> a\n-rwxr-xr-x flutter/b/child\n',
  ), /ancestor/u);
  assert.throws(() => validateFlutterArchiveListing(
    'flutter/\nflutter/a/\nflutter/b/\nflutter/a/link\nflutter/b/link\n',
    'drwxr-xr-x flutter/\ndrwxr-xr-x flutter/a/\ndrwxr-xr-x flutter/b/\nlrwxrwxrwx flutter/a/link -> ../b\nlrwxrwxrwx flutter/b/link -> ../a\n',
  ), /cycle/u);
});

test('Windows tar CRLF listings preserve path and link-target control-character checks', () => {
  const names = ['flutter/', 'flutter/bin/', 'flutter/bin/flutter.bat', 'flutter/bin/alias'];
  const details = [
    'drwxr-xr-x  0 0  0  0 Oct  7 12:00 flutter/',
    'drwxr-xr-x  0 0  0  0 Oct  7 12:00 flutter/bin/',
    '-rw-r--r--  0 0  0  1 Oct  7 12:00 flutter/bin/flutter.bat',
    'lrwxrwxrwx  0 0  0  0 Oct  7 12:00 flutter/bin/alias -> flutter.bat',
  ];
  for (const separator of ['\n', '\r\n']) {
    for (const trailingSeparator of ['', separator]) {
      assert.doesNotThrow(() => validateFlutterArchiveListing(
        names.join(separator) + trailingSeparator,
        details.join(separator) + trailingSeparator,
      ));
    }
    for (const unsafeName of ['flutter/embedded\rname', 'flutter/trailing\t', 'flutter/nul\0name', 'flutter/bare\r']) {
      // No line separator follows the final entry: a bare CR is not CRLF.
      assert.throws(() => validateFlutterArchiveListing(
        `flutter/${separator}${unsafeName}`,
        `${details[0]}${separator}-rw-r--r-- ${unsafeName}`,
      ), (error) => {
        assert.match(error.message, /unsafe path/u);
        assert.ok(error.message.includes(JSON.stringify(unsafeName)));
        assert.ok(error.message.includes('type "-"'));
        assert.ok(error.message.includes(`metadata ${JSON.stringify(`-rw-r--r-- ${unsafeName}`)}`));
        return true;
      });
    }
    assert.throws(() => validateFlutterArchiveListing(
      names.join(separator) + separator,
      details.slice(0, -1).concat('lrwxrwxrwx flutter/bin/alias -> flutter.\rbat').join(separator) + separator,
    ), /unsafe symbolic link target/u);
  }
  assert.throws(() => validateFlutterArchiveListing(
    'flutter/\r\r\n', `${details[0]}\r\n`,
  ), /"flutter\/\\r"/u);
  assert.throws(() => validateFlutterArchiveListing('', ''), /incomplete/u);
});

test('Windows invocation uses the requested SDK, quotes spaces and rejects command expansion', () => {
  const options = { platform: 'win32', systemRoot: 'C:\\Windows' };
  const invocation = resolveFlutterCommand('C:\\Flutter SDK', ['--version', '--machine'], options);
  assert.equal(invocation.command, 'C:\\Windows\\System32\\cmd.exe');
  assert.deepEqual(invocation.args, ['/d', '/s', '/c', '""C:\\Flutter SDK\\bin\\flutter.bat" "--version" "--machine""']);
  assert.equal(invocation.windowsVerbatimArguments, true);
  for (const root of ['C:\\SDK%TEMP%', 'C:\\SDK&whoami', 'C:\\SDK!name']) {
    assert.throws(() => resolveFlutterCommand(root, ['--version'], options), /metacharacters/u);
  }
  assert.throws(() => resolveFlutterCommand('C:\\SDK', ['--dart-define=%SECRET%'], options), /metacharacters/u);
});

test('installed SDK check bootstraps Windows separately, requires pure JSON and detects drift', (t) => {
  const root = temporaryDirectory(t);
  const sdk = path.join(root, 'sdk with spaces');
  writeFakeSdk(sdk);
  assert.equal(checkInstalledFlutter(sdk, fixtureLock()).frameworkVersion, '3.47.6');
  assert.equal(readFileSync(path.join(sdk, 'bin', 'fixture-invocations'), 'utf8'),
    process.platform === 'win32' ? '--version\n--version --machine\n' : '--version --machine\n');
  writeFakeSdk(sdk, { ...fixtureReport(), engineRevision: 'c'.repeat(40) });
  assert.throws(() => checkInstalledFlutter(sdk, fixtureLock()), /engineRevision/u);
  const mixedOutput = `Building flutter tool...\r\n${JSON.stringify(fixtureReport())}${' '.repeat(900)}`;
  writeFakeSdk(sdk, fixtureReport(), { output: mixedOutput });
  assert.throws(() => checkInstalledFlutter(sdk, fixtureLock()), (error) => {
    assert.equal(error.message, `Flutter --version --machine returned invalid JSON; stdout prefix: ${JSON.stringify(mixedOutput.slice(0, 800))}`);
    return true;
  });
  if (process.platform === 'win32') {
    writeFakeSdk(sdk, fixtureReport(), { bootstrapExitCode: 23 });
    writeFileSync(path.join(sdk, 'bin', 'fixture-invocations'), '');
    assert.throws(() => checkInstalledFlutter(sdk, fixtureLock()), /exited with 23/u);
    assert.equal(readFileSync(path.join(sdk, 'bin', 'fixture-invocations'), 'utf8'), '--version\n');
  }
  writeFakeSdk(sdk);
  writeFileSync(path.join(sdk, 'bin', 'cache', 'dart-sdk', 'version'), '3.99.0');
  assert.throws(() => checkInstalledFlutter(sdk, fixtureLock()), /Bundled Dart SDK/u);
});

test('offline installation accepts internal relative links and rejects external links', {
  skip: process.platform !== 'linux' ? 'Archive fixture includes a Linux executable; Windows remains a native test gate.' : false,
}, async (t) => {
  const root = temporaryDirectory(t);
  const source = path.join(root, 'source');
  writeFakeSdk(path.join(source, 'flutter'));
  symlinkSync('cache/dart-sdk/version', path.join(source, 'flutter', 'bin', 'linked-version'));
  const archive = path.join(root, 'sdk.tar.xz');
  const lock = fixtureLock();
  const pack = () => {
    runFlutterTool('/usr/bin/tar', ['-cJf', archive, '-C', source, 'flutter']);
    const content = readFileSync(archive);
    lock.archives['linux-x64'].size = content.length;
    lock.archives['linux-x64'].sha256 = createHash('sha256').update(content).digest('hex');
  };
  pack();
  const destination = path.join(root, 'installed-sdk');
  await installLockedFlutter(destination, { lock, archive });
  assert.equal(checkInstalledFlutter(destination, lock).frameworkVersion, lock.version);
  assert.equal(readFileSync(path.join(destination, 'bin', 'linked-version'), 'utf8'), lock.dartVersion);
  assert.equal(existsSync(path.join(destination, 'flutter')), false, 'The archive wrapper is stripped exactly once.');
  assert.equal(readdirSync(root).some((entry) => entry.startsWith('.flutter-download-')), false);

  const external = path.join(root, 'external');
  mkdirSync(external);
  writeFileSync(path.join(external, 'user-file'), 'preserve');
  symlinkSync(external, path.join(source, 'flutter', 'link'), 'dir');
  pack();
  const rejectedDestination = path.join(root, 'rejected-sdk');
  await assert.rejects(installLockedFlutter(rejectedDestination, { lock, archive }), /unsafe/u);
  assert.equal(existsSync(rejectedDestination), false);
  assert.equal(readFileSync(path.join(external, 'user-file'), 'utf8'), 'preserve');
});
