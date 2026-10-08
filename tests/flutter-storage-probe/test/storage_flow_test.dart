import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_storage_probe/storage_flow.dart';

class MemorySecrets implements ProbeSecretStore {
  final values = <String, String>{'untouched': 'foreign-fixture'};
  @override
  Future<String?> read(String key) async => values[key];
  @override
  Future<void> write(String key, String value) async {
    values[key] = value;
  }

  @override
  Future<void> delete(String key) async {
    values.remove(key);
  }
}

void main() {
  test(
    'ordered separate-process protocol cleans only its own key/files',
    () async {
      final directory = await Directory.systemTemp.createTemp(
        'mystia-storage-test-',
      );
      final secrets = MemorySecrets();
      try {
        for (var i = 0; i < phases.length; i++) {
          await executePhase(
            runId: 'fixture',
            phase: phases[i],
            processId: i + 100,
            support: directory,
            temporary: directory,
            secrets: secrets,
          );
        }
        expect(secrets.values, {'untouched': 'foreign-fixture'});
        expect(await directory.list().isEmpty, isTrue);
      } finally {
        await directory.delete(recursive: true);
      }
    },
  );
  test(
    'read without original state never creates or deletes storage',
    () async {
      final directory = await Directory.systemTemp.createTemp(
        'mystia-storage-test-',
      );
      final secrets = MemorySecrets();
      try {
        await expectLater(
          executePhase(
            runId: 'fixture',
            phase: 'read',
            processId: 200,
            support: directory,
            temporary: directory,
            secrets: secrets,
          ),
          throwsStateError,
        );
        expect(secrets.values, {'untouched': 'foreign-fixture'});
      } finally {
        await directory.delete(recursive: true);
      }
    },
  );
  test('same process cannot pretend to prove restart persistence', () async {
    final directory = await Directory.systemTemp.createTemp(
      'mystia-storage-test-',
    );
    final secrets = MemorySecrets();
    try {
      await executePhase(
        runId: 'fixture',
        phase: 'write',
        processId: 200,
        support: directory,
        temporary: directory,
        secrets: secrets,
      );
      await expectLater(
        executePhase(
          runId: 'fixture',
          phase: 'read',
          processId: 200,
          support: directory,
          temporary: directory,
          secrets: secrets,
        ),
        throwsStateError,
      );
    } finally {
      await directory.delete(recursive: true);
    }
  });
  test('run paths cannot escape isolated namespace', () {
    for (final id in ['../x', 'a/b', 'a\\b', '', 'a b']) {
      expect(() => validateRun(id, 'write'), throwsFormatException);
    }
  });
}
