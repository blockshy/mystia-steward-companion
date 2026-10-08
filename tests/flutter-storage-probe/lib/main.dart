import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/material.dart';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:path_provider/path_provider.dart';

import 'generated/storage_probe_api.g.dart';
import 'storage_flow.dart';
import 'windows_dpapi_store.dart';

const sourceDigest = String.fromEnvironment('MYSTIA_STORAGE_SOURCE_DIGEST');
const gitSha = String.fromEnvironment('MYSTIA_STORAGE_GIT_SHA');
const executableName = 'mystia-steward-companion-storage-probe.exe';

class AndroidSecretStore implements ProbeSecretStore {
  const AndroidSecretStore();
  static const storage = FlutterSecureStorage(
    aOptions: AndroidOptions(
      storageNamespace: 'mystia_storage_p0',
      resetOnError: false,
      migrateOnAlgorithmChange: false,
    ),
  );
  @override
  Future<String?> read(String key) => storage.read(key: key);
  @override
  Future<void> write(String key, String value) =>
      storage.write(key: key, value: value);
  @override
  Future<void> delete(String key) => storage.delete(key: key);
}

Future<void> main(List<String> args) async {
  WidgetsFlutterBinding.ensureInitialized();
  if (!RegExp(r'^[a-f0-9]{64}$').hasMatch(sourceDigest) ||
      !RegExp(r'^[a-f0-9]{40}$').hasMatch(gitSha)) {
    exit(2);
  }
  String runId;
  String phase;
  String? resultFile;
  Map<String, dynamic> native = {};
  if (Platform.isAndroid) {
    native = jsonDecode(
      await StorageProbeApi().launchConfiguration(),
    ) as Map<String, dynamic>;
    runId = native['runId'] as String;
    phase = native['phase'] as String;
  } else if (Platform.isWindows) {
    if (args.length != 7 ||
        args[0] != '--probe' ||
        args[1] != '--run-id' ||
        args[3] != '--suite' ||
        args[5] != '--result-file') {
      exit(2);
    }
    runId = args[2];
    phase = args[4];
    resultFile = args[6];
    validateRun(runId, phase);
    final runRoot = 'D:/dev/mystia-node/runs/$runId';
    final expected = '$runRoot/${phase == 'all' ? 'probe-result' : phase}.json';
    if (resultFile.replaceAll(r'\', '/').toLowerCase() !=
            expected.toLowerCase() ||
        Platform.resolvedExecutable.replaceAll(r'\', '/').toLowerCase() !=
            '$runRoot/payload/$executableName'.toLowerCase() ||
        await File(resultFile).exists()) {
      exit(2);
    }
  } else {
    exit(2);
  }
  validateRun(runId, phase);
  final report = <String, Object?>{
    'schemaVersion': 1,
    'kind': 'flutter-storage-p0',
    'runId': runId,
    'phase': phase,
    'gitSha': gitSha,
    'sourceDigest': sourceDigest,
    'pid': pid,
    'platform': Platform.operatingSystem,
    'dartVersion': Platform.version,
    'native': native,
    'status': 'FAIL',
    'syntheticOnly': true,
    'p0Verified': false,
    'backend': Platform.isWindows
        ? 'current-user-dpapi-owned-file'
        : 'flutter_secure_storage-android',
  };
  runApp(
    const MaterialApp(
      home: Scaffold(
        body: Center(child: Text('Mystia storage P0 · synthetic data only')),
      ),
    ),
  );
  await WidgetsBinding.instance.endOfFrame;
  try {
    if (phase == 'all') {
      final children = <Map<String, dynamic>>[];
      for (final childPhase in [...phases, 'corrupt']) {
        final childFile = File(
          '${File(resultFile!).parent.path}/$childPhase.json',
        );
        final child = await Process.start(Platform.resolvedExecutable, [
          '--probe',
          '--run-id',
          runId,
          '--suite',
          childPhase,
          '--result-file',
          childFile.path,
        ], mode: ProcessStartMode.inheritStdio);
        late int code;
        try {
          code = await child.exitCode.timeout(const Duration(seconds: 60));
        } on TimeoutException {
          child.kill();
          await child.exitCode.timeout(const Duration(seconds: 10));
          rethrow;
        }
        final evidence =
            jsonDecode(await childFile.readAsString()) as Map<String, dynamic>;
        if (code != 0 ||
            evidence['status'] != 'PASS' ||
            evidence['pid'] != child.pid ||
            evidence['runId'] != runId ||
            evidence['phase'] != childPhase ||
            evidence['sourceDigest'] != sourceDigest ||
            evidence['gitSha'] != gitSha ||
            children.any((prior) => prior['pid'] == child.pid)) {
          throw StateError('Child phase/identity/exit failed');
        }
        children.add(evidence);
      }
      report['children'] = children;
      report['normalChildExits'] = true;
    } else {
      final support = await getApplicationSupportDirectory();
      final temporary = await getTemporaryDirectory();
      if (Platform.isWindows &&
          !support.path
              .replaceAll(r'\', '/')
              .endsWith('/mystia_storage_probe')) {
        throw StateError('Isolated Windows ProductName directory required');
      }
      if (Platform.isAndroid &&
          (native['nativePid'] != pid ||
              native['debuggable'] != false ||
              !support.path.startsWith('${native['privateRoot']}/') ||
              !temporary.path.startsWith('${native['privateRoot']}/'))) {
        throw StateError('Release/private directory/native PID differs');
      }
      final secrets = Platform.isWindows
          ? WindowsDpapiStore(support, runId)
          : const AndroidSecretStore();
      if (phase == 'corrupt') {
        if (secrets is! WindowsDpapiStore) {
          throw StateError('Windows-only negative proof');
        }
        report['corruptionProof'] = await secrets.corruptionProof(
          File(resultFile!).parent,
        );
      } else {
        report.addAll(
          await executePhase(
            runId: runId,
            phase: phase,
            processId: pid,
            support: support,
            temporary: temporary,
            secrets: secrets,
          ),
        );
        if (phase == 'confirm' && secrets is WindowsDpapiStore) {
          await secrets.removeEmptyDirectory();
        }
      }
    }
    report['status'] = 'PASS';
  } on Object catch (error) {
    // Avoid serializing plugin exceptions that may contain protected file data.
    report['errorType'] = error.runtimeType.toString();
    if (error is ProbeCheckFailure) report['failedCheck'] = error.check;
    if (error is DpapiFailure) {
      report['nativeFailure'] = {
        'operation': error.operation,
        'code': error.code,
      };
    }
  }
  final encoded = '${const JsonEncoder.withIndent('  ').convert(report)}\n';
  if (Platform.isAndroid) {
    await StorageProbeApi().publishReport(encoded);
    // The native Activity finishes normally and publishes only after onDestroy.
    // Dart exit() would bypass Android's lifecycle SharedPreferences flushing.
    return;
  } else {
    final file = File(resultFile!);
    await file.create(exclusive: true);
    await file.writeAsString(encoded, flush: true);
  }
  exit(report['status'] == 'PASS' ? 0 : 1);
}
