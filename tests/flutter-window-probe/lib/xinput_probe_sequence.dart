import 'generated/input_probe_api.g.dart';
import 'input_probe_contract.dart';

enum XInputStep {
  neutral,
  firstPress,
  firstHold,
  firstRelease,
  secondPress,
  secondRelease,
  complete,
}

// A probe-only observation state machine: it never dispatches product actions.
class XInputProbeSequence {
  XInputStep step = XInputStep.neutral;
  int edges = 0;
  int resets = 0;
  int maxSampleGapMicros = 0;
  int sequenceMaxGapMicros = 0;
  int? slot;
  int? _neutralSince;
  int _neutralSamples = 0;
  int? _pressedSince;
  bool _wasActive = false;
  int _lastTime = -1;
  final proof = <String, int>{};

  String get instruction => switch (step) {
    XInputStep.neutral => '保持本窗口前台，松开全部按键和摇杆，等待归零确认。',
    XInputStep.firstPress => '按下右摇杆（RS），持续按住，直到提示松开。',
    XInputStep.firstHold => '继续按住 RS 至少 1 秒；不重复触发。',
    XInputStep.firstRelease => '长按已记录。松开 RS 和全部控件，等待归零。',
    XInputStep.secondPress => '再次按下 RS，然后松开。',
    XInputStep.secondRelease => '松开 RS 和全部控件，等待第二次归零确认。',
    XInputStep.complete => '两次真实状态变化及长按单边沿已记录，正在归档。',
  };

  bool _neutral(XInputState state, {bool allowRs = false}) =>
      (state.buttons & (allowRs ? ~0x80 : 0xffff)) == 0 &&
      state.leftTrigger <= 30 &&
      state.rightTrigger <= 30 &&
      state.thumbLX.abs() <= 7849 &&
      state.thumbLY.abs() <= 7849 &&
      state.thumbRX.abs() <= 8689 &&
      state.thumbRY.abs() <= 8689;

  void _clearNeutral() {
    _neutralSince = null;
    _neutralSamples = 0;
  }

  bool _observeNeutral(XInputState state, int time) {
    if (!_neutral(state)) {
      _clearNeutral();
      return false;
    }
    _neutralSince ??= time;
    _neutralSamples++;
    return _neutralSamples >= 2 && time - _neutralSince! >= 50000;
  }

  String _reset(String reason) {
    step = XInputStep.neutral;
    edges = 0;
    resets++;
    proof.clear();
    sequenceMaxGapMicros = 0;
    _pressedSince = null;
    _clearNeutral();
    return reason;
  }

  String? sample(XInputSnapshot snapshot, {required bool flutterFocused}) {
    requireInput(
      snapshot.monotonicMicros >= _lastTime,
      'XInput monotonic clock moved backwards.',
    );
    final gap = _lastTime < 0 ? 0 : snapshot.monotonicMicros - _lastTime;
    _lastTime = snapshot.monotonicMicros;
    if (gap > maxSampleGapMicros) maxSampleGapMicros = gap;
    if (gap > 250000) {
      _wasActive = false;
      return _reset('sampling-gap-reset');
    }
    final connected = snapshot.slots
        .where((value) => value.resultCode == 0)
        .toList();
    if (connected.length > 1) {
      throw const InputProbeBlocked(
        'Multiple logical controller slots are connected; no controller is guessed.',
      );
    }
    final selected = connected.isEmpty ? null : connected.single;
    final active = snapshot.probeFocused && flutterFocused && selected != null;
    if (!active) {
      final changed = _wasActive || slot != selected?.index;
      slot = selected?.index;
      _wasActive = false;
      if (changed || edges != 0 || step != XInputStep.neutral) {
        return _reset(selected == null ? 'disconnected-reset' : 'focus-reset');
      }
      _clearNeutral();
      return null;
    }
    if (!_wasActive || slot != selected.index) {
      slot = selected.index;
      _wasActive = true;
      _reset('neutral-rearm');
    }
    if (gap > sequenceMaxGapMicros) sequenceMaxGapMicros = gap;
    final state = selected.state!;
    final time = snapshot.monotonicMicros;
    final pressed = (state.buttons & 0x80) != 0;
    switch (step) {
      case XInputStep.neutral:
        if (_observeNeutral(state, time)) {
          proof['initialNeutralSample'] = snapshot.sequence;
          proof['initialNeutralSamples'] = _neutralSamples;
          proof['initialNeutralMicros'] = time - _neutralSince!;
          step = XInputStep.firstPress;
          _clearNeutral();
          return 'initial-neutral-ready';
        }
      case XInputStep.firstPress:
        if (pressed && _neutral(state, allowRs: true)) {
          edges = 1;
          _pressedSince = time;
          proof['firstPressSample'] = snapshot.sequence;
          proof['holdSampleCount'] = 1;
          step = XInputStep.firstHold;
          return 'first-rs-edge';
        }
      case XInputStep.firstHold:
        if (!pressed || !_neutral(state, allowRs: true)) {
          return _reset('hold-interrupted-reset');
        }
        proof['holdSampleCount'] = proof['holdSampleCount']! + 1;
        if (time - _pressedSince! >= 1000000) {
          proof['holdMicros'] = time - _pressedSince!;
          proof['holdCompletedSample'] = snapshot.sequence;
          step = XInputStep.firstRelease;
          return 'first-hold-single-edge';
        }
      case XInputStep.firstRelease:
        if (_observeNeutral(state, time)) {
          proof['firstReleaseSample'] = snapshot.sequence;
          proof['firstReleaseNeutralSamples'] = _neutralSamples;
          proof['firstReleaseNeutralMicros'] = time - _neutralSince!;
          step = XInputStep.secondPress;
          _clearNeutral();
          return 'first-release-neutral-ready';
        }
      case XInputStep.secondPress:
        if (pressed && _neutral(state, allowRs: true)) {
          edges++;
          proof['secondPressSample'] = snapshot.sequence;
          step = XInputStep.secondRelease;
          return 'second-rs-edge';
        }
      case XInputStep.secondRelease:
        if (_observeNeutral(state, time)) {
          proof['secondReleaseSample'] = snapshot.sequence;
          proof['secondReleaseNeutralSamples'] = _neutralSamples;
          proof['secondReleaseNeutralMicros'] = time - _neutralSince!;
          step = XInputStep.complete;
          return 'second-release-neutral-ready';
        }
      case XInputStep.complete:
        break;
    }
    return null;
  }
}
