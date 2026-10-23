import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/app/workbench_model.dart';
import 'package:mystia_steward_companion/features/profile_controls.dart';
import 'package:mystia_steward_companion/features/settings.dart';
import 'package:mystia_steward_companion/features/settings_management.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

import 'support/fixture_model.dart';
import 'support/steward_test_fonts.dart';
import 'support/text_geometry.dart';

/// 所有凭据、网卡和文件身份都是固定内存夹具；此专项不读取本机安全存储、
/// 不访问网卡、不写剪贴板、不发 HTTP，也不创建或删除真实诊断包。
class _SettingsModel extends FixtureModel {
  _SettingsModel({super.desktop}) {
    authority = {
      'registryId': 'synthetic-registry',
      'authorityRevision': 1,
      'currentDeviceId': 'self',
      'currentDeviceIsPrimary': true,
      'devices': [
        {
          'deviceId': 'self',
          'label': '本机',
          'isPrimary': true,
          'online': true,
          'syncPending': false,
        },
        {
          'deviceId': 'offline',
          'label': '旧手机',
          'isPrimary': false,
          'online': false,
          'syncPending': false,
        },
      ],
    };
    resources = {
      '/local-api/config': {
        'lanEnabled': true,
        'lanRunning': true,
        'lanBindHost': '192.0.2.10',
        'token': 'fixture-token-A',
        'availableLanEndpoints': [
          {'address': '192.0.2.10', 'interfaceName': '合成以太网'},
          {'address': '192.0.2.11', 'interfaceName': '合成无线网'},
        ],
        'lanEndpoints': [
          {
            'address': '192.0.2.10',
            'interfaceName': '合成以太网',
            'endpoint': 'http://192.0.2.10:32145',
          },
        ],
      },
      '/logs/settings': {
        'aggregateModLogMaxFileCount': 12,
        'bepInExConsoleConfiguredVisible': true,
      },
      '/logs/packages': {
        'ok': true,
        'items': [package],
      },
    };
  }

  static const package = <String, Object?>{
    'id': 'opaque-synthetic-package-v1',
    'fileName': 'diagnostics-synthetic.zip',
    'byteLength': 2048,
    'createdAtUtc': '2026-10-21T10:00:00Z',
  };
  @override
  ConnectionSource connectionSource = ConnectionSource.saved;
  @override
  String editableConnectionToken = 'saved-synthetic-token';
  @override
  List<WorkbenchOperation> recentOperations = [];
  final lanWrites = <({bool enabled, String host, String? token})>[];
  int diagnosticsReads = 0;
  @override
  Future<void> refreshDiagnostics() async {
    diagnosticsReads++;
  }

  @override
  Future<void> updateLanConnection({
    required bool enabled,
    required String host,
    String? token,
  }) async {
    lanWrites.add((enabled: enabled, host: host, token: token));
  }
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  setUpAll(loadPreferredStewardTestFont);

  Future<void> open(
    WidgetTester tester,
    _SettingsModel model, {
    Widget Function()? content,
    double width = 627,
    double scale = 1,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 1000);
    addTearDown(tester.view.resetDevicePixelRatio);
    addTearDown(tester.view.resetPhysicalSize);
    final theme = stewardTheme(dark: false, touch: !model.desktop);
    await tester.pumpWidget(
      material.MaterialApp(
        locale: const Locale('zh', 'CN'),
        localizationsDelegates: FLocalizations.localizationsDelegates,
        supportedLocales: FLocalizations.supportedLocales,
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
          padding: EdgeInsets.fromLTRB(
            model.desktop ? 104 : 16,
            16,
            model.desktop ? 24 : 16,
            16,
          ),
          child: AnimatedBuilder(
            animation: model,
            builder: (_, _) => content?.call() ?? SettingsPage(model),
          ),
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  Finder field(String title) => find.descendant(
    of: find.byWidgetPredicate(
      (widget) => widget is StewardField && widget.title == title,
    ),
    matching: find.byType(EditableText),
  );
  Finder button(String title) => find.byWidgetPredicate(
    (widget) => widget is StewardButton && widget.text == title,
  );
  Future<void> reveal(WidgetTester tester, Finder target) async {
    final scrollable = find
        .descendant(
          of: find.byType(SettingsPage),
          matching: find.byType(Scrollable),
        )
        .last;
    await tester.scrollUntilVisible(
      target,
      140,
      scrollable: scrollable,
      maxScrolls: 30,
    );
    await tester.ensureVisible(target);
    await tester.pumpAndSettle();
  }

  testWidgets('启动参数连接移除长说明且保留锁定，局域网配置仍可由主设备管理', (tester) async {
    final model = _SettingsModel()
      ..connectionSource = ConnectionSource.launchArguments;
    addTearDown(model.dispose);
    await open(tester, model);
    expect(find.textContaining('本次运行中不能修改'), findsNothing);
    expect(button('修改连接'), findsNothing);
    expect(field('连接 Token'), findsNothing);
    await reveal(tester, button('保存服务配置'));
    expect(tester.widget<StewardButton>(button('保存服务配置')).onPressed, isNotNull);
    expect(
      find.byWidgetPredicate(
        (widget) => widget is CollapsibleSection && widget.title == '局域网服务',
      ),
      findsNothing,
    );
    expect(find.text('读取设置'), findsNothing);
    expect(find.text('重置连接凭据'), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('手机明确修改时回显安全凭据，默认隐藏且换地址撤回自动回填', (tester) async {
    final model = _SettingsModel(desktop: false);
    addTearDown(model.dispose);
    await open(tester, model, width: 390);
    expect(field('连接 Token'), findsNothing);
    await tester.tap(button('修改连接'));
    await tester.pumpAndSettle();
    var input = tester.widget<EditableText>(field('连接 Token'));
    expect(input.controller.text, 'saved-synthetic-token');
    expect(input.obscureText, isTrue);
    await tester.tap(
      find.byWidgetPredicate(
        (widget) => widget is StewardIconButton && widget.label == '显示连接 Token',
      ),
    );
    await tester.pumpAndSettle();
    expect(tester.widget<EditableText>(field('连接 Token')).obscureText, isFalse);
    await tester.enterText(field('API 地址'), 'http://192.0.2.20:32145');
    await tester.pump();
    expect(
      tester.widget<EditableText>(field('连接 Token')).controller.text,
      isEmpty,
    );
    await tester.enterText(field('连接 Token'), 'new-target-token');
    input = tester.widget<EditableText>(field('连接 Token'));
    input.controller.value = input.controller.value.copyWith(
      selection: const TextSelection(baseOffset: 1, extentOffset: 4),
    );
    final before = input.controller.value;
    model.publish();
    await tester.pump();
    expect(
      tester.widget<EditableText>(field('连接 Token')).controller.value,
      before,
    );
    await tester.tap(button('取消修改'));
    await tester.pumpAndSettle();
    await tester.tap(button('修改连接'));
    await tester.pumpAndSettle();
    expect(
      tester.widget<EditableText>(field('连接 Token')).controller.text,
      'new-target-token',
    );
    expect(tester.widget<EditableText>(field('连接 Token')).obscureText, isTrue);
    expect(tester.takeException(), isNull);
  });

  testWidgets('忘记设备与同步主配置同行，仍拒绝本设备和在线设备', (tester) async {
    final model = _SettingsModel();
    addTearDown(model.dispose);
    await open(tester, model);
    final self = find.byKey(const ValueKey('device-self'));
    final selfForget = find.descendant(of: self, matching: button('忘记设备'));
    await reveal(tester, selfForget);
    expect(tester.widget<StewardButton>(selfForget).onPressed, isNull);
    await reveal(tester, find.text('旧手机 · 查看设备'));
    await tester.tap(find.text('旧手机 · 查看设备'));
    await tester.pumpAndSettle();
    final offline = find.byKey(const ValueKey('device-offline'));
    final forget = find.descendant(of: offline, matching: button('忘记设备'));
    final sync = find.descendant(of: offline, matching: button('同步主配置'));
    await reveal(tester, forget);
    expect(tester.widget<StewardButton>(forget).onPressed, isNotNull);
    expect(
      tester.widget<ToolRow>(
        find.ancestor(of: forget, matching: find.byType(ToolRow)).first,
      ),
      same(
        tester.widget<ToolRow>(
          find.ancestor(of: sync, matching: find.byType(ToolRow)).first,
        ),
      ),
    );
    final entries = records(model.authority['devices']);
    model.authority = {
      ...model.authority,
      'devices': [
        entries.first,
        {...entries.last, 'online': true},
      ],
    };
    model.publish();
    await tester.pump();
    expect(tester.widget<StewardButton>(forget).onPressed, isNull);
  });

  testWidgets('局域网只能选择实际网卡，刷新不改变密码选区、草稿或显示状态', (tester) async {
    final model = _SettingsModel();
    addTearDown(model.dispose);
    await open(
      tester,
      model,
      content: () => ListView(children: [SettingsLanSection(model)]),
    );
    final selector = tester.widget<StewardSelect<String>>(
      find.byKey(const ValueKey('lan-address-select')),
    );
    expect(selector.items.keys, ['auto', '192.0.2.10', '192.0.2.11']);
    expect(field('绑定地址（auto 自动选择）'), findsNothing);
    final input = tester.widget<EditableText>(field('局域网 Token'));
    input.controller.selection = const TextSelection(
      baseOffset: 1,
      extentOffset: 5,
    );
    await tester.tap(
      find.byWidgetPredicate(
        (widget) =>
            widget is StewardIconButton && widget.label == '显示局域网 Token',
      ),
    );
    // Forui 的按压最短反馈有 100ms 单次计时器；等待真实点击反馈结束再检查刷新，
    // 不能在后续全为零时长 pump 时直接结束测试，也不取消或屏蔽产品计时器。
    await tester.pumpAndSettle();
    final before = input.controller.value;
    model.publish();
    await tester.pump();
    expect(input.controller.value, before);
    expect(
      tester.widget<EditableText>(field('局域网 Token')).obscureText,
      isFalse,
    );
    await tester.enterText(field('局域网 Token'), 'draft-token');
    input.controller.value = input.controller.value.copyWith(
      composing: const TextRange(start: 0, end: 5),
    );
    final composing = input.controller.value;
    model.resources['/local-api/config'] = {
      ...model.resources['/local-api/config']!,
      'token': 'external-new-token',
    };
    model.publish();
    await tester.pump();
    expect(input.controller.value, composing);
    // 当前保存地址离开真实列表后，不能作为虚构选项继续提交。
    model.resources['/local-api/config'] = {
      ...model.resources['/local-api/config']!,
      'availableLanEndpoints': <DataMap>[],
    };
    model.publish();
    await tester.pump();
    expect(tester.widget<StewardButton>(button('保存服务配置')).onPressed, isNull);
    expect(model.lanWrites, isEmpty);
    await tester.pumpWidget(const SizedBox.shrink());
    await tester.pumpAndSettle();
  });

  for (final width in [627.0, 390.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('网卡弹层 $width px/$scale 完整换行并在视口内选择精确地址', (tester) async {
        // 小视口沿用桌面模型只验证 LAN 管理组件本身；不会假装手机可管理本机
        // 网卡。夹具故意使用较长接口名，确保既测到 IP 后正文，也测到真实换行。
        final model = _SettingsModel();
        addTearDown(model.dispose);
        const name = '合成高速以太网适配器（独立网络与远程工作站专用接口）';
        const option = '192.0.2.11 · $name';
        model.resources['/local-api/config'] = {
          ...model.resources['/local-api/config']!,
          'availableLanEndpoints': [
            {'address': '192.0.2.10', 'interfaceName': '合成以太网'},
            {'address': '192.0.2.11', 'interfaceName': name},
          ],
        };
        await open(
          tester,
          model,
          width: width,
          scale: scale,
          content: () => ListView(children: [SettingsLanSection(model)]),
        );
        final select = find.byKey(const ValueKey('lan-address-select'));
        final anchorBefore = tester.getRect(select);
        await tester.tap(select);
        await tester.pumpAndSettle();
        final item = find.byWidgetPredicate(
          (widget) =>
              widget is FSelectItem<String> && widget.value == '192.0.2.11',
        );
        expect(item, findsOneWidget);
        final bounds = tester.getRect(item);
        expect(bounds.width, greaterThan(300));
        expect(bounds.width, lessThanOrEqualTo(360));
        expect(bounds.left, greaterThanOrEqualTo(0));
        expect(bounds.right, lessThanOrEqualTo(width));
        expect(tester.getRect(select), anchorBefore);
        final title = find.descendant(of: item, matching: find.text(option));
        final paragraph = renderedParagraph(tester, title);
        expect(paragraph.maxLines, isNull);
        expect(paragraph.didExceedMaxLines, isFalse);
        expect(paragraph.overflow, TextOverflow.visible);
        // 真实最后一个字必须有布局框且仍落在项目内；单纯 find.text 只能证明
        // 数据存在，无法发现 Forui 默认 ellipsis 已把网卡名截断的原生回归。
        final lastGlyph = paragraph.getBoxesForSelection(
          TextSelection(
            baseOffset: option.length - 1,
            extentOffset: option.length,
          ),
        );
        expect(lastGlyph, isNotEmpty);
        final lastBounds = lastGlyph.last.toRect().shift(
          paragraph.localToGlobal(Offset.zero),
        );
        expect(lastBounds.left, greaterThanOrEqualTo(bounds.left));
        expect(lastBounds.right, lessThanOrEqualTo(bounds.right));
        expect(lastBounds.bottom, lessThanOrEqualTo(bounds.bottom));
        final allGlyphs = paragraph.getBoxesForSelection(
          TextSelection(baseOffset: 0, extentOffset: option.length),
        );
        expect(allGlyphs.map((box) => box.top).toSet().length, greaterThan(1));
        await tester.tap(item);
        await tester.pumpAndSettle();
        expect(
          find.byWidgetPredicate((widget) => widget is FSelectItem),
          findsNothing,
        );
        expect(
          tester.widget<StewardSelect<String>>(select).value,
          '192.0.2.11',
        );
        expect(model.lanWrites, isEmpty);
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox.shrink());
        await tester.pumpAndSettle();
      });
    }
  }

  testWidgets('未配置宽弹层的普通选择器仍保持152px上限', (tester) async {
    final model = _SettingsModel();
    addTearDown(model.dispose);
    await open(
      tester,
      model,
      content: () => ListView(
        children: [
          StewardSelect<int>(
            key: const ValueKey('ordinary-select'),
            label: '普通选项',
            items: const {1: '原有短选项', 2: '另一项'},
            value: 1,
            onChanged: (_) {},
          ),
        ],
      ),
    );
    await tester.tap(find.byKey(const ValueKey('ordinary-select')));
    await tester.pumpAndSettle();
    final item = find.byWidgetPredicate(
      (widget) => widget is FSelectItem<int> && widget.value == 2,
    );
    expect(tester.getSize(item).width, lessThanOrEqualTo(stewardSelectWidth));
    await tester.tap(item);
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
  });

  testWidgets('Token格式错误不提交，确认后仅发送明确新Token与所选网卡', (tester) async {
    final model = _SettingsModel();
    addTearDown(model.dispose);
    await open(
      tester,
      model,
      content: () => ListView(children: [SettingsLanSection(model)]),
    );
    await tester.enterText(field('局域网 Token'), 'short');
    await tester.tap(button('保存服务配置'));
    await tester.pumpAndSettle();
    expect(model.lanWrites, isEmpty);
    expect(
      tester
          .widget<PageActivityReporter>(find.byType(PageActivityReporter))
          .activity
          ?.details,
      contains('8–128'),
    );
    await tester.enterText(field('局域网 Token'), 'new-synthetic-token');
    await tester.tap(button('保存服务配置'));
    await tester.pumpAndSettle();
    expect(model.lanWrites, isEmpty);
    expect(find.text('保存连接 Token'), findsOneWidget);
    await tester.tap(button('确认'));
    await tester.pumpAndSettle();
    expect(model.lanWrites.single, (
      enabled: true,
      host: '192.0.2.10',
      token: 'new-synthetic-token',
    ));
    expect(tester.takeException(), isNull);
  });

  testWidgets('首次替换模型也撤回上一宿主的LAN密码草稿', (tester) async {
    final original = _SettingsModel();
    final replacement = _SettingsModel()..endpoint = 'http://192.0.2.30:32145';
    replacement.resources['/local-api/config'] = {
      ...replacement.resources['/local-api/config']!,
      'token': 'replacement-host-token',
    };
    addTearDown(original.dispose);
    addTearDown(replacement.dispose);
    var current = original;
    await open(
      tester,
      original,
      content: () => ListView(children: [SettingsLanSection(current)]),
    );
    await tester.enterText(field('局域网 Token'), 'unsaved-original-draft');
    current = replacement;
    original.publish();
    await tester.pump();
    expect(
      tester.widget<EditableText>(field('局域网 Token')).controller.text,
      'replacement-host-token',
    );
    expect(original.lanWrites, isEmpty);
    expect(replacement.lanWrites, isEmpty);
  });

  testWidgets('诊断仅展示真实本次运行记录，日志范围1至30且包删除使用精确身份', (tester) async {
    final model = _SettingsModel()..notice = '这只是瞬时通知，不能当作操作历史';
    model.recentOperations = [
      WorkbenchOperation(
        id: 4,
        name: '保存日志设置',
        startedAt: DateTime.utc(2026, 10, 21, 10),
        completedAt: DateTime.utc(2026, 10, 21, 10, 0, 1),
        result: WorkbenchOperationResult.unknown,
        summary: '请求结果尚未确认，请刷新核对。',
      ),
    ];
    addTearDown(model.dispose);
    await open(tester, model);
    await tester.tap(find.text('诊断'));
    await tester.pumpAndSettle();
    expect(model.diagnosticsReads, 1);
    final count = tester.widget<IntegerSetting>(
      find.byKey(const ValueKey('aggregateModLogMaxFileCount')),
    );
    expect((count.min, count.max), (1, 30));
    expect(find.text('启动时显示控制台'), findsNothing);
    await reveal(tester, find.text('最近操作 · 本次运行'));
    expect(find.text('保存日志设置'), findsOneWidget);
    expect(find.text('结果待确认'), findsOneWidget);
    expect(find.text(model.notice), findsNothing);
    final delete = find.byKey(
      const ValueKey('delete-package-opaque-synthetic-package-v1'),
    );
    await reveal(tester, delete);
    await tester.tap(delete);
    await tester.pumpAndSettle();
    await tester.tap(button('确认'));
    await tester.pumpAndSettle();
    expect(model.writes.single.$1, '/logs/packages/delete');
    expect(model.writes.single.$3, {'id': 'opaque-synthetic-package-v1'});
  });

  testWidgets('诊断包确认期间替换或重复身份取消删除，查看设备不可执行', (tester) async {
    final model = _SettingsModel();
    addTearDown(model.dispose);
    await open(
      tester,
      model,
      content: () => ListView(children: [SettingsDiagnosticPackages(model)]),
    );
    await tester.tap(button('删除'));
    await tester.pumpAndSettle();
    model.resources['/logs/packages'] = {
      'items': [
        {..._SettingsModel.package, 'byteLength': 4096},
      ],
    };
    model.publish();
    await tester.pump();
    await tester.tap(button('确认'));
    await tester.pumpAndSettle();
    expect(model.writes, isEmpty);
    model.resources['/logs/packages'] = {
      'items': [_SettingsModel.package, _SettingsModel.package],
    };
    model.publish();
    await tester.pump();
    for (final entry in tester.widgetList<StewardButton>(button('删除'))) {
      expect(entry.onPressed, isNull);
    }
    model.resources['/logs/packages'] = {
      'items': [_SettingsModel.package],
    };
    model.authority = {...model.authority, 'currentDeviceIsPrimary': false};
    model.publish();
    await tester.pump();
    expect(tester.widget<StewardButton>(button('删除')).onPressed, isNull);
  });

  testWidgets('诊断包首次失败、空列表刷新失败与重试成功明确区分', (tester) async {
    final model = _SettingsModel(desktop: false);
    final channel = PageActivityChannel();
    addTearDown(model.dispose);
    addTearDown(channel.dispose);
    model.resources['/logs/packages'] = {
      'ok': false,
      'error': '诊断包读取失败，请刷新重试。',
      'items': <DataMap>[],
    };
    await open(
      tester,
      model,
      width: 390,
      scale: 1.5,
      content: () => PageActivityScope(
        channel: channel,
        child: ListView(children: [SettingsDiagnosticPackages(model)]),
      ),
    );
    expect(find.text('诊断包读取失败'), findsOneWidget);
    expect(find.text('暂无诊断包'), findsNothing);
    expect(channel.value?.error, isTrue);
    expect(channel.value?.details, contains('刷新列表'));

    model.resources['/logs/packages'] = {'ok': true, 'items': <DataMap>[]};
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('暂无诊断包'), findsOneWidget);
    expect(channel.value, isNull);

    // 已经成功读过空列表，也不能把下一次失败当作“仍然为空”；旧 items 若被
    // 其它模型保留，同样不能继续给出删除动作，避免操作未经重新确认的文件。
    model.resources['/logs/packages'] = {
      'ok': false,
      'error': '目录暂不可读取。',
      'items': [_SettingsModel.package],
    };
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('诊断包读取失败'), findsOneWidget);
    expect(find.text('暂无诊断包'), findsNothing);
    expect(button('删除'), findsNothing);
    expect(channel.value?.details, contains('目录暂不可读取'));
    await tester.tap(button('刷新列表'));
    await tester.pumpAndSettle();
    expect(model.reads.last, '/logs/packages');
    expect(model.writes, isEmpty);
    model.resources['/logs/packages'] = {'ok': true, 'items': <DataMap>[]};
    model.publish();
    await tester.pumpAndSettle();
    expect(find.text('暂无诊断包'), findsOneWidget);
    expect(find.text('诊断包读取失败'), findsNothing);
    expect(channel.value, isNull);
    expect(tester.takeException(), isNull);
  });

  for (final desktop in [true, false]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('设置管理 ${desktop ? 627 : 390}px/$scale 操作可达且输入不横向越界', (
        tester,
      ) async {
        final model = _SettingsModel(desktop: desktop);
        addTearDown(model.dispose);
        await open(tester, model, width: desktop ? 627 : 390, scale: scale);
        await tester.tap(button('修改连接'));
        await tester.pumpAndSettle();
        final rect = tester.getRect(field('连接 Token'));
        expect(rect.width, greaterThan(150));
        expect(rect.left, greaterThanOrEqualTo(desktop ? 104 : 16));
        expect(
          rect.right,
          lessThanOrEqualTo((desktop ? 627 : 390) - (desktop ? 24 : 16)),
        );
        await reveal(tester, button('保存并重新连接'));
        expect(tester.getRect(button('保存并重新连接')).center.dy, lessThan(1000));
        if (desktop) {
          await reveal(tester, button('保存服务配置'));
          expect(tester.getRect(button('保存服务配置')).center.dy, lessThan(1000));
        }
        expect(tester.takeException(), isNull);
      });
    }
  }
}
