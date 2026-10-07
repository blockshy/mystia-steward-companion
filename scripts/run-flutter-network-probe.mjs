#!/usr/bin/env node

import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import {
  assertRegularFile,
  checkedPath,
  checkInstalledFlutter,
  readFlutterLock,
} from './flutter-toolchain.mjs';

const repoRoot = fileURLToPath(new URL('..', import.meta.url));
const probeRoot = path.join(repoRoot, 'tests', 'flutter-network-probe');

try {
  const args = process.argv.slice(2);
  if (args.length !== 2 || args[0] !== '--sdk-root') {
    throw new Error('Usage: node scripts/run-flutter-network-probe.mjs --sdk-root <locked-flutter-sdk>');
  }
  const toolchain = JSON.parse(readFileSync(path.join(repoRoot, 'toolchain.lock.json'), 'utf8'));
  if (process.versions.node !== toolchain.node) {
    throw new Error(`Node.js ${toolchain.node} is required; current version is ${process.versions.node}.`);
  }
  const sdkRoot = checkedPath(args[1]);
  const flutter = readFlutterLock();
  checkInstalledFlutter(sdkRoot, flutter);
  const dart = path.join(sdkRoot, 'bin', 'cache', 'dart-sdk', 'bin', process.platform === 'win32' ? 'dart.exe' : 'dart');
  assertRegularFile(dart, { executable: true });
  assertRegularFile(path.join(probeRoot, 'pubspec.lock'));

  const outputRoot = path.join(repoRoot, 'temp', 'flutter-network-probe');
  mkdirSync(outputRoot, { recursive: true });
  const runRoot = mkdtempSync(path.join(outputRoot, 'run-'));
  const env = {
    ...process.env,
    CI: 'true',
    FLUTTER_SUPPRESS_ANALYTICS: 'true',
    DART_SUPPRESS_ANALYTICS: 'true',
    PUB_CACHE: path.join(outputRoot, 'pub-cache'),
  };
  function run(command, commandArgs, label) {
    console.log(`Flutter network probe: ${label}`);
    const result = spawnSync(command, commandArgs, {
      cwd: probeRoot,
      env,
      shell: false,
      windowsHide: true,
      encoding: 'utf8',
      stdio: ['ignore', 'pipe', 'pipe'],
      timeout: 180_000,
      maxBuffer: 16 * 1024 * 1024,
    });
    if (result.error) throw new Error(`${label}: ${result.error.message}`);
    if (result.status !== 0) {
      throw new Error(`${label} exited with ${result.status}:\n${result.stdout || ''}${result.stderr || ''}`);
    }
    return result.stdout;
  }
  function saveReport(output, name) {
    let report;
    try {
      report = JSON.parse(output);
    } catch {
      throw new Error(`${name} did not return a JSON report: ${output}`);
    }
    if (report.passed !== true || !Array.isArray(report.checks) || report.checks.length === 0
      || report.checks.some((check) => check.passed !== true)) {
      throw new Error(`${name} has incomplete or failing checks.`);
    }
    if (report.os !== process.platform.replace('win32', 'windows')
      || !report.dartVersion.startsWith(`${flutter.dartVersion} `)) {
      throw new Error(`${name} runtime metadata does not match the verified toolchain.`);
    }
    const reportPath = path.join(runRoot, name);
    writeFileSync(reportPath, `${JSON.stringify(report, null, 2)}\n`, { flag: 'wx' });
    console.log(`${report.checks.length} checks passed; report: ${reportPath}`);
  }

  run(dart, ['pub', 'get', '--offline', '--enforce-lockfile'], 'locked SDK-only dependencies');
  run(dart, ['format', '--output=none', '--set-exit-if-changed', '.'], 'format check');
  run(dart, ['analyze', '--fatal-infos'], 'static analysis');
  saveReport(run(dart, ['run', 'tool/run_probe.dart'], 'JIT HTTP checks'), 'jit.json');

  const executable = path.join(runRoot, `mystia-steward-companion-network-probe${process.platform === 'win32' ? '.exe' : ''}`);
  run(dart, ['compile', 'exe', 'tool/run_probe.dart', '-o', executable], 'AOT compile');
  assertRegularFile(executable, { executable: true });
  saveReport(run(executable, [], 'AOT HTTP checks'), 'aot.json');
  console.log(`Verified Flutter ${flutter.version} / Dart ${flutter.dartVersion}; controlled HTTP checks complete.`);
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
