import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion_window_probe/input_probe_view.dart';

void main() {
  testWidgets(
    'client center delivers pointer and F24, ordinary A is not the focus test',
    (tester) async {
      final ui = InputProbeUi();
      await tester.pumpWidget(InputProbeApp(ui: ui, suite: 'focus'));
      await tester.pumpAndSettle();
      await tester.tapAt(const Offset(400, 300));
      await tester.sendKeyEvent(LogicalKeyboardKey.keyA, platform: 'windows');
      expect(ui.pointerDown, 1);
      expect(ui.keyDown, 0);
      await tester.sendKeyEvent(LogicalKeyboardKey.f24, platform: 'windows');
      await tester.pump();
      expect(ui.keyDown, 1);
      expect(ui.focused, true);
    },
  );
}
