import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

/// Transport evidence, not a claim about a game's eventual state.
enum WriteDisposition { notApplicable, notSent, rejected, unknown }

enum ProbeFailureCode {
  invalidEndpoint,
  invalidRequest,
  connectTimeout,
  connectionRefused,
  connectFailed,
  writeTimeout,
  writeFailed,
  responseTimeout,
  readFailed,
  unauthorized,
  forbidden,
  conflict,
  redirectRejected,
  httpStatus,
  invalidResponse,
  responseTooLarge,
  businessRejected,
}

final class ProbeFailure implements Exception {
  const ProbeFailure(this.code, this.disposition, {this.statusCode});

  final ProbeFailureCode code;
  final WriteDisposition disposition;
  final int? statusCode;

  // Never include endpoint, Token, response body or the underlying I/O message.
  @override
  String toString() =>
      'ProbeFailure(${code.name}, ${disposition.name}, $statusCode)';
}

/// The native proxy's address policy, with the plan's canonical HTTP syntax.
/// No name resolution is permitted; localhost is rewritten to IPv4 loopback.
Uri parseLocalApiProbeUri(String input) {
  ProbeFailure invalid() => const ProbeFailure(
    ProbeFailureCode.invalidEndpoint,
    WriteDisposition.notSent,
  );
  final value = input.trim();
  final authority = RegExp(r'^http://([^/?#]+)(?:[/?]|$)').firstMatch(value);
  if (authority == null || value.contains(RegExp(r'[\x00-\x20\x7f]'))) {
    throw invalid();
  }
  final hostPort = RegExp(r'^([^:@\[\]]+):([0-9]{1,5})$')
      .firstMatch(authority.group(1)!);
  if (hostPort == null) throw invalid();
  final host = hostPort.group(1)!;
  final port = int.tryParse(hostPort.group(2)!);
  if (port == null || port < 1 || port > 65535) throw invalid();

  final address = host.toLowerCase() == 'localhost' ? '127.0.0.1' : host;
  final parts = address.split('.');
  if (parts.length != 4) throw invalid();
  final octets = <int>[];
  for (final part in parts) {
    // Match Rust Ipv4Addr's canonical decimal form, including no leading zero.
    if (!RegExp(r'^(0|[1-9][0-9]{0,2})$').hasMatch(part)) throw invalid();
    final octet = int.parse(part);
    if (octet > 255) throw invalid();
    octets.add(octet);
  }
  final allowed =
      octets[0] == 127 ||
      octets[0] == 10 ||
      (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) ||
      (octets[0] == 192 && octets[1] == 168) ||
      (octets[0] == 169 && octets[1] == 254);
  if (!allowed) throw invalid();

  final Uri uri;
  try {
    uri = Uri.parse(value);
  } on FormatException {
    throw invalid();
  }
  if (uri.userInfo.isNotEmpty || uri.hasFragment || uri.scheme != 'http') {
    throw invalid();
  }
  return uri.replace(host: address, port: port);
}

/// A deliberately small P0 transport, not the future client's session service.
/// It performs one attempt, with no redirects, proxy discovery or POST replay.
final class LocalApiProbeTransport {
  const LocalApiProbeTransport({
    this.connectTimeout = const Duration(seconds: 2),
    this.writeTimeout = const Duration(seconds: 2),
    this.responseTimeout = const Duration(seconds: 2),
    this.maxResponseBytes = 1024 * 1024,
  });

  final Duration connectTimeout;
  final Duration writeTimeout;
  final Duration responseTimeout;
  // P0 fixture budget, not a new production /runtime-data size contract.
  final int maxResponseBytes;

  static const tokenHeader = 'X-Mystia-Steward-Companion-Token';
  static const clientIdHeader = 'X-Mystia-Steward-Companion-Client-Id';
  static const clientLabelHeader = 'X-Mystia-Steward-Companion-Client-Label';
  static const authorityHeader =
      'X-Mystia-Steward-Companion-Authority-Revision';

  Future<Map<String, Object?>> request({
    required String endpoint,
    required String token,
    String method = 'GET',
    String clientId = 'p0-network-probe-device',
    String clientLabel = 'P0 network probe',
    int? authorityRevision,
    Object? body,
  }) async {
    final uri = parseLocalApiProbeUri(endpoint);
    ProbeFailure invalidRequest() => const ProbeFailure(
      ProbeFailureCode.invalidRequest,
      WriteDisposition.notSent,
    );
    bool asciiHeader(String value) =>
        value.codeUnits.every((unit) => unit >= 0x20 && unit <= 0x7e);
    if ((method != 'GET' && method != 'POST') ||
        (method == 'GET' && body != null) ||
        !asciiHeader(token) ||
        !asciiHeader(clientLabel) ||
        clientLabel.length > 48 ||
        !RegExp(r'^[a-zA-Z0-9-]{16,64}$').hasMatch(clientId) ||
        (authorityRevision != null && authorityRevision <= 0) ||
        connectTimeout <= Duration.zero ||
        writeTimeout <= Duration.zero ||
        responseTimeout <= Duration.zero ||
        maxResponseBytes < 1) {
      throw invalidRequest();
    }
    final List<int> bytes;
    try {
      bytes = body == null ? <int>[] : utf8.encode(jsonEncode(body));
    } on Object {
      throw invalidRequest();
    }
    if (bytes.length > 65536) throw invalidRequest();

    final isWrite = method == 'POST';
    var sent = false;
    var stage = 'connect';
    WriteDisposition unresolved() => !isWrite
        ? WriteDisposition.notApplicable
        : sent
        ? WriteDisposition.unknown
        : WriteDisposition.notSent;
    final client = HttpClient();
    client.findProxy = (_) => 'DIRECT';
    client.autoUncompress = false;
    try {
      final request = await client.openUrl(method, uri).timeout(connectTimeout);
      request
        ..followRedirects = false
        ..persistentConnection = false
        ..contentLength = bytes.length;
      request.headers
        ..set(HttpHeaders.cacheControlHeader, 'no-store')
        ..set(tokenHeader, token)
        ..set(clientIdHeader, clientId)
        ..set(clientLabelHeader, clientLabel);
      if (authorityRevision != null) {
        request.headers.set(authorityHeader, authorityRevision.toString());
      }
      if (body != null) {
        request.headers.contentType = ContentType.json;
      }
      stage = 'write';
      // From the first possible flush onwards, failure cannot prove no mutation.
      sent = true;
      request.add(bytes);
      await request.flush().timeout(writeTimeout);
      stage = 'read';
      final response = await request.close().timeout(responseTimeout);
      final status = response.statusCode;
      if (status >= 300 && status < 400) {
        throw ProbeFailure(
          ProbeFailureCode.redirectRejected,
          unresolved(),
          statusCode: status,
        );
      }
      if (status < 200 || status >= 300) {
        final code = switch (status) {
          401 => ProbeFailureCode.unauthorized,
          403 => ProbeFailureCode.forbidden,
          409 => ProbeFailureCode.conflict,
          _ => ProbeFailureCode.httpStatus,
        };
        throw ProbeFailure(
          code,
          isWrite && (status == 401 || status == 403 || status == 409)
              ? WriteDisposition.rejected
              : unresolved(),
          statusCode: status,
        );
      }
      if (response.contentLength > maxResponseBytes) {
        throw ProbeFailure(ProbeFailureCode.responseTooLarge, unresolved());
      }
      final buffer = BytesBuilder(copy: false);
      await response
          .forEach((chunk) {
            if (buffer.length + chunk.length > maxResponseBytes) {
              throw ProbeFailure(
                ProbeFailureCode.responseTooLarge,
                unresolved(),
              );
            }
            buffer.add(chunk);
          })
          .timeout(responseTimeout);
      final Object? decoded;
      try {
        decoded = jsonDecode(utf8.decode(buffer.takeBytes()));
      } on FormatException {
        throw ProbeFailure(ProbeFailureCode.invalidResponse, unresolved());
      }
      if (decoded is! Map<String, Object?> ||
          (isWrite && decoded['ok'] is! bool)) {
        throw ProbeFailure(ProbeFailureCode.invalidResponse, unresolved());
      }
      if (decoded['ok'] == false) {
        // Business rejection is distinct from transport failure. The caller
        // still needs the route-specific outcome to decide what is safe next.
        throw ProbeFailure(
          ProbeFailureCode.businessRejected,
          isWrite ? WriteDisposition.unknown : WriteDisposition.notApplicable,
          statusCode: status,
        );
      }
      return Map<String, Object?>.unmodifiable(decoded);
    } on ProbeFailure {
      rethrow;
    } on TimeoutException {
      final code = switch (stage) {
        'connect' => ProbeFailureCode.connectTimeout,
        'write' => ProbeFailureCode.writeTimeout,
        _ => ProbeFailureCode.responseTimeout,
      };
      throw ProbeFailure(code, unresolved());
    } on SocketException catch (error) {
      final code = switch (stage) {
        'connect' =>
          const {61, 111, 10061}.contains(error.osError?.errorCode)
              ? ProbeFailureCode.connectionRefused
              : ProbeFailureCode.connectFailed,
        'write' => ProbeFailureCode.writeFailed,
        _ => ProbeFailureCode.readFailed,
      };
      throw ProbeFailure(code, unresolved());
    } on HttpException {
      throw ProbeFailure(
        stage == 'write'
            ? ProbeFailureCode.writeFailed
            : ProbeFailureCode.readFailed,
        unresolved(),
      );
    } finally {
      client.close(force: true);
    }
  }
}
