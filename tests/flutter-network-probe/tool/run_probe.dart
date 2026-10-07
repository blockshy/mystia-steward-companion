import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../lib/local_api_probe.dart';

const fixtureToken = 'p0-fake-token-not-a-user-credential';

void require(bool condition, String message) {
  if (!condition) throw StateError(message);
}

Future<ProbeFailure> expectFailure(
  Future<Object?> Function() action,
  ProbeFailureCode code,
  WriteDisposition disposition, {
  Set<ProbeFailureCode> alsoAccept = const {},
}) async {
  final acceptedCodes = <ProbeFailureCode>{code, ...alsoAccept};
  try {
    await action();
  } on ProbeFailure catch (failure) {
    require(
      acceptedCodes.contains(failure.code),
      'Expected ${acceptedCodes.map((value) => value.name).join(' or ')}, '
      'got $failure',
    );
    require(
      failure.disposition == disposition,
      'Expected ${disposition.name}, got $failure',
    );
    require(
      !failure.toString().contains(fixtureToken),
      'Failure exposed Token',
    );
    return failure;
  }
  throw StateError(
    'Expected ${acceptedCodes.map((value) => value.name).join(' or ')}, '
    'request unexpectedly succeeded',
  );
}

final class Fixture {
  Fixture(this.server) {
    subscription = server.listen((request) {
      final work = handle(request);
      pending.add(work);
      unawaited(work.whenComplete(() => pending.remove(work)));
    });
  }

  final HttpServer server;
  late final StreamSubscription<HttpRequest> subscription;
  final pending = <Future<void>>[];
  final counts = <String, int>{};
  final releaseSlow = Completer<void>();

  String url(String path) => 'http://127.0.0.1:${server.port}$path';

  Future<void> handle(HttpRequest request) async {
    final path = request.uri.path;
    counts.update(path, (count) => count + 1, ifAbsent: () => 1);
    try {
      final bytes = await request.fold<List<int>>(
        <int>[],
        (buffer, chunk) => buffer..addAll(chunk),
      );
      request.response.persistentConnection = false;
      request.response.headers.contentType = ContentType.json;
      if (path == '/slow') {
        await releaseSlow.future;
        request.response.write('{"ok":true}');
      } else if (path == '/redirect') {
        request.response
          ..statusCode = HttpStatus.temporaryRedirect
          ..headers.set(HttpHeaders.locationHeader, url('/redirect-target'));
      } else if (path.startsWith('/status/')) {
        request.response
          ..statusCode = int.parse(path.split('/').last)
          ..write(jsonEncode({'ok': false, 'error': fixtureToken}));
      } else if (path == '/invalid-utf8') {
        request.response.add(<int>[0xc3, 0x28]);
      } else if (path == '/invalid-json') {
        request.response.write('{"ok":');
      } else if (path == '/array') {
        request.response.write('[]');
      } else if (path == '/snapshot') {
        request.response.write('{"snapshotSignature":"fixture"}');
      } else if (path == '/business-failure') {
        request.response.write(
          jsonEncode({'ok': false, 'error': fixtureToken}),
        );
      } else if (path == '/large') {
        request.response.write(jsonEncode({'ok': true, 'value': 'x' * 4096}));
      } else {
        request.response.write(
          jsonEncode({
            'ok': true,
            'method': request.method,
            'contentLength': request.contentLength,
            'receivedBytes': bytes.length,
            'transferEncoding': request.headers.value('transfer-encoding'),
            'connection': request.headers.value('connection'),
            'cacheControl': request.headers.value('cache-control'),
            'contentType': request.headers.contentType?.toString(),
            'correctFixtureToken':
                request.headers.value(LocalApiProbeTransport.tokenHeader) ==
                fixtureToken,
            'clientId': request.headers.value(
              LocalApiProbeTransport.clientIdHeader,
            ),
            'clientLabel': request.headers.value(
              LocalApiProbeTransport.clientLabelHeader,
            ),
            'authority': request.headers.value(
              LocalApiProbeTransport.authorityHeader,
            ),
            'body': bytes.isEmpty ? null : jsonDecode(utf8.decode(bytes)),
          }),
        );
      }
      await request.response.close();
    } on Object {
      // Timeout/size cases deliberately close the client before the fixture.
      // Assertions belong in the runner, never in this cleanup branch.
      try {
        await request.response.close();
      } on Object {
        // The peer may already have reset the socket.
      }
    }
  }

  Future<void> close() async {
    if (!releaseSlow.isCompleted) releaseSlow.complete();
    await server.close(force: true);
    await subscription.cancel();
    await Future.wait(pending.toList());
  }
}

Future<void> main(List<String> arguments) async {
  if (arguments.length == 2 && arguments.first == '--proxy-child') {
    try {
      final result = await const LocalApiProbeTransport().request(
        endpoint: arguments.last,
        token: fixtureToken,
      );
      require(
        result['correctFixtureToken'] == true,
        'Origin missed fixture auth',
      );
      stdout.writeln('DIRECT_ORIGIN_CONFIRMED');
    } on Object catch (error) {
      stderr.writeln(error.runtimeType);
      exitCode = 1;
    }
    return;
  }
  if (arguments.isNotEmpty) {
    stderr.writeln('Usage: dart tool/run_probe.dart');
    exitCode = 64;
    return;
  }

  final results = <Map<String, Object?>>[];
  Future<void> check(String name, Future<void> Function() action) async {
    try {
      await action();
      results.add({'name': name, 'passed': true});
    } on Object catch (error) {
      results.add({'name': name, 'passed': false, 'error': error.toString()});
    }
  }

  final fixture = Fixture(
    await HttpServer.bind(InternetAddress.loopbackIPv4, 0),
  );
  const transport = LocalApiProbeTransport();
  try {
    await check('canonical endpoint allowlist and localhost IPv4', () async {
      for (final host in <String>[
        'localhost',
        'LOCALHOST',
        '127.0.0.1',
        '127.255.255.254',
        '10.0.0.1',
        '172.16.0.1',
        '172.31.255.254',
        '192.168.1.1',
        '169.254.1.1',
      ]) {
        final uri = parseLocalApiProbeUri('http://$host:32145/snapshot');
        require(uri.port == 32145, 'Port changed');
        if (host.toLowerCase() == 'localhost') {
          require(uri.host == '127.0.0.1', 'localhost needs fixed IPv4');
        }
      }
      for (final endpoint in <String>[
        'http://8.8.8.8:80/',
        'http://0.0.0.0:32145/',
        'http://172.15.0.1:32145/',
        'http://172.32.0.1:32145/',
        'http://192.169.0.1:32145/',
        'http://example.com:32145/',
        'http://127.1:32145/',
        'http://2130706433:32145/',
        'http://0177.0.0.1:32145/',
        'http://127.0.0.256:32145/',
        'http://[::1]:32145/',
        'http://[::ffff:127.0.0.1]:32145/',
        'https://127.0.0.1:32145/',
        '127.0.0.1:32145/',
        'http://127.0.0.1/',
        'http://127.0.0.1:0/',
        'http://127.0.0.1:65536/',
        'http://user@127.0.0.1:32145/',
        'http://127.0.0.1:32145/#fragment',
        'http://127.0.0.1:32145/\r\nInjected: true',
      ]) {
        await expectFailure(
          () async => parseLocalApiProbeUri(endpoint),
          ProbeFailureCode.invalidEndpoint,
          WriteDisposition.notSent,
        );
      }
    });

    await check(
      'native POST uses UTF-8 Content-Length and no chunked',
      () async {
        final body = <String, Object?>{'label': '设备🌸', 'recipe': '八目鳗'};
        final expectedBytes = utf8.encode(jsonEncode(body)).length;
        final result = await transport.request(
          endpoint: fixture.url('/echo'),
          method: 'POST',
          token: fixtureToken,
          clientLabel: 'Windows companion',
          authorityRevision: 9223372036854775807,
          body: body,
        );
        require(
          result['contentLength'] == expectedBytes,
          'Wrong Content-Length',
        );
        require(
          result['receivedBytes'] == expectedBytes,
          'Wrong received bytes',
        );
        require(
          result['transferEncoding'] == null,
          'Chunked request reached server',
        );
        require(result['connection'] == 'close', 'Connection must close');
        require(result['cacheControl'] == 'no-store', 'Cache policy changed');
        require(
          result['contentType'] == 'application/json; charset=utf-8',
          'JSON charset missing',
        );
        require(result['correctFixtureToken'] == true, 'Token header missing');
        require(
          result['clientId'] == 'p0-network-probe-device',
          'Device ID changed',
        );
        require(result['clientLabel'] == 'Windows companion', 'Label changed');
        require(
          result['authority'] == '9223372036854775807',
          'Int64 lost precision',
        );
        require(
          jsonEncode(result['body']) == jsonEncode(body),
          'UTF-8 body changed',
        );
      },
    );

    await check('HTTP redirect is rejected without a second request', () async {
      await expectFailure(
        () => transport.request(
          endpoint: fixture.url('/redirect'),
          token: fixtureToken,
          method: 'POST',
        ),
        ProbeFailureCode.redirectRejected,
        WriteDisposition.unknown,
      );
      require(fixture.counts['/redirect'] == 1, 'Redirect request replayed');
      require(
        fixture.counts['/redirect-target'] == null,
        'Redirect target contacted',
      );
    });

    await check(
      'HTTP 401/403/409 are distinguished from uncertain 500',
      () async {
        for (final entry in <int, ProbeFailureCode>{
          401: ProbeFailureCode.unauthorized,
          403: ProbeFailureCode.forbidden,
          409: ProbeFailureCode.conflict,
          500: ProbeFailureCode.httpStatus,
        }.entries) {
          final failure = await expectFailure(
            () => transport.request(
              endpoint: fixture.url('/status/${entry.key}'),
              token: fixtureToken,
              method: 'POST',
            ),
            entry.value,
            entry.key == 500
                ? WriteDisposition.unknown
                : WriteDisposition.rejected,
          );
          require(failure.statusCode == entry.key, 'HTTP status lost');
        }
      },
    );

    await check('HTTP 200 does not prove business success', () async {
      await expectFailure(
        () => transport.request(
          endpoint: fixture.url('/business-failure'),
          token: fixtureToken,
          method: 'POST',
        ),
        ProbeFailureCode.businessRejected,
        WriteDisposition.unknown,
      );
      require(
        fixture.counts['/business-failure'] == 1,
        'Business failure replayed',
      );
      final snapshot = await transport.request(
        endpoint: fixture.url('/snapshot'),
        token: fixtureToken,
      );
      require(
        snapshot['snapshotSignature'] == 'fixture',
        'Valid read rejected',
      );
    });

    await check(
      'invalid/truncated JSON or UTF-8 never confirms a POST',
      () async {
        for (final path in <String>[
          '/invalid-utf8',
          '/invalid-json',
          '/array',
          '/snapshot',
        ]) {
          await expectFailure(
            () => transport.request(
              endpoint: fixture.url(path),
              token: fixtureToken,
              method: 'POST',
            ),
            ProbeFailureCode.invalidResponse,
            WriteDisposition.unknown,
          );
        }
      },
    );

    await check('bounded response rejects oversize data', () async {
      await expectFailure(
        () => const LocalApiProbeTransport(maxResponseBytes: 128).request(
          endpoint: fixture.url('/large'),
          token: fixtureToken,
          method: 'POST',
        ),
        ProbeFailureCode.responseTooLarge,
        WriteDisposition.unknown,
      );
    });

    await check('truncated HTTP body leaves POST result unknown', () async {
      final server = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
      final handlers = <Future<void>>[];
      final listener = server.listen((socket) {
        final handler = () async {
          final received = <int>[];
          try {
            await for (final bytes in socket) {
              received.addAll(bytes);
              if (ascii
                  .decode(received, allowInvalid: true)
                  .contains('\r\n\r\n')) {
                socket.add(
                  ascii.encode(
                    'HTTP/1.1 200 OK\r\nContent-Length: 100\r\n'
                    'Connection: close\r\n\r\n{"ok":true}',
                  ),
                );
                await socket.flush();
                await socket.close();
                break;
              }
            }
          } finally {
            socket.destroy();
          }
        }();
        handlers.add(handler);
      });
      try {
        await expectFailure(
          () => transport.request(
            endpoint: 'http://127.0.0.1:${server.port}/truncated',
            token: fixtureToken,
            method: 'POST',
          ),
          ProbeFailureCode.readFailed,
          WriteDisposition.unknown,
        );
      } finally {
        await server.close();
        await listener.cancel();
        await Future.wait(handlers);
      }
    });

    await check(
      'timeout after POST reception is unknown and never replayed',
      () async {
        await expectFailure(
          () =>
              const LocalApiProbeTransport(
                responseTimeout: Duration(milliseconds: 150),
              ).request(
                endpoint: fixture.url('/slow'),
                token: fixtureToken,
                method: 'POST',
                body: <String, Object?>{'fixture': true},
              ),
          ProbeFailureCode.responseTimeout,
          WriteDisposition.unknown,
        );
        require(
          fixture.counts['/slow'] == 1,
          'Expected exactly one received POST',
        );
      },
    );

    await check('GET timeout has no write outcome', () async {
      await expectFailure(
        () => const LocalApiProbeTransport(
          responseTimeout: Duration(milliseconds: 150),
        ).request(endpoint: fixture.url('/slow'), token: fixtureToken),
        ProbeFailureCode.responseTimeout,
        WriteDisposition.notApplicable,
      );
    });

    await check('validation rejects before sending any request', () async {
      final initial = fixture.counts['/must-not-send'];
      for (final attempt in <Future<Map<String, Object?>> Function()>[
        () => transport.request(
          endpoint: fixture.url('/must-not-send'),
          token: 'x\r\ny',
        ),
        () => transport.request(
          endpoint: fixture.url('/must-not-send'),
          token: fixtureToken,
          clientLabel: '中文头部',
        ),
        () => transport.request(
          endpoint: fixture.url('/must-not-send'),
          token: fixtureToken,
          method: 'DELETE',
        ),
        () => transport.request(
          endpoint: fixture.url('/must-not-send'),
          token: fixtureToken,
          body: <String, Object?>{'x': 1},
        ),
        () => transport.request(
          endpoint: fixture.url('/must-not-send'),
          token: fixtureToken,
          method: 'POST',
          body: 'x' * 65537,
        ),
      ]) {
        await expectFailure(
          attempt,
          ProbeFailureCode.invalidRequest,
          WriteDisposition.notSent,
        );
      }
      require(
        fixture.counts['/must-not-send'] == initial,
        'Invalid request sent',
      );
    });

    await check(
      'closed loopback port is a connect failure, not unknown write',
      () async {
        final unused = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
        final port = unused.port;
        await unused.close();
        // The OS may reject immediately or exceed the connection budget.
        // Both are valid only before any POST bytes could have been sent.
        await expectFailure(
          () => transport.request(
            endpoint: 'http://127.0.0.1:$port/closed',
            token: fixtureToken,
            method: 'POST',
          ),
          ProbeFailureCode.connectionRefused,
          WriteDisposition.notSent,
          alsoAccept: {ProbeFailureCode.connectTimeout},
        );
      },
    );

    await check(
      'proxy environment cannot receive fixture traffic or Token',
      () async {
        final proxy = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
        var proxyConnections = 0;
        final listener = proxy.listen((socket) {
          proxyConnections++;
          socket.destroy();
        });
        try {
          final proxyUrl = 'http://127.0.0.1:${proxy.port}';
          final process = await Process.start(
            Platform.resolvedExecutable,
            <String>[
              if (Platform.script.path.endsWith('.dart'))
                Platform.script.toFilePath(),
              '--proxy-child',
              fixture.url('/proxy-origin'),
            ],
            environment: <String, String>{
              'HTTP_PROXY': proxyUrl,
              'HTTPS_PROXY': proxyUrl,
              'http_proxy': proxyUrl,
              'https_proxy': proxyUrl,
              'ALL_PROXY': proxyUrl,
              'all_proxy': proxyUrl,
              'NO_PROXY': '',
              'no_proxy': '',
            },
          );
          final childOutput = process.stdout.transform(utf8.decoder).join();
          final childError = process.stderr.transform(utf8.decoder).join();
          final status = await process.exitCode.timeout(
            const Duration(seconds: 15),
            onTimeout: () async {
              process.kill();
              await process.exitCode;
              throw TimeoutException('Proxy child did not terminate');
            },
          );
          final output = await childOutput;
          final error = await childError;
          require(status == 0, 'Proxy child failed: $error');
          require(
            output.contains('DIRECT_ORIGIN_CONFIRMED'),
            'Origin not confirmed',
          );
          require(
            fixture.counts['/proxy-origin'] == 1,
            'Origin did not receive once',
          );
          require(proxyConnections == 0, 'Fixture traffic reached proxy');
        } finally {
          await proxy.close();
          await listener.cancel();
        }
      },
    );
  } finally {
    await fixture.close();
  }
  final passed = results.every((result) => result['passed'] == true);
  stdout.writeln(
    const JsonEncoder.withIndent('  ').convert({
      'probe': 'mystia-steward-companion-dart-local-api',
      'os': Platform.operatingSystem,
      'osVersion': Platform.operatingSystemVersion,
      'dartVersion': Platform.version,
      'passed': passed,
      'checks': results,
      'scope': 'Native loopback HTTP on the reported host only; no real Mod, LAN, Flutter UI or Android permission evidence.',
    }),
  );
  if (!passed) exitCode = 1;
}
