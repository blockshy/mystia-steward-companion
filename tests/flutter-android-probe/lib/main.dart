import 'dart:convert';
import 'dart:io';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';
import 'package:mystia_steward_companion_network_probe/local_api_probe.dart';

import 'generated/android_probe_api.g.dart';
import 'probe_contract.dart';

void main() {
  runApp(const ProbeApp());
}

class ProbeApp extends StatefulWidget {
  const ProbeApp({super.key});
  @override
  State<ProbeApp> createState() => _ProbeAppState();
}

class _ProbeAppState extends State<ProbeApp> {
  String status = 'Android Release 探针正在运行';
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) {
      _run();
    });
  }

  Future<void> _run() async {
    final api = AndroidProbeApi();
    final checks = <Map<String, Object?>>[];
    void check(String id, bool passed, [Object? evidence]) {
      checks.add({'id': id, 'passed': passed, 'evidence': evidence});
    }

    try {
      final config = ProbeConfiguration.fromJson(
        await api.launchConfiguration(),
      );
      final facts =
          jsonDecode(await api.runtimeFacts()) as Map<String, Object?>;
      await WidgetsBinding.instance.waitUntilFirstFrameRasterized;
      check('release-runtime', kReleaseMode && facts['debuggable'] == false);
      check('target-36', facts['targetSdk'] == 36);
      check('first-frame', WidgetsBinding.instance.firstFrameRasterized);
      check('release-cleartext-policy', facts['cleartextPermitted'] == true);
      check(
        'actual-page-size',
        facts['pageSize'] == 4096 || facts['pageSize'] == 16384,
        facts['pageSize'],
      );
      check(
        'legacy-local-network-permission',
        hasExpectedLegacyLocalNetworkPermission(facts),
        {
          'effective': facts['accessLocalNetworkEffective'],
          'granted': facts['accessLocalNetworkGranted'],
        },
      );
      if (config.expectDenied) {
        check('nearby-denied', facts['nearbyWifiGranted'] == false);
      }
      const transport = LocalApiProbeTransport();
      for (final endpoint in [
        'http://8.8.8.8:80/probe',
        'http://example.com:80/probe',
      ]) {
        var blocked = false;
        try {
          await transport.request(endpoint: endpoint, token: config.nonce);
        } on ProbeFailure catch (error) {
          blocked = error.code == ProbeFailureCode.invalidEndpoint;
        }
        final kind = endpoint.contains('8.8.8.8') ? 'public' : 'dns';
        check('dart-reject-$kind', blocked);
        final native = jsonDecode(
          await api.nativeHttp(endpoint, config.nonce),
        ) as Map<String, Object?>;
        check('native-reject-$kind', native['outcome'] == 'invalidEndpoint');
      }
      for (final name in ['dart', 'native']) {
        final uri = Uri.parse(config.endpoint)
            .replace(queryParameters: {'transport': name});
        try {
          final Map<String, Object?> body;
          if (name == 'dart') {
            body = await transport.request(
              endpoint: uri.toString(),
              token: config.nonce,
            );
          } else {
            final native = jsonDecode(
              await api.nativeHttp(uri.toString(), config.nonce),
            ) as Map<String, Object?>;
            if (native['outcome'] != 'response') {
              check(
                '$name-lan-http',
                config.expectDenied && native['outcome'] == 'ioFailure',
                native,
              );
              continue;
            }
            if (native['status'] != 200 || native['body'] is! String) {
              check('$name-lan-http', false, native);
              continue;
            }
            body =
                jsonDecode(native['body']! as String) as Map<String, Object?>;
          }
          check(
            '$name-lan-http',
            !config.expectDenied && isExpectedBody(body, config.nonce, name),
          );
        } on ProbeFailure catch (error) {
          check(
            '$name-lan-http',
            config.expectDenied &&
                [
                  ProbeFailureCode.connectFailed,
                  ProbeFailureCode.connectTimeout,
                  ProbeFailureCode.connectionRefused,
                  ProbeFailureCode.readFailed,
                  ProbeFailureCode.responseTimeout,
                ].contains(error.code),
            error.code.name,
          );
        }
      }
      if (!config.expectDenied) {
        final redirect = Uri.parse(config.endpoint).replace(path: '/redirect');
        var rejected = false;
        try {
          await transport.request(
            endpoint: redirect.toString(),
            token: config.nonce,
          );
        } on ProbeFailure catch (error) {
          rejected = error.code == ProbeFailureCode.redirectRejected;
        }
        check('dart-redirect-rejected', rejected);
        final native = jsonDecode(
          await api.nativeHttp(redirect.toString(), config.nonce),
        ) as Map<String, Object?>;
        check(
          'native-redirect-rejected',
          native['outcome'] == 'response' && native['status'] == 302,
        );
      }
      final passed = checks.every((value) => value['passed'] == true);
      final report = {
        'schemaVersion': 1,
        'sourceDigest': const String.fromEnvironment(
          'MYSTIA_ANDROID_PROBE_SOURCE_DIGEST',
        ),
        'runId': config.runId,
        'nonce': config.nonce,
        'passed': passed,
        'buildMode': kReleaseMode ? 'release' : 'not-release',
        'dartVersion': Platform.version,
        'facts': facts,
        'checks': checks,
        'expectDenied': config.expectDenied,
        'evidenceKind': 'device-runtime',
      };
      await api.publishReport(jsonEncode(report));
      if (mounted) {
        setState(() {
          status =
              '${passed ? 'PASS' : 'FAIL'} · ${config.runId}\n${facts['processAbi']} / ${facts['pageSize']} B';
        });
      }
    } on Object catch (error) {
      if (mounted) {
        setState(() {
          status = 'FAIL · ${error.runtimeType}';
        });
      }
    }
  }

  @override
  Widget build(BuildContext context) => MaterialApp(
    home: Scaffold(
      appBar: AppBar(title: const Text('Mystia Android P0')),
      body: SafeArea(
        child: Padding(padding: const EdgeInsets.all(24), child: Text(status)),
      ),
    ),
  );
}
