import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/control_probe_contract.dart';
import 'package:mystia_steward_companion_window_probe/control_probe_view.dart';
import 'package:mystia_steward_companion_window_probe/generated/control_probe_api.g.dart';

const sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const runId = 'fixture-control-1';
final args = [
  '--probe',
  '--run-id',
  runId,
  '--suite',
  'hotkey',
  '--result-file',
  'D:/dev/mystia-node/runs/$runId/probe-result.json',
];

Map<String, dynamic> nativeFixture() => {
  'schemaVersion': 1,
  'kind': 'real-mod-control-native',
  'runId': runId,
  'error': null,
  'gameCreationHex': '1dd000123',
  'rsUnavailableReason': '',
  'events': <Object>[],
  'closeMessage': <String, Object>{},
  'gameHwnd': null,
  'gameExitCode': null,
  'selectedSlot': null,
  for (final key in [
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
  ])
    key: false,
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
  ])
    key: 0,
  'slots': [
    for (var i = 0; i < 4; i++)
      {
        'index': i,
        'error': 1167,
        'packet': 0,
        'buttons': 0,
        'leftTrigger': 0,
        'rightTrigger': 0,
        'thumbLX': 0,
        'thumbLY': 0,
        'thumbRX': 0,
        'thumbRY': 0,
      },
  ],
};
ControlState parse(
  Map<String, dynamic> value, {
  int sequence = 1,
  int previous = 0,
  String nativeSha = sha,
  int nativePid = 7,
}) => ControlState.parse(
  ControlSnapshot(
    gitSha: nativeSha,
    processId: nativePid,
    sequence: sequence,
    evidenceJson: jsonEncode(value),
  ),
  sha,
  runId,
  7,
  previous,
);

void main() {
  test('sanitized cold client invocation rejects credentials and unknown generations', () {
    for (final generation in ['1', '2']) {
      expect(
        ControlLaunch.client([
          '--control-client',
          runId,
          generation,
        ], sha).runId,
        runId,
      );
    }
    for (final bad in [
      ['--control-client', runId, '0'],
      ['--control-client', '../escape', '1'],
      ['--control-client', runId, '3'],
      ['--api=http://127.0.0.1:32755', runId, '1'],
      ['--control-client', runId, '1', '--token=secret'],
    ]) {
      expect(() => ControlLaunch.client(bad, sha), throwsFormatException);
    }
  });
  test(
    'retirement and exit generations cannot substitute each others checks',
    () {
      for (final generation in [1, 2]) {
        final report = ControlReport(
          ControlLaunch(runId),
          sha,
          clientGeneration: generation,
        )..status = 'PASS';
        for (final name in report.requiredChecks.take(5)) {
          report.check(name);
        }
        expect(report.encode, throwsA(isA<ControlFailure>()));
        expect(
          () => report.check(
            generation == 1
                ? 'client-exit-notified'
                : 'client-retired-game-alive',
          ),
          throwsA(isA<ControlFailure>()),
        );
        report.check(report.requiredChecks.last);
        final encoded = jsonDecode(report.encode()) as Map<String, dynamic>;
        expect(encoded['kind'], 'flutter-control-client');
        expect(encoded['generation'], generation);
        expect(encoded['p0Verified'], false);
      }
      expect(
        () => ControlReport(ControlLaunch(runId), sha, clientGeneration: 3),
        throwsA(isA<ControlFailure>()),
      );
    },
  );
  test('only the fixed hotkey node role and complete commit are accepted', () {
    expect(ControlLaunch.parse(args, sha).runId, runId);
    for (final changed in [
      [...args]..[4] = 'focus',
      [...args]..[2] = '../escape',
      [...args]..[6] = 'D:/elsewhere/probe-result.json',
      [...args, '--api=token'],
    ]) {
      expect(() => ControlLaunch.parse(changed, sha), throwsFormatException);
    }
    expect(() => ControlLaunch.parse(args, 'a' * 7), throwsFormatException);
  });
  test(
    'identity, sequence and contradictory foreground evidence are rejected',
    () {
      expect(parse(nativeFixture()).flag('registered'), isFalse);
      expect(
        () => parse(nativeFixture(), nativeSha: 'b' * 40),
        throwsA(isA<ControlFailure>()),
      );
      expect(
        () => parse(nativeFixture(), nativePid: 8),
        throwsA(isA<ControlFailure>()),
      );
      expect(
        () => parse(nativeFixture(), sequence: 1, previous: 1),
        throwsA(isA<ControlFailure>()),
      );
      expect(
        () => parse(nativeFixture()..['clientForeground'] = true),
        throwsA(isA<ControlFailure>()),
      );
      expect(
        () => parse(nativeFixture()..['gameReady'] = true),
        throwsA(isA<ControlFailure>()),
      );
    },
  );
  test(
    'RS neutral requires one connected slot and raw axis/trigger deadzones',
    () {
      final value = nativeFixture();
      final slots = value['slots'] as List;
      slots[0]['error'] = 0;
      expect(parse(value).neutral, isTrue);
      slots[0]['buttons'] = 0x80;
      expect(parse(value).rightStickHeld, isTrue);
      expect(parse(value).neutral, isFalse);
      slots[1]['error'] = 0;
      expect(parse(value).rightStickHeld, isFalse);
      slots[1]['error'] = 1167;
      slots[0]['buttons'] = 0;
      slots[0]['thumbRX'] = 8690;
      expect(parse(value).neutral, isFalse);
      slots[0]['thumbRX'] = 8689;
      slots[0]['rightTrigger'] = 31;
      expect(parse(value).neutral, isFalse);
      slots[0]['rightTrigger'] = 256;
      expect(() => parse(value), throwsA(isA<ControlFailure>()));
    },
  );
  test(
    'missing/duplicated XInput slots and malformed counters cannot pass',
    () {
      final duplicated = nativeFixture();
      (duplicated['slots'] as List)[1]['index'] = 0;
      expect(() => parse(duplicated), throwsA(isA<ControlFailure>()));
      expect(
        () => parse(nativeFixture()..remove('activationCount')),
        throwsA(isA<ControlFailure>()),
      );
      expect(
        () => parse(nativeFixture()..['activationCount'] = -1),
        throwsA(isA<ControlFailure>()),
      );
    },
  );
  test('partial checks, duplicate checks and errors prevent PASS', () {
    final report = ControlReport(ControlLaunch.parse(args, sha), sha)
      ..status = 'PASS';
    expect(report.encode, throwsA(isA<ControlFailure>()));
    for (final name in requiredControlChecks) {
      report.check(name);
    }
    expect(jsonDecode(report.encode())['p0Verified'], isFalse);
    expect(
      () => report.check(requiredControlChecks.first),
      throwsA(isA<ControlFailure>()),
    );
    report.errors.add('late failure');
    expect(report.encode, throwsA(isA<ControlFailure>()));
  });
  test(
    'Flutter F8 dispatch ignores repeats, up and synthesized state repair',
    () {
      final ui = ControlUi();
      final dispatched = <int>[];
      ui.onF8 = dispatched.add;
      ui.key(
        const KeyDownEvent(
          physicalKey: PhysicalKeyboardKey.f8,
          logicalKey: LogicalKeyboardKey.f8,
          timeStamp: Duration.zero,
          synthesized: true,
        ),
      );
      ui.key(
        const KeyDownEvent(
          physicalKey: PhysicalKeyboardKey.f8,
          logicalKey: LogicalKeyboardKey.f8,
          timeStamp: Duration.zero,
        ),
      );
      ui.key(
        const KeyRepeatEvent(
          physicalKey: PhysicalKeyboardKey.f8,
          logicalKey: LogicalKeyboardKey.f8,
          timeStamp: Duration(milliseconds: 100),
        ),
      );
      ui.key(
        const KeyUpEvent(
          physicalKey: PhysicalKeyboardKey.f8,
          logicalKey: LogicalKeyboardKey.f8,
          timeStamp: Duration(milliseconds: 200),
        ),
      );
      expect(dispatched, [1]);
      expect(ui.f8Down, 1);
    },
  );
  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('controller prompt fits ${width.toInt()}px', (tester) async {
      tester.view.physicalSize = Size(width, 700);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final ui = ControlUi()
        ..show('请再次向下按住 RS；游戏获得焦点后继续保持至少两秒，再松开。测试会自动关闭游戏副本。');
      await tester.pumpWidget(ControlProbeApp(ui: ui));
      await tester.pump();
      expect(tester.takeException(), isNull);
      expect(find.textContaining('再次向下按住'), findsOneWidget);
    });
  }
}
