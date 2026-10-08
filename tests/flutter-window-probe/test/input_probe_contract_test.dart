import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/input_probe_contract.dart';

import 'input_probe_fixtures.dart';

void main() {
  test('input launch isolates suites and rejects alternate result paths', () {
    final args = [
      '--probe',
      '--run-id',
      'input-01',
      '--suite',
      'xinput',
      '--result-file',
      r'D:\dev\mystia-node\runs\input-01\probe-result.json',
    ];
    expect(InputProbeLaunch.parse(args, inputSha).suite, InputSuite.xinput);
    expect(
      InputProbeLaunch.parse([
        ...args.take(4),
        'focus',
        ...args.skip(5),
      ], inputSha).suite,
      InputSuite.focus,
    );
    for (final invalid in [
      [...args.take(4), 'all', ...args.skip(5)],
      [
        ...args.take(6),
        r'D:\dev\mystia-node\runs\input-01\..\probe-result.json',
      ],
      [...args, '--game-pid', '200'],
    ]) {
      expect(
        () => InputProbeLaunch.parse(invalid, inputSha),
        throwsFormatException,
      );
    }
    expect(
      () => InputProbeLaunch.parse(args, '$inputSha\n'),
      throwsFormatException,
    );
  });

  for (final suite in InputSuite.values) {
    test(
      '$suite report cannot downgrade its mandatory set or accept partial success',
      () {
        final report = InputProbeReport(
          InputProbeLaunch('test', 'test-only', suite),
          inputSha,
        )..status = 'PASS';
        final original = report.required.length;
        expect(report.encode, throwsA(isA<InputProbeFailure>()));
        for (final name in report.required) {
          report.check(name);
        }
        final json = jsonDecode(report.encode()) as Map<String, dynamic>;
        expect(json['kind'], 'flutter-${suite.name}-probe');
        expect(json['p0Verified'], false);
        final removed = report.checks.removeLast();
        expect(report.encode, throwsA(isA<InputProbeFailure>()));
        report.checks.add(removed);
        report.checks.add(removed);
        expect(report.encode, throwsA(isA<InputProbeFailure>()));
        report.checks.removeLast();
        report.checks.last['status'] = 'BLOCKED';
        expect(report.encode, throwsA(isA<InputProbeFailure>()));
        report.status = 'BLOCKED';
        expect(jsonDecode(report.encode())['status'], 'BLOCKED');
        expect(report.required.length, original);
      },
    );
  }

  test('four exact slots and native result/state ranges are mandatory', () {
    validateXInputSnapshot(inputSample(1, 0), inputSha, 100);
    validateXInputSnapshot(inputSample(1, 0, connected: false), inputSha, 100);
    // Each case mutates a fresh real typed DTO, never a source-string assertion.
    for (var caseId = 0; caseId < 7; caseId++) {
      final state = inputSample(1, 0);
      switch (caseId) {
        case 0:
          state.slots.removeLast();
        case 1:
          state.slots[3].index = 0;
        case 2:
          state.slots[0].resultCode = 1167;
        case 3:
          state.slots[0].state = null;
        case 4:
          state.slots[1].resultCode = 5;
        case 5:
          state.slots[0].state!.thumbLX = -32769;
        case 6:
          state.slots[0].state!.packetNumber = 0x100000000;
      }
      expect(
        () => validateXInputSnapshot(state, inputSha, 100),
        throwsA(isA<InputProbeFailure>()),
      );
    }
  });
}
