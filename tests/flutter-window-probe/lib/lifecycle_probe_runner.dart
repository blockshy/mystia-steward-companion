import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';

import 'generated/window_api.g.dart';
import 'probe_contract.dart';

typedef LifecycleExecute = Future<LifecycleSnapshot> Function(
  LifecycleCommand command,
);

Object? _diagnostics(String value) {
  if (value.length > 16384) {
    return {'truncated': true, 'prefix': value.substring(0, 4096)};
  }
  try {
    return jsonDecode(value);
  } on Object {
    return {'invalidJson': value};
  }
}

Map<String, Object?> lifecycleSnapshotJson(LifecycleSnapshot state) => {
  'generation': state.generation,
  'observationSequence': state.observationSequence,
  'controllerProcessId': state.controllerProcessId,
  'primaryProcessId': state.primaryProcessId,
  'primaryExitCode': state.primaryExitCode,
  'primaryAlive': state.primaryAlive,
  'primaryExecutableMatched': state.primaryExecutableMatched,
  'primaryWindowCount': state.primaryWindowCount,
  'nativeGitSha': state.nativeGitSha,
  'primaryGitSha': state.primaryGitSha,
  'topHwnd': state.topHwnd,
  'childHwnd': state.childHwnd,
  'visible': state.visible,
  'inputMode': state.inputMode?.name,
  'foregroundHwnd': state.foregroundHwnd,
  'foregroundProcessId': state.foregroundProcessId,
  'focusHwnd': state.focusHwnd,
  'mouseDown': state.mouseDown,
  'mouseUp': state.mouseUp,
  'keyDown': state.keyDown,
  'uiReady': state.uiReady,
  'uiPointerDown': state.uiPointerDown,
  'uiKeyDown': state.uiKeyDown,
  'uiFocused': state.uiFocused,
  'uiSequence': state.uiSequence,
  'trayAdded': state.trayAdded,
  'trayVersioned': state.trayVersioned,
  'trayDeleted': state.trayDeleted,
  'trayCallbackCount': state.trayCallbackCount,
  'trayMenuCommandCount': state.trayMenuCommandCount,
  'trayLastAction': state.trayLastAction?.name,
  'port': state.port,
  'controlApplied': state.controlApplied,
  'controlRejected': state.controlRejected,
  'controlLastAction': state.controlLastAction?.name,
  'receiveCalls': state.receiveCalls,
  'pendingBytes': state.pendingBytes,
  'connectionUpdates': state.connectionUpdates,
  'activations': state.activations,
  'gamePid': state.gamePid,
  'endpointMatched': state.endpointMatched,
  'tokenMatched': state.tokenMatched,
  'secondaryPid': state.secondaryPid,
  'secondaryAction': state.secondaryAction?.name,
  'secondaryRequestId': state.secondaryRequestId,
  'secondaryDone': state.secondaryDone,
  'secondaryExitCode': state.secondaryExitCode,
  'secondaryBindError': state.secondaryBindError,
  'secondaryServerPid': state.secondaryServerPid,
  'secondaryBytesSent': state.secondaryBytesSent,
  'secondaryWindowCount': state.secondaryWindowCount,
  'secondaryError': state.secondaryError,
  'diagnostics': _diagnostics(state.diagnosticsJson),
};

class LifecycleProbeRunner {
  LifecycleProbeRunner({
    required this.report,
    required this.execute,
    required this.controllerProcessId,
  });

  final ProbeReport report;
  final LifecycleExecute execute;
  final int controllerProcessId;
  int _generation = 1;
  int _sequence = -1;
  int? _port;
  LifecycleSnapshot? _identity;
  bool _started = false;
  bool _exited = false;

  void _require(bool condition, String message) {
    if (!condition) throw FatalProbe(message);
  }

  Map<String, dynamic> _validatedDiagnostics(LifecycleSnapshot state) {
    _require(
      state.diagnosticsJson.length <= 16384,
      'Lifecycle diagnostics exceed 16 KiB.',
    );
    final value = jsonDecode(state.diagnosticsJson);
    _require(
      value is Map<String, dynamic>,
      'Lifecycle diagnostics must be an object.',
    );
    return value as Map<String, dynamic>;
  }

  void _validate(LifecycleSnapshot state) {
    _require(
      state.generation == _generation &&
          state.observationSequence > _sequence &&
          state.controllerProcessId == controllerProcessId &&
          state.nativeGitSha == report.gitSha,
      'Lifecycle generation, sequence, process or compiled identity differs.',
    );
    final diagnostics = _validatedDiagnostics(state);
    _require(
      diagnostics['primaryErrorCode'] == 0,
      'Lifecycle primary reported a native error.',
    );
    final exitCause = diagnostics['exitCause'];
    final closing =
        state.trayDeleted &&
        exitCause is int &&
        exitCause >= 1 &&
        exitCause <= 3;
    final counters = [
      state.mouseDown,
      state.mouseUp,
      state.keyDown,
      state.uiPointerDown,
      state.uiKeyDown,
      state.uiSequence,
      state.trayCallbackCount,
      state.trayMenuCommandCount,
      state.controlApplied,
      state.controlRejected,
      state.receiveCalls,
      state.pendingBytes,
      state.connectionUpdates,
      state.activations,
      state.secondaryRequestId,
      state.secondaryBytesSent,
      state.primaryWindowCount,
    ];
    _require(
      counters.every((value) => value >= 0),
      'Lifecycle counter is negative.',
    );
    _require(
      state.secondaryWindowCount >= -1 &&
          (!state.secondaryDone || state.secondaryWindowCount >= 0),
      'Secondary window enumeration was not completed.',
    );
    if (state.port == 0) {
      _require(
        _identity == null && !state.uiReady && state.primaryAlive,
        'Only a starting primary may have an unbound control port.',
      );
    } else {
      _require(
        state.port > 0 &&
            state.port <= 65535 &&
            state.port != 32145 &&
            state.port != 32146,
        'Lifecycle control port is invalid or reserved.',
      );
      if (_port != null) {
        _require(state.port == _port, 'Lifecycle port changed.');
      }
    }
    if (state.primaryAlive) {
      _require(
        state.primaryProcessId != null &&
            state.primaryProcessId! > 0 &&
            state.primaryProcessId != controllerProcessId &&
            state.primaryExitCode == null &&
            state.primaryExecutableMatched &&
            state.primaryWindowCount <= 1,
        'The exact lifecycle primary identity is unavailable.',
      );
      if (state.uiReady) {
        _require(
          state.primaryGitSha == report.gitSha &&
              state.uiSequence > 0 &&
              (closing
                  ? (state.topHwnd == null || state.topHwnd! > 0) &&
                        (state.childHwnd == null || state.childHwnd! > 0)
                  : state.topHwnd != null &&
                        state.topHwnd! > 0 &&
                        state.childHwnd != null &&
                        state.childHwnd! > 0 &&
                        state.visible != null &&
                        state.inputMode != null &&
                        state.primaryWindowCount == 1),
          'Ready lifecycle Flutter window identity is invalid.',
        );
      }
    } else {
      _require(
        state.topHwnd == null &&
            state.childHwnd == null &&
            state.visible == null &&
            state.inputMode == null &&
            state.focusHwnd == null,
        'Exited primary must not expose live window observations.',
      );
    }
    final identity = _identity;
    if (identity != null) {
      _require(
        state.primaryProcessId == identity.primaryProcessId,
        'The retained primary process was replaced.',
      );
      if (state.primaryAlive && state.uiReady) {
        _require(
          ((closing && state.topHwnd == null) ||
                  state.topHwnd == identity.topHwnd) &&
              ((closing && state.childHwnd == null) ||
                  state.childHwnd == identity.childHwnd),
          'The retained primary HWND was replaced.',
        );
      }
    }
    _sequence = state.observationSequence;
  }

  void _record(
    String label,
    LifecycleSnapshot state, [
    Map<String, Object?> extra = const {},
  ]) {
    report.observations.add({
      'label': 'lifecycle-$label',
      'observationUtc': DateTime.now().toUtc().toIso8601String(),
      ...extra,
      ...lifecycleSnapshotJson(state),
    });
  }

  Future<LifecycleSnapshot> _send(
    LifecycleOperation operation, {
    bool record = true,
  }) async {
    LifecycleSnapshot state;
    try {
      state = await execute(
        LifecycleCommand(operation: operation, generation: _generation),
      ).timeout(const Duration(seconds: 15));
    } on PlatformException catch (error) {
      if (error.details is LifecycleSnapshot) {
        _record('native-error', error.details as LifecycleSnapshot, {
          'operation': operation.name,
          'errorCode': error.code,
          'errorMessage': error.message,
        });
      } else {
        report.observations.add({
          'label': 'lifecycle-native-error',
          'operation': operation.name,
          'errorCode': error.code,
          'errorMessage': error.message,
          'details': error.details is String
              ? _diagnostics(error.details as String)
              : null,
        });
      }
      if (error.code == 'blocked') {
        throw ProbeBlocked(
          error.message ?? 'Lifecycle desktop prerequisite unavailable.',
        );
      }
      throw FatalProbe('${operation.name}: ${error.code}: ${error.message}');
    } on Object catch (error) {
      throw FatalProbe(
        '${operation.name}: $error; the operation will not be replayed.',
      );
    }
    try {
      _validate(state);
    } on Object catch (error) {
      _record('rejected-observation', state, {
        'operation': operation.name,
        'validationError': error.toString(),
      });
      throw FatalProbe(error.toString());
    }
    if (record) _record(operation.name, state);
    return state;
  }

  Future<LifecycleSnapshot> _wait(
    String label,
    bool Function(LifecycleSnapshot) predicate,
  ) async {
    final watch = Stopwatch()..start();
    while (true) {
      final state = await _send(LifecycleOperation.inspect, record: false);
      if (predicate(state)) {
        _record(label, state);
        return state;
      }
      if (watch.elapsed >= const Duration(seconds: 8)) {
        _record('$label-timeout', state);
        throw FatalProbe(
          'No lifecycle evidence for $label before the deadline.',
        );
      }
      await Future<void>.delayed(const Duration(milliseconds: 50));
    }
  }

  Future<void> _case(String name, Future<void> Function() action) async {
    try {
      await action();
      report.check(name, true);
    } on Object catch (error) {
      report.checks.add({
        'name': name,
        'status': error is ProbeBlocked ? 'BLOCKED' : 'FAIL',
        'detail': error.toString(),
      });
      rethrow;
    }
  }

  Future<void> _start() async {
    _exited = false;
    await _send(LifecycleOperation.startPrimary);
    final state = await _wait(
      'primary-ready',
      (state) =>
          state.primaryAlive &&
          state.uiReady &&
          state.trayAdded &&
          state.trayVersioned &&
          !state.trayDeleted,
    );
    _identity = state;
    _port ??= state.port;
    report.context['lifecycleGeneration$_generation'] = {
      'primaryProcessId': state.primaryProcessId,
      'topHwnd': state.topHwnd,
      'childHwnd': state.childHwnd,
      'port': state.port,
      'gitSha': state.primaryGitSha,
    };
  }

  bool _interactiveState(LifecycleSnapshot state) =>
      state.primaryAlive &&
      state.visible == true &&
      state.inputMode == ProbeInputMode.interactive &&
      state.foregroundProcessId == state.primaryProcessId &&
      state.foregroundHwnd == state.topHwnd &&
      state.focusHwnd == state.childHwnd &&
      state.uiFocused;

  Future<void> _proveInteractive() async {
    final before = await _wait('interactive-focus', _interactiveState);
    await _send(LifecycleOperation.clickPrimary);
    await _wait(
      'real-flutter-click',
      (state) =>
          _interactiveState(state) &&
          state.mouseDown == before.mouseDown + 1 &&
          state.mouseUp == before.mouseUp + 1 &&
          state.uiPointerDown == before.uiPointerDown + 1,
    );
    final keyed = await _send(LifecycleOperation.inspect);
    await _send(LifecycleOperation.sendFocusKey);
    await _wait(
      'real-flutter-f24',
      (state) =>
          _interactiveState(state) &&
          state.keyDown == keyed.keyDown + 1 &&
          state.uiKeyDown == keyed.uiKeyDown + 1,
    );
  }

  void _peer(
    LifecycleSnapshot state,
    LifecycleSnapshot before,
    LifecycleControlAction? action,
  ) {
    _require(
      state.secondaryPid != null &&
          state.secondaryPid! > 0 &&
          state.secondaryPid != state.primaryProcessId &&
          state.secondaryPid != controllerProcessId &&
          state.secondaryDone &&
          state.secondaryExitCode == 0 &&
          state.secondaryError == 0 &&
          state.secondaryAction == action &&
          state.secondaryRequestId > before.secondaryRequestId &&
          (state.secondaryBindError == 10013 ||
              state.secondaryBindError == 10048) &&
          state.secondaryServerPid == state.primaryProcessId &&
          state.secondaryWindowCount == 0 &&
          state.secondaryBytesSent > 0,
      'The real secondary process, exclusive bind rejection or exact listener owner was not verified.',
    );
  }

  Future<LifecycleSnapshot> _secondary(
    LifecycleOperation operation,
    LifecycleControlAction action,
  ) async {
    final before = await _send(LifecycleOperation.inspect);
    await _send(operation);
    final state = await _wait(
      'secondary-${action.name}-applied',
      (state) =>
          state.secondaryDone &&
          state.secondaryExitCode != null &&
          state.controlApplied == before.controlApplied + 1 &&
          state.controlLastAction == action,
    );
    _peer(state, before, action);
    _require(
      state.controlRejected == before.controlRejected,
      'A valid control action was rejected.',
    );
    if (action == LifecycleControlAction.exit) {
      _require(
        state.connectionUpdates == before.connectionUpdates &&
            state.activations == before.activations &&
            state.gamePid == before.gamePid &&
            state.endpointMatched == before.endpointMatched &&
            state.tokenMatched == before.tokenMatched,
        'Bare exit modified connection or activation state.',
      );
    } else {
      final updates = before.endpointMatched && before.tokenMatched ? 0 : 1;
      _require(
        state.connectionUpdates == before.connectionUpdates + updates &&
            state.activations == before.activations + (updates == 0 ? 1 : 0) &&
            state.gamePid == controllerProcessId &&
            state.endpointMatched &&
            state.tokenMatched,
        'Control connection update/activation evidence differs from the fixed command.',
      );
    }
    return state;
  }

  Future<void> _invalid() async {
    final before = await _send(LifecycleOperation.inspect);
    await _send(LifecycleOperation.secondaryInvalid);
    final after = await _wait(
      'invalid-rejected',
      (state) =>
          state.secondaryDone &&
          state.secondaryExitCode != null &&
          state.controlRejected == before.controlRejected + 1,
    );
    _peer(after, before, null);
    _require(
      after.controlApplied == before.controlApplied &&
          after.controlLastAction == before.controlLastAction &&
          after.connectionUpdates == before.connectionUpdates &&
          after.activations == before.activations &&
          after.gamePid == before.gamePid &&
          after.endpointMatched == before.endpointMatched &&
          after.tokenMatched == before.tokenMatched &&
          after.visible == before.visible &&
          after.inputMode == before.inputMode &&
          after.uiPointerDown == before.uiPointerDown &&
          after.uiKeyDown == before.uiKeyDown,
      'Invalid control input partially changed primary state.',
    );
  }

  Future<void> _prepareHidden() async {
    await _send(LifecycleOperation.setPassThrough);
    await _wait(
      'fixture-passthrough-applied',
      (state) =>
          state.primaryAlive &&
          state.inputMode == ProbeInputMode.passThrough &&
          _fixtureAcknowledged(state),
    );
    await _send(LifecycleOperation.hidePrimary);
    await _wait(
      'fixture-hidden-passthrough',
      (state) =>
          _hiddenState(state) &&
          state.inputMode == ProbeInputMode.passThrough &&
          _fixtureAcknowledged(state),
    );
  }

  bool _hiddenState(LifecycleSnapshot state) {
    final diagnostics = _validatedDiagnostics(state);
    final sequence = diagnostics['focusHandoffSequence'];
    return state.primaryAlive &&
        state.visible == false &&
        state.foregroundProcessId == controllerProcessId &&
        sequence is int &&
        sequence > 0 &&
        sequence == diagnostics['focusHandoffAck'];
  }

  bool _fixtureAcknowledged(LifecycleSnapshot state) {
    final diagnostics = _validatedDiagnostics(state);
    return diagnostics['fixtureSequence'] is int &&
        diagnostics['fixtureSequence'] == diagnostics['fixtureAck'];
  }

  Future<void> _menu(
    LifecycleOperation operation,
    LifecycleTrayAction action,
  ) async {
    final before = await _send(LifecycleOperation.inspect);
    await _send(LifecycleOperation.openTrayMenu);
    await _wait('real-menu-ready', (state) {
      final diagnostics = _validatedDiagnostics(state);
      return diagnostics['menuOpen'] == true &&
          diagnostics['menuRectsReady'] == true;
    });
    await _send(operation);
    await _wait(
      'menu-${action.name}-observed',
      (state) =>
          state.trayCallbackCount > before.trayCallbackCount &&
          state.trayMenuCommandCount == before.trayMenuCommandCount + 1 &&
          state.trayLastAction == action,
    );
  }

  Future<void> _exitObserved(int cause) async {
    await _wait('primary-exited', (state) {
      final diagnostics = _validatedDiagnostics(state);
      return !state.primaryAlive &&
          state.primaryExitCode == 0 &&
          state.primaryWindowCount == 0 &&
          state.trayDeleted &&
          diagnostics['trayAbsent'] == true &&
          diagnostics['portReleased'] == true &&
          diagnostics['exitCause'] == cause;
    });
    _exited = true;
  }

  Future<void> run() async {
    if (_started) throw StateError('The lifecycle suite cannot be replayed.');
    _started = true;
    try {
      await _case('lifecycle-primary-identity-and-single-tray', _start);
      await _case('secondary-show-restores-existing-primary', () async {
        await _prepareHidden();
        await _secondary(
          LifecycleOperation.secondaryShow,
          LifecycleControlAction.show,
        );
        await _proveInteractive();
      });
      await _case('secondary-toggle-hides-existing-primary', () async {
        await _secondary(
          LifecycleOperation.secondaryToggle,
          LifecycleControlAction.toggle,
        );
        await _wait('secondary-toggle-hidden', _hiddenState);
      });
      await _case('secondary-toggle-restores-existing-primary', () async {
        await _secondary(
          LifecycleOperation.secondaryToggle,
          LifecycleControlAction.toggle,
        );
        await _proveInteractive();
      });
      await _case('secondary-invalid-is-atomically-rejected', _invalid);
      await _case('tray-click-restores-interactive', () async {
        await _prepareHidden();
        final before = await _send(LifecycleOperation.inspect);
        await _send(LifecycleOperation.clickTray);
        await _wait(
          'tray-activate-observed',
          (state) =>
              state.trayCallbackCount > before.trayCallbackCount &&
              state.trayLastAction == LifecycleTrayAction.activate,
        );
        await _proveInteractive();
      });
      await _case('tray-menu-enables-passthrough', () async {
        await _menu(
          LifecycleOperation.trayMenuPassthrough,
          LifecycleTrayAction.passthrough,
        );
        await _wait(
          'menu-passthrough',
          (state) =>
              state.primaryAlive &&
              state.visible == true &&
              state.inputMode == ProbeInputMode.passThrough,
        );
      });
      await _case('tray-menu-show-clears-passthrough', () async {
        await _menu(LifecycleOperation.trayMenuShow, LifecycleTrayAction.show);
        await _proveInteractive();
      });
      await _case('tray-menu-exit-observed', () async {
        await _menu(LifecycleOperation.trayMenuExit, LifecycleTrayAction.exit);
        await _exitObserved(1);
      });
      await _case('lifecycle-relaunch-reuses-port-after-exit', () async {
        _generation = 2;
        _identity = null;
        await _start();
      });
      await _case('secondary-bare-exit-observed', () async {
        await _secondary(
          LifecycleOperation.secondaryExit,
          LifecycleControlAction.exit,
        );
        await _exitObserved(2);
      });
    } finally {
      if (!_exited) {
        try {
          final state = await execute(
            LifecycleCommand(
              operation: LifecycleOperation.abort,
              generation: _generation,
            ),
          ).timeout(const Duration(seconds: 10));
          _record('abort', state);
          final watch = Stopwatch()..start();
          var observed = state;
          while (observed.primaryAlive ||
              (observed.secondaryPid != null &&
                  observed.secondaryExitCode == null)) {
            if (watch.elapsed >= const Duration(seconds: 8)) {
              throw const FatalProbe(
                'Lifecycle cleanup did not observe retained processes exiting.',
              );
            }
            await Future<void>.delayed(const Duration(milliseconds: 50));
            observed = await execute(
              LifecycleCommand(
                operation: LifecycleOperation.inspect,
                generation: _generation,
              ),
            ).timeout(const Duration(seconds: 10));
          }
          _record('abort-exit-observed', observed);
        } on Object catch (error) {
          report.errors.add('Lifecycle fixture cleanup failed: $error');
        }
      }
    }
  }
}
