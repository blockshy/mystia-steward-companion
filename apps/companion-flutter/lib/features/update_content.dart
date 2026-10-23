import 'package:companion_platform/companion_platform.dart';
import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/steward_style.dart';

/// 服务端更新状态只在此处翻译为用户语言。安装阶段优先于下载状态，避免安装
/// 已在等待退出时仍显示“已下载”；未知值保留可读的兜底，不直接泄露协议枚举。
String updateStateLabel(DataMap state) {
  final installation = state['installState'];
  const installs = {
    'waiting': '安装程序已打开',
    'preparing': '正在准备安装',
    'closing-companion': '正在关闭客户端',
    'waiting-game': '等待游戏退出',
    'terminating-game': '正在关闭游戏',
    'game-closed': '游戏已退出',
    'backing-up': '正在备份',
    'installing': '正在安装',
    'verifying': '正在校验',
    'rolling-back': '正在恢复旧版本',
    'succeeded': '安装完成',
    'failed': '安装失败',
    'cancelled': '已取消安装',
  };
  if (installs.containsKey(installation)) return installs[installation]!;
  if (state.isEmpty) return '等待读取';
  if (state['enabled'] == false) return '更新已关闭';
  if (state['state'] == 'checking') return '正在检查';
  if (state['state'] == 'downloading') return '正在下载';
  if (state['staged'] == true) return '已下载';
  if (state['hasUpdate'] == true) return '有新版本';
  return const {
        'current': '已是最新版本',
        'installed': '安装完成',
        'failed': '检查或下载失败',
        'disabled': '更新已关闭',
      }[state['state']] ??
      '尚未检查';
}

bool updateIsBusy(DataMap state) {
  final install = label(state['installState'], '');
  return state['state'] == 'checking' ||
      state['state'] == 'downloading' ||
      install.isNotEmpty &&
          !{'succeeded', 'failed', 'cancelled'}.contains(install);
}

String _date(Object? value) {
  final date = value is String ? DateTime.tryParse(value)?.toLocal() : null;
  if (date == null) return '未记录';
  String pair(int number) => number.toString().padLeft(2, '0');
  return '${date.year}-${pair(date.month)}-${pair(date.day)} ${pair(date.hour)}:${pair(date.minute)}';
}

/// 更新页只接收服务端状态，检查/下载/安装仍通过已有模型权限和连接代次边界。
/// 版本说明和发布页面为只读操作，不要求用户先取得主设备身份。
class SettingsUpdatesSection extends StatelessWidget {
  const SettingsUpdatesSection(this.model, {super.key});
  final WorkbenchModel model;
  @override
  Widget build(BuildContext context) {
    final state =
        model.resources['/updates/status'] ?? const <String, Object?>{};
    final busy = model.busy || updateIsBusy(state);
    final enabled = state['enabled'] == true;
    final releases = records(state['availableReleases']);
    final latestUrl = label(state['releaseUrl'], '');
    final packageSize = state['packageSize'];
    final bytes = packageSize is num && packageSize > 0
        ? '${(packageSize / 1048576).toStringAsFixed(1)} MB'
        : '未读取';
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        Section(
          '成套更新',
          [
            Fact('当前版本', state['currentVersion']),
            Fact('最新版本', state['latestVersion']),
            Fact('更新状态', updateStateLabel(state)),
            if (label(state['installMessage'], '').isNotEmpty)
              Fact('安装进度', state['installMessage']),
            if (label(state['error'], '').isNotEmpty)
              Fact('更新错误', state['error']),
            ToolRow([
              StewardButton(
                '检查更新',
                onPressed: model.connected && !busy
                    ? () => model.mutate('/updates/check')
                    : null,
              ),
              StewardButton(
                '下载整包',
                onPressed:
                    model.canEdit &&
                        enabled &&
                        !busy &&
                        state['hasUpdate'] == true &&
                        state['staged'] != true
                    ? () => model.mutate('/updates/download')
                    : null,
              ),
              StewardButton(
                '退出游戏后安装',
                onPressed:
                    model.canEdit &&
                        model.desktop &&
                        enabled &&
                        !busy &&
                        state['staged'] == true
                    ? () async {
                        if (await confirmCurrentAction(
                          context,
                          model,
                          '安装更新',
                          '保存游戏进度并正常退出。独立更新程序会确认客户端退出、校验整包并在失败时恢复备份。',
                        )) {
                          await model.mutate(
                            '/updates/install-on-exit',
                            localOnly: true,
                          );
                        }
                      }
                    : null,
              ),
              StewardButton(
                '最新发布页',
                onPressed: CompanionPlatform.isProjectReleaseUrl(latestUrl)
                    ? () => model.openReleasePage(latestUrl)
                    : null,
              ),
            ]),
          ],
          titleContent: Row(
            children: [
              const Expanded(
                child: Text('成套更新', style: StewardText.sectionTitle),
              ),
              const SizedBox(width: 8),
              StewardButton(
                '刷新状态',
                onPressed: model.connected && !model.busy
                    ? () => model.readResource('/updates/status')
                    : null,
              ),
            ],
          ),
        ),
        CollapsibleSection('检查计划与安装包', [
          Fact(
            '自动检查',
            state.isEmpty
                ? '未读取'
                : state['autoCheck'] == true
                ? '已开启'
                : '已关闭',
          ),
          Fact(
            '更新通道',
            state.isEmpty
                ? '未读取'
                : state['includePrerelease'] == true
                ? '含预发布版本'
                : '仅正式版本',
          ),
          Fact('最近成功检查', _date(state['lastSuccessAtUtc'])),
          Fact(
            '下次检查',
            state['autoCheck'] == true && enabled
                ? _date(state['nextCheckAtUtc'])
                : '未计划',
          ),
          Fact('安装包大小', bytes),
          Fact('连续检查失败', state['consecutiveFailures'] ?? 0),
        ]),
        Section('版本更新内容', [
          if (state['hasUpdate'] != true)
            Text(
              state['state'] == 'current' ? '当前已经是最新版本。' : '发现新版本后显示更新说明。',
              style: StewardText.body,
            )
          else if (state['releaseHistoryState'] != 'ready' ||
              releases.isEmpty) ...[
            const Text('完整版本说明暂不可用，仍可下载和安装更新。', style: StewardText.body),
            if (label(state['releaseHistoryError'], '').isNotEmpty)
              Fact('说明读取详情', state['releaseHistoryError']),
          ] else
            for (var index = 0; index < releases.length; index++)
              CollapsibleSection(
                label(releases[index]['title'], label(releases[index]['tag'])),
                [
                  Fact('发布版本', releases[index]['tag']),
                  Fact('发布日期', _date(releases[index]['publishedAtUtc'])),
                  const SizedBox(height: StewardDensity.detailGroupGap),
                  ReleaseNotesContent(
                    label(releases[index]['notesMarkdown'], ''),
                  ),
                  ToolRow([
                    StewardButton(
                      '查看发布页',
                      onPressed:
                          CompanionPlatform.isProjectReleaseUrl(
                            label(releases[index]['releaseUrl'], ''),
                          )
                          ? () => model.openReleasePage(
                              releases[index]['releaseUrl'] as String,
                            )
                          : null,
                    ),
                  ]),
                ],
                key: ValueKey('release-${releases[index]['tag']}'),
                initiallyExpanded: index == releases.length - 1,
              ),
        ]),
      ],
    );
  }
}

/// 发布说明采用受限 Markdown 阅读组件：标题、段落、列表、引用和代码具有明确层级。
/// 不执行 HTML、不读取远程图片、不把正文链接变成任意协议入口；正式发布页由上方
/// 白名单按钮打开。输入只作文本解析，代码围栏内保持原文。
class ReleaseNotesContent extends StatelessWidget {
  const ReleaseNotesContent(this.markdown, {super.key});
  final String markdown;

  @override
  Widget build(BuildContext context) {
    if (markdown.trim().isEmpty)
      return const Text('该版本没有提供更新说明。', style: StewardText.body);
    final blocks = <Widget>[];
    var inCode = false;
    final code = <String>[];
    void emitCode() {
      if (code.isEmpty) return;
      blocks.add(
        Container(
          width: double.infinity,
          padding: const EdgeInsets.all(8),
          decoration: BoxDecoration(
            border: Border.all(color: context.theme.colors.border),
            borderRadius: BorderRadius.circular(4),
          ),
          child: Text(
            code.join('\n'),
            style: StewardText.caption.copyWith(fontFamily: 'monospace'),
          ),
        ),
      );
      code.clear();
    }

    for (final line in markdown.replaceAll('\r\n', '\n').split('\n')) {
      if (RegExp(r'^\s{0,3}(```|~~~)').hasMatch(line)) {
        if (inCode) emitCode();
        inCode = !inCode;
        continue;
      }
      if (inCode) {
        code.add(line);
        continue;
      }
      if (line.trim().isEmpty) continue;
      if (RegExp(r'^\s*([-*_])(?:\s*\1){2,}\s*$').hasMatch(line)) {
        blocks.add(Container(height: 1, color: context.theme.colors.border));
        continue;
      }
      final heading = RegExp(r'^\s{0,3}(#{1,6})\s+(.+?)(?:\s+#+)?$')
          .firstMatch(line);
      final list = RegExp(r'^\s*(?:[-+*]|\d+[.)])\s+(.+)$').firstMatch(line);
      final quote = RegExp(r'^\s*>\s?(.*)$').firstMatch(line);
      final text =
          heading?.group(2) ?? list?.group(1) ?? quote?.group(1) ?? line;
      Widget child = Text.rich(
        TextSpan(children: _inline(text)),
        style: heading == null ? StewardText.body : StewardText.sectionTitle,
      );
      if (heading != null) child = Semantics(header: true, child: child);
      if (list != null)
        child = Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            const Text('•  ', style: StewardText.body),
            Expanded(child: child),
          ],
        );
      if (quote != null)
        child = Container(
          padding: const EdgeInsets.only(left: 10),
          decoration: BoxDecoration(
            border: Border(
              left: BorderSide(color: context.theme.colors.border, width: 2),
            ),
          ),
          child: child,
        );
      blocks.add(child);
    }
    emitCode();
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (var index = 0; index < blocks.length; index++) ...[
          if (index != 0) const SizedBox(height: 8),
          blocks[index],
        ],
      ],
    );
  }

  /// 链接和图片仅保留可读名称；正文不能自行加载网络内容。有限的行内标记只影响
  /// 字重/字形，不创建执行回调，未闭合的标记按原文显示。
  static List<InlineSpan> _inline(String source) {
    final result = <InlineSpan>[];
    final pattern = RegExp(
      r'(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|\*[^*]+\*|_\w[^_]*_|!?\[[^\]]*\]\([^)]*\))',
    );
    var offset = 0;
    for (final match in pattern.allMatches(source)) {
      if (match.start > offset)
        result.add(TextSpan(text: source.substring(offset, match.start)));
      final value = match.group(0)!;
      if (value.startsWith('`')) {
        result.add(
          TextSpan(
            text: value.substring(1, value.length - 1),
            style: const TextStyle(fontFamily: 'monospace'),
          ),
        );
      } else if (value.startsWith('**') || value.startsWith('__')) {
        result.add(
          TextSpan(
            text: value.substring(2, value.length - 2),
            style: const TextStyle(fontWeight: FontWeight.w700),
          ),
        );
      } else if (value.startsWith('*') || value.startsWith('_')) {
        result.add(
          TextSpan(
            text: value.substring(1, value.length - 1),
            style: const TextStyle(fontStyle: FontStyle.italic),
          ),
        );
      } else {
        final name = value.substring(
          value.indexOf('[') + 1,
          value.indexOf(']'),
        );
        result.add(TextSpan(text: value.startsWith('!') ? '[图片：$name]' : name));
      }
      offset = match.end;
    }
    if (offset < source.length)
      result.add(TextSpan(text: source.substring(offset)));
    return result;
  }
}
