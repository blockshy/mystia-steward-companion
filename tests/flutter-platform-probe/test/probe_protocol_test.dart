import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_probe/probe_protocol.dart';

void main() {
  const nonce = '0123456789abcdef0123456789abcdef';
  final arguments = [
    '--updater-probe-session=$nonce',
    '--updater-probe-parent-pid=123',
    '--updater-probe-pipe=\\\\.\\pipe\\mystia-steward-companion-p0-123-$nonce',
  ];
  final reply = <String, Object>{
    'protocolVersion': 1,
    'session': nonce,
    'requestId': 1,
    'stateSequence': 1,
    'state': 'ready',
    'message': '启动链已连通',
  };

  test('accept exact bootstrap invocation and ready/cancel replies', () {
    expect(ProbeLaunch.parse(arguments).session, nonce);
    expect(
      ProbeReply.parse(jsonEncode(reply), session: nonce, requestId: 1).message,
      '启动链已连通',
    );
    final cancelled = {
      ...reply,
      'requestId': 2,
      'stateSequence': 2,
      'state': 'cancelled',
    };
    expect(
      ProbeReply.parse(
        jsonEncode(cancelled),
        session: nonce,
        requestId: 2,
      ).message,
      isNotEmpty,
    );
  });

  test('reject missing, duplicated and mismatched launch identities', () {
    for (final invalid in [
      <String>[],
      arguments.take(2).toList(),
      [...arguments, arguments.first],
      [...arguments.take(2), '--updater-probe-pipe=\\\\.\\pipe\\other'],
      [...arguments, '--install=true'],
    ]) {
      expect(() => ProbeLaunch.parse(invalid), throwsFormatException);
    }
  });

  test('reject stale, foreign, future-schema and installed responses', () {
    for (final mutation in <Map<String, Object>>[
      {'protocolVersion': 2},
      {'requestId': 2},
      {'stateSequence': 0},
      {'session': 'f' * 32},
      {'state': 'installed'},
      {'extra': true},
      {'message': ''},
      {'protocolVersion': 1.0},
    ]) {
      expect(
        () => ProbeReply.parse(
          jsonEncode({...reply, ...mutation}),
          session: nonce,
          requestId: 1,
        ),
        throwsFormatException,
      );
    }
    expect(
      () => ProbeReply.parse('x' * 16385, session: nonce, requestId: 1),
      throwsFormatException,
    );
    expect(
      () => ProbeReply.parse('[]', session: nonce, requestId: 1),
      throwsFormatException,
    );
  });
}
