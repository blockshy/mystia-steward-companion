import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/extension_modules.dart';
import 'package:mystia_steward_companion/features/tools.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/module_switch_row.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';

/// 记录页面真正发出的本机偏好、资源订阅和写入；不提供游戏进程或实际 HTTP。
class _ModuleModel extends FixtureModel {
  int cancellations = 0;
  final localWrites = <DataMap>[];
  Completer<void>? writeGate;
  @override
  void clearResourceSubscription() => cancellations++;
  @override
  Future<void> updateLocal(DataMap changes) async {
    localWrites.add(Map.of(changes));
    await super.updateLocal(changes);
  }

  @override
  Future<void> mutate(
    String path, {
    Map<String, String> query = const {},
    DataMap? body,
    bool localOnly = false,
  }) async {
    await super.mutate(path, query: query, body: body, localOnly: localOnly);
    await writeGate?.future;
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;

  Future<ValueNotifier<bool>> open(
    WidgetTester tester,
    _ModuleModel model, {
    double width = 1280,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 920);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(model.dispose);
    final active = ValueNotifier(true);
    addTearDown(active.dispose);
    final theme = stewardTheme(dark: false, touch: width < 640);
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
        home: Padding(
          padding: const EdgeInsets.all(12),
          child: AnimatedBuilder(
            animation: Listenable.merge([model, active]),
            builder: (context, _) => ToolsPage(model, active: active.value),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
    return active;
  }

  Finder toggle(String title) => find.byWidgetPredicate(
    (widget) => widget is FSwitch && widget.semanticsLabel == title,
  );
  Future<void> tap(WidgetTester tester, String title) async {
    final target = find.text(title).last;
    await tester.ensureVisible(target);
    await tester.tap(target);
    await tester.pumpAndSettle();
  }

  for (final width in [390.0, 640.0, 1280.0]) {
    testWidgets('任务工具栏到列表只保留标准间距 $width', (tester) async {
      final model = _ModuleModel();
      model.localPreferences['missionListModuleEnabled'] = true;
      model.resources['/missions/tracked'] = {
        'status': 'ready',
        'missions': [
          {'label': 'layout-task', 'title': '合成任务', 'status': 'tracking'},
        ],
      };
      await open(tester, model, width: width, scale: 1.5);
      final filter = find.byWidgetPredicate(
        (widget) => widget is StewardField && widget.title == '筛选名称',
      );
      final field = find.descendant(
        of: filter,
        matching: find.byType(material.TextField),
      );
      final list = find.byKey(const ValueKey('mission-items'));
      // 检查真实控件边界到结果列表的距离，防止滚动区和工具栏重复叠加底部留白。
      expect(
        tester.getRect(list).top - tester.getRect(field).bottom,
        closeTo(8, .01),
      );
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('五个模块 Tab 始终可见；默认停用不读取，查看设备可保存本机工具开关', (tester) async {
    final model = _ModuleModel();
    model.authority = {'currentDeviceIsPrimary': false};
    final profile = Map.of(model.profile);
    await open(tester, model);
    expect(model.reads, isEmpty);
    expect(find.text('模块管理'), findsNothing);
    expect(find.text('自动化'), findsOneWidget);
    expect(find.text('游戏界面辅助'), findsOneWidget);
    for (final module in extensionModules) {
      expect(module.enabled(model.localPreferences), isFalse);
      expect(
        find.descendant(
          of: find.byType(WorkspaceTabs<String>).first,
          matching: find.text(module.tabLabel),
        ),
        findsOneWidget,
      );
      await tap(tester, module.tabLabel);
      final switchFinder = toggle('启用${module.title}模块');
      await tester.ensureVisible(switchFinder);
      await tester.tap(switchFinder);
      await tester.pumpAndSettle();
      expect(model.localWrites.last, {module.preferenceKey: true});
      expect(
        find.descendant(
          of: find.byType(WorkspaceTabs<String>).first,
          matching: find.text(module.tabLabel),
        ),
        findsOneWidget,
      );
    }
    expect(model.reads, [
      '/missions/tracked',
      '/missions/available',
      '/rare-guests/invitations',
    ], reason: '只读取当前已启用的模块；库存使用已有快照。');
    expect(model.writes, isEmpty);
    expect(model.profile, profile, reason: '模块开关不能改共享任务优先或自动化配置。');
    await tap(tester, '任务');
    final count = model.cancellations;
    await tester.tap(toggle('启用任务列表模块'));
    await tester.pumpAndSettle();
    expect(model.localPreferences['missionListModuleEnabled'], isFalse);
    expect(find.text('任务'), findsOneWidget);
    expect(find.text('任务列表模块已停用'), findsOneWidget);
    expect(model.cancellations, greaterThanOrEqualTo(count));
    expect(tester.takeException(), isNull);
  });

  testWidgets('隐藏模块停止读取，外部停用保留当前 Tab 并清除库存选择', (tester) async {
    final model = _ModuleModel();
    model.localPreferences = {
      for (final module in extensionModules) module.preferenceKey: true,
    };
    final active = await open(tester, model);
    final before = model.cancellations, reads = model.reads.length;
    active.value = false;
    await tester.pumpAndSettle();
    expect(model.cancellations, greaterThan(before));
    await tap(tester, '可接取');
    expect(model.reads.length, reads);
    active.value = true;
    await tester.pumpAndSettle();
    expect(model.reads.last, '/missions/available');
    await tap(tester, '库存修改');
    await tap(tester, '蘑菇');
    expect(find.text('已选 1 项'), findsOneWidget);
    final filter = find.descendant(
      of: find.byWidgetPredicate(
        (widget) => widget is StewardField && widget.title == '筛选名称',
      ),
      matching: find.byType(EditableText),
    );
    await tester.enterText(filter, '蘑菇');
    model.localPreferences['inventoryEditorModuleEnabled'] = false;
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('库存修改模块已停用'), findsOneWidget);
    expect(find.text('已选 1 项'), findsNothing);
    await tester.tap(toggle('启用库存修改模块'));
    await tester.pumpAndSettle();
    expect(find.text('已选 1 项'), findsNothing);
    expect(
      tester.widget<EditableText>(filter).controller.text,
      '蘑菇',
      reason: '停用再启用重建输入后，筛选文字仍与实际条件一致。',
    );
    expect(model.writes, isEmpty);
  });

  for (final hide in [false, true]) {
    testWidgets('库存确认期间${hide ? '隐藏页面' : '外部停用模块'}，返回确认不发送旧写入', (tester) async {
      final model = _ModuleModel();
      model.localPreferences = {'inventoryEditorModuleEnabled': true};
      final active = await open(tester, model);
      await tap(tester, '库存修改');
      await tap(tester, '蘑菇');
      await tap(tester, '设置已选 1 项');
      if (hide) {
        active.value = false;
      } else {
        model.localPreferences['inventoryEditorModuleEnabled'] = false;
        model.publish();
      }
      await tester.pumpAndSettle();
      await tap(tester, '确认');
      expect(model.writes, isEmpty);
      expect(tester.takeException(), isNull);
    });
  }

  testWidgets('当前停用页签被外部历史导入启用后恢复读取，普通刷新不重复订阅', (tester) async {
    final model = _ModuleModel();
    await open(tester, model);
    expect(model.reads, isEmpty);
    model.localPreferences['missionListModuleEnabled'] = true;
    model.publish();
    await tester.pumpAndSettle();
    expect(model.reads, ['/missions/tracked', '/missions/available']);
    model.publish();
    await tester.pumpAndSettle();
    expect(model.reads, ['/missions/tracked', '/missions/available']);
  });

  testWidgets('邀请确认后停用阻止批量写入，已有写入未返回时模块开关禁用', (tester) async {
    final model = _ModuleModel();
    model.localPreferences = {'rareGuestInvitationModuleEnabled': true};
    model.snapshot = {...model.snapshot, 'runtimeDaySceneReady': true};
    await open(tester, model);
    await tap(tester, '稀客邀请');
    await tap(tester, '邀请筛选范围');
    model.localPreferences['rareGuestInvitationModuleEnabled'] = false;
    model.publish();
    await tester.pumpAndSettle();
    await tap(tester, '确认');
    expect(model.writes, isEmpty);
    await tester.tap(toggle('启用稀客邀请模块'));
    await tester.pumpAndSettle();
    model.writeGate = Completer<void>();
    await tap(tester, '邀请筛选范围');
    await tap(tester, '确认');
    expect(model.writes.single.$1, '/rare-guests/invite-all');
    for (final module in extensionModules) {
      await tap(tester, module.tabLabel);
      final setting = tester.widget<ModuleSwitchRow>(
        find.byWidgetPredicate(
          (widget) =>
              widget is ModuleSwitchRow && widget.moduleName == module.title,
        ),
      );
      expect(setting.onChanged, isNull);
    }
    model.writeGate!.complete();
    await tester.pumpAndSettle();
    await tap(tester, '稀客邀请');
    expect(tester.widget<FSwitch>(toggle('启用稀客邀请模块')).enabled, isTrue);
  });

  for (final width in [1280.0, 640.0, 390.0]) {
    testWidgets('模块 Tab 在$width px${width == 390 ? '/150%' : ''}可访问和启停', (
      tester,
    ) async {
      final model = _ModuleModel();
      await open(tester, model, width: width, scale: width == 390 ? 1.5 : 1);
      for (final module in extensionModules) {
        await tap(tester, module.tabLabel);
        final target = toggle('启用${module.title}模块');
        await tester.ensureVisible(target);
        await tester.tap(target);
        await tester.pumpAndSettle();
        expect(module.enabled(model.localPreferences), isTrue);
        await tester.tap(target);
        await tester.pumpAndSettle();
        expect(module.enabled(model.localPreferences), isFalse);
      }
      expect(tester.takeException(), isNull);
      expect(model.writes, isEmpty);
    });
  }
}
