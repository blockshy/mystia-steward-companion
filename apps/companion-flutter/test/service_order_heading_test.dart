import 'package:flutter/rendering.dart';
import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/service_order_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/steward_test_fonts.dart';

void main() {
  setUpAll(() async {
    await loadPreferredStewardTestFont();
    await (FontLoader('packages/forui_lucide/ForuiLucideIcons')
          ..addFont(rootBundle.load('packages/forui_lucide/assets/lucide.ttf')))
        .load();
  });
  // 宽度为扣除导航、页面/卡片边距后的真实正文约束；覆盖桌面最窄窗口和
  // Android 窄屏，并覆盖专注页额外显示客人类型、受限提示及大字号的组合。
  for (final width in [294.0, 342.0, 480.0]) {
    for (final scale in [1.0, 1.5]) {
      for (final showKind in [false, true]) {
        testWidgets(
          '标题阶段变化保持正文位置 $width/$scale focus=$showKind',
          (tester) async {
            final theme = stewardTheme(dark: false, touch: width < 400);
            final order = <String, Object?>{
              'deskCode': 7,
              'guestName': '超长名称的稀客测试对象',
              // 受限标签不会因摘要压缩而丢失，完整信息仍可从语义与提示查看。
              'automationAllowed': false,
            };
            StateSetter? rebuild;
            await tester.pumpWidget(
              material.MaterialApp(
                localizationsDelegates: FLocalizations.localizationsDelegates,
                supportedLocales: FLocalizations.supportedLocales,
                theme: theme.toApproximateMaterialTheme(),
                home: FTheme(
                  data: theme,
                  child: MediaQuery(
                    data: MediaQueryData(textScaler: TextScaler.linear(scale)),
                    child: Align(
                      alignment: Alignment.topLeft,
                      child: SizedBox(
                        width: width,
                        child: StatefulBuilder(
                          builder: (context, setState) {
                            rebuild = setState;
                            return Column(
                              crossAxisAlignment: CrossAxisAlignment.stretch,
                              children: [
                                ServiceOrderHeading(
                                  {
                                    'order': order,
                                    'presentationSchedulingPaused': true,
                                  },
                                  rare: true,
                                  showKind: showKind,
                                ),
                                const SizedBox(
                                  key: ValueKey('recipe-start'),
                                  height: 50,
                                ),
                              ],
                            );
                          },
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            );
            await tester.pump();
            final initial = tester.getTopLeft(
              find.byKey(const ValueKey('recipe-start')),
            );
            for (final state in <DataMap>[
              {'hasServedFood': false, 'hasServedBeverage': false},
              {'hasServedFood': true, 'hasServedBeverage': false},
              {'hasServedFood': true, 'hasServedBeverage': true},
              {'readyToEvaluate': true},
              {'hasEvaluated': true},
            ]) {
              rebuild!(() => order.addAll(state));
              await tester.pump();
              expect(
                tester.getTopLeft(find.byKey(const ValueKey('recipe-start'))),
                initial,
                reason: '状态 $state 不应改变下方配方的起点',
              );
              expect(tester.takeException(), isNull);
              // 真实中文每字宽度和 Ahem 不同；只检查元素存在或没有溢出，仍可能
              // 漏掉文字被省略到只剩图标的退化。逐阶段检查实际 RenderParagraph。
              for (final stage in ['待送达', '已有料理', '已送齐', '待评价', '已评价']) {
                final text = find.text(stage);
                if (text.evaluate().isEmpty) continue;
                final paragraph = tester.renderObject<RenderParagraph>(
                  find.descendant(of: text, matching: find.byType(RichText)),
                );
                expect(
                  paragraph.didExceedMaxLines,
                  isFalse,
                  reason: '$stage 必须完整可读，不能只保留图标或省略号',
                );
                final rect = tester.getRect(text);
                expect(rect.left, greaterThanOrEqualTo(0));
                expect(rect.right, lessThanOrEqualTo(width));
              }
            }
            final heading = tester.widget<OrderHeading>(
              find.byType(OrderHeading),
            );
            expect(heading.statusDescription, contains('调度暂停'));
            expect(heading.statusDescription, contains('已有料理'));
            expect(heading.statusDescription, contains('已有酒水'));
            expect(heading.statusDescription, contains('已评价'));
            expect(heading.statusDescription, contains('暂不可自动处理'));
            final title = tester.getRect(find.text('8 桌 · 超长名称的稀客测试对象'));
            final evaluated = tester.getRect(find.text('已评价'));
            expect(evaluated.left, greaterThan(title.right));
            expect(evaluated.right, lessThanOrEqualTo(width));
          },
          // 未提供真实字体时明确跳过，不能把 Ahem 几何当作中文可读性证据。
          skip: preferredStewardTestFontPath == null,
        );
      }
    }
  }

  testWidgets('普通订单不会由送达事实推断评价，自动化诊断仍保留全部事实', (tester) async {
    final theme = stewardTheme(dark: false, touch: false);
    final order = <String, Object?>{
      'deskCode': 0,
      'guestName': '普通客人',
      'hasServedFood': true,
      'hasServedBeverage': true,
      'readyToEvaluate': false,
    };
    await tester.pumpWidget(
      material.MaterialApp(
        theme: theme.toApproximateMaterialTheme(),
        home: FTheme(
          data: theme,
          child: ServiceOrderHeading({'order': order}, rare: false),
        ),
      ),
    );
    expect(find.text('已送齐'), findsOneWidget);
    expect(find.text('待评价'), findsNothing);
    final diagnostic = orderProgressTags(
      order,
      includeUnknown: true,
    ).cast<OrderTag>().map((tag) => tag.text);
    expect(diagnostic, ['已有料理', '已有酒水']);
    expect(tester.takeException(), isNull);
  });
}
