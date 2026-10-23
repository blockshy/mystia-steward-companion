import 'platform.g.dart';

enum GamepadAction {
  up,
  down,
  left,
  right,
  confirm,
  back,
  favorite,
  previousTab,
  nextTab,
  previousSubTab,
  nextSubTab,
}

/// 纯输入解释器。失焦、拔插和首次连接均要求回中，避免跨窗口长按变成新操作。
class GamepadInputController {
  bool enabled = true;
  bool _neutralRequired = true;
  bool _active = false;
  final Map<GamepadAction, Duration> _held = {};
  final Set<GamepadAction> _analogHeld = {};
  List<GamepadAction> update(PlatformInput input, Duration now) {
    final active = input.focused && input.connected && enabled;
    if (!active || !_active) {
      _held.clear();
      _analogHeld.clear();
      _neutralRequired = true;
    }
    _active = active;
    if (!active) return const [];
    final down = <GamepadAction>{};
    final buttons = input.buttons;
    void button(int mask, GamepadAction action) {
      if (buttons & mask != 0) down.add(action);
    }

    button(0x0001, GamepadAction.up);
    button(0x0002, GamepadAction.down);
    button(0x0004, GamepadAction.left);
    button(0x0008, GamepadAction.right);
    button(0x1000, GamepadAction.confirm);
    button(0x2000, GamepadAction.back);
    button(0x4000, GamepadAction.favorite);
    // Y 不再绑定已移除的专注视图；仍参与首次回中判断，防止持键跨焦点触发其它动作。
    button(0x0100, GamepadAction.previousTab);
    button(0x0200, GamepadAction.nextTab);
    // 按下阈值与释放阈值分离，摇杆/扳机在边界抖动时不会反复形成新的按下边缘。
    void analog(
      double strength,
      GamepadAction action, {
      double releaseThreshold = 0.35,
    }) {
      final threshold = _analogHeld.contains(action) ? releaseThreshold : 0.6;
      if (strength > threshold) {
        _analogHeld.add(action);
        down.add(action);
      } else {
        _analogHeld.remove(action);
      }
    }

    analog(-input.leftX, GamepadAction.left);
    analog(input.leftX, GamepadAction.right);
    analog(-input.leftY, GamepadAction.down);
    analog(input.leftY, GamepadAction.up);
    // 扳机只切换当前横向页签，不再模拟滚动。每次必须释放至回中区后才能
    // 形成新的按下边缘；与可重复的方向键分离，长按不会连续跳过多个页面。
    analog(
      input.leftTrigger,
      GamepadAction.previousSubTab,
      releaseThreshold: 0.25,
    );
    analog(
      input.rightTrigger,
      GamepadAction.nextSubTab,
      releaseThreshold: 0.25,
    );
    if (_neutralRequired) {
      if (buttons == 0 &&
          input.leftX.abs() < 0.25 &&
          input.leftY.abs() < 0.25 &&
          input.leftTrigger < 0.25 &&
          input.rightTrigger < 0.25)
        _neutralRequired = false;
      return const [];
    }
    _held.removeWhere((key, _) => !down.contains(key));
    final events = <GamepadAction>[];
    final opposedTriggers =
        down.contains(GamepadAction.previousSubTab) &&
        down.contains(GamepadAction.nextSubTab);
    for (final action in down) {
      final due = _held[action];
      if (opposedTriggers &&
          (action == GamepadAction.previousSubTab ||
              action == GamepadAction.nextSubTab)) {
        // 同时扣下左右扳机不产生两个相反的导航请求；消费双方按下边缘，
        // 放开单边也不补发旧请求，必须再次回中后重新按下。
        _held.putIfAbsent(action, () => now);
        continue;
      }
      final repeat = {
        GamepadAction.up,
        GamepadAction.down,
        GamepadAction.left,
        GamepadAction.right,
      }.contains(action);
      if (due == null || (repeat && now >= due)) {
        events.add(action);
        _held[action] = now + Duration(milliseconds: due == null ? 400 : 110);
      }
    }
    return events;
  }
}
