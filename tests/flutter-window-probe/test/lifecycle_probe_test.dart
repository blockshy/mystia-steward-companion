import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/generated/window_api.g.dart';
import 'package:mystia_steward_companion_window_probe/lifecycle_fixture_view.dart';
import 'package:mystia_steward_companion_window_probe/lifecycle_probe_runner.dart';
import 'package:mystia_steward_companion_window_probe/probe_contract.dart';

const sha = '283bd56cd10564d64169a8ea521f9fdffe0019b4';

LifecycleSnapshot snapshot(int sequence) => LifecycleSnapshot(
  generation: 1,
  observationSequence: sequence,
  controllerProcessId: 100,
  primaryProcessId: 200,
  primaryAlive: true,
  primaryExecutableMatched: true,
  primaryWindowCount: 1,
  nativeGitSha: sha,
  primaryGitSha: sha,
  topHwnd: 10,
  childHwnd: 11,
  visible: true,
  inputMode: ProbeInputMode.interactive,
  foregroundHwnd: 10,
  foregroundProcessId: 200,
  focusHwnd: 11,
  mouseDown: 0,
  mouseUp: 0,
  keyDown: 0,
  uiReady: true,
  uiPointerDown: 0,
  uiKeyDown: 0,
  uiFocused: true,
  uiSequence: 1,
  trayAdded: true,
  trayVersioned: true,
  trayDeleted: false,
  trayCallbackCount: 0,
  trayMenuCommandCount: 0,
  port: 41001,
  controlApplied: 0,
  controlRejected: 0,
  receiveCalls: 0,
  pendingBytes: 0,
  connectionUpdates: 0,
  activations: 0,
  gamePid: 0,
  endpointMatched: false,
  tokenMatched: false,
  secondaryRequestId: 0,
  secondaryDone: false,
  secondaryBindError: 0,
  secondaryServerPid: 0,
  secondaryBytesSent: 0,
  secondaryWindowCount: 0,
  secondaryError: 0,
  diagnosticsJson: jsonEncode({
    'primaryErrorCode': 0,
    'fixtureSequence': 0,
    'fixtureAck': 0,
    'focusHandoffSequence': 0,
    'focusHandoffAck': 0,
  }),
);

void main() {
  for (final failure in [
    'wrong-identity',
    'unknown-secondary',
    'missing-dart-click',
  ]) {
    test(
      'lifecycle $failure stops without replay or unobserved PASS',
      () async {
        final report = ProbeReport(
          const ProbeLaunch(
            runId: 'test',
            resultFile: 'test-only-never-written',
          ),
          sha,
        );
        final operations = <LifecycleOperation>[];
        var sequence = 0;
        var mode = ProbeInputMode.interactive;
        var visible = true;
        var applied = false;
        var clicked = false;
        var clickPolls = 0;
        var peerExitPolls = 0;
        final runner = LifecycleProbeRunner(
          report: report,
          controllerProcessId: 100,
          execute: (command) async {
            operations.add(command.operation);
            if (command.operation == LifecycleOperation.secondaryShow &&
                failure == 'unknown-secondary') {
              throw TimeoutException('unknown TCP operation result');
            }
            if (command.operation == LifecycleOperation.setPassThrough) {
              mode = ProbeInputMode.passThrough;
            }
            if (command.operation == LifecycleOperation.hidePrimary) {
              visible = false;
            }
            if (command.operation == LifecycleOperation.secondaryShow) {
              applied = true;
              visible = true;
              mode = ProbeInputMode.interactive;
            }
            if (command.operation == LifecycleOperation.clickPrimary) {
              clicked = true;
            }
            if (clicked &&
                command.operation == LifecycleOperation.inspect &&
                ++clickPolls == 2) {
              throw PlatformException(
                code: 'lifecycle_failed',
                message: 'controlled test observation ended',
              );
            }
            final state = snapshot(++sequence)
              ..visible = visible
              ..inputMode = mode;
            if (!visible) {
              state
                ..foregroundProcessId = 100
                ..diagnosticsJson = jsonEncode({
                  'primaryErrorCode': 0,
                  'fixtureSequence': 0,
                  'fixtureAck': 0,
                  'focusHandoffSequence': 1,
                  'focusHandoffAck': 1,
                });
            }
            if (command.operation == LifecycleOperation.startPrimary) {
              state
                ..port = 0
                ..uiReady = false
                ..primaryGitSha = ''
                ..topHwnd = null
                ..childHwnd = null
                ..visible = null
                ..inputMode = null
                ..primaryWindowCount = 0;
            }
            if (failure == 'wrong-identity') state.controllerProcessId = 101;
            if (applied) {
              state
                ..controlApplied = 1
                ..controlLastAction = LifecycleControlAction.show
                ..connectionUpdates = 1
                ..activations = 0
                ..gamePid = 100
                ..endpointMatched = true
                ..tokenMatched = true
                ..secondaryPid = 300
                ..secondaryAction = LifecycleControlAction.show
                ..secondaryRequestId = 1
                ..secondaryDone = true
                ..secondaryExitCode = 0
                ..secondaryBindError = 10013
                ..secondaryServerPid = 200
                ..secondaryBytesSent = 80;
              if (command.operation == LifecycleOperation.secondaryShow) {
                state
                  ..secondaryDone = false
                  ..secondaryWindowCount = -1
                  ..secondaryExitCode = null;
              } else if (command.operation == LifecycleOperation.inspect &&
                  peerExitPolls++ == 0) {
                // Published peer evidence precedes actual process exit.
                state.secondaryExitCode = null;
              }
            }
            if (clicked) {
              state
                ..mouseDown = 1
                ..mouseUp = 1;
            }
            if (command.operation == LifecycleOperation.abort) {
              state
                ..primaryAlive = false
                ..primaryExitCode = 0
                ..topHwnd = null
                ..childHwnd = null
                ..visible = null
                ..inputMode = null
                ..focusHwnd = null;
            }
            return state;
          },
        );
        await expectLater(runner.run(), throwsA(isA<FatalProbe>()));
        expect(operations.last, LifecycleOperation.abort);
        expect(
          operations.where(
            (operation) => operation == LifecycleOperation.secondaryShow,
          ),
          hasLength(failure == 'wrong-identity' ? 0 : 1),
        );
        expect(operations, isNot(contains(LifecycleOperation.sendFocusKey)));
        expect(report.checks.last['status'], 'FAIL');
        expect(
          report.checks.where(
            (check) =>
                check['name'] == 'secondary-show-restores-existing-primary' &&
                check['status'] == 'PASS',
          ),
          isEmpty,
        );
        if (failure == 'missing-dart-click') {
          expect(peerExitPolls, greaterThanOrEqualTo(2));
          expect(clickPolls, 2);
          expect(
            operations.where(
              (operation) => operation == LifecycleOperation.clickPrimary,
            ),
            hasLength(1),
          );
        }
      },
    );
  }

  for (final (cause, trayDeleted, accepted) in [
    (1, true, true),
    (2, true, true),
    (3, true, true),
    (0, true, false),
    (1, false, false),
  ]) {
    test(
      'closing cause $cause with trayDeleted=$trayDeleted is only an observation',
      () async {
        final report = ProbeReport(
          const ProbeLaunch(
            runId: 'test',
            resultFile: 'test-only-never-written',
          ),
          sha,
        );
        final operations = <LifecycleOperation>[];
        var sequence = 0;
        var closing = false;
        var closingPolls = 0;
        final runner = LifecycleProbeRunner(
          report: report,
          controllerProcessId: 100,
          execute: (command) async {
            operations.add(command.operation);
            if (command.operation == LifecycleOperation.setPassThrough) {
              closing = true;
            }
            if (closing && command.operation == LifecycleOperation.inspect) {
              if (++closingPolls == 2) {
                throw PlatformException(
                  code: 'lifecycle_failed',
                  message: 'controlled observation ends before process exit',
                );
              }
            }
            final state = snapshot(++sequence);
            if (closing) {
              // Keep the retained process alive and the historical UI ready.
              // Exercise both partial teardown and both HWNDs already destroyed.
              state
                ..topHwnd = cause == 2 ? 10 : null
                ..childHwnd = null
                ..visible = null
                ..inputMode = null
                ..focusHwnd = null
                ..primaryWindowCount = cause == 2 ? 1 : 0
                ..trayDeleted = trayDeleted
                ..diagnosticsJson = jsonEncode({
                  'primaryErrorCode': 0,
                  'exitCause': cause,
                });
            }
            if (command.operation == LifecycleOperation.abort) {
              state
                ..primaryAlive = false
                ..primaryExitCode = 0;
            }
            return state;
          },
        );
        await expectLater(runner.run(), throwsA(isA<FatalProbe>()));
        expect(closingPolls, accepted ? 2 : 0);
        expect(
          report.observations.where(
            (value) => value['label'] == 'lifecycle-setPassThrough',
          ),
          hasLength(accepted ? 1 : 0),
        );
        expect(
          report.observations.where(
            (value) => value['label'] == 'lifecycle-rejected-observation',
          ),
          hasLength(accepted ? 0 : 1),
        );
        expect(report.checks.last['status'], 'FAIL');
        expect(
          report.checks.where((value) => value['status'] == 'PASS'),
          hasLength(1),
        );
        expect(operations, isNot(contains(LifecycleOperation.hidePrimary)));
        expect(operations, isNot(contains(LifecycleOperation.secondaryShow)));
        expect(operations.last, LifecycleOperation.abort);
      },
    );
  }

  test('hidden focus handoff and menu selection wait for observed readiness', () async {
    final report = ProbeReport(
      const ProbeLaunch(runId: 'test', resultFile: 'test-only-never-written'),
      sha,
    );
    final state = snapshot(0);
    final operations = <LifecycleOperation>[];
    var menuRequested = false;
    var menuPolls = 0;
    var handoffPending = false;
    var handoffPolls = 0;
    var handoffs = 0;
    var focusHandoffSequence = 0;
    var focusHandoffAck = 0;
    void startHandoff() {
      handoffPending = true;
      handoffPolls = 0;
      handoffs++;
      focusHandoffSequence = 0;
      focusHandoffAck = 0;
    }

    final runner = LifecycleProbeRunner(
      report: report,
      controllerProcessId: 100,
      execute: (command) async {
        operations.add(command.operation);
        state.observationSequence++;
        if (handoffPending && command.operation != LifecycleOperation.inspect) {
          fail('A new mutation ran before hidden focus handoff was observed.');
        }
        switch (command.operation) {
          case LifecycleOperation.startPrimary:
            break;
          case LifecycleOperation.inspect:
            if (menuRequested) menuPolls++;
            if (handoffPending) {
              handoffPolls++;
              // Each incomplete observation isolates one required condition:
              // zero sequence, missing acknowledgement, then wrong foreground.
              focusHandoffSequence = handoffPolls == 1 ? 0 : handoffs;
              focusHandoffAck = handoffPolls < 3 ? 0 : handoffs;
              state.foregroundProcessId = handoffPolls == 3 ? 200 : 100;
              if (handoffPolls == 4) handoffPending = false;
            }
          case LifecycleOperation.setPassThrough:
            state.inputMode = ProbeInputMode.passThrough;
          case LifecycleOperation.hidePrimary:
            state.visible = false;
            startHandoff();
          case LifecycleOperation.secondaryShow:
          case LifecycleOperation.secondaryToggle:
          case LifecycleOperation.secondaryInvalid:
            state
              ..secondaryPid = 300
              ..secondaryRequestId = state.secondaryRequestId + 1
              ..secondaryDone = true
              ..secondaryExitCode = 0
              ..secondaryBindError = 10048
              ..secondaryServerPid = 200
              ..secondaryBytesSent = 80;
            if (command.operation == LifecycleOperation.secondaryInvalid) {
              state
                ..controlRejected = state.controlRejected + 1
                ..secondaryAction = null;
            } else {
              final show =
                  command.operation == LifecycleOperation.secondaryShow;
              state
                ..secondaryAction = show
                    ? LifecycleControlAction.show
                    : LifecycleControlAction.toggle
                ..controlLastAction = show
                    ? LifecycleControlAction.show
                    : LifecycleControlAction.toggle
                ..controlApplied = state.controlApplied + 1
                ..visible = show || state.visible == false;
              if (state.visible!) {
                state
                  ..inputMode = ProbeInputMode.interactive
                  ..foregroundProcessId = 200;
              } else {
                startHandoff();
              }
              if (state.endpointMatched) {
                state.activations++;
              } else {
                state
                  ..connectionUpdates = state.connectionUpdates + 1
                  ..endpointMatched = true
                  ..tokenMatched = true
                  ..gamePid = 100;
              }
            }
          case LifecycleOperation.clickPrimary:
            state
              ..mouseDown = state.mouseDown + 1
              ..mouseUp = state.mouseUp + 1
              ..uiPointerDown = state.uiPointerDown + 1;
          case LifecycleOperation.sendFocusKey:
            state
              ..keyDown = state.keyDown + 1
              ..uiKeyDown = state.uiKeyDown + 1;
          case LifecycleOperation.clickTray:
            state
              ..trayCallbackCount = state.trayCallbackCount + 1
              ..trayLastAction = LifecycleTrayAction.activate
              ..visible = true
              ..foregroundProcessId = 200
              ..inputMode = ProbeInputMode.interactive;
          case LifecycleOperation.openTrayMenu:
            menuRequested = true;
            state.trayCallbackCount++;
          case LifecycleOperation.trayMenuPassthrough:
            expect(menuPolls, 2);
            throw PlatformException(
              code: 'lifecycle_failed',
              message: 'stop after verified menu preparation',
            );
          case LifecycleOperation.abort:
            state
              ..primaryAlive = false
              ..primaryExitCode = 0;
          default:
            throw StateError('Unexpected test operation ${command.operation}');
        }
        state.diagnosticsJson = jsonEncode({
          'primaryErrorCode': 0,
          'fixtureSequence': 0,
          'fixtureAck': 0,
          'focusHandoffSequence': focusHandoffSequence,
          'focusHandoffAck': focusHandoffAck,
          'menuOpen': menuRequested,
          'menuRectsReady': menuPolls >= 2,
        });
        return LifecycleSnapshot.decode(state.encode());
      },
    );
    await expectLater(runner.run(), throwsA(isA<FatalProbe>()));
    expect(
      operations.where((value) => value == LifecycleOperation.openTrayMenu),
      hasLength(1),
    );
    expect(
      operations.where(
        (value) => value == LifecycleOperation.trayMenuPassthrough,
      ),
      hasLength(1),
    );
    expect(operations.last, LifecycleOperation.abort);
    expect(handoffs, 3);
    expect(handoffPolls, 4);
    expect(report.checks.last['name'], 'tray-menu-enables-passthrough');
    expect(report.checks.last['status'], 'FAIL');
  });

  testWidgets(
    'fixture publishes first frame and real input with ordered acknowledgements',
    (tester) async {
      final published = <LifecycleUiEvidence>[];
      final errors = <Object>[];
      await tester.pumpWidget(
        LifecycleFixtureApp(
          gitSha: sha,
          publish: (evidence) async {
            published.add(evidence);
            return evidence.sequence;
          },
          onFailure: errors.add,
        ),
      );
      await tester.pumpAndSettle();
      expect(published, isNotEmpty);
      expect(
        published.every((value) => value.ready && value.gitSha == sha),
        isTrue,
      );
      await tester.tapAt(const Offset(160, 96));
      await tester.sendKeyEvent(LogicalKeyboardKey.keyA, platform: 'windows');
      await tester.pumpAndSettle();
      expect(published.last.pointerDown, 1);
      expect(published.last.keyDown, 0);
      await tester.sendKeyEvent(LogicalKeyboardKey.f24, platform: 'windows');
      await tester.pumpAndSettle();
      expect(published.last.keyDown, 1);
      expect(published.last.focused, true);
      expect(
        published.map((value) => value.sequence),
        List.generate(published.length, (index) => index + 1),
      );
      expect(errors, isEmpty);
    },
  );

  testWidgets(
    'fixture stops publishing after an invalid native acknowledgement',
    (tester) async {
      var calls = 0;
      final errors = <Object>[];
      await tester.pumpWidget(
        LifecycleFixtureApp(
          gitSha: sha,
          publish: (evidence) async {
            calls++;
            return evidence.sequence + 1;
          },
          onFailure: errors.add,
        ),
      );
      await tester.pumpAndSettle();
      await tester.tapAt(const Offset(160, 96));
      await tester.sendKeyEvent(LogicalKeyboardKey.f24, platform: 'windows');
      await tester.pumpAndSettle();
      expect(calls, 1);
      expect(errors, hasLength(1));
    },
  );
}
