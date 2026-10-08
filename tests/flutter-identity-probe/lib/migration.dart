import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:crypto/crypto.dart';

class MigrationFailure implements Exception {
  const MigrationFailure(this.code);
  final String code;
  @override
  String toString() => code;
}

void requireMigration(bool condition, String code) {
  if (!condition) throw MigrationFailure(code);
}

Map<String, dynamic> object(dynamic value) {
  requireMigration(value is Map<String, dynamic>, 'object-required');
  return value as Map<String, dynamic>;
}

void exactKeys(
  Map<String, dynamic> value,
  Set<String> required, [
  Set<String> optional = const {},
]) {
  requireMigration(
    required.every(value.containsKey) &&
        value.keys.every(
          (key) => required.contains(key) || optional.contains(key),
        ),
    'unknown-or-missing-field',
  );
}

String canonicalJson(dynamic value) {
  if (value is Map<String, dynamic>) {
    final keys = value.keys.toList()..sort();
    return '{${keys.map((key) => '${jsonEncode(key)}:${canonicalJson(value[key])}').join(',')}}';
  }
  if (value is List) return '[${value.map(canonicalJson).join(',')}]';
  return jsonEncode(value);
}

String profileHash(Map<String, dynamic> value) =>
    sha256.convert(utf8.encode(canonicalJson(value))).toString();

class SettingsImport {
  SettingsImport._(this.settings, this.clientId);
  final Map<String, dynamic> settings;
  final String? clientId;

  factory SettingsImport.parse(
    String text, {
    required String platform,
    required String installationBinding,
    bool confirmIdentity = false,
  }) {
    requireMigration(utf8.encode(text).length <= 16384, 'settings-too-large');
    final envelope = object(jsonDecode(text));
    exactKeys(
      envelope,
      {'schemaVersion', 'kind', 'source', 'settings'},
      {'deviceIdentity'},
    );
    requireMigration(
      envelope['schemaVersion'] == 1 &&
          envelope['kind'] == 'mystia-settings-export',
      'settings-schema',
    );
    final source = object(envelope['source']);
    exactKeys(source, {'platform', 'origin', 'installationBinding'});
    requireMigration(
      ['windows', 'android'].contains(source['platform']) &&
          source['origin'] == 'http://tauri.localhost' &&
          source['installationBinding'] is String &&
          RegExp(r'^[a-f0-9]{64}$')
              .hasMatch(source['installationBinding'] as String),
      'source-binding',
    );
    final settings = object(envelope['settings']);
    const choices = {
      'theme': ['light', 'dark', 'system'],
      'navigation': [
        'overview',
        'recommendations',
        'service',
        'automation',
        'extensions',
        'settings',
        'settings:logs',
      ],
      'focusSwitchBehavior': ['hide', 'keep-visible'],
      'customRecipeGroupMode': ['recipe', 'customer'],
    };
    const ranges = {
      'fontScalePercent': [90, 130],
      'backgroundOpacity': [0.2, 1],
      'contentOpacity': [0.35, 1],
      'focusSwitchCooldownMs': [250, 2000],
      'focusRecipeLimit': [1, 20],
      'focusBeverageLimit': [1, 20],
    };
    const booleanFields = {
      'alwaysOnTop',
      'gamepadNavigation',
      'showDebugDetails',
      'missionListModuleEnabled',
      'rareGuestInvitationModuleEnabled',
      'focusCompact',
    };
    exactKeys(settings, {}, {
      'endpoint',
      ...choices.keys,
      ...ranges.keys,
      ...booleanFields,
    });
    for (final entry in settings.entries) {
      if (entry.key == 'endpoint') {
        requireMigration(entry.value is String, 'endpoint-type');
        final uri = Uri.tryParse(entry.value as String);
        requireMigration(
          uri != null &&
              ['http', 'https'].contains(uri.scheme) &&
              uri.host.isNotEmpty &&
              uri.userInfo.isEmpty &&
              !uri.hasQuery &&
              !uri.hasFragment &&
              (uri.path.isEmpty || uri.path == '/'),
          'endpoint-invalid',
        );
      } else if (choices.containsKey(entry.key)) {
        requireMigration(
          choices[entry.key]!.contains(entry.value),
          'preference-enum',
        );
      } else if (booleanFields.contains(entry.key)) {
        requireMigration(entry.value is bool, 'preference-boolean');
      } else {
        final range = ranges[entry.key]!;
        requireMigration(
          entry.value is num &&
              (entry.value as num).isFinite &&
              entry.value >= range[0] &&
              entry.value <= range[1],
          'preference-range',
        );
        if (!entry.key.endsWith('Opacity'))
          requireMigration(entry.value is int, 'preference-integer');
        if (entry.key == 'fontScalePercent')
          requireMigration((entry.value as int) % 5 == 0, 'font-scale-step');
      }
    }
    String? clientId;
    if (envelope.containsKey('deviceIdentity')) {
      final identity = object(envelope['deviceIdentity']);
      exactKeys(identity, {'clientId'});
      requireMigration(
        confirmIdentity &&
            source['platform'] == platform &&
            source['installationBinding'] == installationBinding,
        'identity-repair-required',
      );
      requireMigration(
        identity['clientId'] is String &&
            RegExp(r'^[A-Za-z0-9-]{16,64}$')
                .hasMatch(identity['clientId'] as String),
        'client-id-invalid',
      );
      clientId = identity['clientId'] as String;
    }
    return SettingsImport._(Map.unmodifiable(settings), clientId);
  }
}

// New test-owned directories only. Existing files are never replaced, including corrupt/future files.
class AtomicMigrationFile {
  AtomicMigrationFile(this.directory);
  final Directory directory;
  Future<File> create(String name, Map<String, dynamic> value) async {
    requireMigration(RegExp(r'^[a-z0-9-]+\.json$').hasMatch(name), 'file-name');
    requireMigration(
      await FileSystemEntity.type(directory.path, followLinks: false) ==
          FileSystemEntityType.directory,
      'owned-directory-required',
    );
    final lockFile = File('${directory.path}/.migration.lock');
    requireMigration(
      await FileSystemEntity.type(lockFile.path, followLinks: false) !=
          FileSystemEntityType.link,
      'lock-link',
    );
    final lock = await lockFile.open(mode: FileMode.append);
    await lock.lock(FileLock.exclusive);
    try {
      final target = File('${directory.path}/$name');
      requireMigration(
        await FileSystemEntity.type(target.path, followLinks: false) ==
            FileSystemEntityType.notFound,
        'existing-file-preserved',
      );
      final temporary = await directory.createTemp('.migration-');
      try {
        final staged = File('${temporary.path}/payload');
        await staged.writeAsString(jsonEncode(value), flush: true);
        requireMigration(
          canonicalJson(jsonDecode(await staged.readAsString())) ==
              canonicalJson(value),
          'atomic-readback',
        );
        await staged.rename(target.path);
      } finally {
        await temporary.delete(recursive: true);
      }
      return target;
    } finally {
      await lock.unlock();
      await lock.close();
    }
  }
}

String newClientId() {
  final random = Random.secure();
  return List.generate(
    16,
    (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
  ).join();
}
