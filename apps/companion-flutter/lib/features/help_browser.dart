import 'dart:convert';

import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';
import 'package:material_ui/material_ui.dart' show SelectionArea;

import '../ui/components.dart';
import '../ui/page_activity.dart';
import '../ui/page_toolbar.dart';
import '../ui/steward_controls.dart';
import '../ui/steward_style.dart';
import '../ui/steward_opacity.dart';

/// 离线帮助浏览器由父级提供有限宽高；不依赖连接、设备身份或业务模型。
/// 目录与文章分别滚动，窄屏只呈现其中一栏，避免将整本文档堆入设置表单。
class HelpBrowser extends StatefulWidget {
  const HelpBrowser({super.key});

  @override
  State<HelpBrowser> createState() => _HelpBrowserState();
}

class _HelpBrowserState extends State<HelpBrowser> {
  static const _asset = 'assets/help-content.json';
  final _search = TextEditingController();
  // 短视口会将搜索从固定头部移入滚动内容。保留整行的元素身份，连同输入框
  // 内部 FocusNode 与 composing 状态一起搬移，避免键盘收放触发失焦或丢失草稿。
  final _searchRowKey = GlobalKey();
  final _directoryScroll = ScrollController();
  final _articleScroll = <String, ScrollController>{};
  // 仅记录用户主动展开的分类；空集合天然表示首次打开时全部收起。
  // 搜索临时展开命中分类，不写入此集合，清空搜索后恢复原有目录状态。
  final _expanded = <String>{};
  late Future<_HelpDocument> _document = _readDocument();
  String? _selectedId;
  bool _showArticle = false;

  Future<_HelpDocument> _readDocument() async =>
      _HelpDocument.parse(jsonDecode(await rootBundle.loadString(_asset)));

  @override
  void dispose() {
    _search.dispose();
    _directoryScroll.dispose();
    for (final controller in _articleScroll.values) {
      controller.dispose();
    }
    super.dispose();
  }

  void _retry() {
    // 失败也可能来自损坏的缓存内容；重试必须重新读取资产，不能复用上次失败的Future。
    rootBundle.evict(_asset);
    setState(() {
      _document = _readDocument();
    });
  }

  void _searchChanged(String _) {
    if (_directoryScroll.hasClients) _directoryScroll.jumpTo(0);
    setState(() {});
  }

  void _open(_HelpArticle article) {
    FocusManager.instance.primaryFocus?.unfocus();
    setState(() {
      _selectedId = article.id;
      _showArticle = true;
    });
  }

  void _backToDirectory() => setState(() => _showArticle = false);

  @override
  Widget build(BuildContext context) => FutureBuilder<_HelpDocument>(
    key: const ValueKey('help-document-loader'),
    future: _document,
    builder: (context, snapshot) => PageActivityReporter(
      // 帮助加载独立于连接状态。完整读取错误进入当前页头，重试仍直接重新读取
      // 本地资产；正文只保留简洁空态，不把文章中的说明/注意事项当作提示删除。
      activity: snapshot.hasError
          ? PageActivity(
              message: '帮助加载失败',
              details: '${snapshot.error}',
              error: true,
            )
          : snapshot.data == null
          ? const PageActivity(message: '正在加载帮助…', busy: true)
          : null,
      child: _documentBody(context, snapshot),
    ),
  );

  Widget _documentBody(
    BuildContext context,
    AsyncSnapshot<_HelpDocument> snapshot,
  ) {
    if (snapshot.hasError) {
      return Center(
        child: SingleChildScrollView(
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              const EmptyState('帮助暂不可用', ''),
              StewardButton('重试加载帮助', onPressed: _retry),
            ],
          ),
        ),
      );
    }
    final document = snapshot.data;
    if (document == null) {
      return const SizedBox.expand();
    }
    if (document.articles.isEmpty) {
      return const EmptyState('暂无帮助内容', '');
    }
    final query = _search.text.trim().toLowerCase();
    final visible = document.articles
        .where((article) => article.searchText.contains(query))
        .toList(growable: false);
    final selected =
        visible.where((article) => article.id == _selectedId).firstOrNull ??
        visible.firstOrNull;
    return LayoutBuilder(
      builder: (context, constraints) {
        // 以实际内容区而非窗口宽度决定双栏；设置目录和固定主导航也会占用宽度。
        final narrow = constraints.maxWidth < 720;
        // 横屏和软键盘可能只留下几十像素。此时全部头部进入滚动轴，不通过
        // 缩小字号或固定搜索/返回区域挤掉正文；普通桌面高度仍沿用固定头部。
        final short =
            constraints.maxHeight < MediaQuery.textScalerOf(context).scale(220);
        final reading = narrow && _showArticle && selected != null;
        final active = TickerMode.valuesOf(context).enabled;
        return PopScope<Object?>(
          // 隐藏的设置页不能截获其他页面的Android返回手势。
          canPop: !active || !reading,
          onPopInvokedWithResult: (didPop, _) {
            if (!didPop && active && reading) _backToDirectory();
          },
          child: Actions(
            actions: {
              DismissIntent: CallbackAction<DismissIntent>(
                onInvoke: (_) {
                  if (active && reading) _backToDirectory();
                  return null;
                },
              ),
            },
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                if (!short) _documentHeading(document),
                Expanded(
                  child: narrow
                      ? reading
                            ? _article(
                                document,
                                visible,
                                selected,
                                narrow: true,
                                short: short,
                              )
                            : _directory(
                                document,
                                visible,
                                selected,
                                query,
                                short: short,
                              )
                      : Row(
                          crossAxisAlignment: CrossAxisAlignment.stretch,
                          children: [
                            SizedBox(
                              width: 250,
                              child: _directory(
                                document,
                                visible,
                                selected,
                                query,
                                short: short,
                              ),
                            ),
                            const SizedBox(width: 16),
                            Expanded(
                              child: selected == null
                                  ? const EmptyState('没有匹配的帮助内容', '')
                                  : _article(
                                      document,
                                      visible,
                                      selected,
                                      narrow: false,
                                      short: short,
                                    ),
                            ),
                          ],
                        ),
                ),
              ],
            ),
          ),
        );
      },
    );
  }

  Widget _documentHeading(_HelpDocument document) => Padding(
    padding: const EdgeInsets.only(bottom: 8),
    child: Wrap(
      spacing: 12,
      runSpacing: 4,
      children: [
        const Text('帮助文档', style: TextStyle(fontWeight: FontWeight.w700)),
        Text(
          '更新 ${document.updatedAt}',
          style: TextStyle(color: context.theme.colors.mutedForeground),
        ),
      ],
    ),
  );

  Widget _directory(
    _HelpDocument document,
    List<_HelpArticle> visible,
    _HelpArticle? selected,
    String query, {
    required bool short,
  }) {
    final search = PageToolbar(
      key: _searchRowKey,
      controller: _search,
      searchLabel: '搜索帮助',
      searchHint: '搜索帮助关键词',
      searchKey: const ValueKey('help-search'),
      onSearchChanged: _searchChanged,
      actions: [
        if (_search.text.isNotEmpty)
          StewardButton(
            '清除',
            onPressed: () {
              _search.clear();
              _searchChanged('');
            },
          ),
      ],
    );
    final count = Padding(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Text(
        '目录 · ${visible.length} / ${document.articles.length} 篇',
        style: TextStyle(
          fontSize: 12,
          color: context.theme.colors.mutedForeground,
        ),
      ),
    );
    final entries = _directoryEntries(document, visible, selected, query);
    return Column(
      key: const ValueKey('help-directory'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (!short) ...[search, count],
        Expanded(
          child: short
              ? SingleChildScrollView(
                  key: const PageStorageKey('help-directory-scroll'),
                  controller: _directoryScroll,
                  padding: const EdgeInsets.only(right: 4, bottom: 12),
                  // 立即构建保证搜索始终保留在元素树，不因当前滚动位置或键盘
                  // 收放后的视口变化被懒加载回收。帮助分类数量有界，仍默认收起。
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      search,
                      count,
                      _documentHeading(document),
                      if (visible.isEmpty)
                        const EmptyState('没有匹配的帮助内容', '')
                      else
                        ...entries,
                    ],
                  ),
                )
              : visible.isEmpty
              ? const EmptyState('没有匹配的帮助内容', '')
              : ListView(
                  key: const PageStorageKey('help-directory-scroll'),
                  controller: _directoryScroll,
                  padding: const EdgeInsets.only(right: 4, bottom: 12),
                  children: entries,
                ),
        ),
      ],
    );
  }

  /// 分类、条目与折叠语义由两种高度模式共用，不维护第二套目录或搜索结果。
  List<Widget> _directoryEntries(
    _HelpDocument document,
    List<_HelpArticle> visible,
    _HelpArticle? selected,
    String query,
  ) => [
    if (document.intro.isNotEmpty)
      Padding(
        padding: const EdgeInsets.only(bottom: 12),
        child: Text(
          document.intro,
          style: TextStyle(
            fontSize: 12,
            color: context.theme.colors.mutedForeground,
          ),
        ),
      ),
    for (final category in document.categories)
      if (visible.any((article) => article.category == category)) ...[
        Semantics(
          header: true,
          child: FButton(
            key: ValueKey('help-category-${category.id}'),
            variant: FButtonVariant.ghost,
            // 目录动作与搜索、清除、返回共用单行度量；仅约束最小高度，
            // 长分类名仍可按当前中文字体和系统字号自然换行。
            style: stewardButtonStyle(context),
            mainAxisAlignment: MainAxisAlignment.start,
            prefix: Icon(
              query.isNotEmpty || _expanded.contains(category.id)
                  ? FLucideIcons.chevronDown
                  : FLucideIcons.chevronRight,
              size: 16,
            ),
            onPress: query.isNotEmpty
                ? null
                : () => setState(() {
                    if (!_expanded.remove(category.id))
                      _expanded.add(category.id);
                  }),
            child: Expanded(
              child: Text(
                category.title,
                style: const TextStyle(fontWeight: FontWeight.w700),
              ),
            ),
          ),
        ),
        // 搜索时强制展开命中分类，但不改变用户原有的折叠偏好。
        if (query.isNotEmpty || _expanded.contains(category.id))
          for (final article in visible.where(
            (article) => article.category == category,
          ))
            Padding(
              padding: const EdgeInsets.only(left: 10, bottom: 4),
              child: Semantics(
                selected: selected?.id == article.id,
                child: FButton(
                  key: ValueKey('help-item-${article.id}'),
                  variant: selected?.id == article.id
                      ? FButtonVariant.secondary
                      : FButtonVariant.ghost,
                  // 文章是可多行的列表动作，沿用公共字号/内边距而不锁死高度。
                  style: stewardButtonStyle(context),
                  mainAxisAlignment: MainAxisAlignment.start,
                  prefix: const Icon(FLucideIcons.fileText, size: 15),
                  onPress: () => _open(article),
                  child: Expanded(
                    child: Text(
                      article.title,
                      maxLines: 3,
                      overflow: TextOverflow.ellipsis,
                    ),
                  ),
                ),
              ),
            ),
      ],
  ];

  Widget _article(
    _HelpDocument document,
    List<_HelpArticle> visible,
    _HelpArticle article, {
    required bool narrow,
    required bool short,
  }) {
    final index = visible.indexOf(article);
    // 每篇文章各自保留阅读位置，窄屏返回目录或调整窗口宽度不会跳回文章开头。
    final scroll = _articleScroll.putIfAbsent(article.id, ScrollController.new);
    final back = Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Align(
        alignment: Alignment.centerLeft,
        child: StewardButton(
          '返回目录',
          key: const ValueKey('help-back'),
          icon: FLucideIcons.arrowLeft,
          onPressed: _backToDirectory,
        ),
      ),
    );
    return Column(
      key: const ValueKey('help-reader'),
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (narrow && !short) back,
        Expanded(
          child: SelectionArea(
            child: ListView(
              // PageStorageKey负责在目录/正文互相卸载后恢复对应ScrollPosition。
              // 仅保留ScrollController并不足以保留已脱离视口的阅读进度。
              key: PageStorageKey('help-article-scroll-${article.id}'),
              controller: scroll,
              padding: const EdgeInsets.fromLTRB(2, 0, 8, 16),
              children: [
                // 短视口返回入口与文章同轴滚动，不再固定占用剩余正文高度。
                // Android系统返回与DismissIntent仍由外层统一处理，不受滚动位置影响。
                if (narrow && short) back,
                if (narrow && short) _documentHeading(document),
                Text(
                  article.category.title,
                  style: TextStyle(
                    fontSize: 12,
                    color: context.theme.colors.mutedForeground,
                  ),
                ),
                const SizedBox(height: 6),
                Semantics(
                  header: true,
                  child: Text(
                    article.title,
                    key: const ValueKey('help-article-title'),
                    style: const TextStyle(
                      fontSize: 20,
                      fontWeight: FontWeight.w700,
                    ),
                  ),
                ),
                if (article.summary.isNotEmpty) ...[
                  const SizedBox(height: 10),
                  Text(article.summary),
                ],
                if (article.category.description.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  Text(
                    article.category.description,
                    style: TextStyle(
                      fontSize: 12,
                      color: context.theme.colors.mutedForeground,
                    ),
                  ),
                ],
                if (article.steps.isNotEmpty)
                  _block('操作步骤', article.steps, ordered: true),
                if (article.notes.isNotEmpty) _block('说明', article.notes),
                if (article.warnings.isNotEmpty)
                  _block('注意事项', article.warnings, warning: true),
                const SizedBox(height: 20),
                Text(
                  '第 ${index + 1} / ${visible.length} 篇${_search.text.trim().isEmpty ? '' : ' · 搜索结果'}',
                  style: TextStyle(
                    fontSize: 12,
                    color: context.theme.colors.mutedForeground,
                  ),
                ),
                if (index > 0)
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: StewardButton(
                      '上一篇：${visible[index - 1].title}',
                      key: const ValueKey('help-previous'),
                      onPressed: () => _open(visible[index - 1]),
                    ),
                  ),
                if (index + 1 < visible.length)
                  Padding(
                    padding: const EdgeInsets.only(top: 8),
                    child: StewardButton(
                      '下一篇：${visible[index + 1].title}',
                      key: const ValueKey('help-next'),
                      onPressed: () => _open(visible[index + 1]),
                    ),
                  ),
              ],
            ),
          ),
        ),
      ],
    );
  }

  Widget _block(
    String title,
    List<String> lines, {
    bool ordered = false,
    bool warning = false,
  }) => Container(
    margin: const EdgeInsets.only(top: 18),
    padding: warning ? const EdgeInsets.all(12) : EdgeInsets.zero,
    decoration: warning
        ? BoxDecoration(
            color: context.stewardPaint.surface(
              context.theme.colors.destructive,
              alpha: .06,
            ),
            border: Border(
              left: BorderSide(
                color: context.theme.colors.destructive,
                width: 3,
              ),
            ),
          )
        : null,
    child: Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Semantics(
          header: true,
          child: Text(
            title,
            style: TextStyle(
              fontWeight: FontWeight.w700,
              color: warning ? context.theme.colors.destructive : null,
            ),
          ),
        ),
        for (var index = 0; index < lines.length; index++)
          Padding(
            padding: const EdgeInsets.only(top: 8),
            child: StewardBaselineRow(
              children: [
                SizedBox(
                  width: 32,
                  child: Text(ordered ? '${index + 1}.' : '•'),
                ),
                Expanded(child: Text(lines[index])),
              ],
            ),
          ),
      ],
    ),
  );
}

/// 只接受帮助资产的明确结构；格式损坏时展示可重试错误，不静默丢弃部分文章。
class _HelpDocument {
  _HelpDocument(this.updatedAt, this.intro, this.categories);
  final String updatedAt, intro;
  final List<_HelpCategory> categories;
  late final articles = categories
      .expand((category) => category.articles)
      .toList(growable: false);

  static _HelpDocument parse(Object? raw) {
    final data = _map(raw);
    if (data['version'] is! int) throw const FormatException('帮助版本无效');
    final ids = <String>{};
    final categories = <_HelpCategory>[];
    for (final rawCategory in _list(data['categories'])) {
      final value = _map(rawCategory);
      final category = _HelpCategory(
        _text(value['id']),
        _text(value['title']),
        _text(value['description'], optional: true),
      );
      if (!ids.add('category:${category.id}'))
        throw const FormatException('帮助分类重复');
      for (final rawArticle in _list(value['items'])) {
        final item = _map(rawArticle);
        final article = _HelpArticle(
          category,
          _text(item['id']),
          _text(item['title']),
          _text(item['summary'], optional: true),
          _strings(item['steps']),
          _strings(item['notes']),
          _strings(item['warnings']),
        );
        if (!ids.add('article:${article.id}'))
          throw const FormatException('帮助条目重复');
        category.articles.add(article);
      }
      categories.add(category);
    }
    return _HelpDocument(
      _text(data['updatedAt']),
      _text(data['intro'], optional: true),
      categories,
    );
  }

  static Map<String, Object?> _map(Object? value) =>
      value is Map<String, Object?>
      ? value
      : throw const FormatException('帮助对象无效');
  static List<Object?> _list(Object? value) =>
      value is List<Object?> ? value : throw const FormatException('帮助列表无效');
  static String _text(Object? value, {bool optional = false}) {
    if (optional && value == null) return '';
    if (value is String && (optional || value.trim().isNotEmpty)) return value;
    throw const FormatException('帮助文字无效');
  }

  static List<String> _strings(Object? value) => value == null
      ? const []
      : _list(value).map((line) => _text(line)).toList(growable: false);
}

class _HelpCategory {
  _HelpCategory(this.id, this.title, this.description);
  final String id, title, description;
  final articles = <_HelpArticle>[];
}

class _HelpArticle {
  _HelpArticle(
    this.category,
    this.id,
    this.title,
    this.summary,
    this.steps,
    this.notes,
    this.warnings,
  );
  final _HelpCategory category;
  final String id, title, summary;
  final List<String> steps, notes, warnings;
  late final searchText = [
    category.title,
    category.description,
    title,
    summary,
    ...steps,
    ...notes,
    ...warnings,
  ].join('\n').toLowerCase();
}
