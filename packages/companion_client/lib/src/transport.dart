import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:companion_protocol/companion_protocol.dart';

/// 连接是不可变凭据组；切换地址或身份必须创建新会话，避免迟到请求复用新 Token。
class ApiConnection {
  ApiConnection({
    required String endpoint,
    required this.token,
    required this.clientId,
    required this.clientLabel,
  }) : endpoint = validateEndpoint(endpoint) {
    if (!RegExp(r'^[a-zA-Z0-9-]{16,64}$').hasMatch(clientId))
      throw const FormatException('设备 ID 格式无效。');
    if (token.isEmpty ||
        token.contains(RegExp(r'[\r\n]')) ||
        clientLabel.isEmpty ||
        clientLabel.length > 48 ||
        clientLabel.contains(RegExp(r'[\r\n]'))) {
      throw const FormatException('连接凭据或设备名称无效。');
    }
  }
  final Uri endpoint;
  final String token;
  final String clientId;
  final String clientLabel;
  bool get isLoopback => endpoint.host.split('.').first == '127';
}

/// 只允许 HTTP 回环或私有 IPv4；不解析 DNS、不跟随重定向，不经过系统代理。
Uri validateEndpoint(String input) {
  final uri = Uri.tryParse(input.trim());
  if (uri == null ||
      uri.scheme != 'http' ||
      uri.userInfo.isNotEmpty ||
      uri.hasQuery ||
      uri.hasFragment ||
      uri.path.isNotEmpty && uri.path != '/' ||
      !uri.hasPort ||
      uri.port < 1024 ||
      uri.port > 65535) {
    throw const FormatException('请填写带端口的 HTTP 回环或局域网 IPv4 地址。');
  }
  final host = uri.host.toLowerCase() == 'localhost' ? '127.0.0.1' : uri.host;
  final pieces = host.split('.');
  if (pieces.length != 4 ||
      pieces.any((part) => !RegExp(r'^(0|[1-9][0-9]{0,2})$').hasMatch(part))) {
    throw const FormatException('只允许明确的 IPv4 地址。');
  }
  final octets = pieces.map(int.parse).toList();
  if (octets.any((value) => value > 255) ||
      !(octets[0] == 127 ||
          octets[0] == 10 ||
          octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31 ||
          octets[0] == 192 && octets[1] == 168 ||
          octets[0] == 169 && octets[1] == 254)) {
    throw const FormatException('地址必须属于回环或私有局域网。');
  }
  return uri.replace(host: host, path: '');
}

/// 测试与产品共用传输接口。实现不能自动重试 mutation，调用者负责结果确认。
abstract interface class CompanionTransport {
  Future<JsonMap> request(
    ApiConnection connection,
    String method,
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  });
  void close();
}

/// 游戏图像采用独立的二进制能力，不把 PNG 转成 JSON/base64，也不要求已有纯 JSON
/// 测试宿主实现无关能力。图片请求仍共用同一凭据、超时、重定向和大小限制。
abstract interface class CompanionImageTransport {
  Future<Uint8List> image(
    ApiConnection connection, {
    required String generation,
    required String key,
  });
}

class DartIoTransport implements CompanionTransport, CompanionImageTransport {
  DartIoTransport({this.maxResponseBytes = 32 * 1024 * 1024}) {
    _client.findProxy = (_) => 'DIRECT';
    _client.connectionTimeout = const Duration(seconds: 5);
    _client.maxConnectionsPerHost = 6;
  }
  final int maxResponseBytes;
  final HttpClient _client = HttpClient();
  bool _closed = false;

  @override
  Future<JsonMap> request(
    ApiConnection connection,
    String method,
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  }) => _request<JsonMap>(
    connection,
    method,
    path,
    query: query,
    body: body,
    authorityRevision: authorityRevision,
    timeout: timeout,
  );

  @override
  Future<Uint8List> image(
    ApiConnection connection, {
    required String generation,
    required String key,
  }) => _request<Uint8List>(
    connection,
    'GET',
    '/game-media/image',
    query: {'generation': generation, 'key': key},
    imageResponse: true,
  );

  /// JSON 与 PNG 只在解码阶段分流，确保图片也经过相同的认证和连接边界。
  /// 非成功响应仍按 JSON 错误解析；成功的图片必须声明 image/png，最多 256 KiB。
  Future<T> _request<T>(
    ApiConnection connection,
    String method,
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
    bool imageResponse = false,
  }) async {
    if (_closed) throw const ApiException('closed', '连接已关闭。');
    if (!{'GET', 'POST'}.contains(method) ||
        !RegExp(r'^/[a-z0-9/-]+$').hasMatch(path) ||
        path.contains('//')) {
      throw const ApiException('invalid-request', '请求路径或方法无效。');
    }
    if (method == 'GET' && body != null)
      throw const ApiException('invalid-request', '读取请求不能包含正文。');
    final bytes = body == null ? <int>[] : utf8.encode(jsonEncode(body));
    if (bytes.length > 65536)
      throw const ApiException('request-too-large', 'JSON 请求超过 64 KiB。');
    final uri = connection.endpoint.replace(
      path: path,
      queryParameters: query.isEmpty ? null : query,
    );
    HttpClientRequest? active;
    var expired = false;
    var sent = false;
    final responseLimit = imageResponse ? 256 * 1024 : maxResponseBytes;
    Future<T> perform() async {
      final request = await _client.openUrl(method, uri);
      active = request;
      if (expired || _closed) {
        request.abort();
        throw const ApiException('cancelled', '请求已取消。');
      }
      request.followRedirects = false;
      request.persistentConnection = false;
      request.headers.set('X-Mystia-Steward-Companion-Token', connection.token);
      request.headers.set(
        'X-Mystia-Steward-Companion-Client-Id',
        connection.clientId,
      );
      // 现有协议使用 ASCII 请求头，设备名称统一 URI 编码并由服务端解码。
      request.headers.set(
        'X-Mystia-Steward-Companion-Client-Label',
        Uri.encodeComponent(connection.clientLabel),
      );
      if (authorityRevision != null)
        request.headers.set(
          'X-Mystia-Steward-Companion-Authority-Revision',
          authorityRevision.toString(),
        );
      request.headers.set(
        HttpHeaders.acceptHeader,
        imageResponse ? 'image/png' : 'application/json',
      );
      if (body != null) request.headers.contentType = ContentType.json;
      request.contentLength = bytes.length;
      sent = true;
      if (bytes.isNotEmpty) request.add(bytes);
      final response = await request.close();
      if (response.isRedirect)
        throw const ApiException('redirect-rejected', '本地 API 不允许重定向。');
      if (response.contentLength > responseLimit)
        throw const ApiException('response-too-large', '响应超过允许大小。');
      final buffer = BytesBuilder(copy: false);
      await for (final chunk in response) {
        if (buffer.length + chunk.length > responseLimit)
          throw const ApiException('response-too-large', '响应超过允许大小。');
        buffer.add(chunk);
      }
      final responseBytes = buffer.takeBytes();
      if (imageResponse &&
          response.statusCode >= 200 &&
          response.statusCode < 300) {
        if (response.headers.contentType?.mimeType != 'image/png') {
          throw const ApiException('invalid-image', '游戏图片响应类型无效。');
        }
        return responseBytes as T;
      }
      final value = jsonMap(jsonDecode(utf8.decode(responseBytes)));
      if (response.statusCode < 200 || response.statusCode >= 300) {
        // 不拼接请求 URL、请求头或 Token；错误只暴露经过服务端生成的短消息。
        final raw = jsonString(
          value,
          'error',
          fallback: 'HTTP ${response.statusCode}',
        );
        throw ApiException(
          jsonString(value, 'code', fallback: 'http-${response.statusCode}'),
          raw.replaceAll(connection.token, '[已隐藏]'),
          statusCode: response.statusCode,
        );
      }
      return value as T;
    }

    try {
      return await perform().timeout(timeout);
    } on TimeoutException {
      expired = true;
      active?.abort();
      throw ApiException(
        'timeout',
        method == 'POST' && sent ? '请求超时，结果待确认；请刷新状态，勿重复提交。' : '连接读取超时。',
        possiblyApplied: method == 'POST' && sent,
      );
    } on ApiException catch (error) {
      active?.abort();
      // 收到完整的非成功 HTTP 响应时已经明确拒绝；响应体超限/重定向等不能证明写入未生效。
      if (method == 'POST' &&
          sent &&
          error.statusCode == null &&
          !error.possiblyApplied) {
        throw ApiException(error.code, error.message, possiblyApplied: true);
      }
      rethrow;
    } on FormatException {
      throw ApiException(
        'invalid-response',
        '服务端返回格式无效。',
        possiblyApplied: method == 'POST' && sent,
      );
    } on Object {
      throw ApiException(
        'transport-failed',
        method == 'POST' && sent ? '连接中断，操作结果待确认。' : '无法连接游戏电脑上的 Mod。',
        possiblyApplied: method == 'POST' && sent,
      );
    }
  }

  @override
  void close() {
    _closed = true;
    _client.close(force: true);
  }
}
