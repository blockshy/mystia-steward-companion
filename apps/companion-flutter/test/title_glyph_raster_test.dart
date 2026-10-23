import 'dart:convert';
import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/ui/steward_style.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

/// 独立字形诊断：比较同一个字出现在段首、段中、段尾时的实际像素。
/// 本测试不预设末字一定相同或一定异常；差异是待解释的证据。重复绘制必须一致、
/// 不同汉字必须不同，防止全透明图、错误裁剪或无效比较器产生“没有差异”的假结论。
/// 输出只来自离线 Canvas，不读取/截取桌面，也不能代替 Windows GPU 实窗验证。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  final regularPath =
      Platform.environment['MYSTIA_UI_FONT'] ??
      (Platform.isWindows ? 'C:/Windows/Fonts/msyh.ttc' : null);
  final boldPath =
      Platform.environment['MYSTIA_UI_BOLD_FONT'] ??
      (regularPath != null &&
              RegExp(
                r'(^|[\\/])msyh\.ttc$',
                caseSensitive: false,
              ).hasMatch(regularPath)
          ? '${File(regularPath).parent.path}${Platform.pathSeparator}msyhbd.ttc'
          : null);
  final fontsSpecified = regularPath != null && boldPath != null;

  setUpAll(() async {
    if (regularPath == null || boldPath == null) return;
    // 两个真实字重注册到同一字体族，由字体自身元数据选择 w700；不把常规体
    // 合成加粗的结果误称为 Windows 的 msyhbd.ttc。明确指定但不存在时直接失败。
    final regular = await File(regularPath).readAsBytes();
    final bold = await File(boldPath).readAsBytes();
    await (FontLoader('Microsoft YaHei')
          ..addFont(Future.value(ByteData.sublistView(regular)))
          ..addFont(Future.value(ByteData.sublistView(bold))))
        .load();
  });

  testWidgets(
    '真实粗体15px同字在开头中间末尾的栅格对照并保存证据',
    (tester) async {
      final output = Directory(
        Platform.environment['MYSTIA_GLYPH_RASTER_OUTPUT'] ??
            '../../temp/flutter-final-polish-20261018/title-glyph-raster',
      ).absolute;
      final report = await tester.runAsync(() async {
        await output.create(recursive: true);
        final theme = stewardTheme(dark: false, touch: false);
        final style = theme.typography.body.sm
            .merge(StewardText.sectionTitle)
            .copyWith(
              color: theme.colors.primary,
              // 本专项保留原雅黑问题的可重复基线，不随产品优先字体改变而悄悄
              // 改成另一字体的诊断。Noto 的实际选择另由 steward_font_test 验证。
              fontFamily: 'Microsoft YaHei',
              fontFamilyFallback: const [],
            );
        // 与默认不透明 Section 的三层平面底色相同；所有位置使用完全相同背景，
        // 因此背景合成不会被误当作末字独有的变化。边框在目标字裁剪范围之外。
        final background = ui.Color.alphaBlend(
          theme.colors.primary.withValues(alpha: .055),
          ui.Color.alphaBlend(
            theme.colors.mutedForeground.withValues(alpha: .025),
            theme.colors.background,
          ),
        );
        expect(style.fontFamily, 'Microsoft YaHei');
        expect(style.fontWeight, FontWeight.w700);
        expect(style.fontSize, 15);
        expect(style.height, 1.35);
        expect(style.letterSpacing ?? 0, 0);

        final comparisons = <Map<String, Object?>>[];
        for (final scale in [1.0, 1.5]) {
          for (final dpr in [1.0, 1.25, 1.5]) {
            for (final phase in [0.0, .5]) {
              final groups = <String, List<_GlyphRaster>>{};
              try {
                for (final definition in const [
                  (name: 'sort', glyph: '序', texts: ['序评分排', '评序分排', '评分排序']),
                  (
                    name: 'devices',
                    glyph: '置',
                    texts: ['置设备与共享配', '设备与置共享配', '设备与共享配置'],
                  ),
                ]) {
                  final samples = <_GlyphRaster>[];
                  groups[definition.name] = samples;
                  for (final text in definition.texts) {
                    samples.add(
                      await _rasterize(
                        text: text,
                        glyph: definition.glyph,
                        style: style,
                        background: background,
                        scale: scale,
                        dpr: dpr,
                        phase: phase,
                      ),
                    );
                  }
                  final end = samples.last;
                  // 栅格对照必须保持同字体、同字号、同物理像素相位。仅将已经
                  // 排版的整段平移到目标字的共同原点，不单独重塑字形、不缩放裁图。
                  for (final sample in samples) {
                    expect(sample.width, end.width);
                    expect(sample.height, end.height);
                    expect(
                      sample.selection.width,
                      closeTo(end.selection.width, .0001),
                    );
                    expect(sample.baseline, closeTo(end.baseline, .0001));
                    expect(
                      sample.inkPixels,
                      greaterThan(0),
                      reason: '${sample.text} 的目标字不能是空白裁图。',
                    );
                  }
                  final replay = await _rasterize(
                    text: end.text,
                    glyph: definition.glyph,
                    style: style,
                    background: background,
                    scale: scale,
                    dpr: dpr,
                    phase: phase,
                  );
                  try {
                    expect(
                      _difference(end, replay)['differentPixels'],
                      0,
                      reason: '同段同相位重画应确定一致，才能解释位置比较。',
                    );
                  } finally {
                    replay.dispose();
                  }
                  final name =
                      '${definition.name}-'
                      's${(scale * 100).round()}-'
                      'd${(dpr * 100).round()}-'
                      'p${(phase * 100).round()}';
                  final sheet = await _contactSheet(samples, background);
                  try {
                    await _writePng(File('${output.path}/$name.png'), sheet);
                  } finally {
                    sheet.dispose();
                  }
                  comparisons.add({
                    'glyph': definition.glyph,
                    'textScale': scale,
                    'devicePixelRatio': dpr,
                    'physicalPixelPhaseXY': phase,
                    'fontSize': style.fontSize,
                    'fontWeight': style.fontWeight?.value,
                    'png': '$name.png',
                    'pngRows': ['start', 'middle', 'end'],
                    'pngColumns': ['original paragraph', '8x nearest glyph'],
                    'samples': [for (final sample in samples) sample.metadata],
                    'endVsStart': _difference(end, samples.first),
                    'endVsMiddle': _difference(end, samples[1]),
                  });
                }
                // 负控制使用同字号/字重/相位的两个不同汉字。不能仅要求非空，
                // 否则裁到统一背景、统一边框或同一份缓存也可能误判为一致。
                expect(
                  _difference(
                    groups['sort']!.last,
                    groups['devices']!.last,
                  )['differentPixels'],
                  greaterThan(0),
                  reason: '序与置必须产生可检测的不同像素。',
                );
              } finally {
                for (final samples in groups.values) {
                  for (final sample in samples) {
                    sample.dispose();
                  }
                }
              }
            }
          }
        }
        final result = <String, Object?>{
          'purpose': '离线末字位置诊断，不预设位置差异为零。',
          'regularFont': File(regularPath!).absolute.path,
          'boldFont': File(boldPath!).absolute.path,
          'foregroundArgb': style.color!.toARGB32(),
          'backgroundArgb': background.toARGB32(),
          'crop':
              '按目标字选择框裁剪；目标框原点固定到物理像素4+phase，'
              '不含邻字，不做平滑缩放，原段落保留在PNG左列。',
          'limitation':
              'flutter_tester离线栅格不等同Windows桌面GPU合成；'
              '选择框外的字体overhang不纳入像素比较。',
          'comparisons': comparisons,
        };
        await File('${output.path}/summary.json')
            .writeAsString(const JsonEncoder.withIndent('  ').convert(result));
        return comparisons;
      });
      expect(report, hasLength(24));
      final differing = report!.where(
        (row) =>
            (row['endVsStart'] as Map<String, Object?>)['differentPixels'] !=
                0 ||
            (row['endVsMiddle'] as Map<String, Object?>)['differentPixels'] !=
                0,
      );
      // 结果写日志而不把尚未确认的引擎行为固化成产品断言；父任务读取PNG和JSON
      // 后再决定是否存在真实回归。测试本身仍严格验证字体、非空及正反控制。
      debugPrint(
        'title-glyph-raster: ${report.length} 组，'
        '${differing.length} 组存在位置像素差异；证据 ${output.path}',
      );
    },
    // 非 Windows 节点必须显式提供两个真实字体；跳过不代表字形验证通过。
    skip: !fontsSpecified,
  );
}

/// 同时保留未缩放字形、原段落及实际RGBA，便于检查差异是否来自采样器。
class _GlyphRaster {
  const _GlyphRaster({
    required this.text,
    required this.glyph,
    required this.image,
    required this.paragraph,
    required this.rgba,
    required this.selection,
    required this.baseline,
    required this.inkPixels,
  });
  final String text, glyph;
  final ui.Image image, paragraph;
  final Uint8List rgba;
  final Rect selection;
  final double baseline;
  final int inkPixels;
  int get width => image.width;
  int get height => image.height;
  Map<String, Object?> get metadata => {
    'text': text,
    'glyph': glyph,
    'utf16Offset': text.indexOf(glyph),
    'selectionLogical': [
      selection.left,
      selection.top,
      selection.right,
      selection.bottom,
    ],
    'baselineLogical': baseline,
    'cropWidth': width,
    'cropHeight': height,
    'inkPixels': inkPixels,
  };
  void dispose() {
    image.dispose();
    paragraph.dispose();
  }
}

Future<_GlyphRaster> _rasterize({
  required String text,
  required String glyph,
  required TextStyle style,
  required Color background,
  required double scale,
  required double dpr,
  required double phase,
}) async {
  final painter = TextPainter(
    text: TextSpan(text: text, style: style),
    textDirection: TextDirection.ltr,
    textScaler: TextScaler.linear(scale),
    locale: const Locale('zh', 'CN'),
  );
  ui.Image? crop;
  ui.Image? paragraph;
  try {
    // Section 的 Text 在 Expanded 内靠左排版，实际框体大于短标题。固定充分宽的
    // 段落约束，避免将“最后一字正好顶着最小段落边界”人为加入实验。
    painter.layout(minWidth: 400, maxWidth: 400);
    final index = text.indexOf(glyph);
    expect(index, greaterThanOrEqualTo(0));
    expect(text.indexOf(glyph, index + glyph.length), -1);
    final boxes = painter.getBoxesForSelection(
      TextSelection(baseOffset: index, extentOffset: index + glyph.length),
    );
    expect(boxes, hasLength(1));
    final box = boxes.single.toRect();
    final width = (box.width * dpr).ceil() + 10;
    final height = (box.height * dpr).ceil() + 10;
    final recorder = ui.PictureRecorder();
    final canvas = Canvas(recorder)..drawColor(background, BlendMode.src);
    final origin = 4 + phase;
    canvas.clipRect(
      Rect.fromLTWH(origin, origin, box.width * dpr, box.height * dpr),
      doAntiAlias: false,
    );
    canvas.scale(dpr);
    painter.paint(
      canvas,
      Offset(origin / dpr - box.left, origin / dpr - box.top),
    );
    final picture = recorder.endRecording();
    try {
      crop = await picture.toImage(width, height);
    } finally {
      picture.dispose();
    }

    final fullRecorder = ui.PictureRecorder();
    final fullCanvas = Canvas(fullRecorder)
      ..drawColor(background, BlendMode.src);
    fullCanvas.scale(dpr);
    painter.paint(fullCanvas, Offset(origin / dpr, origin / dpr));
    final fullPicture = fullRecorder.endRecording();
    try {
      paragraph = await fullPicture.toImage(
        (painter.maxIntrinsicWidth * dpr).ceil() + 12,
        (painter.height * dpr).ceil() + 12,
      );
    } finally {
      fullPicture.dispose();
    }
    final data = await crop.toByteData(format: ui.ImageByteFormat.rawRgba);
    expect(data, isNotNull);
    final rgba = Uint8List.fromList(
      data!.buffer.asUint8List(data.offsetInBytes, data.lengthInBytes),
    );
    final argb = background.toARGB32();
    final expected = [(argb >> 16) & 255, (argb >> 8) & 255, argb & 255, 255];
    var ink = 0;
    for (var offset = 0; offset < rgba.length; offset += 4) {
      if (List.generate(
        4,
        (channel) => rgba[offset + channel],
      ).indexed.any((entry) => entry.$2 != expected[entry.$1])) {
        ink++;
      }
    }
    return _GlyphRaster(
      text: text,
      glyph: glyph,
      image: crop,
      paragraph: paragraph,
      rgba: rgba,
      selection: box,
      baseline: painter.computeLineMetrics().single.baseline,
      inkPixels: ink,
    );
  } catch (_) {
    crop?.dispose();
    paragraph?.dispose();
    rethrow;
  } finally {
    painter.dispose();
  }
}

/// 不做自动位移、容差匹配或重新缩放；完全同像素相位下逐RGBA通道比较。
Map<String, Object?> _difference(_GlyphRaster left, _GlyphRaster right) {
  expect(left.width, right.width);
  expect(left.height, right.height);
  var pixels = 0, total = 0, maximum = 0;
  for (var offset = 0; offset < left.rgba.length; offset += 4) {
    var different = false;
    for (var channel = 0; channel < 4; channel++) {
      final delta = (left.rgba[offset + channel] - right.rgba[offset + channel])
          .abs();
      total += delta;
      if (delta > maximum) maximum = delta;
      different = different || delta != 0;
    }
    if (different) pixels++;
  }
  return {
    'equal': pixels == 0,
    'differentPixels': pixels,
    'maximumChannelDelta': maximum,
    'totalChannelDelta': total,
  };
}

/// PNG每行依次为段首、段中、段尾；左列完整原段落，右列仅用最近邻放大8倍，
/// 让人工能数像素。放大图绝不参与数值比较，也不宣称代表原生实际显示尺寸。
Future<ui.Image> _contactSheet(
  List<_GlyphRaster> samples,
  Color background,
) async {
  final leftWidth = samples
      .map((sample) => sample.paragraph.width)
      .reduce((a, b) => a > b ? a : b);
  final cellHeight = samples.first.height * 8 + 8;
  final recorder = ui.PictureRecorder();
  final canvas = Canvas(recorder)..drawColor(background, BlendMode.src);
  final paint = Paint()..filterQuality = FilterQuality.none;
  for (var index = 0; index < samples.length; index++) {
    final sample = samples[index];
    final top = (index * cellHeight).toDouble();
    canvas.drawImage(sample.paragraph, Offset(0, top), paint);
    canvas.drawImageRect(
      sample.image,
      Rect.fromLTWH(0, 0, sample.width.toDouble(), sample.height.toDouble()),
      Rect.fromLTWH(
        leftWidth + 12.0,
        top,
        sample.width * 8.0,
        sample.height * 8.0,
      ),
      paint,
    );
  }
  final picture = recorder.endRecording();
  try {
    return await picture.toImage(
      leftWidth + 12 + samples.first.width * 8,
      samples.length * cellHeight,
    );
  } finally {
    picture.dispose();
  }
}

Future<void> _writePng(File file, ui.Image image) async {
  final data = await image.toByteData(format: ui.ImageByteFormat.png);
  expect(data, isNotNull);
  await file.writeAsBytes(
    data!.buffer.asUint8List(data.offsetInBytes, data.lengthInBytes),
  );
}
