import 'dart:convert';
import 'dart:ui' show ViewFocusDirection, ViewFocusEvent, ViewFocusState;

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/control_probe_contract.dart';
import 'package:mystia_steward_companion_window_probe/control_probe_runner.dart';
import 'package:mystia_steward_companion_window_probe/control_probe_view.dart';
import 'package:mystia_steward_companion_window_probe/generated/control_probe_api.g.dart';

const sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
const runId = 'fixture-control-1';
Map<String, dynamic> publication({bool ready = false}) => {
  'kind': 'original-mod-cached-snapshot-publication',
  'started': ready,
  'ready': ready,
  'gamePid': ready ? 17 : 0,
  'startedUtcFileTime': ready ? '134358048000000000' : '0',
  'capturedUtcFileTime': ready ? '134358048010000000' : '0',
  'requestCount': ready ? 1 : 0,
  'responseCount': ready ? 1 : 0,
  'capturedAtUtc': ready ? '2026-10-08T00:00:01Z' : null,
  'completedMonotonicMs': ready ? 1000 : 0,
  'businessReadinessClaimed': false,
  'foregroundGrantClaimed': false,
};
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
  'errorBlocked': false,
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
  bool legacy = false,
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
  legacy: legacy,
);

void main() {
  testWidgets(
    'cold MSC1 first frame and progress updates issue no native view-focus request',
    (tester) async {
      final dispatcher = tester.binding.platformDispatcher;
      dispatcher.resetFocusedViewTestValues();
      addTearDown(dispatcher.resetFocusedViewTestValues);
      final ui = ControlUi(deferNativeFocus: true);
      await tester.pumpWidget(ControlProbeApp(ui: ui));
      await tester.pumpAndSettle();
      ui.show('真实注册已完成，正在等待游戏授权');
      await tester.pumpAndSettle();
      expect(dispatcher.testFocusEvents, isEmpty);
      expect(ui.focused, false);
      await tester.sendKeyEvent(LogicalKeyboardKey.f24, platform: 'windows');
      expect(ui.f24Down, 0);
      // Synthetic native-focus delivery is explicit. This proves framework
      // ordering, not Windows permission or an actual game foreground grant.
      dispatcher.onViewFocusChange?.call(
        ViewFocusEvent(
          viewId: tester.view.viewId,
          state: ViewFocusState.focused,
          direction: ViewFocusDirection.undefined,
        ),
      );
      ui.allowFocusAfterNativeActivation();
      await tester.pumpAndSettle();
      expect(ui.focused, true);
      await tester.sendKeyEvent(LogicalKeyboardKey.f24, platform: 'windows');
      expect(ui.f24Down, 1);
    },
  );
  testWidgets('ordinary and legacy hosts retain their initial autofocus', (
    tester,
  ) async {
    final dispatcher = tester.binding.platformDispatcher;
    dispatcher.resetFocusedViewTestValues();
    addTearDown(dispatcher.resetFocusedViewTestValues);
    final ui = ControlUi();
    await tester.pumpWidget(ControlProbeApp(ui: ui));
    await tester.pumpAndSettle();
    expect(ui.focusRequestsEnabled, true);
    expect(ui.focused, true);
    expect(dispatcher.testFocusEvents, isNotEmpty);
  });
  for (final changed in <String, Object?>{
    'none': null,
    'activationCount': 0,
    'source': 1,
    'activationPending': true,
    'clientForeground': false,
    'childFocused': false,
    'visible': false,
    'interactive': false,
  }.entries) {
    test(
      'Dart focus gate requires consumed real activation: ${changed.key}',
      () async {
        final ui = ControlUi(deferNativeFocus: true);
        final value = nativeFixture()
          ..addAll({
            'registered': true,
            'registrationCount': 1,
            'gameWindowBound': true,
            'gameThreadId': 19,
            'activationCount': 1,
            'source': 0,
            'clientForeground': true,
            'foregroundPid': 7,
            'childFocused': true,
            'visible': true,
            'interactive': true,
          });
        if (changed.key != 'none') value[changed.key] = changed.value;
        var calls = 0;
        final runner = ControlRunner(
          report: ControlReport(ControlLaunch(runId), sha, clientGeneration: 1),
          ui: ui,
          processId: 7,
          frame: () async {},
          execute: (_) async {
            if (++calls > 1) {
              throw const ControlFailure(
                'synthetic stop after native observation',
              );
            }
            return ControlSnapshot(
              gitSha: sha,
              processId: 7,
              sequence: calls,
              evidenceJson: jsonEncode(value),
            );
          },
        );
        await expectLater(runner.runClient(), throwsA(isA<ControlFailure>()));
        expect(ui.focusRequestsEnabled, changed.key == 'none');
      },
    );
  }
  for (final automatic in [true, false]) {
    test(
      'legacy runner preserves EOF toggle and click ordering (startup automatic=$automatic)',
      () async {
        final report = ControlReport(
          ControlLaunch(runId),
          sha,
          clientGeneration: 1,
          legacy: true,
        );
        final ui = ControlUi();
        final value = nativeFixture()
          ..addAll({
            'gamePid': 17,
            'gameThreadId': 19,
            'gameWindowBound': true,
            'gameAlive': true,
            'gameFocusOwned': true,
            'visible': true,
            'interactive': true,
            'clientGeneration': 1,
            'modLaunchedClient': true,
            'listenerOwnerPid': 7,
            'legacyControl': <String, dynamic>{
              'scenario': 'old-mod-legacy-client',
              'ready': true,
              'showCount': 1,
              'toggleCount': 0,
              'exitCount': 0,
              'startupAttempted': true,
              'automaticForeground': automatic,
              'clickRequired': !automatic,
              'clickCount': 0,
              'backgroundClickCount': 0,
              'asfwCalled': false,
              'automatedOsInput': true,
              'snapshotPublication': publication(),
            },
          });
        final legacy = value['legacyControl'] as Map<String, dynamic>;
        final operations = <ControlOperation>[];
        var sequence = 0, commandId = 0;
        void foreground(bool client) {
          value['clientForeground'] = client;
          value['gameForeground'] = !client;
          value['childFocused'] = client;
          value['foregroundPid'] = client ? 7 : 17;
          ui.focused = client;
        }

        foreground(automatic);
        final runner = ControlRunner(
          report: report,
          ui: ui,
          processId: 7,
          frame: () async {},
          execute: (command) async {
            if (command.operation == ControlOperation.inspect) {
              expect(command.requestId, commandId);
            } else {
              expect(command.requestId, ++commandId);
              operations.add(command.operation);
            }
            switch (command.operation) {
              case ControlOperation.initialize:
              case ControlOperation.inspect:
                break;
              case ControlOperation.clickProbe:
                if (legacy['clickRequired'] == true) {
                  expect(value['gameForeground'], true);
                  legacy['backgroundClickCount'] =
                      (legacy['backgroundClickCount'] as int) + 1;
                }
                expect(value['injectedF8Held'], false);
                foreground(true);
                legacy['clickRequired'] = false;
                legacy['clickCount'] = (legacy['clickCount'] as int) + 1;
                value['nativeMouseDown'] = legacy['clickCount'];
                value['nativeMouseUp'] = legacy['clickCount'];
                ui.clicked();
              case ControlOperation.sendFocusKey:
                expect(ui.focused, true);
                value['nativeF24Down'] = (value['nativeF24Down'] as int) + 1;
                ui.f24Down++;
              case ControlOperation.focusGame:
                foreground(false);
                value['focusGameCount'] = (value['focusGameCount'] as int) + 1;
                legacy['snapshotPublication'] = publication(ready: true);
              case ControlOperation.pressF8:
                expect(value['gameForeground'], true);
                expect(
                  legacyPublicationReady(legacy['snapshotPublication'], 17),
                  true,
                );
                value['injectedF8Held'] = true;
                value['injectedDownCount'] = 1;
                legacy['toggleCount'] = 1;
                legacy['clickRequired'] = true;
              case ControlOperation.releaseF8:
                expect(legacy['toggleCount'], 1);
                expect(value['gameForeground'], true);
                value['injectedF8Held'] = false;
                value['injectedUpCount'] = 1;
              case ControlOperation.closeGame:
                expect(value['gameForeground'], true);
                expect(value['focusGameCount'], 2);
                value['closeRequested'] = true;
                value['gameAlive'] = false;
                value['gameExitCode'] = 0;
              default:
                fail('Unexpected legacy action: ${command.operation}');
            }
            return ControlSnapshot(
              gitSha: sha,
              processId: 7,
              sequence: ++sequence,
              evidenceJson: jsonEncode(value),
            );
          },
        );
        await runner.runLegacyClient();
        expect(
          report.checks.map((check) => check['name']).toSet(),
          requiredLegacyControlChecks,
        );
        expect(report.context['startupAutomaticForeground'], automatic);
        expect(operations, [
          ControlOperation.initialize,
          ControlOperation.clickProbe,
          ControlOperation.sendFocusKey,
          ControlOperation.focusGame,
          ControlOperation.pressF8,
          ControlOperation.releaseF8,
          ControlOperation.clickProbe,
          ControlOperation.sendFocusKey,
          ControlOperation.focusGame,
          ControlOperation.closeGame,
        ]);
        expect(ui.onF8, null);
      },
    );
  }
  test('legacy sanitized role is separate and only permits its original generation', () {
    expect(
      ControlLaunch.client(
        ['--control-legacy-client', runId, '1'],
        sha,
        legacy: true,
      ).runId,
      runId,
    );
    for (final invalid in [
      ['--control-legacy-client', runId, '2'],
      ['--control-client', runId, '1'],
      ['--control-legacy-client', runId, '1', '--token=credential'],
    ]) {
      expect(
        () => ControlLaunch.client(invalid, sha, legacy: true),
        throwsFormatException,
      );
    }
    expect(
      () => ControlLaunch.client(['--control-legacy-client', runId, '1'], sha),
      throwsFormatException,
    );
  });
  test('legacy raw readiness cannot masquerade as actual Mod Update registration or ASFW', () {
    Map<String, dynamic> fixture() => nativeFixture()
      ..['gameWindowBound'] = true
      ..['legacyControl'] = <String, dynamic>{
        'scenario': 'old-mod-legacy-client',
        'ready': true,
        'showCount': 1,
        'toggleCount': 0,
        'exitCount': 0,
        'startupAttempted': true,
        'automaticForeground': false,
        'clickRequired': true,
        'clickCount': 0,
        'backgroundClickCount': 0,
        'asfwCalled': false,
        'automatedOsInput': true,
        'snapshotPublication': publication(),
      };
    expect(parse(fixture(), legacy: true).legacy['clickRequired'], true);
    expect(() => parse(fixture()), throwsA(isA<ControlFailure>()));
    expect(
      () => parse(nativeFixture(), legacy: true),
      throwsA(isA<ControlFailure>()),
    );
    for (final key in ['registered', 'pipeConnected', 'gameReady']) {
      expect(
        () => parse(fixture()..[key] = true, legacy: true),
        throwsA(isA<ControlFailure>()),
      );
    }
    for (final change in <String, Object>{
      'asfwCalled': true,
      'showCount': 0,
      'startupAttempted': false,
      'clickCount': -1,
    }.entries) {
      final value = fixture();
      (value['legacyControl'] as Map)[change.key] = change.value;
      expect(() => parse(value, legacy: true), throwsA(isA<ControlFailure>()));
    }
  });
  test('legacy publication is only positive evidence after exact focus, not a heartbeat or grant', () {
    expect(legacyPublicationReady(publication(), 17), false);
    expect(legacyPublicationReady(publication(ready: true), 17), true);
    for (final entry in <String, Object?>{
      'started': false,
      'gamePid': 18,
      'capturedUtcFileTime': '134358048000000000',
      'capturedAtUtc': 'unknown',
      'responseCount': 0,
      'businessReadinessClaimed': true,
      'foregroundGrantClaimed': true,
    }.entries) {
      expect(
        () => legacyPublicationReady(
          publication(ready: true)..[entry.key] = entry.value,
          17,
        ),
        throwsA(isA<ControlFailure>()),
      );
    }
    final stale = publication(ready: true)
      ..['ready'] = false
      ..['capturedUtcFileTime'] = '134358048000000000';
    expect(legacyPublicationReady(stale, 17), false);
  });
  for (final blocked in [false, true]) {
    test(
      'native first-error classification survives polling: blocked=$blocked',
      () async {
        final runner = ControlRunner(
          report: ControlReport(ControlLaunch(runId), sha, clientGeneration: 1),
          ui: ControlUi(deferNativeFocus: true),
          processId: 7,
          frame: () async {},
          execute: (_) async => ControlSnapshot(
            gitSha: sha,
            processId: 7,
            sequence: 1,
            evidenceJson: jsonEncode(
              nativeFixture()
                ..['error'] = 'bounded native observation stopped'
                ..['errorBlocked'] = blocked,
            ),
          ),
        );
        await expectLater(
          runner.runClient(),
          throwsA(
            predicate<Object>(
              (error) =>
                  error.runtimeType ==
                  (blocked ? ControlBlocked : ControlFailure),
            ),
          ),
        );
      },
    );
  }
  test('legacy PASS requires all six separate checks and reports automated click limits', () {
    final report = ControlReport(
      ControlLaunch(runId),
      sha,
      clientGeneration: 1,
      legacy: true,
    )..status = 'PASS';
    expect(report.encode, throwsA(isA<ControlFailure>()));
    expect(
      () => report.check('client-msc1-registration'),
      throwsA(isA<ControlFailure>()),
    );
    for (final check in requiredLegacyControlChecks) {
      report.check(check);
    }
    final decoded = jsonDecode(report.encode()) as Map<String, dynamic>;
    expect(decoded['kind'], 'flutter-legacy-control-client');
    expect(
      decoded['executionMode'],
      'original-mod-legacy-tcp-and-automated-os-click',
    );
    expect(decoded['p0Verified'], false);
    expect(
      () => ControlReport(
        ControlLaunch(runId),
        sha,
        clientGeneration: 2,
        legacy: true,
      ),
      throwsA(isA<ControlFailure>()),
    );
  });
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
