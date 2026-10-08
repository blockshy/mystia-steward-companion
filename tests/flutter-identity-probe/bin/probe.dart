import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../lib/migration.dart';
import '../lib/recovery.dart';

Future<void> main(List<String> args) async {
  requireMigration(
    args.length == 2,
    'usage-repository-and-fixture-directories',
  );
  final repository = Directory(args[0]).absolute.path;
  final fixtures = Directory(args[1]).absolute.path;
  final temporary = await Directory.systemTemp.createTemp(
    'mystia-dart-identity-p0-',
  );
  final checks = <String>[];
  final transcripts = <Map<String, dynamic>>[];
  Future<void> test(String name, Future<void> Function() body) async {
    await body();
    checks.add(name);
    stdout.writeln('PASS $name');
  }

  final expectedBinding = 'a' * 64;
  SettingsImport parse(
    String text, {
    String platform = 'windows',
    bool identity = false,
    String? binding,
  }) => SettingsImport.parse(
    text,
    platform: platform,
    installationBinding: binding ?? expectedBinding,
    confirmIdentity: identity,
  );
  try {
    final windows = await File('$fixtures/windows.json').readAsString();
    final android = await File('$fixtures/android.json').readAsString();
    final explicit = await File('$fixtures/windows-identity.json')
        .readAsString();
    await test('browser exports import as whitelisted Dart settings with no credential or default identity', () async {
      final first = parse(windows);
      final second = parse(android, platform: 'android');
      requireMigration(
        first.clientId == null &&
            second.clientId == null &&
            first.settings['fontScalePercent'] == 115 &&
            first.settings['endpoint'] == 'http://127.0.0.1:32145' &&
            first.settings['theme'] == 'dark',
        'whitelist-values',
      );
      requireMigration(
        !first.settings.containsKey('token') &&
            !first.settings.containsKey('automationEnabled'),
        'export-leak',
      );
    });
    await test('identity transfer requires explicit consent and identical source installation', () async {
      await rejected(() => parse(explicit));
      await rejected(() => parse(explicit, identity: true, binding: 'b' * 64));
      await rejected(
        () => parse(explicit, identity: true, platform: 'android'),
      );
      requireMigration(
        parse(explicit, identity: true).clientId ==
            'p0-explicit-old-identity-001',
        'identity-not-preserved',
      );
    });
    await test('future schemas, unknown fields, token injection, corrupt preferences and malformed endpoints reject', () async {
      await rejected(() => parse('{broken'));
      for (final change in <void Function(Map<String, dynamic>)>[
        (value) => value['schemaVersion'] = 2,
        (value) => value['token'] = 'synthetic-forbidden',
        (value) => (value['settings'] as Map)['lease'] = 'forbidden',
        (value) => (value['settings'] as Map)['automationEnabled'] = true,
        (value) => (value['settings'] as Map)['fontScalePercent'] = 116,
        (value) => (value['settings'] as Map)['alwaysOnTop'] = 'true',
        (value) => (value['settings'] as Map)['endpoint'] =
            'http://secret:secret@localhost:32145',
        (value) =>
            (value['settings'] as Map)['endpoint'] = 'file:///tmp/private',
      ]) {
        final value = object(jsonDecode(windows));
        change(value);
        await rejected(() => parse(jsonEncode(value)));
      }
    });
    await test('atomic import preserves old export, existing new settings and separate credentials', () async {
      final directory = await temporary.createTemp('settings-');
      final secret = File('${directory.path}/credentials.synthetic');
      await secret.writeAsString('synthetic-credential-kept');
      final original = await File('$fixtures/windows.json').readAsBytes();
      final files = AtomicMigrationFile(directory);
      final imported = parse(windows);
      final saved = await files.create('preferences.json', {
        'schemaVersion': 1,
        'settings': imported.settings,
      });
      final before = await saved.readAsString();
      await rejected(
        () => files.create('preferences.json', {'schemaVersion': 999}),
      );
      requireMigration(
        await saved.readAsString() == before &&
            await secret.readAsString() == 'synthetic-credential-kept' &&
            base64Encode(await File('$fixtures/windows.json').readAsBytes()) ==
                base64Encode(original),
        'existing-content-changed',
      );
      await rejected(() => files.create('../escape.json', {}));
    });
    await test(
      'new identities are independent of unavailable legacy storage',
      () async {
        final identities = List.generate(100, (_) => newClientId());
        requireMigration(
          identities.toSet().length == 100 &&
              identities.every((id) => RegExp(r'^[a-f0-9]{32}$').hasMatch(id)),
          'identity-generation',
        );
      },
    );

    final defaults = object(
      jsonDecode(await File('$fixtures/defaults-profile.json').readAsString()),
    );
    final oldProfile = object(
      jsonDecode(await File('$fixtures/old-profile.json').readAsString()),
    );
    final host = await FixtureHost.start(repository, fixtures, 'recovery');
    try {
      final directory = await temporary.createTemp('recovery-');
      final client = RecoveryClient(
        host.endpoint,
        'identity-p0-synthetic-token',
        newClientId(),
        AtomicMigrationFile(directory),
      );
      await test('actual Mod router enforces token, registration and exact request shape', () async {
        await httpRejected(
          () =>
              client.request('GET', '/devices', null, 'wrong-synthetic-token'),
          {401},
        );
        await httpRejected(() => client.request('GET', '/devices'), {409});
        await httpRejected(
          () => client.request('POST', '/devices/register', {
            'protocolVersion': 999,
          }),
          {400},
        );
      });
      await test('register new secondary while old primary is offline; defaults do not replace authority', () async {
        await client.register(defaults);
        final state = await client.authority();
        requireMigration(
          state['activeProfileHash'] == profileHash(oldProfile) &&
              state['currentDeviceProfileHash'] == profileHash(defaults),
          'authority-overwritten',
        );
        final old = (state['devices'] as List)
            .cast<Map<String, dynamic>>()
            .singleWhere((item) => item['isPrimary'] == true);
        requireMigration(
          old['online'] == false && old['deviceId'] != client.clientId,
          'old-client-was-online',
        );
        await rejected(() => client.confirmPrimary(userConfirmed: true));
        requireMigration(
          !client.transcript.any((item) => item['path'] == '/devices/primary'),
          'premature-primary-request',
        );
      });
      await test('real sync CAS rejects stale authority and copies offline primary profile without promotion', () async {
        await httpRejected(
          () => client.request('POST', '/devices/sync', {
            'protocolVersion': 1,
            'expectedAuthorityRevision': 999,
            'deviceId': client.clientId,
          }),
          {409},
        );
        final pending = await client.sync();
        requireMigration(
          pending['primaryDeviceId'] == client.originalPrimary &&
              pending['currentDeviceProfileHash'] == profileHash(oldProfile),
          'sync-profile-differs',
        );
        await httpRejected(
          () => client.request('POST', '/devices/primary', {
            'protocolVersion': 1,
            'expectedAuthorityRevision': pending['authorityRevision'],
            'deviceId': client.clientId,
          }),
          {409},
        );
        await httpRejected(
          () => client.request('POST', '/devices/sync-ack', {
            'protocolVersion': 1,
            'syncId': pending['pendingSyncId'],
            'profileRevision': pending['currentDeviceProfileRevision'],
            'profileHash': '0' * 64,
          }),
          {409},
        );
      });
      await test('Android protocol identity with a blocked local apply never sends ACK or promotes', () async {
        final blockedDirectory = await temporary.createTemp('blocked-apply-');
        final sentinel = File('${blockedDirectory.path}/applied-profile.json');
        await sentinel.writeAsString('{preserved-future-or-corrupt');
        final blocked = RecoveryClient(
          host.endpoint,
          'identity-p0-synthetic-token',
          newClientId(),
          AtomicMigrationFile(blockedDirectory),
          platform: 'android',
        );
        await blocked.register(defaults);
        await blocked.sync();
        await rejected(blocked.applyAndAcknowledge);
        requireMigration(
          await sentinel.readAsString() == '{preserved-future-or-corrupt' &&
              blocked.phase == 'sync-pending' &&
              !blocked.transcript.any(
                (item) =>
                    item['path'] == '/devices/sync-ack' ||
                    item['path'] == '/devices/primary',
              ),
          'failed-apply-authorized-state',
        );
        transcripts.add({
          'scenario': 'android-protocol-blocked-apply',
          'requests': blocked.transcript,
        });
      });
      await test('local durable apply/readback precedes exact sync ACK and leaves automation execution disabled', () async {
        await client.applyAndAcknowledge();
        requireMigration(
          client.phase == 'awaiting-user-primary-confirmation',
          'ack-phase',
        );
        final stored = object(
          jsonDecode(
            await File('${directory.path}/applied-profile.json').readAsString(),
          ),
        );
        requireMigration(
          stored['runtimeExecutionEnabled'] == false &&
              stored['profileHash'] == profileHash(oldProfile),
          'runtime-restored-unsafe',
        );
        await rejected(() => client.confirmPrimary(userConfirmed: false));
        requireMigration(
          (await client.authority())['primaryDeviceId'] ==
              client.originalPrimary,
          'implicit-promotion',
        );
      });
      await test('explicit synthetic-user confirmation promotes only the already-applied matching profile', () async {
        await client.confirmPrimary(userConfirmed: true);
        final state = await client.authority();
        requireMigration(
          state['currentDeviceIsPrimary'] == true &&
              state['activeProfileHash'] == profileHash(oldProfile),
          'promotion-damaged-profile',
        );
        requireMigration(
          !client.transcript.any(
            (item) => (item['path'] as String).contains('lease'),
          ),
          'runtime-lease-acquired',
        );
        await rejected(() => client.confirmPrimary(userConfirmed: true));
      });
      transcripts.add({
        'scenario': 'recovery',
        'transport': 'real HTTP / linked unchanged LocalApiServer',
        'requests': client.transcript,
      });
    } finally {
      await host.stop();
    }

    for (final scenario in ['full', 'corrupt', 'future']) {
      final fixture = await FixtureHost.start(repository, fixtures, scenario);
      try {
        await test(
          '$scenario registry refuses recovery without deleting devices or overwriting evidence',
          () async {
            final original = await File(
              '${fixture.root}/companion-devices.json',
            ).readAsBytes();
            final directory = await temporary.createTemp('$scenario-');
            final client = RecoveryClient(
              fixture.endpoint,
              'identity-p0-synthetic-token',
              newClientId(),
              AtomicMigrationFile(directory),
            );
            await httpRejected(
              () => client.register(defaults),
              scenario == 'full' ? {409} : {503},
            );
            requireMigration(
              base64Encode(
                    await File('${fixture.root}/companion-devices.json')
                        .readAsBytes(),
                  ) ==
                  base64Encode(original),
              'blocked-registry-overwritten',
            );
            requireMigration(
              client.phase == 'new-identity' && client.transcript.length == 1,
              'blocked-recovery-continued',
            );
            transcripts.add({
              'scenario': scenario,
              'requests': client.transcript,
            });
          },
        );
      } finally {
        await fixture.stop();
      }
    }
    final report = {
      'schemaVersion': 1,
      'kind': 'flutter-identity-p0',
      'result': 'PASS',
      'feasibilityVerified': true,
      'installedDataMigrationVerified': false,
      'gameRuntimeVerified': false,
      'checks': checks,
      'transcripts': transcripts,
      'boundaries': [
        'Synthetic identities and isolated stores only.',
        'Real unchanged Mod HTTP router and file stores under locked .NET 6; no Unity game runtime.',
        'Browser fixtures exercise Storage API, not installed Windows WebView2/Android storage.',
        'User confirmation is explicit test input, not a change to any real primary device.',
      ],
    };
    await AtomicMigrationFile(Directory(fixtures))
        .create('identity-result.json', report);
    stdout.writeln(
      'PASS identity migration: ${checks.length} checks; real HTTP recovery, no real user state accessed.',
    );
  } finally {
    await temporary.delete(recursive: true);
  }
}

Future<void> rejected(FutureOr<Object?> Function() action) async {
  try {
    await action();
  } on MigrationFailure {
    return;
  } on FormatException {
    return;
  }
  throw const MigrationFailure('expected-rejection');
}

Future<void> httpRejected(
  Future<Object?> Function() action,
  Set<int> statuses,
) async {
  try {
    await action();
  } on ApiFailure catch (error) {
    requireMigration(
      statuses.contains(error.status),
      'unexpected-http-${error.status}',
    );
    return;
  }
  throw const MigrationFailure('expected-http-rejection');
}

class FixtureHost {
  FixtureHost(this.process, this.lines, this.root, this.endpoint, this.errors);
  final Process process;
  final StreamIterator<String> lines;
  final String root;
  final Uri endpoint;
  final Future<String> errors;
  static Future<FixtureHost> start(
    String repository,
    String fixtures,
    String scenario,
  ) async {
    final process = await Process.start('dotnet', [
      '$repository/tests/identity-migration/bin/Release/net6.0/IdentityMigrationHost.dll',
      scenario,
      '$fixtures/old-profile.json',
    ]);
    final errors = process.stderr.transform(utf8.decoder).join();
    final lines = StreamIterator(
      process.stdout.transform(utf8.decoder).transform(const LineSplitter()),
    );
    try {
      if (!await lines.moveNext().timeout(const Duration(seconds: 15))) {
        throw MigrationFailure('host-start-failed:${await errors}');
      }
      final ready = object(jsonDecode(lines.current));
      requireMigration(
        ready['kind'] == 'identity-p0-real-local-api' &&
            ready['scenario'] == scenario &&
            ready['port'] is int &&
            ready['root'] is String,
        'host-descriptor',
      );
      return FixtureHost(
        process,
        lines,
        ready['root'] as String,
        Uri.parse('http://127.0.0.1:${ready['port']}'),
        errors,
      );
    } catch (_) {
      // Only this child is owned. EOF lets its finally clean the fixture even
      // when startup output is malformed; never scan or kill by process name.
      await process.stdin.close();
      await process.exitCode.timeout(
        const Duration(seconds: 10),
        onTimeout: () {
          process.kill();
          return -1;
        },
      );
      await lines.cancel();
      rethrow;
    }
  }

  Future<void> stop() async {
    process.stdin.writeln('stop');
    await process.stdin.flush();
    await process.stdin.close();
    final exit = await process.exitCode.timeout(
      const Duration(seconds: 10),
      onTimeout: () {
        process.kill();
        return -1;
      },
    );
    await lines.cancel();
    requireMigration(exit == 0, 'host-stop-failed:$exit:${await errors}');
    requireMigration(!await Directory(root).exists(), 'host-store-not-cleaned');
  }
}
