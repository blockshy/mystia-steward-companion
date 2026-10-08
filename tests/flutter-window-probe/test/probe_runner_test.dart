import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/generated/window_api.g.dart';
import 'package:mystia_steward_companion_window_probe/probe_contract.dart';
import 'package:mystia_steward_companion_window_probe/probe_runner.dart';
import 'package:mystia_steward_companion_window_probe/probe_view.dart';

const sha = '283bd56cd10564d64169a8ea521f9fdffe0019b4';
ProbeSnapshot fixture(ProbeCommand command) => ProbeSnapshot(
  revision: command.revision,
  frameRevision: 0,
  processId: 100,
  topHwnd: 10,
  childHwnd: 11,
  targetProcessId: 200,
  targetHwnd: 20,
  visible: true,
  inputMode: ProbeInputMode.interactive,
  topmost: true,
  foregroundHwnd: 10,
  foregroundProcessId: 100,
  focusHwnd: 11,
  flutterMouseDown: 0,
  flutterMouseUp: 0,
  targetMouseDown: 0,
  targetMouseUp: 0,
  flutterKeyDown: 0,
  targetKeyDown: 0,
  hotkeyCount: 0,
  dpi: 96,
  devicePixelRatio: 1,
  topAboveTarget: true,
  sampleHitHwnd: 11,
  sampleHitProcessId: 100,
  pixels: [],
  diagnosticsJson: jsonEncode({'schemaVersion': 1, 'nativeGitSha': sha}),
);

void main() {
  final binding = TestWidgetsFlutterBinding.ensureInitialized();
  for (final failure in [
    'identity',
    'build-identity',
    'revision',
    'diagnostic-json',
    'unknown-command-result',
  ]) {
    test(
      'native $failure ends the suite without further window/input operations and archives failure',
      () async {
        final report = ProbeReport.coreTest(
          const ProbeLaunch(
            runId: 'test',
            resultFile: 'test-only-never-written',
          ),
          sha,
        );
        final ui = ProbeUiController();
        addTearDown(ui.dispose);
        final operations = <ProbeOperation>[];
        Map<String, dynamic>? finalReport;
        final runner = WindowProbeRunner(
          report: report,
          ui: ui,
          processId: 100,
          devicePixelRatio: () => 1,
          waitForDartFrame: () async {},
          execute: (command) async {
            operations.add(command.operation);
            if (command.operation == ProbeOperation.finish) {
              expect(command.value, 1);
              finalReport = jsonDecode(command.text) as Map<String, dynamic>;
              return fixture(command);
            }
            if (failure == 'unknown-command-result' &&
                command.operation == ProbeOperation.showInteractive) {
              throw TimeoutException('unknown native result');
            }
            final snapshot = fixture(command);
            if (failure == 'identity') snapshot.targetProcessId = 100;
            if (failure == 'build-identity') {
              snapshot.diagnosticsJson = jsonEncode({
                'schemaVersion': 1,
                'nativeGitSha': '0' * 40,
              });
            }
            if (failure == 'revision') snapshot.revision++;
            if (failure == 'diagnostic-json') {
              snapshot.diagnosticsJson = '{invalid-json';
            }
            return snapshot;
          },
        );
        expect(await runner.run(), 1);
        final expected = [
          ProbeOperation.initialize,
          if (failure == 'unknown-command-result')
            ProbeOperation.showInteractive,
          ProbeOperation.finish,
        ];
        expect(operations, expected);
        expect(finalReport!['status'], 'FAIL');
        expect(finalReport!['p0Verified'], false);
        if (failure != 'unknown-command-result') {
          expect(
            report.observations.single['label'],
            'rejected-native-observation',
          );
          expect(report.observations.single['validationError'], isNotEmpty);
        }
        await expectLater(runner.run(), throwsStateError);
        expect(operations, expected);
      },
    );
  }

  test('retirement failure retains typed terminal evidence and stops the continuation', () async {
    final report = ProbeReport(
      const ProbeLaunch(runId: 'test', resultFile: 'test-only-never-written'),
      sha,
    );
    final ui = ProbeUiController()
      ..pointerDown()
      ..focusChanged(true);
    addTearDown(ui.dispose);
    final operations = <ProbeOperation>[];
    const channel = BasicMessageChannel<Object?>(
      'dev.flutter.pigeon.mystia_steward_companion_window_probe.WindowProbeHostApi.execute',
      WindowProbeHostApi.pigeonChannelCodec,
    );
    binding.defaultBinaryMessenger.setMockDecodedMessageHandler<Object?>(
      channel,
      (request) async {
        final command = (request! as List<Object?>).single! as ProbeCommand;
        operations.add(command.operation);
        final details = fixture(command)
          ..dpi = 0
          ..diagnosticsJson = jsonEncode({
            'schemaVersion': 1,
            'nativeGitSha': sha,
            'terminalOnly': true,
            'coreRetired': false,
            'coreTargetExitCode': 0,
          });
        return [
          'native-observation',
          'Cannot query exited target image',
          details,
        ];
      },
    );
    addTearDown(
      () => binding.defaultBinaryMessenger
          .setMockDecodedMessageHandler<Object?>(channel, null),
    );
    final runner = WindowProbeRunner(
      report: report,
      ui: ui,
      processId: 100,
      devicePixelRatio: () => 1,
      waitForDartFrame: () async {},
      execute: WindowProbeHostApi().execute,
    );
    var lifecycleStarted = false;
    await expectLater(() async {
      await runner.retireCore();
      lifecycleStarted = true;
    }(), throwsA(isA<FatalProbe>()));
    expect(lifecycleStarted, false);
    expect(operations, [ProbeOperation.retireCore]);
    final archived = jsonDecode(report.encode()) as Map<String, dynamic>;
    expect(archived['status'], 'FAIL');
    final observation = (archived['observations'] as List<dynamic>).single;
    expect(observation['label'], 'native-error-observation');
    expect(observation['operation'], 'retireCore');
    expect(observation['errorCode'], 'native-observation');
    expect(observation['errorMessage'], 'Cannot query exited target image');
    expect(observation['snapshotValidated'], false);
    expect(observation['diagnostics']['coreTargetExitCode'], 0);
    expect(observation['flutterUi']['pointerDown'], 1);
    expect(observation['flutterUi']['focused'], true);
  });

  for (final detailsKind in [
    'typed',
    'rejected-typed',
    'fallback',
    'wrong-run-fallback',
    'malformed-fallback',
    'oversized-fallback',
    'missing',
  ]) {
    test(
      'Pigeon click timeout retains $detailsKind and current Flutter events, then only finishes',
      () async {
        final report = ProbeReport.coreTest(
          const ProbeLaunch(
            runId: 'test',
            resultFile: 'test-only-never-written',
          ),
          sha,
        );
        final ui = ProbeUiController();
        addTearDown(ui.dispose);
        final operations = <ProbeOperation>[];
        Map<String, dynamic>? archived;
        var underlay = 0;
        const channel = BasicMessageChannel<Object?>(
          'dev.flutter.pigeon.mystia_steward_companion_window_probe.WindowProbeHostApi.execute',
          WindowProbeHostApi.pigeonChannelCodec,
        );
        binding.defaultBinaryMessenger.setMockDecodedMessageHandler<Object?>(
          channel,
          (request) async {
            final command = (request! as List<Object?>).single! as ProbeCommand;
            operations.add(command.operation);
            final snapshot = fixture(command)..frameRevision = command.revision;
            if (command.operation == ProbeOperation.finish) {
              expect(command.value, 1);
              archived = jsonDecode(command.text) as Map<String, dynamic>;
            } else if (command.operation == ProbeOperation.setUnderlayColor) {
              underlay = command.value == 1 ? 255 : 0;
            } else if (command.operation == ProbeOperation.capture) {
              snapshot.pixels = [
                ...composedRgb(ui.paint, underlay, content: false),
                ...composedRgb(ui.paint, underlay, content: true),
                underlay,
                underlay,
                underlay,
              ];
            } else if (command.operation == ProbeOperation.clickSample) {
              // Simulate Flutter seeing input while the native tagged counter
              // remains zero. The actual Pigeon codec carries the error reply.
              ui.pointerDown();
              ui.keyDown();
              ui.focusChanged(true);
              final fallback = jsonEncode({
                'schemaVersion': 1,
                'kind': 'native-error-fallback',
                'nativeGitSha': sha,
                'runId': detailsKind == 'wrong-run-fallback' ? 'other' : 'test',
                'snapshotError': 'controlled observation unavailable',
                'inputDiagnostics': {'lastSendInserted': 3},
              });
              final Object? details = switch (detailsKind) {
                'typed' => snapshot,
                'rejected-typed' => snapshot..processId = 101,
                'fallback' || 'wrong-run-fallback' => fallback,
                'malformed-fallback' => '{invalid-json',
                'oversized-fallback' => 'x' * 65537,
                _ => null,
              };
              return [
                'native-observation',
                'Bounded observation timeout',
                details,
              ];
            }
            return [snapshot];
          },
        );
        addTearDown(
          () => binding.defaultBinaryMessenger
              .setMockDecodedMessageHandler<Object?>(channel, null),
        );
        final runner = WindowProbeRunner(
          report: report,
          ui: ui,
          processId: 100,
          devicePixelRatio: () => 1,
          waitForDartFrame: () async {},
          execute: WindowProbeHostApi().execute,
        );
        expect(await runner.run(), 1);
        expect(operations.sublist(operations.length - 2), [
          ProbeOperation.clickSample,
          ProbeOperation.finish,
        ]);
        expect(
          operations.where(
            (operation) => operation == ProbeOperation.clickSample,
          ),
          hasLength(1),
        );
        expect(archived!['status'], 'FAIL');
        expect(archived!['p0Verified'], false);
        expect(archived!['checks'], hasLength(20));
        final observations = archived!['observations'] as List<dynamic>;
        final error = observations.last as Map<String, dynamic>;
        expect(error['operation'], 'clickSample');
        expect(error['errorCode'], 'native-observation');
        expect(error['flutterUi'], {
          'pointerDown': 1,
          'keyDown': 1,
          'focused': true,
          'paintRevision': 20,
        });
        if (detailsKind.endsWith('typed')) {
          expect(error['label'], 'native-error-observation');
          expect(error['flutterMouseDown'], 0);
          expect(error['snapshotValidated'], detailsKind == 'typed');
        } else {
          expect(error['label'], 'native-error-fallback');
          expect(error['fallbackValidated'], detailsKind == 'fallback');
          if (detailsKind == 'fallback') {
            expect(error['nativeErrorDetails']['inputDiagnostics'], {
              'lastSendInserted': 3,
            });
          } else if (detailsKind == 'oversized-fallback') {
            expect(error['nativeErrorDetails']['truncated'], true);
            expect(
              error['nativeErrorDetails']['unparsedPrefix'],
              hasLength(4096),
            );
          }
        }
        if (detailsKind != 'typed' && detailsKind != 'fallback') {
          expect(error['validationError'], isNotEmpty);
        }
      },
    );
  }
}
