import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';

/// 完整工作台回归必须真正打开页头详情，确认完整诊断可读；只找到短摘要不能
/// 证明被移出正文的错误仍可访问。关闭对话框后继续原测试的重试、输入和权限断言。
Future<void> expectPageActivityDetails(
  WidgetTester tester,
  String detail,
) async {
  await tester.tap(
    find.byKey(const ValueKey('workbench-page-activity-button')),
  );
  await tester.pumpAndSettle();
  final text = tester.widget<Text>(
    find.byKey(const ValueKey('workbench-page-activity-details')),
  );
  expect(text.data, contains(detail));
  await tester.tap(find.widgetWithText(StewardButton, '关闭'));
  await tester.pumpAndSettle();
}

/// 单组件夹具没有工作台页头，直接检查发布内容完整且带错误级别；通道聚合与
/// 可点击详情由工作台集成测试覆盖，不为测试重新把提示插回控件正文。
void expectReportedFailure(WidgetTester tester, String detail) {
  final failures = tester
      .widgetList<PageActivityReporter>(find.byType(PageActivityReporter))
      .map((reporter) => reporter.activity)
      .whereType<PageActivity>()
      .where(
        (activity) =>
            activity.error && (activity.details?.contains(detail) ?? false),
      );
  expect(failures, isNotEmpty);
  expect(find.textContaining(detail), findsNothing);
}
