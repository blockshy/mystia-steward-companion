import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/extension_item_content.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 使用明确的手机平台事实与合成日间候选，验证控制区不会挤占整张人物卡。
/// HTTP、真实存储与游戏进程均不参与；写入载荷仍通过既有模型端口记录。
class _MobileModel extends FixtureModel {
  _MobileModel({bool desktop = false}) : super(desktop: desktop) {
    localPreferences = {
      'missionListModuleEnabled': true,
      'rareGuestInvitationModuleEnabled': true,
    };
    resources = {
      '/rare-guests/invitations': {
        'ok': true,
        'runtimeAvailable': true,
        'status': '合成日间邀请名单',
        'candidates': [
          for (var id = 1; id <= 3; id++)
            {
              'id': id,
              'name': '合成稀客 $id',
              'kizunaLevel': 3,
              'sceneNames': ['合成地图'],
              'isCurrentScene': true,
              'locationStatus': 'current-map',
              'status': 'available',
              'canInvite': true,
              'reason': '合成条件已就绪',
            },
        ],
      },
    };
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(() async {
    final font = Platform.environment['MYSTIA_UI_FONT'];
    if (font != null) {
      final bytes = await File(font).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
  });

  Future<void> open(
    WidgetTester tester,
    _MobileModel model, {
    double width = 390,
    double scale = 1,
    double height = 700,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 844);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
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
          alignment: Alignment.topCenter,
          child: SizedBox(
            height: height,
            child: Padding(
              padding: const EdgeInsets.symmetric(horizontal: 12),
              child: AnimatedBuilder(
                animation: model,
                builder: (context, _) => ToolsPage(model),
              ),
            ),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    final invitationTab = find.text('稀客邀请');
    await tester.ensureVisible(invitationTab);
    await tester.pumpAndSettle();
    expect(invitationTab.hitTestable(), findsOneWidget);
    await tester.tap(invitationTab);
    await tester.pumpAndSettle();
  }

  Future<void> tap(WidgetTester tester, Finder finder) async {
    await tester.ensureVisible(finder);
    await tester.pumpAndSettle();
    expect(finder.hitTestable(), findsOneWidget);
    await tester.tap(finder);
    await tester.pumpAndSettle();
  }

  Finder invite() => find.descendant(
    of: find.byType(InvitationItemContent).first,
    matching: find.byType(StewardButton),
  );

  for (final desktop in [false, true]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('羁绊数字完整落在复选项内且相邻项不相交 desktop=$desktop scale=$scale', (
        tester,
      ) async {
        final model = _MobileModel(desktop: desktop);
        await open(tester, model, scale: scale);
        Finder item(int level) =>
            find.byKey(ValueKey('invitation-level-$level'));
        Finder checkbox(int level) =>
            find.descendant(of: item(level), matching: find.byType(FCheckbox));
        Finder digit(int level) =>
            find.descendant(of: item(level), matching: find.text('$level'));
        await tester.ensureVisible(item(5));
        await tester.pumpAndSettle();
        // 必须同时检查控件与数字的真实RenderBox，单纯“无RenderFlex错误”不能
        // 发现Forui表格标签绘制到邻项下方的遮挡；矩形包含与两两相交均严格检查。
        final rectangles = <Rect>[];
        for (var level = 0; level <= 5; level++) {
          final area = tester.getRect(checkbox(level));
          final text = tester.getRect(digit(level));
          expect(text.left, greaterThanOrEqualTo(area.left));
          expect(text.right, lessThanOrEqualTo(area.right));
          expect(text.top, greaterThanOrEqualTo(area.top));
          expect(text.bottom, lessThanOrEqualTo(area.bottom));
          expect(tester.getRect(item(level)), area);
          expect(
            tester.widget<FCheckbox>(checkbox(level)).semanticsLabel,
            '羁绊 $level 级',
          );
          for (final previous in rectangles) {
            expect(area.overlaps(previous), isFalse);
            expect(text.overlaps(previous), isFalse);
          }
          rectangles.add(area);
        }
        // 直接点击数字而非方框，验证完整标签区域属于对应操作，不会切换下一等级。
        for (var level = 0; level <= 5; level++) {
          await tap(tester, digit(level));
          for (var other = 0; other <= 5; other++) {
            expect(
              tester.widget<FCheckbox>(checkbox(other)).value,
              other > level,
            );
          }
        }
        expect(model.writes, isEmpty);
        expect(tester.takeException(), isNull);
      });
    }
  }

  for (final sample in [
    (390.0, 1.0, 700.0),
    (390.0, 1.5, 700.0),
    (360.0, 1.5, 480.0),
  ]) {
    testWidgets('手机控制区有界、搜索同排且全部筛选可达 $sample', (tester) async {
      final model = _MobileModel();
      await open(
        tester,
        model,
        width: sample.$1,
        scale: sample.$2,
        height: sample.$3,
      );
      final body = tester.getRect(
        find.byKey(const ValueKey('mobile-extension-body')),
      );
      final controls = tester.getRect(
        find.byKey(const ValueKey('mobile-extension-control-region')),
      );
      final list = tester.getRect(
        find.byKey(const ValueKey('invitation-items')),
      );
      expect(controls.height, lessThanOrEqualTo(280));
      expect(controls.height, lessThanOrEqualTo(body.height * .5));
      expect(list.height, greaterThanOrEqualTo(body.height * .5 - 8));
      expect(find.byType(Section), findsNothing, reason: '已启用模块不重复标题与长引导');
      expect(find.text('本机·可写'), findsOneWidget);
      expect(find.byType(RawScrollbar), findsNWidgets(2));
      final search = tester.getRect(find.byType(StewardField));
      final refresh = tester.getRect(
        find.byKey(const ValueKey('extension-refresh')),
      );
      expect(search.center.dy, closeTo(refresh.center.dy, .01));
      expect(search.right, lessThan(refresh.left));
      expect(refresh.right, lessThanOrEqualTo(body.right));
      if (sample.$2 == 1) {
        final first = tester.getRect(find.byType(InvitationItemContent).first);
        expect(first.top, greaterThanOrEqualTo(list.top));
        expect(
          first.bottom,
          lessThanOrEqualTo(list.bottom),
          reason: '普通字号初始视口应完整露出首张客人卡',
        );
      }

      // 控制区单独滚动后，名单视口仍留在原位；羁绊、地图和批量操作均能真实点击。
      final levelFive = find.byWidgetPredicate(
        (widget) => widget is FCheckbox && widget.semanticsLabel == '羁绊 5 级',
      );
      await tap(tester, levelFive);
      expect(tester.widget<FCheckbox>(levelFive).value, isFalse);
      expect(
        tester.getRect(find.byKey(const ValueKey('invitation-items'))),
        list,
      );
      await tap(tester, find.text('全部地图'));
      await tap(tester, find.text('邀请筛选范围'));
      await tap(tester, find.text('确认'));
      expect(model.writes.single.$1, '/rare-guests/invite-all');
      expect(model.writes.single.$2['levels'], '0,1,2,3,4');
      expect(model.writes.single.$2['scope'], 'all');
      expect(model.writes.single.$2['expectedDaySceneGeneration'], '1');
      await tap(tester, invite());
      expect(model.writes.last.$1, '/rare-guests/invite');
      expect(model.writes.last.$2['guestId'], '1');
      expect(tester.takeException(), isNull);
    });
  }

  for (final sample in [
    (1280.0, 1.0, true),
    (640.0, 1.5, true),
    (390.0, 1.5, false),
    (360.0, 1.5, false),
    // 此夹具扣除两侧12px后是262px正文，覆盖窄桌面真实剩余空间。
    (286.0, 1.5, true),
  ]) {
    testWidgets('羁绊筛选与邀请按钮同行，横滚不带走操作且刷新保留位置 $sample', (tester) async {
      final model = _MobileModel(desktop: sample.$3);
      await open(tester, model, width: sample.$1, scale: sample.$2);
      final row = find.byKey(const ValueKey('invitation-filter-row'));
      final action = find.byKey(const ValueKey('invitation-bulk-action'));
      final viewport = find.byKey(
        const PageStorageKey('invitation-level-scroll'),
      );
      await tester.ensureVisible(action);
      await tester.pumpAndSettle();
      final line = tester.getRect(row);
      final button = tester.getRect(action);
      final scrollArea = tester.getRect(viewport);
      final label = tester.getRect(
        find.descendant(of: row, matching: find.text('羁绊')),
      );
      expect(button.right, closeTo(line.right, .01));
      expect(button.center.dy, closeTo(line.center.dy, .01));
      expect(label.center.dy, closeTo(line.center.dy, .01));
      expect(scrollArea.center.dy, closeTo(line.center.dy, .01));
      expect(label.right, lessThan(scrollArea.left));
      expect(scrollArea.right, lessThan(button.left));
      expect(
        scrollArea.width,
        greaterThanOrEqualTo(44),
        reason: '至少保留一个完整等级的操作宽度',
      );
      expect(action.hitTestable(), findsOneWidget);
      final scroller = tester.widget<SingleChildScrollView>(viewport);
      final controller = scroller.controller!;
      expect(scroller.scrollDirection, Axis.horizontal);

      final last = find.byKey(const ValueKey('invitation-level-5'));
      await tester.ensureVisible(last);
      await tester.pumpAndSettle();
      if (controller.position.maxScrollExtent > 0) {
        expect(controller.offset, greaterThan(0));
      }
      expect(tester.getRect(action), button, reason: '横滚等级不会带走或移动右侧邀请按钮');
      expect(last.hitTestable(), findsOneWidget);
      await tester.tap(find.descendant(of: last, matching: find.text('5')));
      await tester.pumpAndSettle();
      final checkbox = find.descendant(
        of: last,
        matching: find.byType(FCheckbox),
      );
      expect(tester.widget<FCheckbox>(checkbox).value, isFalse);
      final offset = controller.offset;
      model.publish();
      await tester.pumpAndSettle();
      expect(
        tester.widget<SingleChildScrollView>(viewport).controller,
        same(controller),
      );
      expect(controller.offset, closeTo(offset, .01), reason: '只读轮询不能重置等级浏览位置');
      expect(tester.widget<FCheckbox>(checkbox).value, isFalse);
      expect(action.hitTestable(), findsOneWidget);
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('手机紧凑开关保留查看权限与停用边界，说明保留在开关语义中', (tester) async {
    final model = _MobileModel();
    await open(tester, model);
    final oldAction = tester.widget<StewardButton>(invite()).onPressed!;
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('本机·查看'), findsOneWidget);
    expect(tester.widget<StewardButton>(invite()).onPressed, isNull);
    await tap(tester, find.byType(FSwitch));
    expect(model.localPreferences['rareGuestInvitationModuleEnabled'], isFalse);
    expect(find.text('稀客邀请模块已停用'), findsOneWidget);
    // 模块说明不再重复占据正文；开关状态仍以完整读屏文案保留作用域与关闭含义。
    final status = tester.widget<Text>(
      find.byKey(const ValueKey('module-switch-status')),
    );
    expect(status.semanticsLabel, contains('读取日间候选，并按明确选择单独或批量邀请。'));
    expect(find.textContaining('读取日间候选，并按明确选择单独或批量邀请。'), findsNothing);
    oldAction();
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    expect(tester.takeException(), isNull);
  });
}
