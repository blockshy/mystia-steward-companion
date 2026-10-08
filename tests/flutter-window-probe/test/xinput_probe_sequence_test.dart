import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/xinput_probe_sequence.dart';

import 'input_probe_fixtures.dart';

void main() {
  test('unchanged and wrapped packet numbers are not clocks; hold produces one edge', () {
    final engine = XInputProbeSequence();
    var sequence = 0;
    String? sample(int time, bool pressed, {int packet = 0}) => engine.sample(
      inputSample(++sequence, time, pressed: pressed, packet: packet),
      flutterFocused: true,
    );
    sample(0, false);
    expect(sample(50000, false), 'initial-neutral-ready');
    expect(sample(60000, true, packet: 0xffffffff), 'first-rs-edge');
    for (final time in [260000, 460000, 660000, 860000]) {
      sample(time, true);
      expect(engine.edges, 1);
    }
    expect(sample(1060000, true), 'first-hold-single-edge');
    sample(1080000, false);
    expect(sample(1130000, false), 'first-release-neutral-ready');
    expect(sample(1150000, true), 'second-rs-edge');
    sample(1170000, false);
    expect(sample(1220000, false), 'second-release-neutral-ready');
    expect(engine.step, XInputStep.complete);
    expect(engine.edges, 2);
    expect(engine.proof['holdMicros'], 1000000);
    expect(engine.proof['holdSampleCount'], 6);
    expect(engine.sequenceMaxGapMicros, 200000);
  });

  for (final reason in ['disconnect', 'native-focus', 'flutter-focus']) {
    test(
      '$reason while held requires new neutral samples before another edge',
      () {
        final engine = XInputProbeSequence();
        engine.sample(inputSample(1, 0), flutterFocused: true);
        engine.sample(inputSample(2, 50000), flutterFocused: true);
        engine.sample(
          inputSample(3, 60000, pressed: true),
          flutterFocused: true,
        );
        engine.sample(
          inputSample(
            4,
            80000,
            pressed: true,
            connected: reason != 'disconnect',
            focused: reason != 'native-focus',
          ),
          flutterFocused: reason != 'flutter-focus',
        );
        expect(engine.edges, 0);
        engine.sample(
          inputSample(5, 100000, pressed: true),
          flutterFocused: true,
        );
        engine.sample(
          inputSample(6, 150000, pressed: true),
          flutterFocused: true,
        );
        expect(engine.edges, 0);
        expect(engine.step, XInputStep.neutral);
        engine.sample(inputSample(7, 170000), flutterFocused: true);
        engine.sample(inputSample(8, 220000), flutterFocused: true);
        expect(
          engine.sample(
            inputSample(9, 240000, pressed: true),
            flutterFocused: true,
          ),
          'first-rs-edge',
        );
        expect(engine.edges, 1);
      },
    );
  }

  test('long observation gap cannot count as continuous hold or neutral', () {
    final engine = XInputProbeSequence();
    engine.sample(inputSample(1, 0), flutterFocused: true);
    engine.sample(inputSample(2, 50000), flutterFocused: true);
    engine.sample(inputSample(3, 60000, pressed: true), flutterFocused: true);
    expect(
      engine.sample(
        inputSample(4, 2060000, pressed: true),
        flutterFocused: true,
      ),
      'sampling-gap-reset',
    );
    expect(engine.step, XInputStep.neutral);
    expect(engine.edges, 0);
    expect(engine.maxSampleGapMicros, 2000000);
    engine.sample(inputSample(5, 2080000), flutterFocused: true);
    expect(
      engine.sample(inputSample(6, 3080000), flutterFocused: true),
      'sampling-gap-reset',
    );
    expect(engine.step, XInputStep.neutral);
    engine.sample(inputSample(7, 3100000), flutterFocused: true);
    expect(
      engine.sample(inputSample(8, 3150000), flutterFocused: true),
      'initial-neutral-ready',
    );
  });

  test('no connected slot never produces neutral-ready or an input edge', () {
    final engine = XInputProbeSequence();
    for (var index = 0; index < 10; index++) {
      expect(
        engine.sample(
          inputSample(index + 1, index * 50000, connected: false),
          flutterFocused: true,
        ),
        isNull,
      );
    }
    expect(engine.step, XInputStep.neutral);
    expect(engine.edges, 0);
    expect(engine.proof, isEmpty);
  });
}
