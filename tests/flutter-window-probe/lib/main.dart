import 'dart:async';
import 'dart:io';
import 'dart:ui' show AppExitType;

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'generated/window_api.g.dart';
import 'control_probe_runner.dart';
import 'input_probe_runner.dart';
import 'lifecycle_fixture_view.dart';
import 'lifecycle_probe_runner.dart';
import 'probe_contract.dart';
import 'probe_runner.dart';
import 'probe_view.dart';

const compiledGitSha = String.fromEnvironment('MYSTIA_WINDOW_PROBE_GIT_SHA');

void main(List<String> arguments) {
  final binding = WidgetsFlutterBinding.ensureInitialized();
  if (arguments.isNotEmpty &&
      [
        '--control-client',
        '--control-legacy-client',
      ].contains(arguments.first)) {
    unawaited(
      startControlProbe(
        arguments,
        compiledGitSha,
        client: true,
        legacy: arguments.first == '--control-legacy-client',
      ),
    );
    return;
  }
  if (arguments.length == 7 &&
      arguments[3] == '--suite' &&
      arguments[4] == 'hotkey') {
    unawaited(startControlProbe(arguments, compiledGitSha));
    return;
  }
  if (arguments.length == 7 &&
      arguments[3] == '--suite' &&
      (arguments[4] == 'xinput' || arguments[4] == 'focus')) {
    unawaited(startInputProbe(arguments, compiledGitSha));
    return;
  }
  if (arguments.isNotEmpty && arguments.first == '--lifecycle-primary') {
    runApp(
      LifecycleFixtureApp(
        gitSha: compiledGitSha,
        publish: LifecycleFixtureHostApi().publishUi,
        onFailure: (_) {
          unawaited(
            ServicesBinding.instance.exitApplication(AppExitType.required, 1),
          );
        },
      ),
    );
    return;
  }
  final ui = ProbeUiController();
  WindowProbeRunner? runner;
  runApp(
    WindowProbeApp(
      controller: ui,
      restore: () async {
        final current = runner;
        if (current == null) {
          throw StateError('Native probe initialization has not completed.');
        }
        await current.restore();
      },
    ),
  );
  binding.addPostFrameCallback((_) {
    unawaited(() async {
      try {
        if (!Platform.isWindows) {
          throw UnsupportedError('This runtime probe requires Windows.');
        }
        final launch = ProbeLaunch.parse(arguments, compiledGitSha);
        final report = ProbeReport(launch, compiledGitSha);
        late final WindowProbeRunner current;
        current = WindowProbeRunner(
          report: report,
          ui: ui,
          execute: WindowProbeHostApi().execute,
          devicePixelRatio: () =>
              binding.platformDispatcher.views.single.devicePixelRatio,
          waitForDartFrame: () => binding.endOfFrame,
          processId: pid,
          runLifecycle: () async {
            await current.retireCore();
            await LifecycleProbeRunner(
              report: report,
              execute: LifecycleHostApi().execute,
              controllerProcessId: pid,
            ).run();
          },
        );
        runner = current;
        await current.run();
      } on Object catch (error) {
        // Rejected launch/publication is always nonzero. Never print arbitrary
        // arguments or turn a missing report into an assumed pass.
        ui.setDetail('探针失败：$error');
        stderr.writeln('Window probe failed: $error');
        await ServicesBinding.instance.exitApplication(AppExitType.required, 1);
      }
    }());
  });
}
