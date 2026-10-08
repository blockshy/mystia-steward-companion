import 'dart:convert';

import 'generated/input_probe_api.g.dart';

enum InputSuite { xinput, focus }

class InputProbeFailure implements Exception {
  const InputProbeFailure(this.message);
  final String message;
  @override
  String toString() => message;
}

class InputProbeBlocked extends InputProbeFailure {
  const InputProbeBlocked(super.message);
}

void requireInput(bool condition, String message) {
  if (!condition) throw InputProbeFailure(message);
}

class InputProbeLaunch {
  const InputProbeLaunch(this.runId, this.resultFile, this.suite);
  final String runId;
  final String resultFile;
  final InputSuite suite;

  factory InputProbeLaunch.parse(List<String> arguments, String gitSha) {
    if (gitSha.length != 40 ||
        !RegExp(r'^[a-f0-9]{40}$').hasMatch(gitSha) ||
        arguments.length != 7 ||
        arguments[0] != '--probe' ||
        arguments[1] != '--run-id' ||
        arguments[3] != '--suite' ||
        arguments[5] != '--result-file' ||
        !['focus', 'xinput'].contains(arguments[4])) {
      throw const FormatException('Invalid fixed input probe invocation.');
    }
    final runId = arguments[2];
    if (!RegExp(r'^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$').hasMatch(runId) ||
        runId.codeUnits.any((value) => value < 32 || value > 126)) {
      throw const FormatException('Invalid input probe run ID.');
    }
    final path = 'D:/dev/mystia-node/runs/$runId/probe-result.json';
    if (arguments[6].replaceAll(r'\', '/').toLowerCase() !=
        path.toLowerCase()) {
      throw const FormatException(
        'The result must be the fixed node run file.',
      );
    }
    return InputProbeLaunch(
      runId,
      path,
      InputSuite.values.byName(arguments[4]),
    );
  }
}

const requiredXInputChecks = {
  'xinput-system-api-and-process-identity',
  'xinput-four-slot-state-contract',
  'xinput-neutral-gate',
  'xinput-rs-hold-single-edge',
  'xinput-release-neutral-gate',
  'xinput-second-press-and-release',
};

const requiredFocusChecks = {
  'focus-compiled-and-retained-game-identity',
  'focus-game-to-probe-exact-foreground',
  'focus-probe-native-and-flutter-input',
  'focus-probe-to-game-keeps-visible',
  'focus-game-foreground-then-hide-probe',
  'focus-hidden-probe-restores-interaction',
  'focus-passthrough-restores-interaction',
  'focus-retained-game-close-exit-zero',
};

class InputProbeReport {
  InputProbeReport(this.launch, this.gitSha);
  final InputProbeLaunch launch;
  final String gitSha;
  final String startedUtc = DateTime.now().toUtc().toIso8601String();
  final context = <String, Object?>{};
  final observations = <Map<String, Object?>>[];
  final checks = <Map<String, Object?>>[];
  final errors = <String>[];
  String status = 'FAIL';

  Set<String> get required => launch.suite == InputSuite.xinput
      ? requiredXInputChecks
      : requiredFocusChecks;

  void check(String name, {String status = 'PASS', String? detail}) {
    requireInput(required.contains(name), 'Unknown input probe check.');
    requireInput(
      !checks.any((check) => check['name'] == name),
      'Duplicate check.',
    );
    requireInput(
      ['PASS', 'FAIL', 'BLOCKED'].contains(status),
      'Invalid check status.',
    );
    checks.add({'name': name, 'status': status, 'detail': detail});
  }

  void observe(String label, Map<String, Object?> value) {
    requireInput(
      observations.length < 512,
      'Input evidence exceeded its bounded observation count.',
    );
    observations.add({
      'label': label,
      'observationUtc': DateTime.now().toUtc().toIso8601String(),
      ...value,
    });
  }

  String encode() {
    final names = checks.map((check) => check['name']).toSet();
    requireInput(
      ['PASS', 'FAIL', 'BLOCKED'].contains(status) &&
          checks.length == names.length &&
          names.every(required.contains) &&
          checks.every(
            (check) => ['PASS', 'FAIL', 'BLOCKED'].contains(check['status']),
          ),
      'Invalid input probe report checks.',
    );
    requireInput(
      status != 'PASS' ||
          (checks.length == required.length &&
              names.containsAll(required) &&
              checks.every((check) => check['status'] == 'PASS') &&
              errors.isEmpty),
      'A partial or failed input suite cannot report PASS.',
    );
    final encoded = jsonEncode({
      'schemaVersion': 1,
      'kind': 'flutter-${launch.suite.name}-probe',
      'suite': launch.suite.name,
      'runId': launch.runId,
      'gitSha': gitSha,
      'status': status,
      'p0Verified': false,
      'executionMode': launch.suite == InputSuite.xinput
          ? 'user-operated-controller-observed-through-xinput'
          : 'automatic-cooperative-game-window-focus',
      'startedUtc': startedUtc,
      'finishedUtc': DateTime.now().toUtc().toIso8601String(),
      'context': context,
      'observations': observations,
      'checks': checks,
      'errors': errors,
      'limitations': [
        'This independent suite does not rerun or replace the 37 window/lifecycle checks and does not complete P0.',
        if (launch.suite == InputSuite.xinput) ...[
          'XInput identifies logical slots, not physical versus virtual device provenance; operation is requested from the user, with no synthetic input installed or injected by this probe.',
          'No controller input is routed to the game, old client, control port, or production actions.',
          'Neutral uses probe-only native integer deadzones (left axis 7849, right axis 8689, trigger 30); it is not the old product normalized 0.4 gate or a completed input-engine migration/differential test.',
          'Physical disconnect/reconnect, all controller models, navigation mappings, and vibration are not verified by this press/hold/release sequence.',
        ] else ...[
          'Game-to-probe activation uses a dedicated cooperative test plugin and a fresh identity-bound foreground grant for each request; it is not the original Mod F8/RS launch or focus path.',
          'Foreground/focus observations do not prove Unity consumed a gameplay key or that the old Mod F8/RS control path was exercised.',
          'Only the exact prepared game process/window is targeted; this does not verify arbitrary game instances, production control ports, or game business behavior.',
          'Flutter focus uses non-text F24; Chinese IME composition and text editing are not verified.',
        ],
        'Ordinary-user privileges, all DPI/monitor configurations, and other GPUs remain separate acceptance work.',
      ],
    });
    requireInput(
      utf8.encode(encoded).length <= 1048576,
      'Input probe report exceeds 1 MiB.',
    );
    return encoded;
  }
}

Map<String, Object?> xinputJson(XInputSnapshot snapshot) => {
  'nativeGitSha': snapshot.nativeGitSha,
  'processId': snapshot.processId,
  'sequence': snapshot.sequence,
  'monotonicMicros': snapshot.monotonicMicros,
  'systemDllPath': snapshot.systemDllPath,
  'foregroundProcessId': snapshot.foregroundProcessId,
  'probeFocused': snapshot.probeFocused,
  'slots': [
    for (final slot in snapshot.slots)
      {
        'index': slot.index,
        'resultCode': slot.resultCode,
        'state': slot.state == null
            ? null
            : {
                'packetNumber': slot.state!.packetNumber,
                'buttons': slot.state!.buttons,
                'leftTrigger': slot.state!.leftTrigger,
                'rightTrigger': slot.state!.rightTrigger,
                'thumbLX': slot.state!.thumbLX,
                'thumbLY': slot.state!.thumbLY,
                'thumbRX': slot.state!.thumbRX,
                'thumbRY': slot.state!.thumbRY,
              },
      },
  ],
};

void validateXInputSnapshot(
  XInputSnapshot snapshot,
  String sha,
  int processId,
) {
  requireInput(
    snapshot.nativeGitSha == sha &&
        snapshot.processId == processId &&
        processId > 0,
    'XInput compiled/process identity differs.',
  );
  requireInput(
    snapshot.sequence > 0 &&
        snapshot.monotonicMicros >= 0 &&
        snapshot.foregroundProcessId >= 0,
    'XInput observation counters are invalid.',
  );
  final path = snapshot.systemDllPath.replaceAll(r'\', '/');
  final parts = path.split('/');
  requireInput(
    parts.length >= 3 &&
        RegExp(r'^[A-Za-z]:$').hasMatch(parts.first) &&
        parts[parts.length - 2].toLowerCase() == 'system32' &&
        parts.last.toLowerCase() == 'xinput1_4.dll' &&
        !parts.any((part) => part == '..' || part == '.' || part.isEmpty) &&
        !path.codeUnits.any((value) => value < 32),
    'The bound system XInput DLL path is invalid.',
  );
  requireInput(
    !snapshot.probeFocused || snapshot.foregroundProcessId == processId,
    'XInput focus evidence is contradictory.',
  );
  requireInput(
    snapshot.slots.length == 4 &&
        snapshot.slots.map((slot) => slot.index).toSet().containsAll([
          0,
          1,
          2,
          3,
        ]),
    'XInput must report each of the four logical slots exactly once.',
  );
  for (final slot in snapshot.slots) {
    requireInput(
      slot.resultCode >= 0 && slot.resultCode <= 0xffffffff,
      'Invalid XInput return code.',
    );
    if (slot.resultCode != 0) {
      requireInput(
        slot.state == null,
        'An unsuccessful slot must not expose fabricated state.',
      );
      requireInput(
        slot.resultCode == 1167,
        'Unexpected XInput API failure ${slot.resultCode}.',
      );
      continue;
    }
    final state = slot.state;
    requireInput(state != null, 'Successful XInput query omitted its state.');
    requireInput(
      state!.packetNumber >= 0 &&
          state.packetNumber <= 0xffffffff &&
          state.buttons >= 0 &&
          state.buttons <= 0xffff &&
          [
            state.leftTrigger,
            state.rightTrigger,
          ].every((value) => value >= 0 && value <= 255) &&
          [
            state.thumbLX,
            state.thumbLY,
            state.thumbRX,
            state.thumbRY,
          ].every((value) => value >= -32768 && value <= 32767),
      'XInput state is outside the native integer ranges.',
    );
  }
}
