import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/settings.dart';
import 'package:mystia_steward_companion/ui/steward_style.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/page_activity_assertions.dart';
import 'support/text_geometry.dart';

/// 设置字段帮助继承同一中文字体/缩放，错误完整发布到页头而不再挤占表单。
/// 使用真实设置表单和可滚动内容测量，避免仅比较常量而漏掉父级裁切与窄屏溢出。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  for (final sample in [
    (262.0, 1.5, true, false),
    (342.0, 1.5, false, true),
    (616.0, 1.0, true, true),
    (1184.0, 1.0, true, false),
  ]) {
    testWidgets('字段帮助保持层级可读，连接错误完整发布且不占正文 $sample', (tester) async {
      tester.view.devicePixelRatio = 1;
      tester.view.physicalSize = Size(sample.$1, 1100);
      addTearDown(tester.view.resetDevicePixelRatio);
      addTearDown(tester.view.resetPhysicalSize);
      final model = FixtureModel(desktop: sample.$3)
        ..connected = false
        ..error = '合成连接失败：请检查地址后重试。';
      addTearDown(model.dispose);
      final theme = stewardTheme(dark: sample.$4, touch: !model.desktop);
      await tester.pumpWidget(
        material.MaterialApp(
          theme: theme.toApproximateMaterialTheme(),
          builder: (context, child) => FTheme(
            data: theme,
            child: MediaQuery(
              data: MediaQuery.of(context)
                  .copyWith(textScaler: TextScaler.linear(sample.$2)),
              child: child!,
            ),
          ),
          home: SettingsPage(model),
        ),
      );
      await tester.pumpAndSettle();
      const explanation = '同一地址留空 Token 会沿用已保存凭据；更换地址时请填写该电脑的 Token。';
      final note = find.text(explanation);
      await tester.ensureVisible(note);
      await tester.pumpAndSettle();
      final text = tester.widget<Text>(note);
      expect(text.style?.fontSize, StewardText.caption.fontSize);
      expect(text.style?.height, StewardText.caption.height);
      expect(text.maxLines, isNull);
      final paragraph = renderedParagraph(tester, note);
      expect(paragraph.text.toPlainText(), explanation);
      expect(paragraph.textScaler.scale(12), closeTo(12 * sample.$2, .01));
      expect(tester.getRect(note).right, lessThanOrEqualTo(sample.$1));
      if (sample.$1 <= 342) {
        expect(
          tester.getSize(note).height,
          greaterThan(textFirstLine(tester, note).height),
        );
      }
      expect(note.hitTestable(), findsOneWidget, reason: '说明仍在真实滚动区域中可读');

      expectReportedFailure(tester, model.error);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }
}
