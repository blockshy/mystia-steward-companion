import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' as material;
import 'package:mystia_steward_companion/features/help_browser.dart';
import 'package:mystia_steward_companion/ui/page_activity.dart';
import 'package:mystia_steward_companion/ui/steward_theme.dart';

const _asset = 'assets/help-content.json';

/// 测试只读取随包帮助资产；不初始化平台、不建立连接、不访问用户配置。
void main() {
  final binding = TestWidgetsFlutterBinding.ensureInitialized();
  WidgetController.hitTestWarningShouldBeFatal = true;
  late String assetSource;
  late List<Map<String, Object?>> articles;
  late Map<String, String> articleCategories;

  setUpAll(() async {
    final path = Platform.environment['MYSTIA_UI_FONT'];
    if (path != null) {
      final bytes = await File(path).readAsBytes();
      await (FontLoader(
        'Microsoft YaHei',
      )..addFont(Future.value(ByteData.sublistView(bytes)))).load();
    }
    assetSource = await rootBundle.loadString(_asset);
    final document = jsonDecode(assetSource) as Map<String, Object?>;
    articles = [
      for (final category in document['categories']! as List<Object?>)
        for (final article
            in (category! as Map<String, Object?>)['items']! as List<Object?>)
          article! as Map<String, Object?>,
    ];
    articleCategories = {
      for (final raw in document['categories']! as List<Object?>)
        for (final article
            in (raw! as Map<String, Object?>)['items']! as List<Object?>)
          (article! as Map<String, Object?>)['id']! as String:
              (raw as Map<String, Object?>)['id']! as String,
    };
  });

  Widget fixture(
    double width,
    double scale, {
    bool active = true,
    double? contentHeight,
  }) {
    final theme = stewardTheme(dark: width < 640, touch: width < 640);
    return material.MaterialApp(
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
      // 模拟产品始终存在的80px主导航及两侧24px间距，390px不能以满屏宽度冒充内容区。
      home: Padding(
        padding: const EdgeInsets.fromLTRB(104, 20, 24, 20),
        child: contentHeight == null
            ? TickerMode(enabled: active, child: const HelpBrowser())
            : Align(
                alignment: Alignment.topLeft,
                child: SizedBox(
                  width: double.infinity,
                  height: contentHeight,
                  child: TickerMode(
                    enabled: active,
                    child: const HelpBrowser(),
                  ),
                ),
              ),
      ),
    );
  }

  Future<void> open(
    WidgetTester tester, {
    double width = 1280,
    double scale = 1,
    bool wait = true,
    double? contentHeight,
  }) async {
    tester.view.devicePixelRatio = 1;
    tester.view.physicalSize = Size(width, 760);
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await tester.pumpWidget(
      fixture(width, scale, contentHeight: contentHeight),
    );
    if (wait) {
      // 等待产品自己的真实资产Future，不复制常量或以假时钟延迟假装加载成功。
      final loader = tester.widget<FutureBuilder<dynamic>>(
        find.byKey(const ValueKey('help-document-loader')),
      );
      await tester.runAsync(() async {
        await loader.future;
      });
      await tester.pumpAndSettle();
    }
  }

  Finder scrollOf(Finder owner) => find.descendant(
    of: owner,
    matching: find.byWidgetPredicate(
      (widget) =>
          widget is Scrollable && widget.axisDirection == AxisDirection.down,
    ),
  );

  Future<void> read(WidgetTester tester, String id) async {
    final item = find.byKey(ValueKey('help-item-$id'));
    // 目录默认收起；先通过真实分类按钮展开，不让测试依赖旧的全展开状态。
    if (item.evaluate().isEmpty) {
      final category = find.byKey(
        ValueKey('help-category-${articleCategories[id]}'),
      );
      await tester.scrollUntilVisible(
        category,
        160,
        scrollable: scrollOf(
          find.byKey(const PageStorageKey('help-directory-scroll')),
        ),
      );
      await tester.pumpAndSettle();
      await tester.tap(category);
      await tester.pumpAndSettle();
    }
    await tester.scrollUntilVisible(
      item,
      160,
      scrollable: scrollOf(
        find.byKey(const PageStorageKey('help-directory-scroll')),
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(item);
    await tester.pumpAndSettle();
  }

  testWidgets('目录默认全收起，搜索临时展开且清除后恢复手动展开状态', (tester) async {
    await open(tester);
    Finder entries() => find.byWidgetPredicate(
      (widget) =>
          widget is FButton &&
          widget.key is ValueKey<String> &&
          (widget.key! as ValueKey<String>).value.startsWith('help-item-'),
    );
    expect(entries(), findsNothing);
    final search = find.descendant(
      of: find.byKey(const ValueKey('help-search')),
      matching: find.byType(EditableText),
    );
    await tester.enterText(search, articles.first['title']! as String);
    await tester.pumpAndSettle();
    expect(entries(), findsWidgets);
    await tester.tap(find.text('清除'));
    await tester.pumpAndSettle();
    expect(entries(), findsNothing);
    await read(tester, 'install');
    final manuallyExpanded = entries().evaluate().length;
    expect(manuallyExpanded, greaterThan(0));
    await tester.enterText(search, '完全不存在的帮助项');
    await tester.pumpAndSettle();
    await tester.tap(find.text('清除'));
    await tester.pumpAndSettle();
    expect(entries().evaluate().length, manuallyExpanded);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  for (final sample in [(860.0, 75.0), (860.0, 150.0), (390.0, 150.0)]) {
    testWidgets('帮助短视口 $sample /150% 搜索目录正文和返回全部可达', (tester) async {
      await open(
        tester,
        width: sample.$1,
        scale: 1.5,
        contentHeight: sample.$2,
      );
      final directoryList = find.byKey(
        const PageStorageKey('help-directory-scroll'),
      );
      expect(
        tester.getSize(directoryList).height,
        sample.$2,
        reason: '短视口的搜索、计数和标题全部滚动，不再预占目录高度',
      );
      final search = find.descendant(
        of: find.byKey(const ValueKey('help-search')),
        matching: find.byType(EditableText),
      );
      await tester.enterText(search, '安装');
      await tester.pumpAndSettle();
      expect(tester.takeException(), isNull);
      await read(tester, 'install');
      final articleList = find.byKey(
        const PageStorageKey('help-article-scroll-install'),
      );
      expect(
        tester.getSize(articleList).height,
        sample.$2,
        reason: '短视口的返回按钮不得固定占满正文',
      );
      await tester.scrollUntilVisible(
        find.text('操作步骤'),
        100,
        scrollable: scrollOf(articleList),
      );
      await tester.pumpAndSettle();
      expect(find.text('操作步骤').hitTestable(), findsOneWidget);
      expect(tester.takeException(), isNull);
      if (sample.$1 < 720) {
        final back = find.byKey(const ValueKey('help-back'));
        await tester.scrollUntilVisible(
          back,
          -100,
          scrollable: scrollOf(articleList),
        );
        await tester.pumpAndSettle();
        expect(back.hitTestable(), findsOneWidget);
        await tester.tap(back);
        await tester.pumpAndSettle();
        expect(find.byKey(const ValueKey('help-directory')), findsOneWidget);
      }
      await tester.ensureVisible(find.text('清除'));
      await tester.pumpAndSettle();
      expect(find.text('清除').hitTestable(), findsOneWidget);
      expect(tester.widget<EditableText>(search).controller.text, '安装');
      await tester.enterText(search, '不存在的帮助条目-短视口');
      await tester.pumpAndSettle();
      expect(find.text('没有匹配的帮助内容'), findsWidgets);
      expect(find.byKey(const ValueKey('help-reader')), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.tap(find.text('清除'));
      await tester.pumpAndSettle();
      // 搜索只临时展开分类，短高度和返回过程都不能把默认目录改成全展开。
      expect(find.byKey(const ValueKey('help-item-install')), findsNothing);
      expect(tester.takeException(), isNull);
      await tester.pumpWidget(const SizedBox());
    });
  }

  for (final width in [860.0, 390.0]) {
    testWidgets('帮助键盘压缩跨高度阈值保留输入状态与焦点 $width', (tester) async {
      await open(tester, width: width, scale: 1.5, contentHeight: 500);
      final search = find.descendant(
        of: find.byKey(const ValueKey('help-search')),
        matching: find.byType(EditableText),
      );
      await tester.enterText(search, '安装');
      await tester.pumpAndSettle();
      final editable = tester.state<EditableTextState>(search);
      final input = tester.widget<EditableText>(search);
      const draft = TextEditingValue(
        text: '安装',
        selection: TextSelection.collapsed(offset: 2),
        composing: TextRange(start: 0, end: 2),
      );
      input.controller.value = draft;
      await tester.pump();
      expect(input.focusNode.hasFocus, isTrue);
      // 同一浏览器在键盘弹出/收回时只换约束；搜索子树、FocusNode和中文组合态均须保留。
      for (final height in [75.0, 150.0, 500.0]) {
        await tester.pumpWidget(fixture(width, 1.5, contentHeight: height));
        await tester.pumpAndSettle();
        expect(
          identical(tester.state<EditableTextState>(search), editable),
          isTrue,
        );
        expect(
          identical(
            tester.widget<EditableText>(search).focusNode,
            input.focusNode,
          ),
          isTrue,
        );
        expect(input.focusNode.hasFocus, isTrue);
        expect(input.controller.value, draft);
        expect(tester.takeException(), isNull);
      }
      await tester.pumpWidget(const SizedBox());
    });
  }

  for (final width in [1280.0, 640.0, 390.0]) {
    for (final scale in [1.0, 1.5]) {
      testWidgets('真实帮助资产 $width px / $scale 倍字体目录与文章可达', (tester) async {
        await open(tester, width: width, scale: scale);
        expect(find.text('帮助加载失败'), findsNothing);
        await read(tester, 'install');
        final install = articles.singleWhere(
          (article) => article['id'] == 'install',
        );
        expect(
          tester
              .widget<Text>(find.byKey(const ValueKey('help-article-title')))
              .data,
          install['title'],
        );
        expect(find.byType(material.SelectionArea), findsOneWidget);
        expect(find.text('操作步骤'), findsOneWidget);
        expect(find.text('1.'), findsOneWidget);
        final directory = find.byKey(const ValueKey('help-directory'));
        final articleList = find.byKey(
          const PageStorageKey('help-article-scroll-install'),
        );
        if (width == 1280) {
          expect(tester.getSize(directory).width, 250);
          expect(
            tester.getRect(articleList).left,
            greaterThan(tester.getRect(directory).right),
          );
        } else {
          expect(directory, findsNothing);
          expect(
            find.byKey(const ValueKey('help-back')).hitTestable(),
            findsOneWidget,
          );
        }
        final articleScroll = scrollOf(articleList);
        await tester.scrollUntilVisible(
          find.byKey(const ValueKey('help-next')),
          200,
          scrollable: articleScroll,
        );
        await tester.pumpAndSettle();
        final savedOffset = tester
            .state<ScrollableState>(articleScroll)
            .position
            .pixels;
        if (tester
                .state<ScrollableState>(articleScroll)
                .position
                .maxScrollExtent >
            0) {
          expect(savedOffset, greaterThan(0));
        }
        if (width != 1280) {
          await tester.tap(find.byKey(const ValueKey('help-back')));
          await tester.pumpAndSettle();
          expect(find.byKey(const ValueKey('help-directory')), findsOneWidget);
          await read(tester, 'install');
          expect(
            tester
                .state<ScrollableState>(scrollOf(articleList))
                .position
                .pixels,
            closeTo(savedOffset, 1),
          );
        }
        await tester.tap(find.byKey(const ValueKey('help-next')));
        await tester.pumpAndSettle();
        final next = articles[articles.indexOf(install) + 1];
        expect(
          tester
              .widget<Text>(find.byKey(const ValueKey('help-article-title')))
              .data,
          next['title'],
        );
        final nextList = find.byKey(
          PageStorageKey('help-article-scroll-${next['id']}'),
        );
        expect(
          tester.state<ScrollableState>(scrollOf(nextList)).position.pixels,
          0,
        );
        await tester.scrollUntilVisible(
          find.byKey(const ValueKey('help-previous')),
          220,
          scrollable: scrollOf(nextList),
        );
        await tester.pumpAndSettle();
        await tester.tap(find.byKey(const ValueKey('help-previous')));
        await tester.pumpAndSettle();
        // 返回上一篇保留已读位置，前后篇不会把目录滚动位置改为文章位置。
        expect(
          tester.state<ScrollableState>(scrollOf(articleList)).position.pixels,
          closeTo(savedOffset, 1),
        );
        expect(tester.takeException(), isNull);
        await tester.pumpWidget(const SizedBox());
      });
    }
  }

  testWidgets('全文搜索包含注意事项且空搜索可清除恢复选择', (tester) async {
    await open(tester);
    final warningArticle = articles.firstWhere(
      (article) => (article['warnings'] as List<Object?>? ?? []).isNotEmpty,
    );
    final warning =
        (warningArticle['warnings']! as List<Object?>).first! as String;
    final search = find.descendant(
      of: find.byKey(const ValueKey('help-search')),
      matching: find.byType(EditableText),
    );
    await tester.enterText(search, warning);
    await tester.pumpAndSettle();
    await read(tester, warningArticle['id']! as String);
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('help-article-title')))
          .data,
      warningArticle['title'],
    );
    final articleList = find.byKey(
      PageStorageKey('help-article-scroll-${warningArticle['id']}'),
    );
    await tester.scrollUntilVisible(
      find.text('注意事项'),
      200,
      scrollable: scrollOf(articleList),
    );
    await tester.pumpAndSettle();
    expect(find.text('注意事项').hitTestable(), findsOneWidget);
    await tester.enterText(search, '不存在的帮助条目-000000');
    await tester.pumpAndSettle();
    expect(find.text('没有匹配的帮助内容'), findsWidgets);
    expect(find.byKey(const ValueKey('help-reader')), findsNothing);
    await tester.tap(find.text('清除'));
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('help-article-title')))
          .data,
      warningArticle['title'],
    );
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('窄屏系统返回保留目录选择，隐藏帮助不拦截返回', (tester) async {
    await open(tester, width: 390, scale: 1.5);
    // 选择位于后半部分的真实条目，避免“默认第一篇”掩盖返回后丢失选中项的问题。
    await read(tester, 'inventory-edit');
    await tester.binding.handlePopRoute();
    await tester.pumpAndSettle();
    expect(find.byKey(const ValueKey('help-directory')), findsOneWidget);
    final selected = find.byKey(const ValueKey('help-item-inventory-edit'));
    expect(selected.hitTestable(), findsOneWidget);
    expect(tester.widget<FButton>(selected).variant, FButtonVariant.secondary);
    expect(
      tester
          .state<ScrollableState>(
            scrollOf(find.byKey(const PageStorageKey('help-directory-scroll'))),
          )
          .position
          .pixels,
      greaterThan(0),
    );
    await tester.tap(selected);
    await tester.pumpAndSettle();
    expect(
      tester
          .widget<Text>(find.byKey(const ValueKey('help-article-title')))
          .data,
      articles.singleWhere(
        (article) => article['id'] == 'inventory-edit',
      )['title'],
    );
    await tester.pumpWidget(fixture(390, 1.5, active: false));
    await tester.pumpAndSettle();
    final scope = tester.widget<PopScope<Object?>>(
      find.byType(PopScope<Object?>),
    );
    expect(scope.canPop, isTrue);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('加载中、格式错误与重试使用真实资产重新恢复', (tester) async {
    final pending = Completer<ByteData?>();
    var requestCount = 0;
    rootBundle.evict(_asset);
    binding.defaultBinaryMessenger.setMockMessageHandler('flutter/assets', (
      message,
    ) async {
      final name = utf8.decode(
        message!.buffer.asUint8List(
          message.offsetInBytes,
          message.lengthInBytes,
        ),
      );
      if (name != _asset) return null;
      requestCount++;
      return requestCount == 1
          ? pending.future
          : ByteData.sublistView(Uint8List.fromList(utf8.encode(assetSource)));
    });
    addTearDown(() {
      binding.defaultBinaryMessenger.setMockMessageHandler(
        'flutter/assets',
        null,
      );
      rootBundle.evict(_asset);
    });
    await open(tester, width: 390, scale: 1.5, wait: false, contentHeight: 75);
    expect(find.text('正在加载帮助…'), findsNothing);
    expect(
      tester
          .widget<PageActivityReporter>(find.byType(PageActivityReporter))
          .activity,
      const PageActivity(message: '正在加载帮助…', busy: true),
    );
    expect(tester.takeException(), isNull);
    pending.complete(
      ByteData.sublistView(Uint8List.fromList(utf8.encode('{"broken":true}'))),
    );
    await tester.pumpAndSettle();
    final failure = tester
        .widget<PageActivityReporter>(find.byType(PageActivityReporter))
        .activity;
    expect(failure?.message, '帮助加载失败');
    expect(failure?.error, isTrue);
    expect(failure?.details, isNotEmpty);
    expect(find.text('帮助暂不可用'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.ensureVisible(find.text('重试加载帮助'));
    await tester.pumpAndSettle();
    expect(find.text('重试加载帮助').hitTestable(), findsOneWidget);
    // 重试会重新创建整个资产读取／UTF-8 isolate 解码链。必须在真实异步区
    // 触发点击，让 mock 回复及其后续 Future 也属于真实区；仅把最后 await
    // 移入 runAsync，无法推进已经在假时钟区创建的中间微任务，会互相等待。
    await tester.runAsync(() async {
      await tester.tap(find.text('重试加载帮助'));
    });
    await tester.pump();
    // 完整帮助资产会在真实异步环境解码。页头取代正文加载动画后，单纯推进
    // 帧直到静止不能证明读取已完成；必须等待产品重试实际创建的 Future。
    final retryLoader = tester.widget<FutureBuilder<dynamic>>(
      find.byKey(const ValueKey('help-document-loader')),
    );
    await tester.runAsync(() async {
      await retryLoader.future;
    });
    await tester.pumpAndSettle();
    expect(requestCount, 2);
    expect(find.byKey(const ValueKey('help-directory')), findsOneWidget);
    expect(
      tester
          .widget<PageActivityReporter>(find.byType(PageActivityReporter))
          .activity,
      isNull,
    );
    expect(find.text('帮助加载失败'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('合法空文档显示空状态而非无限加载', (tester) async {
    rootBundle.evict(_asset);
    binding.defaultBinaryMessenger.setMockMessageHandler(
      'flutter/assets',
      (_) async => ByteData.sublistView(
        Uint8List.fromList(
          utf8.encode(
            jsonEncode({
              'version': 1,
              'updatedAt': '2026-10-10',
              'intro': '',
              'categories': <Object?>[],
            }),
          ),
        ),
      ),
    );
    addTearDown(() {
      binding.defaultBinaryMessenger.setMockMessageHandler(
        'flutter/assets',
        null,
      );
      rootBundle.evict(_asset);
    });
    await open(tester, width: 860, scale: 1.5, contentHeight: 75);
    expect(find.text('暂无帮助内容'), findsOneWidget);
    expect(find.text('正在加载帮助…'), findsNothing);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}
