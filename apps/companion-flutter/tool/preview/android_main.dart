import 'package:flutter/services.dart';
import 'package:flutter/widgets.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:forui/forui.dart';
import 'package:mystia_steward_companion/app/steward_app.dart';
import 'package:mystia_steward_companion/ui/components.dart';
import 'package:mystia_steward_companion/ui/steward_select.dart';

import 'rich_preview_workbench_model.dart';

/// Android 实机布局验收专用入口，必须构建为独立的 uipreview 包。
/// 全部页面复用产品 Widget，模型仅在内存生成数据；不初始化平台存储、注册设备或连接游戏。
/// 正式 APK 固定使用 lib/main.dart，因此不会把模拟连接或合成记录暴露给用户。
void main() {
  WidgetsFlutterBinding.ensureInitialized();
  final model = RichPreviewWorkbenchModel(
    desktop: false,
    onExit: SystemNavigator.pop,
  );
  runApp(
    ProviderScope(
      overrides: [workbenchProvider.overrideWithValue(model)],
      child: StewardApp(previewControls: AndroidPreviewControls(model)),
    ),
  );
}

/// 手机控制条保持一行，明确标注合成数据；场景在菜单切换，避免测试工具挤占产品页面。
/// 字号、主题及模块开关仍通过产品设置操作，才能检验真实页面的触摸可达性。
class AndroidPreviewControls extends StatelessWidget {
  const AndroidPreviewControls(this.model, {super.key});
  final RichPreviewWorkbenchModel model;

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: model,
    builder: (context, _) {
      // 横屏或输入时只保留合成数据标识，把测试工具占用降到一行文字。
      // 场景在竖屏选择；不能让预览专属下拉框挤掉产品本来可用的输入空间。
      final size = MediaQuery.sizeOf(context);
      if (!model.gameMediaReady) {
        // 初始化门禁有意移除业务 Navigator；因此这里不能使用依赖 Overlay 的
        // FSelect 弹层。直接按钮只切换合成场景，横屏同样可以安全退出初始化预览。
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 3),
          color: context.theme.colors.primary.withValues(alpha: .08),
          child: Row(
            children: [
              const Expanded(
                child: Text(
                  '离线预览 · 合成资源',
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontSize: 11),
                ),
              ),
              const SizedBox(width: 8),
              StewardButton(
                '结束初始化预览',
                onPressed: () => model.selectScenario('night'),
              ),
            ],
          ),
        );
      }
      if (size.width > size.height ||
          MediaQuery.viewInsetsOf(context).bottom > 0) {
        return Container(
          width: double.infinity,
          padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 2),
          color: context.theme.colors.primary.withValues(alpha: .08),
          child: Text(
            '离线预览 · 合成数据 · ${RichPreviewWorkbenchModel.scenarios[model.scenario]}',
            maxLines: 1,
            overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 10),
          ),
        );
      }
      return Container(
        padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 3),
        color: context.theme.colors.primary.withValues(alpha: .08),
        child: Row(
          children: [
            const Expanded(
              child: Text(
                '离线预览 · 合成数据',
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(fontSize: 11, fontWeight: FontWeight.w600),
              ),
            ),
            const SizedBox(width: 8),
            SizedBox(
              width: 136,
              child: StewardSelect<String>(
                label: '预览场景',
                showLabel: false,
                items: RichPreviewWorkbenchModel.scenarios,
                value: model.scenario,
                onChanged: model.selectScenario,
              ),
            ),
          ],
        ),
      );
    },
  );
}
