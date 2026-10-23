import 'dart:async';

import 'package:companion_platform/companion_platform.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/live_workbench_model.dart';

/// 仅替换本机平台边界：不初始化原生桥、不持久化文件、不提供 API 凭据，
/// 保留 LiveWorkbenchModel 的实际事件转换、输入序号和本机偏好保存路径。
class _MemoryInputPlatform extends CompanionPlatform {
  final _events = StreamController<PlatformEvent>.broadcast(sync: true);
  final _preferences = <String, Object?>{};
  Future<void>? _closing;
  @override
  Stream<PlatformEvent> get events => _events.stream;
  @override
  Future<PlatformLaunch> initialize() async => PlatformLaunch(desktop: true);
  @override
  Future<Map<String, Object?>> readPreferences() async => Map.of(_preferences);
  @override
  Future<void> writePreferences(Map<String, Object?> fields) async {
    _preferences.addAll(fields);
  }

  @override
  Future<String?> readSecret(String key) async =>
      key == 'client-id' ? 'synthetic-gamepad-device' : null;
  @override
  Future<void> applyWindowPreferences(WindowPreferences preferences) async {}
  @override
  Future<void> dispose() => _closing ??= _events.close();

  void input({
    int buttons = 0,
    double lt = 0,
    double rt = 0,
    bool focused = true,
    bool connected = true,
  }) {
    _events.add(
      PlatformEvent(
        'input',
        PlatformInput(
          focused: focused,
          connected: connected,
          buttons: buttons,
          leftX: 0,
          leftY: 0,
          leftTrigger: lt,
          rightTrigger: rt,
          activationRevision: 0,
          launch: PlatformLaunch(desktop: true),
        ),
      ),
    );
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('生产模型保留肩键主导航，扳机改发子页事件且失焦只撤回一次输入模式', () async {
    final platform = _MemoryInputPlatform();
    final model = LiveWorkbenchModel(platform: platform);
    addTearDown(() async {
      model.dispose();
      await platform.dispose();
    });
    await model.initialize();
    final actions = <String>[];
    var sequence = model.inputSequence;
    model.addListener(() {
      if (sequence != model.inputSequence) {
        sequence = model.inputSequence;
        actions.add(model.inputAction);
      }
    });
    platform.input();
    platform.input(buttons: 0x0100);
    platform.input();
    platform.input(buttons: 0x0200);
    platform.input();
    platform.input(lt: 1);
    platform.input(lt: 1);
    platform.input();
    platform.input(rt: 1);
    expect(actions, ['previousTab', 'nextTab', 'previousSubTab', 'nextSubTab']);
    platform.input(focused: false);
    platform.input(focused: false);
    expect(actions.last, 'gamepadInactive');
    expect(actions.where((value) => value == 'gamepadInactive'), hasLength(1));
    final count = actions.length;
    platform.input(rt: 1);
    expect(actions, hasLength(count), reason: '重新获得焦点时持有扳机不能跨窗触发。');
    platform.input();
    platform.input(rt: 1);
    expect(actions.last, 'nextSubTab');
    expect(actions, hasLength(count + 1));
    expect(actions, isNot(anyOf(contains('pageUp'), contains('pageDown'))));
    expect(model.error, isEmpty);
  });

  test('关闭手柄导航与断开设备撤回提示，重新启用仍要求回中', () async {
    final platform = _MemoryInputPlatform();
    final model = LiveWorkbenchModel(platform: platform);
    addTearDown(() async {
      model.dispose();
      await platform.dispose();
    });
    await model.initialize();
    platform.input();
    platform.input(rt: 1);
    final started = model.inputSequence;
    await model.updateLocal({'gamepadNavigation': false});
    platform.input(rt: 1);
    expect(model.inputAction, 'gamepadInactive');
    expect(model.inputSequence, started + 1);
    platform.input(rt: 1);
    expect(model.inputSequence, started + 1);
    await model.updateLocal({'gamepadNavigation': true});
    platform.input(rt: 1);
    expect(model.inputSequence, started + 1);
    platform.input();
    platform.input(rt: 1);
    expect(model.inputAction, 'nextSubTab');
    expect(model.inputSequence, started + 2);
    platform.input(connected: false);
    expect(model.inputAction, 'gamepadInactive');
    expect(model.inputSequence, started + 3);
    platform.input(connected: false);
    expect(model.inputSequence, started + 3);
  });
}
