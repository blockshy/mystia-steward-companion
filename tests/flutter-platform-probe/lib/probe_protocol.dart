import 'dart:convert';

class ProbeLaunch {
  ProbeLaunch._(this.session, this.installFixture);

  final String session;
  final bool installFixture;

  factory ProbeLaunch.parse(List<String> arguments) {
    const names = {'pipe', 'session', 'parent-pid', 'mode'};
    final values = <String, String>{};
    for (final argument in arguments) {
      final match = RegExp(r'^--updater-probe-([^=]+)=(.+)$')
          .firstMatch(argument);
      if (match == null ||
          !names.contains(match[1]) ||
          values.containsKey(match[1])) {
        throw const FormatException('启动参数不完整或存在歧义。');
      }
      values[match[1]!] = match[2]!;
    }
    final session = values['session'] ?? '';
    final parent = values['parent-pid'] ?? '';
    final fixture = values['mode'] == 'install-fixture';
    if ((values.containsKey('mode') && !fixture) ||
        values.length != (fixture ? 4 : 3) ||
        !RegExp(r'^[a-f0-9]{32}$').hasMatch(session) ||
        !RegExp(r'^[1-9][0-9]*$').hasMatch(parent) ||
        (int.tryParse(parent) ?? 0) > 0xffffffff ||
        values['pipe'] !=
            '\\\\.\\pipe\\mystia-steward-companion-p0-$parent-$session') {
      throw const FormatException('请通过探针启动脚本运行。');
    }
    return ProbeLaunch._(session, fixture);
  }
}

class ProbeReply {
  ProbeReply._(this.message);
  final String message;

  factory ProbeReply.parse(
    String frame, {
    required String session,
    required int requestId,
  }) {
    if (utf8.encode(frame).length > 16384) {
      throw const FormatException('响应超过协议上限。');
    }
    final decoded = jsonDecode(frame);
    const keys = {
      'protocolVersion',
      'session',
      'requestId',
      'stateSequence',
      'state',
      'message',
    };
    if (decoded is! Map<String, dynamic> ||
        decoded.length != keys.length ||
        !decoded.keys.every(keys.contains) ||
        decoded['protocolVersion'] is! int ||
        decoded['protocolVersion'] != 1 ||
        decoded['session'] != session ||
        decoded['requestId'] is! int ||
        decoded['requestId'] != requestId ||
        decoded['stateSequence'] is! int ||
        decoded['stateSequence'] != requestId ||
        decoded['state'] != (requestId == 1 ? 'ready' : 'cancelled') ||
        decoded['message'] is! String ||
        (decoded['message'] as String).isEmpty ||
        !{1, 2}.contains(requestId)) {
      throw const FormatException('响应与当前探针会话不匹配。');
    }
    return ProbeReply._(decoded['message'] as String);
  }
}
