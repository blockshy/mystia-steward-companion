import 'dart:convert';

import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_android_probe/probe_contract.dart';

void main() {
  test(
    'legacy permission distinguishes OS split grant from APK declaration',
    () {
      const android17 = {
        'sdk': 37,
        'targetSdk': 36,
        'accessLocalNetworkEffective': true,
        'accessLocalNetworkGranted': true,
      };
      expect(hasExpectedLegacyLocalNetworkPermission(android17), isTrue);
      expect(
        hasExpectedLegacyLocalNetworkPermission({
          ...android17,
          'sdk': 36,
          'accessLocalNetworkEffective': false,
          'accessLocalNetworkGranted': false,
        }),
        isTrue,
      );
      for (final invalid in [
        {...android17, 'sdk': '37'},
        {...android17, 'sdk': 23},
        {...android17, 'targetSdk': 37},
        {...android17, 'accessLocalNetworkEffective': false},
        {...android17, 'accessLocalNetworkGranted': false},
        {...android17, 'sdk': 36},
      ]) {
        expect(hasExpectedLegacyLocalNetworkPermission(invalid), isFalse);
      }
    },
  );
  const valid = {
    'runId': 'android-probe-001',
    'nonce': '00112233445566778899aabbccddeeff',
    'endpoint': 'http://192.168.2.88:32866/probe',
    'expectDenied': false,
  };
  test('strict launch identity and local fixture endpoint', () {
    expect(
      ProbeConfiguration.fromJson(jsonEncode(valid)).runId,
      valid['runId'],
    );
    for (final value in [
      {...valid, 'extra': true},
      {...valid, 'runId': '../escape'},
      {...valid, 'nonce': 'bad'},
      {...valid, 'expectDenied': 'false'},
      {...valid, 'endpoint': 'http://8.8.8.8:80/probe'},
      {...valid, 'endpoint': 'http://example.com:80/probe'},
      {...valid, 'endpoint': 'http://192.168.2.88:32866/admin'},
      {...valid, 'endpoint': 'http://192.168.2.88:32866/probe?x=1'},
    ]) {
      expect(
        () => ProbeConfiguration.fromJson(jsonEncode(value)),
        throwsA(anything),
      );
    }
  });
  test('response binds nonce and independent transport', () {
    expect(
      isExpectedBody(
        {'success': true, 'nonce': 'n', 'transport': 'dart'},
        'n',
        'dart',
      ),
      isTrue,
    );
    expect(
      isExpectedBody(
        {'success': true, 'nonce': 'old', 'transport': 'dart'},
        'n',
        'dart',
      ),
      isFalse,
    );
    expect(
      isExpectedBody(
        {'success': true, 'nonce': 'n', 'transport': 'native'},
        'n',
        'dart',
      ),
      isFalse,
    );
    expect(
      isExpectedBody({'nonce': 'n', 'transport': 'dart'}, 'n', 'dart'),
      isFalse,
    );
  });
}
