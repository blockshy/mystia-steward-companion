import 'dart:async';
import 'dart:ui' as ui;

import 'package:flutter/material.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/probe_contract.dart';
import 'package:mystia_steward_companion_window_probe/probe_view.dart';

void main() {
  testWidgets(
    'rendered foreground/background alpha remain separate for all nine specimens',
    (tester) async {
      final key = GlobalKey();
      for (final background in probeAlphaValues) {
        for (final content in probeAlphaValues) {
          final spec = PaintSpec(1, background, content);
          await tester.pumpWidget(
            Directionality(
              textDirection: TextDirection.ltr,
              child: Center(
                child: RepaintBoundary(
                  key: key,
                  child: AlphaSpecimen(spec: spec),
                ),
              ),
            ),
          );
          await tester.pump();
          final boundary =
              key.currentContext!.findRenderObject()! as RenderRepaintBoundary;
          final image = (await tester.runAsync(
            () => boundary.toImage(pixelRatio: 1),
          ))!;
          final bytes = (await tester.runAsync(
            () => image.toByteData(format: ui.ImageByteFormat.rawStraightRgba),
          ))!;
          int alpha(Offset point) => bytes.getUint8(
            (point.dy.toInt() * image.width + point.dx.toInt()) * 4 + 3,
          );
          expect(alpha(backgroundSample), background);
          final expectedAlpha = (content + background * (1 - content / 255))
              .round();
          expect(
            (alpha(contentSample) - expectedAlpha).abs(),
            lessThanOrEqualTo(1),
          );
          if (content == 255) {
            final offset =
                (contentSample.dy.toInt() * image.width +
                    contentSample.dx.toInt()) *
                4;
            expect(
              [
                bytes.getUint8(offset),
                bytes.getUint8(offset + 1),
                bytes.getUint8(offset + 2),
              ],
              [224, 64, 32],
            );
          }
          image.dispose();
        }
      }
    },
  );

  testWidgets(
    'restore waits for native completion, prevents duplicate requests and exposes failure',
    (tester) async {
      final controller = ProbeUiController();
      addTearDown(controller.dispose);
      final pending = Completer<void>();
      var calls = 0;
      await tester.pumpWidget(
        WindowProbeApp(
          controller: controller,
          restore: () {
            calls++;
            return pending.future;
          },
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(find.text('恢复可操作窗口'));
      await tester.pump();
      expect(calls, 1);
      expect(
        tester.widget<FilledButton>(find.byType(FilledButton)).onPressed,
        isNull,
      );
      pending.completeError(StateError('native restore refused'));
      await tester.pump();
      expect(find.byKey(const ValueKey('restore-error')), findsOneWidget);
      expect(find.text('恢复可操作窗口'), findsOneWidget);
      expect(calls, 1);
    },
  );

  testWidgets(
    'Flutter target receives real widget pointer and key events with framework focus',
    (tester) async {
      final controller = ProbeUiController();
      addTearDown(controller.dispose);
      await tester.pumpWidget(
        WindowProbeApp(controller: controller, restore: () async {}),
      );
      await tester.pumpAndSettle();
      await tester.tapAt(contentSample);
      await tester.sendKeyEvent(LogicalKeyboardKey.keyA, platform: 'windows');
      await tester.pump();
      expect(controller.keyCount, 0);
      await tester.sendKeyEvent(LogicalKeyboardKey.f24, platform: 'windows');
      await tester.pump();
      expect(controller.pointerCount, 1);
      expect(controller.keyCount, 1);
      expect(controller.focused, true);
      controller.setPaint(const PaintSpec(2, 128, 255));
      expect(
        () => controller.setPaint(const PaintSpec(1, 0, 0)),
        throwsStateError,
      );
    },
  );
}
