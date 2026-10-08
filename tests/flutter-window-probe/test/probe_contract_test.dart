import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/probe_contract.dart';

void main() {
  const sha = '283bd56cd10564d64169a8ea521f9fdffe0019b4';
  const arguments = [
    '--probe',
    '--run-id',
    'window-01',
    '--suite',
    'all',
    '--result-file',
    r'D:\dev\mystia-node\runs\window-01\probe-result.json',
  ];

  test(
    'fixed node launch rejects ambiguous paths, flags and build identity',
    () {
      expect(ProbeLaunch.parse(arguments, sha).runId, 'window-01');
      for (final changed in [
        [...arguments, '--extra'],
        [...arguments.take(4), 'arbitrary', ...arguments.skip(5)],
        [
          ...arguments.take(6),
          r'D:\dev\mystia-node\runs\other\probe-result.json',
        ],
        [
          ...arguments.take(6),
          r'D:\dev\mystia-node\runs\window-01\..\probe-result.json',
        ],
        [
          ...arguments.take(6),
          r'D:\dev\mystia-node\runs\window-01\probe-result.json:stream',
        ],
        ['--probe', '--run-id', 'window-01\n', ...arguments.skip(3)],
      ]) {
        expect(() => ProbeLaunch.parse(changed, sha), throwsFormatException);
      }
      expect(() => ProbeLaunch.parse(arguments, 'dev'), throwsFormatException);
    },
  );

  test(
    'opaque content is independent of background and real pixel mismatch fails',
    () {
      for (final background in probeAlphaValues) {
        for (final underlay in [0, 255]) {
          expect(
            composedRgb(PaintSpec(1, background, 255), underlay, content: true),
            rgbChannels(probeContentRgb),
          );
        }
      }
      expect(composedRgb(const PaintSpec(1, 0, 0), 255, content: false), [
        255,
        255,
        255,
      ]);
      expect(pixelMatches(0xe04020, [224, 64, 32]), isTrue);
      expect(pixelMatches(0, [224, 64, 32]), isFalse);
      expect(pixelMatches(-1, [0, 0, 0]), isFalse);
      expect(
        () => const PaintSpec(1, 80, 255).validate(),
        throwsFormatException,
      );
    },
  );

  test(
    'report cannot claim PASS without checks or while failures are present',
    () {
      final report = ProbeReport(ProbeLaunch.parse(arguments, sha), sha)
        ..status = 'PASS';
      expect(report.encode, throwsStateError);
      report.check('native-observation', true);
      expect(report.encode, throwsStateError);
      report.checks.clear();
      for (final name in requiredWindowChecks) {
        report.check(name, true);
      }
      expect(report.encode, throwsStateError);
      expect(
        () => ProbeReport.coreTest(ProbeLaunch.parse(arguments, sha), sha),
        throwsArgumentError,
      );
      report.checks.clear();
      for (final name in requiredProbeChecks) {
        report.check(name, true);
      }
      final json = jsonDecode(report.encode()) as Map<String, Object?>;
      expect(json['p0Verified'], false);
      expect(json['limitations'], isNotEmpty);
      final hotkey = report.checks.removeLast();
      expect(report.encode, throwsStateError);
      report.checks.add(hotkey);
      report.checks.add(hotkey);
      expect(report.encode, throwsStateError);
      report.checks.removeLast();
      expect(() => report.check('screen-pixel', false), throwsStateError);
      expect(report.encode, throwsStateError);
    },
  );
}
