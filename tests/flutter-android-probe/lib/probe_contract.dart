import 'dart:convert';

import 'package:mystia_steward_companion_network_probe/local_api_probe.dart';

final class ProbeConfiguration {
  ProbeConfiguration.fromJson(String text) {
    final value = jsonDecode(text);
    if (value is! Map<String, Object?> ||
        value.length != 4 ||
        value['runId'] is! String ||
        value['nonce'] is! String ||
        value['endpoint'] is! String ||
        value['expectDenied'] is! bool) {
      throw const FormatException('Invalid launch configuration');
    }
    runId = value['runId']! as String;
    nonce = value['nonce']! as String;
    endpoint = value['endpoint']! as String;
    expectDenied = value['expectDenied']! as bool;
    if (!RegExp(r'^[a-z0-9][a-z0-9-]{7,63}$').hasMatch(runId) ||
        !RegExp(r'^[a-f0-9]{32}$').hasMatch(nonce)) {
      throw const FormatException('Invalid probe identity');
    }
    final uri = parseLocalApiProbeUri(endpoint);
    if (uri.path != '/probe' || uri.hasQuery) {
      throw const FormatException('Only the fixed probe route is supported');
    }
  }
  late final String runId;
  late final String nonce;
  late final String endpoint;
  late final bool expectDenied;
}

bool isExpectedBody(
  Map<String, Object?> body,
  String nonce,
  String transport,
) =>
    body['success'] == true &&
    body['nonce'] == nonce &&
    body['transport'] == transport;

// APK declarations are audited separately with aapt2. Android 17 adds and
// implicitly grants ACCESS_LOCAL_NETWORK to legacy targets with INTERNET.
bool hasExpectedLegacyLocalNetworkPermission(Map<String, Object?> facts) {
  final sdk = facts['sdk'];
  if (sdk is! int || sdk < 24 || facts['targetSdk'] != 36) return false;
  final implicit = sdk >= 37;
  return facts['accessLocalNetworkEffective'] == implicit &&
      facts['accessLocalNetworkGranted'] == implicit;
}
