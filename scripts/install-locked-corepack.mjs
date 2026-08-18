#!/usr/bin/env node

import { createHash, randomUUID } from 'node:crypto';
import {
  appendFileSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  renameSync,
  rmSync,
} from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const toolchain = JSON.parse(readFileSync(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
const expectedIntegrity = toolchain.corepackIntegrity;
const installRoot = parseArguments(process.argv.slice(2));
const npmCliPath = resolveBundledNpmCli();

if (!/^sha512-[A-Za-z0-9+/]+={0,2}$/u.test(expectedIntegrity ?? '')) {
  fail('toolchain.lock.json contains an invalid Corepack package integrity.');
}

assertInstallDestination(installRoot);
const stagingRoot = mkdtempSync(path.join(os.tmpdir(), 'mystia-corepack-'));
const transactionRoot = path.join(
  path.dirname(installRoot),
  `.${path.basename(installRoot)}.stage-${randomUUID()}`,
);
let transactionCreated = false;
let transactionRenamed = false;
let transactionCommitted = false;
try {
  mkdirSync(transactionRoot, { mode: 0o700 });
  transactionCreated = true;

  const pack = run(process.execPath, [npmCliPath,
    'pack',
    `corepack@${toolchain.corepack}`,
    '--pack-destination',
    stagingRoot,
    '--json',
    '--ignore-scripts',
  ]);
  let packResult;
  try {
    const parsed = JSON.parse(pack.stdout);
    if (!Array.isArray(parsed) || parsed.length !== 1) throw new Error('unexpected entry count');
    [packResult] = parsed;
  } catch (error) {
    fail(`npm pack returned invalid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }

  const expectedFileName = `corepack-${toolchain.corepack}.tgz`;
  if (packResult?.filename !== expectedFileName) {
    fail(`npm pack returned an unexpected Corepack archive: ${String(packResult?.filename)}`);
  }
  const archivePath = path.join(stagingRoot, expectedFileName);
  const archiveStats = lstatSync(archivePath);
  if (!archiveStats.isFile() || archiveStats.isSymbolicLink() || archiveStats.size <= 0) {
    fail('The downloaded Corepack archive is not a non-empty regular file.');
  }
  const actualIntegrity = `sha512-${createHash('sha512').update(readFileSync(archivePath)).digest('base64')}`;
  if (actualIntegrity !== expectedIntegrity) {
    fail('The downloaded Corepack archive does not match toolchain.lock.json.');
  }

  run(process.execPath, [npmCliPath,
    'install',
    '--global',
    '--prefix',
    transactionRoot,
    '--ignore-scripts',
    '--no-audit',
    '--no-fund',
    archivePath,
  ], true);

  assertInstalledCorepack(transactionRoot);
  renameSync(transactionRoot, installRoot);
  transactionRenamed = true;
  assertInstalledCorepack(installRoot);

  const pathDirectory = getCorepackPathDirectory(installRoot);
  writeGitHubPath(pathDirectory);
  transactionCommitted = true;
  console.log(
    `Installed locked Corepack ${toolchain.corepack} from its verified package archive at ${installRoot}.`,
  );
} finally {
  rmSync(stagingRoot, { recursive: true, force: true });
  if (transactionCreated && !transactionCommitted) {
    rmSync(transactionRenamed ? installRoot : transactionRoot, { recursive: true, force: true });
  }
}

function parseArguments(args) {
  if (args.length !== 2 || args[0] !== '--install-root' || !args[1]) {
    fail('Usage: node scripts/install-locked-corepack.mjs --install-root <new-directory>');
  }
  if (args[1].includes('\0') || /[\r\n]/u.test(args[1])) {
    fail('The Corepack install root contains forbidden characters.');
  }
  return path.resolve(args[1]);
}

function assertInstallDestination(destination) {
  const parent = path.dirname(destination);
  if (destination === parent) {
    fail('The Corepack install root must not be a filesystem root.');
  }
  assertRealDirectory(parent, 'Corepack install parent');
  if (tryLstat(destination)) {
    fail(`The Corepack install root already exists: ${destination}`);
  }
}

function assertInstalledCorepack(root) {
  const packageRoot = process.platform === 'win32'
    ? path.join(root, 'node_modules', 'corepack')
    : path.join(root, 'lib', 'node_modules', 'corepack');
  assertRealDirectory(packageRoot, 'installed Corepack package');

  const manifestPath = path.join(packageRoot, 'package.json');
  assertRegularFile(manifestPath, 'installed Corepack package manifest');
  let manifest;
  try {
    manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  } catch (error) {
    fail(`Installed Corepack package manifest is invalid JSON: ${error instanceof Error ? error.message : String(error)}`);
  }
  if (manifest?.version !== toolchain.corepack
    || manifest?.bin?.corepack !== './dist/corepack.js'
    || manifest?.bin?.pnpm !== './dist/pnpm.js') {
    fail('Installed Corepack package identity or executable map is invalid.');
  }

  const corepackEntry = path.join(packageRoot, 'dist', 'corepack.js');
  const pnpmEntry = path.join(packageRoot, 'dist', 'pnpm.js');
  assertRegularFile(corepackEntry, 'installed Corepack entry point');
  assertRegularFile(pnpmEntry, 'installed pnpm entry point');
  const reportedVersion = run(
    process.execPath,
    [corepackEntry, '--version'],
  ).stdout.trim();
  if (reportedVersion !== toolchain.corepack) {
    fail(`Installed Corepack reported version ${reportedVersion || '<empty>'}; expected ${toolchain.corepack}.`);
  }

  const pathDirectory = getCorepackPathDirectory(root);
  assertRealDirectory(pathDirectory, 'installed Corepack PATH directory');
  if (process.platform === 'win32') {
    assertRegularFile(path.join(pathDirectory, 'corepack.cmd'), 'installed Corepack Windows shim');
    assertRegularFile(path.join(pathDirectory, 'pnpm.cmd'), 'installed pnpm Windows shim');
  } else {
    assertExactSymlink(path.join(pathDirectory, 'corepack'), corepackEntry, 'installed Corepack shim');
    assertExactSymlink(path.join(pathDirectory, 'pnpm'), pnpmEntry, 'installed pnpm shim');
  }
}

function getCorepackPathDirectory(root) {
  return process.platform === 'win32' ? root : path.join(root, 'bin');
}

function resolveBundledNpmCli() {
  const nodeDirectory = path.dirname(process.execPath);
  const candidate = process.platform === 'win32'
    ? path.join(nodeDirectory, 'node_modules', 'npm', 'bin', 'npm-cli.js')
    : realpathSync.native(path.join(nodeDirectory, 'npm'));
  const stats = lstatSync(candidate);
  if (!stats.isFile() || stats.isSymbolicLink()) {
    fail(`The npm CLI bundled with Node.js is not a regular file: ${candidate}`);
  }
  return candidate;
}

function assertRealDirectory(directory, label) {
  const stats = tryLstat(directory);
  if (!stats || !stats.isDirectory() || stats.isSymbolicLink()) {
    fail(`${label} must be a real directory: ${directory}`);
  }
}

function assertRegularFile(filePath, label) {
  const stats = tryLstat(filePath);
  if (!stats || !stats.isFile() || stats.isSymbolicLink() || stats.size <= 0) {
    fail(`${label} must be a non-empty regular file: ${filePath}`);
  }
}

function assertExactSymlink(linkPath, expectedTarget, label) {
  const stats = tryLstat(linkPath);
  if (!stats?.isSymbolicLink()) {
    fail(`${label} must be a symbolic link: ${linkPath}`);
  }
  if (realpathSync.native(linkPath) !== realpathSync.native(expectedTarget)) {
    fail(`${label} does not resolve to its verified package entry point.`);
  }
}

function writeGitHubPath(directory) {
  const githubPath = process.env.GITHUB_PATH;
  if (!githubPath) return;
  if (githubPath.includes('\0') || /[\r\n]/u.test(githubPath)) {
    fail('GITHUB_PATH contains forbidden characters.');
  }
  const stats = tryLstat(githubPath);
  if (!stats || !stats.isFile() || stats.isSymbolicLink()) {
    fail(`GITHUB_PATH must be an existing regular file: ${githubPath}`);
  }
  appendFileSync(githubPath, `${directory}\n`, { encoding: 'utf8' });
}

function tryLstat(target) {
  try {
    return lstatSync(target);
  } catch (error) {
    if (error?.code === 'ENOENT') return null;
    throw error;
  }
}

function run(command, args, inheritOutput = false) {
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    encoding: 'utf8',
    windowsHide: true,
    stdio: inheritOutput ? 'inherit' : ['ignore', 'pipe', 'pipe'],
  });
  if (result.error) fail(`${command} could not start: ${result.error.message}`);
  if (result.status !== 0) {
    const diagnostic = [result.stderr, result.stdout]
      .filter(Boolean)
      .map((value) => value.trim())
      .filter(Boolean)
      .join(' | ');
    fail(`${command} exited with ${result.status}${diagnostic ? `: ${diagnostic}` : ''}`);
  }
  return result;
}

function fail(message) {
  throw new Error(message);
}
