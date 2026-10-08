import 'dart:convert';

import 'generated/control_probe_api.g.dart';

class ControlFailure implements Exception {
  const ControlFailure(this.message);
  final String message;
  @override
  String toString() => message;
}

class ControlBlocked extends ControlFailure {
  const ControlBlocked(super.message);
}

void requireControl(bool condition, String message) {
  if (!condition) throw ControlFailure(message);
}

bool legacyPublicationReady(Object? value, int gamePid) {
  requireControl(
    value is Map<String, dynamic>,
    'Missing legacy publication observation.',
  );
  final detail = value as Map<String, dynamic>;
  requireControl(
    detail['kind'] == 'original-mod-cached-snapshot-publication' &&
        detail['started'] is bool &&
        detail['ready'] is bool &&
        detail['businessReadinessClaimed'] == false &&
        detail['foregroundGrantClaimed'] == false,
    'Invalid legacy publication scope.',
  );
  for (final key in [
    'gamePid',
    'requestCount',
    'responseCount',
    'completedMonotonicMs',
  ]) {
    requireControl(
      detail[key] is int && (detail[key] as int) >= 0,
      'Invalid legacy publication counter.',
    );
  }
  for (final key in ['startedUtcFileTime', 'capturedUtcFileTime']) {
    requireControl(
      detail[key] is String &&
          RegExp(r'^(0|[1-9][0-9]{0,17})$').hasMatch(detail[key] as String),
      'Invalid legacy publication timestamp.',
    );
  }
  requireControl(
    (detail['responseCount'] as int) <= (detail['requestCount'] as int) &&
        (detail['requestCount'] as int) <= 300,
    'Legacy publication request accounting differs.',
  );
  if (detail['started'] == true) {
    requireControl(
      detail['gamePid'] == gamePid &&
          gamePid > 0 &&
          int.parse(detail['startedUtcFileTime'] as String) > 0,
      'Legacy publication game/focus identity differs.',
    );
  }
  if (detail['ready'] != true) return false;
  final captured = detail['capturedAtUtc'];
  requireControl(
    detail['started'] == true &&
        (detail['responseCount'] as int) > 0 &&
        (detail['completedMonotonicMs'] as int) > 0 &&
        captured is String &&
        captured.endsWith('Z') &&
        DateTime.tryParse(captured)?.isUtc == true &&
        int.parse(detail['capturedUtcFileTime'] as String) >
            int.parse(detail['startedUtcFileTime'] as String),
    'Legacy publication is stale or lacks a real post-focus response.',
  );
  return true;
}

class ControlLaunch {
  const ControlLaunch(this.runId);
  final String runId;
  factory ControlLaunch.client(
    List<String> args,
    String gitSha, {
    bool legacy = false,
  }) {
    if (!RegExp(r'^[a-f0-9]{40}$').hasMatch(gitSha) ||
        args.length != 3 ||
        args[0] != (legacy ? '--control-legacy-client' : '--control-client') ||
        !RegExp(r'^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$').hasMatch(args[1]) ||
        !(legacy ? ['1'] : ['1', '2']).contains(args[2])) {
      throw const FormatException('Invalid sanitized client generation.');
    }
    return ControlLaunch(args[1]);
  }
  factory ControlLaunch.parse(List<String> args, String gitSha) {
    if (!RegExp(r'^[a-f0-9]{40}$').hasMatch(gitSha) ||
        args.length != 7 ||
        args[0] != '--probe' ||
        args[1] != '--run-id' ||
        args[3] != '--suite' ||
        args[4] != 'hotkey' ||
        args[5] != '--result-file' ||
        !RegExp(r'^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$').hasMatch(args[2]) ||
        args[6].replaceAll(r'\', '/').toLowerCase() !=
            'd:/dev/mystia-node/runs/${args[2]}/probe-result.json'
                .toLowerCase()) {
      throw const FormatException('Invalid fixed real-Mod control invocation.');
    }
    return ControlLaunch(args[2]);
  }
}

const requiredControlChecks = {
  'control-real-mod-registered-identity',
  'control-f8-game-to-flutter',
  'control-native-and-dart-input',
  'control-f8-flutter-to-game-visible',
  'control-f8-hidden-recovery',
  'control-f8-passthrough-recovery',
  'control-rs-game-to-flutter',
  'control-rs-held-no-bounce',
  'control-rs-flutter-to-game-visible',
  'control-retained-game-close-exit-zero',
};

const requiredLegacyControlChecks = {
  'legacy-real-mod-launch',
  'legacy-startup-show',
  'legacy-native-and-dart-input',
  'legacy-existing-instance-toggle',
  'legacy-click-recovery',
  'legacy-retained-game-close-exit-zero',
};

class ControlState {
  ControlState._(this.value);
  final Map<String, dynamic> value;
  int number(String name) => value[name] as int;
  bool flag(String name) => value[name] as bool;
  Map<String, dynamic> get legacy =>
      value['legacyControl'] as Map<String, dynamic>;
  List<Map<String, dynamic>> get slots =>
      (value['slots'] as List).cast<Map<String, dynamic>>();
  List<Map<String, dynamic>> get connected =>
      slots.where((slot) => slot['error'] == 0).toList();
  bool get rightStickHeld =>
      connected.length == 1 &&
      ((connected.single['buttons'] as int) & 0x80) != 0;
  bool get neutral =>
      connected.length == 1 &&
      connected.single['buttons'] == 0 &&
      (connected.single['leftTrigger'] as int) <= 30 &&
      (connected.single['rightTrigger'] as int) <= 30 &&
      (connected.single['thumbLX'] as int).abs() <= 7849 &&
      (connected.single['thumbLY'] as int).abs() <= 7849 &&
      (connected.single['thumbRX'] as int).abs() <= 8689 &&
      (connected.single['thumbRY'] as int).abs() <= 8689;

  factory ControlState.parse(
    ControlSnapshot snapshot,
    String sha,
    String runId,
    int processId,
    int previousSequence, {
    bool legacy = false,
  }) {
    requireControl(
      snapshot.gitSha == sha &&
          snapshot.processId == processId &&
          processId > 0 &&
          snapshot.sequence > previousSequence,
      'Native compiled/process identity or observation sequence changed.',
    );
    requireControl(
      utf8.encode(snapshot.evidenceJson).length <= 1048576,
      'Native control snapshot exceeds 1 MiB.',
    );
    final decoded = jsonDecode(snapshot.evidenceJson);
    requireControl(
      decoded is Map<String, dynamic>,
      'Native snapshot is not an object.',
    );
    final value = decoded as Map<String, dynamic>;
    requireControl(
      value['schemaVersion'] == 1 &&
          value['kind'] == 'real-mod-control-native' &&
          value['runId'] == runId &&
          (value['error'] == null || value['error'] is String),
      'Native control snapshot schema/run identity differs.',
    );
    for (final key in [
      'errorBlocked',
      'gameReady',
      'gameWindowBound',
      'gameAlive',
      'gameForeground',
      'gameFocusOwned',
      'registered',
      'activationPending',
      'returnPending',
      'visible',
      'interactive',
      'clientForeground',
      'childFocused',
      'flutterFocused',
      'pipeConnected',
      'pipeEof',
      'exitReceived',
      'injectedF8Held',
      'rsArmed',
      'closeRequested',
    ]) {
      requireControl(value[key] is bool, 'Invalid native flag: $key.');
    }
    for (final key in [
      'gamePid',
      'gameThreadId',
      'registrationCount',
      'protocolRequestId',
      'inputSequence',
      'source',
      'activationCount',
      'activationAttempts',
      'focusGameCount',
      'f8ReturnCount',
      'rsReturnCount',
      'rsEdgeCount',
      'nativeF8Down',
      'nativeF8Up',
      'rawF8Down',
      'rawF8Up',
      'markerF8Down',
      'markerF8Up',
      'nativeF24Down',
      'nativeMouseDown',
      'nativeMouseUp',
      'clientHwnd',
      'childHwnd',
      'clientThreadId',
      'foregroundHwnd',
      'foregroundPid',
      'listenerOwnerPid',
      'legacyUnsupportedCount',
      'injectedDownCount',
      'injectedUpCount',
      'lastSendRequested',
      'lastSendInserted',
      'lastSendError',
    ]) {
      requireControl(
        value[key] is int && (value[key] as int) >= 0,
        'Invalid native counter: $key.',
      );
    }
    for (final key in ['gameHwnd', 'gameExitCode', 'selectedSlot']) {
      requireControl(
        value[key] == null || (value[key] is int && (value[key] as int) >= 0),
        'Invalid nullable native integer: $key.',
      );
    }
    requireControl(
      value['gameCreationHex'] is String &&
          RegExp(r'^[a-f0-9]+$').hasMatch(value['gameCreationHex'] as String) &&
          value['rsUnavailableReason'] is String &&
          value['events'] is List &&
          value['closeMessage'] is Map<String, dynamic>,
      'Native identity/diagnostic evidence is missing.',
    );
    requireControl(
      value['slots'] is List && (value['slots'] as List).length == 4,
      'Native XInput evidence must contain four slots.',
    );
    final slots = value['slots'] as List;
    for (var index = 0; index < 4; index++) {
      final slot = slots[index];
      requireControl(
        slot is Map<String, dynamic> &&
            slot['index'] == index &&
            (slot['error'] == 0 || slot['error'] == 1167),
        'Invalid XInput slot identity/error.',
      );
      for (final key in [
        'packet',
        'buttons',
        'leftTrigger',
        'rightTrigger',
        'thumbLX',
        'thumbLY',
        'thumbRX',
        'thumbRY',
      ]) {
        requireControl(slot[key] is int, 'XInput integer is missing: $key.');
      }
      requireControl(
        (slot['packet'] as int) >= 0 &&
            (slot['packet'] as int) <= 0xffffffff &&
            (slot['buttons'] as int) >= 0 &&
            (slot['buttons'] as int) <= 0xffff &&
            <int>[
              slot['leftTrigger'] as int,
              slot['rightTrigger'] as int,
            ].every((n) => n >= 0 && n <= 255) &&
            <int>[
              slot['thumbLX'] as int,
              slot['thumbLY'] as int,
              slot['thumbRX'] as int,
              slot['thumbRY'] as int,
            ].every((n) => n >= -32768 && n <= 32767),
        'Invalid raw XInput range.',
      );
    }
    requireControl(
      value['clientForeground'] != true || value['foregroundPid'] == processId,
      'Client foreground identity contradicts the retained process.',
    );
    requireControl(
      value['gameForeground'] != true ||
          (value['foregroundPid'] == value['gamePid'] &&
              (value['gamePid'] as int) > 0),
      'Game foreground identity contradicts the retained process.',
    );
    requireControl(
      !(value['gameForeground'] == true && value['clientForeground'] == true),
      'Two different processes cannot own the same foreground.',
    );
    requireControl(
      value['gameReady'] != true ||
          (value['registered'] == true &&
              value['registrationCount'] == 1 &&
              value['gameWindowBound'] == true &&
              (value['gameThreadId'] as int) > 0),
      'Game readiness lacks the actual Mod Update registration.',
    );
    if (legacy) {
      final detail = value['legacyControl'];
      requireControl(
        detail is Map<String, dynamic> &&
            detail['scenario'] == 'old-mod-legacy-client',
        'Legacy snapshot lacks its explicit original-Mod scenario.',
      );
      for (final key in [
        'ready',
        'startupAttempted',
        'automaticForeground',
        'clickRequired',
        'asfwCalled',
        'automatedOsInput',
      ]) {
        requireControl(detail[key] is bool, 'Invalid legacy flag: $key.');
      }
      for (final key in [
        'showCount',
        'toggleCount',
        'exitCount',
        'clickCount',
        'backgroundClickCount',
      ]) {
        requireControl(
          detail[key] is int && (detail[key] as int) >= 0,
          'Invalid legacy counter: $key.',
        );
      }
      requireControl(
        value['registered'] == false &&
            value['gameReady'] == false &&
            value['pipeConnected'] == false &&
            value['registrationCount'] == 0 &&
            value['activationCount'] == 0 &&
            detail['asfwCalled'] == false &&
            detail['automatedOsInput'] == true &&
            (detail['ready'] != true ||
                (detail['showCount'] == 1 &&
                    detail['startupAttempted'] == true &&
                    value['gameWindowBound'] == true)),
        'Legacy raw readiness cannot claim an MSC1 registration or foreground grant.',
      );
      legacyPublicationReady(
        detail['snapshotPublication'],
        value['gamePid'] as int,
      );
    } else {
      requireControl(
        value['legacyControl'] == null,
        'Legacy scenario reached the MSC1 runner.',
      );
    }
    return ControlState._(value);
  }
}

class ControlReport {
  ControlReport(
    this.launch,
    this.gitSha, {
    this.clientGeneration = 0,
    this.legacy = false,
  }) {
    requireControl(
      [0, 1, 2].contains(clientGeneration) &&
          (!legacy || clientGeneration == 1),
      'Unknown client generation.',
    );
  }
  final ControlLaunch launch;
  final String gitSha;
  final int clientGeneration;
  final bool legacy;
  Set<String> get requiredChecks => legacy
      ? requiredLegacyControlChecks
      : clientGeneration == 0
      ? requiredControlChecks
      : {
          'client-real-mod-launch',
          'client-msc1-registration',
          'client-activation',
          'client-native-and-dart-input',
          'client-dart-return',
          clientGeneration == 1
              ? 'client-retired-game-alive'
              : 'client-exit-notified',
        };
  final String startedUtc = DateTime.now().toUtc().toIso8601String();
  final context = <String, Object?>{};
  final checks = <Map<String, Object?>>[];
  final observations = <Map<String, Object?>>[];
  final errors = <String>[];
  String status = 'FAIL';

  void check(String name, {String status = 'PASS', String? detail}) {
    requireControl(
      requiredChecks.contains(name) &&
          !checks.any((item) => item['name'] == name) &&
          ['PASS', 'FAIL', 'BLOCKED'].contains(status),
      'Unknown/duplicate control check.',
    );
    checks.add({'name': name, 'status': status, 'detail': detail});
  }

  void observe(String label, ControlState state, Map<String, Object> ui) {
    requireControl(observations.length < 64, 'Too many control observations.');
    observations.add({
      'label': label,
      'observationUtc': DateTime.now().toUtc().toIso8601String(),
      'native': state.value,
      'flutterUi': ui,
    });
  }

  String encode() {
    final names = checks.map((item) => item['name']).toSet();
    requireControl(
      ['PASS', 'FAIL', 'BLOCKED'].contains(status) &&
          checks.length == names.length &&
          names.every(requiredChecks.contains) &&
          checks.every(
            (item) => ['PASS', 'FAIL', 'BLOCKED'].contains(item['status']),
          ),
      'Invalid control report checks.',
    );
    requireControl(
      status != 'PASS' ||
          (names.length == requiredChecks.length &&
              errors.isEmpty &&
              checks.every((item) => item['status'] == 'PASS')),
      'A partial or failed control suite cannot report PASS.',
    );
    final json = jsonEncode({
      'schemaVersion': 1,
      'kind': legacy
          ? 'flutter-legacy-control-client'
          : clientGeneration == 0
          ? 'flutter-control-probe'
          : 'flutter-control-client',
      if (clientGeneration != 0) 'generation': clientGeneration,
      'suite': 'hotkey',
      'runId': launch.runId,
      'gitSha': gitSha,
      'status': status,
      'p0Verified': false,
      'executionMode': legacy
          ? 'original-mod-legacy-tcp-and-automated-os-click'
          : clientGeneration == 0
          ? 'real-mod-update-f8-and-user-operated-rs'
          : 'real-mod-launched-client-generation',
      'startedUtc': startedUtc,
      'finishedUtc': DateTime.now().toUtc().toIso8601String(),
      'context': context,
      'checks': checks,
      'observations': observations,
      'errors': errors,
      'limitations': [
        if (legacy) ...[
          'The unchanged Mod 1.3.1 launched this host; raw EOF show/toggle are observed without MSC1 or ASFW.',
          'Startup automatic foreground and subsequent automated marked OS mouse-click recovery are recorded separately; no manual user click is claimed.',
          'Raw exit is an optional observation after our exact game close, not an authenticated shutdown acknowledgement.',
        ] else if (clientGeneration != 0) ...[
          'This client was launched by the real copied Mod; it is a P0 host, not the finished product.',
          'This generation validates its own registration and lifetime; cross-generation claims require the controller report.',
          'Ordinary-user privilege, old-Mod compatibility and the release matrix are separate acceptance work.',
        ] else ...[
          'This suite uses a new Mod DLL and an already running P0 Flutter host, not the unchanged Mod 1.3.1 or a completed product client.',
          'The default LegacyTcp path, first-launch activation, legacy raw TCP messages and ordinary-user privilege cases are separate acceptance work.',
          'F8 input is bounded Win32 SendInput targeting the retained foreground pair; RS is operated by the user and observed through system XInput and the actual Mod Update input path.',
        ],
        'XInput slots cannot distinguish physical versus virtual device provenance. Disconnect/reconnect and all controller models are not covered.',
        'No gameplay actions, Chinese IME, all DPI values, installation/update operations or completion of P0 are claimed.',
      ],
    });
    requireControl(
      utf8.encode(json).length <= 1048576,
      'Control report exceeds 1 MiB.',
    );
    return json;
  }
}
