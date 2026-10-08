import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:crypto/crypto.dart';

const phases = ['write', 'read', 'delete', 'confirm'];

class ProbeCheckFailure extends StateError {
  ProbeCheckFailure(this.check) : super(check);
  final String check;
}

void validateRun(String runId, String phase) {
  if (!RegExp(r'^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$').hasMatch(runId) ||
      ![...phases, 'all', 'corrupt'].contains(phase)) {
    throw const FormatException('Explicit isolated run and phase required');
  }
}

abstract interface class ProbeSecretStore {
  Future<String?> read(String key);
  Future<void> write(String key, String value);
  Future<void> delete(String key);
}

String digest(String value) => sha256.convert(utf8.encode(value)).toString();

Future<Map<String, Object?>> executePhase({
  required String runId,
  required String phase,
  required int processId,
  required Directory support,
  required Directory temporary,
  required ProbeSecretStore secrets,
}) async {
  validateRun(runId, phase);
  if (!phases.contains(phase)) {
    throw const FormatException('Child phase required');
  }
  final root = Directory('${support.path}/storage-p0-$runId');
  final state = File('${root.path}/state.json');
  final key = 'mystia-p0-storage-$runId';
  final checks = <String>[];
  void require(bool value, String check) {
    if (!value) throw ProbeCheckFailure(check);
    checks.add(check);
  }

  final current = await secrets.read(key);
  if (phase == 'write') {
    require(!await root.exists() && current == null, 'fresh-key-and-directory');
    final value = base64UrlEncode(
      List<int>.generate(32, (_) => Random.secure().nextInt(256)),
    );
    await secrets.write(key, value);
    require(await secrets.read(key) == value, 'secure-write-read');
    await root.create();
    final bytes = jsonEncode({
      'runId': runId,
      'digest': digest(value),
      'writePid': processId,
    });
    final pending = File('${root.path}/state.pending');
    await pending.writeAsString(bytes, flush: true);
    await pending.rename(state.path);
    require(await state.readAsString() == bytes, 'support-atomic-write-read');
  } else {
    require(await state.exists(), 'prior-owned-state-required');
    require(
      await FileSystemEntity.type(root.path, followLinks: false) ==
              FileSystemEntityType.directory &&
          await FileSystemEntity.type(state.path, followLinks: false) ==
              FileSystemEntityType.file,
      'owned-path-not-link',
    );
    final prior =
        jsonDecode(await state.readAsString()) as Map<String, dynamic>;
    require(
      prior['runId'] == runId && prior['writePid'] != processId,
      'new-process-owned-state',
    );
    if (phase == 'read') {
      require(current != null, 'secure-value-present-after-process-restart');
      require(
        digest(current!) == prior['digest'],
        'secure-read-after-process-restart',
      );
      final receipt = File('${root.path}/read.json');
      require(!await receipt.exists(), 'read-phase-not-replayed');
      await receipt.writeAsString(
        jsonEncode({'pid': processId, 'runId': runId}),
        flush: true,
      );
    } else {
      final readReceipt = jsonDecode(
        await File('${root.path}/read.json').readAsString(),
      ) as Map<String, dynamic>;
      require(
        readReceipt['runId'] == runId && readReceipt['pid'] != processId,
        'read-phase-completed-in-other-process',
      );
      if (phase == 'delete') {
        require(
          current != null && digest(current) == prior['digest'],
          'exact-synthetic-key-before-delete',
        );
        await secrets.delete(key);
        require(await secrets.read(key) == null, 'exact-key-delete-read');
        final receipt = File('${root.path}/deleted.json');
        require(!await receipt.exists(), 'delete-phase-not-replayed');
        await receipt.writeAsString(
          jsonEncode({'pid': processId, 'runId': runId}),
          flush: true,
        );
      } else {
        final deleted = jsonDecode(
          await File('${root.path}/deleted.json').readAsString(),
        ) as Map<String, dynamic>;
        require(
          deleted['runId'] == runId &&
              deleted['pid'] != processId &&
              current == null,
          'deletion-persists-after-process-restart',
        );
        // Delete only the three exact owned files; unexpected files are preserved.
        for (final name in ['state.json', 'read.json', 'deleted.json']) {
          await File('${root.path}/$name').delete();
        }
        await root.delete();
        require(!await root.exists(), 'owned-support-files-cleaned');
      }
    }
  }
  final temp = File('${temporary.path}/mystia-storage-p0-$runId-$phase.tmp');
  require(!await temp.exists(), 'fresh-temporary-file');
  await temp.writeAsString('synthetic:$runId:$phase', flush: true);
  require(
    await temp.readAsString() == 'synthetic:$runId:$phase',
    'temporary-directory-write-read',
  );
  await temp.delete();
  require(!await temp.exists(), 'owned-temporary-file-cleaned');
  return {
    'checks': checks,
    'supportDirectory': support.path,
    'temporaryDirectory': temporary.path,
  };
}
