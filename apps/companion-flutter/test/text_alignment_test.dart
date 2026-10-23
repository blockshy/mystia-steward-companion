import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/module_switch_row.dart';
import 'package:mystia_steward_companion/ui/semantic_tag.dart';
import 'package:mystia_steward_companion/ui/steward_style.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/text_geometry.dart';

/// 只使用合成中文和内存回调。受控验证入口通过 MYSTIA_UI_FONT 载入真实中文字形，
/// 对齐检查使用实际 RenderParagraph 基线，不依赖 Ahem 方框字体的偶然等高结果。
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

  Widget host(Widget child, {required bool touch, required double scale}) {
    final theme = stewardTheme(dark: false, touch: touch);
    return material.MaterialApp(
      localizationsDelegates: FLocalizations.localizationsDelegates,
      supportedLocales: FLocalizations.supportedLocales,
      theme: theme.toApproximateMaterialTheme(),
      builder: (context, routedChild) => FTheme(
        data: theme,
        child: MediaQuery(
          data: MediaQuery.of(context)
              .copyWith(textScaler: TextScaler.linear(scale)),
          child: routedChild!,
        ),
      ),
      home: Padding(
        // 桌面扣除固定导航，触屏保留16px边距，分别覆盖真实窄正文而非整屏理想宽度。
        padding: EdgeInsets.fromLTRB(touch ? 16 : 104, 16, touch ? 16 : 24, 16),
        child: SingleChildScrollView(child: child),
      ),
    );
  }

  const longValue = '很长的资源说明需要自动换行，第二行与后续行仍然保留完整信息';
  const longTag = '超长的食材名称与完整库存说明需要连续换行';
  for (final touch in [false, true]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('中文混字号首基线、长值首行图标及开关命中保持一致 $touch/$scale', (tester) async {
        tester.view.devicePixelRatio = 1;
        tester.view.physicalSize = const Size(390, 1100);
        addTearDown(tester.view.resetPhysicalSize);
        addTearDown(tester.view.resetDevicePixelRatio);
        var toggles = 0;
        await tester.pumpWidget(
          host(
            Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                ModuleSwitchRow(
                  moduleName: '任务',
                  value: false,
                  status: '本机 · 只读',
                  explanation: '只影响本机任务列表的显示，不修改游戏。',
                  onChanged: (_) => toggles++,
                ),
                const SizedBox(height: 12),
                const Fact('资源说明', longValue),
                const SizedBox(height: 12),
                const StewardBaselineRow(
                  children: [
                    Text('连接状态', style: StewardText.body),
                    SizedBox(width: 8),
                    StatusBadge('已连接'),
                  ],
                ),
                const SizedBox(height: 12),
                const Align(
                  alignment: Alignment.centerLeft,
                  child: SizedBox(
                    width: 150,
                    child: SemanticTag(
                      key: ValueKey('long-aligned-tag'),
                      text: longTag,
                      semanticLabel: longTag,
                      icon: FLucideIcons.utensils,
                      tone: Color(0xff70442b),
                    ),
                  ),
                ),
                const SizedBox(height: 12),
                // 两列中右侧明确换成多行标签；前缀须对齐第一行内部文字，不能与
                // 整个 Wrap 的中心、外壳顶边或后续行对齐。
                const StewardBaselineRow(
                  children: [
                    Text('材料', style: StewardText.body),
                    SizedBox(width: 8),
                    Expanded(
                      child: Wrap(
                        spacing: 4,
                        runSpacing: 4,
                        children: [
                          SemanticTag(
                            text: '蘑菇和竹笋',
                            semanticLabel: '蘑菇和竹笋',
                            icon: FLucideIcons.utensils,
                            tone: Color(0xff70442b),
                          ),
                          SemanticTag(
                            text: '豆腐和海苔',
                            semanticLabel: '豆腐和海苔',
                            icon: FLucideIcons.utensils,
                            tone: Color(0xff70442b),
                          ),
                          SemanticTag(
                            text: '第二行其他材料的完整中文名称',
                            semanticLabel: '第二行其他材料的完整中文名称',
                            icon: FLucideIcons.utensils,
                            tone: Color(0xff70442b),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ],
            ),
            touch: touch,
            scale: scale,
          ),
        );
        await tester.pumpAndSettle();

        final enable = find.text('启用');
        final status = find.text('本机 · 只读');
        expect(
          textBaseline(tester, enable),
          closeTo(textBaseline(tester, status), .01),
        );
        final control = tester.getRect(find.byType(FSwitch));
        expect(
          control.center.dy,
          closeTo(textFirstLine(tester, enable).center, .01),
        );
        expect(
          tester
              .getRect(find.byKey(const ValueKey('module-switch-line')))
              .height,
          greaterThanOrEqualTo(44),
        );
        await tester.tap(find.byType(FSwitch));
        await tester.pumpAndSettle();
        expect(toggles, 1);

        expect(
          textBaseline(tester, find.text('资源说明')),
          closeTo(textBaseline(tester, find.text(longValue)), .01),
        );
        expect(
          tester.getRect(find.text(longValue)).height,
          greaterThan(textFirstLine(tester, find.text(longValue)).height),
        );
        expect(
          textBaseline(tester, find.text('连接状态')),
          closeTo(textBaseline(tester, find.text('已连接')), .01),
        );
        final tag = find.byKey(const ValueKey('long-aligned-tag'));
        final tagIcon = find.descendant(of: tag, matching: find.byType(Icon));
        final line = textFirstLine(tester, find.text(longTag));
        expect(tester.getRect(tagIcon).center.dy, closeTo(line.center, .01));
        expect(
          tester.getRect(find.text(longTag)).height,
          greaterThan(line.height),
        );
        expect(
          textBaseline(tester, find.text('材料')),
          closeTo(textBaseline(tester, find.text('蘑菇和竹笋')), .01),
        );
        expect(
          tester.getRect(find.text('第二行其他材料的完整中文名称')).top,
          greaterThan(tester.getRect(find.text('蘑菇和竹笋')).top),
        );
        expect(tester.takeException(), isNull);
      });
    }
  }
}
