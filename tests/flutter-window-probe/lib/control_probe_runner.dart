import 'dart:async';
import 'dart:io';
import 'dart:ui' show AppExitType;

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import 'control_probe_contract.dart';
import 'control_probe_view.dart';
import 'generated/control_probe_api.g.dart';

typedef ControlExecute = Future<ControlSnapshot> Function(
  ControlCommand command,
);

class ControlRunner {
  ControlRunner({
    required this.report,
    required this.ui,
    required this.execute,
    required this.processId,
    required this.frame,
  });
  final ControlReport report;
  final ControlUi ui;
  final ControlExecute execute;
  final int processId;
  final Future<void> Function() frame;
  Future<void> _tail = Future<void>.value();
  int _requestId = 0, _sequence = 0;
  ControlState? _last;
  Object? _inputFailure;

  Future<ControlState> _send(ControlOperation operation, {int? f8Count}) {
    final result = _tail.then((_) async {
      if (operation != ControlOperation.inspect) _requestId++;
      final command = ControlCommand(
        operation: operation,
        requestId: _requestId,
        flutterFocused: ui.focused,
        dartF8DownCount: f8Count ?? ui.f8Down,
      );
      late final ControlSnapshot snapshot;
      try {
        snapshot = await execute(command).timeout(const Duration(seconds: 10));
      } on PlatformException catch (error) {
        final details = error.details;
        if (details is String &&
            details.length <= 1048576 &&
            report.observations.length < 64) {
          report.observations.add({
            'label': 'native-error',
            'errorCode': error.code,
            'unvalidatedNativeSnapshotJson': details,
            'flutterUi': ui.toJson(),
          });
        }
        rethrow;
      }
      final state = ControlState.parse(
        snapshot,
        report.gitSha,
        report.launch.runId,
        processId,
        _sequence,
        legacy: report.legacy,
      );
      _sequence = snapshot.sequence;
      _last = state;
      final error = state.value['error'];
      if (error != null && error != '' && state.flag('errorBlocked')) {
        throw ControlBlocked('Native control blocked: $error');
      }
      requireControl(
        error == null || error == '',
        'Native control failed: $error',
      );
      requireControl(
        state.number('legacyUnsupportedCount') == 0,
        'An unsupported legacy control message reached the new host.',
      );
      // Flutter's View maps an autofocus request to native SetFocus on
      // Windows. Cold MSC1 clients must not issue that request before the
      // game grants foreground and consumes the actual activation ACK.
      if (!ui.focusRequestsEnabled &&
          report.clientGeneration != 0 &&
          !report.legacy &&
          state.flag('registered') &&
          state.number('registrationCount') == 1 &&
          state.number('activationCount') == 1 &&
          state.number('source') == (report.clientGeneration == 1 ? 0 : 1) &&
          !state.flag('activationPending') &&
          state.flag('clientForeground') &&
          state.flag('childFocused') &&
          state.flag('visible') &&
          state.flag('interactive')) {
        ui.allowFocusAfterNativeActivation();
        _record('client-native-activation-allows-dart-focus', state);
      }
      return state;
    });
    _tail = result.then<void>((_) {}, onError: (Object _) {});
    return result;
  }

  Future<ControlState> _wait(
    bool Function(ControlState) predicate,
    String step, {
    int seconds = 8,
    bool manual = false,
  }) async {
    final watch = Stopwatch()..start();
    while (watch.elapsed < Duration(seconds: seconds)) {
      final failure = _inputFailure;
      if (failure != null) throw failure;
      final state = await _send(ControlOperation.inspect);
      if (predicate(state)) return state;
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }
    if (manual) throw ControlBlocked('User-operated RS step timed out: $step.');
    throw ControlFailure('Actual control state was not observed: $step.');
  }

  void _record(String label, ControlState state) =>
      report.observe(label, state, ui.toJson());
  bool _client(ControlState state) =>
      state.flag('clientForeground') &&
      state.flag('childFocused') &&
      ui.focused &&
      state.flag('visible') &&
      state.flag('interactive') &&
      !state.flag('activationPending');
  bool _game(ControlState state) =>
      state.flag('gameForeground') &&
      state.flag('gameFocusOwned') &&
      !state.flag('returnPending');

  Future<ControlState> _activateF8(int expected, String label) async {
    await _send(ControlOperation.pressF8);
    var state = await _wait(
      (s) =>
          s.number('activationCount') == expected &&
          s.number('source') == 1 &&
          _client(s),
      label,
    );
    await _send(ControlOperation.releaseF8);
    state = await _wait(
      (s) => !s.flag('injectedF8Held') && _client(s),
      '$label key released',
    );
    requireControl(
      state.number('activationAttempts') == expected,
      'An activation was retried or did not receive an applied acknowledgement.',
    );
    _record(label, state);
    return state;
  }

  Future<ControlState> _returnF8(int expected, String label) async {
    final before = ui.f8Down;
    await _send(ControlOperation.pressF8);
    var state = await _wait(
      (s) =>
          s.number('f8ReturnCount') == expected &&
          ui.f8Down == before + 1 &&
          _game(s),
      label,
    );
    await _send(ControlOperation.releaseF8);
    state = await _wait(
      (s) => !s.flag('injectedF8Held') && _game(s),
      '$label key released',
    );
    requireControl(
      state.flag('visible') && state.flag('interactive'),
      'Returning focus must keep the Flutter window visible and interactive.',
    );
    _record(label, state);
    return state;
  }

  Future<void> _hold(ControlState initial, {required bool inClient}) async {
    final watch = Stopwatch()..start();
    var state = initial;
    var previous = 0, maximumGap = 0, samples = 0;
    final activations = initial.number('activationCount');
    final returns = initial.number('rsReturnCount');
    final slot = initial.connected.length == 1
        ? initial.connected.single['index']
        : null;
    while (true) {
      final now = watch.elapsedMicroseconds;
      final gap = now - previous;
      if (gap > maximumGap) maximumGap = gap;
      requireControl(
        gap <= 250000 &&
            state.connected.length == 1 &&
            state.connected.single['index'] == slot &&
            state.rightStickHeld &&
            (inClient ? _client(state) : _game(state)) &&
            state.number('activationCount') == activations &&
            state.number('rsReturnCount') == returns,
        'RS was released early, input/focus changed, or the same held press bounced across focus.',
      );
      samples++;
      if (now >= 1000000) break;
      previous = now;
      await Future<void>.delayed(const Duration(milliseconds: 25));
      state = await _send(ControlOperation.inspect);
    }
    report.context[inClient ? 'clientHeldRs' : 'gameHeldRs'] = {
      'samples': samples,
      'elapsedMicros': watch.elapsedMicroseconds,
      'maximumSampleGapMicros': maximumGap,
      'slot': slot,
    };
    _record(
      inClient ? 'rs-held-client-no-bounce' : 'rs-held-game-no-bounce',
      state,
    );
  }

  Future<void> run() async {
    report.context['processId'] = processId;
    ui.onF8 = (count) {
      unawaited(
        _send(ControlOperation.returnGame, f8Count: count).then<void>(
          (state) {
            _record('dart-f8-return-request', state);
          },
          onError: (Object error) {
            _inputFailure = error;
          },
        ),
      );
    };
    try {
      await _send(ControlOperation.initialize);
      var state = await _wait(
        (s) => s.flag('gameReady') && s.flag('registered'),
        'first real Mod Update registration',
        seconds: 90,
      );
      requireControl(
        state.number('registrationCount') == 1 &&
            state.number('listenerOwnerPid') == processId &&
            state.flag('pipeConnected') &&
            state.number('activationCount') == 0,
        'Initial registration unexpectedly activated or changed its listener.',
      );
      report.context.addAll({
        'processId': processId,
        'protocol': 'MSC1/1/208',
        'manualDeadlineSeconds': 240,
        'holdMinimumMicros': 1000000,
        'maximumSampleGapMicros': 250000,
        'autoLaunch': false,
      });
      _record('registered-real-mod', state);
      report.check('control-real-mod-registered-identity');
      ui.show('正在自动验证 F8 往返、窗口隐藏和穿透恢复。请暂时不要操作手柄。');
      await frame();
      await _send(ControlOperation.focusGame);
      await _wait(_game, 'initial exact game foreground');
      state = await _activateF8(1, 'real-mod-f8-first-activation');
      report.check('control-f8-game-to-flutter');
      final pointer = ui.pointerDown, key = ui.f24Down;
      final nativePointer = state.number('nativeMouseDown'),
          nativeKey = state.number('nativeF24Down');
      await _send(ControlOperation.clickProbe);
      await _send(ControlOperation.sendFocusKey);
      state = await _wait(
        (s) =>
            _client(s) &&
            ui.pointerDown > pointer &&
            ui.f24Down > key &&
            s.number('nativeMouseDown') > nativePointer &&
            s.number('nativeF24Down') > nativeKey,
        'native and Flutter input',
      );
      _record('native-and-dart-input', state);
      report.check('control-native-and-dart-input');
      await _returnF8(1, 'dart-f8-first-return');
      report.check('control-f8-flutter-to-game-visible');

      state = await _send(ControlOperation.hideProbe);
      requireControl(
        _game(state) && !state.flag('visible'),
        'Game foreground changed while hiding Flutter.',
      );
      _record('hidden-before-f8', state);
      await _activateF8(2, 'real-mod-f8-hidden-recovery');
      report.check('control-f8-hidden-recovery');
      await _returnF8(2, 'dart-f8-second-return');
      state = await _send(ControlOperation.setPassThrough);
      requireControl(
        _game(state) && !state.flag('interactive'),
        'Game foreground changed while enabling passthrough.',
      );
      _record('passthrough-before-f8', state);
      await _activateF8(3, 'real-mod-f8-passthrough-recovery');
      report.check('control-f8-passthrough-recovery');

      ui.show('F8 自动验证已完成。接下来游戏会获得焦点：请向下按住右摇杆 RS，看到本窗口后继续保持，等待“松开”提示。');
      await frame();
      await _returnF8(3, 'dart-f8-ready-for-physical-rs');
      state = await _wait(
        (s) =>
            s.number('activationCount') == 4 &&
            s.number('source') == 2 &&
            _client(s),
        'game RS activates Flutter',
        seconds: 240,
        manual: true,
      );
      requireControl(
        state.number('activationAttempts') == 4 &&
            state.number('rsReturnCount') == 0,
        'RS activation was retried or bounced before its hold check.',
      );
      _record('real-mod-rs-activation', state);
      report.check('control-rs-game-to-flutter');
      ui.show('已收到游戏 RS。请继续按住右摇杆，等待“松开”提示。');
      await _hold(state, inClient: true);
      ui.show('现在松开 RS，手柄恢复中立。随后按提示进行第二次按压。');
      state = await _wait(
        (s) => s.neutral && s.flag('rsArmed') && _client(s),
        'client neutral rearm',
        seconds: 240,
        manual: true,
      );
      _record('client-rs-neutral-rearmed', state);
      ui.show('请再次向下按住 RS；游戏获得焦点后继续保持至少两秒，再松开。测试会自动关闭游戏副本。');
      state = await _wait(
        (s) => s.number('rsReturnCount') == 1 && _game(s),
        'Flutter RS returns game foreground',
        seconds: 240,
        manual: true,
      );
      requireControl(
        state.number('rsEdgeCount') == 1 &&
            state.flag('visible') &&
            state.flag('interactive') &&
            state.number('activationCount') == 4,
        'The client RS edge did not preserve the visible window or bounced.',
      );
      _record('client-rs-returned-game', state);
      await _hold(state, inClient: false);
      state = await _wait(
        (s) => s.neutral && _game(s),
        'release final game RS',
        seconds: 240,
        manual: true,
      );
      requireControl(
        state.number('activationCount') == 4 &&
            state.number('rsReturnCount') == 1,
        'Releasing RS unexpectedly activated another action.',
      );
      _record('final-rs-released', state);
      report.check('control-rs-held-no-bounce');
      report.check('control-rs-flutter-to-game-visible');
      await _send(ControlOperation.closeGame);
      state = await _wait(
        (s) => !s.flag('gameAlive') && s.value['gameExitCode'] == 0,
        'retained game process exits zero',
        seconds: 35,
      );
      requireControl(
        state.flag('closeRequested') &&
            !state.flag('injectedF8Held') &&
            state.number('injectedDownCount') ==
                state.number('injectedUpCount'),
        'Normal close/input cleanup evidence is incomplete.',
      );
      _record('retained-game-exit-zero', state);
      report.check('control-retained-game-close-exit-zero');
    } finally {
      ui.onF8 = null;
      if (_last != null) _record('runner-final', _last!);
    }
  }

  Future<void> runClient() async {
    report.context.addAll({
      'processId': processId,
      'generation': report.clientGeneration,
      'protocol': 'MSC1/1/208',
    });
    ui.onF8 = (count) {
      unawaited(
        _send(ControlOperation.returnGame, f8Count: count).then<void>(
          (state) => _record('client-dart-return-request', state),
          onError: (Object error) {
            _inputFailure = error;
          },
        ),
      );
    };
    try {
      ui.show('正在自动验证 Mod 首次启动、客户端换代与退出通知。无需操作。');
      await _send(ControlOperation.initialize);
      var state = await _wait(
        (s) => s.flag('registered'),
        'real Mod client registration',
        seconds: 90,
      );
      requireControl(
        state.number('registrationCount') == 1 &&
            state.number('listenerOwnerPid') == processId &&
            state.value['clientGeneration'] == report.clientGeneration &&
            state.value['modLaunchedClient'] == true,
        'The real Mod did not launch this exact client generation.',
      );
      _record('client-real-mod-launch', state);
      report.check('client-real-mod-launch');
      report.check('client-msc1-registration');
      state = await _wait(
        (s) => s.number('activationCount') == 1 && _client(s),
        'client initial activation',
      );
      requireControl(
        state.number('source') == (report.clientGeneration == 1 ? 0 : 1) &&
            state.number('activationAttempts') == 1,
        'Unexpected first-activation source or repeated action.',
      );
      _record('client-activation', state);
      report.check('client-activation');
      final pointer = ui.pointerDown, key = ui.f24Down;
      await _send(ControlOperation.clickProbe);
      await _send(ControlOperation.sendFocusKey);
      state = await _wait(
        (s) =>
            _client(s) &&
            ui.pointerDown == pointer + 1 &&
            ui.f24Down == key + 1 &&
            s.number('nativeMouseDown') == 1 &&
            s.number('nativeF24Down') == 1,
        'client actual input',
      );
      _record('client-native-and-dart-input', state);
      report.check('client-native-and-dart-input');
      state = await _returnF8(1, 'client-dart-return');
      report.check('client-dart-return');
      if (report.clientGeneration == 1) {
        requireControl(
          state.flag('gameAlive') && _game(state),
          'Game is not retained for the next generation.',
        );
        _record('client-retired-game-alive', state);
        report.check('client-retired-game-alive');
      } else {
        await _send(ControlOperation.closeGame);
        state = await _wait(
          (s) => !s.flag('gameAlive') && s.value['gameExitCode'] == 0,
          'client retained game exit',
          seconds: 35,
        );
        requireControl(
          state.flag('exitReceived'),
          'The real Mod shutdown did not deliver Exit/ExitAck.',
        );
        _record('client-exit-notified', state);
        report.check('client-exit-notified');
      }
    } finally {
      ui.onF8 = null;
      if (_last != null) _record('client-final', _last!);
    }
  }

  Future<void> runLegacyClient() async {
    report.context.addAll({
      'processId': processId,
      'generation': 1,
      'protocol': 'legacy-tcp-eof',
      'scenario': 'old-mod-legacy-client',
      'mouseRecovery': 'marked-automated-os-input',
      'asfwCalled': false,
    });
    try {
      ui.show('正在验证旧 Mod 首次启动与已有实例。鼠标点击由受限 OS 自动化执行，无需操作游戏。');
      await _send(ControlOperation.initialize);
      var state = await _wait(
        (s) => s.legacy['ready'] == true,
        'original Mod EOF show and actual startup foreground outcome',
        seconds: 90,
      );
      requireControl(
        state.value['modLaunchedClient'] == true &&
            state.value['clientGeneration'] == 1 &&
            state.number('listenerOwnerPid') == processId &&
            state.legacy['showCount'] == 1 &&
            state.legacy['toggleCount'] == 0,
        'Original Mod did not launch this exact retained client and complete its first show.',
      );
      _record('legacy-real-mod-launch', state);
      report.check('legacy-real-mod-launch');
      report.context['startupAutomaticForeground'] =
          state.legacy['automaticForeground'];
      report.context['startupClickRequired'] = state.legacy['clickRequired'];
      report.check(
        'legacy-startup-show',
        detail: state.legacy['automaticForeground'] == true
            ? 'One startup foreground attempt was actually observed.'
            : 'Startup automatic foreground was not obtained; visible interactive recovery is required.',
      );

      Future<ControlState> actualInput(int count, String label) async {
        await _send(ControlOperation.clickProbe);
        await _wait(
          (s) =>
              _client(s) &&
              ui.pointerDown == count &&
              s.number('nativeMouseDown') == count &&
              s.number('nativeMouseUp') == count,
          '$label actual click recovery',
        );
        await _send(ControlOperation.sendFocusKey);
        final observed = await _wait(
          (s) =>
              _client(s) &&
              ui.f24Down == count &&
              s.number('nativeF24Down') == count,
          '$label native and Dart keyboard',
        );
        _record(label, observed);
        return observed;
      }

      state = await actualInput(1, 'legacy-first-native-and-dart-input');
      report.check('legacy-native-and-dart-input');
      await _send(ControlOperation.focusGame);
      await _wait(
        _game,
        'legacy exact game foreground before existing-instance F8',
      );
      state = await _wait(
        (s) =>
            _game(s) &&
            legacyPublicationReady(
              s.legacy['snapshotPublication'],
              s.number('gamePid'),
            ),
        'original cached snapshot published after actual game focus',
        seconds: 35,
      );
      _record('legacy-post-focus-publication', state);
      await _send(ControlOperation.pressF8);
      state = await _wait(
        (s) =>
            s.legacy['toggleCount'] == 1 &&
            s.legacy['clickRequired'] == true &&
            _game(s),
        'original Mod existing-instance EOF toggle',
      );
      requireControl(
        state.legacy['showCount'] == 1 &&
            state.number('listenerOwnerPid') == processId &&
            state.flag('visible') &&
            state.flag('interactive') &&
            !state.flag('clientForeground'),
        'Legacy existing-instance fallback changed identity or claimed automatic foreground.',
      );
      await _send(ControlOperation.releaseF8);
      state = await _wait(
        (s) => !s.flag('injectedF8Held') && _game(s),
        'legacy F8 release',
      );
      _record('legacy-existing-instance-toggle', state);
      report.check('legacy-existing-instance-toggle');
      state = await actualInput(2, 'legacy-existing-instance-click-recovery');
      requireControl(
        (state.legacy['backgroundClickCount'] as int) >= 1 &&
            state.legacy['clickCount'] == 2,
        'Legacy fallback lacks an actual marked background mouse recovery.',
      );
      report.check('legacy-click-recovery');
      await _send(ControlOperation.focusGame);
      await _wait(_game, 'legacy final exact game foreground');
      await _send(ControlOperation.closeGame);
      state = await _wait(
        (s) => !s.flag('gameAlive') && s.value['gameExitCode'] == 0,
        'legacy retained game normal exit',
        seconds: 35,
      );
      requireControl(
        state.flag('closeRequested') &&
            state.number('injectedDownCount') == 1 &&
            state.number('injectedUpCount') == 1 &&
            !state.flag('injectedF8Held'),
        'Legacy exit/input evidence is incomplete.',
      );
      _record('legacy-retained-game-exit-zero', state);
      report.check('legacy-retained-game-close-exit-zero');
    } finally {
      if (_last != null) _record('legacy-client-final', _last!);
    }
  }
}

Future<void> startControlProbe(
  List<String> arguments,
  String compiledGitSha, {
  bool client = false,
  bool legacy = false,
}) async {
  final ui = ControlUi(deferNativeFocus: client && !legacy);
  var finishing = false;
  try {
    if (!Platform.isWindows) {
      throw UnsupportedError('The control probe requires Windows.');
    }
    final report = ControlReport(
      client
          ? ControlLaunch.client(arguments, compiledGitSha, legacy: legacy)
          : ControlLaunch.parse(arguments, compiledGitSha),
      compiledGitSha,
      clientGeneration: client ? int.parse(arguments[2]) : 0,
      legacy: legacy,
    );
    final binding = WidgetsFlutterBinding.ensureInitialized();
    final api = ControlProbeHostApi();
    runApp(ControlProbeApp(ui: ui));
    await binding.endOfFrame;
    try {
      final runner = ControlRunner(
        report: report,
        ui: ui,
        execute: api.execute,
        processId: pid,
        frame: () => binding.endOfFrame,
      );
      if (legacy) {
        await runner.runLegacyClient();
      } else if (client) {
        await runner.runClient();
      } else {
        await runner.run();
      }
      report.status = 'PASS';
    } on Object catch (error) {
      report.status =
          error is ControlBlocked ||
              (error is PlatformException && error.code == 'blocked')
          ? 'BLOCKED'
          : 'FAIL';
      report.errors.add(error.toString());
      final remaining = report.requiredChecks.where(
        (name) => !report.checks.any((item) => item['name'] == name),
      );
      if (remaining.isNotEmpty) {
        report.check(
          remaining.first,
          status: report.status,
          detail: error.toString(),
        );
      }
      ui.show('探针 ${report.status}：$error');
    }
    final encoded = report.encode();
    finishing = true;
    await api
        .finish(
          encoded,
          report.status == 'PASS'
              ? 0
              : report.status == 'BLOCKED'
              ? 2
              : 1,
        )
        .timeout(const Duration(seconds: 45));
  } on Object catch (error) {
    stderr.writeln(
      'Control probe ${finishing ? 'publication' : 'startup'} failed: $error',
    );
    ui.show('探针失败：$error');
    await ServicesBinding.instance.exitApplication(AppExitType.required, 1);
  }
}
