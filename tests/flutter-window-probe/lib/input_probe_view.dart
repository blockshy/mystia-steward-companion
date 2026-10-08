import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

class InputProbeUi extends ChangeNotifier {
  String detail = '正在准备独立输入探针';
  int pointerDown = 0;
  int keyDown = 0;
  bool focused = false;

  void show(String message) {
    if (detail == message) return;
    detail = message;
    notifyListeners();
  }

  void pointerReceived() {
    pointerDown++;
    notifyListeners();
  }

  void keyReceived() {
    keyDown++;
    notifyListeners();
  }

  void focusChanged(bool value) {
    focused = value;
    notifyListeners();
  }

  Map<String, Object> toJson() => {
    'pointerDown': pointerDown,
    'keyDown': keyDown,
    'focused': focused,
  };
}

class InputProbeApp extends StatelessWidget {
  const InputProbeApp({super.key, required this.ui, required this.suite});
  final InputProbeUi ui;
  final String suite;

  @override
  Widget build(BuildContext context) => MaterialApp(
    debugShowCheckedModeBanner: false,
    home: Scaffold(
      backgroundColor: const Color(0xff2040c0),
      body: Focus(
        autofocus: true,
        onFocusChange: ui.focusChanged,
        onKeyEvent: (_, event) {
          if (event is KeyDownEvent &&
              event.logicalKey == LogicalKeyboardKey.f24) {
            ui.keyReceived();
            return KeyEventResult.handled;
          }
          return KeyEventResult.ignored;
        },
        child: Listener(
          behavior: HitTestBehavior.opaque,
          onPointerDown: (_) => ui.pointerReceived(),
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
                      Text('mystia-steward-companion · $suite 探针'),
                      const SizedBox(height: 32),
                      Text(ui.detail),
                      const SizedBox(height: 24),
                      Text(
                        'Flutter 点击 ${ui.pointerDown} · F24 ${ui.keyDown} · 焦点 ${ui.focused}',
                      ),
                      const SizedBox(height: 24),
                      const Text('仅用于平台验证；不是产品页面。'),
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
