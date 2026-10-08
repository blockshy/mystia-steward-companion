import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';

import 'generated/window_api.g.dart';
import 'probe_contract.dart';
import 'probe_view.dart';

typedef NativeExecute = Future<ProbeSnapshot> Function(ProbeCommand command);

Object? _diagnosticEvidence(String value) {
  if (value.length > 65536) {
    return {'unparsedPrefix': value.substring(0, 4096), 'truncated': true};
  }
  try {
    return jsonDecode(value);
  } on Object {
    return {'unparsedNativeDiagnostics': value};
  }
}

Map<String, Object?> snapshotJson(ProbeSnapshot value) => {
  'revision': value.revision,
  'frameRevision': value.frameRevision,
  'processId': value.processId,
  'topHwnd': value.topHwnd,
  'childHwnd': value.childHwnd,
  'targetProcessId': value.targetProcessId,
  'targetHwnd': value.targetHwnd,
  'visible': value.visible,
  'inputMode': value.inputMode.name,
  'topmost': value.topmost,
  'foregroundHwnd': value.foregroundHwnd,
  'foregroundProcessId': value.foregroundProcessId,
  'focusHwnd': value.focusHwnd,
  'flutterMouseDown': value.flutterMouseDown,
  'flutterMouseUp': value.flutterMouseUp,
  'targetMouseDown': value.targetMouseDown,
  'targetMouseUp': value.targetMouseUp,
  'flutterKeyDown': value.flutterKeyDown,
  'targetKeyDown': value.targetKeyDown,
  'hotkeyCount': value.hotkeyCount,
  'dpi': value.dpi,
  'devicePixelRatio': value.devicePixelRatio,
  'topAboveTarget': value.topAboveTarget,
  'sampleHitHwnd': value.sampleHitHwnd,
  'sampleHitProcessId': value.sampleHitProcessId,
  'pixels': value.pixels,
  'diagnostics': _diagnosticEvidence(value.diagnosticsJson),
};

class WindowProbeRunner {
  WindowProbeRunner({
    required this.report,
    required this.ui,
    required this.execute,
    required this.devicePixelRatio,
    required this.waitForDartFrame,
    required this.processId,
    this.runLifecycle,
  });
  final ProbeReport report;
  final ProbeUiController ui;
  final NativeExecute execute;
  final double Function() devicePixelRatio;
  final Future<void> Function() waitForDartFrame;
  final int processId;
  final Future<void> Function()? runLifecycle;
  int _revision = 1;
  ProbeSnapshot? _identity;
  bool _running = false;

  Future<ProbeSnapshot> _send(
    ProbeOperation operation, {
    ProbeInputMode mode = ProbeInputMode.interactive,
    int value = 0,
    String text = '',
    bool record = true,
  }) async {
    final command = ProbeCommand(
      operation: operation,
      revision: _revision,
      inputMode: mode,
      value: value,
      devicePixelRatio: devicePixelRatio(),
      text: text,
    );
    ProbeSnapshot snapshot;
    try {
      snapshot = await execute(command).timeout(const Duration(seconds: 10));
    } on PlatformException catch (error) {
      _recordNativeError(operation, error);
      if (error.code == 'blocked') {
        throw ProbeBlocked(
          error.message ?? 'Native environment blocked this check.',
        );
      }
      throw FatalProbe('${operation.name}: $error');
    } on Object catch (error) {
      throw FatalProbe(
        '${operation.name}: $error; no operation will be replayed.',
      );
    }
    try {
      _validate(snapshot);
    } on Object catch (error) {
      _record('rejected-native-observation', snapshot, {
        'operation': operation.name,
        'validationError': error.toString(),
      });
      throw FatalProbe(error.toString());
    }
    if (record) _record(operation.name, snapshot);
    return snapshot;
  }

  void _recordNativeError(ProbeOperation operation, PlatformException error) {
    final extra = <String, Object?>{
      'operation': operation.name,
      'errorCode': error.code,
      'errorMessage': error.message,
    };
    final details = error.details;
    if (details is ProbeSnapshot) {
      try {
        _validate(details);
        extra['snapshotValidated'] = true;
      } on Object catch (validationError) {
        extra['snapshotValidated'] = false;
        extra['validationError'] = validationError.toString();
      }
      _record('native-error-observation', details, extra);
      return;
    }
    Object? evidence;
    try {
      if (details is! String || details.length > 65536) {
        throw StateError('Native error details are missing or exceed 64 KiB.');
      }
      evidence = jsonDecode(details);
      if (evidence is! Map<String, dynamic> ||
          evidence['schemaVersion'] != 1 ||
          evidence['kind'] != 'native-error-fallback' ||
          evidence['nativeGitSha'] != report.gitSha ||
          evidence['runId'] != report.launch.runId) {
        throw StateError('Native error fallback schema/build/run differs.');
      }
      extra['fallbackValidated'] = true;
    } on Object catch (validationError) {
      extra['fallbackValidated'] = false;
      extra['validationError'] = validationError.toString();
      evidence = details is String ? _diagnosticEvidence(details) : null;
    }
    report.observations.add({
      'label': 'native-error-fallback',
      'observationUtc': DateTime.now().toUtc().toIso8601String(),
      ...extra,
      'nativeErrorDetails': evidence,
      'flutterUi': _flutterUiEvidence(),
    });
  }

  void _validate(ProbeSnapshot snapshot) {
    if (snapshot.diagnosticsJson.length > 65536) {
      throw StateError('Native diagnostics exceed 64 KiB.');
    }
    final diagnostics = jsonDecode(snapshot.diagnosticsJson);
    if (diagnostics is! Map<String, dynamic> ||
        diagnostics['schemaVersion'] != 1 ||
        diagnostics['nativeGitSha'] != report.gitSha) {
      throw StateError(
        'Native diagnostic schema/build identity differs from Dart.',
      );
    }
    final counters = [
      snapshot.frameRevision,
      snapshot.flutterMouseDown,
      snapshot.flutterMouseUp,
      snapshot.targetMouseDown,
      snapshot.targetMouseUp,
      snapshot.flutterKeyDown,
      snapshot.targetKeyDown,
      snapshot.hotkeyCount,
    ];
    if (snapshot.revision != _revision ||
        counters.any((value) => value < 0) ||
        snapshot.processId != processId ||
        snapshot.targetProcessId <= 0 ||
        snapshot.targetProcessId == processId ||
        snapshot.topHwnd == 0 ||
        snapshot.childHwnd == 0 ||
        snapshot.targetHwnd == 0 ||
        snapshot.dpi <= 0 ||
        !snapshot.devicePixelRatio.isFinite ||
        snapshot.devicePixelRatio <= 0 ||
        (snapshot.devicePixelRatio - devicePixelRatio()).abs() > 0.01 ||
        (snapshot.dpi / 96 - devicePixelRatio()).abs() > 0.01 ||
        (snapshot.pixels.isNotEmpty && snapshot.pixels.length != 9) ||
        snapshot.pixels.any((value) => value < 0 || value > 255)) {
      throw StateError(
        'Native snapshot identity, revision, geometry or counter range is invalid.',
      );
    }
    final identity = _identity;
    if (identity != null &&
        (snapshot.topHwnd != identity.topHwnd ||
            snapshot.childHwnd != identity.childHwnd ||
            snapshot.targetHwnd != identity.targetHwnd ||
            snapshot.targetProcessId != identity.targetProcessId)) {
      throw StateError(
        'A retained probe window or underlay process was replaced.',
      );
    }
  }

  void _record(
    String label,
    ProbeSnapshot snapshot, [
    Map<String, Object?> extra = const {},
  ]) {
    report.observations.add({
      'label': label,
      'observationUtc': DateTime.now().toUtc().toIso8601String(),
      ...extra,
      ...snapshotJson(snapshot),
      'flutterUi': _flutterUiEvidence(),
    });
  }

  Map<String, Object?> _flutterUiEvidence() => {
    'pointerDown': ui.pointerCount,
    'keyDown': ui.keyCount,
    'focused': ui.focused,
    'paintRevision': ui.paint.revision,
  };

  Future<ProbeSnapshot> _wait(
    String label,
    bool Function(ProbeSnapshot) predicate, {
    bool pixels = false,
    bool allowPixelMismatch = false,
  }) async {
    final watch = Stopwatch()..start();
    while (true) {
      final snapshot = await _send(
        pixels ? ProbeOperation.capture : ProbeOperation.inspect,
        record: false,
      );
      if (predicate(snapshot)) {
        _record(label, snapshot);
        return snapshot;
      }
      if (watch.elapsed >= const Duration(seconds: 5)) {
        _record('$label-timeout', snapshot);
        if (allowPixelMismatch &&
            snapshot.frameRevision == _revision &&
            snapshot.visible &&
            snapshot.topmost &&
            snapshot.topAboveTarget &&
            snapshot.pixels.length == 9) {
          throw PixelMismatch(
            'Screen pixels did not match $label; identities and rendered frame remained valid.',
          );
        }
        throw FatalProbe('No native evidence for $label before the deadline.');
      }
      // This interval only bounds polling. Passing always requires fresh evidence.
      await Future<void>.delayed(const Duration(milliseconds: 50));
    }
  }

  Future<void> _case(String name, Future<void> Function() action) async {
    ui.setDetail(name);
    try {
      await action();
      report.check(name, true);
    } on ProbeBlocked catch (error) {
      report.checks.add({
        'name': name,
        'status': 'BLOCKED',
        'detail': error.message,
      });
      report.errors.add('$name: ${error.message}');
    } on PixelMismatch catch (error) {
      report.checks.add({
        'name': name,
        'status': 'FAIL',
        'detail': error.message,
      });
      report.errors.add('$name: ${error.message}');
    } on Object catch (error) {
      report.checks.add({
        'name': name,
        'status': 'FAIL',
        'detail': error.toString(),
      });
      throw FatalProbe('$name: $error');
    }
  }

  Future<void> restore() async {
    await _send(ProbeOperation.showInteractive);
    await _wait(
      'show-interactive-observed',
      (state) => state.visible && state.inputMode == ProbeInputMode.interactive,
    );
  }

  Future<void> _paint(int background, int content) async {
    _revision++;
    await _send(ProbeOperation.armFrame);
    ui.setPaint(PaintSpec(_revision, background, content));
    try {
      await waitForDartFrame().timeout(const Duration(seconds: 5));
    } on Object catch (error) {
      throw FatalProbe('Dart frame did not settle: $error');
    }
    await _send(ProbeOperation.presentFrame);
    await _wait(
      'native-frame-presented',
      (state) => state.frameRevision == _revision,
    );
  }

  void _require(bool condition, String message) {
    if (!condition) throw FatalProbe(message);
  }

  Future<void> _alpha(int background, int content, int underlay) async {
    await restore();
    await _send(ProbeOperation.setTopmost, value: 1);
    await _send(
      ProbeOperation.setUnderlayColor,
      value: underlay == 255 ? 1 : 0,
    );
    await _paint(background, content);
    final expectedBackground = composedRgb(ui.paint, underlay, content: false);
    final expectedContent = composedRgb(ui.paint, underlay, content: true);
    final expected = [
      ...expectedBackground,
      ...expectedContent,
      underlay,
      underlay,
      underlay,
    ];
    var matched = false;
    try {
      await _wait(
        'alpha-$background-$content-$underlay',
        (state) {
          if (state.frameRevision != _revision ||
              !state.visible ||
              !state.topmost ||
              !state.topAboveTarget ||
              state.pixels.length != 9) {
            return false;
          }
          for (var index = 0; index < expected.length; index++) {
            if ((state.pixels[index] - expected[index]).abs() > 5) return false;
          }
          matched = true;
          return true;
        },
        pixels: true,
        allowPixelMismatch: true,
      );
    } finally {
      report.observations.add({
        'label': 'alpha-expectation',
        ...ui.paint.toJson(),
        'underlayRgb': [underlay, underlay, underlay],
        'expectedPixels': expected,
        'tolerancePerChannel': 5,
        'matched': matched,
      });
    }
  }

  Future<ProbeSnapshot> _interactiveBaseline() async {
    await restore();
    await _send(ProbeOperation.setTopmost, value: 1);
    await _paint(255, 255);
    return _wait(
      'interactive-target',
      (state) =>
          state.visible &&
          state.inputMode == ProbeInputMode.interactive &&
          state.sampleHitProcessId == processId &&
          (state.sampleHitHwnd == state.topHwnd ||
              state.sampleHitHwnd == state.childHwnd),
    );
  }

  Future<void> _click(bool underlay) async {
    final before = await _send(ProbeOperation.inspect);
    final uiPointerBefore = ui.pointerCount;
    _require(
      before.visible &&
          before.inputMode ==
              (underlay
                  ? ProbeInputMode.passThrough
                  : ProbeInputMode.interactive),
      'Unexpected input mode before the one click.',
    );
    final owner = underlay ? before.targetProcessId : before.processId;
    _require(
      before.sampleHitProcessId == owner,
      'Physical click location does not belong to the intended controlled window.',
    );
    await _send(ProbeOperation.clickSample);
    await _wait(
      underlay ? 'underlay-click-observed' : 'flutter-click-observed',
      (state) =>
          state.flutterMouseDown ==
              before.flutterMouseDown + (underlay ? 0 : 1) &&
          state.flutterMouseUp == before.flutterMouseUp + (underlay ? 0 : 1) &&
          state.targetMouseDown ==
              before.targetMouseDown + (underlay ? 1 : 0) &&
          state.targetMouseUp == before.targetMouseUp + (underlay ? 1 : 0) &&
          ui.pointerCount == uiPointerBefore + (underlay ? 0 : 1),
    );
  }

  Future<void> _pointerModes() async {
    await _interactiveBaseline();
    await _click(false);
    await _send(ProbeOperation.setInputMode, mode: ProbeInputMode.passThrough);
    await _wait(
      'pass-through-observed',
      (state) =>
          state.inputMode == ProbeInputMode.passThrough &&
          state.sampleHitProcessId == state.targetProcessId,
    );
    await _click(true);
    await restore();
    await _wait(
      'restored-hit-test',
      (state) => state.sampleHitProcessId == state.processId,
    );
    await _click(false);
  }

  Future<void> _hideRestore(ProbeOperation operation) async {
    await _interactiveBaseline();
    await _send(ProbeOperation.setInputMode, mode: ProbeInputMode.passThrough);
    await _send(operation);
    await _wait('${operation.name}-observed', (state) => !state.visible);
    await restore();
    await _wait(
      '${operation.name}-restored-hit',
      (state) => state.sampleHitProcessId == processId,
    );
    await _click(false);
  }

  Future<void> _keyboard(bool underlay) async {
    await _interactiveBaseline();
    if (underlay) {
      await _send(
        ProbeOperation.setInputMode,
        mode: ProbeInputMode.passThrough,
      );
      await _send(ProbeOperation.focusUnderlay);
    } else {
      await _click(false);
    }
    final focused = await _wait(
      underlay ? 'underlay-focus' : 'flutter-focus',
      (state) =>
          state.foregroundHwnd ==
              (underlay ? state.targetHwnd : state.topHwnd) &&
          state.foregroundProcessId ==
              (underlay ? state.targetProcessId : state.processId) &&
          state.focusHwnd == (underlay ? state.targetHwnd : state.childHwnd),
    );
    final uiKeyBefore = ui.keyCount;
    await _send(ProbeOperation.sendKey);
    await _wait(
      underlay ? 'underlay-key-observed' : 'flutter-key-observed',
      (state) =>
          state.flutterKeyDown == focused.flutterKeyDown + (underlay ? 0 : 1) &&
          state.targetKeyDown == focused.targetKeyDown + (underlay ? 1 : 0) &&
          ui.keyCount == uiKeyBefore + (underlay ? 0 : 1),
    );
  }

  Future<void> _topmost() async {
    await _interactiveBaseline();
    await _send(ProbeOperation.setUnderlayColor, value: 1);
    await _send(ProbeOperation.setTopmost, value: 1);
    await _send(ProbeOperation.focusUnderlay);
    await _wait(
      'topmost-actual-stacking',
      (state) =>
          state.topmost &&
          state.topAboveTarget &&
          state.sampleHitProcessId == processId &&
          _contentPixelMatches(state, [224, 64, 32]),
      pixels: true,
    );
    await _send(ProbeOperation.setTopmost, value: 0);
    await _send(ProbeOperation.focusUnderlay);
    await _wait(
      'normal-actual-stacking',
      (state) =>
          !state.topmost &&
          !state.topAboveTarget &&
          state.sampleHitProcessId == state.targetProcessId &&
          _contentPixelMatches(state, [255, 255, 255]),
      pixels: true,
    );
    await restore();
    await _send(ProbeOperation.setTopmost, value: 1);
  }

  bool _contentPixelMatches(ProbeSnapshot state, List<int> expected) =>
      state.pixels.length == 9 &&
      List.generate(
        3,
        (index) => (state.pixels[index + 3] - expected[index]).abs(),
      ).every((difference) => difference <= 5);

  Future<void> _hotkey() async {
    await _interactiveBaseline();
    await _send(ProbeOperation.setInputMode, mode: ProbeInputMode.passThrough);
    await _send(ProbeOperation.focusUnderlay);
    final before = await _send(ProbeOperation.inspect);
    _require(
      before.foregroundProcessId == before.targetProcessId,
      'F10 target is not the controlled underlay.',
    );
    await _send(ProbeOperation.sendF10);
    await _wait(
      'real-f10-restore',
      (state) =>
          state.hotkeyCount == before.hotkeyCount + 1 &&
          state.visible &&
          state.inputMode == ProbeInputMode.interactive &&
          state.sampleHitProcessId == state.processId,
    );
    await _click(false);
  }

  Future<int> run() async {
    if (_running) throw StateError('This probe suite cannot be replayed.');
    _running = true;
    try {
      if (runLifecycle == null && !report.coreOnlyTest) {
        throw StateError('Production runs require the lifecycle suite.');
      }
      final initial = await _send(
        ProbeOperation.initialize,
        text: report.gitSha,
      );
      _identity = initial;
      report.context.addAll(snapshotJson(initial));
      report.context['samplingGeometry'] = {
        'background': [48, 48],
        'content': [160, 96],
        'clear': [32, 272],
        'coordinateSpace': 'Flutter client logical pixels',
      };
      report.context['backgroundRgb'] = rgbChannels(probeBackgroundRgb);
      report.context['contentRgb'] = rgbChannels(probeContentRgb);
      report.context['flutterDevicePixelRatio'] = devicePixelRatio();
      report.check('compiled-native-dart-identity-and-owned-underlay', true);
      for (final background in probeAlphaValues) {
        for (final content in probeAlphaValues) {
          for (final underlay in [0, 255]) {
            await _case(
              'alpha-$background-$content-${underlay == 0 ? 'black' : 'white'}',
              () => _alpha(background, content, underlay),
            );
          }
        }
      }
      await _case(
        'same-point-interactive-pass-through-restored',
        _pointerModes,
      );
      await _case(
        'hide-restores-interactive',
        () => _hideRestore(ProbeOperation.hide),
      );
      await _case(
        'wm-close-hides-and-restores-interactive',
        () => _hideRestore(ProbeOperation.closeToHide),
      );
      await _case(
        'keyboard-to-flutter-exact-foreground',
        () => _keyboard(false),
      );
      await _case(
        'keyboard-to-underlay-exact-foreground',
        () => _keyboard(true),
      );
      await _case('topmost-style-and-actual-stacking', _topmost);
      await _case('registered-f10-real-input-restores-interactive', _hotkey);
      if (runLifecycle != null) await runLifecycle!();
      report.status = report.checks.any((value) => value['status'] == 'FAIL')
          ? 'FAIL'
          : report.checks.any((value) => value['status'] == 'BLOCKED')
          ? 'BLOCKED'
          : 'PASS';
    } on ProbeBlocked catch (error) {
      report.status = 'BLOCKED';
      report.errors.add(error.message);
    } on Object catch (error) {
      report.status = 'FAIL';
      report.errors.add(error.toString());
    }
    ui.setDetail('${report.status}：正在归档真实观测并退出');
    final exitCode = report.status == 'PASS' ? 0 : 1;
    // The native side revalidates its fixed node path, writes CREATE_NEW, then
    // destroys only the retained underlay and quits normally with this code.
    await execute(
      ProbeCommand(
        operation: ProbeOperation.finish,
        revision: _revision,
        inputMode: ProbeInputMode.interactive,
        value: exitCode,
        devicePixelRatio: devicePixelRatio(),
        text: report.encode(),
      ),
    ).timeout(const Duration(seconds: 10));
    return exitCode;
  }

  Future<void> retireCore() async {
    ProbeSnapshot snapshot;
    try {
      snapshot = await execute(
        ProbeCommand(
          operation: ProbeOperation.retireCore,
          revision: _revision,
          inputMode: ProbeInputMode.interactive,
          value: 0,
          devicePixelRatio: devicePixelRatio(),
          text: '',
        ),
      ).timeout(const Duration(seconds: 10));
    } on PlatformException catch (error) {
      _recordNativeError(ProbeOperation.retireCore, error);
      throw FatalProbe('retireCore: ${error.code}: ${error.message}');
    } on Object catch (error) {
      throw FatalProbe('retireCore: $error; retirement will not be replayed.');
    }
    final diagnostics = jsonDecode(snapshot.diagnosticsJson);
    if (snapshot.processId != processId ||
        diagnostics is! Map<String, dynamic> ||
        diagnostics['nativeGitSha'] != report.gitSha ||
        diagnostics['coreRetired'] != true ||
        diagnostics['coreTargetExitCode'] != 0 ||
        diagnostics['coreHotkeyReleased'] != true ||
        diagnostics['coreControllerHidden'] != true ||
        diagnostics['coreControllerTopmost'] != false) {
      throw const FatalProbe('Core fixture retirement was not verified.');
    }
    report.context['retiredCore'] = diagnostics;
  }
}
