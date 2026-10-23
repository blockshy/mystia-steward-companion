import 'dart:async';
import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:companion_platform/companion_platform.dart';
import 'package:companion_platform/src/platform.g.dart';

/// 假存储模拟有界原生通道；测试关注真实同步边界，不假装验证 DPAPI/Keystore。
class MemoryHost extends PlatformHostApi {
  String? contents;
  bool corruptReadback = false;
  @override
  Future<String?> readPreferences() async => contents;
  @override
  Future<void> writePreferences(String value) async {
    await Future<void>.delayed(const Duration(milliseconds: 2));
    contents = corruptReadback ? '{}' : value;
  }
}

const _initializeChannelName =
    'dev.flutter.pigeon.companion_platform.PlatformHostApi.initialize';
const _pollChannelName =
    'dev.flutter.pigeon.companion_platform.PlatformHostApi.pollInput';

/// 测试穿过真正的 Pigeon HostApi、BasicMessageChannel 和生成 codec，仅在原生
/// messenger 边界控制 handler 注册时间；不以抛异常的假 HostApi 绕过缺通道行为。
class NativeChannels {
  NativeChannels({this.suffix = ''}) {
    messenger.setMockDecodedMessageHandler(pollChannel, (_) async {
      polls++;
      return [input()];
    });
  }
  final String suffix;
  final messenger =
      TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
  int calls = 0, polls = 0;
  String get channelSuffix => suffix.isEmpty ? '' : '.$suffix';
  BasicMessageChannel<Object?> get initializeChannel =>
      BasicMessageChannel<Object?>(
        '$_initializeChannelName$channelSuffix',
        PlatformHostApi.pigeonChannelCodec,
      );
  BasicMessageChannel<Object?> get pollChannel => BasicMessageChannel<Object?>(
    '$_pollChannelName$channelSuffix',
    PlatformHostApi.pigeonChannelCodec,
  );
  CompanionPlatform createPlatform() =>
      CompanionPlatform(host: PlatformHostApi(messageChannelSuffix: suffix));

  void register(Future<Object?> Function() reply) {
    messenger.setMockDecodedMessageHandler(initializeChannel, (_) async {
      calls++;
      return reply();
    });
  }

  /// 返回空二进制回复与原生没有注册 handler 一致，同时计数证明三秒后不再重试。
  void remainUnavailable() {
    messenger.setMockMessageHandler(initializeChannel.name, (_) async {
      calls++;
      return null;
    });
  }

  void clear() {
    messenger.setMockMessageHandler(initializeChannel.name, null);
    messenger.setMockMessageHandler(pollChannel.name, null);
  }
}

/// widget 测试的事件流完成通知也由虚拟事件循环派发；先取消计时器，再推进一次循环，
/// 避免直接 await close() 时测试时钟无法前进，同时保证结束前没有遗留 Timer。
Future<void> closePlatform(
  WidgetTester tester,
  CompanionPlatform platform,
) async {
  final closing = platform.dispose();
  await tester.pump();
  await closing;
}

PlatformInput input({
  bool focus = true,
  bool connected = true,
  int buttons = 0,
  double leftX = 0,
}) => PlatformInput(
  focused: focus,
  connected: connected,
  buttons: buttons,
  leftX: leftX,
  leftY: 0,
  leftTrigger: 0,
  rightTrigger: 0,
  activationRevision: 0,
  launch: PlatformLaunch(desktop: true),
);
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('真实初始化通道迟注册后恢复，成功前没有轮询', (tester) async {
    final native = NativeChannels()..remainUnavailable();
    final platform = native.createPlatform();
    addTearDown(native.clear);
    final ready = platform.initialize();
    var completed = false;
    final checked = expectLater(
      ready.then((launch) {
        completed = true;
        return launch;
      }),
      completion(
        isA<PlatformLaunch>().having((v) => v.desktop, 'desktop', true),
      ),
    );
    // 原生未注册 handler 时 messenger 返回 null；仍由真实生成 HostApi 产生 channel-error。
    // 不把消息交给测试引擎的 OS 回调，避免其真实时钟与下面的测试虚拟时钟互相等待。
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 200));
    expect(completed, isFalse);
    expect(native.polls, 0);
    native.register(() async => [PlatformLaunch(desktop: true)]);
    await tester.pump(const Duration(milliseconds: 200));
    await checked;
    expect(native.calls, greaterThan(1));
    await tester.pump(const Duration(milliseconds: 40));
    expect(native.polls, greaterThan(0));
    await closePlatform(tester, platform);
  });

  testWidgets('初始化一直缺失在三秒截止，保留原错误且停止所有重试', (tester) async {
    final native = NativeChannels()..remainUnavailable();
    final platform = native.createPlatform();
    addTearDown(native.clear);
    final checked = expectLater(
      platform.initialize(),
      throwsA(
        isA<PlatformException>()
            .having((v) => v.code, 'code', 'channel-error')
            .having(
              (v) => v.message,
              '原始 initialize 通道',
              'Unable to establish connection on channel: "$_initializeChannelName".',
            ),
      ),
    );
    await tester.pump();
    await tester.pump(const Duration(milliseconds: 2999));
    expect(native.calls, greaterThan(1));
    await tester.pump(const Duration(milliseconds: 1));
    await checked;
    final finalCalls = native.calls;
    await tester.pump(const Duration(seconds: 4));
    expect(native.calls, finalCalls);
    expect(native.polls, 0);

    // 截止不会永久缓存失败；后来显式重新初始化可读取已就绪的原生通道。
    native.register(() async => [PlatformLaunch(desktop: true)]);
    final retry = platform.initialize();
    await tester.pump();
    expect((await retry).desktop, isTrue);
    await closePlatform(tester, platform);
  });

  testWidgets('原生初始化一直不回复也受总截止约束，迟到成功不复活轮询', (tester) async {
    final response = Completer<Object?>();
    final native = NativeChannels()..register(() => response.future);
    final platform = native.createPlatform();
    addTearDown(native.clear);
    addTearDown(platform.dispose);
    final checked = expectLater(
      platform.initialize(),
      throwsA(isA<TimeoutException>()),
    );
    await tester.pump();
    await tester.pump(const Duration(seconds: 3));
    await checked;
    expect(native.calls, 1);
    response.complete([PlatformLaunch(desktop: true)]);
    await tester.pump();
    await tester.pump(const Duration(seconds: 1));
    expect(native.calls, 1);
    expect(native.polls, 0);
  });

  for (final failure in <({String name, List<Object?> reply})>[
    (name: '普通原生失败', reply: ['platform-failure', 'storage unavailable', null]),
    (
      name: '其它方法的 channel-error',
      reply: [
        'channel-error',
        'Unable to establish connection on channel: "$_pollChannelName".',
        null,
      ],
    ),
    (
      name: '带有原生细节的 channel-error',
      reply: [
        'channel-error',
        'Unable to establish connection on channel: "$_initializeChannelName".',
        {'source': 'native'},
      ],
    ),
  ]) {
    testWidgets('${failure.name}立即原样传播，不等待或重试', (tester) async {
      final native = NativeChannels()..register(() async => failure.reply);
      final platform = native.createPlatform();
      addTearDown(native.clear);
      addTearDown(platform.dispose);
      final checked = expectLater(
        platform.initialize(),
        throwsA(
          isA<PlatformException>()
              .having((v) => v.code, 'code', failure.reply[0])
              .having((v) => v.message, 'message', failure.reply[1])
              .having((v) => v.details, 'details', failure.reply[2]),
        ),
      );
      await tester.pump();
      await checked;
      await tester.pump(const Duration(seconds: 4));
      expect(native.calls, 1);
      expect(native.polls, 0);
    });
  }

  testWidgets('并发初始化共享同一结果，后续初始化读新参数而不增加轮询', (tester) async {
    final response = Completer<Object?>();
    final native = NativeChannels(suffix: 'concurrent')
      ..register(() => response.future);
    final platform = native.createPlatform();
    addTearDown(native.clear);
    final first = platform.initialize();
    final second = platform.initialize();
    expect(identical(first, second), isTrue);
    await tester.pump();
    expect(native.calls, 1);
    response.complete([PlatformLaunch(desktop: true, gamePid: 100)]);
    await tester.pump();
    expect((await first).gamePid, 100);
    expect((await second).gamePid, 100);
    for (var tick = 0; tick < 3; tick++) {
      await tester.pump(const Duration(milliseconds: 40));
    }
    expect(native.polls, 3);
    native.register(() async => [PlatformLaunch(desktop: true, gamePid: 200)]);
    final next = platform.initialize();
    await tester.pump();
    expect((await next).gamePid, 200);
    await tester.pump(const Duration(milliseconds: 40));
    expect(native.calls, 2);
    expect(native.polls, 4);
    await closePlatform(tester, platform);
  });

  testWidgets('带后缀的初始化通道独立等待自己的 handler', (tester) async {
    final native = NativeChannels(suffix: 'late')..remainUnavailable();
    final platform = native.createPlatform();
    addTearDown(native.clear);
    final ready = platform.initialize();
    await tester.pump();
    native.register(() async => [PlatformLaunch(desktop: false)]);
    await tester.pump(const Duration(milliseconds: 20));
    expect((await ready).desktop, isFalse);
    expect(native.calls, 2);
    await closePlatform(tester, platform);
  });

  testWidgets('dispose立即终止初始化退避，关闭后不重新访问原生通道', (tester) async {
    final native = NativeChannels()..remainUnavailable();
    final platform = native.createPlatform();
    addTearDown(native.clear);
    final checked = expectLater(platform.initialize(), throwsStateError);
    await tester.pump();
    final closing = platform.dispose();
    expect(identical(closing, platform.dispose()), isTrue);
    await tester.pump();
    await closing;
    await checked;
    await expectLater(platform.initialize(), throwsStateError);
    final finalCalls = native.calls;
    await tester.pump(const Duration(seconds: 4));
    expect(native.calls, finalCalls);
    expect(native.polls, 0);
  });

  testWidgets('dispose拒绝尚未返回的初始化，迟到回复不向已关闭stream发事件', (tester) async {
    final response = Completer<Object?>();
    final native = NativeChannels()..register(() => response.future);
    final platform = native.createPlatform();
    addTearDown(native.clear);
    final checked = expectLater(platform.initialize(), throwsStateError);
    await tester.pump();
    final closing = platform.dispose();
    await tester.pump();
    await closing;
    await checked;
    response.complete([PlatformLaunch(desktop: true)]);
    await tester.pump();
    await tester.pump(const Duration(seconds: 4));
    expect(native.calls, 1);
    expect(native.polls, 0);
    expect(tester.takeException(), isNull);
  });

  test('dispose之后迟到的既有轮询回复不会写入已关闭事件流', () async {
    final native = NativeChannels()
      ..register(() async => [PlatformLaunch(desktop: true)]);
    final response = Completer<Object?>();
    native.messenger.setMockDecodedMessageHandler(native.pollChannel, (
      _,
    ) async {
      native.polls++;
      return response.future;
    });
    final platform = native.createPlatform();
    addTearDown(native.clear);
    final events = <PlatformEvent>[];
    final errors = <Object>[];
    final subscription = platform.events.listen(
      events.add,
      onError: errors.add,
    );
    await platform.initialize();
    // 此例包含事件流订阅关闭，使用真实异步循环；只等待本测试自己的40ms轮询。
    // 未完成的第一次读取阻止后续轮询，故等待时间抖动不会增加原生调用数量。
    await Future<void>.delayed(const Duration(milliseconds: 60));
    expect(native.polls, 1);
    await platform.dispose();
    response.complete([input()]);
    await Future<void>.delayed(const Duration(milliseconds: 80));
    expect(native.polls, 1);
    expect(events, isEmpty);
    expect(errors, isEmpty);
    await subscription.cancel();
  });

  test('并发偏好写入合并，profile ACK边界仅在持久化回读后完成', () async {
    final host = MemoryHost();
    final platform = CompanionPlatform(host: host);
    await Future.wait([
      platform.writePreferences({'theme': 'dark'}),
      platform.writePreferences({
        'deviceProfile': {'revision': 5, 'hash': 'hash'},
      }),
    ]);
    final value = await platform.readPreferences();
    expect(value['theme'], 'dark');
    expect(value['deviceProfile'], {'revision': 5, 'hash': 'hash'});
    host.corruptReadback = true;
    await expectLater(
      platform.writePreferences({'theme': 'light'}),
      throwsStateError,
    );
  });
  test('禁止把Token写入普通偏好', () async {
    final host = MemoryHost();
    final platform = CompanionPlatform(host: host);
    await expectLater(
      platform.writePreferences({'token': 'synthetic'}),
      throwsArgumentError,
    );
    expect(host.contents, isNull);
  });
  test('历史迁移拒绝身份、自动化开关及未知字段且不返回部分结果', () {
    expect(
      HistoryMigration.parse(
        '{"schemaVersion":1,"preferences":{"mystia-steward-companion-theme-mode":"dark"}}',
      ),
      {'theme-mode': 'dark'},
    );
    for (final field in [
      'token',
      'client-id',
      'automation-enabled',
      'unrecognized',
    ]) {
      expect(
        () => HistoryMigration.parse(
          '{"schemaVersion":1,"preferences":{"$field":"1"}}',
        ),
        throwsFormatException,
      );
    }
  });
  test('历史迁移规范化布尔与列表，拒绝无效数值且不返回部分结果', () {
    Map<String, String> parse(Map<String, String> preferences) =>
        HistoryMigration.parse(
          jsonEncode({'schemaVersion': 1, 'preferences': preferences}),
        );
    expect(
      parse({
        'always-on-top': '1',
        'service-focus-compact': '0',
        'rare-guest-invitation-levels': '3,1,3',
      }),
      {
        'always-on-top': 'true',
        'service-focus-compact': 'false',
        'rare-guest-invitation-levels': '1,3',
      },
    );
    for (final invalid in [
      {'background-opacity': 'NaN'},
      {'content-opacity': '0'},
      {'font-scale-percent': '101'},
      {'focus-switch-cooldown-ms': '249'},
      {'service-focus-recipe-limit': '21'},
      {'always-on-top': 'yes'},
      {'rare-guest-invitation-levels': '1,-2'},
      {'theme-mode': 'unknown'},
    ]) {
      expect(
        () => parse({'theme-mode': 'dark', ...invalid}),
        throwsFormatException,
      );
    }
  });
  test('手柄失焦重新获得焦点必须回中，确认长按不重复', () {
    final controller = GamepadInputController();
    expect(controller.update(input(buttons: 0x1000), Duration.zero), isEmpty);
    expect(controller.update(input(), Duration.zero), isEmpty);
    expect(
      controller.update(
        input(buttons: 0x1000),
        const Duration(milliseconds: 1),
      ),
      [GamepadAction.confirm],
    );
    expect(
      controller.update(input(buttons: 0x1000), const Duration(seconds: 2)),
      isEmpty,
    );
    controller.update(input(focus: false), const Duration(seconds: 3));
    expect(
      controller.update(input(buttons: 0x1000), const Duration(seconds: 4)),
      isEmpty,
    );
  });
  test('摇杆阈值抖动不产生重复按下，真正回落后才允许新边缘', () {
    final controller = GamepadInputController();
    controller.update(input(), Duration.zero);
    expect(
      controller.update(input(leftX: 0.61), const Duration(milliseconds: 1)),
      [GamepadAction.right],
    );
    expect(
      controller.update(input(leftX: 0.59), const Duration(milliseconds: 2)),
      isEmpty,
    );
    expect(
      controller.update(input(leftX: 0.61), const Duration(milliseconds: 3)),
      isEmpty,
    );
    expect(
      controller.update(input(leftX: 0.2), const Duration(milliseconds: 4)),
      isEmpty,
    );
    expect(
      controller.update(input(leftX: 0.61), const Duration(milliseconds: 5)),
      [GamepadAction.right],
    );
  });
  test('方向长按有初始延迟与重复间隔', () {
    final controller = GamepadInputController();
    controller.update(input(), Duration.zero);
    expect(
      controller.update(input(buttons: 1), const Duration(milliseconds: 1)),
      [GamepadAction.up],
    );
    expect(
      controller.update(input(buttons: 1), const Duration(milliseconds: 400)),
      isEmpty,
    );
    expect(
      controller.update(input(buttons: 1), const Duration(milliseconds: 401)),
      [GamepadAction.up],
    );
  });
}
