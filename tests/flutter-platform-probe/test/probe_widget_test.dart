import 'dart:async';
import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_probe/main.dart';

void main() {
  const session = '0123456789abcdef0123456789abcdef';
  final arguments = [
    '--updater-probe-session=$session',
    '--updater-probe-parent-pid=123',
    '--updater-probe-pipe=\\\\.\\pipe\\mystia-steward-companion-p0-123-$session',
  ];
  String reply(int id) => jsonEncode({
    'protocolVersion': 1,
    'session': session,
    'requestId': id,
    'stateSequence': id,
    'state': id == 1 ? 'ready' : 'cancelled',
    'message': '连接已校验',
  });

  testWidgets('cancel waits for a valid response and closes exactly once', (
    tester,
  ) async {
    var closes = 0;
    final calls = <String>[];
    final pending = Completer<String>();
    await tester.pumpWidget(
      ProbeApp(
        arguments: arguments,
        exchange: (command) async {
          calls.add(command);
          return command == 'hello' ? reply(1) : pending.future;
        },
        close: () async {
          closes++;
        },
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('启动链已连通'), findsOneWidget);
    await tester.tap(find.text('结束探针'));
    await tester.pump();
    expect(closes, 0);
    expect(
      tester.widget<FilledButton>(find.byType(FilledButton)).onPressed,
      isNull,
    );
    pending.complete(reply(2));
    await tester.pump();
    expect(calls, ['hello', 'cancel']);
    expect(closes, 1);
  });

  testWidgets(
    'foreign replies stop actions and narrow layouts do not overflow',
    (tester) async {
      tester.view.physicalSize = const Size(390, 640);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.resetPhysicalSize);
      addTearDown(tester.view.resetDevicePixelRatio);
      final calls = <String>[];
      await tester.pumpWidget(
        ProbeApp(
          arguments: arguments,
          exchange: (command) async {
            calls.add(command);
            return reply(2);
          },
          close: () async {},
        ),
      );
      await tester.pumpAndSettle();
      expect(find.text('探针未通过'), findsOneWidget);
      expect(find.text('关闭窗口'), findsOneWidget);
      expect(calls, ['hello']);
      expect(tester.takeException(), isNull);
    },
  );

  testWidgets('disposing during hello does not deliver a stale UI update', (
    tester,
  ) async {
    final pending = Completer<String>();
    await tester.pumpWidget(
      ProbeApp(
        arguments: arguments,
        exchange: (_) => pending.future,
        close: () async {},
      ),
    );
    await tester.pumpWidget(const SizedBox.shrink());
    pending.complete(reply(1));
    await tester.pump();
    expect(tester.takeException(), isNull);
  });

  testWidgets('automatic smoke waits for cancellation acknowledgement', (
    tester,
  ) async {
    final calls = <String>[];
    final pending = Completer<String>();
    var closes = 0;
    await tester.pumpWidget(
      ProbeApp(
        arguments: arguments,
        autoCancel: true,
        exchange: (command) async {
          calls.add(command);
          return command == 'hello' ? reply(1) : pending.future;
        },
        close: () async => closes++,
      ),
    );
    await tester.pump();
    expect(calls, ['hello', 'cancel']);
    expect(closes, 0);
    pending.complete(reply(2));
    await tester.pump();
    expect(closes, 1);
    await tester.pump();
    expect(calls, ['hello', 'cancel']);
  });

  testWidgets('automatic smoke never cancels or exits on an invalid hello', (
    tester,
  ) async {
    final calls = <String>[];
    var closes = 0;
    await tester.pumpWidget(
      ProbeApp(
        arguments: arguments,
        autoCancel: true,
        exchange: (command) async {
          calls.add(command);
          return reply(2);
        },
        close: () async => closes++,
      ),
    );
    await tester.pumpAndSettle();
    expect(calls, ['hello']);
    expect(closes, 0);
    expect(find.text('探针未通过'), findsOneWidget);
  });
}
