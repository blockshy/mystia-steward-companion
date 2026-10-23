import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/ui/steward_style.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/steward_test_fonts.dart';

/// 不只检查 fontFamily 字符串：真实加载新字体，并将主题栅格与明确指定同一字体的
/// 栅格逐字节比较，再用原雅黑作差异对照，防止系统回退让“新字体测试”实际仍测旧字形。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final notoPath = preferredStewardTestFontPath;
  final yaheiPath =
      Platform.environment['MYSTIA_UI_BOLD_FONT'] ??
      (Platform.isWindows ? 'C:/Windows/Fonts/msyhbd.ttc' : null);

  setUpAll(() async {
    await loadPreferredStewardTestFont();
    if (yaheiPath != null) {
      final bytes = await File(yaheiPath).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  test('全局字体改变不改变正文、标题与平台控件的字号规范', () {
    for (final dark in [false, true]) {
      for (final touch in [false, true]) {
        final theme = stewardTheme(dark: dark, touch: touch);
        final body = theme.typography.body.sm;
        expect(body.fontFamily, stewardFontFamily);
        expect(body.fontFamilyFallback, stewardFontFallback);
        expect(body.fontSize, 13);
        expect(body.height, 1.45);
        final title = body.merge(StewardText.sectionTitle);
        expect(title.fontFamily, stewardFontFamily);
        expect(title.fontSize, 15);
        expect(title.height, 1.35);
        expect(title.fontWeight, FontWeight.w700);
      }
    }
  });

  for (final scale in [1.0, 1.5]) {
    testWidgets('真实Noto标题栅格由主题选中，保持同字号中文advance $scale', (tester) async {
      await tester.runAsync(() async {
        final theme = stewardTheme(dark: true, touch: false);
        final style = theme.typography.body.sm
            .merge(StewardText.sectionTitle)
            .copyWith(color: theme.colors.primary);
        for (final text in ['设备与共享配置', '评分排序']) {
          final actual = await _raster(text, style, scale);
          final noto = await _raster(
            text,
            style.copyWith(
              fontFamily: stewardFontFamily,
              fontFamilyFallback: const [],
            ),
            scale,
          );
          final original = await _raster(
            text,
            style.copyWith(
              fontFamily: 'Microsoft YaHei',
              fontFamilyFallback: const [],
            ),
            scale,
          );
          expect(actual.pixels, orderedEquals(noto.pixels));
          expect(actual.pixels, isNot(orderedEquals(original.pixels)));
          expect(actual.inkPixels, greaterThan(100));
          // 比较全角标题的advance，不要求不同汉字轮廓等高；字面留白本属字体设计。
          expect(actual.width, closeTo(original.width, .05));
        }
      });
    }, skip: notoPath == null || yaheiPath == null);
  }
}

Future<({Uint8List pixels, double width, int inkPixels})> _raster(
  String text,
  TextStyle style,
  double scale,
) async {
  final painter = TextPainter(
    text: TextSpan(text: text, style: style),
    textDirection: TextDirection.ltr,
    textScaler: TextScaler.linear(scale),
    locale: const Locale('zh', 'CN'),
  )..layout();
  final recorder = ui.PictureRecorder();
  final canvas = Canvas(recorder)..scale(1.25);
  painter.paint(canvas, const Offset(8, 8));
  final picture = recorder.endRecording();
  final image = await picture.toImage(400, 80);
  try {
    final bytes = (await image.toByteData(format: ui.ImageByteFormat.rawRgba))!;
    final pixels = Uint8List.fromList(bytes.buffer.asUint8List());
    var ink = 0;
    for (var index = 3; index < pixels.length; index += 4) {
      if (pixels[index] > 0) ink++;
    }
    return (pixels: pixels, width: painter.width, inkPixels: ink);
  } finally {
    image.dispose();
    picture.dispose();
    painter.dispose();
  }
}
