import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:ffi/ffi.dart';
import 'package:win32/win32.dart';

import 'storage_flow.dart';

// dpapi.h: prohibit a UI prompt; deliberately never set LOCAL_MACHINE (0x4).
const _cryptprotectUiForbidden = 0x1;

class DpapiFailure implements Exception {
  const DpapiFailure(this.operation, this.code);
  final String operation;
  final int code;
}

/// A bounded P0 current-user adapter. It never resets or repairs a corrupt file.
/// It refuses replacement; production update transactions are a later concern.
class WindowsDpapiStore implements ProbeSecretStore {
  WindowsDpapiStore(Directory support, this.runId)
    : directory = Directory('${support.path}/dpapi-p0-$runId') {
    validateRun(runId, 'write');
    if (!Platform.isWindows) throw UnsupportedError('Windows DPAPI required');
  }
  final String runId;
  final Directory directory;

  File fileFor(String key) {
    if (key != 'mystia-p0-storage-$runId' &&
        key != 'mystia-p0-storage-$runId-negative') {
      throw const FormatException('Exact synthetic DPAPI key required');
    }
    return File('${directory.path}/${digest(key)}.dpapi');
  }

  Future<void> _plainPath() async {
    var cursor = directory;
    while (true) {
      final kind = await FileSystemEntity.type(cursor.path, followLinks: false);
      if (kind != FileSystemEntityType.notFound &&
          kind != FileSystemEntityType.directory) {
        throw StateError('Directory link or unexpected type refused');
      }
      if (cursor.parent.path == cursor.path) break;
      cursor = cursor.parent;
    }
  }

  Uint8List _crypt(Uint8List input, {required bool protect}) {
    if (input.isEmpty || input.length > 16384) {
      throw const FormatException('Bounded DPAPI blob required');
    }
    return using((arena) {
      final inputBytes = arena<Uint8>(input.length);
      inputBytes.asTypedList(input.length).setAll(0, input);
      final source = arena<CRYPT_INTEGER_BLOB>();
      source.ref.cbData = input.length;
      source.ref.pbData = inputBytes;
      final target = arena<CRYPT_INTEGER_BLOB>();
      try {
        final result = protect
            ? CryptProtectData(
                source,
                null,
                null,
                null,
                _cryptprotectUiForbidden,
                target,
              )
            : CryptUnprotectData(
                source,
                null,
                null,
                null,
                _cryptprotectUiForbidden,
                target,
              );
        if (!result.value) {
          throw DpapiFailure(
            protect ? 'protect' : 'unprotect',
            result.error.toInt(),
          );
        }
        if (target.ref.pbData.address == 0 || target.ref.cbData > 16384) {
          throw const FormatException('Invalid DPAPI output');
        }
        return Uint8List.fromList(
          target.ref.pbData.asTypedList(target.ref.cbData),
        );
      } finally {
        inputBytes.asTypedList(input.length).fillRange(0, input.length, 0);
        if (target.ref.pbData.address != 0) {
          target.ref.pbData
              .asTypedList(target.ref.cbData)
              .fillRange(0, target.ref.cbData, 0);
          LocalFree(HLOCAL(target.ref.pbData.cast()));
        }
      }
    });
  }

  @override
  Future<String?> read(String key) async {
    await _plainPath();
    final file = fileFor(key);
    final kind = await FileSystemEntity.type(file.path, followLinks: false);
    if (kind == FileSystemEntityType.notFound) return null;
    if (kind != FileSystemEntityType.file || await file.length() > 16384) {
      throw const FormatException('Invalid owned credential file');
    }
    final plaintext = _crypt(await file.readAsBytes(), protect: false);
    try {
      final decoded = jsonDecode(utf8.decode(plaintext));
      if (decoded is! Map ||
          decoded.length != 3 ||
          decoded['schemaVersion'] != 1 ||
          decoded['key'] != key ||
          decoded['value'] is! String) {
        throw const FormatException('Credential envelope identity differs');
      }
      return decoded['value'] as String;
    } finally {
      plaintext.fillRange(0, plaintext.length, 0);
    }
  }

  @override
  Future<void> write(String key, String value) async {
    await _plainPath();
    final file = fileFor(key);
    if (await FileSystemEntity.type(file.path, followLinks: false) !=
        FileSystemEntityType.notFound) {
      throw StateError('Existing credential is never overwritten');
    }
    await directory.create();
    final plaintext = Uint8List.fromList(
      utf8.encode(jsonEncode({'schemaVersion': 1, 'key': key, 'value': value})),
    );
    late Uint8List encrypted;
    try {
      encrypted = _crypt(plaintext, protect: true);
    } finally {
      plaintext.fillRange(0, plaintext.length, 0);
    }
    final pending = File('${file.path}.pending');
    await pending.create(exclusive: true);
    await pending.writeAsBytes(encrypted, flush: true);
    final moved = using(
      (arena) => MoveFileEx(
        PCWSTR(pending.path.toNativeUtf16(allocator: arena)),
        PCWSTR(file.path.toNativeUtf16(allocator: arena)),
        MOVEFILE_WRITE_THROUGH,
      ),
    );
    if (!moved.value) throw DpapiFailure('publish', moved.error.toInt());
  }

  @override
  Future<void> delete(String key) async {
    // Unknown/corrupt data is an error even for delete; caller must not reset it.
    if (await read(key) == null) return;
    await fileFor(key).delete();
  }

  Future<void> removeEmptyDirectory() async {
    await _plainPath();
    if (await directory.exists()) await directory.delete();
  }

  Future<Map<String, Object?>> corruptionProof(Directory evidence) async {
    final key = 'mystia-p0-storage-$runId';
    final negativeKey = '$key-negative';
    if (await directory.exists()) {
      throw StateError('Fresh corruption namespace required');
    }
    await write(key, 'synthetic-corruption-control-$runId');
    final original = await fileFor(key).readAsBytes();
    final damaged = Uint8List.fromList(original)..[original.length - 1] ^= 1;
    final negative = fileFor(negativeKey);
    await negative.create(exclusive: true);
    await negative.writeAsBytes(damaged, flush: true);
    var rejectedRead = false;
    var rejectedDelete = false;
    try {
      await read(negativeKey);
    } on DpapiFailure {
      rejectedRead = true;
    }
    try {
      await delete(negativeKey);
    } on DpapiFailure {
      rejectedDelete = true;
    }
    final sha = sha256.convert(damaged).toString();
    if (!rejectedRead ||
        !rejectedDelete ||
        sha256.convert(await negative.readAsBytes()).toString() != sha ||
        sha256.convert(await fileFor(key).readAsBytes()).toString() !=
            sha256.convert(original).toString() ||
        await read(key) != 'synthetic-corruption-control-$runId') {
      throw StateError(
        'Corruption must reject without changing either ciphertext',
      );
    }
    for (final entry in {
      'dpapi-original.bin': original,
      'dpapi-tampered.bin': damaged,
    }.entries) {
      final file = File('${evidence.path}/${entry.key}');
      await file.create(exclusive: true);
      await file.writeAsBytes(entry.value, flush: true);
    }
    // Explicit fixture cleanup after evidence capture, outside adapter recovery.
    await delete(key);
    await negative.delete();
    await removeEmptyDirectory();
    return {
      'readRejected': rejectedRead,
      'deleteRejected': rejectedDelete,
      'tamperedCiphertextUnchanged': true,
      'originalCiphertextUnchanged': true,
      'originalCiphertextSha256': sha256.convert(original).toString(),
      'tamperedCiphertextSha256': sha,
      'syntheticCiphertextsRetainedInRunEvidence': true,
    };
  }
}
