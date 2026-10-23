import 'package:flutter/widgets.dart';
import 'package:flutter/services.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/entity_image.dart';
import '../ui/page_activity.dart';
import '../ui/page_toolbar.dart';
import '../ui/steward_select.dart';
import '../ui/steward_style.dart';

const _categories = {
  'food': '料理',
  'beverage': '酒水',
  'ingredient': '食材',
  'cooker': '厨具',
  'rare-guest': '稀客与人物',
  'normal-group': '普通客群',
};

/// 启动缓存核验、等待目录、实际提取及人工重读共用一个独立门禁面板，状态变化
/// 只更新面板内部内容，不在工作台页头与初始化界面之间来回切换。
/// 此处不会挂载经营/设置写入控件；同代 ready 的普通轮询不会进入本组件。
/// 进度取自 Mod，不用本机固定倒计时推测完成；失败保留完整错误和主设备重试入口。
class GameMediaInitialization extends StatelessWidget {
  const GameMediaInitialization(this.model, {this.previewControls, super.key});
  final WorkbenchModel model;
  final Widget? previewControls;

  @override
  Widget build(BuildContext context) {
    final status = model.gameMediaStatus;
    final phase = status['phase'];
    final failed = status['phase'] == 'failed';
    final completed = status['completed'] as int? ?? 0;
    final total = status['total'] as int? ?? 0;
    final content = ColoredBox(
      color: context.theme.colors.background,
      child: SafeArea(
        child: Column(
          children: [
            if (previewControls != null) previewControls!,
            Expanded(
              child: Center(
                child: SingleChildScrollView(
                  padding: const EdgeInsets.all(24),
                  child: ConstrainedBox(
                    constraints: const BoxConstraints(maxWidth: 480),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Row(
                          children: [
                            Icon(
                              failed
                                  ? FLucideIcons.circleAlert
                                  : FLucideIcons.images,
                              size: 28,
                            ),
                            const SizedBox(width: 12),
                            const Expanded(
                              child: Text(
                                '初始化游戏图像资源',
                                style: StewardText.sectionTitle,
                              ),
                            ),
                          ],
                        ),
                        const SizedBox(height: 16),
                        Semantics(
                          liveRegion: true,
                          child: Text(
                            label(status['message'], '正在准备游戏图像资源'),
                            key: const ValueKey('game-media-phase-message'),
                            style: StewardText.body,
                          ),
                        ),
                        const SizedBox(height: 12),
                        if (!failed) ...[
                          if (total > 0)
                            FDeterminateProgress(
                              value: (completed / total).clamp(0, 1),
                            )
                          else
                            const FProgress(),
                          const SizedBox(height: 8),
                          Text(
                            total > 0
                                ? '$completed / $total'
                                : switch (phase) {
                                    'verifying' => '正在核验缓存与资源目录',
                                    'extracting' => '正在读取游戏图像',
                                    _ => '等待游戏目录就绪',
                                  },
                            style: StewardText.caption,
                          ),
                        ],
                        // 正常阶段只保留服务端阶段信息与实际进度，不重复显示固定说明。
                        // 失败详情和重试入口仍需保留，不能为了精简而隐藏可处理的问题。
                        if (failed) ...[
                          const SizedBox(height: 12),
                          Text(
                            label(status['error'], '读取失败，请重试。'),
                            style: StewardText.body,
                          ),
                        ],
                        if (failed || model.desktop) ...[
                          const SizedBox(height: 20),
                          ToolRow([
                            if (failed && model.primary)
                              StewardButton(
                                '重新读取',
                                primary: true,
                                onPressed: model.busy
                                    ? null
                                    : model.reloadGameMedia,
                              ),
                            if (failed)
                              StewardButton(
                                '刷新进度',
                                onPressed: model.busy ? null : model.refresh,
                              ),
                            if (model.desktop)
                              StewardButton(
                                '退出客户端',
                                onPressed: () => model.platformCommand('exit'),
                              ),
                          ]),
                        ],
                        if (failed && !model.primary) ...[
                          const SizedBox(height: 12),
                          const Text(
                            '请在主设备重新读取资源；本设备会自动同步进度。',
                            style: StewardText.caption,
                          ),
                        ],
                      ],
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      ),
    );
    if (model.desktop) return content;
    // 门禁不挂载普通工作区，必须独立设置手机系统栏，避免浅色初始化页继承白色图标。
    final background = context.theme.colors.background;
    final brightness = model.dark ? Brightness.light : Brightness.dark;
    return AnnotatedRegion<SystemUiOverlayStyle>(
      value: SystemUiOverlayStyle(
        statusBarColor: background,
        statusBarIconBrightness: brightness,
        systemNavigationBarColor: background,
        systemNavigationBarIconBrightness: brightness,
        systemStatusBarContrastEnforced: false,
        systemNavigationBarContrastEnforced: false,
      ),
      child: content,
    );
  }
}

/// 分类预览当前宿主已经验证的资源。只为可见单元请求图片，不在打开页面时下载全库。
/// 资源重读沿用主设备权威修订；不会改游戏数据、配方或用户的业务设置。
class ResourceManagementPage extends StatefulWidget {
  const ResourceManagementPage(this.model, {super.key});
  final WorkbenchModel model;
  @override
  State<ResourceManagementPage> createState() => _ResourceManagementPageState();
}

class _ResourceManagementPageState extends State<ResourceManagementPage> {
  String _category = 'all';
  final _search = TextEditingController();

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final model = widget.model, status = widget.model.gameMediaStatus;
    // 搜索只筛选已校验清单，不为了匹配而下载图片；编号和名称均来自同一代资源。
    // 控制器保留输入法组合文本，分类切换或资源刷新不会覆盖用户正在输入的内容。
    final query = _search.text.trim().toLowerCase();
    final entries = model.gameMediaItems
        .where((entry) => _category == 'all' || entry['kind'] == _category)
        .where(
          (entry) =>
              query.isEmpty ||
              '${entry['name']} ${entry['id']} ${entry['key'] ?? ''}'
                  .toLowerCase()
                  .contains(query),
        )
        .toList();
    final failed = status['failedImages'] as int? ?? 0;
    return PageActivityReporter(
      activity: !model.connected
          ? const PageActivity(message: '连接游戏电脑后查看资源')
          : failed > 0
          ? PageActivity(
              message: '$failed 张图片未能载入',
              error: true,
              details: '图片校验或读取失败，可重新读取游戏资源后重试。',
            )
          : PageActivity(
              message: query.isEmpty
                  ? '已初始化 ${model.gameMediaItems.length} 张图片'
                  : '匹配 ${entries.length} / ${model.gameMediaItems.length} 张图片',
            ),
      child: LayoutBuilder(
        builder: (context, constraints) {
          final scale = MediaQuery.textScalerOf(context).scale(1);
          final columns = (constraints.maxWidth / (145 * scale)).floor().clamp(
            1,
            6,
          );
          // 工具条也属于同一滚动面，手机横屏/软键盘的短高度不会挤出固定顶栏。
          // 分类数量直接进入下拉名称，避免单独计数占据窄屏水平空间。
          return CustomScrollView(
            key: PageStorageKey('resource-gallery-$_category'),
            slivers: [
              SliverToBoxAdapter(
                child: PageFilterRow(
                  // 两个输入控件共享剩余宽度，重读按钮保留完整文字；窄屏、大字号
                  // 都维持一行，宽屏则限制输入宽度，避免搜索框占据整个画廊。
                  children: [
                    Flexible(
                      child: ConstrainedBox(
                        constraints: const BoxConstraints(
                          maxWidth: stewardSelectWidth,
                        ),
                        child: StewardSelect<String>(
                          label: '资源分类',
                          showLabel: false,
                          value: _category,
                          items: {
                            'all': '全部资源（${model.gameMediaItems.length}）',
                            for (final category in _categories.entries)
                              category.key:
                                  '${category.value}（${model.gameMediaItems.where((item) => item['kind'] == category.key).length}）',
                          },
                          onChanged: (value) =>
                              setState(() => _category = value),
                        ),
                      ),
                    ),
                    Flexible(
                      child: ConstrainedBox(
                        constraints: const BoxConstraints(maxWidth: 152),
                        child: StewardField(
                          '搜索名称或编号',
                          key: const ValueKey('resource-media-search'),
                          showLabel: false,
                          hint: '搜索名称或编号',
                          controller: _search,
                          onChanged: (_) => setState(() {}),
                        ),
                      ),
                    ),
                    StewardButton(
                      '重新读取',
                      icon: FLucideIcons.refreshCw,
                      onPressed: model.connected && model.primary && !model.busy
                          ? model.reloadGameMedia
                          : null,
                    ),
                  ],
                ),
              ),
              if (entries.isEmpty)
                SliverFillRemaining(
                  hasScrollBody: false,
                  child: EmptyState(
                    model.connected
                        ? query.isEmpty
                              ? '当前分类没有可用图片'
                              : '没有匹配的图片'
                        : '连接游戏电脑后可查看图片资源',
                    model.connected
                        ? query.isEmpty
                              ? '资源由 Mod 从本机游戏读取。游戏更新后可使用“重新读取”更新。'
                              : '试试其他名称、编号，或切换至全部资源。'
                        : '请先到“连接与设备”设置游戏电脑地址。',
                  ),
                )
              else
                SliverGrid.builder(
                  gridDelegate: SliverGridDelegateWithFixedCrossAxisCount(
                    crossAxisCount: columns,
                    mainAxisExtent: 132 * scale,
                    crossAxisSpacing: 10,
                    mainAxisSpacing: 10,
                  ),
                  itemCount: entries.length,
                  itemBuilder: (context, index) {
                    final entry = entries[index];
                    final kind = EntityImageKind.values.singleWhere(
                      (kind) => kind.namespace == entry['kind'],
                    );
                    return DecoratedBox(
                      decoration: BoxDecoration(
                        border: Border.all(color: context.theme.colors.border),
                        borderRadius: BorderRadius.circular(6),
                      ),
                      child: Padding(
                        padding: const EdgeInsets.all(8),
                        child: Column(
                          children: [
                            EntityImage(
                              kind: kind,
                              id: entry['id'] as int,
                              fallback: FLucideIcons.image,
                              size: 64,
                            ),
                            const SizedBox(height: 6),
                            Text(
                              label(entry['name']),
                              style: StewardText.body,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                            ),
                            Text(
                              '编号 ${entry['id']}',
                              style: StewardText.caption,
                            ),
                          ],
                        ),
                      ),
                    );
                  },
                ),
            ],
          );
        },
      ),
    );
  }
}
