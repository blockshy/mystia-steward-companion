import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

class ControlUi extends ChangeNotifier {
  String detail = '正在准备真实 Mod F8/RS 控制链';
  bool focused = false;
  int f8Down = 0, f24Down = 0, pointerDown = 0;
  void Function(int count)? onF8;
  void show(String message) {
    detail = message;
    notifyListeners();
  }

  void focusChanged(bool value) {
    focused = value;
    notifyListeners();
  }

  void clicked() {
    pointerDown++;
    notifyListeners();
  }

  KeyEventResult key(KeyEvent event) {
    if (event is! KeyDownEvent || event.synthesized) {
      return KeyEventResult.ignored;
    }
    if (event.logicalKey == LogicalKeyboardKey.f8) {
      f8Down++;
      notifyListeners();
      onF8?.call(f8Down);
      return KeyEventResult.handled;
    }
    if (event.logicalKey == LogicalKeyboardKey.f24) {
      f24Down++;
      notifyListeners();
      return KeyEventResult.handled;
    }
    return KeyEventResult.ignored;
  }

  Map<String, Object> toJson() => {
    'focused': focused,
    'f8Down': f8Down,
    'f24Down': f24Down,
    'pointerDown': pointerDown,
  };
}

class ControlProbeApp extends StatelessWidget {
  const ControlProbeApp({super.key, required this.ui});
  final ControlUi ui;
  @override
  Widget build(BuildContext context) => MaterialApp(
    debugShowCheckedModeBanner: false,
    home: Scaffold(
      backgroundColor: const Color(0xff2040c0),
      body: Focus(
        autofocus: true,
        onFocusChange: ui.focusChanged,
        onKeyEvent: (_, event) => ui.key(event),
        child: Listener(
          behavior: HitTestBehavior.opaque,
          onPointerDown: (_) => ui.clicked(),
          child: SizedBox.expand(
            child: Padding(
              padding: const EdgeInsets.all(24),
              child: ListenableBuilder(
                listenable: ui,
                builder: (_, _) => DefaultTextStyle(
                  style: const TextStyle(color: Colors.white, fontSize: 20),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      const Text('mystia-steward-companion · F8 / RS 控制探针'),
                      const SizedBox(height: 28),
                      Text(ui.detail),
                      const SizedBox(height: 24),
                      Text(
                        'Flutter F8 ${ui.f8Down} · F24 ${ui.f24Down} · 点击 ${ui.pointerDown}',
                      ),
                      const SizedBox(height: 24),
                      const Text('隔离游戏副本；请勿操作游戏菜单。RS 指向下按右摇杆。'),
                    ],
                  ),
                ),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}
