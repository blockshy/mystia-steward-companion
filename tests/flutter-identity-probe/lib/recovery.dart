import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'migration.dart';

class ApiFailure implements Exception {
  ApiFailure(this.status);
  final int status;
  @override
  String toString() => 'identity-api-http-$status';
}

class RecoveryClient {
  RecoveryClient(
    this.endpoint,
    this.token,
    this.clientId,
    this.files, {
    this.platform = 'windows',
  }) {
    requireMigration(
      endpoint.scheme == 'http' &&
          endpoint.host == '127.0.0.1' &&
          endpoint.port >= 1024 &&
          ['windows', 'android'].contains(platform),
      'probe-loopback-only',
    );
  }
  final Uri endpoint;
  final String token, clientId;
  final String platform;
  final AtomicMigrationFile files;
  final List<Map<String, Object>> transcript = [];
  String phase = 'new-identity';
  String? registryId, originalPrimary, appliedHash;
  int? appliedRevision;
  Map<String, dynamic>? latest;

  Future<Map<String, dynamic>> request(
    String method,
    String path, [
    Map<String, dynamic>? body,
    String? overrideToken,
  ]) async {
    final client = HttpClient()..connectionTimeout = const Duration(seconds: 3);
    try {
      final request = await client
          .openUrl(method, endpoint.resolve(path))
          .timeout(const Duration(seconds: 3));
      request.followRedirects = false;
      request.headers.set(
        'X-Mystia-Steward-Companion-Token',
        overrideToken ?? token,
      );
      request.headers.set('X-Mystia-Steward-Companion-Client-Id', clientId);
      request.headers.set(
        'X-Mystia-Steward-Companion-Client-Label',
        'Synthetic Flutter P0',
      );
      if (body != null) {
        final encoded = utf8.encode(jsonEncode(body));
        request.headers.contentType = ContentType.json;
        // The current Mod TCP HTTP parser requires a bounded Content-Length body.
        request.contentLength = encoded.length;
        request.add(encoded);
      }
      final response = await request.close().timeout(
        const Duration(seconds: 3),
      );
      final bytes = <int>[];
      await for (final chunk in response.timeout(const Duration(seconds: 3))) {
        bytes.addAll(chunk);
        requireMigration(bytes.length <= 131072, 'response-too-large');
      }
      transcript.add({
        'method': method,
        'path': path,
        'status': response.statusCode,
      });
      if (response.statusCode != 200) throw ApiFailure(response.statusCode);
      return object(jsonDecode(utf8.decode(bytes)));
    } finally {
      client.close(force: true);
    }
  }

  Map<String, dynamic> validate(Map<String, dynamic> state) {
    requireMigration(
      state['ok'] == true &&
          state['protocolVersion'] == 1 &&
          state['profileSchemaVersion'] == 4 &&
          state['currentDeviceId'] == clientId &&
          state['registryId'] is String &&
          state['authorityRevision'] is int &&
          state['authorityRevision'] > 0 &&
          state['currentDeviceProfileRevision'] is int &&
          state['currentDeviceProfileRevision'] > 0,
      'authority-contract',
    );
    if (registryId != null)
      requireMigration(state['registryId'] == registryId, 'registry-changed');
    requireMigration(
      profileHash(object(state['currentDeviceProfile'])) ==
              state['currentDeviceProfileHash'] &&
          profileHash(object(state['activeProfile'])) ==
              state['activeProfileHash'],
      'profile-hash',
    );
    latest = state;
    return state;
  }

  Future<void> register(Map<String, dynamic> safeDefaults) async {
    requireMigration(
      phase == 'new-identity' && safeDefaults['automationEnabled'] == false,
      'registration-phase',
    );
    final state = validate(
      await request('POST', '/devices/register', {
        'protocolVersion': 1,
        'profileSchemaVersion': 4,
        'platform': platform,
        'appVersion': 'flutter-p0',
        'profile': safeDefaults,
      }),
    );
    requireMigration(
      state['currentDeviceIsPrimary'] == false &&
          state['primaryDeviceId'] != clientId,
      'recovery-needs-existing-primary-registry',
    );
    registryId = state['registryId'] as String;
    originalPrimary = state['primaryDeviceId'] as String;
    phase = 'registered-secondary';
  }

  Future<Map<String, dynamic>> authority() async =>
      validate(await request('GET', '/devices'));

  Future<Map<String, dynamic>> sync() async {
    requireMigration(phase == 'registered-secondary', 'sync-phase');
    final current = await authority();
    requireMigration(
      current['primaryDeviceId'] == originalPrimary,
      'primary-changed-before-sync',
    );
    final state = validate(
      await request('POST', '/devices/sync', {
        'protocolVersion': 1,
        'expectedAuthorityRevision': current['authorityRevision'],
        'deviceId': clientId,
      }),
    );
    requireMigration(
      state['pendingSyncId'] is String &&
          (state['pendingSyncId'] as String).isNotEmpty &&
          state['currentDeviceProfileHash'] == state['activeProfileHash'] &&
          state['currentDeviceIsPrimary'] == false,
      'sync-not-pending',
    );
    phase = 'sync-pending';
    return state;
  }

  Future<void> applyAndAcknowledge() async {
    requireMigration(phase == 'sync-pending', 'apply-phase');
    final pending = latest!;
    final profile = object(pending['currentDeviceProfile']);
    final hash = profileHash(profile);
    requireMigration(
      hash == pending['currentDeviceProfileHash'],
      'apply-profile-hash',
    );
    final file = await files.create('applied-profile.json', {
      'schemaVersion': 1,
      'registryId': registryId,
      'clientId': clientId,
      'profileRevision': pending['currentDeviceProfileRevision'],
      'profileHash': hash,
      'profile': profile,
      'runtimeExecutionEnabled': false,
    });
    final applied = object(jsonDecode(await file.readAsString()));
    requireMigration(
      profileHash(object(applied['profile'])) == hash &&
          applied['runtimeExecutionEnabled'] == false,
      'applied-readback',
    );
    appliedHash = hash;
    appliedRevision = pending['currentDeviceProfileRevision'] as int;
    phase = 'applied-awaiting-ack';
    // A failed/unknown ACK is not automatically retried and never authorizes promotion.
    final acknowledged = validate(
      await request('POST', '/devices/sync-ack', {
        'protocolVersion': 1,
        'syncId': pending['pendingSyncId'],
        'profileRevision': appliedRevision,
        'profileHash': appliedHash,
      }),
    );
    requireMigration(
      acknowledged['pendingSyncId'] == null &&
          acknowledged['currentDeviceProfileHash'] == appliedHash &&
          acknowledged['currentDeviceProfileRevision'] == appliedRevision &&
          acknowledged['currentDeviceIsPrimary'] == false,
      'ack-not-applied',
    );
    final device = (acknowledged['devices'] as List)
        .cast<Map<String, dynamic>>()
        .singleWhere((item) => item['deviceId'] == clientId);
    requireMigration(
      device['appliedProfileRevision'] == appliedRevision &&
          device['syncPending'] == false,
      'ack-device-state',
    );
    phase = 'awaiting-user-primary-confirmation';
  }

  Future<void> confirmPrimary({required bool userConfirmed}) async {
    requireMigration(
      userConfirmed && phase == 'awaiting-user-primary-confirmation',
      'explicit-primary-confirmation-required',
    );
    final current = await authority();
    requireMigration(
      current['primaryDeviceId'] == originalPrimary &&
          current['pendingSyncId'] == null &&
          current['currentDeviceProfileHash'] == appliedHash &&
          current['activeProfileHash'] == appliedHash &&
          current['currentDeviceProfileRevision'] == appliedRevision,
      'confirmation-state-changed',
    );
    final state = validate(
      await request('POST', '/devices/primary', {
        'protocolVersion': 1,
        'expectedAuthorityRevision': current['authorityRevision'],
        'deviceId': clientId,
      }),
    );
    requireMigration(
      state['currentDeviceIsPrimary'] == true &&
          state['primaryDeviceId'] == clientId &&
          state['activeProfileHash'] == appliedHash,
      'primary-not-confirmed',
    );
    phase = 'primary-restored-runtime-still-disabled';
  }
}
