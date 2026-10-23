import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/extension_item_content.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/semantic_tag.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/text_geometry.dart';

/// 文档与人物卡片的布局契约。数据为纯合成记录，真实权限和失效边界由同目录
/// extension_item_presentation_test / extension_modules_test 覆盖，不在这里模拟业务算法。
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

  // 用实际 RenderBox 的首行基线而非文字盒顶部验证，避免不同字号在 Ahem 或
  // 中文字体下“同顶但不同线”的假阳性；值列多行不会改变第一行基线的比较。
  double baseline(WidgetTester tester, String text) {
    return textBaseline(tester, find.text(text));
  }

  Future<void> open(
    WidgetTester tester,
    Widget child, {
    double width = 262,
    double scale = 1.5,
  }) async {
    final theme = stewardTheme(dark: false, touch: width < 460);
    await tester.pumpWidget(
      material.MaterialApp(
        theme: theme.toApproximateMaterialTheme(),
        builder: (context, child) => FTheme(
          data: theme,
          child: MediaQuery(
            data: MediaQuery.of(context)
                .copyWith(textScaler: TextScaler.linear(scale)),
            child: child!,
          ),
        ),
        home: Align(
          alignment: Alignment.topLeft,
          child: SizedBox(
            width: width,
            child: SingleChildScrollView(child: child),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('窄屏任务采用固定字段列和逐条条件，标题状态不挤作一行', (tester) async {
    await open(
      tester,
      const MissionItemContent({
        'title': '合成长任务标题：准备招待远道而来的客人',
        'status': 'tracking',
        'characterName': '合成任务角色',
        'receiverLabel': '合成接取人',
        'sceneNames': ['合成地图甲', '合成地图乙'],
        'conditionCount': 3,
        'completedConditionCount': 1,
        'conditions': [
          {'description': '向合成角色交付三份含有指定标签的料理', 'completed': true},
          {'description': '与合成角色交谈', 'completed': false},
          {'description': '', 'completed': null},
        ],
        'presentationStatus': 'ready',
      }, available: false),
    );
    expect(find.byType(OrderTag), findsNothing);
    expect(find.byType(SemanticTag), findsNothing);
    expect(find.byType(FCheckbox), findsNothing, reason: '任务条件是只读事实，不可手工勾选');
    final character = tester.getRect(find.text('合成任务角色'));
    final receiver = tester.getRect(find.text('合成接取人'));
    final scene = tester.getRect(find.text('合成地图甲 · 合成地图乙'));
    expect(character.left, receiver.left);
    expect(character.left, scene.left);
    expect(scene.right, lessThanOrEqualTo(262));
    expect(
      tester.getRect(find.text('进行中')).top,
      greaterThan(tester.getRect(find.text('合成长任务标题：准备招待远道而来的客人')).bottom),
    );
    expect(
      tester.getRect(find.text('向合成角色交付三份含有指定标签的料理')).top,
      lessThan(tester.getRect(find.text('与合成角色交谈')).top),
    );
    expect(
      tester.getRect(find.text('与合成角色交谈')).top,
      lessThan(tester.getRect(find.text('条件 3：说明暂不可用')).top),
    );
    expect(find.text('已完成'), findsOneWidget);
    expect(find.text('未完成'), findsOneWidget);
    expect(find.text('待确认'), findsOneWidget);
    // 长条件允许换行，进度列保持同一右边界，且不随段落高度向下漂移。
    final done = tester.getRect(find.text('已完成'));
    final pending = tester.getRect(find.text('未完成'));
    expect(done.right, closeTo(pending.right, .1));
    expect(done.right, lessThanOrEqualTo(262));
    expect(
      baseline(tester, '已完成'),
      closeTo(baseline(tester, '向合成角色交付三份含有指定标签的料理'), .1),
    );
    expect(tester.takeException(), isNull);
  });

  testWidgets('宽屏状态与任务标题同层，可接任务说明完整但不伪造条件进度', (tester) async {
    await open(
      tester,
      const MissionItemContent({
        'title': '合成可接任务',
        'activationStatus': 'triggering',
        'activationMode': 'automatic',
        'triggerKind': 'enter-day-scene-map',
        'sourceTiming': 'before-performance',
        'activationHint': 'native-start-pending',
        'presentationStatus': 'no-receiver',
      }, available: true),
      width: 700,
      scale: 1,
    );
    expect(baseline(tester, '正在触发'), closeTo(baseline(tester, '合成可接任务'), .1));
    expect(find.text('条件进度'), findsNothing);
    expect(find.text('自动触发'), findsOneWidget);
    expect(find.text('进入目标日间地图'), findsOneWidget);
    expect(find.text('演出前'), findsOneWidget);
    expect(find.text('游戏正在启动该任务，请稍候。'), findsOneWidget);
    expect(find.text('无接取人'), findsOneWidget);
    expect(find.text('此任务没有接取人。'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('邀请卡展示未知关系和完整原因，不构造时间字段或多重标签', (tester) async {
    const explanation = '今晚尚不可邀请；请先在游戏中完成前置对话，之后再查看邀请状态。';
    await open(
      tester,
      const InvitationItemContent({
        'name': '合成关系未知的稀客',
        'status': 'kizuna-uninitialized',
        'kizunaLevel': -1,
        'sceneNames': ['合成地图甲', '合成地图乙'],
        'reason': explanation,
        'canInvite': false,
      }, onInvite: null),
    );
    expect(find.text('羁绊未初始化'), findsOneWidget);
    expect(find.text('羁绊'), findsOneWidget);
    expect(
      find.byWidgetPredicate(
        (widget) => widget is Semantics && widget.properties.label == '实时地点',
      ),
      findsOneWidget,
      reason: '合并视觉字段后仍须保留实时地点的读屏名称',
    );
    expect(find.text('未知'), findsOneWidget);
    expect(find.text('位置未确认'), findsOneWidget);
    expect(find.text('位置读取状态未知'), findsOneWidget);
    expect(
      find.text('合成地图甲 · 合成地图乙'),
      findsNothing,
      reason: '没有实时读取状态时，不能将提供的静态地点误作实时位置',
    );
    expect(find.text('邀请说明'), findsOneWidget);
    expect(find.text(explanation), findsOneWidget);
    expect(find.text('时间'), findsNothing);
    expect(find.byType(OrderTag), findsNothing);
    expect(find.byType(SemanticTag), findsNothing);
    expect(
      tester.widget<StewardButton>(find.byType(StewardButton)).onPressed,
      isNull,
    );
    expect(tester.takeException(), isNull);
  });

  // 使用服务器的三态位置协议，不由 isCurrentScene=false 推导其它地图。
  // 查无/失败样本故意含过期地点，确保前端不会将旧值或静态出没地点冒充实时值。
  for (final sample in [
    ('current-map', true, '合成当前地图', '当前地图已登记'),
    ('not-in-current-map', false, '位置未确认', '当前地图未登记'),
    ('unavailable', false, '位置未确认', '位置读取失败'),
  ]) {
    testWidgets('邀请位置 ${sample.$1} 保留准确读取状态', (tester) async {
      await open(
        tester,
        InvitationItemContent({
          'id': 1100,
          'name': '合成地图候选',
          'status': 'available',
          'kizunaLevel': 3,
          'locationStatus': sample.$1,
          'isCurrentScene': sample.$2,
          'sceneNames': ['合成当前地图'],
          'sceneLabels': ['synthetic-map'],
          'reason': '邀请条件已满足。',
        }, onInvite: null),
      );
      expect(find.text(sample.$3), findsOneWidget);
      expect(find.text(sample.$4), findsOneWidget);
      expect(find.text('其他地图'), findsNothing);
      if (!sample.$2) expect(find.text('合成当前地图'), findsNothing);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('当前地图缺少名称时只回退精确标识，缺少全部名称时不查静态目录', (tester) async {
    for (final sample in [
      (<String>['synthetic-map'], '地图标识：synthetic-map'),
      (<String>[], '当前地图（名称未提供）'),
    ]) {
      await open(
        tester,
        InvitationItemContent({
          'name': '合成地图候选',
          'status': 'available',
          'locationStatus': 'current-map',
          'sceneNames': <String>[],
          'sceneLabels': sample.$1,
        }, onInvite: null),
      );
      expect(find.text(sample.$2), findsOneWidget);
      expect(find.text('当前地图已登记'), findsOneWidget);
      expect(tester.takeException(), isNull);
    }
  });

  testWidgets('未知协议枚举明确显示未确认，不泄露原码或伪装成功', (tester) async {
    await open(
      tester,
      const MissionItemContent({
        'title': '合成未知任务',
        'activationStatus': 'future-activation-state',
        'activationMode': 'future-mode',
        'triggerKind': 'future-trigger',
        'sourceTiming': 'future-timing',
        'activationHint': 'future-hint',
      }, available: true),
    );
    expect(find.text('状态未确认'), findsOneWidget);
    expect(find.text('方式未确认'), findsOneWidget);
    expect(find.text('触发来源未确认'), findsOneWidget);
    expect(find.text('时机未确认'), findsOneWidget);
    expect(find.textContaining('future-'), findsNothing);
    expect(find.text('可触发'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  for (final width in [262.0, 390.0, 640.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('紧凑邀请在 $width/$scale 保留完整说明基线与状态羁绊同排', (tester) async {
        const reason = '已按原生羁绊邀请条件加入今晚名单（羁绊 3）；当前地图未登记并不表示其它地图，邀请状态以此说明为准。';
        await open(
          tester,
          const InvitationItemContent({
            'name': '合成长说明候选',
            'status': 'invited',
            'kizunaLevel': 3,
            'locationStatus': 'not-in-current-map',
            'isCurrentScene': false,
            'reason': reason,
          }, onInvite: null),
          width: width,
          scale: scale,
        );
        final caption = tester.getRect(find.text('邀请说明'));
        final value = tester.getRect(find.text(reason));
        expect(
          baseline(tester, reason),
          closeTo(baseline(tester, '邀请说明'), .1),
          reason: '12px标签与13px原因必须按真实首行基线对齐',
        );
        expect(
          baseline(tester, '已邀请'),
          closeTo(baseline(tester, '3 级'), .1),
          reason: '状态与羁绊共享首行，不再独占两条资料行',
        );
        expect(baseline(tester, '羁绊'), closeTo(baseline(tester, '3 级'), .1));
        expect(value.left, greaterThan(caption.right));
        expect(value.right, lessThanOrEqualTo(width - 10));
        if (width == 262) expect(value.height, greaterThan(caption.height));
        expect(tester.takeException(), isNull);
      });
    }
  }
}
