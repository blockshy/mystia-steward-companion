import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:companion_platform/companion_platform.dart';

/// 只向本次隔离控制口发送固定合成请求；不创建业务HTTP客户端、不读取真实Token。
Future<String> exchangeControl(int port, String frame) async {
  final socket = await Socket.connect(
    '127.0.0.1',
    port,
  ).timeout(const Duration(seconds: 2));
  try {
    final response = socket
        .cast<List<int>>()
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .first;
    socket.write(frame);
    await socket.flush();
    // 关闭写半边通知原生请求已完整；读半边仍等待ACK，finally才销毁整个连接。
    await socket.close();
    return await response.timeout(const Duration(seconds: 2));
  } finally {
    socket.destroy();
  }
}

/// 验证TCP接管后真正公开给Pigeon的原子pair，而不是仅检查ACK字符串。
/// 轮询期间只允许完整旧pair或完整新pair，任何混合值均立即失败；证据不记录Token。
Future<void> verifyConnectionArguments(
  CompanionPlatform platform,
  int port,
  Map<String, Object?> evidence,
) async {
  var expectedApi = 'http://127.0.0.1:$port';
  var expectedToken = '';
  Future<void> accepted(String api, String token) async {
    final response = await exchangeControl(
      port,
      'mystia-companion-v2:show\n--api=$api\n--token=$token\n',
    );
    if (!response.startsWith('mystia-companion-v2:ok:'))
      throw StateError('完整凭据对未接管。');
    final deadline = DateTime.now().add(const Duration(seconds: 2));
    while (true) {
      final actual = await platform.initialize();
      if (actual.endpoint == api && actual.token == token) break;
      if (actual.endpoint != expectedApi || actual.token != expectedToken)
        throw StateError('原生激活公开了混合凭据。');
      if (DateTime.now().isAfter(deadline)) throw StateError('原生激活未按期提交。');
      await Future<void>.delayed(const Duration(milliseconds: 10));
    }
    expectedApi = api;
    expectedToken = token;
  }

  await accepted('http://127.0.0.1:49191', 'synthetic-native-token-a');
  var rejected = 0;
  for (final arguments in [
    '--api=http://127.0.0.1:49192\n',
    '--token=synthetic-native-token-b\n',
    '--api=http://127.0.0.1:49192\n--api=http://127.0.0.1:49193\n--token=synthetic-native-token-b\n',
    '--api=http://127.0.0.1:49192\n--token=synthetic-native-token-b\n--token=synthetic-native-token-c\n',
    '--api=\n--token=synthetic-native-token-b\n',
  ]) {
    final response = await exchangeControl(
      port,
      'mystia-companion-v2:show\n$arguments',
    );
    final actual = await platform.initialize();
    if (response != 'mystia-companion-v2:rejected' ||
        actual.endpoint != expectedApi ||
        actual.token != expectedToken)
      throw StateError('无效凭据更新未被原样拒绝。');
    rejected++;
  }
  await accepted('http://127.0.0.1:49192', 'synthetic-native-token-b');
  // 空Token必须明确覆盖旧Token，不能在原生层补回旧值。
  await accepted('http://127.0.0.1:49193', '');
  final bare = await exchangeControl(port, 'mystia-companion-v2:show\n');
  final unchanged = await platform.initialize();
  if (!bare.startsWith('mystia-companion-v2:ok:') ||
      unchanged.endpoint != expectedApi ||
      unchanged.token != expectedToken)
    throw StateError('无凭据显示命令改变了连接。');
  evidence['invalidCredentialFramesRejected'] = rejected;
  evidence['credentialPairAtomicReplacement'] = true;
  evidence['explicitEmptyTokenReplacesOldToken'] = true;
  evidence['bareShowPreservesCredentials'] = true;
}

/// 仅用于受控 Windows 进程的集成入口。强制隔离名称和非生产端口，拒绝游戏参数。
Future<void> main(List<String> arguments) async {
  WidgetsFlutterBinding.ensureInitialized();
  final names = arguments
      .where((item) => item.startsWith('--isolation-id='))
      .toList();
  if (!Platform.isWindows ||
      names.length != 1 ||
      arguments.any(
        (item) =>
            item.startsWith('--game-pid=') ||
            item.startsWith('--token=') && item != '--token=',
      )) {
    throw StateError('原生验证只允许无游戏的隔离 Windows 进程。');
  }
  final id = names.single.substring('--isolation-id='.length);
  if (!RegExp(r'^[a-z0-9-]{8,64}$').hasMatch(id) ||
      !arguments.any(
        (arg) =>
            arg.startsWith('--control-port=') && arg != '--control-port=32146',
      )) {
    throw StateError('缺少独立控制端口或隔离目录。');
  }
  final platform = CompanionPlatform.instance;
  final controlPort = int.parse(
    arguments
        .singleWhere((arg) => arg.startsWith('--control-port='))
        .substring('--control-port='.length),
  );
  final evidence = <String, Object?>{
    'isolationId': id,
    'scope': 'synthetic native platform only',
  };
  try {
    final launch = await platform.initialize();
    if (!launch.desktop ||
        launch.gamePid != null ||
        launch.token != '' ||
        launch.endpoint != 'http://127.0.0.1:$controlPort')
      throw StateError('隔离启动身份错误。');
    evidence['coldStartExplicitEmptyPair'] = true;
    runApp(
      const Directionality(
        textDirection: TextDirection.ltr,
        child: ColoredBox(
          color: Color(0xCC17202A),
          child: Center(
            child: Text('平台隔离验证', style: TextStyle(color: Color(0xFFFFFFFF))),
          ),
        ),
      ),
    );
    await verifyConnectionArguments(platform, controlPort, evidence);
    await platform.writeSecret('native-smoke', 'synthetic-first-value');
    if (await platform.readSecret('native-smoke') != 'synthetic-first-value')
      throw StateError('DPAPI 首次写入回读失败。');
    await platform.writeSecret('native-smoke', 'synthetic-second-value');
    if (await platform.readSecret('native-smoke') != 'synthetic-second-value')
      throw StateError('DPAPI 更新回读失败。');
    evidence['dpapiWriteReadUpdate'] = true;
    await platform.writePreferences({'nativeSmoke': id});
    if ((await platform.readPreferences())['nativeSmoke'] != id)
      throw StateError('偏好持久化失败。');
    evidence['preferencesDurableReadback'] = true;
    final cipher = File(
      '${Platform.environment['LOCALAPPDATA']}/mystia-steward-companion/flutter-v1/isolation/$id/native-smoke.dpapi',
    );
    final original = await cipher.readAsBytes();
    final damaged = original.toList()..[original.length - 1] ^= 1;
    await cipher.writeAsBytes(damaged, flush: true);
    var rejected = 0;
    for (final action in <Future<void> Function()>[
      () async {
        await platform.readSecret('native-smoke');
      },
      () => platform.writeSecret('native-smoke', 'synthetic-must-not-replace'),
      () => platform.deleteSecret('native-smoke'),
    ]) {
      try {
        await action();
      } catch (_) {
        rejected++;
      }
    }
    if (rejected != 3 ||
        base64Encode(await cipher.readAsBytes()) != base64Encode(damaged))
      throw StateError('损坏密文没有被完整保留。');
    evidence['corruptCiphertextReadWriteDeleteRejected'] = true;
    // 只修复本入口刚生成的合成故障样本，产品存储 API 不提供自动重置分支。
    await cipher.writeAsBytes(original, flush: true);
    await platform.deleteSecret('native-smoke');
    evidence['secretDeleted'] =
        await platform.readSecret('native-smoke') == null;
    await platform.applyWindowPreferences(
      WindowPreferences(
        alwaysOnTop: true,
        mousePassthrough: false,
        keepVisible: false,
        switchCooldownMs: 800,
      ),
    );
    evidence['topmostApplied'] = true;
    try {
      await platform.applyWindowPreferences(
        WindowPreferences(
          alwaysOnTop: false,
          mousePassthrough: true,
          keepVisible: false,
          switchCooldownMs: 800,
        ),
      );
      evidence['clickThroughApplied'] = true;
    } catch (_) {
      evidence['clickThroughApplied'] = false;
    }
    await platform.applyWindowPreferences(
      WindowPreferences(
        alwaysOnTop: false,
        mousePassthrough: false,
        keepVisible: false,
        switchCooldownMs: 800,
      ),
    );
    evidence['hidden'] = await platform.hide();
    evidence['showFocused'] = await platform.show();
    evidence['result'] = 'passed';
  } catch (error) {
    evidence['result'] = 'failed';
    evidence['errorType'] = error.runtimeType.toString();
    // 固定测试错误信息不含凭据；不输出任意原生消息或启动参数。
  } finally {
    await File('platform-native-evidence.json').writeAsString(
      const JsonEncoder.withIndent('  ').convert(evidence),
      flush: true,
    );
    await platform.dispose();
    // 保留窗口供父测试进程验证单实例及 v2 exit ACK，最长只存活三十秒。
    Timer(const Duration(seconds: 30), () => platform.exit());
  }
}
