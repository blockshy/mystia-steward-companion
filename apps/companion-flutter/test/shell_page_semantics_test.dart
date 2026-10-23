import 'package:flutter/semantics.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';

import '../tool/preview/rich_preview_workbench_model.dart';
import 'support/steward_test_fonts.dart';

/// 复现窄桌面页面保留状态的切换路径；语义树始终启用，不能通过关闭语义或动画
/// 隐藏更新错误。数据仅来自合成预览模型，不连接平台、HTTP或真实游戏。
void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(loadPreferredStewardTestFont);

  testWidgets('390px大字号切页只发布当前页语义，隐藏通知不泄漏且返回保留输入', (tester) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = const Size(390, 920);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final model = RichPreviewWorkbenchModel();
    model.localPreferences = {'fontScalePercent': 150};
    addTearDown(model.dispose);
    final semantics = tester.ensureSemantics();
    try {
      await tester.pumpWidget(
        ProviderScope(
          overrides: [workbenchProvider.overrideWithValue(model)],
          child: const StewardApp(),
        ),
      );
      await tester.pumpAndSettle();

      final seenPageIds = <int, int>{};
      void assertOnlyCurrentPage(int page) {
        final current = tester.getSemantics(
          find.byKey(ValueKey('workbench-page-semantics-$page')),
        );
        seenPageIds[page] = current.id;
        var root = current;
        while (root.parent != null) {
          root = root.parent!;
        }
        final attachedIds = <int>{};
        void collect(SemanticsNode node) {
          attachedIds.add(node.id);
          node.visitChildren((child) {
            collect(child);
            return true;
          });
        }

        collect(root);
        expect(attachedIds, contains(current.id));
        for (final entry in seenPageIds.entries) {
          if (entry.key != page) {
            expect(
              attachedIds,
              isNot(contains(entry.value)),
              reason: '隐藏页面${entry.key}不能残留在当前可读语义树',
            );
          }
        }
        expect(tester.takeException(), isNull);
      }

      Future<void> navigate(int page) async {
        await tester.tap(find.byKey(ValueKey('nav-$page')));
        await tester.pumpAndSettle();
        assertOnlyCurrentPage(page);
      }

      assertOnlyCurrentPage(1);
      await navigate(0);
      await navigate(2);
      final search = find.descendant(
        of: find.byKey(const ValueKey('recommendation-name-filter')),
        matching: find.byType(EditableText),
      );
      await tester.ensureVisible(search);
      await tester.enterText(search, '合成草稿');
      await tester.pumpAndSettle();
      final input = tester.widget<EditableText>(search).controller;
      final savedSelection = input.selection;
      await navigate(3);
      await navigate(4);
      await navigate(3);
      final gameUi = find.text('游戏界面辅助').last;
      await tester.ensureVisible(gameUi);
      await tester.tap(gameUi);
      await tester.pumpAndSettle();
      assertOnlyCurrentPage(3);

      // 隐藏推荐页仍保留输入控制器并可接收模型通知，但不应重新接入可读语义树。
      await model.refresh();
      await tester.pumpAndSettle();
      assertOnlyCurrentPage(3);
      await navigate(2);
      expect(
        identical(tester.widget<EditableText>(search).controller, input),
        isTrue,
      );
      expect(input.text, '合成草稿');
      expect(input.selection, savedSelection);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    } finally {
      // Flutter在测试主体退出时检查未释放句柄，因此不能仅注册延后的tearDown。
      semantics.dispose();
    }
  });
}
