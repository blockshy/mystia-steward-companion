import 'dart:io';
import 'dart:ui' as ui;

import 'package:flutter/gestures.dart';
import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/steward_test_fonts.dart';

/// 字号断言检查最终 RenderParagraph，避免只检查常量却遗漏默认文字或控件继承。
/// 按压断言读取实际像素：Forui bounce 只影响绘制，单纯 getRect 无法发现旧回归。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  setUpAll(() async {
    // 优先字体必须以真实族名注册，避免产品已使用 Noto 而几何测试仍只测雅黑回退。
    await loadPreferredStewardTestFont();
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      final font = FontLoader('Microsoft YaHei')
        ..addFont(Future.value(ByteData.sublistView(bytes)));
      // 原生 Windows 的 w700 来自独立 msyhbd.ttc；只加载常规字体会让测试引擎
      // 合成粗体，不能用其末字边缘判断原生标题质量。标准本机入口自动加载同目录
      // 粗体，其它字体夹具可显式指定配套文件；已指定的文件读取失败必须报错。
      final boldPath =
          Platform.environment['MYSTIA_UI_BOLD_FONT'] ??
          (RegExp(r'(^|[\\/])msyh\.ttc$', caseSensitive: false).hasMatch(path)
              ? '${File(path).parent.path}${Platform.pathSeparator}msyhbd.ttc'
              : null);
      if (boldPath != null) {
        final boldBytes = await File(boldPath).readAsBytes();
        font.addFont(Future.value(ByteData.sublistView(boldBytes)));
      }
      await font.load();
    }
  });

  Widget host(
    Widget child, {
    bool dark = false,
    bool touch = false,
    double scale = 1,
  }) {
    final theme = stewardTheme(dark: dark, touch: touch);
    return material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, routedChild) => FTheme(
        data: theme,
        // 明确启用动效，不能让测试宿主的“减少动画”设置掩盖按压缩放问题。
        accessibility: const FAccessibility(
          accessibleNavigation: false,
          motion: FAccessibilityMotion.all,
          focusHighlight: true,
        ),
        child: MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: TextScaler.linear(scale)),
          child: routedChild!,
        ),
      ),
      home: ColoredBox(
        color: theme.colors.background,
        child: Padding(
          padding: EdgeInsets.only(
            left: touch ? 16 : 104,
            right: touch ? 16 : 24,
            top: 16,
          ),
          child: Align(alignment: Alignment.topLeft, child: child),
        ),
      ),
    );
  }

  void viewport(WidgetTester tester, double width) {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 1000);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
  }

  RenderParagraph paragraph(WidgetTester tester, String text) =>
      tester.renderObject<RenderParagraph>(
        find.descendant(of: find.text(text), matching: find.byType(RichText)),
      );

  for (final touch in [false, true]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('公共设置正文与信息字段统一字号，390px可读且尊重缩放 $touch/$scale', (tester) async {
        viewport(tester, 390);
        await tester.pumpWidget(
          host(
            Section('统一分组', [
              const Text('模块正文'),
              SettingToggle('自动执行', true, (_) {}, description: '关闭后保留现有配置'),
              IntegerSetting(
                title: '并发任务',
                value: 3,
                min: 1,
                max: 8,
                onSave: (_) async {},
              ),
              const Fact('任务字段', '字段内容'),
              StewardButton('查看详情', onPressed: () {}),
            ]),
            touch: touch,
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();

        for (final text in [
          '模块正文',
          '自动执行',
          '并发任务',
          '3',
          '任务字段',
          '字段内容',
          '查看详情',
        ]) {
          final render = paragraph(tester, text);
          expect(render.text.style!.fontSize, 13, reason: text);
          // 操作控件与正文共用13px/1.45行高，尺寸由项目几何入口统一决定，
          // 不再继承 Forui 默认按钮1倍行高。边框、内容与命中区另有几何回归。
          expect(render.text.style!.height, 1.45, reason: text);
          expect(render.textScaler.scale(13), closeTo(13 * scale, .01));
          final rect = tester.getRect(find.text(text));
          expect(rect.left, greaterThanOrEqualTo(touch ? 16 : 104));
          expect(rect.right, lessThanOrEqualTo(touch ? 374 : 366));
        }
        expect(paragraph(tester, '统一分组').text.style!.fontSize, 15);
        expect(paragraph(tester, '关闭后保留现有配置').text.style!.fontSize, 12);
        expect(find.text('1–8 · 步进 1 · 方向键精调'), findsNothing);
        // 统一字号不能顺带缩小触屏目标，桌面依旧使用项目 36px 最小控制区。
        expect(
          tester.getSize(find.byType(StewardButton)).height,
          greaterThanOrEqualTo(touch ? 44 : 36),
        );
        expect(tester.takeException(), isNull);
      });
    }
  }

  for (final width in [640.0, 1187.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('设置分组标题完整排版，末字不缩放或裁切 $width/$scale', (tester) async {
        viewport(tester, width);
        const titles = ['评分排序', '设备与共享配置'];
        await tester.pumpWidget(
          host(
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                for (final title in titles)
                  Section(title, const [Text('同一中文主题正文')]),
              ],
            ),
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();
        for (final title in titles) {
          final render = paragraph(tester, title);
          final style = render.text.style!;
          expect(render.text.toPlainText(), title);
          expect(style.fontFamily, stewardFontFamily);
          expect(style.fontSize, 15);
          expect(style.fontWeight, FontWeight.w700);
          expect(style.letterSpacing ?? 0, 0);
          expect(style.wordSpacing ?? 0, 0);
          expect(render.textScaler.scale(15), closeTo(15 * scale, .01));

          // 字体选择框可能略超出段落逻辑顶边（真实粗体约-0.02px），这不是裁剪。
          // 应检查实际标题带的绘制空间以及祖先声明的裁剪，而非强行要求字体度量
          // 落在 RenderParagraph 的逻辑高内。像素诊断另见 title_glyph_raster_test。
          final boxes = render.getBoxesForSelection(
            TextSelection(
              baseOffset: title.length - 1,
              extentOffset: title.length,
            ),
          );
          expect(boxes, hasLength(1));
          final last = boxes.single.toRect();
          expect(last.width, greaterThan(0));
          RenderObject child = render;
          var parent = render.parent;
          RenderDecoratedBox? titleSurface;
          while (parent != null) {
            if (titleSurface == null && parent is RenderDecoratedBox) {
              titleSurface = parent;
              final titleBounds = Offset.zero & parent.size;
              final glyphBounds = MatrixUtils.transformRect(
                render.getTransformTo(parent),
                last,
              );
              expect(
                titleBounds.contains(glyphBounds.topLeft),
                isTrue,
                reason: '$title 的首行上缘必须留在实际标题带内。',
              );
              expect(
                titleBounds.contains(glyphBounds.bottomRight),
                isTrue,
                reason: '$title 的末字右下缘必须留在实际标题带内。',
              );
            }
            final clip = parent.describeApproximatePaintClip(child);
            if (clip != null) {
              final glyphBounds = MatrixUtils.transformRect(
                render.getTransformTo(parent),
                last,
              );
              expect(
                clip.contains(glyphBounds.topLeft),
                isTrue,
                reason: '$title 被 ${parent.runtimeType} 的上/左裁剪遮挡。',
              );
              expect(
                clip.contains(glyphBounds.bottomRight),
                isTrue,
                reason: '$title 被 ${parent.runtimeType} 的下/右裁剪遮挡。',
              );
            }
            child = parent;
            parent = parent.parent;
          }
          expect(titleSurface, isNotNull, reason: '必须检查真实Section标题带。');

          // 字号通过 TextScaler 排版，不允许祖先对标题纹理做额外缩放/斜切。
          final transform = render.getTransformTo(null).storage;
          expect(transform[0], closeTo(1, .000001));
          expect(transform[5], closeTo(1, .000001));
          expect(transform[1], closeTo(0, .000001));
          expect(transform[4], closeTo(0, .000001));
        }
        expect(tester.takeException(), isNull);
      });
    }
  }

  for (final dark in [false, true]) {
    testWidgets('折叠头按压填色与边框贴合，并保留键盘及表单状态 $dark', (tester) async {
      viewport(tester, 800);
      final theme = stewardTheme(dark: dark, touch: false);
      await tester.pumpWidget(
        host(
          const RepaintBoundary(
            key: ValueKey('collapsible-pixels'),
            child: CollapsibleSection('高级配置', [
              StewardField('临时草稿', initial: '初始值'),
            ]),
          ),
          dark: dark,
        ),
      );
      await tester.pumpAndSettle();
      final header = find.byType(FButton);
      final originalBounds = tester.getRect(header);
      final mouse = await tester.createGesture(kind: PointerDeviceKind.mouse);
      await mouse.addPointer(location: Offset.zero);
      addTearDown(mouse.removePointer);
      await mouse.moveTo(originalBounds.center);
      await mouse.down(originalBounds.center);
      await tester.pumpAndSettle();

      final boundary = tester.renderObject<RenderRepaintBoundary>(
        find.byKey(const ValueKey('collapsible-pixels')),
      );
      final pixels = await tester.runAsync(() async {
        final image = await boundary.toImage(pixelRatio: 1);
        try {
          return (
            bytes: (await image.toByteData(
              format: ui.ImageByteFormat.rawRgba,
            ))!,
            width: image.width,
          );
        } finally {
          image.dispose();
        }
      });
      final expected = theme.colors.secondary.toARGB32();
      // 远离文字与圆角，紧靠左右边缘各取两点。旧 bounce 收缩约 5px，至少
      // 一侧 2px 点会露出外层底色；仅断言布局尺寸相等无法发现这一绘制回归。
      for (final x in [
        originalBounds.left + 2,
        originalBounds.left + 5,
        originalBounds.right - 2,
        originalBounds.right - 5,
      ]) {
        final point = boundary.globalToLocal(
          Offset(x, originalBounds.center.dy),
        );
        final index = (point.dy.floor() * pixels!.width + point.dx.floor()) * 4;
        expect(
          pixels.bytes.buffer.asUint8List(
            pixels.bytes.offsetInBytes + index,
            4,
          ),
          [(expected >> 16) & 255, (expected >> 8) & 255, expected & 255, 255],
          reason: '按压填色必须覆盖原头部边界内部，不出现缩放形成的空隙。',
        );
      }
      expect(tester.getRect(header), originalBounds);
      await mouse.up();
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(EditableText), '保留的草稿');
      final editable = tester.element(find.byType(EditableText));
      await mouse.moveTo(originalBounds.center);
      await mouse.down(originalBounds.center);
      await mouse.up();
      await tester.pumpAndSettle();
      expect(find.byType(EditableText), findsNothing);

      // 用真实键盘激活重新展开，确认修复没有吞掉焦点/动作，也没有重建隐藏表单。
      // FTappable 将 FFocusedOutline 包在内部 Focus 外面，因此轮廓的 context
      // 不能获取按钮焦点；标题文字在真实 Focus 内，沿该上下文聚焦后发送键盘事件。
      Focus.of(tester.element(find.text('高级配置'))).requestFocus();
      await tester.pump();
      await tester.sendKeyEvent(LogicalKeyboardKey.enter);
      await tester.pumpAndSettle();
      expect(tester.element(find.byType(EditableText)), same(editable));
      expect(
        tester.widget<EditableText>(find.byType(EditableText)).controller.text,
        '保留的草稿',
      );
      expect(tester.takeException(), isNull);
    });
  }
}
