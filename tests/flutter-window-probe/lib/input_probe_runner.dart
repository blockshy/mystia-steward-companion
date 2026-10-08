import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:ui' show AppExitType;

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';

import 'focus_probe_runner.dart';
import 'generated/input_probe_api.g.dart';
import 'input_probe_contract.dart';
import 'input_probe_view.dart';
import 'xinput_probe_sequence.dart';

typedef XInputSample = Future<XInputSnapshot> Function();

class XInputProbeRunner {
  XInputProbeRunner({
    required this.report,
    required this.ui,
    required this.sample,
    required this.processId,
  });
  final InputProbeReport report;
  final InputProbeUi ui;
  final XInputSample sample;
  final int processId;

  Future<void> run() async {
    final sequence = XInputProbeSequence();
    final watch = Stopwatch()..start();
    var lastSequence = 0;
    String? dll;
    String? signature;
    XInputSnapshot? last;
    while (watch.elapsed < const Duration(seconds: 240)) {
      XInputSnapshot state;
      try {
        state = await sample().timeout(const Duration(seconds: 5));
      } on PlatformException catch (error) {
        if (error.details is XInputSnapshot) {
          report.observe('native-error', {
            ...xinputJson(error.details as XInputSnapshot),
            'errorCode': error.code,
            'errorMessage': error.message,
            'flutterUi': ui.toJson(),
          });
        } else {
          final details = error.details;
          report.observe('native-error', {
            'errorCode': error.code,
            'errorMessage': error.message,
            'details': details is String
                ? details.substring(0, details.length.clamp(0, 16384))
                : null,
            'flutterUi': ui.toJson(),
          });
        }
        if (error.code == 'blocked') {
          throw InputProbeBlocked(error.message ?? 'XInput unavailable.');
        }
        throw InputProbeFailure(
          'XInput sampling failed: ${error.code}: ${error.message}',
        );
      }
      try {
        validateXInputSnapshot(state, report.gitSha, processId);
        requireInput(
          state.sequence > lastSequence,
          'XInput sample sequence did not advance.',
        );
        requireInput(
          dll == null || state.systemDllPath == dll,
          'The bound XInput DLL changed.',
        );
      } on Object catch (error) {
        report.observe('rejected-sample', {
          ...xinputJson(state),
          'validationError': error.toString(),
        });
        rethrow;
      }
      if (last == null) {
        report.context.addAll({
          'systemDllPath': state.systemDllPath,
          'processId': processId,
          'deadlineSeconds': 240,
          'neutralMinimumSamples': 2,
          'neutralMinimumMicros': 50000,
          'holdMinimumMicros': 1000000,
          'maximumSampleGapMicros': 250000,
          'probeNeutralThresholds': {
            'leftStickPerAxis': 7849,
            'rightStickPerAxis': 8689,
            'trigger': 30,
          },
          'routesProductActions': false,
        });
        report.check('xinput-system-api-and-process-identity');
        report.check('xinput-four-slot-state-contract');
      }
      dll = state.systemDllPath;
      lastSequence = state.sequence;
      last = state;
      String? event;
      try {
        event = sequence.sample(state, flutterFocused: ui.focused);
      } on Object catch (error) {
        report.observe('sequence-rejected', {
          ...xinputJson(state),
          'validationError': error.toString(),
          'flutterUi': ui.toJson(),
        });
        rethrow;
      }
      // Keep semantic changes and their raw samples, not every polling frame or
      // analog jitter. Final proof references samples retained on step changes.
      final nextSignature = jsonEncode([
        state.probeFocused,
        ui.focused,
        for (final slot in state.slots)
          [slot.index, slot.resultCode, slot.state?.buttons],
      ]);
      if (event != null || nextSignature != signature) {
        report.observe(event ?? 'slot-or-focus-change', {
          ...xinputJson(state),
          'flutterUi': ui.toJson(),
          'step': sequence.step.name,
          'sequenceEdges': sequence.edges,
          'resets': sequence.resets,
          'maxSampleGapMicros': sequence.maxSampleGapMicros,
        });
        signature = nextSignature;
      }
      if (sequence.step == XInputStep.complete) {
        requireInput(
          sequence.edges == 2,
          'The completed sequence did not contain exactly two RS edges.',
        );
        report.context['completedSequence'] = {
          'slot': sequence.slot,
          'edges': sequence.edges,
          'sequenceMaxGapMicros': sequence.sequenceMaxGapMicros,
          'maxObservedGapMicros': sequence.maxSampleGapMicros,
          ...sequence.proof,
        };
        for (final name in requiredXInputChecks.skip(2)) {
          report.check(name);
        }
        return;
      }
      if (!state.probeFocused || !ui.focused) {
        ui.show('采样继续只读；窗口未聚焦，已停止语义输入。切回本窗口后先松开全部控件。');
      } else if (state.slots.every((slot) => slot.resultCode == 1167)) {
        ui.show('未检测到 XInput 手柄。请连接手柄并保持本窗口前台；总等待上限 240 秒。');
      } else {
        ui.show(sequence.instruction);
      }
      await Future<void>.delayed(const Duration(milliseconds: 20));
    }
    if (last != null) {
      report.observe('deadline', {
        ...xinputJson(last),
        'step': sequence.step.name,
        'flutterUi': ui.toJson(),
      });
    }
    throw InputProbeBlocked(
      last == null || last.slots.every((slot) => slot.resultCode == 1167)
          ? 'No connected XInput controller was observed before the 240-second deadline.'
          : 'The user-operated press/hold/release sequence was not completed before the 240-second deadline.',
    );
  }
}

Future<void> startInputProbe(
  List<String> arguments,
  String compiledGitSha,
) async {
  final ui = InputProbeUi();
  InputProbeReport? report;
  var finishStarted = false;
  try {
    if (!Platform.isWindows) {
      throw UnsupportedError('This runtime input probe requires Windows.');
    }
    final launch = InputProbeLaunch.parse(arguments, compiledGitSha);
    report = InputProbeReport(launch, compiledGitSha);
    final api = InputProbeHostApi();
    final binding = WidgetsFlutterBinding.ensureInitialized();
    runApp(InputProbeApp(ui: ui, suite: launch.suite.name));
    await binding.endOfFrame;
    try {
      if (launch.suite == InputSuite.xinput) {
        await XInputProbeRunner(
          report: report,
          ui: ui,
          sample: api.sampleXInput,
          processId: pid,
        ).run();
      } else {
        await FocusProbeRunner(
          report: report,
          ui: ui,
          execute: api.executeFocus,
          processId: pid,
        ).run();
      }
      report.status = 'PASS';
    } on Object catch (error) {
      report.status = error is InputProbeBlocked ? 'BLOCKED' : 'FAIL';
      report.errors.add(error.toString());
      final remaining = report.required.where(
        (name) => !report!.checks.any((value) => value['name'] == name),
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
    finishStarted = true;
    await api
        .finish(
          encoded,
          report.status == 'PASS' ? 0 : (report.status == 'BLOCKED' ? 2 : 1),
        )
        .timeout(const Duration(seconds: 40));
  } on Object catch (error) {
    // A publication/unknown native result is never retried or promoted to PASS.
    stderr.writeln(
      'Input probe ${finishStarted ? 'publication' : 'startup'} failed: $error',
    );
    ui.show('探针失败：$error');
    await ServicesBinding.instance.exitApplication(AppExitType.required, 1);
  }
}
