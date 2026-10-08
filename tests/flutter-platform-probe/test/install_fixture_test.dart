import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_probe/install_fixture.dart';
import 'package:mystia_steward_companion_probe/probe_protocol.dart';

const nonce = '0123456789abcdef0123456789abcdef';
const launch = [
  '--updater-probe-session=$nonce',
  '--updater-probe-parent-pid=123',
  '--updater-probe-pipe=\\\\.\\pipe\\mystia-steward-companion-p0-123-$nonce',
  '--updater-probe-mode=install-fixture',
];
Map<String, Object> reply(int id, String state) => {
  'protocolVersion': 2,
  'session': nonce,
  'requestId': id,
  'stateSequence': id,
  'state': state,
  'message': 'fixture state: $state',
  'progress': state == 'succeeded' ? 100 : 0,
  'terminal': {'succeeded', 'failed', 'cancelled'}.contains(state),
  'canCancel': {'ready', 'waiting-game'}.contains(state),
};

void main() {
  test('explicit fixture launch cannot silently change readonly mode', () {
    expect(ProbeLaunch.parse(launch).installFixture, isTrue);
    expect(ProbeLaunch.parse(launch.take(3).toList()).installFixture, isFalse);
    expect(
      () => ProbeLaunch.parse([
        ...launch.take(3),
        '--updater-probe-mode=install',
      ]),
      throwsFormatException,
    );
  });
  test(
    'fixture reply rejects foreign, stale, malformed and false terminals',
    () {
      expect(
        InstallReply.parse(jsonEncode(reply(1, 'ready')), nonce, 1, 0).terminal,
        isFalse,
      );
      for (final mutation in <Map<String, Object>>[
        {'protocolVersion': 1},
        {'session': 'f' * 32},
        {'requestId': 0},
        {'stateSequence': 0},
        {'stateSequence': 1.0},
        {'extra': true},
        {
          'state': 'succeeded',
          'progress': 90,
          'terminal': true,
          'canCancel': false,
        },
        {'terminal': true},
        {'message': ''},
        {'progress': 101},
      ]) {
        expect(
          () => InstallReply.parse(
            jsonEncode({...reply(2, 'waiting-game'), ...mutation}),
            nonce,
            2,
            1,
          ),
          throwsFormatException,
        );
      }
    },
  );
  testWidgets(
    'install requires user start, waits for core terminal then finish',
    (tester) async {
      final commands = <String>[];
      var closed = false;
      await tester.pumpWidget(
        InstallFixtureApp(
          arguments: launch,
          exchange: (command) async {
            commands.add(command);
            final state = switch (command) {
              'hello' => 'ready',
              'start' => 'waiting-game',
              _ => 'succeeded',
            };
            return jsonEncode(reply(commands.length, state));
          },
          close: () async {
            closed = true;
          },
        ),
      );
      await tester.pumpAndSettle();
      expect(commands, ['hello']);
      await tester.tap(find.text('开始隔离安装'));
      await tester.pump();
      expect(commands, ['hello', 'start']);
      expect(closed, isFalse);
      await tester.pump(const Duration(milliseconds: 100));
      await tester.pumpAndSettle();
      expect(commands, ['hello', 'start', 'status']);
      expect(closed, isFalse);
      await tester.tap(find.text('完成并退出'));
      await tester.pumpAndSettle();
      expect(commands.last, 'finish');
      expect(closed, isTrue);
    },
  );
  testWidgets('wrong reply blocks installation and exposes a close action', (
    tester,
  ) async {
    var closed = false;
    await tester.pumpWidget(
      InstallFixtureApp(
        arguments: launch,
        exchange: (_) async =>
            jsonEncode({...reply(1, 'ready'), 'session': 'f' * 32}),
        close: () async {
          closed = true;
        },
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('开始隔离安装'), findsNothing);
    await tester.tap(find.widgetWithText(FilledButton, '关闭窗口'));
    await tester.pumpAndSettle();
    expect(closed, isTrue);
  });
}
