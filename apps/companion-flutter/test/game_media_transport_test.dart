import 'dart:io';

import 'package:companion_client/companion_client.dart';
import 'package:flutter_test/flutter_test.dart';

/// 使用隔离回环服务器检查实际 HTTP 字节与认证；不启动游戏或复用真实 Token。
void main() {
  test('PNG 使用认证请求头和精确代次，保留原始二进制；拒绝错误 MIME/重定向/超限', () async {
    final server = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    final transport = DartIoTransport();
    final connection = ApiConnection(
      endpoint: 'http://127.0.0.1:${server.port}',
      token: 'synthetic-image-token',
      clientId: 'synthetic-device-0002',
      clientLabel: '合成图片设备',
    );
    var mode = 'png';
    final requests = <HttpRequest>[];
    final subscription = server.listen((request) async {
      requests.add(request);
      final response = request.response;
      if (mode == 'redirect') {
        response.statusCode = 302;
        response.headers.set(HttpHeaders.locationHeader, '/other');
      } else if (mode == 'large') {
        response.headers.contentType = ContentType('image', 'png');
        response.add(List<int>.filled(256 * 1024 + 1, 0));
      } else {
        response.headers.contentType = mode == 'png'
            ? ContentType('image', 'png')
            : ContentType.text;
        response.add([137, 80, 78, 71]);
      }
      await response.close();
    });
    addTearDown(() async {
      transport.close();
      await subscription.cancel();
      await server.close(force: true);
    });
    final bytes = await transport.image(
      connection,
      generation: 'gen-1',
      key: 'food:42',
    );
    expect(bytes, [137, 80, 78, 71]);
    expect(requests.single.uri.path, '/game-media/image');
    expect(requests.single.uri.queryParameters, {
      'generation': 'gen-1',
      'key': 'food:42',
    });
    expect(requests.single.uri.toString(), isNot(contains(connection.token)));
    expect(
      requests.single.headers.value('X-Mystia-Steward-Companion-Token'),
      connection.token,
    );
    for (final entry in {
      'mime': 'invalid-image',
      'redirect': 'redirect-rejected',
      'large': 'response-too-large',
    }.entries) {
      mode = entry.key;
      await expectLater(
        transport.image(connection, generation: 'gen-1', key: 'food:42'),
        throwsA(
          isA<ApiException>().having(
            (value) => value.code,
            'code',
            entry.value,
          ),
        ),
      );
    }
    expect(requests.length, 4);
  });
}
