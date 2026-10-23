import 'dart:async';

import 'package:flutter/widgets.dart';
import 'package:flutter/services.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:forui/forui.dart';
import 'package:companion_platform/src/platform.g.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/ui/components.dart';

import 'rich_preview_workbench_model.dart';

/// 只供开发验收的独立 target。正常打包始终使用 lib/main.dart，不提供产品内的假连接模式。
void main() {
  WidgetsFlutterBinding.ensureInitialized();
  final model = RichPreviewWorkbenchModel(
    onExit: () async {
      // 退出只作用于当前隔离窗口；不初始化业务连接、不轮询、不保存偏好或凭据。
      final exited = await PlatformHostApi().performWindowAction(
        WindowAction.exit,
      );
      if (!exited) throw StateError('宿主未接受退出请求。');
    },
  );
  // 只修改本隔离窗口的标题栏主题，便于实窗核验；不初始化连接、不读取或保存
  // 真实用户偏好，也不启动平台轮询。按主题变化调用，避免合成快照刷新重设窗口。
  bool? appliedDark;
  bool applyingTheme = false;
  Future<void> applyPreviewWindowTheme() async {
    if (appliedDark == model.dark || applyingTheme) return;
    applyingTheme = true;
    try {
      while (appliedDark != model.dark) {
        final dark = model.dark;
        for (var attempt = 0; ; attempt++) {
          try {
            await PlatformHostApi().applyWindowPreferences(
              WindowPreferences(
                alwaysOnTop: false,
                mousePassthrough: false,
                keepVisible: true,
                switchCooldownMs: 800,
                darkMode: dark,
              ),
            );
            break;
          } on PlatformException catch (error) {
            // 首帧可能早于 Pigeon 注册；仅对精确的缺通道错误作有界重试，
            // 不把透明合成失败吞成启动竞态。成功后才记录主题，迟到响应后继续应用最新值。
            if (attempt >= 9 ||
                error.code != 'channel-error' ||
                error.details != null ||
                error.message !=
                    'Unable to establish connection on channel: "dev.flutter.pigeon.companion_platform.PlatformHostApi.applyWindowPreferences".')
              rethrow;
            await Future<void>.delayed(const Duration(milliseconds: 100));
          }
        }
        appliedDark = dark;
      }
    } catch (error, stack) {
      FlutterError.reportError(
        FlutterErrorDetails(
          exception: error,
          stack: stack,
          library: '隔离预览窗口主题',
        ),
      );
    } finally {
      applyingTheme = false;
    }
  }

  model.addListener(() => unawaited(applyPreviewWindowTheme()));
  WidgetsBinding.instance.addPostFrameCallback(
    (_) => unawaited(applyPreviewWindowTheme()),
  );
  runApp(
    ProviderScope(
      overrides: [workbenchProvider.overrideWithValue(model)],
      child: CallbackShortcuts(
        bindings: {
          const SingleActivator(LogicalKeyboardKey.f1): () =>
              model.previewGamepadNavigation('previousTab'),
          const SingleActivator(LogicalKeyboardKey.f2): () =>
              model.previewGamepadNavigation('nextTab'),
          const SingleActivator(LogicalKeyboardKey.f3): () =>
              model.previewGamepadNavigation('previousSubTab'),
          const SingleActivator(LogicalKeyboardKey.f4): () =>
              model.previewGamepadNavigation('nextSubTab'),
        },
        child: StewardApp(previewControls: _PreviewControls(model)),
      ),
    ),
  );
}

/// 标识永久可见，场景控制只替换内存。即使模拟连接成功，也不能去掉合成数据说明。
class _PreviewControls extends StatelessWidget {
  const _PreviewControls(this.model);
  final RichPreviewWorkbenchModel model;
  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: model,
    builder: (context, _) => Container(
      padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 6),
      color: context.theme.colors.primary.withValues(alpha: .08),
      child: Wrap(
        spacing: 8,
        runSpacing: 4,
        crossAxisAlignment: WrapCrossAlignment.center,
        children: [
          const Text(
            '离线交互预览 · 全部为合成数据',
            style: TextStyle(fontWeight: FontWeight.w700),
          ),
          for (final entry in RichPreviewWorkbenchModel.scenarios.entries)
            StewardButton(
              entry.value,
              primary: model.scenario == entry.key,
              onPressed: () => model.selectScenario(entry.key),
            ),
          StewardButton('重置预览', onPressed: model.reset),
          StewardButton('退出预览', onPressed: () => model.platformCommand('exit')),
        ],
      ),
    ),
  );
}
