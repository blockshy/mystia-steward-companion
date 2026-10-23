import 'package:companion_platform/companion_platform.dart';
import 'package:flutter_test/flutter_test.dart';

/// 只输入合成按钮位，不枚举或操作真实手柄；保留原生 XInput 按钮码协议。
PlatformInput _input(
  int buttons, {
  bool focused = true,
  bool connected = true,
  double leftTrigger = 0,
  double rightTrigger = 0,
}) => PlatformInput(
  focused: focused,
  connected: connected,
  buttons: buttons,
  leftX: 0,
  leftY: 0,
  leftTrigger: leftTrigger,
  rightTrigger: rightTrigger,
  activationRevision: 0,
  launch: PlatformLaunch(desktop: true),
);

void main() {
  test('Y 不再生成专注视图动作，组合按下只保留有效确认动作', () {
    final input = GamepadInputController();
    input.update(_input(0), Duration.zero);
    expect(
      input.update(_input(0x8000), const Duration(milliseconds: 10)),
      isEmpty,
    );
    expect(input.update(_input(0x8000), const Duration(seconds: 2)), isEmpty);
    expect(input.update(_input(0x9000), const Duration(seconds: 3)), [
      GamepadAction.confirm,
    ]);
    expect(
      GamepadAction.values.map((action) => action.name),
      isNot(contains('compact')),
    );
  });

  test('移除 Y 映射后左右肩键仍仅切换相邻目的地', () {
    final input = GamepadInputController();
    input.update(_input(0), Duration.zero);
    expect(input.update(_input(0x0100), const Duration(milliseconds: 10)), [
      GamepadAction.previousTab,
    ]);
    input.update(_input(0), const Duration(milliseconds: 20));
    expect(input.update(_input(0x0200), const Duration(milliseconds: 30)), [
      GamepadAction.nextTab,
    ]);
  });

  test('失焦后持有 Y 仍必须回中，不能绕过新窗口的确认保护', () {
    final input = GamepadInputController();
    input.update(_input(0), Duration.zero);
    input.update(
      _input(0x8000, focused: false),
      const Duration(milliseconds: 10),
    );
    expect(
      input.update(_input(0x9000), const Duration(milliseconds: 20)),
      isEmpty,
    );
    input.update(_input(0), const Duration(milliseconds: 30));
    expect(input.update(_input(0x1000), const Duration(milliseconds: 40)), [
      GamepadAction.confirm,
    ]);
  });

  for (final left in [true, false]) {
    test('${left ? 'LT' : 'RT'} 单次切横向页签，长按和阈值抖动不重复，回中后可再次触发', () {
      final input = GamepadInputController();
      PlatformInput trigger(double strength) => _input(
        0,
        leftTrigger: left ? strength : 0,
        rightTrigger: left ? 0 : strength,
      );
      final expected = left
          ? GamepadAction.previousSubTab
          : GamepadAction.nextSubTab;
      input.update(_input(0), Duration.zero);
      expect(input.update(trigger(.61), const Duration(milliseconds: 10)), [
        expected,
      ]);
      expect(
        input.update(trigger(.59), const Duration(milliseconds: 20)),
        isEmpty,
      );
      expect(input.update(trigger(.61), const Duration(seconds: 1)), isEmpty);
      expect(input.update(trigger(1), const Duration(seconds: 10)), isEmpty);
      // 降到 .30 还没有回中，不能利用中间区间形成第二次选择。
      expect(input.update(trigger(.30), const Duration(seconds: 11)), isEmpty);
      expect(input.update(trigger(.7), const Duration(seconds: 12)), isEmpty);
      expect(input.update(trigger(.2), const Duration(seconds: 13)), isEmpty);
      expect(input.update(trigger(.7), const Duration(seconds: 14)), [
        expected,
      ]);
      expect(
        GamepadAction.values.map((value) => value.name),
        isNot(anyOf(contains('scrollUp'), contains('scrollDown'))),
      );
    });
  }

  test('左右扳机同时按下不来回切页，松开单边不补发旧请求', () {
    final input = GamepadInputController();
    input.update(_input(0), Duration.zero);
    expect(
      input.update(
        _input(0, leftTrigger: 1, rightTrigger: 1),
        const Duration(milliseconds: 1),
      ),
      isEmpty,
    );
    expect(
      input.update(_input(0, rightTrigger: 1), const Duration(milliseconds: 2)),
      isEmpty,
    );
    input.update(_input(0), const Duration(milliseconds: 3));
    expect(
      input.update(_input(0, rightTrigger: 1), const Duration(milliseconds: 4)),
      [GamepadAction.nextSubTab],
    );
  });

  test('扳机首次连接、失焦、拔插和关闭再启用都必须释放回中', () {
    final input = GamepadInputController();
    expect(input.update(_input(0, rightTrigger: 1), Duration.zero), isEmpty);
    input.update(_input(0), const Duration(milliseconds: 1));
    expect(
      input.update(_input(0, rightTrigger: 1), const Duration(milliseconds: 2)),
      [GamepadAction.nextSubTab],
    );
    input.update(_input(0, focused: false), const Duration(milliseconds: 3));
    expect(
      input.update(_input(0, rightTrigger: 1), const Duration(milliseconds: 4)),
      isEmpty,
    );
    input.update(_input(0), const Duration(milliseconds: 5));
    input.update(_input(0, connected: false), const Duration(milliseconds: 6));
    expect(
      input.update(_input(0, rightTrigger: 1), const Duration(milliseconds: 7)),
      isEmpty,
    );
    input.update(_input(0), const Duration(milliseconds: 8));
    input.enabled = false;
    input.update(_input(0), const Duration(milliseconds: 9));
    input.enabled = true;
    expect(
      input.update(
        _input(0, rightTrigger: 1),
        const Duration(milliseconds: 10),
      ),
      isEmpty,
    );
    input.update(_input(0), const Duration(milliseconds: 11));
    expect(
      input.update(
        _input(0, rightTrigger: 1),
        const Duration(milliseconds: 12),
      ),
      [GamepadAction.nextSubTab],
    );
  });
}
