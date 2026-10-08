import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/focus_probe_runner.dart';
import 'package:mystia_steward_companion_window_probe/generated/input_probe_api.g.dart';
import 'package:mystia_steward_companion_window_probe/input_probe_contract.dart';
import 'package:mystia_steward_companion_window_probe/input_probe_view.dart';

import 'input_probe_fixtures.dart';

class FocusFixture {
  FocusFixture(this.ui);
  final InputProbeUi ui;
  final operations = <FocusOperation>[];
  final state = focusSample(0, 0);
  int pendingPolls = 0;
  int pointerPending = 0;
  int keyPending = 0;
  int inputUiWaits = 0;
  int grantPendingPolls = 0;
  int grantWaits = 0;

  Future<FocusSnapshot> execute(FocusCommand command) async {
    operations.add(command.operation);
    if (command.operation == FocusOperation.inspect) {
      expect(command.requestId, state.requestId);
      if (pendingPolls > 0) {
        pendingPolls--;
      } else {
        if (pointerPending > 0) {
          ui.pointerReceived();
          pointerPending--;
          inputUiWaits++;
        }
        if (keyPending > 0) {
          ui.keyReceived();
          keyPending--;
          inputUiWaits++;
        }
      }
      if (grantPendingPolls > 0) {
        grantPendingPolls--;
        grantWaits++;
        if (grantPendingPolls == 0) {
          final grant = state.foregroundGrant!;
          grant
            ..responseSequence = grant.grantSequence
            ..allowResult = true
            // GetLastError is not a success criterion when ASFW succeeds.
            ..allowError = 5
            ..foregroundHwnd = 20
            ..foregroundProcessId = 200
            ..foregroundAfterHwnd = 20
            ..foregroundAfterProcessId = 200
            ..activationRequested = true;
        }
      }
      state.requestPending = pendingPolls > 0 || grantPendingPolls > 0;
      if (state.gameCloseRequested && !state.requestPending) {
        state
          ..gameAlive = false
          ..gameExitCode = 0;
      }
    } else {
      expect(
        pendingPolls == 0 && grantPendingPolls == 0,
        true,
        reason: 'A mutation must wait for native acknowledgement.',
      );
      expect(command.requestId, state.requestId + 1);
      state
        ..requestId = command.requestId
        ..requestPending = true;
      pendingPolls = 2;
      switch (command.operation) {
        case FocusOperation.initialize:
          break;
        case FocusOperation.focusProbe:
          final previousGrant = state.foregroundGrant!;
          state.foregroundGrant = ForegroundGrantSnapshot(
            ready: true,
            identityMatched: true,
            grantSequence: previousGrant.grantSequence + 1,
            requestId: command.requestId,
            responseSequence: 0,
            issuerProcessId: 200,
            targetProcessId: 100,
            foregroundHwnd: 0,
            foregroundProcessId: 0,
            foregroundAfterHwnd: 0,
            foregroundAfterProcessId: 0,
            activationRequested: false,
          );
          grantPendingPolls = 3;
          // An early focus observation cannot substitute for the pending ACK.
          state
            ..foregroundHwnd = 10
            ..foregroundProcessId = 100
            ..focusHwnd = 11
            ..focusOwnerPid = 100
            ..probeVisible = true
            ..inputMode = InputProbeMode.interactive;
          ui.focusChanged(true);
        case FocusOperation.focusGame:
          state
            ..foregroundHwnd = 20
            ..foregroundProcessId = 200
            ..focusHwnd = null
            ..focusOwnerPid = 0;
          ui.focusChanged(false);
        case FocusOperation.hideProbe:
          state.probeVisible = false;
        case FocusOperation.setPassThrough:
          state.inputMode = InputProbeMode.passThrough;
        case FocusOperation.clickProbe:
          state
            ..probeMouseDown = state.probeMouseDown + 1
            ..probeMouseUp = state.probeMouseUp + 1;
          pointerPending++;
        case FocusOperation.sendFocusKey:
          state.probeKeyDown++;
          keyPending++;
        case FocusOperation.closeGame:
          state
            ..gameCloseRequested = true
            ..gameHwnd = null
            ..gameFocusHwnd = null
            ..gameWindowCount = 0;
        case FocusOperation.inspect:
          throw StateError('Handled above.');
      }
    }
    state.sequence++;
    return FocusSnapshot.decode(state.encode());
  }
}

void main() {
  test('focus waits for native completion and real Dart delivery, then retained exit', () async {
    final ui = InputProbeUi();
    final fixture = FocusFixture(ui);
    final report = InputProbeReport(
      const InputProbeLaunch('test', 'test-only', InputSuite.focus),
      inputSha,
    );
    await FocusProbeRunner(
      report: report,
      ui: ui,
      execute: fixture.execute,
      processId: 100,
    ).run();
    expect(
      report.checks.map((value) => value['name']).toSet(),
      requiredFocusChecks,
    );
    expect(fixture.inputUiWaits, 6);
    expect(fixture.grantWaits, 9);
    final granted = report.observations
        .where(
          (value) => value['label'] == 'probe-foreground-and-flutter-focus',
        )
        .map((value) => value['foregroundGrant'] as Map<String, Object?>)
        .toList();
    expect(granted.map((value) => value['grantSequence']), [1, 2, 3]);
    expect(granted.map((value) => value['responseSequence']), [1, 2, 3]);
    expect(granted.map((value) => value['requestId']).toSet(), hasLength(3));
    expect(ui.pointerDown, 3);
    expect(ui.keyDown, 3);
    expect(
      fixture.operations.where((value) => value == FocusOperation.closeGame),
      hasLength(1),
    );
    final exit = report.observations.last;
    expect(exit['label'], 'retained-game-exit-zero');
    expect(exit['gameAlive'], false);
    expect(exit['gameExitCode'], 0);
    report.status = 'PASS';
    expect(jsonDecode(report.encode())['status'], 'PASS');
  });

  test('close waits for actual game foreground and owned focus after the final handoff', () async {
    final ui = InputProbeUi();
    final fixture = FocusFixture(ui);
    final report = InputProbeReport(
      const InputProbeLaunch('test', 'test-only', InputSuite.focus),
      inputSha,
    );
    var gameFocusRequests = 0;
    int? finalHandoffRequest;
    var handoffPolls = 0;
    await FocusProbeRunner(
      report: report,
      ui: ui,
      processId: 100,
      execute: (command) async {
        if (command.operation == FocusOperation.focusGame &&
            ++gameFocusRequests == 3) {
          finalHandoffRequest = command.requestId;
        }
        if (command.operation == FocusOperation.closeGame) {
          expect(handoffPolls, 4);
          final observed = report.observations.last;
          expect(observed['label'], 'game-foreground-before-close');
          expect(observed['foregroundHwnd'], 20);
          expect(observed['foregroundProcessId'], 200);
          expect(observed['gameFocusHwnd'], 20);
        }
        final state = await fixture.execute(command);
        if (state.requestId == finalHandoffRequest) {
          if (command.operation == FocusOperation.inspect) handoffPolls++;
          // Even an API success and completed native request cannot replace
          // the actual foreground and owned game focus observations.
          state.lastForegroundResult = true;
          if (handoffPolls < 3) {
            state
              ..foregroundHwnd = 10
              ..foregroundProcessId = 100
              ..focusHwnd = 11
              ..focusOwnerPid = 100;
          }
          if (handoffPolls < 4) {
            state
              ..gameFocusHwnd = null
              ..diagnosticsJson = jsonEncode({'gameFocusOwned': false});
          }
          ui.focusChanged(handoffPolls < 3);
        }
        return state;
      },
    ).run();
    expect(gameFocusRequests, 3);
    expect(handoffPolls, 4);
    expect(report.checks, hasLength(8));
    expect(fixture.state.foregroundGrant!.grantSequence, 3);
    expect(
      fixture.operations.where((value) => value == FocusOperation.closeGame),
      hasLength(1),
    );
  });

  test(
    'failed final handoff never starts normal close or records exit success',
    () async {
      final ui = InputProbeUi();
      final fixture = FocusFixture(ui);
      final report = InputProbeReport(
        const InputProbeLaunch('test', 'test-only', InputSuite.focus),
        inputSha,
      );
      var gameFocusRequests = 0;
      int? finalHandoffRequest;
      var handoffPolls = 0;
      final runner = FocusProbeRunner(
        report: report,
        ui: ui,
        processId: 100,
        execute: (command) async {
          if (command.operation == FocusOperation.focusGame &&
              ++gameFocusRequests == 3) {
            finalHandoffRequest = command.requestId;
          }
          final state = await fixture.execute(command);
          if (state.requestId == finalHandoffRequest) {
            state
              ..foregroundHwnd = 10
              ..foregroundProcessId = 100
              ..gameFocusHwnd = null
              ..diagnosticsJson = jsonEncode({'gameFocusOwned': false});
            if (command.operation == FocusOperation.inspect &&
                ++handoffPolls == 2) {
              throw PlatformException(
                code: 'blocked',
                message: 'The final game foreground handoff was refused.',
                details: state,
              );
            }
          }
          return state;
        },
      );
      await expectLater(runner.run(), throwsA(isA<InputProbeBlocked>()));
      expect(gameFocusRequests, 3);
      expect(handoffPolls, 2);
      expect(fixture.operations, isNot(contains(FocusOperation.closeGame)));
      expect(fixture.state.gameCloseRequested, false);
      expect(report.checks, hasLength(7));
      expect(
        report.checks.map((value) => value['name']),
        isNot(contains('focus-retained-game-close-exit-zero')),
      );
      expect(report.observations.last['label'], 'native-error');
      expect(report.observations.last['errorCode'], 'blocked');
      expect(fixture.state.foregroundGrant!.grantSequence, 3);
    },
  );

  test(
    'unknown focus result retains typed evidence and never replays or closes',
    () async {
      final ui = InputProbeUi();
      final fixture = FocusFixture(ui);
      final report = InputProbeReport(
        const InputProbeLaunch('test', 'test-only', InputSuite.focus),
        inputSha,
      );
      final runner = FocusProbeRunner(
        report: report,
        ui: ui,
        processId: 100,
        execute: (command) async {
          if (command.operation == FocusOperation.focusProbe) {
            fixture.operations.add(command.operation);
            throw PlatformException(
              code: 'blocked',
              message: 'Foreground refused',
              details: focusSample(10, command.requestId),
            );
          }
          return fixture.execute(command);
        },
      );
      await expectLater(runner.run(), throwsA(isA<InputProbeBlocked>()));
      expect(fixture.operations.last, FocusOperation.focusProbe);
      expect(
        fixture.operations.where((value) => value == FocusOperation.focusProbe),
        hasLength(1),
      );
      expect(fixture.operations, isNot(contains(FocusOperation.closeGame)));
      expect(report.observations.last['errorCode'], 'blocked');
      expect(report.observations.last['gamePid'], 200);
      expect(report.checks, hasLength(1));
    },
  );

  test('initialization waits for the cooperative pipe identity', () async {
    final ui = InputProbeUi();
    final fixture = FocusFixture(ui);
    final report = InputProbeReport(
      const InputProbeLaunch('test', 'test-only', InputSuite.focus),
      inputSha,
    );
    var unreadyObservations = 0;
    await FocusProbeRunner(
      report: report,
      ui: ui,
      processId: 100,
      execute: (command) async {
        final state = await fixture.execute(command);
        if (command.requestId == 1 && unreadyObservations < 4) {
          state.foregroundGrant = null;
          unreadyObservations++;
        }
        if (command.operation == FocusOperation.focusProbe) {
          expect(unreadyObservations, 4);
        }
        return state;
      },
    ).run();
    expect(report.checks, hasLength(8));
    final ready = report.observations.firstWhere(
      (value) => value['label'] == 'retained-game-ready',
    );
    expect((ready['foregroundGrant'] as Map)['identityMatched'], true);
  });

  final corruptions = <String, void Function(ForegroundGrantSnapshot)>{
    'reused grant sequence': (grant) => grant.grantSequence--,
    'replayed request': (grant) => grant.requestId--,
    'replayed response': (grant) => grant.responseSequence--,
    'wrong issuer': (grant) => grant.issuerProcessId++,
    'wrong target': (grant) => grant.targetProcessId++,
    'grant issued without game foreground': (grant) =>
        grant.foregroundHwnd = 10,
    'game lost foreground during grant': (grant) =>
        grant.foregroundAfterHwnd = 10,
    'denied grant': (grant) => grant.allowResult = false,
    'activation without acknowledgement': (grant) {
      grant
        ..responseSequence = 0
        ..allowResult = null
        ..allowError = null;
    },
    'completion without acknowledgement': (grant) {
      grant
        ..responseSequence = 0
        ..allowResult = null
        ..allowError = null
        ..foregroundHwnd = 0
        ..foregroundProcessId = 0
        ..foregroundAfterHwnd = 0
        ..foregroundAfterProcessId = 0
        ..activationRequested = false;
    },
    'completion without activation': (grant) =>
        grant.activationRequested = false,
  };
  for (final entry in corruptions.entries) {
    test('${entry.key} cannot authorize a later focus restoration', () async {
      final ui = InputProbeUi();
      final fixture = FocusFixture(ui);
      final report = InputProbeReport(
        const InputProbeLaunch('test', 'test-only', InputSuite.focus),
        inputSha,
      );
      var focusRequests = 0;
      final runner = FocusProbeRunner(
        report: report,
        ui: ui,
        processId: 100,
        execute: (command) async {
          if (command.operation == FocusOperation.focusProbe) focusRequests++;
          final state = await fixture.execute(command);
          if (focusRequests == 2 && !state.requestPending) {
            entry.value(state.foregroundGrant!);
          }
          return state;
        },
      );
      await expectLater(runner.run(), throwsA(isA<InputProbeFailure>()));
      expect(focusRequests, 2);
      expect(ui.pointerDown, 1);
      expect(ui.keyDown, 1);
      expect(fixture.operations.last, FocusOperation.inspect);
      expect(fixture.operations, isNot(contains(FocusOperation.closeGame)));
      expect(report.checks, hasLength(5));
      expect(report.observations.last['label'], 'rejected-observation');
      expect(report.observations.last['foregroundGrant'], isNotNull);
    });
  }
}
