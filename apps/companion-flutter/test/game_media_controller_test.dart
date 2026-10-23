import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:companion_client/companion_client.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/game_media_controller.dart';

/// 合成 PNG 与内存 HTTP 契约只覆盖客户端状态机，不读取真实游戏或发布资产。
final _png = base64Decode(
  'iVBORw0KGgoAAAANSUhEUgAAADIAAAAyCAYAAAAeP4ixAAAASElEQVR42u3PMQ0AMAgAsGFjL/4tYQU08JLWQSN/9TsgREREREREREREREREREREREREREREREREREREREREREREREREREQ2BgNuaUcSjuqqAAAAAElFTkSuQmCC',
);

class _MediaTransport implements CompanionTransport, CompanionImageTransport {
  String phase = 'ready', generation = 'generation-a';
  int manifestReads = 0, imageReads = 0, reloads = 0;
  int count = 1;
  bool corrupt = false,
      duplicate = false,
      failReload = false,
      failStatus = false;
  final imageWaits = <Completer<Uint8List>>[];
  bool holdImages = false;
  Completer<void>? manifestWait, statusWait;
  @override
  Future<JsonMap> request(
    ApiConnection connection,
    String method,
    String path, {
    Map<String, String> query = const {},
    JsonMap? body,
    int? authorityRevision,
    Duration timeout = const Duration(seconds: 5),
  }) async {
    switch (path) {
      case '/game-media/status':
        await statusWait?.future;
        if (failStatus) throw const ApiException('temporary', '合成临时读取失败');
        return {
          'phase': phase,
          'generation': phase == 'ready' ? generation : null,
          'completed': phase == 'ready' ? count : 0,
          'total': count,
          'message': '合成资源',
        };
      case '/game-media/manifest':
        manifestReads++;
        await manifestWait?.future;
        final items = [
          for (var i = 0; i < count; i++)
            {
              'key': 'food:$i',
              'kind': 'food',
              'id': i,
              'name': '合成料理 $i',
              'sha256': sha256.convert(_png).toString(),
              'byteLength': _png.length,
              'width': 50,
              'height': 50,
            },
        ];
        return {
          'version': 1,
          'generation': generation,
          'items': [...items, if (duplicate) items.first],
          'recipes': [
            {
              'foodId': 0,
              'recipeId': 100,
              'ingredientIds': [3, 3, 4],
              'cookerType': 1,
            },
          ],
        };
      case '/game-media/reload':
        expect(method, 'POST');
        expect(authorityRevision, 7);
        reloads++;
        if (failReload)
          throw const ApiException('denied', '合成拒绝', statusCode: 409);
        generation = 'generation-b';
        phase = 'extracting';
        return {'ok': true};
      default:
        throw StateError('意外路径 $path');
    }
  }

  @override
  Future<Uint8List> image(
    ApiConnection connection, {
    required String generation,
    required String key,
  }) {
    imageReads++;
    if (holdImages) {
      final pending = Completer<Uint8List>();
      imageWaits.add(pending);
      return pending.future;
    }
    return Future.value(corrupt ? Uint8List.fromList([1, 2, 3]) : _png);
  }

  @override
  void close() {}
}

CompanionApi _api(_MediaTransport transport) => CompanionApi(
  connection: ApiConnection(
    endpoint: 'http://127.0.0.1:32145',
    token: 'synthetic-only',
    clientId: 'synthetic-device-0001',
    clientLabel: '合成设备',
  ),
  transport: transport,
);
Future<void> _turn() => Future<void>.delayed(Duration.zero);

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  test('已缓存宿主启动只核验清单，不伪装成重新提取，完成前不提前就绪', () async {
    final transport = _MediaTransport()..manifestWait = Completer<void>();
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    final phases = <Object?>[controller.status['phase']];
    controller.addListener(() => phases.add(controller.status['phase']));
    final pending = controller.synchronize();
    await _turn();
    expect(controller.status['phase'], 'verifying');
    expect(controller.ready, isFalse);
    expect(controller.items, isEmpty);
    expect(controller.imageFor('food', 0), isNull);
    expect(transport.imageReads, 0);
    transport.manifestWait!.complete();
    expect(await pending, isTrue);
    expect(phases, isNot(contains('extracting')));
    expect(phases.last, 'ready');
    expect(transport.manifestReads, 1);
    expect(transport.reloads, 0);
  });
  test('宿主核验缓存与缓存失效后实际提取明确区分', () async {
    final transport = _MediaTransport()..phase = 'verifying';
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    expect(await controller.synchronize(), isFalse);
    expect(controller.status['phase'], 'verifying');
    expect(transport.manifestReads, 0);
    transport.phase = 'waiting';
    expect(await controller.synchronize(), isFalse);
    expect(controller.status['phase'], 'waiting');
    expect(controller.ready, isFalse);
    expect(transport.manifestReads, 0);
    transport.phase = 'extracting';
    expect(await controller.synchronize(), isFalse);
    expect(controller.status['phase'], 'extracting');
    transport.phase = 'ready';
    expect(await controller.synchronize(), isTrue);
    expect(transport.manifestReads, 1);
  });
  test('已就绪同代次的普通进度轮询保持ready，不重复弹出核验或撤回图片', () async {
    final transport = _MediaTransport();
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    expect(await controller.synchronize(), isTrue);
    controller.imageFor('food', 0);
    await _turn();
    final image = controller.imageFor('food', 0);
    expect(image, isNotNull);
    final phases = <Object?>[];
    controller.addListener(() => phases.add(controller.status['phase']));
    transport.statusWait = Completer<void>();
    final pending = controller.synchronize();
    await _turn();
    expect(controller.ready, isTrue);
    expect(identical(controller.imageFor('food', 0), image), isTrue);
    transport.statusWait!.complete();
    expect(await pending, isTrue);
    expect(phases, isEmpty);
    expect(transport.manifestReads, 1);
    expect(transport.reloads, 0);
  });
  test('旧连接迟到清单不能关闭新连接的独立等待门禁', () async {
    final old = _MediaTransport()..manifestWait = Completer<void>();
    final controller = GameMediaController()..bind(_api(old));
    addTearDown(controller.dispose);
    final pending = controller.synchronize();
    await _turn();
    expect(controller.status['phase'], 'verifying');
    final next = _MediaTransport()..phase = 'waiting';
    controller.bind(_api(next));
    expect(await controller.synchronize(), isFalse);
    old.manifestWait!.complete();
    expect(await pending, isFalse);
    expect(controller.status['phase'], 'waiting');
    expect(controller.ready, isFalse);
    expect(controller.items, isEmpty);
    expect(controller.recipes, isEmpty);
  });
  test('初始化严格等待 ready，再验证清单；相同代次不重复读取，材料重复与顺序保留', () async {
    final transport = _MediaTransport()..phase = 'waiting';
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    expect(await controller.synchronize(), isFalse);
    expect(controller.items, isEmpty);
    transport.phase = 'ready';
    expect(
      await controller.synchronize(),
      isTrue,
      reason: controller.status.toString(),
    );
    expect(controller.recipes['0:100']?['ingredientIds'], [3, 3, 4]);
    expect(await controller.synchronize(), isTrue);
    expect(transport.manifestReads, 1);
    expect(controller.imageFor('food', 0), isNull);
    await _turn();
    expect(controller.imageFor('food', 0), isNotNull);
    expect(controller.imageFor('beverage', 0), isNull);
    expect(controller.imageFor('food', -1), isNull);
    expect(transport.imageReads, 1);
  });
  test('代次撤回图片与配方，旧连接迟到的图片不提交', () async {
    final transport = _MediaTransport()..holdImages = true;
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    await controller.synchronize();
    controller.imageFor('food', 0);
    await _turn();
    controller.bind(_api(_MediaTransport()..phase = 'waiting'));
    transport.imageWaits.single.complete(_png);
    await _turn();
    expect(controller.status['loadedImages'], 0);
    expect(controller.recipes, isEmpty);
    expect(controller.imageFor('food', 0), isNull);
  });
  test('可见图片最多四并发，损坏图片不重试风暴、不污染 Provider', () async {
    final transport = _MediaTransport()
      ..count = 7
      ..holdImages = true;
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    await controller.synchronize();
    for (var id = 0; id < 7; id++) {
      controller.imageFor('food', id);
    }
    await _turn();
    expect(transport.imageReads, 4);
    transport.imageWaits.first.complete(Uint8List.fromList([1, 2, 3]));
    await _turn();
    expect(transport.imageReads, 5);
    for (var repeat = 0; repeat < 10; repeat++) {
      expect(controller.imageFor('food', 0), isNull);
    }
    expect(controller.status['failedImages'], 1);
    expect(transport.imageReads, 5);
    for (final wait in transport.imageWaits.skip(1).toList()) {
      wait.complete(_png);
    }
    await _turn();
    for (final wait
        in transport.imageWaits.where((value) => !value.isCompleted).toList()) {
      wait.complete(_png);
    }
    await _turn();
  });
  test('相同宿主代次失败后恢复，旧 PNG 不提交并且总请求仍限四个', () async {
    final transport = _MediaTransport()
      ..holdImages = true
      ..count = 6;
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    await controller.synchronize();
    controller.imageFor('food', 0);
    await _turn();
    final old = transport.imageWaits.single;
    transport.failStatus = true;
    expect(await controller.synchronize(), isFalse);
    transport.failStatus = false;
    expect(await controller.synchronize(), isTrue);
    for (var id = 0; id < 6; id++) {
      controller.imageFor('food', id);
    }
    await _turn();
    expect(transport.imageReads, 4);
    old.complete(_png);
    await _turn();
    expect(controller.status['loadedImages'], 0);
    expect(transport.imageReads, 5);
    for (final wait in transport.imageWaits.skip(1).toList()) {
      wait.complete(_png);
    }
    await _turn();
    for (final wait
        in transport.imageWaits.where((value) => !value.isCompleted).toList()) {
      wait.complete(_png);
    }
    await _turn();
  });
  test('重复身份目录整批失败，不能 ready 或公开部分配方', () async {
    final transport = _MediaTransport()..duplicate = true;
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    expect(await controller.synchronize(), isFalse);
    expect(controller.status['phase'], 'failed');
    expect(controller.items, isEmpty);
    expect(controller.recipes, isEmpty);
  });
  test('手动重读只发一次 POST，重读期间撤回，完成后接受新代次', () async {
    final transport = _MediaTransport();
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    await controller.synchronize();
    await controller.reload(7);
    expect(transport.reloads, 1);
    expect(controller.ready, isFalse);
    expect(controller.recipes, isEmpty);
    transport.phase = 'ready';
    expect(await controller.synchronize(), isTrue);
    expect(controller.status['generation'], 'generation-b');
    expect(transport.manifestReads, 2);
  });
  test('等待旧租约释放期间立即门禁，prepare 不读旧 ready 或提前发送 reload', () async {
    final transport = _MediaTransport();
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    await controller.synchronize();
    final release = Completer<void>();
    final pending = controller.reload(7, beforeReload: () => release.future);
    expect(controller.ready, isFalse);
    expect(await controller.synchronize(), isFalse);
    expect(transport.reloads, 0);
    release.complete();
    await pending;
    expect(transport.reloads, 1);
  });
  test('重读请求被拒绝不自动重复，GET 可恢复宿主仍然有效的旧代次', () async {
    final transport = _MediaTransport()..failReload = true;
    final controller = GameMediaController()..bind(_api(transport));
    addTearDown(controller.dispose);
    await controller.synchronize();
    await expectLater(controller.reload(7), throwsA(isA<ApiException>()));
    expect(transport.reloads, 1);
    expect(controller.ready, isTrue);
  });
}
