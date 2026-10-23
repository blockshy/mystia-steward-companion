import 'package:flutter/widgets.dart';
import 'package:forui/forui.dart';

import '../app/workbench_model.dart';
import '../ui/components.dart';
import '../ui/steward_controls.dart';
import '../ui/steward_style.dart';

/// 全局提醒只投影已读取的服务端状态，不能因展示控件挂载而发起检查或下载。
/// 暂缓只针对同一电脑、当前版本和目标版本；新版本及安装失败不会被旧暂缓吞掉。
class UpdateReminder {
  const UpdateReminder(this.scope, this.message, {required this.failed});
  final String scope, message;
  final bool failed;

  static UpdateReminder? read(WorkbenchModel model, {DateTime? now}) {
    if (!model.connected) return null;
    final status =
        model.resources['/updates/status'] ?? const <String, Object?>{};
    final installState = label(status['installState'], '').toLowerCase();
    final state = label(status['state'], '').toLowerCase();
    final failed =
        label(status['error'], '').isNotEmpty ||
        installState == 'failed' ||
        state == 'failed';
    final latest = label(status['latestVersion'], '');
    if (!failed && (status['hasUpdate'] != true || latest.isEmpty)) return null;
    final scope = '${model.endpoint}|${status['currentVersion']}|$latest';
    final deferred = object(model.localPreferences['updateReminderDeferred']);
    final until = DateTime.tryParse(label(deferred['untilUtc'], ''));
    if (!failed &&
        deferred['scope'] == scope &&
        until != null &&
        until.isAfter((now ?? DateTime.now()).toUtc()))
      return null;
    return UpdateReminder(
      scope,
      failed ? '更新需处理' : '新版本 $latest',
      failed: failed,
    );
  }
}

/// 提醒放在已有页头右侧，采用短按钮；详情和稍后提醒在弹层内，不增加全局消息行。
class UpdateNoticeButton extends StatelessWidget {
  const UpdateNoticeButton(
    this.model, {
    required this.onOpenUpdates,
    super.key,
  });
  final WorkbenchModel model;
  final VoidCallback onOpenUpdates;

  @override
  Widget build(BuildContext context) {
    final reminder = UpdateReminder.read(model);
    if (reminder == null) return const SizedBox.shrink();
    return Padding(
      padding: const EdgeInsets.only(left: 8),
      child: FTooltip(
        tipBuilder: (context, _) => Text(reminder.message),
        child: FButton(
          key: const ValueKey('global-update-notice'),
          variant: FButtonVariant.ghost,
          style: stewardButtonStyle(context),
          mainAxisSize: MainAxisSize.min,
          onPress: () => _show(context),
          child: Row(
            mainAxisSize: MainAxisSize.min,
            children: [
              Icon(
                reminder.failed
                    ? FLucideIcons.circleAlert
                    : FLucideIcons.download,
                size: 16,
              ),
              const SizedBox(width: 4),
              Text(
                reminder.failed ? '更新异常' : '有更新',
                style: StewardText.caption,
              ),
            ],
          ),
        ),
      ),
    );
  }

  Future<void> _show(BuildContext context) => showFDialog<void>(
    context: context,
    builder: (dialogContext, style, animation) => FDialog(
      animation: model.reduceMotion ? null : animation,
      semanticsLabel: '版本更新提醒',
      builder: (context, _) => ConstrainedBox(
        constraints: BoxConstraints(
          maxWidth: 460,
          maxHeight: MediaQuery.sizeOf(context).height * .8,
        ),
        child: Padding(
          padding: const EdgeInsets.all(20),
          child: ListenableBuilder(
            listenable: model,
            builder: (context, _) {
              final reminder = UpdateReminder.read(model);
              return SingleChildScrollView(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  mainAxisSize: MainAxisSize.min,
                  children: [
                    Text(
                      reminder?.message ?? '当前没有待处理更新',
                      style: StewardText.sectionTitle,
                    ),
                    const SizedBox(height: 12),
                    Text(
                      reminder?.failed == true
                          ? '更新检查、下载或安装需要处理，可在更新页查看原因与当前进度。'
                          : '可在更新页查看版本说明，选择下载和安装。',
                    ),
                    const SizedBox(height: 16),
                    ToolRow([
                      StewardButton(
                        '查看更新',
                        primary: true,
                        onPressed: !model.connected
                            ? null
                            : () {
                                Navigator.pop(dialogContext);
                                onOpenUpdates();
                              },
                      ),
                      if (reminder != null && !reminder.failed)
                        StewardButton(
                          '24小时后提醒',
                          onPressed: model.busy
                              ? null
                              : () async {
                                  final scope = reminder.scope;
                                  if (UpdateReminder.read(model)?.scope !=
                                      scope)
                                    return;
                                  await model.updateLocal({
                                    'updateReminderDeferred': {
                                      'scope': scope,
                                      'untilUtc': DateTime.now()
                                          .toUtc()
                                          .add(const Duration(hours: 24))
                                          .toIso8601String(),
                                    },
                                  });
                                  if (dialogContext.mounted &&
                                      object(
                                            model
                                                .localPreferences['updateReminderDeferred'],
                                          )['scope'] ==
                                          scope) {
                                    Navigator.pop(dialogContext);
                                  }
                                },
                        ),
                      StewardButton(
                        '关闭',
                        onPressed: () => Navigator.pop(dialogContext),
                      ),
                    ]),
                  ],
                ),
              );
            },
          ),
        ),
      ),
    ),
  );
}
