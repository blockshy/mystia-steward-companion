import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';

import 'generated/input_probe_api.g.dart';
import 'input_probe_contract.dart';
import 'input_probe_view.dart';

typedef FocusExecute = Future<FocusSnapshot> Function(FocusCommand command);

Map<String, Object?> foregroundGrantJson(ForegroundGrantSnapshot grant) => {
  'ready': grant.ready,
  'identityMatched': grant.identityMatched,
  'grantSequence': grant.grantSequence,
  'requestId': grant.requestId,
  'responseSequence': grant.responseSequence,
  'issuerProcessId': grant.issuerProcessId,
  'targetProcessId': grant.targetProcessId,
  'allowResult': grant.allowResult,
  'allowError': grant.allowError,
  'foregroundHwnd': grant.foregroundHwnd,
  'foregroundProcessId': grant.foregroundProcessId,
  'foregroundAfterHwnd': grant.foregroundAfterHwnd,
  'foregroundAfterProcessId': grant.foregroundAfterProcessId,
  'activationRequested': grant.activationRequested,
};

Map<String, Object?> focusSnapshotJson(FocusSnapshot state) => {
  'nativeGitSha': state.nativeGitSha, 'processId': state.processId,
  'sequence': state.sequence,
  'requestId': state.requestId,
  'requestPending': state.requestPending,
  'gamePid': state.gamePid, 'gameCreationTimeHex': state.gameCreationTimeHex,
  'gameAlive': state.gameAlive,
  'gameIdentityMatched': state.gameIdentityMatched,
  'gameExitCode': state.gameExitCode,
  'gameCloseRequested': state.gameCloseRequested,
  'gameHwnd': state.gameHwnd, 'gameWindowCount': state.gameWindowCount,
  'gameThreadId': state.gameThreadId, 'gameFocusHwnd': state.gameFocusHwnd,
  'probeHwnd': state.probeHwnd, 'probeChildHwnd': state.probeChildHwnd,
  'probeThreadId': state.probeThreadId, 'probeVisible': state.probeVisible,
  'inputMode': state.inputMode.name, 'foregroundHwnd': state.foregroundHwnd,
  'foregroundProcessId': state.foregroundProcessId,
  'focusHwnd': state.focusHwnd,
  'focusOwnerPid': state.focusOwnerPid, 'probeMouseDown': state.probeMouseDown,
  'probeMouseUp': state.probeMouseUp, 'probeKeyDown': state.probeKeyDown,
  'lastForegroundResult': state.lastForegroundResult,
  'lastForegroundError': state.lastForegroundError,
  'foregroundGrant': state.foregroundGrant == null
      ? null
      : foregroundGrantJson(state.foregroundGrant!),
  // Keep even malformed error diagnostics as raw evidence, never parse while
  // trying to archive a rejected snapshot.
  'diagnosticsJson': state.diagnosticsJson.length <= 16384
      ? state.diagnosticsJson
      : state.diagnosticsJson.substring(0, 16384),
  if (state.diagnosticsJson.length > 16384) 'diagnosticsTruncated': true,
};

class FocusProbeRunner {
  FocusProbeRunner({
    required this.report,
    required this.ui,
    required this.execute,
    required this.processId,
  });
  final InputProbeReport report;
  final InputProbeUi ui;
  final FocusExecute execute;
  final int processId;
  int _request = 0;
  int _sequence = 0;
  FocusSnapshot? _identity;
  bool _closing = false;
  int _completedGrantSequence = 0;
  int _completedGrantRequest = 0;
  int? _pendingGrantRequest;

  Map<String, dynamic> _diagnostics(FocusSnapshot state) {
    requireInput(
      state.diagnosticsJson.length <= 16384,
      'Focus diagnostics exceed 16 KiB.',
    );
    final parsed = jsonDecode(state.diagnosticsJson);
    requireInput(
      parsed is Map<String, dynamic>,
      'Focus diagnostics must be an object.',
    );
    return parsed as Map<String, dynamic>;
  }

  void _validate(FocusSnapshot state) {
    requireInput(
      state.nativeGitSha == report.gitSha &&
          state.processId == processId &&
          state.sequence > _sequence &&
          state.requestId == _request,
      'Focus process/build/request/observation identity differs.',
    );
    requireInput(
      state.probeHwnd > 0 &&
          state.probeChildHwnd > 0 &&
          state.probeThreadId > 0 &&
          [
            state.gamePid,
            state.gameWindowCount,
            state.gameThreadId,
            state.foregroundHwnd,
            state.foregroundProcessId,
            state.focusOwnerPid,
            state.probeMouseDown,
            state.probeMouseUp,
            state.probeKeyDown,
            state.lastForegroundError,
          ].every((value) => value >= 0),
      'Focus snapshot contains an invalid window or counter.',
    );
    _diagnostics(state);
    final identity = _identity;
    if (identity != null) {
      requireInput(
        state.gamePid == identity.gamePid &&
            state.gameCreationTimeHex == identity.gameCreationTimeHex &&
            state.gameIdentityMatched &&
            state.probeHwnd == identity.probeHwnd &&
            state.probeChildHwnd == identity.probeChildHwnd &&
            state.probeThreadId == identity.probeThreadId,
        'The retained game or probe identity changed.',
      );
      requireInput(
        _closing ||
            (state.gameAlive &&
                state.gameExitCode == null &&
                !state.gameCloseRequested &&
                state.gameHwnd == identity.gameHwnd &&
                state.gameThreadId == identity.gameThreadId &&
                state.gameWindowCount == 1),
        'The game exited or its exact HWND changed before authorized close.',
      );
      requireInput(
        state.gameHwnd == null || state.gameHwnd == identity.gameHwnd,
        'The closing game HWND was replaced.',
      );
    }
    if (!state.gameAlive && state.gamePid != 0) {
      requireInput(
        _closing &&
            state.gameCloseRequested &&
            state.gameExitCode != null &&
            state.gameWindowCount == 0,
        'An unexpected game process exit was observed.',
      );
    }
    _validateGrant(state);
    _sequence = state.sequence;
  }

  bool _grantReady(FocusSnapshot state) {
    final grant = state.foregroundGrant;
    return grant != null &&
        grant.ready &&
        grant.identityMatched &&
        grant.issuerProcessId == state.gamePid &&
        grant.issuerProcessId > 0 &&
        grant.targetProcessId == processId;
  }

  void _validateGrant(FocusSnapshot state) {
    final grant = state.foregroundGrant;
    if (_identity != null) {
      requireInput(
        _grantReady(state),
        'The cooperative foreground grant identity is missing or changed.',
      );
    }
    if (grant == null) return;
    requireInput(
      [
            grant.grantSequence,
            grant.requestId,
            grant.responseSequence,
            grant.issuerProcessId,
            grant.targetProcessId,
            grant.foregroundHwnd,
            grant.foregroundProcessId,
            grant.foregroundAfterHwnd,
            grant.foregroundAfterProcessId,
          ].every((value) => value >= 0) &&
          (grant.allowError == null || grant.allowError! >= 0) &&
          (grant.allowResult == null) == (grant.allowError == null) &&
          (!grant.ready || _grantReady(state)),
      'The cooperative foreground grant contains invalid identity or values.',
    );
    final pendingRequest = _pendingGrantRequest;
    requireInput(
      grant.grantSequence ==
              _completedGrantSequence + (pendingRequest == null ? 0 : 1) &&
          grant.requestId == (pendingRequest ?? _completedGrantRequest),
      'The cooperative foreground grant is stale or belongs to another request.',
    );
    if (grant.responseSequence == 0) {
      requireInput(
        grant.allowResult == null &&
            grant.allowError == null &&
            !grant.activationRequested &&
            grant.foregroundHwnd == 0 &&
            grant.foregroundProcessId == 0 &&
            grant.foregroundAfterHwnd == 0 &&
            grant.foregroundAfterProcessId == 0 &&
            (grant.grantSequence == 0 ||
                (pendingRequest != null && state.requestPending)),
        'Activation or completion was reported without a foreground grant ACK.',
      );
      return;
    }
    requireInput(
      grant.grantSequence > 0 &&
          grant.responseSequence == grant.grantSequence &&
          grant.allowResult == true &&
          grant.allowError != null &&
          grant.foregroundHwnd == _identity?.gameHwnd &&
          grant.foregroundProcessId == state.gamePid &&
          grant.foregroundAfterHwnd == _identity?.gameHwnd &&
          grant.foregroundAfterProcessId == state.gamePid,
      'The foreground grant ACK does not prove this exact game authorized this request.',
    );
    requireInput(
      state.requestPending || grant.activationRequested,
      'The acknowledged foreground grant was never used for activation.',
    );
  }

  void _record(
    String label,
    FocusSnapshot state, [
    Map<String, Object?> extra = const {},
  ]) {
    report.observe(label, {
      ...focusSnapshotJson(state),
      'flutterUi': ui.toJson(),
      ...extra,
    });
  }

  Future<FocusSnapshot> _send(
    FocusOperation operation, {
    bool record = true,
  }) async {
    if (operation != FocusOperation.inspect) _request++;
    FocusSnapshot state;
    try {
      state = await execute(
        FocusCommand(operation: operation, requestId: _request),
      ).timeout(const Duration(seconds: 15));
    } on PlatformException catch (error) {
      final details = error.details;
      if (details is FocusSnapshot) {
        _record('native-error', details, {
          'operation': operation.name,
          'errorCode': error.code,
          'errorMessage': error.message,
        });
      } else {
        report.observe('native-error', {
          'operation': operation.name,
          'errorCode': error.code,
          'errorMessage': error.message,
          'details': details is String
              ? details.substring(0, details.length.clamp(0, 16384))
              : null,
        });
      }
      if (error.code == 'blocked') {
        throw InputProbeBlocked(
          error.message ?? 'Focus desktop prerequisite unavailable.',
        );
      }
      throw InputProbeFailure(
        '${operation.name}: ${error.code}: ${error.message}',
      );
    } on Object catch (error) {
      throw InputProbeFailure(
        '${operation.name}: $error; the request will not be replayed.',
      );
    }
    try {
      _validate(state);
    } on Object catch (error) {
      _record('rejected-observation', state, {
        'operation': operation.name,
        'validationError': error.toString(),
      });
      rethrow;
    }
    if (record) _record(operation.name, state);
    return state;
  }

  Future<FocusSnapshot> _wait(
    String label,
    bool Function(FocusSnapshot) predicate, {
    Duration deadline = const Duration(seconds: 10),
  }) async {
    final watch = Stopwatch()..start();
    while (true) {
      final state = await _send(FocusOperation.inspect, record: false);
      if (!state.requestPending && predicate(state)) {
        _record(label, state);
        return state;
      }
      if (watch.elapsed >= deadline) {
        _record('$label-timeout', state);
        throw InputProbeFailure(
          'The exact focus observation $label did not arrive before its deadline.',
        );
      }
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }
  }

  bool _gameFocused(FocusSnapshot state) =>
      state.gameAlive &&
      state.gameHwnd != null &&
      state.foregroundHwnd == state.gameHwnd &&
      state.foregroundProcessId == state.gamePid &&
      state.gameFocusHwnd != null &&
      state.gameFocusHwnd! > 0 &&
      _diagnostics(state)['gameFocusOwned'] == true;

  bool _probeFocused(FocusSnapshot state) =>
      state.probeVisible &&
      state.inputMode == InputProbeMode.interactive &&
      state.foregroundHwnd == state.probeHwnd &&
      state.foregroundProcessId == processId &&
      state.focusHwnd == state.probeChildHwnd &&
      state.focusOwnerPid == processId &&
      ui.focused;

  Future<void> _focusProbe() async {
    _pendingGrantRequest = _request + 1;
    await _send(FocusOperation.focusProbe);
    final focused = await _wait(
      'probe-foreground-and-flutter-focus',
      (state) =>
          _probeFocused(state) &&
          state.foregroundGrant?.responseSequence ==
              _completedGrantSequence + 1 &&
          state.foregroundGrant?.activationRequested == true,
    );
    _completedGrantSequence = focused.foregroundGrant!.grantSequence;
    _completedGrantRequest = _pendingGrantRequest!;
    _pendingGrantRequest = null;
  }

  Future<void> _proveInput() async {
    final before = await _wait('interactive-input-baseline', _probeFocused);
    final pointers = ui.pointerDown;
    await _send(FocusOperation.clickProbe);
    await _wait(
      'real-flutter-pointer',
      (state) =>
          _probeFocused(state) &&
          state.probeMouseDown == before.probeMouseDown + 1 &&
          state.probeMouseUp == before.probeMouseUp + 1 &&
          ui.pointerDown == pointers + 1,
    );
    final keyed = await _send(FocusOperation.inspect);
    final keys = ui.keyDown;
    await _send(FocusOperation.sendFocusKey);
    await _wait(
      'real-flutter-f24',
      (state) =>
          _probeFocused(state) &&
          state.probeKeyDown == keyed.probeKeyDown + 1 &&
          ui.keyDown == keys + 1,
    );
  }

  Future<void> run() async {
    report.context['foregroundAuthorization'] = {
      'mechanism': 'cooperative-test-plugin',
      'scope': 'fresh game-to-probe grant for each of the three focus requests',
      'oldModF8OrRsPathVerified': false,
    };
    ui.show('正在启动并绑定本次准备的精确游戏进程；最多等待 120 秒。');
    await _send(FocusOperation.initialize);
    final ready = await _wait(
      'retained-game-ready',
      (state) =>
          state.gameAlive &&
          state.gameIdentityMatched &&
          state.gamePid > 0 &&
          state.gamePid != processId &&
          RegExp(r'^[a-fA-F0-9]{1,16}$').hasMatch(state.gameCreationTimeHex) &&
          int.parse(state.gameCreationTimeHex, radix: 16) > 0 &&
          state.gameHwnd != null &&
          state.gameHwnd! > 0 &&
          state.gameThreadId > 0 &&
          state.gameWindowCount == 1 &&
          _grantReady(state),
      deadline: const Duration(seconds: 120),
    );
    _identity = ready;
    report.context['retainedIdentity'] = focusSnapshotJson(ready);
    report.check('focus-compiled-and-retained-game-identity');
    await _wait('initial-real-game-foreground', _gameFocused);
    ui.show('核验游戏与 Flutter 前台切换；不向游戏发送业务输入。');
    await _focusProbe();
    report.check('focus-game-to-probe-exact-foreground');
    await _proveInput();
    report.check('focus-probe-native-and-flutter-input');
    await _send(FocusOperation.focusGame);
    await _wait(
      'game-foreground-probe-stays-visible',
      (state) => _gameFocused(state) && state.probeVisible,
    );
    report.check('focus-probe-to-game-keeps-visible');
    await _send(FocusOperation.hideProbe);
    await _wait(
      'probe-hidden-game-keeps-foreground',
      (state) => _gameFocused(state) && !state.probeVisible,
    );
    report.check('focus-game-foreground-then-hide-probe');
    await _focusProbe();
    await _proveInput();
    report.check('focus-hidden-probe-restores-interaction');
    await _send(FocusOperation.setPassThrough);
    await _wait(
      'probe-passthrough',
      (state) => state.inputMode == InputProbeMode.passThrough,
    );
    await _send(FocusOperation.focusGame);
    await _wait('passthrough-game-foreground', _gameFocused);
    await _focusProbe();
    await _proveInput();
    report.check('focus-passthrough-restores-interaction');
    ui.show('将前台归还本次游戏副本，确认实际焦点后再关闭。');
    await _send(FocusOperation.focusGame);
    await _wait('game-foreground-before-close', _gameFocused);
    ui.show('关闭本次启动的游戏副本，等待保留句柄确认正常退出。');
    _closing = true;
    await _send(FocusOperation.closeGame);
    await _wait(
      'retained-game-exit-zero',
      (state) =>
          !state.gameAlive &&
          state.gameExitCode == 0 &&
          state.gameCloseRequested &&
          state.gameWindowCount == 0 &&
          state.gameHwnd == null,
      deadline: const Duration(seconds: 30),
    );
    report.check('focus-retained-game-close-exit-zero');
  }
}
