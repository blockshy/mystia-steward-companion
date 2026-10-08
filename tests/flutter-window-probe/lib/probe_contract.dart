import 'dart:convert';

const probeBackgroundRgb = 0x2040c0;
const probeContentRgb = 0xe04020;
const probeAlphaValues = [0, 128, 255];
const probeLimitations = [
  'This result does not complete Flutter migration P0.',
  'Only owned fixture processes and their Shell tray are used; no Unity/game focus is tested.',
  'Instance transport uses an isolated test port and synthetic identity; production port coexistence and game toggle policy are not verified.',
  'Real Explorer restart, ordinary-user privilege and clean-machine deployment are not verified.',
  'Keyboard focus uses the non-text F24 key; Chinese IME composition and text input are not verified.',
  'XInput, the full DPI/monitor matrix, Remote Desktop, and other GPUs are not covered.',
  'Text is visible in the specimen; quantitative alpha checks sample solid rectangle interiors.',
];

class ProbeLaunch {
  const ProbeLaunch({required this.runId, required this.resultFile});
  final String runId;
  final String resultFile;

  factory ProbeLaunch.parse(List<String> arguments, String gitSha) {
    if (gitSha.length != 40 || !RegExp(r'^[a-f0-9]+$').hasMatch(gitSha)) {
      throw const FormatException('A full compiled Git SHA is required.');
    }
    if (arguments.length != 7 ||
        arguments[0] != '--probe' ||
        arguments[1] != '--run-id' ||
        arguments[3] != '--suite' ||
        arguments[4] != 'all' ||
        arguments[5] != '--result-file') {
      throw const FormatException(
        'The fixed node probe argument contract is required.',
      );
    }
    final runId = arguments[2];
    if (runId.isEmpty ||
        runId.length > 80 ||
        !RegExp(r'^[A-Za-z0-9][A-Za-z0-9_-]*$').hasMatch(runId) ||
        runId.codeUnits.any((value) => value < 32 || value > 126)) {
      throw const FormatException('Invalid node run ID.');
    }
    final result = arguments[6].replaceAll(r'\', '/');
    final expected = 'D:/dev/mystia-node/runs/$runId/probe-result.json';
    if (result.toLowerCase() != expected.toLowerCase()) {
      throw const FormatException(
        'The result must be the fixed file in this node run.',
      );
    }
    return ProbeLaunch(runId: runId, resultFile: expected);
  }
}

class PaintSpec {
  const PaintSpec(this.revision, this.backgroundAlpha, this.contentAlpha);
  final int revision;
  final int backgroundAlpha;
  final int contentAlpha;

  void validate() {
    if (revision <= 0 ||
        !probeAlphaValues.contains(backgroundAlpha) ||
        !probeAlphaValues.contains(contentAlpha)) {
      throw const FormatException('Invalid fixed alpha specimen.');
    }
  }

  Map<String, Object> toJson() => {
    'revision': revision,
    'backgroundAlpha': backgroundAlpha,
    'contentAlpha': contentAlpha,
  };
}

List<int> rgbChannels(int rgb) => [
  (rgb >> 16) & 255,
  (rgb >> 8) & 255,
  rgb & 255,
];

List<int> composedRgb(PaintSpec spec, int underlay, {required bool content}) {
  spec.validate();
  if (underlay != 0 && underlay != 255) {
    throw const FormatException(
      'Only black/white controlled underlays are supported.',
    );
  }
  final background = rgbChannels(probeBackgroundRgb);
  final foreground = rgbChannels(probeContentRgb);
  final a = spec.backgroundAlpha / 255;
  final b = content ? spec.contentAlpha / 255 : 0.0;
  return List.generate(3, (index) {
    final back = background[index] * a + underlay * (1 - a);
    return (foreground[index] * b + back * (1 - b)).round();
  });
}

bool pixelMatches(int rgb, List<int> expected, {int tolerance = 5}) {
  if (rgb < 0 || rgb > 0xffffff || expected.length != 3) return false;
  final actual = rgbChannels(rgb);
  return List.generate(
    3,
    (index) => (actual[index] - expected[index]).abs(),
  ).every((difference) => difference <= tolerance);
}

class ProbeBlocked implements Exception {
  const ProbeBlocked(this.message);
  final String message;
  @override
  String toString() => message;
}

class FatalProbe implements Exception {
  const FatalProbe(this.message);
  final String message;
  @override
  String toString() => message;
}

class PixelMismatch implements Exception {
  const PixelMismatch(this.message);
  final String message;
  @override
  String toString() => message;
}

final requiredWindowChecks = Set<String>.unmodifiable({
  'compiled-native-dart-identity-and-owned-underlay',
  for (final background in probeAlphaValues)
    for (final content in probeAlphaValues)
      for (final underlay in ['black', 'white'])
        'alpha-$background-$content-$underlay',
  'same-point-interactive-pass-through-restored',
  'hide-restores-interactive',
  'wm-close-hides-and-restores-interactive',
  'keyboard-to-flutter-exact-foreground',
  'keyboard-to-underlay-exact-foreground',
  'topmost-style-and-actual-stacking',
  'registered-f10-real-input-restores-interactive',
});

const requiredLifecycleChecks = {
  'lifecycle-primary-identity-and-single-tray',
  'secondary-show-restores-existing-primary',
  'secondary-toggle-hides-existing-primary',
  'secondary-toggle-restores-existing-primary',
  'secondary-invalid-is-atomically-rejected',
  'tray-click-restores-interactive',
  'tray-menu-enables-passthrough',
  'tray-menu-show-clears-passthrough',
  'tray-menu-exit-observed',
  'lifecycle-relaunch-reuses-port-after-exit',
  'secondary-bare-exit-observed',
};

final requiredProbeChecks = Set<String>.unmodifiable({
  ...requiredWindowChecks,
  ...requiredLifecycleChecks,
});

class ProbeReport {
  ProbeReport(this.launch, this.gitSha) : coreOnlyTest = false;
  ProbeReport.coreTest(this.launch, this.gitSha) : coreOnlyTest = true {
    if (launch.resultFile != 'test-only-never-written') {
      throw ArgumentError('Core-only test reports cannot use a runtime path.');
    }
  }
  final ProbeLaunch launch;
  final String gitSha;
  final bool coreOnlyTest;
  final startedUtc = DateTime.now().toUtc().toIso8601String();
  final context = <String, Object?>{};
  final observations = <Map<String, Object?>>[];
  final checks = <Map<String, Object?>>[];
  final errors = <String>[];
  String status = 'FAIL';

  void check(String name, bool passed, {String? detail}) {
    checks.add({
      'name': name,
      'status': passed ? 'PASS' : 'FAIL',
      'detail': detail,
    });
    if (!passed) {
      throw StateError(
        'Check failed: $name${detail == null ? '' : ' ($detail)'}',
      );
    }
  }

  String encode() {
    final required = coreOnlyTest ? requiredWindowChecks : requiredProbeChecks;
    final names = checks.map((value) => value['name']).toSet();
    if (!['PASS', 'FAIL', 'BLOCKED'].contains(status) ||
        (status == 'PASS' &&
            (checks.length != required.length ||
                names.length != required.length ||
                !names.containsAll(required) ||
                errors.isNotEmpty ||
                checks.any((value) => value['status'] != 'PASS')))) {
      throw StateError('Invalid probe report outcome.');
    }
    final json = jsonEncode({
      'schemaVersion': 1,
      'kind': coreOnlyTest
          ? 'flutter-window-probe-core-test'
          : 'flutter-window-composition-input-probe',
      'runId': launch.runId,
      'gitSha': gitSha,
      'status': status,
      'p0Verified': false,
      'executionMode': coreOnlyTest
          ? 'mock-core-only'
          : 'automatic-controlled-windows-desktop',
      'startedUtc': startedUtc,
      'finishedUtc': DateTime.now().toUtc().toIso8601String(),
      'context': context,
      'observations': observations,
      'checks': checks,
      'errors': errors,
      'limitations': probeLimitations,
    });
    if (utf8.encode(json).length > 1048576) {
      throw StateError('Probe report exceeds 1 MiB.');
    }
    return json;
  }
}
