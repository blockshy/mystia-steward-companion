#!/usr/bin/env node

import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  accessSync,
  appendFileSync,
  constants,
  cpSync,
  createWriteStream,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
} from 'node:fs';
import https from 'node:https';
import os from 'node:os';
import path from 'node:path';
import { Transform } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const toolchain = JSON.parse(readFileSync(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
const installRoot = parseArguments(process.argv.slice(2));
const platformKey = resolvePlatformKey();
const platform = createPlatformPolicy(platformKey, toolchain);
const archives = readArchivePolicy(toolchain, platformKey, platform);

assertInstallDestination(installRoot);
const temporaryRoot = mkdtempSync(path.join(os.tmpdir(), 'mystia-release-tools-'));
let installCreated = false;
let installReady = false;
let operationError = null;

try {
  mkdirSync(installRoot, { mode: 0o700 });
  installCreated = true;
  assertRealDirectory(installRoot, 'release-tools install root');

  const installedPaths = {};
  for (const toolName of ['powershell', 'githubCli']) {
    const archivePolicy = archives[toolName];
    const toolPolicy = platform[toolName];
    const archivePath = path.join(temporaryRoot, toolPolicy.archiveName);
    const extractionRoot = path.join(temporaryRoot, `${toolName}-extracted`);
    mkdirSync(extractionRoot, { mode: 0o700 });

    await downloadVerifiedArchive(archivePolicy, archivePath);
    extractArchive(platform.tar, toolPolicy.archiveType, archivePath, extractionRoot);

    const payloadRoot = toolPolicy.payloadRoot === '.'
      ? extractionRoot
      : path.join(extractionRoot, ...toolPolicy.payloadRoot.split('/'));
    assertRealDirectory(payloadRoot, `${toolName} archive payload`);
    const extractedExecutable = path.join(
      payloadRoot,
      ...toolPolicy.executablePath.split('/'),
    );
    assertExecutable(extractedExecutable, `${toolName} archive executable`);

    const destination = path.join(installRoot, toolPolicy.installDirectory);
    cpSync(payloadRoot, destination, {
      recursive: true,
      errorOnExist: true,
      force: false,
      preserveTimestamps: true,
      verbatimSymlinks: true,
    });
    const installedExecutable = path.join(
      destination,
      ...toolPolicy.executablePath.split('/'),
    );
    assertExecutable(installedExecutable, `${toolName} installed executable`);
    installedPaths[toolName] = installedExecutable;
  }

  assertExactVersion(
    installedPaths.powershell,
    ['--version'],
    /^PowerShell (\d+\.\d+\.\d+)$/u,
    toolchain.powershell,
    'PowerShell',
  );
  assertExactVersion(
    installedPaths.githubCli,
    ['--version'],
    /^gh version (\d+\.\d+\.\d+)(?:\s|$)/u,
    toolchain.githubCli,
    'GitHub CLI',
  );

  const pathDirectories = [
    path.join(installRoot, platform.powershell.installDirectory),
    path.dirname(path.join(
      installRoot,
      platform.githubCli.installDirectory,
      ...platform.githubCli.executablePath.split('/'),
    )),
  ];
  for (const directory of pathDirectories) {
    assertRealDirectory(directory, 'installed release-tool PATH directory');
  }
  writeGitHubPath(pathDirectories);
  installReady = true;
} catch (error) {
  operationError = error;
}

const cleanupErrors = [];
removeOwnedDirectory(temporaryRoot, 'temporary release-tools workspace', cleanupErrors);
if (installCreated && (!installReady || cleanupErrors.length > 0)) {
  removeOwnedDirectory(installRoot, 'incomplete release-tools install root', cleanupErrors);
}
throwOperationOrCleanupError(operationError, cleanupErrors);

console.log(
  `Installed locked PowerShell ${toolchain.powershell} and GitHub CLI ${toolchain.githubCli} at ${installRoot}.`,
);

function parseArguments(args) {
  if (args.length !== 2 || args[0] !== '--install-root' || !args[1]) {
    fail('Usage: node scripts/install-locked-release-tools.mjs --install-root <new-directory>');
  }
  if (args[1].includes('\0') || /[\r\n]/u.test(args[1])) {
    fail('The release-tools install root contains forbidden characters.');
  }
  return path.resolve(args[1]);
}

function resolvePlatformKey() {
  if (process.arch !== 'x64' || !['linux', 'win32'].includes(process.platform)) {
    fail(`Locked release tools do not support ${process.platform}-${process.arch}.`);
  }
  return `${process.platform}-x64`;
}

function createPlatformPolicy(key, lock) {
  const policies = {
    'linux-x64': {
      tar: '/usr/bin/tar',
      powershell: {
        archiveName: 'powershell.tar.gz',
        archiveType: 'tar.gz',
        expectedUrl: `https://github.com/PowerShell/PowerShell/releases/download/v${lock.powershell}/powershell-${lock.powershell}-linux-x64.tar.gz`,
        payloadRoot: '.',
        executablePath: 'pwsh',
        installDirectory: 'powershell',
      },
      githubCli: {
        archiveName: 'github-cli.tar.gz',
        archiveType: 'tar.gz',
        expectedUrl: `https://github.com/cli/cli/releases/download/v${lock.githubCli}/gh_${lock.githubCli}_linux_amd64.tar.gz`,
        payloadRoot: `gh_${lock.githubCli}_linux_amd64`,
        executablePath: 'bin/gh',
        installDirectory: 'github-cli',
      },
    },
    'win32-x64': {
      tar: resolveWindowsSystemTar(),
      powershell: {
        archiveName: 'powershell.zip',
        archiveType: 'zip',
        expectedUrl: `https://github.com/PowerShell/PowerShell/releases/download/v${lock.powershell}/PowerShell-${lock.powershell}-win-x64.zip`,
        payloadRoot: '.',
        executablePath: 'pwsh.exe',
        installDirectory: 'powershell',
      },
      githubCli: {
        archiveName: 'github-cli.zip',
        archiveType: 'zip',
        expectedUrl: `https://github.com/cli/cli/releases/download/v${lock.githubCli}/gh_${lock.githubCli}_windows_amd64.zip`,
        payloadRoot: '.',
        executablePath: 'bin/gh.exe',
        installDirectory: 'github-cli',
      },
    },
  };
  return policies[key];
}

function resolveWindowsSystemTar() {
  if (process.platform !== 'win32') return '';
  const systemRoot = process.env.SystemRoot;
  if (!systemRoot || !path.isAbsolute(systemRoot) || /[\r\n]/u.test(systemRoot)) {
    fail('SystemRoot must identify the Windows system directory for the locked tar.exe.');
  }
  return path.join(systemRoot, 'System32', 'tar.exe');
}

function readArchivePolicy(lock, key, platformPolicy) {
  assertExactKeys(lock.releaseToolArchives, ['linux-x64', 'win32-x64'], 'releaseToolArchives');
  const selected = lock.releaseToolArchives[key];
  assertExactKeys(selected, ['githubCli', 'powershell'], `releaseToolArchives.${key}`);
  for (const toolName of ['powershell', 'githubCli']) {
    const record = selected[toolName];
    assertExactKeys(record, ['sha256', 'size', 'url'], `releaseToolArchives.${key}.${toolName}`);
    if (record.url !== platformPolicy[toolName].expectedUrl) {
      fail(`Locked ${toolName} archive URL does not match its exact version and platform.`);
    }
    const parsedUrl = new URL(record.url);
    if (parsedUrl.protocol !== 'https:'
      || parsedUrl.hostname !== 'github.com'
      || parsedUrl.port
      || parsedUrl.username
      || parsedUrl.password
      || parsedUrl.search
      || parsedUrl.hash) {
      fail(`Locked ${toolName} archive must use its fixed official GitHub HTTPS URL.`);
    }
    if (!Number.isSafeInteger(record.size) || record.size <= 0) {
      fail(`Locked ${toolName} archive size must be a positive safe integer.`);
    }
    if (!/^[0-9a-f]{64}$/u.test(record.sha256 ?? '')) {
      fail(`Locked ${toolName} archive SHA-256 must be lowercase hexadecimal.`);
    }
  }
  return selected;
}

function assertExactKeys(value, expected, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    fail(`${label} must be an object.`);
  }
  const actual = Object.keys(value).sort();
  const normalizedExpected = [...expected].sort();
  if (JSON.stringify(actual) !== JSON.stringify(normalizedExpected)) {
    fail(`${label} has an unexpected schema.`);
  }
}

function assertInstallDestination(destination) {
  const parent = path.dirname(destination);
  if (destination === parent) {
    fail('The release-tools install root must not be a filesystem root.');
  }
  assertRealDirectory(parent, 'release-tools install parent');
  if (tryLstat(destination)) {
    fail(`The release-tools install root already exists: ${destination}`);
  }
}

async function downloadVerifiedArchive(record, destination) {
  let outputCreated = false;
  try {
    const response = await requestArchive(record.url, 0);
    const contentLength = response.headers['content-length'];
    if (contentLength !== undefined
      && (!/^\d+$/u.test(contentLength) || Number(contentLength) !== record.size)) {
      response.destroy();
      fail(`Archive Content-Length does not match the locked size for ${record.url}.`);
    }

    const hash = createHash('sha256');
    let received = 0;
    const verifier = new Transform({
      transform(chunk, _encoding, callback) {
        received += chunk.length;
        if (received > record.size) {
          callback(new Error(`Archive exceeded its locked size for ${record.url}.`));
          return;
        }
        hash.update(chunk);
        callback(null, chunk);
      },
    });
    const output = createWriteStream(destination, { flags: 'wx', mode: 0o600 });
    outputCreated = true;
    await pipeline(response, verifier, output);

    const stats = lstatSync(destination);
    const digest = hash.digest('hex');
    if (!stats.isFile()
      || stats.isSymbolicLink()
      || stats.size !== record.size
      || received !== record.size
      || digest !== record.sha256) {
      fail(`Downloaded archive does not match its locked size and SHA-256: ${record.url}`);
    }
  } catch (error) {
    if (outputCreated) rmSync(destination, { force: true });
    throw error;
  }
}

function requestArchive(url, redirects) {
  if (redirects > 5) fail('Release-tool archive download exceeded the HTTPS redirect limit.');
  const parsed = new URL(url);
  const allowedHost = redirects === 0
    ? parsed.hostname === 'github.com'
    : parsed.hostname === 'github.com' || parsed.hostname === 'release-assets.githubusercontent.com';
  if (parsed.protocol !== 'https:' || !allowedHost || parsed.username || parsed.password || parsed.hash) {
    fail(`Release-tool archive redirect is outside the approved GitHub HTTPS hosts: ${url}`);
  }

  return new Promise((resolve, reject) => {
    const request = https.get(parsed, {
      headers: {
        Accept: 'application/octet-stream',
        'User-Agent': 'mystia-steward-companion-locked-release-tools',
      },
    }, (response) => {
      if ([301, 302, 303, 307, 308].includes(response.statusCode)) {
        const location = response.headers.location;
        response.resume();
        if (!location) {
          reject(new Error('Release-tool archive redirect omitted its Location header.'));
          return;
        }
        requestArchive(new URL(location, parsed).href, redirects + 1).then(resolve, reject);
        return;
      }
      if (response.statusCode !== 200) {
        response.resume();
        reject(new Error(`Release-tool archive request returned HTTP ${response.statusCode}.`));
        return;
      }
      resolve(response);
    });
    request.setTimeout(120_000, () => {
      request.destroy(new Error('Release-tool archive request timed out.'));
    });
    request.on('error', reject);
  });
}

function extractArchive(tarExecutable, archiveType, archivePath, destination) {
  assertExecutable(tarExecutable, 'fixed system tar');
  const args = archiveType === 'tar.gz'
    ? ['-xzf', archivePath, '-C', destination]
    : ['-xf', archivePath, '-C', destination];
  run(tarExecutable, args, 'archive extraction');
}

function assertExactVersion(executable, args, pattern, expected, label) {
  const output = run(executable, args, `${label} version verification`).stdout.trim();
  const actual = pattern.exec(output)?.[1];
  if (actual !== expected) {
    fail(`${label} archive reported version ${actual ?? '<unparseable>'}; expected ${expected}.`);
  }
}

function run(executable, args, label) {
  const result = spawnSync(executable, args, {
    cwd: repoRoot,
    encoding: 'utf8',
    windowsHide: true,
    shell: false,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  if (result.error) fail(`${label} could not start: ${result.error.message}`);
  if (result.status !== 0) {
    const diagnostic = [result.stderr, result.stdout]
      .map((value) => value?.trim())
      .filter(Boolean)
      .join(' | ');
    fail(`${label} exited with ${result.status}${diagnostic ? `: ${diagnostic}` : ''}`);
  }
  return result;
}

function assertRealDirectory(directory, label) {
  const stats = tryLstat(directory);
  if (!stats || !stats.isDirectory() || stats.isSymbolicLink()) {
    fail(`${label} must be a real directory: ${directory}`);
  }
}

function assertExecutable(executable, label) {
  const stats = tryLstat(executable);
  if (!stats || !stats.isFile() || stats.isSymbolicLink() || stats.size <= 0) {
    fail(`${label} must be a non-empty regular file: ${executable}`);
  }
  if (process.platform !== 'win32') {
    try {
      accessSync(executable, constants.X_OK);
    } catch {
      fail(`${label} is not executable: ${executable}`);
    }
  }
}

function writeGitHubPath(directories) {
  const githubPath = process.env.GITHUB_PATH;
  if (!githubPath) return;
  if (githubPath.includes('\0') || /[\r\n]/u.test(githubPath)) {
    fail('GITHUB_PATH contains forbidden characters.');
  }
  const stats = tryLstat(githubPath);
  if (!stats || !stats.isFile() || stats.isSymbolicLink()) {
    fail(`GITHUB_PATH must be an existing regular file: ${githubPath}`);
  }
  appendFileSync(githubPath, `${directories.join('\n')}\n`, { encoding: 'utf8' });
}

function removeOwnedDirectory(directory, label, errors) {
  try {
    rmSync(directory, {
      recursive: true,
      force: true,
      maxRetries: 5,
      retryDelay: 100,
    });
  } catch (error) {
    errors.push(new Error(`${label} cleanup failed at ${directory}.`, { cause: error }));
  }
}

function throwOperationOrCleanupError(operationError, cleanupErrors) {
  if (operationError && cleanupErrors.length === 0) throw operationError;
  if (!operationError && cleanupErrors.length === 0) return;
  const errors = operationError ? [operationError, ...cleanupErrors] : cleanupErrors;
  const message = operationError
    ? `${operationError.message} One or more owned-directory cleanup operations also failed.`
    : 'One or more owned-directory cleanup operations failed.';
  throw new AggregateError(errors, message);
}

function tryLstat(target) {
  try {
    return lstatSync(target);
  } catch (error) {
    if (error?.code === 'ENOENT') return null;
    throw error;
  }
}

function fail(message) {
  throw new Error(message);
}
